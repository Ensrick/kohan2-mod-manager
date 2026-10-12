// Test rig support (tests/rig): lets a scripted copy of the game join another one by IP address.
//
// The game's Direct IP dialog (the only way left to join a LAN game in the Steam rebuild) reads its IP and port
// editboxes when Join is pressed, and no script command can fill an editbox. With K2MP_RIG_JOIN=<ip>:<port> in the
// environment, the two reads return that address instead, so a script can run `LobbyJoinDirect` and `JoinDirectJoin`.
// Without the variable (every normal start) nothing is installed. tests/rig/Start-K2.ps1 sets it for the client copy.
#pragma once
#include <cstdint>

namespace rig {

// Installs the two read hooks when K2MP_RIG_JOIN is set and valid. Returns false when not set (the normal case) or
// when the bytes do not match; logs which.
bool Install(uintptr_t exeBase);

}  // namespace rig
