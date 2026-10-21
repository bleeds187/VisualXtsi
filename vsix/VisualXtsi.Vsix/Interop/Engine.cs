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

    [StructLayout(LayoutKind.Sequential)]
    internal struct VxSpan
    {
        public uint Start;
        public uint Length;
        public VxKind Kind;
    }

    /// <summary>P/Invoke surface of the native engine (vxengine.dll).</summary>
    internal static class Engine
    {
        private const string Dll = "vxengine.dll";
        private const uint ExpectedAbiVersion = 1;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint vx_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint vx_classify(string text, uint len, [Out] VxSpan[] spans, uint cap);
    }
}
