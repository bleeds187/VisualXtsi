using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VisualXtsi.Interop
{
    /// <summary>Kinds returned by vx_classify; mirrors vx_kind in engine/include/vx/vx.h.</summary>
    internal enum VxKind : uint
    {
        Preprocessor = 1,
        Macro = 2,
    }

    /// <summary>Mirrors vx_status.</summary>
    internal enum VxStatus
    {
        Ok = 0,
        BadArguments = 1,
        NotAvailable = 2,
        ParseFailed = 3,
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
        private const uint ExpectedAbiVersion = 3;

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
        private static extern uint vx_macro_info(string path, uint line, uint column, [Out] char[] output, uint cap);
    }
}
