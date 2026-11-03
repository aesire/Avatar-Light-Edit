// Blink Fix · Mouth Fix 고르기 (Protocol/BlinkFix.cs·MouthFix.cs의 blinkFix·mouthFix 연산을 만들고 메시 편집 목록에 넣는다).
//   대상    blinkFix: 눈을 감는 블렌드쉐이프 (이름이 Blink·Wink·まばたき·笑い … 이고 주로 위아래로 움직이는 것)
//           mouthFix: 입을 움직이는 블렌드쉐이프 (모음·mouth_·あいうえお·jaw …) 중 지금 값이 0인 것 (0이 아닌 것은 입 편집 쪽)
//   편집    지금 0이 아닌 블렌드쉐이프 중 대상이 움직이는 곳을 함께 움직이는 것. 모음(vrc.)과 대상은 뺀다
// 보정 연산은 종류마다 하나씩 메시 편집 목록의 맨 앞에 둔다 (정점 번호를 쓰지 않아 앞에 있어도 되고, 끝의 정점 줄이기 슬라이더와 섞이지 않게).
// 눈 편집 값은 연산에 그대로 적어 둔다: 나중에 그 값을 바꾸면 Stale이 true가 되어 "다시 맞추기"를 권한다.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public static class BlinkFixPlan
    {
        public sealed class Source { public string Name = ""; public float Weight; public float Overlap; }

        /// <summary>처음 골라 두는 편집: 겹침이 이만큼 이상인 것 (낮으면 얼굴 전체를 바꾸는 것이라 빼지 않는다). 블렌드쉐이프 페이지·창 없는 편집 공통.</summary>
        public const float DefaultOverlap = 0.2f;

        /// <summary>블렌드쉐이프 페이지의 처음 고르기 그대로 만든 Mouth Fix (대상 = 입 벌리는 이름, 편집 = 겹침 DefaultOverlap 이상). 고를 것이 없으면 null. (2026-11-02 창 없는 편집용)</summary>
        public static JNode? MouthDefaultOp(Document doc, IrRenderer r, MeshData mesh, float strength = 1f)
        {
            var t = MouthTargets(doc, r, mesh).Where(MeshOps.IsMouthOpenName).ToList();
            if (t.Count == 0) return null;
            var s = Sources(doc, r, mesh, t).Where(x => x.Overlap >= DefaultOverlap).Select(x => (x.Name, x.Weight)).ToList();
            return s.Count == 0 ? null : MeshOps.MakeMouthFix(t, s, strength, 0f);
        }

        public static List<string> Targets(MeshData mesh) => mesh.Shapes.Where(s => MeshOps.IsClosingShape(mesh, s)).Select(s => s.Name).ToList();

        /// <summary>입 맞추기 대상 후보: 입 이름이고 실제로 움직이며, 지금 값이 0인 것 (값을 켜 둔 입 모양은 입 편집으로 본다).</summary>
        public static List<string> MouthTargets(Document doc, IrRenderer r, MeshData mesh) =>
            mesh.Shapes.Where(s => MeshOps.IsMouthName(s.Name) && Moves(s) && Math.Abs(doc.Get(Addr.BlendShape(r.Id, s.Name)).AsFloat) < 0.01f).Select(s => s.Name).ToList();

        static bool Moves(MeshData.Shape s)
        {
            if (s.Frames.Count == 0 || s.Frames[s.Frames.Count - 1].DPos is not float[] d) return false;
            foreach (var x in d) if (Math.Abs(x) > 1e-7f) return true;
            return false;
        }

        /// <summary>렌더러의 지금 값으로 눈 편집 블렌드쉐이프를 찾는다 (겹침이 큰 순). overlap = 그 블렌드쉐이프가 움직이는 양 중 깜빡임 영역 안의 몫.</summary>
        public static List<Source> Sources(Document doc, IrRenderer r, MeshData mesh, IEnumerable<string> targets, float minOverlap = 0.05f)
        {
            var tset = new HashSet<string>(targets);
            int n = mesh.VertexCount;
            var mask = new float[n];
            foreach (var sh in mesh.Shapes)
            {
                if (!tset.Contains(sh.Name) || sh.Frames.Count == 0 || sh.Frames[sh.Frames.Count - 1].DPos is not float[] d) continue;
                float max = 0;
                for (int v = 0; v < n; v++) max = Math.Max(max, Len(d, v));
                if (max <= 0) continue;
                for (int v = 0; v < n; v++) mask[v] = Math.Max(mask[v], Math.Min(1f, Len(d, v) / (0.25f * max)));
            }
            var list = new List<Source>();
            foreach (var sh in mesh.Shapes)
            {
                if (tset.Contains(sh.Name) || sh.Name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) || MeshOps.SeparatorName(sh.Name) != null || sh.Frames.Count == 0) continue;
                float w = doc.Get(Addr.BlendShape(r.Id, sh.Name)).AsFloat;
                if (Math.Abs(w) < 0.01f || sh.Frames[sh.Frames.Count - 1].DPos is not float[] d) continue;
                double inside = 0, all = 0;
                for (int v = 0; v < n; v++) { float l = Len(d, v); all += l; inside += l * mask[v]; }
                if (all <= 0) continue;
                float ov = (float)(inside / all);
                if (ov >= minOverlap) list.Add(new Source { Name = sh.Name, Weight = w, Overlap = ov });
            }
            return list.OrderByDescending(x => x.Overlap).ToList();
        }

        public static JNode? Current(Document doc, string meshId, string kind = MeshOps.BlinkFix) =>
            doc.Get(Addr.MeshEdit(meshId)).Items.FirstOrDefault(op => op["op"].AsString == kind);

        /// <summary>보정(kind = blinkFix·mouthFix)을 넣거나(op) 뺀다(null). 실행 취소 한 단계.</summary>
        public static void Set(Document doc, string meshId, JNode? op, string label, string kind = MeshOps.BlinkFix)
        {
            var list = JNode.Arr();
            if (op != null) list.Add(op);
            foreach (var x in doc.Get(Addr.MeshEdit(meshId)).Items) if (x["op"].AsString != kind) list.Add(x);
            doc.Set(Addr.MeshEdit(meshId), Ordered(list), label);
        }

        /// <summary>
        /// 메시 편집 순서: 블렌드쉐이프 다듬기(shapeMove) → 깜빡임·입 맞추기 → 나머지 (순서는 그 안에서 그대로).
        /// 맞추기가 다듬은 델타를 빼야 다듬은 다문 입으로도 벌린 입이 맞는다.
        /// </summary>
        public static JNode Ordered(JNode list)
        {
            int Rank(JNode op) => op["op"].AsString == MeshOps.ShapeMove ? 0 : MeshOps.IsShapeFix(op["op"].AsString) ? 1 : 2;
            var r = JNode.Arr();
            foreach (var op in list.Items.Select((op, i) => (op, i)).OrderBy(x => Rank(x.op)).ThenBy(x => x.i)) r.Add(op.op);
            return r;
        }

        /// <summary>적어 둔 편집 값이 지금 렌더러 값과 다르면 true (다시 맞춰야 한다).</summary>
        public static bool Stale(Document doc, IrRenderer r, JNode op) =>
            op["sources"].Items.Any(s => Math.Abs(doc.Get(Addr.BlendShape(r.Id, s["name"].AsString)).AsFloat - s["weight"].AsFloat) > 0.05f);

        /// <summary>
        /// 아바타 왼쪽 눈이 있는 메시 x 방향 (+1/−1). 이름에 좌우 표시가 있는 블렌드쉐이프(Blink_L, EyeRight …)가 움직이는 쪽으로 정하고,
        /// 그런 것이 없으면 −1 (Unity로 가져온 아바타는 보통 왼쪽이 −x).
        /// </summary>
        public static int LeftX(MeshData mesh)
        {
            int n = mesh.VertexCount;
            if (n == 0) return -1;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int v = 0; v < n && v * 3 < mesh.Pos.Length; v++) { minX = Math.Min(minX, mesh.Pos[v * 3]); maxX = Math.Max(maxX, mesh.Pos[v * 3]); }
            float mid = (minX + maxX) / 2;
            double vote = 0;
            foreach (var sh in mesh.Shapes)
            {
                int side = MeshOps.SideOfName(sh.Name);
                if (side == 0 || MeshOps.SeparatorName(sh.Name) != null || sh.Frames.Count == 0 || sh.Frames[sh.Frames.Count - 1].DPos is not float[] d) continue;
                double sx = 0, sw = 0;
                for (int v = 0; v < n && v * 3 + 2 < d.Length; v++) { float l = Len(d, v); sx += (mesh.Pos[v * 3] - mid) * l; sw += l; }
                if (sw <= 0) continue;
                vote += Math.Sign(sx) * -side;   // 왼쪽(−1) 이름이 +x에서 움직이면 +1 표
            }
            return vote > 0 ? 1 : -1;
        }

        /// <summary>
        /// 자동 모드의 고르기: 대상 = 깜빡임·윙크 (IsPlainBlink), 편집 = 지금 값이 0이 아니고 이름에 눈(Eye·目·눈 …)이 들어간 것,
        /// 또는 이름은 달라도 깜빡임 둘레만 바꾸는 것 (겹침 50% 이상).
        /// </summary>
        public static (List<string> Targets, List<Source> Sources) AutoPick(Document doc, IrRenderer r, MeshData mesh)
        {
            var targets = Targets(mesh).Where(MeshOps.IsPlainBlink).ToList();
            if (targets.Count == 0) return (targets, new List<Source>());
            // 눈을 감는 모양(깜빡임·웃는 눈 …)은 대상이 아니어도 편집이 아니다 — 빼면 그 모양으로 감은 눈이 깨진다
            var closing = new HashSet<string>(Targets(mesh));
            var sources = Sources(doc, r, mesh, targets).Where(s => !closing.Contains(s.Name) && (MeshOps.IsEyeName(s.Name) || s.Overlap >= 0.5f)).ToList();
            return (targets, sources);
        }

        /// <summary>자동 모드 연산 (지금 값으로). 세기·범위·눈마다 세기는 지금 연산에서 이어 받는다. 고를 것이 없으면 null.</summary>
        public static JNode? AutoOp(Document doc, IrRenderer r, MeshData mesh, JNode? cur)
        {
            var (t, s) = AutoPick(doc, r, mesh);
            if (t.Count == 0) return null;
            var op = MeshOps.MakeBlinkFix(t, s.Select(x => (x.Name, x.Weight)),
                cur != null && cur.Has("strength") ? cur["strength"].AsFloat : 1f, cur != null && cur.Has("falloff") ? cur["falloff"].AsFloat : 0f);
            if (cur != null && cur.Has("left")) op = MeshOps.WithEyes(op, cur["left"].AsFloat, cur["right"].AsFloat, (int)cur["leftX"].AsFloat);
            return op.Set("auto", true);
        }

        /// <summary>
        /// 블렌드쉐이프 값이 바뀐 뒤 (Document.Changed, 실행 취소가 아닐 때): 자동 모드인 Blink Fix를 지금 값에 맞게 바꿔 같은 단계에 덧붙인다.
        /// 눈 편집이 하나도 없으면 연산은 남기되 편집이 빈 채로 (아무것도 빼지 않음). 바뀐 메시 id들을 돌려준다.
        /// </summary>
        public static List<string> Follow(Document doc, IEnumerable<string> rendererIds, Func<string, MeshData?> meshOf)
        {
            var changed = new List<string>();
            if (doc.Replaying) return changed;
            foreach (var rid in rendererIds.Distinct())
            {
                var r = doc.Original.FindRenderer(rid);
                if (r == null || r.Mesh.Length == 0) continue;
                var cur = Current(doc, r.Mesh);
                if (cur == null || !cur["auto"].AsBool) continue;
                var mesh = meshOf(r.Mesh);
                if (mesh == null) continue;
                var op = AutoOp(doc, r, mesh, cur);
                if (op == null || Values.Equal(op, cur)) continue;
                var list = JNode.Arr().Add(op);
                foreach (var x in doc.Get(Addr.MeshEdit(r.Mesh)).Items) if (x["op"].AsString != MeshOps.BlinkFix) list.Add(x);
                doc.Amend(Addr.MeshEdit(r.Mesh), Ordered(list));
                changed.Add(r.Mesh);
            }
            return changed;
        }

        static float Len(float[] a, int v) => (float)Math.Sqrt(a[v * 3] * a[v * 3] + a[v * 3 + 1] * a[v * 3 + 1] + a[v * 3 + 2] * a[v * 3 + 2]);
    }
}
