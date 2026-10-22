using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Classification
{
    /// <summary>
    /// Classifies the whole buffer through the engine and caches the result per snapshot.
    /// The lexical pass needs whole-file context (comment state, #defines), so any edit
    /// can change coloring anywhere; we invalidate the full buffer on change.
    /// </summary>
    internal sealed class VxClassifier : IClassifier, IVxSettingsListener
    {
        private readonly ITextBuffer _buffer;
        private readonly IClassificationType _preprocessor;
        private readonly IClassificationType _macro;

        private ITextSnapshot _cachedSnapshot;
        private VxSpan[] _cachedSpans = Array.Empty<VxSpan>();

        public VxClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            _preprocessor = registry.GetClassificationType(ClassificationNames.Preprocessor);
            _macro = registry.GetClassificationType(ClassificationNames.Macro);
            _buffer.Changed += OnBufferChanged;
            VxSettings.AddListener(this);
        }

        public void SettingsChanged(VxSettings settings)
        {
            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            VxSettings settings = VxSettings.Current;
            bool preprocessorOn = settings.IsColorEnabled(ClassificationNames.Preprocessor);
            bool macroOn = settings.IsColorEnabled(ClassificationNames.Macro);
            if (!preprocessorOn && !macroOn)
                return result;
            VxSpan[] spans = GetSpans(span.Snapshot);

            // Spans are sorted by start; binary search for the first one that may intersect.
            int lo = 0, hi = spans.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (spans[mid].Start + spans[mid].Length <= span.Start.Position)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            for (int i = lo; i < spans.Length && spans[i].Start < span.End.Position; i++)
            {
                bool preprocessor = spans[i].Kind == VxKind.Preprocessor;
                if (preprocessor ? !preprocessorOn : !macroOn)
                    continue;
                IClassificationType type = preprocessor ? _preprocessor : _macro;
                result.Add(new ClassificationSpan(
                    new SnapshotSpan(span.Snapshot, (int)spans[i].Start, (int)spans[i].Length), type));
            }
            return result;
        }

        private VxSpan[] GetSpans(ITextSnapshot snapshot)
        {
            if (_cachedSnapshot != snapshot)
            {
                _cachedSpans = Engine.Classify(snapshot.GetText());
                _cachedSnapshot = snapshot;
            }
            return _cachedSpans;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            ITextSnapshot snapshot = e.After;
            ClassificationChanged?.Invoke(this,
                new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
    }
}
