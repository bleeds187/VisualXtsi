using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>One change shown in <see cref="RefactorPreviewDialog"/>.</summary>
    internal sealed class PreviewItem
    {
        public string Group;       // usually a file path
        public string Label;       // "line 12: rename to X"
        public string Diff;        // "- old" / "+ new" / "  context" lines
        public bool Checked = true;
        public object Tag;         // what to apply
    }

    /// <summary>
    /// Shows the changes a refactoring is about to make, grouped by file, each with a check box (Space
    /// toggles; a file's box toggles all of its changes), and the selected change or file as a diff.
    /// Apply returns the checked changes. Keyboard-first and screen-reader friendly like the other dialogs.
    /// </summary>
    internal sealed class RefactorPreviewDialog : DialogWindow
    {
        private readonly List<(PreviewItem Item, CheckBox Box)> _items = new List<(PreviewItem, CheckBox)>();
        private readonly TreeView _tree;
        private readonly TextBox _diff;

        public IReadOnlyList<PreviewItem> Accepted { get; private set; } = Array.Empty<PreviewItem>();

#pragma warning disable VSTHRD010 // dialogs are created on the UI thread
        public RefactorPreviewDialog(string title, string intro, IReadOnlyList<PreviewItem> items, string applyText = "_Apply")
        {
            Title = title + " - VisualXtsi";
            Width = 900;
            Height = 620;
            MinWidth = 480;
            MinHeight = 320;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            HasMinimizeButton = false;
            HasMaximizeButton = true;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Manual;
            AutomationProperties.SetName(this, Title);

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DialogTheme.UseListStyles(root);

            var introText = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            introText.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            root.Children.Add(introText);

            _tree = new TreeView();
            _tree.Style = root.TryFindResource(typeof(TreeView)) as Style;
            AutomationProperties.SetName(_tree, "Changes");
            AutomationProperties.SetHelpText(_tree, "Space includes or excludes the selected change; a file's box covers all of its changes.");
            foreach (var group in items.GroupBy(i => i.Group))
            {
                var fileBox = new CheckBox { Content = new TextBlock { Text = group.Key }, IsThreeState = true, Focusable = false };
                var fileNode = new TreeViewItem { Header = fileBox, IsExpanded = true, Tag = group.Key };
                AutomationProperties.SetName(fileNode, group.Key);
                var boxes = new List<CheckBox>();
                foreach (PreviewItem item in group)
                {
                    var box = new CheckBox { Content = new TextBlock { Text = item.Label }, IsChecked = item.Checked, Focusable = false };
                    var node = new TreeViewItem { Header = box, Tag = item };
                    AutomationProperties.SetName(node, item.Label);
                    box.Checked += (s, e) => Sync(fileBox, boxes);
                    box.Unchecked += (s, e) => Sync(fileBox, boxes);
                    boxes.Add(box);
                    _items.Add((item, box));
                    fileNode.Items.Add(node);
                }
                fileBox.Click += (s, e) =>
                {
                    bool on = boxes.Any(b => b.IsChecked != true);
                    foreach (CheckBox b in boxes)
                        b.IsChecked = on;
                    Sync(fileBox, boxes);
                };
                Sync(fileBox, boxes);
                _tree.Items.Add(fileNode);
            }
            _tree.SelectedItemChanged += (s, e) => ShowDiff();
            _tree.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Space && _tree.SelectedItem is TreeViewItem node && node.Header is CheckBox box)
                {
                    if (node.Tag is PreviewItem)
                        box.IsChecked = box.IsChecked != true;
                    else
                        box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    e.Handled = true;
                }
            };
            Grid.SetRow(_tree, 1);
            root.Children.Add(_tree);

            _diff = new TextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
            };
            AutomationProperties.SetName(_diff, "Preview of the selected change");
            Grid.SetRow(_diff, 3);
            root.Children.Add(_diff);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var apply = new Button { Content = new AccessText { Text = applyText }, IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            apply.Click += (s, e) =>
            {
                Accepted = _items.Where(x => x.Box.IsChecked == true).Select(x => x.Item).ToList();
                DialogResult = true;
            };
            AutomationProperties.SetName(apply, applyText.Replace("_", ""));
            AutomationProperties.SetHelpText(apply, "Makes the checked changes, as one undo step (Enter).");
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
            DialogLayout.Restore(this, "RefactorPreview");
            Closing += (s, e) => DialogLayout.Save(this, "RefactorPreview");
            Loaded += (s, e) =>
            {
                if (_tree.Items.Count > 0 && _tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                {
                    first.IsSelected = true;
                    first.Focus();
                }
            };
        }
#pragma warning restore VSTHRD010

        private static void Sync(CheckBox fileBox, List<CheckBox> boxes)
        {
            int on = boxes.Count(b => b.IsChecked == true);
            fileBox.IsChecked = on == boxes.Count ? true : on == 0 ? (bool?)false : null;
        }

        private void ShowDiff()
        {
            if (!(_tree.SelectedItem is TreeViewItem node))
                return;
            if (node.Tag is PreviewItem item)
            {
                _diff.Text = item.Diff ?? "";
                return;
            }
            var sb = new StringBuilder();
            foreach (var (i, _) in _items.Where(x => x.Item.Group == node.Tag as string))
                sb.Append("@@ ").Append(i.Label).Append('\n').Append(i.Diff).Append('\n');
            _diff.Text = sb.ToString();
        }

        /// <summary>
        /// A diff of replacing <paramref name="length"/> characters at <paramref name="start"/> of
        /// <paramref name="text"/> with <paramref name="replacement"/>: the changed lines as "- " and
        /// "+ " lines, with up to <paramref name="context"/> unchanged lines around them.
        /// </summary>
        public static string Diff(string text, int start, int length, string replacement, int context = 2)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            if (start == 0)
                lineStart = 0;
            int end = start + length;
            int lineEnd = text.IndexOf('\n', end);
            if (lineEnd < 0)
                lineEnd = text.Length;
            string before = text.Substring(lineStart, lineEnd - lineStart);
            string after = text.Substring(lineStart, start - lineStart) + replacement + text.Substring(end, lineEnd - end);
            var sb = new StringBuilder();
            string[] all = text.Replace("\r", "").Split('\n');
            int firstLine = text.Substring(0, lineStart).Count(c => c == '\n');
            for (int i = Math.Max(0, firstLine - context); i < firstLine; i++)
                sb.Append("  ").Append(all[i]).Append('\n');
            foreach (string l in before.Replace("\r", "").Split('\n'))
                sb.Append("- ").Append(l).Append('\n');
            foreach (string l in after.Replace("\r", "").Split('\n'))
                sb.Append("+ ").Append(l).Append('\n');
            int lastLine = firstLine + before.Count(c => c == '\n');
            for (int i = lastLine + 1; i <= Math.Min(all.Length - 1, lastLine + context); i++)
                sb.Append("  ").Append(all[i]).Append('\n');
            return sb.ToString();
        }
    }
}
