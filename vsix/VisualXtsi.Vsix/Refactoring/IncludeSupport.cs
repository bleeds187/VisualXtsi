using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>#include paths and where includes and forward declarations go, on plain strings and paths.</summary>
    internal static class IncludeSupport
    {
        /// <summary>
        /// How a file includes a header: in an Unreal module (the folder with its *.Build.cs), relative
        /// to its Public or Classes folder ("GameFramework/Actor.h"), or to Private from the same
        /// module, else to the module''s folder; outside a module, relative to the including file''s
        /// folder. Paths use forward slashes.
        /// </summary>
        public static string IncludePath(string header, string headerModuleRoot, string includingFile, string includingModuleRoot)
        {
            header = Path.GetFullPath(header);
            if (headerModuleRoot != null && Within(header, headerModuleRoot))
            {
                string relative = Relative(header, headerModuleRoot);
                string[] parts = relative.Split('/');
                bool sameModule = includingModuleRoot != null && Same(includingModuleRoot, headerModuleRoot);
                if (parts.Length > 1 && (parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
                                         (sameModule && parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase))))
                    return string.Join("/", parts.Skip(1));
                return relative;
            }
            string from = Path.GetDirectoryName(Path.GetFullPath(includingFile));
            return Within(header, from) ? Relative(header, from) : MakeRelative(from, header);
        }

        private static bool Within(string path, string dir) =>
            path.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static string Relative(string path, string dir) =>
            path.Substring(Path.GetFullPath(dir).TrimEnd('\\').Length + 1).Replace('\\', '/');

        private static string MakeRelative(string fromDir, string path)
        {
            var from = new Uri(Path.GetFullPath(fromDir).TrimEnd('\\') + "\\");
            return Uri.UnescapeDataString(from.MakeRelativeUri(new Uri(path)).ToString());
        }

        private static readonly Regex IncludeLine = new Regex(@"^[ \t]*#[ \t]*include[ \t]*([""<])([^"">]+)["">]", RegexOptions.Multiline);

        /// <summary>
        /// Where <c>#include "path"</c> goes in a file: in sorted position among its last group of quoted
        /// includes (never before a source file''s own header, which comes first, nor after
        /// X.generated.h, which stays last), else after the existing includes, else after #pragma once,
        /// the include guard or the leading comment. Null if the file already includes it.
        /// </summary>
        public static (int Offset, string Text)? IncludeInsertion(string text, string path, string fileName)
        {
            string nl = TextScan.Newline(text);
            var includes = IncludeLine.Matches(text).Cast<Match>().ToList();
            if (includes.Any(m => m.Groups[2].Value.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                  m.Groups[2].Value.Replace('\\', '/').EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)))
                return null;
            string line = "#include \"" + path + "\"";
            if (includes.Count == 0)
                return (AfterPreamble(text), line + nl);

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            bool isSource = ext != ".h" && ext != ".hpp" && ext != ".hh" && ext != ".hxx" && ext != ".inl";
            // The last group of quoted includes: consecutive include lines (blank lines end a group).
            var quoted = includes.Where(m => m.Groups[1].Value == "\"").ToList();
            var group = new List<Match>();
            foreach (Match m in quoted)
            {
                if (group.Count > 0 && !OnlyBlankBetween(text, group[group.Count - 1], m, allowBlank: false))
                    group.Clear();
                group.Add(m);
            }
            if (group.Count == 0)
                group.Add(includes[includes.Count - 1]);
            Match generated = group.FirstOrDefault(m => m.Groups[2].Value.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase));
            Match own = isSource ? includes.FirstOrDefault(m => Path.GetFileNameWithoutExtension(m.Groups[2].Value).Equals(stem, StringComparison.OrdinalIgnoreCase)) : null;
            var sortable = group.Where(m => m != generated && m != own).ToList();
            Match before = sortable.FirstOrDefault(m => string.Compare(m.Groups[2].Value, path, StringComparison.OrdinalIgnoreCase) > 0) ?? generated;
            if (before != null)
                return (TextScan.LineStart(text, before.Index), line + nl);
            Match after = sortable.LastOrDefault() ?? own ?? group[group.Count - 1];
            return (TextScan.NextLineStart(text, after.Index), line + nl);
        }

        private static bool OnlyBlankBetween(string text, Match a, Match b, bool allowBlank)
        {
            int end = TextScan.NextLineStart(text, a.Index);
            string between = text.Substring(end, Math.Max(0, TextScan.LineStart(text, b.Index) - end));
            return between.Length == 0 || (allowBlank && between.Trim().Length == 0);
        }

        // After the file''s leading comment, #pragma once and include guard.
        private static int AfterPreamble(string text)
        {
            int at = 0;
            var pragma = Regex.Match(text, @"^[ \t]*#[ \t]*pragma[ \t]+once[^\n]*\n", RegexOptions.Multiline);
            if (pragma.Success)
                return pragma.Index + pragma.Length;
            var guard = Regex.Match(text, @"^[ \t]*#[ \t]*ifndef[ \t]+(\w+)[^\n]*\n[ \t]*#[ \t]*define[ \t]+\1[^\n]*\n", RegexOptions.Multiline);
            if (guard.Success)
                return guard.Index + guard.Length;
            // Skip the leading comment lines.
            while (at < text.Length)
            {
                string l = text.Substring(at, TextScan.LineEnd(text, at) - at).Trim();
                if (!(l.StartsWith("//") || l.StartsWith("/*") || l.StartsWith("*")))
                    break;
                at = TextScan.NextLineStart(text, at);
            }
            return at;
        }

        /// <summary>
        /// Where a forward declaration ("class UThing;") goes: into the file's first block of
        /// top-level forward declarations, ordered as such blocks usually are (classes, then structs,
        /// then enums, each by name), or after the includes (a blank line apart). Null if the file
        /// already has it.
        /// </summary>
        public static (int Offset, string Text)? ForwardInsertion(string text, string declaration)
        {
            string nl = TextScan.Newline(text);
            string trimmed = declaration.Trim();
            if (Regex.IsMatch(text, @"^[ \t]*" + Regex.Escape(trimmed).Replace(@"\ ", @"\s+") + @"[ \t]*\r?$", RegexOptions.Multiline))
                return null;
            var forward = new Regex(@"^((?:class|struct|enum(?:[ \t]+class)?)[ \t]+\w+[^;\n{(]*;[ \t]*\r?\n)+", RegexOptions.Multiline);
            Match m = forward.Match(text);
            if (m.Success)
            {
                var key = SortKey(trimmed);
                foreach (Capture c in m.Groups[1].Captures)
                {
                    var other = SortKey(c.Value);
                    if (other.Kind > key.Kind || (other.Kind == key.Kind && string.Compare(other.Name, key.Name, StringComparison.OrdinalIgnoreCase) > 0))
                        return (c.Index, trimmed + nl);
                }
                return (m.Index + m.Length, trimmed + nl);
            }
            var includes = IncludeLine.Matches(text).Cast<Match>().ToList();
            int after = includes.Count > 0 ? TextScan.NextLineStart(text, includes[includes.Count - 1].Index) : AfterPreamble(text);
            return (after, nl + trimmed + nl);
        }

        private static (int Kind, string Name) SortKey(string declaration)
        {
            var m = Regex.Match(declaration, @"(class|struct|enum(?:\s+class)?)\s+(\w+)");
            if (!m.Success)
                return (3, declaration);
            return (m.Groups[1].Value == "class" ? 0 : m.Groups[1].Value == "struct" ? 1 : 2, m.Groups[2].Value);
        }

        /// <summary>"class X;", "struct X;", "enum class X : uint8;", wrapped in its namespaces.</summary>
        public static string ForwardDeclaration(string kind, string name, string namespaces, bool enumScoped, string enumBase)
        {
            string decl;
            switch (kind)
            {
                case "struct": decl = "struct " + name + ";"; break;
                case "enum": decl = (enumScoped ? "enum class " : "enum ") + name + (string.IsNullOrEmpty(enumBase) ? "" : " : " + enumBase) + ";"; break;
                default: decl = "class " + name + ";"; break;
            }
            return string.IsNullOrEmpty(namespaces) ? decl : "namespace " + namespaces + " { " + decl + " }";
        }
    }
}
