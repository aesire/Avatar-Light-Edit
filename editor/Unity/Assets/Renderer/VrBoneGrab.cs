// VR 본·아바타 잡기 (PLAN 3.12 "고정 뒤 손으로 본 잡아 다듬기", 2026-10-25 사용자 "VR 모드에서 본을 컨트롤러로 잡고 움직이는 기능",
// 같은 날 "여러 아바타를 한 공간 안에 배치 · 아바타 전체를 잡고 움직이는 기능"): 그립으로 휴머노이드 관절이나 아바타 전체를 잡아 옮긴다.
//   대상: 지금 아바타(관찰자일 때만 — 아바타 안에서는 IK가 매 프레임 본을 덮는다)와 함께 세운 아바타(GuestAvatars, 몸 모드에서도).
//   - 본: 손이 관절 0.6 m 안에 오면 뼈대 선과 관절 공이 보인다(눈 전용 레이어, 몸에 가려도 보임). 잡을 수 있는 관절은 노랑, 잡은 관절은 주황.
//     손·발은 2본 IK로 컨트롤러를 따라오고 방향도 따른다(6축). 머리·목·척추·엉덩이도 방향이 손을 따른다.
//     팔꿈치·무릎·어깨·손가락은 부모 본을 돌려 자리만 따라온다 — 데스크톱 BonePoser와 같은 계산.
//     놓으면 지금 아바타는 poseEdited(자세 페이지 기록, 실행 취소 한 단계), 함께 세운 아바타는 guestPoseEdited.
//   - 아바타 전체: 손이 아바타 메시 범위 안이고 잡을 관절이 없을 때, 또는 트리거를 당긴 채면 관절 근처라도 그립 = 아바타째 잡기.
//     범위 상자가 노랑(잡을 수 있음)·주황(잡음)으로 보인다. 손을 따라 옮기고 손을 돌린 만큼 세로축으로만 돈다(아바타는 늘 똑바로).
//     놓으면 지금 아바타는 avatarMoved(실행 취소), 함께 세운 아바타는 guestMoved.
//   - 두 손으로 서로 다른 것을 하나씩 잡을 수 있다. 끄는 동안 매 프레임 머슬 값으로 다시 적용(HumanPoser.Reproject) → 화면 = 기록될 자세.
//     끄는 동안 Hips 자리·방향은 붙잡아 둔다(몸 전체가 떨리던 것, 2026-10-25) — 놓을 때 한 번 더 다시 적용해 몸 위치·바닥을 맞춘다.
//   - 거리·공 크기는 방 크기 배율(몸 모드 rig 크기)과 손잡이 크기(HandleScale)를 따른다 — 내 손 기준으로 늘 같게 보인다.
//   사용자가 쓰는 Avatar Pose System(ZeroFactory)의 조작을 참고해 더한 것 (2026-10-25 사용자 고름, 코드는 옮기지 않음):
//   - 다른 관절은 제자리(PinOthers, 설정): 잡은 순간 두 손·두 발·머리의 자리·방향을 기억해, 잡은 본 아래가 아니면 그 자리에 둔다.
//     몸통(엉덩이·척추·가슴)을 잡으면 그 아래 손·발·머리도 제자리 — 팔·다리는 2본 IK로 다시 뻗는다.
//     이때 엉덩이는 위아래로도 움직인다(발은 바닥에 둔 채 무릎이 굽어 앉기·쪼그리기). 가로 자리는 자세 형식에 없어 움직이지 않는다.
//   - 막대 손잡이: 관절마다 다음 관절 쪽으로 짧은 초록 막대가 보이고, 막대 끝을 잡으면 그 본만 돌린다(자식 관절이 막대 끝을 따라감,
//     컨트롤러를 막대 둘레로 비틀면 본도 비틀림). 손·발 막대는 팔·다리를 그대로 두고 손목·발목만 돌린다. 손가락·엉덩이는 막대 없음.
//   - PhysBone: 머리카락·치마 관절(보라 공)을 그립으로 잡아 끈다. 잡은 채 트리거 = 그 자리에 고정(분홍 공), 고정한 것을 다시 잡으면 풀린다.
//   - 잠금(Locked, 설정): 관절·막대·PhysBone은 잡지 않고 아바타째 잡기만 — 아바타 옆에서 실수로 자세가 바뀌지 않게.
//   사용성 (2026-10-27 사용자 "VR 모드의 사용성 개선 — 화살표·고리 핸들, 본 관절 공 잡기, 몸으로 포즈 후 손 다듬기"):
//   - 데드존: 잡은 뒤 손이 Dead(1.2 cm)·6° 넘게 움직이거나 TapTime이 지나야 본이 따라온다 — 쥐는 순간 손 떨림으로 자세가 바뀌던 것.
//   - 짧게 쥐었다 놓기(관절·막대) = 그 관절 고르기: 고른 관절에 데스크톱 BonePoser와 같은 핸들(본 로컬 X 빨강·Y 초록·Z 파랑 고리 +
//     고리 밖 이동 화살표, 엉덩이·손가락은 화살표 없음). 손 공을 고리에 대고 그립 = 그 축으로만 돌리기(고리를 따라 손을 돌리는 만큼),
//     화살표에 대고 그립 = 그 축 위로만 옮기기(손·발은 2본 IK, 손·발 방향은 그대로). 같은 관절을 다시 짧게 쥐거나 아바타째 짧게 쥐면 풀림.
//   - 정밀: 관절·막대·핸들을 잡은 채 트리거를 당기면 손이 움직인 만큼의 Fine배만 따라온다(당긴 순간부터, 놓으면 다시 1배).
//   잡기 차례: VR 창(레이저 + 그립) → 카메라 → 본·아바타 → 개인 거울. 잡은 손의 그립은 비워 거울이 받지 않게 한다.
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class VrBoneGrab
    {
        // 방 기준 m. 관절까지 거리는 컨트롤러 몸통 선(손잡이 → 끝)에서 잰다 — 손끝 한 점에서만 재면 잘 안 잡혔다 (2026-10-25 헤드셋 확인).
        // Reach 안이면 바로, Magnet 안이면 가장 가까운 관절로 끌려간다. 한 번 노랑이 된 관절은 Keep 배까지 유지(경계에서 깜빡이지 않게).
        // 아바타째 잡기는 Magnet 안에 관절이 하나도 없고 메시 범위 안일 때만 (관절을 잡으려다 아바타째 잡히던 것), 또는 트리거를 당긴 채.
        const float ShowRadius = 0.6f, Reach = 0.09f, Magnet = 0.16f, FingerReach = 0.03f, FingerMagnet = 0.045f, Keep = 1.4f;
        const float Ball = 0.022f, FingerBall = 0.01f;
        // 막대: 관절에서 다음 관절까지의 StickFrac 자리가 끝. 막대 끝은 관절 공보다 조금 좁게 잡는다 (관절 공이 먼저)
        const float StickFrac = 0.55f, StickReach = 0.7f, StickBall = 0.016f;
        // PhysBone 관절: 촘촘해서 손가락 정도 거리, 끌려가기 없음
        const float PbReach = 0.05f, PbBall = 0.014f;
        // 데드존·짧게 쥐기·정밀 배율
        const float Dead = 0.012f, DeadAngle = 6, TapTime = 0.35f, Fine = 0.25f;
        // 핸들: 고리 반지름(손가락은 작게), 고리·화살표를 잡는 거리, 화살표는 고리 밖 ArrowFrom~ArrowTo (반지름 배)
        const float GizRing = 0.075f, GizFingerRing = 0.03f, GizReach = 0.022f, ArrowFrom = 1.1f, ArrowTo = 1.7f;

        enum Kind { None, Joint, Stick, Pb, Whole, Ring, Arrow }

        /// <summary>본 하나를 움직이는 잡기 (관절 공·막대·핸들) — 놓으면 자세 기록.</summary>
        static bool IsBone(Kind k) => k == Kind.Joint || k == Kind.Stick || k == Kind.Ring || k == Kind.Arrow;

        /// <summary>잡을 수 있는 아바타 하나: 지금 아바타(Guest = null) 또는 함께 세운 아바타.</summary>
        sealed class Body
        {
            public HumanPoser Poser;
            public PhysBoneSim Phys;
            public Transform Root;
            public GuestAvatars.Guest Guest;
            public Bounds Box;
        }

        HumanPoser _main;
        PhysBoneSim _mainPhys;
        SceneBuilder _scene;
        GuestAvatars _guests;
        Action<string, JNode> _edited;
        Action<JNode> _send;
        readonly List<Body> _bodies = new List<Body>();
        readonly Body _mainBody = new Body();
        readonly List<Body> _guestPool = new List<Body>();

        // 손마다 잡은 것: 몸 + 무엇(관절·막대·PhysBone·아바타째) + 본 번호
        readonly Body[] _held = new Body[2], _hoverBody = new Body[2];
        readonly int[] _bone = { -1, -1 }, _hover = { -1, -1 };
        readonly Kind[] _kind = new Kind[2], _hoverKind = new Kind[2];
        readonly bool[] _changed = new bool[2];
        readonly Vector3[] _offPos = new Vector3[2], _handPos0 = new Vector3[2], _bonePos0 = new Vector3[2], _axis = new Vector3[2], _hoverPb = new Vector3[2];
        readonly Quaternion[] _offRot = new Quaternion[2], _rootRot0 = new Quaternion[2];
        readonly float[] _handYaw0 = new float[2];
        readonly VrLink.Device[] _hands = new VrLink.Device[2];
        // 다른 관절 제자리: 손마다 잡은 순간의 끝 관절 (본 번호, 월드 자리, 월드 방향)
        readonly List<(int Bone, Vector3 Pos, Quaternion Rot)>[] _pins = { new List<(int, Vector3, Quaternion)>(), new List<(int, Vector3, Quaternion)>() };
        static readonly string[] PinEnds = { "LeftHand", "RightHand", "LeftFoot", "RightFoot", "Head" };
        // 데드존·짧게 쥐기: 잡은 순간의 진짜 손, 본이 따라오기 시작했는지
        readonly bool[] _armed = new bool[2];
        readonly float[] _holdAt = new float[2];
        readonly Vector3[] _realPos0 = new Vector3[2];
        readonly Quaternion[] _realRot0 = new Quaternion[2];
        // 정밀: 가상 손 = 기준 가상 손 + (진짜 손 − 기준 진짜 손) × 배율. 트리거를 당기고 놓을 때 기준을 다시 잡는다
        readonly bool[] _fine = new bool[2];
        readonly Vector3[] _vPos = new Vector3[2], _aReal = new Vector3[2], _aVirt = new Vector3[2];
        readonly Quaternion[] _vRot = new Quaternion[2], _aRealR = new Quaternion[2], _aVirtR = new Quaternion[2];
        // 고른 관절과 핸들 (손마다 가리킨·잡은 핸들 0~2 화살표 · 3~5 고리)
        Body _selBody; int _sel = -1;
        readonly int[] _giz = { -1, -1 }, _hoverGiz = { -1, -1 };
        readonly Vector3[] _gizAxis = new Vector3[2], _gizPrev = new Vector3[2], _tipOff = new Vector3[2], _tip0 = new Vector3[2];
        readonly Quaternion[] _gizRot0 = new Quaternion[2];
        readonly float[] _gizAngle = new float[2];
        float _k = 1;   // 방 크기 배율 (월드 m / 방 m)
        float S => _k * HandleScale;   // 잡는 거리·공 크기 배율

        GameObject _root;
        readonly List<(Transform T, Material M)> _balls = new List<(Transform, Material)>();
        Mesh _lines, _ballMesh;
        Material _mat;
        readonly List<Vector3> _lv = new List<Vector3>();
        readonly List<Color> _lc = new List<Color>();
        readonly List<int> _li = new List<int>();
        readonly List<Vector3> _pbPins = new List<Vector3>();
        int _used;

        public bool Enabled = true;
        /// <summary>다른 관절은 제자리 (Host VR 메뉴, 기본 켬).</summary>
        public bool PinOthers = true;
        /// <summary>관절·막대·PhysBone 잠금 — 아바타째 잡기만 (Host VR 메뉴).</summary>
        public bool Locked;
        /// <summary>손잡이 크기: 1 보통 · 0.6 가늘게 · 0.35 더 가늘게 (Host VR 메뉴).</summary>
        public float HandleScale = 1;
        public bool Holding => _held[0] != null || _held[1] != null;
        /// <summary>손마다 아이템(VrItemGrab)을 가리키거나 잡고 있음 — 그 손은 아바타째 잡지 않는다 (2026-10-27).</summary>
        public readonly bool[] ItemHere = new bool[2];
        /// <summary>손 공 (잡는 점 공의 색을 넣어 준다). 없어도 된다.</summary>
        public VrControllers Controllers;
        readonly Vector3[] _tipRoom = new Vector3[2];
        readonly bool[] _hasTip = new bool[2];

        /// <summary>이 손이 지금 가까이 잡을 것(관절·아바타)을 가리키거나 잡고 있는지 — VR 창 레이저 잡기보다 먼저 (멀리 있는 창이 그립을 가져가던 것).</summary>
        public bool Near(int h) => Enabled && (_held[h] != null || _hoverBody[h] != null);
        /// <summary>관절·막대·PhysBone을 가리키거나 잡고 있음 (아바타째는 아님) — 그 손의 VR 창 레이저를 끈다.</summary>
        public bool NearBone(int h) => Enabled && (_held[h] != null && _kind[h] != Kind.Whole || _hoverBody[h] != null && _hoverKind[h] != Kind.Whole);

        /// <summary>VrView.Pre에서 VR 창보다 먼저: 잡을 것만 미리 찾는다 (입력은 건드리지 않음).</summary>
        public void Prepare(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right, bool mainUsable)
        {
            if (!Enabled) { for (int h = 0; h < 2; h++) { _hoverBody[h] = null; _hover[h] = -1; _hoverKind[h] = Kind.None; _hoverGiz[h] = -1; } return; }
            Gather(mainUsable);
            CheckSel();
            _k = Mathf.Max(1e-3f, rig.lossyScale.x);
            _hands[0] = left; _hands[1] = right;
            float nearest = float.MaxValue;
            for (int h = 0; h < 2; h++) if (_held[h] == null) Hover(h, rig, input, -1, ref nearest);
        }

        /// <summary>edited = 지금 아바타의 본을 놓았을 때 (본 이름, 자세) · send = avatarMoved·guestMoved · mainPhys = 지금 아바타 PhysBone.</summary>
        public void Init(HumanPoser main, PhysBoneSim mainPhys, Action<string, JNode> edited, SceneBuilder scene, Action<JNode> send, GuestAvatars guests)
        {
            _main = main; _mainPhys = mainPhys; _edited = edited; _scene = scene; _send = send; _guests = guests;
        }

        /// <summary>VrView.Pre에서 (카메라 다음, 거울 전). mainUsable = 지금 아바타도 잡을지 (관찰자일 때만) · cameraHand = 카메라를 잡은 손(-1 = 없음).</summary>
        public void Tick(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right, int cameraHand, bool mainUsable)
        {
            if (!Enabled) { End(); return; }
            Gather(mainUsable);
            CheckSel();
            if (_bodies.Count == 0) { End(); return; }
            _k = Mathf.Max(1e-3f, rig.lossyScale.x);
            SetRig(rig);
            var hands = _hands; hands[0] = left; hands[1] = right;
            float nearest = float.MaxValue;
            for (int h = 0; h < 2; h++)
            {
                var d = hands[h];
                bool tracked = d != null && d.Tracked;
                if (_held[h] != null)
                {
                    _hover[h] = -1; _hoverBody[h] = null; _hoverKind[h] = Kind.None; _hoverGiz[h] = -1;
                    bool bone = IsBone(_kind[h]);
                    if (!_bodies.Contains(_held[h]) || bone && _bone[h] >= _held[h].Poser.BoneCount || _kind[h] == Kind.Pb && !_held[h].Phys.VrHolding(h)) { Drop(h); continue; }   // 장면을 다시 불러옴·몸 모드로 들어감·PhysBone 끔
                    if (!tracked || !input.Grab[h]) { Release(h); continue; }
                    TipOf(h, input, d);
                    var hp = rig.TransformPoint(d.Position); var hr = rig.rotation * d.Rotation;
                    bool armed = Armed(h, hp, hr);   // 데드존은 진짜 손으로
                    if (bone) Virtual(h, input, ref hp, ref hr);
                    switch (_kind[h])
                    {
                        case Kind.Whole: FollowWhole(h, hp, hr); break;
                        case Kind.Stick: if (armed) FollowStick(h, hp, hr); break;
                        case Kind.Pb: FollowPb(h, hp, hr, input); break;
                        case Kind.Ring: FollowRing(h, hp, hr); break;
                        case Kind.Arrow: FollowArrow(h, hp, hr); break;
                        default: if (armed) Follow(h, hp, hr); break;
                    }
                    input.Grab[h] = input.GrabDown[h] = false;   // 거울 잡기가 받지 않게
                    continue;
                }
                Hover(h, rig, input, cameraHand, ref nearest);
                if (input.GrabDown[h] && _hoverBody[h] != null)
                {
                    var p = rig.TransformPoint(d.Position); var r = rig.rotation * d.Rotation;
                    switch (_hoverKind[h])
                    {
                        case Kind.Whole: HoldWhole(h, _hoverBody[h], p, r); break;
                        case Kind.Stick: HoldStick(h, _hoverBody[h], _hover[h], p, r); break;
                        case Kind.Pb: HoldPb(h, _hoverBody[h], p, r, rig.TransformPoint(_tipRoom[h])); break;
                        case Kind.Ring: case Kind.Arrow: HoldGiz(h, _hoverGiz[h], p, r, rig.TransformPoint(_tipRoom[h])); break;
                        default: Hold(h, _hoverBody[h], _hover[h], p, r); break;
                    }
                    _holdAt[h] = Time.unscaledTime; _realPos0[h] = p; _realRot0[h] = r;
                    _armed[h] = _kind[h] != Kind.Joint && _kind[h] != Kind.Stick;   // 데드존은 관절·막대만 (핸들은 누르자마자)
                    BeginVirtual(h, p, r);
                    input.Grab[h] = input.GrabDown[h] = false;
                }
            }
            Draw(Holding || nearest <= ShowRadius * _k || _selBody != null);
            Show();
        }

        /// <summary>잡는 점 공의 색 (0 없음 · 1 잡을 수 있음 · 2 잡는 중).</summary>
        void Show()
        {
            if (Controllers == null) return;
            for (int h = 0; h < 2; h++)
            {
                Controllers.Hot[h] = _held[h] != null ? 2 : _hoverBody[h] != null ? 1 : 0;
            }
        }

        /// <summary>컨트롤러 끝 (방 기준): aim 자세(/pose/tip), 없으면 손잡이에서 앞으로.</summary>
        void TipOf(int h, VrInput input, VrLink.Device d)
        {
            _tipRoom[h] = VrControllers.Tip(h, input, d);   // 손 공과 같은 점
            _hasTip[h] = true;
        }

        /// <summary>손 h가 잡을 것: 관절·막대·PhysBone(컨트롤러 몸통 선에서 가까운 것) → 없으면 메시 범위 안의 아바타째. 트리거를 당긴 채·잠금이면 아바타째만.</summary>
        void Hover(int h, Transform rig, VrInput input, int cameraHand, ref float nearest)
        {
            var d = _hands[h];
            int keepBone = _hover[h]; var keepKind = _hoverKind[h]; var keepBody = _hoverBody[h]; int keepGiz = _hoverGiz[h];
            _hover[h] = -1; _hoverBody[h] = null; _hoverKind[h] = Kind.None; _hoverGiz[h] = -1; _hasTip[h] = false;
            if (d == null || !d.Tracked || h == cameraHand) return;
            TipOf(h, input, d);
            Vector3 a = rig.TransformPoint(d.Position), b = rig.TransformPoint(_tipRoom[h]);
            bool forceWhole = input.Trigger[h] > 0.5f;
            if (!forceWhole && !Locked)
            {
                // 고른 관절의 핸들이 먼저 (고리 안쪽의 다른 관절보다)
                if (PickGiz(b, 1 - h, keepGiz, out int g, ref nearest)) { _hoverBody[h] = _selBody; _hover[h] = _sel; _hoverKind[h] = g < 3 ? Kind.Arrow : Kind.Ring; _hoverGiz[h] = g; return; }
                Pick(a, b, 1 - h, keepBody, keepKind, keepBone, out var body, out var bone, out var kind, out _hoverPb[h], out bool inMagnet, ref nearest);
                if (body != null) { _hoverBody[h] = body; _hover[h] = bone; _hoverKind[h] = kind; return; }
                if (inMagnet) return;   // 관절 가까이인데 다른 손이 잡은 것뿐 — 아바타째 잡지 않는다
            }
            if (ItemHere[h]) return;   // 아이템 범위 안: 아바타째보다 아이템이 먼저
            _hoverBody[h] = Inside(b, 1 - h) ?? Inside(a, 1 - h);
            if (_hoverBody[h] != null) { _hoverKind[h] = Kind.Whole; nearest = 0; }
        }

        /// <summary>점 p에서 선분 a–b까지 거리.</summary>
        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float l = ab.sqrMagnitude;
            float t = l < 1e-10f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, ab) / l);
            return Vector3.Distance(p, a + ab * t);
        }

        void Gather(bool mainUsable)
        {
            _bodies.Clear();
            if (mainUsable && _main != null && _main.Ready && _scene.Root != null)
            {
                _mainBody.Poser = _main; _mainBody.Phys = _mainPhys; _mainBody.Root = _scene.Root; _mainBody.Guest = null;
                _mainBody.Box = _scene.AllBounds();
                _bodies.Add(_mainBody);
            }
            if (_guests == null) return;
            var all = _guests.All;
            while (_guestPool.Count < all.Count) _guestPool.Add(new Body());
            for (int i = 0; i < all.Count; i++)
            {
                var g = all[i];
                if (!g.Shown) continue;
                var b = _guestPool[i];
                b.Poser = g.Poser; b.Phys = g.Phys; b.Root = g.Root; b.Guest = g;
                b.Box = g.Scene.AllBounds();
                _bodies.Add(b);
            }
        }

        /// <summary>Reach 안은 0~1, Magnet 안은 1~2 (Reach 안의 것이 늘 먼저).</summary>
        static float Score(float d, float reach, float magnet) => d <= reach ? d / reach : 1 + (d - reach) / Mathf.Max(1e-5f, magnet - reach);

        /// <summary>
        /// 컨트롤러 몸통 선 a(손잡이)–b(끝)에서 가장 가까운 관절 공·막대 끝·PhysBone 관절. 거리 ÷ 잡을 거리가 작은 것 (손가락은 촘촘해 더 가까이).
        /// Reach 안이 먼저, 없으면 Magnet 안에서 가장 가까운 것. 지난 프레임에 고른 것은 Keep 배까지 그대로.
        /// inMagnet = 다른 손이 잡은 것까지 쳐서 Magnet 안에 관절이 있었는지 · nearest = 가장 가까운 것까지 거리(보이기 판단).
        /// </summary>
        void Pick(Vector3 a, Vector3 b, int other, Body keepBody, Kind keepKind, int keepBone, out Body body, out int bone, out Kind kind, out Vector3 pbPos, out bool inMagnet, ref float nearest)
        {
            body = null; bone = -1; kind = Kind.None; pbPos = default; inMagnet = false;
            float bestK = float.MaxValue, s = S;
            foreach (var bd in _bodies)
            {
                if (!bd.Poser.Ready) continue;
                var box = bd.Box; box.Expand(2 * ShowRadius * _k);
                if (!box.Contains(a) && !box.Contains(b)) continue;   // 먼 아바타는 관절을 돌지 않는다
                var poser = bd.Poser;
                bool otherHere = _held[other] == bd;
                for (int i = 0; i < poser.BoneCount; i++)
                {
                    if (!Grabbable(poser, i)) continue;
                    bool taken = otherHere && (_kind[other] == Kind.Whole || _bone[other] == i && _kind[other] != Kind.Pb);
                    bool finger = IsFinger(poser, i);
                    // 관절 공
                    float d = SegDist(poser.BoneTransform(i).position, a, b);
                    nearest = Mathf.Min(nearest, d);
                    float reach = (finger ? FingerReach : Reach) * s, magnet = (finger ? FingerMagnet : Magnet) * s;
                    if (bd == keepBody && keepKind == Kind.Joint && i == keepBone) { reach *= Keep; magnet *= Keep; }
                    if (d <= magnet)
                    {
                        inMagnet = true;
                        float k = Score(d, reach, magnet);
                        if (!taken && k < bestK) { bestK = k; body = bd; bone = i; kind = Kind.Joint; }
                    }
                    // 막대 끝 (고른 관절은 핸들이 대신한다)
                    if (!HasStick(poser, i) || bd == _selBody && i == _sel) continue;
                    d = SegDist(StickEnd(bd, i), a, b);
                    nearest = Mathf.Min(nearest, d);
                    reach = Reach * StickReach * s; magnet = Magnet * StickReach * s;
                    if (bd == keepBody && keepKind == Kind.Stick && i == keepBone) { reach *= Keep; magnet *= Keep; }
                    if (d <= magnet)
                    {
                        inMagnet = true;
                        float k = Score(d, reach, magnet);
                        if (!taken && k < bestK) { bestK = k; body = bd; bone = i; kind = Kind.Stick; }
                    }
                }
                // PhysBone 관절 (끌려가기 없이 Reach 안만)
                if (bd.Phys != null)
                {
                    float r = PbReach * s * (bd == keepBody && keepKind == Kind.Pb ? Keep : 1);
                    if (bd.Phys.VrNearest(a, b, r, other, out float d, out var p))
                    {
                        nearest = Mathf.Min(nearest, d);
                        float k = d / r;
                        if (k < bestK) { bestK = k; body = bd; bone = -1; kind = Kind.Pb; pbPos = p; }
                    }
                }
            }
        }

        /// <summary>점 p가 메시 범위 안에 있는 아바타 — 여럿이면 가운데가 가까운 것. 다른 손이 통째로 잡은 것은 뺀다.</summary>
        Body Inside(Vector3 p, int other)
        {
            Body best = null; float bestD = float.MaxValue;
            foreach (var b in _bodies)
            {
                if (_held[other] == b && _kind[other] == Kind.Whole) continue;
                if (!b.Box.Contains(p)) continue;
                float d = (b.Box.center - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        static bool Grabbable(HumanPoser poser, int i)
        {
            var t = poser.BoneTransform(i);
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            var n = poser.BoneName(i);
            return !n.Contains("Eye") && n != "Jaw";
        }

        static bool IsFinger(HumanPoser poser, int i) { var n = poser.BoneName(i); return n.Contains("Proximal") || n.Contains("Intermediate") || n.Contains("Distal"); }

        /// <summary>방향도 손을 따르는 본: 엉덩이·척추·목·머리·손·발·발가락. 나머지(팔꿈치·무릎·어깨·손가락)는 자리만.</summary>
        static bool FollowsRotation(HumanPoser poser, int i)
        {
            var n = poser.BoneName(i);
            return poser.BoneParent(i) < 0 || n is "Spine" or "Chest" or "UpperChest" or "Neck" or "Head" || n.EndsWith("Hand") || n.EndsWith("Foot") || n.EndsWith("Toes");
        }

        /// <summary>몸통: 엉덩이·척추·가슴. 잡아도 그 아래 손·발·머리는 제자리에 둔다.</summary>
        static bool IsTrunk(HumanPoser poser, int i) => poser.BoneParent(i) < 0 || poser.BoneName(i) is "Spine" or "Chest" or "UpperChest";

        /// <summary>본 e가 본 a의 자손인지.</summary>
        static bool Under(HumanPoser poser, int e, int a)
        {
            for (int p = poser.BoneParent(e); p >= 0; p = poser.BoneParent(p)) if (p == a) return true;
            return false;
        }

        // ---- 본 ----
        void Hold(int h, Body b, int bone, Vector3 handPos, Quaternion handRot)
        {
            var t = b.Poser.BoneTransform(bone);
            _held[h] = b; _bone[h] = bone; _kind[h] = Kind.Joint; _changed[h] = false;
            var inv = Quaternion.Inverse(handRot);
            _offPos[h] = inv * (t.position - handPos);
            _offRot[h] = inv * t.rotation;
            _handPos0[h] = handPos; _bonePos0[h] = t.position;
            CapturePins(h);
        }

        void Follow(int h, Vector3 handPos, Quaternion handRot)
        {
            var b = _held[h];
            var poser = b.Poser;
            int i = _bone[h];
            var t = poser.BoneTransform(i);
            bool rot = FollowsRotation(poser, i);
            int p = poser.BoneParent(i);
            // 자리: 방향도 따르는 본은 손에 붙은 것처럼(손을 비틀면 같이 돈다), 중간 관절은 손이 움직인 만큼만
            var target = rot ? handPos + handRot * _offPos[h] : _bonePos0[h] + (handPos - _handPos0[h]);
            if (p >= 0) { if ((t.position - target).sqrMagnitude > 1e-8f) MoveJoint(poser, i, target, Hint(b, poser.BoneName(i))); }
            else if (PinOthers)
            {
                // 엉덩이: 다른 관절이 제자리면 위아래로도 (발은 바닥에 둔 채 앉기). 가로는 자세(bodyHeight만)에 남지 않아 그대로
                var up = b.Root.up;
                var pos = _bonePos0[h] + up * Vector3.Dot(target - _bonePos0[h], up);
                if ((t.position - pos).sqrMagnitude > 1e-8f) t.position = pos;
            }
            if (rot)
            {
                var want = handRot * _offRot[h];
                if (Quaternion.Angle(t.rotation, want) > 0.05f) t.rotation = want;
            }
            ApplyPins(h);
            poser.Reproject(BodyFromBones(poser, i), true);
            _changed[h] = true;
        }

        /// <summary>Hips가 돌아가는 잡기 (Hips를 잡음 · 척추·허벅지처럼 부모 Hips를 돌림) — 몸 방향·높이도 본에서 읽는다 (BonePoser와 같게).
        /// 고정된 몸 방향으로 Hips가 되돌아가면 다음 프레임에 또 돌려 몸 전체가 떨렸다.</summary>
        static bool BodyFromBones(HumanPoser poser, int i) { int p = poser.BoneParent(i); return p < 0 || poser.BoneParent(p) < 0; }

        /// <summary>BonePoser.MoveJoint와 같다: 손·발은 2본 IK, 나머지는 부모 본을 돌린다.</summary>
        static void MoveJoint(HumanPoser poser, int i, Vector3 target, Vector3 hint)
        {
            int p = poser.BoneParent(i);
            int g = poser.BoneParent(p);
            var name = poser.BoneName(i);
            if (g >= 0 && (name.EndsWith("Hand") || name.EndsWith("Foot"))) TwoBoneIk(poser.BoneTransform(g), poser.BoneTransform(p), poser.BoneTransform(i), target, hint);
            else Swing(poser.BoneTransform(p), poser.BoneTransform(i).position, target);
        }

        /// <summary>팔·다리가 거의 곧을 때 굽힐 쪽: 무릎은 앞, 팔꿈치는 아래·뒤.</summary>
        static Vector3 Hint(Body b, string name) =>
            name.EndsWith("Foot") ? b.Root.forward : name.EndsWith("Hand") ? -b.Root.up - 0.5f * b.Root.forward : Vector3.zero;

        static void Swing(Transform t, Vector3 from, Vector3 to)
        {
            Vector3 a = from - t.position, b = to - t.position;
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return;
            t.rotation = Quaternion.FromToRotation(a, b) * t.rotation;
        }

        static void TwoBoneIk(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float la = (b - a).magnitude, lb = (c - b).magnitude;
            if (la < 1e-5f || lb < 1e-5f) return;
            var toT = target - a;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
            var dir = toT.sqrMagnitude > 1e-10f ? toT.normalized : (c - a).normalized;
            // 지금 굽은 쪽을 유지한다 (팔꿈치·무릎이 뒤집히지 않게). 거의 곧으면 hint 쪽으로 (앉을 때 무릎이 옆·뒤로 꺾이지 않게)
            var bend = Vector3.ProjectOnPlane(b - a, dir);
            if (bend.sqrMagnitude < 0.0025f * la * la)
            {
                var hb = Vector3.ProjectOnPlane(hint, dir);
                if (hb.sqrMagnitude > 1e-8f) bend = hb;
            }
            if (bend.sqrMagnitude < 1e-10f) bend = Vector3.ProjectOnPlane(upper.forward, dir);
            bend.Normalize();
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2 * la * d), -1, 1);
            var elbow = a + dir * (la * cosA) + bend * (la * Mathf.Sqrt(1 - cosA * cosA));
            Swing(upper, b, elbow);
            Swing(lower, end.position, a + dir * d);
        }

        // ---- 다른 관절 제자리 ----
        /// <summary>잡은 순간 두 손·두 발·머리의 월드 자리·방향을 기억한다 (PinOthers일 때).</summary>
        void CapturePins(int h)
        {
            var list = _pins[h]; list.Clear();
            if (!PinOthers || _held[h] == null) return;
            var poser = _held[h].Poser;
            foreach (var n in PinEnds)
            {
                int e = poser.FindBone(n);
                if (e < 0 || !Grabbable(poser, e)) continue;
                var t = poser.BoneTransform(e);
                list.Add((e, t.position, t.rotation));
            }
        }

        /// <summary>
        /// 잡은 본을 움직인 뒤, 기억한 끝 관절을 제자리로: 손·발은 2본 IK + 방향, 머리는 방향만.
        /// 잡은 본(또는 다른 손이 잡은 본) 자신과, 몸통이 아닌 그 본 아래(팔꿈치를 잡으면 그 손)는 따라가게 둔다.
        /// </summary>
        void ApplyPins(int h)
        {
            var b = _held[h];
            if (!PinOthers || _pins[h].Count == 0) return;
            int o = 1 - h;
            if (_held[o] == b && _kind[o] == Kind.Whole) return;   // 다른 손이 아바타째 옮기는 중
            var poser = b.Poser;
            int i = _bone[h];
            int oi = _held[o] == b && IsBone(_kind[o]) ? _bone[o] : -1;
            foreach (var (e, pos, rot) in _pins[h])
            {
                if (e == i || Under(poser, e, i) && !IsTrunk(poser, i)) continue;
                if (oi >= 0 && (e == oi || Under(poser, e, oi) && !IsTrunk(poser, oi))) continue;
                var t = poser.BoneTransform(e);
                var name = poser.BoneName(e);
                if (name != "Head")
                {
                    int p = poser.BoneParent(e), g = p >= 0 ? poser.BoneParent(p) : -1;
                    if (g >= 0 && (t.position - pos).sqrMagnitude > 1e-8f) TwoBoneIk(poser.BoneTransform(g), poser.BoneTransform(p), t, pos, Hint(b, name));
                }
                if (Quaternion.Angle(t.rotation, rot) > 0.05f) t.rotation = rot;
            }
        }

        // ---- 막대 ----
        /// <summary>막대가 있는 본: 엉덩이·손가락 빼고 (엉덩이는 관절 공이 이미 돌리기, 손가락은 촘촘해 공만).</summary>
        static bool HasStick(HumanPoser poser, int i) => poser.BoneParent(i) >= 0 && !IsFinger(poser, i);

        /// <summary>막대 끝 (월드): 관절에서 다음 관절 쪽으로 StickFrac. 머리는 위로, 끝 본(발가락 등)은 부모에서 이어지는 쪽으로.</summary>
        static Vector3 StickEnd(Body b, int i)
        {
            var poser = b.Poser;
            var p = poser.BoneTransform(i).position;
            var parent = poser.BoneTransform(poser.BoneParent(i)).position;
            int c = poser.BoneChild(i);
            var name = poser.BoneName(i);
            Vector3 tip;
            if (name == "Head") tip = p + b.Root.up * Mathf.Max(2.2f * Vector3.Distance(p, parent), 0.05f);
            else if (c >= 0 && (poser.BoneTransform(c).position - p).sqrMagnitude > 1e-6f) tip = poser.BoneTransform(c).position;
            else tip = p + (p - parent);
            return p + (tip - p) * StickFrac;
        }

        void HoldStick(int h, Body b, int bone, Vector3 handPos, Quaternion handRot)
        {
            var t = b.Poser.BoneTransform(bone);
            _held[h] = b; _bone[h] = bone; _kind[h] = Kind.Stick; _changed[h] = false;
            var end = StickEnd(b, bone);
            var inv = Quaternion.Inverse(handRot);
            _offPos[h] = inv * (end - handPos);   // 막대 끝이 손에 붙은 것처럼
            _offRot[h] = inv * t.rotation;
            _axis[h] = Quaternion.Inverse(t.rotation) * (end - t.position);   // 본 기준 막대 방향
            CapturePins(h);
        }

        /// <summary>본만 돌린다: 손이 돌린 만큼 돌리고(막대 둘레 비틀기 포함), 막대가 손에 붙은 끝을 가리키게 다시 맞춘다.</summary>
        void FollowStick(int h, Vector3 handPos, Quaternion handRot)
        {
            var poser = _held[h].Poser;
            int i = _bone[h];
            var t = poser.BoneTransform(i);
            var rb = handRot * _offRot[h];
            Vector3 cur = rb * _axis[h], want = handPos + handRot * _offPos[h] - t.position;
            var r = cur.sqrMagnitude > 1e-10f && want.sqrMagnitude > 1e-10f ? Quaternion.FromToRotation(cur, want) * rb : rb;
            if (Quaternion.Angle(t.rotation, r) > 0.05f) t.rotation = r;
            ApplyPins(h);
            poser.Reproject(BodyFromBones(poser, i), true);
            _changed[h] = true;
        }

        // ---- PhysBone ----
        void HoldPb(int h, Body b, Vector3 handPos, Quaternion handRot, Vector3 tip)
        {
            if (b.Phys == null || !b.Phys.VrGrab(h, handPos, tip, PbReach * S * Keep, 1 - h)) return;
            _held[h] = b; _bone[h] = -1; _kind[h] = Kind.Pb; _changed[h] = false;
            _offPos[h] = Quaternion.Inverse(handRot) * (b.Phys.VrHeldPos(h) - handPos);
        }

        /// <summary>잡은 관절이 손에 붙은 것처럼 끈다. 트리거 = 그 자리에 고정하고 놓기.</summary>
        void FollowPb(int h, Vector3 handPos, Quaternion handRot, VrInput input)
        {
            var phys = _held[h].Phys;
            phys.VrDrag(h, handPos + handRot * _offPos[h]);
            if (!input.UseDown[h]) return;
            input.Use[h] = input.UseDown[h] = false;
            var msg = phys.VrPin(h);
            if (msg != null) Debug.Log("[VrcProto] VR PhysBone " + msg);
            Drop(h);
        }

        // ---- 데드존·정밀 ----
        /// <summary>진짜 손이 데드존을 넘었거나 TapTime이 지났으면 true (한 번 넘으면 놓을 때까지).</summary>
        bool Armed(int h, Vector3 hp, Quaternion hr)
        {
            if (_armed[h]) return true;
            float dz = Dead * S;
            if ((hp - _realPos0[h]).sqrMagnitude > dz * dz || Quaternion.Angle(hr, _realRot0[h]) > DeadAngle || Time.unscaledTime - _holdAt[h] > TapTime) _armed[h] = true;
            return _armed[h];
        }

        void BeginVirtual(int h, Vector3 hp, Quaternion hr)
        {
            _fine[h] = false;
            _vPos[h] = _aReal[h] = _aVirt[h] = hp;
            _vRot[h] = _aRealR[h] = _aVirtR[h] = hr;
        }

        /// <summary>트리거를 당긴 동안 손이 움직인 만큼의 Fine배만: 당기고 놓는 순간의 가상 손을 기준으로 이어 간다 (본이 튀지 않게).</summary>
        void Virtual(int h, VrInput input, ref Vector3 hp, ref Quaternion hr)
        {
            bool fine = input.Trigger[h] > 0.5f;
            if (fine != _fine[h])
            {
                _fine[h] = fine;
                _aReal[h] = hp; _aRealR[h] = hr; _aVirt[h] = _vPos[h]; _aVirtR[h] = _vRot[h];
            }
            float f = fine ? Fine : 1;
            _vPos[h] = _aVirt[h] + (hp - _aReal[h]) * f;
            _vRot[h] = Quaternion.SlerpUnclamped(Quaternion.identity, hr * Quaternion.Inverse(_aRealR[h]), f) * _aVirtR[h];
            hp = _vPos[h]; hr = _vRot[h];
        }

        // ---- 고른 관절과 핸들 ----
        void Select(Body b, int bone)
        {
            if (b == null || _selBody == b && _sel == bone) { _selBody = null; _sel = -1; Debug.Log("[VrcProto] VR 본 선택 풀기"); return; }
            _selBody = b; _sel = bone;
            Debug.Log($"[VrcProto] VR 본 고르기: {b.Poser.BoneName(bone)}{(b.Guest != null ? " (함께 세운 아바타 " + b.Guest.Id + ")" : "")}");
        }

        /// <summary>고른 아바타가 없어졌거나(장면 다시 불러옴·몸 모드로 들어감) 관절이 숨었으면 선택을 푼다.</summary>
        void CheckSel()
        {
            if (_selBody == null) return;
            if (!_bodies.Contains(_selBody) || !_selBody.Poser.Ready || _sel < 0 || _sel >= _selBody.Poser.BoneCount || !Grabbable(_selBody.Poser, _sel)) { _selBody = null; _sel = -1; }
        }

        float GizRadius(Body b, int i) => (IsFinger(b.Poser, i) ? GizFingerRing : GizRing) * S;

        /// <summary>이동 화살표: 데스크톱처럼 엉덩이 말고 (손가락도 촘촘해 고리만).</summary>
        static bool GizArrows(Body b, int i) => b.Poser.BoneParent(i) >= 0 && !IsFinger(b.Poser, i);

        /// <summary>핸들 축 k (0 X · 1 Y · 2 Z): 본 로컬 축 (데스크톱 BonePoser 기본과 같음).</summary>
        static Vector3 GizAxis(Body b, int i, int k)
        {
            var t = b.Poser.BoneTransform(i);
            return k == 0 ? t.right : k == 1 ? t.up : t.forward;
        }

        /// <summary>손 공 p에서 가까운 고른 관절의 핸들 (0~2 화살표 · 3~5 고리). 다른 손이 그 관절이나 아바타째 잡고 있으면 없음.</summary>
        bool PickGiz(Vector3 p, int other, int keep, out int g, ref float nearest)
        {
            g = -1;
            if (_selBody == null || !_bodies.Contains(_selBody)) return false;
            if (_held[other] == _selBody && (_kind[other] == Kind.Whole || IsBone(_kind[other]) && _bone[other] == _sel)) return false;
            var c = _selBody.Poser.BoneTransform(_sel).position;
            float R = GizRadius(_selBody, _sel), best = float.MaxValue;
            float reach0 = (IsFinger(_selBody.Poser, _sel) ? 0.012f : GizReach) * S;
            bool arrows = GizArrows(_selBody, _sel);
            for (int k = 0; k < 3; k++)
            {
                var ax = GizAxis(_selBody, _sel, k);
                // 고리: 고리 원까지 거리
                var v = p - c; float along = Vector3.Dot(v, ax);
                float rad = (v - ax * along).magnitude;
                float d = Mathf.Sqrt(along * along + (rad - R) * (rad - R));
                float reach = reach0 * (keep == 3 + k ? Keep : 1);
                nearest = Mathf.Min(nearest, d);
                if (d <= reach && d / reach < best) { best = d / reach; g = 3 + k; }
                if (!arrows) continue;
                d = SegDist(p, c + ax * (R * ArrowFrom), c + ax * (R * ArrowTo));
                reach = reach0 * (keep == k ? Keep : 1);
                nearest = Mathf.Min(nearest, d);
                if (d <= reach && d / reach < best) { best = d / reach; g = k; }
            }
            return g >= 0;
        }

        void HoldGiz(int h, int g, Vector3 handPos, Quaternion handRot, Vector3 tip)
        {
            var b = _selBody;
            if (b == null || g < 0) return;
            var t = b.Poser.BoneTransform(_sel);
            _held[h] = b; _bone[h] = _sel; _kind[h] = g < 3 ? Kind.Arrow : Kind.Ring; _giz[h] = g; _changed[h] = false;
            _gizAxis[h] = GizAxis(b, _sel, g % 3);   // 끄는 동안 고정
            _gizRot0[h] = t.rotation; _bonePos0[h] = t.position; _gizAngle[h] = 0;
            _tipOff[h] = Quaternion.Inverse(handRot) * (tip - handPos);   // 손 공을 (정밀 모드의) 가상 손에 붙여 따라간다
            _tip0[h] = tip;
            _gizPrev[h] = Vector3.ProjectOnPlane(tip - t.position, _gizAxis[h]);
            CapturePins(h);
        }

        /// <summary>고리: 손 공이 고리 축 둘레로 돈 각도만큼 본을 그 축으로만 돌린다 (한 바퀴 넘게도 이어서).</summary>
        void FollowRing(int h, Vector3 handPos, Quaternion handRot)
        {
            var poser = _held[h].Poser;
            int i = _bone[h];
            var t = poser.BoneTransform(i);
            var ax = _gizAxis[h];
            var v = Vector3.ProjectOnPlane(handPos + handRot * _tipOff[h] - _bonePos0[h], ax);
            if (v.sqrMagnitude > 1e-8f && _gizPrev[h].sqrMagnitude > 1e-8f)
            {
                _gizAngle[h] += Vector3.SignedAngle(_gizPrev[h], v, ax);
                _gizPrev[h] = v;
            }
            var want = Quaternion.AngleAxis(_gizAngle[h], ax) * _gizRot0[h];
            if (Quaternion.Angle(t.rotation, want) > 0.05f) t.rotation = want;
            ApplyPins(h);
            poser.Reproject(BodyFromBones(poser, i), true);
            _changed[h] |= Mathf.Abs(_gizAngle[h]) > 0.05f;
        }

        /// <summary>화살표: 손 공이 축 방향으로 움직인 만큼만 관절을 옮긴다. 손·발·머리 등 방향을 따르는 본은 방향을 그대로 둔다.</summary>
        void FollowArrow(int h, Vector3 handPos, Quaternion handRot)
        {
            var b = _held[h];
            var poser = b.Poser;
            int i = _bone[h];
            var t = poser.BoneTransform(i);
            var ax = _gizAxis[h];
            float s = Vector3.Dot(handPos + handRot * _tipOff[h] - _tip0[h], ax);
            var target = _bonePos0[h] + ax * s;
            if ((t.position - target).sqrMagnitude > 1e-8f) MoveJoint(poser, i, target, Hint(b, poser.BoneName(i)));
            if (FollowsRotation(poser, i) && Quaternion.Angle(t.rotation, _gizRot0[h]) > 0.05f) t.rotation = _gizRot0[h];
            ApplyPins(h);
            poser.Reproject(BodyFromBones(poser, i), true);
            _changed[h] |= Mathf.Abs(s) > 1e-4f;
        }

        // ---- 아바타 전체 ----
        static float Yaw(Quaternion q) { var f = q * Vector3.forward; return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }

        void HoldWhole(int h, Body b, Vector3 handPos, Quaternion handRot)
        {
            _held[h] = b; _bone[h] = -1; _kind[h] = Kind.Whole; _changed[h] = false;
            _handPos0[h] = handPos; _handYaw0[h] = Yaw(handRot);
            _bonePos0[h] = b.Root.position; _rootRot0[h] = b.Root.rotation;
        }

        /// <summary>손에 붙은 것처럼 옮기되 손을 돌린 만큼 세로축으로만 (손 둘레로) 돈다.</summary>
        void FollowWhole(int h, Vector3 handPos, Quaternion handRot)
        {
            var t = _held[h].Root;
            var dq = Quaternion.Euler(0, Mathf.DeltaAngle(_handYaw0[h], Yaw(handRot)), 0);
            var pos = handPos + dq * (_bonePos0[h] - _handPos0[h]);
            var rot = dq * _rootRot0[h];
            if ((t.position - pos).sqrMagnitude < 1e-10f && Quaternion.Angle(t.rotation, rot) < 0.01f) return;
            t.SetPositionAndRotation(pos, rot);
            _changed[h] = true;
        }

        // ---- 놓기 ----
        void Release(int h)
        {
            var b = _held[h];
            // 짧게 쥐었다 놓음: 관절·막대 = 그 관절 고르기(같은 것이면 풀기), 아바타째 = 선택 풀기
            bool tap = b != null && !_changed[h] && Time.unscaledTime - _holdAt[h] <= TapTime;
            if (tap && (_kind[h] == Kind.Joint || _kind[h] == Kind.Stick) && !_armed[h]) Select(b, _bone[h]);
            else if (tap && _kind[h] == Kind.Whole && _selBody != null) Select(null, -1);
            if (b != null && _changed[h])
            {
                if (_kind[h] == Kind.Whole)
                {
                    if (b.Guest != null) _send?.Invoke(_guests.Moved(b.Guest));
                    else
                    {
                        var t = b.Root; Vector3 p = t.localPosition; var q = t.localRotation;
                        _send?.Invoke(Msg.Make(Msg.AvatarMoved).Set("trs", JNode.FloatArray(new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w })));
                    }
                    Debug.Log($"[VrcProto] VR 아바타 잡기: {(b.Guest != null ? "함께 세운 아바타 " + b.Guest.Id : "지금 아바타")} ({(h == 0 ? "왼손" : "오른손")})");
                }
                else if (IsBone(_kind[h]) && b.Poser.Ready && _bone[h] < b.Poser.BoneCount)
                {
                    var name = b.Poser.BoneName(_bone[h]);
                    b.Poser.Reproject(BodyFromBones(b.Poser, _bone[h]));   // 끄는 동안 붙잡아 둔 Hips·바닥을 기록될 자세로
                    if (b.Guest != null) _guests.PoseEdited(b.Guest, b.Poser.CurrentPose());
                    else _edited?.Invoke(name, b.Poser.CurrentPose());
                    string how = _kind[h] switch { Kind.Stick => " 막대", Kind.Ring => " 고리 " + "XYZ"[_giz[h] % 3], Kind.Arrow => " 화살표 " + "XYZ"[_giz[h] % 3], _ => "" };
                    Debug.Log($"[VrcProto] VR 본 잡기: {name}{how}{(_fine[h] ? " (정밀)" : "")}{(b.Guest != null ? " (함께 세운 아바타 " + b.Guest.Id + ")" : "")} ({(h == 0 ? "왼손" : "오른손")})");
                }
            }
            Drop(h);
            // 다른 손이 같은 아바타의 본을 잡고 있으면 그 손의 제자리를 지금 자리로 (방금 옮긴 관절이 예전 자리로 끌려가지 않게)
            int o = 1 - h;
            if (b != null && _held[o] == b && IsBone(_kind[o])) CapturePins(o);
        }

        void Drop(int h)
        {
            if (_kind[h] == Kind.Pb) _held[h]?.Phys?.VrRelease(h);
            _held[h] = null; _bone[h] = -1; _kind[h] = Kind.None; _changed[h] = false; _pins[h].Clear(); _giz[h] = -1; _fine[h] = false;
        }

        /// <summary>VR 끝·끔: 잡고 있던 것은 놓은 것으로 기록하고 숨긴다.</summary>
        public void End()
        {
            _selBody = null; _sel = -1;
            for (int h = 0; h < 2; h++) { if (_held[h] != null && _bodies.Contains(_held[h])) Release(h); else Drop(h); _hover[h] = -1; _hoverBody[h] = null; _hoverKind[h] = Kind.None; _hoverGiz[h] = -1; _hasTip[h] = false; }
            if (_root != null) _root.SetActive(false);
            Show();   // 잡을 것이 없으면 잡는 점 공도 숨긴다
        }

        // ---- 그리기: 뼈대 선·막대·상자 선 하나(메시) + 관절마다 공 (눈 전용 레이어, 깊이 무시) ----
        static readonly Color HeldColor = new Color(1f, 0.55f, 0.15f, 0.95f), HoverColor = new Color(1f, 0.9f, 0.2f, 0.95f);

        bool Is(Body b, Kind kind, int bone, bool held)
        {
            for (int h = 0; h < 2; h++)
                if (held ? _held[h] == b && _kind[h] == kind && _bone[h] == bone : _hoverBody[h] == b && _hoverKind[h] == kind && _hover[h] == bone) return true;
            return false;
        }

        void Draw(bool show)
        {
            if (!show) { if (_root != null) _root.SetActive(false); return; }
            if (_root == null) Make();
            _root.SetActive(true);
            _lv.Clear(); _lc.Clear(); _li.Clear();
            var line = new Color(0.55f, 0.85f, 1f, 0.55f);
            var stick = new Color(0.45f, 1f, 0.6f, 0.75f);
            float s = S;
            _used = 0;
            foreach (var b in _bodies)
            {
                bool wholeHeld = Is(b, Kind.Whole, -1, true), wholeHover = Is(b, Kind.Whole, -1, false);
                if (wholeHeld || wholeHover) Box(b.Box, wholeHeld ? new Color(1f, 0.55f, 0.15f, 0.9f) : new Color(1f, 0.9f, 0.2f, 0.8f));
                if (!b.Poser.Ready || Locked) continue;
                if (b == _selBody) DrawGiz(b);   // 손이 멀어도 고른 관절의 핸들은 보인다
                var near = b.Box; near.Expand(2 * ShowRadius * _k);
                bool any = false;
                for (int h = 0; h < 2; h++) if (_hands[h] != null && _hands[h].Tracked && near.Contains(HandWorld(h))) any = true;
                if (!any && _held[0] != b && _held[1] != b) continue;   // 손이 먼 아바타는 뼈대를 안 그린다
                var poser = b.Poser;
                for (int i = 0; i < poser.BoneCount; i++)
                {
                    if (!Grabbable(poser, i)) continue;
                    var t = poser.BoneTransform(i);
                    int p = poser.BoneParent(i);
                    if (p >= 0 && poser.BoneTransform(p) != null) Line(poser.BoneTransform(p).position, t.position, line);
                    bool held = Is(b, Kind.Joint, i, true), hover = Is(b, Kind.Joint, i, false);
                    float r = (IsFinger(poser, i) ? FingerBall : Ball) * s;
                    if (b == _selBody && i == _sel) continue;   // 고른 관절은 DrawGiz가 그린다 (막대도 핸들과 겹쳐 뺀다)
                    Dot(t.position, held || hover ? r * 1.5f : r, held ? HeldColor : hover ? HoverColor : new Color(0.85f, 0.95f, 1f, 0.6f));
                    if (!HasStick(poser, i)) continue;
                    var e = StickEnd(b, i);
                    bool sh = Is(b, Kind.Stick, i, true), sv = Is(b, Kind.Stick, i, false);
                    var sc = sh ? HeldColor : sv ? HoverColor : stick;
                    Line(t.position, e, sc);
                    Dot(e, (sh || sv ? 1.5f : 1f) * StickBall * s, sc);
                }
                // PhysBone: 가리킨·잡은 관절(보라 → 노랑·주황)과 고정한 관절(분홍)
                if (b.Phys == null) continue;
                for (int h = 0; h < 2; h++)
                {
                    if (_hoverBody[h] == b && _hoverKind[h] == Kind.Pb) Dot(_hoverPb[h], PbBall * s * 1.5f, HoverColor);
                    if (_held[h] == b && _kind[h] == Kind.Pb && b.Phys.VrHolding(h)) Dot(b.Phys.VrHeldPos(h), PbBall * s * 1.5f, HeldColor);
                }
                _pbPins.Clear(); b.Phys.PinPositions(_pbPins);
                foreach (var pp in _pbPins) Dot(pp, PbBall * s, new Color(1f, 0.45f, 0.8f, 0.9f));
            }
            for (int k = _used; k < _balls.Count; k++) _balls[k].T.gameObject.SetActive(false);
            _lines.Clear();
            _lines.SetVertices(_lv); _lines.SetColors(_lc);
            _lines.SetIndices(_li, MeshTopology.Lines, 0);
            _lines.RecalculateBounds();
        }

        static readonly Color[] AxisColors = { new Color(1f, 0.32f, 0.32f, 0.95f), new Color(0.4f, 0.95f, 0.35f, 0.95f), new Color(0.35f, 0.6f, 1f, 0.95f) };
        static readonly Color SelColor = new Color(0.3f, 0.95f, 1f, 0.95f);

        bool IsGiz(int g, bool held)
        {
            for (int h = 0; h < 2; h++)
                if (held ? _held[h] == _selBody && _giz[h] == g && (_kind[h] == Kind.Ring || _kind[h] == Kind.Arrow) : _hoverBody[h] == _selBody && _hoverGiz[h] == g) return true;
            return false;
        }

        /// <summary>고른 관절: 하늘색 공 + 본 로컬 X·Y·Z 고리(두 겹으로 굵게) + 고리 밖 화살표(끝에 공). 가리키면 노랑, 잡으면 주황.</summary>
        void DrawGiz(Body b)
        {
            var poser = b.Poser;
            var c = poser.BoneTransform(_sel).position;
            float R = GizRadius(b, _sel), s = S;
            bool joint = Is(b, Kind.Joint, _sel, true) || Is(b, Kind.Joint, _sel, false);
            Dot(c, (IsFinger(poser, _sel) ? FingerBall : Ball) * s * 1.5f, joint ? HoverColor : SelColor);
            bool arrows = GizArrows(b, _sel);
            for (int k = 0; k < 3; k++)
            {
                var ax = GizAxis(b, _sel, k);
                bool rh = IsGiz(3 + k, true), rv = IsGiz(3 + k, false);
                var rc = rh ? HeldColor : rv ? HoverColor : AxisColors[k];
                Circle(c, ax, R, rc);
                Circle(c, ax, R * 1.025f, rc);
                if (!arrows) continue;
                bool ah = IsGiz(k, true), av = IsGiz(k, false);
                var ac = ah ? HeldColor : av ? HoverColor : AxisColors[k];
                var e = c + ax * (R * ArrowTo);
                Line(c + ax * (R * ArrowFrom), e, ac);
                Dot(e, StickBall * s * (ah || av ? 1.5f : 1f), ac);
            }
        }

        void Circle(Vector3 c, Vector3 axis, float r, Color col)
        {
            var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(axis, u);
            const int n = 40;
            var prev = c + u * r;
            for (int i = 1; i <= n; i++)
            {
                float a = i * Mathf.PI * 2 / n;
                var p = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                Line(prev, p, col);
                prev = p;
            }
        }

        void Line(Vector3 a, Vector3 b, Color c)
        {
            _li.Add(_lv.Count); _lv.Add(a); _lc.Add(c);
            _li.Add(_lv.Count); _lv.Add(b); _lc.Add(c);
        }

        void Dot(Vector3 pos, float r, Color c)
        {
            if (_used == _balls.Count) _balls.Add(MakeBall());
            var (bt, m) = _balls[_used++];
            bt.gameObject.SetActive(true);
            bt.position = pos;
            bt.localScale = Vector3.one * r;
            m.color = c;
        }

        Vector3 _rigPos; Quaternion _rigRot = Quaternion.identity; Vector3 _rigScale = Vector3.one;
        Vector3 HandWorld(int h) => _rigPos + _rigRot * Vector3.Scale(_rigScale, _hands[h].Position);

        void SetRig(Transform rig) { _rigPos = rig.position; _rigRot = rig.rotation; _rigScale = rig.lossyScale; }

        void Box(Bounds b, Color c)
        {
            Vector3 a = b.min, z = b.max;
            Vector3 P(int i) => new Vector3((i & 1) == 0 ? a.x : z.x, (i & 2) == 0 ? a.y : z.y, (i & 4) == 0 ? a.z : z.z);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) Line(P(i), P(i | bit), c);
        }

        void Make()
        {
            _root = new GameObject("VrBoneGrab") { layer = VrLayers.VrOnly };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);   // 몸 속 관절도 보이게
            _mat.renderQueue = 3100;
            _lines = new Mesh { name = "VrBoneLines" };
            _lines.MarkDynamic();
            var lg = new GameObject("VrBoneLines") { layer = VrLayers.VrOnly };
            lg.transform.SetParent(_root.transform, false);
            lg.AddComponent<MeshFilter>().sharedMesh = _lines;
            var lr = lg.AddComponent<MeshRenderer>();
            lr.sharedMaterial = _mat;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            _ballMesh = VrControllers.MakeBall();
        }

        (Transform, Material) MakeBall()
        {
            var go = new GameObject("VrBoneBall") { layer = VrLayers.VrOnly };
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _ballMesh;
            var r = go.AddComponent<MeshRenderer>();
            var m = new Material(_mat) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3101 };
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return (go.transform, m);
        }
    }
}
