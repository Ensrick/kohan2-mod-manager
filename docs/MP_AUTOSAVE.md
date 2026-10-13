# Multiplayer autosave

Issue #3. Turned on with the manager's **Autosave multiplayer games** box or
`k2mod autosave on [--minutes N]`, which also turn on the Multiplayer Fixes plugin.

## What it does

While this PC **hosts** a running multiplayer match, the plugin (`native/k2mp/autosave.cpp`) saves the
match every N minutes (default 5) into "MP Autosave A" and "MP Autosave B" in turn. It runs the game's
own `GameSave "Save/MP Autosave A.RSG"` command, the same network order a save from the menu sends, from
the plugin's tick on the game's own thread. Every player's game pauses for the save as for any
multiplayer save; players whose plugin has save copies on also write "MP Autosave A (copy)" and so on.

| Setting (`<game>\Mods\k2mp.ini`) | |
|---|---|
| `[features] autosave = 1` | on; off when missing or 0 |
| `[autosave] minutes = 5` | 1 to 120 |

It saves only when all of these hold (`autosave::HostingMatch`): this PC's admin node is the server, not
in backup mode; the session is in a match (state 2 or 4, the states `KKC_Session::GameSave` accepts);
at least one other PC is connected (so never in single player, which has the game's own autosave). The
first save comes N minutes after the match is first seen; each save is logged in `Mods\plugins.log`
(`autosave: saving "MP Autosave A"`, then `host save ...` with its size and SHA-256).

To resume after a crash: host, choose **Load saved game**, pick the newest "MP Autosave" (or a
"(copy)" if the host's PC is gone). Players without the file get it through the game's transfer,
which the plugin speeds up on the host (fast transfers).

## Why not a script (manager 0.1.0-beta.2 to beta.8)

Those versions installed a loop in the game's script language: a block in `<game>\user.txt` ran
`Exec Mods/mp_autosave.txt`, which did `WaitState game`, `Wait <N*60>`, `GameSave ...` in a loop. On the
test rig (2026-10-12) that loop broke the game: `WaitState` gives up after about a minute, and when no
match is running by then it throws ("WaitState 'game' time out. Current state 'staging'"); the game
caught it as an unhandled exception, wrote a crash dump and stopped answering the rig's own script. The
manager now removes that loop wherever it finds it (keeping anything else in `user.txt`) and moves its
interval to the plugin: at start-up, and on `k2mod autosave`.

## Test status

- Test rig (two copies on one PC, LAN), k2mp 0.2.0-dev.5, 1-minute interval: the host's plugin saved
  "MP Autosave A" one minute into the match (220,646 bytes) and issued "B" a minute later; the client's
  plugin never saved. The client's save copy crashed that run (next section), so B never happened.
- k2mp 0.2.0-dev.6, same test: the host saved "MP Autosave A" and "MP Autosave B" a minute apart
  (220,638 and 220,867 bytes), the client paused for both and played on, its plugin never saved, and it
  skipped its copies with the new check (its lobby had received a saved game first).
- Not yet tried in an online match.

## Save copies and the "source" resources

The game's save task (`KKC_ResourceManagerTaskSaveGame::SaveGame` 0x5c1473) first follows the
resources the world was made from and reads a missing one without checking. On the rig's client, whose
lobby had first picked a saved game and then a random map, that chain ended at the transferred save,
never loaded: `ASSERT: source_res->IsHeaderLoaded()`, `ASSERT: original_create_settings`, then an
access violation on the loader thread. The host's own save of the same moment was fine. Save copies now
make the same walk first, read-only, and skip the copy with a log line where the game's code would
fail (`save copy of ... skipped: ...`).
