using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualXtsi.UI
{
    /// <summary>
    /// Extensions > VisualXtsi > Options...: the Options dialog as a floating tool window. It does
    /// not block Visual Studio, so colors and themes can be judged on real code (switch tabs,
    /// scroll) while they are previewed live; it can sit on any monitor, and Visual Studio
    /// remembers where. Options... again brings it to the front.
    /// </summary>
    [Guid("0954a713-3088-482f-a9ab-4bddf3b35693")]
    public sealed class OptionsToolWindow : ToolWindowPane
    {
        private OptionsDialog _dialog;

        public OptionsToolWindow() : base(null)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // FindToolWindow creates it on the UI thread
            Caption = "VisualXtsi Options";
            Open(); // the frame needs the content when it is created
        }

        // A fresh set of pending settings every time the window is shown again.
        private void Open()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dialog = new OptionsDialog();
            Content = _dialog.TakeContentForToolWindow(CloseFrame, null);
        }

        private void CloseFrame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (Frame as IVsWindowFrame)?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        /// <summary>Shows the window (creating it, or starting over if it was closed) and focuses it.</summary>
        public static void ShowOptions(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = package.FindToolWindow(typeof(OptionsToolWindow), 0, true) as OptionsToolWindow;
            if (!(window?.Frame is IVsWindowFrame frame))
                return;
            if (window._dialog == null)
                window.Open();
            ErrorHandler.ThrowOnFailure(frame.Show());
        }

        // Visual Studio offers a tool window its keys before its own key bindings: Esc is Cancel
        // here (instead of moving to the editor), unless a drop-down is open.
        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
            if (m.Msg == WM_KEYDOWN && (int)m.WParam == VK_ESCAPE && _dialog != null && !_dialog.IsDropDownOpen)
            {
                CloseFrame();
                return true;
            }
            return base.PreProcessMessage(ref m);
        }

        // Closing by any route (Cancel, OK, the close box, Visual Studio shutting down) ends this
        // round: uncommitted preview colors go back, and the next Show starts over.
        protected override void OnClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dialog?.HostClosing();
            _dialog = null;
            Content = null;
            base.OnClose();
        }
    }
}
