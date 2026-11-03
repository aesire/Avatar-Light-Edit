// 아바타에 설치된 페이셜 트래킹 애니메이터 (VRCFaceTracking v2 파라미터를 읽는 FX 레이어: FT 기믹·VRCFT Templates 등) → IR 선택 항목 faceTracking.
// VRChat에서는 VRCFT가 v2/JawOpen 같은 아바타 파라미터를 보내고, 아바타의 FX 블렌드 트리가 그 값으로 블렌드쉐이프 애니메이션을 섞는다.
// 렌더러(FaceFt.cs)가 같은 블렌드 트리를 트래킹 값으로 계산하도록 트리 모양과 클립의 블렌드쉐이프 값을 그대로 옮긴다:
//   { params: { 이름: 기본값 }, clips: [ { name, shapes: [ { path, shape, value } ] } ],
//     layers: [ { name, controller, root: 모션 } ] }
//   모션 = { c: 클립 번호 (-1 = 빈 모션) } | { t: 블렌드 종류(0 1D · 1 2D 단순 방향 · 2 2D 자유 방향 · 3 2D 자유 직교 · 4 Direct), p, py, n: Direct 정규화,
//                                           ch: [ { m: 모션, t: 문턱, x, y: 위치, d: Direct 파라미터 } ] }
// 컨트롤러는 디스크립터의 FX와 Modular Avatar Merge Animator(FX) — FT 기믹은 흔히 MA Merge Animator(절대 경로)로 합친다.
// 상대 경로 Merge Animator는 컴포넌트 오브젝트(또는 relativePathRoot) 경로를 클립 경로 앞에 붙인다.
// 레이어마다 상태 중 v2 파라미터를 읽는 블렌드 트리를 가진 것 하나(클립이 가장 많은 것)만, 블렌드쉐이프 커브가 있는 레이어만 낸다.
// OSCmooth 평활·바이너리 풀기 레이어는 애니메이터 파라미터만 움직이므로 빠진다 (렌더러가 프록시 파라미터에 바로 값을 넣는다).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        const string MergeAnimatorGuid = "1bb122659f724ebf85fe095ac02dc339";

        /// <summary>v2 파라미터 이름인지 (v2/JawOpen, FT/v2/…, OSCm/Proxy/v2/… 등).</summary>
        static bool IsV2(string p) => p.StartsWith("v2/", StringComparison.Ordinal) || p.Contains("/v2/");

        JNode FaceTrackingJson(SimFile f, SimObject root, List<SimObject> nodeGos)
        {
            var json = JNode.Obj();
            try
            {
                var ctrls = new List<(AssetEntry Ctrl, string Prefix)>();
                var desc = Components(f, root).FirstOrDefault(c => c.ClassId == 114 && Ref.Guid(c.Props.GetValueOrDefault("m_Script")) == DescriptorGuid);
                if (desc != null)
                    foreach (var l in L(desc.Props, "baseAnimationLayers"))
                        if (l is Map lm && S(lm, "type") == FxLayer.ToString(CultureInfo.InvariantCulture) && db.Get(Ref.Guid(lm.GetValueOrDefault("animatorController")) ?? "") is AssetEntry c0)
                            ctrls.Add((c0, ""));
                var parentOf = new Dictionary<long, long>();
                foreach (var go in nodeGos)
                {
                    var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
                    if (tr != null && f.Get(Ref.FileId(tr.Props.GetValueOrDefault("m_Father"))) is SimObject ptr && f.Get(Ref.Local(ptr.Props, "m_GameObject")) is SimObject pgo)
                        parentOf[go.Id] = pgo.Id;
                }
                string PathOf(long id)
                {
                    var parts = new List<string>();
                    for (int guard = 0; id != root.Id && guard < 256 && f.Get(id) is SimObject g; guard++)
                    {
                        parts.Add(Name(g));
                        if (!parentOf.TryGetValue(id, out id)) break;
                    }
                    parts.Reverse();
                    return string.Join("/", parts);
                }
                foreach (var go in nodeGos)
                    foreach (var c in Components(f, go))
                    {
                        if (c.ClassId != 114 || Ref.Guid(c.Props.GetValueOrDefault("m_Script")) != MergeAnimatorGuid) continue;
                        if (S(c.Props, "layerType") != FxLayer.ToString(CultureInfo.InvariantCulture)) continue;
                        if (!(db.Get(Ref.Guid(c.Props.GetValueOrDefault("animator")) ?? "") is AssetEntry ctrl)) continue;
                        string prefix = "";
                        if (S(c.Props, "pathMode") != "1")   // 0 = 상대 경로: 컴포넌트 오브젝트 기준 (relativePathRoot가 있으면 그 경로)
                            prefix = c.Props.GetValueOrDefault("relativePathRoot") is Map rp && S(rp, "referencePath").Length > 0 ? S(rp, "referencePath") : PathOf(go.Id);
                        ctrls.Add((ctrl, prefix));
                    }

                var parms = JNode.Obj();
                var clips = JNode.Arr();
                var clipIndex = new Dictionary<string, int>();
                var layers = JNode.Arr();
                foreach (var (ctrl, prefix) in ctrls.Distinct())
                {
                    var docs = ParseYaml(ctrl);
                    if (docs == null) continue;
                    var byId = docs.ToDictionary(d => d.FileId);
                    var controller = docs.FirstOrDefault(d => d.ClassId == 91);
                    if (controller == null) continue;
                    foreach (var pm in L(controller.Body, "m_AnimatorParameters").OfType<Map>())
                    {
                        var n = S(pm, "m_Name");
                        if (n.Length == 0 || parms.Has(n)) continue;
                        float def = S(pm, "m_Type") switch { "1" => P(S(pm, "m_DefaultFloat")), "3" => P(S(pm, "m_DefaultInt")), "4" => S(pm, "m_DefaultBool") == "1" ? 1 : 0, _ => 0 };
                        parms.Set(n, def);
                    }
                    foreach (var layer in L(controller.Body, "m_AnimatorLayers").OfType<Map>())
                    {
                        var states = new List<(string, object?)>();
                        CollectStates(byId, Ref.FileId(layer.GetValueOrDefault("m_StateMachine")), states, new HashSet<long>());
                        JNode? best = null; int bestClips = 0;
                        foreach (var (_, motion) in states)
                        {
                            var ps = new HashSet<string>();
                            var m = FtMotion(byId, motion, prefix, clips, clipIndex, ps, 0, out int shapeClips);
                            if (!ps.Any(IsV2) || shapeClips == 0 || shapeClips <= bestClips) continue;
                            best = m; bestClips = shapeClips;
                        }
                        if (best != null) layers.Add(JNode.Obj().Set("name", S(layer, "m_Name")).Set("controller", ctrl.Name).Set("root", best));
                    }
                }
                if (layers.Count == 0) return json;
                json.Set("params", parms).Set("clips", clips).Set("layers", layers);
            }
            catch (Exception e) { Warnings.Add("페이셜 트래킹 애니메이터를 읽지 못함: " + e.Message); }
            return json;
        }

        /// <summary>모션 하나 → { c } | { t, p, py, n, ch }. 읽은 블렌드 파라미터는 ps에, 블렌드쉐이프가 있는 클립 수는 shapeClips에.</summary>
        JNode FtMotion(Dictionary<long, YamlDoc> byId, object? motion, string prefix, JNode clips, Dictionary<string, int> clipIndex, HashSet<string> ps, int depth, out int shapeClips)
        {
            shapeClips = 0;
            var guid = Ref.Guid(motion);
            if (!string.IsNullOrEmpty(guid))
            {
                var key = prefix + "|" + guid;
                if (!clipIndex.TryGetValue(key, out var ci))
                {
                    var shapes = JNode.Arr();
                    if (db.Get(guid!) is AssetEntry clip && clip.Ext == ".anim" && ClipShapes(clip) is JNode raw)
                        foreach (var s in raw.Items)
                        {
                            var path = s["path"].AsString;
                            shapes.Add(s.Clone().Set("path", prefix.Length == 0 ? path : path.Length == 0 ? prefix : prefix + "/" + path));
                        }
                    ci = clips.Count;
                    clips.Add(JNode.Obj().Set("name", db.Get(guid!)?.Name ?? guid).Set("shapes", shapes));
                    clipIndex[key] = ci;
                }
                if (clips[ci]["shapes"].Count > 0) shapeClips = 1;
                return JNode.Obj().Set("c", ci);
            }
            if (depth > 12 || !byId.TryGetValue(Ref.FileId(motion), out var tree) || tree.ClassId != 206) return JNode.Obj().Set("c", -1);
            var b = tree.Body;
            int type = int.TryParse(S(b, "m_BlendType"), out var t) ? t : 0;
            var j = JNode.Obj().Set("t", type).Set("p", S(b, "m_BlendParameter"));
            if (type is 1 or 2 or 3) j.Set("py", S(b, "m_BlendParameterY"));
            if (type == 4 && S(b, "m_NormalizedBlendValues") == "1") j.Set("n", true);
            if (type != 4) ps.Add(S(b, "m_BlendParameter"));
            if (type is 1 or 2 or 3) ps.Add(S(b, "m_BlendParameterY"));
            var ch = JNode.Arr();
            foreach (var c in L(b, "m_Childs").OfType<Map>())
            {
                var m = FtMotion(byId, c.GetValueOrDefault("m_Motion"), prefix, clips, clipIndex, ps, depth + 1, out int sc);
                shapeClips += sc;
                var cj = JNode.Obj().Set("m", m);
                if (type == 0) cj.Set("t", P(S(c, "m_Threshold")));
                if (type is 1 or 2 or 3 && c.GetValueOrDefault("m_Position") is Map pos) cj.Set("x", P(S(pos, "x"))).Set("y", P(S(pos, "y")));
                if (type == 4) { var d = S(c, "m_DirectBlendParameter"); cj.Set("d", d); ps.Add(d); }
                ch.Add(cj);
            }
            return j.Set("ch", ch);
        }
    }
}
