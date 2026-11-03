// 메시 편집 연산 (PLAN.md 3.9). IR 메시는 읽기 전용이고, 편집은 연산 목록을 원본에 차례로 적용한 결과다.
// Host(통계)와 렌더러(화면)가 같은 코드로 같은 결과를 얻는다. 각 연산의 정점 번호는 그 앞 연산까지 적용한 메시 기준.
//   weld   { distance, sel?, keepSeams=true, keepShapes=true }   거리 안의 정점 합치기 (Merge by Distance)
//   merge  { sel, at: center|first|last }                       고른 정점을 한 점으로
//   delete { sel }                                              정점과 그 정점을 쓰는 면 삭제
//   face   { sel, submesh=0 }                                   고른 정점(3개 이상)을 잇는 면 (부채꼴 삼각형)
//   move   { sel, delta: [x,y,z] | [정점마다 x,y,z...] }         정점 이동 (메시 공간)
//   normals { sel? }                                           노멀 다시 계산 (고른 정점, 없으면 전부) — 옮기거나 면을 만든 뒤 명암이 어긋날 때 (2026-10-23)
//   decimate { triangles | ratio, keepShapes=true }             삼각형을 그 수(또는 비율)까지 줄이기 (MeshDecimate.cs)
//   blinkFix { targets, sources: [{name, weight}], strength, falloff }  깜빡임 블렌드쉐이프에서 눈 편집 블렌드쉐이프의 영향 빼기 (BlinkFix.cs)
//   mouthFix { 같은 칸 }                                          입 벌리는 블렌드쉐이프에서 입 편집 블렌드쉐이프의 영향 빼기 (MouthFix.cs)
//   shapeMove { shape, points: [x,y,z,dx,dy,dz…], tol }                블렌드쉐이프 델타를 위치로 찾은 정점에 더하기 (MouthLine.cs, 다문 입 선 다듬기)
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol.Ir;

namespace VrcProto.Protocol
{
    public sealed class MeshData
    {
        public sealed class Frame { public float Weight; public float[]? DPos, DNrm, DTan; }   // 정점 수 × 3
        public sealed class Shape { public string Name = ""; public List<Frame> Frames = new List<Frame>(); }
        public sealed class Sub { public int[] Indices = Array.Empty<int>(); public string Topology = "Triangles"; }

        public int VertexCount;
        public float[] Pos = Array.Empty<float>();
        public float[]? Nrm, Tan, Col;
        public float[]?[] Uv = new float[]?[4];
        public int[] UvDims = { 2, 2, 2, 2 };
        public int[]? BoneIdx; public float[]? BoneW;
        public float[]? Bindposes;   // 정점과 무관 (편집해도 그대로). float4x4 열 우선
        public List<Sub> Subs = new List<Sub>();
        public List<Shape> Shapes = new List<Shape>();

        static float[]? Opt(IrBuffers b, BufRef r) => r.IsEmpty ? null : b.Floats(r);

        /// <param name="shapes">false면 블렌드쉐이프를 읽지 않는다 (렌더러가 프레임을 하나씩 따로 넣을 때)</param>
        public static MeshData Load(IrMesh m, IrBuffers buf, bool shapes = true)
        {
            var d = new MeshData { VertexCount = m.VertexCount, Pos = buf.Floats(m.Positions), Nrm = Opt(buf, m.Normals), Tan = Opt(buf, m.Tangents), Col = Opt(buf, m.Colors) };
            for (int c = 0; c < 4; c++) { d.Uv[c] = Opt(buf, m.Uvs[c]); d.UvDims[c] = m.UvDims[c]; }
            if (!m.BoneWeights.IsEmpty) { d.BoneIdx = buf.Ints(m.BoneIndices); d.BoneW = buf.Floats(m.BoneWeights); }
            d.Bindposes = Opt(buf, m.Bindposes);
            foreach (var s in m.Submeshes) d.Subs.Add(new Sub { Indices = buf.Ints(s.Indices), Topology = s.Topology });
            if (!shapes) return d;
            foreach (var bs in m.BlendShapes)
            {
                var sh = new Shape { Name = bs.Name };
                foreach (var f in bs.Frames) sh.Frames.Add(new Frame { Weight = f.Weight, DPos = Opt(buf, f.DeltaPos), DNrm = Opt(buf, f.DeltaNormal), DTan = Opt(buf, f.DeltaTangent) });
                d.Shapes.Add(sh);
            }
            return d;
        }

        static float[]? C(float[]? a) => a == null ? null : (float[])a.Clone();
        public MeshData Clone()
        {
            var d = new MeshData { VertexCount = VertexCount, Pos = (float[])Pos.Clone(), Nrm = C(Nrm), Tan = C(Tan), Col = C(Col), BoneIdx = BoneIdx == null ? null : (int[])BoneIdx.Clone(), BoneW = C(BoneW), Bindposes = Bindposes };
            for (int c = 0; c < 4; c++) { d.Uv[c] = C(Uv[c]); d.UvDims[c] = UvDims[c]; }
            foreach (var s in Subs) d.Subs.Add(new Sub { Indices = (int[])s.Indices.Clone(), Topology = s.Topology });
            foreach (var sh in Shapes)
            {
                var n = new Shape { Name = sh.Name };
                foreach (var f in sh.Frames) n.Frames.Add(new Frame { Weight = f.Weight, DPos = C(f.DPos), DNrm = C(f.DNrm), DTan = C(f.DTan) });
                d.Shapes.Add(n);
            }
            return d;
        }

        /// <summary>VRChat 퍼포먼스와 같은 셈: Triangles 그대로, Quads는 2개씩, Lines·Points는 0.</summary>
        public int Triangles
        {
            get
            {
                long t = 0;
                foreach (var s in Subs) t += s.Topology == "Triangles" ? s.Indices.Length / 3 : s.Topology == "Quads" ? s.Indices.Length / 4 * 2 : 0;
                return (int)t;
            }
        }

        /// <summary>
        /// 정점 다시 매기기. map[old] = 새 번호 또는 -1(삭제). 새 정점의 속성은 그 번호로 가는 가장 작은 옛 정점(rep) 것.
        /// 삭제된 정점을 쓰는 면, 넓이가 0이 된 면(같은 정점 두 번), 같은 방향으로 겹친 같은 면은 지운다.
        /// </summary>
        public void Remap(int[] map, int newCount)
        {
            var rep = new int[newCount];
            for (int i = 0; i < newCount; i++) rep[i] = -1;
            for (int i = 0; i < map.Length; i++) { int n = map[i]; if (n >= 0 && rep[n] < 0) rep[n] = i; }
            float[]? Pick(float[]? a, int dim)
            {
                if (a == null) return null;
                var r = new float[newCount * dim];
                for (int i = 0; i < newCount; i++) if (rep[i] >= 0) Array.Copy(a, rep[i] * dim, r, i * dim, dim);
                return r;
            }
            Pos = Pick(Pos, 3)!; Nrm = Pick(Nrm, 3); Tan = Pick(Tan, 4); Col = Pick(Col, 4); BoneW = Pick(BoneW, 4);
            for (int c = 0; c < 4; c++) Uv[c] = Pick(Uv[c], UvDims[c]);
            if (BoneIdx != null)
            {
                var r = new int[newCount * 4];
                for (int i = 0; i < newCount; i++) if (rep[i] >= 0) Array.Copy(BoneIdx, rep[i] * 4, r, i * 4, 4);
                BoneIdx = r;
            }
            foreach (var sh in Shapes) foreach (var f in sh.Frames) { f.DPos = Pick(f.DPos, 3); f.DNrm = Pick(f.DNrm, 3); f.DTan = Pick(f.DTan, 3); }

            foreach (var s in Subs)
            {
                int k = s.Topology == "Triangles" ? 3 : s.Topology == "Quads" ? 4 : s.Topology == "Lines" ? 2 : 1;
                var outIdx = new List<int>(s.Indices.Length);
                var seen = new HashSet<(int, int, int)>();
                var prim = new int[k];
                for (int p = 0; p + k <= s.Indices.Length; p += k)
                {
                    bool drop = false;
                    for (int j = 0; j < k; j++)
                    {
                        int o = s.Indices[p + j];
                        int n = o >= 0 && o < map.Length ? map[o] : -1;
                        if (n < 0) { drop = true; break; }
                        prim[j] = n;
                    }
                    if (drop) continue;
                    if (k == 3)
                    {
                        if (prim[0] == prim[1] || prim[1] == prim[2] || prim[0] == prim[2]) continue;
                        // 같은 감김 방향의 회전은 같은 면 (뒤집힌 면은 양면용일 수 있어 남긴다)
                        int m = prim[0] < prim[1] ? (prim[0] < prim[2] ? 0 : 2) : (prim[1] < prim[2] ? 1 : 2);
                        if (!seen.Add((prim[m], prim[(m + 1) % 3], prim[(m + 2) % 3]))) continue;
                    }
                    else if (k == 2 && prim[0] == prim[1]) continue;
                    outIdx.AddRange(prim);
                }
                s.Indices = outIdx.ToArray();
            }
            VertexCount = newCount;
        }
    }

    public static partial class MeshOps
    {
        public const string Weld = "weld", Merge = "merge", Delete = "delete", Face = "face", Move = "move", Normals = "normals";

        /// <summary>원본에 연산 목록을 적용한 사본. ops가 비면 원본 사본 그대로.</summary>
        public static MeshData Apply(MeshData original, JNode ops)
        {
            var d = original.Clone();
            foreach (var op in ops.Items) ApplyOne(d, op);
            return d;
        }

        public static void ApplyOne(MeshData d, JNode op)
        {
            switch (op["op"].AsString)
            {
                case Weld: DoWeld(d, op["distance"].AsFloat, Sel(op, d), !op.Has("keepSeams") || op["keepSeams"].AsBool, !op.Has("keepShapes") || op["keepShapes"].AsBool); break;
                case Merge: DoMerge(d, Sel(op, d), op["at"].AsString); break;
                case Delete: DoDelete(d, Sel(op, d)); break;
                case Face: DoFace(d, Sel(op, d), op["submesh"].AsInt); break;
                case Move: DoMove(d, Sel(op, d), op["delta"].ToFloatArray()); break;
                case Normals: DoNormals(d, Sel(op, d)); break;
                case BlinkFix: case MouthFix: DoBlinkFix(d, op); break;
                case ShapeMove: DoShapeMove(d, op); break;
                case Decimate: DoDecimate(d, op.Has("ratio") ? op["ratio"].AsFloat : 1f, !op.Has("keepShapes") || op["keepShapes"].AsBool, op["triangles"].AsInt); break;
            }
        }

        /// <summary>sel 배열 (범위 밖 번호·중복 제외, 순서 유지). 없으면 null.</summary>
        static int[]? Sel(JNode op, MeshData d)
        {
            var s = op["sel"];
            if (s.Kind != JKind.Array) return null;
            var seen = new HashSet<int>(); var r = new List<int>(s.Count);
            foreach (var x in s.Items) { int i = x.AsInt; if (i >= 0 && i < d.VertexCount && seen.Add(i)) r.Add(i); }
            return r.ToArray();
        }

        // ---- weld ----
        static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }

        static bool Near(float[]? a, int dim, int i, int j, float eps)
        {
            if (a == null) return true;
            for (int k = 0; k < dim; k++) if (Math.Abs(a[i * dim + k] - a[j * dim + k]) > eps) return false;
            return true;
        }

        static bool Compatible(MeshData d, int i, int j, bool seams, bool shapes)
        {
            if (seams)
            {
                for (int c = 0; c < 4; c++) if (!Near(d.Uv[c], d.UvDims[c], i, j, 1e-5f)) return false;
                if (d.Nrm != null)
                {
                    float dot = d.Nrm[i * 3] * d.Nrm[j * 3] + d.Nrm[i * 3 + 1] * d.Nrm[j * 3 + 1] + d.Nrm[i * 3 + 2] * d.Nrm[j * 3 + 2];
                    if (dot < 0.9998f) return false; // 약 1° — 하드 엣지는 남긴다
                }
                if (!Near(d.Col, 4, i, j, 1e-4f)) return false;
                // 미러 UV 이음매: 탄젠트 방향(w 부호)이 다르면 노멀맵이 뒤집힌다
                if (d.Tan != null && d.Tan[i * 4 + 3] * d.Tan[j * 4 + 3] < 0) return false;
            }
            // 본 가중치가 다른 정점을 합치면 자세를 바꿀 때 찢어지거나 늘어난다 (쉬는 자세에서는 같아 보여도)
            if ((seams || shapes) && !SameWeights(d, i, j)) return false;
            if (shapes)
                foreach (var sh in d.Shapes) foreach (var f in sh.Frames) if (!Near(f.DPos, 3, i, j, 1e-6f)) return false;
            return true;
        }

        /// <summary>같은 본에 같은 가중치 (순서 무관, 가중치 차 0.01 이내).</summary>
        static bool SameWeights(MeshData d, int i, int j)
        {
            if (d.BoneW == null || d.BoneIdx == null) return true;
            for (int a = 0; a < 4; a++)
            {
                float wa = d.BoneW[i * 4 + a];
                if (wa <= 0.001f) continue;
                float wb = 0;
                for (int b = 0; b < 4; b++) if (d.BoneIdx[j * 4 + b] == d.BoneIdx[i * 4 + a]) wb += d.BoneW[j * 4 + b];
                if (Math.Abs(wa - wb) > 0.01f) return false;
            }
            for (int b = 0; b < 4; b++)
            {
                float wb = d.BoneW[j * 4 + b];
                if (wb <= 0.001f) continue;
                bool found = false;
                for (int a = 0; a < 4; a++) if (d.BoneIdx[i * 4 + a] == d.BoneIdx[j * 4 + b] && d.BoneW[i * 4 + a] > 0.001f) found = true;
                if (!found) return false;
            }
            return true;
        }

        /// <summary>거리 안의 정점을 합친다. 대표 = 묶음에서 번호가 가장 작은 정점 (위치도 그 정점).</summary>
        public static void DoWeld(MeshData d, float dist, int[]? sel, bool keepSeams, bool keepShapes)
        {
            int n = d.VertexCount;
            if (n == 0 || dist < 0) return;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            bool[]? inSel = null;
            if (sel != null) { inSel = new bool[n]; foreach (var i in sel) inSel[i] = true; }
            float cell = Math.Max(dist, 1e-7f);
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(int i) => ((int)Math.Floor(d.Pos[i * 3] / cell), (int)Math.Floor(d.Pos[i * 3 + 1] / cell), (int)Math.Floor(d.Pos[i * 3 + 2] / cell));
            float d2 = dist * dist;
            for (int i = 0; i < n; i++)
            {
                if (inSel != null && !inSel[i]) continue;
                var (kx, ky, kz) = Key(i);
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (!grid.TryGetValue((kx + x, ky + y, kz + z), out var list)) continue;
                    foreach (var j in list)
                    {
                        float dx = d.Pos[i * 3] - d.Pos[j * 3], dy = d.Pos[i * 3 + 1] - d.Pos[j * 3 + 1], dz = d.Pos[i * 3 + 2] - d.Pos[j * 3 + 2];
                        if (dx * dx + dy * dy + dz * dz > d2) continue;
                        int ri = Find(parent, i), rj = Find(parent, j);
                        if (ri == rj) continue;
                        // 묶음 대표끼리도 맞아야 한다 (UV가 다른 정점이 사슬로 이어져 섞이지 않게)
                        if (!Compatible(d, i, j, keepSeams, keepShapes) || !Compatible(d, ri, rj, keepSeams, keepShapes)) continue;
                        if (ri < rj) parent[rj] = ri; else parent[ri] = rj;
                    }
                }
                var k = (kx, ky, kz);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i);
            }
            var map = new int[n]; int count = 0;
            for (int i = 0; i < n; i++) { int r = Find(parent, i); map[i] = r == i ? count++ : map[r]; }
            d.Remap(map, count);
        }

        public static void DoMerge(MeshData d, int[]? sel, string at)
        {
            if (sel == null || sel.Length < 2) return;
            int target = at == "last" ? sel[sel.Length - 1] : sel[0];
            if (at != "first" && at != "last")
            {
                double x = 0, y = 0, z = 0;
                foreach (var i in sel) { x += d.Pos[i * 3]; y += d.Pos[i * 3 + 1]; z += d.Pos[i * 3 + 2]; }
                target = int.MaxValue; foreach (var i in sel) target = Math.Min(target, i);
                d.Pos[target * 3] = (float)(x / sel.Length); d.Pos[target * 3 + 1] = (float)(y / sel.Length); d.Pos[target * 3 + 2] = (float)(z / sel.Length);
            }
            var inSel = new HashSet<int>(sel);
            // target으로 모으고 나머지는 차례로 당긴다. 속성은 target 것이 되도록 target 자리를 묶음의 맨 앞 번호로 쓴다.
            int first = int.MaxValue; foreach (var i in sel) first = Math.Min(first, i);
            if (target != first) CopyVertex(d, target, first);
            var map = new int[d.VertexCount]; int c = 0;
            for (int i = 0; i < map.Length; i++) map[i] = inSel.Contains(i) && i != first ? -2 : c++;
            for (int i = 0; i < map.Length; i++) if (map[i] == -2) map[i] = map[first];
            d.Remap(map, c);
        }

        /// <summary>정점 src의 모든 속성을 dst에 복사 (merge가 대표 정점을 바꿀 때).</summary>
        static void CopyVertex(MeshData d, int src, int dst)
        {
            void Cp(float[]? a, int dim) { if (a != null) Array.Copy(a, src * dim, a, dst * dim, dim); }
            Cp(d.Pos, 3); Cp(d.Nrm, 3); Cp(d.Tan, 4); Cp(d.Col, 4); Cp(d.BoneW, 4);
            for (int c = 0; c < 4; c++) Cp(d.Uv[c], d.UvDims[c]);
            if (d.BoneIdx != null) Array.Copy(d.BoneIdx, src * 4, d.BoneIdx, dst * 4, 4);
            foreach (var sh in d.Shapes) foreach (var f in sh.Frames) { Cp(f.DPos, 3); Cp(f.DNrm, 3); Cp(f.DTan, 3); }
        }

        public static void DoDelete(MeshData d, int[]? sel)
        {
            if (sel == null || sel.Length == 0) return;
            var del = new bool[d.VertexCount]; foreach (var i in sel) del[i] = true;
            var map = new int[d.VertexCount]; int c = 0;
            for (int i = 0; i < map.Length; i++) map[i] = del[i] ? -1 : c++;
            d.Remap(map, c);
        }

        public static void DoFace(MeshData d, int[]? sel, int submesh)
        {
            if (sel == null || sel.Length < 3 || d.Subs.Count == 0) return;
            int si = submesh >= 0 && submesh < d.Subs.Count ? submesh : 0;
            var s = d.Subs[si];
            if (s.Topology != "Triangles") return;
            var idx = new List<int>(s.Indices);
            for (int i = 1; i + 1 < sel.Length; i++) { idx.Add(sel[0]); idx.Add(sel[i]); idx.Add(sel[i + 1]); }
            s.Indices = idx.ToArray();
        }

        /// <summary>
        /// 노멀 다시 계산: 정점마다 그 정점을 쓰는 삼각형 면 노멀(넓이 가중)의 합.
        /// 같은 자리에 나뉜 정점(UV 이음매 등)은 원래 노멀이 비슷하면(60° 안) 같이 더해 이음매에 선이 생기지 않게 하고,
        /// 원래 노멀이 크게 다르면(하드 엣지) 따로 둔다. 탄젠트·블렌드쉐이프 노멀 델타는 그대로.
        /// </summary>
        public static void DoNormals(MeshData d, int[]? sel)
        {
            int n = d.VertexCount;
            if (n == 0) return;
            var face = new float[n * 3];
            foreach (var s in d.Subs)
            {
                if (s.Topology != "Triangles") continue;
                var ix = s.Indices;
                for (int t = 0; t + 2 < ix.Length; t += 3)
                {
                    int a = ix[t], b = ix[t + 1], c = ix[t + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    float ux = d.Pos[b * 3] - d.Pos[a * 3], uy = d.Pos[b * 3 + 1] - d.Pos[a * 3 + 1], uz = d.Pos[b * 3 + 2] - d.Pos[a * 3 + 2];
                    float vx = d.Pos[c * 3] - d.Pos[a * 3], vy = d.Pos[c * 3 + 1] - d.Pos[a * 3 + 1], vz = d.Pos[c * 3 + 2] - d.Pos[a * 3 + 2];
                    // Unity는 시계 방향이 앞면 → (b − a) × (c − a)를 뒤집은 것이 바깥쪽 (길이 = 넓이의 2배라 넓이 가중)
                    float nx = -(uy * vz - uz * vy), ny = -(uz * vx - ux * vz), nz = -(ux * vy - uy * vx);
                    foreach (var k in new[] { a, b, c }) { face[k * 3] += nx; face[k * 3 + 1] += ny; face[k * 3 + 2] += nz; }
                }
            }
            // 같은 자리(0.01 mm) 정점 묶음
            var groups = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Key(int i) => ((long)Math.Round(d.Pos[i * 3] * 1e5), (long)Math.Round(d.Pos[i * 3 + 1] * 1e5), (long)Math.Round(d.Pos[i * 3 + 2] * 1e5));
            for (int i = 0; i < n; i++) { var k = Key(i); if (!groups.TryGetValue(k, out var l)) groups[k] = l = new List<int>(); l.Add(i); }
            var old = d.Nrm;
            var nrm = old != null ? (float[])old.Clone() : new float[n * 3];
            IEnumerable<int> which = sel ?? Enumerable.Range(0, n);
            foreach (var i in which)
            {
                float x = 0, y = 0, z = 0;
                foreach (var j in groups[Key(i)])
                {
                    if (j != i && old != null && old[i * 3] * old[j * 3] + old[i * 3 + 1] * old[j * 3 + 1] + old[i * 3 + 2] * old[j * 3 + 2] < 0.5f) continue;
                    x += face[j * 3]; y += face[j * 3 + 1]; z += face[j * 3 + 2];
                }
                float len = (float)Math.Sqrt(x * x + y * y + z * z);
                if (len < 1e-12f) continue;   // 면이 없는 정점은 그대로
                nrm[i * 3] = x / len; nrm[i * 3 + 1] = y / len; nrm[i * 3 + 2] = z / len;
            }
            d.Nrm = nrm;
        }

        public static void DoMove(MeshData d, int[]? sel, float[] delta)
        {
            if (sel == null) return;
            bool each = delta.Length == sel.Length * 3 && sel.Length > 1;
            if (!each && delta.Length < 3) return;
            for (int k = 0; k < sel.Length; k++)
            {
                int i = sel[k], o = each ? k * 3 : 0;
                d.Pos[i * 3] += delta[o]; d.Pos[i * 3 + 1] += delta[o + 1]; d.Pos[i * 3 + 2] += delta[o + 2];
            }
        }
    }
}
