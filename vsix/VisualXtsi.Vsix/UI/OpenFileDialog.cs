using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Open File in Solution: type part of a name (fuzzy; word starts count). "name:123" opens at
    /// line 123; a "/" or "\" searches whole paths.
    /// </summary>
    internal sealed class OpenFileDialog : SearchDialog
    {
        private static string _lastQuery = "";

        public OpenFileDialog() : base("Open File in Solution (VisualXtsi)", _lastQuery)
        {
        }

        protected override IReadOnlyList<SearchResult> Search(string query)
        {
            return Engine.FindFiles(query, MaxResults)
                .Select(path => new SearchResult { Name = Path.GetFileName(path), Detail = Path.GetDirectoryName(path), Path = path })
                .ToList();
        }

        protected override string Status(string query, int resultCount)
        {
            int indexed = Engine.IndexFileCount();
            return query.Length == 0
                ? $"{indexed:N0} files indexed. Type part of a name; word starts count (\"lhc\" finds LyraHeroComponent)."
                : $"{resultCount}{(resultCount == MaxResults ? "+" : "")} matches in {indexed:N0} files";
        }

        protected override void OnAccepted() => _lastQuery = QueryText;
    }
}
