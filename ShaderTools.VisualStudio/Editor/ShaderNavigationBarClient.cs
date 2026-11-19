using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Editor;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Editor
{
    internal sealed class ShaderNavigationBarClient : IVsDropdownBarClient, IVsDropdownBarClient3,
        IVsCoTaskMemFreeMyStrings, IVsCodeWindowEvents, IDisposable
    {
        private readonly IVsCodeWindow window;
        private readonly IVsDropdownBarManager manager;
        private readonly IVsEditorAdaptersFactoryService adapters;
        private readonly ITextDocument document;
        private readonly Action<ShaderNavigationBarClient> closed;
        private readonly HashSet<IWpfTextView> views = [];
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private IConnectionPoint events;
        private int eventCookie;
        private IVsDropdownBar bar;
        private IWpfTextView view;
        private ShaderDocumentSymbols symbols = ShaderDocumentSymbols.Read(null);
        private ITextSnapshot symbolSnapshot;
        private string symbolUri;
        private CancellationTokenSource pending;
        private int generation;
        private int scopeIndex;
        private bool active;
        private bool disposed;

        internal ShaderNavigationBarClient(IVsCodeWindow window, IVsDropdownBarManager manager,
            IVsEditorAdaptersFactoryService adapters, ITextDocument document, Action<ShaderNavigationBarClient> closed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.window = window; this.manager = manager; this.adapters = adapters;
            this.document = document; this.closed = closed;
            if (window is IConnectionPointContainer connection)
            {
                Guid guid = typeof(IVsCodeWindowEvents).GUID;
                connection.FindConnectionPoint(ref guid, out events);
                events.Advise(this, out eventCookie);
            }
            DiscoverViews();
            Buffer.Changed += OnBufferChanged;
            Buffer.ContentTypeChanged += OnContentTypeChanged;
            document.FileActionOccurred += OnFileAction;
            ShaderLanguageClient.ReadyChanged += OnReadyChanged;
        }
        internal ITextBuffer Buffer => document.TextBuffer;
        internal bool HasViews => views.Count > 0;
        internal bool HasBar => bar != null;

        private void DiscoverViews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ErrorHandler.Succeeded(window.GetPrimaryView(out IVsTextView primary)))
            {
                AddView(primary);
            }

            if (ErrorHandler.Succeeded(window.GetSecondaryView(out IVsTextView secondary)))
            {
                AddView(secondary);
            }

            if (ErrorHandler.Succeeded(window.GetLastActiveView(out IVsTextView current)))
            {
                IWpfTextView candidate = current == null ? null : adapters.GetWpfTextView(current);
                if (candidate != null && views.Contains(candidate))
                {
                    view = candidate;
                }
            }
        }
        private void AddView(IVsTextView adapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (adapter == null || disposed)
            {
                return;
            }

            IWpfTextView candidate = adapters.GetWpfTextView(adapter);
            if (candidate == null || candidate.IsClosed || !ReferenceEquals(candidate.TextBuffer, Buffer) || !views.Add(candidate))
            {
                return;
            }

            candidate.Caret.PositionChanged += OnCaretChanged;
            candidate.GotAggregateFocus += OnFocus;
            candidate.Closed += OnViewClosed;
            if (view == null || candidate.HasAggregateFocus)
            {
                view = candidate;
            }
        }
        public int OnNewView(IVsTextView newView)
        {
            ShaderUiNotifications.Post(() => { ThreadHelper.ThrowIfNotOnUIThread(); AddView(newView); });
            return VSConstants.S_OK;
        }
        public int OnCloseView(IVsTextView oldView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IWpfTextView old = adapters.GetWpfTextView(oldView);
            if (old != null)
            {
                RemoveView(old);
            }

            return VSConstants.S_OK;
        }
        private void RemoveView(IWpfTextView old)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!views.Remove(old))
            {
                return;
            }

            old.Caret.PositionChanged -= OnCaretChanged;
            old.GotAggregateFocus -= OnFocus;
            old.Closed -= OnViewClosed;
            if (ReferenceEquals(view, old))
            {
                view = views.FirstOrDefault(x => !x.IsClosed);
            }

            if (views.Count == 0)
            {
                Dispose();
            }
            else if (active)
            {
                RefreshSelections(); if (pending != null)
                {
                    ScheduleRefresh();
                }
            }
        }
        private void OnViewClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is IWpfTextView old)
            {
                RemoveView(old);
            }
        }
        private void OnFocus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || sender is not IWpfTextView focused)
            {
                return;
            }

            view = focused;
            RefreshSelections();
            if (active && (pending != null || !IsCurrent))
            {
                ScheduleRefresh();
            }
        }
        internal void SetActive(bool value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed)
            {
                return;
            }

            active = value;
            if (!value) { pending?.Cancel(); generation++; return; }
            DiscoverViews();
            RefreshSelections();
            if (!IsCurrent)
            {
                ScheduleRefresh();
            }
        }
        private string DocumentUri => Uri.TryCreate(document.FilePath, UriKind.Absolute, out Uri uri) && uri.IsFile
            ? uri.AbsoluteUri : null;
        private bool IsCurrent => symbolSnapshot == Buffer.CurrentSnapshot &&
            string.Equals(symbolUri, DocumentUri, StringComparison.OrdinalIgnoreCase);
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            ShaderUiNotifications.Post(ScheduleRefresh);
        }

        private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            ShaderUiNotifications.Post(ScheduleRefresh);
        }

        private void OnReadyChanged(object sender, EventArgs e)
        {
            ShaderUiNotifications.Post(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    symbolSnapshot = null;
                    ScheduleRefresh();
                });
        }

        private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
        {
            ShaderUiNotifications.Post(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (!ShaderNavigationBarManager.IsShader(Buffer))
                    {
                        Dispose();
                    }
                });
        }

        private void OnCaretChanged(object sender, CaretPositionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ReferenceEquals(e.TextView, view))
            {
                RefreshSelections();
            }
        }

        private void ScheduleRefresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed)
            {
                return;
            }

            pending?.Cancel();
            int version = ++generation;
            ShaderLanguageClient client = ShaderLanguageClient.Instance;
            string uri = DocumentUri;
            if (!active || view == null || view.IsClosed || uri == null || client?.IsReady != true)
            {
                return;
            }

            CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, BackgroundWork.Token);
            request.CancelAfter(TimeSpan.FromSeconds(10));
            pending = request;
            ITextSnapshot snapshot = Buffer.CurrentSnapshot;
            IWpfTextView requestedView = view;
            BackgroundWork.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, request.Token).ConfigureAwait(false);
                    JToken response = await client.RequestAsync("textDocument/documentSymbol", new JObject
                    {
                        ["textDocument"] = new JObject { ["uri"] = uri }
                    }, request.Token).ConfigureAwait(false);
                    ShaderDocumentSymbols updated = ShaderDocumentSymbols.Read(response, uri);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(request.Token);
                    if (disposed || !active || version != generation || snapshot != Buffer.CurrentSnapshot ||
                        !ReferenceEquals(view, requestedView) || view.IsClosed ||
                        !string.Equals(uri, DocumentUri, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    symbols = updated;
                    symbolSnapshot = snapshot;
                    symbolUri = uri;
                    RefreshSelections();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ActivityLog.LogError(nameof(ShaderNavigationBarClient), ex.ToString()); }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ReferenceEquals(pending, request))
                    {
                        pending = null;
                    }

                    request.Dispose();
                }
            }, "ShaderTools/RefreshNavigationBar");
        }

        private void RefreshSelections()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || bar == null || view == null || view.IsClosed || !active || !IsCurrent)
            {
                return;
            }

            SnapshotPoint point = view.Caret.Position.BufferPosition;
            ITextSnapshotLine line = point.GetContainingLine();
            (int Scope, int Member) selection = symbols.Select(line.LineNumber, point.Position - line.Start.Position);
            scopeIndex = selection.Scope;
            _=bar.RefreshCombo(0, scopeIndex);
            _=bar.RefreshCombo(1, selection.Member);
        }
        private IReadOnlyList<ShaderNavigationSymbol> Items(int combo)
        {
            return combo == 0 ? symbols.Scopes :
            combo == 1 && scopeIndex >= 0 && scopeIndex < symbols.Scopes.Count ? symbols.Scopes[scopeIndex].Members :
            Array.Empty<ShaderNavigationSymbol>();
        }

        private ShaderNavigationSymbol Item(int combo, int index)
        {
            IReadOnlyList<ShaderNavigationSymbol> items = Items(combo);
            return index >= 0 && index < items.Count ? items[index] : null;
        }

        public int SetDropdownBar(IVsDropdownBar value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ReferenceEquals(bar, value))
            {
                return VSConstants.S_OK;
            }

            bar = value;
            if (!disposed && active)
            {
                ScheduleRefresh();
            }

            return VSConstants.S_OK;
        }
        public int GetComboAttributes(int combo, out uint count, out uint entryType, out IntPtr images)
        {
            count = (uint)Items(combo).Count;
            entryType = (uint)DROPDOWNENTRYTYPE.ENTRY_TEXT;
            images = IntPtr.Zero;
            return combo is 0 or 1 ? VSConstants.S_OK : VSConstants.E_INVALIDARG;
        }
        public int GetEntryText(int combo, int index, out string text)
        {
            text = Item(combo, index)?.Label;
            return text != null ? VSConstants.S_OK : VSConstants.E_INVALIDARG;
        }
        public int OnItemChosen(int combo, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShaderNavigationSymbol item = Item(combo, index);
            if (item == null || disposed || !active || view == null || view.IsClosed || !IsCurrent)
            {
                return VSConstants.S_OK;
            }

            if (combo == 0)
            {
                scopeIndex = index;
                _=(bar?.RefreshCombo(1, -1));
                if (index == 0)
                {
                    return VSConstants.S_OK;
                }
            }
            ITextSnapshot snapshot = Buffer.CurrentSnapshot;
            if (item.SelectionStart.Line >= snapshot.LineCount)
            {
                return VSConstants.S_OK;
            }

            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(item.SelectionStart.Line);
            if (item.SelectionStart.Character > line.Length)
            {
                return VSConstants.S_OK;
            }

            SnapshotPoint target = new SnapshotPoint(snapshot, line.Start.Position + item.SelectionStart.Character);
            _=view.Caret.MoveTo(target);
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(target, 0));
            _=view.VisualElement.Focus();
            return VSConstants.S_OK;
        }
        public int OnItemSelected(int combo, int index)
        {
            return VSConstants.S_OK;
        }

        public int OnComboGetFocus(int combo)
        {
            return VSConstants.S_OK;
        }

        public int GetComboTipText(int combo, out string text)
        {
            text = combo == 0 ? "Shader scopes and types" : "Shader functions and members";
            return VSConstants.S_OK;
        }
        public int GetEntryAttributes(int combo, int index, out uint attributes) { attributes = 0; return VSConstants.S_OK; }
        public int GetEntryImage(int combo, int index, out int imageIndex) { imageIndex = -1; return VSConstants.S_OK; }
        public int GetEntryImage(int combo, int index, out int imageIndex, out IntPtr images)
        { imageIndex = -1; images = IntPtr.Zero; return VSConstants.S_OK; }
        public int GetComboWidth(int combo, out int width) { width = combo == 0 ? 35 : 65; return VSConstants.S_OK; }
        public int GetAutomationProperties(int combo, out string name, out string automationId)
        {
            name = combo == 0 ? "Shader scopes" : "Shader symbols";
            automationId = combo == 0 ? "ShaderNavigationScopes" : "ShaderNavigationSymbols";
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
            pending?.Cancel();
            Buffer.Changed -= OnBufferChanged;
            Buffer.ContentTypeChanged -= OnContentTypeChanged;
            document.FileActionOccurred -= OnFileAction;
            ShaderLanguageClient.ReadyChanged -= OnReadyChanged;
            try
            {
                if (events != null && eventCookie != 0)
                {
                    events.Unadvise(eventCookie);
                }
            }
            catch (COMException) { /* The code window may already have disconnected its event source. */ }
            events = null;
            eventCookie = 0;
            foreach (IWpfTextView old in views.ToArray())
            {
                RemoveView(old);
            }
            // The native margin can keep its identity when another client takes ownership.
            try
            {
                _=ShaderNavigationBarRegistration.TryRemoveOwned(manager, this, bar);
            }
            catch (COMException) { /* Closing a frame can release the native bar before its managed view. */ }
            bar = null;
            lifetime.Dispose();
            closed(this);
        }
    }
}
