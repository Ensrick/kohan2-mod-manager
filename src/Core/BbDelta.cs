using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace K2Mods
{
    /// <summary>
    /// Binary delta against a game file: the mod's file XOR-ed with the game file's bytes
    /// (repeated to length), zlib-compressed. Without the player's own Data.rwd it is noise.
    ///   BBDELTA1\n
    ///   {"base": "...", "base_sha256": "...", "bytes": n, "result_sha256": "..."}\n
    ///   zlib stream
    /// </summary>
    public static class BbDelta
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("BBDELTA1\n");

        static byte[] Mask(byte[] data, byte[] key)
        {
            if (key.Length == 0) throw new InvalidDataException("empty base file");
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ key[i % key.Length]);
            return o;
        }

        public static byte[] Make(string baseName, byte[] baseData, byte[] mod)
        {
            var meta = new Dictionary<string, object>
            {
                ["base"] = baseName,
                ["base_sha256"] = Codec.Sha256Hex(baseData),
                ["result_sha256"] = Codec.Sha256Hex(mod),
                ["bytes"] = (long)mod.Length,
            };
            var head = Encoding.ASCII.GetBytes(Json.Write(meta, pretty: false, sortKeys: true) + "\n");
            var z = Codec.ZlibCompress(Mask(mod, baseData));
            var o = new byte[Magic.Length + head.Length + z.Length];
            Buffer.BlockCopy(Magic, 0, o, 0, Magic.Length);
            Buffer.BlockCopy(head, 0, o, Magic.Length, head.Length);
            Buffer.BlockCopy(z, 0, o, Magic.Length + head.Length, z.Length);
            return o;
        }

        public static bool IsDelta(byte[] blob)
        {
            if (blob.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (blob[i] != Magic[i]) return false;
            return true;
        }

        public static Dictionary<string, object> ReadMeta(byte[] blob, out int payloadOffset)
        {
            if (!IsDelta(blob)) throw new InvalidDataException("not a .bbdelta file");
            int nl = Array.IndexOf(blob, (byte)'\n', Magic.Length);
            if (nl < 0) throw new InvalidDataException("truncated .bbdelta file");
            payloadOffset = nl + 1;
            return Json.ParseObject(Encoding.UTF8.GetString(blob, Magic.Length, nl - Magic.Length));
        }

        public static byte[] Apply(byte[] blob, Func<string, byte[]> getFile, string label)
        {
            var meta = ReadMeta(blob, out int off);
            var baseName = Json.Str(meta, "base");
            var baseData = getFile(baseName)
                ?? throw new ModException($"{label}: needs the game file {baseName}, which is not in your Data.rwd");
            if (Codec.Sha256Hex(baseData) != Json.Str(meta, "base_sha256"))
                throw new ModException($"{label}: your copy of {baseName} differs from the version this mod was made for");
            var o = Mask(Codec.ZlibDecompress(blob, off, blob.Length - off), baseData);
            if (o.Length != Json.Long(meta, "bytes") || Codec.Sha256Hex(o) != Json.Str(meta, "result_sha256"))
                throw new ModException($"{label}: the rebuilt file does not match what the mod author packed");
            return o;
        }
    }

    /// <summary>A problem the user can act on; the message is shown as-is.</summary>
    public sealed class ModException : Exception
    {
        public ModException(string message) : base(message) { }
    }
}
