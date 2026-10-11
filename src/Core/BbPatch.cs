using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace K2Mods
{
    /// <summary>
    /// Text patch against the player's own game file (the Battleborn .bbpatch format; this is a
    /// port of kohan2-battleborn tools/bbpatch.py and produces identical patches). A patch holds
    /// only the mod's own lines: removed game lines are a count and a hash, and any block the
    /// mod reuses from a game file is a reference (file + line range) read from Data.rwd.
    ///
    ///   # comment
    ///   base &lt;path&gt; sha256=&lt;hex&gt;      (omitted for a file with no base)
    ///   ref &lt;path&gt; sha256=&lt;hex&gt;       (each other game file referenced)
    ///   result sha256=&lt;hex&gt; bytes=&lt;n&gt;
    ///   bom yes|no
    ///   eol crlf | lf | crlf*120 lf*3 none*1
    ///
    ///   @@ &lt;line&gt; -&lt;count&gt;[:&lt;hash8&gt;] +&lt;count&gt; @@ &lt;section, informational&gt;
    ///   +&lt;text&gt;
    ///   = &lt;path&gt; &lt;first&gt;-&lt;last&gt;
    ///
    /// Lines are handled as Latin-1 strings, so every byte maps to one char and back.
    /// </summary>
    public static class BbPatch
    {
        public const int MinRefRun = 3;
        public const string DefaultComment = "# Kohan II mod patch - only the mod's own lines; game text is referenced, never copied.";

        public sealed class Lines
        {
            public bool Bom;
            public List<string> Bodies = new List<string>();
            public List<string> Terms = new List<string>();
        }

        public static Lines Split(byte[] data)
        {
            var r = new Lines();
            int start = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                r.Bom = true;
                start = 3;
            }
            var s = Codec.Latin1.GetString(data, start, data.Length - start);
            int p = 0;
            while (p < s.Length)
            {
                int nl = s.IndexOf('\n', p);
                if (nl < 0)
                {
                    r.Bodies.Add(s.Substring(p));
                    r.Terms.Add("");
                    break;
                }
                if (nl > p && s[nl - 1] == '\r')
                {
                    r.Bodies.Add(s.Substring(p, nl - 1 - p));
                    r.Terms.Add("\r\n");
                }
                else
                {
                    r.Bodies.Add(s.Substring(p, nl - p));
                    r.Terms.Add("\n");
                }
                p = nl + 1;
            }
            return r;
        }

        static readonly char[] Ws = { ' ', '\t', '\n', '\r', '\x0b', '\x0c' };

        /// <summary>Python bytes.strip(): ASCII whitespace only.</summary>
        public static string Strip(string s) => s.Trim(Ws);

        public static bool IsSubstantive(string body)
        {
            var s = Strip(body);
            return s.Length > 3 && s != "{" && s != "}";
        }

        static string Utf8(string latin1, bool strict)
        {
            var b = Codec.Latin1.GetBytes(latin1);
            return strict ? Codec.Utf8Strict.GetString(b) : Encoding.UTF8.GetString(b);
        }

        static string Sha(string latin1) => Codec.Sha256Hex(Codec.Latin1.GetBytes(latin1));

        const string W = @"[ \t\n\r\x0b\x0c]";
        static readonly Regex HeaderRe = new Regex(@"^" + W + @"*\[" + W + @"*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
        static readonly Regex IdsRe = new Regex(@"^" + W + @"*[Ii][Dd][Ss]" + W + @"*=" + W + @"*([^ \t\n\r\x0b\x0c;]+)", RegexOptions.CultureInvariant);

        /// <summary>For each line, a readable location like "[Unit bowman] > [BodyComponent]".</summary>
        public static List<string> SectionPaths(List<string> bodies)
        {
            var paths = new List<string>(bodies.Count);
            var stack = new List<string>();
            var loose = new Dictionary<int, string>();
            string pending = null;
            foreach (var body in bodies)
            {
                int semi = body.IndexOf(';');
                var s = Strip(semi >= 0 ? body.Substring(0, semi) : body);
                int depth = stack.Count;
                var cur = new List<string>(stack);
                if (loose.TryGetValue(depth, out var l)) cur.Add(l);
                paths.Add(string.Join(" > ", cur));
                if (s.Length == 0) continue;
                var m = HeaderRe.Match(s);
                if (m.Success)
                {
                    pending = "[" + m.Groups[1].Value + "]";
                    loose[depth] = pending;
                }
                m = IdsRe.Match(s);
                if (m.Success)
                {
                    var name = Utf8(m.Groups[1].Value, false);
                    if (loose.TryGetValue(depth, out var ld) && !ld.Contains(" "))
                        loose[depth] = ld.Substring(0, ld.Length - 1) + " " + name + "]";
                    else if (stack.Count > 0 && !stack[stack.Count - 1].Contains(" "))
                    {
                        var top = stack[stack.Count - 1];
                        stack[stack.Count - 1] = top.Substring(0, top.Length - 1) + " " + name + "]";
                    }
                }
                foreach (char ch in s)
                {
                    if (ch == '{')
                    {
                        if (loose.TryGetValue(stack.Count, out var lab)) loose.Remove(stack.Count);
                        else lab = pending ?? "{}";
                        stack.Add(lab);
                        pending = null;
                    }
                    else if (ch == '}')
                    {
                        loose.Remove(stack.Count);
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
            return paths;
        }

        /// <summary>Index of game-file lines, used to turn reused blocks into references.</summary>
        public sealed class RefIndex
        {
            public sealed class FileLines
            {
                public string Sha;
                public List<string> Bodies;
                public List<string> Norms;
            }

            public readonly Dictionary<string, FileLines> Files = new Dictionary<string, FileLines>(StringComparer.Ordinal);
            readonly Dictionary<string, List<KeyValuePair<string, int>>> byNorm = new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.Ordinal);

            public void Add(string name, byte[] data, int cap = 4000)
            {
                var bodies = Split(data).Bodies;
                var norms = bodies.Select(Strip).ToList();
                Files[name] = new FileLines { Sha = Codec.Sha256Hex(data), Bodies = bodies, Norms = norms };
                for (int i = 0; i < norms.Count; i++)
                {
                    if (!IsSubstantive(norms[i])) continue;
                    if (!byNorm.TryGetValue(norms[i], out var lst)) byNorm[norms[i]] = lst = new List<KeyValuePair<string, int>>();
                    if (lst.Count < cap) lst.Add(new KeyValuePair<string, int>(name, i));
                }
            }

            /// <summary>(length, file, start, substantive lines) of the best indentation-blind match.</summary>
            public (int n, string name, int idx, int subst) LongestRun(List<string> added, int k, string prefer)
            {
                var best = (n: 0, name: (string)null, idx: 0, subst: 0);
                if (!byNorm.TryGetValue(Strip(added[k]), out var cands)) return best;
                var target = new string[added.Count];
                foreach (var c in cands)
                {
                    var norms = Files[c.Key].Norms;
                    int n = 0, subst = 0;
                    while (k + n < added.Count && c.Value + n < norms.Count)
                    {
                        var t = target[k + n] ?? (target[k + n] = Strip(added[k + n]));
                        if (norms[c.Value + n] != t) break;
                        if (IsSubstantive(t)) subst++;
                        n++;
                    }
                    if (subst > best.subst || (subst == best.subst && c.Key == prefer && best.name != prefer))
                        best = (n, c.Key, c.Value, subst);
                }
                return best;
            }
        }

        static readonly Dictionary<string, string> TermName = new Dictionary<string, string> { ["\r\n"] = "crlf", ["\n"] = "lf", [""] = "none" };
        static readonly Dictionary<string, string> TermBytes = new Dictionary<string, string> { ["crlf"] = "\r\n", ["lf"] = "\n", ["none"] = "" };

        public static string EolSpec(List<string> terms)
        {
            var runs = new List<KeyValuePair<string, int>>();
            foreach (var t in terms)
            {
                var name = TermName[t];
                if (runs.Count > 0 && runs[runs.Count - 1].Key == name)
                    runs[runs.Count - 1] = new KeyValuePair<string, int>(name, runs[runs.Count - 1].Value + 1);
                else
                    runs.Add(new KeyValuePair<string, int>(name, 1));
            }
            if (runs.Count == 1) return runs[0].Key;
            if (runs.Count == 2 && runs[1].Key == "none" && runs[1].Value == 1) return runs[0].Key + " none*1";
            return string.Join(" ", runs.Select(r => r.Key + "*" + r.Value));
        }

        public static List<string> EolExpand(string spec, int count)
        {
            var parts = spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var o = new List<string>();
            if (parts.Length == 1 && !parts[0].Contains("*"))
            {
                for (int i = 0; i < count; i++) o.Add(TermBytes[parts[0]]);
                return o;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Contains("*"))
                {
                    var bits = p.Split('*');
                    int c = int.Parse(bits[1]);
                    for (int x = 0; x < c; x++) o.Add(TermBytes[bits[0]]);
                }
                else
                {
                    int rest = parts.Skip(i + 1).Sum(q => int.Parse(q.Split('*')[1]));
                    int fill = count - o.Count - rest;
                    for (int x = 0; x < fill; x++) o.Add(TermBytes[p]);
                }
            }
            return o;
        }

        /// <summary>
        /// Patch text turning baseData (null for a file with no base) into mod. Throws
        /// DecoderFallbackException when a changed line is not valid UTF-8 (use a delta then).
        /// </summary>
        public static string Make(string baseName, byte[] baseData, byte[] mod, RefIndex index, string comment = DefaultComment)
        {
            var bLines = baseData != null ? Split(baseData) : new Lines();
            var m = Split(mod);
            var paths = SectionPaths(bLines.Bodies.Concat(new[] { "" }).ToList());
            var hunks = new List<string>();
            var refs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var op in SequenceMatcher.Opcodes(bLines.Bodies, m.Bodies))
            {
                if (op.tag == 'e') continue;
                var added = m.Bodies.GetRange(op.j1, op.j2 - op.j1);
                var body = new List<string>();
                int k = 0;
                while (k < added.Count)
                {
                    if (index != null && IsSubstantive(added[k]))
                    {
                        var (n, name, idx, subst) = index.LongestRun(added, k, baseName);
                        if (subst >= MinRefRun)
                        {
                            var src = index.Files[name].Bodies;
                            int j = 0;
                            while (j < n)
                            {
                                if (added[k + j] != src[idx + j])
                                {
                                    body.Add("+" + Utf8(added[k + j], true));
                                    j++;
                                    continue;
                                }
                                int e = j;
                                while (e < n && added[k + e] == src[idx + e]) e++;
                                body.Add($"= {name} {idx + j + 1}-{idx + e}");
                                j = e;
                            }
                            if (name != baseName) refs[name] = index.Files[name].Sha;
                            k += n;
                            continue;
                        }
                    }
                    body.Add("+" + Utf8(added[k], true));
                    k++;
                }
                int del = op.i2 - op.i1;
                var rm = "-" + del + (del > 0 ? ":" + Sha(string.Join("\n", bLines.Bodies.GetRange(op.i1, del))).Substring(0, 8) : "");
                hunks.Add($"@@ {op.i1 + 1} {rm} +{op.j2 - op.j1} @@ {paths[op.i1]}".TrimEnd());
                hunks.AddRange(body);
            }
            var o = new List<string> { comment };
            if (baseData != null) o.Add($"base {baseName} sha256={Codec.Sha256Hex(baseData)}");
            foreach (var r in refs) o.Add($"ref {r.Key} sha256={r.Value}");
            o.Add($"result sha256={Codec.Sha256Hex(mod)} bytes={mod.Length}");
            o.Add("bom " + (m.Bom ? "yes" : "no"));
            o.Add("eol " + EolSpec(m.Terms));
            o.Add("");
            o.AddRange(hunks);
            return string.Join("\n", o) + "\n";
        }

        sealed class Hunk
        {
            public int Start, Del, Add;
            public string Hash;
            public List<string> Body = new List<string>();
        }

        static readonly Regex HunkRe = new Regex(@"^@@ (\d+) -(\d+)(?::([0-9a-f]{8}))? \+(\d+) @@", RegexOptions.CultureInvariant);
        static readonly Regex ResultRe = new Regex(@"^sha256=([0-9a-f]{64}) bytes=(\d+)", RegexOptions.CultureInvariant);

        /// <summary>Game files a patch reads: (path, expected sha256).</summary>
        public static List<KeyValuePair<string, string>> Inputs(string text)
        {
            var o = new List<KeyValuePair<string, string>>();
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("@@ ")) break;
                if (line.StartsWith("base ") || line.StartsWith("ref "))
                {
                    var val = line.Substring(line.IndexOf(' ') + 1);
                    int at = val.LastIndexOf(" sha256=", StringComparison.Ordinal);
                    o.Add(new KeyValuePair<string, string>(val.Substring(0, at), val.Substring(at + 8)));
                }
            }
            return o;
        }

        public static byte[] Apply(string text, Func<string, byte[]> getFile, string label)
        {
            string baseName = null, baseSha = null, result = null, eol = "crlf";
            long bytes = -1;
            bool bom = false;
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            var hunks = new List<Hunk>();
            foreach (var line in text.Split('\n'))
            {
                if (hunks.Count == 0)
                {
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sp = line.IndexOf(' ');
                    var key = sp < 0 ? line : line.Substring(0, sp);
                    var val = sp < 0 ? "" : line.Substring(sp + 1);
                    if (key == "base" || key == "ref")
                    {
                        int at = val.LastIndexOf(" sha256=", StringComparison.Ordinal);
                        if (at < 0) throw new ModException($"{label}: bad '{key}' line");
                        if (key == "base") { baseName = val.Substring(0, at); baseSha = val.Substring(at + 8); }
                        else refs[val.Substring(0, at)] = val.Substring(at + 8);
                        continue;
                    }
                    if (key == "result")
                    {
                        var rm = ResultRe.Match(val);
                        if (!rm.Success) throw new ModException($"{label}: bad 'result' line");
                        result = rm.Groups[1].Value;
                        bytes = long.Parse(rm.Groups[2].Value);
                        continue;
                    }
                    if (key == "bom") { bom = val.Trim() == "yes"; continue; }
                    if (key == "eol") { eol = val.Trim(); continue; }
                }
                if (line.StartsWith("@@ "))
                {
                    var hm = HunkRe.Match(line);
                    if (!hm.Success) throw new ModException($"{label}: bad hunk header: {line}");
                    hunks.Add(new Hunk
                    {
                        Start = int.Parse(hm.Groups[1].Value),
                        Del = int.Parse(hm.Groups[2].Value),
                        Hash = hm.Groups[3].Success ? hm.Groups[3].Value : null,
                        Add = int.Parse(hm.Groups[4].Value),
                    });
                }
                else if (hunks.Count > 0 && line.Length > 0)
                    hunks[hunks.Count - 1].Body.Add(line);
            }

            var cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> BodiesOf(string name, string expect)
            {
                if (cache.TryGetValue(name, out var c)) return c;
                var data = getFile(name) ?? throw new ModException($"{label}: needs the game file {name}, which is not in your Data.rwd");
                if (expect != null && Codec.Sha256Hex(data) != expect)
                    throw new ModException($"{label}: your copy of {name} differs from the version this mod was made for");
                return cache[name] = Split(data).Bodies;
            }

            var bas = baseName != null ? BodiesOf(baseName, baseSha) : new List<string>();
            var o = new List<string>();
            int pos = 0;
            foreach (var h in hunks)
            {
                int s = h.Start - 1;
                if (s < pos || s + h.Del > bas.Count) throw new ModException($"{label}: change at line {h.Start} is out of range");
                o.AddRange(bas.GetRange(pos, s - pos));
                if (h.Hash != null && Sha(string.Join("\n", bas.GetRange(s, h.Del))).Substring(0, 8) != h.Hash)
                    throw new ModException($"{label}: the game text at line {h.Start} is not what the mod expects");
                int produced = 0;
                foreach (var item in h.Body)
                {
                    if (item.StartsWith("="))
                    {
                        var spec = item.Substring(1).Trim();
                        int sp = spec.LastIndexOf(' ');
                        var name = spec.Substring(0, sp);
                        var rng = spec.Substring(sp + 1).Split('-');
                        int a = int.Parse(rng[0]), b = int.Parse(rng[1]);
                        var src = baseName != null && name == baseName ? bas : BodiesOf(name, refs.TryGetValue(name, out var rs) ? rs : null);
                        if (a < 1 || b > src.Count || b < a - 1) throw new ModException($"{label}: reference {name} {a}-{b} is out of range");
                        o.AddRange(src.GetRange(a - 1, b - a + 1));
                        produced += b - a + 1;
                    }
                    else if (item.StartsWith("+"))
                    {
                        o.Add(Codec.Latin1.GetString(Encoding.UTF8.GetBytes(item.Substring(1))));
                        produced++;
                    }
                    else throw new ModException($"{label}: bad line '{(item.Length > 20 ? item.Substring(0, 20) : item)}'");
                }
                if (produced != h.Add) throw new ModException($"{label}: change at line {h.Start} produced {produced} lines, expected {h.Add}");
                pos = s + h.Del;
            }
            o.AddRange(bas.GetRange(pos, bas.Count - pos));
            var terms = EolExpand(eol, o.Count);
            if (terms.Count != o.Count) throw new ModException($"{label}: line-ending list covers {terms.Count} lines, result has {o.Count}");
            var sb = new StringBuilder();
            if (bom) sb.Append("ï»¿");
            for (int i = 0; i < o.Count; i++) sb.Append(o[i]).Append(terms[i]);
            var outBytes = Codec.Latin1.GetBytes(sb.ToString());
            if (result != null && (Codec.Sha256Hex(outBytes) != result || (bytes >= 0 && outBytes.Length != bytes)))
                throw new ModException($"{label}: the rebuilt file does not match what the mod author packed");
            return outBytes;
        }

        public static bool LooksLikePatch(byte[] data) =>
            data.Length > 0 && Codec.Latin1.GetString(data, 0, Math.Min(data.Length, 4096)).Split('\n')
                .Any(l => l.StartsWith("result sha256="));
    }

    /// <summary>Port of Python difflib.SequenceMatcher (no junk, autojunk off): same opcodes.</summary>
    public static class SequenceMatcher
    {
        public static List<(char tag, int i1, int i2, int j1, int j2)> Opcodes(List<string> a, List<string> b)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(string s)
            {
                if (!ids.TryGetValue(s, out var v)) ids[s] = v = ids.Count;
                return v;
            }
            var A = a.Select(Id).ToArray();
            var B = b.Select(Id).ToArray();
            var b2j = new Dictionary<int, List<int>>();
            for (int j = 0; j < B.Length; j++)
            {
                if (!b2j.TryGetValue(B[j], out var l)) b2j[B[j]] = l = new List<int>();
                l.Add(j);
            }
            var prev = new int[B.Length + 1];
            var cur = new int[B.Length + 1];
            var prevT = new List<int>();
            var curT = new List<int>();
            var empty = new List<int>();

            (int, int, int) Longest(int alo, int ahi, int blo, int bhi)
            {
                int besti = alo, bestj = blo, bestsize = 0;
                for (int i = alo; i < ahi; i++)
                {
                    foreach (int j in b2j.TryGetValue(A[i], out var js) ? js : empty)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        int k = prev[j] + 1;
                        cur[j + 1] = k;
                        curT.Add(j + 1);
                        if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
                    }
                    foreach (var t in prevT) prev[t] = 0;
                    prevT.Clear();
                    var tmp = prev; prev = cur; cur = tmp;
                    var tl = prevT; prevT = curT; curT = tl;
                }
                foreach (var t in prevT) prev[t] = 0;
                prevT.Clear();
                while (besti > alo && bestj > blo && A[besti - 1] == B[bestj - 1]) { besti--; bestj--; bestsize++; }
                while (besti + bestsize < ahi && bestj + bestsize < bhi && A[besti + bestsize] == B[bestj + bestsize]) bestsize++;
                return (besti, bestj, bestsize);
            }

            var queue = new List<(int, int, int, int)> { (0, A.Length, 0, B.Length) };
            var blocks = new List<(int i, int j, int k)>();
            while (queue.Count > 0)
            {
                var (alo, ahi, blo, bhi) = queue[queue.Count - 1];
                queue.RemoveAt(queue.Count - 1);
                var (i, j, k) = Longest(alo, ahi, blo, bhi);
                if (k == 0) continue;
                blocks.Add((i, j, k));
                if (alo < i && blo < j) queue.Add((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi) queue.Add((i + k, ahi, j + k, bhi));
            }
            blocks.Sort();
            var merged = new List<(int i, int j, int k)>();
            int i1 = 0, j1 = 0, k1 = 0;
            foreach (var (i2, j2, k2) in blocks)
            {
                if (i1 + k1 == i2 && j1 + k1 == j2) k1 += k2;
                else
                {
                    if (k1 > 0) merged.Add((i1, j1, k1));
                    i1 = i2; j1 = j2; k1 = k2;
                }
            }
            if (k1 > 0) merged.Add((i1, j1, k1));
            merged.Add((A.Length, B.Length, 0));

            var ops = new List<(char, int, int, int, int)>();
            int ia = 0, jb = 0;
            foreach (var (ai, bj, size) in merged)
            {
                char tag = ia < ai && jb < bj ? 'r' : ia < ai ? 'd' : jb < bj ? 'i' : '\0';
                if (tag != '\0') ops.Add((tag, ia, ai, jb, bj));
                ia = ai + size;
                jb = bj + size;
                if (size > 0) ops.Add(('e', ai, ia, bj, jb));
            }
            return ops;
        }
    }
}
