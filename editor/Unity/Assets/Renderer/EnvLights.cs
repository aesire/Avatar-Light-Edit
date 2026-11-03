// 환경 Provider 셋 (PLAN 3.8, 2026-10-23):
//   "Lights"          추가 조명 — Unity 실시간 포인트·스포트 라이트 (모든 셰이더가 받는다, VRChat 픽셀 라이트 수 제한 4 그대로)
//   "VRCLightVolumes" VRC Light Volumes의 포인트·스팟 라이트 볼륨 — 월드의 LightVolumeManager(Udon)가 하듯 전역 값만 쓴다.
//                     lilToon은 빌드에 든 VRC Light Volumes/LightVolumes.cginc(VRCLV_VERSION 2)로 읽는다. 셰이더 키워드 변형은 필요 없다.
//   "VRChatGlobals"   VRChat 클라이언트가 설정하는 _VRChatCameraMode·_VRChatMirrorMode (거울·카메라에서만 보이거나 숨는 셰이더 기능 확인용)
// 조명 자리는 아바타 둘레 각도(0° = 아바타 정면 +Z)·높이·거리로 정한다 (아바타는 원점에 선다). 스포트는 아바타 쪽 "겨누는 높이"를 비춘다.
// 추가 조명은 8개(2026-10-24, 조명 배치판의 "+ 포인트 라이트"), 라이트 볼륨은 3개. 추가 조명의 "카메라 따라가기"는 매 프레임 미리보기 카메라 자리에 두고
// (스포트는 카메라가 보는 쪽으로) — 카메라를 돌려도 늘 보는 쪽에서 비춘다.
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    /// <summary>조명 슬롯 공통: 스키마(이름 앞에 l1_ …, group으로 묶음)와 슬롯 값 읽기.</summary>
    static class LightSlots
    {
        public const int Count = 8, VolumeCount = 3;

        public struct Slot
        {
            public bool On, Spot, Follow;
            public Color Color;
            public float Intensity, Range, Size, SpotAngle, Falloff;
            public Vector3 Position, Direction;
        }

        public static JNode Schema(bool volume)
        {
            var sb = new StringBuilder("[");
            for (int i = 1; i <= (volume ? VolumeCount : Count); i++)
            {
                string g = $@"""group"": ""{(volume ? "볼륨" : "조명")} {i}""";
                if (i > 1) sb.Append(',');
                sb.Append($@"
                  {{ ""name"": ""l{i}_on"", ""label"": ""켜기"", ""type"": ""bool"", ""default"": {(i == 1 ? "true" : "false")}, {g} }},
                  {{ ""name"": ""l{i}_type"", ""label"": ""종류"", ""type"": ""enum"", ""options"": [""point"", ""spot""], ""optionLabels"": [""포인트 (사방으로)"", ""스포트 (한 방향)""], ""default"": ""point"", {g} }},
                  {{ ""name"": ""l{i}_color"", ""label"": ""색"", ""type"": ""color"", ""default"": [1, 0.85, 0.7], {g} }},
                  {{ ""name"": ""l{i}_intensity"", ""label"": ""세기"", ""type"": ""float"", ""min"": 0, ""max"": {(volume ? 5 : 8)}, ""default"": 1, {g} }},
                  {{ ""name"": ""l{i}_range"", ""label"": ""닿는 거리 (m)"", ""type"": ""float"", ""min"": 0.2, ""max"": 20, ""default"": 4, {g} }},
                  {{ ""name"": ""l{i}_around"", ""label"": ""둘레 각도 (°, 0 = 정면)"", ""type"": ""float"", ""min"": -180, ""max"": 180, ""default"": {DefAround(i)}, {g} }},
                  {{ ""name"": ""l{i}_height"", ""label"": ""높이 (m)"", ""type"": ""float"", ""min"": -1, ""max"": 4, ""default"": 1.5, {g} }},
                  {{ ""name"": ""l{i}_distance"", ""label"": ""아바타에서 거리 (m)"", ""type"": ""float"", ""min"": 0, ""max"": 10, ""default"": 1.2, {g} }},
                  {{ ""name"": ""l{i}_aim"", ""label"": ""스포트가 겨누는 높이 (m)"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1, {g} }},
                  {{ ""name"": ""l{i}_angle"", ""label"": ""스포트 각도 (°)"", ""type"": ""float"", ""min"": 5, ""max"": 170, ""default"": 50, {g} }}");
                if (!volume)
                    sb.Append($@",
                  {{ ""name"": ""l{i}_follow"", ""label"": ""카메라 따라가기"", ""type"": ""bool"", ""default"": false, {g} }}");
                if (volume)
                    sb.Append($@",
                  {{ ""name"": ""l{i}_size"", ""label"": ""빛 크기 (m)"", ""type"": ""float"", ""min"": 0.01, ""max"": 1, ""default"": 0.1, {g} }},
                  {{ ""name"": ""l{i}_falloff"", ""label"": ""스포트 가장자리 부드럽기"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0.5, {g} }}");
            }
            return JNode.Parse(sb.Append(']').ToString());
        }

        public static int DefAround(int i) => i == 1 ? 45 : i == 2 ? -60 : i == 3 ? 180 : 0;

        public static Slot Read(JNode p, int i)
        {
            float F(string k, float def) => p.Has($"l{i}_{k}") ? p[$"l{i}_{k}"].AsFloat : def;
            var s = new Slot
            {
                On = p.Has($"l{i}_on") ? p[$"l{i}_on"].AsBool : i == 1,
                Spot = p.Has($"l{i}_type") && p[$"l{i}_type"].AsString == "spot",
                Follow = p.Has($"l{i}_follow") && p[$"l{i}_follow"].AsBool,
                Color = EnvColor.Read(p[$"l{i}_color"], new Color(1, 0.85f, 0.7f)),
                Intensity = Mathf.Max(0, F("intensity", 1)),
                Range = Mathf.Max(0.05f, F("range", 4)),
                Size = Mathf.Max(0.001f, F("size", 0.1f)),
                SpotAngle = Mathf.Clamp(F("angle", 50), 1, 179),
                Falloff = Mathf.Clamp01(F("falloff", 0.5f)),
            };
            float around = F("around", DefAround(i)) * Mathf.Deg2Rad, dist = Mathf.Max(0, F("distance", 1.2f));
            s.Position = new Vector3(Mathf.Sin(around) * dist, F("height", 1.5f), Mathf.Cos(around) * dist);
            var aim = new Vector3(0, F("aim", 1), 0) - s.Position;
            s.Direction = aim.sqrMagnitude > 1e-6f ? aim.normalized : Vector3.down;
            return s;
        }
    }

    public sealed class LightsProvider : IEnvironmentProvider
    {
        public string Id => "Lights";
        public int Version => 1;
        public string Label => "추가 조명";
        readonly GameObject[] _go = new GameObject[LightSlots.Count];
        readonly bool[] _follow = new bool[LightSlots.Count];
        EnvironmentManager _env;

        public JNode ParamSchema() => LightSlots.Schema(false);

        public void Apply(JNode p, EnvironmentManager env)
        {
            _env = env;
            for (int i = 0; i < LightSlots.Count; i++)
            {
                var s = LightSlots.Read(p, i + 1);
                _follow[i] = s.On && s.Follow;
                if (!s.On) { if (_go[i] != null) _go[i].SetActive(false); continue; }
                if (_go[i] == null) { _go[i] = new GameObject("Lights." + (i + 1)); Object.DontDestroyOnLoad(_go[i]); _go[i].AddComponent<Light>(); }
                var l = _go[i].GetComponent<Light>();
                l.type = s.Spot ? LightType.Spot : LightType.Point;
                l.color = s.Color; l.intensity = s.Intensity; l.range = s.Range; l.spotAngle = s.SpotAngle;
                l.shadows = LightShadows.None;   // 월드 실시간 포인트 라이트 그림자는 드물고 무겁다
                l.renderMode = LightRenderMode.Auto;
                _go[i].transform.SetPositionAndRotation(s.Position, Quaternion.LookRotation(s.Direction));
                _go[i].SetActive(true);
            }
        }

        public void Remove(EnvironmentManager env) { foreach (var g in _go) if (g != null) g.SetActive(false); System.Array.Clear(_follow, 0, _follow.Length); }

        /// <summary>카메라 따라가기: 미리보기 카메라 자리·방향으로.</summary>
        public void Update(float dt)
        {
            var cam = _env?.Camera;
            if (cam == null) return;
            for (int i = 0; i < _go.Length; i++)
                if (_follow[i] && _go[i] != null && _go[i].activeSelf)
                    _go[i].transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
        }
    }

    public sealed class LightVolumesProvider : IEnvironmentProvider
    {
        public string Id => "VRCLightVolumes";
        public int Version => 1;
        public string Label => "라이트 볼륨";
        const int MaxLights = 128;   // VRCLV_MAX_LIGHTS_COUNT — 전역 배열은 처음 설정한 길이로 고정되므로 끝까지 채워 보낸다
        readonly Vector4[] _pos = new Vector4[MaxLights], _col = new Vector4[MaxLights], _dir = new Vector4[MaxLights], _id = new Vector4[MaxLights];

        public JNode ParamSchema() => LightSlots.Schema(true);

        public void Apply(JNode p, EnvironmentManager env)
        {
            int n = 0;
            for (int i = 1; i <= LightSlots.VolumeCount; i++)
            {
                var s = LightSlots.Read(p, i);
                if (!s.On) continue;
                float sqSize = s.Size * s.Size;
                // 셰이더의 감쇠는 색 × 크기² / (거리² + 크기²) — 작은 빛은 아주 어둡다. 화면의 "세기"는 1 m 거리에서의 밝기로 맞춘다
                // (크기는 가까이에서 퍼지는 모양만 바꾼다)
                var c = s.Color.linear * (s.Intensity * (1 + sqSize) / sqSize);
                if (s.Spot)
                {
                    // LightVolumes.cginc LV_PointLight: 위치 w < 0 이면 스팟(−빛 크기²), 색 w = cos(바깥 반각), 방향 w = 가장자리 기울기
                    float half = s.SpotAngle * 0.5f * Mathf.Deg2Rad;
                    float cosOuter = Mathf.Cos(half), cosInner = Mathf.Cos(half * (1 - s.Falloff));
                    _pos[n] = new Vector4(s.Position.x, s.Position.y, s.Position.z, -sqSize);
                    _col[n] = new Vector4(c.r, c.g, c.b, cosOuter);
                    _dir[n] = new Vector4(s.Direction.x, s.Direction.y, s.Direction.z, 1f / Mathf.Max(cosInner - cosOuter, 1e-4f));
                }
                else
                {
                    _pos[n] = new Vector4(s.Position.x, s.Position.y, s.Position.z, sqSize);
                    _col[n] = new Vector4(c.r, c.g, c.b, 0);
                    _dir[n] = new Vector4(0, 0, 0, 1);   // 큐브맵 없음: 회전 쿼터니언은 쓰지 않는다
                }
                _id[n] = new Vector4(0, -1, s.Range * s.Range, 0);   // 사용자 텍스처 없음 · 그림자 마스크 없음 · 닿는 거리²
                n++;
            }
            Shader.SetGlobalVectorArray("_UdonPointLightVolumePosition", _pos);
            Shader.SetGlobalVectorArray("_UdonPointLightVolumeColor", _col);
            Shader.SetGlobalVectorArray("_UdonPointLightVolumeDirection", _dir);
            Shader.SetGlobalVectorArray("_UdonPointLightVolumeCustomID", _id);
            // 볼륨(3D 아틀라스)은 없음 → 셰이더는 환경광(라이트 프로브 SH)을 그대로 쓰고 그 위에 포인트 라이트 볼륨을 더한다
            env.SetGlobalFloat("_UdonLightVolumeEnabled", 1);
            env.SetGlobalFloat("_UdonLightVolumeVersion", 2);
            env.SetGlobalFloat("_UdonLightVolumeCount", 0);
            env.SetGlobalFloat("_UdonLightVolumeAdditiveCount", 0);
            env.SetGlobalFloat("_UdonLightVolumeAdditiveMaxOverdraw", 32);
            env.SetGlobalFloat("_UdonLightVolumeProbesBlend", 1);
            env.SetGlobalFloat("_UdonLightVolumeOcclusionCount", 0);
            env.SetGlobalFloat("_UdonPointLightVolumeCount", n);
            env.SetGlobalFloat("_UdonPointLightVolumeCubeCount", 0);
            env.SetGlobalFloat("_UdonLightBrightnessCutoff", 0.35f);
        }

        public void Remove(EnvironmentManager env) { }   // 전역 float은 EnvironmentManager가 0으로 되돌린다 (Enabled 0 → 셰이더가 볼륨을 안 읽음)
        public void Update(float dt) { }
    }

    public sealed class VRChatGlobalsProvider : IEnvironmentProvider
    {
        public string Id => "VRChatGlobals";
        public int Version => 1;
        public string Label => "VRChat 시점";

        public JNode ParamSchema() => JNode.Parse(@"[
          { ""name"": ""cameraMode"", ""label"": ""카메라 (_VRChatCameraMode)"", ""type"": ""enum"", ""options"": [""0"", ""1"", ""2"", ""3""], ""optionLabels"": [""보통 화면"", ""VR 손 카메라"", ""데스크톱 손 카메라"", ""스크린샷""], ""default"": ""0"" },
          { ""name"": ""mirrorMode"", ""label"": ""거울 (_VRChatMirrorMode)"", ""type"": ""enum"", ""options"": [""0"", ""1"", ""2""], ""optionLabels"": [""거울 아님"", ""VR 거울"", ""데스크톱 거울""], ""default"": ""0"" }
        ]");

        public void Apply(JNode p, EnvironmentManager env)
        {
            float Mode(string k) => float.TryParse(p[k].AsString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            env.SetGlobalFloat("_VRChatCameraMode", Mode("cameraMode"));
            env.SetGlobalFloat("_VRChatMirrorMode", Mode("mirrorMode"));
        }

        public void Remove(EnvironmentManager env) { }
        public void Update(float dt) { }
    }
}
