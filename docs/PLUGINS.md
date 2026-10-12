# Game plugins

Mods (`.k2mod`, `.rwd`) change the game's data. Plugins change what `k2.exe` itself does: they are
32-bit DLLs that run inside the game. The manager lists them under **Game plugins** with an on/off
box; `k2mod plugin list | on <id> | off <id>` does the same from a command line.

## What turning a plugin on changes

| File | What it is |
|---|---|
| `<game>\winmm.dll` | The plugin loader. Present only while at least one plugin is on. |
| `<game>\Mods\<id>.dll` | The plugin. |
| `<game>\Mods\plugins.txt` | The plugins the loader starts, one file name per line. Written by the manager. |
| `<game>\Mods\plugins.log` | Written by the loader and the plugins at every game start. |

Nothing in `Data.rwd` or any mod changes. The multiplayer join check compares data archives, not
the exe or DLLs, so a plugin never stops anyone from joining. Whether a plugin's *behaviour* must
match between players depends on the plugin; each says so in its description.

The manager never overwrites a `winmm.dll` it did not install (it checks the file's product name):
if another program put one in the game folder, plugins stay unavailable until that file is gone.

## How the loader works

`k2.exe` imports three functions from `winmm.dll` (`timeBeginPeriod`, `timeGetTime`,
`timeEndPeriod`), and `winmm.dll` is not one of the DLLs Windows always takes from System32, so
Windows loads the copy in the game folder first. The loader passes every winmm function on to the
real one in System32.

The Steam `k2.exe` is wrapped by SteamStub: its code section is encrypted on disk and decrypted by
a stub after Windows has loaded the imported DLLs, so a plugin cannot look at the game's code from
`DllMain`. The loader therefore waits for the first call into one of those three functions that
returns into `k2.exe`'s `.text` section. That proves the game's own code is running, and only then
does it load each DLL listed in `plugins.txt` and call its `K2Plugin_Start`.

In any process other than `k2.exe` the loader is a plain `winmm.dll`.

## Writing a plugin

See [`native/include/k2plugin.h`](../native/include/k2plugin.h). A plugin:

- is a 32-bit DLL exporting `int __cdecl K2Plugin_Start(const K2Host*)` (0 = running);
- finds its way around `k2.exe` by RVA: the exe is relocated (ASLR), so address = `exeBase + RVA`;
- checks the bytes at every place it patches before patching, skipping (`??`) the bytes relocation
  changes, and stays off if they differ (a game update moved things);
- carries a version resource with ProductName `Kohan II plugin`, FileDescription = its name,
  Comments = one or two sentences for players, ProductVersion, InternalName = its id. The manager
  lists any DLL in `Mods\` with that ProductName.

## Building

`native\build.ps1` (Visual Studio 2022 or its Build Tools, C++ workload) builds `winmm.dll`,
`k2mp.dll` and two tests into `native\build\Release`; the root `build.ps1` runs it first, and the
manager embeds both DLLs. Tests:

- `native\test\loader_test.ps1` runs a stand-in `k2.exe` next to the loader in a scratch folder and
  checks `plugins.log`: the plugin loads, refuses the stand-in exe, unsafe and missing names are
  reported.
- `native\build\Release\sigtest.exe <k2mp.dll> <decrypted k2.exe>` maps a decrypted copy of the
  exe as an image (never runs it) at a relocated address and checks every signature.

## Multiplayer Fixes (`k2mp.dll`)

Goal: a player who drops out of a multiplayer match can rejoin it, the match pauses until they
are back, and nobody shares saves by hand. Version 0.1.0-dev.1 only checks that it recognises
`k2.exe` 1.3.68 at the places it will patch:

| RVA | Function | Why |
|---|---|---|
| `0x1624d5` | `KKC_Session::GameSave` | the host writes a save, other players pause until it is done |
| `0x1506dd` | `TGC_AdminConn::SerializeConnect` | the host refuses a joining player with "Game is already in progress" (reason 10) when the session state is 2 or 4 |

What the game already has (static analysis of 1.3.68, and match logs):

- **Games in progress in the browser:** the `NetworkReportGameInProgress` setting (on by default)
  and an "In Progress" filter. Joining one is refused by the check above.
- **A player drops:** the host hands that kingdom to a computer player
  (`KKC_Session::MidgameCreateImplicitAIs`); `KKC_MidgameCreateAIsAndSessionPlayersOrder` exists.
- **The host drops:** everyone freezes ("backup mode"), then another player becomes host after a
  timeout of about 14 s ("%s has taken over as host").
- **Saves:** a save takes about 1 s, loading one in the lobby about 3 s. The game sends a save to a
  player who lacks it, in 64-byte blocks, 20 per frame (`NetworkFileTransferBlockSize`,
  `NetworkFileTransferBlocksPerFrame`, `NetworkFileTransferMaxPendingBlocks` = 256): slow for a
  900 KB save.
- **Replays:** every match is recorded as a film of orders (`KKC_Session::OpenFilmForRecording`)
  and films can be played back faster than real time.

Plan: when a player drops, the host's game pauses, saves and keeps the match open; the dropped
player rejoins from the browser, gets the save at full speed, and every player reloads it (about
3 s) with the returning player back in their slot. Catching up without the reload comes later, if
the reload proves too disruptive. Only the host should need the plugin; not yet verified.
