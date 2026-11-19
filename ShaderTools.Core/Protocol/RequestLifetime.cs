using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ShaderTools.Core.Protocol;

/// <summary>Bounds client requests independently of compiler progress. All times are monotonic.</summary>
public sealed class RequestLifetime
{
    private readonly object gate = new object();
    private readonly Dictionary<string, PendingRequest> pending = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRequest> wireRequests = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
    private readonly Func<long> now;
    private readonly RequestLimits limits;
    private long? recoveryDeadline;
    private long nextWireId;
    private bool closed;

    public RequestLifetime(RequestLimits? limits = null, Func<long>? milliseconds = null)
    {
        this.limits = limits ?? new RequestLimits();
        this.limits.Validate();
        now = milliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
    }

    public bool TryBegin(JObject message, out JObject? error, bool remote = true)
    {
        JToken id = message["id"]!;
        string method = (string)message["method"]!;
        lock (gate)
        {
            string key = Key(id);
            if (closed) { error = Error(id, -32603, "Shader session is closed."); return false; }
            // A duplicate live ID violates JSON-RPC. Do not admit a second operation with it.
            if (pending.ContainsKey(key))
            {
                error = null;
                return false;
            }
            if (pending.Count >= limits.MaximumPending)
            {
                error = Error(id, -32001, "Shader request queue is full. Retry after analysis completes.");
                return false;
            }
            long started = now();
            PendingRequest request = new PendingRequest(id.DeepClone(), new JValue("shader-tools/" + (++nextWireId)), method, started, started + limits.ForMethod(method), remote);
            pending.Add(key, request);
            if (remote)
            {
                wireRequests.Add(Key(request.WireId), request);
            }

            error = null;
            return true;
        }
    }

    public PendingRequest? Find(JToken id) { lock (gate) { _=pending.TryGetValue(Key(id), out PendingRequest request); return request; } }

    public PendingRequest? CompleteWire(JToken wireId)
    {
        lock (gate)
        {
            // Even an acknowledgement of a request already cancelled proves the worker is responsive.
            recoveryDeadline = null;
            return wireRequests.TryGetValue(Key(wireId), out PendingRequest request) ? Remove(request.Id) : null;
        }
    }

    public PendingRequest? CompleteLocal(PendingRequest expected)
    {
        lock (gate)
        {
            return pending.TryGetValue(Key(expected.Id), out PendingRequest request) && ReferenceEquals(request, expected) ? Remove(expected.Id) : null;
        }
    }

    public PendingRequest? Cancel(JToken id)
    {
        lock (gate)
        {
            PendingRequest? request = Remove(id);
            request?.CancelWork();
            if (request != null && request.Remote && !recoveryDeadline.HasValue)
            {
                recoveryDeadline = now() + limits.CancelRecoveryMilliseconds;
            }

            return request;
        }
    }

    public IReadOnlyList<PendingRequest> Expire()
    {
        lock (gate)
        {
            long current = now();
            PendingRequest[] expired = pending.Values.Where(p => p.Deadline <= current).ToArray();
            foreach (PendingRequest? request in expired) { _=Remove(request.Id); request.CancelWork(); }
            if (expired.Any(request => request.Remote))
            {
                long deadline = current + limits.TimeoutRecoveryMilliseconds;
                recoveryDeadline = recoveryDeadline.HasValue ? Math.Min(recoveryDeadline.Value, deadline) : deadline;
            }
            return expired;
        }
    }

    public bool RecoveryExpired { get { lock (gate) { return recoveryDeadline.HasValue && now() >= recoveryDeadline.Value; } } }
    public long Elapsed(PendingRequest request)
    {
        return Math.Max(0, now() - request.Started);
    }

    public IReadOnlyList<PendingRequest> Drain()
    {
        lock (gate)
        {
            PendingRequest[] requests = pending.Values.ToArray();
            pending.Clear(); wireRequests.Clear(); recoveryDeadline = null; closed = true;
            foreach (PendingRequest? request in requests)
            {
                request.CancelWork();
            }

            return requests;
        }
    }
    private PendingRequest? Remove(JToken id)
    {
        string key = Key(id);
        if (!pending.TryGetValue(key, out PendingRequest request))
        {
            return null;
        }

        _=pending.Remove(key);
        _=wireRequests.Remove(Key(request.WireId));
        return request;
    }
    private static string Key(JToken id)
    {
        return id.ToString(Formatting.None);
    }

    public static JObject Error(JToken id, int code, string message)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
    }
}

public sealed class PendingRequest
{
    private readonly CancellationTokenSource workCancellation = new CancellationTokenSource();
    internal PendingRequest(JToken id, JToken wireId, string method, long started, long deadline, bool remote)
    { Id = id; WireId = wireId; Method = method; Started = started; Deadline = deadline; Remote = remote; Cancellation = workCancellation.Token; }
    public CancellationToken Cancellation { get; }
    public long SnapshotGeneration { get; internal set; }
    internal JObject? EditorRequest { get; set; }
    internal void CancelWork() { workCancellation.Cancel(); }
    public JToken WireId { get; }
    public bool Remote { get; }
    public JToken Id { get; }
    public string Method { get; }
    public long Started { get; }
    public long Deadline { get; }
}

public sealed class RequestLimits
{
    public int MaximumPending { get; set; } = 128;
    public int InteractiveMilliseconds { get; set; } = 8000;
    public int AnalysisMilliseconds { get; set; } = 15000;
    public int InitializeMilliseconds { get; set; } = 90000;
    public int TimeoutRecoveryMilliseconds { get; set; } = 5000;
    public int CancelRecoveryMilliseconds { get; set; } = 30000;
    public int PollMilliseconds { get; set; } = 100;
    public int WriteMilliseconds { get; set; } = 15000;
    internal void Validate()
    {
        if (MaximumPending <= 0 || InteractiveMilliseconds <= 0 || AnalysisMilliseconds <= 0 || InitializeMilliseconds <= 0 || TimeoutRecoveryMilliseconds <= 0 || CancelRecoveryMilliseconds <= 0 || PollMilliseconds <= 0 || WriteMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestLimits), "Request limits must be positive.");
        }
    }
    internal int ForMethod(string method)
    {
        return method switch
        {
            "initialize" => InitializeMilliseconds,
            "textDocument/diagnostic" or "workspace/symbol" or "textDocument/documentSymbol" or "textDocument/formatting" or "textDocument/rangeFormatting" or "shaderTools/preprocessor" => AnalysisMilliseconds,
            "shutdown" => Math.Min(3000, InteractiveMilliseconds),
            _ => InteractiveMilliseconds
        };
    }
}
