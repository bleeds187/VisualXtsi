using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using VisualXtsi.Options;

namespace VisualXtsi.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static void Expect(bool condition, string what)
        {
            if (condition)
                return;
            _failures++;
            Console.WriteLine("FAIL " + what);
        }

        private static int Main()
        {
            // Every built-in theme: complete, round-trips through JSON unchanged.
            foreach (ColorTheme theme in ColorThemes.BuiltIn)
            {
                foreach (var variant in new[] { theme.Dark, theme.Light }.Where(v => v != null))
                    Expect(ColorTheme.Categories.All(variant.ContainsKey), $"{theme.Name}: a style for every category");
                ColorTheme back = ColorTheme.FromJson(theme.ToJson());
                Expect(back.Name == theme.Name && back.Author == theme.Author && back.Description == theme.Description,
                       $"{theme.Name}: name, author and description round-trip");
                Expect(SameVariant(theme.Dark, back.Dark) && SameVariant(theme.Light, back.Light), $"{theme.Name}: styles round-trip");
            }
            Expect(ColorThemes.BuiltIn.Select(t => t.Name.ToUpperInvariant()).Distinct().Count() == ColorThemes.BuiltIn.Count,
                   "built-in names are unique");

            // A user theme with a background, disabled category and a missing variant.
            var custom = ColorThemes.Default.Clone("My Theme");
            custom.Light = null;
            custom.Dark["VisualXtsi.Local"].Background = Color.FromRgb(0x10, 0x20, 0x30);
            custom.Dark["VisualXtsi.Macro"].Enabled = false;
            custom.Dark["VisualXtsi.Type"].Bold = true;
            ColorTheme parsed = ColorTheme.FromJson(custom.ToJson());
            Expect(parsed.Light == null && parsed.Dark != null, "a missing variant stays missing");
            Expect(parsed.Dark["VisualXtsi.Local"].Background == Color.FromRgb(0x10, 0x20, 0x30), "background round-trips");
            Expect(!parsed.Dark["VisualXtsi.Macro"].Enabled && parsed.Dark["VisualXtsi.Type"].Bold, "enabled and bold round-trip");
            Expect(!parsed.BuiltIn, "files never load as built-in");

            // A schema 1 file (before the C# categories) loads, with those derived.
            ColorTheme old = ColorTheme.FromJson("{\"schema\":1,\"name\":\"Old\",\"dark\":{\"Field\":{\"foreground\":\"#112233\"},\"Type\":{\"foreground\":\"#445566\",\"bold\":true}}}");
            Expect(old.Dark["VisualXtsi.Property"].Foreground == Color.FromRgb(0x11, 0x22, 0x33) && old.Dark["VisualXtsi.Interface"].Bold &&
                   old.Dark["VisualXtsi.TypeParameter"].Italic, "schema 1 themes get the C# categories derived");
            Expect(ColorTheme.FromJson(ColorThemes.Default.ToJson()).Dark.Count == ColorTheme.Categories.Count, "schema 2 round trip keeps every category");

            // Name validation.
            string[] taken = { "Xtsi Default", "Mine" };
            Expect(ColorTheme.ValidateName("", taken) != null, "empty name rejected");
            Expect(ColorTheme.ValidateName("  ", taken) != null, "blank name rejected");
            Expect(ColorTheme.ValidateName("mine", taken) != null, "duplicate name rejected, ignoring case");
            Expect(ColorTheme.ValidateName("a/b", taken) != null && ColorTheme.ValidateName("a:b", taken) != null, "path characters rejected");
            Expect(ColorTheme.ValidateName("CON", taken) != null && ColorTheme.ValidateName("dots.", taken) != null, "reserved and trailing-dot names rejected");
            Expect(ColorTheme.ValidateName("Night Shift 2", taken) == null, "ordinary name accepted");

            // Malformed files: a FormatException with a message, never anything else.
            string[] bad =
            {
                "", "not json", "[1,2]", "{\"name\":\"X\",\"dark\":{}}", "{\"schema\":99,\"name\":\"X\",\"dark\":{}}",
                "{\"schema\":1,\"name\":\"\",\"dark\":{}}", "{\"schema\":1,\"name\":\"X\"}",
                "{\"schema\":1,\"name\":\"X\",\"dark\":{\"Local\":{\"foreground\":\"blue\"}}}",
                "{\"schema\":1,\"name\":\"X\",\"dark\":{\"Local\":5}}", "{\"schema\":1,\"name\":\"X\",\"dark\":[1]}",
            };
            foreach (string json in bad)
            {
                try
                {
                    ColorTheme.FromJson(json);
                    Expect(false, $"malformed theme rejected: {json}");
                }
                catch (FormatException ex)
                {
                    Expect(!string.IsNullOrWhiteSpace(ex.Message), $"malformed theme has a message: {json}");
                }
                catch (Exception ex)
                {
                    Expect(false, $"malformed theme threw {ex.GetType().Name} instead of FormatException: {json}");
                }
            }
            Expect(ColorTheme.FromJson("{\"schema\":1,\"name\":\"X\",\"dark\":{\"NotACategory\":{}}}").Dark.Count == 0,
                   "unknown categories are ignored");

            // High Contrast: WCAG AA (4.5:1) for every category on the editor background.
            ColorTheme high = ColorThemes.BuiltIn.First(t => t.Name == "High Contrast");
            foreach (var (variant, background) in new[] { (high.Dark, ColorThemes.DarkEditorBackground), (high.Light, ColorThemes.LightEditorBackground) })
                foreach (var pair in variant)
                {
                    double ratio = ColorTheme.ContrastRatio(pair.Value.Foreground.Value, background);
                    Expect(ratio >= 4.5, $"High Contrast {pair.Key} is {ratio:F2}:1 on {ColorTheme.Hex(background)}");
                }
            Expect(Math.Abs(ColorTheme.ContrastRatio(Colors.Black, Colors.White) - 21) < 0.01, "contrast of black on white is 21:1");

            // The variant follows the editor, unless forced; a missing one falls back.
            ColorTheme solarized = ColorThemes.BuiltIn.First(t => t.Name.StartsWith("Solarized"));
            Expect(solarized.VariantFor(0, editorIsLight: true, ColorThemes.Default) == solarized.Light, "light editor: light variant");
            Expect(solarized.VariantFor(0, editorIsLight: false, ColorThemes.Default) == solarized.Dark, "dark editor: dark variant");
            Expect(solarized.VariantFor(1, editorIsLight: true, ColorThemes.Default) == solarized.Dark, "always dark");
            Expect(solarized.VariantFor(2, editorIsLight: false, ColorThemes.Default) == solarized.Light, "always light");
            ColorTheme nord = ColorThemes.BuiltIn.First(t => t.Name.StartsWith("Nord"));
            Expect(nord.VariantFor(0, editorIsLight: true, ColorThemes.Default) == ColorThemes.Default.Light, "missing light variant: the default's");

            // Saving, renaming and loading user themes from a folder; a broken file is reported, not fatal.
            string folder = Path.Combine(Path.GetTempPath(), "VisualXtsiThemeTests");
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            ColorThemes.Save(custom, folder);
            custom.Name = "Renamed";
            ColorThemes.Save(custom, folder);
            File.WriteAllText(Path.Combine(folder, "Broken" + ColorTheme.Extension), "{ nope");
            var errors = new System.Collections.Generic.List<string>();
            var loaded = ColorThemes.LoadUserThemes(folder, errors);
            Expect(loaded.Count == 1 && loaded[0].Name == "Renamed", "rename replaces the old file");
            Expect(errors.Count == 1 && errors[0].StartsWith("Broken"), "a broken theme file is reported");
            try
            {
                ColorThemes.Save(ColorThemes.Default, folder);
                Expect(false, "built-in themes cannot be saved");
            }
            catch (InvalidOperationException)
            {
            }
            Directory.Delete(folder, true);

            Console.WriteLine(_failures == 0 ? "all tests passed" : $"{_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        private static bool SameVariant(System.Collections.Generic.Dictionary<string, ThemeStyle> a,
                                        System.Collections.Generic.Dictionary<string, ThemeStyle> b)
        {
            if (a == null || b == null)
                return a == b;
            return a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out ThemeStyle s) && s.Foreground == p.Value.Foreground &&
                                                     s.Background == p.Value.Background && s.Bold == p.Value.Bold &&
                                                     s.Italic == p.Value.Italic && s.Enabled == p.Value.Enabled);
        }
    }
}
