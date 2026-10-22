using EnvDTE;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>VisualXtsi.FindSymbolInSolution (Shift+Alt+S): fuzzy search over the symbol index.</summary>
    internal static class FindSymbolCommand
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EditorCommands.Add(commands, CommandIds.FindSymbolInSolution,
                async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await ExecuteAsync();
                },
                () => true);
        }

        private static async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            OpenFileInSolutionCommand.EnsureRoots();
            // A selection on one line seeds the search.
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string selected = (dte?.ActiveDocument?.Selection as TextSelection)?.Text;
            string seed = !string.IsNullOrWhiteSpace(selected) && selected.IndexOf('\n') < 0 ? selected.Trim() : null;
            var dialog = new FindSymbolDialog(seed);
            if (dialog.ShowModal() == true && dialog.Selected != null)
                EditorCommands.OpenDocument(dialog.Selected.Path, dialog.Selected.Line, dialog.Selected.Column);
        }
    }
}
