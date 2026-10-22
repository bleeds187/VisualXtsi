using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Interop;

namespace VisualXtsi.Options
{
    /// <summary>
    /// Tools > Options > VisualXtsi > General: the switches Fonts and Colors cannot express.
    /// The page edits a working copy of <see cref="VxSettings"/>; Apply/OK saves it. The public
    /// properties also serve DTE automation (Properties("VisualXtsi", "General")), where each
    /// assignment is saved right away.
    /// </summary>
    [Guid("5e7b1f3c-2a9d-4c84-9f61-0d3b8e2a7c45")]
    [ComVisible(true)] // the page is its own automation object
    [DesignerCategory("code")]
    public sealed class OptionsPage : UIElementDialogPage
    {
        private VxSettings _working = new VxSettings();
        private bool _shownInDialog;
        private StackPanel _root;
        private readonly Dictionary<string, CheckBox> _colorBoxes = new Dictionary<string, CheckBox>();
        private CheckBox _tooltip, _skipBodies, _sharedPch;
        private TextBox _cacheMb;
        private TextBlock _clearStatus;

        public bool MacroTooltip
        {
            get => _working.MacroTooltip;
            set { ThreadHelper.ThrowIfNotOnUIThread(); _working.MacroTooltip = value; SaveIfAutomated(); }
        }

        public bool SkipHeaderBodies
        {
            get => _working.SkipHeaderBodies;
            set { ThreadHelper.ThrowIfNotOnUIThread(); _working.SkipHeaderBodies = value; SaveIfAutomated(); }
        }

        public bool SharedPch
        {
            get => _working.SharedPch;
            set { ThreadHelper.ThrowIfNotOnUIThread(); _working.SharedPch = value; SaveIfAutomated(); }
        }

        public int PchCacheMb
        {
            get => _working.PchCacheMb;
            set { ThreadHelper.ThrowIfNotOnUIThread(); _working.PchCacheMb = Math.Max(256, value); SaveIfAutomated(); }
        }

        /// <summary>Semicolon-separated classification names whose coloring is off.</summary>
        public string DisabledColors
        {
            get => string.Join(";", _working.DisabledColors);
            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _working.DisabledColors = VxSettings.ParseList(value);
                SaveIfAutomated();
            }
        }

        protected override UIElement Child => _root ?? (_root = BuildUi());

        public override void LoadSettingsFromStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _working = VxSettings.Load();
            Refresh();
        }

        public override void SaveSettingsToStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VxSettings.Save(_working);
        }

        protected override void OnActivate(CancelEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _shownInDialog = true;
            LoadSettingsFromStorage(); // discard edits from a previous, cancelled visit
            base.OnActivate(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _shownInDialog = false;
            base.OnClosed(e);
        }

        private void SaveIfAutomated()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_shownInDialog)
                SaveSettingsToStorage();
        }

        private StackPanel BuildUi()
        {
            // The classic Options dialog hosting this page draws with the Windows system colors
            // (not the VS environment theme), so the page follows those, by dynamic reference: it
            // matches the dialog and tracks high-contrast and system color changes live.
            var root = new StackPanel { Margin = new Thickness(4) };
            root.SetResourceReference(Panel.BackgroundProperty, SystemColors.ControlBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, SystemColors.ControlTextBrushKey);

            root.Children.Add(Heading("Coloring"));
            root.Children.Add(Note("Colors and fonts for each category are under Environment > Fonts and Colors (\"VisualXtsi ...\")."));
            var colors = new WrapPanel { Margin = new Thickness(8, 2, 0, 0) };
            foreach (var (name, label) in VxSettings.ColorCategories)
            {
                var box = new CheckBox { Content = label, Width = 210, Margin = new Thickness(0, 2, 0, 2) };
                string category = name;
                box.Click += (s, e) =>
                {
                    if (box.IsChecked == true)
                        _working.DisabledColors.Remove(category);
                    else
                        _working.DisabledColors.Add(category);
                };
                _colorBoxes[name] = box;
                colors.Children.Add(box);
            }
            root.Children.Add(colors);

            root.Children.Add(Heading("Tooltips"));
            _tooltip = Option("Show expanded macros in QuickInfo", v => _working.MacroTooltip = v);
            root.Children.Add(_tooltip);

            root.Children.Add(Heading("Parsing"));
            _skipBodies = Option("Skip function bodies in included headers (faster; recommended)", v => _working.SkipHeaderBodies = v);
            root.Children.Add(_skipBodies);
            _sharedPch = Option("Build a shared precompiled header per Unreal module", v => _working.SharedPch = v);
            root.Children.Add(_sharedPch);

            var cacheRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 0, 0) };
            cacheRow.Children.Add(new TextBlock { Text = "PCH cache size limit (MB):", VerticalAlignment = VerticalAlignment.Center });
            _cacheMb = new TextBox { Width = 70, Margin = new Thickness(6, 0, 0, 0) };
            _cacheMb.TextChanged += (s, e) =>
            {
                if (int.TryParse(_cacheMb.Text, out int mb))
                    _working.PchCacheMb = Math.Max(256, mb);
            };
            cacheRow.Children.Add(_cacheMb);
            var clear = new Button { Content = "Clear PCH cache", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            clear.Click += (s, e) => _ = ClearCacheAsync();
            cacheRow.Children.Add(clear);
            _clearStatus = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            cacheRow.Children.Add(_clearStatus);
            root.Children.Add(cacheRow);

            root.Children.Add(Note("Parsing changes apply to files opened afterwards. The environment variables VX_FULL_PREAMBLE, " +
                                   "VX_PCH_CACHE_MB and VX_PCH_DIR override these settings."));
            Refresh();
            return root;
        }

        private void Refresh()
        {
            if (_root == null)
                return;
            foreach (var pair in _colorBoxes)
                pair.Value.IsChecked = _working.IsColorEnabled(pair.Key);
            _tooltip.IsChecked = _working.MacroTooltip;
            _skipBodies.IsChecked = _working.SkipHeaderBodies;
            _sharedPch.IsChecked = _working.SharedPch;
            _cacheMb.Text = _working.PchCacheMb.ToString();
            _clearStatus.Text = "";
        }

        private async Task ClearCacheAsync()
        {
            _clearStatus.Text = "Clearing...";
            ulong freed = await Task.Run(() => Engine.ClearPchCache());
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _clearStatus.Text = $"Freed {freed / (1024 * 1024)} MB";
            Log.Write($"PCH cache cleared from the options page: {freed / (1024 * 1024)} MB freed");
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };

        private static TextBlock Note(string text)
        {
            var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 0, 2) };
            note.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
            return note;
        }

        private static CheckBox Option(string label, Action<bool> set)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(8, 3, 0, 3) };
            box.Click += (s, e) => set(box.IsChecked == true);
            return box;
        }
    }
}
