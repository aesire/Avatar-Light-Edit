// 배포 zip 만들기 (2026-11-01). build_release.sh가 부른다. 업데이터(AvHost/Updater.cs)가 기대하는 모양:
//   zip 맨 위 폴더 하나 안에 앱 폴더 통째 = ALEdit.exe · VrcProtoAv.dll · .NET 8 런타임 · Renderer/ · version.txt · update-repo.txt
//   AI가 찾아 쓰는 촬영 도구도 함께 (2026-11-02, 사용자 결정): Shoot.exe · Shoot.dll · shoot-poses/ · AGENTS.md · CLAUDE.md · SHOOT.md. README 등 사람이 읽는 곳에는 적지 않는다.
// .NET을 함께 넣어 설치 없이 돈다 (apphost + runtimeconfig의 includedFrameworks + deps.json).
// deps.json이 꼭 있어야 한다 (2026-11-02 발견): 없으면 hostfxr가 현재 폴더의 <앱>.runtimeconfig.json을 보고 옛 "split" 모드로 판단해,
// 프로그램 폴더 안에서 실행하면(= 탐색기 더블클릭) 바로 실패했다. 다른 폴더에서 부를 때만 켜져 시험에서 지나쳤다.
// 넣을 것은 bin/av에서 고르고, 넣으면 안 되는 것(시험 도구·샘플·PDB·VRChat SDK·사용자 셰이더 팩·빌드 PC 경로)이 있으면 실패한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

static class MakeRelease
{
    const string NetVersion = "8.0.27";
    const string ExeName = "ALEdit.exe";
    const string ZipBase = "ALEdit";
    // apphost 안에서 앱 dll 이름으로 바꿔 쓰는 자리 ("foobar"의 SHA-256, .NET SDK와 같은 값)
    const string Placeholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var version = Opt("--version");
        var repo = Opt("--repo");
        bool noRepo = args.Contains("--no-repo");
        if (version == null || !Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") || (repo == null && !noRepo) || (repo != null && !Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")))
        {
            Console.Error.WriteLine("사용: MakeRelease --version 1.2.3 (--repo owner/name | --no-repo) [--renderer 폴더] [--out 폴더] [--dotnet 폴더]");
            return 2;
        }
        var renderer = Opt("--renderer") ?? "Unity/Build";
        var outDir = Path.GetFullPath(Opt("--out") ?? "dist");
        var dotnet = Opt("--dotnet") ?? @"C:\Program Files\dotnet";
        try
        {
            var folder = $"{ZipBase}-{version}";
            var stage = Path.Combine(outDir, folder);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);

            Step("에디터");
            CopyEditor("bin/av", stage);
            Step(".NET " + NetVersion);
            CopyRuntime(dotnet, stage);
            Step("실행 파일");
            File.WriteAllBytes(Path.Combine(stage, ExeName), MakeExe());
            EmbedManifest(Path.Combine(stage, ExeName));
            EmbedIcon(Path.Combine(stage, ExeName), Path.Combine("branding", "ALEdit.ico"));
            File.WriteAllText(Path.Combine(stage, "VrcProtoAv.runtimeconfig.json"), RuntimeConfig());
            Step("촬영 도구");
            CopyShoot(stage);
            WriteDeps(stage, "VrcProtoAv", n => !n.Equals("Shoot.dll", StringComparison.OrdinalIgnoreCase));
            WriteDeps(stage, "Shoot", n => !n.Equals("VrcProtoAv.dll", StringComparison.OrdinalIgnoreCase));
            Step("렌더러");
            CopyDir(renderer, Path.Combine(stage, "Renderer"), name => !name.Contains("_DoNotShip"));
            WarnProtocol(stage);
            File.WriteAllText(Path.Combine(stage, "version.txt"), version);
            if (repo != null) File.WriteAllText(Path.Combine(stage, "update-repo.txt"), repo);
            else Console.WriteLine("  주의: --no-repo — 이 빌드는 온라인 업데이트를 확인하지 않아요");

            Step("빌드 PC 경로 지우기·점검");
            ScrubPdbPaths(Path.Combine(stage, "Renderer", "VrcProtoRenderer_Data", "Managed"));
            Check(stage);

            Step("압축");
            var zip = Path.Combine(outDir, folder + ".zip");
            File.Delete(zip);
            ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
            string hash;
            using (var fs = File.OpenRead(zip)) hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            File.WriteAllText(zip + ".sha256", $"{hash}  {Path.GetFileName(zip)}\n");
            long files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Count();
            Console.WriteLine($"완료: {zip}\n  파일 {files}개 · 압축 {new FileInfo(zip).Length / 1048576} MB · sha256 {hash}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("실패: " + e.Message); return 1; }
    }

    static void Step(string s) => Console.WriteLine("[release] " + s);

    /// <summary>bin/av에서 필요한 것만: 빌드가 남긴 임시 dll(GUID_이름.dll)·개발용 cmd·runtimeconfig(새로 씀)는 뺀다</summary>
    static void CopyEditor(string av, string stage)
    {
        var guid = new Regex(@"^[0-9a-f]{8}-[0-9a-f]{4}-", RegexOptions.IgnoreCase);
        foreach (var f in Directory.GetFiles(av))
        {
            var n = Path.GetFileName(f);
            if (guid.IsMatch(n) || n == "VrcProtoAv.cmd" || n.EndsWith(".runtimeconfig.json") || n.EndsWith(".pdb") || n == "version.txt" || n == "update-repo.txt") continue;
            File.Copy(f, Path.Combine(stage, n));
        }
        foreach (var d in new[] { "texts", "Localization", "ShaderPack", "Upload" }) CopyDir(Path.Combine(av, d), Path.Combine(stage, d));
        foreach (var need in new[] { "VrcProtoAv.dll", "Protocol.dll", "ImportSim.Core.dll", "README.txt", "README.en.txt", "README.ja.txt", "LICENSE.txt", "COPYING.txt", "THIRD_PARTY_NOTICES.txt", "texts/ko.tsv", "texts/en.tsv", "texts/ja.tsv", "Upload/VrcProtoUpload.cs", "ShaderPack/ShaderPackBuilder.cs" })
            if (!File.Exists(Path.Combine(stage, need))) throw new FileNotFoundException("bin/av에 없어요: " + need);
    }

    /// <summary>촬영 도구와 AI용 안내. SHOOT.md는 스킬 문서에서 개발용 내용(숨김 원칙·작업 기록·소스 경로)을 뺀 것.</summary>
    static void CopyShoot(string stage)
    {
        File.Copy("bin/Shoot.dll", Path.Combine(stage, "Shoot.dll"));
        var exe = Path.Combine(stage, "Shoot.exe");
        File.WriteAllBytes(exe, MakeExe("Shoot.dll", gui: false));
        EmbedManifest(exe);
        File.WriteAllText(Path.Combine(stage, "Shoot.runtimeconfig.json"), RuntimeConfig());
        CopyDir("bin/shoot-poses", Path.Combine(stage, "shoot-poses"));
        File.Copy("release/AGENTS.md", Path.Combine(stage, "AGENTS.md"));
        File.WriteAllText(Path.Combine(stage, "CLAUDE.md"), "AGENTS.md를 읽으세요.\n");
        var doc = File.ReadAllText("../.claude/skills/avatar-shoot/SHOOT.md").Replace("\r\n", "\n");
        int cut = doc.IndexOf("## 숨김 원칙", StringComparison.Ordinal);
        if (cut < 0) throw new InvalidDataException("SHOOT.md에서 잘라 낼 자리를 찾지 못했어요");
        doc = doc.Substring(0, cut).TrimEnd() + "\n";
        doc = doc.Replace("dotnet bin/Shoot.dll", "Shoot.exe");
        doc = Regex.Replace(doc, @"^`editor` 폴더에서 실행한다\..*$", "`Shoot.exe`가 있는 폴더에서 실행한다.", RegexOptions.Multiline);
        doc = doc.Replace("머슬 95개 이름은 `editor/Host/Core/HumanPose.cs`의 `Muscles`.", "머슬 95개 이름은 `Shoot.exe inspect <아바타> --muscles`.");
        doc = doc.Replace(" (`editor/Protocol/Messages.cs`)", "").Replace("(`editor/Tools/Shoot/poses`) ", "");
        doc = doc.Replace("../avatar-edit/EDIT.md", "EDIT.md");
        if (doc.Contains("editor/") || doc.Contains("bin/") || doc.Contains("../")) throw new InvalidDataException("SHOOT.md에 소스 경로가 남았어요: " + Regex.Match(doc, @".{0,40}(editor/|bin/|\.\./).{0,40}").Value);
        File.WriteAllText(Path.Combine(stage, "SHOOT.md"), doc);
        // 외형 고치기 안내도 함께 (2026-11-03): 작업 기록을 빼고 명령을 배포본 이름으로
        var edit = File.ReadAllText("../.claude/skills/avatar-edit/EDIT.md").Replace("\r\n", "\n");
        int ecut = edit.IndexOf("## 작업 기록", StringComparison.Ordinal);
        if (ecut < 0) throw new InvalidDataException("EDIT.md에서 잘라 낼 자리를 찾지 못했어요");
        edit = edit.Substring(0, ecut).TrimEnd() + "\n";
        edit = edit.Replace("dotnet bin/Shoot.dll", "Shoot.exe").Replace("dotnet bin/av/VrcProtoAv.dll", ExeName);
        if (edit.Contains("editor/") || edit.Contains("bin/") || edit.Contains("../")) throw new InvalidDataException("EDIT.md에 소스 경로가 남았어요: " + Regex.Match(edit, @".{0,40}(editor/|bin/|\.\./).{0,40}").Value);
        File.WriteAllText(Path.Combine(stage, "EDIT.md"), edit);
    }

    /// <summary>폴더의 dll을 관리 어셈블리·네이티브로 나눠 적은 deps.json (dotnet publish의 자체 포함 앱과 같은 모양).</summary>
    static void WriteDeps(string stage, string app, Func<string, bool> keep)
    {
        var nl = "\n";
        string List(bool managed) => string.Join("," + nl, Directory.GetFiles(stage, "*.dll").Where(f => keep(Path.GetFileName(f)) && IsManaged(f) == managed)
            .Select(f => "          \"" + Path.GetFileName(f) + "\": {}").OrderBy(s => s, StringComparer.Ordinal));
        const string Target = ".NETCoreApp,Version=v8.0/win-x64";
        var json = new[]
        {
            "{",
            "  \"runtimeTarget\": { \"name\": \"" + Target + "\", \"signature\": \"\" },",
            "  \"targets\": {",
            "    \"" + Target + "\": {",
            "      \"" + app + "/1.0.0\": {",
            "        \"runtime\": {", List(true), "        },",
            "        \"native\": {", List(false), "        }",
            "      }",
            "    }",
            "  },",
            "  \"libraries\": { \"" + app + "/1.0.0\": { \"type\": \"project\", \"serviceable\": false, \"sha512\": \"\" } }",
            "}",
        };
        File.WriteAllText(Path.Combine(stage, app + ".deps.json"), string.Join(nl, json) + nl);
    }

    static bool IsManaged(string dll)
    {
        using var s = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(s);
        return pe.HasMetadata;
    }

    static void CopyRuntime(string dotnet, string stage)
    {
        var shared = Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", NetVersion);
        var fxr = Path.Combine(dotnet, "host", "fxr", NetVersion, "hostfxr.dll");
        if (!Directory.Exists(shared) || !File.Exists(fxr)) throw new DirectoryNotFoundException(".NET 런타임 " + NetVersion + "을 찾지 못했어요: " + shared);
        foreach (var f in Directory.GetFiles(shared))
        {
            var n = Path.GetFileName(f);
            if (n == "createdump.exe" || n.EndsWith(".deps.json")) continue;   // 앱 실행에 쓰지 않는다
            File.Copy(f, Path.Combine(stage, n));
        }
        File.Copy(fxr, Path.Combine(stage, "hostfxr.dll"));
        var lic = Path.Combine(stage, "licenses");
        Directory.CreateDirectory(lic);
        File.Copy(Path.Combine(dotnet, "LICENSE.txt"), Path.Combine(lic, "dotnet_LICENSE.txt"));
        File.Copy(Path.Combine(dotnet, "ThirdPartyNotices.txt"), Path.Combine(lic, "dotnet_ThirdPartyNotices.txt"));
        File.Copy(Path.Combine("release", "APACHE_LICENSE_2.0.txt"), Path.Combine(lic, "APACHE_LICENSE_2.0.txt"));   // MediaPipe 모델
    }

    /// <summary>apphost 틀을 nuget.org에서 받아 두고(처음 한 번), 앱 dll 이름을 넣고 창 프로그램으로 표시한다 (콘솔 창이 뜨지 않게).</summary>
    static byte[] MakeExe(string dllName = "VrcProtoAv.dll", bool gui = true)
    {
        var pkg = Path.Combine("packages", "nupkg", $"microsoft.netcore.app.host.win-x64.{NetVersion}.nupkg");
        if (!File.Exists(pkg))
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.host.win-x64/{NetVersion}/microsoft.netcore.app.host.win-x64.{NetVersion}.nupkg";
            Console.WriteLine("  받는 중: " + url);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(pkg)!);
            File.WriteAllBytes(pkg, data);
        }
        byte[] exe;
        using (var z = ZipFile.OpenRead(pkg))
        using (var s = (z.GetEntry("runtimes/win-x64/native/apphost.exe") ?? throw new FileNotFoundException("패키지에 apphost.exe가 없어요")).Open())
        using (var m = new MemoryStream()) { s.CopyTo(m); exe = m.ToArray(); }

        var ph = Encoding.ASCII.GetBytes(Placeholder);
        int at = IndexOf(exe, ph, 0);
        if (at < 0) throw new InvalidDataException("apphost에서 이름 자리를 찾지 못했어요");
        Array.Clear(exe, at, ph.Length);
        var dll = Encoding.UTF8.GetBytes(dllName);
        Array.Copy(dll, 0, exe, at, dll.Length);
        int pe = BitConverter.ToInt32(exe, 0x3C);
        if (gui) BitConverter.GetBytes((ushort)2).CopyTo(exe, pe + 0x5C);   // IMAGE_SUBSYSTEM_WINDOWS_GUI
        return exe;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UpdateResourceW(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool EndUpdateResourceW(IntPtr h, bool discard);

    /// <summary>
    /// apphost 틀에는 매니페스트가 없다. dotnet.exe와 달리 지원 OS 목록이 없으면 Avalonia가 자식 창을 만들지 못해 켜자마자 꺼진다
    /// ("Unable to create child window", 2026-11-01 배포 시험에서 발견).
    /// </summary>
    static void EmbedManifest(string exe)
    {
        var xml = Encoding.UTF8.GetBytes(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <assemblyIdentity version=""1.0.0.0"" name=""ALEdit.app""/>
  <compatibility xmlns=""urn:schemas-microsoft-com:compatibility.v1"">
    <application>
      <supportedOS Id=""{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}""/>
    </application>
  </compatibility>
  <application xmlns=""urn:schemas-microsoft-com:asm.v3"">
    <windowsSettings>
      <dpiAware xmlns=""http://schemas.microsoft.com/SMI/2005/WindowsSettings"">true/pm</dpiAware>
      <dpiAwareness xmlns=""http://schemas.microsoft.com/SMI/2016/WindowsSettings"">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type=""win32"" name=""Microsoft.Windows.Common-Controls"" version=""6.0.0.0"" processorArchitecture=""*"" publicKeyToken=""6595b64144ccf1df"" language=""*""/>
    </dependentAssembly>
  </dependency>
</assembly>
");
        var h = BeginUpdateResourceW(exe, false);
        if (h == IntPtr.Zero) throw new IOException("실행 파일 리소스를 열지 못했어요: " + Marshal.GetLastWin32Error());
        if (!UpdateResourceW(h, (IntPtr)24, (IntPtr)1, 0, xml, (uint)xml.Length)) throw new IOException("매니페스트를 넣지 못했어요: " + Marshal.GetLastWin32Error());   // RT_MANIFEST, CREATEPROCESS_MANIFEST_RESOURCE_ID
        if (!EndUpdateResourceW(h, false)) throw new IOException("실행 파일에 쓰지 못했어요: " + Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// 앱 로고(editor/branding/ALEdit.ico)를 탐색기·작업 표시줄 아이콘으로 넣는다 (2026-11-02).
    /// ICO의 그림마다 RT_ICON 하나, 그것들을 묶는 RT_GROUP_ICON 하나. 그룹 항목은 ICO 항목과 같고 끝 4바이트(파일 위치)만 리소스 번호 2바이트로 바뀐다.
    /// </summary>
    static void EmbedIcon(string exe, string ico)
    {
        var d = File.ReadAllBytes(ico);
        int count = BitConverter.ToUInt16(d, 4);
        if (BitConverter.ToUInt16(d, 2) != 1 || count == 0) throw new InvalidDataException("ICO 파일이 아니에요: " + ico);
        var group = new MemoryStream();
        group.Write(d, 0, 6);
        var h = BeginUpdateResourceW(exe, false);
        if (h == IntPtr.Zero) throw new IOException("실행 파일 리소스를 열지 못했어요: " + Marshal.GetLastWin32Error());
        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            int size = BitConverter.ToInt32(d, e + 8), at = BitConverter.ToInt32(d, e + 12);
            var img = new byte[size];
            Array.Copy(d, at, img, 0, size);
            if (!UpdateResourceW(h, (IntPtr)3, (IntPtr)(i + 1), 0, img, (uint)size)) throw new IOException("아이콘을 넣지 못했어요: " + Marshal.GetLastWin32Error());   // RT_ICON
            group.Write(d, e, 12);
            group.Write(BitConverter.GetBytes((ushort)(i + 1)), 0, 2);
        }
        var g = group.ToArray();
        if (!UpdateResourceW(h, (IntPtr)14, (IntPtr)1, 0, g, (uint)g.Length)) throw new IOException("아이콘 묶음을 넣지 못했어요: " + Marshal.GetLastWin32Error());   // RT_GROUP_ICON
        if (!EndUpdateResourceW(h, false)) throw new IOException("실행 파일에 쓰지 못했어요: " + Marshal.GetLastWin32Error());
    }

    /// <summary>함께 넣은 런타임을 쓰는 설정. GC 값은 build.sh의 runtimeconfig와 같게.</summary>
    static string RuntimeConfig() => "{ \"runtimeOptions\": { \"tfm\": \"net8.0\",\n"
        + "  \"includedFrameworks\": [ { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" + NetVersion + "\" } ],\n"
        + "  \"configProperties\": { \"System.GC.Server\": true, \"System.GC.DynamicAdaptationMode\": 1 } } }\n";

    /// <summary>Unity 빌드 어셈블리에는 PDB 경로(빌드 PC의 전체 경로)가 적혀 있다 → 같은 길이의 0으로 지운다</summary>
    static void ScrubPdbPaths(string managed)
    {
        var pdb = Encoding.ASCII.GetBytes(".pdb");
        foreach (var f in Directory.GetFiles(managed, "Assembly-CSharp*.dll"))
        {
            var d = File.ReadAllBytes(f);
            bool changed = false;
            for (int i = IndexOf(d, pdb, 0); i >= 0; i = IndexOf(d, pdb, i + 4))
            {
                if (i + 4 >= d.Length || d[i + 4] != 0) continue;
                int s = i;
                while (s > 0 && d[s - 1] >= 0x20 && d[s - 1] < 0x7F) s--;
                if (i - s < 3 || d[s + 1] != ':') continue;   // "C:\…" 모양만
                Array.Clear(d, s, i + 4 - s);
                changed = true;
            }
            if (changed) { File.WriteAllBytes(f, d); Console.WriteLine("  경로 지움: " + Path.GetFileName(f)); }
        }
    }

    /// <summary>들어가면 안 되는 것이 있으면 실패</summary>
    static void Check(string stage)
    {
        var problems = new List<string>();
        var all = Directory.GetFiles(stage, "*", SearchOption.AllDirectories);
        var banned = new Regex(@"(^|[\\/])(HostTests|E2E\.|MakeSampleIr|MakeRelease|sample[\\/]|e2e_out|avatar-shoot|VRCSDK|VRC\.SDK|VRCFury|VRC\.Dynamics)|\.pdb$|\.prof$", RegexOptions.IgnoreCase);
        var packOk = new Regex(@"^(poiyomi_[\d.]+(\.variants)?\.(bundle|txt)|poiyomi_[\d.]+\.bundle|vrm_mtoon\.(bundle|txt)|README\.txt)$", RegexOptions.IgnoreCase);
        foreach (var f in all)
        {
            var rel = Path.GetRelativePath(stage, f);
            if (banned.IsMatch(rel)) problems.Add("넣으면 안 되는 파일: " + rel);
            var dir = Path.GetDirectoryName(rel) ?? "";
            if (dir.Replace('\\', '/').EndsWith("StreamingAssets/ShaderPacks") && !packOk.IsMatch(Path.GetFileName(f))) problems.Add("기본 팩이 아닌 셰이더 팩: " + rel);
        }
        foreach (var need in new[] { ExeName, "VrcProtoAv.deps.json", "Shoot.deps.json", "Shoot.exe", "Shoot.dll", "Shoot.runtimeconfig.json", "AGENTS.md", "SHOOT.md", "EDIT.md", "shoot-poses/peace_cheek.pose.json", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "Renderer/VrcProtoRenderer.exe", "Renderer/UnityPlayer.dll", "licenses/dotnet_LICENSE.txt", "Renderer/VrcProtoRenderer_Data/StreamingAssets/ShaderPacks/licenses/lilToon_LICENSE.txt", "Renderer/VrcProtoRenderer_Data/StreamingAssets/FaceModel/NOTICE.txt" })
            if (!File.Exists(Path.Combine(stage, need))) problems.Add("빠진 파일: " + need);
        // 보안 패치 (2026-11-03, CVE-2025-59489): 고치기 전 2022.3.22f1 UnityPlayer.dll이면 실패 — Unity Application Patcher로 고친 것만
        var player = Path.Combine(stage, "Renderer", "UnityPlayer.dll");
        if (File.Exists(player) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(player))).Equals("C2871EE449BEF29E658380BF47F1A23839ABDE7D1E4DCB9BC1ADA7A93AE08DA6", StringComparison.OrdinalIgnoreCase))
            problems.Add("렌더러 UnityPlayer.dll이 보안 패치 전 원본 (Unity/PatchedPlayer에 고친 사본을 두거나 패처로 고치기 — editor/README.md \"배포\")");
        // 빌드 PC 사용자 이름·경로
        var user = Environment.UserName;
        if (user.Length >= 3)
        {
            var a = Encoding.ASCII.GetBytes(user); var w = Encoding.Unicode.GetBytes(user);
            // 저작권자 연락처·업데이트 저장소에는 같은 이름이 일부러 들어간다 (2026-11-03, GitHub aesire = 빌드 PC 사용자 이름)
            var meant = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LICENSE.txt", "update-repo.txt" };
            foreach (var f in all)
            {
                if (meant.Contains(Path.GetRelativePath(stage, f))) continue;
                if (new FileInfo(f).Length > 1L << 31) continue;
                var d = File.ReadAllBytes(f);
                if (IndexOf(d, a, 0) >= 0 || IndexOf(d, w, 0) >= 0) problems.Add("빌드 PC 사용자 이름이 들어 있음: " + Path.GetRelativePath(stage, f));
            }
        }
        if (problems.Count > 0) throw new InvalidDataException("배포물 점검 실패\n  " + string.Join("\n  ", problems));
        Console.WriteLine($"  점검 통과 (파일 {all.Length}개)");
    }

    /// <summary>Protocol이 다르면 렌더러가 옛 빌드일 수 있다 (같은 소스도 빌드 경로가 다르면 다를 수 있어 경고만)</summary>
    static void WarnProtocol(string stage)
    {
        var a = Path.Combine(stage, "Protocol.dll");
        var b = Path.Combine(stage, "Renderer", "VrcProtoRenderer_Data", "Managed", "VrcProto.Protocol.dll");
        if (File.Exists(b) && !File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b))) Console.WriteLine("  주의: 에디터와 렌더러의 Protocol.dll이 달라요 — 렌더러가 최신 빌드인지 확인하세요");
    }

    static void CopyDir(string from, string to, Func<string, bool>? keep = null)
    {
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException(from);
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
            if (keep == null || keep(Path.GetFileName(d))) CopyDir(d, Path.Combine(to, Path.GetFileName(d)), keep);
    }

    static int IndexOf(byte[] d, byte[] p, int start)
    {
        int last = d.Length - p.Length;
        for (int i = Array.IndexOf(d, p[0], start); i >= 0 && i <= last; i = Array.IndexOf(d, p[0], i + 1))
        {
            int k = 1;
            while (k < p.Length && d[i + k] == p[k]) k++;
            if (k == p.Length) return i;
        }
        return -1;
    }
}
