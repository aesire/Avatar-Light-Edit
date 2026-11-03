// 에디터에서 쓰는 가져오기 API: Unity 프로젝트의 씬(.unity)·프리팹, 또는 .unitypackage 안의 프리팹을 통째로 해석하고,
// 그중 루트 오브젝트 하나를 골라 IR v1로 낸다. CLI의 --scene / --root / --ir-out 과 같은 경로를 쓴다.
//   var imp = UnityImport.Open(UnityImport.FindAssetsDir(path)!);     // 또는 Open(new[]{ "a.unitypackage", ... })
//   var file = imp.Resolve(path);              // 씬 전체 (PrefabInstance 펼치기 + 씬 오버라이드). 패키지는 ResolveGuid(guid)
//   var roots = imp.Roots(file);               // 고를 수 있는 루트 목록
//   var ir = imp.Export(file, roots[i].Id, outDir, outfits);   // outfits: 아바타 아래에 넣을 의상 프리팹 guid
// 내보낼 때 의상을 넣고(Resolver.Compose) 의상 본을 합친다(OutfitMerge). 다시 만들 수 있도록 재료(sources·root·outfits)를 IR source에 남긴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed class UnityImport
    {
        /// VRChat SDK3 VRCAvatarDescriptor 스크립트 참조 (루트 목록에서 아바타 표시용)
        const long AvatarDescriptorFileId = 542108242;
        const string AvatarDescriptorGuid = "67cc4cb7839cd3741b63733d5adf0442";

        /// <summary>프로젝트 Assets 폴더 (패키지만으로 열었으면 null)</summary>
        public readonly string? AssetsDir;
        /// <summary>색인한 소스 (Assets 폴더 · .unitypackage, 뒤에 준 것이 우선)</summary>
        public readonly List<string> Sources = new List<string>();
        public readonly AssetDb Db = new AssetDb();
        readonly Resolver resolver;
        readonly Dictionary<string, AssetEntry> byFile = new Dictionary<string, AssetEntry>(StringComparer.OrdinalIgnoreCase);

        public sealed class RootInfo
        {
            public string Name = "";
            public long Id;                 // 씬(또는 프리팹) 안의 GameObject fileID
            public bool Active, IsAvatar;
            public int Nodes, Renderers;
        }

        public sealed class PrefabInfo
        {
            public string Guid = "", Path = "", Name = "", Source = "";
            public bool IsAvatar, HasMergeArmature;
            public int Nodes, Renderers;
        }

        UnityImport(IEnumerable<string> sources)
        {
            foreach (var src in sources)
            {
                var full = Path.GetFullPath(src);
                if (Directory.Exists(full)) { Db.AddProjectFolder(full); AssetsDir ??= full; }
                else if (File.Exists(full) && IsLooseModel(full)) Db.AddLooseModel(full);
                else if (File.Exists(full)) Db.AddPackage(full);
                else throw new FileNotFoundException("가져올 소스가 없어요", full);
                Sources.Add(full);
            }
            foreach (var a in Db.ByGuid.Values) if (a.FilePath != null) byFile[a.FilePath] = a;
            resolver = new Resolver(Db);
            Timing.Step("색인");
        }

        /// <summary>Assets 폴더를 읽기 전용으로 색인한다 (큰 프로젝트는 처음에 수십 초 걸린다).</summary>
        public static UnityImport Open(string assetsDir) => new UnityImport(new[] { assetsDir });

        /// <summary>Assets 폴더와 .unitypackage 를 함께 색인한다 (뒤에 준 것이 같은 guid를 덮어쓴다). 패키지 안 스크립트는 실행하지 않고 데이터로만 읽는다.</summary>
        public static UnityImport Open(IEnumerable<string> sources) => new UnityImport(sources);

        /// <summary>guid로 프리팹·씬을 해석한다 (패키지 소스).</summary>
        public SimFile ResolveGuid(string guid) =>
            resolver.Resolve(guid) ?? throw new InvalidDataException("해석하지 못했어요: " + (Db.Get(guid)?.Path ?? guid));

        /// <summary>
        /// 패키지에 들어 있는 프리팹 목록 (폴더 소스는 의존성 용도라 뺀다). 하나씩 해석해 아바타·MA 의상 여부를 본다.
        /// models: FBX 모델도 넣는다 (스튜디오 소품 — 소품 패키지는 프리팹 없이 FBX만 든 것이 많다).
        /// </summary>
        public List<PrefabInfo> PackagePrefabs(bool models = false)
        {
            var list = new List<PrefabInfo>();
            foreach (var a in Db.ByGuid.Values.Where(a => (a.Ext == ".prefab" || models && a.Ext == ".fbx") && a.FilePath == null).OrderBy(a => a.Path, StringComparer.Ordinal))
            {
                var info = new PrefabInfo { Guid = a.Guid, Path = a.Path, Name = a.Name, Source = a.Source };
                var f = resolver.Resolve(a.Guid);
                if (f?.Root is SimObject root)
                {
                    var r = new RootInfo();
                    Walk(f, root, r, true);
                    info.IsAvatar = r.IsAvatar; info.Nodes = r.Nodes; info.Renderers = r.Renderers;
                    info.HasMergeArmature = f.Objects.Values.Any(o => o.ClassId == 114 && o.Props.ContainsKey("mergeTarget") && o.Props.ContainsKey("prefix"));
                }
                list.Add(info);
            }
            return list;
        }

        /// <summary>아이템을 IR로 쓰지 않고 넣어 보기만 한다: 종류(의상·헤어·악세서리)·붙는 곳·짝 찾은 본 수·위치 차이. 붙일 곳을 모르면 NeedsPlacement.</summary>
        public OutfitMerge.Report? TryOutfit(SimFile f, long rootId, OutfitMerge.Spec spec)
        {
            var composed = resolver.Compose(f, rootId, new[] { spec.Prefab });
            return OutfitMerge.Apply(composed, composed.Get(rootId)!, Db, new[] { spec }).FirstOrDefault(r => r.AddedFrom == spec.Prefab);
        }

        /// <summary>아바타의 휴머노이드 뼈 이름 (수동으로 붙일 곳을 고를 때)</summary>
        public List<string> HumanBones(SimFile f, long rootId) =>
            f.Get(rootId) is SimObject root ? Humanoid.AvatarBones(Db, f, root).Keys.ToList() : new List<string>();

        /// <summary>IR source.outfits 항목 ↔ Spec</summary>
        public static JNode SpecJson(OutfitMerge.Spec s)
        {
            var o = JNode.Obj().Set("prefab", s.Prefab).Set("kind", s.Kind).Set("target", s.Target).Set("keepWorld", s.KeepWorld);
            // 배율 1은 적지 않는다: 예전 레시피와 조합 해시(IR 파일 이름)가 같게
            if (Math.Abs(s.Scale - 1) > 1e-6) o.Set("scale", (float)s.Scale);
            if (s.Offset != null) { var a = JNode.Arr(); foreach (var v in s.Offset) a.Add(v); o.Set("offset", a); }
            return o;
        }

        public static OutfitMerge.Spec SpecFrom(JNode j)
        {
            if (j.Kind == JKind.String) return new OutfitMerge.Spec { Prefab = j.AsString };
            var s = new OutfitMerge.Spec { Prefab = j["prefab"].AsString, Kind = j.Has("kind") ? j["kind"].AsString : "auto", Target = j["target"].AsStringOrNull ?? "", KeepWorld = !j.Has("keepWorld") || j["keepWorld"].AsBool, Scale = j.Has("scale") ? Math.Clamp(j["scale"].AsDouble, 0.01, 100) : 1 };
            if (j["offset"].Count >= 10) s.Offset = j["offset"].Items.Select(x => x.AsDouble).ToArray();
            return s;
        }

        /// <summary>파일이 들어 있는 Unity 프로젝트의 Assets 폴더 (없으면 null).</summary>
        public static string? FindAssetsDir(string file)
        {
            for (var d = Path.GetDirectoryName(Path.GetFullPath(file)); d != null; d = Path.GetDirectoryName(d))
                if (string.Equals(Path.GetFileName(d), "Assets", StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>Unity 프로젝트 밖의 FBX (.meta 없음) — 기본 임포트 설정으로 소품처럼 가져온다.</summary>
        public static bool IsLooseModel(string file) =>
            file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && FindAssetsDir(file) == null;

        public static bool CanImport(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            return ext == ".unity" || ext == ".prefab" || ext == ".unitypackage";
        }

        /// <summary>씬·프리팹 파일을 해석한다. 경고는 SimFile.Warnings.</summary>
        public SimFile Resolve(string file)
        {
            if (!byFile.TryGetValue(Path.GetFullPath(file), out var a))
                throw new FileNotFoundException("프로젝트에서 이 파일의 .meta 를 찾지 못했어요", file);
            var r = resolver.Resolve(a.Guid) ?? throw new InvalidDataException("해석하지 못했어요: " + a.Path);
            Timing.Step("씬·프리팹 해석");
            return r;
        }

        public List<RootInfo> Roots(SimFile f)
        {
            var list = new List<RootInfo>();
            foreach (var go in f.Roots)
            {
                var info = new RootInfo { Name = Name(go), Id = go.Id, Active = S(go.Props, "m_IsActive") != "0" };
                Walk(f, go, info, true);
                list.Add(info);
            }
            return list;
        }

        static void Walk(SimFile f, SimObject go, RootInfo info, bool isRoot)
        {
            info.Nodes++;
            SimObject? tr = null;
            foreach (var c in L(go.Props, "m_Component"))
            {
                if (f.Get(Ref.FileId((c as Map)?.GetValueOrDefault("component"))) is not SimObject comp) continue;
                if (comp.ClassId == 4 || comp.ClassId == 224) tr = comp;
                else if (comp.ClassId == 137 || comp.ClassId == 23) info.Renderers++;
                else if (isRoot && comp.ClassId == 114 && comp.Props.GetValueOrDefault("m_Script") is var s
                         && Ref.FileId(s) == AvatarDescriptorFileId && Ref.Guid(s) == AvatarDescriptorGuid) info.IsAvatar = true;
            }
            if (tr == null) return;
            foreach (var ch in L(tr.Props, "m_Children"))
                if (f.Get(Ref.FileId(ch)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                    Walk(f, cg, info, false);
        }

        /// <summary>
        /// 루트 하나를 IR로 쓴다. outfits: 루트 아래에 넣을 의상 프리팹 guid (Unity에서 아바타 아래로 끌어다 놓은 것처럼).
        /// 의상이 있으면 &lt;이름&gt;+&lt;조합 해시&gt;.ir.json 으로 따로 쓰고, 패키지 텍스처 폴더(&lt;이름&gt;.textures)는 나눠 쓴다.
        /// 반환: .ir.json 경로, 이 루트에 대한 경고 (씬 전체 경고는 SimFile.Warnings), 합친 의상 보고.
        /// </summary>
        public (string Path, List<string> Warnings, List<OutfitMerge.Report> Outfits) Export(SimFile f, long rootId, string outDir, IList<OutfitMerge.Spec>? specs = null, bool includeColorVariants = false)
        {
            if (f.Get(rootId) == null) throw new ArgumentException("루트 GameObject 없음: " + rootId);
            specs ??= new List<OutfitMerge.Spec>();
            var outfits = specs.Select(s => s.Prefab).ToList();
            var composed = resolver.Compose(f, rootId, outfits);
            Timing.Step("의상 넣기");
            var root = composed.Get(rootId)!;
            var reports = OutfitMerge.Apply(composed, root, Db, specs);
            Timing.Step("본 합치기");
            var name = string.Concat(Name(root).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (name.Length == 0) name = "root";
            var ex = new IrExport(Db) { OutfitReports = reports, TextureDir = name + ".textures", IncludeColorVariants = includeColorVariants };
            var srcs = JNode.Arr(); foreach (var s in Sources) srcs.Add(s);
            var outs = JNode.Arr(); foreach (var s in specs) outs.Add(SpecJson(s));
            ex.SourceExtra = JNode.Obj().Set("sources", srcs).Set("root", rootId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Set("outfits", outs)
                .Set("includeColorVariants", includeColorVariants);
            var irName = specs.Count == 0 ? name : name + "+" + Xxh64.Hash(System.Text.Encoding.UTF8.GetBytes(outs.ToJson())).ToString("x16").Substring(0, 8);
            var path = ex.Write(composed, outDir, irName, root, project: AssetsDir);
            Db.ClearGeometry();   // 기하는 크다: IR을 다 쓰면 놓는다 (다시 만들 때는 다시 해석)
            var warns = new List<string>(ex.Warnings);
            foreach (var w in composed.Warnings) if (!f.Warnings.Contains(w)) warns.Add(w);
            return (path, warns, reports);
        }

        /// <summary>
        /// 셰이더 GUID → 프로젝트 안의 원본 .shader 파일과, 셰이더 팩을 만들 때 함께 가져갈 폴더(인클루드를 찾는 범위).
        /// Unity가 실제로 쓰는 곳을 모두 본다: Assets, Packages(로컬·VPM 패키지), Library/PackageCache(레지스트리 패키지).
        /// 폴더 범위: Assets 아래는 최상위 폴더(Assets/&lt;이름&gt;), 패키지는 패키지 폴더. 읽기만 한다.
        /// </summary>
        public static Dictionary<string, (string Shader, string Root)> FindShaderSources(string assetsDir, IEnumerable<string> shaderGuids)
        {
            var want = new HashSet<string>(shaderGuids.Where(g => g.Length == 32));
            var found = new Dictionary<string, (string, string)>();
            if (want.Count == 0) return found;
            var project = Path.GetDirectoryName(Path.GetFullPath(assetsDir).TrimEnd('\\', '/'))!;
            var roots = new[] { Path.GetFullPath(assetsDir), Path.Combine(project, "Packages"), Path.Combine(project, "Library", "PackageCache") };
            foreach (var dir in roots)
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> metas;
                try { metas = Directory.EnumerateFiles(dir, "*.shader.meta", SearchOption.AllDirectories); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var meta in metas)
                {
                    var guid = MetaGuid(meta);
                    if (guid == null || !want.Contains(guid) || found.ContainsKey(guid)) continue;
                    var shader = meta.Substring(0, meta.Length - 5);
                    if (!File.Exists(shader)) continue;
                    // 범위: dir 바로 아래 첫 폴더 (Assets/<이름> 또는 패키지 폴더)
                    var rel = shader.Substring(dir.Length).TrimStart('\\', '/');
                    var first = rel.Split('\\', '/')[0];
                    found[guid] = (shader, rel.Contains('\\') || rel.Contains('/') ? Path.Combine(dir, first) : dir);
                    if (found.Count == want.Count) return found;
                }
            }
            return found;
        }

        static string? MetaGuid(string meta)
        {
            try
            {
                using var fs = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256);
                var buf = new byte[256];
                var head = System.Text.Encoding.UTF8.GetString(buf, 0, fs.Read(buf, 0, buf.Length));
                var i = head.IndexOf("guid: ", StringComparison.Ordinal);
                return i >= 0 && i + 38 <= head.Length ? head.Substring(i + 6, 32) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        static string Name(SimObject go) => S(go.Props, "m_Name");
        static string S(Map m, string k) => m.GetValueOrDefault(k) as string ?? "";
        static List<object?> L(Map m, string k) => m.GetValueOrDefault(k) as List<object?> ?? new List<object?>();
    }
}
