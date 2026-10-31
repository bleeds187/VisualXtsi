using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// A Surround With snippet. In <see cref="Text"/>, "$selected$" is the selection, "$end$" where
    /// the caret ends up, "$file$" the file name, "$guard$" an include guard made from it, "$$" a
    /// dollar sign, and any other "$name$" a field to fill in (Tab and Shift+Tab move between
    /// fields; fields of the same name are filled in together). A tab at the start of a line is one
    /// level of indentation (in the editor's own setting); a line starting with '#' is a
    /// preprocessor line and stays at column 0. A snippet without line breaks wraps the selection
    /// in place.
    /// </summary>
    internal sealed class Snippet
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public string[] Languages { get; set; } = { Snippets.Cpp }; // "cpp", "csharp"

        public bool AppliesTo(string language) => Languages != null && Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The expanded text of a snippet, with where its fields and the caret are (offsets in <see cref="Text"/>).</summary>
    internal sealed class Expansion
    {
        public string Text;
        public List<(string Name, int Start, int Length)> Fields = new List<(string, int, int)>();
        public int End = -1; // the caret, if the snippet says ($end$, or after the selection)
    }

    /// <summary>The built-in snippets and the user's own (%LOCALAPPDATA%\VisualXtsi\snippets\snippets.json).</summary>
    internal static class Snippets
    {
        public const string Cpp = "cpp";
        public const string CSharp = "csharp";
        public const int SchemaVersion = 1;

        private static readonly string[] Both = { Cpp, CSharp };
        private static readonly string[] CSharpOnly = { CSharp };

        public static readonly IReadOnlyList<Snippet> BuiltIn = new[]
        {
            S("#if 0 ... #endif", "#if 0\n$selected$\n#endif$end$"),
            S("#ifdef ... #endif", "#ifdef $symbol$\n$selected$\n#endif // $symbol$$end$"),
            S("#ifndef ... #endif", "#ifndef $symbol$\n$selected$\n#endif // $symbol$$end$"),
            S("#if WITH_EDITOR ... #endif", "#if WITH_EDITOR\n$selected$\n#endif // WITH_EDITOR$end$"),
            S("#if WITH_EDITORONLY_DATA ... #endif", "#if WITH_EDITORONLY_DATA\n$selected$\n#endif // WITH_EDITORONLY_DATA$end$"),
            S("#if !UE_BUILD_SHIPPING ... #endif", "#if !UE_BUILD_SHIPPING\n$selected$\n#endif // !UE_BUILD_SHIPPING$end$"),
            S("Include guard", "#ifndef $guard$\n#define $guard$\n\n$selected$\n\n#endif // $guard$$end$"),
            S("#pragma region ... #pragma endregion", "#pragma region $name$\n$selected$\n#pragma endregion // $name$$end$"),
            S("TEXT(...)", "TEXT($selected$)$end$"),
            S("_T(...)", "_T($selected$)$end$"),
            S("if (...) { ... }", "if ($condition$)\n{\n\t$selected$\n}$end$", Both),
            S("if (...) { ... } else { }", "if ($condition$)\n{\n\t$selected$\n}\nelse\n{\n\t$end$\n}", Both),
            S("if (...) { } else { ... }", "if ($condition$)\n{\n\t$end$\n}\nelse\n{\n\t$selected$\n}", Both),
            S("for (...) { ... }", "for ($init$; $condition$; $step$)\n{\n\t$selected$\n}$end$", Both),
            S("for (auto& ... : ...) { ... }", "for (auto& $item$ : $range$)\n{\n\t$selected$\n}$end$"),
            S("foreach (... in ...) { ... }", "foreach (var $item$ in $collection$)\n{\n\t$selected$\n}$end$", CSharpOnly),
            S("while (...) { ... }", "while ($condition$)\n{\n\t$selected$\n}$end$", Both),
            S("do { ... } while (...)", "do\n{\n\t$selected$\n} while ($condition$);$end$", Both),
            S("switch (...) { ... }", "switch ($expression$)\n{\n\t$selected$\n}$end$", Both),
            S("try { ... } catch (...) { }", "try\n{\n\t$selected$\n}\ncatch ($exception$)\n{\n\t$end$\n}", Both),
            S("try { ... } finally { }", "try\n{\n\t$selected$\n}\nfinally\n{\n\t$end$\n}", CSharpOnly),
            S("using (...) { ... }", "using ($resource$)\n{\n\t$selected$\n}$end$", CSharpOnly),
            S("lock (...) { ... }", "lock ($lockObject$)\n{\n\t$selected$\n}$end$", CSharpOnly),
            S("namespace ... { ... }", "namespace $name$\n{\n$selected$\n} // namespace $name$$end$"),
            S("namespace ... { ... } (C#)", "namespace $name$\n{\n\t$selected$\n}$end$", CSharpOnly),
            S("#if ... #endif (C#)", "#if $symbol$\n$selected$\n#endif$end$", CSharpOnly),
            S("#region ... #endregion", "#region $name$\n$selected$\n#endregion$end$", CSharpOnly),
            S("{ ... }", "{\n\t$selected$\n}$end$", Both),
            S("( ... )", "($selected$)$end$", Both),
        };

        private static Snippet S(string name, string text, string[] languages = null) =>
            new Snippet { Name = name, Text = text, Languages = languages ?? new[] { Cpp } };

        /// <summary>%LOCALAPPDATA%\VisualXtsi\snippets\snippets.json.</summary>
        public static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                      "VisualXtsi", "snippets", "snippets.json");

        private static List<Snippet> _cached;
        private static DateTime _cachedStamp;

        /// <summary>
        /// The snippets in menu order: the user's file when there is one (it starts as a copy of the
        /// built-ins, so it can also remove or reorder them), otherwise the built-ins. A broken file
        /// is logged and the built-ins are used.
        /// </summary>
        public static IReadOnlyList<Snippet> All()
        {
            string file = UserFile;
            try
            {
                if (!File.Exists(file))
                    return BuiltIn;
                DateTime stamp = File.GetLastWriteTimeUtc(file);
                if (_cached == null || stamp != _cachedStamp)
                {
                    _cached = FromJson(File.ReadAllText(file));
                    _cachedStamp = stamp;
                }
                return _cached;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                Log.Write($"Snippets: {file}: {ex.Message}; using the built-in ones");
                return BuiltIn;
            }
        }

        /// <summary>The snippets for a language ("cpp", "csharp").</summary>
        public static IReadOnlyList<Snippet> For(string language) => All().Where(s => s.AppliesTo(language)).ToList();

        /// <summary>Creates the user file from the built-ins if there is none; returns its path.</summary>
        public static string EnsureUserFile()
        {
            string file = UserFile;
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, ToJson(BuiltIn), new UTF8Encoding(false));
            }
            return file;
        }

        public static string ToJson(IEnumerable<Snippet> snippets)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"schema\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"help\": \"$selected$ is the selection, $end$ the caret, $file$ the file name, $guard$ an include guard, ")
              .Append("$$ a dollar sign, other $name$ fields to fill in (Tab moves between them). A tab at the start of a line ")
              .Append("is one indentation level; lines starting with # stay at column 0. languages: cpp, csharp.\",\n");
            sb.Append("  \"snippets\": [\n");
            var serializer = new JavaScriptSerializer();
            sb.Append(string.Join(",\n", snippets.Select(s =>
                "    { \"name\": " + serializer.Serialize(s.Name) + ", \"languages\": " + serializer.Serialize(s.Languages) +
                ", \"text\": " + serializer.Serialize(s.Text) + " }")));
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>Reads a snippets file; throws <see cref="FormatException"/> with a message when it is not one.</summary>
        public static List<Snippet> FromJson(string json)
        {
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer().DeserializeObject(json);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException("Not valid JSON: " + ex.Message);
            }
            if (!(parsed is Dictionary<string, object> root))
                throw new FormatException("Expected an object with a \"snippets\" list.");
            if (root.TryGetValue("schema", out object schema) && !(schema is int version && version >= 1 && version <= SchemaVersion))
                throw new FormatException($"Unsupported schema {schema} (this version reads 1 to {SchemaVersion}).");
            if (!root.TryGetValue("snippets", out object list) || !(list is object[] items))
                throw new FormatException("Expected a \"snippets\" list.");
            var result = new List<Snippet>();
            foreach (object item in items)
            {
                if (!(item is Dictionary<string, object> s) || !(s.TryGetValue("name", out object name) && name is string n && n.Trim().Length > 0) ||
                    !(s.TryGetValue("text", out object text) && text is string t))
                    throw new FormatException("Every snippet needs a \"name\" and a \"text\".");
                string[] languages = s.TryGetValue("languages", out object l) && l is object[] ls
                    ? ls.OfType<string>().ToArray()
                    : new[] { Cpp };
                result.Add(new Snippet { Name = n.Trim(), Text = t, Languages = languages });
            }
            return result;
        }

        /// <summary>
        /// Expands a snippet around <paramref name="selected"/> (whole lines for a multi-line snippet,
        /// any text for a single-line one). <paramref name="baseIndent"/> is the indentation of the
        /// lines replaced (removed from the selected lines, then re-added at the snippet's depth);
        /// <paramref name="indentUnit"/> one level ("\t" or spaces).
        /// </summary>
        public static Expansion Expand(Snippet snippet, string selected, string baseIndent, string indentUnit, string fileName, string newline = "\n")
        {
            var result = new Expansion();
            var sb = new StringBuilder();
            string[] selectedLines = selected.Replace("\r\n", "\n").Split('\n');
            bool multiLine = snippet.Text.Contains('\n');
            string[] lines = snippet.Text.Replace("\r\n", "\n").Split('\n');
            int selectedEnd = -1;
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                if (li > 0)
                    sb.Append(newline);
                int depth = 0;
                while (depth < line.Length && line[depth] == '\t')
                    depth++;
                string body = line.Substring(depth);
                string indent = multiLine && !body.StartsWith("#") ? baseIndent + Repeat(indentUnit, depth) : "";
                if (multiLine && body.Trim() == "$selected$")
                {
                    // The selection's lines, re-indented to this depth (blank lines stay blank).
                    for (int i = 0; i < selectedLines.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(newline);
                        string s = selectedLines[i];
                        if (s.Trim().Length == 0)
                        {
                            if (selectedLines.Length == 1)
                                sb.Append(indent); // nothing selected: the caret goes here, indented
                            continue;
                        }
                        sb.Append(indent).Append(s.StartsWith(baseIndent) ? s.Substring(baseIndent.Length) : s.TrimStart());
                    }
                    selectedEnd = sb.Length;
                    continue;
                }
                sb.Append(indent);
                AppendWithPlaceholders(sb, body, selected, fileName, result, ref selectedEnd);
            }
            result.Text = sb.ToString();
            if (result.End < 0)
                result.End = selectedEnd >= 0 ? selectedEnd : result.Text.Length;
            return result;
        }

        private static void AppendWithPlaceholders(StringBuilder sb, string text, string selected, string fileName, Expansion result, ref int selectedEnd)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '$')
                {
                    sb.Append(text[i]);
                    continue;
                }
                int close = text.IndexOf('$', i + 1);
                if (close < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    return;
                }
                string name = text.Substring(i + 1, close - i - 1);
                i = close;
                switch (name)
                {
                    case "":
                        sb.Append('$');
                        break;
                    case "selected":
                        sb.Append(selected);
                        selectedEnd = sb.Length;
                        break;
                    case "end":
                        result.End = sb.Length;
                        break;
                    case "file":
                        sb.Append(fileName);
                        break;
                    case "guard":
                        sb.Append(Guard(fileName));
                        break;
                    default:
                        result.Fields.Add((name, sb.Length, name.Length));
                        sb.Append(name);
                        break;
                }
            }
        }

        /// <summary>"MyHeader.h" -> "MYHEADER_H".</summary>
        public static string Guard(string fileName)
        {
            var sb = new StringBuilder();
            foreach (char c in fileName ?? "")
                sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0]))
                sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string Repeat(string s, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
                sb.Append(s);
            return sb.ToString();
        }
    }
}
