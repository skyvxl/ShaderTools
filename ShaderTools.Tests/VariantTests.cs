using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using Xunit;

namespace ShaderTools.Tests;

public sealed class VariantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShaderTools.Tests", Guid.NewGuid().ToString("N"));

    public VariantTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void NestedShaderUsesItsProjectCatalogWhenTheParentFolderIsOpen()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "BasicShaders")).FullName;
        string file = Path.Combine(project, "shaders", "triangle.frag");
        File.WriteAllText(Path.Combine(project, ShaderConfiguration.FileName), "{}");
        string catalog = Path.Combine(project, VariantCatalog.FileName);
        Assert.Equal(catalog, VariantCatalog.FindProjectCatalog(_root, file));
        File.WriteAllText(catalog, """[{"name":"Warm","file":"shaders/triangle.frag","entryPoint":"main","defines":{"WARM":"1"}}]""");
        File.WriteAllText(Path.Combine(_root, VariantCatalog.FileName), "[]");
        Assert.Equal(catalog, VariantCatalog.FindProjectCatalog(_root, file));
        Assert.Equal(file, Assert.Single(VariantCatalog.Load(catalog, project)).FilePath);
    }

    [Fact]
    public void MissingCatalogReturnsEmptyCollection()
    {
        Assert.Empty(VariantCatalog.Load(Path.Combine(_root, "missing.json"), _root));
    }

    [Fact]
    public void NativeCatalogResolvesPathsNormalizesStageAndCreatesGlslProtocolPayload()
    {
        var path = Path.Combine(_root, ".shader-variants.json");
        File.WriteAllText(path, """
            [
              {
                "name": "Lit fragment",
                "file": "shaders/lit.frag",
                "entryPoint": "main",
                "stage": "fragment",
                "defines": { "USE_LIGHTS": "1" },
                "includes": ["include", "${workspaceFolder}/generated"]
              },
              {
                "name": "Ray generation",
                "file": "shaders/path.rgen",
                "entryPoint": "raygen",
                "stage": "ray_generation"
              }
            ]
            """);

        var variants = VariantCatalog.Load(path, _root);

        var fragment = Assert.Single(variants, item => item.Name == "Lit fragment");
        Assert.Equal(Path.Combine(_root, "shaders", "lit.frag"), fragment.FilePath);
        Assert.Equal("main", fragment.EntryPoint);
        Assert.Equal("fragment", fragment.Stage);
        Assert.Equal("1", fragment.Defines["USE_LIGHTS"]);
        Assert.Equal(new[] { Path.Combine(_root, "include"), Path.Combine(_root, "generated") }, fragment.Includes);

        var ray = Assert.Single(variants, item => item.Name == "Ray generation");
        Assert.Equal("rayGeneration", ray.Stage);

        var protocol = fragment.ToProtocol();
        Assert.Equal(new Uri(fragment.FilePath).AbsoluteUri, protocol.Value<string>("url"));
        Assert.Equal("Glsl", protocol.Value<string>("shadingLanguage"));
        Assert.Equal("main", protocol.Value<string>("entryPoint"));
        Assert.Equal("fragment", protocol.Value<string>("stage"));
        Assert.Equal("1", protocol.SelectToken("defines.USE_LIGHTS")?.Value<string>());
        Assert.Equal(fragment.Includes, protocol["includes"]!.Values<string>());
        Assert.Equal(new[] { "url", "shadingLanguage", "entryPoint", "stage", "defines", "includes" }, protocol.Properties().Select(p => p.Name));
    }

    [Theory]
    [InlineData("effect.hlsl", "Hlsl")]
    [InlineData("common.hlsli", "Hlsl")]
    [InlineData("effect.fx", "Hlsl")]
    [InlineData("material.wgsl", "Wgsl")]
    [InlineData("legacy.vsh", "Glsl")]
    [InlineData("trace.rchit", "Glsl")]
    public void ProtocolUsesLanguageForKnownShaderExtension(string file, string language)
    {
        var path = WriteNativeCatalog(file, "auto");

        var variant = Assert.Single(VariantCatalog.Load(path, _root));
        var protocol = variant.ToProtocol();

        Assert.Null(variant.Stage);
        Assert.Equal(language, protocol.Value<string>("shadingLanguage"));
        Assert.Equal(JTokenType.Null, protocol["stage"]!.Type);
    }

    [Fact]
    public void ValidatorDatabaseUsesUriAndVariantNameAsEntryPoint()
    {
        var path = Path.Combine(_root, "variant.config.json");
        File.WriteAllText(path, """
            [
              {
                "uri": "shaders/test.variant.hlsl",
                "variants": [
                  {
                    "name": "mainOk",
                    "stage": "vertex",
                    "defines": { "VARIANT_DEFINE": "1" },
                    "includes": ["shared"]
                  }
                ]
              }
            ]
            """);

        var variant = Assert.Single(VariantCatalog.Load(path, _root));

        Assert.Equal("mainOk", variant.Name);
        Assert.Equal("mainOk", variant.EntryPoint);
        Assert.Equal(Path.Combine(_root, "shaders", "test.variant.hlsl"), variant.FilePath);
        Assert.Equal(Path.Combine(_root, "shared"), Assert.Single(variant.Includes));
        Assert.Equal("Hlsl", variant.ToProtocol().Value<string>("shadingLanguage"));
    }

    [Fact]
    public void ValidatorDatabaseAcceptsLocalFileUriAndReturnsImmutableCollections()
    {
        var shaderPath = Path.Combine(_root, "shader with space.frag");
        var path = Path.Combine(_root, "variant.config.json");
        var database = new JArray(
            new JObject
            {
                ["uri"] = new Uri(shaderPath).AbsoluteUri,
                ["variants"] = new JArray(
                    new JObject
                    {
                        ["name"] = "main",
                        ["stage"] = "fragment",
                        ["defines"] = new JObject { ["MODE"] = "1" },
                        ["includes"] = new JArray("include"),
                    }),
            });
        File.WriteAllText(path, database.ToString());

        var variant = Assert.Single(VariantCatalog.Load(path, _root));

        Assert.Equal(shaderPath, variant.FilePath);
        Assert.Contains("shader%20with%20space.frag", variant.ToProtocol().Value<string>("url"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)variant.Defines).Add("OTHER", "2"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)variant.Includes).Add("other"));
    }

    [Fact]
    public void ExampleWorkspaceConfigurationAndVariantsLoadTogether()
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "BasicShaders"));

        var configuration = ShaderConfiguration.Load(workspace);
        var variants = VariantCatalog.Load(Path.Combine(workspace, ".shader-variants.json"), workspace);

        Assert.Equal(Path.Combine(workspace, "include"), Assert.Single(configuration.Settings["includes"]!.Values<string>()));
        Assert.Equal(4, variants.Count);
        Assert.Equal(2, variants.Count(variant => variant.ToProtocol().Value<string>("shadingLanguage") == "Glsl"));
        Assert.Equal(2, variants.Count(variant => variant.ToProtocol().Value<string>("shadingLanguage") == "Hlsl"));
        Assert.All(variants, variant => Assert.True(File.Exists(variant.FilePath), variant.FilePath));
    }

    [Fact]
    public void DuplicateNamesForSameFileAreRejected()
    {
        var path = Path.Combine(_root, ".shader-variants.json");
        File.WriteAllText(path, """
            [
              { "name": "Main", "file": "a.frag", "entryPoint": "main", "stage": "fragment" },
              { "name": "main", "file": "a.frag", "entryPoint": "debug", "stage": "fragment" }
            ]
            """);

        var error = Assert.Throws<InvalidDataException>(() => VariantCatalog.Load(path, _root));

        Assert.Contains(path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Main", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[{ \"name\": \"Bad\", \"file\": \"a.frag\", \"entryPoint\": \"main\", \"stage\": \"pixel\" }]", "stage")]
    [InlineData("[{ \"name\": \"Bad\", \"file\": \"a.frag\", \"entryPoint\": \"main\", \"defines\": { \"COUNT\": 2 } }]", "defines.COUNT")]
    [InlineData("[{ \"name\": \"Bad\", \"file\": \"a.frag\", \"entryPoint\": \"main\", \"includes\": [2] }]", "includes[0]")]
    [InlineData("[{ \"name\": \"Bad\", \"file\": \"readme.txt\", \"entryPoint\": \"main\" }]", "extension")]
    public void MalformedNativeEntriesAreRejectedWithProperty(string json, string property)
    {
        var path = Path.Combine(_root, ".shader-variants.json");
        File.WriteAllText(path, json);

        var error = Assert.Throws<InvalidDataException>(() => VariantCatalog.Load(path, _root));

        Assert.Contains(Path.GetFileName(path), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(property, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJsonIsWrappedAsInvalidDataException()
    {
        var path = Path.Combine(_root, ".shader-variants.json");
        File.WriteAllText(path, "[");

        var error = Assert.Throws<InvalidDataException>(() => VariantCatalog.Load(path, _root));

        Assert.Contains(path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<Newtonsoft.Json.JsonReaderException>(error.InnerException);
    }

    private string WriteNativeCatalog(string file, string stage)
    {
        var path = Path.Combine(_root, ".shader-variants.json");
        var json = new JArray(
            new JObject
            {
                ["name"] = "Main",
                ["file"] = file,
                ["entryPoint"] = "main",
                ["stage"] = stage,
            });
        File.WriteAllText(path, json.ToString());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
