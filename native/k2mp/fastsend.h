// Fast transfers: the host sends saved games to players several times faster.
//
// The game sends a player who is in the lobby one packet every 0.1 s, at most 384 bytes each, and a file goes out in
// whatever room those packets have left: about 2 KB/s, so a 900 KB save takes some 7 minutes (docs/MULTIPLAYER.md).
// While this PC hosts and is sending a file to a player, this feature lets that player's connection send the same
// kind of packet (type 5) as often as `packets_per_second` allows instead of waiting out the 0.1 s. The packets are
// written by the game as always, file blocks not sent yet go first, so a player with a stock game just receives the
// file sooner. Everything else (other players, the match, the game's own bandwidth limits) is left as it is.
//
// Mods\k2mp.ini:
//   [features]
//   fast_transfers = 1
//   [fast_transfers]
//   packets_per_second = 60   ; 11..200; the game's own pace is 10
#pragma once
#include <cstdint>

namespace fastsend {

// Reads [fast_transfers] from Mods\k2mp.ini and patches the two calls of GetWantedPacketType when every byte this
// feature depends on matches. False: nothing changed.
bool Install(uintptr_t exeBase);

// The pace in use: packets_per_second, clamped to 11..200.
int PacketsPerSecond();

// On plain memory so tests can drive it: true when this PC is the host, not in backup mode, the connection is up and
// this PC is sending that player a file. `admin` is this PC's admin node, `conn` the admin connection.
bool Eligible(const uint8_t* admin, const uint8_t* conn);

// Paces the extra packets of one transfer: `rate` tokens per second of the network clock, at most kBurstSeconds' worth
// banked (so several may go in one round of the game's sender, which runs fewer than `rate` times a second), one token
// per packet. True when a packet may go; `consume` takes its token (the packet writer), false only looks (the
// scheduler, which asks first in the same round).
constexpr float kBurstSeconds = 0.1f;
struct Bucket {
    float tokens;
    float refilled;  // network clock of the last refill
};
bool Take(Bucket& b, float clock, float rate, bool consume);

// For test/hooktest.cpp: the function the call sites are pointed at, and a way to swap in a fake original and globals.
namespace testing {
void* Hook();
void Route(uintptr_t original, uintptr_t adminNodeGlobal, uintptr_t netNodeGlobal, int packetsPerSecond);
}  // namespace testing

}  // namespace fastsend
