using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Options;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Captures a new shortcut for a command: press the keys in the box (Tab and Shift+Tab still
    /// move on, Esc cancels), pick where it applies, and see which commands already use it.
    /// </summary>
    internal sealed class KeyCaptureDialog : DialogWindow
    {
        private readonly TextBox _keys;
        private readonly ComboBox _scope;
        private readonly TextBlock _conflicts;
        private readonly Button _ok;
        private readonly string _command;
        private int _conflictCheck;

        /// <summary>"Text Editor::Ctrl+Alt+K" once OK was pressed.</summary>
        public string Binding { get; private set; }

        public KeyCaptureDialog(string command, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _command = command;
            Title = $"Shortcut for {label}";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Height;

            var root = new StackPanel { Margin = new Thickness(14) };
            _keys = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, Padding = new Thickness(4, 3, 4, 3) };
            AutomationProperties.SetHelpText(_keys, "Press the new shortcut while this box has focus, for example Ctrl+Alt+K. Tab moves on; Esc cancels.");
            _keys.PreviewKeyDown += OnKey;
            root.Children.Add(LabelFor("_Press shortcut keys:", _keys));
            root.Children.Add(_keys);

            _scope = new ComboBox { Margin = new Thickness(0, 0, 0, 0) };
            _scope.Items.Add("Text Editor");
            _scope.Items.Add("Global");
            _scope.SelectedIndex = 0;
            AutomationProperties.SetHelpText(_scope, "Text Editor: only while an editor has focus (outranks global shortcuts there). Global: everywhere.");
            _scope.SelectionChanged += (s, e) => _ = CheckConflictsAsync();
            var scopeLabel = LabelFor("_Use new shortcut in:", _scope);
            scopeLabel.Margin = new Thickness(0, 10, 0, 2);
            root.Children.Add(scopeLabel);
            root.Children.Add(_scope);

            _conflicts = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Text = "No shortcut yet." };
            AutomationProperties.SetLiveSetting(_conflicts, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(_conflicts, "Shortcut currently used by");
            root.Children.Add(_conflicts);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _ok = new Button { Content = "_Assign", IsDefault = true, IsEnabled = false, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            _ok.Content = new AccessText { Text = "_Assign" };
            AutomationProperties.SetName(_ok, "Assign");
            _ok.Click += (s, e) =>
            {
                Binding = $"{_scope.SelectedItem}::{_keys.Text}";
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => _keys.Focus();
        }

        private Label LabelFor(string text, Control target)
        {
            var label = new Label { Content = text, Target = target, Padding = new Thickness(0, 0, 0, 2) };
            label.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(target, label);
            return label;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys modifiers = Keyboard.Modifiers;
            // Tab / Shift+Tab keep moving focus, Enter assigns and Esc cancels, so the dialog stays keyboard-operable.
            if ((key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == 0) ||
                ((key == Key.Escape || key == Key.Enter) && modifiers == ModifierKeys.None))
                return;
            e.Handled = true;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
                return;
            string text = KeyBindings.Format(key, modifiers);
            if (text == null)
            {
                _conflicts.Text = "That key cannot be used in a shortcut.";
                return;
            }
            _keys.Text = text;
            _ok.IsEnabled = true;
            _ = CheckConflictsAsync();
        }

        // Walking every command takes a moment, so it runs after the key shows.
        private async System.Threading.Tasks.Task CheckConflictsAsync()
        {
            if (string.IsNullOrEmpty(_keys.Text))
                return;
            int check = ++_conflictCheck;
            _conflicts.Text = "Checking which commands use it...";
            string binding = $"{_scope.SelectedItem}::{_keys.Text}";
            await System.Threading.Tasks.Task.Delay(50); // let the key and the message show first
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (check != _conflictCheck)
                return;
            List<string> used = KeyBindings.Conflicts(binding, _command);
            _conflicts.Text = used.Count == 0
                ? $"{_keys.Text} is not used by any other command."
                : $"{_keys.Text} is also used by: {string.Join(", ", used)}. Text Editor shortcuts win over Global ones in an editor.";
        }
    }
}
