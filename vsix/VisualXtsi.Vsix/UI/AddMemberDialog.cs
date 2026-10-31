using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Refactoring;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Add Member / Add Similar Member: the declaration (a method or a field), an optional reflection
    /// macro, the access section, and whether a method gets an implementation in the source file.
    /// </summary>
    internal sealed class AddMemberDialog : DialogWindow
    {
        private readonly TextBox _declaration, _macro;
        private readonly ComboBox _access;
        private readonly CheckBox _implement;

        public string Declaration => _declaration.Text.Trim().TrimEnd(';').Trim();
        public string Macro => _macro.Text.Trim();
        public string Access => (string)((ComboBoxItem)_access.SelectedItem).Tag;
        public bool Implement => _implement.IsEnabled && _implement.IsChecked == true;

        /// <param name="sourceFile">The file a method's implementation would go to (null: none can be created).</param>
        public AddMemberDialog(string title, string className, string declaration, string macro, string access, string sourceFile, bool selectName)
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
            _declaration = new TextBox { Text = declaration ?? "", Padding = new Thickness(4, 3, 4, 3) };
            root.Children.Add(Caption($"_Declaration in {className}:", _declaration));
            root.Children.Add(_declaration);
            var hint = new TextBlock
            {
                Text = "A method (\"int32 GetCount() const\") or a field (\"float Speed = 1.f\").",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 3, 0, 8),
            };
            root.Children.Add(hint);

            _macro = new TextBox { Text = macro ?? "", Padding = new Thickness(4, 3, 4, 3) };
            root.Children.Add(Caption("_Reflection macro (optional, e.g. UFUNCTION(BlueprintCallable)):", _macro));
            root.Children.Add(_macro);

            _access = new ComboBox { Margin = new Thickness(0, 0, 0, 8), MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (string a in new[] { "public", "protected", "private" })
                _access.Items.Add(new ComboBoxItem { Content = a, Tag = a, IsSelected = a == (access ?? "public") });
            var accessCaption = Caption("_Access:", _access);
            accessCaption.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(accessCaption);
            root.Children.Add(_access);

            _implement = new CheckBox
            {
                Content = sourceFile == null ? "Create an _implementation (no source file found)" : $"Create an _implementation in {System.IO.Path.GetFileName(sourceFile)}",
                IsChecked = sourceFile != null,
                Margin = new Thickness(0, 4, 0, 0),
            };
            root.Children.Add(_implement);

            var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(problem, "Declaration problem");
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
                string why = MemberSupport.Validate(Declaration);
                problem.Text = why ?? "";
                ok.IsEnabled = why == null;
                // Only methods that are not pure, defaulted, inline or generated get a body in the source file.
                _implement.IsEnabled = sourceFile != null && why == null && MemberSupport.MethodInfo(Declaration, className, "", Macro) != null;
            }
            _declaration.TextChanged += (s, e) => Check();
            _macro.TextChanged += (s, e) => Check();
            Check();
            Loaded += (s, e) =>
            {
                _declaration.Focus();
                var (start, length) = MemberSupport.NameIn(_declaration.Text);
                if (selectName && length > 0)
                    _declaration.Select(start, length);
                else
                    _declaration.SelectAll();
            };
        }

        private static Label Caption(string text, Control target)
        {
            var caption = new Label { Content = text, Target = target, Padding = new Thickness(0, 0, 0, 3) };
            caption.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(target, caption);
            return caption;
        }
    }
}
