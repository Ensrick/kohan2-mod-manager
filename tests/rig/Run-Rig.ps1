# Two-player multiplayer test on one PC: two copies of Kohan II play a LAN match against each other, driven by the
# game's own script commands. Needs Steam running (for the copy protection) and the Multiplayer Fixes plugin turned on
# (k2mod plugin on k2mp); the client finds the host through the plugin's rig hook (native/k2mp/rig.h). Exits 0 when the
# scenario's checks pass. See tests/rig/README.md.
#
#   -Scenario SaveCopy   (default) host: LAN game, random map, launch, save once, quit. client: join, play, quit.
#                        Checks the host's save, the client's "(copy)" and what both plugins logged.
#   -Scenario Transfer   host: LAN game set to load a save SaveCopy left, wait, launch, quit. client: join without
#                        that save and receive it through the game's own file transfer. Measures how long it takes.
#   -Scenario Autosave   the plugin's autosave on for the run (every -AutosaveMinutes) in both copies; host: LAN game,
#                        random map, play two intervals, quit. client: join, play, quit first. Checks the host's
#                        "MP Autosave A/B", the client's copies, and that only the host's plugin saved.
#   -Scenario HostLeaves host: LAN lobby, quits once the client is in it. client: join, stay, quit. Checks that the
#                        client survives the host leaving (the game crashed there about half the time: MULTIPLAYER.md).
#
# Each copy gets its own Documents folder (the test shfolder.dll, K2_RIG_DOCUMENTS) under %LOCALAPPDATA%\Kohan2Rig:
# a copy of the player's game data (loose mods included, so both load the same data), but its own saves, replays,
# sync logs and settings, set to a small window that does not hold the mouse, muted, and running in the background.
# Nothing in the player's own Documents\Kohan2 is touched. The game folder gets shfolder.dll for the run only, and
# Mods\rig\ scripts; its Logs folder (the game keeps only about ten logs) is copied aside first.
# Never run this while someone is playing.
param(
    [ValidateSet('SaveCopy', 'Transfer', 'Autosave', 'HostLeaves')] [string] $Scenario = 'SaveCopy',
    [string] $Game = 'C:\Program Files (x86)\Steam\steamapps\common\Kohan II',
    [int] $Port = 5860,                    # the first port the game tries for a LAN host (5860-5869)
    [int] $LaunchAfter = 90,               # seconds the host waits in its lobby for the client before launching
    [int] $SaveAfter = 60,                 # SaveCopy: seconds into the match before the host saves
    [string] $SaveName = 'Rig Test',
    [int] $AutosaveMinutes = 1,            # Autosave: the loop's interval
    [hashtable] $TransferSettings = @{},  # written to Mods\k2mp.ini [transfer] for this run (both copies read it), then removed
    [hashtable] $PluginSettings = @{},    # more of Mods\k2mp.ini for this run: @{ section = @{ key = value } }
    [string[]] $ExtraSettings = @()       # extra lines for both copies' User\UVars.tgi, e.g. 'float NetMaxBandwidthToUnknown = 400000'
)
$ErrorActionPreference = 'Stop'
$logs = Join-Path $Game 'Logs'
$mods = Join-Path $Game 'Mods'
$playerData = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Kohan2\data'
$start = Join-Path $PSScriptRoot 'Start-K2.ps1'
$rigShfolder = Join-Path $PSScriptRoot '..\..\native\build\rig\shfolder.dll'
$rigRoot = Join-Path $env:LOCALAPPDATA 'Kohan2Rig'
$hostDocs = Join-Path $rigRoot 'HostDocuments'       # the game adds \Kohan2\data
$clientDocs = Join-Path $rigRoot 'ClientDocuments'
$hostSaves = Join-Path $hostDocs 'Kohan2\data\Save'
$clientSaves = Join-Path $clientDocs 'Kohan2\data\Save'
$shfolderInGame = Join-Path $Game 'shfolder.dll'
$ini = Join-Path $mods 'k2mp.ini'

if (Get-Process k2 -ErrorAction SilentlyContinue) { throw 'Kohan II is already running; close it first.' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running.' }
if (-not (Test-Path $rigShfolder)) { throw 'build the test shfolder.dll first (native\build.ps1)' }
if ((Test-Path $shfolderInGame) -and (Get-FileHash $shfolderInGame).Hash -ne (Get-FileHash $rigShfolder).Hash) {
    throw 'the game folder has a shfolder.dll that is not the rig''s; not touching it'
}
$iniSections = [ordered]@{}
if ($TransferSettings.Count) { $iniSections['transfer'] = $TransferSettings }
if ($Scenario -eq 'Autosave') {
    $iniSections['features'] = @{ autosave = 1 }
    $iniSections['autosave'] = @{ minutes = $AutosaveMinutes }
}
foreach ($k in $PluginSettings.Keys) { $iniSections[$k] = $PluginSettings[$k] }
# A k2mp.ini an earlier run left behind (a copy outlived the wait at the end) carries the marker; anything else is the
# player's own settings and is never touched.
$rigIniMarker = '; written by tests/rig/Run-Rig.ps1 for one run'
if ((Test-Path $ini) -and (Get-Content $ini -TotalCount 1) -eq $rigIniMarker) { Remove-Item $ini; 'removed a k2mp.ini an earlier rig run left behind' }
if ((Test-Path $ini) -and -not $iniSections.Count) { throw 'Mods\k2mp.ini exists (the player''s own settings): the rig would run with them; move it aside first' }
if ($iniSections.Count -and (Test-Path $ini)) { throw 'Mods\k2mp.ini exists already; not overwriting it' }
if ($Scenario -eq 'Transfer' -and -not (Get-ChildItem $hostSaves -Filter '*.RSG' -ErrorAction SilentlyContinue)) {
    throw 'the rig host has no save to load: run -Scenario SaveCopy first'
}

# The game keeps only its newest ten or so logs and deletes older ones at start-up, so every rig run would push a
# player's own match logs out. Copy them aside first (files already copied are skipped).
$backup = Join-Path $env:LOCALAPPDATA 'Kohan2RigLogBackup'
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($f in Get-ChildItem $logs -File) {
    $to = Join-Path $backup $f.Name
    if (-not (Test-Path $to) -or (Get-Item $to).Length -ne $f.Length) { Copy-Item $f.FullName $to }
}
"game logs copied to $backup"

# ---------------------------------------------------------------- the copies' own Documents folders
$rigSettings = @'
[Vars]
{
	flag	ResolutionWindowed = true
	flag	ResolutionCoopFullscreenMode = false
	int	ResolutionX = 800
	int	ResolutionY = 600
	flag	MouseClipCursor = false
	flag	RunWhenInactive = true
	float	AudioMainVolume = 0
	flag	FirstRun = false
}
'@
if ($ExtraSettings.Count) {
    $rigSettings = $rigSettings.Replace("	flag	FirstRun = false", (@("	flag	FirstRun = false") + ($ExtraSettings | ForEach-Object { "	$_" })) -join "`n")
    "extra settings for both copies: $($ExtraSettings -join '; ')"
}
foreach ($d in $hostDocs, $clientDocs) {
    $data = Join-Path $d 'Kohan2\data'
    robocopy $playerData $data /E /XD Save synclogs Films Screenshots User /NFL /NDL /NJH /NJS /NP | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $data 'User'), (Join-Path $data 'Save') | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'User\UVars.tgi'), $rigSettings.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
}
# The client starts without any save; SaveCopy also starts the host without the test save. File by file: these
# folders are the rig's own.
foreach ($old in Get-ChildItem $clientSaves -Filter '*.RSG') { Remove-Item $old.FullName }
if ($Scenario -eq 'SaveCopy') {
    foreach ($n in "$SaveName.RSG", "$SaveName (copy).RSG") { $p = Join-Path $hostSaves $n; if (Test-Path $p) { Remove-Item $p } }
}
$autosaves = 'MP Autosave A', 'MP Autosave B'
if ($Scenario -eq 'Autosave') {
    foreach ($n in $autosaves) { $p = Join-Path $hostSaves "$n.RSG"; if (Test-Path $p) { Remove-Item $p } }
}

# ---------------------------------------------------------------- scripts
$join = @"
RequestMultiplayer
Wait 3
RequestLobbyLAN
Wait 3
LobbySetLANNickname RigClient
Wait 2
Echo rig: client joining
LobbyJoinDirect
Wait 2
JoinDirectJoin
Wait 10
StagingReady 0
"@
$hostOpen = @"
RequestMultiplayer
Wait 3
RequestLobbyLAN
Wait 3
LobbySetLANNickname RigHost
Wait 2
InternetGameCreate RigGame $Port 2
Wait 3
"@
# No WaitState before the main menu: it crashes the game then (README). Wait 45 covers start-up.
if ($Scenario -eq 'SaveCopy') {
    $hostScript = @"
Wait 45
$hostOpen
SelectLaunchType RANDOM_MAP
Echo rig: host waiting in its lobby
Wait $LaunchAfter
Echo rig: host launching
StagingLaunch
WaitState game
Wait $SaveAfter
Echo rig: host saving
GameSave "Save/$SaveName.RSG"
Wait 30
Echo rig: host end
ReallyQuit
"@
    $clientScript = @"
Wait 45
$join
Echo rig: client waiting
WaitState game
Echo rig: client in the match
Wait $($SaveAfter + 90)
Echo rig: client end
ReallyQuit
"@
} elseif ($Scenario -eq 'HostLeaves') {
    # The client joins about 65 s after the host's lobby opens; the host leaves 30 s later without a word (ReallyQuit,
    # like a crash), and the client's game finds out by timeout and takes over.
    $hostScript = @"
Wait 45
$hostOpen
SelectLaunchType RANDOM_MAP
Echo rig: host waiting in its lobby
Wait 95
Echo rig: host leaving
ReallyQuit
"@
    $clientScript = @"
Wait 45
$join
Echo rig: client in the lobby
Wait 150
Echo rig: client end
ReallyQuit
"@
} elseif ($Scenario -eq 'Autosave') {
    # The loop waits for the match, then saves every $AutosaveMinutes minutes (A, then B). The client leaves first: a
    # player still connected when the host quits can crash (MULTIPLAYER.md, game bug).
    $inMatch = $AutosaveMinutes * 120 + 45
    $hostScript = @"
Wait 45
$hostOpen
SelectLaunchType RANDOM_MAP
Echo rig: host waiting in its lobby
Wait $LaunchAfter
Echo rig: host launching
StagingLaunch
WaitState game
Echo rig: host in the match
Wait $inMatch
Echo rig: host end
ReallyQuit
"@
    $clientScript = @"
Wait 45
$join
Echo rig: client waiting
WaitState game
Echo rig: client in the match
Wait $($inMatch - 20)
Echo rig: client end
ReallyQuit
"@
} else {
    # The load dialog's Commit takes the save its list selects first.
    $hostScript = @"
Wait 45
$hostOpen
SelectLaunchType LOAD_SAVED_GAME
Wait 3
Commit
Echo rig: host waiting in its lobby
Wait $LaunchAfter
Echo rig: host launching
StagingLaunch
Wait 40
Echo rig: host end
ReallyQuit
"@
    $clientScript = @"
Wait 45
$join
Echo rig: client waiting
Wait $($LaunchAfter + 60)
Echo rig: client end
ReallyQuit
"@
}
New-Item -ItemType Directory -Force (Join-Path $mods 'rig') | Out-Null
"# rig host, $Scenario (written by tests/rig/Run-Rig.ps1)`r`n$hostScript" | Set-Content (Join-Path $mods 'rig\host.txt') -Encoding ascii
"# rig client, $Scenario (written by tests/rig/Run-Rig.ps1)`r`n$clientScript" | Set-Content (Join-Path $mods 'rig\client.txt') -Encoding ascii

foreach ($f in Get-ChildItem $mods -Filter 'plugins*.log') { Remove-Item $f.FullName }
if ($iniSections.Count) {
    $iniLines = foreach ($section in $iniSections.Keys) {
        "[$section]"
        foreach ($e in $iniSections[$section].GetEnumerator()) { "$($e.Key) = $($e.Value)" }
    }
    @($rigIniMarker) + $iniLines | Set-Content $ini -Encoding ascii
    "plugin settings for this run: $($iniLines -join '; ')"
}
Copy-Item $rigShfolder $shfolderInGame -Force
$runLimit = switch ($Scenario) {
    'Autosave' { $LaunchAfter + $AutosaveMinutes * 120 + 300 }
    'HostLeaves' { 420 }
    default { $LaunchAfter + $SaveAfter + 300 }
}

# A copy's log is the log-NNN.log file that appears after it starts. (Not the most recently written log: the host
# keeps writing its own after the client starts.)
function Log-Names { @(Get-ChildItem $logs -Filter 'log-*.log' | ForEach-Object Name) }
function Wait-NewLog($known, $seconds) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $seconds) {
        # only a running game's log (no -ok/-crash suffix): at start-up the game also renames the log of a copy that
        # was killed before to log-NNN-crash.log, which is new too
        $new = Get-ChildItem $logs -Filter 'log-*.log' | Where-Object { $_.Name -match '^log-\d+\.log$' -and $known -notcontains $_.Name } |
            Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
        if ($new) { return $new.FullName }
        Start-Sleep 1
    }
    throw 'no new game log appeared'
}
function Current-Length($path) {
    # The directory entry lags while the game holds the file open; open it shared to read the real length.
    try { $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete'); $n = $fs.Length; $fs.Dispose(); $n } catch { 0 }
}

$hostPid = $clientPid = $null
$firstSeen = $complete = $lastGrew = $arriving = $hostCopy = $null
$want = 0; $lastSize = -1
try {
    $known = Log-Names
    $hostPid = & $start -Game $Game -Arguments '+Exec Mods/rig/host.txt' -Env @{ K2_RIG_DOCUMENTS = $hostDocs }
    $hostLog = Wait-NewLog $known 30
    "host: process $hostPid, log $(Split-Path $hostLog -Leaf)"
    # The game buffers its log, so readiness comes from Windows: the host's game exists once it listens on the port.
    $t0 = Get-Date
    while (-not (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $hostPid)) {
        if (((Get-Date) - $t0).TotalSeconds -gt 240 -or -not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) { throw 'the host never opened its game' }
        Start-Sleep 1
    }
    "host: game created"

    $known = Log-Names
    $clientPid = & $start -Game $Game -Arguments '+Exec Mods/rig/client.txt' -Env @{ K2MP_RIG_JOIN = "127.0.0.1:$Port"; K2_RIG_DOCUMENTS = $clientDocs }
    $clientLog = Wait-NewLog $known 60
    "client: process $clientPid, log $(Split-Path $clientLog -Leaf)"

    $t0 = Get-Date
    while ((Get-Process -Id $hostPid, $clientPid -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt $runLimit) {
        if ($Scenario -eq 'Transfer') {
            if (-not $arriving) {
                $new = Get-ChildItem $clientSaves -Filter '*.RSG' -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($new) {
                    $arriving = $new.FullName; $hostCopy = Join-Path $hostSaves $new.Name; $firstSeen = Get-Date
                    $want = if (Test-Path $hostCopy) { (Get-Item $hostCopy).Length } else { 0 }
                    "transfer: $($new.Name) arriving, the host's file is $want bytes"
                }
            } elseif (-not $complete) {
                $size = Current-Length $arriving
                if ($size -ne $lastSize) { $lastSize = $size; $lastGrew = Get-Date }
                if ($want -gt 0 -and $size -ge $want) { $complete = Get-Date; "transfer: complete after $([math]::Round(($complete - $firstSeen).TotalSeconds, 1)) s" }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    $left = Get-Process -Id $hostPid, $clientPid -ErrorAction SilentlyContinue
    if ($left) { "still running after the time limit: $($left.Id -join ', ') (left running for a look)" }
} finally {
    # a copy can take minutes to close after ReallyQuit (a client that lost its host first times it out); the game folder must not keep the test files
    $exitBy = (Get-Date).AddSeconds(300)
    $ours = @($hostPid, $clientPid | Where-Object { $_ })
    while ($ours -and (Get-Process -Id $ours -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitBy) {
        Start-Sleep -Seconds 2
    }
    if (-not (Get-Process k2 -ErrorAction SilentlyContinue)) {
        if (Test-Path $shfolderInGame) { Remove-Item $shfolderInGame; "test shfolder.dll removed from the game folder" }
        if ($iniSections.Count -and (Test-Path $ini)) { Remove-Item $ini }
    } else { "a copy of the game is still running: shfolder.dll and k2mp.ini left in place until it exits" }
}

# ---------------------------------------------------------------- report
"--- plugin logs"
foreach ($f in Get-ChildItem $mods -Filter 'plugins*.log') { "[$($f.Name)]"; Get-Content $f.FullName | Where-Object { $_ -notmatch 'loader: ' } }
# The game renames its log when it exits (log-NNN.log -> log-NNN-ok.log); read this run's logs only.
function Final-Log($path) { Get-ChildItem $logs -Filter (([IO.Path]::GetFileNameWithoutExtension($path)) + '*.log') | Select-Object -First 1 }
$hostLogNow = Final-Log $hostLog
$joined = [bool]$hostLogNow -and [bool](Select-String -Path $hostLogNow.FullName -Pattern 'RigClient has joined the game' -SimpleMatch -Quiet)
$checks = [ordered]@{ 'client joined the host' = $joined }
if ($Scenario -eq 'SaveCopy') {
    "--- saves"
    $hostSave = Join-Path $hostSaves "$SaveName.RSG"
    $copy = Join-Path $clientSaves "$SaveName (copy).RSG"
    foreach ($p in $hostSave, $copy) {
        if (Test-Path $p) { '{0}: {1} bytes, sha256 {2}' -f (Split-Path $p -Leaf), (Get-Item $p).Length, (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }
        else { '{0}: missing' -f (Split-Path $p -Leaf) }
    }
    $checks['host saved'] = (Test-Path $hostSave)
    $checks['client wrote its copy'] = (Test-Path $copy)
    $checks['client plugin logged the copy'] = [bool](Select-String -Path (Join-Path $mods 'plugins*.log') -Pattern 'save copy ".*" written' -Quiet)
    # Keep the copy in the host's folder too, so a Transfer run has two saves to choose from.
    if (Test-Path $copy) { Copy-Item $copy $hostSaves -Force }
} elseif ($Scenario -eq 'Autosave') {
    "--- saves"
    foreach ($n in $autosaves) {
        foreach ($p in (Join-Path $hostSaves "$n.RSG"), (Join-Path $clientSaves "$n (copy).RSG")) {
            if (Test-Path $p) { '{0}: {1} bytes' -f (Split-Path $p -Leaf), (Get-Item $p).Length } else { '{0}: missing' -f (Split-Path $p -Leaf) }
        }
        $checks["host saved $n"] = Test-Path (Join-Path $hostSaves "$n.RSG")
        $checks["client wrote $n (copy)"] = Test-Path (Join-Path $clientSaves "$n (copy).RSG")
    }
    $hostSaving = @(Select-String -Path (Join-Path $mods 'plugins.log') -Pattern 'autosave: saving' -SimpleMatch -ErrorAction SilentlyContinue).Count
    $clientSaving = @(Select-String -Path (Join-Path $mods 'plugins.2.log') -Pattern 'autosave: saving' -SimpleMatch -ErrorAction SilentlyContinue).Count
    $checks['host plugin saved twice'] = $hostSaving -ge 2
    $checks['client plugin never saved'] = $clientSaving -eq 0
    $clientLogNow = Final-Log $clientLog
    $pauses = if ($clientLogNow) { @(Select-String -Path $clientLogNow.FullName -Pattern 'NET: GameSave on client' -SimpleMatch).Count } else { 0 }
    "client paused for $pauses host save(s)"
    $checks['client paused for the host''s saves'] = $pauses -ge 2
} elseif ($Scenario -eq 'HostLeaves') {
    $clientLogNow = Final-Log $clientLog
    $text = if ($clientLogNow) { Get-Content $clientLogNow.FullName -Raw } else { '' }
    $asserts = ([regex]::Matches($text, 'ASSERT: ')).Count
    "client: $asserts failed assertion(s), log $(if ($clientLogNow) { $clientLogNow.Name } else { 'missing' })"
    $checks['client took over when the host left'] = $text.Contains('has taken over as host')
    $checks['client did not crash'] = $clientLogNow -and $clientLogNow.Name -like '*-ok.log' -and -not $text.Contains('timeout waiting for mutex')
    $checks['client ran IgnoreAsserts'] = $text.Contains('executing "IgnoreAsserts"')
} else {
    $checks['client received the save'] = [bool]$complete
    $checks['received file identical to the host''s'] = $arriving -and (Test-Path $arriving) -and (Test-Path $hostCopy) -and (Get-FileHash $arriving).Hash -eq (Get-FileHash $hostCopy).Hash
    if ($complete) {
        $secs = ($complete - $firstSeen).TotalSeconds
        "transfer: $want bytes in $([math]::Round($secs, 1)) s ($([math]::Round($want / $secs / 1024, 2)) KB/s)"
    } elseif ($firstSeen -and $lastGrew) {
        $secs = [math]::Max(0.1, ($lastGrew - $firstSeen).TotalSeconds)
        "transfer incomplete: $lastSize of $want bytes after $([math]::Round($secs, 1)) s ($([math]::Round($lastSize / $secs / 1024, 2)) KB/s)"
    }
}
"--- checks"
$fail = 0
foreach ($c in $checks.GetEnumerator()) { '{0} {1}' -f ($(if ($c.Value) { 'ok  ' } else { 'FAIL' })), $c.Key; if (-not $c.Value) { $fail++ } }
"game logs: host $(Split-Path $hostLog -Leaf), client $(Split-Path $clientLog -Leaf)"
exit $fail
