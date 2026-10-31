using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualXtsi.Interop
{
    /// <summary>A range in a file: 1-based lines, 1-based UTF-16 columns (the end is exclusive).</summary>
    internal sealed class SourceRange
    {
        public string Path;
        public int Line, Column, EndLine, EndColumn;

        public static SourceRange Parse(string value)
        {
            string[] f = value?.Split('\t');
            if (f == null || f.Length < 5 || !int.TryParse(f[1], out int line) || !int.TryParse(f[2], out int column) ||
                !int.TryParse(f[3], out int endLine) || !int.TryParse(f[4], out int endColumn))
                return null;
            return new SourceRange { Path = f[0].Replace('/', '\\'), Line = line, Column = column, EndLine = endLine, EndColumn = endColumn };
        }

        public bool InFile(string path) => string.Equals(System.IO.Path.GetFullPath(Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What vx_cursor_info says about the declaration at a position (see vx.h).</summary>
    internal sealed class CursorInfo
    {
        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>();

        public static CursorInfo Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var info = new CursorInfo();
            foreach (string line in text.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0)
                    continue;
                string key = line.Substring(0, tab);
                if (!info._values.TryGetValue(key, out List<string> list))
                    info._values[key] = list = new List<string>();
                list.Add(line.Substring(tab + 1));
            }
            return info.Kind == null ? null : info;
        }

        private string One(string key) => _values.TryGetValue(key, out List<string> v) ? v[0] : null;
        private bool Flag(string key) => One(key) == "1";

        public string Kind => One("kind");
        public string Name => One("name");
        public string Usr => One("usr");
        public string Scope => One("scope") ?? "";
        public string Classes => One("classes") ?? "";
        public string Namespaces => One("namespaces") ?? "";
        public string ClassName => One("classname");
        public bool InClassTemplate => Flag("classtemplate");

        public SourceRange Declaration => SourceRange.Parse(One("decl"));
        public SourceRange DeclarationExtent => SourceRange.Parse(One("declextent"));
        public SourceRange Definition => SourceRange.Parse(One("def"));
        public SourceRange DefinitionExtent => SourceRange.Parse(One("defextent"));
        public SourceRange Class => SourceRange.Parse(One("class"));
        public SourceRange Body => SourceRange.Parse(One("body"));
        public bool AtDefinition => Flag("atdef");
        public bool DefinitionOnly => Flag("defonly");

        public bool IsFunction => Kind == "function" || Kind == "method" || Kind == "constructor" || Kind == "destructor";
        public bool IsMember => !string.IsNullOrEmpty(Classes);
        public string Result => One("result"); // null for constructors and destructors
        /// <summary>Each parameter as written (without its default argument), its name, and its default argument or null.</summary>
        public IReadOnlyList<(string Text, string Name, string Default)> Parameters =>
            (_values.TryGetValue("param", out List<string> v) ? v : new List<string>())
            .Select(p =>
            {
                string[] f = p.Split('\t');
                return (f[0], f.Length > 1 ? f[1] : "", f.Length > 2 ? f[2] : null);
            })
            .ToList();
        public string Qualifiers => One("qualifiers") ?? "";
        public bool IsStatic => Flag("static");
        public bool IsVirtual => Flag("virtual");
        public bool IsPure => Flag("pure");
        public bool IsTemplate => Flag("template");
        public int Ue => int.TryParse(One("ue"), out int ue) ? ue : 0;

        public string Type => One("type");
        public string TypeSpelling => One("typespelling");
        public bool IsPod => Flag("pod");
        public bool EnumScoped => Flag("enumscoped");
        public string EnumBase => One("enumbase");

        public const int UeImplementation = 1, UeValidate = 2, UeGenerated = 4;
    }
}
