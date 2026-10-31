using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.Parsing;

namespace VisualXtsi.Refactoring
{
    /// <summary>The active C/C++ editor, its caret and what the engine says is there.</summary>
    internal sealed class RefactorContext
    {
        public IWpfTextView View;
        public string Path;
        public ITextSnapshot Snapshot; // the text the engine parsed (and the positions refer to)
        public int Line, Column;       // of the caret, in Snapshot
        public CursorInfo Info;        // null: nothing there, or not parsed

        public bool InHeader => Documents.IsHeader(Path);

        private static RefactorContext _peeked;
        private static (ITextView View, int Version, int Caret, DateTime At) _peekKey;

        /// <summary>
        /// For menu states: what is at the caret by the last parse, without waiting (cached briefly,
        /// since a menu asks once per item). Info is null while the file is being parsed.
        /// </summary>
        public static RefactorContext Peek()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string path = EditorCommands.ActiveCppDocument(null);
            IWpfTextView view = path == null ? null : GoToDefinitionCommand.ActiveView();
            if (view == null)
                return null;
            var key = (view as ITextView, view.TextSnapshot.Version.VersionNumber, view.Caret.Position.BufferPosition.Position, DateTime.UtcNow);
            if (_peeked != null && _peekKey.View == key.Item1 && _peekKey.Version == key.Item2 && _peekKey.Caret == key.Item3 &&
                (key.Item4 - _peekKey.At).TotalSeconds < 2)
                return _peeked;
            var context = Create(view, path, wait: false);
            _peeked = context;
            _peekKey = key;
            return context;
        }

        /// <summary>For running a refactoring: makes sure the engine has the current text first, then asks. UI thread.</summary>
        public static async Task<RefactorContext> AtCaretAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = EditorCommands.ActiveCppDocument(null);
            IWpfTextView view = path == null ? null : GoToDefinitionCommand.ActiveView();
            if (view == null)
                return null;
            DocumentParser parser = DocumentParser.TryGet(view.TextBuffer);
            if (parser != null && !await parser.WhenCurrentAsync())
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EditorCommands.Status("the file could not be parsed; refactorings need a parsed file");
                return null;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var context = Create(view, path, wait: false);
            if (context != null)
            {
                int line = context.Line, column = context.Column;
                context.Info = await Task.Run(() => Engine.CursorInfo(path, line, column, wait: true));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            return context;
        }

        private static RefactorContext Create(IWpfTextView view, string path, bool wait)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocumentParser parser = DocumentParser.TryGet(view.TextBuffer);
            ITextSnapshot parsed = parser?.ParsedSnapshot;
            if (parsed == null)
                return new RefactorContext { View = view, Path = path };
            SnapshotPoint caret = view.Caret.Position.BufferPosition.TranslateTo(parsed, PointTrackingMode.Negative);
            ITextSnapshotLine line = caret.GetContainingLine();
            var context = new RefactorContext
            {
                View = view,
                Path = path,
                Snapshot = parsed,
                Line = line.LineNumber + 1,
                Column = caret.Position - line.Start.Position + 1,
            };
            context.Info = Engine.CursorInfo(path, context.Line, context.Column, wait);
            return context;
        }

        /// <summary>The offset in <see cref="Snapshot"/> of a position the engine gave for this file.</summary>
        public int OffsetOf(int line, int column)
        {
            ITextSnapshotLine l = Snapshot.GetLineFromLineNumber(Math.Min(Snapshot.LineCount, Math.Max(1, line)) - 1);
            return Math.Min(l.End.Position, l.Start.Position + Math.Max(0, column - 1));
        }
    }
}
