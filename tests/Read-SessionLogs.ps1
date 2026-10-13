# Reads the logs players send after a multiplayer test session and prints what matters, file by file: the plugin's
# own lines (Mods\plugins.log) and the game log's network, save, lobby, crash and sync lines (Logs\log-N.log).
#
#   powershell -File tests\Read-SessionLogs.ps1 <folder with the files, any depth>
#
# Nothing is changed. Lines that repeat back to back (the game logs some errors dozens of times) are shown once with
# a count.
param([Parameter(Mandatory)] [string] $Folder)

$gamePattern = @(
    'Version: ', 'version = ', 'SerializeConnect', 'has joined the game', 'quit the game', 'PlayerLeave\(\)',
    'Timeout\(\)', 'SetBackupMode', 'WantsToEnterBackupMode', 'taken over as host', 'Vote to kick', 'CSDKick',
    'AI taken over', 'GameSave', 'Game Saved', 'has paused the game', 'SetNewState', 'ChangeToState',
    'Loading File', 'Launch\(\)', 'already in progress', 'different version', 'Starting download', 'rejected',
    'SyncFailure', 'synclog', 'ASSERT', 'exception', 'timeout waiting', 'minidump', 'TGSteam lobby', 'MP-S5a',
    'Steam auth', 'Error on receive', 'Unknown command'
) -join '|'

function Show-Lines($lines) {
    $last = $null; $count = 0
    foreach ($l in $lines + @($null)) {
        if ($l -eq $last) { $count++; continue }
        if ($null -ne $last) { if ($count -gt 1) { "  $last  (x$count)" } else { "  $last" } }
        $last = $l; $count = 1
    }
}

$files = Get-ChildItem $Folder -Recurse -File | Where-Object { $_.Name -match '^(plugins(\.\d+)?\.log|log-\d+.*\.log)$' } | Sort-Object FullName
if (-not $files) { throw "no plugins.log or log-N.log under $Folder" }
foreach ($f in $files) {
    "=== $($f.FullName.Substring((Resolve-Path $Folder).Path.Length).TrimStart('\'))  ($([math]::Round($f.Length / 1KB)) KB, $($f.LastWriteTime))"
    if ($f.Name -like 'plugins*') {
        Show-Lines @(Get-Content $f.FullName | Where-Object { $_ -match 'k2mp:|loader:' -and $_ -notmatch 'loader: (forward|export)' } | ForEach-Object { $_.Trim() })
    } else {
        Show-Lines @(Select-String -Path $f.FullName -Pattern $gamePattern | ForEach-Object {
            $t = $_.Line.Trim(); if ($t.Length -gt 200) { $t = $t.Substring(0, 200) + '...' }; $t
        })
    }
    ''
}
