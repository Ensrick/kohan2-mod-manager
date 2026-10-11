# Checks the C# tools against kohan2-battleborn's Python tools (needs that repo next to this
# one and Kohan II installed): the package must build byte-identical to `bb.py build --public`.
param([string]$Battleborn = (Join-Path $PSScriptRoot '..\..\kohan2-battleborn'))
$ErrorActionPreference = 'Stop'
$k2mod = Join-Path $PSScriptRoot '..\src\k2mod\bin\Release\net48\k2mod.exe'
Push-Location $Battleborn
try {
    py -3 -I tools/bb.py build --public | Out-Null
    $pkg = (py -3 -I tools/bb.py package) -replace '^wrote (.+?\.k2mod):.*$', '$1'
    $out = Join-Path $env:TEMP 'k2mod-parity.rwd'
    & $k2mod rwd $pkg -o $out | Out-Null
    $a = (Get-FileHash $out).Hash
    $b = (Get-FileHash 'build\public\Battleborn.rwd').Hash
    if ($a -ne $b) { throw "DIFFERENT: k2mod $a vs bb.py $b" }
    "IDENTICAL: $a"
} finally { Pop-Location }
