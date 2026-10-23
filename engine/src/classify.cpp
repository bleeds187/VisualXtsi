#include "vx/vx.h"

#include "lexer.h"

#include <algorithm>
#include <string>
#include <unordered_set>

namespace {

// ALL_CAPS heuristic: at least two characters, at least one letter, and only
// uppercase ASCII letters, digits and underscores.
bool looksLikeMacro(std::u16string_view name)
{
    if (name.size() < 2)
        return false;
    bool hasLetter = false;
    for (char16_t c : name) {
        if (c >= u'A' && c <= u'Z')
            hasLetter = true;
        else if (!(c == u'_' || (c >= u'0' && c <= u'9')))
            return false;
    }
    return hasLetter;
}

std::unordered_set<std::u16string> collectDefines(std::u16string_view text)
{
    std::unordered_set<std::u16string> defines;
    vx::Lexer lexer(text);
    for (vx::Token t = lexer.next(); t.kind != vx::TokenKind::End; t = lexer.next()) {
        if (t.kind != vx::TokenKind::Directive || lexer.slice(t.nameStart, t.nameLength) != u"define")
            continue;
        const vx::Token name = lexer.next();
        if (name.kind == vx::TokenKind::Identifier)
            defines.emplace(lexer.slice(name.start, name.length));
    }
    return defines;
}

// Unreal reflection markup: its arguments are specifiers for UnrealHeaderTool, and the macros
// expand to nothing for the compiler.
bool isReflectionMacro(std::u16string_view name)
{
    static const std::u16string_view macros[] = {u"UCLASS", u"USTRUCT", u"UENUM", u"UINTERFACE", u"UPROPERTY",
                                                 u"UFUNCTION", u"UPARAM", u"UMETA", u"UDELEGATE"};
    return std::find(std::begin(macros), std::end(macros), name) != std::end(macros);
}

} // namespace

extern "C" {

VX_API uint32_t VX_CALL vx_abi_version(void)
{
    return VX_ABI_VERSION;
}

VX_API uint32_t VX_CALL vx_classify(const char16_t* text, uint32_t len, vx_span* out, uint32_t cap)
{
    if (!text || len == 0)
        return 0;
    const std::u16string_view view(text, len);
    const auto defines = collectDefines(view);

    uint32_t count = 0;
    auto emit = [&](uint32_t start, uint32_t length, vx_kind kind) {
        if (out && count < cap)
            out[count] = vx_span{start, length, static_cast<uint32_t>(kind)};
        ++count;
    };

    // Inside UCLASS(...) and friends: specifier keys (not values after '=', not strings), at any
    // depth, so meta=(Key=...) keys count too.
    int depth = 0;           // parenthesis depth inside a reflection macro's arguments
    bool reflection = false; // the previous token was a reflection macro name
    bool afterEquals = false;
    vx::Lexer lexer(view);
    for (vx::Token t = lexer.next(); t.kind != vx::TokenKind::End; t = lexer.next()) {
        const auto tok = lexer.slice(t.start, t.length);
        if (t.kind == vx::TokenKind::Directive) {
            emit(t.start, t.length, VX_KIND_PREPROCESSOR);
            depth = 0;
            reflection = false;
            continue;
        }
        if (t.kind == vx::TokenKind::Identifier) {
            if (depth > 0) {
                if (!afterEquals)
                    emit(t.start, t.length, VX_KIND_UE_SPECIFIER);
            } else if (isReflectionMacro(tok)) {
                emit(t.start, t.length, VX_KIND_MACRO);
                reflection = true;
                afterEquals = false;
                continue;
            } else if (looksLikeMacro(tok) || defines.contains(std::u16string(tok))) {
                emit(t.start, t.length, VX_KIND_MACRO);
            }
        } else if (tok == u"(" && (depth > 0 || reflection)) {
            ++depth;
        } else if (tok == u")" && depth > 0) {
            --depth;
        } else if (tok == u";" || tok == u"{" || tok == u"}") {
            depth = 0; // unbalanced markup never swallows the rest of the file
        }
        afterEquals = depth > 0 && tok == u"=";
        reflection = false;
    }
    return count;
}

} // extern "C"
