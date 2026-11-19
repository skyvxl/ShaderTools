using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderTools.Core.Editor
{
    public readonly struct ShaderSymbolPosition : IComparable<ShaderSymbolPosition>
    {
        public ShaderSymbolPosition(int line, int character) { Line = line; Character = character; }
        public int Line { get; }
        public int Character { get; }
        public int CompareTo(ShaderSymbolPosition other)
        {
            return Line != other.Line
            ? Line.CompareTo(other.Line) : Character.CompareTo(other.Character);
        }
    }

    public sealed class ShaderNavigationSymbol
    {
        internal ShaderNavigationSymbol(string name, string detail, int kind,
            ShaderSymbolPosition start, ShaderSymbolPosition end, ShaderSymbolPosition selection)
        {
            Name = name; Label = name; Detail = detail; Kind = kind;
            RangeStart = start; RangeEnd = end; SelectionStart = selection;
        }
        public string Name { get; }
        public string Label { get; internal set; }
        public string Detail { get; }
        public int Kind { get; }
        public ShaderSymbolPosition RangeStart { get; }
        public ShaderSymbolPosition RangeEnd { get; }
        public ShaderSymbolPosition SelectionStart { get; }
        public IReadOnlyList<ShaderNavigationSymbol> Members => members;
        internal readonly List<ShaderNavigationSymbol> members = [];
        internal bool IsScope => Kind is 2 or 3 or 5 or 10 or 11 or 23;
        internal bool IsFunction => Kind is 6 or 9 or 12;
        internal bool Contains(ShaderSymbolPosition position)
        {
            return RangeStart.CompareTo(position) <= 0 &&
            (RangeEnd.CompareTo(position) > 0 || (RangeStart.CompareTo(RangeEnd) == 0 && RangeStart.CompareTo(position) == 0));
        }
    }

    /// <summary>Projects LSP document symbols into two navigation lists, without interpreting shader source.</summary>
    public sealed class ShaderDocumentSymbols
    {
        private readonly List<ShaderNavigationSymbol> scopes = [];
        private ShaderDocumentSymbols()
        {
            scopes.Add(new ShaderNavigationSymbol("(Global Scope)", "", 0, default, default, default));
        }
        public IReadOnlyList<ShaderNavigationSymbol> Scopes => scopes;

        public static ShaderDocumentSymbols Read(JToken? result, string? documentUri = null)
        {
            ShaderDocumentSymbols model = new ShaderDocumentSymbols();
            if (result is not JArray array)
            {
                return model;
            }

            List<(ShaderNavigationSymbol Symbol, string Container)> flat = [];
            foreach (JObject value in array.OfType<JObject>())
            {
                if (value["location"] is JObject location)
                {
                    string? uri = location["uri"]?.Type == JTokenType.String ? location.Value<string>("uri") : null;
                    if (documentUri != null && !string.Equals(documentUri, uri, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ShaderNavigationSymbol? symbol = Parse(value, location["range"]);
                    if (symbol != null)
                    {
                        flat.Add((symbol, value["containerName"]?.Type == JTokenType.String
                        ? value.Value<string>("containerName") ?? "" : ""));
                    }
                }
                else if (value["children"] is not JArray { Count: > 0 })
                {
                    // shader-sense returns document symbols in a flat list with separate
                    // declaration and body ranges. Use those ranges to exclude locals.
                    var symbol = Parse(value, value["range"]);
                    if (symbol != null) { flat.Add((symbol, "")); }
                }
                else
                {
                    model.AddNested(value, model.scopes[0], "");
                }
            }
            // Legacy SymbolInformation has no children. Prefer explicit container names, then enclosing ranges.
            // Function-local declarations must never become global navigation entries.
            foreach ((ShaderNavigationSymbol Symbol, string Container) entry in flat.Where(x => x.Symbol.IsScope))
            {
                model.scopes.Add(entry.Symbol);
            }

            foreach ((ShaderNavigationSymbol Symbol, string Container) entry in flat)
            {
                ShaderNavigationSymbol symbol = entry.Symbol;
                if (symbol.IsScope)
                {
                    continue;
                }

                List<(ShaderNavigationSymbol Symbol, string Container)> containers = flat.Where(x => x.Symbol != symbol &&
                    (x.Symbol.IsScope || x.Symbol.IsFunction) &&
                    ((entry.Container.Length > 0 && x.Symbol.Name == entry.Container) ||
                     (x.Symbol.Contains(symbol.RangeStart) && x.Symbol.RangeEnd.CompareTo(symbol.RangeEnd) >= 0)))
                    .OrderByDescending(x => entry.Container.Length > 0 && x.Symbol.Name == entry.Container)
                    .ThenByDescending(x => x.Symbol.RangeStart.Line)
                    .ThenByDescending(x => x.Symbol.RangeStart.Character)
                    .ThenBy(x => x.Symbol.RangeEnd.Line)
                    .ThenBy(x => x.Symbol.RangeEnd.Character).ToList();
                if (containers.Count > 0 && containers[0].Symbol.IsFunction)
                {
                    continue;
                } (containers.Count > 0 ? containers[0].Symbol : model.scopes[0]).members.Add(symbol);
            }
            foreach (ShaderNavigationSymbol scope in model.scopes)
            {
                scope.members.Sort((a, b) => a.RangeStart.CompareTo(b.RangeStart));
                Disambiguate(scope.members);
            }
            Disambiguate(model.scopes);
            return model;
        }

        private void AddNested(JObject value, ShaderNavigationSymbol parent, string prefix)
        {
            ShaderNavigationSymbol? symbol = Parse(value, value["range"]);
            if (symbol == null)
            {
                return;
            }

            if (symbol.IsScope)
            {
                symbol.Label = prefix + symbol.Name;
                scopes.Add(symbol);
                if (value["children"] is JArray children)
                {
                    foreach (JObject child in children.OfType<JObject>())
                    {
                        AddNested(child, symbol, symbol.Label + "::");
                    }
                }
            }
            else
            {
                parent.members.Add(symbol); // Deliberately do not descend into function locals.
            }
        }

        public (int Scope, int Member) Select(int line, int character)
        {
            ShaderSymbolPosition position = new ShaderSymbolPosition(line, character);
            int scopeIndex = 0;
            for (int i = 1; i < scopes.Count; i++)
            {
                if (scopes[i].Contains(position) && (scopeIndex == 0 ||
                    (scopes[i].RangeStart.CompareTo(scopes[scopeIndex].RangeStart) >= 0 &&
                    scopes[i].RangeEnd.CompareTo(scopes[scopeIndex].RangeEnd) <= 0)))
                {
                    scopeIndex = i;
                }
            }

            IReadOnlyList<ShaderNavigationSymbol> members = scopes[scopeIndex].Members;
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Contains(position))
                {
                    return (scopeIndex, i);
                }
            }
            // SymbolInformation may describe just the type's name, while its method has a body range.
            for (int i = 1; i < scopes.Count; i++)
            {
                for (int j = 0; j < scopes[i].Members.Count; j++)
                {
                    if (scopes[i].Members[j].Contains(position))
                    {
                        return (i, j);
                    }
                }
            }

            return (scopeIndex, -1);
        }

        private static void Disambiguate(List<ShaderNavigationSymbol> items)
        {
            foreach (IGrouping<string, ShaderNavigationSymbol>? group in items.GroupBy(x => x.Label).Where(x => x.Count() > 1))
            {
                foreach (ShaderNavigationSymbol? symbol in group)
                {
                    if (!string.IsNullOrWhiteSpace(symbol.Detail))
                    {
                        symbol.Label = symbol.Detail.Contains(symbol.Name) ? symbol.Detail : symbol.Name + " — " + symbol.Detail;
                    }
                }
            }

            foreach (IGrouping<string, ShaderNavigationSymbol>? group in items.GroupBy(x => x.Label).Where(x => x.Count() > 1))
            {
                foreach (ShaderNavigationSymbol? symbol in group)
                {
                    symbol.Label += " (line " + (symbol.SelectionStart.Line + 1) + ", " + (symbol.SelectionStart.Character + 1) + ")";
                }
            }
        }

        private static ShaderNavigationSymbol? Parse(JObject value, JToken? range)
        {
            if (value["name"]?.Type != JTokenType.String || value["kind"]?.Type != JTokenType.Integer ||
                !TryPosition((range as JObject)?["start"], out ShaderSymbolPosition start) ||
                !TryPosition((range as JObject)?["end"], out ShaderSymbolPosition end) || start.CompareTo(end) > 0)
            {
                return null;
            }

            if (!int.TryParse(value["kind"]!.ToString(), out int kind) || kind < 1 || kind > 26)
            {
                return null;
            }

            ShaderSymbolPosition selection = start;
            if (TryPosition((value["selectionRange"] as JObject)?["start"], out ShaderSymbolPosition candidate) &&
                candidate.CompareTo(start) >= 0 && candidate.CompareTo(end) <= 0)
            {
                selection = candidate;
            }

            string detail = value["detail"]?.Type == JTokenType.String ? value.Value<string>("detail") ?? "" : "";
            // 1.6.2 reports shader struct declarations as LSP TypeParameter.
            if (kind == 26 && detail.StartsWith("struct ", StringComparison.Ordinal)) { kind = 23; }
            return new ShaderNavigationSymbol(value.Value<string>("name")!, detail, kind, start, end, selection);
        }

        private static bool TryPosition(JToken? token, out ShaderSymbolPosition position)
        {
            position = default;
            if (token is not JObject value || value["line"]?.Type != JTokenType.Integer ||
                value["character"]?.Type != JTokenType.Integer)
            {
                return false;
            }

            if (!int.TryParse(value["line"]!.ToString(), out int line) || line < 0 ||
                !int.TryParse(value["character"]!.ToString(), out int character) || character < 0)
            {
                return false;
            }

            position = new ShaderSymbolPosition(line, character);
            return true;
        }
    }
}
