using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using ShaderTools.VisualStudio.ToolWindows;
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Commands
{
    internal static class ShaderCommands
    {
        public static readonly Guid CommandSet = new Guid("74156DDE-8CA2-4A51-ACCA-94C52A6DB371");
        public const int ShowWindow = 0x0100, Restart = 0x0101, ShowOutput = 0x0102, EditConfiguration = 0x0103,
            EditVariants = 0x0104, Validate = 0x0105, ShowOptions = 0x0106;

        public static async Task InitializeAsync(ShaderToolsPackage package)
        {
            OleMenuCommandService commands = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Add(commands, ShowWindow, () => package.ShowToolWindowAsync(typeof(ShaderToolsWindow), 0, true, package.DisposalToken));
            Add(commands, Restart, () => Client().RestartAsync());
            Add(commands, ShowOutput, ShaderOutput.ShowAsync);
            Add(commands, EditConfiguration, () => EditProjectFileAsync(false));
            Add(commands, EditVariants, () => EditProjectFileAsync(true));
            Add(commands, Validate, ValidateActiveAsync);
            Add(commands, ShowOptions, () => { package.ShowOptionPage(typeof(ShaderOptions)); return Task.CompletedTask; });
        }

        private static void Add(OleMenuCommandService commands, int id, Func<Task> action)
        {
            commands?.AddCommand(
            new OleMenuCommand((_, __) => Run(action), new CommandID(CommandSet, id)));
        }

        public static void Run(Func<Task> action)
        {
            BackgroundWork.Run(async () =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShaderOutput.Log(ex.Message);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _=VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, ex.Message, "Shader Tools",
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }, "ShaderTools/Command");
        }

        public static ShaderLanguageClient Client()
        {
            return ShaderLanguageClient.Instance ?? throw new InvalidOperationException("Open a GLSL or HLSL shader to start Shader Tools.");
        }

        public static async Task EditProjectFileAsync(bool variants)
        {
            string root = await WorkspaceContext.GetRootAsync();
            if (string.IsNullOrEmpty(root))
            {
                throw new InvalidOperationException("Open a solution, folder or saved shader first.");
            }

            string active = await WorkspaceContext.GetActiveFileAsync();
            if (ShaderContentTypes.ForPath(active ?? "") == null) { active = null; }
            string path = variants ? VariantCatalog.FindProjectCatalog(root, active) :
                active == null ? Path.Combine(root, ShaderConfiguration.FileName) : ShaderConfiguration.FindConfigurationPath(root, active);
            if (!File.Exists(path))
            {
                string text;
                if (variants)
                {
                    JArray entries = [];
                    if (!string.IsNullOrEmpty(active) && ShaderContentTypes.ForPath(active) != null)
                    {
                        entries.Add(new JObject
                        {
                            ["name"] = "Default",
                            ["file"] = Uri.UnescapeDataString(new Uri(Path.GetDirectoryName(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(active)).ToString()),
                            ["entryPoint"] = "main",
                            ["stage"] = "auto",
                            ["defines"] = new JObject(),
                            ["includes"] = new JArray()
                        });
                    }

                    text = entries.ToString(Formatting.Indented);
                }
                else
                {
                    text = new JObject
                    {
                        ["includes"] = new JArray(),
                        ["defines"] = new JObject(),
                        ["glsl"] = new JObject { ["targetClient"] = ShaderOptions.Current.TargetClient.ToString(), ["spirvVersion"] = ShaderOptions.Current.SpirvVersion.ToString() }
                    }.ToString(Formatting.Indented);
                }

                await Task.Run(() =>
                {
                    using FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); using StreamWriter writer = new StreamWriter(file);
                    writer.Write(text);
                });
            }
            await WorkspaceContext.OpenFileAsync(path);
        }

        private static async Task ValidateActiveAsync()
        {
            string file = await WorkspaceContext.GetActiveFileAsync();
            if (string.IsNullOrEmpty(file) || ShaderContentTypes.ForPath(file) == null)
            {
                throw new InvalidOperationException("Select a shader editor first. Use a variant to select its entry point and stage.");
            }

            ShaderLanguageClient client = Client();
            using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.EnsureDocumentAsync(file, cancellation.Token);
            JToken result = await client.RequestAsync("textDocument/diagnostic",
                new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(file).AbsoluteUri } }, cancellation.Token);
            if (result?["items"] is not JArray diagnostics)
            {
                throw new InvalidOperationException("The shader server returned no validation report.");
            }

            int errors = 0, warnings = 0;
            foreach (JToken diagnostic in diagnostics)
            {
                if ((int?)diagnostic["severity"] == 1)
                {
                    errors++;
                }
                else if ((int?)diagnostic["severity"] == 2)
                {
                    warnings++;
                }
            }
            ShaderOutput.Log(Path.GetFileName(file) + ": " + errors + " error(s), " + warnings + " warning(s).");
            foreach (JToken diagnostic in diagnostics)
            {
                if ((int?)diagnostic["severity"] <= 2)
                {
                    ShaderOutput.Log("  Line " + (((int?)diagnostic.SelectToken("range.start.line") ?? 0) + 1) + ": " + (string)diagnostic["message"]);
                }
            }

            await ShaderOutput.ShowAsync();
        }
    }
}
