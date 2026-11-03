// 페이셜 트래킹 (미리보기 전용): 얼굴 값(ARKit 52개 이름, 0~1)을 받아 아바타 블렌드쉐이프로 옮겨 SceneBuilder 트래킹 레이어에 넣는다.
//   소스  webcam  렌더러 안에서 웹캠을 읽어 얼굴 값을 계산한다 (WebcamFace.cs)
//         vmc     외부 앱이 보내는 VMC 프로토콜(OSC over UDP, 기본 39539): /VMC/Ext/Blend/Val (이름, 값) … /VMC/Ext/Blend/Apply
//                 iFacialMocap·Waidayo(아이폰 ARKit), XR Animator·VSeeFace(웹캠) 등. ARKit 이름이나 VRM 표정 이름(Blink_L·A·I·U·E·O …)
//   옮기기 (FaceRetarget)  ARKit 이름과 같은 블렌드쉐이프(퍼펙트 싱크)가 있으면 그대로, VRCFaceTracking Unified Expressions 이름(EyeClosedLeft·BrowLowerer·LipFunnel …)도 옮기고,
//                          없으면 얼굴 메시의 눈 감기·입(vrc.v_*)으로 대신 (퍼펙트 싱크여도 눈 감기가 없으면 눈 감기만은 대신)
// 부드럽게 (2026-10-23 사용자: 웹캠 fps가 낮으면 끊겨 보인다 → 스무딩을 크게, 프레임을 강제로라도 높이게):
//   트래킹 결과는 목표값으로만 두고, 렌더 프레임마다 2단 지수 평활(임계 감쇠에 가까움)로 따라간다 → 트래킹이 10~15 fps여도 얼굴·머리는 화면 fps로 움직인다.
//   시간 상수 = 0.4 s × 부드럽게² (0.5 → 0.1 s), "끊김 메우기"(fill)를 켜면 최소 1.3 × 결과 사이 간격(측정) — 다음 결과가 올 때까지 이어 붙인다.
//   눈 감기만 시간 상수 35% (깜빡임이 늦지 않게). 1초마다 Host로 상태(trackingStatus)를 보낸다. 고정(snapshot)하면 지금 레이어 값을 보낸다.
// 머리 회전·시선 본은 FaceHead.cs (머리 각도를 주는 소스: 웹캠, VMC의 Head 본). 부위별 감도(groups)와 머리 켜고 끄기(head).
// 녹화(capture: true → false): 그 사이 매 프레임의 레이어 값을 모아 끝날 때 한 번에 보낸다 (Host가 .anim으로 저장). 최대 10분.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    /// <summary>얼굴 값 소스. Values는 메인 스레드에서만 읽는다.</summary>
    public interface IFaceSource : IDisposable
    {
        string Name { get; }
        bool Ok { get; }
        string Text { get; }
        bool Face { get; }
        float Fps { get; }
        /// <summary>매 프레임. 새 값이 있으면 values를 채우고 true.</summary>
        bool Poll(Dictionary<string, float> values);
    }

    /// <summary>머리 각도도 주는 소스 (도, 사람 기준: yaw 왼쪽 +, pitch 위 +, roll 왼쪽 눈이 올라가면 +).</summary>
    public interface IHeadSource
    {
        bool Head(out Vector3 deg);
        /// <summary>머리 위치 (얼굴 폭 단위, 사람 기준: x 왼쪽 +, y 위 +). 기준은 FaceTracker가 뺀다. 주지 못하면 false.</summary>
        bool Move(out Vector2 pos);
    }

    public sealed class FaceTracker
    {
        readonly SceneBuilder _scene;
        IFaceSource _source;
        FaceRetarget _map;
        readonly Dictionary<string, float> _raw = new Dictionary<string, float>(), _smooth = new Dictionary<string, float>();
        readonly Dictionary<string, float> _target = new Dictionary<string, float>(), _stage = new Dictionary<string, float>();   // 결과(목표) · 평활 1단
        readonly List<string> _keys = new List<string>();
        float _lastSample = -1, _sampleDt = 1 / 30f;   // 결과 사이 간격 (평균)
        Vector3 _headStage;
        float _nextStatus;
        public Action<JNode> Send;
        public bool Mirror;
        // 녹화: 블렌드쉐이프(렌더러, 이름) 목록과 프레임마다 (시간, 값들)
        bool _capturing; float _capStart;
        readonly List<(string R, string S)> _capKeys = new List<(string, string)>();
        readonly Dictionary<(string, string), int> _capIndex = new Dictionary<(string, string), int>();
        readonly List<float> _capTimes = new List<float>();
        readonly List<Dictionary<int, float>> _capVals = new List<Dictionary<int, float>>();
        const float MaxCapture = 600;
        // 캘리브레이션(무표정 보정): 가만히 있을 때의 값(소스 이름 기준)을 빼고 남은 범위를 0~1로 다시 편다. 모으는 동안은 합·개수
        readonly Dictionary<string, float> _neutral = new Dictionary<string, float>(), _calSum = new Dictionary<string, float>();
        int _calCount, _calHeadN; float _calEnd = -1;
        const float CalSeconds = 1.5f;
        public float Smoothing = 0.5f;   // 0 = 바로, 1에 가까울수록 느리게
        public bool Fill = true;          // 끊김 메우기: 트래킹 fps가 낮으면 결과 사이를 이어 붙일 만큼 시간 상수를 늘린다
        public float Gain = 1f;
        // 부위별 감도 (groups: eye 눈 감기·크게·찡그림, gaze 시선, brow 눈썹, mouth 입·턱·혀, cheek 볼·코, head 머리 회전)
        readonly Dictionary<string, float> _groupGain = new Dictionary<string, float>();
        // 머리 회전·시선 본 (FaceHead): 캘리브레이션 때 머리 각도도 기준으로 삼는다 (neutral의 headYaw·headPitch·headRoll)
        FaceHead _headBones;
        public bool HeadOn = true, BodyOn = true, ArmsOn = true, MoveOn = true;   // 고개 · 몸 따라오기 · 팔 자연스럽게 내리기(+숨쉬기) · 머리 위치
        // 머리 위치 (2026-10-24): 기준은 캘리브레이션(neutral의 headX·headY), 없으면 처음 본 자리. 얼굴 폭 → 미터
        Vector2 _pos, _posStage, _posNeutral, _calPos; int _calPosN; bool _posNeutralSet, _posSeen;
        const float FaceWidth = 0.15f;
        // 눈 감기 곡선: 감도(어디서부터 감기나) · 또렷하게(반쯤 감긴 구간을 좁힘) · 두 눈 같이 (값이 비슷하면 하나로)
        float _eyeSens = 0.5f, _eyeSharp = 0.5f; bool _eyeLink = true;
        Vector3 _head, _headNeutral, _calHead; bool _headSeen;
        bool _gazeBones;
        // 수동 연결 (Addr.FaceMap) · 시험: 값 하나를 1로 (트래킹이 꺼져 있어도 보인다)
        JNode _customMap = JNode.Obj();
        string _test; FaceRetarget _testMap;
        // 말하기 → 입 모양 (LipSync.cs): 트래킹과 따로 켜고 끈다. 값(lip.*)은 목표값에 그대로 넣고 짧게만 평활
        LipSync _lip;
        readonly Dictionary<string, float> _lipVals = new Dictionary<string, float>(), _work = new Dictionary<string, float>();

        public FaceTracker(SceneBuilder scene) { _scene = scene; }

        public bool Active => _source != null || _lip != null;
        /// <summary>말하기가 켜져 있으면 그 마이크 (스튜디오 녹화가 소리를 함께 담는다).</summary>
        public LipSync Mic => _lip;

        public void Handle(JNode m)
        {
            if (m.Has("mirror")) Mirror = m["mirror"].AsBool;
            if (m.Has("smoothing")) Smoothing = Mathf.Clamp01(m["smoothing"].AsFloat);
            if (m.Has("fill")) Fill = m["fill"].AsBool;
            if (m.Has("gain")) Gain = Mathf.Clamp(m["gain"].AsFloat, 0.2f, 3f);
            if (m["map"].Kind == JKind.Object)
            {
                _customMap = m["map"].Clone();
                _map?.SetMap(_customMap); _testMap?.SetMap(_customMap);
                if (_test != null) ShowTest();
            }
            if (m["links"].AsBool)
            {
                var fr = _map ?? new FaceRetarget(_scene);
                var auto = JNode.Arr();
                foreach (var (src, r, sh) in fr.Auto) auto.Add(JNode.Obj().Set("src", src).Set("r", r).Set("s", sh));
                Send?.Invoke(Msg.Make(Msg.TrackingStatus).Set("source", _source?.Name ?? "off").Set("ok", true).Set("text", _source?.Text ?? "꺼짐")
                    .Set("auto", auto).Set("perfectSync", fr.PerfectSync).Set("mode", fr.Mode).Set("ftLayers", fr.Ft?.Layers ?? 0).Set("ftShapes", fr.Ft?.Shapes ?? 0));
                return;
            }
            if (m["lip"].Kind == JKind.Object) { Lip(m["lip"]); return; }
            if (m["mics"].AsBool)
            {
                var arr = JNode.Arr(); foreach (var d in LipSync.Devices()) arr.Add(d);
                Send?.Invoke(Status(false).Set("mics", arr));
                return;
            }
            if (m.Has("test"))
            {
                var t = m["test"].AsString;
                _test = string.IsNullOrEmpty(t) ? null : t;
                if (_test != null) ShowTest();
                else if (_source == null) { _scene.SetLayer(SceneBuilder.TrackingLayer, null); _testMap = null; }
                return;
            }
            if (m["eye"].Kind == JKind.Object)
            {
                var e = m["eye"];
                if (e.Has("sensitivity")) _eyeSens = Mathf.Clamp01(e["sensitivity"].AsFloat);
                if (e.Has("sharp")) _eyeSharp = Mathf.Clamp01(e["sharp"].AsFloat);
                if (e.Has("link")) _eyeLink = e["link"].AsBool;
            }
            if (m.Has("body")) BodyOn = m["body"].AsBool;
            if (m.Has("arms")) { ArmsOn = m["arms"].AsBool; if (!ArmsOn) _headBones?.RestoreArms(); }
            if (m["groups"].Kind == JKind.Object) foreach (var kv in m["groups"].Props) _groupGain[kv.Key] = Mathf.Clamp(kv.Value.AsFloat, 0, 3f);
            if (m.Has("head")) { HeadOn = m["head"].AsBool; if (!HeadOn) _headBones?.RestoreHead(); }
            if (m.Has("move")) { MoveOn = m["move"].AsBool; if (!MoveOn) { _headBones?.RestoreMove(); _posSeen = false; } }
            if (m.Has("gain") || m["eye"].Kind == JKind.Object || m["groups"].Kind == JKind.Object)
                Debug.Log($"[VrcProto] 트래킹 옵션: 세기 {Gain:0.##} · 눈 감도 {_eyeSens:0.##} · 또렷하게 {_eyeSharp:0.##} · 두 눈 같이 {_eyeLink} · 부위별 {string.Join(" ", _groupGain.Select(kv => kv.Key + " " + kv.Value.ToString("0.##")))} · 부드럽게 {Smoothing:0.##} · 끊김 {Fill} · 거울 {Mirror} ← {m.ToJson()}");
            if (m["neutral"].Kind == JKind.Object)
            {
                _neutral.Clear(); _headNeutral = Vector3.zero; _posNeutralSet = false;
                foreach (var kv in m["neutral"].Props)
                    switch (kv.Key)
                    {
                        case "headYaw": _headNeutral.x = kv.Value.AsFloat; break;
                        case "headPitch": _headNeutral.y = kv.Value.AsFloat; break;
                        case "headRoll": _headNeutral.z = kv.Value.AsFloat; break;
                        case "headX": _posNeutral.x = kv.Value.AsFloat; _posNeutralSet = true; break;
                        case "headY": _posNeutral.y = kv.Value.AsFloat; _posNeutralSet = true; break;
                        default: _neutral[kv.Key] = Mathf.Clamp(kv.Value.AsFloat, 0, 0.9f); break;
                    }
            }
            if (m["calibrate"].AsBool)
            {
                if (_source == null) { Send?.Invoke(Msg.Make(Msg.TrackingStatus).Set("source", "off").Set("ok", false).Set("text", "트래킹을 켠 뒤 맞출 수 있어요")); return; }
                _calSum.Clear(); _calCount = 0; _calHead = Vector3.zero; _calHeadN = 0; _calPos = Vector2.zero; _calPosN = 0; _calEnd = Time.unscaledTime + CalSeconds;
                return;
            }
            if (m["snapshot"].AsBool) { Send?.Invoke(Status(true)); return; }
            if (m.Has("capture"))
            {
                if (m["capture"].AsBool)
                {
                    _capKeys.Clear(); _capIndex.Clear(); _capTimes.Clear(); _capVals.Clear();
                    _capturing = true; _capStart = Time.unscaledTime;
                }
                else if (_capturing) { _capturing = false; Send?.Invoke(CaptureResult()); }
                return;
            }
            if (m["devices"].AsBool)
            {
                var arr = JNode.Arr(); foreach (var d in WebcamFace.Devices()) arr.Add(d);
                Send?.Invoke(Status(false).Set("devices", arr));
                return;
            }
            if (!m.Has("source")) return;
            Stop();
            try
            {
                switch (m["source"].AsString)
                {
                    case "vmc": _source = new VmcSource(m.Has("port") ? m["port"].AsInt : 39539); break;
                    case "osc": _source = new OscFaceSource(m.Has("port") ? m["port"].AsInt : 8888); break;
                    case "webcam": _source = new WebcamFace(m["camera"].AsString); break;
                    case "image": _source = new WebcamFace(null, m["path"].AsString); break;   // 시험용: 사진 한 장을 웹캠처럼
                }
            }
            catch (Exception e) { Stop(); Send?.Invoke(Msg.Make(Msg.TrackingStatus).Set("source", m["source"].AsString).Set("ok", false).Set("text", "시작하지 못했어요: " + e.Message)); return; }
            _map = _source != null || _lip != null ? new FaceRetarget(_scene, _customMap) : null;
            _headBones = _source != null ? new FaceHead(_scene) : null;
            _head = Vector3.zero; _headSeen = false; _posSeen = false;
            _nextStatus = 0;
            if (_source == null) Send?.Invoke(Msg.Make(Msg.TrackingStatus).Set("source", "off").Set("ok", true).Set("text", "꺼짐"));
        }

        void Capture(List<(string, string, float)> layer)
        {
            float t = Time.unscaledTime - _capStart;
            if (t > MaxCapture) { _capturing = false; Send?.Invoke(CaptureResult()); return; }
            var row = new Dictionary<int, float>();
            foreach (var (r, s, w) in layer)
            {
                if (!_capIndex.TryGetValue((r, s), out var k)) { k = _capKeys.Count; _capKeys.Add((r, s)); _capIndex[(r, s)] = k; }
                row[k] = w;
            }
            _capTimes.Add(t); _capVals.Add(row);
        }

        /// <summary>{ capture: { shapes: [{r, s}], times: [초], values: [[값…]…] } } — 값 줄은 shapes 순서, 없던 값은 0.</summary>
        JNode CaptureResult()
        {
            var shapes = JNode.Arr(); foreach (var (r, s) in _capKeys) shapes.Add(JNode.Obj().Set("r", r).Set("s", s));
            var times = JNode.Arr(); var values = JNode.Arr();
            for (int i = 0; i < _capTimes.Count; i++)
            {
                times.Add(Mathf.Round(_capTimes[i] * 1000) / 1000);
                var row = new float[_capKeys.Count];
                foreach (var kv in _capVals[i]) row[kv.Key] = Mathf.Round(kv.Value * 10) / 10;
                values.Add(JNode.FloatArray(row));
            }
            var m = Msg.Make(Msg.TrackingStatus).Set("source", _source?.Name ?? "off").Set("ok", true).Set("text", _source?.Text ?? "")
                .Set("capture", JNode.Obj().Set("shapes", shapes).Set("times", times).Set("values", values));
            _capKeys.Clear(); _capIndex.Clear(); _capTimes.Clear(); _capVals.Clear();
            return m;
        }

        /// <summary>트래킹 소스를 끈다 (말하기는 켜 둔 채로 둘 수 있다).</summary>
        public void Stop()
        {
            if (_capturing && _lip == null) { _capturing = false; Send?.Invoke(CaptureResult()); }
            if (_source != null) { _source.Dispose(); _source = null; }
            _raw.Clear(); _smooth.Clear(); _target.Clear(); _stage.Clear(); _lastSample = -1;
            _headBones?.Restore(); _headBones = null; _headSeen = false; _posSeen = false; _gazeBones = false;
            if (_lip == null) { _map = null; _scene.SetLayer(SceneBuilder.TrackingLayer, null); }
        }

        /// <summary>렌더러를 닫거나 다시 시작할 때: 트래킹과 말하기 모두.</summary>
        public void StopAll()
        {
            _lip?.Dispose(); _lip = null;
            Stop();
        }

        /// <summary>말하기(립싱크) 켜고 끄기: { on, mic?, gain?, wav?: 시험용 16 kHz WAV }.</summary>
        void Lip(JNode l)
        {
            if (l.Has("gain") && _lip != null) _lip.Gain = Mathf.Clamp(l["gain"].AsFloat, 0.25f, 4f);
            if (!l.Has("on")) return;
            _lip?.Dispose(); _lip = null;
            if (l["on"].AsBool)
            {
                try { _lip = l.Has("wav") ? new LipSync(LipSync.ReadWav(l["wav"].AsString)) : new LipSync(l["mic"].AsString); }
                catch (Exception e) { Send?.Invoke(Status(false).Set("lip", JNode.Obj().Set("on", false).Set("ok", false).Set("text", "마이크를 열지 못했어요: " + e.Message))); return; }
                if (l.Has("gain")) _lip.Gain = Mathf.Clamp(l["gain"].AsFloat, 0.25f, 4f);
                _map ??= new FaceRetarget(_scene, _customMap);
            }
            else
            {
                foreach (var k in new List<string>(_target.Keys)) if (k.StartsWith("lip.")) { _target.Remove(k); _stage.Remove(k); _smooth.Remove(k); }
                if (_source == null) { _map = null; _target.Clear(); _smooth.Clear(); _stage.Clear(); _scene.SetLayer(SceneBuilder.TrackingLayer, null); }
            }
            _nextStatus = 0;
        }

        /// <summary>장면을 다시 불러오면 옮길 곳을 다시 찾는다.</summary>
        public void OnSceneLoaded()
        {
            _testMap = null; _test = null;
            if (_source != null || _lip != null) _map = new FaceRetarget(_scene, _customMap);
            if (_source != null) { _headBones = new FaceHead(_scene); _headSeen = false; _posSeen = false; }
        }

        /// <summary>시험: 고른 값 하나만 1로 두고 연결된 블렌드쉐이프를 보여 준다.</summary>
        void ShowTest()
        {
            var map = _map ?? (_testMap ??= new FaceRetarget(_scene, _customMap));
            _scene.SetLayer(SceneBuilder.TrackingLayer, map.Apply(new Dictionary<string, float> { [_test] = 1f }));
        }

        float GroupGain(string name)
        {
            string g = name.StartsWith("eyeLook") ? "gaze" : name.StartsWith("eye") ? "eye" : name.StartsWith("brow") ? "brow"
                : name.StartsWith("cheek") || name.StartsWith("nose") ? "cheek" : "mouth";
            return _groupGain.TryGetValue(g, out var x) ? x : 1f;
        }

        public void Update()
        {
            if (_source == null && _lip == null) return;
            if (_source != null && _source.Poll(_raw))
            {
                if (_calEnd >= 0) Calibrate();
                float now = Time.unscaledTime;
                if (_lastSample >= 0) _sampleDt = Mathf.Lerp(_sampleDt, Mathf.Clamp(now - _lastSample, 1 / 120f, 0.5f), 0.2f);
                _lastSample = now;
                foreach (var kv in _raw)
                {
                    var name = Mirror ? FaceRetarget.Swap(kv.Key) : kv.Key;
                    float raw = kv.Value;
                    if (_neutral.TryGetValue(kv.Key, out var n)) raw = (raw - n) / Mathf.Max(0.1f, 1 - n);
                    bool blink = kv.Key == "eyeBlinkLeft" || kv.Key == "eyeBlinkRight";
                    float g = Gain * GroupGain(kv.Key);
                    float v = blink ? EyeCurve(EyeGain(raw, g)) : Mathf.Clamp01(raw * g);
                    _target[name] = v;
                }
                LimitMouth(_target, _source.Name == "webcam");
            }
            if (_lip != null) { _lip.Poll(_lipVals); foreach (var kv in _lipVals) _target[kv.Key] = kv.Value; }
            if (_target.Count > 0)
            {
                // 렌더 프레임마다 목표값을 2단 평활로 따라간다 (트래킹 fps와 상관없이 화면 fps로 움직임)
                float tau = SmoothTau(), dt = Time.unscaledDeltaTime;
                _keys.Clear(); _keys.AddRange(_target.Keys);
                foreach (var name in _keys)
                {
                    float t = _target[name];
                    // 말하기는 소리 그대로 빠르게 (입이 늦게 따라오면 어색하다), 눈 감기는 35%
                    float a = Alpha(name.StartsWith("lip.") ? Mathf.Min(tau, 0.03f) : name.StartsWith("eyeBlink") ? tau * 0.35f : tau, dt);
                    float s1 = _stage.TryGetValue(name, out var o1) ? Mathf.Lerp(o1, t, a) : t;
                    _stage[name] = s1;
                    _smooth[name] = _smooth.TryGetValue(name, out var o2) ? Mathf.Lerp(o2, s1, a) : s1;
                }
                if (_eyeLink && _smooth.TryGetValue("eyeBlinkLeft", out var bl) && _smooth.TryGetValue("eyeBlinkRight", out var br) && Mathf.Abs(bl - br) < 0.3f)
                    _smooth["eyeBlinkLeft"] = _smooth["eyeBlinkRight"] = Mathf.Max(bl, br);   // 윙크가 아니면 두 눈을 같이 (한쪽만 반쯤 감기는 것을 막음)
                if (_map != null && _test == null)
                {
                    var layer = _map.Apply(WithLip(_smooth));
                    _map.CloseEyes(layer);
                    _scene.SetLayer(SceneBuilder.TrackingLayer, layer);
                    _lastLayer = layer;
                    if (_capturing) Capture(layer);
                }
                // 시선: 얼굴 메시에 시선 블렌드쉐이프가 없을 때만 눈 본으로
                _gazeBones = _map != null && !_map.HasGaze && _headBones != null && _headBones.HasEyes;
                if (_gazeBones) { var (l, r) = FaceHead.Gaze(_smooth); _headBones.Eyes(l, r); }
            }
            Head();
            if (_headBones != null) _headBones.Idle(ArmsOn, Time.unscaledTime);
            if (Time.unscaledTime >= _nextStatus) { _nextStatus = Time.unscaledTime + 1; Send?.Invoke(Status(false)); }
            if (_source != null && Time.unscaledTime >= _nextDiag) { _nextDiag = Time.unscaledTime + 1; EyeDiag(); }
        }

        List<(string, string, float)> _lastLayer;
        float _nextDiag;

        /// <summary>
        /// 진단 (2026-10-24, 사용자: 실제 트래킹에서만 한쪽 눈이 반쯤 감김 — 시험·VRChat은 정상): 1초마다 렌더러 로그에
        /// 받은 감기 값 → 목표(무표정 보정·세기·곡선 뒤) → 평활 → 레이어의 눈 블렌드쉐이프 → 메시에 실제로 들어간 눈 블렌드쉐이프 값을 남긴다.
        /// </summary>
        void EyeDiag()
        {
            float R(Dictionary<string, float> d, string k) => d.TryGetValue(k, out var x) ? x : -1;
            var sb = new StringBuilder("[VrcProto] 눈 진단 ");
            sb.Append(_source.Name).Append(" · ").Append(_map?.Mode ?? "-").Append(Mirror ? " · 거울" : "");
            sb.AppendFormat(" · 세기 {0:0.##} 눈 {1:0.##} 감도 {2:0.##} 또렷 {3:0.##}", Gain, GroupGain("eyeBlinkLeft"), _eyeSens, _eyeSharp);
            sb.AppendFormat(" | 받은 감기 L {0:0.00} R {1:0.00}", R(_raw, "eyeBlinkLeft"), R(_raw, "eyeBlinkRight"));
            sb.AppendFormat(" | 목표 L {0:0.00} R {1:0.00}", R(_target, "eyeBlinkLeft"), R(_target, "eyeBlinkRight"));
            sb.AppendFormat(" | 평활 L {0:0.00} R {1:0.00}", R(_smooth, "eyeBlinkLeft"), R(_smooth, "eyeBlinkRight"));
            if (_source is OscFaceSource o) sb.Append(" | ").Append(o.EyeDiag);
            var rs = new HashSet<string>();
            if (_lastLayer != null)
            {
                sb.Append(" | 레이어");
                foreach (var (r, s, w) in _lastLayer)
                    if ((MeshOps.IsBlinkName(s) || MeshOps.IsEyeName(s)) && Mathf.Abs(w) > 0.5f) { sb.AppendFormat(" {0} {1:0}", s, w); rs.Add(r); }
            }
            foreach (var r in rs)
            {
                sb.Append(" | 메시");
                foreach (var (s, w) in _scene.CurrentWeights(r, n => MeshOps.IsBlinkName(n) || MeshOps.IsEyeName(n))) sb.AppendFormat(" {0} {1:0}", s, w);
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 입 모양(viseme.*) 연결이 없는 아바타(퍼펙트 싱크·FT 애니메이터)는 말하기 값을 입 벌림·오므림·늘이기로 바꿔 트래킹 값과 큰 쪽으로 합친다.
        /// </summary>
        Dictionary<string, float> WithLip(Dictionary<string, float> v)
        {
            if (_lip == null || _map == null || _map.HasVisemes) return v;
            float L(string k) => v.TryGetValue(k, out var x) ? x : 0;
            float aa = L("lip.aa"), ih = L("lip.ih"), ou = L("lip.ou"), e = L("lip.e"), oh = L("lip.oh");
            if (aa + ih + ou + e + oh < 0.01f) return v;
            _work.Clear();
            foreach (var kv in v) _work[kv.Key] = kv.Value;
            void Max(string k, float x) => _work[k] = Mathf.Max(L(k), Mathf.Clamp01(x));
            Max("jawOpen", aa * 0.75f + oh * 0.5f + e * 0.35f + ih * 0.2f + ou * 0.25f);
            Max("mouthFunnel", oh * 0.7f);
            Max("mouthPucker", ou * 0.8f + oh * 0.2f);
            foreach (var s in new[] { "Left", "Right" })
            {
                Max("mouthStretch" + s, e * 0.5f + ih * 0.4f);
                Max("mouthSmile" + s, ih * 0.3f);
                Max("mouthLowerDown" + s, aa * 0.3f + e * 0.2f);
            }
            return _work;
        }

        /// <summary>
        /// 입 벌림보다 큰 입술 값을 자른다 (2026-10-24 사용자 화면: 웹캠에서 입이 깨짐, E2E faceosc로 FT 기믹을 넣은 시험 아바타에서 확인).
        /// mouthClose가 jawOpen보다 크면 입술이 서로 지나가 입선이 사라진다 (ARKit 정의상 mouthClose ≤ jawOpen) — 모든 소스.
        /// 웹캠(MediaPipe)은 입을 조금만 벌려도 mouthUpperUp·mouthLowerDown을 크게 내는데, 입 안은 jawOpen만큼만 열려 있어
        /// 입선과 입 안 사이에 피부 띠가 생기거나(윗입술이 들쭉날쭉) 입 안 없이 테두리만 남는다: 윗입술 올리기는 jawOpen의 절반, 아랫입술 내리기는 jawOpen까지.
        /// </summary>
        static void LimitMouth(Dictionary<string, float> t, bool webcam)
        {
            float jaw = t.TryGetValue("jawOpen", out var j) ? j : 0;
            void Cap(string k, float max) { if (t.TryGetValue(k, out var x) && x > max) t[k] = max; }
            Cap("mouthClose", jaw);
            if (!webcam) return;
            Cap("mouthUpperUpLeft", jaw * 0.5f); Cap("mouthUpperUpRight", jaw * 0.5f);
            Cap("mouthLowerDownLeft", jaw); Cap("mouthLowerDownRight", jaw);
        }

        /// <summary>2단 평활의 단마다 시간 상수 (초). 끊김 메우기면 결과 사이 간격을 덮을 만큼은 늘린다.</summary>
        float SmoothTau()
        {
            float tau = 0.4f * Smoothing * Smoothing;
            if (Fill) tau = Mathf.Max(tau, 1.3f * _sampleDt);
            return tau * 0.5f;   // 두 단을 거치면 전체 지연은 대략 tau
        }

        static float Alpha(float tau, float dt) => tau <= 1e-4f ? 1 : 1 - Mathf.Exp(-dt / tau);

        /// <summary>
        /// 머리 회전·위치: 소스가 머리 각도를 주면 기준을 빼고 부드럽게 해서 목·머리 본에 더한다.
        /// 머리 위치를 주면 기준(캘리브레이션, 없으면 처음 본 자리)에서 옮겨 간 만큼 몸을 기울이고 Hips를 올리고 내린다 (FaceHead.Move).
        /// </summary>
        void Head()
        {
            if (_headBones == null || !_headBones.HasHead) return;
            var hs = _source as IHeadSource;
            Vector3 deg = default; Vector2 pos = default;
            bool turn = HeadOn && hs != null && hs.Head(out deg);
            bool move = MoveOn && hs != null && hs.Move(out pos);
            float a = Alpha(SmoothTau(), Time.unscaledDeltaTime);
            if (move)
            {
                if (_calEnd >= 0) { _calPos += pos; _calPosN++; }
                if (!_posNeutralSet) { _posNeutral = pos; _posNeutralSet = true; }
                var wantPos = (pos - _posNeutral) * FaceWidth;
                if (_posSeen) { _posStage = Vector2.Lerp(_posStage, wantPos, a); _pos = Vector2.Lerp(_pos, _posStage, a); }
                else _posStage = _pos = wantPos;
                _posSeen = true;
                _headBones.Move(_pos, Mirror);
            }
            else if (_posSeen) { _headBones.RestoreMove(); _posSeen = false; }
            if (!turn && !move) { if (_headSeen) { _headBones.RestoreHead(); _headSeen = false; } return; }
            var want = Vector3.zero;
            if (turn)
            {
                if (_calEnd >= 0) { _calHead += deg; _calHeadN++; }
                float g = _groupGain.TryGetValue("head", out var x) ? x : 1f;
                want = (deg - _headNeutral) * g;
                want = new Vector3(Mathf.Clamp(want.x, -70, 70), Mathf.Clamp(want.y, -50, 50), Mathf.Clamp(want.z, -45, 45));
            }
            if (_headSeen) { _headStage = Vector3.Lerp(_headStage, want, a); _head = Vector3.Lerp(_head, _headStage, a); }
            else _headStage = _head = want;
            _headSeen = true;
            _headBones.Head(_head, Mirror, BodyOn, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 눈 감기 세기(전체 세기 × 부위별 감도): 1보다 크면 곱해서 일찍 감기고, 작으면 거듭제곱(v^(1/세기))으로 가운데만 줄인다 —
        /// 다 감은 눈(1)은 어떤 세기에서도 1 (2026-10-25 사용자: Baballonia로 다 감아도 64%만 감김, 로그 역산 세기 0.44 · 감도 0.72 · 또렷하게 0).
        /// </summary>
        static float EyeGain(float v, float g)
        {
            v = Mathf.Clamp01(v);
            if (g <= 0.001f) return 0;
            return g >= 1 ? Mathf.Clamp01(v * g) : Mathf.Pow(v, 1 / g);
        }

        /// <summary>
        /// 눈 감기 곡선: 감도로 가운데(반쯤 감긴 것으로 볼 값)를, 또렷하게로 0→1이 바뀌는 폭을 정한다 (smoothstep).
        /// 감도 0.5·또렷하게 0.5 = 0.29 아래는 뜬 눈, 0.71 위는 감은 눈. 또렷하게 1이면 거의 뜨거나 감거나 둘 중 하나.
        /// 바뀌는 구간은 0~1 안으로 자른다 — 끝점 고정: 0은 언제나 뜬 눈, 1은 언제나 다 감은 눈 (예전엔 또렷하게를 낮추면 구간이 1 밖으로 나가 다 감아도 덜 감겼다).
        /// </summary>
        float EyeCurve(float v)
        {
            float c = 0.8f - 0.6f * _eyeSens, w = 0.05f + 0.75f * (1 - _eyeSharp);
            float lo = Mathf.Max(0, c - w / 2), hi = Mathf.Min(1, c + w / 2);
            float t = Mathf.Clamp01((v - lo) / Mathf.Max(1e-3f, hi - lo));
            return t * t * (3 - 2 * t);
        }

        /// <summary>보정 중인 프레임: 소스 값을 모으고, 시간이 다 되면 평균을 무표정 기준으로 삼아 Host에 알린다 (Host가 저장했다가 다음에 켤 때 보낸다).</summary>
        void Calibrate()
        {
            foreach (var kv in _raw) _calSum[kv.Key] = (_calSum.TryGetValue(kv.Key, out var o) ? o : 0) + kv.Value;
            _calCount++;
            if (Time.unscaledTime < _calEnd) return;
            _calEnd = -1;
            var m = Msg.Make(Msg.TrackingStatus).Set("source", _source?.Name ?? "off").Set("ok", true).Set("text", _source?.Text ?? "");
            if (_calCount < 5) { Send?.Invoke(m.Set("ok", false).Set("text", "얼굴이 잘 보이지 않아 맞추지 못했어요. 카메라를 정면으로 보고 다시 해 보세요")); return; }
            _neutral.Clear();
            var obj = JNode.Obj();
            if (_calHeadN > 0)
            {
                _headNeutral = _calHead / _calHeadN;
                obj.Set("headYaw", Mathf.Round(_headNeutral.x * 10) / 10).Set("headPitch", Mathf.Round(_headNeutral.y * 10) / 10).Set("headRoll", Mathf.Round(_headNeutral.z * 10) / 10);
            }
            if (_calPosN > 0)
            {
                _posNeutral = _calPos / _calPosN; _posNeutralSet = true;
                obj.Set("headX", Mathf.Round(_posNeutral.x * 1000) / 1000).Set("headY", Mathf.Round(_posNeutral.y * 1000) / 1000);
            }
            foreach (var kv in _calSum)
            {
                float avg = kv.Value / _calCount;
                if (avg < 0.02f) continue;   // 거의 0이면 뺄 것이 없다
                _neutral[kv.Key] = Mathf.Min(avg, 0.9f);
                obj.Set(kv.Key, Mathf.Round(_neutral[kv.Key] * 1000) / 1000);
            }
            Send?.Invoke(m.Set("neutral", obj));
        }

        JNode Status(bool snapshot)
        {
            var m = Msg.Make(Msg.TrackingStatus).Set("source", _source?.Name ?? "off").Set("ok", _source?.Ok ?? true)
                .Set("text", _source?.Text ?? "꺼짐").Set("fps", _source?.Fps ?? 0).Set("face", _source?.Face ?? false)
                .Set("mapped", (_map?.Count ?? 0) + (_map?.Ft?.Shapes ?? 0)).Set("perfectSync", _map?.PerfectSync ?? false)
                .Set("mode", _map?.Mode ?? "").Set("ftLayers", _map?.Ft?.Layers ?? 0)
                .Set("head", _headSeen).Set("gazeBones", _gazeBones)
                .Set("frame", Time.frameCount).Set("gc", GC.CollectionCount(0))   // 시험용: 화면 프레임 수·GC 횟수 (트래킹 중 끊김 확인)
                .Set("headDeg", JNode.FloatArray(new[] { Mathf.Round(_head.x), Mathf.Round(_head.y), Mathf.Round(_head.z) }));
            var vals = JNode.Obj();
            foreach (var kv in _smooth) vals.Set(kv.Key, Mathf.Round(kv.Value * 1000) / 1000);
            m.Set("values", vals);
            if (_lip != null)
                m.Set("lip", JNode.Obj().Set("on", true).Set("ok", _lip.Ok).Set("text", _lip.Text).Set("level", Mathf.Round(_lip.Level * 100) / 100)
                    .Set("f1", Mathf.Round(_lip.F1)).Set("f2", Mathf.Round(_lip.F2)).Set("visemes", _map?.HasVisemes ?? false));
            if (snapshot)
            {
                var arr = JNode.Arr();
                var snapLayer = _map?.Apply(WithLip(_smooth));
                _map?.CloseEyes(snapLayer);
                if (snapLayer != null) foreach (var (r, s, w) in snapLayer) arr.Add(JNode.Obj().Set("r", r).Set("s", s).Set("v", Mathf.Round(w * 10) / 10));
                m.Set("snapshot", true).Set("shapes", arr);
            }
            return m;
        }
    }

    /// <summary>얼굴 값 이름(ARKit 52개·VRM 표정·VRChat 입 모양) → 아바타 블렌드쉐이프.</summary>
    public sealed class FaceRetarget
    {
        public static readonly string[] ArKit =
        {
            "eyeBlinkLeft", "eyeLookDownLeft", "eyeLookInLeft", "eyeLookOutLeft", "eyeLookUpLeft", "eyeSquintLeft", "eyeWideLeft",
            "eyeBlinkRight", "eyeLookDownRight", "eyeLookInRight", "eyeLookOutRight", "eyeLookUpRight", "eyeSquintRight", "eyeWideRight",
            "jawForward", "jawLeft", "jawRight", "jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
            "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight",
            "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
            "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft", "mouthUpperUpRight",
            "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight", "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
            "noseSneerLeft", "noseSneerRight", "tongueOut",
        };

        /// <summary>ARKit 52개 밖이지만 Unified Expressions·Project Babble이 주는 값 (같은 이름의 블렌드쉐이프가 있으면 그대로 잇는다).</summary>
        public static readonly string[] Extra =
        {
            "cheekSuckLeft", "cheekSuckRight", "tongueUp", "tongueDown", "tongueLeft", "tongueRight", "tongueRoll", "tongueBendDown",
            "tongueCurlUp", "tongueSquish", "tongueFlat", "tongueTwistLeft", "tongueTwistRight",
        };

        /// <summary>
        /// 페이셜 쉐이프키(퍼펙트 싱크·FT 애니메이터)가 없을 때 쓰는 대신할 블렌드쉐이프 후보 (앞에 있는 것부터, 정규화한 이름으로 찾는다).
        /// 기본은 MMD 표준 모프 (2026-10-24 사용자: 기본 연결은 MMD 쉐이프키로) — 입 모양은 あいうえお가 먼저, 없으면 vrc.v_*.
        /// mmd.* 는 MMD 표정 모프로 옮기는 값 (Apply의 Get): 웃는 입에 맞춰 눈도 웃고(笑い) 감으면 0 — FT 기믹의 동작을 따랐다.
        /// </summary>
        static readonly (string Source, string[] Targets)[] Fallback =
        {
            ("eyeBlinkLeft", new[] { "blinkl", "blinkleft", "eyeblinkl", "eyeblinkleft", "eyeclosel", "eyecloseleft", "winkl", "winkleft", "まばたき左", "ウィンク２", "ウィンク2", "ウィンク左", "ウィンク" }),
            ("eyeBlinkRight", new[] { "blinkr", "blinkright", "eyeblinkr", "eyeblinkright", "eyecloser", "eyecloseright", "winkr", "winkright", "まばたき右", "ｳｨﾝｸ２右", "ウィンク２右", "ウィンク2右", "ウィンク右" }),
            ("eyeBlink", new[] { "まばたき", "blink", "eyeblink", "eyeblink1", "eyeclose", "eyeclose1", "blinkboth" }),
            ("viseme.aa", new[] { "あ", "vrcvaa", "vaa", "mouthaopen", "mouthaa", "mouth_a" }),
            ("viseme.ih", new[] { "い", "vrcvih", "vih", "mouthi" }),
            ("viseme.ou", new[] { "う", "vrcvou", "vou", "mouthu" }),
            ("viseme.e", new[] { "え", "vrcve", "ve", "mouthe" }),
            ("viseme.oh", new[] { "お", "vrcvoh", "voh", "moutho" }),
            ("mmd.smileEye", new[] { "笑い", "にこ目", "smileeye" }),
            ("mmd.surprise", new[] { "びっくり", "見開き" }),
            ("mmd.browSad", new[] { "困る" }),
            ("mmd.browDown", new[] { "怒り", "下" }),
            ("mmd.browUp", new[] { "上" }),
            ("mmd.smile", new[] { "口角上げ", "にっこり", "にやり" }),
            ("mmd.frown", new[] { "口角下げ" }),
            ("mmd.wide", new[] { "口横広げ" }),
            ("mmd.tongue", new[] { "ぺろっ", "てへぺろ", "舌出し" }),
            ("mmd.puff", new[] { "ぷくー", "頬膨らみ" }),
        };
        static readonly HashSet<string> MmdNames = new HashSet<string>
        {
            "まばたき", "ウィンク", "ウィンク右", "ウィンク２", "ｳｨﾝｸ２右", "あ", "い", "う", "え", "お", "笑い", "びっくり", "困る", "怒り", "にっこり", "にやり", "口角上げ", "口角下げ",
        };

        readonly List<(string Source, string Renderer, string Shape)> _links = new List<(string, string, string)>();   // 자동 연결
        List<(string Source, string Renderer, string Shape, float K)> _final;   // 자동 − 끈 것 + 수동 (세기 K). 처음 쓸 때 자동 그대로
        List<(string Source, string Renderer, string Shape, float K)> Final => _final ??= _links.ConvertAll(l => (l.Source, l.Renderer, l.Shape, 1f));
        public int Count => Final.Count;
        /// <summary>이름으로 찾은 자동 연결 (Host의 수동 연결 창이 보여 준다).</summary>
        public IReadOnlyList<(string Source, string Renderer, string Shape)> Auto => _links;

        /// <summary>수동 연결 { add: [{ src, r, s, k }], off: [{ src, r, s }] } 을 자동 연결 위에 얹는다 (Host Core.FaceMap과 같은 규칙).</summary>
        public void SetMap(JNode map)
        {
            var off = new HashSet<(string, string, string)>();
            foreach (var x in map["off"].Items) off.Add((x["src"].AsString, x["r"].AsString, x["s"].AsString));
            var list = new List<(string, string, string, float)>();
            foreach (var (src, r, sh) in _links) if (!off.Contains((src, r, sh))) list.Add((src, r, sh, 1f));
            foreach (var a in map["add"].Items)
            {
                var key = (a["src"].AsString, a["r"].AsString, a["s"].AsString);
                float k = a.Has("k") ? Mathf.Clamp(a["k"].AsFloat, 0, 2) : 1;
                int i = list.FindIndex(l => l.Item1 == key.Item1 && l.Item2 == key.Item2 && l.Item3 == key.Item3);
                if (i >= 0) list[i] = (key.Item1, key.Item2, key.Item3, k); else list.Add((key.Item1, key.Item2, key.Item3, k));
            }
            _final = list;
        }
        /// <summary>시선 블렌드쉐이프가 이어졌는지 (없으면 FaceHead가 눈 본으로 시선을 옮긴다).</summary>
        public bool HasGaze => (Ft != null && Ft.HasGaze) || Final.Exists(l => l.Source.StartsWith("eyeLook") || l.Source.StartsWith("both:eyeLook"));
        /// <summary>입 모양(viseme.*)이 이어졌는지 — 아니면 말하기(립싱크)를 입 벌림·오므림 값으로 바꿔 넣는다.</summary>
        public bool HasVisemes => Final.Exists(l => l.Source.StartsWith("viseme."));
        public bool PerfectSync { get; }
        /// <summary>아바타에 설치된 페이셜 트래킹 애니메이터 (FT 기믹 등). 있으면 이름으로 잇지 않고 그 블렌드 트리를 VRCFT처럼 돌린다.</summary>
        public FaceFt Ft { get; }
        /// <summary>연결 방식: ft 설치된 FT 애니메이터 · sync 퍼펙트 싱크(ARKit·Unified 이름) · mmd MMD 모프 · basic 눈·입(vrc.v_*)만.</summary>
        public string Mode { get; }
        readonly SceneBuilder _scene;

        public static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) if (c != '_' && c != '-' && c != '.' && c != ' ') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static string Swap(string name) =>
            name.EndsWith("Left") ? name.Substring(0, name.Length - 4) + "Right" : name.EndsWith("Right") ? name.Substring(0, name.Length - 5) + "Left" : name;

        /// <summary>
        /// VRCFaceTracking Unified Expressions 이름(정규화) → 얼굴 값 이름. ARKit 이름과 같은 것(EyeLookOutLeft·JawOpen·MouthSmileLeft …)은 위에서 이미 잡힌다.
        /// "both:X" = 좌우 하나로 합친 블렌드쉐이프 (XLeft·XRight의 평균, 그 메시에 좌우 따로인 것이 없을 때만).
        /// </summary>
        static readonly Dictionary<string, string> Unified = new Dictionary<string, string>
        {
            ["eyeclosedleft"] = "eyeBlinkLeft", ["eyeclosedright"] = "eyeBlinkRight", ["eyeclosed"] = "both:eyeBlink",
            ["eyelookup"] = "both:eyeLookUp", ["eyelookdown"] = "both:eyeLookDown", ["eyesquint"] = "both:eyeSquint", ["eyewide"] = "both:eyeWide",
            ["browlowererleft"] = "browDownLeft", ["browlowererright"] = "browDownRight", ["browlowerer"] = "both:browDown",
            ["browinnerupleft"] = "browInnerUp", ["browinnerupright"] = "browInnerUp", ["browouterup"] = "both:browOuterUp",
            ["nosesneer"] = "both:noseSneer", ["cheeksquint"] = "both:cheekSquint", ["cheekpuffleft"] = "cheekPuff", ["cheekpuffright"] = "cheekPuff",
            ["mouthclosed"] = "mouthClose", ["lipsuckupper"] = "mouthRollUpper", ["lipsucklower"] = "mouthRollLower",
            ["lipfunnel"] = "mouthFunnel", ["lippucker"] = "mouthPucker",
            ["mouthupperup"] = "both:mouthUpperUp", ["mouthlowerdown"] = "both:mouthLowerDown", ["mouthsmile"] = "both:mouthSmile",
            ["mouthsad"] = "both:mouthFrown", ["mouthsadleft"] = "mouthFrownLeft", ["mouthsadright"] = "mouthFrownRight",
            ["mouthstretch"] = "both:mouthStretch", ["mouthraiser"] = "mouthShrugLower", ["mouthraiserlower"] = "mouthShrugLower", ["mouthraiserupper"] = "mouthShrugUpper",
            ["mouthpress"] = "both:mouthPress", ["mouthdimple"] = "both:mouthDimple",
        };

        public FaceRetarget(SceneBuilder scene)
        {
            _scene = scene;
            // 아바타에 FT 애니메이터가 설치되어 있으면 그 연결을 쓴다 (2026-10-24 사용자: 페이셜 트래킹 기믹 같은 기존 페이셜이 있으면 그 쉐이프키로)
            Ft = FaceFt.Load(scene);
            if (Ft != null) { PerfectSync = true; Mode = "ft"; return; }
            var arkit = new Dictionary<string, string>();
            foreach (var a in ArKit) arkit[Norm(a)] = a;
            foreach (var a in Extra) arkit[Norm(a)] = a;
            var mmdNorm = new HashSet<string>();
            foreach (var m in MmdNames) mmdNorm.Add(Norm(m));
            string face = null; int faceScore = -1;
            foreach (var id in scene.SkinnedIds())
            {
                int score = 0;
                // 1. ARKit 이름 그대로 → 2. Unified Expressions 이름 (같은 얼굴 값이 이미 잡혔으면 겹쳐 두 번 움직이지 않게 건너뜀)
                var mine = new List<(string Source, string Shape)>();
                var later = new List<(string Source, string Shape)>();
                foreach (var shape in scene.ShapeNames(id))
                {
                    var n = Norm(shape);
                    if (arkit.TryGetValue(n, out var a)) { mine.Add((a, shape)); score += 2; }
                    else if (Unified.TryGetValue(n, out var u)) { later.Add((u, shape)); score += 2; }
                    else if (n.StartsWith("vrcv") || mmdNorm.Contains(n)) score++;
                }
                // ARKit 이름으로 이미 잡힌 값은 건너뛴다. Unified끼리는 좌우로 나뉜 같은 값(BrowInnerUpLeft·Right → browInnerUp)이 둘 다 들어간다
                var exact = new HashSet<string>();
                foreach (var (s, _) in mine) exact.Add(s);
                var have = new HashSet<string>(exact);
                foreach (var (s, shape) in later)
                {
                    if (s.StartsWith("both:") || exact.Contains(s)) continue;
                    mine.Add((s, shape)); have.Add(s);
                }
                foreach (var (s, shape) in later)
                {
                    if (!s.StartsWith("both:")) continue;
                    var b = s.Substring(5);
                    if (have.Contains(b + "Left") || have.Contains(b + "Right") || have.Contains(s) || have.Contains(b)) continue;
                    mine.Add((b == "eyeBlink" ? b : s, shape)); have.Add(s);   // 두 눈 감기는 두 값 중 큰 쪽 (Get "eyeBlink")
                }
                foreach (var (s, shape) in mine) _links.Add((s, id, shape));
                if (score > faceScore) { faceScore = score; face = id; }
            }
            PerfectSync = _links.Count >= 20;
            Mode = PerfectSync ? "sync" : "basic";
            if (face == null) return;
            // 퍼펙트 싱크가 아니면 얼굴 메시(ARKit·Unified 이름·vrc.v_*·MMD 모프가 가장 많은 메시)의 눈 감기·입 모양·MMD 표정 모프로 대신한다.
            // 퍼펙트 싱크여도 눈 감기가 하나도 잡히지 않았으면 눈 감기만은 대신한다
            bool blinkLinked = _links.Exists(l => l.Source.StartsWith("eyeBlink"));
            var byNorm = new Dictionary<string, string>();
            foreach (var shape in scene.ShapeNames(face)) { var n = Norm(shape); if (!byNorm.ContainsKey(n)) byNorm[n] = shape; }
            var used = new HashSet<string>();
            foreach (var l in _links) used.Add(l.Shape);
            bool hasLr = false;
            foreach (var (src, targets) in Fallback)
            {
                bool blink = src.StartsWith("eyeBlink");
                if (blink ? blinkLinked : PerfectSync) continue;
                if (src == "eyeBlink" && hasLr) continue;
                string found = null;
                foreach (var t in targets)
                    if (byNorm.TryGetValue(Norm(t), out var shape) && !used.Contains(shape)) { found = shape; break; }
                // 정확한 영어 이름이 없으면 이름 규칙이 먼저 ("blink"가 들어 있고 L·Left / R·Right로 끝나는 것, eye_blink_1_L 등),
                // 그다음 위 목록의 일본어 이름(ウィンク 등 — 웃는 눈 모양이 섞인 경우가 많다)
                if (found != null && Norm(found).Contains("blink") == false && (src == "eyeBlinkLeft" || src == "eyeBlinkRight")) found = Pattern(src, byNorm, used) ?? found;
                if (found == null && (src == "eyeBlinkLeft" || src == "eyeBlinkRight")) found = Pattern(src, byNorm, used);
                if (found == null) continue;
                used.Add(found);
                _links.Add((src, face, found));
                if (src == "eyeBlinkLeft" || src == "eyeBlinkRight") hasLr = true;
                if (!PerfectSync && mmdNorm.Contains(Norm(found))) Mode = "mmd";
            }
        }

        /// <summary>자동 연결과 수동 연결(map)로 만든다. </summary>
        public FaceRetarget(SceneBuilder scene, JNode map) : this(scene) { SetMap(map); }

        /// <summary>이 메시에서 이어진 (얼굴 값, 블렌드쉐이프) 목록 — 상태 표시·시험용.</summary>
        public IEnumerable<(string Source, string Shape)> Links { get { foreach (var l in Final) yield return (l.Source, l.Shape); } }

        static string Pattern(string src, Dictionary<string, string> byNorm, HashSet<string> used)
        {
            bool left = src == "eyeBlinkLeft";
            foreach (var kv in byNorm)
                if (kv.Key.Contains("blink") && !kv.Key.StartsWith("vrc") && !used.Contains(kv.Value)
                    && (left ? kv.Key.EndsWith("l") || kv.Key.EndsWith("left") : kv.Key.EndsWith("r") || kv.Key.EndsWith("right")))
                    return kv.Value;
            return null;
        }

        // ---------------- 감은 눈에서 눈 편집 빼기 ----------------
        // 눈매·눈 크기 같은 눈 편집 블렌드쉐이프를 켜 두면 감기 블렌드쉐이프(원래 얼굴 기준)를 100으로 해도 편집만큼 덜 감긴다
        // (2026-10-24 사용자: 웹캠·Baballonia에서 눈이 완전히 감기지 않음 — 아바타 A Body 눈 편집 37개, EyeClosedLeft·eye_blink_1_L·vrc.blink 모두 흰자가 띠로 남음).
        // 메시를 고치는 Blink Fix(target' = target − 편집)와 같은 결과를 값으로 낸다: 감은 정도 c만큼 편집 값을 (1 − c)배로 → target·c + 편집·(1 − c).
        // 이미 Blink Fix 대상인 감기는 편집을 뺀 모양이라 건너뛴다. 좌우 표시가 있는 편집은 그쪽 눈, 없는 것은 두 눈 중 덜 감은 쪽만큼 (윙크 때 뜬 눈은 그대로).
        readonly Dictionary<(string, string), int> _closeSide = new Dictionary<(string, string), int>();   // 감기 블렌드쉐이프의 쪽 (−1·0·+1), 감기가 아니면 2
        readonly Dictionary<string, List<(string Shape, int Side)>> _eyeEdits = new Dictionary<string, List<(string, int)>>();
        readonly Dictionary<string, (float L, float R)> _closed = new Dictionary<string, (float, float)>();
        readonly HashSet<(string, string)> _inLayer = new HashSet<(string, string)>();

        int CloseSide(string r, string s)
        {
            if (_closeSide.TryGetValue((r, s), out var side)) return side;
            side = MeshOps.IsBlinkName(s) && !_scene.MovesSideways(r, s) ? MeshOps.SideOfName(s) : 2;
            return _closeSide[(r, s)] = side;
        }

        List<(string Shape, int Side)> EyeEdits(string r)
        {
            if (_eyeEdits.TryGetValue(r, out var list)) return list;
            list = new List<(string, int)>();
            foreach (var s in _scene.ShapeNames(r))
                if (MeshOps.IsEyeName(s) && MeshOps.SeparatorName(s) == null && CloseSide(r, s) == 2) list.Add((s, MeshOps.SideOfName(s)));
            return _eyeEdits[r] = list;
        }

        /// <summary>트래킹 레이어에 감기 블렌드쉐이프가 있으면, 같은 렌더러의 켜 둔 눈 편집을 감은 만큼 줄여 레이어에 더한다.</summary>
        public void CloseEyes(List<(string, string, float)> layer)
        {
            _closed.Clear();
            foreach (var (r, s, w) in layer)
            {
                if (w < 0.5f) continue;
                int side = CloseSide(r, s);
                if (side == 2 || _scene.BlinkFixed(r, s)) continue;
                var (l, rr) = _closed.TryGetValue(r, out var x) ? x : (0f, 0f);
                float k = Mathf.Clamp01(w / 100f);
                if (side <= 0) l = Mathf.Max(l, k);
                if (side >= 0) rr = Mathf.Max(rr, k);
                _closed[r] = (l, rr);
            }
            if (_closed.Count == 0) return;
            _inLayer.Clear();
            foreach (var (r, s, _) in layer) _inLayer.Add((r, s));
            foreach (var kv in _closed)
                foreach (var (shape, side) in EyeEdits(kv.Key))
                {
                    if (_inLayer.Contains((kv.Key, shape))) continue;
                    float c = side < 0 ? kv.Value.L : side > 0 ? kv.Value.R : Mathf.Min(kv.Value.L, kv.Value.R);
                    if (c <= 0) continue;
                    float u = _scene.BelowTracking(kv.Key, shape);
                    if (Mathf.Abs(u) < 0.01f) continue;
                    layer.Add((kv.Key, shape, u * (1 - c)));
                }
        }

        /// <summary>
        /// 얼굴 값 → (렌더러, 블렌드쉐이프, 0~100). 같은 블렌드쉐이프에 여러 값이 오면 큰 것.
        /// 말하기(lip.aa … lip.oh, LipSync)는 입 모양 값과 큰 쪽으로 합친다. FT 애니메이터가 있으면 그 결과 위에 수동 연결을 덮는다.
        /// </summary>
        public List<(string, string, float)> Apply(Dictionary<string, float> v)
        {
            float Get(string n)
            {
                // 오므리기는 깔때기만큼 줄인다: 웹캠(MediaPipe)은 "오"·"우" 입에서 둘을 함께 크게 내는데, 두 쉐이프를 같이 더하면 윗입술이 접혀 하트 모양으로 깨진다
                // (2026-10-24 사용자 화면, 아바타 A sync 연결). FT 기믹의 트리도 깔때기가 커질수록 오므리기를 빼며 섞는다 — FT 모드는 그 트리가 하므로 그대로
                if (n == "mouthPucker" && Ft == null && v.TryGetValue(n, out var pk)) return pk * (1 - Mathf.Clamp01(v.TryGetValue("mouthFunnel", out var fn) ? fn : 0));
                if (v.TryGetValue(n, out var x)) return x;
                switch (n)
                {
                    case "eyeBlink": return Mathf.Max(Get("eyeBlinkLeft"), Get("eyeBlinkRight"));
                    case var both when both.StartsWith("both:"): return (Get(both.Substring(5) + "Left") + Get(both.Substring(5) + "Right")) * 0.5f;
                    case "viseme.aa": return Mathf.Max(Get("lip.aa"), v.TryGetValue("jawOpen", out var j) ? Mathf.Clamp01(j * 1.4f - Mathf.Max(Get("mouthFunnel"), Get("mouthPucker")) * 0.5f) : 0);
                    case "viseme.oh": return Mathf.Max(Get("lip.oh"), Get("mouthFunnel"));
                    case "viseme.ou": return Mathf.Max(Get("lip.ou"), Get("mouthPucker"));
                    case "viseme.ih": return Mathf.Max(Get("lip.ih"), Mathf.Clamp01((Get("mouthSmileLeft") + Get("mouthSmileRight")) * 0.5f - Get("jawOpen") * 0.3f));
                    case "viseme.e": return Mathf.Max(Get("lip.e"), Mathf.Clamp01((Get("mouthStretchLeft") + Get("mouthStretchRight")) * 0.5f));
                    // MMD 표정 모프: 웃는 입이면 눈도 웃고(볼 올리기가 없을 때), 눈을 감으면 웃는 눈은 0 (まばたき와 겹쳐 눈이 파고들지 않게)
                    case "mmd.smileEye":
                    {
                        float smile = Get("both:mouthSmile"), eye = Mathf.Max(Get("both:cheekSquint"), Mathf.Clamp01((smile - 0.35f) / 0.65f) * 0.6f);
                        return eye * (1 - Get("eyeBlink"));
                    }
                    case "mmd.surprise": return Get("both:eyeWide");
                    case "mmd.browSad": return Get("browInnerUp");
                    case "mmd.browDown": return Get("both:browDown");
                    case "mmd.browUp": return Get("both:browOuterUp");
                    case "mmd.smile": return Get("both:mouthSmile");
                    case "mmd.frown": return Get("both:mouthFrown");
                    case "mmd.wide": return Get("both:mouthStretch");
                    case "mmd.tongue": return Get("tongueOut");
                    case "mmd.puff": return Get("cheekPuff");
                }
                return 0;
            }
            var best = new Dictionary<(string, string), float>();
            foreach (var (src, r, s, k) in Final)
            {
                float w = Mathf.Min(100, Get(src) * 100 * k);
                best[(r, s)] = best.TryGetValue((r, s), out var o) ? Mathf.Max(o, w) : w;
            }
            var list = new List<(string, string, float)>(best.Count);
            if (Ft != null)
                foreach (var (r, s, w) in Ft.Apply(v, _scene.BelowTracking))
                    if (!best.ContainsKey((r, s))) list.Add((r, s, w));
            foreach (var kv in best) list.Add((kv.Key.Item1, kv.Key.Item2, kv.Value));
            return list;
        }
    }

    /// <summary>VMC 프로토콜 수신 (OSC over UDP). 블렌드쉐이프 값만 쓴다 (본·카메라 메시지는 무시).</summary>
    public sealed class VmcSource : IFaceSource, IHeadSource
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        readonly object _lock = new object();
        readonly Dictionary<string, float> _pending = new Dictionary<string, float>(), _ready = new Dictionary<string, float>();
        volatile bool _running = true;
        bool _fresh;
        Vector3 _head; bool _headPending; float _headAt = -10;   // /VMC/Ext/Bone/Pos Head 의 로컬 회전 → 사람 기준 각도
        int _frames; float _fpsAt; float _fps; float _lastAt = -10;
        readonly int _port;

        static readonly Dictionary<string, string> Vrm = new Dictionary<string, string>
        {
            ["blink"] = "eyeBlink", ["blinkl"] = "eyeBlinkLeft", ["blinkr"] = "eyeBlinkRight",
            ["a"] = "viseme.aa", ["i"] = "viseme.ih", ["u"] = "viseme.ou", ["e"] = "viseme.e", ["o"] = "viseme.oh",
            ["aa"] = "viseme.aa", ["ih"] = "viseme.ih", ["ou"] = "viseme.ou", ["ee"] = "viseme.e", ["oh"] = "viseme.oh",
        };
        static Dictionary<string, string> _arkit;

        public VmcSource(int port)
        {
            _port = port;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _udp.Client.ReceiveTimeout = 500;
            if (_arkit == null) { _arkit = new Dictionary<string, string>(); foreach (var a in FaceRetarget.ArKit) _arkit[FaceRetarget.Norm(a)] = a; }
            _thread = new Thread(Run) { IsBackground = true, Name = "VMC" };
            _thread.Start();
        }

        public string Name => "vmc";
        public bool Ok => true;
        public bool Face => Time.unscaledTime - _lastAt < 1f;
        public float Fps => Face ? _fps : 0;
        public string Text => Face ? $"외부 앱에서 받는 중 (UDP {_port})" : $"UDP {_port}에서 기다리는 중 · 외부 앱의 VMC 보내기를 이 PC의 {_port} 포트로 켜 주세요";

        void Run()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _udp.Receive(ref any); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }
                try { Packet(data, 0, data.Length); } catch { /* 깨진 패킷은 버린다 */ }
            }
        }

        void Packet(byte[] b, int off, int len)
        {
            if (len >= 8 && Encoding.ASCII.GetString(b, off, 7) == "#bundle")
            {
                int p = off + 16;   // "#bundle\0" + 시간 태그 8바이트
                while (p + 4 <= off + len)
                {
                    int size = BigInt(b, p); p += 4;
                    if (size <= 0 || p + size > off + len) break;
                    Packet(b, p, size);
                    p += size;
                }
                return;
            }
            int q = off;
            var address = OscString(b, ref q, off + len);
            if (address == "/VMC/Ext/Blend/Val")
            {
                var tags = OscString(b, ref q, off + len);
                if (tags.Length < 3 || tags[1] != 's' || tags[2] != 'f') return;
                var name = OscString(b, ref q, off + len);
                float v = BigFloat(b, q);
                var n = FaceRetarget.Norm(name);
                string key = _arkit.TryGetValue(n, out var a) ? a : Vrm.TryGetValue(n, out var vr) ? vr : null;
                if (key != null) lock (_lock) _pending[key] = v;
            }
            else if (address == "/VMC/Ext/Bone/Pos")
            {
                var tags = OscString(b, ref q, off + len);
                if (tags.Length < 9 || tags[1] != 's') return;
                var name = OscString(b, ref q, off + len);
                if (name != "Head") return;
                q += 12;   // 위치 x, y, z
                var rot = new Quaternion(BigFloat(b, q), BigFloat(b, q + 4), BigFloat(b, q + 8), BigFloat(b, q + 12));
                var e = rot.eulerAngles;
                float Wrap(float a) => Mathf.DeltaAngle(0, a);
                // Unity 각도(Y+ = 오른쪽으로 돌기, X+ = 아래 보기) → 사람 기준
                lock (_lock) { _head = new Vector3(-Wrap(e.y), -Wrap(e.x), Wrap(e.z)); _headPending = true; }
            }
            else if (address == "/VMC/Ext/Blend/Apply")
            {
                lock (_lock)
                {
                    foreach (var kv in _pending) _ready[kv.Key] = kv.Value;
                    _pending.Clear();
                    _fresh = true;
                }
            }
        }

        static int BigInt(byte[] b, int p) => (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
        static float BigFloat(byte[] b, int p) { var t = new[] { b[p + 3], b[p + 2], b[p + 1], b[p] }; return BitConverter.ToSingle(t, 0); }
        static string OscString(byte[] b, ref int p, int end)
        {
            int s = p;
            while (p < end && b[p] != 0) p++;
            var str = Encoding.UTF8.GetString(b, s, p - s);
            p = (p + 4) & ~3;   // 널 포함 4바이트 정렬
            return str;
        }

        public bool Head(out Vector3 deg)
        {
            lock (_lock) { if (_headPending) { _headPending = false; _headAt = Time.unscaledTime; } deg = _head; }
            return Time.unscaledTime - _headAt < 1f;
        }

        /// <summary>VMC의 Head 위치는 부모(목) 기준이라 머리를 옮긴 것을 알 수 없다.</summary>
        public bool Move(out Vector2 pos) { pos = default; return false; }

        public bool Poll(Dictionary<string, float> values)
        {
            lock (_lock)
            {
                if (!_fresh) return false;
                _fresh = false;
                values.Clear();
                foreach (var kv in _ready) values[kv.Key] = kv.Value;
            }
            _lastAt = Time.unscaledTime;
            _frames++;
            if (Time.unscaledTime - _fpsAt >= 1) { _fps = _frames / (Time.unscaledTime - _fpsAt); _frames = 0; _fpsAt = Time.unscaledTime; }
            return true;
        }

        public void Dispose()
        {
            _running = false;
            try { _udp.Close(); } catch { }
        }
    }
}
