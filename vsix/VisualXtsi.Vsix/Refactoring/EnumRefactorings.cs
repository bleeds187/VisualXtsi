using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.Options;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Add Missing Case Statements (the switch on an enum at the caret gets a case per enumerator it
    /// lacks) and Convert Unscoped Enum to Scoped Enum (enum E becomes enum class E, its values are
    /// qualified where they are used and implicit conversions to numbers get a static_cast).
    /// </summary>
    internal static class EnumRefactorings
    {
        // Enabled in a function with a switch before the caret (or on the caret's line); the parse decides when run.
        public static bool CanAddMissingCases(RefactorContext c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c?.Snapshot == null)
                return c != null;
            int caret = c.OffsetOf(c.Line, c.Column);
            int lineEnd = c.Snapshot.GetLineFromPosition(caret).End.Position;
            int from = Math.Max(0, caret - 8000);
            return Regex.IsMatch(c.Snapshot.GetText(from, lineEnd - from), @"\bswitch\s*\(");
        }

        public static async Task AddMissingCasesAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            var s = await Task.Run(() => Engine.SwitchAt(c.Path, c.Line, c.Column, wait: true));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (s == null)
            {
                EditorCommands.Status("put the caret in a switch statement");
                return;
            }
            string text = c.Snapshot.GetText();
            SwitchShape shape = Shape(c, s);
            if (shape?.EnumName == null)
            {
                EditorCommands.Status("the switch is not on an enum");
                return;
            }
            List<string> missing = EnumSupport.Missing(shape, VxSettings.Current.SkipSentinelCases);
            if (missing.Count == 0)
            {
                EditorCommands.Status($"every {shape.EnumName} value has a case");
                return;
            }
            var (offset, insert) = EnumSupport.Insertion(text, shape, missing, SurroundWith.IndentUnit(c.View, c.View.TextBuffer.CurrentSnapshot.GetText()));
            if (Documents.Apply(new[] { FileEdit.Insert(c.Path, offset, insert) }, "Add Missing Case Statements", out string error))
                EditorCommands.Status($"added {missing.Count} case{(missing.Count == 1 ? "" : "s")}: {string.Join(", ", missing)}");
            else
                EditorCommands.Status(error);
        }

        // ---- Convert Unscoped Enum to Scoped Enum

        public static bool CanConvertToScoped(RefactorContext c) =>
            c != null && (c.Info == null || (c.Info.Kind == "enum" && !c.Info.EnumScoped) || c.Info.Kind == "enumerator");

        private sealed class Change
        {
            public Engine.Reference Ref;
            public string What;
        }

        public static async Task ConvertToScopedAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            if (c.Info?.Kind == "enum" && c.Info.EnumScoped)
            {
                EditorCommands.Status($"{c.Info.Name} is already a scoped enum");
                return;
            }
            uint job = await Task.Run(() => Engine.EnumUsesStart(c.Path, c.Line, c.Column));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (job == 0)
            {
                EditorCommands.Status("put the caret on an enum or one of its values");
                return;
            }
            var target = Engine.ReferencesTarget(job);
            string name = target.Name;
            List<Engine.Reference> found = await Rename.WaitAsync(job, $"Finding the uses of {name} and its values...", "Convert to Scoped Enum");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found == null)
                return; // cancelled

            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string TextOf(string path)
            {
                if (!texts.TryGetValue(path, out string t))
                    texts[path] = t = Documents.Read(path);
                return t;
            }
            string IdentifierAt(string t, int at) => Regex.Match(t.Substring(at), @"^\w+").Value;

            // The definition: "enum Name" with its body.
            Engine.Reference definition = found.FirstOrDefault(r => r.Kind == "definition" && TextOf(r.Path) is string t &&
                                                                    IdentifierAt(t, TextScan.Offset(t, r.Line, r.Column)) == name);
            if (definition == null)
            {
                EditorCommands.Status($"the definition of {name} was not found");
                return;
            }
            string declText = TextOf(definition.Path);
            int nameAt = TextScan.Offset(declText, definition.Line, definition.Column);
            if (EnumSupport.ScopedDeclaration(declText, nameAt, name, "") == null)
            {
                EditorCommands.Status($"{name} is not declared as \"enum {name}\" (already scoped, or a typedef)");
                return;
            }
            if (name == "Type" && Regex.IsMatch(target.Label, @"::Type \("))
            {
                EditorCommands.Status($"{target.Label.Split(' ')[0]} is a namespaced enum; converting that pattern is not supported");
                return;
            }
            int bodyOpen = declText.IndexOf('{', nameAt);
            int bodyEnd = bodyOpen < 0 ? -1 : TextScan.BlockEnd(declText, bodyOpen);
            string macro = RenameSupport.ReflectionMacro(declText, nameAt);
            bool uenum = macro == "UENUM";
            bool blueprint = uenum && Regex.IsMatch(declText.Substring(TextScan.LineStart(declText, Math.Max(0, nameAt - 200)), nameAt - TextScan.LineStart(declText, Math.Max(0, nameAt - 200))), @"UENUM\s*\([^)]*BlueprintType");
            string oldBase = EnumSupport.DeclaredBase(declText, nameAt, name);

            var dialog = new NameDialog("Convert to Scoped Enum", $"Underlying type of enum class {name} (empty: none, int):",
                                        oldBase ?? (uenum ? "uint8" : ""),
                                        v => v.Length == 0 || Regex.IsMatch(v, @"^[A-Za-z_][\w:]*(\s+[A-Za-z_]\w*)*$") ? null : "Type an integer type, like uint8 or int32.");
            if (dialog.ShowModal() != true)
                return;
            string newBase = dialog.Value;

            // Every change, by file.
            var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            void Add(Engine.Reference r, TextEdit e, string what)
            {
                if (e == null)
                    return;
                e.Tag = new Change { Ref = r, What = what };
                if (!changes.TryGetValue(r.Path, out var list))
                    changes[r.Path] = list = new List<TextEdit>();
                list.Add(e);
            }
            var values = new List<(int At, string Name)>();
            foreach (Engine.Reference r in found.GroupBy(r => (r.Path.ToLowerInvariant(), r.Line, r.Column, r.Kind)).Select(g => g.First()))
            {
                if (Documents.IsGenerated(r.Path))
                    continue; // rewritten by the next build
                string t = TextOf(r.Path);
                if (t == null)
                    continue;
                int at = TextScan.Offset(t, r.Line, r.Column);
                bool inBody = string.Equals(r.Path, definition.Path, StringComparison.OrdinalIgnoreCase) && at > bodyOpen && at < bodyEnd;
                if (r.Kind == "convert")
                {
                    int end = TextScan.Offset(t, r.EndLine, r.EndColumn);
                    if (end > at)
                        Add(r, EnumSupport.Cast(t, at, end, r.ConvertTo), $"static_cast<{r.ConvertTo}>");
                    continue;
                }
                string word = IdentifierAt(t, at);
                if (word == name)
                {
                    if (r == definition)
                        Add(r, EnumSupport.ScopedDeclaration(t, at, name, newBase), "enum class");
                    else if (r.Kind == "declaration")
                        Add(r, EnumSupport.ScopedDeclaration(t, at, name, newBase), "enum class (declaration ahead)");
                    else
                        Add(r, EnumSupport.UnwrapEnumAsByte(t, at, name), "TEnumAsByte dropped");
                }
                else if (word.Length > 0)
                {
                    if (inBody && r.Kind == "definition")
                        values.Add((at, word));
                    if (!inBody && r.Kind != "definition" && r.Kind != "declaration")
                        Add(r, EnumSupport.Qualify(t, at, name), "qualified");
                }
            }

            var items = new List<PreviewItem>();
            foreach (var file in changes)
            {
                string t = TextOf(file.Key);
                foreach (TextEdit e in EnumSupport.Fold(t, file.Value))
                {
                    var change = (Change)e.Tag;
                    items.Add(new PreviewItem
                    {
                        Group = file.Key,
                        Label = $"line {change.Ref.Line}: {change.What}" + (change.Ref.Confirmed ? "" : "   (possible match)"),
                        Diff = RefactorPreviewDialog.Diff(t, e.Start, e.Length, e.Text, 1),
                        Checked = change.Ref.Confirmed,
                        Tag = FileEdit.Replace(file.Key, t, e.Start, e.Length, e.Text),
                    });
                }
            }
            int casts = items.Count(i => i.Label.Contains("static_cast"));
            string intro = $"Make {target.Label.Split(' ')[0]} an enum class{(newBase.Length > 0 ? " : " + newBase : "")}: its values are written {name}::Value " +
                           $"where they are used{(casts > 0 ? $", and {casts} implicit conversion{(casts == 1 ? "" : "s")} to numbers get a static_cast" : "")}. " +
                           "Possible matches are unchecked.";
            if (uenum)
            {
                intro += $"\n{name} is a UENUM: Blueprints and saved assets refer to its values by name." +
                         (blueprint && newBase != "uint8" ? " A BlueprintType enum class must be based on uint8." : "");
                string module = Rename.ModuleOf(definition.Path), project = Rename.ProjectOf(definition.Path);
                string ini = project == null ? null : System.IO.Path.Combine(project, "Config", "DefaultEngine.ini");
                if (module != null && ini != null && System.IO.File.Exists(ini) && values.Count > 0)
                {
                    string line = RenameSupport.EnumValueRedirect(module, name, values.OrderBy(v => v.At).Select(v => v.Name));
                    string iniText = Documents.Read(ini);
                    var (offset, insert) = RenameSupport.InsertRedirect(iniText, line);
                    intro += $" If assets no longer find its values, the [CoreRedirects] line in {System.IO.Path.GetFileName(ini)} (unchecked) maps the old names to the new.";
                    items.Add(new PreviewItem
                    {
                        Group = ini,
                        Label = "[CoreRedirects] " + line,
                        Diff = RefactorPreviewDialog.Diff(iniText, offset, 0, insert, 1),
                        Checked = false,
                        Tag = FileEdit.Insert(ini, offset, insert),
                    });
                }
            }
            var preview = new RefactorPreviewDialog("Convert to Scoped Enum", intro, items, "_Convert");
            if (preview.ShowModal() != true || preview.Accepted.Count == 0)
                return;
            if (!Documents.Apply(preview.Accepted.Select(i => (FileEdit)i.Tag).ToList(), $"Convert {name} to Scoped Enum", out string error))
                EditorCommands.Status(error);
            else
                EditorCommands.Status($"{name} is an enum class now ({preview.Accepted.Count} changes in {preview.Accepted.Select(i => i.Group).Distinct().Count()} files)");
        }

        // vx_switch_at's lines as offsets in the parsed text.
        private static SwitchShape Shape(RefactorContext c, Dictionary<string, List<string[]>> s)
        {
            if (!s.TryGetValue("switch", out var sw) || !s.TryGetValue("body", out var body))
                return null;
            var shape = new SwitchShape
            {
                SwitchStart = c.OffsetOf(int.Parse(sw[0][1]), int.Parse(sw[0][2])),
                BodyOpen = c.OffsetOf(int.Parse(body[0][1]), int.Parse(body[0][2])),
                BodyClose = c.OffsetOf(int.Parse(body[0][3]), int.Parse(body[0][4])) - 1,
            };
            if (s.TryGetValue("enum", out var e))
            {
                shape.EnumName = e[0][0];
                shape.Scoped = e[0][1] == "1";
                shape.Qualifier = e[0].Length > 2 ? e[0][2] : "";
            }
            List<string[]> Lines(string key) => s.TryGetValue(key, out var list) ? list : new List<string[]>();
            foreach (string[] f in Lines("enumerator"))
                shape.Enumerators.Add((f[0], long.Parse(f[1]), int.Parse(f[2])));
            foreach (string[] f in Lines("case"))
                shape.Cases.Add((long.Parse(f[0]), c.OffsetOf(int.Parse(f[1]), int.Parse(f[2])), f.Length > 3 ? f[3] : ""));
            if (s.TryGetValue("default", out var d))
                shape.Default = c.OffsetOf(int.Parse(d[0][1]), int.Parse(d[0][2]));
            return shape;
        }
    }
}
