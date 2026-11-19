using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShaderTools.Core.Preprocessing;

/// <summary>Lexes directive lines only; does not claim to evaluate compiler preprocessing.</summary>
public sealed class ShaderDirective
{
    internal ShaderDirective(string name, string argument, int line) { Name = name; Argument = argument; Line = line; }
    public string Name { get; }
    public string Argument { get; }
    public int Line { get; }
    public string? IncludePath
    {
        get
        {
            if (Name != "include" || Argument.Length < 3)
            {
                return null;
            }

            char close = Argument[0] == '"' ? '"' : Argument[0] == '<' ? '>' : '\0';
            int end = close == '\0' ? -1 : Argument.IndexOf(close, 1);
            return end > 1 && string.IsNullOrWhiteSpace(Argument.Substring(end + 1)) ? Argument.Substring(1, end - 1) : null;
        }
    }
}

public static class ShaderDirectives
{
    public static IEnumerable<ShaderDirective> Read(string source)
    {
        using StringReader reader = new StringReader(source);
        bool blockComment = false;
        string? line;
        StringBuilder logical = new StringBuilder();
        int lineNumber = -1, startLine = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (logical.Length == 0)
            {
                startLine = lineNumber;
            }

            bool quoted = false;
            StringBuilder clean = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; i++; _=clean.Append(' '); }
                    continue;
                }
                if (!quoted && c == '/' && next == '/')
                {
                    break;
                }

                if (!quoted && c == '/' && next == '*') { blockComment = true; i++; _=clean.Append(' '); continue; }
                _=clean.Append(c);
                if (quoted && c == '\\' && next != '\0') { _=clean.Append(next); i++; continue; }
                if (c == '"')
                {
                    quoted = !quoted;
                }
            }
            string part = clean.ToString().TrimEnd();
            bool continued = part.EndsWith("\\", StringComparison.Ordinal);
            _=logical.Append(continued ? part.Substring(0, part.Length - 1) : part);
            if (continued)
            {
                continue;
            }

            string text = logical.ToString().TrimStart(); _=logical.Clear();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            text = text.Substring(1).TrimStart();
            int nameEnd = 0;
            while (nameEnd < text.Length && (char.IsLetter(text[nameEnd]) || text[nameEnd] == '_'))
            {
                nameEnd++;
            }

            if (nameEnd == 0)
            {
                continue;
            }

            yield return new ShaderDirective(text.Substring(0, nameEnd), text.Substring(nameEnd).Trim(), startLine);
        }
    }
}
