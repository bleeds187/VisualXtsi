using System;
using System.Linq;
using VisualXtsi.Refactoring;

namespace VisualXtsi.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static void Expect(bool condition, string what)
        {
            if (condition)
                return;
            _failures++;
            Console.WriteLine("FAIL " + what);
        }

        private static void ExpectText(string actual, string expected, string what)
        {
            if (actual == expected)
                return;
            _failures++;
            Console.WriteLine($"FAIL {what}\n  expected: {Show(expected)}\n  actual:   {Show(actual)}");
        }

        private static string Show(string s) => s.Replace("\t", "\\t").Replace("\n", "\\n");

        private static Snippet Named(string name) => Snippets.BuiltIn.First(s => s.Name == name);

        private static int Main()
        {
            SnippetTests();
            Console.WriteLine(_failures == 0 ? "all tests passed" : $"{_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        private static void SnippetTests()
        {
            // Block snippets re-indent the selected lines one level deeper, keeping their own structure.
            Expansion e = Snippets.Expand(Named("if (...) { ... }"), "\tFoo();\n\tif (x)\n\t\tBar();", "\t", "\t", "A.cpp");
            ExpectText(e.Text, "\tif (condition)\n\t{\n\t\tFoo();\n\t\tif (x)\n\t\t\tBar();\n\t}", "if: selection indented one level");
            Expect(e.Fields.Count == 1 && e.Fields[0].Name == "condition" && e.Text.Substring(e.Fields[0].Start, e.Fields[0].Length) == "condition",
                   "if: the condition is a field");
            Expect(e.End == e.Text.Length, "if: the caret ends after the snippet");

            // Preprocessor lines stay at column 0; same-named fields are reported each time.
            e = Snippets.Expand(Named("#ifdef ... #endif"), "    int X = 0;", "    ", "    ", "A.cpp");
            ExpectText(e.Text, "#ifdef symbol\n    int X = 0;\n#endif // symbol", "#ifdef: directives at column 0, body unchanged");
            Expect(e.Fields.Count == 2 && e.Fields.All(f => f.Name == "symbol"), "#ifdef: both symbol fields");

            // Spaces for indentation, CRLF line breaks.
            e = Snippets.Expand(Named("{ ... }"), "  A();", "  ", "    ", "A.cpp", "\r\n");
            ExpectText(e.Text, "  {\r\n      A();\r\n  }", "braces: spaces and CRLF");

            // Nothing selected: an indented empty line for the caret.
            e = Snippets.Expand(Named("while (...) { ... }"), "", "\t", "\t", "A.cpp");
            ExpectText(e.Text, "\twhile (condition)\n\t{\n\t\t\n\t}", "while: empty body");
            Expect(e.End == e.Text.Length, "while: caret after the snippet");

            // Inline snippets wrap the selection in place.
            e = Snippets.Expand(Named("TEXT(...)"), "\"Hello\"", "", "\t", "A.cpp");
            ExpectText(e.Text, "TEXT(\"Hello\")", "TEXT: inline");
            Expect(e.End == e.Text.Length, "TEXT: caret after the closing parenthesis");

            // $end$ inside the snippet, $guard$ from the file name, $$ for a dollar sign.
            e = Snippets.Expand(Named("try { ... } catch (...) { }"), "Run();", "", "\t", "A.cpp");
            Expect(e.Text.Substring(e.End).StartsWith("\n}") && e.Text.Substring(0, e.End).EndsWith("{\n\t"), "try: caret in the catch block");
            e = Snippets.Expand(Named("Include guard"), "struct FThing {};", "", "\t", "My Thing-2.h");
            Expect(e.Text.StartsWith("#ifndef MY_THING_2_H\n#define MY_THING_2_H\n\nstruct FThing {};\n\n#endif // MY_THING_2_H"), "include guard from the file name");
            e = Snippets.Expand(new Snippet { Name = "x", Text = "a$$b $selected$" }, "c", "", "\t", "A.cpp");
            ExpectText(e.Text, "a$b c", "$$ is a dollar sign");

            // Languages.
            Expect(Snippets.BuiltIn.Where(s => s.AppliesTo(Snippets.CSharp)).Any(s => s.Name.StartsWith("lock")), "C# has lock");
            Expect(!Snippets.BuiltIn.Where(s => s.AppliesTo(Snippets.CSharp)).Any(s => s.Name.Contains("WITH_EDITOR")), "C# has no Unreal snippets");
            Expect(Snippets.BuiltIn.Where(s => s.AppliesTo(Snippets.Cpp)).Any(s => s.Name.Contains("WITH_EDITOR")), "C++ has Unreal snippets");

            // The user file: a round trip of the built-ins, and errors with messages.
            var back = Snippets.FromJson(Snippets.ToJson(Snippets.BuiltIn));
            Expect(back.Count == Snippets.BuiltIn.Count && back.Zip(Snippets.BuiltIn, (a, b) => a.Name == b.Name && a.Text == b.Text &&
                       a.Languages.SequenceEqual(b.Languages)).All(x => x), "snippets round-trip through JSON");
            foreach (string bad in new[] { "", "[]", "{\"snippets\":[{\"name\":\"x\"}]}", "{\"schema\":9,\"snippets\":[]}", "{\"snippets\":5}" })
            {
                try
                {
                    Snippets.FromJson(bad);
                    Expect(false, $"bad snippets file rejected: {bad}");
                }
                catch (FormatException ex)
                {
                    Expect(ex.Message.Length > 0, $"bad snippets file has a message: {bad}");
                }
            }
        }
    }
}