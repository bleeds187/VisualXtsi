using Microsoft.VisualStudio.Text;
using VisualXtsi.Interop;

namespace VisualXtsi.Parsing
{
    /// <summary>Semantic spans (sorted, non-overlapping) for one parsed snapshot.</summary>
    internal sealed class SemanticResult
    {
        public SemanticResult(ITextSnapshot snapshot, VxSpan[] spans)
        {
            Snapshot = snapshot;
            Spans = spans;
        }

        public ITextSnapshot Snapshot { get; }

        public VxSpan[] Spans { get; }

        /// <summary>Index of the first span that ends after <paramref name="position"/>.</summary>
        public int FirstEndingAfter(int position)
        {
            int lo = 0, hi = Spans.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Spans[mid].Start + Spans[mid].Length <= position)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }
}
