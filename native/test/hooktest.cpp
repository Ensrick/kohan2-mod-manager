// hooktest: offline tests of k2mp's hook layer and save-copy stubs, no game needed. Exit code 0 = all pass.
//
// The stubs replace the target of a `call rel32` inside k2.exe. Here each one is exercised exactly that way: a naked
// harness performs the call the game makes (same registers, same stack arguments, same callee-pops convention), the
// call is redirected with hook::RedirectCall, and the test checks that the original function still receives its
// ECX and arguments, that our callback sees the session in EDI after it, and that ESP, EBX, ESI, EDI and EBP come back
// unchanged, as the game's code after the call expects.
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "../k2mp/autosave.h"
#include "../k2mp/dropguard.h"
#include "../k2mp/fastsend.h"
#include "../k2mp/game.h"
#include "../k2mp/hook.h"
#include "../k2mp/plugin.h"
#include "../k2mp/savecopy.h"

namespace {

int g_failures = 0;

void Check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++g_failures;
}

// What the fakes and callbacks saw.
volatile uintptr_t g_soloSelf, g_soloArg1, g_soloArg2, g_createSelf, g_createArg;
volatile uintptr_t g_clientSession, g_hostSession;
volatile int g_order, g_soloAt, g_createAt, g_clientAt, g_hostAt;
// What the harness saw around the call.
uintptr_t g_espBefore, g_espAfter, g_ebxAfter, g_esiAfter, g_ediAfter, g_ebpBefore, g_ebpAfter;

// Stand-ins for the game's functions. __fastcall with an unused EDX is the free-function spelling of a thiscall:
// ECX = this, stack arguments popped by the callee.
void __fastcall FakeBeginSoloTask(void* self, void*, uintptr_t arg1, uintptr_t arg2) {
    g_soloSelf = reinterpret_cast<uintptr_t>(self);
    g_soloArg1 = arg1;
    g_soloArg2 = arg2;
    g_soloAt = ++g_order;
}

void __fastcall FakeCreateSaveGameTask(void* self, void*, uintptr_t flag) {
    g_createSelf = reinterpret_cast<uintptr_t>(self);
    g_createArg = flag;
    g_createAt = ++g_order;
}

void __cdecl OnClient(void* session) {
    g_clientSession = reinterpret_cast<uintptr_t>(session);
    g_clientAt = ++g_order;
}

void __cdecl OnHost(void* session) {
    g_hostSession = reinterpret_cast<uintptr_t>(session);
    g_hostAt = ++g_order;
}

// The game's client branch: EDI = session, ECX = admin node, push node, push 0, call BeginSoloTask.
__declspec(naked) void ClientHarness() {
    __asm {
        push ebp
        push ebx
        push esi
        push edi
        mov g_ebpBefore, ebp
        mov edi, 0x5E55105E
        mov ebx, 0x0B0B0B0B
        mov esi, 0x05050505
        mov ecx, 0x0AD0E0DE
        mov g_espBefore, esp
        push 0x4057
        push 0
        call FakeBeginSoloTask
        mov g_espAfter, esp
        mov g_ebxAfter, ebx
        mov g_esiAfter, esi
        mov g_ediAfter, edi
        mov g_ebpAfter, ebp
        pop edi
        pop esi
        pop ebx
        pop ebp
        ret
    }
}

// The game's host branch: EDI = ECX = session, push 0, call CreateSaveGameTask.
__declspec(naked) void HostHarness() {
    __asm {
        push ebp
        push ebx
        push esi
        push edi
        mov g_ebpBefore, ebp
        mov edi, 0x5E55105E
        mov ebx, 0x0B0B0B0B
        mov esi, 0x05050505
        mov ecx, edi
        mov g_espBefore, esp
        push 0
        call FakeCreateSaveGameTask
        mov g_espAfter, esp
        mov g_ebxAfter, ebx
        mov g_esiAfter, esi
        mov g_ediAfter, edi
        mov g_ebpAfter, ebp
        pop edi
        pop esi
        pop ebx
        pop ebp
        ret
    }
}

// The address of the first `call rel32` inside `fn` that lands on `target` (directly or through an incremental-link
// jump thunk), or 0.
uintptr_t FindCall(void* fn, void* target, uintptr_t* landsOn) {
    auto p = reinterpret_cast<uintptr_t>(fn);
    for (int i = 0; i < 128; ++i, ++p) {
        uintptr_t t;
        if (!hook::ReadCall(p, &t)) continue;
        uintptr_t real = t;
        if (*reinterpret_cast<unsigned char*>(t) == 0xE9) real = t + 5 + *reinterpret_cast<int32_t*>(t + 1);
        if (real == reinterpret_cast<uintptr_t>(target) || t == reinterpret_cast<uintptr_t>(target)) {
            *landsOn = t;
            return p;
        }
    }
    return 0;
}

void ResetSeen() {
    g_soloSelf = g_soloArg1 = g_soloArg2 = g_createSelf = g_createArg = g_clientSession = g_hostSession = 0;
    g_order = g_soloAt = g_createAt = g_clientAt = g_hostAt = 0;
}

void TestMatches() {
    const unsigned char code[] = {0xB8, 0x45, 0x07, 0x83, 0x00, 0xE8, 0xFD, 0xE8};
    const auto a = reinterpret_cast<uintptr_t>(code);
    Check(hook::Matches(a, "b8 ?? ?? ?? ?? e8 fd e8"), "Matches: exact bytes with wildcards");
    Check(hook::Matches(a, "B8 45 07 83 00"), "Matches: upper-case hex");
    Check(!hook::Matches(a, "b8 ?? ?? ?? ?? e8 fd e9"), "Matches: one byte differs");
    Check(!hook::Matches(a, "b8 4"), "Matches: odd pattern length is refused");
    Check(!hook::Matches(0x10, "00"), "Matches: unreadable memory does not match");
    const char* const unknown[] = {"no such signature"};
    Check(!hook::SignaturesMatch(a, unknown, 1), "SignaturesMatch: an unknown name fails");
}

void TestCopyName() {
    Check(savecopy::CopyName(L"Save/MP Autosave A.RSG") == L"Save/MP Autosave A (copy).RSG", "CopyName: autosave");
    Check(savecopy::CopyName(L"Save/x.rsg") == L"Save/x (copy).rsg", "CopyName: keeps the extension's case");
    Check(savecopy::CopyName(L"Save/../../evil.RSG").empty(), "CopyName: refuses ..");
    Check(savecopy::CopyName(L"C:/x.RSG").empty(), "CopyName: refuses a drive");
    Check(savecopy::CopyName(L"/x.RSG").empty(), "CopyName: refuses a rooted path");
    Check(savecopy::CopyName(L"rmc_temperate03").empty(), "CopyName: refuses a map name");
    Check(savecopy::CopyName(L"").empty(), "CopyName: refuses empty");
}

void TestLobbyChecksum() {
    const unsigned char three[] = {1, 2, 3};
    Check(k2mp::LobbyChecksum(three, 3) == 66, "LobbyChecksum: ((1*7)+2)*7+3 = 66");
    Check(k2mp::LobbyChecksum(three, 3) == k2mp::LobbyChecksum(three + 1, 2, k2mp::LobbyChecksum(three, 1)),
          "LobbyChecksum: chunked equals whole");
    unsigned char ff[16];
    memset(ff, 0xFF, sizeof(ff));
    uint64_t h = 0;
    for (int i = 0; i < 16; ++i) h = (h * 7 + 0xFF) & 0xFFFFFFFFu;
    Check(k2mp::LobbyChecksum(ff, sizeof(ff)) == static_cast<uint32_t>(h), "LobbyChecksum: wraps at 32 bits, bytes unsigned");
}

void TestUserDataFallback() {
    // No game here (exe base 0): the game's value cannot be read, so it falls back to Documents\Kohan2\data.
    const std::wstring d = k2mp::UserDataDir();
    const std::wstring tail = L"\\Kohan2\\data";
    Check(d.size() > tail.size() && d.compare(d.size() - tail.size(), tail.size(), tail) == 0 && d.find(L'/') == std::wstring::npos,
          "UserDataDir: falls back to Documents\\Kohan2\\data with backslashes");
}

void ExpectRegisters(const char* which) {
    char what[160];
    sprintf_s(what, "%s: ESP balanced after the call", which);
    Check(g_espAfter == g_espBefore, what);
    sprintf_s(what, "%s: EBX, ESI, EDI, EBP preserved", which);
    Check(g_ebxAfter == 0x0B0B0B0B && g_esiAfter == 0x05050505 && g_ediAfter == 0x5E55105E && g_ebpAfter == g_ebpBefore,
          what);
}

void TestClientStub() {
    uintptr_t landsOn = 0;
    const uintptr_t site = FindCall(reinterpret_cast<void*>(&ClientHarness), reinterpret_cast<void*>(&FakeBeginSoloTask), &landsOn);
    Check(site != 0, "client: found the harness's call");
    if (!site) return;
    savecopy::testing::Route(reinterpret_cast<uintptr_t>(&FakeBeginSoloTask), 0, OnClient, OnHost);
    Check(!hook::RedirectCall(site, landsOn + 1, savecopy::testing::ClientStub()),
          "client: RedirectCall refuses when the call goes elsewhere");
    Check(hook::RedirectCall(site, landsOn, savecopy::testing::ClientStub()), "client: RedirectCall patches the call");
    ResetSeen();
    ClientHarness();
    Check(g_soloSelf == 0x0AD0E0DE && g_soloArg1 == 0 && g_soloArg2 == 0x4057,
          "client: the original still gets ECX and both arguments in order");
    Check(g_clientSession == 0x5E55105E, "client: our callback gets the session from EDI");
    Check(g_soloAt == 1 && g_clientAt == 2, "client: the original runs first, then our callback");
    ExpectRegisters("client");
    Check(hook::RedirectCall(site, reinterpret_cast<uintptr_t>(savecopy::testing::ClientStub()), reinterpret_cast<void*>(landsOn)),
          "client: the patch can be undone");
    ResetSeen();
    ClientHarness();
    Check(g_soloAt == 1 && g_clientAt == 0, "client: after undoing, only the original runs");
}

void TestHostStub() {
    uintptr_t landsOn = 0;
    const uintptr_t site =
        FindCall(reinterpret_cast<void*>(&HostHarness), reinterpret_cast<void*>(&FakeCreateSaveGameTask), &landsOn);
    Check(site != 0, "host: found the harness's call");
    if (!site) return;
    savecopy::testing::Route(0, reinterpret_cast<uintptr_t>(&FakeCreateSaveGameTask), OnClient, OnHost);
    Check(hook::RedirectCall(site, landsOn, savecopy::testing::HostStub()), "host: RedirectCall patches the call");
    ResetSeen();
    HostHarness();
    Check(g_createSelf == 0x5E55105E && g_createArg == 0, "host: the original still gets ECX and its argument");
    Check(g_hostSession == 0x5E55105E, "host: our callback gets the session from EDI");
    Check(g_createAt == 1 && g_hostAt == 2, "host: the original runs first, then our callback");
    ExpectRegisters("host");
}


// fast transfers: the decision on fake game objects, and the hook as the game calls it (fastcall, ECX = connection).
alignas(16) uint8_t g_admin[0x400], g_conn[0x500], g_net[0x100], g_node[0x100];
void* g_adminPtr = g_admin;
void* g_nodePtr = g_node;
int g_fakeType = 0;
void* volatile g_fakeSaw = nullptr;
int __fastcall FakeWantedType(void* conn) {
    g_fakeSaw = conn;
    return g_fakeType;
}

template <typename T>
void Put(uint8_t* object, uint32_t offset, T value) {
    memcpy(object + offset, &value, sizeof(T));
}

void ResetFastSendObjects() {
    memset(g_admin, 0, sizeof(g_admin));
    memset(g_conn, 0, sizeof(g_conn));
    memset(g_net, 0, sizeof(g_net));
    memset(g_node, 0, sizeof(g_node));
    Put<void*>(g_admin, game::kAdminServer, g_admin);  // this PC hosts
    g_conn[game::kConnUp] = 1;
    Put<int>(g_conn, game::kConnFileState, game::kFileStateSending);
    Put<void*>(g_conn, game::kConnNetConn, g_net);
    Put<float>(g_net, game::kNetConnLastSend, 10.0f);
    Put<float>(g_node, game::kNetNodeClock, 10.05f);
}

void TestFastSend() {
    ResetFastSendObjects();
    Check(fastsend::Eligible(g_admin, g_conn), "fast: the host sending a player a file");
    Put<int>(g_conn, game::kConnFileState, 1);
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: nothing extra once the file is sent");
    ResetFastSendObjects();
    Put<void*>(g_admin, game::kAdminServer, g_node);
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: never on a PC that is not the host");
    ResetFastSendObjects();
    g_admin[game::kAdminBackupMode] = 1;
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: never in backup mode");
    ResetFastSendObjects();
    g_admin[game::kAdminFlag2ec] = 1;
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: never while the admin node's 0x2ec is set");
    ResetFastSendObjects();
    g_conn[game::kConnUp] = 0;
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: never to a connection that is not up");
    ResetFastSendObjects();
    g_conn[game::kConnFlag99] = 1;
    Check(!fastsend::Eligible(g_admin, g_conn), "fast: never when the game sends packet type 11");

    // the bucket: 60 per second, at most 6 banked
    fastsend::Bucket b{1.0f, 10.0f};
    Check(fastsend::Take(b, 10.0f, 60, false) && fastsend::Take(b, 10.0f, 60, false), "bucket: looking takes nothing");
    Check(fastsend::Take(b, 10.0f, 60, true) && !fastsend::Take(b, 10.0f, 60, true), "bucket: one token, one packet");
    int sent = 0;
    for (int i = 0; i < 10; ++i) sent += fastsend::Take(b, 11.0f, 60, true);
    Check(sent == 6, "bucket: a second's wait banks no more than 0.1 s worth (6)");
    sent = 0;
    for (int frame = 1; frame <= 25; ++frame)  // one second of a sender that runs 25 times a second
        while (fastsend::Take(b, 11.0f + frame * 0.04f, 60, true)) ++sent;
    Check(sent >= 59 && sent <= 61, "bucket: 60 packets in a second even when the sender runs 25 times a second");
    fastsend::Bucket back{0.5f, 20.0f};
    Check(!fastsend::Take(back, 19.0f, 60, true) && back.tokens == 0.5f, "bucket: a clock going back adds nothing");

    using Fn = int(__fastcall*)(void*);
    const auto hookFn = reinterpret_cast<Fn>(fastsend::testing::Hook());
    fastsend::testing::Route(reinterpret_cast<uintptr_t>(&FakeWantedType), reinterpret_cast<uintptr_t>(&g_adminPtr),
                             reinterpret_cast<uintptr_t>(&g_nodePtr), 60);
    ResetFastSendObjects();
    g_fakeType = 0;
    Check(hookFn(g_conn) == game::kPacketTypeStreams && g_fakeSaw == g_conn,
          "fast hook: the game's \"nothing yet\" becomes packet type 5, and the original saw the connection");
    Check(hookFn(g_conn) == 0, "fast hook: then the bucket is empty until the clock moves");
    Put<float>(g_node, game::kNetNodeClock, 10.1f);
    g_fakeType = 3;
    Check(hookFn(g_conn) == 3, "fast hook: any other answer of the game stands");
    g_fakeType = 0;
    Check(hookFn(g_conn) == game::kPacketTypeStreams, "fast hook: a later clock refills the bucket");
    Check(hookFn(reinterpret_cast<void*>(0x10)) == 0, "fast hook: an unreadable connection leaves the game's answer");
    Put<int>(g_conn, game::kConnFileState, 1);
    Put<float>(g_node, game::kNetNodeClock, 10.5f);
    Check(hookFn(g_conn) == 0, "fast hook: the game's pace again once the file is sent");
    g_adminPtr = nullptr;
    ResetFastSendObjects();
    Check(hookFn(g_conn) == 0, "fast hook: nothing before the admin node exists");
    g_adminPtr = g_admin;
}


// autosave: when it may save (host, not backup mode, a match running, another PC connected).
void TestAutosaveState() {
    alignas(16) static uint8_t admin[0x400], session[0x200], node[0x100];
    static void* listNode[2] = {nullptr, nullptr};
    auto reset = [] {
        memset(admin, 0, sizeof(admin));
        memset(session, 0, sizeof(session));
        memset(node, 0, sizeof(node));
        Put<void*>(admin, game::kAdminServer, admin);
        Put<int>(session, game::kSessionState, 2);
        Put<void*>(node, game::kNetNodeConnections, listNode);
    };
    reset();
    Check(autosave::HostingMatch(admin, session, node), "autosave: host, match running, a player connected");
    Put<int>(session, game::kSessionState, 4);
    Check(autosave::HostingMatch(admin, session, node), "autosave: session state 4 counts as a match too");
    Put<int>(session, game::kSessionState, 1);
    Check(!autosave::HostingMatch(admin, session, node), "autosave: not in any other session state (1)");
    reset();
    Put<void*>(admin, game::kAdminServer, node);
    Check(!autosave::HostingMatch(admin, session, node), "autosave: never on a PC that is not the host");
    reset();
    admin[game::kAdminBackupMode] = 1;
    Check(!autosave::HostingMatch(admin, session, node), "autosave: never in backup mode");
    reset();
    Put<void*>(node, game::kNetNodeConnections, nullptr);
    Check(!autosave::HostingMatch(admin, session, node), "autosave: not with nobody else connected (single player)");
    Check(!autosave::HostingMatch(nullptr, session, node) && !autosave::HostingMatch(admin, nullptr, node),
          "autosave: nothing before the game's objects exist");
}


// pause on drop: the hook passes the leave on unchanged, then notes it; HostOfRunningMatch allows backup mode.
volatile int g_leaveCalls = 0;
void* volatile g_leaveAdmin = nullptr;
void* volatile g_leavePlayer = nullptr;
volatile int g_leaveReason = 0;
void __fastcall FakePlayerLeave(void* admin, void*, void* player, int reason) {
    ++g_leaveCalls;
    g_leaveAdmin = admin;
    g_leavePlayer = player;
    g_leaveReason = reason;
}

volatile int g_quitCalls = 0;
void* volatile g_quitConn = nullptr;
void* volatile g_quitStream = nullptr;
volatile int g_quitSequence = 0;
void __fastcall FakeDisconnectRequest(void* conn, void*, void* stream, int sequence) {
    ++g_quitCalls;
    g_quitConn = conn;
    g_quitStream = stream;
    g_quitSequence = sequence;
}

void TestDropGuard() {
    using Fn = void(__fastcall*)(void*, void*, void*, int);
    const auto hookFn = reinterpret_cast<Fn>(dropguard::testing::Hook());
    dropguard::testing::Route(reinterpret_cast<uintptr_t>(&FakePlayerLeave));
    int admin = 0, player = 0;
    hookFn(&admin, nullptr, &player, 7);
    Check(g_leaveCalls == 1 && g_leaveAdmin == &admin && g_leavePlayer == &player && g_leaveReason == 7,
          "drop: the game's PlayerLeave gets the same admin node, player and reason");
    Check(dropguard::testing::Pending() == 1, "drop: a real player's leave is noted for the tick");
    hookFn(&admin, nullptr, nullptr, 0);
    Check(g_leaveCalls == 2 && dropguard::testing::Pending() == 1, "drop: a leave with no player is passed on, not noted");

    alignas(16) static uint8_t a[0x400], session[0x200];
    memset(a, 0, sizeof(a));
    memset(session, 0, sizeof(session));
    Put<void*>(a, game::kAdminServer, a);
    Put<int>(session, game::kSessionState, 2);
    Check(dropguard::HostOfRunningMatch(a, session), "drop: host of a running match");
    a[game::kAdminBackupMode] = 1;
    Check(dropguard::HostOfRunningMatch(a, session), "drop: also the player who took over from a lost host (backup mode)");
    Put<int>(session, game::kSessionState, 1);
    Check(!dropguard::HostOfRunningMatch(a, session), "drop: nothing for a leave from the lobby");
    Put<int>(session, game::kSessionState, 4);
    Put<void*>(a, game::kAdminServer, session);
    Check(!dropguard::HostOfRunningMatch(a, session), "drop: never on a PC that is not the host");

    // the lobby step's End Mission: original host, running networked match, no countdown, multiplayer game type
    int world = 0;
    auto lobbyReady = [&] {
        memset(a, 0, sizeof(a));
        memset(session, 0, sizeof(session));
        Put<void*>(a, game::kAdminServer, a);
        Put<int>(session, game::kSessionState, 2);
        session[game::kSessionNetworked] = 1;
    };
    lobbyReady();
    Check(dropguard::CanEndMission(a, session, &world, game::kGameTypeMultiplayer), "lobby: the host of a running multiplayer match");
    Check(!dropguard::CanEndMission(a, session, &world, 0), "lobby: never in a campaign (game type 0)");
    Check(!dropguard::CanEndMission(a, session, nullptr, game::kGameTypeMultiplayer), "lobby: never without a world");
    a[game::kAdminBackupMode] = 1;
    Check(!dropguard::CanEndMission(a, session, &world, game::kGameTypeMultiplayer), "lobby: never from the player who took over (backup mode)");
    lobbyReady();
    session[game::kSessionNetworked] = 0;
    Check(!dropguard::CanEndMission(a, session, &world, game::kGameTypeMultiplayer), "lobby: never in a session that is not networked");
    lobbyReady();
    Put<int>(session, game::kSessionCountdown, 3);
    Check(!dropguard::CanEndMission(a, session, &world, game::kGameTypeMultiplayer), "lobby: never during a launch countdown");
    lobbyReady();
    Put<int>(session, game::kSessionState, 4);
    Check(!dropguard::CanEndMission(a, session, &world, game::kGameTypeMultiplayer), "lobby: never while a film plays (state 4)");

    // the vote: a connection of the net node whose admin connection is down (timed out)
    alignas(16) static uint8_t node[0x100], net1[0x100], net2[0x100], conn1[0x100], conn2[0x100];
    void* list2[2] = {net2, nullptr};
    void* list1[2] = {net1, list2};
    memset(node, 0, sizeof(node));
    memset(net1, 0, sizeof(net1));
    memset(net2, 0, sizeof(net2));
    memset(conn1, 0, sizeof(conn1));
    memset(conn2, 0, sizeof(conn2));
    Put<void*>(node, game::kNetNodeConnections, list1);
    Put<void*>(net1, game::kNetConnAdminConn, conn1);
    Put<void*>(net2, game::kNetConnAdminConn, conn2);
    conn1[game::kConnUp] = 1;
    conn2[game::kConnUp] = 1;
    Check(!dropguard::ConnectionDown(node), "vote: every connection up");
    conn2[game::kConnUp] = 0;
    Check(dropguard::ConnectionDown(node), "vote: the second of two connections timed out");
    Put<void*>(net2, game::kNetConnAdminConn, nullptr);
    Check(!dropguard::ConnectionDown(node), "vote: a net connection without an admin connection does not count");
    Put<void*>(node, game::kNetNodeConnections, nullptr);
    Check(!dropguard::ConnectionDown(node) && !dropguard::ConnectionDown(nullptr), "vote: no connections, no node");

    // quitting on purpose: the disconnect request passes through unchanged and marks the next leave as a quit
    using QuitFn = void(__fastcall*)(void*, void*, void*, int);
    const auto quitHook = reinterpret_cast<QuitFn>(dropguard::testing::DisconnectRequestHook());
    dropguard::testing::RouteDisconnectRequest(reinterpret_cast<uintptr_t>(&FakeDisconnectRequest));
    Check(!dropguard::testing::LeftOnPurposeNow(), "quit: no request seen, a leave is a drop");
    int conn = 0, stream = 0;
    quitHook(&conn, nullptr, &stream, 42);
    Check(g_quitCalls == 1 && g_quitConn == &conn && g_quitStream == &stream && g_quitSequence == 42,
          "quit: the game's SerializeClientDisconnectRequest gets the same connection, stream and sequence");
    Check(dropguard::testing::LeftOnPurposeNow(), "quit: a leave right after the request is a quit");
}

}  // namespace

int main() {
    TestMatches();
    TestCopyName();
    TestLobbyChecksum();
    TestUserDataFallback();
    TestClientStub();
    TestHostStub();
    TestFastSend();
    TestAutosaveState();
    TestDropGuard();
    std::printf("%d failure(s)\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
