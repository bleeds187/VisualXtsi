// Tests of the refactoring queries (vx_cursor_info, vx_class_members) on the refactor fixture.
#include "vx/vx.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <map>
#include <string>
#include <thread>
#include <vector>

namespace {

int failures = 0;

std::string narrow(const std::u16string& s)
{
    std::string r;
    for (char16_t c : s)
        r.push_back(c < 0x80 ? static_cast<char>(c) : '?');
    return r;
}

std::u16string widen(const std::string& s)
{
    return std::u16string(s.begin(), s.end());
}

const std::u16string kDir = VX_SAMPLES_DIR u"/refactor/";

bool parse(const std::u16string& file, bool header)
{
    const std::u16string path = kDir + file;
    std::vector<std::u16string> args = {u"-x", header ? u"c++-header" : u"c++", u"-std=c++20", u"-I" + kDir};
    std::vector<const char16_t*> argv;
    for (auto& a : args)
        argv.push_back(a.c_str());
    return vx_parse(path.c_str(), argv.data(), static_cast<uint32_t>(argv.size()), nullptr, nullptr, 0) == VX_OK;
}

// key -> values (repeated keys, like param, keep every value in order).
using Info = std::multimap<std::string, std::string>;

Info info(const std::u16string& file, uint32_t line, uint32_t column)
{
    const std::u16string path = kDir + file;
    std::vector<char16_t> buf(8192);
    uint32_t n = vx_cursor_info(path.c_str(), line, column, 1, buf.data(), static_cast<uint32_t>(buf.size()));
    Info result;
    std::string text = narrow(std::u16string(buf.data(), std::min<uint32_t>(n, static_cast<uint32_t>(buf.size()))));
    size_t start = 0;
    while (start < text.size()) {
        size_t nl = text.find('\n', start);
        std::string lineText = text.substr(start, nl - start);
        size_t tab = lineText.find('\t');
        if (tab != std::string::npos)
            result.emplace(lineText.substr(0, tab), lineText.substr(tab + 1));
        start = nl == std::string::npos ? text.size() : nl + 1;
    }
    return result;
}

std::string get(const Info& i, const std::string& key)
{
    auto it = i.find(key);
    return it == i.end() ? std::string("<none>") : it->second;
}

std::vector<std::string> all(const Info& i, const std::string& key)
{
    std::vector<std::string> v;
    for (auto [a, b] = i.equal_range(key); a != b; ++a)
        v.push_back(a->second);
    return v;
}

void expect(bool ok, const char* what, const Info* i = nullptr)
{
    if (ok)
        return;
    ++failures;
    std::printf("FAIL %s\n", what);
    if (i)
        for (auto& [k, v] : *i)
            std::printf("  %s = %s\n", k.c_str(), v.c_str());
}

} // namespace

int main()
{
    if (!parse(u"Gadget.h", true) || !parse(u"Gadget.cpp", false)) {
        std::printf("FAIL parsing the fixture\n");
        return 1;
    }

    // Header: a const virtual method with a nested return type and default arguments.
    Info i = info(u"Gadget.h", 27, 23); // GetSettings
    expect(get(i, "kind") == "method" && get(i, "name") == "GetSettings", "GetSettings: a method", &i);
    expect(get(i, "result") == "UGadget::FSettings", "GetSettings: nested return type qualified, virtual dropped", &i);
    auto params = all(i, "param");
    expect(params.size() == 3 && params[0] == "int32 Index\tIndex" && params[1] == "const FString& Name\tName\tTEXT(\"x\")" &&
               params[2] == "bool bLoud\tbLoud\tfalse",
           "GetSettings: parameters, defaults apart", &i);
    expect(get(i, "qualifiers") == "const" && get(i, "virtual") == "1", "GetSettings: const, virtual", &i);
    expect(get(i, "classes") == "UGadget" && get(i, "classname") == "UGadget" && get(i, "body") == "<none>" &&
               get(i, "atdef") == "<none>",
           "GetSettings: in UGadget, no body here", &i);

    // Static, UPARAM, pointer result.
    i = info(u"Gadget.h", 28, 21); // Make
    expect(get(i, "result") == "UGadget*" && get(i, "static") == "1" && get(i, "param") == "int32& Count\tCount",
           "Make: static, UPARAM dropped", &i);

    // An inline definition: FORCEINLINE dropped, a body, and on it.
    i = info(u"Gadget.h", 33, 24); // GetCount
    expect(get(i, "result") == "int32" && get(i, "body") != "<none>" && get(i, "atdef") == "1" && get(i, "qualifiers") == "const",
           "GetCount: inline definition with a body", &i);

    i = info(u"Gadget.h", 33, 44); // "return" inside GetCount's body
    expect(get(i, "name") == "GetCount" && get(i, "atdef") == "1", "inside an inline body: the function", &i);

    // A return type in a namespace, noexcept.
    i = info(u"Gadget.h", 34, 17); // MakePart
    expect(get(i, "result") == "Toys::FPart" && get(i, "qualifiers") == "noexcept", "MakePart: namespace return type, noexcept", &i);

    // Constructor: no result.
    i = info(u"Gadget.h", 24, 5);
    expect(get(i, "kind") == "constructor" && get(i, "result") == "<none>", "UGadget(): a constructor without result", &i);

    // override is not repeated.
    i = info(u"Gadget.h", 36, 17); // Score
    expect(get(i, "qualifiers") == "const" && get(i, "result") == "int", "Score: override dropped", &i);

    // Fields.
    i = info(u"Gadget.h", 41, 11); // Count
    expect(get(i, "kind") == "field" && get(i, "type") == "int32" && get(i, "pod") == "1", "Count: an int32 field", &i);
    i = info(u"Gadget.h", 42, 21); // Names
    expect(get(i, "type") == "TArray<FString>" && get(i, "pod") == "0", "Names: a TArray field", &i);

    // An enum with its base type.
    i = info(u"Gadget.h", 45, 12);
    expect(get(i, "kind") == "enum" && get(i, "enumscoped") == "1" && get(i, "enumbase") == "unsigned char", "EGadgetMode", &i);

    // Source: on a definition whose declaration is in the header.
    i = info(u"Gadget.cpp", 8, 29); // GetSettings
    expect(get(i, "atdef") == "1" && get(i, "body") != "<none>" && get(i, "decl").find("Gadget.h\t27\t") != std::string::npos,
           "GetSettings definition: on it, declared in the header", &i);
    expect(get(i, "result") == "UGadget::FSettings" && all(i, "param").size() == 3, "GetSettings definition: signature", &i);
    expect(get(i, "class").find("Gadget.h") != std::string::npos, "GetSettings definition: its class's extent in the header", &i);

    // A free function with no separate declaration.
    i = info(u"Gadget.cpp", 18, 6);
    expect(get(i, "kind") == "function" && get(i, "defonly") == "1" && get(i, "param") == "int A\tA" &&
               all(i, "param").size() == 2 && all(i, "param")[1] == "int B\tB\t2",
           "FreeHelper: defined only here, default dropped", &i);

    // The class's methods, in order.
    {
        const std::u16string path = kDir + u"Gadget.h";
        std::vector<char16_t> buf(8192);
        uint32_t n = vx_class_members(path.c_str(), 27, 23, 1, buf.data(), static_cast<uint32_t>(buf.size()));
        std::string text = narrow(std::u16string(buf.data(), std::min<uint32_t>(n, 8192)));
        const char* expected[] = {"class\tUGadget\n", "method\tUGadget\t24\t", "method\t~UGadget\t", "method\tGetSettings\t27\t",
                                  "method\tMake\t", "method\tOnPoked\t", "method\tServerPush\t", "method\tGetCount\t33\t23\t1",
                                  "method\tMakePart\t", "method\tDefined\t", "method\tScore\t"};
        size_t at = 0;
        bool ordered = true;
        for (const char* e : expected) {
            size_t f = text.find(e, at);
            if (f == std::string::npos) {
                ordered = false;
                std::printf("  missing: %s\n", e);
                break;
            }
            at = f + 1;
        }
        expect(ordered, "class members in declaration order");
        if (!ordered)
            std::printf("%s\n", text.c_str());
    }

    // Unreal's split UFUNCTIONs, from the symbol index.
    vx_symbols_start();
    vx_index_add_root((kDir).c_str());
    for (int t = 0; t < 200; ++t) {
        uint32_t files = 0, pending = 0, symbols = 0;
        vx_symbols_status(&files, &pending, &symbols);
        if (files >= 3 && pending == 0)
            break;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    i = info(u"Gadget.h", 30, 10); // OnPoked
    expect(get(i, "ue") == "1", "OnPoked: BlueprintNativeEvent", &i);
    i = info(u"Gadget.h", 32, 10); // ServerPush
    expect(get(i, "ue") == "3", "ServerPush: RPC WithValidation", &i);
    i = info(u"Gadget.h", 38, 10); // OnShown
    expect(get(i, "ue") == "4", "OnShown: BlueprintImplementableEvent", &i);
    // Find References: the field Count, not Make's parameter of the same name; a write marked.
    {
        const std::u16string header = kDir + u"Gadget.h";
        uint32_t job = vx_references_start(header.c_str(), 41, 11);
        expect(job != 0, "references: a job for Count");
        uint32_t done = 0, total = 0, finished = 0;
        for (int t = 0; t < 600 && !finished; ++t) {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            vx_references_progress(job, &done, &total, &finished);
        }
        expect(finished && total >= 3, "references: finished over the fixture's files");
        std::vector<char16_t> buf(1 << 16);
        uint32_t n = vx_references_results(job, 0, buf.data(), static_cast<uint32_t>(buf.size()));
        std::vector<std::string> records;
        std::string cur;
        for (uint32_t k = 0; k < n && k < buf.size(); ++k) {
            if (buf[k] == 0) {
                records.push_back(cur);
                cur.clear();
            } else {
                cur.push_back(buf[k] < 0x80 ? static_cast<char>(buf[k]) : '?');
            }
        }
        auto has = [&](const char* file, const char* lineAndKind) {
            return std::any_of(records.begin(), records.end(), [&](const std::string& r) {
                return r.find(file) != std::string::npos && r.find(lineAndKind) != std::string::npos;
            });
        };
        expect(has("Gadget.h", "\t41\t11\t") && has("Gadget.h", "\t33\t"), "references: the declaration and the use in GetCount");
        expect(has("Gadget.cpp", "\t15\t5\twrite\t1\t"), "references: the assignment in Defined is a write");
        expect(!has("Gadget.h", "\t28\t"), "references: Make's parameter Count is another symbol");
        expect(std::all_of(records.begin(), records.end(), [](const std::string& r) { return r.find("\t1\t") != std::string::npos; }),
               "references: all confirmed");
        if (failures)
            for (auto& r : records)
                std::printf("  %s\n", r.c_str());
        char16_t target[256] = {};
        vx_references_target(job, target, 255);
        expect(std::u16string(target).find(u"UGadget::Count") != std::u16string::npos, "references: the target's label");
        vx_references_free(job);
    }
    vx_index_reset();

    vx_release((kDir + u"Gadget.h").c_str());
    vx_release((kDir + u"Gadget.cpp").c_str());
    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
