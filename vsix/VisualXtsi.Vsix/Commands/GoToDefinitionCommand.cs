using System.IO;
using System.Linq;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualXtsi.Interop;
using VisualXtsi.Parsing;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>
    /// VisualXtsi.GoToDefinition (Alt+G in the editor): the definition of the symbol at the caret,
    /// or its declaration when the caret is on the definition. Several candidates open a picker.
    /// </summary>
    internal static class GoToDefinitionCommand
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EditorCommands.Add(commands, CommandIds.GoToDefinition,
                async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await ExecuteAsync(package);
                },
                () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return EditorCommands.VisualXtsiEnabled && EditorCommands.ActiveCppDocument(package) != null;
                });
        }

        private static async Task ExecuteAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = EditorCommands.ActiveCppDocument(package);
            IWpfTextView view = ActiveView();
            if (path == null || view == null)
                return;

            // Ask about the caret as the engine last parsed the file (edits since then shift lines).
            SnapshotPoint caret = view.Caret.Position.BufferPosition;
            ITextSnapshot parsed = DocumentParser.TryGet(view.TextBuffer)?.ParsedSnapshot;
            SnapshotPoint point = parsed != null ? caret.TranslateTo(parsed, PointTrackingMode.Negative) : caret;
            ITextSnapshotLine line = point.GetContainingLine();
            int lineNumber = line.LineNumber + 1, column = point.Position - line.Start.Position + 1;

            Engine.Target[] targets = await Task.Run(() => Engine.GoToDefinition(path, lineNumber, column));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (targets.Length == 0)
            {
                EditorCommands.Status("no definition found");
                return;
            }
            Engine.Target chosen = targets[0];
            if (targets.Length > 1)
            {
                var picker = new PickTargetDialog("Go to Definition - VisualXtsi", targets
                    .Select(t => new SearchResult
                    {
                        Name = t.Label,
                        Path = t.Path,
                        Line = t.Line,
                        Column = t.Column,
                        Icon = SearchDialog.FileIcon(t.Path),
                    })
                    .ToList(),
                    r => EditorCommands.OpenDocument(r.Path, r.Line, r.Column));
                if (picker.ShowModal() != true || picker.Selected == null)
                    return;
                chosen = new Engine.Target { Path = picker.Selected.Path, Line = picker.Selected.Line, Column = picker.Selected.Column };
            }
            EditorCommands.OpenDocument(chosen.Path, chosen.Line, chosen.Column);
        }

        private static IWpfTextView ActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager textManager) ||
                textManager.GetActiveView(1, null, out IVsTextView vsView) != 0 || vsView == null)
                return null;
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView);
        }
    }
}
