// What is at the caret, for refactorings (vx_cursor_info, vx_class_members): the declaration a
// position names or is inside of, with what generating its definition, declaration or accessors
// takes (the return type with nested types qualified, parameters without default arguments,
// qualifiers, the body's extent, the scope, Unreal's UFUNCTION split).
#include "vx/vx.h"

#include "clang_internal.h"
#include "symbol_index.h"
#include "unicode.h"

#include <algorithm>
#include <regex>
#include <string>
#include <vector>

using namespace vx::clangs;

namespace {

// A location as path, line and UTF-16 column (1-based).
struct Pos {
    std::u16string path;
    uint32_t line = 0;
    uint32_t column = 0;
    unsigned offset = 0; // bytes, in its file
    bool valid = false;
};

// Where macro expansions put things in the file being edited ("FORCEINLINE int32 F()" starts at
// FORCEINLINE, not in the header that defines it).
CXSourceLocation fileLoc(CXTranslationUnit tu, CXSourceLocation l)
{
    CXFile file = nullptr;
    unsigned offset = 0;
    clang_getExpansionLocation(l, &file, nullptr, nullptr, &offset);
    return file ? clang_getLocationForOffset(tu, file, offset) : l;
}

CXSourceRange extentOf(CXTranslationUnit tu, CXCursor c)
{
    const CXSourceRange r = clang_getCursorExtent(c);
    return clang_getRange(fileLoc(tu, clang_getRangeStart(r)), fileLoc(tu, clang_getRangeEnd(r)));
}

CXSourceLocation locationOf(CXTranslationUnit tu, CXCursor c)
{
    return fileLoc(tu, clang_getCursorLocation(c));
}

Pos locate(CXTranslationUnit tu, CXSourceLocation loc)
{
    loc = fileLoc(tu, loc);
    Pos p;
    CXFile file = nullptr;
    unsigned line = 0, column = 0;
    clang_getSpellingLocation(loc, &file, &line, &column, &p.offset);
    if (!file || line == 0)
        return p;
    p.path = vx::toUtf16(takeString(clang_getFileName(file)));
    p.line = line;
    size_t size = 0;
    const char* contents = clang_getFileContents(tu, file, &size);
    if (contents && p.offset <= size && column >= 1 && p.offset >= column - 1)
        p.column = static_cast<uint32_t>(vx::toUtf16(std::string_view(contents + p.offset - (column - 1), column - 1)).size()) + 1;
    else
        p.column = column;
    p.valid = true;
    return p;
}

std::u16string num(uint32_t n)
{
    return vx::toUtf16(std::to_string(n));
}

std::u16string where(const Pos& p)
{
    return p.path + u"\t" + num(p.line) + u"\t" + num(p.column);
}

// Byte range of a cursor's extent in its own file, and the file's text.
struct Source {
    const char* text = nullptr;
    size_t size = 0;
    unsigned start = 0, end = 0;
    bool valid() const { return text && start <= end && end <= size; }
    std::string slice(unsigned from, unsigned to) const
    {
        return valid() && from <= to && to <= size ? std::string(text + from, to - from) : std::string();
    }
};

Source sourceOf(CXTranslationUnit tu, CXSourceRange range)
{
    Source s;
    CXFile file = nullptr;
    clang_getSpellingLocation(clang_getRangeStart(range), &file, nullptr, nullptr, &s.start);
    clang_getSpellingLocation(clang_getRangeEnd(range), nullptr, nullptr, nullptr, &s.end);
    if (file)
        s.text = clang_getFileContents(tu, file, &s.size);
    return s;
}

std::string collapse(const std::string& s)
{
    std::string out;
    bool space = false;
    for (char c : s) {
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
            space = !out.empty();
            continue;
        }
        if (space)
            out.push_back(' ');
        space = false;
        out.push_back(c);
    }
    return out;
}

// Removes what a declaration may say that a definition must not (or need not) repeat.
std::string stripSpecifiers(std::string s)
{
    static const std::regex specifiers(
        R"(\b(virtual|static|inline|explicit|extern|friend|FORCEINLINE|FORCENOINLINE|FORCEINLINE_DEBUGGABLE|UE_NODISCARD|UE_NODISCARD_CTOR|[A-Z0-9_]+_API)\b)");
    static const std::regex attributes(R"(\[\[[^\]]*\]\])");
    s = std::regex_replace(s, attributes, "");
    s = std::regex_replace(s, specifiers, "");
    return collapse(s);
}

bool isFunctionKind(CXCursorKind k)
{
    return k == CXCursor_FunctionDecl || k == CXCursor_CXXMethod || k == CXCursor_Constructor ||
           k == CXCursor_Destructor || k == CXCursor_ConversionFunction || k == CXCursor_FunctionTemplate;
}

bool isRecordKind(CXCursorKind k)
{
    return k == CXCursor_ClassDecl || k == CXCursor_StructDecl || k == CXCursor_UnionDecl || k == CXCursor_ClassTemplate ||
           k == CXCursor_ClassTemplatePartialSpecialization;
}

const char16_t* kindName(CXCursorKind k)
{
    switch (k) {
    case CXCursor_FunctionDecl: return u"function";
    case CXCursor_FunctionTemplate: return u"function";
    case CXCursor_CXXMethod: return u"method";
    case CXCursor_Constructor: return u"constructor";
    case CXCursor_Destructor: return u"destructor";
    case CXCursor_ConversionFunction: return u"method";
    case CXCursor_FieldDecl: return u"field";
    case CXCursor_VarDecl: return u"variable";
    case CXCursor_ParmDecl: return u"parameter";
    case CXCursor_ClassDecl: case CXCursor_ClassTemplate: return u"class";
    case CXCursor_StructDecl: return u"struct";
    case CXCursor_UnionDecl: return u"union";
    case CXCursor_EnumDecl: return u"enum";
    case CXCursor_EnumConstantDecl: return u"enumerator";
    case CXCursor_TypedefDecl: case CXCursor_TypeAliasDecl: return u"typedef";
    case CXCursor_Namespace: return u"namespace";
    case CXCursor_MacroDefinition: return u"macro";
    default: return u"other";
    }
}

// Converts a UTF-16 (line, column) of the unit's main file to a cursor.
CXCursor cursorAt(Unit& unit, uint32_t line, uint32_t column, unsigned* offsetOut = nullptr)
{
    size_t lineStart = 0;
    for (uint32_t l = 1; l < line; ++l) {
        lineStart = unit.text.find(u'\n', lineStart);
        if (lineStart == std::u16string::npos)
            return clang_getNullCursor();
        ++lineStart;
    }
    const auto prefix = std::u16string_view(unit.text).substr(lineStart, column - 1);
    const unsigned byteColumn = static_cast<unsigned>(vx::toUtf8(prefix).size()) + 1;
    CXFile file = clang_getFile(unit.tu, unit.path.c_str());
    if (!file)
        return clang_getNullCursor();
    CXSourceLocation loc = clang_getLocation(unit.tu, file, line, byteColumn);
    if (offsetOut)
        clang_getSpellingLocation(loc, nullptr, nullptr, nullptr, offsetOut);
    return clang_getCursor(unit.tu, loc);
}

// The innermost function definition, or else class, whose extent in the main file holds `offset`.
CXCursor enclosing(Unit& unit, unsigned offset, bool functions)
{
    struct Ctx {
        CXTranslationUnit tu;
        unsigned offset;
        bool functions;
        CXCursor found;
    } ctx{unit.tu, offset, functions, clang_getNullCursor()};
    clang_visitChildren(
        clang_getTranslationUnitCursor(unit.tu),
        [](CXCursor c, CXCursor, CXClientData data) {
            auto& ctx = *static_cast<Ctx*>(data);
            if (!clang_Location_isFromMainFile(clang_getCursorLocation(c)))
                return CXChildVisit_Continue;
            const CXCursorKind k = clang_getCursorKind(c);
            const bool container = k == CXCursor_Namespace || k == CXCursor_LinkageSpec || isRecordKind(k);
            if (!container && !isFunctionKind(k))
                return CXChildVisit_Continue;
            CXSourceRange r = extentOf(ctx.tu, c);
            unsigned s = 0, e = 0;
            clang_getSpellingLocation(clang_getRangeStart(r), nullptr, nullptr, nullptr, &s);
            clang_getSpellingLocation(clang_getRangeEnd(r), nullptr, nullptr, nullptr, &e);
            if (ctx.offset < s || ctx.offset > e)
                return CXChildVisit_Continue;
            if (ctx.functions ? isFunctionKind(k) && clang_isCursorDefinition(c) : isRecordKind(k) && clang_isCursorDefinition(c))
                ctx.found = c;
            return container ? CXChildVisit_Recurse : CXChildVisit_Continue;
        },
        &ctx);
    return ctx.found;
}

// The declaration a position is about: the one named there, else the function (or class) it is in.
CXCursor entityAt(Unit& unit, uint32_t line, uint32_t column)
{
    unsigned offset = 0;
    CXCursor c = cursorAt(unit, line, column, &offset);
    if (clang_Cursor_isNull(c))
        return c;
    CXCursorKind k = clang_getCursorKind(c);
    if (clang_isDeclaration(k) && k != CXCursor_ParmDecl && k != CXCursor_UnexposedDecl)
        return c;
    if (clang_isReference(k) || clang_isExpression(k)) {
        CXCursor r = clang_getCursorReferenced(c);
        if (!clang_Cursor_isNull(r) && !clang_isInvalid(clang_getCursorKind(r)))
            return r;
    }
    if (k == CXCursor_MacroExpansion)
        return clang_getCursorReferenced(c);
    CXCursor f = enclosing(unit, offset, true);
    return clang_Cursor_isNull(f) ? enclosing(unit, offset, false) : f;
}

struct Tok {
    std::string text;
    unsigned start, end; // bytes in the file
    CXSourceLocation loc;
};

std::vector<Tok> tokensOf(CXTranslationUnit tu, CXSourceRange range)
{
    CXToken* tokens = nullptr;
    unsigned count = 0;
    clang_tokenize(tu, range, &tokens, &count);
    std::vector<Tok> out;
    for (unsigned i = 0; i < count; ++i) {
        if (clang_getTokenKind(tokens[i]) == CXToken_Comment)
            continue;
        CXSourceRange ext = clang_getTokenExtent(tu, tokens[i]);
        Tok t;
        t.text = takeString(clang_getTokenSpelling(tu, tokens[i]));
        clang_getSpellingLocation(clang_getRangeStart(ext), nullptr, nullptr, nullptr, &t.start);
        clang_getSpellingLocation(clang_getRangeEnd(ext), nullptr, nullptr, nullptr, &t.end);
        t.loc = clang_getRangeStart(ext);
        out.push_back(std::move(t));
    }
    clang_disposeTokens(tu, tokens, count);
    return out;
}

size_t matching(const std::vector<Tok>& t, size_t open)
{
    const std::string o = t[open].text, c = o == "(" ? ")" : o == "{" ? "}" : o == "[" ? "]" : ">";
    int depth = 0;
    for (size_t i = open; i < t.size(); ++i) {
        if (t[i].text == o)
            ++depth;
        else if (t[i].text == c && --depth == 0)
            return i;
    }
    return t.size();
}

// "Outer::Inner" of the classes around a declaration and "A::B" of its namespaces.
void scopesOf(CXCursor decl, std::u16string& classes, std::u16string& namespaces, bool& templated)
{
    for (CXCursor p = clang_getCursorSemanticParent(decl);
         !clang_Cursor_isNull(p) && clang_getCursorKind(p) != CXCursor_TranslationUnit; p = clang_getCursorSemanticParent(p)) {
        const CXCursorKind k = clang_getCursorKind(p);
        const std::u16string name = vx::toUtf16(takeString(clang_getCursorSpelling(p)));
        if (k == CXCursor_ClassTemplate || k == CXCursor_ClassTemplatePartialSpecialization)
            templated = true;
        if (name.empty())
            continue;
        std::u16string& into = k == CXCursor_Namespace ? namespaces : classes;
        into = into.empty() ? name : name + u"::" + into;
    }
}

// The return type as written, with a type nested in a class qualified ("FOnChanged" ->
// "UThing::FOnChanged"), since an out-of-class definition is not in the class's scope there.
std::string resultText(CXTranslationUnit tu, CXCursor f, const Source& src, unsigned nameStart)
{
    std::string text = stripSpecifiers(src.slice(src.start, nameStart));
    // Drop the class qualification of an out-of-class definition ("void UThing::" -> "void").
    while (text.size() >= 2 && text.ends_with("::")) {
        size_t i = text.size() - 2;
        while (i > 0 && (isalnum(static_cast<unsigned char>(text[i - 1])) || text[i - 1] == '_' || text[i - 1] == '>' || text[i - 1] == '<'))
            --i;
        text = collapse(text.substr(0, i));
    }
    CXType t = clang_getCursorResultType(f);
    while (t.kind == CXType_Pointer || t.kind == CXType_LValueReference || t.kind == CXType_RValueReference)
        t = clang_getPointeeType(t);
    CXCursor decl = clang_getTypeDeclaration(t);
    if (!clang_Cursor_isNull(decl) && isRecordKind(clang_getCursorKind(clang_getCursorSemanticParent(decl)))) {
        const std::string name = takeString(clang_getCursorSpelling(decl));
        const std::string scope = vx::toUtf8(qualifiedScope(decl));
        const std::regex bare("(^|[^:\\w])" + name + "\\b");
        std::smatch m;
        if (!name.empty() && std::regex_search(text, m, bare))
            text = text.substr(0, m.position(0) + m.length(1)) + scope + "::" + text.substr(m.position(0) + m.length(1));
    }
    (void)tu;
    return text;
}

// A parameter as written, without its default argument (returned in `defaultValue`) or UPARAM(...).
std::string parameterText(CXTranslationUnit tu, CXCursor arg, std::string& defaultValue)
{
    Source s = sourceOf(tu, extentOf(tu, arg));
    std::string text = s.slice(s.start, s.end);
    defaultValue.clear();
    int depth = 0;
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '(' || c == '<' || c == '[' || c == '{')
            ++depth;
        else if (c == ')' || c == '>' || c == ']' || c == '}')
            --depth;
        else if (c == '=' && depth == 0) {
            defaultValue = collapse(text.substr(i + 1));
            text.resize(i);
            break;
        }
    }
    static const std::regex uparam(R"(\bUPARAM\s*\([^)]*\))");
    return collapse(std::regex_replace(text, uparam, ""));
}

uint8_t ueFlags(const std::u16string& name, const std::u16string& scope)
{
    uint8_t flags = 0;
    for (const vx::symbols::Hit& hit : vx::symbols::lookup(name, 50, vx::symbols::Language::Cpp))
        if (!hit.symbol.definition && sameScope(hit.symbol.scope, scope))
            flags |= hit.symbol.ue;
    return flags;
}

void addRange(std::u16string& out, const char16_t* key, CXTranslationUnit tu, CXSourceLocation from, CXSourceLocation to)
{
    const Pos a = locate(tu, from), b = locate(tu, to);
    if (a.valid && b.valid)
        out += std::u16string(key) + u"\t" + where(a) + u"\t" + num(b.line) + u"\t" + num(b.column) + u"\n";
}

void describeFunction(Unit& unit, CXCursor f, std::u16string& out)
{
    CXTranslationUnit tu = unit.tu;
    const Source src = sourceOf(tu, extentOf(tu, f));
    std::vector<Tok> toks = tokensOf(tu, extentOf(tu, f));
    unsigned nameOffset = 0;
    clang_getSpellingLocation(locationOf(tu, f), nullptr, nullptr, nullptr, &nameOffset);
    size_t name = 0;
    while (name < toks.size() && toks[name].start < nameOffset)
        ++name;
    // The parameter list: the first "(" after the name ("operator()" has its own first).
    size_t open = name;
    while (open < toks.size() && toks[open].text != "(")
        ++open;
    if (open + 1 < toks.size() && toks[open + 1].text == ")" && open + 2 < toks.size() && toks[open + 2].text == "(" &&
        toks[name].text == "operator")
        open += 2;
    const size_t close = open < toks.size() ? matching(toks, open) : toks.size();

    const CXCursorKind kind = clang_getCursorKind(f);
    if (kind != CXCursor_Constructor && kind != CXCursor_Destructor)
        out += u"result\t" + vx::toUtf16(resultText(tu, f, src, name < toks.size() ? toks[name].start : src.start)) + u"\n";
    const int args = clang_Cursor_getNumArguments(f);
    for (int i = 0; i < args; ++i) {
        CXCursor a = clang_Cursor_getArgument(f, static_cast<unsigned>(i));
        std::string defaultValue;
        const std::string text = parameterText(tu, a, defaultValue);
        out += u"param\t" + vx::toUtf16(text) + u"\t" + vx::toUtf16(takeString(clang_getCursorSpelling(a))) +
               (defaultValue.empty() ? u"" : u"\t" + vx::toUtf16(defaultValue)) + u"\n";
    }
    if (clang_Cursor_isVariadic(f))
        out += u"param\t...\t\n";

    // After the parameters: qualifiers to repeat, then (in a definition) the body.
    std::string qualifiers;
    size_t i = close + 1;
    for (; i < toks.size(); ++i) {
        const std::string& t = toks[i].text;
        if (t == "const" || t == "volatile" || t == "&" || t == "&&") {
            qualifiers += (qualifiers.empty() ? "" : " ") + t;
        } else if (t == "noexcept" || t == "throw") {
            std::string q = t;
            if (i + 1 < toks.size() && toks[i + 1].text == "(") {
                const size_t e = matching(toks, i + 1);
                q += src.slice(toks[i + 1].start, e < toks.size() ? toks[e].end : toks[i + 1].end);
                i = e;
            }
            qualifiers += (qualifiers.empty() ? "" : " ") + q;
        } else if (t == "->") { // trailing return type
            size_t e = i + 1;
            while (e < toks.size() && toks[e].text != "{" && toks[e].text != ":" && toks[e].text != "=" &&
                   toks[e].text != "override" && toks[e].text != "final" && toks[e].text != ";")
                ++e;
            qualifiers += (qualifiers.empty() ? "" : " ") + collapse(src.slice(toks[i].start, toks[e - 1].end));
            i = e - 1;
        } else if (t == "override" || t == "final") {
            continue;
        } else if (t == "=") {
            out += u"pure\t1\n"; // = 0, = default, = delete
            break;
        } else if (t == "{" || t == ":" || t == "try") {
            addRange(out, u"body", tu, toks[i].loc, clang_getRangeEnd(extentOf(tu, f)));
            break;
        } else if (t.find('(') == std::string::npos && !t.empty() && isupper(static_cast<unsigned char>(t[0]))) {
            // PURE_VIRTUAL(...) and other macros after the declarator.
            if (i + 1 < toks.size() && toks[i + 1].text == "(") {
                if (t == "PURE_VIRTUAL")
                    out += u"pure\t1\n";
                i = matching(toks, i + 1);
            }
        }
    }
    if (!qualifiers.empty())
        out += u"qualifiers\t" + vx::toUtf16(qualifiers) + u"\n";
    if (clang_CXXMethod_isStatic(f))
        out += u"static\t1\n";
    if (clang_CXXMethod_isVirtual(f))
        out += u"virtual\t1\n";
    if (kind == CXCursor_FunctionTemplate)
        out += u"template\t1\n";
}

std::u16string describe(Unit& unit, CXCursor e, uint32_t line, uint32_t column)
{
    CXTranslationUnit tu = unit.tu;
    std::u16string out;
    const CXCursorKind kind = clang_getCursorKind(e);
    const std::u16string name = vx::toUtf16(takeString(clang_getCursorSpelling(e)));
    out += u"kind\t" + std::u16string(kindName(kind)) + u"\n";
    out += u"name\t" + name + u"\n";
    out += u"usr\t" + vx::toUtf16(takeString(clang_getCursorUSR(e))) + u"\n";
    const std::u16string scope = qualifiedScope(e);
    out += u"scope\t" + scope + u"\n";
    std::u16string classes, namespaces;
    bool templated = false;
    scopesOf(e, classes, namespaces, templated);
    out += u"classes\t" + classes + u"\n";
    out += u"namespaces\t" + namespaces + u"\n";
    if (templated)
        out += u"classtemplate\t1\n";

    // Where it is declared and defined (as far as this translation unit sees).
    const CXCursor canonical = clang_getCanonicalCursor(e);
    addRange(out, u"decl", tu, locationOf(tu, canonical), clang_getRangeEnd(extentOf(tu, canonical)));
    addRange(out, u"declextent", tu, clang_getRangeStart(extentOf(tu, canonical)), clang_getRangeEnd(extentOf(tu, canonical)));
    const CXCursor def = clang_getCursorDefinition(e);
    if (!clang_Cursor_isNull(def)) {
        addRange(out, u"def", tu, locationOf(tu, def), clang_getRangeEnd(extentOf(tu, def)));
        addRange(out, u"defextent", tu, clang_getRangeStart(extentOf(tu, def)), clang_getRangeEnd(extentOf(tu, def)));
    }
    // Is the position on (or in) the definition itself?
    if (!clang_Cursor_isNull(def)) {
        const Pos d = locate(tu, clang_getRangeStart(extentOf(tu, def)));
        const Pos dEnd = locate(tu, clang_getRangeEnd(extentOf(tu, def)));
        const Pos here{vx::toUtf16(unit.path), line, column};
        auto key = [](const std::u16string& p) {
            std::u16string k = p;
            for (auto& c : k)
                c = c == u'/' ? u'\\' : static_cast<char16_t>(towlower(c));
            return k;
        };
        if (d.valid && key(d.path) == key(here.path) && (line > d.line || (line == d.line && column >= d.column)) &&
            (line < dEnd.line || (line == dEnd.line && column <= dEnd.column)))
            out += u"atdef\t1\n";
    }
    if (clang_equalCursors(clang_getCanonicalCursor(def), def) && !clang_Cursor_isNull(def))
        out += u"defonly\t1\n"; // no separate declaration

    const CXCursor parent = clang_getCursorSemanticParent(e);
    if (isRecordKind(clang_getCursorKind(parent))) {
        const CXCursor parentDef = clang_getCursorDefinition(parent);
        if (!clang_Cursor_isNull(parentDef)) {
            addRange(out, u"class", tu, clang_getRangeStart(extentOf(tu, parentDef)), clang_getRangeEnd(extentOf(tu, parentDef)));
            out += u"classname\t" + vx::toUtf16(takeString(clang_getCursorSpelling(parentDef))) + u"\n";
        }
    }

    if (isFunctionKind(kind)) {
        describeFunction(unit, e, out);
        out += u"ue\t" + num(ueFlags(name, scope)) + u"\n";
    } else if (kind == CXCursor_FieldDecl || kind == CXCursor_VarDecl) {
        const Source src = sourceOf(tu, extentOf(tu, e));
        unsigned nameOffset = 0;
        clang_getSpellingLocation(locationOf(tu, e), nullptr, nullptr, nullptr, &nameOffset);
        out += u"type\t" + vx::toUtf16(stripSpecifiers(std::regex_replace(src.slice(src.start, nameOffset), std::regex(R"(\bmutable\b)"), ""))) + u"\n";
        out += u"typespelling\t" + vx::toUtf16(takeString(clang_getTypeSpelling(clang_getCursorType(e)))) + u"\n";
        const CXType t = clang_getCanonicalType(clang_getCursorType(e));
        out += u"pod\t" + std::u16string(t.kind >= CXType_FirstBuiltin && t.kind <= CXType_LastBuiltin || t.kind == CXType_Pointer || t.kind == CXType_Enum ? u"1" : u"0") + u"\n";
        if (clang_getCursorKind(parent) != CXCursor_TranslationUnit && clang_Cursor_getStorageClass(e) == CX_SC_Static)
            out += u"static\t1\n";
    } else if (kind == CXCursor_EnumDecl) {
        out += u"enumscoped\t" + std::u16string(clang_EnumDecl_isScoped(e) ? u"1" : u"0") + u"\n";
        const CXType base = clang_getEnumDeclIntegerType(e);
        out += u"enumbase\t" + vx::toUtf16(takeString(clang_getTypeSpelling(base))) + u"\n";
    }
    if (isRecordKind(kind)) {
        const CXCursor d = clang_getCursorDefinition(e);
        if (!clang_Cursor_isNull(d))
            addRange(out, u"class", tu, clang_getRangeStart(extentOf(tu, d)), clang_getRangeEnd(extentOf(tu, d)));
    }
    return out;
}

// Runs `f` on the open file's unit; `wait` blocks while it is being parsed, otherwise gives up.
template <typename F>
uint32_t withUnit(const char16_t* path, bool wait, F f)
{
    if (!path || !ensureLibclang())
        return 0;
    std::shared_ptr<Slot> holder = findSlot(vx::toUtf8(path), false);
    if (!holder)
        return 0;
    std::unique_lock lock(holder->mutex, std::defer_lock);
    if (wait)
        lock.lock();
    else if (!lock.try_lock())
        return 0;
    if (!holder->unit || !holder->unit->tu)
        return 0;
    return f(*holder->unit);
}

// The innermost if / for / while / do / range-for whose extent in the main file holds `offset`.
CXCursor statementAt(Unit& unit, unsigned offset)
{
    struct Ctx {
        CXTranslationUnit tu;
        unsigned offset;
        CXCursor found;
    } ctx{unit.tu, offset, clang_getNullCursor()};
    clang_visitChildren(
        clang_getTranslationUnitCursor(unit.tu),
        [](CXCursor c, CXCursor, CXClientData data) {
            auto& ctx = *static_cast<Ctx*>(data);
            if (!clang_Location_isFromMainFile(clang_getCursorLocation(c)))
                return CXChildVisit_Continue;
            CXSourceRange r = extentOf(ctx.tu, c);
            unsigned s = 0, e = 0;
            clang_getSpellingLocation(clang_getRangeStart(r), nullptr, nullptr, nullptr, &s);
            clang_getSpellingLocation(clang_getRangeEnd(r), nullptr, nullptr, nullptr, &e);
            if (ctx.offset < s || ctx.offset > e)
                return CXChildVisit_Continue;
            const CXCursorKind k = clang_getCursorKind(c);
            if (k == CXCursor_IfStmt || k == CXCursor_ForStmt || k == CXCursor_WhileStmt || k == CXCursor_DoStmt || k == CXCursor_CXXForRangeStmt)
                ctx.found = c;
            return CXChildVisit_Recurse;
        },
        &ctx);
    return ctx.found;
}

std::vector<CXCursor> childrenOf(CXCursor c)
{
    std::vector<CXCursor> children;
    clang_visitChildren(
        c,
        [](CXCursor child, CXCursor, CXClientData data) {
            static_cast<std::vector<CXCursor>*>(data)->push_back(child);
            return CXChildVisit_Continue;
        },
        &children);
    return children;
}

unsigned startOf(CXTranslationUnit tu, CXCursor c)
{
    unsigned s = 0;
    clang_getSpellingLocation(clang_getRangeStart(extentOf(tu, c)), nullptr, nullptr, nullptr, &s);
    return s;
}

// A body: its extent, whether it is a { } block and how many statements it holds, and the one
// statement's extent if it holds just one.
void describeBody(CXTranslationUnit tu, const char16_t* which, CXCursor body, std::u16string& out)
{
    const bool compound = clang_getCursorKind(body) == CXCursor_CompoundStmt;
    const std::vector<CXCursor> inner = compound ? childrenOf(body) : std::vector<CXCursor>{};
    const CXSourceRange r = extentOf(tu, body);
    const Pos a = locate(tu, clang_getRangeStart(r)), b = locate(tu, clang_getRangeEnd(r));
    if (!a.valid || !b.valid)
        return;
    out += u"body\t" + std::u16string(which) + u"\t" + num(a.line) + u"\t" + num(a.column) + u"\t" + num(b.line) + u"\t" + num(b.column) +
           u"\t" + (compound ? u"1" : u"0") + u"\t" + num(static_cast<uint32_t>(inner.size())) + u"\t" +
           std::u16string(clang_getCursorKind(body) == CXCursor_IfStmt ? u"if" : u"") + u"\n";
    if (inner.size() == 1) {
        const CXSourceRange ir = extentOf(tu, inner[0]);
        const Pos c = locate(tu, clang_getRangeStart(ir)), d = locate(tu, clang_getRangeEnd(ir));
        if (c.valid && d.valid)
            out += u"inner\t" + std::u16string(which) + u"\t" + num(c.line) + u"\t" + num(c.column) + u"\t" + num(d.line) + u"\t" + num(d.column) + u"\n";
    }
}

} // namespace

extern "C" {

VX_API uint32_t VX_CALL vx_statement_at(const char16_t* path, uint32_t line, uint32_t column, uint32_t wait, char16_t* out,
                                        uint32_t cap)
{
    if (line == 0 || column == 0)
        return 0;
    return withUnit(path, wait != 0, [&](Unit& unit) -> uint32_t {
        unsigned offset = 0;
        cursorAt(unit, line, column, &offset);
        const CXCursor stmt = statementAt(unit, offset);
        if (clang_Cursor_isNull(stmt))
            return 0;
        CXTranslationUnit tu = unit.tu;
        const CXCursorKind kind = clang_getCursorKind(stmt);
        std::u16string result = u"kind\t" + std::u16string(kind == CXCursor_IfStmt ? u"if" : kind == CXCursor_ForStmt || kind == CXCursor_CXXForRangeStmt ? u"for"
                                                           : kind == CXCursor_WhileStmt ? u"while" : u"do") + u"\n";
        // The header ends at the ")" closing the parenthesis after the keyword (do: at "do").
        const std::vector<Tok> toks = tokensOf(tu, extentOf(tu, stmt));
        if (toks.empty())
            return 0;
        size_t headerEnd = 0;
        if (kind != CXCursor_DoStmt && toks.size() > 1 && toks[1].text == "(")
            headerEnd = matching(toks, 1);
        if (headerEnd >= toks.size())
            return 0;
        const Pos start = locate(tu, toks[0].loc);
        const Pos end = locate(tu, clang_getLocationForOffset(tu, clang_getFile(tu, unit.path.c_str()), toks[headerEnd].end));
        result += u"header\t" + where(start) + u"\t" + num(end.line) + u"\t" + num(end.column) + u"\n";
        // Bodies: the children starting after the header (then and else for an if).
        std::vector<CXCursor> bodies;
        for (CXCursor child : childrenOf(stmt))
            if (startOf(tu, child) >= toks[headerEnd].end && (kind != CXCursor_DoStmt || clang_isStatement(clang_getCursorKind(child)) ||
                                                               clang_isExpression(clang_getCursorKind(child))))
                bodies.push_back(child);
        if (kind == CXCursor_DoStmt && !bodies.empty())
            bodies.resize(1); // the condition after "while" is a child too
        if (bodies.empty())
            return 0;
        describeBody(tu, kind == CXCursor_IfStmt ? u"then" : u"loop", bodies[0], result);
        if (kind == CXCursor_IfStmt && bodies.size() > 1) {
            for (const Tok& t : toks)
                if (t.text == "else" && t.start >= startOf(tu, bodies[0])) {
                    result += u"else\t" + where(locate(tu, t.loc)) + u"\n";
                    break;
                }
            describeBody(tu, u"else", bodies[1], result);
        }
        return writeOut(result, out, cap);
    });
}

VX_API uint32_t VX_CALL vx_cursor_info(const char16_t* path, uint32_t line, uint32_t column, uint32_t wait, char16_t* out,
                                       uint32_t cap)
{
    if (line == 0 || column == 0)
        return 0;
    return withUnit(path, wait != 0, [&](Unit& unit) -> uint32_t {
        const CXCursor e = entityAt(unit, line, column);
        if (clang_Cursor_isNull(e) || clang_isInvalid(clang_getCursorKind(e)))
            return 0;
        return writeOut(describe(unit, e, line, column), out, cap);
    });
}

VX_API uint32_t VX_CALL vx_class_members(const char16_t* path, uint32_t line, uint32_t column, uint32_t wait, char16_t* out,
                                         uint32_t cap)
{
    if (line == 0 || column == 0)
        return 0;
    return withUnit(path, wait != 0, [&](Unit& unit) -> uint32_t {
        unsigned offset = 0;
        CXCursor c = cursorAt(unit, line, column, &offset);
        CXCursor cls = clang_Cursor_isNull(c) ? c : clang_getCursorReferenced(c);
        if (clang_Cursor_isNull(cls) || !isRecordKind(clang_getCursorKind(cls)))
            cls = clang_getCursorSemanticParent(entityAt(unit, line, column));
        if (clang_Cursor_isNull(cls) || !isRecordKind(clang_getCursorKind(cls)))
            cls = enclosing(unit, offset, false);
        if (clang_Cursor_isNull(cls) || !isRecordKind(clang_getCursorKind(cls)))
            return 0;
        cls = clang_getCursorDefinition(cls);
        if (clang_Cursor_isNull(cls))
            return 0;
        std::u16string result = u"class\t" + qualifiedScope(cls) + (qualifiedScope(cls).empty() ? u"" : u"::") +
                                vx::toUtf16(takeString(clang_getCursorSpelling(cls))) + u"\n";
        struct Ctx {
            CXTranslationUnit tu;
            std::u16string* out;
        } ctx{unit.tu, &result};
        clang_visitChildren(
            cls,
            [](CXCursor m, CXCursor, CXClientData data) {
                auto& ctx = *static_cast<Ctx*>(data);
                if (!isFunctionKind(clang_getCursorKind(m)))
                    return CXChildVisit_Continue;
                const Pos p = locate(ctx.tu, locationOf(ctx.tu, m));
                if (p.valid)
                    *ctx.out += u"method\t" + vx::toUtf16(takeString(clang_getCursorSpelling(m))) + u"\t" + num(p.line) + u"\t" +
                                num(p.column) + u"\t" + (clang_isCursorDefinition(m) ? u"1" : u"0") + u"\n";
                return CXChildVisit_Continue;
            },
            &ctx);
        return writeOut(result, out, cap);
    });
}

} // extern "C"
