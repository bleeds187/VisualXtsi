using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace VisualXtsi.Options
{
    /// <summary>
    /// VisualXtsi's commands and their shortcuts, read and written through Visual Studio's own
    /// binding API (DTE Command.Bindings, "Scope::Keys" strings), so Tools > Options > Keyboard
    /// shows the same. UI thread only.
    /// </summary>
    internal static class KeyBindings
    {
        public sealed class CommandInfo
        {
            public string Name;      // "VisualXtsi.GoToDefinition"
            public string Label;     // "Go to Definition"
            public string[] Defaults; // as declared in VisualXtsi.vsct
        }

        public static readonly IReadOnlyList<CommandInfo> Commands = new[]
        {
            new CommandInfo { Name = "VisualXtsi.OpenFileInSolution", Label = "Open File in Solution", Defaults = new[] { "Text Editor::Shift+Alt+O" } },
            new CommandInfo { Name = "VisualXtsi.ToggleHeaderSource", Label = "Toggle Header/Source", Defaults = new[] { "Text Editor::Alt+O" } },
            new CommandInfo { Name = "VisualXtsi.FindSymbolInSolution", Label = "Find Symbol in Solution", Defaults = new[] { "Global::Shift+Alt+S" } },
            new CommandInfo { Name = "VisualXtsi.GoToDefinition", Label = "Go to Definition", Defaults = new[] { "Text Editor::Alt+G" } },
            new CommandInfo { Name = "VisualXtsi.Refactor.FindReferences", Label = "Find References", Defaults = new[] { "Text Editor::Shift+Alt+F" } },
            new CommandInfo { Name = "VisualXtsi.Refactor.Rename", Label = "Rename", Defaults = new[] { "Text Editor::Shift+Alt+R" } },
            new CommandInfo { Name = "VisualXtsi.Refactor.MenuAtCaret", Label = "Refactor menu at the caret", Defaults = new[] { "Text Editor::Shift+Alt+Q" } },
            new CommandInfo { Name = "VisualXtsi.Refactor.CreateImplementation", Label = "Create Implementation", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.CreateMethodImplementations", Label = "Create Method Implementations", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.CreateDeclaration", Label = "Create Declaration", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.MoveImplementationToSource", Label = "Move Implementation to Source File", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.MoveImplementationToHeader", Label = "Move Implementation to Header File", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.AddInclude", Label = "Add Include", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.AddForwardDeclaration", Label = "Add Forward Declaration", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.DocumentMethod", Label = "Document Method", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.EncapsulateField", Label = "Encapsulate Field", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.AddRemoveBraces", Label = "Add/Remove Braces", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.SurroundWith", Label = "Surround With (menu at the caret)", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Refactor.EditSnippets", Label = "Edit Snippets", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.RebuildSymbolIndex", Label = "Rebuild Symbol Index", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.ClearPchCache", Label = "Clear PCH Cache", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.OpenLogFile", Label = "Open Log File", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.About", Label = "About VisualXtsi", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.KeyboardShortcuts", Label = "Keyboard Shortcuts", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.EnableDisable", Label = "Enable/Disable VisualXtsi", Defaults = new string[0] },
            new CommandInfo { Name = "VisualXtsi.Options", Label = "Options", Defaults = new string[0] },
        };

        public static string[] Get(string command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object bindings = Dte()?.Commands.Item(command).Bindings;
                return bindings is object[] list ? list.Select(b => b.ToString()).ToArray() : new string[0];
            }
            catch (Exception ex)
            {
                Log.Write($"Reading the shortcuts of {command} failed: {ex.Message}");
                return new string[0];
            }
        }

        public static bool Set(string command, string[] bindings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Dte().Commands.Item(command).Bindings = bindings.Cast<object>().ToArray();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"Setting the shortcuts of {command} to {string.Join(", ", bindings)} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Other commands bound to the same keys in the same or the global scope (slow: walks every command).</summary>
        public static List<string> Conflicts(string binding, string except)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var found = new List<string>();
            string keys = KeysOf(binding);
            foreach (Command command in Dte().Commands)
            {
                if (string.IsNullOrEmpty(command.Name) || command.Name == except || !(command.Bindings is object[] list))
                    continue;
                foreach (object b in list)
                    if (string.Equals(KeysOf(b.ToString()), keys, StringComparison.OrdinalIgnoreCase))
                        found.Add($"{command.Name} ({ScopeOf(b.ToString())})");
            }
            return found;
        }

        public static string ScopeOf(string binding)
        {
            int sep = binding.IndexOf("::", StringComparison.Ordinal);
            return sep < 0 ? "" : binding.Substring(0, sep);
        }

        public static string KeysOf(string binding)
        {
            int sep = binding.IndexOf("::", StringComparison.Ordinal);
            return sep < 0 ? binding : binding.Substring(sep + 2);
        }

        /// <summary>"Alt+O (Text Editor)".</summary>
        public static string Describe(string binding) => $"{KeysOf(binding)} ({ScopeOf(binding)})";

        /// <summary>The Visual Studio spelling of a key press ("Ctrl+Shift+K"); null for keys that cannot be bound alone.</summary>
        public static string Format(Key key, ModifierKeys modifiers)
        {
            string name = KeyName(key);
            if (name == null)
                return null;
            var parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0)
                parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Shift) != 0)
                parts.Add("Shift");
            if ((modifiers & ModifierKeys.Alt) != 0)
                parts.Add("Alt");
            parts.Add(name);
            return string.Join("+", parts);
        }

        private static string KeyName(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return key.ToString();
            if (key >= Key.D0 && key <= Key.D9)
                return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.F1 && key <= Key.F24)
                return key.ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "Num " + (key - Key.NumPad0);
            switch (key)
            {
                case Key.Left: return "Left Arrow";
                case Key.Right: return "Right Arrow";
                case Key.Up: return "Up Arrow";
                case Key.Down: return "Down Arrow";
                case Key.PageUp: return "PgUp";
                case Key.PageDown: return "PgDn";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.Insert: return "Ins";
                case Key.Delete: return "Del";
                case Key.Back: return "Bkspce";
                case Key.Space: return "Space";
                case Key.Enter: return "Enter";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemPipe: return "\\";
                case Key.OemMinus: return "-";
                case Key.OemPlus: return "=";
                case Key.OemTilde: return "`";
                default: return null;
            }
        }

        private static DTE Dte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Package.GetGlobalService(typeof(DTE)) as DTE;
        }
    }
}
