// Navigation entry points that need no parsing.
#include "vx/vx.h"

#include "file_index.h"
#include "navigation.h"
#include "project_args.h"
#include "symbol_index.h"
#include "unicode.h"

#include <algorithm>

extern "C" {

VX_API uint32_t VX_CALL vx_counterparts(const char16_t* path, char16_t* out, uint32_t cap)
{
    if (!path)
        return 0;
    const std::string path8 = vx::toUtf8(path);
    std::vector<std::string> includeDirs;
    vx::VcxprojArgs project;
    if (vx::argsFromUnrealProject(path8, project))
        for (const std::string& a : project.args)
            if (a.size() > 2 && a.compare(0, 2, "-I") == 0)
                includeDirs.push_back(a.substr(2));

    std::u16string result;
    for (const std::string& file : vx::nav::counterparts(path8, includeDirs)) {
        result += vx::toUtf16(file);
        result.push_back(u'\0');
    }
    if (out)
        std::copy_n(result.begin(), std::min<size_t>(result.size(), cap), out);
    return static_cast<uint32_t>(result.size());
}

VX_API void VX_CALL vx_index_add_root(const char16_t* dir)
{
    if (dir)
        vx::files::addRoot(reinterpret_cast<const wchar_t*>(dir));
}

VX_API uint32_t VX_CALL vx_index_add_project_of(const char16_t* path)
{
    return path && vx::files::addProjectOf(reinterpret_cast<const wchar_t*>(path)) ? 1u : 0u;
}

VX_API uint32_t VX_CALL vx_index_file_count(void)
{
    return static_cast<uint32_t>(vx::files::count());
}

VX_API uint32_t VX_CALL vx_index_find_files(const char16_t* query, uint32_t max_results, char16_t* out, uint32_t cap)
{
    if (!query)
        return 0;
    std::wstring result;
    for (const std::wstring& path : vx::files::find(reinterpret_cast<const wchar_t*>(query), max_results)) {
        result += path;
        result.push_back(L'\0');
    }
    if (out)
        std::copy_n(reinterpret_cast<const char16_t*>(result.data()), std::min<size_t>(result.size(), cap), out);
    return static_cast<uint32_t>(result.size());
}

VX_API void VX_CALL vx_index_reset(void)
{
    vx::symbols::reset();
    vx::files::reset();
}

VX_API uint32_t VX_CALL vx_index_add_engine_of(const char16_t* path)
{
    if (!path)
        return 0;
    vx::VcxprojArgs project;
    if (!vx::argsFromUnrealProject(vx::toUtf8(path), project))
        return 0;
    std::vector<std::wstring> includeDirs;
    for (const std::string& a : project.args)
        if (a.size() > 2 && a.compare(0, 2, "-I") == 0) {
            const std::u16string w = vx::toUtf16(a.substr(2));
            includeDirs.emplace_back(w.begin(), w.end());
        }
    return vx::files::addEngineOf(reinterpret_cast<const wchar_t*>(path), includeDirs) ? 1u : 0u;
}

VX_API void VX_CALL vx_symbols_start(void)
{
    vx::symbols::start();
}

namespace {

// Symbol records as documented at vx_symbols_find.
uint32_t writeSymbols(const std::vector<vx::symbols::Hit>& hits, char16_t* out, uint32_t cap)
{
    std::u16string result;
    for (const vx::symbols::Hit& hit : hits) {
        const vx::symbols::Symbol& s = hit.symbol;
        result += vx::toUtf16(std::to_string(int(s.kind)) + "\t" + std::to_string(int(s.definition)) + "\t" +
                              std::to_string(s.line) + "\t" + std::to_string(s.column) + "\t");
        result += s.name + u"\t" + s.scope + u"\t";
        result.append(hit.path.begin(), hit.path.end());
        result += u"\t" + vx::toUtf16(std::to_string(int(s.ue)));
        result.push_back(u'\0');
    }
    if (out)
        std::copy_n(result.begin(), std::min<size_t>(result.size(), cap), out);
    return static_cast<uint32_t>(result.size());
}

} // namespace

VX_API uint32_t VX_CALL vx_symbols_find(const char16_t* query, uint32_t max_results, char16_t* out, uint32_t cap)
{
    return query ? writeSymbols(vx::symbols::find(query, max_results), out, cap) : 0;
}

VX_API uint32_t VX_CALL vx_symbols_lookup(const char16_t* name, uint32_t max_results, char16_t* out, uint32_t cap)
{
    return name ? writeSymbols(vx::symbols::lookup(name, max_results), out, cap) : 0;
}

VX_API void VX_CALL vx_symbols_status(uint32_t* files, uint32_t* pending, uint32_t* symbols)
{
    const vx::symbols::Status st = vx::symbols::status();
    if (files)
        *files = static_cast<uint32_t>(st.files);
    if (pending)
        *pending = static_cast<uint32_t>(st.pending);
    if (symbols)
        *symbols = static_cast<uint32_t>(st.symbols);
}

VX_API void VX_CALL vx_symbols_save(void)
{
    vx::symbols::save();
}

VX_API void VX_CALL vx_symbols_rebuild(void)
{
    vx::symbols::rebuild();
}

} // extern "C"
