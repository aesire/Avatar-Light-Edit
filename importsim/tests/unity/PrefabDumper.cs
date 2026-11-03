// 검증 전용 Unity 에디터 스크립트: 실제 Unity가 로드한 프리팹을 importsim 과 같은 JSON 형식으로 덤프.
// Unity.exe -batchmode -projectPath <proj> -executeMethod PrefabDumper.Run -dumpOut <dir> -quit
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PrefabDumper
{
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        var outDir = args[Array.IndexOf(args, "-dumpOut") + 1];
        Directory.CreateDirectory(outDir);
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".prefab")) continue;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(Q(path)).Append(",\"guid\":").Append(Q(guid)).Append(",\"root\":");
            Node(sb, go);
            sb.Append('}');
            File.WriteAllText(Path.Combine(outDir, guid + ".json"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    static void Node(StringBuilder sb, GameObject go)
    {
        sb.Append("{\"name\":").Append(Q(go.name)).Append(",\"active\":").Append(go.activeSelf ? "true" : "false").Append(",\"components\":[");
        bool first = true;
        foreach (var c in go.GetComponents<Component>())
        {
            if (c is Transform) continue;
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"type\":").Append(Q(c == null ? "MonoBehaviour" : c.GetType().Name));
            if (c is Renderer r) sb.Append(",\"enabled\":").Append(r.enabled ? "true" : "false");
            if (c is Behaviour b) sb.Append(",\"enabled\":").Append(b.enabled ? "true" : "false");
            if (c is MeshFilter mf) sb.Append(",\"mesh\":").Append(RefJson(mf.sharedMesh));
            if (c is SkinnedMeshRenderer smr) sb.Append(",\"mesh\":").Append(RefJson(smr.sharedMesh));
            if (c is Renderer rr)
            {
                sb.Append(",\"materials\":[");
                var mats = rr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) { if (i > 0) sb.Append(','); sb.Append(RefJson(mats[i])); }
                sb.Append(']');
            }
            if (c is SkinnedMeshRenderer s2 && s2.sharedMesh != null)
            {
                sb.Append(",\"blendShapes\":[");
                for (int i = 0; i < s2.sharedMesh.blendShapeCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(Q(s2.sharedMesh.GetBlendShapeName(i)))
                      .Append(",\"weight\":").Append(s2.GetBlendShapeWeight(i).ToString("R", CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append(']');
            }
            if (c is Animator an) sb.Append(",\"avatar\":").Append(RefJson(an.avatar));
            if (c is SkinnedMeshRenderer s3)
            {
                var root = go.transform.root;
                sb.Append(",\"rootBone\":").Append(Q(s3.rootBone ? PathOf(root, s3.rootBone) : "(없음)"));
                sb.Append(",\"bones\":[");
                var bs = s3.bones;
                for (int i = 0; i < bs.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Q(bs[i] ? PathOf(root, bs[i]) : "(없음)")); }
                sb.Append(']');
            }
            sb.Append('}');
        }
        var t = go.transform;
        sb.Append("],\"p\":[").Append(F(t.localPosition.x)).Append(',').Append(F(t.localPosition.y)).Append(',').Append(F(t.localPosition.z))
          .Append("],\"r\":[").Append(F(t.localRotation.x)).Append(',').Append(F(t.localRotation.y)).Append(',').Append(F(t.localRotation.z)).Append(',').Append(F(t.localRotation.w))
          .Append("],\"s\":[").Append(F(t.localScale.x)).Append(',').Append(F(t.localScale.y)).Append(',').Append(F(t.localScale.z));
        sb.Append("],\"children\":[");
        for (int i = 0; i < go.transform.childCount; i++)
        {
            if (i > 0) sb.Append(',');
            Node(sb, go.transform.GetChild(i).gameObject);
        }
        sb.Append("]}");
    }

    static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    static string PathOf(Transform root, Transform t)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
        return string.Join("/", parts);
    }

    static string RefJson(UnityEngine.Object o)
    {
        if (o == null) return "null";
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string g, out long id)) return "{\"fileID\":0}";
        return "{\"fileID\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"guid\":" + Q(g) + "}";
    }

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
