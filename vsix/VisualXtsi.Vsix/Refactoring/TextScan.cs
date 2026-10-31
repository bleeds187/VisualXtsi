using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VisualXtsi.Refactoring
{
    /// <summary>Text positions and C++ structure the refactorings need, on plain strings.</summary>
    internal static class TextScan
    {
        /// <summary>"\r\n" if the text uses it, else "\n".</summary>
        public static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        /// <summary>The offset of a 1-based line and UTF-16 column (clamped to the text).</summary>
        public static int Offset(string text, int line, int column)
        {
            int offset = 0;
            for (int l = 1; l < line; l++)
            {
                int nl = text.IndexOf('\n', offset);
                if (nl < 0)
                    return text.Length;
                offset = nl + 1;
            }
            return Math.Min(text.Length, offset + Math.Max(0, column - 1));
        }

        /// <summary>The 1-based line of an offset.</summary>
        public static int LineOf(string text, int offset)
        {
            int line = 1;
            for (int i = 0; i < offset && i < text.Length; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }

        public static int LineStart(string text, int offset)
        {
            int nl = offset > 0 ? text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1) : -1;
            return nl + 1;
        }

        /// <summary>The offset of the line's end (before its line break).</summary>
        public static int LineEnd(string text, int offset)
        {
            int nl = text.IndexOf('\n', Math.Min(offset, text.Length));
            if (nl < 0)
                return text.Length;
            return nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl;
        }

        /// <summary>The offset just after the line break of the line holding <paramref name="offset"/>.</summary>
        public static int NextLineStart(string text, int offset)
        {
            int nl = text.IndexOf('\n', Math.Min(offset, text.Length));
            return nl < 0 ? text.Length : nl + 1;
        }

        public static string IndentOf(string text, int offset)
        {
            int start = LineStart(text, offset), i = start;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text.Substring(start, i - start);
        }

        /// <summary>
        /// The offset after the "}" that closes the first "{" at or after <paramref name="from"/>,
        /// skipping comments, string and character literals (raw strings too); -1 if there is none.
        /// </summary>
        public static int BlockEnd(string text, int from)
        {
            int depth = 0;
            for (int i = from; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i = text.IndexOf('\n', i);
                    if (i < 0)
                        return -1;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (i < 0)
                        return -1;
                    i++;
                }
                else if (c == 'R' && i + 1 < text.Length && text[i + 1] == '"' && (i == 0 || !IsIdent(text[i - 1]) || text[i - 1] == 'u' || text[i - 1] == '8' || text[i - 1] == 'L' || text[i - 1] == 'U'))
                {
                    int paren = text.IndexOf('(', i + 2);
                    if (paren < 0)
                        return -1;
                    string close = ")" + text.Substring(i + 2, paren - i - 2) + "\"";
                    i = text.IndexOf(close, paren, StringComparison.Ordinal);
                    if (i < 0)
                        return -1;
                    i += close.Length - 1;
                }
                else if (c == '"' || c == '\'')
                {
                    for (i++; i < text.Length && text[i] != c && text[i] != '\n'; i++)
                        if (text[i] == '\\')
                            i++;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && depth > 0 && --depth == 0)
                {
                    return i + 1;
                }
            }
            return -1;
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// Moves a block of lines to another indentation: the indentation all its lines after the first
        /// share is replaced with <paramref name="indent"/> (the first line is taken as it is, at
        /// <paramref name="indent"/>). Blank lines stay blank.
        /// </summary>
        public static string Reindent(string block, string indent, string newline)
        {
            string[] lines = block.Replace("\r\n", "\n").Split('\n');
            string common = null;
            foreach (string line in lines.Skip(1))
            {
                if (line.Trim().Length == 0)
                    continue;
                string own = line.Substring(0, line.Length - line.TrimStart().Length);
                if (common == null)
                {
                    common = own;
                    continue;
                }
                int n = 0;
                while (n < common.Length && n < own.Length && common[n] == own[n])
                    n++;
                common = common.Substring(0, n);
            }
            common = common ?? "";
            // A closing "}" usually sits at the opening line's indentation, deeper lines one level in:
            // keep that shape by taking the last line's indentation as the base when it is the shallowest.
            string last = lines.Length > 1 ? lines[lines.Length - 1] : null;
            if (last != null && last.Trim().Length > 0)
            {
                string lastIndent = last.Substring(0, last.Length - last.TrimStart().Length);
                if (lastIndent.Length <= common.Length)
                    common = lastIndent;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    sb.Append(newline);
                string line = lines[i];
                if (i == 0)
                {
                    sb.Append(indent).Append(line.TrimStart());
                    continue;
                }
                if (line.Trim().Length == 0)
                    continue;
                sb.Append(indent).Append(line.StartsWith(common) ? line.Substring(common.Length) : line.TrimStart());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Where a new member declaration goes in a class body ending at <paramref name="classEnd"/>
        /// (the offset after its "}"): the start of the line holding that "}", and whether a "public:"
        /// label is needed first (the last access label in the class is not public, or a class has none).
        /// </summary>
        public static (int Offset, bool NeedsPublic) MemberInsertion(string text, int classStart, int classEnd, bool isStruct)
        {
            int close = text.LastIndexOf('}', Math.Max(0, Math.Min(classEnd, text.Length) - 1));
            int at = LineStart(text, close);
            if (text.Substring(at, close - at).Trim().Length > 0)
                at = close; // "class X { int A; };" on one line
            string body = text.Substring(classStart, Math.Max(0, at - classStart));
            int pub = LastLabel(body, "public"), prot = LastLabel(body, "protected"), priv = LastLabel(body, "private");
            int last = Math.Max(pub, Math.Max(prot, priv));
            bool needsPublic = last < 0 ? !isStruct : last != pub;
            return (at, needsPublic);
        }

        private static int LastLabel(string body, string label)
        {
            int at = body.Length;
            while ((at = at > 0 ? body.LastIndexOf(label, at - 1, StringComparison.Ordinal) : -1) >= 0)
            {
                int after = at + label.Length;
                while (after < body.Length && (body[after] == ' ' || body[after] == '\t'))
                    after++;
                if (after < body.Length && body[after] == ':' && (after + 1 >= body.Length || body[after + 1] != ':') &&
                    (at == 0 || !IsIdent(body[at - 1])))
                    return at;
            }
            return -1;
        }

        /// <summary>
        /// One level of indentation as a file uses it: a tab if more lines are indented with tabs, else
        /// the most common step between the indentation of consecutive lines (2, 4, ... spaces);
        /// <paramref name="fallback"/> (the editor's setting) when the file says nothing.
        /// </summary>
        public static string IndentUnit(string text, string fallback)
        {
            int tabs = 0, spaces = 0;
            var steps = new Dictionary<int, int>();
            int previous = -1;
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;
                if (line[0] == '\t')
                    tabs++;
                else if (line[0] == ' ')
                    spaces++;
                int width = 0;
                while (width < line.Length && line[width] == ' ')
                    width++;
                if (width < line.Length && line[width] == '\t')
                {
                    previous = -1;
                    continue;
                }
                if (previous >= 0 && width > previous && width - previous <= 8)
                    steps[width - previous] = steps.TryGetValue(width - previous, out int n) ? n + 1 : 1;
                previous = width;
            }
            if (tabs == 0 && spaces == 0)
                return fallback;
            if (tabs >= spaces)
                return "\t";
            return steps.Count == 0 ? fallback : new string(' ', steps.OrderByDescending(s => s.Value).ThenBy(s => s.Key).First().Key);
        }

        /// <summary>The indentation of a class's members: as the member line above <paramref name="at"/> has it.</summary>
        public static string MemberIndent(string text, int at, string classIndent, string unit)
        {
            for (int line = LineStart(text, at); line > 0;)
            {
                line = LineStart(text, line - 1);
                string content = text.Substring(line, LineEnd(text, line) - line);
                if (content.Trim().Length == 0)
                    continue;
                string indent = IndentOf(text, line);
                return indent.Length > classIndent.Length ? indent : classIndent + unit;
            }
            return classIndent + unit;
        }

        /// <summary>Where to append to a file: after its last non-blank text, with blank-line separation.</summary>
        public static (int Offset, string Prefix) AppendPoint(string text, string newline)
        {
            int end = text.Length;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            return (end, end == 0 ? "" : newline + newline);
        }
    }
}
