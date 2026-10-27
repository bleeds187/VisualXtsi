// The C# declaration scanner behind Find Symbol: the same idea as the C++ one (lexical, tolerant,
// bodies skipped), with C#'s own tokens (verbatim, interpolated and raw strings, @identifiers),
// attributes, file-scoped namespaces, properties, events, records and expression-bodied members.
#include "symbol_scanner.h"

#include "lexer.h"

#include <algorithm>

namespace vx::symbols {

namespace {

struct Tok {
    std::u16string_view text;
    uint32_t offset;
    bool ident;
    bool verbatim = false; // @class: an identifier even if spelled like a keyword
};

bool is(const Tok& t, std::u16string_view s)
{
    return t.text == s;
}

class CsLexer {
public:
    explicit CsLexer(std::u16string_view text) : text_(text) {}

    // False at the end of the text.
    bool next(Tok& out)
    {
        skipTrivia();
        if (pos_ >= text_.size())
            return false;
        const size_t start = pos_;
        const char16_t c = text_[pos_];
        if (c == u'@' && pos_ + 1 < text_.size() && isIdentStart(text_[pos_ + 1])) { // @class: an identifier
            ++pos_;
            while (pos_ < text_.size() && isIdentChar(text_[pos_]))
                ++pos_;
            out = {text_.substr(start + 1, pos_ - start - 1), static_cast<uint32_t>(start + 1), true, true};
            return true;
        }
        if (c == u'"' || ((c == u'$' || c == u'@') && stringAhead())) {
            skipString();
            out = {text_.substr(start, pos_ - start), static_cast<uint32_t>(start), false};
            return true;
        }
        if (c == u'\'') {
            for (++pos_; pos_ < text_.size() && text_[pos_] != u'\'' && text_[pos_] != u'\n'; ++pos_)
                if (text_[pos_] == u'\\')
                    ++pos_;
            pos_ = std::min(pos_ + 1, text_.size());
            out = {text_.substr(start, pos_ - start), static_cast<uint32_t>(start), false};
            return true;
        }
        if (c >= u'0' && c <= u'9') {
            while (pos_ < text_.size() && (isIdentChar(text_[pos_]) ||
                                           (text_[pos_] == u'.' && pos_ + 1 < text_.size() && text_[pos_ + 1] >= u'0' &&
                                            text_[pos_ + 1] <= u'9')))
                ++pos_;
            out = {text_.substr(start, pos_ - start), static_cast<uint32_t>(start), false};
            return true;
        }
        if (isIdentStart(c) && c != u'$') {
            while (pos_ < text_.size() && isIdentChar(text_[pos_]) && text_[pos_] != u'$')
                ++pos_;
            out = {text_.substr(start, pos_ - start), static_cast<uint32_t>(start), true};
            return true;
        }
        // "=>" and the comparisons that contain "=" are one token, so a lone "=" is an assignment.
        ++pos_;
        if (pos_ < text_.size() && text_[pos_] == u'=' && (c == u'=' || c == u'!' || c == u'<' || c == u'>'))
            ++pos_;
        else if (c == u'=' && pos_ < text_.size() && text_[pos_] == u'>')
            ++pos_;
        out = {text_.substr(start, pos_ - start), static_cast<uint32_t>(start), false};
        return true;
    }

private:
    void skipTrivia()
    {
        while (pos_ < text_.size()) {
            const char16_t c = text_[pos_];
            if (c == u'\n') {
                lineStart_ = true;
                ++pos_;
            } else if (c == u' ' || c == u'\t' || c == u'\r' || c == u'\f' || c == u'\v' || c == 0xFEFF) {
                ++pos_;
            } else if (c == u'/' && pos_ + 1 < text_.size() && text_[pos_ + 1] == u'/') {
                while (pos_ < text_.size() && text_[pos_] != u'\n')
                    ++pos_;
            } else if (c == u'/' && pos_ + 1 < text_.size() && text_[pos_ + 1] == u'*') {
                const size_t end = text_.find(u"*/", pos_ + 2);
                pos_ = end == std::u16string_view::npos ? text_.size() : end + 2;
            } else if (c == u'#' && lineStart_) { // #region, #if, #nullable, ...: the rest of the line
                while (pos_ < text_.size() && text_[pos_] != u'\n')
                    ++pos_;
            } else {
                lineStart_ = false;
                return;
            }
        }
    }

    // At "$" or "@": whether a string literal ($"", @"", $@"", @$"", $$"""...) starts here.
    bool stringAhead() const
    {
        size_t p = pos_;
        while (p < text_.size() && (text_[p] == u'$' || text_[p] == u'@'))
            ++p;
        return p < text_.size() && text_[p] == u'"';
    }

    // Any string literal at pos_, prefixes included. Interpolation holes may hold strings of their own.
    void skipString()
    {
        bool verbatim = false;
        int dollars = 0;
        while (text_[pos_] == u'$' || text_[pos_] == u'@') {
            if (text_[pos_] == u'@')
                verbatim = true;
            else
                ++dollars;
            ++pos_;
        }
        size_t quotes = 0;
        while (pos_ + quotes < text_.size() && text_[pos_ + quotes] == u'"')
            ++quotes;
        if (quotes >= 3) { // raw string: ends at the same number of quotes
            const std::u16string close(quotes, u'"');
            const size_t end = text_.find(close, pos_ + quotes);
            pos_ = end == std::u16string_view::npos ? text_.size() : end + quotes;
            return;
        }
        ++pos_; // opening quote (an empty "" ends right away below)
        int depth = 0;
        while (pos_ < text_.size()) {
            const char16_t c = text_[pos_];
            if (depth > 0) { // inside an interpolation hole: code
                if (c == u'"' || ((c == u'$' || c == u'@') && stringAhead())) {
                    skipString();
                    continue;
                }
                if (c == u'{')
                    ++depth;
                else if (c == u'}')
                    --depth;
                ++pos_;
                continue;
            }
            if (c == u'"') {
                if (verbatim && pos_ + 1 < text_.size() && text_[pos_ + 1] == u'"') {
                    pos_ += 2;
                    continue;
                }
                ++pos_;
                return;
            }
            if (c == u'\\' && !verbatim) {
                pos_ += 2;
                continue;
            }
            if (c == u'\n' && !verbatim)
                return; // unterminated
            if (dollars > 0 && c == u'{') {
                if (pos_ + 1 < text_.size() && text_[pos_ + 1] == u'{') {
                    pos_ += 2;
                    continue;
                }
                depth = 1;
            }
            ++pos_;
        }
        pos_ = std::min(pos_, text_.size());
    }

    std::u16string_view text_;
    size_t pos_ = 0;
    bool lineStart_ = true;
};

bool isModifier(std::u16string_view s)
{
    static const std::u16string_view modifiers[] = {
        u"public",  u"private", u"protected", u"internal", u"static",   u"readonly", u"sealed",   u"abstract",
        u"virtual", u"override", u"new",      u"partial",  u"async",    u"extern",   u"unsafe",   u"volatile",
        u"const",   u"required", u"file",     u"ref",     u"fixed",    u"implicit", u"explicit", u"scoped"};
    return std::find(std::begin(modifiers), std::end(modifiers), s) != std::end(modifiers);
}

bool isKeyword(std::u16string_view s)
{
    static const std::u16string_view keywords[] = {
        u"if",     u"else",   u"for",     u"foreach", u"while",   u"do",     u"switch",    u"case",   u"return",
        u"throw",  u"try",    u"catch",   u"finally", u"using",   u"lock",   u"fixed",     u"await",  u"yield",
        u"new",    u"typeof", u"sizeof",  u"nameof",  u"default", u"this",   u"base",      u"var",    u"void",
        u"int",    u"uint",   u"long",    u"ulong",   u"short",   u"ushort", u"byte",      u"sbyte",  u"bool",
        u"char",   u"float",  u"double",  u"decimal", u"string",  u"object", u"dynamic",   u"class",  u"struct",
        u"enum",   u"record", u"interface", u"delegate", u"event", u"operator", u"namespace", u"where", u"get",
        u"set",    u"init",   u"add",     u"remove",  u"value",   u"in",     u"out",       u"ref",    u"is",
        u"as",     u"checked", u"unchecked", u"goto", u"break",   u"continue"};
    return std::find(std::begin(keywords), std::end(keywords), s) != std::end(keywords);
}

bool isKeyword(const Tok& t)
{
    return !t.verbatim && isKeyword(t.text);
}

// Index just past the bracket group opened at t[i] ("(", "[", "{" or "<"), or t.size().
size_t skipGroup(const std::vector<Tok>& t, size_t i)
{
    const std::u16string_view open = t[i].text;
    const std::u16string_view close = open == u"(" ? u")" : open == u"[" ? u"]" : open == u"{" ? u"}" : u">";
    int depth = 0;
    for (; i < t.size(); ++i) {
        if (t[i].text == open)
            ++depth;
        else if (t[i].text == close && --depth == 0)
            return i + 1;
    }
    return t.size();
}

// Drops attributes ([...]) and modifiers before a declaration.
std::vector<Tok> strip(const std::vector<Tok>& in)
{
    size_t i = 0;
    while (i < in.size()) {
        if (is(in[i], u"["))
            i = skipGroup(in, i);
        else if (isModifier(in[i].text))
            ++i;
        else
            break;
    }
    return std::vector<Tok>(in.begin() + static_cast<ptrdiff_t>(std::min(i, in.size())), in.end());
}

// The first "(" outside <...>, or npos. After "operator" the symbols are the name ("operator <").
size_t topLevelParen(const std::vector<Tok>& t)
{
    int angle = 0;
    bool afterOperator = false;
    for (size_t i = 0; i < t.size(); ++i) {
        if (is(t[i], u"operator"))
            afterOperator = true;
        else if (afterOperator && !is(t[i], u"("))
            continue;
        if (is(t[i], u"(") && afterOperator)
            return i;
        if (is(t[i], u"<"))
            ++angle;
        else if (is(t[i], u">") && angle > 0)
            --angle;
        else if (is(t[i], u"(") && angle == 0)
            return i;
    }
    return std::u16string::npos;
}

// The name token before t[end] (an opening "(", or the end of a property head): an identifier,
// possibly followed by generic arguments; npos if there is none.
size_t nameBefore(const std::vector<Tok>& t, size_t end)
{
    size_t i = end;
    if (i > 0 && is(t[i - 1], u">")) {
        int depth = 0;
        while (i-- > 0) {
            if (is(t[i], u">"))
                ++depth;
            else if (is(t[i], u"<") && --depth == 0)
                break;
        }
        if (i == std::u16string::npos || i == 0)
            return std::u16string::npos;
    }
    if (i == 0 || !t[i - 1].ident || isKeyword(t[i - 1]))
        return std::u16string::npos;
    return i - 1;
}

// Whether a type (or a qualified explicit interface name, "IFoo.Bar") comes before the name at
// t[name]: a member declaration, not a call such as "Console.WriteLine(" or "Foo(".
bool declaresMember(const std::vector<Tok>& t, size_t name)
{
    size_t i = name;
    while (i >= 2 && is(t[i - 1], u".") && t[i - 2].ident)
        i -= 2;
    if (i == 0)
        return false;
    const Tok& before = t[i - 1];
    if (before.ident)
        return !isKeyword(before.text) || before.text == u"void" || before.text == u"int" || before.text == u"bool" ||
               before.text == u"string" || before.text == u"object" || before.text == u"long" || before.text == u"float" ||
               before.text == u"double" || before.text == u"byte" || before.text == u"char" || before.text == u"uint" ||
               before.text == u"ulong" || before.text == u"short" || before.text == u"ushort" || before.text == u"sbyte" ||
               before.text == u"decimal" || before.text == u"dynamic" || before.text == u"event";
    return is(before, u">") || is(before, u"]") || is(before, u"?") || is(before, u"*") || is(before, u")"); // T<X> T[] T? T* (a, b)
}

class Scanner {
public:
    explicit Scanner(std::u16string_view text) : text_(text)
    {
        lineStarts_.push_back(0);
        for (size_t i = 0; i < text.size(); ++i)
            if (text[i] == u'\n')
                lineStarts_.push_back(static_cast<uint32_t>(i + 1));
    }

    std::vector<Symbol> run()
    {
        CsLexer lexer(text_);
        for (Tok t; lexer.next(t);)
            feed(t);
        return std::move(symbols_);
    }

private:
    enum class ScopeKind { Global, Namespace, Type, Enum, Body };
    struct Scope {
        ScopeKind kind;
        std::u16string name;
        bool keepStatement = false; // braces inside a statement's parentheses
    };

    // A symbol at a token, named after it unless a name is given ("operator+", "~Foo").
    void add(Kind kind, const Tok& at, std::u16string scope, bool definition = true, std::u16string name = {})
    {
        const auto it = std::upper_bound(lineStarts_.begin(), lineStarts_.end(), at.offset);
        const uint32_t line = static_cast<uint32_t>(it - lineStarts_.begin());
        if (name.empty())
            name = at.text;
        symbols_.push_back({std::move(name), std::move(scope), kind, definition, line, at.offset - *(it - 1) + 1});
    }

    std::u16string scopeName() const
    {
        std::u16string s;
        for (const Scope& sc : scopes_)
            if (sc.kind != ScopeKind::Global && sc.kind != ScopeKind::Body && !sc.name.empty())
                s += (s.empty() ? u"" : u".") + sc.name;
        return s;
    }

    bool memberScope() const
    {
        const ScopeKind k = scopes_.back().kind;
        return k == ScopeKind::Global || k == ScopeKind::Namespace || k == ScopeKind::Type;
    }

    void feed(const Tok& tok)
    {
        Scope& top = scopes_.back();
        if (top.kind == ScopeKind::Body) {
            if (is(tok, u"{")) {
                scopes_.push_back({ScopeKind::Body, u""});
            } else if (is(tok, u"}")) {
                const bool keep = top.keepStatement;
                scopes_.pop_back();
                if (!keep)
                    statement_.clear();
            }
            return;
        }
        if (skipExpression_) { // an initializer or expression body, up to its ";" (or "," between declarators)
            if (is(tok, u"(") || is(tok, u"[") || is(tok, u"{")) {
                ++depth_;
            } else if (is(tok, u")") || is(tok, u"]") || is(tok, u"}")) {
                if (depth_ == 0) { // the enclosing scope ends (unterminated code)
                    skipExpression_ = false;
                    continuation_ = false;
                    feed(tok);
                    return;
                }
                --depth_;
            } else if (depth_ == 0 && is(tok, u";")) {
                skipExpression_ = false;
                continuation_ = false;
                statement_.clear();
            } else if (is(tok, u"<")) { // generic arguments: "new Dictionary<string, int>()"
                ++angle_;
            } else if (is(tok, u">") && angle_ > 0) {
                --angle_;
            } else if (depth_ == 0 && angle_ == 0 && is(tok, u",") && continuation_) {
                skipExpression_ = false; // "int a = 1, b = 2;": b follows
                statement_.clear();
            }
            return;
        }
        if (continuation_ && statement_.empty() && tok.ident) { // the next declarator of a field or event list
            add(continuationKind_, tok, scopeName());
            statement_.push_back(tok);
            return;
        }
        if (is(tok, u"(") || is(tok, u"["))
            ++paren_;
        else if ((is(tok, u")") || is(tok, u"]")) && paren_ > 0)
            --paren_;
        if (is(tok, u"{")) {
            if (paren_ > 0) {
                scopes_.push_back({ScopeKind::Body, u"", true});
                return;
            }
            open();
            statement_.clear();
            continuation_ = false;
            return;
        }
        if (is(tok, u"}")) {
            if (top.kind == ScopeKind::Enum)
                enumerator();
            if (scopes_.size() > 1)
                scopes_.pop_back();
            statement_.clear();
            paren_ = 0;
            continuation_ = false;
            return;
        }
        if (top.kind == ScopeKind::Enum) {
            if (is(tok, u",") && paren_ == 0) {
                enumerator();
                statement_.clear();
            } else {
                statement_.push_back(tok);
            }
            return;
        }
        if (paren_ == 0 && is(tok, u";")) {
            if (!continuation_)
                declaration();
            statement_.clear();
            continuation_ = false;
            return;
        }
        if (paren_ == 0 && continuation_ && is(tok, u",")) {
            statement_.clear();
            return;
        }
        if (paren_ == 0 && (is(tok, u"=") || is(tok, u"=>")) && memberScope()) {
            if (!continuation_)
                is(tok, u"=") ? assignment() : expressionBody();
            skipExpression_ = true;
            depth_ = 0;
            angle_ = 0;
            statement_.clear();
            return;
        }
        statement_.push_back(tok);
    }

    // The kind of type a declaration starts with (after attributes and modifiers), and where its
    // name is; false if it is not a type.
    static bool typeHead(const std::vector<Tok>& s, Kind& kind, size_t& name, bool& isRecord)
    {
        if (s.empty())
            return false;
        isRecord = is(s[0], u"record");
        size_t n = 1;
        if (is(s[0], u"class") || is(s[0], u"record")) {
            kind = Kind::Class;
            if (isRecord && s.size() > 1 && (is(s[1], u"struct") || is(s[1], u"class"))) {
                kind = is(s[1], u"struct") ? Kind::Struct : Kind::Class;
                n = 2;
            }
        } else if (is(s[0], u"struct")) {
            kind = Kind::Struct;
        } else if (is(s[0], u"interface")) {
            kind = Kind::Interface;
        } else if (is(s[0], u"enum")) {
            kind = Kind::Enum;
        } else {
            return false;
        }
        if (n >= s.size() || !s[n].ident)
            return false;
        name = n;
        return true;
    }

    // A record's positional parameters are its properties.
    void recordProperties(const std::vector<Tok>& s, size_t name, const std::u16string& scope)
    {
        size_t i = name + 1;
        if (i < s.size() && is(s[i], u"<"))
            i = skipGroup(s, i);
        if (i >= s.size() || !is(s[i], u"("))
            return;
        const size_t end = skipGroup(s, i);
        int depth = 0;
        size_t last = std::u16string::npos;
        bool inDefault = false;
        for (size_t k = i + 1; k < end; ++k) {
            if (is(s[k], u"(") || is(s[k], u"[") || is(s[k], u"<"))
                ++depth;
            else if (is(s[k], u")") || is(s[k], u"]") || is(s[k], u">"))
                --depth;
            if (depth < 0 || (depth == 0 && is(s[k], u","))) {
                if (last != std::u16string::npos)
                    add(Kind::Property, s[last], scope);
                last = std::u16string::npos;
                inDefault = false;
            } else if (depth == 0 && is(s[k], u"=")) {
                inDefault = true;
            } else if (depth == 0 && !inDefault && s[k].ident) {
                last = k;
            }
        }
    }

    void typeDeclaration(const std::vector<Tok>& s, Kind kind, size_t name, bool isRecord, bool body)
    {
        const std::u16string scope = scopeName();
        add(kind, s[name], scope);
        if (isRecord)
            recordProperties(s, name, scope + (scope.empty() ? u"" : u".") + std::u16string(s[name].text));
        if (body)
            scopes_.push_back({kind == Kind::Enum ? ScopeKind::Enum : ScopeKind::Type, std::u16string(s[name].text)});
    }

    void namespaceDeclaration(const std::vector<Tok>& s)
    {
        std::u16string scope = scopeName(), name;
        for (size_t i = 1; i < s.size(); ++i) {
            if (!s[i].ident)
                continue;
            add(Kind::Namespace, s[i], scope);
            scope += (scope.empty() ? u"" : u".") + std::u16string(s[i].text);
            name += (name.empty() ? u"" : u".") + std::u16string(s[i].text);
        }
        scopes_.push_back({ScopeKind::Namespace, name});
    }

    // "{" ends the head of a namespace, type, method, property, event or accessor-less block.
    void open()
    {
        const std::vector<Tok> s = strip(statement_);
        Kind kind;
        size_t name;
        bool isRecord;
        if (!s.empty() && is(s[0], u"namespace")) {
            namespaceDeclaration(s);
            return;
        }
        if (typeHead(s, kind, name, isRecord)) {
            typeDeclaration(s, kind, name, isRecord, true);
            return;
        }
        if (memberScope())
            member(s, true);
        scopes_.push_back({ScopeKind::Body, u""});
    }

    // A method, constructor, operator, property, indexer or event with a body or "=>" body.
    void member(const std::vector<Tok>& s, bool withBody)
    {
        if (s.size() < 2)
            return;
        const std::u16string scope = scopeName();
        const bool inType = scopes_.back().kind == ScopeKind::Type;
        const size_t paren = topLevelParen(s);
        for (size_t i = 0; i < s.size() && i < paren; ++i)
            if (is(s[i], u"operator")) { // operator +, implicit operator int
                std::u16string op = u"operator";
                for (size_t k = i + 1; k < paren && k < s.size(); ++k)
                    op += s[k].text;
                add(Kind::Method, s[i], scope, withBody, std::move(op));
                return;
            }
        if (paren != std::u16string::npos) {
            const size_t n = nameBefore(s, paren);
            if (n == std::u16string::npos)
                return;
            const bool constructor = inType && s[n].text == scopes_.back().name;
            if (n > 0 && is(s[n - 1], u"~")) { // finalizer
                add(Kind::Method, s[n - 1], scope, true, u"~" + std::u16string(s[n].text));
                return;
            }
            if (!constructor && !declaresMember(s, n))
                return;
            add(inType ? Kind::Method : Kind::Function, s[n], scope, withBody);
            return;
        }
        if (!inType)
            return;
        if (is(s.back(), u"]")) // indexer: "this[int i]"
            return;
        const size_t n = nameBefore(s, s.size());
        if (n == std::u16string::npos || !declaresMember(s, n))
            return;
        const bool isEvent = std::any_of(s.begin(), s.end(), [](const Tok& t) { return t.text == u"event"; });
        add(isEvent ? Kind::Event : Kind::Property, s[n], scope);
    }

    void expressionBody()
    {
        const std::vector<Tok> s = strip(statement_);
        if (!s.empty() && memberScope())
            member(s, true);
    }

    // "=" at member scope: a field or event with an initializer (more declarators may follow).
    void assignment()
    {
        const std::vector<Tok> s = strip(statement_);
        if (s.size() < 2 || scopes_.back().kind != ScopeKind::Type || topLevelParen(s) != std::u16string::npos)
            return;
        const size_t n = nameBefore(s, s.size());
        if (n == std::u16string::npos || n != s.size() - 1 || !declaresMember(s, n))
            return;
        const bool isEvent = is(s[0], u"event");
        continuationKind_ = isEvent ? Kind::Event : Kind::Field;
        add(continuationKind_, s[n], scopeName());
        continuation_ = true;
    }

    // ";" at member scope.
    void declaration()
    {
        const std::vector<Tok> s = strip(statement_);
        if (s.empty())
            return;
        const auto first = s[0].text;
        if (first == u"using" || first == u"global" || first == u"extern")
            return;
        if (first == u"namespace") { // file-scoped: lasts to the end of the file
            namespaceDeclaration(s);
            return;
        }
        Kind kind;
        size_t name;
        bool isRecord;
        if (typeHead(s, kind, name, isRecord)) { // record R(int X);
            typeDeclaration(s, kind, name, isRecord, false);
            return;
        }
        if (first == u"delegate") {
            const size_t paren = topLevelParen(s);
            const size_t n = paren == std::u16string::npos ? paren : nameBefore(s, paren);
            if (n != std::u16string::npos)
                add(Kind::Delegate, s[n], scopeName());
            return;
        }
        if (scopes_.back().kind != ScopeKind::Type)
            return; // statements of a top-level program
        if (topLevelParen(s) != std::u16string::npos) { // abstract, interface, partial or extern method
            member(s, false);
            return;
        }
        // Fields and events: "int a, b;" "event Action A, B;"
        const Kind fieldKind = first == u"event" ? Kind::Event : Kind::Field;
        int depth = 0;
        for (size_t i = 0; i < s.size(); ++i) {
            if (is(s[i], u"<") || is(s[i], u"("))
                ++depth;
            else if (is(s[i], u">") || is(s[i], u")"))
                --depth;
            const bool end = i + 1 == s.size() || (depth == 0 && is(s[i + 1], u","));
            if (end && s[i].ident && !isKeyword(s[i]) && i > 0 && declaresMember(s, i))
                add(fieldKind, s[i], scopeName());
            else if (end && s[i].ident && !isKeyword(s[i]) && i > 0 && is(s[i - 1], u","))
                add(fieldKind, s[i], scopeName());
        }
    }

    void enumerator()
    {
        const std::vector<Tok> s = strip(statement_);
        if (!s.empty() && s[0].ident)
            add(Kind::EnumConstant, s[0], scopeName());
    }

    std::u16string_view text_;
    std::vector<uint32_t> lineStarts_;
    std::vector<Symbol> symbols_;
    std::vector<Scope> scopes_{{ScopeKind::Global, u""}};
    std::vector<Tok> statement_;
    int paren_ = 0;
    bool skipExpression_ = false;
    int depth_ = 0;
    int angle_ = 0;
    bool continuation_ = false;
    Kind continuationKind_ = Kind::Field;
};

} // namespace

std::vector<Symbol> scanCSharp(std::u16string_view text)
{
    return Scanner(text).run();
}

} // namespace vx::symbols
