using Newtonsoft.Json.Linq;
using ShaderTools.Core.Editor;
using Xunit;

namespace ShaderTools.Tests;

public sealed class DocumentSymbolTests
{
    [Fact]
    public void NestedTypesExposeMembersWithoutPromotingFunctionLocals()
    {
        var type = Symbol("Light", 23, 0, 12);
        var method = Symbol("sample", 6, 3, 10, 4);
        method["children"] = new JArray(Symbol("local", 13, 6, 6));
        type["children"] = new JArray(Symbol("color", 8, 1, 1), method);
        var model = ShaderDocumentSymbols.Read(new JArray(type, Symbol("main", 12, 15, 25)));
        Assert.Equal(new[] { "(Global Scope)", "Light" }, model.Scopes.Select(x => x.Label));
        Assert.Equal(new[] { "main" }, model.Scopes[0].Members.Select(x => x.Label));
        Assert.Equal(new[] { "color", "sample" }, model.Scopes[1].Members.Select(x => x.Label));
        Assert.Equal((1, 1), model.Select(8, 0));
        Assert.Equal(4, model.Scopes[1].Members[1].SelectionStart.Line);
        Assert.Equal((0, 0), model.Select(20, 0));
    }

    [Fact]
    public void FlatLegacySymbolsUseLocationAndRejectLocalsAndOtherDocuments()
    {
        var type = Flat("Light", 23, 0, 12);
        var member = Flat("color", 8, 2, 2, "Light");
        var method = Flat("sample", 6, 4, 10, "Light");
        var local = Flat("local", 13, 6, 6, "sample");
        var foreign = Flat("foreign", 12, 20, 24);
        foreign["location"]!["uri"] = "file:///C:/other.glsl";
        var model = ShaderDocumentSymbols.Read(new JArray(type, member, method, local, foreign), "file:///C:/shader.glsl");
        Assert.Empty(model.Scopes[0].Members);
        Assert.Equal(new[] { "color", "sample" }, model.Scopes[1].Members.Select(x => x.Label));
        Assert.Equal(4, model.Scopes[1].Members[1].SelectionStart.Line);
    }

    [Fact]
    public void OfficialServerFlatDocumentSymbolsKeepStructAndExcludeFunctionLocal()
    {
        var type = Symbol("Material", 26, 1, 1);
        type["detail"] = "struct Material";
        var local = Symbol("sampleValue", 13, 3, 3);
        local["range"]!["start"]!["character"] = 34;
        local["range"]!["end"]!["character"] = 45;
        var main = Symbol("main", 12, 3, 3);
        main["range"]!["start"]!["character"] = 19;
        main["range"]!["end"]!["character"] = 68;
        var model = ShaderDocumentSymbols.Read(new JArray(type, local, main));
        Assert.Equal("Material", model.Scopes[1].Name);
        Assert.Equal("main", Assert.Single(model.Scopes[0].Members).Name);
    }

    [Fact]
    public void OverloadsUseSignaturesAndDuplicateSignaturesUseLocations()
    {
        var a = Symbol("mix", 12, 0, 2); a["detail"] = "float mix(float x)";
        var b = Symbol("mix", 12, 4, 6); b["detail"] = "vec2 mix(vec2 x)";
        var c = Symbol("mix", 12, 8, 10); c["detail"] = "float mix(float x)";
        var labels = ShaderDocumentSymbols.Read(new JArray(a, b, c)).Scopes[0].Members.Select(x => x.Label).ToArray();
        Assert.Contains("float mix(float x)", labels[0]);
        Assert.Contains("vec2 mix(vec2 x)", labels[1]);
        Assert.Equal(3, labels.Distinct().Count());
    }

    [Fact]
    public void FlatLocalWithoutContainerIsExcludedByEnclosingFunctionRange()
    {
        var model = ShaderDocumentSymbols.Read(new JArray(Flat("main", 12, 0, 10), Flat("temporary", 13, 3, 3)));
        Assert.Single(model.Scopes[0].Members);
    }

    [Fact]
    public void LegacyTypeDeclarationRangeStillFollowsItsMemberBody()
    {
        var model = ShaderDocumentSymbols.Read(new JArray(Flat("Light", 23, 0, 0), Flat("sample", 6, 4, 10, "Light")));
        Assert.Equal((1, 0), model.Select(8, 0));
    }

    [Fact]
    public void SmallestNestedScopeFollowsBodyWithHalfOpenRange()
    {
        var outer = Symbol("Outer", 3, 0, 20);
        var inner = Symbol("Inner", 23, 2, 12);
        inner["children"] = new JArray(Symbol("method", 6, 4, 10));
        outer["children"] = new JArray(inner);
        var model = ShaderDocumentSymbols.Read(new JArray(outer));
        Assert.Equal("Outer::Inner", model.Scopes[2].Label);
        Assert.Equal((2, 0), model.Select(8, 3));
        Assert.Equal((2, -1), model.Select(10, 0));
        Assert.Equal((0, -1), model.Select(30, 0));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("[null,{}, {\"name\":\"bad\",\"kind\":12,\"range\":42}]")]
    [InlineData("[{\"name\":\"bad\",\"kind\":999999999999999999999999,\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":1,\"character\":0}}}]")]
    public void EmptyOrMalformedResultsHaveOnlyGlobalScope(string json)
    {
        var model = ShaderDocumentSymbols.Read(JToken.Parse(json));
        Assert.Single(model.Scopes);
        Assert.Empty(model.Scopes[0].Members);
        Assert.Equal((0, -1), model.Select(0, 0));
    }

    private static JObject Symbol(string name, int kind, int start, int end, int? selection = null) => new()
    {
        ["name"] = name, ["kind"] = kind, ["range"] = Range(start, end),
        ["selectionRange"] = Range(selection ?? start, selection ?? start)
    };
    private static JObject Flat(string name, int kind, int start, int end, string? container = null) => new()
    {
        ["name"] = name, ["kind"] = kind, ["containerName"] = container,
        ["location"] = new JObject { ["uri"] = "file:///C:/shader.glsl", ["range"] = Range(start, end) }
    };
    private static JObject Range(int start, int end) => new()
    {
        ["start"] = new JObject { ["line"] = start, ["character"] = 0 },
        ["end"] = new JObject { ["line"] = end, ["character"] = 0 }
    };
}
