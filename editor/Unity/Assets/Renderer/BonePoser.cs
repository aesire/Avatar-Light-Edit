// 뷰포트 본 조작 (PLAN 3.5 FK 기즈모, 사용자 추천 조작). Host가 setView.poseBones로 켠다.
//   관절 끌기        휴머노이드 관절을 왼쪽 버튼으로 끌면 그 관절이 마우스를 따라온다 (부모 본을 돌림. 손·발은 위팔·아래팔/허벅지·종아리 2본 IK)
//   더블클릭         관절을 고른다 (주황). 고른 뒤에는 빈 곳을 끌어도 고른 본이 끄는 쪽으로 돌아간다. 빈 곳 더블클릭으로 풀기
//   잡은 채 휠       잡은(또는 고른) 본을 화면 축으로 돌린다. 양쪽 버튼을 누른 채 휠 = 대각선 축, Shift+휠 = 본 방향(비틀기)
//   핸들             고른 본에 TransformGizmo를 그린다: 본 로컬(또는 월드) X(빨강)·Y(초록)·Z(파랑) 고리와 화면 축 고리(바깥 흰색),
//                    그리고 이동 화살표(Hips 말고). 고리를 끌면 그 축으로만 돌고(앞쪽 반만 잡힌다), 화살표를 끌면 관절이 그 축 위로만 옮겨진다
//                    (관절 끌기와 같다: 손·발은 2본 IK, 나머지는 부모 본을 돌림)
//   빈 곳 더블클릭   PickObject가 커서 아래 아이템·아바타를 고르면(ObjectMover) 본 선택은 풀린다
// 돌린 결과는 끄는 동안 매 프레임 머슬 값으로 읽어 다시 적용한다(HumanPoser.Reproject) → 화면 = 기록될 자세.
// 놓으면 머슬 값을 Host로 보낸다(poseEdited). 기록·실행 취소는 Host가 한다 (상태의 단일 원천).
using System;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class BonePoser
    {
        readonly HumanPoser _poser;
        /// <summary>놓았을 때 (본 이름, 자세). RenderServer가 poseEdited로 보낸다.</summary>
        public Action<string, JNode> Edited;
        public Action<string> Notice;
        /// <summary>본을 고르거나 풀었을 때 (휴머노이드 이름, 풀면 ""). RenderServer가 poseSelected로 보낸다.</summary>
        public Action<string> Selected;
        public bool Enabled;

        int _selected = -1, _held = -1;
        bool _jointDrag, _dragging, _changed, _moved;
        Vector2 _pressPos;
        float _depth;
        Vector2 _last;
        float _lastDown = -10;
        Vector2 _lastDownPos;
        readonly TransformGizmo _gizmo = new TransformGizmo();
        int _handle = -1;          // 끄는 중인 핸들 (TransformGizmo 번호), -1 = 핸들이 아님
        Vector3 _handleStart;      // 화살표: 잡을 때의 관절 위치
        /// <summary>핸들 축을 월드 기준으로 (false = 본 로컬, Unity의 Local).</summary>
        public bool World;
        /// <summary>빈 곳 더블클릭: 커서 아래 아이템·아바타를 고르면 true (그때는 본 선택을 푼다).</summary>
        public Func<Camera, Vector2, bool> PickObject;
        /// <summary>본을 골랐을 때 (다른 대상의 선택을 풀게).</summary>
        public Action BoneChosen;

        const float WheelStep = 7.5f;    // 휠 한 칸에 도는 각도
        const float DoubleClick = 0.35f;

        public BonePoser(HumanPoser poser) { _poser = poser; }

        public bool Active => Enabled && _poser.Ready;
        public bool IsDragging => _dragging;
        static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        /// <summary>장면을 다시 불러오면 고른 본이 없어진다.</summary>
        public void Clear() { if (_selected >= 0) Selected?.Invoke(""); _selected = -1; _held = -1; _dragging = false; _changed = false; _handle = -1; _gizmo.End(); }

        /// <summary>다른 대상을 골라 본 선택만 푼다 (안내 문구 없이).</summary>
        public void Deselect() { if (_dragging) return; if (_selected >= 0) Selected?.Invoke(""); _selected = -1; }

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            if (!on) { Finish(); if (_selected >= 0) Selected?.Invoke(""); _selected = -1; }
            Enabled = on;
            if (on && !_poser.Ready && _poser.Problem.Length > 0) Notice?.Invoke("이 아바타는 휴머노이드 매핑이 없어 본을 조작할 수 없어요.");
        }

        /// <summary>카메라 조작 대신 본 조작을 할 때 true (끄는 중, 관절 위에서 누름, 본을 고른 채 누름).</summary>
        public bool WantsInput(Camera cam, Vector2 mouse, float px) =>
            Active && (_dragging || Input.GetMouseButtonDown(0) && !Shift && (_selected >= 0 || Pick(cam, mouse, px) >= 0));

        public void Update(Camera cam, Vector2 mouse, float px)
        {
            if (!Active) return;
            if (_dragging)
            {
                if (!Input.GetMouseButton(0)) { Finish(); return; }
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f) Rotate(_held, WheelAxis(cam, _held), wheel * WheelStep);
                // 더블클릭의 첫 누름이 본을 조금 돌려 기록하지 않게, 몇 픽셀 움직인 뒤부터 끈다
                if (!_moved && (mouse - _pressPos).sqrMagnitude > px * px * 0.1f) { _moved = true; _last = _pressPos; }
                if (_moved && (mouse - _last).sqrMagnitude > 0.25f)
                {
                    if (TransformGizmo.IsRing(_handle)) Rotate(_held, _gizmo.ActiveAxis, _gizmo.Angle(_last, mouse));
                    else if (TransformGizmo.IsMove(_handle)) MoveJoint(_held, _handleStart + _gizmo.MoveFromStart(cam, mouse));
                    else if (_jointDrag) MoveJoint(_held, cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, _depth)));
                    else Aim(cam, _held, mouse);
                    _last = mouse;
                }
                return;
            }
            if (!Input.GetMouseButtonDown(0) || Shift) return;
            // 핸들이 먼저 (고리 안쪽의 다른 관절보다). 핸들을 두 번 눌러도 선택이 풀리지 않게 더블클릭으로 치지 않는다
            int handle = PickHandle(cam, mouse, px, out var handleHit);
            if (handle >= 0) { _lastDown = -10; BeginHandle(cam, handle, handleHit, mouse); return; }
            bool dbl = Time.unscaledTime - _lastDown < DoubleClick && (mouse - _lastDownPos).sqrMagnitude < px * px;
            _lastDown = dbl ? -10 : Time.unscaledTime; _lastDownPos = mouse;
            int hit = Pick(cam, mouse, px);
            if (dbl)
            {
                if (hit < 0 && PickObject != null && PickObject(cam, mouse)) { Deselect(); return; }
                Select(hit);
                return;
            }
            if (hit >= 0) Begin(cam, hit, _poser.BoneParent(hit) >= 0, mouse);  // Hips는 부모가 없어 돌리기
            else if (_selected >= 0) Begin(cam, _selected, false, mouse);
        }

        void Begin(Camera cam, int bone, bool jointDrag, Vector2 mouse)
        {
            _held = bone; _jointDrag = jointDrag; _dragging = true; _changed = false; _moved = false; _last = _pressPos = mouse; _handle = -1;
            _depth = cam.WorldToScreenPoint(_poser.BoneTransform(bone).position).z;
        }

        void Finish()
        {
            if (_dragging && _changed) Edited?.Invoke(_poser.BoneName(_held), _poser.CurrentPose());
            _dragging = false; _changed = false; _held = -1; _handle = -1; _gizmo.End();
        }

        // ---------------- 핸들 (TransformGizmo) ----------------
        /// <summary>고른 본 i에 핸들을 맞춘다: 중심 = 관절, 축 = 본 로컬(또는 월드), 화살표는 부모가 있는 본만.</summary>
        void PrepareGizmo(int i)
        {
            var t = _poser.BoneTransform(i);
            _gizmo.Center = t.position;
            _gizmo.Frame = World ? Quaternion.identity : t.rotation;
            _gizmo.ShowMove = _poser.BoneParent(i) >= 0;
        }

        /// <summary>축 k(0 X, 1 Y, 2 Z): 본 로컬 축 (월드 모드면 월드 축). 스크립트 조작용.</summary>
        Vector3 RingAxisOf(int i, int k)
        {
            var t = _poser.BoneTransform(i);
            if (World) return k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
            return k == 0 ? t.right : k == 1 ? t.up : t.forward;
        }

        /// <summary>마우스 아래의 고른 본 핸들. 없으면 -1.</summary>
        int PickHandle(Camera cam, Vector2 mouse, float px, out Vector3 hit)
        {
            hit = default;
            if (_selected < 0) return -1;
            PrepareGizmo(_selected);
            return _gizmo.Pick(cam, mouse, px, out hit);
        }

        void BeginHandle(Camera cam, int h, Vector3 hit, Vector2 mouse)
        {
            Begin(cam, _selected, false, mouse);
            _handle = h;
            _handleStart = _poser.BoneTransform(_selected).position;
            _gizmo.Begin(cam, h, hit, mouse);
            _moved = true;   // 핸들은 더블클릭과 섞이지 않으므로 누르자마자 끈다
            var label = HumanBones.Label(_poser.BoneName(_selected));
            int k = TransformGizmo.IsMove(h) ? h : h - TransformGizmo.RingX;
            Notice?.Invoke(TransformGizmo.IsMove(h) ? $"{label} · {TransformGizmo.Names[k]} 축으로 옮기기" : $"{label} · {TransformGizmo.Names[k]} 축으로 돌리기");
        }

        void Select(int bone)
        {
            _selected = bone;
            Selected?.Invoke(bone < 0 ? "" : _poser.BoneName(bone));
            if (bone < 0) { Notice?.Invoke("본 선택을 풀었어요."); return; }
            BoneChosen?.Invoke();
            Notice?.Invoke($"고른 본: {HumanBones.Label(_poser.BoneName(bone))} · 고리를 끌면 그 축으로만 돌고 화살표를 끌면 그 축으로 옮겨져요 (빨강 X · 초록 Y · 파랑 Z · 흰색 화면) · 빈 곳을 끌면 끄는 쪽으로 돌아가요 · 끄는 중 휠: 화면 축, 양쪽 버튼+휠: 대각선 축, Shift+휠: 비틀기 · 빈 곳 더블클릭: 선택 풀기");
        }

        // ---------------- 회전 ----------------
        Vector3 Tip(int i)
        {
            var t = _poser.BoneTransform(i);
            int c = _poser.BoneChild(i), p = _poser.BoneParent(i);
            if (c >= 0) return _poser.BoneTransform(c).position;
            if (p >= 0) return t.position + (t.position - _poser.BoneTransform(p).position) * 0.5f;
            return t.position + t.up * 0.1f;
        }

        Vector3 WheelAxis(Camera cam, int i)
        {
            if (Input.GetMouseButton(1)) return (cam.transform.right + cam.transform.up).normalized;   // 양쪽 버튼: 대각선 축
            if (Shift) { var d = Tip(i) - _poser.BoneTransform(i).position; if (d.sqrMagnitude > 1e-8f) return d.normalized; }
            return cam.transform.forward;
        }

        void Rotate(int i, Vector3 axis, float degrees)
        {
            var t = _poser.BoneTransform(i);
            t.rotation = Quaternion.AngleAxis(degrees, axis) * t.rotation;
            _poser.Reproject(IsHips(i));
            _changed = true;
        }

        /// <summary>본 끝(자식 관절)을 마우스가 움직인 만큼 옮기는 쪽으로 돌린다.</summary>
        void Aim(Camera cam, int i, Vector2 mouse)
        {
            var t = _poser.BoneTransform(i);
            var tip = Tip(i);
            float z = cam.WorldToScreenPoint(tip).z;
            var delta = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, z)) - cam.ScreenToWorldPoint(new Vector3(_last.x, _last.y, z));
            Swing(t, tip, tip + delta);
            _poser.Reproject(IsHips(i));
            _changed = true;
        }

        bool IsHips(int i) => _poser.BoneParent(i) < 0;

        static void Swing(Transform t, Vector3 from, Vector3 to)
        {
            Vector3 a = from - t.position, b = to - t.position;
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return;
            t.rotation = Quaternion.FromToRotation(a, b) * t.rotation;
        }

        /// <summary>관절 i를 target 쪽으로: 손·발은 2본 IK, 나머지는 부모 본을 돌린다.</summary>
        void MoveJoint(int i, Vector3 target)
        {
            int p = _poser.BoneParent(i);
            if (p < 0) return;
            int g = _poser.BoneParent(p);
            var name = _poser.BoneName(i);
            if (g >= 0 && (name.EndsWith("Hand") || name.EndsWith("Foot"))) TwoBoneIk(_poser.BoneTransform(g), _poser.BoneTransform(p), _poser.BoneTransform(i), target);
            else Swing(_poser.BoneTransform(p), _poser.BoneTransform(i).position, target);
            _poser.Reproject(IsHips(p));
            _changed = true;
        }

        static void TwoBoneIk(Transform upper, Transform lower, Transform end, Vector3 target)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float la = (b - a).magnitude, lb = (c - b).magnitude;
            if (la < 1e-5f || lb < 1e-5f) return;
            var toT = target - a;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
            var dir = toT.sqrMagnitude > 1e-10f ? toT.normalized : (c - a).normalized;
            // 지금 굽은 쪽을 유지한다 (팔꿈치·무릎이 뒤집히지 않게)
            var bend = Vector3.ProjectOnPlane(b - a, dir);
            if (bend.sqrMagnitude < 1e-10f) bend = Vector3.ProjectOnPlane(upper.forward, dir);
            bend.Normalize();
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2 * la * d), -1, 1);
            var elbow = a + dir * (la * cosA) + bend * (la * Mathf.Sqrt(1 - cosA * cosA));
            Swing(upper, b, elbow);
            Swing(lower, end.position, a + dir * d);
        }

        // ---------------- 시험·스크립트 조작 (poseBone) ----------------
        /// <summary>끌기와 같은 경로로 본 하나를 돌리거나(axis, angle) 관절을 옮긴다(move). 결과 자세를 Edited로 보낸다.</summary>
        public string Scripted(JNode m)
        {
            if (!_poser.Ready) return "휴머노이드 자세를 쓸 수 없음";
            int i = _poser.FindBone(m["bone"].AsString);
            if (i < 0) return "본 없음: " + m["bone"].AsString;
            if (m["select"].AsBool) { Select(i); return null; }
            _held = i; _dragging = true; _changed = false;
            if (m.Has("axis")) { var a = m["axis"].ToFloatArray(); Rotate(i, new Vector3(a[0], a[1], a[2]), m["angle"].AsFloat); }
            if (m.Has("ring")) { int k = Array.IndexOf(new[] { "x", "y", "z" }, m["ring"].AsString); if (k >= 0) Rotate(i, RingAxisOf(i, k), m["angle"].AsFloat); }
            // 화살표: 관절을 그 축(본 로컬, 월드 모드면 월드) 위로 amount(m)만큼
            if (m.Has("arrow")) { int k = Array.IndexOf(new[] { "x", "y", "z" }, m["arrow"].AsString); if (k >= 0) MoveJoint(i, _poser.BoneTransform(i).position + RingAxisOf(i, k) * m["amount"].AsFloat); }
            if (m.Has("move")) { var v = m["move"].ToFloatArray(); MoveJoint(i, _poser.BoneTransform(i).position + new Vector3(v[0], v[1], v[2])); }
            Finish();
            return null;
        }

        // ---------------- 그리기 ----------------
        public int Pick(Camera cam, Vector2 mouse, float px)
        {
            int best = -1; float bestD = px * px;
            for (int i = 0; i < _poser.BoneCount; i++)
            {
                var t = _poser.BoneTransform(i);
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var sp = cam.WorldToScreenPoint(t.position);
                if (sp.z <= 0) continue;
                float d = (new Vector2(sp.x, sp.y) - mouse).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>GL.LINES 안에서 부른다: 휴머노이드 뼈대 선과 관절 원 (고른 것 주황, 잡은 것 노랑, 마우스 아래 밝게).</summary>
        public void Draw(Camera cam, Vector2 mouse, float px)
        {
            if (!Active) return;
            int hover = _dragging ? _held : Pick(cam, mouse, px);
            var right = cam.transform.right; var up = cam.transform.up;
            var line = new Color(0.55f, 0.85f, 1f, 0.85f);
            for (int i = 0; i < _poser.BoneCount; i++)
            {
                var t = _poser.BoneTransform(i);
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                int p = _poser.BoneParent(i);
                if (p >= 0) { GL.Color(line); GL.Vertex(_poser.BoneTransform(p).position); GL.Vertex(t.position); }
                bool finger = _poser.BoneName(i).Contains("Proximal") || _poser.BoneName(i).Contains("Intermediate") || _poser.BoneName(i).Contains("Distal");
                float r = Mathf.Max(0.004f, Vector3.Distance(cam.transform.position, t.position) * (finger ? 0.006f : 0.012f));
                Color col = new Color(0.85f, 0.95f, 1f, 0.9f);
                if (i == _selected) { col = new Color(1f, 0.55f, 0.15f, 1f); r *= 1.4f; }
                if (_dragging && i == _held) col = new Color(1f, 0.9f, 0.2f, 1f);
                else if (i == hover) col = Color.Lerp(col, Color.white, 0.6f);
                ViewAids.Circle(t.position, right, up, i == hover ? r * 1.3f : r, col, 16);
                // 고른 본은 끝까지 굵게 (두 번 그어 눈에 띄게)
                if (i == _selected)
                {
                    var tip = Tip(i);
                    GL.Color(col); GL.Vertex(t.position); GL.Vertex(tip);
                    var o = (right + up) * (r * 0.15f);
                    GL.Vertex(t.position + o); GL.Vertex(tip + o);
                }
            }
            // 핸들은 뼈대 위에 (굵은 모양은 면으로 그리므로 나중에 그려야 가려지지 않는다)
            if (_selected >= 0 && _poser.BoneTransform(_selected) != null && (!_dragging || _handle >= 0))
            {
                PrepareGizmo(_selected);
                _gizmo.Draw(cam, mouse, px);
            }
        }
    }
}
