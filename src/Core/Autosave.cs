using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace K2Mods
{
    /// <summary>
    /// Multiplayer autosave, done by the Multiplayer Fixes plugin (native/k2mp/autosave.h): while this PC hosts a running
    /// multiplayer match, the plugin saves it every N minutes into "MP Autosave A" and "MP Autosave B" in turn, with the
    /// game's own GameSave command. Every player's game pauses for a moment, as for any multiplayer save; players whose
    /// plugin has save copies on also write "MP Autosave A (copy)" and so on, so anyone can host the match again.
    /// Settings: &lt;game&gt;\Mods\k2mp.ini, [features] autosave = 1 and [autosave] minutes = N. Turning autosave on turns
    /// the plugin on.
    ///
    /// Manager 0.1.0-beta.2 to beta.8 did this with a script loop instead (&lt;game&gt;\user.txt starting
    /// Mods\mp_autosave.txt). Its first `WaitState game` throws about a minute after the game starts when no match is
    /// running (test rig, 2026-10-12), so that loop is removed wherever it is found (<see cref="RemoveLegacy"/>).
    /// </summary>
    public static class MpAutosave
    {
        public const int DefaultMinutes = 5;
        public const string PluginId = "k2mp";
        public static readonly string[] Slots = { "MP Autosave A", "MP Autosave B" };
        const string LegacyScriptName = "mp_autosave.txt";
        const string LegacyBegin = "# >>> Kohan II Mod Manager: multiplayer autosave. Turn it off in K2ModManager.exe.";
        const string LegacyEnd = "# <<< Kohan II Mod Manager";

        public static string IniPath(GameInstall g) => Path.Combine(g.ModsDir, "k2mp.ini");
        static string LegacyUserScript(GameInstall g) => Path.Combine(g.Folder, "user.txt");
        static string LegacyLoop(GameInstall g) => Path.Combine(g.ModsDir, LegacyScriptName);

        /// <summary>The save interval in minutes, or null when autosave is off (or the plugin is).</summary>
        public static int? Minutes(GameInstall g)
        {
            var ini = IniPath(g);
            if (!File.Exists(ini) || ReadInt(ini, "features", "autosave", 0) != 1) return null;
            var plugin = Plugins.Find(g, PluginId);
            if (plugin == null || !plugin.Enabled) return null;
            return Math.Min(120, Math.Max(1, ReadInt(ini, "autosave", "minutes", DefaultMinutes)));
        }

        /// <summary>Turns autosave on (or changes its interval) and the plugin with it. From the next game start.</summary>
        public static List<string> Enable(GameInstall g, int minutes)
        {
            if (minutes < 1 || minutes > 120) throw new ModException("The autosave interval must be 1 to 120 minutes.");
            var plugin = Plugins.Find(g, PluginId)
                ?? throw new ModException("This build of the manager does not include the Multiplayer Fixes plugin, which does the autosave.");
            var o = new List<string>();
            if (!plugin.Enabled) o.AddRange(Plugins.Enable(g, plugin));
            Directory.CreateDirectory(g.ModsDir);
            Write(IniPath(g), "features", "autosave", "1");
            Write(IniPath(g), "autosave", "minutes", minutes.ToString());
            if (RemoveLegacy(g)) o.Add("Removed the old autosave script (user.txt, Mods\\mp_autosave.txt): it could crash the game.");
            return o;
        }

        /// <summary>Turns autosave off. The plugin stays as it is.</summary>
        public static List<string> Disable(GameInstall g)
        {
            var o = new List<string>();
            if (File.Exists(IniPath(g))) Write(IniPath(g), "features", "autosave", "0");
            if (RemoveLegacy(g)) o.Add("Removed the old autosave script (user.txt, Mods\\mp_autosave.txt): it could crash the game.");
            return o;
        }

        /// <summary>
        /// Takes out the script loop of manager beta.2 to beta.8: the manager's block in user.txt (the file is deleted
        /// when nothing else is in it) and Mods\mp_autosave.txt. True when anything was there.
        /// </summary>
        public static bool RemoveLegacy(GameInstall g)
        {
            bool found = false;
            var path = LegacyUserScript(g);
            if (File.Exists(path))
            {
                var user = ReadLines(path);
                var lines = WithoutBlock(user.Lines);
                if (user.Lines.Contains(LegacyBegin))
                {
                    found = true;
                    if (lines.All(string.IsNullOrWhiteSpace)) File.Delete(path);
                    else ModLibrary.WriteText(path, string.Join("\r\n", lines) + "\r\n", user.Encoding);
                }
            }
            if (File.Exists(LegacyLoop(g)))
            {
                File.Delete(LegacyLoop(g));
                found = true;
            }
            return found;
        }

        /// <summary>The script loop from beta.2 to beta.8 is still installed and had autosave on; its interval.</summary>
        public static int? LegacyMinutes(GameInstall g)
        {
            var loop = LegacyLoop(g);
            if (!File.Exists(loop)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(loop), @"^Wait\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            return m.Success ? Math.Min(120, Math.Max(1, int.Parse(m.Groups[1].Value) / 60)) : DefaultMinutes;
        }

        /// <summary>
        /// Moves an installed script loop to the plugin: same interval, script removed. Messages for the player, or
        /// none when there was nothing to move.
        /// </summary>
        public static List<string> MigrateLegacy(GameInstall g)
        {
            var minutes = LegacyMinutes(g);
            if (minutes == null) return RemoveLegacy(g) ? new List<string> { "Removed an old autosave script that could crash the game." } : new List<string>();
            var o = Enable(g, minutes.Value);
            o.Insert(0, $"Multiplayer autosave now runs in the Multiplayer Fixes plugin (every {minutes} min, as before).");
            return o;
        }

        /// <summary>The newest multiplayer autosave on this PC (the host's file or a copy), or null.</summary>
        public static FileInfo Latest() =>
            Slots.SelectMany(s => new[] { s + ".RSG", s + " (copy).RSG" })
                 .Select(n => new FileInfo(Path.Combine(GameInstall.UserDataDir, "Save", n)))
                 .Where(f => f.Exists)
                 .OrderByDescending(f => f.LastWriteTimeUtc)
                 .FirstOrDefault();

        // ---- Mods\k2mp.ini: the plugin reads it with GetPrivateProfileInt, so the manager writes it the same way.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WritePrivateProfileString(string section, string key, string value, string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileInt(string section, string key, int fallback, string path);

        static int ReadInt(string path, string section, string key, int fallback) => GetPrivateProfileInt(section, key, fallback, path);

        static void Write(string path, string section, string key, string value)
        {
            if (!WritePrivateProfileString(section, key, value, path))
                throw new ModException($"Could not write {Path.GetFileName(path)} (error {Marshal.GetLastWin32Error()}).");
        }

        static List<string> WithoutBlock(List<string> lines)
        {
            var o = new List<string>();
            bool inside = false;
            foreach (var l in lines)
            {
                if (l == LegacyBegin) inside = true;
                else if (inside && l == LegacyEnd) inside = false;
                else if (!inside) o.Add(l);
            }
            while (o.Count > 0 && string.IsNullOrWhiteSpace(o[o.Count - 1])) o.RemoveAt(o.Count - 1);
            return o;
        }

        /// <summary>A text file's lines and encoding (kept when the manager rewrites a file the player may have written).</summary>
        static (List<string> Lines, Encoding Encoding) ReadLines(string path)
        {
            if (!File.Exists(path)) return (new List<string>(), Encoding.ASCII);
            using (var r = new StreamReader(path, Encoding.Default, detectEncodingFromByteOrderMarks: true))
            {
                var text = r.ReadToEnd();
                var enc = text.All(c => c < 128) && !(r.CurrentEncoding is UnicodeEncoding) ? Encoding.ASCII : r.CurrentEncoding;
                return (text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList(), enc);
            }
        }
    }
}
