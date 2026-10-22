using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Find Symbol in Solution: fuzzy search over the declarations of the indexed source.
    /// "Scope::name" narrows by class or namespace ("LyraChar::begin").
    /// </summary>
    internal sealed class FindSymbolDialog : SearchDialog
    {
        private static string _lastQuery = "";

        public FindSymbolDialog(string initialQuery)
            : base("Find Symbol in Solution (VisualXtsi)", string.IsNullOrEmpty(initialQuery) ? _lastQuery : initialQuery)
        {
        }

        protected override IReadOnlyList<SearchResult> Search(string query)
        {
            return Engine.FindSymbols(query, MaxResults)
                .Select(s => new SearchResult
                {
                    Name = s.Name,
                    Kind = s.KindLabel,
                    Detail = (string.IsNullOrEmpty(s.Scope) ? "" : s.Scope + "   ") + $"{Path.GetFileName(s.Path)}({s.Line})",
                    Path = s.Path,
                    Line = s.Line,
                    Column = s.Column,
                })
                .ToList();
        }

        protected override string Status(string query, int resultCount)
        {
            Engine.SymbolStatus(out int files, out int pending, out int symbols);
            string indexing = pending > 0 ? $" (indexing, {pending:N0} files to go)" : "";
            return query.Length == 0
                ? $"{symbols:N0} symbols in {files:N0} files{indexing}. \"Scope::name\" narrows by class or namespace."
                : $"{resultCount}{(resultCount == MaxResults ? "+" : "")} matches in {symbols:N0} symbols{indexing}";
        }

        protected override void OnAccepted() => _lastQuery = QueryText;
    }
}
