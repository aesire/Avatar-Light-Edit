using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImportSim
{
    /// <summary>
    /// Unity ModelImporter 시뮬레이션: FBX + .meta -> 모델 프리팹 (fileID 포함).
    /// 규칙과 검증 결과는 fileid-research/RULES.md.
    /// </summary>
    public static class ModelSim
    {
        public const long AvatarFileId = 9000000;
        static readonly Map DefaultMaterial = Ref.Make(10303, Ref.BuiltinExtraGuid, 0);

        sealed class Settings
        {
            public bool PreserveHierarchy, ImportBlendShapes = true, DeformPercent = true, UseFileScale = true, SortByName = true, LegacyBlendShapeNormals;
            public int AnimationType = 2, MaterialImportMode = 1, AvatarSetup = 1, BlendShapeNormalMode = 1, MaxBones = 4, TangentMode = 3;
            public double GlobalScale = 1, MinBoneWeight = 0.001, SmoothAngle = 60;
            public Dictionary<string, Map> Remapped = new Dictionary<string, Map>();

            public static Settings From(string meta)
            {
                int Num(string key, int def)
                {
                    var m = Regex.Match(meta, @"^\s*" + key + @": (-?\d+)", RegexOptions.Multiline);
                    return m.Success ? int.Parse(m.Groups[1].Value) : def;
                }
                var s = new Settings
                {
                    PreserveHierarchy = Num("preserveHierarchy", 0) == 1,
                    AnimationType = Num("animationType", 2),
                    AvatarSetup = Num("avatarSetup", 1),
                    ImportBlendShapes = Num("importBlendShapes", 1) == 1,
                    // 키가 없는 구버전 meta 는 0 (새 임포트는 1)
                    DeformPercent = Num("importBlendShapeDeformPercent", meta.Length == 0 ? 1 : 0) == 1,
                    MaterialImportMode = Num("materialImportMode", 1),
                    UseFileScale = Num("useFileScale", 1) == 1,
                    SortByName = Num("sortHierarchyByName", 1) == 1,
                    BlendShapeNormalMode = Num("blendShapeNormalImportMode", 1),
                    TangentMode = Num("tangentImportMode", 3),
                    // 키가 없는 구버전 meta 는 1 (새 임포트는 0)
                    LegacyBlendShapeNormals = Num("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes", meta.Length == 0 ? 0 : 1) == 1,
                    MaxBones = Num("skinWeightsMode", 0) == 1 ? Num("maxBonesPerVertex", 4) : 4,
                };
                var gsm = Regex.Match(meta, @"^\s*globalScale: ([-0-9.eE+]+)", RegexOptions.Multiline);
                var mbw = Regex.Match(meta, @"^\s*minBoneWeight: ([-0-9.eE+]+)", RegexOptions.Multiline);
                if (mbw.Success) s.MinBoneWeight = double.Parse(mbw.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var sma = Regex.Match(meta, @"^\s*normalSmoothAngle: ([-0-9.eE+]+)", RegexOptions.Multiline);
                if (sma.Success) s.SmoothAngle = double.Parse(sma.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (gsm.Success) s.GlobalScale = double.Parse(gsm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                // externalObjects: FBX 머티리얼 이름 -> 외부 .mat (흐름 매핑이 줄바꿈될 수 있어 YAML 로 읽는다)
                var importer = meta.Length == 0 ? null : Yaml.Parse(meta).GetValueOrDefault("ModelImporter") as Map;
                foreach (var e in importer?.GetValueOrDefault("externalObjects") as List<object?> ?? new List<object?>())
                    if (e is Map em && em.GetValueOrDefault("first") is Map first && first.GetValueOrDefault("type") as string == "UnityEngine:Material"
                        && em.GetValueOrDefault("second") is Map second)
                        s.Remapped[first.GetValueOrDefault("name") as string ?? ""] = second;
                return s;
            }
        }

        /// geometry: 메시 데이터(SimFile.Meshes)까지 만든다
        public static SimFile Build(AssetEntry asset, bool geometry = false)
        {
            var file = new SimFile { Guid = asset.Guid, Path = asset.Path };
            var meta = asset.Meta.Replace("\r", "");
            var st = Settings.From(meta);
            if (meta.Contains("fileIDToRecycleName:") && Regex.IsMatch(meta, @"fileIDToRecycleName:\s*\n\s+\d"))
                file.Warnings.Add("구버전 fileIDToRecycleName 표 FBX: 표 조회는 미구현 (해시 규칙으로 계산함)");

            var swLoad = System.Diagnostics.Stopwatch.StartNew();
            var scene = FbxScene.Load(asset.Data ?? throw new InvalidOperationException("FBX 데이터 없음"), geometry);
            if (geometry) Timing.Detail("FBX 읽기 " + asset.Path, swLoad.ElapsedMilliseconds);
            List<long> Models(long parent) => scene.ChildrenOf(parent).Where(c => scene.Objects.TryGetValue(c, out var o) && o.Cls == "Model").ToList();
            var space = new UnitySpace(scene, st.UseFileScale, st.GlobalScale);

            var top = Models(0);
            bool collapsed = top.Count == 1 && !st.PreserveHierarchy;

            // FBX 계층 깊이 우선 순서 (연결 순서) — m_Bones 순서에 쓴다
            var dfs = new Dictionary<long, int>();
            var depth = new Dictionary<long, int>();
            var parentOf = new Dictionary<long, long>();
            void Walk(long p, int dep) { foreach (var c in Models(p)) { dfs[c] = dfs.Count; depth[c] = dep; parentOf[c] = p; Walk(c, dep + 1); } }
            Walk(0, 0);
            var modelToTransform = new Dictionary<long, long>();
            var pendingBones = new List<(Map smr, List<long> bones)>();
            var meshSettings = new MeshSettings { ImportBlendShapes = st.ImportBlendShapes, BlendShapeNormalMode = st.BlendShapeNormalMode, MaxBones = st.MaxBones, MinBoneWeight = st.MinBoneWeight, SmoothAngle = st.SmoothAngle, TangentMode = st.TangentMode, LegacyBlendShapeNormals = st.LegacyBlendShapeNormals };
            // 메시 기하는 계층을 다 만든 뒤 병렬로 (메시가 많은 의상 FBX 하나가 차례로 3초 넘게 걸렸다 — 2026-10-21)
            var meshJobs = new List<(long MeshId, long ModelId, FbxObject Geom, Mat4 World)>();

            // GameObject 하나 생성 (+ 컴포넌트), 자식 재귀. 반환: Transform id
            long Emit(string unityPath, long modelId, long fatherTransform, Mat4 parentWorld)
            {
                var ident = "//RootNode/root" + unityPath.Substring(unityPath.IndexOf('/') < 0 ? unityPath.Length : unityPath.IndexOf('/'));
                var name = unityPath.Substring(unityPath.LastIndexOf('/') + 1);
                long goId = Xxh64.FileId("GameObject", ident), trId = Xxh64.FileId("Transform", ident + "/Transform");
                var comps = new List<object?> { new Map { ["component"] = Ref.Make(trId) } };
                var go = Add(file, 1, "GameObject", goId, new Map
                {
                    ["m_Name"] = name, ["m_IsActive"] = "1", ["m_Layer"] = "0", ["m_TagString"] = "Untagged", ["m_Component"] = comps,
                });
                var children = new List<object?>();
                var local = modelId != 0 ? space.Local(UnitySpace.FbxLocal(scene.Objects[modelId].Node), top.Contains(modelId)) : Mat4.Identity();
                local.Decompose(out var lp, out var lq, out var ls);
                var world = parentWorld * local;
                Add(file, 4, "Transform", trId, new Map
                {
                    ["m_GameObject"] = Ref.Make(goId), ["m_Father"] = Ref.Make(fatherTransform), ["m_Children"] = children,
                    ["m_LocalRotation"] = new Map { ["x"] = F(lq.X), ["y"] = F(lq.Y), ["z"] = F(lq.Z), ["w"] = F(lq.W) },
                    ["m_LocalPosition"] = new Map { ["x"] = F(lp.X), ["y"] = F(lp.Y), ["z"] = F(lp.Z) },
                    ["m_LocalScale"] = new Map { ["x"] = F(ls.X), ["y"] = F(ls.Y), ["z"] = F(ls.Z) },
                });
                if (modelId != 0) modelToTransform[modelId] = trId;

                if (modelId != 0 && Geometry(scene, modelId) is FbxObject geom)
                {
                    var deformers = scene.ChildObjects(geom.Id, "Deformer").Select(d => d.SubType).ToHashSet();
                    var blendShapes = !st.ImportBlendShapes ? new List<FbxObject>() :
                        scene.ChildObjects(geom.Id, "Deformer").Where(d => d.SubType == "BlendShape")
                        .SelectMany(d => scene.ChildObjects(d.Id, "Deformer")).Where(c => c.SubType == "BlendShapeChannel").ToList();
                    if (!st.ImportBlendShapes) deformers.Remove("BlendShape");
                    var meshName = scene.Objects[modelId].Name;   // Mesh 이름 = 원래 FBX 노드 이름
                    var meshRef = Ref.Make(Xxh64.FileId("Mesh", meshName), asset.Guid, 3);
                    file.MeshBlendShapes[Xxh64.FileId("Mesh", meshName)] = blendShapes.Select(b => b.Name).ToList();
                    var mats = MaterialSlots(scene, modelId, geom);
                    if (geometry) meshJobs.Add((Xxh64.FileId("Mesh", meshName), modelId, geom, world));
                    var matRefs = mats.Select(m => (object?)MaterialRef(m, st, asset.Guid)).ToList();
                    for (int k = 0; k < mats.Count; k++)
                        if (Ref.Guid(matRefs[k]) == asset.Guid) file.MaterialNames[Ref.FileId(matRefs[k])] = mats[k];

                    if (deformers.Contains("Skin") || deformers.Contains("BlendShape"))
                    {
                        long id = Xxh64.FileId("SkinnedMeshRenderer", ident + "/SkinnedMeshRenderer");
                        comps.Add(new Map { ["component"] = Ref.Make(id) });
                        var smr = Add(file, 137, "SkinnedMeshRenderer", id, new Map
                        {
                            ["m_GameObject"] = Ref.Make(goId), ["m_Enabled"] = "1", ["m_Mesh"] = meshRef, ["m_Materials"] = matRefs,
                            // 초기 가중치 = FBX BlendShapeChannel 의 DeformPercent
                            ["m_BlendShapeWeights"] = blendShapes.Select(b => (object?)(st.DeformPercent && b.Node.Find("DeformPercent")?.Props[0] is double dp
                                ? dp.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "0")).ToList(),
                        });
                        pendingBones.Add((smr.Props, SkinBones(scene, geom, dfs)));
                    }
                    else
                    {
                        long mf = Xxh64.FileId("MeshFilter", ident + "/MeshFilter"), mr = Xxh64.FileId("MeshRenderer", ident + "/MeshRenderer");
                        comps.Add(new Map { ["component"] = Ref.Make(mf) });
                        comps.Add(new Map { ["component"] = Ref.Make(mr) });
                        Add(file, 33, "MeshFilter", mf, new Map { ["m_GameObject"] = Ref.Make(goId), ["m_Mesh"] = meshRef });
                        Add(file, 23, "MeshRenderer", mr, new Map { ["m_GameObject"] = Ref.Make(goId), ["m_Enabled"] = "1", ["m_Materials"] = matRefs });
                    }
                }

                // 자식: 연결 순서로 이름 확정(형제끼리 겹치면 " 1", " 2"), 배치 순서는 소문자 서수 정렬
                var used = new HashSet<string>();
                var kids = new List<(string name, long id)>();
                foreach (var c in modelId != 0 ? Models(modelId) : top)
                {
                    string b = scene.Objects[c].Name, n = b;
                    for (int k = 1; used.Contains(n); k++) n = b + " " + k;
                    used.Add(n);
                    kids.Add((n, c));
                }
                var ordered = st.SortByName ? kids.OrderBy(k => k.name.ToLowerInvariant(), StringComparer.Ordinal).ToList() : kids;
                foreach (var (n, c) in ordered)
                    children.Add(Ref.Make(Emit(unityPath + "/" + n, c, trId, world)));
                return trId;
            }

            var stem = asset.Name;
            Emit(stem, collapsed ? top[0] : 0, 0, Mat4.Identity());

            // FbxScene은 읽기만 하므로 메시끼리 나눠 만든다. 넣는 순서는 계층 순서 그대로 (출력이 같게)
            var built = new SimMesh[meshJobs.Count];
            System.Threading.Tasks.Parallel.For(0, meshJobs.Count, i =>
            {
                var (_, modelId, geom, world) = meshJobs[i];
                var swMesh = System.Diagnostics.Stopwatch.StartNew();
                built[i] = MeshSim.Build(scene, scene.Objects[modelId], geom, space, meshSettings, dfs, world);
                Timing.Detail("메시 " + scene.Objects[modelId].Name, swMesh.ElapsedMilliseconds);
            });
            for (int i = 0; i < meshJobs.Count; i++) file.Meshes[meshJobs[i].MeshId] = built[i];

            // m_Bones = 클러스터 본(가중치 0 포함)을 FBX 계층 순서로
            // m_RootBone = 자기 아래(자신 포함)에 m_Bones 본이 가장 많은 본. 같으면 m_Bones 순서상 마지막
            foreach (var (smr, bones) in pendingBones)
            {
                var kept = bones.Where(modelToTransform.ContainsKey).ToList();
                smr["m_Bones"] = kept.Select(b => (object?)Ref.Make(modelToTransform[b])).ToList();
                if (kept.Count == 0) { smr["m_RootBone"] = Ref.Make(0); continue; }
                var under = kept.ToDictionary(b => b, _ => 0);
                foreach (var b in kept)
                    for (long a = b; a != 0; a = parentOf.TryGetValue(a, out var pa) ? pa : 0)
                        if (under.ContainsKey(a)) under[a]++;
                int most = under.Values.Max();
                smr["m_RootBone"] = Ref.Make(modelToTransform[kept.Last(b => under[b] == most)]);
            }

            var rootGo = file.Get(Xxh64.FileId("GameObject", "//RootNode/root"))!;
            if (st.AnimationType == 3 || (st.AnimationType == 2 && st.AvatarSetup == 1))
            {
                long id = Xxh64.FileId("Animator", "//RootNode/root/Animator");
                ((List<object?>)rootGo.Props["m_Component"]!).Add(new Map { ["component"] = Ref.Make(id) });
                Add(file, 95, "Animator", id, new Map
                {
                    ["m_GameObject"] = Ref.Make(rootGo.Id), ["m_Enabled"] = "1", ["m_Avatar"] = Ref.Make(AvatarFileId, asset.Guid, 3),
                });
            }
            return file;
        }

        static SimObject Add(SimFile f, int classId, string type, long id, Map props)
        {
            var o = new SimObject { Id = id, ClassId = classId, Type = type, Props = props };
            f.Objects[id] = o;
            return o;
        }

        static string F(double v) => ((float)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        static List<long> SkinBones(FbxScene s, FbxObject geom, IReadOnlyDictionary<long, int> dfs) =>
            s.ChildObjects(geom.Id, "Deformer").Where(d => d.SubType == "Skin")
             .SelectMany(sk => s.ChildObjects(sk.Id, "Deformer").Where(c => c.SubType == "Cluster"))
             .Select(cl => s.ChildObjects(cl.Id, "Model").FirstOrDefault()?.Id ?? 0).Where(id => id != 0)
             .OrderBy(id => dfs.TryGetValue(id, out var o) ? o : int.MaxValue).ToList();

        static FbxObject? Geometry(FbxScene s, long modelId) =>
            s.ChildObjects(modelId, "Geometry").FirstOrDefault(g => g.SubType == "Mesh");

        /// 서브메시 순서 = 폴리곤에서 머티리얼 인덱스가 처음 나오는 순서. 안 쓰는 슬롯은 버린다.
        static List<string> MaterialSlots(FbxScene s, long modelId, FbxObject geom)
        {
            var slots = s.ChildObjects(modelId, "Material").Select(m => m.Name).ToList();
            if (slots.Count == 0) return new List<string> { "Default-Material" };
            var lem = geom.Node.Find("LayerElementMaterial");
            var mapping = lem?.Find("MappingInformationType")?.Props[0] as string;
            if (lem?.Find("Materials")?.Props[0] is not int[] idx || mapping == "AllSame") return slots.GetRange(0, 1);
            var order = new List<int>();
            var seen = new HashSet<int>();
            foreach (var i in idx)
                if (i >= 0 && i < slots.Count && seen.Add(i)) order.Add(i);
            return order.Select(i => slots[i]).ToList();
        }

        static Map MaterialRef(string name, Settings st, string fbxGuid)
        {
            if (name == "Default-Material" || st.MaterialImportMode == 0) return DefaultMaterial;
            if (st.Remapped.TryGetValue(name, out var r)) return r;
            return Ref.Make(Xxh64.FileId("Material", name), fbxGuid, 3);
        }
    }
}
