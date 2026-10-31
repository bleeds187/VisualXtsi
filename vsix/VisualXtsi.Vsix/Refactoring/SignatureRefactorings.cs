using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Change Signature: reorder, add, remove, rename and retype the parameters of the function at
    /// the caret; its declarations, definitions and overrides (found by Find References) get the new
    /// parameter list, renamed parameters are renamed in the bodies, and every call gets its
    /// arguments moved, dropped or added. Unreal: Name_Implementation and Name_Validate change along,
    /// and a Blueprint-exposed UFUNCTION is flagged.
    /// </summary>
    internal static class SignatureRefactorings
    {
        public static bool CanChangeSignature(RefactorContext c) => c != null && (c.Info == null || c.Info.IsFunction);

        private sealed class Site
        {
            public Engine.Reference Ref;
            public string Name;      // as found there (Name, Name_Implementation, Name_Validate)
            public bool Lexical;     // found by text: its kind is judged from the text
        }

        public static async Task ChangeSignatureAsync()
        {
            const string title = "Change Signature";
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (f == null || !f.IsFunction)
            {
                EditorCommands.Status("put the caret on a function");
                return;
            }
            if (f.Parameters.Any(p => p.Text == "..."))
            {
                EditorCommands.Status($"{f.Name} takes variable arguments (...): not supported");
                return;
            }
            var parameters = f.Parameters.Select((p, i) =>
            {
                var (type, name, _) = SignatureSupport.Parse(p.Text);
                if (name.Length == 0)
                    name = p.Name;
                return new SignatureParameter
                {
                    OldIndex = i, OldType = type, OldName = name, OldDefault = p.Default ?? "",
                    Type = type, Name = name, Default = p.Default ?? "",
                };
            }).ToList();

            // Unreal: the reflection macro of the declaration.
            string macro = "";
            if (f.DeclarationExtent != null && Documents.Read(f.DeclarationExtent.Path) is string declText)
                macro = MemberSupport.MacroText(declText, TextScan.Offset(declText, f.DeclarationExtent.Line, f.DeclarationExtent.Column));
            bool blueprint = Regex.IsMatch(macro, @"\b(BlueprintCallable|BlueprintPure|BlueprintImplementableEvent|BlueprintNativeEvent|BlueprintAuthorityOnly|Exec)\b");
            var notes = new List<string>();
            if ((f.Ue & CursorInfo.UeImplementation) != 0)
                notes.Add($"{f.Name}_Implementation" + ((f.Ue & CursorInfo.UeValidate) != 0 ? $" and {f.Name}_Validate" : "") + " change along.");
            if (blueprint)
                notes.Add($"{f.Name} is a Blueprint-exposed UFUNCTION: Blueprint graphs that use it need their nodes refreshed and pins reconnected.");
            var dialog = new ChangeSignatureDialog(f.Name, $"Parameters of {(f.Classes.Length > 0 ? f.Classes + "::" : "")}{f.Name}:", parameters, string.Join(" ", notes));
            if (dialog.ShowModal() != true)
                return;
            var changed = dialog.Parameters;
            bool same = changed.Count == parameters.Count && changed.Select((p, i) => p.OldIndex == i && !p.Renamed && !p.Retyped && p.Default == p.OldDefault).All(x => x);
            if (same)
            {
                EditorCommands.Status("the signature is unchanged");
                return;
            }

            // Every declaration, definition, override and call, from the name in this file.
            var (line, column) = f.AtDefinition && f.Definition != null && f.Definition.InFile(c.Path) ? (f.Definition.Line, f.Definition.Column)
                               : f.Declaration != null && f.Declaration.InFile(c.Path) ? (f.Declaration.Line, f.Declaration.Column)
                               : (c.Line, c.Column);
            uint job = await Task.Run(() => Engine.ReferencesStart(c.Path, line, column));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (job == 0)
            {
                EditorCommands.Status($"the uses of {f.Name} cannot be searched from here");
                return;
            }
            var found = await Rename.WaitAsync(job, $"Finding the uses of {f.Name}...", title);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found == null)
                return;
            var sites = found.Select(r => new Site { Ref = r, Name = f.Name }).ToList();
            foreach (var (flag, suffix) in new[] { (CursorInfo.UeImplementation, "_Implementation"), (CursorInfo.UeValidate, "_Validate") })
            {
                if ((f.Ue & flag) == 0)
                    continue;
                uint words = await Task.Run(() => Engine.ReferencesStartWord(f.Name + suffix, confirmed: true));
                var more = await Rename.WaitAsync(words, $"Finding {f.Name}{suffix}...", title);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (more == null)
                    return;
                sites.AddRange(more.Select(r => new Site { Ref = r, Name = f.Name + suffix, Lexical = true }));
            }
            sites = sites.Where(s => !Documents.IsGenerated(s.Ref.Path))
                         .GroupBy(s => (s.Ref.Path.ToLowerInvariant(), s.Ref.Line, s.Ref.Column)).Select(g => g.First()).ToList();
            // Default arguments belong on declarations; a function defined without one has them on its definition.
            bool declared = sites.Any(s => !s.Lexical && s.Ref.Kind == "declaration");

            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string TextOf(string path)
            {
                if (!texts.TryGetValue(path, out string t))
                    texts[path] = t = Documents.Read(path);
                return t;
            }
            var items = new List<PreviewItem>();
            int notCalls = 0, unfixable = 0;
            foreach (Site site in sites)
            {
                string text = TextOf(site.Ref.Path);
                if (text == null)
                    continue;
                int at = TextScan.Offset(text, site.Ref.Line, site.Ref.Column);
                if (at + site.Name.Length > text.Length || text.Substring(at, site.Name.Length) != site.Name)
                    continue; // changed since it was searched
                var parens = SignatureSupport.ParensAfter(text, at + site.Name.Length);
                if (parens == null)
                {
                    notCalls++; // &UThing::Name, a delegate binding, a using-declaration...
                    continue;
                }
                var (open, close) = parens.Value;
                string inner = text.Substring(open + 1, close - open - 1);
                string kind = site.Lexical ? SignatureSupport.SiteKind(text, at, close)
                            : site.Ref.Kind == "definition" || site.Ref.Kind == "declaration" ? site.Ref.Kind : "call";
                string where = $"line {site.Ref.Line}: ";
                if (kind == "call")
                {
                    string args = SignatureSupport.ArgumentList(inner, changed);
                    if (args == null)
                    {
                        unfixable++;
                        continue;
                    }
                    if (args != inner)
                        items.Add(Item(site, text, open + 1, inner.Length, args, where + "call: " + site.Ref.Text));
                    continue;
                }
                bool withDefaults = !site.Lexical && (kind == "declaration" || !declared);
                string list = SignatureSupport.ParameterList(inner, changed, withDefaults);
                var old = SignatureSupport.Split(inner).Select(SignatureSupport.Parse).ToList();
                string note = "";
                var body = new List<PreviewItem>();
                if (kind == "definition")
                {
                    // The body (and constructor initializers): renamed parameters renamed; removed ones still used are noted.
                    int bodyEnd = TextScan.BlockEnd(text, close);
                    if (bodyEnd > close)
                    {
                        foreach (SignatureParameter p in changed.Where(p => p.Renamed && p.OldIndex < old.Count && old[p.OldIndex].Name.Length > 0 && old[p.OldIndex].Name != p.Name))
                            foreach (int use in SignatureSupport.UsesIn(text, close + 1, bodyEnd, old[p.OldIndex].Name))
                                body.Add(Item(site, text, use, old[p.OldIndex].Name.Length, p.Name, $"line {TextScan.LineOf(text, use)}: {old[p.OldIndex].Name} -> {p.Name}"));
                        var removed = Enumerable.Range(0, old.Count).Where(i => changed.All(p => p.OldIndex != i)).Select(i => old[i].Name)
                                                .Where(n => n.Length > 0 && SignatureSupport.UsesIn(text, close + 1, bodyEnd, n).Count > 0).ToList();
                        if (removed.Count > 0)
                            note = $"   ({string.Join(", ", removed)} still used in the body)";
                    }
                }
                if (list != inner)
                    items.Add(Item(site, text, open + 1, inner.Length, list, where + kind + note));
                items.AddRange(body);
            }
            if (items.Count == 0)
            {
                EditorCommands.Status($"nothing to change for {f.Name}");
                return;
            }
            string intro = $"Change {f.Name} to {SignatureSupport.Describe(f.Name, changed)}: {items.Count} edits; possible matches are unchecked.";
            if (notCalls > 0)
                intro += $"\n{notCalls} use{(notCalls == 1 ? " is" : "s are")} not a call (a function pointer, a delegate binding): check {(notCalls == 1 ? "it" : "them")} by hand.";
            if (unfixable > 0)
                intro += $"\n{unfixable} call{(unfixable == 1 ? "" : "s")} could not be changed: a new parameter needs a value for calls.";
            if (blueprint)
                intro += $"\n{f.Name} is Blueprint-exposed: Blueprint graphs that use it need their nodes refreshed.";
            var preview = new RefactorPreviewDialog(title, intro, items, "_Change");
            if (preview.ShowModal() != true || preview.Accepted.Count == 0)
                return;
            if (!Documents.Apply(preview.Accepted.Select(i => (FileEdit)i.Tag).ToList(), $"{title} ({f.Name})", out string error))
                EditorCommands.Status(error);
            else
                EditorCommands.Status($"changed {f.Name} in {preview.Accepted.Select(i => i.Group).Distinct().Count()} files");
        }

        private static PreviewItem Item(Site site, string text, int start, int length, string replacement, string label) => new PreviewItem
        {
            Group = site.Ref.Path,
            Label = label + (site.Ref.Confirmed ? "" : "   (possible match)"),
            Diff = RefactorPreviewDialog.Diff(text, start, length, replacement, 1),
            Checked = site.Ref.Confirmed,
            Tag = FileEdit.Replace(site.Ref.Path, text, start, length, replacement),
        };
    }
}
