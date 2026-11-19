using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderTools.Core;
using ShaderTools.Core.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Bridge;

/// <summary>A single official compiler process. Request IDs and configuration belong to this connection.</summary>
internal sealed class ProjectServer : IDisposable
{
    private readonly Process process;
    private readonly ProtocolAdapter adapter;
    private readonly Func<ProjectServer, JObject, Task> publish;
    private readonly Action<ProjectServer, Exception> failed;
    private readonly SemaphoreSlim writer = new SemaphoreSlim(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
    private readonly CancellationTokenSource stopped = new CancellationTokenSource();
    private readonly Stream input;
    private Task latestRequest = Task.CompletedTask;
    private int sequence;
    private int disposed;

    public ProjectServer(string root, JObject settings, string server, string? formatDirectory, ProcessJob job,
        Func<ProjectServer, JObject, Task> publish, Action<ProjectServer, Exception> failed, Action<string> log)
    {
        Root = root;
        Settings = settings;
        this.publish = publish;
        this.failed = failed;
        JObject wireSettings = (JObject)settings.DeepClone();
        // Stock interprets an empty path as its working directory.
        if (wireSettings["glsl"] is JObject glsl && (string?)glsl["preamble"] == "") { glsl.Remove("preamble"); }
        adapter = new ProtocolAdapter(root, _ => wireSettings);
        ProcessStartInfo start = new ProcessStartInfo(server)
        {
            Arguments = "--stdio --glsl --hlsl --wgsl --config " + WindowsCommandLine.Quote(wireSettings.ToString(Formatting.None)) +
                " --cwd " + WindowsCommandLine.Quote(root),
            WorkingDirectory = Path.GetDirectoryName(server)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (formatDirectory != null && Directory.Exists(formatDirectory))
        {
            start.EnvironmentVariables["PATH"] = formatDirectory + Path.PathSeparator + start.EnvironmentVariables["PATH"];
        }

        process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { log(e.Data); } };
        process.Start();
        try
        {
            job.Add(process);
            process.BeginErrorReadLine();
            input = process.StandardInput.BaseStream;
            Stream output = new BufferedStream(process.StandardOutput.BaseStream);
            // Framework pipe reads can block before returning their first Task.
            _ = Task.Run(() => ReadAsync(output));
            log("Started shader-language-server 1.6.2 (PID " + process.Id + "). Workspace: " + root);
        }
        catch
        {
            try { if (!process.HasExited) { process.Kill(); } }
            finally { process.Dispose(); }
            throw;
        }
    }

    // Managed by the router's serialized client pump.
    public HashSet<string> OpenDocuments { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> Dependencies { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    public string Root { get; }
    public JObject Settings { get; }

    public async Task<JObject> RequestAsync(JObject message, CancellationToken token)
    {
        if (pending.Count >= 256) { throw new IOException("Too many outstanding compiler requests for " + Root); }
        int id = Interlocked.Increment(ref sequence);
        TaskCompletionSource<JObject> reply = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, reply)) { throw new InvalidOperationException("Duplicate compiler request ID."); }
        Volatile.Write(ref latestRequest, reply.Task);
        try
        {
            JObject request = (JObject)message.DeepClone();
            request["id"] = id;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token);
            // Also bound abandoned upstream requests while other project workers still respond.
            linked.CancelAfter(TimeSpan.FromSeconds(90));
            using var registration = linked.Token.Register(() => reply.TrySetCanceled());
            await WriteAsync(request, linked.Token).ConfigureAwait(false);
            return await reply.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task WriteAsync(JObject message, CancellationToken token)
    {
        if ((string?)message["method"] == "textDocument/didChange")
        {
            // The native scheduler drains incoming edits before interactive queries. Give an
            // already-submitted query a bounded quiet interval, including during continuous typing.
            Task preceding = Volatile.Read(ref latestRequest);
            if (!preceding.IsCompleted) { await Task.WhenAny(preceding, Task.Delay(250, token)).ConfigureAwait(false); }
        }
        await writer.WaitAsync(token).ConfigureAwait(false);
        try { await LspFraming.WriteAsync(input, message, token).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    public Task SetVariantAsync(JObject message, CancellationToken token)
    {
        return WriteAsync(adapter.AdaptClientMessage(message), token);
    }

    public Task<JObject> InitializeAsync(JObject message, CancellationToken token)
    {
        return RequestAsync(adapter.AdaptClientMessage(message), token);
    }

    private async Task ReadAsync(Stream output)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                JObject? message = await LspFraming.ReadAsync(output, stopped.Token).ConfigureAwait(false);
                if (message == null) { throw new EndOfStreamException("Shader compiler exited for " + Root); }
                if (message["method"] == null)
                {
                    if (message["id"]?.Type == JTokenType.Integer &&
                        pending.TryGetValue((int)message["id"]!, out var result))
                    {
                        result.TrySetResult(message);
                    }
                }
                else if (adapter.GetServerRequestResponse(message) is JObject response)
                {
                    await WriteAsync(response, stopped.Token).ConfigureAwait(false);
                }
                else
                {
                    await publish(this, message).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            if (!stopped.IsCancellationRequested)
            {
                failed(this, ex);
                stopped.Cancel();
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken token)
    {
        try
        {
            await RequestAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "shutdown" }, token).ConfigureAwait(false);
            await WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "exit" }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        stopped.Cancel();
        try { if (!process.HasExited) { process.Kill(); } }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
