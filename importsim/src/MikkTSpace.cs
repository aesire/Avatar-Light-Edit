using System;
using System.Collections.Generic;

namespace ImportSim
{
    /// <summary>
    /// MikkTSpace (Morten S. Mikkelsen, mikktspace.c) 의 C# 이식. 삼각형·사각형 면, 각도 임계값 180도(genTangSpaceDefault).
    /// 원본처럼 float 로 계산한다. 결과는 면 f 의 코너 i 마다 (x, y, z, w) — w 는 +1/-1, 위치는 Offsets[f] + i.
    /// </summary>
    public static class MikkTSpace
    {
        const int MarkDegenerate = 1, QuadOneDegenTri = 2, GroupWithAny = 4, OrientPreserving = 8;

        struct V3
        {
            public float X, Y, Z;
            public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V3 operator *(float s, V3 a) => new V3(s * a.X, s * a.Y, s * a.Z);
            public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public float LengthSquared => X * X + Y * Y + Z * Z;
            public float Length => (float)Math.Sqrt(LengthSquared);
            public V3 Normalize() { float l = Length; return (1.0f / l) * this; }
            public bool Eq(V3 b) => X == b.X && Y == b.Y && Z == b.Z;
        }

        static bool NotZero(float f) => Math.Abs(f) > 1.17549435e-38f;   // FLT_MIN
        static bool VNotZero(V3 v) => NotZero(v.X) || NotZero(v.Y) || NotZero(v.Z);

        sealed class TriInfo
        {
            public int[] FaceNeighbors = { -1, -1, -1 };
            public Group?[] AssignedGroup = new Group?[3];
            public V3 Os, Ot;
            public float MagS, MagT;
            public int OrgFace, Flag, TSpacesOffs;
            public byte[] VertNum = new byte[3];
        }

        sealed class Group
        {
            public List<int> Faces = new List<int>();
            public int VertexRepresentative;
            public bool OrientPreserving;
        }

        struct TSpace
        {
            public V3 Os, Ot;
            public float MagS, MagT;
            public int Counter;
            public bool Orient;
        }

        /// <summary>
        /// faces: 면마다 정점 인덱스 3개 또는 4개. 반환: (코너마다 4개 float, 면마다 시작 코너)
        /// </summary>
        public static (float[] tangents, int[] offsets) Generate(IReadOnlyList<int[]> faces, Func<int, (float, float, float)> position,
                                                                Func<int, (float, float, float)> normal, Func<int, (float, float)> uv)
        {
            // 코너 인덱스 = MakeIndex(f, v) = f*4 + v
            V3 Pos(int c) { var p = position(faces[c >> 2][c & 3]); return new V3(p.Item1, p.Item2, p.Item3); }
            V3 Nrm(int c) { var n = normal(faces[c >> 2][c & 3]); return new V3(n.Item1, n.Item2, n.Item3); }
            V3 Tex(int c) { var t = uv(faces[c >> 2][c & 3]); return new V3(t.Item1, t.Item2, 1.0f); }

            var offsets = new int[faces.Count];
            int nTris = 0, nTSpaces = 0;
            for (int f = 0; f < faces.Count; f++)
            {
                offsets[f] = nTSpaces;
                int n = faces[f].Length;
                if (n == 3) nTris += 1; else if (n == 4) nTris += 2;
                nTSpaces += n;
            }
            var result = new float[nTSpaces * 4];
            if (nTris == 0) return (result, offsets);

            // GenerateInitialVerticesIndexList
            var info = new TriInfo[nTris];
            var triList = new int[nTris * 3];
            int dst = 0;
            for (int f = 0; f < faces.Count; f++)
            {
                int n = faces[f].Length;
                if (n != 3 && n != 4) continue;
                info[dst] = new TriInfo { OrgFace = f, TSpacesOffs = offsets[f] };
                if (n == 3)
                {
                    info[dst].VertNum = new byte[] { 0, 1, 2 };
                    triList[dst * 3] = f * 4; triList[dst * 3 + 1] = f * 4 + 1; triList[dst * 3 + 2] = f * 4 + 2;
                    dst++;
                }
                else
                {
                    info[dst + 1] = new TriInfo { OrgFace = f, TSpacesOffs = offsets[f] };
                    int i0 = f * 4, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                    float d02 = (Tex(i2) - Tex(i0)).LengthSquared, d13 = (Tex(i3) - Tex(i1)).LengthSquared;
                    bool diag02;
                    if (d02 < d13) diag02 = true;
                    else if (d13 < d02) diag02 = false;
                    else diag02 = !((Pos(i3) - Pos(i1)).LengthSquared < (Pos(i2) - Pos(i0)).LengthSquared);
                    if (diag02)
                    {
                        info[dst].VertNum = new byte[] { 0, 1, 2 }; triList[dst * 3] = i0; triList[dst * 3 + 1] = i1; triList[dst * 3 + 2] = i2; dst++;
                        info[dst].VertNum = new byte[] { 0, 2, 3 }; triList[dst * 3] = i0; triList[dst * 3 + 1] = i2; triList[dst * 3 + 2] = i3; dst++;
                    }
                    else
                    {
                        info[dst].VertNum = new byte[] { 0, 1, 3 }; triList[dst * 3] = i0; triList[dst * 3 + 1] = i1; triList[dst * 3 + 2] = i3; dst++;
                        info[dst].VertNum = new byte[] { 1, 2, 3 }; triList[dst * 3] = i1; triList[dst * 3 + 1] = i2; triList[dst * 3 + 2] = i3; dst++;
                    }
                }
            }

            // GenerateSharedVerticesIndexList: 위치·노멀·UV 가 같은 코너는 같은 대표로
            var shared = new Dictionary<(float, float, float, float, float, float, float, float), int>();
            for (int k = 0; k < triList.Length; k++)
            {
                int c = triList[k];
                V3 p = Pos(c), n = Nrm(c), tx = Tex(c);
                var key = (p.X, p.Y, p.Z, n.X, n.Y, n.Z, tx.X, tx.Y);
                if (shared.TryGetValue(key, out var rep)) triList[k] = rep;
                else shared[key] = c;
            }

            // 퇴화 삼각형
            int totTris = nTris, degen = 0;
            for (int t = 0; t < totTris; t++)
            {
                V3 p0 = Pos(triList[t * 3]), p1 = Pos(triList[t * 3 + 1]), p2 = Pos(triList[t * 3 + 2]);
                if (p0.Eq(p1) || p0.Eq(p2) || p1.Eq(p2)) { info[t].Flag |= MarkDegenerate; degen++; }
            }
            nTris = totTris - degen;
            DegenPrologue(info, triList, nTris, totTris);
            InitTriInfo(info, triList, nTris, Pos, Tex);
            var groups = Build4RuleGroups(info, triList, nTris);

            var tspace = new TSpace[nTSpaces];
            for (int i = 0; i < nTSpaces; i++)
                tspace[i] = new TSpace { Os = new V3(1, 0, 0), MagS = 1, Ot = new V3(0, 1, 0), MagT = 1 };
            GenerateTSpaces(tspace, info, groups, triList, -1.0f, Pos, Nrm);
            DegenEpilogue(tspace, info, triList, nTris, totTris, Pos);

            for (int c = 0; c < nTSpaces; c++)
            {
                var ts = tspace[c];
                result[c * 4] = ts.Os.X; result[c * 4 + 1] = ts.Os.Y; result[c * 4 + 2] = ts.Os.Z;
                result[c * 4 + 3] = ts.Orient ? 1.0f : -1.0f;
            }
            return (result, offsets);
        }

        static void DegenPrologue(TriInfo[] info, int[] triList, int nTris, int totTris)
        {
            // 한 삼각형만 퇴화한 사각형 표시
            int t = 0;
            while (t < totTris - 1)
            {
                if (info[t].OrgFace == info[t + 1].OrgFace)
                {
                    bool a = (info[t].Flag & MarkDegenerate) != 0, b = (info[t + 1].Flag & MarkDegenerate) != 0;
                    if (a ^ b) { info[t].Flag |= QuadOneDegenTri; info[t + 1].Flag |= QuadOneDegenTri; }
                    t += 2;
                }
                else t++;
            }
            // 퇴화 삼각형을 뒤로 보낸다 (원본과 같은 교환 방식)
            int nextGood = 1;
            t = 0;
            bool stillFinding = true;
            while (t < nTris && stillFinding)
            {
                bool isGood = (info[t].Flag & MarkDegenerate) == 0;
                if (isGood) { if (nextGood < t + 2) nextGood = t + 2; }
                else
                {
                    bool justDegen = true;
                    while (justDegen && nextGood < totTris)
                    {
                        if ((info[nextGood].Flag & MarkDegenerate) == 0) justDegen = false;
                        else nextGood++;
                    }
                    int t0 = t, t1 = nextGood;
                    nextGood++;
                    if (!justDegen)
                    {
                        for (int i = 0; i < 3; i++) { var tmp = triList[t0 * 3 + i]; triList[t0 * 3 + i] = triList[t1 * 3 + i]; triList[t1 * 3 + i] = tmp; }
                        var ti = info[t0]; info[t0] = info[t1]; info[t1] = ti;
                    }
                    else stillFinding = false;
                }
                if (stillFinding) t++;
            }
        }

        static float TexArea(int[] triList, int off, Func<int, V3> tex)
        {
            V3 t1 = tex(triList[off]), t2 = tex(triList[off + 1]), t3 = tex(triList[off + 2]);
            float a = (t2.X - t1.X) * (t3.Y - t1.Y) - (t2.Y - t1.Y) * (t3.X - t1.X);
            return a < 0 ? -a : a;
        }

        static void InitTriInfo(TriInfo[] info, int[] triList, int nTris, Func<int, V3> pos, Func<int, V3> tex)
        {
            for (int f = 0; f < nTris; f++)
            {
                info[f].Flag |= GroupWithAny;
                info[f].Os = default; info[f].Ot = default; info[f].MagS = 0; info[f].MagT = 0;
            }
            for (int f = 0; f < nTris; f++)
            {
                V3 v1 = pos(triList[f * 3]), v2 = pos(triList[f * 3 + 1]), v3 = pos(triList[f * 3 + 2]);
                V3 t1 = tex(triList[f * 3]), t2 = tex(triList[f * 3 + 1]), t3 = tex(triList[f * 3 + 2]);
                float t21x = t2.X - t1.X, t21y = t2.Y - t1.Y, t31x = t3.X - t1.X, t31y = t3.Y - t1.Y;
                V3 d1 = v2 - v1, d2 = v3 - v1;
                float area = t21x * t31y - t21y * t31x;
                V3 os = t31y * d1 - t21y * d2;
                V3 ot = (-t31x) * d1 + t21x * d2;
                if (area > 0) info[f].Flag |= OrientPreserving;
                if (NotZero(area))
                {
                    float absArea = Math.Abs(area);
                    float lenOs = os.Length, lenOt = ot.Length;
                    float s = (info[f].Flag & OrientPreserving) == 0 ? -1.0f : 1.0f;
                    if (NotZero(lenOs)) info[f].Os = (s / lenOs) * os;
                    if (NotZero(lenOt)) info[f].Ot = (s / lenOt) * ot;
                    info[f].MagS = lenOs / absArea;
                    info[f].MagT = lenOt / absArea;
                    if (NotZero(info[f].MagS) && NotZero(info[f].MagT)) info[f].Flag &= ~GroupWithAny;
                }
            }
            // 멀쩡한 사각형은 두 삼각형 방향을 맞춘다
            for (int t = 0; t < nTris - 1; t++)
            {
                if (info[t].OrgFace != info[t + 1].OrgFace) continue;
                bool degA = (info[t].Flag & MarkDegenerate) != 0, degB = (info[t + 1].Flag & MarkDegenerate) != 0;
                if (!(degA || degB))
                {
                    bool oa = (info[t].Flag & OrientPreserving) != 0, ob = (info[t + 1].Flag & OrientPreserving) != 0;
                    if (oa != ob)
                    {
                        bool chooseFirst = (info[t + 1].Flag & GroupWithAny) != 0 || TexArea(triList, t * 3, tex) >= TexArea(triList, (t + 1) * 3, tex);
                        int t0 = chooseFirst ? t : t + 1, t1 = chooseFirst ? t + 1 : t;
                        info[t1].Flag &= ~OrientPreserving;
                        info[t1].Flag |= info[t0].Flag & OrientPreserving;
                    }
                }
                t++;   // 사각형의 두 번째 삼각형 건너뜀
            }
            BuildNeighbors(info, triList, nTris);
        }

        static void BuildNeighbors(TriInfo[] info, int[] triList, int nTris)
        {
            var edges = new (int i0, int i1, int f)[nTris * 3];
            for (int f = 0; f < nTris; f++)
                for (int i = 0; i < 3; i++)
                {
                    int a = triList[f * 3 + i], b = triList[f * 3 + (i < 2 ? i + 1 : 0)];
                    edges[f * 3 + i] = (Math.Min(a, b), Math.Max(a, b), f);
                }
            Array.Sort(edges, (x, y) => x.i0 != y.i0 ? x.i0.CompareTo(y.i0) : x.i1 != y.i1 ? x.i1.CompareTo(y.i1) : x.f.CompareTo(y.f));
            int n = edges.Length;
            for (int i = 0; i < n; i++)
            {
                var (i0, i1, f) = edges[i];
                GetEdge(out var i0A, out var i1A, out var eA, triList, f * 3, i0, i1);
                if (info[f].FaceNeighbors[eA] != -1) continue;
                int j = i + 1, eB = -1;
                bool notFound = true;
                while (j < n && i0 == edges[j].i0 && i1 == edges[j].i1 && notFound)
                {
                    int t = edges[j].f;
                    GetEdge(out var i1B, out var i0B, out eB, triList, t * 3, edges[j].i0, edges[j].i1);   // 뒤집어서
                    bool unassignedB = info[t].FaceNeighbors[eB] == -1;
                    if (i0A == i0B && i1A == i1B && unassignedB) notFound = false;
                    else j++;
                }
                if (!notFound)
                {
                    int t = edges[j].f;
                    info[f].FaceNeighbors[eA] = t;
                    info[t].FaceNeighbors[eB] = f;
                }
            }
        }

        static void GetEdge(out int i0Out, out int i1Out, out int edgeNum, int[] idx, int off, int i0In, int i1In)
        {
            if (idx[off] == i0In || idx[off] == i1In)
            {
                if (idx[off + 1] == i0In || idx[off + 1] == i1In) { edgeNum = 0; i0Out = idx[off]; i1Out = idx[off + 1]; }
                else { edgeNum = 2; i0Out = idx[off + 2]; i1Out = idx[off]; }
            }
            else { edgeNum = 1; i0Out = idx[off + 1]; i1Out = idx[off + 2]; }
        }

        static List<Group> Build4RuleGroups(TriInfo[] info, int[] triList, int nTris)
        {
            var groups = new List<Group>();
            for (int f = 0; f < nTris; f++)
                for (int i = 0; i < 3; i++)
                {
                    if ((info[f].Flag & GroupWithAny) != 0 || info[f].AssignedGroup[i] != null) continue;
                    var g = new Group { VertexRepresentative = triList[f * 3 + i], OrientPreserving = (info[f].Flag & OrientPreserving) != 0 };
                    groups.Add(g);
                    info[f].AssignedGroup[i] = g;
                    g.Faces.Add(f);
                    int l = info[f].FaceNeighbors[i], r = info[f].FaceNeighbors[i > 0 ? i - 1 : 2];
                    if (l >= 0) AssignRecur(triList, info, l, g);
                    if (r >= 0) AssignRecur(triList, info, r, g);
                }
            return groups;
        }

        static bool AssignRecur(int[] triList, TriInfo[] info, int tri, Group g)
        {
            var my = info[tri];
            int rep = g.VertexRepresentative, i = -1;
            if (triList[tri * 3] == rep) i = 0; else if (triList[tri * 3 + 1] == rep) i = 1; else if (triList[tri * 3 + 2] == rep) i = 2;
            if (i < 0) return false;
            if (my.AssignedGroup[i] == g) return true;
            if (my.AssignedGroup[i] != null) return false;
            if ((my.Flag & GroupWithAny) != 0 && my.AssignedGroup[0] == null && my.AssignedGroup[1] == null && my.AssignedGroup[2] == null)
            {
                my.Flag &= ~OrientPreserving;
                if (g.OrientPreserving) my.Flag |= OrientPreserving;
            }
            if (((my.Flag & OrientPreserving) != 0) != g.OrientPreserving) return false;
            g.Faces.Add(tri);
            my.AssignedGroup[i] = g;
            int l = my.FaceNeighbors[i], r = my.FaceNeighbors[i > 0 ? i - 1 : 2];
            if (l >= 0) AssignRecur(triList, info, l, g);
            if (r >= 0) AssignRecur(triList, info, r, g);
            return true;
        }

        static void GenerateTSpaces(TSpace[] tspace, TriInfo[] info, List<Group> groups, int[] triList, float thresCos,
                                    Func<int, V3> pos, Func<int, V3> nrm)
        {
            V3 Project(V3 v, V3 n) { var r = v - V3.Dot(n, v) * n; return VNotZero(r) ? r.Normalize() : r; }
            foreach (var g in groups)
            {
                var subGroups = new List<List<int>>();
                var subTs = new List<TSpace>();
                foreach (var f in g.Faces)
                {
                    int index = info[f].AssignedGroup[0] == g ? 0 : info[f].AssignedGroup[1] == g ? 1 : 2;
                    int vert = triList[f * 3 + index];
                    var n = nrm(vert);
                    var os = Project(info[f].Os, n);
                    var ot = Project(info[f].Ot, n);
                    int of1 = info[f].OrgFace;
                    var members = new List<int>();
                    foreach (var t in g.Faces)
                    {
                        var os2 = Project(info[t].Os, n);
                        var ot2 = Project(info[t].Ot, n);
                        bool any = ((info[f].Flag | info[t].Flag) & GroupWithAny) != 0;
                        bool sameOrg = of1 == info[t].OrgFace;
                        if (any || sameOrg || (V3.Dot(os, os2) > thresCos && V3.Dot(ot, ot2) > thresCos)) members.Add(t);
                    }
                    members.Sort();
                    int l = -1;
                    for (int k = 0; k < subGroups.Count && l < 0; k++)
                        if (SameList(subGroups[k], members)) l = k;
                    if (l < 0)
                    {
                        l = subGroups.Count;
                        subGroups.Add(members);
                        subTs.Add(EvalTspace(members, triList, info, g.VertexRepresentative, pos, nrm));
                    }
                    int outIdx = info[f].TSpacesOffs + info[f].VertNum[index];
                    var o = tspace[outIdx];
                    if (o.Counter == 1) { o = Avg(o, subTs[l]); o.Counter = 2; }
                    else { o = subTs[l]; o.Counter = 1; }
                    o.Orient = g.OrientPreserving;
                    tspace[outIdx] = o;
                }
            }
        }

        static bool SameList(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static TSpace EvalTspace(List<int> faces, int[] triList, TriInfo[] info, int rep, Func<int, V3> pos, Func<int, V3> nrm)
        {
            var res = new TSpace();
            float angleSum = 0;
            foreach (var f in faces)
            {
                if ((info[f].Flag & GroupWithAny) != 0) continue;
                int i = triList[3 * f] == rep ? 0 : triList[3 * f + 1] == rep ? 1 : 2;
                int index = triList[3 * f + i];
                var n = nrm(index);
                var os = info[f].Os - V3.Dot(n, info[f].Os) * n; if (VNotZero(os)) os = os.Normalize();
                var ot = info[f].Ot - V3.Dot(n, info[f].Ot) * n; if (VNotZero(ot)) ot = ot.Normalize();
                int i2 = triList[3 * f + (i < 2 ? i + 1 : 0)], i1 = triList[3 * f + i], i0 = triList[3 * f + (i > 0 ? i - 1 : 2)];
                V3 p0 = pos(i0), p1 = pos(i1), p2 = pos(i2);
                var v1 = p0 - p1; var v2 = p2 - p1;
                v1 = v1 - V3.Dot(n, v1) * n; if (VNotZero(v1)) v1 = v1.Normalize();
                v2 = v2 - V3.Dot(n, v2) * n; if (VNotZero(v2)) v2 = v2.Normalize();
                float cos = V3.Dot(v1, v2);
                cos = cos > 1 ? 1 : (cos < -1 ? -1 : cos);
                float angle = (float)Math.Acos(cos);
                res.Os = res.Os + angle * os;
                res.Ot = res.Ot + angle * ot;
                res.MagS += angle * info[f].MagS;
                res.MagT += angle * info[f].MagT;
                angleSum += angle;
            }
            if (VNotZero(res.Os)) res.Os = res.Os.Normalize();
            if (VNotZero(res.Ot)) res.Ot = res.Ot.Normalize();
            if (angleSum > 0) { res.MagS /= angleSum; res.MagT /= angleSum; }
            return res;
        }

        static TSpace Avg(TSpace a, TSpace b)
        {
            if (a.MagS == b.MagS && a.MagT == b.MagT && a.Os.Eq(b.Os) && a.Ot.Eq(b.Ot)) return a;
            var r = new TSpace { MagS = 0.5f * (a.MagS + b.MagS), MagT = 0.5f * (a.MagT + b.MagT), Os = a.Os + b.Os, Ot = a.Ot + b.Ot };
            if (VNotZero(r.Os)) r.Os = r.Os.Normalize();
            if (VNotZero(r.Ot)) r.Ot = r.Ot.Normalize();
            return r;
        }

        static void DegenEpilogue(TSpace[] tspace, TriInfo[] info, int[] triList, int nTris, int totTris, Func<int, V3> pos)
        {
            if (totTris > nTris)
            {
                var first = new Dictionary<int, int>();
                for (int j = 0; j < nTris * 3; j++) if (!first.ContainsKey(triList[j])) first[triList[j]] = j;
                for (int t = nTris; t < totTris; t++)
                {
                    if ((info[t].Flag & QuadOneDegenTri) != 0) continue;
                    for (int i = 0; i < 3; i++)
                        if (first.TryGetValue(triList[t * 3 + i], out var j))
                            tspace[info[t].TSpacesOffs + info[t].VertNum[i]] = tspace[info[j / 3].TSpacesOffs + info[j / 3].VertNum[j % 3]];
                }
            }
            // 한 삼각형만 남은 사각형: 빠진 코너는 같은 위치의 코너 값을 쓴다
            for (int t = 0; t < nTris; t++)
            {
                if ((info[t].Flag & QuadOneDegenTri) == 0) continue;
                var pv = info[t].VertNum;
                int flag = (1 << pv[0]) | (1 << pv[1]) | (1 << pv[2]);
                int missing = (flag & 2) == 0 ? 1 : (flag & 4) == 0 ? 2 : (flag & 8) == 0 ? 3 : 0;
                int org = info[t].OrgFace;
                var dstP = pos(org * 4 + missing);
                for (int i = 0; i < 3; i++)
                    if (pos(org * 4 + pv[i]).Eq(dstP))
                    {
                        tspace[info[t].TSpacesOffs + missing] = tspace[info[t].TSpacesOffs + pv[i]];
                        break;
                    }
            }
        }
    }
}
