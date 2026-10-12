#include "transfer.h"

#include <windows.h>
#include <cstring>
#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"

namespace transfer {

namespace {

struct Override {
    const wchar_t* key;
    int index;  // entry in the AVars table
    bool isFloat;  // the var holds a float (written from the whole number in the ini)
    int min, max;
    int value;  // -1 = keep the game's
};

Override g_settings[] = {
    {L"blocks_per_frame", game::kVarBlocksPerFrame, false, 1, 256, -1},
    {L"block_size", game::kVarBlockSize, false, 16, 4096, -1},
    {L"max_pending_blocks", game::kVarMaxPendingBlocks, false, 1, 256, -1},
    {L"admin_ticks_per_second", game::kVarAdminTicksPerSecond, true, 1, 240, -1},
    {L"async_ticks_per_second", game::kVarAdminAsyncTicksPerSecond, true, 1, 240, -1},
};

// The value as stored in the var: the int itself, or the float's bits.
int Stored(const Override& o) {
    if (!o.isFloat) return o.value;
    const float f = static_cast<float>(o.value);
    int bits;
    memcpy(&bits, &f, 4);
    return bits;
}

double Shown(const Override& o, int stored) {
    if (!o.isFloat) return stored;
    float f;
    memcpy(&f, &stored, 4);
    return f;
}
uintptr_t g_tableGlobal = 0;  // address of the AVars table pointer; 0 = nothing to do (off, applied or given up)
int g_ticks = 0;

const char* const kNeeds[] = {"file sender block count", "file sender fill"};

int ReadSetting(const wchar_t* key) {
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    return static_cast<int>(GetPrivateProfileIntW(L"transfer", key, -1, ini.c_str()));
}

// The var object of AVars entry `index`, or null while the table is not built yet. SEH: reading game memory.
int* VarObject(uintptr_t tableGlobal, int index) {
    __try {
        auto* const* table = *reinterpret_cast<int* const* const*>(tableGlobal);
        return table ? table[index] : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

}  // namespace

bool Install(uintptr_t exeBase) {
    int configured = 0;
    for (Override& o : g_settings) {
        const int v = ReadSetting(o.key);
        if (v < 0) continue;
        if (v < o.min || v > o.max) {
            k2mp::Log("transfer: %s = %d is outside %d..%d; ignored", k2mp::Utf8(o.key).c_str(), v, o.min, o.max);
            continue;
        }
        o.value = v;
        ++configured;
    }
    if (!configured) return false;
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) {
        k2mp::Log("transfer: the file transfer code differs in this k2.exe; settings not applied");
        return false;
    }
    // `mov eax, [table]` (A1 imm32): the operand is the table pointer's address, already relocated in memory.
    g_tableGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kFileSenderBlockCount + 1);
    return true;
}

// On the game's thread (K2Plugin_Tick), so nothing else touches the table meanwhile. The table is built during
// start-up and the game may load settings into it more than once, so every tick puts the overrides back if anything
// changed them (a compare per tick; a log line only when a value actually changes).
void Tick() {
    if (!g_tableGlobal) return;
    bool any = false;
    for (const Override& o : g_settings) {
        if (o.value < 0) continue;
        int* var = VarObject(g_tableGlobal, o.index);
        if (!var) continue;
        any = true;
        const int want = Stored(o);
        if (*var == want) continue;
        k2mp::Log("transfer: %s %g -> %d", k2mp::Utf8(o.key).c_str(), Shown(o, *var), o.value);
        *var = want;
    }
    if (!any && ++g_ticks > 20 * 60 * 5) {  // five minutes of ticks without the table: give up
        k2mp::Log("transfer: the game's settings table never appeared; nothing changed");
        g_tableGlobal = 0;
    }
}

}  // namespace transfer
