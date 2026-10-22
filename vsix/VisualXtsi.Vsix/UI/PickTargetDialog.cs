using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualXtsi.UI
{
    /// <summary>Chooses one of several targets (e.g. definitions of an overloaded name); typing filters.</summary>
    internal sealed class PickTargetDialog : SearchDialog
    {
        private readonly IReadOnlyList<SearchResult> _targets;

        public PickTargetDialog(string title, IReadOnlyList<SearchResult> targets) : base(title, "")
        {
            _targets = targets;
        }

        protected override IReadOnlyList<SearchResult> Search(string query)
        {
            if (query.Length == 0)
                return _targets;
            return _targets.Where(t => IsSubsequence(query, t.Name + " " + t.Detail)).ToList();
        }

        protected override string Status(string query, int resultCount) =>
            $"{resultCount} of {_targets.Count} targets. Enter opens; type to filter.";

        // An empty query shows everything (SearchDialog skips searching then), so seed it here.
        protected override bool SearchWhenEmpty => true;

        private static bool IsSubsequence(string query, string text)
        {
            int i = 0;
            foreach (char c in text)
                if (i < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[i]))
                    i++;
            return i == query.Length;
        }
    }
}
