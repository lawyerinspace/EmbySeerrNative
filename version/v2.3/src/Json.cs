using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EmbySeerrNative
{
    /// <summary>
    /// Minimal, dependency-free JSON reader/writer.
    ///
    /// Emby Server 4.10.2.0 does not ship Newtonsoft.Json, so the plugin cannot rely on it at
    /// runtime. Seerr's responses are parsed into plain dictionaries/lists so that unexpected or
    /// missing fields degrade gracefully instead of throwing deserialisation exceptions.
    /// Objects  -> Dictionary&lt;string, object&gt; (case-sensitive keys)
    /// Arrays   -> List&lt;object&gt;
    /// Numbers  -> double
    /// Strings  -> string, true/false -> bool, null -> null
    /// </summary>
    internal static class Json
    {
        private const int MaxDepth = 64;

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("Empty JSON document.");
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("Unexpected trailing characters in JSON.");
            return value;
        }

        // ----- Typed accessors (all null-safe) -----

        public static Dictionary<string, object> Obj(object node, string key)
            => Get(node, key) as Dictionary<string, object>;

        public static List<object> Arr(object node, string key)
            => Get(node, key) as List<object>;

        public static object Get(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            if (d == null || key == null) return null;
            return d.TryGetValue(key, out var v) ? v : null;
        }

        public static string Str(object node, string key)
        {
            var v = Get(node, key);
            if (v == null) return null;
            if (v is string s) return s;
            if (v is double dbl) return dbl.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return null;
        }

        public static int? Int(object node, string key)
        {
            var v = Get(node, key);
            if (v is double d && !double.IsNaN(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            if (v is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            return null;
        }

        /// <summary>A bare JSON number as int (0 when it is not a number).</summary>
        public static int ToInt(object v) => v is double d && !double.IsNaN(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : 0;

        public static double? Num(object node, string key)
        {
            var v = Get(node, key);
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
            return null;
        }

        // ----- Writer (used only for request bodies we construct ourselves) -----

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("JSON nesting too deep.");
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                default:
                    throw new InvalidOperationException("Unsupported JSON value type " + value.GetType().Name);
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }

            public bool End => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    var c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == (char)0xFEFF) _i++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("JSON nesting too deep.");
                if (End) throw new FormatException("Unexpected end of JSON.");
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException("Unexpected character in JSON at position " + _i + ".");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw new FormatException("Invalid JSON literal at position " + _i + ".");
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWs();
                if (!End && _s[_i] == '}') { _i++; return result; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw new FormatException("Expected property name in JSON.");
                    var key = ReadString();
                    SkipWs();
                    if (End || _s[_i] != ':') throw new FormatException("Expected ':' in JSON.");
                    _i++;
                    SkipWs();
                    result[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (End) throw new FormatException("Unterminated JSON object.");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return result; }
                    throw new FormatException("Expected ',' or '}' in JSON.");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var result = new List<object>();
                _i++; // [
                SkipWs();
                if (!End && _s[_i] == ']') { _i++; return result; }
                while (true)
                {
                    SkipWs();
                    result.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (End) throw new FormatException("Unterminated JSON array.");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return result; }
                    throw new FormatException("Expected ',' or ']' in JSON.");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new FormatException("Unterminated JSON string.");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("Unterminated JSON escape.");
                    var e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new FormatException("Invalid JSON unicode escape.");
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw new FormatException("Invalid JSON unicode escape.");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
            }

            private double ReadNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                while (!End)
                {
                    var c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                var token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new FormatException("Invalid JSON number.");
                return d;
            }
        }
    }
}
