#include "symbol_scanner.h"

#include "lexer.h"

#include <algorithm>

namespace vx::symbols {

namespace {

struct Tok {
    std::u16string_view text;
    uint32_t offset;
    bool ident;
};

bool is(const Tok& t, std::u16string_view s)
{
    return t.text == s;
}

bool looksLikeMacro(std::u16string_view name)
{
    if (name.size() < 2)
        return false;
    bool letter = false;
    for (char16_t c : name) {
        if (c >= u'A' && c <= u'Z')
            letter = true;
        else if (!(c == u'_' || (c >= u'0' && c <= u'9')))
            return false;
    }
    return letter;
}

bool isKeyword(std::u16string_view s)
{
    static const std::u16string_view keywords[] = {
        u"const",   u"constexpr", u"consteval", u"static",   u"inline",   u"virtual", u"explicit", u"extern",
        u"mutable", u"volatile",  u"unsigned",  u"signed",   u"return",   u"if",      u"for",      u"while",
        u"switch",  u"sizeof",    u"new",       u"delete",   u"override", u"final",   u"noexcept", u"typename",
        u"decltype", u"auto",     u"void",      u"int",      u"bool",     u"char",    u"float",    u"double",
        u"long",    u"short",     u"class",     u"struct",   u"union",    u"enum",    u"public",   u"private",
        u"protected", u"template", u"operator", u"friend",   u"using",    u"typedef", u"namespace", u"default"};
    return std::find(std::begin(keywords), std::end(keywords), s) != std::end(keywords);
}

// Index just past the bracket matching tokens[i] (one of "(", "<", "["), or tokens.size().
size_t skipGroup(const std::vector<Tok>& t, size_t i)
{
    const std::u16string_view open = t[i].text;
    const std::u16string_view close = open == u"(" ? u")" : open == u"<" ? u">" : u"]";
    int depth = 0;
    for (; i < t.size(); ++i) {
        if (t[i].text == open)
            ++depth;
        else if (t[i].text == close && --depth == 0)
            return i + 1;
    }
    return t.size();
}

// Drops what precedes the declaration proper: template<...> heads, attributes, and
// macro-like markup (UCLASS(...), UPROPERTY(...), GENERATED_BODY(), FORCEINLINE, *_API).
std::vector<Tok> strip(const std::vector<Tok>& in)
{
    size_t i = 0;
    for (;;) {
        if (i < in.size() && is(in[i], u"template") && i + 1 < in.size() && is(in[i + 1], u"<")) {
            i = skipGroup(in, i + 1);
        } else if (i + 1 < in.size() && is(in[i], u"[") && is(in[i + 1], u"[")) {
            i = skipGroup(in, i);
        } else if (i < in.size() && in[i].ident && looksLikeMacro(in[i].text)) {
            i = i + 1 < in.size() && is(in[i + 1], u"(") ? skipGroup(in, i + 1) : i + 1;
        } else {
            break;
        }
    }
    return std::vector<Tok>(in.begin() + static_cast<ptrdiff_t>(std::min(i, in.size())), in.end());
}

// UeSplit flags from a UFUNCTION(...) among the markup before a declaration.
uint8_t ueSplit(const std::vector<Tok>& t)
{
    for (size_t i = 0; i + 1 < t.size(); ++i) {
        if (!is(t[i], u"UFUNCTION") || !is(t[i + 1], u"("))
            continue;
        uint8_t flags = 0;
        const size_t end = skipGroup(t, i + 1);
        for (size_t k = i + 2; k < end; ++k) {
            if (is(t[k], u"BlueprintNativeEvent") || is(t[k], u"Server") || is(t[k], u"Client") || is(t[k], u"NetMulticast"))
                flags |= UeImplementation;
            else if (is(t[k], u"WithValidation"))
                flags |= UeValidate;
            else if (is(t[k], u"BlueprintImplementableEvent"))
                flags |= UeGenerated;
        }
        return flags;
    }
    return 0;
}

// Position of the first "(" outside <...>, or npos.
size_t topLevelParen(const std::vector<Tok>& t, size_t until = std::u16string::npos)
{
    int angle = 0;
    bool afterOperator = false; // "operator==", "operator<": symbols that are part of a name
    for (size_t i = 0; i < t.size() && i < until; ++i) {
        if (is(t[i], u"operator"))
            afterOperator = true;
        else if (afterOperator && !is(t[i], u"("))
            continue;
        else if (is(t[i], u"<"))
            ++angle;
        else if (is(t[i], u">") && angle > 0)
            --angle;
        if (is(t[i], u"(") && (angle == 0 || afterOperator))
            return i;
        if (is(t[i], u"=") && !afterOperator)
            return std::u16string::npos; // an initializer, not a parameter list
    }
    return std::u16string::npos;
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
        Lexer lexer(text_);
        for (Token t = lexer.next(); t.kind != TokenKind::End; t = lexer.next()) {
            if (t.kind == TokenKind::Directive) {
                directive(lexer, t);
                continue;
            }
            if (t.kind == TokenKind::HeaderName)
                continue;
            Tok tok{lexer.slice(t.start, t.length), t.start, t.kind == TokenKind::Identifier};
            if (tok.text.size() > 1 && (tok.text[0] == u'"' || tok.text[0] == u'\''))
                tok.ident = false;
            feed(tok);
        }
        return std::move(symbols_);
    }

private:
    enum class ScopeKind { Global, Namespace, Class, Enum, Body };
    struct Scope {
        ScopeKind kind;
        std::u16string name;
        bool keepStatement = false; // a brace group inside a statement (e.g. default argument)
    };

    void add(Kind kind, const Tok& at, std::u16string name, std::u16string scope, bool definition, uint8_t ue = 0)
    {
        const auto it = std::upper_bound(lineStarts_.begin(), lineStarts_.end(), at.offset);
        const uint32_t line = static_cast<uint32_t>(it - lineStarts_.begin());
        symbols_.push_back({std::move(name), std::move(scope), kind, definition, line, at.offset - *(it - 1) + 1, ue});
    }

    std::u16string scopeName() const
    {
        std::u16string s;
        for (const Scope& sc : scopes_) {
            if ((sc.kind == ScopeKind::Namespace || sc.kind == ScopeKind::Class || sc.kind == ScopeKind::Enum) &&
                !sc.name.empty()) {
                if (!s.empty())
                    s += u"::";
                s += sc.name;
            }
        }
        return s;
    }

    std::u16string join(std::u16string a, std::u16string_view b) const
    {
        if (b.empty())
            return a;
        if (!a.empty())
            a += u"::";
        a += b;
        return a;
    }

    void directive(Lexer& lexer, const Token& t)
    {
        const auto name = lexer.slice(t.nameStart, t.nameLength);
        // The rest of the directive line (with continuations) is not C++ declarations.
        size_t end = t.start;
        while (end < text_.size() && !(text_[end] == u'\n' && (end == 0 || text_[end - 1] != u'\\')))
            ++end;
        if (name == u"define") {
            size_t p = t.start + t.length;
            while (p < end && (text_[p] == u' ' || text_[p] == u'\t'))
                ++p;
            const size_t s = p;
            while (p < end && (isIdentChar(text_[p])))
                ++p;
            if (p > s)
                add(Kind::Macro, Tok{text_.substr(s, p - s), static_cast<uint32_t>(s), true},
                    std::u16string(text_.substr(s, p - s)), u"", true);
        }
        skipUntil_ = static_cast<uint32_t>(end);
    }

    void feed(const Tok& tok)
    {
        if (tok.offset < skipUntil_)
            return;
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
        if (is(tok, u"(")) {
            ++paren_;
        } else if (is(tok, u")") && paren_ > 0) {
            --paren_;
        }
        if (is(tok, u"{")) {
            if (paren_ > 0) { // braces inside a parameter list: skip them, keep the statement
                scopes_.push_back({ScopeKind::Body, u"", true});
                return;
            }
            open();
            statement_.clear();
            return;
        }
        if (is(tok, u"}")) {
            if (top.kind == ScopeKind::Enum)
                enumerator();
            if (scopes_.size() > 1)
                scopes_.pop_back();
            statement_.clear();
            paren_ = 0;
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
        if (is(tok, u";") && paren_ == 0) {
            declaration();
            statement_.clear();
            return;
        }
        if (is(tok, u":") && !statement_.empty() && paren_ == 0) {
            const auto last = statement_.back().text;
            if (last == u"public" || last == u"private" || last == u"protected") {
                statement_.clear(); // access label (possibly after GENERATED_BODY())
                return;
            }
        }
        statement_.push_back(tok);
    }

    // "{" ends the statement: namespace, class, enum, function body or initializer.
    void open()
    {
        const std::vector<Tok> s = strip(statement_);
        const std::u16string scope = scopeName();
        if (!s.empty() && is(s[0], u"namespace")) {
            std::u16string name;
            for (size_t i = 1; i < s.size(); ++i)
                if (s[i].ident && !looksLikeMacro(s[i].text)) {
                    name += std::u16string(s[i].text); // "A::B" nested namespace definitions
                    if (i == 1 || s[i - 1].text == u":")
                        add(Kind::Namespace, s[i], std::u16string(s[i].text), scope, true);
                } else if (is(s[i], u":")) {
                    if (!name.empty() && name.back() != u':')
                        name += u"::";
                }
            scopes_.push_back({ScopeKind::Namespace, name});
            return;
        }
        if (!s.empty() && is(s[0], u"extern") && s.size() >= 2 && s[1].text.starts_with(u"\"")) {
            scopes_.push_back({ScopeKind::Namespace, u""}); // extern "C" { ... }
            return;
        }
        const size_t paren = topLevelParen(s);
        for (size_t i = 0; i < s.size(); ++i) {
            const bool record = is(s[i], u"class") || is(s[i], u"struct") || is(s[i], u"union");
            const bool isEnum = is(s[i], u"enum");
            if (!record && !isEnum)
                continue;
            if (paren != std::u16string::npos && paren < i)
                break; // e.g. a function returning "struct X*"
            size_t n = i + 1;
            if (isEnum && n < s.size() && (is(s[n], u"class") || is(s[n], u"struct")))
                ++n;
            while (n < s.size() && ((s[n].ident && looksLikeMacro(s[n].text)) || is(s[n], u"[")))
                n = is(s[n], u"[") ? skipGroup(s, n) : n + 1; // *_API, alignas-like markup, attributes
            if (n >= s.size() || !s[n].ident || isKeyword(s[n].text)) {
                scopes_.push_back({isEnum ? ScopeKind::Enum : ScopeKind::Class, u""}); // anonymous
                return;
            }
            // A function after the type name ("struct X* F() {") is not a type definition.
            if (paren != std::u16string::npos && paren > n)
                break;
            const Kind kind = isEnum ? Kind::Enum : is(s[i], u"class") ? Kind::Class : is(s[i], u"struct") ? Kind::Struct : Kind::Union;
            add(kind, s[n], std::u16string(s[n].text), scope, true);
            scopes_.push_back({isEnum ? ScopeKind::Enum : ScopeKind::Class, std::u16string(s[n].text)});
            return;
        }
        if (paren != std::u16string::npos) {
            function(s, paren, true, ueSplit(statement_));
        } else {
            variable(s); // brace initializer
        }
        scopes_.push_back({ScopeKind::Body, u""});
    }

    // ";" ends the statement at namespace or class scope.
    void declaration()
    {
        if (delegate(statement_)) // before strip(), which would remove the DECLARE_* macro call
            return;
        std::vector<Tok> s = strip(statement_);
        if (s.empty())
            return;
        const std::u16string scope = scopeName();
        const auto first = s[0].text;
        if (first == u"friend" || first == u"static_assert" || first == u"template" || first == u"return")
            return;
        if (first == u"using") {
            if (s.size() >= 3 && s[1].ident && is(s[2], u"="))
                add(Kind::Typedef, s[1], std::u16string(s[1].text), scope, true);
            return;
        }
        if (first == u"typedef") {
            // typedef void (*Fn)(int);  or  typedef TArray<int> FIntArray;
            for (size_t i = 1; i + 2 < s.size(); ++i)
                if (is(s[i], u"(") && is(s[i + 1], u"*") && s[i + 2].ident) {
                    add(Kind::Typedef, s[i + 2], std::u16string(s[i + 2].text), scope, true);
                    return;
                }
            for (size_t i = s.size(); i-- > 1;)
                if (s[i].ident && !isKeyword(s[i].text)) {
                    add(Kind::Typedef, s[i], std::u16string(s[i].text), scope, true);
                    return;
                }
            return;
        }
        if ((first == u"class" || first == u"struct" || first == u"union" || first == u"enum") &&
            topLevelParen(s) == std::u16string::npos && std::none_of(s.begin(), s.end(), [](const Tok& t) {
                return t.text == u"=";
            }) && s.size() <= 4)
            return; // forward declaration
        const size_t paren = topLevelParen(s);
        if (paren != std::u16string::npos) {
            function(s, paren, false, ueSplit(statement_));
            return;
        }
        variable(s);
    }

    // Unreal delegate and event types: DECLARE_*DELEGATE*(Name, ...), DECLARE_*EVENT*(Owner, Name, ...).
    bool delegate(const std::vector<Tok>& raw)
    {
        if (raw.size() < 3 || !raw[0].ident || !raw[0].text.starts_with(u"DECLARE_") || !is(raw[1], u"("))
            return false;
        const bool isEvent = raw[0].text.find(u"EVENT") != std::u16string_view::npos;
        const bool isDelegate = raw[0].text.find(u"DELEGATE") != std::u16string_view::npos;
        if (!isEvent && !isDelegate)
            return false;
        size_t arg = isEvent ? 1 : 0, index = 0;
        for (size_t i = 2; i < raw.size(); ++i) {
            if (is(raw[i], u",")) {
                ++index;
                continue;
            }
            if (index == arg && raw[i].ident) {
                add(Kind::Delegate, raw[i], std::u16string(raw[i].text), scopeName(), true);
                return true;
            }
        }
        return false;
    }

    void function(const std::vector<Tok>& s, size_t paren, bool definition, uint8_t ue)
    {
        if (paren == 0)
            return;
        // operator overloads: "operator" followed by symbols up to the parameter list.
        for (size_t i = 0; i < paren; ++i) {
            if (is(s[i], u"operator")) {
                std::u16string name = u"operator";
                size_t end = paren;
                if (i + 1 == paren && paren + 2 < s.size() && is(s[paren + 1], u")"))
                    end = paren + 2; // operator()
                for (size_t k = i + 1; k < end; ++k)
                    name += s[k].text;
                emitFunction(s, i, name, definition, ue);
                return;
            }
        }
        const Tok& nameTok = s[paren - 1];
        if (!nameTok.ident || isKeyword(nameTok.text) || looksLikeMacro(nameTok.text))
            return; // e.g. a macro call, or "if ("
        std::u16string name(nameTok.text);
        size_t nameStart = paren - 1;
        if (nameStart > 0 && is(s[nameStart - 1], u"~")) {
            name.insert(name.begin(), u'~');
            --nameStart;
        }
        emitFunction(s, nameStart, name, definition, ue);
    }

    // Records a function or method whose name starts at s[nameStart]; "A::B::" before it is
    // the class of an out-of-line member definition.
    void emitFunction(const std::vector<Tok>& s, size_t nameStart, const std::u16string& name, bool definition, uint8_t ue)
    {
        std::u16string qualifier;
        size_t i = nameStart;
        while (i >= 3 && is(s[i - 1], u":") && is(s[i - 2], u":")) {
            size_t q = i - 3;
            if (is(s[q], u">")) { // Outer<T>::Name
                int depth = 0;
                for (; q > 0; --q) {
                    if (is(s[q], u">"))
                        ++depth;
                    else if (is(s[q], u"<") && --depth == 0)
                        break;
                }
                if (q == 0)
                    break;
                --q;
            }
            if (!s[q].ident)
                break;
            qualifier = qualifier.empty() ? std::u16string(s[q].text) : std::u16string(s[q].text) + u"::" + qualifier;
            i = q;
        }
        const bool inClass = scopes_.back().kind == ScopeKind::Class;
        const Kind kind = inClass || !qualifier.empty() ? Kind::Method : Kind::Function;
        add(kind, s[nameStart], name, join(scopeName(), qualifier), definition, ue);
    }

    void variable(const std::vector<Tok>& s)
    {
        // The declared name: the last identifier before "=", "[", ":" (bit field) or the end.
        size_t end = s.size();
        for (size_t i = 0; i < s.size(); ++i)
            if (is(s[i], u"=") || is(s[i], u"[") || (is(s[i], u":") && !(i + 1 < s.size() && is(s[i + 1], u":")) &&
                                                     !(i > 0 && is(s[i - 1], u":")))) {
                end = i;
                break;
            }
        if (end < 2)
            return; // needs at least a type and a name
        const Tok& name = s[end - 1];
        if (!name.ident || isKeyword(name.text) || looksLikeMacro(name.text))
            return;
        const bool inClass = scopes_.back().kind == ScopeKind::Class;
        add(inClass ? Kind::Field : Kind::Variable, name, std::u16string(name.text), scopeName(), true);
    }

    // An enumerator is the first identifier of its entry (ALL_CAPS ones included; UMETA(...)
    // and initializers follow it).
    void enumerator()
    {
        if (!statement_.empty() && statement_[0].ident)
            add(Kind::EnumConstant, statement_[0], std::u16string(statement_[0].text), scopeName(), true);
    }

    std::u16string_view text_;
    std::vector<uint32_t> lineStarts_;
    std::vector<Symbol> symbols_;
    std::vector<Scope> scopes_{{ScopeKind::Global, u""}};
    std::vector<Tok> statement_;
    int paren_ = 0;
    uint32_t skipUntil_ = 0;
};

} // namespace

std::vector<Symbol> scan(std::u16string_view text)
{
    return Scanner(text).run();
}

} // namespace vx::symbols
