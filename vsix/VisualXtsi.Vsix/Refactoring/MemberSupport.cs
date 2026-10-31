using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VisualXtsi.Interop;

namespace VisualXtsi.Refactoring
{
    /// <summary>Add Member and Add Similar Member, on plain strings.</summary>
    internal static class MemberSupport
    {
        private static readonly Regex Label = new Regex(@"^\s*(public|protected|private)\s*:(?!:)");

        /// <summary>
        /// The access sections of a class body ("{" at <paramref name="open"/>, "}" at <paramref name="close"/>):
        /// each with its access, where its lines start (after the label) and where it ends (the next
        /// label's line, or the line of the closing brace).
        /// </summary>
        public static List<(string Access, int Start, int End)> Sections(string text, int open, int close, bool isStruct)
        {
            var sections = new List<(string, int, int)>();
            string access = isStruct ? "public" : "private";
            if (TextScan.LineStart(text, close) <= open)
                return new List<(string, int, int)> { (access, open + 1, close) }; // all on one line
            int start = Math.Min(TextScan.NextLineStart(text, open), close), depth = 0;
            for (int line = start; line < close; line = TextScan.NextLineStart(text, line))
            {
                int end = Math.Min(TextScan.LineEnd(text, line), close);
                string content = text.Substring(line, end - line);
                Match m = depth == 0 ? Label.Match(content) : Match.Empty;
                if (m.Success)
                {
                    sections.Add((access, start, line));
                    access = m.Groups[1].Value;
                    start = Math.Min(TextScan.NextLineStart(text, line), close);
                }
                depth += Depth(content);
                if (TextScan.NextLineStart(text, line) <= line)
                    break;
            }
            int closeLine = TextScan.LineStart(text, close);
            sections.Add((access, start, Math.Max(start, closeLine < start ? close : closeLine)));
            return sections;
        }

        // How much a line changes the brace depth (comments and literals left out).
        private static int Depth(string line)
        {
            int d = 0;
            string code = Regex.Replace(Regex.Replace(line, @"//.*$", ""), "\"([^\"\\\\]|\\\\.)*\"|'([^'\\\\]|\\\\.)*'", "");
            foreach (char c in code)
                d += c == '{' ? 1 : c == '}' ? -1 : 0;
            return d;
        }

        /// <summary>The access at <paramref name="at"/> in a class body.</summary>
        public static string AccessAt(string text, int open, int close, int at, bool isStruct) =>
            Sections(text, open, close, isStruct).LastOrDefault(s => s.Start <= at).Access ?? (isStruct ? "public" : "private");

        /// <summary>
        /// Where a member with the given access goes: after the last line of the last section with
        /// that access, or at the end of the class with a label first. Label is the label line to
        /// write (null: none); NewLine says the class closes on the same line, so a line break comes first.
        /// </summary>
        public static (int Offset, string Label, bool NewLine) Insertion(string text, int open, int close, string access, bool isStruct)
        {
            var sections = Sections(text, open, close, isStruct);
            // The section before any label counts when it holds members (not just GENERATED_BODY()).
            bool Holds((string Access, int Start, int End) s) =>
                Regex.Replace(text.Substring(s.Start, s.End - s.Start), @"//.*|\bGENERATED_\w*BODY\w*\s*\(\s*\)|\s+", "").Length > 0;
            var match = sections.Where((s, i) => s.Access == access && (i > 0 || Holds(s))).LastOrDefault();
            int closeLine = TextScan.LineStart(text, close);
            bool sameLine = text.Substring(closeLine, close - closeLine).Trim().Length > 0;
            if (match.Access == null)
                return (sameLine ? close : closeLine, access, sameLine);
            int at = match.End;
            while (at > match.Start)
            {
                int prev = TextScan.LineStart(text, at - 1);
                if (text.Substring(prev, at - prev).Trim().Length > 0)
                    break;
                at = prev;
            }
            return sameLine && at >= closeLine ? (close, null, true) : (at, null, false);
        }

        /// <summary>
        /// The reflection macro right above a declaration (from "UFUNCTION(" to its ")", across lines,
        /// whitespace collapsed), or "".
        /// </summary>
        public static string MacroText(string text, int declStart)
        {
            string macro = RenameSupport.ReflectionMacro(text, declStart);
            if (macro == null)
                return "";
            int at = text.LastIndexOf(macro + "(", declStart, StringComparison.Ordinal);
            if (at < 0)
                at = text.LastIndexOf(macro, declStart, StringComparison.Ordinal);
            int open = text.IndexOf('(', at);
            int depth = 0;
            for (int i = open; i >= 0 && i < declStart; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')' && --depth == 0)
                    return Regex.Replace(text.Substring(at, i + 1 - at), @"\s+", " ");
            }
            return "";
        }

        /// <summary>A declaration as written, from its start to <paramref name="end"/>: without the ";" or an inline body.</summary>
        public static string DeclarationText(string text, int start, int end)
        {
            string decl = text.Substring(start, Math.Max(0, Math.Min(end, text.Length) - start));
            int body = TopLevel(decl, '{', afterParen: true);
            if (body >= 0)
                decl = decl.Substring(0, body);
            return Regex.Replace(decl, @"\s+", " ").Trim().TrimEnd(';').Trim();
        }

        /// <summary>The name a declaration declares: before its parameter list, else before its initializer, else last.</summary>
        public static (int Start, int Length) NameIn(string declaration)
        {
            string head = declaration;
            int init = TopLevel(declaration, '=', afterParen: false);
            int paren = TopLevel(declaration, '(', afterParen: false);
            if (paren >= 0 && (init < 0 || paren < init))
                head = declaration.Substring(0, paren);
            else if (init >= 0)
                head = declaration.Substring(0, init);
            else if (TopLevel(declaration, '{', afterParen: false) is int brace && brace >= 0)
                head = declaration.Substring(0, brace);
            Match m = Regex.Match(head, @"(~?[A-Za-z_]\w*)\s*(\[[^\]]*\]\s*)*$");
            return m.Success ? (m.Groups[1].Index, m.Groups[1].Length) : (0, 0);
        }

        // The first `c` outside parentheses, brackets and angle brackets (with afterParen: only after a ")").
        private static int TopLevel(string s, char c, bool afterParen)
        {
            int depth = 0;
            bool seenParen = !afterParen;
            for (int i = 0; i < s.Length; i++)
            {
                char x = s[i];
                if (x == c && depth == 0 && seenParen)
                    return i;
                if (x == '(' || x == '[' || x == '<')
                    depth++;
                else if ((x == ')' || x == ']' || x == '>') && depth > 0)
                {
                    depth--;
                    if (x == ')' && depth == 0)
                        seenParen = true;
                }
            }
            return -1;
        }

        /// <summary>Whether a declaration is a method's (a parameter list before any initializer).</summary>
        public static bool IsMethod(string declaration)
        {
            int init = TopLevel(declaration, '=', afterParen: false);
            int paren = TopLevel(declaration, '(', afterParen: false);
            return paren >= 0 && (init < 0 || paren < init);
        }

        /// <summary>Why a declaration cannot be added (null: it can).</summary>
        public static string Validate(string declaration)
        {
            string d = (declaration ?? "").Trim().TrimEnd(';');
            if (d.Length == 0)
                return "Type a declaration, like \"void Run(int32 Speed)\" or \"float Speed = 1.f\".";
            var (start, length) = NameIn(d);
            if (length == 0)
                return "The declaration needs a name.";
            if (start == 0 && !(IsMethod(d) && d[0] == '~'))
                return "The declaration needs a type before the name.";
            int open = 0;
            foreach (char c in d)
            {
                open += c == '(' ? 1 : c == ')' ? -1 : 0;
                if (open < 0)
                    break;
            }
            return open != 0 ? "The parentheses do not match." : null;
        }

        /// <summary>The Unreal split a UFUNCTION's specifiers call for (CursorInfo.Ue*): native events and RPCs are implemented as _Implementation.</summary>
        public static int UeFlags(string macro)
        {
            if (string.IsNullOrEmpty(macro) || !macro.TrimStart().StartsWith("UFUNCTION"))
                return 0;
            if (Regex.IsMatch(macro, @"\bBlueprintImplementableEvent\b"))
                return CursorInfo.UeGenerated;
            int flags = 0;
            if (Regex.IsMatch(macro, @"\b(BlueprintNativeEvent|Server|Client|NetMulticast)\b"))
                flags |= CursorInfo.UeImplementation;
            if (Regex.IsMatch(macro, @"\bWithValidation\b"))
                flags |= CursorInfo.UeValidate;
            return flags;
        }

        /// <summary>
        /// What the engine would say about a method declared as <paramref name="declaration"/> in
        /// <paramref name="classes"/>, for generating its definition; null when it gets none (a field,
        /// pure, defaulted or deleted, defined inline, or a BlueprintImplementableEvent).
        /// </summary>
        public static CursorInfo MethodInfo(string declaration, string classes, string namespaces, string macro)
        {
            string d = declaration.Trim().TrimEnd(';').Trim();
            if (!IsMethod(d) || TopLevel(d, '{', afterParen: true) >= 0)
                return null;
            int ue = UeFlags(macro);
            if ((ue & CursorInfo.UeGenerated) != 0)
                return null;
            var (nameStart, nameLength) = NameIn(d);
            int open = nameStart + nameLength + d.Substring(nameStart + nameLength).IndexOf('(');
            int close = Matching(d, open);
            if (close < 0)
                return null;
            string after = d.Substring(close + 1).Trim();
            if (Regex.IsMatch(after, @"=\s*(0|default|delete)\b") || Regex.IsMatch(after, @"\bPURE_VIRTUAL\b"))
                return null;
            var sb = new StringBuilder();
            string name = d.Substring(nameStart, nameLength);
            string head = d.Substring(0, nameStart);
            bool isStatic = Regex.IsMatch(head, @"\bstatic\b");
            string result = Regex.Replace(head, @"\b(virtual|static|inline|explicit|constexpr|FORCEINLINE|FORCENOINLINE|[A-Z0-9_]+_API)\b", "");
            result = Regex.Replace(result, @"\s+", " ").Trim();
            string kind = name.StartsWith("~") ? "destructor" : result.Length == 0 ? "constructor" : "method";
            sb.Append("kind\t").Append(kind).Append('\n');
            sb.Append("name\t").Append(name).Append('\n');
            sb.Append("classes\t").Append(classes).Append('\n');
            sb.Append("namespaces\t").Append(namespaces ?? "").Append('\n');
            if (result.Length > 0)
                sb.Append("result\t").Append(result).Append('\n');
            foreach (string p in SplitParameters(d.Substring(open + 1, close - open - 1)))
            {
                int eq = TopLevel(p, '=', afterParen: false);
                string text = Regex.Replace(eq >= 0 ? p.Substring(0, eq) : p, @"\bUPARAM\s*\([^)]*\)", "");
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length == 0 || text == "void")
                    continue;
                Match pn = Regex.Match(text, @"([A-Za-z_]\w*)\s*(\[[^\]]*\]\s*)*$");
                string pname = pn.Success && pn.Index > 0 ? pn.Groups[1].Value : "";
                sb.Append("param\t").Append(text).Append('\t').Append(pname);
                if (eq >= 0)
                    sb.Append('\t').Append(Regex.Replace(p.Substring(eq + 1), @"\s+", " ").Trim());
                sb.Append('\n');
            }
            string qualifiers = string.Join(" ", Regex.Matches(after, @"\b(const|volatile|noexcept(\s*\([^)]*\))?)\b|&&|&").Cast<Match>().Select(m => m.Value.Trim()));
            if (qualifiers.Length > 0)
                sb.Append("qualifiers\t").Append(qualifiers).Append('\n');
            if (isStatic)
                sb.Append("static\t1\n");
            sb.Append("ue\t").Append(ue).Append('\n');
            return CursorInfo.Parse(sb.ToString());
        }

        private static int Matching(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(')
                    depth++;
                else if (s[i] == ')' && --depth == 0)
                    return i;
            }
            return -1;
        }

        private static IEnumerable<string> SplitParameters(string list)
        {
            int depth = 0, start = 0;
            for (int i = 0; i < list.Length; i++)
            {
                char c = list[i];
                if (c == '(' || c == '<' || c == '[' || c == '{')
                    depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}')
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return list.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (list.Trim().Length > 0)
                yield return list.Substring(start);
        }

        /// <summary>
        /// The export macro a class's members are declared with one by one ("UE_API", "GAME_API") when
        /// the class itself is not exported; null otherwise.
        /// </summary>
        public static string MemberExportMacro(string text, int classStart, int open, int close)
        {
            if (Regex.IsMatch(text.Substring(classStart, open - classStart), @"\b[A-Z][A-Z0-9_]*_API\b"))
                return null;
            var macros = Regex.Matches(text.Substring(open, close - open), @"^[ \t]*(UE_API|[A-Z][A-Z0-9_]*_API)\s+(?!\()", RegexOptions.Multiline)
                              .Cast<Match>().GroupBy(m => m.Groups[1].Value).OrderByDescending(g => g.Count()).FirstOrDefault();
            return macros != null && macros.Count() >= 2 ? macros.Key : null;
        }

        /// <summary>
        /// The declaration overriding a base's method: "virtual R Name(params) const override" (no
        /// default arguments); a native event's Name_Implementation.
        /// </summary>
        public static string OverrideDeclaration(CursorInfo m)
        {
            var sb = new StringBuilder("virtual ");
            if (!string.IsNullOrEmpty(m.Result))
                sb.Append(m.Result).Append(' ');
            sb.Append(m.Name).Append(m.NativeEvent ? "_Implementation" : "").Append('(')
              .Append(string.Join(", ", m.Parameters.Select(p => p.Text))).Append(')');
            if (m.Qualifiers.Length > 0)
                sb.Append(' ').Append(m.Qualifiers);
            return sb.Append(" override").ToString();
        }

        /// <summary>
        /// A body for an override: the base's implementation called (through Super:: when it is
        /// reached through the first base of a reflected class), with its result returned; for
        /// pure methods and native events, a value-initialized result (nothing for void or a reference).
        /// </summary>
        public static string OverrideBody(CursorInfo m, bool useSuper)
        {
            string result = (m.Result ?? "").Trim();
            bool returns = result.Length > 0 && result != "void";
            bool named = m.Parameters.All(p => p.Name.Length > 0 && p.Text != "...");
            if (m.IsPure || m.NativeEvent || !named)
                return returns && !result.EndsWith("&") ? "return {};" : "";
            string call = (useSuper ? "Super" : m.Base) + "::" + m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.Name)) + ");";
            return (returns ? "return " : "") + call;
        }

        /// <summary>The lines to insert: the macro (if any) and the declaration, at <paramref name="indent"/>.</summary>
        public static string MemberText(string macro, string declaration, string indent, string nl)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(macro))
                sb.Append(indent).Append(macro.Trim()).Append(nl);
            string d = declaration.Trim();
            if (!d.EndsWith(";") && !d.EndsWith("}"))
                d += ";";
            return sb.Append(indent).Append(d).Append(nl).ToString();
        }
    }
}
