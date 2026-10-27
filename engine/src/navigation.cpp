#include "navigation.h"

#include "unicode.h"

#include <algorithm>
#include <filesystem>
#include <fstream>
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

} // namespace

std::vector<std::string> counterparts(const std::string& path8, const std::vector<std::string>& includeDirs)
{
    const fs::path file = toPath(path8).lexically_normal();
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

} // namespace vx::nav
