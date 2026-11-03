// 아바타 기능 메뉴(VRC Expressions Menu)에서 블렌드쉐이프를 움직이는 조작 → IR 선택 항목 menuShapes (editor/Protocol/Ir.cs IrMenuShape).
// 블렌드쉐이프 단축키(editor Host/Core/ShapeKeys.cs)의 후보로 쓴다. 메뉴를 재현하는 것이 아니라 "이 조작을 켜면 어떤 블렌드쉐이프가 몇이 되는지"만 읽는다.
//   디스크립터 expressionsMenu → controls[] (하위 메뉴 103은 따라 들어가 이름을 /로 잇는다, 8단계까지)
//     토글 102·버튼 101: parameter.name = P, value = v
//       FX 컨트롤러의 전이(상태 전이 1101·진입 전이 1109) 중 조건이 "P 켜짐"(If, Equals v, Greater v 미만)인 전이의 도착 상태 → 그 모션의 블렌드쉐이프 = 켰을 때
//       조건이 "P 꺼짐"(IfNot, Equals 0, NotEqual v)인 전이의 도착 상태 → 껐을 때 (없으면 0)
//       P를 블렌드 파라미터로 쓰는 블렌드 트리가 있으면 그것도 (가장 큰 문턱 = 켰을 때, 가장 작은 = 껐을 때)
//     라디얼 203: subParameters[0].name을 블렌드 파라미터로 쓰는 블렌드 트리 → 가장 작은 문턱의 값 = off, 가장 큰 = on
//                 또는 그 파라미터를 모션 시간(m_TimeParameter)으로 쓰는 상태 → 클립의 첫 키 = off, 마지막 키 = on
//     2축·4축 퍼펫은 건너뛴다
//   블렌드쉐이프 커브가 없는 조작(오브젝트 켜고 끄기 등)은 내지 않는다. Modular Avatar·VRCFury 메뉴는 읽지 않는다 (토글 재현 범위 밖).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        JNode MenuShapesJson(SimFile f, SimObject root)
        {
            var arr = JNode.Arr();
            try
            {
                var desc = Components(f, root).FirstOrDefault(c => c.ClassId == 114 && Ref.Guid(c.Props.GetValueOrDefault("m_Script")) == DescriptorGuid);
                if (desc == null) return arr;
                var menu = db.Get(Ref.Guid(desc.Props.GetValueOrDefault("expressionsMenu")) ?? "");
                if (menu == null) return arr;
                var fx = new List<Dictionary<long, YamlDoc>>();
                foreach (var l in L(desc.Props, "baseAnimationLayers"))
                {
                    if (!(l is Map lm) || S(lm, "type") != FxLayer.ToString(CultureInfo.InvariantCulture)) continue;
                    var ctrl = db.Get(Ref.Guid(lm.GetValueOrDefault("animatorController")) ?? "");
                    var docs = ctrl == null ? null : ParseYaml(ctrl);
                    if (docs != null) fx.Add(docs.ToDictionary(d => d.FileId));
                }
                if (fx.Count == 0) return arr;
                var seen = new HashSet<string>();
                WalkMenu(menu, "", fx, arr, seen, 0);
            }
            catch (Exception e) { Warnings.Add("기능 메뉴를 읽지 못함: " + e.Message); }
            return arr;
        }

        void WalkMenu(AssetEntry menu, string prefix, List<Dictionary<long, YamlDoc>> fx, JNode outArr, HashSet<string> seen, int depth)
        {
            if (depth > 8 || !seen.Add(menu.Guid)) return;
            var doc = ParseYaml(menu)?.FirstOrDefault(d => d.ClassId == 114);
            if (doc == null) return;
            foreach (var c in L(doc.Body, "controls"))
            {
                if (!(c is Map cm)) continue;
                var name = S(cm, "name");
                var type = S(cm, "type");
                string path = prefix.Length > 0 ? prefix + "/" + name : name;
                if (type == "103")
                {
                    var sub = db.Get(Ref.Guid(cm.GetValueOrDefault("subMenu")) ?? "");
                    if (sub != null) WalkMenu(sub, path, fx, outArr, seen, depth + 1);
                    continue;
                }
                string kind; string param; float value = P(S(cm, "value"), 1);
                if (type == "102" || type == "101")
                {
                    kind = type == "102" ? "toggle" : "button";
                    param = cm.GetValueOrDefault("parameter") is Map pm ? S(pm, "name") : "";
                }
                else if (type == "203")
                {
                    kind = "radial";
                    var subs = L(cm, "subParameters");
                    param = subs.Count > 0 && subs[0] is Map sp ? S(sp, "name") : "";
                }
                else continue;
                if (param.Length == 0) continue;
                var (on, off) = kind == "radial" ? TreeShapes(fx, param) : ToggleShapes(fx, param, value);
                if (on.Count == 0) continue;
                var shapes = JNode.Arr();
                foreach (var kv in on)
                {
                    float o = off.TryGetValue(kv.Key, out var ov) ? ov : 0;
                    if (Math.Abs(kv.Value - o) < 0.01f) continue;   // 켜도 그대로인 값은 뺀다
                    shapes.Add(JNode.Obj().Set("path", kv.Key.Path).Set("shape", kv.Key.Shape).Set("on", kv.Value).Set("off", o));
                }
                if (shapes.Count == 0) continue;
                outArr.Add(JNode.Obj().Set("name", name).Set("menu", path).Set("type", kind).Set("parameter", param).Set("shapes", shapes));
            }
        }

        // 조건 모드 (UnityEditor.Animations.AnimatorConditionMode)
        const int If = 1, IfNot = 2, Greater = 3, Less = 4, EqualsMode = 6, NotEqual = 7;

        (Dictionary<(string Path, string Shape), float> On, Dictionary<(string Path, string Shape), float> Off) ToggleShapes(List<Dictionary<long, YamlDoc>> fx, string param, float value)
        {
            var on = new Dictionary<(string, string), float>();
            var off = new Dictionary<(string, string), float>();
            foreach (var byId in fx)
            {
                foreach (var t in byId.Values)
                {
                    if (t.ClassId != 1101 && t.ClassId != 1109) continue;
                    bool? sideOn = null;
                    foreach (var c in L(t.Body, "m_Conditions"))
                    {
                        if (!(c is Map cm) || S(cm, "m_ConditionEvent") != param) continue;
                        int mode = (int)P(S(cm, "m_ConditionMode"));
                        float thr = P(S(cm, "m_EventTreshold"));
                        if (mode == If || mode == EqualsMode && Math.Abs(thr - value) < 0.001f || mode == Greater && thr < value) sideOn = true;
                        else if (mode == IfNot || mode == EqualsMode && Math.Abs(thr) < 0.001f || mode == NotEqual && Math.Abs(thr - value) < 0.001f || mode == Less && thr <= 0) sideOn = false;
                    }
                    if (sideOn == null || !byId.TryGetValue(Ref.FileId(t.Body.GetValueOrDefault("m_DstState")), out var st)) continue;
                    foreach (var guid in MotionClips(byId, st.Body.GetValueOrDefault("m_Motion")))
                        Merge(sideOn.Value ? on : off, guid);
                }
                // 파라미터를 블렌드 파라미터로 쓰는 블렌드 트리 (토글을 float로 받는 아바타)
                var (tOn, tOff) = TreeShapesIn(byId, param);
                foreach (var kv in tOn) if (!on.ContainsKey(kv.Key)) on[kv.Key] = kv.Value;
                foreach (var kv in tOff) if (!off.ContainsKey(kv.Key)) off[kv.Key] = kv.Value;
            }
            return (on, off);
        }

        (Dictionary<(string Path, string Shape), float> On, Dictionary<(string Path, string Shape), float> Off) TreeShapes(List<Dictionary<long, YamlDoc>> fx, string param)
        {
            var on = new Dictionary<(string, string), float>();
            var off = new Dictionary<(string, string), float>();
            foreach (var byId in fx)
            {
                var (a, b) = TreeShapesIn(byId, param);
                foreach (var kv in a) on[kv.Key] = kv.Value;
                foreach (var kv in b) off[kv.Key] = kv.Value;
            }
            return (on, off);
        }

        /// <summary>1D 블렌드 트리(blendParameter = param): 가장 큰 문턱 자식의 값 = on, 가장 작은 = off.</summary>
        (Dictionary<(string, string), float>, Dictionary<(string, string), float>) TreeShapesIn(Dictionary<long, YamlDoc> byId, string param)
        {
            var on = new Dictionary<(string, string), float>();
            var off = new Dictionary<(string, string), float>();
            foreach (var tree in byId.Values)
            {
                if (tree.ClassId != 206 || S(tree.Body, "m_BlendParameter") != param) continue;
                var kids = L(tree.Body, "m_Childs").OfType<Map>().Select(m => (T: P(S(m, "m_Threshold")), M: m.GetValueOrDefault("m_Motion"))).OrderBy(x => x.T).ToList();
                if (kids.Count < 2) continue;
                foreach (var g in MotionClips(byId, kids[kids.Count - 1].M)) Merge(on, g);
                foreach (var g in MotionClips(byId, kids[0].M)) Merge(off, g);
            }
            foreach (var st in byId.Values)
            {
                if (st.ClassId != 1102 || S(st.Body, "m_TimeParameterActive") != "1" || S(st.Body, "m_TimeParameter") != param) continue;
                foreach (var g in MotionClips(byId, st.Body.GetValueOrDefault("m_Motion"))) { Merge(on, g, last: true); Merge(off, g); }
            }
            return (on, off);
        }

        void Merge(Dictionary<(string, string), float> into, string clipGuid, bool last = false)
        {
            var clip = db.Get(clipGuid);
            if (clip == null || clip.Ext != ".anim") return;
            var shapes = ClipShapes(clip, last);
            if (shapes == null) return;
            foreach (var x in shapes.Items) into[(x["path"].AsString, x["shape"].AsString)] = x["value"].AsFloat;
        }
    }
}
