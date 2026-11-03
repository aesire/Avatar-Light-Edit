// VR로 보기 (PLAN 3.12): 장면 앱으로 붙은 동안 눈마다 카메라 하나로 그려 SteamVR에 보낸다.
//   - 눈 카메라는 미리보기 카메라의 배경·후처리(PostFx 값)를 매 프레임 따라간다 — XR 단일 패스가 아니라서 OnRenderImage 효과가 그대로 돈다.
//     HDR은 후처리를 쓸 때만 (MSAA 4× HDR 버퍼는 눈마다 수백 MB를 읽고 써서 무겁다).
//   - 프레임: LateUpdate에서 Pre(WaitGetPoses → 입력 → 이동·잡기, PhysBone 전) → PhysBone → Render(눈 위치 → 그리기 → Submit → PostPresentHandoff).
//     렌더러는 단일 스레드 렌더링(ProjectSettings m_MTRendering 0, 2026-10-23)이라 그린 바로 뒤 메인 스레드에서 보낸다 — 멀티스레드 렌더링에서는
//     렌더 스레드가 한 프레임 늦게 그려 보내서 헤드셋 주기를 놓치고(재투영) 끊겨 보였다. 멀티스레드로 돌면 렌더 스레드 이벤트로 보낸다(예전 방식).
//   - 관찰자만 (2026-11-03 사용자 "VR에서는 작업하는 것만" — 아바타 안으로 들어가기(몸·IK·FBT·캘리브레이션·1인칭·손 제스처)와
//     VR 안 자세 찍기·고정·모션 녹화는 뺐다): 처음과 "다시 맞추기" 때 내 머리가 아바타 앞 1.2 m에서 아바타를 보게 방(리그)을 옮기고 돌린다. 크기는 1:1.
//     조작: VrLocomotion — 왼쪽 스틱 이동 · 오른쪽 스틱 스냅 회전 · A 점프, 바닥은 아바타가 선 높이 (2026-10-23 사용자).
//   - 바닥 격자: 미리보기 "바닥 격자"를 켜 두면 VR에도 실제 미터 격자(1 m, 0.25 m 옅게)를 바닥에 깐다 (눈 카메라 전용 레이어).
//   - 해상도: SteamVR 권장 크기 × vr { resolution } (0.5~1.5). 프레임 시간(GPU ms·재투영·놓친 프레임)은 1초마다 상태로.
//   - PC 화면: eye = 왼눈 화면(VRChat처럼, 미리보기 카메라는 그리지 않는다) · orbit = 지금 미리보기 카메라.
//   - 카메라(VrCamera, 8단계): 손에 드는 카메라 — 그립으로 잡고 트리거로 찍기·녹화, 스틱 줌, 옆 조작판(VrPanels "camera"). VR 창 다음·이동 전에 잡기(그 손 스틱을 비움), 눈 그리기 전에 미리보기·사진·영상. 스트림이면 렌더러 창에 카메라 화면.
//   - 본·아바타 잡기(VrBoneGrab, 2026-10-25): 그립으로 관절(자세 다듬기)이나 아바타 전체(옮기기)를 잡는다 (함께 세운 아바타도).
//     카메라 다음·거울 전 (이동 뒤라 손 자리가 이번 프레임 방 기준).
//   - 거울(VrMirror, 2-3): 월드 거울 · 개인 거울. 눈마다 그 눈을 그리기 직전에 반사를 그린다. 거울 판은 눈 전용이라 orbit PC 화면에는 안 보인다.
//   - 에디터 창(VrPanels, 3단계): Host 창을 SteamVR 오버레이로 띄우고 레이저로 누른다. 입력 바로 다음에 돌아 창을 가리키는 손의 스틱을 휠로 가져간다.
//   스튜디오 배경(파노라마·그라데이션)은 미리보기 카메라에만 맞춰 그려져 VR에서는 아직 맞지 않는다 (환경 배경·하늘은 그대로).
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace VrcProto.Viewer
{
    public sealed class VrView : MonoBehaviour
    {
        VrLink _link;
        OrbitCamera _orbit;
        SceneBuilder _scene;
        Func<bool> _gridOn = () => true;
        Transform _rig;
        readonly Camera[] _eyes = new Camera[2];
        readonly PostFx[] _fx = new PostFx[2];
        readonly RenderTexture[] _draw = new RenderTexture[2], _submit = new RenderTexture[2];
        readonly Vector3[] _eyePos = new Vector3[2];
        readonly Quaternion[] _eyeRot = new Quaternion[2];
        readonly Vector4[] _tan = new Vector4[2];   // 투영 탄젠트 (l, r, t, b) — 방 크기(근평면)가 바뀌면 다시 만든다
        readonly VrInput _input = new VrInput();
        readonly VrLocomotion _move = new VrLocomotion();
        readonly VrMirror _mirror = new VrMirror();
        readonly VrPanels _panels = new VrPanels();
        readonly VrCamera _camera = new VrCamera();
        readonly VrBoneGrab _bones = new VrBoneGrab();
        readonly VrControllers _controllers = new VrControllers();
        readonly VrItemGrab _items = new VrItemGrab();
        GameObject _grid;
        Transform _seenRoot;
        bool _active, _recenter, _posed, _direct;
        float _fpsAt, _projScale = -1, _res = 1, _floor;
        int _frames;
        // 프레임 시간 (1초 모음)
        uint _lastTimingIndex;
        int _timed, _reproj, _dropped;
        float _gpuMs;

        public const float ObserveDistance = 1.2f;
        const float Near = 0.01f;

        public VrInput Input => _input;
        public VrPanels Panels => _panels;
        public VrBoneGrab Bones => _bones;
        public VrItemGrab Items => _items;
        public VrCamera HandCamera => _camera;

        public void Init(VrLink link, OrbitCamera orbit, SceneBuilder scene, Func<bool> gridOn)
        {
            _link = link; _orbit = orbit; _scene = scene; _gridOn = gridOn ?? _gridOn;
            _link.Recenter = () => _recenter = true;
            _panels.Send = m => _link.Send(m);
            _bones.Controllers = _controllers;   // 잡는 점 공
            _items.Send = m => _link.Send(m); _items.Controllers = _controllers; _items.Init(scene);
            _camera.Scene = scene;   // 카메라 심도 자동 초점
            _rig = transform;
        }

        /// <summary>RenderServer.LateUpdate에서 PhysBone 전에: 포즈를 기다리고 이동·잡기를 한다 (PhysBone이 그 움직임에 반응하게).</summary>
        public void Pre()
        {
            _posed = false;
            if (_link == null) return;
            _camera.Flush(_link);
            if (_link.CameraTest.Length > 0) { var f = _link.CameraTest; _link.CameraTest = ""; _camera.Test(_rig, _orbit.Camera, f, _link.CameraWidth, _link.CameraHeight, _link); }
            if (!_active) _camera.TestTick(_orbit.Camera, _link);   // 시험 영상 (헤드셋 없이)
            if (!_link.IsScene) { if (_active) Stop(); return; }
            if (!_active && !Begin()) return;
            if (!Mathf.Approximately(_link.Resolution, _res)) { DestroyEyes(); if (!CreateEyes()) return; }
            if (!_link.WaitPoses()) return;
            var head = _link.Hmd;
            if (head == null || !head.Tracked) return;
            _input.Update();
            // VR 카메라 조작판(id "camera")은 카메라 몸체 옆에 — 카메라는 아래에서 움직이므로 한 프레임 전 자리 (잡은 동안은 그 손 기준이라 늦지 않음)
            _panels.CamOn = _camera.On;
            _camera.PanelPose(out _panels.CamPos, out _panels.CamRot, out _panels.CamHand, out _panels.CamHandPos, out _panels.CamHandRot, out _panels.CamSide, out _panels.CamSize);
            // 가까이 잡을 것(관절·아바타)이 먼저: 그 손은 멀리 있는 VR 창을 그립으로 잡지 않고, 관절을 가리키면 레이저도 끈다 (2026-10-25 "배경 잡기에 조작이 씹힘")
            _bones.Enabled = _link.BoneGrab;
            _bones.PinOthers = _link.BonePinOthers; _bones.Locked = _link.BoneLock; _bones.HandleScale = _link.BoneHandle;
            // 아이템(2026-10-27): 범위 안이면 그 손은 아바타째 잡지 않는다 (관절이 더 가까우면 관절)
            _items.Enabled = _link.ItemGrab; _items.Snap = _link.ItemSnap; _items.Surface = _link.ItemSurface; _items.Mirror = _link.ItemMirror;
            _items.Prepare(_rig, _input, Hand("left"), Hand("right"), true);
            for (int h = 0; h < 2; h++) _bones.ItemHere[h] = _items.Near(h);
            _bones.Prepare(_rig, _input, Hand("left"), Hand("right"), true);
            _panels.NearGrab = h => _bones.Near(h) || _items.Near(h); _panels.NearBone = _bones.NearBone;
            _panels.Tick(_input, head, Hand("left"), Hand("right"), Time.unscaledDeltaTime);   // 메뉴 단추(B/Y) = 에디터 창·빠른 설정   // 창을 가리키는 손의 스틱은 휠로 (아래 이동·회전이 받지 않게)
            _link.Panels = _panels.Shown; _link.PanelProblem = _panels.Problem;
            _camera.Tick(_link, _rig, _input, head, Hand("left"), Hand("right"), Time.unscaledDeltaTime);   // 카메라를 잡은 손의 스틱 = 줌 (이동·회전 전에 비운다)
            _move.Smooth = _link.SmoothTurn;
            if (_scene.Root != _seenRoot) { _seenRoot = _scene.Root; _mirror.Anchor(_scene); }   // 월드 거울은 불러온 자리 정면
            if (_recenter) { Recenter(head); _recenter = false; }
            _move.Tick(_rig, _input, head, _floor, Time.unscaledDeltaTime);
            // 본·아바타 잡기: 카메라를 잡은 손은 빼고, 잡은 손의 그립은 거울이 받지 않게 비운다
            _bones.Tick(_rig, _input, Hand("left"), Hand("right"), _camera.HeldHand, true);
            _items.Tick(_rig, _input, Hand("left"), Hand("right"), _camera.HeldHand, true, _bones.NearBone);
            _mirror.Tick(_link.Mirror, _link.PersonalMirror, _link.MirrorSummon, _rig, _input, head, Hand("left"), Hand("right"));
            _link.MirrorSummon = false;
            _link.Mirrors = _mirror.Count;
            _link.InputProblem = _input.Problem;
            _posed = true;
        }

        VrLink.Device Hand(string role)
        {
            foreach (var d in _link.Devices) if (d.Kind == "controller" && d.Role == role) return d;
            return null;
        }

        /// <summary>RenderServer.LateUpdate에서 PhysBone 다음에: 눈마다 그려 보낸다.</summary>
        public void Render()
        {
            if (!_active || !_posed) return;
            var head = _link.Hmd;
            var main = _orbit.Camera;
            if (_projScale < 0) Project(1);
            var headPos = _rig.TransformPoint(head.Position);
            var headRot = _rig.rotation * head.Rotation;
            var srcFx = main.GetComponent<PostFx>();
            bool fxOn = srcFx != null && srcFx.enabled;
            PlaceGrid(headPos);
            _controllers.Enabled = _link.ShowControllers;
            _controllers.Update(_rig, _input, Hand("left"), Hand("right"));   // 내 손 자리 (2026-10-25)
            // 원본만 본다 (1인칭 사본 레이어는 뺀다). 바닥 격자는 눈 카메라에만
            int mask = (main.cullingMask == 0 ? ~VrLayers.HiddenFromPreview : main.cullingMask) | VrLayers.VrOnlyMask;   // 와이어프레임 보기에서도 VR은 셰이딩
            // 거울 반사: 머리가 있는 원본(3인칭)을 비추고, 거울 판·격자(눈 전용 레이어)는 비추지 않는다
            int mirrorMask = (mask | VrLayers.ThirdPersonMask) & ~VrLayers.FirstPersonMask & ~VrLayers.VrOnlyMask;
            _camera.Grid(_link.CameraGrid);
            _camera.Render(main, mirrorMask, _link.CameraFolder, _link.CameraWidth, _link.CameraHeight, _link, _rig);   // 거울처럼 머리가 있는 원본을 찍는다
            mask &= ~VrLayers.FirstPersonMask;
            for (int i = 0; i < 2; i++)
            {
                var c = _eyes[i];
                c.transform.SetPositionAndRotation(headPos + headRot * _eyePos[i], headRot * _eyeRot[i]);
                c.clearFlags = main.clearFlags;
                c.backgroundColor = main.backgroundColor.a > 0 ? main.backgroundColor : (Color)new Color(0, 0, 0, 1);   // 방송 오버레이의 투명 배경은 헤드셋에서 검정
                _mirror.RenderFor(c, mirrorMask, _draw[i].width, _draw[i].height, _link.MirrorQuality);   // 이 눈의 반사 (거울 텍스처는 눈마다 다시 그린다)
                c.cullingMask = mask;
                CopyFx(srcFx, _fx[i]);
                c.allowHDR = fxOn;   // PostFx.OnEnable이 켜 두므로 매번 다시
                c.Render();
                Graphics.Blit(_draw[i], _submit[i]);   // MSAA 해상 + 제출용(밉·MSAA 없는) 텍스처로
            }
            if (_direct) Submit(); else GL.IssuePluginEvent(SubmitPtr, 1);

            _frames++;
            ReadTiming();
            if (Time.unscaledTime - _fpsAt >= 1)
            {
                float span = Time.unscaledTime - _fpsAt;
                _link.Fps = Mathf.RoundToInt(_frames / span);
                _link.GpuMs = _timed > 0 ? _gpuMs / _timed : 0;
                _link.Reprojected = _timed > 0 ? Mathf.RoundToInt(100f * _reproj / _timed) : 0;
                _link.Dropped = _dropped;
                _link.SubmitError = _submitError != 0 ? $"SteamVR에 화면을 보내지 못했어요 ({(EVRCompositorError)_submitError})" : "";
                _frames = 0; _fpsAt = Time.unscaledTime;
                _timed = _reproj = _dropped = 0; _gpuMs = 0;
            }
            main.enabled = _link.Desktop != "eye" && !(_link.CameraStream && _camera.On);   // 스트림 중에는 데스크톱 화면을 카메라가 덮는다
        }

        /// <summary>컴포지터의 마지막 프레임 기록: 이 앱의 GPU 시간 · 재투영(CPU·GPU가 늦음) · 놓친 프레임.</summary>
        void ReadTiming()
        {
            var t = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
            if (_compositor == null || !_compositor.GetFrameTiming(ref t, 0) || t.m_nFrameIndex == _lastTimingIndex) return;
            _lastTimingIndex = t.m_nFrameIndex;
            _timed++;
            _gpuMs += t.m_flTotalRenderGpuMs;
            if ((t.m_nReprojectionFlags & 0x3) != 0) _reproj++;   // VRCompositor_ReprojectionReason_Cpu | _Gpu
            _dropped += (int)t.m_nNumDroppedFrames;
        }

        /// <summary>눈마다 투영: 근평면을 방 크기에 맞춰 (작은 아바타는 더 가까이까지 보이게).</summary>
        void Project(float scale)
        {
            _projScale = scale;
            float n = Near * Mathf.Clamp(scale, 0.1f, 10), f = 100 * Mathf.Max(1, scale);
            for (int i = 0; i < 2; i++)
            {
                var t = _tan[i];
                // OpenVR의 위·아래 탄젠트는 아래가 +라서 뒤집어 Unity 절두체로 (위 = -t, 아래 = -b)
                _eyes[i].nearClipPlane = n; _eyes[i].farClipPlane = f;
                _eyes[i].projectionMatrix = Matrix4x4.Frustum(t.x * n, t.y * n, -t.w * n, -t.z * n, n, f);
            }
        }

        internal static void CopyFx(PostFx src, PostFx dst)
        {
            bool on = src != null && src.enabled;
            dst.enabled = on;
            if (!on) return;
            dst.BloomIntensity = src.BloomIntensity; dst.BloomThreshold = src.BloomThreshold; dst.BloomSoftKnee = src.BloomSoftKnee;
            dst.BloomScatter = src.BloomScatter; dst.BloomTint = src.BloomTint;
            dst.Exposure = src.Exposure; dst.Temperature = src.Temperature; dst.Tint = src.Tint;
            dst.Contrast = src.Contrast; dst.Saturation = src.Saturation; dst.Vignette = src.Vignette; dst.Tonemap = src.Tonemap;
            dst.Bypass = false;
        }

        bool Begin()
        {
            if (!CreateEyes()) return false;
            _compositor = _link.Compositor;
            _submitError = 0;
            var mode = SystemInfo.renderingThreadingMode;
            _direct = mode == RenderingThreadingMode.Direct || mode == RenderingThreadingMode.SingleThreaded;
            _input.Init();
            if (_input.Problem.Length > 0) Debug.LogWarning("[VrcProto] VR 입력: " + _input.Problem);
            RenderQuality.Vr = true;
            _panels.Begin();
            _recenter = true;
            _active = true;
            _fpsAt = Time.unscaledTime; _frames = 0;
            Debug.Log($"[VrcProto] VR로 보기 시작: 눈마다 {_draw[0].width}×{_draw[0].height} (해상도 ×{_res:0.##}) · 렌더 스레드 {mode} → {(_direct ? "그린 뒤 바로 제출" : "렌더 스레드에서 제출")}");
            return true;
        }

        bool CreateEyes()
        {
            var sys = _link.VrSystem;
            uint rw = 0, rh = 0;
            sys.GetRecommendedRenderTargetSize(ref rw, ref rh);
            if (rw == 0 || rh == 0) return false;
            _res = Mathf.Clamp(_link.Resolution, 0.5f, 1.5f);
            int w = Mathf.Max(256, Mathf.RoundToInt(rw * _res)), h = Mathf.Max(256, Mathf.RoundToInt(rh * _res));
            for (int i = 0; i < 2; i++)
            {
                var eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                // 그리는 텍스처: VRChat과 같은 MSAA 4×, sRGB (선형 색 공간 프로젝트)
                _draw[i] = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4, name = "VrEyeDraw" + i };
                _submit[i] = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "VrEyeSubmit" + i };
                _draw[i].Create(); _submit[i].Create();
                var go = new GameObject(i == 0 ? "VrEyeLeft" : "VrEyeRight");
                go.transform.SetParent(_rig, false);
                var c = go.AddComponent<Camera>();
                c.enabled = false;   // Render()에서 직접 그린다
                c.targetTexture = _draw[i];
                c.allowMSAA = true;
                float l = 0, r = 0, t = 0, b = 0;
                sys.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
                _tan[i] = new Vector4(l, r, t, b);
                VrLink.ToUnity(sys.GetEyeToHeadTransform(eye), out _eyePos[i], out _eyeRot[i]);
                _fx[i] = go.AddComponent<PostFx>();
                _eyes[i] = c;
                _tex[i] = new Texture_t { handle = _submit[i].GetNativeTexturePtr(), eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
            }
            _projScale = -1;
            return true;
        }

        void DestroyEyes()
        {
            RenderTexture.active = null;   // 마지막 Blit의 대상(제출 텍스처)이 활성인 채로 지우면 Unity가 경고한다
            for (int i = 0; i < 2; i++)
            {
                if (_eyes[i] != null) Destroy(_eyes[i].gameObject);
                if (_draw[i] != null) { _draw[i].Release(); Destroy(_draw[i]); }
                if (_submit[i] != null) { _submit[i].Release(); Destroy(_submit[i]); }
                _eyes[i] = null; _draw[i] = _submit[i] = null; _fx[i] = null;
                _tex[i] = default;
            }
        }

        void Stop()
        {
            _rig.localScale = Vector3.one;
            _active = false;
            _compositor = null;
            _mirror.End();
            _camera.End();
            _bones.End();
            _items.End();
            _controllers.End();
            _link.Mirrors = 0;
            _panels.End();
            _link.Panels = 0;
            DestroyEyes();
            if (_grid != null) _grid.SetActive(false);
            RenderQuality.Vr = false;
            _orbit.Camera.enabled = true;
            Debug.Log("[VrcProto] VR로 보기 끝");
        }

        /// <summary>관찰자 자리: 내 머리(바닥에서 잰 높이는 그대로)가 아바타 정면 ObserveDistance m에서 아바타 쪽을 보게 방을 옮긴다.
        /// 정면은 아바타 루트의 앞(+Z) — 아바타째 돌려 둔 뒤에도 얼굴 쪽으로.</summary>
        void Recenter(VrLink.Device head)
        {
            var b = _scene.WorldBounds();
            _floor = _scene.Root != null ? _scene.Root.position.y : 0;   // 아바타가 선 높이
            _move.Reset();
            var fwd = _scene.Root != null ? Vector3.ProjectOnPlane(_scene.Root.forward, Vector3.up) : Vector3.forward;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            var target = new Vector3(b.center.x, _floor, b.center.z) + fwd * ObserveDistance;
            float yaw = Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg - head.Rotation.eulerAngles.y;   // 내 시선(방 기준)을 아바타 쪽으로
            var rot = Quaternion.Euler(0, yaw, 0);
            var flat = new Vector3(head.Position.x, 0, head.Position.z);
            _rig.SetPositionAndRotation(target - rot * flat, rot);
        }

        // ---- 바닥 격자 (실제 미터, 1 m 진하게 · 0.25 m 옅게, 반지름 10 m) ----
        const int GridHalf = 10;

        void PlaceGrid(Vector3 headPos)
        {
            bool on = _gridOn();
            if (!on) { if (_grid != null) _grid.SetActive(false); return; }
            if (_grid == null) _grid = MakeGrid();
            _grid.SetActive(true);
            float floor = _floor;
            // 1 m 단위로 내 아래를 따라온다 (선은 제자리에 있는 것처럼)
            _grid.transform.position = new Vector3(Mathf.Round(headPos.x), floor + 0.002f, Mathf.Round(headPos.z));
        }

        static GameObject MakeGrid()
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var cols = new System.Collections.Generic.List<Color>();
            var idx = new System.Collections.Generic.List<int>();
            for (int k = -GridHalf * 4; k <= GridHalf * 4; k++)
            {
                float v = k * 0.25f;
                bool major = k % 4 == 0;
                var col = k == 0 ? new Color(1, 1, 1, 0.55f) : major ? new Color(1, 1, 1, 0.32f) : new Color(1, 1, 1, 0.12f);
                foreach (var (a, b) in new[] { (new Vector3(v, 0, -GridHalf), new Vector3(v, 0, GridHalf)), (new Vector3(-GridHalf, 0, v), new Vector3(GridHalf, 0, v)) })
                {
                    idx.Add(verts.Count); verts.Add(a); cols.Add(col);
                    idx.Add(verts.Count); verts.Add(b); cols.Add(col);
                }
            }
            var mesh = new Mesh { name = "VrGrid" };
            mesh.SetVertices(verts); mesh.SetColors(cols);
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(GridHalf * 2, 0.1f, GridHalf * 2));
            var go = new GameObject("VrGrid") { layer = VrLayers.VrOnly };
            DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)CullMode.Off);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        void OnGUI()
        {
            if (!_active || Event.current.type != EventType.Repaint) return;
            // VR 카메라 스트림: 카메라 화면을 렌더러 창에 (OBS로 잡기 — VRChat 스트림 카메라처럼, 데스크톱 보기 설정보다 먼저)
            if (_link.CameraStream && _camera.StreamTexture is Texture cam)
            {
                GUI.depth = -100;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture, ScaleMode.StretchToFill, false);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), cam, ScaleMode.ScaleToFit, false);
                return;
            }
            if (_link.Desktop != "eye" || _submit[0] == null) return;
            GUI.depth = -100;   // 보조 표시(방향 기즈모 등) 위에
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _submit[0], ScaleMode.ScaleAndCrop, false);
        }

        void OnDestroy() { if (_active) Stop(); _panels.Shutdown(); }

        // ---- 제출 (단일 스레드면 바로, 멀티스레드면 렌더 스레드 이벤트로) ----
        static readonly Texture_t[] _tex = new Texture_t[2];
        // Unity 텍스처는 D3D에서 위아래가 뒤집혀 저장되므로 v를 뒤집어 보낸다
        static VRTextureBounds_t _bounds = new VRTextureBounds_t { uMin = 0, vMin = 1, uMax = 1, vMax = 0 };
        static CVRCompositor _compositor;
        static volatile int _submitError;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void RenderEvent(int eventId);
        static readonly RenderEvent SubmitFn = OnSubmit;
        static readonly IntPtr SubmitPtr = Marshal.GetFunctionPointerForDelegate(SubmitFn);

        [AOT.MonoPInvokeCallback(typeof(RenderEvent))]
        static void OnSubmit(int eventId) => Submit();

        static void Submit()
        {
            var c = _compositor;
            if (c == null || _tex[0].handle == IntPtr.Zero) return;
            var l = _tex[0]; var r = _tex[1];
            var e1 = c.Submit(EVREye.Eye_Left, ref l, ref _bounds, EVRSubmitFlags.Submit_Default);
            var e2 = c.Submit(EVREye.Eye_Right, ref r, ref _bounds, EVRSubmitFlags.Submit_Default);
            c.PostPresentHandoff();
            var e = e1 != EVRCompositorError.None ? e1 : e2;
            _submitError = e == EVRCompositorError.None || e == EVRCompositorError.DoNotHaveFocus ? 0 : (int)e;
        }
    }
}
