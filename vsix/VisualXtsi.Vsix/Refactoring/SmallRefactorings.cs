using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Refactoring
{
    /// <summary>Document Method, Encapsulate Field and Add/Remove Braces: edits in the current file.</summary>
    internal static class SmallRefactorings
    {
        public static bool CanDocument(RefactorContext c) => c != null && (c.Info == null || c.Info.IsFunction);
        public static bool CanEncapsulate(RefactorContext c) => c != null && (c.Info == null || (c.Info.Kind == "field" && c.Info.IsMember));
        public static bool CanBraces(RefactorContext c) => c != null;

        // ---- Document Method

        public static async Task DocumentMethodAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (f == null || !f.IsFunction)
            {
                EditorCommands.Status("put the caret on a function");
                return;
            }
            SourceRange extent = f.AtDefinition ? f.DefinitionExtent : f.DeclarationExtent;
            if (extent == null || !extent.InFile(c.Path))
            {
                EditorCommands.Status($"the declaration of {f.Name} is in {Path.GetFileName(extent?.Path ?? "another file")}");
                return;
            }
            string text = c.Snapshot.GetText();
            int decl = c.OffsetOf(extent.Line, extent.Column);
            int at = TextScan.LineStart(text, decl);
            // Above the UFUNCTION(...) of a reflected function.
            if (RenameSupport.ReflectionMacro(text, decl) is string macro)
                for (int line = at; line > 0;)
                {
                    line = TextScan.LineStart(text, line - 1);
                    if (text.Substring(line, TextScan.LineEnd(text, line) - line).TrimStart().StartsWith(macro))
                    {
                        at = line;
                        break;
                    }
                }
            string above = at > 0 ? text.Substring(TextScan.LineStart(text, at - 1), at - TextScan.LineStart(text, at - 1)).Trim() : "";
            if (above.EndsWith("*/") || above.StartsWith("//"))
            {
                EditorCommands.Status($"{f.Name} already has a comment");
                return;
            }
            var (comment, caret) = CodeShapes.DocComment(f, VxSettings.Current.DocCommentStyle, TextScan.IndentOf(text, at), TextScan.Newline(text));
            if (!Documents.Apply(new[] { FileEdit.Insert(c.Path, at, comment) }, "Document Method", out string error))
            {
                EditorCommands.Status(error);
                return;
            }
            c.View.Caret.MoveTo(new SnapshotPoint(c.View.TextBuffer.CurrentSnapshot, at + caret));
        }

        // ---- Encapsulate Field

        public static async Task EncapsulateFieldAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (f == null || f.Kind != "field" || !f.IsMember || f.Class == null || string.IsNullOrEmpty(f.Type))
            {
                EditorCommands.Status("put the caret on a member variable");
                return;
            }
            string file = f.Class.Path;
            string text = Documents.Read(file);
            string nl = TextScan.Newline(text);
            int classStart = TextScan.Offset(text, f.Class.Line, f.Class.Column), classEnd = TextScan.Offset(text, f.Class.EndLine, f.Class.EndColumn);
            int brace = text.IndexOf('{', classStart);
            bool isStruct = System.Text.RegularExpressions.Regex.IsMatch(text.Substring(classStart, (brace < 0 ? text.Length : brace) - classStart), @"\bstruct\b");
            var (at, needsPublic) = TextScan.MemberInsertion(text, classStart, classEnd, isStruct);
            string classIndent = TextScan.IndentOf(text, classStart);
            string indent = TextScan.MemberIndent(text, at, classIndent, SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText()));

            // Blueprint accessors for a UPROPERTY of a UCLASS, when chosen.
            string category = null;
            if (VxSettings.Current.EncapsulateBlueprint && f.DeclarationExtent != null &&
                RenameSupport.ReflectionMacro(text, classStart) == "UCLASS" &&
                RenameSupport.ReflectionMacro(text, TextScan.Offset(text, f.DeclarationExtent.Line, f.DeclarationExtent.Column)) == "UPROPERTY")
                category = RenameSupport.ObjectName(f.ClassName ?? "");
            var (_, methods) = await Task.Run(() => Engine.ClassMembers(file, f.Class.Line, f.Class.Column + 1, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var existing = new HashSet<string>(methods.Select(m => m.Name));
            var (accessors, getter, setter) = CodeShapes.Accessors(f.Name, f.Type, f.IsPod, IsStatic(f), category, existing, indent, nl);
            if (accessors.Length == 0)
            {
                EditorCommands.Status($"{f.ClassName} already has {getter} and {setter}");
                return;
            }
            string code = (needsPublic ? classIndent + "public:" + nl : "") + accessors;
            if (Documents.Apply(new[] { FileEdit.Insert(file, at, code) }, "Encapsulate Field", out string error))
                EditorCommands.Status($"added {string.Join(" and ", new[] { getter, setter }.Where(n => !existing.Contains(n)))}");
            else
                EditorCommands.Status(error);
        }

        private static bool IsStatic(CursorInfo f) => f.IsStatic;

        // ---- Add/Remove Braces

        public static async Task BracesAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            var s = await Task.Run(() => Engine.StatementAt(c.Path, c.Line, c.Column, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null || !s.ContainsKey("body"))
            {
                EditorCommands.Status("put the caret on an if, else, for, while or do");
                return;
            }
            string text = c.Snapshot.GetText();
            string nl = TextScan.Newline(text);
            string unit = SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText());
            int caret = c.OffsetOf(c.Line, c.Column);
            // The else body when the caret is on or after "else", else the first body.
            string which = s.TryGetValue("else", out var elses) && caret >= c.OffsetOf(int.Parse(elses[0][1]), int.Parse(elses[0][2])) ? "else" : null;
            string[] body = s["body"].FirstOrDefault(b => which == null || b[0] == which);
            if (body == null)
                return;
            int ownerAt = which == "else" ? c.OffsetOf(int.Parse(elses[0][1]), int.Parse(elses[0][2])) : c.OffsetOf(int.Parse(s["header"][0][1]), int.Parse(s["header"][0][2]));
            string ownerIndent = TextScan.IndentOf(text, ownerAt);
            int start = c.OffsetOf(int.Parse(body[1]), int.Parse(body[2])), end = c.OffsetOf(int.Parse(body[3]), int.Parse(body[4]));
            bool block = body[5] == "1";
            int statements = int.Parse(body[6]);
            (int Start, int Length, string Text) edit;
            if (block)
            {
                string[] inner = s.TryGetValue("inner", out var inners) ? inners.FirstOrDefault(i => i[0] == body[0]) : null;
                if (statements != 1 || inner == null)
                {
                    EditorCommands.Status(statements == 0 ? "the block is empty" : $"the block holds {statements} statements; braces stay");
                    return;
                }
                int innerStart = c.OffsetOf(int.Parse(inner[1]), int.Parse(inner[2])), innerEnd = c.OffsetOf(int.Parse(inner[3]), int.Parse(inner[4]));
                edit = CodeShapes.RemoveBraces(text, start, end - 1, innerStart, innerEnd, ownerIndent, unit, nl);
            }
            else
            {
                if (body.Length > 7 && body[7] == "if")
                {
                    EditorCommands.Status("an else if needs no braces");
                    return;
                }
                edit = CodeShapes.AddBraces(text, start, end, ownerIndent, unit, nl);
            }
            if (!Documents.Apply(new[] { FileEdit.Replace(c.Path, text, edit.Start, edit.Length, edit.Text) }, block ? "Remove Braces" : "Add Braces", out string error))
                EditorCommands.Status(error);
        }
    }
}
