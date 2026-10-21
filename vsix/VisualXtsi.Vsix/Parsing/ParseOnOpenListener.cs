using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

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
            Log.EnsurePane();            DocumentParser parser = DocumentParser.Attach(textView.TextDataModel.DocumentBuffer);
            if (parser != null)
                textView.Closed += (s, e) => parser.Detach();
        }
    }
}
