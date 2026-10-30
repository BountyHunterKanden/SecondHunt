using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MphRecomp.Import
{
    // JSON written the way Python's json.dump(obj, f, indent=1) writes it (the reference converters' output): every
    // element on its own line, one space per level, ", " -> ",\n", ": " between key and value, floats as Python's repr
    // (shortest round trip, "1.0", "1e-05"), ints as ints, non-ASCII escaped. Objects are ordered key/value lists.
    public static class PyJson
    {
        // an ordered JSON object
        public sealed class Obj : IEnumerable<KeyValuePair<string, object?>>
        {
            readonly List<KeyValuePair<string, object?>> _items = new();
            public void Add(string key, object? value) => _items.Add(new(key, value));
            public int Count => _items.Count;
            public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
        }

        public static string Dump(object? value, int indent = 1, string newline = "\n")
        {
            var sb = new StringBuilder();
            Write(sb, value, 0, indent, newline);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object? v, int level, int indent, string nl)
        {
            switch (v)
            {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Str(sb, s); break;
            case double d: sb.Append(Float(d)); break;
            case float f: sb.Append(Float(f)); break;
            case int or long or uint or short or ushort or byte or sbyte:
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); break;
            case Obj o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                {
                    bool first = true;
                    foreach (KeyValuePair<string, object?> kv in o)
                    {
                        sb.Append(first ? "" : ",").Append(nl).Append(' ', (level + 1) * indent);
                        first = false;
                        Str(sb, kv.Key);
                        sb.Append(": ");
                        Write(sb, kv.Value, level + 1, indent, nl);
                    }
                }
                sb.Append(nl).Append(' ', level * indent).Append('}');
                break;
            case IEnumerable e:
                {
                    bool first = true;
                    foreach (object? x in e)
                    {
                        sb.Append(first ? "[" : ",").Append(nl).Append(' ', (level + 1) * indent);
                        first = false;
                        Write(sb, x, level + 1, indent, nl);
                    }
                    if (first) sb.Append("[]");
                    else sb.Append(nl).Append(' ', level * indent).Append(']');
                }
                break;
            default: throw new ArgumentException($"PyJson: cannot write {v.GetType()}");
            }
        }

        static void Str(StringBuilder sb, string s)
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
                    if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
                }
            }
            sb.Append('"');
        }

        // Python's float repr: the shortest digits that round-trip, fixed notation when the decimal exponent is in
        // -4 < decpt <= 16 (with ".0" for whole numbers), else d.ddde+XX
        public static string Float(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            bool neg = r[0] == '-';
            if (neg) r = r[1..];
            int exp = 0;
            int ei = r.IndexOfAny(new[] { 'E', 'e' });
            if (ei >= 0)
            {
                exp = Int32.Parse(r[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r[..ei];
            }
            int dot = r.IndexOf('.');
            string ip = dot >= 0 ? r[..dot] : r, fp = dot >= 0 ? r[(dot + 1)..] : "";
            string digits = ip + fp;
            int decpt = ip.Length + exp;   // value = 0.digits x 10^decpt
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
            digits = digits[lead..]; decpt -= lead;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            if (decpt <= -4 || decpt > 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                int e = decpt - 1;
                sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString("00", CultureInfo.InvariantCulture));
            }
            else if (decpt <= 0)
            {
                sb.Append("0.").Append('0', -decpt).Append(digits);
            }
            else if (decpt >= digits.Length)
            {
                sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
            }
            return sb.ToString();
        }
    }
}
