// 아바타에 들어 있는 기믹을 대략 찾는다 → IR 선택 항목 gimmicks (에디터 아바타 탭 "설치된 기믹 보기", IR v1 밖이라 Raw에 남는다).
// 재현이 아니라 "무엇이 들어 있는지" 목록만:
//   prefabs  아바타 안에 넣은 프리팹 (중첩은 맨 바깥 것만, FBX 모델 제외): 이름·에셋 경로·계층 경로, 안에 든 도구·컴포넌트, 메시가 있으면 의상·악세서리
//   tools    컴포넌트 스크립트의 출처로 묶은 도구 (Modular Avatar · VRCFury · AAO · lilycalInventory · VRChat SDK · 기타 패키지/폴더): 컴포넌트 종류별 개수
//            스크립트가 Packages에 있으면 Assets 색인에 없으므로 <프로젝트>/Packages/**/*.cs.meta만 따로 훑어 guid → 경로를 찾는다
//   maMenu   Modular Avatar 메뉴 항목(ModularAvatarMenuItem)이 붙은 오브젝트 이름 · vrcFury  VRCFury 기능 종류(Toggle 등)
//   menu     디스크립터 기능 메뉴의 항목 (하위 메뉴는 "메뉴/항목", 2단계까지) · fxLayers  FX 컨트롤러 레이어 이름 · params  기능 파라미터 수
// 빌드할 때 도구가 만드는 메뉴·레이어는 여기 없다 (그래서 "추정").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        static readonly Regex MetaGuidRe = new Regex(@"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
        static readonly Regex PackageNameRe = new Regex(@"""displayName""\s*:\s*""([^""]+)""");

        /// <summary>Packages 폴더 스크립트 guid → (Packages 기준 경로, 패키지 표시 이름). 기믹 찾기에서만 쓴다.</summary>
        Dictionary<string, (string Path, string Package)>? _packageScripts;

        Dictionary<string, (string Path, string Package)> PackageScripts(string? project)
        {
            if (_packageScripts != null) return _packageScripts;
            var map = new Dictionary<string, (string, string)>();
            _packageScripts = map;
            try
            {
                if (project == null) return map;
                var root = Path.GetDirectoryName(Path.GetFullPath(project).TrimEnd('\\', '/'));
                var pkgs = root == null ? null : Path.Combine(root, "Packages");
                if (pkgs == null || !Directory.Exists(pkgs)) return map;
                foreach (var dir in Directory.EnumerateDirectories(pkgs))
                {
                    string display = Path.GetFileName(dir);
                    var pj = Path.Combine(dir, "package.json");
                    if (File.Exists(pj) && PackageNameRe.Match(File.ReadAllText(pj)) is { Success: true } m) display = m.Groups[1].Value;
                    // 소스 스크립트와 DLL (VRChat SDK 컴포넌트는 DLL 안에 있다: 스크립트 참조 = DLL guid + 클래스 fileID)
                    foreach (var meta in Directory.EnumerateFiles(dir, "*.meta", SearchOption.AllDirectories))
                    {
                        if (!meta.EndsWith(".cs.meta", StringComparison.OrdinalIgnoreCase) && !meta.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase)) continue;
                        string head;
                        using (var r = new StreamReader(meta)) { var buf = new char[400]; head = new string(buf, 0, r.Read(buf, 0, buf.Length)); }
                        var g = MetaGuidRe.Match(head);
                        if (!g.Success) continue;
                        var rel = "Packages/" + Path.GetFileName(dir) + "/" + meta.Substring(dir.Length + 1, meta.Length - dir.Length - 1 - ".meta".Length).Replace('\\', '/');
                        map[g.Groups[1].Value] = (rel, display);
                    }
                }
            }
            catch (Exception e) { Warnings.Add("Packages 스크립트 색인 실패: " + e.Message); }
            return map;
        }

        /// <summary>컴포넌트 스크립트 → (도구 이름, 컴포넌트 이름). 스크립트를 못 찾으면 도구 "알 수 없는 스크립트".</summary>
        (string Tool, string Comp) ScriptInfo(string guid, string? project)
        {
            string? path = null, package = null;
            if (db.Get(guid) is AssetEntry a && (a.Ext == ".cs" || a.Ext == ".dll")) path = a.Path;
            else if (PackageScripts(project).TryGetValue(guid, out var p)) { path = p.Path; package = p.Package; }
            if (path == null) return ("알 수 없는 스크립트", "스크립트 " + guid.Substring(0, 8));
            var name = Path.GetFileNameWithoutExtension(path);
            var low = path.ToLowerInvariant();
            if (low.Contains("nadena.dev.modular-avatar") || name.StartsWith("ModularAvatar", StringComparison.Ordinal))
                return ("Modular Avatar", name.StartsWith("ModularAvatar", StringComparison.Ordinal) ? "MA " + name.Substring("ModularAvatar".Length) : name);
            if (low.Contains("vrcfury")) return ("VRCFury", name);
            if (low.Contains("avatar-optimizer") || low.Contains("anatawa12")) return ("Avatar Optimizer (AAO)", name);
            if (low.Contains("lilycalinventory")) return ("lilycalInventory", name);
            if (low.Contains("com.vrchat.") || name.StartsWith("VRC", StringComparison.Ordinal)) return ("VRChat SDK", VrcDllNames.TryGetValue(name, out var vn) ? vn : name);
            if (package != null) return (package, name);
            // Assets 안 스크립트: Assets 바로 아래 폴더 이름을 출처로
            var parts = path.Split('/');
            return (parts.Length > 2 ? parts[1] : "Assets", name);
        }

        JNode GimmicksJson(SimFile f, SimObject root, List<SimObject> nodeGos, string? project)
        {
            var json = JNode.Obj();
            try
            {
                // 계층: GameObject → 부모 GameObject
                var parentOf = new Dictionary<long, long>();
                foreach (var go in nodeGos)
                {
                    var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
                    if (tr != null && f.Get(Ref.FileId(tr.Props.GetValueOrDefault("m_Father"))) is SimObject ptr && f.Get(Ref.Local(ptr.Props, "m_GameObject")) is SimObject pgo)
                        parentOf[go.Id] = pgo.Id;
                }
                string PathOf(long id)
                {
                    var parts = new List<string>();
                    for (int guard = 0; id != root.Id && guard < 256 && f.Get(id) is SimObject g; guard++)
                    {
                        parts.Add(Name(g));
                        if (!parentOf.TryGetValue(id, out id)) break;
                    }
                    parts.Reverse();
                    return string.Join("/", parts);
                }
                // 프리팹 인스턴스 루트 (FBX 모델과 아바타 루트 자신은 빼고)
                bool IsGimmickPrefab(long id) => id != root.Id && f.PrefabRoots.TryGetValue(id, out var g) && db.Get(g) is AssetEntry a && a.Ext == ".prefab";
                long OwnerPrefab(long id)
                {
                    long top = 0;
                    for (int guard = 0; guard < 256 && id != root.Id; guard++)
                    {
                        if (IsGimmickPrefab(id)) top = id;   // 위로 올라가며 덮어써서 맨 바깥 것
                        if (!parentOf.TryGetValue(id, out id)) break;
                    }
                    return top;
                }

                var tools = new Dictionary<string, Dictionary<string, int>>();
                var prefabTools = new Dictionary<long, SortedSet<string>>();
                var prefabMeshes = new HashSet<long>();
                var prefabComps = new Dictionary<long, HashSet<string>>();
                var maMenu = new List<string>();
                var fury = new Dictionary<string, int>();
                foreach (var go in nodeGos)
                {
                    long owner = OwnerPrefab(go.Id);
                    foreach (var c in Components(f, go))
                    {
                        if (c.ClassId == 137 || c.ClassId == 33) { if (owner != 0) prefabMeshes.Add(owner); continue; }
                        if (c.ClassId != 114) continue;
                        var guid = Ref.Guid(c.Props.GetValueOrDefault("m_Script"));
                        if (string.IsNullOrEmpty(guid)) continue;
                        var (tool, comp) = ScriptInfo(guid!, project);
                        if (!tools.TryGetValue(tool, out var counts)) tools[tool] = counts = new Dictionary<string, int>();
                        counts[comp] = counts.TryGetValue(comp, out var n) ? n + 1 : 1;
                        if (owner != 0)
                        {
                            if (!prefabComps.TryGetValue(owner, out var cs)) prefabComps[owner] = cs = new HashSet<string>();
                            cs.Add(comp);
                        }
                        if (owner != 0 && tool != "VRChat SDK")
                        {
                            if (!prefabTools.TryGetValue(owner, out var set)) prefabTools[owner] = set = new SortedSet<string>();
                            set.Add(tool);
                        }
                        if (comp == "MA MenuItem" && maMenu.Count < 200) maMenu.Add(Name(go));
                        if (tool == "VRCFury")
                            foreach (var k in FuryFeatures(c.Props))
                            {
                                fury[k] = fury.TryGetValue(k, out var m) ? m + 1 : 1;
                                if (owner != 0) prefabComps[owner].Add("VRCFury " + k);
                            }
                    }
                }

                // 종류 (추정): 메시가 있고 몸에 맞추는 것(아마추어 합치기 · 몸 블렌드쉐이프 맞추기)이 있으면 의상·악세서리 (토글용 애니메이터가 있어도),
                // 아니면 애니메이터를 합치면(MA Merge Animator · VRCFury Full Controller) 기믹, 메시만 있으면 악세서리, 나머지는 기믹
                // (가져오기가 의상을 합치면 Merge Armature 컴포넌트는 이미 빠져 있다)
                string Kind(long id)
                {
                    var cs = prefabComps.TryGetValue(id, out var x) ? x : new HashSet<string>();
                    bool mesh = prefabMeshes.Contains(id);
                    if (mesh && (cs.Contains("MA MergeArmature") || cs.Contains("VRCFury ArmatureLink") || cs.Contains("MA BlendshapeSync") || cs.Contains("MA ShapeChanger"))) return "outfit";
                    if (cs.Contains("MA MergeAnimator") || cs.Contains("VRCFury FullController")) return "gimmick";
                    return mesh ? "outfit" : "gimmick";
                }
                // 같은 프리팹을 여러 곳에 넣었으면 한 줄로 (놓인 곳은 nodes에 모두)
                var byAsset = new Dictionary<string, JNode>();
                var prefabs = JNode.Arr();
                var seen = new HashSet<long>();
                foreach (var go in nodeGos)
                {
                    long id = OwnerPrefab(go.Id);
                    if (id == 0 || !seen.Add(id) || !(db.Get(f.PrefabRoots[id]) is AssetEntry a)) continue;
                    if (byAsset.TryGetValue(a.Guid, out var same)) { same["nodes"].Add(PathOf(id)); continue; }
                    var nodesJ = JNode.Arr(); nodesJ.Add(PathOf(id));
                    var p = JNode.Obj().Set("name", a.Name).Set("asset", a.Path).Set("nodes", nodesJ).Set("kind", Kind(id));
                    var tl = JNode.Arr(); if (prefabTools.TryGetValue(id, out var ts)) foreach (var t in ts) tl.Add(t);
                    p.Set("tools", tl);
                    var cl = JNode.Arr(); if (prefabComps.TryGetValue(id, out var pc)) foreach (var c in pc.OrderBy(x => x, StringComparer.Ordinal)) cl.Add(c);
                    p.Set("components", cl);
                    p.Set("meshes", prefabMeshes.Contains(id));
                    byAsset[a.Guid] = p;
                    prefabs.Add(p);
                }
                json.Set("prefabs", prefabs);

                var toolsJ = JNode.Arr();
                foreach (var kv in tools.OrderBy(k => ToolOrder(k.Key)).ThenByDescending(k => k.Value.Values.Sum()))
                {
                    var comps = JNode.Obj();
                    foreach (var c in kv.Value.OrderByDescending(x => x.Value)) comps.Set(c.Key, c.Value);
                    toolsJ.Add(JNode.Obj().Set("name", kv.Key).Set("count", kv.Value.Values.Sum()).Set("components", comps));
                }
                json.Set("tools", toolsJ);
                var mm = JNode.Arr(); foreach (var n in maMenu) mm.Add(n); json.Set("maMenu", mm);
                var fj = JNode.Obj(); foreach (var kv in fury.OrderByDescending(x => x.Value)) fj.Set(kv.Key, kv.Value); json.Set("vrcFury", fj);

                // 디스크립터: 기능 메뉴 · FX 레이어 · 파라미터
                var desc = Components(f, root).FirstOrDefault(c => c.ClassId == 114 && Ref.Guid(c.Props.GetValueOrDefault("m_Script")) == DescriptorGuid);
                if (desc != null)
                {
                    var menu = JNode.Arr();
                    if (db.Get(Ref.Guid(desc.Props.GetValueOrDefault("expressionsMenu")) ?? "") is AssetEntry ma) MenuNames(ma, "", menu, new HashSet<string>(), 0);
                    json.Set("menu", menu);
                    var layers = JNode.Arr();
                    foreach (var l in L(desc.Props, "baseAnimationLayers"))
                    {
                        if (!(l is Map lm) || S(lm, "type") != FxLayer.ToString(CultureInfo.InvariantCulture)) continue;
                        var ctrl = db.Get(Ref.Guid(lm.GetValueOrDefault("animatorController")) ?? "");
                        var c = ctrl == null ? null : ParseYaml(ctrl)?.FirstOrDefault(d => d.ClassId == 91);
                        if (c != null) foreach (var al in L(c.Body, "m_AnimatorLayers")) if (al is Map alm) layers.Add(S(alm, "m_Name"));
                    }
                    json.Set("fxLayers", layers);
                    if (db.Get(Ref.Guid(desc.Props.GetValueOrDefault("expressionParameters")) ?? "") is AssetEntry pa && ParseYaml(pa)?.FirstOrDefault(d => d.ClassId == 114) is YamlDoc pd)
                        json.Set("params", L(pd.Body, "parameters").OfType<Map>().Count(m => S(m, "name").Length > 0));
                }
            }
            catch (Exception e) { Warnings.Add("기믹을 찾지 못함: " + e.Message); }
            return json;
        }

        /// <summary>VRChat SDK DLL → 컴포넌트 묶음 이름 (DLL 안의 클래스는 fileID로만 갈려 이름을 따로 모른다)</summary>
        static readonly Dictionary<string, string> VrcDllNames = new Dictionary<string, string>
        {
            ["VRCSDK3A"] = "Avatar Descriptor 등", ["VRC.SDK3.Dynamics.PhysBone"] = "PhysBone · 콜라이더", ["VRC.SDK3.Dynamics.Contact"] = "Contact (접촉)",
            ["VRC.SDK3.Dynamics.Constraint"] = "VRC Constraint", ["VRCCore-Editor"] = "Pipeline Manager",
        };

        /// <summary>
        /// 기믹 찾기가 생기기 전에 가져온 아바타(IR에 gimmicks 없음)용: 원본 프로젝트·패키지에서 장면·프리팹만 다시 해석해 기믹만 찾는다 (에디터 아바타 탭).
        /// 메시·텍스처는 읽지 않는다. 붙인 의상(가져오기 outfits)은 빠진다.
        /// </summary>
        public static JNode Scan(IEnumerable<string> sources, string sourceGuid, string? rootId, string? project)
        {
            var db = new AssetDb();
            foreach (var s in sources)
            {
                if (Directory.Exists(s)) db.AddProjectFolder(s);
                else if (File.Exists(s)) db.AddPackage(s);
            }
            var f = new Resolver(db).Resolve(sourceGuid) ?? throw new InvalidOperationException("원본 장면·프리팹을 찾지 못했어요");
            SimObject? root = null;
            if (!string.IsNullOrEmpty(rootId) && long.TryParse(rootId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid)) root = f.Get(rid);
            root ??= f.Root ?? throw new InvalidOperationException("아바타 루트를 찾지 못했어요");
            var gos = new List<SimObject>();
            CollectGameObjects(f, root, gos, 0);
            return new IrExport(db).GimmicksJson(f, root, gos, project);
        }

        static void CollectGameObjects(SimFile f, SimObject go, List<SimObject> outList, int depth)
        {
            outList.Add(go);
            var tr = Components(f, go).FirstOrDefault(c => c.ClassId == 4 || c.ClassId == 224);
            if (tr == null || depth > 256) return;
            foreach (var ch in L(tr.Props, "m_Children"))
                if (f.Get(Ref.FileId(ch)) is SimObject ctr && f.Get(Ref.Local(ctr.Props, "m_GameObject")) is SimObject cgo)
                    CollectGameObjects(f, cgo, outList, depth + 1);
        }

        static int ToolOrder(string tool) => tool switch
        {
            "Modular Avatar" => 0, "VRCFury" => 1, "Avatar Optimizer (AAO)" => 2, "lilycalInventory" => 3, "VRChat SDK" => 9, "알 수 없는 스크립트" => 10, _ => 5,
        };

        /// <summary>VRCFury 컴포넌트가 가진 기능 종류 (직렬화된 managed reference의 class 이름: Toggle, FullController, ArmatureLink…).</summary>
        static IEnumerable<string> FuryFeatures(Map props)
        {
            if (!(props.GetValueOrDefault("references") is Map refs)) yield break;
            foreach (var r in L(refs, "RefIds"))
                if (r is Map rm && rm.GetValueOrDefault("type") is Map t && S(t, "class") is var cls && cls.Length > 0 && S(t, "ns").StartsWith("VF.Model.Feature", StringComparison.Ordinal))
                    yield return cls;
        }

        /// <summary>기능 메뉴 항목 이름 (하위 메뉴는 "메뉴/항목", 2단계까지).</summary>
        void MenuNames(AssetEntry menu, string prefix, JNode outArr, HashSet<string> seen, int depth)
        {
            if (depth > 1 || !seen.Add(menu.Guid) || outArr.Count > 300) return;
            var doc = ParseYaml(menu)?.FirstOrDefault(d => d.ClassId == 114);
            if (doc == null) return;
            foreach (var c in L(doc.Body, "controls"))
            {
                if (!(c is Map cm)) continue;
                var name = S(cm, "name");
                var path = prefix.Length > 0 ? prefix + "/" + name : name;
                outArr.Add(path);
                if (S(cm, "type") == "103" && db.Get(Ref.Guid(cm.GetValueOrDefault("subMenu")) ?? "") is AssetEntry sub)
                    MenuNames(sub, path, outArr, seen, depth + 1);
            }
        }
    }
}
