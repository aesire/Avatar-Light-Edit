// VRChat PhysBone 미리보기 시뮬레이션 (IR physBones / physBoneColliders).
// VRChat의 실제 구현은 공개되지 않았으므로 문서에 나온 파라미터 의미에 맞춘 근사다 (위치 기반 적분):
//   스텝: 값은 60 Hz 스텝 기준. 프레임마다 그 프레임 시간을 1~4 스텝으로 나눠 돌고 값을 스텝 길이에 맞게 환산한다 (90 Hz VR·144 Hz 화면에서도 같은 움직임).
//     루트(움직이지 않는 관절)는 스텝 사이에 지난 프레임 자리 → 이번 자리로 옮겨 간다.
//   Pull: 쉬는 자세로 돌아가려는 힘 · Spring(Momentum): 움직임을 이어 가는 정도(흔들림) · Stiffness(Advanced): 쉬는 자세를 지키는 정도
//     속도 감쇠는 몸(쉬는 자리)의 움직임에 대해서다: 일정한 속도로 걸으면 체인은 그대로, 몸이 빨라지거나 멈출 때만 흔들린다 (월드 기준이면 바람에 날리듯 뒤로 누웠다)
//   Gravity / Gravity Falloff: 쉬는 방향을 아래(음수면 위)로 기울임. Falloff는 불러온 자세(쉬는 자세)에서 중력을 빼는 정도 — 뼈가 그때만큼 아래를 향하면 그만큼 뺀다
//   Immobile: 아바타(또는 루트) 움직임을 얼마나 무시하고 같이 움직일지 (All Motion: 루트의 위치·회전, World: 아바타 루트의 위치만) · Limits: Angle / Hinge / Polar
//   순간이동: 체인 루트가 한 프레임에 크게 옮겨지거나 돌거나 크기가 바뀌면(스냅 회전, 방 크기 배율, 아바타 옮기기) 체인을 통째로 같이 옮긴다
//   Stretch·Squish: 뼈 길이 허용 범위. 움직임으로는 Stretch Motion만큼만 늘고 줄고, 잡기·충돌은 범위 끝까지 · Collider: Sphere / Capsule / Plane, Inside Bounds
//   Version: 1.1은 Gravity가 쉬는 자세가 돌아가는 비율(Pull이 있어야 그쪽으로 간다)·Stiffness가 직전 방향을 지키는 비율. 1.0은 Gravity가 Pull과 겨루는 힘이고 줄어듦·움직임으로 늘어남이 없다
//   꺼짐: 컴포넌트가 붙은 오브젝트나 루트가 꺼져 있으면 돌지 않는다. Reset When Disabled면 꺼질 때 쉬는 자세로 · Is Animated: 쉬는 자세가 밖에서 바뀐 뼈 값을 따라간다
//   충돌은 PhysBone에 적힌 콜라이더 목록과 한다. Allow Collision은 "목록 밖 콜라이더(다른 사람의 손 등)"를 받을지라서 목록과는 무관하다.
//   뼈는 반지름을 가진 캡슐(부모 관절 → 자식 관절)로 부딪힌다. 콜라이더에 Bones As Spheres가 켜져 있으면 관절 구로만.
// 값마다 커브가 있으면 체인 위치(루트 0 → 끝 1)의 커브 값을 곱한다.
// 매 프레임: 뼈를 쉬는 자세로 되돌림 → 목표 위치 계산 → 스텝마다 적분·제약·충돌 → 시뮬레이션 위치로 뼈 회전.
// Time.deltaTime을 쓰므로 셰이더 시간 멈춤(pauseTime, timeScale 0)이면 시뮬레이션도 멈춘다 (촬영 결과가 매번 같게).
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
        const float JumpDist = 0.3f, JumpAngle = 20f; // 한 프레임에 체인 루트가 이만큼(아바타 크기 1 기준 m, 도) 넘게 움직이면 순간이동으로 본다
        const float LenReturn = 0.25f;     // 잡기·충돌로 늘거나 준 뼈가 움직임 범위로 돌아오는 빠르기 (60 Hz 스텝마다 남은 차이의 몫)
        const float V10GravityPull = 0.2f; // Version 1.0 Gravity의 힘 세기 (Pull과 같은 단위)

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
            // 매 프레임
            public Vector3 Target; public Quaternion RestRot;
            public Vector3 LastTarget;     // 지난번 스텝을 돈 프레임의 Target (몸이 움직인 속도 → 흔들림 속도 제한)
            public Vector3 Pos, Prev;
            public Vector3 BodyV, BodyF;   // 몸(쉬는 자리) 속도와 FollowTime으로 거른 값 (m/s)
            public Quaternion Rot = Quaternion.identity; // 쉬는 자세 → 시뮬레이션 자세 회전 (부모부터 누적)
            // 입자별 값 (커브 적용)
            public float Pull, Spring, Stiffness, Gravity, GravityFalloff, Immobile, Radius, MaxAngleX, MaxAngleZ, MaxStretch, MaxSquish, StretchMotion;
            public Vector3 LimitRot;
            public float LenRatio = 1;     // 지금 뼈 길이 / 쉬는 길이 (잡았다 놓은 뒤 천천히 돌아오게)
            public Quaternion ShownRot; public Vector3 ShownPos, ShownScale; // 지난번에 뼈에 적은 값 (Is Animated: 이와 다르면 밖에서 바꾼 것)
            /// <summary>불러온 자세(원본이 만든 그대로)에서 이 관절이 콜라이더(번호)와 이미 겹쳐 있던 깊이. 이만큼은 원본 의도로 보고 봐준다.</summary>
            public readonly Dictionary<int, float> BindPen = new Dictionary<int, float>();
            // 떨림 재기 (Jitter): 프레임마다 보이는 위치의 2차 차분 크기 평균
            public Vector3 F1, F2;
            public Vector3 Show, Dev; // 화면에 보이는 위치와 그 쉬는 자리에서 벗어난 정도 (Smooth)
            public int Frames; public float Jitter;
        }

        sealed class Chain
        {
            public IrPhysBone Src;
            public Transform Root, Comp;   // Comp: 컴포넌트가 붙은 오브젝트 (루트와 다를 수 있다)
            public bool Active = true, ResetWhenDisabled, IsAnimated, V11; // V11: Version 1.1
            public readonly List<Particle> P = new List<Particle>();
            public readonly List<int> Colliders = new List<int>();
            public int LimitType, ImmobileType, MultiChild;
            public bool Advanced, Grabbable, Posable, SoftTarget; // SoftTarget: 말랑 모드 대상 (PhysBoneSoft.cs)
            public float GrabMovement;
            public Vector3 LastPos; public Quaternion LastRot; public bool HasLast;
            public Vector3 JumpPos; public Quaternion JumpRot; public float JumpScale; // 지난 프레임 체인 루트 (순간이동 알아보기)
            public readonly List<int> LegColliders = new List<int>(); // 다리 뚫림 막기로 더한 콜라이더 (PhysBoneLegGuard.cs)
            public int Depth;
        }

        sealed class Col
        {
            public Transform T; public bool Enabled;
            public int Shape; public bool Inside, AsSpheres, Auto; public float Radius, Height; // Auto: 다리 뚫림 막기로 더한 것
            public Vector3 Pos; public Quaternion Rot = Quaternion.identity;
            // 매 프레임
            public Vector3 C, A, B, N; public float R; public bool Active;
        }

        readonly List<Chain> _chains = new List<Chain>();
        readonly List<Col> _cols = new List<Col>();
        float _acc, _h = Step;   // 모은 시간, 지난 스텝 길이 (Verlet 속도를 다음 스텝 길이로 환산)
        Transform _sceneRoot;
        public bool Enabled = true;
        /// <summary>
        /// 무게감 (1 = 값을 스텝마다 그대로). 무거울수록 Pull을 1/W, 속도 감쇠를 1/√W로 줄여 같은 출렁임 성질(감쇠비)로 √W배 느리게 움직인다.
        /// 그리고 몸에 대해 흔들리는 속도를 MaxSwing/√W로 묶는다 (빠르게 휙휙 튀는 움직임이 가벼워 보인다).
        /// 60 Hz 스텝마다 Pull을 그대로 넣으면 제자리로 너무 빨리 돌아와 가벼워 보여서 기본은 4 (Host 프리셋 1.5 · 4 · 10).
        /// </summary>
        public float Weight = 4f;
        const float MaxSwing = 2.5f;   // 무게 1일 때 몸에 대한 흔들림 속도 상한 (아바타 크기 1 기준 m/s, 예전 방식)
        // 느낌 (2026-10-23 사용자: "작고 빠른 움직임에 반응하지 않게, 한참 더 천천히, 중력은 더, 더 단단하고 복원력도 더, 부딪히면 떨지 말고 제자리에"):
        // 2026-10-24 "너무 단단해, 관성을 더" → 기본값을 그 사이로 옮기고 Host 설정 › 미리보기 PhysBone › 느낌에서 바꾸게 (setPhysBones { feel: { 열쇠: 값 } }).
        // 기본값은 Host `PhysBoneFeel.Items`와 같게 둔다 (Host가 켤 때 모두 보내므로 Host 없이 도는 E2E에서만 이 값을 쓴다).
        public float FollowTime = 0.1f;   // follow: 몸 움직임의 빠르기 변화를 이 시간(초)으로 걸러 느낀다 — 길수록 작고 빠른 움직임(고개 끄덕임·잔걸음)에 덜 흔들린다. 0 = 거르지 않음
        public float SlowSwing = 1.6f;    // swing: 무게 1일 때 몸에 대한 흔들림 속도 상한 (m/s, 실제 상한은 ÷√무게)
        public float FirmPull = 1.5f;     // pull: Pull 배율 — 클수록 쉬는 자세로 단단히 돌아온다
        public float FirmDamp = 0.9f;     // damp: Spring(Momentum) 배율 — 클수록 관성이 남아 더 출렁인다
        public float GravityScale = 1.5f, FalloffScale = 0.7f; // gravity · falloff: Gravity 배율, Falloff로 빼는 몫의 배율
        public float Slop = 0.004f;       // slop: 콜라이더에 이만큼(아바타 크기 1 기준 m)까지는 파고들어도 그냥 둔다 (밀고 당기며 떨지 않게)
        public float ContactFriction = 0.7f; // friction: 닿은 채 미끄러지는 속도를 스텝마다 이만큼 줄인다 (1 = 닿은 자리에 멈춤)

        /// <summary>함께 세운 아바타(GuestAvatars): 지금 아바타의 켜짐·무게·느낌을 그대로 따른다 (매 프레임, 바뀐 것만).</summary>
        public void CopyFrom(PhysBoneSim o)
        {
            if (Enabled != o.Enabled) SetEnabled(o.Enabled);
            Weight = o.Weight; Inelastic = o.Inelastic; Smoothing = o.Smoothing; Legacy = o.Legacy;
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
        /// <summary>콜라이더에 닿은 관절의 튕겨 나가는 속도를 없앤다 (끄면 예전처럼 밀어낸 만큼이 속도로 남아 떨림: 비교 시험용).</summary>
        public bool Inelastic = true;
        float Friction => Legacy ? 0.2f : ContactFriction;
        public int ChainCount => _chains.Count;
        /// <summary>시뮬레이션이 움직이는 뼈 (고정 루트 제외). 바닥 맞추기가 흔들리는 천·머리카락을 몸에 붙은 것으로 세지 않게.</summary>
        public HashSet<Transform> MovingBones() { var s = new HashSet<Transform>(); foreach (var c in _chains) foreach (var p in c.P) if (p.T != null && !p.Fixed) s.Add(p.T); return s; }
        public int BoneCount { get { int n = 0; foreach (var c in _chains) foreach (var p in c.P) if (p.T != null) n++; return n; } }

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
                    Src = pb, Root = root, Comp = scene.NodeTransform(pb.Node) is Transform comp ? comp : root,
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
                foreach (var p in ch.P) { p.T01 = p.Depth / (float)maxDepth; Params(pb, p, curves); }
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
            ch.P.Add(new Particle { T = t, Parent = parent, Depth = depth, RestLocalRot = t.localRotation, RestLocalPos = t.localPosition, RestLocalScale = t.localScale, ShownRot = t.localRotation, ShownPos = t.localPosition, ShownScale = t.localScale });
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
            foreach (var ch in _chains) Seat(ch);
            _acc = 0; _h = Step;
        }

        /// 입자를 지금 쉬는 자리에 둔다 (뼈는 쉬는 자세여야 한다)
        static void Seat(Chain ch)
        {
            Targets(ch);
            foreach (var p in ch.P) { p.Pos = p.Prev = p.Show = p.LastTarget = p.Target; p.Dev = Vector3.zero; p.BodyV = p.BodyF = Vector3.zero; p.Rot = Quaternion.identity; p.Frames = 0; p.Jitter = 0; p.LenRatio = 1; }
            ch.LastPos = ch.Root.position; ch.LastRot = ch.Root.rotation; ch.HasLast = true;
            ch.JumpPos = ch.Root.position; ch.JumpRot = ch.Root.rotation; ch.JumpScale = Mathf.Abs(ch.Root.lossyScale.x);
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
            float moved = 0;
            // 프레임 시간을 같은 길이의 스텝 1~4개로 나눈다 (예전 60 Hz 고정 스텝은 90 Hz에서 세 프레임에 한 번 스텝이 없어 체인이 몸보다 늦게 보였다가 따라붙었다)
            _acc = Mathf.Min(_acc + dt, Step * MaxSteps);
            int steps = _acc < MinStep ? 0 : Mathf.Clamp(Mathf.CeilToInt(_acc / Step - 1e-3f), 1, MaxSteps);
            float h = steps > 0 ? _acc / steps : 0;
            if (Legacy) { steps = (int)(_acc / Step); h = Step; _acc -= steps * Step; }
            else if (steps > 0) _acc = 0;
            foreach (var c in _cols) Prepare(c);
            foreach (var ch in _chains)
            {
                if (!Awake(ch)) continue;
                RestoreChain(ch);
                Targets(ch);
                if (!Legacy) Carry(ch);
                Immobile(ch);
                for (int s = 0; s < steps; s++) { Simulate(ch, s, steps, h, s == 0 ? _h : h); Holds(ch); }
                if (steps > 0) foreach (var p in ch.P) p.LastTarget = p.Target;
                moved = Mathf.Max(moved, Smooth(ch, steps > 0 || Legacy));
                Apply(ch, SoftOf(ch));
                if (ch.IsAnimated) foreach (var p in ch.P) if (p.T != null) { p.ShownRot = p.T.localRotation; p.ShownPos = p.T.localPosition; p.ShownScale = p.T.localScale; }
                if (steps > 0) MeasureJitter(ch);
            }
            if (steps > 0) _h = h;
            Motion = moved / dt;
        }

        /// <summary>
        /// 지난 Tick에서 쉬는 자리에 대해 가장 많이 흔들린 관절의 빠르기 (아바타 크기 1 기준 m/s). 렌더러가 가만히 있는지 볼 때 쓴다 (2026-11-02).
        /// 몸 전체가 움직인 것은 Target에 들어 있어 세지 않는다 — 그건 자세·옮기기 메시지나 움직임 시험(Testing)으로 안다.
        /// </summary>
        public float Motion { get; private set; }
        /// <summary>움직임 시험(걷기·점프 등)이 도는 중.</summary>
        public bool Testing => _motion != null;

        /// <summary>
        /// 순간이동: 체인 루트가 한 프레임에 JumpDist·JumpAngle 넘게 움직이거나 크기가 바뀌면 체인을 루트와 함께 통째로 옮긴다.
        /// VR 스냅 회전(머리 본이 한 프레임에 30° 돈다)·방 크기 배율·아바타 옮기기에서 체인이 옛 자리에 남아 크게 휘둘리지 않게.
        /// </summary>
        void Carry(Chain ch)
        {
            var t = ch.Root; var pos = t.position; var rot = t.rotation; float sc = Mathf.Abs(t.lossyScale.x);
            float ratio = sc / Mathf.Max(1e-6f, ch.JumpScale);
            if (Vector3.Distance(pos, ch.JumpPos) > JumpDist * sc || Quaternion.Angle(rot, ch.JumpRot) > JumpAngle || Mathf.Abs(ratio - 1) > 0.01f)
            {
                var dr = rot * Quaternion.Inverse(ch.JumpRot); var from = ch.JumpPos;
                Vector3 M(Vector3 x) => pos + dr * (x - from) * ratio;
                foreach (var p in ch.P)
                {
                    p.Pos = M(p.Pos); p.Prev = M(p.Prev); p.LastTarget = M(p.LastTarget); p.Show = M(p.Show);
                    p.F1 = M(p.F1); p.F2 = M(p.F2); p.Dev = dr * p.Dev * ratio; p.BodyV = dr * p.BodyV * ratio; p.BodyF = dr * p.BodyF * ratio;
                }
                // Immobile이 같은 움직임을 또 옮기지 않게 기준을 지금으로
                var src = ch.ImmobileType == 1 && _sceneRoot != null ? _sceneRoot : ch.Root;
                ch.LastPos = src.position; ch.LastRot = src.rotation;
            }
            ch.JumpPos = pos; ch.JumpRot = rot; ch.JumpScale = sc;
        }

        /// <summary>
        /// 화면에 보일 위치: 쉬는 자리에서 벗어난 정도(Dev)를 크기에 따라 따라간다. 몇 mm 안쪽의 잔떨림(여러 콜라이더에 번갈아 밀리며 생기는 것)은
        /// 거의 따라가지 않고, 크게 움직이면 그대로 따라간다. 아바타 전체가 움직이는 것은 Target에 들어 있어 늦어지지 않는다. 시뮬레이션 값(Pos)은 그대로다.
        /// </summary>
        public bool Smoothing = true;
        /// <summary>예전 방식 (비교 시험용, 2026-10-23 전): 60 Hz 고정 스텝(스텝 없는 프레임 있음)·월드 기준 속도 감쇠·순간이동 처리 없음.</summary>
        public bool Legacy;
        float SmoothBand = 0.006f; // smooth: 이 정도(아바타 크기 1 기준 m) 움직임이면 반만 따라간다 (2026-10-23 3 → 6 mm: 닿은 관절이 제자리에 있게)

        /// <returns>벗어난 정도(Dev)가 이번에 가장 많이 바뀐 관절의 바뀐 양 (아바타 크기 1 기준 m)</returns>
        float Smooth(Chain ch, bool stepped)
        {
            float scale = Mathf.Max(1e-6f, Mathf.Abs(ch.Root.lossyScale.x));
            float b = Mathf.Max(1e-5f, SmoothBand) * scale, b2 = b * b, moved = 0;
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
            return Mathf.Sqrt(moved) / scale;
        }

        static void MeasureJitter(Chain ch)
        {
            float scale = Mathf.Max(1e-6f, Mathf.Abs(ch.Root.lossyScale.x));
            foreach (var p in ch.P)
            {
                if (p.Fixed || p.Parent < 0) continue;
                var x = p.T != null ? p.T.position : p.Pos;
                if (p.Frames >= 2) p.Jitter = Mathf.Lerp(p.Jitter, (x - 2 * p.F1 + p.F2).magnitude / scale, 0.05f);
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
        void Settle(Particle p, Vector3 pos, Vector3 push, float r)
        {
            if (!Inelastic || push.sqrMagnitude < 1e-14f) return;
            var v = Vector3.ProjectOnPlane(pos - p.Prev, push.normalized) * Mathf.Pow(1 - Friction, r);
            p.Prev = pos - v;
        }

        void Restore() { foreach (var ch in _chains) RestoreChain(ch); }

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
                if (ch.IsAnimated) { p.ShownRot = p.RestLocalRot; p.ShownPos = p.RestLocalPos; p.ShownScale = p.RestLocalScale; }
            }
        }

        static void Targets(Chain ch)
        {
            foreach (var p in ch.P)
            {
                if (p.T != null) { p.Target = p.T.position; p.RestRot = p.T.rotation; }
                else { p.Target = p.Owner.TransformPoint(p.EndLocal); p.RestRot = p.Owner.rotation; }
            }
        }

        /// Immobile: 루트의 위치·회전(All Motion) 또는 아바타 루트의 위치(World, VRChat 문서: 월드 공간 위치 이동만)만큼 입자를 같이 옮긴다
        void Immobile(Chain ch)
        {
            bool world = ch.ImmobileType == 1 && _sceneRoot != null;
            var src = world ? _sceneRoot : ch.Root;
            var pos = src.position; var rot = src.rotation;
            if (!ch.HasLast) { ch.LastPos = pos; ch.LastRot = rot; ch.HasLast = true; return; }
            var dr = world ? Quaternion.identity : rot * Quaternion.Inverse(ch.LastRot);
            foreach (var p in ch.P)
            {
                if (p.Immobile <= 0 || p.Fixed) continue;
                p.Pos = Vector3.Lerp(p.Pos, pos + dr * (p.Pos - ch.LastPos), p.Immobile);
                p.Prev = Vector3.Lerp(p.Prev, pos + dr * (p.Prev - ch.LastPos), p.Immobile);
                p.LastTarget = Vector3.Lerp(p.LastTarget, pos + dr * (p.LastTarget - ch.LastPos), p.Immobile); // 몸이 움직인 속도에서 이미 같이 옮긴 만큼 뺀다
            }
            ch.LastPos = pos; ch.LastRot = rot;
        }

        /// <param name="s">이 프레임의 몇 번째 스텝 (0부터)</param>
        /// <param name="h">스텝 길이 (초)</param>
        /// <param name="hPrev">지난 스텝 길이: Verlet 속도(Pos − Prev)를 이번 스텝 길이로 환산한다</param>
        void Simulate(Chain ch, int s, int steps, float h, float hPrev)
        {
            float scale = Mathf.Abs(ch.Root.lossyScale.x);
            var soft = SoftOf(ch);
            float rs = h / Step, vScale = h / Mathf.Max(1e-6f, hPrev);
            float frac = Legacy ? 1 : (s + 1) / (float)steps; // 움직이지 않는 관절: 지난 프레임 자리 → 이번 자리
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
                    float gs = Legacy ? 1 : GravityScale, fs = Legacy ? 1 : FalloffScale;
                    float g = Mathf.Abs(p.Gravity) * gs * (1 - p.GravityFalloff * fs * Mathf.Max(0, 1 - Mathf.Abs(Vector3.Dot(restDir, gd) - bind)));
                    if (ch.V11 || Legacy) aim = Vector3.Slerp(restDir, gd, Mathf.Clamp01(g));
                    else gForce = Mathf.Clamp01(g);   // Version 1.0: 쉬는 방향은 그대로 두고 아래에서 힘으로 더한다
                }
                var goal = par.Pos + aim * (soft != null ? len * SagLength(soft, aim) : len);

                var vel = (p.Pos - p.Prev) * vScale;
                p.Prev = p.Pos;
                float pull = soft != null ? Mathf.Min(p.Pull, soft.PullMax) : p.Pull, spring = soft != null ? Mathf.Max(p.Spring, soft.SpringMin) : p.Spring;
                float w = Mathf.Max(1, Weight);
                pull /= w; spring = 1 - (1 - spring) / Mathf.Sqrt(w);
                if (!Legacy) { pull = Mathf.Min(0.5f, pull * FirmPull); spring = Mathf.Min(0.98f, spring * FirmDamp); }
                pull = 1 - Mathf.Pow(1 - pull, rs); spring = Mathf.Pow(spring, rs);   // 60 Hz 값 → 이 스텝 길이
                // 몸(쉬는 자리)이 이 스텝에 움직인 만큼. Immobile만큼은 Immobile()이 이미 같이 옮겼고 LastTarget에서도 뺐다
                var bodyStep = (p.Target - p.LastTarget) / steps;
                // 감쇠·속도 제한은 몸에 대한 속도에만: 일정하게 걸으면 그대로 따라가고, 빨라지거나 멈춘 만큼만 흔들린다
                var rel = vel - bodyStep; float cap = (Legacy ? MaxSwing : SlowSwing) / Mathf.Sqrt(w) * h * scale;
                if (!Legacy)
                {
                    // 몸 빠르기의 변화(가속)를 걸러서만 느낀다: 지난 스텝 몸 속도에 대한 속도에서, 거른 몸 속도가 바뀐 만큼만 뺀다
                    var bodyV = bodyStep / h;
                    var f = FollowTime < 1e-4f ? bodyV : p.BodyF + (bodyV - p.BodyF) * (1 - Mathf.Exp(-h / FollowTime));
                    rel = vel - p.BodyV * h - (f - p.BodyF) * h;
                    p.BodyV = bodyV; p.BodyF = f;
                }
                if (rel.sqrMagnitude > cap * cap) rel *= cap / rel.magnitude;
                var pos = Legacy ? p.Pos + (bodyStep + rel) * spring + (goal - p.Pos) * pull   // 예전: 월드 기준 감쇠
                                 : p.Pos + bodyStep + rel * spring + (goal - p.Pos - bodyStep) * pull;
                // Version 1.0의 Gravity: Pull과 겨루는 힘. VRChat 실제 세기는 비공개라, Pull이 V10GravityPull일 때 Version 1.1 Gravity와 비슷하게 기울도록 맞췄다 (Pull이 0이면 끝까지 처지고 Pull이 크면 덜 처진다)
                if (gForce > 0) pos += gd * (len * gForce * (1 - Mathf.Pow(1 - Mathf.Min(0.5f, V10GravityPull / w * (Legacy ? 1 : FirmPull)), rs)));
                if (ch.Advanced && p.Stiffness > 0)
                {
                    // Version 1.1: 직전 방향을 지킨다 (몸이 움직인 만큼은 같이 옮긴 자리에서. p.Prev = 이 스텝 전 위치) · 1.0: 쉬는 자세를 지킨다
                    var keep = ch.V11 && !Legacy ? p.Prev + bodyStep : par.Pos + par.Rot * (p.Target - par.Target);
                    pos = Vector3.Lerp(pos, keep, 1 - Mathf.Pow(1 - p.Stiffness * 0.5f, rs));
                }

                pos = MotionLength(par.Pos, pos, len, p, restVec, soft, rs);
                pos = Limit(ch, par, p, pos, restDir);
                var free = pos;
                if (ch.Colliders.Count > 0 || LegGuard && ch.LegColliders.Count > 0)
                {
                    // 뼈 캡슐: 부모 관절 반지름 → 자식 관절 반지름으로 가늘어지거나 굵어진다
                    float r0 = par.Radius * scale, r = p.Radius * scale;
                    for (int it = 0; it < 4; it++)  // 밀어내기와 뼈 길이 맞추기가 서로 당기므로 몇 번 되풀이한다
                    {
                        float slop = Legacy ? 0 : Slop * scale;
                        foreach (var ci in ch.Colliders) Collide(_cols[ci], par.Pos, ref pos, r0, r, Allow(par, ci), slop);
                        if (LegGuard) foreach (var ci in ch.LegColliders) Collide(_cols[ci], par.Pos, ref pos, r0, r, Allow(par, ci), slop);
                        pos = Length(par.Pos, pos, len, p, restVec, soft);
                    }
                }
                if (soft != null) { Contact(ch, p, ref pos, SoftRadius(p, scale)); pos = Length(par.Pos, pos, len, p, restVec, soft); }
                Settle(p, pos, pos - free, rs);
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
        /// 말랑 모드·예전 방식은 범위 전부
        Vector3 MotionLength(Vector3 from, Vector3 pos, float len, Particle p, Vector3 restVec, SoftPreset soft, float rs)
        {
            if (soft != null || Legacy || p.StretchMotion >= 1) return Length(from, pos, len, p, restVec, soft);
            float lo = 1 - p.MaxSquish * p.StretchMotion, hi = 1 + p.MaxStretch * p.StretchMotion;
            float eased = Mathf.Lerp(p.LenRatio, Mathf.Clamp(p.LenRatio, lo, hi), 1 - Mathf.Pow(1 - LenReturn, rs));
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
            var frame = par.Rot * par.RestRot * Quaternion.Euler(p.LimitRot);
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
                        var u = Vector3.ProjectOnPlane(frame * Vector3.right, axis).normalized;
                        var v = Vector3.Cross(axis, u);
                        float cu = Vector3.Dot(dev, u), cv = Vector3.Dot(dev, v);
                        float a = Mathf.Max(0.01f, p.MaxAngleZ), b = Mathf.Max(0.01f, p.MaxAngleX);
                        float max = a * b / Mathf.Sqrt(b * b * cu * cu + a * a * cv * cv);
                        if (theta > max) dir = Vector3.RotateTowards(axis, dir, max * Mathf.Deg2Rad, 0);
                        break;
                    }
            }
            return par.Pos + dir * l;
        }

        static void Prepare(Col c)
        {
            c.Active = c.T != null && c.Enabled && c.T.gameObject.activeInHierarchy; // 프레임마다 한 번 (스텝·입자마다 묻지 않게)
            if (!c.Active) return;
            float s = Mathf.Abs(c.T.lossyScale.x);
            var rot = c.T.rotation * c.Rot;
            c.C = c.T.TransformPoint(c.Pos); c.R = c.Radius * s; c.N = rot * Vector3.up;
            float half = Mathf.Max(0, c.Height * s * 0.5f - c.R); // 캡슐 높이는 양 끝 반구 포함
            c.A = c.C - c.N * half; c.B = c.C + c.N * half;
        }

        /// <summary>
        /// 뼈(부모 관절 a → 자식 관절 pos, 반지름 r)를 콜라이더 밖(Inside Bounds면 안)으로 민다. 부모는 이 스텝에서 이미 정해졌으므로 자식만 옮긴다.
        /// 뼈 위 t 지점이 겹치면 자식을 겹친 만큼 / t 옮긴다 (부모에 가까운 지점일수록 크게: 그래야 그 지점이 빠진다).
        /// </summary>
        /// 부모 관절의 겹침을 얼마까지 봐줄지: 움직이지 않는 관절은 전부(-1), 그 밖에는 불러온 자세에서 이미 겹쳐 있던 만큼만
        static float Allow(Particle par, int ci) => par.Fixed || par.Parent < 0 ? -1 : par.BindPen.TryGetValue(ci, out var v) ? v : 0;

        /// 불러온 자세에서 관절마다 콜라이더와 겹친 깊이 (Build 끝에서 한 번)
        void MeasureBindPen()
        {
            foreach (var c in _cols) Prepare(c);
            foreach (var ch in _chains)
            {
                float scale = Mathf.Abs(ch.Root.lossyScale.x);
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

        /// <param name="parentAllow">부모 관절 겹침을 봐줄 최대 깊이 (-1 = 지금 겹친 만큼 전부: 움직이지 않는 관절)</param>
        /// <param name="slop">이만큼 파고든 것은 그냥 둔다</param>
        static void Collide(Col c, Vector3 a, ref Vector3 pos, float r0, float r, float parentAllow = -1, float slop = 0)
        {
            if (!c.Active) return;
            if (c.Shape == 2) // Plane: 위쪽(Y) 반공간 밖으로 (뼈 끝만: 부모는 이미 밖에 있다)
            {
                float d = Vector3.Dot(pos - c.C, c.N) - r;
                if (d < -slop) pos -= c.N * (d + slop);
                return;
            }
            if (c.Inside)
            {
                var q0 = c.Shape == 1 ? Closest(c.A, c.B, pos) : c.C;
                var v0 = pos - q0; float l0 = v0.magnitude;
                float max = Mathf.Max(0, c.R - r);
                if (l0 > max + slop) pos = q0 + v0 / l0 * (max + slop);
                return;
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
            if (bestExcess <= 0) return;
            if (bestN.sqrMagnitude < 1e-8f) bestN = Vector3.forward;
            pos += bestN * (bestExcess / Mathf.Max(bestT, 0.25f));
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

        /// 시뮬레이션 위치로 뼈를 돌린다 (부모부터. 부모를 돌리면 자식 뼈도 따라가므로 지금 회전을 기준으로 다시 잰다)
        static void Apply(Chain ch, SoftPreset soft)
        {
            for (int i = 0; i < ch.P.Count; i++)
            {
                var p = ch.P[i];
                if (p.T == null || p.Children.Count == 0) continue;
                if (p.Children.Count > 1 && i == 0 && ch.MultiChild == 0) continue; // Ignore: 자식이 여럿인 루트는 돌리지 않음
                var undo = p.T.rotation * Quaternion.Inverse(p.RestRot);
                Vector3 rest = Vector3.zero, sim = Vector3.zero;
                int n = p.Children.Count > 1 && ch.MultiChild == 1 ? 1 : p.Children.Count; // First: 첫 자식만
                for (int k = 0; k < n; k++)
                {
                    var c = ch.P[p.Children[k]];
                    rest += undo * (c.Target - p.Target);
                    sim += c.Show - p.T.position;
                }
                if (rest.sqrMagnitude < 1e-12f || sim.sqrMagnitude < 1e-12f) continue;
                p.T.rotation = Quaternion.FromToRotation(rest, sim) * p.T.rotation;
                if (soft != null) Bulge(p, ch.P[p.Children[0]], soft);
                // 늘어남·줄어듦이 허용되면 자식 위치도 시뮬레이션 위치로
                foreach (var ci in p.Children)
                {
                    var c = ch.P[ci];
                    if (c.T != null && (soft != null || c.MaxStretch > 0 || c.MaxSquish > 0)) c.T.position = c.Show;
                }
            }
        }

        // ---------------- 잡기 · 고정 ----------------
        // 미리보기에서 관절을 마우스로 끌어 본다 (VRChat에서 손으로 잡는 것과 비슷하게). 놓으면 물리대로 돌아간다.
        // 잡은 채 오른쪽 클릭하면 그 자리에 고정(VRChat의 Posing)한다. 고정은 체인 루트 기준 위치로 기억해 아바타가 움직이면 같이 움직인다.
        // 고정한 관절을 오른쪽 클릭하면 풀린다.

        sealed class Hold
        {
            public Chain Ch; public Particle P;
            public readonly List<Particle> Path = new List<Particle>(); // 루트 → 잡은 입자
            public Vector3 Local;                                       // 고정: 체인 루트 기준 위치
            public Hold(Chain ch, Particle p) { Ch = ch; P = p; for (var q = p; ; q = ch.P[q.Parent]) { Path.Insert(0, q); if (q.Fixed) break; } }
        }

        Hold _grab; Vector3 _grabTarget; float _grabDepth;
        readonly List<Hold> _pins = new List<Hold>();
        public bool IsGrabbing => _grab != null;

        /// <summary>화면 좌표(픽셀, 왼쪽 아래 원점) 근처에서 잡을 수 있는 입자. 없으면 null.</summary>
        Particle Pick(Camera cam, Vector2 mouse, float radiusPx, out float depth) => Pick(cam, mouse, radiusPx, out depth, out _);

        Particle Pick(Camera cam, Vector2 mouse, float radiusPx, out float depth, out Chain chain)
        {
            Particle best = null; float bestD = radiusPx * radiusPx; depth = 0; chain = null;
            if (!Enabled) return null;
            foreach (var ch in _chains)
            {
                if (!ch.Grabbable || !ch.Active) continue;
                for (int i = 1; i < ch.P.Count; i++)
                {
                    if (ch.P[i].Fixed) continue;
                    var sp = cam.WorldToScreenPoint(Position(ch.P[i]));
                    if (sp.z <= 0) continue;
                    float d = (new Vector2(sp.x, sp.y) - mouse).sqrMagnitude;
                    if (d <= bestD) { bestD = d; best = ch.P[i]; depth = sp.z; chain = ch; }
                }
            }
            return best;
        }

        public bool CanGrab(Camera cam, Vector2 mouse, float radiusPx) => Pick(cam, mouse, radiusPx, out _) != null;

        public bool Grab(Camera cam, Vector2 mouse, float radiusPx)
        {
            var p = Pick(cam, mouse, radiusPx, out _grabDepth, out var ch);
            if (p == null) return false;
            _pins.RemoveAll(h => h.P == p); // 고정한 관절을 다시 잡으면 고정이 풀린다
            _grab = new Hold(ch, p);
            _grabTarget = Position(p);
            return true;
        }

        public void DragTo(Camera cam, Vector2 mouse)
        {
            if (_grab != null) _grabTarget = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, _grabDepth));
        }

        // 놓은 직후 잠깐은 오른쪽 클릭으로 고정할 수 있다 (왼쪽을 먼저 떼고 오른쪽을 누르는 경우)
        const float PinGrace = 0.5f;
        Hold _released; Vector3 _releasedTarget; float _releasedAt = -10;
        public bool RecentlyReleased => _released != null && Time.unscaledTime - _releasedAt < PinGrace;

        public void Release()
        {
            if (_grab != null) { _released = _grab; _releasedTarget = _grabTarget; _releasedAt = Time.unscaledTime; }
            _grab = null;
        }

        /// <summary>잡고 있는 관절을 지금 자리에 고정한다 (allowPosing이 꺼진 PhysBone은 안 됨). 결과 안내 문구를 돌려준다.</summary>
        public string PinGrabbed()
        {
            if (_grab == null && RecentlyReleased) { _grab = _released; _grabTarget = _releasedTarget; }
            _released = null;
            if (_grab == null) return null;
            var name = _grab.Ch.Root.name;
            if (!_grab.Ch.Posable) return $"{name}: 원본 PhysBone에서 고정(Allow Posing)이 꺼져 있어 고정할 수 없어요";
            _grab.Local = _grab.Ch.Root.InverseTransformPoint(_grabTarget);
            _pins.Add(_grab);
            _grab = null;
            return $"{name} 고정 · 고정한 관절을 오른쪽 클릭하면 풀려요";
        }

        public bool IsPinnedAt(Camera cam, Vector2 mouse, float radiusPx) => PinAt(cam, mouse, radiusPx) != null;

        Hold PinAt(Camera cam, Vector2 mouse, float radiusPx)
        {
            Hold best = null; float bestD = radiusPx * radiusPx;
            foreach (var h in _pins)
            {
                if (!h.Ch.Active) continue;
                var sp = cam.WorldToScreenPoint(Position(h.P));
                if (sp.z <= 0) continue;
                float d = (new Vector2(sp.x, sp.y) - mouse).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = h; }
            }
            return best;
        }

        public string UnpinAt(Camera cam, Vector2 mouse, float radiusPx) { var h = PinAt(cam, mouse, radiusPx); return h != null && _pins.Remove(h) ? h.Ch.Root.name + " 고정 풀림" : null; }
        public void UnpinAll() => _pins.Clear();

        /// 스텝마다: 고정한 관절, 잡은 관절 순으로 체인을 굽힌다
        void Holds(Chain ch)
        {
            var soft = SoftOf(ch);
            foreach (var h in _pins) if (h.Ch == ch) Pull(h, ch.Root.TransformPoint(h.Local), 1, soft);
            if (_grab != null && _grab.Ch == ch) Pull(_grab, _grabTarget, Mathf.Lerp(0.2f, 1f, ch.GrabMovement), soft);
            for (int h = 0; h < 2; h++) if (_vrGrab[h] != null && _vrGrab[h].Ch == ch) Pull(_vrGrab[h], _vrTarget[h], Mathf.Lerp(0.2f, 1f, ch.GrabMovement), soft);   // VR 손 (PhysBoneVrGrab.cs)
        }

        /// <summary>
        /// 관절을 목표 쪽으로 끌고 루트까지의 뼈를 따라 굽힌다 (FABRIK: 끝에서 루트로, 루트에서 끝으로 뼈 길이 맞추기).
        /// follow: 한 스텝에 목표까지 가는 비율 (잡기는 Grab Movement, 고정은 1). 경로 밖의 가지는 다음 스텝에 부모를 따라간다.
        /// 말랑 모드면 잡은 관절 바로 위 뼈가 프리셋 범위 안에서 줄고 늘어난다 (몸 쪽으로 밀면 눌린다).
        /// 그 밖에는 닿지 않는 곳을 잡으면 경로의 뼈가 Max Stretch까지 늘고, 부모 쪽으로 밀면 잡은 관절 바로 위 뼈가 Max Squish까지 준다.
        /// </summary>
        static void Pull(Hold h, Vector3 target, float follow, SoftPreset soft)
        {
            var path = h.Path; int n = path.Count;
            if (n < 2) return;
            var len = new float[n]; float total = 0;
            for (int i = 1; i < n; i++) { len[i] = (path[i].Target - path[i - 1].Target).magnitude; total += len[i]; }
            var goal = Vector3.Lerp(h.P.Pos, target, follow);
            float last = len[n - 1], want = (goal - path[n - 2].Pos).magnitude;
            if (soft != null) len[n - 1] = Mathf.Clamp(want, last * (1 - soft.Squish), last * (1 + soft.Stretch));
            else
            {
                float over = total > 1e-6f ? (goal - path[0].Pos).magnitude / total : 1;
                if (over > 1) for (int i = 1; i < n; i++) len[i] *= Mathf.Min(over, 1 + path[i].MaxStretch);
                else if (want < last) len[n - 1] = Mathf.Max(want, last * (1 - h.P.MaxSquish));
            }
            var root = path[0].Pos;
            for (int it = 0; it < 4; it++)
            {
                path[n - 1].Pos = goal;
                for (int i = n - 2; i > 0; i--) path[i].Pos = path[i + 1].Pos + (path[i].Pos - path[i + 1].Pos).normalized * len[i + 1];
                for (int i = 1; i < n; i++) path[i].Pos = path[i - 1].Pos + (path[i].Pos - path[i - 1].Pos).normalized * len[i];
                path[0].Pos = root;
            }
            for (int i = 1; i < n; i++)
            {
                var p = path[i]; var par = path[i - 1];
                p.Prev = Vector3.Lerp(p.Prev, p.Pos, 0.5f); // 끌던 속도를 반만 남겨 놓았을 때 살짝 튕기게
                var restVec = par.Rot * (p.Target - par.Target);
                if (restVec.sqrMagnitude > 1e-12f) { p.Rot = Quaternion.FromToRotation(restVec, p.Pos - par.Pos) * par.Rot; p.LenRatio = (p.Pos - par.Pos).magnitude / restVec.magnitude; }
            }
        }

        // ---------------- 표시 (기즈모) ----------------
        // ViewAids가 GL.LINES를 연 상태에서 부른다: 체인(뼈 사이 선, 관절, 반지름 원), 콜라이더(구·캡슐·평면)

        /// 지금 보이는 위치: 뼈는 Transform, 끝점은 시뮬레이션 위치 (꺼져 있으면 쉬는 자리)
        Vector3 Position(Particle p) => p.T != null ? p.T.position : Enabled ? p.Show : p.Owner.TransformPoint(p.EndLocal);

        public void DrawGizmos(Camera cam, Vector2 hoverMouse, float hoverPx)
        {
            var right = cam.transform.right; var up = cam.transform.up;
            var hover = _grab?.P ?? Pick(cam, hoverMouse, hoverPx, out _);
            var pinned = new HashSet<Particle>(); foreach (var h in _pins) pinned.Add(h.P);
            var pinCol = new Color(1f, 0.3f, 0.45f, 1f);
            var chain = new Color(0.45f, 1f, 0.55f, 0.9f);
            var hot = new Color(1f, 0.85f, 0.2f, 1f);
            foreach (var ch in _chains)
            {
                if (!ch.Active) continue;
                float scale = Mathf.Abs(ch.Root.lossyScale.x);
                for (int i = 0; i < ch.P.Count; i++)
                {
                    var p = ch.P[i]; var pos = Position(p);
                    if (p.Parent >= 0) { GL.Color(chain); GL.Vertex(Position(ch.P[p.Parent])); GL.Vertex(pos); }
                    float r = p.Radius * scale;
                    float mark = Mathf.Max(0.004f, Vector3.Distance(cam.transform.position, pos) * 0.008f);
                    var c = p == hover ? hot : pinned.Contains(p) ? pinCol : chain;
                    ViewAids.Circle(pos, right, up, r > 0 ? r : mark, i == 0 ? new Color(c.r, c.g, c.b, 0.45f) : c, r > 0 ? 20 : 8);
                }
            }
            foreach (var c in _cols)
            {
                Prepare(c);
                if (c.T == null) continue;
                if (c.Auto && !LegGuard) continue;
                var col = !c.Active ? new Color(0.6f, 0.6f, 0.6f, 0.35f) : c.Auto ? new Color(0.3f, 0.85f, 1f, 0.8f) : c.Inside ? new Color(0.75f, 0.5f, 1f, 0.9f) : new Color(1f, 0.55f, 0.2f, 0.9f);
                switch (c.Shape)
                {
                    case 2: // 평면: 사각형 + 법선
                        {
                            var t = Vector3.Cross(c.N, Mathf.Abs(c.N.y) < 0.9f ? Vector3.up : Vector3.right).normalized * 0.15f;
                            var b = Vector3.Cross(c.N, t);
                            GL.Color(col);
                            var q = new[] { c.C + t + b, c.C - t + b, c.C - t - b, c.C + t - b };
                            for (int k = 0; k < 4; k++) { GL.Vertex(q[k]); GL.Vertex(q[(k + 1) % 4]); }
                            GL.Vertex(c.C); GL.Vertex(c.C + c.N * 0.1f);
                            break;
                        }
                    case 1: // 캡슐: 양 끝 원 + 옆선 (화면 쪽에서 본 윤곽)
                        {
                            ViewAids.Circle(c.A, right, up, c.R, col, 20); ViewAids.Circle(c.B, right, up, c.R, col, 20);
                            var side = Vector3.Cross(c.B - c.A, cam.transform.forward);
                            if (side.sqrMagnitude > 1e-10f)
                            {
                                side = side.normalized * c.R;
                                GL.Color(col);
                                GL.Vertex(c.A + side); GL.Vertex(c.B + side); GL.Vertex(c.A - side); GL.Vertex(c.B - side);
                            }
                            break;
                        }
                    default: // 구
                        ViewAids.Circle(c.C, right, up, c.R, col, 24);
                        break;
                }
            }
        }

        // ---------------- 움직임 시험 ----------------
        // 아바타 전체(장면 루트)를 잠깐 흔들어 PhysBone 반응을 본다. 끝나면 제자리로.

        string _motion; float _mt; Vector3 _basePos, _baseScale; Quaternion _baseRot;

        public void StartMotion(string kind)
        {
            StopMotion();
            if (_sceneRoot == null || string.IsNullOrEmpty(kind) || kind == "none") return;
            _motion = kind; _mt = 0; _basePos = _sceneRoot.localPosition; _baseRot = _sceneRoot.localRotation; _baseScale = _sceneRoot.localScale;
        }

        void StopMotion()
        {
            if (_motion != null && _sceneRoot != null) { _sceneRoot.localPosition = _basePos; _sceneRoot.localRotation = _baseRot; _sceneRoot.localScale = _baseScale; }
            _motion = null;
        }

        /// <summary>쉬는 자리(중력 처짐 포함 전)에서 보이는 관절이 벗어난 거리 평균·최대 (아바타 크기 1 기준 cm, 시험용).</summary>
        public (float mean, float max) Swing()
        {
            float sum = 0, max = 0; int n = 0;
            foreach (var ch in _chains)
            {
                if (!ch.Active) continue;
                float scale = Mathf.Max(1e-6f, Mathf.Abs(ch.Root.lossyScale.x));
                foreach (var p in ch.P)
                {
                    if (p.Fixed || p.Parent < 0) continue;
                    float d = (Position(p) - p.Target).magnitude / scale * 100;
                    sum += d; n++; max = Mathf.Max(max, d);
                }
            }
            return (n > 0 ? sum / n : 0, max);
        }

        void UpdateMotion(float dt)
        {
            if (_motion == null || _sceneRoot == null) return;
            _mt += dt;
            float dur = _motion == "jump" ? 1.6f : _motion == "vrwalk" ? 3.5f : 3f;
            if (_mt >= dur) { StopMotion(); return; }
            float env = Mathf.Sin(Mathf.PI * _mt / dur); // 부드럽게 시작·끝
            var off = Vector3.zero; var rot = Quaternion.identity;
            switch (_motion)
            {
                // VR처럼 (시험용): 4 m/s로 0.8초 걷다 바로 멈춤 · 30° 스냅 회전 0 → 30 → 60 → 30 → 0 · 방 크기 배율 1 → 1.5 → 1
                case "vrwalk": off.z = 4f * Mathf.Min(_mt, 0.8f); break;
                case "snap": rot = Quaternion.Euler(0, 30f * (_mt < 0.6f ? 1 : _mt < 1.2f ? 2 : _mt < 1.8f ? 1 : 0), 0); break;
                case "resize": _sceneRoot.localScale = _baseScale * (_mt < 1.2f ? 1.5f : 1f); break;
                case "sway": off.x = 0.15f * env * Mathf.Sin(2 * Mathf.PI * 1.2f * _mt); break;
                case "jump": { float ph = _mt % 0.8f; off.y = ph < 0.5f ? 0.3f * Mathf.Sin(Mathf.PI * ph / 0.5f) : 0; break; }
                case "turn": rot = Quaternion.Euler(0, 70f * env * Mathf.Sin(2 * Mathf.PI * 0.7f * _mt), 0); break;
                case "walk": off.z = 0.35f * env; off.y = 0.03f * Mathf.Abs(Mathf.Sin(2 * Mathf.PI * 1.8f * _mt)); break;
            }
            _sceneRoot.localPosition = _basePos + off;
            _sceneRoot.localRotation = _baseRot * rot;
        }

        static Vector3 V(float[] a) => new Vector3(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0, a.Length > 2 ? a[2] : 0);
    }
}
