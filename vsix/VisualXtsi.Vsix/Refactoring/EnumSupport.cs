using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>A switch on an enum, as vx_switch_at describes it (offsets in the file's text).</summary>
    internal sealed class SwitchShape
    {
        public int SwitchStart;          // "switch"
        public int BodyOpen, BodyClose;  // "{" and "}"
        public string EnumName;          // null: not on an enum
        public bool Scoped { get; set; }
        public string Qualifier = "";    // what to write before an enumerator here
        public List<(string Name, long Value, int Line)> Enumerators = new List<(string, long, int)>();
        public List<(long Value, int Offset, string Label)> Cases = new List<(long, int, string)>();
        public int Default = -1;         // offset of "default", or -1
    }

    /// <summary>Add Missing Case Statements, on plain strings.</summary>
    internal static class EnumSupport
    {
        private static readonly Regex Sentinel = new Regex(@"(^|_)(MAX|Max|COUNT|Count|NUM|Num)$");

        /// <summary>Whether an enumerator's name says it counts the others ("EFoo_MAX", "Count", "NUM").</summary>
        public static bool IsSentinel(string name) => Sentinel.IsMatch(name);

        /// <summary>The enumerators the switch has no case for, in declaration order: one per value, sentinels left out when asked.</summary>
        public static List<string> Missing(SwitchShape s, bool skipSentinels)
        {
            var covered = new HashSet<long>(s.Cases.Select(c => c.Value));
            var missing = new List<string>();
            foreach (var e in s.Enumerators)
            {
                if (skipSentinels && IsSentinel(e.Name))
                    continue;
                if (covered.Add(e.Value))
                    missing.Add(e.Name);
            }
            return missing;
        }

        /// <summary>
        /// The cases to add (each "case X:" with a break) and where: before "default" when nothing
        /// falls through into it from above, else at the end of the switch. Enumerators are written
        /// the way the existing cases write them, else with <see cref="SwitchShape.Qualifier"/>.
        /// </summary>
        public static (int Offset, string Text) Insertion(string text, SwitchShape s, IReadOnlyList<string> names, string unit)
        {
            string nl = TextScan.Newline(text);
            string prefix = s.Qualifier ?? "";
            foreach (var c in s.Cases)
            {
                var e = s.Enumerators.FirstOrDefault(x => x.Value == c.Value && c.Label.EndsWith(x.Name) &&
                                                         (c.Label.Length == x.Name.Length || !IsIdent(c.Label[c.Label.Length - x.Name.Length - 1])));
                if (e.Name != null)
                {
                    prefix = c.Label.Substring(0, c.Label.Length - e.Name.Length);
                    break;
                }
            }
            string switchIndent = TextScan.IndentOf(text, s.SwitchStart);
            // Without labels to follow: at the switch's own indentation, as Unreal's code has them.
            string caseIndent = s.Cases.Count > 0 ? TextScan.IndentOf(text, s.Cases[0].Offset)
                : s.Default >= 0 ? TextScan.IndentOf(text, s.Default) : switchIndent;
            var sb = new StringBuilder();
            foreach (string name in names)
                sb.Append(caseIndent).Append("case ").Append(prefix).Append(name).Append(':').Append(nl)
                  .Append(caseIndent).Append(unit).Append("break;").Append(nl);

            if (s.Default >= 0 && NothingFallsInto(text, s, s.Default))
            {
                // Sections set apart by blank lines: the new ones too.
                int line = TextScan.LineStart(text, s.Default);
                int above = line > 0 ? TextScan.LineStart(text, line - 1) : line;
                if (above < line && text.Substring(above, line - above).Trim().Length == 0)
                    sb.Append(nl);
                return (line, sb.ToString());
            }
            // The end of the body: before a "}" on its own line, else on new lines before it.
            int closeLine = TextScan.LineStart(text, s.BodyClose);
            if (text.Substring(closeLine, s.BodyClose - closeLine).Trim().Length == 0 && closeLine > s.BodyOpen)
                return (closeLine, sb.ToString());
            int at = s.BodyClose;
            while (at > s.BodyOpen + 1 && (text[at - 1] == ' ' || text[at - 1] == '\t'))
                at--;
            return (at, nl + sb.ToString() + switchIndent);
        }

        // Whether the code before a label cannot run on into it: the label opens the body, or the
        // statement before it leaves the switch (break, return, continue, throw, goto).
        private static bool NothingFallsInto(string text, SwitchShape s, int label)
        {
            int line = TextScan.LineStart(text, label);
            if (text.Substring(s.BodyOpen + 1, Math.Max(0, line - s.BodyOpen - 1)).Trim().Length == 0)
                return true;
            for (int seen = 0; line > s.BodyOpen && seen < 2;)
            {
                line = TextScan.LineStart(text, line - 1);
                string content = Regex.Replace(text.Substring(line, TextScan.LineEnd(text, line) - line), @"//.*$", "").Trim();
                if (content.Length == 0)
                    continue;
                if (Regex.IsMatch(content, @"(^|[;{:]\s*)(break|continue|return\b[^;]*|throw\b[^;]*|goto\s+\w+)\s*;$"))
                    return true;
                if (content != "}")
                    return false;
                seen++; // "}" closing a case's block: the statement before it
            }
            return false;
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
