// 자체 웹캠 페이셜 트래킹 (FaceTracker의 소스 하나, 미리보기 전용).
//   1. WebCamTexture로 웹캠 프레임을 읽는다 (Windows는 권한 창 없음). 시험용으로 이미지 파일 한 장을 계속 넣을 수도 있다 (image).
//   2. 얼굴이 있을 만한 정사각형(이전 프레임의 얼굴 범위·기울기, 처음·놓치면 화면 가운데부터 몇 군데)을 눈이 수평이 되게 돌려 256×256으로 잘라
//      MediaPipe Face Mesh V2(478점, 눈동자 포함)를 ONNX Runtime(CPU)으로 돌린다.
//   3. 점을 One Euro로 평활한 뒤 그중 146점(프레임 픽셀 좌표)을 MediaPipe Blendshape V2에 넣어 ARKit 52개 값(0~1)을 바로 얻는다.
//      머리 각도는 좌우 짝 점 여럿으로, 시선(eyeLook*)은 모델 값 대신 눈동자 점으로 구한다 (2026-10-24).
//      모델: StreamingAssets/FaceModel (Google MediaPipe Face Landmarker, Apache 2.0, TFLite → ONNX 변환, NOTICE.txt)
// 추론은 백그라운드 스레드 하나에서 하고(메인 스레드는 픽셀만 넘김), 결과는 Poll로 메인 스레드가 가져간다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class WebcamFace : IFaceSource, IHeadSource
    {
        const int Size = 256;
        readonly WebCamTexture _cam;
        readonly Color32[] _still; readonly int _stillW, _stillH;
        readonly InferenceSession _mesh, _blend;
        readonly string _meshIn, _blendIn;
        readonly Thread _thread;
        readonly object _lock = new object();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _running = true, _busy;
        Color32[] _frame, _buf; int _fw, _fh; double _frameT;   // 일꾼에게 넘긴 프레임과 넘긴 시각(초) · 웹캠 픽셀 버퍼
        readonly OneEuro _filter = new OneEuro();
        float _gazeH, _gazeV;   // 눈을 감는 동안 붙잡아 두는 시선 (눈동자 점이 눈꺼풀에 가려 흔들린다)
        readonly Dictionary<string, float> _result = new Dictionary<string, float>();
        bool _fresh, _face;
        Vector3 _head; bool _headOk;   // 머리 각도 (도, 사람 기준: yaw 왼쪽 +, pitch 위 +, roll 왼쪽 눈이 올라가면 +)
        Vector2 _pos;                  // 머리 위치 (얼굴 폭 단위, 사람 기준: x 왼쪽 +, y 위 +)
        string _error = "";
        int _frames; float _fpsAt, _fps;
        // 얼굴 범위 (프레임 픽셀·위에서 아래로, 정사각형 가운데·한 변·기울기 라디안). 한 변이 0이면 찾는 중
        float _roiX, _roiY, _roiS, _roiA; int _search;

        /// <summary>Blendshape V2에 넣는 478점 중 146점 (MediaPipe face_blendshapes_graph.cc kLandmarksSubsetIdxs).</summary>
        static readonly int[] Subset =
        {
            0, 1, 4, 5, 6, 7, 8, 10, 13, 14, 17, 21, 33, 37, 39, 40, 46, 52, 53, 54, 55, 58, 61, 63, 65, 66, 67, 70, 78, 80,
            81, 82, 84, 87, 88, 91, 93, 95, 103, 105, 107, 109, 127, 132, 133, 136, 144, 145, 146, 148, 149, 150, 152, 153, 154, 155, 157, 158, 159, 160,
            161, 162, 163, 168, 172, 173, 176, 178, 181, 185, 191, 195, 197, 234, 246, 249, 251, 263, 267, 269, 270, 276, 282, 283, 284, 285, 288, 291, 293, 295,
            296, 297, 300, 308, 310, 311, 312, 314, 317, 318, 321, 323, 324, 332, 334, 336, 338, 356, 361, 362, 365, 373, 374, 375, 377, 378, 379, 380, 381, 382,
            384, 385, 386, 387, 388, 389, 390, 397, 398, 400, 402, 405, 409, 415, 454, 466, 468, 469, 470, 471, 472, 473, 474, 475, 476, 477,
        };
        /// <summary>Blendshape V2 출력 순서 (0번 _neutral은 쓰지 않는다). Left·Right는 사람 기준 (거울 아님) — FaceTracker가 거울처럼 옵션으로 바꾼다.</summary>
        static readonly string[] BlendNames =
        {
            "_neutral", "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight", "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
            "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight",
            "eyeLookUpLeft", "eyeLookUpRight", "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight", "jawForward", "jawLeft", "jawOpen", "jawRight",
            "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel", "mouthLeft", "mouthLowerDownLeft",
            "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight", "mouthPucker", "mouthRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower",
            "mouthShrugUpper", "mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight", "mouthUpperUpLeft", "mouthUpperUpRight",
            "noseSneerLeft", "noseSneerRight",
        };
        public string Device { get; }

        public WebcamFace(string device, string imagePath = null)
        {
            var dir = Path.Combine(Application.streamingAssetsPath, "FaceModel");
            var mesh = Path.Combine(dir, "face_landmarks_v2.onnx"); var blend = Path.Combine(dir, "face_blendshapes_v2.onnx");
            foreach (var f in new[] { mesh, blend }) if (!File.Exists(f)) throw new FileNotFoundException("얼굴 모델이 없어요 (StreamingAssets/FaceModel)", f);
            var opt = new SessionOptions { IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _mesh = new InferenceSession(mesh, opt); _meshIn = _mesh.InputMetadata.Keys.First();
            _blend = new InferenceSession(blend, opt); _blendIn = _blend.InputMetadata.Keys.First();
            if (imagePath != null)
            {
                var tex = new Texture2D(2, 2);
                if (!tex.LoadImage(File.ReadAllBytes(imagePath))) throw new InvalidDataException("이미지를 읽지 못했어요: " + imagePath);
                _still = tex.GetPixels32(); _stillW = tex.width; _stillH = tex.height;
                UnityEngine.Object.Destroy(tex);
                Device = Path.GetFileName(imagePath);
            }
            else
            {
                var devices = WebCamTexture.devices;
                if (devices.Length == 0) throw new InvalidOperationException("웹캠을 찾지 못했어요");
                var d = devices.FirstOrDefault(x => x.name == device);
                if (string.IsNullOrEmpty(d.name)) d = devices[0];
                Device = d.name;
                _cam = new WebCamTexture(d.name, 640, 480, 60);   // 되는 카메라는 60 fps로 (안 되면 카메라가 고른 값, 2026-10-23)
                _cam.Play();
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "WebcamFace" };
            _thread.Start();
        }

        public static string[] Devices() => WebCamTexture.devices.Select(d => d.name).ToArray();

        static double Clock() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        public string Name => "webcam";
        public bool Ok => _error.Length == 0;
        public bool Face { get { lock (_lock) return _face; } }
        public float Fps => _fps;
        public string Text
        {
            get
            {
                if (_error.Length > 0) return _error;
                if (_cam != null && !_cam.isPlaying) return $"{Device} 켜는 중…";
                return Face ? $"{Device} · 얼굴 찾음" : $"{Device} · 얼굴을 찾는 중 (카메라를 정면으로 봐 주세요)";
            }
        }

        public bool Poll(Dictionary<string, float> values)
        {
            // 새 프레임을 일꾼에게 넘긴다 (일꾼이 쉬고 있을 때만)
            if (!_busy)
            {
                if (_cam != null && _cam.isPlaying && _cam.didUpdateThisFrame && _cam.width > 16)
                {
                    // 버퍼 하나를 계속 쓴다 (프레임마다 1 MB 넘게 새로 만들면 GC로 렌더러가 끊긴다). 일꾼이 쉴 때만 채우므로 겹치지 않는다
                    int n = _cam.width * _cam.height;
                    if (_buf == null || _buf.Length != n) _buf = new Color32[n];
                    _cam.GetPixels32(_buf);
                    lock (_lock) { _frame = _buf; _fw = _cam.width; _fh = _cam.height; _frameT = Clock(); }
                    _busy = true; _wake.Set();
                }
                else if (_still != null)
                {
                    lock (_lock) { _frame = _still; _fw = _stillW; _fh = _stillH; _frameT = Clock(); }
                    _busy = true; _wake.Set();
                }
            }
            lock (_lock)
            {
                if (!_fresh) return false;
                _fresh = false;
                values.Clear();
                foreach (var kv in _result) values[kv.Key] = kv.Value;
            }
            _frames++;
            if (Time.unscaledTime - _fpsAt >= 1) { _fps = _frames / Mathf.Max(0.001f, Time.unscaledTime - _fpsAt); _frames = 0; _fpsAt = Time.unscaledTime; }
            return true;
        }

        void Run()
        {
            var input = new float[Size * Size * 3];
            while (_running)
            {
                if (!_wake.WaitOne(500)) continue;
                if (!_running) break;
                Color32[] f; int w, h; double t;
                lock (_lock) { f = _frame; w = _fw; h = _fh; t = _frameT; }
                try { if (f != null) Process(f, w, h, t, input); }
                catch (Exception e) { _error = "트래킹 오류: " + e.Message; }
                _busy = false;
            }
        }

        /// <summary>한 프레임: 얼굴 범위를 돌려 잘라 478점을 찾고, 146점으로 52개 값을 구한다. 놓치면 다음 프레임에 다른 자리를 찾아본다.</summary>
        void Process(Color32[] f, int w, int h, double time, float[] input)
        {
            if (_roiS <= 0)
            {
                // 찾는 중: 가운데 큰 것 → 가운데 작은 것 → 위·옆으로 조금씩 (프레임마다 하나)
                float m = Mathf.Min(w, h);
                var tries = new (float X, float Y, float S)[] { (0.5f, 0.5f, 0.8f), (0.5f, 0.45f, 0.55f), (0.5f, 0.4f, 0.4f), (0.35f, 0.45f, 0.5f), (0.65f, 0.45f, 0.5f), (0.5f, 0.6f, 0.5f) };
                var t = tries[_search++ % tries.Length];
                _roiX = w * t.X; _roiY = h * t.Y; _roiS = m * t.S; _roiA = 0;
            }
            Crop(f, w, h, _roiX, _roiY, _roiS, _roiA, input);
            float presence; float[] lm;
            using (var res = _mesh.Run(new[] { NamedOnnxValue.CreateFromTensor(_meshIn, new DenseTensor<float>(input, new[] { 1, Size, Size, 3 })) }))
            {
                // 출력: 478×3 점(잘라 낸 상자 픽셀), 얼굴 있음 로짓, 혀 (쓰지 않음)
                var outs = res.ToList();
                lm = outs.First(r => r.AsTensor<float>().Length == 478 * 3).AsEnumerable<float>().ToArray();
                presence = outs.First(r => r.Name == "Identity_1").AsEnumerable<float>().First();
            }
            presence = 1 / (1 + Mathf.Exp(-presence));
            if (presence < 0.5f)
            {
                _roiS = 0; _filter.Reset();
                lock (_lock) _face = false;
                return;
            }
            // 잘라 낸 상자(0~256, 돌린 좌표) → 프레임 픽셀
            float cos = Mathf.Cos(_roiA), sin = Mathf.Sin(_roiA), k = _roiS / Size;
            var p = new Vector2[478];
            for (int i = 0; i < 478; i++)
            {
                float u = (lm[i * 3] - Size / 2f) * k, v = (lm[i * 3 + 1] - Size / 2f) * k;
                p[i] = new Vector2(_roiX + u * cos - v * sin, _roiY + u * sin + v * cos);
            }
            // 다음 프레임의 얼굴 범위: 눈 바깥 꼬리(33·263)로 기울기, 점 범위(돌린 좌표)의 가운데와 큰 쪽 × 1.5
            var eye = p[263] - p[33];
            float angle = Mathf.Atan2(eye.y, eye.x);
            float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var q in p)
            {
                float u = q.x * ca + q.y * sa, v = -q.x * sa + q.y * ca;
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u); minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
            }
            float mu = (minU + maxU) / 2, mv = (minV + maxV) / 2;
            float cx = mu * ca - mv * sa, cy = mu * sa + mv * ca;
            float s = Mathf.Max(maxU - minU, maxV - minV) * 1.5f;
            _roiX = Mathf.Lerp(_roiX, cx, 0.7f); _roiY = Mathf.Lerp(_roiY, cy, 0.7f); _roiS = Mathf.Clamp(Mathf.Lerp(_roiS, s, 0.7f), 48, Mathf.Max(w, h));
            _roiA = Mathf.LerpAngle(_roiA * Mathf.Rad2Deg, angle * Mathf.Rad2Deg, 0.7f) * Mathf.Deg2Rad;
            // 점을 One Euro로 평활한다 (얼굴 범위는 평활 전 점으로 — 늦게 따라가면 빠른 움직임에서 얼굴이 잘린다)
            var xyz = new float[478 * 3];
            for (int i = 0; i < 478; i++) { xyz[i * 3] = p[i].x; xyz[i * 3 + 1] = p[i].y; xyz[i * 3 + 2] = lm[i * 3 + 2] * k; }
            _filter.Apply(xyz, time, Mathf.Max(1, (maxU - minU + maxV - minV) / 2));
            var P = new Vector3[478];   // 카메라 좌표 (X 오른쪽, Y 위, Z 보는 사람 쪽)
            for (int i = 0; i < 478; i++) { p[i] = new Vector2(xyz[i * 3], xyz[i * 3 + 1]); P[i] = new Vector3(xyz[i * 3], -xyz[i * 3 + 1], -xyz[i * 3 + 2]); }
            var vals = Blend(p);
            var (right, up) = HeadAxes(P);
            var head = HeadAngles(right, up);
            var pos = HeadPosition(P, w, h);
            Gaze(P, right, up, vals);
            lock (_lock)
            {
                _head = head; _headOk = true; _pos = pos;
                _result.Clear();
                foreach (var kv in vals) _result[kv.Key] = kv.Value;
                _fresh = true; _face = true;
            }
        }

        /// <summary>좌우 짝 점 (사람의 오른쪽, 왼쪽). 표정으로 거의 안 움직이는 곳만 — 눈꼬리·관자놀이·이마 가장자리·볼 끝·콧방울·광대 (눈꺼풀·눈썹·입·턱은 뺀다).</summary>
        static readonly (int R, int L)[] Pairs =
        {
            (33, 263), (133, 362), (234, 454), (127, 356), (93, 323), (21, 251), (54, 284), (103, 332), (67, 297), (109, 338), (162, 389),
            (98, 327), (129, 358), (50, 280), (123, 352), (116, 345), (117, 346), (118, 347), (119, 348),
        };
        /// <summary>위 방향: 이마(10·151·9) 가운데 → 코 밑(2·98·327) 가운데. 턱(152)은 입을 벌리면 내려가 고개가 숙여지던 것을 뺐다.</summary>
        static readonly int[] Upper = { 10, 151, 9 }, Lower = { 2, 98, 327 };
        /// <summary>코 밑 → 이마는 얼굴 면보다 뒤로 누워 있다 — 예전 턱 → 이마와 정면 얼굴에서 같은 각도가 나오게 돌린다 (E2E 사진: 예전 0.2°, 새 7.8° → 7.6°).
        /// 좌우 짝 점은 같은 사진에서 가운데 어긋남·높이 차이 모두 얼굴 폭의 1% 이하로 확인했다.</summary>
        const float UpTilt = 7.6f;

        /// <summary>
        /// 머리 축 (카메라 좌표, X 오른쪽, Y 위, Z 보는 사람 쪽 — MediaPipe z는 x와 같은 크기, 작을수록 카메라에 가깝다).
        /// 오른쪽 = 짝 점마다 오른쪽 → 왼쪽을 더한 것 (넓은 짝일수록 많이 들어간다), 위 = 이마 − 코 밑을 오른쪽에 직교하게.
        /// 점 4개로만 구하던 때보다 점 하나가 흔들려도 각도가 덜 흔들린다.
        /// </summary>
        static (Vector3 Right, Vector3 Up) HeadAxes(Vector3[] P)
        {
            var right = Vector3.zero;
            foreach (var (r, l) in Pairs) right += P[l] - P[r];
            right.Normalize();
            Vector3 Mean(int[] ids) { var s = Vector3.zero; foreach (var i in ids) s += P[i]; return s / ids.Length; }
            var up = Mean(Upper) - Mean(Lower);
            up = (up - right * Vector3.Dot(up, right)).normalized;
            if (UpTilt != 0) up = Quaternion.AngleAxis(UpTilt, right) * up;
            return (right, up);
        }

        static Vector3 HeadAngles(Vector3 right, Vector3 up)
        {
            var fwd = Vector3.Cross(right, up).normalized;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1, 1)) * Mathf.Rad2Deg;
            float roll = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg;
            return new Vector3(yaw, pitch, roll);
        }

        /// <summary>
        /// 머리 위치: 양쪽 볼 끝(234·454, 귀 앞) 가운데를 그 사이 3D 거리(얼굴 폭)로 나눈 값. 눈·코보다 목의 회전축에 가까워 고개만 돌려도 덜 움직이고,
        /// 얼굴 폭으로 나누어 카메라와의 거리·해상도와 상관없다. 카메라에서 오른쪽 = 사람의 왼쪽 (HeadAngles와 같은 축).
        /// </summary>
        static Vector2 HeadPosition(Vector3[] P, int w, int h)
        {
            var a = P[234]; var b = P[454];
            float width = Mathf.Max(1e-3f, (b - a).magnitude);
            var c = (a + b) / 2;
            return new Vector2((c.x - w / 2f) / width, (c.y + h / 2f) / width);   // 프레임 가운데 기준 (y는 위가 +라 −h/2가 가운데)
        }

        // 시선: 눈동자가 눈 가운데에서 옆으로 이만큼(눈 폭의 반 단위) 가면 1 — 사람 눈은 옆보다 위아래로 덜 움직인다. 실제 웹캠으로 맞출 값
        // 가운데 이만큼은 정면으로 본다 (눈동자 점의 잔떨림·사람마다 다른 정면 자리 — 무표정 맞추기를 안 해도 눈이 한쪽으로 쏠리지 않게)
        const float GazeSide = 0.4f, GazeUpDown = 0.25f, GazeDead = 0.05f;
        static float Look(float x, float full) => Mathf.Clamp01((Mathf.Abs(x) - GazeDead) / (full - GazeDead));

        /// <summary>
        /// 시선을 눈동자 점으로 (Blendshape V2의 eyeLook* 값은 작고 둔하다). 눈마다 두 눈꼬리 가운데에서 눈동자 가운데(468·473)가 머리 오른쪽·위 축으로
        /// 얼마나 갔는지를 눈 폭으로 나누고, 두 눈을 평균해 같은 곳을 보게 한다. 눈을 감는 동안(감기 0.5 넘음)은 감기 전 시선을 붙잡아 둔다.
        /// </summary>
        void Gaze(Vector3[] P, Vector3 right, Vector3 up, Dictionary<string, float> vals)
        {
            float blink = Mathf.Max(vals.TryGetValue("eyeBlinkLeft", out var bl) ? bl : 0, vals.TryGetValue("eyeBlinkRight", out var br) ? br : 0);
            if (blink < 0.5f)
            {
                // 눈동자 가운데 두 개 중 눈에 가까운 쪽 (모델의 번호 순서에 기대지 않는다)
                Vector3 rc = (P[33] + P[133]) / 2, lc = (P[263] + P[362]) / 2;
                bool swap = (P[468] - rc).sqrMagnitude > (P[473] - rc).sqrMagnitude;
                Vector3 ri = swap ? P[473] : P[468], li = swap ? P[468] : P[473];
                float rw = Mathf.Max(1e-3f, (P[133] - P[33]).magnitude / 2), lw = Mathf.Max(1e-3f, (P[263] - P[362]).magnitude / 2);
                _gazeH = (Vector3.Dot(ri - rc, right) / rw + Vector3.Dot(li - lc, right) / lw) / 2;
                _gazeV = (Vector3.Dot(ri - rc, up) / rw + Vector3.Dot(li - lc, up) / lw) / 2;
            }
            // 사람 기준: _gazeH > 0 = 사람의 왼쪽 → 오른눈은 안쪽(코), 왼눈은 바깥쪽
            float side = Look(_gazeH, GazeSide), ud = Look(_gazeV, GazeUpDown);
            float toLeft = _gazeH > 0 ? side : 0, toRight = _gazeH < 0 ? side : 0;
            vals["eyeLookInRight"] = toLeft; vals["eyeLookOutLeft"] = toLeft;
            vals["eyeLookOutRight"] = toRight; vals["eyeLookInLeft"] = toRight;
            vals["eyeLookUpLeft"] = vals["eyeLookUpRight"] = _gazeV > 0 ? ud : 0;
            vals["eyeLookDownLeft"] = vals["eyeLookDownRight"] = _gazeV < 0 ? ud : 0;
        }

        public bool Head(out Vector3 deg) { lock (_lock) { deg = _head; return _headOk && _face; } }
        public bool Move(out Vector2 pos) { lock (_lock) { pos = _pos; return _headOk && _face; } }

        /// <summary>
        /// 프레임에서 가운데 (cx,cy)·한 변 s·기울기 a(라디안)인 정사각형을 256×256 RGB 0~1, NHWC로. Unity 픽셀은 아래에서 위로라 뒤집는다.
        /// 둘레 네 픽셀을 섞는다 (가장 가까운 픽셀만 쓰면 얼굴 범위가 조금만 움직여도 잘린 그림이 흔들려 점이 떨린다).
        /// </summary>
        static void Crop(Color32[] f, int w, int h, float cx, float cy, float s, float a, float[] dst)
        {
            float cos = Mathf.Cos(a), sin = Mathf.Sin(a), k = s / Size;
            const float n = 1 / 255f;
            for (int y = 0; y < Size; y++)
            {
                float v = (y + 0.5f - Size / 2f) * k;
                for (int x = 0; x < Size; x++)
                {
                    float u = (x + 0.5f - Size / 2f) * k;
                    float sx = cx + u * cos - v * sin, sy = h - 1 - (cy + u * sin + v * cos);   // 위에서 아래로 → Unity 줄
                    sx = Mathf.Clamp(sx, 0, w - 1); sy = Mathf.Clamp(sy, 0, h - 1);
                    int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                    float tx = sx - x0, ty = sy - y0;
                    Color32 c00 = f[y0 * w + x0], c10 = f[y0 * w + x1], c01 = f[y1 * w + x0], c11 = f[y1 * w + x1];
                    float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
                    int o = (y * Size + x) * 3;
                    dst[o] = (c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11) * n;
                    dst[o + 1] = (c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11) * n;
                    dst[o + 2] = (c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11) * n;
                }
            }
        }

        /// <summary>478점(프레임 픽셀) → ARKit 52개 값. 모델이 크기·위치를 스스로 맞추므로 픽셀 좌표를 그대로 넣는다.
        /// 눈 감기는 완전히 감아도 0.7쯤에서 멈춰 0.75로 나눠 편다.</summary>
        Dictionary<string, float> Blend(Vector2[] p)
        {
            var pts = new float[Subset.Length * 2];
            for (int i = 0; i < Subset.Length; i++) { pts[i * 2] = p[Subset[i]].x; pts[i * 2 + 1] = p[Subset[i]].y; }
            float[] o;
            using (var res = _blend.Run(new[] { NamedOnnxValue.CreateFromTensor(_blendIn, new DenseTensor<float>(pts, new[] { 1, Subset.Length, 2 })) }))
                o = res.First().AsEnumerable<float>().ToArray();
            var r = new Dictionary<string, float>(BlendNames.Length);
            for (int i = 1; i < BlendNames.Length && i < o.Length; i++) r[BlendNames[i]] = Mathf.Clamp01(o[i]);
            r["eyeBlinkLeft"] = Mathf.Clamp01(r["eyeBlinkLeft"] / 0.75f); r["eyeBlinkRight"] = Mathf.Clamp01(r["eyeBlinkRight"] / 0.75f);
            return r;
        }

        public void Dispose()
        {
            _running = false; _wake.Set();
            if (_cam != null) { _cam.Stop(); UnityEngine.Object.Destroy(_cam); }
            try { _thread.Join(1000); } catch { }
            _mesh.Dispose(); _blend.Dispose();
        }

        /// <summary>
        /// One Euro 필터 (Casiez 2012) — 값마다 따로, 속도에 따라 차단 주파수가 바뀐다: 가만히 있으면 강하게(떨림 없음), 빠르게 움직이면 거의 그대로(늦지 않음).
        /// 값은 MediaPipe 얼굴 점 평활과 같다 (최소 차단 0.05 Hz, beta 80, 미분 차단 1 Hz). 속도는 얼굴 크기로 나눠 보므로 카메라와의 거리와 상관없다.
        /// </summary>
        sealed class OneEuro
        {
            const float MinCutoff = 0.05f, Beta = 80f, DCutoff = 1f;
            float[] _x, _raw, _dx; double _t = -1;

            public void Reset() => _t = -1;

            /// <summary>v를 평활한 값으로 바꾼다. t = 프레임 시각(초), scale = 얼굴 크기 (v와 같은 단위).</summary>
            public void Apply(float[] v, double t, float scale)
            {
                if (_t < 0 || _x == null || _x.Length != v.Length)
                {
                    _x = (float[])v.Clone(); _raw = (float[])v.Clone(); _dx = new float[v.Length]; _t = t;
                    return;
                }
                float dt = (float)Math.Max(1e-3, t - _t); _t = t;
                float ad = Alpha(DCutoff, dt);
                for (int i = 0; i < v.Length; i++)
                {
                    float d = (v[i] - _raw[i]) / dt / scale;   // 얼굴 크기/초
                    _raw[i] = v[i];
                    _dx[i] += ad * (d - _dx[i]);
                    _x[i] += Alpha(MinCutoff + Beta * Math.Abs(_dx[i]), dt) * (v[i] - _x[i]);
                    v[i] = _x[i];
                }
            }

            static float Alpha(float cutoff, float dt) => 1 / (1 + 1 / (2 * Mathf.PI * cutoff * dt));
        }
    }
}
