// 의상 → IR 선택 항목 outfits / outfitRules (editor/Protocol/Ir.cs).
//   outfits: 붙인 아이템마다 { node(아이템 루트), name, prefab(Compose로 넣은 프리팹 guid, 없으면 ""), kind(outfit·hair·accessory·unknown), reason,
//            method(ma-merge·ma-proxy·name·manual·none), needsPlacement, target, mergeArmature, bones, matched, merged, offsetMean, offsetMax,
//            headScale(아바타 머리 ÷ 아이템 머리 크기, 모르면 없음), scale(현재 아이템 배율), scaleRoots: [{ node, path(원래 프리팹 안 경로, 루트 "") }] — 아이템 크기를 바꿀 때 함께 키울 노드 }
//   outfitRules.shapeChanges: Modular Avatar Shape Changer — 그 오브젝트가 켜져 있으면(inverted면 꺼져 있으면) 대상 블렌드쉐이프를 바꾼다
//     { node, enabled, inverted, renderer, shape, mode: "set"|"delete", value, threshold }
//   outfitRules.blendShapeSyncs: Modular Avatar Blendshape Sync — 의상 메시의 블렌드쉐이프가 몸 메시 값을 따른다
//     { renderer, shape, source, sourceShape }
// MA 컴포넌트는 직렬화 필드 이름으로 알아본다 (Shape Changer = m_shapes[ShapeName·ChangeType·Value], Blendshape Sync = Bindings[ReferenceMesh·Blendshape]).
// 대상 경로(referencePath)는 아바타 루트 기준이다. 적용 규칙은 렌더러(OutfitRules)가 가진다.
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        /// <summary>Write 전에 OutfitMerge.Apply 결과를 넣는다.</summary>
        public List<OutfitMerge.Report> OutfitReports = new List<OutfitMerge.Report>();

        JNode OutfitsJson(List<SimObject> nodeGos)
        {
            var arr = JNode.Arr();
            var goToNode = new Dictionary<long, int>();
            for (int i = 0; i < nodeGos.Count; i++) goToNode[nodeGos[i].Id] = i;
            foreach (var r in OutfitReports)
            {
                if (!goToNode.TryGetValue(r.OutfitRoot, out var node)) continue;
                var parts = JNode.Arr();
                foreach (var m in r.Moved) if (goToNode.TryGetValue(m, out var pn)) parts.Add(pn);
                var j = ReportJson(r).Set("node", node).Set("name", Name(nodeGos[node]));
                if (parts.Count > 0) j.Set("parts", parts);
                var roots = JNode.Arr();
                foreach (var kv in r.ScaleRoots) if (goToNode.TryGetValue(kv.Key, out var sn)) roots.Add(JNode.Obj().Set("node", sn).Set("path", kv.Value));
                if (roots.Count > 0) j.Set("scaleRoots", roots);
                arr.Add(j);
            }
            return arr;
        }

        /// <summary>아이템 보고 하나 (IR outfits 항목과 Host의 "넣어 보기" 결과가 같은 모양)</summary>
        public static JNode ReportJson(OutfitMerge.Report r) =>
            JNode.Obj().Set("prefab", r.AddedFrom).Set("kind", r.Kind).Set("reason", r.Reason).Set("method", r.Method)
                .Set("needsPlacement", r.NeedsPlacement).Set("nearOrigin", r.NearOrigin).Set("target", r.Target).Set("mergeArmature", r.MergeArmature)
                .Set("bones", r.Bones).Set("matched", r.Matched).Set("merged", r.Merged)
                .Set("offsetMean", (float)r.MeanOffset).Set("offsetMax", (float)r.MaxOffset)
                .Set("headScale", (float)r.HeadScale).Set("scale", (float)r.Scale);

        static JNode OutfitRulesJson(SimFile f, SimObject root, List<SimObject> nodeGos)
        {
            var changes = JNode.Arr(); var syncs = JNode.Arr();
            long rootTr = OutfitMerge.Tr(f, root);
            string RendererAt(Map? reference)
            {
                if (reference == null) return "";
                long tr = Ref.Local(reference, "targetObject") is long t && t != 0 && f.Get(t) is SimObject tg ? OutfitMerge.Tr(f, tg) : OutfitMerge.Find(f, rootTr, S(reference, "referencePath"));
                var go = tr == 0 ? null : f.Get(Ref.Local(f.Get(tr)!.Props, "m_GameObject"));
                return go == null ? "" : SmrOf(f, go);
            }
            for (int node = 0; node < nodeGos.Count; node++)
                foreach (var c in Components(f, nodeGos[node]))
                {
                    if (c.ClassId != 114) continue;
                    var enabled = S(c.Props, "m_Enabled") != "0";
                    if (c.Props.GetValueOrDefault("m_shapes") is List<object?> shapes && shapes.OfType<Map>().Any(m => m.ContainsKey("ShapeName")))
                    {
                        bool inverted = S(c.Props, "m_inverted") == "1";
                        float threshold = float.TryParse(S(c.Props, "m_threshold"), System.Globalization.NumberStyles.Float, C, out var th) ? th : 0.01f;
                        foreach (var m in shapes.OfType<Map>())
                        {
                            var r = RendererAt(m.GetValueOrDefault("Object") as Map);
                            if (r.Length == 0) { f.Warnings.Add($"Shape Changer 대상 메시 없음: {Name(nodeGos[node])} → {S(m.GetValueOrDefault("Object") as Map ?? new Map(), "referencePath")}"); continue; }
                            changes.Add(JNode.Obj().Set("node", node).Set("enabled", enabled).Set("inverted", inverted).Set("renderer", r)
                                .Set("shape", S(m, "ShapeName")).Set("mode", S(m, "ChangeType") == "0" ? "delete" : "set")
                                .Set("value", float.TryParse(S(m, "Value"), System.Globalization.NumberStyles.Float, C, out var v) ? v : 100f)
                                .Set("threshold", threshold));
                        }
                    }
                    else if (c.Props.GetValueOrDefault("Bindings") is List<object?> binds && binds.OfType<Map>().Any(m => m.ContainsKey("Blendshape") && m.ContainsKey("ReferenceMesh")))
                    {
                        var local = SmrOf(f, nodeGos[node]);
                        if (local.Length == 0 || !enabled) continue;
                        foreach (var m in binds.OfType<Map>())
                        {
                            var src = RendererAt(m.GetValueOrDefault("ReferenceMesh") as Map);
                            if (src.Length == 0) continue;
                            var shape = S(m, "LocalBlendshape");
                            syncs.Add(JNode.Obj().Set("renderer", local).Set("shape", shape.Length > 0 ? shape : S(m, "Blendshape"))
                                .Set("source", src).Set("sourceShape", S(m, "Blendshape")));
                        }
                    }
                }
            return JNode.Obj().Set("shapeChanges", changes).Set("blendShapeSyncs", syncs);
        }

        /// <summary>
        /// 아바타 본의 자식으로 붙인 의상 본이 아바타 PhysBone 체인 안에 들어가면 그 PhysBone의 ignore에 넣는다.
        /// 의상 본은 아바타 본을 그대로 따라가야 하고, 체인의 새 가지(길이 0)로 흔들리면 안 된다 (MA에서는 같은 위치의 본을 하나로 합친다).
        /// </summary>
        void IgnoreAttachedBones(JNode physBones, JNode nodes, Dictionary<long, int> trToNode)
        {
            var attached = new HashSet<int>();
            foreach (var r in OutfitReports) foreach (var t in r.Attached) if (trToNode.TryGetValue(t, out var n)) attached.Add(n);
            if (attached.Count == 0) return;
            foreach (var pb in physBones.Items)
            {
                int root = pb["root"].AsInt;
                if (root < 0) continue;
                var ignore = pb.Has("ignore") ? pb["ignore"] : JNode.Arr();
                var have = new HashSet<int>(ignore.Items.Select(x => x.AsInt));
                foreach (var n in attached)
                {
                    // n이 root 아래인지 (부모를 따라 올라가 본다)
                    for (int p = nodes[n]["parent"].AsInt; p >= 0; p = nodes[p]["parent"].AsInt)
                        if (p == root) { if (have.Add(n)) ignore.Add(n); break; }
                }
                pb.Set("ignore", ignore);
            }
        }

        static string SmrOf(SimFile f, SimObject go) =>
            Components(f, go).FirstOrDefault(x => x.ClassId == 137)?.Id.ToString(C) ?? "";
    }
}
