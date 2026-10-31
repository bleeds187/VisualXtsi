using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Create Implementation, Create Method Implementations, Create Declaration and Move
    /// Implementation (to the source file, to the header): definitions and declarations between a
    /// header and its source, placed among their neighbors in declaration order.
    /// </summary>
    internal static class ImplementationRefactorings
    {
        // ---- Menu states (by the last parse; null info: unknown, so allowed and checked when run)

        public static bool CanCreateImplementation(RefactorContext c) =>
            c != null && (c.Info == null || (c.Info.IsFunction && !c.Info.AtDefinition && !c.Info.IsPure && c.Info.Definition == null));

        public static bool CanCreateDeclaration(RefactorContext c) =>
            c != null && (c.Info == null || (c.Info.IsFunction && c.Info.AtDefinition && c.Info.DefinitionOnly));

        public static bool CanCreateMethodImplementations(RefactorContext c) =>
            c != null && c.InHeader && (c.Info == null || c.Info.IsMember || c.Info.Kind == "class" || c.Info.Kind == "struct");

        public static bool CanMoveToSource(RefactorContext c) =>
            c != null && c.InHeader && (c.Info == null || (c.Info.IsFunction && c.Info.AtDefinition && c.Info.Body != null));

        public static bool CanMoveToHeader(RefactorContext c) =>
            c != null && !c.InHeader && (c.Info == null || (c.Info.IsFunction && c.Info.AtDefinition && c.Info.Body != null && !c.Info.DefinitionOnly));

        // ---- Create Implementation

        public static async Task CreateImplementationAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (!Check(f != null && f.IsFunction && !f.AtDefinition, "put the caret on a function declaration") ||
                !Check(!f.IsPure, $"{f.Name} is pure virtual, defaulted or deleted") ||
                !Check((f.Ue & CursorInfo.UeGenerated) == 0, $"{f.Name} is a BlueprintImplementableEvent: UnrealHeaderTool writes its body") ||
                !Check(f.Definition == null, $"{f.Name} already has a definition") ||
                !Check(!f.IsTemplate && !f.InClassTemplate, "templates are defined in headers; nothing to generate"))
                return;
            if (ExistingDefinition(f) is SymbolInfo existing)
            {
                EditorCommands.Status($"{f.Name} is already defined in {Path.GetFileName(existing.Path)}({existing.Line})");
                return;
            }
            string declFile = f.Declaration?.Path ?? c.Path;
            string target = Documents.IsHeader(declFile) ? Documents.SourceFor(declFile) : declFile;
            if (!Check(target != null, $"no source file found for {Path.GetFileName(declFile)}"))
                return;
            string text = Documents.Read(target);
            string nl = TextScan.Newline(text);
            Placement place = PlaceDefinition(text, target, declFile, f, null, nl);
            var defs = CodeGen.Definitions(f, CodeGen.Qualifier(f, place.WithNamespaces), SurroundWith.IndentUnit(c.View), nl);
            string code = place.Prefix + string.Join(nl + nl, defs.Select(d => d.Text)) + place.Suffix;
            if (!Documents.Apply(new[] { FileEdit.Insert(target, place.Offset, code) }, "Create Implementation", out string error))
            {
                EditorCommands.Status(error);
                return;
            }
            OpenAt(target, text.Insert(place.Offset, code), place.Offset + place.Prefix.Length + defs[0].Text.IndexOf('{') + 1);
        }

        // ---- Create Method Implementations

        public static async Task CreateMethodImplementationsAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c == null || c.Snapshot == null)
                return;
            if (!Check(c.InHeader, "put the caret in a class in its header"))
                return;
            string header = c.Path;
            var (cls, methods) = await Task.Run(() => Engine.ClassMembers(header, c.Line, c.Column, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Check(cls != null, "put the caret in a class"))
                return;
            string target = Documents.SourceFor(header);
            if (!Check(target != null, $"no source file found for {Path.GetFileName(header)}"))
                return;
            var missing = new List<CursorInfo>();
            foreach (var m in methods.Where(m => !m.Inline))
            {
                CursorInfo f = await Task.Run(() => Engine.CursorInfo(header, m.Line, m.Column, wait: true));
                if (f != null && f.IsFunction && !f.IsPure && (f.Ue & CursorInfo.UeGenerated) == 0 && !f.IsTemplate && !f.InClassTemplate &&
                    f.Definition == null && ExistingDefinition(f) == null)
                    missing.Add(f);
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Check(missing.Count > 0, $"every method of {cls} has a definition"))
                return;

            string text = Documents.Read(target);
            string nl = TextScan.Newline(text);
            string unit = SurroundWith.IndentUnit(c.View);
            var items = missing.Select(f => new PreviewItem
            {
                Group = target,
                Label = CodeGen.Signature(f, f.Name, ""),
                Diff = string.Join("\n", CodeGen.Definitions(f, CodeGen.Qualifier(f, false), unit, "\n").SelectMany(d => d.Text.Split('\n')).Select(l => "+ " + l)),
                Tag = f,
            }).ToList();
            var dialog = new RefactorPreviewDialog("Create Method Implementations",
                $"Definitions to add to {Path.GetFileName(target)} for the methods of {cls} that have none, each next to its neighbors:", items, "_Create");
            if (dialog.ShowModal() != true || dialog.Accepted.Count == 0)
                return;

            // In declaration order, each after the previous one created (or its neighbors already defined).
            var placed = new Dictionary<string, int>();
            var inserts = new List<(int Offset, string Code)>();
            foreach (CursorInfo f in dialog.Accepted.Select(i => (CursorInfo)i.Tag))
            {
                Placement place = PlaceDefinition(text, target, header, f, placed, nl);
                string code = place.Prefix + string.Join(nl + nl, CodeGen.Definitions(f, CodeGen.Qualifier(f, place.WithNamespaces), unit, nl).Select(d => d.Text)) + place.Suffix;
                inserts.Add((place.Offset, code));
                placed[f.Name] = place.Offset;
            }
            var edits = inserts.GroupBy(i => i.Offset).Select(g => FileEdit.Insert(target, g.Key, string.Concat(g.Select(i => i.Code)))).ToList();
            if (!Documents.Apply(edits, "Create Method Implementations", out string error))
                EditorCommands.Status(error);
            else
                EditorCommands.Status($"{dialog.Accepted.Count} definitions added to {Path.GetFileName(target)}");
        }

        // ---- Create Declaration

        public static async Task CreateDeclarationAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (!Check(f != null && f.IsFunction && f.AtDefinition, "put the caret on a function definition") ||
                !Check(f.DefinitionOnly, $"{f.Name} is already declared"))
                return;
            string unit = SurroundWith.IndentUnit(c.View);
            string file, text, code;
            int at;
            if (f.IsMember && f.Class != null)
            {
                file = f.Class.Path;
                text = Documents.Read(file);
                string nl = TextScan.Newline(text);
                int classStart = TextScan.Offset(text, f.Class.Line, f.Class.Column);
                int classEnd = TextScan.Offset(text, f.Class.EndLine, f.Class.EndColumn);
                int brace = text.IndexOf('{', classStart);
                string head = text.Substring(classStart, (brace < 0 ? text.Length : brace) - classStart);
                bool isStruct = System.Text.RegularExpressions.Regex.IsMatch(head, @"\bstruct\b");
                var (offset, needsPublic) = TextScan.MemberInsertion(text, classStart, classEnd, isStruct);
                string classIndent = TextScan.IndentOf(text, classStart);
                at = offset;
                code = (needsPublic ? classIndent + "public:" + nl : "") + MemberIndent(text, offset, classIndent, unit) + CodeGen.Declaration(f) + nl;
            }
            else
            {
                file = Documents.HeaderFor(c.Path);
                if (!Check(file != null, $"no header found for {Path.GetFileName(c.Path)}"))
                    return;
                text = Documents.Read(file);
                string nl = TextScan.Newline(text);
                var (offset, prefix) = TextScan.AppendPoint(text, nl);
                at = offset;
                code = prefix + CodeGen.Declaration(f);
            }
            var declaration = FileEdit.Insert(file, at, code);

            // Default arguments belong to the declaration: they leave the definition.
            string source = c.Snapshot.GetText();
            int nameAt = c.OffsetOf(f.Definition.Line, f.Definition.Column);
            int open = source.IndexOf('(', nameAt);
            int close = open < 0 ? -1 : MatchingParen(source, open);
            string list = close > open ? source.Substring(open, close + 1 - open) : null;
            string stripped = list == null ? null : CodeGen.WithoutDefaults(list, f);
            if (stripped != null)
            {
                await PreviewAndApplyAsync("Create Declaration", $"Declare {f.Name} in {Path.GetFileName(file)}; its default arguments move there:",
                    new List<(FileEdit, string)> { (declaration, text), (FileEdit.Replace(c.Path, source, open, list.Length, stripped), source) });
                return;
            }
            if (!Documents.Apply(new[] { declaration }, "Create Declaration", out string error))
            {
                EditorCommands.Status(error);
                return;
            }
            OpenAt(file, text.Insert(at, code), at + code.TrimEnd().Length);
        }

        // The indentation of the class's members: as the member line above the insertion point has it.
        private static string MemberIndent(string text, int at, string classIndent, string unit)
        {
            for (int line = TextScan.LineStart(text, at); line > 0;)
            {
                line = TextScan.LineStart(text, line - 1);
                string content = text.Substring(line, TextScan.LineEnd(text, line) - line);
                if (content.Trim().Length == 0)
                    continue;
                string indent = TextScan.IndentOf(text, line);
                return indent.Length > classIndent.Length ? indent : classIndent + unit;
            }
            return classIndent + unit;
        }

        private static int MatchingParen(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')' && --depth == 0)
                    return i;
            }
            return -1;
        }

        // ---- Move Implementation to Source File

        public static async Task MoveToSourceAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (!Check(f != null && f.IsFunction && f.AtDefinition && f.Body != null && c.InHeader, "put the caret on a function defined in a header") ||
                !Check(!f.IsTemplate && !f.InClassTemplate, "templates must stay in headers"))
                return;
            string target = Documents.SourceFor(c.Path);
            if (!Check(target != null, $"no source file found for {Path.GetFileName(c.Path)}"))
                return;
            string header = c.Snapshot.GetText();
            string nl = TextScan.Newline(header);
            int bodyStart = c.OffsetOf(f.Body.Line, f.Body.Column), bodyEnd = c.OffsetOf(f.Body.EndLine, f.Body.EndColumn);
            int before = bodyStart;
            while (before > 0 && char.IsWhiteSpace(header[before - 1]))
                before--;
            var headerEdits = new List<FileEdit> { FileEdit.Replace(c.Path, header, before, bodyEnd - before, ";") };
            // An inline function defined in one source file cannot be called from the others: drop inline.
            if (f.DefinitionExtent != null)
            {
                int declStart = c.OffsetOf(f.DefinitionExtent.Line, f.DefinitionExtent.Column);
                int nameAt = c.OffsetOf(f.Definition.Line, f.Definition.Column);
                string prefix = header.Substring(declStart, Math.Max(0, nameAt - declStart));
                string stripped = System.Text.RegularExpressions.Regex.Replace(prefix, @"\b(FORCEINLINE|inline)\s+", "");
                if (stripped != prefix)
                    headerEdits.Insert(0, FileEdit.Replace(c.Path, header, declStart, prefix.Length, stripped));
            }
            string body = Expand(header.Substring(bodyStart, bodyEnd - bodyStart), SurroundWith.IndentUnit(c.View), nl);
            string text = Documents.Read(target);
            string targetNl = TextScan.Newline(text);
            Placement place = PlaceDefinition(text, target, c.Path, f, null, targetNl);
            string code = place.Prefix + CodeGen.Signature(f, f.Name, CodeGen.Qualifier(f, place.WithNamespaces)) + targetNl +
                          TextScan.Reindent(body, "", targetNl) + place.Suffix;
            var sourceEdit = FileEdit.Insert(target, place.Offset, code);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await PreviewAndApplyAsync("Move Implementation to Source File", $"Move the body of {f.Name} to {Path.GetFileName(target)}:",
                headerEdits.Select(e => (e, header)).Append((sourceEdit, text)).ToList());
        }

        // ---- Move Implementation to Header File

        public static async Task MoveToHeaderAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (!Check(f != null && f.IsFunction && f.AtDefinition && f.Body != null && !c.InHeader, "put the caret on a function defined in a source file") ||
                !Check(f.Declaration != null && Documents.IsHeader(f.Declaration.Path) && f.DeclarationExtent != null,
                       $"{f?.Name} has no declaration in a header"))
                return;
            string source = c.Snapshot.GetText();
            int defStart = c.OffsetOf(f.DefinitionExtent.Line, f.DefinitionExtent.Column);
            int bodyStart = c.OffsetOf(f.Body.Line, f.Body.Column), bodyEnd = c.OffsetOf(f.Body.EndLine, f.Body.EndColumn);
            int delStart = TextScan.LineStart(source, defStart);
            int delEnd = TextScan.NextLineStart(source, bodyEnd);
            // Take one of the blank lines around it along, so no double blank line is left.
            if (delEnd < source.Length && source.Substring(delEnd, TextScan.LineEnd(source, delEnd) - delEnd).Trim().Length == 0)
                delEnd = TextScan.NextLineStart(source, delEnd);
            else if (delStart > 0 && source.Substring(TextScan.LineStart(source, delStart - 1), delStart - TextScan.LineStart(source, delStart - 1)).Trim().Length == 0)
                delStart = TextScan.LineStart(source, delStart - 1);
            var sourceEdit = FileEdit.Replace(c.Path, source, delStart, delEnd - delStart, "");

            string headerPath = f.DeclarationExtent.Path;
            string header = Documents.Read(headerPath);
            string nl = TextScan.Newline(header);
            int declStart = TextScan.Offset(header, f.DeclarationExtent.Line, f.DeclarationExtent.Column);
            int declEnd = TextScan.Offset(header, f.DeclarationExtent.EndLine, f.DeclarationExtent.EndColumn);
            int semi = header.IndexOf(';', declEnd);
            if (!Check(semi >= 0 && header.Substring(declEnd, semi - declEnd).Trim().Length == 0,
                       $"the declaration of {f.Name} in {Path.GetFileName(headerPath)} has changed; save and try again"))
                return;
            string indent = TextScan.IndentOf(header, declStart);
            string body = TextScan.Reindent(Expand(source.Substring(bodyStart, bodyEnd - bodyStart), SurroundWith.IndentUnit(c.View), nl), indent, nl);
            var headerEdits = new List<FileEdit> { FileEdit.Replace(headerPath, header, declEnd, semi + 1 - declEnd, nl + body) };
            if (!f.IsMember) // a free function defined in a header must be inline
                headerEdits.Insert(0, FileEdit.Insert(headerPath, declStart, "inline "));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await PreviewAndApplyAsync("Move Implementation to Header File", $"Move the body of {f.Name} to {Path.GetFileName(headerPath)}:",
                headerEdits.Select(e => (e, header)).Append((sourceEdit, source)).ToList());
        }

        // ---- Shared

        private sealed class Placement
        {
            public int Offset;
            public string Prefix = "", Suffix = "";
            public bool WithNamespaces;
        }

        /// <summary>
        /// Where a new definition goes in <paramref name="text"/> (the source file): after the definition of
        /// the nearest method declared before it that has one, else before the nearest declared after it,
        /// else at the end of the file (fully qualified there). <paramref name="placed"/>: definitions
        /// created in this same run (name -> offset), which count as neighbors too.
        /// </summary>
        private static Placement PlaceDefinition(string text, string target, string declFile, CursorInfo f, Dictionary<string, int> placed, string nl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (f.IsMember && f.Declaration != null)
            {
                var (_, methods) = Engine.ClassMembers(declFile, f.Declaration.Line, f.Declaration.Column, wait: true);
                int index = methods.FindIndex(m => m.Line == f.Declaration.Line && m.Name == f.Name);
                if (index >= 0)
                {
                    for (int j = index - 1; j >= 0; j--)
                    {
                        if (placed != null && placed.TryGetValue(methods[j].Name, out int after))
                            return new Placement { Offset = after, Prefix = nl + nl };
                        int at = FindDefinition(text, target, methods[j].Name, f.Scope);
                        int end = at >= 0 ? TextScan.BlockEnd(text, at) : -1;
                        if (end > 0)
                            return new Placement { Offset = end, Prefix = nl + nl };
                    }
                    for (int j = index + 1; j < methods.Count; j++)
                    {
                        int at = FindDefinition(text, target, methods[j].Name, f.Scope);
                        if (at >= 0)
                            return new Placement { Offset = DefinitionStart(text, at), Suffix = nl + nl };
                    }
                }
            }
            var (offset, prefix) = TextScan.AppendPoint(text, nl);
            return new Placement { Offset = offset, Prefix = prefix, WithNamespaces = true };
        }

        // The offset of the name of a definition of `name` (or its _Implementation) in `scope`, in this text.
        private static int FindDefinition(string text, string path, string name, string scope)
        {
            foreach (string n in new[] { name, name + "_Implementation" })
                foreach (SymbolInfo s in Engine.LookupSymbols(n, 50, SymbolLanguage.Cpp))
                {
                    if (!s.Definition || (s.Scope ?? "") != scope || !string.Equals(Path.GetFullPath(s.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        continue;
                    int at = TextScan.Offset(text, s.Line, s.Column);
                    if (at + n.Length <= text.Length && text.Substring(at, n.Length) == n) // the index may be older than unsaved edits
                        return at;
                }
            return -1;
        }

        // The start of a definition whose name is at `nameAt`: its first line, with a return type or
        // template line and comments right above it.
        private static int DefinitionStart(string text, int nameAt)
        {
            int start = TextScan.LineStart(text, nameAt);
            while (start > 0)
            {
                int prev = TextScan.LineStart(text, start - 1);
                string line = text.Substring(prev, start - prev).Trim();
                if (line.Length == 0 || line.EndsWith("}") || line.EndsWith(";") || line.EndsWith("{"))
                    break;
                start = prev;
            }
            return start;
        }

        private static SymbolInfo ExistingDefinition(CursorInfo f)
        {
            string name = (f.Ue & CursorInfo.UeImplementation) != 0 ? f.Name + "_Implementation" : f.Name;
            return Engine.LookupSymbols(name, 50, SymbolLanguage.Cpp)
                .FirstOrDefault(s => s.Definition && (s.Scope ?? "") == f.Scope && !s.Path.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase));
        }

        // "{ return X; }" on one line becomes a block.
        private static string Expand(string body, string unit, string nl)
        {
            string trimmed = body.Trim();
            if (trimmed.Contains('\n') || !trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
                return body;
            string inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return inner.Length == 0 ? "{" + nl + "}" : "{" + nl + unit + inner + nl + "}";
        }

        private static async Task PreviewAndApplyAsync(string title, string intro, List<(FileEdit Edit, string Text)> edits)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var items = edits.Select(e => new PreviewItem
            {
                Group = e.Edit.Path,
                Label = $"line {TextScan.LineOf(e.Text, e.Edit.Start)}: " + (e.Edit.NewText.Length == 0 ? "remove" : e.Edit.Length == 0 ? "insert" : "replace"),
                Diff = RefactorPreviewDialog.Diff(e.Text, e.Edit.Start, e.Edit.Length, e.Edit.NewText),
                Tag = e.Edit,
            }).ToList();
            var dialog = new RefactorPreviewDialog(title, intro, items);
            if (dialog.ShowModal() != true || dialog.Accepted.Count == 0)
                return;
            if (!Documents.Apply(dialog.Accepted.Select(i => (FileEdit)i.Tag).ToList(), title, out string error))
                EditorCommands.Status(error);
        }

        private static void OpenAt(string path, string text, int offset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int line = TextScan.LineOf(text, offset);
            EditorCommands.OpenDocument(path, line, offset - TextScan.LineStart(text, offset) + 1);
        }

        private static bool Check(bool ok, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!ok)
                EditorCommands.Status(message);
            return ok;
        }
    }
}
