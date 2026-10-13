#include "dropguard.h"

#include <windows.h>

#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"
#include "script.h"

namespace dropguard {

namespace {

using PlayerLeaveFn = void(__fastcall*)(void* admin, void* edx, void* player, int reason);

PlayerLeaveFn g_original = nullptr;
uintptr_t g_adminGlobal = 0;  // addresses of the game's pointer variables; 0 = off
uintptr_t g_sessionGlobal = 0;
bool g_pause = true;
volatile LONG g_pending = 0;      // leaves the hook saw; the hook may run on the game's network thread
volatile LONG g_lastReason = -1;
ULONGLONG g_lastHandled = 0;

const char* const kNeeds[] = {"player leave decree", "TGC_AdminNode::PlayerLeave", "session read",
                              "TGC_AdminConn::GetWantedPacketType", "KKC_Session::GameSave"};

template <typename T>
T Field(const uint8_t* object, uint32_t offset) {
    return *reinterpret_cast<const T*>(object + offset);
}

// The decree's call, ECX = admin node, (player, reason) on the stack, callee pops 8: the game's code runs first and
// unchanged; a leave of a real player is only noted here, and acted on from the game's own thread.
void __fastcall OnPlayerLeave(void* admin, void* edx, void* player, int reason) {
    g_original(admin, edx, player, reason);
    if (player) {
        InterlockedExchange(&g_lastReason, reason);
        InterlockedIncrement(&g_pending);
    }
}

// SEH: reads game memory.
bool HostingNow() {
    __try {
        return HostOfRunningMatch(*reinterpret_cast<const uint8_t* const*>(g_adminGlobal),
                                  *reinterpret_cast<const uint8_t* const*>(g_sessionGlobal));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace

bool HostOfRunningMatch(const uint8_t* admin, const uint8_t* session) {
    if (!admin || !session) return false;
    if (Field<const void*>(admin, game::kAdminServer) != admin) return false;
    const int state = Field<int>(session, game::kSessionState);
    return state == 2 || state == 4;
}

bool Install(uintptr_t exeBase) {
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) return false;
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    g_pause = GetPrivateProfileIntW(L"pause_on_drop", L"pause", 1, ini.c_str()) != 0;
    g_sessionGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kSessionRead + 1);
    g_adminGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 5);
    const uintptr_t original = exeBase + game::kPlayerLeave;
    g_original = reinterpret_cast<PlayerLeaveFn>(original);
    return hook::RedirectCall(exeBase + game::kPlayerLeaveDecreeCall, original,
                              reinterpret_cast<void*>(&OnPlayerLeave));
}

void Tick() {
    if (!g_sessionGlobal || !g_pending) return;
    const LONG leaves = InterlockedExchange(&g_pending, 0);
    const LONG reason = g_lastReason;
    if (!HostingNow()) return;  // not the host, or not in a match (a leave from the lobby)
    const ULONGLONG now = GetTickCount64();
    if (g_lastHandled && now - g_lastHandled < 10000) {  // one save for several players leaving at once
        k2mp::Log("pause on drop: %ld more player(s) left (reason %ld); already saved", leaves, reason);
        return;
    }
    g_lastHandled = now;
    k2mp::Log("pause on drop: %ld player(s) left the match (reason %ld); saving \"MP Resume\"%s", leaves, reason,
              g_pause ? " and pausing" : "");
    if (!script::Run(L"GameSave \"Save/MP Resume.RSG\"")) k2mp::Log("pause on drop: could not run GameSave");
    if (g_pause && !script::Run(L"GamePause 1")) k2mp::Log("pause on drop: could not run GamePause");
}

namespace testing {
void* Hook() { return reinterpret_cast<void*>(&OnPlayerLeave); }
void Route(uintptr_t original) {
    g_original = reinterpret_cast<PlayerLeaveFn>(original);
    g_pending = 0;
}
long Pending() { return g_pending; }
}  // namespace testing

}  // namespace dropguard
