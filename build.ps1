# Builds dist\K2ModManager.exe and dist\k2mod.exe (Release, .NET Framework 4.8, single files).
# The game plugins (native\, 32-bit C++) are built first and embedded in both exes; without Visual Studio's C++ tools
# the exes are built without plugins (-SkipNative does that on purpose).
param([switch] $SkipNative)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $SkipNative) {
    & (Join-Path $root 'native\build.ps1') | Out-Host
}
foreach ($p in 'src\K2ModManager\K2ModManager.csproj', 'src\k2mod\k2mod.csproj') {
    dotnet build (Join-Path $root $p) -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root 'src\K2ModManager\bin\Release\net48\K2ModManager.exe') $dist -Force
Copy-Item (Join-Path $root 'src\k2mod\bin\Release\net48\k2mod.exe') $dist -Force
Get-ChildItem $dist -Filter *.exe | ForEach-Object { '{0,-18} {1,9:N0} bytes' -f $_.Name, $_.Length }
