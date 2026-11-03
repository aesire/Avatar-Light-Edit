// 다문 입 선 다듬기: mouth_straight처럼 입을 다물어 선으로 만드는 블렌드쉐이프에서, 선(입 선 텍스처를 입힌 가는 띠)의 두께를
// 양 끝으로 갈수록 줄이고 끝을 살짝 올려, 끝이 뭉툭하게 두꺼운 선 대신 가늘어지는 곡선으로 만든다. 그 블렌드쉐이프의 델타만 바꾼다.
//   shapeMove { shape, points: [x, y, z, dx, dy, dz, …], tol = 0.00001, enabled = true }   enabled: false면 값은 남기고 적용하지 않는다 (토글)
//     원래 위치가 (x, y, z)에서 tol 안인 정점마다 그 블렌드쉐이프 델타에 (dx, dy, dz)를 더한다 (프레임이 여럿이면 프레임 가중치 비율만큼).
//     정점 번호 대신 위치로 찾으므로 UV 경계에서 갈라진 같은 자리 정점이 함께 움직이고, 올리기에서 Unity 메시에 바로 적용된다.
// 띠 찾기 (MakeMouthLine): 그 블렌드쉐이프가 움직이는 정점이 대부분(90% 이상)인 UV 섬(삼각형으로 이어진 정점 묶음) — 피부처럼 넓은 섬은 빠진다.
//   블렌드쉐이프 100에서의 띠 위치로 x마다 가운데 선을 구하고, 가운데에서 끝으로 가며 두께 비율을 center → ends로 (taper부터 smoothstep),
//   끝을 curve만큼 올린다 (t² — 가운데 0, 끝 curve). round > 0이면 끝 마개를 가로로도 줄여 둥근 끝으로 남긴다.
//   Blink · Mouth Fix 플러그인(Core/FaceFix.cs MouthLine)과 같은 계산.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcProto.Protocol
{
    public static partial class MeshOps
    {
        public const string ShapeMove = "shapeMove";

        /// <summary>꺼 둔 다듬기 (enabled: false — 값은 남기고 적용하지 않음).</summary>
        public static bool IsOff(JNode op) => op.Has("enabled") && !op["enabled"].AsBool;

        static void DoShapeMove(MeshData d, JNode op)
        {
            if (IsOff(op)) return;
            var sh = d.Shapes.Find(s => s.Name == op["shape"].AsString);
            if (sh == null || sh.Frames.Count == 0) return;
            var p = op["points"].ToFloatArray();
            float tol = op.Has("tol") ? op["tol"].AsFloat : 1e-5f;
            int n = d.VertexCount;
            // 위치 격자 (칸 = tol): 이웃 칸까지 본다
            var grid = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Cell(float x, float y, float z) => ((long)Math.Floor(x / tol), (long)Math.Floor(y / tol), (long)Math.Floor(z / tol));
            for (int v = 0; v < n; v++)
            {
                var c = Cell(d.Pos[v * 3], d.Pos[v * 3 + 1], d.Pos[v * 3 + 2]);
                if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
                l.Add(v);
            }
            var last = sh.Frames[sh.Frames.Count - 1];
            for (int i = 0; i + 5 < p.Length; i += 6)
            {
                var (cx, cy, cz) = Cell(p[i], p[i + 1], p[i + 2]);
                for (long a = cx - 1; a <= cx + 1; a++)
                    for (long b = cy - 1; b <= cy + 1; b++)
                        for (long c = cz - 1; c <= cz + 1; c++)
                        {
                            if (!grid.TryGetValue((a, b, c), out var l)) continue;
                            foreach (var v in l)
                            {
                                float dx = d.Pos[v * 3] - p[i], dy = d.Pos[v * 3 + 1] - p[i + 1], dz = d.Pos[v * 3 + 2] - p[i + 2];
                                if (dx * dx + dy * dy + dz * dz > tol * tol) continue;
                                foreach (var f in sh.Frames)
                                {
                                    float k = last.Weight == 0 ? 1f : f.Weight / last.Weight;
                                    var o = f.DPos ??= new float[n * 3];
                                    o[v * 3] += k * p[i + 3]; o[v * 3 + 1] += k * p[i + 4]; o[v * 3 + 2] += k * p[i + 5];
                                }
                            }
                        }
            }
        }

        /// <summary>
        /// 다문 입 선 다듬기 연산을 만든다. ends·center = 끝·가운데의 두께 비율 (1 = 그대로), curve = 끝을 올리는 높이 (메시 단위, 0.0002 = 0.2mm).
        /// taper = 가늘어지기 시작하는 곳 (가운데 0 ~ 끝 1, 기본 0.25 — 낮출수록 길고 완만하게),
        /// round = 끝 둥글기 (0~1). 선 끝은 정점 여러 개로 된 둥근 마개인데, 위아래로만 누르면 마개가 접혀 가시처럼 뾰족해진다.
        ///         round만큼 마개를 가로로도 같은 비율로 줄여, 작고 둥근 끝으로 남긴다 (1 = 원래 둥근 모양 그대로 작게).
        /// 위쪽 축은 메시에서 고른다 (UpAxis: 축을 돌리지 않고 가져온 Z-up 메시는 z). 띠를 찾지 못하면 null.
        /// </summary>
        public static JNode? MakeMouthLine(MeshData d, string shape, float ends = 0.3f, float center = 1f, float curve = 0.0002f, float taper = 0.25f, float round = 0f)
        {
            taper = Math.Min(0.95f, Math.Max(0f, taper));
            round = Math.Min(1f, Math.Max(0f, round));
            var sh = d.Shapes.Find(s => s.Name == shape);
            if (sh == null || sh.Frames.Count == 0 || sh.Frames[sh.Frames.Count - 1].DPos is not float[] dp) return null;
            var band = LineBand(d, dp);
            if (band.Count < 4) return null;
            int up = UpAxis(d);
            // 같은 자리는 하나로 (UV 경계에서 갈라진 정점). FY·FX = 블렌드쉐이프 100에서의 위쪽·가로 위치
            var pts = new List<(float X, float Y, float Z, float FY, float FX)>();
            var seen = new HashSet<(long, long, long)>();
            foreach (var v in band)
            {
                var key = ((long)Math.Round(d.Pos[v * 3] * 1e6), (long)Math.Round(d.Pos[v * 3 + 1] * 1e6), (long)Math.Round(d.Pos[v * 3 + 2] * 1e6));
                if (seen.Add(key)) pts.Add((d.Pos[v * 3], d.Pos[v * 3 + 1], d.Pos[v * 3 + 2], d.Pos[v * 3 + up] + dp[v * 3 + up], d.Pos[v * 3] + dp[v * 3]));
            }
            float minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), xc = (minX + maxX) / 2, w = Math.Max(1e-6f, (maxX - minX) / 2);
            float win = w * 0.12f;
            (float Lo, float Hi) Span(float x)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var r in pts) if (Math.Abs(r.X - x) <= win) { lo = Math.Min(lo, r.FY); hi = Math.Max(hi, r.FY); }
                return (lo, hi);
            }
            // 끝 마개 (블렌드쉐이프 100에서): 끝 x와 반지름 (끝 조금 안쪽의 반 두께) → 마개 가운데
            float CapCenter(int side)
            {
                float tip = side > 0 ? pts.Max(q => q.FX) : pts.Min(q => q.FX);
                var inner = pts.Where(q => Math.Sign(q.X - xc) == side).OrderBy(q => Math.Abs(q.FX - (tip - side * win))).First();
                var (lo, hi) = Span(inner.X);
                return tip - side * Math.Max(0f, (hi - lo) / 2);
            }
            float capL = round > 0 ? CapCenter(-1) : 0, capR = round > 0 ? CapCenter(1) : 0;
            var outp = JNode.Arr();
            foreach (var q in pts)
            {
                // 이 x 둘레 띠의 위·아래 (블렌드쉐이프 100에서) → 가운데 선
                var (lo, hi) = Span(q.X);
                float mid = (lo + hi) / 2;
                float t = Math.Min(1f, Math.Abs(q.X - xc) / w);
                float e = Smooth(taper, 1f, t);
                float scale = center + (ends - center) * e;
                float target = mid + (q.FY - mid) * scale + curve * t * t;
                float dUp = target - q.FY;
                float dx = 0;
                if (round > 0)
                {
                    // 마개 안의 정점은 가로로도 마개 가운데 쪽으로 같은 비율만큼 (round만큼 섞어서)
                    int side = q.X >= xc ? 1 : -1;
                    float cap = side > 0 ? capR : capL;
                    if ((q.FX - cap) * side > 0) dx = (q.FX - cap) * (scale - 1) * round;
                }
                if (Math.Abs(dUp) < 1e-8f && Math.Abs(dx) < 1e-8f) continue;
                foreach (var x in new[] { q.X, q.Y, q.Z, dx, up == 1 ? dUp : 0f, up == 2 ? dUp : 0f }) outp.Add(x);
            }
            return JNode.Obj().Set("op", ShapeMove).Set("shape", shape).Set("points", outp).Set("tol", 1e-5f)
                .Set("tidy", JNode.Obj().Set("ends", ends).Set("center", center).Set("curve", curve).Set("taper", taper).Set("round", round));
        }

        /// <summary>메시의 위쪽 축 (1 = y, 2 = z): 높이가 더 긴 쪽 (FBX에서 축을 돌리지 않고 가져온 Z-up 메시).</summary>
        public static int UpAxis(MeshData d)
        {
            float y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int v = 0; v < d.VertexCount && v * 3 + 2 < d.Pos.Length; v++)
            {
                y0 = Math.Min(y0, d.Pos[v * 3 + 1]); y1 = Math.Max(y1, d.Pos[v * 3 + 1]);
                z0 = Math.Min(z0, d.Pos[v * 3 + 2]); z1 = Math.Max(z1, d.Pos[v * 3 + 2]);
            }
            return z1 - z0 > (y1 - y0) * 1.2f ? 2 : 1;
        }

        static float Smooth(float a, float b, float x) { float t = Math.Min(1f, Math.Max(0f, (x - a) / (b - a))); return t * t * (3 - 2 * t); }

        /// <summary>블렌드쉐이프가 움직이는 정점이 90% 이상인 UV 섬의 정점 (입 선 띠·입 안처럼 그 블렌드쉐이프로만 움직이는 작은 섬).</summary>
        static List<int> LineBand(MeshData d, float[] dp) => LineBand(d, dp, IslandRoots(d));

        static List<int> LineBand(MeshData d, float[] dp, int[] root)
        {
            int n = d.VertexCount;
            bool Moved(int v) => Math.Abs(dp[v * 3]) + Math.Abs(dp[v * 3 + 1]) + Math.Abs(dp[v * 3 + 2]) > 1e-7f;
            // 섬마다 (정점 수, 움직인 정점 수) — 대표 정점 번호로 센다 (사전 대신 배열, 2026-10-27)
            var all = new int[n]; var moved = new int[n]; var mv = new bool[n];
            for (int v = 0; v < n; v++) { var r = root[v]; all[r]++; if (mv[v] = Moved(v)) moved[r]++; }
            var list = new List<int>();
            for (int v = 0; v < n; v++) { var r = root[v]; if (mv[v] && all[r] >= 6 && moved[r] >= 0.9 * all[r]) list.Add(v); }
            return list;
        }

        /// <summary>정점마다 속한 UV 섬(삼각형으로 이어진 묶음)의 대표 정점</summary>
        static int[] IslandRoots(MeshData d)
        {
            int n = d.VertexCount;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var s in d.Subs)
                for (int t = 0; t + 2 < s.Indices.Length; t += 3)
                {
                    int a = Find(s.Indices[t]), b = Find(s.Indices[t + 1]), c = Find(s.Indices[t + 2]);
                    parent[b] = a; parent[Find(c)] = a;
                }
            for (int v = 0; v < n; v++) parent[v] = Find(v);
            return parent;
        }

        /// <summary>
        /// 다문 입 다듬기를 쓸 수 있는 블렌드쉐이프 (MakeMouthLine이 띠를 찾는 것) — 섬은 메시마다 한 번만 나눈다 (2026-10-27:
        /// 이름마다 MakeMouthLine을 불러 메시 전체를 다시 나누느라 큰 시험 장면 몸 메시를 열 때 0.5초).
        /// </summary>
        public static List<string> MouthLineShapes(MeshData d, IEnumerable<string> names)
        {
            var found = new List<string>();
            int[]? root = null;
            foreach (var name in names)
            {
                var sh = d.Shapes.Find(s => s.Name == name);
                if (sh == null || sh.Frames.Count == 0 || sh.Frames[sh.Frames.Count - 1].DPos is not float[] dp) continue;
                if (LineBand(d, dp, root ??= IslandRoots(d)).Count >= 4) found.Add(name);
            }
            return found;
        }
    }
}
