// 임포터 ↔ 툴 경계(IR). PLAN.md 2장.
// 파일 구성: <name>.ir.json (구조) + buffers 파일 (리틀 엔디언 float32/int32 배열).
// 버퍼 참조는 { "off": 바이트 오프셋, "count": 원소 개수 }.
// 툴은 IR을 읽기만 한다 (Original). 편집은 Document의 Changeset으로만.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VrcProto.Protocol.Ir
{
    public struct BufRef
    {
        public long Off; public int Count;
        public bool IsEmpty => Count <= 0;
        public static BufRef From(JNode n) => n.IsNull ? default : new BufRef { Off = n["off"].AsLong, Count = n["count"].AsInt };
        public JNode ToJson() => JNode.Obj().Set("off", Off).Set("count", Count);
    }

    public sealed class SourceRef
    {
        public string Guid = ""; public long FileId;
        public static SourceRef? From(JNode n) => n.IsNull ? null : new SourceRef { Guid = n["guid"].AsString, FileId = n["fileID"].AsLong };
        public JNode ToJson() => JNode.Obj().Set("guid", Guid).Set("fileID", FileId.ToString());
    }

    public sealed class IrNode
    {
        public int Id; public string Name = ""; public int Parent = -1;
        public float[] Position = { 0, 0, 0 };
        public float[] Rotation = { 0, 0, 0, 1 }; // 쿼터니언 xyzw (Unity 좌표계)
        public float[] Scale = { 1, 1, 1 };
        public bool Active = true;
        public bool EditorOnly;   // 태그 EditorOnly: VRChat 업로드에서 빠지므로 처음부터 꺼 둔다 (Active = false)
        public SourceRef? Source;
    }

    public sealed class IrBlendFrame { public float Weight; public BufRef DeltaPos, DeltaNormal, DeltaTangent; }
    public sealed class IrBlendShape { public string Name = ""; public List<IrBlendFrame> Frames = new List<IrBlendFrame>(); }
    public sealed class IrSubmesh { public BufRef Indices; public string Topology = "Triangles"; }

    public sealed class IrMesh
    {
        public string Id = ""; public string Name = ""; public SourceRef? Source;
        public int VertexCount;
        public BufRef Positions, Normals, Tangents, Colors;      // float3, float3, float4, float4
        public BufRef[] Uvs = new BufRef[4]; public int[] UvDims = { 2, 2, 2, 2 };
        public BufRef BoneIndices, BoneWeights;                  // int4, float4 (정점당 4개)
        public BufRef Bindposes;                                 // float4x4 열 우선(Unity Matrix4x4 m00,m10,m20,m30,m01...)
        public List<IrSubmesh> Submeshes = new List<IrSubmesh>();
        public List<IrBlendShape> BlendShapes = new List<IrBlendShape>();
        public float[]? BoundsCenter, BoundsExtents;

        Dictionary<string, int>? _shapeIdx; int _shapeIdxFor = -1;
        /// <summary>이름이 같은 셰이프가 여럿이면 첫 번째 (Unity와 같음). 없으면 -1.</summary>
        public int BlendShapeIndex(string name)
        {
            if (_shapeIdx == null || _shapeIdxFor != BlendShapes.Count)
            {
                _shapeIdxFor = BlendShapes.Count;
                _shapeIdx = new Dictionary<string, int>();
                for (int i = 0; i < BlendShapes.Count; i++) if (!_shapeIdx.ContainsKey(BlendShapes[i].Name)) _shapeIdx[BlendShapes[i].Name] = i;
            }
            return _shapeIdx.TryGetValue(name, out var idx) ? idx : -1;
        }
    }

    /// <summary>
    /// 키 → 항목 조회표 (목록에서 처음 나온 것). 목록 길이가 바뀌면 다시 만든다.
    /// IR은 불러온 뒤 바뀌지 않으므로 List.Find를 매번 도는 것보다 훨씬 싸다.
    /// </summary>
    sealed class Lookup<T> where T : class
    {
        readonly Func<T, string> _key;
        Dictionary<string, T>? _map;
        int _builtFor = -1;
        public Lookup(Func<T, string> key) { _key = key; }

        public T? Find(List<T> list, string? key)
        {
            if (key == null) return null;
            if (_map == null || _builtFor != list.Count)
            {
                _map = new Dictionary<string, T>(list.Count);
                foreach (var x in list) { var k = _key(x); if (!_map.ContainsKey(k)) _map[k] = x; }
                _builtFor = list.Count;
            }
            return _map.TryGetValue(key, out var v) ? v : null;
        }
    }

    public sealed class IrRenderer
    {
        public string Id = ""; public int Node; public string Type = "SkinnedMeshRenderer";
        public string Mesh = ""; public int[] Bones = Array.Empty<int>(); public int RootBone = -1;
        public List<string> Materials = new List<string>(); // 머티리얼 키 (슬롯 순서 = 서브메시 순서). IrMaterial.Guid 참고
        public List<float> BlendShapeWeights = new List<float>();
        public bool Enabled = true;
        public float[]? BoundsCenter, BoundsExtents;
    }

    public sealed class IrTexEnv { public string? Texture; public float[] Scale = { 1, 1 }; public float[] Offset = { 0, 0 }; }

    /// <summary>
    /// Guid는 머티리얼 키다. .mat 에셋은 GUID 그대로, FBX 내장 머티리얼·Default-Material 같은 하위 에셋은 "guid:fileID".
    /// 툴은 이 값을 불투명한 문자열로만 비교한다. 텍스처 키(IrTexture.Guid)도 같은 규칙이다.
    /// </summary>
    public sealed class IrMaterial
    {
        public string Guid = ""; public string Path = ""; public string Name = "";
        // ShaderGuid: .shader 에셋은 GUID, Unity 기본 셰이더처럼 한 GUID에 여럿이 있는 것은 "guid:fileID" (렌더러 셰이더 표와 같은 키)
        public string ShaderGuid = ""; public string? ShaderName; // ShaderName은 임포터가 알 때만 (패키지 내 셰이더 등)
        public Dictionary<string, IrTexEnv> TexEnvs = new Dictionary<string, IrTexEnv>();
        public Dictionary<string, float> Floats = new Dictionary<string, float>();
        public Dictionary<string, float[]> Colors = new Dictionary<string, float[]>();
        public List<string> Keywords = new List<string>();
        public int RenderQueue = -1;
        public string? RawYaml;
    }

    public sealed class IrTextureImporter
    {
        public bool SRgb = true, NormalMap, Mipmaps = true, AlphaIsTransparency;
        public int MaxSize = 2048, Wrap /*0 repeat,1 clamp,2 mirror*/, Filter = 1 /*0 point,1 bilinear,2 trilinear*/, Aniso = 1;
        public string Gpu = TextureGpu.Auto;   // PC에서 GPU에 올리는 형식 (TextureGpu, 2026-11-02)
    }

    public sealed class IrTexture
    {
        public string Guid = ""; public string Path = ""; public string File = ""; // File: 디코드할 원본 이미지 파일의 절대 경로
        public IrTextureImporter Importer = new IrTextureImporter();
    }

    public sealed class IrScene
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string Name = "";
        public string BufferFile = "";   // 절대 경로로 정규화됨
        public List<IrNode> Nodes = new List<IrNode>();
        public List<IrMesh> Meshes = new List<IrMesh>();
        public List<IrRenderer> Renderers = new List<IrRenderer>();
        public List<IrMaterial> Materials = new List<IrMaterial>();
        public List<IrTexture> Textures = new List<IrTexture>();
        public List<IrPhysBone> PhysBones = new List<IrPhysBone>();                     // 선택 항목 (없으면 빈 목록)
        public List<IrPhysBoneCollider> PhysBoneColliders = new List<IrPhysBoneCollider>();
        /// <summary>휴머노이드 뼈 이름(모델 .meta humanDescription의 humanName: Hips, LeftUpperArm, "Left Thumb Proximal"…) → 노드 번호. 선택 항목 (Generic이거나 없으면 비어 있음)</summary>
        public Dictionary<string, int> Humanoid = new Dictionary<string, int>();
        /// <summary>VRC Avatar Descriptor의 ViewPosition (아바타 루트 기준 눈 위치 m, VR 시점). 없으면 null (importsim IrExpressions.cs)</summary>
        public float[]? ViewPosition;
        /// <summary>모델 .meta humanDescription의 비틀림 분배·늘이기·발 간격·근육 범위. 선택 항목 (없으면 Unity 기본값)</summary>
        public IrHumanoidSettings HumanoidSettings = new IrHumanoidSettings();
        /// <summary>합친 의상 (선택 항목 outfits, importsim OutfitMerge). 의상 노드는 아바타 노드 뒤에 온다.</summary>
        public List<IrOutfit> Outfits = new List<IrOutfit>();
        /// <summary>아바타의 기본 표정 (FX 레이어의 블렌드쉐이프 애니메이션, importsim IrExpressions.cs). 선택 항목</summary>
        public List<IrExpression> Expressions = new List<IrExpression>();
        /// <summary>아바타 기능 메뉴(VRC Expressions Menu)에서 블렌드쉐이프를 움직이는 조작 (importsim IrMenuShapes.cs). 선택 항목</summary>
        public List<IrMenuShape> MenuShapes = new List<IrMenuShape>();
        /// <summary>Modular Avatar Shape Changer (선택 항목 outfitRules.shapeChanges)</summary>
        public List<IrShapeChange> ShapeChanges = new List<IrShapeChange>();
        /// <summary>Modular Avatar Blendshape Sync (선택 항목 outfitRules.blendShapeSyncs)</summary>
        public List<IrBlendShapeSync> BlendShapeSyncs = new List<IrBlendShapeSync>();
        public JNode Raw = JNode.Null;   // 해석하지 않은 항목(assets, monoBehaviours 등) 원문 보존

        readonly Lookup<IrMaterial> _matIdx = new Lookup<IrMaterial>(m => m.Guid);
        readonly Lookup<IrRenderer> _renIdx = new Lookup<IrRenderer>(r => r.Id);
        readonly Lookup<IrMesh> _meshIdx = new Lookup<IrMesh>(m => m.Id);
        readonly Lookup<IrTexture> _texIdx = new Lookup<IrTexture>(t => t.Guid);
        public IrMaterial? FindMaterial(string guid) => _matIdx.Find(Materials, guid);
        public IrRenderer? FindRenderer(string id) => _renIdx.Find(Renderers, id);
        public IrMesh? FindMesh(string id) => _meshIdx.Find(Meshes, id);
        public IrTexture? FindTexture(string guid) => _texIdx.Find(Textures, guid);

        public string NodePath(int id)
        {
            var parts = new List<string>();
            for (int i = id; i >= 0 && i < Nodes.Count; i = Nodes[i].Parent) parts.Add(Nodes[i].Name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static IrScene Load(string jsonPath)
        {
            var root = JNode.Parse(File.ReadAllText(jsonPath));
            var s = FromJson(root);
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(jsonPath)) ?? ".";
            if (s.BufferFile.Length > 0 && !System.IO.Path.IsPathRooted(s.BufferFile))
                s.BufferFile = System.IO.Path.Combine(dir, s.BufferFile);
            foreach (var t in s.Textures)
                if (t.File.Length > 0 && !System.IO.Path.IsPathRooted(t.File)) t.File = System.IO.Path.Combine(dir, t.File);
            return s;
        }

        static float[] F(JNode n, float[] def) => n.IsNull ? def : n.ToFloatArray();
        static float[]? Fo(JNode n) => n.IsNull ? null : n.ToFloatArray();

        public static IrScene FromJson(JNode root)
        {
            var s = new IrScene { Raw = root, Version = root["irVersion"].AsInt, Name = root["name"].AsString, BufferFile = root["buffers"].AsString };
            if (s.Version > CurrentVersion) throw new NotSupportedException($"IR version {s.Version} > {CurrentVersion}");

            foreach (var n in root["nodes"].Items)
                s.Nodes.Add(new IrNode
                {
                    Id = n["id"].AsInt, Name = n["name"].AsString, Parent = n.Has("parent") ? n["parent"].AsInt : -1,
                    Position = F(n["position"], new float[] { 0, 0, 0 }), Rotation = F(n["rotation"], new float[] { 0, 0, 0, 1 }),
                    Scale = F(n["scale"], new float[] { 1, 1, 1 }), Active = (!n.Has("active") || n["active"].AsBool) && !n["editorOnly"].AsBool,
                    EditorOnly = n["editorOnly"].AsBool,
                    Source = SourceRef.From(n["source"]),
                });

            foreach (var m in root["meshes"].Items)
            {
                var mesh = new IrMesh
                {
                    Id = m["id"].AsString, Name = m["name"].AsString, Source = SourceRef.From(m["source"]),
                    VertexCount = m["vertexCount"].AsInt,
                    Positions = BufRef.From(m["positions"]), Normals = BufRef.From(m["normals"]),
                    Tangents = BufRef.From(m["tangents"]), Colors = BufRef.From(m["colors"]),
                    BoneIndices = BufRef.From(m["boneIndices"]), BoneWeights = BufRef.From(m["boneWeights"]),
                    Bindposes = BufRef.From(m["bindposes"]),
                    BoundsCenter = Fo(m["bounds"]["center"]), BoundsExtents = Fo(m["bounds"]["extents"]),
                };
                for (int i = 0; i < 4; i++)
                {
                    var uv = m["uv" + i];
                    mesh.Uvs[i] = BufRef.From(uv);
                    if (uv.Has("dim")) mesh.UvDims[i] = uv["dim"].AsInt;
                }
                foreach (var sm in m["submeshes"].Items)
                    mesh.Submeshes.Add(new IrSubmesh { Indices = BufRef.From(sm["indices"]), Topology = sm.Has("topology") ? sm["topology"].AsString : "Triangles" });
                foreach (var bs in m["blendShapes"].Items)
                {
                    var shape = new IrBlendShape { Name = bs["name"].AsString };
                    foreach (var f in bs["frames"].Items)
                        shape.Frames.Add(new IrBlendFrame
                        {
                            Weight = f["weight"].AsFloat, DeltaPos = BufRef.From(f["dPos"]),
                            DeltaNormal = BufRef.From(f["dNormal"]), DeltaTangent = BufRef.From(f["dTangent"]),
                        });
                    mesh.BlendShapes.Add(shape);
                }
                s.Meshes.Add(mesh);
            }

            foreach (var r in root["renderers"].Items)
            {
                var ren = new IrRenderer
                {
                    Id = r["id"].AsString, Node = r["node"].AsInt, Type = r.Has("type") ? r["type"].AsString : "SkinnedMeshRenderer",
                    Mesh = r["mesh"].AsString, RootBone = r.Has("rootBone") ? r["rootBone"].AsInt : -1,
                    Enabled = !r.Has("enabled") || r["enabled"].AsBool,
                    BoundsCenter = Fo(r["bounds"]["center"]), BoundsExtents = Fo(r["bounds"]["extents"]),
                };
                var bones = new List<int>(); foreach (var b in r["bones"].Items) bones.Add(b.AsInt); ren.Bones = bones.ToArray();
                foreach (var g in r["materials"].Items) ren.Materials.Add(g.AsString);
                foreach (var w in r["blendShapeWeights"].Items) ren.BlendShapeWeights.Add(w.AsFloat);
                s.Renderers.Add(ren);
            }

            foreach (var m in root["materials"].Items)
            {
                var mat = new IrMaterial
                {
                    Guid = m["guid"].AsString, Path = m["path"].AsString, Name = m["name"].AsString,
                    ShaderGuid = m["shaderGuid"].AsString, ShaderName = m["shaderName"].AsStringOrNull,
                    RenderQueue = m.Has("renderQueue") ? m["renderQueue"].AsInt : -1, RawYaml = m["rawYaml"].AsStringOrNull,
                };
                foreach (var kv in m["texEnvs"].Props)
                    mat.TexEnvs[kv.Key] = new IrTexEnv
                    {
                        Texture = kv.Value["texture"].AsStringOrNull,
                        Scale = F(kv.Value["scale"], new float[] { 1, 1 }), Offset = F(kv.Value["offset"], new float[] { 0, 0 }),
                    };
                foreach (var kv in m["floats"].Props) mat.Floats[kv.Key] = kv.Value.AsFloat;
                foreach (var kv in m["colors"].Props) mat.Colors[kv.Key] = kv.Value.ToFloatArray();
                foreach (var k in m["keywords"].Items) mat.Keywords.Add(k.AsString);
                if (mat.Name.Length == 0) mat.Name = System.IO.Path.GetFileNameWithoutExtension(mat.Path);
                s.Materials.Add(mat);
            }

            foreach (var t in root["textures"].Items)
            {
                var imp = t["importer"];
                s.Textures.Add(new IrTexture
                {
                    Guid = t["guid"].AsString, Path = t["path"].AsString, File = t["file"].AsString,
                    Importer = new IrTextureImporter
                    {
                        SRgb = !imp.Has("sRGB") || imp["sRGB"].AsBool, NormalMap = imp["normalMap"].AsBool,
                        Mipmaps = !imp.Has("mipmaps") || imp["mipmaps"].AsBool, AlphaIsTransparency = imp["alphaIsTransparency"].AsBool,
                        MaxSize = imp.Has("maxSize") ? imp["maxSize"].AsInt : 2048, Wrap = imp["wrap"].AsInt,
                        Filter = imp.Has("filter") ? imp["filter"].AsInt : 1, Aniso = imp.Has("aniso") ? imp["aniso"].AsInt : 1,
                        // 예전 IR(2026-11-02 전)에는 없다 → 원본 .meta에서 바로 (다시 가져오지 않아도 되게)
                        Gpu = imp["gpu"].AsStringOrNull ?? TextureGpu.FromFile(t["file"].AsString),
                    },
                });
            }
            foreach (var c in root["physBoneColliders"].Items) s.PhysBoneColliders.Add(ReadComponent(new IrPhysBoneCollider(), c));
            foreach (var b in root["physBones"].Items)
            {
                var pb = ReadComponent(new IrPhysBone(), b);
                foreach (var n in b["ignore"].Items) pb.Ignore.Add(n.AsInt);
                foreach (var n in b["colliders"].Items) pb.Colliders.Add(n.AsInt);
                s.PhysBones.Add(pb);
            }
            foreach (var kv in root["humanoid"].Props) s.Humanoid[kv.Key] = kv.Value.AsInt;
            if (root.Has("humanoidSettings")) s.HumanoidSettings = IrHumanoidSettings.FromJson(root["humanoidSettings"]);
            if (root["viewPosition"].Kind == JKind.Array && root["viewPosition"].Count >= 3) s.ViewPosition = root["viewPosition"].ToFloatArray();
            foreach (var e in root["expressions"].Items)
            {
                var x = new IrExpression { Name = e["name"].AsString, Layer = e["layer"].AsString, Clip = e["clip"].AsString, Guid = e["guid"].AsString };
                foreach (var sh in e["shapes"].Items) x.Shapes.Add(new IrExpressionShape { Path = sh["path"].AsString, Shape = sh["shape"].AsString, Value = sh["value"].AsFloat });
                s.Expressions.Add(x);
            }
            foreach (var e in root["menuShapes"].Items)
            {
                var x = new IrMenuShape { Name = e["name"].AsString, Menu = e["menu"].AsString, Type = e["type"].AsString, Parameter = e["parameter"].AsString };
                foreach (var sh in e["shapes"].Items) x.Shapes.Add(new IrMenuShapeValue { Path = sh["path"].AsString, Shape = sh["shape"].AsString, On = sh["on"].AsFloat, Off = sh["off"].AsFloat });
                s.MenuShapes.Add(x);
            }
            foreach (var o in root["outfits"].Items)
                s.Outfits.Add(new IrOutfit
                {
                    Node = o["node"].AsInt, Name = o["name"].AsString, Prefab = o["prefab"].AsString, MergeArmature = o["mergeArmature"].AsBool,
                    Kind = o.Has("kind") ? o["kind"].AsString : "outfit", Reason = o["reason"].AsStringOrNull ?? "", Method = o["method"].AsStringOrNull ?? "",
                    Target = o["target"].AsStringOrNull ?? "", NeedsPlacement = o["needsPlacement"].AsBool, NearOrigin = o["nearOrigin"].AsBool,
                    Bones = o["bones"].AsInt, Matched = o["matched"].AsInt, Merged = o["merged"].AsInt,
                    OffsetMean = o["offsetMean"].AsFloat, OffsetMax = o["offsetMax"].AsFloat,
                    HeadScale = o.Has("headScale") ? o["headScale"].AsDouble : 0,
                    Scale = o.Has("scale") ? o["scale"].AsDouble : 1,
                    Parts = o["parts"].Items.Select(x => x.AsInt).ToList(),
                });
            foreach (var c in root["outfitRules"]["shapeChanges"].Items)
                s.ShapeChanges.Add(new IrShapeChange
                {
                    Node = c["node"].AsInt, Enabled = !c.Has("enabled") || c["enabled"].AsBool, Inverted = c["inverted"].AsBool,
                    Renderer = c["renderer"].AsString, Shape = c["shape"].AsString, Delete = c["mode"].AsString == "delete",
                    Value = c.Has("value") ? c["value"].AsFloat : 100, Threshold = c.Has("threshold") ? c["threshold"].AsFloat : 0.01f,
                });
            foreach (var c in root["outfitRules"]["blendShapeSyncs"].Items)
                s.BlendShapeSyncs.Add(new IrBlendShapeSync { Renderer = c["renderer"].AsString, Shape = c["shape"].AsString, Source = c["source"].AsString, SourceShape = c["sourceShape"].AsString });
            return s;
        }

        static T ReadComponent<T>(T c, JNode n) where T : IrVrcComponent
        {
            c.Node = n["node"].AsInt; c.Root = n.Has("root") ? n["root"].AsInt : -1;
            c.Enabled = !n.Has("enabled") || n["enabled"].AsBool; c.Source = SourceRef.From(n["source"]);
            foreach (var kv in n["values"].Props) c.Values[kv.Key] = kv.Value.AsFloat;
            foreach (var kv in n["vectors"].Props) c.Vectors[kv.Key] = kv.Value.ToFloatArray();
            foreach (var kv in n["curves"].Props) c.Curves[kv.Key] = kv.Value.ToFloatArray();
            return c;
        }
    }

    /// <summary>
    /// VRChat 컴포넌트 (IR v1 선택 항목 physBones / physBoneColliders). 값은 SDK가 직렬화한 이름 그대로:
    /// Values = 숫자 (묶음은 "collisionFilter.allowSelf"), Vectors = xyz(w), Curves = 키프레임 [time, value, inSlope, outSlope]…
    /// Root: rootTransform 노드 (-1 = 컴포넌트가 붙은 노드)
    /// </summary>
    public abstract class IrVrcComponent
    {
        public int Node; public int Root = -1; public bool Enabled = true; public SourceRef? Source;
        public Dictionary<string, float> Values = new Dictionary<string, float>();
        public Dictionary<string, float[]> Vectors = new Dictionary<string, float[]>();
        public Dictionary<string, float[]> Curves = new Dictionary<string, float[]>();
        public float Get(string key, float def = 0) => Values.TryGetValue(key, out var v) ? v : def;
        public float[] Vec(string key, float[] def) => Vectors.TryGetValue(key, out var v) ? v : def;
        public int RootNode => Root >= 0 ? Root : Node;
    }

    public sealed class IrPhysBone : IrVrcComponent
    {
        public List<int> Ignore = new List<int>();      // ignoreTransforms 노드
        public List<int> Colliders = new List<int>();   // IrScene.PhysBoneColliders 인덱스
    }

    public sealed class IrPhysBoneCollider : IrVrcComponent { }

    /// <summary>근육 범위 (도, Unity HumanLimit과 같은 값). 기본값을 바꾼 뼈만 IR에 있다.</summary>
    public sealed class IrMuscleLimit { public float[] Min = new float[3], Max = new float[3], Center = new float[3]; public float AxisLength; }

    /// <summary>모델 .meta humanDescription의 설정 (IR humanoidSettings). 기본값 = Unity 모델 임포트 기본값.</summary>
    /// <summary>표정 하나: 애니메이션 클립의 블렌드쉐이프 값 (Path = 아바타 루트 기준 렌더러 경로, 루트 이름 없이 — .anim과 같다).</summary>
    public sealed class IrExpression
    {
        public string Name = "", Layer = "", Clip = "", Guid = "";
        public List<IrExpressionShape> Shapes = new List<IrExpressionShape>();
    }
    public sealed class IrExpressionShape { public string Path = "", Shape = ""; public float Value; }

    /// <summary>
    /// 아바타 기능 메뉴의 조작 하나 (토글·버튼·라디얼): 메뉴 경로(하위 메뉴 이름을 /로), 종류(toggle·button·radial), 파라미터,
    /// FX 레이어에서 그 파라미터로 바뀌는 블렌드쉐이프 (Path = 아바타 루트 기준 렌더러 경로, On = 켰을 때·라디얼 최대, Off = 껐을 때·라디얼 최소).
    /// </summary>
    public sealed class IrMenuShape
    {
        public string Name = "", Menu = "", Type = "", Parameter = "";
        public List<IrMenuShapeValue> Shapes = new List<IrMenuShapeValue>();
    }
    public sealed class IrMenuShapeValue { public string Path = "", Shape = ""; public float On, Off; }

    public sealed class IrHumanoidSettings
    {
        public float ArmTwist = 0.5f, ForeArmTwist = 0.5f, UpperLegTwist = 0.5f, LegTwist = 0.5f, ArmStretch = 0.05f, LegStretch = 0.05f, FeetSpacing;
        public bool HasTranslationDoF;
        public Dictionary<string, IrMuscleLimit> Limits = new Dictionary<string, IrMuscleLimit>();

        public static IrHumanoidSettings FromJson(JNode j)
        {
            var s = new IrHumanoidSettings();
            float F(string k, float d) => j.Has(k) ? j[k].AsFloat : d;
            s.ArmTwist = F("armTwist", 0.5f); s.ForeArmTwist = F("foreArmTwist", 0.5f); s.UpperLegTwist = F("upperLegTwist", 0.5f); s.LegTwist = F("legTwist", 0.5f);
            s.ArmStretch = F("armStretch", 0.05f); s.LegStretch = F("legStretch", 0.05f); s.FeetSpacing = F("feetSpacing", 0);
            s.HasTranslationDoF = j["hasTranslationDoF"].AsBool;
            foreach (var kv in j["limits"].Props)
                s.Limits[kv.Key] = new IrMuscleLimit { Min = kv.Value["min"].ToFloatArray(), Max = kv.Value["max"].ToFloatArray(), Center = kv.Value["center"].ToFloatArray(), AxisLength = kv.Value["axisLength"].AsFloat };
            return s;
        }
    }

    /// <summary>붙인 아이템(의상·헤어·악세서리) 하나. Node: 아이템 루트 노드. Prefab: 가져올 때 넣은 프리팹 guid (씬에 원래 있던 것이면 "").
    /// Kind: outfit · hair · accessory · unknown, Method: ma-merge · ma-proxy · name · manual · none, NeedsPlacement: 붙일 곳을 정하지 못함.
    /// Matched/OffsetMean: 아바타 본과 짝을 찾은 본 수와 위치 차이(m) — 이 아바타용인지 가늠한다.</summary>
    public sealed class IrOutfit
    {
        public int Node; public string Name = "", Prefab = "", Kind = "outfit", Reason = "", Method = "", Target = ""; public bool MergeArmature, NeedsPlacement, NearOrigin;
        public int Bones, Matched, Merged; public float OffsetMean, OffsetMax;
        /// <summary>아바타 머리 크기에 맞추기 위한 배율과 현재 아이템 전체 배율.</summary>
        public double HeadScale, Scale = 1;
        public List<int> Parts = new List<int>();   // Bone Proxy로 아이템 밖(아바타 뼈 아래)으로 옮긴 노드: 아이템을 켜고 끌 때 함께
    }

    /// <summary>Shape Changer 한 줄: Node가 켜져 있으면(Inverted면 꺼져 있으면) Renderer의 Shape를 Value로 (Delete면 그 블렌드쉐이프가 움직이는 면을 지운다).</summary>
    public sealed class IrShapeChange
    {
        public int Node; public bool Enabled = true, Inverted, Delete; public string Renderer = "", Shape = ""; public float Value = 100, Threshold = 0.01f;
    }

    /// <summary>Blendshape Sync 한 줄: Renderer의 Shape가 Source 렌더러의 SourceShape 값을 따른다.</summary>
    public sealed class IrBlendShapeSync
    {
        public string Renderer = "", Shape = "", Source = "", SourceShape = "";
    }

    /// <summary>buffers 파일에서 배열을 읽는다 (Unity 측 SceneBuilder용).</summary>
    public sealed class IrBuffers : IDisposable
    {
        readonly FileStream _fs;
        public IrBuffers(string path) { _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        byte[] Bytes(BufRef r, int elemSize)
        {
            var b = new byte[r.Count * elemSize];
            _fs.Position = r.Off;
            int read = 0;
            while (read < b.Length) { int n = _fs.Read(b, read, b.Length - read); if (n <= 0) throw new EndOfStreamException(); read += n; }
            return b;
        }
        public float[] Floats(BufRef r) { if (r.IsEmpty) return Array.Empty<float>(); var b = Bytes(r, 4); var f = new float[r.Count]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }
        public int[] Ints(BufRef r) { if (r.IsEmpty) return Array.Empty<int>(); var b = Bytes(r, 4); var f = new int[r.Count]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }

        byte[] _scratch = Array.Empty<byte>();
        /// <summary>
        /// dst에 읽는다 (새 배열을 만들지 않음 — 읽기 버퍼도 다시 씀). 렌더러가 블렌드쉐이프 프레임을 하나씩 메시에 넣을 때 쓴다:
        /// 프레임마다 배열을 새로 만들면 Unity Mono 힙이 커진 채 돌아오지 않는다.
        /// </summary>
        public void ReadFloats(BufRef r, float[] dst)
        {
            int len = r.Count * 4;
            if (_scratch.Length < len) _scratch = new byte[len];
            _fs.Position = r.Off;
            int read = 0;
            while (read < len) { int n = _fs.Read(_scratch, read, len - read); if (n <= 0) throw new EndOfStreamException(); read += n; }
            Buffer.BlockCopy(_scratch, 0, dst, 0, len);
        }

        /// <summary>
        /// dst에 바로 읽는다 (중간 버퍼·복사 없음). 렌더러는 Vector3[]를 float로 본 span을 넘겨 정점마다 옮기는 일을 없앤다 (2026-10-23 로드 시간).
        /// r.Count가 dst보다 길면 앞부분만.
        /// </summary>
        public void ReadFloats(BufRef r, Span<float> dst)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(dst.Slice(0, Math.Min(dst.Length, r.Count)));
            _fs.Position = r.Off;
            while (bytes.Length > 0) { int n = _fs.Read(bytes); if (n <= 0) throw new EndOfStreamException(); bytes = bytes.Slice(n); }
        }
        public void Dispose() => _fs.Dispose();
    }

    /// <summary>테스트·샘플용 IR 작성기.</summary>
    public sealed class IrBufferWriter : IDisposable
    {
        readonly FileStream _fs;
        public IrBufferWriter(string path) { _fs = new FileStream(path, FileMode.Create, FileAccess.Write); }
        public BufRef Write(float[] data) { var r = new BufRef { Off = _fs.Position, Count = data.Length }; var b = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length); _fs.Write(b, 0, b.Length); return r; }
        public BufRef Write(int[] data) { var r = new BufRef { Off = _fs.Position, Count = data.Length }; var b = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length); _fs.Write(b, 0, b.Length); return r; }
        public void Dispose() => _fs.Dispose();
    }
}
