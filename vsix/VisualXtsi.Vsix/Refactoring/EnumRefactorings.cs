using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Refactoring
{
    /// <summary>Add Missing Case Statements: the switch on an enum at the caret gets a case per enumerator it lacks.</summary>
    internal static class EnumRefactorings
    {
        // Enabled in a function with a switch before the caret (or on the caret's line); the parse decides when run.
        public static bool CanAddMissingCases(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c?.Snapshot == null)
                return c != null;
            int caret = c.OffsetOf(c.Line, c.Column);
            int lineEnd = c.Snapshot.GetLineFromPosition(caret).End.Position;
            int from = Math.Max(0, caret - 8000);
            return Regex.IsMatch(c.Snapshot.GetText(from, lineEnd - from), @"\bswitch\s*\(");
        }

        public static async Task AddMissingCasesAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            var s = await Task.Run(() => Engine.SwitchAt(c.Path, c.Line, c.Column, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null)
            {
                EditorCommands.Status("put the caret in a switch statement");
                return;
            }
            string text = c.Snapshot.GetText();
            SwitchShape shape = Shape(c, s);
            if (shape?.EnumName == null)
            {
                EditorCommands.Status("the switch is not on an enum");
                return;
            }
            List<string> missing = EnumSupport.Missing(shape, VxSettings.Current.SkipSentinelCases);
            if (missing.Count == 0)
            {
                EditorCommands.Status($"every {shape.EnumName} value has a case");
                return;
            }
            var (offset, insert) = EnumSupport.Insertion(text, shape, missing, SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText()));
            if (Documents.Apply(new[] { FileEdit.Insert(c.Path, offset, insert) }, "Add Missing Case Statements", out string error))
                EditorCommands.Status($"added {missing.Count} case{(missing.Count == 1 ? "" : "s")}: {string.Join(", ", missing)}");
            else
                EditorCommands.Status(error);
        }

        // vx_switch_at's lines as offsets in the parsed text.
        private static SwitchShape Shape(RefactorContext c, Dictionary<string, List<string[]>> s)
        {
            if (!s.TryGetValue("switch", out var sw) || !s.TryGetValue("body", out var body))
                return null;
            var shape = new SwitchShape
            {
                SwitchStart = c.OffsetOf(int.Parse(sw[0][1]), int.Parse(sw[0][2])),
                BodyOpen = c.OffsetOf(int.Parse(body[0][1]), int.Parse(body[0][2])),
                BodyClose = c.OffsetOf(int.Parse(body[0][3]), int.Parse(body[0][4])) - 1,
            };
            if (s.TryGetValue("enum", out var e))
            {
                shape.EnumName = e[0][0];
                shape.Scoped = e[0][1] == "1";
                shape.Qualifier = e[0].Length > 2 ? e[0][2] : "";
            }
            List<string[]> Lines(string key) => s.TryGetValue(key, out var list) ? list : new List<string[]>();
            foreach (string[] f in Lines("enumerator"))
                shape.Enumerators.Add((f[0], long.Parse(f[1]), int.Parse(f[2])));
            foreach (string[] f in Lines("case"))
                shape.Cases.Add((long.Parse(f[0]), c.OffsetOf(int.Parse(f[1]), int.Parse(f[2])), f.Length > 3 ? f[3] : ""));
            if (s.TryGetValue("default", out var d))
                shape.Default = c.OffsetOf(int.Parse(d[0][1]), int.Parse(d[0][2]));
            return shape;
        }
    }
}
