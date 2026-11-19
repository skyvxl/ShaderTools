using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Core.Protocol
{
    /// <summary>Forwards standard LSP unchanged, except for the explicit VS adaptations.</summary>
    public sealed class LspRelay
    {
        private readonly ProtocolAdapter adapter;
        private readonly ClientFileUris fileUris = new ClientFileUris();
        private readonly PositionEncodingConverter positions;
        private readonly SemaphoreSlim serverWriter = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim clientWriter = new SemaphoreSlim(1, 1);
        private readonly RequestLifetime requests;
        private readonly RequestLimits limits;
        private readonly LocalShaderFeatures localFeatures;
        private readonly SemaphoreSlim localWorkers = new SemaphoreSlim(2, 2);
        private readonly Action<string> log;
        private readonly TaskCompletionSource<bool> transportFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RecoveryRequired { get; private set; }
        private volatile bool clientExit;

        public LspRelay(ProtocolAdapter adapter, Action<string>? log = null, RequestLimits? limits = null, PositionEncodingConverter? positions = null)
        {
            this.adapter = adapter;
            this.positions = positions ?? new PositionEncodingConverter();
            this.log = log ?? (_ => { });
            this.limits = limits ?? new RequestLimits();
            requests = new RequestLifetime(this.limits);
            localFeatures = new LocalShaderFeatures(this.positions, adapter);
        }

        // The caller owns the streams and closes them when this task returns. This also unblocks
        // ReadAsync on .NET Framework's console streams, which do not support mid-read cancellation.
        public Task RunAsync(Stream clientInput, Stream clientOutput, Stream serverInput, Stream serverOutput, CancellationToken token)
        {
            return RunAsync(clientInput, clientOutput, new StreamLspTransport(serverInput, serverOutput), token);
        }

        public async Task RunAsync(Stream clientInput, Stream clientOutput, ILspTransport serverInput, CancellationToken token)
        {
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task fromClient = PumpClientAsync(clientInput, serverInput, clientOutput, cancellation.Token);
            Task fromServer = PumpServerAsync(serverInput, clientOutput, cancellation.Token);
            Task watchdog = WatchRequestsAsync(serverInput, clientOutput, cancellation.Token);
            Task cancelled = Task.Delay(Timeout.Infinite, cancellation.Token);
            Task finished = await Task.WhenAny(fromClient, fromServer, watchdog, cancelled, transportFailure.Task).ConfigureAwait(false);
            try
            {
                IReadOnlyList<PendingRequest> pending = requests.Drain();
                if (finished == fromServer || finished == watchdog || transportFailure.Task.IsCompleted)
                {
                    RecoveryRequired = !token.IsCancellationRequested && !clientExit;
                    foreach (PendingRequest request in pending)
                    {
                        await SendClientAsync(clientOutput, RequestLifetime.Error(request.Id, -32603, "Shader analysis stopped. Reopen the shader or restart the language server."), cancellation.Token).ConfigureAwait(false);
                    }
                }
                await finished.ConfigureAwait(false);
            }
            catch (IOException) when (RecoveryRequired) { }
            finally
            {
                _=requests.Drain();
                cancellation.Cancel();
                Observe(fromClient);
                Observe(fromServer);
                Observe(watchdog);
            }
        }

        private async Task PumpClientAsync(Stream source, ILspTransport serverInput, Stream clientOutput, CancellationToken token)
        {
            bool firstFrame = true;
            while (!token.IsCancellationRequested)
            {
                JObject? message = await LspFraming.ReadAsync(source, token, allowUtf8Preamble: firstFrame).ConfigureAwait(false);
                firstFrame = false;
                if (message == null)
                {
                    return;
                }

                fileUris.NormalizeIncoming(message);
                if (message["id"] != null && message["method"] != null && !requests.TryBegin(message, out JObject? rejected, !localFeatures.Handles(message)))
                {
                    if (rejected != null)
                    {
                        await SendClientAsync(clientOutput, rejected, token).ConfigureAwait(false);
                    }

                    continue;
                }

                if ((string?)message["method"] == "exit")
                {
                    clientExit = true;
                }

                if (message["id"] != null && localFeatures.Handles(message))
                {
                    PendingRequest? localRequest = requests.Find(message["id"]!);
                    if (localRequest != null)
                    {
                        Observe(HandleLocalAsync(message, localRequest, clientOutput, token));
                    }

                    continue;
                }
                if (message["id"] != null && message["method"] != null)
                {
                    PendingRequest? request = requests.Find(message["id"]!);
                    if (request == null)
                    {
                        continue;
                    }

                    request.SnapshotGeneration = positions.DocumentGeneration;
                    if (request.Method is "shaderTools/compilationContext" or "textDocument/codeAction")
                    {
                        request.EditorRequest = (JObject)message.DeepClone();
                        JToken? document = message.SelectToken("params.textDocument");
                        if (request.Method == "shaderTools/compilationContext")
                        {
                            message["method"] = "textDocument/dependencyTree";
                            message["params"] = document?.DeepClone();
                        }
                        else
                        {
                            // Upstream push diagnostics may be unversioned. Pull the current
                            // diagnostics before offering an action for this editor snapshot.
                            message["method"] = "textDocument/diagnostic";
                            message["params"] = new JObject { ["textDocument"] = document?.DeepClone() };
                        }
                    }
                    message["id"] = request.WireId.DeepClone();
                }
                if ((string?)message["method"] == "$/cancelRequest" && message.SelectToken("params.id") is JToken cancelledId)
                {
                    PendingRequest? request = requests.Cancel(cancelledId);
                    if (request == null)
                    {
                        continue;
                    }

                    await SendClientAsync(clientOutput, RequestLifetime.Error(cancelledId, -32800, "Shader request cancelled."), token).ConfigureAwait(false);
                    LogRequest(request, "cancelled");
                    if (!request.Remote)
                    {
                        continue;
                    }

                    message["params"]!["id"] = request.WireId.DeepClone();
                }
                positions.ProcessClientMessage(message);
                JObject adapted = adapter.AdaptClientMessage(message);
                await SendServerAsync(serverInput, adapted, token).ConfigureAwait(false);
                if ((string?)message["method"] == "initialized")
                {
                    await NotifyConfigurationAsync(serverInput, token).ConfigureAwait(false);
                }

                if ((string?)message["method"] == "exit")
                {
                    return;
                }
            }
        }

        private async Task PumpServerAsync(ILspTransport serverInput, Stream clientOutput, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                JObject? message = await serverInput.ReadAsync(token).ConfigureAwait(false);
                if (message == null)
                {
                    return;
                }

                PendingRequest? completed = null;
                if (message["id"] is JToken id && message["method"] == null)
                {
                    PendingRequest? request = requests.CompleteWire(id);
                    if (request == null)
                    {
                        continue;
                    }

                    completed = request;
                    LogRequest(request, message["error"] == null ? "completed" : "failed");
                    if (request.Method == "initialize" && message["result"] is JObject initializeResult)
                    {
                        LocalShaderFeatures.Advertise(initializeResult);
                    }
                }
                positions.ProcessServerMessage(message);
                if (completed != null)
                {
                    message["id"] = completed.Id.DeepClone();
                }

                if (completed?.EditorRequest is JObject editorRequest && message["result"] is JObject result)
                {
                    try
                    {
                        string uri = (string?)editorRequest.SelectToken("params.textDocument.uri")
                            ?? throw new ArgumentException("A shader document URI is required.");
                        if (completed.Method == "shaderTools/compilationContext")
                        {
                            JObject context = adapter.GetInspectionContext(uri);
                            context["dependencyTree"] = result;
                            context["documentVersion"] = positions.GetDocumentVersion(uri);
                            context["snapshotGeneration"] = completed.SnapshotGeneration;
                            message["result"] = context;
                        }
                        else
                        {
                            message["result"] = localFeatures.Execute(editorRequest, token, result["items"] as JArray ?? new JArray()).Value;
                        }
                        if (completed.SnapshotGeneration != positions.DocumentGeneration)
                        {
                            message = RequestLifetime.Error(completed.Id, -32801, "Shader documents or settings changed. Refresh the view.");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
                    {
                        message = RequestLifetime.Error(completed.Id, -32603, ex.Message);
                    }
                }
                JObject? response;
                try { response = adapter.GetServerRequestResponse(message); }
                catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or ArgumentException)
                {
                    log(ex.Message);
                    response = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = message["id"]?.DeepClone(),
                        ["error"] = new JObject { ["code"] = -32603, ["message"] = ex.Message }
                    };
                }
                if (response != null)
                {
                    await SendServerAsync(serverInput, response, token).ConfigureAwait(false);
                }
                else
                {
                    await SendClientAsync(clientOutput, message, token).ConfigureAwait(false);
                    // VS middleware handles outbound traffic. A custom inbound notification feeds
                    // editor adornments while the original diagnostics still reach native Error List.
                    if ((string?)message["method"] == "textDocument/publishDiagnostics")
                    {
                        await SendClientAsync(clientOutput, new JObject { ["jsonrpc"] = "2.0", ["method"] = "shaderTools/diagnostics", ["params"] = message["params"]!.DeepClone() }, token).ConfigureAwait(false);
                    }
                }
            }
        }

        public Task NotifyConfigurationAsync(Stream serverInput, CancellationToken token)
        {
            return NotifyConfigurationAsync(new StreamLspTransport(serverInput, Stream.Null), token);
        }

        private Task NotifyConfigurationAsync(ILspTransport serverInput, CancellationToken token)
        {
            return SendServerAsync(serverInput,
            new JObject { ["jsonrpc"] = "2.0", ["method"] = "workspace/didChangeConfiguration", ["params"] = new JObject { ["settings"] = new JObject() } }, token);
        }

        private async Task WatchRequestsAsync(ILspTransport serverInput, Stream clientOutput, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(limits.PollMilliseconds, token).ConfigureAwait(false);
                foreach (PendingRequest request in requests.Expire())
                {
                    await SendClientAsync(clientOutput, RequestLifetime.Error(request.Id, -32800, "Shader request timed out. Analysis may still be running."), token).ConfigureAwait(false);
                    LogRequest(request, "timed out");
                    if (!request.Remote)
                    {
                        continue;
                    }

                    JObject cancel = new JObject { ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest", ["params"] = new JObject { ["id"] = request.WireId.DeepClone() } };
                    positions.ProcessClientMessage(cancel);
                    // Do not let a full compiler input pipe stop subsequent deadlines.
                    Observe(SendServerAsync(serverInput, cancel, token));
                }
                if (requests.RecoveryExpired)
                {
                    RecoveryRequired = true;
                    log("Shader analysis is unresponsive; closing the session for recovery.");
                    return;
                }
            }
        }

        private async Task HandleLocalAsync(JObject message, PendingRequest request, Stream clientOutput, CancellationToken token)
        {
            JToken id = message["id"]!;
            if (!localWorkers.Wait(0))
            {
                if (requests.CompleteLocal(request) != null)
                {
                    await SendClientAsync(clientOutput, RequestLifetime.Error(id, -32001, "Shader inspection is busy. Retry shortly."), token).ConfigureAwait(false);
                }

                return;
            }
            try
            {
                using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(token, request.Cancellation);
                LocalShaderResult result = await Task.Run(() => localFeatures.Execute(message, work.Token), work.Token).ConfigureAwait(false);
                if (requests.CompleteLocal(request) == null)
                {
                    return;
                }

                await SendClientAsync(clientOutput, new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result.Value }, token).ConfigureAwait(false);
                if (result.Notification != null)
                {
                    await SendClientAsync(clientOutput, result.Notification, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                if (requests.CompleteLocal(request) != null)
                {
                    await SendClientAsync(clientOutput, RequestLifetime.Error(id, -32603, ex.Message), token).ConfigureAwait(false);
                }
            }
            finally { _=localWorkers.Release(); }
        }

        private void LogRequest(PendingRequest request, string outcome)
        {
            long elapsed = requests.Elapsed(request);
            if (outcome != "completed" || elapsed >= 1000)
            {
                log(request.Method + " " + outcome + " after " + elapsed + " ms.");
            }
        }

        private Task SendClientAsync(Stream stream, JObject message, CancellationToken token)
        {
            fileUris.RestoreOutgoing(message);
            return WriteBoundedAsync(clientWriter, ct => LspFraming.WriteAsync(stream, message, ct), limits.WriteMilliseconds, token);
        }

        private Task SendServerAsync(ILspTransport transport, JObject message, CancellationToken token)
        {
            // A routed initialize also starts a process and negotiates its capabilities.
            int timeout = (string?)message["method"] == "initialize" && transport is not StreamLspTransport
                ? limits.InitializeMilliseconds : limits.WriteMilliseconds;
            return WriteBoundedAsync(serverWriter, ct => transport.WriteAsync(message, ct), timeout, token);
        }

        private async Task WriteBoundedAsync(SemaphoreSlim writer, Func<CancellationToken, Task> send, int timeout, CancellationToken token)
        {
            using CancellationTokenSource writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken writeToken = writeCancellation.Token;
            // Some framework pipe streams implement WriteAsync with a blocking native write.
            // Isolate it, including the serialization lock, so a full pipe cannot stop the reader/watchdog.
            Task write = Task.Run(async () =>
            {
                await writer.WaitAsync(writeToken).ConfigureAwait(false);
                try { await send(writeToken).ConfigureAwait(false); }
                finally { _=writer.Release(); }
            }, token);
            Task deadline = Task.Delay(timeout, token);
            if (await Task.WhenAny(write, deadline).ConfigureAwait(false) != write)
            {
                writeCancellation.Cancel();
                Observe(write);
                token.ThrowIfCancellationRequested();
                RecoveryRequired = true;
                _=transportFailure.TrySetResult(true);
                throw new IOException("Shader transport write timed out.");
            }
            try { await write.ConfigureAwait(false); }
            catch (IOException)
            {
                RecoveryRequired = true;
                _=transportFailure.TrySetResult(true);
                throw;
            }
        }

        private void Observe(Task task)
        {
            _ = task.ContinueWith(t => log("LSP connection ended: " + t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
