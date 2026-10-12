# Multiplayer autosave

Issue #3. Turned on with the manager's **Autosave multiplayer games** box or
`k2mod autosave on [--minutes N]`.

## What it installs

| File | Content |
|---|---|
| `<game>\user.txt` | a marked block: `Exec Mods/mp_autosave.txt`. Anything else in the file is kept. |
| `<game>\Mods\mp_autosave.txt` | `WaitState game`, then twice: `Wait <N*60>`, `WaitState game`, `GameSave "Save/MP Autosave A.RSG"` (then `B`); then `Exec` itself again. |

Saves land in `Documents\Kohan2\data\Save\`. To resume after a crash: host, choose
**Load saved game**, pick the newer of "MP Autosave A" and "MP Autosave B".

## Why it works

Static analysis of k2.exe 1.3.68 (Dev Build 4345.17; addresses are virtual addresses in the
image once Steam has unpacked it), plus a real match log from 2026-10-11.

- **Start-up order** (comment in the game's `start.txt`): autoexec, vars, command line, UI,
  `start.txt` from the depots, then `user.txt` from the game folder.
- **Script pipelines.** `Exec` (handler 0x53e1d1) creates a new pipeline and queues the
  script's lines on it; `ExecHere` runs on the current one. `Wait <s>` (0x53e2e8) sets the
  pipeline's wake time to app time + s and is refused on the main pipeline ("Cannot use the
  'wait' command on the main pipeline"), so the loop must be started with `Exec`. The game's
  own `UI/start.txt` uses `WaitState game` and `WaitOrders` in its aliases.
- **`GameSave "<path>"`** is a network order (`KKC_GameSaveOrder`). `KKC_Session::GameSave`
  (0x5624d5) first drops orders whose sender is not the host ("GameSave order from non-host
  (ignoring)"). On the host, in session state 2 or 4, it creates the save task; on the
  others it shows "The host is saving the game." and waits for the host's task. A client's
  loop therefore saves nothing and pauses nobody; the host's loop saves for everyone.
- **The game's own autosave** (`KKF_AutoSaveQueue`, 0x540ad9) issues exactly this command
  (`GameSave "Save/Autosave<n>.RSG"`). Its timer (0x540c3b) has several gates; the options
  screen calls it "Auto-Save (single-player)", and no "Auto-save: queued" line appears in
  any of 115 logs on the test PC.
- **Match log, 2026-10-11** (client side, host saved by hand after a player froze):
  `The host is saving the game.` / `NET: GameSave on client` / `ALL_TASKS_SUCCEEDED`, the
  pause lasted about 1.2 s. On reload the client checked its own copy:
  `_filename Save/<name>.RSG, _crc = <c>` then `_crc = <c>, map_file = <c>`.
- **Loading a save in staging:** `KKC_Session::SendUpdateMapStatus` (0x568660) reports
  status 1 when the player's copy has the host's CRC, and status 2 (wants the file) when the
  player has none; the game has a file transfer for that (`FileTransfer.cpp`,
  "staging_percent_downloaded"). A copy with a different CRC is refused ("Your version of %s
  is different from the host's version").

## Not verified yet

The loop has not run in the game. Test, in this order:

1. Single player: `k2mod autosave on --minutes 1`, start a skirmish, play 3 minutes.
   Expect `executing "GameSave \"Save/MP Autosave A.RSG\""` in the log and the file in
   `Documents\Kohan2\data\Save\`; nothing in the log while in menus.
2. Multiplayer, host with it on: every player pauses for about a second each interval;
   the host gets the files; clients log `NET: GameSave on client`.
3. Multiplayer, only a client with it on: the logs show "GameSave order from non-host
   (ignoring)" and nobody pauses.
4. Resume: host loads "MP Autosave B"; a player without the file should see a download
   percentage in staging.

## Not built: a copy on every PC

Other players' games never write the save (the client branch of `KKC_Session::GameSave`
only pauses), so if the host's PC is gone for good, so is the save. A copy on every PC
needs code in that branch to write the same save locally, and then only helps if a client's
file is byte-identical to the host's (the CRC check above). That means a DLL hook, which
breaks with each game update and is a modified client in online play.
