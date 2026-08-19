using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Pancing.Sim
{
    /// <summary>
    /// A small, dependency-free JSON reader and writer.
    ///
    /// Why not JsonUtility: it lives in UnityEngine, and Pancing.Sim deliberately
    /// has no engine reference (see the asmdef). Why not a package: the parity
    /// harness compiles these same files outside Unity with nothing but the base
    /// class library, so every dependency added here is a dependency added there.
    ///
    /// Why not JsonUtility even if we could: it cannot represent the shapes this
    /// game's data actually uses — `times: { dawn: 1.3, day: 1.0 }` is a map with
    /// arbitrary keys, and `lures` is either a map or null. JsonUtility needs a
    /// concrete field per key and would force the data into a shape that exists
    /// only to satisfy the serialiser.
    ///
    /// Numbers are parsed as double with InvariantCulture — a Malaysian Windows
    /// locale uses a comma decimal separator, and picking that up would quietly
    /// turn 0.62 into 62.
    /// </summary>
    public sealed class Json
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }

        public Kind Type { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<Json> _array;

        // Objects keep DOCUMENT ORDER, not hash order.
        //
        // This is load-bearing, not tidiness. JavaScript's Object.entries() yields
        // keys in insertion order, and the catch table feeds exactly that order
        // into a weighted draw: the roll walks the entries subtracting weights
        // until it goes negative. Reorder the entries and the same random number
        // picks a different fish. A Dictionary's enumeration order is explicitly
        // unspecified, so relying on it would give a game that is reproducible
        // within one runtime and silently different across two.
        private List<KeyValuePair<string, Json>> _fields;
        private Dictionary<string, int> _index;

        private Json(Kind kind) { Type = kind; }

        public static readonly Json Null = new Json(Kind.Null);

        /* --- accessors ------------------------------------------------------ */
        //
        // Every accessor has a default, and a missing key returns Null rather than
        // throwing. Content data should degrade — a species missing `jumpChance`
        // should be a fish that never jumps, not a crash on the loading screen.

        public bool IsNull => Type == Kind.Null;
        public int Count => Type == Kind.Array ? _array.Count : (Type == Kind.Object ? _fields.Count : 0);

        public Json this[int i] =>
            Type == Kind.Array && i >= 0 && i < _array.Count ? _array[i] : Null;

        public Json this[string key] =>
            Type == Kind.Object && key != null && _index.TryGetValue(key, out int i) ? _fields[i].Value : Null;

        public bool Has(string key) => Type == Kind.Object && key != null && _index.ContainsKey(key);

        public IEnumerable<Json> Items => Type == Kind.Array ? _array : System.Linq.Enumerable.Empty<Json>();

        /// <summary>Object members, in document order. See the note on _fields.</summary>
        public IEnumerable<KeyValuePair<string, Json>> Fields =>
            Type == Kind.Object ? _fields : System.Linq.Enumerable.Empty<KeyValuePair<string, Json>>();

        public double AsDouble(double fallback = 0)
        {
            if (Type == Kind.Number) return _number;
            // The exporter encodes Infinity as "__Inf__" because JSON has no
            // literal for it; unlimited bait stock relies on this round-tripping.
            if (Type == Kind.String && _string == "__Inf__") return double.PositiveInfinity;
            if (Type == Kind.Bool) return _bool ? 1 : 0;
            return fallback;
        }

        public float AsFloat(float fallback = 0) => (float)AsDouble(fallback);

        public int AsInt(int fallback = 0)
        {
            double d = AsDouble(fallback);
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            return (int)Math.Round(d);
        }

        public bool AsBool(bool fallback = false) =>
            Type == Kind.Bool ? _bool : (Type == Kind.Number ? _number != 0 : fallback);

        public string AsString(string fallback = null) =>
            Type == Kind.String ? _string
            : Type == Kind.Number ? _number.ToString(CultureInfo.InvariantCulture)
            : Type == Kind.Bool ? (_bool ? "true" : "false")
            : fallback;

        /// <summary>Array of doubles, e.g. a depth band [0.10, 0.55].</summary>
        public double[] AsDoubleArray()
        {
            if (Type != Kind.Array) return Array.Empty<double>();
            var outv = new double[_array.Count];
            for (int i = 0; i < _array.Count; i++) outv[i] = _array[i].AsDouble();
            return outv;
        }

        public string[] AsStringArray()
        {
            if (Type != Kind.Array) return Array.Empty<string>();
            var outv = new string[_array.Count];
            for (int i = 0; i < _array.Count; i++) outv[i] = _array[i].AsString();
            return outv;
        }

        /// <summary>
        /// Flatten an object of numbers for lookup, e.g. a species' `times`.
        /// Use AsNumberList where the ORDER matters — see the note on _fields.
        /// </summary>
        public Dictionary<string, double> AsNumberMap()
        {
            var map = new Dictionary<string, double>();
            if (Type != Kind.Object) return map;
            foreach (var kv in _fields) map[kv.Key] = kv.Value.AsDouble();
            return map;
        }

        /// <summary>
        /// The same thing in document order, for anything that feeds a weighted
        /// draw — a spot's `pool`, above all.
        /// </summary>
        public List<KeyValuePair<string, double>> AsNumberList()
        {
            var list = new List<KeyValuePair<string, double>>();
            if (Type != Kind.Object) return list;
            foreach (var kv in _fields)
                list.Add(new KeyValuePair<string, double>(kv.Key, kv.Value.AsDouble()));
            return list;
        }

        /* --- construction --------------------------------------------------- */

        public static Json Of(bool b) { var j = new Json(Kind.Bool); j._bool = b; return j; }
        public static Json Of(double d) { var j = new Json(Kind.Number); j._number = d; return j; }
        public static Json Of(string s) { if (s == null) return Null; var j = new Json(Kind.String); j._string = s; return j; }
        public static Json Array_() { var j = new Json(Kind.Array); j._array = new List<Json>(); return j; }
        public static Json Object_()
        {
            var j = new Json(Kind.Object);
            j._fields = new List<KeyValuePair<string, Json>>();
            j._index = new Dictionary<string, int>();
            return j;
        }

        public Json Add(Json v) { _array.Add(v ?? Null); return this; }
        public Json Set(string k, Json v)
        {
            if (_index.TryGetValue(k, out int at)) _fields[at] = new KeyValuePair<string, Json>(k, v ?? Null);
            else { _index[k] = _fields.Count; _fields.Add(new KeyValuePair<string, Json>(k, v ?? Null)); }
            return this;
        }
        public Json Set(string k, double v) => Set(k, Of(v));
        public Json Set(string k, string v) => Set(k, Of(v));
        public Json Set(string k, bool v) => Set(k, Of(v));

        /* --- parsing -------------------------------------------------------- */

        public static Json Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("JSON: empty input");
            int i = 0;
            var v = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i < text.Length) throw Error(text, i, "trailing content after top-level value");
            return v;
        }

        /// <summary>Parse, or return Null and the message instead of throwing.</summary>
        public static bool TryParse(string text, out Json value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (Exception e) { value = Null; error = e.Message; return false; }
        }

        private static FormatException Error(string s, int i, string what)
        {
            // Report a line and column, not a character offset: a 28 KB species
            // table is unusable to debug by offset.
            int line = 1, col = 1;
            for (int k = 0; k < i && k < s.Length; k++)
            {
                if (s[k] == '\n') { line++; col = 1; } else col++;
            }
            return new FormatException($"JSON: {what} at line {line}, column {col}");
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static Json ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end of input");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return Of(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true"); return Of(true);
                case 'f':
                    Expect(s, ref i, "false"); return Of(false);
                case 'n':
                    Expect(s, ref i, "null"); return Null;
                default: return Of(ParseNumber(s, ref i));
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw Error(s, i, $"expected '{word}'");
            i += word.Length;
        }

        private static Json ParseObject(string s, ref int i)
        {
            var obj = Object_();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Error(s, i, "expected a string key");
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Error(s, i, "expected ':'");
                i++;
                obj.Set(key, ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return obj; }
                throw Error(s, i, "expected ',' or '}'");
            }
        }

        private static Json ParseArray(string s, ref int i)
        {
            var arr = Array_();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }

            while (true)
            {
                arr._array.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return arr; }
                throw Error(s, i, "expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Error(s, i, "unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw Error(s, i, "unterminated escape");
                char e = s[i++];
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
                        if (i + 4 > s.Length) throw Error(s, i, "truncated \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw Error(s, i, $"unknown escape '\\{e}'");
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                   || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')))) i++;

            string slice = s.Substring(start, i - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw Error(s, start, $"bad number '{slice}'");
            return d;
        }

        /* --- writing -------------------------------------------------------- */

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Type)
            {
                case Kind.Null: sb.Append("null"); break;
                case Kind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case Kind.Number:
                    if (double.IsPositiveInfinity(_number)) sb.Append("\"__Inf__\"");
                    else if (double.IsNaN(_number) || double.IsInfinity(_number)) sb.Append("null");
                    // "R" round-trips exactly, which matters for a save file: a
                    // record length that drifts on every save is a bug report.
                    else sb.Append(_number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case Kind.String: WriteString(sb, _string); break;
                case Kind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case Kind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in _fields)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        kv.Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
