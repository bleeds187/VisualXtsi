using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.UI
{
    /// <summary>Asks for a name (a new theme, a rename); OK stays disabled while the name is not usable.</summary>
    internal sealed class NameDialog : DialogWindow
    {
        private readonly TextBox _name;

        public string Value => _name.Text.Trim();

        /// <param name="validate">Why a name cannot be used, or null when it can.</param>
        public NameDialog(string title, string label, string initial, Func<string, string> validate)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Title = title;
            Width = 420;
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
            var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(problem, "Name problem");
            root.Children.Add(caption);
            root.Children.Add(_name);
            root.Children.Add(problem);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            void Check()
            {
                string why = validate(Value);
                problem.Text = why ?? "";
                ok.IsEnabled = why == null;
            }
            _name.TextChanged += (s, e) => Check();
            Check();
            Loaded += (s, e) =>
            {
                _name.Focus();
                _name.SelectAll();
            };
        }
    }
}
