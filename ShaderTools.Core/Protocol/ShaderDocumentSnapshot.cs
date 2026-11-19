namespace ShaderTools.Core.Protocol;

public sealed class ShaderDocumentSnapshot
{
    public ShaderDocumentSnapshot(string uri, string text, int version) { Uri = uri; Text = text; Version = version; }
    public string Uri { get; }
    public string Text { get; }
    public int Version { get; }
}
