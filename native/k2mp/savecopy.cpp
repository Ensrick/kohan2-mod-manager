#include "savecopy.h"

#include <windows.h>
#include <cwchar>

#include "game.h"
#include "hook.h"
#include "plugin.h"

// The stubs reach these by name from inline assembly, so they live at file scope with C linkage.
extern "C" {
uintptr_t g_k2mpOrigBeginSoloTask = 0;
uintptr_t g_k2mpOrigCreateSaveGameTask = 0;
void(__cdecl* g_k2mpOnClientSave)(void* session) = nullptr;
void(__cdecl* g_k2mpOnHostSave)(void* session) = nullptr;
}

namespace savecopy {

namespace {

using EngineNewFn = void*(__cdecl*)(size_t size);
using SaveTaskCtorFn = void*(__thiscall*)(void* self, void* worldResource, int isMap, void* filename);
using QueueTaskFn = void(__thiscall*)(void* manager, void* task);
using BlockOnTaskFn = void(__thiscall*)(void* task);
using StringCtorFn = void*(__thiscall*)(void* self, const wchar_t* text);
using StringReleaseFn = void(__thiscall*)(void* header);
using DeletingDtorFn = void*(__thiscall*)(void* self, int flags);

uintptr_t g_base = 0;
volatile LONG g_broken = 0;  // set after an exception inside the engine: the feature stays off for this run

// The client branch's `call BeginSoloTask(bool, node*)`, a thiscall that pops its two arguments. The stub repeats the
// call with the same ECX and arguments, then hands EDI (the session, kept by the callee as the ABI requires) to our
// code with every register and flag preserved, and returns the way BeginSoloTask would.
__declspec(naked) void ClientSaveStub() {
    __asm {
        push dword ptr [esp + 8]
        push dword ptr [esp + 8]
        call dword ptr [g_k2mpOrigBeginSoloTask]
        pushad
        pushfd
        cld
        push edi
        call dword ptr [g_k2mpOnClientSave]
        add esp, 4
        popfd
        popad
        ret 8
    }
}

// The host branch's `call CreateSaveGameTask(int)` (ECX = EDI = the session), a thiscall that pops one argument.
__declspec(naked) void HostSaveStub() {
    __asm {
        push dword ptr [esp + 4]
        call dword ptr [g_k2mpOrigCreateSaveGameTask]
        pushad
        pushfd
        cld
        push edi
        call dword ptr [g_k2mpOnHostSave]
        add esp, 4
        popfd
        popad
        ret 4
    }
}

// The session's current file name (an engine string) into `buf`; empty if it cannot be read. SEH needs a function
// without C++ objects, hence the split from SessionFilename.
void ReadSessionFilename(void* session, wchar_t* buf, size_t count) {
    buf[0] = 0;
    __try {
        const wchar_t* text = *reinterpret_cast<wchar_t* const*>(static_cast<char*>(session) + game::kSessionFilename);
        if (text) wcsncpy_s(buf, count, text, _TRUNCATE);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        buf[0] = 0;
    }
}

std::wstring SessionFilename(void* session) {
    wchar_t buf[MAX_PATH];
    ReadSessionFilename(session, buf, MAX_PATH);
    return buf;
}

int SessionState(void* session) {
    __try {
        return *reinterpret_cast<const int*>(static_cast<char*>(session) + game::kSessionState);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

using IsFromResourceFn = bool(__cdecl*)(void* settings);

// The walk the save task starts with (game::kSaveSourceWalk), read-only and checked, so a copy is only attempted
// where the game's own code would not fault: 0 ready, 1 a resource without create settings, 2 a source resource whose
// header is not loaded, 3 a chain too long to trust, -1 unreadable. `name` gets the offending resource's name.
int SourcesReady(wchar_t* name, size_t count) {
    name[0] = 0;
    __try {
        void* world = *reinterpret_cast<void**>(g_base + game::kGWorld);
        if (!world) return -1;
        auto* res = *reinterpret_cast<const uint8_t**>(static_cast<char*>(world) + 0x28);
        if (!res) return -1;
        if (!(*reinterpret_cast<const uint32_t*>(res + game::kResourceFlags) & game::kResourceMadeFromOther)) return 0;
        const auto isFromResource = reinterpret_cast<IsFromResourceFn>(g_base + game::kIsFromResourceSettings);
        for (int depth = 0; depth < 16; ++depth) {
            const wchar_t* text = *reinterpret_cast<const wchar_t* const*>(res + game::kResourceName);
            name[0] = 0;
            if (text) wcsncpy_s(name, count, text, _TRUNCATE);
            void* settings = *reinterpret_cast<void* const*>(res + game::kResourceCreateSettings);
            if (!settings) return 1;
            if (!isFromResource(settings)) return 0;
            res = *reinterpret_cast<const uint8_t* const*>(static_cast<char*>(settings) + game::kSettingsSource);
            if (!res) return 1;
            if (!(*reinterpret_cast<const uint32_t*>(res + game::kResourceFlags) & game::kResourceHeaderLoaded)) {
                text = *reinterpret_cast<const wchar_t* const*>(res + game::kResourceName);
                name[0] = 0;
                if (text) wcsncpy_s(name, count, text, _TRUNCATE);
                return 2;
            }
        }
        return 3;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// Exactly what KKC_Session::CreateSaveGameTask does (new task, construct, queue), except that the task is ours, not
// the session's, and that we then wait for it with the engine's own TGC_ResourceManagerTask::Block, the call the
// session itself uses to wait for an unfinished task: it runs a task that has not started right here, or waits for
// the loader thread to finish it. The game is paused for the host's save at this point, so nothing changes the
// world while it is written. Returns the task's final status, or a negative code; no C++ objects in here (SEH).
int WriteCopy(const wchar_t* copyName, DWORD* exceptionCode) {
    __try {
        void* world = *reinterpret_cast<void**>(g_base + game::kGWorld);
        if (!world) return -2;
        void* worldResource = *reinterpret_cast<void**>(static_cast<char*>(world) + 0x28);
        void* manager = *reinterpret_cast<void**>(g_base + game::kResourceManager);
        if (!worldResource || !manager) return -3;

        wchar_t* filename = nullptr;  // an engine string is one pointer to its text, its header 16 bytes before it
        reinterpret_cast<StringCtorFn>(g_base + game::kStringFromLiteral)(&filename, copyName);
        void* task = reinterpret_cast<EngineNewFn>(g_base + game::kEngineNew)(0x2c);
        if (task) reinterpret_cast<SaveTaskCtorFn>(g_base + game::kSaveTaskCtor)(task, worldResource, 0, &filename);
        reinterpret_cast<StringReleaseFn>(g_base + game::kStringRelease)(reinterpret_cast<char*>(filename) - 0x10);
        if (!task) return -4;

        auto* refs = reinterpret_cast<volatile LONG*>(static_cast<char*>(task) + game::kTaskRefCount);
        InterlockedIncrement(refs);  // ours, released below
        reinterpret_cast<QueueTaskFn>(g_base + game::kQueueTask)(manager, task);
        reinterpret_cast<BlockOnTaskFn>(g_base + game::kBlockOnTask)(task);
        const int status = *reinterpret_cast<const int*>(static_cast<char*>(task) + game::kTaskStatus);
        if (InterlockedDecrement(refs) == 0) {
            void** vtable = *reinterpret_cast<void***>(task);
            reinterpret_cast<DeletingDtorFn>(vtable[0])(task, 1);
        }
        return status;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *exceptionCode = GetExceptionCode();
        return -1;
    }
}

std::wstring OnDisk(const std::wstring& relative) {
    std::wstring p = k2mp::UserDataDir() + L"\\" + relative;
    for (wchar_t& c : p)
        if (c == L'/') c = L'\\';
    return p;
}

void __cdecl OnClientSave(void* session) {
    if (g_broken) return;
    const std::wstring name = SessionFilename(session);
    const std::wstring copy = CopyName(name);
    const int state = SessionState(session);
    if (copy.empty() || (state != 2 && state != 4)) {
        k2mp::Log("save copy skipped: file \"%s\", session state %d", k2mp::Utf8(name).c_str(), state);
        return;
    }
    wchar_t source[MAX_PATH];
    const int ready = SourcesReady(source, MAX_PATH);
    if (ready != 0) {
        static const char* const kWhy[] = {"cannot be read", "", "has no create settings", "is not loaded",
                                           "chain is too long"};
        k2mp::Log("save copy of \"%s\" skipped: the game's save code would fail here (map source \"%s\" %s)",
                  k2mp::Utf8(name).c_str(), k2mp::Utf8(source).c_str(), kWhy[ready + 1]);
        return;
    }
    const ULONGLONG start = GetTickCount64();
    DWORD exceptionCode = 0;
    const int status = WriteCopy(copy.c_str(), &exceptionCode);
    const unsigned ms = static_cast<unsigned>(GetTickCount64() - start);
    if (status == -1) {
        InterlockedExchange(&g_broken, 1);
        k2mp::Log("save copy FAILED with exception 0x%08lx after %u ms; save copies are off until the game restarts",
                  exceptionCode, ms);
        return;
    }
    if (status != game::kTaskFinished) {
        k2mp::Log("save copy of \"%s\" did not finish (status %d) after %u ms", k2mp::Utf8(name).c_str(), status, ms);
        return;
    }
    std::string hex;
    uint64_t size = 0;
    if (k2mp::HashFile(OnDisk(copy), &hex, &size))
        k2mp::Log("save copy \"%s\" written in %u ms: %llu bytes, sha256 %s", k2mp::Utf8(copy).c_str(), ms, size,
                  hex.c_str());
    else
        k2mp::Log("save copy \"%s\" finished in %u ms but was not found at %s", k2mp::Utf8(copy).c_str(), ms,
                  k2mp::Utf8(OnDisk(copy)).c_str());
}

void __cdecl OnHostSave(void* session) {
    const std::wstring name = SessionFilename(session);
    if (name.empty()) return;
    k2mp::HashWhenSettled(OnDisk(name), "host save \"" + k2mp::Utf8(name) + "\"", 120000);
}

const char* const kNeeds[] = {
    "KKC_Session::GameSave",
    "GameSave client call",
    "GameSave host call",
    "TGC_AdminNode::BeginSoloTask",
    "KKC_Session::CreateSaveGameTask",
    "operator new",
    "KKC_ResourceManagerTaskSaveGame ctor",
    "resource manager queue",
    "TGC_ResourceManagerTask::Block",
    "engine string ctor",
    "engine string release",
    "save task source walk",
    "save task source step",
    "save task settings read",
    "settings type check",
};

}  // namespace

std::wstring CopyName(const std::wstring& name) {
    if (name.size() < 5 || name.find(L"..") != std::wstring::npos || name.find(L':') != std::wstring::npos ||
        name[0] == L'/' || name[0] == L'\\' || _wcsicmp(name.c_str() + name.size() - 4, L".rsg") != 0)
        return {};
    return name.substr(0, name.size() - 4) + L" (copy)" + name.substr(name.size() - 4);
}

bool Install(uintptr_t exeBase) {
    if (!hook::SignaturesMatch(exeBase, kNeeds, static_cast<int>(sizeof(kNeeds) / sizeof(kNeeds[0])))) return false;
    g_base = exeBase;
    g_k2mpOrigBeginSoloTask = exeBase + game::kBeginSoloTask;
    g_k2mpOrigCreateSaveGameTask = exeBase + game::kCreateSaveGameTask;
    g_k2mpOnClientSave = OnClientSave;
    g_k2mpOnHostSave = OnHostSave;
    if (!hook::RedirectCall(exeBase + game::kGameSaveClientCall, g_k2mpOrigBeginSoloTask,
                            reinterpret_cast<void*>(&ClientSaveStub)))
        return false;
    if (!hook::RedirectCall(exeBase + game::kGameSaveHostCall, g_k2mpOrigCreateSaveGameTask,
                            reinterpret_cast<void*>(&HostSaveStub))) {
        // put the first one back so the game runs exactly as shipped
        hook::RedirectCall(exeBase + game::kGameSaveClientCall, reinterpret_cast<uintptr_t>(&ClientSaveStub),
                           reinterpret_cast<void*>(g_k2mpOrigBeginSoloTask));
        return false;
    }
    return true;
}

namespace testing {
void* ClientStub() { return reinterpret_cast<void*>(&ClientSaveStub); }
void* HostStub() { return reinterpret_cast<void*>(&HostSaveStub); }
void Route(uintptr_t beginSoloTask, uintptr_t createSaveGameTask, void(__cdecl* onClient)(void*),
           void(__cdecl* onHost)(void*)) {
    g_k2mpOrigBeginSoloTask = beginSoloTask;
    g_k2mpOrigCreateSaveGameTask = createSaveGameTask;
    g_k2mpOnClientSave = onClient;
    g_k2mpOnHostSave = onHost;
}
}  // namespace testing

}  // namespace savecopy
