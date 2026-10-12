using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace K2Mods
{
    /// <summary>One depot k2.exe mounts: an .rwd archive or a folder of loose files, added by a startup script.</summary>
    public sealed class GameDepot
    {
        /// <summary>An .rwd archive (false: a folder of loose files).</summary>
        public bool IsArchive;
        /// <summary>Full path on disk. Never written to a report.</summary>
        public string Path;
        /// <summary>
        /// Name without personal folders, for reports and messages: a path inside the game folder as the script spells it
        /// ("Data.rwd", "Mods/immortals.rwd", "&lt;game&gt;\data"), "Documents\Kohan2\data", or for anything else only the
        /// file or folder name.
        /// </summary>
        public string Label;
        public int Priority;
        /// <summary>Position in the order the scripts add depots, from 0.</summary>
        public int Added;
        /// <summary>Data.rwd or Music.rwd in the game folder: Steam's own archives, identified as a whole, not file by file.</summary>
        public bool IsBase;
        /// <summary>The startup script that adds it ("autoexec.txt").</summary>
        public string Script;
    }

    /// <summary>
    /// The depots k2.exe mounts and the order it searches them, read from its startup scripts (verified against the
    /// game's log, which prints every script and command in this order):
    ///   startup\autoexec.txt runs first, then every other startup\autoexec*.txt in name order (the manager's
    ///   autoexec_mods.txt and the Steam game's autoexec_wc.txt among them). Each of these adds one depot:
    ///     adddepot &lt;path&gt; [priority]        a folder or an archive; priority 0 when not given
    ///     addmod &lt;path&gt; [priority]          adddepot plus the engine's mod flag
    ///     addlocaledepot &lt;path&gt; [priority]  ranked like adddepot here [unverified: how the engine ranks locale depots]
    /// Engine rule (reverse-engineered from k2.exe): depots are sorted by priority, ties in the order they were added, and
    /// the highest rank wins a file. So at equal priority the depot added last wins, and Documents\Kohan2\data (priority 1)
    /// beats every priority-0 archive. Paths are relative to the game folder; %USERDATA% is Documents\Kohan2 (the
    /// K2MODS_USERDATA variable moves %USERDATA%/data for tests, like it moves LooseMods' folder). A depot that does not
    /// exist is left out: the engine only scans what is there.
    /// </summary>
    public static class LoadOrder
    {
        static readonly string[] Commands = { "adddepot", "addmod", "addlocaledepot" };
        static readonly string[] BaseArchives = { "Data.rwd", "Music.rwd" };
        public const string MainScript = "autoexec.txt";

        /// <summary>The startup scripts in the order k2.exe runs them: autoexec.txt, then the other autoexec*.txt by name.</summary>
        public static List<string> Scripts(GameInstall game)
        {
            var main = System.IO.Path.Combine(game.StartupDir, MainScript);
            if (!File.Exists(main))
                throw new ModException("startup\\autoexec.txt is missing from the game folder, so Kohan II would not load its data. Check the game's files in Steam.");
            var o = new List<string> { main };
            // same pattern as the engine; ordinal ignore-case is the order Windows lists names in on NTFS
            o.AddRange(Directory.GetFiles(game.StartupDir, "autoexec*.txt")
                .Where(f => !System.IO.Path.GetFileName(f).Equals(MainScript, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
            return o;
        }

        /// <summary>The depots that exist, ranked lowest first: of the depots that have a file, the last one is the copy the game uses.</summary>
        public static List<GameDepot> Read(GameInstall game)
        {
            var all = new List<GameDepot>();
            foreach (var script in Scripts(game))
            {
                var name = System.IO.Path.GetFileName(script);
                foreach (var line in File.ReadAllLines(script))
                {
                    var d = Parse(game, line);
                    if (d == null) continue;
                    d.Script = name;
                    d.Added = all.Count;
                    all.Add(d);
                }
            }
            return all.Where(d => d.IsArchive ? File.Exists(d.Path) : Directory.Exists(d.Path))
                      .OrderBy(d => d.Priority).ThenBy(d => d.Added).ToList();
        }

        /// <summary>One script line: a depot, or null for anything else (comments, other commands).</summary>
        public static GameDepot Parse(GameInstall game, string line)
        {
            var t = Tokens(line);
            if (t.Count < 2 || !Commands.Contains(t[0], StringComparer.OrdinalIgnoreCase)) return null;
            int priority = 0;
            if (t.Count > 2 && int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) priority = p;
            var raw = t[1].Replace('\\', '/');
            string full;
            try { full = Resolve(game, raw); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
            bool folder = raw.EndsWith("/") || Directory.Exists(full);
            full = full.TrimEnd('\\');
            var d = new GameDepot { IsArchive = !folder, Path = full, Priority = priority, Label = LabelOf(game, full, folder) };
            d.IsBase = d.IsArchive &&
                       string.Equals(System.IO.Path.GetDirectoryName(full), game.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                       BaseArchives.Contains(System.IO.Path.GetFileName(full), StringComparer.OrdinalIgnoreCase);
            return d;
        }

        /// <summary>Words of a script line; a "quoted path" is one word, and a word starting with # ends the line.</summary>
        static List<string> Tokens(string line)
        {
            var o = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length || line[i] == '#') break;
                if (line[i] == '"')
                {
                    int end = line.IndexOf('"', i + 1);
                    if (end < 0) end = line.Length;
                    o.Add(line.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                    o.Add(line.Substring(start, i - start));
                }
            }
            return o;
        }

        static string Resolve(GameInstall game, string raw)
        {
            const string user = "%USERDATA%";
            if (!raw.StartsWith(user, StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(game.Folder, raw.Replace('/', '\\')));
            var parts = raw.Substring(user.Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            // %USERDATA%/data is the folder GameInstall.UserDataDir names (and K2MODS_USERDATA moves); anything else sits next to it
            string root;
            if (parts.Count > 0 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                root = GameInstall.UserDataDir;
                parts.RemoveAt(0);
            }
            else root = System.IO.Path.GetDirectoryName(GameInstall.UserDataDir.TrimEnd('\\'));
            return System.IO.Path.GetFullPath(parts.Aggregate(root, System.IO.Path.Combine));
        }

        /// <summary>A name for the depot that holds no personal folder names (see GameDepot.Label).</summary>
        static string LabelOf(GameInstall game, string full, bool folder)
        {
            string Under(string root)
            {
                var r = System.IO.Path.GetFullPath(root).TrimEnd('\\');
                if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase)) return "";
                return full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length + 1) : null;
            }
            var inGame = Under(game.Folder);
            if (inGame != null) return folder ? @"<game>\" + inGame : inGame.Replace('\\', '/');
            var inUser = Under(GameInstall.UserDataDir);
            if (inUser != null) return @"Documents\Kohan2\data" + (inUser.Length > 0 ? "\\" + inUser : "");
            return System.IO.Path.GetFileName(full);
        }
    }

    /// <summary>
    /// SHA-256 of files and archive entries, kept for the whole session and keyed by the file's full path, length and
    /// last write time, so working the code out again after a change reads only what changed. Thread-safe.
    /// </summary>
    public sealed class FingerprintCache
    {
        readonly object gate = new object();
        readonly Dictionary<string, string> known = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count
        {
            get { lock (gate) return known.Count; }
        }

        /// <summary>The file's identity for the cache: full path, length, last write time (UTC ticks).</summary>
        internal static string StateOf(string path)
        {
            var f = new FileInfo(path);
            return f.FullName.ToLowerInvariant() + "|" + f.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                   f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The cached value, or compute() stored under key (a null key is never cached).</summary>
        internal string Get(string key, Func<string> compute)
        {
            if (key != null)
                lock (gate)
                    if (known.TryGetValue(key, out var v)) return v;
            var h = compute();
            if (key != null)
                lock (gate) known[key] = h;
            return h;
        }

        /// <summary>SHA-256 of a whole file, read in blocks; cached only when the file did not change while it was read.</summary>
        public string FileSha256(string path, CancellationToken cancel = default)
        {
            var before = StateOf(path);
            lock (gate)
                if (known.TryGetValue("file|" + before, out var v)) return v;
            string h;
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                h = Codec.Hex(sha.Hash);
            }
            if (StateOf(path) == before)
                lock (gate) known["file|" + before] = h;
            return h;
        }
    }

    /// <summary>A base archive (Data.rwd, Music.rwd) identified without reading it whole.</summary>
    public sealed class ArchiveIdentity
    {
        /// <summary>Its depot label ("Data.rwd").</summary>
        public string Label;
        public uint Stamp;
        public long Bytes;
        /// <summary>SHA-256 over its directory: one line "name TAB offset TAB size LF" per entry, in the archive's own order.</summary>
        public string DirSha256;

        public string Key => GameFingerprint.KeyOf(Label);

        public bool SameAs(ArchiveIdentity o) =>
            o != null && Stamp == o.Stamp && Bytes == o.Bytes && string.Equals(DirSha256, o.DirSha256, StringComparison.OrdinalIgnoreCase);

        public string Describe() => $"stamp {Stamp:x8}, {Bytes:N0} bytes";
    }

    /// <summary>One file the game loads from a mod archive or a folder depot (the winning copy).</summary>
    public sealed class FingerprintFile
    {
        /// <summary>Path in the game's data with the game's own spelling, '/' separators.</summary>
        public string Path;
        /// <summary>SHA-256 of the copy the game uses; null in a parsed report (only the short hash travels).</summary>
        public string Sha256;
        string shortHash;
        /// <summary>Where that copy comes from: an archive ("Mods/immortals.rwd") or "loose: &lt;depot label&gt;".</summary>
        public string Source;

        public string Key => GameFingerprint.KeyOf(Path);

        /// <summary>First 12 hex digits of Sha256: what a report carries.</summary>
        public string ShortHash
        {
            get => shortHash ?? Sha256?.Substring(0, GameFingerprint.ShortHashLength);
            set => shortHash = value;
        }
    }

    /// <summary>
    /// The game files fingerprint: a short code ("7F3A-91C2") for the game data one Kohan II install loads, so a group can check
    /// before a multiplayer match that everyone's game loads exactly the same data. A match is lockstep: every PC simulates the
    /// same world from its own data, and any difference desyncs it. The game's own join check compares archive stamps only
    /// (Data.rwd, Music.rwd, addmod archives), never loose files in folder depots such as Documents\Kohan2\data, where most
    /// players install mods by hand. The definition below is also in docs/FINGERPRINT.md; change both together, and change
    /// the format tag when the definition changes.
    ///
    /// The code depends only on what the game loads, not on how it was installed: a mod copied loose into
    /// Documents\Kohan2\data and the same files packed into an .rwd give the same code.
    ///
    /// IN, as UTF-8 text with LF line endings and TAB between fields (the "canonical text"):
    ///   k2fp 1                                          the format tag (FormatTag)
    ///   exe     &lt;SHA-256 of k2.exe, or "none"&gt;            the game program decides how data is used
    ///   base    &lt;label&gt; &lt;stamp&gt; &lt;bytes&gt; &lt;dir SHA-256&gt;   per base archive (Data.rwd, Music.rwd in the game folder), in
    ///                                                   load order: archive stamp (8 hex), length, SHA-256 of its directory
    ///                                                   (entry names, offsets, sizes). Steam's archives are too big to hash on
    ///                                                   every check; a change that keeps every name, offset and size and the
    ///                                                   stamp goes unseen.
    ///   file    &lt;path&gt; &lt;SHA-256&gt;                       per game file that a mod archive or a folder depot provides, the
    ///                                                   copy the game uses (LoadOrder's ranking), sorted by path (lower case,
    ///                                                   '/' separators, ordinal)
    /// Code: the first 8 hex digits (upper case) of the SHA-256 of that text, shown as XXXX-XXXX.
    ///
    /// A file is left OUT when:
    ///   - LooseMods.IsModFile rejects its path (the same rules for archives and folders, so a loose install and a packed one
    ///     count the same files): anything under the player's own folders (saves, campaign saves, films, maps, random maps,
    ///     screenshots, synclogs, User\ settings such as User\UVars.tgi), saved-game, film, campaign, preference, log and
    ///     crash-dump file types, archives inside a folder depot, readmes and other work files (documents, image editor
    ///     files, backups, mod.json, hidden files), and files outside the game's data folders that are not .tgi. Those are
    ///     the player's own files or not game data, and differ between players without changing the match.
    ///   - a base archive wins it (that copy is covered by the archive's identity line);
    ///   - the copy the game uses is byte-identical to the base archive's copy (a hand install often carries unchanged game
    ///     files, a .k2mod package never does: both load the same data).
    /// Files that only a base archive provides are covered by its identity line. The game folder's own data\ files (the Steam
    /// game ships UI/Shared/options_dialog.tgi there) are IN: they are game data the game loads (LooseMods leaves them out of
    /// packing only because they are not part of a mod).
    /// NOT covered: game plugins (winmm.dll and Mods\*.dll change the program, not the data, and need not match on every
    /// PC), user.txt scripts (multiplayer autosave), and settings outside the depots.
    /// </summary>
    public sealed class GameFingerprint
    {
        public const string FormatTag = "k2fp 1";
        public const string ExeName = "k2.exe";
        public const int ShortHashLength = 12;
        /// <summary>A report bigger than this is not one (a 5,000-file report is about 400 KB).</summary>
        public const long MaxReportBytes = 16L << 20;

        /// <summary>"7F3A-91C2".</summary>
        public string Code;
        /// <summary>SHA-256 of k2.exe, or null when there is none.</summary>
        public string ExeSha256;
        public readonly List<ArchiveIdentity> Archives = new List<ArchiveIdentity>();
        /// <summary>Sorted by Key.</summary>
        public readonly List<FingerprintFile> Files = new List<FingerprintFile>();

        /// <summary>Lower case, '/' separators, no leading '/': how paths are compared and sorted.</summary>
        public static string KeyOf(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        static readonly Regex CodePattern = new Regex("^[0-9A-F]{4}-[0-9A-F]{4}$");

        public static bool IsCode(string s) => s != null && CodePattern.IsMatch(s);

        // ------------------------------------------------------------------ computing

        /// <summary>
        /// Works out the fingerprint of one install. Reads only: k2.exe (hashed whole, about 6.6 MB), the directories of the
        /// base archives, and the files that mods and folder depots provide. Pass one cache for a whole session so a repeat
        /// reads only what changed. Throws ModException with a message for players when something cannot be read.
        /// </summary>
        public static GameFingerprint Compute(GameInstall game, FingerprintCache cache = null, CancellationToken cancel = default)
        {
            cache = cache ?? new FingerprintCache();
            try
            {
                return ComputeUnchecked(game, cache, cancel);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ModException("The game files could not be read: " + ex.Message);
            }
        }

        static GameFingerprint ComputeUnchecked(GameInstall game, FingerprintCache cache, CancellationToken cancel)
        {
            var depots = LoadOrder.Read(game);
            var fp = new GameFingerprint();
            var exe = Path.Combine(game.Folder, ExeName);
            fp.ExeSha256 = File.Exists(exe) ? cache.FileSha256(exe, cancel) : null;

            var open = new List<IDisposable>();
            try
            {
                var data = new GameData(game.DataRwd);
                open.Add(data);
                // the game's data folders, for LooseMods.IsModFile (same set as LooseMods.Find builds)
                var top = new HashSet<string>(data.Entries.Where(e => e.Name.Contains("/")).Select(e => e.Name.Split('/')[0]), StringComparer.OrdinalIgnoreCase);

                // every copy of every counted file, collected in rank order (lowest first), so a list's last copy is the winner
                var copies = new Dictionary<string, List<Copy>>(StringComparer.Ordinal);
                void Add(Copy c)
                {
                    var key = KeyOf(c.Name);
                    if (!copies.TryGetValue(key, out var list)) copies[key] = list = new List<Copy>();
                    else if (list[list.Count - 1].Rank == c.Rank) return;   // a name twice in one depot: the first one found stands for it
                    list.Add(c);
                }
                var bases = new List<Copy>();
                for (int rank = 0; rank < depots.Count; rank++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var d = depots[rank];
                    if (d.IsArchive)
                    {
                        var arc = OpenArchive(d, out var state);
                        open.Add(arc);
                        if (d.IsBase)
                        {
                            fp.Archives.Add(Identify(d, arc, state, cache));
                            bases.Add(new Copy { Depot = d, Rank = rank, Archive = arc, ArchiveState = state });
                            continue;
                        }
                        foreach (var e in arc.Entries)
                        {
                            var name = e.Name.Replace('\\', '/').TrimStart('/');
                            if (!LooseMods.IsModFile(name, data, top)) continue;
                            var entry = arc.Find(name);   // the entry a lookup by name gets, if a name is in the archive twice
                            Add(new Copy { Depot = d, Rank = rank, Name = name, Size = entry.Size, Archive = arc, Entry = entry, ArchiveState = state });
                        }
                    }
                    else
                    {
                        var prefix = d.Path.TrimEnd('\\') + "\\";
                        foreach (var f in Directory.EnumerateFiles(d.Path, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.GetFullPath(f).Substring(prefix.Length).Replace('\\', '/');
                            if (!LooseMods.IsModFile(rel, data, top)) continue;
                            Add(new Copy { Depot = d, Rank = rank, Name = rel, Size = new FileInfo(f).Length, File = f });
                        }
                    }
                }

                foreach (var kv in copies.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();
                    var winner = kv.Value[kv.Value.Count - 1];
                    Copy own = null;   // the base archive copy the game would use without any mod
                    foreach (var b in bases)
                    {
                        var e = b.Archive.Find(winner.Name);
                        if (e != null && (own == null || b.Rank > own.Rank))
                            own = new Copy { Depot = b.Depot, Rank = b.Rank, Name = e.Name, Size = e.Size, Archive = b.Archive, Entry = e, ArchiveState = b.ArchiveState };
                    }
                    if (own != null && own.Rank > winner.Rank) continue;   // the base archive wins: covered by its identity
                    var sha = winner.Sha256(cache, cancel);
                    if (own != null && own.Size == winner.Size && own.Sha256(cache, cancel) == sha) continue;   // the game's own bytes
                    fp.Files.Add(new FingerprintFile
                    {
                        Path = data.Canon(winner.Name) ?? winner.Name,
                        Sha256 = sha,
                        Source = winner.Depot.IsArchive ? winner.Depot.Label : "loose: " + winner.Depot.Label,
                    });
                }
            }
            finally
            {
                foreach (var o in open) o.Dispose();
            }
            fp.Code = CodeOf(fp.CanonicalText());
            return fp;
        }

        sealed class Copy
        {
            public GameDepot Depot;
            public int Rank;
            public string Name;
            public long Size;
            public string File;              // a loose copy
            public RwdArchive Archive;       // or an archived one
            public RwdEntry Entry;
            public string ArchiveState;      // the archive's cache identity, null when it changed while it was opened

            public string Sha256(FingerprintCache cache, CancellationToken cancel)
            {
                if (File != null) return cache.FileSha256(File, cancel);
                var key = ArchiveState == null ? null
                    : "entry|" + ArchiveState + "|" + Entry.Offset.ToString(CultureInfo.InvariantCulture) + "|" +
                      Entry.Size.ToString(CultureInfo.InvariantCulture) + "|" + Entry.Name.ToLowerInvariant();
                return cache.Get(key, () => Codec.Sha256Hex(Archive.Read(Entry)));
            }
        }

        /// <summary>Opens a mounted archive; state is its cache identity, or null when the file changed while it was being opened (nothing from it is cached then).</summary>
        static RwdArchive OpenArchive(GameDepot d, out string state)
        {
            var before = FingerprintCache.StateOf(d.Path);
            RwdArchive arc;
            try { arc = new RwdArchive(d.Path); }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is ArgumentException)
            {
                throw new ModException($"{d.Label} (loaded by startup\\{d.Script}) is not a Kohan II archive that can be read.");
            }
            state = FingerprintCache.StateOf(d.Path) == before ? before : null;
            return arc;
        }

        static ArchiveIdentity Identify(GameDepot d, RwdArchive arc, string state, FingerprintCache cache) => new ArchiveIdentity
        {
            Label = d.Label,
            Stamp = arc.Stamp,
            Bytes = arc.Length,
            DirSha256 = cache.Get(state == null ? null : "dir|" + state, () =>
            {
                var sb = new StringBuilder();
                foreach (var e in arc.Entries)
                    sb.Append(e.Name).Append('\t').Append(e.Offset.ToString(CultureInfo.InvariantCulture))
                      .Append('\t').Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
                return Codec.Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
            }),
        };

        /// <summary>The text the code is the hash of (see the class summary). Needs the full hashes, so only for a computed fingerprint.</summary>
        public string CanonicalText()
        {
            var sb = new StringBuilder();
            sb.Append(FormatTag).Append('\n');
            sb.Append("exe\t").Append(ExeSha256 ?? "none").Append('\n');
            foreach (var a in Archives)
                sb.Append("base\t").Append(a.Key).Append('\t').Append(a.Stamp.ToString("x8")).Append('\t')
                  .Append(a.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(a.DirSha256).Append('\n');
            foreach (var f in Files)
            {
                if (f.Sha256 == null) throw new InvalidOperationException("a fingerprint read from a report has no full hashes");
                sb.Append("file\t").Append(f.Key).Append('\t').Append(f.Sha256).Append('\n');
            }
            return sb.ToString();
        }

        public static string CodeOf(string canonicalText)
        {
            using (var sha = SHA256.Create())
            {
                var hex = Codec.Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(canonicalText))).ToUpperInvariant();
                return hex.Substring(0, 4) + "-" + hex.Substring(4, 4);
            }
        }

        // ------------------------------------------------------------------ words for players

        /// <summary>Where the listed files come from, most first: ("Documents\Kohan2\data" loose, 211).</summary>
        public List<KeyValuePair<string, int>> Sources() =>
            Files.GroupBy(f => f.Source).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                 .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).ToList();

        /// <summary>"Made from k2.exe, Data.rwd, Music.rwd and 214 files that mods and loose folders add or change (211 loose in Documents\Kohan2\data, 3 from Warchest/Warchest.rwd)."</summary>
        public string Summary()
        {
            var parts = new List<string>();
            if (ExeSha256 != null) parts.Add(ExeName);
            parts.AddRange(Archives.Select(a => a.Label));
            var files = Files.Count == 0 ? "no files that mods or loose folders add or change"
                : $"{Files.Count} file{(Files.Count == 1 ? "" : "s")} that mods and loose folders add or change";
            var from = Files.Count == 0 ? "" : " (" + string.Join(", ", Sources().Select(s =>
                $"{s.Value} {(s.Key.StartsWith("loose: ") ? "loose in " + s.Key.Substring(7) : "from " + s.Key)}")) + ")";
            return "Made from " + (parts.Count > 0 ? string.Join(", ", parts) + " and " : "") + files + from + ".";
        }

        // ------------------------------------------------------------------ reports

        /// <summary>This program's version ("0.1.0-beta.5"), for report headers.</summary>
        public static string ToolVersion()
        {
            var a = Assembly.GetEntryAssembly() ?? typeof(GameFingerprint).Assembly;
            var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version.ToString();
            int plus = v.IndexOf('+');
            return plus >= 0 ? v.Substring(0, plus) : v;
        }

        /// <summary>"game-files-7F3A-91C2.txt".</summary>
        public string ReportFileName => "game-files-" + Code + ".txt";

        /// <summary>
        /// The plain-text report players exchange: a header (format tag, code, k2.exe hash, archive identities), then one line
        /// per listed file: short hash | path | where the game takes it from. No absolute paths, no user names: depots appear
        /// by their labels. CRLF line endings (Notepad); ParseReport takes any.
        /// </summary>
        public string ToReport(string madeBy)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append(s).Append("\r\n");
            Line($"# Kohan II game files report, made by {madeBy}.");
            Line("# Players whose codes match load exactly the same game data. To see what differs, compare this report with");
            Line("# yours in the Kohan II Mod Manager (Game files, Details..., Compare) or with: k2mod fingerprint --compare <file>");
            Line("format: " + FormatTag);
            Line("code: " + Code);
            Line("k2.exe: " + (ExeSha256 ?? "none"));
            foreach (var a in Archives)
                Line($"archive: {a.Label} | stamp {a.Stamp:x8} | {a.Bytes.ToString(CultureInfo.InvariantCulture)} bytes | directory {a.DirSha256}");
            Line("files: " + Files.Count.ToString(CultureInfo.InvariantCulture));
            Line($"# first {ShortHashLength} digits of the file's SHA-256 | file | where the game takes it from");
            foreach (var f in Files) Line($"{f.ShortHash} | {f.Path} | {f.Source}");
            return sb.ToString();
        }

        static readonly Regex FileLine = new Regex(@"^([0-9a-fA-F]{12}) \| (.+?) \| (.+)$");
        static readonly Regex ArchiveLine = new Regex(@"^(.+?) \| stamp ([0-9a-fA-F]{8}) \| (\d+) bytes \| directory ([0-9a-fA-F]{64})$");
        static readonly Regex Sha = new Regex("^[0-9a-fA-F]{64}$");

        /// <summary>Reads a report written by ToReport. Throws ModException, in words for players, for anything else.</summary>
        public static GameFingerprint ParseReport(string text)
        {
            const string notReport = "That file is not a game files report from the Kohan II Mod Manager";
            var fp = new GameFingerprint();
            string format = null;
            int? count = null;
            int n = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                n++;
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                ModException Bad() => new ModException($"{notReport} (line {n} is not as expected).");
                var m = FileLine.Match(line);
                if (m.Success)
                {
                    if (format == null) throw new ModException(notReport + ".");
                    fp.Files.Add(new FingerprintFile { ShortHash = m.Groups[1].Value.ToLowerInvariant(), Path = m.Groups[2].Value.Trim(), Source = m.Groups[3].Value.Trim() });
                    continue;
                }
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0) throw format == null ? new ModException(notReport + ".") : Bad();
                var key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 2).Trim();
                if (key == "format")
                {
                    if (value != FormatTag)
                        throw value.StartsWith("k2fp ", StringComparison.Ordinal)
                            ? new ModException($"That report was made by another version of the Kohan II Mod Manager (report format \"{value}\", this one reads \"{FormatTag}\"). Use the same version to compare.")
                            : new ModException(notReport + ".");
                    format = value;
                    continue;
                }
                if (format == null) throw new ModException(notReport + ".");
                switch (key)
                {
                    case "code":
                        if (!IsCode(value)) throw Bad();
                        fp.Code = value;
                        break;
                    case "k2.exe":
                        if (value != "none" && !Sha.IsMatch(value)) throw Bad();
                        fp.ExeSha256 = value == "none" ? null : value.ToLowerInvariant();
                        break;
                    case "archive":
                        var a = ArchiveLine.Match(value);
                        if (!a.Success) throw Bad();
                        fp.Archives.Add(new ArchiveIdentity
                        {
                            Label = a.Groups[1].Value.Trim(),
                            Stamp = uint.Parse(a.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            Bytes = long.Parse(a.Groups[3].Value, CultureInfo.InvariantCulture),
                            DirSha256 = a.Groups[4].Value.ToLowerInvariant(),
                        });
                        break;
                    case "files":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var c)) throw Bad();
                        count = c;
                        break;
                    default:
                        throw Bad();
                }
            }
            if (format == null) throw new ModException(notReport + ".");
            if (fp.Code == null || count == null) throw new ModException(notReport + " (its header is incomplete).");
            if (count.Value != fp.Files.Count)
                throw new ModException($"That report is incomplete: it says {count} files but lists {fp.Files.Count}. Ask for the file itself rather than a copy of its text.");
            fp.Files.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return fp;
        }
    }

    /// <summary>What differs between two players' game files, in plain words.</summary>
    public sealed class FingerprintComparison
    {
        public sealed class FileDifference
        {
            public FingerprintFile Mine, Theirs;
        }

        public sealed class ArchiveDifference
        {
            public string Label;
            /// <summary>Null when that side does not load the archive.</summary>
            public ArchiveIdentity Mine, Theirs;
        }

        public GameFingerprint Mine { get; }
        public GameFingerprint Theirs { get; }
        public bool ExeDiffers { get; }
        public List<ArchiveDifference> Archives { get; } = new List<ArchiveDifference>();
        /// <summary>Files only my game changes or adds (theirs uses the game's own copy, or has no such file).</summary>
        public List<FingerprintFile> OnlyMine { get; } = new List<FingerprintFile>();
        public List<FingerprintFile> OnlyTheirs { get; } = new List<FingerprintFile>();
        public List<FileDifference> Different { get; } = new List<FileDifference>();

        /// <summary>The codes are the same: both games load the same data.</summary>
        public bool Same => string.Equals(Mine.Code, Theirs.Code, StringComparison.OrdinalIgnoreCase);

        public FingerprintComparison(GameFingerprint mine, GameFingerprint theirs)
        {
            Mine = mine;
            Theirs = theirs;
            ExeDiffers = !string.Equals(mine.ExeSha256, theirs.ExeSha256, StringComparison.OrdinalIgnoreCase);
            var labels = mine.Archives.Select(a => a.Key).Concat(theirs.Archives.Select(a => a.Key)).Distinct().ToList();
            foreach (var key in labels)
            {
                var a = mine.Archives.FirstOrDefault(x => x.Key == key);
                var b = theirs.Archives.FirstOrDefault(x => x.Key == key);
                if (a == null || !a.SameAs(b)) Archives.Add(new ArchiveDifference { Label = (a ?? b).Label, Mine = a, Theirs = b });
            }
            var theirFiles = new Dictionary<string, FingerprintFile>(StringComparer.Ordinal);
            foreach (var f in theirs.Files) theirFiles[f.Key] = f;
            var mineKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in mine.Files)
            {
                mineKeys.Add(f.Key);
                if (!theirFiles.TryGetValue(f.Key, out var t)) OnlyMine.Add(f);
                else if (!string.Equals(f.ShortHash, t.ShortHash, StringComparison.OrdinalIgnoreCase)) Different.Add(new FileDifference { Mine = f, Theirs = t });
            }
            OnlyTheirs.AddRange(theirs.Files.Where(f => !mineKeys.Contains(f.Key)));
        }

        public string Headline => Same
            ? $"Same game files: both codes are {Mine.Code}."
            : $"Different game files: your code is {Mine.Code}, theirs is {Theirs.Code}.";

        static string Files(int n) => n == 1 ? "1 file" : n + " files";

        /// <summary>The differences, one sentence or file per line; file lines start with two spaces.</summary>
        public List<string> Lines()
        {
            var o = new List<string>();
            if (Same)
            {
                o.Add("Your game and theirs load exactly the same game data.");
                return o;
            }
            if (ExeDiffers)
                o.Add(Mine.ExeSha256 == null || Theirs.ExeSha256 == null
                    ? $"Only one of you has the game program ({GameFingerprint.ExeName}) where the launcher looked."
                    : $"The game program ({GameFingerprint.ExeName}) is different: you have different versions of Kohan II (for example one of you uses a Steam beta), or one {GameFingerprint.ExeName} was changed.");
            foreach (var a in Archives)
                o.Add(a.Mine == null ? $"Only their game loads {a.Label}."
                    : a.Theirs == null ? $"Only your game loads {a.Label}."
                    : $"The game's own {a.Label} is different (yours: {a.Mine.Describe()}; theirs: {a.Theirs.Describe()}). " +
                      "Let Steam update the game, or check its files in Steam (Properties, Installed Files, Verify integrity).");
            if (Different.Count > 0)
            {
                o.Add($"{Files(Different.Count)} {(Different.Count == 1 ? "differs" : "differ")}:");
                foreach (var d in Different)
                    o.Add($"  {d.Mine.Path}   (yours from {d.Mine.Source}; theirs from {d.Theirs.Source})");
            }
            if (OnlyMine.Count > 0)
            {
                o.Add($"{Files(OnlyMine.Count)} only your game adds or changes (theirs uses the game's own copy or has no such file):");
                foreach (var f in OnlyMine) o.Add($"  {f.Path}   (from {f.Source})");
            }
            if (OnlyTheirs.Count > 0)
            {
                o.Add($"{Files(OnlyTheirs.Count)} only their game adds or changes (yours uses the game's own copy or has no such file):");
                foreach (var f in OnlyTheirs) o.Add($"  {f.Path}   (from {f.Source})");
            }
            if (!ExeDiffers && Archives.Count == 0 && Different.Count + OnlyMine.Count + OnlyTheirs.Count == 0)
                o.Add("The codes differ, but no single difference shows. One of the two reports may be out of date: make both again and compare.");
            else
                o.Add("To match, use the same game version, the same mods in the same order, and the same files in Documents\\Kohan2\\data.");
            return o;
        }

        public override string ToString() => Headline + Environment.NewLine + string.Join(Environment.NewLine, Lines());
    }
}
