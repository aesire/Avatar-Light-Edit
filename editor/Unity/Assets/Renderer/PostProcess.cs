// 후처리 환경 Provider ("PostProcess"): 블룸 · 노출 · 화이트 밸런스 · 대비·채도 · 톤 매핑 · 비네트.
// 패키지(Post Processing Stack) 없이 카메라의 OnRenderImage + Hidden/VrcProto/PostProcess 셰이더로 그린다.
// VRChat 월드에서 흔한 후처리를 흉내 내 "이런 월드에서는 이렇게 보인다"를 확인하는 용도다 (월드마다 실제 값은 다르다).
// 투명 배경 촬영은 검정·흰 배경 차이로 알파를 구하므로 후처리를 끄고 찍은 뒤(Capture가 Bypass를 켠다), 색 보정만 Grade로 다시 넣는다 (2026-10-24).
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    [RequireComponent(typeof(Camera))]
    public sealed class PostFx : MonoBehaviour
    {
        const int PassPrefilter = 0, PassDown = 1, PassUp = 2, PassFinal = 3;
        const int MaxLevels = 8;

        public float BloomIntensity, BloomThreshold = 1, BloomSoftKnee = 0.5f, BloomScatter = 0.7f;
        public Color BloomTint = Color.white;
        public float Exposure, Temperature, Tint, Contrast, Saturation, Vignette;
        public int Tonemap; // 0 없음, 1 Neutral, 2 ACES
        /// <summary>촬영(투명 배경)처럼 후처리 없이 그려야 할 때.</summary>
        public bool Bypass;

        Material _mat;
        readonly RenderTexture[] _down = new RenderTexture[MaxLevels], _up = new RenderTexture[MaxLevels];

        void OnEnable()
        {
            GetComponent<Camera>().allowHDR = true; // 블룸·톤 매핑은 1보다 밝은 값이 있어야 의미가 있다
            if (_mat == null)
            {
                var s = Shader.Find("Hidden/VrcProto/PostProcess");
                if (s != null) _mat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                else Debug.LogWarning("[VrcProto] 후처리 셰이더가 빌드에 없음");
            }
        }

        void OnDestroy() { if (_mat != null) Destroy(_mat); }

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_mat == null || Bypass) { Graphics.Blit(src, dst); return; }
            bool bloom = BloomIntensity > 0;
            if (bloom) Bloom(src);
            SetGrade(bloom ? BloomIntensity : 0);
            Graphics.Blit(src, dst, _mat, PassFinal);
            for (int i = 0; i < MaxLevels; i++)
            {
                if (_down[i] != null) { RenderTexture.ReleaseTemporary(_down[i]); _down[i] = null; }
                if (_up[i] != null) { RenderTexture.ReleaseTemporary(_up[i]); _up[i] = null; }
            }
        }

        /// <summary>
        /// 색 보정만 (블룸 없이: 노출·화이트 밸런스·대비·채도·톤 매핑·비네트) — 투명 배경 촬영에서 알파를 구한 뒤 일반 알파 색에 (2026-10-24).
        /// 픽셀마다의 계산이라 배경과 상관없다. 블룸은 배경에 번지는 빛이라 투명 촬영에는 넣지 않는다. 알파는 그대로. 켜져 있지 않으면 false.
        /// </summary>
        public bool Grade(RenderTexture src, RenderTexture dst)
        {
            if (_mat == null || !enabled) return false;
            SetGrade(0);
            Graphics.Blit(src, dst, _mat, PassFinal);
            return true;
        }

        void SetGrade(float bloom)
        {
            _mat.SetFloat("_BloomIntensity", bloom);
            _mat.SetVector("_BloomTint", BloomTint.linear);
            _mat.SetFloat("_Exposure", Mathf.Pow(2, Exposure));
            _mat.SetVector("_WhiteBalance", WhiteBalance(Temperature, Tint));
            _mat.SetFloat("_Contrast", 1 + Contrast);
            _mat.SetFloat("_Saturation", 1 + Saturation);
            _mat.SetFloat("_Tonemap", Tonemap);
            _mat.SetFloat("_Vignette", Vignette);
        }

        /// 밝은 부분을 반씩 줄여 가며 흐리게 하고(최대 8단계), 다시 키우며 단계마다 더한다 → _BloomTex
        void Bloom(RenderTexture src)
        {
            float knee = BloomThreshold * Mathf.Clamp01(BloomSoftKnee) + 1e-5f;
            _mat.SetVector("_Threshold", new Vector4(BloomThreshold, knee, knee * 2, 0.25f / knee));
            _mat.SetFloat("_Scatter", Mathf.Clamp01(BloomScatter));
            int w = src.width / 2, h = src.height / 2;
            int levels = Mathf.Clamp(Mathf.FloorToInt(Mathf.Log(Mathf.Max(w, h), 2)) - 1, 1, MaxLevels);
            var last = src;
            for (int i = 0; i < levels; i++)
            {
                _down[i] = RenderTexture.GetTemporary(Mathf.Max(1, w), Mathf.Max(1, h), 0, RenderTextureFormat.ARGBHalf);
                _down[i].filterMode = FilterMode.Bilinear;
                Graphics.Blit(last, _down[i], _mat, i == 0 ? PassPrefilter : PassDown);
                last = _down[i]; w /= 2; h /= 2;
            }
            for (int i = levels - 2; i >= 0; i--)
            {
                _up[i] = RenderTexture.GetTemporary(_down[i].width, _down[i].height, 0, RenderTextureFormat.ARGBHalf);
                _up[i].filterMode = FilterMode.Bilinear;
                _mat.SetTexture("_BaseTex", _down[i]);
                Graphics.Blit(last, _up[i], _mat, PassUp);
                last = _up[i];
            }
            _mat.SetTexture("_BloomTex", last);
        }

        /// 색온도(-100 차갑게 ~ 100 따뜻하게)와 틴트(-100 초록 ~ 100 자홍)를 RGB 배율로. 밝기는 유지한다
        static Vector3 WhiteBalance(float temperature, float tint)
        {
            float t = temperature / 100f * 0.25f, g = tint / 100f * 0.2f;
            var v = new Vector3(1 + t, 1 - g, 1 - t);
            float l = 0.2126f * v.x + 0.7152f * v.y + 0.0722f * v.z;
            return v / l;
        }
    }

    public sealed class PostProcessProvider : IEnvironmentProvider
    {
        public string Id => "PostProcess";
        public int Version => 1;
        public string Label => "후처리";

        public JNode ParamSchema() => JNode.Parse(@"[
          { ""name"": ""bloom"", ""label"": ""블룸 세기"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 0.6 },
          { ""name"": ""bloomThreshold"", ""label"": ""블룸 문턱값 (이보다 밝은 곳이 번짐)"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 },
          { ""name"": ""bloomScatter"", ""label"": ""블룸 퍼짐"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0.7 },
          { ""name"": ""bloomTint"", ""label"": ""블룸 색"", ""type"": ""color"", ""default"": [1, 1, 1] },
          { ""name"": ""tonemap"", ""label"": ""톤 매핑"", ""type"": ""enum"", ""options"": [""none"", ""neutral"", ""aces""], ""optionLabels"": [""없음"", ""Neutral"", ""ACES (영화 같은 대비)""], ""default"": ""none"" },
          { ""name"": ""exposure"", ""label"": ""노출 (EV)"", ""type"": ""float"", ""min"": -3, ""max"": 3, ""default"": 0 },
          { ""name"": ""temperature"", ""label"": ""색온도 (차갑게 ↔ 따뜻하게)"", ""type"": ""float"", ""min"": -100, ""max"": 100, ""default"": 0 },
          { ""name"": ""tint"", ""label"": ""틴트 (초록 ↔ 자홍)"", ""type"": ""float"", ""min"": -100, ""max"": 100, ""default"": 0 },
          { ""name"": ""contrast"", ""label"": ""대비"", ""type"": ""float"", ""min"": -0.5, ""max"": 0.5, ""default"": 0 },
          { ""name"": ""saturation"", ""label"": ""채도"", ""type"": ""float"", ""min"": -1, ""max"": 1, ""default"": 0 },
          { ""name"": ""vignette"", ""label"": ""비네트 (가장자리 어둡게)"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0 }
        ]");

        public void Apply(JNode p, EnvironmentManager env)
        {
            var cam = env.Camera;
            if (cam == null) return;
            var fx = cam.GetComponent<PostFx>();
            if (fx == null) fx = cam.gameObject.AddComponent<PostFx>();
            float F(string k, float def) => p.Has(k) ? p[k].AsFloat : def;
            fx.BloomIntensity = Mathf.Max(0, F("bloom", 0.6f));
            fx.BloomThreshold = Mathf.Max(0, F("bloomThreshold", 1));
            fx.BloomScatter = F("bloomScatter", 0.7f);
            fx.BloomTint = EnvColor.Read(p["bloomTint"], Color.white);
            var tm = p["tonemap"].AsString;
            fx.Tonemap = tm == "aces" ? 2 : tm == "neutral" ? 1 : 0;
            fx.Exposure = F("exposure", 0);
            fx.Temperature = F("temperature", 0); fx.Tint = F("tint", 0);
            fx.Contrast = F("contrast", 0); fx.Saturation = F("saturation", 0);
            fx.Vignette = Mathf.Clamp01(F("vignette", 0));
            fx.enabled = true;
        }

        public void Remove(EnvironmentManager env)
        {
            var fx = env.Camera != null ? env.Camera.GetComponent<PostFx>() : null;
            if (fx != null) fx.enabled = false;
        }

        public void Update(float dt) { }
    }

    /// <summary>환경 파라미터의 색 ([r, g, b(, a)] 0~1, 감마 값 — Unity 색 고르기와 같다).</summary>
    public static class EnvColor
    {
        public static Color Read(JNode n, Color def)
        {
            if (n.Kind != JKind.Array || n.Count < 3) return def;
            var a = n.ToFloatArray();
            return new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1);
        }
    }
}
