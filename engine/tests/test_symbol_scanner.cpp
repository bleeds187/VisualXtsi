// Tests for the lexical declaration scanner behind Find Symbol.
#include "symbol_scanner.h"

#include <cstdio>
#include <string>
#include <vector>

using vx::symbols::Kind;

namespace {

int failures = 0;

std::string narrow(const std::u16string& s)
{
    std::string r;
    for (char16_t c : s)
        r.push_back(c < 0x80 ? static_cast<char>(c) : '?');
    return r;
}

const vx::symbols::Symbol* findSymbol(const std::vector<vx::symbols::Symbol>& all, const char16_t* name, Kind kind,
                                      const char16_t* scope)
{
    for (const auto& s : all)
        if (s.name == name && s.kind == kind && s.scope == scope)
            return &s;
    return nullptr;
}

void expect(const std::vector<vx::symbols::Symbol>& all, const char16_t* name, Kind kind, const char16_t* scope,
            uint32_t line = 0, bool definition = true)
{
    const auto* s = findSymbol(all, name, kind, scope);
    if (s && (line == 0 || s->line == line) && s->definition == definition)
        return;
    ++failures;
    std::printf("FAIL %s (kind %d, scope '%s', line %u, def %d)%s\n", narrow(name).c_str(), int(kind),
                narrow(scope).c_str(), line, int(definition), s ? " found with other line/def" : " not found");
}

void expectAbsent(const std::vector<vx::symbols::Symbol>& all, const char16_t* name)
{
    for (const auto& s : all)
        if (s.name == name) {
            ++failures;
            std::printf("FAIL %s should not be a symbol (kind %d, line %u)\n", narrow(name).c_str(), int(s.kind), s.line);
        }
}

} // namespace

int main()
{
    const std::u16string header = uR"(#pragma once
#define SAMPLE_MACRO(x) (x + 1)
#include "Thing.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnThingChanged, int32, NewValue);
DECLARE_EVENT_OneParam(AThing, FThingEvent, float);

namespace Game::Detail
{
class AForward;

UENUM(BlueprintType)
enum class EThingState : uint8
{
    Idle UMETA(DisplayName = "Idle"),
    Busy = 2,
    MAX_VALUE
};

UCLASS(MinimalAPI, Config = Game)
class SAMPLEGAME_API AThing : public AActor
{
    GENERATED_BODY()

public:
    AThing(const FObjectInitializer& Init = FObjectInitializer::Get());
    virtual ~AThing();

    UFUNCTION(BlueprintCallable, Category = "Thing")
    int32 GetCount() const { return Count; }

    UPROPERTY(EditAnywhere)
    int32 Count = 0;

    TArray<FString> Names{TEXT("a"), TEXT("b")};
    static constexpr int32 Limit = 8;
    uint32 bFlag : 1;

    bool operator==(const AThing& Other) const;
    FORCEINLINE void Inline() {}

    struct FNested
    {
        float Value;
    };

private:
    void Helper(int32 A, TFunction<void()> Callback = [] {});
};

template <typename T>
struct TWrapper
{
    T Item;
};

using FThingPtr = TSharedPtr<AThing>;
typedef void (*FThingCallback)(int32);
typedef TMap<FName, int32> FCountMap;

int32 GlobalCounter = 0;
int32 FreeFunction(int32 Value);
} // namespace Game::Detail
)";
    const auto h = vx::symbols::scan(header);
    expect(h, u"SAMPLE_MACRO", Kind::Macro, u"", 2);
    expect(h, u"FOnThingChanged", Kind::Delegate, u"", 5);
    expect(h, u"FThingEvent", Kind::Delegate, u"", 6);
    expect(h, u"Game", Kind::Namespace, u"", 8);
    expect(h, u"Detail", Kind::Namespace, u"", 8);
    expectAbsent(h, u"AForward");
    expect(h, u"EThingState", Kind::Enum, u"Game::Detail", 13);
    expect(h, u"Idle", Kind::EnumConstant, u"Game::Detail::EThingState", 15);
    expect(h, u"Busy", Kind::EnumConstant, u"Game::Detail::EThingState", 16);
    expect(h, u"MAX_VALUE", Kind::EnumConstant, u"Game::Detail::EThingState", 17);
    expect(h, u"AThing", Kind::Class, u"Game::Detail", 21);
    expect(h, u"AThing", Kind::Method, u"Game::Detail::AThing", 26, false);   // constructor
    expect(h, u"~AThing", Kind::Method, u"Game::Detail::AThing", 27, false);
    expect(h, u"GetCount", Kind::Method, u"Game::Detail::AThing", 30, true);  // inline body
    expect(h, u"Count", Kind::Field, u"Game::Detail::AThing", 33);
    expect(h, u"Names", Kind::Field, u"Game::Detail::AThing", 35);
    expect(h, u"Limit", Kind::Field, u"Game::Detail::AThing", 36);
    expect(h, u"bFlag", Kind::Field, u"Game::Detail::AThing", 37);
    expect(h, u"operator==", Kind::Method, u"Game::Detail::AThing", 39, false);
    expect(h, u"Inline", Kind::Method, u"Game::Detail::AThing", 40, true);
    expect(h, u"FNested", Kind::Struct, u"Game::Detail::AThing", 42);
    expect(h, u"Value", Kind::Field, u"Game::Detail::AThing::FNested", 44);
    expect(h, u"Helper", Kind::Method, u"Game::Detail::AThing", 48, false);
    expect(h, u"TWrapper", Kind::Struct, u"Game::Detail", 52);
    expect(h, u"Item", Kind::Field, u"Game::Detail::TWrapper", 54);
    expect(h, u"FThingPtr", Kind::Typedef, u"Game::Detail", 57);
    expect(h, u"FThingCallback", Kind::Typedef, u"Game::Detail", 58);
    expect(h, u"FCountMap", Kind::Typedef, u"Game::Detail", 59);
    expect(h, u"GlobalCounter", Kind::Variable, u"Game::Detail", 61);
    expect(h, u"FreeFunction", Kind::Function, u"Game::Detail", 62, false);
    expectAbsent(h, u"UCLASS");
    expectAbsent(h, u"GENERATED_BODY");
    expectAbsent(h, u"UPROPERTY");
    expectAbsent(h, u"SAMPLEGAME_API");
    expectAbsent(h, u"Init");          // parameters are not symbols
    expectAbsent(h, u"Callback");

    const std::u16string source = uR"(#include "Thing.h"

namespace Game::Detail
{
AThing::AThing(const FObjectInitializer& Init)
    : Super(Init)
{
    int32 Local = 0; // inside a body: not indexed
}

AThing::~AThing() = default;

void AThing::Helper(int32 A, TFunction<void()> Callback)
{
    if (A > 0) { Callback(); }
}

int32 FreeFunction(int32 Value)
{
    return Value * 2;
}

template <typename T>
void TWrapper<T>::Reset() {}
}

IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, SampleGame, "SampleGame")
static int32 FileStatic = 3;
)";
    const auto s = vx::symbols::scan(source);
    expect(s, u"AThing", Kind::Method, u"Game::Detail::AThing", 5, true);
    expect(s, u"Helper", Kind::Method, u"Game::Detail::AThing", 13, true);
    expect(s, u"FreeFunction", Kind::Function, u"Game::Detail", 18, true);
    expect(s, u"Reset", Kind::Method, u"Game::Detail::TWrapper", 24, true);
    expect(s, u"FileStatic", Kind::Variable, u"", 28);
    expectAbsent(s, u"Local");
    expectAbsent(s, u"Super");
    expectAbsent(s, u"IMPLEMENT_PRIMARY_GAME_MODULE");

    // Columns are 1-based UTF-16 units.
    const auto* helper = findSymbol(s, u"Helper", Kind::Method, u"Game::Detail::AThing");
    if (!helper || helper->column != 14) {
        ++failures;
        std::printf("FAIL Helper column %u\n", helper ? helper->column : 0);
    }

    // UnrealHeaderTool-split UFUNCTIONs carry how they are implemented.
    {
        const auto ue = vx::symbols::scan(uR"(UCLASS()
class AGame : public AActor
{
    GENERATED_BODY()
    UFUNCTION(BlueprintCallable, BlueprintNativeEvent, Category="Game")
    void Travel();
    UFUNCTION(Server, Reliable, WithValidation)
    void ServerScore(int32 Score);
    UFUNCTION(NetMulticast, Unreliable) void Boom();
    UFUNCTION(BlueprintCallable)
    void Plain();
    void NoMarkup();
    UFUNCTION(BlueprintImplementableEvent)
    void OnHit(float Damage);
};
)");
        const struct { const char16_t* name; uint8_t flags; } cases[] = {
            {u"Travel", vx::symbols::UeImplementation},
            {u"ServerScore", vx::symbols::UeImplementation | vx::symbols::UeValidate},
            {u"Boom", vx::symbols::UeImplementation},
            {u"Plain", 0},
            {u"NoMarkup", 0},
            {u"OnHit", vx::symbols::UeGenerated},
        };
        for (const auto& c : cases) {
            const auto* f = findSymbol(ue, c.name, Kind::Method, u"AGame");
            if (!f || f->ue != c.flags) {
                ++failures;
                std::printf("FAIL %s ue flags %d, expected %d\n", narrow(c.name).c_str(), f ? int(f->ue) : -1, int(c.flags));
            }
        }
    }

    // C#: a file-scoped namespace, types of every kind, members, and literals that must not
    // confuse the scanner (verbatim, interpolated and raw strings with braces and quotes).
    {
        const std::u16string cs = uR"cs(using System;
using Alias = System.Collections.Generic.List<int>;
[assembly: SomeAttribute("x")]
namespace Fake.Tools;

/// <summary>Docs { not code }</summary>
[Serializable, Obsolete("old")]
public sealed partial class ScoreBoard<T> : IScored, IDisposable where T : class, new()
{
    public const int MaxPlayers = 8, MinPlayers = 1;
    private readonly Dictionary<string, int> _scores = new() { ["a"] = 1 }, _byName = new Dictionary<string, int>();
    private static string s_path = @"C:\temp\""quoted""{";
    private string _greeting = $"Hello {Name("}")} {{literal}}";
    private string _raw = """
        { "json": "with } braces" }
        """;
    private Action<int> _onChange = x => { Console.WriteLine(x); };
    private char _brace = '{';

    public event EventHandler Changed;
    public event Action<int> Added, Removed;
    public event EventHandler Custom { add { } remove { } }
    public int Total { get; private set; } = 0;
    public string Label => $"{Total} points";
    public int this[int index] => index;
    [JsonIgnore] public T @class { get; init; }

    public ScoreBoard(int capacity) : base() { }
    ~ScoreBoard() { }
    public void Add(string name, int points = 1) { _scores[name] = points; }
    public override string ToString() => $"{Total}";
    public static ScoreBoard<T> operator +(ScoreBoard<T> a, int b) => a; public static bool operator <(ScoreBoard<T> a, int b) => true;
    public TResult Map<TResult>(Func<T, TResult> f) where TResult : struct { return default; }
    int IScored.Score() { return Total; }
    partial void OnReset();
    public void Dispose() { }

    private enum Phase : byte { Warmup = 1, [Description("live")] Live, Done }
    public record struct Entry(string Name, int Points = 0);
    public interface INested { void Ping(); int Count { get; } }
}

public interface IScored { int Score(); }
public record Player(string Name, List<int> Scores) : Person(Name);
public delegate void ScoreHandler<T>(object sender, T score);
internal readonly ref struct Point { public readonly int X, Y; }
)cs";
        const auto all = vx::symbols::scanCSharp(cs);
        expect(all, u"Fake", Kind::Namespace, u"", 4);
        expect(all, u"Tools", Kind::Namespace, u"Fake", 4);
        expect(all, u"ScoreBoard", Kind::Class, u"Fake.Tools", 8);
        expect(all, u"MaxPlayers", Kind::Field, u"Fake.Tools.ScoreBoard", 10);
        expect(all, u"MinPlayers", Kind::Field, u"Fake.Tools.ScoreBoard", 10);
        expect(all, u"_scores", Kind::Field, u"Fake.Tools.ScoreBoard", 11);
        expect(all, u"_byName", Kind::Field, u"Fake.Tools.ScoreBoard", 11);
        expect(all, u"s_path", Kind::Field, u"Fake.Tools.ScoreBoard", 12);
        expect(all, u"_greeting", Kind::Field, u"Fake.Tools.ScoreBoard", 13);
        expect(all, u"_raw", Kind::Field, u"Fake.Tools.ScoreBoard", 14);
        expect(all, u"_onChange", Kind::Field, u"Fake.Tools.ScoreBoard", 17);
        expect(all, u"_brace", Kind::Field, u"Fake.Tools.ScoreBoard", 18);
        expect(all, u"Changed", Kind::Event, u"Fake.Tools.ScoreBoard", 20);
        expect(all, u"Added", Kind::Event, u"Fake.Tools.ScoreBoard", 21);
        expect(all, u"Removed", Kind::Event, u"Fake.Tools.ScoreBoard", 21);
        expect(all, u"Custom", Kind::Event, u"Fake.Tools.ScoreBoard", 22);
        expect(all, u"Total", Kind::Property, u"Fake.Tools.ScoreBoard", 23);
        expect(all, u"Label", Kind::Property, u"Fake.Tools.ScoreBoard", 24);
        expect(all, u"class", Kind::Property, u"Fake.Tools.ScoreBoard", 26);
        expect(all, u"ScoreBoard", Kind::Method, u"Fake.Tools.ScoreBoard", 28);
        expect(all, u"~ScoreBoard", Kind::Method, u"Fake.Tools.ScoreBoard", 29);
        expect(all, u"Add", Kind::Method, u"Fake.Tools.ScoreBoard", 30);
        expect(all, u"ToString", Kind::Method, u"Fake.Tools.ScoreBoard", 31);
        expect(all, u"operator+", Kind::Method, u"Fake.Tools.ScoreBoard", 32);
        expect(all, u"operator<", Kind::Method, u"Fake.Tools.ScoreBoard", 32);
        expect(all, u"Map", Kind::Method, u"Fake.Tools.ScoreBoard", 33);
        expect(all, u"Score", Kind::Method, u"Fake.Tools.ScoreBoard", 34);
        expect(all, u"OnReset", Kind::Method, u"Fake.Tools.ScoreBoard", 35, false);
        expect(all, u"Dispose", Kind::Method, u"Fake.Tools.ScoreBoard", 36);
        expect(all, u"Phase", Kind::Enum, u"Fake.Tools.ScoreBoard", 38);
        expect(all, u"Warmup", Kind::EnumConstant, u"Fake.Tools.ScoreBoard.Phase", 38);
        expect(all, u"Live", Kind::EnumConstant, u"Fake.Tools.ScoreBoard.Phase", 38);
        expect(all, u"Done", Kind::EnumConstant, u"Fake.Tools.ScoreBoard.Phase", 38);
        expect(all, u"Entry", Kind::Struct, u"Fake.Tools.ScoreBoard", 39);
        expect(all, u"Points", Kind::Property, u"Fake.Tools.ScoreBoard.Entry", 39);
        expect(all, u"INested", Kind::Interface, u"Fake.Tools.ScoreBoard", 40);
        expect(all, u"Ping", Kind::Method, u"Fake.Tools.ScoreBoard.INested", 40, false);
        expect(all, u"Count", Kind::Property, u"Fake.Tools.ScoreBoard.INested", 40);
        expect(all, u"IScored", Kind::Interface, u"Fake.Tools", 43);
        expect(all, u"Score", Kind::Method, u"Fake.Tools.IScored", 43, false);
        expect(all, u"Player", Kind::Class, u"Fake.Tools", 44);
        expect(all, u"Scores", Kind::Property, u"Fake.Tools.Player", 44);
        expect(all, u"ScoreHandler", Kind::Delegate, u"Fake.Tools", 45);
        expect(all, u"Point", Kind::Struct, u"Fake.Tools", 46);
        expect(all, u"X", Kind::Field, u"Fake.Tools.Point", 46);
        expect(all, u"Y", Kind::Field, u"Fake.Tools.Point", 46);
        for (const char16_t* absent : {u"Alias", u"WriteLine", u"Console", u"json", u"literal", u"index", u"name", u"points",
                                       u"capacity", u"x", u"get", u"string", u"int", u"Description", u"Serializable", u"base", u"a", u"b"})
            expectAbsent(all, absent);

        // Block-scoped namespaces and a top-level program: statements are not declarations.
        const auto top = vx::symbols::scanCSharp(uR"cs(var builder = Make(args);
Console.WriteLine("hi");
if (builder != null) { Run(); }
void LocalHelper() { }
namespace Outer.Inner { class Deep { int _v; } }
namespace Second { enum Mode { A, B } }
)cs");
        expect(top, u"LocalHelper", Kind::Function, u"", 4);
        expect(top, u"Deep", Kind::Class, u"Outer.Inner", 5);
        expect(top, u"_v", Kind::Field, u"Outer.Inner.Deep", 5);
        expect(top, u"Mode", Kind::Enum, u"Second", 6);
        expect(top, u"B", Kind::EnumConstant, u"Second.Mode", 6);
        for (const char16_t* absent : {u"builder", u"WriteLine", u"Run", u"Make"})
            expectAbsent(top, absent);

        // The shapes of build rules code that Build.cs and Target.cs navigation relies on (made up):
        // partial rules classes, nested enums, attributed and wrapper properties, global:: aliases.
        const auto rules = vx::symbols::scanCSharp(uR"cs(namespace FakeBuildTool
{
    public enum KindOfTarget { Game, Editor }
    public partial class FakeModuleRules
    {
        public enum PchMode { Default, UseShared }
        [RequiresUnique, CommandLine("-Unity")]
        public bool bUseUnity { get; set; } = true;
        public PchMode PchUsage
        {
            get { return _pch; }
            set { _pch = value; }
        }
        public List<string> PublicDependencyNames = new List<string>();
        public FakeModuleRules(ReadOnlyFakeTargetRules Target) { }
    }
    public abstract partial class FakeTargetRules
    {
        public static class KindOfTarget
        {
            public const global::FakeBuildTool.KindOfTarget Game = global::FakeBuildTool.KindOfTarget.Game;
        }
        public global::FakeBuildTool.KindOfTarget Type { get; set; } = global::FakeBuildTool.KindOfTarget.Game;
    }
    public partial class ReadOnlyFakeTargetRules
    {
        private readonly FakeTargetRules Inner;
        public global::FakeBuildTool.KindOfTarget Type => Inner.Type;
        public bool bIsEngineInstalled => Inner.bIsEngineInstalled;
    }
}
)cs");
        expect(rules, u"Editor", Kind::EnumConstant, u"FakeBuildTool.KindOfTarget", 3);
        expect(rules, u"FakeModuleRules", Kind::Class, u"FakeBuildTool", 4);
        expect(rules, u"PchMode", Kind::Enum, u"FakeBuildTool.FakeModuleRules", 6);
        expect(rules, u"UseShared", Kind::EnumConstant, u"FakeBuildTool.FakeModuleRules.PchMode", 6);
        expect(rules, u"bUseUnity", Kind::Property, u"FakeBuildTool.FakeModuleRules", 8);
        expect(rules, u"PchUsage", Kind::Property, u"FakeBuildTool.FakeModuleRules", 9);
        expect(rules, u"PublicDependencyNames", Kind::Field, u"FakeBuildTool.FakeModuleRules", 14);
        expect(rules, u"FakeModuleRules", Kind::Method, u"FakeBuildTool.FakeModuleRules", 15);
        expect(rules, u"KindOfTarget", Kind::Class, u"FakeBuildTool.FakeTargetRules", 19);
        expect(rules, u"Game", Kind::Field, u"FakeBuildTool.FakeTargetRules.KindOfTarget", 21);
        expect(rules, u"Type", Kind::Property, u"FakeBuildTool.FakeTargetRules", 23);
        expect(rules, u"Type", Kind::Property, u"FakeBuildTool.ReadOnlyFakeTargetRules", 28);
        expect(rules, u"bIsEngineInstalled", Kind::Property, u"FakeBuildTool.ReadOnlyFakeTargetRules", 29);
        for (const char16_t* absent : {u"RequiresUnique", u"CommandLine", u"_pch", u"value", u"get", u"Target"})
            expectAbsent(rules, absent);
    }

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
