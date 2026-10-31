using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.Options;
using VisualXtsi.UI;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Create File (a module-aware header and source for a new class, from a template), Move Class to
    /// New File (the class at the caret and its method definitions into files of their own) and
    /// Rename Files (a header/source pair, with every #include of them). New files are written to
    /// disk only: Unreal's generated project files list sources, so they need regenerating.
    /// </summary>
    internal static class FileRefactorings
    {
        private const string RegenerateHint = "regenerate the project files for the solution to list them";

        public static bool CanCreateFile(RefactorContext c) => c != null;
        public static bool CanMoveClass(RefactorContext c) => c != null && (c.Info == null || c.Info.InClass != null && Documents.IsHeader(c.Info.InClass.Path));
        public static bool CanRenameFiles(RefactorContext c) => c != null;

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        // The comment new files start with: the project's CopyrightNotice, else the one in the options.
        private static string Copyright(string near)
        {
            string project = Rename.ProjectOf(near);
            string ini = project == null ? null : Path.Combine(project, "Config", "DefaultGame.ini");
            string notice = ini != null && File.Exists(ini) ? FileSupport.CopyrightFrom(File.ReadAllText(ini)) : "";
            if (notice.Length == 0 && !string.IsNullOrWhiteSpace(VxSettings.Current.CopyrightNotice))
                notice = FileSupport.CopyrightFrom("CopyrightNotice=" + VxSettings.Current.CopyrightNotice.Trim());
            return notice;
        }

        private static string ModuleName(string moduleRoot)
        {
            string buildCs = moduleRoot == null ? null : Directory.EnumerateFiles(moduleRoot, "*.Build.cs").FirstOrDefault();
            return buildCs == null ? null : Path.GetFileName(buildCs).Substring(0, Path.GetFileName(buildCs).Length - ".Build.cs".Length);
        }

        // ---- Create File

        public static async Task CreateFileAsync()
        {
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Path == null)
                return;
            string moduleRoot = IncludeRefactorings.ModuleRoot(c.Path);
            string module = ModuleName(moduleRoot);
            var (headerDir, sourceDir) = FileSupport.Layout(moduleRoot, c.Path, Directory.Exists);
            var dialog = new CreateFileDialog(module, headerDir, sourceDir);
            if (dialog.ShowModal() != true)
                return;
            string header = dialog.HeaderPath, source = dialog.SourcePath;
            if (File.Exists(header) || source != null && File.Exists(source))
            {
                EditorCommands.Status("a file with that name is there already");
                return;
            }
            string include = source == null ? Path.GetFileName(header) : IncludeSupport.IncludePath(header, moduleRoot, source, moduleRoot);
            string api = module == null ? "" : module.ToUpperInvariant() + "_API";
            var (headerText, sourceText) = FileSupport.Template(dialog.Kind, dialog.ClassName, api, include, Copyright(c.Path), "\r\n");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(header));
                File.WriteAllText(header, headerText, Utf8);
                if (source != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source));
                    File.WriteAllText(source, sourceText, Utf8);
                }
            }
            catch (Exception ex)
            {
                EditorCommands.Status($"cannot create the files: {ex.Message}");
                return;
            }
            if (source != null)
                EditorCommands.OpenDocument(source);
            EditorCommands.OpenDocument(header);
            EditorCommands.Status($"created {Path.GetFileName(header)}" + (source != null ? $" and {Path.GetFileName(source)}" : "") + "; " + RegenerateHint);
        }

        // ---- Move Class to New File

        public static async Task MoveClassAsync()
        {
            const string title = "Move Class to New File";
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CursorInfo f = c?.Info;
            if (f?.InClass == null)
            {
                EditorCommands.Status("put the caret in a class");
                return;
            }
            string className = f.InClassClasses;
            if (className.Contains("::"))
            {
                EditorCommands.Status($"{className} is nested in another class; it moves with it");
                return;
            }
            string header = f.InClass.Path;
            if (!Documents.IsHeader(header))
            {
                EditorCommands.Status($"{className} is defined in a source file");
                return;
            }
            string text = Documents.Read(header);
            string nl = TextScan.Newline(text);
            int classStart = TextScan.Offset(text, f.InClass.Line, f.InClass.Column), classEnd = TextScan.Offset(text, f.InClass.EndLine, f.InClass.EndColumn);
            var (blockStart, blockEnd) = FileSupport.ClassBlock(text, classStart, classEnd);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            // What the class needs from the header it leaves, and whether the rest of that header needs it:
            // both would make the two headers include each other.
            string rest = text.Remove(blockStart, blockEnd - blockStart);
            var leftBehind = FileSupport.DeclaredNames(rest);
            leftBehind.Remove(className);
            List<string> needs = FileSupport.Uses(block, leftBehind);
            bool usedAfter = FileSupport.Uses(rest, new[] { className }).Count > 0;
            if (needs.Count > 0 && usedAfter)
            {
                EditorCommands.Status($"{className} uses {string.Join(", ", needs.Take(3))} from {Path.GetFileName(header)}, which uses {className} too: " +
                                      "the two headers would need each other; move those along first");
                return;
            }
            string source = Documents.SourceFor(header);
            string dir = Path.GetDirectoryName(header);
            var dialog = new NameOptionsDialog(title, "_File name for the class (no extension):", FileSupport.BaseName(className),
                new[] { ("Move its method _definitions from " + (source == null ? "the source file" : Path.GetFileName(source)) + " to a new source file", source != null, source != null) },
                n => !Regex.IsMatch(n ?? "", @"^[\w.-]+$") ? "Type a file name." :
                     string.Equals(n + ".h", Path.GetFileName(header), StringComparison.OrdinalIgnoreCase) ? "That is the current file." :
                     File.Exists(Path.Combine(dir, n + ".h")) ? $"{n}.h is there already." : null,
                (n, o) => Path.Combine(dir, n + ".h") + (o[0] ? nl + Path.Combine(Path.GetDirectoryName(source), n + ".cpp") : ""));
            if (dialog.ShowModal() != true)
                return;
            string name = dialog.Value;
            string newHeader = Path.Combine(dir, name + ".h");
            string moduleRoot = IncludeRefactorings.ModuleRoot(header);
            string copyright = Copyright(header);

            // The new header: the old one's includes (its own .generated.h aside), the class (in its namespaces).
            bool reflected = FileSupport.HasReflectedTypes(block);
            var hb = new StringBuilder();
            if (copyright.Length > 0)
                hb.Append(copyright).Append(nl).Append(nl);
            hb.Append("#pragma once").Append(nl).Append(nl);
            foreach (string inc in FileSupport.Includes(text).Where(i => !i.Contains(".generated.h")))
                hb.Append(inc).Append(nl);
            if (needs.Count > 0) // what it uses from the header it leaves
                hb.Append("#include \"").Append(IncludeSupport.IncludePath(header, moduleRoot, newHeader, moduleRoot)).Append('"').Append(nl);
            if (reflected)
                hb.Append("#include \"").Append(name).Append(".generated.h\"").Append(nl);
            hb.Append(nl);
            string ns = f.InClassNamespaces;
            if (ns.Length > 0)
                hb.Append("namespace ").Append(ns).Append(nl).Append('{').Append(nl);
            hb.Append(block.TrimEnd('\r', '\n')).Append(nl);
            if (ns.Length > 0)
                hb.Append("}").Append(nl);

            // The old header: the class out (with a blank line after it), the new header included (unless
            // the new one includes it: then code that uses the class includes the new header itself).
            var edits = new List<(FileEdit Edit, string Text, string Label)>();
            int removeEnd = blockEnd;
            if (removeEnd < text.Length && text.Substring(removeEnd, TextScan.LineEnd(text, removeEnd) - removeEnd).Trim().Length == 0)
                removeEnd = TextScan.NextLineStart(text, removeEnd);
            edits.Add((FileEdit.Replace(header, text, blockStart, removeEnd - blockStart, ""), text, $"{className} moved out"));
            string remaining = text.Remove(blockStart, removeEnd - blockStart);
            string include = IncludeSupport.IncludePath(newHeader, moduleRoot, header, moduleRoot);
            string oldGenerated = Path.GetFileNameWithoutExtension(header) + ".generated.h";
            Match generatedLine = Regex.Match(text, @"^[ \t]*#[ \t]*include[ \t]*[""<][^"">]*" + Regex.Escape(oldGenerated) + @"[>""][^\r\n]*", RegexOptions.Multiline);
            bool dropGenerated = generatedLine.Success && !FileSupport.HasReflectedTypes(remaining);
            if (needs.Count > 0)
            {
                if (dropGenerated)
                    edits.Add((FileEdit.Replace(header, text, generatedLine.Index, TextScan.NextLineStart(text, generatedLine.Index) - generatedLine.Index, ""), text, $"{oldGenerated} no longer needed"));
            }
            else if (dropGenerated)
            {
                // Nothing reflected is left: its .generated.h line becomes the include of the new header.
                edits.Add((FileEdit.Replace(header, text, generatedLine.Index, generatedLine.Length, $"#include \"{include}\""), text, $"#include \"{include}\" instead of {oldGenerated}"));
            }
            else if (IncludeSupport.IncludeInsertion(text, include, Path.GetFileName(header)) is var insertion && insertion.HasValue && insertion.Value.Offset <= blockStart)
            {
                edits.Add((FileEdit.Insert(header, insertion.Value.Offset, insertion.Value.Text), text, $"#include \"{include}\""));
            }

            // Its method definitions: into a new source file next to the old one.
            string newSource = null, sourceText = null;
            if (dialog.Option(0) && source != null && Documents.Read(source) is string src)
            {
                string snl = TextScan.Newline(src);
                var ranges = new List<(int Start, int End)>();
                foreach (Match m in Regex.Matches(src, @"\b" + Regex.Escape(className) + @"\s*::\s*(~?\w+)\s*(?=\()"))
                {
                    if (TextScan.IndentOf(src, TextScan.LineStart(src, m.Index)).Length > 0 || ranges.Any(r => m.Index >= r.Start && m.Index < r.End))
                        continue; // inside a function, or not at the top level
                    var parens = SignatureSupport.ParensAfter(src, m.Groups[1].Index + m.Groups[1].Length);
                    if (parens == null)
                        continue;
                    int close = parens.Value.Close, brace = src.IndexOf('{', close), semi = src.IndexOf(';', close);
                    if (brace < 0 || semi >= 0 && semi < brace)
                        continue; // a declaration or a call, not a definition
                    int start = ImplementationRefactorings.DefinitionStart(src, m.Index);
                    int end = TextScan.NextLineStart(src, TextScan.BlockEnd(src, close));
                    if (end < src.Length && src.Substring(end, TextScan.LineEnd(src, end) - end).Trim().Length == 0)
                        end = TextScan.NextLineStart(src, end);
                    ranges.Add((start, end));
                }
                if (ranges.Count > 0)
                {
                    newSource = Path.Combine(Path.GetDirectoryName(source), name + ".cpp");
                    if (File.Exists(newSource))
                    {
                        EditorCommands.Status($"{Path.GetFileName(newSource)} is there already");
                        return;
                    }
                    var sb = new StringBuilder();
                    if (copyright.Length > 0)
                        sb.Append(copyright).Append(snl).Append(snl);
                    sb.Append("#include \"").Append(IncludeSupport.IncludePath(newHeader, moduleRoot, newSource, moduleRoot)).Append('"').Append(snl);
                    foreach (string inc in FileSupport.Includes(src))
                        sb.Append(inc).Append(snl);
                    sb.Append(snl);
                    sb.Append(string.Join(snl, ranges.OrderBy(r => r.Start).Select(r => src.Substring(r.Start, r.End - r.Start).TrimEnd('\r', '\n') + snl)));
                    sourceText = sb.ToString();
                    foreach (var r in ranges.OrderByDescending(r => r.Start))
                        edits.Add((FileEdit.Replace(source, src, r.Start, r.End - r.Start, ""), src, $"definition moved: {src.Substring(r.Start, TextScan.LineEnd(src, r.Start) - r.Start).Trim()}"));
                }
            }

            // A preview of it all; it goes together or not at all.
            var items = new List<PreviewItem>
            {
                new PreviewItem { Group = newHeader, Label = "new file", Diff = string.Join("\n", hb.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => "+ " + l)) },
            };
            if (newSource != null)
                items.Add(new PreviewItem { Group = newSource, Label = "new file", Diff = string.Join("\n", sourceText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => "+ " + l)) });
            items.AddRange(edits.Select(e => new PreviewItem
            {
                Group = e.Edit.Path,
                Label = $"line {TextScan.LineOf(e.Text, e.Edit.Start)}: {e.Label}",
                Diff = RefactorPreviewDialog.Diff(e.Text, e.Edit.Start, e.Edit.Length, e.Edit.NewText, 1),
            }));
            string intro = $"Move {className} to {Path.GetFileName(newHeader)}" + (newSource != null ? $" and {Path.GetFileName(newSource)}" : "") +
                           $". The new files are written to disk; {RegenerateHint}.";
            if (needs.Count > 0)
                intro += $"\n{Path.GetFileName(newHeader)} includes {Path.GetFileName(header)} for {string.Join(", ", needs.Take(4))}; code that uses {className} includes {Path.GetFileName(newHeader)}.";
            var preview = new RefactorPreviewDialog(title, intro, items, "_Move");
            if (preview.ShowModal() != true)
                return;
            if (preview.Accepted.Count != items.Count)
            {
                EditorCommands.Status("the moved class, its new files and the edits go together; nothing was changed");
                return;
            }
            try
            {
                File.WriteAllText(newHeader, hb.ToString(), Utf8);
                if (newSource != null)
                    File.WriteAllText(newSource, sourceText, Utf8);
            }
            catch (Exception ex)
            {
                EditorCommands.Status($"cannot create the files: {ex.Message}");
                return;
            }
            if (!Documents.Apply(edits.Select(e => e.Edit).ToList(), title, out string error))
            {
                File.Delete(newHeader);
                if (newSource != null)
                    File.Delete(newSource);
                EditorCommands.Status(error);
                return;
            }
            EditorCommands.OpenDocument(newHeader);
            EditorCommands.Status($"moved {className} to {Path.GetFileName(newHeader)}" + (newSource != null ? $" and {Path.GetFileName(newSource)}" : "") + "; " + RegenerateHint);
        }

        // ---- Rename Files

        public static async Task RenameFilesAsync()
        {
            const string title = "Rename Files";
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Path == null)
                return;
            string oldBase = Path.GetFileNameWithoutExtension(c.Path);
            string counterpart = Documents.IsHeader(c.Path) ? Documents.SourceFor(c.Path) : Documents.HeaderFor(c.Path);
            var files = new List<string> { c.Path };
            if (counterpart != null && string.Equals(Path.GetFileNameWithoutExtension(counterpart), oldBase, StringComparison.OrdinalIgnoreCase))
                files.Add(counterpart);
            var dialog = new NameOptionsDialog(title, "_New name (no extension):", oldBase, new (string, bool, bool)[0],
                n => !Regex.IsMatch(n ?? "", @"^[\w.-]+$") ? "Type a file name." :
                     n == oldBase ? "That is the current name." :
                     files.Any(p => !string.Equals(n, oldBase, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(p), n + Path.GetExtension(p))))
                         ? "A file with that name is there already." : null,
                (n, o) => string.Join(Environment.NewLine, files.Select(p => Path.GetFileName(p) + " -> " + n + Path.GetExtension(p))));
            if (dialog.ShowModal() != true)
                return;
            string newBase = dialog.Value;
            var renames = files.Select(p => (Old: p, New: Path.Combine(Path.GetDirectoryName(p), newBase + Path.GetExtension(p)))).ToList();
            var open = renames.Where(r => VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, r.Old, Guid.Empty, out _, out _, out _)).Select(r => r.Old).ToList();

            // Every #include of the files (by text, then the path checked), and the header's own .generated.h.
            uint job = await Task.Run(() => Engine.ReferencesStartWord(oldBase, confirmed: true));
            var found = await Rename.WaitAsync(job, $"Finding the includes of {oldBase}...", title);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found == null)
                return;
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<PreviewItem>();
            var namesakes = renames.ToDictionary(r => r.Old, r => Engine.FindFiles(Path.GetFileName(r.Old), 50)
                .Count(p => string.Equals(Path.GetFileName(p), Path.GetFileName(r.Old), StringComparison.OrdinalIgnoreCase)));
            foreach (var line in found.Where(r => r.Text.TrimStart().StartsWith("#")).GroupBy(r => (r.Path.ToLowerInvariant(), r.Line)).Select(g => g.First()))
            {
                if (Documents.IsGenerated(line.Path))
                    continue;
                if (!texts.TryGetValue(line.Path, out string text))
                    texts[line.Path] = text = Documents.Read(line.Path);
                if (text == null)
                    continue;
                int start = TextScan.Offset(text, line.Line, 1), end = TextScan.LineEnd(text, start);
                string current = text.Substring(start, end - start);
                foreach (var (oldPath, newPath) in renames)
                {
                    string rewritten = FileSupport.RewriteInclude(current, Path.GetFileName(oldPath), Path.GetFileName(newPath));
                    bool generated = false;
                    if (rewritten == null && Documents.IsHeader(oldPath) && string.Equals(line.Path, oldPath, StringComparison.OrdinalIgnoreCase))
                    {
                        rewritten = FileSupport.RewriteInclude(current, oldBase + ".generated.h", newBase + ".generated.h");
                        generated = rewritten != null;
                    }
                    if (rewritten == null)
                        continue;
                    string path = Regex.Match(current, @"[""<]([^"">]*)[>""]").Groups[1].Value;
                    bool bare = !path.Contains("/") && !path.Contains("\\");
                    if (!generated && !FileSupport.IncludeMatches(path, oldPath) && !bare)
                        continue; // another file of that name
                    bool sure = generated || !bare || namesakes[oldPath] <= 1;
                    items.Add(new PreviewItem
                    {
                        Group = line.Path,
                        Label = $"line {line.Line}: {rewritten.Trim()}" + (sure ? "" : "   (another file has that name)"),
                        Diff = RefactorPreviewDialog.Diff(text, start, end - start, rewritten, 1),
                        Checked = sure,
                        Tag = FileEdit.Replace(line.Path, text, start, end - start, rewritten),
                    });
                    break;
                }
            }
            string intro = $"Rename {string.Join(" and ", renames.Select(r => Path.GetFileName(r.Old)))} to {string.Join(" and ", renames.Select(r => Path.GetFileName(r.New)))}, " +
                           $"with {items.Count} include{(items.Count == 1 ? "" : "s")}. The files are renamed on disk; {RegenerateHint}.";
            var preview = new RefactorPreviewDialog(title, intro, items, "_Rename");
            if (preview.ShowModal() != true)
                return;
            if (preview.Accepted.Count > 0 && !Documents.Apply(preview.Accepted.Select(i => (FileEdit)i.Tag).ToList(), title, out string error))
            {
                EditorCommands.Status(error);
                return;
            }
            // The files themselves: moved on disk; open documents keep their text (and undo) under the new name.
            var table = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            foreach (var (oldPath, newPath) in renames)
            {
                try
                {
                    File.Move(oldPath, newPath);
                    if (table != null && (open.Contains(oldPath) || VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, oldPath, Guid.Empty, out _, out _, out _)))
                        table.RenameDocument(oldPath, newPath, new IntPtr(-1), VSConstants.VSITEMID_NIL);
                }
                catch (Exception ex)
                {
                    EditorCommands.Status($"cannot rename {Path.GetFileName(oldPath)}: {ex.Message}");
                    Log.Write($"Rename Files: {oldPath} -> {newPath}: {ex}");
                    return;
                }
            }
            EditorCommands.Status($"renamed to {newBase} ({preview.Accepted.Count} includes); {RegenerateHint}");
        }
    }
}
