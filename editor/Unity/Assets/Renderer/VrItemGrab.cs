// VR 악세서리 잡아 배치 (PLAN 3.12, 2026-10-27 사용자 "VR모드 남은 작업 해치우자"): 붙인 아이템을 그립으로 잡아 옮긴다.
//   대상: 지금 아바타에 붙인 아이템(Outfits) 중 통째로 옮길 수 있는 것 — 렌더러와 그 뼈가 모두 아이템 루트 아래 (ObjectMover와 같은 조건).
//   관찰자일 때만 (몸 모드는 1인칭 사본·IK가 덮는다). 관절 공이 더 가까우면 관절이 먼저, 아이템 범위 안이면 아바타째 잡기보다 아이템이 먼저.
//   - 손 공이 아이템 메시 범위 안이면 범위 상자가 노랑, 잡으면 주황. 손에 붙은 것처럼 6축으로 따라온다.
//   - 잡은 채 다른 손 그립 = 두 손 크기 (손 사이 거리 비율, 0.2~5배).
//   - 놓을 곳: 아이템 가운데에서 가까운 휴머노이드 뼈가 초록 선으로 보인다. 잡은 손 스틱 좌우 = 다음 후보(가까운 5개).
//   - 놓으면: 지금 붙은 뼈와 같으면 옮기기만(nodeMoved — 바로 반영, 실행 취소) · 다르면 vrItemPlaced로 그 뼈에 다시 붙인다
//     (Host가 "커서로 놓기"와 같이 가져오기 재료에 적고 다시 만든다 — 잠깐 다시 불러온다).
//   서랍은 VR 에디터 창의 아바타 페이지 목록으로 대신한다.
//   2026-11-01 더함 (Host VR 메뉴에서 켜고 끔):
//   - 스냅: 부모 기준 1 cm · 15° 단위로 맞춘다.
//   - 표면에 붙이기: 놓을 때 아이템 가운데에서 놓을 뼈 쪽으로 광선을 쏴 12 cm 안에 메시가 있으면, 가장 낮은 정점이 그 면에 닿게 민다 (계산은 ItemPlacer와 같음).
//   - 좌우 대칭: 이름이 짝인 아이템(_L/_R, Left/Right, 左/右, 왼쪽/오른쪽)이 있으면 아바타 좌우로 거울 자리에 같이 옮긴다. 아이템을 복제하지는 않는다 (재료가 프리팹 하나당 하나).
//   - 스튜디오 소품: 같은 조작으로 잡는다. 똑바로 선 채 Y축으로만 돌고 뼈에 붙지 않으며, 놓으면 vrPropMoved로 Host의 소품 값에 적는다.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class VrItemGrab
    {
        sealed class Item { public int Node = -1; public string Name, PropId; public Transform T; public Bounds B; }

        const float Pad = 0.02f, MinScale = 0.2f, MaxScale = 5f, SnapMove = 0.01f, SnapAngle = 15f, SurfaceBack = 0.15f, SurfaceReach = 0.12f;
        const int Candidates = 5;
        SceneBuilder _scene;
        readonly List<Item> _items = new List<Item>();
        readonly Item[] _hover = new Item[2];
        Item _held; int _hand = -1, _scaleHand = -1;
        Vector3 _offPos, _startPos, _startScale; Quaternion _offRot, _startRot;
        float _d0;
        readonly List<(string Name, Transform T)> _cand = new List<(string, Transform)>();
        int _pick; bool _stickArmed = true; bool _changed;
        GameObject _root; Mesh _lines; Material _mat;
        readonly List<Vector3> _lv = new List<Vector3>(); readonly List<Color> _lc = new List<Color>(); readonly List<int> _li = new List<int>();

        public Action<JNode> Send = _ => { };
        public bool Enabled = true, Snap, Surface, Mirror;
        public VrControllers Controllers;
        public StudioManager Studio;

        public void Init(SceneBuilder scene) { _scene = scene; }

        /// <summary>손 h가 아이템을 가리키거나 잡고 있음 (아바타째 잡기·VR 창 그립보다 먼저).</summary>
        public bool Near(int h) => Enabled && (_hand == h || _scaleHand == h || _hover[h] != null);

        /// <summary>VrView.Pre에서 본 잡기 전에: 가리키는 아이템만 찾는다.</summary>
        public void Prepare(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right, bool usable)
        {
            _hover[0] = _hover[1] = null;
            if (!Enabled || !usable || _scene?.Scene == null) { Drop(true); return; }
            Gather();
            float k = Mathf.Max(1e-3f, rig.lossyScale.x);
            for (int h = 0; h < 2; h++)
            {
                var d = h == 0 ? left : right;
                if (d == null || !d.Tracked || h == _hand || h == _scaleHand) continue;
                var tip = rig.TransformPoint(VrControllers.Tip(h, input, d));
                float best = float.MaxValue;
                foreach (var it in _items)
                {
                    if (it == _held) continue;
                    var b = it.B; b.Expand(2 * Pad * k);
                    if (!b.Contains(tip)) continue;
                    float vol = b.size.x * b.size.y * b.size.z;
                    if (vol < best) { best = vol; _hover[h] = it; }
                }
            }
        }

        /// <summary>VrView.Pre에서 본 잡기 다음에. boneHand(h) = 그 손이 관절을 가리키거나 잡음 (관절이 먼저).</summary>
        public void Tick(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right, int cameraHand, bool usable, Func<int, bool> boneHand)
        {
            if (!Enabled || !usable || _scene?.Scene == null) { Drop(true); Draw(); return; }
            if (_held != null && _held.T == null) Drop(false);   // 다시 불러옴
            if (_held == null)
            {
                for (int h = 0; h < 2; h++)
                {
                    var d = h == 0 ? left : right;
                    if (_hover[h] == null || h == cameraHand || boneHand(h) || d == null || !input.GrabDown[h]) continue;
                    Hold(h, _hover[h], rig.TransformPoint(d.Position), rig.rotation * d.Rotation);
                    input.Grab[h] = input.GrabDown[h] = false;
                    break;
                }
            }
            else
            {
                var d = _hand == 0 ? left : right;
                if (d == null || !d.Tracked || !input.Grab[_hand]) { Release(); }
                else
                {
                    var hp = rig.TransformPoint(d.Position); var hr = rig.rotation * d.Rotation;
                    var t = _held.T;
                    var pos = hp + hr * _offPos; var rot = hr * _offRot;
                    if (_held.PropId != null) rot = Quaternion.Euler(0, rot.eulerAngles.y, 0);   // 소품 값은 Y 회전뿐
                    if (Snap) Snapped(t.parent, ref pos, ref rot);
                    if ((t.position - pos).sqrMagnitude > 1e-10f || Quaternion.Angle(t.rotation, rot) > 0.01f) { t.SetPositionAndRotation(pos, rot); _changed = true; }
                    Scale(rig, input, left, right);
                    if (_held.PropId == null) Choose(input);
                    input.Grab[_hand] = input.GrabDown[_hand] = false;
                }
            }
            Draw();
            if (Controllers != null) for (int h = 0; h < 2; h++) if (Controllers.Hot[h] == 0) Controllers.Hot[h] = h == _hand || h == _scaleHand ? 2 : _hover[h] != null ? 1 : 0;
        }

        void Gather()
        {
            _items.Clear();
            Studio?.PropTargets((id, t, b) => _items.Add(new Item { Name = "소품 " + id, PropId = id, T = t, B = b }));
            var outfits = _scene.Scene?.Outfits;
            if (outfits != null) foreach (var o in outfits)
            {
                var t = _scene.NodeTransform(o.Node);
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                bool any = false, ok = true; var b = new Bounds(t.position, Vector3.zero);
                foreach (var ren in _scene.Renderers)
                {
                    if (ren == null || !(ren.transform == t || ren.transform.IsChildOf(t))) continue;
                    if (ren is SkinnedMeshRenderer smr)
                        foreach (var bone in smr.bones) if (bone != null && !(bone == t || bone.IsChildOf(t))) { ok = false; break; }
                    if (!ok) break;
                    if (!ren.enabled || !ren.gameObject.activeInHierarchy) continue;
                    if (!any) { b = ren.bounds; any = true; } else b.Encapsulate(ren.bounds);
                }
                if (ok && any) _items.Add(new Item { Node = o.Node, Name = o.Name.Length > 0 ? o.Name : t.name, T = t, B = b });
            }
            if (_held != null) foreach (var it in _items) if (it.T == _held.T) { _held.B = it.B; break; }
        }

        /// <summary>부모 기준 1 cm · 15° 단위로 (부모가 없으면 월드 기준).</summary>
        static void Snapped(Transform parent, ref Vector3 pos, ref Quaternion rot)
        {
            float step = SnapMove / Mathf.Max(1e-5f, parent != null ? parent.lossyScale.x : 1);
            var lp = parent != null ? parent.InverseTransformPoint(pos) : pos;
            lp = new Vector3(Mathf.Round(lp.x / step) * step, Mathf.Round(lp.y / step) * step, Mathf.Round(lp.z / step) * step);
            pos = parent != null ? parent.TransformPoint(lp) : lp;
            var pr = parent != null ? parent.rotation : Quaternion.identity;
            var e = (Quaternion.Inverse(pr) * rot).eulerAngles;
            rot = pr * Quaternion.Euler(Mathf.Round(e.x / SnapAngle) * SnapAngle, Mathf.Round(e.y / SnapAngle) * SnapAngle, Mathf.Round(e.z / SnapAngle) * SnapAngle);
        }

        /// <summary>표면에 붙이기: 아이템 가운데에서 뼈 쪽으로 광선을 쏴 가까운 메시 면에 가장 낮은 정점이 닿게 민다. 아이템 자신은 건너뛴다.</summary>
        void Stick(Item it, Transform bone)
        {
            var t = it.T; var c = it.B.center;
            var toBone = bone.position - c;
            if (toBone.sqrMagnitude < 1e-6f) return;
            var ray = new Ray(c - toBone.normalized * SurfaceBack, toBone.normalized);
            float best = SurfaceBack + SurfaceReach; Vector3 n = Vector3.zero;
            var baked = new Mesh();
            var own = new List<Vector3>();
            try
            {
                foreach (var ren in _scene.Renderers)
                {
                    if (ren == null || !ren.enabled || !ren.gameObject.activeInHierarchy) continue;
                    bool mine = ren.transform == t || ren.transform.IsChildOf(t);
                    if (!mine && !ren.bounds.IntersectRay(ray)) continue;
                    if (!MeshPick.WorldTriangles(ren, baked, out var v, out var tri)) continue;
                    if (mine) { int step = Math.Max(1, v.Length / 4000); for (int i = 0; i < v.Length; i += step) own.Add(v[i]); continue; }
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                    {
                        Vector3 a = v[tri[i]], b = v[tri[i + 1]], d = v[tri[i + 2]];
                        if (!MeshPick.RayTri(ray, a, b, d, out float dist) || dist >= best) continue;
                        best = dist;
                        n = Vector3.Cross(b - a, d - a).normalized;
                    }
                }
            }
            finally { UnityEngine.Object.Destroy(baked); }
            if (n == Vector3.zero || own.Count == 0) return;
            if (Vector3.Dot(n, ray.direction) > 0) n = -n;   // 아이템 쪽을 향하게
            var hit = ray.origin + ray.direction * best;
            float lowest = float.MaxValue;
            foreach (var p in own) lowest = Mathf.Min(lowest, Vector3.Dot(p - hit, n));
            t.position -= n * lowest;
            Debug.Log($"[VrcProto] VR 아이템 표면에 붙이기: {it.Name} ({-lowest * 100:0.0} cm)");
        }

        /// <summary>왼쪽·오른쪽을 바꾼 이름 (짝이 될 표시가 없으면 null).</summary>
        static string MirrorName(string name)
        {
            foreach (var (a, b) in new[] { ("左", "右"), ("왼쪽", "오른쪽") })
            {
                if (name.Contains(a)) return name.Replace(a, b);
                if (name.Contains(b)) return name.Replace(b, a);
            }
            foreach (var (a, b) in new[] { ("Left", "Right"), ("left", "right"), ("LEFT", "RIGHT") })
            {
                if (name.Contains(a)) return name.Replace(a, b);
                if (name.Contains(b)) return name.Replace(b, a);
            }
            var m = Regex.Match(name, @"(?<![A-Za-z0-9])([LRlr])(?![A-Za-z0-9])");
            if (!m.Success) return null;
            var g = m.Groups[1];
            var other = g.Value == "L" ? "R" : g.Value == "R" ? "L" : g.Value == "l" ? "r" : "l";
            return name.Substring(0, g.Index) + other + name.Substring(g.Index + 1);
        }

        /// <summary>좌우 대칭: 이름이 짝인 아이템을 아바타 좌우 거울 자리로 옮긴다 (실행 취소는 따로 한 단계).</summary>
        void MovePartner(Item it)
        {
            var other = MirrorName(it.Name);
            var root = _scene.Root;
            if (other == null || root == null) return;
            foreach (var p in _items)
            {
                if (p.PropId != null || p.T == it.T || p.Name != other) continue;
                var lp = root.InverseTransformPoint(it.T.position); lp.x = -lp.x;
                var lr = Quaternion.Inverse(root.rotation) * it.T.rotation;
                p.T.SetPositionAndRotation(root.TransformPoint(lp), root.rotation * new Quaternion(lr.x, -lr.y, -lr.z, lr.w));
                p.T.localScale = it.T.localScale;
                SendMoved(p);
                Debug.Log($"[VrcProto] VR 아이템 대칭: {it.Name} → {p.Name}");
                return;
            }
        }

        void SendMoved(Item it)
        {
            Vector3 p = it.T.localPosition, s = it.T.localScale; var q = it.T.localRotation;
            Send(Msg.Make(Msg.NodeMoved).Set("node", it.Node).Set("trs", JNode.FloatArray(new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w, s.x, s.y, s.z })));
        }

        void Hold(int h, Item it, Vector3 hp, Quaternion hr)
        {
            _held = it; _hand = h; _scaleHand = -1; _changed = false; _pick = 0; _stickArmed = true;
            var t = it.T; var inv = Quaternion.Inverse(hr);
            _offPos = inv * (t.position - hp); _offRot = inv * t.rotation;
            _startPos = t.localPosition; _startRot = t.localRotation; _startScale = t.localScale;
            Debug.Log($"[VrcProto] VR 아이템 잡기: {it.Name} ({(h == 0 ? "왼손" : "오른손")})");
        }

        /// <summary>다른 손 그립 = 두 손 크기 (잡은 순간 손 사이 거리 기준).</summary>
        void Scale(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right)
        {
            int o = 1 - _hand;
            var od = o == 0 ? left : right; var hd = _hand == 0 ? left : right;
            if (od == null || !od.Tracked) { _scaleHand = -1; return; }
            float dist = Vector3.Distance(od.Position, hd.Position);
            if (_scaleHand < 0 && input.GrabDown[o]) { _scaleHand = o; _d0 = Mathf.Max(0.03f, dist); _startScale = _held.T.localScale; }
            if (_scaleHand < 0) return;
            if (!input.Grab[o]) { _scaleHand = -1; return; }
            float f = Mathf.Clamp(dist / _d0, MinScale, MaxScale);
            var s = _startScale * f;
            if ((_held.T.localScale - s).sqrMagnitude > 1e-12f)
            {
                // 크기를 바꿔도 손에 잡은 점은 그대로 (손 기준 오프셋도 같은 배율)
                float r = s.x / Mathf.Max(1e-6f, _held.T.localScale.x);
                _offPos *= r;
                _held.T.localScale = s; _changed = true;
            }
            input.Grab[o] = input.GrabDown[o] = false;
        }

        /// <summary>놓을 뼈 후보: 아이템 가운데에서 가까운 휴머노이드 뼈 5개. 잡은 손 스틱 좌우로 넘긴다.</summary>
        void Choose(VrInput input)
        {
            var map = _scene.Scene?.Humanoid;
            _cand.Clear();
            if (map == null) return;
            var c = _held.B.center;
            foreach (var kv in map)
            {
                var bt = _scene.NodeTransform(kv.Value);
                if (bt == null || kv.Key.Contains("Eye") || kv.Key == "Jaw") continue;
                _cand.Add((kv.Key, bt));
            }
            _cand.Sort((a, b) => (a.T.position - c).sqrMagnitude.CompareTo((b.T.position - c).sqrMagnitude));
            if (_cand.Count > Candidates) _cand.RemoveRange(Candidates, _cand.Count - Candidates);
            float x = _hand == 0 ? input.Move.x : input.Turn.x;
            if (_stickArmed && Mathf.Abs(x) > 0.7f) { _pick = (_pick + (x > 0 ? 1 : -1) + Candidates) % Math.Max(1, _cand.Count); _stickArmed = false; }
            else if (Mathf.Abs(x) < 0.3f) _stickArmed = true;
            if (_hand == 0) input.Move = Vector2.zero; else input.Turn = Vector2.zero;   // 이동·회전이 받지 않게
            if (_pick >= _cand.Count) _pick = 0;
        }

        /// <summary>지금 붙은 휴머노이드 뼈 (아이템 루트의 부모에서 위로).</summary>
        string Attached(Transform t)
        {
            var map = _scene.Scene?.Humanoid;
            if (map == null) return null;
            for (var x = t.parent; x != null; x = x.parent)
                foreach (var kv in map) if (_scene.NodeTransform(kv.Value) == x) return kv.Key;
            return null;
        }

        void Release()
        {
            var it = _held; var t = it.T;
            if (it.PropId != null)
            {
                if (_changed)
                {
                    float y = Mathf.DeltaAngle(0, t.eulerAngles.y);
                    Send(Msg.Make(Msg.VrPropMoved).Set("id", it.PropId).Set("pos", JNode.FloatArray(new[] { t.position.x, t.position.y, t.position.z })).Set("rotY", y).Set("scale", t.localScale.x));
                    Debug.Log($"[VrcProto] VR 소품 놓기: {it.PropId}");
                }
                Drop(false);
                return;
            }
            if (_changed && _cand.Count > 0)
            {
                var (bone, bt) = _cand[Mathf.Clamp(_pick, 0, _cand.Count - 1)];
                if (Surface) Stick(it, bt);
                if (bone == Attached(t))
                {
                    SendMoved(it);
                    Debug.Log($"[VrcProto] VR 아이템 놓기: {it.Name} 옮기기 ({bone} 그대로)");
                    if (Mirror) MovePartner(it);
                }
                else
                {
                    var lp = bt.InverseTransformPoint(t.position);
                    var lr = Quaternion.Inverse(bt.rotation) * t.rotation;
                    var bs = bt.lossyScale; var isc = t.lossyScale;
                    var ls = new Vector3(isc.x / Nz(bs.x), isc.y / Nz(bs.y), isc.z / Nz(bs.z));
                    Send(Msg.Make(Msg.VrItemPlaced).Set("node", it.Node).Set("bone", bone)
                        .Set("offset", JNode.FloatArray(new[] { lp.x, lp.y, lp.z, lr.x, lr.y, lr.z, lr.w, ls.x, ls.y, ls.z })));
                    Debug.Log($"[VrcProto] VR 아이템 놓기: {it.Name} → {bone}");
                }
            }
            Drop(false);
        }

        static float Nz(float v) => Mathf.Abs(v) < 1e-8f ? 1 : v;

        /// <summary>놓기 없이 그만 (cancel = 처음 자리로).</summary>
        void Drop(bool cancel)
        {
            if (cancel && _held?.T != null && _changed) { _held.T.localPosition = _startPos; _held.T.localRotation = _startRot; _held.T.localScale = _startScale; }
            _held = null; _hand = _scaleHand = -1; _changed = false; _cand.Clear();
        }

        public void End() { Drop(true); _hover[0] = _hover[1] = null; if (_root != null) _root.SetActive(false); }

        // ---- 그리기: 범위 상자 · 놓을 뼈 선 (눈 전용, 깊이 무시) ----
        void Draw()
        {
            _lv.Clear(); _lc.Clear(); _li.Clear();
            var hoverCol = new Color(1f, 0.9f, 0.2f, 0.9f); var heldCol = new Color(1f, 0.55f, 0.15f, 0.95f);
            if (_held != null) Box(_held.B, heldCol);
            for (int h = 0; h < 2; h++) if (_hover[h] != null && _hover[h] != _held && (h == 0 || _hover[1] != _hover[0])) Box(_hover[h], hoverCol);
            if (_held != null && _cand.Count > 0)
            {
                var c = _held.B.center;
                for (int i = 0; i < _cand.Count; i++)
                {
                    bool on = i == _pick;
                    Line(c, _cand[i].T.position, on ? new Color(0.3f, 1f, 0.5f, 0.95f) : new Color(0.3f, 1f, 0.5f, 0.15f));
                    if (on) Cross(_cand[i].T.position, 0.02f, new Color(0.3f, 1f, 0.5f, 0.95f));
                }
            }
            if (_lv.Count == 0) { if (_root != null) _root.SetActive(false); return; }
            if (_root == null) Make();
            _root.SetActive(true);
            _lines.Clear(); _lines.SetVertices(_lv); _lines.SetColors(_lc); _lines.SetIndices(_li, MeshTopology.Lines, 0); _lines.RecalculateBounds();
        }

        void Box(Item it, Color c) => Box(it.B, c);
        void Box(Bounds b, Color c)
        {
            Vector3 a = b.min, z = b.max;
            Vector3 P(int i) => new Vector3((i & 1) == 0 ? a.x : z.x, (i & 2) == 0 ? a.y : z.y, (i & 4) == 0 ? a.z : z.z);
            for (int i = 0; i < 8; i++) for (int bit = 1; bit < 8; bit <<= 1) if ((i & bit) == 0) Line(P(i), P(i | bit), c);
        }
        void Line(Vector3 a, Vector3 b, Color c) { _li.Add(_lv.Count); _lv.Add(a); _lc.Add(c); _li.Add(_lv.Count); _lv.Add(b); _lc.Add(c); }
        void Cross(Vector3 p, float s, Color c) { Line(p - Vector3.right * s, p + Vector3.right * s, c); Line(p - Vector3.up * s, p + Vector3.up * s, c); Line(p - Vector3.forward * s, p + Vector3.forward * s, c); }

        void Make()
        {
            _root = new GameObject("VrItemGrab") { layer = VrLayers.VrOnly };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.AddComponent<MeshFilter>().sharedMesh = _lines = new Mesh { name = "VrItemGrabLines" };
            var r = _root.AddComponent<MeshRenderer>();
            _mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull", (int)CullMode.Off); _mat.SetInt("_ZWrite", 0); _mat.SetInt("_ZTest", (int)CompareFunction.Always);
            _mat.renderQueue = 3200;
            r.sharedMaterial = _mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        }
    }
}
