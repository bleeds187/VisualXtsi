#pragma once

#include <string>
#include <vector>

namespace vx {

// Compile arguments for one file, derived from an Unreal Build Tool generated
// .vcxproj (Intermediate/ProjectFiles/*.vcxproj). Works for both layouts:
// UE5's per-file ClCompile metadata behind property indirection, and UE4's
// project-wide NMakeIncludeSearchPath / NMakePreprocessorDefinitions.
struct VcxprojArgs {
    std::vector<std::string> args; // clang arguments (no compiler, no source file)
    std::string vcxproj;           // project file used
    std::string basedOn;           // ClCompile item the arguments came from (empty: project-wide)
};

// All paths are UTF-8. Returns false when no generated project covers `path`.
bool argsFromUnrealProject(const std::string& path, VcxprojArgs& out);

// Exposed for tests: resolve against one specific project file.
bool argsFromVcxproj(const std::string& vcxproj, const std::string& path, VcxprojArgs& out);

} // namespace vx
