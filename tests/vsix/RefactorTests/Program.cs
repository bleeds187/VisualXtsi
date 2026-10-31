using System;
using System.Linq;
using VisualXtsi.Interop;
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
            CodeGenTests();
            TextScanTests();
            Console.WriteLine(_failures == 0 ? "all tests passed" : $"{_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        private static void CodeGenTests()
        {
            // As the engine describes UGadget::GetSettings (a BlueprintNativeEvent here, WithValidation too).
            CursorInfo f = CursorInfo.Parse(string.Join("\n",
                "kind\tmethod", "name\tGetSettings", "scope\tGame::UGadget", "classes\tUGadget", "namespaces\tGame",
                "result\tUGadget::FSettings", "param\tint32 Index\tIndex", "param\tconst FString& Name\tName", "qualifiers\tconst",
                "virtual\t1", "ue\t3", "decl\tC:/P/Gadget.h\t27\t23\t27\t80", "class\tC:/P/Gadget.h\t15\t1\t41\t2"));
            Expect(f != null && f.IsFunction && f.IsMember && f.Parameters.Count == 2 && f.Parameters[1].Name == "Name" &&
                   f.Declaration.Path == @"C:\P\Gadget.h" && f.Declaration.Line == 27 && f.Class.EndLine == 41, "cursor info parsed");
            ExpectText(CodeGen.Qualifier(f, false), "UGadget::", "qualifier: classes");
            ExpectText(CodeGen.Qualifier(f, true), "Game::UGadget::", "qualifier: with namespaces");
            var defs = CodeGen.Definitions(f, "UGadget::", "\t", "\n");
            Expect(defs.Count == 2, "BlueprintNativeEvent + WithValidation: two definitions");
            ExpectText(defs[0].Text, "UGadget::FSettings UGadget::GetSettings_Implementation(int32 Index, const FString& Name) const\n{\n}", "_Implementation stub");
            ExpectText(defs[1].Text, "bool UGadget::GetSettings_Validate(int32 Index, const FString& Name)\n{\n\treturn true;\n}", "_Validate stub");
            CursorInfo ptr = CursorInfo.Parse("kind\tfunction\nname\tMake\nresult\tUGadget*\nparam\tint32& Count\tCount");
            ExpectText(CodeGen.Definitions(ptr, "", "\t", "\r\n")[0].Text, "UGadget* Make(int32& Count)\r\n{\r\n}", "pointer result, CRLF");
            CursorInfo ctor = CursorInfo.Parse("kind\tconstructor\nname\tUGadget\nclasses\tUGadget");
            ExpectText(CodeGen.Signature(ctor, ctor.Name, "UGadget::"), "UGadget::UGadget()", "constructor: no result");
            ExpectText(CodeGen.Declaration(ptr), "UGadget* Make(int32& Count);", "declaration");
            Expect(CursorInfo.Parse("") == null && CursorInfo.Parse("name\tX") == null, "no kind, no info");

            // Default arguments: kept apart from the parameter, written in declarations, taken out of definitions.
            CursorInfo free = CursorInfo.Parse("kind\tfunction\nname\tFreeHelper\nresult\tvoid\nparam\tint A\tA\nparam\tint B\tB\t2\nparam\tconst FString& S\tS\tTEXT(\"a b\")");
            Expect(free.Parameters[1].Default == "2" && free.Parameters[0].Default == null, "defaults parsed");
            ExpectText(CodeGen.Declaration(free), "void FreeHelper(int A, int B = 2, const FString& S = TEXT(\"a b\"));", "declaration with defaults");
            ExpectText(CodeGen.Signature(free, free.Name, ""), "void FreeHelper(int A, int B, const FString& S)", "definition signature without defaults");
            ExpectText(CodeGen.WithoutDefaults("(int A, int B =  2,\n\tconst FString& S = TEXT(\"a b\"))", free), "(int A, int B,\n\tconst FString& S)",
                       "defaults removed from a definition, whatever the spacing");
            Expect(CodeGen.WithoutDefaults("(int A)", ptr) == null, "nothing to remove");
        }

        private static void TextScanTests()
        {
            const string text = "line1\r\n\tline2 { \"}\" // }\r\n\t/* } */ x = R\"(})\"; '}'\r\n\t}\r\nafter";
            Expect(TextScan.Offset(text, 2, 2) == 8 && text[8] == 'l', "offset of line 2 column 2");
            Expect(TextScan.LineOf(text, 8) == 2, "line of an offset");
            int end = TextScan.BlockEnd(text, 0);
            Expect(end > 0 && text.Substring(end - 1, 1) == "}" && TextScan.LineOf(text, end) == 4, "block end skips strings, comments, raw strings, chars");
            Expect(TextScan.Newline(text) == "\r\n" && TextScan.Newline("a\nb") == "\n", "newline style");
            Expect(TextScan.IndentOf(text, 9) == "\t", "indentation of a line");

            ExpectText(TextScan.Reindent("{\n\t\treturn Count;\n\t}", "", "\n"), "{\n\treturn Count;\n}", "reindent a member body to column 0");
            ExpectText(TextScan.Reindent("{\n\tA();\n\n\tif (x)\n\t\tB();\n}", "\t", "\r\n"), "\t{\r\n\t\tA();\r\n\r\n\t\tif (x)\r\n\t\t\tB();\r\n\t}", "reindent into a class");

            string cls = "class UThing\n{\npublic:\n\tvoid A();\nprivate:\n\tint B;\n};\n";
            var (at, needsPublic) = TextScan.MemberInsertion(cls, 0, cls.IndexOf("};") + 1, false);
            Expect(at == cls.IndexOf("};") && needsPublic, "member insertion: before }; and after private: needs public:");
            string st = "struct FThing\n{\n\tint A;\n};";
            Expect(!TextScan.MemberInsertion(st, 0, st.Length - 1, true).NeedsPublic, "a struct without labels is public");
            string pub = "class X\n{\nprivate:\n\tint A;\npublic:\n\tvoid F();\n};";
            Expect(!TextScan.MemberInsertion(pub, 0, pub.Length - 1, false).NeedsPublic, "last label public");
            var (offset, prefix) = TextScan.AppendPoint("a\n\n\n", "\n");
            Expect(offset == 1 && prefix == "\n\n", "append after the last text with a blank line");
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