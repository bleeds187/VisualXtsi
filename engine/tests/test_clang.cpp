// End-to-end test of vx_parse / vx_macro_info on the UE-like sample.
#include "vx/vx.h"

#include <chrono>
#include <cstdio>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace {

int failures = 0;

struct MacroInfo {
    std::string name, definition, location, expansion;
};

std::string narrow(const std::u16string& s)
{
    std::string r;
    for (char16_t c : s)
        r.push_back(c < 0x80 ? static_cast<char>(c) : '?');
    return r;
}

bool query(const std::u16string& path, uint32_t line, uint32_t col, MacroInfo& info)
{
    std::vector<char16_t> buf(16);
    uint32_t n = vx_macro_info(path.c_str(), line, col, buf.data(), static_cast<uint32_t>(buf.size()));
    if (n == 0)
        return false;
    if (n > buf.size()) {
        buf.resize(n);
        n = vx_macro_info(path.c_str(), line, col, buf.data(), n);
    }
    std::vector<std::string> parts;
    std::u16string cur;
    for (uint32_t i = 0; i < n; ++i) {
        if (buf[i] == 0) {
            parts.push_back(narrow(cur));
            cur.clear();
        } else {
            cur.push_back(buf[i]);
        }
    }
    if (parts.size() != 4)
        return false;
    info = {parts[0], parts[1], parts[2], parts[3]};
    return true;
}

void expect(bool ok, const char* what, const MacroInfo* info = nullptr)
{
    if (ok)
        return;
    ++failures;
    std::printf("FAIL %s\n", what);
    if (info)
        std::printf("  name: %s\n  definition: %s\n  location: %s\n  expansion:\n%s\n", info->name.c_str(),
                    info->definition.c_str(), info->location.c_str(), info->expansion.c_str());
}

bool contains(const std::string& s, const char* sub)
{
    return s.find(sub) != std::string::npos;
}

} // namespace

int main()
{
    const std::u16string header = std::u16string(VX_SAMPLES_DIR) + u"/ue_like/MyActor.h";

    auto t0 = std::chrono::steady_clock::now();
    int32_t status = vx_parse(header.c_str(), nullptr, 0, nullptr, nullptr, 0);
    auto t1 = std::chrono::steady_clock::now();
    status = status == VX_OK ? vx_parse(header.c_str(), nullptr, 0, nullptr, nullptr, 0) : status;
    auto t2 = std::chrono::steady_clock::now();
    std::printf("parse %.1f ms, reparse %.1f ms\n", std::chrono::duration<double, std::milli>(t1 - t0).count(),
                std::chrono::duration<double, std::milli>(t2 - t1).count());
    if (status != VX_OK) {
        std::printf("FAIL vx_parse returned %d\n", status);
        return 1;
    }

    MacroInfo info;
    bool ok = query(header, 12, 5, info); // GENERATED_BODY()
    expect(ok && info.name == "GENERATED_BODY", "GENERATED_BODY found", &info);
    expect(contains(info.definition, "#define GENERATED_BODY(...) BODY_MACRO_COMBINE("), "GENERATED_BODY definition", &info);
    expect(contains(info.location, "ObjectMacros.h(14)"), "GENERATED_BODY location", &info);
    expect(contains(info.expansion, "(void) X;") && !contains(info.expansion, "Name,"),
           "#undef'd X-macro is not used in the expansion", &info);
    expect(contains(info.expansion, "typedef ABaseActor Super;") && contains(info.expansion, "return \"AMyActor\";") &&
               contains(info.expansion, "\"AMyActor\" \"_Generated\"") && contains(info.expansion, "StaticPropertyCount"),
           "GENERATED_BODY expansion", &info);

    ok = query(header, 9, 3, info); // UCLASS(BlueprintType) -> empty prolog
    expect(ok && info.name == "UCLASS" && info.expansion == "/* expands to nothing */", "UCLASS expands to empty prolog",
           &info);

    ok = query(header, 12, 18, info);
    expect(ok && info.name == "GENERATED_BODY", "hover at end of name", &info);

    ok = query(header, 15, 6, info); // UPROPERTY(EditAnywhere)
    expect(ok && info.name == "UPROPERTY", "UPROPERTY found", &info);

    expect(!query(header, 16, 9, info), "non-macro returns nothing");

    // Edit the buffer: moving GENERATED_BODY to a line without a generated macro.
    const std::u16string edited = u"#pragma once\n#include \"FakeUObject/ObjectMacros.h\"\n#include \"MyActor.generated.h\"\n"
                                  u"class X\n{\n    GENERATED_BODY()\n};\n";
    auto t3 = std::chrono::steady_clock::now();
    status = vx_parse(header.c_str(), nullptr, 0, nullptr, edited.c_str(), static_cast<uint32_t>(edited.size()));
    auto t4 = std::chrono::steady_clock::now();
    std::printf("reparse with unsaved edit %.1f ms\n", std::chrono::duration<double, std::milli>(t4 - t3).count());
    ok = status == VX_OK && query(header, 6, 7, info);
    expect(ok && contains(info.expansion, "Sample_MyActor_h_6_GENERATED_BODY;"), "unsaved edit uses new __LINE__", &info);

    vx_release(header.c_str());

    // Arguments discovered from a UE5-style generated project: the forced
    // Definitions header and the UHT include directory make these resolve.
    const std::u16string ue5Header = std::u16string(VX_SAMPLES_DIR) + u"/ue5_project/Source/FakeGame/Public/FakeThing.h";
    uint32_t source = 0;
    expect(vx_discover_args(ue5Header.c_str(), nullptr, 0, &source) > 0 && source == VX_ARGS_VCXPROJ,
           "vx_discover_args finds the vcxproj");
    status = vx_parse(ue5Header.c_str(), nullptr, 0, nullptr, nullptr, 0);
    ok = status == VX_OK && query(ue5Header, 5, 2, info);
    expect(ok && info.name == "FAKEGAME_API" && contains(info.location, "Definitions.FakeGame.h") &&
               info.expansion == "__declspec(dllexport)",
           "macro from discovered forced include", &info);
    vx_release(ue5Header.c_str());

    // UE 4.27 shape: the API macro comes through the forced PCH.<Module>.h -> Definitions.<Module>.h.
    const std::u16string ue4Header = std::u16string(VX_SAMPLES_DIR) + u"/ue427_project/Source/Fake4Game/Fake4Thing.h";
    status = vx_parse(ue4Header.c_str(), nullptr, 0, nullptr, nullptr, 0);
    ok = status == VX_OK && query(ue4Header, 5, 2, info);
    expect(ok && info.name == "FAKE4GAME_API" && contains(info.location, "Definitions.Fake4Game.h"),
           "UE4 macro from the forced module header", &info);
    vx_release(ue4Header.c_str());

    // Semantic classification: every occurrence of each name gets exactly the expected kind.
    const std::u16string semantic = std::u16string(VX_SAMPLES_DIR) + u"/semantic/Semantic.cpp";
    status = vx_parse(semantic.c_str(), nullptr, 0, nullptr, nullptr, 0);
    std::vector<vx_span> spans(vx_semantic_tokens(semantic.c_str(), nullptr, 0));
    vx_semantic_tokens(semantic.c_str(), spans.data(), static_cast<uint32_t>(spans.size()));
    std::u16string text;
    if (FILE* f = nullptr; _wfopen_s(&f, reinterpret_cast<const wchar_t*>(semantic.c_str()), L"rb") == 0 && f) {
        std::string bytes;
        char buf[4096];
        for (size_t n; (n = fread(buf, 1, sizeof buf, f)) > 0;)
            bytes.append(buf, n);
        fclose(f);
        text.assign(bytes.begin(), bytes.end()); // the fixture is ASCII
    }
    std::map<std::string, std::set<uint32_t>> kinds;
    for (const vx_span& s : spans)
        kinds[narrow(text.substr(s.start, s.length))].insert(s.kind);
    const std::map<std::string, uint32_t> expected = {
        {"Game", VX_KIND_NAMESPACE},       {"EColor", VX_KIND_TYPE},          {"Red", VX_KIND_ENUM_CONSTANT},
        {"Green", VX_KIND_ENUM_CONSTANT}, {"FPoint", VX_KIND_TYPE},          {"FPoint3", VX_KIND_TYPE},
        {"X", VX_KIND_FIELD},             {"Count", VX_KIND_GLOBAL},         {"Length", VX_KIND_METHOD},
        {"Origin", VX_KIND_STATIC_METHOD}, {"Draw", VX_KIND_VIRTUAL_METHOD}, {"GlobalCounter", VX_KIND_GLOBAL},
        {"Twice", VX_KIND_FUNCTION},      {"TValue", VX_KIND_TYPE},          {"Size", VX_KIND_PARAMETER},
        {"Value", VX_KIND_PARAMETER},     {"Result", VX_KIND_LOCAL},         {"Calls", VX_KIND_GLOBAL},
        {"Point", VX_KIND_LOCAL},         {"Color", VX_KIND_LOCAL},          {"Alias", VX_KIND_TYPE},
        {"Use", VX_KIND_FUNCTION},
    };
    expect(status == VX_OK && !spans.empty(), "semantic tokens produced");
    for (auto& [name, kind] : expected) {
        auto it = kinds.find(name);
        const bool match = it != kinds.end() && it->second == std::set<uint32_t>{kind};
        if (!match) {
            std::printf("  %s: expected %u, got", name.c_str(), kind);
            if (it != kinds.end())
                for (uint32_t k : it->second)
                    std::printf(" %u", k);
            std::printf("\n");
        }
        expect(match, "semantic kind");
    }
    expect(!kinds.contains("override"), "'override' is not colored");
    vx_release(semantic.c_str());

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
