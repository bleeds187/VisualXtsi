using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Refactoring;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Create File: the class's name and kind, the folders its header and source go to (prefilled from
    /// the module's Public/Private layout), whether it gets a source file; the paths are shown.
    /// </summary>
    internal sealed class CreateFileDialog : DialogWindow
    {
        private readonly TextBox _name, _headerDir, _sourceDir;
        private readonly ComboBox _kind;
        private readonly CheckBox _source;

        public string ClassName => FileSupport.ClassName(_name.Text.Trim(), Prefix);
        public string Kind => (string)((ComboBoxItem)_kind.SelectedItem).Tag;
        public string HeaderPath => Path.Combine(_headerDir.Text.Trim(), FileSupport.BaseName(ClassName) + ".h");
        public string SourcePath => _source.IsChecked == true ? Path.Combine(_sourceDir.Text.Trim(), FileSupport.BaseName(ClassName) + ".cpp") : null;
        private string Prefix => FileSupport.Templates[_kind.SelectedIndex].Prefix;

        public CreateFileDialog(string moduleName, string headerDir, string sourceDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Title = "Create File - VisualXtsi";
            Width = 640;
            ResizeMode = ResizeMode.NoResize;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Height;

            var root = new StackPanel { Margin = new Thickness(14) };
            if (moduleName != null)
                root.Children.Add(new TextBlock { Text = $"A new class in the {moduleName} module.", Margin = new Thickness(0, 0, 0, 8) });
            _name = Field(root, "Class _name:", "MyClass");
            _kind = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (kind, _) in FileSupport.Templates)
                _kind.Items.Add(new ComboBoxItem { Content = kind, Tag = kind });
            _kind.SelectedIndex = moduleName != null ? 1 : 0;
            root.Children.Add(Caption("_Kind:", _kind));
            root.Children.Add(_kind);
            _headerDir = Field(root, "_Header folder:", headerDir);
            _sourceDir = Field(root, "_Source folder:", sourceDir);
            _source = new CheckBox { Content = "Create a source (.cpp) _file", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(_source);
            var paths = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(4, 3, 4, 3) };
            AutomationProperties.SetName(paths, "Files to create");
            root.Children.Add(paths);
            var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(problem, "Problem");
            root.Children.Add(problem);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            void Update()
            {
                string why = FileSupport.ValidateClassName(_name.Text.Trim());
                if (why == null && (_headerDir.Text.Trim().Length == 0 || _source.IsChecked == true && _sourceDir.Text.Trim().Length == 0))
                    why = "Choose the folders.";
                if (why == null && (File.Exists(HeaderPath) || SourcePath != null && File.Exists(SourcePath)))
                    why = "A file with that name is there already.";
                paths.Text = why == null || why.StartsWith("A file") ? ClassName + ":\n" + HeaderPath + (SourcePath != null ? "\n" + SourcePath : "") : "";
                problem.Text = why ?? "";
                ok.IsEnabled = why == null;
                _sourceDir.IsEnabled = _source.IsChecked == true;
            }
            _name.TextChanged += (s, e) => Update();
            _headerDir.TextChanged += (s, e) => Update();
            _sourceDir.TextChanged += (s, e) => Update();
            _kind.SelectionChanged += (s, e) => Update();
            _source.Checked += (s, e) => Update();
            _source.Unchecked += (s, e) => Update();
            Update();
            Loaded += (s, e) =>
            {
                _name.Focus();
                _name.SelectAll();
            };
        }

        private TextBox Field(Panel root, string label, string text)
        {
            var box = new TextBox { Text = text ?? "", Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(Caption(label, box));
            root.Children.Add(box);
            return box;
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
