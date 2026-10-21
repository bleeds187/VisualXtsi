using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace VisualXtsi.Classification
{
    internal static class ClassificationNames
    {
        public const string Preprocessor = "VisualXtsi.Preprocessor";
        public const string Macro = "VisualXtsi.Macro";
    }

    internal static class ClassificationDefinitions
    {
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(ClassificationNames.Preprocessor)]
        internal static ClassificationTypeDefinition Preprocessor = null;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(ClassificationNames.Macro)]
        internal static ClassificationTypeDefinition Macro = null;
    }

    // Formats appear under Tools > Options > Fonts and Colors > Text Editor.
    // Order after High so they win over the built-in C++ colorizer.

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = ClassificationNames.Preprocessor)]
    [Name(ClassificationNames.Preprocessor)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class PreprocessorFormat : ClassificationFormatDefinition
    {
        public PreprocessorFormat()
        {
            DisplayName = "VisualXtsi Preprocessor Directive";
            ForegroundColor = Color.FromRgb(0xC5, 0x86, 0xC0);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = ClassificationNames.Macro)]
    [Name(ClassificationNames.Macro)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class MacroFormat : ClassificationFormatDefinition
    {
        public MacroFormat()
        {
            DisplayName = "VisualXtsi Macro";
            ForegroundColor = Color.FromRgb(0xE0, 0x6C, 0x2B);
        }
    }
}
