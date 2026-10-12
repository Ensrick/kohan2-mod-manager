#include "fastsend.h"

#include <windows.h>

#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"

namespace fastsend {

namespace {

using WantedTypeFn = int(__fastcall*)(void* conn);

WantedTypeFn g_original = nullptr;
uintptr_t g_adminNodeGlobal = 0;  // addresses of the game's pointer variables
uintptr_t g_netNodeGlobal = 0;
int g_packetsPerSecond = 60;

// Connections this PC is sending a file to: each paces its extra packets with its own bucket, and gets one log line
// when its transfer starts and one when it ends. Only the hooks touch this, and the game calls both from
// TGC_NetNode::SendPackets, so always from the same thread.
struct Transfer {
    const void* conn;
    float started;
    float seen;  // a connection unseen for seconds is gone; another one may get its address
    int early;   // packets sent before the game's own 0.1 s
    Bucket bucket;
};
Transfer g_transfers[16] = {};

const char* const kNeeds[] = {"TGC_AdminConn::GetWantedPacketType", "GetWantedPacketType host branch",
                              "packet writer call", "send scheduler call", "TGC_AdminConn::UpdateFileTransferStatus"};

template <typename T>
T Field(const uint8_t* object, uint32_t offset) {
    return *reinterpret_cast<const T*>(object + offset);
}

Transfer* Find(const void* conn) {
    for (Transfer& t : g_transfers)
        if (t.conn == conn) return &t;
    return nullptr;
}

// The transfer entry of `conn` while it is eligible, created (and logged) on first sight; ends and logs it otherwise.
Transfer* Follow(const void* conn, float clock, bool eligible) {
    Transfer* t = Find(conn);
    if (t && clock - t->seen > 5) *t = {};  // that player left mid-transfer: a new connection at the same address
    if (t && !t->conn) t = nullptr;
    if (!eligible) {
        if (t) {
            k2mp::Log("fast transfers: transfer ended after %.1f s; %d packets went early", clock - t->started, t->early);
            *t = {};
        }
        return nullptr;
    }
    if (!t) {
        t = Find(nullptr);
        if (!t) return nullptr;  // more than 16 at once: the others keep the game's pace
        *t = {conn, clock, clock, 0, {1.0f, clock}};
        k2mp::Log("fast transfers: sending a player a file, up to %d packets/s", g_packetsPerSecond);
    }
    t->seen = clock;
    return t;
}

// Both call sites, ECX = the connection. The game's answer stands unless it is "nothing yet".
int Decide(void* conn, bool writing) {
    const int type = g_original(conn);
    __try {
        const auto* admin = *reinterpret_cast<const uint8_t* const*>(g_adminNodeGlobal);
        const auto* node = *reinterpret_cast<const uint8_t* const*>(g_netNodeGlobal);
        const auto* c = static_cast<const uint8_t*>(conn);
        if (!admin || !node || !c) return type;
        const float clock = Field<float>(node, game::kNetNodeClock);
        Transfer* t = Follow(c, clock, Eligible(admin, c));
        if (!t || type != 0) return type;
        if (!Take(t->bucket, clock, static_cast<float>(g_packetsPerSecond), writing)) return 0;
        if (writing) ++t->early;
        return game::kPacketTypeStreams;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return type;
    }
}

int __fastcall ScheduleHook(void* conn) { return Decide(conn, false); }
int __fastcall WriteHook(void* conn) { return Decide(conn, true); }

}  // namespace

bool Eligible(const uint8_t* admin, const uint8_t* conn) {
    if (Field<const void*>(admin, game::kAdminServer) != admin) return false;  // not the host
    if (Field<uint8_t>(admin, game::kAdminFlag2ec) || Field<uint8_t>(admin, game::kAdminBackupMode)) return false;
    if (!Field<uint8_t>(conn, game::kConnUp) || Field<uint8_t>(conn, game::kConnFlag99)) return false;
    return Field<int>(conn, game::kConnFileState) == game::kFileStateSending;
}

bool Take(Bucket& b, float clock, float rate, bool consume) {
    const float cap = rate * kBurstSeconds > 1.0f ? rate * kBurstSeconds : 1.0f;
    if (clock > b.refilled) {
        b.tokens += (clock - b.refilled) * rate;
        if (b.tokens > cap) b.tokens = cap;
    }
    b.refilled = clock;
    if (b.tokens < 1.0f) return false;
    if (consume) b.tokens -= 1.0f;
    return true;
}

int PacketsPerSecond() { return g_packetsPerSecond; }

bool Install(uintptr_t exeBase) {
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) return false;
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    const int rate = static_cast<int>(GetPrivateProfileIntW(L"fast_transfers", L"packets_per_second", 60, ini.c_str()));
    g_packetsPerSecond = rate < 11 ? 11 : rate > 200 ? 200 : rate;
    // `mov ecx, [admin node]` (8B 0D imm32) and `mov eax, [net node]` (A1 imm32) inside GetWantedPacketType: the
    // operands are the globals' addresses, already relocated in memory.
    g_adminNodeGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 5);
    g_netNodeGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 0x21);
    const uintptr_t original = exeBase + game::kGetWantedPacketType;
    g_original = reinterpret_cast<WantedTypeFn>(original);
    if (!hook::RedirectCall(exeBase + game::kWantedTypeWriteCall, original, reinterpret_cast<void*>(&WriteHook)))
        return false;
    if (!hook::RedirectCall(exeBase + game::kWantedTypeScheduleCall, original,
                            reinterpret_cast<void*>(&ScheduleHook))) {
        // put the first one back so the game runs exactly as shipped
        hook::RedirectCall(exeBase + game::kWantedTypeWriteCall, reinterpret_cast<uintptr_t>(&WriteHook),
                           reinterpret_cast<void*>(original));
        return false;
    }
    return true;
}

namespace testing {
void* Hook() { return reinterpret_cast<void*>(&WriteHook); }
void Route(uintptr_t original, uintptr_t adminNodeGlobal, uintptr_t netNodeGlobal, int packetsPerSecond) {
    g_original = reinterpret_cast<WantedTypeFn>(original);
    g_adminNodeGlobal = adminNodeGlobal;
    g_netNodeGlobal = netNodeGlobal;
    g_packetsPerSecond = packetsPerSecond;
    for (Transfer& t : g_transfers) t = {};
}
}  // namespace testing

}  // namespace fastsend
