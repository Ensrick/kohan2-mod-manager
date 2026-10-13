// Multiplayer Fixes (k2mp.dll), a Kohan II plugin. docs/MULTIPLAYER.md has the design and the evidence behind it.
//
// Features (each needs its own signatures to match, can be turned off in Mods\k2mp.ini [features], and stays off
// on an unknown k2.exe):
//   save_copies     every player's game keeps a copy of each multiplayer save (savecopy.h)
//   fast_transfers  the host sends saved games to players several times faster (fastsend.h)
//   ignore_asserts  the game's assertion dialogs no longer stop it: a failed check is logged, the game goes on
//   autosave        the host saves a multiplayer match every few minutes (autosave.h; off unless set)
//   pause_on_drop   when a player leaves a running match, the host saves "MP Resume" and pauses (dropguard.h)
#include <windows.h>
#include <cstdio>
#include <cstring>

#include "autosave.h"
#include "dropguard.h"
#include "fastsend.h"
#include "game.h"
#include "hook.h"
#include "k2plugin.h"
#include "plugin.h"
#include "rig.h"
#include "savecopy.h"
#include "script.h"
#include "transfer.h"

namespace {

// ignore_asserts: the game's own IgnoreAsserts command, run once the console exists. A failed assertion otherwise
// shows an Abort/Retry/Ignore dialog on whichever thread hit it; when that thread holds a lock the main thread needs,
// the main thread gives up waiting and the game crashes ("timeout waiting for mutex"). Seen on the test rig when the
// host left a player behind in the lobby: all 4 crashes in 18 such runs had exactly one ASSERT, none of the clean runs
// had one. Ignore is what a player would click.
bool g_ignoreAssertsPending = false;

}  // namespace

// Checks every signature and call site against the image at `base`. Returns how many do not match (0 = the build
// this plugin knows) and writes one line per check into `report` when given. Exported for tests: test/sigtest.cpp
// maps a decrypted copy of k2.exe and calls this.
extern "C" __declspec(dllexport) int __cdecl K2Mp_CheckBuild(uintptr_t base, char* report, int reportSize) {
    int bad = 0, used = 0;
    auto line = [&](bool ok, const char* name, uint32_t rva) {
        if (!ok) ++bad;
        if (report && used < reportSize) {
            const int n = _snprintf_s(report + used, reportSize - used, _TRUNCATE, "%s %s at +0x%x\n",
                                      ok ? "ok " : "BAD", name, rva);
            used = n < 0 ? reportSize : used + n;
        }
    };
    for (const game::Signature& s : game::kSignatures) line(hook::Matches(base + s.rva, s.bytes), s.name, s.rva);
    for (const game::CallSite& c : game::kCallSites) {
        uintptr_t target = 0;
        line(hook::ReadCall(base + c.site, &target) && target == base + c.target, c.name, c.site);
    }
    return bad;
}

extern "C" __declspec(dllexport) int __cdecl K2Plugin_Start(const K2Host* host) {
    if (!host || host->api != K2_PLUGIN_API) return 1;
    k2mp::g.host = host;
    k2mp::g.exeBase = host->exeBase;
    k2mp::g.gameDir = host->gameDir;
    k2mp::g.modsDir = host->modsDir;
    k2mp::Log("Multiplayer Fixes %s", K2MP_VERSION);

    char report[4096] = {};
    const int bad = K2Mp_CheckBuild(host->exeBase, report, sizeof(report));
    if (bad) {
        for (char* line = report; *line;) {
            char* end = strchr(line, '\n');
            if (end) *end = 0;
            if (strncmp(line, "BAD", 3) == 0) k2mp::Log("%s", line);
            if (!end) break;
            line = end + 1;
        }
        k2mp::Log("%d place(s) in k2.exe differ from the build this plugin knows (1.3.68): every feature that needs "
                  "them stays off",
                  bad);
    }

    int on = 0;
    if (!k2mp::Setting(L"save_copies", true))
        k2mp::Log("save copies: off in Mods\\k2mp.ini");
    else if (savecopy::Install(host->exeBase)) {
        k2mp::Log("save copies: on (this PC writes \"<save> (copy).RSG\" whenever the host saves)");
        ++on;
    } else
        k2mp::Log("save copies: not available for this k2.exe");

    if (!k2mp::Setting(L"fast_transfers", true))
        k2mp::Log("fast transfers: off in Mods\\k2mp.ini");
    else if (fastsend::Install(host->exeBase)) {
        k2mp::Log("fast transfers: on (as host, files go out at up to %d packets/s instead of 10)",
                  fastsend::PacketsPerSecond());
        ++on;
    } else
        k2mp::Log("fast transfers: not available for this k2.exe");

    const bool scripts = script::Install(host->exeBase);
    if (!k2mp::Setting(L"ignore_asserts", true))
        k2mp::Log("ignore asserts: off in Mods\\k2mp.ini");
    else if (scripts) {
        g_ignoreAssertsPending = true;
        k2mp::Log("ignore asserts: on (the game's IgnoreAsserts command, run once the game's console exists)");
        ++on;
    } else
        k2mp::Log("ignore asserts: not available for this k2.exe");

    if (scripts && autosave::Install(host->exeBase)) ++on;

    if (!k2mp::Setting(L"pause_on_drop", true))
        k2mp::Log("pause on drop: off in Mods\\k2mp.ini");
    else if (scripts && dropguard::Install(host->exeBase)) {
        k2mp::Log("pause on drop: on (when a player leaves a running match, the host saves \"MP Resume\" and pauses)");
        ++on;
    } else
        k2mp::Log("pause on drop: not available for this k2.exe");

    if (transfer::Install(host->exeBase)) {  // experimental: only with [transfer] keys in Mods\k2mp.ini
        k2mp::Log("transfer settings: configured (applied on the game's thread once its settings table is built)");
        ++on;
    }
    rig::Install(host->exeBase);  // test rig only: does nothing unless K2MP_RIG_JOIN is set
    k2mp::Log("%d feature(s) on", on);
    return on > 0 ? 0 : 1;
}

// Runs on the game's own thread (see k2plugin.h): every change to game state that is not made inside a hook goes here.
extern "C" __declspec(dllexport) void __cdecl K2Plugin_Tick() {
    if (g_ignoreAssertsPending && script::Run(L"IgnoreAsserts")) {
        g_ignoreAssertsPending = false;
        k2mp::Log("ignore asserts: IgnoreAsserts queued");
    }
    autosave::Tick();
    dropguard::Tick();
    transfer::Tick();
}

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
