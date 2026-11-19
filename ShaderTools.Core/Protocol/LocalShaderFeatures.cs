using Newtonsoft.Json.Linq;
using ShaderTools.Core.Preprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ShaderTools.Core.Protocol;

/// <summary>Editor services that use client snapshots and do not invoke a compiler.</summary>
public sealed class LocalShaderFeatures
{
    public const string OpenConfigurationCommand = "shaderTools.openConfiguration";
    private readonly PositionEncodingConverter documents;
    private readonly ProtocolAdapter adapter;
    public LocalShaderFeatures(PositionEncodingConverter documents, ProtocolAdapter adapter) { this.documents = documents; this.adapter = adapter; }

    public bool Handles(JObject message)
    {
        return (string?)message["method"] == "shaderTools/preprocessor" ||
        ((string?)message["method"] == "workspace/executeCommand" && (string?)message.SelectToken("params.command") == OpenConfigurationCommand);
    }

    public static void Advertise(JObject initializeResult)
    {
        if (initializeResult["capabilities"] is not JObject capabilities)
        {
            return;
        }

        capabilities["codeActionProvider"] = new JObject { ["codeActionKinds"] = new JArray("quickfix") };
        JArray commands = capabilities.SelectToken("executeCommandProvider.commands") as JArray ?? [];
        if (!commands.Values<string>().Contains(OpenConfigurationCommand))
        {
            commands.Add(OpenConfigurationCommand);
        }

        capabilities["executeCommandProvider"] = new JObject { ["commands"] = commands };
    }

    public LocalShaderResult Execute(JObject request, CancellationToken cancellation, JArray? currentDiagnostics = null)
    {
        string method = (string)request["method"]!;
        JObject parameters = request["params"] as JObject ?? [];
        string? uri = method == "workspace/executeCommand" ? (string?)parameters.SelectToken("arguments[0].uri") : (string?)parameters.SelectToken("textDocument.uri");
        long generation = documents.DocumentGeneration;
        IReadOnlyList<ShaderDocumentSnapshot> snapshots = documents.CaptureOpenDocuments();
        ShaderDocumentSnapshot root = snapshots.FirstOrDefault(d => string.Equals(d.Uri, uri, StringComparison.OrdinalIgnoreCase));
        if (root == null || !Uri.TryCreate(root.Uri, UriKind.Absolute, out Uri file) || !file.IsFile)
        {
            throw new InvalidOperationException("Open a shader document before using this command.");
        }

        cancellation.ThrowIfCancellationRequested();
        if (method == "shaderTools/preprocessor")
        {
            if (parameters["context"] is not JObject context || (int?)context["documentVersion"] != root.Version || (long?)context["snapshotGeneration"] != generation)
            {
                throw new InvalidOperationException("The shader changed while its settings were being read. Refresh the view.");
            }

            Dictionary<string, string> text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderDocumentSnapshot snapshot in snapshots)
            {
                if (Uri.TryCreate(snapshot.Uri, UriKind.Absolute, out Uri snapshotUri) && snapshotUri.IsFile)
                {
                    text[snapshotUri.LocalPath] = snapshot.Text;
                }
            }

            JObject report = new PreprocessorInspector().Inspect(file.LocalPath, text, context, cancellation);
            report["version"] = root.Version;
            report["configuration"] = context["configurationPath"]?.DeepClone() ?? JToken.FromObject(ConfigurationPath(file.LocalPath) ?? string.Empty);
            if (generation != documents.DocumentGeneration)
            {
                throw new InvalidOperationException("Shader documents changed during inspection. Refresh the view.");
            }

            return new LocalShaderResult(report);
        }

        string? configuration = ConfigurationPath(file.LocalPath);
        if (method == "textDocument/codeAction")
        {
            JArray actions = [];
            if (parameters.SelectToken("context.only") is JArray only && !only.Values<string>().Any(kind => kind != null && (kind.Length == 0 || kind == "quickfix")))
            {
                return new LocalShaderResult(actions);
            }

            if (configuration == null)
            {
                return new LocalShaderResult(actions);
            }

            if (currentDiagnostics == null) { return new LocalShaderResult(actions); }

            int start = (int?)parameters.SelectToken("range.start.line") ?? -1;
            int end = (int?)parameters.SelectToken("range.end.line") ?? start;
            foreach (ShaderDirective? include in ShaderDirectives.Read(root.Text).Where(d => d.Name == "include" && d.Line >= start && d.Line <= end))
            {
                JToken[] related = currentDiagnostics.Where(d => (int?)d.SelectToken("range.start.line") == include.Line).ToArray();
                if (related.Length == 0)
                {
                    continue;
                }

                actions.Add(new JObject
                {
                    ["title"] = "Open shader configuration",
                    ["kind"] = "quickfix",
                    ["diagnostics"] = new JArray(related.Select(d => d.DeepClone())),
                    ["command"] = new JObject { ["title"] = "Open shader configuration", ["command"] = OpenConfigurationCommand, ["arguments"] = new JArray(new JObject { ["uri"] = root.Uri, ["version"] = root.Version, ["generation"] = generation }) }
                });
                break;
            }
            return new LocalShaderResult(actions);
        }
        if ((int?)parameters.SelectToken("arguments[0].version") != root.Version ||
            (long?)parameters.SelectToken("arguments[0].generation") != generation || configuration == null)
        {
            throw new InvalidOperationException("The shader or its configuration changed. Request the action again.");
        }

        return new LocalShaderResult(JValue.CreateNull(), new JObject { ["jsonrpc"] = "2.0", ["method"] = "shaderTools/openConfiguration", ["params"] = new JObject { ["uri"] = new Uri(configuration).AbsoluteUri } });
    }

    private string? ConfigurationPath(string source)
    {
        string path = adapter.FindConfigurationPath(new Uri(source).AbsoluteUri);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}

public sealed class LocalShaderResult
{
    public LocalShaderResult(JToken value, JObject? notification = null) { Value = value; Notification = notification; }
    public JToken Value { get; }
    public JObject? Notification { get; }
}
