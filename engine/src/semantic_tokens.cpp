#include "semantic_tokens.h"

#include <cstring>

namespace vx {

namespace {

std::string spelling(CXCursor c)
{
    CXString s = clang_getCursorSpelling(c);
    const char* p = clang_getCString(s);
    std::string r = p ? p : "";
    clang_disposeString(s);
    return r;
}

bool isFunctionLike(CXCursorKind k)
{
    return k == CXCursor_FunctionDecl || k == CXCursor_CXXMethod || k == CXCursor_Constructor ||
           k == CXCursor_Destructor || k == CXCursor_ConversionFunction || k == CXCursor_FunctionTemplate ||
           k == CXCursor_LambdaExpr;
}

uint32_t methodKind(CXCursor decl)
{
    if (clang_CXXMethod_isStatic(decl))
        return VX_KIND_STATIC_METHOD;
    if (clang_CXXMethod_isVirtual(decl))
        return VX_KIND_VIRTUAL_METHOD;
    return VX_KIND_METHOD;
}

// The vx_kind for a declaration, or 0 when it is not something we color.
uint32_t classify(CXCursor decl)
{
    switch (clang_getCursorKind(decl)) {
    case CXCursor_ParmDecl:
    case CXCursor_NonTypeTemplateParameter:
        return VX_KIND_PARAMETER;
    case CXCursor_FieldDecl:
        return VX_KIND_FIELD;
    case CXCursor_VarDecl: {
        const CXCursorKind parent = clang_getCursorKind(clang_getCursorSemanticParent(decl));
        if (isFunctionLike(parent) && clang_Cursor_getStorageClass(decl) != CX_SC_Static)
            return VX_KIND_LOCAL;
        return VX_KIND_GLOBAL; // namespace scope, function-static, or static data member
    }
    case CXCursor_EnumConstantDecl:
        return VX_KIND_ENUM_CONSTANT;
    case CXCursor_Namespace:
    case CXCursor_NamespaceAlias:
        return VX_KIND_NAMESPACE;
    case CXCursor_ClassDecl:
    case CXCursor_StructDecl:
    case CXCursor_UnionDecl:
    case CXCursor_EnumDecl:
    case CXCursor_TypedefDecl:
    case CXCursor_TypeAliasDecl:
    case CXCursor_TypeAliasTemplateDecl:
    case CXCursor_ClassTemplate:
    case CXCursor_ClassTemplatePartialSpecialization:
    case CXCursor_TemplateTypeParameter:
    case CXCursor_TemplateTemplateParameter:
    case CXCursor_Constructor: // a constructor's or destructor's name is its class's name
    case CXCursor_Destructor:
        return VX_KIND_TYPE;
    case CXCursor_FunctionDecl: // includes friends; member functions are CXXMethod
        return VX_KIND_FUNCTION;
    case CXCursor_CXXMethod:
    case CXCursor_ConversionFunction:
        return methodKind(decl);
    case CXCursor_FunctionTemplate:
        switch (clang_getTemplateCursorKind(decl)) {
        case CXCursor_CXXMethod:
            return methodKind(decl);
        case CXCursor_Constructor:
            return VX_KIND_TYPE;
        default:
            return VX_KIND_FUNCTION;
        }
    default:
        return 0;
    }
}

// The declaration an identifier token refers to: itself for declarations,
// the referenced declaration for references and expressions.
CXCursor target(CXCursor c)
{
    const CXCursorKind k = clang_getCursorKind(c);
    if (clang_isDeclaration(k))
        return c;
    if (k == CXCursor_OverloadedDeclRef)
        return clang_getNumOverloadedDecls(c) > 0 ? clang_getOverloadedDecl(c, 0) : clang_getNullCursor();
    if (clang_isReference(k) || clang_isExpression(k))
        return clang_getCursorReferenced(c);
    return clang_getNullCursor();
}

// Name a token must match to be colored as `decl` (guards against tokens that
// annotateTokens attributes to an enclosing declaration, like `override`).
bool namesDeclaration(const std::string& token, CXCursor decl)
{
    std::string name = spelling(decl);
    if (!name.empty() && name[0] == '~')
        name.erase(0, 1);
    // Templates spell as "TArray<T>" in some contexts; compare the identifier part.
    if (size_t lt = name.find('<'); lt != std::string::npos)
        name.erase(lt);
    return name == token;
}

} // namespace

std::vector<vx_span> semanticTokens(CXTranslationUnit tu, const std::string& path, std::u16string_view text)
{
    std::vector<vx_span> spans;
    CXFile file = clang_getFile(tu, path.c_str());
    if (!file)
        return spans;
    size_t size = 0;
    const char* contents = clang_getFileContents(tu, file, &size);
    if (!contents)
        return spans;

    // UTF-8 byte offset -> UTF-16 offset, built by walking the parsed text.
    std::vector<uint32_t> toUtf16(size + 1, 0);
    {
        size_t byte = 0;
        uint32_t unit = 0;
        for (size_t i = 0; i < text.size() && byte < size; ++i) {
            char32_t c = text[i];
            const bool pair = c >= 0xD800 && c <= 0xDBFF && i + 1 < text.size();
            const size_t bytes = c < 0x80 ? 1 : c < 0x800 ? 2 : pair ? 4 : 3;
            for (size_t b = 0; b < bytes && byte + b <= size; ++b)
                toUtf16[byte + b] = unit;
            byte += bytes;
            unit += pair ? 2 : 1;
            if (pair)
                ++i;
        }
        for (size_t b = byte; b <= size; ++b)
            toUtf16[b] = unit;
    }

    CXSourceRange range = clang_getRange(clang_getLocationForOffset(tu, file, 0),
                                         clang_getLocationForOffset(tu, file, static_cast<unsigned>(size)));
    CXToken* tokens = nullptr;
    unsigned count = 0;
    clang_tokenize(tu, range, &tokens, &count);
    if (!tokens)
        return spans;
    std::vector<CXCursor> cursors(count);
    clang_annotateTokens(tu, tokens, count, cursors.data());

    for (unsigned i = 0; i < count; ++i) {
        if (clang_getTokenKind(tokens[i]) != CXToken_Identifier)
            continue;
        const CXCursorKind k = clang_getCursorKind(cursors[i]);
        if (clang_isPreprocessing(k) || clang_isInvalid(k))
            continue; // macro names and expansions are colored lexically
        CXCursor decl = target(cursors[i]);
        if (clang_Cursor_isNull(decl))
            continue;
        const uint32_t kind = classify(decl);
        if (!kind)
            continue;
        CXString tokenSpelling = clang_getTokenSpelling(tu, tokens[i]);
        const std::string token = clang_getCString(tokenSpelling);
        clang_disposeString(tokenSpelling);
        if (!namesDeclaration(token, decl))
            continue;

        unsigned offset = 0;
        clang_getSpellingLocation(clang_getTokenLocation(tu, tokens[i]), nullptr, nullptr, nullptr, &offset);
        if (offset + token.size() > size)
            continue;
        const uint32_t start = toUtf16[offset];
        const uint32_t end = toUtf16[offset + token.size()];
        if (end > start)
            spans.push_back(vx_span{start, end - start, kind});
    }
    clang_disposeTokens(tu, tokens, count);
    return spans;
}

} // namespace vx
