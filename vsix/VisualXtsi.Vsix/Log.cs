using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualXtsi
{
    /// <summary>
    /// Writes to %LOCALAPPDATA%\VisualXtsi\VisualXtsi.log and, once <see cref="EnsurePane"/> has
    /// run on the UI thread, to a "VisualXtsi" pane in the Output window. Write is callable from
    /// any thread and never throws.
    /// </summary>
    internal static class Log
    {
        private static readonly Guid PaneGuid = new Guid("3c1f7a52-9d4e-4b6a-8f0d-5e2b7c9a4d16");
        private static readonly object Gate = new object();
        public static readonly string FilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualXtsi", "VisualXtsi.log");
        private static volatile IVsOutputWindowPane _pane;

        /// <summary>Creates the Output window pane. Call on the UI thread (e.g. when a view opens).</summary>
        public static void EnsurePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null)
                return;
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow output))
                {
                    WriteFile("Output window service unavailable");
                    return;
                }
                Guid guid = PaneGuid;
                if (output.GetPane(ref guid, out IVsOutputWindowPane pane) != VSConstants.S_OK)
                {
                    int created = output.CreatePane(ref guid, "VisualXtsi", 1, 1);
                    int got = output.GetPane(ref guid, out pane);
                    if (pane == null)
                        WriteFile($"Output window pane not created (CreatePane 0x{created:X8}, GetPane 0x{got:X8})");
                }
                _pane = pane;
            }
            catch (Exception ex)
            {
                WriteFile($"Creating the Output window pane failed: {ex.Message}");
            }
        }

        public static void Write(string message)
        {
            WriteFile(message);
            IVsOutputWindowPane pane = _pane;
            if (pane == null)
                return;
            try
            {
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is documented as callable from any thread.
                pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
#pragma warning restore VSTHRD010
            }
            catch (Exception)
            {
                // The pane is a convenience; the file has the message.
            }
        }

        private static void WriteFile(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // Logging must never break the caller.
            }
        }
    }
}
