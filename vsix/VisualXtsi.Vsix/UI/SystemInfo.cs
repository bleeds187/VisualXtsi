using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualXtsi.Interop;

namespace VisualXtsi.UI
{
    /// <summary>Versions, paths and cache sizes for the About box and the options' System Info page.</summary>
    internal static class SystemInfo
    {
        public static string Version
        {
            get
            {
                Version v = typeof(SystemInfo).Assembly.GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static string ExtensionFolder => Path.GetDirectoryName(typeof(SystemInfo).Assembly.Location);

        public static string LocalFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualXtsi");

        /// <summary>The PCH cache folder (VX_PCH_DIR overrides, as in the engine).</summary>
        public static string PchFolder =>
            Environment.GetEnvironmentVariable("VX_PCH_DIR") is string dir && dir.Length > 0 ? dir : Path.Combine(LocalFolder, "pch");

        /// <summary>The symbol cache file (VX_SYMBOL_CACHE overrides, as in the engine).</summary>
        public static string SymbolCache =>
            Environment.GetEnvironmentVariable("VX_SYMBOL_CACHE") is string file && file.Length > 0 ? file : Path.Combine(LocalFolder, "symbols.cache");

        /// <summary>The Visual Studio version, e.g. "17.14.36310.24". UI thread.</summary>
        public static string VisualStudioVersion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell &&
                shell.GetProperty((int)__VSSPROPID5.VSSPROPID_ReleaseVersion, out object version) == 0 && version is string text)
                return text.Split(' ')[0];
            return "unknown";
        }

        /// <summary>Label/value pairs; touches the disk (cache sizes), so call off the UI thread.</summary>
        public static IReadOnlyList<(string Label, string Value)> Collect(string visualStudioVersion)
        {
            var items = new List<(string, string)>
            {
                ("VisualXtsi", Version),
                ("Visual Studio", visualStudioVersion),
            };
            string clang;
            try
            {
                clang = Engine.ClangVersion() ?? "not available (lexical coloring only)";
                Engine.SymbolStatus(out int files, out int pending, out int symbols);
                items.Add(("libclang", clang));
                items.Add(("Engine", Path.Combine(ExtensionFolder, "vxengine.dll")));
                items.Add(("Files indexed", Engine.IndexFileCount().ToString("N0")));
                items.Add(("Symbols", $"{symbols:N0} in {files:N0} files" + (pending > 0 ? $" ({pending:N0} files to scan)" : "")));
            }
            catch (Exception ex)
            {
                items.Add(("Engine", "failed to load: " + ex.Message));
            }
            items.Add(("Extension folder", ExtensionFolder));
            items.Add(("Log file", Log.FilePath));
            items.Add(("Symbol cache", $"{SymbolCache} ({Size(FileSize(SymbolCache))})"));
            items.Add(("PCH cache", $"{PchFolder} ({Size(FolderSize(PchFolder))})"));
            return items;
        }

        public static string Format(IEnumerable<(string Label, string Value)> items) =>
            string.Join(Environment.NewLine, items.Select(i => $"{i.Label}: {i.Value}"));

        public static string Size(long bytes) =>
            bytes < 0 ? "none" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:N0} KB" : $"{bytes / (1024.0 * 1024):N1} MB";

        private static long FileSize(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static long FolderSize(string path)
        {
            try
            {
                return Directory.Exists(path)
                    ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
