using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Introduce Variable: the selected expression becomes a local constant declared before its
    /// statement, optionally replacing its identical occurrences in the same block.
    /// </summary>
    internal static class ExtractRefactorings
    {
        public static bool CanIntroduceVariable(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return c?.View != null && !c.View.Selection.IsEmpty;
        }

        // The selection, in the parsed text, as 1-based lines and columns; null when there is none.
        private static (int Line, int Column, int EndLine, int EndColumn)? Selection(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c?.Snapshot == null || c.View.Selection.IsEmpty)
                return null;
            SnapshotSpan span = c.View.Selection.StreamSelectionSpan.SnapshotSpan;
            SnapshotPoint start = span.Start.TranslateTo(c.Snapshot, PointTrackingMode.Positive);
            SnapshotPoint end = span.End.TranslateTo(c.Snapshot, PointTrackingMode.Negative);
            ITextSnapshotLine a = start.GetContainingLine(), b = end.GetContainingLine();
            return (a.LineNumber + 1, start - a.Start + 1, b.LineNumber + 1, end - b.Start + 1);
        }

        private static int Offset(RefactorContext c, string[] f, int at) => c.OffsetOf(int.Parse(f[at]), int.Parse(f[at + 1]));

        public static async Task IntroduceVariableAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var range = Selection(c);
            if (range == null)
            {
                EditorCommands.Status("select an expression");
                return;
            }
            var (l1, c1, l2, c2) = range.Value;
            var s = await Task.Run(() => Engine.SelectionInfo(c.Path, l1, c1, l2, c2, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null || !s.TryGetValue("expression", out var expression) || !s.ContainsKey("statement"))
            {
                EditorCommands.Status("select an expression in a function body");
                return;
            }
            if (s.ContainsKey("void"))
            {
                EditorCommands.Status("the expression has no value");
                return;
            }
            if (s.ContainsKey("inloop"))
            {
                EditorCommands.Status("the expression is in a loop's condition or increment: a variable would compute it only once");
                return;
            }
            string text = c.Snapshot.GetText();
            string nl = TextScan.Newline(text);
            string[] e = expression[0];
            int start = Offset(c, e, 0), end = Offset(c, e, 2);
            string type = e.Length > 4 ? e[4] : "";
            var selected = (start, end, Offset(c, s["statement"][0], 0));
            var others = (s.TryGetValue("same", out var same) ? same : new List<string[]>())
                .Select(f => (Offset(c, f, 0), Offset(c, f, 2), Offset(c, f, 4))).ToList();
            string exprText = text.Substring(start, end - start);
            string suggested = ExtractSupport.SuggestName(exprText, type);
            var options = new List<(string, bool, bool)>
            {
                ($"_Replace the {others.Count} other identical occurrence{(others.Count == 1 ? "" : "s")} in the block", others.Count > 0, others.Count > 0),
                ($"_Explicit type ({type})", false, type.Length > 0),
            };
            var dialog = new NameOptionsDialog("Introduce Variable", "_Name:", suggested, options,
                n => RenameSupport.Validate(n, null),
                (n, o) => ExtractSupport.VariableType(type, o[1]) + " " + n + " = " + System.Text.RegularExpressions.Regex.Replace(exprText.Trim(), @"\s+", " ") + ";");
            if (dialog.ShowModal() != true)
                return;
            var occurrences = new List<(int, int, int)> { selected };
            if (dialog.Option(0))
                occurrences.AddRange(others);
            var edits = ExtractSupport.IntroduceVariable(text, occurrences, dialog.Value, ExtractSupport.VariableType(type, dialog.Option(1)), nl);
            if (Documents.Apply(edits.Select(x => FileEdit.Replace(c.Path, text, x.Start, x.Length, x.Text)).ToList(), "Introduce Variable", out string error))
                EditorCommands.Status($"introduced {dialog.Value}" + (occurrences.Count > 1 ? $" for {occurrences.Count} occurrences" : ""));
            else
                EditorCommands.Status(error);
        }
    }
}
