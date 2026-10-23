using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Interop;
using VisualXtsi.Options;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Commands
{
    /// <summary>
    /// Extensions > VisualXtsi > Tools (rebuild the symbol index, clear the PCH cache, open the
    /// log), Help (About, keyboard shortcuts) and Enable/Disable VisualXtsi.
    /// </summary>
    internal static class ToolCommands
    {
        /// <summary>Tools > Options > Environment > Keyboard.</summary>
        private const string KeyboardOptionsPage = "{BAFF6A1A-0CF2-11D1-8C8D-0000F87570EE}";

        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EditorCommands.Add(commands, CommandIds.RebuildSymbolIndex, async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EditorCommands.Status("rebuilding the symbol index...");
                await Task.Run(() => Engine.SymbolsRebuild());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                OpenFileInSolutionCommand.EnsureRoots();
                Log.Write("Symbol index rebuild started");
                EditorCommands.Status("rebuilding the symbol index in the background (Find Symbol shows the progress)");
            }, () => EditorCommands.VisualXtsiEnabled);

            EditorCommands.Add(commands, CommandIds.ClearPchCache, async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EditorCommands.Status("clearing the PCH cache...");
                ulong freed = await Task.Run(() => Engine.ClearPchCache());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Log.Write($"PCH cache cleared from the menu: {freed / (1024 * 1024)} MB freed");
                EditorCommands.Status($"PCH cache cleared, {freed / (1024 * 1024)} MB freed");
            }, () => true);

            EditorCommands.Add(commands, CommandIds.OpenLog, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!File.Exists(Log.FilePath))
                    Log.Write("Log opened");
                EditorCommands.OpenDocument(Log.FilePath);
            }, () => true);

            EditorCommands.Add(commands, CommandIds.About, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                new AboutDialog().ShowModal();
            }, () => true);

            EditorCommands.Add(commands, CommandIds.KeyboardShortcuts, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ShowKeyboardOptions();
            }, () => true);

            OleMenuCommand toggle = EditorCommands.Add(commands, CommandIds.ToggleEnabled, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VxSettings settings = VxSettings.Load();
                settings.Enabled = !VxSettings.Current.Enabled;
                VxSettings.Save(settings);
                if (settings.Enabled)
                    OpenFileInSolutionCommand.EnsureRoots();
                EditorCommands.Status(settings.Enabled ? "enabled" : "disabled (Extensions > VisualXtsi > Enable/Disable turns it back on)");
            }, () => true);
            toggle.BeforeQueryStatus += (s, e) => toggle.Checked = VxSettings.Current.Enabled;
        }

        /// <summary>
        /// Opens Tools > Options > Environment > Keyboard and types "VisualXtsi." into its "Show
        /// commands containing" box. The dialog is modal, so a background thread finds the box
        /// through UI Automation while the UI thread runs the dialog.
        /// </summary>
        private static void ShowKeyboardOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int processId = Process.GetCurrentProcess().Id;
            var done = new CancellationTokenSource();
            _ = Task.Run(() => FilterKeyboardPage(processId, done.Token));
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ExecuteCommand("Tools.Options", KeyboardOptionsPage);
            }
            catch (Exception ex)
            {
                Log.Write($"Opening the keyboard options failed: {ex.Message}");
            }
            finally
            {
                done.Cancel();
            }
        }

        private static void FilterKeyboardPage(int processId, CancellationToken cancel)
        {
            try
            {
                var window = new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.NameProperty, "Options"));
                var box = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, "Show commands containing:"));
                for (int attempt = 0; attempt < 150 && !cancel.IsCancellationRequested; attempt++)
                {
                    Thread.Sleep(200);
                    // An owned window is a child of its owner (the main window) in the automation tree.
                    IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                    AutomationElement owner = main != IntPtr.Zero ? AutomationElement.FromHandle(main) : AutomationElement.RootElement;
                    AutomationElement options = owner.FindFirst(TreeScope.Children, window);
                    AutomationElement edit = options?.FindFirst(TreeScope.Descendants, box);
                    if (edit != null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                    {
                        ((ValuePattern)pattern).SetValue("VisualXtsi.");
                        return;
                    }
                }
                Log.Write("Keyboard options: the command filter box was not found");
            }
            catch (Exception ex)
            {
                Log.Write($"Filtering the keyboard options failed: {ex.Message}");
            }
        }
    }
}
