// Kohan II plugin loader, installed as avifil32.dll in the game folder.
//
// Why avifil32.dll: k2.exe imports it (for its movie recorder), it is not a KnownDLL, and nothing loads it earlier,
// so Windows takes this copy from the game folder. Every export passes straight on to the real avifil32.dll in the
// system folder (functions by a jump, the four interface IDs as copies). winmm.dll, the obvious choice, does not work:
// the compatibility shims Windows applies to k2.exe (AcSpecfc.dll) load the system winmm.dll before the game's own
// imports are resolved.
//
// When to start the plugins: the Steam k2.exe is wrapped by SteamStub, whose stub decrypts the game's code section
// after Windows has loaded the imported DLLs, so plugins cannot look at the game's code from DllMain. DllMain points
// k2.exe's own import entries for winmm's timeBeginPeriod, timeGetTime and timeEndPeriod at small wrappers; the first
// call that returns into k2.exe's .text proves the game's code is decrypted and running, and starts the plugins listed
// in <game>\Mods\plugins.txt. The wrappers call whatever the entries held before (the system winmm.dll, or a shim).
#include <windows.h>
#include <intrin.h>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <string>

#include "k2plugin.h"

#pragma intrinsic(_ReturnAddress)

// ---------------------------------------------------------------- avifil32.dll exports (tools/gen_proxy_exports.py)
#define PROXY_EXPORT(index, name, ordinal) #name,
#define PROXY_ORDINAL(index, ordinal) nullptr,
#define PROXY_DATA(index, name, ordinal, l) nullptr,
static const char* const kExportNames[] = {
#include "proxy_exports.inc"
};
#undef PROXY_EXPORT
#undef PROXY_ORDINAL
#undef PROXY_DATA

#define PROXY_EXPORT(index, name, ordinal) ordinal,
#define PROXY_ORDINAL(index, ordinal) ordinal,
#define PROXY_DATA(index, name, ordinal, l) 0,
static const WORD kExportOrdinals[] = {
#include "proxy_exports.inc"
};
#undef PROXY_EXPORT
#undef PROXY_ORDINAL
#undef PROXY_DATA

constexpr int kExportCount = PROXY_EXPORT_COUNT;
static_assert(sizeof(kExportNames) / sizeof(kExportNames[0]) == kExportCount, "export table out of step");

extern "C" FARPROC g_realExports[kExportCount] = {};

#define PROXY_EXPORT(index, name, ordinal) \
    extern "C" __declspec(naked) void proxy_##name() { __asm { jmp dword ptr [g_realExports + index * 4] } }
#define PROXY_ORDINAL(index, ordinal) \
    extern "C" __declspec(naked) void proxy_ord_##ordinal() { __asm { jmp dword ptr [g_realExports + index * 4] } }
#define PROXY_DATA(index, name, ordinal, l) \
    extern "C" const GUID data_##name = {l, 0, 0, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
#include "proxy_exports.inc"
#undef PROXY_EXPORT
#undef PROXY_ORDINAL
#undef PROXY_DATA

namespace {

uintptr_t g_exeBase = 0;
uintptr_t g_textBegin = 0, g_textEnd = 0;  // k2.exe's code section; 0 when this is not the game
volatile LONG g_started = 0;
std::wstring g_gameDir, g_modsDir;
HANDLE g_log = INVALID_HANDLE_VALUE;
CRITICAL_SECTION g_logLock;
int g_hooked = 0;  // how many of k2.exe's winmm import entries point at our wrappers

using PeriodFn = UINT(WINAPI*)(UINT);
using TimeFn = DWORD(WINAPI*)();
PeriodFn g_timeBeginPeriod = nullptr, g_timeEndPeriod = nullptr;
TimeFn g_timeGetTime = nullptr;

bool LoadRealDll() {
    wchar_t path[MAX_PATH];
    const UINT n = GetSystemDirectoryW(path, MAX_PATH);  // SysWOW64 for this 32-bit process
    if (!n || n > MAX_PATH - 16) return false;
    wcscat_s(path, L"\\avifil32.dll");
    const HMODULE real = LoadLibraryW(path);
    if (!real) return false;
    for (int i = 0; i < kExportCount; ++i) {
        if (kExportNames[i])
            g_realExports[i] = GetProcAddress(real, kExportNames[i]);
        else if (kExportOrdinals[i])
            g_realExports[i] = GetProcAddress(real, MAKEINTRESOURCEA(kExportOrdinals[i]));
    }
    return true;
}

const IMAGE_NT_HEADERS32* NtHeaders(uintptr_t base) {
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    return nt->Signature == IMAGE_NT_SIGNATURE ? nt : nullptr;
}

bool FindTextSection(uintptr_t base) {
    const IMAGE_NT_HEADERS32* nt = NtHeaders(base);
    if (!nt) return false;
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++s) {
        if (memcmp(s->Name, ".text", 6) == 0) {
            g_textBegin = base + s->VirtualAddress;
            g_textEnd = g_textBegin + s->Misc.VirtualSize;
            return true;
        }
    }
    return false;
}

// Points k2.exe's import entry for `dll`!`function` at `hook`, returning what it held (0 if not found). Found by name
// through the import lookup table, so it works whatever the entry currently points at.
uintptr_t HookImport(uintptr_t base, const char* dll, const char* function, void* hook) {
    const IMAGE_NT_HEADERS32* nt = NtHeaders(base);
    if (!nt) return 0;
    const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return 0;
    for (auto* d = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); d->Name; ++d) {
        if (_stricmp(reinterpret_cast<const char*>(base + d->Name), dll) != 0 || !d->OriginalFirstThunk) continue;
        auto* names = reinterpret_cast<const IMAGE_THUNK_DATA32*>(base + d->OriginalFirstThunk);
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + d->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL32(names->u1.Ordinal)) continue;
            const auto* byName = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            if (strcmp(reinterpret_cast<const char*>(byName->Name), function) != 0) continue;
            const uintptr_t old = slots->u1.Function;
            DWORD protect;
            if (!VirtualProtect(&slots->u1.Function, 4, PAGE_READWRITE, &protect)) return 0;
            slots->u1.Function = reinterpret_cast<uintptr_t>(hook);
            VirtualProtect(&slots->u1.Function, 4, protect, &protect);
            return old;
        }
    }
    return 0;
}

void __cdecl HostLog(const char* source, const char* fmt, ...) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    char line[1024];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int n = _snprintf_s(line, _TRUNCATE, "[%02d:%02d:%02d.%03d] %s: ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
                        source ? source : "?");
    if (n < 0) n = 0;
    va_list ap;
    va_start(ap, fmt);
    int m = _vsnprintf_s(line + n, sizeof(line) - n - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    n = m < 0 ? static_cast<int>(strlen(line)) : n + m;
    line[n++] = '\r';
    line[n++] = '\n';
    EnterCriticalSection(&g_logLock);
    DWORD written;
    WriteFile(g_log, line, static_cast<DWORD>(n), &written, nullptr);
    LeaveCriticalSection(&g_logLock);
}

// A plugins.txt entry is a bare DLL file name inside Mods\: no folders, no drive, nothing that climbs out.
bool SafeName(const std::wstring& name) {
    if (name.size() < 5 || name.find_first_of(L"\\/:") != std::wstring::npos || name.find(L"..") != std::wstring::npos)
        return false;
    return _wcsicmp(name.c_str() + name.size() - 4, L".dll") == 0;
}

std::wstring Trim(const std::wstring& s) {
    const size_t a = s.find_first_not_of(L" \t\r\n\xFEFF");
    if (a == std::wstring::npos) return L"";
    return s.substr(a, s.find_last_not_of(L" \t\r\n") - a + 1);
}

// Mods\plugins.log, or plugins.2.log ... when another copy of the game running from the same folder has it open.
HANDLE OpenLog(std::wstring* name) {
    for (int n = 1; n <= 8; ++n) {
        *name = n == 1 ? L"plugins.log" : L"plugins." + std::to_wstring(n) + L".log";
        const std::wstring path = g_modsDir + L"\\" + *name;
        HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE || GetLastError() != ERROR_SHARING_VIOLATION) return h;
    }
    return INVALID_HANDLE_VALUE;
}

void StartPlugins() {
    std::wstring logName;
    g_log = OpenLog(&logName);
    HostLog("loader", "Kohan II plugin loader %s, k2.exe at %p, process %lu, log Mods\\%ls", LOADER_VERSION,
            reinterpret_cast<void*>(g_exeBase), GetCurrentProcessId(), logName.c_str());

    const std::wstring listPath = g_modsDir + L"\\plugins.txt";
    FILE* f = nullptr;
    if (_wfopen_s(&f, listPath.c_str(), L"rt, ccs=UTF-8") != 0 || !f) {
        HostLog("loader", "no Mods\\plugins.txt: nothing to load");
        return;
    }
    static const K2Host host = {K2_PLUGIN_API, g_exeBase, g_gameDir.c_str(), g_modsDir.c_str(), HostLog};
    wchar_t buf[512];
    int loaded = 0;
    while (fgetws(buf, 512, f)) {
        const std::wstring name = Trim(buf);
        if (name.empty() || name[0] == L'#') continue;
        if (!SafeName(name)) {
            HostLog("loader", "skipped \"%ls\": not a plain .dll file name", name.c_str());
            continue;
        }
        const std::wstring path = g_modsDir + L"\\" + name;
        const HMODULE m = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!m) {
            HostLog("loader", "could not load %ls (error %lu)", name.c_str(), GetLastError());
            continue;
        }
        const auto start = reinterpret_cast<K2PluginStartFn>(GetProcAddress(m, K2_PLUGIN_START));
        if (!start) {
            HostLog("loader", "%ls has no " K2_PLUGIN_START ": not a Kohan II plugin", name.c_str());
            continue;
        }
        const int r = start(&host);
        HostLog("loader", "%ls %s", name.c_str(), r == 0 ? "started" : "stayed off");
        ++loaded;
    }
    fclose(f);
    HostLog("loader", "%d plugin(s) loaded", loaded);
}

void MaybeStart(void* returnAddress) {
    const auto r = reinterpret_cast<uintptr_t>(returnAddress);
    if (g_started || r < g_textBegin || r >= g_textEnd) return;
    if (InterlockedCompareExchange(&g_started, 1, 0) != 0) return;
    StartPlugins();
}

UINT WINAPI WrapTimeBeginPeriod(UINT period) {
    MaybeStart(_ReturnAddress());
    return g_timeBeginPeriod(period);
}

UINT WINAPI WrapTimeEndPeriod(UINT period) {
    MaybeStart(_ReturnAddress());
    return g_timeEndPeriod(period);
}

DWORD WINAPI WrapTimeGetTime() {
    MaybeStart(_ReturnAddress());
    return g_timeGetTime();
}

template <typename Fn>
void Hook(const char* function, void* wrapper, Fn* original) {
    const uintptr_t old = HookImport(g_exeBase, "WINMM.dll", function, wrapper);
    if (!old) return;
    *original = reinterpret_cast<Fn>(old);
    ++g_hooked;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    if (!LoadRealDll()) return FALSE;

    // Anything other than the game that loads this DLL from the folder gets a plain avifil32.dll.
    wchar_t exePath[MAX_PATH];
    const DWORD len = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    if (!len || len >= MAX_PATH) return TRUE;
    wchar_t* slash = wcsrchr(exePath, L'\\');
    if (!slash || _wcsicmp(slash + 1, L"k2.exe") != 0) return TRUE;
    *slash = 0;
    g_gameDir = exePath;
    g_modsDir = g_gameDir + L"\\Mods";
    InitializeCriticalSection(&g_logLock);
    g_exeBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    if (!FindTextSection(g_exeBase)) return TRUE;
    Hook("timeBeginPeriod", reinterpret_cast<void*>(&WrapTimeBeginPeriod), &g_timeBeginPeriod);
    Hook("timeGetTime", reinterpret_cast<void*>(&WrapTimeGetTime), &g_timeGetTime);
    Hook("timeEndPeriod", reinterpret_cast<void*>(&WrapTimeEndPeriod), &g_timeEndPeriod);
    return TRUE;
}
