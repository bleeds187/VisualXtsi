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
            EnumTests();
            MemberTests();
            ExtractTests();
            SignatureTests();
            Console.WriteLine(_failures == 0 ? "all tests passed" : $"{_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        // A switch's shape from its text: the labels found by their text, values from `values`.
        private static SwitchShape Switch(string text, string qualifier, (string Name, long Value)[] enumerators, params (string Label, long Value)[] cases)
        {
            var s = new SwitchShape
            {
                SwitchStart = text.IndexOf("switch"),
                EnumName = "EColor",
                Qualifier = qualifier,
            };
            s.BodyOpen = text.IndexOf('{', s.SwitchStart);
            for (int i = s.BodyOpen, depth = 0; i < text.Length; i++)
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    s.BodyClose = i;
                    break;
                }
            foreach (var e in enumerators)
                s.Enumerators.Add((e.Name, e.Value, 0));
            foreach (var c in cases)
                s.Cases.Add((c.Value, text.IndexOf("case " + c.Label), c.Label));
            s.Default = text.IndexOf("default");
            return s;
        }

        private static void EnumTests()
        {
            var colors = new[] { ("Red", 0L), ("Green", 1L), ("Blue", 2L), ("Crimson", 0L), ("EColor_MAX", 3L) };
            var s = Switch("void F(EColor C)\n{\n    switch (C)\n    {\n    case EColor::Red:\n        break;\n    }\n}\n", "Ns::EColor::", colors, ("EColor::Red", 0));
            var missing = EnumSupport.Missing(s, skipSentinels: true);
            Expect(missing.SequenceEqual(new[] { "Green", "Blue" }), "missing: by value (an alias is covered), the sentinel left out");
            Expect(EnumSupport.Missing(s, skipSentinels: false).Last() == "EColor_MAX", "missing: the sentinel when asked");
            string text = "void F(EColor C)\n{\n    switch (C)\n    {\n    case EColor::Red:\n        break;\n    }\n}\n";
            var (at, insert) = EnumSupport.Insertion(text, s, missing, "    ");
            ExpectText(text.Insert(at, insert),
                       "void F(EColor C)\n{\n    switch (C)\n    {\n    case EColor::Red:\n        break;\n    case EColor::Green:\n        break;\n    case EColor::Blue:\n        break;\n    }\n}\n",
                       "cases at the end, written like the existing one");

            // A default the previous case cannot fall into: the cases go before it.
            text = "switch (C)\n{\n\tcase Red: return 1;\n\tdefault:\n\t\treturn 0;\n}\n";
            s = Switch(text, "", colors, ("Red", 0));
            (at, insert) = EnumSupport.Insertion(text, s, new[] { "Green" }, "\t");
            ExpectText(text.Insert(at, insert), "switch (C)\n{\n\tcase Red: return 1;\n\tcase Green:\n\t\tbreak;\n\tdefault:\n\t\treturn 0;\n}\n", "cases before default");
            text = "switch (C)\n{\ncase Red:\n\tbreak;\n\ndefault:\n\tbreak;\n}\n";
            s = Switch(text, "", colors, ("Red", 0));
            (at, insert) = EnumSupport.Insertion(text, s, new[] { "Green" }, "\t");
            ExpectText(text.Insert(at, insert), "switch (C)\n{\ncase Red:\n\tbreak;\n\ncase Green:\n\tbreak;\n\ndefault:\n\tbreak;\n}\n", "a blank line between sections kept");

            // A case running on into default: the new cases go after it all.
            text = "switch (C)\n{\ncase Red:\n    Log();\ndefault:\n    break;\n}\n";
            s = Switch(text, "", colors, ("Red", 0));
            (at, insert) = EnumSupport.Insertion(text, s, new[] { "Blue" }, "    ");
            ExpectText(text.Insert(at, insert), "switch (C)\n{\ncase Red:\n    Log();\ndefault:\n    break;\ncase Blue:\n    break;\n}\n", "fall-through into default kept");

            // An empty switch on one line, with the qualifier the engine gave.
            text = "    switch (C) {}\n";
            s = Switch(text, "EColor::", colors);
            (at, insert) = EnumSupport.Insertion(text, s, new[] { "Red" }, "    ");
            ExpectText(text.Insert(at, insert), "    switch (C) {\n    case EColor::Red:\n        break;\n    }\n", "an empty one-line switch");

            // Convert to Scoped Enum.
            string Apply(string t, System.Collections.Generic.IEnumerable<TextEdit> edits)
            {
                foreach (TextEdit e in EnumSupport.Fold(t, edits).OrderByDescending(e => e.Start))
                    t = t.Substring(0, e.Start) + e.Text + t.Substring(e.Start + e.Length);
                return t;
            }
            text = "UENUM(BlueprintType)\nenum EMode\n{\n    Off UMETA(Hidden),\n    On,\n};\n";
            int name = text.IndexOf("EMode");
            ExpectText(Apply(text, new[] { EnumSupport.ScopedDeclaration(text, name, "EMode", "uint8") }),
                       "UENUM(BlueprintType)\nenum class EMode : uint8\n{\n    Off UMETA(Hidden),\n    On,\n};\n", "scoped, a base chosen");
            text = "enum EMode : int32 { A };\nenum EMode : int32;";
            ExpectText(Apply(text, new[] { EnumSupport.ScopedDeclaration(text, text.IndexOf("EMode"), "EMode", EnumSupport.DeclaredBase(text, text.IndexOf("EMode"), "EMode")) }),
                       "enum class EMode : int32 { A };\nenum EMode : int32;", "scoped, its base kept");
            ExpectText(Apply(text, new[] { EnumSupport.ScopedDeclaration(text, text.LastIndexOf("EMode"), "EMode", "int32") }),
                       "enum EMode : int32 { A };\nenum class EMode : int32;", "an opaque declaration");
            Expect(EnumSupport.ScopedDeclaration("enum class EMode { A };", 11, "EMode", "") == null, "already scoped: nothing");

            text = "int F(EMode M) { if (M == On) return Off; return EMode::On + M; }";
            var edits = new[]
            {
                EnumSupport.Qualify(text, text.IndexOf("On)"), "EMode"),
                EnumSupport.Qualify(text, text.IndexOf("Off;"), "EMode"),
                EnumSupport.Cast(text, text.IndexOf("Off;"), text.IndexOf("Off;") + 3, "int"),
                EnumSupport.Qualify(text, text.IndexOf("On +"), "EMode"),
                EnumSupport.Cast(text, text.IndexOf("EMode::On"), text.IndexOf("EMode::On") + 9, "int"),
                EnumSupport.Cast(text, text.LastIndexOf("M;"), text.LastIndexOf("M;") + 1, "int"),
            };
            Expect(edits[3] == null, "qualify: already qualified");
            ExpectText(Apply(text, edits),
                       "int F(EMode M) { if (M == EMode::On) return static_cast<int>(EMode::Off); return static_cast<int>(EMode::On) + static_cast<int>(M); }",
                       "enumerators qualified, conversions cast, nested edits folded");
            text = "TEnumAsByte< EMode > Mode;";
            ExpectText(Apply(text, new[] { EnumSupport.UnwrapEnumAsByte(text, text.IndexOf("EMode"), "EMode") }), "EMode Mode;", "TEnumAsByte unwrapped");
            Expect(EnumSupport.UnwrapEnumAsByte("EMode Mode;", 0, "EMode") == null, "no TEnumAsByte: nothing");
            ExpectText(RenameSupport.EnumValueRedirect("Game", "EMode", new[] { "Off", "On" }),
                       "+EnumRedirects=(OldName=\"/Script/Game.EMode\",ValueChanges=((\"Off\",\"EMode::Off\"),(\"On\",\"EMode::On\")))", "enum value redirects");
        }

        private static string Apply(string text, System.Collections.Generic.IEnumerable<TextEdit> edits)
        {
            foreach (TextEdit e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length))
                text = text.Substring(0, e.Start) + e.Text + text.Substring(e.Start + e.Length);
            return text;
        }

        private static void SignatureTests()
        {
            string Show3((string, string, string) t) => t.Item1 + "|" + t.Item2 + "|" + t.Item3;
            ExpectText(Show3(SignatureSupport.Parse("const FString& Name = TEXT(\"x\")")), "const FString&|Name|TEXT(\"x\")", "parse: reference, default");
            ExpectText(Show3(SignatureSupport.Parse("TArray<int32>* Values")), "TArray<int32>*|Values|", "parse: pointer to a template");
            ExpectText(Show3(SignatureSupport.Parse("int32")), "int32||", "parse: unnamed");
            ExpectText(Show3(SignatureSupport.Parse("unsigned int")), "unsigned int||", "parse: a two-word type, unnamed");
            ExpectText(Show3(SignatureSupport.Parse("float Grid[4]")), "float[4]|Grid|", "parse: an array");
            Expect(SignatureSupport.Split("void").Count == 0 && SignatureSupport.Split(" ").Count == 0 && SignatureSupport.Split("A, F(B, C), D<E, G>").Count == 3, "split: lists");
            var parens = SignatureSupport.ParensAfter("x = Make<int32, float>(A, (B));", 8);
            Expect(parens.HasValue && parens.Value.Open == 22 && parens.Value.Close == 29, "parens after a name with template arguments");
            Expect(!SignatureSupport.ParensAfter("auto P = &UThing::Run;", 18).HasValue, "no call: a pointer to the function");

            // Old: Run(int32 Speed, const FString& Name = TEXT("x"), bool bLoud = false).
            // New: Run(const FString& Label, int32 Speed, float Scale = 1.f) - swapped, Name renamed to Label, bLoud removed, Scale added.
            var ps = new[]
            {
                new SignatureParameter { OldIndex = 1, OldType = "const FString&", OldName = "Name", OldDefault = "TEXT(\"x\")", Type = "const FString&", Name = "Label", Default = "TEXT(\"x\")" },
                new SignatureParameter { OldIndex = 0, OldType = "int32", OldName = "Speed", Type = "int32", Name = "Speed" },
                new SignatureParameter { Type = "float", Name = "Scale", Default = "1.f", CallValue = "" },
            };
            ExpectText(SignatureSupport.ParameterList("int32 Speed, const FString& Name = TEXT(\"x\"), bool bLoud = false", ps, withDefaults: true),
                       "const FString& Label = TEXT(\"x\"), int32 Speed, float Scale = 1.f", "declaration: reordered, renamed, removed, added with defaults");
            ExpectText(SignatureSupport.ParameterList("int32 InSpeed, const FString& InName, bool bInLoud", ps, withDefaults: false),
                       "const FString& Label, int32 InSpeed, float Scale", "definition: its own names kept unless renamed, no defaults");
            ExpectText(SignatureSupport.ArgumentList("3, Text, true", ps), "Text, 3", "call: moved, removed, new one left to its default");
            ExpectText(SignatureSupport.ArgumentList("3", ps), "TEXT(\"x\"), 3", "call: a default it relied on written out before a later argument");
            ps[2].CallValue = "Scale0";
            ExpectText(SignatureSupport.ArgumentList("3", ps), "TEXT(\"x\"), 3, Scale0", "call: a new parameter's value at calls");
            var required = new[] { new SignatureParameter { Type = "int32", Name = "Count" } };
            Expect(SignatureSupport.ArgumentList("", required) == null, "call: a new parameter without a value or default");
            ExpectText(SignatureSupport.Describe("Run", ps), "Run(const FString& Label = TEXT(\"x\"), int32 Speed, float Scale = 1.f)", "the signature as it will read");

            string Kind(string t, string name)
            {
                int at = t.IndexOf(name, StringComparison.Ordinal);
                int length = System.Text.RegularExpressions.Regex.Match(t.Substring(at), @"^\w+").Length;
                return SignatureSupport.SiteKind(t, at, SignatureSupport.ParensAfter(t, at + length).Value.Close);
            }
            ExpectText(Kind("virtual void OnHit_Implementation(float D) override;", "OnHit_Implementation"), "declaration", "site: a declaration");
            ExpectText(Kind("void AThing::OnHit_Implementation(float D)\n{\n}", "OnHit_Implementation"), "definition", "site: a qualified definition");
            ExpectText(Kind("    Super::OnHit_Implementation(D);", "OnHit_Implementation"), "call", "site: a call through Super");
            ExpectText(Kind("    return OnHit_Implementation(D);", "OnHit_Implementation"), "call", "site: a returned call");
            ExpectText(Kind("    Thing->OnHit_Implementation(D);", "OnHit_Implementation"), "call", "site: a call through a pointer");
            ExpectText(Kind("UThing::UThing(int32 A) : Super(A) {}", "UThing("), "definition", "site: a constructor with initializers");

            const string body = "{\n    Log(Name); // Name\n    Other.Name = Name + \"Name\";\n    A::Name();\n}";
            var uses = SignatureSupport.UsesIn(body, 0, body.Length, "Name");
            Expect(uses.Count == 2 && uses[0] == body.IndexOf("Name)") && uses[1] == body.IndexOf("= Name") + 2, "uses in a body: not members, comments or strings");
        }

        private static void ExtractTests()
        {
            // Introduce Variable.
            ExpectText(ExtractSupport.SuggestName("Character->GetHealthComponent()", "UHealthComponent*"), "HealthComponent", "name from a getter call");
            ExpectText(ExtractSupport.SuggestName("Owner.bIsAlive", "bool"), "bIsAlive", "a bool keeps its b prefix");
            ExpectText(ExtractSupport.SuggestName("IsValid(Actor)", "bool"), "bValid", "a bool predicate");
            ExpectText(ExtractSupport.SuggestName("A * 2.f", "float"), "Value", "an arithmetic expression");
            ExpectText(ExtractSupport.SuggestName("Values.Num()", "int32"), "Num", "a method call");
            ExpectText(ExtractSupport.VariableType("float", false), "const auto", "auto by default");
            ExpectText(ExtractSupport.VariableType("const FString &", true), "const FString&", "an explicit type, const once");
            ExpectText(ExtractSupport.VariableType("UCharacterMovementComponent *", true), "UCharacterMovementComponent* const", "a pointer stays pointing to something writable");
            const string body = "{\n    float Total = A * 2.f + 1.f;\n    for (int32 I = 0; I < Count; ++I)\n    {\n        Total += A * 2.f;\n    }\n}\n";
            int first = body.IndexOf("A * 2.f"), second = body.LastIndexOf("A * 2.f");
            var occurrences = new[] { (first, first + 7, body.IndexOf("float Total")), (second, second + 7, body.IndexOf("for (")) };
            ExpectText(Apply(body, ExtractSupport.IntroduceVariable(body, occurrences, "Twice", "const auto", "\n")),
                       "{\n    const auto Twice = A * 2.f;\n    float Total = Twice + 1.f;\n    for (int32 I = 0; I < Count; ++I)\n    {\n        Total += Twice;\n    }\n}\n",
                       "introduce variable: declared before the first statement, both occurrences replaced");
            ExpectText(Apply(body, ExtractSupport.IntroduceVariable(body, new[] { occurrences[1] }, "Twice", "const float", "\n")),
                       "{\n    float Total = A * 2.f + 1.f;\n    const float Twice = A * 2.f;\n    for (int32 I = 0; I < Count; ++I)\n    {\n        Total += Twice;\n    }\n}\n",
                       "introduce variable: only the selected one, before its statement");

            // Extract Method: parameters by how the variables are used.
            ExpectText(ExtractSupport.Parameter(new ExtractVariable { Name = "Count", Type = "int32", Shape = "value", Written = true, UsedOutside = true }), "int32& Count", "changed and needed back: a reference");
            ExpectText(ExtractSupport.Parameter(new ExtractVariable { Name = "A", Type = "const float", Shape = "value" }), "float A", "read: a copy");
            ExpectText(ExtractSupport.Parameter(new ExtractVariable { Name = "Name", Type = "FString", Shape = "class" }), "const FString& Name", "a class read: const reference");
            ExpectText(ExtractSupport.Parameter(new ExtractVariable { Name = "Values", Type = "TArray<int32> &", Shape = "ref" }), "TArray<int32>& Values", "a reference stays one");
            Expect(ExtractSupport.Parameter(new ExtractVariable { Name = "Grid", Type = "int [4]", Shape = "array" }) == null, "an array cannot be passed");
            ExpectText(ExtractSupport.OutAssignment("int32 Last = Count + 1", "Last", "int32"), "Last = Count + 1", "out: an initialized declaration");
            ExpectText(ExtractSupport.OutAssignment("FVector V(1, 2, 3)", "V", "FVector"), "V = FVector(1, 2, 3)", "out: direct initialization");
            ExpectText(ExtractSupport.OutAssignment("float Sum", "Sum", "float"), "Sum = {}", "out: no initializer");

            const string mix = "float Mix(float A, int32 Count)\n{\n    float Total = 0.f;\n    for (int32 I = 0; I < Count; ++I)\n    {\n        Total += A;\n        ++Count;\n    }\n    return Total + Count;\n}\n";
            int loop = mix.IndexOf("for ("), loopEnd = mix.IndexOf("    }\n") + 5;
            var request = new ExtractRequest
            {
                Text = mix, Start = loop, End = loopEnd, Name = "Accumulate",
                In =
                {
                    new ExtractVariable { Name = "Count", Type = "int32", Shape = "value", Written = true, UsedOutside = true },
                    new ExtractVariable { Name = "Total", Type = "float", Shape = "value", Written = true, UsedOutside = true },
                    new ExtractVariable { Name = "A", Type = "float", Shape = "value" },
                },
            };
            ExtractResult x = ExtractSupport.ExtractMethod(request);
            ExpectText(x.Signature, "void Accumulate(int32& Count, float& Total, float A)", "extract: a signature by use");
            ExpectText(x.Call, "Accumulate(Count, Total, A);", "extract: the call");
            ExpectText(x.Definition, "void Accumulate(int32& Count, float& Total, float A)\n{\n    for (int32 I = 0; I < Count; ++I)\n    {\n        Total += A;\n        ++Count;\n    }\n}",
                       "extract: the definition, reindented");

            // One result: returned; an expression: its value.
            const string one = "{\n    int32 Last = Count + 1;\n    Use(Last);\n}";
            request = new ExtractRequest
            {
                Text = one, Start = one.IndexOf("int32 Last"), End = one.IndexOf(";") + 1, Name = "Next", Qualifier = "UThing::", Const = true,
                In = { new ExtractVariable { Name = "Count", Type = "int32", Shape = "value" } },
                Out = { new ExtractVariable { Name = "Last", Type = "int32", Shape = "value" } },
            };
            x = ExtractSupport.ExtractMethod(request);
            ExpectText(x.Call, "int32 Last = Next(Count);", "extract: one result is returned");
            ExpectText(x.Definition, "int32 UThing::Next(int32 Count) const\n{\n    int32 Last = Count + 1;\n    return Last;\n}", "extract: a const member, qualified");
            const string lines = "{\n    int Index = 1;\n    int32 Last = Index + 1;\n    Use(Last);\n}";
            request.Text = lines;
            request.Start = lines.IndexOf("int Index");
            request.End = lines.IndexOf("+ 1;") + 4;
            ExpectText(ExtractSupport.ExtractMethod(request).Definition, "int32 UThing::Next(int32 Count) const\n{\n    int Index = 1;\n    int32 Last = Index + 1;\n    return Last;\n}",
                       "extract: several lines and the returned variable, all at one indentation");
            request = new ExtractRequest { Text = "X = A * 2.f + B;", Start = 4, End = 11, Expression = true, ExpressionType = "float", Name = "Twice", Static = true,
                                           In = { new ExtractVariable { Name = "A", Type = "float", Shape = "value" } } };
            x = ExtractSupport.ExtractMethod(request);
            Expect(x.Call == "Twice(A)" && x.Signature == "static float Twice(float A)" && x.Definition.Contains("return A * 2.f;"), "extract: an expression");

            // Several results: out parameters.
            const string two = "{\n    int32 Min = 0;\n    int32 Max = Count;\n    Use(Min, Max);\n}";
            int s1 = two.IndexOf("int32 Min"), s2 = two.IndexOf("int32 Max");
            request = new ExtractRequest
            {
                Text = two, Start = s1, End = two.IndexOf("Count;") + 6, Name = "Bounds",
                In = { new ExtractVariable { Name = "Count", Type = "int32", Shape = "value" } },
                Out =
                {
                    new ExtractVariable { Name = "Min", Type = "int32", Shape = "value", DeclStart = s1, DeclEnd = two.IndexOf(" = 0") + 4 },
                    new ExtractVariable { Name = "Max", Type = "int32", Shape = "value", DeclStart = s2, DeclEnd = two.IndexOf("Count;") + 5 },
                },
            };
            x = ExtractSupport.ExtractMethod(request);
            ExpectText(x.Call, "int32 Min{};\nint32 Max{};\nBounds(Count, Min, Max);", "extract: several results declared before the call");
            ExpectText(x.Definition, "void Bounds(int32 Count, int32& Min, int32& Max)\n{\n    Min = 0;\n    Max = Count;\n}", "extract: assigned through out parameters");
        }

        private static void MemberTests()
        {
            const string cls = "UCLASS()\nclass UThing : public UObject\n{\n    GENERATED_BODY()\npublic:\n    UFUNCTION(BlueprintCallable,\n              Category = \"X\")\n" +
                               "    void Run(int32 Speed = 1) const;\n\n    FORCEINLINE int32 Get() const { return Value; }\nprotected:\n    int32 Value = 0;\n};\n";
            int open = cls.IndexOf('{'), close = cls.LastIndexOf('}');
            var (at, label, newLine) = MemberSupport.Insertion(cls, open, close, "public", false);
            Expect(at == cls.IndexOf("protected:") && label == null && !newLine, "insertion: after the public section's last member");
            (at, label, _) = MemberSupport.Insertion(cls, open, close, "protected", false);
            Expect(at == cls.IndexOf("};") && label == null, "insertion: the protected section, at the class end");
            (at, label, _) = MemberSupport.Insertion(cls, open, close, "private", false);
            Expect(at == cls.IndexOf("};") && label == "private", "insertion: no private section (GENERATED_BODY alone does not count): a label at the end");
            (at, label, newLine) = MemberSupport.Insertion("struct F { int A; };", 9, 18, "public", true);
            Expect(at == 18 && label == null && newLine, "insertion: a one-line struct");
            Expect(MemberSupport.AccessAt(cls, open, close, cls.IndexOf("Value ="), false) == "protected", "access at a member");
            Expect(MemberSupport.AccessAt(cls, open, close, cls.IndexOf("GENERATED"), false) == "private", "access before any label");

            ExpectText(MemberSupport.MacroText(cls, cls.IndexOf("void Run")), "UFUNCTION(BlueprintCallable, Category = \"X\")", "the macro above a declaration, across lines");
            ExpectText(MemberSupport.MacroText(cls, cls.IndexOf("int32 Value")), "", "no macro");
            int get = cls.IndexOf("FORCEINLINE");
            ExpectText(MemberSupport.DeclarationText(cls, get, cls.IndexOf('}', get) + 1), "FORCEINLINE int32 Get() const", "declaration text without the inline body");
            string Name(string d) { var (s, l) = MemberSupport.NameIn(d); return d.Substring(s, l); }
            ExpectText(Name("void Run(int32 Speed = 1) const"), "Run", "name of a method");
            ExpectText(Name("TArray<int32> Values = {}"), "Values", "name of a field with an initializer");
            ExpectText(Name("int32 Grid[4][4]"), "Grid", "name of an array");
            Expect(MemberSupport.Validate("") != null && MemberSupport.Validate("Speed") != null && MemberSupport.Validate("void F(") != null &&
                   MemberSupport.Validate("float Speed = 1.f") == null && MemberSupport.Validate("~UThing()") == null, "declaration validation");
            Expect(MemberSupport.UeFlags("UFUNCTION(Server, Reliable, WithValidation)") == 3 && MemberSupport.UeFlags("UFUNCTION(BlueprintImplementableEvent)") == 4 &&
                   MemberSupport.UeFlags("UPROPERTY(EditAnywhere)") == 0, "UFUNCTION specifiers");

            CursorInfo f = MemberSupport.MethodInfo("virtual int32 Score(UPARAM(ref) int32& A, float B = 2.f) const override", "UThing", "", "UFUNCTION(BlueprintNativeEvent)");
            Expect(f != null && f.Name == "Score" && f.Result == "int32" && f.Qualifiers == "const" && f.Ue == 1, "method info: name, result, qualifiers, native event");
            var defs = CodeGen.Definitions(f, CodeGen.Qualifier(f, false), "\t", "\n");
            ExpectText(defs[0].Text, "int32 UThing::Score_Implementation(int32& A, float B) const\n{\n}", "definition from a typed declaration");
            f = MemberSupport.MethodInfo("static UThing* Make()", "Ns::UThing", "", "");
            ExpectText(CodeGen.Signature(f, f.Name, CodeGen.Qualifier(f, false)), "UThing* Ns::UThing::Make()", "static dropped, nested class qualified");
            Expect(MemberSupport.MethodInfo("void Tick() = 0", "A", "", "") == null && MemberSupport.MethodInfo("int32 Get() const { return 1; }", "A", "", "") == null &&
                   MemberSupport.MethodInfo("float Speed = 1.f", "A", "", "") == null, "no definition for pure, inline or a field");
            ExpectText(MemberSupport.MemberText("UPROPERTY(EditAnywhere)", "float Speed = 1.f", "\t", "\n"), "\tUPROPERTY(EditAnywhere)\n\tfloat Speed = 1.f;\n", "member text with its macro");

            // Implement Interface / Override Methods.
            CursorInfo run = CursorInfo.Parse("kind\tmethod\nname\tRun\nclasses\tFTurbine\nbase\tIRunnable\nresult\tbool\nparam\tint32 Speed\tSpeed\nparam\tfloat Scale\tScale\tfloat(1)\nqualifiers\tconst\nprimary\t1");
            ExpectText(MemberSupport.OverrideDeclaration(run), "virtual bool Run(int32 Speed, float Scale) const override", "override declaration, default dropped");
            ExpectText(MemberSupport.OverrideBody(run, useSuper: true), "return Super::Run(Speed, Scale);", "override body through Super");
            ExpectText(MemberSupport.OverrideBody(run, useSuper: false), "return IRunnable::Run(Speed, Scale);", "override body through the base");
            CursorInfo draw = CursorInfo.Parse("kind\tmethod\nname\tDraw\nbase\tIPowered\nresult\tfloat\npure\t1");
            ExpectText(MemberSupport.OverrideBody(draw, false), "return {};", "a pure method's stub returns a value");
            CursorInfo powered = CursorInfo.Parse("kind\tmethod\nname\tOnPowered\nbase\tIPowered\nresult\tvoid\nparam\tEPower Level\tLevel\nnativeevent\t1");
            ExpectText(MemberSupport.OverrideDeclaration(powered), "virtual void OnPowered_Implementation(EPower Level) override", "a native event: its _Implementation");
            ExpectText(MemberSupport.OverrideBody(powered, true), "", "a native event's stub");
            CursorInfo byRef = CursorInfo.Parse("kind\tmethod\nname\tGet\nbase\tA\nresult\tFThing&\npure\t1");
            ExpectText(MemberSupport.OverrideBody(byRef, false), "", "no value for a reference");
            const string perMember = "class AThing : public AActor\n{\npublic:\n\tUE_API AThing();\n\tUE_API virtual void Tick(float D) override;\n\tint32 Count;\n};\n";
            Expect(MemberSupport.MemberExportMacro(perMember, 0, perMember.IndexOf('{'), perMember.LastIndexOf('}')) == "UE_API", "members exported one by one");
            const string whole = "class GAME_API AThing : public AActor\n{\npublic:\n\tUE_API AThing();\n\tUE_API void F();\n};\n";
            Expect(MemberSupport.MemberExportMacro(whole, 0, whole.IndexOf('{'), whole.LastIndexOf('}')) == null, "an exported class: no macro per member");
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
            const string empty = "[CoreRedirects]\r\n\r\n[/Script/Engine.Engine]\r\nX=1\r\n";
            (at, text) = RenameSupport.InsertRedirect(empty, "+B");
            ExpectText(empty.Insert(at, text), "[CoreRedirects]\r\n+B\r\n\r\n[/Script/Engine.Engine]\r\nX=1\r\n", "an empty [CoreRedirects] section followed by a blank line");
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