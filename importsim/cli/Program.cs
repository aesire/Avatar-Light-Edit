using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ImportSim;

static class Program
{
    const string Usage = @"importsim — Unity 없이 .unitypackage / Assets 폴더를 Unity가 임포트한 것처럼 해석

사용법:
  importsim <소스...> [옵션]

소스 (여러 개 가능, 뒤에 준 것이 우선):
  *.unitypackage           패키지 파일
  <폴더>                   Unity 프로젝트의 Assets 폴더 (읽기 전용)

옵션:
  --list                   프리팹/FBX 목록만 출력
  --prefab <경로|guid>     이 프리팹만 해석 (여러 번 가능, 경로 일부만 써도 됨)
  --out <폴더>             프리팹마다 <이름>.json 트리 저장
  --only-from-packages     패키지에 들어 있는 프리팹만 대상 (폴더 소스는 의존성 용도)
  --geometry-out <폴더>    FBX 마다 Transform 값 + 메시 데이터를 <guid>.json 으로 저장
  --model <경로|guid>      --geometry-out 대상 FBX 를 제한 (여러 번 가능, 경로 일부만 써도 됨)
  --ir-out <폴더>          --prefab 으로 고른 프리팹마다 에디터 IR v1 장면(<이름>.ir.json + .ir.bin) 저장
  --scene <경로|guid>      --ir-out 대상으로 씬(.unity)의 루트 오브젝트 하나를 쓴다 (씬 오버라이드 적용)
  --root <이름>            --scene 에서 내보낼 루트 오브젝트 (없으면 루트 목록만 출력)
  --outfit <경로|guid>     --ir-out 때 아바타 루트 아래에 넣을 의상 프리팹 (여러 번 가능). MA Merge Armature대로 본을 합친다
";

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help")) { Console.Write(Usage); return 1; }

        var sources = new List<string>();
        var prefabFilters = new List<string>();
        string? outDir = null, geometryOut = null, irOut = null, scene = null, sceneRoot = null;
        var modelFilters = new List<string>();
        var outfitFilters = new List<string>();
        bool list = false, onlyPackages = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list": list = true; break;
                case "--prefab": prefabFilters.Add(args[++i]); break;
                case "--out": outDir = args[++i]; break;
                case "--only-from-packages": onlyPackages = true; break;
                case "--geometry-out": geometryOut = args[++i]; break;
                case "--model": modelFilters.Add(args[++i]); break;
                case "--ir-out": irOut = args[++i]; break;
                case "--scene": scene = args[++i]; break;
                case "--root": sceneRoot = args[++i]; break;
                case "--outfit": outfitFilters.Add(args[++i]); break;
                default: sources.Add(args[i]); break;
            }
        }

        if (irOut != null && prefabFilters.Count == 0 && scene == null) { Console.Error.WriteLine("--ir-out 에는 --prefab 이 필요함"); return 1; }

        var sw = Stopwatch.StartNew();
        Timing.Start();
        var db = new AssetDb();
        var packageGuids = new HashSet<string>();
        foreach (var s in sources)
        {
            if (Directory.Exists(s)) db.AddProjectFolder(s);
            else if (File.Exists(s))
            {
                var before = new HashSet<string>(db.ByGuid.Keys);
                db.AddPackage(s);
                foreach (var g in db.ByGuid.Keys) if (!before.Contains(g) || db.ByGuid[g].Source == s) packageGuids.Add(g);
            }
            else { Console.Error.WriteLine($"소스 없음: {s}"); return 2; }
        }
        Console.Error.WriteLine($"에셋 {db.ByGuid.Count}개 로드 ({sw.ElapsedMilliseconds} ms)");
        Timing.Step("색인");

        var prefabs = db.ByGuid.Values.Where(a => a.Ext == ".prefab")
            .Where(a => !onlyPackages || packageGuids.Contains(a.Guid))
            .Where(a => prefabFilters.Count == 0 || prefabFilters.Any(f => a.Guid == f || a.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(a => a.Path, StringComparer.Ordinal).ToList();

        if (list)
        {
            foreach (var a in db.ByGuid.Values.Where(a => a.Ext == ".fbx" || a.Ext == ".prefab").OrderBy(a => a.Path, StringComparer.Ordinal))
                Console.WriteLine($"{a.Guid}  {a.Path}");
            return 0;
        }

        if (geometryOut != null)
        {
            Directory.CreateDirectory(geometryOut);
            int n = 0;
            foreach (var a in db.ByGuid.Values.Where(a => a.Ext == ".fbx")
                         .Where(a => !onlyPackages || packageGuids.Contains(a.Guid))
                         .Where(a => modelFilters.Count == 0 || modelFilters.Any(f => a.Guid == f || a.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                try
                {
                    var m = ModelSim.Build(a, geometry: true);
                    File.WriteAllText(Path.Combine(geometryOut, a.Guid + ".json"), ExportGeometry.Json(m), new UTF8Encoding(false));
                    n++;
                }
                catch (Exception e) { Console.WriteLine($"FAIL {a.Path}: {e.GetType().Name}: {e.Message}"); }
            }
            Console.Error.WriteLine($"모델 {n}개 기하 출력 ({sw.ElapsedMilliseconds} ms)");
            if (outDir == null && prefabFilters.Count == 0) return 0;
        }

        var resolver = new Resolver(db);
        if (outDir != null) Directory.CreateDirectory(outDir);
        // 씬은 폴더 소스(Assets)에서 온다: 에디터가 셰이더 원본을 다시 찾을 수 있게 IR에 프로젝트 위치를 남긴다
        var project = sources.FirstOrDefault(Directory.Exists);
        var outfits = new List<string>();
        foreach (var o in outfitFilters)
        {
            var a = db.ByGuid.Values.Where(x => x.Ext == ".prefab" && (x.Guid == o || x.Path.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(x => x.Path.Length).FirstOrDefault();
            if (a == null) { Console.Error.WriteLine($"의상 프리팹 없음: {o}"); return 2; }
            outfits.Add(a.Guid);
        }
        if (scene != null) return WriteSceneIr(db, resolver, scene, sceneRoot, irOut, sw, project == null ? null : Path.GetFullPath(project), outfits);
        if (irOut != null) return WriteIr(db, resolver, prefabs, irOut, sw, outfits);
        int ok = 0, warned = 0;
        foreach (var a in prefabs)
        {
            var f = resolver.Resolve(a.Guid);
            if (f == null) continue;
            if (f.Root == null) f.Warnings.Insert(0, "루트를 찾지 못함");
            ok++;
            if (f.Warnings.Count > 0) warned++;
            Console.WriteLine($"{(f.Warnings.Count == 0 ? "OK  " : "WARN")} {a.Path}  (객체 {f.Objects.Count}, 경고 {f.Warnings.Count})");
            foreach (var w in f.Warnings.Take(3)) Console.WriteLine($"       - {w}");
            if (outDir != null)
                File.WriteAllText(Path.Combine(outDir, a.Guid + ".json"), Export.PrefabJson(f, db, resolver), new UTF8Encoding(false));
        }
        Console.Error.WriteLine($"프리팹 {ok}개 해석, 경고 있음 {warned}개 ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    /// 의상을 넣고(없어도 씬에 있던 MA 의상은) 본을 합친 뒤 IR로 쓴다
    static (string path, IrExport ex) ExportIr(AssetDb db, Resolver resolver, SimFile f, SimObject root, string irOut, string name, string? project, List<string> outfits)
    {
        var composed = resolver.Compose(f, root.Id, outfits);
        Timing.Step("의상 넣기");
        var reports = OutfitMerge.Apply(composed, composed.Get(root.Id)!, db);
        Timing.Step("본 합치기");
        var ex = new IrExport(db) { OutfitReports = reports };
        foreach (var r in reports)
            Console.WriteLine($"     {composed.Get(r.OutfitRoot)?.Props.GetValueOrDefault("m_Name")}: 종류 {r.Kind} ({r.Reason}), 방법 {r.Method}{(r.NeedsPlacement ? " · 붙일 곳 모름" : "")} → {r.Target}, 본 {r.Bones} 중 짝 {r.Matched} (완전히 합침 {r.Merged}), 위치 차이 평균 {r.MeanOffset * 100:0.00} cm · 최대 {r.MaxOffset * 100:0.00} cm");
        return (ex.Write(composed, irOut, name, composed.Get(root.Id), project), ex);
    }

    static int WriteSceneIr(AssetDb db, Resolver resolver, string filter, string? rootName, string? irOut, Stopwatch sw, string? project, List<string> outfits)
    {
        var a = db.ByGuid.Values.Where(x => x.Ext == ".unity" && (x.Guid == filter || x.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(x => x.Path.Length).FirstOrDefault();
        if (a == null) { Console.Error.WriteLine($"씬 없음: {filter}"); return 2; }
        var f = resolver.Resolve(a.Guid);
        if (f == null) { Console.Error.WriteLine($"씬 해석 실패: {a.Path}"); return 2; }
        Timing.Step("씬·프리팹 해석");
        string N(SimObject go) => go.Props.GetValueOrDefault("m_Name") as string ?? "";
        var roots = f.Roots.ToList();
        var root = rootName == null ? null : roots.FirstOrDefault(r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == rootName) ?? roots.FirstOrDefault(r => N(r) == rootName) ?? roots.FirstOrDefault(r => N(r).IndexOf(rootName, StringComparison.OrdinalIgnoreCase) >= 0);
        if (root == null || irOut == null)
        {
            Console.WriteLine($"{a.Path}: 루트 {roots.Count}개 (경고 {f.Warnings.Count})");
            foreach (var r in roots) Console.WriteLine($"  {N(r)}");
            return rootName == null ? 0 : 2;
        }
        var name = string.Concat(N(root).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var (path, ex) = ExportIr(db, resolver, f, root, irOut, name, project, outfits);
        var s = VrcProto.Protocol.Ir.IrScene.Load(path);
        // 씬 경고는 씬 전체에 대한 것이라 개수만 알린다
        Console.WriteLine($"{path}  (노드 {s.Nodes.Count}, 메시 {s.Meshes.Count}, 렌더러 {s.Renderers.Count}, 머티리얼 {s.Materials.Count}, 텍스처 {s.Textures.Count}, 경고 {ex.Warnings.Count}, 씬 경고 {f.Warnings.Count})");
        foreach (var w in ex.Warnings.Take(10)) Console.WriteLine($"       - {w}");
        Timing.Step("IR 다시 읽기(확인)");
        Console.Error.WriteLine($"({sw.ElapsedMilliseconds} ms)");
        if (Environment.GetEnvironmentVariable("VRCPROTO_IMPORT_TIMING") == "1") Console.Error.WriteLine(Timing.Report());
        return 0;
    }

    static int WriteIr(AssetDb db, Resolver resolver, List<AssetEntry> prefabs, string irOut, Stopwatch sw, List<string> outfits)
    {
        if (prefabs.Count == 0) { Console.Error.WriteLine("--prefab 에 맞는 프리팹 없음"); return 2; }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fail = 0;
        foreach (var a in prefabs)
        {
            var f = resolver.Resolve(a.Guid);
            if (f?.Root == null) { Console.WriteLine($"FAIL {a.Path}: 루트를 찾지 못함"); fail++; continue; }
            var name = string.Concat(a.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (!used.Add(name)) { name += "_" + a.Guid.Substring(0, 8); used.Add(name); }
            try
            {
                var (path, ex) = ExportIr(db, resolver, f, f.Root, irOut, name, null, outfits);
                // 에디터와 같은 정의로 다시 읽어 확인
                var s = VrcProto.Protocol.Ir.IrScene.Load(path);
                int warns = f.Warnings.Count + ex.Warnings.Count;
                Console.WriteLine($"{(warns == 0 ? "OK  " : "WARN")} {path}  (노드 {s.Nodes.Count}, 메시 {s.Meshes.Count}, 렌더러 {s.Renderers.Count}, 머티리얼 {s.Materials.Count}, 텍스처 {s.Textures.Count}, 경고 {warns})");
                foreach (var w in f.Warnings.Concat(ex.Warnings).Take(5)) Console.WriteLine($"       - {w}");
            }
            catch (Exception e) { Console.WriteLine($"FAIL {a.Path}: {e.GetType().Name}: {e.Message}"); fail++; }
        }
        Console.Error.WriteLine($"IR {prefabs.Count - fail}개 출력 ({sw.ElapsedMilliseconds} ms)");
        return fail == 0 ? 0 : 3;
    }
}
