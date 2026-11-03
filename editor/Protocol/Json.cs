// 의존성 없는 최소 JSON DOM. Host(.NET 8)와 Unity(Mono, netstandard2.1) 양쪽에서 같은 코드를 쓴다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VrcProto.Protocol
{
    public enum JKind { Null, Bool, Number, String, Array, Object }

    public sealed class JNode
    {
        public JKind Kind { get; private set; }
        bool _b; double _n; string? _s;
        List<JNode>? _arr;
        List<KeyValuePair<string, JNode>>? _obj; // 키 순서 보존

        public static readonly JNode Null = new JNode { Kind = JKind.Null };
        JNode() { }

        public static JNode Obj() => new JNode { Kind = JKind.Object, _obj = new List<KeyValuePair<string, JNode>>() };
        public static JNode Arr() => new JNode { Kind = JKind.Array, _arr = new List<JNode>() };
        public static JNode From(bool v) => new JNode { Kind = JKind.Bool, _b = v };
        public static JNode From(double v) => new JNode { Kind = JKind.Number, _n = v };
        public static JNode From(string? v) => v == null ? Null : new JNode { Kind = JKind.String, _s = v };

        public static implicit operator JNode(bool v) => From(v);
        public static implicit operator JNode(double v) => From(v);
        public static implicit operator JNode(int v) => From((double)v);
        public static implicit operator JNode(long v) => From((double)v);
        // float는 최단 표기로 넓혀 0.1f가 0.10000000149011612로 저장되지 않게 한다
        public static implicit operator JNode(float v) => From(v);
        public static JNode From(float v) => From(float.IsNaN(v) || float.IsInfinity(v) ? 0d : double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        public static implicit operator JNode(string? v) => From(v);

        // ---- 읽기 ----
        public bool IsNull => Kind == JKind.Null;
        public bool AsBool => Kind == JKind.Bool ? _b : Kind == JKind.Number && _n != 0;
        public double AsDouble => Kind == JKind.Number ? _n : Kind == JKind.Bool ? (_b ? 1 : 0) : Kind == JKind.String && double.TryParse(_s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        public float AsFloat => (float)AsDouble;
        public int AsInt => (int)AsDouble;
        // fileID는 64비트 전 범위를 쓰므로 문자열로 주고받는다.
        public long AsLong => Kind == JKind.String && long.TryParse(_s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (long)AsDouble;
        public string AsString => Kind == JKind.String ? _s! : Kind == JKind.Null ? "" : ToJson();
        public string? AsStringOrNull => Kind == JKind.String ? _s : null;
        public int Count => _arr?.Count ?? _obj?.Count ?? 0;

        public JNode this[int i] => _arr != null && i >= 0 && i < _arr.Count ? _arr[i] : Null;
        public JNode this[string key]
        {
            get
            {
                if (_obj == null) return Null;
                foreach (var kv in _obj) if (kv.Key == key) return kv.Value;
                return Null;
            }
            set => Set(key, value);
        }
        public bool Has(string key) { if (_obj == null) return false; foreach (var kv in _obj) if (kv.Key == key) return true; return false; }
        public IEnumerable<JNode> Items => (IEnumerable<JNode>?)_arr ?? Array.Empty<JNode>();
        public IEnumerable<KeyValuePair<string, JNode>> Props => (IEnumerable<KeyValuePair<string, JNode>>?)_obj ?? Array.Empty<KeyValuePair<string, JNode>>();

        // ---- 쓰기 ----
        public JNode Set(string key, JNode? value)
        {
            if (_obj == null) throw new InvalidOperationException("not an object");
            value ??= Null;
            for (int i = 0; i < _obj.Count; i++)
                if (_obj[i].Key == key) { _obj[i] = new KeyValuePair<string, JNode>(key, value); return this; }
            _obj.Add(new KeyValuePair<string, JNode>(key, value));
            return this;
        }
        public bool Remove(string key)
        {
            if (_obj == null) return false;
            for (int i = 0; i < _obj.Count; i++) if (_obj[i].Key == key) { _obj.RemoveAt(i); return true; }
            return false;
        }
        public JNode Add(JNode? value)
        {
            if (_arr == null) throw new InvalidOperationException("not an array");
            _arr.Add(value ?? Null);
            return this;
        }

        public JNode Clone() => Parse(ToJson());

        public static JNode FloatArray(IEnumerable<float> xs) { var a = Arr(); foreach (var x in xs) a.Add(x); return a; }
        public float[] ToFloatArray() { var r = new float[Count]; for (int i = 0; i < r.Length; i++) r[i] = this[i].AsFloat; return r; }

        // ---- 직렬화 ----
        public string ToJson(bool indent = false) { var sb = new StringBuilder(); Write(sb, indent, 0); return sb.ToString(); }
        public override string ToString() => ToJson();

        void Write(StringBuilder sb, bool indent, int depth)
        {
            switch (Kind)
            {
                case JKind.Null: sb.Append("null"); break;
                case JKind.Bool: sb.Append(_b ? "true" : "false"); break;
                case JKind.Number:
                    if (double.IsNaN(_n) || double.IsInfinity(_n)) sb.Append("0");
                    else if (_n == Math.Floor(_n) && Math.Abs(_n) < 1e15) sb.Append(((long)_n).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(_n.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JKind.String: WriteString(sb, _s!); break;
                case JKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _arr!.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indent) NewLine(sb, depth + 1);
                        _arr[i].Write(sb, indent, depth + 1);
                    }
                    if (indent && _arr.Count > 0) NewLine(sb, depth);
                    sb.Append(']');
                    break;
                case JKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _obj!.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indent) NewLine(sb, depth + 1);
                        WriteString(sb, _obj[i].Key);
                        sb.Append(indent ? ": " : ":");
                        _obj[i].Value.Write(sb, indent, depth + 1);
                    }
                    if (indent && _obj.Count > 0) NewLine(sb, depth);
                    sb.Append('}');
                    break;
            }
        }
        static void NewLine(StringBuilder sb, int depth) { sb.Append('\n'); sb.Append(' ', depth * 2); }

        static void WriteString(StringBuilder sb, string s)
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
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- 파싱 ----
        public static JNode Parse(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i);
            return v;
        }

        static void SkipWs(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        static JNode ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end");
            char c = t[i];
            if (c == '{')
            {
                i++; var o = Obj(); SkipWs(t, ref i);
                if (t[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs(t, ref i);
                    var k = ParseString(t, ref i);
                    SkipWs(t, ref i);
                    if (t[i] != ':') throw new FormatException("':' expected at " + i);
                    i++;
                    o._obj!.Add(new KeyValuePair<string, JNode>(k, ParseValue(t, ref i)));
                    SkipWs(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == '}') { i++; return o; }
                    throw new FormatException("',' or '}' expected at " + i);
                }
            }
            if (c == '[')
            {
                i++; var a = Arr(); SkipWs(t, ref i);
                if (t[i] == ']') { i++; return a; }
                while (true)
                {
                    a._arr!.Add(ParseValue(t, ref i));
                    SkipWs(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == ']') { i++; return a; }
                    throw new FormatException("',' or ']' expected at " + i);
                }
            }
            if (c == '"') return From(ParseString(t, ref i));
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return From(true); }
            if (t.Length - i >= 5 && string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return From(false); }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return Null; }
            int s = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (s == i) throw new FormatException("unexpected '" + c + "' at " + i);
            return From(double.Parse(t.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        static string ParseString(string t, ref int i)
        {
            if (t[i] != '"') throw new FormatException("string expected at " + i);
            i++;
            // 대부분의 문자열은 이스케이프가 없다: 그대로 잘라 낸다
            int start = i;
            while (i < t.Length && t[i] != '"' && t[i] != '\\') i++;
            if (i >= t.Length) throw new FormatException("unterminated string");
            if (t[i] == '"') return t.Substring(start, i++ - start);
            var sb = new StringBuilder(t, start, i - start, i - start + 16);
            while (true)
            {
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = t[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(t.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }
    }
}
