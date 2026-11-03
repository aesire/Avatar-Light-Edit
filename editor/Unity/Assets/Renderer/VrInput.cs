// VR 컨트롤러 입력 (PLAN 3.12): SteamVR Input(IVRInput) 액션으로 읽는다 — VRChat처럼 버튼은 SteamVR 바인딩 창에서 바꾼다.
// 액션·기본 바인딩: StreamingAssets/SteamVR/actions.json (Oculus Touch · PICO · Index · Vive). 기본 배치는 VRChat과 같다:
//   왼쪽 스틱 이동 · 오른쪽 스틱 회전 · A 점프 · B/Y 메뉴(짧게 빠른 메뉴, 길게 액션 메뉴 — 3단계) · 트리거 사용 · 그립 잡기.
// 손 (VrHands, VRChat 퀘스트 컨트롤러처럼): 정전식 터치 두 가지 + 그립 — 엄지 터치(A/B/X/Y·스틱·엄지 받침) · 검지 터치(트리거) · 그립 당김.
//   터치 센서가 없는 컨트롤러(Vive 트리거 등)는 트리거를 조금이라도 당기면 검지 터치, 엄지 터치 바인딩이 없으면 엄지는 늘 닿은 것으로.
//   스켈레탈 입력(skeleton_left/right)은 손목 자리(Wrist·HandFrame)에만 쓴다 — 손가락 굽힘은 쓰지 않는다.
// 레이저 (3단계 VR 에디터 창): "aim" 자세 액션 — 기본 바인딩은 컨트롤러 끝(/pose/tip), 트리거 당김 값은 손마다 Trigger.
// 장면 앱으로 붙었을 때만 쓸 수 있다 (백그라운드 앱은 입력을 받지 않는다).
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace VrcProto.Viewer
{
    public sealed class VrInput
    {
        public Vector2 Move, Turn;
        public bool Jump, JumpDown, Menu, MenuDown, MenuUp;
        public readonly bool[] Use = new bool[2], UseDown = new bool[2], Grab = new bool[2], GrabDown = new bool[2];   // 0 왼손 · 1 오른손
        /// <summary>손마다 메뉴 단추(Y 왼손 · B 오른손) — 손에 붙는 빠른 설정을 어느 손에 띄울지.</summary>
        public readonly bool[] MenuH = new bool[2];
        /// <summary>손마다 엄지 터치 · 검지(트리거) 터치 · 그립 당김 0~1 (VrHands 제스처). HasTouch[손] = 터치 바인딩이 있는지 (없으면 트리거·그립으로 대신).</summary>
        public readonly bool[] ThumbTouch = new bool[2], IndexTouch = new bool[2], HasTouch = new bool[2];
        public readonly float[] Grip = new float[2];
        /// <summary>손목 (방 좌표, 실제 m) · 손 틀(앞 = 가운데 손가락 쪽, 위 = 손등) — SteamVR 손 뼈대(컨트롤러를 쥔 손)에서. HasWrist가 false면 VrIk가 컨트롤러 기준 어림값을 쓴다.</summary>
        public readonly Vector3[] Wrist = new Vector3[2];
        public readonly Quaternion[] HandFrame = { Quaternion.identity, Quaternion.identity };
        public readonly bool[] HasWrist = new bool[2];
        /// <summary>레이저 방향 (방 좌표, 실제 m): SteamVR Input "aim" 자세(기본 바인딩 = 컨트롤러 끝 /pose/tip). 바인딩이 없으면 HasAim false → 컨트롤러 자세로 대신.</summary>
        public readonly Vector3[] AimPos = new Vector3[2];
        public readonly Quaternion[] AimRot = { Quaternion.identity, Quaternion.identity };
        public readonly bool[] HasAim = new bool[2];
        /// <summary>트리거 당김 0~1 (손마다).</summary>
        public readonly float[] Trigger = new float[2];
        public bool Ready { get; private set; }
        public string Problem { get; private set; } = "";

        ulong _set, _move, _turn, _jump, _menu, _use, _grab, _triggerValue, _gripValue, _aim, _thumbTouch, _indexTouch;
        readonly ulong[] _skeleton = new ulong[2];
        readonly ulong[] _hand = new ulong[2];
        readonly VRActiveActionSet_t[] _sets = new VRActiveActionSet_t[1];
        static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        static readonly uint SetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));

        /// <summary>장면 앱으로 붙은 직후 한 번. 실패해도 VR로 보기는 되고 조작만 안 된다 (Problem에 이유).</summary>
        public void Init()
        {
            Ready = false;
            var input = OpenVR.Input;
            if (input == null) { Problem = "SteamVR 입력을 열지 못했어요"; return; }
            var path = Path.Combine(Application.streamingAssetsPath, "SteamVR", "actions.json");
            if (!File.Exists(path)) { Problem = "actions.json이 없어요: " + path; return; }
            var err = input.SetActionManifestPath(path);
            if (err != EVRInputError.None) { Problem = "SteamVR 입력 설정 실패: " + err; return; }
            input.GetActionSetHandle("/actions/main", ref _set);
            input.GetActionHandle("/actions/main/in/move", ref _move);
            input.GetActionHandle("/actions/main/in/turn", ref _turn);
            input.GetActionHandle("/actions/main/in/jump", ref _jump);
            input.GetActionHandle("/actions/main/in/menu", ref _menu);
            input.GetActionHandle("/actions/main/in/use", ref _use);
            input.GetActionHandle("/actions/main/in/grab", ref _grab);
            input.GetActionHandle("/actions/main/in/trigger_value", ref _triggerValue);
            input.GetActionHandle("/actions/main/in/grip_value", ref _gripValue);
            input.GetActionHandle("/actions/main/in/aim", ref _aim);
            input.GetActionHandle("/actions/main/in/thumb_touch", ref _thumbTouch);
            input.GetActionHandle("/actions/main/in/index_touch", ref _indexTouch);
            input.GetActionHandle("/actions/main/in/skeleton_left", ref _skeleton[0]);
            input.GetActionHandle("/actions/main/in/skeleton_right", ref _skeleton[1]);
            input.GetInputSourceHandle("/user/hand/left", ref _hand[0]);
            input.GetInputSourceHandle("/user/hand/right", ref _hand[1]);
            _sets[0] = new VRActiveActionSet_t { ulActionSet = _set, nPriority = 0 };
            Problem = "";
            Ready = true;
        }

        public void Update()
        {
            if (!Ready) return;
            var input = OpenVR.Input;
            if (input == null || input.UpdateActionState(_sets, SetSize) != EVRInputError.None) { Clear(); return; }
            Move = Analog(input, _move, 0);
            Turn = Analog(input, _turn, 0);
            Digital(input, _jump, 0, out Jump, out JumpDown, out _);
            Digital(input, _menu, 0, out Menu, out MenuDown, out MenuUp);
            for (int h = 0; h < 2; h++)
            {
                Digital(input, _use, _hand[h], out Use[h], out UseDown[h], out _);
                Digital(input, _grab, _hand[h], out Grab[h], out GrabDown[h], out _);
                Digital(input, _menu, _hand[h], out MenuH[h], out _, out _);
                Trigger[h] = Mathf.Max(Analog(input, _triggerValue, _hand[h]).x, Use[h] ? 1 : 0);
                Touches(input, h);
                WristOf(input, h);
                AimOf(input, h);
            }
        }

        readonly VRBoneTransform_t[] _bones = new VRBoneTransform_t[31];
        InputPoseActionData_t _pose;
        static readonly uint PoseSize = (uint)Marshal.SizeOf(typeof(InputPoseActionData_t));
        // SteamVR 손 뼈대 번호: 1 손목 · 7 검지 첫 마디 · 12 중지 첫 마디 · 22 새끼 첫 마디
        const int BWrist = 1, BIndex1 = 7, BMiddle1 = 12, BPinky1 = 22;

        /// <summary>손 뼈대(컨트롤러를 쥔 손, 모델 공간)를 스켈레톤 액션 자세로 방 좌표에 옮긴다. 손목 = 1번 뼈, 손가락 방향 = 손목 → 중지, 엄지 쪽 = 새끼 → 검지.</summary>
        void WristOf(CVRInput input, int h)
        {
            HasWrist[h] = false;
            if (input.GetPoseActionDataForNextFrame(_skeleton[h], ETrackingUniverseOrigin.TrackingUniverseStanding, ref _pose, PoseSize, 0) != EVRInputError.None
                || !_pose.bActive || !_pose.pose.bPoseIsValid) return;
            if (input.GetSkeletalBoneData(_skeleton[h], EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange.WithController, _bones) != EVRInputError.None) return;
            VrLink.ToUnity(_pose.pose.mDeviceToAbsoluteTracking, out var pos, out var rot);
            Vector3 P(int i) { var v = _bones[i].position; return new Vector3(v.v0, v.v1, -v.v2); }   // 오른손 좌표 → Unity
            var wrist = P(BWrist);
            var f = P(BMiddle1) - wrist;
            var thumb = P(BIndex1) - P(BPinky1);
            if (f.sqrMagnitude < 1e-6f || thumb.sqrMagnitude < 1e-6f) return;
            // 손등: 오른손은 -(손가락 × 엄지 쪽), 왼손은 +
            var back = Vector3.Cross(f, thumb) * (h == 0 ? 1 : -1);
            if (back.sqrMagnitude < 1e-8f) return;
            Wrist[h] = pos + rot * wrist;
            HandFrame[h] = rot * Quaternion.LookRotation(f, back);
            HasWrist[h] = true;
        }

        void AimOf(CVRInput input, int h)
        {
            HasAim[h] = false;
            if (_aim == 0 || input.GetPoseActionDataForNextFrame(_aim, ETrackingUniverseOrigin.TrackingUniverseStanding, ref _pose, PoseSize, _hand[h]) != EVRInputError.None
                || !_pose.bActive || !_pose.pose.bPoseIsValid) return;
            VrLink.ToUnity(_pose.pose.mDeviceToAbsoluteTracking, out AimPos[h], out AimRot[h]);
            HasAim[h] = true;
        }

        void Touches(CVRInput input, int h)
        {
            Grip[h] = Mathf.Max(Analog(input, _gripValue, _hand[h]).x, Grab[h] ? 1 : 0);
            bool thumbBound = Bound(input, _thumbTouch, _hand[h], out var thumb), indexBound = Bound(input, _indexTouch, _hand[h], out var index);
            ThumbTouch[h] = !thumbBound || thumb;
            IndexTouch[h] = index || Trigger[h] > 0.05f;
            HasTouch[h] = thumbBound || indexBound;
        }

        /// <summary>디지털 액션이 이 손에 바인딩되어 있는지(bActive)와 지금 값.</summary>
        static bool Bound(CVRInput input, ulong action, ulong device, out bool held)
        {
            var d = new InputDigitalActionData_t();
            bool ok = action != 0 && input.GetDigitalActionData(action, ref d, DigitalSize, device) == EVRInputError.None && d.bActive;
            held = ok && d.bState;
            return ok;
        }

        void Clear()
        {
            Move = Turn = Vector2.zero;
            Jump = JumpDown = Menu = MenuDown = MenuUp = false;
            for (int h = 0; h < 2; h++) { Use[h] = UseDown[h] = Grab[h] = GrabDown[h] = HasAim[h] = MenuH[h] = ThumbTouch[h] = IndexTouch[h] = HasTouch[h] = false; Trigger[h] = Grip[h] = 0; }
        }

        static Vector2 Analog(CVRInput input, ulong action, ulong device)
        {
            var d = new InputAnalogActionData_t();
            return input.GetAnalogActionData(action, ref d, AnalogSize, device) == EVRInputError.None && d.bActive ? new Vector2(d.x, d.y) : Vector2.zero;
        }

        static void Digital(CVRInput input, ulong action, ulong device, out bool held, out bool down, out bool up)
        {
            var d = new InputDigitalActionData_t();
            bool ok = input.GetDigitalActionData(action, ref d, DigitalSize, device) == EVRInputError.None && d.bActive;
            held = ok && d.bState;
            down = ok && d.bState && d.bChanged;
            up = ok && !d.bState && d.bChanged;
        }
    }
}
