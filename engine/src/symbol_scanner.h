#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace vx::symbols {

// Mirrors vx_symbol_kind in vx.h.
enum class Kind : uint8_t {
    Namespace = 1,
    Class = 2,
    Struct = 3,
    Union = 4,
    Enum = 5,
    EnumConstant = 6,
    Typedef = 7,
    Function = 8,
    Method = 9,
    Field = 10,
    Variable = 11,
    Macro = 12,
    Delegate = 13,
    Property = 14, // C#
    Event = 15,    // C#
    Interface = 16, // C#
};

struct Symbol {
    std::u16string name;
    std::u16string scope; // "Outer::Inner", empty at global scope
    Kind kind;
    bool definition;      // class/function bodies, variables; false for function declarations
    uint32_t line;        // 1-based
    uint32_t column;      // 1-based, UTF-16 units
    uint8_t ue = 0;       // UeSplit flags for UFUNCTION declarations
};

// How UnrealHeaderTool splits a UFUNCTION: BlueprintNativeEvent and RPCs (Server, Client,
// NetMulticast) are implemented by Name_Implementation, and WithValidation RPCs also by Name_Validate.
enum UeSplit : uint8_t {
    UeImplementation = 1,
    UeValidate = 2,
    UeGenerated = 4, // BlueprintImplementableEvent: UnrealHeaderTool writes the body
};

// Declarations in one file, found lexically (no preprocessing, no includes): fast enough to
// index whole projects, and tolerant of Unreal markup (UCLASS(...), UPROPERTY(...),
// GENERATED_BODY(), *_API) and of code that does not compile. Function bodies are skipped.
std::vector<Symbol> scan(std::u16string_view text);

// The same for C# (csharp_scanner.cpp): types (records as classes or structs), members,
// properties, events and delegates; scopes are joined with ".".
std::vector<Symbol> scanCSharp(std::u16string_view text);

} // namespace vx::symbols
