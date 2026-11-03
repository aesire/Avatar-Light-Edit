// 메시 편집(PLAN.md 3.9)의 Host 쪽: 편집 결과 정점·삼각형 수(퍼포먼스 등급), 정점 합치기 일괄 적용.
// 연산 적용은 렌더러와 같은 Protocol.MeshOps를 쓴다.
// 원본 메시: 블렌드쉐이프 페이지(깜빡임·입 맞추기·다문 입)가 Original로 부른 것만 계속 들고 있다 — 값을 바꿀 때마다 다시 쓰므로.
//   정점·삼각형 수를 세느라 읽은 것은 다 세면 버린다 (결과 숫자만 남김). 예전엔 정점 합치기 한 번에 모든 메시의 원본(블렌드쉐이프 델타 포함)을
//   계속 들고 있었다 (큰 시험 장면: 메시 70개 약 120 MB). 다시 셀 일은 대부분 백그라운드(RecountAll·DecimatePreview)라 화면이 멈추지 않는다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public sealed class MeshEdits
    {
        readonly Document _doc;
        readonly Dictionary<string, MeshData> _original = new Dictionary<string, MeshData>();
        readonly Dictionary<string, (string ops, int verts, int tris)> _counts = new Dictionary<string, (string, int, int)>();
        readonly object _lock = new object();   // 아바타·최적화 페이지가 백그라운드에서 미리 센다

        public MeshEdits(Document doc) { _doc = doc; }

        public static JNode WeldOp(float distance, bool keepSeams = true, bool keepShapes = true) =>
            JNode.Obj().Set("op", MeshOps.Weld).Set("distance", distance).Set("keepSeams", keepSeams).Set("keepShapes", keepShapes);

        readonly Dictionary<string, Task<MeshData?>> _loading = new Dictionary<string, Task<MeshData?>>();

        /// <summary>
        /// 원본 메시를 백그라운드에서 미리 읽기 시작한다 (Original로 들고 있을 것). 파일을 열 때 몸 메시를: IR을 읽자마자 시작해
        /// 페이지를 채우는 동안(UI 스레드)·렌더러가 장면을 불러오는 동안 같이 읽는다. Original이 그새 불리면 남은 만큼만 기다린다.
        /// </summary>
        public void Prefetch(string meshId)
        {
            lock (_lock)
            {
                if (_original.ContainsKey(meshId) || _loading.ContainsKey(meshId)) return;
                _loading[meshId] = Task.Run(() => Load(meshId));
            }
        }

        /// <summary>원본 메시 (버퍼가 없으면 null). 계속 들고 있다 — 자주 다시 쓰는 곳(블렌드쉐이프 페이지)만 부른다.</summary>
        public MeshData? Original(string meshId)
        {
            Task<MeshData?>? pending;
            lock (_lock)
            {
                if (_original.TryGetValue(meshId, out var d)) return d;
                _loading.TryGetValue(meshId, out pending);
            }
            if (pending != null)
            {
                MeshData? got = null;
                try { got = pending.Result; } catch (Exception) { }
                lock (_lock)
                {
                    _loading.Remove(meshId);
                    if (_original.TryGetValue(meshId, out var d)) return d;
                    if (got != null) { _original[meshId] = got; return got; }
                }
            }
            var loaded = Load(meshId);   // 읽는 동안 잠그지 않는다 (백그라운드에서 세는 동안 UI 스레드가 기다리지 않게)
            if (loaded == null) return null;
            lock (_lock)
            {
                if (_original.TryGetValue(meshId, out var d)) return d;   // 그새 다른 스레드가 읽었다
                _original[meshId] = loaded;
                return loaded;
            }
        }

        /// <summary>세기용 원본: 들고 있는 것이 있으면 그것, 없으면 새로 읽고 들고 있지 않는다.</summary>
        MeshData? Source(string meshId)
        {
            bool pending;
            lock (_lock)
            {
                if (_original.TryGetValue(meshId, out var d)) return d;
                pending = _loading.ContainsKey(meshId);
            }
            return pending ? Original(meshId) : Load(meshId);   // 미리 읽는 중이면 그것을 (어차피 들고 있을 것)
        }

        MeshData? Load(string meshId)
        {
            var m = _doc.Original.FindMesh(meshId);
            if (m == null || _doc.Original.BufferFile.Length == 0 || !System.IO.File.Exists(_doc.Original.BufferFile)) return null;
            using (var buf = new IrBuffers(_doc.Original.BufferFile)) return MeshData.Load(m, buf);
        }

        /// <summary>정점·삼각형 수를 바꾸는 연산이 있는지 (옮기기·노멀·블렌드쉐이프 연산만이면 IR 값 그대로 — 원본을 읽지 않는다)</summary>
        public static bool ChangesCounts(JNode ops)
        {
            foreach (var op in ops.Items)
                switch (op["op"].AsString)
                {
                    case MeshOps.Weld: case MeshOps.Merge: case MeshOps.Delete: case MeshOps.Face: case MeshOps.Decimate: return true;
                }
            return false;
        }

        /// <summary>편집 결과의 정점·삼각형 수. 정점 수를 바꾸는 편집이 없으면 IR 값 그대로 (버퍼를 읽지 않음).</summary>
        public (int verts, int tris) Counts(string meshId) => Counts(meshId, null);

        (int verts, int tris) Counts(string meshId, MeshData? src)
        {
            var m = _doc.Original.FindMesh(meshId);
            if (m == null) return (0, 0);
            var ops = _doc.Get(Addr.MeshEdit(meshId));
            if (!ChangesCounts(ops)) return (m.VertexCount, AvatarStats.TriangleCount(m));
            var key = ops.ToJson();
            lock (_lock) if (_counts.TryGetValue(meshId, out var c) && c.ops == key) return (c.verts, c.tris);
            var orig = src ?? Source(meshId);
            if (orig == null) return (m.VertexCount, AvatarStats.TriangleCount(m));
            var r = MeshOps.Apply(orig, ops);
            lock (_lock) _counts[meshId] = (key, r.VertexCount, r.Triangles);
            return (r.VertexCount, r.Triangles);
        }

        string? _recountKey;
        Task _recountTask = Task.CompletedTask;

        /// <summary>
        /// 모든 메시의 정점·삼각형 수(와 정점 줄이기 슬라이더의 "전체")를 백그라운드에서 센다. 아바타·최적화 페이지가 같은 편집에 함께 불러도 한 번만 센다.
        /// 메시마다 원본을 한 번만 읽어 둘 다 세고 버린다. 끝나면 Counts·BaseTris는 바로 답한다 (UI 스레드에서 읽거나 계산하지 않음).
        /// </summary>
        public Task RecountAll()
        {
            var ids = MeshIds().ToList();
            var key = string.Join("|", ids.Select(id => _doc.Get(Addr.MeshEdit(id)).ToJson()));
            lock (_lock)
            {
                if (key == _recountKey) return _recountTask;
                _recountKey = key;
                return _recountTask = Task.Run(() =>
                {
                    foreach (var id in ids)
                    {
                        lock (_lock) if (_recountKey != key) return;   // 그새 또 바뀌었다 (새 RecountAll이 센다)
                        if (!ChangesCounts(_doc.Get(Addr.MeshEdit(id)))) continue;
                        var src = Source(id);
                        if (src == null) continue;
                        Counts(id, src);
                        BaseTris(id, src);
                    }
                });
            }
        }

        /// <summary>렌더러가 쓰는 메시 (같은 메시를 여러 렌더러가 쓰면 한 번만).</summary>
        public IEnumerable<string> MeshIds(IEnumerable<IrRenderer>? renderers = null) =>
            (renderers ?? _doc.Original.Renderers).Select(r => r.Mesh).Where(id => _doc.Original.FindMesh(id) != null).Distinct();

        /// <summary>연산 하나를 메시 편집 목록 끝에 붙인다 (Undo 한 단계).</summary>
        public void Append(string meshId, JNode op, string label)
        {
            var list = _doc.Get(Addr.MeshEdit(meshId)).Clone();
            list.Add(op);
            _doc.Set(Addr.MeshEdit(meshId), list, label);
        }

        public static JNode DecimateOp(float ratio, bool keepShapes = true) =>
            JNode.Obj().Set("op", MeshOps.Decimate).Set("ratio", ratio).Set("keepShapes", keepShapes);
        public static JNode DecimateOpTris(int triangles, bool keepShapes = true) =>
            JNode.Obj().Set("op", MeshOps.Decimate).Set("triangles", Math.Max(1, triangles)).Set("keepShapes", keepShapes);

        static bool IsDec(JNode op) => op["op"].AsString == MeshOps.Decimate;

        /// <summary>목록 끝의 decimate를 op로 바꾼 목록 (끝이 decimate가 아니면 붙이고, op가 null이면 끝의 decimate를 뺀다). 슬라이더를 여러 번 움직여도 쌓이지 않게.</summary>
        public static JNode WithDecimate(JNode ops, JNode? op)
        {
            var list = JNode.Arr();
            int n = ops.Count;
            bool lastIsDec = n > 0 && IsDec(ops[n - 1]);
            for (int i = 0; i < (lastIsDec ? n - 1 : n); i++) list.Add(ops[i]);
            if (op != null) list.Add(op);
            return list;
        }
        public static JNode WithDecimate(JNode ops, float ratio, bool keepShapes) => WithDecimate(ops, ratio < 0.999f ? DecimateOp(ratio, keepShapes) : null);

        /// <summary>끝의 decimate 앞까지 적용한 삼각형 수 (슬라이더의 "전체").</summary>
        public int BaseTris(string meshId) => BaseTris(meshId, null);

        int BaseTris(string meshId, MeshData? src)
        {
            var m = _doc.Original.FindMesh(meshId);
            if (m == null) return 0;
            var ops = _doc.Get(Addr.MeshEdit(meshId));
            var before = WithDecimate(ops, null);
            if (!ChangesCounts(before)) return AvatarStats.TriangleCount(m);
            var key = "base:" + before.ToJson();
            lock (_lock) if (_counts.TryGetValue(meshId + "#base", out var c) && c.ops == key) return c.tris;
            var orig = src ?? Source(meshId);
            if (orig == null) return AvatarStats.TriangleCount(m);
            var r = MeshOps.Apply(orig, before);
            lock (_lock) _counts[meshId + "#base"] = (key, r.VertexCount, r.Triangles);
            return r.Triangles;
        }

        /// <summary>지금 목록 끝의 decimate가 남기는 삼각형 수 (없으면 null).</summary>
        public int? DecimateTarget(string meshId)
        {
            var ops = _doc.Get(Addr.MeshEdit(meshId));
            if (ops.Count == 0 || !IsDec(ops[ops.Count - 1])) return null;
            var op = ops[ops.Count - 1];
            return op.Has("triangles") ? op["triangles"].AsInt : (int)Math.Round(BaseTris(meshId) * op["ratio"].AsDouble);
        }

        /// <summary>지금 목록 끝의 decimate 비율 (없으면 1).</summary>
        public float DecimateRatio(string meshId)
        {
            var t = DecimateTarget(meshId);
            int b = BaseTris(meshId);
            return t == null || b == 0 ? 1f : Math.Min(1f, t.Value / (float)b);
        }

        /// <summary>
        /// 고른 메시들의 삼각형 합을 total로 맞추도록 메시마다 나눈다 (각자의 전체에 비례, 합 = total). 전체 이상이면 null(줄이지 않음).
        /// </summary>
        public Dictionary<string, int?> Distribute(IEnumerable<string> meshIds, int total)
        {
            var ids = meshIds.ToList();
            var bases = ids.ToDictionary(id => id, BaseTris);
            long sum = bases.Values.Sum(x => (long)x);
            var r = new Dictionary<string, int?>();
            if (sum == 0 || total >= sum) { foreach (var id in ids) r[id] = null; return r; }
            total = Math.Max(1, total);
            int given = 0;
            // 큰 메시부터 나누고 남는 것은 마지막에 (반올림 오차)
            var order = ids.OrderByDescending(id => bases[id]).ThenBy(id => id, StringComparer.Ordinal).ToList();
            for (int i = 0; i < order.Count; i++)
            {
                var id = order[i];
                int share = i == order.Count - 1 ? total - given : (int)Math.Round(total * (double)bases[id] / sum);
                share = Math.Max(0, Math.Min(bases[id], share));
                given += share;
                r[id] = share >= bases[id] ? (int?)null : Math.Max(1, share);
            }
            return r;
        }

        /// <summary>기록하지 않고 줄였을 때의 정점·삼각형 합 (슬라이더를 끄는 동안 보여 주기). 무거워 백그라운드에서 부른다.</summary>
        public (int verts, int tris) DecimatePreview(IDictionary<string, int?> targets, bool keepShapes, Func<bool>? cancelled = null)
        {
            int v = 0, t = 0;
            foreach (var kv in targets.ToList())
            {
                if (cancelled?.Invoke() == true) break;
                var orig = Source(kv.Key);
                if (orig == null) continue;
                var ops = WithDecimate(_doc.Get(Addr.MeshEdit(kv.Key)), kv.Value is int n ? DecimateOpTris(n, keepShapes) : null);
                var r = MeshOps.Apply(orig, ops);
                v += r.VertexCount; t += r.Triangles;
            }
            return (v, t);
        }
        public (int verts, int tris) DecimatePreview(IEnumerable<string> meshIds, float ratio, bool keepShapes, Func<bool>? cancelled = null) =>
            DecimatePreview(meshIds.ToDictionary(id => id, id => ratio < 0.999f ? (int?)Math.Max(1, (int)Math.Round(BaseTris(id) * ratio)) : null), keepShapes, cancelled);

        /// <summary>여러 메시의 정점 줄이기를 한 번에 (실행 취소 한 단계). 값이 null이면 그 메시의 정점 줄이기를 뺀다.</summary>
        public void SetDecimate(IDictionary<string, int?> targets, bool keepShapes, string? label = null)
        {
            int total = targets.Values.Sum(x => x ?? 0);
            using (_doc.Group(label ?? (targets.Values.Any(x => x != null) ? $"정점 줄이기 (삼각형 {total:N0})" : "정점 줄이기 끄기")))
                foreach (var kv in targets.ToList())
                    _doc.Set(Addr.MeshEdit(kv.Key), WithDecimate(_doc.Get(Addr.MeshEdit(kv.Key)), kv.Value is int n ? DecimateOpTris(n, keepShapes) : null));
        }
        public void SetDecimate(IEnumerable<string> meshIds, float ratio, bool keepShapes) =>
            SetDecimate(meshIds.ToDictionary(id => id, id => ratio < 0.999f ? (int?)Math.Max(1, (int)Math.Round(BaseTris(id) * ratio)) : null), keepShapes, ratio < 0.999f ? $"정점 줄이기 ({ratio * 100:0}%)" : null);

        /// <summary>
        /// 정점 합치기를 여러 메시에 한 번에 (Undo 한 단계). 줄어든 정점·삼각형 수를 돌려준다.
        /// 아무것도 줄지 않는 메시에는 연산을 붙이지 않는다 (목록이 쓸데없이 길어지지 않게).
        /// </summary>
        public (int verts, int tris) WeldAll(IEnumerable<string> meshIds, float distance, bool keepSeams, bool keepShapes)
        {
            int dv = 0, dt = 0;
            using (_doc.Group($"정점 합치기 ({distance * 1000:0.###} mm)"))
                foreach (var id in meshIds.ToList())
                {
                    var orig = Source(id);
                    if (orig == null) continue;
                    var ops = _doc.Get(Addr.MeshEdit(id));
                    var before = MeshOps.Apply(orig, ops);
                    var op = WeldOp(distance, keepSeams, keepShapes);
                    var after = before.Clone();
                    MeshOps.ApplyOne(after, op);
                    if (after.VertexCount == before.VertexCount && after.Triangles == before.Triangles) continue;
                    dv += before.VertexCount - after.VertexCount; dt += before.Triangles - after.Triangles;
                    var list = ops.Clone(); list.Add(op);
                    _doc.Set(Addr.MeshEdit(id), list);
                }
            return (dv, dt);
        }
    }
}
