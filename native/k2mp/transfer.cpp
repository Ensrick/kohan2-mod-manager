#include "transfer.h"

#include <windows.h>
#include <cstring>
#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"

namespace transfer {

namespace {

enum Table { kTransferTable, kNetTable };

struct Override {
    const wchar_t* key;
    Table table;
    int index;     // entry in that table
    bool isFloat;  // the var holds a float (written from the whole number in the ini)
    int min, max;
    int value;  // -1 = keep the game's
};

Override g_settings[] = {
    {L"blocks_per_frame", kTransferTable, game::kVarBlocksPerFrame, false, 1, 256, -1},
    {L"block_size", kTransferTable, game::kVarBlockSize, false, 16, 4096, -1},
    {L"max_pending_blocks", kTransferTable, game::kVarMaxPendingBlocks, false, 1, 256, -1},
    {L"admin_ticks_per_second", kTransferTable, game::kVarAdminTicksPerSecond, true, 1, 240, -1},
    {L"async_ticks_per_second", kTransferTable, game::kVarAdminAsyncTicksPerSecond, true, 1, 240, -1},
    {L"lan_bandwidth", kNetTable, game::kVarLanBandwidth, true, 1000, 10000000, -1},
    {L"unknown_bandwidth", kNetTable, game::kVarUnknownBandwidth, true, 1000, 10000000, -1},
    {L"steam_bandwidth", kNetTable, game::kVarSteamBandwidth, true, 1000, 10000000, -1},
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

// Addresses of the game's pointer variables (0 = nothing to do: off, or given up).
uintptr_t g_tables[2] = {};
uintptr_t g_netNode = 0;
uintptr_t g_adminNode = 0;
int g_ticks = 0;
int g_lastSource = -2;  // which bandwidth the game's connections use, as last logged

const char* const kNeeds[] = {"file sender block count", "file sender fill", "connection bandwidth"};

int ReadSetting(const wchar_t* key) {
    const std::wstring ini = k2mp::g.modsDir + L"\\k2mp.ini";
    return static_cast<int>(GetPrivateProfileIntW(L"transfer", key, -1, ini.c_str()));
}

// The var object of entry `index` of a table, or null while the table is not built yet. SEH: reading game memory.
int* VarObject(uintptr_t tableGlobal, int index) {
    __try {
        auto* const* table = *reinterpret_cast<int* const* const*>(tableGlobal);
        return table ? table[index] : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

// Which of the three bandwidths the game's connections send at right now (the choice made by game::kConnBandwidth):
// 0 LAN, 1 unknown, 2 Steam, -1 while the nodes do not exist.
int BandwidthSource() {
    __try {
        const auto* node = *reinterpret_cast<const uint8_t* const*>(g_netNode);
        const auto* admin = *reinterpret_cast<const uint8_t* const*>(g_adminNode);
        if (!node || !admin) return -1;
        if (node[0x65]) return 2;
        return admin[0x304] ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
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
        k2mp::Log("transfer: the network code differs in this k2.exe; settings not applied");
        return false;
    }
    // `mov eax, [global]` (A1 imm32): each operand is a pointer variable's address, already relocated in memory.
    const auto operand = [exeBase](uint32_t rva) { return *reinterpret_cast<const uint32_t*>(exeBase + rva + 1); };
    g_tables[kTransferTable] = operand(game::kFileSenderBlockCount);
    g_tables[kNetTable] = operand(game::kConnBandwidth + 0xb);
    g_netNode = operand(game::kConnBandwidth);
    g_adminNode = operand(game::kConnBandwidth + 0x18);
    return true;
}

// On the game's thread (K2Plugin_Tick), so nothing else touches the tables meanwhile. The tables are built during
// start-up and the game may load settings into them more than once, so every tick puts the overrides back if anything
// changed them (a compare per tick; a log line only when a value actually changes).
void Tick() {
    if (!g_tables[kTransferTable]) return;
    bool any = false;
    for (const Override& o : g_settings) {
        if (o.value < 0) continue;
        int* var = VarObject(g_tables[o.table], o.index);
        if (!var) continue;
        any = true;
        const int want = Stored(o);
        if (*var == want) continue;
        k2mp::Log("transfer: %s %g -> %d", k2mp::Utf8(o.key).c_str(), Shown(o, *var), o.value);
        *var = want;
    }
    const int source = BandwidthSource();
    if (source != g_lastSource) {
        static const char* const kNames[] = {"none yet", "LAN", "unknown", "Steam"};
        k2mp::Log("transfer: connections send at the %s bandwidth", kNames[source + 1]);
        g_lastSource = source;
    }
    if (!any && ++g_ticks > 20 * 60 * 5) {  // five minutes of ticks without the tables: give up
        k2mp::Log("transfer: the game's settings tables never appeared; nothing changed");
        g_tables[kTransferTable] = 0;
    }
}

}  // namespace transfer
