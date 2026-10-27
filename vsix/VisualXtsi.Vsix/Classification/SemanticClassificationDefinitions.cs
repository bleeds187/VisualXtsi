using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using VisualXtsi.Interop;

namespace VisualXtsi.Classification
{
    internal static class SemanticNames
    {
        public const string Local = "VisualXtsi.Local";
        public const string Parameter = "VisualXtsi.Parameter";
        public const string Field = "VisualXtsi.Field";
        public const string Global = "VisualXtsi.Global";
        public const string Type = "VisualXtsi.Type";
        public const string EnumConstant = "VisualXtsi.EnumConstant";
        public const string Namespace = "VisualXtsi.Namespace";
        public const string Function = "VisualXtsi.Function";
        public const string Method = "VisualXtsi.Method";
        public const string StaticMethod = "VisualXtsi.StaticMethod";
        public const string VirtualMethod = "VisualXtsi.VirtualMethod";

        // Kinds C# has and C++ does not (C# coloring only).
        public const string Property = "VisualXtsi.Property";
        public const string Event = "VisualXtsi.Event";
        public const string Interface = "VisualXtsi.Interface";
        public const string Delegate = "VisualXtsi.Delegate";
        public const string TypeParameter = "VisualXtsi.TypeParameter";
        public const string Attribute = "VisualXtsi.Attribute";

        public static string For(VxKind kind)
        {
            switch (kind)
            {
                case VxKind.Local: return Local;
                case VxKind.Parameter: return Parameter;
                case VxKind.Field: return Field;
                case VxKind.Global: return Global;
                case VxKind.Type: return Type;
                case VxKind.EnumConstant: return EnumConstant;
                case VxKind.Namespace: return Namespace;
                case VxKind.Function: return Function;
                case VxKind.Method: return Method;
                case VxKind.StaticMethod: return StaticMethod;
                case VxKind.VirtualMethod: return VirtualMethod;
                default: return null;
            }
        }
    }

    internal static class SemanticClassificationDefinitions
    {
        [Export, Name(SemanticNames.Local)] internal static ClassificationTypeDefinition Local = null;
        [Export, Name(SemanticNames.Parameter)] internal static ClassificationTypeDefinition Parameter = null;
        [Export, Name(SemanticNames.Field)] internal static ClassificationTypeDefinition Field = null;
        [Export, Name(SemanticNames.Global)] internal static ClassificationTypeDefinition Global = null;
        [Export, Name(SemanticNames.Type)] internal static ClassificationTypeDefinition Type = null;
        [Export, Name(SemanticNames.EnumConstant)] internal static ClassificationTypeDefinition EnumConstant = null;
        [Export, Name(SemanticNames.Namespace)] internal static ClassificationTypeDefinition Namespace = null;
        [Export, Name(SemanticNames.Function)] internal static ClassificationTypeDefinition Function = null;
        [Export, Name(SemanticNames.Method)] internal static ClassificationTypeDefinition Method = null;
        [Export, Name(SemanticNames.StaticMethod)] internal static ClassificationTypeDefinition StaticMethod = null;
        [Export, Name(SemanticNames.VirtualMethod)] internal static ClassificationTypeDefinition VirtualMethod = null;
        [Export, Name(SemanticNames.Property)] internal static ClassificationTypeDefinition Property = null;
        [Export, Name(SemanticNames.Event)] internal static ClassificationTypeDefinition Event = null;
        [Export, Name(SemanticNames.Interface)] internal static ClassificationTypeDefinition Interface = null;
        [Export, Name(SemanticNames.Delegate)] internal static ClassificationTypeDefinition Delegate = null;
        [Export, Name(SemanticNames.TypeParameter)] internal static ClassificationTypeDefinition TypeParameter = null;
        [Export, Name(SemanticNames.Attribute)] internal static ClassificationTypeDefinition Attribute = null;
    }

    // Fonts and Colors entries, ordered after the lexical macro format so the parser's answer wins\r
    // over the ALL_CAPS guess. Defaults are tuned for the dark theme; every category can be
    // changed under Tools > Options > Environment > Fonts and Colors > Text Editor.
    internal abstract class SemanticFormat : ClassificationFormatDefinition
    {
        protected SemanticFormat(string displayName, byte r, byte g, byte b, bool italic = false)
        {
            DisplayName = "VisualXtsi " + displayName;
            ForegroundColor = Color.FromRgb(r, g, b);
            if (italic)
                IsItalic = true;
        }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Local)]
    [Name(SemanticNames.Local), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class LocalFormat : SemanticFormat
    {
        public LocalFormat() : base("Local Variable", 0x9C, 0xDC, 0xFE) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Parameter)]
    [Name(SemanticNames.Parameter), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class ParameterFormat : SemanticFormat
    {
        public ParameterFormat() : base("Parameter", 0x8F, 0xB8, 0xDE, italic: true) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Field)]
    [Name(SemanticNames.Field), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class FieldFormat : SemanticFormat
    {
        public FieldFormat() : base("Member Field", 0xC5, 0xA5, 0xF5) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Global)]
    [Name(SemanticNames.Global), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class GlobalFormat : SemanticFormat
    {
        public GlobalFormat() : base("Global or Static Variable", 0xD7, 0xBA, 0x7D) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Type)]
    [Name(SemanticNames.Type), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class TypeFormat : SemanticFormat
    {
        public TypeFormat() : base("Type", 0x4E, 0xC9, 0xB0) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.EnumConstant)]
    [Name(SemanticNames.EnumConstant), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class EnumConstantFormat : SemanticFormat
    {
        public EnumConstantFormat() : base("Enum Constant", 0xB8, 0xD7, 0xA3) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Namespace)]
    [Name(SemanticNames.Namespace), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class NamespaceFormat : SemanticFormat
    {
        public NamespaceFormat() : base("Namespace", 0xC8, 0xC8, 0xC8) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Function)]
    [Name(SemanticNames.Function), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class FunctionFormat : SemanticFormat
    {
        public FunctionFormat() : base("Function", 0xDC, 0xDC, 0xAA) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Method)]
    [Name(SemanticNames.Method), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class MethodFormat : SemanticFormat
    {
        public MethodFormat() : base("Method", 0xFF, 0xD6, 0x8F) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.StaticMethod)]
    [Name(SemanticNames.StaticMethod), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class StaticMethodFormat : SemanticFormat
    {
        public StaticMethodFormat() : base("Static Method", 0xFF, 0xD6, 0x8F, italic: true) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.VirtualMethod)]
    [Name(SemanticNames.VirtualMethod), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class VirtualMethodFormat : SemanticFormat
    {
        public VirtualMethodFormat() : base("Virtual Method", 0xF2, 0xB8, 0x6B) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Property)]
    [Name(SemanticNames.Property), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class PropertyFormat : SemanticFormat
    {
        public PropertyFormat() : base("Property", 0xB9, 0xA3, 0xE3) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Event)]
    [Name(SemanticNames.Event), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class EventFormat : SemanticFormat
    {
        public EventFormat() : base("Event", 0xE8, 0xA3, 0xD4) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Interface)]
    [Name(SemanticNames.Interface), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class InterfaceFormat : SemanticFormat
    {
        public InterfaceFormat() : base("Interface", 0x9F, 0xD8, 0xC2) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Delegate)]
    [Name(SemanticNames.Delegate), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class DelegateFormat : SemanticFormat
    {
        public DelegateFormat() : base("Delegate", 0x6C, 0xD3, 0xBC) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.TypeParameter)]
    [Name(SemanticNames.TypeParameter), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class TypeParameterFormat : SemanticFormat
    {
        public TypeParameterFormat() : base("Type Parameter", 0x7E, 0xC8, 0xC8, italic: true) { }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = SemanticNames.Attribute)]
    [Name(SemanticNames.Attribute), UserVisible(true), Order(After = ClassificationNames.Macro)]
    internal sealed class AttributeFormat : SemanticFormat
    {
        public AttributeFormat() : base("Attribute", 0x6F, 0xC3, 0xB2) { }
    }
}
