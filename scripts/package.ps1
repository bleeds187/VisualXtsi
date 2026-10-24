<#
.SYNOPSIS
    Builds a release VisualXtsi VSIX: the engine (Release), its tests, then the extension.

.DESCRIPTION
    One command for a distributable package. Stops at the first failure, including a failing
    engine test. The VSIX version comes from vsix\VisualXtsi.Vsix\source.extension.vsixmanifest.
    The result is written to dist\VisualXtsi-<version>.vsix; nothing is installed.

.PARAMETER LlvmRoot
    LLVM install providing include\clang-c, lib\libclang.lib, bin\libclang.dll and lib\clang\<n>\include.
    Defaults to VX_LLVM_ROOT, then C:\Program Files\LLVM.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -LlvmRoot D:\tools\clang+llvm-19.1.5-x86_64-pc-windows-msvc
#>
param([string]$LlvmRoot = $env:VX_LLVM_ROOT)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# No lingering build processes afterwards: idle MSBuild nodes (including the ones CMake's builds
# start) and the C# compiler server would otherwise keep files locked, e.g. during a VSIX install.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
if (-not $LlvmRoot) { $LlvmRoot = 'C:\Program Files\LLVM' }
if (-not (Test-Path (Join-Path $LlvmRoot 'include\clang-c\Index.h')) -or -not (Test-Path (Join-Path $LlvmRoot 'bin\libclang.dll'))) {
    throw "libclang not found under '$LlvmRoot'. Pass -LlvmRoot or set VX_LLVM_ROOT."
}

function Step([string]$name) { Write-Host "`n=== $name" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

[xml]$manifest = Get-Content (Join-Path $repo 'vsix\VisualXtsi.Vsix\source.extension.vsixmanifest')
$version = $manifest.PackageManifest.Metadata.Identity.Version
$engineBuild = Join-Path $repo 'build\release\engine'
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (Visual Studio 2022 with the extension development workload is required).' }

Step "Engine (Release) with libclang from $LlvmRoot"
Run cmake @('-S', (Join-Path $repo 'engine'), '-B', $engineBuild, '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DVX_LLVM_ROOT=$LlvmRoot")
Run cmake @('--build', $engineBuild, '--config', 'Release', '--parallel')

Step 'Engine tests'
Run ctest @('--test-dir', $engineBuild, '-C', 'Release', '--output-on-failure')

Step 'Theme tests'
$themeTests = Join-Path $repo 'tests\vsix\ThemeTests\ThemeTests.csproj'
Run $msbuild @($themeTests, '-nologo', '-v:m', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:Configuration=Release')
Run (Join-Path $repo 'tests\vsix\ThemeTests\bin\Release\ThemeTests.exe') @()

Step "VSIX $version (Release)"
$project = Join-Path $repo 'vsix\VisualXtsi.Vsix\VisualXtsi.Vsix.csproj'
# Rebuild: an incremental build can reuse a stale extension.vsixmanifest (e.g. after a version bump).
Run $msbuild @($project, '-restore', '-t:Rebuild', '-nologo', '-v:m', '-nodeReuse:false', '-p:UseSharedCompilation=false',
    '-p:Configuration=Release', '-p:DeployExtension=false',
    "-p:EngineBuildDir=$engineBuild", "-p:VxLlvmRoot=$LlvmRoot")

$built = Join-Path $repo 'vsix\VisualXtsi.Vsix\bin\Release\VisualXtsi.vsix'
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$output = Join-Path $dist "VisualXtsi-$version.vsix"
Copy-Item $built $output -Force

Step 'Checking the package'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($output)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName }
    foreach ($required in 'THIRD_PARTY_NOTICES.md', 'VisualXtsi.dll', 'VisualXtsi.pkgdef', 'vxengine.dll', 'libclang.dll', 'clang-resource/include/stddef.h', 'extension.vsixmanifest') {
        if ($entries -notcontains $required) { throw "The package is missing $required" }
    }
    $packedVersion = ([xml](New-Object IO.StreamReader($zip.GetEntry('extension.vsixmanifest').Open())).ReadToEnd()).PackageManifest.Metadata.Identity.Version
    if ($packedVersion -ne $version) { throw "Packed manifest version $packedVersion, expected $version" }
    $headers = ($entries | Where-Object { $_ -like 'clang-resource/*' }).Count
} finally {
    $zip.Dispose()
}

$size = (Get-Item $output).Length
Write-Host ("`nBuilt {0} ({1:N1} MB, {2} files, {3} builtin headers)" -f $output, ($size / 1MB), $entries.Count, $headers) -ForegroundColor Green
