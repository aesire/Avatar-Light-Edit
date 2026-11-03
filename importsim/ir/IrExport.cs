// 해석된 프리팹 하나 → 에디터 IR v1 장면 하나 (editor/Protocol/Ir.cs).
// 원본 무변환: 값은 Unity가 직렬화한 값(또는 importsim이 Unity 임포트 결과로 재현한 값)을 그대로 옮긴다.
// 출력: <이름>.ir.json, <이름>.ir.bin, 패키지 안 텍스처는 <이름>.textures/<guid>.<확장자> 로 풀어 둔다.
//
// 머티리얼 키 (IR의 materials[].guid, renderers[].materials[]):
//   .mat 에셋은 GUID 그대로. FBX 내장 머티리얼·Default-Material 같은 하위 에셋은 "guid:fileID".
//   에디터는 이 값을 불투명한 문자열로만 비교하므로 IR 버전을 올리지 않아도 된다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const long MatMainFileId = 2100000, TexMainFileId = 2800000, ShaderMainFileId = 4800000, StandardShaderFileId = 46;
        /// 빌트인 셰이더 fileID -> 이름 (guid 0000000000000000f000000000000000)
        static readonly Dictionary<long, string> BuiltinShaders = new Dictionary<long, string> { [46] = "Standard", [45] = "Standard (Specular setup)" };
        static readonly HashSet<string> ImageExt = new HashSet<string> { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".gif", ".exr", ".hdr" };
        static readonly Regex ShaderNameRe = new Regex(@"^\s*Shader\s+""([^""]+)""", RegexOptions.Multiline);

        readonly AssetDb db;
        readonly Dictionary<string, SimFile?> geometry = new Dictionary<string, SimFile?>();   // FBX guid -> 기하 포함 모델
        public readonly List<string> Warnings = new List<string>();

        public IrExport(AssetDb db) { this.db = db; }

        /// <summary>source 항목에 더할 값 (가져오기 재료: sources · root · outfits). 의상을 바꿔 다시 만들 때 쓴다.</summary>
        public JNode? SourceExtra;
        /// <summary>패키지에서 풀어 둘 텍스처 폴더 (outDir 기준). 없으면 &lt;이름&gt;.textures. 의상 조합마다 IR을 따로 쓸 때 같은 폴더를 나눠 쓴다.</summary>
        public string? TextureDir;
        /// <summary>머티리얼이 현재 참조하지 않는 같은 계열의 색상 변형 텍스처도 IR에 넣는다.</summary>
        public bool IncludeColorVariants;

        /// <summary>prefab: Resolver 결과 (프리팹 또는 씬). root: 씬에서 내보낼 루트 (없으면 첫 루트). 반환: 쓴 .ir.json 경로</summary>
        /// project: 원본 Unity 프로젝트의 Assets 폴더 (에디터가 셰이더 원본을 다시 찾을 때 쓴다. IR v1 밖의 추가 항목이라 Raw에 남는다)
        public string Write(SimFile prefab, string outDir, string name, SimObject? root = null, string? project = null)
        {
            Directory.CreateDirectory(outDir);
            root ??= prefab.Root ?? throw new InvalidOperationException("루트 GameObject 없음");
            string jsonPath = Path.Combine(outDir, name + ".ir.json"), binName = name + ".ir.bin";

            var nodes = JNode.Arr(); var meshes = JNode.Arr(); var renderers = JNode.Arr();
            var trToNode = new Dictionary<long, int>();
            var nodeGos = new List<SimObject>();
            // 아바타 노드가 먼저, Resolver.Compose로 넣은 의상 노드는 뒤에 (의상을 더하고 빼도 아바타 노드 번호가 그대로)
            AddNode(prefab, root, -1, nodes, trToNode, nodeGos, go => !prefab.AddedFrom.ContainsKey(go.Id));
            if (prefab.AddedFrom.Count > 0) AddLate(prefab, root, nodes, trToNode, nodeGos);
            Timing.Step("IR: 노드");

            _nodes = nodes; _world.Clear();
            var meshInfo = new Dictionary<string, SimMesh?>();   // mesh id -> 메시 (없으면 null)
            var matKeys = new List<(string key, object? reference)>();
            PrebuildModels(prefab, nodeGos);
            Timing.Step("IR: FBX 해석 (병렬)");
            using (var w = new IrBufferWriter(Path.Combine(outDir, binName)))
            {
                for (int id = 0; id < nodeGos.Count; id++)
                {
                    var go = nodeGos[id];
                    var comps = Components(prefab, go);
                    var mf = comps.FirstOrDefault(c => c.ClassId == 33);
                    foreach (var c in comps)
                    {
                        if (c.ClassId != 137 && c.ClassId != 23) continue;
                        var meshRef = c.ClassId == 137 ? c.Props.GetValueOrDefault("m_Mesh") : mf?.Props.GetValueOrDefault("m_Mesh");
                        string meshId = "";
                        SimMesh? mesh = null;
                        if (Ref.FileId(meshRef) != 0)
                        {
                            meshId = Ref.Guid(meshRef) + ":" + Ref.FileId(meshRef);
                            if (!meshInfo.TryGetValue(meshId, out mesh))
                            {
                                mesh = FindMesh(meshRef);
                                meshInfo[meshId] = mesh;
                                if (mesh != null) meshes.Add(MeshJson(w, meshId, mesh, meshRef));
                            }
                        }
                        else if (c.ClassId == 23 && mf == null) Warnings.Add($"{prefab.Path}: {Name(go)}: MeshFilter 없음");
                        renderers.Add(RendererJson(prefab, c, id, meshId, mesh, trToNode, matKeys));
                    }
                }
            }

            Timing.Step("IR: 메시·렌더러");
            // 머티리얼 → 텍스처 (텍스처·셰이더 본문은 패키지마다 한 번에 읽는다)
            var mats = JNode.Arr();
            var texGuids = new List<string>();
            var shaderAssets = new Dictionary<string, AssetEntry>();
            var matNodes = new List<(JNode node, string shaderGuid)>();
            foreach (var (key, reference) in matKeys)
            {
                var m = MaterialJson(key, reference, texGuids);
                if (m == null) continue;
                mats.Add(m);
                var sg = m["shaderGuid"].AsString;
                if (!m.Has("shaderName") && db.Get(sg) is AssetEntry sa && sa.Ext == ".shader") { shaderAssets[sg] = sa; matNodes.Add((m, sg)); }
            }
            var shaderNames = new Dictionary<string, string>();
            db.ReadEach(shaderAssets.Values, (a, data) =>
            {
                var sm = ShaderNameRe.Match(Encoding.UTF8.GetString(data));
                if (sm.Success) shaderNames[a.Guid] = sm.Groups[1].Value;
            });
            foreach (var (node, sg) in matNodes) if (shaderNames.TryGetValue(sg, out var sn)) node.Set("shaderName", sn);
            Timing.Step("IR: 머티리얼·셰이더");

            var texs = TexturesJson(texGuids, outDir, name);
            Timing.Step("IR: 텍스처");
            var (physBones, physColliders) = PhysBonesJson(prefab, nodeGos, trToNode);
            IgnoreAttachedBones(physBones, nodes, trToNode);
            Timing.Step("IR: PhysBone");
            var humanoid = HumanoidJson(prefab, nodeGos, nodes);
            Timing.Step("IR: 휴머노이드");
            var outfitsJ = OutfitsJson(nodeGos);
            var rulesJ = OutfitRulesJson(prefab, root, nodeGos);
            Timing.Step("IR: 의상 정보·규칙");

            var json = JNode.Obj().Set("irVersion", IrScene.CurrentVersion).Set("name", Name(root)).Set("buffers", binName)
                .Set("generator", "importsim")
                .Set("source", SourceJson(prefab, project))
                .Set("nodes", nodes).Set("meshes", meshes).Set("renderers", renderers).Set("materials", mats).Set("textures", texs)
                .Set("physBones", physBones).Set("physBoneColliders", physColliders)
                .Set("humanoid", humanoid)
                .Set("outfits", outfitsJ)
                .Set("outfitRules", rulesJ);
            if (_humanSettings != null) json.Set("humanoidSettings", _humanSettings);
            if (ViewPositionJson(prefab, root) is JNode vpos) json.Set("viewPosition", vpos);
            var expressions = ExpressionsJson(prefab, root);
            if (expressions.Count > 0) json.Set("expressions", expressions);
            Timing.Step("IR: 표정");
            var faceTracking = FaceTrackingJson(prefab, root, nodeGos);
            if (faceTracking.Count > 0) json.Set("faceTracking", faceTracking);
            Timing.Step("IR: 페이셜 트래킹 애니메이터");
            var menuShapes = MenuShapesJson(prefab, root);
            if (menuShapes.Count > 0) json.Set("menuShapes", menuShapes);
            Timing.Step("IR: 기능 메뉴");
            json.Set("gimmicks", GimmicksJson(prefab, root, nodeGos, project));
            Timing.Step("IR: 기믹 찾기");
            var blinkFix = BlinkFixJson(prefab, nodeGos);
            if (blinkFix.Count > 0) json.Set("existingBlinkFix", blinkFix);
            Timing.Step("IR: Blink Fix");
            var warns = JNode.Arr();
            foreach (var s in prefab.Warnings.Concat(Warnings)) warns.Add(s);
            json.Set("warnings", warns);
            File.WriteAllText(jsonPath, json.ToJson(indent: true), new UTF8Encoding(false));
            Timing.Step("IR: JSON 쓰기");
            return jsonPath;
        }

        // ---------------- 노드 ----------------

        JNode SourceJson(SimFile f, string? project)
        {
            var j = JNode.Obj().Set("guid", f.Guid).Set("path", f.Path).Set("project", project);
            if (SourceExtra != null) foreach (var kv in SourceExtra.Props) j.Set(kv.Key, kv.Value);
            return j;
        }

        static void AddNode(SimFile f, SimObject go, int parent, JNode nodes, Dictionary<long, int> trToNode, List<SimObject> gos, Func<SimObject, bool>? include = null)
        {
            if (include != null && !include(go)) return;
            int id = gos.Count;
            gos.Add(go);
            var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
            var n = JNode.Obj().Set("id", id).Set("name", Name(go)).Set("parent", parent);
            if (tr != null)
            {
                trToNode[tr.Id] = id;
                n.Set("position", JNode.FloatArray(Vec(tr.Props, "m_LocalPosition", "x", "y", "z")))
                 .Set("rotation", JNode.FloatArray(Vec(tr.Props, "m_LocalRotation", "x", "y", "z", "w")))
                 .Set("scale", JNode.FloatArray(Vec(tr.Props, "m_LocalScale", "x", "y", "z")));
            }
            if (S(go.Props, "m_TagString") == "EditorOnly") n.Set("editorOnly", true);   // VRChat 업로드에서 빠진다 (위치 확인용 더미 등)
            n.Set("active", S(go.Props, "m_IsActive") != "0")
             .Set("source", new SourceRef { Guid = f.Guid, FileId = go.Id }.ToJson());
            nodes.Add(n);
            if (tr == null) return;
            foreach (var ch in L(tr.Props, "m_Children"))
                if (f.Get(Ref.FileId(ch)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                    AddNode(f, cg, id, nodes, trToNode, gos, include);
        }

        /// 첫 번째 순회에서 뺀 노드(의상)를 계층 순서대로 뒤에 붙인다. 부모는 이미 번호가 있다 (아바타 본 또는 앞서 붙인 의상 노드)
        static void AddLate(SimFile f, SimObject go, JNode nodes, Dictionary<long, int> trToNode, List<SimObject> gos)
        {
            var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
            if (tr == null) return;
            foreach (var ch in L(tr.Props, "m_Children"))
                if (f.Get(Ref.FileId(ch)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                {
                    if (trToNode.ContainsKey(ct.Id)) AddLate(f, cg, nodes, trToNode, gos);
                    else AddNode(f, cg, trToNode[tr.Id], nodes, trToNode, gos);
                }
        }

        // ---------------- 메시 ----------------

        /// <summary>렌더러가 쓰는 FBX를 미리 병렬로 해석한다 (FBX 하나 해석이 가져오기에서 가장 오래 걸리는 일, 서로 독립).
        /// 결과는 AssetDb.Geometry에 남고, 실패는 FindMesh가 경고로 남긴다.</summary>
        void PrebuildModels(SimFile prefab, List<SimObject> nodeGos)
        {
            var want = new HashSet<string>();
            foreach (var go in nodeGos)
                foreach (var c in Components(prefab, go))
                {
                    if (c.ClassId != 137 && c.ClassId != 33) continue;
                    var g = Ref.Guid(c.Props.GetValueOrDefault("m_Mesh"));
                    if (g != null && !geometry.ContainsKey(g) && !assetMeshes.ContainsKey(g) && db.Get(g) is AssetEntry a && (a.Ext == ".fbx" || a.Ext == ".asset")) want.Add(g);
                }
            if (want.Count == 0) return;
            System.Threading.Tasks.Parallel.ForEach(want, g =>
            {
                var a = db.Get(g)!;
                if (a.Ext == ".fbx") db.Geometry(a);
                else ParseMeshAsset(a);
            });
        }

        /// 메시 에셋(.asset) guid → (fileID → 메시, 읽지 못한 이유). 경고는 FindMesh가 처음 쓸 때 한 번 남긴다
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Dictionary<long, SimMesh> Meshes, List<string> Errors)> assetMeshes =
            new System.Collections.Concurrent.ConcurrentDictionary<string, (Dictionary<long, SimMesh>, List<string>)>();
        readonly HashSet<string> assetMeshWarned = new HashSet<string>();

        (Dictionary<long, SimMesh> Meshes, List<string> Errors) ParseMeshAsset(AssetEntry a) => assetMeshes.GetOrAdd(a.Guid, _ =>
        {
            var errors = new List<string>();
            var meshes = MeshAsset.Parse(a.Data ?? Array.Empty<byte>(), errors);
            return (meshes, errors);
        });

        SimMesh? FindMesh(object? meshRef)
        {
            var g = Ref.Guid(meshRef);
            var a = g != null ? db.Get(g) : null;
            if (a == null) { Warnings.Add($"메시 에셋 없음: {Ref.Format(meshRef)}"); return null; }
            if (a.Ext == ".asset")
            {
                var (meshes, errors) = ParseMeshAsset(a);
                if (assetMeshWarned.Add(a.Guid)) foreach (var e in errors) Warnings.Add($"{a.Path}: {e}");
                if (meshes.TryGetValue(Ref.FileId(meshRef), out var am)) return am;
                if (errors.Count == 0) Warnings.Add($"{a.Path}: 메시 fileID {Ref.FileId(meshRef)} 없음");
                return null;
            }
            if (a.Ext != ".fbx") { Warnings.Add($"지원하지 않는 메시 에셋 ({a.Ext}): {a.Path}"); return null; }
            if (!geometry.TryGetValue(g!, out var model))
            {
                var (built, err) = db.Geometry(a);
                if (err != null) Warnings.Add($"{a.Path}: 기하 해석 실패: {err}");
                model = built;
                geometry[g!] = model;
            }
            if (model != null && model.Meshes.TryGetValue(Ref.FileId(meshRef), out var m)) return m;
            if (model != null) Warnings.Add($"{a.Path}: 메시 fileID {Ref.FileId(meshRef)} 없음");
            return null;
        }

        static JNode MeshJson(IrBufferWriter w, string id, SimMesh m, object? meshRef)
        {
            int n = m.Vertices.Count;
            var pos = V3(m.Vertices);
            var j = JNode.Obj().Set("id", id).Set("name", m.Name)
                .Set("source", new SourceRef { Guid = Ref.Guid(meshRef) ?? "", FileId = Ref.FileId(meshRef) }.ToJson())
                .Set("vertexCount", n)
                .Set("positions", w.Write(pos).ToJson());
            if (m.Normals.Count == n) j.Set("normals", w.Write(V3(m.Normals)).ToJson());
            if (m.Tangents != null && m.Tangents.Count == n) j.Set("tangents", w.Write(m.Tangents.SelectMany(t => t).ToArray()).ToJson());
            if (m.Colors != null && m.Colors.Count == n) j.Set("colors", w.Write(m.Colors.SelectMany(c => c.Select(x => (float)x)).ToArray()).ToJson());
            for (int ch = 0; ch < m.Uvs.Count && ch < 4; ch++)
                if (m.Uvs[ch].Count == n)
                    j.Set("uv" + ch, w.Write(m.Uvs[ch].SelectMany(uv => new[] { (float)uv.u, (float)uv.v }).ToArray()).ToJson().Set("dim", 2));
            if (m.BoneIndices != null && m.BoneWeights != null && m.BoneIndices.Count == n)
            {
                j.Set("boneIndices", w.Write(m.BoneIndices.SelectMany(b => b.Take(4).Concat(Enumerable.Repeat(0, Math.Max(0, 4 - b.Length)))).ToArray()).ToJson());
                j.Set("boneWeights", w.Write(m.BoneWeights.SelectMany(b => b.Take(4).Select(x => (float)x).Concat(Enumerable.Repeat(0f, Math.Max(0, 4 - b.Length)))).ToArray()).ToJson());
            }
            if (m.Bindposes.Count > 0)
                j.Set("bindposes", w.Write(m.Bindposes.SelectMany(p => Enumerable.Range(0, 16).Select(i => (float)p.M[i % 4, i / 4])).ToArray()).ToJson());

            var subs = JNode.Arr();
            foreach (var s in m.Submeshes) subs.Add(JNode.Obj().Set("indices", w.Write(s.ToArray()).ToJson()).Set("topology", "Triangles"));
            j.Set("submeshes", subs);

            var shapes = JNode.Arr();
            foreach (var bs in m.BlendShapes)
            {
                var frames = JNode.Arr();
                foreach (var fr in bs.Frames)
                {
                    var f = JNode.Obj().Set("weight", fr.Weight);
                    // IR은 정점 수 × 3 의 조밀 배열. 델타가 하나도 없는 채널은 비운다 (Unity AddBlendShapeFrame 의 null 과 같음)
                    if (fr.DeltaVertices.Count > 0) f.Set("dPos", w.Write(Dense(fr.DeltaVertices, n)).ToJson());
                    if (fr.DeltaNormals.Count > 0) f.Set("dNormal", w.Write(Dense(fr.DeltaNormals, n)).ToJson());
                    if (fr.DeltaTangents.Count > 0) f.Set("dTangent", w.Write(Dense(fr.DeltaTangents, n)).ToJson());
                    frames.Add(f);
                }
                shapes.Add(JNode.Obj().Set("name", bs.Name).Set("frames", frames));
            }
            j.Set("blendShapes", shapes);

            // Unity Mesh.bounds = 정점 AABB
            if (n > 0)
            {
                float[] lo = { pos[0], pos[1], pos[2] }, hi = { pos[0], pos[1], pos[2] };
                for (int i = 0; i < pos.Length; i++) { lo[i % 3] = Math.Min(lo[i % 3], pos[i]); hi[i % 3] = Math.Max(hi[i % 3], pos[i]); }
                j.Set("bounds", JNode.Obj()
                    .Set("center", JNode.FloatArray(Enumerable.Range(0, 3).Select(k => (lo[k] + hi[k]) * 0.5f)))
                    .Set("extents", JNode.FloatArray(Enumerable.Range(0, 3).Select(k => (hi[k] - lo[k]) * 0.5f))));
            }
            return j;
        }

        static float[] Dense(Dictionary<int, Vec3> d, int n)
        {
            var r = new float[n * 3];
            foreach (var kv in d)
                if (kv.Key >= 0 && kv.Key < n) { r[kv.Key * 3] = (float)kv.Value.X; r[kv.Key * 3 + 1] = (float)kv.Value.Y; r[kv.Key * 3 + 2] = (float)kv.Value.Z; }
            return r;
        }

        static float[] V3(List<Vec3> v)
        {
            var r = new float[v.Count * 3];
            for (int i = 0; i < v.Count; i++) { r[i * 3] = (float)v[i].X; r[i * 3 + 1] = (float)v[i].Y; r[i * 3 + 2] = (float)v[i].Z; }
            return r;
        }

        // ---------------- 렌더러 ----------------

        JNode RendererJson(SimFile f, SimObject c, int node, string meshId, SimMesh? mesh, Dictionary<long, int> trToNode, List<(string, object?)> matKeys)
        {
            bool smr = c.ClassId == 137;
            int NodeOf(object? r) => Ref.Guid(r) == null && trToNode.TryGetValue(Ref.FileId(r), out var id) ? id : -1;
            var j = JNode.Obj().Set("id", c.Id.ToString(C)).Set("node", node).Set("type", smr ? "SkinnedMeshRenderer" : "MeshRenderer")
                .Set("mesh", meshId);
            if (smr)
            {
                var bones = JNode.Arr();
                foreach (var b in L(c.Props, "m_Bones")) bones.Add(NodeOf(b));
                j.Set("bones", bones).Set("rootBone", Ref.FileId(c.Props.GetValueOrDefault("m_RootBone")) == 0 ? -1 : NodeOf(c.Props.GetValueOrDefault("m_RootBone")));
            }
            var mats = JNode.Arr();
            foreach (var m in L(c.Props, "m_Materials"))
            {
                var key = MaterialKey(m);
                mats.Add(key);
                if (key.Length > 0 && !matKeys.Any(k => k.Item1 == key)) matKeys.Add((key, m));
            }
            j.Set("materials", mats);
            if (smr)
            {
                // Unity는 가중치 배열을 메시의 블렌드쉐이프 개수에 맞춘다 (모자라면 0)
                var weights = L(c.Props, "m_BlendShapeWeights").Select(x => P(x as string)).ToList();
                int count = mesh?.BlendShapes.Count ?? weights.Count;
                j.Set("blendShapeWeights", JNode.FloatArray(Enumerable.Range(0, count).Select(i => i < weights.Count ? weights[i] : 0f)));
                if (c.Props.GetValueOrDefault("m_AABB") is Map aabb)
                    j.Set("bounds", JNode.Obj().Set("center", JNode.FloatArray(Vec(aabb, "m_Center", "x", "y", "z")))
                                               .Set("extents", JNode.FloatArray(Vec(aabb, "m_Extent", "x", "y", "z"))));
                else if (mesh != null && SkinnedBounds(mesh, j["bones"], j["rootBone"].AsInt, node) is JNode sb) j.Set("bounds", sb);
            }
            j.Set("enabled", S(c.Props, "m_Enabled") != "0");
            return j;
        }

        /// <summary>FBX에서 바로 온 SkinnedMeshRenderer는 프리팹 YAML에 m_AABB가 없다 (Unity가 임포트할 때 계산해 모델 안에 둔다).
        /// Unity처럼 뼈마다 그 뼈가 움직이는 정점(가중치 &gt; 0)의 범위 상자를 바인드 자세 뼈 공간에서 재고(Mesh m_BonesAABB),
        /// 상자 꼭짓점 8개를 임포트 자세(노드 Transform 값)의 뼈로 옮겨 루트 뼈(없으면 렌더러 노드) 공간에서 합친다.
        /// 뼈가 없는 메시는 정점 범위를 그대로 옮긴다. 블렌드쉐이프로 옮긴 정점도 넣는다.
        /// Unity 정답(tests/unity/GeometryDumper.cs localBounds, 실제 아바타 FBX 11개·렌더러 225개)과 비교 (2026-10-23):
        /// 블렌드쉐이프 없는 메시 85개 중 84개 일치(1e-4), 전체 201개가 5% 안. 블렌드쉐이프가 있으면 Unity가 조금 더 크게 잡는다 (규칙 못 찾음).
        /// 미리보기 렌더러는 updateWhenOffscreen이라 이 값을 쓰지 않는다 — IR을 Unity 값에 가깝게 두는 용도.</summary>
        JNode? SkinnedBounds(SimMesh mesh, JNode bones, int rootBone, int node)
        {
            int n = mesh.Vertices.Count;
            if (n == 0 || _nodes == null) return null;
            var toRoot = World(rootBone >= 0 ? rootBone : node).Inverse();
            int nb = bones.Count;
            // 정점과 블렌드쉐이프 프레임마다 옮긴 정점
            var pts = new List<(int v, Vec3 p)>(n);
            for (int v = 0; v < n; v++) pts.Add((v, mesh.Vertices[v]));
            foreach (var bs in mesh.BlendShapes) foreach (var fr in bs.Frames) foreach (var kv in fr.DeltaVertices) if (kv.Key < n) pts.Add((kv.Key, mesh.Vertices[kv.Key] + kv.Value));

            Vec3 lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), hi = -lo;
            void Add(Vec3 q) { lo = new Vec3(Math.Min(lo.X, q.X), Math.Min(lo.Y, q.Y), Math.Min(lo.Z, q.Z)); hi = new Vec3(Math.Max(hi.X, q.X), Math.Max(hi.Y, q.Y), Math.Max(hi.Z, q.Z)); }
            if (mesh.BoneIndices == null || mesh.BoneWeights == null || nb == 0)
            {
                var m = toRoot * World(node);
                foreach (var (_, p) in pts) Add(m.MulPoint(p));
            }
            else
            {
                var blo = new Vec3[nb]; var bhi = new Vec3[nb]; var used = new bool[nb];
                foreach (var (v, p) in pts)
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = mesh.BoneIndices[v][k];
                        if (mesh.BoneWeights[v][k] <= 0 || bi < 0 || bi >= nb || bi >= mesh.Bindposes.Count) continue;
                        var q = mesh.Bindposes[bi].MulPoint(p);
                        if (!used[bi]) { used[bi] = true; blo[bi] = bhi[bi] = q; }
                        blo[bi] = new Vec3(Math.Min(blo[bi].X, q.X), Math.Min(blo[bi].Y, q.Y), Math.Min(blo[bi].Z, q.Z));
                        bhi[bi] = new Vec3(Math.Max(bhi[bi].X, q.X), Math.Max(bhi[bi].Y, q.Y), Math.Max(bhi[bi].Z, q.Z));
                    }
                for (int b = 0; b < nb; b++)
                {
                    if (!used[b]) continue;
                    int bn = bones[b].AsInt;
                    var m = toRoot * (bn >= 0 ? World(bn) : World(node));
                    for (int c = 0; c < 8; c++)
                        Add(m.MulPoint(new Vec3((c & 1) == 0 ? blo[b].X : bhi[b].X, (c & 2) == 0 ? blo[b].Y : bhi[b].Y, (c & 4) == 0 ? blo[b].Z : bhi[b].Z)));
                }
            }
            if (lo.X > hi.X) return null;
            var center = (lo + hi) * 0.5; var ext = (hi - lo) * 0.5;
            return JNode.Obj().Set("center", JNode.FloatArray(new[] { (float)center.X, (float)center.Y, (float)center.Z }))
                              .Set("extents", JNode.FloatArray(new[] { (float)ext.X, (float)ext.Y, (float)ext.Z }));
        }

        JNode? _nodes;
        readonly Dictionary<int, Mat4> _world = new Dictionary<int, Mat4>();

        /// IR 노드의 월드 행렬 (Transform 값 그대로, 부모부터)
        Mat4 World(int id)
        {
            if (_world.TryGetValue(id, out var w)) return w;
            var nd = _nodes![id];
            float[] F(string k, params float[] def) => nd.Has(k) ? Enumerable.Range(0, def.Length).Select(i => (float)nd[k][i].AsDouble).ToArray() : def;
            var t = F("position", 0, 0, 0); var r = F("rotation", 0, 0, 0, 1); var s = F("scale", 1, 1, 1);
            var local = Mat4.TRS(new Vec3(t[0], t[1], t[2]), new Quat(r[0], r[1], r[2], r[3]), new Vec3(s[0], s[1], s[2]));
            int parent = nd["parent"].AsInt;
            return _world[id] = parent >= 0 ? World(parent) * local : local;
        }

        static string MaterialKey(object? r)
        {
            var g = Ref.Guid(r);
            long id = Ref.FileId(r);
            if (g == null || id == 0) return "";
            return id == MatMainFileId ? g : g + ":" + id.ToString(C);
        }

        // ---------------- 머티리얼 ----------------

        JNode? MaterialJson(string key, object? reference, List<string> texGuids)
        {
            var g = Ref.Guid(reference)!;
            long id = Ref.FileId(reference);
            var a = db.Get(g);
            if (Ref.IsBuiltin(g) || a?.Ext == ".fbx")
            {
                // FBX 내장 머티리얼과 Default-Material: Unity는 Standard 셰이더로 만든다. 내장 머티리얼은 FBX 머티리얼 속성으로 값을 채운다 (IrFbxMaterials.cs).
                string name = Ref.IsBuiltin(g) ? (id == 10303 ? "Default-Material" : $"builtin {id}")
                            : geometry.TryGetValue(g, out var model) && model != null && model.MaterialNames.TryGetValue(id, out var n) ? n
                            : MaterialNameFromModel(a!, id);
                var jm = JNode.Obj().Set("guid", key).Set("path", a?.Path ?? "Resources/unity_builtin_extra").Set("name", name)
                    .Set("shaderGuid", Ref.BuiltinExtraGuid + ":" + StandardShaderFileId).Set("shaderName", "Standard")
                    .Set("texEnvs", JNode.Obj()).Set("floats", JNode.Obj()).Set("colors", JNode.Obj()).Set("keywords", JNode.Arr()).Set("renderQueue", -1);
                if (!Ref.IsBuiltin(g) && !FbxMaterial(a!, name, id, jm, texGuids))
                    Warnings.Add($"FBX 내장 머티리얼 '{name}'을 FBX에서 찾지 못함: Standard 기본값으로 둠");
                return jm;
            }
            if (a == null) { Warnings.Add($"머티리얼 에셋 없음: {g}"); return null; }
            // .asset 머티리얼: UniVRM 등이 Material 하나를 .asset으로 저장한다 (본문 형식은 .mat과 같음)
            if ((a.Ext != ".mat" && a.Ext != ".asset") || id != MatMainFileId) { Warnings.Add($"지원하지 않는 머티리얼 참조: {a.Path} ({id})"); return null; }

            var text = Encoding.UTF8.GetString(a.Data ?? Array.Empty<byte>());
            var mat = new Resolved();
            if (!LoadMat(a, mat, new HashSet<string>())) { Warnings.Add($"머티리얼 해석 실패: {a.Path}"); return null; }

            var texEnvs = JNode.Obj();
            foreach (var kv in mat.TexEnvs)
            {
                var te = kv.Value;
                var tr = te.GetValueOrDefault("m_Texture");
                var tg = Ref.Guid(tr);
                string? tex = null;
                if (tg != null && Ref.FileId(tr) != 0)
                {
                    tex = Ref.FileId(tr) == TexMainFileId ? tg : tg + ":" + Ref.FileId(tr).ToString(C);
                    if (!texGuids.Contains(tex)) texGuids.Add(tex);
                }
                texEnvs.Set(kv.Key, JNode.Obj().Set("texture", tex)
                    .Set("scale", JNode.FloatArray(Vec(te, "m_Scale", "x", "y")))
                    .Set("offset", JNode.FloatArray(Vec(te, "m_Offset", "x", "y"))));
            }
            var floats = JNode.Obj(); foreach (var kv in mat.Floats) floats.Set(kv.Key, kv.Value);
            var colors = JNode.Obj(); foreach (var kv in mat.Colors) colors.Set(kv.Key, JNode.FloatArray(kv.Value));
            var kws = JNode.Arr(); foreach (var k in mat.Keywords) kws.Add(k);

            var j = JNode.Obj().Set("guid", key).Set("path", a.Path).Set("name", mat.Name)
                .Set("shaderGuid", ShaderKey(mat.Shader));
            if (Ref.IsBuiltin(Ref.Guid(mat.Shader)) && BuiltinShaders.TryGetValue(Ref.FileId(mat.Shader), out var bsn)) j.Set("shaderName", bsn);
            return j.Set("texEnvs", texEnvs).Set("floats", floats).Set("colors", colors).Set("keywords", kws)
                .Set("renderQueue", mat.RenderQueue).Set("rawYaml", text);
        }

        /// 셰이더 키: .shader 에셋은 GUID, 빌트인처럼 한 GUID에 여럿이 있는 것은 "guid:fileID" (렌더러 셰이더 표와 같은 규칙)
        static string ShaderKey(object? r)
        {
            var g = Ref.Guid(r) ?? "";
            long id = Ref.FileId(r);
            return g.Length == 0 || id == ShaderMainFileId || id == 0 ? g : g + ":" + id.ToString(C);
        }

        static string MaterialNameFromModel(AssetEntry fbx, long id) => $"{fbx.Name} 내장 머티리얼 {id}";

        sealed class Resolved
        {
            public string Name = "";
            public object? Shader;
            public Dictionary<string, Map> TexEnvs = new Dictionary<string, Map>();
            public Dictionary<string, float> Floats = new Dictionary<string, float>();
            public Dictionary<string, float[]> Colors = new Dictionary<string, float[]>();
            public List<string> Keywords = new List<string>();
            public int RenderQueue = -1;
        }

        /// .mat 을 읽어 mat 에 덮어쓴다. 머티리얼 베리언트(m_Parent)는 부모 값을 먼저 깐다.
        bool LoadMat(AssetEntry a, Resolved mat, HashSet<string> seen)
        {
            if (!seen.Add(a.Guid)) return false;
            var doc = Yaml.ParseDocuments(Encoding.UTF8.GetString(a.Data ?? Array.Empty<byte>())).FirstOrDefault(d => d.ClassId == 21);
            if (doc == null) return false;
            var b = doc.Body;
            var parent = b.GetValueOrDefault("m_Parent");
            if (Ref.FileId(parent) != 0 && Ref.Guid(parent) is string pg)
            {
                if (db.Get(pg) is AssetEntry pa && (pa.Ext == ".mat" || pa.Ext == ".asset")) LoadMat(pa, mat, seen);
                else Warnings.Add($"{a.Path}: 부모 머티리얼 없음 ({pg})");
            }
            mat.Name = S(b, "m_Name");
            if (Ref.FileId(b.GetValueOrDefault("m_Shader")) != 0 || mat.Shader == null) mat.Shader = b.GetValueOrDefault("m_Shader");
            if (b.ContainsKey("m_CustomRenderQueue")) mat.RenderQueue = (int)P(S(b, "m_CustomRenderQueue"), -1);

            // 키워드: 2022 은 m_ValidKeywords (목록), 구버전은 m_ShaderKeywords (공백 구분 문자열)
            List<string> kws;
            if (b.TryGetValue("m_ValidKeywords", out var vk)) kws = (vk as List<object?> ?? new List<object?>()).OfType<string>().ToList();
            else kws = S(b, "m_ShaderKeywords").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (kws.Count > 0 || Ref.FileId(parent) == 0) mat.Keywords = kws;

            var sp = b.GetValueOrDefault("m_SavedProperties") as Map ?? new Map();
            foreach (var (k, v) in Entries(sp, "m_TexEnvs")) if (v is Map m) mat.TexEnvs[k] = m;
            // m_Ints(2021.1+ Integer 프로퍼티)도 숫자 값 그대로 floats 에 둔다 (IR v1에 정수 칸이 없음)
            foreach (var (k, v) in Entries(sp, "m_Ints")) mat.Floats[k] = P(v as string);
            foreach (var (k, v) in Entries(sp, "m_Floats")) mat.Floats[k] = P(v as string);
            foreach (var (k, v) in Entries(sp, "m_Colors")) if (v is Map m) mat.Colors[k] = Vec(m, null, "r", "g", "b", "a");
            return true;
        }

        /// m_SavedProperties 목록: serializedVersion 3 은 "- 이름: 값", 2 는 "- first: {name: 이름} second: 값"
        static IEnumerable<(string, object?)> Entries(Map sp, string key)
        {
            foreach (var e in L(sp, key))
            {
                if (e is not Map m) continue;
                if (m.TryGetValue("first", out var first) && first is Map fm && m.ContainsKey("second"))
                    yield return (S(fm, "name"), m["second"]);
                else foreach (var kv in m) yield return (kv.Key, kv.Value);
            }
        }

        // ---------------- 텍스처 ----------------

        JNode TexturesJson(List<string> keys, string outDir, string name)
        {
            var arr = JNode.Arr();
            var allKeys = new List<string>(keys);
            if (IncludeColorVariants) AddColorVariants(allKeys);
            var extract = new Dictionary<string, (AssetEntry a, JNode node)>();
            foreach (var key in allKeys)
            {
                var g = key.Split(':')[0];
                var a = db.Get(g);
                if (Ref.IsBuiltin(g)) { Warnings.Add($"빌트인 텍스처 참조는 지원하지 않음: {key}"); continue; }
                if (a == null) { Warnings.Add($"텍스처 에셋 없음: {key}"); continue; }
                if (!ImageExt.Contains(a.Ext)) { Warnings.Add($"이미지가 아닌 텍스처 에셋 ({a.Ext}): {a.Path}"); continue; }
                if (key != g) Warnings.Add($"텍스처 하위 에셋 참조 ({key}): 원본 이미지 전체로 둠 ({a.Path})");
                var t = JNode.Obj().Set("guid", key).Set("path", a.Path).Set("importer", Importer(a));
                if (a.FilePath != null) t.Set("file", a.FilePath);
                else
                {
                    // 패키지 안 텍스처: 원본 바이트를 그대로 풀어 둔다. 파일 이름은 guid + 확인한 확장자만 쓴다
                    var rel = (TextureDir ?? name + ".textures") + "/" + g + a.Ext;
                    t.Set("file", rel);
                    extract[g] = (a, t);
                }
                arr.Add(t);
            }
            if (extract.Count > 0)
            {
                var dir = Path.Combine(outDir, TextureDir ?? name + ".textures");
                Directory.CreateDirectory(dir);
                var done = new HashSet<string>();
                db.ReadEach(extract.Values.Select(x => x.a).Distinct(), (a, data) =>
                {
                    var file = Path.Combine(dir, a.Guid + a.Ext);
                    // 다른 의상 조합의 IR이 이미 풀어 둔 같은 파일이면 그대로 둔다 (열려 있을 수 있다)
                    if (!File.Exists(file) || new FileInfo(file).Length != data.Length) File.WriteAllBytes(file, data);
                    done.Add(a.Guid);
                });
                foreach (var kv in extract) if (!done.Contains(kv.Key)) Warnings.Add($"텍스처를 패키지에서 꺼내지 못함: {kv.Value.a.Path}");
            }
            return arr;
        }

        static readonly string[] ColorWords =
        {
            "red", "blue", "navy", "cyan", "aqua", "teal", "green", "lime", "yellow", "gold", "orange",
            "pink", "magenta", "purple", "violet", "lavender", "brown", "beige", "cream", "white", "ivory",
            "black", "gray", "grey", "silver", "maroon", "wine", "color", "colour", "カラー", "색", "검정", "흰색", "빨강", "파랑", "노랑", "초록", "보라", "분홍", "갈색", "회색",
            "赤", "青", "黒", "白", "緑", "黄", "紫", "茶", "灰", "ピンク", "ブルー", "レッド", "ブラック", "ホワイト"
        };

        void AddColorVariants(List<string> keys)
        {
            var seen = new HashSet<string>(keys.Select(k => k.Split(':')[0]), StringComparer.OrdinalIgnoreCase);
            // 계열(색 낱말을 뺀 파일 이름) → 쓰는 텍스처가 든 폴더 묶음. 같은 이름의 다른 패키지 텍스처(Body_red 등)는 들이지 않는다
            var families = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in keys.Select(k => db.Get(k.Split(':')[0])))
            {
                if (a == null || !ImageExt.Contains(a.Ext)) continue;
                var fam = TextureFamily(a.Path);
                var home = TextureHome(a.Path);
                if (fam.Length == 0 || home.Length == 0) continue;
                if (!families.TryGetValue(fam, out var homes)) families[fam] = homes = new List<string>();
                if (!homes.Contains(home, StringComparer.OrdinalIgnoreCase)) homes.Add(home);
            }
            if (families.Count == 0) return;
            foreach (var a in db.ByGuid.Values)
            {
                if (!ImageExt.Contains(a.Ext) || seen.Contains(a.Guid) || !HasColorWord(a.Path)) continue;
                if (!families.TryGetValue(TextureFamily(a.Path), out var homes)) continue;
                var p = a.Path.Replace('\\', '/');
                if (!homes.Any(h => p.StartsWith(h, StringComparison.OrdinalIgnoreCase))) continue;
                keys.Add(a.Guid);
                seen.Add(a.Guid);
            }
        }

        /// 텍스처 폴더의 위 폴더 (Textures/Red/… 와 Textures/Blue/… 처럼 색마다 폴더를 나눈 것도 잡게). 두 단계보다 얕으면 너무 넓어 ""
        static string TextureHome(string path)
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 4 ? string.Join("/", parts.Take(parts.Length - 2)) + "/" : "";
        }

        static string TextureFamily(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var words = name.Split(new[] { '_', '-', ' ', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = words.Where(w => !ColorWords.Contains(w, StringComparer.OrdinalIgnoreCase) && !w.StartsWith("color", StringComparison.OrdinalIgnoreCase) && !w.StartsWith("colour", StringComparison.OrdinalIgnoreCase));
            return string.Join("_", kept);
        }

        static bool HasColorWord(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var words = name.Split(new[] { '_', '-', ' ', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => ColorWords.Contains(w, StringComparer.OrdinalIgnoreCase) || w.StartsWith("color", StringComparison.OrdinalIgnoreCase) || w.StartsWith("colour", StringComparison.OrdinalIgnoreCase));
        }

        /// TextureImporter 설정 (.meta). -1 은 Unity 기본값으로 둔다.
        static JNode Importer(AssetEntry a)
        {
            var meta = a.Meta.Length == 0 ? new Map() : Yaml.Parse(a.Meta).GetValueOrDefault("TextureImporter") as Map ?? new Map();
            int I(Map m, string k, int def) => int.TryParse(S(m, k), NumberStyles.Integer, C, out var v) ? v : def;
            var mip = meta.GetValueOrDefault("mipmaps") as Map ?? new Map();
            var ts = meta.GetValueOrDefault("textureSettings") as Map ?? new Map();

            // 최대 크기: Windows Standalone 오버라이드 → DefaultTexturePlatform → 최상위 maxTextureSize
            int maxSize = I(meta, "maxTextureSize", 2048);
            var plats = L(meta, "platformSettings").OfType<Map>().ToList();
            var def = plats.FirstOrDefault(p => S(p, "buildTarget") == "DefaultTexturePlatform");
            var standalone = plats.FirstOrDefault(p => S(p, "buildTarget") == "Standalone");
            if (standalone != null && S(standalone, "overridden") == "1") maxSize = I(standalone, "maxTextureSize", maxSize);
            else if (def != null) maxSize = I(def, "maxTextureSize", maxSize);

            int filter = I(ts, "filterMode", -1), aniso = I(ts, "aniso", -1), wrap = I(ts, "wrapU", -1);
            return JNode.Obj()
                .Set("sRGB", I(meta, "sRGBTexture", 1) == 1)
                .Set("normalMap", I(meta, "textureType", 0) == 1)
                .Set("mipmaps", I(mip, "enableMipMap", 1) == 1)
                .Set("alphaIsTransparency", I(meta, "alphaIsTransparency", 0) == 1)
                .Set("maxSize", maxSize)
                .Set("wrap", wrap < 0 ? 0 : wrap)
                .Set("filter", filter < 0 ? 1 : filter)
                .Set("aniso", aniso < 0 ? 1 : aniso)
                .Set("gpu", TextureGpu.FromMeta(a.Meta));   // PC에서 GPU에 올리는 형식 (2026-11-02)
        }

        // ---------------- 공통 ----------------

        static List<SimObject> Components(SimFile f, SimObject go) =>
            L(go.Props, "m_Component").Select(c => f.Get(Ref.FileId((c as Map)?.GetValueOrDefault("component")))).OfType<SimObject>().ToList();

        static string Name(SimObject go) => S(go.Props, "m_Name");
        static string S(Map m, string k) => m.GetValueOrDefault(k) as string ?? "";
        static List<object?> L(Map m, string k) => m.GetValueOrDefault(k) as List<object?> ?? new List<object?>();
        static float P(string? s, float def = 0) => double.TryParse(s, NumberStyles.Float, C, out var d) ? (float)d : def;

        static float[] Vec(Map props, string? key, params string[] comps)
        {
            var m = key == null ? props : props.GetValueOrDefault(key) as Map ?? new Map();
            return comps.Select(c => P(m.GetValueOrDefault(c) as string, c == "w" || c == "a" ? 1 : 0)).ToArray();
        }
    }
}
