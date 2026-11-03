// 월드 환경 레이어 (PLAN 3.8). 전역 셰이더 상태·조명의 단일 소유자. Provider: UnityLighting.cs(조명), PostProcess.cs(후처리),
// EnvLights.cs(추가 조명·VRC 라이트 볼륨·VRChat 전역 값), EnvAudioLink.cs(AudioLink).
// 규칙: RenderSettings / Shader.SetGlobal* 은 Provider가 이 클래스를 거쳐서만 바꾼다.
// Apply = 기본값으로 완전 리셋 → 목록 순서대로 각 Provider 적용 (잔류 값·충돌 방지).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public interface IEnvironmentProvider
    {
        string Id { get; }
        int Version { get; }
        string Label { get; }
        JNode ParamSchema();                 // Host가 패널을 자동 생성
        void Apply(JNode param, EnvironmentManager env);
        void Remove(EnvironmentManager env);
        void Update(float dt);
    }

    public sealed class EnvironmentManager
    {
        readonly Dictionary<string, IEnvironmentProvider> _providers = new Dictionary<string, IEnvironmentProvider>();
        readonly List<IEnvironmentProvider> _active = new List<IEnvironmentProvider>();
        readonly HashSet<string> _globals = new HashSet<string>();
        readonly HashSet<string> _globalTextures = new HashSet<string>();
        readonly HashSet<string> _globalVectors = new HashSet<string>();
        public Camera Camera { get; private set; }
        public Color Background { get; set; }

        public void Register(IEnvironmentProvider p) => _providers[p.Id] = p;

        public JNode Schema()
        {
            var arr = JNode.Arr();
            foreach (var p in _providers.Values)
                arr.Add(JNode.Obj().Set("id", p.Id).Set("version", p.Version).Set("label", p.Label).Set("params", p.ParamSchema()));
            return arr;
        }

        public void Apply(JNode[] list, Camera cam)
        {
            Camera = cam;
            foreach (var p in _active) p.Remove(this);
            _active.Clear();
            ResetGlobals();
            foreach (var e in list)
            {
                if (e.Has("enabled") && !e["enabled"].AsBool) continue; // 꺼 둔 항목 (값은 Host가 보관)
                if (!_providers.TryGetValue(e["provider"].AsString, out var p)) { Debug.LogWarning("[VrcProto] 알 수 없는 환경 Provider: " + e["provider"].AsString); continue; }
                p.Apply(e["params"], this);
                _active.Add(p);
            }
            cam.backgroundColor = Background;
        }

        void ResetGlobals()
        {
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.skybox = null;
            var sh = new SphericalHarmonicsL2(); sh.Clear();
            RenderSettings.ambientProbe = sh;
            RenderSettings.ambientIntensity = 1;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.reflectionIntensity = 1;
            RenderSettings.fog = false;
            RenderSettings.sun = null;
            QualitySettings.pixelLightCount = 4; // VRChat 기본 품질과 같게
            foreach (var g in _globals) Shader.SetGlobalFloat(g, 0);
            // 4×4 검정 = 없음 (lilToon은 _AudioTexture 크기가 16보다 작으면 AudioLink가 없다고 본다)
            foreach (var g in _globalTextures) Shader.SetGlobalTexture(g, Texture2D.blackTexture);
            foreach (var g in _globalVectors) Shader.SetGlobalVector(g, Vector4.zero);
            Background = new Color(0.2f, 0.2f, 0.22f, 1);
        }

        /// <summary>Provider가 설정한 전역 float을 기록해 두어 다음 Apply에서 0으로 되돌린다.</summary>
        public void SetGlobalFloat(string name, float v) { _globals.Add(name); Shader.SetGlobalFloat(name, v); }

        /// <summary>Provider가 설정한 전역 텍스처를 기록해 두어 다음 Apply에서 비운다.</summary>
        public void SetGlobalTexture(string name, Texture t) { _globalTextures.Add(name); Shader.SetGlobalTexture(name, t); }

        /// <summary>Provider가 설정한 전역 벡터를 기록해 두어 다음 Apply에서 0으로 되돌린다.</summary>
        public void SetGlobalVector(string name, Vector4 v) { _globalVectors.Add(name); Shader.SetGlobalVector(name, v); }

        public void Update(float dt) { foreach (var p in _active) p.Update(dt); }

        /// <summary>쉬지 않고 바뀌는 환경이 켜져 있다 (AudioLink는 소리에 맞춰 매 프레임 전역 텍스처를 바꾼다). 렌더러가 가만히 있어도 프레임을 줄이지 않게 (2026-11-02).</summary>
        public bool Animating { get { foreach (var p in _active) if (p is AudioLinkProvider) return true; return false; } }
    }
}
