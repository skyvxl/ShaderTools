using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using ShaderTools.VisualStudio.Commands;
using ShaderTools.VisualStudio.Editor;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using ShaderTools.VisualStudio.ToolWindows;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(ShaderOptions), "Shader Tools", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(ShaderToolsWindow))]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ShaderToolsPackage : AsyncPackage
    {
        public const string PackageGuidString = "ffe86866-e88b-483c-ba23-3346fff7bde5";
        private ShaderNavigationBarManager navigationBars;
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await ShaderCommands.InitializeAsync(this);
            object selectionService = await GetServiceAsync(typeof(SVsShellMonitorSelection));
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await GetServiceAsync(typeof(SComponentModel)) is IComponentModel components && selectionService is IVsMonitorSelection selection)
            {
                navigationBars = new ShaderNavigationBarManager(selection,
                                components.GetService<IVsEditorAdaptersFactoryService>(),
                                components.GetService<ITextDocumentFactoryService>());
            }
        }
        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                navigationBars?.Dispose();
                ShaderLanguageClient.Instance?.Dispose();
                BackgroundWork.Stop();
            }
            base.Dispose(disposing);
        }
    }
}
