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
    if (!vx::nav::counterparts(sample("Source/NavGame/NavGame.Build.cs"), {}).empty()) {
        ++failures;
        std::printf("FAIL non-C++ files have no counterpart\n");
    }

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
