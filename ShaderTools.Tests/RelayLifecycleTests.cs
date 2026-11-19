using System.Threading.Channels;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Protocol;
using Xunit;

namespace ShaderTools.Tests;

public sealed class RelayLifecycleTests
{
    [Fact]
    public async Task InspectionUsesOfficialDependencyTreeAndBridgeSnapshot()
    {
        string root = Path.GetTempPath();
        string uri = new Uri(Path.Combine(root, "shader.frag")).AbsoluteUri;
        var store = new ShaderTools.Core.Configuration.ShaderConfigurationStore();
        var adapter = new ProtocolAdapter(root, r => store.LoadWorkspace(r).Settings, documentConfiguration: store.LoadDocument);
        await using var session = new Session(adapter: adapter);
        var document = new JObject { ["uri"] = uri };
        await session.SendClient(new JObject { ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["text"] = "void main() {}", ["version"] = 3, ["languageId"] = "glsl" } } });
        Assert.Equal("textDocument/didOpen", (string?)(await session.ReadServer())["method"]);
        await session.SendClient(new JObject { ["id"] = 1, ["method"] = "shaderTools/compilationContext", ["params"] = new JObject { ["textDocument"] = document } });
        var treeRequest = await session.ReadServer();
        Assert.Equal("textDocument/dependencyTree", (string?)treeRequest["method"]);
        Assert.Equal(uri, (string?)treeRequest.SelectToken("params.uri"));
        await session.SendServer(new JObject { ["id"] = treeRequest["id"], ["result"] = new JObject { ["url"] = uri, ["includes"] = new JArray() } });
        var context = (await session.ReadClient())["result"]!;
        Assert.Equal(uri, (string?)context.SelectToken("dependencyTree.url"));
        Assert.Equal(3, (int)context["documentVersion"]!);
        Assert.Equal(1, (long)context["snapshotGeneration"]!);
    }

    [Fact]
    public async Task ServerEofDuringExitWriteDoesNotRequestRecovery()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var client = new AsyncByteStream();
        var server = new AsyncByteStream();
        var input = new ExitWriteStream(server);
        var relay = new LspRelay(new ProtocolAdapter("", _ => new JObject()));
        Task running = relay.RunAsync(client, new AsyncByteStream(), input, server, lifetime.Token);
        await LspFraming.WriteAsync(client, new JObject { ["method"] = "exit" }, lifetime.Token);
        try { await running.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(relay.RecoveryRequired); }
        finally { input.Release(); client.Complete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedTransportEndsEvenWithoutAnOutstandingRequest(bool blockClient)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var clientInput = new AsyncByteStream();
        var serverOutput = new AsyncByteStream();
        var blocked = new BlockedWriteStream();
        var relay = new LspRelay(new ProtocolAdapter("", _ => new JObject()), limits: new RequestLimits { WriteMilliseconds = 40 });
        Task running = relay.RunAsync(clientInput, blockClient ? blocked : new AsyncByteStream(), blockClient ? new AsyncByteStream() : blocked, serverOutput, lifetime.Token);
        var notification = new JObject { ["method"] = blockClient ? "window/logMessage" : "textDocument/didOpen", ["params"] = new JObject() };
        await LspFraming.WriteAsync(blockClient ? serverOutput : clientInput, notification, lifetime.Token);
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(relay.RecoveryRequired);
        }
        finally { blocked.Release(); clientInput.Complete(); serverOutput.Complete(); }
    }

    [Fact]
    public void LocalCancellationStopsWorkAndCannotCompleteAReplacementWithTheSameId()
    {
        long now = 0;
        var requests = new RequestLifetime(new RequestLimits { InteractiveMilliseconds = 50 }, () => now);
        requests.TryBegin(Request(1, "textDocument/codeAction"), out _, remote: false);
        var old = requests.Find(new JValue(1))!;
        requests.Cancel(old.Id);
        Assert.True(old.Cancellation.IsCancellationRequested);
        requests.TryBegin(Request(1, "textDocument/codeAction"), out _, remote: false);
        var current = requests.Find(new JValue(1))!;
        Assert.Null(requests.CompleteLocal(old));
        Assert.False(current.Cancellation.IsCancellationRequested);
        now = 50;
        Assert.Single(requests.Expire());
        Assert.True(current.Cancellation.IsCancellationRequested);
        now = 100000;
        Assert.False(requests.RecoveryExpired);
        requests.Drain();
        Assert.False(requests.TryBegin(Request(2, "textDocument/hover"), out _));
    }

    [Fact]
    public async Task HungCompilerGetsTerminalErrorThenSessionEndsWithinRecoveryBudget()
    {
        await using var session = new Session(new RequestLimits { InteractiveMilliseconds = 40, TimeoutRecoveryMilliseconds = 60, PollMilliseconds = 10 });
        await session.SendClient(Request(9, "textDocument/signatureHelp"));
        await session.ReadServer();
        var reply = await session.ReadClient();
        Assert.Equal(-32800, (int)reply.SelectToken("error.code")!);
        Assert.Contains("timed out", (string)reply.SelectToken("error.message")!);
        Assert.Equal("$/cancelRequest", (string?)(await session.ReadServer())["method"]);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DeadlinesUseMonotonicTimeAndInitializationHasSeparateBudget()
    {
        long now = 0;
        var requests = new RequestLifetime(new RequestLimits { InteractiveMilliseconds = 50, InitializeMilliseconds = 500, MaximumPending = 2 }, () => now);
        Assert.True(requests.TryBegin(Request(1, "initialize"), out _));
        Assert.True(requests.TryBegin(Request("1", "textDocument/hover"), out _));
        Assert.False(requests.TryBegin(Request(2, "textDocument/hover"), out var full));
        Assert.Equal(-32001, (int)full!.SelectToken("error.code")!);
        Assert.Null(requests.Cancel(new JValue("unknown")));
        now = 50;
        var expired = Assert.Single(requests.Expire());
        Assert.Equal(JTokenType.String, expired.Id.Type);
        Assert.Null(requests.CompleteWire(expired.WireId));
        Assert.Equal("initialize", requests.CompleteWire(requests.Find(new JValue(1))!.WireId)!.Method);
        Assert.Empty(requests.Drain());
    }

    [Fact]
    public async Task ReusingAnIdAfterCancellationCannotAcceptThePreviousResult()
    {
        await using var session = new Session();
        await session.SendClient(Request(1, "textDocument/hover"));
        var oldWire = (await session.ReadServer())["id"]!.DeepClone();
        await session.SendClient(new JObject { ["method"] = "$/cancelRequest", ["params"] = new JObject { ["id"] = 1 } });
        await session.ReadClient(); await session.ReadServer();
        await session.SendClient(Request(1, "textDocument/hover"));
        var newWire = (await session.ReadServer())["id"]!.DeepClone();
        await session.SendServer(new JObject { ["id"] = oldWire, ["result"] = "obsolete" });
        await session.SendServer(new JObject { ["id"] = newWire, ["result"] = "replacement" });
        Assert.Equal("replacement", (string?)(await session.ReadClient())["result"]);
    }

    [Fact]
    public async Task CancellationCompletesOnlyItsRequestAndSuppressesLateResponse()
    {
        await using var session = new Session();
        await session.SendClient(Request(1, "textDocument/hover"));
        await session.SendClient(Request("1", "textDocument/hover"));
        var numericWire = (await session.ReadServer())["id"]!.DeepClone();
        var stringWire = (await session.ReadServer())["id"]!.DeepClone();
        Assert.NotEqual(numericWire.ToString(), stringWire.ToString());
        await session.SendClient(new JObject { ["method"] = "$/cancelRequest", ["params"] = new JObject { ["id"] = 1 } });
        Assert.Equal(numericWire.ToString(), (await session.ReadServer()).SelectToken("params.id")!.ToString());
        JObject cancelled = await session.ReadClient();
        Assert.Equal(-32800, (int)cancelled.SelectToken("error.code")!);
        Assert.Equal(JTokenType.Integer, cancelled["id"]!.Type);
        await session.SendServer(new JObject { ["id"] = numericWire, ["result"] = "late" });
        await session.SendServer(new JObject { ["id"] = stringWire, ["result"] = "current" });
        JObject result = await session.ReadClient();
        Assert.Equal("current", (string?)result["result"]);
        Assert.Equal(JTokenType.String, result["id"]!.Type);
    }

    [Fact]
    public async Task ServerExitCompletesPendingRequests()
    {
        await using var session = new Session();
        await session.SendClient(Request(7, "textDocument/definition"));
        await session.ReadServer();
        session.ServerOutput.Complete();
        JObject error = await session.ReadClient();
        Assert.Equal(7, (int)error["id"]!);
        Assert.NotNull(error["error"]);
    }

    internal static JObject Request(object id, string method) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = JToken.FromObject(id), ["method"] = method,
        ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = "file:///C:/test.frag" }, ["position"] = new JObject { ["line"] = 0, ["character"] = 0 } }
    };

    private sealed class Session : IAsyncDisposable
    {
        private readonly AsyncByteStream clientInput = new();
        private readonly AsyncByteStream clientOutput = new();
        private readonly AsyncByteStream serverInput = new();
        public AsyncByteStream ServerOutput { get; } = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task relay;

        public Task Completion => relay;
        public Session(RequestLimits? limits = null, ProtocolAdapter? adapter = null)
        {
            adapter ??= new ProtocolAdapter("", _ => new JObject());
            relay = new LspRelay(adapter, limits: limits).RunAsync(clientInput, clientOutput, serverInput, ServerOutput, lifetime.Token);
        }
        public Task SendClient(JObject message) => LspFraming.WriteAsync(clientInput, message, lifetime.Token);
        public Task SendServer(JObject message) => LspFraming.WriteAsync(ServerOutput, message, lifetime.Token);
        public async Task<JObject> ReadClient() => (await LspFraming.ReadAsync(clientOutput, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2)))!;
        public async Task<JObject> ReadServer() => (await LspFraming.ReadAsync(serverInput, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2)))!;
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            clientInput.Complete(); ServerOutput.Complete(); clientOutput.Complete(); serverInput.Complete();
            try { await relay.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            lifetime.Dispose();
        }
    }

    private sealed class ExitWriteStream(AsyncByteStream serverOutput) : BlockedWriteStream
    {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { serverOutput.Complete(); return base.WriteAsync(buffer, offset, count, token); }
    }

    private class BlockedWriteStream : AsyncByteStream
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => completion.TrySetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => completion.Task;
    }

    internal class AsyncByteStream : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private byte[] current = [];
        private int offset;
        public void Complete() => channel.Writer.TryComplete();
        public override async Task<int> ReadAsync(byte[] buffer, int destination, int count, CancellationToken token)
        {
            while (offset == current.Length)
            {
                if (!await channel.Reader.WaitToReadAsync(token)) return 0;
                if (!channel.Reader.TryRead(out var chunk)) continue;
                current = chunk; offset = 0;
            }
            int length = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, destination, length); offset += length;
            return length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => channel.Writer.WriteAsync(buffer.AsSpan(offset, count).ToArray(), token).AsTask();
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
