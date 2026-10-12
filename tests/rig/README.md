# Multiplayer test rig

Two copies of Kohan II play a LAN match against each other on one PC, minimized and without taking
focus, driven by the game's own script commands. It tests multiplayer changes without a second player.

```
k2mod plugin on k2mp                 # the client finds the host through the plugin's rig hook
powershell -File tests\rig\Run-Rig.ps1
```

Needs Steam running (the game's copy protection) and nobody playing. Exit code 0 when every check
passes; it prints what each copy's plugin logged and the SHA-256 of the host's save and the
client's copy.

Other runs: `-Scenario Transfer` (the host's lobby loads a save the client lacks; measures how long
the game takes to send it), `-PluginSettings @{ fast_transfers = @{ packets_per_second = 120 } }`
(any `Mods\k2mp.ini` section for this run only), `-TransferSettings @{ ... }` (the `[transfer]`
test settings), `-ExtraSettings` (extra lines for both copies' `User\UVars.tgi`).

## How it works

| Step | How |
|---|---|
| Start a copy with its own script | `Start-K2.ps1`: `k2.exe RunWhenInactive=true +Exec Mods/rig/host.txt` (the command line takes `Name=Value` UI settings and `+command` script commands, `KKC_Application::ParseCommandLine`), `SteamAppId` in its environment, `SW_SHOWMINNOACTIVE`. |
| Keep both running in the background | `RunWhenInactive=true`: off by default, a copy that is not in front nearly stops (seen as minutes of nothing after `WM_ACTIVATEAPP 0`). |
| Host a LAN game | `RequestMultiplayer`, `RequestLobbyLAN`, `LobbySetLANNickname <name>`, `InternetGameCreate <name> <port> <max players>` (the in-game usage text adds `[room number]`, but in the Steam rebuild a fourth argument is the password: "0" there made the host refuse the client with reason 1, wrong password), `SelectLaunchType RANDOM_MAP`, `StagingLaunch`. The host listens on the first free port from 5860. |
| Know the host is ready | Windows, not the game log (the game buffers its log): the host process listens on UDP 5860. |
| Join by IP | `LobbyJoinDirect` opens the Direct IP dialog, `JoinDirectJoin` presses Join. `InternetGameJoin` only adds a browser row in the Steam rebuild ("LAN browser rows removed (MP-S4)"), and no script command fills an editbox, so the plugin answers the dialog's two editbox reads with `K2MP_RIG_JOIN=<ip>:<port>` from the client's environment (`native/k2mp/rig.cpp`; nothing is installed without that variable). |
| Save in the match | `GameSave "Save/<name>.RSG"` on the host: the same order a player's save sends. |
| Quit | `ReallyQuit` (no confirmation), so the logs end as `-ok` and no crash dump is written. |

Script commands only run in a pipeline started with `Exec`, which is the only place `Wait` works.
The game writes each script line to its log twice: `(Echo ...)` when it queues the script, and
`APP:  ...` when the `Echo` runs.

## Keeping the player's game untouched

Early runs used the player's own `Documents\Kohan2` and a full-screen window, and that went wrong in
four ways, now all prevented:

| What happened | Why | Now |
|---|---|---|
| A setting given on the command line ended up in the player's settings | the game saves its settings to `Documents\Kohan2\data\User\UVars.tgi` on exit | each copy has its own Documents folder |
| The player's last five replays were replaced by test matches | every match is recorded over the oldest `Films\LastFilm_<n>.RFM` | same |
| The mouse was held inside a test window | the copies inherited the player's full-screen setting | each copy's own settings: `ResolutionWindowed`, 800x600, `MouseClipCursor = false`, muted |
| The player's newest match logs were deleted | the game keeps only about ten logs in `<game>\Logs` | they are copied to `%LOCALAPPDATA%\Kohan2RigLogBackup` before each run |

Each copy's Documents folder comes from a test-only `shfolder.dll` (native/test/rig_shfolder.cpp):
the game finds its user folder with `LoadLibrary("shfolder.dll")` + `SHGetFolderPathW` (0x6ffea0),
Windows takes that DLL from the game folder first, and the stand-in answers with `K2_RIG_DOCUMENTS`.
`Run-Rig.ps1` copies it in for the run and deletes it afterwards. The folders live under
`%LOCALAPPDATA%\Kohan2Rig` and hold a copy of the player's game data (loose mods included, so both copies
load the same data) without saves, replays, sync logs or settings.

## Game bug found here

`WaitState <state>` crashes the game (access violation in `KKC_CommandPipelineWaitState::IsReady`,
0x540973) when it runs before the main menu: it walks the table of app states at 0x9dd2b0 and calls a
method on each entry without skipping empty ones, and during start-up some are still empty. The rig
starts its scripts with a plain `Wait` and uses `WaitState game` only after `StagingLaunch`. With an
unknown state name (and a full table) it should log the valid names and return at once (read from
the code, not tried).
