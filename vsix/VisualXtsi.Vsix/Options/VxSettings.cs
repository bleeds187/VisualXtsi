using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using VisualXtsi.Classification;
using VisualXtsi.Interop;

namespace VisualXtsi.Options
{
    /// <summary>Something that reacts to settings changes (held weakly; called on the UI thread).</summary>
    internal interface IVxSettingsListener
    {
        void SettingsChanged(VxSettings settings);
    }

    /// <summary>
    /// VisualXtsi's settings, persisted in the Visual Studio user settings store (collection
    /// "VisualXtsi"). <see cref="Current"/> holds defaults until <see cref="EnsureLoaded"/> runs
    /// on the UI thread; parse settings are pushed to the engine whenever they load or change.
    /// </summary>
    internal sealed class VxSettings
    {
        private const string Collection = "VisualXtsi";

        /// <summary>Coloring categories that can be switched off, with their option labels.</summary>
        public static readonly IReadOnlyList<(string Name, string Label)> ColorCategories = new[]
        {
            (ClassificationNames.Preprocessor, "Preprocessor directives"),
            (ClassificationNames.Macro, "Macros"),
            (ClassificationNames.UeSpecifier, "Unreal reflection specifiers"),
            (SemanticNames.Local, "Local variables"),
            (SemanticNames.Parameter, "Parameters"),
            (SemanticNames.Field, "Member fields"),
            (SemanticNames.Global, "Global and static variables"),
            (SemanticNames.Type, "Types"),
            (SemanticNames.EnumConstant, "Enum constants"),
            (SemanticNames.Namespace, "Namespaces"),
            (SemanticNames.Function, "Functions"),
            (SemanticNames.Method, "Methods"),
            (SemanticNames.StaticMethod, "Static methods"),
            (SemanticNames.VirtualMethod, "Virtual methods"),
        };

        private static readonly object Gate = new object();
        private static readonly List<WeakReference<IVxSettingsListener>> Listeners = new List<WeakReference<IVxSettingsListener>>();
        private static bool _loaded;

        public static VxSettings Current { get; private set; } = new VxSettings();

        /// <summary>
        /// Extensions > VisualXtsi > Enable/Disable: off stops coloring, tooltips, parsing, new
        /// indexing and the navigation commands without unloading anything; the other settings
        /// keep their values.
        /// </summary>
        public bool Enabled { get; set; } = true;
        public bool MacroTooltip { get; set; } = true;

        /// <summary>QuickInfo describes well-known Unreal reflection specifiers.</summary>
        public bool SpecifierTooltip { get; set; } = true;
        public bool SkipHeaderBodies { get; set; } = true;
        public bool SharedPch { get; set; } = true;
        public int PchCacheMb { get; set; } = 4096;
        public bool IndexEngineSource { get; set; } = false;
        public HashSet<string> DisabledColors { get; set; } = new HashSet<string>();

        /// <summary>The master switch for enhanced syntax coloring (all categories).</summary>
        public bool ColoringEnabled { get; set; } = true;

        /// <summary>Color the code editor (the classifiers).</summary>
        public bool ColorEditor { get; set; } = true;

        /// <summary>Color macro expansions in QuickInfo with the coloring categories.</summary>
        public bool ColorQuickInfo { get; set; } = true;

        /// <summary>Color symbol names in Find Symbol results by kind with the coloring categories.</summary>
        public bool ColorSearchResults { get; set; } = true;

        /// <summary>Categories drawn in italics (Fonts and Colors has no italic setting).</summary>
        public HashSet<string> ItalicColors { get; set; } = new HashSet<string> { SemanticNames.Parameter, SemanticNames.StaticMethod };

        /// <summary>The italic categories by default (as the format definitions declare); keep in step with <see cref="ItalicColors"/>.</summary>
        public static IReadOnlyList<string> DefaultItalicColors => new[] { SemanticNames.Parameter, SemanticNames.StaticMethod };

        /// <summary>The coloring theme last applied (Options > Coloring and Attributes); empty: none.</summary>
        public string ColorTheme { get; set; } = "";

        /// <summary>Which variant of the theme: 0 follows Visual Studio's light or dark theme, 1 always dark, 2 always light.</summary>
        public int ThemeVariant { get; set; } = 0;

        /// <summary>Note in macro QuickInfo when UnrealHeaderTool's generated code is missing.</summary>
        public bool GeneratedCodeHint { get; set; } = true;

        /// <summary>Where compile arguments come from: 0 compile_commands.json, else Unreal project files; 1 compile_commands.json only; 2 Unreal project files only.</summary>
        public int ArgsSource { get; set; } = 0;

        /// <summary>Open File and Find Symbol start with the previous query.</summary>
        public bool RememberQueries { get; set; } = true;

        /// <summary>Most results Open File and Find Symbol list.</summary>
        public int MaxResults { get; set; } = 100;

        /// <summary>Go to Definition with several candidates: true shows a picker, false goes to the best one.</summary>
        public bool PickDefinition { get; set; } = true;

        /// <summary>Whether a coloring category is switched on in the options (ignores <see cref="Enabled"/>).</summary>
        public bool IsColorOn(string classificationName) => !DisabledColors.Contains(classificationName);

        /// <summary>Whether a coloring category is in use right now (anywhere it applies).</summary>
        public bool IsCategoryActive(string classificationName) => Enabled && ColoringEnabled && IsColorOn(classificationName);

        /// <summary>Whether a coloring category is applied in the editor right now.</summary>
        public bool IsColorEnabled(string classificationName) => IsCategoryActive(classificationName) && ColorEditor;

        /// <summary>Whether the macro QuickInfo is shown right now.</summary>
        public bool ShowMacroTooltip => Enabled && MacroTooltip;

        public VxSettings Clone()
        {
            var copy = (VxSettings)MemberwiseClone();
            copy.DisabledColors = new HashSet<string>(DisabledColors);
            copy.ItalicColors = new HashSet<string>(ItalicColors);
            return copy;
        }

        /// <summary>Loads the stored settings once. Call on the UI thread; later calls do nothing.</summary>
        public static void EnsureLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_loaded)
                return;
            _loaded = true;
            Current = Load();
            ApplyToEngine(Current);
        }

        public static VxSettings Load()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = new VxSettings();
            try
            {
                SettingsStore store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(Collection))
                    return settings;
                settings.Enabled = store.GetBoolean(Collection, nameof(Enabled), settings.Enabled);
                settings.MacroTooltip = store.GetBoolean(Collection, nameof(MacroTooltip), settings.MacroTooltip);
                settings.SpecifierTooltip = store.GetBoolean(Collection, nameof(SpecifierTooltip), settings.SpecifierTooltip);
                settings.SkipHeaderBodies = store.GetBoolean(Collection, nameof(SkipHeaderBodies), settings.SkipHeaderBodies);
                settings.SharedPch = store.GetBoolean(Collection, nameof(SharedPch), settings.SharedPch);
                settings.PchCacheMb = store.GetInt32(Collection, nameof(PchCacheMb), settings.PchCacheMb);
                settings.IndexEngineSource = store.GetBoolean(Collection, nameof(IndexEngineSource), settings.IndexEngineSource);
                settings.DisabledColors = ParseList(store.GetString(Collection, nameof(DisabledColors), ""));
                settings.ColoringEnabled = store.GetBoolean(Collection, nameof(ColoringEnabled), settings.ColoringEnabled);
                settings.ColorEditor = store.GetBoolean(Collection, nameof(ColorEditor), settings.ColorEditor);
                settings.ColorQuickInfo = store.GetBoolean(Collection, nameof(ColorQuickInfo), settings.ColorQuickInfo);
                settings.ColorSearchResults = store.GetBoolean(Collection, nameof(ColorSearchResults), settings.ColorSearchResults);
                if (store.PropertyExists(Collection, nameof(ItalicColors)))
                    settings.ItalicColors = ParseList(store.GetString(Collection, nameof(ItalicColors), ""));
                settings.GeneratedCodeHint = store.GetBoolean(Collection, nameof(GeneratedCodeHint), settings.GeneratedCodeHint);
                settings.ColorTheme = store.GetString(Collection, nameof(ColorTheme), settings.ColorTheme);
                settings.ThemeVariant = store.GetInt32(Collection, nameof(ThemeVariant), settings.ThemeVariant);
                settings.ArgsSource = store.GetInt32(Collection, nameof(ArgsSource), settings.ArgsSource);
                settings.RememberQueries = store.GetBoolean(Collection, nameof(RememberQueries), settings.RememberQueries);
                settings.MaxResults = Math.Max(10, Math.Min(1000, store.GetInt32(Collection, nameof(MaxResults), settings.MaxResults)));
                settings.PickDefinition = store.GetBoolean(Collection, nameof(PickDefinition), settings.PickDefinition);
            }
            catch (Exception ex)
            {
                Log.Write($"Reading settings failed: {ex.Message}");
            }
            return settings;
        }

        /// <summary>Stores the settings, applies them, and notifies listeners. Call on the UI thread.</summary>
        public static void Save(VxSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                WritableSettingsStore store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(Collection))
                    store.CreateCollection(Collection);
                store.SetBoolean(Collection, nameof(Enabled), settings.Enabled);
                store.SetBoolean(Collection, nameof(MacroTooltip), settings.MacroTooltip);
                store.SetBoolean(Collection, nameof(SpecifierTooltip), settings.SpecifierTooltip);
                store.SetBoolean(Collection, nameof(SkipHeaderBodies), settings.SkipHeaderBodies);
                store.SetBoolean(Collection, nameof(SharedPch), settings.SharedPch);
                store.SetInt32(Collection, nameof(PchCacheMb), settings.PchCacheMb);
                store.SetBoolean(Collection, nameof(IndexEngineSource), settings.IndexEngineSource);
                store.SetString(Collection, nameof(DisabledColors), string.Join(";", settings.DisabledColors.OrderBy(n => n)));
                store.SetBoolean(Collection, nameof(ColoringEnabled), settings.ColoringEnabled);
                store.SetBoolean(Collection, nameof(ColorEditor), settings.ColorEditor);
                store.SetBoolean(Collection, nameof(ColorQuickInfo), settings.ColorQuickInfo);
                store.SetBoolean(Collection, nameof(ColorSearchResults), settings.ColorSearchResults);
                store.SetString(Collection, nameof(ItalicColors), string.Join(";", settings.ItalicColors.OrderBy(n => n)));
                store.SetBoolean(Collection, nameof(GeneratedCodeHint), settings.GeneratedCodeHint);
                store.SetString(Collection, nameof(ColorTheme), settings.ColorTheme ?? "");
                store.SetInt32(Collection, nameof(ThemeVariant), settings.ThemeVariant);
                store.SetInt32(Collection, nameof(ArgsSource), settings.ArgsSource);
                store.SetBoolean(Collection, nameof(RememberQueries), settings.RememberQueries);
                store.SetInt32(Collection, nameof(MaxResults), settings.MaxResults);
                store.SetBoolean(Collection, nameof(PickDefinition), settings.PickDefinition);
            }
            catch (Exception ex)
            {
                Log.Write($"Saving settings failed: {ex.Message}");
            }
            _loaded = true;
            Current = settings.Clone();
            ApplyToEngine(Current);
            List<IVxSettingsListener> listeners = LiveListeners();
            Log.Write($"Settings saved (enabled {settings.Enabled}, tooltip {settings.MacroTooltip}, skip bodies {settings.SkipHeaderBodies}, " +
                      $"shared PCH {settings.SharedPch}, cache {settings.PchCacheMb} MB, colors off: " +
                      $"{(settings.DisabledColors.Count == 0 ? "none" : string.Join(", ", settings.DisabledColors))}); " +
                      $"updating {listeners.Count} classifiers");
            foreach (IVxSettingsListener listener in listeners)
                listener.SettingsChanged(Current);
        }

        /// <summary>
        /// Makes <paramref name="settings"/> current for the classifiers without storing it (the
        /// Options dialog's live preview); <see cref="Load"/> still returns the stored settings.
        /// </summary>
        public static void Preview(VxSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _loaded = true;
            Current = settings.Clone();
            foreach (IVxSettingsListener listener in LiveListeners())
                listener.SettingsChanged(Current);
        }

        /// <summary>Parses a semicolon-separated list of classification names.</summary>
        public static HashSet<string> ParseList(string value)
        {
            return new HashSet<string>((value ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0));
        }

        public static void AddListener(IVxSettingsListener listener)
        {
            lock (Gate)
                Listeners.Add(new WeakReference<IVxSettingsListener>(listener));
        }

        private static List<IVxSettingsListener> LiveListeners()
        {
            lock (Gate)
            {
                var live = new List<IVxSettingsListener>();
                Listeners.RemoveAll(w => !w.TryGetTarget(out IVxSettingsListener l) || !Add(live, l));
                return live;
            }
        }

        private static bool Add(List<IVxSettingsListener> list, IVxSettingsListener listener)
        {
            list.Add(listener);
            return true;
        }

        private static void ApplyToEngine(VxSettings settings)
        {
            try
            {
                Engine.Configure(settings.SkipHeaderBodies, settings.SharedPch, settings.PchCacheMb, settings.ArgsSource);
            }
            catch (Exception ex)
            {
                Log.Write($"Applying settings to the engine failed: {ex.Message}");
            }
        }
    }
}
