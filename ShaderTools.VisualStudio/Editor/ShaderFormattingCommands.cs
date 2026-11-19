using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Options;
using ShaderTools.VisualStudio.Services;
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(ShaderContentTypes.Glsl)]
    [ContentType(ShaderContentTypes.Hlsl)]
    [ContentType(ShaderContentTypes.Wgsl)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class ShaderFormattingCommands : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters { get; set; }
        [Import] internal ShaderFormatting Formatting { get; set; }

        [Import] internal ShaderNavigableSymbolSourceProvider Navigation { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IWpfTextView view = Adapters.GetWpfTextView(textViewAdapter);
            if (view == null)
            {
                return;
            }

            if (!view.TextBuffer.ContentType.IsOfType(ShaderContentTypes.Wgsl)) { Formatting.Attach(view); }
            _ = new CommandFilter(textViewAdapter, view, Formatting, Navigation);
        }

        // Handle the standard editor commands, so user keyboard bindings and the Edit menu
        // both work for shader documents, independently of the CodeRemote formatting handler.
        private sealed class CommandFilter : IOleCommandTarget
        {
            private readonly IVsTextView adapter;
            private readonly IWpfTextView view;
            private readonly ShaderFormatting formatting;
            private readonly ShaderNavigableSymbolSourceProvider navigation;
            private INavigableSymbolSource symbols;
            private readonly IOleCommandTarget next;

            internal CommandFilter(IVsTextView adapter, IWpfTextView view, ShaderFormatting formatting, ShaderNavigableSymbolSourceProvider navigation)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                this.adapter = adapter;
                this.view = view;
                this.formatting = formatting;
                this.navigation = navigation;
                _=ErrorHandler.ThrowOnFailure(adapter.AddCommandFilter(this, out next));
                view.Closed += OnClosed;
            }

            public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                int result = next.QueryStatus(ref group, count, commands, text);
                bool handled = false;
                for (int i = 0; i < count; i++)
                {
                    if ((IsFormat(group, commands[i].cmdID) && ShaderFormatting.Supports(view.TextBuffer)) ||
                        CanGoToDefinition(group, commands[i].cmdID))
                    {
                        commands[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                        handled = true;
                    }
                }
                return handled ? VSConstants.S_OK : result;
            }

            public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (IsFormat(group, id) && ShaderFormatting.Supports(view.TextBuffer))
                {
                    Span? selection = id == (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION
                        ? view.Selection.StreamSelectionSpan.SnapshotSpan.Span : null;
                    formatting.Format(view.TextBuffer, view, selection);
                    return VSConstants.S_OK;
                }
                if (CanGoToDefinition(group, id))
                {
                    GoToDefinition();
                    return VSConstants.S_OK;
                }
                return next.Exec(ref group, id, options, input, output);
            }

            private bool CanGoToDefinition(Guid group, uint id)
            {
                return group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn &&
                    ShaderLanguageClient.Instance?.IsReady == true &&
                    (ShaderFormatting.Supports(view.TextBuffer) ||
                     (ShaderOptions.Current.EnableWgsl && view.TextBuffer.ContentType.IsOfType(ShaderContentTypes.Wgsl)));
            }

            private void GoToDefinition()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                SnapshotPoint point = view.Caret.Position.BufferPosition;
                if (point.Snapshot.TextBuffer != view.TextBuffer) { return; }
                symbols ??= navigation.TryCreateNavigableSymbolSource(view, view.TextBuffer);
                BackgroundWork.Run(async () =>
                {
                    INavigableSymbol symbol = await symbols.GetNavigableSymbolAsync(new SnapshotSpan(point, 0), BackgroundWork.Token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(BackgroundWork.Token);
                    if (view.IsClosed || !view.HasAggregateFocus || point != view.Caret.Position.BufferPosition) { return; }
                    if (symbol != null) { symbol.Navigate(PredefinedNavigableRelationships.Definition); }
                    else { ShaderOutput.Log("No unique shader definition found at the caret."); }
                }, "ShaderTools/GoToDefinition");
            }

            private static bool IsFormat(Guid group, uint id)
            {
                return group == VSConstants.VSStd2K &&
                (id == (uint)VSConstants.VSStd2KCmdID.FORMATDOCUMENT || id == (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION);
            }

            private void OnClosed(object sender, EventArgs e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                view.Closed -= OnClosed;
                symbols?.Dispose();
                try { _=adapter.RemoveCommandFilter(this); }
                catch (COMException) { } // The native view may already have been torn down.
            }
        }
    }
}
