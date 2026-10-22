using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Open File in Solution: type part of a name (fuzzy; word starts count), Up/Down to choose,
    /// Enter to open, Esc to close. "name:123" opens at line 123; a "/" or "\" searches whole paths.
    /// </summary>
    internal sealed class OpenFileDialog : DialogWindow
    {
        private const int MaxResults = 100;
        private static readonly Regex LineSuffix = new Regex(@"^(.*?):(\d+)$");
        private static string _lastQuery = "";

        private readonly TextBox _query;
        private readonly ListBox _results;
        private readonly TextBlock _status;
        private int _searchGeneration;

        public string SelectedPath { get; private set; }
        public int SelectedLine { get; private set; }

        public OpenFileDialog()
        {
            Title = "Open File in Solution (VisualXtsi)";
            Width = 760;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            this.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            this.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new DockPanel { Margin = new Thickness(8) };
            _query = new TextBox { Text = _lastQuery, Padding = new Thickness(4, 3, 4, 3), FontSize = 14 };
            _query.SetResourceReference(BackgroundProperty, EnvironmentColors.SearchBoxBackgroundBrushKey);
            _query.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _query.SetResourceReference(BorderBrushProperty, EnvironmentColors.SearchBoxBorderBrushKey);
            _query.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty,
                                        EnvironmentColors.ToolWindowTextBrushKey);
            _query.TextChanged += (s, e) => _ = SearchAsync();
            _query.PreviewKeyDown += OnQueryKey;
            DockPanel.SetDock(_query, Dock.Top);
            root.Children.Add(_query);

            _status = new TextBlock { Margin = new Thickness(2, 6, 2, 0) };
            _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _status.Opacity = 0.7;
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _results = new ListBox { Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0) };
            _results.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _results.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _results.MouseDoubleClick += (s, e) => Accept();
            root.Children.Add(_results);

            Content = root;
            Loaded += (s, e) =>
            {
                _query.Focus();
                _query.SelectAll();
                _ = SearchAsync();
            };
        }

        private void OnQueryKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    Move(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    Move(10);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    Move(-10);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Accept();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
            }
        }

        private void Move(int delta)
        {
            if (_results.Items.Count == 0)
                return;
            int index = Math.Max(0, Math.Min(_results.Items.Count - 1, _results.SelectedIndex + delta));
            _results.SelectedIndex = index;
            _results.ScrollIntoView(_results.SelectedItem);
        }

        private void Accept()
        {
            if (!(_results.SelectedItem is ListBoxItem item))
                return;
            SelectedPath = (string)item.Tag;
            Match m = LineSuffix.Match(_query.Text.Trim());
            SelectedLine = m.Success ? int.Parse(m.Groups[2].Value) : 0;
            _lastQuery = _query.Text;
            DialogResult = true;
        }

        private async Task SearchAsync()
        {
            int generation = ++_searchGeneration;
            string query = _query.Text.Trim();
            Match m = LineSuffix.Match(query);
            if (m.Success)
                query = m.Groups[1].Value;
            string[] found = query.Length == 0
                ? Array.Empty<string>()
                : await Task.Run(() => Engine.FindFiles(query, MaxResults));
            int indexed = await Task.Run(() => Engine.IndexFileCount());
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (generation != _searchGeneration)
                return; // a newer search is on its way

            _results.Items.Clear();
            foreach (string path in found)
                _results.Items.Add(ResultItem(path));
            if (_results.Items.Count > 0)
                _results.SelectedIndex = 0;
            _status.Text = query.Length == 0
                ? $"{indexed:N0} files indexed. Type part of a name; word starts count (\"lhc\" finds LyraHeroComponent)."
                : $"{found.Length}{(found.Length == MaxResults ? "+" : "")} matches in {indexed:N0} files";
        }

        private static ListBoxItem ResultItem(string path)
        {
            var name = new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
            var folder = new TextBlock { Text = Path.GetDirectoryName(path), Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel();
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(folder);
            return new ListBoxItem { Content = row, Tag = path, ToolTip = path, Padding = new Thickness(4, 2, 4, 2) };
        }
    }
}
