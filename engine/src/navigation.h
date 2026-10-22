#pragma once

#include <string>
#include <vector>

namespace vx::nav {

// Files to switch to from `path` (a header gives sources, a source gives headers), best
// first: same folder, Unreal's mirrored Public/Classes <-> Private layout, the rest of the
// module (the folder holding its *.Build.cs), and for sources a same-named #include resolved
// through `includeDirs`. UTF-8 paths.
std::vector<std::string> counterparts(const std::string& path, const std::vector<std::string>& includeDirs);

} // namespace vx::nav
