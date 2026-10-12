using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using K2Mods;

namespace K2ModCli
{
    /// <summary>
    /// Starts Kohan II, waits until it reaches the main menu or reports an error, then closes it.
    /// By default the game starts minimized without taking focus. Steam must be running; the app id is passed in the
    /// environment (SteamAppId/SteamGameId), so nothing is written into the game folder.
    /// The result comes from the game's own log (Logs\log-N.log):
    ///   "(init time) command line scripts" + 10 s without an ERROR line, or
    ///   "connect MainMenuInterface"   main menu reached (every data file has loaded)
    ///   "ERROR:" / "An error has occurred:"   the engine's fatal-error handler ran
    /// </summary>
    static class LaunchTest
    {
        public sealed class Result
        {
            public string Outcome;          // "main menu", "error", "exited", "timeout"
            public string LogPath;
            public double Seconds;
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
            public bool Ok => Outcome == "main menu";
        }

        const string MenuMarker = "connect MainMenuInterface";
        // The engine buffers its log: "connect MainMenuInterface" can sit unwritten until the game
        // exits (log-251: menu at 39.8 s, on disk only at quit, 268 s). This line comes right after
        // the main menu is built, with ~400 lines behind it, so it reaches the disk at once.
        const string ReadyMarker = "(init time) command line scripts";
        const string ErrorMarker = "An error has occurred:";
        static readonly Regex ErrorLine = new Regex(@"^ERROR:", RegexOptions.Multiline);
        static readonly Regex LogName =new Regex(@"^log-(\d+)(?:-\w+)?\.log$", RegexOptions.IgnoreCase);

        public static Result Run(GameInstall game, int timeoutSeconds, bool visible, Action<string> say)
        {
            if (GameInstall.IsGameRunning()) throw new ModException("Kohan II is already running. Close it first.");
            if (Process.GetProcessesByName("steam").Length == 0)
                throw new ModException("Steam is not running. Start Steam first (the game needs it for its Steam login).");
            var logs = Path.Combine(game.Folder, "Logs");
            int before = LastLogNumber(logs);
            var exe = Path.Combine(game.Folder, "k2.exe");
            var clock = Stopwatch.StartNew();
            int pid = visible ? StartVisible(exe, game.Folder) : StartMinimized(exe, game.Folder);
            say($"started k2.exe (pid {pid}){(visible ? "" : " minimized")}; waiting up to {timeoutSeconds} s for the main menu");

            var result = new Result();
            var errorSeen = new Stopwatch();
            var ready = new Stopwatch();
            string log = null;
            try
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    log = log ?? NewLog(logs, before);
                    string text = log != null ? ReadShared(log) : "";
                    bool alive = IsAlive(pid);
                    if (text.Contains(MenuMarker)) { result.Outcome = "main menu"; break; }
                    if (text.Contains(ReadyMarker) && !ErrorLine.IsMatch(text))
                    {
                        if (ready.Elapsed.TotalSeconds == 0) ready.Start();
                        else if (ready.Elapsed.TotalSeconds > 10) { result.Outcome = "main menu"; break; }
                    }
                    if (text.Contains(ErrorMarker)) { result.Outcome = "error"; break; }
                    // clean runs log no ERROR lines; after one the engine sits in its error box
                    if (ErrorLine.IsMatch(text))
                    {
                        if (errorSeen.Elapsed.TotalSeconds == 0) errorSeen.Start();
                        else if (errorSeen.Elapsed.TotalSeconds > 5) { result.Outcome = "error"; break; }
                    }
                    if (!alive)
                    {
                        // the engine renames the log on exit (-ok / -error / -crash); give it a moment
                        Thread.Sleep(1500);
                        log = FinalLog(logs, before) ?? log;
                        text = log != null ? ReadShared(log) : "";
                        result.Outcome = text.Contains(MenuMarker) ? "main menu" : text.Contains(ErrorMarker) ? "error" : "exited";
                        break;
                    }
                    if (clock.Elapsed.TotalSeconds > timeoutSeconds) { result.Outcome = "timeout"; break; }
                }
            }
            finally
            {
                Stop(pid);
            }
            result.Seconds = clock.Elapsed.TotalSeconds;
            Thread.Sleep(1500);
            result.LogPath = FinalLog(logs, before) ?? log;
            if (result.LogPath != null) Parse(ReadShared(result.LogPath), result);
            return result;
        }

        static void Parse(string text, Result r)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var s = lines[i].Trim();
                if (s.StartsWith("ERROR:"))
                {
                    var msg = s.Substring(6).Trim();
                    if (msg.Length == 0 && i + 1 < lines.Length) msg = lines[i + 1].Trim();
                    if (msg.Length > 0 && !r.Errors.Contains(msg)) r.Errors.Add(msg);
                }
                else if (s.StartsWith("WARNING:"))
                {
                    var msg = s.Substring(8).Trim();
                    if (!r.Warnings.Contains(msg)) r.Warnings.Add(msg);
                }
            }
        }

        static int LastLogNumber(string logs)
        {
            if (!Directory.Exists(logs)) return 0;
            return Directory.GetFiles(logs, "log-*.log")
                .Select(f => LogName.Match(Path.GetFileName(f)))
                .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        }

        static string NewLog(string logs, int before)
        {
            if (!Directory.Exists(logs)) return null;
            return Directory.GetFiles(logs, "log-*.log")
                .Select(f => (f, m: LogName.Match(Path.GetFileName(f))))
                .Where(x => x.m.Success && int.Parse(x.m.Groups[1].Value) > before)
                .OrderBy(x => int.Parse(x.m.Groups[1].Value)).Select(x => x.f).FirstOrDefault();
        }

        static string FinalLog(string logs, int before) => NewLog(logs, before);

        static string ReadShared(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.GetEncoding(1252)))
                        return sr.ReadToEnd();
                }
                catch (FileNotFoundException) { return ""; }
                catch (IOException) { Thread.Sleep(200); }
            }
            return "";
        }

        static bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }
        }

        static void Stop(int pid)
        {
            foreach (var p in Process.GetProcessesByName("k2"))
            {
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(10000); } }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }

        static Dictionary<string, string> GameEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string)e.Value;
            env["SteamAppId"] = GameInstall.SteamAppId;
            env["SteamGameId"] = GameInstall.SteamAppId;
            return env;
        }

        static int StartVisible(string exe, string dir)
        {
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false };
            foreach (var kv in GameEnvironment()) psi.EnvironmentVariables[kv.Key] = kv.Value;
            return Process.Start(psi).Id;
        }

        // ---------------------------------------------------------------- private desktop

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
            IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const int STARTF_USESHOWWINDOW = 0x1;
        const short SW_SHOWMINNOACTIVE = 7;

        // A private desktop (CreateDesktop) does not work: the renderer finds no HAL device there
        // and stops with "No adapters available" (log-249). The game reads its show state from
        // STARTUPINFO (it logs "nCmdShow 10" = SW_SHOWDEFAULT), so it starts minimized instead.
        static int StartMinimized(string exe, string dir)
        {
            var block = new StringBuilder();
            foreach (var kv in GameEnvironment().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            block.Append('\0');
            var envPtr = Marshal.StringToHGlobalUni(block.ToString());
            try
            {
                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                    dwFlags = STARTF_USESHOWWINDOW,
                    wShowWindow = SW_SHOWMINNOACTIVE,
                };
                var cmd = new StringBuilder("\"" + exe + "\"");
                if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, envPtr, dir, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally
            {
                Marshal.FreeHGlobal(envPtr);
            }
        }
    }
}
