using System.Text;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task RedirectedFrameworkStdinPreambleIsAcceptedOnlyAtConnectionStart()
    {
        using var wire = new MemoryStream();
        var bom = new System.Text.UTF8Encoding(true).GetPreamble();
        wire.Write(bom);
        await LspFraming.WriteAsync(wire, new JObject { ["id"] = 1 }, default);
        wire.Write(bom);
        await LspFraming.WriteAsync(wire, new JObject { ["id"] = 2 }, default);
        wire.Position = 0;
        Assert.Equal(1, (int)(await LspFraming.ReadAsync(wire, default, allowUtf8Preamble: true))!["id"]!);
        await Assert.ThrowsAsync<InvalidDataException>(() => LspFraming.ReadAsync(wire, default));
    }

    [Fact]
    public async Task UnicodePayloadUsesUtf8ByteLengthAndLeavesNextFrameIntact()
    {
        var first = new JObject { ["jsonrpc"] = "2.0", ["method"] = "test", ["params"] = "шейдер 🟩" };
        var second = new JObject { ["id"] = 2, ["result"] = true };
        using var wire = new MemoryStream();
        await LspFraming.WriteAsync(wire, first, default);
        await LspFraming.WriteAsync(wire, second, default);
        wire.Position = 0;
        Assert.True(JToken.DeepEquals(first, await LspFraming.ReadAsync(wire, default)));
        Assert.True(JToken.DeepEquals(second, await LspFraming.ReadAsync(wire, default)));
        Assert.Null(await LspFraming.ReadAsync(wire, default));
    }

    [Fact]
    public async Task ReadsFragmentedFramesAndOptionalContentType()
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length: 8\r\n\r\n{\"id\":1}"));
        Assert.Equal(1, (int)(await LspFraming.ReadAsync(stream, default))!["id"]!);
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 33554433\r\n\r\n")]
    [InlineData("Content-Length: one\r\n\r\n")]
    [InlineData("Content-Type: utf8\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    public async Task RejectsMalformedOrUnboundedLengths(string frame)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        await Assert.ThrowsAsync<InvalidDataException>(() => LspFraming.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("Content-Length: 8\r\n\r\n{\"id\"")]
    [InlineData("Content-Length: 8\r")]
    public async Task TruncatedFramesCannotBeMistakenForCleanShutdown(string frame)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LspFraming.ReadAsync(stream, default));
    }

    [Fact]
    public async Task HonorsCancellationBeforeReading()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LspFraming.ReadAsync(stream, cancelled.Token));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(2, count), cancellationToken);
    }
}
