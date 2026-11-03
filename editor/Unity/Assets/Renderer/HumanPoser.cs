// 휴머노이드 자세 (PLAN 3.5): IR humanoid 매핑과 humanoidSettings(원본 .meta의 비틀림 분배·근육 범위)로 실행 중에 Avatar를 만들고 HumanPoseHandler로 머슬 값을 적용한다.
// 머슬 값은 아바타에 묶이지 않아 다른 아바타에 그대로 옮길 수 있다 (프리셋·.anim·저장한 자세가 모두 이 형식).
//
// 실제 본에 바로 Avatar를 만들지 않고, 휴머노이드 본과 그 조상만 이름을 겹치지 않게 복제한 대리 뼈대에 만든다.
// 의상을 합치면 같은 이름(Hips 등)의 본이 여럿 생겨 AvatarBuilder가 엉뚱한 본을 고를 수 있기 때문이다.
// 자세는 대리 뼈대에 적용한 뒤 휴머노이드 본의 로컬 회전(Hips는 위치도)만 실제 본에 옮긴다.
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class HumanPoser
    {
        sealed class Bone { public string Human; public Transform Real, Proxy; public Vector3 RestPos; public Quaternion RestRot; public int Parent = -1, Child = -1; }

        GameObject _proxyRoot;
        Avatar _avatar;
        HumanPoseHandler _handler;
        readonly List<Bone> _bones = new List<Bone>();   // 휴머노이드 본만 (자세를 옮길 대상)
        readonly List<(Transform t, Vector3 p, Quaternion r)> _proxyRest = new List<(Transform, Vector3, Quaternion)>();
        Bone _hips;
        HumanPose _rest;
        float _height;   // 지금 자세의 몸 높이 (bodyHeight가 없으면 쉬는 자세 값). 본 조작 결과도 이 높이로 맞춘다
        // 지금 적용된 머슬 값과, 그 결과 본을 다시 읽은 값. GetHumanPose는 SetHumanPose의 정확한 역이 아니어서
        // (비틀림 분배 등) 다시 읽은 값을 그대로 쓰면 건드리지 않은 머슬도 조금씩 움직인다 → 본 조작은 "읽은 값의 변화"만 더한다
        float[] _muscles, _readBack;
        Quaternion _bodyRot;   // 지금 자세의 몸 방향. 본 조작은 Hips를 돌릴 때만 바꾼다 (팔을 돌려도 Unity가 계산하는 몸 방향이 조금 바뀌어 화면과 기록이 어긋나므로)
        readonly Dictionary<string, int> _muscleIndex = new Dictionary<string, int>();
        readonly FloorFit _floor = new FloorFit();
        bool _floorFit = true;   // setView.floorFit
        JNode _lastPose;
        public bool Ready => _handler != null;
        public string Problem { get; private set; } = "";
        /// <summary>마지막 자세에서 모르는 머슬 이름 (Host 머슬 표가 Unity와 다르면 여기에 남는다).</summary>
        public readonly List<string> Unknown = new List<string>();

        public void Clear()
        {
            _handler?.Dispose(); _handler = null;
            if (_avatar != null) Object.Destroy(_avatar);
            if (_proxyRoot != null) Object.Destroy(_proxyRoot);
            _avatar = null; _proxyRoot = null; _hips = null;
            _bones.Clear(); _proxyRest.Clear(); _fingerBones.Clear(); _floor.Clear(); _lastPose = null;
            Problem = "";
        }

        /// <summary>moving = PhysBone이 움직이는 뼈 (바닥 맞추기에서 뺀다).</summary>
        public void Build(SceneBuilder scene, ICollection<Transform> moving = null)
        {
            Clear();
            // MonoBehaviour 필드 초기화 중에는 Unity API를 부를 수 없어 여기서 채운다
            if (_muscleIndex.Count == 0) for (int i = 0; i < HumanTrait.MuscleCount; i++) _muscleIndex[HumanTrait.MuscleName[i]] = i;
            var map = scene.Scene?.Humanoid;
            if (map == null || map.Count == 0) { Problem = "휴머노이드 매핑 없음"; return; }

            // 휴머노이드 본과 그 조상 노드만 대리 뼈대로 복제한다 (이름은 노드 번호로 겹치지 않게)
            _proxyRoot = new GameObject("HumanPoseProxy");
            _proxyRoot.hideFlags = HideFlags.HideAndDontSave;
            var proxies = new Dictionary<int, Transform>();
            var skeleton = new List<SkeletonBone> { Skel(_proxyRoot.transform) };
            Transform ProxyOf(int id)
            {
                if (proxies.TryGetValue(id, out var p)) return p;
                var real = scene.NodeTransform(id);
                if (real == null) return null;
                int parent = scene.Scene.Nodes[id].Parent;
                var parentProxy = parent >= 0 ? ProxyOf(parent) : _proxyRoot.transform;
                if (parentProxy == null) return null;
                var go = new GameObject("b" + id);
                go.transform.SetParent(parentProxy, false);
                go.transform.localPosition = real.localPosition; go.transform.localRotation = real.localRotation; go.transform.localScale = real.localScale;
                proxies[id] = go.transform;
                skeleton.Add(Skel(go.transform));
                _proxyRest.Add((go.transform, go.transform.localPosition, go.transform.localRotation));
                return go.transform;
            }

            var human = new List<HumanBone>();
            var boneNames = new HashSet<string>(HumanTrait.BoneName);
            var settings = scene.Scene.HumanoidSettings;
            foreach (var kv in map)
            {
                if (!boneNames.Contains(kv.Key)) continue;
                var proxy = ProxyOf(kv.Value);
                if (proxy == null) continue;
                var hb = new HumanBone { humanName = kv.Key, boneName = proxy.name };
                if (settings.Limits.TryGetValue(kv.Key, out var lim) && lim.Min.Length == 3 && lim.Max.Length == 3 && lim.Center.Length == 3)
                {
                    // 원본 .meta에서 바꾼 근육 범위 (머슬 −1~1이 이 각도 범위로)
                    hb.limit.useDefaultValues = false;
                    hb.limit.min = new Vector3(lim.Min[0], lim.Min[1], lim.Min[2]);
                    hb.limit.max = new Vector3(lim.Max[0], lim.Max[1], lim.Max[2]);
                    hb.limit.center = new Vector3(lim.Center[0], lim.Center[1], lim.Center[2]);
                    hb.limit.axisLength = lim.AxisLength;
                }
                else hb.limit.useDefaultValues = true;
                human.Add(hb);
                var real = scene.NodeTransform(kv.Value);
                var b = new Bone { Human = kv.Key, Real = real, Proxy = proxy, RestPos = real.localPosition, RestRot = real.localRotation };
                _bones.Add(b);
                if (kv.Key == "Hips") _hips = b;
            }

            LinkBones();
            // 바꾼 근육 범위에 뼈 길이가 없으면(0) 휴머노이드 자식 본까지 거리로 (Unity 아바타 설정 창이 채우는 값과 같은 뜻)
            for (int h = 0; h < human.Count; h++)
            {
                var hb = human[h];
                if (hb.limit.useDefaultValues || hb.limit.axisLength > 1e-5f) continue;
                int bi = _bones.FindIndex(b => b.Human == hb.humanName);
                if (bi < 0 || _bones[bi].Child < 0) continue;
                hb.limit.axisLength = Vector3.Distance(_bones[bi].Real.position, _bones[_bones[bi].Child].Real.position);
                human[h] = hb;
            }

            // 원본 .meta humanDescription 값 (IR humanoidSettings, 없으면 Unity 모델 임포트 기본값)
            var desc = new HumanDescription
            {
                human = human.ToArray(), skeleton = skeleton.ToArray(),
                upperArmTwist = settings.ArmTwist, lowerArmTwist = settings.ForeArmTwist, upperLegTwist = settings.UpperLegTwist, lowerLegTwist = settings.LegTwist,
                armStretch = settings.ArmStretch, legStretch = settings.LegStretch, feetSpacing = settings.FeetSpacing, hasTranslationDoF = settings.HasTranslationDoF,
            };
            if (settings.Limits.Count > 0) Debug.Log($"[VrcProto] 원본 근육 범위 {settings.Limits.Count}개: {string.Join(", ", settings.Limits.Keys)}");
            _avatar = AvatarBuilder.BuildHumanAvatar(_proxyRoot, desc);
            if (_avatar == null || !_avatar.isValid || !_avatar.isHuman)
            {
                Problem = "휴머노이드 Avatar를 만들지 못함 (필수 본 부족)";
                Debug.LogWarning("[VrcProto] " + Problem);
                if (_avatar != null) Object.Destroy(_avatar);
                _avatar = null;
                return;
            }
            _avatar.name = "HumanPoseProxy";
            _handler = new HumanPoseHandler(_avatar, _proxyRoot.transform);
            _rest = new HumanPose();
            _handler.GetHumanPose(ref _rest);
            _height = _rest.bodyPosition.y; _bodyRot = _rest.bodyRotation;
            _muscles = (float[])_rest.muscles.Clone(); _readBack = (float[])_rest.muscles.Clone();
            MeasureHumanScale();
            var fitBones = new List<Transform>(); foreach (var b in _bones) fitBones.Add(b.Real);
            _floor.Build(scene, fitBones, moving);
            Debug.Log($"[VrcProto] 휴머노이드 자세 준비: 본 {human.Count}, 쉬는 자세 몸 위치 {_rest.bodyPosition}");
        }

        static SkeletonBone Skel(Transform t) =>
            new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale };

        /// <summary>쉬는 자세(불러온 그대로)의 머슬 값. Host 슬라이더의 시작 값.</summary>
        public JNode RestMuscles()
        {
            var o = JNode.Obj();
            if (!Ready) return o;
            for (int i = 0; i < _rest.muscles.Length; i++) o.Set(HumanTrait.MuscleName[i], Mathf.Round(_rest.muscles[i] * 10000f) / 10000f);
            return o;
        }

        /// <summary>
        /// pose = null → 불러온 그대로. 아니면 { muscles: { 이름: 값 }, bodyRotation?: [x,y,z,w], bodyHeight?: 숫자 }.
        /// 적히지 않은 머슬은 0 (Unity 애니메이션에서 커브가 없는 머슬과 같다). 몸 위치·방향은 쉬는 자세를 쓰고,
        /// bodyRotation(.anim의 RootQ)·bodyHeight(RootT.y)가 있으면 그것으로 바꾼다.
        /// </summary>
        public void Apply(JNode pose)
        {
            if (!Ready) return;
            _lastPose = pose;
            Unknown.Clear();
            _height = _rest.bodyPosition.y; _bodyRot = _rest.bodyRotation;
            if (pose == null || pose.IsNull || !pose.Has("muscles"))
            {
                foreach (var b in _bones) { b.Real.localRotation = b.RestRot; b.Real.localPosition = b.RestPos; }
                _muscles = (float[])_rest.muscles.Clone(); _readBack = (float[])_rest.muscles.Clone();
                return;
            }
            foreach (var (t, p, r) in _proxyRest) { t.localPosition = p; t.localRotation = r; }
            var hp = new HumanPose { bodyPosition = _rest.bodyPosition, bodyRotation = _rest.bodyRotation, muscles = new float[HumanTrait.MuscleCount] };
            foreach (var kv in pose["muscles"].Props)
            {
                if (_muscleIndex.TryGetValue(kv.Key, out var i)) hp.muscles[i] = kv.Value.AsFloat;
                else Unknown.Add(kv.Key);
            }
            if (pose.Has("bodyRotation"))
            {
                var q = pose["bodyRotation"].ToFloatArray();
                if (q.Length == 4) hp.bodyRotation = _bodyRot = new Quaternion(q[0], q[1], q[2], q[3]).normalized;
            }
            if (pose.Has("bodyHeight")) hp.bodyPosition.y = _height = pose["bodyHeight"].AsFloat;
            _handler.SetHumanPose(ref hp);
            foreach (var b in _bones) b.Real.localRotation = b.Proxy.localRotation;
            if (_hips != null) _hips.Real.localPosition = _hips.Proxy.localPosition;
            FitFloor();
            _muscles = (float[])hp.muscles.Clone(); _readBack = ReadMuscles().muscles;
            if (Unknown.Count > 0) Debug.LogWarning("[VrcProto] 모르는 머슬 이름: " + string.Join(", ", Unknown));
        }

        HumanPose _fingerPose;
        readonly List<Bone> _fingerBones = new List<Bone>();

        /// <summary>쉬는 자세(불러온 그대로)의 머슬 값 배열 (HumanTrait 순서). 바꾸지 말 것.</summary>
        public float[] RestMuscleArray => _rest.muscles;

        /// <summary>VR 손 (VrHands): 머슬 값(HumanTrait 순서) 중 손가락 것만 손가락 본에 적용한다 — 몸·팔 본(IK가 놓은 것)은 건드리지 않는다.
        /// 손가락 머슬은 손 기준 손가락 회전만 정하므로 쉬는 몸 자세로 풀어 손가락 본의 로컬 회전만 옮긴다. 매 프레임 불러도 할당이 없다.</summary>
        public void ApplyFingers(float[] muscles)
        {
            if (!Ready) return;
            if (_fingerBones.Count == 0)
                foreach (var b in _bones)
                    if (b.Human.Contains("Thumb") || b.Human.Contains("Index") || b.Human.Contains("Middle") || b.Human.Contains("Ring") || b.Human.Contains("Little")) _fingerBones.Add(b);
            foreach (var (t, p, r) in _proxyRest) { t.localPosition = p; t.localRotation = r; }
            _fingerPose.bodyPosition = _rest.bodyPosition; _fingerPose.bodyRotation = _rest.bodyRotation;
            _fingerPose.muscles = muscles;
            _handler.SetHumanPose(ref _fingerPose);
            foreach (var b in _fingerBones) b.Real.localRotation = b.Proxy.localRotation;
        }

        HumanPose _read;

        /// <summary>휴머노이드 크기 (Animator.humanScale과 같은 뜻 — bodyPosition·RootT는 m를 이 값으로 나눈 것). Build에서 잰다.</summary>
        public float HumanScale { get; private set; } = 1;

        /// <summary>대리 뼈대의 Hips를 0.1 m 올려 bodyPosition이 얼마나 바뀌는지로 잰다 (Avatar에는 humanScale이 없다).</summary>
        void MeasureHumanScale()
        {
            HumanScale = 1;
            if (_hips == null) return;
            var p = _hips.Proxy.position;
            _hips.Proxy.position = p + Vector3.up * 0.1f;
            var hp = new HumanPose();
            _handler.GetHumanPose(ref hp);
            _hips.Proxy.position = p;
            float dy = hp.bodyPosition.y - _rest.bodyPosition.y;
            if (dy > 1e-5f) HumanScale = 0.1f / dy;
        }

        /// <summary>VR 자세 캡처·모션 녹화 (VrPoseCapture): 실제 본의 지금 회전(IK·FBT·손가락이 놓은 것)을 머슬 값으로 읽는다.
        /// 몸 방향·위치도 본에서 읽고(아바타 루트 기준), 화면·기록된 자세(_muscles)는 바꾸지 않는다. muscles는 HumanTrait 순서로 채운다.</summary>
        public bool ReadBones(float[] muscles, out Vector3 bodyPos, out Quaternion bodyRot)
        {
            bodyPos = Vector3.zero; bodyRot = Quaternion.identity;
            if (!Ready) return false;
            foreach (var (t, p, r) in _proxyRest) { t.localPosition = p; t.localRotation = r; }
            foreach (var b in _bones) b.Proxy.localRotation = b.Real.localRotation;
            if (_hips != null) _hips.Proxy.localPosition = _hips.Real.localPosition;
            if (_read.muscles == null) _read.muscles = new float[HumanTrait.MuscleCount];
            _handler.GetHumanPose(ref _read);
            System.Array.Copy(_read.muscles, muscles, Mathf.Min(muscles.Length, _read.muscles.Length));
            bodyPos = _read.bodyPosition; bodyRot = _read.bodyRotation.normalized;
            return true;
        }

        /// <summary>ReadBones 결과를 setPose 형식으로 { muscles, bodyRotation, bodyHeight } (몸 가로 위치는 쓰지 않는다 — Apply와 같게).</summary>
        public static JNode PoseJson(float[] muscles, Vector3 bodyPos, Quaternion bodyRot)
        {
            var m = JNode.Obj();
            for (int i = 0; i < muscles.Length && i < HumanTrait.MuscleCount; i++) m.Set(HumanTrait.MuscleName[i], Mathf.Round(muscles[i] * 10000f) / 10000f);
            return JNode.Obj().Set("muscles", m).Set("bodyRotation", JNode.FloatArray(new[] { bodyRot.x, bodyRot.y, bodyRot.z, bodyRot.w }))
                .Set("bodyHeight", Mathf.Round(bodyPos.y * 10000f) / 10000f);
        }

        // ---------------- 뷰포트 본 조작 (BonePoser) ----------------
        // 실제 본을 돌린 결과를 대리 뼈대로 옮겨 머슬 값으로 읽고(GetHumanPose), 그 머슬 값을 다시 적용해
        // 화면이 Host에 기록될 자세와 같게 한다 (머슬로 나타낼 수 없는 회전 — 팔꿈치 옆으로 꺾기 등 — 은 빠진다).

        public int BoneCount => _bones.Count;
        public string BoneName(int i) => _bones[i].Human;
        public Transform BoneTransform(int i) => _bones[i].Real;
        /// <summary>휴머노이드 부모 본 (Hips는 -1).</summary>
        public int BoneParent(int i) => _bones[i].Parent;
        /// <summary>본이 향하는 휴머노이드 자식 (끝 본은 -1).</summary>
        public int BoneChild(int i) => _bones[i].Child;
        public int FindBone(string human) => _bones.FindIndex(b => b.Human == human);

        void LinkBones()
        {
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < _bones.Count; i++) index[_bones[i].Real] = i;
            for (int i = 0; i < _bones.Count; i++)
                for (var p = _bones[i].Real.parent; p != null; p = p.parent)
                    if (index.TryGetValue(p, out var pi)) { _bones[i].Parent = pi; break; }
            // 자식이 여럿이면 이름으로 잇는 쪽 (Hips → Spine, Chest → UpperChest·Neck, 손 → 가운뎃손가락)
            string[] prefer = { "Spine", "Chest", "UpperChest", "Neck", "Head", "LowerArm", "Hand", "LowerLeg", "Foot", "Toes", "MiddleProximal", "Intermediate", "Distal" };
            for (int i = 0; i < _bones.Count; i++)
            {
                int best = -1, bestRank = int.MaxValue;
                for (int c = 0; c < _bones.Count; c++)
                {
                    if (_bones[c].Parent != i) continue;
                    int rank = System.Array.FindIndex(prefer, x => _bones[c].Human.EndsWith(x));
                    if (rank < 0) rank = prefer.Length;
                    if (rank < bestRank) { bestRank = rank; best = c; }
                }
                _bones[i].Child = best;
            }
        }

        HumanPose ReadMuscles(bool bodyFromBones = false)
        {
            foreach (var (t, p, r) in _proxyRest) { t.localPosition = p; t.localRotation = r; }
            foreach (var b in _bones) b.Proxy.localRotation = b.Real.localRotation;
            if (_hips != null) _hips.Proxy.localPosition = _hips.Real.localPosition;
            var hp = new HumanPose();
            _handler.GetHumanPose(ref hp);
            // 몸 위치는 Apply와 같게: 가로는 쉬는 자세, 높이는 지금 자세 (Hips를 움직였으면 높이도 본에서 — VR 잡기로 엉덩이를 내려 앉기)
            if (bodyFromBones) { _bodyRot = hp.bodyRotation.normalized; _height = hp.bodyPosition.y; } else hp.bodyRotation = _bodyRot;
            hp.bodyPosition = new Vector3(_rest.bodyPosition.x, _height, _rest.bodyPosition.z);
            return hp;
        }

        /// <summary>실제 본의 지금 회전을 머슬 값으로 읽어 다시 적용한다 (끄는 동안 매 프레임). bodyFromBones = Hips를 돌렸으면 몸 방향도 본에서 읽는다.
        /// keepHips = Hips 자리·방향은 지금 그대로 둔다 (VR 잡기: 몸 위치·방향을 다시 계산해 Hips가 옮겨지면 손이 잡은 본이 또 끌려가
        /// 매 프레임 몸 전체가 떨렸다). 바닥 맞추기도 건너뛴다 — 놓을 때 keepHips 없이 한 번 더 불러 기록될 자세로 맞춘다.</summary>
        public void Reproject(bool bodyFromBones = false, bool keepHips = false)
        {
            if (!Ready) return;
            keepHips &= _hips != null;
            Vector3 hipsPos = keepHips ? _hips.Real.position : default;
            Quaternion hipsRot = keepHips ? _hips.Real.rotation : default;
            var hp = ReadMuscles(bodyFromBones);
            for (int i = 0; i < hp.muscles.Length; i++)
            {
                float d = hp.muscles[i] - _readBack[i];
                hp.muscles[i] = Mathf.Abs(d) < 1e-5f ? _muscles[i] : Mathf.Clamp(_muscles[i] + d, -1, 1); // Host 슬라이더 범위
            }
            _handler.SetHumanPose(ref hp);
            foreach (var b in _bones) b.Real.localRotation = b.Proxy.localRotation;
            if (_hips != null) _hips.Real.localPosition = _hips.Proxy.localPosition;
            if (keepHips) _hips.Real.SetPositionAndRotation(hipsPos, hipsRot);
            else FitFloor();
            _muscles = (float[])hp.muscles.Clone(); _readBack = ReadMuscles().muscles;
        }

        // ---------------- 바닥 맞추기 (FloorFit) ----------------
        /// <summary>마지막 자세를 다시 적용한다 (VR 아바타에서 나올 때 — 손가락 등 VR이 바꾼 본을 자세 페이지 자세로).</summary>
        public void Reapply() { if (Ready) Apply(_lastPose); }

        /// <summary>켜고 끄면 지금 자세를 다시 적용한다.</summary>
        public void SetFloorFit(bool on)
        {
            if (_floorFit == on) return;
            _floorFit = on;
            if (Ready) Apply(_lastPose);
        }

        /// <summary>가장 낮은 곳이 불러온 자세의 가장 낮은 높이에 오도록 Hips를 위아래로 옮긴다 (몸 위치는 머슬로 다시 읽을 때 쉬는 자세 값으로 덮이므로 기록에 섞이지 않는다).</summary>
        void FitFloor()
        {
            if (!_floorFit || _hips == null || !_floor.Ready) return;
            if (_floor.Lowest(out var rest, out var now)) _hips.Real.position += (_floor.Root != null ? _floor.Root.up * _floor.Root.lossyScale.y : Vector3.up) * (rest - now);
        }

        /// <summary>시험용: (불러온 자세의 가장 낮은 높이, 지금 가장 낮은 높이).</summary>
        public (float rest, float now, string which) FloorHeights() => _floor.Lowest(out var r, out var n, out var w) ? (r, n, w) : (float.NaN, float.NaN, "");

        /// <summary>지금 화면의 자세를 setPose 형식으로 { muscles, bodyRotation?, bodyHeight? }.</summary>
        public JNode CurrentPose()
        {
            var o = JNode.Obj();
            if (!Ready) return o;
            var m = JNode.Obj();
            for (int i = 0; i < _muscles.Length; i++) m.Set(HumanTrait.MuscleName[i], Mathf.Round(_muscles[i] * 10000f) / 10000f);
            o.Set("muscles", m);
            if (Quaternion.Angle(_bodyRot, _rest.bodyRotation) > 0.01f)
            {
                var q = _bodyRot;
                o.Set("bodyRotation", JNode.FloatArray(new[] { q.x, q.y, q.z, q.w }));
            }
            if (Mathf.Abs(_height - _rest.bodyPosition.y) > 1e-4f) o.Set("bodyHeight", _height);
            return o;
        }
    }
}
