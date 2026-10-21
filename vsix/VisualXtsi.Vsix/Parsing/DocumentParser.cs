using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Interop;

namespace VisualXtsi.Parsing
{
    /// <summary>
    /// Keeps the engine's translation unit for one C/C++ buffer up to date in the background:
    /// parses when the document opens and reparses shortly after edits stop. Consumers read
    /// <see cref="ParsedSnapshot"/> and never wait for a parse.
    /// </summary>
    internal sealed class DocumentParser
    {
        private const int ReparseDelayMs = 750;

        private readonly ITextBuffer _buffer;
        private readonly string _path;
        private readonly object _gate = new object();
        private Timer _debounce;
        private bool _running;
        private bool _pending;
        private bool _unavailable;
        private int _views;
        private volatile ITextSnapshot _parsedSnapshot;

        private DocumentParser(ITextBuffer buffer, string path)
        {
            _buffer = buffer;
            _path = path;
        }

        /// <summary>The snapshot the engine's translation unit was built from, or null before the first parse.</summary>
        public ITextSnapshot ParsedSnapshot => _parsedSnapshot;

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
            DocumentParser parser = buffer.Properties.GetOrCreateSingletonProperty(
                typeof(DocumentParser), () => new DocumentParser(buffer, document.FilePath));
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
                    if (status == VxStatus.Ok)
                        _parsedSnapshot = snapshot;
                    string what = first ? "Parsed" : "Reparsed";
                    if (first || status != VxStatus.Ok)
                    {
                        VxArgsSource source = Engine.DiscoverArgs(_path, out int count);
                        Log.Write($"{what} {_path}: {status} in {elapsed} ms (arguments: {Describe(source)}, {count})");
                    }
                    else
                    {
                        Log.Write($"{what} {_path} in {elapsed} ms");
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
