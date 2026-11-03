// 셰이더 팩 만들기 (UI 없는 부분). 렌더러는 셰이더 소스를 컴파일하지 못하므로, PC에 설치된 같은 버전의 Unity를 batchmode로 불러
// AssetBundle을 만든다. 렌더러는 시작할 때 팩 폴더(%LOCALAPPDATA%\ALEdit\shaderpacks)의 번들을 불러온다 (MaterialFactory).
//  1. 원본(.unitypackage 또는 폴더)에서 셰이더·인클루드 파일과 .meta만 고른다. 스크립트·DLL은 복사하지 않는다 (신뢰하지 않는 패키지).
//  2. 전용 프로젝트 Assets/Pack 아래에 원래 경로 그대로 둔다 (.meta 덕분에 GUID가 그대로라 IR의 shaderGuid와 맞는다).
//  3. 팩 셰이더를 쓰는 머티리얼(원본에 든 것 + 지금 열린 아바타의 것)을 함께 둔다 → shader_feature 변형이 팩에 들어간다.
//  4. Unity -batchmode … -executeMethod VrcProto.ShaderPack.Builder.Build (Tools/ShaderPack/ShaderPackBuilder.cs)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImportSim;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public static class ShaderPacks
    {
        static readonly HashSet<string> ShaderExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".shader", ".cginc", ".hlsl", ".hlslinc", ".glslinc", ".compute" };
        static readonly Regex ShaderName = new Regex(@"^[\s﻿]*Shader\s+""([^""]+)""", RegexOptions.Multiline); // 파일 맨 앞 BOM 허용
        static readonly Regex ShaderRef = new Regex(@"m_Shader: \{fileID: \d+, guid: ([0-9a-f]{32})");

        static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// 렌더러(MaterialFactory.ShaderPackDir)와 같은 위치
        public static string PackDir
        {
            get { var e = Environment.GetEnvironmentVariable("VRCPROTO_SHADERPACKS"); return !string.IsNullOrEmpty(e) ? e : Path.Combine(Local, "ALEdit", "shaderpacks"); }
        }
        public static string ProjectDir => Path.Combine(Local, "ALEdit", "shaderpack-project");

        public sealed class StagedFile { public string Path = ""; public byte[] Data = Array.Empty<byte>(); public string Meta = ""; }

        public sealed class Source
        {
            public string Origin = "";
            public List<StagedFile> Files = new List<StagedFile>();          // 셰이더·인클루드
            public List<StagedFile> Materials = new List<StagedFile>();      // 팩 셰이더를 쓰는 원본 머티리얼
            public List<(string Guid, string Name, string Path)> Shaders = new List<(string, string, string)>();
        }

        /// <summary>.unitypackage 또는 폴더에서 셰이더 파일을 고른다 (읽기만 함).</summary>
        /// onlyShaders / onlyNames: 주면 그 GUID·이름의 .shader만 가져간다 (인클루드 파일은 전부). 셰이더 모음 전체를 컴파일하지 않게.
        public static Source Scan(string origin, ISet<string>? onlyShaders = null, ISet<string>? onlyNames = null)
        {
            var db = new AssetDb();
            if (Directory.Exists(origin)) db.AddProjectFolder(origin);
            else if (File.Exists(origin) && origin.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) db.AddPackage(origin);
            else if (File.Exists(origin) && origin.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(origin) is string d) db.AddProjectFolder(d);
            else throw new ArgumentException("셰이더가 든 .unitypackage 나 폴더를 선택해 주세요: " + origin);

            var src = new Source { Origin = origin };
            var wanted = db.ByGuid.Values.Where(a => ShaderExt.Contains(a.Ext) || a.Ext == ".mat").ToList();
            var mats = new List<(AssetEntry a, byte[] data)>();
            db.ReadEach(wanted, (a, data) =>
            {
                if (a.Ext == ".mat") { mats.Add((a, data)); return; }
                if (a.Ext == ".shader" && onlyShaders != null && !onlyShaders.Contains(a.Guid)) return;
                var sm = a.Ext == ".shader" ? ShaderName.Match(Encoding.UTF8.GetString(data)) : null;
                if (sm != null && onlyNames != null && !(sm.Success && onlyNames.Contains(sm.Groups[1].Value))) return;
                src.Files.Add(new StagedFile { Path = Rel(a.Path), Data = data, Meta = a.Meta });
                if (sm != null && sm.Success) src.Shaders.Add((a.Guid, sm.Groups[1].Value, a.Path));
            });
            var guids = new HashSet<string>(src.Shaders.Select(s => s.Guid));
            foreach (var (a, data) in mats)
                if (ShaderRef.Match(Encoding.UTF8.GetString(data)) is Match m && m.Success && guids.Contains(m.Groups[1].Value))
                    src.Materials.Add(new StagedFile { Path = Rel(a.Path), Data = data });
            src.Shaders.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));
            return src;
        }

        /// <summary>
        /// 프로젝트에서 찾은 셰이더 원본들(UnityImport.FindShaderSources)을 팩 하나의 재료로 모은다.
        /// 원본 폴더(범위)마다 필요한 셰이더와 그 폴더의 인클루드 파일을 가져온다.
        /// </summary>
        public static Source ScanProject(Dictionary<string, (string Shader, string Root)> found)
        {
            var all = new Source { Origin = string.Join("; ", found.Values.Select(v => v.Root).Distinct()) };
            foreach (var g in found.GroupBy(kv => kv.Value.Root, StringComparer.OrdinalIgnoreCase))
            {
                var part = Scan(g.Key, new HashSet<string>(g.Select(kv => kv.Key)));
                all.Files.AddRange(part.Files); all.Materials.AddRange(part.Materials); all.Shaders.AddRange(part.Shaders);
            }
            all.Shaders.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));
            return all;
        }

        /// 원래 경로를 Assets/Pack 아래 상대 경로로. ".." 같은 경로 조작은 지운다.
        static string Rel(string path)
        {
            var parts = path.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != "." && p != "..").ToList();
            if (parts.Count > 1 && (parts[0] == "Assets" || parts[0] == "Packages")) parts.RemoveAt(0);
            return string.Join("/", parts.Select(p => string.Concat(p.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))));
        }

        /// <summary>렌더러와 같은 버전의 Unity.exe (환경 변수 UNITY_EXE → Unity Hub 기본 위치).</summary>
        public static string? FindUnity(string version)
        {
            var env = Environment.GetEnvironmentVariable("UNITY_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
            {
                var p = Path.Combine(root, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public sealed class Result { public bool Ok; public List<string> Compiled = new List<string>(); public List<string> Errors = new List<string>(); public string Log = ""; }

        /// <summary>
        /// 팩을 만든다. extraMaterials: 지금 열린 아바타의 머티리얼 (이름, .mat 원문) — 변형 수집용.
        /// outDir: 기본은 사용자 팩 폴더. variantSpec: 미리 컴파일할 키워드 조합 목록 (배포용 기본 팩, ShaderPackBuilder.cs 참고).
        /// </summary>
        public static async Task<Result> Build(Source src, string name, IEnumerable<(string Name, string Yaml)> extraMaterials, string unityExe, Action<string> progress,
                                               string? outDir = null, string? variantSpec = null)
        {
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
            var proj = ProjectDir;
            var staging = Path.Combine(proj, "Assets", "Pack");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(Path.Combine(proj, "Assets", "Editor"));
            var builder = Path.Combine(AppContext.BaseDirectory, "ShaderPack", "ShaderPackBuilder.cs");
            File.Copy(builder, Path.Combine(proj, "Assets", "Editor", "VrcProtoShaderPackBuilder.cs"), true);

            foreach (var f in src.Files.Concat(src.Materials))
            {
                var dst = Path.Combine(staging, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, f.Data);
                if (f.Meta.Length > 0) File.WriteAllText(dst + ".meta", f.Meta);
            }
            int i = 0;
            foreach (var (mname, yaml) in extraMaterials)
            {
                var dir = Path.Combine(staging, "_avatar_materials");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{i++:D3}_{string.Concat(mname.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}.mat"), yaml);
            }

            var log = Path.Combine(proj, "shaderpack.log");
            var psi = new ProcessStartInfo(unityExe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-batchmode", "-nographics", "-quit", "-projectPath", proj, "-executeMethod", "VrcProto.ShaderPack.Builder.Build",
                                      "-packName", name, "-packOut", Path.GetFullPath(outDir ?? PackDir), "-logFile", log })
                psi.ArgumentList.Add(a);
            if (variantSpec != null) { psi.ArgumentList.Add("-variantSpec"); psi.ArgumentList.Add(Path.GetFullPath(variantSpec)); }
            progress("Unity로 셰이더를 컴파일하는 중… 처음에는 프로젝트를 만드느라 몇 분 걸려요.");
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync();

            var r = new Result();
            r.Log = await ReadShared(log);
            foreach (var line in r.Log.Split('\n'))
            {
                int k = line.IndexOf("[ShaderPack] ", StringComparison.Ordinal);
                if (k < 0) continue;
                var msg = line.Substring(k + 13).Trim();
                if (msg.StartsWith("ok ")) r.Compiled.Add(msg.Substring(3));
                else if (msg.StartsWith("error ")) r.Errors.Add(msg.Substring(6));
                else if (msg.StartsWith("done ")) r.Ok = true;
            }
            if (!r.Ok && r.Errors.Count == 0)
                r.Errors.Add(r.Log.Contains("error CS") ? "빌더 스크립트 컴파일 오류 (" + log + ")" : "Unity가 팩을 만들지 못했습니다 (" + log + ")");
            return r;
        }

        /// Unity가 끝난 직후에도 자식 프로세스가 로그를 잡고 있을 수 있어 공유 모드로, 잠깐 기다려 가며 읽는다
        static async Task<string> ReadShared(string path)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (!File.Exists(path)) return "";
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    return await sr.ReadToEndAsync();
                }
                catch (IOException) { await Task.Delay(500); }
            }
            return "";
        }

        /// <summary>설치된 팩 이름 목록.</summary>
        public static List<string> Installed() =>
            Directory.Exists(PackDir) ? Directory.GetFiles(PackDir, "*.bundle").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x).ToList() : new List<string>();

        /// <summary>지금 열린 아바타의 머티리얼 중 팩 셰이더를 쓰는 것의 .mat 원문 (그 키워드 조합의 변형을 팩에 넣기 위해).</summary>
        public static List<(string Name, string Yaml)> AvatarMaterials(IrScene? scene, Source src)
        {
            var guids = new HashSet<string>(src.Shaders.Select(s => s.Guid));
            return scene == null ? new List<(string, string)>()
                : scene.Materials.Where(m => guids.Contains(m.ShaderGuid) && !string.IsNullOrEmpty(m.RawYaml)).Select(m => (m.Name, m.RawYaml!)).ToList();
        }
    }
}
