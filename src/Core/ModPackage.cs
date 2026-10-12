using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace K2Mods
{
    /// <summary>mod.json: who made the mod and how it relates to other mods.</summary>
    public sealed class ModManifest
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Description = "";
        public string Website = "";
        public List<string> Requires = new List<string>();
        public List<string> Incompatible = new List<string>();
        public List<string> LoadAfter = new List<string>();
        /// <summary>Author-declared game file each new art file was painted over (stored as a delta against it).</summary>
        public SortedDictionary<string, string> Bases = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly Regex IdRe = new Regex("^[a-z0-9][a-z0-9_-]{0,47}$");

        public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name;

        public static bool IsValidId(string id) => IdRe.IsMatch(id ?? "");

        public static string MakeId(string name)
        {
            var s = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (s.Length > 48) s = s.Substring(0, 48).Trim('-');
            return s.Length == 0 ? "mod" : s;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new ModException("mod.json: \"name\" is missing");
            if (!IdRe.IsMatch(Id ?? "")) throw new ModException($"mod.json: \"id\" must be 1-48 characters of a-z, 0-9, - and _ (got \"{Id}\")");
            if (Id == "mods") throw new ModException($"mod.json: \"{Id}\" is a reserved id");
        }

        public static ModManifest FromJson(Dictionary<string, object> d) => new ModManifest
        {
            Id = Json.Str(d, "id", "").Trim(),
            Name = Json.Str(d, "name", "").Trim(),
            Version = Json.Str(d, "version", "").Trim(),
            Author = Json.Str(d, "author", "").Trim(),
            Description = Json.Str(d, "description", ""),
            Website = Json.Str(d, "website", "").Trim(),
            Requires = Json.StrList(d, "requires").Select(x => x.ToLowerInvariant()).ToList(),
            Incompatible = Json.StrList(d, "incompatible").Select(x => x.ToLowerInvariant()).ToList(),
            LoadAfter = Json.StrList(d, "loadAfter").Select(x => x.ToLowerInvariant()).ToList(),
            Bases = ReadBases(d),
        };

        static SortedDictionary<string, string> ReadBases(Dictionary<string, object> d)
        {
            var o = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (d.TryGetValue("bases", out var v) && v is Dictionary<string, object> m)
                foreach (var kv in m)
                    if (kv.Value is string s && s.Trim().Length > 0)
                        o[kv.Key.Replace('\\', '/')] = s.Trim().Replace('\\', '/');
            return o;
        }

        public static ModManifest Read(string path) => FromJson(Json.ParseObject(File.ReadAllText(path, Encoding.UTF8)));

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["format"] = 1L,
            ["id"] = Id,
            ["name"] = Name,
            ["version"] = Version,
            ["author"] = Author,
            ["description"] = Description,
            ["website"] = Website,
            ["requires"] = Requires,
            ["incompatible"] = Incompatible,
            ["loadAfter"] = LoadAfter,
            ["bases"] = Bases.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        };

        public string ToJsonText() => Json.Write(ToJson()) + "\n";
    }

    /// <summary>
    /// A .k2mod package (a zip) or the same layout in a folder:
    ///   mod.json
    ///   files/&lt;game path&gt;             whole files that belong to the mod
    ///   patches/&lt;game path&gt;.bbpatch   text changes against the player's own game file
    ///   patches/&lt;game path&gt;.bbdelta   binary changes against a game file
    /// Installing builds one .rwd archive from it. Patches and deltas read the player's
    /// Data.rwd, so a package never has to contain the game's own files.
    /// </summary>
    public sealed class ModPackage
    {
        public ModManifest Manifest = new ModManifest();
        public readonly SortedDictionary<string, byte[]> Files = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, byte[]> Patches = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, byte[]> Deltas = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public bool NeedsGameData => Patches.Count + Deltas.Count > 0;
        public int Count => Files.Count + Patches.Count + Deltas.Count;

        public static bool IsPackageLayout(IList<string> relPaths) =>
            relPaths.Any(p => p.Equals("mod.json", StringComparison.OrdinalIgnoreCase)) &&
            relPaths.Any(p => p.StartsWith("files/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("patches/", StringComparison.OrdinalIgnoreCase));

        public static ModPackage Load(string path)
        {
            if (Directory.Exists(path))
                return FromEntries(FolderEntries(path), path);
            try
            {
                using (var z = ZipFile.OpenRead(path))
                    return FromEntries(ZipEntries(z), path);
            }
            catch (InvalidDataException)
            {
                throw new ModException($"{Path.GetFileName(path)} is not a mod package (it is not a valid zip file)");
            }
        }

        /// <summary>Reads only mod.json from a package.</summary>
        public static ModManifest ReadManifest(string path)
        {
            try
            {
                using (var z = ZipFile.OpenRead(path))
                {
                    var e = z.Entries.FirstOrDefault(x => x.FullName.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new ModException($"{Path.GetFileName(path)} has no mod.json, so it is not a mod package");
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8))
                    {
                        var m = ModManifest.FromJson(Json.ParseObject(r.ReadToEnd()));
                        m.Validate();
                        return m;
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw new ModException($"{Path.GetFileName(path)} is not a mod package (it is not a valid zip file)");
            }
        }

        internal static List<KeyValuePair<string, Func<byte[]>>> ZipEntries(ZipArchive z)
        {
            var o = new List<KeyValuePair<string, Func<byte[]>>>();
            foreach (var e in z.Entries.Where(e => !e.FullName.EndsWith("/")))
            {
                byte[] bytes;
                using (var s = e.Open())
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    bytes = m.ToArray();
                }
                o.Add(new KeyValuePair<string, Func<byte[]>>(e.FullName.Replace('\\', '/'), () => bytes));
            }
            return o;
        }

        internal static List<KeyValuePair<string, Func<byte[]>>> FolderEntries(string root)
        {
            var full = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                .Select(f => new KeyValuePair<string, Func<byte[]>>(f.Substring(full.Length).Replace('\\', '/'), () => File.ReadAllBytes(f)))
                .ToList();
        }

        static ModPackage FromEntries(List<KeyValuePair<string, Func<byte[]>>> entries, string label)
        {
            var pkg = new ModPackage();
            var manifest = entries.FirstOrDefault(e => e.Key.Equals("mod.json", StringComparison.OrdinalIgnoreCase));
            if (manifest.Key == null) throw new ModException($"{Path.GetFileName(label)} has no mod.json, so it is not a mod package");
            pkg.Manifest = ModManifest.FromJson(Json.ParseObject(Encoding.UTF8.GetString(manifest.Value())));
            pkg.Manifest.Validate();
            foreach (var e in entries)
            {
                var p = e.Key;
                if (p.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
                    pkg.Files[p.Substring(6)] = e.Value();
                else if (p.StartsWith("patches/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".bbpatch", StringComparison.OrdinalIgnoreCase))
                    pkg.Patches[p.Substring(8, p.Length - 16)] = e.Value();
                else if (p.StartsWith("patches/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".bbdelta", StringComparison.OrdinalIgnoreCase))
                    pkg.Deltas[p.Substring(8, p.Length - 16)] = e.Value();
            }
            var twice = pkg.Files.Keys.Concat(pkg.Patches.Keys).Concat(pkg.Deltas.Keys)
                .GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) throw new ModException($"{Path.GetFileName(label)}: {twice.Key} is in the package twice");
            if (pkg.Count == 0) throw new ModException($"{Path.GetFileName(label)} contains no game files");
            return pkg;
        }

        /// <summary>Builds every file of the mod. game may be null when NeedsGameData is false.</summary>
        public List<KeyValuePair<string, byte[]>> Build(GameData game)
        {
            if (NeedsGameData && game == null) throw new ModException("This mod needs Kohan II's Data.rwd to install");
            var o = new List<KeyValuePair<string, byte[]>>();
            var errors = new List<string>();
            foreach (var f in Files) o.Add(f);
            foreach (var p in Patches)
            {
                try
                {
                    var text = Encoding.UTF8.GetString(p.Value);
                    if (text.Contains("\r\n")) text = text.Replace("\r\n", "\n");   // a patch that went through a CRLF checkout
                    o.Add(new KeyValuePair<string, byte[]>(p.Key, BbPatch.Apply(text, game.Get, p.Key)));
                }
                catch (Exception ex) when (ex is ModException || ex is FormatException || ex is InvalidDataException) { errors.Add(ex.Message); }
            }
            foreach (var d in Deltas)
            {
                try { o.Add(new KeyValuePair<string, byte[]>(d.Key, BbDelta.Apply(d.Value, game.Get, d.Key))); }
                catch (Exception ex) when (ex is ModException || ex is FormatException || ex is InvalidDataException) { errors.Add(ex.Message); }
            }
            if (errors.Count > 0)
                throw new ModException($"{Manifest.Title} could not be built from your copy of Kohan II:\n  " + string.Join("\n  ", errors.Take(12)) +
                                       (errors.Count > 12 ? $"\n  ... and {errors.Count - 12} more" : ""));
            return RwdArchive.SortForBuild(o);
        }

        static readonly DateTimeOffset ZipTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>Writes the package as a zip with fixed timestamps, so equal content gives an equal file.</summary>
        public void Save(string path)
        {
            Manifest.Validate();
            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                void Add(string name, byte[] data)
                {
                    var e = z.CreateEntry(name, CompressionLevel.Optimal);
                    e.LastWriteTime = ZipTime;
                    using (var s = e.Open()) s.Write(data, 0, data.Length);
                }
                Add("mod.json", new UTF8Encoding(false).GetBytes(Manifest.ToJsonText()));
                foreach (var f in Files) Add("files/" + f.Key, f.Value);
                foreach (var p in Patches) Add("patches/" + p.Key + ".bbpatch", p.Value);
                foreach (var d in Deltas) Add("patches/" + d.Key + ".bbdelta", d.Value);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>Turns a folder of finished mod files into a package that contains none of the game's own files.</summary>
    public static class ModPacker
    {
        public sealed class Report
        {
            public int Unchanged, Patched, Delta, New, Aliased;
            public readonly List<string> Notes = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            /// <summary>New binary files shipped whole: if one was painted over a game file, declare it in "bases".</summary>
            public readonly List<string> NewArt = new List<string>();

            public override string ToString() =>
                $"{Patched} text file(s) as patches, {Delta} changed art/binary file(s) as deltas, " +
                $"{Aliased} new file(s) stored against a game file, {New} new file(s) shipped whole; {Unchanged} unchanged game file(s) left out";
        }

        static readonly HashSet<string> JunkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "thumbs.db", "desktop.ini", ".ds_store", "mod.json" };
        static readonly HashSet<string> WorkExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".xcf", ".bak", ".tmp", ".orig", ".blend", ".blend1", ".kra", ".k2mod", ".zip", ".rar", ".7z", ".doc", ".docx", ".pdf", ".md", ".rtf" };

        public static bool IsWorkFile(string rel)
        {
            var parts = rel.Split('/');
            if (parts.Any(p => p.StartsWith("."))) return true;
            var name = parts[parts.Length - 1];
            return JunkNames.Contains(name) || WorkExtensions.Contains(Path.GetExtension(name));
        }

        /// <summary>
        /// The folder inside `paths` that lines up with the game's data layout (Units/, Buildings/, ...).
        /// Returns "" for the top level, or a prefix ending in '/'.
        /// </summary>
        public static string DetectRoot(IEnumerable<string> paths, GameData game)
        {
            var list = paths.ToList();
            var top = new HashSet<string>(game.Entries.Select(e => e.Name.Split('/')[0]), StringComparer.OrdinalIgnoreCase);
            var prefixes = new HashSet<string> { "" };
            foreach (var p in list)
            {
                var parts = p.Split('/');
                for (int i = 1; i < parts.Length && i <= 4; i++) prefixes.Add(string.Join("/", parts.Take(i)) + "/");
            }
            string best = "";
            int bestScore = -1;
            foreach (var pre in prefixes.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int score = 0;
                foreach (var p in list)
                {
                    if (!p.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
                    var rest = p.Substring(pre.Length);
                    if (game.Contains(rest)) score += 2;
                    else if (rest.Contains("/") && top.Contains(rest.Split('/')[0])) score += 1;
                }
                if (score > bestScore) { best = pre; bestScore = score; }
            }
            return best;
        }

        /// <summary>
        /// Files to include from a folder or zip of finished files (no mod.json needed).
        /// strict: drop files outside the game's top-level folders (for third-party zips with readmes).
        /// </summary>
        public static SortedDictionary<string, byte[]> CollectLoose(List<KeyValuePair<string, Func<byte[]>>> entries, GameData game, bool strict, Report report)
        {
            var paths = entries.Where(e => !IsWorkFile(e.Key)).Select(e => e.Key).ToList();
            var root = DetectRoot(paths, game);
            var top = new HashSet<string>(game.Entries.Select(e => e.Name.Split('/')[0]), StringComparer.OrdinalIgnoreCase);
            var o = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (IsWorkFile(e.Key) || !e.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    report.Skipped.Add(e.Key);
                    continue;
                }
                var rel = e.Key.Substring(root.Length);
                bool known = game.Contains(rel) || (rel.Contains("/") && top.Contains(rel.Split('/')[0]));
                if (strict && !known && !rel.EndsWith(".tgi", StringComparison.OrdinalIgnoreCase))
                {
                    report.Skipped.Add(e.Key);
                    continue;
                }
                if (!known) report.Notes.Add($"{rel} is not in a folder the game has; check the path is right");
                o[game.Canon(rel) ?? rel] = e.Value();
            }
            if (root.Length > 0) report.Notes.Insert(0, $"Using {root.TrimEnd('/')} as the data folder");
            return o;
        }

        /// <summary>Converts finished files into patches, deltas and new files.</summary>
        public static ModPackage FromFiles(ModManifest manifest, SortedDictionary<string, byte[]> files, GameData game, Report report, Action<string> progress = null)
        {
            var pkg = new ModPackage { Manifest = manifest };
            int i = 0;
            foreach (var f in files)
            {
                progress?.Invoke($"{++i}/{files.Count} {f.Key}");
                var name = f.Key;
                var data = f.Value;
                var canon = game.Canon(name);
                if (canon != null)
                {
                    var baseData = game.Get(canon);
                    if (baseData.AsSpanEquals(data)) { report.Unchanged++; continue; }
                    if (GameData.IsTextName(canon) && TryPatch(canon, baseData, data, game, out var text))
                    {
                        pkg.Patches[canon] = text;
                        report.Patched++;
                    }
                    else
                    {
                        pkg.Deltas[canon] = BbDelta.Make(canon, baseData, data);
                        report.Delta++;
                    }
                    continue;
                }
                if (manifest.Bases.TryGetValue(name, out var declared))
                {
                    var bname = game.Canon(declared) ?? throw new ModException($"mod.json \"bases\": {declared} (for {name}) is not a game file");
                    pkg.Deltas[name] = BbDelta.Make(bname, game.Get(bname), data);
                    report.Aliased++;
                    continue;
                }
                if (GameData.IsTextName(name))
                {
                    var derived = game.BestTextBase(data);
                    if (TryPatch(derived, derived != null ? game.Get(derived) : null, data, game, out var text) &&
                        (derived != null || Encoding.UTF8.GetString(text).Contains("\n= ")))
                    {
                        pkg.Patches[name] = text;
                        report.Patched++;
                    }
                    else
                    {
                        pkg.Files[name] = data;
                        report.New++;
                    }
                    continue;
                }
                var basis = BestDeltaBase(name, data, game);
                if (basis != null)
                {
                    pkg.Deltas[name] = BbDelta.Make(basis, game.Get(basis), data);
                    report.Aliased++;
                    report.Notes.Add($"{name} is stored as a change to the game's {basis}");
                }
                else
                {
                    pkg.Files[name] = data;
                    report.New++;
                    report.NewArt.Add(name);
                }
            }
            if (pkg.Count == 0) throw new ModException("Every file in the folder is identical to the game's own copy, so there is nothing to package");
            return pkg;
        }

        static bool TryPatch(string baseName, byte[] baseData, byte[] data, GameData game, out byte[] text)
        {
            text = null;
            try
            {
                var t = BbPatch.Make(baseName, baseData, data, game.Index);
                if (!BbPatch.Apply(t, game.Get, baseName ?? "new file").AsSpanEquals(data)) return false;
                text = new UTF8Encoding(false).GetBytes(t);
                return true;
            }
            catch (DecoderFallbackException) { return false; }
            catch (ModException) { return false; }
        }

        /// <summary>A same-type, same-size game file this art was painted over, if storing a delta clearly beats storing the file.</summary>
        static string BestDeltaBase(string name, byte[] data, GameData game)
        {
            var same = game.SameSize(name, data.Length).ToList();
            if (same.Count == 0 || same.Count > 600) return null;
            long plain = Compressed(data);
            string best = null;
            long bestSize = long.MaxValue;
            foreach (var e in same)
            {
                var b = game.Read(e);
                if (b.AsSpanEquals(data)) return e.Name;
                var x = new byte[data.Length];
                for (int i = 0; i < x.Length; i++) x[i] = (byte)(data[i] ^ b[i]);
                long c = Compressed(x);
                if (c < bestSize) { bestSize = c; best = e.Name; }
            }
            return bestSize < plain * 0.6 ? best : null;
        }

        static long Compressed(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(data, 0, data.Length);
                return ms.Length;
            }
        }
    }
}
