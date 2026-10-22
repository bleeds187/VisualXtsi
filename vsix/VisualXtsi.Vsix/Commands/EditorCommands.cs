using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>Shared plumbing for commands that act on the active C/C++ document.</summary>
    internal static class EditorCommands
    {
        private static readonly string[] CppExtensions = { ".h", ".hpp", ".hh", ".hxx", ".inl", ".cpp", ".cc", ".cxx", ".c" };

        /// <summary>Full path of the active document if it is a C/C++ file; null otherwise. UI thread.</summary>
        public static string ActiveCppDocument(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string path = dte?.ActiveDocument?.FullName;
            if (string.IsNullOrEmpty(path))
                return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return CppExtensions.Contains(ext) ? path : null;
        }

        public static void Add(OleMenuCommandService commands, int id, Func<Task> execute, Func<bool> enabled)
        {
            var command = new OleMenuCommand((s, e) => _ = RunAsync(execute), new CommandID(CommandIds.CommandSet, id));
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                command.Enabled = enabled();
            };
            commands.AddCommand(command);
        }

        public static void OpenDocument(string path, int line = 0)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty,
                out IVsUIHierarchy _, out uint _, out IVsWindowFrame frame, out Microsoft.VisualStudio.TextManager.Interop.IVsTextView view);
            frame?.Show();
            if (view != null && line > 0)
            {
                view.SetCaretPos(line - 1, 0);
                view.CenterLines(line - 1, 1);
            }
        }

        public static void Status(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
                statusBar.SetText("VisualXtsi: " + message);
        }

        private static async Task RunAsync(Func<Task> execute)
        {
            try
            {
                await execute();
            }
            catch (Exception ex)
            {
                Log.Write($"Command failed: {ex}");
            }
        }
    }
}
