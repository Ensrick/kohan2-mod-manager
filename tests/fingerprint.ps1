# Checks the game files fingerprint (k2mod fingerprint) on fake game folders in a scratch directory.
# Needs Kohan II installed and k2mod built (dotnet build src\k2mod\k2mod.csproj -c Release); Kohan II must be closed
# (installing and packing refuse while it runs).
# The fake folders hard-link the game's Data.rwd and Music.rwd (never copied, never changed) and hard-link one private
# copy of k2.exe; K2MODS_USERDATA points each at its own fake Documents\Kohan2\data. Nothing is written to the game folder
# or to Documents\Kohan2. The scratch directory is left in place (its path is printed at the end).
# Exit code 0 = every check passed.
param(
    [string]$Game,
    [string]$Scratch = (Join-Path ([IO.Path]::GetTempPath()) ('k2fp-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$k2mod = (Resolve-Path (Join-Path $PSScriptRoot '..\src\k2mod\bin\Release\net48\k2mod.exe')).Path
$utf8 = New-Object Text.UTF8Encoding $false

function Find-Game {
    $roots = @()
    try { $roots += (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    $roots += Join-Path ${env:ProgramFiles(x86)} 'Steam'
    foreach ($root in ($roots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
        $libs = @($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $libs += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
        }
        foreach ($lib in $libs) {
            $g = Join-Path $lib 'steamapps\common\Kohan II'
            if (Test-Path (Join-Path $g 'Data.rwd')) { return (Resolve-Path $g).Path }
        }
    }
    throw 'Kohan II was not found. Pass -Game "<folder with Data.rwd>".'
}
if (-not $Game) { $Game = Find-Game }
New-Item -ItemType Directory -Force $Scratch | Out-Null
$Scratch = (Resolve-Path $Scratch).Path

# ------------------------------------------------------------------ helpers

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    if ($detail) { "$mark  $name  ($detail)" } else { "$mark  $name" }
}

function Put([string]$root, [string]$rel, $content) {
    $path = Join-Path $root ($rel -replace '/', '\')
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    if ($content -is [byte[]]) { [IO.File]::WriteAllBytes($path, $content) }
    else { [IO.File]::WriteAllText($path, $content, $utf8) }
    $path
}

function Copy-Tree([string]$from, [string]$to) {
    Get-ChildItem $from -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($from.Length).TrimStart('\')
        $dest = Join-Path $to $rel
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }
}

# one private copy of k2.exe: fake folders hard-link to it, never to the game's own
$exeCopy = Join-Path $Scratch 'k2.exe'
Copy-Item (Join-Path $Game 'k2.exe') $exeCopy

function New-FakeGame([string]$name) {
    $g = Join-Path $Scratch $name
    New-Item -ItemType Directory -Force (Join-Path $g 'startup') | Out-Null
    Copy-Item (Join-Path $Game 'startup\*.txt') (Join-Path $g 'startup')
    foreach ($f in 'Data.rwd', 'Music.rwd') {
        New-Item -ItemType HardLink -Path (Join-Path $g $f) -Target (Join-Path $Game $f) | Out-Null
    }
    New-Item -ItemType HardLink -Path (Join-Path $g 'k2.exe') -Target $exeCopy | Out-Null
    # the Steam game's own extras: Warchest\Warchest.rwd (startup\autoexec_wc.txt) and data\UI\Shared\options_dialog.tgi
    $wc = Join-Path $Game 'Warchest\Warchest.rwd'
    if (Test-Path $wc) { New-Item -ItemType Directory -Force (Join-Path $g 'Warchest') | Out-Null; Copy-Item $wc (Join-Path $g 'Warchest') }
    $od = Join-Path $Game 'data\UI\Shared\options_dialog.tgi'
    if (Test-Path $od) { New-Item -ItemType Directory -Force (Join-Path $g 'data\UI\Shared') | Out-Null; Copy-Item $od (Join-Path $g 'data\UI\Shared') }
    $u = Join-Path $Scratch "$name-documents\data"
    New-Item -ItemType Directory -Force $u | Out-Null
    [pscustomobject]@{ Game = $g; User = $u }
}

function K2($fake, [string[]]$arguments) {
    $ErrorActionPreference = 'Continue'
    $env:K2MODS_USERDATA = $fake.User
    $out = & $k2mod @arguments --game $fake.Game 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = @($out) }
}

function Must($fake, [string[]]$arguments) {
    $r = K2 $fake $arguments
    if ($r.Exit -ne 0) { throw "k2mod $($arguments -join ' ') failed ($($r.Exit)):`n$($r.Out -join "`n")" }
    $r
}

function Code($fake) {
    $r = Must $fake @('fingerprint')
    $m = $r.Out | Select-String '^game files: ([0-9A-F]{4}-[0-9A-F]{4})$' | Select-Object -First 1
    if (-not $m) { throw "no code in: $($r.Out -join "`n")" }
    $m.Matches[0].Groups[1].Value
}

# the file lines of a comparison (two leading spaces, after the headline)
function Listed($r) {
    $i = 0
    while ($i -lt $r.Out.Count -and $r.Out[$i] -notmatch '^(Same|Different) game files') { $i++ }
    @($r.Out | Select-Object -Skip ($i + 1) | Where-Object { $_ -match '^  \S' })
}

function Report-Is-Clean([string]$path, [string[]]$private) {
    $text = [IO.File]::ReadAllText($path)
    if ($text -match '[A-Za-z]:[\\/]') { return 'contains a drive path' }
    if ($text -match ('[\\/]' + [regex]::Escape($env:USERNAME) + '([\\/]|$)')) { return 'contains the user name as a folder' }
    foreach ($p in $private) { if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return "contains $p" } }
    ''
}

# ------------------------------------------------------------------ a mod, as a player would download it

[void][Reflection.Assembly]::LoadFrom($k2mod)
$arc = New-Object K2Mods.RwdArchive (Join-Path $Game 'Data.rwd')   # read only
try {
    $spells = @($arc.Entries | Where-Object { $_.Name -like 'Spells/*.tgi' } | Sort-Object Name | Select-Object -First 2)
    $replaced = $spells[0].Name                 # the mod replaces this game file with its own text
    $same = $spells[1].Name                     # and carries this one unchanged, as hand installs often do
    $sameBytes = $arc.Read($spells[1])
} finally { $arc.Dispose() }

$src = Join-Path $Scratch 'modsrc'
$unit = 'Units/FpTest/fptest_unit.tgi'
Put $src $unit "[Unit]`n{`n`tstring Name = Fingerprint Test`n`tint Speed = 7`n}`n" | Out-Null
Put $src 'Buildings/FpTest/fptest_art.dds' ([byte[]](0..1023 | ForEach-Object { ($_ * 7) % 256 })) | Out-Null
Put $src $replaced "; replaced by the fingerprint test`n[Spell]`n{`n`tint Power = 3`n}`n" | Out-Null
Put $src $same $sameBytes | Out-Null

"game:    $Game"
"scratch: $Scratch"
""

# ------------------------------------------------------------------ A: loose files in Documents\Kohan2\data

$A = New-FakeGame 'A'
$empty = Code $A
Put $A.User $same $sameBytes | Out-Null
Check '(f) a loose copy identical to the game''s own file leaves the code alone' ((Code $A) -eq $empty)

Copy-Tree $src $A.User
$loose = Code $A
Check 'loose mod files change the code' ($loose -ne $empty) "$empty -> $loose"

foreach ($f in 'saves/FP Test.rsg', 'Save/quick.rsg', 'Screenshots/shot0001.bmp', 'User/UVars.tgi', 'Films/LastFilm_0.RFM',
               'synclogs/sync_0.txt', 'k2.log', 'readme.txt', 'Units/FpTest/notes.docx') {
    Put $A.User $f "player's own file $f`n" | Out-Null
}
Check '(d) saves, screenshots, User\UVars.tgi, films, synclogs, logs, readmes leave the code alone' ((Code $A) -eq $loose)

$reportA = Join-Path $Scratch 'report-A.txt'
Must $A @('fingerprint', '--report', $reportA) | Out-Null
$same0 = K2 $A @('fingerprint', '--compare', $reportA)
Check 'comparing with its own report: exit 0, nothing listed' ($same0.Exit -eq 0 -and @(Listed $same0).Count -eq 0)

$unitPath = Join-Path $A.User ($unit -replace '/', '\')
$original = [IO.File]::ReadAllBytes($unitPath)
$changed = [byte[]]$original.Clone()
$changed[20] = $changed[20] -bxor 1
[IO.File]::WriteAllBytes($unitPath, $changed)
$oneByte = Code $A
$cmp = K2 $A @('fingerprint', '--compare', $reportA)
$listed = @(Listed $cmp)
Check '(b) one changed byte changes the code' ($oneByte -ne $loose) "$loose -> $oneByte"
Check '(b) --compare: exit 1 and names exactly that file' ($cmp.Exit -eq 1 -and $listed.Count -eq 1 -and $listed[0] -match [regex]::Escape($unit)) ($listed -join '; ')
[IO.File]::WriteAllBytes($unitPath, $original)
Check 'putting the byte back restores the code' ((Code $A) -eq $loose)

$clean = Report-Is-Clean $reportA @($Scratch, $A.User, $env:USERPROFILE)
Check '(e) report A has no absolute path and no user name' ($clean -eq '') $clean

Must $A @('loose', 'pack', '--name', 'FP Test', '--version', '1.0') | Out-Null
Check '(a) packed by k2mod loose pack: same code as loose' ((Code $A) -eq $loose)

# ------------------------------------------------------------------ B, C: the same mod installed as an .rwd and as a .k2mod

$B = New-FakeGame 'B'
$rwd = Join-Path $Scratch 'fptest.rwd'
Must $B @('rwd', $src, '-o', $rwd) | Out-Null
Must $B @('install', $rwd) | Out-Null
$codeB = Code $B
Check '(a) built by k2mod rwd and installed: same code as loose' ($codeB -eq $loose) "$loose vs $codeB"

$C = New-FakeGame 'C'
$pkg = Join-Path $Scratch 'fptest.k2mod'
Must $C @('pack', $src, '-o', $pkg, '--name', 'FP Test', '--version', '1.0') | Out-Null
Must $C @('install', $pkg) | Out-Null
$codeC = Code $C
Check '(a) .k2mod package (unchanged game file left out) installed: same code as loose' ($codeC -eq $loose) "$loose vs $codeC"

# ------------------------------------------------------------------ D: load order

$D = New-FakeGame 'D'
$shared = 'Units/FpOrder/shared.tgi'
$ma = Join-Path $Scratch 'moda'; $mb = Join-Path $Scratch 'modb'; $mb2 = Join-Path $Scratch 'modb2'
Put $ma $shared "[Shared]`n{`n`tint Value = 1`n}`n" | Out-Null
Put $ma 'Units/FpOrder/only_a.tgi' "[A]`n{`n}`n" | Out-Null
Put $mb $shared "[Shared]`n{`n`tint Value = 1`n}`n" | Out-Null
Put $mb 'Units/FpOrder/only_b.tgi' "[B]`n{`n}`n" | Out-Null
Put $mb2 $shared "[Shared]`n{`n`tint Value = 2`n}`n" | Out-Null
Put $mb2 'Units/FpOrder/only_b.tgi' "[B]`n{`n}`n" | Out-Null
$ra = Join-Path $Scratch 'fpordera.rwd'; $rb = Join-Path $Scratch 'fporderb.rwd'
Must $D @('rwd', $ma, '-o', $ra) | Out-Null
Must $D @('rwd', $mb, '-o', $rb) | Out-Null
Must $D @('install', $ra) | Out-Null
Must $D @('install', $rb) | Out-Null
$ab = Code $D
Must $D @('up', 'fporderb') | Out-Null
$ba = Code $D
Check '(c) swapping two mods that share a file with the same content: same code' ($ab -eq $ba) "$ab vs $ba"

Must $D @('rwd', $mb2, '-o', $rb) | Out-Null
Must $D @('install', $rb) | Out-Null          # now different content, still loaded first: mod a's copy wins
$bFirst = Code $D
$reportD = Join-Path $Scratch 'report-D.txt'
Must $D @('fingerprint', '--report', $reportD) | Out-Null
Must $D @('down', 'fporderb') | Out-Null       # mod b's copy wins
$bLast = Code $D
Check '(c) the copy that loses does not count' ($bFirst -eq $ab) "$ab vs $bFirst"
Check '(c) swapping two mods that share a file with different content: code changes' ($bFirst -ne $bLast) "$bFirst -> $bLast"
$cmpD = K2 $D @('fingerprint', '--compare', $reportD)
$listedD = @(Listed $cmpD)
Check '(c) --compare names exactly the shared file' ($cmpD.Exit -eq 1 -and $listedD.Count -eq 1 -and $listedD[0] -match [regex]::Escape($shared)) ($listedD -join '; ')
$clean = Report-Is-Clean $reportD @($Scratch, $D.User, $env:USERPROFILE)
Check '(e) report D has no absolute path and no user name' ($clean -eq '') $clean

# ------------------------------------------------------------------ reports that are not reports

$bad = Put $Scratch 'not-a-report.txt' "hello`n"
$r = K2 $D @('fingerprint', '--compare', $bad)
Check 'a file that is not a report: exit 2' ($r.Exit -eq 2) ($r.Out -join ' ')
$cut = Join-Path $Scratch 'report-D-cut.txt'
[IO.File]::WriteAllLines($cut, @([IO.File]::ReadAllLines($reportD) | Select-Object -SkipLast 1))
$r = K2 $D @('fingerprint', '--compare', $cut)
Check 'a report missing a line: exit 2' ($r.Exit -eq 2) ($r.Out -join ' ')

$env:K2MODS_USERDATA = $null
""
$failed = @($results | Where-Object { -not $_.Ok })
"{0} of {1} checks passed. Scratch left in place: {2}" -f ($results.Count - $failed.Count), $results.Count, $Scratch
if ($failed.Count -gt 0) { exit 1 }
exit 0
