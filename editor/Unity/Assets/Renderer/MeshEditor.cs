// 뷰포트 정점 편집 (PLAN 3.9, 간단한 Blender식). Host가 meshTool { enabled, renderer }로 켠다.
//   클릭            정점 고르기 (Shift: 더하기/빼기) · 빈 곳 클릭: 선택 풀기
//   빈 곳에서 끌기  상자 선택 (Shift: 더하기, Ctrl: 빼기). 카메라를 향한 정점만
//   고른 정점 끌기  고른 정점을 화면과 나란한 면 위로 옮기기
//   브러시 (B)      끄는 동안 반지름 안의 정점을 표면 안쪽으로 누르기 (Shift: 당기기) · Ctrl+휠: 반지름 · Ctrl+Shift+휠: 세기
//   더블클릭        커서 아래 메시로 편집 대상 바꾸기
//   오른쪽 끌기·휠 버튼·휠은 평소처럼 카메라
// 선택은 여기서만 가지는 임시 상태다. 확정된 편집은 연산 하나로 Host에 보낸다(meshEdited) → Host가 기록하고 setMeshEdits로 돌려준다.
// 정점 위치는 메시 공간에 기록한다: 스킨드 메시는 현재 자세의 스킨 행렬(가중치 합)의 역으로 월드 이동을 바꾼다.
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class MeshEditor
    {
        readonly SceneBuilder _scene;
        public Action<string> Notice;
        public Action<JNode> Send;
        public bool Enabled { get; private set; }
        public bool Brush { get; private set; }

        string _rid, _meshId;
        Renderer _ren;
        MeshData _data;
        Vector3[] _world = new Vector3[0], _worldN = new Vector3[0];
        int[] _tris = new int[0];
        Mesh _baked, _points;
        readonly List<int> _sel = new List<int>();
        readonly HashSet<int> _selSet = new HashSet<int>();

        enum DragKind { None, Pending, Box, Move, Brush }
        DragKind _drag;
        Vector2 _press, _mouse;
        Camera _cam;
        int _pressHit = -1;
        float _lastDown = -10; Vector2 _lastDownPos;
        // 옮기기
        Vector3 _grabWorld; float _grabDepth; Vector3 _moveWorld;
        // 브러시: 정점 → 누적 월드 이동
        readonly Dictionary<int, Vector3> _stroke = new Dictionary<int, Vector3>();
        Vector3[] _strokeBase;
        float _radius = 0.02f, _strength = 1f;
        bool _hasHover; Vector3 _hoverPos, _hoverN;
        float[] _preview;

        const float DoubleClick = 0.35f;
        static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        public MeshEditor(SceneBuilder scene) { _scene = scene; }

        public bool HasTarget => _ren != null && _data != null;
        public bool IsDragging => _drag != DragKind.None;

        public void Clear()
        {
            _drag = DragKind.None; _stroke.Clear();
            _rid = null; _meshId = null; _ren = null; _data = null;
            _sel.Clear(); _selSet.Clear();
        }

        /// <summary>meshTool 메시지.</summary>
        public void Handle(JNode m)
        {
            if (m.Has("enabled"))
            {
                bool on = m["enabled"].AsBool;
                if (on != Enabled)
                {
                    Enabled = on;
                    if (!on) { CancelDrag(); Brush = false; }
                    else if (_ren == null) Notice?.Invoke("메시 편집: 편집할 메시를 더블클릭해 고르세요 · 클릭·끌기로 정점 고르기 · X 삭제 · M 합치기 · F 면 만들기 · N 노멀 다시 계산 · B 누르기 브러시 · E 끝내기");
                }
            }
            if (m.Has("renderer") && m["renderer"].AsStringOrNull is string rid && rid != _rid) SetTarget(rid);
            if (m.Has("brush")) { var b = m["brush"]; if (b.Has("radius")) _radius = Mathf.Clamp(b["radius"].AsFloat, 0.001f, 0.5f); if (b.Has("strength")) _strength = Mathf.Clamp(b["strength"].AsFloat, 0.1f, 10); if (b.Has("on")) Brush = b["on"].AsBool; }
            if (!Enabled) return;
            switch (m["action"].AsString)
            {
                case "delete": DoDelete(); break;
                case "merge": DoMerge(); break;
                case "face": DoFace(); break;
                case "normals": DoNormals(); break;
                case "selectAll": if (!HasTarget) break; if (_sel.Count == _data.VertexCount) ClearSel(); else { ClearSel(); for (int i = 0; i < _data.VertexCount; i++) AddSel(i); } Report(); break;
                case "selectNone": ClearSel(); Report(); break;
                case "selectLinked": SelectLinked(); break;
                case "toggleBrush": Brush = !Brush; Notice?.Invoke(Brush ? $"누르기 브러시: 끌면 표면을 안쪽으로 눌러요 (Shift: 당기기) · 반지름 {_radius * 100:0.#} cm (Ctrl+휠) · 세기 {_strength:0.#} (Ctrl+Shift+휠) · B: 끄기" : "누르기 브러시를 껐어요"); break;
            }
        }

        void SetTarget(string rid)
        {
            CancelDrag();
            var ren = _scene.RendererOf(rid);
            var mid = _scene.MeshIdOf(rid);
            _sel.Clear(); _selSet.Clear();
            if (ren == null || mid == null) { _rid = null; _ren = null; _data = null; _meshId = null; return; }
            _rid = rid; _ren = ren; _meshId = mid;
            _data = _scene.CurrentData(mid);
            Bake();
            Report();
            Notice?.Invoke($"메시 편집: {ren.name} (정점 {_data?.VertexCount ?? 0:N0}개)");
        }

        /// <summary>setMeshEdits 뒤: 정점 수가 바뀌었으면 선택을 푼다 (번호가 달라짐).</summary>
        public void OnMeshChanged(string meshId)
        {
            if (meshId != _meshId) return;
            int before = _data?.VertexCount ?? -1;
            _data = _scene.CurrentData(meshId);
            if (_data == null || _data.VertexCount != before) ClearSel();
            else { _sel.RemoveAll(i => i >= _data.VertexCount); _selSet.RemoveWhere(i => i >= _data.VertexCount); }
            _preview = null;
            Bake();
            Report();
        }

        // ---------------- 월드 정점 ----------------
        void Bake()
        {
            if (_ren == null || _data == null) return;
            int n = _data.VertexCount;
            if (_ren is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                _baked ??= new Mesh { hideFlags = HideFlags.HideAndDontSave };
                smr.BakeMesh(_baked, true);
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                var v = _baked.vertices; var nn = _baked.normals;
                if (v.Length != n) return;
                if (_world.Length != n) { _world = new Vector3[n]; _worldN = new Vector3[n]; }
                for (int i = 0; i < n; i++) { _world[i] = m.MultiplyPoint3x4(v[i]); _worldN[i] = nn.Length == n ? m.MultiplyVector(nn[i]) : Vector3.up; }
                _tris = smr.sharedMesh.triangles;
            }
            else if (_ren.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh)
            {
                var m = _ren.transform.localToWorldMatrix;
                var v = mesh.vertices; var nn = mesh.normals;
                if (v.Length != n) return;
                if (_world.Length != n) { _world = new Vector3[n]; _worldN = new Vector3[n]; }
                for (int i = 0; i < n; i++) { _world[i] = m.MultiplyPoint3x4(v[i]); _worldN[i] = nn.Length == n ? m.MultiplyVector(nn[i]).normalized : Vector3.up; }
                _tris = mesh.triangles;
            }
        }

        /// <summary>정점 i의 메시 공간 → 월드 행렬 (스킨드: 현재 자세의 가중치 합).</summary>
        Matrix4x4 Skin(int i)
        {
            if (_ren is SkinnedMeshRenderer smr && _data.BoneW != null && _data.BoneIdx != null && smr.bones.Length > 0 && smr.sharedMesh != null)
            {
                var bp = smr.sharedMesh.bindposes; var bones = smr.bones;
                var sum = new Matrix4x4(); float total = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = _data.BoneW[i * 4 + k]; int b = _data.BoneIdx[i * 4 + k];
                    if (w <= 0 || b < 0 || b >= bones.Length || b >= bp.Length || bones[b] == null) continue;
                    var mm = bones[b].localToWorldMatrix * bp[b];
                    for (int e = 0; e < 16; e++) sum[e] += mm[e] * w;
                    total += w;
                }
                if (total > 0) return sum;
                return smr.transform.localToWorldMatrix;
            }
            return _ren.transform.localToWorldMatrix;
        }

        Vector3 ToMesh(int i, Vector3 worldDelta) => Skin(i).inverse.MultiplyVector(worldDelta);

        bool Facing(Camera cam, int i)
        {
            var to = cam.orthographic ? -cam.transform.forward : cam.transform.position - _world[i];
            return Vector3.Dot(_worldN[i], to) > 0;
        }

        // ---------------- 입력 ----------------
        /// <summary>카메라 대신 여기서 쓸 입력: 왼쪽 버튼(고르기·끌기·브러시), 브러시 중 Ctrl+휠.</summary>
        public bool WantsInput() => Enabled && (_drag != DragKind.None || Input.GetMouseButtonDown(0) || Ctrl && Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f);

        public void Update(Camera cam, Vector2 mouse, float px)
        {
            if (!Enabled) return;
            _mouse = mouse; _cam = cam;
            if (_drag == DragKind.None || _drag == DragKind.Pending) Bake();   // 자세·블렌드쉐이프가 바뀌어도 점이 따라가게
            float wheel = Input.mouseScrollDelta.y;
            if (Ctrl && Mathf.Abs(wheel) > 0.01f)
            {
                if (Shift) _strength = Mathf.Clamp(_strength * (wheel > 0 ? 1.25f : 0.8f), 0.1f, 10);
                else _radius = Mathf.Clamp(_radius * (wheel > 0 ? 1.15f : 0.87f), 0.001f, 0.5f);
                Notice?.Invoke($"브러시 반지름 {_radius * 100:0.#} cm · 세기 {_strength:0.#}");
            }
            if (Brush && HasTarget && _drag != DragKind.Brush) _hasHover = Raycast(cam.ScreenPointToRay(mouse), out _hoverPos, out _hoverN, out _);

            switch (_drag)
            {
                case DragKind.None:
                    if (!Input.GetMouseButtonDown(0)) return;
                    bool dbl = Time.unscaledTime - _lastDown < DoubleClick && (mouse - _lastDownPos).sqrMagnitude < px * px;
                    _lastDown = dbl ? -10 : Time.unscaledTime; _lastDownPos = mouse;
                    if (dbl || !HasTarget) { PickRenderer(cam, mouse); return; }
                    _press = mouse;
                    if (Brush) { BeginBrush(cam, mouse); return; }
                    _pressHit = PickVertex(cam, mouse, px);
                    _drag = DragKind.Pending;
                    return;
                case DragKind.Pending:
                    if (!Input.GetMouseButton(0))
                    {
                        // 클릭: 고르기
                        if (!Shift) ClearSel();
                        if (_pressHit >= 0) { if (Shift && _selSet.Contains(_pressHit)) RemoveSel(_pressHit); else AddSel(_pressHit); }
                        _drag = DragKind.None; Report(); return;
                    }
                    if ((mouse - _press).sqrMagnitude < px * px * 0.25f) return;
                    if (_pressHit >= 0 && (_selSet.Contains(_pressHit) || !Shift && !Ctrl))
                    {
                        if (!_selSet.Contains(_pressHit)) { ClearSel(); AddSel(_pressHit); Report(); }
                        _grabWorld = _world[_pressHit];
                        _grabDepth = cam.WorldToScreenPoint(_grabWorld).z;
                        _moveWorld = Vector3.zero;
                        _drag = DragKind.Move;
                    }
                    else _drag = DragKind.Box;
                    return;
                case DragKind.Box:
                    if (Input.GetMouseButton(0)) return;
                    BoxSelect(cam, _press, mouse);
                    _drag = DragKind.None; Report(); return;
                case DragKind.Move:
                    if (Input.GetMouseButton(0))
                    {
                        var p = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, _grabDepth));
                        _moveWorld = p - _grabWorld;
                        PreviewMove();
                        return;
                    }
                    FinishMove(); return;
                case DragKind.Brush:
                    if (Input.GetMouseButton(0)) { StrokeStep(cam, mouse); return; }
                    FinishBrush(); return;
            }
        }

        void CancelDrag()
        {
            if (_drag == DragKind.Move || _drag == DragKind.Brush) { if (_meshId != null) _scene.PreviewPositions(_meshId, null); }
            _drag = DragKind.None; _stroke.Clear(); _preview = null;
        }

        void PickRenderer(Camera cam, Vector2 mouse)
        {
            var hit = MeshPick.RendererAt(_scene.Renderers, cam.ScreenPointToRay(mouse));
            if (hit == null) { Notice?.Invoke("커서 아래에 메시가 없어요. 편집할 메시를 더블클릭하세요"); return; }
            SetTarget(_scene.RendererId(hit));
        }

        bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out int nearest)
        {
            point = normal = Vector3.zero; nearest = -1;
            float best = float.MaxValue;
            for (int i = 0; i + 2 < _tris.Length; i += 3)
            {
                int a = _tris[i], b = _tris[i + 1], c = _tris[i + 2];
                if (a >= _world.Length || b >= _world.Length || c >= _world.Length) continue;
                if (!MeshPick.RayTri(ray, _world[a], _world[b], _world[c], out float d) || d >= best) continue;
                best = d;
                point = ray.origin + ray.direction * d;
                normal = Vector3.Cross(_world[b] - _world[a], _world[c] - _world[a]).normalized;
                if (Vector3.Dot(normal, ray.direction) > 0) normal = -normal;
                float da = (_world[a] - point).sqrMagnitude, db = (_world[b] - point).sqrMagnitude, dc = (_world[c] - point).sqrMagnitude;
                nearest = da <= db && da <= dc ? a : db <= dc ? b : c;
            }
            return best < float.MaxValue;
        }

        /// <summary>커서 아래 정점: 맞은 면의 가장 가까운 정점이 커서 근처면 그것, 아니면 카메라를 향한 정점 중 화면에서 가장 가까운 것.</summary>
        int PickVertex(Camera cam, Vector2 mouse, float px)
        {
            if (!HasTarget) return -1;
            float lim = px * px;
            if (Raycast(cam.ScreenPointToRay(mouse), out _, out _, out var near) && near >= 0)
            {
                var s = cam.WorldToScreenPoint(_world[near]);
                if (((Vector2)s - mouse).sqrMagnitude <= lim * 4) return near;
            }
            int best = -1; float bd = lim;
            for (int i = 0; i < _world.Length; i++)
            {
                var s = cam.WorldToScreenPoint(_world[i]);
                if (s.z <= 0) continue;
                float d = ((Vector2)s - mouse).sqrMagnitude;
                if (d < bd && Facing(cam, i)) { bd = d; best = i; }
            }
            return best;
        }

        void BoxSelect(Camera cam, Vector2 a, Vector2 b)
        {
            if (!HasTarget) return;
            var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            bool sub = Ctrl, add = Shift;
            if (!sub && !add) ClearSel();
            for (int i = 0; i < _world.Length; i++)
            {
                var s = cam.WorldToScreenPoint(_world[i]);
                if (s.z <= 0 || !r.Contains(s) || !Facing(cam, i)) continue;
                if (sub) RemoveSel(i); else AddSel(i);
            }
        }

        void SelectLinked()
        {
            if (!HasTarget || _sel.Count == 0) return;
            var adj = new List<int>[_data.VertexCount];
            foreach (var s in _data.Subs)
            {
                if (s.Topology != "Triangles") continue;
                var t = s.Indices;
                for (int i = 0; i + 2 < t.Length; i += 3)
                    for (int k = 0; k < 3; k++) (adj[t[i + k]] ??= new List<int>()).Add(t[i + (k + 1) % 3]);
            }
            var stack = new Stack<int>(_sel);
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                if (adj[v] == null) continue;
                foreach (var w in adj[v]) if (!_selSet.Contains(w)) { AddSel(w); stack.Push(w); }
            }
            Report();
        }

        void AddSel(int i) { if (_selSet.Add(i)) _sel.Add(i); }
        void RemoveSel(int i) { if (_selSet.Remove(i)) _sel.Remove(i); }
        void ClearSel() { _sel.Clear(); _selSet.Clear(); }

        void Report()
        {
            Send?.Invoke(Msg.Make(Msg.MeshSelection).Set("renderer", _rid).Set("mesh", _meshId).Set("count", _sel.Count)
                .Set("vertices", _data?.VertexCount ?? 0).Set("triangles", _data?.Triangles ?? 0).Set("brush", Brush));
        }

        // ---------------- 연산 ----------------
        static JNode SelJson(IEnumerable<int> sel) { var a = JNode.Arr(); foreach (var i in sel) a.Add(i); return a; }

        void Commit(JNode op, string label)
        {
            if (_meshId == null) return;
            Send?.Invoke(Msg.Make(Msg.MeshEdited).Set("mesh", _meshId).Set("op", op).Set("label", label));
        }

        void DoDelete()
        {
            if (!HasTarget || _sel.Count == 0) { Notice?.Invoke("지울 정점을 먼저 고르세요"); return; }
            Commit(JNode.Obj().Set("op", MeshOps.Delete).Set("sel", SelJson(_sel)), $"정점 {_sel.Count}개 삭제");
        }

        void DoMerge()
        {
            if (!HasTarget || _sel.Count < 2) { Notice?.Invoke("합칠 정점을 2개 이상 고르세요"); return; }
            Commit(JNode.Obj().Set("op", MeshOps.Merge).Set("sel", SelJson(_sel)).Set("at", "center"), $"정점 {_sel.Count}개 합치기");
        }

        /// 고른 정점의 노멀 다시 계산 (고른 것이 없으면 메시 전체)
        void DoNormals()
        {
            if (!HasTarget) { Notice?.Invoke("노멀을 다시 계산할 메시를 더블클릭해 고르세요"); return; }
            var op = JNode.Obj().Set("op", MeshOps.Normals);
            if (_sel.Count > 0) op.Set("sel", SelJson(_sel));
            Commit(op, _sel.Count > 0 ? $"노멀 다시 계산 (정점 {_sel.Count}개)" : "노멀 다시 계산 (메시 전체)");
        }

        /// 고른 순서대로 부채꼴 면을 잇고, 면이 카메라를 향하게 감김 방향을 맞춘다 (Unity: 시계 방향 = 앞면)
        void DoFace()
        {
            if (!HasTarget || _sel.Count < 3) { Notice?.Invoke("면을 만들 정점을 3개 이상 차례로 고르세요"); return; }
            var order = new List<int>(_sel);
            var a = _world[order[0]]; var n = Vector3.Cross(_world[order[1]] - a, _world[order[2]] - a);
            var to = _cam != null ? (_cam.orthographic ? -_cam.transform.forward : _cam.transform.position - a) : Vector3.back;
            if (Vector3.Dot(n, to) < 0) order.Reverse(1, order.Count - 1);
            int sub = SubmeshOf(order[0]);
            Commit(JNode.Obj().Set("op", MeshOps.Face).Set("sel", SelJson(order)).Set("submesh", sub), $"면 만들기 (정점 {order.Count}개)");
        }

        /// 정점을 쓰는 첫 서브메시 (새 면이 같은 머티리얼을 쓰게)
        int SubmeshOf(int v)
        {
            for (int s = 0; s < _data.Subs.Count; s++)
                if (_data.Subs[s].Topology == "Triangles" && Array.IndexOf(_data.Subs[s].Indices, v) >= 0) return s;
            return 0;
        }

        void PreviewMove()
        {
            _preview ??= new float[_data.Pos.Length];
            Array.Copy(_data.Pos, _preview, _preview.Length);
            foreach (var i in _sel)
            {
                var d = ToMesh(i, _moveWorld);
                _preview[i * 3] += d.x; _preview[i * 3 + 1] += d.y; _preview[i * 3 + 2] += d.z;
            }
            _scene.PreviewPositions(_meshId, _preview);
        }

        void FinishMove()
        {
            _drag = DragKind.None; _preview = null;
            if (_moveWorld.sqrMagnitude < 1e-12f) { _scene.PreviewPositions(_meshId, null); return; }
            var delta = new float[_sel.Count * 3];
            for (int k = 0; k < _sel.Count; k++) { var d = ToMesh(_sel[k], _moveWorld); delta[k * 3] = d.x; delta[k * 3 + 1] = d.y; delta[k * 3 + 2] = d.z; }
            Commit(JNode.Obj().Set("op", MeshOps.Move).Set("sel", SelJson(_sel)).Set("delta", JNode.FloatArray(delta)), $"정점 {_sel.Count}개 옮기기 ({_moveWorld.magnitude * 1000:0.#} mm)");
        }

        void BeginBrush(Camera cam, Vector2 mouse)
        {
            _stroke.Clear();
            _strokeBase = (Vector3[])_world.Clone();
            _drag = DragKind.Brush;
            StrokeStep(cam, mouse);
        }

        /// 한 프레임 누르기: 커서 아래 면의 법선 반대 방향으로, 가운데가 세고 가장자리로 갈수록 약하게
        void StrokeStep(Camera cam, Vector2 mouse)
        {
            if (!Raycast(cam.ScreenPointToRay(mouse), out var hit, out var n, out _)) { _hasHover = false; return; }
            _hasHover = true; _hoverPos = hit; _hoverN = n;
            float step = 0.0004f * _strength * (Shift ? 1 : -1);
            float r2 = _radius * _radius;
            for (int i = 0; i < _world.Length; i++)
            {
                float d2 = (_world[i] - hit).sqrMagnitude;
                if (d2 > r2) continue;
                float t = 1 - d2 / r2; float f = t * t;
                var dv = n * (step * f);
                _stroke[i] = (_stroke.TryGetValue(i, out var acc) ? acc : Vector3.zero) + dv;
                _world[i] += dv;   // 다음 프레임의 광선·거리 계산이 눌린 표면을 따라가게
            }
            _preview ??= new float[_data.Pos.Length];
            Array.Copy(_data.Pos, _preview, _preview.Length);
            foreach (var kv in _stroke)
            {
                var d = ToMesh(kv.Key, kv.Value);
                _preview[kv.Key * 3] += d.x; _preview[kv.Key * 3 + 1] += d.y; _preview[kv.Key * 3 + 2] += d.z;
            }
            _scene.PreviewPositions(_meshId, _preview);
        }

        void FinishBrush()
        {
            _drag = DragKind.None; _preview = null;
            if (_stroke.Count == 0) return;
            var sel = new List<int>(_stroke.Keys);
            sel.Sort();
            var delta = new float[sel.Count * 3];
            for (int k = 0; k < sel.Count; k++) { var d = ToMesh(sel[k], _stroke[sel[k]]); delta[k * 3] = d.x; delta[k * 3 + 1] = d.y; delta[k * 3 + 2] = d.z; }
            _stroke.Clear();
            // 정점마다 델타 (정점이 하나면 델타 3개 = 같은 델타 형식과 같다)
            Commit(JNode.Obj().Set("op", MeshOps.Move).Set("sel", SelJson(sel)).Set("delta", JNode.FloatArray(delta)), Shift ? "브러시로 당기기" : "브러시로 누르기");
        }

        // ---------------- 그리기 (ViewAids가 카메라 렌더 뒤에 부름) ----------------
        Vector3[] _pv = new Vector3[0]; Color[] _pc = new Color[0]; int[] _pi = new int[0];
        public void Draw(Camera cam, Material depth, Material always)
        {
            if (!Enabled || !HasTarget || _world.Length == 0) return;
            var camPos = cam.transform.position;
            // 고르지 않은 정점: 1px 점 (가려진 것은 깊이로 숨김, 표면과 겹치지 않게 카메라 쪽으로 살짝)
            _points ??= new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            int n = _world.Length;
            if (_pv.Length != n) { _pv = new Vector3[n]; _pc = new Color[n]; _pi = new int[n]; for (int i = 0; i < n; i++) _pi[i] = i; }
            var pv = _pv; var pc = _pc;
            var white = new Color(0.1f, 0.1f, 0.12f, 0.9f); var orange = new Color(1f, 0.55f, 0.1f, 1f);
            for (int i = 0; i < n; i++)
            {
                var w = _world[i];
                pv[i] = w + (camPos - w).normalized * 0.0008f;
                pc[i] = _selSet.Contains(i) ? orange : white;
            }
            _points.Clear();
            _points.SetVertices(pv); _points.SetColors(pc); _points.SetIndices(_pi, MeshTopology.Points, 0, false);
            depth.SetPass(0);
            Graphics.DrawMeshNow(_points, Matrix4x4.identity);
            // 고른 정점: 작은 십자 (많으면 점만)
            always.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            if (_sel.Count <= 4000)
            {
                GL.Color(orange);
                foreach (var i in _sel)
                {
                    if (i >= n) continue;
                    var p = _world[i]; float s = 0.004f * Mathf.Max(0.3f, (p - camPos).magnitude);
                    var right = cam.transform.right * s; var up = cam.transform.up * s;
                    GL.Vertex(p - right); GL.Vertex(p + right); GL.Vertex(p - up); GL.Vertex(p + up);
                }
            }
            // 브러시 고리
            if (Brush && _hasHover)
            {
                GL.Color(Shift ? new Color(0.3f, 0.8f, 1f, 0.9f) : new Color(1f, 0.4f, 0.4f, 0.9f));
                var t1 = Vector3.Cross(_hoverN, Mathf.Abs(_hoverN.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var t2 = Vector3.Cross(_hoverN, t1);
                const int seg = 32;
                for (int k = 0; k < seg; k++)
                {
                    float a0 = k * Mathf.PI * 2 / seg, a1 = (k + 1) * Mathf.PI * 2 / seg;
                    GL.Vertex(_hoverPos + (t1 * Mathf.Cos(a0) + t2 * Mathf.Sin(a0)) * _radius);
                    GL.Vertex(_hoverPos + (t1 * Mathf.Cos(a1) + t2 * Mathf.Sin(a1)) * _radius);
                }
                GL.Vertex(_hoverPos); GL.Vertex(_hoverPos - _hoverN * _radius * 0.5f);
            }
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>상자 선택 사각형 (화면 좌표, ViewAids.OnGUI에서 픽셀 행렬로).</summary>
        public bool BoxRect(out Rect r)
        {
            r = default;
            if (_drag != DragKind.Box) return false;
            var a = new Vector2(_press.x, Screen.height - _press.y); var b = new Vector2(_mouse.x, Screen.height - _mouse.y);
            r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return true;
        }
    }
}
