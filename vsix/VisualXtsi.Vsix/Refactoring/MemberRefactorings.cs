using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Add Member (a method or field typed in a dialog, into the class at the caret, in the access
    /// section chosen, with an implementation in the source file if wanted) and Add Similar Member
    /// (the same, starting from the member at the caret: its declaration, macro and access, placed
    /// right after it).
    /// </summary>
    internal static class MemberRefactorings
    {
        public static bool CanAddMember(RefactorContext c) => c != null && (c.Info == null || c.Info.InClass != null);
        public static bool CanAddSimilarMember(RefactorContext c) =>
            c != null && (c.Info == null || (c.Info.IsMember && (c.Info.IsFunction || c.Info.Kind == "field") && c.Info.Class != null));

        public static bool CanImplementInterface(RefactorContext c) => c != null && (c.Info == null || c.Info.InClass != null);

        /// <summary>
        /// Implement Interface / Override Methods: the virtual methods of the class's bases it does not
        /// override yet (pure ones checked, Unreal native events as Name_Implementation), declared
        /// with override in their base's access section, grouped by base, with stubs in the source file.
        /// </summary>
        public static async Task ImplementInterfaceAsync()
        {
            const string title = "Implement Interface";
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            var found = await Task.Run(() => Engine.Overridable(c.Path, c.Line, c.Column, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found == null)
            {
                EditorCommands.Status("put the caret in a class");
                return;
            }
            var (cls, methods) = found.Value;
            string classes = cls["class"][0][0];
            string className = classes.Split(new[] { "::" }, System.StringSplitOptions.None).Last();
            if (methods.Count == 0)
            {
                EditorCommands.Status($"{className} overrides every virtual method of its bases");
                return;
            }
            SourceRange extent = SourceRange.Parse(string.Join("\t", cls["extent"][0]));
            string header = extent.Path;
            string text = Documents.Read(header);
            if (text == null)
                return;
            string nl = TextScan.Newline(text);
            string unit = SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText());
            int classStart = TextScan.Offset(text, extent.Line, extent.Column), classEnd = TextScan.Offset(text, extent.EndLine, extent.EndColumn);
            int open = text.IndexOf('{', classStart), close = text.LastIndexOf('}', System.Math.Max(0, classEnd - 1));
            if (open < 0 || close < open)
                return;
            bool isStruct = Regex.IsMatch(text.Substring(classStart, open - classStart), @"\bstruct\b");
            bool reflected = Regex.IsMatch(text.Substring(open, close - open), @"\bGENERATED_\w*BODY\w*\s*\(");
            string classIndent = TextScan.IndentOf(text, classStart);
            bool templated = Regex.IsMatch(text.Substring(TextScan.LineStart(text, System.Math.Max(0, classStart - 1)), classStart - TextScan.LineStart(text, System.Math.Max(0, classStart - 1))), @"\btemplate\s*<") ||
                             Regex.IsMatch(text.Substring(TextScan.LineStart(text, classStart), open - TextScan.LineStart(text, classStart)), @"^\s*template\s*<");
            string target = templated ? null : Documents.IsHeader(header) ? Documents.SourceFor(header) : header;
            string export = MemberSupport.MemberExportMacro(text, classStart, open, close);

            // One item per method: its declaration and stub; pure ones (and native events) checked.
            var items = methods.Select(m =>
            {
                string decl = (export != null ? export + " " : "") + MemberSupport.OverrideDeclaration(m);
                CursorInfo info = MemberSupport.MethodInfo(decl, m.Classes, m.Namespaces, "");
                string body = MemberSupport.OverrideBody(m, reflected && m.Primary);
                string def = target == null || info == null ? null : CodeGen.Definition(CodeGen.Signature(info, info.Name, CodeGen.Qualifier(info, false)), body, unit, "\n");
                return new PreviewItem
                {
                    Group = m.Base,
                    Label = MemberSupport.OverrideDeclaration(m).Substring("virtual ".Length).Replace(" override", "") + (m.IsPure ? "   (pure)" : "") + (m.NativeEvent ? "   (native event)" : ""),
                    Diff = "+ " + decl + ";" + (def == null ? "" : "\n\n" + string.Join("\n", def.Split('\n').Select(l => "+ " + l))),
                    Checked = m.IsPure || m.NativeEvent,
                    Tag = (m, decl, body),
                };
            }).ToList();
            string intro = $"Override the virtual methods of {className}'s bases: pure ones are checked. " +
                           (target != null ? $"Stubs go to {Path.GetFileName(target)}." : "Declarations only (no source file for the class).") +
                           (methods.Any(m => m.NativeEvent) ? " Unreal native events are implemented as Name_Implementation; call them through the interface's Execute_Name." : "");
            var preview = new RefactorPreviewDialog(title, intro, items, "_Add");
            if (preview.ShowModal() != true || preview.Accepted.Count == 0)
                return;
            var chosen = preview.Accepted.Select(i => ((CursorInfo M, string Decl, string Body))i.Tag).ToList();

            // Declarations: per access section, grouped by base with Begin/End comments.
            var inserts = new Dictionary<int, string>();
            foreach (var access in chosen.GroupBy(x => x.M.Access))
            {
                var (offset, label, newLine) = MemberSupport.Insertion(text, open, close, access.Key, isStruct);
                string indent = label != null || newLine ? classIndent + unit : TextScan.MemberIndent(text, offset, classIndent, unit);
                var sb = new System.Text.StringBuilder();
                if (label != null)
                    sb.Append(classIndent).Append(label).Append(':').Append(nl);
                foreach (var group in access.GroupBy(x => x.M.Base))
                {
                    string baseName = group.Key.Split(new[] { "::" }, System.StringSplitOptions.None).Last();
                    sb.Append(indent).Append("//~ Begin ").Append(baseName).Append(" interface").Append(nl);
                    foreach (var x in group)
                        sb.Append(indent).Append(x.Decl).Append(';').Append(nl);
                    sb.Append(indent).Append("//~ End ").Append(baseName).Append(" interface").Append(nl);
                }
                string code = (newLine ? nl : "") + sb + (newLine ? classIndent : "");
                inserts[offset] = inserts.TryGetValue(offset, out string before) ? before + code : code;
            }
            var edits = inserts.Select(p => FileEdit.Insert(header, p.Key, p.Value)).ToList();

            // Stubs: at the end of the source file.
            if (target != null)
            {
                string targetText = target == header ? text : Documents.Read(target);
                if (targetText != null)
                {
                    string tnl = TextScan.Newline(targetText);
                    var defs = chosen.Select(x => (Info: MemberSupport.MethodInfo(x.Decl, x.M.Classes, x.M.Namespaces, ""), x.Body))
                                     .Where(x => x.Info != null)
                                     .Select(x => CodeGen.Definition(CodeGen.Signature(x.Info, x.Info.Name, CodeGen.Qualifier(x.Info, true)), x.Body, unit, tnl))
                                     .ToList();
                    if (defs.Count > 0)
                    {
                        var (at, prefix) = TextScan.AppendPoint(targetText, tnl);
                        edits.Add(FileEdit.Insert(target, at, prefix + string.Join(tnl + tnl, defs) + (target == header ? tnl : "")));
                    }
                }
            }
            if (!Documents.Apply(edits, $"{title} ({className})", out string error))
                EditorCommands.Status(error);
            else
                EditorCommands.Status($"{className} overrides {chosen.Count} more method{(chosen.Count == 1 ? "" : "s")}");
        }

        public static Task AddMemberAsync() => RunAsync(similar: false);
        public static Task AddSimilarMemberAsync() => RunAsync(similar: true);

        private static async Task RunAsync(bool similar)
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            string title = similar ? "Add Similar Member" : "Add Member";
            // The class the caret is in; for a similar member, the member's own class.
            bool member = f != null && f.IsMember && (f.IsFunction || f.Kind == "field");
            SourceRange cls = f == null ? null : similar ? (member ? f.Class : null) : f.InClass;
            if (cls == null)
            {
                EditorCommands.Status(similar ? "put the caret on a member of a class" : "put the caret in a class or on one of its members");
                return;
            }
            string classes = similar ? f.Classes : f.InClassClasses;
            string namespaces = similar ? f.Namespaces : f.InClassNamespaces;
            string className = classes.Split(new[] { "::" }, System.StringSplitOptions.None).Last();
            string file = cls.Path;
            string text = Documents.Read(file);
            if (text == null)
                return;
            string nl = TextScan.Newline(text);
            string unit = SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText());
            int classStart = TextScan.Offset(text, cls.Line, cls.Column), classEnd = TextScan.Offset(text, cls.EndLine, cls.EndColumn);
            int open = text.IndexOf('{', classStart), close = text.LastIndexOf('}', System.Math.Max(0, classEnd - 1));
            if (open < 0 || close < open)
            {
                EditorCommands.Status($"the body of {className} was not found");
                return;
            }
            bool isStruct = Regex.IsMatch(text.Substring(classStart, open - classStart), @"\bstruct\b");
            string classIndent = TextScan.IndentOf(text, classStart);

            // A similar member: its text, macro and access to start from.
            string export = MemberSupport.MemberExportMacro(text, classStart, open, close);
            string declaration = (export != null ? export + " " : "") + "void NewMethod()", macro = "", access = "public";
            int afterSimilar = -1;
            string similarIndent = null;
            if (similar)
            {
                SourceRange d = f.DeclarationExtent;
                if (d == null || !d.InFile(file))
                {
                    EditorCommands.Status($"the declaration of {f.Name} is not in {Path.GetFileName(file)}");
                    return;
                }
                int declStart = TextScan.Offset(text, d.Line, d.Column), declEnd = TextScan.Offset(text, d.EndLine, d.EndColumn);
                declaration = MemberSupport.DeclarationText(text, declStart, declEnd);
                macro = MemberSupport.MacroText(text, declStart);
                access = MemberSupport.AccessAt(text, open, close, declStart, isStruct);
                int end = declEnd;
                while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                    end++;
                if (end < text.Length && text[end] == ';')
                    end++;
                afterSimilar = TextScan.NextLineStart(text, end);
                similarIndent = TextScan.IndentOf(text, declStart);
            }

            string header = file;
            string target = Documents.IsHeader(file) ? Documents.SourceFor(file) : file;
            var dialog = new AddMemberDialog(title, className, declaration, macro, access, target, selectName: similar);
            if (dialog.ShowModal() != true)
                return;

            // The declaration: after the similar member (same access), else at the end of the chosen section.
            int at;
            string code;
            if (similar && dialog.Access == access && afterSimilar > 0 && afterSimilar <= close)
            {
                at = afterSimilar;
                code = MemberSupport.MemberText(dialog.Macro, dialog.Declaration, similarIndent, nl);
                if (text.Substring(at, TextScan.LineEnd(text, at) - at).Trim().Length == 0 && at < close)
                    code = nl + code; // members set apart by blank lines: this one too
            }
            else
            {
                var (offset, label, newLine) = MemberSupport.Insertion(text, open, close, dialog.Access, isStruct);
                at = offset;
                string indent = label != null || newLine ? classIndent + unit : TextScan.MemberIndent(text, at, classIndent, unit);
                code = (label != null ? classIndent + label + ":" + nl : "") + MemberSupport.MemberText(dialog.Macro, dialog.Declaration, indent, nl);
                if (newLine)
                    code = nl + code + classIndent;
            }
            var edits = new List<(FileEdit Edit, string Text)> { (FileEdit.Insert(header, at, code), text) };

            // The implementation: after the similar method's definition, else at the end of the source file.
            int bodyAt = -1;
            if (dialog.Implement && target != null)
            {
                CursorInfo method = MemberSupport.MethodInfo(dialog.Declaration, classes, namespaces, dialog.Macro);
                string targetText = target == header ? text : Documents.Read(target);
                if (method != null && targetText != null)
                {
                    string tnl = TextScan.Newline(targetText);
                    int defAt = -1;
                    string prefix = tnl + tnl, suffix = "";
                    bool withNamespaces = false;
                    if (similar && f.IsFunction)
                    {
                        int found = ImplementationRefactorings.FindDefinition(targetText, target, f.Name, f.Scope);
                        defAt = found >= 0 ? TextScan.BlockEnd(targetText, found) : -1;
                    }
                    if (defAt < 0)
                    {
                        (defAt, prefix) = TextScan.AppendPoint(targetText, tnl);
                        withNamespaces = true;
                        if (target == header)
                            suffix = tnl;
                    }
                    var defs = CodeGen.Definitions(method, CodeGen.Qualifier(method, withNamespaces), unit, tnl);
                    string definition = prefix + string.Join(tnl + tnl, defs.Select(x => x.Text)) + suffix;
                    edits.Add((FileEdit.Insert(target, defAt, definition), targetText));
                    bodyAt = defAt + prefix.Length + defs[0].Text.IndexOf('{') + 1;
                }
            }

            // Several files: a preview first.
            if (edits.Select(e => e.Edit.Path.ToLowerInvariant()).Distinct().Count() > 1)
            {
                var items = edits.Select(e => new PreviewItem
                {
                    Group = e.Edit.Path,
                    Label = $"line {TextScan.LineOf(e.Text, e.Edit.Start)}: " + (e.Edit.Path == header && e.Edit.Start == at ? "declaration" : "implementation"),
                    Diff = RefactorPreviewDialog.Diff(e.Text, e.Edit.Start, 0, e.Edit.NewText, 1),
                    Tag = e.Edit,
                }).ToList();
                var preview = new RefactorPreviewDialog(title, $"Add {dialog.Declaration} to {className}, with its implementation.", items, "_Add");
                if (preview.ShowModal() != true || preview.Accepted.Count == 0)
                    return;
                var accepted = preview.Accepted.Select(i => (FileEdit)i.Tag).ToList();
                if (!Documents.Apply(accepted, title, out string error))
                {
                    EditorCommands.Status(error);
                    return;
                }
                if (bodyAt >= 0 && accepted.Contains(edits[1].Edit))
                {
                    ImplementationRefactorings.OpenAt(target, Documents.Read(target), bodyAt);
                    EditorCommands.Status($"added {dialog.Declaration} to {className}, with its implementation");
                    return;
                }
            }
            else if (!Documents.Apply(edits.Select(e => e.Edit).ToList(), title, out string error))
            {
                EditorCommands.Status(error);
                return;
            }
            // The caret on the new member's name.
            string updated = Documents.Read(header);
            int declAt = updated.IndexOf(MemberSupport.MemberText("", dialog.Declaration, "", "").TrimEnd(), at, System.StringComparison.Ordinal);
            var (nameStart, nameLength) = MemberSupport.NameIn(dialog.Declaration);
            ImplementationRefactorings.OpenAt(header, updated, declAt >= 0 ? declAt + nameStart : at);
            EditorCommands.Status($"added {dialog.Declaration.Substring(nameStart, nameLength)} to {className}");
        }
    }
}
