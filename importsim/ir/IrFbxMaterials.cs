// FBX 내장 머티리얼 → IR 머티리얼 값 (2026-10-23). 외부 .mat으로 바꿔 두지 않은 FBX 머티리얼은 Unity가 임포트할 때
// FBX 머티리얼 속성으로 Standard 머티리얼을 만든다 (Unity FBXMaterialDescriptionPreprocessor 규칙):
//   불투명도 = Opacity, 없으면 1 − TransparencyFactor (1이면 1) → 1보다 작으면 Transparent 모드(_Mode 3, 미리 곱한 알파, 큐 3000)
//   DiffuseColor 텍스처 → _MainTex, _Color = 흰색 × DiffuseFactor (알파 = 불투명도) / 텍스처가 없으면 _Color = DiffuseColor × DiffuseFactor (Linear 프로젝트라 감마로)
//   Bump·NormalMap 텍스처 → _BumpMap + _NORMALMAP, _BumpScale = BumpFactor
//   EmissiveColor 텍스처 → _EmissionMap, _EmissionColor 흰색 / 색이면 × EmissiveFactor, 0보다 크면 _EMISSION
//   ReflectionFactor → _Metallic, Shininess → _Glossiness = √(Shininess × 0.01)
// FBX 파일에 적힌 속성만 쓴다 (FBX SDK 기본값까지 재현하지는 않음 — 없는 값은 Standard 기본값).
// 텍스처는 Unity처럼 파일 이름으로 찾는다: FBX 옆 → 옆·위 폴더의 Textures → 프로젝트 전체. FBX 안에 묻힌 텍스처는 꺼내지 않는다(경고).
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        readonly Dictionary<string, FbxScene?> fbxStructure = new Dictionary<string, FbxScene?>();
        Dictionary<string, List<AssetEntry>>? _byFileName;

        /// <summary>FBX 내장 머티리얼 name의 값을 j에 채운다. FBX를 읽지 못하거나 머티리얼이 없으면 false.</summary>
        bool FbxMaterial(AssetEntry fbx, string name, long fileId, JNode j, List<string> texGuids)
        {
            if (!fbxStructure.TryGetValue(fbx.Guid, out var scene))
            {
                try { scene = FbxScene.Load(fbx.Data ?? Array.Empty<byte>()); }
                catch (Exception e) { Warnings.Add($"{fbx.Path}: 머티리얼을 읽지 못함: {e.Message}"); scene = null; }
                fbxStructure[fbx.Guid] = scene;
            }
            // 이름으로, 이름을 모르면 Unity가 이름에서 만드는 fileID로 찾는다
            var mat = scene?.Objects.Values.FirstOrDefault(o => o.Cls == "Material" && (o.Name == name || Xxh64.FileId("Material", o.Name) == fileId));
            if (scene == null || mat == null) return false;
            j.Set("name", mat.Name);

            var tex = new Dictionary<string, FbxObject>();
            foreach (var (child, parent, prop) in scene.OP)
                if (parent == mat.Id && scene.Objects.TryGetValue(child, out var t) && t.Cls == "Texture" && !tex.ContainsKey(prop)) tex[prop] = t;
            double? Num(string k) => mat.Node.P70(k) is List<object> v && v.Count > 0 ? FbxNode.Num(v[0]) : (double?)null;
            Vec3? Col(string k) => mat.Node.P70(k) is List<object> v && v.Count >= 3 ? new Vec3(FbxNode.Num(v[0]), FbxNode.Num(v[1]), FbxNode.Num(v[2])) : (Vec3?)null;

            var texEnvs = JNode.Obj(); var floats = JNode.Obj(); var colors = JNode.Obj(); var kws = JNode.Arr();
            int queue = -1;
            bool SetTex(string unityProp, string fbxProp)
            {
                if (!tex.TryGetValue(fbxProp, out var t)) return false;
                var file = FbxTextureFile(fbx, t);
                if (file == null) return false;
                if (!texGuids.Contains(file.Guid)) texGuids.Add(file.Guid);
                var sc = t.Node.P70Vec("Scaling", new Vec3(1, 1, 1)); var tr = t.Node.P70Vec("Translation", new Vec3(0, 0, 0));
                texEnvs.Set(unityProp, JNode.Obj().Set("texture", file.Guid)
                    .Set("scale", JNode.FloatArray(new[] { (float)sc.X, (float)sc.Y })).Set("offset", JNode.FloatArray(new[] { (float)tr.X, (float)tr.Y })));
                return true;
            }

            double opacity = Num("Opacity") ?? (Num("TransparencyFactor") is double tf ? (tf == 1 ? 1 : 1 - tf) : 1);
            if (opacity < 1 || tex.ContainsKey("TransparentColor"))
            {
                floats.Set("_Mode", 3).Set("_SrcBlend", 1).Set("_DstBlend", 10).Set("_ZWrite", 0);
                kws.Add("_ALPHAPREMULTIPLY_ON");
                queue = 3000;
            }
            double diffuseFactor = Num("DiffuseFactor") ?? 1;
            if (SetTex("_MainTex", "DiffuseColor"))
                colors.Set("_Color", JNode.FloatArray(new[] { (float)diffuseFactor, (float)diffuseFactor, (float)diffuseFactor, (float)opacity }));
            else if (Col("DiffuseColor") is Vec3 dc)
            {
                var c = dc * diffuseFactor;
                colors.Set("_Color", JNode.FloatArray(new[] { Gamma(c.X), Gamma(c.Y), Gamma(c.Z), (float)opacity }));
            }
            if (SetTex("_BumpMap", "Bump") || SetTex("_BumpMap", "NormalMap"))
            {
                kws.Add("_NORMALMAP");
                if (Num("BumpFactor") is double bf) floats.Set("_BumpScale", bf);
            }
            if (SetTex("_EmissionMap", "EmissiveColor"))
            {
                colors.Set("_EmissionColor", JNode.FloatArray(new[] { 1f, 1f, 1f, 1f }));
                if ((Num("EmissiveFactor") ?? 0) > 0) kws.Add("_EMISSION");
            }
            else if (Col("EmissiveColor") is Vec3 ec && ec.Length > 1e-6)
            {
                double ef = Num("EmissiveFactor") ?? 1;
                var e = ec * ef;
                // Unity는 (r, g, b, 1) 벡터 전체에 EmissiveFactor를 곱한다 → 알파도 세기 값
                colors.Set("_EmissionColor", JNode.FloatArray(new[] { (float)e.X, (float)e.Y, (float)e.Z, (float)ef }));
                if (ef > 0) kws.Add("_EMISSION");
            }
            if (Num("ReflectionFactor") is double rf) floats.Set("_Metallic", rf);
            if (Num("Shininess") is double sh) floats.Set("_Glossiness", Math.Sqrt(Math.Max(0, sh) * 0.01));

            j.Set("texEnvs", texEnvs).Set("floats", floats).Set("colors", colors).Set("keywords", kws).Set("renderQueue", queue);
            return true;
        }

        /// Linear 프로젝트에서 Unity가 하는 Color.gamma (선형 → sRGB)
        static float Gamma(double v) => (float)(v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);

        /// FBX 텍스처가 가리키는 이미지 에셋. Unity처럼 파일 이름으로 찾는다.
        AssetEntry? FbxTextureFile(AssetEntry fbx, FbxObject tex)
        {
            string? path = null;
            foreach (var key in new[] { "RelativeFilename", "FileName" })
                if (tex.Node.Find(key)?.Props.FirstOrDefault() is string s && s.Length > 0) { path = s; break; }
            if (path == null) { Warnings.Add($"{fbx.Path}: 텍스처 '{tex.Name}'에 파일 이름이 없음"); return null; }
            var file = System.IO.Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant();

            if (_byFileName == null)
            {
                _byFileName = new Dictionary<string, List<AssetEntry>>();
                foreach (var a in db.ByGuid.Values)
                    if (ImageExt.Contains(a.Ext))
                    {
                        var k = System.IO.Path.GetFileName(a.Path).ToLowerInvariant();
                        if (!_byFileName.TryGetValue(k, out var l)) _byFileName[k] = l = new List<AssetEntry>();
                        l.Add(a);
                    }
            }
            if (!_byFileName.TryGetValue(file, out var found) || found.Count == 0)
            {
                Warnings.Add($"{fbx.Path}: 텍스처 파일 '{file}'을 찾지 못함 (FBX 안에 묻힌 텍스처는 꺼내지 않음)");
                return null;
            }
            // FBX 폴더부터 위로 올라가며: 그 폴더 → 그 폴더의 Textures
            var dir = System.IO.Path.GetDirectoryName(fbx.Path)?.Replace('\\', '/') ?? "";
            while (true)
            {
                foreach (var cand in new[] { dir, dir.Length == 0 ? "Textures" : dir + "/Textures" })
                {
                    var hit = found.FirstOrDefault(a => string.Equals(System.IO.Path.GetDirectoryName(a.Path)?.Replace('\\', '/') ?? "", cand, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit;
                }
                if (dir.Length == 0) break;
                int cut = dir.LastIndexOf('/');
                dir = cut < 0 ? "" : dir.Substring(0, cut);
            }
            return found.OrderBy(a => a.Path, StringComparer.Ordinal).First();
        }
    }
}
