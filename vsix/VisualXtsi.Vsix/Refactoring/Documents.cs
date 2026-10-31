using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VisualXtsi.Refactoring
{
    /// <summary>A replacement in a file, made against the text <see cref="Documents.Read"/> returned.</summary>
    internal sealed class FileEdit
    {
        public string Path;
        public int Start;
        public int Length;
        public string OldText; // checked before applying: the file must not have changed meanwhile
        public string NewText;

        public static FileEdit Replace(string path, string text, int start, int length, string newText) =>
            new FileEdit { Path = path, Start = start, Length = length, OldText = text.Substring(start, length), NewText = newText };

        public static FileEdit Insert(string path, int at, string newText) =>
            new FileEdit { Path = path, Start = at, Length = 0, OldText = "", NewText = newText };
    }

    /// <summary>Files as the editor has them, and applying edits to them as one undo step.</summary>
    internal static class Documents
    {
        /// <summary>A file's text: the open document's (unsaved changes included), else the file on disk.</summary>
        public static string Read(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ITextBuffer buffer = OpenBuffer(path, open: false);
            if (buffer != null)
                return buffer.CurrentSnapshot.GetText();
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>The editor buffer of a file; opens the document when <paramref name="open"/> (without activating it).</summary>
        public static ITextBuffer OpenBuffer(string path, bool open)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IVsWindowFrame frame = null;
            if (!VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out frame) && open)
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out frame);
            if (frame == null || frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out object docData) != VSConstants.S_OK)
                return null;
            var adapters = (Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel)?.GetService<IVsEditorAdaptersFactoryService>();
            return docData is IVsTextBuffer vsBuffer ? adapters?.GetDocumentBuffer(vsBuffer) : null;
        }

        /// <summary>
        /// Applies edits (in any number of files) as one undo step. Every edit is checked against the
        /// current text first; if any file changed since the edits were made, nothing is applied.
        /// Files not open yet are opened (and stay open, modified, for saving or undoing).
        /// </summary>
        public static bool Apply(IReadOnlyList<FileEdit> edits, string description, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            var byFile = edits.GroupBy(e => Path.GetFullPath(e.Path), StringComparer.OrdinalIgnoreCase).ToList();
            var buffers = new List<(ITextBuffer Buffer, List<FileEdit> Edits)>();
            foreach (var file in byFile)
            {
                ITextBuffer buffer = OpenBuffer(file.Key, open: true);
                if (buffer == null)
                {
                    error = $"cannot open {Path.GetFileName(file.Key)}";
                    return false;
                }
                ITextSnapshot snapshot = buffer.CurrentSnapshot;
                foreach (FileEdit e in file)
                    if (e.Start + e.Length > snapshot.Length || snapshot.GetText(e.Start, e.Length) != e.OldText)
                    {
                        error = $"{Path.GetFileName(file.Key)} changed meanwhile; nothing was changed";
                        return false;
                    }
                buffers.Add((buffer, file.ToList()));
            }
            IVsLinkedUndoTransactionManager linked = buffers.Count > 1
                ? Package.GetGlobalService(typeof(SVsLinkedUndoTransactionManager)) as IVsLinkedUndoTransactionManager
                : null;
            bool open = linked != null && linked.OpenLinkedUndo((uint)LinkedTransactionFlags.mdtDefault, description) == VSConstants.S_OK;
            try
            {
                foreach (var (buffer, list) in buffers)
                {
                    using (ITextEdit edit = buffer.CreateEdit())
                    {
                        foreach (FileEdit e in list)
                            edit.Replace(e.Start, e.Length, e.NewText);
                        edit.Apply();
                    }
                }
            }
            catch (Exception ex)
            {
                if (open)
                    linked.AbortLinkedUndo();
                open = false;
                error = ex.Message;
                Log.Write($"{description} failed: {ex}");
                return false;
            }
            finally
            {
                if (open)
                    linked.CloseLinkedUndo();
            }
            return true;
        }

        public static bool IsHeader(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hh" || ext == ".hxx" || ext == ".inl";
        }

        /// <summary>The source file of a header (by the header/source toggle), or null.</summary>
        public static string SourceFor(string header) =>
            Interop.Engine.Counterparts(header).FirstOrDefault(p => !IsHeader(p) && !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        /// <summary>The header of a source file, or null.</summary>
        public static string HeaderFor(string source) => Interop.Engine.Counterparts(source).FirstOrDefault(IsHeader);
    }
}
