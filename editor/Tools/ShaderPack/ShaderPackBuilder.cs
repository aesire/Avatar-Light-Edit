// 셰이더 팩 빌더 (Unity 에디터 스크립트). Host가 전용 프로젝트(%LOCALAPPDATA%\ALEdit\shaderpack-project)의
// Assets/Editor 에 복사해 두고 batchmode로 부른다. 렌더러 프로젝트에는 넣지 않는다.
//   Unity.exe -batchmode -nographics -quit -projectPath <프로젝트> -executeMethod VrcProto.ShaderPack.Builder.Build
//             -packName <이름> -packOut <폴더> [-variantSpec <변형 목록 파일>] -logFile <로그>
// 입력: Host가 Assets/Pack 아래에 원래 경로 그대로 둔 셰이더·인클루드 파일(.meta 포함 → GUID 유지)과 머티리얼.
//       머티리얼은 shader_feature 변형을 팩에 넣기 위해서만 쓴다.
// 출력: <이름>.bundle (StandaloneWindows64 AssetBundle) + <이름>.txt (셰이더 GUID \t 셰이더 이름)
//       -variantSpec 이 있으면 <이름>.variants.txt 도 (넣은 키워드 조합 목록 — 렌더러가 가장 가까운 조합을 고른다)
// 로그: "[ShaderPack] ok|error|skip ..." 줄을 Host가 읽어 사용자에게 보여 준다.
//
// 변형 목록 파일 (배포용 기본 팩처럼 사용자 머티리얼 없이 미리 컴파일할 때). 한 줄에 하나, 탭으로 구분:
//   <셰이더 이름 앞부분>\t<키워드 식>    식은 공백으로 구분한 토큰의 곱: "A" 항상, "?A" 있음·없음, "A|B" 하나씩,
//                                       "@group:접두사" = 그 접두사로 시작하는 선언된 키워드 하나씩
//   <셰이더 이름 앞부분>\t@singles [-제외 …]   선언된 shader_feature 키워드를 하나씩만 켠 조합 전부
//   alias\t<GUID>\t<셰이더 이름>             표에 GUID를 더한다 (같은 프로퍼티를 쓰는 다른 셰이더 → 팩 셰이더로 대신 표시).
//                                            표 줄 끝에 "\talias"가 붙어 렌더러가 근사 표시로 알린다
//   ignore\t<키워드 …>                     미리보기에 영향이 없는 기능 (AudioLink 등). 렌더러가 끄고 근사 표시로 치지 않는다
//   셰이더가 선언하지 않은 키워드는 버린다 (같은 목록을 여러 버전에 쓸 수 있게). 키워드 없는 조합은 늘 넣는다.
//   이 모드에서는 ForwardBase 패스만 기능 키워드 조합을 갖고, 나머지 패스(추가 조명·그림자·외곽선)는 키워드 없는 변형만 남긴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace VrcProto.ShaderPack
{
    public static class Builder
    {
        const string Staging = "Assets/Pack";

        public static void Build()
        {
            int code = 1;
            try { code = Run(Arg("-packName") ?? "pack", Arg("-packOut") ?? throw new ArgumentException("-packOut 없음"), Arg("-variantSpec")); }
            catch (Exception e) { Debug.Log("[ShaderPack] error " + e.Message); }
            finally { Stripper.Active = false; }
            EditorApplication.Exit(code);
        }

        static int Run(string name, string outDir, string spec)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear; // 렌더러와 같게
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var table = new StringBuilder();
            var assets = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { Staging }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (s == null) continue;
                if (ShaderUtil.ShaderHasError(s))
                {
                    var msg = ShaderUtil.GetShaderMessages(s).Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
                        .Select(m => $"{Path.GetFileName(m.file)}:{m.line} {m.message}").FirstOrDefault() ?? "컴파일 오류";
                    Debug.Log($"[ShaderPack] error {s.name} ({path}): {msg}");
                    continue;
                }
                table.Append(guid).Append('\t').Append(s.name).Append('\n');
                assets.Add(path);
                Debug.Log($"[ShaderPack] ok {s.name}");
            }
            if (assets.Count == 0) { Debug.Log("[ShaderPack] error 쓸 수 있는 셰이더가 없음"); return 2; }

            // 팩 셰이더를 쓰는 머티리얼만 함께 넣는다 (그 키워드 조합의 변형이 팩에 들어가게)
            var packShaders = new HashSet<string>(assets);
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Staging }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m != null && m.shader != null && packShaders.Contains(AssetDatabase.GetAssetPath(m.shader))) assets.Add(path);
            }
            Debug.Log($"[ShaderPack] skip 머티리얼 {assets.Count - packShaders.Count}개로 변형 수집");

            string variantsTxt = string.IsNullOrEmpty(spec) ? null : MakeVariants(spec, assets, packShaders, table);
            Stripper.Active = true;

            var tmp = Path.Combine("Temp", "ShaderPackOut");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            // 번들 이름은 팩마다 달라야 한다: 같은 이름의 번들은 렌더러에서 동시에 불러올 수 없다
            var bundle = "vrcproto_" + name.ToLowerInvariant();
            var build = new AssetBundleBuild { assetBundleName = bundle, assetNames = assets.ToArray() };
            var manifest = BuildPipeline.BuildAssetBundles(tmp, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null) { Debug.Log("[ShaderPack] error AssetBundle 빌드 실패"); return 3; }

            Directory.CreateDirectory(outDir);
            File.Copy(Path.Combine(tmp, bundle), Path.Combine(outDir, name + ".bundle"), true);
            File.WriteAllText(Path.Combine(outDir, name + ".txt"), table.ToString(), new UTF8Encoding(false));
            var vpath = Path.Combine(outDir, name + ".variants.txt");
            if (variantsTxt != null) File.WriteAllText(vpath, variantsTxt, new UTF8Encoding(false));
            else if (File.Exists(vpath)) File.Delete(vpath);
            Debug.Log($"[ShaderPack] skip 변형 정리: 남김 {Stripper.Kept}, 뺌 {Stripper.Removed}");
            Debug.Log($"[ShaderPack] done {Path.Combine(outDir, name + ".bundle")}");
            AssetDatabase.DeleteAsset(Staging); // 다음 팩이 섞이지 않게
            return 0;
        }

        // ---------------- 변형 목록 ----------------
        static readonly Regex FeaturePragma = new Regex(@"^[ \t]*#pragma[ \t]+shader_feature\w*[ \t]+(.+)$", RegexOptions.Multiline);

        /// pragma 한 줄의 키워드 묶음. Mandatory: "_" 가 없는 여러 키워드 묶음 (늘 하나가 켜짐, 아무것도 안 켜면 첫 키워드)
        sealed class FeatureGroup { public List<string> Keys = new List<string>(); public bool Mandatory; }

        /// 셰이더 파일에 선언된 shader_feature 키워드 묶음 (pragma 한 줄 = 묶음 하나, 주석 처리된 줄은 뺀다, 선언 순서대로)
        static List<FeatureGroup> DeclaredFeatures(string shaderPath)
        {
            var groups = new List<FeatureGroup>();
            var seen = new HashSet<string>();
            foreach (Match m in FeaturePragma.Matches(File.ReadAllText(shaderPath)))
            {
                var all = new List<string>();
                foreach (var k in m.Groups[1].Value.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (k.StartsWith("//")) break;
                    all.Add(k);
                }
                bool hasOff = all.Any(k => k.Trim('_').Length == 0);
                var g = new FeatureGroup { Mandatory = !hasOff && all.Count > 1 };
                foreach (var k in all) if (k.Trim('_').Length > 0 && seen.Add(k)) g.Keys.Add(k);
                if (g.Keys.Count > 0) groups.Add(g);
            }
            return groups;
        }

        /// 변형 목록 파일대로 키워드 조합마다 머티리얼을 만들어 팩에 넣는다. 반환: .variants.txt 내용
        static string MakeVariants(string specFile, List<string> assets, HashSet<string> packShaders, StringBuilder table)
        {
            var lines = File.ReadAllLines(specFile).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")).Select(l => l.Split('\t')).ToList();
            foreach (var a in lines.Where(p => p[0] == "alias" && p.Length >= 3)) table.Append(a[1].Trim()).Append('\t').Append(a[2].Trim()).Append("\talias\n");
            var dir = Staging + "/_variants";
            Directory.CreateDirectory(dir);
            var outTxt = new StringBuilder();
            int total = 0;
            foreach (var path in packShaders.OrderBy(p => p))
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                var groups = DeclaredFeatures(path);
                var declared = groups.SelectMany(g => g.Keys).ToList();
                var sets = new List<SortedSet<string>>();
                void AddSet(IEnumerable<string> kws)
                {
                    var s = new SortedSet<string>(kws.Where(k => k.Length > 0 && declared.Contains(k)), StringComparer.Ordinal);
                    // 끄기 없는 묶음에서 아무것도 안 켰으면 Unity는 첫 키워드로 컴파일한다 → 목록에도 그렇게 적는다
                    foreach (var g in groups) if (g.Mandatory && !g.Keys.Any(s.Contains)) s.Add(g.Keys[0]);
                    if (!sets.Any(x => x.SetEquals(s))) sets.Add(s);
                }
                foreach (var p in lines.Where(p => p[0] != "alias" && p[0] != "ignore" && p.Length >= 2 && shader.name.StartsWith(p[0].Trim(), StringComparison.Ordinal)))
                {
                    var toks = p[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length > 0 && toks[0] == "@singles")
                    {
                        var except = new HashSet<string>(toks.Skip(1).Where(t => t.StartsWith("-")).Select(t => t.Substring(1)));
                        foreach (var k in declared) if (!except.Contains(k)) AddSet(new[] { k });
                        continue;
                    }
                    var combos = new List<List<string>> { new List<string>() };
                    foreach (var t in toks)
                    {
                        var opts = t.StartsWith("?") ? new List<string> { "", t.Substring(1) }
                                 : t.StartsWith("@group:") ? declared.Where(k => k.StartsWith(t.Substring(7), StringComparison.Ordinal)).ToList()
                                 : t.Split('|').ToList();
                        opts = opts.Where(o => o.Length == 0 || declared.Contains(o)).Distinct().ToList();
                        if (opts.Count == 0) continue; // 이 셰이더에 없는 키워드
                        combos = combos.SelectMany(c => opts.Select(o => new List<string>(c) { o })).ToList();
                    }
                    foreach (var c in combos) AddSet(c);
                }
                AddSet(Array.Empty<string>()); // 키워드 없는 조합은 늘
                // #features: 선언된 키워드. 한 pragma에 여럿인 열거형 묶음은 "|"로 잇고 (렌더러가 켜기/끄기 키워드보다 낮게 친다),
                //            끄기가 없는 묶음은 앞에 "!" (아무것도 안 켜면 첫 키워드)
                outTxt.Append("#features\t").Append(shader.name).Append('\t').Append(string.Join(" ", groups.Select(g => (g.Mandatory ? "!" : "") + string.Join("|", g.Keys)))).Append('\n');
                // #ignore: 미리보기에 영향이 없는 기능 (렌더러가 끄고, 근사 표시로 치지 않는다)
                var ignore = lines.Where(p => p[0] == "ignore" && p.Length >= 2).SelectMany(p => p[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Where(declared.Contains).Distinct().ToList();
                if (ignore.Count > 0) outTxt.Append("#ignore\t").Append(shader.name).Append('\t').Append(string.Join(" ", ignore)).Append('\n');
                int i = 0;
                foreach (var set in sets)
                {
                    var mp = $"{dir}/{Path.GetFileNameWithoutExtension(path)}_{i++:D4}.mat";
                    AssetDatabase.CreateAsset(new Material(shader) { shaderKeywords = set.ToArray() }, mp);
                    assets.Add(mp);
                    outTxt.Append(shader.name).Append('\t').Append(string.Join(" ", set)).Append('\n');
                }
                Stripper.Features.UnionWith(declared);
                foreach (var g in groups.Where(g => g.Mandatory)) Stripper.Mandatory.UnionWith(g.Keys);
                total += sets.Count;
                Debug.Log($"[ShaderPack] skip {shader.name}: 기능 키워드 {declared.Count}개, 조합 {sets.Count}개");
            }
            Stripper.BaseOnly = true;
            AssetDatabase.SaveAssets();
            Debug.Log($"[ShaderPack] skip 변형 목록으로 조합 {total}개");
            return outTxt.ToString();
        }

        static string Arg(string key)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
            return null;
        }
    }

    /// <summary>
    /// 렌더러가 쓰지 않는 조명 변형을 빼서 컴파일 시간과 팩 크기를 줄인다 (라이트맵·포그·인스턴싱·쿠키·정점 조명 등).
    /// 렌더러: 방향광 + 그림자, 포그 끔, 라이트맵 없음. 빠진 변형이 필요해지면 Unity가 가장 가까운 변형으로 대신 그린다.
    /// 변형 목록 모드(BaseOnly)에서는 ForwardBase가 아닌 패스의 기능 키워드 조합도 뺀다 (키워드 없는 변형만 남김).
    /// </summary>
    sealed class Stripper : IPreprocessShaders
    {
        public static bool Active, BaseOnly;
        public static readonly HashSet<string> Features = new HashSet<string>();
        public static readonly HashSet<string> Mandatory = new HashSet<string>();   // 끄기 없는 묶음 — 어느 패스든 늘 하나는 켜져 있음
        public static int Kept, Removed;
        static readonly HashSet<string> Unused = new HashSet<string>
        {
            "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "DYNAMICLIGHTMAP_ON", "LIGHTMAP_SHADOW_MIXING", "SHADOWS_SHADOWMASK",
            "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "INSTANCING_ON", "LOD_FADE_CROSSFADE", "VERTEXLIGHT_ON",
            "POINT_COOKIE", "DIRECTIONAL_COOKIE", "_LIGHT_COOKIES", "_CASTING_PUNCTUAL_LIGHT_SHADOW",
            "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION",
        };
        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!Active) return;
            // 라이트맵이 없는 오브젝트의 ForwardBase는 늘 LIGHTPROBE_SH 변형을 쓴다 (렌더러는 라이트 프로브 기본값) → 없는 쪽은 뺀다
            bool hasSH = false;
            if (snippet.passType == PassType.ForwardBase)
                foreach (var d in data) if (d.shaderKeywordSet.GetShaderKeywords().Any(k => k.name == "LIGHTPROBE_SH")) { hasSH = true; break; }
            for (int i = data.Count - 1; i >= 0; i--)
            {
                bool drop = hasSH && !data[i].shaderKeywordSet.GetShaderKeywords().Any(k => k.name == "LIGHTPROBE_SH");
                foreach (var k in data[i].shaderKeywordSet.GetShaderKeywords())
                {
                    var n = k.name;
                    if (Unused.Contains(n) || (BaseOnly && snippet.passType != PassType.ForwardBase && Features.Contains(n) && !Mandatory.Contains(n))) { drop = true; break; }
                }
                if (drop) { data.RemoveAt(i); Removed++; } else Kept++;
            }
        }
    }
}
