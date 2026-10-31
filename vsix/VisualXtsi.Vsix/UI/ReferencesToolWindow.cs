using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>A search the References window shows: the engine's (C++) or a finished list (C#).</summary>
    internal interface IReferenceSearch
    {
        string Label { get; }
        string Name { get; } // what the references spell (shown in bold)
        (int Done, int Total, bool Finished) Progress();
        List<Engine.Reference> Results(int from);
        void Cancel();
        void Free();
    }

    internal sealed class EngineReferenceSearch : IReferenceSearch
    {
        private readonly uint _job;

        public EngineReferenceSearch(uint job)
        {
            _job = job;
            var target = Engine.ReferencesTarget(job);
            Label = target.Label;
            Name = target.Name;
        }

        public string Label { get; }
        public string Name { get; }
        public (int Done, int Total, bool Finished) Progress() => Engine.ReferencesProgress(_job);
        public List<Engine.Reference> Results(int from) => Engine.ReferencesResults(_job, from);
        public void Cancel() => Engine.ReferencesCancel(_job);
        public void Free() => Engine.ReferencesFree(_job);
    }

    internal sealed class ListReferenceSearch : IReferenceSearch
    {
        private readonly List<Engine.Reference> _all;

        public ListReferenceSearch(string label, string name, List<Engine.Reference> all)
        {
            Label = label;
            Name = name;
            _all = all;
        }

        public string Label { get; }
        public string Name { get; }
        public (int Done, int Total, bool Finished) Progress() => (1, 1, true);
        public List<Engine.Reference> Results(int from) => _all.Skip(from).ToList();
        public void Cancel() { }
        public void Free() { }
    }

    /// <summary>
    /// VisualXtsi References: the results of Find References, grouped by file (definitions and
    /// declarations first), with unconfirmed matches apart under "Possible matches". Enter or a
    /// double-click opens a reference; results appear as the background search finds them.
    /// </summary>
    [Guid("6f0b1c7e-2d4a-4c55-9e1f-8a3b5d27c914")]
    internal sealed class ReferencesToolWindow : ToolWindowPane
    {
        private readonly ReferencesView _view = new ReferencesView();

        public ReferencesToolWindow() : base(null)
        {
            Caption = "VisualXtsi References";
            Content = _view;
        }

        /// <summary>Shows the window and starts showing a search in it (the previous one is stopped).</summary>
        public static void Show(AsyncPackage package, IReferenceSearch search)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = package.FindToolWindow(typeof(ReferencesToolWindow), 0, true) as ReferencesToolWindow;
            if (window?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
            {
                frame.Show();
                window._view.Start(search);
            }
        }
    }

    internal sealed class ReferencesView : DockPanel
    {
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressBar _progress = new ProgressBar { Width = 120, Height = 10, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _cancel = new Button { Content = "_Cancel", Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0) };
        private readonly List<Engine.Reference> _all = new List<Engine.Reference>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private IReferenceSearch _search;

        public ReferencesView()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            DialogTheme.UseListStyles(this); // the themed tree styles
            _cancel.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var bar = new DockPanel { Margin = new Thickness(6, 4, 6, 4) };
            DockPanel.SetDock(_cancel, Dock.Right);
            DockPanel.SetDock(_progress, Dock.Right);
            bar.Children.Add(_cancel);
            bar.Children.Add(_progress);
            bar.Children.Add(_title);
            bar.Children.Add(_status);
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(_cancel, "Cancel the search");
            _cancel.Click += (s, e) => _search?.Cancel();
            DockPanel.SetDock(bar, Dock.Top);
            Children.Add(bar);
            AutomationProperties.SetName(_tree, "References");
            AutomationProperties.SetHelpText(_tree, "Enter or a double-click opens the reference.");
            _tree.KeyDown += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.Key == Key.Enter && Open())
                    e.Handled = true;
            };
            _tree.MouseDoubleClick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Open())
                    e.Handled = true;
            };
            Children.Add(_tree);
            _timer.Tick += (s, e) => Poll();
        }

        public void Start(IReferenceSearch search)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_search != null)
            {
                IReferenceSearch old = _search;
                _ = System.Threading.Tasks.Task.Run(() => old.Free());
            }
            _search = search;
            _all.Clear();
            _tree.Items.Clear();
            _title.Text = "References to " + search.Label;
            _status.Text = "searching...";
            _cancel.IsEnabled = true;
            _progress.Visibility = Visibility.Visible;
            _timer.Start();
            Poll();
        }

        private void Poll()
        {
            if (_search == null)
                return;
            var (done, total, finished) = _search.Progress();
            List<Engine.Reference> fresh = _search.Results(_all.Count);
            if (fresh.Count > 0)
            {
                _all.AddRange(fresh);
                Rebuild();
            }
            _progress.Maximum = Math.Max(1, total);
            _progress.Value = done;
            int confirmed = _all.Count(r => r.Confirmed), files = _all.Where(r => r.Confirmed).Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int possible = _all.Count - confirmed;
            string counts = $"{confirmed} reference{(confirmed == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}" + (possible > 0 ? $", {possible} possible" : "");
            _status.Text = finished ? counts : $"{counts}; searching {done:N0} of {total:N0} files...";
            if (finished)
            {
                _timer.Stop();
                _cancel.IsEnabled = false;
                _progress.Visibility = Visibility.Collapsed;
            }
        }

        // Files in order: the ones with definitions or declarations first, then by path.
        private void Rebuild()
        {
            string selected = (_tree.SelectedItem as TreeViewItem)?.Tag is Engine.Reference r ? $"{r.Path}|{r.Line}|{r.Column}" : null;
            _tree.Items.Clear();
            AddGroups(_tree.Items, _all.Where(x => x.Confirmed), selected, _search.Name);
            var possible = _all.Where(x => !x.Confirmed).ToList();
            if (possible.Count > 0)
            {
                var node = (new TreeViewItem { Header = $"Possible matches ({possible.Count}): the name, where the parser could not confirm it", IsExpanded = true });
                AutomationProperties.SetName(node, $"Possible matches, {possible.Count}");
                AddGroups(node.Items, possible, selected, _search.Name);
                _tree.Items.Add(node);
            }
        }

        private static void AddGroups(ItemCollection into, IEnumerable<Engine.Reference> refs, string selected, string name)
        {
            foreach (var file in refs.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(g => g.Any(x => x.Kind == "definition" || x.Kind == "declaration") ? 0 : 1)
                                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var fileNode = (new TreeViewItem { Header = $"{Path.GetFileName(file.Key)}  ({file.Count()})  {Path.GetDirectoryName(file.Key)}", IsExpanded = true });
                AutomationProperties.SetName(fileNode, $"{Path.GetFileName(file.Key)}, {file.Count()} reference{(file.Count() == 1 ? "" : "s")}");
                foreach (Engine.Reference r in file.OrderBy(x => x.Line).ThenBy(x => x.Column))
                {
                    string marker = r.Kind == "definition" ? "[definition] " : r.Kind == "declaration" ? "[declaration] " : r.Kind == "write" ? "[write] " : "";
                    var item = new TreeViewItem { Header = Line(r, marker, name), Tag = r };
                    AutomationProperties.SetName(item, $"line {r.Line}, {(marker.Length > 0 ? r.Kind + ", " : "")}{r.Text}");
                    if ($"{r.Path}|{r.Line}|{r.Column}" == selected)
                        item.IsSelected = true;
                    fileNode.Items.Add(item);
                }
                into.Add(fileNode);
            }
        }

        // "123: [write] " dimmer, then the line with the name in bold. Colors come from the item (the theme).
        private static StackPanel Line(Engine.Reference r, string marker, string name)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = $"{r.Line}: {marker}", Opacity = 0.72 });
            var text = new TextBlock();
            line.Children.Add(text);
            int at = string.IsNullOrEmpty(name) ? -1 : WordIndex(r.Text, name);
            if (at < 0)
            {
                text.Inlines.Add(new System.Windows.Documents.Run(r.Text));
                return line;
            }
            text.Inlines.Add(new System.Windows.Documents.Run(r.Text.Substring(0, at)));
            text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.Bold });
            text.Inlines.Add(new System.Windows.Documents.Run(r.Text.Substring(at + name.Length)));
            return line;
        }

        private static int WordIndex(string text, string word)
        {
            for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                bool before = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_');
                int end = at + word.Length;
                bool after = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
                if (before && after)
                    return at;
            }
            return -1;
        }

        private bool Open()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!((_tree.SelectedItem as TreeViewItem)?.Tag is Engine.Reference r))
                return false;
            EditorCommands.OpenDocument(r.Path, r.Line, r.Column);
            return true;
        }
    }
}
