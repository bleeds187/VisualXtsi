// Tests for Unreal project argument discovery on the fixture projects.
#include "project_args.h"

#include <algorithm>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int failures = 0;

std::string samples(const char* rel)
{
    std::string s = std::string(VX_SAMPLES_DIR) + "/" + rel;
    std::replace(s.begin(), s.end(), '/', '\\');
    return s;
}

bool hasArg(const vx::VcxprojArgs& a, const std::string& needle)
{
    return std::any_of(a.args.begin(), a.args.end(), [&](const std::string& s) { return s.find(needle) != std::string::npos; });
}

bool hasForcedInclude(const vx::VcxprojArgs& a, const std::string& name)
{
    for (size_t i = 0; i + 1 < a.args.size(); ++i)
        if (a.args[i] == "-include" && a.args[i + 1].find(name) != std::string::npos)
            return true;
    return false;
}

void expect(bool ok, const char* what, const vx::VcxprojArgs* a = nullptr)
{
    if (ok)
        return;
    ++failures;
    std::printf("FAIL %s\n", what);
    if (a) {
        std::printf("  vcxproj: %s\n  based on: %s\n", a->vcxproj.c_str(), a->basedOn.c_str());
        for (auto& s : a->args)
            std::printf("  %s\n", s.c_str());
    }
}

} // namespace

int main()
{
    vx::VcxprojArgs a;

    // UE5: header resolved through its module's .cpp, via property indirection.
    bool ok = vx::argsFromUnrealProject(samples("ue5_project/Source/FakeGame/Public/FakeThing.h"), a);
    expect(ok, "UE5 header discovered");
    expect(a.vcxproj.find("Fake5.vcxproj") != std::string::npos, "UE5 project file", &a);
    expect(a.basedOn.find("FakeThing.cpp") != std::string::npos, "UE5 header uses its module's source", &a);
    expect(hasArg(a, "c++-header") && hasArg(a, "-std=c++20"), "UE5 language options", &a);
    expect(hasArg(a, "-DSAL_NO_ATTRIBUTE_DECLARATIONS=1"), "UE5 /D from AdditionalOptions", &a);
    expect(hasForcedInclude(a, "Definitions.FakeGame.h"), "UE5 module definitions forced include", &a);
    expect(!hasArg(a, "SharedPCH"), "UE5 shared PCH is skipped", &a);
    expect(hasArg(a, "Source\\FakeGame\\Public") && hasArg(a, "Inc\\FakeGame\\UHT"), "UE5 include dirs", &a);
    expect(!hasArg(a, "Source\\Other\\Public"), "UE5 per-module include dirs", &a);
    expect(a.moduleSources.size() == 1 && a.moduleSources[0].find("FakeThing.cpp") != std::string::npos,
           "UE5 module sources", &a);
    expect(a.projectRoot.find("ue5_project") != std::string::npos, "UE5 project root", &a);

    ok = vx::argsFromUnrealProject(samples("ue5_project/Source/Other/Private/Other.cpp"), a);
    expect(ok && a.basedOn.find("Other.cpp") != std::string::npos && hasForcedInclude(a, "Definitions.Other.h") &&
               hasArg(a, "-x") && hasArg(a, "Source\\Other\\Public"),
           "UE5 exact source item", &a);

    // A platform extension of a module (Platforms/<Platform>/Source/<Module>, made-up platform):
    // the arguments of the module it extends, not of whichever module happens to sort first.
    ok = vx::argsFromUnrealProject(samples("ue5_project/Platforms/FakeConsole/Source/Other/Public/OtherFakeConsole.h"), a);
    expect(ok && a.basedOn.find("Other.cpp") != std::string::npos && hasForcedInclude(a, "Definitions.Other.h"),
           "UE5 platform extension uses the module it extends", &a);

    // UE 4.27 (shape of a real generated project): per-file include directories, a forced
    // PCH.<Module>.h carrying the definitions, C++14, and configuration groups for other
    // platforms that must not leak in.
    ok = vx::argsFromUnrealProject(samples("ue427_project/Source/Fake4Game/Fake4Thing.h"), a);
    expect(ok, "UE4 header discovered");
    expect(a.basedOn.find("Fake4Thing.cpp") != std::string::npos, "UE4 header uses its module's source", &a);
    expect(!hasArg(a, "WRONG_CONFIGURATION") && !hasArg(a, "ZW"), "UE4 configuration (Development_Editor|x64)", &a);
    expect(hasForcedInclude(a, "PCH.Fake4Game.h"), "UE4 module forced include", &a);
    expect(hasArg(a, "-std=c++14") && !hasArg(a, "-std=c++20"), "UE4 language standard", &a);
    expect(hasArg(a, "Source\\Fake4Game") && hasArg(a, "UE4Editor\\Inc\\Fake4Game"), "UE4 include dirs", &a);

    // Files outside any Unreal project are not claimed.
    expect(!vx::argsFromUnrealProject(samples("ue_like/MyActor.h"), a), "non-project file");

    if (failures == 0)
        std::printf("all tests passed\n");
    return failures == 0 ? 0 : 1;
}
