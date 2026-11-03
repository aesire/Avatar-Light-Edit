// 렌더 서버 플레이어 빌드 (batchmode):
//   Unity.exe -batchmode -quit -projectPath editor/Unity -executeMethod VrcProto.Editor.BuildPlayer.Build -logFile build.log
// 하는 일
//  1. 플레이어 설정: Linear, 창 모드, 백그라운드 실행, LILTOON_DISABLE_OPTIMIZATION (lilToon 전 기능 유지)
//  2. GUID→셰이더 이름 표를 Resources/VrcProtoShaderTable.txt 로 생성 (PLAN 3.1)
//  3. lilToon 셰이더 + Unity 기본 셰이더(BuiltinShaders) + 미지원 표시 셰이더를 Always Included Shaders에 등록
//     기본 셰이더는 GUID 하나를 같이 쓰므로 표에 "guid:fileID" 로 적는다 (importsim도 같은 키로 IR에 쓴다)
//  4. 빈 장면 하나로 Build/VrcProtoRenderer.exe 빌드
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VrcProto.Editor
{
    public static class BuildPlayer
    {
        const string ScenePath = "Assets/Main.unity";

        /// 아바타에 흔히 쓰이는 Unity 기본(Built-in) 셰이더. 빌드에 넣고 표에 "guid:fileID" 로 적는다
        static readonly string[] BuiltinShaders =
        {
            "Standard", "Standard (Specular setup)", "Autodesk Interactive",
            "Unlit/Texture", "Unlit/Color", "Unlit/Transparent", "Unlit/Transparent Cutout",
            "Legacy Shaders/Diffuse", "Legacy Shaders/Bumped Diffuse", "Legacy Shaders/Specular", "Legacy Shaders/Bumped Specular",
            "Legacy Shaders/Transparent/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse", "Legacy Shaders/Transparent/Bumped Diffuse",
            "Legacy Shaders/Self-Illumin/Diffuse", "Legacy Shaders/VertexLit",
            "Mobile/Diffuse", "Mobile/Bumped Diffuse", "Mobile/Unlit (Supports Lightmap)", "Mobile/VertexLit",
            "Particles/Standard Unlit", "Particles/Standard Surface",
            "Legacy Shaders/Particles/Additive", "Legacy Shaders/Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended Premultiply",
            "Sprites/Default", "UI/Default",
        };

        [MenuItem("VrcProto/Build Renderer")]
        public static void Build()
        {
            ConfigurePlayer();
            var shaders = WriteShaderTable();
            IncludeShaders(shaders);
            EnsureScene();
            AssetDatabase.SaveAssets();

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.GetFullPath(Environment.GetEnvironmentVariable("VRCPROTO_RENDERER_OUT") ?? "Build/VrcProtoRenderer.exe"),   // 앱이 Build를 쓰는 중이면 다른 곳에 (시험용)
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[VrcProto] build {report.summary.result}: {report.summary.totalSize / 1048576} MB, {report.summary.totalTime}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear; // VRChat과 같음
            PlayerSettings.productName = "VrcProtoRenderer";
            PlayerSettings.companyName = "VrcProto";
            PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.usePlayerLog = true;
            // 스플래시 끄기 (2026-10-23): 렌더러 시작이 2초 줄어든다. Pro(학생 플랜 포함) 라이선스로 빌드할 때만 적용되고, Personal로 빌드하면 Unity가 다시 켠다
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.useFlipModelSwapchain = false;   // 방송 오버레이의 투명 창(DWM 알파 합성)은 flip 모델에서 알파가 무시된다 (Renderer/Overlay.cs)
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard_2_0);
            var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';').Where(s => s.Length > 0).ToList();
            if (!defines.Contains("LILTOON_DISABLE_OPTIMIZATION")) defines.Add("LILTOON_DISABLE_OPTIMIZATION");
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, string.Join(";", defines));
            QualitySettings.antiAliasing = 4;
            QualitySettings.shadowDistance = 20;
        }

        /// <summary>프로젝트 안 모든 셰이더의 GUID→이름. 패키지에 들어 있는 lilToon GUID도 같은 셰이더로 찾게 된다.</summary>
        static List<Shader> WriteShaderTable()
        {
            var sb = new StringBuilder();
            var included = new List<Shader>();
            foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (s == null || ShaderUtil.ShaderHasError(s)) continue;
                bool lil = path.Contains("jp.lilxyzw.liltoon/Shader/");
                bool ours = path.StartsWith("Assets/Renderer/Shaders/");
                if (!lil && !ours) continue;
                var file = Path.GetFileNameWithoutExtension(path);
                // lilToonMulti는 shader_feature_local 조합이 커서 ShaderVariantCollection 단계(이후)에서 다룬다
                if (file == "ltspass_baker") { included.Add(s); continue; }   // 색 굽기 (ColorBaker) — 표에는 넣지 않고 빌드에만 포함
                if (file.StartsWith("ltsmulti") || file.StartsWith("ltspass_")) continue;
                sb.Append(guid).Append('\t').Append(s.name).Append('\n');
                included.Add(s);
            }
            foreach (var name in BuiltinShaders)
            {
                var s = Shader.Find(name);
                if (s == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string g, out long id)) { Debug.LogWarning("[VrcProto] built-in shader not found: " + name); continue; }
                sb.Append(g).Append(':').Append(id).Append('\t').Append(s.name).Append('\n');
                included.Add(s);
            }
            Directory.CreateDirectory("Assets/Resources");
            File.WriteAllText("Assets/Resources/VrcProtoShaderTable.txt", sb.ToString());
            AssetDatabase.ImportAsset("Assets/Resources/VrcProtoShaderTable.txt");
            Debug.Log($"[VrcProto] shader table: {included.Count} shaders");
            return included;
        }

        static void IncludeShaders(List<Shader> shaders)
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            var set = new List<Shader>();
            for (int i = 0; i < arr.arraySize; i++) { var s = arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader; if (s != null) set.Add(s); }
            foreach (var s in shaders) if (!set.Contains(s)) set.Add(s);
            arr.arraySize = set.Count;
            for (int i = 0; i < set.Count; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = set[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void EnsureScene()
        {
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
