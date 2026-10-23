using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualXtsi.Interop;
using VisualXtsi.Options;
using VisualXtsi.Parsing;

namespace VisualXtsi.QuickInfo
{
    /// <summary>
    /// Adds what VS's own C++ QuickInfo lacks for macros: the fully expanded invocation laid
    /// out one statement per line, a link to the definition, and notes for Unreal markers.
    /// (VS already shows the #define line and a one-line raw expansion, so we don't.)
    /// Parsing happens in <see cref="DocumentParser"/>; a hover only queries.
    /// </summary>
    internal sealed class MacroQuickInfoSource : IAsyncQuickInfoSource
    {
        private const int MaxExpansionLines = 80;
        private const string ExpandsToNothing = "/* expands to nothing */";
        private static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
        private static readonly Regex LocationPattern = new Regex(@"^(.*)\((\d+)\)$");

        // Unreal reflection markers: empty for the C++ compiler, read by UnrealHeaderTool.
        private static readonly HashSet<string> UhtMarkers = new HashSet<string>
        {
            "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UPROPERTY", "UFUNCTION", "UPARAM", "UMETA", "UDELEGATE",
        };

        private readonly ITextBuffer _buffer;

        public MacroQuickInfoSource(ITextBuffer buffer)
        {
            _buffer = buffer;
        }

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            SnapshotPoint? trigger = session.GetTriggerPoint(_buffer.CurrentSnapshot);
            if (trigger == null || !VxSettings.Current.Enabled)
                return null;
            SnapshotPoint point = trigger.Value;
            SnapshotSpan? word = IdentifierAt(point);
            if (word == null)
                return null;

            // An Unreal reflection specifier (UPROPERTY(EditAnywhere, ...)): what it does. Lexical,
            // so it works before (and without) a parse.
            if (VxSettings.Current.SpecifierTooltip)
            {
                SnapshotSpan at = word.Value;
                string help = await Task.Run(() => IsSpecifier(at) ? UeSpecifiers.Describe(at.GetText()) : null, cancellationToken)
                    .ConfigureAwait(false);
                if (help != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    return new QuickInfoItem(point.Snapshot.CreateTrackingSpan(at, SpanTrackingMode.EdgeInclusive), SpecifierContent(at.GetText(), help));
                }
            }
            // A BlueprintNativeEvent or RPC: where UnrealHeaderTool expects its body. From the symbol
            // index, so it works without a parse too.
            if (VxSettings.Current.SpecifierTooltip)
            {
                string name = word.Value.GetText();
                List<SymbolInfo> implementations = await Task.Run(() => UeImplementations(name), cancellationToken).ConfigureAwait(false);
                if (implementations.Count > 0)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    return new QuickInfoItem(point.Snapshot.CreateTrackingSpan(word.Value, SpanTrackingMode.EdgeInclusive), ImplementationContent(implementations));
                }
            }
            if (!VxSettings.Current.MacroTooltip)
                return null;

            // Never parse here: use whatever the background parser last finished, mapping the
            // hover position back to that snapshot. Until the first parse lands, show nothing.
            DocumentParser parser = DocumentParser.TryGet(_buffer);
            ITextSnapshot parsed = parser?.ParsedSnapshot;
            if (parsed == null)
                return null;
            SnapshotPoint parsedPoint = point.TranslateTo(parsed, PointTrackingMode.Negative);
            ITextSnapshotLine line = parsedPoint.GetContainingLine();
            int lineNumber = line.LineNumber + 1;
            int column = parsedPoint.Position - line.Start.Position + 1;
            string path = parser.Path;

            MacroInfo info = await Task.Run(() => Engine.GetMacroInfo(path, lineNumber, column), cancellationToken)
                .ConfigureAwait(false);
            // Hovering the #define itself: VS's QuickInfo already covers it.
            if (info == null || string.IsNullOrEmpty(info.Expansion))
                return null;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            ITrackingSpan applicable = point.Snapshot.CreateTrackingSpan(word.Value, SpanTrackingMode.EdgeInclusive);
            return new QuickInfoItem(applicable, BuildContent(info));
        }

        private static SnapshotSpan? IdentifierAt(SnapshotPoint point)
        {
            ITextSnapshot snapshot = point.Snapshot;
            int start = point.Position, end = point.Position;
            while (start > 0 && IsIdentChar(snapshot[start - 1]))
                start--;
            while (end < snapshot.Length && IsIdentChar(snapshot[end]))
                end++;
            if (start == end || char.IsDigit(snapshot[start]))
                return null;
            return new SnapshotSpan(snapshot, start, end - start);
        }

        private static bool IsIdentChar(char c) => c == '_' || char.IsLetterOrDigit(c);

        private static bool IsSpecifier(SnapshotSpan word)
        {
            foreach (VxSpan span in Engine.Classify(word.Snapshot.GetText()))
                if (span.Start == word.Start.Position)
                    return span.Kind == VxKind.UeSpecifier;
            return false;
        }

        // Definitions of Name_Implementation / Name_Validate when Name is a split UFUNCTION declaration.
        private static List<SymbolInfo> UeImplementations(string name)
        {
            var found = new List<SymbolInfo>();
            int ue = 0;
            foreach (SymbolInfo s in Engine.LookupSymbols(name, 20))
                if (!s.Definition)
                    ue |= s.Ue;
            if ((ue & SymbolInfo.UeImplementation) != 0)
                found.AddRange(Engine.LookupSymbols(name + "_Implementation", 20).Where(s => s.Definition));
            if ((ue & SymbolInfo.UeValidate) != 0)
                found.AddRange(Engine.LookupSymbols(name + "_Validate", 20).Where(s => s.Definition));
            return found;
        }

        // "Implemented by AClass::Name_Implementation (File.cpp:123)", the location a link.
        private static UIElement ImplementationContent(List<SymbolInfo> implementations)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var panel = new StackPanel { MaxWidth = 1000 };
            foreach (SymbolInfo s in implementations)
            {
                bool validate = s.Name.EndsWith("_Validate", StringComparison.Ordinal);
                var block = Text(validate ? "Validated by " : "Implemented by ");
                block.Inlines.Add(new Run((string.IsNullOrEmpty(s.Scope) ? "" : s.Scope + "::") + s.Name + " ("));
                var link = new Hyperlink(new Run($"{Path.GetFileName(s.Path)}:{s.Line}")) { ToolTip = s.Path };
                link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
                string file = s.Path;
                int line = s.Line;
                link.Click += (o, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    OpenAt(file, line);
                };
                block.Inlines.Add(link);
                block.Inlines.Add(new Run(")"));
                panel.Children.Add(block);
            }
            return panel;
        }

        private static UIElement SpecifierContent(string name, string help)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var panel = new StackPanel { MaxWidth = 600 };
            panel.Children.Add(Text($"Unreal specifier {name}", bold: true));
            panel.Children.Add(Text(help));
            return panel;
        }

        private static UIElement BuildContent(MacroInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var panel = new StackPanel { MaxWidth = 1000 };
            panel.Children.Add(Header(info));

            if (info.Expansion == ExpandsToNothing)
            {
                panel.Children.Add(Note(UhtMarkers.Contains(info.Name)
                    ? "Expands to nothing. Reflection marker read by UnrealHeaderTool."
                    : "Expands to nothing."));
                return panel;
            }

            panel.Children.Add(Text("Expanded:", bold: true, topMargin: 4));
            panel.Children.Add(Code(Limit(info.Expansion)));
            // Without UnrealHeaderTool's output the chain ends in an undefined CURRENT_FILE_ID or
            // <file id>_<line>_GENERATED_BODY(_LEGACY)/_PROLOG name (UE4 and UE5 alike).
            if (VxSettings.Current.GeneratedCodeHint && (info.Expansion.Contains("CURRENT_FILE_ID") ||
                (info.Name.StartsWith("GENERATED_") && info.Expansion.Contains("_GENERATED_BODY"))))
                panel.Children.Add(Note("No generated code for this file yet: its .generated.h is missing or stale. Build the project to run UnrealHeaderTool."));
            return panel;
        }

        // "Macro NAME · File.h(123)", with the location linking to the definition.
        private static TextBlock Header(MacroInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var block = Text("", bold: true);
            block.Inlines.Add(new Run($"Macro {info.Name}  ·  "));
            Match m = LocationPattern.Match(info.Location);
            if (m.Success && File.Exists(m.Groups[1].Value))
            {
                string file = m.Groups[1].Value;
                int line = int.Parse(m.Groups[2].Value);
                var link = new Hyperlink(new Run($"{Path.GetFileName(file)}({line})")) { ToolTip = file };
                link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
                link.Click += (s, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    OpenAt(file, line);
                };
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Run(info.Location));
            }
            return block;
        }

        private static void OpenAt(string file, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty,
                out IVsUIHierarchy _, out uint _, out IVsWindowFrame _, out IVsTextView view);
            if (view == null)
                return;
            view.SetCaretPos(line - 1, 0);
            view.CenterLines(line - 1, 1);
        }

        private static string Limit(string text)
        {
            string[] lines = text.Split('\n');
            if (lines.Length <= MaxExpansionLines)
                return text;
            return string.Join("\n", lines, 0, MaxExpansionLines) + $"\n... ({lines.Length - MaxExpansionLines} more lines)";
        }

        private static TextBlock Code(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var block = Text(text);
            if (VxSettings.Current.ColorQuickInfo)
            {
                // Macros and directives in the editor's colors (Options > Coloring > Apply coloring to).
                block.Text = "";
                Classification.EditorColors.AddClassifiedRuns(block.Inlines, text);
            }
            block.FontFamily = CodeFont;
            block.TextWrapping = TextWrapping.NoWrap;
            return block;
        }

        private static TextBlock Note(string text)
        {
            var block = Text(text, topMargin: 4);
            block.FontStyle = FontStyles.Italic;
            return block;
        }

        private static TextBlock Text(string text, bool bold = false, double topMargin = 0)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, topMargin, 0, 2),
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);
            return block;
        }

        public void Dispose()
        {
        }
    }
}
