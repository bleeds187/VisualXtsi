using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using VisualXtsi.Classification;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Find Symbol in Solution: fuzzy search over the declarations of the indexed source.
    /// "Scope::name" (or C#'s "Scope.name") narrows by class or namespace ("LyraChar::begin"); the
    /// language box limits results to C/C++ or C#.
    /// </summary>
    internal sealed class FindSymbolDialog : SearchDialog
    {
        private static string _lastQuery = "";
        private readonly Dictionary<int, Brush> _kindBrushes = new Dictionary<int, Brush>();

#pragma warning disable VSTHRD010 // dialogs are created on the UI thread
        public FindSymbolDialog(string initialQuery, Action<SearchResult> openKeepingDialog)
            : base("Find Symbol in Solution - VisualXtsi", "FindSymbol", "_Symbol",
                   "Part of a symbol name; word starts count. \"Scope::name\" or \"Scope.name\" narrows by class or namespace.",
                   string.IsNullOrEmpty(initialQuery) ? (Options.VxSettings.Current.RememberQueries ? _lastQuery : "") : initialQuery,
                   new[]
                   {
                       new SearchColumn("Name", nameof(SearchResult.Name), 240, isName: true),
                       new SearchColumn("Kind", nameof(SearchResult.Kind), 110),
                       new SearchColumn("Scope", nameof(SearchResult.Scope), 150),
                       new SearchColumn("File", nameof(SearchResult.Location), 190),
                       new SearchColumn("Folder", nameof(SearchResult.Folder), 300),
                       new SearchColumn("Language", nameof(SearchResult.Language), 70),
                   },
                   openKeepingDialog)
        {
            // Names in the editor's colors for their kind (Options > Coloring > Apply coloring to);
            // not in high contrast, where the system colors must stay.
            if (Options.VxSettings.Current.ColorSearchResults && !SystemParameters.HighContrast)
                foreach (var (kind, category) in KindCategories)
                    if (Options.VxSettings.Current.IsCategoryActive(category) && EditorColors.Properties(category) is var p && p != null && !p.ForegroundBrushEmpty)
                        _kindBrushes[kind] = p.ForegroundBrush.GetAsFrozen() as Brush;
        }
#pragma warning restore VSTHRD010

        protected override IReadOnlyList<SearchResult> Search(string query)
        {
            return Engine.FindSymbols(query, MaxResults, (SymbolLanguage)FilterIndex)
                .Select(s => new SearchResult
                {
                    Name = s.Name,
                    Kind = s.KindLabel,
                    Language = s.Language,
                    Scope = s.Scope ?? "",
                    Path = s.Path,
                    Line = s.Line,
                    Column = s.Column,
                    Icon = KindIcon(s.Kind),
                    NameBrush = _kindBrushes.TryGetValue(s.Kind, out Brush brush) ? brush : null,
                })
                .ToList();
        }

        // Mirrors SymbolLanguage: All, C/C++, C#.
        protected override IReadOnlyList<string> FilterChoices => new[] { "All languages", "C/C++", "C#" };

        protected override string FilterLabel => "_Language";

        protected override string NamePart(string query)
        {
            int sep = query.LastIndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
                return query.Substring(sep + 2);
            int dot = query.LastIndexOf('.');
            return dot < 0 ? query : query.Substring(dot + 1);
        }

        protected override string Status(string query, int resultCount)
        {
            Engine.SymbolStatus(out int files, out int pending, out int symbols);
            string indexing = pending > 0 ? $" (indexing, {pending:N0} files to go)" : "";
            return query.Length == 0
                ? $"{symbols:N0} symbols in {files:N0} files{indexing}. \"Scope::name\" or \"Scope.name\" narrows by class or namespace."
                : $"{resultCount}{(resultCount == MaxResults ? "+" : "")} matches in {symbols:N0} symbols{indexing}";
        }

        protected override void OnAccepted() => _lastQuery = QueryText;

        // Mirrors vx_symbol_kind: the coloring category each kind of symbol is drawn with.
        private static readonly (int Kind, string Category)[] KindCategories =
        {
            (1, SemanticNames.Namespace), (2, SemanticNames.Type), (3, SemanticNames.Type), (4, SemanticNames.Type),
            (5, SemanticNames.Type), (6, SemanticNames.EnumConstant), (7, SemanticNames.Type), (8, SemanticNames.Function),
            (9, SemanticNames.Method), (10, SemanticNames.Field), (11, SemanticNames.Global), (12, ClassificationNames.Macro),
            (13, SemanticNames.Delegate), (14, SemanticNames.Property), (15, SemanticNames.Event), (16, SemanticNames.Interface),
        };

        // Mirrors vx_symbol_kind.
        private static ImageMoniker KindIcon(int kind)
        {
            switch (kind)
            {
                case 1: return KnownMonikers.Namespace;
                case 2: return KnownMonikers.Class;
                case 3: return KnownMonikers.Structure;
                case 4: return KnownMonikers.Union;
                case 5: return KnownMonikers.Enumeration;
                case 6: return KnownMonikers.EnumerationItemPublic;
                case 7: return KnownMonikers.TypeDefinition;
                case 8: return KnownMonikers.Method;
                case 9: return KnownMonikers.MethodPublic;
                case 10: return KnownMonikers.FieldPublic;
                case 11: return KnownMonikers.Field;
                case 12: return KnownMonikers.MacroPublic;
                case 13: return KnownMonikers.DelegatePublic;
                case 14: return KnownMonikers.PropertyPublic;
                case 15: return KnownMonikers.EventPublic;
                case 16: return KnownMonikers.InterfacePublic;
                default: return KnownMonikers.QuestionMark;
            }
        }
    }
}
