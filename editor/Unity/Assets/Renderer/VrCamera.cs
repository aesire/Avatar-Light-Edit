// VR 카메라 (PLAN 3.12, 2026-10-24 사용자 "잡고 찍을 수 있는 카메라" → 2026-10-25 "VRC 카메라와 비슷하게, 영상 기능"): VRChat 카메라처럼 손에 드는 카메라.
//   - 몸체: 둥근 모서리 몸체 · 원형 렌즈(금속 고리 · 유리) · 위 셔터 단추 · 앞 녹화 표시등. 셰이딩은 빛 방향을 정점 UV에 구워 Unlit/Texture 하나로 그린다(새 셰이더 없이).
//     뒷면 화면에 렌즈가 보는 것(미리보기), 그 위에 3분할 격자 · 녹화 중 빨간 점과 테두리 · 타이머 중 노란 테두리(깜빡임).
//   - 켜면(또는 "내 앞으로") 내 앞 0.35 m에 렌즈가 내가 보는 쪽을 향하게 뜬다. 그립으로 잡아 옮긴다(손에서 0.12 m 안).
//     놓으면 cameraOpts.anchor대로: room = 그 자리(방 기준, 스틱으로 걸으면 함께) · world = 월드에 고정 · follow = 고개 방향 기준 그 자리로 천천히 따라옴.
//   - 크기(cameraOpts.scale 0.5~3): 몸체·화면·렌즈를 함께 키운다 (화면을 크게 보기 — 찍히는 그림·사진 크기와는 무관). 잡는 거리·부르는 거리도 따라감, 조작판은 크기 그대로 옆에.
//   - 고정(lock): 그 자리(월드)에 박혀 그립으로 잡히지 않는다 (실수로 옮기지 않게, 삼각대처럼). 풀면 다시 잡을 수 있다.
//   - 잡은 손의 트리거 = 셔터, 잡은 손의 스틱 위·아래 = 줌(화각 20~100°, 그동안 그 스틱은 이동·회전에 안 감). 조작판(Host가 그리는 VR 창 "camera", VrPanels가 몸체 옆에 붙임)의 셔터도 같다.
//     셔터: 타이머가 있으면 그만큼 세고 → 사진 한 장, 영상이면 녹화 시작 · 녹화 중이면 멈춤 · 세는 중이면 취소.
//   - 렌즈: 셀카(flip) = 화면 쪽에서 나를 · 나를 보기(lookAt) = 늘 내 머리를 향함 · 손떨림 보정(smooth 0~3) = 렌즈 자리·방향을 부드럽게 따라감 · 세로(portrait) = 세로 사진·영상.
//   - 사진: 사진 크기(Host, 기본 1920×1080)·MSAA 8×로 미리보기 카메라의 배경·후처리를 그대로 그려 AsyncGPUReadback → PNG를 스레드에서 써서 vrCameraShot (VR 프레임을 멈추지 않게). 화면이 잠깐 하얘진다.
//   - 영상: Host가 미리 열어 둔 녹화 파이프(cameraPipe, Core.RecordJob.Pipe)에 스튜디오 녹화와 같은 형식(RecordOut.cs RecordPipe)으로 실제 시각 기준 장을 보낸다 —
//     그리기가 늦으면 같은 그림을 되풀이하고, Host가 뒤처지면(보내기 큐 4장) 건너뛰었다 되풀이로 메운다. 1920×1080(세로면 1080×1920), 30 또는 60 fps, 소리 없음, 최대 30분.
//     파이프 연결은 스레드에서 (VR 프레임이 멈추지 않게). 시작·끝은 vrCameraVideo, MP4 마무리·파일 이름은 Host.
//   - 스트림(stream): 미리보기를 1280×720으로 매 프레임 그려 렌더러 창(데스크톱)에 카메라 화면을 띄운다 (VrView.OnGUI — OBS로 잡기, VRChat 스트림 카메라처럼).
//   - 상태(녹화·남은 초·줌·장 수)는 바뀔 때만 vrCameraState로 Host에 (조작판 표시).
//   - 카메라 몸체·화면은 눈 전용 레이어(VrOnly)라 사진·미리보기·거울에는 안 찍힌다. 렌즈에는 머리가 있는 원본(3인칭)이 보인다(셀카에 내 얼굴).
//   방(리그)의 자식이라 몸 모드 크기 배율과 상관없이 내 손에 같은 크기로 보인다.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class VrCamera
    {
        const float GrabRadius = 0.12f, SummonDistance = 0.35f, FovMin = 20, FovMax = 100, ZoomSpeed = 45;   // m · 도/초
        /// <summary>몸체 크기 (방 기준 m) — VrPanels가 조작판을 옆에 붙일 때도 쓴다.</summary>
        public static readonly Vector3 BodySize = new Vector3(0.14f, 0.09f, 0.035f);
        const float Corner = 0.016f, LensR = 0.024f, LensDepth = 0.02f, ScreenMargin = 0.009f;
        const int PreviewLong = 512, PreviewShort = 288, StreamLong = 1280, StreamShort = 720;
        const int VideoLong = 1920, VideoShort = 1080, MaxVideoSeconds = 30 * 60;
        static readonly float[] SmoothRate = { 0, 14, 7, 3.5f };   // 손떨림 보정 세기별 따라가는 빠르기 (1/초, 0 = 바로)

        GameObject _go, _screen, _led, _dot;
        readonly List<GameObject> _gridLines = new List<GameObject>();
        readonly GameObject[] _frame = new GameObject[4];
        Material _bodyMat, _screenMat, _ledMat, _dotMat, _frameMat, _gridMat;
        Camera _cam;
        PostFx _fx;
        RenderTexture _preview;
        Material _pack;
        RenderTexture _packRt;
        int _held = -1, _frameNo, _side = -1;
        Vector3 _offPos; Quaternion _offRot = Quaternion.identity;
        float _fov = 60, _flashUntil, _countEnd = -1, _size = 1;
        bool _shoot, _layoutPortrait, _layoutStream, _layoutDone;
        string _error = "";
        readonly VrLink.Device[] _hands = new VrLink.Device[2];
        readonly ConcurrentQueue<JNode> _done = new ConcurrentQueue<JNode>();
        // 놓은 뒤 자리 (anchor): 무엇으로 적어 뒀는지 · 월드 자리 · 고개 방향 기준 자리
        string _anchorSet = "";
        Vector3 _anchorPos; Quaternion _anchorRot = Quaternion.identity;
        // 내 머리 (방 기준, 나를 보기·따라오기)
        Vector3 _headPos; bool _headOk;
        // 렌즈 (손떨림 보정이 따라가는 자리, 월드)
        Vector3 _lensPos; Quaternion _lensRot = Quaternion.identity; bool _lensInit;
        // 영상
        Task<RecordPipe> _connecting;
        RecordPipe _rec, _finishing;
        int _recW, _recH, _recFps, _written, _inflight, _finishFrames;
        float _recT0, _finishSeconds;
        bool _stopRec;
        string _recError;
        string _lastState = "";

        public bool On => _go != null && _go.activeSelf;
        public bool Held => _held >= 0;
        /// <summary>카메라를 잡은 손 (0 왼손 · 1 오른손, -1 = 없음).</summary>
        public int HeldHand => _held;
        public int Shots { get; private set; }
        public int Videos { get; private set; }
        public bool Recording => _rec != null || _connecting != null;
        /// <summary>스트림으로 데스크톱에 띄울 화면 (켜져 있을 때만, 아니면 null).</summary>
        public Texture StreamTexture => On && _layoutStream ? _preview : null;

        /// <summary>조작판(VrPanels "camera")을 붙일 자리: 몸체(방 기준) · 잡은 손과 그 손 기준 몸체 자리 · 조작판 쪽(-1 = 화면에서 보아 왼쪽 — 오른손으로 잡으면 왼쪽).</summary>
        public void PanelPose(out Vector3 pos, out Quaternion rot, out int hand, out Vector3 handPos, out Quaternion handRot, out float side, out float size)
        {
            size = _size;
            var t = _go != null ? _go.transform : null;
            pos = t != null ? t.localPosition : Vector3.zero; rot = t != null ? t.localRotation : Quaternion.identity;
            hand = _held; handPos = _offPos; handRot = _offRot; side = _side;
        }

        /// <summary>VrView.Pre 맨 앞에서 (VR이 멈춰 있어도): 다 쓴 사진·끝난 영상을 알린다.</summary>
        public void Flush(VrLink link)
        {
            while (_done.TryDequeue(out var m)) link.Send(m);
            if (_finishing != null && _finishing.Done)
            {
                bool ok = _finishing.Error == null && _recError == null;
                link.Send(Msg.Make(Msg.VrCameraVideo).Set("stopped", true).Set("ok", ok).Set("frames", _finishFrames).Set("seconds", _finishSeconds).Set("error", _finishing.Error ?? _recError));
                if (ok) Videos++; else _error = "영상을 저장하지 못했어요: " + (_finishing.Error ?? _recError);
                _finishing = null; _recError = null;
            }
        }

        /// <summary>VrView.Pre에서 VR 창 다음·몸 전에: 켜고 끄기, 부르기, 잡기·줌·셔터·타이머, 놓은 뒤 자리 (잡은 손의 스틱은 비워 이동·회전이 받지 않게).</summary>
        public void Tick(VrLink link, Transform rig, VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right, float dt)
        {
            bool want = link.CameraOn;
            if (want && _go == null) { Make(rig); link.CameraSummon = true; }
            if (_go == null) { link.CameraShutter = false; return; }
            if (_go.activeSelf != want) { _go.SetActive(want); if (want) link.CameraSummon = true; }
            if (!Mathf.Approximately(_size, link.CameraScale)) { _size = link.CameraScale; _go.transform.localScale = Vector3.one * _size; }   // 크기: 몸체·화면·렌즈를 함께 (방 기준)
            if (!want)
            {
                _held = -1; _countEnd = -1; link.CameraShutter = false;
                if (_rec != null) FinishRecording(_written > 0); else if (_connecting != null) _stopRec = true;   // 끄면 그리기가 멈추므로 바로 마무리
                PollConnect(link);
                SendState(link); return;
            }
            _headOk = head != null && head.Tracked;
            if (_headOk) _headPos = head.Position;
            if (link.CameraSummon && _headOk) { Summon(head); link.CameraSummon = false; _held = -1; _anchorSet = ""; _lensInit = false; }
            if (link.CameraFov >= 0) { _fov = Mathf.Clamp(link.CameraFov, FovMin, FovMax); link.CameraFov = -1; }
            // 자리 저장·불러오기 (2026-10-27, VRChat 카메라 북마크처럼): 저장은 지금 몸체 자리(월드)를 Host로, 불러오면 그 자리에 놓고 놓은 자리 기준을 다시 잡는다
            if (link.CameraBookmark >= 0)
            {
                var bt = _go.transform; var bp = bt.position; var bq = bt.rotation;
                link.Send(Msg.Make(Msg.VrCameraBookmark).Set("slot", link.CameraBookmark).Set("pos", JNode.FloatArray(new[] { bp.x, bp.y, bp.z })).Set("rot", JNode.FloatArray(new[] { bq.x, bq.y, bq.z, bq.w })));
                link.CameraBookmark = -1;
            }
            if (link.CameraGoto is float[] go)
            {
                link.CameraGoto = null;
                _held = -1; _anchorSet = ""; _lensInit = false;
                _go.transform.SetPositionAndRotation(new Vector3(go[0], go[1], go[2]), new Quaternion(go[3], go[4], go[5], go[6]).normalized);
            }
            if (!link.CameraVideo && Recording) StopRecording();   // 사진으로 바꾸면 녹화를 끝낸다

            var t = _go.transform;
            var hands = _hands; hands[0] = left; hands[1] = right;
            PollConnect(link);
            if (link.CameraLock && _held >= 0) _held = -1;   // 고정하면 손에서 놓는다
            if (_held < 0 && !link.CameraLock)
                for (int h = 0; h < 2; h++)
                {
                    var d = hands[h];
                    if (d == null || !d.Tracked || !input.GrabDown[h]) continue;
                    if (Vector3.Distance(d.Position, t.localPosition) > GrabRadius + BodySize.x * 0.5f * _size) continue;
                    _held = h; _side = h == 1 ? -1 : 1;
                    _offPos = Quaternion.Inverse(d.Rotation) * (t.localPosition - d.Position);
                    _offRot = Quaternion.Inverse(d.Rotation) * t.localRotation;
                    _anchorSet = "";
                    break;
                }
            if (_held >= 0)
            {
                var hd = hands[_held];
                if (hd == null || !input.Grab[_held]) _held = -1;
                else
                {
                    t.localPosition = hd.Position + hd.Rotation * _offPos;
                    t.localRotation = hd.Rotation * _offRot;
                    // 잡은 손 스틱 = 줌 (왼손 = 이동 스틱, 오른손 = 회전 스틱)
                    var stick = _held == 0 ? input.Move : input.Turn;
                    if (Mathf.Abs(stick.y) > 0.2f) _fov = Mathf.Clamp(_fov - stick.y * ZoomSpeed * dt, FovMin, FovMax);
                    if (_held == 0) input.Move = Vector2.zero; else input.Turn = Vector2.zero;
                    if (input.UseDown[_held]) Shutter(link);
                }
            }
            if (_held < 0) Anchor(link.CameraLock ? "world" : link.CameraAnchor, rig, head, dt);   // 고정 = 그 자리(월드)에서 안 움직이고 잡히지도 않음
            if (link.CameraShutter) { link.CameraShutter = false; Shutter(link); }
            if (_countEnd > 0 && Time.unscaledTime >= _countEnd) { _countEnd = -1; Fire(link); }
            if (_rec != null && Time.realtimeSinceStartup - _recT0 >= MaxVideoSeconds) { StopRecording(); _error = "영상은 30분까지예요 — 끝냈어요"; }
            SendState(link);
        }

        /// <summary>놓은 카메라의 자리: room = 그대로(리그 자식) · world = 처음 놓은 월드 자리 · follow = 고개 방향 기준 그 자리로 천천히.</summary>
        void Anchor(string anchor, Transform rig, VrLink.Device head, float dt)
        {
            var t = _go.transform;
            if (_anchorSet != anchor)
            {
                _anchorSet = anchor;
                if (anchor == "world") { _anchorPos = t.position; _anchorRot = t.rotation; }
                else if (anchor == "follow" && _headOk) { var yaw = Yaw(head); _anchorPos = Quaternion.Inverse(yaw) * (t.localPosition - head.Position); _anchorRot = Quaternion.Inverse(yaw) * t.localRotation; }
            }
            if (anchor == "world") t.SetPositionAndRotation(_anchorPos, _anchorRot);
            else if (anchor == "follow" && _headOk)
            {
                var yaw = Yaw(head);
                float k = 1 - Mathf.Exp(-5f * dt);
                t.localPosition = Vector3.Lerp(t.localPosition, head.Position + yaw * _anchorPos, k);
                t.localRotation = Quaternion.Slerp(t.localRotation, yaw * _anchorRot, k);
            }
        }

        static Quaternion Yaw(VrLink.Device head)
        {
            var f = head.Rotation * Vector3.forward; f.y = 0;
            return f.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        /// <summary>셔터 (잡은 손 트리거 · 조작판): 녹화 중 = 멈춤 · 세는 중 = 취소 · 타이머 = 세기 시작 · 아니면 바로.</summary>
        void Shutter(VrLink link)
        {
            _error = "";
            if (Recording) { StopRecording(); return; }
            if (_countEnd > 0) { _countEnd = -1; return; }
            if (link.CameraTimer > 0) { _countEnd = Time.unscaledTime + link.CameraTimer; return; }
            Fire(link);
        }

        void Fire(VrLink link)
        {
            if (link.CameraVideo) StartRecording(link);
            else _shoot = true;
        }

        void SendState(VrLink link)
        {
            int count = _countEnd > 0 ? Mathf.CeilToInt(_countEnd - Time.unscaledTime) : 0;
            int secs = _rec != null ? Mathf.FloorToInt(Time.realtimeSinceStartup - _recT0) : 0;
            var key = $"{On}|{link.CameraVideo}|{_held}|{Recording}|{secs}|{count}|{Mathf.RoundToInt(_fov)}|{Shots}|{Videos}|{_error}";
            if (key == _lastState) return;
            _lastState = key;
            link.Send(Msg.Make(Msg.VrCameraState).Set("on", On).Set("video", link.CameraVideo).Set("held", _held >= 0).Set("hand", _held)
                .Set("recording", Recording).Set("seconds", secs).Set("countdown", count).Set("fov", Mathf.Round(_fov)).Set("shots", Shots).Set("videos", Videos).Set("error", _error));
        }

        /// <summary>렌즈가 내가 보는 쪽, 화면이 나를 보게 내 앞에.</summary>
        void Summon(VrLink.Device head)
        {
            var f = head.Rotation * Vector3.forward;
            var flat = new Vector3(f.x, 0, f.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            _go.transform.localPosition = head.Position + flat * (SummonDistance + (_size - 1) * 0.15f) + Vector3.down * 0.12f;   // 크게 하면 조금 더 멀리
            _go.transform.localRotation = Quaternion.LookRotation(flat);
        }

        // ---- 그리기 ----

        /// <summary>VrView.Render에서 눈을 그리기 전에 (PhysBone 다음): 렌즈 자리 → 영상 한 장 · 사진 · 미리보기 화면 · 화면 표시.</summary>
        public void Render(Camera main, int mask, string folder, int width, int height, VrLink link, Transform rig = null)
        {
            if (!On) return;
            bool portrait = link.CameraPortrait, stream = link.CameraStream;
            if (!_layoutDone || portrait != _layoutPortrait || stream != _layoutStream) Layout(portrait, stream);
            var t = _go.transform;
            float scale = t.lossyScale.x;
            // 렌즈 자리: 앞면(셀카면 화면 쪽) · 나를 보기면 내 머리를 향해 · 손떨림 보정이면 부드럽게
            Vector3 pos; Quaternion rot;
            if (link.CameraFlip) { pos = t.position - t.forward * (BodySize.z * 0.5f * scale); rot = t.rotation * Quaternion.Euler(0, 180, 0); }
            else { pos = t.position + t.forward * ((BodySize.z * 0.5f + LensDepth) * scale); rot = t.rotation; }
            if (link.CameraLookAt && _headOk && rig != null)
            {
                var to = rig.TransformPoint(_headPos) - pos;
                if (to.sqrMagnitude > 1e-6f) rot = Quaternion.LookRotation(to, Vector3.up);
            }
            float rate = SmoothRate[Mathf.Clamp(link.CameraSmooth, 0, 3)];
            if (!_lensInit || rate <= 0 || Vector3.Distance(_lensPos, pos) > 1f * scale) { _lensPos = pos; _lensRot = rot; _lensInit = true; }
            else
            {
                float k = 1 - Mathf.Exp(-rate * Time.unscaledDeltaTime);
                _lensPos = Vector3.Lerp(_lensPos, pos, k); _lensRot = Quaternion.Slerp(_lensRot, rot, k);
            }
            _cam.transform.SetPositionAndRotation(_lensPos, _lensRot);
            _cam.fieldOfView = _fov;
            _cam.nearClipPlane = Mathf.Max(0.005f, 0.02f * scale);
            _cam.farClipPlane = Mathf.Max(main.farClipPlane, 100 * scale);
            // 투명 배경 (2026-10-27): 배경(하늘·소품·바닥·함께 세운 아바타)을 빼고 알파 0으로 지운다 — 사진만 투명, 미리보기 화면은 회색 위에
            bool clear = link.CameraTransparent;
            _cam.clearFlags = clear ? CameraClearFlags.SolidColor : main.clearFlags;
            _cam.backgroundColor = clear ? new Color(0, 0, 0, 0) : main.backgroundColor.a > 0 ? main.backgroundColor : new Color(0, 0, 0, 1);
            _cam.cullingMask = clear ? mask & ~VrLayers.SceneryMask : mask;
            Dof(clear ? 0 : link.CameraDof);   // 투명 촬영은 심도 없이 (두 번 그린 차이로 알파를 구한다)
            var src = main.GetComponent<PostFx>();
            VrView.CopyFx(src, _fx);
            _cam.allowHDR = src != null && src.enabled;

            bool drewVideo = RecordTick(link);
            if (_shoot) { _shoot = false; Shoot(folder, portrait ? height : width, portrait ? width : height, link); }
            if (!drewVideo && (stream || (_frameNo++ & 1) == 0))
            {
                _cam.aspect = (float)_preview.width / _preview.height;
                _cam.targetTexture = _preview;
                _cam.Render();
                _cam.targetTexture = null;
            }
            _screenMat.mainTexture = Time.unscaledTime < _flashUntil ? Texture2D.whiteTexture : (Texture)_preview;
            Hud();
        }

        /// <summary>피사계 심도 (2026-10-27): 스튜디오 심도(StudioDof)를 렌즈 카메라에 붙인다. 초점은 늘 자동(아바타 머리).</summary>
        void Dof(float strength)
        {
            var dof = _cam.GetComponent<StudioDof>();
            if (strength <= 0.001f) { if (dof != null) UnityEngine.Object.Destroy(dof); return; }
            if (dof == null) { dof = _cam.gameObject.AddComponent<StudioDof>(); dof.Scene = Scene; dof.Auto = true; }
            dof.Strength = strength;
        }

        /// <summary>자동 초점에 쓸 지금 아바타 (VrView가 넣는다).</summary>
        public SceneBuilder Scene;

        /// <summary>화면 표시: 녹화 = 빨간 점·테두리·앞 표시등(깜빡임) · 타이머 = 노란 테두리(깜빡임, 끝이 가까울수록 빠르게).</summary>
        void Hud()
        {
            float now = Time.unscaledTime;
            bool rec = Recording, count = _countEnd > 0;
            bool blink = rec ? (now % 1f) < 0.6f : count && (now % ((_countEnd - now) < 1.5f ? 0.25f : 0.5f)) < (((_countEnd - now) < 1.5f) ? 0.12f : 0.3f);
            _dot.SetActive(rec && blink);
            bool frame = rec || (count && blink);
            if (frame) _frameMat.color = rec ? new Color(0.9f, 0.12f, 0.12f) : new Color(1f, 0.8f, 0.15f);
            foreach (var f in _frame) f.SetActive(frame);
            _ledMat.color = rec ? (blink ? new Color(1f, 0.1f, 0.1f) : new Color(0.35f, 0.04f, 0.04f)) : count && blink ? new Color(1f, 0.75f, 0.1f) : new Color(0.18f, 0.06f, 0.06f);
        }

        /// <summary>화면 크기·격자·테두리를 방향(가로·세로)에 맞춘다. 미리보기 텍스처도 (스트림이면 1280×720).</summary>
        void Layout(bool portrait, bool stream)
        {
            _layoutPortrait = portrait; _layoutStream = stream; _layoutDone = true;
            int lw = stream ? StreamLong : PreviewLong, sw = stream ? StreamShort : PreviewShort;
            int pw = portrait ? sw : lw, ph = portrait ? lw : sw;
            if (_preview == null || _preview.width != pw || _preview.height != ph)
            {
                if (_preview != null) { _preview.Release(); UnityEngine.Object.Destroy(_preview); }
                _preview = new RenderTexture(new RenderTextureDescriptor(pw, ph, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = 2 }) { name = "VrCameraPreview", hideFlags = HideFlags.HideAndDontSave };
                _preview.Create();
            }
            // 화면 (m): 몸체 뒷면 안쪽에 맞춤
            float maxW = BodySize.x - ScreenMargin * 2, maxH = BodySize.y - ScreenMargin * 2;
            float w = maxW, h = maxW * ph / pw;
            if (h > maxH) { h = maxH; w = maxH * pw / ph; }
            var st = _screen.transform;
            st.localScale = new Vector3(w, h, 1);
            // 격자 3분할 (선 0.6 mm) · 테두리 (1.2 mm) · 빨간 점 — 화면 판(1×1) 안 좌표라 크기를 나눠 둔다
            float gx = 0.0006f / w, gy = 0.0006f / h, fx = 0.0012f / w, fy = 0.0012f / h;
            for (int i = 0; i < 4; i++)
            {
                var g = _gridLines[i].transform;
                bool vertical = i < 2; float at = (i % 2 == 0 ? -1f : 1f) / 6f;
                g.localPosition = vertical ? new Vector3(at, 0, -0.0005f) : new Vector3(0, at, -0.0005f);
                g.localScale = vertical ? new Vector3(gx, 1, 1) : new Vector3(1, gy, 1);
            }
            for (int i = 0; i < 4; i++)
            {
                var f = _frame[i].transform;
                bool vertical = i < 2; float at = i % 2 == 0 ? -0.5f : 0.5f;
                f.localPosition = vertical ? new Vector3(at - Mathf.Sign(at) * fx * 0.5f, 0, -0.0007f) : new Vector3(0, at - Mathf.Sign(at) * fy * 0.5f, -0.0007f);
                f.localScale = vertical ? new Vector3(fx, 1, 1) : new Vector3(1, fy, 1);
            }
            float dot = 0.005f;
            _dot.transform.localPosition = new Vector3(-0.5f + (0.004f + dot * 0.5f) / w, 0.5f - (0.004f + dot * 0.5f) / h, -0.0009f);   // 화면 왼쪽 위 (뒤에서 보아)
            _dot.transform.localScale = new Vector3(dot / w, dot / h, 1);
        }

        /// <summary>격자 켜고 끄기 (VrView가 프레임마다 — 설정이 바뀔 때만 실제로 바뀜).</summary>
        public void Grid(bool on) { foreach (var g in _gridLines) if (g.activeSelf != on) g.SetActive(on); }

        void Shoot(string folder, int width, int height, VrLink link)
        {
            if (string.IsNullOrEmpty(folder)) { link.Send(Msg.Make(Msg.VrCameraShot).Set("ok", false).Set("error", "사진 폴더가 없어요")); return; }
            width = Mathf.Clamp(width, 256, 7680); height = Mathf.Clamp(height, 256, 7680);
            if (link.CameraTransparent) { ShootTransparent(folder, width, height); return; }
            var draw = RenderTexture.GetTemporary(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = 8 });
            var flat = RenderTexture.GetTemporary(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = true });
            _cam.aspect = (float)width / height;
            _cam.targetTexture = draw;
            _cam.Render();
            _cam.targetTexture = null;
            Graphics.Blit(draw, flat);   // MSAA 해상
            RenderTexture.ReleaseTemporary(draw);
            var path = Path.Combine(folder, "VR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            _flashUntil = Time.unscaledTime + 0.12f;
            Shots++;
            AsyncGPUReadback.Request(flat, 0, TextureFormat.RGBA32, r =>
            {
                RenderTexture.ReleaseTemporary(flat);
                if (r.hasError) { _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", false).Set("error", "화면을 읽지 못했어요")); return; }
                var bytes = r.GetData<byte>().ToArray();
                Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        File.WriteAllBytes(path, Capture.EncodePng(bytes, width, height));
                        _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", true).Set("width", width).Set("height", height));
                    }
                    catch (Exception e) { _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", false).Set("error", e.Message)); }
                });
            });
            Debug.Log($"[VrcProto] VR 카메라 찍기: {path} ({width}×{height}, 화각 {_fov:0}°)");
        }

        Capture _matte;

        /// <summary>투명 배경 사진 (2026-10-27): 스튜디오 투명 촬영과 같은 검정·흰 두 번 그리기(Capture) — 블룸만 빠지고 색 보정은 다시 넣는다. 찍는 순간 잠깐 멈춘다.</summary>
        void ShootTransparent(string folder, int width, int height)
        {
            _matte ??= new Capture(_cam, null);
            _cam.aspect = (float)width / height;
            var path = Path.Combine(folder, "VR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            _flashUntil = Time.unscaledTime + 0.12f;
            Shots++;
            byte[] bytes;
            try { bytes = _matte.Pixels(width, height, true, 1); }
            catch (Exception e) { _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", false).Set("error", e.Message)); return; }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(path, Capture.EncodePng(bytes, width, height));
                    _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", true).Set("width", width).Set("height", height).Set("transparent", true));
                }
                catch (Exception e) { _done.Enqueue(Msg.Make(Msg.VrCameraShot).Set("path", path).Set("ok", false).Set("error", e.Message)); }
            });
            Debug.Log($"[VrcProto] VR 카메라 찍기 (투명): {path} ({width}×{height})");
        }

        // ---- 영상 ----

        void StartRecording(VrLink link)
        {
            if (Recording || _finishing != null) { _error = _finishing != null ? "앞 영상을 마무리하는 중이에요 — 잠시 뒤 다시" : ""; return; }
            var pipe = link.CameraPipe;
            if (pipe.Length == 0) { _error = "영상 준비 중이에요 — 잠시 뒤 다시 눌러 주세요"; return; }
            link.CameraPipe = "";   // 한 번 쓰면 끝 (Host가 끝난 뒤 새로 연다)
            bool portrait = link.CameraPortrait;
            _recW = portrait ? VideoShort : VideoLong; _recH = portrait ? VideoLong : VideoShort; _recFps = link.CameraFps;
            int w = _recW, h = _recH, fps = _recFps;
            _stopRec = false; _recError = null;
            bool audio = link.CameraMic && OpenMic(link.CameraMicName);
            _connecting = Task.Run(() => new RecordPipe(pipe, w, h, fps, audio));
            Debug.Log($"[VrcProto] VR 카메라 영상: {w}×{h} {fps}fps{(audio ? " · 소리" : "")} 연결 중 → {pipe}");
        }

        // ---- 영상 소리 (2026-11-01): 말하기가 켜져 있으면 그 마이크, 아니면 녹화하는 동안만 연다 ----
        public Func<LipSync> Lip;
        LipSync _mic; bool _ownMic;
        int _recBehind, _recBehindPending, _recBehindInflight;   // 로그용

        bool OpenMic(string device)
        {
            var shared = Lip?.Invoke();
            if (shared != null && shared.Ok) { _mic = shared; _ownMic = false; return true; }
            var own = new LipSync(device);
            if (own.Ok) { _mic = own; _ownMic = true; return true; }
            _error = "소리 없이 찍어요: " + own.Text;
            return false;
        }

        float[] CloseMic()
        {
            var tape = _mic?.StopTape();
            if (_ownMic) _mic?.Dispose();
            _mic = null; _ownMic = false;
            return tape;
        }

        void StopRecording()
        {
            if (_connecting != null && _rec == null) _stopRec = true;   // 연결되면 바로 끝낸다
            if (_rec != null) _stopRec = true;
        }

        /// <summary>Render에서: 연결 마무리 → 장 시각이 됐으면 한 장 그려 보낸다 (그렸으면 true — 미리보기도 그 그림으로). 멈추면 걸어 둔 읽기를 기다려 끝 레코드.</summary>
        bool RecordTick(VrLink link)
        {
            PollConnect(link);
            if (_rec == null) return false;
            _mic?.KeepTape();
            if (_rec.Error != null && _recError == null) { _recError = _rec.Error; _stopRec = true; }
            if (_stopRec)
            {
                if (_inflight > 0) return false;   // 걸어 둔 GPU 읽기를 마저 보낸다
                FinishRecording(_recError == null && _written > 0);
                return false;
            }
            int due = Mathf.FloorToInt((Time.realtimeSinceStartup - _recT0) * _recFps);
            if (due < _written) return false;   // 다음 장 시각 전
            if (_rec.Pending + _inflight >= 4) { _recBehind++; _recBehindPending = _rec.Pending; _recBehindInflight = _inflight; return false; }   // Host가 뒤처짐 — 다음 장이 밀린 만큼 되풀이한다
            if (!EnsurePack()) { _recError = "촬영 셰이더가 빌드에 없어요 (Hidden/VrcProto/CaptureResolve)"; _stopRec = true; return false; }
            int count = due + 1 - _written;
            var src = RenderTexture.GetTemporary(new RenderTextureDescriptor(_recW, _recH, RenderTextureFormat.ARGBHalf, 24) { sRGB = false, msaaSamples = 4 });
            _cam.aspect = (float)_recW / _recH;
            _cam.targetTexture = src;
            _cam.Render();
            _cam.targetTexture = null;
            Graphics.Blit(src, _packRt, _pack, 3);   // CaptureResolve Pack: 위아래 뒤집기 · sRGB · BGRA
            Graphics.Blit(src, _preview);            // 화면에도 지금 찍히는 그림
            RenderTexture.ReleaseTemporary(src);
            _inflight++;
            var rp = _rec; int cnt = count;
            AsyncGPUReadback.Request(_packRt, 0, TextureFormat.RGBA32, r =>
            {
                _inflight--;
                if (r.hasError) { _recError ??= "GPU에서 영상 프레임을 읽지 못했어요"; return; }
                if (rp.Done) return;
                var buf = rp.Rent();
                r.GetData<byte>().CopyTo(buf);
                rp.Frame(buf, cnt);
            });
            _written += count;
            return true;
        }

        /// <summary>스레드에서 걸어 둔 파이프 연결이 끝났으면: 실패 = 알림 · 성공 = 녹화 시작 (그 사이 멈춤을 눌렀으면 바로 끝).</summary>
        void PollConnect(VrLink link)
        {
            if (_connecting != null && _connecting.IsCompleted)
            {
                var task = _connecting; _connecting = null;
                if (task.IsFaulted || task.IsCanceled)
                {
                    var why = task.Exception?.GetBaseException().Message ?? "취소됨";
                    CloseMic();
                    _error = "영상을 시작하지 못했어요: " + why;
                    link.Send(Msg.Make(Msg.VrCameraVideo).Set("stopped", true).Set("ok", false).Set("frames", 0).Set("seconds", 0).Set("error", why));
                    return;
                }
                _rec = task.Result; _recT0 = Time.realtimeSinceStartup; _written = 0; _inflight = 0;
                _mic?.StartTape();   // 첫 장과 같은 때부터
                link.Send(Msg.Make(Msg.VrCameraVideo).Set("started", true).Set("width", _recW).Set("height", _recH).Set("fps", _recFps));
                Debug.Log("[VrcProto] VR 카메라 영상 시작");
            }
            if (_rec != null && _stopRec && !On) FinishRecording(_written > 0);
        }

        void FinishRecording(bool keep)
        {
            var rp = _rec; _rec = null; _stopRec = false;
            var tape = CloseMic();
            if (rp == null) return;
            if (!keep && _recError == null) _recError = _written == 0 ? "찍힌 장이 없어요" : "멈췄어요";
            _finishFrames = _written; _finishSeconds = _recFps > 0 ? (float)_written / _recFps : 0;
            if (keep && tape != null && tape.Length > 0) rp.Audio(Wav.To48k(tape), Wav.OutRate);
            if (keep) rp.Finish(); else rp.Abort();
            _finishing = rp;
            Debug.Log($"[VrcProto] VR 카메라 영상 끝: {_written}장 ({_finishSeconds:0.0}초) · 흐른 {Time.realtimeSinceStartup - _recT0:0.00}초 · 뒤처짐 {_recBehind}번(보내는 중 {_recBehindPending}, GPU 읽기 {_recBehindInflight}){(tape != null ? $" · 소리 {tape.Length / (float)LipSync.TapeRate:0.0}초" : "")}{(keep ? "" : " · 취소 " + _recError)}");
            _recBehind = 0;
        }

        bool EnsurePack()
        {
            if (_pack == null)
            {
                var s = Shader.Find("Hidden/VrcProto/CaptureResolve");
                if (s == null) return false;
                _pack = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                _pack.SetInt("_Scale", 1);
            }
            if (_packRt == null || _packRt.width != _recW || _packRt.height != _recH)
            {
                if (_packRt != null) { _packRt.Release(); UnityEngine.Object.Destroy(_packRt); }
                _packRt = new RenderTexture(new RenderTextureDescriptor(_recW, _recH, RenderTextureFormat.ARGB32, 0) { sRGB = false }) { name = "VrCameraBGRA", hideFlags = HideFlags.HideAndDontSave };
                _packRt.Create();
            }
            return true;
        }

        // ---- 몸체 ----

        void Make(Transform rig)
        {
            _go = new GameObject("VrCamera") { layer = VrLayers.VrOnly };
            _go.transform.SetParent(rig, false);
            // 몸체·렌즈·셔터 단추: 메시 하나 (빛을 UV에 구움)
            var body = Child(_go, "Body", Vector3.zero, Vector3.one);
            body.AddComponent<MeshFilter>().sharedMesh = BodyMesh();
            _bodyMat = new Material(Shader.Find("Unlit/Texture")) { hideFlags = HideFlags.HideAndDontSave, mainTexture = ShadeTexture() };
            Renderer(body, _bodyMat);
            // 앞 녹화 표시등 (렌즈 왼쪽 위, 앞에서 보아)
            _led = Child(_go, "Led", new Vector3(BodySize.x * 0.5f - 0.014f, BodySize.y * 0.5f - 0.012f, BodySize.z * 0.5f + 0.0008f), new Vector3(0.0045f, 0.0045f, 1));
            _led.AddComponent<MeshFilter>().sharedMesh = DiskMesh(true);
            _ledMat = Flat(new Color(0.18f, 0.06f, 0.06f));
            Renderer(_led, _ledMat);
            // 뒷면 화면 (-Z 쪽에서 보이는 판) + 격자 · 테두리 · 빨간 점
            _screen = Child(_go, "Screen", new Vector3(0, 0, -BodySize.z * 0.5f - 0.0008f), Vector3.one);
            _screen.AddComponent<MeshFilter>().sharedMesh = QuadMesh();
            _screenMat = new Material(Shader.Find("Unlit/Texture")) { hideFlags = HideFlags.HideAndDontSave };
            Renderer(_screen, _screenMat);
            _gridMat = Flat(new Color(0.85f, 0.85f, 0.85f));
            for (int i = 0; i < 4; i++) { var g = Child(_screen, "Grid" + i, Vector3.zero, Vector3.one); g.AddComponent<MeshFilter>().sharedMesh = QuadMesh(); Renderer(g, _gridMat); g.SetActive(false); _gridLines.Add(g); }
            _frameMat = Flat(Color.red);
            for (int i = 0; i < 4; i++) { var f = Child(_screen, "Frame" + i, Vector3.zero, Vector3.one); f.AddComponent<MeshFilter>().sharedMesh = QuadMesh(); Renderer(f, _frameMat); f.SetActive(false); _frame[i] = f; }
            _dot = Child(_screen, "Rec", Vector3.zero, Vector3.one);
            _dot.AddComponent<MeshFilter>().sharedMesh = DiskMesh(false);
            _dotMat = Flat(new Color(0.95f, 0.1f, 0.1f));
            Renderer(_dot, _dotMat);
            _dot.SetActive(false);
            var cg = new GameObject("VrCameraLens") { hideFlags = HideFlags.HideAndDontSave };
            _cam = cg.AddComponent<Camera>();
            _cam.enabled = false;
            _fx = cg.AddComponent<PostFx>();
            _layoutDone = false;
        }

        static GameObject Child(GameObject parent, string name, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject(name) { layer = VrLayers.VrOnly };
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos; go.transform.localScale = scale;
            return go;
        }

        static Material Flat(Color c) => new Material(Shader.Find("Unlit/Color")) { hideFlags = HideFlags.HideAndDontSave, color = c };

        static void Renderer(GameObject go, Material mat)
        {
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>시험용 (E2E vrlink, 헤드셋 없이): 카메라를 미리보기 카메라 자리에 놓고 한 장 찍는다(vrCameraShot) + 카메라 모형을 뒤·앞에서 본 그림(vr_camera_back·front.png).
        /// 파이프가 있으면(cameraPipe) 1초 남짓 영상도 (vrCameraVideo) — 이때는 RenderServer가 프레임마다 TestTick을 부른다.</summary>
        public void Test(Transform rig, Camera main, string folder, int width, int height, VrLink link)
        {
            if (_go == null) Make(rig);
            _go.SetActive(true);
            var t = _go.transform;
            t.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            _shoot = true;
            int mask = TestMask(main);
            bool grid = link.CameraGrid;
            Grid(true);   // 모형 그림에 격자도
            Render(main, mask, folder, width, height, link);
            Render(main, mask, folder, width, height, link);   // 미리보기 화면도 한 번 (두 프레임에 한 번)
            _flashUntil = 0; _screenMat.mainTexture = _preview;   // 찍은 뒤 하얀 화면 대신 미리보기를 보이게
            _dot.SetActive(true); foreach (var f in _frame) f.SetActive(true); _frameMat.color = new Color(0.9f, 0.12f, 0.12f);   // 녹화 표시도 그림에
            // 모형: 눈 전용 레이어만 보는 카메라로 뒤(화면)·앞(렌즈)에서
            var go = new GameObject("VrCameraTestView") { hideFlags = HideFlags.HideAndDontSave };
            var c = go.AddComponent<Camera>();
            c.enabled = false; c.cullingMask = VrLayers.VrOnlyMask; c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(0.55f, 0.6f, 0.65f, 1);
            c.fieldOfView = 40; c.nearClipPlane = 0.01f; c.aspect = 4f / 3;
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(640, 480, RenderTextureFormat.ARGB32, 24) { sRGB = true, msaaSamples = 4 });
            var tex = new Texture2D(640, 480, TextureFormat.RGBA32, false);
            foreach (var (name, dir) in new[] { ("vr_camera_back.png", -1f), ("vr_camera_front.png", 1f), ("vr_camera_side.png", 0f) })
            {
                var off = dir == 0 ? (t.right * 0.22f + t.forward * 0.12f + t.up * 0.07f) : (t.forward * dir * 0.3f + t.up * 0.08f);
                var eye = t.position + off * t.lossyScale.x;
                c.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(t.position - eye, t.up));
                c.targetTexture = rt; c.Render(); c.targetTexture = null;
                var flat = RenderTexture.GetTemporary(640, 480, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(rt, flat);
                var prev = RenderTexture.active; RenderTexture.active = flat;
                tex.ReadPixels(new Rect(0, 0, 640, 480), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(flat);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name), tex.EncodeToPNG());
            }
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(go);
            Grid(grid);
            _dot.SetActive(false); foreach (var f in _frame) f.SetActive(false);
            if (link.CameraPipe.Length > 0) { _testVideoUntil = Time.realtimeSinceStartup + 1.2f; StartRecording(link); }
            _go.SetActive(link.CameraOn || _testVideoUntil > 0);
        }

        float _testVideoUntil;
        static int TestMask(Camera main) => (main.cullingMask == 0 ? ~VrLayers.HiddenFromPreview : main.cullingMask) & ~VrLayers.VrOnlyMask & ~VrLayers.FirstPersonMask | VrLayers.ThirdPersonMask;

        /// <summary>시험 영상 (VR을 보지 않을 때 RenderServer가 프레임마다): 1.2초 녹화하고 끝낸 뒤 알린다.</summary>
        public void TestTick(Camera main, VrLink link)
        {
            if (_testVideoUntil <= 0) return;
            Render(main, TestMask(main), link.CameraFolder, link.CameraWidth, link.CameraHeight, link);
            if (Time.realtimeSinceStartup >= _testVideoUntil && _rec != null) StopRecording();
            Flush(link);
            if (_testVideoUntil > 0 && !Recording && _finishing == null && Time.realtimeSinceStartup >= _testVideoUntil) { _testVideoUntil = 0; _go.SetActive(link.CameraOn); }
        }

        public void End()
        {
            if (_rec != null) FinishRecording(_written > 0);   // VR을 끄면 찍던 영상은 그대로 저장 (걸어 둔 몇 장은 빠질 수 있다)
            else if (_connecting != null) _stopRec = true;
            if (_go != null) _go.SetActive(false);
            _held = -1; _shoot = false; _countEnd = -1;
        }

        // ---- 메시 ----
        static Mesh _body, _quad, _disk, _diskFront;
        static Texture2D _shade;
        // 셰이딩 텍스처 줄: 0 몸체 · 1 고무(옆 띠) · 2 렌즈 금속 · 3 렌즈 유리 · 4 셔터 단추 · 5 강조 고리
        const int ShadeRows = 6;
        static readonly Color[] ShadeColor =
        {
            new Color(0.17f, 0.18f, 0.2f), new Color(0.08f, 0.085f, 0.095f), new Color(0.62f, 0.64f, 0.68f),
            new Color(0.03f, 0.05f, 0.09f), new Color(0.85f, 0.86f, 0.88f), new Color(0.25f, 0.62f, 0.95f),
        };
        static readonly Vector3 LightDir = new Vector3(0.35f, 0.8f, -0.48f).normalized;   // 몸체 기준 (위·화면 쪽에서) — 몸체와 함께 돈다

        /// <summary>가로 = 밝기(어두움 → 밝음), 세로 = 재질 줄. 유리 줄은 밝은 쪽 끝에 하이라이트.</summary>
        static Texture2D ShadeTexture()
        {
            if (_shade != null) return _shade;
            const int W = 64;
            _shade = new Texture2D(W, ShadeRows, TextureFormat.RGBA32, false, false) { name = "VrCameraShade", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[W * ShadeRows];
            for (int r = 0; r < ShadeRows; r++)
                for (int x = 0; x < W; x++)
                {
                    float s = x / (W - 1f);
                    var c = ShadeColor[r] * (0.38f + 0.75f * s);
                    if (r == 3 && s > 0.9f) c = Color.Lerp(c, new Color(0.6f, 0.7f, 0.9f), (s - 0.9f) * 6f);
                    c.a = 1; px[r * W + x] = c;
                }
            _shade.SetPixels(px); _shade.Apply(false, true);
            return _shade;
        }

        sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> T = new List<int>();
            public int Add(Vector3 p, Vector3 n, int row)
            {
                float s = Mathf.Clamp01(Vector3.Dot(n.normalized, LightDir) * 0.5f + 0.5f);
                V.Add(p); Uv.Add(new Vector2(s, (row + 0.5f) / ShadeRows));
                return V.Count - 1;
            }
            /// <summary>삼각형 (감김은 바깥이 앞이 되게 자동으로 — 면 법선 out과 맞춘다).</summary>
            public void Tri(int a, int b, int c, Vector3 out_)
            {
                var n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (Vector3.Dot(n, out_) < 0) { T.Add(a); T.Add(c); T.Add(b); } else { T.Add(a); T.Add(b); T.Add(c); }
            }
            public void Quad(int a, int b, int c, int d, Vector3 out_) { Tri(a, b, c, out_); Tri(a, c, d, out_); }
            public Mesh Mesh(string name)
            {
                var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
                m.SetVertices(V); m.SetUVs(0, Uv); m.SetTriangles(T, 0);
                m.RecalculateNormals(); m.RecalculateBounds();
                return m;
            }
        }

        /// <summary>둥근 사각형 둘레 (xy, 반시계, 모서리마다 seg칸) 와 그 점의 바깥 법선.</summary>
        static List<(Vector2 P, Vector2 N)> RoundRect(float w, float h, float r, int seg)
        {
            var pts = new List<(Vector2, Vector2)>();
            var centers = new[] { new Vector2(w / 2 - r, h / 2 - r), new Vector2(-w / 2 + r, h / 2 - r), new Vector2(-w / 2 + r, -h / 2 + r), new Vector2(w / 2 - r, -h / 2 + r) };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i <= seg; i++)
                {
                    float a = (c * 90f + 90f * i / seg) * Mathf.Deg2Rad;
                    var n = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    pts.Add((centers[c] + n * r, n));
                }
            return pts;
        }

        /// <summary>몸체(둥근 모서리, 옆 고무 띠) · 렌즈 통·금속 고리·유리 · 셔터 단추. 1 = 1 m.</summary>
        static Mesh BodyMesh()
        {
            if (_body != null) return _body;
            var b = new Builder();
            float hz = BodySize.z / 2;
            var ring = RoundRect(BodySize.x, BodySize.y, Corner, 6);
            // 앞·뒤 판 (가운데 점 부채꼴) · 옆면 (앞쪽 3/5는 몸체 색, 뒤쪽 2/5는 고무 띠, 모서리는 살짝 깎음)
            foreach (var z in new[] { hz, -hz })
            {
                var n = new Vector3(0, 0, Mathf.Sign(z));
                int c = b.Add(new Vector3(0, 0, z), n, 0);
                var idx = new List<int>();
                foreach (var (p, _) in ring) idx.Add(b.Add(new Vector3(p.x * 0.985f, p.y * 0.975f, z), n, 0));
                for (int i = 0; i < idx.Count; i++) b.Tri(c, idx[i], idx[(i + 1) % idx.Count], n);
            }
            float bevel = 0.0025f;
            var zs = new[] { (hz, 0.985f, 0.975f, 0), (hz - bevel, 1f, 1f, 0), (-hz * 0.2f, 1f, 1f, 0), (-hz * 0.2f, 1f, 1f, 1), (-hz + bevel, 1f, 1f, 1), (-hz, 0.985f, 0.975f, 1) };
            for (int s = 0; s + 1 < zs.Length; s++)
            {
                if (zs[s].Item1 == zs[s + 1].Item1) continue;   // 재질 바뀌는 자리 (같은 z)
                var (z0, sx0, sy0, row) = zs[s]; var (z1, sx1, sy1, _) = zs[s + 1];
                for (int i = 0; i < ring.Count; i++)
                {
                    var (p, n) = ring[i]; var (q, m) = ring[(i + 1) % ring.Count];
                    var nn = new Vector3(n.x, n.y, 0); var mm = new Vector3(m.x, m.y, 0);
                    int a = b.Add(new Vector3(p.x * sx0, p.y * sy0, z0), nn, row), bb = b.Add(new Vector3(q.x * sx0, q.y * sy0, z0), mm, row);
                    int c = b.Add(new Vector3(q.x * sx1, q.y * sy1, z1), mm, row), d = b.Add(new Vector3(p.x * sx1, p.y * sy1, z1), nn, row);
                    b.Quad(a, bb, c, d, (nn + mm) * 0.5f);
                }
            }
            // 렌즈: 앞면 가운데에서 조금 오른쪽(앞에서 보아 왼쪽)으로 — 통(금속) · 강조 고리 · 앞 고리(금속) · 안쪽 통 · 유리
            var lc = new Vector2(-BodySize.x * 0.12f, -0.002f);
            Cylinder(b, lc, LensR, hz, hz + LensDepth * 0.8f, 2);
            Cylinder(b, lc, LensR * 1.02f, hz + LensDepth * 0.8f, hz + LensDepth, 5);
            Annulus(b, lc, LensR * 0.74f, LensR * 1.02f, hz + LensDepth, 2);
            Cylinder(b, lc, LensR * 0.74f, hz + LensDepth * 0.55f, hz + LensDepth, 1, inward: true);
            Disk(b, lc, LensR * 0.74f, hz + LensDepth * 0.55f, 3);
            Annulus(b, lc, LensR * 1.18f, LensR * 1.32f, hz + 0.0006f, 5);   // 몸체 위 얇은 강조 고리
            // 셔터 단추: 위면 오른쪽(뒤에서 보아)
            var top = BodySize.y / 2;
            CylinderY(b, new Vector2(BodySize.x * 0.3f, 0), 0.0075f, top, top + 0.004f, 4);
            _body = b.Mesh("VrCameraBody");
            return _body;
        }

        const int CircleSeg = 28;

        static void Cylinder(Builder b, Vector2 c, float r, float z0, float z1, int row, bool inward = false)
        {
            for (int i = 0; i < CircleSeg; i++)
            {
                float a0 = i * Mathf.PI * 2 / CircleSeg, a1 = (i + 1) * Mathf.PI * 2 / CircleSeg;
                var n0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0); var n1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0);
                if (inward) { n0 = -n0; n1 = -n1; }
                float s = inward ? -r : r;
                int a = b.Add(new Vector3(c.x + n0.x * s, c.y + n0.y * s, z0), n0, row), bb = b.Add(new Vector3(c.x + n1.x * s, c.y + n1.y * s, z0), n1, row);
                int cc = b.Add(new Vector3(c.x + n1.x * s, c.y + n1.y * s, z1), n1, row), d = b.Add(new Vector3(c.x + n0.x * s, c.y + n0.y * s, z1), n0, row);
                b.Quad(a, bb, cc, d, (n0 + n1) * 0.5f);
            }
        }

        static void CylinderY(Builder b, Vector2 cxz, float r, float y0, float y1, int row)
        {
            var up = Vector3.up;
            int center = b.Add(new Vector3(cxz.x, y1, cxz.y), up, row);
            for (int i = 0; i < CircleSeg; i++)
            {
                float a0 = i * Mathf.PI * 2 / CircleSeg, a1 = (i + 1) * Mathf.PI * 2 / CircleSeg;
                var n0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)); var n1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                var p0 = new Vector3(cxz.x, 0, cxz.y) + n0 * r; var p1 = new Vector3(cxz.x, 0, cxz.y) + n1 * r;
                int a = b.Add(p0 + up * y0, n0, row), bb = b.Add(p1 + up * y0, n1, row), cc = b.Add(p1 + up * y1, n1, row), d = b.Add(p0 + up * y1, n0, row);
                b.Quad(a, bb, cc, d, (n0 + n1) * 0.5f);
                b.Tri(center, b.Add(p0 + up * y1, up, row), b.Add(p1 + up * y1, up, row), up);
            }
        }

        static void Annulus(Builder b, Vector2 c, float r0, float r1, float z, int row)
        {
            var n = Vector3.forward;
            for (int i = 0; i < CircleSeg; i++)
            {
                float a0 = i * Mathf.PI * 2 / CircleSeg, a1 = (i + 1) * Mathf.PI * 2 / CircleSeg;
                var d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                int a = b.Add(new Vector3(c.x + d0.x * r0, c.y + d0.y * r0, z), n, row), bb = b.Add(new Vector3(c.x + d1.x * r0, c.y + d1.y * r0, z), n, row);
                int cc = b.Add(new Vector3(c.x + d1.x * r1, c.y + d1.y * r1, z), n, row), d = b.Add(new Vector3(c.x + d0.x * r1, c.y + d0.y * r1, z), n, row);
                b.Quad(a, bb, cc, d, n);
            }
        }

        static void Disk(Builder b, Vector2 c, float r, float z, int row)
        {
            // 유리: 가운데는 어둡게, 왼쪽 위 가장자리는 하이라이트 (법선을 살짝 기울여 밝기를 다르게)
            int center = b.Add(new Vector3(c.x, c.y, z), new Vector3(0, 0, 1), row);
            for (int i = 0; i < CircleSeg; i++)
            {
                float a0 = i * Mathf.PI * 2 / CircleSeg, a1 = (i + 1) * Mathf.PI * 2 / CircleSeg;
                var d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0); var d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0);
                int p = b.Add(new Vector3(c.x, c.y, z) + d0 * r, d0 * 0.9f + Vector3.forward * 0.3f, row);
                int q = b.Add(new Vector3(c.x, c.y, z) + d1 * r, d1 * 0.9f + Vector3.forward * 0.3f, row);
                b.Tri(center, p, q, Vector3.forward);
            }
        }

        /// <summary>1×1 판 (앞면이 -Z — VrMirror와 같다).</summary>
        static Mesh QuadMesh()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "VrCameraQuad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _quad.triangles = new[] { 0, 3, 1, 3, 0, 2 };
            _quad.RecalculateBounds();
            return _quad;
        }

        /// <summary>지름 1 원판 — front = 앞면이 +Z(몸체 앞쪽 표시등), 아니면 -Z(화면 위 점).</summary>
        static Mesh DiskMesh(bool front)
        {
            ref var m = ref front ? ref _diskFront : ref _disk;
            if (m != null) return m;
            var v = new List<Vector3> { Vector3.zero };
            var t = new List<int>();
            for (int i = 0; i < 20; i++) { float a = i * Mathf.PI * 2 / 20; v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * 0.5f); }
            for (int i = 0; i < 20; i++)
            {
                int a = i + 1, c = (i + 1) % 20 + 1;
                // Unity 앞면은 시계 방향으로 보이는 쪽: xy에서 반시계(0, a, c)는 +Z 쪽에서 시계로 보인다 (QuadMesh와 같은 규칙)
                if (front) { t.Add(0); t.Add(a); t.Add(c); } else { t.Add(0); t.Add(c); t.Add(a); }
            }
            m = new Mesh { name = front ? "VrCameraDiskFront" : "VrCameraDisk", hideFlags = HideFlags.HideAndDontSave };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateBounds();
            return m;
        }
    }
}
