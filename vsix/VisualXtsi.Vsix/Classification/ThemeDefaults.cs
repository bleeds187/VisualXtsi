using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Formatting;

namespace VisualXtsi.Classification
{
    /// <summary>
    /// A ClassificationFormatDefinition has one default color, tuned here for the dark theme.
    /// On light themes this swaps each VisualXtsi format to a light-theme default, and swaps back
    /// when the theme changes. Colors the user picked in Fonts and Colors are left alone: only a
    /// color still equal to one of our two defaults is replaced.
    /// </summary>
    internal static class ThemeDefaults
    {
        private static readonly Dictionary<string, (Color Dark, Color Light)> Defaults = new Dictionary<string, (Color, Color)>
        {
            [ClassificationNames.Preprocessor] = (Rgb(0xC5, 0x86, 0xC0), Rgb(0x8B, 0x2D, 0x8B)),
            [ClassificationNames.Macro] = (Rgb(0xE0, 0x6C, 0x2B), Rgb(0xB0, 0x4A, 0x00)),
            [ClassificationNames.UeSpecifier] = (Rgb(0x86, 0xC6, 0x91), Rgb(0x2E, 0x7D, 0x5B)),
            [SemanticNames.Local] = (Rgb(0x9C, 0xDC, 0xFE), Rgb(0x1F, 0x37, 0x7F)),
            [SemanticNames.Parameter] = (Rgb(0x8F, 0xB8, 0xDE), Rgb(0x3B, 0x6E, 0x9E)),
            [SemanticNames.Field] = (Rgb(0xC5, 0xA5, 0xF5), Rgb(0x6F, 0x3F, 0xA0)),
            [SemanticNames.Global] = (Rgb(0xD7, 0xBA, 0x7D), Rgb(0x8A, 0x5A, 0x00)),
            [SemanticNames.Type] = (Rgb(0x4E, 0xC9, 0xB0), Rgb(0x2B, 0x91, 0xAF)),
            [SemanticNames.EnumConstant] = (Rgb(0xB8, 0xD7, 0xA3), Rgb(0x2F, 0x7F, 0x4F)),
            [SemanticNames.Namespace] = (Rgb(0xC8, 0xC8, 0xC8), Rgb(0x4F, 0x4F, 0x4F)),
            [SemanticNames.Function] = (Rgb(0xDC, 0xDC, 0xAA), Rgb(0x74, 0x53, 0x1F)),
            [SemanticNames.Method] = (Rgb(0xFF, 0xD6, 0x8F), Rgb(0x8A, 0x61, 0x00)),
            [SemanticNames.StaticMethod] = (Rgb(0xFF, 0xD6, 0x8F), Rgb(0x8A, 0x61, 0x00)),
            [SemanticNames.VirtualMethod] = (Rgb(0xF2, 0xB8, 0x6B), Rgb(0xA0, 0x52, 0x2D)),
            [SemanticNames.Property] = (Rgb(0xB9, 0xA3, 0xE3), Rgb(0x6A, 0x4B, 0xA0)),
            [SemanticNames.Event] = (Rgb(0xE8, 0xA3, 0xD4), Rgb(0x8B, 0x3A, 0x79)),
            [SemanticNames.Interface] = (Rgb(0x9F, 0xD8, 0xC2), Rgb(0x2E, 0x8B, 0x6E)),
            [SemanticNames.Delegate] = (Rgb(0x6C, 0xD3, 0xBC), Rgb(0x23, 0x80, 0x6E)),
            [SemanticNames.TypeParameter] = (Rgb(0x7E, 0xC8, 0xC8), Rgb(0x2B, 0x7F, 0x87)),
            [SemanticNames.Attribute] = (Rgb(0x6F, 0xC3, 0xB2), Rgb(0x26, 0x7F, 0x70)),
        };

        private static bool _hooked;

        /// <summary>Applies the defaults for the current theme. Call on the UI thread.</summary>
        public static void Apply(IClassificationFormatMapService formatMaps, IClassificationTypeRegistryService registry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_hooked)
            {
                _hooked = true;
                VSColorTheme.ThemeChanged += _ =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    Apply(formatMaps, registry);
                };
            }

            System.Drawing.Color background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            bool light = background.GetBrightness() > 0.5f;
            ApplyActiveTheme(light);
            IClassificationFormatMap map = formatMaps.GetClassificationFormatMap("text");
            foreach (var entry in Defaults)
            {
                IClassificationType type = registry.GetClassificationType(entry.Key);
                if (type == null)
                    continue;
                TextFormattingRunProperties properties = map.GetExplicitTextProperties(type);
                Color wanted = light ? entry.Value.Light : entry.Value.Dark;
                Color? current = (properties.ForegroundBrush as SolidColorBrush)?.Color;
                bool ours = current == null || current == entry.Value.Dark || current == entry.Value.Light;
                if (ours && current != wanted)
                    map.SetExplicitTextProperties(type, properties.SetForeground(wanted));
            }
            EditorColors.ApplyItalics(Options.VxSettings.Current.ItalicColors);
        }

        /// <summary>
        /// With a coloring theme chosen (Options > Coloring and Attributes) that follows the editor,
        /// writes its light or dark variant to Fonts and Colors when that variant is not already there.
        /// </summary>
        private static void ApplyActiveTheme(bool light)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Options.VxSettings settings = Options.VxSettings.Current;
            if (string.IsNullOrEmpty(settings.ColorTheme) || settings.ThemeVariant != 0)
                return;
            Options.ColorTheme theme = Options.ColorThemes.Find(settings.ColorTheme);
            if (theme == null)
                return;
            var updated = settings.Clone();
            foreach (var pair in theme.VariantFor(0, light, Options.ColorThemes.Default))
            {
                ColorItem item = ItemFor(pair.Key, pair.Value, light);
                if (!item.SameAs(EditorColors.Read(pair.Key)))
                    EditorColors.Write(item);
                Toggle(updated.ItalicColors, pair.Key, pair.Value.Italic);
                Toggle(updated.DisabledColors, pair.Key, !pair.Value.Enabled);
            }
            if (!updated.ItalicColors.SetEquals(settings.ItalicColors) || !updated.DisabledColors.SetEquals(settings.DisabledColors))
                Options.VxSettings.Save(updated);
            Log.Write($"Coloring theme {theme.Name}: applied its {(light ? "light" : "dark")} variant");
        }

        /// <summary>A theme style as a Fonts and Colors entry; a theme-default color reads as the default.</summary>
        public static ColorItem ItemFor(string name, Options.ThemeStyle style, bool light)
        {
            Color? foreground = style.Foreground;
            if (foreground is Color c && IsDefault(name, c))
                foreground = null;
            return new ColorItem { Name = name, Foreground = foreground, Background = style.Background, Bold = style.Bold };
        }

        private static void Toggle(HashSet<string> set, string name, bool on)
        {
            if (on)
                set.Add(name);
            else
                set.Remove(name);
        }

        /// <summary>The default foreground of a category for a light or dark theme.</summary>
        public static Color For(string name, bool light) =>
            Defaults.TryGetValue(name, out var pair) ? (light ? pair.Light : pair.Dark) : Colors.Gray;

        /// <summary>Whether a color is one of a category's two theme defaults.</summary>
        public static bool IsDefault(string name, Color color) =>
            Defaults.TryGetValue(name, out var pair) && (color == pair.Dark || color == pair.Light);

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    }
}
