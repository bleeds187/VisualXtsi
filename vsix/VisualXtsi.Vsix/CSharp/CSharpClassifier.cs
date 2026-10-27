using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using VisualXtsi.Classification;
using VisualXtsi.Options;

namespace VisualXtsi.CSharp
{
    [Export(typeof(IClassifierProvider))]
    [ContentType("CSharp")]
    internal sealed class CSharpClassifierProvider : IClassifierProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry = null;

        [Import]
        internal IClassificationFormatMapService FormatMaps = null;

        public IClassifier GetClassifier(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // classifier providers are called on the UI thread
            VxSettings.EnsureLoaded();
            ThemeDefaults.EnsureApplied(FormatMaps, Registry);
            return buffer.Properties.GetOrCreateSingletonProperty(() => new CSharpClassifier(buffer, Registry));
        }
    }

    /// <summary>
    /// Colors C# identifiers with VisualXtsi's categories (and so its themes), from Roslyn's own
    /// classification of the open document. Work happens off the UI thread, only for the lines the
    /// editor asks about, and is cancelled when the text changes; until a range is done, the colors
    /// of the previous version of the text are shown, mapped forward. In Unreal Build.cs and Target.cs
    /// files, which Roslyn sees outside any project, names it cannot resolve are colored from
    /// UnrealBuildTool's rules code in the symbol index (<see cref="UbtSymbols"/>).
    /// </summary>
    internal sealed class CSharpClassifier : IClassifier, IVxSettingsListener
    {
        // Roslyn classification names (Microsoft.CodeAnalysis.Classification.ClassificationTypeNames)
        // to our categories. Type names inside an attribute become attributes; static fields and
        // methods become globals and static methods.
        private static readonly Dictionary<string, string> Categories = new Dictionary<string, string>
        {
            ["class name"] = SemanticNames.Type,
            ["record class name"] = SemanticNames.Type,
            ["struct name"] = SemanticNames.Type,
            ["record struct name"] = SemanticNames.Type,
            ["enum name"] = SemanticNames.Type,
            ["module name"] = SemanticNames.Type,
            ["interface name"] = SemanticNames.Interface,
            ["delegate name"] = SemanticNames.Delegate,
            ["type parameter name"] = SemanticNames.TypeParameter,
            ["field name"] = SemanticNames.Field,
            ["constant name"] = SemanticNames.Field,
            ["enum member name"] = SemanticNames.EnumConstant,
            ["local name"] = SemanticNames.Local,
            ["parameter name"] = SemanticNames.Parameter,
            ["method name"] = SemanticNames.Method,
            ["extension method name"] = SemanticNames.Method,
            ["property name"] = SemanticNames.Property,
            ["event name"] = SemanticNames.Event,
            ["namespace name"] = SemanticNames.Namespace,
        };

        private const string StaticSymbol = "static symbol";
        private const string Identifier = "identifier";

        private readonly ITextBuffer _buffer;
        private readonly string _rulesFile; // the path of an Unreal Build.cs or Target.cs; null for other files
        private readonly Dictionary<string, IClassificationType> _types = new Dictionary<string, IClassificationType>();
        private HashSet<string> _enabled = new HashSet<string>();

        private readonly object _gate = new object();
        private ITextSnapshot _snapshot; // the version _spans and _done describe
        private List<Classified> _spans = new List<Classified>(); // sorted by start
        private NormalizedSpanCollection _done = new NormalizedSpanCollection();
        private NormalizedSpanCollection _requested = new NormalizedSpanCollection();
        private ITextSnapshot _previousSnapshot; // shown, mapped forward, until ranges are done
        private List<Classified> _previousSpans = new List<Classified>();
        private CancellationTokenSource _cancel = new CancellationTokenSource();
        private bool _working;

        private struct Classified
        {
            public int Start, Length;
            public string Category;
        }

        public CSharpClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            foreach (string category in Categories.Values.Concat(new[] { SemanticNames.Global, SemanticNames.StaticMethod, SemanticNames.Attribute }).Distinct())
                _types[category] = registry.GetClassificationType(category);
            UpdateEnabled(VxSettings.Current);
            VxSettings.AddListener(this);
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) && CSharpNavigation.IsUnrealRules(document.FilePath))
            {
                string path = _rulesFile = document.FilePath; // C# files have no parser to index their project: do it here
                _ = Task.Run(() => Commands.OpenFileInSolutionCommand.IndexDocument(path));
            }
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        public void SettingsChanged(VxSettings settings)
        {
            UpdateEnabled(settings);
            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void UpdateEnabled(VxSettings settings)
        {
            _enabled = new HashSet<string>(_types.Keys.Where(settings.IsColorEnabled));
        }

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            HashSet<string> enabled = _enabled;
            if (enabled.Count == 0)
                return result;
            lock (_gate)
            {
                if (_snapshot != span.Snapshot)
                    NewVersion(span.Snapshot);
                var lines = new SnapshotSpan(span.Start.GetContainingLine().Start, span.End.GetContainingLine().EndIncludingLineBreak);
                NormalizedSpanCollection missing = NormalizedSpanCollection.Difference(new NormalizedSpanCollection(lines), _done);
                if (missing.Count > 0)
                {
                    _requested = NormalizedSpanCollection.Union(_requested, missing);
                    if (!_working)
                    {
                        _working = true;
                        _ = Task.Run(() => WorkAsync(_cancel.Token));
                    }
                }
                Collect(_spans, span.Snapshot, span, enabled, result);
                if (missing.Count > 0 && _previousSnapshot != null)
                    foreach (Span gap in missing)
                    {
                        var inGap = new SnapshotSpan(span.Snapshot, gap).Overlap(span);
                        if (inGap.HasValue)
                            CollectMapped(inGap.Value, enabled, result);
                    }
            }
            return result;
        }

        // The text changed: start over for the new version, keeping the old colors to show meanwhile.
        private void NewVersion(ITextSnapshot snapshot)
        {
            if (_snapshot != null && _done.Count > 0)
            {
                _previousSnapshot = _snapshot;
                _previousSpans = _spans;
            }
            _snapshot = snapshot;
            _spans = new List<Classified>();
            _done = new NormalizedSpanCollection();
            _requested = new NormalizedSpanCollection();
            _cancel.Cancel();
            _cancel = new CancellationTokenSource();
            _working = false;
        }

        private void Collect(List<Classified> spans, ITextSnapshot snapshot, SnapshotSpan span, HashSet<string> enabled, List<ClassificationSpan> result)
        {
            for (int i = FirstEndingAfter(spans, span.Start); i < spans.Count && spans[i].Start < span.End; i++)
                if (enabled.Contains(spans[i].Category))
                    result.Add(new ClassificationSpan(new SnapshotSpan(snapshot, spans[i].Start, spans[i].Length), _types[spans[i].Category]));
        }

        private void CollectMapped(SnapshotSpan span, HashSet<string> enabled, List<ClassificationSpan> result)
        {
            SnapshotSpan old = span.TranslateTo(_previousSnapshot, SpanTrackingMode.EdgeInclusive);
            for (int i = FirstEndingAfter(_previousSpans, old.Start); i < _previousSpans.Count && _previousSpans[i].Start < old.End; i++)
            {
                Classified c = _previousSpans[i];
                if (!enabled.Contains(c.Category))
                    continue;
                SnapshotSpan mapped = new SnapshotSpan(_previousSnapshot, c.Start, c.Length).TranslateTo(span.Snapshot, SpanTrackingMode.EdgeExclusive);
                if (mapped.Length == c.Length && mapped.IntersectsWith(span)) // tokens edited since are left alone
                    result.Add(new ClassificationSpan(mapped, _types[c.Category]));
            }
        }

        private static int FirstEndingAfter(List<Classified> spans, int position)
        {
            int lo = 0, hi = spans.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (spans[mid].Start + spans[mid].Length <= position)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        // Background: classifies the requested ranges of the current version until none are left.
        private async Task WorkAsync(CancellationToken cancel)
        {
            try
            {
                await Task.Delay(30, cancel); // let the editor ask for all visible lines first
                for (;;)
                {
                    ITextSnapshot snapshot;
                    NormalizedSpanCollection ranges;
                    lock (_gate)
                    {
                        if (cancel.IsCancellationRequested)
                            return;
                        if (_requested.Count == 0)
                        {
                            _working = false;
                            return;
                        }
                        snapshot = _snapshot;
                        ranges = _requested;
                        _requested = new NormalizedSpanCollection();
                    }
                    List<Classified> found = await ClassifyAsync(snapshot, ranges, _rulesFile, cancel);
                    if (_rulesFile != null)
                        WhenIndexed(snapshot);
                    lock (_gate)
                    {
                        if (cancel.IsCancellationRequested || _snapshot != snapshot)
                            return;
                        _spans.AddRange(found);
                        _spans.Sort((a, b) => a.Start.CompareTo(b.Start));
                        _done = NormalizedSpanCollection.Union(_done, ranges);
                    }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancel);
                    foreach (Span range in ranges)
                        ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, range)));
                    await TaskScheduler.Default;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"C# classification failed: {ex.Message}");
                lock (_gate)
                    _working = false;
            }
        }

        // While the symbol index is still filling (the rules code is indexed when the first rules file
        // opens), rules files are colored again once it has settled with more symbols than before.
        private int _waitingForIndex;

        private void WhenIndexed(ITextSnapshot snapshot)
        {
            if (Interlocked.Exchange(ref _waitingForIndex, 1) == 1)
                return;
            _ = Task.Run(async () =>
            {
                long before = UbtSymbols.Stamp(out int pending), last = before, now = before;
                for (int i = 0, stable = 0; i < 300 && stable < 3; i++) // settled: nothing pending, unchanged for 3 s
                {
                    await Task.Delay(1000);
                    now = UbtSymbols.Stamp(out pending);
                    stable = pending == 0 && now == last ? stable + 1 : 0;
                    last = now;
                }
                lock (_gate)
                {
                    _waitingForIndex = 0;
                    if (_snapshot != snapshot || now == before)
                        return; // edited since (being redone anyway), or nothing new
                    _previousSnapshot = _snapshot;
                    _previousSpans = _spans;
                    _spans = new List<Classified>();
                    _done = new NormalizedSpanCollection();
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            });
        }

        private static async Task<List<Classified>> ClassifyAsync(ITextSnapshot snapshot, NormalizedSpanCollection ranges, string rulesFile, CancellationToken cancel)
        {
            var found = new List<Classified>();
            Document document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
                return found;
            SyntaxNode root = await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false);
            foreach (Span range in ranges)
            {
                IEnumerable<ClassifiedSpan> spans = await Classifier.GetClassifiedSpansAsync(document, TextSpan.FromBounds(range.Start, range.End), cancel).ConfigureAwait(false);
                var statics = new HashSet<TextSpan>(spans.Where(s => s.ClassificationType == StaticSymbol).Select(s => s.TextSpan));
                foreach (ClassifiedSpan s in spans)
                {
                    if (!Categories.TryGetValue(s.ClassificationType, out string category))
                        continue;
                    if (category == SemanticNames.Field && s.ClassificationType == "field name" && statics.Contains(s.TextSpan))
                        category = SemanticNames.Global;
                    else if (category == SemanticNames.Method && s.ClassificationType == "method name" && statics.Contains(s.TextSpan))
                        category = SemanticNames.StaticMethod;
                    else if (category == SemanticNames.Type && InAttributeName(root, s.TextSpan))
                        category = SemanticNames.Attribute;
                    found.Add(new Classified { Start = s.TextSpan.Start, Length = s.TextSpan.Length, Category = category });
                }
                if (rulesFile == null)
                    continue;
                // Names Roslyn left unresolved: UnrealBuildTool's types and members, by name.
                var classified = new HashSet<TextSpan>(spans.Where(s => Categories.ContainsKey(s.ClassificationType)).Select(s => s.TextSpan));
                foreach (ClassifiedSpan s in spans)
                {
                    if (s.ClassificationType != Identifier || classified.Contains(s.TextSpan))
                        continue;
                    int before = s.TextSpan.Start - 1;
                    while (before >= 0 && char.IsWhiteSpace(snapshot[before]))
                        before--;
                    string category = UbtSymbols.CategoryFor(snapshot.GetText(s.TextSpan.Start, s.TextSpan.Length), before >= 0 && snapshot[before] == '.', rulesFile);
                    if (category != null)
                        found.Add(new Classified { Start = s.TextSpan.Start, Length = s.TextSpan.Length, Category = category });
                }
            }
            return found;
        }

        // [Serializable], [Obsolete("...")], [System.Diagnostics.Conditional("X")]: the attribute's name.
        private static bool InAttributeName(SyntaxNode root, TextSpan span)
        {
            SyntaxNode node = root?.FindToken(span.Start).Parent;
            while (node is NameSyntax && !(node.Parent is AttributeSyntax))
                node = node.Parent;
            return node?.Parent is AttributeSyntax attribute && attribute.Name == node;
        }
    }
}
