// Pause on drop: when a player leaves a running multiplayer match (their game crashed, their connection was lost, or
// they quit), the host saves the match as "MP Resume" and pauses it for everyone, with the game's own GameSave and
// GamePause commands. Today the game hands the leaver's kingdom to the AI and plays on, and a leaver who comes back is
// refused ("Game is already in progress", test rig 2026-10-12). With this, nothing is lost while they are gone: the
// others can wait, or unpause (Pause key) and play on, and the match can be reloaded with everyone from "MP Resume".
// Players with save copies on also write "MP Resume (copy)". After the host's own game crashes, the player who takes
// over as host does the same.
//
// Mods\k2mp.ini:
//   [features]
//   pause_on_drop = 1        ; on unless set to 0
//   [pause_on_drop]
//   pause = 1                ; 0: only save, keep playing
#pragma once
#include <cstdint>

namespace dropguard {

// Checks the code and points the decree's call of TGC_AdminNode::PlayerLeave at our hook. False: nothing changed.
bool Install(uintptr_t exeBase);

// From K2Plugin_Tick (the game's thread): saves and pauses after a leave the hook saw.
void Tick();

// True when this PC hosts a running match: `admin` is the server (backup mode allowed: that is the player who took
// over from a host that is gone), `session` is in state 2 or 4. Plain memory, for tests.
bool HostOfRunningMatch(const uint8_t* admin, const uint8_t* session);

// For test/hooktest.cpp.
namespace testing {
void* Hook();
void Route(uintptr_t original);
long Pending();  // leaves seen and not yet handled by Tick
}  // namespace testing

}  // namespace dropguard
