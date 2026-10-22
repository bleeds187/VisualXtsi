using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VisualXtsi.Interop
{
    /// <summary>Kinds returned by vx_classify and vx_semantic_tokens; mirrors vx_kind in engine/include/vx/vx.h.</summary>
    internal enum VxKind : uint
    {
        Preprocessor = 1,
        Macro = 2,

        Local = 10,
        Parameter = 11,
        Field = 12,
        Global = 13,
        Type = 14,
        EnumConstant = 15,
        Namespace = 16,
        Function = 17,
        Method = 18,
        StaticMethod = 19,
        VirtualMethod = 20,
    }

    /// <summary>Mirrors vx_status.</summary>
    internal enum VxStatus
    {
        Ok = 0,
        BadArguments = 1,
        NotAvailable = 2,
        ParseFailed = 3,
    }

    /// <summary>Mirrors vx_pch_status.</summary>
    internal enum VxPchStatus
    {
        Ready = 0,
        NotApplicable = 1,
        Failed = 2,
        Busy = 3,
    }

    /// <summary>Mirrors vx_args_source.</summary>
    internal enum VxArgsSource
    {
        None = 0,
        CompileCommands = 1,
        Vcxproj = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VxSpan
    {
        public uint Start;
        public uint Length;
        public VxKind Kind;
    }

    internal sealed class MacroInfo
    {
        public string Name;
        public string Definition;
        public string Location;
        public string Expansion; // empty when hovering the definition itself
    }

    /// <summary>A symbol index entry (see vx_symbols_find).</summary>
    internal sealed class SymbolInfo
    {
        private static readonly string[] KindLabels =
        {
            "", "namespace", "class", "struct", "union", "enum", "enumerator", "typedef", "function", "method",
            "field", "variable", "macro", "delegate",
        };

        public int Kind;
        public bool Definition;
        public int Line;
        public int Column;
        public string Name;
        public string Scope;
        public string Path;

        public string KindLabel => (Kind > 0 && Kind < KindLabels.Length ? KindLabels[Kind] : "") + (Definition ? "" : " (declaration)");
    }

    /// <summary>P/Invoke surface of the native engine (vxengine.dll).</summary>
    internal static class Engine
    {
        private const string Dll = "vxengine.dll";
        private const uint ExpectedAbiVersion = 10;

        static Engine()
        {
            // devenv won't search the extension directory, so load the DLL by full path
            // before the first P/Invoke; later DllImports then bind to the loaded module.
            string dir = Path.GetDirectoryName(typeof(Engine).Assembly.Location);
            if (LoadLibraryW(Path.Combine(dir, Dll)) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to load " + Path.Combine(dir, Dll));
            uint abi = vx_abi_version();
            if (abi != ExpectedAbiVersion)
                throw new InvalidOperationException($"{Dll} ABI version {abi}, expected {ExpectedAbiVersion}");
        }

        /// <summary>Applies parse settings to parses started afterwards (environment variables still override).</summary>
        public static void Configure(bool skipHeaderBodies, bool sharedPch, int pchCacheMb)
        {
            var config = new VxConfig
            {
                SkipHeaderBodies = skipHeaderBodies ? 1u : 0u,
                SharedPch = sharedPch ? 1u : 0u,
                PchCacheMb = (uint)Math.Max(0, pchCacheMb),
            };
            vx_configure(ref config);
        }

        /// <summary>Deletes the cached shared PCHs that are not in use; returns the bytes freed.</summary>
        public static ulong ClearPchCache()
        {
            return vx_clear_pch_cache();
        }

        public static VxSpan[] Classify(string text)
        {
            var spans = new VxSpan[Math.Max(64, text.Length / 32)];
            uint count = vx_classify(text, (uint)text.Length, spans, (uint)spans.Length);
            if (count > spans.Length)
            {
                spans = new VxSpan[count];
                count = vx_classify(text, (uint)text.Length, spans, (uint)spans.Length);
            }
            Array.Resize(ref spans, (int)count);
            return spans;
        }

        /// <summary>
        /// Parses or reparses <paramref name="path"/> using its current editor text. The engine
        /// discovers arguments (compile_commands.json, then Unreal project files). Blocking: call
        /// from a background thread.
        /// </summary>
        public static VxStatus Parse(string path, string text)
        {
            return (VxStatus)vx_parse(path, IntPtr.Zero, 0, null, text, (uint)text.Length);
        }

        /// <summary>Where the engine finds compile arguments for <paramref name="path"/>, and how many.</summary>
        public static VxArgsSource DiscoverArgs(string path, out int argumentCount)
        {
            uint needed = vx_discover_args(path, null, 0, out uint source);
            var buffer = new char[needed];
            needed = vx_discover_args(path, buffer, (uint)buffer.Length, out source);
            argumentCount = 0;
            for (int i = 0; i < needed && i < buffer.Length; i++)
            {
                if (buffer[i] == '\0')
                    argumentCount++;
            }
            return (VxArgsSource)source;
        }

        /// <summary>
        /// Semantic spans for the text of the last successful parse of <paramref name="path"/>.
        /// Blocks while that file is being parsed: call from a background thread.
        /// </summary>
        public static VxSpan[] SemanticTokens(string path)
        {
            uint count = vx_semantic_tokens(path, null, 0);
            var spans = new VxSpan[count];
            count = vx_semantic_tokens(path, spans, (uint)spans.Length);
            if (count < spans.Length)
                Array.Resize(ref spans, (int)count);
            return spans;
        }

        /// <summary>
        /// Builds the shared precompiled header of the Unreal module containing <paramref name="path"/>
        /// if it does not exist yet. Takes seconds: call from a background thread.
        /// </summary>
        public static VxPchStatus PrepareModulePch(string path)
        {
            return (VxPchStatus)vx_prepare_module_pch(path);
        }

        public static void Release(string path)
        {
            vx_release(path);
        }

        /// <param name="line">1-based line.</param>
        /// <param name="column">1-based column in UTF-16 units.</param>
        public static MacroInfo GetMacroInfo(string path, int line, int column)
        {
            var buffer = new char[8192];
            uint needed = vx_macro_info(path, (uint)line, (uint)column, buffer, (uint)buffer.Length);
            if (needed == 0)
                return null;
            if (needed > buffer.Length)
            {
                buffer = new char[needed];
                needed = vx_macro_info(path, (uint)line, (uint)column, buffer, (uint)buffer.Length);
            }
            string[] parts = new string(buffer, 0, (int)needed).Split('\0');
            if (parts.Length < 4)
                return null;
            return new MacroInfo { Name = parts[0], Definition = parts[1], Location = parts[2], Expansion = parts[3] };
        }

        /// <summary>Header for a source or source for a header, best first. Touches the disk: background thread.</summary>
        public static string[] Counterparts(string path)
        {
            return SplitStrings((buffer, cap) => vx_counterparts(path, buffer, cap));
        }

        public static void IndexAddRoot(string dir) => vx_index_add_root(dir);

        /// <summary>Indexes the Unreal project containing <paramref name="path"/>; false if there is none.</summary>
        public static bool IndexAddProjectOf(string path) => vx_index_add_project_of(path) != 0;

        public static int IndexFileCount() => (int)vx_index_file_count();

        /// <summary>Fuzzy file search over the index, best first. Never waits for indexing.</summary>
        public static string[] FindFiles(string query, int maxResults)
        {
            return SplitStrings((buffer, cap) => vx_index_find_files(query, (uint)maxResults, buffer, cap));
        }

        /// <summary>Indexes the engine source of the Unreal project containing <paramref name="path"/>.</summary>
        public static bool IndexAddEngineOf(string path) => vx_index_add_engine_of(path) != 0;

        /// <summary>A place to navigate to.</summary>
        public sealed class Target
        {
            public string Path;
            public int Line;
            public int Column;
            public string Label;
        }

        /// <summary>
        /// Definition(s) of the symbol at a 1-based line and UTF-16 column of the file as last parsed
        /// (or, without a parse, of the word there via the symbol index). Background thread.
        /// </summary>
        public static Target[] GoToDefinition(string path, int line, int column)
        {
            var targets = new List<Target>();
            foreach (string record in SplitStrings((buffer, cap) => vx_goto_definition(path, (uint)line, (uint)column, buffer, cap)))
            {
                string[] f = record.Split('\t');
                if (f.Length >= 4)
                    targets.Add(new Target { Path = f[0], Line = int.Parse(f[1]), Column = int.Parse(f[2]), Label = f[3] });
            }
            return targets.ToArray();
        }

        public static void SymbolsStart() => vx_symbols_start();

        public static void SymbolStatus(out int files, out int pending, out int symbols)
        {
            vx_symbols_status(out uint f, out uint p, out uint s);
            files = (int)f;
            pending = (int)p;
            symbols = (int)s;
        }

        /// <summary>Fuzzy symbol search, best first. Never waits for indexing.</summary>
        public static SymbolInfo[] FindSymbols(string query, int maxResults)
        {
            var result = new List<SymbolInfo>();
            foreach (string record in SplitStrings((buffer, cap) => vx_symbols_find(query, (uint)maxResults, buffer, cap)))
            {
                string[] f = record.Split('\t');
                if (f.Length < 7)
                    continue;
                result.Add(new SymbolInfo
                {
                    Kind = int.Parse(f[0]),
                    Definition = f[1] == "1",
                    Line = int.Parse(f[2]),
                    Column = int.Parse(f[3]),
                    Name = f[4],
                    Scope = f[5],
                    Path = f[6],
                });
            }
            return result.ToArray();
        }

        // Calls an engine function that writes NUL-terminated UTF-16 strings, growing the buffer as needed.
        private static string[] SplitStrings(Func<char[], uint, uint> call)
        {
            uint needed = call(null, 0);
            if (needed == 0)
                return Array.Empty<string>();
            var buffer = new char[needed];
            needed = call(buffer, (uint)buffer.Length);
            return new string(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length))
                .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_counterparts(string path, [Out] char[] output, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void vx_index_add_root(string dir);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_index_add_project_of(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint vx_index_file_count();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_index_find_files(string query, uint maxResults, [Out] char[] output, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_index_add_engine_of(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_goto_definition(string path, uint line, uint column, [Out] char[] output, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vx_symbols_start();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vx_symbols_status(out uint files, out uint pending, out uint symbols);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_symbols_find(string query, uint maxResults, [Out] char[] output, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint vx_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_classify(string text, uint len, [Out] VxSpan[] spans, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int vx_parse(string path, IntPtr args, uint argc, string compdbDir, string unsaved, uint unsavedLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_discover_args(string path, [Out] char[] output, uint cap, out uint source);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void vx_release(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int vx_prepare_module_pch(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vx_configure(ref VxConfig config);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong vx_clear_pch_cache();

        [StructLayout(LayoutKind.Sequential)]
        private struct VxConfig
        {
            public uint SkipHeaderBodies;
            public uint SharedPch;
            public uint PchCacheMb;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_semantic_tokens(string path, [Out] VxSpan[] spans, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_macro_info(string path, uint line, uint column, [Out] char[] output, uint cap);
    }
}
