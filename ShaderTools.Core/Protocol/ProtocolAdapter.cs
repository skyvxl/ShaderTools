using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using System;
using System.IO;

namespace ShaderTools.Core.Protocol
{
    public sealed class ProtocolAdapter
    {
        private readonly Func<string, JObject> configuration;
        private readonly Func<string, string, ShaderConfiguration>? documentConfiguration;
        private readonly Func<string, string?>? compilationRoot;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private JObject? activeVariant;
        private string workspaceRoot;

        public ProtocolAdapter(string? workspaceRoot, Func<string, JObject> configuration, Action<string>? log = null,
            Func<string, string, ShaderConfiguration>? documentConfiguration = null, Func<string, string?>? compilationRoot = null)
        {
            this.workspaceRoot = workspaceRoot ?? "";
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.log = log ?? (_ => { });
            this.documentConfiguration = documentConfiguration;
            this.compilationRoot = compilationRoot;
        }
        public string WorkspaceRoot { get { lock (gate) { return workspaceRoot; } } }

        internal string FindConfigurationPath(string uri)
        {
            var source = new Uri(compilationRoot?.Invoke(uri) ?? uri);
            return ShaderConfiguration.FindConfigurationPath(WorkspaceRoot, source.LocalPath);
        }

        // The official server exposes its dependency tree. The settings alongside it are
        // the Bridge's inputs for the owning project and selected variant.
        internal JObject GetInspectionContext(string uri)
        {
            string source = compilationRoot?.Invoke(uri) ?? uri;
            ShaderConfiguration? config = GetDocumentConfiguration(source);
            JObject settings = (JObject)(config?.Settings ?? configuration(WorkspaceRoot)).DeepClone();
            JObject? variant;
            lock (gate)
            {
                variant = Uri.TryCreate((string?)activeVariant?["url"], UriKind.Absolute, out Uri selected) &&
                    Uri.TryCreate(source, UriKind.Absolute, out Uri document) && selected == document
                    ? (JObject?)activeVariant?.DeepClone() : null;
            }
            string? directory = config == null ? WorkspaceRoot : Path.GetDirectoryName(config.ConfigurationPath);
            var paths = new JArray();
            if (!string.IsNullOrEmpty(directory)) { paths.Add(directory); }
            foreach (JToken path in variant?["includes"] as JArray ?? new JArray()) { paths.Add(path.DeepClone()); }
            foreach (JToken path in settings["includes"] as JArray ?? new JArray()) { paths.Add(path.DeepClone()); }
            var defines = (JObject?)variant?["defines"]?.DeepClone() ?? new JObject();
            defines.Merge(settings["defines"]);
            string? stage = (string?)variant?["stage"];
            if (stage != null) { defines.Merge(settings["stageDefine"]?[stage]); }
            settings["includes"] = paths;
            settings["defines"] = defines;
            settings["stage"] = stage;
            settings["entryPoint"] = variant?["entryPoint"]?.DeepClone();
            settings["configurationPath"] = config?.ConfigurationPath;
            return settings;
        }

        private ShaderConfiguration? GetDocumentConfiguration(string? uri)
        {
            return documentConfiguration != null && Uri.TryCreate(uri, UriKind.Absolute, out Uri file) && file.IsFile ?
                documentConfiguration(WorkspaceRoot, file.LocalPath) : null;
        }

        public JObject AdaptClientMessage(JObject message)
        {
            lock (gate)
            {
                JObject result = (JObject)message.DeepClone();
                switch ((string?)result["method"])
                {
                    case "initialize":
                        JObject parameters = Object(result, "params");
                        if (string.IsNullOrEmpty(workspaceRoot))
                        {
                            string? root = (string?)parameters["rootUri"] ?? (string?)parameters.SelectToken("workspaceFolders[0].uri");
                            if (Uri.TryCreate(root, UriKind.Absolute, out Uri uri) && uri.IsFile)
                            {
                                workspaceRoot = uri.LocalPath;
                            }
                            else
                            {
                                workspaceRoot = (string?)parameters["rootPath"] ?? "";
                            }
                        }
                        if (!string.IsNullOrEmpty(workspaceRoot))
                        {
                            string uri = new Uri(Path.GetFullPath(workspaceRoot)).AbsoluteUri;
                            parameters["rootUri"] = uri;
                            parameters["rootPath"] = workspaceRoot;
                            parameters["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)) });
                        }
                        JObject capabilities = Object(parameters, "capabilities");
                        Object(capabilities, "workspace")["configuration"] = true;
                        Object(capabilities, "window")["workDoneProgress"] = false;
                        Object(Object(Object(capabilities, "textDocument"), "publishDiagnostics"), "tagSupport")["valueSet"] = new JArray(1, 2);
                        break;
                    case "textDocument/didOpen":
                        if (result.SelectToken("params.textDocument") is JObject document)
                        {
                            string language = (string?)document["languageId"] ?? "";
                            if (language.StartsWith("shader-tools-", StringComparison.OrdinalIgnoreCase))
                            {
                                document["languageId"] = language.Substring("shader-tools-".Length).ToLowerInvariant();
                            }

                            if (string.IsNullOrEmpty(workspaceRoot) && Uri.TryCreate((string?)document["uri"], UriKind.Absolute, out Uri file) && file.IsFile)
                            {
                                workspaceRoot = FindWorkspaceRoot(file.LocalPath);
                            }
                        }
                        break;
                    case "textDocument/didChangeShaderVariant":
                        activeVariant = result.SelectToken("params.shaderVariant") as JObject;
                        break;
                }
                return result;
            }
        }

        public JObject? GetServerRequestResponse(JObject message)
        {
            if (message["id"] == null || message["method"] == null)
            {
                return null;
            }

            JToken value;
            switch ((string?)message["method"])
            {
                case "workspace/configuration":
                    JArray values = [];
                    foreach (JToken item in message.SelectToken("params.items") as JArray ?? [])
                    {
                        JObject settings = GetDocumentConfiguration((string?)item["scopeUri"])?.Settings ?? configuration(WorkspaceRoot);
                        string? section = (string?)item["section"];
                        JToken? selected = section is null or "shader-validator" ? settings : null;
                        if (section != null && section.StartsWith("shader-validator.", StringComparison.Ordinal))
                        {
                            selected = settings;
                            foreach (string part in section.Substring("shader-validator.".Length).Split('.'))
                            {
                                selected = (selected as JObject)?[part];
                            }
                        }
                        values.Add(selected?.DeepClone() ?? JValue.CreateNull());
                    }
                    value = values;
                    log("Applied shader configuration for " + WorkspaceRoot);
                    break;
                case "textDocument/shaderVariant":
                    lock (gate)
                    {
                        value = new JObject { ["shaderVariant"] = activeVariant?.DeepClone() ?? JValue.CreateNull() };
                    }

                    break;
                case "window/workDoneProgress/create":
                    value = JValue.CreateNull();
                    break;
                default: return null;
            }
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = value };
        }

        private static JObject Object(JObject parent, string name)
        {
            if (parent[name] is JObject existing)
            {
                return existing;
            }

            JObject created = [];
            parent[name] = created;
            return created;
        }

        public static string FindWorkspaceRoot(string filePath)
        {
            DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
            for (DirectoryInfo current = directory; current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, ".shadertools.json")))
                {
                    return current.FullName;
                }
            }

            return directory.FullName;
        }
    }
}
