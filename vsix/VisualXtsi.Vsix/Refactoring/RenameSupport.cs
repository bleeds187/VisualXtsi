using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>Rename''s checks and Unreal specifics, on plain strings.</summary>
    internal static class RenameSupport
    {
        private static readonly string[] Keywords =
        {
            "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t",
            "class", "concept", "const", "consteval", "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return",
            "co_yield", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit", "export",
            "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new",
            "noexcept", "not", "nullptr", "operator", "or", "private", "protected", "public", "register", "reinterpret_cast",
            "requires", "return", "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
            "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned",
            "using", "virtual", "void", "volatile", "wchar_t", "while", "xor",
        };

        /// <summary>Why a new name cannot be used (null: it can).</summary>
        public static string Validate(string name, string oldName)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Type a name.";
            if (name == oldName)
                return "That is the current name.";
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return "A name has letters, digits and underscores, and does not start with a digit.";
            if (Keywords.Contains(name))
                return $"\"{name}\" is a C++ keyword.";
            return null;
        }

        /// <summary>
        /// The Unreal reflection macro a declaration is marked with (UCLASS, USTRUCT, UENUM, UINTERFACE,
        /// UPROPERTY, UFUNCTION, UDELEGATE), from the lines right above it (comments and blank lines
        /// skipped) or the start of its own line; null if none.
        /// </summary>
        public static string ReflectionMacro(string text, int declStart)
        {
            int lineStart = TextScan.LineStart(text, declStart);
            string own = text.Substring(lineStart, Math.Max(0, declStart - lineStart));
            var macro = new Regex(@"^\s*(UCLASS|USTRUCT|UENUM|UINTERFACE|UPROPERTY|UFUNCTION|UDELEGATE)\s*\(");
            Match m = macro.Match(own);
            if (m.Success)
                return m.Groups[1].Value;
            int line = lineStart;
            for (int seen = 0; line > 0 && seen < 6; seen++)
            {
                line = TextScan.LineStart(text, line - 1);
                string content = text.Substring(line, TextScan.LineEnd(text, line) - line).Trim();
                if (content.Length == 0 || content.StartsWith("//") || content.StartsWith("*") || content.StartsWith("/*"))
                    continue;
                m = macro.Match(content);
                if (m.Success)
                    return m.Groups[1].Value;
                if (!content.EndsWith(")") && !content.EndsWith(","))
                    return null; // another declaration
            }
            return null;
        }

        /// <summary>A class or struct''s object name: without its U, A, F, S or I prefix ("UThing" -> "Thing").</summary>
        public static string ObjectName(string typeName) =>
            typeName.Length > 1 && "UAFSI".IndexOf(typeName[0]) >= 0 && char.IsUpper(typeName[1]) ? typeName.Substring(1) : typeName;

        /// <summary>
        /// The [CoreRedirects] line that keeps assets working after renaming a reflected type or
        /// member: <paramref name="macro"/> says what it is, <paramref name="owner"/> is the class or
        /// struct a property or function belongs to. Null when there is no redirect for it.
        /// </summary>
        public static string CoreRedirect(string macro, string module, string owner, string oldName, string newName)
        {
            string path = "/Script/" + module + ".";
            switch (macro)
            {
                case "UCLASS":
                case "UINTERFACE":
                    return $"+ClassRedirects=(OldName=\"{path}{ObjectName(oldName)}\",NewName=\"{path}{ObjectName(newName)}\")";
                case "USTRUCT":
                    return $"+StructRedirects=(OldName=\"{path}{ObjectName(oldName)}\",NewName=\"{path}{ObjectName(newName)}\")";
                case "UENUM":
                    return $"+EnumRedirects=(OldName=\"{path}{oldName}\",NewName=\"{path}{newName}\")";
                case "UPROPERTY":
                    return owner == null ? null : $"+PropertyRedirects=(OldName=\"{path}{ObjectName(owner)}.{oldName}\",NewName=\"{newName}\")";
                case "UFUNCTION":
                    return owner == null ? null : $"+FunctionRedirects=(OldName=\"{path}{ObjectName(owner)}.{oldName}\",NewName=\"{newName}\")";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Where a redirect line goes in an ini file''s text: at the end of its [CoreRedirects] section,
        /// or a new section at the end. Returns the offset and the text to insert there.
        /// </summary>
        public static (int Offset, string Text) InsertRedirect(string ini, string line)
        {
            string nl = TextScan.Newline(ini.Length > 0 ? ini : "\r\n");
            Match section = Regex.Match(ini, @"^\[CoreRedirects\]\s*$", RegexOptions.Multiline);
            if (!section.Success)
            {
                string prefix = ini.Length == 0 || ini.EndsWith("\n") ? "" : nl;
                if (ini.Trim().Length > 0)
                    prefix += nl;
                return (ini.Length, prefix + "[CoreRedirects]" + nl + line + nl);
            }
            // The end of the section: its last non-blank line before the next [Section] or the end.
            Match next = Regex.Match(ini.Substring(section.Index + section.Length), @"^\[", RegexOptions.Multiline);
            int end = next.Success ? section.Index + section.Length + next.Index : ini.Length;
            int last = end;
            while (last > section.Index + section.Length && char.IsWhiteSpace(ini[last - 1]))
                last--;
            return (last, nl + line);
        }
    }
}
