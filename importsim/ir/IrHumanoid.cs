// Animator의 휴머노이드 Avatar → IR humanoid { 휴머노이드 뼈 이름(Hips, LeftUpperArm…): 노드 번호 } (IR v1에 더한 선택 항목).
// FBX 모델의 Avatar는 모델 .meta의 ModelImporter.humanDescription.human (boneName ↔ humanName) 에 있다.
// Unity처럼 Animator가 붙은 오브젝트 아래에서 이름으로 뼈를 찾는다 (계층 순서로 처음 만나는 것).
// 따로 만든 Avatar 에셋(.asset, UniVRM 등이 만듦)은 본문의 m_HumanDescription을 같은 방식으로 읽는다 (2026-10-23).
// 같은 humanDescription의 비틀림 분배·늘이기·발 간격과, 기본값을 바꾼 뼈의 근육 범위(limit)는 IR humanoidSettings로 (Unity 기본값과 같으면 생략).
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        const long ModelAvatarFileId = 9000000;
        /// <summary>HumanoidJson이 읽은 모델의 비틀림·근육 범위 설정 (기본값이면 null).</summary>
        JNode? _humanSettings;

        JNode HumanoidJson(SimFile f, List<SimObject> nodeGos, JNode nodes)
        {
            var result = JNode.Obj();
            for (int id = 0; id < nodeGos.Count; id++)
            {
                var anim = Components(f, nodeGos[id]).FirstOrDefault(c => c.ClassId == 95 && Ref.FileId(c.Props.GetValueOrDefault("m_Avatar")) != 0);
                if (anim == null) continue;
                var av = anim.Props.GetValueOrDefault("m_Avatar");
                var asset = Ref.Guid(av) is string g ? db.Get(g) : null;
                if (asset == null) { Warnings.Add($"{Name(nodeGos[id])}: Animator의 Avatar 에셋을 찾지 못함 (휴머노이드 정보 없음)"); return result; }
                var desc = HumanDescription(asset, Ref.FileId(av));
                if (desc == null) { Warnings.Add($"{asset.Path}: Avatar를 읽지 못함 (휴머노이드 정보 없음)"); return result; }
                var human = L(desc, "human").OfType<Map>().ToList();
                if (human.Count == 0) return result; // Generic 리그
                _humanSettings = HumanoidSettings(desc, human);

                // Animator 오브젝트 아래 노드: 이름 → 처음 만나는 노드 (nodeGos는 계층 순서)
                var byName = new Dictionary<string, int>();
                var inside = new HashSet<int> { id };
                for (int n = id; n < nodeGos.Count; n++)
                {
                    if (n != id && !inside.Contains(nodes[n]["parent"].AsInt)) continue;
                    inside.Add(n);
                    var name = Name(nodeGos[n]);
                    if (!byName.ContainsKey(name)) byName[name] = n;
                }
                foreach (var h in human)
                {
                    string bone = S(h, "boneName"), hn = S(h, "humanName");
                    if (hn.Length == 0) continue;
                    if (byName.TryGetValue(bone, out var node)) result.Set(hn, node);
                    else Warnings.Add($"{asset.Path}: 휴머노이드 뼈 {hn}({bone})를 계층에서 찾지 못함");
                }
                return result;
            }
            return result;
        }

        /// <summary>Avatar의 humanDescription. 모델 안 Avatar는 모델 .meta의 ModelImporter.humanDescription,
        /// 따로 만든 Avatar 에셋(.asset, 클래스 90)은 본문의 m_HumanDescription — 키의 "m_"를 떼고 첫 글자를 소문자로 바꾸면 .meta와 같은 모양이다
        /// (m_Human[].m_BoneName → human[].boneName, m_Limit.m_Modified → limit.modified, m_ArmTwist → armTwist).</summary>
        Map? HumanDescription(AssetEntry asset, long fileId)
        {
            if (fileId == ModelAvatarFileId && asset.Ext != ".asset")
            {
                if (asset.Meta.Length == 0) return null;
                var importer = Yaml.Parse(asset.Meta).GetValueOrDefault("ModelImporter") as Map;
                return importer?.GetValueOrDefault("humanDescription") as Map ?? new Map();
            }
            var text = System.Text.Encoding.UTF8.GetString(asset.Data ?? System.Array.Empty<byte>());
            if (!text.StartsWith("%YAML")) { Warnings.Add($"{asset.Path}: 바이너리로 저장된 Avatar 에셋은 읽지 않음"); return null; }
            // 본문의 큰 hex 배열(m_SkeletonNameIDArray 등)은 필요 없으니 m_HumanDescription 부분만 잘라 읽는다
            int at = text.IndexOf("\n  m_HumanDescription:", System.StringComparison.Ordinal);
            if (at < 0) return null;
            var doc = Yaml.ParseDocuments("--- !u!90 &" + fileId + "\nAvatar:" + text.Substring(at)).FirstOrDefault();
            return doc?.Body.GetValueOrDefault("m_HumanDescription") is Map m ? Unprefix(m) : null;
        }

        static Map Unprefix(Map m)
        {
            var r = new Map();
            foreach (var kv in m) r[Key(kv.Key)] = Value(kv.Value);
            return r;
            static string Key(string k) => k.StartsWith("m_") && k.Length > 2 ? char.ToLowerInvariant(k[2]) + k.Substring(3) : k;
            static object? Value(object? v) => v switch { Map mm => Unprefix(mm), List<object?> l => l.Select(Value).ToList(), _ => v };
        }

        static readonly (string Key, float Default)[] HumanFloats =
        {
            ("armTwist", 0.5f), ("foreArmTwist", 0.5f), ("upperLegTwist", 0.5f), ("legTwist", 0.5f),
            ("armStretch", 0.05f), ("legStretch", 0.05f), ("feetSpacing", 0f),
        };

        /// <summary>
        /// { armTwist, foreArmTwist, upperLegTwist, legTwist, armStretch, legStretch, feetSpacing, hasTranslationDoF,
        ///   limits: { 휴머노이드 이름: { min: [x,y,z], max: [x,y,z], center: [x,y,z], axisLength } } } — 모두 Unity 기본값이면 null.
        /// limit은 serializedVersion 3이면 modified: 1, 예전 형식이면 useDefaultValues: 0인 뼈만 (도 단위, Unity HumanLimit과 같은 값).
        /// </summary>
        public static JNode? HumanoidSettings(Map desc, List<Map> human)
        {
            var o = JNode.Obj();
            bool any = false;
            foreach (var (key, def) in HumanFloats)
            {
                float v = Num(S(desc, key), def);
                o.Set(key, v);
                any |= System.Math.Abs(v - def) > 1e-4f;
            }
            bool dof = S(desc, "hasTranslationDoF") == "1";
            o.Set("hasTranslationDoF", dof);
            any |= dof;
            var limits = JNode.Obj();
            foreach (var h in human)
            {
                var hn = S(h, "humanName");
                if (hn.Length == 0 || !(h.GetValueOrDefault("limit") is Map lim)) continue;
                bool custom = lim.ContainsKey("modified") ? S(lim, "modified") == "1" : S(lim, "useDefaultValues") == "0";
                if (!custom) continue;
                limits.Set(hn, JNode.Obj().Set("min", JNode.FloatArray(V(lim, "min"))).Set("max", JNode.FloatArray(V(lim, "max")))
                    .Set("center", JNode.FloatArray(V(lim, "value"))).Set("axisLength", Num(S(lim, "length"), 0)));
            }
            if (limits.Props.Any()) { o.Set("limits", limits); any = true; }
            return any ? o : null;
        }

        static float Num(string s, float def) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        static float[] V(Map m, string k) => m.GetValueOrDefault(k) is Map v ? new[] { Num(S(v, "x"), 0), Num(S(v, "y"), 0), Num(S(v, "z"), 0) } : new float[3];
    }
}
