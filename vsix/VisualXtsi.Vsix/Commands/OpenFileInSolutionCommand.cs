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
                () => EditorCommands.VisualXtsiEnabled);
        }

        /// <summary>
        /// Indexes the open solution's folder and the Unreal project (and, if enabled, engine source)
        /// of the active document, and starts the symbol index on them.
        /// </summary>
        public static void EnsureRoots()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Options.VxSettings.Current.Enabled)
                return;
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string solution = dte?.Solution?.FullName;
            string active = dte?.ActiveDocument?.FullName;
            _ = Task.Run(() =>
            {
                Engine.SymbolsStart();
                if (!string.IsNullOrEmpty(solution))
                    Engine.IndexAddRoot(Path.GetDirectoryName(solution));
                if (!string.IsNullOrEmpty(active))
                    IndexDocument(active);
            });
        }

        /// <summary>
        /// Indexes the Unreal project of a document, its engine's build rules code (for Build.cs and
        /// Target.cs files) and, if enabled, the engine source. Background thread.
        /// </summary>
        public static void IndexDocument(string path)
        {
            if (!Options.VxSettings.Current.Enabled)
                return;
            Engine.SymbolsStart();
            if (!Engine.IndexAddProjectOf(path))
                return;
            CSharp.UnrealModules.IndexRulesOf(path);
            if (Options.VxSettings.Current.IndexEngineSource)
                Engine.IndexAddEngineOf(path);
        }

        private static async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureRoots();
            var dialog = new OpenFileDialog(r => EditorCommands.OpenDocument(r.Path, r.Line));
            if (dialog.ShowModal() == true && dialog.Selected != null)
                EditorCommands.OpenDocument(dialog.Selected.Path, dialog.Selected.Line);
        }
    }
}
