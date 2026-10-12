// Transfer settings (experimental, off unless configured): overrides of the game's file transfer settings, for
// measuring on the test rig what makes sending a save faster.
//
// Mods\k2mp.ini:
//   [transfer]
//   blocks_per_frame = 20      ; NetworkFileTransferBlocksPerFrame
//   block_size = 64            ; NetworkFileTransferBlockSize: MUST be the same on every PC in a game, or files arrive
//                              ; corrupt (the receiver splits them with its own value)
//   max_pending_blocks = 256   ; NetworkFileTransferMaxPendingBlocks (at most 256: the stream's ring buffer)
// Keys left out keep the game's values. The values are written into the game's settings table once it exists.
#pragma once
#include <cstdint>

namespace transfer {

// Reads the configured overrides. False when nothing is configured or the code differs from the build this plugin
// knows. The values are written later, by Tick, on the game's own thread.
bool Install(uintptr_t exeBase);

// From K2Plugin_Tick: writes the overrides once the game's settings table exists.
void Tick();

}  // namespace transfer
