// Everything Multiplayer Fixes knows about k2.exe 1.3.68 (Steam, Dev Build 4345.17), in one place.
//
// Addresses are RVAs: the exe is relocated (ASLR), so address = exe base + RVA. Every function the plugin calls or
// patches is listed in kSignatures with the bytes it must find there first; the bytes that relocation changes
// (absolute addresses inside instructions) are skipped with "??". After a game update the bytes differ and the
// features that need them stay off. Names come from the exe's own log strings; docs/MULTIPLAYER.md has the evidence.
#pragma once
#include <cstdint>

namespace game {

// ---------------------------------------------------------------- globals (RVA of a pointer variable)
constexpr uint32_t kGWorld = 0x5d7fb0;           // g_gworld; the world resource the save task serialises is at +0x28
constexpr uint32_t kResourceManager = 0x5d7fcc;  // runs resource tasks (loads, saves) on the loader thread
constexpr uint32_t kAdminNode = 0x5d7fe4;        // this PC's network node (TGC_AdminNode)

// ---------------------------------------------------------------- KKC_Session
constexpr uint32_t kSessionFilename = 0x68;  // engine string: the map or save the session plays / last saved
constexpr uint32_t kSessionState = 0xf0;     // 2 or 4 while a match runs (GameSave refuses other states)

// ---------------------------------------------------------------- functions
// KKC_Session::GameSave (thiscall, session). Applies a GameSave order on every PC: the host creates the save task,
// the others show "The host is saving the game." and call BeginSoloTask to wait for it. Both freeze the sim first.
constexpr uint32_t kGameSave = 0x1624d5;
// The client branch's `call TGC_AdminNode::BeginSoloTask` (0x54c4c0): reached only on PCs that are not the host,
// with EDI = the session. Multiplayer Fixes adds its local copy of the save here.
constexpr uint32_t kGameSaveClientCall = 0x1625f5;
// The host branch's `call KKC_Session::CreateSaveGameTask` (0x56a457), EDI = the session.
constexpr uint32_t kGameSaveHostCall = 0x16269b;
constexpr uint32_t kBeginSoloTask = 0x14c4c0;        // TGC_AdminNode::BeginSoloTask (thiscall, bool, node*), ret 8
constexpr uint32_t kCreateSaveGameTask = 0x16a457;   // KKC_Session::CreateSaveGameTask (thiscall, int flag), ret 4
constexpr uint32_t kEngineNew = 0x2ee91a;            // operator new (cdecl, size)
constexpr uint32_t kSaveTaskCtor = 0x1c1268;         // KKC_ResourceManagerTaskSaveGame(world res, int map, string*)
constexpr uint32_t kQueueTask = 0x1cbe21;            // resource manager: queue a task (thiscall, task*)
constexpr uint32_t kBlockOnTask = 0x1ce1de;          // TGC_ResourceManagerTask::Block (thiscall): run now or wait
constexpr uint32_t kStringFromLiteral = 0x205de;     // engine string: construct from const wchar_t* (thiscall)
constexpr uint32_t kStringRelease = 0x21375;         // engine string: release, ECX = data - 0x10 (thiscall)
// TGC_AdminConn::SerializeConnect(I): the host's answer to a joining player ("Game is already in progress").
constexpr uint32_t kSerializeConnect = 0x1506dd;

// ---------------------------------------------------------------- resource tasks (TGC_ResourceManagerTask)
constexpr uint32_t kTaskRefCount = 0x04;  // interlocked; the deleting destructor (vtable slot 0) runs at 0
constexpr uint32_t kTaskStatus = 0x14;    // 0 queued, 1 running, 2 finished, 3 failed (KKC_Session::CheckForFinishedTask)
constexpr int kTaskFinished = 2;
constexpr int kTaskFailed = 3;

// ---------------------------------------------------------------- signatures
struct Signature {
    const char* name;
    uint32_t rva;
    const char* bytes;  // hex pairs, "??" = any byte
};

// clang-format off
inline constexpr Signature kSignatures[] = {
    {"KKC_Session::GameSave", kGameSave,
     "b8 ?? ?? ?? ?? e8 fd e8 27 00 51 51 53 56 57 8b f9 8b 0d ?? ?? ?? ?? e8"},
    // client branch: push eax; push ebx; call BeginSoloTask; push "NET: GameSave on client"
    {"GameSave client call", kGameSaveClientCall - 2,
     "50 53 e8 c6 9e fe ff 68 ?? ?? ?? ?? 68 ?? ?? ?? ?? 68 56 0c 00 00"},
    // host branch: push esi; call 0x423fb6; push 0; mov ecx, edi; call CreateSaveGameTask; mov ecx, [ebp-0xc]
    {"GameSave host call", kGameSaveHostCall - 10,
     "56 e8 1f 19 ec ff 6a 00 8b cf e8 b7 7d 00 00 8b 4d f4"},
    {"TGC_AdminNode::BeginSoloTask", kBeginSoloTask,
     "8a 44 24 04 56 8b f1 88 86 9c 00 00 00 0f b6 c0 50 68"},
    {"KKC_Session::CreateSaveGameTask", kCreateSaveGameTask,
     "b8 ?? ?? ?? ?? e8 ?? ?? ?? ?? 51 56 6a 2c 8b f1 e8 ae 44 18 00 8b d0 59"},
    {"operator new", kEngineNew,
     "8b 44 24 04 83 c0 24 80 3d ?? ?? ?? ?? 00 50 75 07 e8"},
    {"KKC_ResourceManagerTaskSaveGame ctor", kSaveTaskCtor,
     "55 8b ec 51 8b 45 10 56 57 51 8b 00 8b f9 83 e8 10 89 7d fc"},
    {"resource manager queue", kQueueTask,
     "b8 ?? ?? ?? ?? e8 ?? ?? ?? ?? 51 51 56 57 8b f9 8d 77 20 8b 06 8b ce"},
    {"TGC_ResourceManagerTask::Block", kBlockOnTask,
     "51 51 55 8b e9 8b 45 00 ff 50 10 8b cd 84 c0 74 0a e8"},
    // CStringW(const wchar_t*): gets the string manager, then assigns the text; ret 4
    {"engine string ctor", kStringFromLiteral,
     "b8 ?? ?? ?? ?? e8 f4 07 3c 00 51 56 8b f1 6a 02 89 75 f0 e8 77 d7 2d 00"},
    // CStringData::Release: lock xadd [edx+0xc] (reference count), free through the string manager at 0
    {"engine string release", kStringRelease,
     "8b d1 83 c8 ff f0 0f c1 42 0c 48 85 c0 7f 08 8b 0a 52 8b 01 ff 50 04 c3"},
    {"TGC_AdminConn::SerializeConnect", kSerializeConnect,
     "b8 ?? ?? ?? ?? e8 f5 06 29 00 81 ec 8c 00 00 00 a1 ?? ?? ?? ?? 53 56 8b"},
};
// clang-format on

// Calls the plugin redirects: the E8 instruction at `site` must call `target` in this build.
struct CallSite {
    const char* name;
    uint32_t site;
    uint32_t target;
};

inline constexpr CallSite kCallSites[] = {
    {"GameSave client call -> BeginSoloTask", kGameSaveClientCall, kBeginSoloTask},
    {"GameSave host call -> CreateSaveGameTask", kGameSaveHostCall, kCreateSaveGameTask},
};

}  // namespace game
