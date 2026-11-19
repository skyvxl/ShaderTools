using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public sealed class LocalFeatureTests
{
    [Fact]
    public void InspectionRejectsContextAfterAnotherBufferOrConfigurationChanges()
    {
        var documents = new PositionEncodingConverter();
        const string uri = "file:///C:/shaders/main.frag";
        documents.ProcessClientMessage(new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = 1, ["text"] = "void main(){}" } } });
        var features = new LocalShaderFeatures(documents, new ProtocolAdapter("C:/shaders", _ => new JObject()));
        var context = new JObject { ["documentVersion"] = 1, ["snapshotGeneration"] = documents.DocumentGeneration };
        var request = new JObject { ["method"] = "shaderTools/preprocessor", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri }, ["context"] = context } };
        Assert.NotNull(features.Execute(request, default).Value);
        documents.ProcessClientMessage(new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = "file:///C:/shaders/shared.glsl", ["version"] = 2, ["text"] = "#define ACTIVE 1" } } });
        Assert.Throws<InvalidOperationException>(() => features.Execute(request, default));
        context["snapshotGeneration"] = documents.DocumentGeneration;
        Assert.NotNull(features.Execute(request, default).Value);
        documents.ProcessClientMessage(new JObject { ["method"] = "textDocument/didChangeShaderVariant" });
        Assert.Throws<InvalidOperationException>(() => features.Execute(request, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationActionRequiresCurrentServerDiagnosticsAndRechecksVersionBeforeExecution(bool nested)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ShaderTools-action-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string project = nested ? Path.Combine(directory, "BasicShaders") : directory;
            Directory.CreateDirectory(project);
            string configurationPath = Path.Combine(project, ".shadertools.json");
            File.WriteAllText(configurationPath, "{}");
            string uri = new Uri(Path.Combine(project, "main.frag")).AbsoluteUri;
            var positions = new PositionEncodingConverter();
            positions.ProcessClientMessage(new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = 3, ["text"] = "#include \"missing.glsl\"" } } });
            var features = new LocalShaderFeatures(positions, new ProtocolAdapter(directory, _ => new JObject()));
            var request = new JObject { ["method"] = "textDocument/codeAction", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri }, ["range"] = Range(0) } };
            Assert.Empty(features.Execute(request, default).Value);
            JObject diagnostic = new() { ["range"] = Range(0), ["message"] = "include unavailable", ["severity"] = 1 };
            var action = Assert.Single(features.Execute(request, default, new JArray(diagnostic)).Value);
            var execute = new JObject { ["method"] = "workspace/executeCommand", ["params"] = action["command"]!.DeepClone() };
            var result = features.Execute(execute, default);
            Assert.Equal("shaderTools/openConfiguration", (string?)result.Notification!["method"]);
            Assert.Equal(new Uri(configurationPath).AbsoluteUri, (string?)result.Notification.SelectToken("params.uri"));
            positions.ProcessClientMessage(new JObject { ["method"] = "textDocument/didChange", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = 4 }, ["contentChanges"] = new JArray(new JObject { ["text"] = "void main(){}" }) } });
            Assert.Empty(features.Execute(request, default).Value);
            Assert.Throws<InvalidOperationException>(() => features.Execute(execute, default));
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("ShaderTools-action-", Path.GetFileName(resolved));
            Directory.Delete(resolved, true);
        }
    }

    private static JObject Range(int line) => new() { ["start"] = new JObject { ["line"] = line, ["character"] = 0 }, ["end"] = new JObject { ["line"] = line, ["character"] = 20 } };

    [Fact]
    public void SnapshotCaptureKeepsUnsavedUtf16EditsAndVersion()
    {
        var positions = new PositionEncodingConverter();
        positions.ProcessClientMessage(new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = "file:///C:/test.frag", ["version"] = 1, ["text"] = "// 🟩\n#include \"old.glsl\"" } } });
        positions.ProcessClientMessage(new JObject { ["method"] = "textDocument/didChange", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = "file:///C:/test.frag", ["version"] = 2 }, ["contentChanges"] = new JArray(new JObject { ["text"] = "// 🟩\n#include \"new.glsl\"" }) } });
        var snapshot = Assert.Single(positions.CaptureOpenDocuments());
        Assert.Equal(2, snapshot.Version);
        Assert.Contains("new.glsl", snapshot.Text);
        positions.ProcessClientMessage(new JObject { ["method"] = "textDocument/didClose", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = snapshot.Uri } } });
        Assert.Empty(positions.CaptureOpenDocuments());
    }
}
