# Starts one copy of Kohan II for the multiplayer test rig and prints its process id.
#   -Arguments   the game's command line after "k2.exe": `Name=Value` sets a UVar, `+<command>` runs a script command
#                (KKC_Application::ParseCommandLine), e.g. "+Exec Mods/rig/host.txt"
#   -Visible     show the window normally instead of minimized without focus
#   -Env         extra environment variables, e.g. @{ K2MP_RIG_JOIN = '127.0.0.1:5860' } for a client copy
# The game is started the way tests/../src/k2mod/LaunchTest.cs does: SteamAppId/SteamGameId in its environment (Steam
# must be running), minimized and not activated (SW_SHOWMINNOACTIVE through STARTUPINFO), so it never takes focus.
param(
    [string] $Game = 'C:\Program Files (x86)\Steam\steamapps\common\Kohan II',
    [string] $Arguments = '',
    [hashtable] $Env = @{},
    [switch] $Visible
)
$ErrorActionPreference = 'Stop'
if (-not ('K2Rig.Launcher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace K2Rig {
public static class Launcher {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                     string env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    const int STARTF_USESHOWWINDOW = 1;

    public static int Start(string exe, string dir, string args, bool visible, string extraEnv) {
        var env = new StringBuilder();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) {
            var k = (string)e.Key;
            if (k == "SteamAppId" || k == "SteamGameId") continue;
            env.Append(k).Append('=').Append(e.Value).Append('\0');
        }
        env.Append("SteamAppId=97130\0SteamGameId=97130\0").Append(extraEnv).Append('\0');
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), dwFlags = STARTF_USESHOWWINDOW,
                                   wShowWindow = (short)(visible ? 1 : 7) };   // 7 = SW_SHOWMINNOACTIVE
        var cmd = new StringBuilder("\"" + exe + "\"" + (args.Length > 0 ? " " + args : ""));
        PROCESS_INFORMATION pi;
        if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env.ToString(), dir,
                           ref si, out pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
}
}
'@
}
$extra = ($Env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)`0" }) -join ''
[K2Rig.Launcher]::Start((Join-Path $Game 'k2.exe'), $Game, $Arguments, [bool]$Visible, $extra)
