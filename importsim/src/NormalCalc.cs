using System;
using System.Collections.Generic;

namespace ImportSim
{
    /// <summary>
    /// 블렌드쉐이프 노멀 Calculate 모드의 근사:
    /// 변형된 위치로 노멀을 다시 계산(면적·각도 가중, 스무딩 각도 이내로 이어진 면끼리만 평균)하고
    /// 임포트한 기본 노멀과의 차이를 델타로 쓴다(셰이프가 움직이지 않은 곳도). 정점은 기본 위치가 같으면 같은 위치 묶음으로 본다.
    /// 스무딩 묶음은 기본 형상의 면 각도로 한 번 정하고, 합은 변형 위치로 구한다.
    /// Unity 결과와의 오차: 중앙값 약 0.008, 90% 약 0.05 (Unity 델타 크기 중앙값 약 0.09).
    /// </summary>
    public sealed class NormalCalc
    {
        readonly int[] tri;          // 삼각형 정점 인덱스 (3개씩)
        readonly int[] group;        // 정점 -> 위치 묶음
        readonly int groups;
        readonly List<(int face, int corner)>[] around;   // 묶음 -> (면, 코너)
        readonly Dictionary<long, List<int>> edgeFaces = new Dictionary<long, List<int>>();
        readonly double cosThreshold;
        readonly IReadOnlyList<Vec3> basePositions;
        /// 기본 형상에서 정한 스무딩 묶음 (묶음 g -> 면 -> 대표 면). 처음 쓸 때 만든다
        // 묶음별 면 나누기 캐시: 블렌드쉐이프 프레임마다 병렬로 ComputeCorners를 부르므로 여러 스레드에서 채운다 (같은 값이라 겹쳐 써도 된다)
        readonly System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<int, int>> basePartition = new System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<int, int>>();

        public NormalCalc(IReadOnlyList<Vec3> basePositions, IEnumerable<List<int>> submeshes, double smoothAngleDeg)
        {
            var t = new List<int>();
            foreach (var s in submeshes) t.AddRange(s);
            tri = t.ToArray();
            this.basePositions = basePositions;
            cosThreshold = Math.Cos(smoothAngleDeg * Math.PI / 180) - 1e-7;

            var ids = new Dictionary<(long, long, long), int>();
            group = new int[basePositions.Count];
            for (int i = 0; i < group.Length; i++)
            {
                var p = basePositions[i];
                var k = ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5), (long)Math.Round(p.Z * 1e5));
                if (!ids.TryGetValue(k, out var g)) { g = ids.Count; ids[k] = g; }
                group[i] = g;
            }
            groups = ids.Count;
            around = new List<(int, int)>[groups];
            for (int f = 0; f < tri.Length / 3; f++)
                for (int c = 0; c < 3; c++)
                {
                    int g = group[tri[f * 3 + c]];
                    (around[g] ??= new List<(int, int)>()).Add((f, c));
                    int g2 = group[tri[f * 3 + (c + 1) % 3]];
                    long key = Math.Min(g, g2) * (long)groups + Math.Max(g, g2);
                    if (!edgeFaces.TryGetValue(key, out var l)) edgeFaces[key] = l = new List<int>();
                    l.Add(f);
                }
        }

        Vec3 FaceNormal(IReadOnlyList<Vec3> p, int f) => Vec3.Cross(p[tri[f * 3 + 1]] - p[tri[f * 3]], p[tri[f * 3 + 2]] - p[tri[f * 3]]);

        /// 묶음 g 주변 면을 기본 형상에서 스무딩 각도 이내로 이어진 것끼리 묶는다 (면 -> 대표 면)
        Dictionary<int, int> Partition(int g, List<(int face, int corner)> list)
        {
            if (basePartition.TryGetValue(g, out var rootOf)) return rootOf;
            var parent = new Dictionary<int, int>();
            foreach (var (f, _) in list) parent[f] = f;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var (f, c) in list)
            {
                var fu = FaceNormal(basePositions, f).Normalized;
                for (int k = 1; k <= 2; k++)
                {
                    int g2 = group[tri[f * 3 + (c + k) % 3]];
                    long key = Math.Min(g, g2) * (long)groups + Math.Max(g, g2);
                    foreach (var f2 in edgeFaces[key])
                        if (f2 != f && parent.ContainsKey(f2) && Vec3.Dot(fu, FaceNormal(basePositions, f2).Normalized) >= cosThreshold)
                            parent[Find(f)] = Find(f2);
                }
            }
            rootOf = new Dictionary<int, int>();
            foreach (var (f, _) in list) rootOf[f] = Find(f);
            return basePartition[g] = rootOf;
        }

        /// 코너(삼각형 f 의 c 번째 = f*3+c) 단위 노멀: 그 면이 속한 스무딩 묶음의 합만 쓴다 (Unity 는 코너가 다르면 정점을 나눈다)
        public Dictionary<int, Vec3> ComputeCorners(IReadOnlyList<Vec3> positions, HashSet<int> onlyGroups)
        {
            var result = new Dictionary<int, Vec3>();
            var sums = new Dictionary<int, Vec3>();
            foreach (var g in onlyGroups)
            {
                var list = around[g];
                if (list == null) continue;
                var rootOf = Partition(g, list);
                sums.Clear();
                foreach (var (f, c) in list)
                {
                    var v = positions[tri[f * 3 + c]];
                    var e1 = (positions[tri[f * 3 + (c + 1) % 3]] - v).Normalized;
                    var e2 = (positions[tri[f * 3 + (c + 2) % 3]] - v).Normalized;
                    double ang = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(e1, e2))));
                    int r = rootOf[f];
                    sums[r] = (sums.TryGetValue(r, out var s) ? s : new Vec3(0, 0, 0)) + FaceNormal(positions, f) * ang;
                }
                foreach (var (f, c) in list) result[f * 3 + c] = sums[rootOf[f]].Normalized;
            }
            return result;
        }

        /// 모든 위치 묶음 (Unity 는 셰이프마다 메시 전체의 노멀을 다시 계산한다)
        public HashSet<int> AllGroups() { var r = new HashSet<int>(); for (int g = 0; g < groups; g++) r.Add(g); return r; }
    }
}
