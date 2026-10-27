using System.IO;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Interop;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>
    /// VisualXtsi.ToggleHeaderSource (Alt+O): opens the active file's header or source. An Unreal
    /// Build.cs goes to the source implementing the module (its header first, if it has one; a
    /// header-less one goes back); C# goes between designer and code files.
    /// </summary>
    internal static class ToggleHeaderSourceCommand
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EditorCommands.Add(commands, CommandIds.ToggleHeaderSource,
                async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await ExecuteAsync(package);
                },
                () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return EditorCommands.VisualXtsiEnabled && EditorCommands.ActiveSourceDocument(package) != null;
                });
        }

        private static async Task ExecuteAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = EditorCommands.ActiveSourceDocument(package);
            if (path == null)
                return;
            string[] found = await Task.Run(() => Engine.Counterparts(path));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found.Length == 0)
            {
                EditorCommands.Status($"no counterpart found for {Path.GetFileName(path)}");
                return;
            }
            EditorCommands.OpenDocument(found[0]);
        }
    }
}
