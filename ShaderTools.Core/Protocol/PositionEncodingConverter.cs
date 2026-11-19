using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace ShaderTools.Core.Protocol
{
    /// <summary>
    /// Adapts Visual Studio's UTF-16 positions to shader-sense. Its text edits use Unicode
    /// scalars, while the syntax tree's symbol locations and navigation queries use UTF-8 bytes.
    /// </summary>
    public sealed class PositionEncodingConverter
    {
        private const int MaximumOpenDocuments = 256;
        private const int MaximumPendingRequests = 512;
        private const int MaximumDiskDocuments = 128;

        private readonly object gate = new object();
        private readonly Dictionary<string, string> openDocuments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> documentVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private long documentGeneration;
        public long DocumentGeneration { get { lock (gate) { return documentGeneration; } } }
        public int? GetDocumentVersion(string uri)
        {
            lock (gate) { return documentVersions.TryGetValue(NormalizeUri(uri) ?? uri, out int version) ? version : (int?)null; }
        }

        public IReadOnlyList<ShaderDocumentSnapshot> CaptureOpenDocuments()
        {
            lock (gate)
            {
                List<ShaderDocumentSnapshot> snapshots = [];
                foreach (KeyValuePair<string, string> pair in openDocuments)
                {
                    if (documentVersions.TryGetValue(pair.Key, out int version))
                    {
                        snapshots.Add(new ShaderDocumentSnapshot(pair.Key, pair.Value, version));
                    }
                }

                return snapshots;
            }
        }
        private readonly Dictionary<string, RequestContext> pendingRequests = new Dictionary<string, RequestContext>(StringComparer.Ordinal);
        private readonly LinkedList<string> pendingOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> pendingNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DiskDocument> diskDocuments = new Dictionary<string, DiskDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> diskOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> diskNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);

        public void ProcessClientMessage(JObject message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (gate)
            {
                string? method = (string?)message["method"];
                if (method == null)
                {
                    return;
                }

                switch (method)
                {
                    case "textDocument/didOpen":
                        ProcessDidOpen(message);
                        return;
                    case "textDocument/didChange":
                        ProcessDidChange(message);
                        return;
                    case "textDocument/didClose":
                        ProcessDidClose(message);
                        return;
                    case "workspace/didChangeConfiguration":
                    case "textDocument/didChangeShaderVariant":
                    case "workspace/didChangeWatchedFiles":
                        documentGeneration++;
                        return;
                    case "$/cancelRequest":
                        RemovePending(message.SelectToken("params.id"));
                        return;
                    case "shutdown":
                    case "exit":
                        Clear();
                        return;
                }

                JObject? parameters = message["params"] as JObject;
                string? uri = (string?)parameters?.SelectToken("textDocument.uri");
                string? text = GetText(uri, null);
                if (message["id"] != null && NeedsResponseConversion(method))
                {
                    AddPending(message["id"]!, new RequestContext(method, NormalizeUri(uri), text));
                }

                ConvertClientParameters(method, parameters, text);
            }
        }

        public void ProcessServerMessage(JObject message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (gate)
            {
                string? method = (string?)message["method"];
                if (method != null)
                {
                    ProcessServerNotificationOrRequest(method, message);
                    return;
                }

                if (message["id"] == null || (message["result"] == null && message["error"] == null))
                {
                    return;
                }

                string key = RequestKey(message["id"]!);
                if (!pendingRequests.TryGetValue(key, out RequestContext? context))
                {
                    return;
                }

                RemovePendingKey(key);
                JToken? result = message["result"];
                if (result == null || result.Type == JTokenType.Null)
                {
                    return;
                }

                ConvertServerResult(context, result);
            }
        }

        private void ProcessDidOpen(JObject message)
        {
            JObject? document = message.SelectToken("params.textDocument") as JObject;
            string? uri = NormalizeUri((string?)document?["uri"]);
            string? text = (string?)document?["text"];
            if (uri == null || text == null)
            {
                return;
            }

            SetOpenDocument(uri, text);
            documentVersions[uri] = (int?)document?["version"] ?? 0;
            documentGeneration++;
        }

        private void ProcessDidChange(JObject message)
        {
            JObject? parameters = message["params"] as JObject;
            string? uri = NormalizeUri((string?)parameters?.SelectToken("textDocument.uri"));
            if (uri == null || parameters?["contentChanges"] is not JArray changes)
            {
                return;
            }

            _=openDocuments.TryGetValue(uri, out string? workingText);
            JArray convertedChanges = (JArray)changes.DeepClone();
            for (int index = 0; index < changes.Count; index++)
            {
                if (changes[index] is not JObject original || convertedChanges[index] is not JObject converted)
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                string? replacement = (string?)original["text"];
                if (replacement == null)
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                if (original["range"] is not JObject originalRange)
                {
                    if (original["range"] != null && original["range"]!.Type != JTokenType.Null)
                    {
                        InvalidateOpenDocument(uri);
                        return;
                    }

                    workingText = replacement;
                    continue;
                }

                if (workingText == null || converted["range"] is not JObject convertedRange)
                {
                    InvalidateOpenDocument(uri);
                    return;
                }

                if (!TryConvertRange(convertedRange, workingText, toServer: true) ||
                    !TryApplyUtf16Edit(workingText, originalRange, replacement, out workingText))
                {
                    InvalidateOpenDocument(uri);
                    return;
                }
            }

            parameters!["contentChanges"] = convertedChanges;
            if (workingText != null)
            {
                SetOpenDocument(uri, workingText);
                documentVersions[uri] = (int?)parameters.SelectToken("textDocument.version") ?? 0;
                documentGeneration++;
            }
        }

        private void ProcessDidClose(JObject message)
        {
            string? uri = NormalizeUri((string?)message.SelectToken("params.textDocument.uri"));
            if (uri == null)
            {
                return;
            }

            _=openDocuments.Remove(uri);
            _=documentVersions.Remove(uri);
            documentGeneration++;
            List<string> removals = [];
            foreach (KeyValuePair<string, RequestContext> pair in pendingRequests)
            {
                if (string.Equals(pair.Value.Uri, uri, StringComparison.OrdinalIgnoreCase))
                {
                    removals.Add(pair.Key);
                }
            }

            foreach (string key in removals)
            {
                RemovePendingKey(key);
            }
        }

        private void ConvertClientParameters(string method, JObject? parameters, string? text)
        {
            if (parameters == null || text == null)
            {
                return;
            }

            switch (method)
            {
                case "textDocument/hover":
                case "textDocument/definition":
                case "textDocument/declaration":
                case "textDocument/typeDefinition":
                case "textDocument/implementation":
                case "textDocument/references":
                    _=TryConvertPosition(parameters["position"] as JObject, text, toServer: true, utf8Bytes: true);
                    break;
                case "textDocument/completion":
                case "textDocument/signatureHelp":
                case "textDocument/documentHighlight":
                case "textDocument/rename":
                case "textDocument/prepareRename":
                case "textDocument/onTypeFormatting":
                    _=TryConvertPosition(parameters["position"] as JObject, text, toServer: true);
                    break;
                case "textDocument/rangeFormatting":
                case "textDocument/inlayHint":
                case "textDocument/semanticTokens/range":
                case "textDocument/colorPresentation":
                    _=TryConvertRange(parameters["range"] as JObject, text, toServer: true);
                    break;
                case "textDocument/codeAction":
                    _=TryConvertRange(parameters["range"] as JObject, text, toServer: true);
                    ConvertDiagnostics(parameters.SelectToken("context.diagnostics") as JArray, null, text, null, toServer: true);
                    break;
                case "textDocument/selectionRange":
                    ConvertPositions(parameters["positions"] as JArray, text, toServer: true);
                    break;
            }
        }

        private void ProcessServerNotificationOrRequest(string method, JObject message)
        {
            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    string? uri = (string?)message.SelectToken("params.uri");
                    ConvertDiagnostics(
                        message.SelectToken("params.diagnostics") as JArray,
                        uri,
                        GetText(uri, null),
                        null);
                    break;
                case "workspace/applyEdit":
                    ConvertWorkspaceEdit(message.SelectToken("params.edit") as JObject, null);
                    break;
            }
        }

        private void ConvertServerResult(RequestContext context, JToken result)
        {
            string? text = context.Text ?? GetText(context.Uri, context);
            switch (context.Method)
            {
                case "textDocument/completion":
                    ConvertCompletion(result, text);
                    break;
                case "textDocument/hover":
                    _=TryConvertRange((result as JObject)?["range"] as JObject, text, toServer: false, utf8Bytes: true);
                    break;
                case "textDocument/foldingRange":
                    ConvertFoldingRanges(result as JArray, text);
                    break;
                case "textDocument/inlayHint":
                    ConvertInlayHints(result as JArray, context, text);
                    break;
                case "textDocument/documentSymbol":
                    ConvertDocumentSymbols(result as JArray, text, context);
                    break;
                case "workspace/symbol":
                    ConvertWorkspaceSymbols(result as JArray, context);
                    break;
                case "textDocument/definition":
                case "textDocument/declaration":
                case "textDocument/typeDefinition":
                case "textDocument/implementation":
                case "textDocument/references":
                    ConvertLocations(result, context, text);
                    break;
                case "textDocument/documentHighlight":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/formatting":
                case "textDocument/rangeFormatting":
                case "textDocument/onTypeFormatting":
                    ConvertTextEdits(result as JArray, text);
                    break;
                case "textDocument/prepareRename":
                    if (result is JObject prepareResult)
                    {
                        if (prepareResult["start"] != null)
                        {
                            _=TryConvertRange(prepareResult, text, toServer: false);
                        }
                        else
                        {
                            _=TryConvertRange(prepareResult["range"] as JObject, text, toServer: false);
                        }
                    }
                    break;
                case "textDocument/rename":
                    ConvertWorkspaceEdit(result as JObject, context);
                    break;
                case "textDocument/selectionRange":
                    ConvertSelectionRanges(result as JArray, text);
                    break;
                case "textDocument/codeAction":
                    ConvertCodeActions(result as JArray, context, text);
                    break;
                case "textDocument/documentColor":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/colorPresentation":
                    ConvertColorPresentations(result as JArray, text);
                    break;
                case "textDocument/documentLink":
                    ConvertRangesOnArray(result as JArray, "range", text);
                    break;
                case "textDocument/diagnostic":
                    ConvertDocumentDiagnosticReport(result as JObject, context.Uri, context);
                    break;
                case "workspace/diagnostic":
                    ConvertWorkspaceDiagnosticReport(result as JObject, context);
                    break;
                case "textDocument/semanticTokens/full":
                case "textDocument/semanticTokens/range":
                    ConvertSemanticTokens(result as JObject, text);
                    break;
            }
        }

        private static bool NeedsResponseConversion(string method)
        {
            return method switch
            {
                "textDocument/completion"or"textDocument/hover"or"textDocument/foldingRange"or"textDocument/inlayHint"or"textDocument/documentSymbol"or"workspace/symbol"or"textDocument/definition"or"textDocument/declaration"or"textDocument/typeDefinition"or"textDocument/implementation"or"textDocument/references"or"textDocument/documentHighlight"or"textDocument/formatting"or"textDocument/rangeFormatting"or"textDocument/onTypeFormatting"or"textDocument/prepareRename"or"textDocument/rename"or"textDocument/selectionRange"or"textDocument/codeAction"or"textDocument/documentColor"or"textDocument/colorPresentation"or"textDocument/documentLink"or"textDocument/diagnostic"or"workspace/diagnostic"or"textDocument/semanticTokens/full"or"textDocument/semanticTokens/range" => true,
                _ => false,
            };
        }

        private void ConvertCompletion(JToken result, string? text)
        {
            if (text == null)
            {
                return;
            }

            JArray? items = result as JArray ?? (result as JObject)?["items"] as JArray;
            if (items != null)
            {
                foreach (JToken item in items)
                {
                    if (item is JObject completion)
                    {
                        ConvertCompletionEdit(completion["textEdit"] as JObject, text);
                        ConvertTextEdits(completion["additionalTextEdits"] as JArray, text);
                    }
                }
            }

            JObject? defaults = (result as JObject)?["itemDefaults"] as JObject;
            ConvertCompletionEdit(defaults?["editRange"] as JObject, text);
        }

        private static void ConvertCompletionEdit(JObject? edit, string text)
        {
            if (edit == null)
            {
                return;
            }

            if (edit["start"] != null)
            {
                _=TryConvertRange(edit, text, toServer: false);
            }
            else
            {
                _=TryConvertRange(edit["range"] as JObject, text, toServer: false);
                _=TryConvertRange(edit["insert"] as JObject, text, toServer: false);
                _=TryConvertRange(edit["replace"] as JObject, text, toServer: false);
            }
        }

        private static void ConvertFoldingRanges(JArray? ranges, string? text)
        {
            if (ranges == null || text == null)
            {
                return;
            }

            foreach (JToken token in ranges)
            {
                if (token is not JObject range)
                {
                    continue;
                }

                TryConvertLineCharacter(range, "startLine", "startCharacter", text);
                TryConvertLineCharacter(range, "endLine", "endCharacter", text);
            }
        }

        private void ConvertInlayHints(JArray? hints, RequestContext context, string? text)
        {
            if (hints == null || text == null)
            {
                return;
            }

            foreach (JToken token in hints)
            {
                if (token is not JObject hint)
                {
                    continue;
                }

                _=TryConvertPosition(hint["position"] as JObject, text, toServer: false, utf8Bytes: true);
                ConvertTextEdits(hint["textEdits"] as JArray, text);
                if (hint["label"] is JArray labelParts)
                {
                    foreach (JToken labelToken in labelParts)
                    {
                        if (labelToken is JObject label && label["location"] is JObject location)
                        {
                            ConvertLocation(location, context, text);
                        }
                    }
                }
            }
        }

        private void ConvertDocumentSymbols(JArray? symbols, string? text, RequestContext context)
        {
            if (symbols == null)
            {
                return;
            }

            foreach (JToken token in symbols)
            {
                if (token is not JObject symbol)
                {
                    continue;
                }

                if (symbol["location"] is JObject location)
                {
                    ConvertLocation(location, context, text);
                }
                else if (text != null)
                {
                    _=TryConvertRange(symbol["range"] as JObject, text, toServer: false, utf8Bytes: true);
                    _=TryConvertRange(symbol["selectionRange"] as JObject, text, toServer: false, utf8Bytes: true);
                }

                ConvertDocumentSymbols(symbol["children"] as JArray, text, context);
            }
        }

        private void ConvertWorkspaceSymbols(JArray? symbols, RequestContext context)
        {
            if (symbols == null)
            {
                return;
            }

            foreach (JToken token in symbols)
            {
                if (token is JObject symbol && symbol["location"] is JObject location)
                {
                    ConvertLocation(location, context, null);
                }
            }
        }

        private void ConvertLocations(JToken result, RequestContext context, string? sourceText)
        {
            if (result is JArray locations)
            {
                foreach (JToken token in locations)
                {
                    if (token is JObject location)
                    {
                        ConvertLocation(location, context, sourceText);
                    }
                }
            }
            else if (result is JObject location)
            {
                ConvertLocation(location, context, sourceText);
            }
        }

        private void ConvertLocation(JObject location, RequestContext? context, string? sourceText, bool utf8Bytes = true)
        {
            if (location["targetUri"] != null)
            {
                _=TryConvertRange(location["originSelectionRange"] as JObject, sourceText, toServer: false, utf8Bytes: utf8Bytes);
                string? targetUri = (string?)location["targetUri"];
                string? targetText = GetText(targetUri, context);
                _=TryConvertRange(location["targetRange"] as JObject, targetText, toServer: false, utf8Bytes: utf8Bytes);
                _=TryConvertRange(location["targetSelectionRange"] as JObject, targetText, toServer: false, utf8Bytes: utf8Bytes);
                return;
            }

            string? uri = (string?)location["uri"];
            string? text = GetText(uri, context);
            _=TryConvertRange(location["range"] as JObject, text, toServer: false, utf8Bytes: utf8Bytes);
        }

        private static void ConvertTextEdits(JArray? edits, string? text)
        {
            if (edits == null || text == null)
            {
                return;
            }

            foreach (JToken token in edits)
            {
                if (token is JObject edit)
                {
                    _=TryConvertRange(edit["range"] as JObject, text, toServer: false);
                }
            }
        }

        private static void ConvertRangesOnArray(JArray? values, string propertyName, string? text)
        {
            if (values == null || text == null)
            {
                return;
            }

            foreach (JToken token in values)
            {
                if (token is JObject value)
                {
                    _=TryConvertRange(value[propertyName] as JObject, text, toServer: false);
                }
            }
        }

        private static void ConvertPositions(JArray? positions, string text, bool toServer)
        {
            if (positions == null)
            {
                return;
            }

            foreach (JToken token in positions)
            {
                _=TryConvertPosition(token as JObject, text, toServer);
            }
        }

        private static void ConvertSelectionRanges(JArray? values, string? text)
        {
            if (values == null || text == null)
            {
                return;
            }

            foreach (JToken token in values)
            {
                JObject? current = token as JObject;
                while (current != null)
                {
                    _=TryConvertRange(current["range"] as JObject, text, toServer: false);
                    current = current["parent"] as JObject;
                }
            }
        }

        private void ConvertCodeActions(JArray? actions, RequestContext context, string? sourceText)
        {
            if (actions == null)
            {
                return;
            }

            foreach (JToken token in actions)
            {
                if (token is not JObject action)
                {
                    continue;
                }

                ConvertDiagnostics(action["diagnostics"] as JArray, context.Uri, sourceText, context);
                ConvertWorkspaceEdit(action["edit"] as JObject, context);
            }
        }

        private static void ConvertColorPresentations(JArray? presentations, string? text)
        {
            if (presentations == null || text == null)
            {
                return;
            }

            foreach (JToken token in presentations)
            {
                if (token is JObject presentation)
                {
                    if (presentation["textEdit"] is JObject textEdit)
                    {
                        _=TryConvertRange(textEdit["range"] as JObject, text, toServer: false);
                    }

                    ConvertTextEdits(presentation["additionalTextEdits"] as JArray, text);
                }
            }
        }

        private void ConvertWorkspaceEdit(JObject? edit, RequestContext? context)
        {
            if (edit == null)
            {
                return;
            }

            if (edit["changes"] is JObject changes)
            {
                foreach (JProperty property in changes.Properties())
                {
                    ConvertTextEdits(property.Value as JArray, GetText(property.Name, context));
                }
            }

            if (edit["documentChanges"] is JArray documentChanges)
            {
                foreach (JToken token in documentChanges)
                {
                    if (token is JObject documentEdit)
                    {
                        string? uri = (string?)documentEdit.SelectToken("textDocument.uri");
                        ConvertTextEdits(documentEdit["edits"] as JArray, GetText(uri, context));
                    }
                }
            }
        }

        private void ConvertDiagnostics(JArray? diagnostics, string? uri, string? text, RequestContext? context, bool toServer = false)
        {
            if (diagnostics == null)
            {
                return;
            }

            string? documentText = text ?? GetText(uri, context);

            foreach (JToken token in diagnostics)
            {
                if (token is not JObject diagnostic)
                {
                    continue;
                }

                _=TryConvertRange(diagnostic["range"] as JObject, documentText, toServer);
                if (diagnostic["relatedInformation"] is JArray relatedInformation)
                {
                    foreach (JToken relatedToken in relatedInformation)
                    {
                        if (relatedToken is JObject related && related["location"] is JObject location)
                        {
                            if (toServer)
                            {
                                string? relatedUri = (string?)location["uri"];
                                _=TryConvertRange(location["range"] as JObject, GetText(relatedUri, context), toServer: true);
                            }
                            else
                            {
                                ConvertLocation(location, context, text, utf8Bytes: false);
                            }
                        }
                    }
                }
            }
        }

        private void ConvertDocumentDiagnosticReport(JObject? report, string? uri, RequestContext context)
        {
            if (report == null)
            {
                return;
            }

            ConvertDiagnostics(report["items"] as JArray, uri, GetText(uri, context), context);
            if (report["relatedDocuments"] is JObject related)
            {
                foreach (JProperty property in related.Properties())
                {
                    ConvertDocumentDiagnosticReport(property.Value as JObject, property.Name, context);
                }
            }
        }

        private void ConvertWorkspaceDiagnosticReport(JObject? report, RequestContext context)
        {
            if (report?["items"] is not JArray items)
            {
                return;
            }

            foreach (JToken token in items)
            {
                if (token is JObject documentReport)
                {
                    string? uri = (string?)documentReport["uri"];
                    ConvertDiagnostics(documentReport["items"] as JArray, uri, GetText(uri, context), context);
                }
            }
        }

        private static void ConvertSemanticTokens(JObject? result, string? text)
        {
            if (result?["data"] is not JArray data || text == null || data.Count % 5 != 0)
            {
                return;
            }

            JArray converted = (JArray)data.DeepClone();
            int scalarLine = 0;
            int scalarStart = 0;
            int previousUtf16Line = 0;
            int previousUtf16Start = 0;
            for (int index = 0; index < data.Count; index += 5)
            {
                if (!TryReadNonNegativeInt(data[index], out int deltaLine) ||
                    !TryReadNonNegativeInt(data[index + 1], out int deltaStart) ||
                    !TryReadNonNegativeInt(data[index + 2], out int scalarLength))
                {
                    return;
                }

                scalarLine += deltaLine;
                scalarStart = deltaLine == 0 ? scalarStart + deltaStart : deltaStart;
                if (!TryScalarToUtf16(text, scalarLine, scalarStart, out int utf16Start) ||
                    !TryUtf8LengthToUtf16(text, scalarLine, utf16Start, scalarLength, out int utf16Length))
                {
                    return;
                }

                converted[index] = scalarLine - previousUtf16Line;
                converted[index + 1] = scalarLine == previousUtf16Line ? utf16Start - previousUtf16Start : utf16Start;
                converted[index + 2] = utf16Length;
                previousUtf16Line = scalarLine;
                previousUtf16Start = utf16Start;
            }

            result["data"] = converted;
        }

        private static bool TryUtf8LengthToUtf16(string text, int line, int utf16Start, int utf8Length, out int utf16Length)
        {
            utf16Length = 0;
            if (!TryGetLine(text, line, out int lineStart, out int lineLength) || utf16Start > lineLength)
            {
                return false;
            }

            int offset = utf16Start;
            int bytes = 0;
            while (bytes < utf8Length && offset < lineLength)
            {
                char character = text[lineStart + offset];
                int scalarBytes;
                if (char.IsHighSurrogate(character))
                {
                    if (offset + 1 >= lineLength || !char.IsLowSurrogate(text[lineStart + offset + 1]))
                    {
                        return false;
                    }

                    scalarBytes = 4;
                    offset += 2;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
                else
                {
                    scalarBytes = character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                    offset++;
                }

                bytes += scalarBytes;
                if (bytes > utf8Length)
                {
                    return false;
                }
            }

            utf16Length = offset - utf16Start;
            return bytes == utf8Length;
        }

        private static bool TryConvertRange(JObject? range, string? text, bool toServer, bool utf8Bytes = false)
        {
            if (range == null || text == null || range["start"] is not JObject start || range["end"] is not JObject end)
            {
                return false;
            }

            if (!TryConvertedCharacter(start, text, toServer, utf8Bytes, out int convertedStart) ||
                !TryConvertedCharacter(end, text, toServer, utf8Bytes, out int convertedEnd))
            {
                return false;
            }

            start["character"] = convertedStart;
            end["character"] = convertedEnd;
            return true;
        }

        private static bool TryConvertPosition(JObject? position, string? text, bool toServer, bool utf8Bytes = false)
        {
            if (position == null || text == null || !TryConvertedCharacter(position, text, toServer, utf8Bytes, out int converted))
            {
                return false;
            }

            position["character"] = converted;
            return true;
        }

        private static bool TryConvertedCharacter(JObject position, string text, bool toServer, bool utf8Bytes, out int converted)
        {
            converted = 0;
            if (!TryReadNonNegativeInt(position["line"], out int line) ||
                !TryReadNonNegativeInt(position["character"], out int character))
            {
                return false;
            }

            if (toServer)
            {
                return TryUtf16ToServer(text, line, character, utf8Bytes, out converted);
            }
            return utf8Bytes ? TryUtf8LengthToUtf16(text, line, 0, character, out converted)
                : TryScalarToUtf16(text, line, character, out converted);
        }

        private static void TryConvertLineCharacter(JObject range, string lineProperty, string characterProperty, string text)
        {
            if (range[characterProperty] == null ||
                !TryReadNonNegativeInt(range[lineProperty], out int line) ||
                !TryReadNonNegativeInt(range[characterProperty], out int character) ||
                !TryScalarToUtf16(text, line, character, out int converted))
            {
                return;
            }

            range[characterProperty] = converted;
        }

        private static bool TryUtf16ToServer(string text, int line, int utf16Character, bool utf8Bytes, out int column)
        {
            column = 0;
            if (!TryGetLine(text, line, out int lineStart, out int lineLength) || utf16Character > lineLength)
            {
                return false;
            }

            if (utf16Character > 0 && utf16Character < lineLength &&
                char.IsHighSurrogate(text[lineStart + utf16Character - 1]) &&
                char.IsLowSurrogate(text[lineStart + utf16Character]))
            {
                return false;
            }

            int offset = 0;
            while (offset < utf16Character)
            {
                if (offset + 1 < utf16Character &&
                    char.IsHighSurrogate(text[lineStart + offset]) &&
                    char.IsLowSurrogate(text[lineStart + offset + 1]))
                {
                    offset += 2;
                    column += utf8Bytes ? 4 : 1;
                }
                else
                {
                    char character = text[lineStart + offset];
                    column += !utf8Bytes || character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
                    offset++;
                }
            }

            return true;
        }

        private static bool TryScalarToUtf16(string text, int line, int scalarCharacter, out int utf16Character)
        {
            utf16Character = 0;
            if (!TryGetLine(text, line, out int lineStart, out int lineLength))
            {
                return false;
            }

            int scalar = 0;
            while (utf16Character < lineLength && scalar < scalarCharacter)
            {
                if (utf16Character + 1 < lineLength &&
                    char.IsHighSurrogate(text[lineStart + utf16Character]) &&
                    char.IsLowSurrogate(text[lineStart + utf16Character + 1]))
                {
                    utf16Character += 2;
                }
                else
                {
                    utf16Character++;
                }

                scalar++;
            }

            return scalar == scalarCharacter;
        }

        private static bool TryApplyUtf16Edit(string text, JObject range, string replacement, out string updated)
        {
            updated = text;
            if (range["start"] is not JObject start || range["end"] is not JObject end ||
                !TryAbsoluteUtf16Offset(text, start, out int startOffset) ||
                !TryAbsoluteUtf16Offset(text, end, out int endOffset) ||
                endOffset < startOffset)
            {
                return false;
            }

            updated = text.Substring(0, startOffset) + replacement + text.Substring(endOffset);
            return true;
        }

        private static bool TryAbsoluteUtf16Offset(string text, JObject position, out int offset)
        {
            offset = 0;
            if (!TryReadNonNegativeInt(position["line"], out int line) ||
                !TryReadNonNegativeInt(position["character"], out int character) ||
                !TryGetLine(text, line, out int lineStart, out int lineLength) ||
                character > lineLength)
            {
                return false;
            }

            if (character > 0 && character < lineLength &&
                char.IsHighSurrogate(text[lineStart + character - 1]) &&
                char.IsLowSurrogate(text[lineStart + character]))
            {
                return false;
            }

            offset = lineStart + character;
            return true;
        }

        private static bool TryGetLine(string text, int requestedLine, out int start, out int length)
        {
            start = 0;
            length = 0;
            int line = 0;
            while (line < requestedLine)
            {
                int newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    return false;
                }

                start = newline + 1;
                line++;
            }

            int end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            length = end - start;
            return true;
        }

        private static bool TryReadNonNegativeInt(JToken? token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            long number = token.Value<long>();
            if (number is <0 or >int.MaxValue)
            {
                return false;
            }

            value = (int)number;
            return true;
        }

        private string? GetText(string? uri, RequestContext? context)
        {
            string? normalized = NormalizeUri(uri);
            if (normalized == null)
            {
                return null;
            }

            if (context?.Text != null && string.Equals(context.Uri, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return context.Text;
            }

            if (openDocuments.TryGetValue(normalized, out string? openText))
            {
                return openText;
            }

            return GetDiskText(normalized);
        }

        private string? GetDiskText(string normalizedUri)
        {
            if (!Uri.TryCreate(normalizedUri, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            {
                return null;
            }

            try
            {
                FileInfo info = new FileInfo(uri.LocalPath);
                if (!info.Exists)
                {
                    RemoveDiskDocument(normalizedUri);
                    return null;
                }

                long lastWrite = info.LastWriteTimeUtc.Ticks;
                long length = info.Length;
                if (diskDocuments.TryGetValue(normalizedUri, out DiskDocument? cached) &&
                    cached.LastWriteTicks == lastWrite && cached.Length == length)
                {
                    TouchDiskDocument(normalizedUri);
                    return cached.Text;
                }

                string text = File.ReadAllText(info.FullName);
                AddDiskDocument(normalizedUri, new DiskDocument(lastWrite, length, text));
                return text;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException or
                SecurityException)
            {
                RemoveDiskDocument(normalizedUri);
                return null;
            }
        }

        private void SetOpenDocument(string normalizedUri, string text)
        {
            if (!openDocuments.ContainsKey(normalizedUri) && openDocuments.Count >= MaximumOpenDocuments)
            {
                string? oldest = null;
                using (Dictionary<string, string>.KeyCollection.Enumerator enumerator = openDocuments.Keys.GetEnumerator())
                {
                    if (enumerator.MoveNext())
                    {
                        oldest = enumerator.Current;
                    }
                }

                if (oldest != null)
                {
                    _=openDocuments.Remove(oldest);
                    _=documentVersions.Remove(oldest);
                }
            }

            openDocuments[normalizedUri] = text;
        }

        private void InvalidateOpenDocument(string normalizedUri)
        {
            _=openDocuments.Remove(normalizedUri);
            _=documentVersions.Remove(normalizedUri);
            documentGeneration++;
        }

        private void AddPending(JToken id, RequestContext context)
        {
            string key = RequestKey(id);
            if (pendingNodes.TryGetValue(key, out LinkedListNode<string>? existing))
            {
                pendingOrder.Remove(existing);
            }

            pendingRequests[key] = context;
            pendingNodes[key] = pendingOrder.AddLast(key);
            while (pendingRequests.Count > MaximumPendingRequests && pendingOrder.First != null)
            {
                RemovePendingKey(pendingOrder.First.Value);
            }
        }

        private void RemovePending(JToken? id)
        {
            if (id != null)
            {
                RemovePendingKey(RequestKey(id));
            }
        }

        private void RemovePendingKey(string key)
        {
            _=pendingRequests.Remove(key);
            if (pendingNodes.TryGetValue(key, out LinkedListNode<string>? node))
            {
                pendingOrder.Remove(node);
                _=pendingNodes.Remove(key);
            }
        }

        private static string RequestKey(JToken id)
        {
            return ((int)id.Type).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + id.ToString(Formatting.None);
        }

        private void AddDiskDocument(string uri, DiskDocument document)
        {
            diskDocuments[uri] = document;
            TouchDiskDocument(uri);
            while (diskDocuments.Count > MaximumDiskDocuments && diskOrder.First != null)
            {
                RemoveDiskDocument(diskOrder.First.Value);
            }
        }

        private void TouchDiskDocument(string uri)
        {
            if (diskNodes.TryGetValue(uri, out LinkedListNode<string>? existing))
            {
                diskOrder.Remove(existing);
            }

            LinkedListNode<string> node = diskOrder.AddLast(uri);
            diskNodes[uri] = node;
        }

        private void RemoveDiskDocument(string uri)
        {
            _=diskDocuments.Remove(uri);
            if (diskNodes.TryGetValue(uri, out LinkedListNode<string>? node))
            {
                diskOrder.Remove(node);
                _=diskNodes.Remove(uri);
            }
        }

        private void Clear()
        {
            openDocuments.Clear();
            documentVersions.Clear();
            documentGeneration++;
            pendingRequests.Clear();
            pendingOrder.Clear();
            pendingNodes.Clear();
            diskDocuments.Clear();
            diskNodes.Clear();
            diskOrder.Clear();
        }

        private static string? NormalizeUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                return null;
            }

            return uri.AbsoluteUri;
        }

        private sealed class RequestContext
        {
            public RequestContext(string method, string? uri, string? text)
            {
                Method = method;
                Uri = uri;
                Text = text;
            }

            public string Method { get; }

            public string? Uri { get; }

            public string? Text { get; }
        }

        private sealed class DiskDocument
        {
            public DiskDocument(long lastWriteTicks, long length, string text)
            {
                LastWriteTicks = lastWriteTicks;
                Length = length;
                Text = text;
            }

            public long LastWriteTicks { get; }

            public long Length { get; }

            public string Text { get; }
        }
    }
}
