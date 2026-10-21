#include "lexer.h"

#include <string>

namespace vx {

bool isIdentStart(char16_t c)
{
    return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z') || c == u'_' || c == u'$' || c >= 0x80;
}

bool isIdentChar(char16_t c)
{
    return isIdentStart(c) || (c >= u'0' && c <= u'9');
}

bool Lexer::skipLineContinuation()
{
    if (peek() != u'\\')
        return false;
    if (peek(1) == u'\n') {
        pos_ += 2;
        return true;
    }
    if (peek(1) == u'\r' && peek(2) == u'\n') {
        pos_ += 3;
        return true;
    }
    return false;
}

void Lexer::skipWhitespaceAndComments()
{
    while (!atEnd()) {
        char16_t c = peek();
        if (c == u'\n') {
            atLineStart_ = true;
            expectHeaderName_ = false;
            ++pos_;
        } else if (c == u' ' || c == u'\t' || c == u'\r' || c == u'\f' || c == u'\v') {
            ++pos_;
        } else if (skipLineContinuation()) {
        } else if (c == u'/' && peek(1) == u'/') {
            // A line comment ends at a newline not escaped by a backslash.
            while (!atEnd() && peek() != u'\n') {
                if (!skipLineContinuation())
                    ++pos_;
            }
        } else if (c == u'/' && peek(1) == u'*') {
            pos_ += 2;
            while (!atEnd() && !(peek() == u'*' && peek(1) == u'/')) {
                if (peek() == u'\n') {
                    atLineStart_ = true;
                    expectHeaderName_ = false;
                }
                ++pos_;
            }
            pos_ = pos_ + 2 <= text_.size() ? pos_ + 2 : text_.size();
        } else {
            break;
        }
    }
}

void Lexer::skipQuoted(char16_t quote)
{
    ++pos_; // opening quote
    while (!atEnd()) {
        char16_t c = peek();
        if (c == u'\\') {
            pos_ += 2;
        } else if (c == quote) {
            ++pos_;
            return;
        } else if (c == u'\n') {
            return; // unterminated literal: stop at end of line
        } else {
            ++pos_;
        }
    }
    if (pos_ > text_.size())
        pos_ = text_.size();
}

// At 'R' followed by '"'. Raw string: R"delim( ... )delim"
bool Lexer::tryRawString()
{
    size_t p = pos_ + 2;
    const size_t delimStart = p;
    while (p < text_.size() && p - delimStart <= 16 && text_[p] != u'(' && text_[p] != u'"' &&
           text_[p] != u' ' && text_[p] != u'\\' && text_[p] != u'\n')
        ++p;
    if (p >= text_.size() || text_[p] != u'(')
        return false;
    std::u16string closing = u")";
    closing.append(text_.substr(delimStart, p - delimStart));
    closing.push_back(u'"');
    const size_t end = text_.find(closing, p + 1);
    pos_ = end == std::u16string_view::npos ? text_.size() : end + closing.size();
    return true;
}

void Lexer::skipNumber()
{
    // pp-number: digits, letters, '.', digit separators and exponent signs.
    while (!atEnd()) {
        char16_t c = peek();
        if (c == u'+' || c == u'-') {
            char16_t p = text_[pos_ - 1];
            if (p == u'e' || p == u'E' || p == u'p' || p == u'P') {
                ++pos_;
                continue;
            }
            return;
        }
        if (c == u'\'' && isIdentChar(peek(1))) {
            pos_ += 2;
            continue;
        }
        if (isIdentChar(c) || c == u'.')
            ++pos_;
        else
            return;
    }
}

void Lexer::skipIdentifierChars()
{
    while (!atEnd() && isIdentChar(peek()))
        ++pos_;
}

Token Lexer::next()
{
    skipWhitespaceAndComments();
    if (atEnd())
        return {TokenKind::End, static_cast<uint32_t>(text_.size()), 0};

    const bool lineStart = atLineStart_;
    atLineStart_ = false;
    const uint32_t start = static_cast<uint32_t>(pos_);
    const char16_t c = peek();

    auto make = [&](TokenKind kind) {
        return Token{kind, start, static_cast<uint32_t>(pos_) - start};
    };

    if (expectHeaderName_ && (c == u'<' || c == u'"')) {
        expectHeaderName_ = false;
        const char16_t close = c == u'<' ? u'>' : u'"';
        ++pos_;
        while (!atEnd() && peek() != close && peek() != u'\n')
            ++pos_;
        if (peek() == close)
            ++pos_;
        return make(TokenKind::HeaderName);
    }
    expectHeaderName_ = false;

    if (c == u'#' && lineStart) {
        ++pos_;
        // Spaces, tabs and line continuations may separate '#' from the name.
        for (;;) {
            if (peek() == u' ' || peek() == u'\t')
                ++pos_;
            else if (!skipLineContinuation())
                break;
        }
        const uint32_t nameStart = static_cast<uint32_t>(pos_);
        skipIdentifierChars();
        Token t = make(TokenKind::Directive);
        t.nameStart = nameStart;
        t.nameLength = static_cast<uint32_t>(pos_) - nameStart;
        const auto name = text_.substr(nameStart, t.nameLength);
        expectHeaderName_ = name == u"include" || name == u"include_next" || name == u"import";
        return t;
    }

    if (c == u'"' || c == u'\'') {
        skipQuoted(c);
        return make(TokenKind::Other);
    }

    if ((c >= u'0' && c <= u'9') || (c == u'.' && peek(1) >= u'0' && peek(1) <= u'9')) {
        skipNumber();
        return make(TokenKind::Other);
    }

    if (isIdentStart(c)) {
        skipIdentifierChars();
        // Encoding prefixes on string/char literals: u8"", u"", U"", L"", R"", u8R"", ...
        const auto ident = text_.substr(start, pos_ - start);
        const char16_t q = peek();
        if (q == u'"' || q == u'\'') {
            const bool raw = ident.back() == u'R' && q == u'"';
            const auto prefix = raw ? ident.substr(0, ident.size() - 1) : ident;
            if (prefix.empty() || prefix == u"u8" || prefix == u"u" || prefix == u"U" || prefix == u"L") {
                if (!raw) {
                    skipQuoted(q);
                    return make(TokenKind::Other);
                }
                pos_ -= 1; // back to 'R'
                if (tryRawString())
                    return make(TokenKind::Other);
                pos_ += 1;
            }
        }
        return make(TokenKind::Identifier);
    }

    ++pos_;
    return make(TokenKind::Other);
}

} // namespace vx
