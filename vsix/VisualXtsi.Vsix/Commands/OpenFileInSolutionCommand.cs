using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Interop;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>VisualXtsi.OpenFileInSolution (Shift+Alt+O in the editor): fuzzy file search over the index.</summary>
    internal static class OpenFileInSolutionCommand
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EditorCommands.Add(commands, CommandIds.OpenFileInSolution,
                async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await ExecuteAsync();
                },
                () => true);
        }

        /// <summary>Indexes the open solution's folder and the Unreal project of the active document.</summary>
        public static void EnsureRoots()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string solution = dte?.Solution?.FullName;
            string active = dte?.ActiveDocument?.FullName;
            _ = Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(solution))
                    Engine.IndexAddRoot(Path.GetDirectoryName(solution));
                if (!string.IsNullOrEmpty(active))
                    Engine.IndexAddProjectOf(active);
            });
        }

        private static async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureRoots();
            var dialog = new OpenFileDialog();
            if (dialog.ShowModal() == true && dialog.SelectedPath != null)
                EditorCommands.OpenDocument(dialog.SelectedPath, dialog.SelectedLine);
        }
    }
}
