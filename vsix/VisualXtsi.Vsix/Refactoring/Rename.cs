using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualXtsi.Commands;
using VisualXtsi.Interop;
using VisualXtsi.UI;
using Task = System.Threading.Tasks.Task;

namespace VisualXtsi.Refactoring
{
    /// <summary>
    /// Rename (Shift+Alt+R): every reference Find References confirms (overrides of a virtual method
    /// included) is renamed after a preview; possible matches are listed unchecked. Unreal: a
    /// BlueprintNativeEvent or RPC''s Name_Implementation and Name_Validate are renamed along, and
    /// renaming a reflected type or member warns about assets and offers a [CoreRedirects] line. C#
    /// goes to Visual Studio''s own (Roslyn) rename.
    /// </summary>
    internal static class Rename
    {
        private sealed class Occurrence
        {
            public Engine.Reference Ref;
            public string OldName, NewName;
        }

        public static async Task RunAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = EditorCommands.ActiveSourceDocument(package);
            if (path == null)
                return;
            if (CSharp.CSharpNavigation.IsCSharp(path))
            {
                try
                {
                    (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("Refactor.Rename");
                }
                catch (Exception ex)
                {
                    EditorCommands.Status("nothing to rename at the caret");
                    Log.Write($"Refactor.Rename: {ex.Message}");
                }
                return;
            }
            RefactorContext c = await RefactorContext.AtCaretAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (c?.Snapshot == null)
                return;
            uint job = await Task.Run(() => Engine.ReferencesStart(c.Path, c.Line, c.Column));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (job == 0)
            {
                EditorCommands.Status("nothing to rename at the caret");
                return;
            }
            var target = Engine.ReferencesTarget(job);
            string oldName = target.Name;
            CursorInfo info = c.Info;
            var dialog = new NameDialog("Rename", $"New name for {target.Label}:", oldName, n => RenameSupport.Validate(n?.Trim(), oldName));
            if (dialog.ShowModal() != true)
            {
                _ = Task.Run(() => Engine.ReferencesFree(job));
                return;
            }
            string newName = dialog.Value;

            var occurrences = new List<Occurrence>();
            var found = await WaitAsync(job, $"Finding the references of {oldName}...");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (found == null)
                return; // cancelled
            occurrences.AddRange(found.Select(r => new Occurrence { Ref = r, OldName = oldName, NewName = newName }));

            // Unreal native events and RPCs: their _Implementation and _Validate go along.
            if (info != null && info.IsFunction && info.Name == oldName)
                foreach (var (flag, suffix) in new[] { (CursorInfo.UeImplementation, "_Implementation"), (CursorInfo.UeValidate, "_Validate") })
                {
                    if ((info.Ue & flag) == 0)
                        continue;
                    uint words = await Task.Run(() => Engine.ReferencesStartWord(oldName + suffix, confirmed: true));
                    var more = await WaitAsync(words, $"Finding {oldName}{suffix}...");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (more == null)
                        return;
                    occurrences.AddRange(more.Select(r => new Occurrence { Ref = r, OldName = oldName + suffix, NewName = newName + suffix }));
                }

            // The edits, against each file as the editor has it.
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<PreviewItem>();
            foreach (Occurrence o in occurrences.GroupBy(o => (o.Ref.Path.ToLowerInvariant(), o.Ref.Line, o.Ref.Column)).Select(g => g.First()))
            {
                if (!texts.TryGetValue(o.Ref.Path, out string text))
                    texts[o.Ref.Path] = text = Documents.Read(o.Ref.Path);
                if (text == null)
                    continue;
                int at = TextScan.Offset(text, o.Ref.Line, o.Ref.Column);
                if (at + o.OldName.Length > text.Length || text.Substring(at, o.OldName.Length) != o.OldName)
                    continue; // changed since it was searched
                items.Add(new PreviewItem
                {
                    Group = o.Ref.Path,
                    Label = $"line {o.Ref.Line}: {o.Ref.Text}" + (o.Ref.Confirmed ? "" : "   (possible match)"),
                    Diff = RefactorPreviewDialog.Diff(text, at, o.OldName.Length, o.NewName, 1),
                    Checked = o.Ref.Confirmed,
                    Tag = FileEdit.Replace(o.Ref.Path, text, at, o.OldName.Length, o.NewName),
                });
            }
            if (items.Count == 0)
            {
                EditorCommands.Status($"no references of {oldName} found");
                return;
            }

            // Reflected names: assets refer to them by name.
            string intro = $"Rename {target.Label} to {newName}: {items.Count(i => i.Checked)} references; possible matches are unchecked.";
            var redirect = Redirect(info, oldName, newName);
            if (redirect.HasValue)
            {
                var (iniPath, iniText, offset, line, macro) = redirect.Value;
                intro += $"\n{oldName} is a {macro} name: Blueprints and other assets refer to it and will not find {newName}. " +
                         $"The [CoreRedirects] line in {Path.GetFileName(iniPath)} (unchecked) keeps them working.";
                items.Add(new PreviewItem
                {
                    Group = iniPath,
                    Label = "[CoreRedirects] " + line.Trim(),
                    Diff = RefactorPreviewDialog.Diff(iniText, offset, 0, InsertText(iniText, line), 1),
                    Checked = false,
                    Tag = FileEdit.Insert(iniPath, offset, InsertText(iniText, line)),
                });
            }
            var preview = new RefactorPreviewDialog("Rename", intro, items, "_Rename");
            if (preview.ShowModal() != true || preview.Accepted.Count == 0)
                return;
            if (!Documents.Apply(preview.Accepted.Select(i => (FileEdit)i.Tag).ToList(), $"Rename {oldName} to {newName}", out string error))
                EditorCommands.Status(error);
            else
                EditorCommands.Status($"renamed {oldName} to {newName} in {preview.Accepted.Select(i => i.Group).Distinct().Count()} files");
        }

        private static string InsertText(string ini, string line) => RenameSupport.InsertRedirect(ini, line).Text;

        // The [CoreRedirects] line for a reflected type or member, and where it goes (DefaultEngine.ini of the project).
        private static (string Path, string Text, int Offset, string Line, string Macro)? Redirect(CursorInfo info, string oldName, string newName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (info?.DeclarationExtent == null || info.Name != oldName)
                return null;
            string declFile = info.DeclarationExtent.Path;
            string decl = Documents.Read(declFile);
            if (decl == null)
                return null;
            string macro = RenameSupport.ReflectionMacro(decl, TextScan.Offset(decl, info.DeclarationExtent.Line, info.DeclarationExtent.Column));
            string module = ModuleOf(declFile);
            string project = ProjectOf(declFile);
            if (macro == null || module == null || project == null)
                return null;
            string line = RenameSupport.CoreRedirect(macro, module, info.ClassName, oldName, newName);
            string ini = Path.Combine(project, "Config", "DefaultEngine.ini");
            if (line == null || !File.Exists(ini))
                return null;
            string text = Documents.Read(ini);
            var (offset, _) = RenameSupport.InsertRedirect(text, line);
            return (ini, text, offset, line, macro);
        }

        private static string ModuleOf(string file)
        {
            for (string dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string buildCs = Directory.EnumerateFiles(dir, "*.Build.cs").FirstOrDefault();
                if (buildCs != null)
                {
                    string name = Path.GetFileName(buildCs);
                    return name.Substring(0, name.Length - ".Build.cs".Length);
                }
            }
            return null;
        }

        private static string ProjectOf(string file)
        {
            for (string dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.EnumerateFiles(dir, "*.uproject").Any())
                    return dir;
            return null;
        }

        /// <summary>Waits for a search behind Visual Studio''s wait dialog (with progress and Cancel); null if cancelled.</summary>
        private static async Task<List<Engine.Reference>> WaitAsync(uint job, string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var factory = (IVsThreadedWaitDialogFactory)Package.GetGlobalService(typeof(SVsThreadedWaitDialogFactory));
            using (ThreadedWaitDialogHelper.Session session = factory.StartWaitDialog("Rename - VisualXtsi", new ThreadedWaitDialogProgressData(message, "", "VisualXtsi: " + message, true), TimeSpan.FromMilliseconds(500)))
            {
                CancellationToken cancel = session.UserCancellationToken;
                for (;;)
                {
                    var (done, total, finished) = Engine.ReferencesProgress(job);
                    if (finished)
                        break;
                    if (cancel.IsCancellationRequested)
                    {
                        _ = Task.Run(() => Engine.ReferencesFree(job));
                        return null;
                    }
                    session.Progress.Report(new ThreadedWaitDialogProgressData(message, $"{done:N0} of {total:N0} files", null, true, done, Math.Max(1, total)));
                    await Task.Delay(150);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
            }
            var refs = await Task.Run(() => Engine.ReferencesResults(job, 0));
            _ = Task.Run(() => Engine.ReferencesFree(job));
            return refs;
        }
    }
}
