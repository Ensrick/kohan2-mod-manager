using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace K2Mods
{
    /// <summary>A Kohan II: Kings of War install (the folder that holds k2.exe and Data.rwd).</summary>
    public sealed class GameInstall
    {
        public const string SteamAppId = "97130";

        public string Folder { get; }
        public string DataRwd => Path.Combine(Folder, "Data.rwd");
        public string StartupDir => Path.Combine(Folder, "startup");
        public string ModsDir => Path.Combine(Folder, "Mods");

        /// <summary>The game's work depot (autoexec.txt: adddepot %USERDATA%/data/ 1). Loose files here override every mod.</summary>
        public static string UserDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kohan2", "data");

        public GameInstall(string folder)
        {
            Folder = Path.GetFullPath(folder);
        }

        public static bool IsGameFolder(string folder) =>
            !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "Data.rwd")) && Directory.Exists(Path.Combine(folder, "startup"));

        public static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("k2").Length > 0; }
            catch { return false; }
        }

        /// <summary>Finds Kohan II through Steam (registry, library folders, app manifest).</summary>
        public static string Find()
        {
            foreach (var root in SteamRoots())
            {
                var libs = new List<string> { root };
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (IOException) { }
                foreach (var lib in libs)
                {
                    try
                    {
                        var acf = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
                        if (!File.Exists(acf)) continue;
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (!m.Success) continue;
                        var game = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                        if (IsGameFolder(game)) return Path.GetFullPath(game);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
                }
            }
            return null;
        }

        static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();
            void From(RegistryKey hive, string key, string value)
            {
                try
                {
                    using (var k = hive.OpenSubKey(key))
                        if (k?.GetValue(value) is string s && s.Length > 0) roots.Add(s.Replace('/', '\\'));
                }
                catch { }
            }
            From(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            From(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            From(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
            return roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Read access to the player's Data.rwd, with a lazily built line index for patches.</summary>
    public sealed class GameData : IDisposable
    {
        readonly RwdArchive arc;
        BbPatch.RefIndex index;

        public GameData(string dataRwd)
        {
            arc = new RwdArchive(dataRwd);
        }

        public string Path => arc.Path;
        public IReadOnlyList<RwdEntry> Entries => arc.Entries;
        public uint Stamp => arc.Stamp;
        public long Length => arc.Length;

        /// <summary>Identifies this exact Data.rwd: mods built from patches are rebuilt when it changes.</summary>
        public string Fingerprint => $"{arc.Length}:{arc.Stamp:x8}:{arc.Entries.Count}";

        public byte[] Get(string name) => arc.Read(name);

        public string Canon(string name) => arc.Find(name)?.Name;

        public bool Contains(string name) => arc.Find(name) != null;

        public static bool IsTextName(string name)
        {
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext == ".tgi" || ext == ".txt" || ext == ".lst" || ext == ".ini" || ext == ".csv";
        }

        /// <summary>Every game text file, indexed so copied blocks become references.</summary>
        public BbPatch.RefIndex Index
        {
            get
            {
                if (index != null) return index;
                var ix = new BbPatch.RefIndex();
                foreach (var e in arc.Entries)
                {
                    var ext = System.IO.Path.GetExtension(e.Name).ToLowerInvariant();
                    if (ext == ".tgi" || ext == ".txt") ix.Add(e.Name, arc.Read(e));
                }
                return index = ix;
            }
        }

        static HashSet<string> SubstantiveSet(List<string> norms) =>
            new HashSet<string>(norms.Where(BbPatch.IsSubstantive), StringComparer.Ordinal);

        /// <summary>
        /// For a new text file: the game text file it most resembles (Jaccard similarity of
        /// substantive lines, at least 0.25), used as its patch base. Same rule as
        /// kohan2-battleborn tools/bb_import.py.
        /// </summary>
        public string BestTextBase(byte[] data)
        {
            if (sets == null)
            {
                sets = new List<KeyValuePair<string, HashSet<string>>>();
                foreach (var f in Index.Files) sets.Add(new KeyValuePair<string, HashSet<string>>(f.Key, SubstantiveSet(f.Value.Norms)));
            }
            var mine = SubstantiveSet(BbPatch.Split(data).Bodies.Select(BbPatch.Strip).ToList());
            string best = null;
            double score = 0;
            foreach (var s in sets)
            {
                int inter = mine.Count(s.Value.Contains);
                if (inter == 0) continue;
                double j = (double)inter / (mine.Count + s.Value.Count - inter);
                if (j > score) { best = s.Key; score = j; }
            }
            return score >= 0.25 ? best : null;
        }

        List<KeyValuePair<string, HashSet<string>>> sets;

        /// <summary>Same-extension game files of exactly this size (candidates for a delta base).</summary>
        public IEnumerable<RwdEntry> SameSize(string name, long size)
        {
            var ext = System.IO.Path.GetExtension(name);
            return arc.Entries.Where(e => e.Size == size && string.Equals(System.IO.Path.GetExtension(e.Name), ext, StringComparison.OrdinalIgnoreCase));
        }

        public byte[] Read(RwdEntry e) => arc.Read(e);

        public void Dispose() => arc.Dispose();
    }

    static class ByteArrayExtensions
    {
        public static bool AsSpanEquals(this byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
