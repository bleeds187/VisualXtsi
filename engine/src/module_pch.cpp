#include "module_pch.h"

#include "vx/vx.h"

#include "unicode.h"

#include <clang-c/Index.h>

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <mutex>
#include <regex>
#include <set>
#include <sstream>
#include <unordered_map>

namespace fs = std::filesystem;

namespace vx::pch {

namespace {

// How long a validated PCH is trusted before its manifest is checked again
// (VX_PCH_REVALIDATE_SEC overrides, for tests).
std::chrono::seconds revalidateEvery()
{
    char value[16] = {};
    if (GetEnvironmentVariableA("VX_PCH_REVALIDATE_SEC", value, sizeof value) > 0)
        return std::chrono::seconds(std::strtol(value, nullptr, 10));
    return std::chrono::minutes(5);
}
constexpr size_t kMaxHeaders = 64;
constexpr int kMaxAttempts = 6;

fs::path toPath(const std::string& utf8)
{
    const std::u16string w = toUtf16(utf8);
    return fs::path(std::wstring(w.begin(), w.end()));
}

std::string fromPath(const fs::path& p)
{
    const std::wstring w = p.wstring();
    return toUtf8(std::u16string(w.begin(), w.end()));
}

std::string lower(std::string s)
{
    for (auto& c : s)
        c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
    return s;
}

std::string pathKey(const fs::path& p)
{
    std::string s = lower(fromPath(p.lexically_normal()));
    std::replace(s.begin(), s.end(), '/', '\\');
    return s;
}

std::string takeString(CXString s)
{
    const char* c = clang_getCString(s);
    std::string r = c ? c : "";
    clang_disposeString(s);
    return r;
}

fs::path cacheDir()
{
    wchar_t buf[MAX_PATH * 2];
    DWORD n = GetEnvironmentVariableW(L"VX_PCH_DIR", buf, static_cast<DWORD>(std::size(buf)));
    if (n > 0 && n < std::size(buf))
        return fs::path(std::wstring(buf, n)); // tests and experiments
    n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, static_cast<DWORD>(std::size(buf)));
    fs::path base = n > 0 && n < std::size(buf) ? fs::path(std::wstring(buf, n)) : fs::temp_directory_path();
    return base / L"VisualXtsi" / L"pch";
}

uintmax_t cacheCapBytes()
{
    char value[32] = {};
    if (GetEnvironmentVariableA("VX_PCH_CACHE_MB", value, sizeof value) > 0)
        if (unsigned long mb = std::strtoul(value, nullptr, 10); mb > 0)
            return static_cast<uintmax_t>(mb) << 20;
    return uintmax_t(4096) << 20;
}

enum class State { Unknown, Building, Ready, Failed };

struct Entry {
    State state = State::Unknown;
    std::chrono::steady_clock::time_point validated;
};

std::mutex g_mutex;
std::unordered_map<std::string, Entry> g_entries;

fs::path pchFile(const std::string& key) { return cacheDir() / toPath(key + ".pch"); }
fs::path manifestFile(const std::string& key) { return cacheDir() / toPath(key + ".manifest"); }
fs::path headerFile(const std::string& key) { return cacheDir() / toPath(key + ".h"); }
fs::path logFile(const std::string& key) { return cacheDir() / toPath(key + ".log"); }

// Manifest: one "<write time> <size> <path>" line per file the PCH was built from.
bool manifestCurrent(const std::string& key)
{
    std::ifstream in(manifestFile(key));
    std::string header;
    if (!std::getline(in, header) || header != "vxpch 1")
        return false;
    std::error_code ec;
    for (std::string line; std::getline(in, line);) {
        std::istringstream fields(line);
        long long stamp = 0;
        unsigned long long size = 0;
        std::string path;
        fields >> stamp >> size;
        std::getline(fields >> std::ws, path);
        const fs::path p = toPath(path);
        if (fs::last_write_time(p, ec).time_since_epoch().count() != stamp || ec || fs::file_size(p, ec) != size || ec)
            return false;
    }
    return fs::exists(pchFile(key), ec);
}

void removeFiles(const std::string& key)
{
    std::error_code ec;
    fs::remove(pchFile(key), ec);
    fs::remove(manifestFile(key), ec);
    fs::remove(headerFile(key), ec);
}

// Evicts least recently used PCHs (by write time, refreshed on use) beyond the size cap.
void enforceCap(const std::string& keep)
{
    std::error_code ec;
    std::vector<std::pair<fs::file_time_type, fs::path>> files;
    uintmax_t total = 0;
    for (auto& e : fs::directory_iterator(cacheDir(), ec)) {
        if (e.path().extension() != L".pch")
            continue;
        total += e.file_size(ec);
        files.emplace_back(e.last_write_time(ec), e.path());
    }
    std::sort(files.begin(), files.end());
    const uintmax_t cap = cacheCapBytes();
    for (auto& [stamp, path] : files) {
        if (total <= cap)
            break;
        const std::string key = fromPath(path.stem());
        if (key == keep)
            continue;
        total -= fs::file_size(path, ec);
        removeFiles(key);
        std::lock_guard lock(g_mutex);
        g_entries.erase(key);
    }
}

// Headers most of the module's sources include, outside the project (engine and plugin
// headers rarely change while you edit), resolved against the module's include paths.
std::vector<fs::path> chooseHeaders(const std::vector<std::string>& args, const VcxprojArgs& project)
{
    std::vector<fs::path> includeDirs;
    for (const std::string& a : args)
        if (a.size() > 2 && a.compare(0, 2, "-I") == 0)
            includeDirs.push_back(toPath(a.substr(2)));

    const std::string projectKey = pathKey(toPath(project.projectRoot)) + "\\";
    auto inProject = [&](const fs::path& p) { return pathKey(p).rfind(projectKey, 0) == 0; };
    auto resolve = [&](const std::string& name, const fs::path& fromDir) -> fs::path {
        std::error_code ec;
        if (!fromDir.empty()) {
            fs::path local = (fromDir / toPath(name)).lexically_normal();
            if (fs::is_regular_file(local, ec))
                return local;
        }
        for (const fs::path& dir : includeDirs) {
            fs::path candidate = (dir / toPath(name)).lexically_normal();
            if (fs::is_regular_file(candidate, ec))
                return candidate;
        }
        return {};
    };

    // For each source, the engine/plugin headers it reaches directly or through the project's
    // own headers (Unreal sources mostly include their own header first, and that header
    // brings in the engine). Counted once per source.
    static const std::regex includeLine(R"(^\s*#\s*include\s*[<"]([^>"]+)[>"])");
    std::unordered_map<std::string, std::vector<std::string>> includesOf; // file -> raw include names
    auto includes = [&](const fs::path& file) -> const std::vector<std::string>& {
        auto [it, inserted] = includesOf.try_emplace(pathKey(file));
        if (inserted) {
            std::ifstream in(file);
            for (std::string line; std::getline(in, line);) {
                std::smatch m;
                if (std::regex_search(line, m, includeLine))
                    it->second.push_back(m[1].str());
            }
        }
        return it->second;
    };
    std::map<std::string, size_t> counts;      // resolved engine header key -> sources reaching it
    std::map<std::string, fs::path> paths;     // key -> path
    std::map<std::string, size_t> firstSeen;   // key -> discovery order, for a natural include order
    for (const std::string& source : project.moduleSources) {
        std::set<std::string> reached, visited;
        std::function<void(const fs::path&, int)> walk = [&](const fs::path& file, int depth) {
            if (!visited.insert(pathKey(file)).second)
                return;
            for (const std::string& name : includes(file)) {
                if (name.ends_with(".generated.h"))
                    continue;
                fs::path resolved = resolve(name, file.parent_path());
                if (resolved.empty())
                    continue;
                if (inProject(resolved)) {
                    if (depth < 3)
                        walk(resolved, depth + 1);
                } else if (reached.insert(pathKey(resolved)).second) {
                    paths.emplace(pathKey(resolved), resolved);
                    firstSeen.emplace(pathKey(resolved), firstSeen.size());
                }
            }
        };
        walk(toPath(source), 0);
        for (const std::string& key : reached)
            ++counts[key];
    }
    const size_t sources = project.moduleSources.size();
    const size_t threshold = std::max<size_t>(2, (sources * 2 + 9) / 10); // at least 20% of the files

    std::vector<std::string> ranked;
    for (auto& [key, count] : counts)
        if (count >= threshold)
            ranked.push_back(key);
    std::sort(ranked.begin(), ranked.end(), [&](const std::string& a, const std::string& b) {
        return counts[a] != counts[b] ? counts[a] > counts[b] : firstSeen[a] < firstSeen[b];
    });
    if (ranked.size() > kMaxHeaders)
        ranked.resize(kMaxHeaders);
    std::sort(ranked.begin(), ranked.end(), [&](auto& a, auto& b) { return firstSeen[a] < firstSeen[b]; });

    std::vector<fs::path> headers;
    if (fs::path core = resolve("CoreMinimal.h", {}); !core.empty() && !inProject(core))
        headers.push_back(core); // the base of nearly every Unreal translation unit
    for (const std::string& key : ranked)
        if (headers.empty() || pathKey(headers.front()) != key)
            headers.push_back(paths[key]);
    return headers;
}

void writeHeader(const std::string& key, const std::vector<fs::path>& headers)
{
    std::ofstream out(headerFile(key), std::ios::binary);
    out << "// Shared precompiled header generated by VisualXtsi.\n#pragma once\n";
    for (const fs::path& h : headers) {
        std::string p = fromPath(h);
        std::replace(p.begin(), p.end(), '\\', '/');
        out << "#include \"" << p << "\"\n";
    }
}

struct InclusionMap {
    CXFile mainFile = nullptr;
    std::unordered_map<std::string, std::string> topLevel; // any file -> header the PCH includes it through
    std::vector<std::string> files;                        // every file in the TU
};

void collectInclusions(CXTranslationUnit tu, InclusionMap& map)
{
    clang_getInclusions(
        tu,
        [](CXFile included, CXSourceLocation* stack, unsigned depth, CXClientData data) {
            auto& m = *static_cast<InclusionMap*>(data);
            const std::string name = takeString(clang_getFileName(included));
            m.files.push_back(name);
            if (depth == 0)
                return; // the PCH header itself
            std::string top = name;
            if (depth >= 2) {
                CXFile file = nullptr;
                // stack[depth - 1] is in the PCH header; the file containing stack[depth - 2] is
                // the top-level header this file came through.
                clang_getSpellingLocation(stack[depth - 2], &file, nullptr, nullptr, nullptr);
                top = takeString(clang_getFileName(file));
            }
            m.topLevel[pathKey(toPath(name))] = pathKey(toPath(top));
        },
        &map);
}

// Builds the PCH, dropping top-level headers that do not compile on their own. Returns true
// when an error-free PCH and its manifest were written.
bool build(const std::string& key, const std::vector<std::string>& args, std::vector<fs::path> headers)
{
    std::vector<std::string> all = {"-x", "c++-header"};
    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "-x") {
            ++i; // the language is decided above
            continue;
        }
        all.push_back(args[i]);
    }
    std::vector<const char*> argv;
    for (auto& a : all)
        argv.push_back(a.c_str());

    const std::string header8 = fromPath(headerFile(key));
    const unsigned options = CXTranslationUnit_Incomplete | CXTranslationUnit_ForSerialization |
                             CXTranslationUnit_KeepGoing | CXTranslationUnit_SkipFunctionBodies |
                             CXTranslationUnit_DetailedPreprocessingRecord;
    CXIndex index = clang_createIndex(0, 0);
    bool ok = false;
    std::ostringstream log; // why a build failed, for <key>.log
    for (int attempt = 0; attempt < kMaxAttempts && !headers.empty() && !ok; ++attempt) {
        writeHeader(key, headers);
        log << "attempt " << attempt + 1 << ": " << headers.size() << " headers\n";
        CXTranslationUnit tu = nullptr;
        if (clang_parseTranslationUnit2(index, header8.c_str(), argv.data(), static_cast<int>(argv.size()), nullptr,
                                        0, options, &tu) != CXError_Success || !tu) {
            log << "  parse failed\n";
            break;
        }
        InclusionMap map;
        collectInclusions(tu, map);

        // Errors: drop the top-level headers they came from (or, for errors in the generated
        // header itself such as a missing file, the include on that line).
        std::set<std::string> drop;
        int logged = 0;
        for (unsigned d = 0, n = clang_getNumDiagnostics(tu); d < n; ++d) {
            CXDiagnostic diag = clang_getDiagnostic(tu, d);
            if (clang_getDiagnosticSeverity(diag) >= CXDiagnostic_Error) {
                if (logged++ < 10)
                    log << "  " << takeString(clang_formatDiagnostic(diag, clang_defaultDiagnosticDisplayOptions()))
                        << '\n';
                CXFile file = nullptr;
                unsigned line = 0;
                // Expansion location: errors inside macro expansions (e.g. an undefined
                // CURRENT_FILE_ID_..._PROLOG when a .generated.h is missing) belong to the
                // header that used the macro.
                clang_getExpansionLocation(clang_getDiagnosticLocation(diag), &file, &line, nullptr, nullptr);
                const std::string name = file ? pathKey(toPath(takeString(clang_getFileName(file)))) : std::string();
                if (name == pathKey(headerFile(key))) {
                    if (line >= 3 && line - 3 < headers.size()) // line 1 comment, 2 #pragma once
                        drop.insert(pathKey(headers[line - 3]));
                } else if (auto it = map.topLevel.find(name); it != map.topLevel.end()) {
                    drop.insert(it->second);
                } else {
                    drop.insert("*"); // cannot attribute it: give up on this module
                }
            }
            clang_disposeDiagnostic(diag);
        }
        if (drop.empty()) {
            const fs::path tmp = cacheDir() / toPath(key + ".pch.tmp");
            ok = clang_saveTranslationUnit(tu, fromPath(tmp).c_str(), clang_defaultSaveOptions(tu)) ==
                 CXSaveError_None;
            std::error_code ec;
            if (ok) {
                fs::rename(tmp, pchFile(key), ec);
                ok = !ec;
            }
            if (ok) {
                std::ofstream manifest(manifestFile(key), std::ios::binary);
                manifest << "vxpch 1\n";
                for (const std::string& f : map.files) {
                    const fs::path p = toPath(f);
                    manifest << fs::last_write_time(p, ec).time_since_epoch().count() << ' ' << fs::file_size(p, ec)
                             << ' ' << f << '\n';
                }
            }
        } else if (drop.contains("*")) {
            log << "  errors outside any included header; giving up\n";
            clang_disposeTranslationUnit(tu);
            break;
        } else {
            for (const std::string& d : drop)
                log << "  dropping " << d << '\n';
            const size_t before = headers.size();
            std::erase_if(headers, [&](const fs::path& h) { return drop.contains(pathKey(h)); });
            if (headers.size() == before) {
                // The errors come from something we did not choose (a forced include, or engine
                // headers whose generated .generated.h files do not exist yet): retrying cannot help.
                log << "  none of those are PCH candidates; giving up\n";
                clang_disposeTranslationUnit(tu);
                break;
            }
        }
        clang_disposeTranslationUnit(tu);
    }
    clang_disposeIndex(index);
    if (!ok)
        removeFiles(key);
    std::ofstream(logFile(key), std::ios::binary) << (ok ? "built\n" : "failed\n") << log.str();
    return ok;
}

// A build that failed recently is not retried (it costs as much as a parse each time).
bool failedRecently(const std::string& key)
{
    std::error_code ec;
    const fs::path log = logFile(key);
    std::ifstream in(log);
    std::string first;
    if (!std::getline(in, first) || first != "failed")
        return false;
    return fs::file_time_type::clock::now() - fs::last_write_time(log, ec) < std::chrono::hours(24);
}

} // namespace

std::string moduleKey(const std::vector<std::string>& args)
{
    uint64_t hash = 1469598103934665603ull; // FNV-1a
    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "-x") {
            ++i;
            continue;
        }
        for (unsigned char c : args[i] + '\n') {
            hash ^= c;
            hash *= 1099511628211ull;
        }
    }
    char buf[17];
    std::snprintf(buf, sizeof buf, "%016llx", static_cast<unsigned long long>(hash));
    return buf;
}

std::string ready(const std::string& key)
{
    const auto now = std::chrono::steady_clock::now();
    {
        std::lock_guard lock(g_mutex);
        Entry& e = g_entries[key];
        if (e.state == State::Failed || e.state == State::Building)
            return {};
        if (e.state == State::Ready && now - e.validated < revalidateEvery())
            return fromPath(pchFile(key));
    }
    const bool current = manifestCurrent(key);
    std::lock_guard lock(g_mutex);
    Entry& e = g_entries[key];
    if (e.state == State::Building || e.state == State::Failed)
        return {};
    e.state = current ? State::Ready : State::Unknown;
    e.validated = now;
    if (!current)
        return {};
    std::error_code ec;
    fs::last_write_time(pchFile(key), fs::file_time_type::clock::now(), ec); // LRU: mark as used
    return fromPath(pchFile(key));
}

int prepare(const std::string& key, const std::vector<std::string>& args, const VcxprojArgs& project)
{
    if (!ready(key).empty())
        return VX_PCH_READY;
    {
        std::lock_guard lock(g_mutex);
        Entry& e = g_entries[key];
        if (e.state == State::Building)
            return VX_PCH_BUSY;
        if (e.state == State::Failed)
            return VX_PCH_FAILED;
        e.state = State::Building;
    }
    if (failedRecently(key)) {
        std::lock_guard lock(g_mutex);
        g_entries[key].state = State::Failed;
        return VX_PCH_FAILED;
    }
    std::error_code ec;
    fs::create_directories(cacheDir(), ec);
    std::vector<fs::path> headers = project.moduleSources.size() >= 2 ? chooseHeaders(args, project)
                                                                      : std::vector<fs::path>{};
    int status = VX_PCH_NOT_APPLICABLE;
    if (headers.size() >= 2)
        status = build(key, args, std::move(headers)) ? VX_PCH_READY : VX_PCH_FAILED;
    {
        std::lock_guard lock(g_mutex);
        Entry& e = g_entries[key];
        e.state = status == VX_PCH_READY ? State::Ready : status == VX_PCH_FAILED ? State::Failed : State::Unknown;
        e.validated = std::chrono::steady_clock::now();
    }
    if (status == VX_PCH_READY)
        enforceCap(key);
    return status;
}

void markBad(const std::string& key)
{
    removeFiles(key);
    std::lock_guard lock(g_mutex);
    g_entries[key].state = State::Failed;
}

} // namespace vx::pch
