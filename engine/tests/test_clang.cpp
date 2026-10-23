// End-to-end test of vx_parse / vx_macro_info on the UE-like sample.
#include "vx/vx.h"

#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <string>
#include <thread>
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
    {
        vx_config compdbOnly{1, 1, 0, 1};
        vx_configure(&compdbOnly);
        expect(vx_discover_args(ue5Header.c_str(), nullptr, 0, &source) == 0 && source == VX_ARGS_NONE,
               "args_source 1 (compile_commands.json only) skips Unreal project files");
        vx_config defaults{1, 1, 0, 0};
        vx_configure(&defaults);
    }
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

    // Go to Definition: the parser for what this TU sees, the symbol index for other files.
    {
        const std::u16string dir = std::u16string(VX_SAMPLES_DIR) + u"/goto";
        const std::u16string main = dir + u"/Main.cpp", impl = dir + u"/Shapes.cpp";
        vx_symbols_start();
        vx_index_add_root(dir.c_str());
        uint32_t files = 0, pending = 1;
        for (int i = 0; i < 250 && !(files >= 3 && pending == 0); ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            vx_symbols_status(&files, &pending, nullptr);
        }
        auto gotoAt = [](const std::u16string& file, uint32_t line, uint32_t col) {
            std::vector<char16_t> buf(4096);
            const uint32_t n = vx_goto_definition(file.c_str(), line, col, buf.data(), static_cast<uint32_t>(buf.size()));
            std::string first;
            for (uint32_t k = 0; k < n && buf[k] != 0; ++k)
                first.push_back(buf[k] < 0x80 ? static_cast<char>(buf[k]) : '?');
            return first; // "path\tline\tcolumn\tlabel" of the best target
        };
        auto expectTarget = [&](const std::string& got, const char* file, const char* lineCol, const char* what) {
            const bool match = got.find(file) != std::string::npos && got.find(lineCol) != std::string::npos;
            if (!match)
                std::printf("  got: %s\n", got.c_str());
            expect(match, what);
        };
        expect(vx_parse(main.c_str(), nullptr, 0, nullptr, nullptr, 0) == VX_OK, "parse Main.cpp");
        expectTarget(gotoAt(main, 6, 7), "Shapes.cpp", "\t3\t13\t", "out-of-line method via the symbol index");
        expectTarget(gotoAt(main, 7, 14), "Shapes.h", "\t10\t9\t", "inline member defined in the header");
        expectTarget(gotoAt(main, 5, 5), "Shapes.h", "\t6\t7\t", "class definition");
        expectTarget(gotoAt(main, 7, 25), "Shapes.h", "\t4\t9\t", "macro definition");
        // Another class's Paint() must not stand in for the definition Canvas::Paint lacks.
        expectTarget(gotoAt(main, 13, 7), "Shapes.h", "\t17\t10\t", "undefined method: its own declaration");
        vx_release(main.c_str());
        expect(vx_parse(impl.c_str(), nullptr, 0, nullptr, nullptr, 0) == VX_OK, "parse Shapes.cpp");
        expectTarget(gotoAt(impl, 3, 14), "Shapes.h", "\t9\t10\t", "from a definition to its declaration");
        vx_release(impl.c_str());
        // Not parsed at all: the word under the caret, through the symbol index.
        expectTarget(gotoAt(main, 6, 8), "Shapes.cpp", "\t3\t13\t", "unparsed file falls back to the index");
        vx_index_reset();
    }

    // Shared module PCH: built from the "engine" headers both sources include, leaving out
    // the one that does not compile and the project's own header; then used by new parses.
    namespace fs = std::filesystem;
    const fs::path pchDir = VX_TEST_PCH_DIR;
    std::error_code ec;
    fs::remove_all(pchDir, ec);
    fs::create_directories(pchDir, ec);
    { // an old, unused cache entry that the 1 MB cap must evict
        std::ofstream stale(pchDir / "0000000000000000.pch", std::ios::binary);
        stale << std::string(2 << 20, 'x');
    }
    fs::last_write_time(pchDir / "0000000000000000.pch", fs::file_time_type::clock::now() - std::chrono::hours(24), ec);
    _wputenv_s(L"VX_PCH_DIR", pchDir.wstring().c_str());
    _wputenv_s(L"VX_PCH_CACHE_MB", L"1");
    const std::u16string sourceA = std::u16string(VX_SAMPLES_DIR) + u"/pch_project/Source/PchGame/Private/A.cpp";
    const std::u16string sourceB = std::u16string(VX_SAMPLES_DIR) + u"/pch_project/Source/PchGame/Private/B.cpp";
    expect(vx_prepare_module_pch(sourceA.c_str()) == VX_PCH_READY, "module PCH built");
    expect(vx_prepare_module_pch(sourceB.c_str()) == VX_PCH_READY, "module PCH shared by the module");
    std::string pchHeader;
    size_t pchCount = 0;
    for (auto& e : fs::directory_iterator(pchDir, ec)) {
        if (e.path().extension() == ".pch")
            ++pchCount;
        if (e.path().extension() == ".h") {
            std::ifstream in(e.path());
            pchHeader.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
        }
    }
    expect(pchCount == 1 && !fs::exists(pchDir / "0000000000000000.pch"), "one PCH for the module, stale one evicted");
    expect(contains(pchHeader, "CoreMinimal.h") && contains(pchHeader, "FakeEngine/Actor.h") &&
               !contains(pchHeader, "Broken.h") && !contains(pchHeader, "PchThing.h"),
           "PCH headers: common engine headers only, without the broken one");
    status = vx_parse(sourceB.c_str(), nullptr, 0, nullptr, nullptr, 0);
    ok = status == VX_OK && query(sourceB, 6, 30, info);
    expect(ok && info.name == "FAKE_ENGINE_VERSION" && contains(info.location, "CoreMinimal.h") &&
               info.expansion == "5",
           "macro from the PCH resolves", &info);
    vx_release(sourceB.c_str());

    // Editing a header inside the PCH makes it stale: new parses stop using it, and the
    // next prepare rebuilds it. Works on a copy of the fixtures so the repo is untouched.
    {
        const fs::path copyRoot = pchDir.parent_path() / "test-pch-src";
        fs::remove_all(copyRoot, ec);
        fs::create_directories(copyRoot, ec);
        const fs::path samples = std::u16string(VX_SAMPLES_DIR);
        fs::copy(samples / "pch_project", copyRoot / "pch_project", fs::copy_options::recursive, ec);
        fs::copy(samples / "pch_engine", copyRoot / "pch_engine", fs::copy_options::recursive, ec);
        _wputenv_s(L"VX_PCH_REVALIDATE_SEC", L"0");
        const std::u16string copyA = (copyRoot / "pch_project/Source/PchGame/Private/A.cpp").u16string();
        const std::u16string copyB = (copyRoot / "pch_project/Source/PchGame/Private/B.cpp").u16string();
        expect(vx_prepare_module_pch(copyA.c_str()) == VX_PCH_READY, "PCH for the copied module");
        auto newestPch = [&] {
            fs::file_time_type newest{};
            for (auto& e : fs::directory_iterator(pchDir, ec))
                if (e.path().extension() == ".pch")
                    newest = std::max(newest, e.last_write_time());
            return newest;
        };
        const auto before = newestPch();
        {
            std::ofstream edit(copyRoot / "pch_engine/Public/CoreMinimal.h", std::ios::app);
            edit << "\n#define FAKE_ENGINE_EXTRA 1\n";
        }
        // A new parse must not use the stale PCH: the new macro has to be visible.
        const std::u16string probe = u"#include \"CoreMinimal.h\"\nint Extra = FAKE_ENGINE_EXTRA;\n";
        status = vx_parse(copyB.c_str(), nullptr, 0, nullptr, probe.c_str(), static_cast<uint32_t>(probe.size()));
        ok = status == VX_OK && query(copyB, 2, 13, info);
        expect(ok && info.name == "FAKE_ENGINE_EXTRA", "stale PCH is not used after a header edit", &info);
        vx_release(copyB.c_str());
        expect(vx_prepare_module_pch(copyA.c_str()) == VX_PCH_READY && newestPch() > before,
               "PCH rebuilt after a header edit");
        _wputenv_s(L"VX_PCH_REVALIDATE_SEC", L"");
    }

    // Settings: the shared PCH can be switched off, and the cache cleared.
    vx_config config{1, 0, 0};
    vx_configure(&config);
    expect(vx_prepare_module_pch(sourceA.c_str()) == VX_PCH_NOT_APPLICABLE, "shared PCH switched off");
    config.shared_pch = 1;
    vx_configure(&config);
    expect(vx_clear_pch_cache() > 0, "PCH cache cleared");
    bool anyLeft = false;
    for (auto& e : fs::directory_iterator(pchDir, ec))
        anyLeft |= e.path().extension() == ".pch";
    expect(!anyLeft, "no PCH left after clearing");

    _wputenv_s(L"VX_PCH_DIR", L"");
    _wputenv_s(L"VX_PCH_CACHE_MB", L"");

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
