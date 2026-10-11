using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace K2Mods
{
    /// <summary>
    /// Kohan II .rwd resource archive (TimeGate "TGCK"), little-endian:
    ///   "TGCK", u32 version 2, u32 section count 3
    ///   Header section: u32 2, u16 n + wchar[n] game id "K2", u32 0, u32 stamp
    ///   Files section: every file's bytes back to back in load-index order
    ///   Footer section: u32 count; per file u16 n + wchar[n] name, u64 offset (from the
    ///     Files section), u64 size, u32 0, u32 load index
    ///   Section table at the end, 96 bytes each: wchar[32] name, u64 offset, u64 size,
    ///     u32 x2 (junk in TimeGate's packer), u64 size
    /// The stamp is what the multiplayer handshake compares per archive.
    /// </summary>
    public sealed class RwdEntry
    {
        public string Name;
        public long Offset;
        public long Size;
        public int Index;
    }

    public sealed class RwdArchive : IDisposable
    {
        const int SectionRecord = 96;
        readonly FileStream fs;
        readonly long filesBase;
        readonly Dictionary<string, RwdEntry> byName = new Dictionary<string, RwdEntry>(StringComparer.OrdinalIgnoreCase);

        public string Path { get; }
        public List<RwdEntry> Entries { get; } = new List<RwdEntry>();
        public uint Stamp { get; }
        public long Length => fs.Length;

        public RwdArchive(string path)
        {
            Path = path;
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var r = new BinaryReader(fs);
                if (fs.Length < 12 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "TGCK")
                    throw new InvalidDataException($"{path} is not a Kohan II .rwd archive");
                r.ReadUInt32();
                int sections = r.ReadInt32();
                if (sections < 3 || sections > 16) throw new InvalidDataException($"{path}: unexpected section count {sections}");
                fs.Seek(fs.Length - SectionRecord * sections, SeekOrigin.Begin);
                var table = new Dictionary<string, (long off, long size)>();
                for (int i = 0; i < sections; i++)
                {
                    var name = Encoding.Unicode.GetString(r.ReadBytes(64)).Split('\0')[0];
                    long off = r.ReadInt64(), size = r.ReadInt64();
                    r.ReadBytes(16);
                    table[name] = (off, size);
                }
                if (!table.ContainsKey("Header") || !table.ContainsKey("Files") || !table.ContainsKey("Footer"))
                    throw new InvalidDataException($"{path}: missing archive sections");
                fs.Seek(table["Header"].off, SeekOrigin.Begin);
                var header = r.ReadBytes((int)table["Header"].size);
                Stamp = BitConverter.ToUInt32(header, header.Length - 4);
                filesBase = table["Files"].off;
                fs.Seek(table["Footer"].off, SeekOrigin.Begin);
                var foot = new BinaryReader(new MemoryStream(r.ReadBytes((int)table["Footer"].size)));
                int count = foot.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int n = foot.ReadUInt16();
                    var e = new RwdEntry { Name = Encoding.Unicode.GetString(foot.ReadBytes(2 * n)) };
                    e.Offset = foot.ReadInt64();
                    e.Size = foot.ReadInt64();
                    foot.ReadUInt32();
                    e.Index = foot.ReadInt32();
                    Entries.Add(e);
                    byName[e.Name.Replace('\\', '/')] = e;
                }
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public RwdEntry Find(string name) =>
            byName.TryGetValue(name.Replace('\\', '/').TrimStart('/'), out var e) ? e : null;

        public byte[] Read(RwdEntry e)
        {
            var buf = new byte[e.Size];
            lock (fs)
            {
                fs.Seek(filesBase + e.Offset, SeekOrigin.Begin);
                int got = 0;
                while (got < buf.Length)
                {
                    int n = fs.Read(buf, got, buf.Length - got);
                    if (n <= 0) throw new EndOfStreamException($"{Path}: {e.Name} is truncated");
                    got += n;
                }
            }
            return buf;
        }

        public byte[] Read(string name)
        {
            var e = Find(name);
            return e == null ? null : Read(e);
        }

        public void Dispose() => fs.Dispose();

        /// <summary>Lower-cased names sorted ordinally: the order every Battleborn-style build uses.</summary>
        public static List<KeyValuePair<string, byte[]>> SortForBuild(IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            var list = files.ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in list)
                if (!seen.Add(f.Key)) throw new InvalidDataException($"{f.Key} appears twice (names are not case-sensitive)");
            return list.OrderBy(f => f.Key.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Deterministic stamp: CRC32 of SHA-256 over (lower-case name, NUL, bytes) per file in
        /// build order. Identical content gives an identical archive on every machine, which is
        /// what the multiplayer checksum needs.
        /// </summary>
        public static uint DefaultStamp(IList<KeyValuePair<string, byte[]>> sorted)
        {
            using (var h = SHA256.Create())
            {
                foreach (var f in sorted)
                {
                    var n = Encoding.UTF8.GetBytes(f.Key.ToLowerInvariant() + "\0");
                    h.TransformBlock(n, 0, n.Length, null, 0);
                    h.TransformBlock(f.Value, 0, f.Value.Length, null, 0);
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                return Codec.Crc32(h.Hash);
            }
        }

        /// <summary>Writes files (sorted with SortForBuild) to an archive; returns the stamp.</summary>
        public static uint Write(string path, IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            var sorted = SortForBuild(files.Select(f => new KeyValuePair<string, byte[]>(f.Key.Replace('\\', '/'), f.Value)));
            uint stamp = DefaultStamp(sorted);
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(o))
            {
                w.Write(Encoding.ASCII.GetBytes("TGCK"));
                w.Write(2u);
                w.Write(3u);
                long hdrOff = o.Position;
                var gid = Encoding.Unicode.GetBytes("K2");
                w.Write(2u);
                w.Write((ushort)2);
                w.Write(gid);
                w.Write(0u);
                w.Write(stamp);
                long hdrSize = o.Position - hdrOff;
                long filesOff = o.Position;
                var offsets = new long[sorted.Count];
                long rel = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    offsets[i] = rel;
                    w.Write(sorted[i].Value);
                    rel += sorted[i].Value.Length;
                }
                long footOff = o.Position;
                w.Write(sorted.Count);
                for (int i = 0; i < sorted.Count; i++)
                {
                    w.Write((ushort)sorted[i].Key.Length);
                    w.Write(Encoding.Unicode.GetBytes(sorted[i].Key));
                    w.Write(offsets[i]);
                    w.Write((long)sorted[i].Value.Length);
                    w.Write(0u);
                    w.Write(i);
                }
                long footSize = o.Position - footOff;
                foreach (var (name, off, size) in new[] { ("Header", hdrOff, hdrSize), ("Files", filesOff, rel), ("Footer", footOff, footSize) })
                {
                    var rec = new byte[64];
                    var nb = Encoding.Unicode.GetBytes(name);
                    Array.Copy(nb, rec, nb.Length);
                    w.Write(rec);
                    w.Write(off);
                    w.Write(size);
                    w.Write(0u);
                    w.Write(0u);
                    w.Write(size);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return stamp;
        }
    }
}
