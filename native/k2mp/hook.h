// Byte checks and call-site patches inside k2.exe.
//
// Multiplayer Fixes only ever redirects existing `call rel32` instructions (opcode E8): the patched call goes to a
// stub of ours that calls the original target itself. Nothing is copied or relocated, the instruction keeps its
// length, and undoing it is writing the old four bytes back.
#pragma once
#include <cstdint>

#include "game.h"

namespace hook {

// True when the bytes at `addr` match `pattern` (hex pairs, "??" = any byte). Unreadable memory does not match.
bool Matches(uintptr_t addr, const char* pattern);

// True when every signature in game::kSignatures whose name is in `names` matches at exeBase.
bool SignaturesMatch(uintptr_t exeBase, const char* const* names, int count);

// The call instruction at `site` (opcode E8) and its current target, or false if `site` is not such a call.
bool ReadCall(uintptr_t site, uintptr_t* target);

// Points the call at `site` to `stub` if it currently calls `expected`. Returns false and changes nothing otherwise.
bool RedirectCall(uintptr_t site, uintptr_t expected, void* stub);

}  // namespace hook
