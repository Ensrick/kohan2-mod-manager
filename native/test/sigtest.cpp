// sigtest <k2mp.dll> <decrypted k2.exe>
// Maps the exe as an image without running it (DONT_RESOLVE_DLL_REFERENCES: no imports, no entry point), at a
// relocated address because this test occupies k2.exe's preferred base, and checks k2mp's signatures against it.
// The Steam k2.exe is encrypted on disk, so the input is a copy decrypted for analysis; exit code 0 = all sites match.
#include <windows.h>
#include <cstdint>
#include <cstdio>

using CheckFn = int(__cdecl*)(uintptr_t, char*, int);

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: sigtest <k2mp.dll> <decrypted k2.exe>\n");
        return 2;
    }
    const HMODULE plugin = LoadLibraryW(argv[1]);
    const auto check = plugin ? reinterpret_cast<CheckFn>(GetProcAddress(plugin, "K2Mp_CheckBuild")) : nullptr;
    if (!check) {
        std::fprintf(stderr, "cannot load K2Mp_CheckBuild from %ls\n", argv[1]);
        return 2;
    }
    const HMODULE exe = LoadLibraryExW(argv[2], nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!exe) {
        std::fprintf(stderr, "cannot map %ls (error %lu)\n", argv[2], GetLastError());
        return 2;
    }
    char report[8192] = {};
    const int bad = check(reinterpret_cast<uintptr_t>(exe), report, sizeof(report));
    std::printf("image at %p\n%s%d site(s) differ\n", static_cast<void*>(exe), report, bad);
    return bad == 0 ? 0 : 1;
}
