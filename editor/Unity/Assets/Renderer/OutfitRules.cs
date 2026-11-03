// 의상 규칙 (IR outfitRules, importsim이 Modular Avatar 설정에서 읽은 것):
//   Shape Changer: 그 오브젝트가 켜져 있으면(Inverted면 꺼져 있으면) 대상 메시의 블렌드쉐이프를 바꾼다.
//     set    = 값을 정한다 (몸을 줄이는 블렌드쉐이프 등)
//     delete = 그 블렌드쉐이프가 움직이는 정점(델타 > threshold)이 하나라도 든 삼각형을 그리지 않는다 (옷 아래로 뚫고 나오는 몸)
//   Blendshape Sync: 의상 메시의 블렌드쉐이프가 몸 메시의 값을 따른다 (가슴 크기 등).
// Host가 보낸 블렌드쉐이프 값(사용자 값)은 따로 기억하고, 오브젝트 켜고 끄기·블렌드쉐이프가 바뀔 때마다 규칙을 다시 적용한다.
// 지울 면이 생긴 메시는 렌더러마다 복사본을 만들어 인덱스만 바꾼다 (같은 메시를 쓰는 다른 렌더러는 그대로).
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed partial class SceneBuilder
    {
        readonly Dictionary<string, float[]> _userWeights = new Dictionary<string, float[]>();   // 렌더러 → 사용자 값 (규칙 대상만)
        readonly Dictionary<string, string> _deleted = new Dictionary<string, string>();         // 렌더러 → 지금 지운 블렌드쉐이프 목록 (키)
        readonly Dictionary<string, (Mesh Source, Mesh Copy)> _deleteMeshes = new Dictionary<string, (Mesh, Mesh)>();

        void ClearOutfitRules()
        {
            foreach (var m in _deleteMeshes.Values) if (m.Copy != null) Object.Destroy(m.Copy);
            _userWeights.Clear(); _deleted.Clear(); _deleteMeshes.Clear();
        }

        bool HasOutfitRules => Scene != null && (Scene.ShapeChanges.Count > 0 || Scene.BlendShapeSyncs.Count > 0);

        void InitOutfitRules()
        {
            if (!HasOutfitRules) return;
            void Track(string id)
            {
                if (_userWeights.ContainsKey(id) || !(_renderers.TryGetValue(id, out var r) && r is SkinnedMeshRenderer smr && smr.sharedMesh != null)) return;
                var w = new float[smr.sharedMesh.blendShapeCount];
                for (int i = 0; i < w.Length; i++) w[i] = smr.GetBlendShapeWeight(i);
                _userWeights[id] = w;
            }
            foreach (var c in Scene.ShapeChanges) Track(c.Renderer);
            foreach (var s in Scene.BlendShapeSyncs) { Track(s.Renderer); Track(s.Source); }
            ApplyOutfitRules();
        }

        /// <summary>사용자 값을 기억해 두고 규칙을 다시 적용한다. 규칙 대상이 아니면 false (그냥 바로 넣는다).</summary>
        bool SetUserBlendShape(string renderer, int index, float w)
        {
            if (!_userWeights.TryGetValue(renderer, out var arr) || index >= arr.Length) return false;
            arr[index] = w;
            ApplyOutfitRules();
            return true;
        }

        void ApplyOutfitRules()
        {
            if (!HasOutfitRules) return;
            var eff = new Dictionary<string, float[]>();
            foreach (var kv in _userWeights) { eff[kv.Key] = (float[])kv.Value.Clone(); OverlayLayers(kv.Key, eff[kv.Key]); }   // 표정·트래킹 (FaceLayers)
            var deletes = new Dictionary<string, List<(int Shape, float Threshold)>>();

            foreach (var c in Scene.ShapeChanges)
            {
                if (!c.Enabled || c.Node < 0 || c.Node >= _nodes.Count || _nodes[c.Node] == null) continue;
                if (_nodes[c.Node].activeInHierarchy == c.Inverted) continue;
                if (!eff.TryGetValue(c.Renderer, out var w) || !_shapeIndex.TryGetValue(c.Renderer, out var idx) || !idx.TryGetValue(c.Shape, out var i)) continue;
                if (c.Delete)
                {
                    if (!deletes.TryGetValue(c.Renderer, out var l)) deletes[c.Renderer] = l = new List<(int, float)>();
                    l.Add((i, c.Threshold));
                }
                else w[i] = c.Value;
            }
            foreach (var s in Scene.BlendShapeSyncs)
            {
                if (!eff.TryGetValue(s.Renderer, out var w) || !eff.TryGetValue(s.Source, out var src)) continue;
                if (_shapeIndex.TryGetValue(s.Renderer, out var idx) && idx.TryGetValue(s.Shape, out var i)
                    && _shapeIndex.TryGetValue(s.Source, out var sidx) && sidx.TryGetValue(s.SourceShape, out var si))
                    w[i] = src[si];
            }
            // 메시를 바꾼 뒤에 값을 넣는다
            foreach (var id in _userWeights.Keys)
            {
                deletes.TryGetValue(id, out var l);
                var key = l == null ? "" : string.Join(",", l.ConvertAll(x => x.Shape + ":" + x.Threshold));
                _deleted.TryGetValue(id, out var was);
                if ((was ?? "") == key) continue;
                _deleted[id] = key;
                ApplyDelete(id, l);
            }
            foreach (var kv in eff)
                if (_renderers.TryGetValue(kv.Key, out var r) && r is SkinnedMeshRenderer smr)
                    for (int i = 0; i < kv.Value.Length; i++) smr.SetBlendShapeWeight(i, kv.Value[i]);
        }

        /// 지울 블렌드쉐이프가 움직이는 정점이 든 삼각형을 뺀 인덱스로 바꾼다 (없으면 원래 메시로)
        void ApplyDelete(string id, List<(int Shape, float Threshold)> shapes)
        {
            if (!(_renderers.TryGetValue(id, out var r) && r is SkinnedMeshRenderer smr)) return;
            if (!_deleteMeshes.TryGetValue(id, out var pair))
            {
                if (shapes == null) return;
                pair = (smr.sharedMesh, Object.Instantiate(smr.sharedMesh));
                pair.Copy.name = pair.Source.name + " (지운 면)";
                _deleteMeshes[id] = pair;
            }
            if (shapes == null) { smr.sharedMesh = pair.Source; return; }

            var src = pair.Source;
            int n = src.vertexCount;
            var gone = new bool[n];
            var dv = new Vector3[n];
            foreach (var (shape, th) in shapes)
            {
                int frames = src.GetBlendShapeFrameCount(shape);
                if (frames == 0) continue;
                src.GetBlendShapeFrameVertices(shape, frames - 1, dv, null, null);
                for (int i = 0; i < n; i++) if (dv[i].sqrMagnitude > th * th) gone[i] = true;
            }
            var copy = pair.Copy;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                if (src.GetTopology(s) != MeshTopology.Triangles) continue;
                var ind = src.GetIndices(s);
                var keep = new List<int>(ind.Length);
                for (int t = 0; t + 2 < ind.Length; t += 3)
                    if (!gone[ind[t]] && !gone[ind[t + 1]] && !gone[ind[t + 2]]) { keep.Add(ind[t]); keep.Add(ind[t + 1]); keep.Add(ind[t + 2]); }
                copy.SetIndices(keep, MeshTopology.Triangles, s, false);
            }
            smr.sharedMesh = copy;
        }
    }
}
