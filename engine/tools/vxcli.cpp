// Developer CLI: parse a file with the engine and print macro info at positions.
//   vxcli <file> [@args.rsp] [--pch] [--semantic | --dump=<line>] [line:col ...]
// The response file holds one compiler argument per line (UTF-8).
#include "vx/vx.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>

namespace {

std::u16string widen(const std::string& s)
{
    std::u16string r;
    for (size_t i = 0; i < s.size();) {
        unsigned char b = static_cast<unsigned char>(s[i]);
        char32_t c = b;
        size_t n = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : 4;
        if (n > 1)
            c = b & (0x7F >> n);
        for (size_t k = 1; k < n && i + k < s.size(); ++k)
            c = (c << 6) | (static_cast<unsigned char>(s[i + k]) & 0x3F);
        i += n;
        if (c >= 0x10000) {
            c -= 0x10000;
            r.push_back(static_cast<char16_t>(0xD800 + (c >> 10)));
            r.push_back(static_cast<char16_t>(0xDC00 + (c & 0x3FF)));
        } else {
            r.push_back(static_cast<char16_t>(c));
        }
    }
    return r;
}

void printUtf16(const char16_t* s, size_t n)
{
    std::string out;
    for (size_t i = 0; i < n; ++i) {
        char32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n) {
            c = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
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
    std::fwrite(out.data(), 1, out.size(), stdout);
}

double msSince(std::chrono::steady_clock::time_point t)
{
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t).count();
}

} // namespace

int main(int argc, char** argv)
{
    if (argc < 2) {
        std::fprintf(stderr, "usage: vxcli <file> [@args.rsp] [line:col ...]\n");
        return 2;
    }
    const std::u16string file = widen(argv[1]);
    std::vector<std::u16string> args;
    std::vector<std::pair<unsigned, unsigned>> queries;
    bool semantic = false;
    bool usePch = false;
    unsigned dumpLine = 0;
    for (int i = 2; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--pch") {
            usePch = true;
        } else if (a == "--semantic") {
            semantic = true;
        } else if (a.rfind("--dump=", 0) == 0) {
            semantic = true;
            dumpLine = static_cast<unsigned>(std::stoul(a.substr(7)));
        } else if (!a.empty() && a[0] == '@') {
            std::ifstream rsp(a.substr(1));
            for (std::string line; std::getline(rsp, line);) {
                if (!line.empty() && line.back() == '\r')
                    line.pop_back();
                if (!line.empty())
                    args.push_back(widen(line));
            }
        } else {
            unsigned l = 0, c = 0;
            if (sscanf_s(a.c_str(), "%u:%u", &l, &c) == 2)
                queries.emplace_back(l, c);
        }
    }
    std::vector<const char16_t*> argPtrs;
    for (auto& a : args)
        argPtrs.push_back(a.c_str());
    if (args.empty()) {
        uint32_t source = 0;
        auto t0 = std::chrono::steady_clock::now();
        uint32_t n = vx_discover_args(file.c_str(), nullptr, 0, &source);
        std::vector<char16_t> discovered(n);
        vx_discover_args(file.c_str(), discovered.data(), n, &source);
        size_t count = static_cast<size_t>(std::count(discovered.begin(), discovered.end(), u'\0'));
        static const char* sources[] = {"none (defaults)", "compile_commands.json", "vcxproj"};
        std::printf("arguments: %s, %zu args, %.0f ms\n", sources[source < 3 ? source : 0], count, msSince(t0));
    }

    if (usePch) {
        auto t0 = std::chrono::steady_clock::now();
        int32_t pch = vx_prepare_module_pch(file.c_str());
        static const char* pchStates[] = {"ready", "not applicable", "failed", "busy"};
        std::printf("module pch: %s, %.0f ms\n", pch >= 0 && pch < 4 ? pchStates[pch] : "?", msSince(t0));
    }
    auto t = std::chrono::steady_clock::now();
    int32_t status = vx_parse(file.c_str(), argPtrs.empty() ? nullptr : argPtrs.data(),
                              static_cast<uint32_t>(argPtrs.size()), nullptr, nullptr, 0);
    std::printf("parse: status %d, %.0f ms\n", status, msSince(t));
    if (status != VX_OK)
        return 1;
    t = std::chrono::steady_clock::now();
    status = vx_parse(file.c_str(), argPtrs.empty() ? nullptr : argPtrs.data(), static_cast<uint32_t>(argPtrs.size()),
                      nullptr, nullptr, 0);
    std::printf("reparse: status %d, %.0f ms\n", status, msSince(t));

    if (semantic) {
        t = std::chrono::steady_clock::now();
        uint32_t n = vx_semantic_tokens(file.c_str(), nullptr, 0);
        std::printf("semantic tokens: %u in %.0f ms\n", n, msSince(t));
        std::vector<vx_span> spans(n);
        vx_semantic_tokens(file.c_str(), spans.data(), n);
        static const char* names[] = {"local", "parameter", "field", "global", "type", "enum-constant",
                                      "namespace", "function", "method", "static-method", "virtual-method"};
        std::vector<unsigned> histogram(std::size(names));
        for (const vx_span& s : spans)
            if (s.kind >= VX_KIND_LOCAL && s.kind <= VX_KIND_VIRTUAL_METHOD)
                ++histogram[s.kind - VX_KIND_LOCAL];
        for (size_t k = 0; k < histogram.size(); ++k)
            std::printf("  %-15s %u\n", names[k], histogram[k]);
        if (dumpLine) {
            std::ifstream in(argv[1], std::ios::binary);
            const std::string bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
            const std::u16string text = widen(bytes);
            for (const vx_span& s : spans) {
                const unsigned line = 1 + static_cast<unsigned>(std::count(text.begin(), text.begin() + s.start, u'\n'));
                if (line != dumpLine)
                    continue;
                std::printf("  %u: ", line);
                printUtf16(text.data() + s.start, s.length);
                std::printf(" -> %s\n", names[s.kind - VX_KIND_LOCAL]);
            }
        }
    }

    std::vector<char16_t> buf(1 << 20);
    for (auto [line, col] : queries) {
        t = std::chrono::steady_clock::now();
        uint32_t n = vx_macro_info(file.c_str(), line, col, buf.data(), static_cast<uint32_t>(buf.size()));
        std::printf("\n=== %u:%u (%.0f ms) ===\n", line, col, msSince(t));
        if (n == 0) {
            std::printf("(no macro)\n");
            continue;
        }
        static const char* labels[] = {"name", "definition", "location", "expansion"};
        size_t start = 0, field = 0;
        for (size_t i = 0; i < n && i < buf.size() && field < 4; ++i) {
            if (buf[i] == 0) {
                std::printf("--- %s:\n", labels[field++]);
                printUtf16(buf.data() + start, i - start);
                std::printf("\n");
                start = i + 1;
            }
        }
    }
    return 0;
}
