# Game plugins

Mods (`.k2mod`, `.rwd`) change the game's data. Plugins change what `k2.exe` itself does: they are
32-bit DLLs that run inside the game. The manager lists them under **Game plugins** with an on/off
box; `k2mod plugin list | on <id> | off <id>` does the same from a command line.

## What turning a plugin on changes

| File | What it is |
|---|---|
| `<game>\avifil32.dll` | The plugin loader. Present only while at least one plugin is on. |
| `<game>\Mods\<id>.dll` | The plugin. |
| `<game>\Mods\plugins.txt` | The plugins the loader starts, one file name per line. Written by the manager. |
| `<game>\Mods\plugins.log` | Written by the loader and the plugins at every game start (`plugins.2.log` ... for a second copy of the game running from the same folder). |

Nothing in `Data.rwd` or any mod changes. The multiplayer join check compares data archives, not
the exe or DLLs, so a plugin never stops anyone from joining. Whether a plugin's *behaviour* must
match between players depends on the plugin; each says so in its description.

The manager never overwrites an `avifil32.dll` it did not install (it checks the file's product
name): if another program put one in the game folder, plugins stay unavailable until that file is
gone. It removes the loader's earlier name, `winmm.dll`, when that copy is its own.

## How the loader works

`k2.exe` imports `avifil32.dll` (for its movie recorder). It is not one of the DLLs Windows always
takes from System32, and nothing loads it earlier, so Windows loads the copy in the game folder. The
loader passes every avifil32 function on to the real one in System32 and exports copies of its four
interface IDs.

`winmm.dll`, the first choice, does not work: Windows applies compatibility shims to `k2.exe`
(`AcSpecfc.dll`), and they load the system `winmm.dll` before the game's own imports are resolved,
so a copy in the game folder is never used. (Found by listing the modules of a running game.)

The Steam `k2.exe` is wrapped by SteamStub: its code section is encrypted on disk and decrypted by
a stub after Windows has loaded the imported DLLs, so a plugin cannot look at the game's code from
`DllMain`. In `DllMain` the loader points k2.exe's own import entries for winmm's
`timeBeginPeriod`, `timeGetTime` and `timeEndPeriod` at small wrappers (found by name, so they work
whatever a shim put there; the wrappers call it). The first call that returns into `k2.exe`'s
`.text` section proves the game's own code is running, and only then does the loader load each DLL
listed in `plugins.txt` and call its `K2Plugin_Start`.

In any process other than `k2.exe` the loader is a plain `avifil32.dll`.
## Writing a plugin

See [`native/include/k2plugin.h`](../native/include/k2plugin.h). A plugin:

- is a 32-bit DLL exporting `int __cdecl K2Plugin_Start(const K2Host*)` (0 = running), and optionally
  `void __cdecl K2Plugin_Tick(void)`, which the loader calls on the game's own thread (the one that started
  the plugins) at most every 50 ms. Read or change game state only there or inside a hook, never from a
  thread of the plugin's own: the game's data is not guarded against other threads;
- finds its way around `k2.exe` by RVA: the exe is relocated (ASLR), so address = `exeBase + RVA`;
- checks the bytes at every place it patches before patching, skipping (`??`) the bytes relocation
  changes, and stays off if they differ (a game update moved things);
- carries a version resource with ProductName `Kohan II plugin`, FileDescription = its name,
  Comments = one or two sentences for players, ProductVersion, InternalName = its id. The manager
  lists any DLL in `Mods\` with that ProductName.

## Building and testing

`native\build.ps1` (Visual Studio 2022 or its Build Tools, C++ workload) builds `avifil32.dll` (the loader),
`k2mp.dll` and the tests into `native\build\Release`; the root `build.ps1` runs it first, and the
manager embeds both DLLs. `native\test\run_all.ps1 [-DecryptedExe <path>]` runs every test:

- `hooktest.exe`: the hook layer, and each save-copy stub reached through a real redirected `call`
  the way the game makes it (same registers, same stack arguments). Checks that the original function
  still gets its ECX and arguments, the plugin sees the session, and ESP, EBX, ESI, EDI and EBP come
  back unchanged.
- `loader_test.ps1`: a stand-in `k2.exe` next to the loader in a scratch folder; checks
  `plugins.log`: the plugin loads, refuses the stand-in exe, unsafe and missing names are reported.
- `sigtest.exe <k2mp.dll> <decrypted k2.exe>` (with `-DecryptedExe`): maps a decrypted copy of the
  exe as an image (never runs it) at a relocated address and checks every signature and call site.

## Multiplayer Fixes (`k2mp.dll`)

Design, evidence and status: [MULTIPLAYER.md](MULTIPLAYER.md). Source in `native\k2mp`: every
address it uses is in `game.h` with the bytes it must find there; `hook.*` checks and redirects
calls; each feature has its own file and its own switch in `Mods\k2mp.ini`:

```ini
[features]
save_copies = 1      ; every player's game keeps "<save> (copy).RSG" of each multiplayer save
fast_transfers = 1   ; as host, send saved games to players several times faster (only the host needs it)
ignore_asserts = 1   ; the game's IgnoreAsserts: a failed check is logged instead of freezing the game
autosave = 0         ; as host, save the match every few minutes (the manager's autosave box sets it)
pause_on_drop = 1    ; when a player leaves a running match, the host saves "MP Resume" and pauses

[fast_transfers]
packets_per_second = 60   ; 11..200; the game's own pace is 10

[autosave]
minutes = 5               ; 1..120
```
