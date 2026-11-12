// VRChat PhysBone 미리보기 시뮬레이션 (IR physBones / physBoneColliders).
// VRChat의 실제 구현은 공개되지 않았으므로 문서에 나온 파라미터 의미에 맞춘 근사다 (위치 기반 적분).
// 2026-11-12에 핵심(스텝·시간 환산·관성·각도 제한·충돌·뼈에 적기)을 다시 썼다. 잡기·표시·움직임 시험은 PhysBoneGrab.cs.
//
//   스텝: 값은 60 Hz 스텝 기준. 프레임마다 그 프레임 시간을 1~4 스텝으로 나눠 돌고 값을 스텝 길이에 맞게 환산한다 (Rates).
//     Pull은 관성(Spring)이 없으면 "남은 거리의 몫", 관성이 클수록 "힘"이라 스텝 길이의 제곱에 가깝게 줄여야 30·60·90·144 Hz에서 같은 움직임이 된다.
//     프레임 시간이 스텝 길이를 조금 넘는다고 스텝을 늘리지 않는다 (StepSlack: 60fps에서 프레임마다 1↔2 스텝을 오가던 것).
//     루트(움직이지 않는 관절)와 콜라이더는 스텝 사이에 지난 프레임 자리 → 이번 자리로 옮겨 간다 (콜라이더만 앞서 가면 몸이 움직일 때 체인을 잘못 민다).
//   Pull: 쉬는 자세로 돌아가려는 힘 · Spring(Momentum): 움직임을 이어 가는 정도(흔들림) · Stiffness(Advanced): 쉬는 자세를 지키는 정도
//   몸에 대한 움직임: 관절은 몸(쉬는 자리)과 함께 움직이고, 감쇠·복원은 몸에 대한 속도·위치에만 건다 (월드 기준이면 걸을 때 바람에 날리듯 뒤로 눕는다).
//     몸이 빨라지거나 멈추면 그 속도 변화만큼을 관성으로 받는다 — 이 몫은 Spring과 무관하게 전부 받고, 얼마나 밀리는지는 Pull이 정한다
//     (Pull이 약한 본은 많이 처지고 Pull이 센 본은 거의 안 움직인다. 예전에는 관성에도 Spring을 곱해 Spring이 작은 본이 단단해 보였다).
//   무게감(Weight): 몸에 대한 움직임을 √W배 느린 시간으로 돌린다. 값을 깎지 않으므로 Pull 1은 그대로 단단하고 Spring 0은 그대로 출렁이지 않는다
//     (예전에는 Pull ÷ W · 감쇠 ÷ √W로 값을 바꿔 단단한 본은 무르게, 출렁이는 본은 덜 출렁이게 했다).
//   Gravity / Gravity Falloff: 쉬는 방향을 아래(음수면 위)로 기울임. Falloff는 불러온 자세(쉬는 자세)에서 중력을 빼는 정도
//   Immobile: 아바타(또는 루트) 움직임을 얼마나 무시하고 같이 움직일지 (All Motion: 루트의 위치·회전, World: 아바타 루트의 위치만)
//   Limits: Angle(원뿔) / Hinge(평면) / Polar(타원 원뿔). 벗어나면 제한 범위에서 가장 가까운 자리로 옮긴다
//     (Polar를 원뿔 가운데 쪽으로 당기던 예전 방식은 옆으로 조금만 벗어나도 치마·리본을 쉬는 자세보다 한참 위로 들어 올렸다).
//   순간이동: 체인 루트가 한 프레임에 크게 옮겨지거나 돌거나 크기가 바뀌면(스냅 회전, 방 크기 배율, 아바타 옮기기) 체인을 통째로 같이 옮긴다
//   Stretch·Squish: 뼈 길이 허용 범위. 움직임으로는 Stretch Motion만큼만 늘고 줄고, 잡기·충돌은 범위 끝까지 · Collider: Sphere / Capsule / Plane, Inside Bounds
//   Version: 1.1은 Gravity가 쉬는 자세가 돌아가는 비율(Pull이 있어야 그쪽으로 간다)·Stiffness가 직전 방향을 지키는 비율. 1.0은 Gravity가 Pull과 겨루는 힘이고 줄어듦·움직임으로 늘어남이 없다
//   꺼짐: 컴포넌트가 붙은 오브젝트나 루트가 꺼져 있으면 돌지 않는다. Reset When Disabled면 꺼질 때 쉬는 자세로 · Is Animated: 쉬는 자세가 밖에서 바뀐 뼈 값을 따라간다
//   충돌은 PhysBone에 적힌 콜라이더 목록과 한다. Allow Collision은 "목록 밖 콜라이더(다른 사람의 손 등)"를 받을지라서 목록과는 무관하다.
//   뼈는 반지름을 가진 캡슐(부모 관절 → 자식 관절)로 부딪힌다. 콜라이더에 Bones As Spheres가 켜져 있으면 관절 구로만.
// 값마다 커브가 있으면 체인 위치(루트 0 → 끝 1)의 커브 값을 곱한다.
// 매 프레임: 쉬는 자세의 관절 자리를 계산(뼈를 되돌려 놓지 않고 루트 부모의 행렬 × 쉬는 로컬 값으로) → 스텝마다 적분·제약·충돌 → 바뀐 뼈만 회전을 적는다.
//   가만히 있으면 뼈에 아무것도 적지 않는다 (예전에는 프레임마다 모든 뼈를 쉬는 자세로 되돌렸다가 다시 돌려 계산 시간의 대부분을 썼다).
//   잠들기: 몸·콜라이더·설정이 그대로이고 체인이 0.5초쯤 움직이지 않으면 스텝을 건너뛴다. 무엇이든 바뀌면 그 프레임에 깬다 (Tick의 busy).
// Time.deltaTime을 쓰므로 셰이더 시간 멈춤(pauseTime, timeScale 0)이면 시뮬레이션도 멈춘다. 그때는 Advance(바로 돌리기)로만 돈다 (촬영·E2E 결과가 매번 같게).
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed partial class PhysBoneSim
    {
        const float Step = 1f / 60f;       // 값(Pull·Spring 등)이 맞춰진 스텝 길이
        const int MaxSteps = 4;            // 한 프레임 최대 스텝 (그 이상 걸린 프레임의 시간은 버린다)
        const float MinStep = 1f / 240f;   // 이보다 짧은 프레임은 모았다가 돈다 (그동안은 몸에 붙어 따라간다)
        const float StepSlack = 0.25f;     // 프레임 시간이 스텝 길이의 이 몫까지 넘쳐도 스텝을 늘리지 않는다 (60fps ±1 ms 흔들림에 1↔2 스텝을 오가지 않게)
        const float JumpDist = 0.3f, JumpAngle = 20f; // 한 프레임에 체인 루트가 이만큼(아바타 크기 1 기준 m, 도) 넘게 움직이면 순간이동으로 본다
        const float LenReturn = 0.25f;     // 잡기·충돌로 늘거나 준 뼈가 움직임 범위로 돌아오는 빠르기 (60 Hz 스텝마다 남은 차이의 몫)
        const float V10GravityPull = 0.2f; // Version 1.0 Gravity의 힘 세기 (Pull과 같은 단위)
        const int SleepAfter = 30;         // 이만큼의 프레임 동안 움직이지 않으면 잠든다
        const float SleepMove = 2e-5f;     // "움직이지 않음": 한 프레임에 이보다 덜 움직임 (아바타 크기 1 기준 m = 0.02 mm)
        static readonly Vector3 Unset = new Vector3(float.NaN, float.NaN, float.NaN);

        sealed class Particle
        {
            public Transform T;            // null = 끝점(endpointPosition)으로 더한 가상 입자
            public Transform Owner;        // 끝점: 붙은 뼈
            public Vector3 EndLocal;
            public int Parent = -1;
            public readonly List<int> Children = new List<int>();
            public Quaternion RestLocalRot; public Vector3 RestLocalPos, RestLocalScale;
            public float T01; public int Depth;  // 체인 위치 (루트 0 → 끝 1), 루트에서 몇 번째
            public float BindDown;         // 불러온 자세에서 부모 → 이 관절 방향이 아래를 향한 정도 (Gravity Falloff 기준)
            public bool Fixed;             // 움직이지 않는 입자: 루트, 그리고 Multi Child Type이 Ignore인 루트의 자식들 (각각 체인의 시작)
            public bool Placed;            // 늘고 주는 뼈: 자리도 시뮬레이션 자리로 적는다
            // 매 프레임: 쉬는 자세
            public Matrix4x4 RestM;        // 쉬는 자세의 월드 행렬 (부모 행렬 × 쉬는 로컬 값)
            public Vector3 Target; public Quaternion RestRot;
            public Quaternion NowRot; public Vector3 NowPos, NowScale;   // 이번 프레임에 뼈에 들어 있던 로컬 값
            public Vector3 LastTarget;     // 지난번 스텝을 돈 프레임의 Target (몸이 움직인 속도)
            // 시뮬레이션
            public Vector3 Pos, Prev;
            public Vector3 BodyV, BodyF;   // 몸(쉬는 자리) 속도와 FollowTime으로 거른 값 (m/s)
            public Quaternion Rot = Quaternion.identity; // 쉬는 자세 → 시뮬레이션 자세 회전 (부모부터 누적)
            // 입자별 값 (커브 적용)
            public float Pull, Spring, Stiffness, Gravity, GravityFalloff, Immobile, Radius, MaxAngleX, MaxAngleZ, MaxStretch, MaxSquish, StretchMotion;
            public Vector3 LimitRot; public Quaternion LimitQ = Quaternion.identity;   // Limit Rotation (오일러 각과 그 회전)
            // 이 스텝 길이로 환산한 값 (Rates): 속도를 이어 가는 몫 · Pull · Version 1.0 Gravity · Stiffness
            public float Keep, Draw, Fall, Stiff, Kick = 1, RateFor = -1; public int RateVer = -1;   // Kick: 몸 속도 변화(관성)에 곱하는 값
            public Vector3 Still;          // Immobile: 이번 프레임 몸 움직임 가운데 "같이 움직인 것으로 치는" 몫 (관성에서 뺀다)
            public float LenRatio = 1;     // 지금 뼈 길이 / 쉬는 길이 (잡았다 놓은 뒤 천천히 돌아오게)
            // 뼈에 적기
            public Quaternion Delta = Quaternion.identity, World = Quaternion.identity; // 쉬는 자세 → 보이는 자세 (월드), 뼈의 월드 회전
            public Vector3 At;             // 뼈가 실제로 놓인 자리 (끝점은 보이는 자리)
            public Quaternion SetRot, ShownRot; public Vector3 SetAt = Unset, ShownPos, ShownScale; // 지난번에 적은 값과 적은 뒤 뼈에 든 값 (이와 다르면 밖에서 바꾼 것)
            public bool Moved;             // 이번 프레임에 이 뼈나 조상 뼈를 옮겼다
            /// <summary>불러온 자세(원본이 만든 그대로)에서 이 관절이 콜라이더(번호)와 이미 겹쳐 있던 깊이. 이만큼은 원본 의도로 보고 봐준다.</summary>
            public readonly Dictionary<int, float> BindPen = new Dictionary<int, float>();
            // 떨림 재기 (Jitter): 프레임마다 보이는 위치의 2차 차분 크기 평균
            public Vector3 F1, F2;
            public Vector3 Show, Dev; // 화면에 보이는 위치와 그 쉬는 자리에서 벗어난 정도 (Smooth)
            public int Frames; public float Jitter;
            public float PeakUp, PeakFar; // 시험용: ResetPeaks 뒤 쉬는 자리에서 가장 높이 뜬 높이·가장 멀리 벗어난 거리 (아바타 크기 1 기준 m)
        }

        sealed class Chain
        {
            public IrPhysBone Src;
            public Transform Root, Comp, Parent;   // Comp: 컴포넌트가 붙은 오브젝트 (루트와 다를 수 있다), Parent: 루트의 부모
            public bool Active = true, ResetWhenDisabled, IsAnimated, V11; // V11: Version 1.1
            public readonly List<Particle> P = new List<Particle>();
            public readonly List<int> Colliders = new List<int>();
            public int LimitType, ImmobileType, MultiChild;
            public bool Advanced, Grabbable, Posable, SoftTarget; // SoftTarget: 말랑 모드 대상 (PhysBoneSoft.cs)
            public bool AnyImmobile;
            public float GrabMovement;
            public Vector3 LastPos; public Quaternion LastRot; public bool HasLast;   // Immobile 기준 (지난 프레임 루트)
            public Vector3 JumpPos; public Quaternion JumpRot; public float JumpScale; // 지난 프레임 체인 루트 (순간이동 알아보기)
            public readonly List<int> LegColliders = new List<int>(); // 다리 뚫림 막기로 더한 콜라이더 (PhysBoneLegGuard.cs)
            public readonly List<int> Near = new List<int>();         // 이번 프레임에 닿을 수 있는 콜라이더
            public int Depth;
            // 매 프레임
            public bool Carried;   // 이번 프레임에 순간이동으로 통째로 옮겼다 (콜라이더를 지난 자리에서 이어 오지 않는다)
            public float Scale = 1; public Quaternion ParentRot = Quaternion.identity; public bool Stir;   // 루트 크기(아바타 크기), 루트 부모의 회전, 몸이 움직였는지
            public Matrix4x4 StirM;   // 지난 프레임 루트의 쉬는 행렬
            public bool RestChanged, Touched;   // 이번 프레임: 쉬는 값이 밖에서 바뀜(Is Animated) · 뼈에 든 값이 지난번에 적은 뒤와 다름
            public bool Sleep; public int Calm; public float MaxMove;   // 잠듦 · 움직이지 않은 프레임 수 · 이번 프레임에 몸에 대해 가장 많이 움직인 거리²
        }

        sealed class Col
        {
            public Transform T; public bool Enabled;
            public int Shape; public bool Inside, AsSpheres, Auto; public float Radius, Height; // Auto: 다리 뚫림 막기로 더한 것
            public Vector3 Pos; public Quaternion Rot = Quaternion.identity;
            // 매 프레임: 지금 쓰는 값 (스텝 사이에는 지난 프레임 → 이번 프레임을 잇는다)
            public Vector3 C, A, B, N; public float R, S = 1; public bool Active;
            public Vector3 C0, A0, B0, N0, C1, A1, B1, N1; public bool HasPrev;
            public bool Changed, WasActive; public float R0;   // 지난 프레임과 달라졌는지 (잠든 체인을 깨운다)

            public void Blend(float t)
            {
                C = Vector3.Lerp(C0, C1, t); A = Vector3.Lerp(A0, A1, t); B = Vector3.Lerp(B0, B1, t);
                var n = Vector3.Lerp(N0, N1, t); N = n.sqrMagnitude > 1e-6f ? n.normalized : N1;
            }
            public void Keep() { C0 = C1; A0 = A1; B0 = B1; N0 = N1; }
        }

        readonly List<Chain> _chains = new List<Chain>();
        readonly List<Col> _cols = new List<Col>();
        float _acc, _h = Step;   // 모은 시간, 지난 스텝 길이 (Verlet 속도를 다음 스텝 길이로 환산)
        Transform _sceneRoot;
        // 아바타 크기 (장면 루트의 배율). "아바타 크기 1 기준" 값(순간이동 거리·속도 상한·파고듦 허용·잔떨림 거르기)에 곱한다.
        // 체인 루트·콜라이더 뼈의 배율은 반지름에만 쓴다 (2026-11-12: 배율 0.09인 뼈에 붙은 콜라이더는 2.7 cm만 움직여도 순간이동으로 봐,
        // 30fps로 걸으면 스텝 사이를 잇지 않고 앞서 가서 치마 뒤쪽이 30 cm 솟았다. 배율 0.02인 체인은 속도 상한이 1/50이었다)
        float _unit = 1;
        public bool Enabled = true;
        /// <summary>
        /// 무게감 (1 = 값 그대로의 빠르기). 몸에 대한 움직임을 √W배 느린 시간으로 돌린다: 출렁이는 성질(감쇠비)·단단함의 순서는 원본 값 그대로이고,
        /// 몸이 빨라지거나 멈출 때 더 많이 밀렸다가 천천히 돌아온다. 그리고 몸에 대해 흔들리는 속도를 SlowSwing/√W로 묶는다.
        /// 60 Hz 스텝마다 값을 그대로 넣으면 제자리로 너무 빨리 돌아와 가벼워 보여서 기본은 4 (Host 프리셋 1.5 · 4 · 10).
        /// </summary>
        public float Weight = 4f;
        // 느낌 (2026-10-23 사용자: "작고 빠른 움직임에 반응하지 않게, 한참 더 천천히, 중력은 더, 더 단단하고 복원력도 더, 부딪히면 떨지 말고 제자리에"):
        // 2026-10-24 "너무 단단해, 관성을 더" → 기본값을 그 사이로 옮기고 Host 설정 › 미리보기 PhysBone › 느낌에서 바꾸게 (setPhysBones { feel: { 열쇠: 값 } }).
        // 기본값은 Host `PhysBoneFeel.Items`와 같게 둔다 (Host가 켤 때 모두 보내므로 Host 없이 도는 E2E에서만 이 값을 쓴다).
        public float FollowTime = 0.1f;   // follow: 몸 움직임의 빠르기 변화를 이 시간(초)으로 걸러 느낀다 — 길수록 작고 빠른 움직임(고개 끄덕임·잔걸음)에 덜 흔들린다. 0 = 거르지 않음
        public float SlowSwing = 1.6f;    // swing: 무게 1일 때 몸에 대한 흔들림 속도 상한 (m/s, 실제 상한은 ÷√무게)
        // pull · damp (2026-11-12): 값에 곱하지 않고 지수로 건다 — 0은 0, 1은 1로 남아 단단한 본·출렁이지 않는 본이 그대로다 (작은 값에서는 곱하는 것과 같다).
        public float FirmPull = 1.5f;     // pull: 1 − (1 − Pull)^값 — 클수록 쉬는 자세로 단단히 돌아온다
        public float FirmDamp = 0.9f;     // damp: Spring^(1/값) — 클수록 관성이 남아 더 출렁인다
        public float GravityScale = 1.5f, FalloffScale = 0.7f; // gravity · falloff: Gravity 배율, Falloff로 빼는 몫의 배율
        public float Slop = 0.004f;       // slop: 콜라이더에 이만큼(아바타 크기 1 기준 m)까지는 파고들어도 그냥 둔다 (밀고 당기며 떨지 않게)
        public float ContactFriction = 0.7f; // friction: 닿은 채 미끄러지는 속도를 스텝마다 이만큼 줄인다 (1 = 닿은 자리에 멈춤)
        /// <summary>콜라이더에 닿은 관절의 튕겨 나가는 속도를 없앤다 (끄면 밀어낸 만큼이 속도로 남아 떨림: 비교 시험용).</summary>
        public bool Inelastic = true;
        /// <summary>화면에 보일 위치를 거른다 (Smooth). 끄면 시뮬레이션 위치 그대로: 비교 시험용.</summary>
        public bool Smoothing = true;
        float SmoothBand = 0.006f; // smooth: 이 정도(아바타 크기 1 기준 m) 움직임이면 반만 따라간다 (2026-10-23 3 → 6 mm: 닿은 관절이 제자리에 있게)
        int _feelVer; SoftPreset _kSoft; readonly float[] _kSet = new float[12];   // 설정이 바뀌었는지 (Rates를 다시 계산하고 잠든 체인을 깨운다)

        /// <summary>함께 세운 아바타(GuestAvatars): 지금 아바타의 켜짐·무게·느낌을 그대로 따른다 (매 프레임, 바뀐 것만).</summary>
        public void CopyFrom(PhysBoneSim o)
        {
            if (Enabled != o.Enabled) SetEnabled(o.Enabled);
            Weight = o.Weight; Inelastic = o.Inelastic; Smoothing = o.Smoothing;
            FollowTime = o.FollowTime; SlowSwing = o.SlowSwing; FirmPull = o.FirmPull; FirmDamp = o.FirmDamp;
            GravityScale = o.GravityScale; FalloffScale = o.FalloffScale; Slop = o.Slop; ContactFriction = o.ContactFriction; SmoothBand = o.SmoothBand;
        }

        /// <summary>느낌 값 (Host 설정). 없는 열쇠는 그대로.</summary>
        public void SetFeel(VrcProto.Protocol.JNode f)
        {
            float G(string k, float cur, float lo, float hi) => f.Has(k) ? Mathf.Clamp((float)f[k].AsDouble, lo, hi) : cur;
            FollowTime = G("follow", FollowTime, 0, 1);
            SlowSwing = G("swing", SlowSwing, 0.1f, 10);
            FirmPull = G("pull", FirmPull, 0.1f, 5);
            FirmDamp = G("damp", FirmDamp, 0, 2);
            GravityScale = G("gravity", GravityScale, 0, 5);
            FalloffScale = G("falloff", FalloffScale, 0, 1);
            Slop = G("slop", Slop * 1000, 0, 30) / 1000;
            ContactFriction = G("friction", ContactFriction, 0, 1);
            SmoothBand = G("smooth", SmoothBand * 1000, 0, 30) / 1000;
        }

        public int ChainCount => _chains.Count;
        /// <summary>시뮬레이션이 움직이는 뼈 (고정 루트 제외). 바닥 맞추기가 흔들리는 천·머리카락을 몸에 붙은 것으로 세지 않게.</summary>
        public HashSet<Transform> MovingBones() { var s = new HashSet<Transform>(); foreach (var c in _chains) foreach (var p in c.P) if (p.T != null && !p.Fixed) s.Add(p.T); return s; }
        public int BoneCount { get { int n = 0; foreach (var c in _chains) foreach (var p in c.P) if (p.T != null) n++; return n; } }
        /// <summary>Tick 한 번에 걸린 시간 (ms, 최근 평균). 시험·통계용.</summary>
        public float TickMs { get; private set; }
        /// <summary>그 가운데 쉬는 자리 계산 · 스텝 · 뼈에 적기에 걸린 시간 (ms, 최근 평균).</summary>
        public string TickParts => $"쉬는 자리 {_partMs[0]:0.000} · 스텝 {_partMs[1]:0.000} · 적기 {_partMs[2]:0.000}";
        readonly float[] _partMs = new float[3]; readonly long[] _part = new long[3];

        // ---------------- 만들기 ----------------

        public void Build(SceneBuilder scene)
        {
            Restore(); _chains.Clear(); _cols.Clear(); _acc = 0; StopMotion(); _grab = _released = null; _pins.Clear(); _vrGrab[0] = _vrGrab[1] = null;
            var ir = scene.Scene;
            _sceneRoot = scene.Root;
            if (ir == null) return;
            foreach (var c in ir.PhysBoneColliders)
            {
                var v = c.Vec("rotation", new float[] { 0, 0, 0, 1 });
                _cols.Add(new Col
                {
                    T = scene.NodeTransform(c.RootNode), Enabled = c.Enabled,
                    Shape = (int)c.Get("shapeType"), Inside = c.Get("insideBounds") > 0, AsSpheres = c.Get("bonesAsSpheres") > 0, Radius = c.Get("radius"), Height = c.Get("height"),
                    Pos = V(c.Vec("position", new float[] { 0, 0, 0 })),
                    Rot = v.Length >= 4 ? new Quaternion(v[0], v[1], v[2], v[3]) : Quaternion.identity,
                });
            }
            // 다른 PhysBone의 루트는 그 PhysBone이 맡는다 (부모 체인에서 뺌)
            var otherRoots = new HashSet<Transform>();
            foreach (var pb in ir.PhysBones) if (pb.Enabled && scene.NodeTransform(pb.RootNode) is Transform t) otherRoots.Add(t);
            foreach (var pb in ir.PhysBones)
            {
                if (!pb.Enabled) continue;
                var root = scene.NodeTransform(pb.RootNode);
                if (root == null) continue;
                var ignore = new HashSet<Transform>();
                foreach (var n in pb.Ignore) if (scene.NodeTransform(n) is Transform it) ignore.Add(it);
                var ch = new Chain
                {
                    Src = pb, Root = root, Parent = root.parent, Comp = scene.NodeTransform(pb.Node) is Transform comp ? comp : root,
                    ResetWhenDisabled = pb.Get("resetWhenDisabled") > 0, IsAnimated = pb.Get("isAnimated") > 0, V11 = pb.Get("version") >= 1,
                    LimitType = (int)pb.Get("limitType"), ImmobileType = (int)pb.Get("immobileType"), MultiChild = (int)pb.Get("multiChildType"),
                    Advanced = pb.Get("integrationType") > 0,
                    Grabbable = pb.Get("allowGrabbing", 1) != 0, Posable = pb.Get("allowPosing", 1) != 0, GrabMovement = Mathf.Clamp01(pb.Get("grabMovement", 0.5f)),
                };
                foreach (var ci in pb.Colliders) if (ci >= 0 && ci < _cols.Count) ch.Colliders.Add(ci);
                var end = V(pb.Vec("endpointPosition", new float[] { 0, 0, 0 }));
                AddParticle(ch, root, -1, 0, ignore, otherRoots, end);
                if (ch.P.Count < 2) continue;
                // Ignore: 자식이 여럿인 루트는 무시하고 그 자식들이 각 체인의 루트가 된다 (스커트처럼 루트 하나에 여러 가닥)
                ch.P[0].Fixed = true;
                if (ch.MultiChild == 0 && ch.P[0].Children.Count > 1) foreach (var ci in ch.P[0].Children) ch.P[ci].Fixed = true;
                int maxDepth = 1; foreach (var p in ch.P) maxDepth = Math.Max(maxDepth, p.Depth);
                var curves = new Dictionary<string, AnimationCurve>();
                foreach (var p in ch.P)
                {
                    p.T01 = p.Depth / (float)maxDepth; Params(pb, p, curves);
                    p.Placed = p.T != null && p.Parent >= 0 && (p.MaxStretch > 0 || p.MaxSquish > 0);
                    ch.AnyImmobile |= p.Immobile > 0 && !p.Fixed;
                }
                for (var t = root; t != null; t = t.parent) ch.Depth++;
                ch.SoftTarget = IsSoftTarget(root);
                _chains.Add(ch);
            }
            _chains.Sort((a, b) => a.Depth.CompareTo(b.Depth)); // 부모 쪽 체인을 먼저 (안쪽 PhysBone이 바깥 결과를 따라가게)
            BuildLegGuard(scene);
            Reset();
            foreach (var ch in _chains)
                foreach (var p in ch.P)
                    if (p.Parent >= 0 && ch.P[p.Parent].Target is var pt && (p.Target - pt).sqrMagnitude > 1e-12f) p.BindDown = Vector3.Dot((p.Target - pt).normalized, Vector3.down);
            MeasureBindPen();
        }

        void AddParticle(Chain ch, Transform t, int parent, int depth, HashSet<Transform> ignore, HashSet<Transform> otherRoots, Vector3 end)
        {
            int id = ch.P.Count;
            ch.P.Add(new Particle
            {
                T = t, Parent = parent, Depth = depth, RestLocalRot = t.localRotation, RestLocalPos = t.localPosition, RestLocalScale = t.localScale,
                ShownRot = t.localRotation, SetRot = t.localRotation, ShownPos = t.localPosition, ShownScale = t.localScale,
            });
            if (parent >= 0) ch.P[parent].Children.Add(id);
            int kids = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (ignore.Contains(c) || otherRoots.Contains(c)) continue;
                kids++;
                AddParticle(ch, c, id, depth + 1, ignore, otherRoots, end);
            }
            if (kids == 0 && end.sqrMagnitude > 1e-12f)
            {
                ch.P.Add(new Particle { Owner = t, EndLocal = end, Parent = id, Depth = depth + 1 });
                ch.P[id].Children.Add(ch.P.Count - 1);
            }
        }

        static void Params(IrPhysBone pb, Particle p, Dictionary<string, AnimationCurve> curves)
        {
            float C(string k, float def = 0) => pb.Get(k, def) * CurveOnly(pb, k + "Curve", p.T01, curves);
            p.Pull = Mathf.Clamp01(C("pull", 0.2f));
            p.Spring = Mathf.Clamp(C("spring", 0.2f), 0, 0.98f); // 1이면 감쇠가 없어 멈추지 않는다
            p.Stiffness = Mathf.Clamp01(C("stiffness"));
            p.Gravity = C("gravity"); p.GravityFalloff = Mathf.Clamp01(C("gravityFalloff"));
            p.Immobile = Mathf.Clamp01(C("immobile"));
            p.Radius = Mathf.Max(0, C("radius"));
            p.MaxAngleX = C("maxAngleX", 45); p.MaxAngleZ = C("maxAngleZ", 45);
            // Version 1.0에는 줄어듦과 움직임으로 늘어남이 없다 (VRChat 문서: 둘 다 1.1부터). Stretch Motion이 0이면 잡기·충돌로만 늘고 준다
            bool v11 = pb.Get("version") >= 1;
            p.MaxStretch = Mathf.Max(0, C("maxStretch")); p.MaxSquish = v11 ? Mathf.Clamp01(C("maxSquish")) : 0;
            p.StretchMotion = v11 ? Mathf.Clamp01(C("stretchMotion")) : 0;
            var lr = pb.Vec("limitRotation", new float[] { 0, 0, 0 });
            p.LimitRot = new Vector3(lr[0] * CurveOnly(pb, "limitRotationXCurve", p.T01, curves), lr[1] * CurveOnly(pb, "limitRotationYCurve", p.T01, curves), lr[2] * CurveOnly(pb, "limitRotationZCurve", p.T01, curves));
            p.LimitQ = Quaternion.Euler(p.LimitRot);
        }

        /// 커브 값 (커브 이름은 "<값>Curve", 없으면 1). 체인마다 커브는 한 번만 만든다
        static float CurveOnly(IrPhysBone pb, string key, float t, Dictionary<string, AnimationCurve> cache)
        {
            if (!cache.TryGetValue(key, out var curve))
            {
                if (pb.Curves.TryGetValue(key, out var k) && k.Length >= 4)
                {
                    var keys = new Keyframe[k.Length / 4];
                    for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(k[i * 4], k[i * 4 + 1], k[i * 4 + 2], k[i * 4 + 3]);
                    curve = new AnimationCurve(keys);
                }
                cache[key] = curve;
            }
            return curve != null ? curve.Evaluate(t) : 1;
        }

        // ---------------- 실행 ----------------

        /// <summary>뼈를 쉬는 자세로 되돌리고 입자를 그 자리에 둔다 (고정도 푼다).</summary>
        public void Reset()
        {
            Restore(); _pins.Clear();
            foreach (var c in _cols) c.HasPrev = false;
            foreach (var ch in _chains) Seat(ch);
            _acc = 0; _h = Step;
        }

        /// 입자를 지금 쉬는 자리에 둔다 (뼈는 쉬는 자세여야 한다)
        static void Seat(Chain ch)
        {
            Targets(ch);
            foreach (var p in ch.P)
            {
                p.Pos = p.Prev = p.Show = p.LastTarget = p.At = p.Target; p.Dev = Vector3.zero; p.BodyV = p.BodyF = p.Still = Vector3.zero;
                p.Rot = p.Delta = Quaternion.identity; p.World = p.RestRot; p.Frames = 0; p.Jitter = 0; p.LenRatio = 1;
            }
            // Immobile 기준은 다음 프레임에 다시 잡는다 (World는 아바타 루트가 기준이다: 예전에는 여기서 체인 루트 자리를 넣어,
            // 다시 놓은 첫 프레임에 Immobile World 체인(앞머리·옆머리)이 "아바타 루트 − 체인 루트"만큼 한 번 끌려갔다)
            ch.HasLast = false;
            ch.JumpPos = ch.P[0].Target; ch.JumpRot = ch.P[0].RestRot; ch.JumpScale = ch.Scale;
            ch.Sleep = false; ch.Calm = 0;
        }

        /// <summary>
        /// 이 체인을 돌릴지. 컴포넌트가 붙은 오브젝트나 루트가 꺼져 있으면 돌지 않고 뼈는 그 자리에 둔다 (Reset When Disabled면 꺼질 때 쉬는 자세로).
        /// 다시 켜지면 그동안 아바타가 움직였을 수 있으니 쉬는 자리에서 시작한다.
        /// </summary>
        static bool Awake(Chain ch)
        {
            bool on = ch.Comp.gameObject.activeInHierarchy && ch.Root.gameObject.activeInHierarchy;
            if (on == ch.Active) return on;
            ch.Active = on;
            if (!on) { if (ch.ResetWhenDisabled) RestoreChain(ch); }
            else { RestoreChain(ch); Seat(ch); }
            return on;
        }

        public void SetEnabled(bool on)
        {
            if (Enabled == on) return;
            Enabled = on;
            if (on) Reset(); else { Release(); VrRelease(0); VrRelease(1); Restore(); }
        }

        /// <summary>LateUpdate에서 부른다.</summary>
        public void Tick(float dt)
        {
            Motion = 0;
            if (dt <= 0) return;           // 시간 멈춤: 자세 유지
            UpdateMotion(dt);
            if (!Enabled || _chains.Count == 0) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool wake = _kSoft != _soft; _kSoft = _soft;
            void Key(int i, float v) { if (_kSet[i] != v) { _kSet[i] = v; wake = true; } }
            Key(0, Weight); Key(1, FirmPull); Key(2, FirmDamp); Key(3, GravityScale); Key(4, FalloffScale); Key(5, Slop); Key(6, ContactFriction);
            Key(7, FollowTime); Key(8, SlowSwing); Key(9, SmoothBand); Key(10, LegGuard ? 1 : 0); Key(11, (Inelastic ? 1 : 0) + (Smoothing ? 2 : 0));
            if (wake) _feelVer++;
            float moved = 0;
            // 프레임 시간을 같은 길이의 스텝 1~4개로 나눈다. 스텝 길이는 Step의 1.25배까지 (그보다 길면 하나 더)
            _acc = Mathf.Min(_acc + dt, Step * MaxSteps);
            int steps = _acc < MinStep ? 0 : Mathf.Clamp(Mathf.CeilToInt(_acc / Step - StepSlack), 1, MaxSteps);
            float h = steps > 0 ? _acc / steps : 0;
            if (steps > 0) _acc = 0;
            _unit = _sceneRoot != null ? Mathf.Max(1e-6f, Mathf.Abs(_sceneRoot.lossyScale.x)) : 1;
            foreach (var c in _cols) Sweep(c, JumpDist * _unit);
            foreach (var ch in _chains)
            {
                if (!Awake(ch)) continue;
                long ta = System.Diagnostics.Stopwatch.GetTimestamp();
                Targets(ch);
                Carry(ch);
                Immobile(ch);
                // 깨어 있어야 하는가: 설정·몸·쉬는 값·콜라이더가 바뀌었거나 잡고 있거나 말랑 모드(서로 민다)
                bool busy = wake || ch.Stir || ch.Carried || ch.RestChanged || ch.SoftTarget && _soft != null || Held(ch) || ColliderChanged(ch);
                if (busy) { ch.Sleep = false; ch.Calm = 0; }
                if (ch.Sleep)   // 잠든 체인: 스텝·뼈 적기를 건너뛴다 (뼈를 밖에서 건드렸으면 다시 적는다)
                {
                    if (ch.Touched) Apply(ch, SoftOf(ch));
                    _part[0] += System.Diagnostics.Stopwatch.GetTimestamp() - ta;
                    continue;
                }
                if (steps > 0) Nearby(ch);
                ch.MaxMove = 0;
                long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int s = 0; s < steps; s++) { Simulate(ch, s, steps, h, s == 0 ? _h : h); Holds(ch); }
                long tc = System.Diagnostics.Stopwatch.GetTimestamp();
                if (steps > 0) foreach (var p in ch.P) { p.LastTarget = p.Target; p.Still = Vector3.zero; }
                float shown = Smooth(ch, steps > 0);
                moved = Mathf.Max(moved, shown);
                Apply(ch, SoftOf(ch));
                if (steps > 0)
                {
                    MeasureJitter(ch);
                    float still = SleepMove * _unit;
                    if (busy || ch.MaxMove > still * still || shown > SleepMove) ch.Calm = 0;
                    else if (++ch.Calm >= SleepAfter)
                    {
                        ch.Sleep = true;
                        foreach (var p in ch.P) { p.Prev = p.Pos; p.BodyV = p.BodyF = Vector3.zero; p.Jitter = 0; }
                    }
                }
                _part[0] += tb - ta; _part[1] += tc - tb; _part[2] += System.Diagnostics.Stopwatch.GetTimestamp() - tc;
            }
            foreach (var c in _cols) { if (c.Active) c.Keep(); c.WasActive = c.Active; c.R0 = c.R; }
            if (steps > 0) _h = h;
            Motion = moved / dt;
            TrackPeaks();
            float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
            TickMs = TickMs <= 0 ? ms : Mathf.Lerp(TickMs, ms, 0.05f);
            for (int i = 0; i < 3; i++) { _partMs[i] = Mathf.Lerp(_partMs[i], _part[i] * 1000f / System.Diagnostics.Stopwatch.Frequency, 0.05f); _part[i] = 0; }
        }

        /// <summary>
        /// 바로 돌리기 (2026-11-12): 화면을 그리지 않고 fps 간격의 프레임을 seconds만큼 이어서 돈다. 촬영에서 자세를 바꾼 뒤 자리 잡기,
        /// E2E에서 기다리지 않고 매번 같은 결과를 얻는 데 쓴다 (실제 시간으로 기다리면 프레임 시간에 따라 결과가 조금씩 달랐다).
        /// </summary>
        public void Advance(float seconds, float fps)
        {
            float dt = 1f / Mathf.Clamp(fps, 10, 1000);
            int n = Mathf.Clamp(Mathf.RoundToInt(seconds / dt), 0, 20000);
            for (int i = 0; i < n; i++) Tick(dt);
        }

        /// 시험용: 관절마다 쉬는 자리에서 가장 많이 벗어난 거리와 가장 높이 뜬 높이 (아바타 크기 1 기준 m). ResetPeaks 뒤부터 잰다
        void TrackPeaks()
        {
            foreach (var ch in _chains)
            {
                if (!ch.Active || ch.Sleep) continue;
                foreach (var p in ch.P)
                {
                    if (p.Fixed || p.Parent < 0) continue;
                    var d = (p.At - p.Target) / ch.Scale;
                    if (d.y > p.PeakUp) p.PeakUp = d.y;
                    float m = d.sqrMagnitude; if (m > p.PeakFar * p.PeakFar) p.PeakFar = Mathf.Sqrt(m);
                }
            }
        }

        public void ResetPeaks() { foreach (var ch in _chains) foreach (var p in ch.P) p.PeakUp = p.PeakFar = 0; }

        /// <summary>
        /// 지난 Tick에서 쉬는 자리에 대해 가장 많이 흔들린 관절의 빠르기 (아바타 크기 1 기준 m/s). 렌더러가 가만히 있는지 볼 때 쓴다 (2026-11-02).
        /// 몸 전체가 움직인 것은 Target에 들어 있어 세지 않는다 — 그건 자세·옮기기 메시지나 움직임 시험(Testing)으로 안다.
        /// </summary>
        public float Motion { get; private set; }
        /// <summary>움직임 시험(걷기·점프 등)이 도는 중.</summary>
        public bool Testing => _motion != null;

        /// <summary>
        /// 쉬는 자세의 관절 자리. 뼈를 쉬는 자세로 되돌려 읽지 않고, 루트 부모의 행렬에 쉬는 로컬 값을 차례로 곱해 계산한다 (뼈에는 지난 프레임의 흔들린 자세가 그대로 있다).
        /// 뼈에 지금 든 로컬 값도 읽어 둔다: Is Animated면 지난번에 적은 값과 다를 때 밖에서(자세·애니메이션) 바꾼 것이니 그 값이 새 쉬는 자세다.
        /// </summary>
        static void Targets(Chain ch)
        {
            var pm = ch.Parent != null ? ch.Parent.localToWorldMatrix : Matrix4x4.identity;
            ch.ParentRot = ch.Parent != null ? ch.Parent.rotation : Quaternion.identity;
            ch.RestChanged = ch.Touched = false;
            foreach (var p in ch.P)   // 부모가 먼저 온다
            {
                if (p.T == null) { var o = ch.P[p.Parent]; p.Target = o.RestM.MultiplyPoint3x4(p.EndLocal); p.RestRot = o.RestRot; continue; }
                p.NowRot = p.T.localRotation; p.NowPos = p.T.localPosition; p.NowScale = p.T.localScale;
                if (!Same(p.NowRot, p.ShownRot) || p.NowPos != p.ShownPos || p.NowScale != p.ShownScale) ch.Touched = true;
                if (ch.IsAnimated)
                {
                    if (p.NowRot != p.ShownRot) { p.RestLocalRot = p.NowRot; ch.RestChanged = true; }
                    if (p.NowPos != p.ShownPos) { p.RestLocalPos = p.NowPos; ch.RestChanged = true; }
                    if (p.NowScale != p.ShownScale) { p.RestLocalScale = p.NowScale; ch.RestChanged = true; }
                }
                var par = p.Parent >= 0 ? ch.P[p.Parent] : null;
                p.RestM = (par != null ? par.RestM : pm) * Matrix4x4.TRS(p.RestLocalPos, p.RestLocalRot, p.RestLocalScale);
                p.Target = p.RestM.GetColumn(3);
                p.RestRot = (par != null ? par.RestRot : ch.ParentRot) * p.RestLocalRot;
            }
            var m = ch.P[0].RestM;
            ch.Scale = Mathf.Max(1e-6f, new Vector3(m.m00, m.m10, m.m20).magnitude);
            ch.Stir = !Same(m, ch.StirM);   // 몸(루트)이 조금이라도 움직였는지 (비트까지 견준다)
            ch.StirM = m;
        }

        /// <summary>
        /// 순간이동: 체인 루트가 한 프레임에 JumpDist·JumpAngle 넘게 움직이거나 크기가 바뀌면 체인을 루트와 함께 통째로 옮긴다.
        /// VR 스냅 회전(머리 본이 한 프레임에 30° 돈다)·방 크기 배율·아바타 옮기기에서 체인이 옛 자리에 남아 크게 휘둘리지 않게.
        /// </summary>
        void Carry(Chain ch)
        {
            var pos = ch.P[0].Target; var rot = ch.P[0].RestRot; float sc = ch.Scale;
            float ratio = sc / Mathf.Max(1e-6f, ch.JumpScale);
            ch.Carried = Vector3.Distance(pos, ch.JumpPos) > JumpDist * _unit || Quaternion.Angle(rot, ch.JumpRot) > JumpAngle || Mathf.Abs(ratio - 1) > 0.01f;
            if (ch.Carried)
            {
                var dr = rot * Quaternion.Inverse(ch.JumpRot); var from = ch.JumpPos;
                Vector3 M(Vector3 x) => pos + dr * (x - from) * ratio;
                foreach (var p in ch.P)
                {
                    p.Pos = M(p.Pos); p.Prev = M(p.Prev); p.LastTarget = M(p.LastTarget); p.Show = M(p.Show);
                    p.F1 = M(p.F1); p.F2 = M(p.F2); p.Dev = dr * p.Dev * ratio; p.BodyV = dr * p.BodyV * ratio; p.BodyF = dr * p.BodyF * ratio; p.Still = Vector3.zero;
                }
                ch.HasLast = false;   // Immobile이 같은 움직임을 또 옮기지 않게 기준을 지금으로
            }
            ch.JumpPos = pos; ch.JumpRot = rot; ch.JumpScale = sc;
        }

        /// <summary>
        /// 화면에 보일 위치: 쉬는 자리에서 벗어난 정도(Dev)를 크기에 따라 따라간다. 몇 mm 안쪽의 잔떨림(여러 콜라이더에 번갈아 밀리며 생기는 것)은
        /// 거의 따라가지 않고, 크게 움직이면 그대로 따라간다. 아바타 전체가 움직이는 것은 Target에 들어 있어 늦어지지 않는다. 시뮬레이션 값(Pos)은 그대로다.
        /// </summary>
        /// <returns>벗어난 정도(Dev)가 이번에 가장 많이 바뀐 관절의 바뀐 양 (아바타 크기 1 기준 m)</returns>
        float Smooth(Chain ch, bool stepped)
        {
            float b = Mathf.Max(1e-5f, SmoothBand) * _unit, b2 = b * b, moved = 0;
            foreach (var p in ch.P)
            {
                if (!stepped) { p.Show = p.Target + p.Dev; continue; } // 스텝을 안 돈 프레임: 벗어난 정도를 그대로 두고 몸을 따라간다
                var dev = p.Pos - p.Target;
                var old = p.Dev;
                if (!Smoothing || p.Fixed || p.Parent < 0) { p.Dev = dev; p.Show = p.Pos; }
                else
                {
                    var d = dev - p.Dev; float m2 = d.sqrMagnitude;
                    p.Dev += d * Mathf.Max(0.03f, m2 / (m2 + b2)); // 아주 작은 차이도 1~2초면 따라붙게
                    p.Show = p.Target + p.Dev;
                }
                if (!p.Fixed) moved = Mathf.Max(moved, (p.Dev - old).sqrMagnitude);
            }
            return Mathf.Sqrt(moved) / _unit;
        }

        static void MeasureJitter(Chain ch)
        {
            foreach (var p in ch.P)
            {
                if (p.Fixed || p.Parent < 0) continue;
                var x = p.At;
                if (p.Frames >= 2) p.Jitter = Mathf.Lerp(p.Jitter, (x - 2 * p.F1 + p.F2).magnitude / ch.Scale, 0.05f);
                p.F2 = p.F1; p.F1 = x; p.Frames++;
            }
        }

        /// <summary>떨림: 관절마다 프레임 사이 가속(위치 2차 차분, 아바타 크기 기준 mm) 평균과 가장 큰 관절.</summary>
        public (float mean, float max, string worst) Jitter()
        {
            float sum = 0, max = 0; int n = 0; string worst = "";
            foreach (var ch in _chains)
                foreach (var p in ch.P)
                {
                    if (p.Fixed || p.Parent < 0 || p.Frames < 3) continue;
                    float j = p.Jitter * 1000; sum += j; n++;
                    if (j > max) { max = j; worst = p.T != null ? p.T.name : ch.P[p.Parent].T?.name + "(끝)"; }
                }
            return (n > 0 ? sum / n : 0, max, worst);
        }

        /// 콜라이더에 밀려난 관절: 밀린 쪽 속도는 없애고 미끄러지는 속도는 마찰만큼 줄인다.
        /// (Verlet은 밀어낸 거리가 다음 스텝 속도가 되므로, 그대로 두면 튕겨 나갔다가 Pull에 다시 끌려 들어오며 떨린다)
        /// 속도는 몸에 대한 것만 줄인다 (bodyStep = 몸이 이 스텝에 움직인 만큼). 월드 속도를 줄이면 걷는 동안 다리에 닿은 치마가 몸보다 뒤처진다
        void Settle(Particle p, Vector3 pos, Vector3 push, float r, Vector3 bodyStep)
        {
            if (!Inelastic || push.sqrMagnitude < 1e-14f) return;
            var v = Vector3.ProjectOnPlane(pos - p.Prev - bodyStep, push.normalized) * Mathf.Pow(1 - ContactFriction, r);
            p.Prev = pos - bodyStep - v;
        }

        void Restore() { foreach (var ch in _chains) RestoreChain(ch); }

        /// 뼈를 쉬는 자세로 되돌린다 (끄기·다시 놓기·꺼진 체인). 매 프레임에는 부르지 않는다
        static void RestoreChain(Chain ch)
        {
            foreach (var p in ch.P)
            {
                if (p.T == null) continue;
                // Is Animated: 지난번에 적은 값과 다르면 밖에서(자세·애니메이션) 바꾼 것이니 그 값이 새 쉬는 자세다
                if (ch.IsAnimated)
                {
                    if (p.T.localRotation != p.ShownRot) p.RestLocalRot = p.T.localRotation;
                    if (p.T.localPosition != p.ShownPos) p.RestLocalPos = p.T.localPosition;
                    if (p.T.localScale != p.ShownScale) p.RestLocalScale = p.T.localScale;
                }
                p.T.localRotation = p.RestLocalRot; p.T.localPosition = p.RestLocalPos; p.T.localScale = p.RestLocalScale;
                p.ShownRot = p.SetRot = p.T.localRotation; p.ShownPos = p.RestLocalPos; p.ShownScale = p.RestLocalScale; p.SetAt = Unset;
            }
        }

        /// Immobile: 루트의 위치·회전(All Motion) 또는 아바타 루트의 위치(World, VRChat 문서: 월드 공간 위치 이동만)를 그 몫만큼 "없던 움직임"으로 친다.
        /// 관절은 어차피 몸과 함께 가므로(bodyStep), 여기서는 관성에서 뺄 몫(Still)만 모은다 — 스텝을 돌 때 스텝 수로 나눠 쓴다.
        /// (2026-11-12: 예전에는 프레임 처음에 입자를 그 몫만큼 한꺼번에 옮겨, 스텝이 여럿인 프레임(60fps 아래)에서 루트·콜라이더보다 앞서 가 치마가 콜라이더에 밀려 솟았다)
        void Immobile(Chain ch)
        {
            if (!ch.AnyImmobile) return;
            bool world = ch.ImmobileType == 1 && _sceneRoot != null;
            var pos = world ? _sceneRoot.position : ch.P[0].Target; var rot = world ? Quaternion.identity : ch.P[0].RestRot;
            if (!ch.HasLast) { ch.LastPos = pos; ch.LastRot = rot; ch.HasLast = true; return; }
            var dr = world ? Quaternion.identity : rot * Quaternion.Inverse(ch.LastRot);
            foreach (var p in ch.P)
            {
                if (p.Immobile <= 0 || p.Fixed) continue;
                var from = p.LastTarget + p.Still;   // 스텝을 안 돈 프레임이 이어지면 이어서 모은다
                p.Still += (pos + dr * (from - ch.LastPos) - from) * p.Immobile;
            }
            ch.LastPos = pos; ch.LastRot = rot;
        }

        /// <summary>
        /// 60 Hz 스텝 기준 값 → 이 스텝 길이의 값 (slow = 스텝 길이 ÷ 1/60초 ÷ √무게).
        /// 속도를 이어 가는 몫은 Spring^slow. Pull은 "남은 거리의 몫"(1 − (1 − Pull)^slow)에 (1 − Keep) ÷ (1 − Spring)을 곱한다:
        /// 관성이 없으면(Spring 0) 그대로이고, 관성이 크면 힘처럼 스텝 길이의 제곱에 비례한다 — 어느 쪽이든 초당 돌아오는 빠르기·출렁임이 스텝 길이와 무관하다.
        /// 느낌 값은 지수로: Pull → 1 − (1 − Pull)^FirmPull, Spring → Spring^(1/FirmDamp).
        /// </summary>
        /// <param name="rs">스텝 길이 ÷ 1/60초 (무게를 넣지 않은 값)</param>
        void Rates(Chain ch, Particle p, SoftPreset soft, float slow, float rs)
        {
            float pull = soft != null ? Mathf.Min(p.Pull, soft.PullMax) : p.Pull, spring = soft != null ? Mathf.Max(p.Spring, soft.SpringMin) : p.Spring;
            float s0 = Mathf.Min(0.995f, Mathf.Pow(spring, 1 / Mathf.Max(0.05f, FirmDamp)));
            p.Keep = Mathf.Pow(s0, slow);
            float k = (1 - p.Keep) / (1 - s0);
            p.Draw = Mathf.Min(1, (1 - Mathf.Pow(1 - pull, FirmPull * slow)) * k);
            // Version 1.0의 Gravity: Pull과 겨루는 힘. VRChat 실제 세기는 비공개라, Pull이 V10GravityPull일 때 Version 1.1 Gravity와 비슷하게 기울도록 맞췄다
            p.Fall = Mathf.Min(1, (1 - Mathf.Pow(1 - V10GravityPull, FirmPull * slow)) * k);
            // Stiffness: 1.1은 직전 방향을 지킨다(속도를 줄이는 쪽이라 Spring처럼 환산) · 1.0은 쉬는 자세를 지킨다(Pull처럼 환산)
            float st = 1 - Mathf.Pow(1 - p.Stiffness * 0.5f, slow);
            p.Stiff = ch.V11 ? st : Mathf.Min(1, st * k);
            // 관성(몸 속도가 바뀐 만큼 밀리는 것): 밀려서 흘러가는 거리 = 속도 변화 × 스텝 길이 ÷ (1 − Keep). 60 Hz 스텝일 때와 같게 맞춘다
            // (맞추지 않으면 스텝이 짧을수록 덜 밀린다: 감쇠가 큰 본은 144 Hz에서 60 Hz의 0.7배)
            float keep60 = Mathf.Pow(s0, slow / rs);
            p.Kick = (1 - p.Keep) / ((1 - keep60) * rs);
            p.RateFor = slow; p.RateVer = _feelVer;
        }

        /// <summary>이번 프레임에 이 체인이 닿을 수 있는 콜라이더만 고른다 (체인을 둘러싼 구와 견줌). 평면·Inside Bounds는 언제나 넣는다.</summary>
        void Nearby(Chain ch)
        {
            ch.Near.Clear();
            bool leg = LegGuard && ch.LegColliders.Count > 0;
            if (ch.Colliders.Count == 0 && !leg) return;
            var c = ch.P[0].Target; float far = 0, rad = 0, move = 0;
            foreach (var p in ch.P)
            {
                far = Mathf.Max(far, Mathf.Max((p.Pos - c).sqrMagnitude, (p.Target - c).sqrMagnitude));
                rad = Mathf.Max(rad, p.Radius); move = Mathf.Max(move, (p.Target - p.LastTarget).sqrMagnitude);
            }
            // 여유: 이번 프레임에 몸이 움직인 만큼 + 몸에 대해 흔들릴 수 있는 만큼 (속도 상한 × 가장 긴 프레임)
            float bound = Mathf.Sqrt(far) + Mathf.Sqrt(move) + rad * ch.Scale + (0.05f + SlowSwing * Step * MaxSteps) * _unit;
            void Add(int ci)
            {
                var col = _cols[ci];
                if (!col.Active) return;
                if (col.Shape == 2 || col.Inside) { ch.Near.Add(ci); return; }
                var q = col.Shape == 1 ? Closest(col.A1, col.B1, c) : col.C1;
                float reach = bound + col.R + (col.C1 - col.C0).magnitude;
                if ((c - q).sqrMagnitude <= reach * reach) ch.Near.Add(ci);
            }
            foreach (var ci in ch.Colliders) Add(ci);
            if (leg) foreach (var ci in ch.LegColliders) Add(ci);
        }

        /// <param name="s">이 프레임의 몇 번째 스텝 (0부터)</param>
        /// <param name="h">스텝 길이 (초)</param>
        /// <param name="hPrev">지난 스텝 길이: Verlet 속도(Pos − Prev)를 이번 스텝 길이로 환산한다</param>
        void Simulate(Chain ch, int s, int steps, float h, float hPrev)
        {
            float scale = ch.Scale;
            var soft = SoftOf(ch);
            float rs = h / Step, vScale = h / Mathf.Max(1e-6f, hPrev);
            float w = Mathf.Max(1, Weight), slow = rs / Mathf.Sqrt(w);   // 무게감: 몸에 대한 움직임만 √W배 느린 시간으로
            float frac = (s + 1) / (float)steps;                         // 움직이지 않는 관절·콜라이더: 지난 프레임 자리 → 이번 자리
            if (steps > 1 && !ch.Carried) foreach (var ci in ch.Near) _cols[ci].Blend(frac);
            float cap = SlowSwing / Mathf.Sqrt(w) * h * _unit, slop = Slop * _unit;
            float follow = FollowTime < 1e-4f ? 1 : 1 - Mathf.Exp(-h / FollowTime);
            float lenBack = 1 - Mathf.Pow(1 - LenReturn, rs);
            var root = ch.P[0];
            root.Pos = root.Prev = Vector3.Lerp(root.LastTarget, root.Target, frac); root.Rot = Quaternion.identity;
            for (int i = 1; i < ch.P.Count; i++)
            {
                var p = ch.P[i]; var par = ch.P[p.Parent];
                if (p.Fixed) { p.Pos = p.Prev = Vector3.Lerp(p.LastTarget, p.Target, frac); p.Rot = Quaternion.identity; continue; }
                var restVec = par.Rot * (p.Target - par.Target);
                float len = restVec.magnitude;
                if (len < 1e-6f) { p.Pos = p.Prev = par.Pos; p.Rot = par.Rot; continue; }
                var restDir = restVec / len;

                // 중력: 쉬는 방향을 아래로 기울인다. Falloff: 불러온 자세만큼 아래를 향해 있으면(쉬는 자세) 그만큼 중력을 뺀다.
                // 아래로 늘어진 머리카락은 서 있으면 그대로, 고개를 숙이거나 누우면 중력을 받는다. 위로 선 귀는 서 있으면 그대로 선다
                var aim = restDir; var gd = Vector3.zero; float gForce = 0;
                if (Mathf.Abs(p.Gravity) > 1e-4f)
                {
                    gd = p.Gravity > 0 ? Vector3.down : Vector3.up;
                    float bind = p.Gravity > 0 ? p.BindDown : -p.BindDown;
                    float g = Mathf.Abs(p.Gravity) * GravityScale * (1 - p.GravityFalloff * FalloffScale * Mathf.Max(0, 1 - Mathf.Abs(Vector3.Dot(restDir, gd) - bind)));
                    if (ch.V11) aim = Vector3.Slerp(restDir, gd, Mathf.Clamp01(g));
                    else gForce = Mathf.Clamp01(g);   // Version 1.0: 쉬는 방향은 그대로 두고 아래에서 힘으로 더한다
                }
                var goal = par.Pos + aim * (soft != null ? len * SagLength(soft, aim) : len);

                if (p.RateFor != slow || p.RateVer != _feelVer) Rates(ch, p, soft, slow, rs);
                // 몸(쉬는 자리)이 이 스텝에 움직인 만큼. 관절은 이만큼 몸과 함께 간다
                var bodyStep = (p.Target - p.LastTarget) / steps;
                var bodyV = bodyStep / h;
                // 관성으로 느끼는 몸 속도: Immobile 몫(Still)은 뺀다. 그 변화(가속)는 FollowTime으로 걸러서 느낀다 — 고개 끄덕임·잔걸음 같은 작고 빠른 움직임은 거의 지나간다
                var felt = bodyV - p.Still / (steps * h);
                var f = p.BodyF + (felt - p.BodyF) * follow;
                // 몸에 대한 속도: 지난 스텝 속도에서 이어 가는 몫(Keep) − 느낀 몸 속도가 바뀐 만큼(관성, Spring과 무관하게 받는다)
                var rel = ((p.Pos - p.Prev) * vScale - p.BodyV * h) * p.Keep - (f - p.BodyF) * (h * p.Kick);
                p.BodyV = bodyV; p.BodyF = f;
                p.Prev = p.Pos;
                if (rel.sqrMagnitude > cap * cap) rel *= cap / rel.magnitude;
                var carried = p.Pos + bodyStep;   // 몸과 함께 옮겨 간 자리
                var pos = carried + rel + (goal - carried) * p.Draw;
                if (gForce > 0) pos += gd * (len * gForce * p.Fall);
                // Stiffness — Version 1.1: 직전 방향을 지킨다 (몸이 움직인 만큼은 같이 옮긴 자리에서) · 1.0: 쉬는 자세를 지킨다
                if (ch.Advanced && p.Stiffness > 0) pos = Vector3.Lerp(pos, ch.V11 ? carried : par.Pos + restVec, p.Stiff);

                pos = MotionLength(par.Pos, pos, len, p, restVec, soft, lenBack);
                pos = Limit(ch, par, p, pos, restDir);
                var free = pos;
                if (ch.Near.Count > 0)
                {
                    // 뼈 캡슐: 부모 관절 반지름 → 자식 관절 반지름으로 가늘어지거나 굵어진다
                    float r0 = par.Radius * scale, r = p.Radius * scale;
                    for (int it = 0; it < 4; it++)  // 밀어내기와 뼈 길이 맞추기가 서로 당기므로 몇 번 되풀이한다. 밀린 것이 없으면 거기서 끝
                    {
                        bool hit = false;
                        foreach (var ci in ch.Near) hit |= Collide(_cols[ci], par.Pos, ref pos, r0, r, Allow(par, ci), slop);
                        if (!hit) break;
                        pos = Length(par.Pos, pos, len, p, restVec, soft);
                    }
                }
                if (soft != null) { Contact(ch, p, ref pos, SoftRadius(p, scale)); pos = Length(par.Pos, pos, len, p, restVec, soft); }
                Settle(p, pos, pos - free, rs, bodyStep);
                float went = (pos - carried).sqrMagnitude; if (went > ch.MaxMove) ch.MaxMove = went;
                p.Pos = pos;
                p.LenRatio = (pos - par.Pos).magnitude / len;
                p.Rot = Quaternion.FromToRotation(restVec, pos - par.Pos) * par.Rot;
            }
        }

        /// 뼈 길이를 허용 범위로 (말랑 모드면 프리셋 범위와 원래 범위 중 넓은 쪽)
        static Vector3 Length(Vector3 from, Vector3 pos, float len, Particle p, Vector3 restVec, SoftPreset soft)
        {
            float lo = 1 - (soft != null ? Mathf.Max(p.MaxSquish, soft.Squish) : p.MaxSquish);
            float hi = 1 + (soft != null ? Mathf.Max(p.MaxStretch, soft.Stretch) : p.MaxStretch);
            return Clamped(from, pos, len, lo, hi, restVec);
        }

        static Vector3 Clamped(Vector3 from, Vector3 pos, float len, float lo, float hi, Vector3 restVec)
        {
            var d = pos - from; float l = d.magnitude;
            if (l < 1e-6f) return from + restVec;
            return from + d / l * Mathf.Clamp(l, len * lo, len * hi);
        }

        /// 움직임으로 허용되는 뼈 길이: 범위의 Stretch Motion 몫만큼만. 잡거나 부딪혀 그보다 늘거나 줄어 있던 뼈는 한 번에 튀지 않고 LenReturn 빠르기로 돌아온다.
        /// 말랑 모드는 범위 전부
        static Vector3 MotionLength(Vector3 from, Vector3 pos, float len, Particle p, Vector3 restVec, SoftPreset soft, float lenBack)
        {
            if (soft != null || p.StretchMotion >= 1) return Length(from, pos, len, p, restVec, soft);
            float lo = 1 - p.MaxSquish * p.StretchMotion, hi = 1 + p.MaxStretch * p.StretchMotion;
            float eased = Mathf.Lerp(p.LenRatio, Mathf.Clamp(p.LenRatio, lo, hi), lenBack);
            return Clamped(from, pos, len, Mathf.Min(lo, eased), Mathf.Max(hi, eased), restVec);
        }

        static Vector3 Limit(Chain ch, Particle par, Particle p, Vector3 pos, Vector3 restDir)
        {
            if (ch.LimitType == 0) return pos;
            var d = pos - par.Pos; float l = d.magnitude;
            if (l < 1e-6f) return pos;
            // 제한 틀: 부모 뼈의 (시뮬레이션) 회전 × limitRotation. 축 = 쉬는 방향을 그 회전만큼 돌린 것
            // restDir에는 이미 부모 회전(par.Rot)이 들어 있어 빼고 돌린다 (2026-11-03: 두 번 돌려 부모가 기운 만큼 자식 축이 더 기울어,
            // 쉬는 방향이 제한 끝에 걸친 치마(Limit Rotation −30°, Hinge 30°)가 아래로 갈수록 두 배씩 꺾여 뒤집혔다)
            var frame = par.Rot * par.RestRot * p.LimitQ;
            var axis = frame * (Quaternion.Inverse(par.Rot * par.RestRot) * restDir);
            var dir = d / l;
            switch (ch.LimitType)
            {
                case 1: // Angle: 원뿔
                    {
                        float max = Mathf.Max(0, p.MaxAngleX);
                        if (Vector3.Angle(axis, dir) > max) dir = Vector3.RotateTowards(axis, dir, max * Mathf.Deg2Rad, 0);
                        break;
                    }
                case 2: // Hinge: 틀의 X축을 축으로 도는 평면 안에서 ±MaxAngleX
                    {
                        var n = frame * Vector3.right;
                        var proj = Vector3.ProjectOnPlane(dir, n);
                        if (proj.sqrMagnitude < 1e-8f) proj = axis;
                        dir = proj.normalized;
                        float max = Mathf.Max(0, p.MaxAngleX);
                        if (Vector3.Angle(axis, dir) > max) dir = Vector3.RotateTowards(axis, dir, max * Mathf.Deg2Rad, 0);
                        break;
                    }
                case 3: // Polar: 틀의 X축 둘레 회전 ±MaxAngleX, Z축 둘레 회전 ±MaxAngleZ 인 타원 원뿔
                    {
                        float theta = Vector3.Angle(axis, dir);
                        if (theta < 1e-3f) break;
                        var dev = Vector3.ProjectOnPlane(dir, axis).normalized;
                        // X축 둘레로 돌면 Z 방향(v)으로, Z축 둘레로 돌면 X 방향(u)으로 기운다
                        var u = Vector3.ProjectOnPlane(frame * Vector3.right, axis);
                        if (u.sqrMagnitude < 1e-8f) u = Vector3.ProjectOnPlane(frame * Vector3.forward, axis);   // 틀의 X축이 뼈 방향과 나란한 뼈
                        u.Normalize();
                        var v = Vector3.Cross(axis, u);
                        float a = Mathf.Max(0.01f, p.MaxAngleZ), b = Mathf.Max(0.01f, p.MaxAngleX);
                        // 축에서 기운 각을 (u 쪽, v 쪽) 두 값으로 펴서 타원 a × b 안인지 본다. 밖이면 타원에서 가장 가까운 점으로.
                        // (2026-11-12: 예전에는 같은 방위로 축 쪽으로 당겼다. Limit Rotation으로 쉬는 방향이 타원 긴 쪽 끝에 놓인 치마·리본·옷깃은
                        //  옆으로 5°만 벗어나도 그 방위의 타원 반지름(가늘어서 몇 도~30°)까지 끌려가 쉬는 자세보다 20~30° 위로 들렸다)
                        float x = theta * Vector3.Dot(dev, u), y = theta * Vector3.Dot(dev, v);
                        if (x * x / (a * a) + y * y / (b * b) <= 1) break;
                        EllipseNearest(a, b, ref x, ref y);
                        float t2 = Mathf.Sqrt(x * x + y * y);
                        if (t2 < 1e-4f) { dir = axis; break; }
                        var to = (u * x + v * y) / t2;
                        dir = axis * Mathf.Cos(t2 * Mathf.Deg2Rad) + to * Mathf.Sin(t2 * Mathf.Deg2Rad);
                        break;
                    }
            }
            return par.Pos + dir * l;
        }

        /// 타원 (x/a)² + (y/b)² = 1 에서 점 (x, y)에 가장 가까운 점 (몇 번 되풀이해 좁힌다)
        static void EllipseNearest(float a, float b, ref float px, ref float py)
        {
            float ax = Mathf.Abs(px), ay = Mathf.Abs(py);
            float tx = 0.70710678f, ty = 0.70710678f;
            for (int i = 0; i < 5; i++)
            {
                float x = a * tx, y = b * ty;
                float ex = (a * a - b * b) * tx * tx * tx / a, ey = (b * b - a * a) * ty * ty * ty / b;
                float rx = x - ex, ry = y - ey, qx = ax - ex, qy = ay - ey;
                float r = Mathf.Sqrt(rx * rx + ry * ry), q = Mathf.Sqrt(qx * qx + qy * qy);
                if (q < 1e-9f) break;
                tx = Mathf.Clamp01((qx * r / q + ex) / a); ty = Mathf.Clamp01((qy * r / q + ey) / b);
                float t = Mathf.Sqrt(tx * tx + ty * ty);
                if (t < 1e-9f) { tx = 1; ty = 0; break; }
                tx /= t; ty /= t;
            }
            px = (px < 0 ? -1 : 1) * a * tx; py = (py < 0 ? -1 : 1) * b * ty;
        }

        /// 콜라이더의 이번 프레임 자리 (지금 쓰는 값 C·A·B·N에 넣는다). 표시·통계에서도 부른다
        static void Prepare(Col c)
        {
            c.Active = c.T != null && c.Enabled && c.T.gameObject.activeInHierarchy; // 프레임마다 한 번 (스텝·입자마다 묻지 않게)
            if (!c.Active) return;
            float s = Mathf.Abs(c.T.lossyScale.x);
            var rot = c.T.rotation * c.Rot;
            c.S = s; c.C = c.T.TransformPoint(c.Pos); c.R = c.Radius * s; c.N = rot * Vector3.up;
            float half = Mathf.Max(0, c.Height * s * 0.5f - c.R); // 캡슐 높이는 양 끝 반구 포함
            c.A = c.C - c.N * half; c.B = c.C + c.N * half;
        }

        /// Tick: 이번 프레임 자리를 구하고 지난 프레임 자리와 잇는다. 한 프레임에 크게 옮겨졌으면(순간이동·새로 켜짐) 잇지 않는다
        static void Sweep(Col c, float jump)
        {
            Prepare(c);
            if (!c.Active) { c.HasPrev = false; c.Changed = c.WasActive; return; }
            c.C1 = c.C; c.A1 = c.A; c.B1 = c.B; c.N1 = c.N;
            c.Changed = !c.HasPrev || !c.WasActive || c.R != c.R0 || !Same(c.C1, c.C0) || !Same(c.A1, c.A0) || !Same(c.B1, c.B0) || !Same(c.N1, c.N0);
            if (!c.HasPrev || (c.C1 - c.C0).sqrMagnitude > jump * jump || Vector3.Angle(c.N0, c.N1) > JumpAngle) c.Keep();
            c.HasPrev = true;
        }

        /// 부모 관절의 겹침을 얼마까지 봐줄지: 움직이지 않는 관절은 전부(-1), 그 밖에는 불러온 자세에서 이미 겹쳐 있던 만큼만
        static float Allow(Particle par, int ci) => par.Fixed || par.Parent < 0 ? -1 : par.BindPen.TryGetValue(ci, out var v) ? v : 0;

        /// 불러온 자세에서 관절마다 콜라이더와 겹친 깊이 (Build 끝에서 한 번)
        void MeasureBindPen()
        {
            foreach (var c in _cols) Prepare(c);
            foreach (var ch in _chains)
            {
                float scale = ch.Scale;
                foreach (var p in ch.P)
                {
                    p.BindPen.Clear();
                    void One(int ci)
                    {
                        var c = _cols[ci];
                        if (!c.Active || c.Shape == 2 || c.Inside) return;
                        var q = c.Shape == 1 ? Closest(c.A, c.B, p.Target) : c.C;
                        float pen = c.R + p.Radius * scale - (p.Target - q).magnitude;
                        if (pen > 0) p.BindPen[ci] = pen;
                    }
                    foreach (var ci in ch.Colliders) One(ci);
                    foreach (var ci in ch.LegColliders) One(ci);
                }
            }
        }

        /// <summary>
        /// 뼈(부모 관절 a → 자식 관절 pos, 반지름 r)를 콜라이더 밖(Inside Bounds면 안)으로 민다. 부모는 이 스텝에서 이미 정해졌으므로 자식만 옮긴다.
        /// 뼈 위 t 지점이 겹치면 자식을 겹친 만큼 / t 옮긴다 (부모에 가까운 지점일수록 크게: 그래야 그 지점이 빠진다).
        /// </summary>
        /// <param name="parentAllow">부모 관절 겹침을 봐줄 최대 깊이 (-1 = 지금 겹친 만큼 전부: 움직이지 않는 관절)</param>
        /// <param name="slop">이만큼 파고든 것은 그냥 둔다</param>
        /// <returns>밀었으면 true</returns>
        static bool Collide(Col c, Vector3 a, ref Vector3 pos, float r0, float r, float parentAllow = -1, float slop = 0)
        {
            if (!c.Active) return false;
            if (c.Shape == 2) // Plane: 위쪽(Y) 반공간 밖으로 (뼈 끝만: 부모는 이미 밖에 있다)
            {
                float d = Vector3.Dot(pos - c.C, c.N) - r;
                if (d >= -slop) return false;
                pos -= c.N * (d + slop);
                return true;
            }
            if (c.Inside)
            {
                var q0 = c.Shape == 1 ? Closest(c.A, c.B, pos) : c.C;
                var v0 = pos - q0; float l0 = v0.magnitude;
                float max = Mathf.Max(0, c.R - r);
                if (l0 <= max + slop) return false;
                pos = q0 + v0 / l0 * (max + slop);
                return true;
            }
            Vector3 ca = c.Shape == 1 ? c.A : c.C, cb = c.Shape == 1 ? c.B : c.C;
            // 부모 관절이 이미 겹친 만큼은 이 뼈가 풀 수 없다 (부모는 이 스텝에서 정해졌다).
            // 그 겹침을 뼈 시작에서는 그대로, 끝으로 갈수록 0으로 봐주고 그보다 더 들어간 만큼만 민다.
            // (봐주지 않으면 시작점의 겹침을 풀려고 자식 관절을 끝없이 밀어 Limit 끝까지 벌어진다: 치마·코트 자락이 쉬는 자세에서 벌어지던 문제)
            float parentPen = Mathf.Max(0, c.R + r0 - (a - Closest(ca, cb, a)).magnitude);
            if (parentAllow >= 0) parentPen = Mathf.Min(parentPen, parentAllow);
            float bestExcess = 0, bestT = 1; Vector3 bestN = Vector3.zero;
            var end = pos;   // ref 매개변수는 지역 함수 안에서 못 쓴다
            void Check(float t, Vector3 onCol)
            {
                var onBone = a + (end - a) * t;
                var v = onBone - onCol; float l = v.magnitude;
                float excess = c.R + Mathf.Lerp(r0, r, t) - l - parentPen * (1 - t) - slop;
                if (excess <= bestExcess) return;
                bestExcess = excess; bestT = t;
                bestN = l > 1e-6f ? v / l : (c.Shape == 1 ? Vector3.Cross(cb - ca, Vector3.up).normalized : Vector3.up);
            }
            Check(1, Closest(ca, cb, end));                       // 끝 관절
            if (!c.AsSpheres) { SegmentClosest(ca, cb, a, end, out var onCol, out var tc); Check(tc, onCol); }   // 뼈 캡슐에서 가장 가까운 곳
            if (bestExcess <= 0) return false;
            if (bestN.sqrMagnitude < 1e-8f) bestN = Vector3.forward;
            pos += bestN * (bestExcess / Mathf.Max(bestT, 0.25f));
            return true;
        }

        /// 두 선분 p1-q1, p2-q2 사이 가장 가까운 점: 첫 선분 위 점(c1)과 둘째 선분의 매개변수 t (0~1)
        static void SegmentClosest(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out float t)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s;
            if (a <= 1e-10f && e <= 1e-10f) { c1 = p1; t = 0; return; }
            if (a <= 1e-10f) { s = 0; t = Mathf.Clamp01(f / e); }
            else
            {
                float cc = Vector3.Dot(d1, r);
                if (e <= 1e-10f) { t = 0; s = Mathf.Clamp01(-cc / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                    s = den > 1e-10f ? Mathf.Clamp01((b * f - cc * e) / den) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Mathf.Clamp01(-cc / a); }
                    else if (t > 1) { t = 1; s = Mathf.Clamp01((b - cc) / a); }
                }
            }
            c1 = p1 + d1 * s;
        }

        static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a; float d = ab.sqrMagnitude;
            if (d < 1e-10f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / d);
        }

        static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;   // 비트까지 같은지 (Unity의 ==는 0.1°쯤을 같다고 본다)
        static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        static bool Same(Matrix4x4 a, Matrix4x4 b) =>
            a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 && a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13 &&
            a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23;

        /// 이 체인을 잡거나 고정하고 있는지 (그동안은 잠들지 않는다)
        bool Held(Chain ch)
        {
            if (_grab != null && _grab.Ch == ch || _vrGrab[0] != null && _vrGrab[0].Ch == ch || _vrGrab[1] != null && _vrGrab[1].Ch == ch) return true;
            foreach (var h in _pins) if (h.Ch == ch) return true;
            return false;
        }

        /// 이 체인이 부딪히는 콜라이더 가운데 지난 프레임과 달라진 것이 있는지
        bool ColliderChanged(Chain ch)
        {
            foreach (var ci in ch.Colliders) if (_cols[ci].Changed) return true;
            if (LegGuard) foreach (var ci in ch.LegColliders) if (_cols[ci].Changed) return true;
            return false;
        }

        /// <summary>
        /// 보이는 위치로 뼈를 돌린다. 부모부터: 뼈의 월드 회전 = (쉬는 방향 → 보이는 방향 회전) × 부모가 돈 만큼 × 쉬는 회전.
        /// 뼈에서 읽지 않고 계산한 값으로 로컬 회전을 구해, 지난번에 적은 값과 다를 때만 적는다 — 가만히 있는 체인은 뼈를 건드리지 않는다.
        /// 뼈에 든 값이 지난번에 적은 뒤와 다르면(밖에서 바꿈) 다시 적는다. 늘고 주는 뼈(Placed)·말랑 모드는 자리·크기도 적는다.
        /// </summary>
        static void Apply(Chain ch, SoftPreset soft)
        {
            for (int i = 0; i < ch.P.Count; i++)
            {
                var p = ch.P[i];
                var par = p.Parent >= 0 ? ch.P[p.Parent] : null;
                var dPar = par != null ? par.Delta : Quaternion.identity;
                bool placed = par != null && p.T != null && (soft != null || p.Placed);
                // 뼈가 놓이는 자리: 루트는 쉬는 자리, 늘고 주는 뼈는 보이는 자리, 그 밖에는 부모가 돈 만큼 따라간 자리
                p.At = par == null ? p.Target : placed || p.T == null ? p.Show : par.At + dPar * (p.Target - par.Target);
                p.Delta = dPar; p.Moved = par != null ? par.Moved : ch.Stir;
                if (p.T == null) continue;
                bool turn = p.Children.Count > 0 && !(p.Children.Count > 1 && i == 0 && ch.MultiChild == 0); // Ignore: 자식이 여럿인 루트는 돌리지 않음
                if (turn)
                {
                    Vector3 rest = Vector3.zero, sim = Vector3.zero;
                    int n = p.Children.Count > 1 && ch.MultiChild == 1 ? 1 : p.Children.Count; // First: 첫 자식만
                    for (int k = 0; k < n; k++)
                    {
                        var c = ch.P[p.Children[k]];
                        rest += dPar * (c.Target - p.Target);
                        sim += c.Show - p.At;
                    }
                    if (rest.sqrMagnitude >= 1e-12f && sim.sqrMagnitude >= 1e-12f) p.Delta = Quaternion.FromToRotation(rest, sim) * dPar;
                }
                p.World = p.Delta * p.RestRot;
                var local = Quaternion.Inverse(par != null ? par.World : ch.ParentRot) * p.World;
                if (!Same(local, p.SetRot) || !Same(p.NowRot, p.ShownRot))
                {
                    p.T.localRotation = local; p.SetRot = local; p.ShownRot = p.T.localRotation; p.Moved = true;
                }
                // 자리: 늘고 주는 뼈는 보이는 자리로 (부모가 움직였거나 자리가 바뀌었을 때만), 그 밖에는 쉬는 로컬 자리 그대로
                if (placed)
                {
                    if (p.Moved || !Same(p.Show, p.SetAt) || p.NowPos != p.ShownPos) { p.T.position = p.Show; p.SetAt = p.Show; p.ShownPos = p.T.localPosition; p.Moved = true; }
                }
                else
                {
                    if (p.NowPos != p.RestLocalPos) { p.T.localPosition = p.RestLocalPos; p.Moved = true; }
                    p.ShownPos = p.RestLocalPos; p.SetAt = Unset;
                }
                // 크기: 말랑 모드는 눌린 만큼 옆으로 넓힌다 (PhysBoneSoft.cs), 그 밖에는 쉬는 크기 그대로
                if (soft != null && turn) { Bulge(p, ch.P[p.Children[0]], soft); p.ShownScale = p.T.localScale; p.Moved = true; }
                else
                {
                    if (p.NowScale != p.RestLocalScale) { p.T.localScale = p.RestLocalScale; p.Moved = true; }
                    p.ShownScale = p.RestLocalScale;
                }
            }
        }

        static Vector3 V(float[] a) => new Vector3(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0, a.Length > 2 ? a[2] : 0);
    }
}
