// Tests for the file index: initial scan, skipped folders, fuzzy ranking and incremental updates.
#include "file_index.h"

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

void touch(const fs::path& p)
{
    fs::create_directories(p.parent_path());
    std::ofstream(p) << "// test\n";
}

bool waitFor(const std::function<bool()>& condition)
{
    for (int i = 0; i < 200; ++i) { // up to 4 s
        if (condition())
            return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return false;
}

std::wstring first(const std::wstring& query)
{
    auto hits = vx::files::find(query, 5);
    return hits.empty() ? std::wstring() : fs::path(hits.front()).filename().wstring();
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
    touch(root / "Game.uproject");
    touch(root / "Source/Game/Public/LyraCharacterComponent.h");
    touch(root / "Source/Game/Private/LyraCharacterComponent.cpp");
    touch(root / "Source/Game/Private/LocalCacheCleaner.cpp");
    touch(root / "Source/Game/Private/Character.cpp");
    touch(root / "Source/Game/Private/Lucchese.cpp"); // l, c, c without word starts
    touch(root / "Source/Game/Private/LyraHud.cpp");  // l, h, c only through the extension
    touch(root / "Hot/LyraTextHotfixConfig.h");       // l, h, c as word starts, but skipping "Text"
    touch(root / "Source/Game/Character/LyraHeroComponent.h");
    touch(root / "Source/Game/Game.Build.cs");
    touch(root / "Config/DefaultGame.ini");
    touch(root / "Intermediate/Build/Generated.h");      // skipped folder
    touch(root / "Source/Game/Private/readme.md");        // not a code file
    touch(root / "Plugins/Feature/Source/Feature/Private/FeatureModule.cpp");

    expect(vx::files::addProjectOf((root / "Source/Game/Private/Character.cpp").wstring()), "project root found");
    expect(waitFor([] { return vx::files::count() == 12; }), "initial scan (12 files)");
    expect(first(L"lhc") == L"LyraHeroComponent.h", "skipping words costs: lhc -> LyraHeroComponent");
    expect(vx::files::find(L"generated", 10).empty(), "Intermediate is skipped");
    expect(vx::files::find(L"readme", 10).empty(), "non-code files are skipped");

    // Ranking: CamelCase word starts beat scattered letters; exact names win; shorter wins ties.
    expect(first(L"lcco") == L"LyraCharacterComponent.h" || first(L"lcco") == L"LyraCharacterComponent.cpp",
           "word starts: lcco -> LyraCharacterComponent");
    {
        auto hits = vx::files::find(L"lcc", 10);
        auto rank = [&](const wchar_t* name) {
            for (size_t i = 0; i < hits.size(); ++i)
                if (fs::path(hits[i]).filename() == name)
                    return i;
            return hits.size();
        };
        expect(rank(L"Lucchese.cpp") > rank(L"LocalCacheCleaner.cpp") &&
                   rank(L"Lucchese.cpp") > rank(L"LyraCharacterComponent.h") && rank(L"Lucchese.cpp") < hits.size(),
               "word-start matches rank above scattered ones");
    }
    expect(first(L"lycc").starts_with(L"LyraCharacterComponent"), "extension letters are not word starts (lycc)");
    expect(first(L"character.cpp") == L"Character.cpp", "exact name first");
    expect(first(L"defgame") == L"DefaultGame.ini", "config files indexed");
    expect(first(L"feature/private/fm") == L"FeatureModule.cpp", "path queries");
    expect(vx::files::find(L"zzz", 10).empty(), "no match, no results");

    // Incremental updates.
    touch(root / "Source/Game/Private/NewlyAdded.cpp");
    expect(waitFor([] { return first(L"newlyadded") == L"NewlyAdded.cpp"; }), "added file appears");
    fs::rename(root / "Source/Game/Private/NewlyAdded.cpp", root / "Source/Game/Private/Renamed.cpp", ec);
    expect(waitFor([] { return first(L"renamed") == L"Renamed.cpp" && vx::files::find(L"newlyadded", 5).empty(); }),
           "renamed file follows");
    fs::remove(root / "Source/Game/Private/Renamed.cpp", ec);
    expect(waitFor([] { return vx::files::find(L"renamed", 5).empty(); }), "deleted file disappears");
    touch(root / "Intermediate/Build/AlsoGenerated.cpp");
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    expect(vx::files::find(L"alsogenerated", 5).empty(), "changes in skipped folders are ignored");

    vx::files::reset();
    expect(vx::files::count() == 0, "reset clears the index");

    // The engine: Source plus plugins and platform extensions, found by structure (made-up
    // platform names), wherever they sit (Platforms\<P>, Restricted\<Group>\Platforms\<P>).
    {
        const fs::path engine = root / "UE/Engine";
        touch(engine / "Source/Runtime/Widget/Public/Widget.h");
        touch(engine / "Plugins/Runtime/Gadget/Source/Gadget/Private/GadgetModule.cpp");
        touch(engine / "Platforms/FakeConsole/Source/Runtime/Widget/Private/FakeConsoleWidget.cpp");
        touch(engine / "Platforms/FakeConsole/Plugins/FakePad/Source/FakePad/Public/FakePadInput.h");
        touch(engine / "Platforms/FakeConsole/Config/FakeConsoleEngine.ini");
        touch(engine / "Restricted/NotForLicensees/Platforms/OtherBox/Source/Runtime/Core/Private/OtherBoxMemory.cpp");
        touch(engine / "Intermediate/Build/EngineGenerated.h");
        const std::wstring include = (engine / "Source/Runtime/Widget/Public").wstring();
        expect(vx::files::addEngineOf(L"", {include}), "engine found from an include directory");
        expect(waitFor([] { return vx::files::count() == 6; }), "engine source, plugins and platform extensions (6 files)");
        expect(first(L"fakeconsolewidget") == L"FakeConsoleWidget.cpp", "a platform extension's source");
        expect(first(L"fakepadinput") == L"FakePadInput.h", "a platform extension's plugin");
        expect(first(L"fakeconsoleengine") == L"FakeConsoleEngine.ini", "a platform extension's config");
        expect(first(L"otherboxmemory") == L"OtherBoxMemory.cpp", "a restricted platform extension");
        expect(first(L"gadgetmodule") == L"GadgetModule.cpp", "an engine plugin");
        expect(vx::files::find(L"enginegenerated", 5).empty(), "the engine's Intermediate is skipped");
        vx::files::reset();
    }

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
