#include "unicode.h"

#include <cstdint>

namespace vx {

std::string toUtf8(std::u16string_view s)
{
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s.size() && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
            ++i;
        } else if (c >= 0xD800 && c <= 0xDFFF) {
            c = 0xFFFD; // lone surrogate
        }
        if (c < 0x80) {
            out.push_back(static_cast<char>(c));
        } else if (c < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (c >> 6)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        } else if (c < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (c >> 12)));
            out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (c >> 18)));
            out.push_back(static_cast<char>(0x80 | ((c >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (c & 0x3F)));
        }
    }
    return out;
}

std::u16string toUtf16(std::string_view s)
{
    std::u16string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size();) {
        const auto b = static_cast<unsigned char>(s[i]);
        uint32_t c;
        size_t n;
        if (b < 0x80) {
            c = b;
            n = 1;
        } else if ((b & 0xE0) == 0xC0) {
            c = b & 0x1F;
            n = 2;
        } else if ((b & 0xF0) == 0xE0) {
            c = b & 0x0F;
            n = 3;
        } else if ((b & 0xF8) == 0xF0) {
            c = b & 0x07;
            n = 4;
        } else {
            out.push_back(u'\xFFFD');
            ++i;
            continue;
        }
        if (i + n > s.size()) {
            out.push_back(u'\xFFFD');
            break;
        }
        for (size_t k = 1; k < n; ++k)
            c = (c << 6) | (static_cast<unsigned char>(s[i + k]) & 0x3F);
        i += n;
        if (c >= 0x10000) {
            c -= 0x10000;
            out.push_back(static_cast<char16_t>(0xD800 + (c >> 10)));
            out.push_back(static_cast<char16_t>(0xDC00 + (c & 0x3FF)));
        } else {
            out.push_back(static_cast<char16_t>(c));
        }
    }
    return out;
}

} // namespace vx
