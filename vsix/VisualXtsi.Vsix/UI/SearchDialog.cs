using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>One row of a search dialog. Properties, so that columns can bind and sort.</summary>
    internal sealed class SearchResult
    {
        public string Name { get; set; }
        public string Kind { get; set; } = "";  // "class", "method"; optional
        public string Scope { get; set; } = ""; // enclosing class or namespace; optional
        public string Path { get; set; }
        public int Line { get; set; }           // 1-based; 0 = none
        public int Column { get; set; }         // 1-based; 0 = none
        public ImageMoniker Icon { get; set; }

        /// <summary>The part of the query that matched <see cref="Name"/>, for highlighting.</summary>
        public string Match { get; set; } = "";

        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path);
        public string Location => Line > 0 ? $"{FileName}({Line})" : FileName;
        public string FullLocation => Line > 0 ? $"{Path}({Line})" : Path;

        /// <summary>What a screen reader says for the row: name, kind, scope and where it is.</summary>
        public string AccessibleName =>
            string.Join(", ", new[] { Name, Kind, Scope, Line > 0 ? $"{FileName} line {Line}" : FileName == Name ? null : FileName, Folder }
                .Where(s => !string.IsNullOrEmpty(s)));

        public override string ToString() => AccessibleName;
    }

    /// <summary>A result column: header, the <see cref="SearchResult"/> property shown and sorted by, and default width.</summary>
    internal sealed class SearchColumn
    {
        public SearchColumn(string header, string property, double width, bool isName = false)
        {
            Header = header;
            Property = property;
            Width = width;
            IsName = isName;
        }

        public string Header { get; }
        public string Property { get; }
        public double Width { get; }

        /// <summary>The name column: icon plus name, with the matched characters in bold.</summary>
        public bool IsName { get; }
    }

    /// <summary>
    /// A themed, resizable, keyboard-driven fuzzy search dialog. Type to search (off the UI thread);
    /// Up/Down/PageUp/PageDown choose from the search box; Enter opens the selected result and
    /// closes, Ctrl+Enter opens it and keeps the dialog open; Esc closes. A trailing ":123" in the
    /// query overrides the line to open at. Results can be sorted by any column (header click or
    /// the "Sort by" box). Size, position, column widths and sorting are remembered per dialog.
    /// </summary>
    internal abstract class SearchDialog : DialogWindow
    {
        protected const int MaxResults = 100;
        private const string Relevance = "Relevance";
        private static readonly Regex LineSuffix = new Regex(@"^(.*?):(\d+)$");

        private readonly string _layoutKey;
        private readonly IReadOnlyList<SearchColumn> _columns;
        private readonly Action<SearchResult> _openKeepingDialog;
        private readonly TextBox _query;
        private readonly ListView _results;
        private readonly GridView _grid;
        private readonly TextBlock _status;
        private readonly ComboBox _sortBy;
        private string _sortProperty;
        private ListSortDirection _sortDirection = ListSortDirection.Ascending;
        private int _searchGeneration;

        public SearchResult Selected { get; private set; }

        /// <param name="layoutKey">Names the remembered size and columns ("OpenFile").</param>
        /// <param name="queryLabel">The search box label, with an access key ("_File name").</param>
        /// <param name="openKeepingDialog">Opens a result for Ctrl+Enter; null: Ctrl+Enter is Enter.</param>
        protected SearchDialog(string title, string layoutKey, string queryLabel, string queryHelp, string initialQuery,
                               IReadOnlyList<SearchColumn> columns, Action<SearchResult> openKeepingDialog)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _layoutKey = layoutKey;
            _columns = columns;
            _openKeepingDialog = openKeepingDialog;
            Title = title;
            Width = 860;
            Height = 560;
            MinWidth = 420;
            MinHeight = 260;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            HasMinimizeButton = false;
            HasMaximizeButton = true;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Manual; // resizable: the themed dialog style sizes to content
            AutomationProperties.SetName(this, title);

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DialogTheme.UseListStyles(root);

            // Row 0: label and search box.
            var top = new DockPanel();
            _query = new TextBox { Text = initialQuery ?? "", Padding = new Thickness(4, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center };
            _query.FontSize = FontSize * 1.15;
            AutomationProperties.SetHelpText(_query, queryHelp);
            ToolTipService.SetToolTip(_query, queryHelp);
            var label = new Label { Content = queryLabel, Target = _query, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(_query, label);
            DockPanel.SetDock(label, Dock.Left);
            top.Children.Add(label);
            top.Children.Add(_query);
            _query.TextChanged += (s, e) => _ = RefreshAsync();
            _query.PreviewKeyDown += OnQueryKey;
            root.Children.Add(top);

            // Row 1: results.
            _grid = new GridView { AllowsColumnReorder = false };
            _results = new ListView
            {
                View = _grid,
                Margin = new Thickness(0, 8, 0, 0),
                SelectionMode = SelectionMode.Single,
            };
            // Set explicitly: Visual Studio's themed dialog styles would otherwise replace the
            // template with one that has no column headers.
            _results.Style = root.TryFindResource(typeof(ListView)) as Style;
            AutomationProperties.SetName(_results, "Results");
            AutomationProperties.SetHelpText(_results, "Enter opens the selected result; Ctrl+Enter opens it and keeps this window open.");
            var itemStyle = new Style(typeof(ListViewItem), root.TryFindResource(typeof(ListViewItem)) as Style);
            itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(SearchResult.AccessibleName))));
            itemStyle.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new Binding(nameof(SearchResult.FullLocation))));
            itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(SearchResult.FullLocation))));
            _results.ItemContainerStyle = itemStyle;
            _results.MouseDoubleClick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (ItemsControl.ContainerFromElement(_results, e.OriginalSource as DependencyObject) is ListViewItem)
                    Accept(keepOpen: false);
            };
            _results.PreviewKeyDown += OnResultsKey;
            _results.PreviewTextInput += (s, e) =>
            {
                // Typing in the list goes on with the query.
                _query.Focus();
                _query.CaretIndex = _query.Text.Length;
            };
            Grid.SetRow(_results, 1);
            root.Children.Add(_results);

            // Row 2: status (announced to screen readers) and sorting.
            var statusRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            _sortBy = new ComboBox { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
            _sortBy.Items.Add(Relevance);
            foreach (SearchColumn column in columns)
                _sortBy.Items.Add(column.Header);
            _sortBy.SelectedIndex = 0;
            _sortBy.SelectionChanged += (s, e) => SortBy(_sortBy.SelectedIndex <= 0 ? null : columns[_sortBy.SelectedIndex - 1].Property, keepDirection: true);
            var sortLabel = new Label { Content = "S_ort by", Target = _sortBy, Padding = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            sortLabel.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(_sortBy, sortLabel);
            DockPanel.SetDock(_sortBy, Dock.Right);
            DockPanel.SetDock(sortLabel, Dock.Right);
            statusRow.Children.Add(_sortBy);
            statusRow.Children.Add(sortLabel);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            statusRow.Children.Add(_status);
            Grid.SetRow(statusRow, 2);
            root.Children.Add(statusRow);

            // Row 3: key hint and buttons.
            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var open = new Button { Content = new AccessText { Text = "_Open" }, IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            open.Click += (s, e) => Accept(keepOpen: false);
            AutomationProperties.SetName(open, "Open");
            AutomationProperties.SetHelpText(open, "Opens the selected result and closes this window (Enter).");
            var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            close.Click += (s, e) => Close();
            AutomationProperties.SetHelpText(close, "Closes this window (Esc).");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            buttons.Children.Add(open);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(new TextBlock
            {
                Text = openKeepingDialog != null
                    ? "Enter opens · Ctrl+Enter opens and keeps this window open · Esc closes"
                    : "Enter opens · Esc closes",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            Content = root;
            BuildColumns();
            DialogLayout.Restore(this, _layoutKey);
            Loaded += (s, e) =>
            {
                _query.Focus();
                _query.SelectAll();
                _ = RefreshAsync();
            };
            Closing += (s, e) => SaveLayout();
        }

        /// <summary>The query as typed (with any ":line" suffix), for remembering it.</summary>
        protected string QueryText => _query.Text;

        /// <summary>Runs on a background thread.</summary>
        protected abstract IReadOnlyList<SearchResult> Search(string query);

        /// <summary>Whether an empty query is searched too (otherwise it shows nothing).</summary>
        protected virtual bool SearchWhenEmpty => false;

        /// <summary>Runs on a background thread; the line under the results.</summary>
        protected abstract string Status(string query, int resultCount);

        /// <summary>The part of a query that is matched against names (highlighted in results).</summary>
        protected virtual string NamePart(string query) => query;

        private void BuildColumns()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            double[] widths = (DialogLayout.Get(_layoutKey + ".Columns") ?? "").Split(',')
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= 20 ? w : double.NaN)
                .ToArray();
            for (int i = 0; i < _columns.Count; i++)
            {
                SearchColumn column = _columns[i];
                var header = new GridViewColumnHeader { Content = column.Header, Tag = column };
                header.Click += (s, e) => SortBy(column.Property, keepDirection: false);
                _grid.Columns.Add(new GridViewColumn
                {
                    Header = header,
                    Width = i < widths.Length && !double.IsNaN(widths[i]) ? widths[i] : column.Width,
                    CellTemplate = column.IsName ? NameCell() : TextCell(column.Property),
                });
            }
            string[] sort = (DialogLayout.Get(_layoutKey + ".Sort") ?? "").Split(',');
            if (sort.Length == 2 && _columns.Any(c => c.Property == sort[0]))
            {
                _sortDirection = sort[1] == "D" ? ListSortDirection.Descending : ListSortDirection.Ascending;
                SortBy(sort[0], keepDirection: true);
            }
        }

        private static DataTemplate TextCell(string property)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(property));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            return new DataTemplate { VisualTree = text };
        }

        private static DataTemplate NameCell()
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var icon = new FrameworkElementFactory(typeof(CrispImage));
            icon.SetBinding(CrispImage.MonikerProperty, new Binding(nameof(SearchResult.Icon)));
            icon.SetValue(WidthProperty, 16.0);
            icon.SetValue(HeightProperty, 16.0);
            icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
            panel.AppendChild(icon);
            var name = new FrameworkElementFactory(typeof(HighlightedText));
            name.SetBinding(HighlightedText.SourceProperty, new Binding(nameof(SearchResult.Name)));
            name.SetBinding(HighlightedText.MatchProperty, new Binding(nameof(SearchResult.Match)));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            panel.AppendChild(name);
            return new DataTemplate { VisualTree = panel };
        }

        private void SortBy(string property, bool keepDirection)
        {
            if (!keepDirection)
            {
                // A header click sorts ascending, then descending, then back to relevance.
                if (property != _sortProperty)
                    _sortDirection = ListSortDirection.Ascending;
                else if (_sortDirection == ListSortDirection.Ascending)
                    _sortDirection = ListSortDirection.Descending;
                else
                    property = null;
            }
            _sortProperty = property;
            ApplySort();
            int index = property == null ? 0 : 1 + _columns.ToList().FindIndex(c => c.Property == property);
            if (_sortBy.SelectedIndex != index)
                _sortBy.SelectedIndex = index;
            // The arrow is text, so the sort order does not rely on color or a glyph alone.
            foreach (GridViewColumn c in _grid.Columns)
            {
                var header = (GridViewColumnHeader)c.Header;
                var column = (SearchColumn)header.Tag;
                bool sorted = column.Property == _sortProperty;
                string direction = _sortDirection == ListSortDirection.Ascending ? "ascending" : "descending";
                header.Content = sorted ? $"{column.Header} {(_sortDirection == ListSortDirection.Ascending ? "▲" : "▼")}" : column.Header;
                AutomationProperties.SetName(header, sorted ? $"{column.Header}, sorted {direction}" : column.Header);
            }
        }

        private void ApplySort()
        {
            if (_results.ItemsSource == null)
                return;
            ICollectionView view = CollectionViewSource.GetDefaultView(_results.ItemsSource);
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (_sortProperty != null)
                    view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
            }
        }

        private void SaveLayout()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DialogLayout.Save(this, _layoutKey);
            DialogLayout.Set(_layoutKey + ".Columns", string.Join(",", _grid.Columns.Select(c => Math.Round(c.ActualWidth).ToString(CultureInfo.InvariantCulture))));
            DialogLayout.Set(_layoutKey + ".Sort", _sortProperty == null ? "" : _sortProperty + "," + (_sortDirection == ListSortDirection.Descending ? "D" : "A"));
        }

        private void OnQueryKey(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (e.Key)
            {
                case Key.Down: Move(1); break;
                case Key.Up: Move(-1); break;
                case Key.PageDown: Move(PageSize()); break;
                case Key.PageUp: Move(-PageSize()); break;
                case Key.Enter: Accept(keepOpen: (Keyboard.Modifiers & ModifierKeys.Control) != 0); break;
                case Key.Escape: Close(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void OnResultsKey(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter)
            {
                Accept(keepOpen: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
            }
        }

        private int PageSize()
        {
            var first = _results.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _results.SelectedIndex)) as FrameworkElement;
            double row = first?.ActualHeight > 0 ? first.ActualHeight : 20;
            return Math.Max(1, (int)(_results.ActualHeight / row) - 1);
        }

        private void Move(int delta)
        {
            if (_results.Items.Count == 0)
                return;
            _results.SelectedIndex = Math.Max(0, Math.Min(_results.Items.Count - 1, _results.SelectedIndex + delta));
            _results.ScrollIntoView(_results.SelectedItem);
        }

        private void Accept(bool keepOpen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_results.SelectedItem is SearchResult result))
                return;
            Match m = LineSuffix.Match(_query.Text.Trim());
            if (m.Success)
            {
                result.Line = int.Parse(m.Groups[2].Value);
                result.Column = 0;
            }
            Selected = result;
            OnAccepted();
            if (keepOpen && _openKeepingDialog != null)
            {
                _openKeepingDialog(result);
                Activate();
                _query.Focus();
                SetStatus($"Opened {result.Location}", $"Opened {result.FileName}");
                return;
            }
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
            string namePart = NamePart(query);
            var (found, status) = await Task.Run(() =>
            {
                IReadOnlyList<SearchResult> results = query.Length == 0 && !SearchWhenEmpty ? Array.Empty<SearchResult>() : Search(query);
                return (results, Status(query, results.Count));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (generation != _searchGeneration)
                return; // a newer search is on its way
            foreach (SearchResult result in found)
                result.Match = namePart;
            _results.ItemsSource = found.ToList();
            ApplySort();
            if (_results.Items.Count > 0)
            {
                _results.SelectedIndex = 0;
                _results.ScrollIntoView(_results.SelectedItem);
            }
            string count = found.Count == 0 ? "No results" : found.Count == 1 ? "1 result" : $"{found.Count}{(found.Count == MaxResults ? " or more" : "")} results";
            SetStatus(status, query.Length == 0 && !SearchWhenEmpty ? status : count);
        }

        /// <summary>Shows a status line; screen readers announce <paramref name="spoken"/>.</summary>
        private void SetStatus(string text, string spoken)
        {
            _status.Text = text;
            AutomationProperties.SetName(_status, spoken);
            (UIElementAutomationPeer.FromElement(_status) ?? UIElementAutomationPeer.CreatePeerForElement(_status))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        /// <summary>The icon for a file, by extension.</summary>
        public static ImageMoniker FileIcon(string path)
        {
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".h": case ".hpp": case ".hh": case ".hxx": case ".inl": case ".ipp": return KnownMonikers.CPPHeaderFile;
                case ".cpp": case ".cc": case ".cxx": case ".c": case ".ixx": case ".cppm": return KnownMonikers.CPPSourceFile;
                case ".cs": return KnownMonikers.CSFileNode;
                case ".ini": case ".json": case ".uproject": case ".uplugin": return KnownMonikers.JSONScript;
                default: return KnownMonikers.TextFile;
            }
        }
    }

    /// <summary>
    /// A text block that shows the characters of <see cref="Source"/> matched by the fuzzy query
    /// <see cref="Match"/> in bold (weight, not color, so it reads in every theme).
    /// </summary>
    internal sealed class HighlightedText : TextBlock
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(string), typeof(HighlightedText), new PropertyMetadata("", (d, e) => ((HighlightedText)d).Rebuild()));

        public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(
            nameof(Match), typeof(string), typeof(HighlightedText), new PropertyMetadata("", (d, e) => ((HighlightedText)d).Rebuild()));

        public string Source
        {
            get => (string)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public string Match
        {
            get => (string)GetValue(MatchProperty);
            set => SetValue(MatchProperty, value);
        }

        private void Rebuild()
        {
            string text = Source ?? "";
            bool[] hit = Positions(Match ?? "", text);
            Inlines.Clear();
            int start = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                if (i < text.Length && hit[i] == hit[start])
                    continue;
                var run = new Run(text.Substring(start, i - start));
                if (hit[start])
                    run.FontWeight = FontWeights.Bold;
                Inlines.Add(run);
                start = i;
            }
        }

        /// <summary>
        /// Which characters of <paramref name="text"/> a fuzzy <paramref name="query"/> matches:
        /// each query character takes the next word start that has it (CamelCase, after '_' or a
        /// digit run), unless it simply continues the current run; plain left-to-right if that fails.
        /// </summary>
        public static bool[] Positions(string query, string text)
        {
            var hit = new bool[text.Length];
            query = new string(query.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (query.Length == 0)
                return hit;
            if (Assign(query, text, hit, preferWordStarts: true))
                return hit;
            hit = new bool[text.Length];
            return Assign(query, text, hit, preferWordStarts: false) ? hit : new bool[text.Length];
        }

        private static bool Assign(string query, string text, bool[] hit, bool preferWordStarts)
        {
            int t = 0;
            foreach (char q in query)
            {
                int any = -1, wordStart = -1;
                for (int i = t; i < text.Length; i++)
                {
                    if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(q))
                        continue;
                    if (any < 0)
                        any = i;
                    if (!preferWordStarts || IsWordStart(text, i))
                    {
                        wordStart = i;
                        break;
                    }
                }
                int chosen = any == t && t > 0 && hit[t - 1] ? any : wordStart >= 0 ? wordStart : any;
                if (chosen < 0)
                    return false;
                hit[chosen] = true;
                t = chosen + 1;
            }
            return true;
        }

        private static bool IsWordStart(string text, int i)
        {
            if (i == 0)
                return true;
            char c = text[i], p = text[i - 1];
            return (char.IsUpper(c) && !char.IsUpper(p)) || (char.IsLetterOrDigit(c) && !char.IsLetterOrDigit(p)) ||
                   (char.IsDigit(c) && !char.IsDigit(p));
        }
    }
}
