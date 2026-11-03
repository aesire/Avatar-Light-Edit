// VR 에디터 창 (PLAN 3.12, 2026-10-24): Host 창을 그린 화면을 SteamVR 오버레이로 띄우고, 레이저로 누른 자리를 Host로 돌려보낸다.
//   - 화면: Host가 창을 그려(BGRA, 위에서 아래로) 공유 메모리에 쓰고 vrPanel { id, map, width, height, frame }을 보낸다 → 텍스처로 올리고 vrPanelShown { id, frame }.
//     Host는 그 답을 받아야 다음 화면을 쓴다 (쓰는 중에 읽지 않게). D3D 텍스처는 첫 줄이 위라서 뒤집지 않고 보낸다 (손잡이·레이저 텍스처도 같은 규칙).
//   - 자리: 방(SteamVR 서 있는 원점) 기준 실제 m — 스틱으로 걸어도 방과 함께 따라오고, 아바타 크기(방 배율)와 상관없이 실제 크기.
//   - 열고 닫기 (VRChat 빠른 메뉴처럼, 2026-10-24 사용자): B/Y 짧게 = 에디터 창 열기·닫기. 열 때마다 메인 창을 내 앞 0.75 m로 부르고
//     떠 있는 창은 메인 창과 같이 옮긴다(배치 유지). B/Y 길게(0.45초) = 그 손에 붙는 빠른 설정(id "hand", Host가 그리는 작은 사각 창) 열기·닫기.
//     빠른 설정이 열려 있으면 짧게 눌러도 그것만 닫는다. 열림 상태는 vrPanelsOpen { editor, hand }로 Host에 알린다 (Host는 닫힌 창은 그리지 않는다).
//     메뉴 단추는 여기서 가져가므로 VrView는 받지 않는다 .
//   - 레이저: SteamVR Input "aim" 자세(컨트롤러 끝). SteamVR 오버레이 레이저(마우스 입력 방식)는 장면 앱의 조작과 다투므로 직접 쏜다.
//     창에 닿을 때만 레이저·커서를 보인다(둘 다 오버레이). 트리거 = 누르기, 그 손 스틱 위·아래 = 휠(그동안 이동·회전 안 함).
//     트리거를 당긴 손이 레이저를 가져간다. 누른 채 창 밖으로 나가도 놓을 때까지 그 창에 보낸다(끌기). 빠른 설정은 그 창을 단 손의 레이저로는 누르지 않는다.
//   - 창 여러 개: id "main"이 메인 창, 나머지는 떠 있는 창(메인 창보다 위). 떠 있는 창은 처음에 메인 창 옆(오른쪽 → 왼쪽 → 한 칸 더 바깥)에.
//   - 옮기기·크기 (SteamVR 데스크톱 창처럼, 2026-10-24 사용자): 창을 가리키면 창 아래에 손잡이 줄이 나온다 —
//     왼쪽 구석 = 닫기(메인 창은 에디터 닫기, 떠 있는 창은 작업 창에 다시 붙이기 → vrPanelAction close) · 머리 따라오기 켜고 끄기,
//     가운데 손잡이 = 트리거로 끌어 옮기기(창은 늘 나를 봄, 끄는 동안 그 손 스틱 위·아래 = 멀리·가까이, 왼쪽·오른쪽 = 작게·크게),
//     오른쪽 모서리 = 트리거로 끌어 크기(왼쪽 위 모서리 고정), 그 왼쪽 기울이기 = 트리거로 끌어 각도(2026-10-25 사용자: 레이저 위·아래 = 앞뒤로 기울이기,
//     아래로 = 책상처럼 눕힘, 왼·오른 = 돌리기, 2.5배 · 움직이지 않고 눌렀다 떼면 = 기울기 없애기). 위·아래 기울기는 창마다 Tilt로 남겨 옮기거나 놓거나 다시 불러도 그대로. 그 밖에 창을 가리키고 그립 = 잡아 옮기기(쥔 채 스틱 위·아래 = 밀고 당기기),
//     두 손 그립 = 크기·자리·기울기. 놓으면 똑바로 세우고 나를 보게(FaceMe). 오른손으로 잡아 왼손 가까이(20 cm) 놓으면 왼손목에 붙는다(내 쪽을 볼 때만 보임).
//   - 창 상태(자리·크기 배율·붙이기)는 바뀔 때마다 vrPanelPlaced { id, state }로 알려 Host가 창마다 저장하고, 다음에 vrPanel { state }로 돌려준다.
//   - 트리거 반 당김(0.2 넘게) = 커서를 그 자리에 멈춤(Freeze). 레이저를 2.5° 넘게 움직이거나 트리거를 놓으면 다시 따라온다.
//   - VR 카메라 조작판 (id "camera", 2026-10-25): 카메라가 켜져 있으면 에디터 창 열림과 상관없이 카메라 몸체 옆에 붙어 보인다 (VrView가 Cam* 자리를 넣음).
//     카메라를 쥔 손은 레이저를 쏘지 않는다(그 손 트리거 = 셔터). 옮기기·잡기·손잡이 줄·저장 없음.
//   - 가상 키보드: Host가 입력 칸 포커스를 보고 vrKeyboard { show, text }를 보내면 그 창 오버레이에 SteamVR 키보드를 띄운다.
//     친 글은 오버레이 이벤트(KeyboardCharInput, UTF-8 조각 — 전체 글 읽기 함수는 ANSI로 바뀌어 한글이 깨진다)를 처음 글에 이어 붙여(\b = 지우기)
//     바뀔 때마다 vrKeyboardText { text }, 끝내면 { done }, 닫으면 { cancel }.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class VrPanels
    {
        sealed class Panel
        {
            public string Id = "";
            public ulong Handle;
            public Texture2D Tex;
            public RenderTexture Mips;                       // Tex를 GPU에서 복사해 밉맵까지 만든 것 (오버레이에는 이걸 — 고개를 돌릴 때 글자가 일렁이지 않게)
            public bool TexDirty, Placed, Summon, Beside, Grabbed, Following, Visible, MetersDirty = true, PoseDirty = true;
            public float Meters = 1, Scale = 1;              // 너비 = Host가 정한 m × 내가 바꾼 배율
            public float Tilt;                               // 기울이기로 더한 위·아래 각도 (°, 나를 보는 각도 기준 — 놓거나 다시 불러도 그대로)
            public string Mode = "world";                    // world | wrist | head (빠른 설정은 hand)
            public Vector3 Pos, LocalPos;                     // Local = 붙인 것(왼손 컨트롤러 · 머리 방향 · 빠른 설정을 단 손) 기준
            public Quaternion Rot = Quaternion.identity, LocalRot = Quaternion.identity;
            public uint Device = uint.MaxValue;               // 지금 오버레이를 붙여 둔 장치
            public string Map = "";
            public IntPtr MapHandle, View;
            public int Width, Height;
            public bool Main => Id == "main";
            public bool Hand => Id == "hand";
            public bool Cam => Id == "camera";
            /// <summary>손·카메라에 붙어 있어 옮기기·저장·손잡이 줄이 없는 창.</summary>
            public bool Attached => Hand || Cam;
            public float Aspect => Width > 0 ? (float)Height / Width : 0.6f;
            public float W => Meters * Scale;
            public float H => W * Aspect;
        }

        sealed class Grab
        {
            public Panel P;
            public int Hand, Other = -1;
            public Vector3 LocalPos;               // 잡은 손(레이저 자세) 기준
            public Quaternion LocalRot;
            public float D0, S0;                   // 두 손: 처음 손 사이 거리 · 배율
            public Vector3 Mid0, Pos0, V0;
            public Quaternion Rot0;
        }

        enum Part { None, Close, Pin, Handle, Tilt, Resize }

        /// <summary>트리거로 끄는 중 (손잡이 = 옮기기, 기울이기 = 각도, 모서리 = 크기).</summary>
        sealed class Drag
        {
            public Panel P;
            public Part Part;
            public int Hand;
            public float Dist, GrabX, S0, R0;
            public Vector3 Anchor;                 // 크기: 고정한 왼쪽 위 모서리
            public float Tilt;                     // 옮기기: 나를 보는 각도에 더한 위·아래 기울기 (°, 옮겨도 그대로)
            public Vector3 Dir0;                   // 기울이기: 누른 순간 레이저 방향 · 창 위아래 각도·방향(°) · 가장 많이 움직인 각도
            public float Elev0, Yaw0, Moved;
        }

        public Action<JNode> Send = _ => { };
        /// <summary>놓을 때 창이 나를 보게 (끄면 기울기만 없앤다).</summary>
        public bool FaceMe = true;
        /// <summary>트리거 반 당김 = 커서 멈춤.</summary>
        public bool Freeze = true;
        /// <summary>VrView가 넣는다: VR 카메라 조작판(id "camera") — 카메라가 켜졌는지 · 몸체 자리(방 기준) · 잡은 손(-1 = 없음 — 그 손 레이저는 창을 누르지 않는다, 트리거 = 셔터)과
        /// 그 손 기준 몸체 자리 · 조작판 쪽(-1 = 화면에서 보아 왼쪽). 조작판은 몸체 옆, 위를 맞추고 화면과 같은 쪽을 본다.</summary>
        public bool CamOn;
        /// <summary>VrBoneGrab: 이 손이 가까이 잡을 것(관절·아바타)을 가리키면 그립으로 창을 잡지 않는다 · 관절을 가리키면 레이저도 끈다 (2026-10-25).</summary>
        public Func<int, bool> NearGrab = _ => false, NearBone = _ => false;
        public Vector3 CamPos, CamHandPos;
        public Quaternion CamRot = Quaternion.identity, CamHandRot = Quaternion.identity;
        public int CamHand = -1;
        public float CamSide = -1, CamSize = 1;
        const float CamGap = 0.012f;
        readonly Dictionary<string, Panel> _panels = new Dictionary<string, Panel>();
        readonly List<Panel> _order = new List<Panel>();
        ulong _laser, _cursor, _icons, _handle, _tilt, _grip;
        Texture2D _laserTex, _cursorTex, _iconsTex, _handleTex, _tiltTex, _gripTex;
        bool _active;
        string _problem = "";
        // 열림: 에디터 창 · 빠른 설정(어느 손) · 메뉴 단추 누른 시간
        bool _open, _handOpen, _summonGroup, _menuLong;
        int _handSide = 1;
        float _menuHeld = -1;
        const float LongPress = 0.45f;
        // 레이저 상태: 지금 레이저를 가진 손 · 닿은 창 · 누른 창(놓을 때까지)
        int _hand = 1, _pressHand = -1;
        Panel _hover, _pressed;
        Vector2 _uv, _sentUv = new Vector2(-1, -1);
        float _sentAt, _wheel;
        Grab _grab;
        Drag _drag;
        // 창 아래 손잡이 줄
        Panel _chromePanel;
        float _chromeUntil;
        Part _chromeHover;
        string _chromeKey = "";
        // 트리거 반 당김 커서 멈춤
        bool _lock;
        Panel _lockPanel;
        Vector2 _lockUv;
        Vector3 _lockDir;
        int _lockHand = -1;
        Part _chromeLock;
        Panel _chromeLockPanel;
        Vector3 _chromeLockDir;
        int _chromeLockHand = -1;
        const float LockOn = 0.2f, LockOff = 0.1f, LockAngle = 2.5f;
        // 가상 키보드
        Panel _kbPanel;
        string _kbText = "";
        bool _kbMulti;
        static readonly uint EventSize = (uint)Marshal.SizeOf(typeof(VREvent_t));

        enum Kind { None, Panel, Chrome }
        struct Hit { public Kind Kind; public Panel P; public Part Part; public Vector2 Uv; public Vector3 O, At, Dir; public float T; }
        readonly Hit[] _hits = new Hit[2];

        const float Distance = 0.75f, BelowEye = 0.12f, MaxRay = 6f, LaserWidth = 0.004f, CursorSize = 0.014f, WristNear = 0.2f, Linger = 1.2f;
        // 손잡이 줄 (m): 왼쪽 구석 아이콘 두 칸 · 가운데 손잡이 · 오른쪽 크기 모서리
        const float Gap = 0.012f, IconCell = 0.04f, HandleW = 0.14f, HandleH = 0.02f, GripS = 0.04f;
        const int IconTex = 64, HandleTexW = 256, HandleTexH = 36, LaserTexW = 8, LaserTexH = 128;
        const float HandMenuW = 0.15f;
        // 기울이기: 레이저 각도 × TiltGain = 창 각도, 위·아래는 ±MaxTilt°까지
        const float TiltGain = 2.5f, MaxTilt = 80f;
        // 빠른 설정: 그 손 컨트롤러 위, 내 쪽으로 기울여 (컨트롤러 기준 m) ⓘ 헤드셋에서 맞출 값
        static readonly Vector3 HandMenuPos = new Vector3(0, 0.09f, 0.0f);
        static readonly Quaternion HandMenuRot = Quaternion.LookRotation(new Vector3(0, -0.6f, 0.8f), new Vector3(0, 0.8f, 0.6f));

        /// <summary>보이는 창 수 (상태 표시용).</summary>
        public int Shown { get; private set; }
        /// <summary>레이저가 창 위에 있거나 누른 채 끄는 중·잡는 중 (장면 조작이 레이저를 쓰지 않게).</summary>
        public bool Pointing => _hover != null || _pressed != null || _grab != null || _drag != null;
        public string Problem => _problem;

        /// <summary>vrPanel { id, map?, width?, height?, seq?, meters?, summon?, beside?, state?: 저장해 둔 상태(아직 안 놓았을 때만), remove? }
        /// · id "*": { open?: 에디터 창 열기·닫기, hand?: 빠른 설정 열기·닫기, faceMe?, freeze?, dump?: 시험용 그림 폴더 }</summary>
        public void Handle(JNode m)
        {
            var id = m["id"].AsString;
            if (id.Length == 0) return;
            if (id == "*")
            {
                if (m.Has("faceMe")) FaceMe = m["faceMe"].AsBool;
                if (m.Has("freeze")) { Freeze = m["freeze"].AsBool; if (!Freeze) _lock = false; }
                if (m.Has("open")) SetOpen(m["open"].AsBool);
                if (m.Has("hand")) SetHand(m["hand"].AsBool, _handSide);
                if (m["dump"].AsString.Length > 0) DumpDrawings(m["dump"].AsString);
                return;
            }
            if (m["remove"].AsBool)
            {
                if (_panels.TryGetValue(id, out var gone))
                {
                    if (_hover == gone) _hover = null;
                    if (_pressed == gone) { _pressed = null; _pressHand = -1; }
                    if (_grab?.P == gone) _grab = null;
                    if (_drag?.P == gone) _drag = null;
                    if (_chromePanel == gone) _chromePanel = null;
                    if (_lockPanel == gone) _lock = false;
                    if (_kbPanel == gone) HideKeyboard();
                    Destroy(gone); CloseMap(gone); _panels.Remove(id); _order.Remove(gone);
                    if (gone.Mips != null) { gone.Mips.Release(); UnityEngine.Object.Destroy(gone.Mips); }
                    if (gone.Tex != null) UnityEngine.Object.Destroy(gone.Tex);
                }
                return;
            }
            if (!_panels.TryGetValue(id, out var p)) { _panels[id] = p = new Panel { Id = id, Mode = id == "hand" ? "hand" : id == "camera" ? "camera" : "world" }; _order.Add(p); Debug.Log($"[VrcProto] VR 창 {id}: 첫 화면 {m["width"].AsInt}×{m["height"].AsInt} (frame {m["frame"].AsInt})"); }
            if (m.Has("meters") && Mathf.Abs(m["meters"].AsFloat - p.Meters) > 1e-5f) { p.Meters = Mathf.Clamp(m["meters"].AsFloat, 0.1f, 3f); p.MetersDirty = true; }
            if (m["summon"].AsBool) { p.Summon = true; if (p.Main) SetOpen(true); }
            if (m["beside"].AsBool) p.Beside = true;
            if (m["state"].Kind == JKind.Object && !p.Placed && !p.Attached) ApplyState(p, m["state"]);
            if (m.Has("frame"))   // "seq"는 파이프가 메시지마다 덮어쓰는 번호라 쓰면 안 된다
            {
                int w = m["width"].AsInt, h = m["height"].AsInt;
                if (Upload(p, m["map"].AsString, w, h) && Mathf.Abs(p.Aspect - (float)h / w) > 1e-4f) p.MetersDirty = true;
                Send(Msg.Make(Msg.VrPanelShown).Set("id", id).Set("frame", m["frame"].AsInt));   // 못 올렸어도 답한다 (Host가 기다리지 않게)
            }
        }

        // ---- 열고 닫기 ----
        void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            if (open) _summonGroup = true;
            else { if (_hover != null && !_hover.Attached) { Pointer(_hover, "leave", _uv); _hover = null; } _drag = null; if (_grab != null && !_grab.P.Hand) { _grab.P.Grabbed = false; _grab = null; } }
            SendOpen();
        }

        void SetHand(bool open, int side)
        {
            if (_handOpen == open && _handSide == side) return;
            _handOpen = open; _handSide = side;
            if (_panels.TryGetValue("hand", out var hp)) { hp.PoseDirty = true; if (!open && _hover == hp) { Pointer(hp, "leave", _uv); _hover = null; } }
            SendOpen();
        }

        void SendOpen() => Send(Msg.Make(Msg.VrPanelsOpen).Set("editor", _open).Set("hand", _handOpen));

        /// <summary>B/Y: 짧게 = 에디터 창 (빠른 설정이 열려 있으면 그것만 닫기), 길게 = 그 손에 빠른 설정.</summary>
        void MenuButton(VrInput input, float dt)
        {
            int side = input.MenuH[1] ? 1 : input.MenuH[0] ? 0 : _handSide;
            if (input.MenuDown) { _menuHeld = 0; _menuLong = false; }
            else if (input.Menu && _menuHeld >= 0)
            {
                _menuHeld += dt;
                if (!_menuLong && _menuHeld >= LongPress) { _menuLong = true; SetHand(!(_handOpen && _handSide == side), side); }
            }
            if (input.MenuUp && _menuHeld >= 0)
            {
                if (!_menuLong) { if (_handOpen) SetHand(false, _handSide); else SetOpen(!_open); }
                _menuHeld = -1;
            }
            input.Menu = input.MenuDown = input.MenuUp = false;   // 아래(VrView 크기 조절 등)가 받지 않게
        }

        /// <summary>vrKeyboard { id, show?, text?, multiline?, password?, title? } | { id, hide }</summary>
        public void Keyboard(JNode m)
        {
            var ov = OpenVR.Overlay;
            if (m["hide"].AsBool) { HideKeyboard(); return; }
            if (!m["show"].AsBool || ov == null || !_panels.TryGetValue(m["id"].AsString, out var p) || p.Handle == 0) return;
            _kbText = m["text"].AsString; _kbMulti = m["multiline"].AsBool;
            var err = ov.ShowKeyboardForOverlay(p.Handle, m["password"].AsBool ? (int)EGamepadTextInputMode.k_EGamepadTextInputModePassword : (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                _kbMulti ? (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeMultipleLines : (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
                0, m["title"].AsString, 1024, _kbText, 0);
            if (err != EVROverlayError.None) { _problem = "SteamVR 키보드를 띄우지 못했어요 (" + err + ")"; return; }
            _kbPanel = p;
        }

        void HideKeyboard()
        {
            if (_kbPanel == null) return;
            _kbPanel = null;
            try { OpenVR.Overlay?.HideKeyboard(); } catch (Exception) { }
        }

        /// <summary>키보드를 띄운 창 오버레이의 이벤트: 친 글자를 이어 붙여 Host로 (끝냄·닫음도).</summary>
        void PollKeyboard(CVROverlay ov)
        {
            var p = _kbPanel;
            if (p == null || p.Handle == 0) return;
            var ev = new VREvent_t();
            bool changed = false;
            while (_kbPanel == p && ov.PollNextOverlayEvent(p.Handle, ref ev, EventSize))
            {
                switch ((EVREventType)ev.eventType)
                {
                    case EVREventType.VREvent_KeyboardCharInput:
                        var k = ev.data.keyboard;
                        var bytes = new[] { k.cNewInput0, k.cNewInput1, k.cNewInput2, k.cNewInput3, k.cNewInput4, k.cNewInput5, k.cNewInput6, k.cNewInput7 };
                        int n = Array.IndexOf(bytes, (byte)0); if (n < 0) n = 8;
                        foreach (var c in System.Text.Encoding.UTF8.GetString(bytes, 0, n))
                        {
                            if (c == '\b') { if (_kbText.Length > 0) _kbText = _kbText.Substring(0, char.IsLowSurrogate(_kbText[_kbText.Length - 1]) && _kbText.Length > 1 ? _kbText.Length - 2 : _kbText.Length - 1); }
                            else if (c == '\n' || c == '\r') { if (_kbMulti) _kbText += "\n"; }
                            else if (!char.IsControl(c)) _kbText += c;
                        }
                        changed = true;
                        break;
                    case EVREventType.VREvent_KeyboardDone:
                        Send(Msg.Make(Msg.VrKeyboardText).Set("id", p.Id).Set("text", _kbText).Set("done", true));
                        _kbPanel = null; changed = false;
                        break;
                    case EVREventType.VREvent_KeyboardClosed:
                        Send(Msg.Make(Msg.VrKeyboardText).Set("id", p.Id).Set("text", _kbText).Set("cancel", true));
                        _kbPanel = null; changed = false;
                        break;
                }
            }
            if (changed) Send(Msg.Make(Msg.VrKeyboardText).Set("id", p.Id).Set("text", _kbText));
        }

        static Vector3 V3(JNode a, int i) => new Vector3(a[i].AsFloat, a[i + 1].AsFloat, a[i + 2].AsFloat);
        static Quaternion Q(JNode a, int i)
        {
            var q = new Quaternion(a[i].AsFloat, a[i + 1].AsFloat, a[i + 2].AsFloat, a[i + 3].AsFloat);
            return q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f ? q.normalized : Quaternion.identity;
        }
        static JNode Arr(Vector3 v) => JNode.Arr().Add(v.x).Add(v.y).Add(v.z);
        static JNode Arr(Quaternion q) => JNode.Arr().Add(q.x).Add(q.y).Add(q.z).Add(q.w);

        static void ApplyState(Panel p, JNode s)
        {
            if (s["pos"].Count != 3 || s["rot"].Count != 4) return;
            p.Pos = V3(s["pos"], 0); p.Rot = Q(s["rot"], 0);
            if (s.Has("scale")) p.Scale = Mathf.Clamp(s["scale"].AsFloat, 0.3f, 4f);
            p.Tilt = Mathf.Clamp(s["tilt"].AsFloat, -MaxTilt, MaxTilt);
            var mode = s["mode"].AsString;
            if ((mode == "wrist" || mode == "head") && s["localPos"].Count == 3 && s["localRot"].Count == 4) { p.Mode = mode; p.LocalPos = V3(s["localPos"], 0); p.LocalRot = Q(s["localRot"], 0); }
            p.Placed = true; p.PoseDirty = p.MetersDirty = true;
        }

        /// <summary>창 상태를 Host에 알린다 (Host가 창마다 저장해 다음에 돌려준다).</summary>
        void SendState(Panel p)
        {
            if (p.Attached) return;
            var s = JNode.Obj().Set("pos", Arr(p.Pos)).Set("rot", Arr(p.Rot)).Set("scale", p.Scale).Set("tilt", p.Tilt).Set("mode", p.Mode);
            if (p.Mode != "world") s.Set("localPos", Arr(p.LocalPos)).Set("localRot", Arr(p.LocalRot));
            Send(Msg.Make(Msg.VrPanelPlaced).Set("id", p.Id).Set("state", s));
        }

        bool Upload(Panel p, string map, int w, int h)
        {
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || map.Length == 0) return false;
            if (map != p.Map)
            {
                CloseMap(p);
                p.MapHandle = OpenFileMappingW(FILE_MAP_READ, false, map);
                if (p.MapHandle == IntPtr.Zero) { _problem = $"에디터 창 화면을 열지 못했어요 ({Marshal.GetLastWin32Error()})"; return false; }
                p.View = MapViewOfFile(p.MapHandle, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
                if (p.View == IntPtr.Zero) { _problem = $"에디터 창 화면을 읽지 못했어요 ({Marshal.GetLastWin32Error()})"; CloseMap(p); return false; }
                p.Map = map;
            }
            if (p.Tex == null || p.Tex.width != w || p.Tex.height != h)
            {
                if (p.Tex != null) UnityEngine.Object.Destroy(p.Tex);
                p.Tex = new Texture2D(w, h, TextureFormat.BGRA32, false, false) { name = "VrPanel_" + p.Id, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 4 };
            }
            p.Tex.LoadRawTextureData(p.View, w * h * 4);
            p.Tex.Apply(false, false);
            // 창 화면(m당 2048 px)이 헤드셋 화소보다 촘촘해 밉맵 없이 줄여 그리면 고개를 돌릴 때마다 글자가 일렁인다 → GPU에서 그대로 복사(뒤집힘 없음)하고 밉맵을 만든다
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.TextureToRT) != 0)
            {
                if (p.Mips == null || p.Mips.width != w || p.Mips.height != h)
                {
                    if (p.Mips != null) { p.Mips.Release(); UnityEngine.Object.Destroy(p.Mips); }
                    p.Mips = new RenderTexture(w, h, 0, RenderTextureFormat.BGRA32, RenderTextureReadWrite.Default) { name = "VrPanelMips_" + p.Id, useMipMap = true, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                    if (!p.Mips.Create()) { UnityEngine.Object.Destroy(p.Mips); p.Mips = null; }
                }
                if (p.Mips != null)
                {
                    Graphics.CopyTexture(p.Tex, 0, 0, p.Mips, 0, 0);
                    p.Mips.GenerateMips();
                }
            }
            p.Width = w; p.Height = h;
            p.TexDirty = true;
            _problem = "";
            return true;
        }

        /// <summary>VR로 보기를 시작할 때: 에디터 창은 닫힌 채 (B/Y로 연다, VRChat 빠른 메뉴처럼).</summary>
        public void Begin() { _active = true; _problem = ""; _open = _handOpen = false; _menuHeld = -1; SendOpen(); }

        /// <summary>VR로 보기를 끌 때: 오버레이만 지운다 (화면·자리는 남겨 두고 다시 켜면 그 자리에).</summary>
        public void End()
        {
            if (!_active) return;
            _active = false;
            HideKeyboard();
            foreach (var p in _panels.Values) Destroy(p);
            DestroyOverlay(ref _laser); DestroyOverlay(ref _cursor); DestroyOverlay(ref _icons); DestroyOverlay(ref _handle); DestroyOverlay(ref _tilt); DestroyOverlay(ref _grip);
            _hover = _pressed = _chromePanel = null; _pressHand = -1; _grab = null; _drag = null; _lock = false;
            _chromeKey = "";
            _open = _handOpen = false;
            SendOpen();
            Shown = 0;
        }

        /// <summary>VrView.Pre에서 입력 다음, 몸·이동·거울 전에 (메뉴 단추와 창을 가리키는 손의 스틱·그립을 가져간다).</summary>
        public void Tick(VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            if (!_active || head == null || !head.Tracked) return;
            var ov = OpenVR.Overlay;
            if (ov == null) { _problem = "SteamVR 오버레이를 열지 못했어요"; return; }
            MenuButton(input, dt);
            int shown = 0;
            _panels.TryGetValue("main", out var main);
            if (_summonGroup && (main == null || main.Tex != null)) { SummonGroup(main, head); _summonGroup = false; }
            // 메인 창 먼저 (떠 있는 창은 메인 창 옆에 놓는다)
            if (main != null) TickPanel(ov, main, main, head, left, right, dt, ref shown);
            foreach (var p in _order) if (p != main) TickPanel(ov, p, main, head, left, right, dt, ref shown);
            Shown = shown;
            PollKeyboard(ov);
            Interact(ov, input, head, left, right, dt);
            Chrome(ov);
        }

        /// <summary>에디터를 열 때: 메인 창을 내 앞으로 부르고, 방에 둔 떠 있는 창은 메인 창과 같이 옮긴다 (배치 그대로).</summary>
        void SummonGroup(Panel main, VrLink.Device head)
        {
            if (main == null || !main.Placed) return;   // 아직 안 놓았으면 처음 놓을 때 내 앞
            Vector3 oldPos = main.Pos; Quaternion oldRot = main.Rot;
            Place(main, head); Tilted(main);
            // 머리 따라오기는 다시 열어도 그대로 (내 앞에 부른 자리를 고개 방향 기준으로 다시 적는다), 손목에 붙였던 것은 방에
            if (main.Mode == "head") { var yaw = Yaw(head); main.LocalPos = Quaternion.Inverse(yaw) * (main.Pos - head.Position); main.LocalRot = Quaternion.Inverse(yaw) * main.Rot; main.Following = false; }
            else main.Mode = "world";
            var turn = main.Rot * Quaternion.Inverse(oldRot);
            foreach (var p in _order)
            {
                if (p == main || p.Attached || !p.Placed || p.Mode != "world") continue;
                p.Pos = main.Pos + turn * (p.Pos - oldPos);
                p.Rot = turn * p.Rot;
                p.PoseDirty = true;
                SendState(p);
            }
            SendState(main);
        }

        void TickPanel(CVROverlay ov, Panel p, Panel main, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt, ref int shown)
        {
            if (p.Tex == null) return;
            if (p.Handle == 0)
            {
                if (!Create(ov, "vrcproto.panel." + p.Id, "Avatar Light Edit", out p.Handle)) return;
                ov.SetOverlaySortOrder(p.Handle, p.Main ? 0u : p.Attached ? 2u : 1u);   // 떠 있는 창이 메인 창보다 위, 빠른 설정이 맨 위
            }
            if (p.Hand) { p.MetersDirty |= Mathf.Abs(p.Meters - HandMenuW) > 1e-4f; p.Meters = HandMenuW; p.Scale = 1; }
            else if (p.Cam) { if (p.Scale != 1) { p.Scale = 1; p.MetersDirty = true; } p.Placed = true; }
            else if (!p.Placed || p.Summon || p.Beside)
            {
                // 떠 있는 창: 처음이나 "옆으로"면 메인 창 옆 (메인 창이 아직 없으면 내 앞)
                bool beside = !p.Main && main != null && main.Placed && main.Tex != null && (!p.Placed || p.Beside);
                if (p.Summon || p.Beside) p.Mode = "world";
                if (beside) PlaceBeside(p, main, head); else Place(p, head);
                p.Placed = true; p.Summon = p.Beside = false;
                SendState(p);
            }
            Follow(p, head, left, right, dt);
            if (p.MetersDirty) { ov.SetOverlayWidthInMeters(p.Handle, p.W); p.MetersDirty = false; }
            if (p.PoseDirty)
            {
                var dev = p.Hand ? (_handSide == 0 ? left : right) : p.Cam ? (CamHand == 0 ? left : CamHand == 1 ? right : null) : p.Mode == "wrist" ? left : null;
                if (dev != null && !p.Grabbed)
                {
                    var mat = ToOpenVr(p.LocalPos, p.LocalRot);
                    ov.SetOverlayTransformTrackedDeviceRelative(p.Handle, dev.Index, ref mat);   // 컴포지터가 손을 따라 붙여 그린다 (늦지 않게)
                    p.Device = dev.Index;
                }
                else
                {
                    var mat = ToOpenVr(p.Pos, p.Rot);
                    ov.SetOverlayTransformAbsolute(p.Handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref mat);
                    p.Device = uint.MaxValue;
                }
                p.PoseDirty = false;
            }
            if (p.TexDirty)
            {
                var t = new Texture_t { handle = p.Mips != null ? p.Mips.GetNativeTexturePtr() : p.Tex.GetNativeTexturePtr(), eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
                var err = ov.SetOverlayTexture(p.Handle, ref t);
                if (err != EVROverlayError.None) _problem = "에디터 창 화면을 보내지 못했어요 (" + err + ")";
                p.TexDirty = false;
            }
            bool vis = p.Cam ? CamOn && p.Visible : p.Hand ? _handOpen && p.Visible : _open && (p.Mode != "wrist" || p.Grabbed || p.Visible);
            if (vis) { ov.ShowOverlay(p.Handle); if (!p.Attached) shown++; } else ov.HideOverlay(p.Handle);
            if (!vis && _hover == p) { Pointer(p, "leave", _uv); _hover = null; }
            if (p.Mode == "world" || p.Mode == "head") p.Visible = vis;   // 손목·빠른 설정은 Follow가
        }

        /// <summary>붙인 창의 지금 자리: 손목 = 왼손 컨트롤러 × 붙인 자리(내 쪽을 볼 때만 보임) · 머리 = 고개를 돌리면 천천히 따라옴 · 빠른 설정 = 그 손.</summary>
        void Follow(Panel p, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            if (p.Grabbed) return;
            if (p.Cam)
            {
                // 몸체 옆 (조작판 쪽 · 위 맞춤 · 화면 판과 같은 면) — 잡고 있으면 그 손 기준으로 붙여 늦지 않게
                var body = VrCamera.BodySize * CamSize;   // 카메라 크기 배율 (조작판은 크기 그대로)
                var local = new Vector3(CamSide * (body.x * 0.5f + CamGap + p.W * 0.5f), body.y * 0.5f - p.H * 0.5f, -body.z * 0.5f - 0.001f);
                var hd = CamHand == 0 ? left : CamHand == 1 ? right : null;
                p.Visible = CamOn;
                if (hd != null && hd.Tracked)
                {
                    var lp = CamHandPos + CamHandRot * local;
                    if (p.Device != hd.Index || (lp - p.LocalPos).sqrMagnitude > 1e-8f || Quaternion.Angle(p.LocalRot, CamHandRot) > 0.05f) { p.LocalPos = lp; p.LocalRot = CamHandRot; p.PoseDirty = true; }
                    p.Pos = hd.Position + hd.Rotation * lp; p.Rot = hd.Rotation * CamHandRot;
                }
                else
                {
                    var pos = CamPos + CamRot * local;
                    if (p.Device != uint.MaxValue || (pos - p.Pos).sqrMagnitude > 1e-8f || Quaternion.Angle(p.Rot, CamRot) > 0.05f) p.PoseDirty = true;
                    p.Pos = pos; p.Rot = CamRot;
                }
                return;
            }
            if (p.Hand)
            {
                var dev = _handSide == 0 ? left : right;
                if (dev == null || !dev.Tracked) { p.Visible = false; return; }
                if (p.LocalPos != HandMenuPos || p.Device != dev.Index) { p.LocalPos = HandMenuPos; p.LocalRot = HandMenuRot; p.PoseDirty = true; }
                p.Pos = dev.Position + dev.Rotation * p.LocalPos;
                p.Rot = dev.Rotation * p.LocalRot;
                p.Visible = true;
            }
            else if (p.Mode == "wrist")
            {
                if (left == null || !left.Tracked) { p.Visible = false; return; }
                p.Pos = left.Position + left.Rotation * p.LocalPos;
                p.Rot = left.Rotation * p.LocalRot;
                if (p.Device != left.Index) p.PoseDirty = true;
                // 판 앞면(나를 보는 쪽)이 내 머리 쪽을 볼 때만 — 손목을 돌려 볼 때
                var toHead = (head.Position - p.Pos).normalized;
                p.Visible = Vector3.Dot(-(p.Rot * Vector3.forward), toHead) > (p.Visible ? 0.25f : 0.45f);
            }
            else if (p.Mode == "head")
            {
                var yaw = Yaw(head);
                var tPos = head.Position + yaw * p.LocalPos;
                var tRot = yaw * p.LocalRot;
                if (!p.Following && (Vector3.Angle(p.Pos - head.Position, tPos - head.Position) > 18f || Vector3.Distance(p.Pos, tPos) > 0.25f)) p.Following = true;
                if (p.Following)
                {
                    float k = 1 - Mathf.Exp(-4f * dt);
                    p.Pos = Vector3.Lerp(p.Pos, tPos, k); p.Rot = Quaternion.Slerp(p.Rot, tRot, k);
                    p.PoseDirty = true;
                    if (Vector3.Distance(p.Pos, tPos) < 0.01f && Quaternion.Angle(p.Rot, tRot) < 1f) p.Following = false;
                }
            }
        }

        /// <summary>방향의 위·아래 각도 (°, 위 +) · 좌우 방향 (°, +Z에서 오른쪽 +).</summary>
        static float Elev(Vector3 v) => Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        static float YawOf(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        /// <summary>똑바로 위·아래면 좌우 방향을 알 수 없으니 지금 창 방향으로.</summary>
        static Vector3 Flat(Vector3 face, Quaternion rot) => new Vector2(face.x, face.z).sqrMagnitude < 1e-4f ? rot * Vector3.forward : face;

        /// <summary>나를 보게 세운 창에 기울이기로 맞춘 위·아래 각도를 다시 더한다.</summary>
        static void Tilted(Panel p)
        {
            if (Mathf.Abs(p.Tilt) < 0.01f) return;
            var f = p.Rot * Vector3.forward;
            p.Rot = Quaternion.Euler(-Mathf.Clamp(Elev(f) + p.Tilt, -MaxTilt, MaxTilt), YawOf(f), 0);
            p.PoseDirty = true;
        }

        static Quaternion Yaw(VrLink.Device head)
        {
            var f = head.Rotation * Vector3.forward; f.y = 0;
            return f.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        /// <summary>머리 따라오기 켜고 끄기: 지금 자리를 고개 방향 기준으로 적어 둔다.</summary>
        void ToggleFollow(Panel p, VrLink.Device head)
        {
            if (p.Mode == "head") p.Mode = "world";
            else { var yaw = Yaw(head); p.LocalPos = Quaternion.Inverse(yaw) * (p.Pos - head.Position); p.LocalRot = Quaternion.Inverse(yaw) * p.Rot; p.Following = false; p.Mode = "head"; }
            p.PoseDirty = true;
            SendState(p);
            _chromeKey = "";
        }

        // ---- 레이저 · 끌기 · 잡기 ----
        void Interact(CVROverlay ov, VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            if (_grab != null) { Grabbing(ov, input, head, left, right, dt); return; }
            if (_drag != null) { Dragging(ov, input, head, left, right, dt); return; }
            for (int h = 0; h < 2; h++)
            {
                bool ray = Ray(h, input, h == 0 ? left : right, out var o, out var d) && !(CamOn && h == CamHand)   // 카메라를 쥔 손은 레이저 없음 (트리거 = 셔터)
                    && !NearBone(h);   // 관절을 가리키는 손도 (뒤의 창이 트리거·그립을 가져가지 않게)
                _hits[h] = ray ? Cast(o, d, h) : default; _hits[h].Dir = d;
            }
            // 누른 채면 그 손·그 창 그대로 (창 밖으로 끌어도 놓을 때까지)
            if (_pressed != null)
            {
                int h = _pressHand;
                var hit = new Hit { Kind = Kind.Panel, P = _pressed };
                if (Ray(h, input, h == 0 ? left : right, out var o, out var d) && Plane(_pressed.Pos, _pressed.Rot, _pressed.W, _pressed.H, o, d, out var uv, out var at))
                {
                    _uv = Clamp01(uv); hit.O = o; hit.At = at;
                    // 멈춘 커서로 눌렀으면 레이저를 크게 움직일 때까지 그 자리 (누르는 순간의 떨림이 끌기가 되지 않게)
                    if (_lock && Vector3.Angle(d, _lockDir) > LockAngle) _lock = false;
                    if (_lock) { _uv = _lockUv; hit.At = PointOn(_pressed, _uv); }
                }
                else hit.Kind = Kind.None;
                Move(_pressed, _uv, false);
                if (!input.Use[h]) { Pointer(_pressed, "up", _uv); _pressed = null; _pressHand = -1; }
                Laser(ov, head, hit.Kind != Kind.None ? hit.O : (Vector3?)null, hit.At, _pressed ?? hit.P);
                return;
            }
            // 그립 = 잡기 (창을 가리키는 손, 빠른 설정은 빼고). 잡은 손의 그립은 거울 잡기 등이 받지 않게 가져간다
            for (int h = 0; h < 2; h++)
                if (input.GrabDown[h] && _hits[h].Kind == Kind.Panel && !_hits[h].P.Attached && !NearGrab(h))
                {
                    BeginGrab(h, _hits[h].P, input, h == 0 ? left : right);
                    input.Grab[h] = input.GrabDown[h] = false;
                    Grabbing(ov, input, head, left, right, dt);
                    return;
                }
            // 트리거를 당긴 손이 레이저를 가져간다. 지금 손이 아무것도 안 가리키면 닿은 다른 손으로
            for (int h = 0; h < 2; h++) if (input.UseDown[h] && _hits[h].Kind != Kind.None) _hand = h;
            if (_hits[_hand].Kind == Kind.None && _hits[1 - _hand].Kind != Kind.None) _hand = 1 - _hand;
            var hh = _hits[_hand];
            // 손잡이 줄도 트리거 반 당김이면 그 칸에 멈춤 — 당기다 손끝이 내려가 작은 아이콘을 놓치지 않게 (레이저를 크게 움직이거나 놓으면 풀림)
            if (_chromeLock != Part.None && (!Freeze || !ChromeShown || _chromeLockPanel != _chromePanel || _chromeLockHand != _hand
                || (input.Trigger[_hand] < LockOff && !input.Use[_hand]) || Vector3.Angle(hh.Dir, _chromeLockDir) > LockAngle * 2)) _chromeLock = Part.None;
            if (Freeze && _chromeLock == Part.None && hh.Kind == Kind.Chrome && input.Trigger[_hand] > LockOn && !input.Use[_hand])
            { _chromeLock = hh.Part; _chromeLockPanel = hh.P; _chromeLockHand = _hand; _chromeLockDir = hh.Dir; }
            if (_chromeLock != Part.None && Ray(_hand, input, _hand == 0 ? left : right, out var lo, out _))
                hh = new Hit { Kind = Kind.Chrome, P = _chromeLockPanel, Part = _chromeLock, O = lo, At = ChromeCenter(_chromeLockPanel, _chromeLock), Dir = hh.Dir };
            var onPanel = hh.Kind == Kind.Panel ? hh.P : null;
            if (_hover != null && _hover != onPanel) { Pointer(_hover, "leave", _uv); _sentUv = new Vector2(-1, -1); }
            _hover = onPanel;
            _chromeHover = hh.Kind == Kind.Chrome ? hh.Part : Part.None;
            if ((hh.Kind == Kind.Panel && !hh.P.Attached) || hh.Kind == Kind.Chrome) { _chromePanel = hh.P; _chromeUntil = Time.unscaledTime + Linger; }
            if (_hover != null)
            {
                _uv = hh.Uv;
                // 트리거 반 당김 = 커서 멈춤 (놓거나, 다른 창·손으로 가거나, 레이저를 2.5° 넘게 움직이면 풀림)
                if (Freeze)
                {
                    float pull = input.Trigger[_hand];
                    if (_lock && ((pull < LockOff && !input.Use[_hand]) || _lockPanel != _hover || _lockHand != _hand || Vector3.Angle(hh.Dir, _lockDir) > LockAngle)) _lock = false;
                    if (!_lock && pull > LockOn && !input.Use[_hand]) { _lock = true; _lockPanel = _hover; _lockUv = _uv; _lockDir = hh.Dir; _lockHand = _hand; }
                    if (_lock) { _uv = _lockUv; hh.At = PointOn(_hover, _uv); }
                }
                Move(_hover, _uv, false);
                if (input.UseDown[_hand]) { Move(_hover, _uv, true); Pointer(_hover, "down", _uv); _pressed = _hover; _pressHand = _hand; }
            }
            else _lock = false;
            if (hh.Kind == Kind.Chrome && input.UseDown[_hand]) { ChromeDown(hh, head); _chromeLock = Part.None; }
            // 휠: 창 위에서 그 손 스틱 위·아래 (오른손 = 회전 스틱, 왼손 = 이동 스틱) — 그동안 이동·회전하지 않는다
            if (hh.Kind != Kind.None)
            {
                var stick = _hand == 1 ? input.Turn : input.Move;
                if (_hand == 1) input.Turn = Vector2.zero; else input.Move = Vector2.zero;
                float y = _hover != null && Mathf.Abs(stick.y) > 0.2f ? stick.y : 0;
                _wheel += y * 8f * dt;   // 끝까지 밀면 1초에 8칸
                if (Mathf.Abs(_wheel) >= 0.25f) { Send(Msg.Make(Msg.VrPointer).Set("id", _hover.Id).Set("kind", "wheel").Set("u", _uv.x).Set("v", _uv.y).Set("delta", _wheel)); _wheel = 0; }
            }
            else _wheel = 0;
            Laser(ov, head, hh.Kind != Kind.None ? hh.O : (Vector3?)null, hh.At, hh.Kind == Kind.None ? null : hh.P);   // 손잡이 줄 위에도 커서 (어느 칸을 가리키는지 보이게)
        }

        /// <summary>손잡이 줄을 트리거로 눌렀을 때: 닫기 · 머리 따라오기 · 손잡이(옮기기) · 모서리(크기).</summary>
        void ChromeDown(Hit h, VrLink.Device head)
        {
            var p = h.P;
            switch (h.Part)
            {
                case Part.Close:
                    if (p.Main) SetOpen(false);
                    else Send(Msg.Make(Msg.VrPanelAction).Set("id", p.Id).Set("action", "close"));
                    break;
                case Part.Pin: ToggleFollow(p, head); break;
                case Part.Handle:
                    {
                        var handle = ChromeCenter(p, Part.Handle);
                        _drag = new Drag { P = p, Part = Part.Handle, Hand = _hand, Dist = Vector3.Distance(h.O, h.At), GrabX = Vector3.Dot(h.At - handle, p.Rot * Vector3.right),
                            Tilt = Elev(p.Rot * Vector3.forward) - Elev(h.At - head.Position) };
                        p.Grabbed = true; p.Following = false;
                        break;
                    }
                case Part.Tilt:
                    {
                        var f = p.Rot * Vector3.forward;
                        _drag = new Drag { P = p, Part = Part.Tilt, Hand = _hand, Dir0 = h.Dir, Elev0 = Elev(f), Yaw0 = YawOf(f) };
                        p.Grabbed = true; p.Following = false;
                        break;
                    }
                case Part.Resize:
                    {
                        var anchor = p.Pos + p.Rot * new Vector3(-p.W / 2, p.H / 2, 0);
                        _drag = new Drag { P = p, Part = Part.Resize, Hand = _hand, Anchor = anchor, S0 = p.Scale, R0 = Mathf.Max(0.05f, Vector3.Distance(h.At, anchor)) };
                        p.Grabbed = true;
                        break;
                    }
            }
        }

        /// <summary>트리거로 끄는 중: 손잡이 = 레이저 끝을 따라 옮기기(늘 나를 봄, 스틱 위·아래 = 멀리·가까이, 왼·오른 = 크기) · 모서리 = 크기.</summary>
        void Dragging(CVROverlay ov, VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            var g = _drag; var p = g.P;
            var dev = g.Hand == 0 ? left : right;
            if (!input.Use[g.Hand] || !Ray(g.Hand, input, dev, out var o, out var d))
            {
                _drag = null; p.Grabbed = false;
                // 기울이기를 움직이지 않고 눌렀다 떼면 = 기울기 없애고 다시 나를 보게
                if (g.Part == Part.Tilt && g.Moved < 1.5f) { p.Tilt = 0; p.Rot = Quaternion.LookRotation(Flat(p.Pos - head.Position, p.Rot), Vector3.up); }
                else if (g.Part == Part.Tilt) p.Tilt = Elev(p.Rot * Vector3.forward) - Elev(p.Pos - head.Position);
                if (p.Mode == "head") { var yaw = Yaw(head); p.LocalPos = Quaternion.Inverse(yaw) * (p.Pos - head.Position); p.LocalRot = Quaternion.Inverse(yaw) * p.Rot; }
                else if (p.Mode == "wrist") p.Mode = "world";
                p.PoseDirty = true;
                SendState(p);
                return;
            }
            var stick = g.Hand == 1 ? input.Turn : input.Move;
            if (g.Hand == 1) input.Turn = Vector2.zero; else input.Move = Vector2.zero;
            Vector3 laserEnd;
            if (g.Part == Part.Handle)
            {
                if (Mathf.Abs(stick.y) > 0.2f) g.Dist = Mathf.Clamp(g.Dist + stick.y * 1.5f * dt, 0.15f, 5f);
                if (Mathf.Abs(stick.x) > 0.2f) { p.Scale = Mathf.Clamp(p.Scale * Mathf.Exp(stick.x * 1.2f * dt), 0.3f, 4f); p.MetersDirty = true; }
                var target = o + d * g.Dist;
                var face = Flat(target - head.Position, p.Rot);
                // 나를 보되 기울이기로 맞춘 위·아래 각도는 그대로
                p.Rot = Quaternion.Euler(-Mathf.Clamp(Elev(face) + g.Tilt, -MaxTilt, MaxTilt), YawOf(face), 0);
                // 손잡이가 창 아래 가운데에 있으니 창 가운데는 그만큼 위 (잡은 자리 좌우도)
                p.Pos = target + p.Rot * new Vector3(-g.GrabX, p.H / 2 + Gap + IconCell / 2, 0);
                laserEnd = target;
            }
            else if (g.Part == Part.Tilt)
            {
                // 레이저를 위·아래로 = 창을 앞뒤로 기울이기 (아래로 = 책상처럼 눕힘), 왼쪽·오른쪽 = 돌리기. 손목을 조금만 움직여도 되게 TiltGain배
                float de = Elev(d) - Elev(g.Dir0), dy = Mathf.DeltaAngle(YawOf(g.Dir0), YawOf(d));
                g.Moved = Mathf.Max(g.Moved, Vector3.Angle(d, g.Dir0));
                p.Rot = Quaternion.Euler(-Mathf.Clamp(g.Elev0 + de * TiltGain, -MaxTilt, MaxTilt), g.Yaw0 + dy * TiltGain, 0);
                laserEnd = ChromeCenter(p, Part.Tilt);
            }
            else
            {
                if (Plane(p.Pos, p.Rot, p.W, p.H, o, d, out _, out var at))
                {
                    float r = Vector3.Distance(at, g.Anchor);
                    p.Scale = Mathf.Clamp(g.S0 * r / g.R0, 0.3f, 4f);
                    p.MetersDirty = true;
                    p.Pos = g.Anchor + p.Rot * new Vector3(p.W / 2, -p.H / 2, 0);
                    laserEnd = at;
                }
                else laserEnd = o + d * 0.5f;
            }
            p.PoseDirty = true;
            _chromePanel = p; _chromeUntil = Time.unscaledTime + Linger;
            _chromeHover = g.Part;
            Laser(ov, head, o, laserEnd, p, false);
        }

        /// <summary>손 자세 (잡기 기준): 레이저 자세가 있으면 그것, 없으면 컨트롤러.</summary>
        static bool HandPose(int h, VrInput input, VrLink.Device d, out Vector3 pos, out Quaternion rot)
        {
            if (input.HasAim[h]) { pos = input.AimPos[h]; rot = input.AimRot[h]; return true; }
            if (d != null && d.Tracked) { pos = d.Position; rot = d.Rotation; return true; }
            pos = default; rot = Quaternion.identity; return false;
        }

        void BeginGrab(int h, Panel p, VrInput input, VrLink.Device d)
        {
            if (!HandPose(h, input, d, out var cp, out var cr)) return;
            if (_hover != null) { Pointer(_hover, "leave", _uv); _hover = null; }
            _grab = new Grab { P = p, Hand = h, LocalPos = Quaternion.Inverse(cr) * (p.Pos - cp), LocalRot = Quaternion.Inverse(cr) * p.Rot };
            p.Grabbed = true; p.Following = false;
        }

        void Grabbing(CVROverlay ov, VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            var g = _grab; var p = g.P;
            VrLink.Device Dev(int h) => h == 0 ? left : right;
            bool held = input.Grab[g.Hand], otherHeld = g.Other >= 0 && input.Grab[g.Other];
            // 다른 손 그립을 새로 쥐면 두 손
            int other = 1 - g.Hand;
            if (g.Other < 0 && input.GrabDown[other] && HandPose(g.Hand, input, Dev(g.Hand), out var a0, out _) && HandPose(other, input, Dev(other), out var b0, out _))
            {
                g.Other = other; otherHeld = true;
                g.D0 = Mathf.Max(0.05f, Vector3.Distance(a0, b0)); g.S0 = p.Scale;
                g.Mid0 = (a0 + b0) * 0.5f; g.V0 = b0 - a0; g.Pos0 = p.Pos; g.Rot0 = p.Rot;
            }
            for (int h = 0; h < 2; h++) if (h == g.Hand || h == g.Other) input.Grab[h] = input.GrabDown[h] = false;   // 거울 잡기가 받지 않게
            if (g.Other >= 0 && !(held && otherHeld))
            {
                // 두 손 중 한 손을 놓으면 남은 손으로 계속 잡는다
                int keep = held ? g.Hand : otherHeld ? g.Other : -1;
                if (keep < 0) { Release(head, left, g.Hand); return; }
                if (HandPose(keep, input, Dev(keep), out var kp, out var kr)) { g.Hand = keep; g.Other = -1; g.LocalPos = Quaternion.Inverse(kr) * (p.Pos - kp); g.LocalRot = Quaternion.Inverse(kr) * p.Rot; }
                held = true;
            }
            if (!held && g.Other < 0) { Release(head, left, g.Hand); return; }
            if (g.Other >= 0)
            {
                if (HandPose(g.Hand, input, Dev(g.Hand), out var a, out _) && HandPose(g.Other, input, Dev(g.Other), out var b, out _))
                {
                    float s = Vector3.Distance(a, b) / g.D0;
                    var turn = Quaternion.FromToRotation(g.V0, b - a);
                    var mid = (a + b) * 0.5f;
                    float scale = Mathf.Clamp(g.S0 * s, 0.3f, 4f);
                    p.Pos = mid + turn * ((g.Pos0 - g.Mid0) * (scale / g.S0));
                    p.Rot = turn * g.Rot0;
                    if (!Mathf.Approximately(scale, p.Scale)) { p.Scale = scale; p.MetersDirty = true; }
                }
            }
            else if (HandPose(g.Hand, input, Dev(g.Hand), out var cp, out var cr))
            {
                // 쥔 채 그 손 스틱 위·아래 = 밀기·당기기 (그동안 이동·회전 안 함)
                var stick = g.Hand == 1 ? input.Turn : input.Move;
                if (g.Hand == 1) input.Turn = Vector2.zero; else input.Move = Vector2.zero;
                float y = Mathf.Abs(stick.y) > 0.2f ? stick.y : 0;
                if (y != 0) g.LocalPos = g.LocalPos.normalized * Mathf.Clamp(g.LocalPos.magnitude + y * 1.5f * dt, 0.1f, 5f);
                p.Pos = cp + cr * g.LocalPos;
                p.Rot = cr * g.LocalRot;
            }
            p.PoseDirty = true;
            _chromePanel = p; _chromeUntil = Time.unscaledTime + Linger;
            Ray(g.Hand, input, Dev(g.Hand), out var lo, out _);
            Laser(ov, head, lo, p.Pos, p, false);
        }

        /// <summary>놓기: 똑바로 세우고(나를 보게 · 끄면 기울기만 없앰), 오른손으로 왼손 가까이 놓았으면 왼손목에 붙인다.</summary>
        void Release(VrLink.Device head, VrLink.Device left, int hand)
        {
            var p = _grab.P;
            _grab = null;
            p.Grabbed = false;
            if (hand == 1 && left != null && left.Tracked && Vector3.Distance(p.Pos, left.Position) < WristNear)
            {
                p.Mode = "wrist";
                p.LocalPos = Quaternion.Inverse(left.Rotation) * (p.Pos - left.Position);
                p.LocalRot = Quaternion.Inverse(left.Rotation) * p.Rot;
                p.Visible = true;
            }
            else
            {
                var f = FaceMe ? p.Pos - head.Position : p.Rot * Vector3.forward;
                if (new Vector2(f.x, f.z).sqrMagnitude < 1e-4f) f = p.Rot * Vector3.up;   // 똑바로 위·아래를 보면 기울기만
                p.Rot = Quaternion.LookRotation(f, Vector3.up);
                if (FaceMe) Tilted(p);
                if (p.Mode == "head") { var yaw = Yaw(head); p.LocalPos = Quaternion.Inverse(yaw) * (p.Pos - head.Position); p.LocalRot = Quaternion.Inverse(yaw) * p.Rot; }
                else p.Mode = "world";
            }
            p.PoseDirty = true;
            SendState(p);
        }

        /// <summary>레이저가 닿는 것: 창 · 창 아래 손잡이 줄 중 가장 가까운 것. 빠른 설정은 그 창을 단 손으로는 누르지 않는다.</summary>
        Hit Cast(Vector3 o, Vector3 d, int hand)
        {
            var best = new Hit { T = MaxRay, O = o };
            foreach (var p in _order)
            {
                if (p.Handle == 0 || (!p.Placed && !p.Attached) || !p.Visible) continue;
                if (p.Cam) { if (!CamOn) continue; }
                else if (p.Hand) { if (!_handOpen || hand == _handSide) continue; }
                else if (!_open) continue;
                if (Plane(p.Pos, p.Rot, p.W, p.H, o, d, out var uv, out var at) && In01(uv) && Vector3.Distance(o, at) < best.T)
                    best = new Hit { Kind = Kind.Panel, P = p, Uv = uv, O = o, At = at, T = Vector3.Distance(o, at) };
            }
            if (ChromeShown)
            {
                var p = _chromePanel;
                foreach (var part in new[] { Part.Close, Part.Pin, Part.Handle, Part.Tilt, Part.Resize })
                {
                    var (w, h) = ChromeSize(part);
                    w += 0.006f; h += Gap;   // 누르는 자리는 그림보다 조금 넓게 (옆 칸과 틈 반씩 · 위아래로 틈 반씩)
                    if (Plane(ChromeCenter(p, part), p.Rot, w, h, o, d, out var uv, out var at) && In01(uv) && Vector3.Distance(o, at) < best.T)
                        best = new Hit { Kind = Kind.Chrome, P = p, Part = part, Uv = uv, O = o, At = at, T = Vector3.Distance(o, at) };
                }
            }
            return best;
        }

        static bool In01(Vector2 uv) => uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
        static Vector3 PointOn(Panel p, Vector2 uv) => p.Pos + p.Rot * new Vector3((uv.x - 0.5f) * p.W, (0.5f - uv.y) * p.H, 0);

        void Move(Panel p, Vector2 uv, bool force)
        {
            // 1/60초마다, 반 픽셀 넘게 움직였을 때만 (파이프를 채우지 않게)
            float px = p.Width > 0 ? 0.5f / p.Width : 0.0005f;
            if (!force && (Time.unscaledTime - _sentAt < 1f / 60 || (uv - _sentUv).sqrMagnitude < px * px)) return;
            Pointer(p, "move", uv);
        }

        void Pointer(Panel p, string kind, Vector2 uv)
        {
            Send(Msg.Make(Msg.VrPointer).Set("id", p.Id).Set("kind", kind).Set("u", uv.x).Set("v", uv.y));
            _sentUv = uv; _sentAt = Time.unscaledTime;
        }

        /// <summary>레이저 (방 좌표): aim 자세가 있으면 그것, 없으면 컨트롤러 자세 앞쪽 ⓘ.</summary>
        static bool Ray(int h, VrInput input, VrLink.Device d, out Vector3 o, out Vector3 dir)
        {
            if (h >= 0 && input.HasAim[h]) { o = input.AimPos[h]; dir = input.AimRot[h] * Vector3.forward; return true; }
            if (d != null && d.Tracked) { o = d.Position; dir = d.Rotation * Vector3.forward; return true; }
            o = dir = Vector3.zero;
            return false;
        }

        /// <summary>판(가운데 pos, 회전 rot, 너비·높이 m)과 만나는 점 · 판 안 좌표(0~1, 왼쪽 위 원점). 판 앞쪽(나를 보는 쪽)에서 들어올 때만.</summary>
        static bool Plane(Vector3 pos, Quaternion rot, float w, float h, Vector3 o, Vector3 d, out Vector2 uv, out Vector3 at)
        {
            uv = default; at = default;
            var f = rot * Vector3.forward;   // 판이 바라보는 쪽의 반대 (내 쪽에서 판으로)
            float den = Vector3.Dot(d, f);
            if (den < 1e-4f) return false;
            float t = Vector3.Dot(pos - o, f) / den;
            if (t <= 0 || t > MaxRay) return false;
            at = o + d * t;
            var local = at - pos;
            uv = new Vector2(Vector3.Dot(local, rot * Vector3.right) / w + 0.5f, 0.5f - Vector3.Dot(local, rot * Vector3.up) / h);
            return true;
        }

        static Vector2 Clamp01(Vector2 v) => new Vector2(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y));

        /// <summary>레이저(손 → 닿은 점, 가는 띠)와 커서(창 위의 점) — 둘 다 창보다 위에 그리는 오버레이.</summary>
        void Laser(CVROverlay ov, VrLink.Device head, Vector3? from, Vector3 to, Panel on, bool cursor = true)
        {
            if (_laser == 0) { if (!Create(ov, "vrcproto.laser", "Avatar Light Edit laser", out _laser)) return; Setup(ov, _laser, _laserTex ??= LaserTexture(), LaserWidth, 4); }
            if (_cursor == 0) { if (!Create(ov, "vrcproto.cursor", "Avatar Light Edit cursor", out _cursor)) return; Setup(ov, _cursor, _cursorTex ??= CursorTexture(), CursorSize, 5); }
            if (from == null) { ov.HideOverlay(_laser); ov.HideOverlay(_cursor); return; }
            var o = from.Value;
            var dir = to - o;
            float len = dir.magnitude;
            if (len < 0.02f) { ov.HideOverlay(_laser); }
            else
            {
                dir /= len;
                var mid = (o + to) * 0.5f;
                // 띠는 레이저 축(Y)을 따라 길고, 판(Z 반대쪽)은 내 머리를 본다
                var face = mid - head.Position;
                face -= dir * Vector3.Dot(face, dir);
                if (face.sqrMagnitude < 1e-8f) face = Vector3.Cross(dir, Vector3.right);
                var rot = Quaternion.LookRotation(face.normalized, dir);
                var mat = ToOpenVr(mid, rot);
                ov.SetOverlayTexelAspect(_laser, LaserWidth * LaserTexH / (LaserTexW * len));   // 높이(= 길이) = 너비 × 텍스처 세로/가로 ÷ 텍셀 비
                ov.SetOverlayTransformAbsolute(_laser, ETrackingUniverseOrigin.TrackingUniverseStanding, ref mat);
                ov.ShowOverlay(_laser);
            }
            if (!cursor || on == null) { ov.HideOverlay(_cursor); return; }
            var cm = ToOpenVr(to - on.Rot * Vector3.forward * 0.002f, on.Rot);
            ov.SetOverlayTransformAbsolute(_cursor, ETrackingUniverseOrigin.TrackingUniverseStanding, ref cm);
            ov.ShowOverlay(_cursor);
        }

        // ---- 창 아래 손잡이 줄: 왼쪽 구석 아이콘(닫기 · 머리 따라오기) · 가운데 손잡이 · 오른쪽 크기 모서리 ----
        bool ChromeShown => _open && _chromePanel != null && !_chromePanel.Hand && (Time.unscaledTime < _chromeUntil || _drag?.P == _chromePanel || _grab?.P == _chromePanel) && (_chromePanel.Visible || _chromePanel.Grabbed);

        static (float w, float h) ChromeSize(Part part) => part switch
        {
            Part.Close or Part.Pin or Part.Tilt => (IconCell, IconCell),
            Part.Handle => (HandleW, HandleH),
            _ => (GripS, GripS),
        };

        /// <summary>창 아래 한 줄 (창 기준): 닫기·따라오기는 왼쪽 끝, 손잡이는 가운데, 기울이기·크기 모서리는 오른쪽 끝.</summary>
        static Vector3 ChromeCenter(Panel p, Part part)
        {
            float y = -p.H / 2 - Gap - IconCell / 2;
            float x = part switch
            {
                Part.Close => -p.W / 2 + IconCell / 2,
                Part.Pin => -p.W / 2 + IconCell * 1.5f + 0.006f,
                Part.Handle => 0,
                Part.Tilt => p.W / 2 - GripS - 0.006f - IconCell / 2,
                _ => p.W / 2 - GripS / 2,
            };
            return p.Pos + p.Rot * new Vector3(x, y, 0);
        }

        void Chrome(CVROverlay ov)
        {
            if (_icons == 0)
            {
                if (!Create(ov, "vrcproto.chrome.icons", "Avatar Light Edit window buttons", out _icons) || !Create(ov, "vrcproto.chrome.handle", "Avatar Light Edit window handle", out _handle)
                    || !Create(ov, "vrcproto.chrome.tilt", "Avatar Light Edit window tilt", out _tilt) || !Create(ov, "vrcproto.chrome.grip", "Avatar Light Edit window resize", out _grip)) return;
                _iconsTex = new Texture2D(IconTex * 2, IconTex, TextureFormat.RGBA32, true, false) { name = "VrChromeIcons", wrapMode = TextureWrapMode.Clamp };
                _handleTex = new Texture2D(HandleTexW, HandleTexH, TextureFormat.RGBA32, true, false) { name = "VrChromeHandle", wrapMode = TextureWrapMode.Clamp };
                _tiltTex = new Texture2D(IconTex, IconTex, TextureFormat.RGBA32, true, false) { name = "VrChromeTilt", wrapMode = TextureWrapMode.Clamp };
                _gripTex = new Texture2D(IconTex, IconTex, TextureFormat.RGBA32, true, false) { name = "VrChromeGrip", wrapMode = TextureWrapMode.Clamp };
                ov.SetOverlayWidthInMeters(_icons, IconCell * 2 + 0.006f); ov.SetOverlayWidthInMeters(_handle, HandleW); ov.SetOverlayWidthInMeters(_tilt, IconCell); ov.SetOverlayWidthInMeters(_grip, GripS);
                foreach (var h in new[] { _icons, _handle, _tilt, _grip }) ov.SetOverlaySortOrder(h, 3);
            }
            if (!ChromeShown) { ov.HideOverlay(_icons); ov.HideOverlay(_handle); ov.HideOverlay(_tilt); ov.HideOverlay(_grip); return; }
            var p = _chromePanel;
            var key = p.Id + p.Mode + _chromeHover + (_drag?.Part ?? Part.None);
            if (key != _chromeKey)
            {
                _chromeKey = key;
                var active = _drag?.P == p ? _drag.Part : _chromeHover;
                DrawIcons(_iconsTex, p.Main, p.Mode == "head", active);
                DrawHandle(_handleTex, active == Part.Handle);
                DrawTilt(_tiltTex, active == Part.Tilt);
                DrawGrip(_gripTex, active == Part.Resize);
                foreach (var (h, t) in new[] { (_icons, _iconsTex), (_handle, _handleTex), (_tilt, _tiltTex), (_grip, _gripTex) })
                {
                    var tx = new Texture_t { handle = t.GetNativeTexturePtr(), eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
                    ov.SetOverlayTexture(h, ref tx);
                }
            }
            // 아이콘 두 칸은 한 오버레이 (닫기 칸 가운데 + 반 칸 + 틈 반)
            var iconsCenter = (ChromeCenter(p, Part.Close) + ChromeCenter(p, Part.Pin)) * 0.5f;
            foreach (var (h, pos) in new[] { (_icons, iconsCenter), (_handle, ChromeCenter(p, Part.Handle)), (_tilt, ChromeCenter(p, Part.Tilt)), (_grip, ChromeCenter(p, Part.Resize)) })
            {
                var mat = ToOpenVr(pos, p.Rot);
                ov.SetOverlayTransformAbsolute(h, ETrackingUniverseOrigin.TrackingUniverseStanding, ref mat);
                ov.ShowOverlay(h);
            }
        }

        // ---- 그리기 (CPU, 첫 줄 = 위) ----
        static readonly Color32 BgCol = new Color32(30, 34, 44, 225), HoverCol = new Color32(60, 92, 150, 240), IconCol = new Color32(235, 240, 248, 255), AccentCol = new Color32(110, 175, 255, 255), HandleCol = new Color32(205, 212, 224, 235);

        static void DrawIcons(Texture2D tex, bool main, bool follow, Part hover)
        {
            int w = tex.width, h = tex.height;
            var px = new Color32[w * h];
            int gap = 3;
            int cw = (w - gap) / 2;
            FillRound(px, w, h, 1, 1, cw - 1, h - 1, cw * 0.5f, hover == Part.Close ? new Color32(190, 70, 70, 240) : BgCol);   // 둥근 단추
            Icon(px, w, h, cw / 2f, h / 2f, h * 0.28f, Cross, IconCol);
            int x1 = cw + gap;
            FillRound(px, w, h, x1 + 1, 1, w - 1, h - 1, cw * 0.5f, hover == Part.Pin ? HoverCol : follow ? new Color32(45, 80, 130, 240) : BgCol);
            Icon(px, w, h, x1 + cw / 2f, h / 2f, h * 0.3f, HeadIcon, follow ? AccentCol : IconCol);
            tex.SetPixels32(px); tex.Apply(true, false);
        }

        static void DrawHandle(Texture2D tex, bool hot)
        {
            int w = tex.width, h = tex.height;
            var px = new Color32[w * h];
            // 가로로 긴 알약 (SteamVR·Quest 창 손잡이처럼). 가리키거나 끄는 중이면 굵고 밝게
            float pad = hot ? 4 : 9;
            FillRound(px, w, h, 6, (int)pad, w - 6, h - (int)pad, (h - 2 * pad) / 2, hot ? AccentCol : HandleCol);
            tex.SetPixels32(px); tex.Apply(true, false);
        }

        static void DrawTilt(Texture2D tex, bool hot)
        {
            int n = tex.width;
            var px = new Color32[n * n];
            // 둥근 바탕 + 옆에서 본 기울어진 창과 위아래 화살표 (끌어서 각도)
            FillRound(px, n, n, 2, 2, n - 2, n - 2, n * 0.5f, hot ? HoverCol : BgCol);
            Icon(px, n, n, n / 2f, n / 2f, n * 0.3f, TiltIcon, hot ? AccentCol : IconCol);
            tex.SetPixels32(px); tex.Apply(true, false);
        }

        static void DrawGrip(Texture2D tex, bool hot)
        {
            int n = tex.width;
            var px = new Color32[n * n];
            // 오른쪽 아래 모서리: 둥근 바탕 + 꺾쇠 (끌어서 크기)
            FillRound(px, n, n, 2, 2, n - 2, n - 2, n * 0.5f, hot ? HoverCol : BgCol);
            Icon(px, n, n, n / 2f, n / 2f, n * 0.3f, Corner, hot ? AccentCol : IconCol);
            tex.SetPixels32(px); tex.Apply(true, false);
        }

        /// <summary>시험용 (vrPanel { id: "*", dump: 폴더 }): 손잡이 줄 그림을 SteamVR 없이 PNG로 — 오버레이에 보이는 그대로(첫 줄 = 위).</summary>
        void DumpDrawings(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            void Save(Texture2D t, string name)
            {
                // 오버레이 텍스처는 첫 줄이 위, PNG 인코더는 첫 줄을 아래로 쓴다 → 뒤집은 사본으로
                var src = t.GetPixels32(); var dst = new Color32[src.Length];
                for (int y = 0; y < t.height; y++) Array.Copy(src, y * t.width, dst, (t.height - 1 - y) * t.width, t.width);
                var c = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, false);
                c.SetPixels32(dst); c.Apply(false, false);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), c.EncodeToPNG());
                UnityEngine.Object.Destroy(c);
            }
            var icons = new Texture2D(IconTex * 2, IconTex, TextureFormat.RGBA32, false, false);
            DrawIcons(icons, true, false, Part.None); Save(icons, "vr_chrome_icons.png");
            DrawIcons(icons, false, true, Part.Close); Save(icons, "vr_chrome_icons_hover.png");
            var handle = new Texture2D(HandleTexW, HandleTexH, TextureFormat.RGBA32, false, false);
            DrawHandle(handle, false); Save(handle, "vr_chrome_handle.png");
            DrawHandle(handle, true); Save(handle, "vr_chrome_handle_hot.png");
            var tilt = new Texture2D(IconTex, IconTex, TextureFormat.RGBA32, false, false);
            DrawTilt(tilt, false); Save(tilt, "vr_chrome_tilt.png");
            var grip = new Texture2D(IconTex, IconTex, TextureFormat.RGBA32, false, false);
            DrawGrip(grip, false); Save(grip, "vr_chrome_grip.png");
            foreach (var t in new[] { icons, handle, tilt, grip }) UnityEngine.Object.Destroy(t);
            Debug.Log("[VrcProto] VR 손잡이 줄 그림: " + dir);
        }

        static void FillRound(Color32[] px, int W, int H, int x0, int y0, int x1, int y1, float rad, Color32 c)
        {
            rad = Mathf.Min(rad, (x1 - x0) / 2f, (y1 - y0) / 2f);
            for (int y = Mathf.Max(0, y0); y < Mathf.Min(H, y1); y++)
                for (int x = Mathf.Max(0, x0); x < Mathf.Min(W, x1); x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(x0 + rad - (x + 0.5f), (x + 0.5f) - (x1 - rad)));
                    float dy = Mathf.Max(0, Mathf.Max(y0 + rad - (y + 0.5f), (y + 0.5f) - (y1 - rad)));
                    float cov = Mathf.Clamp01(rad + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                    if (cov > 0) px[y * W + x] = Blend(px[y * W + x], c, cov);
                }
        }

        /// <summary>아이콘: sdf(x, y)는 -1~1 좌표(y 위로)에서 안쪽이 음수인 거리. r = 아이콘 반지름(픽셀).</summary>
        static void Icon(Color32[] px, int W, int H, float cx, float cy, float r, Func<float, float, float> sdf, Color32 c)
        {
            float aa = 1.2f / r;
            for (int y = Mathf.Max(0, (int)(cy - r - 2)); y < Mathf.Min(H, (int)(cy + r + 2)); y++)
                for (int x = Mathf.Max(0, (int)(cx - r - 2)); x < Mathf.Min(W, (int)(cx + r + 2)); x++)
                {
                    float d = sdf((x + 0.5f - cx) / r, -(y + 0.5f - cy) / r);
                    float cov = Mathf.Clamp01(0.5f - d / aa);
                    if (cov > 0) px[y * W + x] = Blend(px[y * W + x], c, cov);
                }
        }

        static Color32 Blend(Color32 a, Color32 b, float t) => new Color32((byte)Mathf.Lerp(a.r, b.r, t), (byte)Mathf.Lerp(a.g, b.g, t), (byte)Mathf.Lerp(a.b, b.b, t), (byte)Mathf.Lerp(a.a, b.a, t));

        const float Th = 0.11f;   // 선 굵기 (반지름 기준)
        static float Disc(float x, float y, float cx, float cy, float r) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
        static float Seg(float x, float y, float ax, float ay, float bx, float by)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
            float ex = px - dx * t, ey = py - dy * t;
            return Mathf.Sqrt(ex * ex + ey * ey) - Th;
        }
        // 닫기 · 머리 따라오기 · 기울이기 · 크기 모서리
        static float TiltIcon(float x, float y) => Mathf.Min(Seg(x, y, -0.55f, -0.8f, -0.05f, 0.8f),   // 옆에서 본 기울어진 창
            Mathf.Min(Mathf.Min(Seg(x, y, 0.5f, -0.75f, 0.5f, 0.75f), Mathf.Min(Seg(x, y, 0.5f, 0.85f, 0.2f, 0.5f), Seg(x, y, 0.5f, 0.85f, 0.8f, 0.5f))),
                Mathf.Min(Seg(x, y, 0.5f, -0.85f, 0.2f, -0.5f), Seg(x, y, 0.5f, -0.85f, 0.8f, -0.5f))));   // 위아래 화살표
        static float Cross(float x, float y) => Mathf.Min(Seg(x, y, -0.7f, -0.7f, 0.7f, 0.7f), Seg(x, y, -0.7f, 0.7f, 0.7f, -0.7f));
        static float HeadIcon(float x, float y) => Mathf.Min(Disc(x, y, 0, 0.38f, 0.36f), Mathf.Max(Disc(x, y, 0, -0.95f, 0.75f), -(y + 0.95f)));
        static float Corner(float x, float y) => Mathf.Min(Mathf.Min(Seg(x, y, 0.8f, -0.8f, 0.8f, 0.4f), Seg(x, y, 0.8f, -0.8f, -0.4f, -0.8f)), Seg(x, y, 0.6f, -0.6f, -0.4f, 0.4f));

        static void Setup(CVROverlay ov, ulong h, Texture2D tex, float width, uint sort)
        {
            ov.SetOverlayWidthInMeters(h, width);
            ov.SetOverlaySortOrder(h, sort);
            var t = new Texture_t { handle = tex.GetNativeTexturePtr(), eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
            ov.SetOverlayTexture(h, ref t);
        }

        static Texture2D LaserTexture()
        {
            // 첫 줄(위) = 닿은 쪽 진하게, 아래(손 쪽)로 갈수록 옅게 · 가로 가운데가 진하게
            var t = new Texture2D(LaserTexW, LaserTexH, TextureFormat.RGBA32, false, false) { name = "VrLaser", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[LaserTexW * LaserTexH];
            for (int y = 0; y < LaserTexH; y++)
                for (int x = 0; x < LaserTexW; x++)
                {
                    float across = 1 - Mathf.Abs((x + 0.5f) / LaserTexW * 2 - 1);
                    float along = 1 - 0.75f * (y / (float)(LaserTexH - 1));   // 첫 줄(y 0)이 오버레이 위쪽 = 닿은 쪽
                    px[y * LaserTexW + x] = new Color32(140, 200, 255, (byte)(255 * Mathf.Clamp01(across * 1.6f) * along));
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        static Texture2D CursorTexture()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false, false) { name = "VrCursor", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2(x + 0.5f - n / 2f, y + 0.5f - n / 2f).magnitude / (n / 2f);
                    // 흰 점 + 어두운 테두리 (밝은 창·어두운 창 둘 다에서 보이게)
                    var c = r < 0.62f ? new Color32(255, 255, 255, 255) : r < 0.9f ? new Color32(20, 30, 50, 230) : new Color32(0, 0, 0, (byte)(Mathf.Clamp01((1 - r) * 10) * 120));
                    px[y * n + x] = c;
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        /// <summary>내 앞 Distance m, 눈보다 BelowEye m 아래, 내 머리를 보게.</summary>
        static void Place(Panel p, VrLink.Device head)
        {
            var f = head.Rotation * Vector3.forward; f.y = 0;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();
            p.Pos = head.Position + f * Distance + Vector3.down * BelowEye;
            p.Rot = Quaternion.LookRotation(p.Pos - head.Position, Vector3.up);
            p.PoseDirty = true;
        }

        /// <summary>떠 있는 창: 메인 창 옆 (오른쪽 → 왼쪽 → 한 칸 더 바깥 …), 위를 메인 창 위에 맞추고 내 머리를 보게. 다른 창이 있는 자리는 건너뛴다.</summary>
        void PlaceBeside(Panel p, Panel main, VrLink.Device head)
        {
            const float Space = 0.04f;
            var right = main.Rot * Vector3.right; var up = main.Rot * Vector3.up;
            float w = p.W, h = p.H;
            for (int k = 0; k < 12; k++)
            {
                float side = k % 2 == 0 ? 1 : -1;
                int ring = k / 2;
                float x = side * (main.W / 2 + Space + w / 2 + ring * (w + Space));
                var pos = main.Pos + right * x + up * (main.H / 2 - h / 2);
                bool free = true;
                foreach (var o in _panels.Values)
                    if (o != p && o != main && !o.Hand && o.Placed && o.Tex != null && Vector3.Distance(o.Pos, pos) < Mathf.Max(0.12f, (o.W + w) * 0.4f)) { free = false; break; }
                if (!free) continue;
                p.Pos = pos;
                p.Rot = Quaternion.LookRotation(pos - head.Position, Vector3.up);
                p.PoseDirty = true;
                return;
            }
            Place(p, head);
        }

        bool Create(CVROverlay ov, string key, string name, out ulong handle)
        {
            handle = 0;
            var err = ov.CreateOverlay(key, name, ref handle);
            if (err == EVROverlayError.KeyInUse) err = ov.FindOverlay(key, ref handle);
            if (err == EVROverlayError.None && handle != 0) { Debug.Log("[VrcProto] VR 오버레이: " + key); return true; }
            handle = 0;
            _problem = "SteamVR 오버레이를 만들지 못했어요 (" + err + ")";
            return false;
        }

        static void Destroy(Panel p) => DestroyOverlay(ref p.Handle, p);

        static void DestroyOverlay(ref ulong h, Panel p = null)
        {
            if (h != 0) { try { OpenVR.Overlay?.DestroyOverlay(h); } catch (Exception) { } }
            h = 0;
            if (p != null) { p.TexDirty = p.MetersDirty = p.PoseDirty = true; p.Device = uint.MaxValue; }
        }

        static void CloseMap(Panel p)
        {
            if (p.View != IntPtr.Zero) UnmapViewOfFile(p.View);
            if (p.MapHandle != IntPtr.Zero) CloseHandle(p.MapHandle);
            p.View = p.MapHandle = IntPtr.Zero;
            p.Map = "";
        }

        public void Shutdown()
        {
            End();
            foreach (var p in _panels.Values) CloseMap(p);
        }

        /// <summary>Unity(왼손, 방 좌표) 위치·회전 → OpenVR 3×4 (VrLink.ToUnity의 반대: Z를 뒤집는다). 오버레이는 +Z 쪽에서 보인다 = Unity 회전의 뒤쪽.
        /// 장치 기준 자리에도 그대로 쓴다 (Z 뒤집기는 곱셈을 지킨다).</summary>
        static HmdMatrix34_t ToOpenVr(Vector3 p, Quaternion r)
        {
            Vector3 x = r * Vector3.right, y = r * Vector3.up, z = -(r * Vector3.forward);
            return new HmdMatrix34_t
            {
                m0 = x.x, m1 = y.x, m2 = z.x, m3 = p.x,
                m4 = x.y, m5 = y.y, m6 = z.y, m7 = p.y,
                m8 = -x.z, m9 = -y.z, m10 = -z.z, m11 = -p.z,
            };
        }

        const uint FILE_MAP_READ = 4;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
