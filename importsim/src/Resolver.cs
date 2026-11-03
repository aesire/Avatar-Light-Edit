using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ImportSim
{
    /// <summary>
    /// 에셋(guid)을 Unity가 로드했을 때의 객체 그래프로 만든다.
    /// 모델(FBX)은 ModelSim, 프리팹은 중첩 PrefabInstance 를 펼치고 m_Modifications 를 적용한다.
    /// </summary>
    public sealed class Resolver
    {
        const long Mask = long.MaxValue;   // 인스턴스 객체 fileID = (원본 fileID ^ PrefabInstance fileID) & 0x7FFF...
        readonly AssetDb db;
        readonly Dictionary<string, SimFile?> cache = new Dictionary<string, SimFile?>();
        readonly HashSet<string> resolving = new HashSet<string>();

        public Resolver(AssetDb db) { this.db = db; }

        /// <summary>메시 참조 -> 블렌드쉐이프 이름 (FBX 메시만, 모르면 null)</summary>
        public List<string>? BlendShapes(object? meshRef)
        {
            var g = Ref.Guid(meshRef);
            if (g == null || db.Get(g)?.Ext != ".fbx") return null;
            return Resolve(g)?.MeshBlendShapes.TryGetValue(Ref.FileId(meshRef), out var l) == true ? l : null;
        }

        public SimFile? Resolve(string guid)
        {
            if (cache.TryGetValue(guid, out var f)) return f;
            var a = db.Get(guid);
            if (a == null || !resolving.Add(guid)) return null;
            try
            {
                f = a.Ext switch
                {
                    ".fbx" => ModelSim.Build(a),
                    ".prefab" or ".unity" => ResolvePrefab(a),   // 씬도 같은 구조 (루트가 여러 개)
                    _ => null,
                };
            }
            catch (Exception e)
            {
                f = new SimFile { Guid = guid, Path = a.Path };
                f.Warnings.Add($"해석 실패: {e.GetType().Name}: {e.Message}");
            }
            finally { resolving.Remove(guid); }
            return cache[guid] = f;
        }

        SimFile ResolvePrefab(AssetEntry a)
        {
            var file = new SimFile { Guid = a.Guid, Path = a.Path };
            var docs = Yaml.ParseDocuments(Encoding.UTF8.GetString(a.Data ?? Array.Empty<byte>()));

            // stripped 객체 fileID -> 인스턴스 객체 fileID. 프리팹 파일에서는 XOR 규칙과 같아 바뀌는 것이 없고,
            // 씬은 stripped fileID 를 따로 매기므로 m_PrefabInstance / m_CorrespondingSourceObject 로 맞춘다
            var alias = new Dictionary<long, long>();
            foreach (var d in docs.Where(d => d.Stripped))
            {
                long pi = Ref.FileId(d.Body.GetValueOrDefault("m_PrefabInstance")), src = Ref.FileId(d.Body.GetValueOrDefault("m_CorrespondingSourceObject"));
                if (pi != 0 && src != 0 && ((src ^ pi) & Mask) != d.FileId) alias[d.FileId] = (src ^ pi) & Mask;
            }
            if (alias.Count > 0)
                foreach (var d in docs) d.Body = (Map)Yaml.DeepCopy(d.Body, id => alias.TryGetValue(id, out var x) ? x : id)!;

            // 1) 이 파일이 직접 가진 객체
            foreach (var d in docs)
                if (!d.Stripped && d.ClassId != 1001)
                    file.Objects[d.FileId] = new SimObject { Id = d.FileId, ClassId = d.ClassId, Type = d.Type, Props = d.Body };

            // 2) PrefabInstance 펼치기
            var added = new List<(long parentTr, int index, long child)>();
            var addedComps = new List<(long go, int index, long comp)>();
            foreach (var d in docs.Where(d => d.ClassId == 1001))
                Instantiate(file, d, added, addedComps);

            // 3) 부모-자식, GameObject-컴포넌트 관계를 이 파일 기준으로 다시 맞춘다
            Relink(file, added, addedComps);

            // 프리팹 에셋의 루트 이름은 항상 파일 이름
            if (a.Ext == ".prefab" && file.Root is SimObject root) root.Props["m_Name"] = a.Name;
            return file;
        }

        /// <summary>
        /// 이미 해석한 파일(프리팹·씬)을 복사하고, parentGo 아래에 프리팹들을 넣은 새 파일을 만든다.
        /// Unity 에디터에서 프리팹을 그 오브젝트 아래로 끌어다 놓은 것과 같다 (PrefabInstance, 오버라이드 없음).
        /// 넣은 객체는 AddedFrom[객체 fileID] = 프리팹 guid 로 표시한다. 인스턴스 fileID는 guid로 정하므로 넣는 순서·개수가 바뀌어도 같다.
        /// </summary>
        public SimFile Compose(SimFile baseFile, long parentGo, IEnumerable<string> prefabGuids)
        {
            var file = new SimFile { Guid = baseFile.Guid, Path = baseFile.Path };
            foreach (var o in baseFile.Objects.Values) file.Objects[o.Id] = o.Clone(x => x);
            file.Warnings.AddRange(baseFile.Warnings);
            foreach (var kv in baseFile.AddedFrom) file.AddedFrom[kv.Key] = kv.Value;
            foreach (var kv in baseFile.PrefabRoots) file.PrefabRoots[kv.Key] = kv.Value;
            var parentTr = file.Get(parentGo) is SimObject pg ? TransformOf(file, pg) : 0;
            if (parentTr == 0) throw new ArgumentException("의상을 넣을 오브젝트가 없음: " + parentGo);

            var added = new List<(long, int, long)>();
            var addedComps = new List<(long, int, long)>();
            foreach (var guid in prefabGuids)
            {
                var src = Resolve(guid);
                if (src == null) { file.Warnings.Add($"의상 프리팹 없음: {guid}"); continue; }
                long piId = (long)Xxh64.Hash(Encoding.ASCII.GetBytes("outfit:" + guid)) & Mask;
                while (src.Objects.Keys.Any(k => file.Objects.ContainsKey((k ^ piId) & Mask))) piId = (piId + 1) & Mask;
                var pi = new YamlDoc
                {
                    ClassId = 1001, FileId = piId, Type = "PrefabInstance",
                    Body = new Map
                    {
                        ["m_SourcePrefab"] = Ref.Make(100100000, guid, 3),
                        ["m_Modification"] = new Map { ["m_TransformParent"] = Ref.Make(parentTr), ["m_Modifications"] = new List<object?>() },
                    },
                };
                var before = new HashSet<long>(file.Objects.Keys);
                Instantiate(file, pi, added, addedComps);
                foreach (var k in file.Objects.Keys) if (!before.Contains(k)) file.AddedFrom[k] = guid;
            }
            Relink(file, added, addedComps);
            return file;
        }

        /// <summary>m_Father 를 바꾼 뒤 m_Children 을 다시 맞출 때 (의상 본 합치기).</summary>
        public static void Relink(SimFile file) => Relink(file, new List<(long, int, long)>(), new List<(long, int, long)>());

        void Instantiate(SimFile file, YamlDoc pi, List<(long, int, long)> added, List<(long, int, long)> addedComps)
        {
            long piId = pi.FileId;
            long Outer(long srcId) => (srcId ^ piId) & Mask;
            var mod = pi.Body.TryGetValue("m_Modification", out var mv) ? mv as Map ?? new Map() : new Map();
            var srcRef = pi.Body.TryGetValue("m_SourcePrefab", out var sp) ? sp : null;
            var srcGuid = Ref.Guid(srcRef);
            var src = srcGuid != null ? Resolve(srcGuid) : null;
            if (src == null)
            {
                file.Warnings.Add($"원본 에셋 없음: PrefabInstance {piId} -> {srcGuid} ({db.Get(srcGuid ?? "")?.Path ?? "패키지에 없음"})");
                AddMissingPlaceholder(file, piId, srcGuid ?? "", mod);
                return;
            }
            foreach (var w in src.Warnings) if (!file.Warnings.Contains(w)) file.Warnings.Add($"[{src.Path}] {w}");

            // 복제 (원본 파일 안의 참조는 바깥 fileID 로 변환)
            var root = src.Root;
            foreach (var o in src.Objects.Values)
            {
                var c = o.Clone(Outer);
                file.Objects[c.Id] = c;
            }
            // 어느 프리팹에서 왔는지: 안쪽 인스턴스는 그대로, 이 인스턴스의 루트는 지금 원본 (변형 프리팹이면 변형 쪽 이름)
            foreach (var kv in src.PrefabRoots) file.PrefabRoots[Outer(kv.Key)] = kv.Value;
            if (root != null && srcGuid != null) file.PrefabRoots[Outer(root.Id)] = srcGuid;

            // 루트를 부모 Transform 에 붙인다
            var parent = Ref.Local(mod, "m_TransformParent");
            if (root != null)
            {
                var rootTr = file.Get(Outer(TransformOf(src, root)));
                if (rootTr != null) rootTr.Props["m_Father"] = Ref.Make(parent);
            }

            // 오버라이드 적용
            if (mod.TryGetValue("m_Modifications", out var mods) && mods is List<object?> list)
                foreach (var item in list.OfType<Map>().OrderBy(m => (m.GetValueOrDefault("propertyPath") as string ?? "").EndsWith(".Array.size") ? 0 : 1))
                {
                    var target = item.TryGetValue("target", out var t) ? t : null;
                    var obj = file.Get(Outer(Ref.FileId(target)));
                    var path = item.TryGetValue("propertyPath", out var pp) ? pp as string ?? "" : "";
                    if (obj == null) { file.Warnings.Add($"대상 없는 오버라이드 (끊긴 참조): {Ref.Format(target)} {path}"); continue; }
                    item.TryGetValue("value", out var value);
                    item.TryGetValue("objectReference", out var objRef);
                    SetProperty(obj.Props, path, value as string ?? "", objRef as Map);
                }

            // 제거된 컴포넌트 / GameObject
            foreach (var r in List(mod, "m_RemovedComponents"))
            {
                var id = Outer(Ref.FileId(r));
                if (file.Get(id) is SimObject comp)
                {
                    var go = file.Get(Ref.Local(comp.Props, "m_GameObject"));
                    if (go != null) RemoveFromList(go.Props, "m_Component", e => Ref.FileId((e as Map)?["component"]) == id);
                    file.Objects.Remove(id);
                }
            }
            foreach (var r in List(mod, "m_RemovedGameObjects"))
                if (file.Get(Outer(Ref.FileId(r))) is SimObject go) RemoveGameObject(file, go);

            // 추가된 GameObject / 컴포넌트의 삽입 위치
            foreach (var e in List(mod, "m_AddedGameObjects").OfType<Map>())
                added.Add((Outer(Ref.FileId(e.GetValueOrDefault("targetCorrespondingSourceObject"))), Int(e.GetValueOrDefault("insertIndex")), Ref.FileId(e.GetValueOrDefault("addedObject"))));
            foreach (var e in List(mod, "m_AddedComponents").OfType<Map>())
                addedComps.Add((Outer(Ref.FileId(e.GetValueOrDefault("targetCorrespondingSourceObject"))), Int(e.GetValueOrDefault("insertIndex")), Ref.FileId(e.GetValueOrDefault("addedObject"))));
        }

        /// Unity 처럼 "이름 (Missing Prefab with guid: ...)" 빈 GameObject 를 둔다
        static void AddMissingPlaceholder(SimFile file, long piId, string srcGuid, Map mod)
        {
            string name = "Missing Prefab";
            foreach (var item in List(mod, "m_Modifications").OfType<Map>())
                if (item.GetValueOrDefault("propertyPath") as string == "m_Name") { name = item.GetValueOrDefault("value") as string ?? name; break; }
            long go = (piId ^ 0x5A5A5A5A5A5AL) & Mask, tr = (piId ^ 0x3C3C3C3C3C3CL) & Mask;
            file.Objects[go] = new SimObject
            {
                Id = go, ClassId = 1, Type = "GameObject",
                Props = new Map { ["m_Name"] = $"{name} (Missing Prefab with guid: {srcGuid})", ["m_IsActive"] = "1",
                                  ["m_Component"] = new List<object?> { new Map { ["component"] = Ref.Make(tr) } } },
            };
            file.Objects[tr] = new SimObject
            {
                Id = tr, ClassId = 4, Type = "Transform",
                Props = new Map
                {
                    ["m_GameObject"] = Ref.Make(go), ["m_Father"] = Ref.Make(Ref.Local(mod, "m_TransformParent")), ["m_Children"] = new List<object?>(),
                    ["m_LocalRotation"] = new Map { ["x"] = "0", ["y"] = "0", ["z"] = "0", ["w"] = "1" },
                    ["m_LocalPosition"] = new Map { ["x"] = "0", ["y"] = "0", ["z"] = "0" },
                    ["m_LocalScale"] = new Map { ["x"] = "1", ["y"] = "1", ["z"] = "1" },
                },
            };
            // 원본이 없어도 Transform 오버라이드(보통 인스턴스 루트 대상)는 자리표시에 적용된다
            foreach (var item in List(mod, "m_Modifications").OfType<Map>())
            {
                var path = item.GetValueOrDefault("propertyPath") as string ?? "";
                if (path.StartsWith("m_LocalPosition.") || path.StartsWith("m_LocalRotation.") || path.StartsWith("m_LocalScale."))
                    SetProperty(file.Objects[tr].Props, path, item.GetValueOrDefault("value") as string ?? "", null);
            }
        }

        static long TransformOf(SimFile f, SimObject go)
        {
            foreach (var c in go.Props.GetValueOrDefault("m_Component") as List<object?> ?? new List<object?>())
            {
                var id = Ref.FileId((c as Map)?.GetValueOrDefault("component"));
                if (f.Get(id) is SimObject o && (o.ClassId == 4 || o.ClassId == 224)) return id;
            }
            return 0;
        }

        static void RemoveGameObject(SimFile file, SimObject go)
        {
            var tr = file.Get(TransformOf(file, go));
            if (tr != null)
                foreach (var ch in tr.Props.GetValueOrDefault("m_Children") as List<object?> ?? new List<object?>())
                    if (file.Get(Ref.FileId(ch)) is SimObject ct && file.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                        RemoveGameObject(file, cg);
            foreach (var c in go.Props.GetValueOrDefault("m_Component") as List<object?> ?? new List<object?>())
                file.Objects.Remove(Ref.FileId((c as Map)?.GetValueOrDefault("component")));
            file.Objects.Remove(go.Id);
        }

        /// m_Father / m_GameObject 를 기준으로 m_Children / m_Component 를 다시 만든다.
        static void Relink(SimFile file, List<(long parentTr, int index, long child)> added, List<(long go, int index, long comp)> addedComps)
        {
            // Transform 자식
            var byParent = new Dictionary<long, List<long>>();
            foreach (var o in file.Objects.Values)
                if (o.ClassId == 4 || o.ClassId == 224)
                {
                    var p = Ref.Local(o.Props, "m_Father");
                    if (p != 0) { if (!byParent.TryGetValue(p, out var l)) byParent[p] = l = new List<long>(); l.Add(o.Id); }
                }
            foreach (var o in file.Objects.Values.Where(o => o.ClassId == 4 || o.ClassId == 224).ToList())
            {
                var kids = new List<long>();
                foreach (var c in o.Props.GetValueOrDefault("m_Children") as List<object?> ?? new List<object?>())
                {
                    var id = Ref.FileId(c);
                    if (file.Get(id) is SimObject ct && Ref.Local(ct.Props, "m_Father") == o.Id && !kids.Contains(id)) kids.Add(id);
                }
                if (byParent.TryGetValue(o.Id, out var actual))
                {
                    // m_AddedGameObjects 목록 순서대로 (insertIndex -1 = 뒤에 붙임), 그 밖의 것은 그 뒤에
                    foreach (var x in added)
                        if (x.parentTr == o.Id && actual.Contains(x.child) && !kids.Contains(x.child))
                        {
                            if (x.index >= 0 && x.index <= kids.Count) kids.Insert(x.index, x.child);
                            else kids.Add(x.child);
                        }
                    // 구버전 프리팹: 추가 위치 기록이 없으면 m_RootOrder 순으로 뒤에 붙는다
                    foreach (var id in actual.Where(id => !kids.Contains(id))
                                 .OrderBy(id => int.TryParse(file.Get(id)!.Props.GetValueOrDefault("m_RootOrder") as string, out var ro) ? ro : int.MaxValue))
                        kids.Add(id);
                }
                o.Props["m_Children"] = kids.Select(k => (object?)Ref.Make(k)).ToList();
            }

            // GameObject 컴포넌트
            var byGo = new Dictionary<long, List<long>>();
            foreach (var o in file.Objects.Values)
                if (o.ClassId != 1 && o.Props.ContainsKey("m_GameObject"))
                {
                    var g = Ref.Local(o.Props, "m_GameObject");
                    if (g != 0) { if (!byGo.TryGetValue(g, out var l)) byGo[g] = l = new List<long>(); l.Add(o.Id); }
                }
            foreach (var go in file.Objects.Values.Where(o => o.ClassId == 1).ToList())
            {
                var comps = new List<long>();
                foreach (var c in go.Props.GetValueOrDefault("m_Component") as List<object?> ?? new List<object?>())
                {
                    var id = Ref.FileId((c as Map)?.GetValueOrDefault("component"));
                    if (file.Get(id) != null && !comps.Contains(id)) comps.Add(id);
                }
                if (byGo.TryGetValue(go.Id, out var actual))
                {
                    foreach (var x in addedComps)
                        if (x.go == go.Id && actual.Contains(x.comp) && !comps.Contains(x.comp))
                        {
                            if (x.index >= 0 && x.index <= comps.Count) comps.Insert(x.index, x.comp);
                            else comps.Add(x.comp);
                        }
                    foreach (var id in actual) if (!comps.Contains(id)) comps.Add(id);
                }
                go.Props["m_Component"] = comps.Select(k => (object?)new Map { ["component"] = Ref.Make(k) }).ToList();
            }
        }

        // ---- propertyPath 적용 ----

        static readonly Regex DataIndex = new Regex(@"^data\[(\d+)\]$");

        /// "m_Materials.Array.data[1]", "m_LocalPosition.x", "m_BlendShapeWeights.Array.size" 등
        public static void SetProperty(Map props, string path, string value, Map? objRef)
        {
            var parts = path.Split('.');
            object? cur = props;
            for (int k = 0; k < parts.Length; k++)
            {
                bool last = k == parts.Length - 1;
                var part = parts[k];
                if (part == "Array" && cur is List<object?>) continue;
                if (cur is List<object?> list)
                {
                    if (part == "size")
                    {
                        int n = Int(value);
                        while (list.Count > n) list.RemoveAt(list.Count - 1);
                        // 새 칸은 기본값: 숫자 0, 참조 None (구조체 원소만 마지막 원소 모양을 빌린다)
                        while (list.Count < n)
                        {
                            var tail = list.Count > 0 ? list[list.Count - 1] : null;
                            list.Add(Ref.IsRef(tail) || objRef != null && tail == null ? Ref.Make(0)
                                   : tail is Map || tail is List<object?> ? Yaml.DeepCopy(tail, x => x) : "0");
                        }
                        return;
                    }
                    var dm = DataIndex.Match(part);
                    if (!dm.Success) return;
                    int i = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (i >= list.Count) return;   // Unity: 배열 범위 밖 오버라이드는 무시
                    if (last) { list[i] = Leaf(list[i], value, objRef); return; }
                    if (list[i] is not Map && list[i] is not List<object?>) list[i] = new Map();
                    cur = list[i];
                    continue;
                }
                if (cur is Map m)
                {
                    if (last) { m[part] = Leaf(m.GetValueOrDefault(part), value, objRef); return; }
                    if (!m.TryGetValue(part, out var next) || (next is not Map && next is not List<object?>))
                        m[part] = next = parts[k + 1] == "Array" ? new List<object?>() : (object?)new Map();
                    cur = next;
                    continue;
                }
                return;
            }
        }

        /// 참조 칸이면 objectReference, 아니면 value 문자열
        static object? Leaf(object? old, string value, Map? objRef)
        {
            bool isRefSlot = Ref.IsRef(old) || (objRef != null && Ref.FileId(objRef) != 0);
            return isRefSlot && objRef != null ? Yaml.DeepCopy(objRef, x => x) : value;
        }

        static IEnumerable<object?> List(Map m, string key) =>
            m.TryGetValue(key, out var v) && v is List<object?> l ? l : Enumerable.Empty<object?>();

        static void RemoveFromList(Map m, string key, Predicate<object?> pred)
        {
            if (m.TryGetValue(key, out var v) && v is List<object?> l) l.RemoveAll(pred);
        }

        static int Int(object? v) =>
            v is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }
}
