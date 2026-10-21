#include "vx/vx.h"

#include "lexer.h"

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

    vx::Lexer lexer(view);
    for (vx::Token t = lexer.next(); t.kind != vx::TokenKind::End; t = lexer.next()) {
        if (t.kind == vx::TokenKind::Directive) {
            emit(t.start, t.length, VX_KIND_PREPROCESSOR);
        } else if (t.kind == vx::TokenKind::Identifier) {
            const auto name = lexer.slice(t.start, t.length);
            if (looksLikeMacro(name) || defines.contains(std::u16string(name)))
                emit(t.start, t.length, VX_KIND_MACRO);
        }
    }
    return count;
}

} // extern "C"
