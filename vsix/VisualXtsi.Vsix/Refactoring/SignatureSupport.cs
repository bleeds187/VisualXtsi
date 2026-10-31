using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>A parameter of the changed signature: where it was (-1: new) and what it is now.</summary>
    internal sealed class SignatureParameter
    {
        public int OldIndex = -1;
        public string Type = "", Name = "", Default = "", CallValue = "";
        public string OldType = "", OldName = "", OldDefault = "";

        public bool IsNew => OldIndex < 0;
        public bool Renamed => !IsNew && Name != OldName && OldName.Length > 0;
        public bool Retyped => !IsNew && Type != OldType;
    }

    /// <summary>Change Signature, on plain strings.</summary>
    internal static class SignatureSupport
    {
        /// <summary>The "(" and matching ")" after a function's name ending at <paramref name="nameEnd"/> (template arguments skipped); null if none.</summary>
        public static (int Open, int Close)? ParensAfter(string text, int nameEnd)
        {
            int i = nameEnd;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i < text.Length && text[i] == '<')
            {
                int depth = 0;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '<')
                        depth++;
                    else if (text[i] == '>' && --depth == 0)
                        break;
                    else if (text[i] == ';' || text[i] == '{')
                        return null;
                }
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
            }
            if (i >= text.Length || text[i] != '(')
                return null;
            int close = Matching(text, i);
            return close < 0 ? ((int, int)?)null : (i, close);
        }

        // The ")" matching the "(" at `open`, skipping literals and comments.
        private static int Matching(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\')
                            i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    i = Math.Max(i, text.IndexOf('\n', i) < 0 ? text.Length : text.IndexOf('\n', i));
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is int e && e >= 0 ? e + 1 : text.Length;
                else if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0)
                    return i;
            }
            return -1;
        }

        /// <summary>The items of a parameter or argument list (the text between the parentheses), trimmed; empty for "()" or "(void)".</summary>
        public static List<string> Split(string inner)
        {
            var items = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < inner.Length && inner[i] != c; i++)
                        if (inner[i] == '\\')
                            i++;
                }
                else if (c == '(' || c == '[' || c == '{' || c == '<')
                    depth++;
                else if ((c == ')' || c == ']' || c == '}' || c == '>') && depth > 0)
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    items.Add(inner.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            string last = inner.Substring(start).Trim();
            if (last.Length > 0 || items.Count > 0)
                items.Add(last);
            return items.Count == 1 && items[0] == "void" ? new List<string>() : items;
        }

        /// <summary>A parameter as written: its type, its name ("" if unnamed) and its default argument ("" if none).</summary>
        public static (string Type, string Name, string Default) Parse(string parameter)
        {
            string p = parameter.Trim(), def = "";
            int depth = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '(' || c == '<' || c == '[' || c == '{')
                    depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0)
                    depth--;
                else if (c == '=' && depth == 0)
                {
                    def = p.Substring(i + 1).Trim();
                    p = p.Substring(0, i).Trim();
                    break;
                }
            }
            Match m = Regex.Match(p, @"^(.*?[\s*&>])\s*([A-Za-z_]\w*)\s*(\[[^\]]*\]\s*)*$");
            if (m.Success && !Regex.IsMatch(m.Groups[2].Value, @"^(const|volatile|int|float|double|char|bool|long|short|unsigned|signed|void|auto)$"))
                return (Regex.Replace(m.Groups[1].Value.Trim() + m.Groups[3].Value, @"\s+", " "), m.Groups[2].Value, def);
            return (Regex.Replace(p, @"\s+", " "), "", def);
        }

        /// <summary>A parameter written from its type, name and default ("" for none).</summary>
        public static string Compose(string type, string name, string def)
        {
            string t = type.Trim();
            string head = name.Length == 0 ? t : t + " " + name;
            return def.Length == 0 ? head : head + " = " + def;
        }

        /// <summary>
        /// A declaration's or definition's new parameter list (between the parentheses): each kept
        /// parameter as this site writes it (its own name, unless renamed), with the new type when
        /// retyped; new ones as given; defaults only where <paramref name="withDefaults"/>.
        /// </summary>
        public static string ParameterList(string oldInner, IReadOnlyList<SignatureParameter> parameters, bool withDefaults)
        {
            List<string> old = Split(oldInner);
            var items = new List<string>();
            foreach (SignatureParameter p in parameters)
            {
                string def = withDefaults ? p.Default : "";
                if (p.IsNew || p.OldIndex >= old.Count)
                {
                    items.Add(Compose(p.Type, p.Name, def));
                    continue;
                }
                var (type, name, _) = Parse(old[p.OldIndex]);
                if (p.Retyped)
                    type = p.Type;
                if (p.Renamed && name.Length > 0)
                    name = p.Name;
                items.Add(Compose(type, name, def));
            }
            return string.Join(", ", items);
        }

        /// <summary>
        /// A call's new argument list: kept arguments moved along (a default the call relied on is
        /// written out when a later argument follows), new parameters given their value at the call;
        /// trailing arguments equal to nothing but defaults are left out. Null when a new parameter
        /// has neither a value for calls nor a default.
        /// </summary>
        public static string ArgumentList(string oldInner, IReadOnlyList<SignatureParameter> parameters)
        {
            List<string> old = Split(oldInner);
            var args = new List<(string Text, bool Omittable)>();
            foreach (SignatureParameter p in parameters)
            {
                if (p.IsNew)
                {
                    if (p.CallValue.Length > 0)
                        args.Add((p.CallValue, false));
                    else if (p.Default.Length > 0)
                        args.Add((p.Default, true));
                    else
                        return null;
                }
                else if (p.OldIndex < old.Count)
                    args.Add((old[p.OldIndex], false));
                else if (p.OldDefault.Length > 0 || p.Default.Length > 0)
                    args.Add((p.OldDefault.Length > 0 ? p.OldDefault : p.Default, true));
                else
                    return null;
            }
            // A default can be left out only when the parameter keeps that default and nothing follows.
            int keep = args.Count;
            while (keep > 0 && args[keep - 1].Omittable && parameters[keep - 1].Default.Length > 0 &&
                   (parameters[keep - 1].IsNew || parameters[keep - 1].Default == parameters[keep - 1].OldDefault))
                keep--;
            return string.Join(", ", args.Take(keep).Select(a => a.Text));
        }

        /// <summary>The whole-word uses of a name in a body that are not members of something else ("x.Name", "A::Name").</summary>
        public static List<int> UsesIn(string text, int start, int end, string name)
        {
            var uses = new List<int>();
            foreach (Match m in Regex.Matches(text.Substring(start, end - start), @"(?<![\w.]|->|::)\b" + Regex.Escape(name) + @"\b"))
                if (!InCommentOrString(text, start, start + m.Index))
                    uses.Add(start + m.Index);
            return uses;
        }

        // Whether `at` is inside a comment or literal that started after `from`.
        private static bool InCommentOrString(string text, int from, int at)
        {
            for (int i = from; i < at; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\')
                            i++;
                    if (i >= at)
                        return true;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int e = text.IndexOf('\n', i);
                    if (e < 0 || e >= at)
                        return true;
                    i = e;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (e < 0 || e + 2 > at)
                        return true;
                    i = e + 1;
                }
            }
            return false;
        }

        /// <summary>
        /// What a function's name at <paramref name="at"/> is part of, by the text around it:
        /// "declaration", "definition" (a body or constructor initializers follow its parameters) or
        /// "call". For uses found by text, where the parser did not say.
        /// </summary>
        public static string SiteKind(string text, int at, int close)
        {
            // Back over "A::B::" qualifiers to what comes before.
            int i = at;
            string name = Regex.Match(text.Substring(at), @"^~?\w+").Value, lastQualifier = null;
            while (true)
            {
                int j = i;
                while (j > 0 && char.IsWhiteSpace(text[j - 1]))
                    j--;
                if (j >= 2 && text[j - 1] == ':' && text[j - 2] == ':')
                {
                    j -= 2;
                    while (j > 0 && char.IsWhiteSpace(text[j - 1]))
                        j--;
                    int k = j;
                    while (k > 0 && (char.IsLetterOrDigit(text[k - 1]) || text[k - 1] == '_'))
                        k--;
                    if (k == j)
                        break; // "::Name"
                    lastQualifier ??= text.Substring(k, j - k);
                    i = k;
                    continue;
                }
                i = j;
                break;
            }
            int wordEnd = i, wordStart = i;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                wordStart--;
            string word = text.Substring(wordStart, wordEnd - wordStart);
            char before = i > 0 ? text[i - 1] : '\n';
            bool typeBefore = word.Length > 0 && !Regex.IsMatch(word, @"^(return|co_return|throw|else|case|new|delete|do|sizeof)$") ||
                              before == '*' || before == '&' || (before == '>' && !(i >= 2 && text[i - 2] == '-')) ||
                              lastQualifier != null && lastQualifier == name.TrimStart('~'); // UThing::UThing, UThing::~UThing
            if (!typeBefore)
                return "call";
            int after = close + 1;
            Match tail = Regex.Match(text.Substring(after), @"^(\s|const\b|override\b|final\b|noexcept(\s*\([^)]*\))?|volatile\b|&&?)*");
            after += tail.Length;
            return after < text.Length && (text[after] == '{' || text[after] == ':') ? "definition" : "declaration";
        }

        /// <summary>The signature as it will read: "Name(int32 A, float B = 1.f)".</summary>
        public static string Describe(string name, IEnumerable<SignatureParameter> parameters) =>
            name + "(" + string.Join(", ", parameters.Select(p => Compose(p.Type, p.Name, p.Default))) + ")";
    }
}
