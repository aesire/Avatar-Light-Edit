// 검증 전용: Unity가 임포트한 모델의 Transform 값과 메시 데이터를 importsim --geometry 출력과 같은 JSON 으로 덤프.
// Unity.exe -batchmode -projectPath <proj> -executeMethod GeometryDumper.Run -dumpOut <dir> [-under Assets/Models] -quit
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class GeometryDumper
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        var outDir = args[Array.IndexOf(args, "-dumpOut") + 1];
        int u = Array.IndexOf(args, "-under");
        var under = u >= 0 ? args[u + 1] : "Assets";
        Directory.CreateDirectory(outDir);
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { under }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(Q(path)).Append(",\"guid\":").Append(Q(guid)).Append(",\"nodes\":[");
            bool first = true;
            Walk(sb, root.transform, root.transform, ref first);
            sb.Append("],\"meshes\":[");
            first = true;
            foreach (var m in AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<Mesh>())
            {
                if (!first) sb.Append(','); first = false;
                MeshJson(sb, m);
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(outDir, guid + ".json"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    static string PathOf(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
        return string.Join("/", parts);
    }

    static void Walk(StringBuilder sb, Transform root, Transform t, ref bool first)
    {
        if (!first) sb.Append(','); first = false;
        sb.Append("{\"path\":").Append(Q(PathOf(root, t)))
          .Append(",\"p\":").Append(V(t.localPosition))
          .Append(",\"r\":[").Append(F(t.localRotation.x)).Append(',').Append(F(t.localRotation.y)).Append(',')
          .Append(F(t.localRotation.z)).Append(',').Append(F(t.localRotation.w)).Append(']')
          .Append(",\"s\":").Append(V(t.localScale));
        var smr = t.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            sb.Append(",\"mesh\":").Append(Q(smr.sharedMesh ? smr.sharedMesh.name : ""));
            sb.Append(",\"rootBone\":").Append(Q(smr.rootBone ? PathOf(root, smr.rootBone) : ""));
            sb.Append(",\"bones\":[").Append(string.Join(",", smr.bones.Select(b => Q(b ? PathOf(root, b) : "")))).Append(']');
            // 임포트할 때 Unity가 정한 렌더러 범위 (루트 뼈 공간) — importsim IrExport.SkinnedBounds와 비교 (2026-10-23)
            sb.Append(",\"localBounds\":[").Append(V(smr.localBounds.center, false)).Append(',').Append(V(smr.localBounds.extents, false)).Append(']');
        }
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null) sb.Append(",\"mesh\":").Append(Q(mf.sharedMesh ? mf.sharedMesh.name : ""));
        sb.Append('}');
        foreach (Transform c in t) Walk(sb, root, c, ref first);
    }

    static void MeshJson(StringBuilder sb, Mesh m)
    {
        sb.Append("{\"name\":").Append(Q(m.name)).Append(",\"vertexCount\":").Append(m.vertexCount);
        sb.Append(",\"bounds\":[").Append(V(m.bounds.center, false)).Append(',').Append(V(m.bounds.extents, false)).Append(']');
        sb.Append(",\"vertices\":").Append(Arr(m.vertices.SelectMany(v => new[] { v.x, v.y, v.z })));
        sb.Append(",\"normals\":").Append(Arr(m.normals.SelectMany(v => new[] { v.x, v.y, v.z })));
        for (int ch = 0; ch < 4; ch++)
        {
            var uv = new List<Vector2>();
            m.GetUVs(ch, uv);
            if (uv.Count > 0) sb.Append(",\"uv").Append(ch).Append("\":").Append(Arr(uv.SelectMany(v => new[] { v.x, v.y })));
        }
        if (m.colors.Length > 0) sb.Append(",\"colors\":").Append(Arr(m.colors.SelectMany(c => new[] { c.r, c.g, c.b, c.a })));
        if (m.tangents.Length > 0) sb.Append(",\"tangents\":").Append(Arr(m.tangents.SelectMany(t => new[] { t.x, t.y, t.z, t.w })));
        sb.Append(",\"submeshes\":[");
        for (int s = 0; s < m.subMeshCount; s++)
        {
            if (s > 0) sb.Append(',');
            sb.Append("{\"triangles\":[").Append(string.Join(",", m.GetTriangles(s))).Append("]}");
        }
        sb.Append(']');
        var bw = m.boneWeights;
        if (bw.Length > 0)
        {
            sb.Append(",\"boneWeights\":").Append(Arr(bw.SelectMany(b => new[] { (float)b.boneIndex0, b.weight0, b.boneIndex1, b.weight1, b.boneIndex2, b.weight2, b.boneIndex3, b.weight3 })));
            sb.Append(",\"bindposes\":").Append(Arr(m.bindposes.SelectMany(p => Enumerable.Range(0, 16).Select(i => p[i % 4, i / 4]))));
        }
        // 탄젠트: 임포트 값과, 런타임 RecalculateTangents 결과의 차이
        if (m.tangents.Length > 0)
        {
            var copy = UnityEngine.Object.Instantiate(m);
            copy.RecalculateTangents();
            var a = m.tangents; var b = copy.tangents;
            double maxd = 0;
            for (int i = 0; i < a.Length; i++) maxd = Math.Max(maxd, (a[i] - b[i]).magnitude);
            sb.Append(",\"tangentRecalcMaxDiff\":").Append(F((float)maxd));
            UnityEngine.Object.DestroyImmediate(copy);
        }
        sb.Append(",\"blendShapes\":[");
        var dv = new Vector3[m.vertexCount]; var dn = new Vector3[m.vertexCount]; var dt = new Vector3[m.vertexCount];
        for (int s = 0; s < m.blendShapeCount; s++)
        {
            if (s > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(Q(m.GetBlendShapeName(s))).Append(",\"frames\":[");
            for (int f = 0; f < m.GetBlendShapeFrameCount(s); f++)
            {
                if (f > 0) sb.Append(',');
                m.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                sb.Append("{\"weight\":").Append(F(m.GetBlendShapeFrameWeight(s, f)));
                sb.Append(",\"dv\":").Append(Sparse(dv)).Append(",\"dn\":").Append(Sparse(dn)).Append(",\"dt\":").Append(Sparse(dt)).Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
    }

    // [인덱스, x, y, z, 인덱스, x, y, z, ...] (0이 아닌 것만)
    static string Sparse(Vector3[] d)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i].x == 0 && d[i].y == 0 && d[i].z == 0) continue;
            if (!first) sb.Append(','); first = false;
            sb.Append(i).Append(',').Append(F(d[i].x)).Append(',').Append(F(d[i].y)).Append(',').Append(F(d[i].z));
        }
        return sb.Append(']').ToString();
    }

    static string Arr(IEnumerable<float> xs) => "[" + string.Join(",", xs.Select(F)) + "]";
    static string F(float f) => f.ToString("R", C);
    static string V(Vector3 v, bool _ = true) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
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
