// libclang session internals shared by clang_session.cpp (parsing, macro queries, Go to Definition)
// and refactor_queries.cpp (what is at the caret, Find References): the translation units of open
// files, argument discovery and locations.
#pragma once

#include "vx/vx.h"

#include "macro_expand.h"
#include "project_args.h"

#include <clang-c/Index.h>

#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace vx::clangs {

std::string takeString(CXString s);
std::filesystem::path moduleDirectory();
// libclang.dll is delay-loaded; false if it cannot be loaded.
bool ensureLibclang();

struct TokenInfo {
    std::string text;
    CXTokenKind kind;
    unsigned startOffset;
    unsigned endOffset;
};

std::unordered_map<std::string, std::vector<unsigned>> scanUndefs(const char* text, size_t size);

struct Unit {
    // One #define of a name and the range of the TU where it is in effect.
    struct Definition {
        CXCursor cursor;
        bool inMainFile;
        unsigned offset;      // of the definition in its file
        unsigned undefOffset; // of a later #undef in the same file, or UINT_MAX
    };

    CXTranslationUnit tu = nullptr;
    std::string path;               // UTF-8
    std::vector<std::string> args;  // as passed to libclang
    bool fullArgv = false;          // args[0] is the compiler (from compile_commands.json)
    std::u16string text;            // current text of the main file
    std::unordered_map<std::string, std::vector<Definition>> definitions; // in preprocessing order
    std::unordered_map<std::string, std::unique_ptr<vx::MacroDef>> parsedDefs;
    bool definitionsBuilt = false;
    std::vector<vx_span> semantic;
    bool semanticBuilt = false;

    ~Unit()
    {
        if (tu)
            clang_disposeTranslationUnit(tu);
    }

    void invalidate()
    {
        definitions.clear();
        parsedDefs.clear();
        definitionsBuilt = false;
        semantic.clear();
        semanticBuilt = false;
    }

    void buildDefinitions()
    {
        if (definitionsBuilt)
            return;
        definitionsBuilt = true;
        struct Context {
            Unit* unit;
            CXFile mainFile;
            std::unordered_map<void*, std::unordered_map<std::string, std::vector<unsigned>>> undefs;
        } ctx{this, clang_getFile(tu, path.c_str()), {}};
        clang_visitChildren(
            clang_getTranslationUnitCursor(tu),
            [](CXCursor c, CXCursor, CXClientData data) {
                if (clang_getCursorKind(c) != CXCursor_MacroDefinition)
                    return CXChildVisit_Continue;
                auto& ctx = *static_cast<Context*>(data);
                CXFile file = nullptr;
                unsigned offset = 0;
                clang_getSpellingLocation(clang_getCursorLocation(c), &file, nullptr, nullptr, &offset);
                std::string name = takeString(clang_getCursorSpelling(c));
                unsigned undefOffset = UINT_MAX;
                if (file) {
                    auto [it, inserted] = ctx.undefs.try_emplace(file);
                    if (inserted) {
                        size_t size = 0;
                        if (const char* contents = clang_getFileContents(ctx.unit->tu, file, &size))
                            it->second = scanUndefs(contents, size);
                    }
                    if (auto u = it->second.find(name); u != it->second.end()) {
                        for (unsigned o : u->second) {
                            if (o > offset) {
                                undefOffset = o;
                                break;
                            }
                        }
                    }
                }
                const bool inMain = file && ctx.mainFile && clang_File_isEqual(file, ctx.mainFile);
                ctx.unit->definitions[std::move(name)].push_back({c, inMain, offset, undefOffset});
                return CXChildVisit_Continue;
            },
            &ctx);
    }

    std::vector<TokenInfo> tokenize(CXSourceRange range)
    {
        CXToken* tokens = nullptr;
        unsigned count = 0;
        clang_tokenize(tu, range, &tokens, &count);
        std::vector<TokenInfo> out;
        out.reserve(count);
        for (unsigned i = 0; i < count; ++i) {
            CXSourceRange ext = clang_getTokenExtent(tu, tokens[i]);
            unsigned s = 0, e = 0;
            clang_getSpellingLocation(clang_getRangeStart(ext), nullptr, nullptr, nullptr, &s);
            clang_getSpellingLocation(clang_getRangeEnd(ext), nullptr, nullptr, nullptr, &e);
            out.push_back({takeString(clang_getTokenSpelling(tu, tokens[i])), clang_getTokenKind(tokens[i]), s, e});
        }
        clang_disposeTokens(tu, tokens, count);
        return out;
    }

    static std::vector<vx::PPToken> toPP(const std::vector<TokenInfo>& toks, size_t begin)
    {
        std::vector<vx::PPToken> out;
        for (size_t i = begin; i < toks.size(); ++i) {
            if (toks[i].kind == CXToken_Comment)
                continue;
            vx::PPToken t;
            t.text = toks[i].text;
            t.ident = toks[i].kind == CXToken_Identifier || toks[i].kind == CXToken_Keyword;
            t.spaceBefore = i > begin && toks[i].startOffset > toks[i - 1].endOffset;
            out.push_back(std::move(t));
        }
        return out;
    }

    const vx::MacroDef* definitionFor(CXCursor def)
    {
        const std::string name = takeString(clang_getCursorSpelling(def));
        auto& slot = parsedDefs[name + "@" + std::to_string(clang_hashCursor(def))];
        if (slot)
            return slot.get();
        auto toks = tokenize(clang_getCursorExtent(def));
        auto md = std::make_unique<vx::MacroDef>();
        size_t bodyStart = 1; // toks[0] is the macro name
        // Function-like iff '(' touches the name. (clang_Cursor_isMacroFunctionLike
        // answers false once the macro has been #undef'd later in the TU.)
        if (toks.size() > 1 && toks[1].text == "(" && toks[1].startOffset == toks[0].endOffset) {
            md->functionLike = true;
            size_t i = 2; // skip name and '('
            for (; i < toks.size() && toks[i].text != ")"; ++i) {
                if (toks[i].text == ",")
                    continue;
                if (toks[i].text == "...") {
                    md->variadic = true;
                    md->params.push_back("__VA_ARGS__");
                } else if (i + 1 < toks.size() && toks[i + 1].text == "...") {
                    md->variadic = true; // GNU named variadic: args...
                    md->params.push_back(toks[i].text);
                    ++i;
                } else {
                    md->params.push_back(toks[i].text);
                }
            }
            bodyStart = i + 1;
        }
        md->body = toPP(toks, bodyStart);
        if (!md->body.empty())
            md->body.front().spaceBefore = false;
        slot = std::move(md);
        return slot.get();
    }

    // Source text of a cursor's extent, read from libclang's view of the file.
    std::string extentText(CXCursor c)
    {
        CXSourceRange r = clang_getCursorExtent(c);
        CXFile file = nullptr;
        unsigned s = 0, e = 0;
        clang_getSpellingLocation(clang_getRangeStart(r), &file, nullptr, nullptr, &s);
        clang_getSpellingLocation(clang_getRangeEnd(r), nullptr, nullptr, nullptr, &e);
        size_t size = 0;
        const char* contents = file ? clang_getFileContents(tu, file, &size) : nullptr;
        if (!contents || e > size || s > e)
            return {};
        return std::string(contents + s, e - s);
    }
};


// g_mutex guards g_index and the map; each slot's own mutex guards its translation
// unit, so a long parse of one file never blocks queries on another, and queries on
// a file that is being (re)parsed return immediately instead of waiting.
struct Slot {
    std::mutex mutex;
    std::unique_ptr<Unit> unit;
};

extern std::mutex g_mutex;
extern CXIndex g_index;

// The slot of an open file's translation unit (created on demand when create).
std::shared_ptr<Slot> findSlot(const std::string& path8, bool create);
std::u16string readFile16(const std::string& path);
std::filesystem::path widePath(const std::string& path8);

struct ParseArgs {
    std::vector<std::string> args; // everything passed to libclang except an -include-pch
    bool fullArgv = false;         // args[0] is the compiler
    vx_args_source source = VX_ARGS_NONE;
    vx::VcxprojArgs project;       // when source == VX_ARGS_VCXPROJ
};

// The arguments vx_parse uses for a file (explicit, compile_commands.json, discovered or defaults).
ParseArgs computeArgs(const std::string& path8, const char16_t* const* args, uint32_t argc, const char16_t* compdb_dir);

struct Target {
    std::u16string path;
    uint32_t line = 0;
    uint32_t column = 0; // UTF-16, 1-based
    std::u16string label;
};

// A cursor's location as path, line and UTF-16 column.
bool targetOf(CXTranslationUnit tu, CXCursor c, Target& t);
// "Outer::Inner" for the semantic parents of a declaration (namespaces, classes).
std::u16string qualifiedScope(CXCursor decl);
bool sameScope(const std::u16string& symbolScope, const std::u16string& scope);
uint32_t writeOut(const std::u16string& s, char16_t* out, uint32_t cap);

} // namespace vx::clangs