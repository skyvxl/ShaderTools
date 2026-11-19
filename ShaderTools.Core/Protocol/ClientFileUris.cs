using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderTools.Core.Protocol;

/// <summary>Accepts file:///C%3A/... from LSP clients on .NET Framework, which otherwise
/// interprets the drive as a path component. Echo the client's spelling in editor messages.</summary>
internal sealed class ClientFileUris
{
    private readonly object gate = new object();
    private readonly Dictionary<string, string> originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> order = new Queue<string>();

    public void NormalizeIncoming(JObject message)
    {
        if (message["params"] is not JObject parameters) { return; }
        void Normalize(JToken? field)
        {
            if (field?.Type != JTokenType.String) { return; }
            string value = (string)field!;
            string canonical = NormalizeDrive(value);
            if (ReferenceEquals(value, canonical)) { return; }
            field.Replace(new JValue(canonical));
            // Match percent escapes/case normalized by Uri or by the native compiler.
            if (!Uri.TryCreate(canonical, UriKind.Absolute, out Uri file)) { return; }
            lock (gate)
            {
                if (!originals.ContainsKey(file.LocalPath))
                {
                    if (order.Count == 512) { originals.Remove(order.Dequeue()); }
                    order.Enqueue(file.LocalPath);
                }
                originals[file.LocalPath] = value;
            }
        }
        Normalize(parameters["uri"]);
        Normalize(parameters["rootUri"]);
        Normalize(parameters.SelectToken("textDocument.uri"));
        Normalize(parameters.SelectToken("shaderVariant.url"));
        if ((string?)message["method"] == "shaderTools/preprocessor")
        {
            VisitTree(parameters.SelectToken("context.dependencyTree") as JObject, Normalize);
        }
        foreach (string field in new[] { "workspaceFolders", "changes", "arguments" })
        {
            if (parameters[field] is JArray list)
            {
                foreach (JObject item in list.OfType<JObject>()) { Normalize(item["uri"]); }
            }
        }
    }

    public void RestoreOutgoing(JObject message)
    {
        lock (gate)
        {
            if (originals.Count == 0) { return; }
            void Restore(JToken? field)
            {
                if (field?.Type == JTokenType.String && Uri.TryCreate((string?)field, UriKind.Absolute, out Uri file) &&
                    file.IsFile && originals.TryGetValue(file.LocalPath, out string spelling))
                {
                    field.Replace(new JValue(spelling));
                }
            }
            Restore(message.SelectToken("params.uri"));
            VisitTree(message.SelectToken("result.dependencyTree") as JObject, Restore);
            if (message["result"] is JObject result && result["includes"] is JArray) { VisitTree(result, Restore); }
            // Limit substitutions to protocol locations and our own configuration command.
            foreach (var obj in message.Descendants().OfType<JObject>())
            {
                if (obj["range"] is JObject) { Restore(obj["uri"]); }
                if (obj["targetRange"] is JObject) { Restore(obj["targetUri"]); }
                if ((string?)(obj["command"] as JValue) == LocalShaderFeatures.OpenConfigurationCommand)
                {
                    Restore(obj.SelectToken("arguments[0].uri"));
                }
            }
        }
    }

    private static void VisitTree(JObject? node, Action<JToken?> visit)
    {
        if (node?["includes"] is not JArray children || node["url"]?.Type != JTokenType.String) { return; }
        visit(node["url"]);
        foreach (JObject child in children.OfType<JObject>()) { VisitTree(child, visit); }
    }

    internal static string NormalizeDrive(string value)
    {
        if (value.Length < 13 || !value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ||
            !IsDrive(value[8]) || value[9] != '%' || value[10] != '3' || char.ToUpperInvariant(value[11]) != 'A' || value[12] != '/') { return value; }
        return value.Substring(0, 9) + ":" + value.Substring(12);
    }

    private static bool IsDrive(char ch) => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
