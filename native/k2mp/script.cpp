#include "script.h"

#include <windows.h>

#include "game.h"
#include "hook.h"
#include "plugin.h"

namespace script {

namespace {

using StringCtorFn = void*(__thiscall*)(void* self, const wchar_t* text);
using StringReleaseFn = void(__thiscall*)(void* header);
using RunFn = void(__thiscall*)(void* console, void* command, int flag);

uintptr_t g_base = 0;
uintptr_t g_consoleGlobal = 0;  // address of the game's pointer to its console; 0 = not available

const char* const kNeeds[] = {"script command read", "run script command", "engine string ctor",
                              "engine string release"};

// 1 queued, 0 no console yet, -1 an exception (code in *code). SEH: calls into the game.
int RunGuarded(const wchar_t* command, DWORD* code) {
    __try {
        void* console = *reinterpret_cast<void* const*>(g_consoleGlobal);
        if (!console) return 0;
        wchar_t* text = nullptr;  // an engine string is one pointer to its text, its header 16 bytes before it
        reinterpret_cast<StringCtorFn>(g_base + game::kStringFromLiteral)(&text, command);
        reinterpret_cast<RunFn>(g_base + game::kRunScriptCommand)(console, &text, 0);  // copies the string
        reinterpret_cast<StringReleaseFn>(g_base + game::kStringRelease)(reinterpret_cast<char*>(text) - 0x10);
        return 1;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

}  // namespace

bool Install(uintptr_t exeBase) {
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) return false;
    g_base = exeBase;
    // `mov ecx, [console]` (8B 0D imm32): the operand is the address of the game's pointer, already relocated.
    g_consoleGlobal = *reinterpret_cast<const uint32_t*>(exeBase + game::kScriptCommandRead + 2);
    return true;
}

bool Run(const wchar_t* command) {
    if (!g_consoleGlobal) return false;
    DWORD code = 0;
    const int r = RunGuarded(command, &code);
    if (r < 0) {
        k2mp::Log("script: \"%s\" failed with exception 0x%08lx; no more script commands from the plugin",
                  k2mp::Utf8(command).c_str(), code);
        g_consoleGlobal = 0;
    }
    return r > 0;
}

}  // namespace script
