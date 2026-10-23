using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Remembers a dialog's size, position and extra layout values (column widths, sorting, last
    /// page) in the Visual Studio user settings store, collection "VisualXtsi\Dialogs". Sizes are
    /// in device-independent units, so they survive DPI changes.
    /// </summary>
    internal static class DialogLayout
    {
        private const string Collection = @"VisualXtsi\Dialogs";
        private const string RestoredKey = "VisualXtsi.LayoutRestored";

        /// <summary>Whether <see cref="Restore"/> placed the window.</summary>
        public static bool WasRestored(Window window) => window.Resources.Contains(RestoredKey);

        /// <summary>Applies the stored bounds of <paramref name="key"/>, if any are still on screen.</summary>
        public static void Restore(Window window, string key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            double[] b = Get(key + ".Bounds")?.Split(',')
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
                .ToArray();
            if (b == null || b.Length != 4 || b.Any(double.IsNaN) || b[2] < window.MinWidth || b[3] < window.MinHeight)
                return;
            // Keep the title bar reachable when the monitor it was on is gone.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!screen.IntersectsWith(new Rect(b[0], b[1], b[2], 30)))
                return;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Resources[RestoredKey] = true;
            window.Left = b[0];
            window.Top = b[1];
            window.Width = Math.Min(b[2], screen.Width);
            window.Height = Math.Min(b[3], screen.Height);
        }

        /// <summary>Stores the window's current bounds under <paramref name="key"/>.</summary>
        public static void Save(Window window, string key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Rect r = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            Set(key + ".Bounds", string.Join(",", new[] { r.Left, r.Top, r.Width, r.Height }
                .Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))));
        }

        public static string Get(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                SettingsStore store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                return store.CollectionExists(Collection) ? store.GetString(Collection, name, null) : null;
            }
            catch (Exception ex)
            {
                Log.Write($"Reading dialog layout {name} failed: {ex.Message}");
                return null;
            }
        }

        public static void Set(string name, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                WritableSettingsStore store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(Collection))
                    store.CreateCollection(Collection);
                store.SetString(Collection, name, value ?? "");
            }
            catch (Exception ex)
            {
                Log.Write($"Saving dialog layout {name} failed: {ex.Message}");
            }
        }
    }
}
