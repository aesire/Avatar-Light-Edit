// 블렌드쉐이프 단축키 (2026-10-20 사용자 요청): 방송 중에 홍조·얼굴 이펙트·눈동자 모양 같은 블렌드쉐이프 값을 단축키로 바꾼다.
//   정의  Addr.ShapeKeys = [{ name, key, mode, group, steps, source, shapes: [{ r: 렌더러 id, s: 블렌드쉐이프, on: 켰을 때 0~100, off: 껐을 때 0~100 }] }]
//         key   전역 단축키 "Ctrl+Alt+B" 같은 글 (빈 글 = 단축키 없음, 단추로만). 게임에 포커스가 있어도 된다 (AvHost/GlobalHotkeys)
//         mode  toggle = 누를 때마다 켜고 끄기 · cycle = 누를 때마다 steps 단계로 (0~1, off에서 on까지의 비율. 라디얼 메뉴처럼 세기를 고르는 것)
//         group 같은 이름의 묶음은 하나만 켜진다 (눈동자 모양 A·B·C처럼 같이 켜면 안 되는 것). 빈 글 = 묶음 없음
//         source menu = 아바타 기능 메뉴(Expressions Menu)에서 찾은 것 · manual = 직접 지정
//   상태  누른 결과는 Document에 기록하지 않는 실시간 레이어다 (ShapeKeyRunner → 렌더러 setExpression layer=keys, 가장 위).
//         실행 취소·"저장 안 됨"이 생기지 않고, 올리기에도 들어가지 않는다 (방송 미리보기 전용). 정의는 세션에 저장되고 실행 취소된다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public static class ShapeKeys
    {
        public static readonly float[] DefaultSteps = { 0, 0.5f, 1 };

        public static JNode Make(string name, string key, string mode, string group, IEnumerable<(string Renderer, string Shape, float On, float Off)> shapes, float[]? steps = null, string source = "manual")
        {
            var arr = JNode.Arr();
            foreach (var (r, s, on, off) in shapes) arr.Add(JNode.Obj().Set("r", r).Set("s", s).Set("on", on).Set("off", off));
            var k = JNode.Obj().Set("name", name).Set("key", key ?? "").Set("mode", mode == "cycle" ? "cycle" : "toggle").Set("group", group ?? "").Set("source", source).Set("shapes", arr);
            if (mode == "cycle") k.Set("steps", JNode.FloatArray(steps != null && steps.Length >= 2 ? steps : DefaultSteps));
            return k;
        }

        public static IEnumerable<(string Renderer, string Shape, float On, float Off)> Shapes(JNode k)
        {
            foreach (var x in k["shapes"].Items) yield return (x["r"].AsString, x["s"].AsString, x["on"].AsFloat, x["off"].AsFloat);
        }

        public static float[] Steps(JNode k)
        {
            var s = k["steps"].Kind == JKind.Array ? k["steps"].ToFloatArray() : DefaultSteps;
            return s.Length >= 2 ? s : DefaultSteps;
        }

        public static void Replace(Document doc, IEnumerable<JNode> keys, string label)
        {
            var arr = JNode.Arr();
            foreach (var k in keys) arr.Add(k);
            doc.Set(Addr.ShapeKeys, arr, label);
        }

        /// <summary>IR menuShapes(아바타 기능 메뉴에서 찾은 블렌드쉐이프 조작)를 단축키 정의로. 이 아바타에서 렌더러를 찾지 못한 값은 뺀다.</summary>
        public static JNode FromMenu(IrScene s, IrMenuShape m)
        {
            var map = FaceExpression.RendererByPath(s);
            var shapes = m.Shapes.Where(x => map.ContainsKey(x.Path)).Select(x => (map[x.Path], x.Shape, x.On, x.Off)).ToList();
            bool radial = m.Type == "radial";
            return Make(m.Name, "", radial ? "cycle" : "toggle", "", shapes, radial ? new[] { 0f, 0.25f, 0.5f, 0.75f, 1f } : null, "menu");
        }

        /// <summary>단계 값을 보이는 글로 ("0 · 50 · 100%").</summary>
        public static string StepsText(float[] steps) => string.Join(" · ", steps.Select(x => (x * 100).ToString("0", CultureInfo.InvariantCulture))) + "%";
    }

    /// <summary>
    /// 단축키를 누른 상태 (실시간, 기록하지 않음). Press로 바꾸고, Layer()가 렌더러에 보낼 값이다.
    /// 상태는 정의의 순서(인덱스)로 기억한다. 정의 목록이 바뀌면 이름·순서가 같은 것만 이어 간다 (Sync).
    /// </summary>
    public sealed class ShapeKeyRunner
    {
        // 인덱스 → 단계 (toggle: 0 꺼짐 / 1 켜짐, cycle: steps 인덱스)
        readonly Dictionary<int, int> _state = new Dictionary<int, int>();
        JNode _keys = JNode.Arr();

        public event Action? Changed;

        public JNode Keys => _keys;

        /// <summary>정의 목록이 바뀌었을 때 (아바타를 열었거나 고쳤을 때). 같은 자리에 같은 이름이면 상태를 남긴다.</summary>
        public void Sync(JNode keys)
        {
            var old = _keys;
            _keys = keys.Kind == JKind.Array ? keys : JNode.Arr();
            foreach (var i in _state.Keys.ToList())
                if (i >= _keys.Count || i >= old.Count || _keys[i]["name"].AsString != old[i]["name"].AsString) _state.Remove(i);
            Changed?.Invoke();
        }

        public void Clear() { if (_state.Count == 0) return; _state.Clear(); Changed?.Invoke(); }

        /// <summary>지금 단계 (0 = 꺼짐).</summary>
        public int Level(int i) => _state.TryGetValue(i, out var v) ? v : 0;
        public bool IsOn(int i) => Level(i) > 0;

        /// <summary>i번째를 누른다. 누른 뒤의 상태 글 ("켬", "끔", "50%"). 없으면 null.</summary>
        public string? Press(int i)
        {
            if (i < 0 || i >= _keys.Count) return null;
            var k = _keys[i];
            int next;
            if (k["mode"].AsString == "cycle") next = (Level(i) + 1) % ShapeKeys.Steps(k).Length;
            else next = IsOn(i) ? 0 : 1;
            Set(i, next);
            return Describe(i);
        }

        public void Set(int i, int level)
        {
            if (i < 0 || i >= _keys.Count) return;
            if (level <= 0) _state.Remove(i);
            else
            {
                _state[i] = level;
                // 같은 묶음의 다른 것은 끈다
                var g = _keys[i]["group"].AsString;
                if (g.Length > 0)
                    for (int j = 0; j < _keys.Count; j++)
                        if (j != i && _keys[j]["group"].AsString == g) _state.Remove(j);
            }
            Changed?.Invoke();
        }

        public string Describe(int i)
        {
            var k = _keys[i];
            if (k["mode"].AsString != "cycle") return IsOn(i) ? "켬" : "끔";
            var steps = ShapeKeys.Steps(k);
            return (steps[Math.Min(Level(i), steps.Length - 1)] * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>렌더러 단축키 레이어 값: 누른 것만 (렌더러 id, 블렌드쉐이프, 값). 뒤의 정의가 앞을 덮는다.</summary>
        public List<(string Renderer, string Shape, float Value)> Layer()
        {
            var d = new Dictionary<(string, string), float>();
            var order = new List<(string, string)>();
            for (int i = 0; i < _keys.Count; i++)
            {
                if (!_state.TryGetValue(i, out var lv) || lv <= 0) continue;
                var k = _keys[i];
                float t = k["mode"].AsString == "cycle" ? ShapeKeys.Steps(k)[Math.Min(lv, ShapeKeys.Steps(k).Length - 1)] : 1;
                foreach (var (r, s, on, off) in ShapeKeys.Shapes(k))
                {
                    if (!d.ContainsKey((r, s))) order.Add((r, s));
                    d[(r, s)] = off + (on - off) * t;
                }
            }
            return order.Select(x => (x.Item1, x.Item2, d[x])).ToList();
        }

        /// <summary>렌더러로 보낼 메시지 (빈 목록 = 레이어 지우기).</summary>
        public JNode Message()
        {
            var arr = JNode.Arr();
            foreach (var (r, s, v) in Layer()) arr.Add(JNode.Obj().Set("renderer", r).Set("shape", s).Set("weight", v));
            return Msg.Make(Msg.SetExpression).Set("layer", "keys").Set("shapes", arr);
        }
    }
}
