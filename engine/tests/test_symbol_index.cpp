// Tests for the symbol index: initial indexing, search, live updates and the on-disk cache.
#include "file_index.h"
#include "symbol_index.h"

#include <windows.h>

#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <string>
#include <thread>

namespace fs = std::filesystem;

namespace {

int failures = 0;

void write(const fs::path& p, const char* text)
{
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary) << text;
}

bool waitFor(const std::function<bool()>& condition)
{
    for (int i = 0; i < 250; ++i) { // up to 5 s
        if (condition())
            return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return false;
}

bool idle()
{
    return vx::symbols::status().pending == 0;
}

std::u16string top(const char16_t* query)
{
    auto hits = vx::symbols::find(query, 5);
    return hits.empty() ? std::u16string() : hits.front().symbol.name;
}

void expect(bool ok, const char* what)
{
    if (!ok) {
        ++failures;
        std::printf("FAIL %s\n", what);
    }
}

} // namespace

int main()
{
    const fs::path root = VX_TEST_DIR;
    std::error_code ec;
    fs::remove_all(root, ec);
    const fs::path cache = root / "cache" / "symbols.cache";
    SetEnvironmentVariableW(L"VX_SYMBOL_CACHE", cache.c_str());
    write(root / "Game" / "Game.uproject", "{}");
    write(root / "Game" / "Source" / "Game" / "Thing.h",
          "#pragma once\nUCLASS()\nclass GAME_API AThing : public AActor\n{\n    GENERATED_BODY()\npublic:\n"
          "    void ThingMethod();\n    int32 ThingCount = 0;\n};\n");
    write(root / "Game" / "Source" / "Game" / "Thing.cpp",
          "#include \"Thing.h\"\nvoid AThing::ThingMethod()\n{\n}\n");
    write(root / "Game" / "Source" / "Game" / "Other.h", "#pragma once\nstruct FOtherThing {};\n");

    vx::symbols::start();
    vx::files::addRoot((root / "Game").wstring());
    expect(waitFor([] { return vx::symbols::status().files == 3 && idle(); }), "initial index (3 files)");
    expect(top(u"athing") == u"AThing", "class found");
    {
        auto hits = vx::symbols::find(u"AThing", 5);
        expect(!hits.empty() && hits[0].symbol.kind == vx::symbols::Kind::Class && hits[0].symbol.line == 3,
               "type definition ranks first, with its line");
    }
    expect(top(u"AThing::tm") == u"ThingMethod", "Scope::name queries");
    {
        auto defs = vx::symbols::lookup(u"ThingMethod", 5);
        expect(defs.size() == 2 && defs[0].symbol.definition && defs[0].path.ends_with(L"Thing.cpp"),
               "lookup puts the definition (in the .cpp) first");
    }

    // Live updates: a new declaration appears, a deleted file's symbols go away.
    write(root / "Game" / "Source" / "Game" / "Other.h", "#pragma once\nstruct FOtherThing {};\nenum class ENewKind { A };\n");
    expect(waitFor([] { return top(u"enewkind") == u"ENewKind"; }), "edited file is rescanned");
    fs::remove(root / "Game" / "Source" / "Game" / "Other.h", ec);
    expect(waitFor([] { return vx::symbols::find(u"FOtherThing", 5).empty(); }), "deleted file's symbols are dropped");

    // The cache: written when the queue drains, reused after a restart.
    expect(waitFor([&] { return idle() && fs::exists(cache); }), "cache written");
    vx::symbols::save();
    vx::symbols::reset();
    vx::files::reset();
    expect(vx::symbols::find(u"AThing", 5).empty(), "reset forgets symbols");
    std::ifstream in(cache);
    std::string contents((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    expect(contents.find("ThingMethod") != std::string::npos && contents.find("FOtherThing") == std::string::npos,
           "cache holds present files only");
    vx::symbols::start();
    vx::files::addRoot((root / "Game").wstring());
    expect(waitFor([] { return vx::symbols::status().files == 2 && idle(); }), "restart indexes from the cache");
    expect(top(u"thingmethod") == u"ThingMethod", "symbols available after restart");

    vx::symbols::reset();
    vx::files::reset();
    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
