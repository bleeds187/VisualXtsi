#pragma once

#include <cstdint>
#include <string_view>

namespace vx {

enum class TokenKind {
    End,
    Identifier,
    Directive,  // '#' at line start through the directive name
    HeaderName, // <...> or "..." after #include
    Other,      // numbers, literals, punctuation (comments are skipped)
};

struct Token {
    TokenKind kind;
    uint32_t start;
    uint32_t length;
    uint32_t nameStart = 0; // Directive only: the directive name
    uint32_t nameLength = 0;
};

// A minimal C++ lexer: enough to find identifiers and directives while
// skipping comments, string/char literals (including raw strings) and numbers.
class Lexer {
public:
    explicit Lexer(std::u16string_view text) : text_(text) {}
    Token next();
    std::u16string_view slice(uint32_t start, uint32_t length) const {
        return text_.substr(start, length);
    }

private:
    char16_t peek(size_t ahead = 0) const {
        return pos_ + ahead < text_.size() ? text_[pos_ + ahead] : u'\0';
    }
    bool atEnd() const { return pos_ >= text_.size(); }
    void skipWhitespaceAndComments();
    bool skipLineContinuation();
    void skipQuoted(char16_t quote);
    bool tryRawString();
    void skipNumber();
    void skipIdentifierChars();

    std::u16string_view text_;
    size_t pos_ = 0;
    bool atLineStart_ = true;
    bool expectHeaderName_ = false;
};

bool isIdentStart(char16_t c);
bool isIdentChar(char16_t c);

} // namespace vx
