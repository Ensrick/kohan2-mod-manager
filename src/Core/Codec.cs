using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace K2Mods
{
    /// <summary>Hashes, CRC32 and zlib streams.</summary>
    public static class Codec
    {
        /// <summary>Maps bytes 0-255 to chars 0-255 one to one, so byte lines can be dictionary keys.</summary>
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        public static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

        public static string Sha256Hex(byte[] data) => Sha256Hex(data, 0, data.Length);

        public static string Sha256Hex(byte[] data, int offset, int count)
        {
            using (var h = SHA256.Create())
                return Hex(h.ComputeHash(data, offset, count));
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>Same value as Python's zlib.crc32.</summary>
        public static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var x in data)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        /// <summary>zlib stream (RFC 1950), readable by Python's zlib.decompress.</summary>
        public static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0xDA);
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    z.Write(data, 0, data.Length);
                uint ad = Adler32(data);
                ms.WriteByte((byte)(ad >> 24));
                ms.WriteByte((byte)(ad >> 16));
                ms.WriteByte((byte)(ad >> 8));
                ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        public static byte[] ZlibDecompress(byte[] data, int offset, int count)
        {
            if (count < 6 || (data[offset] & 0x0F) != 8 || (data[offset + 1] & 0x20) != 0)
                throw new InvalidDataException("not a zlib stream");
            using (var src = new MemoryStream(data, offset + 2, count - 2))
            using (var z = new DeflateStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                z.CopyTo(dst);
                var result = dst.ToArray();
                int t = offset + count - 4;
                uint want = (uint)(data[t] << 24 | data[t + 1] << 16 | data[t + 2] << 8 | data[t + 3]);
                if (Adler32(result) != want) throw new InvalidDataException("zlib checksum mismatch");
                return result;
            }
        }
    }
}
