// 표정 (미리보기 전용, 업로드에는 쓰지 않음): Addr.Expression = null(없음) | { name, source: "avatar"|"anim"|"tracking", shapes: [ { r: 렌더러 id, s: 블렌드쉐이프, v: 0~100 } ] }.
// 프리셋은 만들지 않는다 — 아바타에 들어 있는 기본 표정(IR expressions, FX 레이어 애니메이션)이나 사용자가 가진 표정 .anim에서만 만든다.
// .anim·IR의 경로는 아바타 루트 기준(루트 이름 없이)이라 IR 노드 경로(루트 이름 포함)와 맞춰 렌더러를 찾는다.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public static class FaceExpression
    {
        /// <summary>아바타 루트 기준 경로 → 스킨드 렌더러 id (같은 경로가 여럿이면 처음 것).</summary>
        public static Dictionary<string, string> RendererByPath(IrScene s)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in s.Renderers)
            {
                var full = s.NodePath(r.Node);
                int slash = full.IndexOf('/');
                var rel = slash < 0 ? "" : full.Substring(slash + 1);
                if (!d.ContainsKey(rel)) d[rel] = r.Id;
            }
            return d;
        }

        public static JNode Make(string name, string source, IEnumerable<(string Renderer, string Shape, float Value)> shapes)
        {
            var arr = JNode.Arr();
            foreach (var (r, sh, v) in shapes) arr.Add(JNode.Obj().Set("r", r).Set("s", sh).Set("v", v));
            return JNode.Obj().Set("name", name).Set("source", source).Set("shapes", arr);
        }

        /// <summary>아바타 기본 표정 하나. 찾지 못한 렌더러 경로의 값은 뺀다.</summary>
        public static JNode FromIr(IrScene s, IrExpression e)
        {
            var map = RendererByPath(s);
            return Make(e.Name, "avatar", e.Shapes.Where(x => map.ContainsKey(x.Path)).Select(x => (map[x.Path], x.Shape, x.Value)));
        }

        /// <summary>.anim의 블렌드쉐이프 커브(시간 t)로 만든 표정. missing = 이 아바타에서 찾지 못한 렌더러 경로 수.</summary>
        public static JNode FromClip(IrScene s, AnimClip clip, float t, out int missing, out int count)
        {
            var map = RendererByPath(s);
            var list = new List<(string, string, float)>();
            missing = 0; count = 0;
            foreach (var c in clip.Curves)
            {
                if (c.ClassId != 137 || !c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                count++;
                if (!map.TryGetValue(c.Path, out var r)) { missing++; continue; }
                list.Add((r, c.Attribute.Substring("blendShape.".Length), c.Evaluate(t)));
            }
            return Make(clip.Name, "anim", list);
        }

        public static string Name(JNode e) => e.IsNull ? "" : e["name"].AsString;

        public static IEnumerable<(string Renderer, string Shape, float Value)> Shapes(JNode e)
        {
            if (e.IsNull) yield break;
            foreach (var x in e["shapes"].Items) yield return (x["r"].AsString, x["s"].AsString, x["v"].AsFloat);
        }

        /// <summary>FX 레이어 이름을 사용자에게 보일 묶음 이름으로 (손 제스처 레이어는 하나로).</summary>
        public static string GroupLabel(string layer)
        {
            var l = layer.ToLowerInvariant();
            if (l.Contains("hand") || l.Contains("gesture") || l.Contains("手")) return "손 제스처";
            return layer.Length > 0 ? layer : "기타";
        }
    }
}
