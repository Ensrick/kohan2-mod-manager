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
4. **Game state only on the game's own thread**: inside a hook, or in `K2Plugin_Tick` (called by the loader on the
   thread that started the plugins). A plugin thread that wrote the game's settings while the game was still
   building them crashed a test copy at start-up (2026-10-12); the game's data is not guarded against other
   threads.
5. **One switch and one log line per feature.** `Mods\k2mp.ini` `[features]` turns each off; everything
   goes to `Mods\plugins.log`.
6. **Offline tests for every patch**: `native\test\run_all.ps1` (hook stubs exercised the way the game
   calls them, every signature against a decrypted exe, the loader with a stand-in game).
7. **A real two-player match before any player sees it**: `tests\rig\Run-Rig.ps1` runs two copies of the
   game against each other on one PC (LAN, scripted, minimized). See `tests/rig/README.md`.

## Plan and status

| # | Change | Status |
|---|---|---|
| 1 | **Game files code** in the manager: one short code per install from the data the game actually loads (archives, mods and loose files), and a report two players can compare. Catches the data mismatches the join check misses. Definition: [FINGERPRINT.md](FINGERPRINT.md). | built, manager 0.1.0-beta.6 |
| 2 | **Save copies** (`save_copies`): every non-host PC writes `<save> (copy).RSG` when the host saves, with the engine's own save task while the game is paused for the host's save. Host and players log the SHA-256 of their files. | built; works in a two-player LAN match on the test rig (below); not yet in an online match |
| 3 | **No more refused reloads**: a player whose own `X` differs from the host's is told "Your version of X is different" and cannot load; move such a file aside and download the host's instead. (Reusing copies by checksum is out: copies are never byte-identical, see below.) | planned |
| 4 | **Fast transfers** (`fast_transfers`): the host sends a save to each player at up to 60 packets/s instead of 10. Only the host needs the plugin; players receive the game's own packets, just more of them. | built, k2mp 0.2.0-dev.4: 8.4x faster on the test rig (below); not yet in an online match |
| 5 | **Host autosave** every few minutes, so a crash loses minutes at most. | planned (the script-based autosave in 0.1.0-beta.2 is the stopgap) |
| 6 | **Rejoin a running match** after a drop. | planned |
| 7 | **Desync cause** of the early desyncs. | needs a second player's sync log |

## Save copies: first real match (test rig, 2026-10-12)

`tests\rig\Run-Rig.ps1`, two copies of the game on one PC in a LAN match, k2mp 0.2.0-dev.2:

- The client joined, the host saved 60 s into the match with `GameSave`, the client's game wrote
  `Rig Test (copy).RSG` in 16 ms while paused for the host's save, and the match went on. No error.
- A save is a TGCK chunk file of 20 zlib streams (1,065,204 bytes unpacked). Compared stream by stream,
  the copy matches the host's save in 18 of them, including every large one (374 KB, 186 KB, 186 KB).
  Stream 0 differs only in the save's own name ("Rig Test (copy)" in the copy's header); stream 13
  (287 KB) differs in 48 bytes, all small runs of float values. No `SyncFailure` was logged, so the
  simulation itself was identical; those bytes look like per-PC view state.
- So a copy is never byte-identical to the host's save, and the lobby's checksum (h = h * 7 + byte over
  the file) will always tell them apart. A copy is a working save of the same moment for whoever hosts
  from it; the other players still get the file sent. That makes **fast transfers (step 4) the next
  step**, and step 3 changes: no reuse of copies by checksum.

## The game's save transfer: what limits it, and fast transfers (test rig, 2026-10-12)

`Run-Rig.ps1 -Scenario Transfer`: the host's lobby loads a save, the client joins without it and the game sends
it. The rig watches the file grow in the client's own folder. A 220,530-byte save:

| Settings | Result |
|---|---|
| game defaults (3 runs) | 104-109 s: **2.0 KB/s**. A 900 KB save takes about 7.5 minutes. |
| `NetworkFileTransferBlocksPerFrame` 20 -> 100 | 1.6 KB/s, did not finish in the time given |
| `NetworkFileTransferBlockSize` 64 -> 512 or 1024 (blocks per frame 6 / 3) | nothing arrives; one copy hung on exit |
| `NetworkAdminAsyncTicksPerSecond` 16 -> 64, `NetworkAdminTicksPerSecond` 16 -> 120 | 1.97 / 2.01 KB/s |
| `NetMaxBandwidthToUnknown` 4000 -> 400000 | 2.03 KB/s (the rig's connections use `NetLanBandwidth`, 200,000) |
| **fast transfers, 60 packets/s** | **12.6 s, 17.1 KB/s (8.4x)**; file identical |
| fast transfers, 120 packets/s | 6.0 s, 35.6 KB/s (17x); file identical |

What sets the pace (all on the host's side):

- **Packets.** `TGC_AdminConn::GetWantedPacketType` (0x54fb23) decides, per connection, when the next packet goes. The
  host sends a player who is in the lobby or the match packet type 5 once more than 0.1 s have passed since the last
  one: about 10 packets per second, whatever the settings above.
- **Room in a packet.** Type 5 (0x551304) carries four streams, each written into what is left of the packet: the
  first up to 256 bytes, the rest up to 384 bytes in all. The file being sent (the connection's `TGC_FileSender` at
  +0x3b4) is the last of them and got about 170 bytes per packet in the measurement.
- **Not the bandwidth settings.** `TGC_NetNode::SendPackets` (0x55b84d) spends a byte budget per connection at
  `NetSteamBandwidth` (Steam games), `NetMaxBandwidthToUnknown` (4,000 bytes/s, when the admin node's byte at +0x304,
  taken from the host settings, is set) or `NetLanBandwidth` (200,000). At 10 packets of 384 bytes a second, only the
  4,000 limit could ever bind. These settings live in a second table (0x9dd478, entries 0xc1-0xc3);
  `[transfer]` in `Mods\k2mp.ini` can set them for tests.
- The file stream sends the blocks never sent before first, then fills spare room with copies of blocks still
  unacknowledged (0x557f98), and the receiver acknowledges every packet by sequence number in each packet it sends
  (32 at a time, about 10 a second). More packets therefore carry more new blocks.

**Fast transfers** (`fast_transfers`, on by default): while this PC hosts and its connection to a player is sending a
file (that connection's state at +0x3c is 2, set by the game until the last block is acknowledged), the plugin answers
"type 5 now" where `GetWantedPacketType` said "nothing yet", paced by a token bucket per player: `packets_per_second`
(default 60, 11..200) tokens a second, at most 0.1 s worth banked, because the game's sender runs only about 25 times
a second. Both places that ask (the sender's choice of connection at 0x55bfc8 and the packet writer at 0x54fd8d) are
redirected, and they agree because they ask in the same round with the same clock. The packets are the game's own;
a player with a stock game just gets more of them, so only the host needs the plugin. The game's own per-connection
and per-PC byte budgets still apply on top (200,000 bytes/s on Steam and LAN). Logged per transfer: start, end, and
how many packets went early.

60 is the default rather than 120 because the host's upload is shared: 60 packets of about 400 bytes is about
24 KB/s per player receiving a save at the same time, comfortably inside any current home connection; 120 is tested
and can be set in `Mods\k2mp.ini`. Larger packets (the receivers take up to 4,096 bytes: `TGC_WSSocket::Receive`
0x571484 and `TGC_SteamSock::Update` 0x4804b8) would add more; not done.

## Game bug: a player still in the lobby crashes when the host leaves

Seen four times on the rig (client logs of runs at 15:14, 15:48, 17:16 and 17:28): the host quits while the client
is in the lobby; the client logs `NET: PlayerLeave()` / `ClientLeave(<host>)`, then dozens of
`NET: PlayerLeave() called with invalid player`, `ASSERT: _data<_n_possibilities`, `ERROR: thread <a> timeout locking
mutex <m> (owned by <b>)`, and dies with "timeout waiting for mutex" (C++ exception E06D7363) and a crash dump. The
plugin has no code on that path (it happened with and without its tick hooks). Same family as "the host's game
crashed and the match never resumed" in the evidence above; a fix target once its cause is traced.

## Testing save copies with real players (k2mp 0.2.0-dev.1)

Two players, both with Multiplayer Fixes ticked in the manager:

1. Start any match. The host saves from the menu (any name).
2. Both open `Mods\plugins.log` in the game folder.
   - Host: `host save "Save/<name>.RSG": <n> bytes, sha256 <H>`.
   - Other player: `save copy "Save/<name> (copy).RSG" written in <ms> ms: <n> bytes, sha256 <H'>`, and
     the file in `Documents\Kohan2\data\Save\`.
3. If `H` equals `H'`, copies are byte-identical to the host's save and step 3 above becomes simple. If
   not, step 3 compares the game's own checksum instead.
4. Anything else in the log (`save copy FAILED`, `did not finish`) and the game's own log go into an issue.
