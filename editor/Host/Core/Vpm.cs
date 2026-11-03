// VPM(VRChat Package Manager) 저장소 목록 읽기·버전 고르기 (PLAN.md 3.10). 네트워크·파일은 Upload 쪽이 맡고, 여기는 순수 계산만.
// 저장소 JSON: { id, url, packages: { 패키지 id: { versions: { "1.2.3": { name, version, url(zip), zipSHA256, unity, vpmDependencies, license, displayName } } } } }
// 버전 범위는 VPM이 쓰는 npm식: "1.2.3", ">=1.0.0 <2.0.0", "^1.5", "~1.2", "1.x", "a || b". 미리보기판(-beta 등)은 범위가 직접 가리킬 때만 고른다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public readonly struct SemVer : IComparable<SemVer>
    {
        public readonly int Major, Minor, Patch;
        public readonly string Pre;   // "" = 정식판
        public SemVer(int a, int b, int c, string pre = "") { Major = a; Minor = b; Patch = c; Pre = pre ?? ""; }
        public bool IsPre => Pre.Length > 0;

        public static bool TryParse(string s, out SemVer v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+'); if (plus >= 0) s = s.Substring(0, plus);
            string pre = "";
            int dash = s.IndexOf('-'); if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var p = s.Split('.');
            if (p.Length < 1 || p.Length > 3) return false;
            var n = new int[3];
            for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
            v = new SemVer(n[0], n[1], n[2], pre);
            return true;
        }
        public static SemVer Parse(string s) => TryParse(s, out var v) ? v : throw new FormatException("bad version: " + s);

        public int CompareTo(SemVer o)
        {
            int c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            // 정식판 > 미리보기판 (1.0.0 > 1.0.0-beta)
            if (Pre.Length == 0) return o.Pre.Length == 0 ? 0 : 1;
            if (o.Pre.Length == 0) return -1;
            var a = Pre.Split('.'); var b = o.Pre.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                bool na = int.TryParse(a[i], out var ia), nb = int.TryParse(b[i], out var ib);
                c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
        public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + Pre : "");
    }

    /// <summary>npm식 버전 범위.</summary>
    public sealed class VersionRange
    {
        readonly List<List<(string Op, SemVer V)>> _any = new List<List<(string, SemVer)>>();   // OR 묶음, 묶음 안은 AND
        public readonly string Text;

        VersionRange(string text) { Text = text; }

        public static VersionRange Parse(string? text)
        {
            var r = new VersionRange(text ?? "");
            foreach (var alt in (string.IsNullOrWhiteSpace(text) ? "*" : text!).Split(new[] { "||" }, StringSplitOptions.None))
            {
                var set = new List<(string, SemVer)>();
                var toks = alt.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                // "1.0.0 - 2.0.0"
                if (toks.Count == 3 && toks[1] == "-") { set.Add((">=", Loose(toks[0]).lo)); set.Add(("<=", Loose(toks[2]).lo)); r._any.Add(set); continue; }
                for (int i = 0; i < toks.Count; i++)
                {
                    var t = toks[i];
                    // ">= 1.0" 처럼 띄어 쓴 경우
                    if ((t == ">=" || t == "<=" || t == ">" || t == "<" || t == "=") && i + 1 < toks.Count) t += toks[++i];
                    string op = t.StartsWith(">=") ? ">=" : t.StartsWith("<=") ? "<=" : t.StartsWith(">") ? ">" : t.StartsWith("<") ? "<" : t.StartsWith("=") ? "=" : t.StartsWith("^") ? "^" : t.StartsWith("~") ? "~" : "";
                    var body = t.Substring(op.Length).Trim();
                    if (body == "*" || body == "x" || body.Length == 0) continue;
                    var (lo, parts, pre) = Loose(body);
                    switch (op)
                    {
                        case "^":
                            set.Add((">=", lo));
                            set.Add(("<", lo.Major > 0 || parts == 1 ? new SemVer(lo.Major + 1, 0, 0, "0") : lo.Minor > 0 || parts == 2 ? new SemVer(0, lo.Minor + 1, 0, "0") : new SemVer(0, 0, lo.Patch + 1, "0")));
                            break;
                        case "~":
                            set.Add((">=", lo));
                            set.Add(("<", parts == 1 ? new SemVer(lo.Major + 1, 0, 0, "0") : new SemVer(lo.Major, lo.Minor + 1, 0, "0")));
                            break;
                        case "":
                        case "=":
                            if (parts == 3) set.Add(("=", lo));
                            else { set.Add((">=", lo)); set.Add(("<", parts == 1 ? new SemVer(lo.Major + 1, 0, 0, "0") : new SemVer(lo.Major, lo.Minor + 1, 0, "0"))); }
                            break;
                        case "<" when parts < 3: set.Add(("<", lo)); break;
                        case "<=" when parts < 3: set.Add(("<", parts == 1 ? new SemVer(lo.Major + 1, 0, 0, "0") : new SemVer(lo.Major, lo.Minor + 1, 0, "0"))); break;
                        default: set.Add((op, lo)); break;
                    }
                }
                r._any.Add(set);
            }
            return r;
        }

        /// "1.2" → (1.2.0, 2개), "3.1.x" → (3.1.0, 2개)
        static (SemVer lo, int parts, string pre) Loose(string s)
        {
            string pre = "";
            int dash = s.IndexOf('-'); if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var p = s.Split('.').Where(x => x != "x" && x != "X" && x != "*").ToArray();
            var n = new int[3];
            for (int i = 0; i < p.Length && i < 3; i++) int.TryParse(p[i], out n[i]);
            return (new SemVer(n[0], n[1], n[2], pre), Math.Max(1, Math.Min(3, p.Length)), pre);
        }

        public bool Satisfies(SemVer v, bool allowPre = false)
        {
            foreach (var set in _any)
            {
                bool ok = true;
                foreach (var (op, b) in set)
                {
                    int c = v.CompareTo(b);
                    ok &= op switch { ">=" => c >= 0, "<=" => c <= 0, ">" => c > 0, "<" => c < 0, _ => c == 0 };
                    if (!ok) break;
                }
                if (!ok) continue;
                // 미리보기판은 범위의 같은 major.minor.patch 미리보기판을 직접 적었을 때만 (npm 규칙)
                if (v.IsPre && !allowPre && !set.Any(x => x.V.IsPre && x.V.Major == v.Major && x.V.Minor == v.Minor && x.V.Patch == v.Patch)) continue;
                return true;
            }
            return false;
        }
    }

    public sealed class VpmVersion
    {
        public string Id = "", Version = "", DisplayName = "", Url = "", Sha256 = "", Unity = "", License = "", Repo = "";
        public Dictionary<string, string> Deps = new Dictionary<string, string>();
        public SemVer V;
    }

    public sealed class VpmRepo
    {
        public string Id = "", Url = "", Name = "";
        public Dictionary<string, List<VpmVersion>> Packages = new Dictionary<string, List<VpmVersion>>();

        public static VpmRepo Parse(string json, string? url = null)
        {
            var j = JNode.Parse(json.TrimStart('﻿'));
            var r = new VpmRepo { Id = j["id"].AsString, Url = url ?? j["url"].AsString, Name = j["name"].AsString };
            foreach (var p in j["packages"].Props)
            {
                var list = new List<VpmVersion>();
                foreach (var v in p.Value["versions"].Props)
                {
                    if (!SemVer.TryParse(v.Key, out var sv)) continue;
                    var e = v.Value;
                    var x = new VpmVersion
                    {
                        Id = p.Key, Version = v.Key, V = sv, DisplayName = e["displayName"].AsString, Url = e["url"].AsString,
                        Sha256 = e["zipSHA256"].AsString, Unity = e["unity"].AsString, License = e["license"].AsString, Repo = r.Id,
                    };
                    foreach (var d in e["vpmDependencies"].Props) x.Deps[d.Key] = d.Value.AsString;
                    list.Add(x);
                }
                list.Sort((a, b) => b.V.CompareTo(a.V));   // 새 것부터
                r.Packages[p.Key] = list;
            }
            return r;
        }
    }

    public static class VpmResolver
    {
        /// <summary>
        /// 원하는 패키지(id → 범위, 빈 문자열 = 최신 정식판)와 그 의존성을 고른다. 같은 패키지에 걸린 범위를 모두 만족하는 가장 새 정식판.
        /// unity: 프로젝트의 Unity 버전 ("2022.3") — 패키지의 unity 값이 이보다 새로우면 고르지 않는다.
        /// </summary>
        public static (List<VpmVersion> picked, List<string> errors) Resolve(IDictionary<string, string> wanted, IEnumerable<VpmRepo> repos, string unity = "2022.3")
        {
            var all = new Dictionary<string, List<VpmVersion>>();
            foreach (var r in repos)
                foreach (var kv in r.Packages)
                    if (!all.ContainsKey(kv.Key)) all[kv.Key] = kv.Value;   // 먼저 온 저장소가 이긴다 (공식 저장소를 앞에)
            var constraints = new Dictionary<string, List<(string from, VersionRange range)>>();
            void Need(string id, string range, string from)
            {
                if (!constraints.TryGetValue(id, out var l)) constraints[id] = l = new List<(string, VersionRange)>();
                l.Add((from, VersionRange.Parse(range)));
            }
            foreach (var kv in wanted) Need(kv.Key, kv.Value, "선택");
            SemVer.TryParse(unity, out var unityV);
            var picked = new Dictionary<string, VpmVersion>();
            var errors = new List<string>();
            for (int round = 0; round < 50; round++)
            {
                bool changed = false;
                foreach (var id in constraints.Keys.ToList())
                {
                    if (!all.TryGetValue(id, out var versions)) { if (!errors.Contains("저장소에 없음: " + id)) errors.Add("저장소에 없음: " + id); continue; }
                    var cs = constraints[id];
                    var best = versions.FirstOrDefault(v => cs.All(c => c.range.Satisfies(v.V)) && UnityOk(v, unityV));
                    if (best == null)
                    {
                        var msg = $"{id}: 조건을 모두 만족하는 버전이 없음 ({string.Join(", ", cs.Select(c => c.from + " " + c.range.Text))})";
                        if (!errors.Contains(msg)) errors.Add(msg);
                        continue;
                    }
                    if (picked.TryGetValue(id, out var cur) && cur.Version == best.Version) continue;
                    // 바뀐 버전: 예전 버전이 건 조건을 지우고 새 의존성 조건을 건다
                    if (cur != null) foreach (var l in constraints.Values) l.RemoveAll(c => c.from == id + "@" + cur.Version);
                    picked[id] = best; changed = true;
                    foreach (var d in best.Deps) Need(d.Key, d.Value, id + "@" + best.Version);
                }
                if (!changed) break;
            }
            // 더는 아무도 필요로 하지 않는 패키지는 뺀다 (버전을 바꾸며 의존성이 사라진 경우)
            var keep = new HashSet<string>(wanted.Keys);
            var stack = new Stack<string>(keep);
            while (stack.Count > 0) { var id = stack.Pop(); if (picked.TryGetValue(id, out var v)) foreach (var d in v.Deps.Keys) if (keep.Add(d)) stack.Push(d); }
            return (picked.Values.Where(v => keep.Contains(v.Id)).OrderBy(v => v.Id).ToList(), errors);
        }

        static bool UnityOk(VpmVersion v, SemVer unity)
        {
            if (v.Unity.Length == 0 || !SemVer.TryParse(v.Unity, out var need)) return true;
            return new SemVer(need.Major, need.Minor, 0).CompareTo(new SemVer(unity.Major, unity.Minor, 0)) <= 0;
        }

        /// <summary>VCC가 읽는 Packages/vpm-manifest.json.</summary>
        public static JNode Manifest(IEnumerable<VpmVersion> picked, IEnumerable<string> direct)
        {
            var list = picked.ToList();
            var deps = JNode.Obj(); var locked = JNode.Obj();
            foreach (var id in direct) { var v = list.FirstOrDefault(x => x.Id == id); if (v != null) deps.Set(id, JNode.Obj().Set("version", v.Version)); }
            foreach (var v in list)
            {
                var d = JNode.Obj(); foreach (var kv in v.Deps) d.Set(kv.Key, kv.Value);
                locked.Set(v.Id, JNode.Obj().Set("version", v.Version).Set("dependencies", d));
            }
            return JNode.Obj().Set("dependencies", deps).Set("locked", locked);
        }
    }
}
