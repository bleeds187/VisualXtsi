using System;

namespace VisualXtsi.Commands
{
    /// <summary>Command set and ids; must match VisualXtsi.vsct.</summary>
    internal static class CommandIds
    {
        public static readonly Guid CommandSet = new Guid("3d8c5e21-6a4f-4b7e-9c12-7f0a8e3b5d64");

        public const int ToggleHeaderSource = 0x0100;
        public const int OpenFileInSolution = 0x0101;
        public const int FindSymbolInSolution = 0x0102;
        public const int GoToDefinition = 0x0103;
        public const int ShowOptions = 0x0104;
        public const int RebuildSymbolIndex = 0x0105;
        public const int ClearPchCache = 0x0106;
        public const int OpenLog = 0x0107;
        public const int About = 0x0108;
        public const int KeyboardShortcuts = 0x0109;
        public const int ToggleEnabled = 0x010A;

        // Refactor and Surround With (0x0200...)
        public const int EditSnippets = 0x020D;
        public const int SurroundWithAtCaret = 0x020E;
        public const int SurroundSnippetFirst = 0x0400; // a dynamic list of SurroundSnippetSlots items
        public const int SurroundSnippetSlots = 128;

        // Menus
        public const int SurroundMenu = 0x2003;
    }
}
