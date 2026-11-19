using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using Xunit;

namespace ShaderTools.Tests;

public sealed class DocumentConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ShaderTools.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParentFolderUsesNestedSettingsAndResolvesPathsAtTheirSource()
    {
        Write(".shadertools.json", """{"defines":{"ROOT":"1","WINNER":"root"},"includes":["shared"],"pathRemapping":{"/common":"common"}}""");
        string configurationPath = Write("BasicShaders/.shadertools.json", """{"defines":{"CHILD":"1","WINNER":"child"},"includes":["include","${workspaceFolder}/shared"],"glsl":{"preamble":"preamble.glsl"}}""");
        string document = Write("BasicShaders/shaders/triangle.frag", "void main() {}");

        var configuration = ShaderConfiguration.LoadForDocument(root, document);

        Assert.Equal(configurationPath, configuration.ConfigurationPath);
        Assert.Equal(new[] { Path.Combine(root, "BasicShaders", "include"), Path.Combine(root, "shared") }, configuration.Settings["includes"]!.Values<string>());
        Assert.Equal("1", (string?)configuration.Settings.SelectToken("defines.ROOT"));
        Assert.Equal("1", (string?)configuration.Settings.SelectToken("defines.CHILD"));
        Assert.Equal("child", (string?)configuration.Settings.SelectToken("defines.WINNER"));
        Assert.Equal(Path.Combine(root, "common"), (string?)configuration.Settings["pathRemapping"]!["/common"]);
        Assert.Equal(Path.Combine(root, "BasicShaders", "preamble.glsl"), (string?)configuration.Settings.SelectToken("glsl.preamble"));
        Assert.Equal(configurationPath, ShaderConfiguration.FindConfigurationPath(root, document));
    }

    [Fact]
    public void NestedProjectsDoNotBorrowSiblingOrOutsideWorkspaceSettings()
    {
        Write(".shadertools.json", """{"defines":{"OUTSIDE":"1"}}""");
        string workspace = Path.Combine(root, "examples");
        Write("examples/first/.shadertools.json", """{"includes":["include"],"defines":{"FIRST":"1"}}""");
        Write("examples/second/.shadertools.json", """{"includes":["other"],"defines":{"SECOND":"1"}}""");
        string first = Write("examples/first/shader.frag", "");
        string second = Write("examples/second/shader.hlsl", "");
        string plain = Write("examples/plain/shader.frag", "");

        Assert.Equal(new[] { Path.Combine(workspace, "first", "include") }, ShaderConfiguration.LoadForDocument(workspace, first).Settings["includes"]!.Values<string>());
        var settings = ShaderConfiguration.LoadForDocument(workspace, second).Settings;
        Assert.Equal(new[] { Path.Combine(workspace, "second", "other") }, settings["includes"]!.Values<string>());
        Assert.Null(settings.SelectToken("defines.FIRST"));
        Assert.Null(settings.SelectToken("defines.OUTSIDE"));
        Assert.Empty((JObject)ShaderConfiguration.LoadForDocument(workspace, plain).Settings["defines"]!);
    }

    [Fact]
    public void ExternalDocumentUsesItsOwnNearestProject()
    {
        Write("workspace/.shadertools.json", """{"defines":{"WRONG":"1"}}""");
        string externalConfig = Write("workspace-other/.shadertools.json", """{"includes":["include"]}""");
        string document = Write("workspace-other/shaders/test.frag", "");
        var configuration = ShaderConfiguration.LoadForDocument(Path.Combine(root, "workspace"), document);
        Assert.Equal(externalConfig, configuration.ConfigurationPath);
        Assert.Equal(new[] { Path.Combine(root, "workspace-other", "include") }, configuration.Settings["includes"]!.Values<string>());
        Assert.Null(configuration.Settings.SelectToken("defines.WRONG"));
    }

    [Fact]
    public void ReloadKeepsLastGoodSettingsForOnlyTheAffectedDocument()
    {
        Write(".shadertools.json", """{"defines":{"ROOT":"1"}}""");
        Write("first/.shadertools.json", """{"includes":["include"]}""");
        Write("second/.shadertools.json", """{"includes":["other"]}""");
        string first = Write("first/test.frag", "");
        string second = Write("second/test.hlsl", "");
        var logs = new List<string>();
        var store = new ShaderConfigurationStore(log: logs.Add);
        var original = store.LoadDocument(root, first);
        store.LoadDocument(root, second);
        Write("first/.shadertools.json", "{ broken");
        Write("second/.shadertools.json", """{"includes":["updated"]}""");

        Assert.True(JToken.DeepEquals(original.Settings, store.LoadDocument(root, first).Settings));
        Assert.Equal(new[] { Path.Combine(root, "second", "updated") }, store.LoadDocument(root, second).Settings["includes"]!.Values<string>());
        Assert.Contains(logs, log => log.Contains("first"));
        Write("first/.shadertools.json", """{"includes":["fixed"]}""");
        Assert.Equal(new[] { Path.Combine(root, "first", "fixed") }, store.LoadDocument(root, first).Settings["includes"]!.Values<string>());
        File.Delete(Path.Combine(root, "first", ".shadertools.json"));
        Assert.Empty((JArray)store.LoadDocument(root, first).Settings["includes"]!);
        Assert.Equal(Path.Combine(root, ".shadertools.json"), store.LoadDocument(root, first).ConfigurationPath);
    }

    [Fact]
    public void InvalidNewChildFallsBackToWorkspaceAndDoesNotStopOtherDocuments()
    {
        Write(".shadertools.json", """{"includes":["shared"]}""");
        Write("broken/.shadertools.json", "{ broken");
        string document = Write("broken/shader.frag", "");
        var store = new ShaderConfigurationStore();
        var configuration = store.LoadDocument(root, document);
        Assert.Equal(Path.Combine(root, ".shadertools.json"), configuration.ConfigurationPath);
        Assert.Equal(new[] { Path.Combine(root, "shared") }, configuration.Settings["includes"]!.Values<string>());
    }

    private string Write(string relative, string text)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ShaderTools.Tests"), Path.GetDirectoryName(Path.GetFullPath(root)));
        Directory.Delete(root, recursive: true);
    }
}
