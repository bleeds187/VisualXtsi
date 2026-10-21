# VisualXtsi

A C++ and Unreal Engine productivity extension for Visual Studio 2022 and later, built for large,
multi-platform (including console) codebases, with first-class support for Unreal Engine C++
(UE 4.27 and all UE5 versions).

## Architecture

- **engine/**: a native C++ DLL that holds all of the real logic (parsing, classification,
  and later indexing and navigation). It exposes a flat C ABI (`vx.h`). It doesn't use C++/CLI.
- **vsix/**: a thin C# VSIX shell. It exists only because Visual Studio's editor extension
  points (MEF classifiers, QuickInfo, etc.) are managed-only. It forwards work to the engine
  through P/Invoke.

Text offsets crossing the ABI are in UTF-16 code units, matching the VS editor buffer.

## Building

Requirements: Visual Studio 2022 with the "Desktop development with C++" and
"Visual Studio extension development" workloads, and CMake 3.20+.

```
cmake -S engine -B build/engine -G "Visual Studio 17 2022" -A x64
cmake --build build/engine --config Release
ctest --test-dir build/engine -C Release
msbuild vsix/VisualXtsi.Vsix/VisualXtsi.Vsix.csproj -restore -p:Configuration=Release
```

## License

MIT. See [LICENSE](LICENSE). This repository must not contain Unreal Engine source code.
