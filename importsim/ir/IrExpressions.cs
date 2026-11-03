// 아바타의 기본 표정 → IR 선택 항목 expressions (editor/Protocol/Ir.cs IrExpression).
// 아바타 루트의 VRCAvatarDescriptor → baseAnimationLayers 중 FX(type 5) 애니메이터 컨트롤러 → 레이어별 상태(AnimatorState)·블렌드 트리의 모션(.anim)
// 중 블렌드쉐이프 커브(SkinnedMeshRenderer blendShape.*)가 있는 것을 표정 하나로 낸다:
//   { name: 상태 이름, layer: 레이어 이름, clip: 클립 이름, guid: .anim guid, shapes: [ { path: 아바타 루트 기준 렌더러 경로, shape, value: 첫 키 값 } ] }
// 얼굴 메시(VisemeSkinnedMesh, 없으면 eyelidsSkinnedMesh)의 블렌드쉐이프를 0이 아닌 값으로 바꾸는 클립만 표정으로 본다
// (옷 끄기·가슴 크기·앞머리 같은 토글 애니메이션, 값을 0으로 되돌리는 reset 클립은 뺀다). 얼굴 메시를 모르면 0이 아닌 값이 있는 클립 모두.
// 같은 클립은 처음 만난 상태 이름으로 한 번만. 바이너리 .anim·없는 에셋은 건너뛴다. 표정을 실제로 적용하는 것은 Host·렌더러(미리보기 전용)다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        const string DescriptorGuid = "67cc4cb7839cd3741b63733d5adf0442";
        const int FxLayer = 5;

        JNode ExpressionsJson(SimFile f, SimObject root)
        {
            var arr = JNode.Arr();
            try
            {
                var desc = Components(f, root).FirstOrDefault(c => c.ClassId == 114 && Ref.Guid(c.Props.GetValueOrDefault("m_Script")) == DescriptorGuid);
                if (desc == null) return arr;
                var face = FacePath(f, root, desc);
                foreach (var l in L(desc.Props, "baseAnimationLayers"))
                {
                    if (!(l is Map lm) || S(lm, "type") != FxLayer.ToString(CultureInfo.InvariantCulture)) continue;
                    var ctrl = db.Get(Ref.Guid(lm.GetValueOrDefault("animatorController")) ?? "");
                    if (ctrl == null) continue;
                    foreach (var e in ControllerExpressions(ctrl))
                        if (e["shapes"].Items.Any(x => x["value"].AsFloat != 0 && (face == null || x["path"].AsString == face))) arr.Add(e);
                }
            }
            catch (Exception e) { Warnings.Add("기본 표정을 읽지 못함: " + e.Message); }
            return arr;
        }

        /// <summary>디스크립터의 ViewPosition (아바타 루트 기준 눈 위치, VR 시점 높이) → [x, y, z]. 없으면 null.</summary>
        JNode? ViewPositionJson(SimFile f, SimObject root)
        {
            var desc = Components(f, root).FirstOrDefault(c => c.ClassId == 114 && Ref.Guid(c.Props.GetValueOrDefault("m_Script")) == DescriptorGuid);
            if (desc == null || !(desc.Props.GetValueOrDefault("ViewPosition") is Map vp)) return null;
            float P(string k) => float.TryParse(S(vp, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return JNode.FloatArray(new[] { P("x"), P("y"), P("z") });
        }

        /// <summary>디스크립터가 가리키는 얼굴 메시의 아바타 루트 기준 경로 (모르면 null).</summary>
        static string? FacePath(SimFile f, SimObject root, SimObject desc)
        {
            long id = Ref.FileId(desc.Props.GetValueOrDefault("VisemeSkinnedMesh"));
            if (id == 0 && desc.Props.GetValueOrDefault("customEyeLookSettings") is Map eye) id = Ref.FileId(eye.GetValueOrDefault("eyelidsSkinnedMesh"));
            if (id == 0 || !(f.Get(id) is SimObject ren) || !(f.Get(Ref.Local(ren.Props, "m_GameObject")) is SimObject go)) return null;
            var parts = new List<string>();
            var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
            for (int guard = 0; tr != null && guard < 256; guard++)
            {
                if (!(f.Get(Ref.Local(tr.Props, "m_GameObject")) is SimObject g) || g.Id == root.Id) break;
                parts.Add(Name(g));
                tr = f.Get(Ref.FileId(tr.Props.GetValueOrDefault("m_Father")));
            }
            parts.Reverse();
            return parts.Count > 0 ? string.Join("/", parts) : null;
        }

        IEnumerable<JNode> ControllerExpressions(AssetEntry ctrl)
        {
            var docs = ParseYaml(ctrl);
            if (docs == null) yield break;
            var byId = docs.ToDictionary(d => d.FileId);
            var seen = new HashSet<string>();
            var controller = docs.FirstOrDefault(d => d.ClassId == 91);
            if (controller == null) yield break;
            foreach (var layer in L(controller.Body, "m_AnimatorLayers"))
            {
                if (!(layer is Map lm)) continue;
                var layerName = S(lm, "m_Name");
                var motions = new List<(string State, object? Motion)>();
                CollectStates(byId, Ref.FileId(lm.GetValueOrDefault("m_StateMachine")), motions, new HashSet<long>());
                foreach (var (state, motion) in motions)
                    foreach (var guid in MotionClips(byId, motion))
                    {
                        if (!seen.Add(guid)) continue;
                        var clip = db.Get(guid);
                        if (clip == null || clip.Ext != ".anim") continue;
                        var shapes = ClipShapes(clip);
                        if (shapes == null || shapes.Count == 0) continue;
                        yield return JNode.Obj().Set("name", state.Length > 0 ? state : clip.Name).Set("layer", layerName).Set("clip", clip.Name).Set("guid", guid).Set("shapes", shapes);
                    }
            }
        }

        static void CollectStates(Dictionary<long, YamlDoc> byId, long machine, List<(string, object?)> outList, HashSet<long> visited)
        {
            if (!visited.Add(machine) || !byId.TryGetValue(machine, out var sm)) return;
            foreach (var cs in L(sm.Body, "m_ChildStates"))
                if (cs is Map cm && byId.TryGetValue(Ref.FileId(cm.GetValueOrDefault("m_State")), out var st))
                    outList.Add((S(st.Body, "m_Name"), st.Body.GetValueOrDefault("m_Motion")));
            foreach (var csm in L(sm.Body, "m_ChildStateMachines"))
                if (csm is Map cm) CollectStates(byId, Ref.FileId(cm.GetValueOrDefault("m_StateMachine")), outList, visited);
        }

        /// <summary>모션이 가리키는 .anim guid들 (블렌드 트리는 자식까지, 컨트롤러 안 블렌드 트리는 fileID로).</summary>
        static IEnumerable<string> MotionClips(Dictionary<long, YamlDoc> byId, object? motion, int depth = 0)
        {
            if (depth > 8) yield break;
            var guid = Ref.Guid(motion);
            if (!string.IsNullOrEmpty(guid)) { yield return guid!; yield break; }
            if (!byId.TryGetValue(Ref.FileId(motion), out var tree) || tree.ClassId != 206) yield break;
            foreach (var ch in L(tree.Body, "m_Childs"))
                if (ch is Map cm) foreach (var g in MotionClips(byId, cm.GetValueOrDefault("m_Motion"), depth + 1)) yield return g;
        }

        static List<YamlDoc>? ParseYaml(AssetEntry a)
        {
            var data = a.Data;
            if (data == null) return null;
            var text = Encoding.UTF8.GetString(data);
            return text.StartsWith("%YAML", StringComparison.Ordinal) ? Yaml.ParseDocuments(text) : null;
        }

        /// <summary>.anim의 블렌드쉐이프 커브: 렌더러 경로·이름·첫 키 값 (last면 마지막 키 값 — 모션 시간 라디얼의 최대).</summary>
        static JNode? ClipShapes(AssetEntry clip, bool last = false)
        {
            var docs = ParseYaml(clip);
            var anim = docs?.FirstOrDefault(d => d.ClassId == 74);
            if (anim == null) return null;
            var arr = JNode.Arr();
            foreach (var c in L(anim.Body, "m_FloatCurves"))
            {
                if (!(c is Map cm)) continue;
                var attr = S(cm, "attribute");
                if (!attr.StartsWith("blendShape.", StringComparison.Ordinal) || S(cm, "classID") != "137") continue;
                var keys = cm.GetValueOrDefault("curve") is Map curve ? L(curve, "m_Curve") : new List<object?>();
                if (keys.Count == 0 || !(keys[last ? keys.Count - 1 : 0] is Map k0)) continue;
                arr.Add(JNode.Obj().Set("path", S(cm, "path")).Set("shape", attr.Substring("blendShape.".Length)).Set("value", P(S(k0, "value"))));
            }
            return arr;
        }
    }
}
