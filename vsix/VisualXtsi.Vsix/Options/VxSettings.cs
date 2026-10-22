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

        public bool MacroTooltip { get; set; } = true;
        public bool SkipHeaderBodies { get; set; } = true;
        public bool SharedPch { get; set; } = true;
        public int PchCacheMb { get; set; } = 4096;
        public HashSet<string> DisabledColors { get; set; } = new HashSet<string>();

        public bool IsColorEnabled(string classificationName) => !DisabledColors.Contains(classificationName);

        public VxSettings Clone()
        {
            var copy = (VxSettings)MemberwiseClone();
            copy.DisabledColors = new HashSet<string>(DisabledColors);
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
                settings.MacroTooltip = store.GetBoolean(Collection, nameof(MacroTooltip), settings.MacroTooltip);
                settings.SkipHeaderBodies = store.GetBoolean(Collection, nameof(SkipHeaderBodies), settings.SkipHeaderBodies);
                settings.SharedPch = store.GetBoolean(Collection, nameof(SharedPch), settings.SharedPch);
                settings.PchCacheMb = store.GetInt32(Collection, nameof(PchCacheMb), settings.PchCacheMb);
                settings.DisabledColors = ParseList(store.GetString(Collection, nameof(DisabledColors), ""));
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
                store.SetBoolean(Collection, nameof(MacroTooltip), settings.MacroTooltip);
                store.SetBoolean(Collection, nameof(SkipHeaderBodies), settings.SkipHeaderBodies);
                store.SetBoolean(Collection, nameof(SharedPch), settings.SharedPch);
                store.SetInt32(Collection, nameof(PchCacheMb), settings.PchCacheMb);
                store.SetString(Collection, nameof(DisabledColors), string.Join(";", settings.DisabledColors.OrderBy(n => n)));
            }
            catch (Exception ex)
            {
                Log.Write($"Saving settings failed: {ex.Message}");
            }
            _loaded = true;
            Current = settings.Clone();
            ApplyToEngine(Current);
            List<IVxSettingsListener> listeners = LiveListeners();
            Log.Write($"Settings saved (tooltip {settings.MacroTooltip}, skip bodies {settings.SkipHeaderBodies}, " +
                      $"shared PCH {settings.SharedPch}, cache {settings.PchCacheMb} MB, colors off: " +
                      $"{(settings.DisabledColors.Count == 0 ? "none" : string.Join(", ", settings.DisabledColors))}); " +
                      $"updating {listeners.Count} classifiers");
            foreach (IVxSettingsListener listener in listeners)
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
                Engine.Configure(settings.SkipHeaderBodies, settings.SharedPch, settings.PchCacheMb);
            }
            catch (Exception ex)
            {
                Log.Write($"Applying settings to the engine failed: {ex.Message}");
            }
        }
    }
}
