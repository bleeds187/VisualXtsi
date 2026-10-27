using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using VisualXtsi.Options;

namespace VisualXtsi.Classification
{
    [Export(typeof(IClassifierProvider))]
    [ContentType("C/C++")]
    internal sealed class SemanticClassifierProvider : IClassifierProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry = null;

        [Import]
        internal IClassificationFormatMapService FormatMaps = null;

        public IClassifier GetClassifier(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // classifier providers are called on the UI thread
            VxSettings.EnsureLoaded(); // before the classifier reads which categories are on
            ThemeDefaults.EnsureApplied(FormatMaps, Registry);
            return buffer.Properties.GetOrCreateSingletonProperty(() => new SemanticClassifier(buffer, Registry));
        }
    }
}
