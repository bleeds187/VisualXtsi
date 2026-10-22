// libclang-backed parsing and macro queries (vx_parse, vx_macro_info, vx_release).
#include "vx/vx.h"

#include "macro_expand.h"
#include "project_args.h"
#include "semantic_tokens.h"
#include "unicode.h"

#include <clang-c/CXCompilationDatabase.h>
#include <clang-c/Index.h>

#define NOMINMAX
#include <windows.h>

#include <climits>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace fs = std::filesystem;

namespace {

std::string takeString(CXString s)
{
    const char* c = clang_getCString(s);
    std::string r = c ? c : "";
    clang_disposeString(s);
    return r;
}

fs::path moduleDirectory()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&moduleDirectory), &self);
    wchar_t buf[MAX_PATH * 4];
    DWORD n = GetModuleFileNameW(self, buf, static_cast<DWORD>(std::size(buf)));
    return fs::path(std::wstring(buf, n)).parent_path();
}

// libclang.dll is delay-loaded so that lexical classification keeps working
// without it. Prefer a copy next to vxengine.dll, then the normal search path.
bool ensureLibclang()
{
    static const bool loaded = [] {
        if (GetModuleHandleW(L"libclang.dll"))
            return true;
        const fs::path local = moduleDirectory() / L"libclang.dll";
        if (LoadLibraryExW(local.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH))
            return true;
        return LoadLibraryW(L"libclang.dll") != nullptr;
    }();
    return loaded;
}

struct TokenInfo {
    std::string text;
    CXTokenKind kind;
    unsigned startOffset;
    unsigned endOffset;
};

struct Unit;

class UnitMacroTable final : public vx::MacroTable {
public:
    // `hoverOffset` is the byte offset in the main file where expansion happens.
    UnitMacroTable(Unit& unit, unsigned hoverOffset) : unit_(unit), hoverOffset_(hoverOffset) {}
    const vx::MacroDef* find(const std::string& name) override;

private:
    Unit& unit_;
    unsigned hoverOffset_;
};

// Byte offsets of every "#undef NAME" in a file, by name. libclang's preprocessing
// record has no #undef entries, so we find them in the source text.
std::unordered_map<std::string, std::vector<unsigned>> scanUndefs(const char* text, size_t size)
{
    std::unordered_map<std::string, std::vector<unsigned>> undefs;
    auto blank = [](char c) { return c == ' ' || c == '\t'; };
    for (size_t i = 0; i < size;) {
        size_t p = i;
        while (p < size && blank(text[p]))
            ++p;
        if (p < size && text[p] == '#') {
            const size_t hash = p++;
            while (p < size && blank(text[p]))
                ++p;
            if (size - p > 5 && std::string_view(text + p, 5) == "undef" && blank(text[p + 5])) {
                p += 5;
                while (p < size && blank(text[p]))
                    ++p;
                const size_t nameStart = p;
                while (p < size && (isalnum(static_cast<unsigned char>(text[p])) || text[p] == '_'))
                    ++p;
                if (p > nameStart)
                    undefs[std::string(text + nameStart, p - nameStart)].push_back(static_cast<unsigned>(hash));
            }
        }
        const void* nl = std::memchr(text + p, '\n', size - std::min(p, size));
        i = nl ? static_cast<const char*>(nl) - text + 1 : size;
    }
    return undefs;
}

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

const vx::MacroDef* UnitMacroTable::find(const std::string& name)
{
    unit_.buildDefinitions();
    auto it = unit_.definitions.find(name);
    if (it == unit_.definitions.end())
        return nullptr;
    // The latest definition still in effect: one in an included file that was not
    // #undef'd later in that file, or one in the main file whose range covers the hover.
    for (auto d = it->second.rbegin(); d != it->second.rend(); ++d) {
        const bool live = d->inMainFile ? d->offset < hoverOffset_ && hoverOffset_ < d->undefOffset
                                        : d->undefOffset == UINT_MAX;
        if (live)
            return unit_.definitionFor(d->cursor);
    }
    return nullptr;
}

// g_mutex guards g_index and the map; each slot's own mutex guards its translation
// unit, so a long parse of one file never blocks queries on another, and queries on
// a file that is being (re)parsed return immediately instead of waiting.
struct Slot {
    std::mutex mutex;
    std::unique_ptr<Unit> unit;
};

std::mutex g_mutex;
CXIndex g_index = nullptr;
std::unordered_map<std::string, std::shared_ptr<Slot>> g_units;

std::string normalizeKey(std::string path)
{
    for (auto& c : path)
        c = c == '/' ? '\\' : static_cast<char>(tolower(static_cast<unsigned char>(c)));
    return path;
}

std::vector<std::string> defaultArgs(const std::string& path)
{
    std::string ext = fs::path(path).extension().string();
    for (auto& c : ext)
        c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
    const bool header = ext == ".h" || ext == ".hpp" || ext == ".hh" || ext == ".hxx" || ext == ".inl";
    return {"-x", header ? "c++-header" : "c++", "-std=c++20", "-fms-extensions", "-fms-compatibility",
            "-I" + fs::path(path).parent_path().string()};
}

bool argsFromCompilationDatabase(const std::string& dir, const std::string& path, std::vector<std::string>& out)
{
    CXCompilationDatabase_Error err;
    CXCompilationDatabase db = clang_CompilationDatabase_fromDirectory(dir.c_str(), &err);
    if (err != CXCompilationDatabase_NoError || !db)
        return false;
    bool found = false;
    if (CXCompileCommands cmds = clang_CompilationDatabase_getCompileCommands(db, path.c_str())) {
        if (clang_CompileCommands_getSize(cmds) > 0) {
            CXCompileCommand cmd = clang_CompileCommands_getCommand(cmds, 0);
            for (unsigned i = 0, n = clang_CompileCommand_getNumArgs(cmd); i < n; ++i)
                out.push_back(takeString(clang_CompileCommand_getArg(cmd, i)));
            found = !out.empty();
        }
        clang_CompileCommands_dispose(cmds);
    }
    clang_CompilationDatabase_dispose(db);
    return found;
}

std::u16string readFile16(const std::string& path)
{
    const std::u16string wide = vx::toUtf16(path);
    std::FILE* f = nullptr;
    if (_wfopen_s(&f, reinterpret_cast<const wchar_t*>(wide.c_str()), L"rb") != 0 || !f)
        return {};
    std::string bytes;
    char buf[65536];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0)
        bytes.append(buf, n);
    std::fclose(f);
    if (bytes.size() >= 3 && bytes.compare(0, 3, "\xEF\xBB\xBF") == 0)
        bytes.erase(0, 3);
    return vx::toUtf16(bytes);
}

std::shared_ptr<Slot> findSlot(const std::string& path8, bool create)
{
    std::lock_guard lock(g_mutex);
    if (create && !g_index)
        g_index = clang_createIndex(0, 0);
    auto& slot = g_units[normalizeKey(path8)];
    if (!slot && create)
        slot = std::make_shared<Slot>();
    return slot;
}

fs::path widePath(const std::string& path8)
{
    const std::u16string w = vx::toUtf16(path8);
    return fs::path(std::wstring(w.begin(), w.end()));
}

// Arguments for `path8`: compile_commands.json in a parent directory that lists the
// file, else the generated .vcxproj of the Unreal project (or engine) containing it.
vx_args_source discoverArgs(const std::string& path8, std::vector<std::string>& out)
{
    std::error_code ec;
    for (fs::path dir = widePath(path8).parent_path(); !dir.empty(); dir = dir.parent_path()) {
        if (fs::exists(dir / L"compile_commands.json", ec)) {
            const std::wstring w = dir.wstring();
            std::vector<std::string> args;
            if (argsFromCompilationDatabase(vx::toUtf8(std::u16string(w.begin(), w.end())), path8, args)) {
                out = std::move(args);
                return VX_ARGS_COMPILE_COMMANDS;
            }
        }
        if (dir == dir.root_path())
            break;
    }
    vx::VcxprojArgs project;
    if (vx::argsFromUnrealProject(path8, project)) {
        out = std::move(project.args);
        return VX_ARGS_VCXPROJ;
    }
    return VX_ARGS_NONE;
}

uint32_t writeOut(const std::u16string& s, char16_t* out, uint32_t cap)
{
    if (out)
        std::copy_n(s.begin(), std::min<size_t>(s.size(), cap), out);
    return static_cast<uint32_t>(s.size());
}

} // namespace

extern "C" {

VX_API int32_t VX_CALL vx_parse(const char16_t* path, const char16_t* const* args, uint32_t argc,
                                const char16_t* compdb_dir, const char16_t* unsaved, uint32_t unsaved_len)
{
    if (!path)
        return VX_ERR_ARGS;
    if (!ensureLibclang())
        return VX_ERR_NOT_AVAILABLE;
    const std::string path8 = vx::toUtf8(path);
    std::shared_ptr<Slot> holder = findSlot(path8, true);
    std::lock_guard unitLock(holder->mutex);
    std::unique_ptr<Unit>& slot = holder->unit;

    std::vector<std::string> wanted;
    bool fullArgv = false;
    if (argc > 0 && args) {
        for (uint32_t i = 0; i < argc; ++i)
            wanted.push_back(vx::toUtf8(args[i]));
    } else if (compdb_dir && argsFromCompilationDatabase(vx::toUtf8(compdb_dir), path8, wanted)) {
        fullArgv = true;
    } else {
        const vx_args_source source = compdb_dir ? VX_ARGS_NONE : discoverArgs(path8, wanted);
        fullArgv = source == VX_ARGS_COMPILE_COMMANDS;
        if (source == VX_ARGS_NONE)
            wanted = defaultArgs(path8);
    }
    // Keep going past errors: a partially broken TU still has a full preprocessing record.
    wanted.push_back("-ferror-limit=0");
    wanted.push_back("-w");
    const fs::path resourceDir = moduleDirectory() / L"clang-resource";
    if (fs::exists(resourceDir / L"include")) {
        wanted.push_back("-resource-dir");
        wanted.push_back(resourceDir.string());
    }

    std::u16string text = unsaved ? std::u16string(unsaved, unsaved_len) : readFile16(path8);
    const std::string text8 = vx::toUtf8(text);
    CXUnsavedFile file{path8.c_str(), text8.c_str(), static_cast<unsigned long>(text8.size())};
    const unsigned numUnsaved = unsaved ? 1u : 0u;

    if (slot && slot->tu && slot->args == wanted) {
        slot->invalidate();
        slot->text = std::move(text);
        if (clang_reparseTranslationUnit(slot->tu, numUnsaved, &file, clang_defaultReparseOptions(slot->tu)) == 0)
            return VX_OK;
        slot.reset(); // a failed reparse leaves the TU unusable; parse from scratch
    }

    auto unit = std::make_unique<Unit>();
    unit->path = path8;
    unit->args = wanted;
    unit->fullArgv = fullArgv;
    unit->text = std::move(text);

    std::vector<const char*> argv;
    for (auto& a : unit->args)
        argv.push_back(a.c_str());
    const unsigned options = CXTranslationUnit_DetailedPreprocessingRecord | CXTranslationUnit_PrecompiledPreamble |
                             CXTranslationUnit_CacheCompletionResults | CXTranslationUnit_CreatePreambleOnFirstParse |
                             CXTranslationUnit_KeepGoing;
    CXErrorCode err = fullArgv
        ? clang_parseTranslationUnit2FullArgv(g_index, nullptr, argv.data(), static_cast<int>(argv.size()), &file,
                                              numUnsaved, options, &unit->tu)
        : clang_parseTranslationUnit2(g_index, path8.c_str(), argv.data(), static_cast<int>(argv.size()), &file,
                                      numUnsaved, options, &unit->tu);
    if (err != CXError_Success || !unit->tu) {
        slot.reset();
        return VX_ERR_PARSE;
    }
    slot = std::move(unit);
    return VX_OK;
}

VX_API uint32_t VX_CALL vx_macro_info(const char16_t* path, uint32_t line, uint32_t column, char16_t* out,
                                      uint32_t cap)
{
    if (!path || line == 0 || column == 0 || !ensureLibclang())
        return 0;
    std::shared_ptr<Slot> holder = findSlot(vx::toUtf8(path), false);
    if (!holder)
        return 0;
    std::unique_lock unitLock(holder->mutex, std::try_to_lock);
    if (!unitLock.owns_lock() || !holder->unit)
        return 0; // being parsed right now: answer nothing rather than block
    Unit* unit = holder->unit.get();

    // Convert the UTF-16 column to libclang's 1-based UTF-8 byte column.
    size_t lineStart = 0;
    for (uint32_t l = 1; l < line; ++l) {
        lineStart = unit->text.find(u'\n', lineStart);
        if (lineStart == std::u16string::npos)
            return 0;
        ++lineStart;
    }
    const auto prefix = std::u16string_view(unit->text).substr(lineStart, column - 1);
    const unsigned byteColumn = static_cast<unsigned>(vx::toUtf8(prefix).size()) + 1;

    CXFile file = clang_getFile(unit->tu, unit->path.c_str());
    if (!file)
        return 0;
    CXCursor cursor = clang_getCursor(unit->tu, clang_getLocation(unit->tu, file, line, byteColumn));
    const CXCursorKind kind = clang_getCursorKind(cursor);

    CXCursor def;
    std::vector<vx::PPToken> invocation;
    unsigned expansionLine = line;
    unsigned expansionOffset = 0;
    if (kind == CXCursor_MacroExpansion) {
        def = clang_getCursorReferenced(cursor);
        invocation = Unit::toPP(unit->tokenize(clang_getCursorExtent(cursor)), 0);
        clang_getSpellingLocation(clang_getCursorLocation(cursor), nullptr, &expansionLine, nullptr, &expansionOffset);
    } else if (kind == CXCursor_MacroDefinition) {
        def = cursor;
    } else {
        return 0;
    }
    if (clang_Cursor_isNull(def) || clang_getCursorKind(def) != CXCursor_MacroDefinition)
        return 0;

    const std::string name = takeString(clang_getCursorSpelling(def));
    const std::string definition = "#define " + unit->extentText(def);

    CXFile defFile = nullptr;
    unsigned defLine = 0;
    clang_getSpellingLocation(clang_getCursorLocation(def), &defFile, &defLine, nullptr, nullptr);
    const std::string location = (defFile ? takeString(clang_getFileName(defFile)) : std::string("<built-in>")) +
                                 "(" + std::to_string(defLine) + ")";

    std::string expansion;
    if (!invocation.empty()) {
        UnitMacroTable table(*unit, expansionOffset);
        vx::MacroExpander expander(table, expansionLine, unit->path);
        expansion = vx::formatTokens(expander.expand(std::move(invocation)));
        if (expansion.empty())
            expansion = "/* expands to nothing */";
        if (expander.truncated())
            expansion += "\n/* ... expansion truncated ... */";
    }

    std::u16string result;
    for (const std::string& s : {name, definition, location, expansion}) {
        result += vx::toUtf16(s);
        result.push_back(u'\0');
    }
    return writeOut(result, out, cap);
}

VX_API void VX_CALL vx_release(const char16_t* path)
{
    if (!path)
        return;
    std::lock_guard lock(g_mutex);
    g_units.erase(normalizeKey(vx::toUtf8(path))); // an in-flight parse keeps its slot alive
}

VX_API uint32_t VX_CALL vx_semantic_tokens(const char16_t* path, vx_span* out, uint32_t cap)
{
    if (!path || !ensureLibclang())
        return 0;
    std::shared_ptr<Slot> holder = findSlot(vx::toUtf8(path), false);
    if (!holder)
        return 0;
    std::lock_guard unitLock(holder->mutex);
    Unit* unit = holder->unit.get();
    if (!unit)
        return 0;
    if (!unit->semanticBuilt) {
        unit->semantic = vx::semanticTokens(unit->tu, unit->path, unit->text);
        unit->semanticBuilt = true;
    }
    const auto& spans = unit->semantic;
    if (out)
        std::copy_n(spans.begin(), std::min<size_t>(spans.size(), cap), out);
    return static_cast<uint32_t>(spans.size());
}

VX_API uint32_t VX_CALL vx_discover_args(const char16_t* path, char16_t* out, uint32_t cap, uint32_t* source)
{
    if (source)
        *source = VX_ARGS_NONE;
    if (!path || !ensureLibclang())
        return 0;
    std::vector<std::string> args;
    const vx_args_source found = discoverArgs(vx::toUtf8(path), args);
    if (source)
        *source = found;
    std::u16string result;
    for (const std::string& a : args) {
        result += vx::toUtf16(a);
        result.push_back(u'\0');
    }
    return writeOut(result, out, cap);
}

} // extern "C"
