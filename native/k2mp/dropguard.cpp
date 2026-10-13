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
using EndMissionFn = void(__cdecl*)();
using StringCtorFn = void*(__thiscall*)(void* self, const wchar_t* text);
using StringReleaseFn = void(__thiscall*)(void* header);
using OrderCtorFn = void*(__thiscall*)(void* self, void* name);
using OrderIssueFn = void(__thiscall*)(void* self, int target);

// The save of the moment of a leave: "Save/MP Resume <date> <time>.RSG", a new name each time. A player who already
// has a file of the name the lobby selects, with other contents, is asked "A different version of this map exists -
// overwrite it?" (Cancel leaves the lobby); a fresh name means nobody has an older one.
std::wstring g_resumeSave;

std::wstring NewResumeName() {
    SYSTEMTIME t;
    GetLocalTime(&t);
    wchar_t name[64];
    swprintf_s(name, L"Save/MP Resume %04u-%02u-%02u %02u%02u%02u.RSG", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute,
               t.wSecond);
    return name;
}

PlayerLeaveFn g_original = nullptr;
uintptr_t g_base = 0;
uintptr_t g_adminGlobal = 0;  // addresses of the game's pointer variables; 0 = off
uintptr_t g_sessionGlobal = 0;
uintptr_t g_app = 0;          // the application object End Mission reads the game type from; 0 = no lobby step
uintptr_t g_netNodeGlobal = 0;  // 0 = no voting
int g_voteAfterMs = 20000;
ULONGLONG g_downSince = 0;      // a connection in the running match has been down since then
ULONGLONG g_lastVote = 0;
int g_votes = 0;
bool g_pause = true;
bool g_lobby = true;
volatile LONG g_pending = 0;  // leaves the hook saw; the hook may run on the game's network thread
volatile LONG g_lastReason = -1;

// After a leave: save, then (lobby on, this PC the original host) wait for the save, take everyone to the lobby, and
// select the save there. One step per tick; each waits at most kStepTimeoutMs.
//   Selecting: in the lobby, a moment after arriving (an order sent in the same instant as the switch is refused
//              "due to versioning": the switch resets the order version), send the save's selection;
//   Confirming: until the session plays that file, send it again every 2 s (at most 5 times); then take the host's
//              own screen from the score screen to the lobby page (ScoringExit), where Launch works.
enum class Step { Idle, Saving, Ending, Selecting, Confirming };
Step g_step = Step::Idle;
ULONGLONG g_stepSince = 0;
ULONGLONG g_sentAt = 0;
ULONGLONG g_arrivedAt = 0;  // when the session was first seen in the lobby after End Mission
int g_sends = 0;
ULONGLONG g_askedAt = 0;  // system time (FILETIME units) when GameSave was queued
ULONGLONG g_lastSize = ~0ull;
constexpr ULONGLONG kStepTimeoutMs = 30000;

const char* const kNeeds[] = {"player leave decree", "TGC_AdminNode::PlayerLeave", "session read",
                              "TGC_AdminConn::GetWantedPacketType", "KKC_Session::GameSave"};
const char* const kVoteNeeds[] = {"for each connection", "net connection's admin connection",
                                  "kick lagging players vote"};
const char* const kLobbyNeeds[] = {"End Mission", "game type object", "HostSetLoadSavedGame ctor",
                                   "HostSetLoadSavedGame issue", "session countdown read", "engine string ctor",
                                   "engine string release"};

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

struct Snapshot {
    const uint8_t* admin;
    const uint8_t* session;
    const void* world;
    int gameType;
    int state;
    int hostKind;
    bool resumeSelected;  // the session's file is the resume save
    bool connectionDown;
};

// SEH: reads game memory. False when anything is missing.
bool Read(Snapshot* s) {
    __try {
        s->admin = *reinterpret_cast<const uint8_t* const*>(g_adminGlobal);
        s->session = *reinterpret_cast<const uint8_t* const*>(g_sessionGlobal);
        s->world = *reinterpret_cast<const void* const*>(g_base + game::kGWorld);
        s->gameType = -1;
        if (g_app) {
            const auto* type = *reinterpret_cast<const uint8_t* const*>(g_app + game::kAppGameTypeObject);
            if (type) s->gameType = Field<int>(type, game::kGameTypeValue);
        }
        if (!s->admin || !s->session) return false;
        s->state = Field<int>(s->session, game::kSessionState);
        s->connectionDown = g_netNodeGlobal && ConnectionDown(*reinterpret_cast<const uint8_t* const*>(g_netNodeGlobal));
        s->hostKind = Field<int>(s->admin, game::kAdminHostKind);
        const wchar_t* file = Field<const wchar_t*>(s->session, game::kSessionFilename);
        s->resumeSelected = file && !g_resumeSave.empty() && _wcsicmp(file, g_resumeSave.c_str()) == 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

int EndMission(DWORD* code) {
    __try {
        reinterpret_cast<EndMissionFn>(g_base + game::kEndMission)();
        return 1;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// What the lobby's "Load saved game" does: KKC_HostSetLoadSavedGameOrder with the file name, issued to the host.
int SelectSave(const wchar_t* name, DWORD* code) {
    __try {
        wchar_t* text = nullptr;  // an engine string is one pointer to its text, its header 16 bytes before it
        reinterpret_cast<StringCtorFn>(g_base + game::kStringFromLiteral)(&text, name);
        struct {
            void* vtable;
            wchar_t* name;
        } order = {};
        reinterpret_cast<OrderCtorFn>(g_base + game::kHostSetLoadSavedGameCtor)(&order, &text);
        reinterpret_cast<OrderIssueFn>(g_base + game::kHostSetLoadSavedGameIssue)(&order, 0);
        const auto release = reinterpret_cast<StringReleaseFn>(g_base + game::kStringRelease);
        if (order.name) release(reinterpret_cast<char*>(order.name) - 0x10);
        release(reinterpret_cast<char*>(text) - 0x10);
        return 1;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

ULONGLONG SystemTime() {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    return (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
}

// The resume save's size once it is newer than the request, or ~0 while it is not there yet.
ULONGLONG ResumeSaveSize() {
    std::wstring path = k2mp::UserDataDir() + L"\\" + g_resumeSave;
    for (wchar_t& c : path)
        if (c == L'/') c = L'\\';
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &a)) return ~0ull;
    const ULONGLONG written = (static_cast<ULONGLONG>(a.ftLastWriteTime.dwHighDateTime) << 32) |
                              a.ftLastWriteTime.dwLowDateTime;
    if (written + 20000000ull < g_askedAt) return ~0ull;  // older than the request (2 s of clock slack)
    return (static_cast<ULONGLONG>(a.nFileSizeHigh) << 32) | a.nFileSizeLow;
}

void Go(Step step) {
    g_step = step;
    g_stepSince = GetTickCount64();
}

void OnLeave() {
    const LONG leaves = InterlockedExchange(&g_pending, 0);
    const LONG reason = g_lastReason;
    Snapshot s;
    if (!Read(&s) || !HostOfRunningMatch(s.admin, s.session)) return;  // not the host, or a leave from the lobby
    if (g_step != Step::Idle) {
        k2mp::Log("pause on drop: %ld more player(s) left (reason %ld); already handling a leave", leaves, reason);
        return;
    }
    const bool tookOver = Field<uint8_t>(s.admin, game::kAdminBackupMode) != 0;
    g_resumeSave = NewResumeName();
    k2mp::Log("pause on drop: %ld player(s) left the match (reason %ld); saving \"%s\"%s", leaves, reason,
              k2mp::Utf8(g_resumeSave).c_str(), g_pause ? " and pausing" : "");
    g_askedAt = SystemTime();
    if (!script::Run((std::wstring(L"GameSave \"") + g_resumeSave + L"\"").c_str())) {
        k2mp::Log("pause on drop: could not run GameSave");
        return;
    }
    if (g_pause && !script::Run(L"GamePause 1")) k2mp::Log("pause on drop: could not run GamePause");
    if (!g_lobby || !g_app) return;
    if (tookOver) {
        // The lost host's game would have to find this PC's lobby; that path is untested, so the match stays.
        k2mp::Log("pause on drop: this PC took over from the lost host; staying in the match (reload \"MP Resume\" "
                  "to continue with them)");
        return;
    }
    g_lastSize = ~0ull;
    Go(Step::Saving);
}

}  // namespace

bool HostOfRunningMatch(const uint8_t* admin, const uint8_t* session) {
    if (!admin || !session) return false;
    if (Field<const void*>(admin, game::kAdminServer) != admin) return false;
    const int state = Field<int>(session, game::kSessionState);
    return state == 2 || state == 4;
}

bool ConnectionDown(const uint8_t* netNode) {
    if (!netNode) return false;
    int guard = 0;
    for (auto* node = Field<const uint8_t*>(netNode, game::kNetNodeConnections); node && guard < 64; ++guard) {
        const auto* net = Field<const uint8_t*>(node, 0);
        const auto* conn = net ? Field<const uint8_t*>(net, game::kNetConnAdminConn) : nullptr;
        if (conn && !Field<uint8_t>(conn, game::kConnUp)) return true;
        node = Field<const uint8_t*>(node, 4);
    }
    return false;
}

// Votes to drop players whose connection stays down in a running match (see the header).
static void Vote(const Snapshot& s, ULONGLONG now) {
    // Only while the game waits: backup mode with a connection down. (A connection of a player who already left can
    // stay listed, down, into the next match; the game is not waiting for anyone then.)
    if (!g_voteAfterMs || !g_netNodeGlobal || s.state != 2 || !s.connectionDown ||
        !Field<uint8_t>(s.admin, game::kAdminBackupMode)) {
        g_downSince = 0;
        g_votes = 0;
        return;
    }
    if (!g_downSince) {
        g_downSince = now;
        k2mp::Log("pause on drop: a connection in the match went down; voting to drop that player in %d s unless it "
                  "comes back", g_voteAfterMs / 1000);
        return;
    }
    if (now - g_downSince < static_cast<ULONGLONG>(g_voteAfterMs)) return;
    if (g_votes >= 3 || (g_lastVote && now - g_lastVote < 30000)) return;
    // The command's argument is the vote currently shown; it votes the opposite: 0 = vote to drop.
    if (script::Run(L"CSDKickLaggingPlayers 0")) {
        ++g_votes;
        g_lastVote = now;
        k2mp::Log("pause on drop: voted to drop the player who stopped answering %llu s ago (vote %d)",
                  (now - g_downSince) / 1000, g_votes);
    }
}

bool CanEndMission(const uint8_t* admin, const uint8_t* session, const void* world, int gameType) {
    if (!admin || !session || !world) return false;
    if (Field<const void*>(admin, game::kAdminServer) != admin || Field<uint8_t>(admin, game::kAdminBackupMode))
        return false;
    return Field<int>(session, game::kSessionState) == 2 && Field<uint8_t>(session, game::kSessionNetworked) &&
           Field<int>(session, game::kSessionCountdown) <= 0 && gameType == game::kGameTypeMultiplayer;
}

bool Install(uintptr_t exeBase) {
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) return false;
    g_base = exeBase;
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    g_pause = GetPrivateProfileIntW(L"pause_on_drop", L"pause", 1, ini.c_str()) != 0;
    g_lobby = GetPrivateProfileIntW(L"pause_on_drop", L"lobby", 1, ini.c_str()) != 0;
    const int voteAfter = static_cast<int>(GetPrivateProfileIntW(L"pause_on_drop", L"vote_after", 20, ini.c_str()));
    g_voteAfterMs = (voteAfter < 0 ? 0 : voteAfter > 600 ? 600 : voteAfter) * 1000;
    if (g_voteAfterMs && hook::SignaturesMatch(exeBase, kVoteNeeds, static_cast<int>(sizeof(kVoteNeeds) / sizeof(kVoteNeeds[0]))))
        g_netNodeGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 0x21);
    else if (g_voteAfterMs)
        k2mp::Log("pause on drop: voting is not available for this k2.exe");
    g_sessionGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kSessionRead + 1);
    g_adminGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kGetWantedPacketType + 5);
    uintptr_t target = 0;
    if (g_lobby && hook::SignaturesMatch(exeBase, kLobbyNeeds, static_cast<int>(sizeof(kLobbyNeeds) / sizeof(kLobbyNeeds[0]))) &&
        hook::ReadCall(exeBase + game::kEndMissionSetMapTypeCall, &target) && target == exeBase + game::kSetMapTypeIssue &&
        hook::ReadCall(exeBase + game::kEndMissionLaunchCall, &target) && target == exeBase + game::kLaunchIssue)
        g_app = *reinterpret_cast<const uint32_t*>(exeBase + game::kEndMission + 0xe);  // `mov ecx, app` operand
    else if (g_lobby)
        k2mp::Log("pause on drop: the lobby step is not available for this k2.exe (save and pause only)");
    const uintptr_t original = exeBase + game::kPlayerLeave;
    g_original = reinterpret_cast<PlayerLeaveFn>(original);
    return hook::RedirectCall(exeBase + game::kPlayerLeaveDecreeCall, original,
                              reinterpret_cast<void*>(&OnPlayerLeave));
}

bool LobbyStep() { return g_app != 0; }

void Tick() {
    if (!g_sessionGlobal) return;
    if (g_pending) OnLeave();
    const ULONGLONG now = GetTickCount64();
    if (g_step == Step::Idle) {
        static ULONGLONG lastLook = 0;
        if (now - lastLook < 1000) return;  // the vote needs no more than a look a second
        lastLook = now;
        Snapshot v;
        if (Read(&v)) Vote(v, now);
        return;
    }
    if (now - g_stepSince > kStepTimeoutMs) {
        k2mp::Log("pause on drop: gave up waiting (step %d); the match stays as it is", static_cast<int>(g_step));
        Go(Step::Idle);
        return;
    }
    Snapshot s;
    if (!Read(&s)) return;
    switch (g_step) {
        case Step::Saving: {
            // the save task writes the file on the loader thread: wait until it is there and stops growing
            const ULONGLONG size = ResumeSaveSize();
            if (size == ~0ull || size == 0 || size != g_lastSize) {
                g_lastSize = size;
                return;
            }
            Go(Step::Ending);
            return;
        }
        case Step::Ending: {
            if (!CanEndMission(s.admin, s.session, s.world, s.gameType)) {
                k2mp::Log("pause on drop: cannot take everyone to the lobby now (state %d, game type %d); staying",
                          s.state, s.gameType);
                Go(Step::Idle);
                return;
            }
            k2mp::Log("pause on drop: \"MP Resume\" saved; taking everyone back to the lobby (host kind %d)",
                      s.hostKind);
            DWORD code = 0;
            if (EndMission(&code) < 0) {
                k2mp::Log("pause on drop: End Mission failed with exception 0x%08lx; lobby step off", code);
                g_app = 0;
                Go(Step::Idle);
                return;
            }
            g_arrivedAt = 0;
            Go(Step::Selecting);
            return;
        }
        case Step::Selecting: {
            if (s.state != game::kSessionStaging) {
                g_arrivedAt = 0;
                return;
            }
            if (!g_arrivedAt) g_arrivedAt = now;
            if (now - g_arrivedAt < 1500) return;
            g_sends = 0;
            g_sentAt = 0;
            Go(Step::Confirming);
            return;
        }
        case Step::Confirming: {
            if (s.state != game::kSessionStaging) {
                k2mp::Log("pause on drop: the session left the lobby (state %d) before \"MP Resume\" was selected", s.state);
                Go(Step::Idle);
                return;
            }
            if (s.resumeSelected) {
                k2mp::Log("pause on drop: in the lobby with \"MP Resume\" selected after %d send(s): the player who "
                          "dropped can join again, then launch", g_sends);
                if (s.hostKind != 2 && !script::Run(L"ScoringExit")) k2mp::Log("pause on drop: could not run ScoringExit");
                Go(Step::Idle);
                return;
            }
            if (g_sentAt && now - g_sentAt < 2000) return;
            if (g_sends >= 5) {
                k2mp::Log("pause on drop: the lobby did not take \"MP Resume\" after %d sends; choose it in Load saved game",
                          g_sends);
                Go(Step::Idle);
                return;
            }
            DWORD code = 0;
            if (SelectSave(g_resumeSave.c_str(), &code) < 0) {
                k2mp::Log("pause on drop: selecting \"MP Resume\" failed with exception 0x%08lx", code);
                g_app = 0;
                Go(Step::Idle);
                return;
            }
            ++g_sends;
            g_sentAt = now;
            return;
        }
        default:
            Go(Step::Idle);
    }
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
