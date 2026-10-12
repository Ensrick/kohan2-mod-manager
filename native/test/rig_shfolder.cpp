// Test rig only (tests/rig): a stand-in shfolder.dll that gives one copy of the game its own Documents folder.
//
// k2.exe finds its user folder (Documents\Kohan2, "%USERDATA%", where saves live) with LoadLibrary("shfolder.dll") +
// SHGetFolderPathW(CSIDL_PERSONAL) (0x6ffea0); shfolder.dll is not a KnownDLL, so a copy in the game folder wins.
// With K2_RIG_DOCUMENTS=<folder> in the environment this one answers CSIDL_PERSONAL with that folder, so the rig's
// client can lack the host's save and the game's own file transfer can be measured. Every other call, and every
// call without the variable, goes to the system shfolder.dll. Run-Rig.ps1 copies this DLL in for a run and deletes it
// afterwards; it never ships to players.
#include <windows.h>
#include <shlobj.h>

namespace {

using GetFolderPathW = HRESULT(WINAPI*)(HWND, int, HANDLE, DWORD, LPWSTR);
using GetFolderPathA = HRESULT(WINAPI*)(HWND, int, HANDLE, DWORD, LPSTR);

HMODULE RealShfolder() {
    static HMODULE real = [] {
        wchar_t path[MAX_PATH];
        const UINT n = GetSystemDirectoryW(path, MAX_PATH);
        if (!n || n > MAX_PATH - 16) return static_cast<HMODULE>(nullptr);
        wcscat_s(path, L"\\shfolder.dll");
        return LoadLibraryW(path);
    }();
    return real;
}

}  // namespace

extern "C" HRESULT WINAPI RigSHGetFolderPathW(HWND hwnd, int csidl, HANDLE token, DWORD flags, LPWSTR path) {
    wchar_t over[MAX_PATH];
    const DWORD n = GetEnvironmentVariableW(L"K2_RIG_DOCUMENTS", over, MAX_PATH);
    if (path && n > 0 && n < MAX_PATH && (csidl & 0xFF) == CSIDL_PERSONAL  /* CSIDL_FOLDER_MASK */) {
        if (csidl & CSIDL_FLAG_CREATE) CreateDirectoryW(over, nullptr);
        wcscpy_s(path, MAX_PATH, over);
        return S_OK;
    }
    const auto real = reinterpret_cast<GetFolderPathW>(GetProcAddress(RealShfolder(), "SHGetFolderPathW"));
    return real ? real(hwnd, csidl, token, flags, path) : E_FAIL;
}

extern "C" HRESULT WINAPI RigSHGetFolderPathA(HWND hwnd, int csidl, HANDLE token, DWORD flags, LPSTR path) {
    const auto real = reinterpret_cast<GetFolderPathA>(GetProcAddress(RealShfolder(), "SHGetFolderPathA"));
    return real ? real(hwnd, csidl, token, flags, path) : E_FAIL;
}

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
