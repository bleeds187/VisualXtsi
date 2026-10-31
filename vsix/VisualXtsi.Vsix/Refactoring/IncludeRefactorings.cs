using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Add Include and Add Forward Declaration for the symbol at the caret: its header found by the
    /// parser (when the name resolves) or the symbol index (when it does not yet), the include path
    /// by the Unreal module layout, placed in sorted order with X.generated.h kept last.
    /// </summary>
    internal static class IncludeRefactorings
    {
        public static bool CanAddInclude(RefactorContext c) => c != null;
        public static bool CanAddForwardDeclaration(RefactorContext c) =>
            c != null && (c.Info == null || ((c.Info.Kind == "class" || c.Info.Kind == "struct" || c.Info.Kind == "enum") && !c.Info.IsMember));

        public static async Task AddIncludeAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.View == null)
                return;
            string name = c.Info?.Name ?? WordAt(c);
            string header = null;
            if (c.Info?.Declaration != null && Documents.IsHeader(c.Info.Declaration.Path) && !c.Info.Declaration.InFile(c.Path) && !IsGenerated(c.Info.Declaration.Path))
                header = c.Info.Declaration.Path;
            else if (!string.IsNullOrEmpty(name))
                header = PickHeader(name, c.Path, typesOnly: false);
            if (header == null)
            {
                EditorCommands.Status(string.IsNullOrEmpty(name) ? "put the caret on a name" : $"no header found that declares {name}");
                return;
            }
            string include = IncludeSupport.IncludePath(header, ModuleRoot(header), c.Path, ModuleRoot(c.Path));
            string text = c.View.TextBuffer.CurrentSnapshot.GetText();
            var insertion = IncludeSupport.IncludeInsertion(text, include, Path.GetFileName(c.Path));
            if (!insertion.HasValue)
            {
                EditorCommands.Status($"\"{include}\" is already included");
                return;
            }
            if (Documents.Apply(new[] { FileEdit.Insert(c.Path, insertion.Value.Offset, insertion.Value.Text) }, "Add Include", out string error))
                EditorCommands.Status($"added #include \"{include}\"");
            else
                EditorCommands.Status(error);
        }

        public static async Task AddForwardDeclarationAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.View == null)
                return;
            string kind = null, name = null, namespaces = "", enumBase = null;
            bool scoped = false;
            CursorInfo f = c.Info;
            if (f != null && (f.Kind == "class" || f.Kind == "struct" || f.Kind == "enum"))
            {
                if (f.IsMember)
                {
                    EditorCommands.Status($"{f.Name} is nested in {f.Classes}: a nested type cannot be declared ahead");
                    return;
                }
                (kind, name, namespaces, scoped, enumBase) = (f.Kind, f.Name, f.Namespaces, f.EnumScoped, f.EnumBase);
            }
            else
            {
                string word = WordAt(c);
                SymbolInfo s = string.IsNullOrEmpty(word) ? null :
                    Engine.LookupSymbols(word, 50, SymbolLanguage.Cpp).FirstOrDefault(x => x.Definition && (x.Kind == 2 || x.Kind == 3 || x.Kind == 5) && Documents.IsHeader(x.Path));
                if (s == null)
                {
                    EditorCommands.Status(string.IsNullOrEmpty(word) ? "put the caret on a class, struct or enum name" : $"no class, struct or enum named {word} found");
                    return;
                }
                kind = s.Kind == 3 ? "struct" : s.Kind == 5 ? "enum" : "class";
                name = s.Name;
                namespaces = s.Scope ?? "";
                if (kind == "enum")
                {
                    // The index does not know an enum's base: read its declaration line.
                    string source = Documents.Read(s.Path) ?? "";
                    int at = TextScan.Offset(source, s.Line, 1);
                    Match m = Regex.Match(source.Substring(TextScan.LineStart(source, at), TextScan.LineEnd(source, at) - TextScan.LineStart(source, at)),
                                          @"enum\s+(class\s+|struct\s+)?" + Regex.Escape(name) + @"\s*(?::\s*([\w:]+))?");
                    scoped = m.Success && m.Groups[1].Success;
                    enumBase = m.Success && m.Groups[2].Success ? m.Groups[2].Value : null;
                }
            }
            if (kind == "enum" && !scoped && string.IsNullOrEmpty(enumBase))
            {
                EditorCommands.Status($"{name} is an enum without a fixed underlying type: it cannot be declared ahead");
                return;
            }
            string declaration = IncludeSupport.ForwardDeclaration(kind, name, namespaces, scoped, enumBase);
            string text = c.View.TextBuffer.CurrentSnapshot.GetText();
            var insertion = IncludeSupport.ForwardInsertion(text, declaration);
            if (!insertion.HasValue)
            {
                EditorCommands.Status($"{name} is already declared here");
                return;
            }
            if (Documents.Apply(new[] { FileEdit.Insert(c.Path, insertion.Value.Offset, insertion.Value.Text) }, "Add Forward Declaration", out string error))
                EditorCommands.Status($"added {declaration}");
            else
                EditorCommands.Status(error);
        }

        // The header that declares `name`, by the symbol index: definitions of types first; a picker for several.
        private static string PickHeader(string name, string from, bool typesOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hits = Engine.LookupSymbols(name, 50, SymbolLanguage.Cpp)
                .Where(s => Documents.IsHeader(s.Path) && !IsGenerated(s.Path) && !string.Equals(s.Path, from, StringComparison.OrdinalIgnoreCase))
                .Where(s => !typesOnly || s.Kind <= 7 || s.Kind == 13)
                .OrderBy(s => s.Kind >= 2 && s.Kind <= 7 || s.Kind == 13 ? 0 : 1)
                .ThenBy(s => s.Definition ? 0 : 1)
                .ToList();
            var files = hits.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count <= 1)
                return files.FirstOrDefault();
            var picker = new PickTargetDialog($"Add Include for {name} - VisualXtsi",
                files.Select(p => new SearchResult { Name = Path.GetFileName(p), Path = p, Icon = SearchDialog.FileIcon(p) }).ToList(), null);
            return picker.ShowModal() == true ? picker.Selected?.Path : null;
        }

        private static bool IsGenerated(string path) =>
            path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gen.h", StringComparison.OrdinalIgnoreCase);

        // The Unreal module folder of a file (the one with its *.Build.cs), or null.
        private static string ModuleRoot(string file)
        {
            for (string dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.EnumerateFiles(dir, "*.Build.cs").Any())
                    return dir;
            return null;
        }

        private static string WordAt(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return CSharp.CSharpNavigation.WordAt(c.View.Caret.Position.BufferPosition);
        }
    }
}
