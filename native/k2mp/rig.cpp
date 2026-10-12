#include "rig.h"

#include <windows.h>
#include <cwchar>
#include <string>

#include "game.h"
#include "hook.h"
#include "plugin.h"

// Engine strings (one pointer to the text, header 16 bytes before it) holding the rig's address. Built once with the
// engine's own constructor and never released, so the dialog can take references to them like to its own editboxes.
extern "C" {
wchar_t* g_k2mpRigIp = nullptr;
wchar_t* g_k2mpRigPort = nullptr;
}

namespace rig {

namespace {

using StringCtorFn = void*(__thiscall*)(void* self, const wchar_t* text);

// Stand in for `call widget text getter` (thiscall, no arguments, returns the address of an engine string).
__declspec(naked) void IpReadStub() {
    __asm {
        lea eax, g_k2mpRigIp
        ret
    }
}

__declspec(naked) void PortReadStub() {
    __asm {
        lea eax, g_k2mpRigPort
        ret
    }
}

const char* const kNeeds[] = {"widget text getter", "join direct IP read", "join direct port read",
                              "engine string ctor"};

}  // namespace

bool Install(uintptr_t exeBase) {
    wchar_t value[64] = {};
    if (!GetEnvironmentVariableW(L"K2MP_RIG_JOIN", value, 64)) return false;
    const wchar_t* colon = wcsrchr(value, L':');
    if (!colon || colon == value || !colon[1]) {
        k2mp::Log("test rig: K2MP_RIG_JOIN=\"%s\" is not <ip>:<port>; not installed", k2mp::Utf8(value).c_str());
        return false;
    }
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) {
        k2mp::Log("test rig: the Direct IP dialog differs in this k2.exe; not installed");
        return false;
    }
    const std::wstring ip(static_cast<const wchar_t*>(value), colon), port(colon + 1);
    const auto ctor = reinterpret_cast<StringCtorFn>(exeBase + game::kStringFromLiteral);
    ctor(&g_k2mpRigIp, ip.c_str());
    ctor(&g_k2mpRigPort, port.c_str());
    const uintptr_t getter = exeBase + game::kWidgetText;
    if (!hook::RedirectCall(exeBase + game::kJoinDirectIpRead, getter, reinterpret_cast<void*>(&IpReadStub)) ||
        !hook::RedirectCall(exeBase + game::kJoinDirectPortRead, getter, reinterpret_cast<void*>(&PortReadStub))) {
        k2mp::Log("test rig: could not patch the Direct IP dialog");
        return false;
    }
    k2mp::Log("test rig: Direct IP joins go to %s", k2mp::Utf8(value).c_str());
    return true;
}

}  // namespace rig
