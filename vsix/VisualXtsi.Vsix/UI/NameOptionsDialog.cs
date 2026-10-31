using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>
    /// A name and a few options (Introduce Variable, Extract Method), with a live preview of what
    /// will be written; OK stays disabled while the name is not usable.
    /// </summary>
    internal sealed class NameOptionsDialog : DialogWindow
    {
        private readonly TextBox _name;
        private readonly List<CheckBox> _options = new List<CheckBox>();

        public string Value => _name.Text.Trim();
        public bool Option(int index) => _options[index].IsEnabled && _options[index].IsChecked == true;

        /// <param name="options">Each option's text (with an access key), whether it starts checked, and whether it can be changed.</param>
        /// <param name="validate">Why a name cannot be used, or null when it can.</param>
        /// <param name="preview">What will be written for a name and the options' states.</param>
        public NameOptionsDialog(string title, string label, string initial, IEnumerable<(string Text, bool Checked, bool Enabled)> options,
                                 Func<string, string> validate, Func<string, bool[], string> preview)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Title = title + " - VisualXtsi";
            Width = 560;
            ResizeMode = ResizeMode.NoResize;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Height;

            var root = new StackPanel { Margin = new Thickness(14) };
            _name = new TextBox { Text = initial ?? "", Padding = new Thickness(4, 3, 4, 3) };
            var caption = new Label { Content = label, Target = _name, Padding = new Thickness(0, 0, 0, 3) };
            caption.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(_name, caption);
            root.Children.Add(caption);
            root.Children.Add(_name);
            foreach (var (text, isChecked, enabled) in options)
            {
                var box = new CheckBox { Content = text, IsChecked = isChecked, IsEnabled = enabled, Margin = new Thickness(0, 8, 0, 0) };
                _options.Add(box);
                root.Children.Add(box);
            }
            var shown = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(4, 3, 4, 3),
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AutomationProperties.SetName(shown, "Preview");
            root.Children.Add(shown);
            var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(problem, "Name problem");
            root.Children.Add(problem);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            void Update()
            {
                string why = validate(Value);
                problem.Text = why ?? "";
                ok.IsEnabled = why == null;
                shown.Text = preview(Value, _options.Select((o, i) => Option(i)).ToArray());
            }
            _name.TextChanged += (s, e) => Update();
            foreach (CheckBox box in _options)
            {
                box.Checked += (s, e) => Update();
                box.Unchecked += (s, e) => Update();
            }
            Update();
            Loaded += (s, e) =>
            {
                _name.Focus();
                _name.SelectAll();
            };
        }
    }
}
