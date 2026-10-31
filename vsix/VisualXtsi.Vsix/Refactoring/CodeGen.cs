using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualXtsi.Interop;

namespace VisualXtsi.Refactoring
{
    /// <summary>C++ text generated from what the engine says about a declaration.</summary>
    internal static class CodeGen
    {
        /// <summary>"Ns::UClass::" for an out-of-class definition (namespaces only when asked for).</summary>
        public static string Qualifier(CursorInfo f, bool withNamespaces)
        {
            var sb = new StringBuilder();
            if (withNamespaces && f.Namespaces.Length > 0)
                sb.Append(f.Namespaces).Append("::");
            if (f.Classes.Length > 0)
                sb.Append(f.Classes).Append("::");
            return sb.ToString();
        }

        /// <summary>"int32 UClass::Name(int32 A, const FString& B) const" (no default arguments, no virtual/static).</summary>
        public static string Signature(CursorInfo f, string name, string qualifier)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(f.Result))
                sb.Append(f.Result).Append(' ');
            sb.Append(qualifier).Append(name).Append('(').Append(string.Join(", ", f.Parameters.Select(p => p.Text))).Append(')');
            if (f.Qualifiers.Length > 0)
                sb.Append(' ').Append(f.Qualifiers);
            return sb.ToString();
        }

        /// <summary>A definition: the signature and a body (empty: an empty block).</summary>
        public static string Definition(string signature, string body, string unit, string newline)
        {
            var sb = new StringBuilder(signature).Append(newline).Append('{').Append(newline);
            if (!string.IsNullOrEmpty(body))
                foreach (string line in body.Replace("\r\n", "\n").Split('\n'))
                    sb.Append(line.Length == 0 ? "" : unit + line).Append(newline);
            return sb.Append('}').ToString();
        }

        /// <summary>
        /// The definitions a declaration needs: Name, or for an Unreal BlueprintNativeEvent or RPC
        /// Name_Implementation (and with WithValidation, Name_Validate returning true).
        /// </summary>
        public static List<(string Name, string Text)> Definitions(CursorInfo f, string qualifier, string unit, string newline)
        {
            var result = new List<(string, string)>();
            if ((f.Ue & CursorInfo.UeImplementation) != 0)
            {
                string name = f.Name + "_Implementation";
                result.Add((name, Definition(Signature(f, name, qualifier), "", unit, newline)));
                if ((f.Ue & CursorInfo.UeValidate) != 0)
                {
                    string validate = "bool " + qualifier + f.Name + "_Validate(" + string.Join(", ", f.Parameters.Select(p => p.Text)) + ")";
                    result.Add((f.Name + "_Validate", Definition(validate, "return true;", unit, newline)));
                }
            }
            else
            {
                result.Add((f.Name, Definition(Signature(f, f.Name, qualifier), "", unit, newline)));
            }
            return result;
        }

        /// <summary>A declaration for a definition, default arguments included: "int32 Name(int32 A, int32 B = 2) const;".</summary>
        public static string Declaration(CursorInfo f)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(f.Result))
                sb.Append(f.Result).Append(' ');
            sb.Append(f.Name).Append('(')
              .Append(string.Join(", ", f.Parameters.Select(p => p.Default == null ? p.Text : p.Text + " = " + p.Default))).Append(')');
            if (f.Qualifiers.Length > 0)
                sb.Append(' ').Append(f.Qualifiers);
            return sb.Append(';').ToString();
        }

        /// <summary>
        /// A definition's parameter list (from "(" to ")") without its default arguments, which move to the
        /// declaration; null if it has none.
        /// </summary>
        public static string WithoutDefaults(string parameterList, CursorInfo f)
        {
            string result = parameterList;
            foreach (var p in f.Parameters.Where(p => p.Default != null))
            {
                // The default as the engine gives it has its spaces collapsed: match any spacing.
                string pattern = @"\s*=\s*" + string.Join(@"\s*", p.Default.Split(' ').Select(System.Text.RegularExpressions.Regex.Escape));
                int nameAt = p.Name.Length > 0 ? result.IndexOf(p.Name, StringComparison.Ordinal) : -1;
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                var m = regex.Match(result, Math.Max(0, nameAt));
                if (m.Success)
                    result = result.Remove(m.Index, m.Length);
            }
            return result == parameterList ? null : result;
        }
    }
}
