// 커서로 놓기: 악세서리를 마우스 커서 아래 아바타 표면 바로 위에 올려 놓는다 (Host가 placeItem으로 시작).
//   움직이는 동안: 커서 아래에서 가장 가까운 메시 표면을 찾고, 아이템이 그 면에 파묻히지 않게 법선 방향으로 띄워 놓는다.
//   휠: 표면 법선을 축으로 돌리기 · 클릭(끌지 않고 뗌): 놓기 · 오른쪽 클릭 / Esc: 취소. 끌면 카메라는 평소처럼 돈다.
//   놓으면 맞은 면이 가장 많이 따르는 뼈에서 가장 가까운 휴머노이드 뼈를 붙일 곳으로 정하고,
//   그 뼈 기준 로컬 위치·회전·크기를 Host로 보낸다 (itemPlaced). Host가 가져오기 재료에 적고 IR을 다시 만든다.
// 렌더러 빌드에 Physics 모듈이 없어서 광선 검사는 직접 한다(MeshPick): 시작할 때 메시를 한 번 구워(BakeMesh) 월드 삼각형으로 두고,
// 렌더러마다 범위 상자로 먼저 거른다.
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class ItemPlacer
    {
        sealed class Target
        {
            public Vector3[] V; public int[] Tri; public Bounds B;
            public Transform[] Bones; public BoneWeight[] W; public Transform Self;
        }

        readonly SceneBuilder _scene;
        readonly List<Target> _targets = new List<Target>();
        Transform _item;
        int _node;
        Vector3 _startPos; Quaternion _startRot;
        readonly List<Vector3> _offs = new List<Vector3>();   // 시작 자세 기준 아이템 정점 − 아이템 루트 (월드 방향)
        Vector3 _centerOff;
        float _spin;                                          // 법선 둘레 회전(도)
        bool _hasHit; Vector3 _hitPos, _hitN; Transform _hitBone;
        Vector2 _pressAt, _lastMouse = new Vector2(-1, -1); bool _pressed;
        public bool Active => _item != null;
        public Action<JNode> Send;
        public Action<string> Notice;

        public ItemPlacer(SceneBuilder scene) { _scene = scene; }

        public void Start(int node)
        {
            Cancel(false);
            var t = _scene.NodeTransform(node);
            if (t == null) return;
            _item = t; _node = node; _startPos = t.position; _startRot = t.rotation; _spin = 0; _hasHit = false; _lastMouse = new Vector2(-1, -1);
            _targets.Clear(); _offs.Clear();
            var baked = new Mesh();
            try
            {
                var center = Vector3.zero; int n = 0;
                foreach (var ren in _scene.Renderers)
                {
                    if (ren == null || !ren.enabled || !ren.gameObject.activeInHierarchy) continue;
                    bool mine = ren.transform == t || ren.transform.IsChildOf(t);
                    if (!MeshPick.WorldTriangles(ren, baked, out var v, out var tri)) continue;
                    Transform[] bones = null; BoneWeight[] w = null;
                    if (ren is SkinnedMeshRenderer smr) { bones = smr.bones; w = smr.sharedMesh.boneWeights; }
                    if (mine)
                    {
                        // 아이템 자신: 바닥 맞추기용 정점 (많으면 솎는다)
                        int step = Math.Max(1, v.Length / 4000);
                        for (int i = 0; i < v.Length; i += step) { _offs.Add(v[i] - t.position); center += v[i]; n++; }
                        continue;
                    }
                    var b = new Bounds(v.Length > 0 ? v[0] : Vector3.zero, Vector3.zero);
                    foreach (var p in v) b.Encapsulate(p);
                    _targets.Add(new Target { V = v, Tri = tri, B = b, Bones = bones, W = w, Self = ren.transform });
                }
                _centerOff = n > 0 ? center / n - t.position : Vector3.zero;
            }
            finally { UnityEngine.Object.Destroy(baked); }
            Notice?.Invoke("아바타 위에 커서를 올리면 그 표면에 붙어요 · 클릭: 놓기 · 휠: 돌리기 · 오른쪽 클릭/Esc: 취소");
        }

        /// <summary>카메라 조작을 막을지: 휠(돌리기)과 오른쪽 클릭(취소)은 여기서 쓴다.</summary>
        public bool WantsInput() => Active && (Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f || Input.GetMouseButtonDown(1));

        public void Update(Camera cam, Vector2 mouse, bool overUi)
        {
            if (!Active) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) { Cancel(true); return; }
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f) _spin += wheel * 15f;
            if (Input.GetMouseButtonDown(0)) { _pressed = !overUi; _pressAt = mouse; }
            bool click = _pressed && Input.GetMouseButtonUp(0) && (mouse - _pressAt).magnitude < 5;
            if (!Input.GetMouseButton(0)) _pressed = false;
            if (Input.GetMouseButton(0) && !click) return;   // 끄는 동안은 카메라 회전: 아이템은 그대로

            // 광선 검사는 커서가 움직였을 때만 (삼각형이 수십만 개라 매 프레임은 무겁다). 휠로 돌리면 자리만 다시 잡는다
            if ((mouse - _lastMouse).sqrMagnitude > 0.25f || click)
            {
                _lastMouse = mouse;
                if (Raycast(cam.ScreenPointToRay(mouse), out var hit, out var n, out var bone)) { _hasHit = true; _hitPos = hit; _hitN = n; _hitBone = bone; }
            }
            if (_hasHit) Place();
            if (click)
            {
                if (_hasHit) Finish(); else Notice?.Invoke("아바타 위를 눌러 주세요 (빈 곳에는 놓을 수 없어요)");
            }
        }

        /// 아이템 회전 = 처음 회전을 법선 둘레로 _spin만큼. 위치 = 맞은 점 위, 아이템이 면 아래로 내려가지 않을 만큼 법선 쪽으로
        void Place()
        {
            var spin = Quaternion.AngleAxis(_spin, _hitN);
            var rot = spin * _startRot;
            float lowest = 0;
            if (_offs.Count > 0)
            {
                lowest = float.MaxValue;
                foreach (var o in _offs) lowest = Mathf.Min(lowest, Vector3.Dot(spin * o, _hitN));
            }
            var c = spin * _centerOff;
            // 중심이 맞은 점의 법선 위에 오고, 가장 낮은 정점이 면에 닿게
            var pos = _hitPos - c + _hitN * (Vector3.Dot(c, _hitN) - lowest);
            _item.SetPositionAndRotation(pos, rot);
        }

        bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out Transform bone)
        {
            point = normal = Vector3.zero; bone = null;
            float best = float.MaxValue;
            foreach (var t in _targets)
            {
                if (!t.B.IntersectRay(ray, out var enter) || enter > best) continue;
                var v = t.V; var tri = t.Tri;
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    Vector3 a = v[tri[i]], b = v[tri[i + 1]], c = v[tri[i + 2]];
                    if (!MeshPick.RayTri(ray, a, b, c, out float d) || d >= best) continue;
                    best = d;
                    point = ray.origin + ray.direction * d;
                    normal = Vector3.Cross(b - a, c - a).normalized;
                    if (Vector3.Dot(normal, ray.direction) > 0) normal = -normal;   // 카메라 쪽을 향하게
                    bone = BoneOf(t, tri[i], tri[i + 1], tri[i + 2]);
                }
            }
            return best < float.MaxValue;
        }

        /// 맞은 면의 세 정점이 가장 많이 따르는 뼈 (스킨드 메시가 아니면 렌더러 자신)
        static Transform BoneOf(Target t, int i0, int i1, int i2)
        {
            if (t.Bones == null || t.W == null || t.W.Length == 0) return t.Self;
            var sum = new Dictionary<int, float>();
            void Add(int bi, float w) { if (w > 0 && bi >= 0 && bi < t.Bones.Length) sum[bi] = (sum.TryGetValue(bi, out var s) ? s : 0) + w; }
            foreach (var i in new[] { i0, i1, i2 })
            {
                if (i >= t.W.Length) continue;
                var w = t.W[i];
                Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
            }
            int best = -1; float bw = 0;
            foreach (var kv in sum) if (kv.Value > bw) { bw = kv.Value; best = kv.Key; }
            return best >= 0 ? t.Bones[best] : t.Self;
        }

        void Finish()
        {
            // 맞은 뼈에서 위로 올라가며 가장 가까운 휴머노이드 뼈 (머리카락·옷 뼈는 그 부모 휴머노이드 뼈로)
            string human = null; Transform bone = null;
            var map = _scene.Scene?.Humanoid;
            if (map != null)
                for (var x = _hitBone; x != null && human == null; x = x.parent)
                    foreach (var kv in map)
                        if (_scene.NodeTransform(kv.Value) == x) { human = kv.Key; bone = x; break; }
            if (bone == null) { Notice?.Invoke("이 자리의 뼈를 찾지 못했어요 (휴머노이드 뼈 아래가 아님). 다른 곳을 눌러 주세요"); return; }
            var lp = bone.InverseTransformPoint(_item.position);
            var lr = Quaternion.Inverse(bone.rotation) * _item.rotation;
            var bs = bone.lossyScale; var isc = _item.lossyScale;
            var ls = new Vector3(isc.x / Nz(bs.x), isc.y / Nz(bs.y), isc.z / Nz(bs.z));
            Send?.Invoke(Msg.Make(Msg.ItemPlaced).Set("node", _node).Set("bone", human)
                .Set("offset", JNode.FloatArray(new[] { lp.x, lp.y, lp.z, lr.x, lr.y, lr.z, lr.w, ls.x, ls.y, ls.z })));
            _item = null; _targets.Clear(); _offs.Clear();   // 미리보기 자리에 둔다 (Host가 IR을 다시 만들어 다시 불러온다)
        }

        static float Nz(float v) => Mathf.Abs(v) < 1e-8f ? 1 : v;

        public void Cancel(bool tell)
        {
            if (_item != null) _item.SetPositionAndRotation(_startPos, _startRot);
            bool was = _item != null;
            _item = null; _targets.Clear(); _offs.Clear();
            if (was && tell) { Notice?.Invoke("놓기를 취소했어요"); Send?.Invoke(Msg.Make(Msg.ItemPlaced).Set("node", _node).Set("cancelled", true)); }
        }

        /// 맞은 점·법선 표시 (ViewAids가 GL.LINES를 연 상태에서)
        public void Draw()
        {
            if (!Active || !_hasHit) return;
            GL.Color(new Color(0.3f, 1f, 0.6f, 1f));
            GL.Vertex(_hitPos); GL.Vertex(_hitPos + _hitN * 0.06f);
            var t = Vector3.Cross(_hitN, Mathf.Abs(_hitN.y) < 0.9f ? Vector3.up : Vector3.right).normalized * 0.015f;
            var b = Vector3.Cross(_hitN, t);
            GL.Vertex(_hitPos - t); GL.Vertex(_hitPos + t); GL.Vertex(_hitPos - b); GL.Vertex(_hitPos + b);
        }
    }
}
