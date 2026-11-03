// VRChat PhysBone · PhysBone Collider → IR physBones / physBoneColliders (IR v1에 더한 선택 항목, editor/Protocol/Ir.cs).
// 컴포넌트는 VRC SDK DLL 안의 스크립트라 GUID·fileID 대신 필드 이름으로 알아본다 (SDK 버전이 달라도 같은 이름을 쓴다):
//   PhysBone: rootTransform + pull + spring,  Collider: shapeType + insideBounds
// 값은 해석하지 않고 숫자·벡터·커브·참조로만 나눠 옮긴다. 시뮬레이션 규칙은 렌더러(PhysBoneSim)가 가진다.
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        static bool IsPhysBone(SimObject c) => c.ClassId == 114 && c.Props.ContainsKey("rootTransform") && c.Props.ContainsKey("pull") && c.Props.ContainsKey("spring");
        static bool IsPhysBoneCollider(SimObject c) => c.ClassId == 114 && c.Props.ContainsKey("shapeType") && c.Props.ContainsKey("insideBounds");

        // 편집기 표시용 값 (시뮬레이션과 무관)
        static readonly HashSet<string> PbSkip = new HashSet<string> { "showGizmos", "boneOpacity", "limitOpacity", "parameter" };

        static (JNode bones, JNode colliders) PhysBonesJson(SimFile f, List<SimObject> nodeGos, Dictionary<long, int> trToNode)
        {
            var bones = JNode.Arr(); var colliders = JNode.Arr();
            var colliderIndex = new Dictionary<long, int>();
            var pbs = new List<(SimObject c, int node)>();
            for (int id = 0; id < nodeGos.Count; id++)
                foreach (var c in Components(f, nodeGos[id]))
                {
                    if (IsPhysBoneCollider(c)) { colliderIndex[c.Id] = colliders.Count; colliders.Add(ComponentJson(f, c, id, trToNode, null)); }
                    else if (IsPhysBone(c)) pbs.Add((c, id));
                }
            foreach (var (c, node) in pbs) bones.Add(ComponentJson(f, c, node, trToNode, colliderIndex).Set("id", bones.Count));
            return (bones, colliders);
        }

        /// 공통: { node, enabled, source, root, values{}, vectors{}, curves{}, refs… }
        static JNode ComponentJson(SimFile f, SimObject c, int node, Dictionary<long, int> trToNode, Dictionary<long, int>? colliderIndex)
        {
            int NodeOf(object? r) => trToNode.TryGetValue(Ref.FileId(r), out var n) ? n : -1;
            var j = JNode.Obj().Set("node", node).Set("enabled", S(c.Props, "m_Enabled") != "0")
                .Set("source", new SourceRef { Guid = f.Guid, FileId = c.Id }.ToJson());
            var values = JNode.Obj(); var vectors = JNode.Obj(); var curves = JNode.Obj();
            foreach (var kv in c.Props)
            {
                var k = kv.Key;
                if (k.StartsWith("m_") || k.StartsWith("foldout_") || PbSkip.Contains(k)) continue;
                switch (kv.Value)
                {
                    case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, C, out var d):
                        values.Set(k, (float)d); break;
                    case Map m when Ref.IsRef(m):
                        if (k == "rootTransform") j.Set("root", NodeOf(m));
                        break;
                    case Map m when m.ContainsKey("m_Curve"):
                        {
                            var keys = L(m, "m_Curve").OfType<Map>().ToList();
                            if (keys.Count == 0) break;
                            curves.Set(k, JNode.FloatArray(keys.SelectMany(kf => new[] { P(S(kf, "time")), P(S(kf, "value")), P(S(kf, "inSlope")), P(S(kf, "outSlope")) }).ToArray()));
                            break;
                        }
                    case Map m when m.ContainsKey("x"):
                        vectors.Set(k, JNode.FloatArray(Vec(m, null, m.ContainsKey("w") ? new[] { "x", "y", "z", "w" } : new[] { "x", "y", "z" })));
                        break;
                    case Map m: // collisionFilter { allowSelf, allowOthers } 같은 묶음 → "collisionFilter.allowSelf"
                        foreach (var sub in m) if (sub.Value is string ss && double.TryParse(ss, System.Globalization.NumberStyles.Float, C, out var sd)) values.Set(k + "." + sub.Key, (float)sd);
                        break;
                    case List<object?> list when k == "ignoreTransforms":
                        {
                            var a = JNode.Arr(); foreach (var r in list) { int n = NodeOf(r); if (n >= 0) a.Add(n); }
                            j.Set("ignore", a); break;
                        }
                    case List<object?> list when k == "colliders" && colliderIndex != null:
                        {
                            var a = JNode.Arr(); foreach (var r in list) if (colliderIndex.TryGetValue(Ref.FileId(r), out var ci)) a.Add(ci);
                            j.Set("colliders", a); break;
                        }
                }
            }
            if (!j.Has("root")) j.Set("root", -1);
            return j.Set("values", values).Set("vectors", vectors).Set("curves", curves);
        }
    }
}
