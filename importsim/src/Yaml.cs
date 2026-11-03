using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImportSim
{
    public sealed class YamlDoc
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;
        public string Type = "";
        public Map Body = new Map();
    }

    /// <summary>Unity가 쓰는 YAML 부분집합 파서 (블록 매핑/시퀀스, 흐름 {..} [..], 따옴표 문자열, 줄바꿈된 값).</summary>
    public static class Yaml
    {
        static readonly Regex DocHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);
        static readonly Regex KeyRe = new Regex(@"^([^\s'""{\[\-#][^:]*|-[^\s:][^:]*|'[^']*'|""[^""]*""):(?: (.*)|$)", RegexOptions.Compiled);

        public static List<YamlDoc> ParseDocuments(string text)
        {
            var docs = new List<YamlDoc>();
            var lines = text.Replace("\r", "").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var m = DocHeader.Match(lines[i]);
                if (!m.Success) { i++; continue; }
                int start = ++i;
                while (i < lines.Length && !lines[i].StartsWith("--- ", StringComparison.Ordinal)) i++;
                var body = ParseBlock(lines, start, i);
                var doc = new YamlDoc
                {
                    ClassId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    FileId = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Stripped = m.Groups[3].Success,
                };
                foreach (var kv in body) { doc.Type = kv.Key; doc.Body = kv.Value as Map ?? new Map(); break; }
                docs.Add(doc);
            }
            return docs;
        }

        /// <summary>단일 YAML 문서(.meta 등)를 매핑으로.</summary>
        public static Map Parse(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            return ParseBlock(lines, 0, lines.Length);
        }

        // ---- 블록 파서 ----

        struct Line { public int Indent; public string Text; }

        static Map ParseBlock(string[] raw, int from, int to)
        {
            var lines = new List<Line>();
            for (int k = from; k < to; k++)
            {
                var t = raw[k];
                var trimmed = t.TrimStart(' ');
                if (trimmed.Length == 0 || trimmed[0] == '%' || trimmed[0] == '#') continue;
                lines.Add(new Line { Indent = t.Length - trimmed.Length, Text = trimmed.TrimEnd() });
            }
            int i = 0;
            return lines.Count == 0 ? new Map() : ParseMapping(lines, ref i, lines[0].Indent);
        }

        static Map ParseMapping(List<Line> lines, ref int i, int indent)
        {
            var map = new Map();
            while (i < lines.Count && lines[i].Indent == indent && !IsSeqItem(lines[i].Text))
            {
                var km = KeyRe.Match(lines[i].Text);
                if (!km.Success) { i++; continue; }   // 알 수 없는 줄은 건너뜀
                var key = Unquote(km.Groups[1].Value.Trim());
                var rest = km.Groups[2].Success ? km.Groups[2].Value : "";
                i++;
                if (rest.Length > 0)
                    map[key] = ParseInlineValue(lines, ref i, indent, rest);
                else if (i < lines.Count && lines[i].Indent > indent)
                    map[key] = IsSeqItem(lines[i].Text) ? ParseSequence(lines, ref i, lines[i].Indent) : (object?)ParseMapping(lines, ref i, lines[i].Indent);
                else if (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text))
                    map[key] = ParseSequence(lines, ref i, indent);   // Unity: "key:\n- item" (같은 들여쓰기)
                else
                    map[key] = "";
            }
            return map;
        }

        static List<object?> ParseSequence(List<Line> lines, ref int i, int indent)
        {
            var list = new List<object?>();
            while (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text))
            {
                var content = lines[i].Text.Length > 1 ? lines[i].Text.Substring(2) : "";
                if (content.Length > 0 && KeyRe.IsMatch(content) && content[0] != '{' && content[0] != '[')
                {
                    // "- key: v" -> 들여쓰기 indent+2 의 매핑으로 다시 읽는다
                    lines[i] = new Line { Indent = indent + 2, Text = content };
                    list.Add(ParseMapping(lines, ref i, indent + 2));
                }
                else
                {
                    i++;
                    list.Add(content.Length == 0 ? "" : ParseInlineValue(lines, ref i, indent, content));
                }
            }
            return list;
        }

        /// key 줄에 붙은 값. 흐름 컬렉션/따옴표/일반 문자열이 다음 줄로 이어질 수 있다.
        static object? ParseInlineValue(List<Line> lines, ref int i, int indent, string first)
        {
            var sb = new StringBuilder(first);
            char c = first[0];
            if (c == '{' || c == '[')
            {
                while (!Balanced(sb.ToString()) && i < lines.Count) { sb.Append(' ').Append(lines[i].Text); i++; }
                int p = 0;
                return ParseFlow(sb.ToString(), ref p);
            }
            if (c == '\'' || c == '"')
            {
                while (!QuoteClosed(sb.ToString()) && i < lines.Count) { sb.Append(' ').Append(lines[i].Text); i++; }
                return Unquote(sb.ToString());
            }
            // 일반 문자열: 더 깊게 들여쓴 다음 줄은 이어지는 값
            while (i < lines.Count && lines[i].Indent > indent) { sb.Append(' ').Append(lines[i].Text); i++; }
            return sb.ToString();
        }

        static bool IsSeqItem(string t) => t == "-" || t.StartsWith("- ", StringComparison.Ordinal);

        static bool Balanced(string s)
        {
            int depth = 0; char q = '\0';
            for (int k = 0; k < s.Length; k++)
            {
                char ch = s[k];
                if (q != '\0') { if (ch == q) q = '\0'; continue; }
                if (ch == '\'' || ch == '"') q = ch;
                else if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
            }
            return depth <= 0;
        }

        static bool QuoteClosed(string s)
        {
            char q = s[0];
            for (int k = 1; k < s.Length; k++)
            {
                if (s[k] == '\\' && q == '"') { k++; continue; }
                if (s[k] == q)
                {
                    if (q == '\'' && k + 1 < s.Length && s[k + 1] == '\'') { k++; continue; }
                    return true;
                }
            }
            return false;
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return Regex.Unescape(s.Substring(1, s.Length - 2).Replace("\\x", "\\u00"));
            return s;
        }

        // ---- 흐름 파서 ----

        static object? ParseFlow(string s, ref int p)
        {
            Ws(s, ref p);
            if (p >= s.Length) return "";
            if (s[p] == '{')
            {
                p++;
                var m = new Map();
                while (true)
                {
                    Ws(s, ref p);
                    if (p >= s.Length) break;
                    if (s[p] == '}') { p++; break; }
                    var key = Scalar(s, ref p, ":}").Trim();
                    if (p < s.Length && s[p] == ':') p++;
                    m[Unquote(key)] = ParseFlow(s, ref p);
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') p++;
                }
                return m;
            }
            if (s[p] == '[')
            {
                p++;
                var l = new List<object?>();
                while (true)
                {
                    Ws(s, ref p);
                    if (p >= s.Length) break;
                    if (s[p] == ']') { p++; break; }
                    l.Add(ParseFlow(s, ref p));
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') p++;
                }
                return l;
            }
            return Unquote(Scalar(s, ref p, ",}]").Trim());
        }

        static string Scalar(string s, ref int p, string stops)
        {
            int start = p;
            if (p < s.Length && (s[p] == '\'' || s[p] == '"'))
            {
                char q = s[p++];
                while (p < s.Length)
                {
                    if (s[p] == q) { if (q == '\'' && p + 1 < s.Length && s[p + 1] == '\'') { p += 2; continue; } p++; break; }
                    if (s[p] == '\\' && q == '"') p++;
                    p++;
                }
                return s.Substring(start, p - start);
            }
            while (p < s.Length && stops.IndexOf(s[p]) < 0) p++;
            return s.Substring(start, p - start);
        }

        static void Ws(string s, ref int p) { while (p < s.Length && s[p] == ' ') p++; }

        // ---- 유틸 ----

        /// <summary>깊은 복사. guid 없는 참조 {fileID: X}의 X는 remapLocal 로 바꾼다 (0 제외).</summary>
        public static object? DeepCopy(object? v, Func<long, long> remapLocal)
        {
            switch (v)
            {
                case Map m:
                    var c = new Map();
                    bool localRef = m.ContainsKey("fileID") && !m.ContainsKey("guid");
                    foreach (var kv in m)
                    {
                        if (localRef && kv.Key == "fileID")
                        {
                            long id = Ref.FileId(m);
                            c[kv.Key] = (id == 0 ? 0 : remapLocal(id)).ToString(CultureInfo.InvariantCulture);
                        }
                        else c[kv.Key] = DeepCopy(kv.Value, remapLocal);
                    }
                    return c;
                case List<object?> l:
                    var r = new List<object?>(l.Count);
                    foreach (var x in l) r.Add(DeepCopy(x, remapLocal));
                    return r;
                default:
                    return v;
            }
        }
    }
}
