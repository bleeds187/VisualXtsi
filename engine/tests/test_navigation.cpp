// Tests for header/source counterpart lookup on the nav_project fixture.
#include "navigation.h"

#include <algorithm>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int failures = 0;

std::string sample(const char* rel)
{
    std::string s = std::string(VX_SAMPLES_DIR) + "/nav_project/" + rel;
    std::replace(s.begin(), s.end(), '/', '\\');
    return s;
}

bool endsWith(const std::string& s, const std::string& suffix)
{
    std::string a = s, b = suffix;
    std::replace(a.begin(), a.end(), '/', '\\');
    std::replace(b.begin(), b.end(), '/', '\\');
    return a.size() >= b.size() && a.compare(a.size() - b.size(), b.size(), b) == 0;
}

void expectNone(const char* from)
{
    const auto found = vx::nav::counterparts(sample(from), {});
    if (found.empty())
        return;
    ++failures;
    std::printf("FAIL %s -> expected none, got %s\n", from, found.front().c_str());
}

void expectFirst(const char* from, const char* expected, const std::vector<std::string>& includeDirs = {})
{
    const auto found = vx::nav::counterparts(sample(from), includeDirs);
    if (!found.empty() && endsWith(found.front(), expected))
        return;
    ++failures;
    std::printf("FAIL %s -> expected %s, got:", from, expected);
    for (auto& f : found)
        std::printf(" %s", f.c_str());
    std::printf("\n");
}

} // namespace

int main()
{
    expectFirst("Source/NavGame/Public/Things/Widget.h", "Source/NavGame/Private/Things/Widget.cpp");
    expectFirst("Source/NavGame/Private/Things/Widget.cpp", "Source/NavGame/Public/Things/Widget.h");
    expectFirst("Source/NavGame/Public/Gadget.h", "Source/NavGame/Private/Sub/Gadget.cpp");
    expectFirst("Source/NavGame/Private/Sub/Gadget.cpp", "Source/NavGame/Public/Gadget.h");
    expectFirst("Source/NavGame/Private/Local.h", "Source/NavGame/Private/Local.cpp");
    expectFirst("Source/NavGame/Private/Local.cpp", "Source/NavGame/Private/Local.h");
    expectFirst("Source/NavGame/Classes/OldStyle.h", "Source/NavGame/Private/OldStyle.cpp");
    expectFirst("Source/NavGame/Private/OldStyle.cpp", "Source/NavGame/Classes/OldStyle.h");
    expectFirst("Source/NavGame/Private/Impl/Doohickey.cpp", "Source/NavCore/Public/Doohickey.h",
                {sample("Source/NavCore/Public")});

    // Platform extensions (Platforms/<Platform>/Source/<Module>) without a Build.cs of their own.
    expectFirst("Platforms/FakeConsole/Source/NavGame/Public/Pad/FakePad.h", "Platforms/FakeConsole/Source/NavGame/Private/Pad/FakePad.cpp");
    expectFirst("Platforms/FakeConsole/Source/NavGame/Private/Pad/FakePad.cpp", "Platforms/FakeConsole/Source/NavGame/Public/Pad/FakePad.h");
    expectFirst("Platforms/FakeConsole/Source/NavGame/Public/Deep.h", "Platforms/FakeConsole/Source/NavGame/Private/Sub/Deep.cpp");

    if (!vx::nav::counterparts(sample("Source/NavGame/Private/Lonely.cpp"), {}).empty()) {
        ++failures;
        std::printf("FAIL Lonely.cpp should have no counterpart\n");
    }
    // A module's Build.cs and the source implementing it (the IMPLEMENT_*MODULE macro naming it).
    // With a header: Build.cs -> header, then header <-> source as usual.
    expectFirst("Source/NavGame/NavGame.Build.cs", "Source/NavGame/Public/NavGame.h");
    expectFirst("Source/NavGame/Public/NavGame.h", "Source/NavGame/Private/NavGame.cpp");
    expectFirst("Source/NavGame/Private/NavGame.cpp", "Source/NavGame/Public/NavGame.h");
    expectNone("Source/NavGame/Private/NavGameModule.cpp"); // named like one, implements nothing
    // Header-less, whatever it is called: Build.cs <-> source.
    expectFirst("Source/NavTool/NavTool.Build.cs", "Source/NavTool/Private/ToolUnrealModule.cpp");
    expectFirst("Source/NavTool/Private/ToolUnrealModule.cpp", "Source/NavTool/NavTool.Build.cs");
    expectNone("Source/NavTool/Private/NavToolLegacy.cpp"); // the macro is commented out
    // The macro must name this module.
    expectFirst("Source/NavCore/NavCore.Build.cs", "Source/NavCore/Private/Startup.cpp");
    expectFirst("Source/NavCore/Private/Startup.cpp", "Source/NavCore/NavCore.Build.cs");
    expectNone("Source/NavCore/Private/AOtherCore.cpp");
    // No macro at all: <Name>.cpp or <Name>Module.cpp.
    expectFirst("Source/NavPlain/NavPlain.Build.cs", "Source/NavPlain/Private/NavPlainModule.cpp");

    // C#: designer and XAML code-behind pairs.
    expectFirst("Tools/Editor/SettingsForm.cs", "Tools/Editor/SettingsForm.Designer.cs");
    expectFirst("Tools/Editor/SettingsForm.Designer.cs", "Tools/Editor/SettingsForm.cs");
    expectFirst("Tools/Editor/MainWindow.xaml.cs", "Tools/Editor/MainWindow.xaml");

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
