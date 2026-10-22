using System;
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

    /// <summary>P/Invoke surface of the native engine (vxengine.dll).</summary>
    internal static class Engine
    {
        private const string Dll = "vxengine.dll";
        private const uint ExpectedAbiVersion = 5;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

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

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_semantic_tokens(string path, [Out] VxSpan[] spans, uint cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_macro_info(string path, uint line, uint column, [Out] char[] output, uint cap);
    }
}
