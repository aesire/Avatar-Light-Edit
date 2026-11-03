// FBX 내장 머티리얼 정답 덤프 (2026-10-23): importsim ir/IrFbxMaterials.cs와 비교한다.
// Unity.exe -batchmode -projectPath <프로젝트> -executeMethod MaterialDumper.Run -dumpOut <폴더> -under Assets/... -quit
// 모델마다 <guid>.json = [{ name, shader, renderQueue, keywords, floats: {...}, colors: {...}, textures: { 속성: 텍스처 guid } }]
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class MaterialDumper
{
    static readonly string[] Floats = { "_Mode", "_SrcBlend", "_DstBlend", "_ZWrite", "_BumpScale", "_Metallic", "_Glossiness" };
    static readonly string[] Colors = { "_Color", "_EmissionColor" };
    static readonly string[] Textures = { "_MainTex", "_BumpMap", "_EmissionMap" };

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
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var m in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(Q(m.name)).Append(",\"shader\":").Append(Q(m.shader.name))
                  .Append(",\"renderQueue\":").Append(m.renderQueue)
                  .Append(",\"keywords\":[").Append(string.Join(",", m.shaderKeywords.OrderBy(k => k).Select(Q))).Append("]");
                sb.Append(",\"floats\":{").Append(string.Join(",", Floats.Where(m.HasProperty).Select(k => Q(k) + ":" + F(m.GetFloat(k))))).Append('}');
                sb.Append(",\"colors\":{").Append(string.Join(",", Colors.Where(m.HasProperty).Select(k =>
                {
                    var c = m.GetColor(k);
                    return Q(k) + ":[" + F(c.r) + "," + F(c.g) + "," + F(c.b) + "," + F(c.a) + "]";
                }))).Append('}');
                sb.Append(",\"textures\":{").Append(string.Join(",", Textures.Where(m.HasProperty).Where(k => m.GetTexture(k) != null).Select(k =>
                    Q(k) + ":" + Q(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m.GetTexture(k))))))).Append('}');
                sb.Append('}');
            }
            sb.Append(']');
            File.WriteAllText(Path.Combine(outDir, guid + ".json"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
