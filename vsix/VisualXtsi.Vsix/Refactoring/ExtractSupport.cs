using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>Introduce Variable (and Extract Method), on plain strings.</summary>
    internal static class ExtractSupport
    {
        /// <summary>
        /// A name for the value of an expression, Unreal style: the function called ("GetHealth()" ->
        /// "Health") or the last name in it, capitalized; bools get a "b" prefix; "Value" otherwise.
        /// </summary>
        public static string SuggestName(string expression, string type)
        {
            string e = expression.Trim();
            string name = null;
            Match call = Regex.Match(e, @"([A-Za-z_]\w*)\s*(<[^<>]*>)?\s*\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)\s*$");
            if (call.Success)
                name = Regex.Replace(call.Groups[1].Value, @"^(Get|Is|Has|Calc|Calculate|Compute|Find|Make)(?=[A-Z])", "");
            else if (Regex.Match(e, @"^[A-Za-z_][\w]*(\s*(\.|->|::)\s*[A-Za-z_]\w*)*$") is Match path && path.Success)
                name = Regex.Match(e, @"([A-Za-z_]\w*)$").Value; // a name, or a member of one
            if (string.IsNullOrEmpty(name) || RenameSupport.Validate(name, null) != null)
                name = "Value";
            name = name.TrimStart('_');
            if (name.StartsWith("m_"))
                name = name.Substring(2);
            if (name.Length == 0)
                name = "Value";
            bool flagged = name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]);
            if (!flagged)
                name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            if ((type ?? "").Trim() == "bool" && !flagged)
                name = "b" + name;
            return name;
        }

        /// <summary>
        /// The type a new constant is declared with: "const auto", or the expression's own type made
        /// const as auto would (a pointer itself, "T* const", not what it points to).
        /// </summary>
        public static string VariableType(string type, bool explicitType)
        {
            if (!explicitType || string.IsNullOrWhiteSpace(type))
                return "const auto";
            string t = Regex.Replace(type.Trim(), @"\s+([&*])", "$1");
            if (t.EndsWith("*"))
                return t + " const";
            return t.StartsWith("const ") ? t : "const " + t;
        }

        /// <summary>
        /// Introduce Variable's edits: "Type Name = expression;" on its own line before the earliest
        /// statement involved (at its indentation), and every occurrence replaced by the name.
        /// <paramref name="occurrences"/>: each occurrence's range and the start of its statement.
        /// </summary>
        public static List<TextEdit> IntroduceVariable(string text, IReadOnlyList<(int Start, int End, int Statement)> occurrences, string name, string type, string nl)
        {
            var first = occurrences.OrderBy(o => o.Statement).First();
            int at = TextScan.LineStart(text, first.Statement);
            string indent = TextScan.IndentOf(text, first.Statement);
            string expression = text.Substring(occurrences[0].Start, occurrences[0].End - occurrences[0].Start).Trim();
            var edits = new List<TextEdit> { new TextEdit(at, 0, indent + type + " " + name + " = " + expression + ";" + nl) };
            edits.AddRange(occurrences.Select(o => new TextEdit(o.Start, o.End - o.Start, name)));
            return edits;
        }
    }
}
