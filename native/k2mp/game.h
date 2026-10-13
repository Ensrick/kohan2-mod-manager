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
constexpr uint32_t kNetNode = 0x5d7fbc;          // the packet layer under it (TGC_NetNode)
// The pointer variables of KKC_Session (the lobby and the match) and of the console that runs script commands are read
// from the code that uses them: kSessionRead and kScriptCommandRead below.

// Registers the "%USERDATA%" path token: `push dword ptr [personal folder]` (an engine string such as
// "C:/Users/<name>/Documents/Kohan2", from SHGetFolderPathW(CSIDL_PERSONAL) + "Kohan2"). The game's work depot, where
// saves go, is that folder's "data". The plugin reads the operand instead of working the path out itself.
constexpr uint32_t kUserDataToken = 0x1cb1f2;

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
// File transfer (TGC_FileSender / TGC_FileReceiver). The AVars settings table is reached through a global pointer whose
// address the code below embeds: `mov eax, [table]` at kFileSenderBlockCount + 0. Entries 0x48, 0x49, 0x4a are
// NetworkFileTransferBlocksPerFrame (20), NetworkFileTransferBlockSize (64), NetworkFileTransferMaxPendingBlocks (256);
// each entry points at a var object whose int value is at offset 0. The receiver splits files with its own block size
// (0x5593ea), so a block size must be the same on every PC of a game.
constexpr uint32_t kFileSenderBlockCount = 0x158ca4;  // TGC_FileSender: number of blocks of the file being sent
constexpr uint32_t kFileSenderFill = 0x158ef4;        // TGC_FileSender: queue blocks up to MaxPendingBlocks
constexpr int kVarAdminTicksPerSecond = 0x46;       // NetworkAdminTicksPerSecond (float)
constexpr int kVarAdminAsyncTicksPerSecond = 0x47;  // NetworkAdminAsyncTicksPerSecond (float, 16)
constexpr int kVarBlocksPerFrame = 0x48;
constexpr int kVarBlockSize = 0x49;
constexpr int kVarMaxPendingBlocks = 0x4a;
// The send rate of one connection in bytes per second (TGC_NetNode::SendPackets 0x55b84d spends it as a byte budget per
// connection): NetSteamBandwidth when the net node runs on Steam (byte at node + 0x65), else NetMaxBandwidthToUnknown when
// the admin node's byte at + 0x304 is set, else NetLanBandwidth. These three floats live in a second settings table,
// reached through `mov eax, [table]` at kConnBandwidth + 0xb; the net node and admin node globals are read the same way
// at + 0x0 and + 0x18.
constexpr uint32_t kConnBandwidth = 0x15c44b;
constexpr int kVarLanBandwidth = 0xc1;      // NetLanBandwidth (float, 200000)
constexpr int kVarUnknownBandwidth = 0xc2;  // NetMaxBandwidthToUnknown (float, 4000)
constexpr int kVarSteamBandwidth = 0xc3;    // NetSteamBandwidth (float, 200000)

// TGC_AdminConn::GetWantedPacketType (fastcall, ECX = the connection to one other PC): which packet to send that PC
// now, 0 = none yet. SendPackets (0x55b84d) asks it through TGC_NetConn 0x55bfb1 to pick a connection, and the packet
// writer (0x54fd85) asks it again for what to write. On the host, a player who is in the lobby or the match gets packet
// type 5 at most every 0.1 s; type 5 carries the connection's streams, the file being sent among them, in at most 384
// bytes (0x551304). Hence about 2 KB/s for a save sent from the lobby.
constexpr uint32_t kGetWantedPacketType = 0x14fb23;
constexpr uint32_t kWantedTypeWriteCall = 0x14fd8d;     // in the packet writer, ECX = the connection
constexpr uint32_t kWantedTypeScheduleCall = 0x15bfc8;  // in TGC_NetConn 0x55bfb1, ECX = the connection
constexpr int kPacketTypeStreams = 5;
// TGC_AdminConn::UpdateFileTransferStatus (thiscall, the connection): reports the progress of the file this PC is
// sending that player; the connection's state at +0x3c is 2 while it does, and it sets 1 when the last block is in.
constexpr uint32_t kUpdateFileTransferStatus = 0x15000a;

// ---------------------------------------------------------------- network objects (field offsets)
// TGC_AdminNode
constexpr uint32_t kAdminServer = 0x2e4;     // the node acting as server: the admin node itself on the host
constexpr uint32_t kAdminFlag2ec = 0x2ec;    // byte; when set GetWantedPacketType only answers for the server
constexpr uint32_t kAdminBackupMode = 0x2ed; // byte; set while the host is lost ("backup mode")
// TGC_AdminConn
constexpr uint32_t kConnFileState = 0x3c;    // 2 while this PC sends that player a file
constexpr int kFileStateSending = 2;
constexpr uint32_t kConnUp = 0x98;           // byte; GetWantedPacketType sends nothing while it is 0
constexpr uint32_t kConnFlag99 = 0x99;       // byte; when set the host sends packet type 11 instead
constexpr uint32_t kConnNetConn = 0x42c;     // its TGC_NetConn
// TGC_NetConn / TGC_NetNode
constexpr uint32_t kNetConnLastSend = 0x90;  // float: network clock when it last sent a packet
constexpr uint32_t kNetNodeClock = 0x50;     // float: network clock, seconds

// TGC_NetNode::OnReceiveError (0x55b5d8): `mov eax, [session]; add esp, 0x14; test eax, eax; je; cmp dword [eax+0xf0],
// 0`. Every function the game calls with ECX = [session] lies in KKC_Session's code (0x561000-0x570000).
constexpr uint32_t kSessionRead = 0x15b643;
// Each connection of the net node: `mov esi, [ecx+0x70]`, then {conn, next} list nodes (TGC_NetNode 0x55b4a2).
constexpr uint32_t kForEachConnection = 0x15b4a2;
constexpr uint32_t kNetNodeConnections = 0x70;
// KKC_Application::ParseCommandLine (0x53d61c) runs a "+command" from the command line with
// `mov ecx, [console]; lea eax, [string]; add esp, 0x18; push ebx (0); push eax; call RunScriptCommand`.
constexpr uint32_t kScriptCommandRead = 0x13da73;
constexpr uint32_t kRunScriptCommand = 0x1f9333;  // thiscall(console, engine string*, bool), ret 8: queue a command
// The script command IgnoreAsserts (handler 0x53f7c6) sets the flag the assertion handler (0x700d92) checks before it
// shows its Abort/Retry/Ignore dialog: with the flag set, a failed check is logged ("ASSERT: ...") and the game goes on
// as if Ignore had been clicked.

// KKC_JoinDirectDialog's join handler (0x4eedb8) reads the IP and port editboxes with the widget text getter
// (thiscall, returns the address of the widget's engine string). Only the test rig redirects these two calls.
constexpr uint32_t kWidgetText = 0x29f774;
constexpr uint32_t kJoinDirectIpRead = 0x0eedd0;
constexpr uint32_t kJoinDirectPortRead = 0x0eedf2;

// KKC_ResourceManagerTaskSaveGame::SaveGame (0x5c1473) first walks the resources the world was made from: when the
// world resource's flags (+0x8) have bit 4, it follows res -> create settings (+0x88) -> while those settings are the
// "from a resource" kind (0x56b9f0), their resource (+0x50), asserting that each settings object exists and each
// resource has its header loaded (flags bit 1). It reads a missing settings object anyway: a crash on the loader
// thread. Seen on a client whose lobby had first picked a saved game and then a random map (test rig, 2026-10-12).
constexpr uint32_t kSaveSourceWalk = 0x1c1480;
constexpr uint32_t kSaveSourceStep = 0x1c14f7;
constexpr uint32_t kIsFromResourceSettings = 0x16b9f0;  // bool cdecl(settings*)
constexpr uint32_t kResourceFlags = 0x08;
constexpr uint32_t kResourceName = 0x38;          // engine string
constexpr uint32_t kResourceCreateSettings = 0x88;
constexpr uint32_t kSettingsSource = 0x50;
constexpr uint32_t kResourceMadeFromOther = 1u << 4;
constexpr uint32_t kResourceHeaderLoaded = 1u << 1;

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
    // mov eax, [AVars table]; push esi; push edi; mov edi, [ecx+0x48]; mov eax, [eax+0x124] (block size); mov ecx, [ecx+0x2c]
    {"file sender block count", kFileSenderBlockCount, "a1 ?? ?? ?? ?? 56 57 8b 79 48 8b 80 24 01 00 00 8b 49 2c 8b 30"},
    // mov edx, [AVars table]; push ebx; push ebp; mov ebx, ecx; mov eax, [edx+0x128] (max pending); push esi; push edi
    {"file sender fill", kFileSenderFill, "8b 15 ?? ?? ?? ?? 53 55 8b d9 8b 82 28 01 00 00 56 57 8b 28 8b 82 24 01"},
    // mov eax, [net node]; cmp byte [eax+0x65], 0; je; mov eax, [net table]; mov eax, [eax+0x30c]; jmp;
    // mov eax, [admin node]; cmp byte [eax+0x304], 0
    {"connection bandwidth", kConnBandwidth,
     "a1 ?? ?? ?? ?? 80 78 65 00 74 0d a1 ?? ?? ?? ?? 8b 80 0c 03 00 00 eb 21 a1 ?? ?? ?? ?? 80 b8 04 03 00 00 00"},
    // push dword ptr [personal folder]; mov ecx, [esp+8]; push "%USERDATA%"; call; ret 4
    {"user data token", kUserDataToken, "ff 35 ?? ?? ?? ?? 8b 4c 24 08 68 ?? ?? ?? ?? e8 1d 58 e6 ff c2 04 00"},
    // push esi; mov esi, ecx; mov ecx, [admin node]; cmp byte [ecx+0x2ec], 0; je; cmp esi, [ecx+0x2e4]; jne; push 2;
    // mov eax, [net node]; mov ecx, [esi+0x42c]; movss xmm0, [eax+0x50]; xor eax, eax; subss xmm0, [ecx+0x90]
    {"TGC_AdminConn::GetWantedPacketType", kGetWantedPacketType,
     "56 8b f1 8b 0d ?? ?? ?? ?? 80 b9 ec 02 00 00 00 74 35 3b b1 e4 02 00 00 0f 85 16 01 00 00 6a 02 a1 ?? ?? ?? ?? "
     "8b 8e 2c 04 00 00 f3 0f 10 40 50 33 c0 f3 0f 5c 81 90 00 00 00"},
    // its host branch: cmp byte [esi+0x99], 0; ... comiss xmm0, [0.1]; ...; cmp byte [esi+0x98], 0; ...;
    // cmp byte [ecx+0x2ed], 0
    {"GetWantedPacketType host branch", kGetWantedPacketType + 0x55,
     "80 be 99 00 00 00 00 74 25 6a 0b 8b 8e 2c 04 00 00 a1 ?? ?? ?? ?? f3 0f 10 40 50 f3 0f 5c 81 90 00 00 00 33 c0 "
     "0f 2f 05 ?? ?? ?? ?? eb be 80 be 98 00 00 00 00 0f 84 a4 00 00 00 80 b9 ed 02 00 00 00"},
    // mov esi, ecx; call GetWantedPacketType; mov edi, [ebp+8]
    {"packet writer call", kWantedTypeWriteCall - 2, "8b f1 e8 91 fd ff ff 8b 7d 08"},
    // mov ecx, eax; neg esi; call GetWantedPacketType; mov [edi+4], esi
    {"send scheduler call", kWantedTypeScheduleCall - 4, "8b c8 f7 de e8 56 3b ff ff 89 77 04"},
    // cmp dword [edi+0x3c], 2; jne; push ebx; lea ebx, [edi+0x3b4] (the file sender)
    {"TGC_AdminConn::UpdateFileTransferStatus", kUpdateFileTransferStatus + 0x10,
     "83 7f 3c 02 0f 85 bb 00 00 00 53 8d 9f b4 03 00 00"},
    // mov eax, [session]; add esp, 0x14; test eax, eax; je; cmp dword [eax+0xf0], 0; je
    {"session read", kSessionRead, "a1 ?? ?? ?? ?? 83 c4 14 85 c0 74 4d 83 b8 f0 00 00 00 00 74 44"},
    // push esi; mov esi, [ecx+0x70]; jmp; mov ecx, [esi]; call; mov esi, [esi+4]; test esi, esi; jne; pop esi; ret
    {"for each connection", kForEachConnection, "56 8b 71 70 eb 0a 8b 0e e8 ?? ?? ?? ?? 8b 76 04 85 f6 75 f2 5e c3"},
    // mov ecx, [console]; lea eax, [ebp-0x1c]; add esp, 0x18; push ebx; push eax; call RunScriptCommand
    {"script command read", kScriptCommandRead, "8b 0d ?? ?? ?? ?? 8d 45 e4 83 c4 18 53 50 e8 ad b8 0b 00"},
    // push ebp; mov ebp, esp; mov eax, [ebp+8]; push ebx; push esi; push edi; mov eax, [eax]; mov ebx, ecx;
    // sub eax, 0x10; cmp byte [ebp+0xc], 0; je
    {"run script command", kRunScriptCommand, "55 8b ec 8b 45 08 53 56 57 8b 00 8b d9 83 e8 10 80 7d 0c 00 74 1a"},
    // mov edi, ecx; mov esi, [edi+0x1c]; mov eax, [esi+8]; shr eax, 4; test al, 1; je; push dword [esi+0x38]
    {"save task source walk", kSaveSourceWalk, "8b f9 8b 77 1c 8b 46 08 c1 e8 04 a8 01 0f 84 f8 00 00 00 ff 76 38"},
    // push ebx; call 0x56b9f0; pop ecx; test al, al; je; push ebx; call 0x5c4f12; cmp byte [assert flag], 0; pop ecx;
    // mov esi, [eax+0x50]; jne; mov eax, [esi+8]; shr eax, 1; test al, 1
    {"save task source step", kSaveSourceStep,
     "53 e8 f3 a4 fa ff 59 84 c0 74 52 53 e8 0a 3a 00 00 80 3d ?? ?? ?? ?? 00 59 8b 70 50 75 0d 8b 46 08 d1 e8 a8 01"},
    // mov ebx, [esi+0x88] (the create settings), right after the "looking at source_res" log call
    {"save task settings read", kSaveSourceWalk + 0x34, "8b 9e 88 00 00 00 83 c4 18"},
    // mov ecx, [esp+4]; push type; mov eax, [ecx]; call [eax+4]; mov ecx, eax; call IsA; ret
    {"settings type check", kIsFromResourceSettings, "8b 4c 24 04 68 ?? ?? ?? ?? 8b 01 ff 50 04 8b c8 e8 ?? ?? ?? ?? c3"},
    // lea eax, [ecx + 0x6c]; ret
    {"widget text getter", kWidgetText, "8d 41 6c c3"},
    // mov ecx, [edi + 0xa8] (IP editbox); call text getter; mov eax, [eax]; sub eax, 0x10; push eax; call
    {"join direct IP read", kJoinDirectIpRead - 6, "8b 8f a8 00 00 00 e8 9f 09 1b 00 8b 00 83 e8 10 50 e8"},
    // mov ecx, [edi + 0xac] (port editbox); xor ebx, ebx; mov [ebp-4], ebx; call text getter; mov eax, [eax]
    {"join direct port read", kJoinDirectPortRead - 11, "8b 8f ac 00 00 00 33 db 89 5d fc e8 7d 09 1b 00 8b 00"},
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
    {"packet writer -> GetWantedPacketType", kWantedTypeWriteCall, kGetWantedPacketType},
    {"send scheduler -> GetWantedPacketType", kWantedTypeScheduleCall, kGetWantedPacketType},
    {"join direct IP read -> widget text", kJoinDirectIpRead, kWidgetText},
    {"join direct port read -> widget text", kJoinDirectPortRead, kWidgetText},
};

}  // namespace game
