// Save copies: every player's game keeps its own copy of each multiplayer save.
//
// In a multiplayer match only the host writes a save; the other players' games just pause until the host is done
// (KKC_Session::GameSave). Every game in a match is at the same point of the simulation when it applies the save
// order, so each of them can write the same save. With this feature on, a non-host game writes "<name> (copy).RSG"
// next to where the host's "<name>.RSG" would be, using the engine's own save task, while it is paused anyway. If
// the host's PC is gone, any player can host the match again from their copy.
//
// The copy gets its own name until it is known that copies come out byte-identical to the host's file: a different
// file under the host's name would make the lobby refuse it ("Your version of ... is different"). To find out, the
// host logs the SHA-256 of its save and every other player logs the SHA-256 of their copy (Mods\plugins.log).
#pragma once
#include <cstdint>
#include <string>

namespace savecopy {

// Patches the two GameSave call sites when every byte this feature depends on matches. False: nothing changed.
bool Install(uintptr_t exeBase);

// "Save/X.RSG" -> "Save/X (copy).RSG". Empty when `name` is not a relative path to an .RSG file.
std::wstring CopyName(const std::wstring& name);

// For test/hooktest.cpp: the stubs, with the functions they call swapped for test doubles.
namespace testing {
void* ClientStub();
void* HostStub();
void Route(uintptr_t beginSoloTask, uintptr_t createSaveGameTask, void(__cdecl* onClient)(void*),
           void(__cdecl* onHost)(void*));
}  // namespace testing

}  // namespace savecopy
