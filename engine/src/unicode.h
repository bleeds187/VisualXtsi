#pragma once

#include <string>
#include <string_view>

namespace vx {

std::string toUtf8(std::u16string_view s);
std::u16string toUtf16(std::string_view s);

} // namespace vx
