// 렌더 품질 (2026-10-23, setQuality 메시지): 프레임 제한 + 스튜디오 고화질.
//   프레임 제한  설정 창 "렌더러 프레임 제한". 0 = 제한 없음, -1 = 모니터 주사율(VSync), n = 초당 n장. 처음엔 60 (RenderServer.Awake).
//                가만히 있거나 창이 최소화되면 더 낮춘다 (SetRest, 2026-11-02).
//   VRChat 품질 (평소)  빌드 품질 그대로 — 화면이 VRChat에서 보이는 것과 같도록 (MSAA 4×, 픽셀 라이트 4, 그림자 거리 20 m).
//   스튜디오 고화질  Host가 스튜디오 페이지를 여는 동안 켠다. VRChat과 같게 보이는 것은 신경 쓰지 않고 BiRP에서 올릴 수 있는 것은 다 올린다:
//     - MSAA 8×, 픽셀 라이트 제한 없음(64), 이방성 필터 모든 텍스처, 스킨 가중치 제한 없음, LOD 최고
//     - 그림자: 4 캐스케이드 · 해상도 최고 + 그림자 있는 라이트마다 8192 맵 · 바이어스를 줄여 접촉 그림자가 떨어지지 않게 ·
//       그림자 거리를 장면(아바타·소품) 범위에 딱 맞춰 같은 맵을 좁은 곳에 쓴다 (텍셀 밀도↑)
//     - 주변 가림(SSAO, StudioAo): 옷 겹침·목 아래·발밑에 깊이감. 방송 오버레이 중에는 끈다 (머리 장식 아래·얼굴이 거뭇해 보였다, 2026-10-23)
//     - 촬영·녹화: 카메라를 서브픽셀만큼 흔들며 여러 장을 찍어 평균 (Capture.Samples — 외곽선·머리카락 끝 계단과 SSAO 잡티가 사라진다)
//   환경 Provider가 전역 품질(픽셀 라이트 수 등)을 VRChat 값으로 되돌려도 LateUpdate가 다시 맞춘다.
//   그림자 부드럽기(UnityLighting.ShadowSoftness): 맵 해상도 8192 → 512, 바이어스도 같이 올려 얼굴의 얼룩(그림자 여드름)을 없앤다.
//   머리에 붙은 것의 그림자(UnityLighting.AccessoryShadows, 늘, 기본 끔): 끄면 머리 본 아래 렌더러와, 본이 모두 머리 쪽인 스킨드 메시(머리카락·Bone Proxy 장식 —
//   보통 루트 아래에 있다)의 그림자 드리우기를 끈다. 켜거나 장면이 바뀌면 되돌린다. 받는 그림자(몸이 드리우는 것)는 그대로.
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    [RequireComponent(typeof(Camera))]
    public sealed class RenderQuality : MonoBehaviour
    {
        public SceneBuilder Scene;
        public System.Func<Bounds?> PropsBounds;   // 스튜디오 소품 범위 (그림자 거리 맞추기)
        public Capture Capture;
        public bool Studio { get; private set; }
        int _captureAa = 8;
        float _ao = 0.5f;

        // 빌드 품질 (VRChat 품질로 돌아갈 때)
        int _aa, _pixelLights, _cascades;
        float _shadowDist, _lodBias;
        ShadowResolution _shadowRes;
        AnisotropicFiltering _aniso;
        SkinWeights _skin;
        Camera _cam;
        StudioAo _aoFx;
        int _scan;
        Bounds _bounds;
        // 머리 장식 그림자를 끈 렌더러와 원래 값
        readonly System.Collections.Generic.List<(Renderer R, ShadowCastingMode Mode)> _noShadow = new System.Collections.Generic.List<(Renderer, ShadowCastingMode)>();
        Transform _noShadowRoot;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _aa = QualitySettings.antiAliasing; _pixelLights = QualitySettings.pixelLightCount; _cascades = QualitySettings.shadowCascades;
            _shadowDist = QualitySettings.shadowDistance; _lodBias = QualitySettings.lodBias; _shadowRes = QualitySettings.shadowResolution;
            _aniso = QualitySettings.anisotropicFiltering; _skin = QualitySettings.skinWeights;
        }

        public void Handle(JNode m)
        {
            if (m.Has("maxFps")) SetMaxFps(m["maxFps"].AsInt);
            if (m.Has("hidden")) Hidden = m["hidden"].AsBool;
            if (m.Has("ao")) _ao = Mathf.Clamp01(m["ao"].AsFloat);
            if (m.Has("captureAa")) _captureAa = Mathf.Clamp(m["captureAa"].AsInt, 1, 32);
            if (m.Has("studio")) SetStudio(m["studio"].AsBool);
            else if (Studio) SetStudio(true);   // 세기·장 수만 바뀜
        }

        static int _maxFps = 60;
        static bool _vr;

        /// <summary>VR로 보기 중: 프레임은 SteamVR(WaitGetPoses)이 헤드셋 주기에 맞추므로 제한·VSync를 푼다. 끄면 설정한 제한으로.</summary>
        public static bool Vr { get => _vr; set { _vr = value; ApplyFps(); } }

        static void SetMaxFps(int fps)
        {
            _maxFps = fps;
            ApplyFps();
            Debug.Log($"[VrcProto] 프레임 제한: {(fps < 0 ? "VSync" : fps == 0 ? "없음" : fps + " fps")}{(_vr ? " (VR로 보기 중에는 헤드셋 주기)" : "")}");
        }

        static int _overlayFps;

        /// <summary>방송 오버레이가 켜 있는 동안의 제한 (0 = 평소 제한). 게임과 GPU를 나눠 쓰므로 디스코드 공유(보통 30 fps)에 맞춰 낮춘다 (2026-10-24).</summary>
        public static void SetOverlayFps(int fps)
        {
            if (fps == _overlayFps) return;
            _overlayFps = fps;
            ApplyFps();
            if (fps > 0) Debug.Log($"[VrcProto] 오버레이 프레임 제한: {fps} fps");
        }

        // 쉬는 동안 (2026-11-02): 가만히 있어도 초당 60장을 그려 CPU 16코어 중 1.5~2코어(약 10%)를 늘 썼다.
        //   Idle   입력·메시지·흔들림이 잠깐 없으면 IdleFps로 (RenderServer.IdleCheck). 움직이면 다음 프레임부터 원래대로.
        //   Hidden 에디터 창이 최소화되면 HiddenFps로 (setQuality hidden).
        //   VR·방송 오버레이는 늘 제 프레임 (위가 먼저). 녹화·촬영 중(hold)에는 둘 다 쉰다.
        public const int IdleFps = 20, HiddenFps = 5;
        static bool _idle, _hidden, _hold;

        public static bool Hidden { get => _hidden; set { if (_hidden == value) return; _hidden = value; ApplyFps(); } }

        public static void SetRest(bool idle, bool hold)
        {
            if (idle == _idle && hold == _hold) return;
            _idle = idle; _hold = hold;
            ApplyFps();
        }

        static void ApplyFps()
        {
            int fps = _maxFps;
            if (_vr) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
            else if (_overlayFps > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = Mathf.Clamp(_overlayFps, 10, 1000); }
            else if (!_hold && (_hidden || _idle))
            {
                int rest = _hidden ? HiddenFps : IdleFps;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = fps > 0 ? Mathf.Min(Mathf.Clamp(fps, 10, 1000), rest) : rest;
            }
            else if (fps < 0) { QualitySettings.vSyncCount = 1; Application.targetFrameRate = -1; }
            else { QualitySettings.vSyncCount = 0; Application.targetFrameRate = fps == 0 ? -1 : Mathf.Clamp(fps, 10, 1000); }
        }

        void SetStudio(bool on)
        {
            bool changed = on != Studio;
            Studio = on;
            if (Capture != null) Capture.Samples = on ? _captureAa : 1;
            if (on)
            {
                if (_aoFx == null) _aoFx = new StudioAo(_cam);
                _aoFx.Strength = _ao;
                _aoFx.Enabled = _ao > 0.001f;
                _scan = 0;
                Enforce();
            }
            else
            {
                if (_aoFx != null) _aoFx.Enabled = false;
                QualitySettings.antiAliasing = _aa; QualitySettings.pixelLightCount = _pixelLights; QualitySettings.shadowCascades = _cascades;
                QualitySettings.shadowDistance = _shadowDist; QualitySettings.lodBias = _lodBias; QualitySettings.shadowResolution = _shadowRes;
                QualitySettings.anisotropicFiltering = _aniso; QualitySettings.skinWeights = _skin;
                foreach (var l in FindObjectsOfType<Light>()) { l.shadowCustomResolution = -1; l.shadowBias = 0.05f; l.shadowNormalBias = 0.4f; }
            }
            if (changed) Debug.Log($"[VrcProto] 스튜디오 고화질 {(on ? "켬" : "끔")} (주변 가림 {_ao:0.##}, 촬영 누적 {_captureAa}장)");
        }

        void LateUpdate()
        {
            AccessoryShadowsUpdate();
            if (!Studio) return;
            Enforce();
            if (_aoFx != null) _aoFx.Enabled = _ao > 0.001f && !Overlay.On;
        }

        void AccessoryShadowsUpdate()
        {
            var root = Scene != null ? Scene.Root : null;
            bool off = !UnityLightingProvider.AccessoryShadows && root != null;
            if (off == (_noShadowRoot != null) && root == _noShadowRoot) return;
            foreach (var (r, mode) in _noShadow) if (r != null) r.shadowCastingMode = mode;
            _noShadow.Clear();
            _noShadowRoot = null;
            if (!off) return;
            _noShadowRoot = root;
            var head = Scene.HumanBone("Head") ?? Scene.FindHumanoidHead();
            if (head == null) return;
            var headSet = new System.Collections.Generic.HashSet<Transform>(head.GetComponentsInChildren<Transform>(true));
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
                bool onHead = headSet.Contains(r.transform);
                if (!onHead && r is SkinnedMeshRenderer smr) onHead = SkinnedOnHead(smr, headSet);
                if (!onHead) continue;
                _noShadow.Add((r, r.shadowCastingMode));
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
            Debug.Log($"[VrcProto] 머리에 붙은 것의 그림자 끔: 렌더러 {_noShadow.Count}개");
        }

        /// <summary>가중치가 실린 본이 모두 머리 쪽인 스킨드 메시 (본 배열에는 쓰지 않는 뼈대 전체가 들어 있기도 하다).</summary>
        static bool SkinnedOnHead(SkinnedMeshRenderer smr, System.Collections.Generic.HashSet<Transform> head)
        {
            var mesh = smr.sharedMesh; var bones = smr.bones;
            if (mesh == null || bones == null || bones.Length == 0) return false;
            if (!mesh.isReadable) { foreach (var t in bones) if (t != null && !head.Contains(t)) return false; return true; }
            var used = new bool[bones.Length];
            bool any = false;
            foreach (var w in mesh.GetAllBoneWeights())
                if (w.weight > 0.001f && w.boneIndex < used.Length) { used[w.boneIndex] = true; any = true; }
            if (!any) return false;
            for (int i = 0; i < bones.Length; i++) if (used[i] && (bones[i] == null || !head.Contains(bones[i]))) return false;
            return true;
        }

        void Enforce()
        {
            if (QualitySettings.antiAliasing != 8) QualitySettings.antiAliasing = 8;
            if (QualitySettings.pixelLightCount != 64) QualitySettings.pixelLightCount = 64;
            if (QualitySettings.shadowCascades != 4) QualitySettings.shadowCascades = 4;
            if (QualitySettings.shadowResolution != ShadowResolution.VeryHigh) QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            if (QualitySettings.anisotropicFiltering != AnisotropicFiltering.ForceEnable) QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            if (QualitySettings.skinWeights != SkinWeights.Unlimited) QualitySettings.skinWeights = SkinWeights.Unlimited;
            if (QualitySettings.lodBias < 4) QualitySettings.lodBias = 4;
            // 라이트·범위는 가끔만 (환경을 바꾸면 라이트가 새로 생긴다)
            if (_scan-- <= 0)
            {
                _scan = 30;
                foreach (var l in FindObjectsOfType<Light>())
                {
                    if (l.shadows == LightShadows.None) continue;
                    float soft = UnityLightingProvider.ShadowSoftness;
                    int res = Mathf.RoundToInt((l.type == LightType.Directional ? 8192 : 4096) / Mathf.Pow(2, soft * 4));
                    l.shadowCustomResolution = Mathf.Max(256, res);
                    l.shadowBias = Mathf.Lerp(0.02f, 0.08f, soft); l.shadowNormalBias = Mathf.Lerp(0.15f, 0.5f, soft);
                }
                _bounds = Scene != null ? Scene.AllBounds() : default;
                var pb = PropsBounds?.Invoke();
                if (pb.HasValue) { if (_bounds.size == Vector3.zero) _bounds = pb.Value; else _bounds.Encapsulate(pb.Value); }
            }
            // 그림자 거리: 카메라에서 장면 가장 먼 곳 + 낮은 해가 바닥에 늘이는 그림자 길이까지. 장면이 없으면 VRChat과 같은 20 m
            float d = _shadowDist;
            if (_bounds.size != Vector3.zero)
            {
                var sun = RenderSettings.sun;
                float elev = sun != null && sun.isActiveAndEnabled ? Mathf.Asin(Mathf.Clamp(Vector3.Dot(-sun.transform.forward, Vector3.up), -1, 1)) : Mathf.PI / 4;
                float cast = elev <= 0.01f ? 0 : Mathf.Min(_bounds.size.y / Mathf.Tan(elev), 15);   // 해가 지평선 아래면 바닥 그림자 없음
                d = Mathf.Clamp(Vector3.Distance(_cam.transform.position, _bounds.center) + _bounds.extents.magnitude * 1.5f + cast + 0.5f, 2, 40);
            }
            if (Mathf.Abs(QualitySettings.shadowDistance - d) > 0.05f) QualitySettings.shadowDistance = d;
        }

        void OnDestroy() { _aoFx?.Dispose(); }
    }
}
