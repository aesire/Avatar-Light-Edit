// 뷰포트 보조 표시 (Unity 씬 뷰에서 아바타 작업할 때 쓰던 것들). 화면에만 그리고 장면·머티리얼은 건드리지 않는다.
//   방향 기즈모   오른쪽 위 X/Y/Z 축. 축을 누르면 그 방향에서 보고, 아래 글자를 누르면 원근 ↔ 직교
//   바닥 격자     y = 0 평면, 25cm 간격 (1m마다 진하게), X축 빨강 · Z축 파랑
//   본 표시       스킨드 메시가 쓰는 본을 부모-자식 선으로 (가려져도 보이게)
//   그리기 방식   셰이딩 / 셰이딩 + 와이어프레임 / 와이어프레임
//   거울 보기     VRChat 거울처럼 좌우 반전
//   배경          환경 설정 그대로 / 회색 / 흰색 / 검정 / 초록(크로마키)
//   PhysBone      체인·콜라이더 표시. 켜 두면 관절을 왼쪽 버튼으로 끌어 잡고, 잡은 채 오른쪽 클릭으로 고정, 고정한 관절 오른쪽 클릭으로 풀기 (PhysBoneSim)
//   조작         휴머노이드 관절 끌기·더블클릭 선택·잡은 채 휠·핸들 (BonePoser), 아이템·아바타 전체 고르기·핸들 (ObjectMover).
//                켜 두면 PhysBone 관절 잡기는 쉰다 (같은 왼쪽 드래그)
//   메시 편집    정점 고르기·옮기기·누르기 브러시 (MeshEditor). 켜 두면 본 조작·PhysBone 잡기보다 먼저 왼쪽 버튼을 쓴다
// 상태의 단일 원천은 Host: setView 메시지로 전부 받는다 (멱등). 촬영(Capture)에는 아무것도 섞이지 않는다.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class ViewAids : MonoBehaviour
    {
        public bool Gizmo = true, Grid = true, Bones, Mirror, PhysGizmos;
        /// <summary>구도 안내선: 촬영 크기의 비율 틀(가로/세로, 0 = 미리보기 그대로) 안 3분할 선 (2026-10-23)</summary>
        public bool Thirds;
        public float FrameAspect;
        /// <summary>방향 기즈모 모양 (setView axisStyle): disc 원(기본) | cube 큐브 | mini 미니.</summary>
        public string AxisStyle = "disc";
        /// <summary>뷰포트 본 조작 (RenderServer가 Init 뒤에 넣는다).</summary>
        public BonePoser BonePose;
        /// <summary>악세서리 커서로 놓기 (RenderServer가 넣는다). 놓는 동안은 관절 잡기·본 조작을 쉰다.</summary>
        public ItemPlacer Placer;
        /// <summary>아이템·아바타 핸들 (RenderServer가 넣는다). 조작 모드에서 본 조작과 함께 쓴다.</summary>
        public ObjectMover Mover;
        bool Moving => Mover != null && Mover.Enabled;
        bool Placing => Placer != null && Placer.Active;
        bool PoseBones => BonePose != null && BonePose.Enabled;
        /// <summary>뷰포트 정점 편집 (RenderServer가 넣는다).</summary>
        public MeshEditor MeshEdit;
        bool MeshEditing => MeshEdit != null && MeshEdit.Enabled;
        /// <summary>조작 결과 안내 (RenderServer가 Host 상태 표시줄로 보낸다).</summary>
        public System.Action<string> Notice;
        public string Draw = "shaded";   // shaded | shadedWire | wire
        public string Background = "env"; // env | gray | white | black | green
        // "아바타만" 카메라 (2026-10-27): 배경 레이어(소품·바닥·함께 세운 아바타)·스튜디오 배경·격자를 빼고 고른 단색으로 지운다. 촬영·녹화(Capture)도 따른다
        public static bool AvatarOnly;
        public static Color AvatarOnlyColor = new Color(0, 1, 0);
        /// <summary>미리보기·촬영 카메라의 컬링 마스크.</summary>
        public static int PreviewMask => ~VrLayers.HiddenFromPreview & (AvatarOnly ? ~VrLayers.SceneryMask : ~0);
        string _outlineRenderer = "";

        /// <summary>GL.LINES를 연 상태에서 c를 중심으로 right·up 평면의 원 (본 조작·PhysBone 표시가 같이 쓴다).</summary>
        public static void Circle(Vector3 c, Vector3 right, Vector3 up, float r, Color col, int seg)
        {
            GL.Color(col);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                GL.Vertex(c + (right * Mathf.Cos(a0) + up * Mathf.Sin(a0)) * r);
                GL.Vertex(c + (right * Mathf.Cos(a1) + up * Mathf.Sin(a1)) * r);
            }
        }

        OrbitCamera _orbit;
        SceneBuilder _scene;
        EnvironmentManager _env;
        PhysBoneSim _phys;
        Camera _main, _wireCam;
        Material _overlayDepth, _overlayAlways;
        Shader _wireShader;
        Font _font;
        GUIStyle _axisStyle, _modeStyle;
        bool _mirrorApplied;

        // 기즈모 배치 (GUI 좌표: 왼쪽 위 원점). Unity 씬 뷰와 비슷한 크기
        // 고해상도 화면(125%·150% 배율)에서도 같은 크기로 보이게 DPI에 맞춰 키운다
        static float S => Screen.dpi > 0 ? Mathf.Clamp(Screen.dpi / 96f, 1, 3) : 1;
        static float GizmoRadius => 34 * S;
        static float GizmoMargin => 14 * S;
        static float PosDisc => 9 * S;
        static float NegDisc => 6 * S;
        struct Axis { public string Id, Label; public Vector3 Dir; public Color Color; public bool Positive; public Vector2 Screen; public float Depth; }
        readonly Axis[] _axes = new Axis[6];
        readonly List<Axis> _order = new List<Axis>(6);
        Camera[] _cams;
        string _hover;
        Rect _modeRect;

        public void Init(OrbitCamera orbit, SceneBuilder scene, EnvironmentManager env, PhysBoneSim phys)
        {
            _orbit = orbit; _scene = scene; _env = env; _phys = phys;
            _main = orbit.Camera;
            var overlay = Shader.Find("Hidden/VrcProto/Overlay");
            _overlayDepth = new Material(overlay) { hideFlags = HideFlags.HideAndDontSave };
            _overlayDepth.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
            _overlayAlways = new Material(overlay) { hideFlags = HideFlags.HideAndDontSave };
            _overlayAlways.SetFloat("_ZTest", (float)CompareFunction.Always);
            _wireShader = Shader.Find("Hidden/VrcProto/Wire");
            Shader.SetGlobalColor("_VrcProtoWireColor", new Color(0.05f, 0.05f, 0.08f, 0.55f));

            // 와이어프레임은 같은 위치의 보조 카메라가 대체 셰이더로 한 번 더 그린다
            var go = new GameObject("WireCamera");
            go.transform.SetParent(orbit.transform, false);
            _wireCam = go.AddComponent<Camera>();
            _wireCam.CopyFrom(_main);
            _wireCam.depth = _main.depth + 1;
            _wireCam.clearFlags = CameraClearFlags.Nothing;
            _wireCam.cullingMask = ~VrLayers.HiddenFromPreview;
            _wireCam.enabled = false;
            if (_wireShader != null) _wireCam.SetReplacementShader(_wireShader, "");

            _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Malgun Gothic" }, 12);
            _axisStyle = new GUIStyle { font = _font, fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _axisStyle.normal.textColor = Color.white;
            _modeStyle = new GUIStyle { font = _font, fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _modeStyle.normal.textColor = new Color(1, 1, 1, 0.85f);

            Camera.onPreRender += OnCamPreRender;
            Camera.onPostRender += OnCamPostRender;
            orbit.BlockInput = m => Overlay.On ? Overlay.BlocksOrbit() : OverGizmo(m) || (Placing ? Placer.WantsInput() : MeshEditing ? MeshEdit.WantsInput() : OverObject(m) || OverBone(m) || OverPhysBone(m));
        }

        void OnDestroy()
        {
            Camera.onPreRender -= OnCamPreRender;
            Camera.onPostRender -= OnCamPostRender;
        }

        public void Set(JNode m)
        {
            if (m.Has("gizmo")) Gizmo = m["gizmo"].AsBool;
            if (m.Has("grid")) Grid = m["grid"].AsBool;
            if (m.Has("bones")) Bones = m["bones"].AsBool;
            if (m.Has("mirror")) Mirror = m["mirror"].AsBool;
            if (m.Has("thirds")) Thirds = m["thirds"].AsBool;
            if (m.Has("frameAspect")) FrameAspect = Mathf.Max(0, m["frameAspect"].AsFloat);
            if (m.Has("draw")) Draw = m["draw"].AsString;
            if (m.Has("background")) Background = m["background"].AsString;
            if (m.Has("avatarOnly"))
            {
                AvatarOnly = m["avatarOnly"]["on"].AsBool;
                AvatarOnlyColor = EnvColor.Read(m["avatarOnly"]["color"], new Color(0, 1, 0));
            }
            if (m.Has("physBoneGizmos")) { PhysGizmos = m["physBoneGizmos"].AsBool; if (!PhysGizmos) _phys.Release(); }
            if (m.Has("poseBones") && BonePose != null) { BonePose.SetEnabled(m["poseBones"].AsBool); Mover?.SetEnabled(m["poseBones"].AsBool); if (PoseBones) _phys.Release(); }
            if (m.Has("handleStyle")) TransformGizmo.Style = m["handleStyle"].AsString;
            if (m.Has("handleSize")) TransformGizmo.Size = Mathf.Clamp(m["handleSize"].AsFloat, 0.5f, 2f);
            if (m.Has("axisStyle")) AxisStyle = m["axisStyle"].AsString;
            if (m.Has("nav")) _orbit.UnityNav = m["nav"].AsString == "unity";
            if (m.Has("handleSpace")) { bool w = m["handleSpace"].AsString == "world"; if (BonePose != null) BonePose.World = w; if (Mover != null) Mover.World = w; }
            _orbit.Mirror = Mirror;
        }

        /// <summary>아바타·메시 목록에서 윤곽을 켠 파츠. 마지막 카메라 위에 얇은 윤곽 상자를 그린다.</summary>
        public void SetOutline(string renderer, bool on) => _outlineRenderer = on ? renderer ?? "" : "";

        // ---------------- 카메라 ----------------
        void LateUpdate()
        {
            if (_main == null) return;
            bool wire = Draw == "wire" || Draw == "shadedWire";
            _wireCam.enabled = wire && _wireShader != null;
            if (_wireCam.enabled)
            {
                _wireCam.fieldOfView = _main.fieldOfView; _wireCam.orthographic = _main.orthographic;
                _wireCam.orthographicSize = _main.orthographicSize;
                _wireCam.nearClipPlane = _main.nearClipPlane; _wireCam.farClipPlane = _main.farClipPlane;
                _wireCam.rect = _main.rect;
                _wireCam.cullingMask = PreviewMask;
            }
            // 와이어프레임만: 본 카메라는 배경만 지운다
            _main.cullingMask = Draw == "wire" ? 0 : PreviewMask;   // VR 1인칭 머리 숨김 사본·바닥 격자는 눈 카메라에만
            _main.backgroundColor = Overlay.On ? Color.clear : AvatarOnly ? AvatarOnlyColor : BackgroundColor();   // 방송 오버레이: 투명 (알파 0)

            // 거울 보기: 투영 행렬 좌우 반전 (+ 렌더할 때 컬링 반전). 반전 행렬은 화면비가 바뀌어도 따라가도록 매 프레임 다시 만든다
            if (Mirror || _mirrorApplied)
            {
                foreach (var c in _cams ??= new[] { _main, _wireCam })
                {
                    c.ResetProjectionMatrix();
                    if (Mirror) c.projectionMatrix = c.projectionMatrix * Matrix4x4.Scale(new Vector3(-1, 1, 1));
                }
                _mirrorApplied = Mirror;
            }
        }

        Color BackgroundColor()
        {
            switch (Background)
            {
                case "gray": return new Color(0.42f, 0.42f, 0.44f);
                case "white": return Color.white;
                case "black": return Color.black;
                case "green": return new Color(0, 1, 0);
                default: return _env.Background;
            }
        }

        bool Ours(Camera c) => (c == _main || c == _wireCam) && c.targetTexture == null; // 촬영(RenderTexture)은 제외

        void OnCamPreRender(Camera c)
        {
            if (!Ours(c)) return;
            if (Mirror) GL.invertCulling = true;
            if (c == _wireCam) GL.wireframe = true;
        }

        void OnCamPostRender(Camera c)
        {
            if (!Ours(c)) return;
            if (c == _wireCam) GL.wireframe = false;
            if (Mirror) GL.invertCulling = false;
            // 격자·본은 마지막 카메라 위에 한 번만
            bool last = _wireCam.enabled ? c == _wireCam : c == _main;
            if (!last || Overlay.On) return;   // 방송 오버레이에는 보조 표시를 그리지 않는다
            if (Grid && !AvatarOnly) DrawGrid();
            if (Thirds) DrawThirds();
            if (Bones) DrawBones();
            if (PhysGizmos) DrawPhysBones();
            if (PoseBones) DrawPoseBones();
            if (MeshEditing)
            {
                MeshEdit.Draw(_main, _overlayDepth, _overlayAlways);
                if (MeshEdit.BoxRect(out var box))
                {
                    GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); _overlayAlways.SetPass(0);
                    Rect(box, new Color(1f, 0.6f, 0.2f, 0.15f)); GL.PopMatrix();
                }
            }
            if (Placing)
            {
                _overlayAlways.SetPass(0);
                GL.PushMatrix(); GL.Begin(GL.LINES); Placer.Draw(); GL.End(); GL.PopMatrix();
            }
            DrawSelectionOutline();
        }

        void DrawSelectionOutline()
        {
            if (_outlineRenderer.Length == 0 || !_scene.TryRendererBounds(_outlineRenderer, out var b)) return;
            var r = _scene.RendererOf(_outlineRenderer);
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return;
            _overlayAlways.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 0.65f, 0.15f, 0.95f));
            Box(b.min, b.max);
            GL.End();
            GL.PopMatrix();
        }

        static void Box(Vector3 a, Vector3 b)
        {
            Vector3 P(int i) => new Vector3((i & 1) == 0 ? a.x : b.x, (i & 2) == 0 ? a.y : b.y, (i & 4) == 0 ? a.z : b.z);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) { GL.Vertex(P(i)); GL.Vertex(P(i | bit)); }
        }

        // ---------------- PhysBone ----------------
        static float GrabPx => 12 * S;
        static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        /// 왼쪽 버튼을 누른 곳에 잡을 관절이 있으면 카메라 회전 대신 잡기
        /// 카메라 조작 대신 PhysBone 조작: 잡는 중(오른쪽 클릭 = 고정), 왼쪽 버튼으로 관절 잡기, 오른쪽 버튼으로 고정한 관절 풀기
        bool OverPhysBone(Vector2 mouse) => PhysGizmos && !PoseBones && (_phys.IsGrabbing || GrabStart(mouse) || UnpinStart(mouse));
        bool GrabStart(Vector2 mouse) => Input.GetMouseButtonDown(0) && !Shift && _phys.CanGrab(_main, mouse, GrabPx);
        bool UnpinStart(Vector2 mouse) => Input.GetMouseButtonDown(1) && (_phys.RecentlyReleased || _phys.IsPinnedAt(_main, mouse, GrabPx));

        void UpdateGrab()
        {
            var mouse = (Vector2)Input.mousePosition;
            if (_phys.IsGrabbing)
            {
                if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonUp(1)) Say(_phys.PinGrabbed()); // 빠른 클릭은 누름·뗌이 한 프레임에 올 수 있다
                else if (Input.GetMouseButton(0)) _phys.DragTo(_main, mouse);
                else _phys.Release();
            }
            else if (!PhysGizmos || PoseBones || OverGizmo(mouse)) return;
            else if (GrabStart(mouse)) _phys.Grab(_main, mouse, GrabPx);
            else if (Input.GetMouseButtonDown(1) && _phys.RecentlyReleased) Say(_phys.PinGrabbed());
            else if (UnpinStart(mouse)) Say(_phys.UnpinAt(_main, mouse, GrabPx));
        }

        // ---------------- 본 조작 ----------------
        bool OverBone(Vector2 mouse) => PoseBones && (BonePose.IsDragging || !OverGizmo(mouse) && BonePose.WantsInput(_main, mouse, GrabPx));

        bool OverObject(Vector2 mouse) => Moving && (Mover.IsDragging || !BonePose.IsDragging && !OverGizmo(mouse) && Mover.WantsInput(_main, mouse, GrabPx));

        void UpdateBones()
        {
            var mouse = (Vector2)Input.mousePosition;
            if (!BonePose.IsDragging && !Mover.IsDragging && OverGizmo(mouse)) return;
            // 고른 아이템·아바타의 핸들이 먼저 (본을 끄는 중이 아니면)
            if (Moving && !BonePose.IsDragging && (Mover.IsDragging || Mover.WantsInput(_main, mouse, GrabPx))) { Mover.Update(_main, mouse, GrabPx); return; }
            BonePose.Update(_main, mouse, GrabPx);
        }

        void DrawPoseBones()
        {
            _overlayAlways.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            BonePose.Draw(_main, Input.mousePosition, GrabPx);
            if (Moving) Mover.Draw(_main, Input.mousePosition, GrabPx);
            GL.End();
            GL.PopMatrix();
        }

        void Say(string text) { if (!string.IsNullOrEmpty(text)) Notice?.Invoke(text); }

        void DrawPhysBones()
        {
            _overlayAlways.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            _phys.DrawGizmos(_main, Input.mousePosition, GrabPx);
            GL.End();
            GL.PopMatrix();
        }

        // ---------------- 격자 ----------------
        void DrawGrid()
        {
            const float step = 0.25f; const int n = 12; // ±3m
            _overlayDepth.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            float ext = step * n;
            for (int i = -n; i <= n; i++)
            {
                float v = i * step;
                bool major = i % 4 == 0;
                float a = major ? 0.32f : 0.14f;
                float fade = 1 - Mathf.Abs(i) / (float)(n + 1) * 0.6f;
                // x = v 선 (Z 방향). x = 0 은 Z축(파랑)
                GL.Color(i == 0 ? new Color(0.3f, 0.55f, 1f, 0.8f) : new Color(1, 1, 1, a * fade));
                GL.Vertex3(v, 0, -ext); GL.Vertex3(v, 0, ext);
                // z = v 선 (X 방향). z = 0 은 X축(빨강)
                GL.Color(i == 0 ? new Color(1f, 0.35f, 0.3f, 0.8f) : new Color(1, 1, 1, a * fade));
                GL.Vertex3(-ext, 0, v); GL.Vertex3(ext, 0, v);
            }
            GL.End();
            GL.PopMatrix();
        }

        // ---------------- 본 ----------------
        void DrawBones()
        {
            var bones = _scene.BoneTransforms();
            if (bones.Count == 0) return;
            _overlayAlways.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            var line = new Color(0.35f, 0.9f, 1f, 0.9f);
            var joint = new Color(1f, 0.85f, 0.3f, 0.95f);
            foreach (var b in bones)
            {
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                var p = b.parent;
                if (p != null && bones.Contains(p))
                {
                    GL.Color(line);
                    GL.Vertex(p.position); GL.Vertex(b.position);
                }
                // 관절 표시: 작은 십자 (크기는 카메라 거리에 비례)
                float s = Mathf.Max(0.006f, Vector3.Distance(_main.transform.position, b.position) * 0.014f);
                GL.Color(joint);
                var pos = b.position;
                GL.Vertex(pos - Vector3.right * s); GL.Vertex(pos + Vector3.right * s);
                GL.Vertex(pos - Vector3.up * s); GL.Vertex(pos + Vector3.up * s);
                GL.Vertex(pos - Vector3.forward * s); GL.Vertex(pos + Vector3.forward * s);
            }
            GL.End();
            GL.PopMatrix();
        }

        // ---------------- 방향 기즈모 ----------------
        Vector2 GizmoCenter() => new Vector2(Screen.width - GizmoMargin - GizmoRadius - PosDisc, GizmoMargin + GizmoRadius + PosDisc);

        void UpdateAxes()
        {
            var view = _main.worldToCameraMatrix;
            _view = view;
            var c = GizmoCenter();
            var x = new Color(0.93f, 0.33f, 0.36f); var y = new Color(0.52f, 0.80f, 0.27f); var z = new Color(0.28f, 0.55f, 0.95f);
            Fill(0, "+x", "X", Vector3.right, x, true); Fill(1, "-x", "", Vector3.left, x, false);
            Fill(2, "+y", "Y", Vector3.up, y, true); Fill(3, "-y", "", Vector3.down, y, false);
            Fill(4, "+z", "Z", Vector3.forward, z, true); Fill(5, "-z", "", Vector3.back, z, false);

            void Fill(int i, string id, string label, Vector3 dir, Color col, bool pos)
            {
                var v = view.MultiplyVector(dir);
                if (Mirror) v.x = -v.x;
                _axes[i] = new Axis
                {
                    Id = id, Label = label, Dir = dir, Color = col, Positive = pos,
                    Screen = c + new Vector2(v.x, -v.y) * GizmoRadius, Depth = v.z, // 카메라 공간 +z = 보는 사람 쪽
                };
            }
        }

        static Vector2 GuiMouse() { var m = Input.mousePosition; return new Vector2(m.x, Screen.height - m.y); }

        string HitAxis(Vector2 gui)
        {
            string best = null; float bestDepth = float.MinValue;
            if (AxisStyle == "cube")
            {
                for (int i = 0; i < 6; i++)
                {
                    if (_axes[i].Depth < 0.05f) continue;
                    CubeFace(i, _quad);
                    if (InQuad(gui, _quad) && _axes[i].Depth > bestDepth) { best = _axes[i].Id; bestDepth = _axes[i].Depth; }
                }
                return best;
            }
            foreach (var a in _axes)
            {
                if (AxisStyle == "mini" && !a.Positive) continue;
                float r = (a.Positive ? PosDisc : NegDisc) + 3 * S;
                if ((gui - a.Screen).sqrMagnitude <= r * r && a.Depth > bestDepth) { best = a.Id; bestDepth = a.Depth; }
            }
            return best;
        }

        bool OverGizmo(Vector2 mouse)
        {
            if (!Gizmo || _main == null || Overlay.On) return false;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            var c = GizmoCenter();
            return (gui - c).magnitude <= GizmoRadius + PosDisc + 4 * S || _modeRect.Contains(gui);
        }

        void Update()
        {
            // 방송 오버레이: 관절 잡기·핸들·메시 편집·방향 기즈모를 모두 쉬고, 창 옮기기만
            if (Overlay.On)
            {
                if (_phys.IsGrabbing) _phys.Release();
                _hover = null;
                Overlay.Tick();
                return;
            }
            if (_main != null)
            {
                if (Placing) { var mp = (Vector2)Input.mousePosition; Placer.Update(_main, mp, OverGizmo(mp)); }
                else if (MeshEditing) { var mp = (Vector2)Input.mousePosition; if (MeshEdit.IsDragging || !OverGizmo(mp)) MeshEdit.Update(_main, mp, GrabPx); }
                else { UpdateGrab(); if (PoseBones) UpdateBones(); }
            }
            if (!Gizmo || _main == null) { _hover = null; return; }
            UpdateAxes();
            var gui = GuiMouse();
            _hover = HitAxis(gui);
            if (_hover == null && _modeRect.Contains(gui)) _hover = "mode";
            if (Input.GetMouseButtonUp(0) && _hover != null)
            {
                if (_hover == "mode") _orbit.SetOrthographic(!_orbit.Orthographic);
                else _orbit.LookFromAxis(_hover);
            }
        }

        void OnGUI()
        {
            if (Overlay.On) { Overlay.DrawGui(_font); return; }   // 방송 오버레이: 조정 모드의 테두리·✕만
            if (!Gizmo || _main == null || Event.current.type != EventType.Repaint) return;
            UpdateAxes();
            _axisStyle.fontSize = Mathf.RoundToInt(11 * S);
            _modeStyle.fontSize = Mathf.RoundToInt(12 * S);
            var c = GizmoCenter();
            var order = _order;
            order.Clear(); order.AddRange(_axes);
            order.Sort((a, b) => a.Depth.CompareTo(b.Depth)); // 먼 것부터

            if (AxisStyle == "cube") DrawCube(c, order);
            else if (AxisStyle == "mini") DrawMini(c, order);
            else DrawDiscs(c, order);
            DrawModeLabel(c);
        }

        void DrawDiscs(Vector2 c, List<Axis> order)
        {
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); // GUI와 같은 왼쪽 위 원점
            _overlayAlways.SetPass(0);
            // 뒤판
            // 뒤판: 어두운 유리 원 + 가는 밝은 테 (마우스를 올리면 조금 진하게)
            float plate = GizmoRadius + PosDisc + 5 * S;
            SoftDisc(c, plate, new Color(0.07f, 0.08f, 0.10f, _hover != null ? 0.42f : 0.26f));
            Ring(c, plate, 1f * S, new Color(1, 1, 1, _hover != null ? 0.16f : 0.08f));
            foreach (var a in order)
            {
                // 뒤를 향한 축은 흐리고 어둡게, 앞을 향한 축은 또렷하게
                float front = Mathf.InverseLerp(-1, 1, a.Depth);
                float k = Mathf.Lerp(0.62f, 1f, front);
                bool hot = _hover == a.Id;
                var col = a.Color * k; col.a = 1;
                if (hot) col = Color.Lerp(col, Color.white, 0.3f);
                if (a.Positive)
                {
                    // 축 선: 가운데에서 원 가장자리까지, 끝이 둥글게
                    var dir = a.Screen - c; float len = dir.magnitude;
                    if (len > PosDisc * 0.5f)
                    {
                        var end = c + dir / len * Mathf.Max(0, len - PosDisc * 0.6f);
                        var lc = col; lc.a = Mathf.Lerp(0.55f, 0.95f, front);
                        Line(c, end, lc, 2.2f * S);
                    }
                    float r = hot ? PosDisc + 1.5f * S : PosDisc;
                    SoftDisc(a.Screen, r, col);
                    Ring(a.Screen, r, 1.1f * S, new Color(col.r * 0.55f, col.g * 0.55f, col.b * 0.55f, 0.9f));
                    // 위쪽 옅은 빛 (살짝 볼록하게)
                    Disc(a.Screen + new Vector2(0, -r * 0.35f), r * 0.55f, new Color(1, 1, 1, 0.13f));
                }
                else
                {
                    // 음의 축: 속이 옅은 고리. 마우스를 올리면 채워지고 "-X" 글자
                    float r = hot ? PosDisc : NegDisc;
                    SoftDisc(a.Screen, r, new Color(col.r, col.g, col.b, hot ? 1f : 0.22f));
                    Ring(a.Screen, r, 1.5f * S, new Color(col.r, col.g, col.b, Mathf.Lerp(0.55f, 0.95f, front)));
                }
            }
            SoftDisc(c, 2.6f * S, new Color(0.88f, 0.89f, 0.92f, 1));
            GL.PopMatrix();

            foreach (var a in order)
            {
                string label = a.Positive ? a.Label : _hover == a.Id ? "-" + a.Id.Substring(1).ToUpperInvariant() : "";
                if (label.Length == 0) continue;
                var keep = _axisStyle.normal.textColor;
                // 밝은 원(초록) 위에서도 읽히게 글자 그림자
                _axisStyle.normal.textColor = new Color(0, 0, 0, 0.35f);
                GUI.Label(new Rect(a.Screen.x - 10 * S, a.Screen.y - 10 * S + 1, 20 * S, 20 * S), label, _axisStyle);
                _axisStyle.normal.textColor = keep;
                GUI.Label(new Rect(a.Screen.x - 10 * S, a.Screen.y - 10 * S, 20 * S, 20 * S), label, _axisStyle);
            }
        }

        /// <summary>가장자리를 반 픽셀 옅게 한 번 더 그려 계단을 줄인 원 (GL에는 안티에일리어싱이 없다).</summary>
        static void SoftDisc(Vector2 c, float r, Color col)
        {
            Disc(c, r + 0.7f, new Color(col.r, col.g, col.b, col.a * 0.35f), 40);
            Disc(c, r, col, 40);
        }

        /// <summary>두께 w의 고리 (바깥 반지름 r).</summary>
        static void Ring(Vector2 c, float r, float w, Color col)
        {
            const int seg = 40;
            GL.Begin(GL.QUADS); GL.Color(col);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                GL.Vertex3(c.x + d0.x * r, c.y + d0.y * r, 0); GL.Vertex3(c.x + d1.x * r, c.y + d1.y * r, 0);
                GL.Vertex3(c.x + d1.x * (r - w), c.y + d1.y * (r - w), 0); GL.Vertex3(c.x + d0.x * (r - w), c.y + d0.y * (r - w), 0);
            }
            GL.End();
        }

        // 미니: 뒤판·원 없이 양의 축 세 줄과 글자만 (마우스를 올리면 옅은 뒤판)
        void DrawMini(Vector2 c, List<Axis> order)
        {
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            _overlayAlways.SetPass(0);
            if (_hover != null) Disc(c, GizmoRadius + PosDisc + 4 * S, new Color(0, 0, 0, 0.18f));
            foreach (var a in order)
            {
                if (!a.Positive) continue;
                float k = Mathf.Lerp(0.5f, 1f, Mathf.InverseLerp(-1, 1, a.Depth));
                var col = a.Color * k; col.a = 1;
                if (_hover == a.Id) col = Color.Lerp(col, Color.white, 0.4f);
                var tip = a.Screen;
                Line(c, tip, new Color(0, 0, 0, 0.35f), 5f * S);
                Line(c, tip, col, 2.8f * S);
                Disc(tip, 2.2f * S, col);
            }
            Disc(c, 2.5f * S, new Color(0.9f, 0.9f, 0.92f, 1));
            GL.PopMatrix();
            var keep = _axisStyle.normal.textColor;
            foreach (var a in order)
            {
                if (!a.Positive) continue;
                var col = Color.Lerp(a.Color, Color.white, _hover == a.Id ? 0.6f : 0.25f); col.a = Mathf.Lerp(0.55f, 1f, Mathf.InverseLerp(-1, 1, a.Depth));
                _axisStyle.normal.textColor = col;
                var at = c + (a.Screen - c) * 1.3f;
                GUI.Label(new Rect(at.x - 10 * S, at.y - 10 * S, 20 * S, 20 * S), a.Label, _axisStyle);
            }
            _axisStyle.normal.textColor = keep;
        }

        // 큐브: 보이는 면에 방향 이름. 면을 누르면 그 방향에서 본다
        static readonly Dictionary<string, string> FaceNames = new Dictionary<string, string>
        { { "+z", "정면" }, { "-z", "후면" }, { "+x", "오른쪽" }, { "-x", "왼쪽" }, { "+y", "위" }, { "-y", "아래" } };
        readonly Vector2[] _quad = new Vector2[4];
        Matrix4x4 _view;
        float CubeHalf => GizmoRadius * 0.68f;

        Vector2 Project(Vector3 d)
        {
            var v = _view.MultiplyVector(d);
            if (Mirror) v.x = -v.x;
            return GizmoCenter() + new Vector2(v.x, -v.y) * CubeHalf;
        }

        /// <summary>면 i(_axes 순서)의 네 꼭짓점 (GUI 좌표).</summary>
        void CubeFace(int i, Vector2[] q)
        {
            var n = _axes[i].Dir;
            var a = Mathf.Abs(n.x) > 0.5f ? Vector3.up : Vector3.right;
            var b = Vector3.Cross(n, a);
            q[0] = Project(n + a + b); q[1] = Project(n + a - b); q[2] = Project(n - a - b); q[3] = Project(n - a + b);
        }

        static bool InQuad(Vector2 p, Vector2[] q)
        {
            float sign = 0;
            for (int i = 0; i < 4; i++)
            {
                var e = q[(i + 1) % 4] - q[i]; var w = p - q[i];
                float cr = e.x * w.y - e.y * w.x;
                if (Mathf.Abs(cr) < 1e-6f) continue;
                if (sign == 0) sign = Mathf.Sign(cr);
                else if (Mathf.Sign(cr) != sign) return false;
            }
            return true;
        }

        void DrawCube(Vector2 c, List<Axis> order)
        {
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            _overlayAlways.SetPass(0);
            Disc(c, GizmoRadius + PosDisc + 4 * S, new Color(0, 0, 0, _hover != null ? 0.24f : 0.12f));
            var faces = new List<int>(3);
            for (int i = 0; i < 6; i++) if (_axes[i].Depth > 0.02f) faces.Add(i);
            faces.Sort((x, y) => _axes[x].Depth.CompareTo(_axes[y].Depth));
            // 축 표시: 큐브 한 모서리에서 모서리를 따라 밖으로 나가는 X·Y·Z 선 (면이 덮고 바깥으로 나온 끝만 보인다)
            var corner = Project(new Vector3(-1, -1, -1));
            foreach (var a in order)
            {
                if (!a.Positive) continue;
                var end = Project(new Vector3(-1, -1, -1) + a.Dir * 2.6f);
                Line(corner, end, a.Color, 2.2f * S);
            }
            var q = _quad;
            foreach (int i in faces)
            {
                var a = _axes[i];
                CubeFace(i, q);
                // 면: 회색에 축 색을 살짝, 정면을 향할수록 밝게. 마우스 아래는 축 색을 진하게
                float lit = Mathf.Lerp(0.62f, 1f, a.Depth);
                var face = Color.Lerp(new Color(0.30f, 0.32f, 0.36f), a.Color, a.Positive ? 0.22f : 0.12f) * lit;
                if (_hover == a.Id) face = Color.Lerp(face, a.Color, 0.55f);
                face.a = 0.94f;
                GL.Begin(GL.QUADS); GL.Color(face);
                for (int k = 0; k < 4; k++) GL.Vertex3(q[k].x, q[k].y, 0);
                GL.End();
                var edge = new Color(0.08f, 0.09f, 0.11f, 0.85f);
                for (int k = 0; k < 4; k++) Line(q[k], q[(k + 1) % 4], edge, 1.4f * S);
            }
            GL.PopMatrix();
            var keep = _axisStyle.fontSize;
            _axisStyle.fontSize = Mathf.RoundToInt(10 * S);
            foreach (int i in faces)
            {
                var a = _axes[i];
                if (a.Depth < 0.35f) continue;
                CubeFace(i, q);
                var mid = (q[0] + q[1] + q[2] + q[3]) / 4;
                var col = Color.white; col.a = Mathf.InverseLerp(0.35f, 0.75f, a.Depth);
                _axisStyle.normal.textColor = col;
                GUI.Label(new Rect(mid.x - 24 * S, mid.y - 10 * S, 48 * S, 20 * S), FaceNames[a.Id], _axisStyle);
            }
            _axisStyle.normal.textColor = Color.white;
            _axisStyle.fontSize = keep;
        }

        void DrawModeLabel(Vector2 c)
        {
            var text = ViewName() + "  ·  " + (_orbit.Orthographic ? "직교" : "원근");
            var size = _modeStyle.CalcSize(new GUIContent(text));
            _modeRect = new Rect(c.x - size.x / 2 - 6 * S, c.y + GizmoRadius + PosDisc + 6 * S, size.x + 12 * S, size.y + 4 * S);
            if (_hover == "mode")
            {
                GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); _overlayAlways.SetPass(0);
                Rect(_modeRect, new Color(0, 0, 0, 0.3f)); GL.PopMatrix();
            }
            GUI.Label(_modeRect, text, _modeStyle);
        }

        /// <summary>지금 보는 방향 이름 (축에 거의 맞을 때만).</summary>
        string ViewName()
        {
            float yaw = Mathf.Repeat(_orbit.Yaw, 360), pitch = _orbit.Pitch;
            if (pitch > 88) return "위";
            if (pitch < -88) return "아래";
            if (Mathf.Abs(pitch) > 2) return "자유";
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, 180)) < 2) return "정면";
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, 0)) < 2) return "후면";
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, 270)) < 2) return "오른쪽";
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, 90)) < 2) return "왼쪽";
            return "자유";
        }

        static void Disc(Vector2 c, float r, Color col, int seg = 24)
        {
            GL.Begin(GL.TRIANGLES); GL.Color(col);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                GL.Vertex3(c.x, c.y, 0);
                GL.Vertex3(c.x + Mathf.Cos(a0) * r, c.y + Mathf.Sin(a0) * r, 0);
                GL.Vertex3(c.x + Mathf.Cos(a1) * r, c.y + Mathf.Sin(a1) * r, 0);
            }
            GL.End();
        }

        static void Line(Vector2 a, Vector2 b, Color col, float w)
        {
            var d = b - a; if (d.sqrMagnitude < 1e-4f) return;
            var n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            GL.Begin(GL.QUADS); GL.Color(col);
            GL.Vertex3(a.x + n.x, a.y + n.y, 0); GL.Vertex3(b.x + n.x, b.y + n.y, 0);
            GL.Vertex3(b.x - n.x, b.y - n.y, 0); GL.Vertex3(a.x - n.x, a.y - n.y, 0);
            GL.End();
        }

        /// 촬영은 세로 시야각을 그대로 두고 가로를 비율대로 넓히거나 줄인다 → 틀 = 화면 높이 × 비율, 가운데 맞춤.
        /// 틀이 화면보다 좁으면 바깥을 어둡게, 넓으면 화면 밖으로 나가는 선은 잘린다.
        void DrawThirds()
        {
            float w = Screen.width, h = Screen.height;
            float fw = FrameAspect > 0 ? h * FrameAspect : w, x0 = (w - fw) * 0.5f;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, w, h, 0); _overlayAlways.SetPass(0);
            if (x0 > 0.5f)
            {
                var dim = new Color(0, 0, 0, 0.35f);
                Rect(new Rect(0, 0, x0, h), dim); Rect(new Rect(x0 + fw, 0, x0, h), dim);
            }
            GL.Begin(GL.LINES);
            GL.Color(new Color(1, 1, 1, 0.5f));
            for (int i = 1; i <= 2; i++)
            {
                float x = x0 + fw * i / 3f, y = h * i / 3f;
                GL.Vertex3(x, 0, 0); GL.Vertex3(x, h, 0);
                GL.Vertex3(Mathf.Max(0, x0), y, 0); GL.Vertex3(Mathf.Min(w, x0 + fw), y, 0);
            }
            GL.End(); GL.PopMatrix();
        }

        static void Rect(Rect r, Color col)
        {
            GL.Begin(GL.QUADS); GL.Color(col);
            GL.Vertex3(r.xMin, r.yMin, 0); GL.Vertex3(r.xMax, r.yMin, 0); GL.Vertex3(r.xMax, r.yMax, 0); GL.Vertex3(r.xMin, r.yMax, 0);
            GL.End();
        }
    }
}
