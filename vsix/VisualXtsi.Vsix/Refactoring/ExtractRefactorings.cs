using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Introduce Variable (the selected expression becomes a local constant declared before its
    /// statement, optionally replacing its identical occurrences in the same block) and Extract
    /// Method.
    /// </summary>
    internal static class ExtractRefactorings
    {
        public static bool CanIntroduceVariable(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return c?.View != null && !c.View.Selection.IsEmpty;
        }

        // The selection, in the parsed text, as 1-based lines and columns; null when there is none.
        private static (int Line, int Column, int EndLine, int EndColumn)? Selection(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c?.Snapshot == null || c.View.Selection.IsEmpty)
                return null;
            SnapshotSpan span = c.View.Selection.StreamSelectionSpan.SnapshotSpan;
            SnapshotPoint start = span.Start.TranslateTo(c.Snapshot, PointTrackingMode.Positive);
            SnapshotPoint end = span.End.TranslateTo(c.Snapshot, PointTrackingMode.Negative);
            ITextSnapshotLine a = start.GetContainingLine(), b = end.GetContainingLine();
            return (a.LineNumber + 1, start - a.Start + 1, b.LineNumber + 1, end - b.Start + 1);
        }

        private static int Offset(RefactorContext c, string[] f, int at) => c.OffsetOf(int.Parse(f[at]), int.Parse(f[at + 1]));

        // ---- Extract Method

        public static bool CanExtractMethod(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return CanIntroduceVariable(c);
        }

        /// <summary>
        /// Extract Method: the selected statements (or expression) become a function of their own,
        /// with the variables they use as parameters (references where they change one that is
        /// needed after), a variable they declare and that is used after as the result (several: out
        /// parameters). In a method: a member of its class (const like it; static when asked and it
        /// uses no members), declared in the class and defined after the method; else a static
        /// function before it.
        /// </summary>
        public static async Task ExtractMethodAsync()
        {
            const string title = "Extract Method";
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var range = Selection(c);
            if (range == null)
            {
                EditorCommands.Status("select the statements or the expression to extract");
                return;
            }
            var (l1, c1, l2, c2) = range.Value;
            var s = await Task.Run(() => Engine.SelectionInfo(c.Path, l1, c1, l2, c2, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null || !s.TryGetValue("function", out var functionLines))
            {
                EditorCommands.Status("select statements or an expression in a function body");
                return;
            }
            string[] fn = functionLines[0]; // name, classes, namespaces, const, static, method, template
            if (s.ContainsKey("return") || s.ContainsKey("jump") || fn[6] == "1")
            {
                EditorCommands.Status(s.ContainsKey("return") ? "the selection returns from the function; select statements without return"
                                      : s.ContainsKey("jump") ? "the selection breaks, continues or jumps out of itself"
                                      : "extracting from a template is not supported");
                return;
            }
            string text = c.Snapshot.GetText();
            string nl = TextScan.Newline(text);
            string unit = SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText());

            // What is extracted: an expression with a value, else whole statements (a void call is one).
            var request = new ExtractRequest { Text = text, Unit = unit, Newline = nl };
            if (s.TryGetValue("expression", out var expression) && !s.ContainsKey("void"))
            {
                request.Expression = true;
                request.Start = Offset(c, expression[0], 0);
                request.End = Offset(c, expression[0], 2);
                request.ExpressionType = expression[0][4];
            }
            else
            {
                string[] r = s.TryGetValue("statements", out var statements) ? statements[0] : s.TryGetValue("statement", out var statement) ? statement[0] : null;
                if (r == null)
                {
                    EditorCommands.Status("select whole statements or an expression");
                    return;
                }
                request.Start = Offset(c, r, 0);
                request.End = Offset(c, r, 2);
                if (request.End < text.Length && text[request.End] == ';')
                    request.End++;
            }
            foreach (string[] f in s.TryGetValue("in", out var ins) ? ins : new List<string[]>())
                request.In.Add(new ExtractVariable { Name = f[0], Type = f[1], Shape = f[2], Written = f[3] == "1", UsedOutside = f[4] == "1" });
            foreach (string[] f in s.TryGetValue("out", out var outs) ? outs : new List<string[]>())
                request.Out.Add(new ExtractVariable { Name = f[0], Type = f[1], Shape = f[2], DeclStart = Offset(c, f, 3), DeclEnd = Offset(c, f, 5) });
            if (request.In.Any(v => string.IsNullOrEmpty(v.Type)) || request.Out.Any(v => string.IsNullOrEmpty(v.Type)))
            {
                EditorCommands.Status("the selection uses a variable whose type cannot be written (a lambda)");
                return;
            }
            if (request.Expression && request.Out.Count > 0)
                request.Out.Clear();

            // Where it goes.
            bool method = fn[5] == "1", usesThis = s.ContainsKey("this");
            string classes = fn[1];
            SourceRange cls = s.TryGetValue("class", out var clsLines) ? SourceRange.Parse(string.Join("\t", clsLines[0])) : null;
            SourceRange declaration = s.TryGetValue("functiondecl", out var declLines) ? SourceRange.Parse(string.Join("\t", declLines[0])) : null;
            string[] fe = s["functionextent"][0];
            int functionStart = Offset(c, fe, 0), functionEnd = Offset(c, fe, 2);
            bool inClassBody = method && declaration == null && cls != null && cls.InFile(c.Path);
            if (method && cls == null)
            {
                EditorCommands.Status($"the class of {fn[0]} was not found");
                return;
            }

            var options = new List<(string, bool, bool)>
            {
                (method ? "_Static (it uses no members of the class)" : "_Static (visible in this file only)",
                 !method && !Documents.IsHeader(c.Path) || method && fn[4] == "1",
                 method ? !usesThis && fn[4] != "1" : !Documents.IsHeader(c.Path)),
            };
            ExtractResult Plan(string name, bool isStatic)
            {
                request.Name = string.IsNullOrEmpty(name) ? "ExtractedFunction" : name;
                request.Static = isStatic;
                request.Const = method && !isStatic && fn[3] == "1";
                request.Qualifier = method && !inClassBody ? classes + "::" : "";
                return ExtractSupport.ExtractMethod(request);
            }
            ExtractResult first = Plan("ExtractedFunction", false);
            if (first.Error != null)
            {
                EditorCommands.Status(first.Error);
                return;
            }
            var dialog = new NameOptionsDialog(title, "_Name of the new function:", "ExtractedFunction", options,
                n => RenameSupport.Validate(n, null),
                (n, o) => Plan(n, o[0]).Signature + nl + nl + Plan(n, o[0]).Call.Replace(nl, " "));
            if (dialog.ShowModal() != true)
                return;
            bool makeStatic = dialog.Option(0) || (method && fn[4] == "1");
            ExtractResult plan = Plan(dialog.Value, makeStatic);

            // The call in place of the selection, its continuation lines at the selection's indentation.
            string indent = TextScan.IndentOf(text, request.Start);
            var edits = new List<(FileEdit Edit, string Text)>
            {
                (FileEdit.Replace(c.Path, text, request.Start, request.End - request.Start, plan.Call.Replace(nl, nl + indent)), text),
            };
            string definition = plan.Definition;
            if (!method)
            {
                // A free function: before the enclosing one (and the comments above it).
                int at = TextScan.LineStart(text, functionStart);
                while (at > 0)
                {
                    int prev = TextScan.LineStart(text, at - 1);
                    string line = text.Substring(prev, at - prev).Trim();
                    if (!(line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*") || line.StartsWith("template")))
                        break;
                    at = prev;
                }
                string prefix = makeStatic ? "static " : Documents.IsHeader(c.Path) ? "inline " : "";
                edits.Add((FileEdit.Insert(c.Path, at, ExtractSupport.Indent(prefix + definition, TextScan.IndentOf(text, functionStart), nl) + nl + nl), text));
            }
            else if (inClassBody)
            {
                // A method defined in its class: the new one right after it, in the class too.
                string member = TextScan.IndentOf(text, functionStart);
                edits.Add((FileEdit.Insert(c.Path, functionEnd, nl + nl + ExtractSupport.Indent((makeStatic ? "static " : "") + definition, member, nl)), text));
            }
            else
            {
                // A method defined outside its class: declared in the class (private), defined after it.
                edits.Add((FileEdit.Insert(c.Path, functionEnd, nl + nl + definition), text));
                string header = cls.Path;
                string headerText = cls.InFile(c.Path) ? text : Documents.Read(header);
                if (headerText == null)
                    return;
                string hnl = TextScan.Newline(headerText);
                int classStart = TextScan.Offset(headerText, cls.Line, cls.Column), classEnd = TextScan.Offset(headerText, cls.EndLine, cls.EndColumn);
                int open = headerText.IndexOf('{', classStart), close = headerText.LastIndexOf('}', System.Math.Max(0, classEnd - 1));
                bool isStruct = System.Text.RegularExpressions.Regex.IsMatch(headerText.Substring(classStart, open - classStart), @"\bstruct\b");
                string classIndent = TextScan.IndentOf(headerText, classStart);
                var (offset, label, newLine) = MemberSupport.Insertion(headerText, open, close, "private", isStruct);
                string memberIndent = label != null || newLine ? classIndent + unit : TextScan.MemberIndent(headerText, offset, classIndent, unit);
                string export = MemberSupport.MemberExportMacro(headerText, classStart, open, close);
                string code = (label != null ? classIndent + label + ":" + hnl : "") + memberIndent + (export != null ? export + " " : "") + plan.Signature + ";" + hnl;
                if (newLine)
                    code = hnl + code + classIndent;
                edits.Add((FileEdit.Insert(header, offset, code), headerText));
            }

            if (edits.Select(e => e.Edit.Path.ToLowerInvariant()).Distinct().Count() > 1)
            {
                var items = edits.Select((e, i) => new PreviewItem
                {
                    Group = e.Edit.Path,
                    Label = $"line {TextScan.LineOf(e.Text, e.Edit.Start)}: " + (i == 0 ? "the call" : e.Edit.Path == c.Path ? "the definition" : "the declaration"),
                    Diff = RefactorPreviewDialog.Diff(e.Text, e.Edit.Start, e.Edit.Length, e.Edit.NewText, 1),
                    Tag = e.Edit,
                }).ToList();
                var preview = new RefactorPreviewDialog(title, $"Extract {plan.Signature}.", items, "_Extract");
                if (preview.ShowModal() != true || preview.Accepted.Count != items.Count)
                {
                    if (preview.DialogResult == true)
                        EditorCommands.Status("the call, the definition and the declaration go together; nothing was changed");
                    return;
                }
            }
            if (Documents.Apply(edits.Select(e => e.Edit).ToList(), title, out string error))
                EditorCommands.Status($"extracted {plan.Signature}");
            else
                EditorCommands.Status(error);
        }

        public static async Task IntroduceVariableAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var range = Selection(c);
            if (range == null)
            {
                EditorCommands.Status("select an expression");
                return;
            }
            var (l1, c1, l2, c2) = range.Value;
            var s = await Task.Run(() => Engine.SelectionInfo(c.Path, l1, c1, l2, c2, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null || !s.TryGetValue("expression", out var expression) || !s.ContainsKey("statement"))
            {
                EditorCommands.Status("select an expression in a function body");
                return;
            }
            if (s.ContainsKey("void"))
            {
                EditorCommands.Status("the expression has no value");
                return;
            }
            if (s.ContainsKey("inloop"))
            {
                EditorCommands.Status("the expression is in a loop's condition or increment: a variable would compute it only once");
                return;
            }
            string text = c.Snapshot.GetText();
            string nl = TextScan.Newline(text);
            string[] e = expression[0];
            int start = Offset(c, e, 0), end = Offset(c, e, 2);
            string type = e.Length > 4 ? e[4] : "";
            var selected = (start, end, Offset(c, s["statement"][0], 0));
            var others = (s.TryGetValue("same", out var same) ? same : new List<string[]>())
                .Select(f => (Offset(c, f, 0), Offset(c, f, 2), Offset(c, f, 4))).ToList();
            string exprText = text.Substring(start, end - start);
            string suggested = ExtractSupport.SuggestName(exprText, type);
            var options = new List<(string, bool, bool)>
            {
                ($"_Replace the {others.Count} other identical occurrence{(others.Count == 1 ? "" : "s")} in the block", others.Count > 0, others.Count > 0),
                ($"_Explicit type ({type})", false, type.Length > 0),
            };
            var dialog = new NameOptionsDialog("Introduce Variable", "_Name:", suggested, options,
                n => RenameSupport.Validate(n, null),
                (n, o) => ExtractSupport.VariableType(type, o[1]) + " " + n + " = " + System.Text.RegularExpressions.Regex.Replace(exprText.Trim(), @"\s+", " ") + ";");
            if (dialog.ShowModal() != true)
                return;
            var occurrences = new List<(int, int, int)> { selected };
            if (dialog.Option(0))
                occurrences.AddRange(others);
            var edits = ExtractSupport.IntroduceVariable(text, occurrences, dialog.Value, ExtractSupport.VariableType(type, dialog.Option(1)), nl);
            if (Documents.Apply(edits.Select(x => FileEdit.Replace(c.Path, text, x.Start, x.Length, x.Text)).ToList(), "Introduce Variable", out string error))
                EditorCommands.Status($"introduced {dialog.Value}" + (occurrences.Count > 1 ? $" for {occurrences.Count} occurrences" : ""));
            else
                EditorCommands.Status(error);
        }
    }
}
