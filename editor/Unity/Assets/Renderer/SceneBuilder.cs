// IR → GameObject / Mesh / SkinnedMeshRenderer. 원본 데이터를 변환하지 않고 그대로 Unity 객체로 만든다.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed partial class SceneBuilder
    {
        public sealed class LoadResult { public JNode Materials = JNode.Arr(), Schemas = JNode.Obj(), Warnings = JNode.Arr(); }

        readonly MaterialFactory _materials;
        GameObject _root;
        readonly List<GameObject> _nodes = new List<GameObject>();
        readonly Dictionary<string, Renderer> _renderers = new Dictionary<string, Renderer>();
        readonly Dictionary<string, Dictionary<string, int>> _shapeIndex = new Dictionary<string, Dictionary<string, int>>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        // 메시 편집 (PLAN 3.9): 메시 id → Unity 메시·쓰는 렌더러, 편집한(또는 정점 편집 중인) 메시의 원본·현재 데이터
        readonly Dictionary<string, Mesh> _meshById = new Dictionary<string, Mesh>();
        readonly Dictionary<string, List<Renderer>> _meshUsers = new Dictionary<string, List<Renderer>>();
        readonly Dictionary<string, string> _rendererMesh = new Dictionary<string, string>();
        readonly Dictionary<string, MeshData> _origData = new Dictionary<string, MeshData>(), _curData = new Dictionary<string, MeshData>();
        // 정점 줄이기처럼 무거운 편집은 백그라운드에서 계산해 다음 프레임들에 반영한다 (최신 요청만)
        readonly Dictionary<string, int> _editVer = new Dictionary<string, int>();
        readonly ConcurrentQueue<(int gen, string id, int ver, MeshData data)> _ready = new ConcurrentQueue<(int, string, int, MeshData)>();
        int _gen;
        /// <summary>메시를 편집 결과로 다시 만든 뒤 (메시 id).</summary>
        public event Action<string> MeshChanged;
        readonly HashSet<Transform> _bones = new HashSet<Transform>();
        public IrScene Scene { get; private set; }

        public SceneBuilder(MaterialFactory materials) { _materials = materials; }

        public Bounds WorldBounds()
        {
            bool any = false; var b = new Bounds(Vector3.up, Vector3.one);
            foreach (var r in _renderers.Values)
            {
                if (r == null || !r.gameObject.activeInHierarchy || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>모든 렌더러(꺼 둔 것 포함)를 감싸는 범위. VRChat 퍼포먼스의 Bounds와 같은 기준.</summary>
        /// <summary>
        /// 아바타 범위: 보이는 렌더러만 (켜진 오브젝트·켜진 렌더러). 꺼 둔 스킨드 메시는 Unity가 스키닝하지 않아 범위가 실제와 달라서
        /// (예: 꺼 둔 핸들 메시가 바인드 자세 그대로 높이 278 m) 높이·등급·카메라를 망친다. 보이는 것이 없으면 전부.
        /// </summary>
        public Bounds AllBounds()
        {
            bool any = false; var b = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var visibleOnly in new[] { true, false })
            {
                foreach (var r in _renderers.Values)
                {
                    if (r == null || (visibleOnly && (!r.enabled || !r.gameObject.activeInHierarchy))) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (any) break;
            }
            return b;
        }

        public bool TryRendererBounds(string id, out Bounds b)
        {
            b = default;
            if (!_renderers.TryGetValue(id, out var r) || r == null) return false;
            b = r.bounds;
            return true;
        }

        /// <summary>스킨드 메시가 참조하는 본 (본 표시용).</summary>
        public HashSet<Transform> BoneTransforms() => _bones;

        /// <summary>장면 루트 (PhysBone 움직임 시험이 아바타 전체를 움직일 때).</summary>
        public Transform Root => _root != null ? _root.transform : null;
        public Transform NodeTransform(int id) => id >= 0 && id < _nodes.Count && _nodes[id] != null ? _nodes[id].transform : null;

        /// <summary>렌더러 전부 (PhysBone 다리 콜라이더 크기 재기, 커서 아래 표면 찾기).</summary>
        public IEnumerable<Renderer> Renderers => _renderers.Values;

        /// <summary>휴머노이드 본 Transform (IR humanoid 매핑, 없으면 null).</summary>
        public Transform HumanBone(string humanName) =>
            Scene != null && Scene.Humanoid.TryGetValue(humanName, out var id) ? NodeTransform(id) : null;

        public Transform FindHumanoidHead()
        {
            foreach (var n in _nodes) if (n != null && n.name.Equals("Head", StringComparison.OrdinalIgnoreCase)) return n.transform;
            return null;
        }

        /// <summary>스튜디오 소품을 내릴 때 (아바타 장면은 Load가 지우고 다시 만든다).</summary>
        public void Unload() => Clear();

        void Clear(bool keepTextures = false)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            foreach (var m in _meshes) UnityEngine.Object.Destroy(m);
            _meshes.Clear(); _nodes.Clear(); _renderers.Clear(); _shapeIndex.Clear(); _bones.Clear();
            _meshById.Clear(); _meshUsers.Clear(); _rendererMesh.Clear(); _origData.Clear(); _curData.Clear(); _editVer.Clear(); _blinkFixed.Clear(); _gen++;
            _materials.Clear(keepTextures);
            ClearOutfitRules();
            ClearFaceLayers();
        }

        /// <summary>마지막 Load의 단계별 시간 (ms): IR 읽기, 노드, 메시, 머티리얼·텍스처, 나머지.</summary>
        public double TParse, TNodes, TMesh, TMat, TRest;
        /// <summary>로드 진행 (단계, 한 수, 전체 수) — RenderServer가 Host에 loadProgress로 보낸다.</summary>
        public Action<string, int, int> LoadProgress;

        public LoadResult Load(string irPath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); double lap = 0;
            double Lap() { var now = sw.Elapsed.TotalMilliseconds; var d = now - lap; lap = now; return d; }
            TMesh = TMat = 0;
            Clear(keepTextures: true);   // 같은 텍스처는 다시 디코드하지 않는다 (아이템을 붙이고 떼어 IR을 다시 만들 때)
            var result = new LoadResult();
            void Warn(string s) { result.Warnings.Add(s); Debug.LogWarning("[VrcProto] " + s); }

            try { Scene = IrScene.Load(irPath); }
            catch (Exception e) { Warn("IR 읽기 실패: " + e.Message); return result; }
            TParse = Lap();
            LoadProgress?.Invoke("parse", 1, 1);
            // 텍스처는 메시를 만드는 동안 작업 스레드에서 미리 읽고 디코드한다 (TextureLoader.Prefetch).
            // 머티리얼은 기다리지 않고 부탁만 한다 — RenderServer.Load가 PhysBone·자세를 준비한 뒤 모두 기다린다 (TextureLoader.Flush)
            _materials.Textures.BeginScene();
            _materials.Textures.Prefetch(Scene.Textures);

            _root = new GameObject("Scene");
            foreach (var n in Scene.Nodes)
            {
                var go = new GameObject(n.Name);
                _nodes.Add(go);
            }
            for (int i = 0; i < Scene.Nodes.Count; i++)
            {
                var n = Scene.Nodes[i]; var t = _nodes[i].transform;
                t.SetParent(n.Parent >= 0 && n.Parent < _nodes.Count ? _nodes[n.Parent].transform : _root.transform, false);
                t.localScale = V3(n.Scale);
                // 씬에서 가져온 아바타는 루트가 씬 안 어딘가에 놓여 있다. VRChat처럼 루트의 위치·회전은 버리고 원점에 정면(+Z)으로 세운다 (크기는 유지).
                if (n.Parent < 0) continue;
                t.localPosition = V3(n.Position); t.localRotation = Q(n.Rotation);
            }
            for (int i = 0; i < Scene.Nodes.Count; i++) _nodes[i].SetActive(Scene.Nodes[i].Active);

            TNodes = Lap();
            _materials.Begin(Scene, Warn);
            IrBuffers buf = null;
            try
            {
                if (Scene.BufferFile.Length > 0) buf = new IrBuffers(Scene.BufferFile);
                var meshCache = new Dictionary<string, Mesh>();
                int done = 0;
                foreach (var r in Scene.Renderers)
                {
                    LoadProgress?.Invoke("renderers", done++, Scene.Renderers.Count);
                    if (r.Node < 0 || r.Node >= _nodes.Count) { Warn($"렌더러 {r.Id}: 노드 없음"); continue; }
                    var irMesh = Scene.FindMesh(r.Mesh);
                    Mesh mesh = null;
                    if (irMesh == null) Warn($"렌더러 {r.Id}: 메시 {r.Mesh} 없음");
                    else if (buf == null) Warn("버퍼 파일 없음: 메시를 만들 수 없음");
                    else if (!meshCache.TryGetValue(irMesh.Id, out mesh))
                    {
                        var ms = System.Diagnostics.Stopwatch.StartNew();
                        try { mesh = BuildMesh(irMesh, buf); meshCache[irMesh.Id] = mesh; _meshes.Add(mesh); _meshById[irMesh.Id] = mesh; }
                        catch (Exception e) { Warn($"메시 {irMesh.Name}: {e.Message}"); }
                        TMesh += ms.Elapsed.TotalMilliseconds;
                    }

                    var go = _nodes[r.Node];
                    var mats = new Material[r.Materials.Count];
                    var mt = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < mats.Length; i++) mats[i] = _materials.Get(r.Materials[i]);
                    TMat += mt.Elapsed.TotalMilliseconds;

                    Renderer ren;
                    if (r.Type == "MeshRenderer")
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        ren = go.AddComponent<MeshRenderer>();
                    }
                    else
                    {
                        var smr = go.AddComponent<SkinnedMeshRenderer>();
                        var bones = new Transform[r.Bones.Length];
                        for (int i = 0; i < bones.Length; i++) bones[i] = r.Bones[i] >= 0 && r.Bones[i] < _nodes.Count ? _nodes[r.Bones[i]].transform : null;
                        smr.bones = bones;
                        foreach (var bt in bones) if (bt != null) _bones.Add(bt);
                        smr.rootBone = r.RootBone >= 0 && r.RootBone < _nodes.Count ? _nodes[r.RootBone].transform : null;
                        smr.sharedMesh = mesh;
                        smr.updateWhenOffscreen = true; // 포즈·블렌드쉐이프로 범위가 바뀌어도 잘리지 않게
                        if (mesh != null)
                        {
                            for (int i = 0; i < r.BlendShapeWeights.Count && i < mesh.blendShapeCount; i++) smr.SetBlendShapeWeight(i, r.BlendShapeWeights[i]);
                            var idx = new Dictionary<string, int>();
                            for (int i = 0; i < mesh.blendShapeCount; i++) idx[mesh.GetBlendShapeName(i)] = i;
                            _shapeIndex[r.Id] = idx;
                        }
                        ren = smr;
                    }
                    ren.sharedMaterials = mats;
                    ren.enabled = r.Enabled;
                    _renderers[r.Id] = ren;
                    if (mesh != null)
                    {
                        _rendererMesh[r.Id] = irMesh.Id;
                        if (!_meshUsers.TryGetValue(irMesh.Id, out var users)) _meshUsers[irMesh.Id] = users = new List<Renderer>();
                        users.Add(ren);
                    }
                }
            }
            finally { buf?.Dispose(); }

            Lap();
            LoadProgress?.Invoke("renderers", Scene.Renderers.Count, Scene.Renderers.Count);
            InitOutfitRules();
            result.Materials = _materials.ReportAll(result.Schemas);
            _materials.Textures.EndScene();
            TRest = Lap();
            Debug.Log($"[VrcProto] 장면 로드: 노드 {Scene.Nodes.Count}, 렌더러 {_renderers.Count}, 메시 {_meshes.Count}");
            return result;
        }

        static Mesh BuildMesh(IrMesh m, IrBuffers buf)
        {
            var mesh = new Mesh { name = m.Name };
            // 블렌드쉐이프는 프레임마다 같은 버퍼로 읽어 바로 넣는다 (2026-10-20). 메시 하나의 프레임 전부를 정점 수 × 3 배열로 한꺼번에 들고 있으면
            // 큰 얼굴 메시에서 수백 MB가 되고, Unity Mono GC는 커진 힙을 돌려주지 않는다. 결과 메시는 같다
            Fill(mesh, MeshData.Load(m, buf, shapes: false));
            AddShapes(mesh, m, buf);
            if (m.BoundsCenter != null && m.BoundsExtents != null) mesh.bounds = new Bounds(V3(m.BoundsCenter), V3(m.BoundsExtents) * 2);
            mesh.UploadMeshData(false);
            return mesh;
        }

        static void AddShapes(Mesh mesh, IrMesh m, IrBuffers buf)
        {
            int n = m.VertexCount;
            if (m.BlendShapes.Count == 0 || n == 0) return;
            Vector3[] dp = new Vector3[n], dn = null, dt = null;
            // Vector3 배열을 float로 보고 파일에서 바로 읽는다 (2026-10-23: 예전엔 바이트 → float[] → 정점마다 Vector3로 두 번 옮겼다).
            // 정점 수보다 긴 버퍼(이상한 IR)는 앞부분만, 짧으면 나머지는 0
            void Read(BufRef r, Vector3[] v)
            {
                var span = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(v.AsSpan());
                if (r.Count < span.Length) span.Clear();
                buf.ReadFloats(r, span);
            }
            foreach (var bs in m.BlendShapes)
                foreach (var fr in bs.Frames)
                {
                    if (fr.DeltaPos.IsEmpty) Array.Clear(dp, 0, n); else Read(fr.DeltaPos, dp);
                    if (!fr.DeltaNormal.IsEmpty) Read(fr.DeltaNormal, dn ??= new Vector3[n]);
                    if (!fr.DeltaTangent.IsEmpty) Read(fr.DeltaTangent, dt ??= new Vector3[n]);
                    mesh.AddBlendShapeFrame(bs.Name, fr.Weight, dp, fr.DeltaNormal.IsEmpty ? null : dn, fr.DeltaTangent.IsEmpty ? null : dt);
                }
        }

        /// <summary>MeshData를 Unity 메시로 (원본 무변환: 값 그대로). 범위는 정점으로 다시 잰다.</summary>
        static void Fill(Mesh mesh, MeshData d)
        {
            int n = d.VertexCount;
            mesh.indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(ToV3(d.Pos, n));
            if (d.Nrm != null) mesh.SetNormals(ToV3(d.Nrm, n));
            if (d.Tan != null) mesh.SetTangents(ToV4(d.Tan, n));
            if (d.Col != null)
            {
                var c = d.Col; var cols = new Color[n];
                for (int i = 0; i < n; i++) cols[i] = new Color(c[i * 4], c[i * 4 + 1], c[i * 4 + 2], c[i * 4 + 3]);
                mesh.SetColors(cols);
            }
            for (int ch = 0; ch < 4; ch++)
            {
                var f = d.Uv[ch];
                if (f == null) continue;
                int dim = d.UvDims[ch];
                if (dim == 2) { var uv = new Vector2[n]; for (int i = 0; i < n; i++) uv[i] = new Vector2(f[i * 2], f[i * 2 + 1]); mesh.SetUVs(ch, uv); }
                else if (dim == 3) mesh.SetUVs(ch, ToV3(f, n));
                else mesh.SetUVs(ch, ToV4(f, n));
            }
            if (d.BoneW != null && d.BoneIdx != null)
            {
                var bi = d.BoneIdx; var bw = d.BoneW;
                var w = new BoneWeight[n];
                for (int i = 0; i < n; i++)
                    w[i] = new BoneWeight
                    {
                        boneIndex0 = bi[i * 4], boneIndex1 = bi[i * 4 + 1], boneIndex2 = bi[i * 4 + 2], boneIndex3 = bi[i * 4 + 3],
                        weight0 = bw[i * 4], weight1 = bw[i * 4 + 1], weight2 = bw[i * 4 + 2], weight3 = bw[i * 4 + 3],
                    };
                mesh.boneWeights = w;
            }
            if (d.Bindposes != null)
            {
                var f = d.Bindposes; var bp = new Matrix4x4[f.Length / 16];
                for (int i = 0; i < bp.Length; i++) { var mm = new Matrix4x4(); for (int k = 0; k < 16; k++) mm[k] = f[i * 16 + k]; bp[i] = mm; }
                mesh.bindposes = bp;
            }
            mesh.subMeshCount = d.Subs.Count;
            for (int s = 0; s < d.Subs.Count; s++)
            {
                var topo = d.Subs[s].Topology == "Lines" ? MeshTopology.Lines : d.Subs[s].Topology == "Quads" ? MeshTopology.Quads : MeshTopology.Triangles;
                mesh.SetIndices(d.Subs[s].Indices, topo, s, false);
            }
            foreach (var bs in d.Shapes)
                foreach (var fr in bs.Frames)
                {
                    var dp = fr.DPos == null ? new Vector3[n] : ToV3(fr.DPos, n);
                    var dn = fr.DNrm == null ? null : ToV3(fr.DNrm, n);
                    var dt = fr.DTan == null ? null : ToV3(fr.DTan, n);
                    mesh.AddBlendShapeFrame(bs.Name, fr.Weight, dp, dn, dt);
                }
            mesh.RecalculateBounds();
            if (d.Nrm == null) mesh.RecalculateNormals();
        }

        // ---------------- 메시 편집 (PLAN 3.9) ----------------
        public string MeshIdOf(string renderer) => renderer != null && _rendererMesh.TryGetValue(renderer, out var id) ? id : null;
        public Renderer RendererOf(string id) => id != null && _renderers.TryGetValue(id, out var r) ? r : null;
        public string RendererId(Renderer ren) { foreach (var kv in _renderers) if (kv.Value == ren) return kv.Key; return null; }

        MeshData Original(string meshId)
        {
            if (_origData.TryGetValue(meshId, out var d)) return d;
            var m = Scene?.FindMesh(meshId);
            if (m == null || Scene.BufferFile.Length == 0) return null;
            using (var buf = new IrBuffers(Scene.BufferFile)) d = MeshData.Load(m, buf);
            _origData[meshId] = d;
            return d;
        }

        /// <summary>메시의 현재 데이터 (편집 결과). 정점 편집 모드가 읽기만 한다.</summary>
        public MeshData CurrentData(string meshId)
        {
            if (meshId == null) return null;
            if (_curData.TryGetValue(meshId, out var d)) return d;
            d = Original(meshId);
            if (d != null) _curData[meshId] = d;
            return d;
        }

        readonly Dictionary<string, HashSet<string>> _blinkFixed = new Dictionary<string, HashSet<string>>();
        /// <summary>이 렌더러 메시의 블렌드쉐이프가 깜빡임 맞추기(Blink Fix) 대상인지 — 이미 눈 편집을 뺀 감기라 트래킹이 따로 빼지 않는다.</summary>
        public bool BlinkFixed(string renderer, string shape) { var m = MeshIdOf(renderer); return m != null && _blinkFixed.TryGetValue(m, out var s) && s.Contains(shape); }

        /// <summary>진단: 렌더러의 지금 블렌드쉐이프 값 중 조건에 맞는 것 (이름, 값).</summary>
        public IEnumerable<(string, float)> CurrentWeights(string renderer, Func<string, bool> pick)
        {
            if (!(_renderers.TryGetValue(renderer, out var r) && r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) yield break;
            var m = smr.sharedMesh;
            for (int i = 0; i < m.blendShapeCount; i++)
            {
                float w = smr.GetBlendShapeWeight(i);
                if (Mathf.Abs(w) > 1f && pick(m.GetBlendShapeName(i))) yield return (m.GetBlendShapeName(i), w);
            }
        }

        /// <summary>블렌드쉐이프가 주로 옆(x)으로 움직이는지 — Face_Custom의 eye_close처럼 이름은 눈 감기지만 눈 사이 거리를 바꾸는 편집 (MeshOps.IsClosingShape와 같은 기준).</summary>
        public bool MovesSideways(string renderer, string shape)
        {
            if (!(_renderers.TryGetValue(renderer, out var r) && r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) return false;
            var m = smr.sharedMesh;
            int i = m.GetBlendShapeIndex(shape), f = i < 0 ? -1 : m.GetBlendShapeFrameCount(i) - 1;
            if (f < 0) return false;
            var d = new Vector3[m.vertexCount];
            m.GetBlendShapeFrameVertices(i, f, d, null, null);
            double side = 0, all = 0;
            foreach (var v in d) { side += Mathf.Abs(v.x); all += Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z); }
            return all > 0 && side / all >= 0.5;
        }

        /// <summary>원본에 ops를 적용한 결과로 메시를 다시 만든다 (멱등). 블렌드쉐이프 가중치는 렌더러마다 그대로 둔다.</summary>
        public void SetMeshEdits(string meshId, JNode ops)
        {
            if (!_meshById.TryGetValue(meshId, out var mesh)) return;
            HashSet<string> fixedSet = null;
            foreach (var o in ops.Items)
                if (o["op"].AsString == MeshOps.BlinkFix && !MeshOps.IsOff(o))
                    foreach (var t in o["targets"].Items) (fixedSet ??= new HashSet<string>()).Add(t.AsString);
            if (fixedSet != null) _blinkFixed[meshId] = fixedSet; else _blinkFixed.Remove(meshId);
            if (ops.Count == 0 && !_curData.ContainsKey(meshId)) return;   // 편집한 적도 읽은 적도 없는 메시: 이미 원본
            var orig = Original(meshId);
            if (orig == null) return;
            int ver = _editVer[meshId] = (_editVer.TryGetValue(meshId, out var v0) ? v0 : 0) + 1;
            if (ops.Items.Any(o => o["op"].AsString == MeshOps.Decimate))
            {
                var copy = ops.Clone(); int gen = _gen;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { _ready.Enqueue((gen, meshId, ver, MeshOps.Apply(orig, copy))); }
                    catch (Exception e) { Debug.LogException(e); }
                });
                return;
            }
            Commit(meshId, ops.Count == 0 ? orig : MeshOps.Apply(orig, ops));
        }

        /// <summary>백그라운드에서 끝난 편집 결과를 반영한다 (RenderServer.Update가 매 프레임 부름). 그 사이 더 새 요청이 왔으면 버린다.</summary>
        public void PumpMeshEdits()
        {
            while (_ready.TryDequeue(out var r))
                if (r.gen == _gen && _editVer.TryGetValue(r.id, out var v) && v == r.ver) Commit(r.id, r.data);
        }

        void Commit(string meshId, MeshData d)
        {
            if (!_meshById.TryGetValue(meshId, out var mesh)) return;
            _curData[meshId] = d;
            var users = _meshUsers.TryGetValue(meshId, out var u) ? u : new List<Renderer>();
            var weights = new List<float[]>();
            foreach (var ren in users)
            {
                var smr = ren as SkinnedMeshRenderer;
                var w = new float[smr != null ? mesh.blendShapeCount : 0];
                for (int i = 0; i < w.Length; i++) w[i] = smr.GetBlendShapeWeight(i);
                weights.Add(w);
            }
            mesh.Clear();
            Fill(mesh, d);
            mesh.UploadMeshData(false);
            for (int k = 0; k < users.Count; k++)
            {
                if (users[k] is SkinnedMeshRenderer smr)
                {
                    smr.sharedMesh = mesh;
                    for (int i = 0; i < weights[k].Length && i < mesh.blendShapeCount; i++) smr.SetBlendShapeWeight(i, weights[k][i]);
                }
                else if (users[k].GetComponent<MeshFilter>() is MeshFilter mf) mf.sharedMesh = mesh;
            }
            MeshChanged?.Invoke(meshId);
        }

        /// <summary>정점 끌기·브러시 중 미리보기: 기록 전에 위치만 바꿔 보인다 (메시 공간). null이면 현재 데이터로 되돌림.</summary>
        public void PreviewPositions(string meshId, float[] pos)
        {
            if (!_meshById.TryGetValue(meshId, out var mesh)) return;
            var d = CurrentData(meshId);
            if (d == null) return;
            var p = pos ?? d.Pos;
            if (p.Length != mesh.vertexCount * 3) return;
            mesh.SetVertices(ToV3(p, mesh.vertexCount));
            mesh.RecalculateBounds();
        }

        public void SetBlendShape(string renderer, string name, float w)
        {
            if (!(_renderers.TryGetValue(renderer, out var r) && r is SkinnedMeshRenderer smr && _shapeIndex.TryGetValue(renderer, out var idx) && idx.TryGetValue(name, out var i))) return;
            if (_base.TryGetValue(renderer, out var b) && i < b.Length) b[i] = w;   // 표정 레이어 밑의 사용자 값
            if (!SetUserBlendShape(renderer, i, w)) smr.SetBlendShapeWeight(i, LayerValue(renderer, i) ?? w);
        }

        public void SetNodeActive(int node, bool active)
        {
            if (node < 0 || node >= _nodes.Count) return;
            _nodes[node].SetActive(active);
            ApplyOutfitRules();   // 의상을 켜고 끄면 Shape Changer가 바뀐다
        }
        public void SetRendererEnabled(string id, bool on) { if (_renderers.TryGetValue(id, out var r)) r.enabled = on; }

        /// <summary>노드 하나의 로컬 위치·회전·크기 (IR 부모 기준, [위치3, 회전4, 크기3]). 핸들로 옮긴 아이템.</summary>
        public void SetNodeTransform(int node, float[] trs)
        {
            if (node < 0 || node >= _nodes.Count || trs == null || trs.Length < 10) return;
            var t = _nodes[node].transform;
            t.localPosition = new Vector3(trs[0], trs[1], trs[2]);
            t.localRotation = new Quaternion(trs[3], trs[4], trs[5], trs[6]);
            t.localScale = new Vector3(trs[7], trs[8], trs[9]);
        }

        /// <summary>아바타 전체의 자리 (장면 루트, [위치3, 회전4]). 불러오면 원점에 정면.</summary>
        public void SetAvatarTransform(float[] trs)
        {
            if (_root == null || trs == null || trs.Length < 7) return;
            _root.transform.SetPositionAndRotation(new Vector3(trs[0], trs[1], trs[2]), new Quaternion(trs[3], trs[4], trs[5], trs[6]));
        }

        static Vector3 V3(float[] f) => new Vector3(f[0], f[1], f[2]);
        static Quaternion Q(float[] f) => new Quaternion(f[0], f[1], f[2], f[3]);
        static Vector3[] ToV3(float[] f, int n) { var r = new Vector3[n]; for (int i = 0; i < n; i++) r[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]); return r; }
        static Vector4[] ToV4(float[] f, int n) { var r = new Vector4[n]; for (int i = 0; i < n; i++) r[i] = new Vector4(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]); return r; }
    }
}
