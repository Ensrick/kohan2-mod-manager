// Stand-in for the game in test/loader_test.ps1: named k2.exe, imports winmm.dll, and calls timeGetTime from its own
// code section the way the game does, which is the loader's signal to start the plugins.
#include <windows.h>
#include <mmsystem.h>
#include <cstdio>

int main() {
    timeBeginPeriod(1);
    const DWORD t = timeGetTime();
    timeEndPeriod(1);
    std::printf("fake k2.exe ran, timeGetTime=%lu\n", t);
    return 0;
}
