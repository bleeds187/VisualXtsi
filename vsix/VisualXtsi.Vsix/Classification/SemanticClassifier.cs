using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using VisualXtsi.Interop;
using VisualXtsi.Options;
using VisualXtsi.Parsing;

namespace VisualXtsi.Classification
{
    /// <summary>
    /// Colors identifiers from the last background parse. Never parses: spans from the parsed
    /// snapshot are mapped forward to the current one (tokens edited since then are skipped),
    /// and after each parse only the spans that actually changed are invalidated.
    /// </summary>
    internal sealed class SemanticClassifier : IClassifier, IDocumentParserListener, IVxSettingsListener
    {
        private const int MaxInvalidatedRanges = 64;

        private readonly ITextBuffer _buffer;
        private readonly Dictionary<VxKind, IClassificationType> _types = new Dictionary<VxKind, IClassificationType>();
        private DocumentParser _parser;
        private HashSet<VxKind> _enabled = new HashSet<VxKind>();

        public SemanticClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            foreach (VxKind kind in Enum.GetValues(typeof(VxKind)))
            {
                string name = SemanticNames.For(kind);
                if (name != null)
                    _types[kind] = registry.GetClassificationType(name);
            }
            // The parser may be created before or after this classifier: it looks us up in the
            // buffer's properties when it appears (no static events, so nothing outlives the buffer).
            buffer.Properties[typeof(IDocumentParserListener)] = this;
            UpdateEnabled(VxSettings.Current);
            VxSettings.AddListener(this);
            if (DocumentParser.TryGet(buffer) is DocumentParser existing)
                Link(existing);
        }

        public void SettingsChanged(VxSettings settings)
        {
            UpdateEnabled(settings);
            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void UpdateEnabled(VxSettings settings)
        {
            var enabled = new HashSet<VxKind>();
            foreach (VxKind kind in _types.Keys)
                if (settings.IsColorEnabled(SemanticNames.For(kind)))
                    enabled.Add(kind);
            _enabled = enabled;
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            SemanticResult semantic = _parser?.Semantic;
            if (semantic == null)
                return result;

            bool sameSnapshot = span.Snapshot == semantic.Snapshot;
            SnapshotSpan requested = sameSnapshot ? span : span.TranslateTo(semantic.Snapshot, SpanTrackingMode.EdgeInclusive);
            VxSpan[] spans = semantic.Spans;
            HashSet<VxKind> enabled = _enabled;
            for (int i = semantic.FirstEndingAfter(requested.Start); i < spans.Length && spans[i].Start < requested.End; i++)
            {
                if (!enabled.Contains(spans[i].Kind) || !_types.TryGetValue(spans[i].Kind, out IClassificationType type))
                    continue;
                var token = new SnapshotSpan(semantic.Snapshot, (int)spans[i].Start, (int)spans[i].Length);
                if (!sameSnapshot)
                {
                    token = token.TranslateTo(span.Snapshot, SpanTrackingMode.EdgeExclusive);
                    if (token.Length != spans[i].Length)
                        continue; // edited since the parse; the next parse will color it
                }
                if (token.IntersectsWith(span))
                    result.Add(new ClassificationSpan(token, type));
            }
            return result;
        }

        public void ParserCreated(DocumentParser parser)
        {
            Link(parser);
        }

        private void Link(DocumentParser parser)
        {
            lock (_types)
            {
                if (_parser == parser)
                    return;
                if (_parser != null)
                    _parser.SemanticUpdated -= OnSemanticUpdated;
                _parser = parser;
                parser.SemanticUpdated += OnSemanticUpdated;
            }
            if (parser.Semantic != null)
                OnSemanticUpdated(null, parser.Semantic);
        }

        // Background thread: work out which ranges changed, then notify on the UI thread.
        private void OnSemanticUpdated(SemanticResult previous, SemanticResult current)
        {
            List<Span> changed = ChangedRanges(previous, current);
            if (changed.Count > 0)
                _ = NotifyAsync(current.Snapshot, changed);
        }

        private static List<Span> ChangedRanges(SemanticResult previous, SemanticResult current)
        {
            var changed = new List<Span>();
            ITextSnapshot snapshot = current.Snapshot;
            if (previous == null)
            {
                changed.Add(new Span(0, snapshot.Length));
                return changed;
            }

            // Map the previous spans onto the current snapshot, then walk both sorted lists.
            var old = new List<(int Start, int Length, VxKind Kind)>(previous.Spans.Length);
            foreach (VxSpan s in previous.Spans)
            {
                SnapshotSpan mapped = new SnapshotSpan(previous.Snapshot, (int)s.Start, (int)s.Length)
                    .TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                old.Add((mapped.Start, mapped.Length, s.Kind));
            }
            VxSpan[] now = current.Spans;
            int i = 0, j = 0;
            while (i < old.Count || j < now.Length)
            {
                if (i < old.Count && j < now.Length && old[i].Start == now[j].Start &&
                    old[i].Length == now[j].Length && old[i].Kind == now[j].Kind)
                {
                    i++;
                    j++;
                }
                else if (j >= now.Length || (i < old.Count && old[i].Start <= now[j].Start))
                {
                    Add(changed, old[i].Start, old[i].Length);
                    i++;
                }
                else
                {
                    Add(changed, (int)now[j].Start, (int)now[j].Length);
                    j++;
                }
            }
            if (changed.Count > MaxInvalidatedRanges)
            {
                Span all = Span.FromBounds(changed[0].Start, changed[changed.Count - 1].End);
                changed.Clear();
                changed.Add(all);
            }
            return changed;
        }

        // Appends a range, merging it with the previous one when they touch.
        private static void Add(List<Span> ranges, int start, int length)
        {
            int end = start + Math.Max(length, 1);
            if (ranges.Count > 0 && start <= ranges[ranges.Count - 1].End)
            {
                Span last = ranges[ranges.Count - 1];
                ranges[ranges.Count - 1] = Span.FromBounds(last.Start, Math.Max(last.End, end));
                return;
            }
            ranges.Add(Span.FromBounds(start, end));
        }

        private async Task NotifyAsync(ITextSnapshot snapshot, List<Span> ranges)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ITextSnapshot currentSnapshot = _buffer.CurrentSnapshot;
            foreach (Span range in ranges)
            {
                Span clamped = Span.FromBounds(Math.Min(range.Start, snapshot.Length), Math.Min(range.End, snapshot.Length));
                SnapshotSpan target = new SnapshotSpan(snapshot, clamped).TranslateTo(currentSnapshot, SpanTrackingMode.EdgeInclusive);
                ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(target));
            }
        }
    }
}
