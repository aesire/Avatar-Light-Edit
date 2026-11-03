// 바닥 맞추기 (PLAN 3.5): 자세를 바꾸면 아바타의 가장 낮은 곳이 불러온 자세의 가장 낮은 곳과 같은 높이에 오도록 몸(Hips)을 위아래로 옮긴다.
// 앉거나 눕는 자세가 공중에 뜨거나, 다리를 든 자세가 바닥에 묻히지 않게. 자세 값(bodyHeight)은 바꾸지 않는 미리보기 보조다.
//
// 자세를 바꿀 때마다(본을 끄는 동안 매 프레임) 메시를 굽지 않도록, 장면을 불러올 때 한 번 구워 각 정점을 가장 가까운 휴머노이드 본
// (정점의 주 가중치 본에서 위로 올라가며 처음 만나는 휴머노이드 본)에 묶고, 렌더러·본마다 여러 방향의 끝점(지지점)만 본 공간에 남긴다.
// 가장 낮은 곳 = 켜져 있는 렌더러의 지지점을 지금 본 위치로 옮긴 것 중 가장 낮은 y. 휴머노이드 본 밑에 있지 않은 것(발밑 장식 등)과 PhysBone이 움직이는 뼈에 실린 것(치마·코트 자락·머리카락)은 세지 않는다.
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class FloorFit
    {
        sealed class Part { public Renderer Ren; public Transform Bone; public Vector3[] Points; public float RestMin; }
        readonly List<Part> _parts = new List<Part>();
        static Vector3[] _dirs;

        Transform _root;   // 아바타 전체(장면 루트): 높이는 이 공간에서 잰다 (아바타를 핸들로 옮기거나 돌려도 바닥 맞추기가 따라간다)
        float Up(Vector3 world) => _root != null ? _root.InverseTransformPoint(world).y : world.y;
        public Transform Root => _root;

        public bool Ready => _parts.Count > 0;

        public void Clear() => _parts.Clear();

        /// <summary>쉬는 자세에서 부른다 (장면을 불러온 직후).</summary>
        /// <summary>moving = PhysBone이 움직이는 뼈: 이 뼈를 거쳐 휴머노이드 본에 닿는 정점(치마·코트 자락·머리카락)은 세지 않는다 — 앉으면 실제로는 다리·바닥에 얹히므로.</summary>
        public void Build(SceneBuilder scene, ICollection<Transform> humanBones, ICollection<Transform> moving = null)
        {
            _parts.Clear();
            _root = scene.Root;
            if (_dirs == null) _dirs = Directions(64);
            var set = new HashSet<Transform>(humanBones);
            Transform Human(Transform t)
            {
                for (; t != null; t = t.parent)
                {
                    if (set.Contains(t)) return t;
                    if (moving != null && moving.Contains(t)) return null;
                }
                return null;
            }
            var baked = new Mesh();
            try
            {
                foreach (var ren in scene.Renderers)
                {
                    if (ren == null) continue;
                    Vector3[] verts; Matrix4x4 m; Transform[] owner;
                    if (ren is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh == null) continue;
                        smr.BakeMesh(baked, true);
                        verts = baked.vertices;
                        m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                        var weights = smr.sharedMesh.boneWeights;
                        var bones = smr.bones;
                        var boneHuman = new Transform[bones.Length];
                        for (int b = 0; b < bones.Length; b++) boneHuman[b] = Human(bones[b]);
                        var fallback = Human(smr.rootBone != null ? smr.rootBone : smr.transform);
                        owner = new Transform[verts.Length];
                        for (int v = 0; v < verts.Length; v++)
                            owner[v] = v < weights.Length && weights[v].boneIndex0 < boneHuman.Length ? boneHuman[weights[v].boneIndex0] : fallback;
                    }
                    else
                    {
                        var mf = ren.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;
                        verts = mf.sharedMesh.vertices;
                        m = ren.transform.localToWorldMatrix;
                        var h = Human(ren.transform);
                        owner = new Transform[verts.Length];
                        for (int v = 0; v < verts.Length; v++) owner[v] = h;
                    }
                    // 본마다 정점을 모아 방향별 끝점만 남긴다
                    var groups = new Dictionary<Transform, List<Vector3>>();
                    for (int v = 0; v < verts.Length; v++)
                    {
                        var bone = owner[v];
                        if (bone == null) continue;
                        if (!groups.TryGetValue(bone, out var list)) groups[bone] = list = new List<Vector3>();
                        list.Add(bone.InverseTransformPoint(m.MultiplyPoint3x4(verts[v])));
                    }
                    foreach (var kv in groups)
                    {
                        var pts = Support(kv.Value);
                        float restMin = float.MaxValue;
                        foreach (var p in pts) restMin = Mathf.Min(restMin, Up(kv.Key.TransformPoint(p)));
                        _parts.Add(new Part { Ren = ren, Bone = kv.Key, Points = pts, RestMin = restMin });
                    }
                }
            }
            finally { Object.Destroy(baked); }
        }

        /// <summary>켜져 있는 렌더러에 대해 (불러온 자세의 가장 낮은 높이, 지금 가장 낮은 높이). 셀 것이 없으면 false.</summary>
        public bool Lowest(out float rest, out float now) => Lowest(out rest, out now, out _);

        /// <summary>which = 지금 가장 낮은 곳 "렌더러 이름 / 본 이름" (시험용).</summary>
        public bool Lowest(out float rest, out float now, out string which)
        {
            rest = float.MaxValue; now = float.MaxValue; which = "";
            foreach (var part in _parts)
            {
                if (part.Ren == null || !part.Ren.enabled || !part.Ren.gameObject.activeInHierarchy || part.Bone == null) continue;
                rest = Mathf.Min(rest, part.RestMin);
                var l2w = _root != null ? _root.worldToLocalMatrix * part.Bone.localToWorldMatrix : part.Bone.localToWorldMatrix;
                foreach (var p in part.Points) { float y = l2w.MultiplyPoint3x4(p).y; if (y < now) { now = y; which = part.Ren.name + " / " + part.Bone.name; } }
            }
            return rest < float.MaxValue;
        }

        static Vector3[] Support(List<Vector3> pts)
        {
            var keep = new HashSet<int>();
            foreach (var d in _dirs)
            {
                int best = 0; float bestDot = float.MinValue;
                for (int i = 0; i < pts.Count; i++) { float x = Vector3.Dot(pts[i], d); if (x > bestDot) { bestDot = x; best = i; } }
                keep.Add(best);
            }
            var r = new Vector3[keep.Count]; int k = 0;
            foreach (var i in keep) r[k++] = pts[i];
            return r;
        }

        /// <summary>구 위에 고르게 퍼진 방향 (피보나치 격자).</summary>
        static Vector3[] Directions(int n)
        {
            var r = new Vector3[n];
            float golden = Mathf.PI * (3 - Mathf.Sqrt(5));
            for (int i = 0; i < n; i++)
            {
                float y = 1 - 2f * (i + 0.5f) / n, rad = Mathf.Sqrt(1 - y * y), a = golden * i;
                r[i] = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
            }
            return r;
        }
    }
}
