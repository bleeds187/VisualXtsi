namespace VisualXtsi
{
    /// <summary>Stands in for the extension's log in the tests.</summary>
    internal static class Log
    {
        public static void Write(string message) => System.Console.WriteLine("log: " + message);
    }
}