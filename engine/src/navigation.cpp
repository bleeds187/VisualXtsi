#include "navigation.h"

#include "unicode.h"

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <map>
#include <mutex>
#include <regex>
#include <set>

namespace fs = std::filesystem;

namespace vx::nav {

namespace {

const char* const kHeaderExts[] = {".h", ".hpp", ".hh", ".hxx", ".inl"};
const char* const kSourceExts[] = {".cpp", ".cc", ".cxx", ".c"};
constexpr size_t kMaxModuleFiles = 20000;

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

std::wstring lower(std::wstring s)
{
    for (auto& c : s)
        c = static_cast<wchar_t>(towlower(c));
    return s;
}

bool hasExt(const fs::path& p, const char* const* exts, size_t count)
{
    const std::wstring ext = lower(p.extension().wstring());
    for (size_t i = 0; i < count; ++i)
        if (ext == fs::path(exts[i]).wstring())
            return true;
    return false;
}

// The Unreal module root: the nearest folder (up to the drive) with a *.Build.cs file, or,
// failing that, the nearest with a Public, Private or Classes folder: platform extensions
// (Platforms\<Platform>\Source\...\<Module>) often extend a module without a Build.cs of their own.
fs::path moduleRoot(const fs::path& file)
{
    std::error_code ec;
    fs::path layout;
    for (fs::path dir = file.parent_path(); !dir.empty(); dir = dir.parent_path()) {
        for (auto& e : fs::directory_iterator(dir, ec)) {
            const std::wstring name = lower(e.path().filename().wstring());
            if (name.ends_with(L".build.cs"))
                return dir;
            if (layout.empty() && e.is_directory(ec) && (name == L"public" || name == L"private" || name == L"classes"))
                layout = dir;
        }
        if (dir == dir.root_path())
            break;
    }
    return layout;
}


// An Unreal module's root holds <Name>.Build.cs.
bool buildCsOf(const fs::path& root, fs::path& buildCs, std::wstring& module)
{
    std::error_code ec;
    for (auto& e : fs::directory_iterator(root, ec)) {
        const std::wstring name = e.path().filename().wstring();
        if (lower(name).ends_with(L".build.cs") && e.is_regular_file(ec)) {
            buildCs = e.path().lexically_normal();
            module = name.substr(0, name.size() - std::wstring(L".Build.cs").size());
            return true;
        }
    }
    return false;
}

std::string lowerAscii(std::string s)
{
    for (auto& c : s)
        if (c >= 'A' && c <= 'Z')
            c = static_cast<char>(c - 'A' + 'a');
    return s;
}

bool isMacroChar(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
}

// Whether a source implements the module: IMPLEMENT_MODULE(FImpl, Name), IMPLEMENT_GAME_MODULE(FImpl,
// Name), IMPLEMENT_PRIMARY_GAME_MODULE(FImpl, Name, "Game") or another IMPLEMENT_*MODULE macro naming
// it among its arguments (not commented out with //).
bool implementsModule(const fs::path& file, const std::string& lowerModule)
{
    std::ifstream in(file, std::ios::binary);
    const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    for (size_t at = text.find("IMPLEMENT_"); at != std::string::npos; at = text.find("IMPLEMENT_", at + 10)) {
        if (at > 0 && isMacroChar(text[at - 1]))
            continue;
        const size_t lineStart = text.rfind('\n', at) == std::string::npos ? 0 : text.rfind('\n', at) + 1;
        if (text.find("//", lineStart) < at)
            continue;
        size_t p = at;
        while (p < text.size() && isMacroChar(text[p]))
            ++p;
        if (!std::string_view(text).substr(at, p - at).ends_with("MODULE"))
            continue;
        while (p < text.size() && (text[p] == ' ' || text[p] == '\t'))
            ++p;
        const size_t close = p < text.size() && text[p] == '(' ? text.find(')', p) : std::string::npos;
        if (close == std::string::npos)
            continue;
        std::string arg;
        for (size_t i = p + 1; i <= close; ++i) {
            if (i == close || text[i] == ',') {
                if (lowerAscii(arg) == lowerModule)
                    return true;
                arg.clear();
            } else if (text[i] != ' ' && text[i] != '\t' && text[i] != '\r' && text[i] != '\n') {
                arg.push_back(text[i]);
            }
        }
    }
    return false;
}

struct CachedModule {
    fs::path source;
    fs::file_time_type sourceStamp, buildStamp;
};
std::mutex g_modulesMutex;
std::map<std::wstring, CachedModule> g_modules; // by lower-case module root

// The source implementing a module: the one whose IMPLEMENT_*MODULE macro names it, wherever it is
// in the module and whatever it is called. Without such a macro, <Name>.cpp or <Name>Module.cpp.
// Cached, and found again when the source (or, if there was none, the Build.cs) changes.
fs::path implementingSource(const fs::path& root, const fs::path& buildCs, const std::wstring& module)
{
    std::error_code ec;
    const std::wstring key = lower(root.wstring());
    const fs::file_time_type buildStamp = fs::last_write_time(buildCs, ec);
    {
        std::lock_guard lock(g_modulesMutex);
        if (auto it = g_modules.find(key); it != g_modules.end()) {
            const CachedModule& c = it->second;
            if (c.source.empty() ? c.buildStamp == buildStamp : fs::last_write_time(c.source, ec) == c.sourceStamp && !ec)
                return c.source;
        }
    }
    // Sources named after the module or "...Module" first: usually the one, so the rest is rarely read.
    const std::wstring lowerModule = lower(module);
    std::vector<fs::path> likely, others;
    size_t visited = 0;
    for (auto i = fs::recursive_directory_iterator(root, fs::directory_options::skip_permission_denied, ec);
         i != fs::recursive_directory_iterator() && visited < kMaxModuleFiles; i.increment(ec)) {
        ++visited;
        if (!i->is_regular_file(ec) || !hasExt(i->path(), kSourceExts, std::size(kSourceExts)))
            continue;
        const std::wstring stem = lower(i->path().stem().wstring());
        (stem.find(lowerModule) != std::wstring::npos || stem.find(L"module") != std::wstring::npos ? likely : others)
            .push_back(i->path().lexically_normal());
    }
    std::sort(likely.begin(), likely.end());
    std::sort(others.begin(), others.end());
    std::string module8;
    for (wchar_t c : lowerModule)
        module8.push_back(c < 128 ? static_cast<char>(c) : '?');
    fs::path found;
    for (const auto* group : {&likely, &others}) {
        for (const fs::path& source : *group)
            if (implementsModule(source, module8)) {
                found = source;
                break;
            }
        if (!found.empty())
            break;
    }
    for (const std::wstring& stem : {module, module + L"Module"})
        for (const wchar_t* dir : {L"Private", L""})
            if (found.empty() && fs::is_regular_file(root / dir / (stem + L".cpp"), ec))
                found = (root / dir / (stem + L".cpp")).lexically_normal();
    CachedModule entry{found, found.empty() ? fs::file_time_type{} : fs::last_write_time(found, ec), buildStamp};
    std::lock_guard lock(g_modulesMutex);
    g_modules[key] = entry;
    return found;
}

bool samePath(const fs::path& a, const fs::path& b)
{
    return lower(a.lexically_normal().wstring()) == lower(b.lexically_normal().wstring());
}
// C#: Form.cs <-> Form.Designer.cs, Window.xaml.cs -> Window.xaml.
std::vector<std::string> csharpCounterparts(const fs::path& file)
{
    const std::wstring name = file.filename().wstring(), lowerName = lower(name);
    std::vector<fs::path> candidates;
    if (lowerName.ends_with(L".designer.cs"))
        candidates.push_back(file.parent_path() / (name.substr(0, name.size() - 12) + L".cs"));
    else if (lowerName.ends_with(L".xaml.cs"))
        candidates.push_back(file.parent_path() / name.substr(0, name.size() - 3));
    else
        candidates.push_back(file.parent_path() / (name.substr(0, name.size() - 3) + L".Designer.cs"));
    std::vector<std::string> found;
    std::error_code ec;
    for (const fs::path& c : candidates)
        if (fs::is_regular_file(c, ec))
            found.push_back(fromPath(c));
    return found;
}


// A header's sources or a source's headers.
std::vector<std::string> plainCounterparts(const fs::path& file, const std::vector<std::string>& includeDirs)
{
    const bool isHeader = hasExt(file, kHeaderExts, std::size(kHeaderExts));
    const bool isSource = hasExt(file, kSourceExts, std::size(kSourceExts));
    if (!isHeader && !isSource)
        return {};
    const char* const* targets = isHeader ? kSourceExts : kHeaderExts;
    const size_t targetCount = isHeader ? std::size(kSourceExts) : std::size(kHeaderExts);
    const std::wstring stem = lower(file.stem().wstring());

    std::vector<std::string> found;
    std::set<std::wstring> seen;
    std::error_code ec;
    auto add = [&](const fs::path& candidate) {
        if (fs::is_regular_file(candidate, ec) && seen.insert(lower(candidate.lexically_normal().wstring())).second)
            found.push_back(fromPath(candidate.lexically_normal()));
    };
    auto addWithExts = [&](const fs::path& dir, const fs::path& stemPath) {
        for (size_t i = 0; i < targetCount; ++i) {
            fs::path candidate = dir / stemPath;
            candidate += targets[i];
            add(candidate);
        }
    };

    // 1. Same folder.
    addWithExts(file.parent_path(), file.stem());

    // 2. Mirrored Unreal layout: <Module>/Public|Classes/<sub>/X.h <-> <Module>/Private/<sub>/X.cpp.
    const fs::path root = moduleRoot(file);
    if (!root.empty()) {
        const fs::path relative = file.lexically_relative(root);
        auto it = relative.begin();
        if (it != relative.end()) {
            const std::wstring top = lower(it->wstring());
            fs::path sub;
            for (++it; it != relative.end(); ++it)
                sub /= *it;
            sub = sub.parent_path();
            std::vector<const wchar_t*> mirrors;
            if (top == L"public" || top == L"classes")
                mirrors = {L"Private"};
            else if (top == L"private")
                mirrors = {L"Public", L"Classes"};
            for (const wchar_t* mirror : mirrors)
                addWithExts(root / mirror / sub, file.stem());
        }

        // 3. Anywhere else in the module.
        size_t visited = 0;
        for (auto i = fs::recursive_directory_iterator(root, fs::directory_options::skip_permission_denied, ec);
             i != fs::recursive_directory_iterator() && visited < kMaxModuleFiles; i.increment(ec)) {
            ++visited;
            if (i->is_regular_file(ec) && lower(i->path().stem().wstring()) == stem &&
                hasExt(i->path(), targets, targetCount))
                add(i->path());
        }
    }

    // 4. A source's own header, as it includes it: #include ".../X.h" through the include paths.
    if (isSource) {
        static const std::regex includeLine(R"(^\s*#\s*include\s*[<"]([^>"]+)[>"])");
        std::ifstream in(file);
        std::string line;
        for (int n = 0; n < 200 && std::getline(in, line); ++n) {
            std::smatch m;
            if (!std::regex_search(line, m, includeLine))
                continue;
            const fs::path name = toPath(m[1].str());
            if (lower(name.stem().wstring()) != stem)
                continue;
            add(file.parent_path() / name);
            for (const std::string& dir : includeDirs)
                add(toPath(dir) / name);
        }
    }
    return found;
}

} // namespace

std::vector<std::string> counterparts(const std::string& path8, const std::vector<std::string>& includeDirs)
{
    const fs::path file = toPath(path8).lexically_normal();
    const std::wstring lowerName = lower(file.filename().wstring());
    if (lowerName.ends_with(L".build.cs")) {
        // The source implementing the module: its header if it has one (from there Alt+O toggles
        // header and source as usual), otherwise the source itself.
        const std::wstring name = file.filename().wstring();
        const fs::path source = implementingSource(file.parent_path(), file, name.substr(0, name.size() - 9));
        if (source.empty())
            return {};
        std::vector<std::string> found = plainCounterparts(source, includeDirs);
        found.push_back(fromPath(source));
        return found;
    }
    if (lowerName.ends_with(L".cs"))
        return csharpCounterparts(file);
    std::vector<std::string> found = plainCounterparts(file, includeDirs);
    // A module's implementing source without a header goes back to the module's Build.cs.
    if (found.empty() && hasExt(file, kSourceExts, std::size(kSourceExts))) {
        const fs::path root = moduleRoot(file);
        fs::path buildCs;
        std::wstring module;
        if (!root.empty() && buildCsOf(root, buildCs, module) && samePath(implementingSource(root, buildCs, module), file))
            found.push_back(fromPath(buildCs));
    }
    return found;
}

} // namespace vx::nav