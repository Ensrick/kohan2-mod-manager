// Multiplayer Fixes (k2mp.dll), a Kohan II plugin. docs/MULTIPLAYER.md has the design and the evidence behind it.
//
// Features (each needs its own signatures to match, can be turned off in Mods\k2mp.ini [features], and stays off
// on an unknown k2.exe):
//   save_copies   every player's game keeps a copy of each multiplayer save (savecopy.h)
#include <windows.h>
#include <cstdio>
#include <cstring>

#include "game.h"
#include "hook.h"
#include "k2plugin.h"
#include "plugin.h"
#include "rig.h"
#include "savecopy.h"
#include "transfer.h"


namespace {


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

    if (transfer::Install(host->exeBase)) {  // experimental: only with [transfer] keys in Mods\k2mp.ini
        k2mp::Log("transfer settings: configured (applied on the game's thread once its settings table is built)");
        ++on;
    }
    rig::Install(host->exeBase);  // test rig only: does nothing unless K2MP_RIG_JOIN is set
    k2mp::Log("%d feature(s) on", on);
    return on > 0 ? 0 : 1;
}

// Runs on the game's own thread (see k2plugin.h): every change to game state that is not made inside a hook goes here.
extern "C" __declspec(dllexport) void __cdecl K2Plugin_Tick() { transfer::Tick(); }

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
