// 아이템 하나·아바타 전체·함께 세운 아바타(GuestAvatars, 2026-10-25)를 핸들로 옮기고 돌린다. 조작 모드(setView.poseBones)에서만 쓴다.
//   고르기   빈 곳(관절이 아닌 곳)을 더블클릭하면 커서 아래 메시를 찾아, 붙인 아이템이면 그 아이템, 아바타 메시면 아바타 전체를 고른다
//            (BonePoser.PickObject). Host 목록에서 고르면 select 메시지로 온다. 고른 것에만 핸들(TransformGizmo)과 테두리 상자를 그린다.
//   끌기     화살표 = 그 축으로 옮기기, 고리 = 그 축으로 돌리기. 아이템은 메시 범위의 가운데, 아바타는 발밑(루트)을 중심으로 돈다.
//   놓으면   아이템은 IR 부모 기준 로컬 TRS(nodeMoved), 아바타는 루트 위치·회전(avatarMoved)을 Host로 보낸다. 기록·실행 취소는 Host가 한다.
// 아이템 루트를 옮겨도 함께 움직이지 않는 아이템(뼈를 아바타 뼈에 합치거나 붙인 의상·헤어)은 고르지 않고 까닭을 알린다.
// 렌더러 빌드에 Physics 모듈이 없어서 광선 검사는 MeshPick으로 직접 한다 (더블클릭할 때만 메시를 굽는다).
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class ObjectMover
    {
        readonly SceneBuilder _scene;
        readonly TransformGizmo _gizmo = new TransformGizmo();
        public Action<JNode> Send;
        public Action<string> Notice;
        /// <summary>고르거나 풀었을 때 (종류 "item"·"avatar"·"", 아이템 노드). RenderServer가 selected로 보낸다.</summary>
        public Action<string, int> Selected;
        public bool Enabled;
        public bool World;

        int _node = -1; bool _avatar;
        GuestAvatars.Guest _guest;   // 함께 세운 아바타 (2026-10-25) — 아바타 전체처럼 발밑 중심, 놓으면 guestMoved
        bool _dragging, _changed;
        Vector3 _startPos, _pivot; Quaternion _startRot;
        Vector2 _last;
        /// <summary>함께 세운 아바타 (RenderServer가 넣는다). 빈 곳 더블클릭으로 고를 수 있다.</summary>
        public GuestAvatars Guests;

        public ObjectMover(SceneBuilder scene) { _scene = scene; }

        public bool HasTarget => _avatar || _node >= 0 || _guest != null;
        public bool IsDragging => _dragging;
        public string Kind => _avatar || _guest != null ? "avatar" : _node >= 0 ? "item" : "";
        public int Node => _node;
        Transform Target => _guest != null ? _guest.Root : _avatar ? _scene.Root : _scene.NodeTransform(_node);
        IEnumerable<Renderer> TargetRenderers => _guest != null ? _guest.Scene.Renderers : _scene.Renderers;

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            if (!on) { Finish(); Clear(); }
            Enabled = on;
        }

        /// <summary>장면을 다시 불러오거나 다른 것을 고르면 선택이 풀린다.</summary>
        public void Clear()
        {
            _dragging = false; _changed = false; _gizmo.End();
            bool had = HasTarget;
            _node = -1; _avatar = false; _guest = null;
            if (had) Selected?.Invoke("", -1);
        }

        /// <summary>아이템을 고른다. 통째로 옮길 수 없는 아이템이면 까닭을 돌려준다.</summary>
        public string SelectItem(int node)
        {
            var t = _scene.NodeTransform(node);
            if (t == null) return "아이템 노드를 찾지 못했어요";
            var why = NotMovable(node);
            if (why != null) return why;
            if (_dragging) Finish();
            _avatar = false; _guest = null; _node = node;
            Selected?.Invoke("item", node);
            Notice?.Invoke($"고른 아이템: {ItemName(node)} · 화살표를 끌면 그 축으로 옮기고 고리를 끌면 돌려요 (빨강 X · 초록 Y · 파랑 Z · 흰색 화면) · 빈 곳 더블클릭: 선택 풀기");
            return null;
        }

        public void SelectAvatar()
        {
            if (_scene.Root == null) return;
            if (_dragging) Finish();
            _avatar = true; _node = -1; _guest = null;
            Selected?.Invoke("avatar", -1);
            Notice?.Invoke("고른 것: 아바타 전체 · 화살표를 끌면 옮기고 고리를 끌면 발밑을 중심으로 돌려요 · 빈 곳 더블클릭: 선택 풀기");
        }

        /// <summary>함께 세운 아바타를 고른다 (Host 목록의 "옮기기" 또는 빈 곳 더블클릭).</summary>
        public bool SelectGuest(string id)
        {
            var g = Guests?.Find(id);
            if (g?.Scene?.Root == null) return false;
            if (_dragging) Finish();
            _avatar = false; _node = -1; _guest = g;
            Selected?.Invoke("guest", -1);
            Notice?.Invoke($"고른 것: 함께 세운 아바타 {g.Scene.Root.name} · 화살표를 끌면 옮기고 고리를 끌면 발밑을 중심으로 돌려요 · 빈 곳 더블클릭: 선택 풀기");
            return true;
        }

        public string GuestId => _guest?.Id ?? "";

        string ItemName(int node)
        {
            var outfits = _scene.Scene?.Outfits;
            if (outfits != null) foreach (var o in outfits) if (o.Node == node && o.Name.Length > 0) return o.Name;
            var t = _scene.NodeTransform(node);
            return t != null ? t.name : "아이템";
        }

        /// <summary>아이템 루트를 옮기면 그 아이템의 메시가 모두 따라오는가: 렌더러와 그 뼈가 모두 루트 아래에 있어야 한다.</summary>
        string NotMovable(int node)
        {
            var root = _scene.NodeTransform(node);
            int n = 0;
            foreach (var ren in _scene.Renderers)
            {
                if (ren == null || !(ren.transform == root || ren.transform.IsChildOf(root))) continue;
                n++;
                if (ren is SkinnedMeshRenderer smr)
                {
                    foreach (var b in smr.bones)
                        if (b != null && !(b == root || b.IsChildOf(root)))
                            return $"{ItemName(node)}은(는) 뼈가 아바타 뼈에 맞춰 붙어 있어 통째로 옮길 수 없어요 (아바타 페이지 \"붙일 곳 바꾸기\"나 \"커서로 놓기\"로 뼈 하나에 붙이면 옮길 수 있어요)";
                }
            }
            return n == 0 ? $"{ItemName(node)}에 메시가 없어요" : null;
        }

        /// <summary>빈 곳 더블클릭: 커서 아래 메시의 아이템(없으면 아바타 전체)을 고른다. 메시가 없으면 false.</summary>
        public bool PickAt(Camera cam, Vector2 mouse)
        {
            if (!Enabled) return false;
            var all = Guests != null ? System.Linq.Enumerable.Concat(_scene.Renderers, Guests.Renderers()) : _scene.Renderers;
            var ren = MeshPick.RendererAt(all, cam.ScreenPointToRay(mouse));
            if (ren == null) { if (HasTarget) { Clear(); Notice?.Invoke("선택을 풀었어요."); } return false; }
            if (Guests?.Owner(ren) is GuestAvatars.Guest guest) { SelectGuest(guest.Id); return true; }
            int item = ItemOf(ren.transform);
            if (item < 0) { SelectAvatar(); return true; }
            var why = SelectItem(item);
            if (why != null) { Clear(); Notice?.Invoke(why); }
            return true;
        }

        /// <summary>렌더러가 속한 붙인 아이템의 루트 노드 (없으면 -1).</summary>
        int ItemOf(Transform t)
        {
            var outfits = _scene.Scene?.Outfits;
            if (outfits == null) return -1;
            foreach (var o in outfits)
            {
                var root = _scene.NodeTransform(o.Node);
                if (root != null && (t == root || t.IsChildOf(root))) return o.Node;
            }
            return -1;
        }

        /// <summary>고른 것의 메시 범위 (월드). 없으면 루트 위치의 점.</summary>
        Bounds TargetBounds()
        {
            var t = Target;
            var b = new Bounds(t.position, Vector3.zero); bool any = false;
            foreach (var ren in TargetRenderers)
            {
                if (ren == null || !ren.enabled || !ren.gameObject.activeInHierarchy || !(ren.transform == t || ren.transform.IsChildOf(t))) continue;
                if (!any) { b = ren.bounds; any = true; } else b.Encapsulate(ren.bounds);
            }
            return b;
        }

        /// <summary>회전 중심: 아이템은 메시 범위 가운데, 아바타는 루트(발밑).</summary>
        Vector3 Pivot() => _avatar || _guest != null ? Target.position : TargetBounds().center;

        void PrepareGizmo()
        {
            var t = Target;
            _gizmo.Center = _dragging ? _gizmo.Center : Pivot();
            _gizmo.Frame = World ? Quaternion.identity : t.rotation;
        }

        // ---------------- 입력 ----------------
        public bool WantsInput(Camera cam, Vector2 mouse, float px)
        {
            if (!Enabled || !HasTarget || Target == null) return false;
            if (_dragging) return true;
            if (!Input.GetMouseButtonDown(0)) return false;
            PrepareGizmo();
            return _gizmo.Pick(cam, mouse, px, out _) >= 0;
        }

        public void Update(Camera cam, Vector2 mouse, float px)
        {
            if (!Enabled || !HasTarget || Target == null) return;
            if (_dragging)
            {
                if (!Input.GetMouseButton(0)) { Finish(); return; }
                if ((mouse - _last).sqrMagnitude > 0.25f)
                {
                    if (TransformGizmo.IsMove(_gizmo.Active)) MoveTo(_gizmo.MoveFromStart(cam, mouse));
                    else Turn(_gizmo.ActiveAxis, _gizmo.Angle(_last, mouse));
                    _last = mouse;
                }
                return;
            }
            if (!Input.GetMouseButtonDown(0)) return;
            PrepareGizmo();
            int h = _gizmo.Pick(cam, mouse, px, out var hit);
            if (h < 0) return;
            Begin();
            _gizmo.Begin(cam, h, hit, mouse);
            _last = mouse;
            int k = TransformGizmo.IsMove(h) ? h : h - TransformGizmo.RingX;
            var who = _guest != null ? "함께 세운 아바타" : _avatar ? "아바타" : ItemName(_node);
            Notice?.Invoke(TransformGizmo.IsMove(h) ? $"{who} · {TransformGizmo.Names[k]} 축으로 옮기기" : $"{who} · {TransformGizmo.Names[k]} 축으로 돌리기");
        }

        void Begin()
        {
            var t = Target;
            _dragging = true; _changed = false;
            _startPos = t.position; _startRot = t.rotation;
            _pivot = Pivot();
            _gizmo.Center = _pivot;
        }

        /// <summary>잡을 때 자리에서 delta만큼 (화살표는 잡은 뒤 누적값을 준다).</summary>
        void MoveTo(Vector3 delta)
        {
            var t = Target;
            t.position = _startPos + delta;
            _gizmo.Center = _pivot + delta;
            _changed = true;
        }

        /// <summary>중심(_pivot) 둘레로 axis 축 degrees만큼 더 돌린다.</summary>
        void Turn(Vector3 axis, float degrees)
        {
            var t = Target;
            var r = Quaternion.AngleAxis(degrees, axis);
            t.SetPositionAndRotation(_gizmo.Center + r * (t.position - _gizmo.Center), r * t.rotation);
            _changed = true;
        }

        void Finish()
        {
            if (_dragging && _changed && Target != null) Send?.Invoke(Moved());
            _dragging = false; _changed = false; _gizmo.End();
        }

        /// <summary>지금 자리: 아이템은 IR 부모 기준 로컬 [위치3, 회전4, 크기3], 아바타는 [위치3, 회전4].</summary>
        JNode Moved()
        {
            if (_guest != null) return Guests.Moved(_guest);
            var t = Target;
            Vector3 p = t.localPosition, s = t.localScale; var q = t.localRotation;
            if (_avatar) return Msg.Make(Msg.AvatarMoved).Set("trs", JNode.FloatArray(new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w }));
            return Msg.Make(Msg.NodeMoved).Set("node", _node).Set("trs", JNode.FloatArray(new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w, s.x, s.y, s.z }));
        }

        // ---------------- 시험·스크립트 조작 (moveTarget) ----------------
        /// <summary>끌기와 같은 경로로 고른 것을 옮기거나(arrow: "x"|"y"|"z", amount m) 돌린다(ring: "x"|"y"|"z", angle 도). 결과를 nodeMoved·avatarMoved로 보낸다.</summary>
        public string Scripted(JNode m)
        {
            if (!HasTarget || Target == null) return "고른 아이템·아바타가 없음";
            PrepareGizmo();
            Begin();
            if (m.Has("arrow")) { int k = Array.IndexOf(new[] { "x", "y", "z" }, m["arrow"].AsString); if (k >= 0) MoveTo(_gizmo.Axis(null, k) * m["amount"].AsFloat); }
            if (m.Has("ring")) { int k = Array.IndexOf(new[] { "x", "y", "z" }, m["ring"].AsString); if (k >= 0) Turn(_gizmo.Axis(null, TransformGizmo.RingX + k), m["angle"].AsFloat); }
            Finish();
            return null;
        }

        // ---------------- 그리기 ----------------
        /// <summary>GL.LINES 안에서 부른다: 고른 것의 테두리 상자와 핸들.</summary>
        public void Draw(Camera cam, Vector2 mouse, float px)
        {
            if (!Enabled || !HasTarget || Target == null) return;
            var b = TargetBounds();
            GL.Color(new Color(1f, 0.55f, 0.15f, 0.7f));
            Box(b.min, b.max);
            PrepareGizmo();
            _gizmo.Draw(cam, mouse, px);
        }

        static void Box(Vector3 a, Vector3 b)
        {
            Vector3 P(int i) => new Vector3((i & 1) == 0 ? a.x : b.x, (i & 2) == 0 ? a.y : b.y, (i & 4) == 0 ? a.z : b.z);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) { GL.Vertex(P(i)); GL.Vertex(P(i | bit)); }
        }
    }
}
