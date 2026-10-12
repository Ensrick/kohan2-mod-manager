# Loader test without the game: puts the stand-in k2.exe (build\fake_game\k2.exe), the loader and k2mp.dll in a
# scratch folder laid out like the game folder, runs the stand-in and checks Mods\plugins.log.
# k2mp must load and stay off there (the stand-in is not the real k2.exe), and a name that tries to leave Mods\ must be
# skipped. Exit code 0 = pass.
param([string] $Work = (Join-Path ([IO.Path]::GetTempPath()) "k2_loader_test_$PID"))
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot '..\build'
New-Item -ItemType Directory -Force (Join-Path $Work 'Mods') | Out-Null
Copy-Item (Join-Path $build 'fake_game\k2.exe') $Work -Force
Copy-Item (Join-Path $build 'Release\winmm.dll') $Work -Force
Copy-Item (Join-Path $build 'Release\k2mp.dll') (Join-Path $Work 'Mods') -Force
Set-Content (Join-Path $Work 'Mods\plugins.txt') "# test`r`nk2mp.dll`r`n..\evil.dll`r`nmissing.dll`r`n" -Encoding ascii -NoNewline

$out = & (Join-Path $Work 'k2.exe')
$log = Get-Content (Join-Path $Work 'Mods\plugins.log') -Raw
"--- stand-in output: $out"
"--- Mods\plugins.log:"
$log
$checks = [ordered]@{
    'loader banner'          = 'loader: Kohan II plugin loader '
    'k2mp loaded'            = 'k2mp: Multiplayer Fixes '
    'k2mp refuses a non-game' = 'save copies: not available for this k2.exe'
    'k2mp reported'          = 'k2mp.dll stayed off'
    'unsafe name skipped'    = 'skipped "..\\evil.dll"'
    'missing dll reported'   = 'could not load missing.dll'
}
$fail = 0
foreach ($c in $checks.GetEnumerator()) {
    $ok = $log -match [regex]::Escape($c.Value).Replace('\\\\', '\\')
    '{0} {1}' -f ($(if ($ok) { 'ok  ' } else { 'FAIL' })), $c.Key
    if (-not $ok) { $fail++ }
}
# Single files only, never a recursive delete.
foreach ($f in 'k2.exe', 'winmm.dll', 'Mods\k2mp.dll', 'Mods\plugins.txt', 'Mods\plugins.log') { Remove-Item (Join-Path $Work $f) -ErrorAction SilentlyContinue }
exit $fail
