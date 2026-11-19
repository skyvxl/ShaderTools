using Newtonsoft.Json.Linq;
using ShaderTools.Core.Preprocessing;
using Xunit;

namespace ShaderTools.Tests;

public sealed class PreprocessorTests
{
    [Fact]
    public void ServerTreeWinsOverFileSearchAndUnconfirmedConditionalIncludesStayConditional()
    {
        string root = Path.GetFullPath("C:/shaders/main.frag");
        string resolved = Path.GetFullPath("C:/virtual/shared.glsl");
        var files = new Dictionary<string, string> { [root] = "#include \"shared.glsl\"\n#if 0\n#include \"absent.glsl\"\n#endif", [resolved] = "", [Path.GetFullPath("C:/shaders/shared.glsl")] = "" };
        var context = new JObject { ["dependencyTree"] = new JObject { ["url"] = new Uri(root).AbsoluteUri,
            ["includes"] = new JArray(new JObject { ["url"] = new Uri(resolved).AbsoluteUri, ["includes"] = new JArray() }) } };
        var report = new PreprocessorInspector(path => files.GetValueOrDefault(path)).Inspect(root, files, context);
        Assert.Equal(resolved, (string?)report["includes"]![0]!["target"]);
        Assert.Equal("analysis", (string?)report["includes"]![0]!["resolution"]);
        Assert.Equal("conditional", (string?)report["includes"]![1]!["status"]);
    }

    [Fact]
    public void ResolvesNestedIncludesUsingUnsavedBuffersAndReportsCyclesAndMissingFiles()
    {
        string root = Path.GetFullPath("C:/shaders/main.frag");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [root] = "#include \"shared.glsl\"\n#include \"missing.glsl\"",
            [Path.GetFullPath("C:/shaders/shared.glsl")] = "#include \"main.frag\"\n#define FROM_BUFFER 1"
        };
        var report = new PreprocessorInspector(path => files.GetValueOrDefault(path)).Inspect(root, files, new JObject());
        Assert.Equal(new[] { "resolved", "cyclic", "missing" }, report["includes"]!.Select(i => (string)i["status"]!).ToArray());
        Assert.Contains(report["defines"]!, item => (string?)item["name"] == "FROM_BUFFER");
        Assert.True((bool)report["includes"]![0]!["unsaved"]!);
    }

    [Fact]
    public void CommentsDoNotCreateIncludesAndConditionalOrMacroIncludesAreExplicit()
    {
        string root = Path.GetFullPath("C:/shaders/main.frag");
        var files = new Dictionary<string, string> { [root] = "/*\n#include \"fake.glsl\"\n*/\n#if ENABLED\n#include HEADER\n#endif\n// #include \"fake2.glsl\"\n" };
        var report = new PreprocessorInspector(_ => null).Inspect(root, files, new JObject());
        JToken include = Assert.Single(report["includes"]!);
        Assert.Equal("dynamic", (string?)include["status"]);
        Assert.True((bool)include["conditional"]!);
        Assert.Equal(4, (int)include["line"]!);
    }

    [Fact]
    public void SearchOrderPrefersLocalThenConfiguredAndTraversalIsBounded()
    {
        string root = Path.GetFullPath("C:/shaders/main.frag");
        string local = Path.GetFullPath("C:/shaders/shared.glsl");
        var files = new Dictionary<string, string> { [root] = "#include \"shared.glsl\"\n#include \"third.glsl\"", [local] = "#include \"fourth.glsl\"" };
        var report = new PreprocessorInspector(path => files.GetValueOrDefault(path), maximumFiles: 1).Inspect(root, files, new JObject { ["includes"] = new JArray("C:/include") });
        Assert.True((bool)report["truncated"]!);
        Assert.Equal(local, (string?)report["includes"]![0]!["target"]);
        Assert.DoesNotContain(report["includes"]!, item => (string?)item["path"] == "fourth.glsl");
    }
}
