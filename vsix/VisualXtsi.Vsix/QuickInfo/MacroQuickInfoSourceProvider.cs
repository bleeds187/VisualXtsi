using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace VisualXtsi.QuickInfo
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("VisualXtsi Macro QuickInfo")]
    [ContentType("C/C++")]
    [Order(Before = "default")]
    internal sealed class MacroQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            return textBuffer.Properties.GetOrCreateSingletonProperty(() => new MacroQuickInfoSource(textBuffer));
        }
    }
}
