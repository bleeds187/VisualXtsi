using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
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
    /// Everything else is resolved with Roslyn: a symbol defined in source opens there (a partial type
    /// or method lists its parts), one from metadata goes to Visual Studio's own Go to Definition.
    /// When Roslyn cannot tell (a C# file outside any C# project, such as Build.cs and Target.cs files,
    /// which belong to C++ projects), the C# symbol index is tried before Visual Studio.
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
            IReadOnlyList<SearchResult> roslyn = null;
            if (view != null)
            {
                SnapshotPoint caret = view.Caret.Position.BufferPosition;
                roslyn = await Task.Run(() => RoslynTargetsAsync(caret));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            if (roslyn != null)
            {
                if (roslyn.Count > 0)
                    Open(roslyn);
                else
                    VisualStudioGoToDefinition(); // defined in metadata: Visual Studio shows it
                return;
            }
            string word = view != null && !InCSharpProject() ? WordAt(view.Caret.Position.BufferPosition) : null;
            if (word != null)
            {
                // In a rules file, UnrealBuildTool's types and members first, narrowed by the receiver
                // ("Target.Platform") or the file's rules class; then any C# definition of the name.
                string receiver = ReceiverAt(view.Caret.Position.BufferPosition);
                bool rules = IsUnrealRules(path);
                Interop.SymbolInfo[] definitions = await Task.Run(() =>
                {
                    IReadOnlyList<Interop.SymbolInfo> ubt = rules ? UbtSymbols.DefinitionsInContext(word, receiver, path) : null;
                    if (ubt != null && ubt.Count > 0)
                        return ubt.ToArray();
                    // The project's own C# (other rules files); in a rules file, not UnrealBuildTool's code,
                    // which was narrowed above (and of another engine, if any).
                    return Engine.LookupSymbols(word, 50, SymbolLanguage.CSharp)
                        .Where(s => s.Definition && !(rules && UnrealModules.IsRulesCode(s.Path))).ToArray();
                });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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
                if (rules) // Visual Studio knows even less here, and says so in a message box
                {
                    EditorCommands.Status($"no definition of {word} found");
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

        /// <summary>
        /// Where Roslyn says the symbol at the caret is defined: its source locations (several for
        /// partial types and methods), empty when it is defined in metadata only, null when there is
        /// no symbol (or no Roslyn document) at the caret.
        /// </summary>
        private static async System.Threading.Tasks.Task<IReadOnlyList<SearchResult>> RoslynTargetsAsync(SnapshotPoint caret)
        {
            try
            {
                Document document = caret.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null)
                    return null;
                ISymbol symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caret.Position, CancellationToken.None);
                if (symbol == null && caret.Position > 0) // the caret just after the name
                    symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caret.Position - 1, CancellationToken.None);
                if (symbol is IAliasSymbol alias)
                    symbol = alias.Target;
                if (symbol == null || symbol.Kind == SymbolKind.ErrorType)
                    return null;
                symbol = symbol.OriginalDefinition;
                var locations = symbol.Locations.ToList();
                if (symbol is IMethodSymbol method && method.PartialImplementationPart != null)
                    locations.AddRange(method.PartialImplementationPart.Locations);
                string label = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
                string kind = (symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.Kind.ToString()).ToLowerInvariant();
                return locations
                    .Where(l => l.IsInSource && File.Exists(l.SourceTree?.FilePath))
                    .Select(l => l.GetLineSpan())
                    .Distinct()
                    .Select(s => new SearchResult
                    {
                        Name = label,
                        Kind = kind,
                        Path = s.Path,
                        Line = s.StartLinePosition.Line + 1,
                        Column = s.StartLinePosition.Character + 1,
                        Icon = SearchDialog.FileIcon(s.Path),
                    })
                    .ToList();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Write($"C# Go to Definition: {ex.Message}");
                return null;
            }
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
                EnvDTE.Project project = (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveDocument?.ProjectItem?.ContainingProject;
                return project?.CodeModel?.Language == EnvDTE.CodeModelLanguageConstants.vsCMLanguageCSharp;
            }
            catch (Exception) // projects without a code model
            {
                return false;
            }
        }

        /// <summary>The identifier before the "." in front of the one at the caret ("Target" in Target.Platform), or null.</summary>
        internal static string ReceiverAt(SnapshotPoint caret)
        {
            ITextSnapshotLine line = caret.GetContainingLine();
            string text = line.GetText();
            int i = caret.Position - line.Start.Position;
            while (i > 0 && IsIdentifierChar(text[i - 1]))
                i--;
            while (i > 0 && text[i - 1] == ' ')
                i--;
            if (i == 0 || text[i - 1] != '.')
                return null;
            int end = --i;
            while (end > 0 && text[end - 1] == ' ')
                end--;
            int start = end;
            while (start > 0 && IsIdentifierChar(text[start - 1]))
                start--;
            return end > start ? text.Substring(start, end - start) : null;
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
                (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("Edit.GoToDefinition");
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
