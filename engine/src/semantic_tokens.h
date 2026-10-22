#pragma once

#include "vx/vx.h"

#include <clang-c/Index.h>

#include <string>
#include <string_view>
#include <vector>

namespace vx {

// Classifies every identifier token of `path` (the TU's main file) by the
// declaration it names. `text` is the UTF-16 text that was parsed; offsets in
// the result are UTF-16 units into it.
std::vector<vx_span> semanticTokens(CXTranslationUnit tu, const std::string& path, std::u16string_view text);

} // namespace vx
