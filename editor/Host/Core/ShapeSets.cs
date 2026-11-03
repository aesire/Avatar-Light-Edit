// 블렌드쉐이프 세팅 저장·비교·되돌리기 (2026-10-23).
// 지금 블렌드쉐이프 값(모든 메시, 원본과 다른 값만)을 이름을 붙여 Document.Variants에 Kind "shapes"로 담는다 — 세션 저장·복구·아이템 다시 만들기(주소 옮기기)는 변형과 같은 길.
// 되돌리기는 블렌드쉐이프만 바꾼다 (머티리얼·자세 등은 그대로): 세팅에 없는 블렌드쉐이프 덮어쓰기는 원본으로, 있는 것은 그 값으로 — 바뀌는 값만 실행 취소 한 단계.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class ShapeSets
    {
        public const string Kind = "shapes";
        public const string DefaultName = "저장된 블렌드쉐이프";   // 이름을 비우고 저장하면 "저장된 블렌드쉐이프 1, 2, 3…"

        public static IEnumerable<Document.Variant> All(Document doc) => doc.Variants.Where(v => v.Kind == Kind);

        /// <summary>지금 블렌드쉐이프 값 (원본과 다른 것만, 주소 순).</summary>
        public static JNode Snapshot(Document doc)
        {
            var arr = JNode.Arr();
            foreach (var kv in doc.Overrides.Where(k => k.Key.Kind == AddrKind.BlendShape).OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
                arr.Add(JNode.Obj().Set("op", "set").Set("a", kv.Key.ToString()).Set("v", kv.Value));
            return arr;
        }

        /// <summary>지금 값으로 새 세팅을 만들어 목록 끝에 더한다. 이름이 비면 "저장된 블렌드쉐이프 N" (2026-10-23 사용자 요청).</summary>
        public static Document.Variant Add(Document doc, string name, string photo = "")
        {
            var v = new Document.Variant
            {
                Kind = Kind, Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = name.Trim().Length > 0 ? name.Trim() : NextName(doc),
                Changeset = Snapshot(doc), Photo = photo, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            doc.Variants.Add(v);
            doc.MarkUnsaved();
            return v;
        }

        public static string NextName(Document doc)
        {
            // 이름을 직접 붙인 세팅은 세지 않는다: 지금 있는 "저장된 블렌드쉐이프 N" 중 가장 큰 번호 다음
            int max = 0;
            foreach (var v in All(doc))
                if (v.Name.StartsWith(DefaultName + " ", StringComparison.Ordinal) && int.TryParse(v.Name.Substring(DefaultName.Length + 1), out var n)) max = Math.Max(max, n);
            return DefaultName + " " + (max + 1);
        }

        /// <summary>지금 값으로 덮어쓴다 (사진을 새로 찍었으면 사진도).</summary>
        public static void Overwrite(Document doc, Document.Variant v, string? photo = null)
        {
            v.Changeset = Snapshot(doc);
            if (photo != null) v.Photo = photo;
            v.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            doc.MarkUnsaved();
        }

        public static void Remove(Document doc, Document.Variant v) { if (doc.Variants.Remove(v)) doc.MarkUnsaved(); }

        static Dictionary<Addr, JNode> Saved(Document.Variant v)
        {
            var d = new Dictionary<Addr, JNode>();
            foreach (var op in v.Changeset.Items)
            {
                if (op["op"].AsString != "set") continue;
                Addr a;
                try { a = Addr.Parse(op["a"].AsString); } catch (Exception) { continue; }
                if (a.Kind == AddrKind.BlendShape) d[a] = op["v"];
            }
            return d;
        }

        /// <summary>세팅과 지금이 다른 블렌드쉐이프 하나.</summary>
        public sealed class Diff { public Addr Addr; public double Now, Saved; internal JNode Value = JNode.Null; }

        /// <summary>
        /// 지금과 세팅이 다른 블렌드쉐이프 (세팅에 없는 것 = 원본 값). 지금 IR에 없는 렌더러·블렌드쉐이프는 뺀다.
        /// 렌더러·이름 순.
        /// </summary>
        public static List<Diff> Compare(Document doc, Document.Variant v)
        {
            var saved = Saved(v);
            var list = new List<Diff>();
            foreach (var a in saved.Keys.Concat(doc.Overrides.Keys.Where(k => k.Kind == AddrKind.BlendShape)).Distinct())
            {
                if (!Exists(doc, a)) continue;
                double now = doc.Get(a).AsDouble;
                var value = saved.TryGetValue(a, out var s) ? s : doc.GetOriginal(a);
                double want = value.AsDouble;
                if (Math.Abs(now - want) > 1e-4) list.Add(new Diff { Addr = a, Now = now, Saved = want, Value = value });
            }
            return list.OrderBy(d => d.Addr.Target, StringComparer.Ordinal).ThenBy(d => d.Addr.Name, StringComparer.Ordinal).ToList();
        }

        static bool Exists(Document doc, Addr a)
        {
            var r = doc.Original.FindRenderer(a.Target);
            var m = r == null ? null : doc.Original.FindMesh(r.Mesh);
            return m != null && m.BlendShapeIndex(a.Name) >= 0;
        }

        /// <summary>세팅의 값으로 블렌드쉐이프를 바꾼다 (실행 취소 한 단계). 돌려주는 값: 바뀐 블렌드쉐이프 수.</summary>
        public static int Apply(Document doc, Document.Variant v)
        {
            var diffs = Compare(doc, v);
            if (diffs.Count == 0) return 0;
            using (doc.Group("BlendShape 프리셋 불러오기: " + v.Name))
                foreach (var d in diffs) doc.Set(d.Addr, d.Value);
            return diffs.Count;
        }

        /// <summary>세팅에 든 블렌드쉐이프 수 (지금 IR에 있는 것만).</summary>
        public static int Count(Document doc, Document.Variant v) => Saved(v).Keys.Count(a => Exists(doc, a));
    }
}
