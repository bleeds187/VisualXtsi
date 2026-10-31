using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>A variable an extracted function deals with (see vx_selection_info's in and out).</summary>
    internal sealed class ExtractVariable
    {
        public string Name, Type, Shape;      // shape: value, ref, class, array
        public bool Written, UsedOutside;
        public int DeclStart = -1, DeclEnd = -1; // out: its declaration ("int32 X = ...") in the text
    }

    /// <summary>What Extract Method is asked to do; offsets in <see cref="Text"/>.</summary>
    internal sealed class ExtractRequest
    {
        public string Text;
        public int Start, End;                 // the expression, or the whole statements (with their ";")
        public bool Expression;
        public string ExpressionType;
        public List<ExtractVariable> In = new List<ExtractVariable>(), Out = new List<ExtractVariable>();
        public string Name, Qualifier = "";    // "UThing::" for a member defined out of its class
        public bool Const, Static;
        public string Unit = "    ", Newline = "\n";
    }

    /// <summary>Extract Method's text: the new function and the call that replaces the selection.</summary>
    internal sealed class ExtractResult
    {
        public string Error;
        public string Signature;   // "int32 Name(float A, int32& Count) const", static included
        public string Definition;  // "int32 UThing::Name(...) const\n{\n...\n}" (no static: added where it is placed)
        public string Call;        // replaces the selection; continuation lines without indentation
    }

    /// <summary>Introduce Variable and Extract Method, on plain strings.</summary>
    internal static class ExtractSupport
    {
        /// <summary>A type as written in C++ code: "TArray<int32> &" -> "TArray<int32>&".</summary>
        public static string NormalizeType(string type) => Regex.Replace((type ?? "").Trim(), @"\s+([&*])", "$1");

        /// <summary>
        /// The parameter that carries a variable into an extracted function: a reference when the
        /// function changes it and it is used outside (or it is a reference already), a copy for
        /// simple types and for class types the function changes but nobody needs back, else a const
        /// reference. Null for an array (cannot be passed as it is).
        /// </summary>
        public static string Parameter(ExtractVariable v)
        {
            string type = NormalizeType(v.Type);
            string bare = Regex.Replace(type, @"^const\s+", "");
            switch (v.Shape)
            {
                case "array":
                    return null;
                case "ref":
                    return type + " " + v.Name;
                case "value":
                    return (v.Written && v.UsedOutside ? bare + "&" : bare) + " " + v.Name;
                default:
                    if (v.Written)
                        return (v.UsedOutside ? bare + "&" : bare) + " " + v.Name;
                    return (type.StartsWith("const ") ? type : "const " + type) + "& " + v.Name;
            }
        }

        /// <summary>A variable's declaration made an assignment: "int32 X = 1" -> "X = 1"; "FVector V(1, 2, 3)" -> "V = FVector(1, 2, 3)"; "T X" -> "X = {}".</summary>
        public static string OutAssignment(string declaration, string name, string type)
        {
            Match m = Regex.Match(declaration, @"\b" + Regex.Escape(name) + @"\b(?=\s*(=|\(|\{|$))");
            if (!m.Success)
                return null;
            string rest = declaration.Substring(m.Index + m.Length).TrimStart();
            if (rest.StartsWith("="))
                return name + " " + rest;
            if (rest.StartsWith("(") || rest.StartsWith("{"))
                return name + " = " + NormalizeType(type) + rest;
            return name + " = {}";
        }

        public static ExtractResult ExtractMethod(ExtractRequest r)
        {
            var result = new ExtractResult();
            string nl = r.Newline;
            var parameters = new List<string>();
            foreach (ExtractVariable v in r.In)
            {
                string p = Parameter(v);
                if (p == null)
                    return new ExtractResult { Error = $"{v.Name} is an array; it cannot be passed as it is" };
                parameters.Add(p);
            }
            var arguments = r.In.Select(v => v.Name).ToList();
            string region = r.Text.Substring(r.Start, r.End - r.Start);
            string returnType = "void", body, call, tail = null;
            if (r.Expression)
            {
                returnType = NormalizeType(r.ExpressionType);
                body = "return " + region.Trim() + ";";
                call = r.Name + "(" + string.Join(", ", arguments) + ")";
            }
            else if (r.Out.Count == 1)
            {
                ExtractVariable v = r.Out[0];
                if (v.Shape == "ref")
                    return new ExtractResult { Error = $"{v.Name} is a reference declared in the selection; it cannot be returned" };
                returnType = NormalizeType(v.Type);
                body = region;
                tail = "return " + v.Name + ";";
                call = returnType + " " + v.Name + " = " + r.Name + "(" + string.Join(", ", arguments) + ");";
            }
            else
            {
                // Several results: declared before the call, passed by reference, assigned inside.
                var declarations = new List<string>();
                foreach (ExtractVariable v in r.Out.OrderByDescending(v => v.DeclStart))
                {
                    if (v.Shape == "ref" || v.Shape == "array" || v.DeclStart < r.Start || v.DeclEnd > r.End)
                        return new ExtractResult { Error = $"{v.Name} is declared in the selection and used after it; it cannot be passed out" };
                    string decl = r.Text.Substring(v.DeclStart, v.DeclEnd - v.DeclStart);
                    string assignment = OutAssignment(decl, v.Name, v.Type);
                    if (assignment == null)
                        return new ExtractResult { Error = $"the declaration of {v.Name} was not understood" };
                    region = region.Substring(0, v.DeclStart - r.Start) + assignment + region.Substring(v.DeclEnd - r.Start);
                }
                foreach (ExtractVariable v in r.Out)
                {
                    parameters.Add(NormalizeType(v.Type) + "& " + v.Name);
                    arguments.Add(v.Name);
                    declarations.Add(NormalizeType(v.Type) + " " + v.Name + "{};");
                }
                body = region;
                call = string.Concat(declarations.Select(d => d + nl)) + r.Name + "(" + string.Join(", ", arguments) + ");";
            }
            string list = "(" + string.Join(", ", parameters) + ")" + (r.Const ? " const" : "");
            result.Signature = (r.Static ? "static " : "") + returnType + " " + r.Name + list;
            result.Definition = returnType + " " + r.Qualifier + r.Name + list + nl + "{" + nl + TextScan.Reindent(body, r.Unit, nl) +
                                (tail != null ? nl + r.Unit + tail : "") + nl + "}";
            result.Call = call;
            return result;
        }

        /// <summary>Lines prefixed with an indentation (blank lines left empty).</summary>
        public static string Indent(string block, string indent, string nl) =>
            string.Join(nl, block.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim().Length == 0 ? "" : indent + l));
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
