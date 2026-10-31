using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Find References (Shift+Alt+F): C/C++ by the engine (candidate files parsed in the background,
    /// each use confirmed by the symbol's USR), C# by Roslyn; the results go to the References window.
    /// </summary>
    internal static class FindReferences
    {
        public static async Task RunAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = EditorCommands.ActiveSourceDocument(package);
            IWpfTextView view = GoToDefinitionCommand.ActiveView();
            if (path == null || view == null)
                return;
            if (CSharp.CSharpNavigation.IsCSharp(path))
            {
                SnapshotPoint caret = view.Caret.Position.BufferPosition;
                EditorCommands.Status("finding references...");
                var (label, name, refs) = await Task.Run(() => CSharpReferencesAsync(caret));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (refs == null)
                {
                    EditorCommands.Status("nothing to find references of at the caret");
                    return;
                }
                EditorCommands.Status("");
                ReferencesToolWindow.Show(package, new ListReferenceSearch(label, name, refs));
                return;
            }
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            uint job = await Task.Run(() => Engine.ReferencesStart(c.Path, c.Line, c.Column));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (job == 0)
            {
                EditorCommands.Status("nothing to find references of at the caret");
                return;
            }
            ReferencesToolWindow.Show(package, new EngineReferenceSearch(job));
        }

        /// <summary>Roslyn's references of the symbol at the caret: its definitions and uses (null: no symbol).</summary>
        public static async Task<(string Label, string Name, List<Engine.Reference> Refs)> CSharpReferencesAsync(SnapshotPoint caret)
        {
            Document document = caret.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
                return (null, null, null);
            ISymbol symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caret.Position, CancellationToken.None);
            if (symbol == null && caret.Position > 0)
                symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caret.Position - 1, CancellationToken.None);
            if (symbol == null)
                return (null, null, null);
            var refs = new List<Engine.Reference>();
            foreach (ReferencedSymbol found in await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution))
            {
                // A property's or event's accessors come back as symbols of their own, defined where it is: once is enough.
                if (!(found.Definition is IMethodSymbol accessor && accessor.AssociatedSymbol != null))
                    foreach (Location l in found.Definition.Locations.Where(l => l.IsInSource))
                        refs.Add(await ReferenceAtAsync(document.Project.Solution, l, "definition"));
                foreach (ReferenceLocation r in found.Locations)
                    refs.Add(await ReferenceAtAsync(document.Project.Solution, r.Location, "reference"));
            }
            return (symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) + " (" + symbol.Kind.ToString().ToLowerInvariant() + ")", symbol.Name,
                    refs.Where(r => r != null).GroupBy(r => (r.Path, r.Line, r.Column)).Select(g => g.First()).ToList());
        }

        private static async Task<Engine.Reference> ReferenceAtAsync(Solution solution, Location location, string kind)
        {
            Document doc = solution.GetDocument(location.SourceTree);
            if (doc == null)
                return null;
            SourceText text = await doc.GetTextAsync();
            LinePosition start = location.GetLineSpan().StartLinePosition;
            return new Engine.Reference
            {
                Path = doc.FilePath,
                Line = start.Line + 1,
                Column = start.Character + 1,
                Kind = kind,
                Text = text.Lines[start.Line].ToString().Trim(),
            };
        }
    }
}
