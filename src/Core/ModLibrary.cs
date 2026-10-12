using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace K2Mods
{
    public sealed class InstalledMod
    {
        public ModManifest Manifest;
        public bool Enabled;
        /// <summary>Mods\, or Mods\&lt;id&gt;\ for a mod still in the folder of a manager build before 2026-10-11.</summary>
        public string Folder;
        public bool InOwnFolder;
        public string BuiltAgainst;
        public string Problem;
        List<string> files;

        public string Id => Manifest.Id;
        public string RwdPath => Path.Combine(Folder, Id + ".rwd");
        public string PackagePath => Path.Combine(Folder, Id + ".k2mod");
        public bool HasPackage => File.Exists(PackagePath);
        public bool IsInstalled => File.Exists(RwdPath);

        /// <summary>Path the startup script mounts, relative to the game folder.</summary>
        public string MountPath => InOwnFolder ? "Mods/" + Id + "/" + Id + ".rwd" : "Mods/" + Id + ".rwd";

        public List<string> FileNames
        {
            get
            {
                if (files != null) return files;
                try
                {
                    using (var a = new RwdArchive(RwdPath)) files = a.Entries.Select(e => e.Name).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    files = new List<string>();
                }
                return files;
            }
        }

        internal void ForgetFiles() => files = null;
    }

    /// <summary>Two enabled mods that replace the same files; the later one in the load order wins.</summary>
    public sealed class FileConflict
    {
        public InstalledMod Loser, Winner;
        public List<string> Files;
    }

    /// <summary>A startup script outside the manager that mounts extra data (adddepot / addmod).</summary>
    public sealed class ExternalScript
    {
        public string Path;
        public bool Disabled;
        public List<string> Mounts;
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// The installed mods of one game folder:
    ///   &lt;game&gt;\Mods\&lt;id&gt;.rwd            the built archive the game mounts
    ///   &lt;game&gt;\Mods\&lt;id&gt;.k2mod          the package it was built from (rebuilt after game updates)
    ///   &lt;game&gt;\Mods\mods.json            load order, on/off state and each mod's mod.json
    /// Manager builds before 2026-10-11 kept each mod in Mods\&lt;id&gt;\ with its own mod.json;
    /// Load moves those up into Mods\ while the game is not running.
    ///   &lt;game&gt;\startup\autoexec_mods.txt  one 'addmod' line per enabled mod, in load order
    /// k2.exe runs every startup\autoexec*.txt after autoexec.txt has mounted Data.rwd. Mods are
    /// added at priority 0, where a later archive overrides an earlier one, so the last mod in
    /// the list wins any file two mods both change. The Documents\Kohan2\data folder
    /// (priority 1) still overrides every mod.
    /// </summary>
    public sealed class ModLibrary
    {
        public const string ScriptName = "autoexec_mods.txt";
        public const string ScriptMarker = "# Kohan II Mod Manager";
        const string StateName = "mods.json";

        public GameInstall Game { get; }
        public List<InstalledMod> Mods { get; } = new List<InstalledMod>();
        public string ScriptPath => Path.Combine(Game.StartupDir, ScriptName);
        string StatePath => Path.Combine(Game.ModsDir, StateName);
        string DisabledScriptsDir => Path.Combine(Game.StartupDir, "disabled");

        static readonly Mutex Gate = new Mutex(false, @"Local\K2ModManager.Library");

        ModLibrary(GameInstall game)
        {
            Game = game;
        }

        /// <summary>Runs a change to the mod list while no other manager window is changing it.</summary>
        public static T Locked<T>(Func<T> action)
        {
            bool held = false;
            try
            {
                try { held = Gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { held = true; }
                return action();
            }
            finally
            {
                if (held) Gate.ReleaseMutex();
            }
        }

        public static ModLibrary Load(GameInstall game)
        {
            var lib = new ModLibrary(game);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(lib.StatePath))
            {
                var state = Json.ParseObject(File.ReadAllText(lib.StatePath, Encoding.UTF8));
                foreach (var row in Json.ObjList(state, "mods"))
                {
                    var m = lib.Open(Json.Str(row, "id", ""), row);
                    if (m == null || !known.Add(m.Id)) continue;
                    m.Enabled = Json.Bool(row, "enabled");
                    m.BuiltAgainst = Json.Str(row, "builtAgainst");
                    lib.Mods.Add(m);
                }
            }
            // mods missing from mods.json (lost, or copied in by hand): older per-mod folders, then packages, then archives;
            // they start off unless a startup script of their own was already loading them
            bool adopted = false;
            if (Directory.Exists(game.ModsDir))
            {
                foreach (var dir in Directory.GetDirectories(game.ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var m = lib.Open(Path.GetFileName(dir).ToLowerInvariant(), null);
                    if (m != null && known.Add(m.Id)) lib.Mods.Add(m);
                }
                foreach (var ext in new[] { ".k2mod", ".rwd" })
                    foreach (var f in Directory.GetFiles(game.ModsDir).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (known.Contains(Path.GetFileNameWithoutExtension(f))) continue;   // a mod's own files
                        var m = lib.Adopt(f);
                        if (m != null && known.Add(m.Id))
                        {
                            lib.Mods.Add(m);
                            adopted = true;
                        }
                    }
            }
            if (adopted) Locked(() => { lib.Save(); return 0; });   // keeps the names taken from file names
            lib.MoveOutOfFolders();
            return lib;
        }

        /// <summary>
        /// A .k2mod or .rwd put into Mods\ by hand. A package brings its own mod.json; an archive gets its
        /// name and version from its file name ("Immortals_V2.1.rwd" is Immortals 2.1). The file is renamed
        /// to &lt;id&gt;.k2mod or &lt;id&gt;.rwd, the name the startup script mounts, unless that name is taken.
        /// </summary>
        InstalledMod Adopt(string path)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                ModManifest man;
                if (ext == ".k2mod") man = ModPackage.ReadManifest(path);
                else
                {
                    var (name, version) = NameAndVersion(Path.GetFileNameWithoutExtension(path));
                    man = new ModManifest { Name = name, Version = version, Id = ModManifest.MakeId(name) };
                }
                man.Validate();
                if (Find(man.Id) != null) return null;
                // installed by hand with its own startup script: the manager takes that script over (turns it off and
                // ticks the mod, so the game loads the same thing). A script that also loads other files is left alone.
                var scripts = ExternalScripts().Where(s => !s.Disabled && s.Mounts.Any(p => IsMountOf(p, path))).ToList();
                if (scripts.Count > 0 && (GameInstall.IsGameRunning() || scripts.Any(s => s.Mounts.Count > 1))) return null;
                var target = Path.Combine(Game.ModsDir, man.Id + ext);
                if (!string.Equals(Path.GetFileName(path), Path.GetFileName(target), StringComparison.Ordinal))
                {
                    if (GameInstall.IsGameRunning()) return null;
                    if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(target)) return null;
                        File.Move(path, target);
                    }
                    else
                    {
                        File.Move(path, target + ".rename");   // only the letter case differs
                        File.Move(target + ".rename", target);
                    }
                }
                if (ext == ".rwd") using (new RwdArchive(target)) { }   // a real archive, not a damaged download
                var m = new InstalledMod { Manifest = man, Folder = Game.ModsDir };
                if (!m.IsInstalled && !m.HasPackage) return null;
                foreach (var s in scripts) SetExternal(s, false);
                m.Enabled = scripts.Count > 0;
                return m;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is ModException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Whether a startup script's addmod/adddepot path (relative to the game folder) is this file.</summary>
        bool IsMountOf(string mount, string file)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(Game.Folder, mount.Trim('"').Replace('/', '\\')));
                return string.Equals(full, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>The mod with this id: its mod.json comes from its mods.json row, its package, or (an archive alone) its file name.</summary>
        InstalledMod Open(string id, Dictionary<string, object> row)
        {
            if (!ModManifest.IsValidId(id)) return null;
            try
            {
                InstalledMod m;
                var own = Path.Combine(Game.ModsDir, id);
                if (File.Exists(Path.Combine(own, "mod.json")))
                    m = new InstalledMod { Manifest = ModManifest.Read(Path.Combine(own, "mod.json")), Folder = own, InOwnFolder = true };
                else
                {
                    m = new InstalledMod { Folder = Game.ModsDir };
                    var pkg = Path.Combine(Game.ModsDir, id + ".k2mod");
                    m.Manifest = row != null && row.TryGetValue("manifest", out var o) && o is Dictionary<string, object> d ? ModManifest.FromJson(d)
                        : File.Exists(pkg) ? ModPackage.ReadManifest(pkg)
                        : new ModManifest { Id = id, Name = id };
                }
                if (!string.Equals(m.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)) return null;
                return m.IsInstalled || m.HasPackage ? m : null;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is ModException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Moves mods out of the Mods\&lt;id&gt;\ folders of manager builds before 2026-10-11, so every .rwd sits
        /// straight in Mods\. Skipped while the game runs (it holds mounted archives open); a mod that cannot
        /// move stays where it is and keeps working, and is tried again next time.
        /// </summary>
        void MoveOutOfFolders()
        {
            var old = Mods.Where(m => m.InOwnFolder).ToList();
            if (old.Count == 0 || GameInstall.IsGameRunning()) return;
            Locked(() =>
            {
                foreach (var m in old)
                {
                    var names = new[] { m.Id + ".k2mod", m.Id + ".rwd" };
                    // never overwrite a file already in Mods\ (one moved by an earlier, interrupted try is fine: its source is gone)
                    if (names.Any(n => File.Exists(Path.Combine(m.Folder, n)) && File.Exists(Path.Combine(Game.ModsDir, n)))) continue;
                    try
                    {
                        foreach (var n in names)
                            if (File.Exists(Path.Combine(m.Folder, n))) File.Move(Path.Combine(m.Folder, n), Path.Combine(Game.ModsDir, n));
                        File.Delete(Path.Combine(m.Folder, "mod.json"));
                        if (!Directory.EnumerateFileSystemEntries(m.Folder).Any()) Directory.Delete(m.Folder);
                        m.Folder = Game.ModsDir;
                        m.InOwnFolder = false;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                Save();
                return 0;
            });
        }

        public InstalledMod Find(string id) => Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Writes mods.json and the startup script.</summary>
        public void Save()
        {
            Directory.CreateDirectory(Game.ModsDir);
            var state = new Dictionary<string, object>
            {
                ["format"] = 2L,   // 2: each row carries the mod's mod.json ("manifest"); 1 kept it in Mods\<id>\mod.json
                ["mods"] = Mods.Select(m => (object)new Dictionary<string, object>
                {
                    ["id"] = m.Id,
                    ["enabled"] = m.Enabled,
                    ["builtAgainst"] = m.BuiltAgainst,
                    ["manifest"] = m.Manifest.ToJson(),
                }).ToList(),
            };
            WriteText(StatePath, Json.Write(state) + "\n");
            var lines = new List<string>
            {
                ScriptMarker + ": this file is rewritten whenever the mod list changes.",
                "# Turn mods on and off in K2ModManager.exe instead of editing it. Later lines override earlier ones.",
            };
            lines.AddRange(Mods.Where(m => m.Enabled && m.IsInstalled && m.Problem == null).Select(m => "addmod " + m.MountPath));
            Directory.CreateDirectory(Game.StartupDir);
            WriteText(ScriptPath, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        }

        internal static void WriteText(string path, string text, Encoding enc = null)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, enc ?? new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // ------------------------------------------------------------------ installing

        public sealed class InstallResult
        {
            public InstalledMod Mod;
            public bool Upgraded;
            public string OldVersion;
            public readonly List<string> Messages = new List<string>();
        }

        /// <summary>
        /// Installs a .k2mod package, a ready-made .rwd archive, or a .zip / folder of loose mod
        /// files, then turns it on. Mods it is declared incompatible with are turned off.
        /// </summary>
        public InstallResult Install(string source, Func<GameData> openGame, Action<string> progress = null)
        {
            var r = new InstallResult();
            ModPackage pkg = null;
            string rwdSource = null;
            var ext = Path.GetExtension(source).ToLowerInvariant();
            progress?.Invoke("Reading " + Path.GetFileName(source));
            if (ext == ".rwd")
            {
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(Game.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new ModException($"{Path.GetFileName(source)} is one of Kohan II's own archives, not a mod");
                using (new RwdArchive(source)) { }
                rwdSource = source;
            }
            else if (ext == ".k2mod" || IsPackageSource(source))
                pkg = ModPackage.Load(source);
            else
                pkg = LoosePackage(source, openGame(), r.Messages);

            var (rwdName, rwdVersion) = NameAndVersion(Path.GetFileNameWithoutExtension(source));   // "Immortals_V2.1.rwd" is Immortals 2.1
            var man = pkg?.Manifest ?? new ModManifest
            {
                Name = rwdName,
                Version = rwdVersion,
                Id = ModManifest.MakeId(rwdName),
            };
            man.Validate();
            var existing = Find(man.Id);
            if (existing != null)
            {
                r.Upgraded = true;
                r.OldVersion = existing.Manifest.Version;
            }
            Directory.CreateDirectory(Game.ModsDir);
            var mod = existing ?? new InstalledMod { Folder = Game.ModsDir };
            mod.Manifest = man;
            mod.Problem = null;
            mod.ForgetFiles();

            if (pkg != null)
            {
                progress?.Invoke("Building " + man.Title);
                var game = pkg.NeedsGameData ? openGame() : null;
                var files = pkg.Build(game);
                RwdArchive.Write(mod.RwdPath, files);
                mod.BuiltAgainst = game?.Fingerprint;
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(mod.PackagePath), StringComparison.OrdinalIgnoreCase))
                    pkg.Save(mod.PackagePath);
            }
            else
            {
                var tmp = mod.RwdPath + ".tmp";
                File.Copy(rwdSource, tmp, true);
                if (File.Exists(mod.RwdPath)) File.Delete(mod.RwdPath);
                File.Move(tmp, mod.RwdPath);
                mod.BuiltAgainst = null;
                if (File.Exists(mod.PackagePath)) File.Delete(mod.PackagePath);
            }
            if (mod.InOwnFolder) WriteText(Path.Combine(mod.Folder, "mod.json"), man.ToJsonText());   // not moved up yet; Save() keeps it in mods.json too

            if (existing == null) Mods.Add(mod);
            r.Mod = mod;
            r.Messages.AddRange(Enable(mod));
            SortByRules();
            foreach (var s in ExternalScripts().Where(s => !s.Disabled && s.Mounts.Any(p => Path.GetFileName(p).Equals(mod.Id + ".rwd", StringComparison.OrdinalIgnoreCase))))
            {
                SetExternal(s, false);
                r.Messages.Add($"Turned off {s.Name}, an older startup script that loaded {man.Title} outside the manager");
            }
            Save();
            return r;
        }

        /// <summary>A .k2mod-style zip or folder (mod.json plus files\ or patches\), as opposed to loose game files.</summary>
        public static bool IsPackageSource(string source)
        {
            if (Directory.Exists(source))
                return File.Exists(Path.Combine(source, "mod.json")) &&
                       (Directory.Exists(Path.Combine(source, "files")) || Directory.Exists(Path.Combine(source, "patches")));
            if (!string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using (var z = System.IO.Compression.ZipFile.OpenRead(source))
                    return ModPackage.IsPackageLayout(z.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList());
            }
            catch (InvalidDataException) { return false; }
        }

        /// <summary>A zip or folder of finished files (for example an older mod made before packages existed).</summary>
        public static ModPackage LoosePackage(string source, GameData game, List<string> messages)
        {
            List<KeyValuePair<string, Func<byte[]>>> entries;
            if (Directory.Exists(source))
                entries = ModPackage.FolderEntries(source);
            else if (string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                entries = new List<KeyValuePair<string, Func<byte[]>>>();
                try
                {
                    using (var z = System.IO.Compression.ZipFile.OpenRead(source))
                        foreach (var e in z.Entries.Where(e => !e.FullName.EndsWith("/")))
                        {
                            using (var s = e.Open())
                            using (var m = new MemoryStream())
                            {
                                s.CopyTo(m);
                                var bytes = m.ToArray();
                                entries.Add(new KeyValuePair<string, Func<byte[]>>(e.FullName.Replace('\\', '/'), () => bytes));
                            }
                        }
                }
                catch (InvalidDataException)
                {
                    throw new ModException($"{Path.GetFileName(source)} is not a valid zip file");
                }
            }
            else
                throw new ModException($"{Path.GetFileName(source)} is not something the manager can install. Use a .k2mod package, a .rwd archive, or a .zip of mod files.");

            var report = new ModPacker.Report();
            var files = ModPacker.CollectLoose(entries, game, strict: true, report);
            if (files.Count == 0)
                throw new ModException($"{Path.GetFileName(source)} has no files laid out like the game's data (Units\\, Buildings\\, ...)");
            var (name, version) = NameAndVersion(Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')));
            var pkg = new ModPackage
            {
                Manifest = new ModManifest
                {
                    Name = name,
                    Version = version,
                    Id = ModManifest.MakeId(name),
                    Description = "Installed from " + Path.GetFileName(source.TrimEnd('\\', '/')),
                },
            };
            foreach (var f in files) pkg.Files[f.Key] = f.Value;
            if (report.Skipped.Count > 0)
                messages.Add($"Left out {report.Skipped.Count} file(s) that are not game data (readmes and the like)");
            messages.AddRange(report.Notes.Take(5));
            return pkg;
        }

        /// <summary>"Immortals_V2.1" -> ("Immortals", "2.1").</summary>
        public static (string name, string version) NameAndVersion(string fileName)
        {
            var s = fileName.Replace('_', ' ').Trim();
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*?)[\s-]+[vV]?(\d+(?:\.\d+)*[a-z]?)$");
            return m.Success && m.Groups[1].Value.Trim().Length > 0 ? (m.Groups[1].Value.Trim(), m.Groups[2].Value) : (s, "");
        }

        /// <summary>Rebuilds mods made from patches when Data.rwd has changed (a game update or file check).</summary>
        public List<string> RebuildStale(GameData game, Action<string> progress = null)
        {
            var o = new List<string>();
            foreach (var m in Mods.Where(m => m.HasPackage && (!m.IsInstalled || m.BuiltAgainst != null && m.BuiltAgainst != game.Fingerprint)).ToList())
            {
                bool first = !m.IsInstalled;
                progress?.Invoke((first ? "Building " : "Updating ") + m.Manifest.Title + " for your copy of the game");
                try
                {
                    var pkg = ModPackage.Load(m.PackagePath);
                    RwdArchive.Write(m.RwdPath, pkg.Build(pkg.NeedsGameData ? game : null));
                    m.BuiltAgainst = pkg.NeedsGameData ? game.Fingerprint : null;
                    m.Problem = null;
                    m.ForgetFiles();
                    o.Add(first ? $"Built {m.Manifest.Title} from its package" : $"Rebuilt {m.Manifest.Title} because the game's data changed");
                }
                catch (ModException ex)
                {
                    m.Problem = ex.Message;
                    o.Add($"{m.Manifest.Title} no longer fits your copy of the game, so it is off until the author updates it.\n{ex.Message}");
                }
            }
            if (o.Count > 0) Save();
            return o;
        }

        /// <summary>Removes a mod's files (only the ones the manager wrote), and an older per-mod folder if that leaves it empty.</summary>
        public void Remove(InstalledMod m)
        {
            Mods.Remove(m);
            foreach (var f in new[] { m.RwdPath, m.PackagePath })
                if (File.Exists(f)) File.Delete(f);
            if (m.InOwnFolder)
            {
                var mf = Path.Combine(m.Folder, "mod.json");
                if (File.Exists(mf)) File.Delete(mf);
                if (Directory.Exists(m.Folder) && !Directory.EnumerateFileSystemEntries(m.Folder).Any())
                    Directory.Delete(m.Folder);
            }
            Save();
        }

        // ------------------------------------------------------------------ rules

        /// <summary>Turns a mod on; turns off mods it cannot run with and turns on installed mods it needs.</summary>
        public List<string> Enable(InstalledMod m)
        {
            var o = new List<string>();
            if (m.Problem != null)
            {
                o.Add($"{m.Manifest.Title} cannot be turned on: {m.Problem}");
                return o;
            }
            m.Enabled = true;
            foreach (var other in Mods.Where(x => x != m && x.Enabled && Incompatible(m, x)).ToList())
            {
                other.Enabled = false;
                o.Add($"Turned off {other.Manifest.Title}: it cannot run together with {m.Manifest.Title}");
            }
            foreach (var req in m.Manifest.Requires)
            {
                var need = Find(req);
                if (need == null) o.Add($"{m.Manifest.Title} needs the mod \"{req}\", which is not installed");
                else if (!need.Enabled && need.Problem == null)
                {
                    o.AddRange(Enable(need));
                    o.Add($"Turned on {need.Manifest.Title}: {m.Manifest.Title} needs it");
                }
            }
            return o;
        }

        public List<string> Disable(InstalledMod m)
        {
            var o = new List<string>();
            m.Enabled = false;
            foreach (var other in Mods.Where(x => x.Enabled && x.Manifest.Requires.Contains(m.Id, StringComparer.OrdinalIgnoreCase)))
                o.Add($"{other.Manifest.Title} needs {m.Manifest.Title} and may not work without it");
            return o;
        }

        public static bool Incompatible(InstalledMod a, InstalledMod b) =>
            a.Manifest.Incompatible.Contains(b.Id, StringComparer.OrdinalIgnoreCase) ||
            b.Manifest.Incompatible.Contains(a.Id, StringComparer.OrdinalIgnoreCase);

        /// <summary>Moves mods so each loads after the mods it requires or lists in loadAfter. Otherwise keeps the order.</summary>
        public void SortByRules()
        {
            var order = new List<InstalledMod>();
            var placed = new HashSet<InstalledMod>();
            var visiting = new HashSet<InstalledMod>();
            void Place(InstalledMod m)
            {
                if (placed.Contains(m) || !visiting.Add(m)) return;
                foreach (var dep in m.Manifest.Requires.Concat(m.Manifest.LoadAfter))
                {
                    var d = Find(dep);
                    if (d != null) Place(d);
                }
                visiting.Remove(m);
                placed.Add(m);
                order.Add(m);
            }
            foreach (var m in Mods.ToList()) Place(m);
            Mods.Clear();
            Mods.AddRange(order);
        }

        public void Move(InstalledMod m, int delta)
        {
            int i = Mods.IndexOf(m), j = i + delta;
            if (i < 0 || j < 0 || j >= Mods.Count) return;
            Mods.RemoveAt(i);
            Mods.Insert(j, m);
        }

        public List<FileConflict> FileConflicts()
        {
            var owner = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
            var pairs = new Dictionary<(InstalledMod, InstalledMod), List<string>>();
            foreach (var m in Mods.Where(x => x.Enabled && x.Problem == null))
                foreach (var f in m.FileNames)
                {
                    if (owner.TryGetValue(f, out var prev))
                    {
                        if (!pairs.TryGetValue((prev, m), out var l)) pairs[(prev, m)] = l = new List<string>();
                        l.Add(f);
                    }
                    owner[f] = m;
                }
            return pairs.Select(p => new FileConflict { Loser = p.Key.Item1, Winner = p.Key.Item2, Files = p.Value }).ToList();
        }

        /// <summary>Plain-language problems with the current set of enabled mods.</summary>
        public List<string> Problems()
        {
            var o = new List<string>();
            var on = Mods.Where(m => m.Enabled).ToList();
            for (int i = 0; i < on.Count; i++)
                for (int j = i + 1; j < on.Count; j++)
                    if (Incompatible(on[i], on[j])) o.Add($"{on[i].Manifest.Title} and {on[j].Manifest.Title} are not meant to run together");
            foreach (var m in on)
                foreach (var req in m.Manifest.Requires)
                    if (Find(req)?.Enabled != true) o.Add($"{m.Manifest.Title} needs \"{req}\" turned on");
            foreach (var m in Mods.Where(x => x.Problem != null)) o.Add($"{m.Manifest.Title}: {m.Problem}");
            return o;
        }

        // ------------------------------------------------------------------ outside the manager

        /// <summary>Other startup scripts that mount data with adddepot/addmod (the game's own are left out).</summary>
        public List<ExternalScript> ExternalScripts()
        {
            var o = new List<ExternalScript>();
            void Scan(string dir, bool disabled)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "autoexec*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    if (name.Equals("autoexec.txt", StringComparison.OrdinalIgnoreCase) || name.Equals(ScriptName, StringComparison.OrdinalIgnoreCase)) continue;
                    var mounts = File.ReadAllLines(f)
                        .Select(l => l.Trim())
                        .Where(l => l.StartsWith("adddepot ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("addmod ", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Substring(l.IndexOf(' ') + 1).Trim())
                        .ToList();
                    if (mounts.Count > 0) o.Add(new ExternalScript { Path = f, Disabled = disabled, Mounts = mounts });
                }
            }
            Scan(Game.StartupDir, false);
            Scan(DisabledScriptsDir, true);
            return o;
        }

        /// <summary>Turns another startup script off by moving it to startup\disabled (the game only reads startup\), or back on.</summary>
        public void SetExternal(ExternalScript s, bool on)
        {
            var dest = Path.Combine(on ? Game.StartupDir : DisabledScriptsDir, s.Name);
            if (on == !s.Disabled) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest)) throw new ModException($"{dest} already exists");
            File.Move(s.Path, dest);
            s.Path = dest;
            s.Disabled = !on;
        }

        static readonly HashSet<string> UserFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CampaignSave", "Films", "Maps", "Save", "saves", "Screenshots", "synclogs", "User" };

        /// <summary>Loose files in Documents\Kohan2\data that replace game files (they override every mod).</summary>
        public static List<string> LooseOverrides(GameData game)
        {
            var o = new List<string>();
            var root = GameInstall.UserDataDir;
            if (!Directory.Exists(root)) return o;
            var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).ToList();
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (!UserFolders.Contains(Path.GetFileName(dir)))
                    files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
            foreach (var f in files)
            {
                var rel = f.Substring(prefix.Length).Replace('\\', '/');
                if (game.Contains(rel) || rel.EndsWith(".tgi", StringComparison.OrdinalIgnoreCase)) o.Add(rel);
            }
            return o;
        }
    }
}
