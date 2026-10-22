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
    }
}
