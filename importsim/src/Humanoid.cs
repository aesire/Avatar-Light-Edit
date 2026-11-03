// 휴머노이드 뼈 이름 (모델 .meta ModelImporter.humanDescription.human: boneName ↔ humanName).
//   아바타: 루트 아래 Animator의 모델 Avatar → humanName → Transform (IrHumanoid와 같은 규칙: Animator 아래에서 이름으로 처음 만나는 것)
//   의상·헤어: 스킨드 메시가 쓰는 FBX의 .meta → boneName → humanName (의상 본 이름이 아바타와 달라도 짝을 찾을 수 있게)
// HumanBodyBones 번호(MA Bone Proxy의 boneReference)도 humanName으로 바꾼다.
using System;
using System.Collections.Generic;
using System.Linq;

namespace ImportSim
{
    public static class Humanoid
    {
        const long ModelAvatarFileId = 9000000;

        /// <summary>UnityEngine.HumanBodyBones 순서 → humanDescription의 humanName</summary>
        public static readonly string[] BodyBones = BuildBodyBones();

        static string[] BuildBodyBones()
        {
            var l = new List<string> { "Hips", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "LeftFoot", "RightFoot", "Spine", "Chest", "Neck", "Head",
                "LeftShoulder", "RightShoulder", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand", "LeftToes", "RightToes",
                "LeftEye", "RightEye", "Jaw" };
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                    foreach (var part in new[] { "Proximal", "Intermediate", "Distal" })
                        l.Add($"{side} {finger} {part}");
            l.Add("UpperChest");
            return l.ToArray();
        }

        /// <summary>아바타 루트 아래 Animator의 휴머노이드 뼈: humanName → Transform fileID (없으면 빈 사전)</summary>
        public static Dictionary<string, long> AvatarBones(AssetDb db, SimFile f, SimObject root)
        {
            var result = new Dictionary<string, long>();
            foreach (var go in Walk(f, root))
            {
                var anim = Comps(f, go).FirstOrDefault(c => c.ClassId == 95 && Ref.FileId(c.Props.GetValueOrDefault("m_Avatar")) != 0);
                if (anim == null) continue;
                var av = anim.Props.GetValueOrDefault("m_Avatar");
                if (Ref.FileId(av) != ModelAvatarFileId || !(Ref.Guid(av) is string g) || db.Get(g) is not AssetEntry asset) return result;
                // 같은 이름이 여럿일 수 있다: 아바타 루트 아래 아이템(헤어·의상 프리팹)이 자기 Armature/Hips/…/Head를 갖고 있고,
                // 하이어라키 순서상 아바타 Armature보다 먼저 올 수 있다. 얕은 것부터 보고(너비 우선), Hips를 정한 뒤로는 그 Hips 아래 것을 고른다.
                var list = HumanList(asset).ToList();
                var cands = new Dictionary<string, List<(long Tr, int Depth)>>();
                var parent = new Dictionary<long, long>();
                var q = new Queue<(SimObject Go, long ParentTr, int Depth)>();
                q.Enqueue((go, 0, 0));
                while (q.Count > 0)
                {
                    var (o, ptr, depth) = q.Dequeue();
                    var tr = OutfitMerge.Tr(f, o);
                    if (tr == 0) continue;
                    parent[tr] = ptr;
                    var n = o.Props.GetValueOrDefault("m_Name") as string ?? "";
                    if (!cands.TryGetValue(n, out var cl)) cands[n] = cl = new List<(long, int)>();
                    cl.Add((tr, depth));
                    foreach (var c in f.Get(tr)!.Props.GetValueOrDefault("m_Children") as List<object?> ?? new List<object?>())
                        if (f.Get(Ref.FileId(c)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg) q.Enqueue((cg, tr, depth + 1));
                }
                bool Under(long t, long anc) { for (var x = t; x != 0; x = parent.TryGetValue(x, out var p) ? p : 0) if (x == anc) return true; return false; }
                long hips = 0;
                var hipsBone = list.FirstOrDefault(b => b.Human == "Hips").Bone;
                if (hipsBone != null && cands.TryGetValue(hipsBone, out var hc)) hips = hc[0].Tr;   // 가장 얕은 Hips
                foreach (var (bone, human) in list)
                {
                    if (!cands.TryGetValue(bone, out var cl)) continue;
                    var pick = hips != 0 ? cl.FirstOrDefault(x => Under(x.Tr, hips)) : default;
                    result[human] = pick.Tr != 0 ? pick.Tr : cl[0].Tr;
                }
                return result;
            }
            return result;
        }

        /// <summary>모델 하나의 boneName → humanName (Humanoid 리그가 아니면 빈 사전)</summary>
        public static Dictionary<string, string> ModelBones(AssetEntry? model)
        {
            var d = new Dictionary<string, string>();
            if (model == null) return d;
            foreach (var (bone, human) in HumanList(model)) d[bone] = human;
            return d;
        }

        static IEnumerable<(string Bone, string Human)> HumanList(AssetEntry asset)
        {
            if (asset.Meta.Length == 0) yield break;
            Map? importer;
            try { importer = Yaml.Parse(asset.Meta).GetValueOrDefault("ModelImporter") as Map; }
            catch (Exception) { yield break; }
            var human = (importer?.GetValueOrDefault("humanDescription") as Map)?.GetValueOrDefault("human") as List<object?>;
            if (human == null) yield break;
            foreach (var h in human.OfType<Map>())
            {
                string bone = h.GetValueOrDefault("boneName") as string ?? "", hn = h.GetValueOrDefault("humanName") as string ?? "";
                if (bone.Length > 0 && hn.Length > 0) yield return (bone, hn);
            }
        }

        /// GameObject와 그 아래 전부 (계층 순서)
        public static IEnumerable<SimObject> Walk(SimFile f, SimObject go)
        {
            yield return go;
            var tr = OutfitMerge.Tr(f, go);
            if (tr == 0) yield break;
            foreach (var c in f.Get(tr)!.Props.GetValueOrDefault("m_Children") as List<object?> ?? new List<object?>())
                if (f.Get(Ref.FileId(c)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                    foreach (var x in Walk(f, cg)) yield return x;
        }

        public static IEnumerable<SimObject> Comps(SimFile f, SimObject go)
        {
            foreach (var c in go.Props.GetValueOrDefault("m_Component") as List<object?> ?? new List<object?>())
                if (f.Get(Ref.FileId((c as Map)?.GetValueOrDefault("component"))) is SimObject o) yield return o;
        }
    }
}
