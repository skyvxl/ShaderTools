using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ShaderTools.Core.Preprocessing;

public sealed class PreprocessorInspector
{
    private readonly Func<string, string?> readFile;
    private readonly int maximumFiles;
    private const int MaximumFileCharacters = 2 * 1024 * 1024;
    private const int MaximumCharacters = 8 * 1024 * 1024;
    public PreprocessorInspector(Func<string, string?>? readFile = null, int maximumFiles = 256)
    {
        this.readFile = readFile ?? ReadBoundedFile;
        this.maximumFiles = maximumFiles > 0 ? maximumFiles : throw new ArgumentOutOfRangeException(nameof(maximumFiles));
    }
    public JObject Inspect(string root, IReadOnlyDictionary<string, string> snapshots, JObject settings)
    {
        return Inspect(root, snapshots, settings, CancellationToken.None);
    }

    public JObject Inspect(string root, IReadOnlyDictionary<string, string> snapshots, JObject settings, CancellationToken cancellation)
    {
        JArray includes = [];
        JArray defines = [];
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in snapshots)
        {
            open[Path.GetFullPath(pair.Key)] = pair.Value;
        }

        string[] paths = (settings["includes"] as JArray ?? []).Values<string>().Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Take(128).ToArray();
        int characters = 0;
        bool truncated = false;
        string absoluteRoot = Path.GetFullPath(root);
        foreach (JProperty define in (settings["defines"] as JObject ?? []).Properties())
        {
            defines.Add(new JObject { ["name"] = define.Name, ["value"] = define.Value.DeepClone(), ["source"] = "Configuration", ["conditional"] = false });
        }

        string? Read(string file)
        {
            cancellation.ThrowIfCancellationRequested();
            if (open.TryGetValue(file, out string text))
            {
                return text;
            }

            try { return readFile(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }

        void Visit(string file, string text, int depth, JObject? dependency)
        {
            cancellation.ThrowIfCancellationRequested();
            if (visited.Contains(file))
            {
                return;
            }

            if (visited.Count >= maximumFiles || depth >= 32 || text.Length > MaximumFileCharacters || characters + text.Length > MaximumCharacters)
            { truncated = true; return; }
            _=visited.Add(file); _=chain.Add(file); characters += text.Length;
            int conditionalDepth = 0;
            foreach (ShaderDirective directive in ShaderDirectives.Read(text))
            {
                cancellation.ThrowIfCancellationRequested();
                if (directive.Name is "if" or "ifdef" or "ifndef") { conditionalDepth++; continue; }
                if (directive.Name == "endif") { conditionalDepth = Math.Max(0, conditionalDepth - 1); continue; }
                if (directive.Name == "define")
                {
                    string argument = directive.Argument;
                    int end = 0;
                    while (end < argument.Length && (char.IsLetterOrDigit(argument[end]) || argument[end] == '_'))
                    {
                        end++;
                    }

                    if (end > 0)
                    {
                        defines.Add(new JObject { ["name"] = argument.Substring(0, end), ["value"] = argument.Substring(end).Trim(), ["source"] = file, ["line"] = directive.Line, ["conditional"] = conditionalDepth > 0 });
                    }
                }
                if (directive.Name != "include")
                {
                    continue;
                }

                if (includes.Count >= 2048) { truncated = true; break; }
                string? path = directive.IncludePath;
                JObject edge = new JObject { ["source"] = file, ["line"] = directive.Line, ["path"] = path ?? directive.Argument, ["conditional"] = conditionalDepth > 0, ["status"] = path == null ? "dynamic" : "missing", ["candidates"] = new JArray() };
                includes.Add(edge);
                if (path == null) { continue; }
                // dependencyTree contains paths but no directive positions. Only attribute
                // an edge when the name identifies a unique direct child of this file.
                string suffix = "/" + path.Replace('\\', '/').TrimStart('/');
                JObject[] matching = (dependency?["includes"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(node => Uri.TryCreate((string?)node["url"], UriKind.Absolute, out Uri target) &&
                        target.IsFile && target.LocalPath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToArray();
                if (matching.Length == 1)
                {
                    string candidate = new Uri((string)matching[0]["url"]!).LocalPath;
                    edge["target"] = candidate;
                    edge["resolution"] = "analysis";
                    edge["unsaved"] = open.ContainsKey(candidate);
                    edge["status"] = chain.Contains(candidate) ? "cyclic" : "resolved";
                    string? included = Read(candidate);
                    if (included != null && !chain.Contains(candidate)) { Visit(candidate, included, depth + 1, matching[0]); }
                    continue;
                }
                if (conditionalDepth > 0) { edge["status"] = "conditional"; }
                edge["resolution"] = "file search";
                List<string> candidates = [];
                try
                {
                    candidates.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path)));
                    foreach (string directory in paths)
                    {
                        candidates.Add(Path.GetFullPath(Path.Combine(directory, path)));
                    }

                    string virtualPath = path.Replace('\\', '/');
                    if (virtualPath.StartsWith("./", StringComparison.Ordinal))
                    {
                        virtualPath = virtualPath.Substring(1);
                    }

                    foreach (JProperty mapping in (settings["pathRemapping"] as JObject ?? []).Properties())
                    {
                        string prefix = mapping.Name.Replace('\\', '/').TrimEnd('/') + "/";
                        if (virtualPath.StartsWith(prefix, StringComparison.Ordinal) && mapping.Value.Type == JTokenType.String)
                        {
                            candidates.Add(Path.GetFullPath(Path.Combine((string)mapping.Value!, virtualPath.Substring(prefix.Length))));
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { edge["status"] = "invalid"; continue; }
                foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ((JArray)edge["candidates"]!).Add(candidate);
                    string? included = Read(candidate);
                    if (included == null)
                    {
                        continue;
                    }

                    edge["target"] = candidate;
                    edge["unsaved"] = open.ContainsKey(candidate);
                    edge["status"] = chain.Contains(candidate) ? "cyclic" : "resolved";
                    if (!chain.Contains(candidate))
                    {
                        Visit(candidate, included, depth + 1, null);
                    }

                    break;
                }
            }
            _=chain.Remove(file);
        }
        string? source = Read(absoluteRoot);
        if (source != null)
        {
            Visit(absoluteRoot, source, 0, settings["dependencyTree"] as JObject);
        }

        return new JObject
        {
            ["file"] = absoluteRoot,
            ["includes"] = includes,
            ["defines"] = defines,
            ["settings"] = new JObject(settings.Properties().Where(property => property.Name != "dependencyTree").Select(property => new JProperty(property.Name, property.Value.DeepClone()))),
            ["dependencyTree"] = settings["dependencyTree"]?.DeepClone(),
            ["filesVisited"] = visited.Count,
            ["truncated"] = truncated,
            ["note"] = "Analysis links come from the server dependency tree. File search lists possible targets for other directives. Conditional directives can belong to inactive branches. Settings show the project and variant inputs. Engine overrides and source macros can change the values used during compilation."
        };
    }
    private static string? ReadBoundedFile(string path)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length > MaximumFileCharacters)
        {
            return null;
        }
        // A bounded read also handles a file growing between the length check and opening it.
        using StreamReader reader = new StreamReader(path);
        char[] buffer = new char[MaximumFileCharacters + 1];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        return count <= MaximumFileCharacters ? new string(buffer, 0, count) : null;
    }
}
