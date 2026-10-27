using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VisualXtsi.CSharp
{
    /// <summary>
    /// Finds Unreal modules (their *.Build.cs) by name in the engine a project uses, without
    /// indexing the engine: the engine is found as UnrealBuildTool finds it (an Engine folder above
    /// the project, else the .uproject's EngineAssociation in the registry), and its Build.cs files
    /// are listed once per engine, on first use.
    /// </summary>
    internal static class UnrealModules
    {
        private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, List<string>>>> Engines =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, List<string>>>>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Content", "Saved", "DerivedDataCache", "Resources", "Shaders", "Extras", "Documentation",
        };

        private static readonly Regex Association = new Regex("\"EngineAssociation\"\\s*:\\s*\"([^\"]*)\"");

        /// <summary>The Build.cs files of module <paramref name="name"/> in the engine of the project holding <paramref name="file"/>.</summary>
        public static IReadOnlyList<string> FindInEngine(string file, string name)
        {
            string engine = EngineDirectoryFor(file);
            if (engine == null)
                return Array.Empty<string>();
            Dictionary<string, List<string>> modules = Engines.GetOrAdd(engine, root => new Lazy<Dictionary<string, List<string>>>(() => List(root))).Value;
            return modules.TryGetValue(name, out List<string> paths) ? paths : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>The Engine folder for a file of a project (or of the engine itself); null if unknown.</summary>
        public static string EngineDirectoryFor(string file)
        {
            string uproject = null;
            for (string dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "Engine", "Source", "Runtime")))
                    return Path.Combine(dir, "Engine"); // inside an engine tree (a source build, or the engine's own files)
                if (uproject == null)
                    uproject = Directory.EnumerateFiles(dir, "*.uproject").FirstOrDefault();
            }
            if (uproject == null)
                return null;
            Match m = Association.Match(File.ReadAllText(uproject));
            string root = m.Success ? RegisteredEngine(m.Groups[1].Value) : null;
            return root != null && Directory.Exists(Path.Combine(root, "Engine")) ? Path.Combine(root, "Engine") : null;
        }

        // Source builds registered by UnrealVersionSelector (HKCU, by id) and installed engines (HKLM, by version).
        private static string RegisteredEngine(string association)
        {
            if (string.IsNullOrEmpty(association))
                return null;
            using (RegistryKey builds = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds"))
                if (builds?.GetValue(association) is string path)
                    return path;
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine\" + association))
                    if (key?.GetValue("InstalledDirectory") is string installed)
                        return installed;
            return null;
        }

        private static Dictionary<string, List<string>> List(string engine)
        {
            var modules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var started = DateTime.UtcNow;
            foreach (string top in new[] { "Source", "Plugins", "Platforms", "Restricted" })
                Walk(Path.Combine(engine, top), modules);
            Log.Write($"Unreal modules: {modules.Count} in {engine} ({(DateTime.UtcNow - started).TotalSeconds:F1} s)");
            return modules;
        }

        private static void Walk(string dir, Dictionary<string, List<string>> modules)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.Build.cs"))
                {
                    string name = Path.GetFileName(file);
                    name = name.Substring(0, name.Length - ".Build.cs".Length);
                    if (!modules.TryGetValue(name, out List<string> paths))
                        modules[name] = paths = new List<string>();
                    paths.Add(file);
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                    if (!Skipped.Contains(Path.GetFileName(sub)))
                        Walk(sub, modules);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
