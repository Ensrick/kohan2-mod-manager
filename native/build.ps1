# Builds the 32-bit plugin DLLs: build\Release\winmm.dll (plugin loader), k2mp.dll (Multiplayer Fixes) and the tests.
# Needs Visual Studio 2022 (or its Build Tools) with the C++ workload.
param([ValidateSet('Release', 'Debug')] [string] $Config = 'Release')
$ErrorActionPreference = 'Stop'
$cmake = 'cmake'
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    foreach ($vs in (& $vswhere -all -products * -property installationPath)) {
        $c = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        if (Test-Path $c) { $cmake = $c; break }
    }
}
$build = Join-Path $PSScriptRoot 'build'
& $cmake -S $PSScriptRoot -B $build -G 'Visual Studio 17 2022' -A Win32 | Out-Host
if ($LASTEXITCODE) { throw "cmake configure failed ($LASTEXITCODE)" }
& $cmake --build $build --config $Config | Out-Host
if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }
Get-ChildItem (Join-Path $build $Config) -Filter *.dll | Select-Object Name, Length, LastWriteTime
