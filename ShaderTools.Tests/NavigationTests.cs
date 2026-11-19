using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void VisualStudioEncodedDriveOpensTheNativeWindowsPath()
    {
        var link = JObject.Parse("""{"targetUri":"file:///H%3A/Shader%20Tools/%D0%AE.glsl","targetSelectionRange":{"start":{"line":3,"character":9}}}""");
        Assert.Equal(@"H:\Shader Tools\Ю.glsl", DefinitionLocation.Read(link).Single().FilePath);
    }

    [Fact]
    public void IncludeLinkAllowsZeroLengthTargetAndDecodesUnicodePath()
    {
        var link = JObject.Parse("""{"targetUri":"file:///C:/shaders%20%C3%BC/colors.glsl","targetRange":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"targetSelectionRange":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}}}""");
        var target = DefinitionLocation.Read(new JArray(link)).Single();
        Assert.Equal(@"C:\shaders ü\colors.glsl", target.FilePath);
        Assert.Equal(0, target.Line);
        Assert.Equal(0, target.Character);
    }

    [Fact]
    public void LocationAndLocationArrayUseTheRangeStart()
    {
        var location = JObject.Parse("""{"uri":"file:///C:/shader.frag","range":{"start":{"line":3,"character":9},"end":{"line":3,"character":12}}}""");
        Assert.Equal(3, DefinitionLocation.Read(location).Single().Line);
        Assert.Equal(9, DefinitionLocation.Read(new JArray(location)).Single().Character);
    }

    [Fact]
    public void LocationLinkUsesSelectionInsteadOfTheDeclarationRange()
    {
        var link = JObject.Parse("""{"targetUri":"file:///C:/shader.frag","targetRange":{"start":{"line":1,"character":0},"end":{"line":10,"character":0}},"targetSelectionRange":{"start":{"line":2,"character":5},"end":{"line":2,"character":8}}}""");
        var target = DefinitionLocation.Read(new JArray(link)).Single();
        Assert.Equal(2, target.Line);
        Assert.Equal(5, target.Character);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("[null,42,{}]")]
    [InlineData("""{"uri":"https://example.com/shader","range":{"start":{"line":0,"character":0}}}""")]
    [InlineData("""{"uri":"shader.frag","range":{"start":{"line":0,"character":0}}}""")]
    [InlineData("""{"uri":"file:///C:/shader.frag","range":{"start":{"line":-1,"character":0}}}""")]
    [InlineData("""{"uri":"file:///C:/shader.frag","range":{"start":{"line":0,"character":2147483648}}}""")]
    [InlineData("""{"uri":"file:///C:/shader.frag","range":{"start":{"line":"0","character":0}}}""")]
    public void IgnoresMissingMalformedAndNonFileTargets(string json)
        => Assert.Empty(DefinitionLocation.Read(JToken.Parse(json)));
}
