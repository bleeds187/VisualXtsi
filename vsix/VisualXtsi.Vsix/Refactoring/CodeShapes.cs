using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualXtsi.Interop;

namespace VisualXtsi.Refactoring
{
    /// <summary>The text of Document Method, Encapsulate Field and Add/Remove Braces, on plain strings.</summary>
    internal static class CodeShapes
    {
        /// <summary>
        /// A doc comment for a function, at <paramref name="indent"/>: style 0 "/** ... */" with " * " lines,
        /// 1 "///" lines; a summary line (where the caret goes: the offset returned), then @param
        /// per parameter and @return unless it returns nothing.
        /// </summary>
        public static (string Text, int Caret) DocComment(CursorInfo f, int style, string indent, string nl)
        {
            // The summary line keeps its trailing space: the caret goes there, ready for typing.
            string prefix = style == 1 ? "/// " : " * ";
            var lines = new List<string> { prefix };
            var parameters = f.Parameters.Where(p => p.Name.Length > 0).ToList();
            bool returns = !string.IsNullOrEmpty(f.Result) && f.Result.Trim() != "void";
            if (parameters.Count > 0 || returns)
                lines.Add(prefix.TrimEnd());
            foreach (var p in parameters)
                lines.Add(prefix + "@param " + p.Name);
            if (returns)
                lines.Add(prefix + "@return");
            var sb = new StringBuilder();
            if (style != 1)
                sb.Append(indent).Append("/**").Append(nl);
            int caret = -1;
            foreach (string line in lines)
            {
                sb.Append(indent).Append(line);
                if (caret < 0)
                    caret = sb.Length;
                sb.Append(nl);
            }
            if (style != 1)
                sb.Append(indent).Append(" */").Append(nl);
            return (sb.ToString(), caret);
        }

        /// <summary>The name accessors are built on: without "m_" or a leading "_", capitalized.</summary>
        public static string AccessorBase(string field)
        {
            string name = field.StartsWith("m_") ? field.Substring(2) : field.TrimStart('_');
            return name.Length == 0 ? field : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Getter and setter declarations (inline bodies) for a field, each at <paramref name="indent"/>:
        /// a bool "bName" gets IsName / SetName(bool bInName), others GetName / SetName(InName), by value
        /// for simple types and by const reference otherwise. With a Blueprint category, they get
        /// UFUNCTION(BlueprintPure) and UFUNCTION(BlueprintCallable). Accessors in <paramref name="existing"/>
        /// are left out.
        /// </summary>
        public static (string Text, string Getter, string Setter) Accessors(string field, string type, bool pod, bool isStatic,
            string blueprintCategory, ICollection<string> existing, string indent, string nl)
        {
            bool flag = type.Trim() == "bool" && field.Length > 1 && field[0] == 'b' && char.IsUpper(field[1]);
            string baseName = flag ? field.Substring(1) : AccessorBase(field);
            string getter = (flag ? "Is" : "Get") + baseName, setter = "Set" + baseName;
            string param = flag ? "bIn" + baseName : "In" + baseName;
            string stat = isStatic ? "static " : "";
            string byRef = pod ? type : "const " + type + "&";
            var sb = new StringBuilder();
            if (existing == null || !existing.Contains(getter))
            {
                if (blueprintCategory != null)
                    sb.Append(indent).Append("UFUNCTION(BlueprintPure, Category = \"").Append(blueprintCategory).Append("\")").Append(nl);
                sb.Append(indent).Append(stat).Append(byRef).Append(' ').Append(getter).Append("()").Append(isStatic ? "" : " const")
                  .Append(" { return ").Append(field).Append("; }").Append(nl);
            }
            if (existing == null || !existing.Contains(setter))
            {
                if (blueprintCategory != null)
                    sb.Append(indent).Append("UFUNCTION(BlueprintCallable, Category = \"").Append(blueprintCategory).Append("\")").Append(nl);
                sb.Append(indent).Append(stat).Append("void ").Append(setter).Append('(').Append(byRef).Append(' ').Append(param).Append(") { ")
                  .Append(field).Append(" = ").Append(param).Append("; }").Append(nl);
            }
            return (sb.ToString(), getter, setter);
        }

        /// <summary>
        /// Removing the braces of a block holding one statement: the replacement of the block
        /// (from its "{" to its "}") with the statement on its own line, one level in from
        /// <paramref name="ownerIndent"/> (the if / for / else line''s indentation).
        /// </summary>
        public static (int Start, int Length, string Text) RemoveBraces(string text, int open, int close, int innerStart, int innerEnd,
            string ownerIndent, string unit, string nl)
        {
            int statementEnd = SemicolonAfter(text, innerEnd);
            string statement = TextScan.Reindent(text.Substring(innerStart, statementEnd - innerStart), ownerIndent + unit, nl);
            int start = open, end = close + 1;
            string replacement;
            int lineStart = TextScan.LineStart(text, open);
            if (text.Substring(lineStart, open - lineStart).Trim().Length == 0)
            {
                start = lineStart; // "{" on its own line: the line goes
                replacement = statement;
            }
            else
            {
                while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t'))
                    start--; // "if (x) {": the statement goes on the next line
                replacement = nl + statement;
            }
            // "} else": else moves to its own line.
            int after = end;
            while (after < text.Length && (text[after] == ' ' || text[after] == '\t'))
                after++;
            if (after < text.Length && text[after] != '\r' && text[after] != '\n')
            {
                replacement += nl + ownerIndent;
                end = after;
            }
            return (start, end - start, replacement);
        }

        /// <summary>
        /// Adding braces around a statement body (<paramref name="bodyStart"/> to <paramref name="bodyEnd"/>,
        /// its semicolon taken along): Allman style, the braces at <paramref name="ownerIndent"/>.
        /// </summary>
        public static (int Start, int Length, string Text) AddBraces(string text, int bodyStart, int bodyEnd, string ownerIndent, string unit, string nl)
        {
            int end = SemicolonAfter(text, bodyEnd);
            string statement = TextScan.Reindent(text.Substring(bodyStart, end - bodyStart), ownerIndent + unit, nl);
            int lineStart = TextScan.LineStart(text, bodyStart);
            int start;
            string replacement;
            if (text.Substring(lineStart, bodyStart - lineStart).Trim().Length == 0)
            {
                start = lineStart; // the body on its own line
                replacement = ownerIndent + "{" + nl + statement + nl + ownerIndent + "}";
            }
            else
            {
                start = bodyStart; // "if (x) Do();": braces on their own lines
                while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t'))
                    start--;
                replacement = nl + ownerIndent + "{" + nl + statement + nl + ownerIndent + "}";
            }
            int after = end;
            while (after < text.Length && (text[after] == ' ' || text[after] == '\t'))
                after++;
            if (after < text.Length && text[after] != '\r' && text[after] != '\n')
            {
                replacement += nl + ownerIndent; // "...; else": else on its own line
                end = after;
            }
            return (start, end - start, replacement);
        }

        // Past the ";" that ends a statement whose extent stops before it.
        private static int SemicolonAfter(string text, int at)
        {
            int i = at;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return i < text.Length && text[i] == ';' ? i + 1 : at;
        }
    }
}
