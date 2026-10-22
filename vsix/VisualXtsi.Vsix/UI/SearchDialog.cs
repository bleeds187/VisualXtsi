using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>One row of a search dialog.</summary>
    internal sealed class SearchResult
    {
        public string Name;   // bold
        public string Kind;   // small label before the detail ("class", "method"), optional
        public string Detail; // dimmed
        public string Path;
        public int Line;      // 1-based; 0 = none
        public int Column;    // 1-based; 0 = none
    }

    /// <summary>
    /// A themed, keyboard-driven fuzzy search dialog: type to search (off the UI thread), Up/Down/
    /// PageUp/PageDown to choose, Enter to accept, Esc to close. A trailing ":123" in the query
    /// overrides the line to open at.
    /// </summary>
    internal abstract class SearchDialog : DialogWindow
    {
        protected const int MaxResults = 100;
        private static readonly Regex LineSuffix = new Regex(@"^(.*?):(\d+)$");

        private readonly TextBox _query;
        private readonly ListBox _results;
        private readonly TextBlock _status;
        private int _searchGeneration;

        public SearchResult Selected { get; private set; }

        protected SearchDialog(string title, string initialQuery)
        {
            Title = title;
            Width = 820;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            this.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            this.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new DockPanel { Margin = new Thickness(8) };
            _query = new TextBox { Text = initialQuery ?? "", Padding = new Thickness(4, 3, 4, 3), FontSize = 14 };
            _query.SetResourceReference(BackgroundProperty, EnvironmentColors.SearchBoxBackgroundBrushKey);
            _query.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _query.SetResourceReference(BorderBrushProperty, EnvironmentColors.SearchBoxBorderBrushKey);
            _query.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty,
                                        EnvironmentColors.ToolWindowTextBrushKey);
            _query.TextChanged += (s, e) => _ = RefreshAsync();
            _query.PreviewKeyDown += OnQueryKey;
            DockPanel.SetDock(_query, Dock.Top);
            root.Children.Add(_query);

            _status = new TextBlock { Margin = new Thickness(2, 6, 2, 0), Opacity = 0.7 };
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
                _ = RefreshAsync();
            };
        }

        /// <summary>The query as typed (with any ":line" suffix), for remembering it.</summary>
        protected string QueryText => _query.Text;

        /// <summary>Runs on a background thread.</summary>
        protected abstract IReadOnlyList<SearchResult> Search(string query);

        /// <summary>Whether an empty query is searched too (otherwise it shows nothing).</summary>
        protected virtual bool SearchWhenEmpty => false;

        /// <summary>Runs on a background thread; the line under the results.</summary>
        protected abstract string Status(string query, int resultCount);

        private void OnQueryKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); break;
                case Key.Up: Move(-1); break;
                case Key.PageDown: Move(10); break;
                case Key.PageUp: Move(-10); break;
                case Key.Enter: Accept(); break;
                case Key.Escape: Close(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Move(int delta)
        {
            if (_results.Items.Count == 0)
                return;
            _results.SelectedIndex = Math.Max(0, Math.Min(_results.Items.Count - 1, _results.SelectedIndex + delta));
            _results.ScrollIntoView(_results.SelectedItem);
        }

        private void Accept()
        {
            if (!(_results.SelectedItem is ListBoxItem item) || !(item.Tag is SearchResult result))
                return;
            Match m = LineSuffix.Match(_query.Text.Trim());
            if (m.Success)
            {
                result.Line = int.Parse(m.Groups[2].Value);
                result.Column = 0;
            }
            Selected = result;
            OnAccepted();
            DialogResult = true;
        }

        protected virtual void OnAccepted()
        {
        }

        private async Task RefreshAsync()
        {
            int generation = ++_searchGeneration;
            string query = _query.Text.Trim();
            Match m = LineSuffix.Match(query);
            if (m.Success)
                query = m.Groups[1].Value;
            var (found, status) = await Task.Run(() =>
            {
                IReadOnlyList<SearchResult> results = query.Length == 0 && !SearchWhenEmpty ? Array.Empty<SearchResult>() : Search(query);
                return (results, Status(query, results.Count));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (generation != _searchGeneration)
                return; // a newer search is on its way
            _results.Items.Clear();
            foreach (SearchResult result in found)
                _results.Items.Add(Row(result));
            if (_results.Items.Count > 0)
                _results.SelectedIndex = 0;
            _status.Text = status;
        }

        private static ListBoxItem Row(SearchResult result)
        {
            var row = new DockPanel();
            var name = new TextBlock { Text = result.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            if (!string.IsNullOrEmpty(result.Kind))
            {
                var kind = new TextBlock { Text = result.Kind, Opacity = 0.8, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 10, 0) };
                DockPanel.SetDock(kind, Dock.Left);
                row.Children.Add(kind);
            }
            row.Children.Add(new TextBlock { Text = result.Detail, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
            return new ListBoxItem
            {
                Content = row,
                Tag = result,
                ToolTip = result.Line > 0 ? $"{result.Path}({result.Line})" : result.Path,
                Padding = new Thickness(4, 2, 4, 2),
            };
        }
    }
}
