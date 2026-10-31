using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VisualXtsi.Refactoring
{
    /// <summary>Surround With: wraps the selection (or the caret's line) in a snippet, as one undo step.</summary>
    internal static class SurroundWith
    {
        public static void Apply(IWpfTextView view, Snippet snippet, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ITextBuffer buffer = view.TextBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            SnapshotSpan selection = view.Selection.IsEmpty
                ? new SnapshotSpan(view.Caret.Position.BufferPosition, 0)
                : view.Selection.StreamSelectionSpan.SnapshotSpan.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
            bool multiLine = snippet.Text.Contains('\n');
            Span replace;
            string baseIndent = "";
            if (multiLine)
            {
                // Whole lines: a selection ending at the start of a line leaves that line out; nothing
                // selected wraps the caret's line (or, on a blank line, leaves the body empty).
                ITextSnapshotLine first = selection.Start.GetContainingLine();
                SnapshotPoint end = selection.End;
                if (!selection.IsEmpty && end == end.GetContainingLine().Start && end > selection.Start)
                    end -= 1;
                ITextSnapshotLine last = end.GetContainingLine();
                replace = Span.FromBounds(first.Start, last.End);
                baseIndent = CommonIndent(snapshot, first.LineNumber, last.LineNumber);
            }
            else
            {
                replace = selection.Span;
            }
            string selected = snapshot.GetText(replace);
            string newline = selection.Start.GetContainingLine().GetLineBreakText();
            if (string.IsNullOrEmpty(newline))
                newline = view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId);
            string unit = IndentUnit(view);
            Expansion expansion = Snippets.Expand(snippet, selected, baseIndent, unit, Path.GetFileName(path), newline);

            ITextSnapshot after;
            using (ITextEdit edit = buffer.CreateEdit())
            {
                edit.Replace(replace, expansion.Text);
                after = edit.Apply();
            }
            view.Selection.Clear();
            if (expansion.Fields.Count > 0)
                SnippetSession.Start(view, after, replace.Start, expansion);
            else
                view.Caret.MoveTo(new SnapshotPoint(after, replace.Start + expansion.End));
        }

        /// <summary>One level of indentation in the editor's settings: a tab or spaces.</summary>
        public static string IndentUnit(ITextView view) =>
            view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId)
                ? new string(' ', view.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId))
                : "\t";

        // The indentation the given lines share (blank lines aside).
        private static string CommonIndent(ITextSnapshot snapshot, int firstLine, int lastLine)
        {
            string common = null;
            for (int i = firstLine; i <= lastLine; i++)
            {
                string text = snapshot.GetLineFromLineNumber(i).GetText();
                if (text.Trim().Length == 0)
                    continue;
                string indent = text.Substring(0, text.Length - text.TrimStart().Length);
                if (common == null)
                {
                    common = indent;
                    continue;
                }
                int n = 0;
                while (n < common.Length && n < indent.Length && common[n] == indent[n])
                    n++;
                common = common.Substring(0, n);
            }
            return common ?? "";
        }
    }

    /// <summary>
    /// The fields of an inserted snippet: Tab and Shift+Tab move between them (each selected for
    /// typing over), Enter or Esc finishes at the snippet's end. Fields of the same name take the
    /// text of the first when moving on or finishing. Ends by itself once the caret leaves the fields.
    /// </summary>
    internal sealed class SnippetSession : IOleCommandTarget
    {
        private readonly IWpfTextView _view;
        private readonly List<(string Name, ITrackingSpan Span)> _fields = new List<(string, ITrackingSpan)>();
        private readonly List<string> _order = new List<string>();
        private readonly ITrackingPoint _end;
        private IVsTextView _adapter;
        private IOleCommandTarget _next;
        private int _current;
        private bool _moving;

        private SnippetSession(IWpfTextView view, ITextSnapshot snapshot, int offset, Expansion expansion)
        {
            _view = view;
            foreach (var f in expansion.Fields)
            {
                _fields.Add((f.Name, snapshot.CreateTrackingSpan(offset + f.Start, f.Length, SpanTrackingMode.EdgeInclusive)));
                if (!_order.Contains(f.Name))
                    _order.Add(f.Name);
            }
            _end = snapshot.CreateTrackingPoint(offset + expansion.End, PointTrackingMode.Positive);
        }

        public static void Start(IWpfTextView view, ITextSnapshot snapshot, int offset, Expansion expansion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.Properties.TryGetProperty(typeof(SnippetSession), out SnippetSession old))
                old.Finish(moveToEnd: false);
            var session = new SnippetSession(view, snapshot, offset, expansion);
            var adapters = (Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel)?.GetService<IVsEditorAdaptersFactoryService>();
            session._adapter = adapters?.GetViewAdapter(view);
            if (session._adapter == null || session._adapter.AddCommandFilter(session, out session._next) != VSConstants.S_OK)
            {
                session._adapter = null;
                session.SelectField(0);
                return; // no keys to move with: just select the first field
            }
            view.Properties[typeof(SnippetSession)] = session;
            view.Caret.PositionChanged += session.OnCaretMoved;
            view.Closed += session.OnClosed;
            session.SelectField(0);
        }

        private void SelectField(int index)
        {
            _current = (index + _order.Count) % _order.Count;
            SnapshotSpan span = _fields.First(f => f.Name == _order[_current]).Span.GetSpan(_view.TextBuffer.CurrentSnapshot);
            _moving = true;
            try
            {
                _view.Selection.Select(span, isReversed: false);
                _view.Caret.MoveTo(span.End);
                _view.ViewScroller.EnsureSpanVisible(span);
            }
            finally
            {
                _moving = false;
            }
        }

        // Same-named fields take the first one's text.
        private void Mirror()
        {
            ITextSnapshot snapshot = _view.TextBuffer.CurrentSnapshot;
            var edits = new List<(Span Span, string Text)>();
            foreach (string name in _order)
            {
                var spans = _fields.Where(f => f.Name == name).Select(f => f.Span.GetSpan(snapshot)).ToList();
                string text = spans[0].GetText();
                foreach (SnapshotSpan other in spans.Skip(1))
                    if (other.GetText() != text)
                        edits.Add((other.Span, text));
            }
            if (edits.Count == 0)
                return;
            using (ITextEdit edit = _view.TextBuffer.CreateEdit())
            {
                foreach (var e in edits)
                    edit.Replace(e.Span, e.Text);
                edit.Apply();
            }
        }

        private void Finish(bool moveToEnd)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view.Properties.RemoveProperty(typeof(SnippetSession));
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.Closed -= OnClosed;
            _adapter?.RemoveCommandFilter(this);
            _adapter = null;
            Mirror();
            if (moveToEnd)
            {
                _view.Selection.Clear();
                _view.Caret.MoveTo(_end.GetPoint(_view.TextBuffer.CurrentSnapshot));
            }
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_moving)
                return;
            ITextSnapshot snapshot = _view.TextBuffer.CurrentSnapshot;
            SnapshotPoint caret = e.NewPosition.BufferPosition;
            if (!_fields.Any(f => f.Span.GetSpan(snapshot).Contains(caret) || f.Span.GetSpan(snapshot).End == caret))
                Finish(moveToEnd: false);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Finish(moveToEnd: false);
        }

        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (group == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)id)
                {
                    case VSConstants.VSStd2KCmdID.TAB:
                        Mirror();
                        SelectField(_current + 1);
                        return VSConstants.S_OK;
                    case VSConstants.VSStd2KCmdID.BACKTAB:
                        Mirror();
                        SelectField(_current - 1);
                        return VSConstants.S_OK;
                    case VSConstants.VSStd2KCmdID.RETURN:
                    case VSConstants.VSStd2KCmdID.CANCEL:
                        Finish(moveToEnd: true);
                        return VSConstants.S_OK;
                }
            }
            return _next.Exec(ref group, id, options, input, output);
        }

        public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _next.QueryStatus(ref group, count, commands, text);
        }
    }
}
