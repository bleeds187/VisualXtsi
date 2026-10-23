using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Parsing
{
    /// <summary>
    /// Keeps the engine's translation unit for one C/C++ buffer up to date in the background:
    /// parses when the document opens and reparses shortly after edits stop. Consumers read
    /// <see cref="ParsedSnapshot"/> and never wait for a parse.
    /// </summary>
    internal sealed class DocumentParser : IVxSettingsListener
    {
        private const int ReparseDelayMs = 750;

        private readonly ITextBuffer _buffer;
        private readonly string _path;
        private readonly object _gate = new object();
        private Timer _debounce;
        private bool _running;
        private bool _pending;
        private bool _unavailable;
        private bool _skipped; // a parse was due while VisualXtsi was disabled
        private int _views;
        private volatile ITextSnapshot _parsedSnapshot;
        private volatile SemanticResult _semantic;

        private DocumentParser(ITextBuffer buffer, string path)
        {
            _buffer = buffer;
            _path = path;
            VxSettings.AddListener(this);
        }

        /// <summary>Catches up on the parse skipped while VisualXtsi was disabled.</summary>
        public void SettingsChanged(VxSettings settings)
        {
            bool due;
            lock (_gate)
            {
                due = settings.Enabled && _skipped;
                _skipped = false;
            }
            if (due)
                StartParse();
        }

        /// <summary>The snapshot the engine's translation unit was built from, or null before the first parse.</summary>
        public ITextSnapshot ParsedSnapshot => _parsedSnapshot;

        /// <summary>Semantic spans of the last parse, with the snapshot they belong to; null before the first parse.</summary>
        public SemanticResult Semantic => _semantic;

        /// <summary>Raised on a background thread after each parse with (previous, current) results.</summary>
        public event Action<SemanticResult, SemanticResult> SemanticUpdated;

        public string Path => _path;

        public static DocumentParser TryGet(ITextBuffer buffer)
        {
            return buffer.Properties.TryGetProperty(typeof(DocumentParser), out DocumentParser parser) ? parser : null;
        }

        /// <summary>Called for each document view opened on the buffer.</summary>
        public static DocumentParser Attach(ITextBuffer buffer)
        {
            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
                return null;
            bool created = false;
            DocumentParser parser = buffer.Properties.GetOrCreateSingletonProperty(typeof(DocumentParser), () =>
            {
                created = true;
                return new DocumentParser(buffer, document.FilePath);
            });
            if (created && buffer.Properties.TryGetProperty(typeof(IDocumentParserListener), out IDocumentParserListener listener))
                listener.ParserCreated(parser);
            lock (parser._gate)
            {
                if (parser._views++ == 0)
                {
                    buffer.Changed += parser.OnBufferChanged;
                    parser._debounce = new Timer(_ => parser.StartParse(), null, 0, Timeout.Infinite);
                }
            }
            return parser;
        }

        /// <summary>Called when a view closes; the last one releases the engine's translation unit.</summary>
        public void Detach()
        {
            lock (_gate)
            {
                if (--_views > 0)
                    return;
                _buffer.Changed -= OnBufferChanged;
                _debounce?.Dispose();
                _debounce = null;
                _parsedSnapshot = null;
                _buffer.Properties.RemoveProperty(typeof(DocumentParser));
            }
            _ = Task.Run(() => Engine.Release(_path));
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            lock (_gate)
                _debounce?.Change(ReparseDelayMs, Timeout.Infinite);
        }

        private void StartParse()
        {
            lock (_gate)
            {
                if (_unavailable || _views == 0)
                    return;
                if (!VxSettings.Current.Enabled)
                {
                    _skipped = true;
                    return;
                }
                if (_running)
                {
                    _pending = true;
                    return;
                }
                _running = true;
            }
            _ = Task.Run(ParseLoop);
        }

        private void ParseLoop()
        {
            for (;;)
            {
                VxStatus status = VxStatus.NotAvailable;
                try
                {
                    ITextSnapshot snapshot = _buffer.CurrentSnapshot;
                    bool first = _parsedSnapshot == null;
                    var timer = Stopwatch.StartNew();
                    status = Engine.Parse(_path, snapshot.GetText());
                    long elapsed = timer.ElapsedMilliseconds;
                    long semanticMs = 0;
                    if (status == VxStatus.Ok)
                    {
                        _parsedSnapshot = snapshot;
                        timer.Restart();
                        var result = new SemanticResult(snapshot, Engine.SemanticTokens(_path));
                        semanticMs = timer.ElapsedMilliseconds;
                        SemanticResult previous = _semantic;
                        _semantic = result;
                        SemanticUpdated?.Invoke(previous, result);
                    }
                    string what = first ? "Parsed" : "Reparsed";
                    if (first || status != VxStatus.Ok)
                    {
                        VxArgsSource source = Engine.DiscoverArgs(_path, out int count);
                        Log.Write($"{what} {_path}: {status} in {elapsed} ms + {semanticMs} ms semantic (arguments: {Describe(source)}, {count})");
                        // The module's next files can start from a shared PCH: build it now, off the parse loop.
                        if (first && status == VxStatus.Ok && source == VxArgsSource.Vcxproj)
                            _ = Task.Run(() => PrepareModulePch());
                    }
                    else
                    {
                        Log.Write($"{what} {_path} in {elapsed} ms + {semanticMs} ms semantic");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"Parsing {_path} failed: {ex}");
                }
                finally
                {
                    // Always settle the state, or an exception would stop all future reparses.
                    lock (_gate)
                    {
                        if (status == VxStatus.NotAvailable)
                            _unavailable = true;
                        _running = _pending && !_unavailable && _views > 0;
                        _pending = false;
                    }
                }
                lock (_gate)
                {
                    if (!_running)
                        return;
                }
            }
        }

        private void PrepareModulePch()
        {
            try
            {
                var timer = Stopwatch.StartNew();
                VxPchStatus pch = Engine.PrepareModulePch(_path);
                if (pch == VxPchStatus.Ready && timer.ElapsedMilliseconds < 1000)
                    return; // it already existed
                Log.Write($"Module PCH for {_path}: {pch} in {timer.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Write($"Module PCH for {_path} failed: {ex.Message}");
            }
        }

        private static string Describe(VxArgsSource source)
        {
            switch (source)
            {
                case VxArgsSource.CompileCommands: return "compile_commands.json";
                case VxArgsSource.Vcxproj: return "Unreal project files";
                default: return "defaults";
            }
        }
    }
}
