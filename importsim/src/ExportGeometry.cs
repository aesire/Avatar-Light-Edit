using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImportSim
{
    /// <summary>모델의 Transform 값과 메시 데이터를 JSON 으로 (tests/unity/GeometryDumper.cs 와 같은 형식)</summary>
    public static class ExportGeometry
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string Json(SimFile f)
        {
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(Q(f.Path)).Append(",\"guid\":").Append(Q(f.Guid)).Append(",\"nodes\":[");
            var root = f.Root;
            var paths = new Dictionary<long, string>();   // Transform id -> 경로
            bool first = true;
            if (root != null) Node(sb, f, root, "", paths, ref first);
            sb.Append("],\"meshes\":[");
            first = true;
            foreach (var m in f.Meshes.Values)
            {
                if (!first) sb.Append(','); first = false;
                Mesh(sb, m);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static void Node(StringBuilder sb, SimFile f, SimObject go, string path, Dictionary<long, string> paths, ref bool first)
        {
            SimObject? tr = null, smr = null, mf = null;
            foreach (var c in go.Props.GetValueOrDefault("m_Component") as List<object?> ?? new List<object?>())
            {
                var o = f.Get(Ref.FileId((c as Map)?.GetValueOrDefault("component")));
                if (o?.ClassId == 4) tr = o; else if (o?.ClassId == 137) smr = o; else if (o?.ClassId == 33) mf = o;
            }
            if (tr == null) return;
            paths[tr.Id] = path;
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"path\":").Append(Q(path))
              .Append(",\"p\":").Append(V(tr.Props, "m_LocalPosition", "x", "y", "z"))
              .Append(",\"r\":").Append(V(tr.Props, "m_LocalRotation", "x", "y", "z", "w"))
              .Append(",\"s\":").Append(V(tr.Props, "m_LocalScale", "x", "y", "z"));
            if (smr != null)
            {
                sb.Append(",\"mesh\":").Append(Q(MeshName(f, smr)));
                sb.Append(",\"rootBone\":").Append(Q(PathOfTransform(f, Ref.FileId(smr.Props.GetValueOrDefault("m_RootBone")))));
                sb.Append(",\"bones\":[").Append(string.Join(",", (smr.Props.GetValueOrDefault("m_Bones") as List<object?> ?? new List<object?>())
                    .Select(b => Q(PathOfTransform(f, Ref.FileId(b)))))).Append(']');
            }
            if (mf != null) sb.Append(",\"mesh\":").Append(Q(MeshName(f, mf)));
            sb.Append('}');
            foreach (var ch in tr.Props.GetValueOrDefault("m_Children") as List<object?> ?? new List<object?>())
                if (f.Get(Ref.FileId(ch)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                    Node(sb, f, cg, path.Length == 0 ? Name(cg) : path + "/" + Name(cg), paths, ref first);
        }

        static string Name(SimObject go) => go.Props.GetValueOrDefault("m_Name") as string ?? "";

        static string PathOfTransform(SimFile f, long trId)
        {
            var parts = new List<string>();
            for (var t = f.Get(trId); t != null && Ref.Local(t.Props, "m_Father") != 0; t = f.Get(Ref.Local(t.Props, "m_Father")))
                if (f.Get(Ref.Local(t.Props, "m_GameObject")) is SimObject g) parts.Insert(0, Name(g));
            return string.Join("/", parts);
        }

        static string MeshName(SimFile f, SimObject comp) =>
            f.Meshes.TryGetValue(Ref.FileId(comp.Props.GetValueOrDefault("m_Mesh")), out var m) ? m.Name : "";

        static void Mesh(StringBuilder sb, SimMesh m)
        {
            sb.Append("{\"name\":").Append(Q(m.Name)).Append(",\"vertexCount\":").Append(m.Vertices.Count);
            sb.Append(",\"vertices\":").Append(Arr(m.Vertices.SelectMany(v => new[] { v.X, v.Y, v.Z })));
            sb.Append(",\"normals\":").Append(Arr(m.Normals.SelectMany(v => new[] { v.X, v.Y, v.Z })));
            for (int ch = 0; ch < m.Uvs.Count; ch++)
                sb.Append(",\"uv").Append(ch).Append("\":").Append(Arr(m.Uvs[ch].SelectMany(v => new[] { v.u, v.v })));
            if (m.Colors != null) sb.Append(",\"colors\":").Append(Arr(m.Colors.SelectMany(c => c)));
            if (m.Tangents != null) sb.Append(",\"tangents\":").Append(Arr(m.Tangents.SelectMany(t => t.Select(x => (double)x))));
            sb.Append(",\"submeshes\":[").Append(string.Join(",", m.Submeshes.Select(s => "{\"triangles\":[" + string.Join(",", s) + "]}"))).Append(']');
            if (m.ControlPoints != null && System.Environment.GetEnvironmentVariable("IMPORTSIM_DEBUG_CP") == "1") sb.Append(",\"cp\":[").Append(string.Join(",", m.ControlPoints)).Append(']');
            if (m.BoneIndices != null && m.BoneWeights != null)
            {
                sb.Append(",\"boneWeights\":").Append(Arr(Enumerable.Range(0, m.BoneIndices.Count)
                    .SelectMany(i => Enumerable.Range(0, 4).SelectMany(k => new double[] { m.BoneIndices[i][k], m.BoneWeights[i][k] }))));
                sb.Append(",\"bindposes\":").Append(Arr(m.Bindposes.SelectMany(p => Enumerable.Range(0, 16).Select(i => p.M[i % 4, i / 4]))));
            }
            sb.Append(",\"blendShapes\":[");
            for (int s = 0; s < m.BlendShapes.Count; s++)
            {
                if (s > 0) sb.Append(',');
                var bs = m.BlendShapes[s];
                sb.Append("{\"name\":").Append(Q(bs.Name)).Append(",\"frames\":[");
                for (int fi = 0; fi < bs.Frames.Count; fi++)
                {
                    if (fi > 0) sb.Append(',');
                    var fr = bs.Frames[fi];
                    sb.Append("{\"weight\":").Append(F(fr.Weight)).Append(",\"dv\":").Append(Sparse(fr.DeltaVertices)).Append(",\"dn\":").Append(Sparse(fr.DeltaNormals)).Append(",\"dt\":").Append(Sparse(fr.DeltaTangents)).Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
        }

        static string Sparse(Dictionary<int, Vec3> d) =>
            "[" + string.Join(",", d.OrderBy(k => k.Key).Select(k => k.Key + "," + F(k.Value.X) + "," + F(k.Value.Y) + "," + F(k.Value.Z))) + "]";

        static string V(Map props, string key, params string[] comps)
        {
            var m = props.GetValueOrDefault(key) as Map ?? new Map();
            return "[" + string.Join(",", comps.Select(c => double.TryParse(m.GetValueOrDefault(c) as string, NumberStyles.Float, C, out var v) ? F(v) : "0")) + "]";
        }

        static string Arr(IEnumerable<double> xs) => "[" + string.Join(",", xs.Select(F)) + "]";
        static string F(double v) => ((float)v).ToString("R", C);
        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }
}
