using System;
using System.Collections.Generic;
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

#pragma warning disable VSTHRD010 // dialogs are created on the UI thread
        public OpenFileDialog(Action<SearchResult> openKeepingDialog)
            : base("Open File in Solution - VisualXtsi", "OpenFile", "_File name",
                   "Part of a file name; word starts count (\"lhc\" finds LyraHeroComponent). " +
                   "\"name:123\" opens at line 123; a / or \\ searches whole paths.",
                   Options.VxSettings.Current.RememberQueries ? _lastQuery : "",
                   new[]
                   {
                       new SearchColumn("Name", nameof(SearchResult.Name), 280, isName: true),
                       new SearchColumn("Folder", nameof(SearchResult.Folder), 500),
                   },
                   openKeepingDialog)
        {
        }
#pragma warning restore VSTHRD010

        protected override IReadOnlyList<SearchResult> Search(string query)
        {
            return Engine.FindFiles(query, MaxResults)
                .Select(path => new SearchResult { Name = System.IO.Path.GetFileName(path), Path = path, Icon = FileIcon(path) })
                .ToList();
        }

        // A path query matches whole paths; only its last part can be highlighted in the name.
        protected override string NamePart(string query) => query.Substring(query.LastIndexOfAny(new[] { '/', '\\' }) + 1);

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
