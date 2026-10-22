// Navigation entry points that need no parsing.
#include "vx/vx.h"

#include "navigation.h"
#include "project_args.h"
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

} // extern "C"
