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
};

struct Symbol {
    std::u16string name;
    std::u16string scope; // "Outer::Inner", empty at global scope
    Kind kind;
    bool definition;      // class/function bodies, variables; false for function declarations
    uint32_t line;        // 1-based
    uint32_t column;      // 1-based, UTF-16 units
};

// Declarations in one file, found lexically (no preprocessing, no includes): fast enough to
// index whole projects, and tolerant of Unreal markup (UCLASS(...), UPROPERTY(...),
// GENERATED_BODY(), *_API) and of code that does not compile. Function bodies are skipped.
std::vector<Symbol> scan(std::u16string_view text);

} // namespace vx::symbols
