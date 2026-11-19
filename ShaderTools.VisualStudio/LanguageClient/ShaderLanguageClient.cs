using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using ShaderTools.Core;
using ShaderTools.Core.Configuration;
using ShaderTools.VisualStudio.Editor;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using StreamJsonRpc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.LanguageClient
{
    [Export(typeof(ILanguageClient)), ContentType(ShaderContentTypes.Base)]
    public sealed class ShaderLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
    {
        private Process process;
        private JsonRpc rpc;
        private FileSystemWatcher configurationWatcher;
        private CancellationTokenSource reloadDelay;
        private WorkspaceMonitor workspaceMonitor;
        private long workspaceChangeVersion;
        private long variantIntentVersion;
        private readonly SemaphoreSlim variantGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim restartGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool loaded;
        private bool disposed;
        private readonly object recoveryGate = new object();
        private int automaticRecoveries;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> documents = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);
        public static ShaderLanguageClient Instance { get; private set; }
        public static event EventHandler ReadyChanged;
        public event EventHandler StateChanged;
        public bool IsReady { get; private set; }
        public ShaderVariant ActiveVariant { get; private set; }
        public string WorkspaceRoot { get; private set; } = "";
        public string Status { get; private set; } = "Open a shader to start the language server.";
        public string Name => "Shader Tools";
        public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
        public object InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => new[] { "**/*.glsl", "**/*.hlsli", "**/*.hlsl", "**/*.frag", "**/*.vert" };
        public bool ShowNotificationOnInitializeFailed => true;
        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;
        public object CustomMessageTarget { get; } = new ShaderCustomMessageTarget();
        public object MiddleLayer { get; }

        public ShaderLanguageClient() { Instance = this; MiddleLayer = new ShaderMiddleLayer(this); }

        internal void DocumentOpened(string uri)
        {
            _=documents.GetOrAdd(uri, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
            if (IsReady)
            {
                OnWorkspaceChanged();
            }
        }
        internal void DocumentClosed(string uri)
        {
            if (documents.TryRemove(uri, out TaskCompletionSource<bool> pending))
            {
                _=pending.TrySetCanceled();
            }
        }
        public async Task EnsureDocumentAsync(string path, CancellationToken token)
        {
            string uri = new Uri(path).AbsoluteUri;
            TaskCompletionSource<bool> pending = documents.GetOrAdd(uri, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!pending.Task.IsCompleted)
            {
                await WorkspaceContext.OpenFileAsync(path);
            }

            Task deadline = Task.Delay(10000, token);
            if (await Task.WhenAny(pending.Task, deadline) != pending.Task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The shader document has not connected to the language server. Reopen it or restart the server.");
            }
            _=await pending.Task;
        }

        public async Task OnLoadedAsync()
        {
            if (!loaded)
            {
                ShaderOptions.Changed += OnOptionsChanged;
                workspaceMonitor = new WorkspaceMonitor(OnWorkspaceChanged);
                await workspaceMonitor.InitializeAsync();
                loaded = true;
            }
            if (StartAsync != null)
            {
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
            }
        }

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            SetState(false, "Starting shader language server…");
            string root = await WorkspaceContext.GetRootAsync();
            if (!SameRoot(root, WorkspaceRoot))
            {
                _=Interlocked.Increment(ref variantIntentVersion);
                ActiveVariant = null;
            }
            WorkspaceRoot = root;
            ShaderOptions options = ShaderOptions.Current;
            await options.SaveSnapshotAsync();
            await TaskScheduler.Default;
            token.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bridge = Path.Combine(directory, "Bridge", "ShaderTools.Bridge.exe");
            string server = string.IsNullOrWhiteSpace(options.ServerPath) ? Path.Combine(directory, "Server", "shader-language-server.exe") : options.ServerPath;
            if (!File.Exists(bridge) || !File.Exists(server))
            {
                throw new FileNotFoundException("Shader Tools is incomplete. Reinstall the VSIX; the Bridge and Server folders must be present.");
            }

            await StopProcessAsync();
            string args = "--server " + WindowsCommandLine.Quote(server) + " --settings " + WindowsCommandLine.Quote(ShaderOptions.SettingsPath) +
                " --parent " + Process.GetCurrentProcess().Id;
            if (!string.IsNullOrEmpty(WorkspaceRoot))
            {
                args += " --workspace " + WindowsCommandLine.Quote(WorkspaceRoot);
            }

            string formatDirectory = FindFormatDirectory(options.FormatDirectory);
            if (formatDirectory != null)
            {
                args += " --format-directory " + WindowsCommandLine.Quote(formatDirectory);
            }
            else
            {
                ShaderOutput.Log("Formatting unavailable: install LLVM or set Tools > Options > Shader Tools > clang-format directory, then restart the server.");
            }

            Process started = new Process
            {
                StartInfo = new ProcessStartInfo(bridge, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = string.IsNullOrEmpty(WorkspaceRoot) ? directory : WorkspaceRoot
                },
                EnableRaisingEvents = true
            };
            started.ErrorDataReceived += (_, e) => { if (e.Data != null) { ShaderOutput.Log(e.Data); } };
            started.Exited += (_, __) =>
            {
                if (ReferenceEquals(process, started))
                {
                    SetState(false, "Shader server stopped. Use Extensions > Shader Tools > Restart language server.");
                    if (!disposed && started.ExitCode == 2)
                    {
                        BackgroundWork.Run(() => RecoverAsync(started), "ShaderTools/RecoverServer");
                    }
                }
            };
            _=started.Start();
            process = started;
            started.BeginErrorReadLine();
            WatchConfiguration();
            ShaderOutput.Log("Language client connected. Workspace: " + (string.IsNullOrEmpty(WorkspaceRoot) ? "single file" : WorkspaceRoot));
            return new Connection(started.StandardOutput.BaseStream, started.StandardInput.BaseStream);
        }

        public Task AttachForCustomMessageAsync(JsonRpc jsonRpc)
        {
            rpc = jsonRpc;
            return Task.CompletedTask;
        }
        public Task OnServerInitializedAsync()
        {
            SetState(true, "Ready · GLSL / HLSL / WGSL · shader-sense 1.6.2");
            ShaderVariant variant = ActiveVariant;
            long intent = Interlocked.Read(ref variantIntentVersion);
            if (variant != null)
            {
                BackgroundWork.Run(() => SetVariantCoreAsync(variant, intent, lifetime.Token), "ShaderTools/RestoreVariant");
            }

            return Task.CompletedTask;
        }
        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo info)
        {
            string message = "Shader Tools could not initialize: " + (info.InitializationException?.Message ?? info.StatusMessage) +
                ". Open Extensions > Shader Tools > Show server output for details.";
            SetState(false, message);
            return Task.FromResult(new InitializationFailureContext { FailureMessage = message });
        }

        public Task<JToken> RequestAsync(string method, JObject parameters, CancellationToken token)
        {
            JsonRpc connection = rpc;
            if (!IsReady || connection == null)
            {
                throw new InvalidOperationException("The shader language server is not ready. Open a shader or restart the server.");
            }

            return connection.InvokeWithParameterObjectAsync<JToken>(method, parameters, token);
        }

        public Task NotifyAsync(string method, JObject parameters)
        {
            JsonRpc connection = rpc;
            if (!IsReady || connection == null)
            {
                throw new InvalidOperationException("The shader language server is not ready.");
            }

            return connection.NotifyWithParameterObjectAsync(method, parameters);
        }

        public Task RestartAsync()
        {
            lock (recoveryGate)
            {
                automaticRecoveries = 0;
            }

            return RestartCoreAsync(false);
        }

        private async Task RecoverAsync(Process stopped)
        {
            lock (recoveryGate)
            {
                if (automaticRecoveries >= 2)
                {
                    return;
                }

                automaticRecoveries++;
            }
            await Task.Delay(500, lifetime.Token);
            if (disposed || !ReferenceEquals(process, stopped))
            {
                return;
            }

            ShaderOutput.Log("Restarting shader analysis after an unresponsive session.");
            // The standard VS stop/start lifecycle reopens its current buffers. ActiveVariant is restored
            // by OnServerInitializedAsync, and ActivateAsync reloads the current configuration.
            await RestartCoreAsync(false);
        }

        private async Task RestartCoreAsync(bool workspaceChanged)
        {
            await restartGate.WaitAsync(lifetime.Token);
            try
            {
                if (workspaceChanged && SameRoot(WorkspaceRoot, await WorkspaceContext.GetRootAsync()))
                {
                    return;
                }

                SetState(false, "Restarting shader language server…");
                Task stopping = StopAsync?.InvokeAsync(this, EventArgs.Empty) ?? Task.CompletedTask;
                if (await Task.WhenAny(stopping, Task.Delay(3000, lifetime.Token)) != stopping)
                {
                    ShaderOutput.Log("Graceful server shutdown timed out; closing the bridge.");
                }

                await StopProcessAsync();
                if (await Task.WhenAny(stopping, Task.Delay(2000, lifetime.Token)) == stopping)
                {
                    try { await stopping; } catch (Exception ex) { ShaderOutput.Log("Server shutdown: " + ex.Message); }
                }
                else
                {
                    // Observe a late fault without allowing a broken host stop handler to block recovery.
                    _ = stopping.ContinueWith(t => ShaderOutput.Log(t.Exception.GetBaseException().Message),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                lifetime.Token.ThrowIfCancellationRequested();
                rpc = null;
                foreach (KeyValuePair<string, TaskCompletionSource<bool>> entry in documents)
                {
                    _=entry.Value.TrySetCanceled();
                }

                documents.Clear();
                ShaderDiagnostics.Clear();
                if (StartAsync != null)
                {
                    await StartAsync.InvokeAsync(this, EventArgs.Empty);
                }
            }
            finally { _=restartGate.Release(); }
        }

        public Task SetVariantAsync(ShaderVariant variant, CancellationToken token)
        {
            return SetVariantCoreAsync(variant, Interlocked.Increment(ref variantIntentVersion), token);
        }

        private async Task SetVariantCoreAsync(ShaderVariant variant, long intent, CancellationToken token)
        {
            await variantGate.WaitAsync(token);
            try
            {
                if (intent != Interlocked.Read(ref variantIntentVersion))
                {
                    return;
                }

                if (variant != null)
                {
                    await EnsureDocumentAsync(variant.FilePath, token);
                }

                if (intent != Interlocked.Read(ref variantIntentVersion))
                {
                    return;
                }

                if (IsReady)
                {
                    await NotifyAsync("textDocument/didChangeShaderVariant",
                    new JObject { ["shaderVariant"] = variant == null ? JValue.CreateNull() : variant.ToProtocol() });
                }
                else if (variant != null)
                {
                    throw new InvalidOperationException("The shader language server is not ready.");
                }

                ActiveVariant = variant;
                SetState(IsReady, Status);
            }
            finally { _=variantGate.Release(); }
        }

        private static bool SameRoot(string first, string second)
        {
            return string.Equals(
            (first ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            (second ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private void OnWorkspaceChanged()
        {
            long version = Interlocked.Increment(ref workspaceChangeVersion);
            BackgroundWork.Run(async () =>
            {
                await Task.Delay(350, lifetime.Token);
                if (version == Interlocked.Read(ref workspaceChangeVersion))
                {
                    await RestartCoreAsync(true);
                }
            }, "ShaderTools/WorkspaceChanged");
        }

        public async Task ReloadConfigurationAsync()
        {
            if (IsReady)
            {
                await NotifyAsync("workspace/didChangeConfiguration", new JObject { ["settings"] = new JObject() });
                // Invalidate editor caches even when the document text itself is unchanged.
                SetState(IsReady, Status);
            }
        }

        private void WatchConfiguration()
        {
            configurationWatcher?.Dispose();
            if (string.IsNullOrEmpty(WorkspaceRoot) || !Directory.Exists(WorkspaceRoot))
            {
                return;
            }

            configurationWatcher = new FileSystemWatcher(WorkspaceRoot, ".shadertools.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            configurationWatcher.Changed += OnConfigurationFileChanged;
            configurationWatcher.Created += OnConfigurationFileChanged;
            configurationWatcher.Deleted += OnConfigurationFileChanged;
            configurationWatcher.Renamed += OnConfigurationFileChanged;
            configurationWatcher.EnableRaisingEvents = true;
        }

        private void OnConfigurationFileChanged(object sender, FileSystemEventArgs e)
        {
            CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CancellationTokenSource old = Interlocked.Exchange(ref reloadDelay, delay);
            try { old?.Cancel(); } catch (ObjectDisposedException) { }
            BackgroundWork.Run(async () =>
            {
                try { await Task.Delay(350, delay.Token); await ReloadConfigurationAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ShaderOutput.Log("Settings reload failed: " + ex.Message); }
                finally { _=Interlocked.CompareExchange(ref reloadDelay, null, delay); delay.Dispose(); }
            }, "ShaderTools/ConfigurationReload");
        }

        private void OnOptionsChanged(object sender, EventArgs e)
        {
            BackgroundWork.Run(async () =>
        {
            try { await ReloadConfigurationAsync(); }
            catch (Exception ex) { ShaderOutput.Log(ex.Message); }
        }, "ShaderTools/OptionsReload");
        }

        private void SetState(bool ready, string status)
        {
            IsReady = ready;
            Status = status;
            ShaderOutput.Log(status);
            BackgroundWork.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StateChanged?.Invoke(this, EventArgs.Empty);
                ReadyChanged?.Invoke(this, EventArgs.Empty);
            }, "ShaderTools/StateChanged");
        }

        private async Task StopProcessAsync()
        {
            Process previous = process;
            process = null;
            if (previous == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    if (!previous.HasExited)
                    {
                        previous.StandardInput.Close();
                        if (!previous.WaitForExit(4000))
                        {
                            previous.Kill();
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { ShaderOutput.Log(ex.Message); }
                finally { previous.Dispose(); }
            });
        }

        internal static string FindFormatDirectory(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return File.Exists(Path.Combine(configured, "clang-format.exe")) ? configured : null;
            }

            string ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string install = Path.GetFullPath(Path.Combine(ide, "..", ".."));
            foreach (string candidate in new[] { Path.Combine(install, "VC", "Tools", "Llvm", "x64", "bin"), Path.Combine(install, "VC", "Tools", "Llvm", "bin") })
            {
                if (File.Exists(Path.Combine(candidate, "clang-format.exe")))
                {
                    return candidate;
                }
            }

            foreach (string candidate in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    if (File.Exists(Path.Combine(candidate.Trim('"'), "clang-format.exe")))
                    {
                        return candidate.Trim('"');
                    }
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetime.Cancel();
            configurationWatcher?.Dispose();
            ShaderOptions.Changed -= OnOptionsChanged;
            WorkspaceMonitor monitor = workspaceMonitor;
            workspaceMonitor = null;
            if (monitor != null)
            {
                BackgroundWork.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                monitor.Dispose();
            }, "ShaderTools/WorkspaceMonitorDispose");
            }

            Process previous = process;
            process = null;
            // Closing stdin allows the bridge to stop its compiler process without blocking VS teardown.
            try { previous?.StandardInput.Close(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            previous?.Dispose();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
    }

    internal sealed class ShaderMiddleLayer : ILanguageClientMiddleLayer2<JToken>
    {
        private readonly ShaderLanguageClient owner;
        public ShaderMiddleLayer(ShaderLanguageClient owner) { this.owner = owner; }
        public bool CanHandle(string methodName)
        {
            return methodName is "textDocument/didOpen" or "textDocument/didClose";
        }

        public async Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
        {
            await sendNotification(methodParam);
            string uri = (string)methodParam.SelectToken("textDocument.uri");
            if (uri != null && methodName == "textDocument/didOpen")
            {
                owner.DocumentOpened(uri);
            }

            if (uri != null && methodName == "textDocument/didClose")
            {
                owner.DocumentClosed(uri);
            }
        }
        public Task<JToken> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken>> sendRequest)
        {
            return sendRequest(methodParam);
        }
    }

    internal sealed class ShaderCustomMessageTarget
    {
        [JsonRpcMethod("shaderTools/openConfiguration", UseSingleObjectParameterDeserialization = true)]
        public Task OpenConfigurationAsync(JToken parameters)
        {
            string uri = (string)parameters?["uri"];
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri file) || !file.IsFile)
            {
                return Task.CompletedTask;
            }

            return WorkspaceContext.OpenFileAsync(file.LocalPath);
        }
        [JsonRpcMethod("shaderTools/diagnostics", UseSingleObjectParameterDeserialization = true)]
        public void Diagnostics(JToken parameters)
        {
            ShaderDiagnostics.Update(parameters);
        }
    }
}
