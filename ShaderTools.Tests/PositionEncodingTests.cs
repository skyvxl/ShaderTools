using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public sealed class PositionEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShaderTools.PositionTests", Guid.NewGuid().ToString("N"));

    public PositionEncodingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void IncrementalChangesUseUtf16InputAndUpdateUnsavedCrLfTextSequentially()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "editing.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "a😀b\r\nc😀d"));
        var change = JObject.Parse("""
            {
              "method": "textDocument/didChange",
              "params": {
                "textDocument": { "uri": "placeholder", "version": 2 },
                "contentChanges": [
                  { "range": { "start": { "line": 0, "character": 3 }, "end": { "line": 0, "character": 4 } }, "rangeLength": 1, "text": "B" },
                  { "range": { "start": { "line": 1, "character": 3 }, "end": { "line": 1, "character": 4 } }, "rangeLength": 1, "text": "D" }
                ]
              }
            }
            """);
        change.SelectToken("params.textDocument")!["uri"] = uri;

        converter.ProcessClientMessage(change);

        Assert.Equal(2, change.SelectToken("params.contentChanges[0].range.start.character")!.Value<int>());
        Assert.Equal(3, change.SelectToken("params.contentChanges[0].range.end.character")!.Value<int>());
        Assert.Equal(2, change.SelectToken("params.contentChanges[1].range.start.character")!.Value<int>());
        Assert.Equal(3, change.SelectToken("params.contentChanges[1].range.end.character")!.Value<int>());
        Assert.Equal("B", change.SelectToken("params.contentChanges[0].text")!.Value<string>());

        var request = PositionRequest(10, "textDocument/hover", uri, 1, 3);
        converter.ProcessClientMessage(request);
        Assert.Equal(5, request.SelectToken("params.position.character")!.Value<int>());
    }

    [Fact]
    public void PinnedServerRegressionMovesInsertionBeforeBraceInScalarCoordinates()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "real-server.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "#version 450\r\nlayout(location=0) out vec4 outColor;\r\nvoid main() { /* 😀 юникод */ outColor = vec4(1.0) }"));
        var change = JObject.Parse("""{"method":"textDocument/didChange","params":{"textDocument":{"uri":"placeholder","version":2},"contentChanges":[{"range":{"start":{"line":2,"character":51},"end":{"line":2,"character":51}},"rangeLength":0,"text":";"}]}}""");
        change.SelectToken("params.textDocument")!["uri"] = uri;

        converter.ProcessClientMessage(change);

        Assert.Equal(50, change.SelectToken("params.contentChanges[0].range.start.character")!.Value<int>());
        Assert.Equal(";", change.SelectToken("params.contentChanges[0].text")!.Value<string>());
    }

    [Fact]
    public void FullChangeReplacesCachedTextForLaterRequests()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "full.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "plain"));
        var change = JObject.Parse("""{"method":"textDocument/didChange","params":{"textDocument":{"uri":"placeholder"},"contentChanges":[{"text":"😀value"}]}}""");
        change.SelectToken("params.textDocument")!["uri"] = uri;

        converter.ProcessClientMessage(change);
        var request = PositionRequest(11, "textDocument/completion", uri, 0, 2);
        converter.ProcessClientMessage(request);

        Assert.Equal(1, request.SelectToken("params.position.character")!.Value<int>());
    }

    [Fact]
    public void ClientRequestRangesAndCodeActionDiagnosticsConvertToServerScalars()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "request-ranges.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "a😀bc"));
        var formatting = Request(12, "textDocument/rangeFormatting", uri);
        formatting.SelectToken("params")!["range"] = Range(3, 4);
        var codeAction = Request(13, "textDocument/codeAction", uri);
        codeAction.SelectToken("params")!["range"] = Range(3, 4);
        codeAction.SelectToken("params")!["context"] = new JObject
        {
            ["diagnostics"] = new JArray(new JObject { ["range"] = Range(3, 4), ["message"] = "keep 😀" }),
        };

        converter.ProcessClientMessage(formatting);
        converter.ProcessClientMessage(codeAction);

        Assert.Equal(2, formatting.SelectToken("params.range.start.character")!.Value<int>());
        Assert.Equal(3, formatting.SelectToken("params.range.end.character")!.Value<int>());
        Assert.Equal(2, codeAction.SelectToken("params.context.diagnostics[0].range.start.character")!.Value<int>());
        Assert.Equal(3, codeAction.SelectToken("params.context.diagnostics[0].range.end.character")!.Value<int>());
        Assert.Equal("keep 😀", codeAction.SelectToken("params.context.diagnostics[0].message")!.Value<string>());
    }

    [Fact]
    public void PublishDiagnosticsConvertsOnlyKnownRangesAndPreservesPayloadText()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "diagnostic.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "a😀bc"));
        var message = JObject.Parse("""
            {
              "jsonrpc": "2.0",
              "method": "textDocument/publishDiagnostics",
              "params": {
                "uri": "placeholder",
                "diagnostics": [{
                  "range": { "start": { "line": 0, "character": 2 }, "end": { "line": 0, "character": 3 } },
                  "message": "😀 exact text",
                  "data": { "range": { "start": { "line": 9, "character": 9 }, "end": { "line": 9, "character": 9 } } }
                }]
              }
            }
            """);
        message.SelectToken("params")!["uri"] = uri;

        converter.ProcessServerMessage(message);

        Assert.Equal(3, message.SelectToken("params.diagnostics[0].range.start.character")!.Value<int>());
        Assert.Equal(4, message.SelectToken("params.diagnostics[0].range.end.character")!.Value<int>());
        Assert.Equal("😀 exact text", message.SelectToken("params.diagnostics[0].message")!.Value<string>());
        Assert.Equal(9, message.SelectToken("params.diagnostics[0].data.range.start.character")!.Value<int>());
        Assert.Equal("2.0", message.Value<string>("jsonrpc"));
    }

    [Theory]
    [InlineData("textDocument/completion", "result.items[0].textEdit.range.start.character")]
    [InlineData("textDocument/hover", "result.range.start.character")]
    [InlineData("textDocument/inlayHint", "result[0].position.character")]
    [InlineData("textDocument/documentSymbol", "result[0].selectionRange.start.character")]
    [InlineData("textDocument/formatting", "result[0].range.start.character")]
    [InlineData("textDocument/rangeFormatting", "result[0].range.start.character")]
    public void ServerResponsesUseRequestDocumentContext(string method, string resultPath)
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "responses.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "a😀bc"));
        converter.ProcessClientMessage(Request(20, method, uri));
        var response = ResponseFor(method, 20);

        converter.ProcessServerMessage(response);

        Assert.Equal(3, response.SelectToken(resultPath)!.Value<int>());
    }

    [Fact]
    public void FoldingRangesConvertOptionalCharacterFields()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "fold.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "a😀bc"));
        converter.ProcessClientMessage(Request("fold", "textDocument/foldingRange", uri));
        var response = JObject.Parse("""{"id":"fold","result":[{"startLine":0,"startCharacter":1,"endLine":0,"endCharacter":3},{"startLine":0,"endLine":0}]}""");

        converter.ProcessServerMessage(response);

        Assert.Equal(1, response.SelectToken("result[0].startCharacter")!.Value<int>());
        Assert.Equal(4, response.SelectToken("result[0].endCharacter")!.Value<int>());
        Assert.Null(response.SelectToken("result[1].startCharacter"));
    }

    [Fact]
    public void DefinitionConvertsOriginAgainstSourceAndTargetAgainstDiskFile()
    {
        var converter = new PositionEncodingConverter();
        var sourcePath = Path.Combine(_root, "source.frag");
        var targetPath = Path.Combine(_root, "shared.glsl");
        File.WriteAllText(targetPath, "x😀target");
        var sourceUri = FileUri(sourcePath);
        var targetUri = FileUri(targetPath);
        converter.ProcessClientMessage(DidOpen(sourceUri, "😀use"));
        var request = PositionRequest("definition", "textDocument/definition", sourceUri, 0, 2);
        converter.ProcessClientMessage(request);
        var response = JObject.Parse("""
            {
              "id": "definition",
              "result": [{
                "originSelectionRange": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 4 } },
                "targetUri": "target",
                "targetRange": { "start": { "line": 0, "character": 1 }, "end": { "line": 0, "character": 5 } },
                "targetSelectionRange": { "start": { "line": 0, "character": 5 }, "end": { "line": 0, "character": 11 } }
              }]
            }
            """);
        response.SelectToken("result[0]")!["targetUri"] = targetUri;

        converter.ProcessServerMessage(response);

        Assert.Equal(4, request.SelectToken("params.position.character")!.Value<int>());
        Assert.Equal(2, response.SelectToken("result[0].originSelectionRange.end.character")!.Value<int>());
        Assert.Equal(3, response.SelectToken("result[0].targetRange.end.character")!.Value<int>());
        Assert.Equal(3, response.SelectToken("result[0].targetSelectionRange.start.character")!.Value<int>());
        Assert.Equal(9, response.SelectToken("result[0].targetSelectionRange.end.character")!.Value<int>());
    }

    [Fact]
    public void WorkspaceSymbolLocationUsesItsOwnFileUri()
    {
        var converter = new PositionEncodingConverter();
        var targetPath = Path.Combine(_root, "workspace-symbol.glsl");
        var targetUri = FileUri(targetPath);
        File.WriteAllText(targetPath, "😀symbol");
        converter.ProcessClientMessage(new JObject
        {
            ["id"] = "symbols",
            ["method"] = "workspace/symbol",
            ["params"] = new JObject { ["query"] = "symbol" },
        });
        var response = JObject.Parse("""{"id":"symbols","result":[{"name":"symbol","kind":13,"location":{"uri":"placeholder","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":4}}}}]}""");
        response.SelectToken("result[0].location")!["uri"] = targetUri;

        converter.ProcessServerMessage(response);

        Assert.Equal(2, response.SelectToken("result[0].location.range.end.character")!.Value<int>());
    }

    [Fact]
    public void DiskDocumentCacheReloadsWhenIncludeLastWriteChanges()
    {
        var converter = new PositionEncodingConverter();
        var sourceUri = FileUri(Path.Combine(_root, "source.frag"));
        var targetPath = Path.Combine(_root, "include.glsl");
        var targetUri = FileUri(targetPath);
        converter.ProcessClientMessage(DidOpen(sourceUri, "main"));
        File.WriteAllText(targetPath, "😀a");

        converter.ProcessClientMessage(Request(31, "textDocument/definition", sourceUri));
        var first = LocationResponse(31, targetUri, 0, 4);
        converter.ProcessServerMessage(first);
        Assert.Equal(2, first.SelectToken("result.range.end.character")!.Value<int>());

        File.WriteAllText(targetPath, "x😀a");
        File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddSeconds(2));
        converter.ProcessClientMessage(Request(32, "textDocument/definition", sourceUri));
        var second = LocationResponse(32, targetUri, 1, 5);
        converter.ProcessServerMessage(second);

        Assert.Equal(3, second.SelectToken("result.range.end.character")!.Value<int>());
    }

    [Fact]
    public void OutOfOrderResponsesUseTheirOwnRequestIdsAndAreRemovedAfterResponse()
    {
        var converter = new PositionEncodingConverter();
        var firstUri = FileUri(Path.Combine(_root, "first.frag"));
        var secondUri = FileUri(Path.Combine(_root, "second.frag"));
        converter.ProcessClientMessage(DidOpen(firstUri, "😀a"));
        converter.ProcessClientMessage(DidOpen(secondUri, "xx😀b"));
        converter.ProcessClientMessage(Request("first", "textDocument/hover", firstUri));
        converter.ProcessClientMessage(Request(42, "textDocument/hover", secondUri));
        var secondResponse = RangeResponse(42, 2, 6);
        var firstResponse = RangeResponse("first", 0, 4);

        converter.ProcessServerMessage(secondResponse);
        converter.ProcessServerMessage(firstResponse);

        Assert.Equal(4, secondResponse.SelectToken("result.range.end.character")!.Value<int>());
        Assert.Equal(2, firstResponse.SelectToken("result.range.end.character")!.Value<int>());

        var duplicateResponse = RangeResponse("first", 0, 1);
        converter.ProcessServerMessage(duplicateResponse);
        Assert.Equal(1, duplicateResponse.SelectToken("result.range.end.character")!.Value<int>());
    }

    [Fact]
    public void ReusedRequestIdRemainsTrackedWhenTheBoundedCacheEvictsOlderRequests()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "reused.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "😀a"));
        converter.ProcessClientMessage(Request("reused", "textDocument/hover", uri));
        converter.ProcessServerMessage(RangeResponse("reused", 0, 4));
        for (var id = 0; id < 512; id++)
        {
            converter.ProcessClientMessage(Request(id, "textDocument/hover", uri));
        }

        converter.ProcessClientMessage(Request("reused", "textDocument/hover", uri));
        var response = RangeResponse("reused", 0, 4);
        converter.ProcessServerMessage(response);

        Assert.Equal(2, response.SelectToken("result.range.end.character")!.Value<int>());
    }

    [Fact]
    public void SemanticTokenDeltasAndLengthsConvertToUtf16()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "tokens.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "😀аб\r\nx😀yz"));
        converter.ProcessClientMessage(Request(50, "textDocument/semanticTokens/full", uri));
        var response = JObject.Parse("""{"id":50,"result":{"data":[0,1,4,1,0,1,2,2,1,0]}}""");

        converter.ProcessServerMessage(response);

        Assert.Equal(new[] { 0, 2, 2, 1, 0, 1, 3, 2, 1, 0 }, response.SelectToken("result.data")!.Values<int>());
    }

    [Fact]
    public void UnknownTargetAndInvalidClientRangeArePreservedWithoutThrowing()
    {
        var converter = new PositionEncodingConverter();
        var sourceUri = FileUri(Path.Combine(_root, "known.frag"));
        converter.ProcessClientMessage(DidOpen(sourceUri, "a😀b"));
        var invalidChange = JObject.Parse("""{"method":"textDocument/didChange","params":{"textDocument":{"uri":"placeholder"},"contentChanges":[{"range":{"start":{"line":0,"character":2},"end":{"line":0,"character":2}},"text":"x"}]}}""");
        invalidChange.SelectToken("params.textDocument")!["uri"] = sourceUri;

        converter.ProcessClientMessage(invalidChange);
        Assert.Equal(2, invalidChange.SelectToken("params.contentChanges[0].range.start.character")!.Value<int>());

        converter.ProcessClientMessage(DidOpen(sourceUri, "😀known"));

        converter.ProcessClientMessage(Request(55, "textDocument/definition", sourceUri));
        var response = JObject.Parse("""
            {
              "id": 55,
              "result": {
                "originSelectionRange": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 4 } },
                "targetUri": "untitled:missing",
                "targetRange": { "start": { "line": 0, "character": 7 }, "end": { "line": 0, "character": 8 } },
                "targetSelectionRange": { "start": { "line": 0, "character": 7 }, "end": { "line": 0, "character": 8 } }
              }
            }
            """);

        converter.ProcessServerMessage(response);

        Assert.Equal(2, response.SelectToken("result.originSelectionRange.end.character")!.Value<int>());
        Assert.Equal(7, response.SelectToken("result.targetRange.start.character")!.Value<int>());
    }

    [Fact]
    public void CloseAndShutdownDiscardDocumentState()
    {
        var converter = new PositionEncodingConverter();
        var uri = FileUri(Path.Combine(_root, "close.frag"));
        converter.ProcessClientMessage(DidOpen(uri, "😀a"));
        converter.ProcessClientMessage(new JObject
        {
            ["method"] = "textDocument/didClose",
            ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
        });
        var afterClose = PositionRequest(60, "textDocument/hover", uri, 0, 2);
        converter.ProcessClientMessage(afterClose);
        Assert.Equal(2, afterClose.SelectToken("params.position.character")!.Value<int>());

        converter.ProcessClientMessage(DidOpen(uri, "😀a"));
        converter.ProcessClientMessage(JObject.Parse("""{"id":61,"method":"shutdown"}"""));
        var afterShutdown = PositionRequest(62, "textDocument/hover", uri, 0, 2);
        converter.ProcessClientMessage(afterShutdown);
        Assert.Equal(2, afterShutdown.SelectToken("params.position.character")!.Value<int>());
    }

    private static JObject DidOpen(string uri, string text) => new JObject
    {
        ["method"] = "textDocument/didOpen",
        ["params"] = new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri, ["text"] = text, ["version"] = 1 },
        },
    };

    private static JObject Request(object id, string method, string uri) => new JObject
    {
        ["id"] = JToken.FromObject(id),
        ["method"] = method,
        ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    private static JObject PositionRequest(object id, string method, string uri, int line, int character)
    {
        var request = Request(id, method, uri);
        request.SelectToken("params")!["position"] = new JObject { ["line"] = line, ["character"] = character };
        return request;
    }

    private static JObject ResponseFor(string method, object id)
    {
        const string scalarRange = "{start:{line:0,character:2},end:{line:0,character:3}}";
        const string syntaxRange = "{start:{line:0,character:5},end:{line:0,character:6}}";
        var response = method switch
        {
            "textDocument/completion" => JObject.Parse($"{{result:{{items:[{{label:'x',textEdit:{{range:{scalarRange},newText:'x'}}}}]}}}}"),
            "textDocument/hover" => JObject.Parse($"{{result:{{contents:'x',range:{syntaxRange}}}}}"),
            "textDocument/inlayHint" => JObject.Parse("{result:[{position:{line:0,character:5},label:'x'}]}"),
            "textDocument/documentSymbol" => JObject.Parse($"{{result:[{{name:'x',kind:12,range:{syntaxRange},selectionRange:{syntaxRange}}}]}}"),
            "textDocument/formatting" => JObject.Parse($"{{result:[{{range:{scalarRange},newText:'x'}}]}}"),
            "textDocument/rangeFormatting" => JObject.Parse($"{{result:[{{range:{scalarRange},newText:'x'}}]}}"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
        response["id"] = JToken.FromObject(id);
        return response;
    }

    private static JObject LocationResponse(object id, string uri, int start, int end) => new JObject
    {
        ["id"] = JToken.FromObject(id),
        ["result"] = new JObject
        {
            ["uri"] = uri,
            ["range"] = new JObject
            {
                ["start"] = new JObject { ["line"] = 0, ["character"] = start },
                ["end"] = new JObject { ["line"] = 0, ["character"] = end },
            },
        },
    };

    private static JObject RangeResponse(object id, int start, int end) => new JObject
    {
        ["id"] = JToken.FromObject(id),
        ["result"] = new JObject
        {
            ["contents"] = "hover",
            ["range"] = new JObject
            {
                ["start"] = new JObject { ["line"] = 0, ["character"] = start },
                ["end"] = new JObject { ["line"] = 0, ["character"] = end },
            },
        },
    };

    private static JObject Range(int start, int end) => new JObject
    {
        ["start"] = new JObject { ["line"] = 0, ["character"] = start },
        ["end"] = new JObject { ["line"] = 0, ["character"] = end },
    };

    private static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
