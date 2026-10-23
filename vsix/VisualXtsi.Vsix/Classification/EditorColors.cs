using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualXtsi.Interop;
using VisualXtsi.Options;

namespace VisualXtsi.Classification
{
    /// <summary>A coloring category's Fonts and Colors entry: colors and bold (italic lives in <see cref="VxSettings"/>).</summary>
    internal sealed class ColorItem
    {
        public string Name;
        public Color? Foreground; // null: the theme default
        public Color? Background; // null: none (the editor background)
        public bool Bold;

        public ColorItem Clone() => (ColorItem)MemberwiseClone();

        public bool SameAs(ColorItem other) =>
            other != null && Foreground == other.Foreground && Background == other.Background && Bold == other.Bold;
    }

    /// <summary>
    /// The editor's colors for VisualXtsi's categories and for plain text, keywords and the like.
    /// Reads and writes Fonts and Colors > Text Editor through IVsFontAndColorStorage, so the
    /// Options dialog and Fonts and Colors always agree; italics are applied to the editor format
    /// map. UI thread only.
    /// </summary>
    internal static class EditorColors
    {
        private static readonly Guid TextEditorCategory = new Guid("A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0");

        public static bool IsLightTheme()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey).GetBrightness() > 0.5f;
        }

        /// <summary>The current Fonts and Colors entry of a category; a theme default color reads as null.</summary>
        public static ColorItem Read(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = new ColorItem { Name = name };
            if (!(Package.GetGlobalService(typeof(SVsFontAndColorStorage)) is IVsFontAndColorStorage storage))
                return item;
            Guid category = TextEditorCategory;
            if (storage.OpenCategory(ref category, (uint)__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS) != VSConstants.S_OK)
                return item;
            try
            {
                var info = new ColorableItemInfo[1];
                if (storage.GetItem(name, info) != VSConstants.S_OK)
                    return item;
                var utilities = storage as IVsFontAndColorUtilities;
                if (info[0].bForegroundValid != 0 && Raw(utilities, info[0].crForeground) is Color fg && !ThemeDefaults.IsDefault(name, fg))
                    item.Foreground = fg;
                if (info[0].bBackgroundValid != 0)
                    item.Background = Raw(utilities, info[0].crBackground);
                item.Bold = info[0].bFontFlagsValid != 0 && (info[0].dwFontFlags & (uint)FONTFLAGS.FF_BOLD) != 0;
            }
            finally
            {
                storage.CloseCategory();
            }
            return item;
        }

        /// <summary>Writes a category to Fonts and Colors; the editor picks it up right away.</summary>
        public static void Write(ColorItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsFontAndColorStorage)) is IVsFontAndColorStorage storage))
                return;
            Guid category = TextEditorCategory;
            uint flags = (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES);
            if (storage.OpenCategory(ref category, flags) != VSConstants.S_OK)
                return;
            try
            {
                var info = new ColorableItemInfo[1];
                storage.GetItem(item.Name, info);
                uint automatic = 0;
                (storage as IVsFontAndColorUtilities)?.EncodeAutomaticColor(out automatic);
                // A default foreground is written as the current theme's default, which the theme
                // switch then keeps up to date (ThemeDefaults).
                info[0].bForegroundValid = 1;
                info[0].crForeground = ColorRef(item.Foreground ?? ThemeDefaults.For(item.Name, IsLightTheme()));
                info[0].bBackgroundValid = 1;
                info[0].crBackground = item.Background is Color bg ? ColorRef(bg) : automatic;
                info[0].bFontFlagsValid = 1;
                info[0].dwFontFlags = item.Bold ? (uint)FONTFLAGS.FF_BOLD : 0;
                int hr = storage.SetItem(item.Name, info);
                if (hr != VSConstants.S_OK)
                    Log.Write($"Writing the colors of {item.Name} failed: 0x{hr:X8}");
            }
            finally
            {
                storage.CloseCategory();
            }
        }

        /// <summary>Applies italics to the editor's format map (Fonts and Colors has no italic setting).</summary>
        public static void ApplyItalics(ICollection<string> italic)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IEditorFormatMap map = EditorFormatMap();
            if (map == null)
                return;
            foreach (var (name, _) in VxSettings.ColorCategories)
            {
                ResourceDictionary properties = map.GetProperties(name);
                bool wanted = italic.Contains(name);
                if (properties.Contains(ClassificationFormatDefinition.IsItalicId) && (bool)properties[ClassificationFormatDefinition.IsItalicId] == wanted)
                    continue;
                properties[ClassificationFormatDefinition.IsItalicId] = wanted;
                map.SetProperties(name, properties);
            }
        }

        /// <summary>The editor's text properties for a classification ("keyword", "VisualXtsi.Macro"); null if unknown.</summary>
        public static TextFormattingRunProperties Properties(string classification)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            IClassificationType type = model?.GetService<IClassificationTypeRegistryService>().GetClassificationType(classification);
            return type == null ? null : model.GetService<IClassificationFormatMapService>().GetClassificationFormatMap("text").GetTextProperties(type);
        }

        /// <summary>Plain text: the editor font and its default colors.</summary>
        public static TextFormattingRunProperties PlainText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return model?.GetService<IClassificationFormatMapService>().GetClassificationFormatMap("text").DefaultTextProperties;
        }

        /// <summary>The editor background.</summary>
        public static Brush Background()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ResourceDictionary view = EditorFormatMap()?.GetProperties("TextView Background");
            return view?[EditorFormatDefinition.BackgroundBrushId] as Brush
                ?? (view?[EditorFormatDefinition.BackgroundColorId] is Color c ? new SolidColorBrush(c) : null)
                ?? SystemColors.WindowBrush;
        }

        /// <summary>Text with macros and preprocessor directives colored as in the editor (when enabled).</summary>
        public static void AddClassifiedRuns(InlineCollection inlines, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VxSpan[] spans;
            try
            {
                spans = Engine.Classify(text);
            }
            catch (Exception)
            {
                spans = Array.Empty<VxSpan>();
            }
            int at = 0;
            foreach (VxSpan span in spans)
            {
                string name = span.Kind == VxKind.Preprocessor ? ClassificationNames.Preprocessor
                    : span.Kind == VxKind.Macro ? ClassificationNames.Macro : null;
                if (name == null || !VxSettings.Current.IsCategoryActive(name) || span.Start < at || span.Start + span.Length > text.Length)
                    continue;
                if (span.Start > at)
                    inlines.Add(new Run(text.Substring(at, (int)span.Start - at)));
                var run = new Run(text.Substring((int)span.Start, (int)span.Length));
                TextFormattingRunProperties properties = Properties(name);
                if (properties?.ForegroundBrushEmpty == false)
                    run.Foreground = properties.ForegroundBrush;
                if (properties?.Bold == true)
                    run.FontWeight = FontWeights.Bold;
                inlines.Add(run);
                at = (int)(span.Start + span.Length);
            }
            if (at < text.Length)
                inlines.Add(new Run(text.Substring(at)));
        }

        private static IEditorFormatMap EditorFormatMap()
        {
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return model?.GetService<IEditorFormatMapService>().GetEditorFormatMap("text");
        }

        private static Color? Raw(IVsFontAndColorUtilities utilities, uint colorRef)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (utilities != null && utilities.GetColorType(colorRef, out int type) == VSConstants.S_OK && type != (int)__VSCOLORTYPE.CT_RAW)
                return null;
            return Color.FromRgb((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
        }

        private static uint ColorRef(Color c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16);
    }
}
