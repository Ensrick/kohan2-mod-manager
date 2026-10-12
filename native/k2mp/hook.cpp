#include "hook.h"

#include <windows.h>
#include <cstring>

namespace hook {

namespace {

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

}  // namespace

bool Matches(uintptr_t addr, const char* pattern) {
    size_t n = 0;
    for (const char* p = pattern; *p; ++p)
        if (*p != ' ') ++n;
    if (n % 2 || !Readable(addr, n / 2)) return false;
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

bool SignaturesMatch(uintptr_t exeBase, const char* const* names, int count) {
    for (int i = 0; i < count; ++i) {
        bool found = false;
        for (const game::Signature& s : game::kSignatures) {
            if (strcmp(s.name, names[i]) != 0) continue;
            found = true;
            if (!Matches(exeBase + s.rva, s.bytes)) return false;
        }
        if (!found) return false;
    }
    return true;
}

bool ReadCall(uintptr_t site, uintptr_t* target) {
    if (!Readable(site, 5) || *reinterpret_cast<const unsigned char*>(site) != 0xE8) return false;
    int32_t rel;
    memcpy(&rel, reinterpret_cast<const void*>(site + 1), 4);
    *target = site + 5 + rel;
    return true;
}

bool RedirectCall(uintptr_t site, uintptr_t expected, void* stub) {
    uintptr_t current;
    if (!ReadCall(site, &current) || current != expected) return false;
    const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(stub) - (site + 5));
    DWORD old;
    if (!VirtualProtect(reinterpret_cast<void*>(site + 1), 4, PAGE_EXECUTE_READWRITE, &old)) return false;
    memcpy(reinterpret_cast<void*>(site + 1), &rel, 4);
    DWORD ignored;
    VirtualProtect(reinterpret_cast<void*>(site + 1), 4, old, &ignored);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(site), 5);
    return true;
}

}  // namespace hook
