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
            string declaration = "void NewMethod()", macro = "", access = "public";
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
