# Starts one copy of Kohan II for the multiplayer test rig and prints its process id.
#   -Arguments   the game's command line after "k2.exe": `Name=Value` sets a UVar, `+<command>` runs a script command
#                (KKC_Application::ParseCommandLine), e.g. "+Exec Mods/rig/host.txt"
#   -Env         extra environment variables, e.g. @{ K2MP_RIG_JOIN = '127.0.0.1:5860' } for a client copy
#   -Visible     leave the window where the game puts it (default: moved off the screen)
# The game is started the way src/k2mod/LaunchTest.cs does: SteamAppId/SteamGameId in its environment (Steam must be
# running). The window is shown without being activated (SW_SHOWNOACTIVATE), so it never takes focus, and then moved
# past the right edge of the desktop. Not minimized: a minimized window gives a windowed game a 0x0 client area and
# its renderer fails to start ("could not change resolution to 0x0").
param(
    [string] $Game = 'C:\Program Files (x86)\Steam\steamapps\common\Kohan II',
    [string] $Arguments = '',
    [hashtable] $Env = @{},
    [switch] $Visible
)
$ErrorActionPreference = 'Stop'
if (-not ('K2Rig.Launcher2' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace K2Rig {
public static class Launcher2 {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                     string env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    const int STARTF_USESHOWWINDOW = 1;
    const short SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int SM_XVIRTUALSCREEN = 76, SM_CXVIRTUALSCREEN = 78;

    public static int Start(string exe, string dir, string args, string extraEnv) {
        var env = new StringBuilder();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) {
            var k = (string)e.Key;
            if (k == "SteamAppId" || k == "SteamGameId") continue;
            env.Append(k).Append('=').Append(e.Value).Append('\0');
        }
        env.Append("SteamAppId=97130\0SteamGameId=97130\0").Append(extraEnv).Append('\0');
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), dwFlags = STARTF_USESHOWWINDOW,
                                   wShowWindow = SW_SHOWNOACTIVATE };
        var cmd = new StringBuilder("\"" + exe + "\"" + (args.Length > 0 ? " " + args : ""));
        PROCESS_INFORMATION pi;
        if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env.ToString(), dir,
                           ref si, out pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    // Moves every visible top-level window of the process past the right edge of the desktop, without activating it.
    // Returns how many windows it moved, waiting up to `seconds` for the first one to appear.
    public static int Park(int pid, int seconds) {
        int moved = 0;
        int right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do {
            EnumWindows((hwnd, l) => {
                int owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner == pid && IsWindowVisible(hwnd) &&
                    SetWindowPos(hwnd, IntPtr.Zero, right + 200, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                    moved++;
                return true;
            }, IntPtr.Zero);
            if (moved == 0) Thread.Sleep(100);
        } while (moved == 0 && DateTime.UtcNow < until);
        return moved;
    }
}
}
'@
}
$extra = ($Env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)`0" }) -join ''
$processId = [K2Rig.Launcher2]::Start((Join-Path $Game 'k2.exe'), $Game, $Arguments, $extra)
if (-not $Visible) {
    # The game may move or recreate its window while its renderer starts: keep parking it for a while.
    $null = [K2Rig.Launcher2]::Park($processId, 30)
    foreach ($i in 1..20) { Start-Sleep -Milliseconds 500; $null = [K2Rig.Launcher2]::Park($processId, 0) }
}
$processId
