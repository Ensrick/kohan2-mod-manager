# Multiplayer stability

What goes wrong in Kohan II multiplayer, what the game already does about it, and what the Multiplayer
Fixes plugin (`native/k2mp`) and the manager add. Addresses are for `k2.exe` 1.3.68 (Steam, Dev Build
4345.17), as virtual addresses at the exe's preferred base 0x400000; the plugin uses them as RVAs
(minus 0x400000) because the exe is relocated at run time.

## Evidence: one evening, eight players

From one player's game log and sync logs (2026-10-11):

| Matches | What happened |
|---|---|
| 1 | Ended normally (the host ended it). |
| 2 | **Host crashed.** Every game froze ("backup mode"), the connection to the host timed out after about 14 s, another player's game became the network server, and the match never resumed. |
| 1 | **A player froze.** The host saved by hand, the save was passed around outside the game, everyone reloaded it. |
| 4 | **Desync** (`SyncFailure`). Three of them within 2 seconds of a fresh start, each right after players had quit and rejoined the lobby; one after about 30 minutes. |

In the three early desyncs the sync log puts the first difference at the first value compared on the
first tick (`(int)m_rng.GetSeed()`): the games did not start identical. Two candidate causes, not yet
told apart: different game data (see the join check below) or lobby state that went stale when players
rejoined (the log has "PlayerJoin() Rejected join from player already in game"). Telling them apart
needs the sync log of a second player from the same match.

## How the game works (what matters here)

- **Lockstep.** Every PC simulates the whole world from the same data and the same orders ("decrees"),
  relayed by the host. `TGC_Synchronizer` compares checksums of the simulation between PCs; a mismatch
  is a `SyncFailure` (`TGC_AdminNode::SyncFailure` 0x54a10c) and each PC writes
  `Documents\Kohan2\data\synclogs\synclog_<name><n>.txt`.
- **Join check** (`TGC_AdminConn::SerializeConnect` 0x5506dd): compares the version, a string of every
  priority-0 data archive as "name stamp" (built by 0x5cadaf; folder depots appear with stamp 0) and the
  list of `addmod` archives (0x5cae57). **Loose files in `Documents\Kohan2\data` are not compared**, so
  players whose loose mod files differ can join each other and desync. It refuses with reason 10
  "Game is already in progress" while the session state is 2 or 4.
- **Saves in multiplayer** (`KKC_Session::GameSave` 0x5624d5): every PC applies the GameSave order at the
  same point of the simulation and freezes it (`KKC_Sim::SetFrozen` 0x560d0d). Only the host writes the
  file: `CreateSaveGameTask` (0x56a457) queues a `KKC_ResourceManagerTaskSaveGame` on the resource
  manager (loader thread) and every other PC waits (`TGC_AdminNode::BeginSoloTask` 0x54c4c0). The file
  goes to `Documents\Kohan2\data\Save\`.
- **Loading a save in the lobby** (`KKC_Session::SendUpdateMapStatus` 0x568660): each player reports 1
  (has the file, same checksum), 2 (no file: wants it sent) or 3 (different checksum: refused).
- **Sending a file** (`TGC_FileSender` / `TGC_FileReceiver`, vtables 0x8bc4ac / 0x8bc4dc): 64-byte blocks
  (`NetworkFileTransferBlockSize`), 20 per frame (`...BlocksPerFrame`), up to 256 in flight
  (`...MaxPendingBlocks`; the stream's ring buffer also holds 256). These are AVars in the settings table
  at 0x9dd4c8 (entries 0x48, 0x49, 0x4a). **The receiver splits the file with its own block size**
  (0x5593ea), so the block size must be the same on both ends: raising it on one PC corrupts transfers to
  every PC without the same change. Steam messages over 4096 bytes are dropped on receipt
  (`TGC_SteamSock::Update` 0x4804b8).
- **Host loss:** `TGC_AdminConn::Timeout` (0x551c04) makes the next node the server
  (`TGC_AdminNode::SetServer` 0x54f58c); in the evening above the match still never resumed.
- **Games in progress** can be listed in the browser (`NetworkReportGameInProgress`, on by default), but
  joining one is refused (reason 10 above).

## Principles for every change

1. **Never touch the simulation.** Nothing the plugin does may change what the sim computes; it works
   around saves, files and the lobby. A plugin bug must not be able to cause a desync.
2. **Stay compatible with players who do not have it.** A stock game must never receive anything it would
   not receive from another stock game (this rules out raising the transfer block size, above).
3. **Use the engine's own routines in the contexts the engine uses them**, and check the bytes of every
   function called or patched first: after a game update, features whose bytes moved stay off.
4. **One switch and one log line per feature.** `Mods\k2mp.ini` `[features]` turns each off; everything
   goes to `Mods\plugins.log`.
5. **Offline tests for every patch**: `native\test\run_all.ps1` (hook stubs exercised the way the game
   calls them, every signature against a decrypted exe, the loader with a stand-in game).

## Plan and status

| # | Change | Status |
|---|---|---|
| 1 | **Game files code** in the manager: one short code per install from the data the game actually loads (archives, mods and loose files), and a report two players can compare. Catches the data mismatches the join check misses. Definition: [FINGERPRINT.md](FINGERPRINT.md). | built, manager 0.1.0-beta.6 |
| 2 | **Save copies** (`save_copies`): every non-host PC writes `<save> (copy).RSG` when the host saves, with the engine's own save task while the game is paused for the host's save. Host and players log the SHA-256 of their files. | built, k2mp 0.2.0-dev.1; needs a two-player test |
| 3 | **Use the copy when reloading**: when the host loads `X`, a player whose `X (copy)` has the host's checksum uses it instead of downloading; a different `X` is moved aside and downloaded instead of refused. | planned, after 2 is tested |
| 4 | **Fast save delivery** between players who both have the plugin, on a private Steam channel; players without it keep the game's own transfer. | planned |
| 5 | **Host autosave** every few minutes, so a crash loses minutes at most. | planned (the script-based autosave in 0.1.0-beta.2 is the stopgap) |
| 6 | **Rejoin a running match** after a drop. | planned |
| 7 | **Desync cause** of the early desyncs. | needs a second player's sync log |

## Testing save copies (k2mp 0.2.0-dev.1)

Two players, both with Multiplayer Fixes ticked in the manager:

1. Start any match. The host saves from the menu (any name).
2. Both open `Mods\plugins.log` in the game folder.
   - Host: `host save "Save/<name>.RSG": <n> bytes, sha256 <H>`.
   - Other player: `save copy "Save/<name> (copy).RSG" written in <ms> ms: <n> bytes, sha256 <H'>`, and
     the file in `Documents\Kohan2\data\Save\`.
3. If `H` equals `H'`, copies are byte-identical to the host's save and step 3 above becomes simple. If
   not, step 3 compares the game's own checksum instead.
4. Anything else in the log (`save copy FAILED`, `did not finish`) and the game's own log go into an issue.
