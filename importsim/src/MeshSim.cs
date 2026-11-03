using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ImportSim
{
    public sealed class SimBlendShapeFrame
    {
        public float Weight;
        public Dictionary<int, Vec3> DeltaVertices = new Dictionary<int, Vec3>();
        public Dictionary<int, Vec3> DeltaNormals = new Dictionary<int, Vec3>();
        public Dictionary<int, Vec3> DeltaTangents = new Dictionary<int, Vec3>();
    }

    public sealed class SimBlendShape
    {
        public string Name = "";
        public List<SimBlendShapeFrame> Frames = new List<SimBlendShapeFrame>();
    }

    /// <summary>Unity Mesh 와 같은 형태의 메시 데이터 (Unity 공간)</summary>
    public sealed class SimMesh
    {
        public string Name = "";
        public List<Vec3> Vertices = new List<Vec3>();
        public List<Vec3> Normals = new List<Vec3>();
        public List<List<(double u, double v)>> Uvs = new List<List<(double, double)>>();
        public List<double[]>? Colors;
        public List<float[]>? Tangents;           // 정점마다 (x, y, z, w)
        public List<List<int>> Submeshes = new List<List<int>>();
        public List<int[]>? BoneIndices;          // 정점마다 4개
        public List<double[]>? BoneWeights;       // 정점마다 4개 (내림차순, 합 1)
        public List<Mat4> Bindposes = new List<Mat4>();
        public List<long> Bones = new List<long>();   // FBX Model id (Unity m_Bones 순서)
        public List<int>? ControlPoints;             // 정점마다 FBX 제어점 번호 (디버그 출력용)
        public List<SimBlendShape> BlendShapes = new List<SimBlendShape>();
    }

    public sealed class MeshSettings
    {
        public bool ImportBlendShapes = true;
        public int BlendShapeNormalMode = 1;   // 0 Import, 1 Calculate, 2 None
        public int MaxBones = 4;
        public double MinBoneWeight = 0.001;
        public double SmoothAngle = 60;
        public int TangentMode = 3;            // 0 = FBX 값(없으면 탄젠트 없음), 1 = 레거시 계산, 2 = 없음, 3 = MikkTSpace
        /// legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes: 1 이면 블렌드쉐이프 노멀·탄젠트 델타가 없다
        public bool LegacyBlendShapeNormals;
    }

    /// <summary>FBX Geometry -> Unity Mesh</summary>
    public static class MeshSim
    {
        /// meshWorld: Unity 계층에서 이 메시 노드의 월드 행렬 (바인드포즈 계산용)
        public static SimMesh Build(FbxScene scene, FbxObject model, FbxObject geom, UnitySpace space, MeshSettings st,
                                    IReadOnlyDictionary<long, int> dfsOrder, Mat4 meshWorld)
        {
            var g = geom.Node;
            var mesh = new SimMesh { Name = model.Name };
            var cp = g.Array<double>("Vertices") ?? Array.Empty<double>();
            var poly = g.Array<int>("PolygonVertexIndex") ?? Array.Empty<int>();
            int nCp = cp.Length / 3;
            var geo = UnitySpace.Geometric(model.Node);
            var geoN = geo.Inverse();   // 노멀: 역전치 (아래에서 전치로 곱한다)

            Vec3 P(int i) => space.Point(geo.MulPoint(new Vec3(cp[i * 3], cp[i * 3 + 1], cp[i * 3 + 2])));
            Vec3 N(Vec3 n) => UnitySpace.Dir(new Vec3(
                geoN.M[0, 0] * n.X + geoN.M[1, 0] * n.Y + geoN.M[2, 0] * n.Z,
                geoN.M[0, 1] * n.X + geoN.M[1, 1] * n.Y + geoN.M[2, 1] * n.Z,
                geoN.M[0, 2] * n.X + geoN.M[1, 2] * n.Y + geoN.M[2, 2] * n.Z)).Normalized;

            var normals = Layer(g, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            var uvLayers = g.All("LayerElementUV").OrderBy(n => n.Props.Count > 0 ? FbxNode.Num(n.Props[0]) : 0)
                .Select(n => Layer(n, "UV", "UVIndex", 2)).ToList();
            var colorNode = g.Find("LayerElementColor");
            var colors = colorNode != null ? Layer(colorNode, "Colors", "ColorIndex", 4) : null;
            var matLayer = g.Find("LayerElementMaterial");
            var matIdx = matLayer?.Array<int>("Materials");
            bool matAllSame = matLayer?.Find("MappingInformationType")?.Props[0] as string != "ByPolygon";

            // ---- 스킨: 제어점별 (본 순번, 가중치) ----
            // Unity: 본의 바인드 월드 = 클러스터 TransformLink (Unity 공간), 메시 쪽은 Unity 계층의 메시 노드 월드
            var skinBones = new List<(long bone, Mat4 bindpose)>();
            var cpInfluence = new List<(int bone, double w)>?[nCp];
            foreach (var skin in scene.ChildObjects(geom.Id, "Deformer").Where(d => d.SubType == "Skin"))
                foreach (var cl in scene.ChildObjects(skin.Id, "Deformer").Where(d => d.SubType == "Cluster"))
                {
                    var bone = scene.ChildObjects(cl.Id, "Model").FirstOrDefault();
                    if (bone == null) continue;
                    var tl = cl.Node.Array<double>("TransformLink");
                    var boneWorld = tl != null ? space.World(Mat4.FromColumnMajor(tl)) : meshWorld;
                    int bi = skinBones.Count;
                    skinBones.Add((bone.Id, boneWorld.Inverse() * meshWorld));
                    var idx = cl.Node.Array<int>("Indexes");
                    var ws = cl.Node.Array<double>("Weights");
                    if (idx == null || ws == null) continue;
                    for (int k = 0; k < idx.Length; k++)
                        if (idx[k] >= 0 && idx[k] < nCp)
                            (cpInfluence[idx[k]] ??= new List<(int, double)>()).Add((bi, ws[k]));
                }
            // m_Bones 순서 = FBX 계층 깊이 우선 순서
            var boneOrder = Enumerable.Range(0, skinBones.Count)
                .OrderBy(i => dfsOrder.TryGetValue(skinBones[i].bone, out var o) ? o : int.MaxValue).ToList();
            var remap = new int[skinBones.Count];
            for (int k = 0; k < boneOrder.Count; k++) remap[boneOrder[k]] = k;
            foreach (var i in boneOrder) { mesh.Bones.Add(skinBones[i].bone); mesh.Bindposes.Add(skinBones[i].bindpose); }
            bool skinned = skinBones.Count > 0;

            // 제어점별 최종 가중치: 큰 순서로 MaxBones 개, 합 1로 정규화
            var cpWeights = new (int[] bi, double[] bw)[nCp];
            if (skinned)
                for (int c = 0; c < nCp; c++)
                {
                    var all = (cpInfluence[c] ?? new List<(int, double)>())
                        .Select(x => (bone: remap[x.bone], x.w)).Where(x => x.w > 0)
                        .OrderByDescending(x => x.w).ThenBy(x => x.bone).ToList();
                    // minBoneWeight 미만은 버리되, 전부 미만이면 가장 큰 것 하나는 남긴다
                    var inf = all.Where(x => (float)x.w >= (float)st.MinBoneWeight).Take(st.MaxBones).ToList();
                    if (inf.Count == 0 && all.Count > 0) inf.Add(all[0]);
                    // 어느 본에도 안 묶인 정점은 m_Bones 0번 본에 가중치 1
                    if (inf.Count == 0) inf.Add((0, 1.0));
                    double sum = inf.Sum(x => x.w);
                    var bi = new int[4]; var bw = new double[4];
                    for (int k = 0; k < inf.Count && k < 4; k++) { bi[k] = inf[k].bone; bw[k] = sum > 0 ? inf[k].w / sum : 0; }
                    cpWeights[c] = (bi, bw);
                }

            // ---- 블렌드쉐이프: 채널 -> 프레임(Shape) -> 제어점 델타 ----
            var shapes = new List<(string name, List<(double w, Dictionary<int, Vec3> dv, Dictionary<int, Vec3> dn)> frames)>();
            if (st.ImportBlendShapes)
                foreach (var bs in scene.ChildObjects(geom.Id, "Deformer").Where(d => d.SubType == "BlendShape"))
                    foreach (var ch in scene.ChildObjects(bs.Id, "Deformer").Where(d => d.SubType == "BlendShapeChannel"))
                    {
                        var full = ch.Node.Array<double>("FullWeights");
                        var frames = new List<(double, Dictionary<int, Vec3>, Dictionary<int, Vec3>)>();
                        int fi = 0;
                        foreach (var sh in scene.ChildObjects(ch.Id, "Geometry").Where(x => x.SubType == "Shape"))
                        {
                            var ix = sh.Node.Array<int>("Indexes") ?? Array.Empty<int>();
                            var dv = sh.Node.Array<double>("Vertices") ?? Array.Empty<double>();
                            var dn = sh.Node.Array<double>("Normals");
                            var mv = new Dictionary<int, Vec3>();
                            var mn = new Dictionary<int, Vec3>();
                            for (int k = 0; k < ix.Length; k++)
                            {
                                mv[ix[k]] = new Vec3(dv[k * 3], dv[k * 3 + 1], dv[k * 3 + 2]);
                                if (dn != null && dn.Length >= ix.Length * 3) mn[ix[k]] = new Vec3(dn[k * 3], dn[k * 3 + 1], dn[k * 3 + 2]);
                            }
                            frames.Add((full != null && fi < full.Length ? full[fi] : 100, mv, mn));
                            fi++;
                        }
                        shapes.Add((ch.Name, frames));
                    }

            // ---- 제어점 서명: 위치(float) + 최종 스킨 가중치 + 블렌드쉐이프 델타 ----
            // 서명과 노멀·UV·컬러가 같으면 Unity weld 로 하나가 된다 (제어점이 달라도)
            var sigIds = new Dictionary<string, int>();
            var cpSig = new int[nCp];
            for (int c = 0; c < nCp; c++)
            {
                var sb = new StringBuilder();
                var pos = P(c);
                sb.Append((float)pos.X).Append(',').Append((float)pos.Y).Append(',').Append((float)pos.Z).Append('|');
                if (skinned) for (int k = 0; k < 4; k++) sb.Append(cpWeights[c].bi[k]).Append(':').Append((float)cpWeights[c].bw[k]).Append(';');
                foreach (var (_, frames) in shapes)
                    foreach (var (_, dv, _) in frames)
                        if (dv.TryGetValue(c, out var dd)) sb.Append((float)dd.X).Append(',').Append((float)dd.Y).Append(',').Append((float)dd.Z).Append(';');
                        else sb.Append('0').Append(';');
                var key = sb.ToString();
                if (!sigIds.TryGetValue(key, out var id)) { id = sigIds.Count; sigIds[key] = id; }
                cpSig[c] = id;
            }

            // ---- 폴리곤 -> 삼각형, 정점 분리(서명 + 속성이 같으면 공유) ----
            var slotToSub = new Dictionary<int, int>();
            var keyToVertex = new Dictionary<VertexKey, int>();
            var vertexCp = new List<int>();
            int pv = 0, polyIndex = 0;
            var corners = new List<int>();
            // MikkTSpace 입력: Unity 처럼 원래 폴리곤(사각형 이하) 단위, 감김은 삼각형과 같게 뒤집은 순서
            var mikkFaces = new List<int[]>();
            var subRefs = new List<List<(int face, int corner)>>();
            for (int i = 0; i < poly.Length; i++)
            {
                int raw = poly[i];
                bool end = raw < 0;
                int c = end ? ~raw : raw;
                var key = new VertexKey { Cp = cpSig[c] };
                key.N = normals?.Get(polyIndex, pv, c);
                key.Uv = new double[uvLayers.Count][];
                for (int u = 0; u < uvLayers.Count; u++) key.Uv[u] = uvLayers[u]?.Get(polyIndex, pv, c) ?? new double[2];
                key.Color = colors?.Get(polyIndex, pv, c);
                if (!keyToVertex.TryGetValue(key, out var vi))
                {
                    vi = mesh.Vertices.Count;
                    keyToVertex[key] = vi;
                    vertexCp.Add(c);
                    mesh.Vertices.Add(P(c));
                    mesh.Normals.Add(key.N != null ? N(new Vec3(key.N[0], key.N[1], key.N[2])) : new Vec3(0, 0, 0));
                    for (int u = 0; u < uvLayers.Count; u++)
                    {
                        if (mesh.Uvs.Count <= u) mesh.Uvs.Add(new List<(double, double)>());
                        mesh.Uvs[u].Add((key.Uv[u][0], key.Uv[u][1]));
                    }
                    if (key.Color != null) (mesh.Colors ??= new List<double[]>()).Add(key.Color);
                }
                corners.Add(vi);
                pv++;
                if (!end) continue;

                int slot = matIdx == null || matIdx.Length == 0 ? 0 : matAllSame ? matIdx[0] : matIdx[Math.Min(polyIndex, matIdx.Length - 1)];
                if (!slotToSub.TryGetValue(slot, out var sub))
                {
                    sub = slotToSub.Count; slotToSub[slot] = sub;
                    mesh.Submeshes.Add(new List<int>()); subRefs.Add(new List<(int, int)>());
                }
                var tris = mesh.Submeshes[sub];
                var refs = subRefs[sub];
                int nc = corners.Count;
                int Rev(int j) => j == 0 ? 0 : nc - j;   // 뒤집은 순서에서의 코너 번호
                int faceBase = mikkFaces.Count;
                if (nc >= 3 && nc <= 4)
                {
                    var face = new int[nc];
                    for (int j = 0; j < nc; j++) face[Rev(j)] = corners[j];
                    mikkFaces.Add(face);
                }
                // 부채꼴 분할 (사각형 포함). X 반전 때문에 감김 방향을 뒤집는다
                for (int k = 1; k + 1 < nc; k++)
                {
                    tris.Add(corners[0]); tris.Add(corners[k + 1]); tris.Add(corners[k]);
                    if (nc <= 4) { refs.Add((faceBase, 0)); refs.Add((faceBase, Rev(k + 1))); refs.Add((faceBase, Rev(k))); }
                    else
                    {
                        int fi = mikkFaces.Count;
                        mikkFaces.Add(new[] { corners[0], corners[k + 1], corners[k] });
                        refs.Add((fi, 0)); refs.Add((fi, 1)); refs.Add((fi, 2));
                    }
                }
                corners.Clear();
                polyIndex++;
            }
            if (mesh.Submeshes.Count == 0) { mesh.Submeshes.Add(new List<int>()); subRefs.Add(new List<(int, int)>()); }

            // ---- 탄젠트 (MikkTSpace): 코너마다 계산하고, 탄젠트가 다른 코너는 정점을 나눈다 ----
            if (st.TangentMode == 3 && mesh.Uvs.Count > 0)
                vertexCp = SplitByTangents(mesh, vertexCp, mikkFaces, subRefs);
            // 레거시 계산(1): 같은 위치의 비슷한 방향 면들로 탄젠트를 평균하고, 방향이 갈리는 코너는 정점을 나눈다
            else if (st.TangentMode == 1 && mesh.Uvs.Count > 0)
                vertexCp = LegacyTangents(mesh, vertexCp);

            // ---- 블렌드쉐이프 노멀 (Calculate) ----
            // Unity 는 셰이프마다 메시 전체의 노멀을 다시 계산한다(움직이지 않은 곳도 계산 노멀 - 임포트 노멀 = 델타).
            // 코너 단위로 계산하고, 코너마다 델타가 다르면 Unity 처럼 정점을 나눈다
            List<Dictionary<int, Vec3>>? vertexDn = null;
            if (st.BlendShapeNormalMode == 1 && !st.LegacyBlendShapeNormals && shapes.Count > 0)
            {
                var calc = new NormalCalc(mesh.Vertices, mesh.Submeshes, st.SmoothAngle);
                var allGroups = calc.AllGroups();
                var allTris = mesh.Submeshes.SelectMany(x => x).ToList();
                // 프레임마다 메시 전체 노멀을 다시 계산한다: 프레임끼리 독립이라 병렬로 (얼굴 메시는 프레임이 수백 개)
                var frameDv = shapes.SelectMany(s => s.frames.Select(fr => fr.dv)).ToList();
                var cornerDnArr = new Dictionary<int, Vec3>[frameDv.Count];
                // 옮긴 위치 배열은 스레드마다 하나를 다시 쓴다 (프레임마다 정점 전체를 복사하면 GC가 병렬 작업을 막았다): 쓰고 나서 바꾼 자리만 되돌린다
                System.Threading.Tasks.Parallel.For(0, frameDv.Count, () => (Buf: mesh.Vertices.ToArray(), Changed: new List<int>()), (fi, _, local) =>
                {
                    var dv = frameDv[fi];
                    var moved = local.Buf;
                    local.Changed.Clear();
                    for (int v = 0; v < vertexCp.Count; v++)
                        if (dv.TryGetValue(vertexCp[v], out var d))
                        {
                            moved[v] = moved[v] + space.Point(geo.MulDir(d));
                            local.Changed.Add(v);
                        }
                    var cn = calc.ComputeCorners(moved, allGroups);
                    foreach (var v in local.Changed) moved[v] = mesh.Vertices[v];
                    var dnc = new Dictionary<int, Vec3>();
                    foreach (var kv in cn)
                    {
                        var dd = kv.Value - mesh.Normals[allTris[kv.Key]];
                        if (Math.Abs(dd.X) > 1e-7 || Math.Abs(dd.Y) > 1e-7 || Math.Abs(dd.Z) > 1e-7) dnc[kv.Key] = dd;
                    }
                    cornerDnArr[fi] = dnc;
                    return local;
                }, _ => { });
                var cornerDn = cornerDnArr.ToList();
                vertexCp = SplitByCornerNormals(mesh, vertexCp, cornerDn, out var cornerToVertex);
                vertexDn = cornerDn.Select(cd =>
                {
                    var m = new Dictionary<int, Vec3>();
                    foreach (var kv in cd) m[cornerToVertex[kv.Key]] = kv.Value;
                    return m;
                }).ToList();
            }

            // ---- 허용 오차 weld: 탄젠트·블렌드쉐이프 노멀까지 정한 뒤, 노멀·탄젠트 2.75°·UV 거리 0.001 안이면 먼저 나온 정점으로 합친다 ----
            vertexCp = ToleranceWeld(mesh, vertexCp, cpSig, vertexDn);

            // ---- 스킨 가중치 (정점마다) ----
            if (skinned)
            {
                mesh.BoneIndices = new List<int[]>(); mesh.BoneWeights = new List<double[]>();
                foreach (var c in vertexCp) { mesh.BoneIndices.Add(cpWeights[c].bi); mesh.BoneWeights.Add(cpWeights[c].bw); }
            }

            // ---- 블렌드쉐이프 델타 (정점마다) ----
            int frameIndex = 0;
            foreach (var (name, frames) in shapes)
            {
                var s = new SimBlendShape { Name = name };
                foreach (var (w, dv, dn) in frames)
                {
                    var f = new SimBlendShapeFrame { Weight = (float)w };
                    for (int v = 0; v < vertexCp.Count; v++)
                    {
                        if (dv.TryGetValue(vertexCp[v], out var d))
                        {
                            var u = space.Point(geo.MulDir(d));
                            if (u.X != 0 || u.Y != 0 || u.Z != 0) f.DeltaVertices[v] = u;
                        }
                        if (st.BlendShapeNormalMode == 0 && !st.LegacyBlendShapeNormals && dn.TryGetValue(vertexCp[v], out var n))
                            f.DeltaNormals[v] = UnitySpace.Dir(n);
                    }
                    if (vertexDn != null) f.DeltaNormals = vertexDn[frameIndex];
                    frameIndex++;
                    s.Frames.Add(f);
                }
                mesh.BlendShapes.Add(s);
            }

            // ---- 블렌드쉐이프 탄젠트 델타: 변형된 위치·노멀로 MikkTSpace 를 다시 돌려 기본 탄젠트와의 차이 ----
            if (mesh.Tangents != null && mesh.BlendShapes.Count > 0 && st.BlendShapeNormalMode != 2 && !st.LegacyBlendShapeNormals)
            {
                // 최종 정점 번호로 MikkTSpace 면 목록을 다시 만든다 (subRefs 는 삼각형 코너와 나란하다)
                var finalFaces = mikkFaces.Select(fc => new int[fc.Length]).ToArray();
                for (int si = 0; si < mesh.Submeshes.Count; si++)
                    for (int k = 0; k < mesh.Submeshes[si].Count; k++)
                    {
                        var (fc, cn) = subRefs[si][k];
                        finalFaces[fc][cn] = mesh.Submeshes[si][k];
                    }
                // 프레임마다 MikkTSpace를 다시 돌린다: 프레임끼리 독립이라 병렬로 (각 프레임은 자기 DeltaTangents만 쓴다)
                var allFrames = mesh.BlendShapes.SelectMany(b => b.Frames).ToList();
                // 위치·노멀 배열은 스레드마다 하나를 다시 쓰고, 프레임이 끝나면 바꾼 자리만 되돌린다
                System.Threading.Tasks.Parallel.ForEach(allFrames, () => (Pos: mesh.Vertices.ToArray(), Nrm: mesh.Normals.ToArray()), (f, _, local) =>
                    {
                        if (f.DeltaVertices.Count == 0 && f.DeltaNormals.Count == 0) return local;
                        var pos = local.Pos; var nrm = local.Nrm;
                        foreach (var kv in f.DeltaVertices) pos[kv.Key] = pos[kv.Key] + kv.Value;
                        foreach (var kv in f.DeltaNormals) nrm[kv.Key] = (nrm[kv.Key] + kv.Value).Normalized;
                        var (tan, offs) = MikkTSpace.Generate(finalFaces,
                            i => ((float)pos[i].X, (float)pos[i].Y, (float)pos[i].Z),
                            i => ((float)nrm[i].X, (float)nrm[i].Y, (float)nrm[i].Z),
                            i => ((float)mesh.Uvs[0][i].u, (float)mesh.Uvs[0][i].v));
                        var done = new HashSet<int>();
                        for (int fc = 0; fc < finalFaces.Length; fc++)
                            for (int cn = 0; cn < finalFaces[fc].Length; cn++)
                            {
                                int v = finalFaces[fc][cn];
                                if (!done.Add(v)) continue;
                                int c = offs[fc] + cn;
                                var bt = mesh.Tangents[v];
                                var d = new Vec3(tan[c * 4] - bt[0], tan[c * 4 + 1] - bt[1], tan[c * 4 + 2] - bt[2]);
                                if (Math.Abs(d.X) > 1e-7 || Math.Abs(d.Y) > 1e-7 || Math.Abs(d.Z) > 1e-7) f.DeltaTangents[v] = d;
                            }
                        foreach (var kv in f.DeltaVertices) pos[kv.Key] = mesh.Vertices[kv.Key];
                        foreach (var kv in f.DeltaNormals) nrm[kv.Key] = mesh.Normals[kv.Key];
                        return local;
                    }, _ => { });
            }
            mesh.ControlPoints = vertexCp;
            return mesh;
        }

        /// 코너별 블렌드쉐이프 노멀 델타가 다른 코너끼리는 정점을 나눈다. cornerToVertex: 코너 -> 새 정점
        static List<int> SplitByCornerNormals(SimMesh mesh, List<int> vertexCp, List<Dictionary<int, Vec3>> cornerDn, out int[] cornerToVertex)
        {
            int nCorners = mesh.Submeshes.Sum(x => x.Count);
            cornerToVertex = new int[nCorners];
            var sigOf = new (ulong, ulong)[nCorners];
            for (int c = 0; c < nCorners; c++) sigOf[c] = DnHash(cornerDn, c);
            var split = new Dictionary<(int, (ulong, ulong)), int>();
            var verts = new List<Vec3>(); var nrms = new List<Vec3>(); var cps = new List<int>();
            var uvs = mesh.Uvs.Select(_ => new List<(double, double)>()).ToList();
            var cols = mesh.Colors != null ? new List<double[]>() : null;
            var tans = mesh.Tangents != null ? new List<float[]>() : null;
            int corner = 0;
            foreach (var sub in mesh.Submeshes)
                for (int k = 0; k < sub.Count; k++, corner++)
                {
                    int v = sub[k];
                    var key = (v, sigOf[corner]);
                    if (!split.TryGetValue(key, out var nv))
                    {
                        nv = verts.Count;
                        split[key] = nv;
                        verts.Add(mesh.Vertices[v]); nrms.Add(mesh.Normals[v]); cps.Add(vertexCp[v]);
                        for (int u = 0; u < uvs.Count; u++) uvs[u].Add(mesh.Uvs[u][v]);
                        cols?.Add(mesh.Colors![v]);
                        tans?.Add(mesh.Tangents![v]);
                    }
                    sub[k] = nv;
                    cornerToVertex[corner] = nv;
                }
            mesh.Vertices = verts; mesh.Normals = nrms; mesh.Uvs = uvs; mesh.Colors = cols; mesh.Tangents = tans;
            return cps;
        }

        /// 레거시 탄젠트 (tangentImportMode 1). Unity 결과에 맞춘 근사 (README 참고)
        /// - 삼각형 탄젠트·바이탄젠트 = UV 행렬식으로 나누지 않고 부호만 곱한 방향, 코너 각도로 가중한다
        /// - 정점의 코너를 탄젠트·바이탄젠트가 90° 이내인 묶음으로 나누고, 묶음마다 정점을 하나 만든다
        /// - 묶음 탄젠트 = 같은 위치(UV·노멀 경계 무시)의 모든 코너 중 묶음 방향과 90° 이내인 것의 합을 노멀에 직교화
        /// - w = 묶음 바이탄젠트 합의 방향 부호
        static List<int> LegacyTangents(SimMesh mesh, List<int> vertexCp)
        {
            var uv = mesh.Uvs[0];
            var all = mesh.Submeshes.SelectMany(x => x).ToList();
            int nTri = all.Count / 3;
            var fT = new Vec3[nTri]; var fB = new Vec3[nTri]; var fTu = new Vec3[nTri]; var fBu = new Vec3[nTri];
            for (int f = 0; f < nTri; f++)
            {
                int a = all[f * 3], b = all[f * 3 + 1], c = all[f * 3 + 2];
                var e1 = mesh.Vertices[b] - mesh.Vertices[a];
                var e2 = mesh.Vertices[c] - mesh.Vertices[a];
                double s1 = uv[b].u - uv[a].u, s2 = uv[c].u - uv[a].u;
                double q1 = uv[b].v - uv[a].v, q2 = uv[c].v - uv[a].v;
                double det = s1 * q2 - s2 * q1;
                double r = Math.Abs(det) < 1e-20 ? 0 : Math.Sign(det);
                fT[f] = (e1 * q2 - e2 * q1) * r; fB[f] = (e2 * s1 - e1 * s2) * r;
                fTu[f] = fT[f].Normalized; fBu[f] = fB[f].Normalized;
            }
            double CornerAngle(int c)
            {
                int f = c / 3, j = c % 3, v0 = all[c];
                var u1 = (mesh.Vertices[all[f * 3 + (j + 1) % 3]] - mesh.Vertices[v0]).Normalized;
                var u2 = (mesh.Vertices[all[f * 3 + (j + 2) % 3]] - mesh.Vertices[v0]).Normalized;
                return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(u1, u2))));
            }
            var cornersOf = new List<int>[mesh.Vertices.Count];
            var posCorners = new Dictionary<(float, float, float), List<int>>();
            for (int c = 0; c < all.Count; c++)
            {
                (cornersOf[all[c]] ??= new List<int>()).Add(c);
                var p0 = mesh.Vertices[all[c]];
                var pk = ((float)p0.X, (float)p0.Y, (float)p0.Z);
                if (!posCorners.TryGetValue(pk, out var pl)) posCorners[pk] = pl = new List<int>();
                pl.Add(c);
            }
            var cornerVertex = new int[all.Count];
            var verts = new List<Vec3>(); var nrms = new List<Vec3>(); var cps = new List<int>();
            var uvs = mesh.Uvs.Select(_ => new List<(double, double)>()).ToList();
            var cols = mesh.Colors != null ? new List<double[]>() : null;
            var tans = new List<float[]>();
            for (int v = 0; v < mesh.Vertices.Count; v++)
            {
                if (cornersOf[v] == null) continue;
                var nn = mesh.Normals[v];
                var groups = new List<(Vec3 seedT, Vec3 seedB, Vec3 sumT, Vec3 sumB, List<int> cs)>();
                foreach (int c in cornersOf[v])
                {
                    int f = c / 3;
                    double w = CornerAngle(c);
                    int gi = groups.FindIndex(g => Vec3.Dot(g.seedT, fTu[f]) >= 0 && Vec3.Dot(g.seedB, fBu[f]) >= 0);
                    if (gi < 0) groups.Add((fTu[f], fBu[f], fT[f] * w, fB[f] * w, new List<int> { c }));
                    else { var g = groups[gi]; g.sumT = g.sumT + fT[f] * w; g.sumB = g.sumB + fB[f] * w; g.cs.Add(c); groups[gi] = g; }
                }
                var p0 = mesh.Vertices[v];
                var around = posCorners[((float)p0.X, (float)p0.Y, (float)p0.Z)];
                foreach (var g in groups)
                {
                    var seed = g.sumT.Normalized;
                    var sumT = new Vec3(0, 0, 0);
                    foreach (int c in around)
                        if (Vec3.Dot(fTu[c / 3], seed) > 1e-6) sumT = sumT + fT[c / 3] * CornerAngle(c);
                    var tt = (sumT - nn * Vec3.Dot(nn, sumT)).Normalized;
                    double hw = Vec3.Dot(Vec3.Cross(nn, tt), g.sumB) < 0 ? -1 : 1;
                    int nv = verts.Count;
                    verts.Add(p0); nrms.Add(nn); cps.Add(vertexCp[v]);
                    for (int u = 0; u < uvs.Count; u++) uvs[u].Add(mesh.Uvs[u][v]);
                    cols?.Add(mesh.Colors![v]);
                    tans.Add(new[] { (float)tt.X, (float)tt.Y, (float)tt.Z, (float)hw });
                    foreach (int c in g.cs) cornerVertex[c] = nv;
                }
            }
            int k2 = 0;
            foreach (var sub in mesh.Submeshes)
                for (int k = 0; k < sub.Count; k++, k2++) sub[k] = cornerVertex[k2];
            mesh.Vertices = verts; mesh.Normals = nrms; mesh.Uvs = uvs; mesh.Colors = cols; mesh.Tangents = tans;
            return cps;
        }

        /// Unity 결과에서 잰 경계 (아바타 A·의상 B 등): 합쳐진 쌍 최대 2.7495°, 나뉜 쌍 최소 2.7510° → 2.75°.
        /// 노멀도 2.740° 쌍이 합쳐진다 (이전 2.736° 기준은 조금 작았다)
        const double WeldCos = 0.9988484;
        /// UV 는 채널마다 거리(L2) 0.001 이내면 합쳐진다 (합쳐진 쌍 최대 0.00099. 성분마다 0.001 안이어도 거리 0.00103 인 쌍은 나뉜다)
        const float WeldUv = 0.001f;

        /// 블렌드쉐이프 노멀 델타 서명: (프레임, float 값) 목록의 128비트 해시
        static (ulong, ulong) DnHash(List<Dictionary<int, Vec3>> dn, int v)
        {
            ulong h1 = 1469598103934665603UL, h2 = 0x9E3779B97F4A7C15UL;
            void Mix(uint x) { h1 = (h1 ^ x) * 1099511628211UL; h2 = (h2 + x) * 0xC2B2AE3D27D4EB4FUL; h2 ^= h2 >> 29; }
            for (int k = 0; k < dn.Count; k++)
                if (dn[k].TryGetValue(v, out var d))
                {
                    Mix((uint)k);
                    Mix(Bits((float)d.X)); Mix(Bits((float)d.Y)); Mix(Bits((float)d.Z));
                }
            return (h1, h2);
        }
        static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f == 0 ? 0f : f);   // -0 == 0

        /// 허용 오차 weld: 제어점 서명·컬러·탄젠트 w·블렌드쉐이프 노멀 델타가 같고
        /// 노멀·탄젠트가 2.75° 안, UV 가 0.001 안이면 먼저 나온 정점 하나로 합친다 (값도 그 정점 것)
        static List<int> ToleranceWeld(SimMesh mesh, List<int> vertexCp, int[] cpSig, List<Dictionary<int, Vec3>>? vertexDn)
        {
            int n = mesh.Vertices.Count;
            var reps = new Dictionary<string, List<int>>();
            var map = new int[n];
            var keep = new List<int>();
            var sb = new StringBuilder();
            static double Cos(Vec3 a, Vec3 b) { double la = a.Length, lb = b.Length; return la > 0 && lb > 0 ? Vec3.Dot(a, b) / (la * lb) : (la == lb ? 1 : 0); }
            bool UvClose(int a, int b)
            {
                foreach (var uv in mesh.Uvs)
                {
                    float du = (float)uv[a].u - (float)uv[b].u, dv = (float)uv[a].v - (float)uv[b].v;
                    if (du * du + dv * dv > WeldUv * WeldUv) return false;
                }
                return true;
            }
            for (int v = 0; v < n; v++)
            {
                sb.Clear().Append(cpSig[vertexCp[v]]).Append('|');
                if (mesh.Colors != null) foreach (var c in mesh.Colors[v]) sb.Append((float)c).Append(',');
                if (mesh.Tangents != null) sb.Append('w').Append(mesh.Tangents[v][3]);
                if (vertexDn != null) { var (a, b) = DnHash(vertexDn, v); sb.Append('|').Append(a).Append(',').Append(b); }
                var key = sb.ToString();
                if (!reps.TryGetValue(key, out var list)) reps[key] = list = new List<int>();
                int hit = -1;
                foreach (int r in list)
                {
                    if (Cos(mesh.Normals[r], mesh.Normals[v]) < WeldCos || !UvClose(r, v)) continue;
                    if (mesh.Tangents != null)
                    {
                        var ta = mesh.Tangents[r]; var tb = mesh.Tangents[v];
                        if (Cos(new Vec3(ta[0], ta[1], ta[2]), new Vec3(tb[0], tb[1], tb[2])) < WeldCos) continue;
                    }
                    hit = r; break;
                }
                if (hit >= 0) { map[v] = map[hit]; continue; }
                list.Add(v);
                map[v] = keep.Count;
                keep.Add(v);
            }
            if (keep.Count == n) return vertexCp;
            foreach (var sub in mesh.Submeshes)
                for (int k = 0; k < sub.Count; k++) sub[k] = map[sub[k]];
            mesh.Vertices = keep.Select(v => mesh.Vertices[v]).ToList();
            mesh.Normals = keep.Select(v => mesh.Normals[v]).ToList();
            mesh.Uvs = mesh.Uvs.Select(uv => keep.Select(v => uv[v]).ToList()).ToList();
            if (mesh.Colors != null) mesh.Colors = keep.Select(v => mesh.Colors[v]).ToList();
            if (mesh.Tangents != null) mesh.Tangents = keep.Select(v => mesh.Tangents[v]).ToList();
            if (vertexDn != null)
                for (int k = 0; k < vertexDn.Count; k++)
                {
                    var m = new Dictionary<int, Vec3>();
                    foreach (var kv in vertexDn[k]) if (keep[map[kv.Key]] == kv.Key) m[map[kv.Key]] = kv.Value;   // 대표 정점 값 (합쳐진 정점끼리는 같다)
                    vertexDn[k] = m;
                }
            return keep.Select(v => vertexCp[v]).ToList();
        }

        /// MikkTSpace 탄젠트를 코너마다 계산하고 (정점, 탄젠트)가 같은 코너끼리만 정점을 공유하게 다시 만든다
        static List<int> SplitByTangents(SimMesh mesh, List<int> vertexCp, List<int[]> faces, List<List<(int face, int corner)>> subRefs)
        {
            var (tan, offsets) = MikkTSpace.Generate(faces,
                i => ((float)mesh.Vertices[i].X, (float)mesh.Vertices[i].Y, (float)mesh.Vertices[i].Z),
                i => ((float)mesh.Normals[i].X, (float)mesh.Normals[i].Y, (float)mesh.Normals[i].Z),
                i => ((float)mesh.Uvs[0][i].u, (float)mesh.Uvs[0][i].v));
            var split = new Dictionary<(int, float, float, float, float), int>();
            var verts = new List<Vec3>(); var nrms = new List<Vec3>(); var cps = new List<int>();
            var uvs = mesh.Uvs.Select(_ => new List<(double, double)>()).ToList();
            var cols = mesh.Colors != null ? new List<double[]>() : null;
            var tangents = new List<float[]>();
            for (int si = 0; si < mesh.Submeshes.Count; si++)
                for (int k = 0; k < mesh.Submeshes[si].Count; k++)
                {
                    var sub = mesh.Submeshes[si];
                    int v = sub[k];
                    var (fc, cn) = subRefs[si][k];
                    int corner = offsets[fc] + cn;
                    var key = (v, tan[corner * 4], tan[corner * 4 + 1], tan[corner * 4 + 2], tan[corner * 4 + 3]);
                    if (!split.TryGetValue(key, out var nv))
                    {
                        nv = verts.Count;
                        split[key] = nv;
                        verts.Add(mesh.Vertices[v]); nrms.Add(mesh.Normals[v]); cps.Add(vertexCp[v]);
                        for (int u = 0; u < uvs.Count; u++) uvs[u].Add(mesh.Uvs[u][v]);
                        cols?.Add(mesh.Colors![v]);
                        tangents.Add(new[] { key.Item2, key.Item3, key.Item4, key.Item5 });
                    }
                    sub[k] = nv;
                }
            mesh.Vertices = verts; mesh.Normals = nrms; mesh.Uvs = uvs; mesh.Colors = cols; mesh.Tangents = tangents;
            return cps;
        }

        sealed class LayerReader
        {
            public string Mapping = "", Reference = "";
            public double[] Data = Array.Empty<double>();
            public int[]? Index;
            public int Width;

            public double[]? Get(int polygon, int polygonVertex, int controlPoint)
            {
                int i = Mapping switch
                {
                    "ByPolygonVertex" => polygonVertex,
                    "ByVertice" or "ByVertex" or "ByControlPoint" => controlPoint,
                    "ByPolygon" => polygon,
                    "AllSame" => 0,
                    _ => polygonVertex,
                };
                if (Reference != "Direct" && Index != null) i = i < Index.Length ? Index[i] : -1;
                if (i < 0 || (i + 1) * Width > Data.Length) return null;
                var r = new double[Width];
                Array.Copy(Data, i * Width, r, 0, Width);
                return r;
            }
        }

        static LayerReader? Layer(FbxNode parent, string element, string data, string index, int width)
        {
            var e = parent.Find(element);
            return e == null ? null : Layer(e, data, index, width);
        }

        static LayerReader? Layer(FbxNode e, string data, string index, int width)
        {
            var d = e.Array<double>(data);
            if (d == null) return null;
            return new LayerReader
            {
                Mapping = e.Find("MappingInformationType")?.Props[0] as string ?? "",
                Reference = e.Find("ReferenceInformationType")?.Props[0] as string ?? "Direct",
                Data = d, Index = e.Array<int>(index), Width = width,
            };
        }

        struct VertexKey : IEquatable<VertexKey>
        {
            public int Cp;
            public double[]? N;
            public double[][] Uv;
            public double[]? Color;

            public bool Equals(VertexKey o)
            {
                if (Cp != o.Cp || !Same(N, o.N) || !Same(Color, o.Color) || Uv.Length != o.Uv.Length) return false;
                for (int i = 0; i < Uv.Length; i++) if (!Same(Uv[i], o.Uv[i])) return false;
                return true;
            }

            static bool Same(double[]? a, double[]? b)
            {
                if (a == null || b == null) return a == b;
                for (int i = 0; i < a.Length; i++) if ((float)a[i] != (float)b[i]) return false;   // Unity 는 float 로 비교
                return true;
            }

            public override bool Equals(object? obj) => obj is VertexKey k && Equals(k);
            public override int GetHashCode()
            {
                int h = Cp;
                if (N != null) foreach (var x in N) h = h * 31 + ((float)x).GetHashCode();
                foreach (var uv in Uv) foreach (var x in uv) h = h * 31 + ((float)x).GetHashCode();
                return h;
            }
        }
    }
}
