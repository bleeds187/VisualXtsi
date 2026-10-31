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
            RenameTests();
            IncludeTests();
            ShapeTests();
            Console.WriteLine(_failures == 0 ? "all tests passed" : $"{_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        private static void ShapeTests()
        {
            // Document Method.
            CursorInfo f = CursorInfo.Parse("kind\tmethod\nname\tRun\nresult\tbool\nparam\tint32 Count\tCount\nparam\tconst FString& Name\tName\tTEXT(\"x\")");
            var (doc, caret) = CodeShapes.DocComment(f, 0, "\t", "\n");
            ExpectText(doc, "\t/**\n\t * \n\t *\n\t * @param Count\n\t * @param Name\n\t * @return\n\t */\n", "doc comment, Unreal style");
            Expect(doc.Substring(0, caret).EndsWith("/**\n\t * "), "caret on the summary line");
            CursorInfo v = CursorInfo.Parse("kind\tmethod\nname\tTick\nresult\tvoid");
            ExpectText(CodeShapes.DocComment(v, 1, "", "\r\n").Text, "/// \r\n", "/// style, nothing to list");

            // Encapsulate Field.
            var (acc, getter, setter) = CodeShapes.Accessors("bEnabled", "bool", true, false, null, null, "\t", "\n");
            ExpectText(acc, "\tbool IsEnabled() const { return bEnabled; }\n\tvoid SetEnabled(bool bInEnabled) { bEnabled = bInEnabled; }\n", "bool accessors");
            Expect(getter == "IsEnabled" && setter == "SetEnabled", "bool accessor names");
            (acc, _, _) = CodeShapes.Accessors("Names", "TArray<FString>", false, false, "Gadget", null, "    ", "\n");
            ExpectText(acc, "    UFUNCTION(BlueprintPure, Category = \"Gadget\")\n    const TArray<FString>& GetNames() const { return Names; }\n" +
                            "    UFUNCTION(BlueprintCallable, Category = \"Gadget\")\n    void SetNames(const TArray<FString>& InNames) { Names = InNames; }\n",
                       "class-type accessors by reference, Blueprint style");
            (acc, _, _) = CodeShapes.Accessors("m_count", "int", true, true, null, new[] { "GetCount" }, "", "\n");
            ExpectText(acc, "static void SetCount(int InCount) { m_count = InCount; }\n", "static, m_ prefix, existing getter left out");

            // Remove Braces.
            string text = "\tif (X > 0)\n\t{\n\t\tX = 1;\n\t}\n\telse\n\t\tX = 2;\n";
            int open = text.IndexOf('{'), close = text.IndexOf('}');
            int inner = text.IndexOf("X = 1"), innerEnd = inner + "X = 1".Length;
            var (s, l, r) = CodeShapes.RemoveBraces(text, open, close, inner, innerEnd, "\t", "\t", "\n");
            ExpectText(text.Remove(s, l).Insert(s, r), "\tif (X > 0)\n\t\tX = 1;\n\telse\n\t\tX = 2;\n", "remove braces of a one-statement block");
            text = "\tif (X) {\n\t\tA();\n\t} else {\n\t\tB();\n\t}\n";
            (s, l, r) = CodeShapes.RemoveBraces(text, text.IndexOf('{'), text.IndexOf('}'), text.IndexOf("A()"), text.IndexOf("A()") + 3, "\t", "\t", "\n");
            ExpectText(text.Remove(s, l).Insert(s, r), "\tif (X)\n\t\tA();\n\telse {\n\t\tB();\n\t}\n", "remove K&R braces, else to its own line");

            // Add Braces.
            text = "\tfor (int I = 0; I < 3; ++I)\n\t\tX += I;\n\treturn X;\n";
            int body = text.IndexOf("X += I"), bodyEnd = body + "X += I".Length;
            (s, l, r) = CodeShapes.AddBraces(text, body, bodyEnd, "\t", "\t", "\n");
            ExpectText(text.Remove(s, l).Insert(s, r), "\tfor (int I = 0; I < 3; ++I)\n\t{\n\t\tX += I;\n\t}\n\treturn X;\n", "add braces around a loop body");
            text = "    if (X) A(); else B();\n";
            (s, l, r) = CodeShapes.AddBraces(text, text.IndexOf("A()"), text.IndexOf("A()") + 3, "    ", "    ", "\n");
            ExpectText(text.Remove(s, l).Insert(s, r), "    if (X)\n    {\n        A();\n    }\n    else B();\n", "add braces on one line, else to its own line");
        }

        private static void IncludeTests()
        {
            // Include paths by Unreal module layout.
            string engine = @"C:\UE\Engine\Source\Runtime\Engine";
            ExpectText(IncludeSupport.IncludePath(engine + @"\Classes\GameFramework\Actor.h", engine, @"C:\P\Source\Game\Private\A.cpp", @"C:\P\Source\Game"),
                       "GameFramework/Actor.h", "engine header relative to Classes");
            ExpectText(IncludeSupport.IncludePath(@"C:\P\Source\Game\Public\Items\Item.h", @"C:\P\Source\Game", @"C:\P\Source\Other\Private\B.cpp", @"C:\P\Source\Other"),
                       "Items/Item.h", "another module's Public header");
            ExpectText(IncludeSupport.IncludePath(@"C:\P\Source\Game\Private\Internal.h", @"C:\P\Source\Game", @"C:\P\Source\Game\Private\Sub\C.cpp", @"C:\P\Source\Game"),
                       "Internal.h", "own module's Private header");
            ExpectText(IncludeSupport.IncludePath(@"C:\P\Source\Game\GameModule.h", @"C:\P\Source\Game", @"C:\P\Source\Game\Private\D.cpp", @"C:\P\Source\Game"),
                       "GameModule.h", "a header in the module folder");
            ExpectText(IncludeSupport.IncludePath(@"C:\Tool\src\util\Text.h", null, @"C:\Tool\src\main.cpp", null), "util/Text.h", "no module: relative");
            ExpectText(IncludeSupport.IncludePath(@"C:\Tool\inc\Api.h", null, @"C:\Tool\src\main.cpp", null), "../inc/Api.h", "no module: up and over");

            // A source file: its own header first, then sorted, generated.h last (in a header).
            string cpp = "// Copyright\n#include \"Thing.h\"\n\n#include \"Engine/World.h\"\n#include \"Kismet/GameplayStatics.h\"\n\n" +
                         "#include UE_INLINE_GENERATED_CPP_BY_NAME(Thing)\n\nvoid F() {}\n";
            var at = IncludeSupport.IncludeInsertion(cpp, "GameFramework/Actor.h", "Thing.cpp");
            Expect(at.HasValue && cpp.Insert(at.Value.Offset, at.Value.Text).Contains("#include \"Engine/World.h\"\n#include \"GameFramework/Actor.h\"\n#include \"Kismet/GameplayStatics.h\""),
                   "sorted among the last group");
            at = IncludeSupport.IncludeInsertion(cpp, "AAA/First.h", "Thing.cpp");
            Expect(at.HasValue && cpp.Insert(at.Value.Offset, at.Value.Text).Contains("\n\n#include \"AAA/First.h\"\n#include \"Engine/World.h\""), "first of its group");
            Expect(!IncludeSupport.IncludeInsertion(cpp, "World.h", "Thing.cpp").HasValue && !IncludeSupport.IncludeInsertion(cpp, "Engine/World.h", "Thing.cpp").HasValue,
                   "already included");
            string h = "#pragma once\n\n#include \"CoreMinimal.h\"\n#include \"GameFramework/Actor.h\"\n#include \"Thing.generated.h\"\n\nUCLASS()\n";
            at = IncludeSupport.IncludeInsertion(h, "Zoo/Last.h", "Thing.h");
            Expect(at.HasValue && h.Insert(at.Value.Offset, at.Value.Text).Contains("#include \"Zoo/Last.h\"\n#include \"Thing.generated.h\""), "generated.h stays last");
            at = IncludeSupport.IncludeInsertion("#pragma once\n\nstruct X {};\n", "A.h", "X.h");
            Expect(at.HasValue && "#pragma once\n\nstruct X {};\n".Insert(at.Value.Offset, at.Value.Text).StartsWith("#pragma once\n#include \"A.h\"\n"), "no includes: after #pragma once");

            // Forward declarations.
            ExpectText(IncludeSupport.ForwardDeclaration("class", "UThing", "", false, null), "class UThing;", "class");
            ExpectText(IncludeSupport.ForwardDeclaration("enum", "EMode", "", true, "uint8"), "enum class EMode : uint8;", "scoped enum with its base");
            ExpectText(IncludeSupport.ForwardDeclaration("struct", "FPart", "Toys", false, null), "namespace Toys { struct FPart; }", "in a namespace");
            string withForward = "#pragma once\n#include \"A.h\"\n\nclass UAlpha;\nclass UGamma;\n\nUCLASS()\n";
            var f = IncludeSupport.ForwardInsertion(withForward, "class UBeta;");
            Expect(f.HasValue && withForward.Insert(f.Value.Offset, f.Value.Text).Contains("class UAlpha;\nclass UBeta;\nclass UGamma;"), "sorted among the forward declarations");
            Expect(!IncludeSupport.ForwardInsertion(withForward, "class UAlpha;").HasValue, "already declared");
            Expect(!IncludeSupport.ForwardInsertion(withForward.Replace("\n", "\r\n"), "class UAlpha;").HasValue, "already declared (CRLF)");
            string lyraLike = "#pragma once\n\n#include \"A.h\"\n\n#include \"X.generated.h\"\n\n#define UE_API GAME_API\n\nclass AActor;\nclass UClass;\nstruct FFrame;\nenum class EMode : uint8;\n\nUCLASS()\n";
            f = IncludeSupport.ForwardInsertion(lyraLike, "class APawn;");
            Expect(f.HasValue && lyraLike.Insert(f.Value.Offset, f.Value.Text).Contains("class AActor;\nclass APawn;\nclass UClass;"), "the block after a #define, classes by name");
            f = IncludeSupport.ForwardInsertion(lyraLike, "struct FPart;");
            Expect(f.HasValue && lyraLike.Insert(f.Value.Offset, f.Value.Text).Contains("struct FFrame;\nstruct FPart;\nenum class EMode"), "structs after classes, before enums");
            string none = "#pragma once\n#include \"A.h\"\n\nUCLASS()\n";
            f = IncludeSupport.ForwardInsertion(none, "struct FPart;");
            Expect(f.HasValue && none.Insert(f.Value.Offset, f.Value.Text).StartsWith("#pragma once\n#include \"A.h\"\n\nstruct FPart;\n"), "after the includes");
        }

        private static void RenameTests()
        {
            Expect(RenameSupport.Validate("NewName", "Old") == null, "a valid name");
            Expect(RenameSupport.Validate("Old", "Old") != null && RenameSupport.Validate("1x", "Old") != null &&
                   RenameSupport.Validate("a-b", "Old") != null && RenameSupport.Validate("class", "Old") != null &&
                   RenameSupport.Validate(" ", "Old") != null, "invalid names have reasons");

            string header = "UCLASS(Blueprintable)\nclass GAME_API UThing : public UObject\n{\n\tGENERATED_BODY()\n\n\t/** The speed. */\n" +
                            "\tUPROPERTY(EditAnywhere,\n\t\tCategory = \"Move\")\n\tfloat Speed;\n\n\tvoid Plain();\n\tUFUNCTION() void Inline();\n};";
            Expect(RenameSupport.ReflectionMacro(header, header.IndexOf("class GAME_API")) == "UCLASS", "UCLASS above a class");
            Expect(RenameSupport.ReflectionMacro(header, header.IndexOf("float Speed")) == "UPROPERTY", "a multi-line UPROPERTY above a field");
            Expect(RenameSupport.ReflectionMacro(header, header.IndexOf("void Plain")) == null, "no macro above a plain method");
            Expect(RenameSupport.ReflectionMacro(header, header.IndexOf("void Inline")) == "UFUNCTION", "UFUNCTION on the same line");

            ExpectText(RenameSupport.CoreRedirect("UCLASS", "Game", null, "UThing", "UGizmo"),
                       "+ClassRedirects=(OldName=\"/Script/Game.Thing\",NewName=\"/Script/Game.Gizmo\")", "class redirect without the prefix");
            ExpectText(RenameSupport.CoreRedirect("USTRUCT", "Game", null, "FPart", "FPiece"),
                       "+StructRedirects=(OldName=\"/Script/Game.Part\",NewName=\"/Script/Game.Piece\")", "struct redirect");
            ExpectText(RenameSupport.CoreRedirect("UENUM", "Game", null, "EMode", "EState"),
                       "+EnumRedirects=(OldName=\"/Script/Game.EMode\",NewName=\"/Script/Game.EState\")", "enum redirect keeps E");
            ExpectText(RenameSupport.CoreRedirect("UPROPERTY", "Game", "AThing", "Speed", "Velocity"),
                       "+PropertyRedirects=(OldName=\"/Script/Game.Thing.Speed\",NewName=\"Velocity\")", "property redirect");
            ExpectText(RenameSupport.CoreRedirect("UFUNCTION", "Game", "UThing", "Run", "Go"),
                       "+FunctionRedirects=(OldName=\"/Script/Game.Thing.Run\",NewName=\"Go\")", "function redirect");
            Expect(RenameSupport.ObjectName("Usage") == "Usage" && RenameSupport.ObjectName("UThing") == "Thing", "prefix only before a capital");

            string ini = "[/Script/Engine.Engine]\r\nX=1\r\n\r\n[CoreRedirects]\r\n+ClassRedirects=(A)\r\n\r\n[Other]\r\nY=2\r\n";
            var (at, text) = RenameSupport.InsertRedirect(ini, "+B");
            ExpectText(ini.Insert(at, text), "[/Script/Engine.Engine]\r\nX=1\r\n\r\n[CoreRedirects]\r\n+ClassRedirects=(A)\r\n+B\r\n\r\n[Other]\r\nY=2\r\n",
                       "redirect appended to the [CoreRedirects] section");
            (at, text) = RenameSupport.InsertRedirect("[A]\nX=1\n", "+B");
            ExpectText("[A]\nX=1\n".Insert(at, text), "[A]\nX=1\n\n[CoreRedirects]\n+B\n", "a new [CoreRedirects] section");
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
            Expect(TextScan.IndentUnit("a\n    b\n        c\n    d\n", "\t") == "    ", "indent unit: four spaces");
            Expect(TextScan.IndentUnit("a\n  b\n    c\n", "\t") == "  ", "indent unit: two spaces");
            Expect(TextScan.IndentUnit("a\n\tb\n\t\tc\n    d\n", "    ") == "\t", "indent unit: tabs win");
            Expect(TextScan.IndentUnit("a\nb\n", "\t") == "\t", "indent unit: nothing indented, the fallback");
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