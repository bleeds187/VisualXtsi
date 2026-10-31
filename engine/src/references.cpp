// Find References (vx_references_*): every use of the declaration at a position, across the indexed
// files. A whole-word scan finds the files that could refer to it; each is then parsed in the
// background (an open file's own translation unit is used as it is) and every token with the name
// is confirmed by the USR of what it refers to. Files that cannot be parsed, and tokens the parser
// cannot place (inside macro arguments it does not map, for instance), give unconfirmed matches.
#include "vx/vx.h"

#include "clang_internal.h"
#include "file_index.h"
#include "module_pch.h"
#include "unicode.h"

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

using namespace vx::clangs;
namespace fs = std::filesystem;

namespace {

struct Reference {
    std::u16string path;
    uint32_t line = 0, column = 0; // 1-based, UTF-16
    const char16_t* kind = u"reference"; // definition, declaration, write, reference
    bool confirmed = true;
    std::u16string text;           // the line, trimmed
};

struct Job {
    std::string usr;
    std::string name;     // the identifier to look for
    std::u16string label; // "UThing::Run (method)"
    std::vector<std::string> scopes; // the names of its enclosing scopes ("UThing")
    bool macro = false;
    std::vector<std::wstring> files;
    std::atomic<size_t> next{0};
    std::atomic<uint32_t> done{0};
    std::atomic<bool> cancel{false};
    std::atomic<int> running{0};
    std::mutex mutex;
    std::vector<Reference> refs;
    std::vector<std::thread> workers; // joined by vx_references_free, which owns the job

    void stop()
    {
        cancel = true;
        for (std::thread& t : workers)
            if (t.joinable())
                t.join();
        workers.clear();
    }
};

std::mutex g_jobsMutex;
std::unordered_map<uint32_t, std::shared_ptr<Job>> g_jobs;
uint32_t g_nextJob = 1;

std::shared_ptr<Job> jobOf(uint32_t id)
{
    std::lock_guard lock(g_jobsMutex);
    auto it = g_jobs.find(id);
    return it == g_jobs.end() ? nullptr : it->second;
}

std::wstring lower(std::wstring s)
{
    for (auto& c : s)
        c = static_cast<wchar_t>(towlower(c));
    return s;
}

bool isCpp(const std::wstring& path)
{
    const std::wstring ext = lower(fs::path(path).extension().wstring());
    for (const wchar_t* e : {L".h", L".hpp", L".hh", L".hxx", L".inl", L".ipp", L".cpp", L".cc", L".cxx", L".c", L".ixx", L".cppm"})
        if (ext == e)
            return true;
    return false;
}

// Unreal's engine code (its Source, Plugins, Platforms and Restricted folders), by structure.
bool isEngine(const std::wstring& path)
{
    const std::wstring l = lower(fs::path(path).lexically_normal().wstring());
    for (const wchar_t* part : {L"\\engine\\source\\", L"\\engine\\plugins\\", L"\\engine\\platforms\\", L"\\engine\\restricted\\"})
        if (l.find(part) != std::wstring::npos)
            return true;
    return false;
}

bool identChar(char c)
{
    return isalnum(static_cast<unsigned char>(c)) || c == '_' || static_cast<unsigned char>(c) >= 0x80;
}

std::string readBytes(const std::wstring& path)
{
    std::FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || !f)
        return {};
    std::string bytes;
    char buf[65536];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0)
        bytes.append(buf, n);
    std::fclose(f);
    return bytes;
}

// Byte offsets of whole-word occurrences of `name`.
std::vector<size_t> wordHits(const std::string& text, const std::string& name)
{
    std::vector<size_t> hits;
    for (size_t at = text.find(name); at != std::string::npos; at = text.find(name, at + 1))
        if ((at == 0 || !identChar(text[at - 1])) && (at + name.size() >= text.size() || !identChar(text[at + name.size()])))
            hits.push_back(at);
    return hits;
}

// Line, UTF-16 column and the trimmed line text of a byte offset.
void place(const char* text, size_t size, size_t offset, Reference& r)
{
    size_t lineStart = offset;
    while (lineStart > 0 && text[lineStart - 1] != '\n')
        --lineStart;
    uint32_t line = 1;
    for (size_t i = 0; i < lineStart; ++i)
        if (text[i] == '\n')
            ++line;
    size_t lineEnd = offset;
    while (lineEnd < size && text[lineEnd] != '\n' && text[lineEnd] != '\r')
        ++lineEnd;
    r.line = line;
    r.column = static_cast<uint32_t>(vx::toUtf16(std::string_view(text + lineStart, offset - lineStart)).size()) + 1;
    std::string_view lineText(text + lineStart, lineEnd - lineStart);
    while (!lineText.empty() && (lineText.front() == ' ' || lineText.front() == '\t'))
        lineText.remove_prefix(1);
    if (lineText.size() > 240)
        lineText = lineText.substr(0, 240);
    r.text = vx::toUtf16(lineText);
}

void addUnconfirmed(Job& job, const std::wstring& path, const std::string& bytes)
{
    std::vector<Reference> found;
    for (size_t at : wordHits(bytes, job.name)) {
        Reference r;
        r.path = std::u16string(path.begin(), path.end());
        r.confirmed = false;
        place(bytes.data(), bytes.size(), at, r);
        found.push_back(std::move(r));
    }
    std::lock_guard lock(job.mutex);
    job.refs.insert(job.refs.end(), found.begin(), found.end());
}

// Whether a cursor names the target: its referenced declaration (or itself, for a declaration) has
// the target's USR. Unknown when the parser could not place the token.
enum class Match { Yes, No, Unknown };

Match matches(const Job& job, CXCursor c)
{
    const CXCursorKind kind = clang_getCursorKind(c);
    if (kind == CXCursor_MacroExpansion || kind == CXCursor_MacroDefinition) {
        if (!job.macro)
            return Match::Unknown;
        const CXCursor def = kind == CXCursor_MacroDefinition ? c : clang_getCursorReferenced(c);
        return takeString(clang_getCursorUSR(def)) == job.usr ? Match::Yes : Match::No;
    }
    if (clang_isInvalid(kind) || kind == CXCursor_UnexposedExpr || kind == CXCursor_UnexposedDecl)
        return Match::Unknown;
    if (kind == CXCursor_OverloadedDeclRef) {
        for (unsigned i = 0, n = clang_getNumOverloadedDecls(c); i < n; ++i)
            if (takeString(clang_getCursorUSR(clang_getOverloadedDecl(c, i))) == job.usr)
                return Match::Yes;
        return Match::No;
    }
    const CXCursor ref = clang_isDeclaration(kind) ? c : clang_getCursorReferenced(c);
    if (clang_Cursor_isNull(ref) || clang_isInvalid(clang_getCursorKind(ref)))
        return Match::Unknown;
    return takeString(clang_getCursorUSR(ref)) == job.usr ? Match::Yes : Match::No;
}

bool isWrite(const std::vector<std::string>& spellings, size_t i)
{
    static const char* const assigns[] = {"=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "++", "--"};
    if (i + 1 < spellings.size())
        for (const char* a : assigns)
            if (spellings[i + 1] == a)
                return true;
    return i > 0 && (spellings[i - 1] == "++" || spellings[i - 1] == "--");
}

// The references in `path` by the translation unit `tu` (whose main file it is).
bool annotate(Job& job, CXTranslationUnit tu, const std::string& path8, const std::wstring& path)
{
    CXFile file = clang_getFile(tu, path8.c_str());
    size_t size = 0;
    const char* contents = file ? clang_getFileContents(tu, file, &size) : nullptr;
    if (!contents)
        return false;
    CXSourceRange range = clang_getRange(clang_getLocationForOffset(tu, file, 0), clang_getLocationForOffset(tu, file, static_cast<unsigned>(size)));
    CXToken* tokens = nullptr;
    unsigned count = 0;
    clang_tokenize(tu, range, &tokens, &count);
    std::vector<std::string> spellings(count);
    std::vector<CXToken> selected;
    std::vector<unsigned> index;
    for (unsigned i = 0; i < count; ++i) {
        spellings[i] = takeString(clang_getTokenSpelling(tu, tokens[i]));
        if (clang_getTokenKind(tokens[i]) == CXToken_Identifier && spellings[i] == job.name) {
            selected.push_back(tokens[i]);
            index.push_back(i);
        }
    }
    std::vector<CXCursor> cursors(selected.size());
    if (!selected.empty())
        clang_annotateTokens(tu, selected.data(), static_cast<unsigned>(selected.size()), cursors.data());
    std::vector<Reference> found;
    for (size_t k = 0; k < selected.size(); ++k) {
        Match m = matches(job, cursors[k]);
        // Unplaced, but qualified with a scope that is not the target's ("EOther::Count"): another symbol.
        const unsigned at = index[k];
        if (m == Match::Unknown && at >= 2 && spellings[at - 1] == "::" &&
            std::find(job.scopes.begin(), job.scopes.end(), spellings[at - 2]) == job.scopes.end())
            m = Match::No;
        if (m == Match::No)
            continue;
        unsigned offset = 0;
        clang_getSpellingLocation(clang_getTokenLocation(tu, selected[k]), nullptr, nullptr, nullptr, &offset);
        Reference r;
        r.path = std::u16string(path.begin(), path.end());
        r.confirmed = m == Match::Yes;
        const CXCursorKind kind = clang_getCursorKind(cursors[k]);
        if (r.confirmed && clang_isDeclaration(kind))
            r.kind = clang_isCursorDefinition(cursors[k]) ? u"definition" : u"declaration";
        else if (r.confirmed && kind == CXCursor_MacroDefinition)
            r.kind = u"definition";
        else if (r.confirmed && isWrite(spellings, index[k]))
            r.kind = u"write";
        place(contents, size, offset, r);
        found.push_back(std::move(r));
    }
    clang_disposeTokens(tu, tokens, count);
    std::lock_guard lock(job.mutex);
    job.refs.insert(job.refs.end(), found.begin(), found.end());
    return true;
}

void searchFile(Job& job, CXIndex& index, const std::wstring& path)
{
    const std::string path8 = vx::toUtf8(std::u16string(path.begin(), path.end()));
    // An open file: its own translation unit (with the editor's unsaved text).
    if (std::shared_ptr<Slot> slot = findSlot(path8, false)) {
        std::lock_guard lock(slot->mutex);
        if (slot->unit && slot->unit->tu && annotate(job, slot->unit->tu, slot->unit->path, path))
            return;
    }
    const std::string bytes = readBytes(path);
    if (wordHits(bytes, job.name).empty())
        return;
    if (!index)
        index = clang_createIndex(0, 0);
    ParseArgs args = computeArgs(path8, nullptr, 0, nullptr);
    std::string pch;
    if (args.source == VX_ARGS_VCXPROJ)
        pch = vx::pch::ready(vx::pch::moduleKey(args.args));
    std::vector<const char*> argv;
    for (auto& a : args.args)
        argv.push_back(a.c_str());
    if (!pch.empty()) {
        argv.push_back("-include-pch");
        argv.push_back(pch.c_str());
    }
    CXTranslationUnit tu = nullptr;
    const unsigned options = CXTranslationUnit_KeepGoing;
    CXErrorCode err = args.fullArgv
        ? clang_parseTranslationUnit2FullArgv(index, nullptr, argv.data(), static_cast<int>(argv.size()), nullptr, 0, options, &tu)
        : clang_parseTranslationUnit2(index, path8.c_str(), argv.data(), static_cast<int>(argv.size()), nullptr, 0, options, &tu);
    if (err != CXError_Success && !pch.empty()) { // an unusable PCH: plainly
        argv.resize(argv.size() - 2);
        tu = nullptr;
        err = args.fullArgv
            ? clang_parseTranslationUnit2FullArgv(index, nullptr, argv.data(), static_cast<int>(argv.size()), nullptr, 0, options, &tu)
            : clang_parseTranslationUnit2(index, path8.c_str(), argv.data(), static_cast<int>(argv.size()), nullptr, 0, options, &tu);
    }
    if (err != CXError_Success || !tu || !annotate(job, tu, path8, path))
        addUnconfirmed(job, path, bytes);
    if (tu)
        clang_disposeTranslationUnit(tu);
}

void work(Job* job)
{
    CXIndex index = nullptr;
    for (size_t i; !job->cancel && (i = job->next++) < job->files.size();) {
        try {
            searchFile(*job, index, job->files[i]);
        } catch (...) {
        }
        ++job->done;
    }
    if (index)
        clang_disposeIndex(index);
    --job->running;
}

// The declaration a position refers to (or is), for searching.
CXCursor targetAt(Unit& unit, uint32_t line, uint32_t column)
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
    CXCursor c = clang_getCursor(unit.tu, clang_getLocation(unit.tu, file, line, byteColumn));
    const CXCursorKind kind = clang_getCursorKind(c);
    if (kind == CXCursor_MacroExpansion)
        return clang_getCursorReferenced(c);
    if (kind == CXCursor_MacroDefinition || (clang_isDeclaration(kind) && kind != CXCursor_UnexposedDecl))
        return c;
    if (kind == CXCursor_OverloadedDeclRef && clang_getNumOverloadedDecls(c) > 0)
        return clang_getOverloadedDecl(c, 0);
    return clang_getCursorReferenced(c);
}

} // namespace

extern "C" {

VX_API uint32_t VX_CALL vx_references_start(const char16_t* path, uint32_t line, uint32_t column)
{
    if (!path || line == 0 || column == 0 || !ensureLibclang())
        return 0;
    auto job = std::make_shared<Job>();
    std::wstring declFile;
    {
        std::shared_ptr<Slot> slot = findSlot(vx::toUtf8(path), false);
        if (!slot)
            return 0;
        std::lock_guard lock(slot->mutex);
        if (!slot->unit || !slot->unit->tu)
            return 0;
        const CXCursor target = targetAt(*slot->unit, line, column);
        if (clang_Cursor_isNull(target) || clang_isInvalid(clang_getCursorKind(target)))
            return 0;
        job->usr = takeString(clang_getCursorUSR(target));
        job->name = takeString(clang_getCursorSpelling(target));
        if (!job->name.empty() && job->name[0] == '~')
            job->name.erase(0, 1); // a destructor: its tokens are "~" and the class name
        job->macro = clang_getCursorKind(target) == CXCursor_MacroDefinition;
        if (job->usr.empty() || job->name.empty() || !identChar(job->name[0]))
            return 0; // nothing to search by (operators and the like)
        const std::u16string scope = job->macro ? std::u16string() : qualifiedScope(target);
        for (size_t from = 0; from < scope.size();) {
            size_t sep = scope.find(u"::", from);
            job->scopes.push_back(vx::toUtf8(scope.substr(from, sep == std::u16string::npos ? std::u16string::npos : sep - from)));
            from = sep == std::u16string::npos ? scope.size() : sep + 2;
        }
        job->label = (scope.empty() ? u"" : scope + u"::") + vx::toUtf16(takeString(clang_getCursorSpelling(target))) + u" (" +
                     vx::toUtf16(takeString(clang_getCursorKindSpelling(clang_getCursorKind(target)))) + u")";
        CXFile file = nullptr;
        clang_getSpellingLocation(clang_getCursorLocation(target), &file, nullptr, nullptr, nullptr);
        if (file) {
            const std::u16string f = vx::toUtf16(takeString(clang_getFileName(file)));
            declFile = fs::path(std::wstring(f.begin(), f.end())).lexically_normal().wstring();
        }
    }
    // Candidates: the indexed C/C++ files (the engine's only if the target is declared in it), the
    // open file and the declaring file first. A local symbol is only in its own file.
    const std::wstring self = fs::path(std::wstring(path, path + std::char_traits<char16_t>::length(path))).lexically_normal().wstring();
    const bool local = job->usr.find('@') != std::string::npos && (job->usr.rfind("c:", 0) == 0) &&
                       (job->usr.find(".cpp@") != std::string::npos || job->usr.find(".h@") != std::string::npos);
    std::vector<std::wstring> files{self};
    if (!declFile.empty() && lower(declFile) != lower(self))
        files.push_back(declFile);
    if (!local) {
        const bool engineToo = !declFile.empty() && isEngine(declFile);
        for (const std::wstring& f : vx::files::all()) {
            const std::wstring n = fs::path(f).lexically_normal().wstring();
            if (isCpp(n) && (engineToo || !isEngine(n)) && lower(n) != lower(self) && lower(n) != lower(declFile))
                files.push_back(n);
        }
    }
    job->files = std::move(files);
    const unsigned workers = std::max(1u, std::min(6u, std::thread::hardware_concurrency() / 2));
    job->running = static_cast<int>(workers);
    for (unsigned i = 0; i < workers; ++i)
        job->workers.emplace_back(work, job.get());
    std::lock_guard lock(g_jobsMutex);
    const uint32_t id = g_nextJob++;
    g_jobs[id] = job;
    return id;
}

VX_API uint32_t VX_CALL vx_references_target(uint32_t job, char16_t* out, uint32_t cap)
{
    std::shared_ptr<Job> j = jobOf(job);
    if (!j)
        return 0;
    return writeOut(vx::toUtf16(j->name) + u"\t" + vx::toUtf16(j->usr) + u"\t" + j->label, out, cap);
}

VX_API void VX_CALL vx_references_progress(uint32_t job, uint32_t* done, uint32_t* total, uint32_t* finished)
{
    std::shared_ptr<Job> j = jobOf(job);
    if (done)
        *done = j ? j->done.load() : 0;
    if (total)
        *total = j ? static_cast<uint32_t>(j->files.size()) : 0;
    if (finished)
        *finished = !j || j->running == 0 ? 1 : 0;
}

VX_API uint32_t VX_CALL vx_references_results(uint32_t job, uint32_t from, char16_t* out, uint32_t cap)
{
    std::shared_ptr<Job> j = jobOf(job);
    if (!j)
        return 0;
    std::u16string result;
    {
        std::lock_guard lock(j->mutex);
        for (size_t i = from; i < j->refs.size(); ++i) {
            const Reference& r = j->refs[i];
            result += r.path + u"\t" + vx::toUtf16(std::to_string(r.line)) + u"\t" + vx::toUtf16(std::to_string(r.column)) + u"\t" +
                      r.kind + u"\t" + (r.confirmed ? u"1" : u"0") + u"\t" + r.text;
            result.push_back(u'\0');
        }
    }
    return writeOut(result, out, cap);
}

VX_API void VX_CALL vx_references_cancel(uint32_t job)
{
    if (std::shared_ptr<Job> j = jobOf(job))
        j->cancel = true;
}

VX_API void VX_CALL vx_references_free(uint32_t job)
{
    std::shared_ptr<Job> j;
    {
        std::lock_guard lock(g_jobsMutex);
        auto it = g_jobs.find(job);
        if (it == g_jobs.end())
            return;
        j = std::move(it->second);
        g_jobs.erase(it);
    }
    j->stop(); // a query running meanwhile holds its own reference; the workers are done here
}

} // extern "C"
