// 아바타에 설치된 페이셜 트래킹 애니메이터(IR faceTracking, importsim IrFaceTracking.cs — FT 기믹·VRCFT Templates 등)를 VRCFaceTracking처럼 돌린다.
//   1. 트래킹 값(ARKit 52개 + 혀·볼 빨기 등 Unified 값) → VRCFT v2 파라미터 (JawOpen, SmileSadLeft = 웃음 − 입꼬리 내림, EyeLidLeft = 뜬 정도 × 0.75 + 크게 × 0.25, EyeLeftX …)
//   2. 아바타의 FX 블렌드 트리를 그 파라미터로 계산해 블렌드쉐이프 값을 낸다 (Unity 애니메이터와 같은 섞기, Write Defaults 켬 기준):
//      1D = 문턱 사이 직선 보간(끝은 고정) · 2D = 그라디언트 밴드 보간(자유 직교, 단순·자유 방향도 같은 식으로) · Direct = 자식마다 파라미터 값을 가중치로 더함
//      속성마다 Σ(가중치 × 클립 값)과 Σ가중치를 모으고, 가중치가 1이 못 되는 몫은 그 아래 값(사용자 값·표정)으로 채운다 — 정규화 트리에서 그 속성이 없는 클립은 기본값을 쓰는 것과 같다.
//   파라미터 이름은 마지막 "v2/" 뒤만 본다 (v2/JawOpen, FT/v2/…, OSCm/Proxy/v2/… 모두 같은 값 — OSCmooth 평활은 FaceTracker가 이미 했다).
//   …TrackingActive 는 1 (트래킹 켬), 그 밖의 파라미터는 컨트롤러 기본값 (Blend가 정의되어 있지 않으면 1), 손 제스처 등은 0.
// 레이어는 순서대로 덮는다 (뒤 레이어가 같은 블렌드쉐이프를 다시 정하면 그 값).
using System;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class FaceFt
    {
        sealed class Motion
        {
            public int Clip = -1;           // 클립이면 번호 (-1 = 빈 모션 또는 트리)
            public int Type = -1;           // 트리 종류 (0 1D · 1~3 2D · 4 Direct)
            public int P = -1, Py = -1;     // 파라미터 번호
            public bool Normalized;
            public Motion[] Ch;
            public float[] T, X, Y;
            public int[] D;
        }

        readonly List<string> _paramNames = new List<string>();
        readonly Dictionary<string, int> _paramIndex = new Dictionary<string, int>();
        float[] _params;
        string[] _v2;                       // 파라미터마다 v2 이름 (아니면 null)
        readonly List<(int Prop, float Value)[]> _clips = new List<(int, float)[]>();
        readonly List<(string Renderer, string Shape)> _props = new List<(string, string)>();
        readonly List<(string Name, Motion Root)> _layers = new List<(string, Motion)>();
        float[] _acc, _w;
        readonly List<int> _touched = new List<int>();
        public int Layers => _layers.Count;
        public int Shapes => _props.Count;
        /// <summary>시선(EyeLeftX·EyeY …)을 블렌드쉐이프로 움직이는지 (아니면 FaceHead가 눈 본으로).</summary>
        public bool HasGaze { get; private set; }
        /// <summary>쓰는 v2 파라미터 이름 (상태 표시·시험용).</summary>
        public readonly HashSet<string> V2Used = new HashSet<string>();

        /// <summary>IR faceTracking을 읽는다. 이 아바타에서 움직일 블렌드쉐이프가 하나도 없으면 null.</summary>
        public static FaceFt Load(SceneBuilder scene)
        {
            var ft = scene.Scene?.Raw["faceTracking"];
            if (ft == null || ft.Kind != JKind.Object || ft["layers"].Count == 0) return null;
            var f = new FaceFt();
            try { f.Build(scene, ft); } catch (Exception e) { Debug.LogWarning("페이셜 트래킹 애니메이터를 읽지 못함: " + e.Message); return null; }
            return f._props.Count > 0 && f._layers.Count > 0 ? f : null;
        }

        void Build(SceneBuilder scene, JNode ft)
        {
            // 렌더러 경로(아바타 루트 기준, 루트 이름 없이 — .anim 경로) → id
            var byPath = new Dictionary<string, string>();
            var ir = scene.Scene;
            foreach (var r in ir.Renderers)
            {
                var full = ir.NodePath(r.Node);
                int slash = full.IndexOf('/');
                byPath[slash < 0 ? "" : full.Substring(slash + 1)] = r.Id;
            }
            var have = new Dictionary<string, HashSet<string>>();
            foreach (var id in scene.SkinnedIds()) have[id] = new HashSet<string>(scene.ShapeNames(id));
            var propIndex = new Dictionary<(string, string), int>();
            foreach (var c in ft["clips"].Items)
            {
                var list = new List<(int, float)>();
                foreach (var s in c["shapes"].Items)
                {
                    if (!byPath.TryGetValue(s["path"].AsString, out var rid) || !have.TryGetValue(rid, out var names) || !names.Contains(s["shape"].AsString)) continue;
                    var key = (rid, s["shape"].AsString);
                    if (!propIndex.TryGetValue(key, out var pi)) { pi = _props.Count; _props.Add(key); propIndex[key] = pi; }
                    list.Add((pi, s["value"].AsFloat));
                }
                _clips.Add(list.ToArray());
            }
            var defaults = ft["params"];
            foreach (var l in ft["layers"].Items)
            {
                var root = Read(l["root"], 0);
                if (Uses(root)) _layers.Add((l["name"].AsString, root));
            }
            _params = new float[_paramNames.Count];
            _v2 = new string[_paramNames.Count];
            for (int i = 0; i < _paramNames.Count; i++)
            {
                var n = _paramNames[i];
                int k = n.LastIndexOf("v2/", StringComparison.Ordinal);
                if (k >= 0 && (k == 0 || n[k - 1] == '/')) { _v2[i] = n.Substring(k + 3); V2Used.Add(_v2[i]); continue; }
                _params[i] = n.EndsWith("TrackingActive", StringComparison.Ordinal) ? 1
                    : defaults.Has(n) ? defaults[n].AsFloat : n == "Blend" ? 1 : 0;
            }
            _acc = new float[_props.Count]; _w = new float[_props.Count];
            HasGaze = false;
            foreach (var (_, root) in _layers) if (GazeShapes(root, false)) HasGaze = true;
        }

        int Param(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (!_paramIndex.TryGetValue(name, out var i)) { i = _paramNames.Count; _paramNames.Add(name); _paramIndex[name] = i; }
            return i;
        }

        Motion Read(JNode j, int depth)
        {
            var m = new Motion();
            if (j.Has("c")) { m.Clip = j["c"].AsInt; return m; }
            if (depth > 16 || !j.Has("t")) return m;
            m.Type = j["t"].AsInt; m.Normalized = j["n"].AsBool;
            m.P = Param(j["p"].AsString); m.Py = Param(j["py"].AsString);
            int n = j["ch"].Count;
            m.Ch = new Motion[n]; m.T = new float[n]; m.X = new float[n]; m.Y = new float[n]; m.D = new int[n];
            for (int i = 0; i < n; i++)
            {
                var c = j["ch"][i];
                m.Ch[i] = Read(c["m"], depth + 1);
                m.T[i] = c["t"].AsFloat; m.X[i] = c["x"].AsFloat; m.Y[i] = c["y"].AsFloat; m.D[i] = Param(c["d"].AsString);
            }
            if (m.Type == 0) Array.Sort(m.T, m.Ch);   // 문턱 순서 (Unity는 문턱 순서로 섞는다)
            return m;
        }

        /// <summary>이 아바타의 블렌드쉐이프를 하나라도 움직이는지.</summary>
        bool Uses(Motion m)
        {
            if (m.Ch == null) return m.Clip >= 0 && m.Clip < _clips.Count && _clips[m.Clip].Length > 0;
            foreach (var c in m.Ch) if (Uses(c)) return true;
            return false;
        }

        /// <summary>눈 방향 파라미터 아래에 블렌드쉐이프 클립이 있는지.</summary>
        bool GazeShapes(Motion m, bool under)
        {
            if (m.Ch == null) return under && Uses(m);
            bool g = under || IsGaze(m.P) || IsGaze(m.Py);
            foreach (var c in m.Ch) if (GazeShapes(c, g)) return true;
            return false;
        }

        bool IsGaze(int p)
        {
            if (p < 0) return false;
            var n = _paramNames[p];
            int k = n.LastIndexOf("v2/", StringComparison.Ordinal);
            if (k < 0) return false;
            var v = n.Substring(k + 3);
            return v.StartsWith("Eye", StringComparison.Ordinal) && (v.EndsWith("X", StringComparison.Ordinal) || v.EndsWith("Y", StringComparison.Ordinal));
        }

        /// <summary>
        /// 트래킹 값 → 블렌드쉐이프 (렌더러, 이름, 0~100). under(렌더러, 이름) = 이 레이어가 없을 때의 값 (가중치가 1이 못 되는 몫).
        /// </summary>
        public List<(string, string, float)> Apply(Dictionary<string, float> v, Func<string, string, float> under)
        {
            for (int i = 0; i < _params.Length; i++) if (_v2[i] != null) _params[i] = V2(_v2[i], v);
            var result = new Dictionary<int, float>();
            foreach (var (_, root) in _layers)
            {
                _touched.Clear();
                Eval(root, 1f);
                foreach (var pi in _touched)
                {
                    float w = _w[pi];
                    if (w <= 0) continue;
                    var (r, s) = _props[pi];
                    result[pi] = _acc[pi] + (w < 1 ? (1 - w) * under(r, s) : 0);
                }
                foreach (var pi in _touched) { _acc[pi] = 0; _w[pi] = 0; }
            }
            var list = new List<(string, string, float)>(result.Count);
            foreach (var kv in result) { var (r, s) = _props[kv.Key]; list.Add((r, s, Mathf.Clamp(kv.Value, -100, 200))); }
            return list;
        }

        void Eval(Motion m, float weight)
        {
            if (weight < 1e-5f) return;
            if (m.Ch == null)
            {
                if (m.Clip < 0 || m.Clip >= _clips.Count) return;
                foreach (var (pi, val) in _clips[m.Clip])
                {
                    if (_w[pi] == 0) _touched.Add(pi);
                    _acc[pi] += weight * val; _w[pi] += weight;
                }
                return;
            }
            int n = m.Ch.Length;
            if (n == 0) return;
            switch (m.Type)
            {
                case 0:
                {
                    float p = m.P >= 0 ? _params[m.P] : 0;
                    if (p <= m.T[0]) { Eval(m.Ch[0], weight); break; }
                    if (p >= m.T[n - 1]) { Eval(m.Ch[n - 1], weight); break; }
                    for (int i = 0; i < n - 1; i++)
                        if (p <= m.T[i + 1])
                        {
                            float span = m.T[i + 1] - m.T[i], t = span > 1e-6f ? (p - m.T[i]) / span : 1;
                            Eval(m.Ch[i], weight * (1 - t)); Eval(m.Ch[i + 1], weight * t);
                            break;
                        }
                    break;
                }
                case 4:
                {
                    float sum = 0;
                    if (m.Normalized) for (int i = 0; i < n; i++) sum += Mathf.Max(0, m.D[i] >= 0 ? _params[m.D[i]] : 0);
                    for (int i = 0; i < n; i++)
                    {
                        float w = m.D[i] >= 0 ? _params[m.D[i]] : 0;
                        if (m.Normalized) w = sum > 1e-6f ? Mathf.Max(0, w) / sum : 0;
                        Eval(m.Ch[i], weight * w);
                    }
                    break;
                }
                default:
                {
                    // 2D: 그라디언트 밴드 보간 (Unity 자유 직교와 같은 식)
                    var p = new Vector2(m.P >= 0 ? _params[m.P] : 0, m.Py >= 0 ? _params[m.Py] : 0);
                    var h = new float[n]; float total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var pi = new Vector2(m.X[i], m.Y[i]);
                        float best = 1;
                        for (int j = 0; j < n; j++)
                        {
                            if (j == i) continue;
                            var d = new Vector2(m.X[j], m.Y[j]) - pi;
                            float len2 = d.sqrMagnitude;
                            if (len2 < 1e-8f) continue;
                            best = Mathf.Min(best, 1 - Vector2.Dot(p - pi, d) / len2);
                        }
                        h[i] = Mathf.Max(0, best); total += h[i];
                    }
                    if (total <= 1e-6f) break;
                    for (int i = 0; i < n; i++) Eval(m.Ch[i], weight * h[i] / total);
                    break;
                }
            }
        }

        // ---------------- 트래킹 값 → VRCFT v2 파라미터 ----------------
        static float G(Dictionary<string, float> v, string n) => v.TryGetValue(n, out var x) ? x : 0;
        static float Avg(Dictionary<string, float> v, string b) => (G(v, b + "Left") + G(v, b + "Right")) * 0.5f;

        /// <summary>VRCFT v2 파라미터 하나 (좌우 하나로 합친 것 · 양쪽으로 움직이는 것은 VRCFT와 같은 식).</summary>
        public static float V2(string name, Dictionary<string, float> v)
        {
            switch (name)
            {
                // 눈: EyeLid는 뜬 정도 (0 감음, 0.75 보통, 1 크게)
                case "EyeLidLeft": return (1 - G(v, "eyeBlinkLeft")) * 0.75f + G(v, "eyeWideLeft") * 0.25f;
                case "EyeLidRight": return (1 - G(v, "eyeBlinkRight")) * 0.75f + G(v, "eyeWideRight") * 0.25f;
                case "EyeLid": return (1 - Mathf.Max(G(v, "eyeBlinkLeft"), G(v, "eyeBlinkRight"))) * 0.75f + Avg(v, "eyeWide") * 0.25f;
                case "EyeClosedLeft": return G(v, "eyeBlinkLeft");
                case "EyeClosedRight": return G(v, "eyeBlinkRight");
                case "EyeClosed": return Mathf.Max(G(v, "eyeBlinkLeft"), G(v, "eyeBlinkRight"));
                case "EyeSquint": return Avg(v, "eyeSquint");
                case "EyeWide": return Avg(v, "eyeWide");
                // 시선: +X = 아바타 오른쪽, +Y = 위 (왼눈 오른쪽 = 안쪽, 오른눈 오른쪽 = 바깥쪽)
                case "EyeLeftX": return G(v, "eyeLookInLeft") - G(v, "eyeLookOutLeft");
                case "EyeRightX": return G(v, "eyeLookOutRight") - G(v, "eyeLookInRight");
                case "EyeX": return (G(v, "eyeLookInLeft") - G(v, "eyeLookOutLeft") + G(v, "eyeLookOutRight") - G(v, "eyeLookInRight")) * 0.5f;
                case "EyeLeftY": return G(v, "eyeLookUpLeft") - G(v, "eyeLookDownLeft");
                case "EyeRightY": return G(v, "eyeLookUpRight") - G(v, "eyeLookDownRight");
                case "EyeY": return Avg(v, "eyeLookUp") - Avg(v, "eyeLookDown");
                // 눈썹
                case "BrowInnerUp": case "BrowInnerUpLeft": case "BrowInnerUpRight": return G(v, "browInnerUp");
                case "BrowOuterUp": return Avg(v, "browOuterUp");
                case "BrowUp": return Mathf.Max(G(v, "browInnerUp"), Avg(v, "browOuterUp"));
                case "BrowPinchLeft": case "BrowLowererLeft": return G(v, "browDownLeft");
                case "BrowPinchRight": case "BrowLowererRight": return G(v, "browDownRight");
                case "BrowPinch": case "BrowLowerer": case "BrowDown": return Avg(v, "browDown");
                case "BrowExpressionLeft": return (G(v, "browInnerUp") + G(v, "browOuterUpLeft")) * 0.5f - G(v, "browDownLeft");
                case "BrowExpressionRight": return (G(v, "browInnerUp") + G(v, "browOuterUpRight")) * 0.5f - G(v, "browDownRight");
                case "BrowExpression": return (G(v, "browInnerUp") + Avg(v, "browOuterUp")) * 0.5f - Avg(v, "browDown");
                // 코·볼
                case "NoseSneer": return Avg(v, "noseSneer");
                case "CheekPuffLeft": case "CheekPuffRight": case "CheekPuff": return G(v, "cheekPuff");
                case "CheekSuck": return Avg(v, "cheekSuck");
                case "CheekPuffSuckLeft": return G(v, "cheekPuff") - G(v, "cheekSuckLeft");
                case "CheekPuffSuckRight": return G(v, "cheekPuff") - G(v, "cheekSuckRight");
                case "CheekPuffSuck": return G(v, "cheekPuff") - Avg(v, "cheekSuck");
                case "CheekSquint": return Avg(v, "cheekSquint");
                // 턱
                case "JawX": return G(v, "jawRight") - G(v, "jawLeft");
                case "JawZ": return G(v, "jawForward");
                case "MouthClosed": return G(v, "mouthClose");
                case "MouthApeShape": return Mathf.Clamp01(G(v, "jawOpen") * G(v, "mouthClose"));
                // 입술
                case "LipFunnel": case "LipFunnelUpper": case "LipFunnelLower": return G(v, "mouthFunnel");
                case "LipPucker": case "LipPuckerUpper": case "LipPuckerLower": return G(v, "mouthPucker");
                case "LipSuckUpper": return G(v, "mouthRollUpper");
                case "LipSuckLower": return G(v, "mouthRollLower");
                case "LipSuck": return (G(v, "mouthRollUpper") + G(v, "mouthRollLower")) * 0.5f;
                // 입
                case "MouthUpperUp": return Avg(v, "mouthUpperUp");
                case "MouthLowerDown": return Avg(v, "mouthLowerDown");
                case "MouthX": case "MouthUpperX": case "MouthLowerX": return G(v, "mouthRight") - G(v, "mouthLeft");
                case "SmileSadLeft": case "SmileFrownLeft": return G(v, "mouthSmileLeft") - G(v, "mouthFrownLeft");
                case "SmileSadRight": case "SmileFrownRight": return G(v, "mouthSmileRight") - G(v, "mouthFrownRight");
                case "SmileSad": case "SmileFrown": return Avg(v, "mouthSmile") - Avg(v, "mouthFrown");
                case "MouthSmile": return Avg(v, "mouthSmile");
                case "MouthSadLeft": return G(v, "mouthFrownLeft");
                case "MouthSadRight": return G(v, "mouthFrownRight");
                case "MouthSad": case "MouthFrown": return Avg(v, "mouthFrown");
                case "MouthStretch": return Avg(v, "mouthStretch");
                case "MouthDimple": return Avg(v, "mouthDimple");
                case "MouthPress": return Avg(v, "mouthPress");
                case "MouthRaiserLower": return G(v, "mouthShrugLower");
                case "MouthRaiserUpper": return G(v, "mouthShrugUpper");
                case "MouthTightener": return Avg(v, "mouthPress");
                // 혀
                case "TongueX": return G(v, "tongueRight") - G(v, "tongueLeft");
                case "TongueY": return G(v, "tongueUp") - G(v, "tongueDown");
            }
            // 나머지는 같은 이름의 값 (JawOpen → jawOpen, MouthSmileLeft → mouthSmileLeft, TongueOut → tongueOut …)
            if (name.Length > 0 && v.TryGetValue(char.ToLowerInvariant(name[0]) + name.Substring(1), out var same)) return same;
            if (name.EndsWith("Left") || name.EndsWith("Right"))
            {
                // Unified 이름 → ARKit 이름 (MouthCornerPullLeft → mouthSmileLeft …)
                bool left = name.EndsWith("Left");
                string b = name.Substring(0, name.Length - (left ? 4 : 5)), side = left ? "Left" : "Right";
                switch (b)
                {
                    case "MouthCornerPull": return G(v, "mouthSmile" + side);
                    case "MouthCornerSlant": return G(v, "mouthSmile" + side) * 0.5f;
                    case "EyeClosed": return G(v, "eyeBlink" + side);
                    case "BrowOuterUp": return G(v, "browOuterUp" + side);
                }
            }
            return 0;
        }
    }
}
