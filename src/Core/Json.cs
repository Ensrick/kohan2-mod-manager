using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace K2Mods
{
    /// <summary>Small JSON reader/writer: objects, arrays, strings, numbers, booleans, null.</summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.Ws();
            var v = p.Value();
            p.Ws();
            if (p.Pos != text.Length) throw p.Error("unexpected text after the value");
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text) =>
            Parse(text) as Dictionary<string, object> ?? throw new FormatException("JSON: expected an object");

        public static string Str(Dictionary<string, object> d, string key, string fallback = null) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;

        public static long Long(Dictionary<string, object> d, string key, long fallback = 0) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : fallback;

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback = false) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        public static List<string> StrList(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return new List<string>();
            if (v is string s) return s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return ((IEnumerable)v).Cast<object>().Where(x => x != null)
                .Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
        }

        public static List<Dictionary<string, object>> ObjList(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || !(v is IEnumerable e) || v is string)
                return new List<Dictionary<string, object>>();
            return e.Cast<object>().OfType<Dictionary<string, object>>().ToList();
        }

        /// <summary>
        /// pretty: two-space indent, arrays of plain values on one line.
        /// compact: Python json.dumps separators (", " and ": "), non-ASCII escaped.
        /// </summary>
        public static string Write(object value, bool pretty = true, bool sortKeys = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, sortKeys, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, bool pretty, bool sort, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: WriteString(sb, s); return;
                case int _: case long _: case uint _: case ulong _: case short _: case ushort _: case byte _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case double dd: sb.Append(dd.ToString("R", CultureInfo.InvariantCulture)); return;
                case float ff: sb.Append(((double)ff).ToString("R", CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> d:
                {
                    var keys = d.Keys.ToList();
                    if (sort) keys.Sort(StringComparer.Ordinal);
                    if (keys.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (i > 0) sb.Append(pretty ? "," : ", ");
                        if (pretty) Indent(sb, depth + 1);
                        WriteString(sb, keys[i]);
                        sb.Append(": ");
                        WriteValue(sb, d[keys[i]], pretty, sort, depth + 1);
                    }
                    if (pretty) Indent(sb, depth);
                    sb.Append('}');
                    return;
                }
                case IEnumerable e:
                {
                    var items = e.Cast<object>().ToList();
                    bool nested = pretty && items.Any(x => x is IDictionary<string, object> || (x is IEnumerable && !(x is string)));
                    sb.Append('[');
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) sb.Append(nested ? "," : ", ");
                        if (nested) Indent(sb, depth + 1);
                        WriteValue(sb, items[i], pretty, sort, depth + 1);
                    }
                    if (nested) Indent(sb, depth);
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            public int Pos;

            public Parser(string s) { this.s = s; }

            public FormatException Error(string msg) => new FormatException($"JSON: {msg} (character {Pos})");

            char Peek() => Pos < s.Length ? s[Pos] : '\0';

            public void Ws()
            {
                while (Pos < s.Length && (s[Pos] == ' ' || s[Pos] == '\t' || s[Pos] == '\r' || s[Pos] == '\n' || s[Pos] == '﻿')) Pos++;
            }

            public object Value()
            {
                switch (Peek())
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Lit("true"); return true;
                    case 'f': Lit("false"); return false;
                    case 'n': Lit("null"); return null;
                    case '\0': throw Error("unexpected end");
                    default: return Num();
                }
            }

            void Lit(string w)
            {
                if (Pos + w.Length > s.Length || string.CompareOrdinal(s, Pos, w, 0, w.Length) != 0) throw Error("unknown word");
                Pos += w.Length;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Pos++;
                Ws();
                if (Peek() == '}') { Pos++; return d; }
                while (true)
                {
                    Ws();
                    if (Peek() != '"') throw Error("expected a quoted key");
                    var k = Str();
                    Ws();
                    if (Peek() != ':') throw Error("expected ':'");
                    Pos++;
                    Ws();
                    d[k] = Value();
                    Ws();
                    char c = Peek();
                    Pos++;
                    if (c == '}') return d;
                    if (c != ',') throw Error("expected ',' or '}'");
                }
            }

            List<object> Arr()
            {
                var l = new List<object>();
                Pos++;
                Ws();
                if (Peek() == ']') { Pos++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    char c = Peek();
                    Pos++;
                    if (c == ']') return l;
                    if (c != ',') throw Error("expected ',' or ']'");
                }
            }

            string Str()
            {
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= s.Length) throw Error("unterminated string");
                    char c = s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= s.Length) throw Error("unterminated string");
                    char e = s[Pos++];
                    switch (e)
                    {
                        case '"': case '\\': case '/': sb.Append(e); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > s.Length) throw Error("bad \\u escape");
                            sb.Append((char)Convert.ToInt32(s.Substring(Pos, 4), 16));
                            Pos += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
            }

            object Num()
            {
                int st = Pos;
                while (Pos < s.Length && "+-0123456789.eE".IndexOf(s[Pos]) >= 0) Pos++;
                var t = s.Substring(st, Pos - st);
                if (t.Length == 0) throw Error("unexpected character");
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
