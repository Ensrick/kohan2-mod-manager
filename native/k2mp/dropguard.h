// Pause on drop: when a player leaves a running multiplayer match (their game crashed, their connection was lost, or
// they quit), the host saves the match as "MP Resume <date> <time>" and pauses it for everyone, with the game's own GameSave and
// GamePause commands. Today the game hands the leaver's kingdom to the AI and plays on, and a leaver who comes back is
// refused ("Game is already in progress", test rig 2026-10-12). With this, nothing is lost while they are gone: the
// others can wait, or unpause (Pause key) and play on, and the match can be reloaded with everyone from "MP Resume".
// Players with save copies on also write "MP Resume (copy)". After the host's own game crashes, the player who takes
// over as host does the same.
//
// A player whose game crashed often just goes silent: the game times the connection out, goes into "backup mode" and
// waits on its connection status screen for the players to vote to drop them (test rig: indefinitely). Every PC
// with this on casts that vote, the game's own CSDKickLaggingPlayers, once it has been in backup mode with a
// connection down for `vote_after` seconds in a running match; the game then removes the player and the steps below
// follow.
//
// A player who quits on purpose is left alone, as the game does without the plugin: their game asks the host to let
// them go first (SerializeClientDisconnectRequest), and a leave that follows such a request within 3 s is not a drop.
//
// Then, on the original host's game (lobby step): once "MP Resume" is written, it takes everyone back to the lobby
// with the game's own "End Mission" (still connected), and selects "MP Resume" there as "Load saved game" would. The
// lobby accepts joins, so the player who dropped can join again and get the save (fast transfers), and the host
// launches when everyone is back. After a host crash the match stays (the old host would have to find the new
// host's lobby, which is untested).
//
// Mods\k2mp.ini:
//   [features]
//   pause_on_drop = 1        ; on unless set to 0
//   [pause_on_drop]
//   pause = 1                ; 0: only save, keep playing
//   lobby = 1                ; 0: no lobby step (save and pause only)
//   vote_after = 20          ; seconds a connection may be down before voting to drop the player; 0: never vote
#pragma once
#include <cstdint>

namespace dropguard {

// Checks the code and points the decree's call of TGC_AdminNode::PlayerLeave at our hook. False: nothing changed.
bool Install(uintptr_t exeBase);

// From K2Plugin_Tick (the game's thread): saves and pauses after a leave the hook saw, then the lobby step.
void Tick();

// The lobby step's code matched this k2.exe and is on.
bool LobbyStep();

// True when this PC hosts a running match: `admin` is the server (backup mode allowed: that is the player who took
// over from a host that is gone), `session` is in state 2 or 4. Plain memory, for tests.
bool HostOfRunningMatch(const uint8_t* admin, const uint8_t* session);

// End Mission may run: this PC is the original host (not backup mode), the session is a running multiplayer match
// (state 2, networked, no launch countdown), the world exists and the game type is multiplayer.
bool CanEndMission(const uint8_t* admin, const uint8_t* session, const void* world, int gameType);

// True when a connection of `netNode` to another PC is down (timed out, still listed). Plain memory, for tests.
bool ConnectionDown(const uint8_t* netNode);

// For test/hooktest.cpp.
namespace testing {
void* Hook();
void* DisconnectRequestHook();
void RouteDisconnectRequest(uintptr_t original);
bool LeftOnPurposeNow();  // a disconnect request was seen in the last 3 s
void Route(uintptr_t original);
long Pending();  // leaves seen and not yet handled by Tick
}  // namespace testing

}  // namespace dropguard
