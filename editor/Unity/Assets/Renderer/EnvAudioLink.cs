// 환경 Provider "AudioLink" (PLAN 3.8, 2026-10-23): 월드의 AudioLink가 만드는 전역 텍스처 _AudioTexture(128×64)를 흉내 낸다.
// 셰이더(lilToon·Poiyomi)가 읽는 곳만 채운다 (AudioLink 공개 형식):
//   0~3번 줄  대역 4개(저음·중저음·중고음·고음)의 기록 — x = 0이 지금, 오른쪽으로 갈수록 과거 (lilToon은 R, 줄 = _AudioLinkUVParams.a × 4/64)
//   4~5번 줄  스펙트럼 256칸 (낮은 음 → 높은 음, 로그 간격; lilToon 스펙트럼 모드는 B)
//   22번 줄   x 0~1 VU (지금 크기), 23번 줄 x 0~3 테마 색
// 소리: "박자 패턴"(BPM에 맞춘 킥·스네어·하이햇 합성) 또는 "오디오 파일"(wav·ogg·mp3를 재생하며 Unity GetSpectrumData로 대역을 잰다).
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class AudioLinkProvider : IEnvironmentProvider
    {
        public string Id => "AudioLink";
        public int Version => 1;
        public string Label => "AudioLink";

        const int W = 128, H = 64, Fps = 90;   // 한 칸 = AudioLink 한 프레임
        Texture2D _tex;
        Color[] _px;
        bool _active, _file;
        float _bpm = 128, _strength = 1, _volume = 0.5f, _time, _step;
        readonly float[] _peak = { 1e-4f, 1e-4f, 1e-4f, 1e-4f };
        readonly float[] _spectrum = new float[2048];
        Color _theme0 = Color.white, _theme1 = Color.white;

        // 오디오 파일
        string _path = "";
        UnityWebRequest _req;
        AudioSource _src;

        public JNode ParamSchema() => JNode.Parse(@"[
          { ""name"": ""source"", ""label"": ""소리"", ""type"": ""enum"", ""options"": [""pattern"", ""file""], ""optionLabels"": [""박자 패턴 (합성)"", ""오디오 파일""], ""default"": ""pattern"" },
          { ""name"": ""bpm"", ""label"": ""박자 (BPM)"", ""type"": ""float"", ""min"": 60, ""max"": 200, ""default"": 128 },
          { ""name"": ""strength"", ""label"": ""세기"", ""type"": ""float"", ""min"": 0, ""max"": 1.5, ""default"": 1 },
          { ""name"": ""file"", ""label"": ""오디오 파일 (wav·ogg·mp3)"", ""type"": ""file"", ""filter"": [""wav"", ""ogg"", ""mp3""], ""default"": """" },
          { ""name"": ""volume"", ""label"": ""파일 소리 크기 (0 = 소리 없이 분석만)"", ""type"": ""float"", ""min"": 0, ""max"": 1, ""default"": 0.5 },
          { ""name"": ""theme0"", ""label"": ""테마 색 1"", ""type"": ""color"", ""default"": [1, 0.3, 0.6] },
          { ""name"": ""theme1"", ""label"": ""테마 색 2"", ""type"": ""color"", ""default"": [0.3, 0.6, 1] }
        ]");

        public void Apply(JNode p, EnvironmentManager env)
        {
            if (_tex == null)
            {
                _tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true) { name = "AudioLink._AudioTexture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                _px = new Color[W * H];
            }
            _file = p["source"].AsString == "file";
            _bpm = p.Has("bpm") ? Mathf.Clamp(p["bpm"].AsFloat, 30, 300) : 128;
            _strength = p.Has("strength") ? Mathf.Max(0, p["strength"].AsFloat) : 1;
            _volume = p.Has("volume") ? Mathf.Clamp01(p["volume"].AsFloat) : 0.5f;
            _theme0 = EnvColor.Read(p["theme0"], new Color(1, 0.3f, 0.6f)); _theme1 = EnvColor.Read(p["theme1"], new Color(0.3f, 0.6f, 1));
            var path = _file ? p["file"].AsString : "";
            if (path != _path) LoadFile(path);
            if (_src != null) { _src.volume = _volume; if (!_src.isPlaying && _src.clip != null) _src.Play(); }
            env.SetGlobalTexture("_AudioTexture", _tex);
            env.SetGlobalVector("_AudioTexture_TexelSize", new Vector4(1f / W, 1f / H, W, H));
            _active = true;
        }

        public void Remove(EnvironmentManager env)
        {
            _active = false;
            if (_src != null) _src.Stop();
        }

        void LoadFile(string path)
        {
            _path = path;
            _req?.Dispose(); _req = null;
            if (_src != null) { _src.Stop(); _src.clip = null; }
            if (path.Length == 0) return;
            if (!File.Exists(path)) { Debug.LogWarning("[VrcProto] AudioLink: 오디오 파일이 없어요: " + path); return; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var type = ext == ".wav" ? AudioType.WAV : ext == ".ogg" ? AudioType.OGGVORBIS : ext == ".mp3" ? AudioType.MPEG : AudioType.UNKNOWN;
            _req = UnityWebRequestMultimedia.GetAudioClip(new System.Uri(Path.GetFullPath(path)).AbsoluteUri, type);
            _req.SendWebRequest();
        }

        public void Update(float dt)
        {
            if (!_active || _tex == null) return;
            if (_req != null && _req.isDone)
            {
                if (_req.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(_req);
                    if (_src == null)
                    {
                        var go = new GameObject("AudioLink.Source"); Object.DontDestroyOnLoad(go);
                        _src = go.AddComponent<AudioSource>(); _src.loop = true; _src.spatialBlend = 0; _src.playOnAwake = false;
                    }
                    _src.clip = clip; _src.volume = _volume; _src.Play();
                    Debug.Log($"[VrcProto] AudioLink: 오디오 파일 재생 ({clip.length:0.0}초): {_path}");
                }
                else Debug.LogWarning("[VrcProto] AudioLink: 오디오 파일을 열지 못했어요: " + _req.error);
                _req.Dispose(); _req = null;
            }

            _time += dt;
            _step += dt;
            if (_step < 1f / Fps) return;
            int steps = Mathf.Min(8, (int)(_step * Fps));
            _step -= steps / (float)Fps;

            var bands = new float[4];
            bool fromFile = _file && _src != null && _src.isPlaying;
            if (fromFile) FileBands(bands);
            else if (!_file) PatternBands(bands);
            for (int b = 0; b < 4; b++) bands[b] = Mathf.Clamp01(bands[b] * _strength);

            // 0~3번 줄: 기록을 오른쪽으로 밀고 x = 0에 지금 값
            for (int s = 0; s < steps; s++)
                for (int b = 0; b < 4; b++)
                {
                    int row = b * W;
                    System.Array.Copy(_px, row, _px, row + 1, W - 1);
                    _px[row] = new Color(bands[b], bands[b], bands[b], 1);
                }
            // 4~5번 줄: 스펙트럼 256칸
            for (int k = 0; k < 256; k++)
            {
                float v = fromFile ? SpectrumAt(k) : _file ? 0 : PatternSpectrum(k, bands);
                v = Mathf.Clamp01(v * _strength);
                _px[(4 + k / W) * W + k % W] = new Color(v, v, v, 1);
            }
            float vu = (bands[0] + bands[1] + bands[2] + bands[3]) * 0.25f;
            _px[22 * W] = new Color(vu, vu, vu, 1); _px[22 * W + 1] = new Color(Mathf.Max(bands[0], bands[3]), 0, 0, 1);
            _px[23 * W] = _theme0.linear; _px[23 * W + 1] = _theme1.linear; _px[23 * W + 2] = Color.Lerp(_theme0, _theme1, 0.5f).linear; _px[23 * W + 3] = Color.white;
            _tex.SetPixels(_px);
            _tex.Apply(false);
        }

        /// 박자 패턴: 박마다 킥(저음), 뒷박에 스네어(중저음), 반 박마다 하이햇(고음), 중고음은 느린 물결
        void PatternBands(float[] b)
        {
            float beat = _time * _bpm / 60f;
            float f = beat - Mathf.Floor(beat), half = beat * 2 - Mathf.Floor(beat * 2);
            bool offBeat = ((int)Mathf.Floor(beat) & 1) == 1;
            b[0] = Mathf.Exp(-f * 7f);
            b[1] = offBeat ? Mathf.Exp(-f * 9f) * 0.85f : Mathf.Exp(-f * 9f) * 0.2f;
            b[2] = 0.35f + 0.25f * Mathf.Sin(_time * 1.7f) + 0.15f * Mathf.Exp(-f * 5f);
            b[3] = Mathf.Exp(-half * 14f) * 0.8f;
        }

        static float PatternSpectrum(int k, float[] b)
        {
            float t = k / 255f;   // 0 = 낮은 음
            float band = t * 4f;
            int i = Mathf.Min(3, (int)band);
            float shape = 0.75f + 0.25f * Mathf.Sin(k * 0.37f);
            return b[i] * shape * (1 - 0.3f * t);
        }

        /// 오디오 파일: 대역 경계 (Hz) — 저음 ~120, 중저음 ~500, 중고음 ~2000, 고음 ~8000. 크기는 대역마다 천천히 줄어드는 최대값으로 나눈다
        static readonly float[] Edges = { 20, 120, 500, 2000, 8000 };
        void FileBands(float[] b)
        {
            _src.GetSpectrumData(_spectrum, 0, FFTWindow.BlackmanHarris);
            float hzPerBin = AudioSettings.outputSampleRate * 0.5f / _spectrum.Length;
            for (int i = 0; i < 4; i++)
            {
                int lo = Mathf.Max(1, (int)(Edges[i] / hzPerBin)), hi = Mathf.Min(_spectrum.Length - 1, Mathf.Max(lo + 1, (int)(Edges[i + 1] / hzPerBin)));
                float sum = 0;
                for (int k = lo; k < hi; k++) sum += _spectrum[k];
                _peak[i] = Mathf.Max(_peak[i] * 0.9985f, sum, 1e-4f);
                b[i] = Mathf.Pow(sum / _peak[i], 1.5f);
            }
        }

        /// 스펙트럼 칸 k (0~255) = 27.5 Hz부터 1/24옥타브 간격 (약 10옥타브)
        float SpectrumAt(int k)
        {
            float hz = 27.5f * Mathf.Pow(2, k / 24f);
            float hzPerBin = AudioSettings.outputSampleRate * 0.5f / _spectrum.Length;
            int bin = Mathf.Clamp((int)(hz / hzPerBin), 0, _spectrum.Length - 1);
            return Mathf.Sqrt(_spectrum[bin]) * 4f;
        }
    }
}
