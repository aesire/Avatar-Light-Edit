// VRChat 업로드 (PLAN.md 3.10): 업로드용 Unity 프로젝트 만들기 → VPM 패키지(SDK·기믹)를 공식 저장소에서 받아 설치 → 원본 가져오기 →
// Document 변경 적용(Unity batchmode, Tools/Upload/VrcProtoUpload.cs) → Unity 창을 열어 사용자가 SDK 창에서 로그인하고 올린다.
// 툴은 SDK·기믹을 포함·미러하지 않는다: 사용자 PC에서 각 공식 저장소로 받고(zip 해시 확인), 받은 파일을 고치지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public static partial class Upload
    {
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit");
        public static string CacheDir => Path.Combine(Root, "vpm-cache");
        /// <summary>아바타를 가리키는 열쇠 (원본 guid, 없으면 이름): 업로드 프로젝트·고른 썸네일에 쓴다.</summary>
        public static string AvatarKey(Document doc) { var guid = doc.Original.Raw["source"]["guid"].AsString; return guid.Length > 0 ? guid : doc.Original.Name; }

        /// <summary>업로드 프로젝트 (Unity 경로 길이 제한 때문에 짧게: up\아바타 guid 앞 8자).</summary>
        public static string ProjectFor(string avatarGuid) => Path.Combine(Root, "up", (avatarGuid.Length >= 8 ? avatarGuid.Substring(0, 8) : "avatar").ToLowerInvariant());
        public const string UnityVersion = "2022.3.22f1";

        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            // packages.vrchat.com은 이름·버전이 든 User-Agent를 요구한다 (기본 목록은 GitHub Pages 주소를 쓴다)
            h.DefaultRequestHeaders.UserAgent.ParseAdd("ALEdit/0.1 (Avatar Light Edit, VRChat avatar editor; Windows)");
            return h;
        }

        /// <summary>Unity 2022.3 (VRChat 지정 버전 우선, 없으면 설치된 다른 2022.3).</summary>
        public static string? FindUnity()
        {
            var exact = ShaderPacks.FindUnity(UnityVersion);
            if (exact != null) return exact;
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
            {
                var hub = Path.Combine(root, "Unity", "Hub", "Editor");
                if (!Directory.Exists(hub)) continue;
                var any = Directory.GetDirectories(hub, "2022.3.*").OrderByDescending(d => d).Select(d => Path.Combine(d, "Editor", "Unity.exe")).FirstOrDefault(File.Exists);
                if (any != null) return any;
            }
            return null;
        }

        // ---------------- VPM ----------------
        static async Task<VpmRepo> FetchRepo(string url)
        {
            var json = await Http.GetStringAsync(url);
            return VpmRepo.Parse(json, url);
        }

        /// <summary>SDK + 고른 기믹과 의존성을 골라 Packages/에 설치한다. 이미 같은 버전이 있으면 건너뛴다.</summary>
        public static async Task<List<VpmVersion>> InstallPackages(string project, IEnumerable<string> gimmickKeys, Action<string> log)
        {
            var gimmicks = gimmickKeys.Select(UploadPlan.Find).Where(g => g != null).Select(g => g!).ToList();
            var urls = new List<string> { UploadPlan.VrchatRepo, UploadPlan.CuratedRepo };
            urls.AddRange(gimmicks.Select(g => g.RepoUrl));
            // MA·AAO가 쓰는 NDMF는 nadena 저장소에 있다
            if (gimmicks.Any(g => g.Key == "ma" || g.Key == "aao") && !urls.Contains("https://vpm.nadena.dev/vpm.json")) urls.Add("https://vpm.nadena.dev/vpm.json");
            var repos = new List<VpmRepo>();
            foreach (var u in urls.Distinct())
            {
                log("저장소 목록 받기: " + u);
                try { repos.Add(await FetchRepo(u)); }
                catch (Exception e) { throw new Exception($"저장소를 읽지 못했어요 ({u}): {e.Message}"); }
            }
            var wanted = new Dictionary<string, string> { [UploadPlan.SdkId] = "" };
            foreach (var g in gimmicks) wanted[g.PackageId] = "";
            var (picked, errors) = VpmResolver.Resolve(wanted, repos, "2022.3");
            if (errors.Count > 0) throw new Exception("패키지 버전을 선택하지 못했습니다: " + string.Join("; ", errors));

            Directory.CreateDirectory(CacheDir);
            var pkgDir = Path.Combine(project, "Packages");
            foreach (var p in picked)
            {
                var dst = Path.Combine(pkgDir, p.Id);
                var pj = Path.Combine(dst, "package.json");
                if (File.Exists(pj) && JNode.Parse(File.ReadAllText(pj))["version"].AsString == p.Version) { log($"있음: {p.Id} {p.Version}"); continue; }
                var zip = Path.Combine(CacheDir, $"{p.Id}-{p.Version}.zip");
                if (!File.Exists(zip) || !HashOk(zip, p.Sha256))
                {
                    log($"받기: {p.Id} {p.Version}");
                    var tmp = zip + ".part";
                    using (var resp = await Http.GetAsync(p.Url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        using var fs = File.Create(tmp);
                        await resp.Content.CopyToAsync(fs);
                    }
                    if (!HashOk(tmp, p.Sha256)) { File.Delete(tmp); throw new Exception($"{p.Id} {p.Version}: 받은 파일의 해시가 저장소 목록과 달라요"); }
                    File.Move(tmp, zip, true);
                }
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                ZipFile.ExtractToDirectory(zip, dst);
                log($"설치: {p.Id} {p.Version}" + (p.License.Length > 0 ? $" ({p.License})" : ""));
            }
            File.WriteAllText(Path.Combine(pkgDir, "vpm-manifest.json"), VpmResolver.Manifest(picked, wanted.Keys).ToJson(true));
            return picked;
        }

        static bool HashOk(string file, string sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return true;   // 목록에 해시가 없는 저장소 (VRCFury 등)
            using var s = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- 프로젝트 ----------------
        static readonly string[] Modules =
        {
            "ai", "androidjni", "animation", "assetbundle", "audio", "cloth", "director", "imageconversion", "imgui", "jsonserialize", "particlesystem",
            "physics", "physics2d", "screencapture", "terrain", "terrainphysics", "tilemap", "ui", "uielements", "umbra", "unityanalytics",
            "unitywebrequest", "unitywebrequestassetbundle", "unitywebrequestaudio", "unitywebrequesttexture", "unitywebrequestwww",
            "vehicles", "video", "vr", "wind", "xr",
        };

        /// <summary>빈 프로젝트 (처음 한 번). 에디터 스크립트·Protocol.dll은 매번 새로 복사한다.</summary>
        public static void PrepareProject(string project)
        {
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            Directory.CreateDirectory(Path.Combine(project, "VrcProto"));
            var manifest = Path.Combine(project, "Packages", "manifest.json");
            if (!File.Exists(manifest))
            {
                var deps = JNode.Obj();
                foreach (var m in Modules) deps.Set("com.unity.modules." + m, "1.0.0");
                deps.Set("com.unity.ugui", "1.0.0");
                File.WriteAllText(manifest, JNode.Obj().Set("dependencies", deps).ToJson(true));
            }
            var editor = Path.Combine(project, "Assets", "VrcProto", "Editor");
            Directory.CreateDirectory(editor);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Upload", "VrcProtoUpload.cs"), Path.Combine(editor, "VrcProtoUpload.cs"), true);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Protocol.dll"), Path.Combine(editor, "VrcProto.Protocol.dll"), true);
        }

        /// <summary>지난번 적용 결과(사본 머티리얼·메시·장면)를 지운다. 같은 이름 사본이 쌓이지 않게.</summary>
        public static void ClearApplied(string project)
        {
            var dir = Path.Combine(project, "Assets", "VrcProto");
            foreach (var d in new[] { "Materials", "Meshes", "Textures" })
            {
                var p = Path.Combine(dir, d);
                if (Directory.Exists(p)) Directory.Delete(p, true);
                if (File.Exists(p + ".meta")) File.Delete(p + ".meta");
            }
            foreach (var f in new[] { "Upload.unity", "Upload.unity.meta" }) { var p = Path.Combine(dir, f); if (File.Exists(p)) File.Delete(p); }
        }

        public static bool IsOpenInUnity(string project)
        {
            var lockFile = Path.Combine(project, "Temp", "UnityLockfile");
            if (!File.Exists(lockFile)) return false;
            try { using (File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
        }

        /// <summary>Unity를 batchmode로 한 번 돌린다. 반환: 끝 코드, 로그 파일.</summary>
        public static async Task<(int code, string log)> RunBatch(string unity, string project, string method, string logName)
        {
            var log = Path.Combine(project, "VrcProto", logName);
            var psi = new ProcessStartInfo(unity) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-batchmode", "-quit", "-projectPath", project, "-executeMethod", method, "-logFile", log }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return (p.ExitCode, log);
        }

        /// <summary>창 모드로 열기 (사용자가 SDK 창에서 로그인·업로드).</summary>
        public static void OpenUnity(string unity, string project)
        {
            var psi = new ProcessStartInfo(unity) { UseShellExecute = false };
            foreach (var a in new[] { "-projectPath", project, "-executeMethod", "VrcProto.Upload.Setup.OpenForUpload" }) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }

        /// <summary>
        /// 1~5단계 (Unity 찾기 → 프로젝트 → 패키지 → 원본 가져오기 → 변경 적용). step(번호, 상태 0대기·1진행·2끝·3실패, 메모).
        /// 실패하면 그 단계에 3을 알린 뒤 예외. 반환: Unity.exe 경로. thumbnail: 썸네일을 찍어 경로를 돌려주는 함수(없으면 null).
        /// </summary>
        public static async Task<string> Prepare(Document doc, string project, string name, List<string> gimmicks, Func<Task<string?>>? thumbnail, Action<string> log, Action<int, int, string?> step,
                                                 bool copyPackages = false, bool excludeBlinkFix = false, bool excludeMouthFix = false)
        {
            int cur = 0;
            try
            {
                step(cur, 1, null);
                var unity = FindUnity() ?? throw new Exception($"Unity 2022.3을 찾지 못했어요. Unity Hub에서 {UnityVersion}을 설치하거나 환경 변수 UNITY_EXE에 경로를 지정하세요.");
                step(cur, 2, unity); cur++;

                step(cur, 1, null);
                if (IsOpenInUnity(project)) throw new Exception("이 업로드 프로젝트가 이미 Unity에서 열려 있어요. Unity를 닫고 다시 해 주세요.");
                PrepareProject(project);
                if (SavedAvatarId(project) is string id)
                    log($"지난번에 올린 아바타 {id}를 덮어써요. 새 아바타로 올리려면 Unity의 \"ALEdit 업로드\" 창에서 \"새 아바타로\"를 눌러 주세요.");
                var thumb = thumbnail != null ? await thumbnail() : null;
                var plan = UploadPlan.Build(doc, name, thumb, excludeBlinkFix, null, excludeMouthFix);
                File.WriteAllText(Path.Combine(project, "VrcProto", "apply.json"), plan.ToJson(true));
                step(cur, 2, $"머티리얼 {plan["materials"].Count} · 메시 {plan["meshes"].Count} · 아이템 {plan["outfits"].Count}"); cur++;

                step(cur, 1, null);
                var picked = await Task.Run(() => InstallPackages(project, gimmicks, log));
                // 원본 프로젝트의 Packages (VCC로 설치한 기믹 등): 고른 VPM 패키지가 우선
                var orig = OriginalProject(doc.Original);
                var ids = picked.Select(x => x.Id).ToList();
                if (copyPackages && orig != null) await Task.Run(() => CopyProjectPackages(project, orig, ids, log));
                else RemoveCopiedPackages(project, ids, log);
                step(cur, 2, string.Join(", ", picked.Where(x => x.Id == UploadPlan.SdkId || gimmicks.Any(k => UploadPlan.Find(k)?.PackageId == x.Id)).Select(x => $"{x.DisplayName} {x.Version}"))); cur++;

                step(cur, 1, null);
                if (NeedsImport(project, plan["sources"]))
                {
                    var count = await Task.Run(() => ImportSources(project, plan["sources"], log));
                    if (count == 0) throw new Exception("원본 패키지에서 가져올 것이 없었어요");
                    MarkImported(project, plan["sources"]);
                    step(cur, 2, $"파일 {count}개");
                }
                else step(cur, 2, "지난번에 가져온 것 그대로");
                cur++;

                step(cur, 1, null);
                log("Unity로 장면을 만들고 변경을 적용하는 중… 처음에는 SDK·원본을 가져오고 컴파일하느라 몇 분 걸려요.");
                ClearApplied(project);
                var (_, alog) = await RunBatch(unity, project, "VrcProto.Upload.Setup.Assemble", "assemble.log");
                var st2 = ReadStatus(project);
                foreach (var l in st2["log"].Items) log("  " + l.AsString);
                if (st2["stage"].AsString != "assembled" || st2["scene"].IsNull) throw new Exception(Fail("변경을 적용하지 못했어요", st2, alog));
                step(cur, st2["ok"].AsBool ? 2 : 3, st2["ok"].AsBool ? null : "일부를 찾지 못했어요 (로그 확인)");
                return unity;
            }
            catch { step(cur, 3, null); throw; }
        }

        static string Fail(string what, JNode st, string log)
        {
            var errs = CompileErrors(log);
            if (errs.Count > 0) return what + ": 스크립트 컴파일 오류\n" + string.Join("\n", errs) + "\n로그: " + log;
            return what + (st["message"].AsString.Length > 0 ? ": " + st["message"].AsString : "") + "\n로그: " + log;
        }

        public static JNode ReadStatus(string project)
        {
            var p = Path.Combine(project, "VrcProto", "status.json");
            try { return File.Exists(p) ? JNode.Parse(File.ReadAllText(p)) : JNode.Null; } catch (IOException) { return JNode.Null; }
        }

        /// <summary>이 업로드 프로젝트로 지난번에 올린 아바타 ID (Unity 쪽이 VrcProto/uploads.json에 남김). 없으면 null.</summary>
        public static string? SavedAvatarId(string project)
        {
            var p = Path.Combine(project, "VrcProto", "uploads.json");
            try { return File.Exists(p) ? JNode.Parse(File.ReadAllText(p))["avatarId"].AsStringOrNull : null; } catch (Exception) { return null; }
        }

        /// <summary>로그 파일에서 스크립트 컴파일 오류 줄 (있으면 적용 스크립트가 돌지 않았다).</summary>
        public static List<string> CompileErrors(string log)
        {
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd().Split('\n').Where(l => l.Contains("error CS")).Select(l => l.Trim()).Distinct().Take(10).ToList();
            }
            catch (IOException) { return new List<string>(); }
        }

        /// <summary>
        /// 원본 가져오기: .unitypackage는 Unity가 하는 것처럼 guid/pathname · guid/asset · guid/asset.meta를 그 경로에 풀고(batchmode의 ImportPackage는
        /// 비동기라 Unity가 끝나기 전에 가져오지 않는다), 폴더는 .meta째 복사한다. 경로는 Assets/ 아래만 받고 ".."은 거절한다. 반환: 푼 파일 수.
        /// </summary>
        public static int ImportSources(string project, JNode sources, Action<string> log)
        {
            int n = 0;
            var assets = Path.GetFullPath(Path.Combine(project, "Assets"));
            foreach (var s in sources.Items)
            {
                var src = s.AsString;
                if (src.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) && File.Exists(src))
                {
                    var stage = Path.Combine(project, "VrcProto", "staging");
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    ImportSim.TarGz.Read(src, (name, read) =>
                    {
                        var parts = name.TrimStart('.', '/').Split('/');
                        if (parts.Length != 2 || (parts[1] != "pathname" && parts[1] != "asset" && parts[1] != "asset.meta")) return;
                        var dir = Path.Combine(stage, parts[0]);
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, parts[1]), read());
                    });
                    int k = 0, skipped = 0;
                    foreach (var dir in Directory.Exists(stage) ? Directory.GetDirectories(stage) : Array.Empty<string>())
                    {
                        var pn = Path.Combine(dir, "pathname");
                        if (!File.Exists(pn)) continue;
                        var rel = File.ReadAllText(pn).Split('\n')[0].Trim().Replace('\\', '/');
                        if (!rel.StartsWith("Assets/") || rel.Split('/').Any(x => x == "..")) { skipped++; continue; }
                        var dst = Path.GetFullPath(Path.Combine(project, rel));
                        if (!dst.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                        var asset = Path.Combine(dir, "asset");
                        if (File.Exists(asset)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(asset, dst, true); }
                        else Directory.CreateDirectory(dst);   // 폴더 항목
                        var meta = Path.Combine(dir, "asset.meta");
                        if (File.Exists(meta)) File.Copy(meta, dst + ".meta", true);
                        k++;
                    }
                    Directory.Delete(stage, true);
                    log($"풀기: {Path.GetFileName(src)} ({k}개" + (skipped > 0 ? $", Assets 밖 경로 {skipped}개 건너뜀" : "") + ")");
                    n += k;
                }
                else if (Directory.Exists(src))
                {
                    var from = Path.GetFullPath(src);
                    var to = Path.GetFileName(from).Equals("Assets", StringComparison.OrdinalIgnoreCase) ? assets : Path.Combine(assets, Path.GetFileName(from));
                    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var dst = Path.Combine(to, Path.GetRelativePath(from, f));
                        if (File.Exists(dst)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(f, dst);
                        n++;
                    }
                    log($"복사: {src}");
                }
                else log("원본을 찾지 못함: " + src);
            }
            return n;
        }

        /// <summary>원본을 다시 가져와야 하는지 (처음이거나 원본 목록이 바뀜).</summary>
        public static bool NeedsImport(string project, JNode sources)
        {
            var p = Path.Combine(project, "VrcProto", "imported.json");
            return !File.Exists(p) || File.ReadAllText(p) != sources.ToJson();
        }
        public static void MarkImported(string project, JNode sources) => File.WriteAllText(Path.Combine(project, "VrcProto", "imported.json"), sources.ToJson());
    }
}
