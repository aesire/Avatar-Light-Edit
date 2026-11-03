// 소품·아바타 가져오기 (2026-11-01): 패키지·zip·프리팹·씬·FBX → IR. 에디터 가져오기 창(AvHost/ImportWindow)과 같은 importsim 경로, 같은 캐시 폴더.
//   dotnet bin/Shoot.dll import <파일> [--list] [--root 이름]
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ImportSim;
using VrcProto.Protocol;

static partial class Shoot
{
    static int Import(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("가져올 파일을 적어 주세요: import <.unitypackage·.zip·.prefab·.unity·.fbx> [--list] [--root 이름]");
        var file = Path.GetFullPath(a[1]);
        if (!File.Exists(file)) throw new IOException("파일이 없어요: " + file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var want = Opt(a, "--root");
        Timing.Start();
        var sources = new[] { file };
        if (ext == ".zip")
        {
            var dir = Path.Combine(TempDir, "zip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ZipFile.ExtractToDirectory(file, dir);
            sources = Directory.GetFiles(dir, "*.unitypackage", SearchOption.AllDirectories);
            if (sources.Length == 0) throw new InvalidDataException("zip 안에 .unitypackage가 없어요");
        }
        bool package = ext == ".zip" || ext == ".unitypackage";

        UnityImport imp;
        List<(string Name, bool Avatar, int Renderers, Func<(SimFile File, long Root)> Pick, string Guid)> roots;
        if (package)
        {
            imp = UnityImport.Open(sources);
            var i2 = imp;
            roots = imp.PackagePrefabs(true).OrderByDescending(x => x.IsAvatar).ThenByDescending(x => x.Renderers > 0)
                .Select(x => (x.Name, x.IsAvatar, x.Renderers, (Func<(SimFile, long)>)(() => { var f = i2.ResolveGuid(x.Guid); return (f, f.Root?.Id ?? throw new InvalidDataException("프리팹 루트가 없어요")); }), x.Guid)).ToList();
        }
        else
        {
            var assets = UnityImport.FindAssetsDir(file);
            bool loose = UnityImport.IsLooseModel(file);
            if (assets == null && !loose) throw new InvalidDataException("이 파일은 Unity 프로젝트의 Assets 폴더 안에 있지 않아요");
            imp = loose ? UnityImport.Open(new[] { file }) : UnityImport.Open(assets!);
            var scene = imp.Resolve(file);
            var guid = imp.Db.ByGuid.Values.FirstOrDefault(x => x.FilePath != null && string.Equals(x.FilePath, file, StringComparison.OrdinalIgnoreCase))?.Guid ?? "";
            roots = imp.Roots(scene).OrderByDescending(r => r.IsAvatar && r.Active).ThenByDescending(r => r.Renderers)
                .Select(r => (r.Name, r.IsAvatar, r.Renderers, (Func<(SimFile, long)>)(() => (scene, r.Id)), guid)).ToList();
        }
        if (roots.Count == 0) throw new InvalidDataException("가져올 프리팹·모델이 없어요");
        if (a.Contains("--list"))
        {
            var arr = JNode.Arr();
            foreach (var r in roots) arr.Add(JNode.Obj().Set("name", r.Name).Set("avatar", r.Avatar).Set("renderers", r.Renderers));
            Console.WriteLine(JNode.Obj().Set("ok", true).Set("roots", arr).ToJson(true));
            return 0;
        }
        var root = want == null ? roots[0] : roots.FirstOrDefault(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase));
        if (root.Name == null) throw new InvalidDataException($"--root를 찾지 못했어요: {want}. 목록은 --list");
        var (sim, id) = root.Pick();
        var baseName = package ? root.Name : Path.GetFileNameWithoutExtension(file);
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports", baseName + (root.Guid.Length >= 8 ? "_" + root.Guid.Substring(0, 8) : ""));
        var (path, warns, _) = imp.Export(sim, id, outDir, null, false);
        var w = JNode.Arr(); foreach (var x in warns.Take(10)) w.Add(x);
        Console.WriteLine(JNode.Obj().Set("ok", true).Set("root", root.Name).Set("path", path).Set("warnings", w).ToJson(true));
        return 0;
    }
}
