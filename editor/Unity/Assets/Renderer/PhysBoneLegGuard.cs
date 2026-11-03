// 다리 뚫림 막기: 치마·코트 자락처럼 엉덩이 아래로 늘어진 PhysBone이 다리를 뚫지 않게, 허벅지·종아리에 캡슐 콜라이더를 더한다.
// 이 에디터 안에서만 쓰는 미리보기 보조다 (VRChat에서는 원본 PhysBone에 적힌 콜라이더만 쓴다). PhysBone 메뉴에서 끌 수 있다.
//   캡슐: 휴머노이드 UpperLeg → LowerLeg, LowerLeg → Foot. 반지름은 그 뼈를 주로 따르는 정점이 뼈에서 떨어진 거리(35번째 백분위)로 잰다.
//   대상: 루트가 Hips 아래(다리·팔 본 아래는 빼고)에 있고 쉬는 자세에서 엉덩이보다 아래로 내려오는 체인.
//         그 다리 본에 원본 콜라이더가 이미 붙어 있으면 그 구간은 더하지 않는다.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed partial class PhysBoneSim
    {
        public bool LegGuard = true;
        public int LegGuardChains { get; private set; }

        public void SetLegGuard(bool on) => LegGuard = on;

        void BuildLegGuard(SceneBuilder scene)
        {
            LegGuardChains = 0;
            var hips = scene.HumanBone("Hips");
            if (hips == null) return;
            var segs = new List<(Transform bone, Transform child, int col)>();
            foreach (var side in new[] { "Left", "Right" })
            {
                var up = scene.HumanBone(side + "UpperLeg"); var lo = scene.HumanBone(side + "LowerLeg"); var foot = scene.HumanBone(side + "Foot");
                if (up != null && lo != null) segs.Add((up, lo, -1));
                if (lo != null && foot != null) segs.Add((lo, foot, -1));
            }
            if (segs.Count == 0) return;
            var radii = LegRadii(scene, segs);
            for (int i = 0; i < segs.Count; i++)
            {
                var (bone, child, _) = segs[i];
                var a = bone.position; var b = child.position;
                float len = Vector3.Distance(a, b);
                if (len < 1e-4f) continue;
                float s = Mathf.Max(1e-6f, Mathf.Abs(bone.lossyScale.x));
                float r = radii[i] > 0 ? Mathf.Clamp(radii[i], 0.08f * len, 0.45f * len) : 0.18f * len;
                var worldRot = Quaternion.FromToRotation(Vector3.up, (b - a) / len);
                _cols.Add(new Col
                {
                    T = bone, Enabled = true, Auto = true, Shape = 1,
                    Radius = r / s, Height = (len + 2 * r) / s,
                    Pos = bone.InverseTransformPoint((a + b) * 0.5f), Rot = Quaternion.Inverse(bone.rotation) * worldRot,
                });
                segs[i] = (bone, child, _cols.Count - 1);
            }

            // 다리에 달린 것(양말 리본 등)과 팔에 달린 것(소매·팔찌 끈: 팔을 내리면 뿌리가 허벅지 안에 들어가 서로 밀어낸다)은 뺀다
            var limbs = new List<Transform>();
            foreach (var side in new[] { "Left", "Right" })
                foreach (var b in new[] { "UpperLeg", "Shoulder", "UpperArm" })
                    if (scene.HumanBone(side + b) is Transform t) limbs.Add(t);
            foreach (var ch in _chains)
            {
                if (ch.SoftTarget || !IsUnder(ch.Root, hips) || limbs.Exists(l => IsUnder(ch.Root, l))) continue;
                float low = float.MaxValue;
                foreach (var p in ch.P) low = Mathf.Min(low, (p.T != null ? p.T.position : p.Owner.TransformPoint(p.EndLocal)).y);
                if (low > hips.position.y - 0.02f) continue;   // 엉덩이 아래로 늘어지지 않으면 다리와 만날 일이 없다
                foreach (var (bone, _, col) in segs)
                {
                    if (col < 0) continue;
                    bool has = false;
                    foreach (var ci in ch.Colliders) { var ct = _cols[ci].T; if (ct != null && (ct == bone || ct.parent == bone)) has = true; }
                    if (!has) ch.LegColliders.Add(col);
                }
                if (ch.LegColliders.Count > 0) LegGuardChains++;
            }
        }

        static bool IsUnder(Transform t, Transform ancestor)
        {
            for (var x = t; x != null; x = x.parent) if (x == ancestor) return true;
            return false;
        }

        /// 구간마다 그 뼈를 주로(가중치 0.5 이상) 따르는 정점이 뼈 선분에서 떨어진 거리의 35번째 백분위 (정점이 없으면 0)
        static float[] LegRadii(SceneBuilder scene, List<(Transform bone, Transform child, int col)> segs)
        {
            var dist = new List<float>[segs.Count];
            for (int i = 0; i < dist.Length; i++) dist[i] = new List<float>();
            var baked = new Mesh();
            try
            {
                foreach (var ren in scene.Renderers)
                {
                    if (!(ren is SkinnedMeshRenderer smr) || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                    var bones = smr.bones;
                    var which = new int[bones.Length];
                    bool any = false;
                    for (int b = 0; b < bones.Length; b++)
                    {
                        which[b] = segs.FindIndex(s => s.bone == bones[b]);
                        any |= which[b] >= 0;
                    }
                    if (!any) continue;
                    var weights = smr.sharedMesh.boneWeights;
                    if (weights.Length == 0) continue;
                    smr.BakeMesh(baked, true);
                    var verts = baked.vertices;
                    var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                    for (int v = 0; v < verts.Length && v < weights.Length; v++)
                    {
                        var w = weights[v];
                        if (w.weight0 < 0.5f || w.boneIndex0 >= which.Length) continue;
                        int si = which[w.boneIndex0];
                        if (si < 0) continue;
                        var a = segs[si].bone.position; var ab = segs[si].child.position - a;
                        var p = m.MultiplyPoint3x4(verts[v]);
                        float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-10f, ab.sqrMagnitude);
                        if (t < 0.15f || t > 0.85f) continue;   // 관절 근처의 불룩한 곳은 뺀다
                        dist[si].Add(Vector3.Distance(p, a + ab * t));
                    }
                }
            }
            finally { UnityEngine.Object.Destroy(baked); }
            var r = new float[segs.Count];
            for (int i = 0; i < r.Length; i++)
            {
                if (dist[i].Count < 8) continue;
                dist[i].Sort();
                r[i] = dist[i][(int)(dist[i].Count * 0.35f)];
            }
            return r;
        }

        /// <summary>시험용: 이름으로 고른 체인의 관절마다 쉬는 자리·시뮬레이션 자리 차이와 각 콜라이더와의 겹침(쉬는 자리 기준).</summary>
        public void Describe(string rootName, List<string> lines)
        {
            foreach (var c in _cols) Prepare(c);
            foreach (var ch in _chains)
            {
                if (ch.Root.name != rootName) continue;
                float scale = Mathf.Abs(ch.Root.lossyScale.x);
                lines.Add($"chain {rootName} parts {ch.P.Count} cols {ch.Colliders.Count} leg {ch.LegColliders.Count} limit {ch.LimitType} scale {scale:0.###} active {ch.Active} v11 {ch.V11}");
                foreach (var p in ch.P)
                {
                    if (p.Parent < 0) continue;
                    var par = ch.P[p.Parent];
                    var restDir = (p.Target - par.Target).normalized; var simDir = (p.Pos - par.Pos).normalized;
                    string ov = "";
                    foreach (var ci in ch.Colliders)
                    {
                        var c = _cols[ci]; if (!c.Active || c.Shape == 2) continue;
                        Vector3 ca = c.Shape == 1 ? c.A : c.C, cb = c.Shape == 1 ? c.B : c.C;
                        float o = c.R + p.Radius * scale - (p.Target - Closest(ca, cb, p.Target)).magnitude;
                        if (o > 0) ov += $" {c.T.name}:{o * 100:0.0}";
                    }
                    lines.Add($"  {(p.T != null ? p.T.name : "end")}{(p.Fixed ? "[F]" : "")} r {p.Radius * scale * 100:0.0}cm move {(p.Pos - p.Target).magnitude * 100:0.0}cm angle {Vector3.Angle(restDir, simDir):0}° grav {p.Gravity:0.##} pull {p.Pull:0.##} lim {p.MaxAngleX:0}/{p.MaxAngleZ:0} len {p.LenRatio:0.###} at {Position(p).x:0.####},{Position(p).y:0.####},{Position(p).z:0.####} restOverlap[{ov} ]");
                }
            }
        }

        /// <summary>시험용: 이름으로 고른 체인의 끝 관절을 지금 자리에서 offset만큼 옮긴 곳으로 끌어 잡고 있는다. rootName이 비었으면 놓는다. VR 손 자리를 쓴다 (마우스 잡기는 버튼을 안 누르면 바로 풀린다).</summary>
        public void TestGrab(string rootName, Vector3 offset)
        {
            VrRelease(0);
            foreach (var ch in _chains)
                if (ch.Root.name == rootName) { var p = ch.P[ch.P.Count - 1]; _vrGrab[0] = new Hold(ch, p); _vrTarget[0] = Position(p) + offset; return; }
        }

        /// <summary>뼈 캡슐이 콜라이더를 1 cm(아바타 크기 기준) 넘게 파고든 곳 수 / 검사한 뼈 수 (E2E 확인용). 다리 캡슐은 꺼 두어도 잰다 (끄고 켠 차이를 보려고).</summary>
        public (int pen, int bones) Penetrations(List<string> detail = null)
        {
            foreach (var c in _cols) Prepare(c);
            int pen = 0, n = 0;
            foreach (var ch in _chains)
            {
                if (!ch.Active) continue;
                float scale = Mathf.Abs(ch.Root.lossyScale.x);
                for (int i = 1; i < ch.P.Count; i++)
                {
                    var p = ch.P[i]; var par = ch.P[p.Parent];
                    float r0 = par.Radius * scale, r = p.Radius * scale;
                    var a = Position(par); var b = Position(p);
                    bool hit = false;
                    string what = null; float depth = 0;
                    void Test(int ci)
                    {
                        var c = _cols[ci];
                        if (!c.Active || c.Shape == 2 || c.Inside) return;
                        Vector3 ca = c.Shape == 1 ? c.A : c.C, cb = c.Shape == 1 ? c.B : c.C;
                        // 겹친 깊이: (콜라이더 반지름 + 뼈 반지름) − 콜라이더 축과 뼈 사이 거리. 부모 관절이 이미 겹친 만큼은 빼고 센다 (시뮬레이션과 같은 규칙)
                        float parentPen = Mathf.Max(0, c.R + r0 - (a - Closest(ca, cb, a)).magnitude);
                        float allow = Allow(par, ci);
                        if (allow >= 0) parentPen = Mathf.Min(parentPen, allow);
                        Vector3 onCol; float t;
                        if (c.AsSpheres) { onCol = Closest(ca, cb, b); t = 1; } else SegmentClosest(ca, cb, a, b, out onCol, out t);
                        float d = c.R + Mathf.Lerp(r0, r, t) - Vector3.Distance(onCol, a + (b - a) * t) - parentPen * (1 - t);
                        if (d > 0.01f * scale && (what == null || d > depth)) { hit = true; depth = d; what = $"{(p.T != null ? p.T.name : par.T?.name + "(끝)")}{(p.Fixed ? "[고정]" : "")} ↔ {(c.Auto ? "다리:" : "")}{c.T.name} {d * 100 / scale:0.0}cm"; }
                    }
                    foreach (var ci in ch.Colliders) Test(ci);
                    foreach (var ci in ch.LegColliders) Test(ci);
                    n++; if (hit) { pen++; detail?.Add(what); }
                }
            }
            return (pen, n);
        }
    }
}
