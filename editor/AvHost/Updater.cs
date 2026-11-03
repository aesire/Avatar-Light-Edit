// 온라인 업데이트 (2026-10-19): GitHub Releases의 최신 릴리스를 확인하고, zip을 받아 앱 폴더를 바꾼 뒤 다시 시작한다.
//   버전      앱 폴더의 version.txt (릴리스 zip에 넣는다, 예: 1.2.0). 없으면 "dev" — 개발 빌드는 자동 확인을 하지 않는다
//   릴리스    태그 = 버전 (앞의 v는 무시), 첨부 파일 중 .zip 하나 = 앱 폴더 통째로 (VrcProtoAv.dll · Renderer/ …, 맨 위에 폴더 하나로 싸여 있어도 된다)
//   바꾸기    %TEMP%에 풀고, 이 프로그램이 끝나기를 기다렸다가 앱 폴더에 덮어쓰고 다시 여는 배치 파일을 실행한다 (사용자 설정·세션은 %APPDATA%라 그대로)
//   저장소    앱 폴더의 update-repo.txt (2026-11-01, 배포 빌드가 넣는다 — 저장소 주소를 코드 대신 릴리스 때 정한다)
//   확인      GitHub가 첨부 파일에 sha256 digest를 주면 받은 zip과 맞춰 본다 (2026-11-01)
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public static class Updater
    {
        /// <summary>업데이트를 받을 GitHub 저장소 "owner/name" (설정에서 바꿀 수 있다). 개발 빌드는 빈 값.</summary>
        public static string DefaultRepo
        {
            get
            {
                try { var p = Path.Combine(AppDir, "update-repo.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
                catch { return ""; }
            }
        }

        /// <summary>시험 (--test --test-update): GitHub API 대신 볼 주소. 테스트 모드에서만 쓴다.</summary>
        public static string ApiBase = "https://api.github.com";

        public sealed class Release
        {
            public string Tag = "", Name = "", Notes = "", Url = "", ZipUrl = "", ZipName = "", ZipSha256 = "";
            public long ZipSize;
            public Version Version = new Version(0, 0);
        }

        public static string AppDir => AppContext.BaseDirectory;

        /// <summary>지금 버전 (version.txt). 없으면 null = 개발 빌드.</summary>
        public static string? CurrentText
        {
            get
            {
                try { var p = Path.Combine(AppDir, "version.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
                catch { return null; }
            }
        }

        /// <summary>"v1.2.3-beta" → 1.2.3. 숫자가 없으면 null.</summary>
        public static Version? Parse(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim().TrimStart('v', 'V');
            var nums = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            var parts = nums.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            if (parts.Count == 0) return null;
            while (parts.Count < 2) parts.Add(0);
            return parts.Count switch { 2 => new Version(parts[0], parts[1]), 3 => new Version(parts[0], parts[1], parts[2]), _ => new Version(parts[0], parts[1], parts[2], parts[3]) };
        }

        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("ALEdit-Updater");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        /// <summary>최신 릴리스 (초안·프리릴리스 제외). zip 첨부가 없으면 ZipUrl = "".</summary>
        public static async Task<Release> Latest(string repo, CancellationToken ct = default)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("저장소는 \"owner/name\" 모양이어야 해요");
            using var res = await Http.GetAsync($"{ApiBase}/repos/{repo}/releases/latest", ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("릴리스가 없거나 저장소를 찾지 못했어요 (비공개 저장소는 쓸 수 없어요)");
            res.EnsureSuccessStatusCode();
            var j = JNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = new Release { Tag = j["tag_name"].AsString, Name = j["name"].AsString, Notes = j["body"].AsString, Url = j["html_url"].AsString };
            r.Version = Parse(r.Tag) ?? new Version(0, 0);
            var zip = j["assets"].Items.FirstOrDefault(a => a["name"].AsString.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip != null) { r.ZipUrl = zip["browser_download_url"].AsString; r.ZipName = zip["name"].AsString; r.ZipSize = (long)zip["size"].AsDouble; var d = zip["digest"].AsString; if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.ZipSha256 = d.Substring(7).ToLowerInvariant(); }
            return r;
        }

        public static bool IsNewer(Release r) => Parse(CurrentText) is not Version cur || r.Version > cur;

        /// <summary>zip을 받아 풀고, 앱 폴더로 옮길 준비를 한다. 돌려준 배치 파일을 Apply로 실행하면 바뀐다.</summary>
        public static async Task<string> Prepare(Release r, IProgress<double>? progress, CancellationToken ct = default)
        {
            if (r.ZipUrl.Length == 0) throw new InvalidOperationException("이 릴리스에 zip 파일이 없어요");
            var work = Path.Combine(Path.GetTempPath(), "VrcProto_update", Safe(r.Tag));
            // 큰 파일을 다루는 일은 작업 스레드에서 (UI 스레드에서 부르므로 창이 멈추지 않게)
            await Task.Run(() => { if (Directory.Exists(work)) Directory.Delete(work, true); Directory.CreateDirectory(work); }, ct);
            var zipPath = Path.Combine(work, "update.zip");
            using (var res = await Http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? r.ZipSize;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[1 << 16]; long got = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Report((double)got / total);
                }
            }
            var stage = Path.Combine(work, "files");
            await Task.Run(() =>
            {
                if (r.ZipSha256.Length > 0)
                {
                    using var fs = File.OpenRead(zipPath);
                    var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
                    if (got != r.ZipSha256) throw new InvalidDataException("받은 파일이 릴리스와 달라요 (해시가 맞지 않음). 다시 시도해 주세요");
                }
                ZipFile.ExtractToDirectory(zipPath, stage);
            }, ct);
            // 맨 위가 폴더 하나뿐이면 그 안이 앱 폴더
            var root = stage;
            while (!File.Exists(Path.Combine(root, "VrcProtoAv.dll")) && Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is { Length: 1 } one) root = one[0];
            if (!File.Exists(Path.Combine(root, "VrcProtoAv.dll"))) throw new InvalidDataException("zip 안에 VrcProtoAv.dll이 없어요 (이 프로그램의 릴리스가 아니에요)");
            if (!File.Exists(Path.Combine(root, "version.txt"))) File.WriteAllText(Path.Combine(root, "version.txt"), r.Tag.TrimStart('v', 'V'));

            var app = AppDir.TrimEnd('\\', '/');
            var cmd = Path.Combine(work, "update.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("echo ALEdit 업데이트: 프로그램이 끝나기를 기다려요...");
            sb.AppendLine($":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>nul | find \"{Environment.ProcessId}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            // 이 앱 폴더의 렌더러만 (다른 곳에서 띄운 것은 두고, 2026-11-01)
            sb.AppendLine($"powershell -NoProfile -Command \"Get-Process VrcProtoRenderer -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{Bat(app.Replace("'", "''"))}\\*' }} | Stop-Process -Force\" >nul 2>nul");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            // robocopy: 0~7 = 성공. 바뀐 파일만 덮어쓴다 (지우지 않는다: 사용자가 넣은 셰이더 팩 등은 남긴다)
            sb.AppendLine($"robocopy \"{Bat(root)}\" \"{Bat(app)}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >nul");
            sb.AppendLine("if %ERRORLEVEL% GEQ 8 (echo 업데이트 파일을 복사하지 못했어요. 프로그램 폴더 권한을 확인해 주세요. & pause & exit /b 1)");
            // 배포판은 ALEdit.exe(앱 폴더 바깥, .NET 함께 넣음)로 켜져 있다 — 그 exe로 다시 연다. 개발 빌드는 dotnet
            var self = Environment.ProcessPath;
            var launcher = Path.Combine(app, "VrcProtoAv.cmd");
            if (self != null && !Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) sb.AppendLine($"start \"\" \"{Bat(self)}\"");
            else sb.AppendLine(File.Exists(launcher) ? $"start \"\" \"{Bat(launcher)}\"" : $"start \"\" dotnet \"{Bat(Path.Combine(app, "VrcProtoAv.dll"))}\"");
            sb.AppendLine($"rmdir /S /Q \"{Bat(stage)}\" >nul 2>nul");
            sb.AppendLine($"del /Q \"{Bat(zipPath)}\" >nul 2>nul");
            File.WriteAllText(cmd, sb.ToString(), new UTF8Encoding(false));
            return cmd;
        }

        /// <summary>배치 파일을 띄운다. 부른 쪽은 곧바로 프로그램을 끝내야 한다.</summary>
        public static void Apply(string cmd)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetTempPath() });
        }

        /// <summary>배치 파일 안의 경로: %가 변수로 읽히지 않게</summary>
        static string Bat(string path) => path.Replace("%", "%%");

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Length == 0 ? "latest" : s; }
    }
}
