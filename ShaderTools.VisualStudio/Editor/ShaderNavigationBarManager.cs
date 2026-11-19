using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Editor
{
    // Code windows (including their split views) own bars; buffers can be shared by several windows.
    internal sealed class ShaderNavigationBarManager : IVsSelectionEvents, IDisposable
    {
        private readonly IVsMonitorSelection selection;
        private readonly IVsEditorAdaptersFactoryService adapters;
        private readonly ITextDocumentFactoryService documents;
        private readonly Dictionary<IVsCodeWindow, ShaderNavigationBarClient> clients =
            [];
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private uint cookie;
        private int attachmentVersion;
        private bool disposed;
        private ShaderNavigationBarClient active;

        internal ShaderNavigationBarManager(IVsMonitorSelection selection,
            IVsEditorAdaptersFactoryService adapters, ITextDocumentFactoryService documents)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.selection = selection;
            this.adapters = adapters;
            this.documents = documents;
            _=ErrorHandler.ThrowOnFailure(selection.AdviseSelectionEvents(this, out cookie));
            documents.TextDocumentCreated += OnDocumentCreated;
            documents.TextDocumentDisposed += OnDocumentDisposed;
            ScheduleAttachment();
        }

        private void ScheduleAttachment()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed)
            {
                return;
            }

            int version = ++attachmentVersion;
            CancellationToken token = lifetime.Token;
            active?.SetActive(false);
            active = null;
            BackgroundWork.Run(async () =>
            {
                try
                {
                    // Selection can arrive before the editor adapters are initialized.
                    foreach (int delay in new[] { 1, 250, 500 })
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                        if (disposed || version != attachmentVersion)
                        {
                            return;
                        }

                        if (TryAttachActiveFrame())
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ActivityLog.LogError(nameof(ShaderNavigationBarManager), ex.ToString()); }
            }, "ShaderTools/AttachNavigationBar");
        }

        private bool TryAttachActiveFrame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ErrorHandler.Failed(selection.GetCurrentElementValue(
                (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object value)) ||
                value is not IVsWindowFrame frame)
            {
                return true;
            }

            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object raw)) ||
                raw is not IVsCodeWindow window || raw is not IVsDropdownBarManager manager)
            {
                return true;
            }

            if (ErrorHandler.Failed(window.GetBuffer(out IVsTextLines lines)))
            {
                return false;
            }

            ITextBuffer buffer = adapters.GetDataBuffer(lines);
            if (buffer == null)
            {
                return false;
            }

            if (clients.TryGetValue(window, out ShaderNavigationBarClient previous))
            {
                if (ReferenceEquals(previous.Buffer, buffer) && IsShader(buffer))
                {
                    // The generic LSP provider can replace a bar while its document is inactive.
                    if (!ShaderNavigationBarRegistration.TryInstall(manager, previous, out IVsDropdownBar restored))
                    {
                        return true;
                    }

                    _=previous.SetDropdownBar(restored);
                    active = previous;
                    active.SetActive(true);
                    return true;
                }
                previous.Dispose();
            }
            if (!IsShader(buffer))
            {
                return true;
            }

            if (!documents.TryGetTextDocument(buffer, out ITextDocument document))
            {
                return false;
            }

            ShaderNavigationBarClient client = new ShaderNavigationBarClient(window, manager, adapters, document,
                closed => { _=clients.Remove(window); if (ReferenceEquals(active, closed)) { active = null; } });
            if (!client.HasViews)
            {
                client.Dispose();
                return false;
            }
            clients.Add(window, client);
            if (!ShaderNavigationBarRegistration.TryInstall(manager, client, out IVsDropdownBar bar))
            {
                client.Dispose();
                return true;
            }
            _=client.SetDropdownBar(bar);
            active = client;
            client.SetActive(true);
            return true;
        }

        internal static bool IsShader(ITextBuffer buffer)
        {
            return buffer.ContentType.IsOfType(ShaderContentTypes.Glsl) ||
            buffer.ContentType.IsOfType(ShaderContentTypes.Hlsl) ||
            buffer.ContentType.IsOfType(ShaderContentTypes.Wgsl);
        }

        private void OnDocumentDisposed(object sender, TextDocumentEventArgs e)
        {
            ShaderUiNotifications.Post(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ShaderNavigationBarClient client in clients.Values.Where(x => ReferenceEquals(x.Buffer, e.TextDocument.TextBuffer)).ToArray())
            {
                client.Dispose();
            }
        });
        }

        private void OnDocumentCreated(object sender, TextDocumentEventArgs e)
        {
            ShaderUiNotifications.Post(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsShader(e.TextDocument.TextBuffer))
            {
                ScheduleAttachment();
            }
        });
        }

        public int OnElementValueChanged(uint elementid, object oldValue, object newValue)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame)
            {
                ScheduleAttachment();
            }

            return VSConstants.S_OK;
        }
        public int OnCmdUIContextChanged(uint context, int active)
        {
            return VSConstants.S_OK;
        }

        public int OnSelectionChanged(IVsHierarchy oldHierarchy, uint oldItem, IVsMultiItemSelect oldSelection,
                    ISelectionContainer oldContainer, IVsHierarchy newHierarchy, uint newItem,
                    IVsMultiItemSelect newSelection, ISelectionContainer newContainer)
        {
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetime.Cancel();
            documents.TextDocumentCreated -= OnDocumentCreated;
            documents.TextDocumentDisposed -= OnDocumentDisposed;
            if (cookie != 0)
            {
                _=selection.UnadviseSelectionEvents(cookie);
            }

            cookie = 0;
            foreach (ShaderNavigationBarClient client in clients.Values.ToArray())
            {
                client.Dispose();
            }

            clients.Clear();
            lifetime.Dispose();
        }
    }
}
