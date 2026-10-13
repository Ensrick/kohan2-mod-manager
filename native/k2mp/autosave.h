// Multiplayer autosave: while this PC hosts a running multiplayer match, it saves the match every `minutes` into
// "MP Autosave A" and "MP Autosave B" in turn, with the game's own GameSave command (the order a player's save from the
// menu sends). Every player pauses for the save as for any multiplayer save; with save copies on, every player's game
// also writes "MP Autosave A (copy)" and so on. After a crash, whoever hosts again loads the newest of them.
//
// It replaces the script loop of manager 0.1.0-beta.2 (Mods\mp_autosave.txt): that loop's `WaitState game` throws
// about a minute after the game starts when no match is running (seen on the test rig, 2026-10-12).
//
// Mods\k2mp.ini:
//   [features]
//   autosave = 1             ; off unless set (the manager's "Autosave multiplayer games" box sets it)
//   [autosave]
//   minutes = 5              ; 1..120
#pragma once
#include <cstdint>

namespace autosave {

// Reads the settings and checks the code this needs. False: off, or not available for this k2.exe (logged).
bool Install(uintptr_t exeBase);

// From K2Plugin_Tick (the game's thread): watches for a match this PC hosts and saves when the time has come.
void Tick();

// The state Tick acts on, from plain memory so tests can drive it: true when `admin` is the host's admin node (not in
// backup mode), `session` is in a match (state 2 or 4) and `netNode` has at least one connection to another PC.
bool HostingMatch(const uint8_t* admin, const uint8_t* session, const uint8_t* netNode);

}  // namespace autosave
