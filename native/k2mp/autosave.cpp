#include "autosave.h"

#include <windows.h>

#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"
#include "script.h"

namespace autosave {

namespace {

const wchar_t* const kSlots[] = {L"MP Autosave A", L"MP Autosave B"};

uintptr_t g_adminGlobal = 0;  // addresses of the game's pointer variables; 0 = off
uintptr_t g_sessionGlobal = 0;
uintptr_t g_netNodeGlobal = 0;
ULONGLONG g_intervalMs = 5 * 60 * 1000;
ULONGLONG g_since = 0;  // when the current match was first seen (0 = none), then the last save
int g_next = 0;         // slot of the next save

const char* const kNeeds[] = {"session read", "for each connection", "TGC_AdminConn::GetWantedPacketType",
                              "KKC_Session::GameSave"};

template <typename T>
T Field(const uint8_t* object, uint32_t offset) {
    return *reinterpret_cast<const T*>(object + offset);
}

// SEH: reads game memory.
bool HostingMatchNow() {
    __try {
        return HostingMatch(*reinterpret_cast<const uint8_t* const*>(g_adminGlobal),
                            *reinterpret_cast<const uint8_t* const*>(g_sessionGlobal),
                            *reinterpret_cast<const uint8_t* const*>(g_netNodeGlobal));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace

bool HostingMatch(const uint8_t* admin, const uint8_t* session, const uint8_t* netNode) {
    if (!admin || !session || !netNode) return false;
    if (Field<const void*>(admin, game::kAdminServer) != admin) return false;  // not the host
    if (Field<uint8_t>(admin, game::kAdminBackupMode)) return false;
    const int state = Field<int>(session, game::kSessionState);
    if (state != 2 && state != 4) return false;  // KKC_Session::GameSave refuses the others
    return Field<const void*>(netNode, game::kNetNodeConnections) != nullptr;  // someone else is in the match
}

bool Install(uintptr_t exeBase) {
    if (!k2mp::Setting(L"autosave", false)) return false;
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) {
        k2mp::Log("autosave: not available for this k2.exe");
        return false;
    }
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    int minutes = static_cast<int>(GetPrivateProfileIntW(L"autosave", L"minutes", 5, ini.c_str()));
    minutes = minutes < 1 ? 1 : minutes > 120 ? 120 : minutes;
    g_intervalMs = static_cast<ULONGLONG>(minutes) * 60 * 1000;
    // The globals' addresses, relocated, from instructions that read them: `mov eax, [session]` (A1 imm32), and in
    // GetWantedPacketType `mov ecx, [admin node]` (8B 0D imm32) and `mov eax, [net node]` (A1 imm32).
    g_sessionGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kSessionRead + 1);
    g_adminGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 5);
    g_netNodeGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 0x21);
    k2mp::Log("autosave: on (as host of a multiplayer match, saves every %d minute(s) into \"MP Autosave A\" and "
              "\"B\" in turn)",
              minutes);
    return true;
}

void Tick() {
    if (!g_sessionGlobal) return;
    const ULONGLONG now = GetTickCount64();
    if (!HostingMatchNow()) {
        if (g_since) k2mp::Log("autosave: the match this PC hosted is over");
        g_since = 0;
        return;
    }
    if (!g_since) {
        g_since = now;
        k2mp::Log("autosave: hosting a match; first save in %llu minute(s)", g_intervalMs / 60000);
        return;
    }
    if (now - g_since < g_intervalMs) return;
    const std::wstring command = std::wstring(L"GameSave \"Save/") + kSlots[g_next] + L".RSG\"";
    if (!script::Run(command.c_str())) return;  // console not there (logged if it failed): try again next tick
    k2mp::Log("autosave: saving \"%s\"", k2mp::Utf8(kSlots[g_next]).c_str());
    g_since = now;
    g_next ^= 1;
}

}  // namespace autosave
