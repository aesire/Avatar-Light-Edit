// 아바타 퍼포먼스 예상치: IR에서 셀 수 있는 항목만 VRChat 공개 기준표로 등급을 매긴다.
// 기준: https://creators.vrchat.com/avatars/avatar-performance-ranking-system (2026-10 확인)
// - VRChat처럼 꺼 둔 GameObject·렌더러도 센다.
// - PhysBone은 IR physBones/physBoneColliders로 센다. 텍스처 메모리·파티클 등은 IR에 정보가 없어 빠진다 (화면에 그렇게 안내).
// VRChat SDK 코드는 쓰지 않는다 — 공개된 표의 숫자만 옮겼다.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public enum PerfRank { Excellent, Good, Medium, Poor, VeryPoor }
    public enum PerfPlatform { PC, Mobile }

    public sealed class PerfItem
    {
        public string Key = "", Label = "";
        public double Value;
        public string Display = "";
        public PerfRank Rank;
        public double[] Limits = Array.Empty<double>(); // Excellent, Good, Medium, Poor 상한
    }

    public sealed class AvatarStats
    {
        public int Triangles, SkinnedMeshes, BasicMeshes, MaterialSlots, Bones;
        public int PhysBones, PhysBoneTransforms, PhysBoneColliders, PhysBoneChecks;
        /// <summary>렌더러가 보낸 월드 범위 (min/max). 연결 전에는 null.</summary>
        public float[]? BoundsSize;

        /// <param name="triangles">메시 id → 삼각형 수 (메시 편집 결과). null이면 IR 값.</param>
        public static AvatarStats From(IrScene s, float[]? boundsSize = null, Func<string, int>? triangles = null)
        {
            var st = new AvatarStats { BoundsSize = boundsSize };
            var bones = new HashSet<int>();
            foreach (var r in s.Renderers)
            {
                bool skinned = r.Type != "MeshRenderer";
                if (skinned) st.SkinnedMeshes++; else st.BasicMeshes++;
                st.MaterialSlots += r.Materials.Count;
                var mesh = s.FindMesh(r.Mesh);
                if (mesh != null) st.Triangles += triangles != null ? triangles(mesh.Id) : TriangleCount(mesh);
                if (skinned) foreach (var b in r.Bones) if (b >= 0) bones.Add(b);
            }
            st.Bones = bones.Count;
            CountPhysBones(s, st);
            return st;
        }

        /// <summary>
        /// PhysBone 수치. 컴포넌트·콜라이더는 꺼 둔 것도 센다.
        /// 영향받는 Transform = 루트부터 자식 전부 (Ignore와 다른 PhysBone 루트 아래는 뺌, 렌더러의 PhysBoneSim과 같은 범위).
        /// 충돌 검사 수 = PhysBone마다 (영향받는 Transform 수 × 그 PhysBone이 쓰는 콜라이더 수)의 합 (VRChat 설명: 콜라이더마다 영향 줄 수 있는 Transform 수의 합).
        /// Allow Collision은 목록 밖 콜라이더를 받을지라서 세지 않는다 (2026-11-01: 꺼져 있어도 목록의 콜라이더와는 부딪힌다, PhysBoneSim과 같게).
        /// </summary>
        static void CountPhysBones(IrScene s, AvatarStats st)
        {
            st.PhysBones = s.PhysBones.Count;
            st.PhysBoneColliders = s.PhysBoneColliders.Count;
            if (s.PhysBones.Count == 0) return;
            var children = new List<int>[s.Nodes.Count];
            for (int i = 0; i < s.Nodes.Count; i++)
            {
                int p = s.Nodes[i].Parent;
                if (p >= 0 && p < children.Length) (children[p] ??= new List<int>()).Add(i);
            }
            var roots = new HashSet<int>(s.PhysBones.Select(pb => pb.RootNode));
            foreach (var pb in s.PhysBones)
            {
                int root = pb.RootNode;
                if (root < 0 || root >= s.Nodes.Count) continue;
                var ignore = new HashSet<int>(pb.Ignore);
                int n = 0;
                var stack = new Stack<int>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    int t = stack.Pop();
                    n++;
                    if (children[t] == null) continue;
                    foreach (var c in children[t]) if (!ignore.Contains(c) && !roots.Contains(c)) stack.Push(c);
                }
                st.PhysBoneTransforms += n;
                st.PhysBoneChecks += n * pb.Colliders.Count(ci => ci >= 0 && ci < s.PhysBoneColliders.Count);
            }
        }

        public static int TriangleCount(IrMesh m)
        {
            int t = 0;
            foreach (var sm in m.Submeshes)
            {
                switch (sm.Topology)
                {
                    case "Triangles": t += sm.Indices.Count / 3; break;
                    case "Quads": t += sm.Indices.Count / 4 * 2; break;
                }
            }
            return t;
        }

        // 표: Excellent, Good, Medium, Poor 상한. 넘으면 Very Poor
        static readonly Dictionary<string, (double[] Pc, double[] Mobile)> Table = new Dictionary<string, (double[], double[])>
        {
            ["triangles"] = (new double[] { 32000, 70000, 70000, 70000 }, new double[] { 7500, 10000, 15000, 20000 }),
            ["skinned"] = (new double[] { 1, 2, 8, 16 }, new double[] { 1, 1, 2, 2 }),
            ["basic"] = (new double[] { 4, 8, 16, 24 }, new double[] { 1, 1, 2, 2 }),
            ["materials"] = (new double[] { 4, 8, 16, 32 }, new double[] { 1, 1, 2, 4 }),
            ["bones"] = (new double[] { 75, 150, 256, 400 }, new double[] { 75, 90, 150, 150 }),
            ["pbComponents"] = (new double[] { 4, 8, 16, 32 }, new double[] { 0, 4, 6, 8 }),
            ["pbTransforms"] = (new double[] { 16, 64, 128, 256 }, new double[] { 0, 16, 32, 64 }),
            ["pbColliders"] = (new double[] { 4, 8, 16, 32 }, new double[] { 0, 4, 8, 16 }),
            ["pbChecks"] = (new double[] { 32, 128, 256, 512 }, new double[] { 0, 16, 32, 64 }),
        };
        // 범위 (x, y, z 미터) — PC·모바일 같음
        static readonly float[][] BoundsLimits = { new[] { 2.5f, 2.5f, 2.5f }, new[] { 4f, 4f, 4f }, new[] { 5f, 6f, 5f }, new[] { 5f, 6f, 5f } };

        public static PerfRank RankOf(double value, double[] limits)
        {
            for (int i = 0; i < limits.Length; i++) if (value <= limits[i]) return (PerfRank)i;
            return PerfRank.VeryPoor;
        }

        public static PerfRank BoundsRank(float[] size)
        {
            for (int i = 0; i < BoundsLimits.Length; i++)
                if (size[0] <= BoundsLimits[i][0] && size[1] <= BoundsLimits[i][1] && size[2] <= BoundsLimits[i][2]) return (PerfRank)i;
            return PerfRank.VeryPoor;
        }

        public List<PerfItem> Items(PerfPlatform p)
        {
            PerfItem Make(string key, string label, int v)
            {
                var lim = p == PerfPlatform.PC ? Table[key].Pc : Table[key].Mobile;
                return new PerfItem { Key = key, Label = label, Value = v, Display = v.ToString("N0"), Limits = lim, Rank = RankOf(v, lim) };
            }
            var list = new List<PerfItem>
            {
                Make("triangles", "삼각형", Triangles),
                Make("skinned", "스킨드 메시", SkinnedMeshes),
                Make("basic", "일반 메시", BasicMeshes),
                Make("materials", "머티리얼 슬롯", MaterialSlots),
                Make("bones", "본", Bones),
                Make("pbComponents", "PhysBone 컴포넌트", PhysBones),
                Make("pbTransforms", "PhysBone 영향 Transform", PhysBoneTransforms),
                Make("pbColliders", "PhysBone 콜라이더", PhysBoneColliders),
                Make("pbChecks", "PhysBone 충돌 검사 수", PhysBoneChecks),
            };
            if (BoundsSize != null)
                list.Add(new PerfItem
                {
                    Key = "bounds", Label = "범위", Value = BoundsSize.Max(),
                    Display = $"{BoundsSize[0]:0.00}×{BoundsSize[1]:0.00}×{BoundsSize[2]:0.00} m", Rank = BoundsRank(BoundsSize),
                });
            return list;
        }

        public PerfRank Overall(PerfPlatform p) => Items(p).Select(i => i.Rank).DefaultIfEmpty(PerfRank.Excellent).Max();

        public static string Label(PerfRank r) => r switch
        {
            PerfRank.Excellent => "Excellent", PerfRank.Good => "Good", PerfRank.Medium => "Medium", PerfRank.Poor => "Poor", _ => "Very Poor",
        };

        /// <summary>한 등급 올리려면 얼마나 줄여야 하는지. 이미 Excellent거나 표가 없는 항목이면 null.</summary>
        public static string? Hint(PerfItem it)
        {
            if (it.Rank == PerfRank.Excellent || it.Limits.Length == 0) return null;
            var limit = it.Limits[Math.Min((int)it.Rank, it.Limits.Length) - 1];
            // PC 삼각형처럼 상한이 같은 등급이 이어지면 그 상한에서 가장 좋은 등급을 알려 준다
            return $"{Label(RankOf(limit, it.Limits))}까지 {it.Value - limit:N0} 줄이기";
        }
    }
}
