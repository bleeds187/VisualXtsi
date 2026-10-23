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
};
)");
        const struct { const char16_t* name; uint8_t flags; } cases[] = {
            {u"Travel", vx::symbols::UeImplementation},
            {u"ServerScore", vx::symbols::UeImplementation | vx::symbols::UeValidate},
            {u"Boom", vx::symbols::UeImplementation},
            {u"Plain", 0},
            {u"NoMarkup", 0},
        };
        for (const auto& c : cases) {
            const auto* f = findSymbol(ue, c.name, Kind::Method, u"AGame");
            if (!f || f->ue != c.flags) {
                ++failures;
                std::printf("FAIL %s ue flags %d, expected %d\n", narrow(c.name).c_str(), f ? int(f->ue) : -1, int(c.flags));
            }
        }
    }

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
