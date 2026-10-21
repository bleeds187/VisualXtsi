// Minimal self-contained test runner for the engine's C ABI.
#include "vx/vx.h"

#include <cstdio>
#include <string>
#include <string_view>
#include <vector>

namespace {

int failures = 0;

struct Expected {
    std::u16string_view text;
    uint32_t kind;
};

std::vector<vx_span> classify(std::u16string_view src)
{
    // Start small to exercise the "call again with a larger buffer" path.
    std::vector<vx_span> spans(2);
    uint32_t n = vx_classify(src.data(), static_cast<uint32_t>(src.size()), spans.data(), 2);
    if (n > spans.size()) {
        spans.resize(n);
        n = vx_classify(src.data(), static_cast<uint32_t>(src.size()), spans.data(), n);
    }
    spans.resize(n);
    return spans;
}

std::string narrow(std::u16string_view s)
{
    std::string r;
    for (char16_t c : s)
        r.push_back(c < 0x80 ? static_cast<char>(c) : '?');
    return r;
}

void check(const char* name, std::u16string_view src, const std::vector<Expected>& expected)
{
    const auto spans = classify(src);
    bool ok = spans.size() == expected.size();
    for (size_t i = 0; ok && i < spans.size(); ++i)
        ok = src.substr(spans[i].start, spans[i].length) == expected[i].text && spans[i].kind == expected[i].kind;
    if (ok)
        return;
    ++failures;
    std::printf("FAIL %s\n  got:", name);
    for (const auto& s : spans)
        std::printf(" [%u:%s]", s.kind, narrow(src.substr(s.start, s.length)).c_str());
    std::printf("\n  expected:");
    for (const auto& e : expected)
        std::printf(" [%u:%s]", e.kind, narrow(e.text).c_str());
    std::printf("\n");
}

constexpr uint32_t P = VX_KIND_PREPROCESSOR;
constexpr uint32_t M = VX_KIND_MACRO;

} // namespace

int main()
{
    if (vx_abi_version() != VX_ABI_VERSION) {
        ++failures;
        std::printf("FAIL abi version\n");
    }

    check("empty", u"", {});

    check("directives", u"#include <WINDOWS_H.h>\n  #  pragma once\nint x;",
          {{u"#include", P}, {u"#  pragma", P}});

    check("define name and uses", u"#define square(x) ((x)*(x))\nint y = square(2);",
          {{u"#define", P}, {u"square", M}, {u"square", M}});

    check("use before define", u"int a = later(1);\n#define later(x) x\n",
          {{u"later", M}, {u"#define", P}, {u"later", M}});

    check("all caps heuristic", u"UCLASS() class AFoo { GENERATED_BODY() int X; int A1; };",
          {{u"UCLASS", M}, {u"GENERATED_BODY", M}, {u"A1", M}});

    check("comments and strings skipped",
          u"// NOT_ME\n/* NOR_ME\n#define NOPE */ \"STR_LIT\" 'C' L\"WIDE_X\" u8\"U8_X\" int Y2;",
          {{u"Y2", M}});

    check("raw string", u"auto s = R\"x(IN_RAW \" )\" still)x\"; OUT_RAW;", {{u"OUT_RAW", M}});

    check("hash not at line start", u"int a; # define NOT_DIRECTIVE\n", {{u"NOT_DIRECTIVE", M}});

    check("line continuation", u"#define LONG_ONE \\\n  OTHER_ONE\n#if XY\n#endif",
          {{u"#define", P}, {u"LONG_ONE", M}, {u"OTHER_ONE", M}, {u"#if", P}, {u"XY", M}, {u"#endif", P}});

    check("crlf", u"#ifdef WIN\r\n#endif\r\n", {{u"#ifdef", P}, {u"WIN", M}, {u"#endif", P}});

    check("numbers", u"int a = 0xFFULL + 1'000 + 1E+10 + MAX_N;", {{u"MAX_N", M}});

    check("utf16 offsets", u"/* é\U0001F600 */ FOO_BAR", {{u"FOO_BAR", M}});

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
