// 정점 줄이기 (MeshOps decimate) 테스트
using System;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class CoreTests
{
    /// k×k 칸 격자 (z = 높이 함수). seamAt >= 0이면 그 열에서 UV를 끊어 정점을 따로 둔다 (이음매)
    static MeshData Grid(int k, Func<float, float, float> h, int seamAt = -1)
    {
        var pos = new System.Collections.Generic.List<float>(); var uv = new System.Collections.Generic.List<float>();
        var idx = new int[k + 1, k + 1]; var idx2 = new int[k + 1];
        int n = 0;
        for (int y = 0; y <= k; y++)
            for (int x = 0; x <= k; x++)
            {
                float fx = x / (float)k, fy = y / (float)k;
                pos.AddRange(new[] { fx, fy, h(fx, fy) }); uv.AddRange(new[] { fx, fy }); idx[x, y] = n++;
                if (x == seamAt) { pos.AddRange(new[] { fx, fy, h(fx, fy) }); uv.AddRange(new[] { fx + 0.5f, fy }); idx2[y] = n++; }
            }
        var tris = new System.Collections.Generic.List<int>();
        for (int y = 0; y < k; y++)
            for (int x = 0; x < k; x++)
            {
                int I(int xx, int yy) => xx == seamAt && x >= seamAt ? idx2[yy] : idx[xx, yy];
                tris.AddRange(new[] { I(x, y), I(x, y + 1), I(x + 1, y + 1), I(x, y), I(x + 1, y + 1), I(x + 1, y) });
            }
        var d = new MeshData { VertexCount = n, Pos = pos.ToArray() };
        d.Uv[0] = uv.ToArray();
        d.Subs.Add(new MeshData.Sub { Indices = tris.ToArray() });
        return d;
    }

    static void DecimateBasics()
    {
        var flat = Grid(16, (x, y) => 0);
        Eq(flat.Triangles, 512, "grid tris");
        var half = MeshOps.Apply(flat, JNode.Arr().Add(MeshEdits.DecimateOp(0.5f)));
        Check(half.Triangles <= 256 && half.Triangles > 200, $"decimate 50%: {half.Triangles} tris");
        Check(half.VertexCount < flat.VertexCount, $"vertices reduced {flat.VertexCount} → {half.VertexCount}");
        var tiny = MeshOps.Apply(flat, JNode.Arr().Add(MeshEdits.DecimateOp(0.02f)));
        Check(tiny.Triangles <= 16, $"flat plane collapses far: {tiny.Triangles} tris");
        // 평면의 모서리 네 점은 남는다 (가장자리 평면)
        bool Has(MeshData m, float x, float y) { for (int i = 0; i < m.VertexCount; i++) if (Math.Abs(m.Pos[i * 3] - x) < 1e-6 && Math.Abs(m.Pos[i * 3 + 1] - y) < 1e-6) return true; return false; }
        Check(Has(tiny, 0, 0) && Has(tiny, 1, 0) && Has(tiny, 0, 1) && Has(tiny, 1, 1), "plane corners kept");
        Check(Enumerable.Range(0, tiny.VertexCount).All(i => tiny.Pos[i * 3 + 2] == 0), "no new positions (endpoint collapse)");

        var bumpy = Grid(16, (x, y) => (float)(0.05 * Math.Sin(x * 12) * Math.Cos(y * 9)));
        var a = MeshOps.Apply(bumpy, JNode.Arr().Add(MeshEdits.DecimateOp(0.3f)));
        var b = MeshOps.Apply(bumpy, JNode.Arr().Add(MeshEdits.DecimateOp(0.3f)));
        Eq(string.Join(",", a.Subs[0].Indices), string.Join(",", b.Subs[0].Indices), "deterministic");
        Check(a.Triangles <= 154 && a.Triangles > 100, $"bumpy 30%: {a.Triangles}");

        // 이음매: 같은 자리 정점 두 벌이 각자 남거나 같이 없어진다 (한쪽만 접혀 찢어지지 않음)
        var seam = Grid(16, (x, y) => 0, seamAt: 8);
        var s = MeshOps.Apply(seam, JNode.Arr().Add(MeshEdits.DecimateOp(0.3f)));
        Check(s.Triangles < seam.Triangles * 0.4, $"seam mesh reduced: {s.Triangles}");
        var uvs = s.Uv[0]!;
        int left = 0, right = 0;
        for (int i = 0; i < s.VertexCount; i++) if (Math.Abs(s.Pos[i * 3] - 0.5f) < 1e-6) { if (uvs[i * 2] > 0.9f) right++; else left++; }
        Eq(left, right, "seam copies stay paired");

        // Host: 끝의 decimate는 바꿔 쓴다
        var ops = JNode.Arr().Add(MeshEdits.WeldOp(0.0001f));
        var w1 = MeshEdits.WithDecimate(ops, 0.5f, true);
        var w2 = MeshEdits.WithDecimate(w1, 0.3f, true);
        Eq(w2.Count, 2, "slider replaces trailing decimate"); Eq(w2[1]["ratio"].AsFloat, 0.3f, "new ratio");
        Eq(MeshEdits.WithDecimate(w2, 1f, true).Count, 1, "100% removes it");
    }
}

static partial class CoreTests
{
    static void DecimateIslands()
    {
        // 큰 평면 + 아주 작은 평면 조각 (머리핀처럼): 작은 조각도 자기 몫만큼 남는다
        var big = Grid(16, (x, y) => (float)(0.05 * Math.Sin(x * 12)));
        var small = Grid(4, (x, y) => (float)(0.0005 * Math.Sin(x * 12)));
        int nb = big.VertexCount;
        var pos = big.Pos.Concat(small.Pos.Select((v, i) => i % 3 == 2 ? v + 2f : v * 0.01f)).ToArray();
        var d = new MeshData { VertexCount = nb + small.VertexCount, Pos = pos };
        d.Uv[0] = big.Uv[0]!.Concat(small.Uv[0]!).ToArray();
        d.Subs.Add(new MeshData.Sub { Indices = big.Subs[0].Indices.Concat(small.Subs[0].Indices.Select(i => i + nb)).ToArray() });
        var r = MeshOps.Apply(d, JNode.Arr().Add(MeshEdits.DecimateOp(0.5f)));
        int smallLeft = 0; for (int i = 0; i < r.VertexCount; i++) if (r.Pos[i * 3 + 2] > 1f) smallLeft++;
        Check(smallLeft >= 4, $"small island survives ({smallLeft} vertices)");
        Check(r.Triangles <= d.Triangles / 2 + 2, $"overall ~50% ({r.Triangles} / {d.Triangles})");
    }
}

static partial class CoreTests
{
    static void DecimateToCount()
    {
        var bumpy = Grid(16, (x, y) => (float)(0.05 * Math.Sin(x * 12) * Math.Cos(y * 9)));
        var r3 = MeshOps.Apply(bumpy, JNode.Arr().Add(MeshEdits.DecimateOpTris(3)));
        Check(r3.Triangles <= 4 && r3.Triangles >= 1, $"down to ~3 triangles ({r3.Triangles})");
        var r100 = MeshOps.Apply(bumpy, JNode.Arr().Add(MeshEdits.DecimateOpTris(100)));
        Eq(r100.Triangles, 100, "exact triangle target");
        var full = MeshOps.Apply(bumpy, JNode.Arr().Add(MeshEdits.DecimateOpTris(9999)));
        Eq(full.Triangles, bumpy.Triangles, "target ≥ all = unchanged");
    }
}
