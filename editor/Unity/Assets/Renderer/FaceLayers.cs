// 표정 레이어 (미리보기 전용): Host가 보낸 블렌드쉐이프 값(사용자 값) 위에 표정(Addr.Expression → setExpression)과
// 페이셜 트래킹 값(FaceTracker), 블렌드쉐이프 단축키 값(Addr.ShapeKeys → setExpression layer=keys)을 덮어 보인다.
// 우선순위: 단축키 > 트래킹 > 표정 > 사용자 값 (홍조·눈동자 모양처럼 방송 중 누른 것이 트래킹에 덮이지 않게).
// 레이어가 처음 덮는 렌더러는 그때의 값을 사용자 값으로 기억해 두고(_base), 레이어가 빠지면 그 값으로 되돌린다.
// 의상 규칙(Shape Changer·Blendshape Sync) 대상 렌더러는 규칙 쪽 사용자 값(_userWeights)을 그대로 쓰고, 규칙을 적용할 때 레이어를 끼워 넣는다.
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed partial class SceneBuilder
    {
        public const int ExpressionLayer = 0, TrackingLayer = 1, KeyLayer = 2;
        readonly Dictionary<string, float[]> _base = new Dictionary<string, float[]>();
        readonly Dictionary<string, Dictionary<int, float>>[] _layers = { new Dictionary<string, Dictionary<int, float>>(), new Dictionary<string, Dictionary<int, float>>(), new Dictionary<string, Dictionary<int, float>>() };

        void ClearFaceLayers() { _base.Clear(); foreach (var l in _layers) l.Clear(); }

        /// <summary>레이어 값 (단축키 → 트래킹 → 표정 순). 없으면 null.</summary>
        float? LayerValue(string id, int i)
        {
            for (int l = KeyLayer; l >= ExpressionLayer; l--)
                if (_layers[l].TryGetValue(id, out var d) && d.TryGetValue(i, out var v)) return v;
            return null;
        }

        /// <summary>의상 규칙을 적용할 때: 사용자 값 사본 위에 레이어 값을 덮는다.</summary>
        void OverlayLayers(string id, float[] w)
        {
            foreach (var layer in _layers)
                if (layer.TryGetValue(id, out var d))
                    foreach (var kv in d) if (kv.Key < w.Length) w[kv.Key] = kv.Value;
        }

        /// <summary>
        /// 레이어 하나를 통째로 바꾼다. values = (렌더러 id, 블렌드쉐이프 이름, 값 0~100). 없는 렌더러·이름은 건너뛰고, 건너뛴 수를 돌려준다.
        /// null이나 빈 목록이면 그 레이어를 지운다.
        /// </summary>
        public int SetLayer(int layer, IEnumerable<(string Renderer, string Shape, float Weight)> values)
        {
            var next = new Dictionary<string, Dictionary<int, float>>();
            int missing = 0;
            if (values != null)
                foreach (var (id, shape, w) in values)
                {
                    if (!(_shapeIndex.TryGetValue(id, out var idx) && idx.TryGetValue(shape, out var i))) { missing++; continue; }
                    if (!next.TryGetValue(id, out var d)) next[id] = d = new Dictionary<int, float>();
                    d[i] = w;
                }
            var touched = new HashSet<string>(_layers[layer].Keys);
            foreach (var id in next.Keys) { touched.Add(id); RememberBase(id); }
            var old = _layers[layer];
            _layers[layer] = next;
            bool rules = false;
            foreach (var id in touched)
            {
                if (_userWeights.ContainsKey(id)) { rules = true; continue; }
                if (!(_renderers.TryGetValue(id, out var r) && r is SkinnedMeshRenderer smr) || !_base.TryGetValue(id, out var b)) continue;
                var idxs = new HashSet<int>();
                if (old.TryGetValue(id, out var o)) idxs.UnionWith(o.Keys);
                if (next.TryGetValue(id, out var n)) idxs.UnionWith(n.Keys);
                foreach (var i in idxs) if (i < b.Length) smr.SetBlendShapeWeight(i, LayerValue(id, i) ?? b[i]);
            }
            if (rules) ApplyOutfitRules();
            return missing;
        }

        /// <summary>레이어가 처음 덮기 전의 값(= 사용자 값)을 기억한다. 의상 규칙 대상은 규칙 쪽 값이 원천이라 기억하지 않는다.</summary>
        void RememberBase(string id)
        {
            if (_base.ContainsKey(id) || _userWeights.ContainsKey(id)) return;
            if (!(_renderers.TryGetValue(id, out var r) && r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) return;
            var b = new float[smr.sharedMesh.blendShapeCount];
            for (int i = 0; i < b.Length; i++) b[i] = LayerValue(id, i) == null ? smr.GetBlendShapeWeight(i) : 0;
            _base[id] = b;
        }

        /// <summary>트래킹 레이어가 없을 때의 값 (표정 레이어 → 사용자 값). 페이셜 트래킹 애니메이터가 가중치 1이 못 되는 몫을 이 값으로 채운다.</summary>
        public float BelowTracking(string id, string shape)
        {
            if (!(_shapeIndex.TryGetValue(id, out var idx) && idx.TryGetValue(shape, out var i))) return 0;
            if (_layers[ExpressionLayer].TryGetValue(id, out var e) && e.TryGetValue(i, out var ev)) return ev;
            if (_userWeights.TryGetValue(id, out var uw) && i < uw.Length) return uw[i];
            if (_base.TryGetValue(id, out var b) && i < b.Length) return b[i];
            return _renderers.TryGetValue(id, out var r) && r is SkinnedMeshRenderer smr ? smr.GetBlendShapeWeight(i) : 0;
        }

        /// <summary>렌더러의 블렌드쉐이프 이름 목록 (트래킹 대상 고르기용).</summary>
        public IEnumerable<string> ShapeNames(string id) => _shapeIndex.TryGetValue(id, out var idx) ? idx.Keys : (IEnumerable<string>)new string[0];
        public IEnumerable<string> SkinnedIds()
        {
            foreach (var kv in _renderers) if (kv.Value is SkinnedMeshRenderer && _shapeIndex.ContainsKey(kv.Key)) yield return kv.Key;
        }
    }
}
