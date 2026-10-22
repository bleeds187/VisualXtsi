using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Parsing
{
    /// <summary>Starts background parsing when a C/C++ document is opened in an editor.</summary>
    [Export(typeof(ITextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ParseOnOpenListener : ITextViewCreationListener
    {
        public void TextViewCreated(ITextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // view creation runs on the UI thread
            Log.EnsurePane();
            VxSettings.EnsureLoaded(); // configures the engine before the first parse
            DocumentParser parser = DocumentParser.Attach(textView.TextDataModel.DocumentBuffer);
            if (parser != null)
            {
                textView.Closed += (s, e) => parser.Detach();
                // Open File in Solution covers the Unreal project of every file opened.
                string path = parser.Path;
                _ = System.Threading.Tasks.Task.Run(() => Engine.IndexAddProjectOf(path));
            }
        }
    }
}
