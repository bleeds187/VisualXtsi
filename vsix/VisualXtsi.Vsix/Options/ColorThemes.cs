using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace VisualXtsi.Options
{
    /// <summary>How one coloring category looks in a theme.</summary>
    internal sealed class ThemeStyle
    {
        public Color? Foreground;
        public Color? Background;
        public bool Bold;
        public bool Italic;
        public bool Enabled = true;

        public ThemeStyle Clone() => (ThemeStyle)MemberwiseClone();
    }

    /// <summary>
    /// A named coloring theme: a style per category for dark editors and one for light editors
    /// (either may be missing). Saved as JSON (*.xtsitheme). This file has no Visual Studio
    /// dependencies, so the theme tests can compile it on its own.
    /// </summary>
    internal sealed class ColorTheme
    {
        public const int SchemaVersion = 1;
        public const string Extension = ".xtsitheme";

        /// <summary>The coloring categories, by classification name; keys in files drop the "VisualXtsi." prefix.</summary>
        public static readonly IReadOnlyList<string> Categories = new[]
        {
            "VisualXtsi.Preprocessor", "VisualXtsi.Macro", "VisualXtsi.UeSpecifier", "VisualXtsi.Local", "VisualXtsi.Parameter",
            "VisualXtsi.Field", "VisualXtsi.Global", "VisualXtsi.Type", "VisualXtsi.EnumConstant", "VisualXtsi.Namespace",
            "VisualXtsi.Function", "VisualXtsi.Method", "VisualXtsi.StaticMethod", "VisualXtsi.VirtualMethod",
        };

        public string Name = "";
        public string Author = "";
        public string Description = "";
        public bool BuiltIn;
        public string FilePath; // user themes only
        public Dictionary<string, ThemeStyle> Dark;  // null: no dark variant
        public Dictionary<string, ThemeStyle> Light; // null: no light variant

        public ColorTheme Clone(string name)
        {
            var copy = (ColorTheme)MemberwiseClone();
            copy.Name = name;
            copy.BuiltIn = false;
            copy.FilePath = null;
            copy.Dark = Copy(Dark);
            copy.Light = Copy(Light);
            return copy;
        }

        /// <summary>"dark and light", "dark only" or "light only".</summary>
        public string Variants => Dark != null && Light != null ? "dark and light" : Dark != null ? "dark only" : "light only";

        /// <summary>
        /// The variant to use: <paramref name="mode"/> 0 follows the editor (light or dark), 1 is
        /// always dark, 2 always light. A missing variant falls back to <paramref name="fallback"/>'s.
        /// </summary>
        public Dictionary<string, ThemeStyle> VariantFor(int mode, bool editorIsLight, ColorTheme fallback)
        {
            bool light = mode == 2 || (mode == 0 && editorIsLight);
            Dictionary<string, ThemeStyle> wanted = light ? Light : Dark;
            return wanted ?? (light ? fallback?.Light : fallback?.Dark) ?? (light ? Dark : Light);
        }

        // ---- JSON ----

        public string ToJson()
        {
            var root = new Dictionary<string, object>
            {
                ["schema"] = SchemaVersion,
                ["name"] = Name,
                ["author"] = Author ?? "",
                ["description"] = Description ?? "",
            };
            if (Dark != null)
                root["dark"] = VariantJson(Dark);
            if (Light != null)
                root["light"] = VariantJson(Light);
            return Indent(new JavaScriptSerializer().Serialize(root));
        }

        /// <summary>Parses a theme file's text; throws <see cref="FormatException"/> with a readable message when it is not one.</summary>
        public static ColorTheme FromJson(string json)
        {
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                throw new FormatException("The file is not valid JSON: " + ex.Message, ex);
            }
            if (root == null)
                throw new FormatException("The file does not contain a theme object.");
            if (!(root.TryGetValue("schema", out object schema) && schema is int version))
                throw new FormatException("The file has no \"schema\" version; it is not a VisualXtsi theme.");
            if (version > SchemaVersion)
                throw new FormatException($"The theme uses schema {version}; this version of VisualXtsi reads schema {SchemaVersion}.");
            var theme = new ColorTheme
            {
                Name = (root.TryGetValue("name", out object name) ? name as string : null)?.Trim() ?? "",
                Author = root.TryGetValue("author", out object author) ? author as string ?? "" : "",
                Description = root.TryGetValue("description", out object description) ? description as string ?? "" : "",
                Dark = root.TryGetValue("dark", out object dark) ? ParseVariant(dark, "dark") : null,
                Light = root.TryGetValue("light", out object light) ? ParseVariant(light, "light") : null,
            };
            string problem = ValidateName(theme.Name, Array.Empty<string>());
            if (problem != null)
                throw new FormatException("The theme's name is not usable: " + problem);
            if (theme.Dark == null && theme.Light == null)
                throw new FormatException("The theme has neither a \"dark\" nor a \"light\" variant.");
            return theme;
        }

        /// <summary>Why a name cannot be used (null when it can): empty, taken (ignoring case), or not filesystem-safe.</summary>
        public static string ValidateName(string name, IEnumerable<string> taken)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0)
                return "Enter a name.";
            if (name.Length > 60)
                return "Use at most 60 characters.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".", StringComparison.Ordinal))
                return "Leave out \\ / : * ? \" < > | and a trailing period.";
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
            if (reserved.Contains(name.ToUpperInvariant()))
                return "That name is reserved by Windows.";
            if (taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                return "A theme with that name exists already.";
            return null;
        }

        private static Dictionary<string, object> VariantJson(Dictionary<string, ThemeStyle> variant)
        {
            var result = new Dictionary<string, object>();
            foreach (string category in Categories)
            {
                if (!variant.TryGetValue(category, out ThemeStyle s))
                    continue;
                var style = new Dictionary<string, object>();
                if (s.Foreground is Color fg)
                    style["foreground"] = Hex(fg);
                if (s.Background is Color bg)
                    style["background"] = Hex(bg);
                style["bold"] = s.Bold;
                style["italic"] = s.Italic;
                style["enabled"] = s.Enabled;
                result[Key(category)] = style;
            }
            return result;
        }

        private static Dictionary<string, ThemeStyle> ParseVariant(object value, string which)
        {
            if (value == null)
                return null;
            if (!(value is Dictionary<string, object> entries))
                throw new FormatException($"\"{which}\" must be an object of categories.");
            var variant = new Dictionary<string, ThemeStyle>();
            foreach (var entry in entries)
            {
                string category = Categories.FirstOrDefault(c => string.Equals(Key(c), entry.Key, StringComparison.OrdinalIgnoreCase));
                if (category == null)
                    continue; // a category this version does not know: ignore it
                if (!(entry.Value is Dictionary<string, object> s))
                    throw new FormatException($"\"{which}.{entry.Key}\" must be an object.");
                variant[category] = new ThemeStyle
                {
                    Foreground = ParseColor(s, "foreground", which, entry.Key),
                    Background = ParseColor(s, "background", which, entry.Key),
                    Bold = s.TryGetValue("bold", out object b) && b is bool bold && bold,
                    Italic = s.TryGetValue("italic", out object i) && i is bool italic && italic,
                    Enabled = !(s.TryGetValue("enabled", out object e) && e is bool enabled && !enabled),
                };
            }
            return variant;
        }

        private static Color? ParseColor(Dictionary<string, object> style, string field, string which, string category)
        {
            if (!style.TryGetValue(field, out object value) || value == null)
                return null;
            string text = (value as string ?? "").Trim().TrimStart('#');
            if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            throw new FormatException($"\"{which}.{category}.{field}\" is \"{value}\"; colors are written as \"#RRGGBB\".");
        }

        private static string Key(string category) => category.Substring(category.IndexOf('.') + 1);

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Dictionary<string, ThemeStyle> Copy(Dictionary<string, ThemeStyle> variant) =>
            variant?.ToDictionary(p => p.Key, p => p.Value.Clone());

        // JavaScriptSerializer writes one line; themes are meant to be read and edited by people.
        private static string Indent(string json)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                        sb.Append(json[++i]);
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{': sb.Append(c).Append('\n').Append(' ', ++depth * 2); break;
                    case '}': sb.Append('\n').Append(' ', --depth * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('\n').ToString();
        }

        // ---- Contrast (WCAG 2) ----

        public static double ContrastRatio(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }

    /// <summary>The built-in themes and the user's own (%LOCALAPPDATA%\VisualXtsi\themes).</summary>
    internal static class ColorThemes
    {
        public const string DefaultName = "Xtsi Default";

        /// <summary>The editor backgrounds the High Contrast theme is checked against (Visual Studio's dark and light).</summary>
        public static readonly Color DarkEditorBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);
        public static readonly Color LightEditorBackground = Color.FromRgb(0xFF, 0xFF, 0xFF);

        public static string UserFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualXtsi", "themes");

        // Category order of the palettes below.
        private static readonly string[] Order =
        {
            "pp", "mac", "ue", "lo", "pa", "fi", "gl", "ty", "en", "ns", "fn", "me", "sm", "vm",
        };

        public static IReadOnlyList<ColorTheme> BuiltIn { get; } = new[]
        {
            Theme(DefaultName, "The default VisualXtsi colors.",
                dark: "C586C0 E06C2B 86C691 9CDCFE 8FB8DE C5A5F5 D7BA7D 4EC9B0 B8D7A3 C8C8C8 DCDCAA FFD68F FFD68F F2B86B",
                light: "8B2D8B B04A00 2E7D5B 1F377F 3B6EA5 6F3FA0 8A5A00 2B91AF 2F7F4F 4F4F4F 74531F 8A6100 8A6100 A0522D",
                italic: "pa sm"),
            Theme("Xtsi Muted", "Low-saturation colors that stay out of the way in long sessions.",
                dark: "A889A6 C08A6A 8FAF95 A9C4D6 9AAFC2 B3A6C9 C2B08E 8FB8AE A8B89C B4B4B4 C6C6A6 D6C4A0 D6C4A0 CDB08C",
                light: "7A4F78 8C5A3A 4F6E55 3A4F66 52677D 5E5078 6E5A35 3F6E66 4F6847 5A5A5A 5E5530 6E5A30 6E5A30 7A5538",
                italic: "pa sm"),
            Theme("Xtsi Vivid", "Saturated colors that make every kind of identifier stand out.",
                dark: "E080FF FF8A3D 5EE89A 7FD4FF 66B3FF D69CFF FFC94D 3DE0C8 A6F26B E0E0E0 FFF27A FFB347 FFB347 FF7F6E",
                light: "A100B8 C24E00 008F4C 0050B3 1F6FD1 7B2CBF A36A00 007E8A 3C7D00 404040 7A6A00 B35900 B35900 C2331F",
                italic: "pa sm", bold: "ty"),
            Theme("High Contrast", "Every color reaches at least 4.5:1 contrast (WCAG AA) on the editor background.",
                dark: "FF9EFF FFA64D 8CFF9E 9EE6FF 9EC9FF E0B3FF FFE066 66FFE0 C2FF8C FFFFFF FFFF99 FFD480 FFD480 FFB3A6",
                light: "8A008A 9E3A00 005C2E 00338C 004C99 5A1F99 6B4700 005C66 2E5C00 202020 5C4D00 7A4100 7A4100 8F2600",
                italic: "pa sm", bold: "ty fn me"),
            // Adapted palettes: only the colors, mapped onto VisualXtsi's categories. Licenses and
            // attribution are in THIRD_PARTY_NOTICES.md.
            Theme("Solarized (adapted)", "Ethan Schoonover's Solarized accents, for dark and light editors.",
                dark: "D33682 CB4B16 859900 93A1A1 839496 6C71C4 B58900 2AA198 CB4B16 839496 268BD2 268BD2 268BD2 2AA198",
                light: "D33682 CB4B16 859900 586E75 657B83 6C71C4 B58900 2AA198 CB4B16 657B83 268BD2 268BD2 268BD2 2AA198",
                italic: "pa sm", author: "Colors: Ethan Schoonover (Solarized, MIT)"),
            Theme("Nord (adapted)", "Arctic Ice Studio's Nord palette (dark only).",
                dark: "B48EAD D08770 A3BE8C D8DEE9 E5E9F0 88C0D0 EBCB8B 8FBCBB B48EAD 81A1C1 88C0D0 88C0D0 88C0D0 5E81AC",
                light: null, italic: "pa sm", author: "Colors: Arctic Ice Studio and Sven Greb (Nord, MIT)"),
            Theme("Gruvbox (adapted)", "Pavel Pertsev's retro gruvbox palette, for dark and light editors.",
                dark: "8EC07C FE8019 B8BB26 EBDBB2 D5C4A1 83A598 FABD2F FABD2F D3869B A89984 B8BB26 8EC07C 8EC07C FB4934",
                light: "427B58 AF3A03 79740E 3C3836 504945 076678 B57614 B57614 8F3F71 7C6F64 79740E 427B58 427B58 9D0006",
                italic: "pa sm", author: "Colors: Pavel Pertsev (gruvbox, MIT)"),
            Theme("Dracula (adapted)", "The Dracula palette (dark only).",
                dark: "FF79C6 FFB86C 50FA7B F8F8F2 FFB86C BD93F9 F1FA8C 8BE9FD BD93F9 F8F8F2 50FA7B 50FA7B 50FA7B 8BE9FD",
                light: null, italic: "pa sm", author: "Colors: Zeno Rocha and contributors (Dracula, MIT)"),
            Theme("One (adapted)", "The One Dark and One Light syntax palettes.",
                dark: "C678DD D19A66 98C379 E06C75 D19A66 E06C75 E5C07B E5C07B D19A66 ABB2BF 61AFEF 61AFEF 61AFEF 56B6C2",
                light: "A626A4 986801 50A14F E45649 986801 E45649 C18401 C18401 986801 383A42 4078F2 4078F2 4078F2 0184BC",
                italic: "pa sm", author: "Colors: GitHub Inc. (Atom One Dark / One Light, MIT)"),
        };

        public static ColorTheme Default => BuiltIn[0];

        /// <summary>A built-in or user theme by name (ignoring case); null if there is none.</summary>
        public static ColorTheme Find(string name)
        {
            return BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ??
                   LoadUserThemes(UserFolder, null).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static List<ColorTheme> LoadUserThemes(string folder, List<string> errors)
        {
            var themes = new List<ColorTheme>();
            if (!Directory.Exists(folder))
                return themes;
            foreach (string file in Directory.EnumerateFiles(folder, "*" + ColorTheme.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    ColorTheme theme = ColorTheme.FromJson(File.ReadAllText(file));
                    theme.FilePath = file;
                    themes.Add(theme);
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    errors?.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            return themes;
        }

        /// <summary>Writes a user theme to <paramref name="folder"/> as "Name.xtsitheme"; returns the path.</summary>
        public static string Save(ColorTheme theme, string folder)
        {
            if (theme.BuiltIn)
                throw new InvalidOperationException("Built-in themes are read-only.");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, theme.Name.Trim() + ColorTheme.Extension);
            File.WriteAllText(path, theme.ToJson(), new UTF8Encoding(false));
            if (theme.FilePath != null && !string.Equals(theme.FilePath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(theme.FilePath))
                File.Delete(theme.FilePath); // renamed
            theme.FilePath = path;
            return path;
        }

        private static ColorTheme Theme(string name, string description, string dark, string light, string italic,
                                        string bold = "", string author = "VisualXtsi")
        {
            return new ColorTheme
            {
                Name = name,
                Description = description,
                Author = author,
                BuiltIn = true,
                Dark = Variant(dark, italic, bold),
                Light = Variant(light, italic, bold),
            };
        }

        private static Dictionary<string, ThemeStyle> Variant(string colors, string italic, string bold)
        {
            if (colors == null)
                return null;
            string[] hex = colors.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (hex.Length != Order.Length)
                throw new InvalidOperationException("A built-in palette needs one color per category.");
            var italics = new HashSet<string>(italic.Split(' '));
            var bolds = new HashSet<string>(bold.Split(' '));
            var variant = new Dictionary<string, ThemeStyle>();
            for (int i = 0; i < Order.Length; i++)
            {
                uint rgb = uint.Parse(hex[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                variant[ColorTheme.Categories[i]] = new ThemeStyle
                {
                    Foreground = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb),
                    Italic = italics.Contains(Order[i]),
                    Bold = bolds.Contains(Order[i]),
                };
            }
            return variant;
        }
    }
}
