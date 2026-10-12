using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace K2Mods
{
    /// <summary>A folder the game reads loose files from (a folder depot in startup\autoexec.txt).</summary>
    public sealed class LooseDepot
    {
        public string Folder;
        /// <summary>How players know the folder ("Documents\Kohan2\data").</summary>
        public string Label;
        /// <summary>Files the game itself puts in this folder (never part of a mod).</summary>
        public HashSet<string> GameOwn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class LooseFile
    {
        public LooseDepot Depot;
        /// <summary>Full path on disk.</summary>
        public string Path;
        /// <summary>Path inside the depot, '/' separators, as found on disk.</summary>
        public string Rel;
        /// <summary>Name in the game's data: the game's own spelling for a file it replaces, otherwise Rel.</summary>
        public string Name;
        /// <summary>A game file of the same name exists in Data.rwd (false: a file the mod adds).</summary>
        public bool Replaces;
    }

    /// <summary>A mod installed by hand: game files copied loose into the game's folder depots.</summary>
    public sealed class LooseInstall
    {
        public readonly List<LooseFile> Files = new List<LooseFile>();
        public string SuggestedName = "";
        public string SuggestedVersion = "";
        /// <summary>Where the suggestion came from ("its AVars_version.tgi", "the installed mod Immortals"), or null.</summary>
        public string SuggestedFrom;

        public int Count => Files.Count;
        public int Replacing => Files.Count(f => f.Replaces);
        public int Adding => Files.Count - Replacing;
        public List<LooseDepot> Depots => Files.Select(f => f.Depot).Distinct().ToList();
        public string Where => string.Join(" and ", Depots.Select(d => d.Label));
        public string SuggestedTitle => SuggestedName + (SuggestedVersion.Length > 0 ? " " + SuggestedVersion : "");
    }

    public sealed class LoosePackResult
    {
        public InstalledMod Mod;
        public bool Replaced;
        public int Files;
        public uint Stamp;
        /// <summary>The loose_backup_* folders the files were moved into.</summary>
        public readonly List<string> Backups = new List<string>();
        /// <summary>Files that could not be moved (still loose, still overriding).</summary>
        public readonly List<string> NotMoved = new List<string>();
        public readonly List<string> Messages = new List<string>();
    }

    /// <summary>
    /// Mods installed by hand. Most Kohan II mods are installed by copying their files into
    /// Documents\Kohan2\data, a folder the game reads after everything else, so those files override
    /// every mod in the manager. The manager finds them and can pack them into one deterministic .rwd
    /// it manages like any other mod; the loose files are then moved (never deleted) to a
    /// loose_backup_&lt;date&gt; folder next to the folder they were in.
    ///
    /// Folder depots, from the game's startup\autoexec.txt:
    ///   adddepot data/                 &lt;game&gt;\data, after Data.rwd (mods added later override it)
    ///   adddepot %USERDATA%/data/ 1    Documents\Kohan2\data, priority 1: overrides every mod
    ///   adddepot Userdata/ 1           &lt;game&gt;\Userdata, priority 1, added last: overrides even that
    /// Multiplayer: the join check lists folder depots without their contents, but an addmod archive with
    /// its stamp. Loose files are therefore never compared, a packed mod is: after packing, a player matches
    /// players who use the same packed mod and no longer players who installed it by hand.
    /// </summary>
    public static class LooseMods
    {
        /// <summary>Folders the game writes the player's own files into (Data.rwd UVars PathSavedGames, PathFilms,
        /// PathMaps, PathRMCS, PathDynamicCampaignSavedGames, CampaignSaveDirectory, RendererScreenshotFolder,
        /// UserPreferencesFilename; k2.exe synclogs/): saved games, films, maps, screenshots, settings.</summary>
        static readonly HashSet<string> UserFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CampaignSave", "DynamicCampaignSave", "Films", "Maps", "RandomMap", "Save", "saves", "Screenshots", "synclogs", "User" };

        /// <summary>File types the game writes for the player: saved games, films, campaign progress, preferences, logs, crash dumps.</summary>
        static readonly HashSet<string> UserFileTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".rsg", ".rfm", ".rcs", ".rup", ".log", ".dmp" };

        /// <summary>Archives: a folder depot mounts these itself, they are not loose files.</summary>
        static readonly HashSet<string> ArchiveTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rwd", ".rti" };

        /// <summary>Files the Steam game ships in &lt;game&gt;\data (the Modern Rebuild's options dialog, installed with it).</summary>
        static readonly string[] ShippedInGameData = { "UI/Shared/options_dialog.tgi" };

        public const string BackupPrefix = "loose_backup_";

        /// <summary>The folder depots, highest priority first (the copy the game uses comes first).</summary>
        public static List<LooseDepot> Depots(GameInstall game)
        {
            var user = GameInstall.UserDataDir;
            bool real = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GameInstall.UserDataVariable));
            var o = new List<LooseDepot>
            {
                new LooseDepot { Folder = Path.Combine(game.Folder, "Userdata"), Label = "the game's Userdata folder" },
                new LooseDepot { Folder = user, Label = real ? @"Documents\Kohan2\data" : user },
                new LooseDepot { Folder = Path.Combine(game.Folder, "data"), Label = "the game's data folder" },
            };
            foreach (var f in ShippedInGameData) o[2].GameOwn.Add(f);
            return o;
        }

        /// <summary>Whether a file in a folder depot is game data a mod put there (not the player's saves or settings).</summary>
        public static bool IsModFile(string rel, GameData game, HashSet<string> topFolders)
        {
            var parts = rel.Split('/');
            if (parts.Length > 1 && UserFolders.Contains(parts[0])) return false;
            var ext = System.IO.Path.GetExtension(rel);
            if (UserFileTypes.Contains(ext) || ArchiveTypes.Contains(ext) || ModPacker.IsWorkFile(rel)) return false;
            if (game.Contains(rel)) return true;
            if (ext.Equals(".tgi", StringComparison.OrdinalIgnoreCase)) return true;
            return parts.Length > 1 && topFolders.Contains(parts[0]);
        }

        /// <summary>Loose mod files in every folder depot, with a suggested name and version for packing them.</summary>
        public static LooseInstall Find(GameInstall gameFolder, GameData game, IEnumerable<InstalledMod> installed = null)
        {
            var o = new LooseInstall();
            var top = new HashSet<string>(game.Entries.Where(e => e.Name.Contains("/")).Select(e => e.Name.Split('/')[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var d in Depots(gameFolder))
            {
                if (!Directory.Exists(d.Folder)) continue;
                var prefix = System.IO.Path.GetFullPath(d.Folder).TrimEnd('\\') + "\\";
                var files = new List<string>();
                try
                {
                    files.AddRange(Directory.EnumerateFiles(d.Folder, "*", SearchOption.TopDirectoryOnly));
                    foreach (var dir in Directory.EnumerateDirectories(d.Folder))
                        if (!UserFolders.Contains(System.IO.Path.GetFileName(dir)))
                            files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = System.IO.Path.GetFullPath(f).Substring(prefix.Length).Replace('\\', '/');
                    if (d.GameOwn.Contains(rel) || !IsModFile(rel, game, top)) continue;
                    var canon = game.Canon(rel);
                    o.Files.Add(new LooseFile { Depot = d, Path = f, Rel = rel, Name = canon ?? rel, Replaces = canon != null });
                }
            }
            if (o.Count > 0) Suggest(o, game, installed);
            return o;
        }

        static readonly Regex VersionVar = new Regex(@"^\s*string\s+Version\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        static string VersionText(byte[] data)
        {
            if (data == null) return null;
            var m = VersionVar.Match(Encoding.UTF8.GetString(data));
            return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
        }

        /// <summary>
        /// Name and version for the pack: the mod's own AVars_version.tgi ("Version = Immortals 2.1"), else an
        /// installed mod with the same files, else a plain default.
        /// </summary>
        static void Suggest(LooseInstall o, GameData game, IEnumerable<InstalledMod> installed)
        {
            var ver = o.Files.FirstOrDefault(f => f.Name.Equals("AVars_version.tgi", StringComparison.OrdinalIgnoreCase));
            if (ver != null)
            {
                try
                {
                    var mine = VersionText(File.ReadAllBytes(ver.Path));
                    var stock = VersionText(game.Get("AVars_version.tgi"));
                    if (!string.IsNullOrEmpty(mine) && !string.Equals(mine, stock, StringComparison.OrdinalIgnoreCase))
                    {
                        var (name, version) = ModLibrary.NameAndVersion(mine);
                        if (Regex.IsMatch(name, "[A-Za-z]"))
                        {
                            o.SuggestedName = name;
                            o.SuggestedVersion = version;
                            o.SuggestedFrom = "its AVars_version.tgi";
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            var names = new HashSet<string>(o.Files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var best = (installed ?? Enumerable.Empty<InstalledMod>())
                .Where(m => m.IsInstalled)
                .Select(m => (mod: m, same: m.FileNames.Count(names.Contains)))
                .Where(x => x.same >= 0.8 * names.Count)
                .OrderByDescending(x => x.same)
                .FirstOrDefault();
            if (best.mod != null)
            {
                o.SuggestedName = best.mod.Manifest.Title;
                o.SuggestedVersion = best.mod.Manifest.Version;
                o.SuggestedFrom = "the installed mod " + best.mod.Manifest.Title;
                return;
            }
            o.SuggestedName = "Hand-installed mod";
            o.SuggestedVersion = "1.0";
        }

        /// <summary>The mod.json for packing these files under a name and version (id made from the name).</summary>
        public static ModManifest ManifestFor(LooseInstall loose, string name, string version) => new ModManifest
        {
            Name = (name ?? "").Trim(),
            Version = (version ?? "").Trim(),
            Id = ModManifest.MakeId((name ?? "").Trim()),
            Description = "Packed from files installed by hand in " + loose.Where + ".",
        };

        /// <summary>The installed mod a pack under this manifest would replace, or null. Refuses an id taken by a package.</summary>
        public static InstalledMod ExistingFor(ModLibrary lib, ModManifest man)
        {
            var existing = lib.Find(man.Id);
            if (existing != null && existing.HasPackage)
                throw new ModException($"{existing.Manifest.Title} already uses the id \"{man.Id}\". Choose another name.");
            return existing;
        }

        /// <summary>
        /// Packs the loose files into Mods\&lt;id&gt;.rwd (deterministic: the same files always give the same
        /// archive and stamp), installs it ticked, then moves the loose files to loose_backup_&lt;date&gt;\ next to
        /// their folder so they stop overriding it. The game must be closed.
        /// </summary>
        public static LoosePackResult Pack(ModLibrary lib, LooseInstall loose, ModManifest man, Action<string> progress = null)
        {
            if (GameInstall.IsGameRunning()) throw new ModException("Kohan II is running. Close it before packing the hand-installed mod.");
            if (loose == null || loose.Count == 0) throw new ModException("No hand-installed mod files were found.");
            if (string.IsNullOrWhiteSpace(man.Id)) man.Id = ModManifest.MakeId(man.Name);
            man.Validate();
            var existing = ExistingFor(lib, man);
            var r = new LoosePackResult { Replaced = existing != null };

            // the copy the game uses wins: depots come highest priority first
            progress?.Invoke("Reading the loose files");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in loose.Files)
                if (!files.ContainsKey(f.Name)) files[f.Name] = File.ReadAllBytes(f.Path);

            progress?.Invoke("Packing " + man.Title);
            Directory.CreateDirectory(lib.Game.ModsDir);
            var mod = existing ?? new InstalledMod { Folder = lib.Game.ModsDir };
            mod.Manifest = man;
            mod.Problem = null;
            r.Stamp = RwdArchive.Write(mod.RwdPath, files);
            r.Files = files.Count;
            mod.BuiltAgainst = null;
            mod.ForgetFiles();
            if (mod.InOwnFolder) ModLibrary.WriteText(Path.Combine(mod.Folder, "mod.json"), man.ToJsonText());
            if (existing == null) lib.Mods.Add(mod);
            r.Mod = mod;
            r.Messages.AddRange(lib.Enable(mod));
            lib.SortByRules();
            lib.Save();

            progress?.Invoke("Moving the loose files out of the way");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var group in loose.Files.GroupBy(f => f.Depot))
            {
                var depot = group.Key;
                var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(depot.Folder).TrimEnd('\\')), BackupPrefix + stamp);
                for (int n = 2; Directory.Exists(backup); n++) backup = Path.Combine(Path.GetDirectoryName(backup), BackupPrefix + stamp + "-" + n);
                int moved = 0;
                foreach (var f in group)
                {
                    try
                    {
                        var dest = Path.Combine(backup, f.Rel.Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Move(f.Path, dest);
                        moved++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        r.NotMoved.Add(f.Path);
                    }
                }
                if (moved == 0) continue;
                r.Backups.Add(backup);
                WriteBackupNote(backup, depot, man, mod, moved);
                RemoveEmptyFolders(depot.Folder, group.Select(f => Path.GetDirectoryName(f.Path)));
            }
            return r;
        }

        static void WriteBackupNote(string backup, LooseDepot depot, ModManifest man, InstalledMod mod, int count)
        {
            var text =
                $"Kohan II Mod Manager, {DateTime.Now:yyyy-MM-dd HH:mm}\r\n\r\n" +
                $"These {count} file(s) were installed by hand in {depot.Folder}.\r\n" +
                $"They were packed into the mod \"{man.Title}\" ({mod.RwdPath}) and moved here, so they no longer override the mods in the manager.\r\n\r\n" +
                "To go back to the hand install: remove the mod in the manager, then move everything in this folder\r\n" +
                $"(except this file) back into {depot.Folder}.\r\n";
            try { File.WriteAllText(Path.Combine(backup, "README.txt"), text, new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Removes folders that moving the files left empty, deepest first (one empty folder at a time, never their contents).</summary>
        static void RemoveEmptyFolders(string root, IEnumerable<string> dirs)
        {
            var top = Path.GetFullPath(root).TrimEnd('\\');
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
                for (var p = Path.GetFullPath(d).TrimEnd('\\'); p.Length > top.Length && p.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
                    all.Add(p);
            foreach (var d in all.OrderByDescending(x => x.Length))
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        /// <summary>What packing changes for online games, in words for players.</summary>
        public const string MultiplayerWarning =
            "Online play: after packing, you can only play online with players who use the same packed mod in this launcher. " +
            "The game refuses to connect you with players who installed the mod by hand (\"Bad Depot Checksums - Versions Incompatible\").";
    }
}
