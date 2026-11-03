// SteamVR 연결 (PLAN 3.12): OpenVR(openvr_api.dll)로 SteamVR에 붙어 장치 목록과 포즈를 읽는다.
// 연결만 켰을 때는 백그라운드 앱(VRApplication_Background) — 헤드셋에 화면을 보내지 않고, SteamVR을 대신 켜지도 않는다.
// "VR로 보기"(View)를 켜면 장면 앱(VRApplication_Scene)으로 다시 붙는다 — SteamVR이 꺼져 있으면 vrstartup을 띄우고(멈추지 않게
// 백그라운드로 켜졌는지 보다가) 켜지면 붙는다. 그리기·제출은 VrView, 여기서는 연결·장치·포즈(WaitGetPoses)를 맡는다.
// 켜 둔 동안(Wanted) SteamVR이 꺼져 있거나 도중에 꺼지면 몇 초마다 다시 붙어 본다. 상태가 바뀔 때마다 Host로 vrStatus.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Valve.VR;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class VrLink
    {
        public Action<JNode> Send = _ => { };
        /// <summary>Host가 "다시 맞추기"를 눌렀을 때 (VrView가 관찰자 자리를 다시 잡는다).</summary>
        public Action Recenter = () => { };
        /// <summary>VR로 보기의 PC 화면: eye(내 눈, VRChat처럼) | orbit(지금 미리보기 카메라).</summary>
        public string Desktop { get; private set; } = "eye";
        /// <summary>VrView가 1초마다 넣는 제출 프레임 수 (상태에 보인다).</summary>
        public int Fps;
        public string SubmitError = "";
        /// <summary>눈 텍스처 크기 = SteamVR 권장 × Resolution (0.5~1.5).</summary>
        public float Resolution = 1;
        /// <summary>부드러운 회전 (끄면 스냅 30°).</summary>
        public bool SmoothTurn;
        /// <summary>VrView가 넣는다: 1초 평균 GPU ms · 재투영 % · 놓친 프레임.</summary>
        public float GpuMs;
        /// <summary>VrView가 넣는다: 입력 문제.</summary>
        public string InputProblem = "";
        public int Reprojected, Dropped;
        /// <summary>거울 (VrMirror): 월드 거울 · 개인 거울 · 반사 화질(눈 텍스처 배율 0.25~1). MirrorSummon은 "개인 거울 내 앞으로" (VrView가 읽고 끈다).</summary>
        public bool Mirror, PersonalMirror, MirrorSummon;
        public float MirrorQuality = 0.75f;
        /// <summary>VrView가 넣는다: 켜진 거울 수.</summary>
        public int Mirrors;
        /// <summary>VrView가 넣는다: 헤드셋에 보이는 에디터 창 수 · 창 문제 (VrPanels).</summary>
        public int Panels;
        public string PanelProblem = "";
        /// <summary>VR 카메라 (VrCamera): 켜기 · 내 앞으로(VrView가 읽고 비운다) · 사진 폴더 · 사진 크기.</summary>
        public bool CameraOn, CameraSummon;
        public string CameraFolder = "", CameraTest = "";   // CameraTest: 시험용 폴더 (VrView가 읽고 비운다)
        public int CameraWidth = 1920, CameraHeight = 1080;
        /// <summary>VR 카메라 조작판 값 (cameraOpts, 받은 것만 바꿈) · 셔터(조작판 단추, VrCamera가 읽고 비운다) · 영상 녹화 파이프(Host가 열어 둔 것, 한 번 쓰면 비운다) · 줌을 Host가 바꿈(-1 = 없음).</summary>
        public bool CameraVideo, CameraLock, CameraFlip, CameraLookAt, CameraGrid, CameraPortrait, CameraStream, CameraShutter;
        public int CameraTimer, CameraSmooth, CameraFps = 30;
        public string CameraAnchor = "room", CameraPipe = "";
        public float CameraFov = -1, CameraScale = 1;
        /// <summary>본 잡기 (VrBoneGrab): 관찰자일 때 그립으로 관절을 잡아 옮기기.</summary>
        public bool BoneGrab = true;
        /// <summary>본 잡기 설정 (Avatar Pose System 참고, 2026-10-25): 다른 관절은 제자리 · 관절 잠금(아바타째만) · 손잡이 크기 배율.</summary>
        public bool BonePinOthers = true, BoneLock;
        public float BoneHandle = 1;
        /// <summary>손 공 (VrControllers): 컨트롤러 끝(잡는 점)에 공.</summary>
        public bool ShowControllers = true;
        /// <summary>아이템 잡아 배치 (VrItemGrab, 2026-10-27): 관찰자일 때 붙인 악세서리를 그립으로.</summary>
        public bool ItemGrab = true;
        /// <summary>아이템 배치 도움 (2026-11-01): 스냅 · 놓을 때 표면에 붙이기 · 짝 아이템 좌우 대칭.</summary>
        public bool ItemSnap, ItemSurface, ItemMirror;
        /// <summary>VR 카메라 영상에 마이크 소리 (2026-11-01) · 쓸 마이크 이름 ("" = 첫 마이크).</summary>
        public bool CameraMic;
        public string CameraMicName = "";
        /// <summary>VR 카메라 (2026-10-27): 피사계 심도 세기(0 = 끔) · 투명 배경 · 자리 저장 칸(-1 = 없음, VrCamera가 읽고 비운다) · 불러올 자리.</summary>
        public float CameraDof;
        public bool CameraTransparent;
        public int CameraBookmark = -1;
        public float[] CameraGoto;

        /// <summary>장치 하나 (OpenVR 장치 번호 기준). 포즈는 방 공간(OpenVR 서 있는 원점)을 Unity 좌표(왼손, 미터)로 — 방 바닥 가운데가 원점.</summary>
        public sealed class Device
        {
            public uint Index;
            public string Kind = "", Role = "", Model = "", Maker = "", Type = "", Serial = "";
            public bool Tracked;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
        }

        public bool Wanted { get; private set; }
        /// <summary>VR로 보기를 원함 (장면 앱으로 붙기).</summary>
        public bool WantView { get; private set; }
        /// <summary>장면 앱으로 붙어 있음 — VrView가 그리고 제출해도 된다.</summary>
        public bool IsScene => _system != null && _scene;
        public CVRSystem VrSystem => _system;
        public CVRCompositor Compositor { get; private set; }
        public string State { get; private set; } = "off";   // off | waiting | ok | error
        public string Text { get; private set; } = "";
        public readonly List<Device> Devices = new List<Device>();
        public Device Hmd { get; private set; }

        CVRSystem _system;
        bool _scene, _launched, _noLaunch;
        float _retryAt, _reportAt;
        string _lastSent = "";
        readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        readonly TrackedDevicePose_t[] _gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        readonly StringBuilder _sb = new StringBuilder(256);
        const float RetrySeconds = 3f;

        /// <summary>vr { enabled?, view?, launch?, resolution?, desktop?, smoothTurn?, mirror?, personalMirror?, mirrorQuality?, mirrorSummon?, recenter?, status?, camera…, bone…, item… }</summary>
        public void Handle(JNode m)
        {
            if (m.Has("enabled"))
            {
                Wanted = m["enabled"].AsBool;
                if (!Wanted) WantView = false;
            }
            if (m.Has("view"))
            {
                WantView = m["view"].AsBool;
                if (WantView) Wanted = true;
                _launched = false;
                _noLaunch = m.Has("launch") && !m["launch"].AsBool;   // 시험용: SteamVR을 켜지 않는다
            }
            if (m.Has("resolution")) Resolution = Mathf.Clamp(m["resolution"].AsFloat, 0.5f, 1.5f);
            if (m.Has("desktop")) Desktop = m["desktop"].AsString == "orbit" ? "orbit" : "eye";
            if (m.Has("smoothTurn")) SmoothTurn = m["smoothTurn"].AsBool;
            if (m.Has("mirror")) Mirror = m["mirror"].AsBool;
            if (m.Has("personalMirror")) PersonalMirror = m["personalMirror"].AsBool;
            if (m.Has("mirrorQuality")) MirrorQuality = Mathf.Clamp(m["mirrorQuality"].AsFloat, 0.25f, 1f);
            if (m["mirrorSummon"].AsBool) MirrorSummon = true;
            if (m.Has("camera")) CameraOn = m["camera"].AsBool;
            if (m["cameraSummon"].AsBool) CameraSummon = true;
            if (m.Has("cameraTest")) CameraTest = m["cameraTest"].AsString;
            if (m.Has("cameraFolder")) CameraFolder = m["cameraFolder"].AsString;
            if (m.Has("cameraWidth")) CameraWidth = m["cameraWidth"].AsInt;
            if (m.Has("cameraHeight")) CameraHeight = m["cameraHeight"].AsInt;
            if (m["cameraOpts"].Kind == JKind.Object)
            {
                var o = m["cameraOpts"];
                if (o.Has("video")) CameraVideo = o["video"].AsBool;
                if (o.Has("timer")) CameraTimer = Mathf.Clamp(o["timer"].AsInt, 0, 30);
                if (o.Has("scale")) CameraScale = Mathf.Clamp(o["scale"].AsFloat, 0.5f, 3f);
                if (o.Has("lock")) CameraLock = o["lock"].AsBool;
                if (o.Has("flip")) CameraFlip = o["flip"].AsBool;
                if (o.Has("lookAt")) CameraLookAt = o["lookAt"].AsBool;
                if (o.Has("smooth")) CameraSmooth = Mathf.Clamp(o["smooth"].AsInt, 0, 3);
                if (o.Has("anchor")) CameraAnchor = o["anchor"].AsString is "world" or "follow" ? o["anchor"].AsString : "room";
                if (o.Has("grid")) CameraGrid = o["grid"].AsBool;
                if (o.Has("portrait")) CameraPortrait = o["portrait"].AsBool;
                if (o.Has("fps")) CameraFps = o["fps"].AsInt >= 45 ? 60 : 30;
                if (o.Has("stream")) CameraStream = o["stream"].AsBool;
                if (o.Has("mic")) CameraMic = o["mic"].AsBool;
                if (o.Has("micName")) CameraMicName = o["micName"].AsString;
                if (o.Has("fov")) CameraFov = o["fov"].AsFloat;
                if (o.Has("dof")) CameraDof = Mathf.Clamp01(o["dof"].AsFloat);
                if (o.Has("transparent")) CameraTransparent = o["transparent"].AsBool;
                if (o.Has("bookmark")) CameraBookmark = o["bookmark"].AsInt;
            }
            if (m["cameraGoto"].Kind == JKind.Object && m["cameraGoto"]["pos"].Count >= 3 && m["cameraGoto"]["rot"].Count >= 4)
            {
                var g = m["cameraGoto"];
                CameraGoto = new[] { g["pos"][0].AsFloat, g["pos"][1].AsFloat, g["pos"][2].AsFloat, g["rot"][0].AsFloat, g["rot"][1].AsFloat, g["rot"][2].AsFloat, g["rot"][3].AsFloat };
            }
            if (m.Has("itemGrab")) ItemGrab = m["itemGrab"].AsBool;
            if (m.Has("itemSnap")) ItemSnap = m["itemSnap"].AsBool;
            if (m.Has("itemSurface")) ItemSurface = m["itemSurface"].AsBool;
            if (m.Has("itemMirror")) ItemMirror = m["itemMirror"].AsBool;
            if (m["cameraShutter"].AsBool) CameraShutter = true;
            if (m.Has("cameraPipe")) CameraPipe = m["cameraPipe"].AsString;
            if (m.Has("boneGrab")) BoneGrab = m["boneGrab"].AsBool;
            if (m.Has("bonePinOthers")) BonePinOthers = m["bonePinOthers"].AsBool;
            if (m.Has("boneLock")) BoneLock = m["boneLock"].AsBool;
            if (m.Has("boneHandle")) BoneHandle = Mathf.Clamp(m["boneHandle"].AsFloat, 0.2f, 2f);
            if (m.Has("controllers")) ShowControllers = m["controllers"].AsBool;
            if (m.Has("enabled") || m.Has("view"))
            {
                if (!Wanted) { Disconnect(); SetState("off", ""); }
                else if (_system != null && _scene != WantView)
                {
                    // 백그라운드 ↔ 장면 앱은 붙는 방식이 달라 끊고 다시 붙는다
                    Disconnect();
                    _retryAt = 0;
                    SetState("waiting", WantView ? "VR로 보기를 준비하는 중…" : "VR로 보기를 끄는 중…");
                }
                else if (_system == null) { _retryAt = 0; SetState("waiting", "SteamVR에 연결하는 중…"); }
            }
            if (m["recenter"].AsBool && IsScene) Recenter();
            if (m["status"].AsBool) { _lastSent = ""; Report(); }
        }

        public void Update()
        {
            if (!Wanted) return;
            if (_system == null)
            {
                if (Time.realtimeSinceStartup >= _retryAt) TryConnect();
                return;
            }
            PumpEvents();
            if (_system == null) return;
            if (!_scene) ReadPoses();   // 장면 앱은 VrView가 WaitGetPoses로 읽는다
            // 상태 비교는 1초에 네 번만 (매 프레임 문자열을 만들면 GC가 쌓여 VR에서 끊긴다)
            if (Time.unscaledTime >= _reportAt) { _reportAt = Time.unscaledTime + 0.25f; Report(); }
        }

        public void Shutdown() => Disconnect();

        void TryConnect()
        {
            _retryAt = Time.realtimeSinceStartup + RetrySeconds;
            var err = EVRInitError.None;
            try
            {
                if (!OpenVR.IsRuntimeInstalled()) { SetState("error", "SteamVR이 설치되어 있지 않아요"); return; }
                // 먼저 백그라운드로 붙어 본다 — SteamVR이 꺼져 있으면 바로 실패한다 (장면 앱 Init은 SteamVR을 켜며 몇 초 멈춘다)
                _system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
                if (_system != null && WantView)
                {
                    OpenVR.Shutdown();
                    _system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
                }
            }
            catch (DllNotFoundException) { SetState("error", "openvr_api.dll을 찾지 못했어요 (렌더러 빌드 확인)"); Wanted = WantView = false; return; }
            catch (Exception e) { SetState("error", "SteamVR 연결 실패: " + e.Message); return; }
            if (_system == null)
            {
                bool launching = err == EVRInitError.Init_NoServerForBackgroundApp && WantView && !_noLaunch;
                if (launching && !_launched) LaunchSteamVr();
                SetState("waiting", err switch
                {
                    EVRInitError.Init_NoServerForBackgroundApp => launching ? "SteamVR을 켜는 중… (켜지면 자동으로 이어져요)" : "SteamVR이 꺼져 있어요 — SteamVR을 켜면 자동으로 연결돼요",
                    EVRInitError.Init_HmdNotFound or EVRInitError.Init_HmdNotFoundPresenceFailed => "헤드셋을 찾지 못했어요 — 헤드셋을 연결하면 자동으로 연결돼요",
                    _ => "SteamVR 연결 실패: " + OpenVR.GetStringForHmdError(err) + $" ({(int)err})",
                });
                return;
            }
            _scene = WantView;
            Compositor = _scene ? OpenVR.Compositor : null;
            if (_scene && Compositor == null)
            {
                Disconnect();
                SetState("error", "SteamVR 화면 출력(Compositor)을 열지 못했어요");
                return;
            }
            RefreshDevices();
            SetState("ok", _scene ? "VR로 보는 중이에요" : "SteamVR에 연결됐어요");
            Debug.Log($"[VrcProto] SteamVR 연결 ({(_scene ? "장면 앱" : "백그라운드")}): {Summary()}");
        }

        /// <summary>SteamVR 켜기: OpenVR가 장면 앱 Init에서 하는 것과 같이 런타임의 vrstartup.exe를 띄운다 (기다리지 않는다).</summary>
        void LaunchSteamVr()
        {
            _launched = true;
            try
            {
                var exe = Path.Combine(OpenVR.RuntimePath(), "bin", "win64", "vrstartup.exe");
                if (!File.Exists(exe)) { Debug.LogWarning("[VrcProto] vrstartup.exe 없음: " + exe); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                Debug.Log("[VrcProto] SteamVR 켜기: " + exe);
            }
            catch (Exception e) { Debug.LogWarning("[VrcProto] SteamVR 켜기 실패: " + e.Message); }
        }

        void Disconnect()
        {
            if (_system == null) return;
            _system = null;
            Compositor = null;
            _scene = false;
            Devices.Clear();
            Hmd = null;
            Fps = 0;
            try { OpenVR.Shutdown(); } catch (Exception e) { Debug.LogWarning("[VrcProto] OpenVR 끊기: " + e.Message); }
        }

        void PumpEvents()
        {
            var ev = new VREvent_t();
            uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VREvent_t));
            bool devicesChanged = false;
            while (_system != null && _system.PollNextEvent(ref ev, size))
            {
                switch ((EVREventType)ev.eventType)
                {
                    case EVREventType.VREvent_Quit:
                        // SteamVR이 끝나는 중: 바로 놓아 주고, 조금 뒤부터 다시 붙어 본다 (그동안 다시 켜지면 이어진다)
                        _system.AcknowledgeQuit_Exiting();
                        Disconnect();
                        _launched = true;   // 사용자가 끈 SteamVR을 다시 켜지는 않는다
                        _retryAt = Time.realtimeSinceStartup + RetrySeconds;
                        SetState("waiting", "SteamVR이 꺼졌어요 — 다시 켜면 자동으로 연결돼요");
                        return;
                    case EVREventType.VREvent_TrackedDeviceActivated:
                    case EVREventType.VREvent_TrackedDeviceDeactivated:
                    case EVREventType.VREvent_TrackedDeviceRoleChanged:
                    case EVREventType.VREvent_TrackedDeviceUpdated:
                        devicesChanged = true; break;
                }
            }
            if (devicesChanged) RefreshDevices();
        }

        void RefreshDevices()
        {
            Devices.Clear();
            Hmd = null;
            for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                var cls = _system.GetTrackedDeviceClass(i);
                if (cls == ETrackedDeviceClass.Invalid || !_system.IsTrackedDeviceConnected(i)) continue;
                var role = _system.GetControllerRoleForTrackedDeviceIndex(i);
                var d = new Device
                {
                    Index = i,
                    Kind = cls switch
                    {
                        ETrackedDeviceClass.HMD => "hmd", ETrackedDeviceClass.Controller => "controller",
                        ETrackedDeviceClass.GenericTracker => "tracker", ETrackedDeviceClass.TrackingReference => "base", _ => "other",
                    },
                    Role = role == ETrackedControllerRole.LeftHand ? "left" : role == ETrackedControllerRole.RightHand ? "right" : "",
                    Model = Prop(i, ETrackedDeviceProperty.Prop_ModelNumber_String),
                    Maker = Prop(i, ETrackedDeviceProperty.Prop_ManufacturerName_String),
                    Type = Prop(i, ETrackedDeviceProperty.Prop_ControllerType_String),
                    Serial = Prop(i, ETrackedDeviceProperty.Prop_SerialNumber_String),
                };
                Devices.Add(d);
                if (d.Kind == "hmd" && Hmd == null) Hmd = d;
            }
            // 장치 목록이 바뀔 때마다 로그에 (2026-11-03)
            var sb = new StringBuilder();
            foreach (var d in Devices)
                if (d.Kind != "base") sb.Append(sb.Length > 0 ? " · " : "").Append($"#{d.Index} {d.Kind}{(d.Role.Length > 0 ? "/" + d.Role : "")} [{d.Maker} {d.Model} · {d.Type}]");
            var list = sb.ToString();
            if (list != _lastDevices) { _lastDevices = list; Debug.Log("[VrcProto] SteamVR 장치: " + (list.Length > 0 ? list : "없음")); }
        }

        string _lastDevices = "";

        string Prop(uint i, ETrackedDeviceProperty p)
        {
            var err = ETrackedPropertyError.TrackedProp_Success;
            _sb.Clear();
            _system.GetStringTrackedDeviceProperty(i, p, _sb, (uint)_sb.Capacity, ref err);
            return err == ETrackedPropertyError.TrackedProp_Success ? _sb.ToString() : "";
        }

        void ReadPoses()
        {
            _system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
            ApplyPoses();
        }

        /// <summary>장면 앱: 컴포지터가 다음 프레임을 받을 준비가 될 때까지 기다리고(90 Hz 등 헤드셋 주기에 맞춰진다) 그 프레임용 포즈를 읽는다.</summary>
        public bool WaitPoses()
        {
            if (!IsScene) return false;
            var err = Compositor.WaitGetPoses(_poses, _gamePoses);
            if (err != EVRCompositorError.None && err != EVRCompositorError.DoNotHaveFocus) return false;
            ApplyPoses();
            return true;
        }

        void ApplyPoses()
        {
            foreach (var d in Devices)
            {
                var p = _poses[d.Index];
                d.Tracked = p.bPoseIsValid && p.bDeviceIsConnected;
                if (d.Tracked) ToUnity(p.mDeviceToAbsoluteTracking, out d.Position, out d.Rotation);
            }
        }

        /// <summary>OpenVR(오른손 좌표, -Z 앞) 3×4 행렬 → Unity(왼손, +Z 앞) 위치·회전. Z를 뒤집는다.</summary>
        public static void ToUnity(HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(m.m3, m.m7, -m.m11);
            var forward = new Vector3(-m.m2, -m.m6, m.m10);
            var up = new Vector3(m.m1, m.m5, -m.m9);
            rot = forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
        }

        void SetState(string state, string text)
        {
            if (State == state && Text == text) return;
            State = state; Text = text;
            Report();
        }

        /// <summary>vrStatus를 보낸다 — 상태·장치 목록·추적 여부·fps(10 단위)가 바뀌었을 때만 (포즈 값은 넣지 않는다).</summary>
        void Report()
        {
            var devs = JNode.Arr();
            int fps = IsScene ? Mathf.RoundToInt(Fps / 10f) * 10 : 0;
            var key = new StringBuilder(State).Append('|').Append(Text).Append('|').Append(IsScene).Append(fps).Append(SubmitError).Append(Desktop).Append(InputProblem)
                .Append(Mathf.Round(GpuMs)).Append(Reprojected / 5).Append(Dropped > 0).Append(Resolution).Append('r').Append(Mirrors)
                .Append('p').Append(Panels).Append(PanelProblem);
            foreach (var d in Devices)
            {
                devs.Add(JNode.Obj().Set("index", (int)d.Index).Set("kind", d.Kind).Set("role", d.Role).Set("model", d.Model)
                    .Set("maker", d.Maker).Set("type", d.Type).Set("tracked", d.Tracked));
                key.Append('|').Append(d.Index).Append(d.Kind).Append(d.Role).Append(d.Model).Append(d.Tracked);
            }
            var k = key.ToString();
            if (k == _lastSent) return;
            _lastSent = k;
            var m = Msg.Make(Msg.VrStatus).Set("state", State).Set("text", Text).Set("wanted", Wanted).Set("view", IsScene)
                .Set("wantView", WantView).Set("desktop", Desktop).Set("devices", devs);
            if (IsScene) m.Set("fps", fps).Set("gpuMs", Mathf.Round(GpuMs * 10) / 10f).Set("reprojected", Reprojected).Set("dropped", Dropped)
                .Set("resolution", Resolution).Set("mirrors", Mirrors).Set("panels", Panels);
            if (IsScene && InputProblem.Length > 0) m.Set("inputProblem", InputProblem);
            if (IsScene && PanelProblem.Length > 0) m.Set("panelProblem", PanelProblem);
            if (SubmitError.Length > 0) m.Set("error", SubmitError);
            Send(m);
        }

        string Summary()
        {
            var parts = new List<string>();
            foreach (var d in Devices) parts.Add($"{d.Kind}{(d.Role.Length > 0 ? "/" + d.Role : "")} {d.Maker} {d.Model}".Trim());
            return parts.Count == 0 ? "장치 없음" : string.Join(" · ", parts);
        }
    }
}
