// Multiplayer Fixes (k2mp.dll), a Kohan II plugin.
//
// Goal: players who drop out of a multiplayer match can rejoin it, the match pauses while they are gone, and nobody
// passes saves around by hand. This first version only checks that it recognises the running k2.exe at the places it
// will patch, and logs the result to Mods\plugins.log. It changes nothing in the game yet.
//
// Addresses are RVAs into k2.exe 1.3.68 (Steam, Dev Build 4345.17); the exe is relocated, so address = base + RVA.
// Each signature skips (??) the bytes that relocation changes: absolute addresses inside the instructions.
#include <windows.h>
#include <cstdint>
#include <cstdio>

#include "k2plugin.h"

namespace {

struct Site {
    const char* name;
    uint32_t rva;
    const char* bytes;  // hex pairs, "??" = any byte
};

// clang-format off
const Site kSites[] = {
    // Applies a GameSave order: the host writes the save, other players pause until it is done.
    {"KKC_Session::GameSave", 0x1624d5,
     "b8 ?? ?? ?? ?? e8 fd e8 27 00 51 51 53 56 57 8b f9 8b 0d ?? ?? ?? ?? e8"},
    // The host's answer to a joining player; refuses with "Game is already in progress" once a match has started.
    {"TGC_AdminConn::SerializeConnect", 0x1506dd,
     "b8 ?? ?? ?? ?? e8 f5 06 29 00 81 ec 8c 00 00 00 a1 ?? ?? ?? ?? 53 56 8b"},
};
// clang-format on

bool Readable(uintptr_t addr, size_t n) {
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(reinterpret_cast<void*>(addr), &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    return addr + n <= reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
}

int Hex(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

bool Matches(uintptr_t addr, const char* pattern) {
    size_t n = 0;
    for (const char* p = pattern; *p; ++p)
        if (*p != ' ') ++n;
    if (!Readable(addr, n / 2)) return false;
    const auto* b = reinterpret_cast<const unsigned char*>(addr);
    for (const char* p = pattern; *p;) {
        if (*p == ' ') {
            ++p;
            continue;
        }
        if (p[0] == '?' && p[1] == '?') {
            p += 2;
            ++b;
            continue;
        }
        const int hi = Hex(p[0]), lo = Hex(p[1]);
        if (hi < 0 || lo < 0 || *b != (hi << 4 | lo)) return false;
        p += 2;
        ++b;
    }
    return true;
}

}  // namespace

// Checks every site against the image at `base`. Returns the number of sites that do not match (0 = known build) and
// writes one line per site into `report` when given. Exported for tests (test/sigtest.cpp maps a decrypted k2.exe).
extern "C" __declspec(dllexport) int __cdecl K2Mp_CheckBuild(uintptr_t base, char* report, int reportSize) {
    int bad = 0;
    int used = 0;
    for (const Site& s : kSites) {
        const bool ok = Matches(base + s.rva, s.bytes);
        if (!ok) ++bad;
        if (report && used < reportSize) {
            const int n = _snprintf_s(report + used, reportSize - used, _TRUNCATE, "%s %s at +0x%x\n", ok ? "ok " : "BAD",
                                      s.name, s.rva);
            used = n < 0 ? reportSize : used + n;
        }
    }
    return bad;
}

extern "C" __declspec(dllexport) int __cdecl K2Plugin_Start(const K2Host* host) {
    if (!host || host->api != K2_PLUGIN_API) return 1;
    host->log("k2mp", "Multiplayer Fixes %s", K2MP_VERSION);
    char report[512] = {};
    const int bad = K2Mp_CheckBuild(host->exeBase, report, sizeof(report));
    for (char* line = report; *line;) {
        char* end = strchr(line, '\n');
        if (end) *end = 0;
        host->log("k2mp", "%s", line);
        if (!end) break;
        line = end + 1;
    }
    if (bad) {
        host->log("k2mp", "this k2.exe is not the build this plugin knows (1.3.68): staying off");
        return 1;
    }
    host->log("k2mp", "recognised k2.exe 1.3.68; nothing is changed in the game yet");
    return 0;
}

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
