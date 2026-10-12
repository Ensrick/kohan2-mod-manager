# Runs every offline test of the native plugins. Exit code 0 = all pass.
#   -DecryptedExe <path>  a copy of k2.exe with its code section decrypted (the Steam exe is encrypted on disk);
#                         without it the signature test is skipped, and says so.
# Build first: native\build.ps1
param([string] $DecryptedExe)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '..\build\Release'
$failed = @()

"=== hooktest: hook layer and save-copy stubs"
& (Join-Path $bin 'hooktest.exe')
if ($LASTEXITCODE) { $failed += 'hooktest' }

"=== loader_test: plugin loader with a stand-in k2.exe"
& (Join-Path $PSScriptRoot 'loader_test.ps1') | Select-Object -Last 6
if ($LASTEXITCODE) { $failed += 'loader_test' }

if ($DecryptedExe) {
    "=== sigtest: every signature and call site against $(Split-Path $DecryptedExe -Leaf)"
    & (Join-Path $bin 'sigtest.exe') (Join-Path $bin 'k2mp.dll') $DecryptedExe
    if ($LASTEXITCODE) { $failed += 'sigtest' }
} else {
    "=== sigtest: SKIPPED (pass -DecryptedExe)"
}

if ($failed) { "FAILED: $($failed -join ', ')"; exit 1 }
"all passed"
exit 0
