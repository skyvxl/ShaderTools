using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using ShaderTools.Core.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Bridge;

/// <summary>Multiplexes one editor connection over project-scoped shader-sense processes.</summary>
internal sealed class ProjectRouter : ILspTransport, IDisposable
{
    private readonly string executable;
    private readonly string? formatDirectory;
    private readonly ShaderConfigurationStore configurations;
    private readonly PositionEncodingConverter documents;
    private readonly ProcessJob job;
    private readonly Action<string> log;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly BlockingCollection<(ProjectServer? source, JObject message)> incoming = new BlockingCollection<(ProjectServer?, JObject)>(256);
    private readonly ConcurrentDictionary<string, ProjectServer> servers = new ConcurrentDictionary<string, ProjectServer>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectServer> owners = new ConcurrentDictionary<string, ProjectServer>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ProjectServer server, JToken id)> callbacks = new ConcurrentDictionary<string, (ProjectServer, JToken)>();
    private long callbackId;
    private string workspace;
    private JObject? initialization;
    private bool overlaysDirty;
    private volatile bool shuttingDown;
    private JObject? variant;
    private ProjectServer? variantServer;
    // Immutable after publication. Local inspection and the server reader run concurrently with routing.
    private volatile Dictionary<string, string> compilationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ProjectRouter(string server, string workspace, string? formatDirectory, ShaderConfigurationStore configurations,
        PositionEncodingConverter documents, ProcessJob job, Action<string> log)
    {
        executable = Path.GetFullPath(server);
        this.workspace = workspace;
        this.formatDirectory = formatDirectory;
        this.configurations = configurations;
        this.documents = documents;
        this.job = job;
        this.log = log;
    }

    public string? CompilationRoot(string uri) =>
        compilationRoots.TryGetValue(Key(uri), out string root) ? root : null;

    public async Task<JObject?> ReadAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            while (true)
            {
                var item = await Task.Run(() => incoming.Take(linked.Token), linked.Token).ConfigureAwait(false);
                if (item.source == null || AcceptNotification(item.source, item.message)) { return item.message; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
    }

    // LspRelay serializes writes. Ownership, replay, and initialization therefore complete before the
    // next editor message is dispatched, even when multiple compiler responses are outstanding.
    public async Task WriteAsync(JObject message, CancellationToken token)
    {
        string? method = (string?)message["method"];
        string? uri = (string?)message.SelectToken("params.textDocument.uri") ?? (string?)message.SelectToken("params.uri");
        if (initialization == null && method != "initialize")
        {
            if (message["id"] != null) { Publish(RequestLifetime.Error(message["id"]!, -32002, "Initialize the shader server first.")); }
            return;
        }
        if (overlaysDirty && message["id"] != null && method != null && method != "shutdown")
        {
            await SyncOverlaysAsync(token).ConfigureAwait(false);
        }
        switch (method)
        {
            case null:
                if (callbacks.TryRemove((string?)message["id"] ?? "", out var target))
                {
                    message["id"] = target.id;
                    if (IsCurrent(target.server)) { await target.server.WriteAsync(message, token).ConfigureAwait(false); }
                }
                return;
            case "initialize":
                if (initialization != null)
                {
                    Publish(RequestLifetime.Error(message["id"]!, -32600, "Shader server is already initialized."));
                    return;
                }
                initialization = (JObject)message.DeepClone();
                if (Uri.TryCreate((string?)message.SelectToken("params.rootUri"), UriKind.Absolute, out Uri root) && root.IsFile)
                {
                    workspace = root.LocalPath;
                }
                var initial = Start(workspace);
                JObject response = await initial.InitializeAsync(message, token).ConfigureAwait(false);
                response["id"] = message["id"]!.DeepClone();
                Publish(response);
                return;
            case "initialized":
                await servers[workspace].WriteAsync(message, token).ConfigureAwait(false);
                return;
            case "exit":
                return;
            case "shutdown":
                shuttingDown = true;
                using (var grace = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    grace.CancelAfter(TimeSpan.FromSeconds(1));
                    await Task.WhenAll(servers.Values.Select(server => server.ShutdownAsync(grace.Token))).ConfigureAwait(false);
                }
                Publish(Result(message, JValue.CreateNull()));
                return;
            case "$/cancelRequest":
                // The relay has already replied to VS. The stock server's cancel handler can remove
                // unrelated queued requests. Let the original result drain under its unique wire ID.
                return;
            case "workspace/didChangeConfiguration":
            case "workspace/didChangeWatchedFiles":
                await ReloadAsync(method == "workspace/didChangeWatchedFiles" ? message : null, token).ConfigureAwait(false);
                return;
            case "textDocument/didChangeShaderVariant":
                await SelectVariantAsync(message, token).ConfigureAwait(false);
                return;
            case "workspace/symbol":
                _ = ForwardAsync(message, WorkspaceSymbolsAsync(message));
                return;
            case "textDocument/didOpen" when uri != null:
                languages[Key(uri)] = (string?)message.SelectToken("params.textDocument.languageId") ?? "glsl";
                break;
        }

        if (uri == null)
        {
            if (message["id"] != null)
            {
                var fallback = await GetServerAsync(workspace, token).ConfigureAwait(false);
                _ = ForwardAsync(message, fallback.RequestAsync(message, lifetime.Token));
            }
            else
            {
                foreach (var server in servers.Values) { await server.WriteAsync(message, token).ConfigureAwait(false); }
            }
            return;
        }

        string key = Key(uri);
        if (message["id"] != null && !owners.ContainsKey(key) && CompilationRoot(uri) == null)
        {
            Publish(RequestLifetime.Error(message["id"]!, -32602, "Open this shader before requesting analysis."));
            return;
        }
        ProjectServer owner = owners.TryGetValue(key, out ProjectServer current) ? current :
            await GetServerAsync(ProjectRoot(CompilationRoot(uri) ?? uri), token).ConfigureAwait(false);
        if (method == "textDocument/didOpen")
        {
            owners[key] = owner;
            RetireUnused();
        }
        if (message["id"] != null)
        {
            _ = ForwardAsync(message, owner.RequestAsync(message, lifetime.Token), owner, key);
        }
        else
        {
            if (method == "textDocument/didOpen")
            {
                JObject item = (JObject)message.SelectToken("params.textDocument")!;
                var snapshot = new ShaderDocumentSnapshot(uri, (string?)item["text"] ?? "", (int?)item["version"] ?? 0);
                await OpenAsync(owner, snapshot, languages[key], token, owner.OpenDocuments.Count != 0 || owner == variantServer).ConfigureAwait(false);
            }
            else if (method is "textDocument/didChange" or "textDocument/didSave" or "textDocument/didClose")
            {
                foreach (var server in servers.Values.Where(server => server.OpenDocuments.Contains(key)))
                {
                    await server.WriteAsync(message, token).ConfigureAwait(false);
                    if (method == "textDocument/didClose") { server.OpenDocuments.Remove(key); }
                    foreach (var root in server.Dependencies.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || pair.Value.Contains(key)).Select(pair => pair.Key).ToArray())
                    {
                        server.Dependencies.Remove(root);
                    }
                }
            }
            else { await owner.WriteAsync(message, token).ConfigureAwait(false); }
            if (method == "textDocument/didClose")
            {
                owners.TryRemove(key, out _);
                languages.Remove(key);
                Publish(Notification("textDocument/publishDiagnostics", new JObject { ["uri"] = uri, ["diagnostics"] = new JArray() }));
                RetireUnused();
            }
            overlaysDirty = true;
        }
    }

    private static string Key(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out Uri file) && file.IsFile
        ? Path.GetFullPath(file.LocalPath) : uri;

    private string ProjectRoot(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri file) || !file.IsFile) { return workspace; }
        return Path.GetDirectoryName(ShaderConfiguration.FindConfigurationPath(workspace, file.LocalPath))!;
    }

    private JObject Settings(string root) =>
        configurations.LoadDocument(workspace, Path.Combine(root, ShaderConfiguration.FileName)).Settings;

    private ProjectServer Start(string root)
    {
        var server = new ProjectServer(root, Settings(root), executable, formatDirectory, job, FromServerAsync, ServerFailed, log);
        servers[root] = server;
        return server;
    }

    private async Task<ProjectServer> GetServerAsync(string root, CancellationToken token)
    {
        if (servers.TryGetValue(root, out var existing)) { return existing; }
        var server = Start(root);
        JObject reply = await server.InitializeAsync(initialization!, token).ConfigureAwait(false);
        if (reply["error"] != null) { throw new IOException("Project compiler initialization failed: " + reply["error"]); }
        await server.WriteAsync(Notification("initialized", new JObject()), token).ConfigureAwait(false);
        return server;
    }

    private async Task SelectVariantAsync(JObject message, CancellationToken token)
    {
        if (variantServer != null)
        {
            await variantServer.SetVariantAsync(Notification("textDocument/didChangeShaderVariant", new JObject { ["shaderVariant"] = null }), token).ConfigureAwait(false);
        }
        variant = message.SelectToken("params.shaderVariant") as JObject;
        variantServer = null;
        if ((string?)variant?["url"] is string root)
        {
            variantServer = await GetServerAsync(ProjectRoot(root), token).ConfigureAwait(false);
            await variantServer.SetVariantAsync(message, token).ConfigureAwait(false);
        }
        await RefreshVariantAsync(token).ConfigureAwait(false);
        await MoveDocumentsAsync(token).ConfigureAwait(false);
        overlaysDirty = true;
        RetireUnused();
    }

    private async Task RefreshVariantAsync(CancellationToken token)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (variantServer != null && (string?)variant?["url"] is string uri)
        {
            roots[Key(uri)] = uri;
            foreach (string key in await DependenciesAsync(variantServer, uri, token).ConfigureAwait(false)) { roots[key] = uri; }
        }
        compilationRoots = roots;
    }

    private async Task MoveDocumentsAsync(CancellationToken token)
    {
        foreach (var document in documents.CaptureOpenDocuments())
        {
            string key = Key(document.Uri);
            if (!languages.TryGetValue(key, out string language)) { continue; }
            string destination = ProjectRoot(CompilationRoot(document.Uri) ?? document.Uri);
            var server = await GetServerAsync(destination, token).ConfigureAwait(false);
            owners[key] = server;
            await OpenAsync(server, document, language, token, server.OpenDocuments.Count != 0 || server == variantServer).ConfigureAwait(false);
        }
    }

    private static async Task OpenAsync(ProjectServer server, ShaderDocumentSnapshot document, string language, CancellationToken token, bool invalidateIncluders)
    {
        if (!server.OpenDocuments.Add(Key(document.Uri))) { return; }
        int initialVersion = invalidateIncluders && document.Version > int.MinValue ? document.Version - 1 : document.Version;
        await server.WriteAsync(Notification("textDocument/didOpen", new JObject { ["textDocument"] = new JObject {
            ["uri"] = document.Uri, ["languageId"] = language, ["version"] = initialVersion, ["text"] = document.Text
        } }), token).ConfigureAwait(false);
        if (invalidateIncluders)
        {
            await server.WriteAsync(Notification("textDocument/didChange", new JObject {
                ["textDocument"] = new JObject { ["uri"] = document.Uri, ["version"] = document.Version },
                ["contentChanges"] = new JArray(new JObject { ["text"] = document.Text })
            }), token).ConfigureAwait(false);
        }
    }

    private async Task SyncOverlaysAsync(CancellationToken token)
    {
        if (variantServer != null)
        {
            await RefreshVariantAsync(token).ConfigureAwait(false);
            await MoveDocumentsAsync(token).ConfigureAwait(false);
        }
        var snapshots = documents.CaptureOpenDocuments().ToDictionary(d => Key(d.Uri), StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(servers.Values.Select(server => SyncServerOverlaysAsync(server, snapshots, token))).ConfigureAwait(false);
        if (variantServer != null)
        {
            // Promoting the unsaved dependencies can change the active variant's include tree.
            // Route the very first subsequent editor query using the converged tree.
            var previous = compilationRoots;
            await RefreshVariantAsync(token).ConfigureAwait(false);
            if (previous.Count != compilationRoots.Count || previous.Keys.Any(key => !compilationRoots.ContainsKey(key)))
            {
                await MoveDocumentsAsync(token).ConfigureAwait(false);
                await Task.WhenAll(servers.Values.Select(server => SyncServerOverlaysAsync(server, snapshots, token))).ConfigureAwait(false);
                RetireUnused();
            }
        }
        overlaysDirty = false;
    }

    private async Task SyncServerOverlaysAsync(ProjectServer server, IReadOnlyDictionary<string, ShaderDocumentSnapshot> snapshots, CancellationToken token)
    {
        var primary = new HashSet<string>(owners.Where(pair => pair.Value == server).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
        var roots = primary.Where(key => snapshots.TryGetValue(key, out var source) && source.Text.IndexOf("include", StringComparison.Ordinal) >= 0)
            .Select(key => snapshots[key].Uri).ToList();
        if (server == variantServer && (string?)variant?["url"] is string selected && !roots.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(selected);
        }
        HashSet<string> desired = primary;
        if (snapshots.Keys.Any(key => !primary.Contains(key)))
        {
            // An unsaved header can introduce another include which does not exist in its on-disk
            // text. Each iteration opens at least one previously unseen snapshot or finishes.
            while (true)
            {
                desired = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
                foreach (string uri in roots)
                {
                    string root = Key(uri);
                    if (!server.Dependencies.TryGetValue(root, out var tree))
                    {
                        tree = await DependenciesAsync(server, uri, token).ConfigureAwait(false);
                        server.Dependencies[root] = tree;
                    }
                    desired.UnionWith(tree.Where(snapshots.ContainsKey));
                }
                bool added = false;
                foreach (string key in desired.Where(key => !server.OpenDocuments.Contains(key)))
                {
                    if (!snapshots.TryGetValue(key, out var snapshot) || !languages.TryGetValue(key, out string language)) { continue; }
                    // Stock promotes its cached dependency to an open document on didOpen. A
                    // subsequent didChange invalidates the cached analysis of its includers.
                    await OpenAsync(server, snapshot, language, token, invalidateIncluders: true).ConfigureAwait(false);
                    added = true;
                }
                if (!added) { break; }
                server.Dependencies.Clear();
            }
        }
        foreach (string key in server.OpenDocuments.Where(key => !desired.Contains(key)).ToArray())
        {
            await server.WriteAsync(Notification("textDocument/didClose", new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(key).AbsoluteUri } }), token).ConfigureAwait(false);
            server.OpenDocuments.Remove(key);
            server.Dependencies.Remove(key);
        }
    }

    private static async Task<HashSet<string>> DependenciesAsync(ProjectServer server, string uri, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reply = await server.RequestAsync(Notification("textDocument/dependencyTree", new JObject { ["uri"] = uri }), token).ConfigureAwait(false);
        if (reply["result"] is JObject node)
        {
            foreach (var url in node.Descendants().OfType<JProperty>().Where(p => p.Name == "url" && p.Value.Type == JTokenType.String))
            {
                result.Add(Key((string)url.Value!));
            }
        }
        return result;
    }

    private async Task ReloadAsync(JObject? watchedFiles, CancellationToken token)
    {
        // shader-sense requests its config asynchronously. Restart only changed projects so the
        // following request always sees the replacement config (including deleted JSON layers).
        foreach (var server in servers.Values)
        {
            if (!JToken.DeepEquals(server.Settings, Settings(server.Root))) { Remove(server); }
            else if (watchedFiles != null)
            {
                server.Dependencies.Clear();
                await server.WriteAsync(watchedFiles, token).ConfigureAwait(false);
            }
        }
        if ((string?)variant?["url"] is string uri)
        {
            string root = ProjectRoot(uri);
            var destination = await GetServerAsync(root, token).ConfigureAwait(false);
            if (destination != variantServer)
            {
                if (variantServer != null && IsCurrent(variantServer))
                {
                    await variantServer.SetVariantAsync(Notification("textDocument/didChangeShaderVariant", new JObject { ["shaderVariant"] = null }), token).ConfigureAwait(false);
                }
                variantServer = destination;
                await variantServer.SetVariantAsync(Notification("textDocument/didChangeShaderVariant", new JObject { ["shaderVariant"] = variant.DeepClone() }), token).ConfigureAwait(false);
            }
            // Replay the root's current buffer before asking the compiler which headers are active.
            await MoveDocumentsAsync(token).ConfigureAwait(false);
            await RefreshVariantAsync(token).ConfigureAwait(false);
        }
        await MoveDocumentsAsync(token).ConfigureAwait(false);
        overlaysDirty = true;
        if (!owners.IsEmpty || variantServer != null) { RetireUnused(); }
    }

    private async Task<JObject> WorkspaceSymbolsAsync(JObject message)
    {
        string query = (string?)message.SelectToken("params.query") ?? "";
        var replies = await Task.WhenAll(servers.Values.Select(server => server.RequestAsync(message, lifetime.Token))).ConfigureAwait(false);
        var symbols = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            if (reply["error"] != null) { return reply; }
            foreach (var symbol in reply["result"] as JArray ?? new JArray())
            {
                if (((string?)symbol["name"] ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                string key = symbol["name"] + "\n" + symbol["kind"] + "\n" + symbol["location"]?.ToString(Formatting.None);
                symbols[key] = symbol;
            }
        }
        return Result(message, new JArray(symbols.Values));
    }

    private async Task ForwardAsync(JObject request, Task<JObject> operation, ProjectServer? owner = null, string? document = null)
    {
        try
        {
            JObject reply = await operation.ConfigureAwait(false);
            reply["id"] = request["id"]!.DeepClone();
            if (owner != null && (!IsCurrent(owner) || (document != null && owners.TryGetValue(document, out var current) && current != owner)))
            {
                reply = RequestLifetime.Error(request["id"]!, -32801, "Shader project changed during analysis.");
            }
            Publish(reply);
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Publish(RequestLifetime.Error(request["id"]!, -32801, "Project analysis changed: " + ex.Message));
            }
        }
    }

    private bool IsCurrent(ProjectServer server) => servers.TryGetValue(server.Root, out var current) && current == server;

    private Task FromServerAsync(ProjectServer server, JObject message)
    {
        if (!IsCurrent(server)) { return Task.CompletedTask; }
        if (message["id"] is JToken id)
        {
            if (callbacks.Count >= 128) { return server.WriteAsync(RequestLifetime.Error(id, -32001, "Too many compiler callbacks."), lifetime.Token); }
            string proxyId = "project-callback/" + Interlocked.Increment(ref callbackId);
            callbacks[proxyId] = (server, id.DeepClone());
            message["id"] = proxyId;
        }
        Publish(message, server);
        return Task.CompletedTask;
    }

    private bool AcceptNotification(ProjectServer server, JObject message)
    {
        if (!IsCurrent(server)) { return false; }
        if ((string?)message["method"] == "textDocument/publishDiagnostics" && (string?)message.SelectToken("params.uri") is string uri)
        {
            return !owners.TryGetValue(Key(uri), out var owner) || owner == server;
        }
        return true;
    }

    private void ServerFailed(ProjectServer server, Exception ex)
    {
        if (!shuttingDown && IsCurrent(server))
        {
            log("Project compiler stopped: " + ex.Message);
            lifetime.Cancel();
        }
    }

    private void Publish(JObject message, ProjectServer? source = null)
    {
        try { incoming.Add((source, message), lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void RetireUnused()
    {
        foreach (var server in servers.Values)
        {
            if (server != variantServer && !owners.Values.Contains(server)) { Remove(server); }
        }
    }

    private void Remove(ProjectServer server)
    {
        servers.TryRemove(server.Root, out _);
        foreach (var pair in callbacks.Where(pair => pair.Value.server == server)) { callbacks.TryRemove(pair.Key, out _); }
        server.Dispose();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var server in servers.Values) { Remove(server); }
    }

    private static JObject Notification(string method, JObject parameters) =>
        new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };

    private static JObject Result(JObject request, JToken result) =>
        new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result };
}
