// 기존 Unity 프로젝트로 내보내기 + 올리기 프로젝트에 원본 프로젝트의 Packages 복사.
//   내보내기: 사용자의 프로젝트에 에디터 스크립트(Assets/VrcProto/Editor)와 apply.json(<프로젝트>/VrcProto)을 넣고, 올리기와 같은 조립 스크립트로
//            Assets/VrcProto/Export/<이름>_<시각>/에 새 장면과 바뀐 머티리얼·메시 사본만 만든다. 원본 장면·프리팹·머티리얼·임포트 설정은 고치지 않는다.
//            그 프로젝트에 없는 원본(붙인 의상 패키지 등)은 GUID도 경로도 없는 파일만 넣는다 (있는 파일은 덮어쓰지 않음).
//            Unity가 프로젝트를 닫고 있으면 batchmode로, 열고 있으면(또는 그 Unity 버전이 없으면) 요청 파일을 두고 Unity 쪽이 실행하기를 기다린다.
//   Packages 복사: VCC·패키지 관리자로 설치한 기믹이 업로드 프로젝트에 없어 생기는 컴파일·누락 오류를 막는다.
//            올리기가 고른 VPM 패키지가 우선이고, 그 밖의 원본 패키지 폴더와 manifest 의존성만 더한다. 끄면 더한 것을 뺀다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public static partial class Upload
    {
        /// <summary>IR을 가져온 원본 Unity 프로젝트 폴더 (Assets의 부모). 패키지에서 가져왔으면 null.</summary>
        public static string? OriginalProject(IrScene s)
        {
            var assets = s.Raw["source"]["project"].AsStringOrNull;
            if (string.IsNullOrEmpty(assets)) return null;
            var root = Path.GetDirectoryName(Path.GetFullPath(assets).TrimEnd('\\', '/'));
            return root != null && IsUnityProject(root) ? root : null;
        }

        public static bool IsUnityProject(string dir) =>
            Directory.Exists(Path.Combine(dir, "Assets")) && File.Exists(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt"));

        /// <summary>ProjectSettings/ProjectVersion.txt의 m_EditorVersion.</summary>
        public static string? ProjectUnityVersion(string dir)
        {
            try
            {
                foreach (var l in File.ReadAllLines(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt")))
                    if (l.StartsWith("m_EditorVersion:")) return l.Substring("m_EditorVersion:".Length).Trim();
            }
            catch (IOException) { }
            return null;
        }

        // ---------------- Packages 복사 ----------------
        const string CopiedFile = "copied-packages.json";

        /// <summary>
        /// 원본 프로젝트의 Packages/를 업로드 프로젝트에 더한다. keep: 올리기가 고른 VPM 패키지 (덮어쓰지 않음).
        /// 패키지 폴더(package.json이 있는 것)는 없으면 복사, 지난번에 복사한 것은 버전이 바뀌었으면 다시 복사. manifest.json의 의존성·scopedRegistries는 없는 것만 더한다.
        /// </summary>
        public static void CopyProjectPackages(string project, string orig, IEnumerable<string> keep, Action<string> log)
        {
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            var from = Path.Combine(orig, "Packages"); var to = Path.Combine(project, "Packages");
            var state = ReadCopied(project);
            var copied = state["folders"].Items.Select(x => x.AsString).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deps = state["deps"].Items.Select(x => x.AsString).ToHashSet(StringComparer.Ordinal);
            var regs = state["registries"].Items.Select(x => x.AsString).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            if (Directory.Exists(from))
                foreach (var dir in Directory.GetDirectories(from))
                {
                    var id = Path.GetFileName(dir);
                    var pj = Path.Combine(dir, "package.json");
                    if (!File.Exists(pj) || keepSet.Contains(id)) continue;
                    var dst = Path.Combine(to, id);
                    if (Directory.Exists(dst))
                    {
                        if (!copied.Contains(id)) continue;   // 올리기가 설치한 의존성 등
                        if (PackageVersion(pj) == PackageVersion(Path.Combine(dst, "package.json"))) continue;
                        Directory.Delete(dst, true);
                    }
                    CopyDir(dir, dst);
                    copied.Add(id); added++;
                    log($"원본 패키지 복사: {id} {PackageVersion(pj)}");
                }
            // manifest.json: 레지스트리 패키지(com.unity.timeline 등)·scoped registry
            var mf = Path.Combine(to, "manifest.json"); var omf = Path.Combine(from, "manifest.json");
            if (File.Exists(mf) && File.Exists(omf))
            {
                var mine = JNode.Parse(File.ReadAllText(mf)); var theirs = JNode.Parse(File.ReadAllText(omf));
                if (!mine.Has("dependencies")) mine.Set("dependencies", JNode.Obj());
                var md = mine["dependencies"];
                foreach (var kv in theirs["dependencies"].Props)
                {
                    if (md.Has(kv.Key) || keepSet.Contains(kv.Key) || Directory.Exists(Path.Combine(to, kv.Key))) continue;
                    var v = kv.Value.AsString;
                    // file: 경로는 원본 Packages 기준이라 절대 경로로
                    if (v.StartsWith("file:")) v = "file:" + Path.GetFullPath(Path.Combine(from, v.Substring(5))).Replace('\\', '/');
                    md.Set(kv.Key, v); deps.Add(kv.Key);
                    log($"원본 패키지 의존성: {kv.Key} {v}");
                }
                var mr = mine["scopedRegistries"].Kind == JKind.Array ? mine["scopedRegistries"] : JNode.Arr();
                foreach (var r in theirs["scopedRegistries"].Items)
                {
                    var url = r["url"].AsString;
                    if (url.Length == 0 || mr.Items.Any(x => x["url"].AsString.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
                    mr.Add(r.Clone()); regs.Add(url);
                    log("원본 scoped registry: " + url);
                }
                if (mr.Count > 0) mine.Set("scopedRegistries", mr);
                File.WriteAllText(mf, mine.ToJson(true));
            }
            WriteCopied(project, copied, deps, regs);
            log(added > 0 ? $"원본 프로젝트의 Packages에서 {added}개를 더했어요" : "원본 프로젝트의 Packages: 더할 것이 없어요 (이미 있음)");
        }

        /// <summary>지난번에 복사한 원본 패키지·의존성을 뺀다 (Packages 복사를 끈 경우). keep(VPM 패키지)은 그대로.</summary>
        public static void RemoveCopiedPackages(string project, IEnumerable<string> keep, Action<string> log)
        {
            var state = ReadCopied(project);
            if (state["folders"].Count == 0 && state["deps"].Count == 0 && state["registries"].Count == 0) return;
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            var to = Path.Combine(project, "Packages");
            foreach (var id in state["folders"].Items.Select(x => x.AsString))
            {
                var d = Path.Combine(to, id);
                if (!keepSet.Contains(id) && Directory.Exists(d)) Directory.Delete(d, true);
            }
            var mf = Path.Combine(to, "manifest.json");
            if (File.Exists(mf))
            {
                var mine = JNode.Parse(File.ReadAllText(mf));
                foreach (var k in state["deps"].Items) mine["dependencies"].Remove(k.AsString);
                if (mine["scopedRegistries"].Kind == JKind.Array)
                {
                    var urls = state["registries"].Items.Select(x => x.AsString).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var keepRegs = JNode.Arr(); foreach (var r in mine["scopedRegistries"].Items) if (!urls.Contains(r["url"].AsString)) keepRegs.Add(r);
                    if (keepRegs.Count > 0) mine.Set("scopedRegistries", keepRegs); else mine.Remove("scopedRegistries");
                }
                File.WriteAllText(mf, mine.ToJson(true));
            }
            File.Delete(Path.Combine(project, "VrcProto", CopiedFile));
            log("지난번에 복사한 원본 패키지를 뺐어요");
        }

        static JNode ReadCopied(string project)
        {
            var p = Path.Combine(project, "VrcProto", CopiedFile);
            try { if (File.Exists(p)) return JNode.Parse(File.ReadAllText(p)); } catch (Exception) { }
            return JNode.Obj();
        }

        static void WriteCopied(string project, IEnumerable<string> folders, IEnumerable<string> deps, IEnumerable<string> regs)
        {
            JNode A(IEnumerable<string> xs) { var a = JNode.Arr(); foreach (var x in xs.OrderBy(x => x, StringComparer.Ordinal)) a.Add(x); return a; }
            Directory.CreateDirectory(Path.Combine(project, "VrcProto"));
            File.WriteAllText(Path.Combine(project, "VrcProto", CopiedFile), JNode.Obj().Set("folders", A(folders)).Set("deps", A(deps)).Set("registries", A(regs)).ToJson(true));
        }

        static string PackageVersion(string packageJson)
        {
            try { return JNode.Parse(File.ReadAllText(packageJson))["version"].AsString; } catch (Exception) { return ""; }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from))
                if (Path.GetFileName(d) != ".git") CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // ---------------- 기존 프로젝트로 내보내기 ----------------
        public static readonly string[] ExportSteps = { "Unity 프로젝트 확인", "변경 내용·에디터 스크립트 넣기", "이 프로젝트에 없는 원본 넣기", "Unity에서 새 장면 만들기" };

        static string SafeName(string n)
        {
            var s = string.Concat(n.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '.' ? '_' : c));
            return s.Length > 0 ? s : "Avatar";
        }

        /// <summary>
        /// 내보내기. step(번호, 0대기·1진행·2끝·3실패, 메모). 반환: Unity 쪽 status.json (scene = 새 장면, existingBlinkFix·blinkFixSkipped·mouthFixSkipped).
        /// 실패하면 그 단계에 3을 알린 뒤 예외. ct: Unity 쪽을 기다리는 동안 창을 닫으면 그만 기다린다.
        /// upload (2026-11-03): 원본 프로젝트에서 올리기 — Unity를 창으로 열어(열려 있으면 그대로) 새 장면을 만들고 SDK 창·"ALEdit 업로드" 창까지 연다.
        ///   batchmode는 쓰지 않는다 (사용자가 그 창에서 로그인·올리기). thumbnail: 올리기 창에 넘길 썸네일 (없으면 Unity 쪽이 장면에서 찍음).
        /// </summary>
        public static async Task<JNode> Export(Document doc, string project, string name, bool excludeBlinkFix, Action<string> log, Action<int, int, string?> step, CancellationToken ct, bool excludeMouthFix = false,
                                               bool upload = false, Func<Task<string?>>? thumbnail = null)
        {
            int cur = 0;
            try
            {
                step(cur, 1, null);
                project = Path.GetFullPath(project);
                if (!IsUnityProject(project)) throw new Exception("Unity 프로젝트 폴더가 아니에요. Assets와 ProjectSettings가 들어 있는 폴더를 골라 주세요.");
                if (Path.GetFullPath(Path.Combine(Root, "up")).TrimEnd('\\') is var up && project.StartsWith(up, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("업로드용 프로젝트에는 내보낼 수 없어요. 내 Unity 프로젝트를 골라 주세요.");
                if (upload && !HasAvatarSdk(project))
                    throw new Exception("이 Unity 프로젝트에 VRChat SDK(Avatars)가 없어요. VRChat Creator Companion으로 SDK를 넣은 뒤 다시 해 주세요.");
                var ver = ProjectUnityVersion(project) ?? "";
                bool open = IsOpenInUnity(project);
                string? unity = open ? null : ShaderPacks.FindUnity(ver);
                step(cur, 2, $"Unity {ver}" + (open ? " · 열려 있음" : unity == null ? " · 이 버전을 찾지 못함" : "")); cur++;

                step(cur, 1, null);
                var dir = "Assets/VrcProto/Export/" + SafeName(name) + "_" + DateTime.Now.ToString("yyMMdd-HHmmss");
                var thumb = upload && thumbnail != null ? await thumbnail() : null;
                var plan = UploadPlan.Build(doc, name, thumb, excludeBlinkFix, dir, excludeMouthFix);
                var work = Path.Combine(project, "VrcProto");
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, "apply.json"), plan.ToJson(true));
                foreach (var f in new[] { "status.json", "export-request.json" }) { var p = Path.Combine(work, f); if (File.Exists(p)) File.Delete(p); }
                var editor = Path.Combine(project, "Assets", "VrcProto", "Editor");
                Directory.CreateDirectory(editor);
                // 내용이 같으면 복사하지 않는다 (열린 Unity가 괜히 다시 컴파일하지 않게)
                CopyIfChanged(Path.Combine(AppContext.BaseDirectory, "Upload", "VrcProtoUpload.cs"), Path.Combine(editor, "VrcProtoUpload.cs"));
                CopyIfChanged(Path.Combine(AppContext.BaseDirectory, "Protocol.dll"), Path.Combine(editor, "VrcProto.Protocol.dll"));
                step(cur, 2, dir); cur++;

                step(cur, 1, null);
                int added = await Task.Run(() => ImportMissing(project, plan["sources"], log));
                step(cur, 2, added > 0 ? $"파일 {added}개 넣음" : "모두 있음"); cur++;

                step(cur, 1, null);
                JNode st;
                if (unity != null && !upload)
                {
                    log("Unity를 창 없이 열어 새 장면을 만드는 중… 프로젝트가 크면 처음 몇 분 걸려요.");
                    var (_, l) = await RunBatch(unity, project, "VrcProto.Upload.Setup.Export", "export.log");
                    st = ReadStatus(project);
                    if (st["stage"].AsString != "exported") throw new Exception(Fail("내보내지 못했어요", st, l));
                }
                else
                {
                    File.WriteAllText(Path.Combine(work, "export-request.json"), JNode.Obj().Set("time", DateTime.Now.ToString("o")).Set("upload", upload).ToJson());
                    if (unity != null)   // 올리기: 창으로 연다 (열리면 요청을 보고 장면을 만든 뒤 올리기 창을 연다)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(unity) { UseShellExecute = false };
                        psi.ArgumentList.Add("-projectPath"); psi.ArgumentList.Add(project);
                        System.Diagnostics.Process.Start(psi);
                        log("Unity를 여는 중… 열리면 새 장면을 만들고 올리기 창을 열어요. 프로젝트가 크면 몇 분 걸려요.");
                    }
                    else log(open ? "Unity 창으로 가면 새 장면을 만들어요 (스크립트를 처음 넣었으면 컴파일이 끝난 뒤). 열린 장면에 저장하지 않은 것이 있으면 Unity가 먼저 물어봐요."
                                  : $"이 프로젝트의 Unity {ver}를 찾지 못했어요. Unity Hub에서 이 프로젝트를 열면 새 장면을 자동으로 만들어요.");
                    step(cur, 1, "Unity에서 기다리는 중");
                    // 올리기는 장면을 만든 바로 뒤 "opened"로 바뀔 수 있어 둘 다 받는다
                    st = await WaitStatus(project, upload ? new[] { "exported", "opened" } : new[] { "exported" }, ct);
                }
                foreach (var l in st["log"].Items) log("  " + l.AsString);
                if (st["scene"].IsNull) throw new Exception(st["message"].AsString.Length > 0 ? st["message"].AsString : "새 장면을 만들지 못했어요");
                step(cur, st["ok"].AsBool ? 2 : 3, st["ok"].AsBool ? st["scene"].AsString : "일부를 찾지 못했어요 (로그 확인)");
                return st;
            }
            catch { step(cur, 3, null); throw; }
        }

        static void CopyIfChanged(string from, string to)
        {
            if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from))) return;
            File.Copy(from, to, true);
        }

        static async Task<JNode> WaitStatus(string project, string[] stages, CancellationToken ct)
        {
            while (true)
            {
                await Task.Delay(1000, ct);
                var st = ReadStatus(project);
                if (stages.Contains(st["stage"].AsString)) return st;
            }
        }

        /// <summary>프로젝트에 VRChat SDK(Avatars)가 있는지: Packages 폴더·manifest·vpm-manifest·PackageCache 중 하나.</summary>
        public static bool HasAvatarSdk(string project)
        {
            var pk = Path.Combine(project, "Packages");
            if (Directory.Exists(Path.Combine(pk, UploadPlan.SdkId))) return true;
            foreach (var f in new[] { "manifest.json", "vpm-manifest.json" })
            {
                try
                {
                    var p = Path.Combine(pk, f);
                    if (!File.Exists(p)) continue;
                    var j = JNode.Parse(File.ReadAllText(p));
                    if (j["dependencies"].Has(UploadPlan.SdkId) || j["locked"].Has(UploadPlan.SdkId)) return true;
                }
                catch (Exception) { }
            }
            var cache = Path.Combine(project, "Library", "PackageCache");
            return Directory.Exists(cache) && Directory.EnumerateDirectories(cache, UploadPlan.SdkId + "*").Any();
        }

        /// <summary>
        /// 내보낼 프로젝트에 없는 원본만 넣는다: .unitypackage 항목과 다른 폴더의 파일 중 GUID가 프로젝트에 없고 그 경로에 파일도 없는 것.
        /// 프로젝트 자신의 Assets는 건너뛴다. 반환: 넣은 파일 수.
        /// </summary>
        public static int ImportMissing(string project, JNode sources, Action<string> log)
        {
            var assets = Path.GetFullPath(Path.Combine(project, "Assets"));
            var have = ProjectGuids(project);
            int n = 0;
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
                        if (parts.Length != 2 || (parts[1] != "pathname" && parts[1] != "asset" && parts[1] != "asset.meta") || have.Contains(parts[0])) return;
                        var d = Path.Combine(stage, parts[0]);
                        Directory.CreateDirectory(d);
                        File.WriteAllBytes(Path.Combine(d, parts[1]), read());
                    });
                    int k = 0, exists = 0;
                    foreach (var d in Directory.Exists(stage) ? Directory.GetDirectories(stage) : Array.Empty<string>())
                    {
                        var pn = Path.Combine(d, "pathname");
                        if (!File.Exists(pn)) continue;
                        var rel = File.ReadAllText(pn).Split('\n')[0].Trim().Replace('\\', '/');
                        if (!rel.StartsWith("Assets/") || rel.Split('/').Any(x => x == "..")) continue;
                        var dst = Path.GetFullPath(Path.Combine(project, rel));
                        if (!dst.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) continue;
                        var asset = Path.Combine(d, "asset");
                        if (File.Exists(dst) || File.Exists(dst + ".meta")) { exists++; continue; }   // 다른 GUID로 같은 경로가 있음 → 덮어쓰지 않음
                        if (File.Exists(asset)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(asset, dst); }
                        else Directory.CreateDirectory(dst);
                        var meta = Path.Combine(d, "asset.meta");
                        if (File.Exists(meta)) File.Copy(meta, dst + ".meta");
                        have.Add(Path.GetFileName(d)); k++;
                    }
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    if (k > 0 || exists > 0) log($"넣기: {Path.GetFileName(src)} ({k}개" + (exists > 0 ? $", 같은 경로에 다른 파일이 있어 {exists}개 건너뜀" : "") + ")");
                    n += k;
                }
                else if (Directory.Exists(src))
                {
                    var from = Path.GetFullPath(src).TrimEnd('\\', '/');
                    if (from.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) continue;   // 이 프로젝트 자신
                    var to = Path.GetFileName(from).Equals("Assets", StringComparison.OrdinalIgnoreCase) ? assets : Path.Combine(assets, Path.GetFileName(from));
                    int k = 0;
                    foreach (var meta in Directory.GetFiles(from, "*.meta", SearchOption.AllDirectories))
                    {
                        var g = MetaGuid(meta);
                        if (g == null || have.Contains(g)) continue;
                        var file = meta.Substring(0, meta.Length - 5);
                        var dst = Path.Combine(to, Path.GetRelativePath(from, file));
                        if (File.Exists(dst + ".meta")) continue;
                        if (Directory.Exists(file)) Directory.CreateDirectory(dst);
                        else if (File.Exists(file)) { if (File.Exists(dst)) continue; Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(file, dst); }
                        else continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(meta, dst + ".meta");
                        have.Add(g); k++;
                    }
                    if (k > 0) log($"넣기: {src} ({k}개)");
                    n += k;
                }
                else log("원본을 찾지 못함: " + src);
            }
            return n;
        }

        /// <summary>프로젝트에 있는 에셋 GUID (Assets·Packages·Library/PackageCache의 .meta).</summary>
        static HashSet<string> ProjectGuids(string project)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in new[] { "Assets", "Packages", Path.Combine("Library", "PackageCache") })
            {
                var d = Path.Combine(project, sub);
                if (!Directory.Exists(d)) continue;
                foreach (var m in Directory.EnumerateFiles(d, "*.meta", SearchOption.AllDirectories))
                    if (MetaGuid(m) is string g) set.Add(g);
            }
            return set;
        }

        static string? MetaGuid(string meta)
        {
            try
            {
                using var r = new StreamReader(meta);
                for (int i = 0; i < 4 && r.ReadLine() is string l; i++)
                    if (l.StartsWith("guid:")) return l.Substring(5).Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }
}
