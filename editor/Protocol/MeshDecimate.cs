// 정점 줄이기 (decimate, PLAN.md 3.9): 모서리를 한쪽 끝 정점으로 접는(half-edge collapse) 방식으로 삼각형이 ratio 비율이 될 때까지 줄인다.
//   decimate { triangles | ratio, keepShapes=true }   남길 삼각형 수(triangles) 또는 비율(ratio, 0~1) — 그 앞 연산까지 적용한 메시 기준
// - 비용: 접히는 정점 묶음의 면 평면 오차(quadric)를 남는 정점 위치에서 잰 값. 메시 가장자리는 가장자리 평면을 더해 덜 움직이게.
// - 정점은 새로 만들지 않는다: 접힌 정점은 남는 정점으로 바뀌므로 UV·본 가중치·블렌드쉐이프는 남는 정점 것을 그대로 쓴다 (원본 값 무변환).
// - UV 이음매: 같은 자리의 정점 사본(이음매 양쪽)마다 같은 쪽 사본으로 가야 한다. 짝이 없으면 그 접기는 하지 않는다 → 이음매가 찢어지지 않는다.
// - keepShapes: 블렌드쉐이프 델타·본 가중치가 다른 정점끼리 접으면 비용을 더한다 (표정·자세에서 찢어지지 않게).
// - 면이 뒤집히는(법선이 크게 꺾이는) 접기는 하지 않는다. 같은 입력이면 같은 결과 (비용이 같으면 번호 순).
using System;
using System.Collections.Generic;

namespace VrcProto.Protocol
{
    public static partial class MeshOps
    {
        public const string Decimate = "decimate";

        sealed class Quadric
        {
            public double A, B, C, D, E, F, G, H, I, J; // 대칭 4×4 (aa ab ac ad bb bc bd cc cd dd)
            public void AddPlane(double a, double b, double c, double d, double w)
            {
                A += w * a * a; B += w * a * b; C += w * a * c; D += w * a * d;
                E += w * b * b; F += w * b * c; G += w * b * d;
                H += w * c * c; I += w * c * d; J += w * d * d;
            }
            public void Add(Quadric q) { A += q.A; B += q.B; C += q.C; D += q.D; E += q.E; F += q.F; G += q.G; H += q.H; I += q.I; J += q.J; }
            public double Eval(double x, double y, double z) =>
                A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x + E * y * y + 2 * F * y * z + 2 * G * y + H * z * z + 2 * I * z + J;
            public double EvalSum(Quadric o, double x, double y, double z) => Eval(x, y, z) + o.Eval(x, y, z);
        }

        /// <summary>최소 힙 (비용, 접히는 묶음, 남는 묶음, 두 묶음의 버전). 비용이 같으면 번호 순 → 결과가 늘 같다.</summary>
        sealed class Heap
        {
            struct E { public double Cost; public int U, V, VerU, VerV; }
            readonly List<E> _a = new List<E>();
            public int Count => _a.Count;
            static bool Less(E x, E y) => x.Cost < y.Cost || x.Cost == y.Cost && (x.U < y.U || x.U == y.U && x.V < y.V);
            public void Push(double cost, int u, int v, int vu, int vv)
            {
                _a.Add(new E { Cost = cost, U = u, V = v, VerU = vu, VerV = vv });
                int i = _a.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (!Less(_a[i], _a[p])) break; (_a[i], _a[p]) = (_a[p], _a[i]); i = p; }
            }
            public (double cost, int u, int v, int vu, int vv) Pop()
            {
                var top = _a[0];
                _a[0] = _a[_a.Count - 1]; _a.RemoveAt(_a.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < _a.Count && Less(_a[l], _a[m])) m = l;
                    if (r < _a.Count && Less(_a[r], _a[m])) m = r;
                    if (m == i) break;
                    (_a[i], _a[m]) = (_a[m], _a[i]); i = m;
                }
                return (top.Cost, top.U, top.V, top.VerU, top.VerV);
            }
        }

        /// <param name="triangles">남길 삼각형 수 (0보다 크면 ratio 대신)</param>
        public static void DoDecimate(MeshData d, float ratio, bool keepShapes, int triangles = 0)
        {
            if (triangles <= 0 && ratio >= 1 || d.VertexCount < 4) return;
            int n = d.VertexCount;
            var P = d.Pos;

            // 삼각형 (Triangles 서브메시만. Quads·Lines는 그대로 둔다)
            var tv = new List<int>(); var tsub = new List<int>();
            for (int s = 0; s < d.Subs.Count; s++)
            {
                if (d.Subs[s].Topology != "Triangles") continue;
                var ix = d.Subs[s].Indices;
                for (int i = 0; i + 2 < ix.Length; i += 3)
                {
                    if (ix[i] == ix[i + 1] || ix[i + 1] == ix[i + 2] || ix[i] == ix[i + 2]) continue;
                    tv.Add(ix[i]); tv.Add(ix[i + 1]); tv.Add(ix[i + 2]); tsub.Add(s);
                }
            }
            int T = tsub.Count;
            if (T == 0) return;
            var T3 = tv.ToArray();
            var alive = new bool[T]; for (int t = 0; t < T; t++) alive[t] = true;
            int aliveCount = T, target = triangles > 0 ? Math.Min(triangles, T) : Math.Max(1, (int)Math.Round(T * Math.Max(0, ratio)));
            if (target >= T) return;
            ratio = target / (float)T;

            // 같은 자리 정점 = 한 묶음 (이음매 사본)
            var group = new int[n]; var groupVerts = new List<List<int>>();
            var byPos = new Dictionary<(float, float, float), int>();
            for (int i = 0; i < n; i++)
            {
                var k = (P[i * 3], P[i * 3 + 1], P[i * 3 + 2]);
                if (!byPos.TryGetValue(k, out var g)) { g = groupVerts.Count; byPos[k] = g; groupVerts.Add(new List<int>()); }
                group[i] = g; groupVerts[g].Add(i);
            }
            int G = groupVerts.Count;
            var vtris = new List<int>[n];
            for (int t = 0; t < T; t++) for (int k = 0; k < 3; k++) (vtris[T3[t * 3 + k]] ??= new List<int>()).Add(t);

            // 면 평면 quadric + 가장자리 평면
            var Q = new Quadric[G]; for (int g = 0; g < G; g++) Q[g] = new Quadric();
            var edgeCount = new Dictionary<long, int>();
            long EK(int a, int b) => a < b ? (long)a * G + b : (long)b * G + a;
            for (int t = 0; t < T; t++)
            {
                int a = T3[t * 3], b = T3[t * 3 + 1], c = T3[t * 3 + 2];
                if (!Plane(P, a, b, c, out var nx, out var ny, out var nz, out var dd)) continue;
                foreach (var v in new[] { a, b, c }) Q[group[v]].AddPlane(nx, ny, nz, dd, 1);
                for (int k = 0; k < 3; k++)
                {
                    int ga = group[T3[t * 3 + k]], gb = group[T3[t * 3 + (k + 1) % 3]];
                    if (ga == gb) continue;
                    var key = EK(ga, gb); edgeCount[key] = edgeCount.TryGetValue(key, out var c0) ? c0 + 1 : 1;
                }
            }
            for (int t = 0; t < T; t++)
            {
                int a = T3[t * 3], b = T3[t * 3 + 1], c = T3[t * 3 + 2];
                if (!Plane(P, a, b, c, out var nx, out var ny, out var nz, out _)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int va = T3[t * 3 + k], vb = T3[t * 3 + (k + 1) % 3];
                    int ga = group[va], gb = group[vb];
                    if (ga == gb || edgeCount[EK(ga, gb)] != 1) continue;
                    // 모서리를 지나고 면에 수직인 평면
                    double ex = P[vb * 3] - P[va * 3], ey = P[vb * 3 + 1] - P[va * 3 + 1], ez = P[vb * 3 + 2] - P[va * 3 + 2];
                    double bx = ey * nz - ez * ny, by = ez * nx - ex * nz, bz = ex * ny - ey * nx;
                    double len = Math.Sqrt(bx * bx + by * by + bz * bz);
                    if (len < 1e-12) continue;
                    bx /= len; by /= len; bz /= len;
                    double bd = -(bx * P[va * 3] + by * P[va * 3 + 1] + bz * P[va * 3 + 2]);
                    Q[ga].AddPlane(bx, by, bz, bd, 10); Q[gb].AddPlane(bx, by, bz, bd, 10);
                }
            }

            // 떨어진 조각(머리핀·단추 같은 섬)마다 자기 몫(ratio × 조각의 삼각형 수)까지만 줄인다.
            // 오차가 절대값이라 작은 조각은 통째로 지우는 게 가장 싸서, 이게 없으면 작은 장식부터 사라진다
            var comp = new int[G]; for (int g = 0; g < G; g++) comp[g] = g;
            int Root(int x) { while (comp[x] != x) { comp[x] = comp[comp[x]]; x = comp[x]; } return x; }
            for (int t = 0; t < T; t++)
            {
                int r0 = Root(group[T3[t * 3]]);
                for (int k = 1; k < 3; k++) { int rk = Root(group[T3[t * 3 + k]]); if (rk != r0) comp[rk] = r0; }
            }
            var compAlive = new Dictionary<int, int>();
            for (int t = 0; t < T; t++) { int c = Root(group[T3[t * 3]]); compAlive[c] = compAlive.TryGetValue(c, out var x) ? x + 1 : 1; }
            var compFloor = new Dictionary<int, int>();
            // 몫이 삼각형 하나도 안 되는 조각은 없어질 수 있다 (슬라이더를 삼각형 몇 개까지 내릴 수 있게)
            foreach (var kv in compAlive) compFloor[kv.Key] = (int)Math.Floor(kv.Value * ratio + 1e-6);
            for (int g = 0; g < G; g++) comp[g] = Root(g);

            var ver = new int[G]; var dead = new bool[G];
            bool relaxed = false;   // 2차: 엄격한 규칙으로 목표에 못 미치면 이음매·뒤집힘 검사를 풀고 더 접는다 (슬라이더를 끝까지 내릴 수 있게)
            var heap = new Heap();
            var map = new Dictionary<int, int>();

            // 묶음 u의 사본마다 같은 면을 쓰는 v의 사본 (없으면 false)
            bool Mapping(int gu, int gv, Dictionary<int, int> m)
            {
                m.Clear();
                foreach (var a in groupVerts[gu])
                {
                    int best = -1;
                    if (vtris[a] != null)
                        foreach (var t in vtris[a])
                        {
                            if (!alive[t]) continue;
                            for (int k = 0; k < 3; k++) { int b = T3[t * 3 + k]; if (group[b] == gv && (best < 0 || b < best)) best = b; }
                        }
                    if (best < 0 && relaxed && HasAlive(vtris[a])) { best = int.MaxValue; foreach (var b in groupVerts[gv]) best = Math.Min(best, b); }
                    if (best < 0) { if (HasAlive(vtris[a])) return false; continue; }   // 면을 안 쓰는 사본은 상관없음
                    m[a] = best;
                }
                return m.Count > 0;
            }
            bool HasAlive(List<int>? l) { if (l == null) return false; foreach (var t in l) if (alive[t]) return true; return false; }

            // 접은 뒤 남는 면이 뒤집히지 않는지
            bool NoFlip(int gu, int gv, Dictionary<int, int> m)
            {
                if (relaxed) return true;
                foreach (var a in groupVerts[gu])
                {
                    if (vtris[a] == null) continue;
                    foreach (var t in vtris[a])
                    {
                        if (!alive[t]) continue;
                        int i0 = T3[t * 3], i1 = T3[t * 3 + 1], i2 = T3[t * 3 + 2];
                        if (group[i0] == gv || group[i1] == gv || group[i2] == gv) continue;   // 없어질 면
                        if (!Plane(P, i0, i1, i2, out var ox, out var oy, out var oz, out _)) continue;
                        int j0 = i0 == a ? m[a] : i0, j1 = i1 == a ? m[a] : i1, j2 = i2 == a ? m[a] : i2;
                        if (!Plane(P, j0, j1, j2, out var nx, out var ny, out var nz, out _)) return false;   // 넓이 0이 되는 면
                        if (ox * nx + oy * ny + oz * nz < 0.2) return false;
                    }
                }
                return true;
            }

            double Penalty(Dictionary<int, int> m)
            {
                if (!keepShapes) return 0;
                double pen = 0;
                foreach (var kv in m)
                {
                    int a = kv.Key, b = kv.Value;
                    double ex = P[a * 3] - P[b * 3], ey = P[a * 3 + 1] - P[b * 3 + 1], ez = P[a * 3 + 2] - P[b * 3 + 2];
                    double len2 = ex * ex + ey * ey + ez * ez;
                    foreach (var sh in d.Shapes)
                        foreach (var f in sh.Frames)
                        {
                            if (f.DPos == null) continue;
                            for (int k = 0; k < 3; k++) { double x = f.DPos[a * 3 + k] - f.DPos[b * 3 + k]; pen += x * x; }
                        }
                    if (d.BoneW != null && d.BoneIdx != null) pen += WeightDiff(d, a, b) * len2;
                }
                return pen;
            }

            // 면 안쪽(법선 반대)으로 꺼지는 접기는 10배 더 비싸게: 옷이 안으로 꺼지면 아래 몸이 뚫고 나온다 (밖으로 조금 부푸는 쪽이 낫다)
            double Inward(int gu, int pv)
            {
                double sum = 0;
                foreach (var a in groupVerts[gu])
                {
                    if (vtris[a] == null) continue;
                    double mx = P[pv * 3] - P[a * 3], my = P[pv * 3 + 1] - P[a * 3 + 1], mz = P[pv * 3 + 2] - P[a * 3 + 2];
                    foreach (var t in vtris[a])
                    {
                        if (!alive[t] || !Plane(P, T3[t * 3], T3[t * 3 + 1], T3[t * 3 + 2], out var nx, out var ny, out var nz, out _)) continue;
                        double din = -(mx * nx + my * ny + mz * nz);
                        if (din > 0) sum += 10 * din * din;
                    }
                }
                return sum;
            }

            bool Cost(int gu, int gv, out double cost)
            {
                cost = 0;
                if (!Mapping(gu, gv, map) || !NoFlip(gu, gv, map)) return false;
                int pv = map.Values.GetEnumerator() is var e && e.MoveNext() ? e.Current : groupVerts[gv][0];
                cost = Q[gu].EvalSum(Q[gv], P[pv * 3], P[pv * 3 + 1], P[pv * 3 + 2]) + Penalty(map) + Inward(gu, pv);
                return true;
            }

            void PushPair(int gu, int gv)
            {
                if (Cost(gu, gv, out var c)) heap.Push(c, gu, gv, ver[gu], ver[gv]);
            }

            for (int pass = 0; pass < 2 && aliveCount > target; pass++)
            {
                relaxed = pass == 1;
                heap = new Heap();
                var seen = new HashSet<long>();
                for (int t = 0; t < T; t++)
                {
                    if (!alive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int ga = group[T3[t * 3 + k]], gb = group[T3[t * 3 + (k + 1) % 3]];
                        if (ga == gb || !seen.Add(EK(ga, gb))) continue;
                        PushPair(ga, gb); PushPair(gb, ga);
                    }
                }
                var neigh = new HashSet<int>();
                while (aliveCount > target && heap.Count > 0)
                {
                    var (_, gu, gv, vu, vv) = heap.Pop();
                    if (dead[gu] || dead[gv] || ver[gu] != vu || ver[gv] != vv) continue;
                    if (!Mapping(gu, gv, map) || !NoFlip(gu, gv, map)) continue;   // 이웃이 접히며 바뀌었을 수 있다
                    int cc = comp[gu], lose = 0;
                    foreach (var a in groupVerts[gu])
                        if (vtris[a] != null)
                            foreach (var t in vtris[a])
                                if (alive[t] && (group[T3[t * 3]] == gv || group[T3[t * 3 + 1]] == gv || group[T3[t * 3 + 2]] == gv)) lose++;
                    if (compAlive[cc] - lose < compFloor[cc]) continue;   // 이 조각은 몫만큼 줄었다
                    compAlive[cc] -= lose;
                    // 접기: u 사본을 쓰는 면은 v 사본으로, u와 v를 같이 쓰는 면은 없어진다
                    foreach (var a in groupVerts[gu])
                    {
                        if (vtris[a] == null) continue;
                        foreach (var t in vtris[a])
                        {
                            if (!alive[t]) continue;
                            bool hasV = false;
                            for (int k = 0; k < 3; k++) if (group[T3[t * 3 + k]] == gv) hasV = true;
                            if (hasV) { alive[t] = false; aliveCount--; continue; }   // (compAlive는 위에서 뺐다)
                            int b = map[a];
                            for (int k = 0; k < 3; k++) if (T3[t * 3 + k] == a) T3[t * 3 + k] = b;
                            (vtris[b] ??= new List<int>()).Add(t);
                        }
                        vtris[a] = null;
                    }
                    dead[gu] = true;
                    Q[gv].Add(Q[gu]);
                    ver[gv]++;
                    // v와 이웃한 묶음의 접기 비용을 다시
                    neigh.Clear();
                    foreach (var b in groupVerts[gv])
                    {
                        if (vtris[b] == null) continue;
                        vtris[b]!.RemoveAll(t => !alive[t]);
                        foreach (var t in vtris[b]!) for (int k = 0; k < 3; k++) { int g = group[T3[t * 3 + k]]; if (g != gv) neigh.Add(g); }
                    }
                    // v의 quadric이 바뀌었으니 v가 낀 접기만 다시 잰다 (다른 접기의 비용은 그대로, 뒤집힘은 꺼낼 때 다시 확인)
                    foreach (var g in neigh) { PushPair(gv, g); PushPair(g, gv); }
                }
            }

            // 남은 면으로 서브메시를 다시 쓰고, 어떤 면도 쓰지 않게 된 정점은 지운다 (Quads·Lines가 쓰는 정점은 남김)
            var lists = new List<int>[d.Subs.Count];
            for (int s = 0; s < d.Subs.Count; s++) if (d.Subs[s].Topology == "Triangles") lists[s] = new List<int>();
            for (int t = 0; t < T; t++) if (alive[t]) { var l = lists[tsub[t]]; l.Add(T3[t * 3]); l.Add(T3[t * 3 + 1]); l.Add(T3[t * 3 + 2]); }
            for (int s = 0; s < d.Subs.Count; s++) if (lists[s] != null) d.Subs[s].Indices = lists[s].ToArray();
            var used = new bool[n];
            foreach (var s in d.Subs) foreach (var i in s.Indices) if (i >= 0 && i < n) used[i] = true;
            var remap = new int[n]; int count = 0;
            for (int i = 0; i < n; i++) remap[i] = used[i] ? count++ : -1;
            d.Remap(remap, count);
        }

        static double WeightDiff(MeshData d, int a, int b)
        {
            double diff = 0;
            for (int i = 0; i < 4; i++)
            {
                float wa = d.BoneW![a * 4 + i]; if (wa <= 0) continue;
                float wb = 0; for (int j = 0; j < 4; j++) if (d.BoneIdx![b * 4 + j] == d.BoneIdx[a * 4 + i]) wb += d.BoneW[b * 4 + j];
                diff += Math.Abs(wa - wb);
            }
            for (int j = 0; j < 4; j++)
            {
                float wb = d.BoneW![b * 4 + j]; if (wb <= 0) continue;
                bool found = false; for (int i = 0; i < 4; i++) if (d.BoneIdx![a * 4 + i] == d.BoneIdx[b * 4 + j] && d.BoneW[a * 4 + i] > 0) found = true;
                if (!found) diff += wb;
            }
            return diff * 100;   // 가중치가 다르면 모서리 길이²의 100배까지 (길게 늘어지는 접기를 피함)
        }

        /// 단위 법선과 평면 d (넓이가 0이면 false)
        static bool Plane(float[] P, int a, int b, int c, out double nx, out double ny, out double nz, out double dd)
        {
            double ux = P[b * 3] - P[a * 3], uy = P[b * 3 + 1] - P[a * 3 + 1], uz = P[b * 3 + 2] - P[a * 3 + 2];
            double vx = P[c * 3] - P[a * 3], vy = P[c * 3 + 1] - P[a * 3 + 1], vz = P[c * 3 + 2] - P[a * 3 + 2];
            nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-14) { dd = 0; return false; }
            nx /= l; ny /= l; nz /= l;
            dd = -(nx * P[a * 3] + ny * P[a * 3 + 1] + nz * P[a * 3 + 2]);
            return true;
        }
    }
}
