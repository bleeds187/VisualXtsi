using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Formatting;
using VisualXtsi.Classification;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.UI
{
    /// <summary>
    /// The VisualXtsi Options dialog: every setting in one place, including the colors and font
    /// attributes of the coloring categories (read from and written to Fonts and Colors, so both
    /// always agree), the keyboard shortcuts (through Visual Studio's binding API) and system
    /// information. A category list on the left, the page on the right, and a description of
    /// the focused or hovered setting at the bottom. OK applies and closes, Apply applies, and
    /// Cancel restores everything as it was when the dialog opened, applied or not.
    /// </summary>
    internal sealed class OptionsDialog : DialogWindow
    {
        private const string LayoutKey = "Options";

        private static readonly string[] Pages =
        {
            "Coloring and Attributes", "Tooltips", "Navigation", "Parsing and Performance", "Unreal Engine", "Keyboard", "System Info",
        };

        // The preview: «category|text» marks colored spans (categories as in PreviewCategory).
        private const string Sample =
            "«pp|#include» «str|\"ScoreActor.h\"»\n" +
            "«pp|#define» «mac|LOG_SCORE»(Text) «mac|UE_LOG»(LogTemp, Log, Text)\n\n" +
            "«mac|USTRUCT»(«ue|BlueprintType»)\nstruct «ty|FScoreRules» { «mac|GENERATED_BODY»()\n    «mac|UPROPERTY»(«ue|EditAnywhere», «ue|Category»=«str|\"Score\"») «ty|int32» «fi|Bonus»; };\n\n" +
            "«kw|namespace» «ns|Scoring»\n{\n" +
            "«kw|enum class» «ty|EBonus» : «ty|uint8» { «en|None», «en|Double» };\n" +
            "«ty|int32» «gl|GTotalScore» = «num|0»;\n\n" +
            "«kw|void» «fn|ResetScores»() { «gl|GTotalScore» = «num|0»; }\n\n" +
            "«kw|void» «ty|AScoreActor»::«me|AddScore»(«ty|int32» «pa|Points», «ty|EBonus» «pa|Bonus»)\n{\n" +
            "    «cm|// Doubled points count twice.»\n" +
            "    «kw|const» «ty|int32» «lo|Scaled» = «pa|Bonus» == «ty|EBonus»::«en|Double» ? «pa|Points» * «num|2» : «pa|Points»;\n" +
            "    «fi|CurrentScore» += «lo|Scaled»;\n" +
            "    «gl|GTotalScore» += «lo|Scaled»;\n" +
            "    «mac|LOG_SCORE»(«mac|TEXT»(«str|\"Score changed\"»));\n" +
            "    «vm|OnScoreChanged»();\n" +
            "    «ty|UGameplayStatics»::«sm|GetPlayerPawn»(«kw|this», «num|0»);\n}\n}";

        private static readonly Dictionary<string, string> PreviewCategory = new Dictionary<string, string>
        {
            ["pp"] = ClassificationNames.Preprocessor, ["mac"] = ClassificationNames.Macro, ["ns"] = SemanticNames.Namespace,
            ["ty"] = SemanticNames.Type, ["ue"] = ClassificationNames.UeSpecifier, ["en"] = SemanticNames.EnumConstant, ["gl"] = SemanticNames.Global,
            ["fn"] = SemanticNames.Function, ["me"] = SemanticNames.Method, ["pa"] = SemanticNames.Parameter,
            ["lo"] = SemanticNames.Local, ["fi"] = SemanticNames.Field, ["vm"] = SemanticNames.VirtualMethod,
            ["sm"] = SemanticNames.StaticMethod, ["kw"] = "keyword", ["str"] = "string", ["num"] = "number", ["cm"] = "comment",
        };

        private readonly VxSettings _snapshot;
        private readonly Dictionary<string, ColorItem> _colorsSnapshot = new Dictionary<string, ColorItem>();
        private readonly Dictionary<string, string[]> _bindingsSnapshot = new Dictionary<string, string[]>();
        private readonly VxSettings _working;
        private readonly Dictionary<string, ColorItem> _colors = new Dictionary<string, ColorItem>();
        private readonly Dictionary<string, string[]> _bindings = new Dictionary<string, string[]>();
        private readonly bool _light;
        private readonly ListBox _categories;
        private readonly ContentControl _page;
        private readonly TextBlock _description;
        private readonly Grid _root;
        private readonly Dictionary<int, FrameworkElement> _built = new Dictionary<int, FrameworkElement>();
        private bool _applied;
        private TextBlock _preview;
        private StackPanel _colorRows;
        private ListView _keyList;

        public OptionsDialog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _light = EditorColors.IsLightTheme();
            _snapshot = VxSettings.Load();
            _working = _snapshot.Clone();
            foreach (var (name, _) in VxSettings.ColorCategories)
            {
                _colorsSnapshot[name] = EditorColors.Read(name);
                _colors[name] = _colorsSnapshot[name].Clone();
            }
            foreach (KeyBindings.CommandInfo command in KeyBindings.Commands)
            {
                _bindingsSnapshot[command.Name] = KeyBindings.Get(command.Name);
                _bindings[command.Name] = _bindingsSnapshot[command.Name];
            }

            Title = "VisualXtsi Options";
            Width = 1080;
            Height = 720;
            MinWidth = 640;
            MinHeight = 480;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            HasMinimizeButton = false;
            HasMaximizeButton = true;
            ShowInTaskbar = false;
            DialogTheme.Apply(this);
            SizeToContent = SizeToContent.Manual;

            var root = _root = new Grid { Margin = new Thickness(10) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170 * FontSize / 12) }); // grows with larger text
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DialogTheme.UseListStyles(root);

            // Left: categories (Up/Down switch pages).
            var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
            _categories = new ListBox { ItemsSource = Pages };
            var categoriesLabel = Label("Cate_gories", _categories);
            DockPanel.SetDock(categoriesLabel, Dock.Top);
            left.Children.Add(categoriesLabel);
            left.Children.Add(_categories);
            Describe(_categories, "The option pages. Up and Down switch pages; Tab moves into the page.");
            _categories.SelectionChanged += (s, e) => ShowPage(_categories.SelectedIndex);
            root.Children.Add(left);

            // Right: the page.
            _page = new ContentControl { Focusable = false };
            var scroller = new ScrollViewer
            {
                Content = _page,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // Text wraps to the page width; only the wide color grid scrolls sideways.
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(4, 0, 4, 0),
            };
            Grid.SetColumn(scroller, 1);
            root.Children.Add(scroller);

            // Bottom: description of the focused or hovered setting.
            _description = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 3 * FontSize * 1.4, Margin = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(_description, "Description");
            var descriptionBox = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0), Child = _description };
            descriptionBox.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(descriptionBox, 1);
            Grid.SetColumnSpan(descriptionBox, 2);
            root.Children.Add(descriptionBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 2, 10, 2) };
            ok.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Apply())
                    DialogResult = true;
            };
            Describe(ok, "Applies the changes and closes the dialog (Enter).");
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            Describe(cancel, "Closes the dialog and restores every setting as it was when the dialog opened, including changes already applied (Esc).");
            var apply = new Button { Content = new AccessText { Text = "_Apply" }, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetName(apply, "Apply");
            apply.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Apply();
            };
            Describe(apply, "Applies the changes and keeps the dialog open.");
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            Grid.SetRow(buttons, 2);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);
            Content = root;

            DialogLayout.Restore(this, LayoutKey);
            int.TryParse(DialogLayout.Get(LayoutKey + ".Page"), out int page);
            _categories.SelectedIndex = Math.Max(0, Math.Min(Pages.Length - 1, page));
            Loaded += (s, e) =>
            {
                if (_categories.ItemContainerGenerator.ContainerFromIndex(_categories.SelectedIndex) is ListBoxItem item)
                    item.Focus();
            };
            Closing += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (DialogResult != true && _applied)
                    Restore();
                DialogLayout.Save(this, LayoutKey);
                DialogLayout.Set(LayoutKey + ".Page", _categories.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            };
        }

        /// <summary>Opens the dialog on a page ("Keyboard"), or the last one shown.</summary>
        public void SelectPage(string name)
        {
            int index = Array.IndexOf(Pages, name);
            if (index >= 0)
                _categories.SelectedIndex = index;
        }

        private void ShowPage(int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (index < 0)
                return;
            if (!_built.TryGetValue(index, out FrameworkElement page))
            {
                switch (index)
                {
                    case 0: page = ColoringPage(); break;
                    case 1: page = TooltipsPage(); break;
                    case 2: page = NavigationPage(); break;
                    case 3: page = ParsingPage(); break;
                    case 4: page = UnrealPage(); break;
                    case 5: page = KeyboardPage(); break;
                    default: page = SystemInfoPage(); break;
                }
                AutomationProperties.SetName(page, Pages[index]);
                _built[index] = page;
            }
            _page.Content = page;
            _description.Text = AutomationProperties.GetHelpText(page);
        }

        // ---- Pages ----

        private FrameworkElement ColoringPage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var page = Page("Colors and font attributes of VisualXtsi's coloring categories. They are the same entries as " +
                            "Tools > Options > Environment > Fonts and Colors > Text Editor > \"VisualXtsi ...\", so changes show in both.");
            CheckBox master = Option(page, "_Enable enhanced syntax coloring", _working.ColoringEnabled, v => { _working.ColoringEnabled = v; Refresh(); },
                "Turns all VisualXtsi coloring on or off. Off, the editor shows Visual Studio's own C++ colors.");
            master.Margin = new Thickness(0, 0, 0, 6);

            page.Children.Add(Heading("Apply coloring to"));
            var targets = new WrapPanel { Margin = new Thickness(12, 0, 0, 6) };
            targets.Children.Add(Check("E_ditor", _working.ColorEditor, v => { _working.ColorEditor = v; Refresh(); },
                "Colors identifiers, macros and preprocessor directives in the code editor."));
            targets.Children.Add(Check("_QuickInfo tooltips", _working.ColorQuickInfo, v => _working.ColorQuickInfo = v,
                "Colors macros and directives in the expanded-macro QuickInfo."));
            targets.Children.Add(Check("_Find Symbol results", _working.ColorSearchResults, v => _working.ColorSearchResults = v,
                "Colors symbol names in Find Symbol in Solution by their kind (not in high contrast, which keeps the system colors)."));
            foreach (UIElement child in targets.Children)
                ((FrameworkElement)child).Margin = new Thickness(0, 0, 18, 0);
            page.Children.Add(targets);

            page.Children.Add(Heading("Categories"));
            // Each category is a row of fixed-width cells that wraps as one when the dialog is
            // narrow, so the cells stay aligned with the header and nothing scrolls sideways.
            _colorRows = new StackPanel { Margin = new Thickness(12, 0, 0, 6) };
            var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var (text, width) in new[] { ("Category", CategoryWidth), ("Foreground", PickerWidth), ("Style", StyleWidth), ("Background", PickerWidth) })
                header.Children.Add(Cell(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold }, width));
            _colorRows.Children.Add(header);
            foreach (var (name, label) in VxSettings.ColorCategories)
                _colorRows.Children.Add(ColorRow(name, label));
            page.Children.Add(_colorRows);
            page.Children.Add(Heading("Preview"));
            _preview = new TextBlock { Padding = new Thickness(10), TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetName(_preview, "Preview");
            var previewBox = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 0, 0), Focusable = false,
                Child = new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false } };
            previewBox.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Describe(previewBox, "A short Unreal-style sample drawn with the pending colors and font attributes.");
            page.Children.Add(previewBox);
            Refresh();
            return page;
        }

        private const double CategoryWidth = 200, PickerWidth = 215, StyleWidth = 130, ResetWidth = 70;

        // A fixed-width cell (scaled with the dialog font, so larger text does not clip).
        private Border Cell(UIElement content, double width) =>
            new Border { Width = width * FontSize / 12, Child = content, VerticalAlignment = VerticalAlignment.Center };

        private WrapPanel ColorRow(string name, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ColorItem item = _colors[name];
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            var enabled = new CheckBox { Content = label, IsChecked = _working.IsColorOn(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
            enabled.Click += (s, e) =>
            {
                if (enabled.IsChecked == true)
                    _working.DisabledColors.Remove(name);
                else
                    _working.DisabledColors.Add(name);
                Refresh();
            };
            Describe(enabled, $"Colors {label.ToLowerInvariant()} in the editor. Off, they keep Visual Studio's own color.");
            row.Children.Add(Cell(enabled, CategoryWidth));

            var foreground = new ColorPicker($"Foreground of {label}", "Default", ThemeDefaults.For(name, _light), _light) { Value = item.Foreground, Margin = new Thickness(0, 2, 10, 2) };
            foreground.Changed += () => { item.Foreground = foreground.Value; Refresh(); };
            Describe(foreground, $"Text color of {label.ToLowerInvariant()}. Default follows the light or dark theme; pick a named color, type a hex value, or choose Custom....");
            row.Children.Add(Cell(foreground, PickerWidth));

            var background = new ColorPicker($"Background of {label}", "None", null, _light) { Value = item.Background, Margin = new Thickness(0, 2, 10, 2) };
            background.Changed += () => { item.Background = background.Value; Refresh(); };
            Describe(background, $"Background behind {label.ToLowerInvariant()}. None keeps the editor background.");

            var bold = new CheckBox { Content = "Bold", IsChecked = item.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            AutomationProperties.SetName(bold, $"Bold {label}");
            bold.Click += (s, e) => { item.Bold = bold.IsChecked == true; Refresh(); };
            Describe(bold, $"Draws {label.ToLowerInvariant()} in bold (the Fonts and Colors \"Bold\" setting).");

            var italic = new CheckBox { Content = "Italic", IsChecked = _working.ItalicColors.Contains(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            AutomationProperties.SetName(italic, $"Italic {label}");
            italic.Click += (s, e) =>
            {
                if (italic.IsChecked == true)
                    _working.ItalicColors.Add(name);
                else
                    _working.ItalicColors.Remove(name);
                Refresh();
            };
            Describe(italic, $"Draws {label.ToLowerInvariant()} in italics (a VisualXtsi setting: Fonts and Colors has none).");
            var style = new StackPanel { Orientation = Orientation.Horizontal };
            style.Children.Add(bold);
            style.Children.Add(italic);
            row.Children.Add(Cell(style, StyleWidth));
            row.Children.Add(Cell(background, PickerWidth));

            var reset = new Button { Content = "Reset", Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(reset, $"Reset {label}");
            reset.Click += (s, e) =>
            {
                item.Foreground = null;
                item.Background = null;
                item.Bold = false;
                foreground.Value = null;
                background.Value = null;
                bold.IsChecked = false;
                bool defaultItalic = VxSettings.DefaultItalicColors.Contains(name);
                italic.IsChecked = defaultItalic;
                if (defaultItalic)
                    _working.ItalicColors.Add(name);
                else
                    _working.ItalicColors.Remove(name);
                Refresh();
            };
            Describe(reset, $"Puts {label.ToLowerInvariant()} back to the theme's default color, no background, and the default font style.");
            row.Children.Add(Cell(reset, ResetWidth));
            return row;
        }

        private FrameworkElement TooltipsPage()
        {
            var page = Page("What VisualXtsi adds to QuickInfo tooltips.");
            Option(page, "Show _expanded macros in QuickInfo", _working.MacroTooltip, v => _working.MacroTooltip = v,
                "Hovering a macro shows its full expansion, laid out one statement per line, with a link to its #define.");
            Option(page, "_Color macros in the expansion", _working.ColorQuickInfo, v => _working.ColorQuickInfo = v,
                "Uses the Macro and Preprocessor directive colors inside the expansion (also under Coloring and Attributes).");
            Option(page, "Describe Unreal _specifiers and where native events and RPCs are implemented", _working.SpecifierTooltip, v => _working.SpecifierTooltip = v,
                "Hovering a specifier inside UCLASS(...), UPROPERTY(...), UFUNCTION(...) shows what it does; hovering a BlueprintNativeEvent or RPC shows its Name_Implementation (and Name_Validate) with a link.");
            return page;
        }

        private FrameworkElement NavigationPage()
        {
            var page = Page("Open File in Solution, Find Symbol in Solution and Go to Definition.");
            Option(page, "_Remember the last search in Open File and Find Symbol", _working.RememberQueries, v => _working.RememberQueries = v,
                "Each dialog starts with the query you last opened something with. Off, they start empty (Find Symbol still starts with selected text).");
            page.Children.Add(Number("_Maximum results listed:", _working.MaxResults, 10, 1000, v => _working.MaxResults = v,
                "How many matches Open File and Find Symbol list, best first (10 to 1000)."));

            page.Children.Add(Heading("When Go to Definition finds several definitions"));
            var choices = new StackPanel { Margin = new Thickness(12, 0, 0, 6) };
            choices.Children.Add(Radio("definitions", "_Show a list to choose from", _working.PickDefinition, () => _working.PickDefinition = true,
                "Overloads and declarations in several files open a list; Enter goes to the selected one."));
            choices.Children.Add(Radio("definitions", "_Go to the best match", !_working.PickDefinition, () => _working.PickDefinition = false,
                "Goes straight to the first candidate (definitions in the same scope rank first)."));
            page.Children.Add(choices);

            page.Children.Add(Heading("Index"));
            Option(page, "Index the _Unreal Engine source for Open File and Find Symbol (about 60,000 files, ~0.8 GB)", _working.IndexEngineSource,
                v => _working.IndexEngineSource = v,
                "The project's own source is always indexed. Turning engine indexing off takes effect after a restart.");
            var rebuild = new Button { Content = new AccessText { Text = "Re_build Symbol Index" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 6, 0, 0) };
            AutomationProperties.SetName(rebuild, "Rebuild Symbol Index");
            var rebuildStatus = new TextBlock { Margin = new Thickness(12, 4, 0, 0) };
            AutomationProperties.SetLiveSetting(rebuildStatus, AutomationLiveSetting.Polite);
            async Task RebuildAsync()
            {
                rebuildStatus.Text = "Rebuilding...";
                await Task.Run(() => Engine.SymbolsRebuild());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Commands.OpenFileInSolutionCommand.EnsureRoots();
                rebuildStatus.Text = "The symbol index is being rebuilt in the background.";
            }
            rebuild.Click += (s, e) => _ = RebuildAsync();
            Describe(rebuild, "Discards the symbol index and its cache and rescans every indexed file in the background.");
            page.Children.Add(rebuild);
            page.Children.Add(rebuildStatus);
            return page;
        }

        private FrameworkElement ParsingPage()
        {
            var page = Page("How files are parsed in the background. Changes apply to files opened afterwards. " +
                            "The environment variables VX_FULL_PREAMBLE, VX_PCH_CACHE_MB and VX_PCH_DIR override these settings.");
            Option(page, "_Skip function bodies in included headers (faster; recommended)", _working.SkipHeaderBodies, v => _working.SkipHeaderBodies = v,
                "Included headers are parsed for declarations only; the open file keeps its function bodies. About a third faster on first parse.");
            Option(page, "Build a shared _precompiled header per Unreal module", _working.SharedPch, v => _working.SharedPch = v,
                "After a module's first file is parsed, a PCH of the headers its files share is built, so its other files open faster.");
            page.Children.Add(Number("PCH cache size _limit (MB):", _working.PchCacheMb, 256, 1 << 20, v => _working.PchCacheMb = v,
                "The least recently used module PCHs are deleted beyond this size (at least 256 MB)."));
            var clear = new Button { Content = new AccessText { Text = "_Clear PCH Cache" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 6, 0, 0) };
            AutomationProperties.SetName(clear, "Clear PCH Cache");
            var status = new TextBlock { Margin = new Thickness(12, 4, 0, 0) };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            async Task ClearAsync()
            {
                status.Text = "Clearing...";
                ulong freed = await Task.Run(() => Engine.ClearPchCache());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                status.Text = $"Freed {freed / (1024 * 1024)} MB.";
                Log.Write($"PCH cache cleared from the Options dialog: {freed / (1024 * 1024)} MB freed");
            }
            clear.Click += (s, e) => _ = ClearAsync();
            Describe(clear, "Deletes the cached module PCHs that are not in use right now. They are rebuilt when needed.");
            page.Children.Add(clear);
            page.Children.Add(status);
            return page;
        }

        private FrameworkElement UnrealPage()
        {
            var page = Page("Unreal Engine projects (UE 4.27 and UE5).");
            page.Children.Add(Heading("Compile arguments come from"));
            var sources = new StackPanel { Margin = new Thickness(12, 0, 0, 6) };
            string[] labels =
            {
                "compile_commands.json if one lists the file, else the _Unreal project files (recommended)",
                "compile_commands.json _only",
                "Unreal project files _only (Intermediate\\ProjectFiles\\*.vcxproj)",
            };
            string[] help =
            {
                "A compile_commands.json in a parent folder wins; otherwise the project files Unreal generates for Visual Studio are read.",
                "Only compile_commands.json is used; files it does not list are parsed with default arguments.",
                "Only the generated project files are read (\"Generate Visual Studio project files\"), even if a compile_commands.json exists.",
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                sources.Children.Add(Radio("argsSource", labels[i], _working.ArgsSource == i, () => _working.ArgsSource = value, help[i]));
            }
            page.Children.Add(sources);
            Option(page, "Note missing _generated code in macro QuickInfo", _working.GeneratedCodeHint, v => _working.GeneratedCodeHint = v,
                "When a GENERATED_BODY-style macro cannot expand because UnrealHeaderTool has not run (no .generated.h yet), the tooltip says so.");
            return page;
        }

        private FrameworkElement KeyboardPage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var page = Page("VisualXtsi's commands and their shortcuts, as in Tools > Options > Environment > Keyboard. " +
                            "Text Editor shortcuts apply in code editors and win over Global ones there.");
            _keyList = new ListView { View = new GridView(), Margin = new Thickness(0, 0, 0, 8), MinHeight = 260 };
            // The list styles live on the dialog's content (see DialogTheme.UseListStyles).
            _keyList.Style = _root.TryFindResource(typeof(ListView)) as Style;
            var grid = (GridView)_keyList.View;
            grid.Columns.Add(new GridViewColumn { Header = "Command", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Label") });
            grid.Columns.Add(new GridViewColumn { Header = "Shortcuts", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Shortcuts") });
            grid.Columns.Add(new GridViewColumn { Header = "Command name", Width = 240, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            var itemStyle = new Style(typeof(ListViewItem), _root.TryFindResource(typeof(ListViewItem)) as Style);
            itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding("Accessible")));
            _keyList.ItemContainerStyle = itemStyle;
            var label = Label("_Commands", _keyList);
            page.Children.Add(label);
            page.Children.Add(_keyList);
            Describe(_keyList, "Select a command, then change or remove its shortcuts. Enter changes the selected command's shortcut.");
            _keyList.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ChangeShortcut();
                    e.Handled = true;
                }
            };
            _keyList.MouseDoubleClick += (s, e) => ChangeShortcut();

            var buttons = new WrapPanel();
            var change = new Button { Content = new AccessText { Text = "C_hange Shortcut..." }, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(change, "Change Shortcut");
            change.Click += (s, e) => ChangeShortcut();
            Describe(change, "Press a new shortcut for the selected command. It replaces the command's current shortcuts when you apply.");
            var remove = new Button { Content = new AccessText { Text = "_Remove Shortcuts" }, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(remove, "Remove Shortcuts");
            remove.Click += (s, e) =>
            {
                if (_keyList.SelectedItem is KeyRow row)
                {
                    _bindings[row.Name] = new string[0];
                    RefreshKeys(row.Name);
                }
            };
            Describe(remove, "Removes every shortcut of the selected command (it stays in the menus).");
            var reset = new Button { Content = new AccessText { Text = "Reset All to _Defaults" }, Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetName(reset, "Reset All to Defaults");
            reset.Click += (s, e) =>
            {
                foreach (KeyBindings.CommandInfo command in KeyBindings.Commands)
                    _bindings[command.Name] = command.Defaults;
                RefreshKeys((_keyList.SelectedItem as KeyRow)?.Name);
            };
            Describe(reset, "Puts every VisualXtsi command back to its default shortcut: Alt+O, Shift+Alt+O and Alt+G in the editor, Shift+Alt+S everywhere.");
            buttons.Children.Add(change);
            buttons.Children.Add(remove);
            buttons.Children.Add(reset);
            page.Children.Add(buttons);
            RefreshKeys(null);
            return page;
        }

        private FrameworkElement SystemInfoPage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var page = Page("Versions, paths and cache sizes, for bug reports.");
            var details = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(4),
                Text = "Collecting...",
            };
            page.Children.Add(Label("_Details", details));
            page.Children.Add(details);
            Describe(details, "VisualXtsi and Visual Studio versions, libclang, the index, and where logs and caches live.");
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var copy = new Button { Content = new AccessText { Text = "_Copy to Clipboard" }, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(copy, "Copy to Clipboard");
            copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(details.Text);
                    _description.Text = "Copied.";
                }
                catch (Exception ex)
                {
                    Log.Write($"Copying the system info failed: {ex.Message}");
                }
            };
            Describe(copy, "Copies the details to the clipboard.");
            var refresh = new Button { Content = new AccessText { Text = "R_efresh" }, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(refresh, "Refresh");
            Describe(refresh, "Collects the details again (index counts and cache sizes change as you work).");
            var openLog = new Button { Content = new AccessText { Text = "Open _Log File" }, Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetName(openLog, "Open Log File");
            openLog.Click += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Commands.EditorCommands.OpenDocument(Log.FilePath);
            };
            Describe(openLog, "Opens VisualXtsi.log in the editor (after this dialog closes, if it is modal over the editor).");
            buttons.Children.Add(copy);
            buttons.Children.Add(refresh);
            buttons.Children.Add(openLog);
            page.Children.Add(buttons);

            string vsVersion = SystemInfo.VisualStudioVersion();
            async Task CollectAsync()
            {
                details.Text = "Collecting...";
                string text = await Task.Run(() => SystemInfo.Format(SystemInfo.Collect(vsVersion)));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                details.Text = text;
            }
            refresh.Click += (s, e) => _ = CollectAsync();
            _ = CollectAsync();
            return page;
        }

        // ---- Apply / restore ----

        private bool Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CommitPendingEdits();
            foreach (var (name, _) in VxSettings.ColorCategories)
                if (!_colors[name].SameAs(EditorColors.Read(name)))
                    EditorColors.Write(_colors[name]);
            VxSettings.Save(_working.Clone());
            EditorColors.ApplyItalics(_working.ItalicColors);
            foreach (KeyBindings.CommandInfo command in KeyBindings.Commands)
                if (!_bindings[command.Name].SequenceEqual(KeyBindings.Get(command.Name)))
                    KeyBindings.Set(command.Name, _bindings[command.Name]);
            _applied = true;
            _description.Text = "Applied.";
            return true;
        }

        private void Restore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var pair in _colorsSnapshot)
                if (!pair.Value.SameAs(EditorColors.Read(pair.Key)))
                    EditorColors.Write(pair.Value);
            VxSettings.Save(_snapshot.Clone());
            EditorColors.ApplyItalics(_snapshot.ItalicColors);
            foreach (var pair in _bindingsSnapshot)
                if (!pair.Value.SequenceEqual(KeyBindings.Get(pair.Key)))
                    KeyBindings.Set(pair.Key, pair.Value);
            Log.Write("Options dialog cancelled: applied changes were restored");
        }

        // A number box or color text being edited commits when it loses focus; make sure it has.
        private void CommitPendingEdits()
        {
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox)
                (Keyboard.FocusedElement as UIElement)?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            if (Keyboard.FocusedElement is DependencyObject focused && FindParent<ColorPicker>(focused) != null)
                FocusManager.SetFocusedElement(this, _categories);
        }

        private static T FindParent<T>(DependencyObject element) where T : DependencyObject
        {
            for (DependencyObject e = element; e != null; e = VisualTreeHelper.GetParent(e))
                if (e is T match)
                    return match;
            return null;
        }

        // ---- Coloring preview ----

        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_colorRows != null)
                foreach (UIElement child in _colorRows.Children)
                    child.IsEnabled = _working.ColoringEnabled;
            if (_preview == null)
                return;
            TextFormattingRunProperties plain = EditorColors.PlainText();
            _preview.FontFamily = plain?.Typeface.FontFamily ?? new FontFamily("Consolas");
            _preview.FontSize = plain?.FontRenderingEmSize > 0 ? plain.FontRenderingEmSize : 13;
            _preview.Background = EditorColors.Background();
            _preview.Foreground = plain != null && !plain.ForegroundBrushEmpty ? plain.ForegroundBrush : SystemColors.WindowTextBrush;
            _preview.Inlines.Clear();
            int at = 0;
            while (at < Sample.Length)
            {
                int open = Sample.IndexOf('«', at);
                if (open < 0)
                {
                    _preview.Inlines.Add(new Run(Sample.Substring(at)));
                    break;
                }
                if (open > at)
                    _preview.Inlines.Add(new Run(Sample.Substring(at, open - at)));
                int bar = Sample.IndexOf('|', open), close = Sample.IndexOf('»', open);
                string key = Sample.Substring(open + 1, bar - open - 1), text = Sample.Substring(bar + 1, close - bar - 1);
                _preview.Inlines.Add(PreviewRun(PreviewCategory[key], text));
                at = close + 1;
            }
        }

        private Run PreviewRun(string category, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var run = new Run(text);
            if (_colors.TryGetValue(category, out ColorItem item))
            {
                if (!(_working.ColoringEnabled && _working.ColorEditor && _working.IsColorOn(category)))
                    return run; // Visual Studio's own color would show; plain text stands in for it
                run.Foreground = new SolidColorBrush(item.Foreground ?? ThemeDefaults.For(category, _light));
                if (item.Background is Color bg)
                    run.Background = new SolidColorBrush(bg);
                if (item.Bold)
                    run.FontWeight = FontWeights.Bold;
                if (_working.ItalicColors.Contains(category))
                    run.FontStyle = FontStyles.Italic;
                return run;
            }
            TextFormattingRunProperties properties = EditorColors.Properties(category);
            if (properties != null && !properties.ForegroundBrushEmpty)
                run.Foreground = properties.ForegroundBrush;
            return run;
        }

        // ---- Keyboard ----

        private sealed class KeyRow
        {
            public string Name { get; set; }
            public string Label { get; set; }
            public string Shortcuts { get; set; }
            public string Accessible => $"{Label}, {Shortcuts}";
        }

        private void RefreshKeys(string select)
        {
            var rows = KeyBindings.Commands.Select(c => new KeyRow
            {
                Name = c.Name,
                Label = c.Label,
                Shortcuts = _bindings[c.Name].Length == 0 ? "(none)" : string.Join(", ", _bindings[c.Name].Select(KeyBindings.Describe)),
            }).ToList();
            _keyList.ItemsSource = rows;
            _keyList.SelectedItem = rows.FirstOrDefault(r => r.Name == select) ?? rows[0];
            if (_keyList.ItemContainerGenerator.ContainerFromItem(_keyList.SelectedItem) is ListViewItem item && _keyList.IsKeyboardFocusWithin)
                item.Focus();
        }

        private void ChangeShortcut()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_keyList.SelectedItem is KeyRow row))
                return;
            var capture = new KeyCaptureDialog(row.Name, row.Label);
            if (capture.ShowModal() == true && capture.Binding != null)
            {
                _bindings[row.Name] = new[] { capture.Binding };
                RefreshKeys(row.Name);
                _description.Text = $"{row.Label}: {KeyBindings.Describe(capture.Binding)}, once applied.";
            }
            if (_keyList.ItemContainerGenerator.ContainerFromItem(_keyList.SelectedItem) is ListViewItem item)
                item.Focus();
        }

        // ---- Building blocks ----

        /// <summary>Shows <paramref name="text"/> in the description box while the element has focus or the mouse; screen readers get it as help text.</summary>
        private void Describe(FrameworkElement element, string text)
        {
            AutomationProperties.SetHelpText(element, text);
            element.GotKeyboardFocus += (s, e) => _description.Text = text;
            element.MouseEnter += (s, e) => _description.Text = text;
            element.MouseLeave += (s, e) =>
            {
                if (Keyboard.FocusedElement is DependencyObject focused)
                    for (DependencyObject d = focused; d != null; d = VisualTreeHelper.GetParent(d))
                        if (AutomationProperties.GetHelpText(d) is string help && help.Length > 0)
                        {
                            _description.Text = help;
                            return;
                        }
            };
        }

        private StackPanel Page(string description)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetHelpText(page, description);
            page.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            return page;
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };

        private Label Label(string text, Control target)
        {
            var label = new Label { Content = text, Target = target, Padding = new Thickness(0, 0, 0, 3) };
            label.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            AutomationProperties.SetLabeledBy(target, label);
            return label;
        }

        private CheckBox Check(string label, bool value, Action<bool> set, string description)
        {
            var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            box.Click += (s, e) => set(box.IsChecked == true);
            Describe(box, description);
            return box;
        }

        private CheckBox Option(StackPanel page, string label, bool value, Action<bool> set, string description)
        {
            CheckBox box = Check(label, value, set, description);
            box.Margin = new Thickness(0, 3, 0, 3);
            page.Children.Add(box);
            return box;
        }

        private RadioButton Radio(string group, string label, bool value, Action select, string description)
        {
            var radio = new RadioButton { GroupName = group, Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
            radio.Checked += (s, e) => select();
            Describe(radio, description);
            return radio;
        }

        private FrameworkElement Number(string label, int value, int min, int max, Action<int> set, string description)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 3) };
            var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = 90, Padding = new Thickness(3, 1, 3, 1) };
            var caption = Label(label, box);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Padding = new Thickness(0, 0, 8, 0);
            row.Children.Add(caption);
            row.Children.Add(box);
            int current = value;
            box.LostFocus += (s, e) =>
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) && parsed >= min && parsed <= max)
                {
                    current = parsed;
                    set(parsed);
                }
                else
                {
                    _description.Text = $"Enter a whole number from {min:N0} to {max:N0}.";
                    box.Text = current.ToString(CultureInfo.CurrentCulture);
                }
            };
            Describe(box, description);
            return row;
        }
    }
}
