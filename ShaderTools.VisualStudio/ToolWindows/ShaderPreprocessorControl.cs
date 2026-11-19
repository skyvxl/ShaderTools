using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using ShaderTools.VisualStudio.Commands;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShaderTools.VisualStudio.ToolWindows
{
    internal sealed class ShaderPreprocessorControl : UserControl
    {
        private readonly TreeView tree = new TreeView { Margin = new Thickness(8) };
        private readonly TextBlock status = new TextBlock { Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap, Text = "Inspect includes and settings for the active shader." };
        private readonly TextBox detail = new TextBox { Margin = new Thickness(8), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private CancellationTokenSource refresh;

        public ShaderPreprocessorControl()
        {
            DockPanel panel = new DockPanel();
            StackPanel toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            Button reload = new Button { Content = "Inspect active shader", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2) };
            reload.Click += (_, __) => BackgroundWork.Run(RefreshAsync, "ShaderTools/Preprocessor");
            _=toolbar.Children.Add(reload);
            Button settings = new Button { Content = "Project settings", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2) };
            settings.Click += (_, __) => ShaderCommands.Run(() => ShaderCommands.EditProjectFileAsync(false));
            _=toolbar.Children.Add(settings);
            DockPanel.SetDock(toolbar, Dock.Top); _=panel.Children.Add(toolbar);
            DockPanel.SetDock(status, Dock.Top); _=panel.Children.Add(status);
            DockPanel.SetDock(detail, Dock.Bottom); _=panel.Children.Add(detail);
            _=panel.Children.Add(tree); Content = panel;
            tree.SelectedItemChanged += (_, __) => ShowDetails();
            tree.MouseDoubleClick += (_, __) => Navigate();
            tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(); e.Handled = true; } };
            Unloaded += (_, __) => refresh?.Cancel();
        }

        private async Task RefreshAsync()
        {
            refresh?.Cancel();
            using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            refresh = cancellation;
            try
            {
                string file = await WorkspaceContext.GetActiveFileAsync();
                if (ShaderContentTypes.ForPath(file) == null)
                {
                    throw new InvalidOperationException("Select a GLSL or HLSL shader first.");
                }

                ShaderLanguageClient client = ShaderCommands.Client();
                await client.EnsureDocumentAsync(file, cancellation.Token);
                string uri = new Uri(file).AbsoluteUri;
                status.Text = "Reading " + Path.GetFileName(file) + "…";
                JObject document = new JObject { ["uri"] = uri };
                JToken context = await client.RequestAsync("shaderTools/compilationContext", new JObject { ["textDocument"] = document }, cancellation.Token);
                JToken report = await client.RequestAsync("shaderTools/preprocessor", new JObject { ["textDocument"] = document.DeepClone(), ["context"] = context }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!ReferenceEquals(refresh, cancellation))
                {
                    return;
                }

                tree.Items.Clear();
                if (report["dependencyTree"]?["includes"] is JArray dependencies && dependencies.Count > 0)
                {
                    TreeViewItem analyzed = new TreeViewItem { Header = "Active include files", IsExpanded = true };
                    AddDependencies(analyzed, dependencies);
                    _=tree.Items.Add(analyzed);
                }
                TreeViewItem includes = new TreeViewItem { Header = "Include directives", IsExpanded = true };
                foreach (JToken edge in report["includes"] as JArray ?? [])
                {
                    string conditional = (bool?)edge["conditional"] == true ? " · conditional" : "";
                    string unsaved = (bool?)edge["unsaved"] == true ? " · open buffer" : "";
                    _=includes.Items.Add(new TreeViewItem { Header = Path.GetFileName((string)edge["source"]) + ":" + ((int)edge["line"] + 1) + "  " + (string)edge["path"] + "  [" + (string)edge["status"] + conditional + unsaved + "]", Tag = edge, ToolTip = (string)edge["target"] ?? (string)edge["source"] });
                }
                _=tree.Items.Add(includes);
                TreeViewItem defines = new TreeViewItem { Header = "Defines", IsExpanded = false };
                foreach (JToken define in report["defines"] as JArray ?? [])
                {
                    _=defines.Items.Add(new TreeViewItem { Header = (string)define["name"] + " = " + (string)define["value"] + ((string)define["source"] == "Configuration" ? "  [configuration]" : "  [source declaration]"), Tag = define });
                }

                _=tree.Items.Add(defines);
                TreeViewItem configuration = new TreeViewItem { Header = "Compilation settings", IsExpanded = false };
                foreach (JProperty setting in (report["settings"] as JObject ?? []).Properties())
                {
                    _=configuration.Items.Add(new TreeViewItem { Header = setting.Name, Tag = setting.Value });
                }

                _=tree.Items.Add(configuration);
                status.Text = Path.GetFileName(file) + " · " + (int?)report["filesVisited"] + " files" + ((bool?)report["truncated"] == true ? " · inspection limit reached" : "");
                detail.Text = (string)report["note"];
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ReferenceEquals(refresh, cancellation))
                {
                    status.Text = ex.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(refresh, cancellation))
                {
                    refresh = null;
                }
            }
        }

        private static void AddDependencies(TreeViewItem parent, JArray dependencies)
        {
            foreach (JObject node in dependencies.OfType<JObject>())
            {
                if (!Uri.TryCreate((string)node["url"], UriKind.Absolute, out Uri uri) || !uri.IsFile) { continue; }
                TreeViewItem item = new TreeViewItem
                {
                    Header = Path.GetFileName(uri.LocalPath), ToolTip = uri.LocalPath,
                    Tag = new JObject { ["target"] = uri.LocalPath }
                };
                _=parent.Items.Add(item);
                if (node["includes"] is JArray children && children.Count > 0)
                {
                    item.Items.Add(new TreeViewItem());
                    void Expand(object sender, RoutedEventArgs e)
                    {
                        if (!ReferenceEquals(e.OriginalSource, item)) { return; }
                        item.Expanded -= Expand;
                        item.Items.Clear();
                        AddDependencies(item, children);
                    }
                    item.Expanded += Expand;
                }
            }
        }

        private void ShowDetails()
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is JToken item)
            {
                if (item is JObject edge && edge["path"] != null)
                {
                    detail.Text = (string)edge["source"] + Environment.NewLine + ((string)edge["target"] ?? "Not resolved") + Environment.NewLine + "Resolution: " + ((string)edge["resolution"] ?? (string)edge["status"]) + Environment.NewLine + "Searched:" + Environment.NewLine + string.Join(Environment.NewLine, (edge["candidates"] as JArray ?? []).Values<string>());
                }
                else
                {
                    detail.Text = item is JObject target && target["target"] != null ? (string)target["target"] : item.ToString();
                }
            }
        }
        private void Navigate()
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is not JObject item)
            {
                return;
            }

            string file = (string)item["target"] ?? (string)item["source"];
            if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
            {
                return;
            }

            int line = item["target"] != null ? 0 : (int?)item["line"] ?? 0;
            BackgroundWork.Run(() => WorkspaceContext.OpenFileAsync(file, line, 0), "ShaderTools/IncludeNavigation");
        }
    }
}
