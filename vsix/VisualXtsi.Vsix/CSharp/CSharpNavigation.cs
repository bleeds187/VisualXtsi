using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.CSharp
{
    /// <summary>
    /// Go to Definition (Alt+G) in C# files. In an Unreal *.Build.cs or *.Target.cs, a module name in
    /// a string ("Core" in PublicDependencyModuleNames) opens that module's Build.cs, found in the
    /// file index (the project's modules) or in the project's engine (<see cref="UnrealModules"/>).
    /// In a C# project everything else goes to Visual Studio's own C# Go to Definition; a C# file
    /// outside one (Build.cs and Target.cs files belong to C++ projects) first tries the C# symbol
    /// index, since Visual Studio knows little about such files.
    /// </summary>
    internal static class CSharpNavigation
    {
        private static readonly Regex ModuleName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        public static bool IsCSharp(string path) => path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        public static bool IsUnrealRules(string path) =>
            path != null && (path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase));

        public static async Task GoToDefinitionAsync(string path, IWpfTextView view)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string module = IsUnrealRules(path) && view != null ? StringAt(view.Caret.Position.BufferPosition) : null;
            if (module != null && ModuleName.IsMatch(module))
            {
                await GoToModuleAsync(path, module);
                return;
            }
            string word = view != null && !InCSharpProject() ? WordAt(view.Caret.Position.BufferPosition) : null;
            if (word != null)
            {
                SymbolInfo[] hits = await Task.Run(() => Engine.LookupSymbols(word, 50, SymbolLanguage.CSharp));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SymbolInfo[] definitions = hits.Where(s => s.Definition).ToArray();
                if (definitions.Length > 0)
                {
                    Open(definitions.Select(s => new SearchResult
                    {
                        Name = string.IsNullOrEmpty(s.Scope) ? s.Name : s.Scope + "." + s.Name,
                        Kind = s.KindLabel,
                        Path = s.Path,
                        Line = s.Line,
                        Column = s.Column,
                        Icon = SearchDialog.FileIcon(s.Path),
                    }).ToList());
                    return;
                }
            }
            VisualStudioGoToDefinition();
        }

        private static async Task GoToModuleAsync(string path, string module)
        {
            string fileName = module + ".Build.cs";
            EditorCommands.Status($"looking for module {module}...");
            string[] found = await Task.Run(() => Engine.FindFiles(fileName, 50)
                .Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                .Concat(UnrealModules.FindInEngine(path, module))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found.Length == 0)
            {
                EditorCommands.Status($"no module {module} found in the project or its engine");
                return;
            }
            EditorCommands.Status("");
            Open(found.Select(f => new SearchResult { Name = Path.GetFileName(f), Path = f, Icon = SearchDialog.FileIcon(f) }).ToList());
        }

        // Opens the only target, or lets the user pick one.
        private static void Open(System.Collections.Generic.IReadOnlyList<SearchResult> targets)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SearchResult chosen = targets[0];
            if (targets.Count > 1 && Options.VxSettings.Current.PickDefinition)
            {
                var picker = new PickTargetDialog("Go to Definition - VisualXtsi", targets.ToList(),
                    r => EditorCommands.OpenDocument(r.Path, r.Line, r.Column));
                if (picker.ShowModal() != true || picker.Selected == null)
                    return;
                chosen = picker.Selected;
            }
            EditorCommands.OpenDocument(chosen.Path, chosen.Line, chosen.Column);
        }

        /// <summary>Whether the active document belongs to a C# project (rather than to a C++ project or none).</summary>
        private static bool InCSharpProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Project project = (Package.GetGlobalService(typeof(DTE)) as DTE)?.ActiveDocument?.ProjectItem?.ContainingProject;
                return project?.CodeModel?.Language == CodeModelLanguageConstants.vsCMLanguageCSharp;
            }
            catch (Exception) // projects without a code model
            {
                return false;
            }
        }

        /// <summary>The identifier at (or just before) the caret.</summary>
        internal static string WordAt(SnapshotPoint caret)
        {
            ITextSnapshotLine line = caret.GetContainingLine();
            string text = line.GetText();
            int at = caret.Position - line.Start.Position, start = at, end = at;
            while (start > 0 && IsIdentifierChar(text[start - 1]))
                start--;
            while (end < text.Length && IsIdentifierChar(text[end]))
                end++;
            return end > start && !char.IsDigit(text[start]) ? text.Substring(start, end - start) : null;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Visual Studio's Go to Definition (F12), which knows C# through Roslyn.</summary>
        public static void VisualStudioGoToDefinition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                (Package.GetGlobalService(typeof(DTE)) as DTE)?.ExecuteCommand("Edit.GoToDefinition");
            }
            catch (Exception ex) // not available here (e.g. no definition at the caret)
            {
                EditorCommands.Status("no definition found");
                Log.Write($"Edit.GoToDefinition: {ex.Message}");
            }
        }

        /// <summary>The contents of the regular string literal on the caret's line that holds the caret.</summary>
        internal static string StringAt(SnapshotPoint caret)
        {
            ITextSnapshotLine line = caret.GetContainingLine();
            return StringAt(line.GetText(), caret.Position - line.Start.Position);
        }

        internal static string StringAt(string line, int column)
        {
            int start = -1;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (start < 0)
                {
                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                        return null; // a comment
                    if (c == '"')
                        start = i;
                }
                else if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    if (column > start && column <= i)
                        return line.Substring(start + 1, i - start - 1);
                    start = -1;
                }
            }
            return null;
        }
    }
}
