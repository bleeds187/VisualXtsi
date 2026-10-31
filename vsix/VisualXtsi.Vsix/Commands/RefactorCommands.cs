using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using VisualXtsi.Refactoring;

namespace VisualXtsi.Commands
{
    /// <summary>
    /// The Refactor (Xtsi) and Surround With (Xtsi) menus of the code editor's context menu (also
    /// under Extensions > VisualXtsi > Refactor), and the commands that open them at the caret.
    /// </summary>
    internal static class RefactorCommands
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RegisterSurroundItems(commands);
            EditorCommands.Add(commands, CommandIds.EditSnippets, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                EditorCommands.OpenDocument(Snippets.EnsureUserFile());
            }, () => true);
            // Without an argument: the Surround With menu at the caret. With a snippet name as its argument
            // (Command window, an alias, or automation): that snippet directly.
            var surround = new OleMenuCommand((s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string name = ((e as OleMenuCmdEventArgs)?.InValue as string)?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    ShowMenuAtCaret(CommandIds.SurroundMenu);
                    return;
                }
                IWpfTextView view = GoToDefinitionCommand.ActiveView();
                string path = EditorCommands.ActiveSourceDocument(package);
                Snippet snippet = path == null ? null : Snippets.For(LanguageOf(path)).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (view == null || snippet == null)
                {
                    EditorCommands.Status($"no snippet named \"{name}\" for this file");
                    return;
                }
                SurroundWith.Apply(view, snippet, path);
            }, new CommandID(CommandIds.CommandSet, CommandIds.SurroundWithAtCaret)) { ParametersDescription = "$" };
            surround.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                surround.Enabled = EditorCommands.VisualXtsiEnabled && LanguageOf(EditorCommands.ActiveSourceDocument(package)) != null;
            };
            commands.AddCommand(surround);
        }

        /// <summary>"cpp" or "csharp" for a source file, else null.</summary>
        public static string LanguageOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            return CSharp.CSharpNavigation.IsCSharp(path) ? Snippets.CSharp : Snippets.Cpp;
        }

        // One menu item per snippet of the active document's language (a dynamic item list).
        private static void RegisterSurroundItems(OleMenuCommandService commands)
        {
            IReadOnlyList<Snippet> current = Array.Empty<Snippet>();
            var root = new CommandID(CommandIds.CommandSet, CommandIds.SurroundSnippetFirst);
            DynamicItemCommand command = null;
            command = new DynamicItemCommand(root,
                id => id >= CommandIds.SurroundSnippetFirst && id < CommandIds.SurroundSnippetFirst + CommandIds.SurroundSnippetSlots,
                (s, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    int index = command.Index;
                    IWpfTextView view = GoToDefinitionCommand.ActiveView();
                    string path = EditorCommands.ActiveSourceDocument(null);
                    if (view == null || path == null)
                        return;
                    current = Snippets.For(LanguageOf(path)); // also when invoked without the menu (automation)
                    if (index >= current.Count)
                        return;
                    try
                    {
                        SurroundWith.Apply(view, current[index], path);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Surround With {current[index].Name} failed: {ex}");
                    }
                },
                (s, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (command.Index == 0) // the menu is being built: read the snippets once for all items
                    {
                        string language = EditorCommands.VisualXtsiEnabled ? LanguageOf(EditorCommands.ActiveSourceDocument(null)) : null;
                        current = language == null ? Array.Empty<Snippet>() : Snippets.For(language);
                    }
                    if (command.Index > 0 && current.Count == 0) // queried on its own (automation): no menu read the snippets
                    {
                        string language = EditorCommands.VisualXtsiEnabled ? LanguageOf(EditorCommands.ActiveSourceDocument(null)) : null;
                        current = language == null ? Array.Empty<Snippet>() : Snippets.For(language);
                    }
                    bool has = command.Index < current.Count;
                    command.Visible = has || command.Index == 0;
                    command.Enabled = has;
                    command.Text = has ? current[command.Index].Name.Replace("&", "&&") : "(no snippets for this file)";
                });
            commands.AddCommand(command);
        }

        /// <summary>Opens one of our menus as a popup under the caret of the active editor.</summary>
        public static void ShowMenuAtCaret(int menuId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IWpfTextView view = GoToDefinitionCommand.ActiveView();
            if (view == null || !(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell))
                return;
            ITextViewLine line = view.Caret.ContainingTextViewLine;
            var local = new Point(view.Caret.Left - view.ViewportLeft, line.Bottom - view.ViewportTop);
            Point screen = view.VisualElement.PointToScreen(local);
            var points = new[] { new POINTS { x = (short)screen.X, y = (short)screen.Y } };
            Guid group = CommandIds.CommandSet;
            shell.ShowContextMenu(0, ref group, menuId, points, null);
        }

        /// <summary>An item of a dynamic item list: one command answering for a range of ids.</summary>
        private sealed class DynamicItemCommand : OleMenuCommand
        {
            private readonly Predicate<int> _matches;

            public DynamicItemCommand(CommandID root, Predicate<int> matches, EventHandler invoke, EventHandler beforeQueryStatus)
                : base(invoke, null, beforeQueryStatus, root)
            {
                _matches = matches;
            }

            /// <summary>Which item of the list this is (0 for the first).</summary>
            public int Index => MatchedCommandId == 0 ? 0 : MatchedCommandId - CommandID.ID;

            public override bool DynamicItemMatch(int cmdId)
            {
                if (_matches(cmdId))
                {
                    MatchedCommandId = cmdId;
                    return true;
                }
                MatchedCommandId = 0;
                return false;
            }
        }
    }
}
