// 말하기 → 입 모양 (립싱크, 미리보기 전용, 2026-10-24 사용자: 말할 때 입 따라 움직이기).
// 마이크(Unity Microphone, 16 kHz)의 마지막 1024 샘플(64 ms)을 렌더 프레임마다 읽어
//   크기  RMS → dB, 조용할 때의 바닥(천천히 따라가는 최소값)보다 얼마나 큰지로 0~1 (감도로 범위를 바꾼다)
//   모음  프리엠퍼시스 · 해밍 창 · LPC 16차(자기상관 + 레빈슨-더빈) 다항식의 근에서 낮은 두 공진 = 포먼트 F1·F2,
//         일본어(한국어도 비슷) 다섯 모음의 평균 포먼트와 로그 거리로 가중치 (아 · 이 · 우 · 에 · 오)
// 결과는 lip.aa · lip.ih · lip.ou · lip.e · lip.oh (크기 × 모음 가중치)와 lip.vol. FaceTracker가 입 모양(viseme.*) 연결에 큰 쪽으로 합치고,
// 입 모양 블렌드쉐이프가 없는 퍼펙트 싱크·FT 아바타는 입 벌림·오므림 값(jawOpen·mouthFunnel …)으로 바꿔 넣는다.
// 소리는 저장·전송하지 않는다. 예외: 스튜디오 녹화 중에는 StartTape~StopTape 사이의 소리를 모아 녹화 파일(MP4 소리·PNG 옆 audio.wav)에만 넣는다 (2026-10-24).
using System;
using System.Collections.Generic;
using Complex = System.Numerics.Complex;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class LipSync : IDisposable
    {
        const int Rate = 16000, Window = 1024, Order = 16;
        readonly string _device;
        readonly AudioClip _clip;
        readonly float[] _buf = new float[Window], _x = new float[Window];
        readonly double[] _r = new double[Order + 1], _a = new double[Order + 1], _tmp = new double[Order + 1];
        float _floor = -60, _level;
        public float Gain = 1;   // 감도 0.25~4: 크면 작은 소리에도 입이 크게
        public string Text { get; private set; } = "";
        public bool Ok { get; private set; }
        public float Level => _level;
        public float F1 { get; private set; }
        public float F2 { get; private set; }

        // 모음 평균 포먼트 (Hz, 성인 남녀 평균에 가깝게 — 목소리가 높으면 F1·F2가 함께 올라가 로그 거리라 크게 어긋나지 않는다)
        static readonly (string Key, float F1, float F2)[] Vowels =
        {
            ("lip.aa", 800, 1300), ("lip.ih", 320, 2500), ("lip.ou", 380, 1350), ("lip.e", 520, 2000), ("lip.oh", 520, 900),
        };

        public static string[] Devices() => Microphone.devices;

        // 시험용: 마이크 대신 16 kHz 소리 (WAV, 되풀이) — E2E가 합성한 모음으로 확인한다
        readonly float[] _samples; readonly float _t0;

        public LipSync(float[] samples)
        {
            _samples = samples; _t0 = Time.unscaledTime;
            Ok = samples.Length >= Window;
            Text = Ok ? "소리 파일" : "소리 파일이 너무 짧아요";
        }

        /// <summary>16비트 PCM 모노 WAV (16 kHz). 시험용.</summary>
        public static float[] ReadWav(string path)
        {
            var b = System.IO.File.ReadAllBytes(path);
            int p = 12, data = -1, size = 0;
            while (p + 8 <= b.Length)
            {
                int n = BitConverter.ToInt32(b, p + 4);
                if (b[p] == 'd' && b[p + 1] == 'a' && b[p + 2] == 't' && b[p + 3] == 'a') { data = p + 8; size = Math.Min(n, b.Length - data); break; }
                p += 8 + n + (n & 1);
            }
            if (data < 0) throw new Exception("WAV data 없음");
            var s = new float[size / 2];
            for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(b, data + i * 2) / 32768f;
            return s;
        }

        public LipSync(string device)
        {
            var list = Microphone.devices;
            if (list.Length == 0) { Text = "마이크를 찾지 못했어요"; return; }
            _device = !string.IsNullOrEmpty(device) && Array.IndexOf(list, device) >= 0 ? device : list[0];
            _clip = Microphone.Start(_device, true, 4, Rate);   // 4초 고리 (녹화 중 프레임이 1초 넘게 걸려도 녹음이 끊기지 않게)
            Ok = _clip != null;
            Text = Ok ? "마이크: " + _device : "마이크를 열지 못했어요: " + _device;
        }

        /// <summary>렌더 프레임마다: 입 모양 값을 values에 쓴다 (lip.*).</summary>
        public void Poll(Dictionary<string, float> values)
        {
            foreach (var v in Vowels) values[v.Key] = 0;
            values["lip.vol"] = 0;
            if (!Ok) return;
            Tape();
            if (_samples != null)
            {
                int at = (int)((Time.unscaledTime - _t0) * Rate) % (_samples.Length - Window);
                Array.Copy(_samples, at, _buf, 0, Window);
            }
            else if (!ReadMic()) return;
            Analyze(values);
        }

        // ---- 녹화용 소리 모으기 (StudioManager 녹화) ----
        public const int TapeRate = Rate;
        List<float> _tape;
        int _tapePos; float _tapeT;

        /// <summary>지금부터 들어오는 소리를 모은다 (렌더 프레임마다 Poll이 새로 들어온 샘플을 붙인다).</summary>
        public void StartTape()
        {
            _tape = new List<float>(Rate * 30);
            if (_samples != null) _tapeT = Time.unscaledTime;
            else if (_clip != null) _tapePos = Microphone.GetPosition(_device);
        }

        /// <summary>모으기를 끝내고 모은 소리 (16 kHz 모노, 없으면 빈 배열).</summary>
        public float[] StopTape()
        {
            if (_tape != null && Ok) Tape();
            var t = _tape?.ToArray() ?? new float[0];
            _tape = null;
            return t;
        }

        /// <summary>Poll을 부르지 않는 녹음(VR 카메라)이 프레임마다: 고리가 한 바퀴 돌기 전에 모은다.</summary>
        public void KeepTape() { if (Ok) Tape(); }

        void Tape()
        {
            if (_tape == null) return;
            if (_samples != null)
            {
                // 시험용 소리: 흐른 시간만큼 되풀이 소리에서 (Poll의 읽기와 같은 시각 기준)
                int from = (int)((_tapeT - _t0) * Rate), to = (int)((Time.unscaledTime - _t0) * Rate);
                for (int k = from; k < to; k++) _tape.Add(_samples[k % _samples.Length]);
                _tapeT = Time.unscaledTime;
                return;
            }
            int pos = Microphone.GetPosition(_device), n = pos - _tapePos;
            if (n < 0) n += _clip.samples;
            if (n <= 0) return;
            var a = new float[n];
            _clip.GetData(a, _tapePos);   // 끝을 넘으면 처음부터 이어 읽는다
            _tape.AddRange(a);
            _tapePos = pos;
        }

        bool ReadMic()
        {
            int pos = Microphone.GetPosition(_device), len = _clip.samples;
            if (pos <= 0 && !Microphone.IsRecording(_device)) { Ok = false; Text = "마이크가 끊겼어요: " + _device; return false; }
            int start = pos - Window; if (start < 0) start += len;
            if (start + Window <= len) _clip.GetData(_buf, start);
            else
            {
                var a = new float[len - start]; var b = new float[Window - a.Length];
                _clip.GetData(a, start); if (b.Length > 0) _clip.GetData(b, 0);
                Array.Copy(a, _buf, a.Length); Array.Copy(b, 0, _buf, a.Length, b.Length);
            }
            return true;
        }

        void Analyze(Dictionary<string, float> values)
        {
            double sum = 0;
            for (int i = 0; i < Window; i++) sum += _buf[i] * _buf[i];
            float db = 10 * Mathf.Log10((float)(sum / Window) + 1e-10f);
            // 바닥: 조용해지면 빨리 내려가고 소리가 나면 아주 천천히 올라간다 (잡음 수준)
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.1f);
            _floor = db < _floor ? Mathf.Lerp(_floor, db, 1 - Mathf.Exp(-dt / 0.3f)) : Mathf.Lerp(_floor, db, 1 - Mathf.Exp(-dt / 8f));
            _floor = Mathf.Clamp(_floor, -90, -25);
            float range = 30f / Mathf.Clamp(Gain, 0.25f, 4f);
            float vol = Mathf.Clamp01((db - _floor - 6) / range);
            vol = vol * vol * (3 - 2 * vol);
            _level = vol;
            values["lip.vol"] = vol;
            if (vol < 0.02f) return;
            if (!Formants(out var f1, out var f2)) { values["lip.aa"] = vol * 0.6f; return; }
            F1 = f1; F2 = f2;
            float total = 0;
            var w = new float[Vowels.Length];
            for (int i = 0; i < Vowels.Length; i++)
            {
                float d1 = Mathf.Log(f1 / Vowels[i].F1) / 0.22f, d2 = Mathf.Log(f2 / Vowels[i].F2) / 0.18f;
                w[i] = Mathf.Exp(-0.5f * (d1 * d1 + d2 * d2)); total += w[i];
            }
            if (total < 1e-6f) { values["lip.aa"] = vol * 0.5f; return; }
            // 가장 그럴듯한 모음이 두드러지게 (섞인 입 모양이 흐릿해 보이지 않게)
            float max = 0; foreach (var x in w) max = Mathf.Max(max, x / total);
            for (int i = 0; i < Vowels.Length; i++)
            {
                float p = w[i] / total;
                values[Vowels[i].Key] = vol * Mathf.Clamp01(p >= max - 1e-4f ? 1 : p * 0.6f);
            }
        }

        /// <summary>LPC 근 중 낮은 두 공진 (150 Hz 위, 대역폭 500 Hz 아래). 찾지 못하면 false.</summary>
        bool Formants(out float f1, out float f2)
        {
            f1 = f2 = 0;
            for (int i = 0; i < Window; i++)
            {
                float pre = _buf[i] - (i > 0 ? 0.97f * _buf[i - 1] : 0);
                _x[i] = pre * (0.54f - 0.46f * Mathf.Cos(2 * Mathf.PI * i / (Window - 1)));
            }
            for (int k = 0; k <= Order; k++)
            {
                double s = 0;
                for (int i = k; i < Window; i++) s += _x[i] * _x[i - k];
                _r[k] = s;
            }
            if (_r[0] <= 1e-12) return false;
            _r[0] *= 1.0001;   // 조금 띄워 안정적으로
            Array.Clear(_a, 0, _a.Length); _a[0] = 1;
            double err = _r[0];
            for (int i = 1; i <= Order; i++)
            {
                double acc = _r[i];
                for (int j = 1; j < i; j++) acc += _a[j] * _r[i - j];
                double k = -acc / err;
                Array.Copy(_a, _tmp, Order + 1);
                for (int j = 1; j < i; j++) _a[j] = _tmp[j] + k * _tmp[i - j];
                _a[i] = k;
                err *= 1 - k * k;
                if (err <= 0) return false;
            }
            // A(z)의 근(뒤랑-커너) → 위쪽 반평면의 근마다 주파수·대역폭. 포락선 봉우리는 "오"처럼 F1·F2가 가까우면 하나로 붙어 근으로 찾는다
            for (int i = 0; i < Order; i++) _z[i] = Complex.Pow(new Complex(0.4, 0.9), i);
            for (int it = 0; it < 200; it++)
            {
                double moved = 0;
                for (int i = 0; i < Order; i++)
                {
                    Complex p = 0, d = 1;
                    for (int j = 0; j <= Order; j++) p = p * _z[i] + _a[j];
                    for (int j = 0; j < Order; j++) if (j != i) d *= _z[i] - _z[j];
                    if (d.Magnitude < 1e-30) d = 1e-30;
                    var next = _z[i] - p / d;
                    moved = Math.Max(moved, (next - _z[i]).Magnitude);
                    _z[i] = next;
                }
                if (moved < 1e-9) break;
            }
            float lo = float.MaxValue, hi = float.MaxValue;
            foreach (var z in _z)
            {
                if (z.Imaginary <= 0) continue;
                double f = z.Phase * Rate / (2 * Math.PI), bw = -Math.Log(Math.Max(z.Magnitude, 1e-9)) * Rate / Math.PI;
                if (f < 150 || bw > 500) continue;
                if (f < lo) { hi = lo; lo = (float)f; } else if (f < hi) hi = (float)f;
            }
            if (hi == float.MaxValue) return false;
            f1 = lo; f2 = hi;
            return true;
        }
        readonly Complex[] _z = new Complex[Order];

        public void Dispose()
        {
            if (_device != null) try { Microphone.End(_device); } catch { }
            Ok = false;
        }
    }
}
