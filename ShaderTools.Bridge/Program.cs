using ShaderTools.Core.Configuration;
using ShaderTools.Core.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.Bridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("Cannot run shader server: " + ex.Message); return 1; }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            Dictionary<string, string> arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !new[] { "--server", "--workspace", "--settings", "--parent", "--format-directory" }.Contains(args[i]))
                {
                    throw new ArgumentException("Expected --server PATH [--workspace PATH] [--settings PATH] [--parent PID] [--format-directory PATH].");
                }

                arguments.Add(args[i], args[i + 1]);
            }
            if (!arguments.TryGetValue("--server", out string server) || !File.Exists(server))
            {
                throw new FileNotFoundException("Shader language server is missing. Reinstall Shader Tools or check its server path.", server);
            }

            _=arguments.TryGetValue("--workspace", out string root);
            _=arguments.TryGetValue("--settings", out string settingsPath);
            string configurationRoot = string.IsNullOrWhiteSpace(root) ? Environment.CurrentDirectory : Path.GetFullPath(root);
            ShaderConfigurationStore configurations = new ShaderConfigurationStore(settingsPath, Log);
            arguments.TryGetValue("--format-directory", out string formatDirectory);
            // Framework Process constructs its redirected stdin writer with Console.InputEncoding.
            // A preamble would precede the first Content-Length header sent to each compiler.
            if (Console.InputEncoding.GetPreamble().Length != 0)
            {
                Console.InputEncoding = new System.Text.UTF8Encoding(false);
            }
            using ProcessJob job = ProcessJob.CreateKillOnClose();
            using CancellationTokenSource lifetime = new CancellationTokenSource();
            var documents = new PositionEncodingConverter();
            using ProjectRouter router = new ProjectRouter(server, configurationRoot, formatDirectory, configurations, documents, job, Log);
            var adapter = new ProtocolAdapter(root,
                workspace => configurations.LoadWorkspace(string.IsNullOrEmpty(workspace) ? configurationRoot : workspace).Settings,
                Log, configurations.LoadDocument, compilationRoot: router.CompilationRoot);
            var relay = new LspRelay(adapter, Log, positions: documents);
            Task monitor = MonitorParentAsync(arguments, lifetime);
            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            try
            {
                await relay.RunAsync(input, output, router, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally
            {
                lifetime.Cancel();
                await monitor.ConfigureAwait(false);
            }
            // A distinct code permits bounded recovery in VS without retrying configuration/startup errors.
            return relay.RecoveryRequired ? 2 : 0;
        }

        private static async Task MonitorParentAsync(Dictionary<string, string> arguments, CancellationTokenSource lifetime)
        {
            if (!arguments.TryGetValue("--parent", out string parentText) || !int.TryParse(parentText, out int parentId))
            {
                return;
            }

            try
            {
                using Process parent = Process.GetProcessById(parentId);
                while (!lifetime.IsCancellationRequested)
                {
                    if (parent.HasExited) { lifetime.Cancel(); return; }
                    await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ArgumentException) { lifetime.Cancel(); }
            catch (InvalidOperationException) { lifetime.Cancel(); }
        }

        private static void Log(string message)
        {
            Console.Error.WriteLine("[Shader Tools] " + message);
        }
    }
}
