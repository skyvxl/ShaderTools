using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace ShaderTools.Core.Protocol;

/// <summary>A local navigation destination from an LSP Location or LocationLink.</summary>
public sealed class DefinitionLocation
{
    private DefinitionLocation(string filePath, int line, int character, JToken? originRange)
    {
        FilePath = filePath;
        Line = line;
        Character = character;
        OriginRange = originRange;
    }

    public string FilePath { get; }
    public int Line { get; }
    public int Character { get; }
    public JToken? OriginRange { get; }

    public static IEnumerable<DefinitionLocation> Read(JToken? response)
    {
        IEnumerable<JToken?> items = response is JArray array ? array : new[] { response };
        foreach (JToken? item in items)
        {
            if (item is not JObject location)
            {
                continue;
            }

            JToken? uriToken = location["targetUri"] ?? location["uri"];
            if (uriToken?.Type != JTokenType.String ||
                !Uri.TryCreate(ClientFileUris.NormalizeDrive((string)uriToken!), UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            {
                continue;
            }

            JToken? range = location["targetSelectionRange"] ?? location["targetRange"] ?? location["range"];
            JObject? start = (range as JObject)?["start"] as JObject;
            if (!TryCoordinate(start?["line"], out int line) ||
                !TryCoordinate(start?["character"], out int character))
            {
                continue;
            }

            yield return new DefinitionLocation(uri.LocalPath, line, character, location["originSelectionRange"]?.DeepClone());
        }
    }

    private static bool TryCoordinate(JToken? token, out int value)
    {
        value = 0;
        if (token?.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out long number) ||
            number < 0 || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;
        return true;
    }
}
