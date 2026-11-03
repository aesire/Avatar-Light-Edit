// 기본 환경 Provider ("UnityLighting"): 실시간 방향광(주광) + 환경광(위·옆·아래 3색 SH) + 같은 색으로 만든 반사 큐브맵.
// 프리셋이 바탕 값을 정하고 나머지 파라미터는 그 위에 곱하거나 더한다 (예전 세션의 { preset, intensity, yaw, ambient }도 그대로 읽힌다).
// lilToon·Poiyomi는 환경광을 ShadeSH9(앰비언트 SH)로, 반사는 unity_SpecCube0(기본 반사)로 읽는다.
// 그림자 부드럽기·머리 장식 그림자 (2026-10-23, 방송 화질): 값만 여기 두고 실제 적용은 RenderQuality (스튜디오 고화질·오버레이 중 그림자 맵 해상도·바이어스,
//   장식 그림자는 늘). 부드럽기 0 = 8192 맵(가장 또렷), 1 = 512 맵 — 내장 렌더러의 소프트 그림자는 맵 텍셀 몇 개만큼 흐리므로 해상도를 낮추면 가장자리가 넓게 번진다.
// 정면광 (2026-10-24 사용자: 밝은 프리셋 대부분에 정면광, 에디터 기본 화면에서도 얼굴 음영이 없게):
//   밝은 월드·실내·노을은 아바타 정면 10° 높이에서 그림자 없는 방향광을 하나 더 둔다(프리셋마다 세기, "정면광 배율"로 곱함). 밤·어두운 월드는 없음.
//   머리카락·머리 장식이 드리우는 그림자는 기본 끔 — 앞머리·장식이 얼굴과 머리카락에 지저분한 그림자를 떨어뜨렸다.
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class UnityLightingProvider : IEnvironmentProvider
    {
        public string Id => "UnityLighting";
        public int Version => 1;
        public string Label => "조명";
        GameObject _lightGo;
        /// <summary>그림자 부드럽기 0~1 (RenderQuality가 맵 해상도·바이어스로).</summary>
        public static float ShadowSoftness;
        /// <summary>false면 머리 본 아래에 붙은 것(머리 장식·핀·귀 등)이 그림자를 드리우지 않는다.</summary>
        public static bool AccessoryShadows;
        Cubemap _reflection;
        GameObject _frontGo;

        struct Preset { public Color Sun; public float Intensity, Pitch, Yaw, Front; public Color Sky, Equator, Ground; public Color Bg; public bool HasSun; }

        static Preset Get(string name)
        {
            switch (name)
            {
                case "indoor": return new Preset { HasSun = true, Sun = new Color(1f, 0.93f, 0.82f), Intensity = 0.75f, Pitch = 40, Yaw = 160, Front = 0.4f, Sky = new Color(0.55f, 0.52f, 0.5f), Equator = new Color(0.42f, 0.4f, 0.38f), Ground = new Color(0.25f, 0.22f, 0.2f), Bg = new Color(0.32f, 0.29f, 0.27f) };
                case "sunset": return new Preset { HasSun = true, Sun = new Color(1f, 0.62f, 0.38f), Intensity = 0.9f, Pitch = 12, Yaw = 120, Front = 0.3f, Sky = new Color(0.55f, 0.5f, 0.68f), Equator = new Color(0.78f, 0.55f, 0.45f), Ground = new Color(0.28f, 0.22f, 0.24f), Bg = new Color(0.62f, 0.45f, 0.45f) };
                case "night": return new Preset { HasSun = true, Sun = new Color(0.6f, 0.7f, 1f), Intensity = 0.35f, Pitch = 55, Yaw = 210, Sky = new Color(0.14f, 0.17f, 0.3f), Equator = new Color(0.1f, 0.11f, 0.18f), Ground = new Color(0.05f, 0.05f, 0.08f), Bg = new Color(0.06f, 0.07f, 0.12f) };
                case "dark": return new Preset { HasSun = true, Sun = new Color(0.55f, 0.6f, 0.85f), Intensity = 0.2f, Pitch = 35, Yaw = 200, Sky = new Color(0.08f, 0.09f, 0.14f), Equator = new Color(0.05f, 0.05f, 0.08f), Ground = new Color(0.02f, 0.02f, 0.03f), Bg = new Color(0.04f, 0.04f, 0.06f) };
                case "flat": return new Preset { HasSun = false, Sky = new Color(0.8f, 0.8f, 0.8f), Equator = new Color(0.8f, 0.8f, 0.8f), Ground = new Color(0.8f, 0.8f, 0.8f), Bg = new Color(0.5f, 0.5f, 0.52f) };
                case "none": return new Preset { HasSun = false, Sky = Color.black, Equator = Color.black, Ground = Color.black, Bg = new Color(0.1f, 0.1f, 0.1f) };
                default: return new Preset { HasSun = true, Sun = new Color(1f, 0.97f, 0.92f), Intensity = 1.0f, Pitch = 50, Yaw = 150, Front = 0.45f, Sky = new Color(0.75f, 0.82f, 0.95f), Equator = new Color(0.62f, 0.65f, 0.7f), Ground = new Color(0.4f, 0.38f, 0.36f), Bg = new Color(0.55f, 0.6f, 0.68f) };
            }
        }

        public JNode ParamSchema() => JNode.Parse(@"[
          { ""name"": ""preset"", ""label"": ""프리셋"", ""type"": ""enum"", ""options"": [""bright"", ""indoor"", ""sunset"", ""night"", ""dark"", ""flat"", ""none""], ""optionLabels"": [""밝은 월드"", ""실내"", ""노을"", ""밤"", ""어두운 월드"", ""그림자 없는 고른 빛"", ""라이트 없음""], ""default"": ""bright"" },
          { ""name"": ""intensity"", ""label"": ""주광 세기 배율"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 },
          { ""name"": ""yaw"", ""label"": ""주광 방향 회전(°)"", ""type"": ""float"", ""min"": -180, ""max"": 180, ""default"": 0 },
          { ""name"": ""pitch"", ""label"": ""주광 높이 더하기(°)"", ""type"": ""float"", ""min"": -60, ""max"": 60, ""default"": 0 },
          { ""name"": ""sunColor"", ""label"": ""주광 색 (곱하기)"", ""type"": ""color"", ""default"": [1, 1, 1] },
          { ""name"": ""front"", ""label"": ""정면광 배율 (얼굴 음영 메우기)"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 },
          { ""name"": ""shadows"", ""label"": ""주광 그림자"", ""type"": ""bool"", ""default"": true },
          { ""name"": ""shadowStrength"", ""label"": ""그림자 진하기"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0.8 },
          { ""name"": ""shadowSoftness"", ""label"": ""그림자 부드럽기 (스튜디오·방송)"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0 },
          { ""name"": ""accessoryShadows"", ""label"": ""머리카락·머리 장식이 드리우는 그림자"", ""type"": ""bool"", ""default"": false },
          { ""name"": ""ambient"", ""label"": ""환경광 배율"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 },
          { ""name"": ""ambientColor"", ""label"": ""환경광 색 (곱하기)"", ""type"": ""color"", ""default"": [1, 1, 1] },
          { ""name"": ""reflection"", ""label"": ""반사 세기"", ""type"": ""float"", ""min"": 0, ""max"": 2, ""default"": 1 },
          { ""name"": ""bgCustom"", ""label"": ""배경 색 직접 고르기"", ""type"": ""bool"", ""default"": false },
          { ""name"": ""bgColor"", ""label"": ""배경 색"", ""type"": ""color"", ""default"": [0.55, 0.6, 0.68] }
        ]");

        public void Apply(JNode p, EnvironmentManager env)
        {
            var pr = Get(p.Has("preset") ? p["preset"].AsString : "bright");
            float F(string k, float def) => p.Has(k) ? p[k].AsFloat : def;
            ShadowSoftness = Mathf.Clamp01(F("shadowSoftness", 0));
            AccessoryShadows = p.Has("accessoryShadows") && p["accessoryShadows"].AsBool;
            float intensity = F("intensity", 1), yaw = F("yaw", 0), pitch = F("pitch", 0), ambient = F("ambient", 1);
            var sunTint = EnvColor.Read(p["sunColor"], Color.white);
            var ambTint = EnvColor.Read(p["ambientColor"], Color.white).linear;

            if (pr.HasSun)
            {
                if (_lightGo == null) { _lightGo = new GameObject("UnityLighting.Sun"); Object.DontDestroyOnLoad(_lightGo); _lightGo.AddComponent<Light>(); }
                var l = _lightGo.GetComponent<Light>();
                l.type = LightType.Directional; l.color = pr.Sun * sunTint; l.intensity = pr.Intensity * intensity;
                l.shadows = p.Has("shadows") && !p["shadows"].AsBool ? LightShadows.None : LightShadows.Soft;
                l.shadowStrength = Mathf.Clamp01(F("shadowStrength", 0.8f));
                _lightGo.transform.rotation = Quaternion.Euler(Mathf.Clamp(pr.Pitch + pitch, -89, 89), pr.Yaw + yaw, 0);
                _lightGo.SetActive(true);
                RenderSettings.sun = l;
            }
            else if (_lightGo != null) _lightGo.SetActive(false);

            // 정면광: 아바타 정면(+Z) 10° 높이에서 아바타 쪽으로, 그림자 없음
            float front = pr.Front * Mathf.Max(0, F("front", 1));
            if (pr.HasSun && front > 0.001f)
            {
                if (_frontGo == null) { _frontGo = new GameObject("UnityLighting.Front"); Object.DontDestroyOnLoad(_frontGo); _frontGo.AddComponent<Light>(); }
                var fl = _frontGo.GetComponent<Light>();
                fl.type = LightType.Directional; fl.shadows = LightShadows.None; fl.renderMode = LightRenderMode.ForcePixel;
                fl.color = Color.Lerp(Color.white, pr.Sun * sunTint, 0.5f); fl.intensity = front * intensity;
                _frontGo.transform.rotation = Quaternion.Euler(10, 180, 0);
                _frontGo.SetActive(true);
            }
            else if (_frontGo != null) _frontGo.SetActive(false);

            Color sky = pr.Sky.linear * ambTint * ambient, eq = pr.Equator.linear * ambTint * ambient, ground = pr.Ground.linear * ambTint * ambient;
            var sh = new SphericalHarmonicsL2(); sh.Clear();
            sh.AddAmbientLight(eq);
            sh.AddDirectionalLight(Vector3.up, sky - eq, 0.6f);
            sh.AddDirectionalLight(Vector3.down, ground - eq, 0.6f);
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = sh;

            // 반사: 하늘·지평선·바닥 색 그라데이션 큐브맵 (월드의 반사 프로브 대신)
            _reflection = SkyCube(_reflection, sky, eq, ground);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _reflection;
            RenderSettings.reflectionIntensity = Mathf.Max(0, F("reflection", 1));
            // 배경 색: 끄면 프리셋 색. 미리보기 "표시 ▸ 배경"이 환경 설정 그대로일 때와 촬영 배경에 쓰인다 (조명에는 영향 없음)
            env.Background = p.Has("bgCustom") && p["bgCustom"].AsBool ? EnvColor.Read(p["bgColor"], pr.Bg) : pr.Bg;
        }

        /// 32×32 면, 밉맵 포함 (거친 표면은 낮은 밉을 읽는다). Linear 값 그대로 (Half 형식)
        static Cubemap SkyCube(Cubemap cube, Color sky, Color eq, Color ground)
        {
            const int n = 32;
            if (cube == null) cube = new Cubemap(n, TextureFormat.RGBAHalf, true) { name = "UnityLighting.Reflection", hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[n * n];
            for (int f = 0; f < 6; f++)
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                        var d = FaceDir((CubemapFace)f, u, v).normalized;
                        px[y * n + x] = d.y >= 0 ? Color.Lerp(eq, sky, Mathf.Sqrt(d.y)) : Color.Lerp(eq, ground, Mathf.Sqrt(-d.y));
                    }
                cube.SetPixels(px, (CubemapFace)f);
            }
            cube.Apply(true);
            return cube;
        }

        /// 큐브맵 면의 (u, v) → 방향 (Unity 큐브맵 규칙: 면 안에서 v는 아래로)
        static Vector3 FaceDir(CubemapFace f, float u, float v)
        {
            switch (f)
            {
                case CubemapFace.PositiveX: return new Vector3(1, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1);
                default: return new Vector3(-u, -v, -1);
            }
        }

        public void Remove(EnvironmentManager env) { if (_lightGo != null) _lightGo.SetActive(false); if (_frontGo != null) _frontGo.SetActive(false); ShadowSoftness = 0; AccessoryShadows = false; }
        public void Update(float dt) { }
    }
}
