using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace K2Mods
{
    /// <summary>
    /// Multiplayer autosave, built only from the game's own script commands (no change to k2.exe or its data,
    /// nothing in the multiplayer checksums):
    ///   &lt;game&gt;\user.txt              the game runs it once after start-up; the manager's block in it starts the loop
    ///   &lt;game&gt;\Mods\mp_autosave.txt  the loop: wait for a match, wait N minutes, GameSave into slot A, the same into
    ///                                slot B, then Exec itself again
    /// Exec gives a script its own pipeline, the only place Wait is allowed, so the loop holds up nothing else.
    /// GameSave is a network order: the game carries it out only when it comes from the host (KKC_Session::GameSave
    /// ignores anyone else's), and then the host writes the save while every player's game pauses for a moment.
    /// So every player can have this on; whoever is host when the time comes saves. Saves land in
    /// Documents\Kohan2\data\Save\, the folder the host picks "Load saved game" from. Other players' games do not
    /// write a copy (the client branch of KKC_Session::GameSave only pauses); a joining player without the file
    /// should get it through the game's own file transfer (KKC_Session::SendUpdateMapStatus asks for it), not yet
    /// seen working. See docs/MP_AUTOSAVE.md.
    /// </summary>
    public static class MpAutosave
    {
        public const int DefaultMinutes = 5;
        public const string ScriptName = "mp_autosave.txt";
        public static readonly string[] Slots = { "MP Autosave A", "MP Autosave B" };
        const string Begin = "# >>> Kohan II Mod Manager: multiplayer autosave. Turn it off in K2ModManager.exe.";
        const string End = "# <<< Kohan II Mod Manager";

        public static string UserScriptPath(GameInstall g) => Path.Combine(g.Folder, "user.txt");
        public static string LoopPath(GameInstall g) => Path.Combine(g.ModsDir, ScriptName);

        /// <summary>The save interval in minutes, or null when multiplayer autosave is off.</summary>
        public static int? Minutes(GameInstall g)
        {
            var user = UserScriptPath(g);
            var loop = LoopPath(g);
            if (!File.Exists(user) || !File.Exists(loop) || !ReadLines(user).Lines.Contains(Begin)) return null;
            var m = Regex.Match(File.ReadAllText(loop), @"^Wait\s+(\d+)", RegexOptions.Multiline);
            return m.Success ? Math.Max(1, int.Parse(m.Groups[1].Value) / 60) : DefaultMinutes;
        }

        /// <summary>Turns multiplayer autosave on (or changes its interval). Takes effect the next time the game starts.</summary>
        public static void Enable(GameInstall g, int minutes)
        {
            if (minutes < 1 || minutes > 120) throw new ModException("The autosave interval must be 1 to 120 minutes.");
            Directory.CreateDirectory(g.ModsDir);
            ModLibrary.WriteText(LoopPath(g), LoopScript(minutes), Encoding.ASCII);
            var user = ReadLines(UserScriptPath(g));
            var lines = WithoutBlock(user.Lines);
            lines.AddRange(new[] { Begin, $"Exec Mods/{ScriptName}", End });
            ModLibrary.WriteText(UserScriptPath(g), string.Join("\r\n", lines) + "\r\n", user.Encoding);
        }

        /// <summary>Turns it off: takes the manager's block out of user.txt (deleting the file if nothing else is in it) and deletes the loop.</summary>
        public static void Disable(GameInstall g)
        {
            var path = UserScriptPath(g);
            if (File.Exists(path))
            {
                var user = ReadLines(path);
                var lines = WithoutBlock(user.Lines);
                if (lines.All(string.IsNullOrWhiteSpace)) File.Delete(path);
                else if (lines.Count != user.Lines.Count) ModLibrary.WriteText(path, string.Join("\r\n", lines) + "\r\n", user.Encoding);
            }
            if (File.Exists(LoopPath(g))) File.Delete(LoopPath(g));
        }

        /// <summary>The newest multiplayer autosave on this PC, or null.</summary>
        public static FileInfo Latest() =>
            Slots.Select(s => new FileInfo(Path.Combine(GameInstall.UserDataDir, "Save", s + ".RSG")))
                 .Where(f => f.Exists)
                 .OrderByDescending(f => f.LastWriteTimeUtc)
                 .FirstOrDefault();

        static string LoopScript(int minutes)
        {
            int seconds = minutes * 60;
            var lines = new List<string>
            {
                ModLibrary.ScriptMarker + ": multiplayer autosave. The manager rewrites this file; turn it off there.",
                "# user.txt starts it with Exec, so it runs in its own script pipeline and its waits hold up nothing else.",
                "# Every player's game runs it, but the game only carries out the host's GameSave: whoever is host",
                "# saves, and every player's game pauses for a moment while it does.",
                "WaitState game",
            };
            foreach (var slot in Slots)
                lines.AddRange(new[] { $"Wait {seconds}", "WaitState game", $"GameSave \"Save/{slot}.RSG\"" });
            lines.Add($"Exec Mods/{ScriptName}");
            return string.Join("\r\n", lines) + "\r\n";
        }

        static List<string> WithoutBlock(List<string> lines)
        {
            var o = new List<string>();
            bool inside = false;
            foreach (var l in lines)
            {
                if (l == Begin) inside = true;
                else if (inside && l == End) inside = false;
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
