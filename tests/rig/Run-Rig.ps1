# Two-player multiplayer test on one PC: two copies of Kohan II play a LAN match against each other, minimized and
# without taking focus, driven by the game's own script commands. Needs Steam running (for the copy protection) and
# the Multiplayer Fixes plugin turned on (k2mod plugin on k2mp); the client finds the host through the plugin's test
# rig hook (native/k2mp/rig.h). Prints what each side's plugin logged and exits 0 when the checks pass.
#
#   1. host copy: LAN lobby, creates a game, picks a random map, waits for the client, launches, saves once, quits
#   2. client copy, started once the host's game exists: joins by IP, stays in the match, quits
#   3. checks: the host's save and the client's "(copy)" exist and both plugins logged them
#
# Scripts go to <game>\Mods\rig\ (left there for reruns). Never run this while someone is playing.
param(
    [string] $Game = 'C:\Program Files (x86)\Steam\steamapps\common\Kohan II',
    [int] $Port = 5860,                    # the first port the game tries for a LAN host (5860-5869)
    [int] $LaunchAfter = 90,               # seconds the host waits in its lobby for the client before launching
    [int] $SaveAfter = 60,                 # seconds into the match before the host saves
    [string] $SaveName = 'Rig Test'
)
$ErrorActionPreference = 'Stop'
$logs = Join-Path $Game 'Logs'
$mods = Join-Path $Game 'Mods'
$saves = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Kohan2\data\Save'
$start = Join-Path $PSScriptRoot 'Start-K2.ps1'

if (Get-Process k2 -ErrorAction SilentlyContinue) { throw 'Kohan II is already running; close it first.' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running.' }

# The game keeps only its newest ten or so logs and deletes older ones at start-up, so every rig run would push a
# player's own match logs out. Copy them aside first (files already copied are skipped).
$backup = Join-Path $env:LOCALAPPDATA 'Kohan2RigLogBackup'
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($f in Get-ChildItem $logs -File) {
    $to = Join-Path $backup $f.Name
    if (-not (Test-Path $to) -or (Get-Item $to).Length -ne $f.Length) { Copy-Item $f.FullName $to }
}
"game logs copied to $backup"

New-Item -ItemType Directory -Force (Join-Path $mods 'rig') | Out-Null
@"
# rig host (written by tests/rig/Run-Rig.ps1). No WaitState before the main menu: it crashes the game then.
Wait 45
RequestMultiplayer
Wait 3
RequestLobbyLAN
Wait 3
LobbySetLANNickname RigHost
Wait 2
InternetGameCreate RigGame $Port 2
Wait 3
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
"@ | Set-Content (Join-Path $mods 'rig\host.txt') -Encoding ascii
@"
# rig client (written by tests/rig/Run-Rig.ps1). No WaitState before the main menu: it crashes the game then.
Wait 45
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
Echo rig: client waiting
WaitState game
Echo rig: client in the match
Wait $($SaveAfter + 90)
Echo rig: client end
ReallyQuit
"@ | Set-Content (Join-Path $mods 'rig\client.txt') -Encoding ascii

foreach ($f in Get-ChildItem $mods -Filter 'plugins*.log') { Remove-Item $f.FullName }
foreach ($n in "$SaveName.RSG", "$SaveName (copy).RSG") { $p = Join-Path $saves $n; if (Test-Path $p) { Remove-Item $p } }

function Newest-Log { Get-ChildItem $logs -Filter 'log-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }

# Both copies keep running while not in front (RunWhenInactive is off by default, so a copy in the background
# almost stops; MPRunWhenInactive covers matches only).
$common = 'RunWhenInactive=true +Exec'
$before = (Newest-Log).FullName
$hostPid = & $start -Game $Game -Arguments "$common Mods/rig/host.txt"
$t0 = Get-Date
while ((Newest-Log).FullName -eq $before -and ((Get-Date) - $t0).TotalSeconds -lt 30) { Start-Sleep 1 }
$hostLog = (Newest-Log).FullName
"host: process $hostPid, log $(Split-Path $hostLog -Leaf)"
# The game buffers its log, so readiness comes from Windows instead: the host's game exists once its process listens on the port.
$t0 = Get-Date
while (-not (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $hostPid)) {
    if (((Get-Date) - $t0).TotalSeconds -gt 240 -or -not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) { throw 'the host never opened its game' }
    Start-Sleep 1
}
"host: game created"

$clientPid = & $start -Game $Game -Arguments "$common Mods/rig/client.txt" -Env @{ K2MP_RIG_JOIN = "127.0.0.1:$Port" }
$t0 = Get-Date
while ((Newest-Log).FullName -eq $hostLog -and ((Get-Date) - $t0).TotalSeconds -lt 60) { Start-Sleep 1 }
$clientLog = (Newest-Log).FullName
"client: process $clientPid, log $(Split-Path $clientLog -Leaf)"

$t0 = Get-Date
while ((Get-Process -Id $hostPid, $clientPid -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt ($LaunchAfter + $SaveAfter + 300)) { Start-Sleep 2 }
$left = Get-Process -Id $hostPid, $clientPid -ErrorAction SilentlyContinue
if ($left) { "still running after the time limit: $($left.Id -join ', ') (left running for a look)" }

"--- plugin logs"
foreach ($f in Get-ChildItem $mods -Filter 'plugins*.log') { "[$($f.Name)]"; Get-Content $f.FullName | Where-Object { $_ -notmatch 'loader: ' } }
"--- saves"
$hostSave = Join-Path $saves "$SaveName.RSG"
$copy = Join-Path $saves "$SaveName (copy).RSG"
foreach ($p in $hostSave, $copy) {
    if (Test-Path $p) { '{0}: {1} bytes, sha256 {2}' -f (Split-Path $p -Leaf), (Get-Item $p).Length, (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }
    else { '{0}: missing' -f (Split-Path $p -Leaf) }
}
# The game renames its log when it exits (log-NNN.log -> log-NNN-ok.log); read this run's host log only.
$hostLogNow = Get-ChildItem $logs -Filter (([IO.Path]::GetFileNameWithoutExtension($hostLog)) + '*.log') | Select-Object -First 1
$joined = [bool]$hostLogNow -and [bool](Select-String -Path $hostLogNow.FullName -Pattern 'RigClient has joined the game' -SimpleMatch -Quiet)
$checks = [ordered]@{
    'client joined the host' = $joined
    'host saved' = (Test-Path $hostSave)
    'client wrote its copy' = (Test-Path $copy)
    'client plugin logged the copy' = [bool](Select-String -Path (Join-Path $mods 'plugins*.log') -Pattern 'save copy ".*" written' -Quiet)
}
"--- checks"
$fail = 0
foreach ($c in $checks.GetEnumerator()) { '{0} {1}' -f ($(if ($c.Value) { 'ok  ' } else { 'FAIL' })), $c.Key; if (-not $c.Value) { $fail++ } }
if ((Test-Path $hostSave) -and (Test-Path $copy)) {
    $same = (Get-FileHash $hostSave).Hash -eq (Get-FileHash $copy).Hash
    "copy byte-identical to the host's save: $same"
}
"game logs: host $(Split-Path $hostLog -Leaf), client $(Split-Path $clientLog -Leaf)"
exit $fail
