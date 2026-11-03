// 창 없는 편집 (2026-11-02): apply <편집.json> — 아바타·세션에 편집 목록(DocEdit)을 적용해 세션으로 저장한다.
// 렌더러에 아바타를 올려 둔 채 하는 까닭: 셰이더 스키마(프로퍼티가 색·벡터·숫자·텍스처 중 무엇인지)와 기본 자세를 렌더러가 알려 준다.
// 이어서 run으로 결과 세션을 찍으면 같은 IR이라 다시 올리지 않는다 (EnsureAvatar가 값만 바꿈).
// 아이템 붙이기·빼기·옮기기·크기는 에디터의 아이템 줄과 같은 길(AvHost/Items.cs → importsim → Recompose로 값 옮기기)로 IR을 다시 만든다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ImportSim;
using VrcProto.AvHost;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

sealed partial class ShootServer
{
    JNode ApplyCommand(JNode req)
    {
        var total = Stopwatch.StartNew();
        var problems = new SceneProblems();
        var editsPath = req["edits"].AsString;
        if (!File.Exists(editsPath)) { problems.Error("편집 파일이 없어요: " + editsPath); return Problems(problems); }
        JNode spec;
        try { spec = JNode.Parse(File.ReadAllText(editsPath)); }
        catch (Exception e) when (e is InvalidDataException || e is FormatException || e is IndexOutOfRangeException) { problems.Error($"편집 JSON을 읽지 못했어요: {e.Message}"); return Problems(problems); }
        var baseDir = Path.GetDirectoryName(editsPath)!;
        var avatarRel = req["avatar"].AsStringOrNull ?? spec["avatar"].AsString;
        if (avatarRel.Length == 0) { problems.Error("avatar가 없어요 (x.ir.json 또는 세션.alesav). 목록: dotnet bin/Shoot.dll avatars"); return Problems(problems); }
        var avatar = Path.GetFullPath(Path.Combine(baseDir, avatarRel));
        if (!File.Exists(avatar)) { problems.Error("아바타 파일이 없어요: " + avatar); return Problems(problems); }
        var edits = DocEdit.List(spec);
        if (edits.Count == 0) { problems.Error("편집이 없어요: { \"avatar\": …, \"edits\": [ … ] }"); return Problems(problems); }
        var outPath = req["out"].AsStringOrNull ?? (spec.Has("out") ? Path.GetFullPath(Path.Combine(baseDir, spec["out"].AsString)) : Path.ChangeExtension(editsPath, null) + Session.Extension);
        if (!Session.IsSessionFile(outPath)) outPath += Session.Extension;

        _rendererLog.Clear();
        _notes.Clear();
        var reports = JNode.Arr();
        var load = avatar;
        var itemOps = edits.Where(DocEdit.IsItemOp).ToList();
        if (itemOps.Count > 0)
        {
            load = RebuildItems(avatar, itemOps, baseDir, problems, reports);
            if (problems.Errors.Count > 0) return Problems(problems);
        }
        EnsureRenderer();
        EnsureAvatar(load, _notes);
        var doc = _doc!;
        var before = doc.ChangesetToJson();
        var beforeVariants = doc.VariantsToJson();
        var env = new EditEnv { Scene = Context(baseDir), Meshes = new MeshEdits(doc), BaseDir = baseDir };
        var done = DocEdit.Apply(doc, edits, env, problems);
        if (problems.Errors.Count > 0)
        {
            // 하나라도 틀리면 아무것도 바꾸지 않는다 (렌더러도 앞 상태로)
            doc.LoadChangeset(before);
            doc.LoadVariants(beforeVariants);
            return Problems(problems);
        }

        // 들어온 세션의 카메라·스튜디오(배경·소품)는 그대로 넘긴다
        var src = Session.IsSessionFile(load) ? Session.Read(load) : null;
        var j = Session.ToJson(doc, src != null && !src.Camera.IsNull ? src.Camera : null);
        if (src != null && !src.Studio.IsNull) j.Set("studio", src.Studio);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, j.ToJson(indent: true));
        _avatarKey = $"{outPath}|{File.GetLastWriteTimeUtc(outPath).Ticks}";   // 이어서 이 세션을 찍을 때 다시 올리지 않게
        _sent.Clear();

        var lines = JNode.Arr(); foreach (var d in done) lines.Add(d);
        var warnings = JNode.Arr();
        foreach (var w in problems.Warnings.Concat(_notes).Concat(_rendererLog.Where(l => !_notes.Contains("불러오기: " + l)))) warnings.Add(w);
        var result = JNode.Obj().Set("ok", true).Set("session", outPath).Set("done", lines).Set("changes", doc.Overrides.Count);
        if (reports.Count > 0) result.Set("items", reports);
        return result.Set("warnings", warnings).Set("seconds", Math.Round(total.Elapsed.TotalSeconds, 2));
    }

    /// <summary>
    /// 아이템 편집대로 IR을 다시 만들고, 지금까지 바꾼 값을 새 IR로 옮긴 세션(임시 폴더)을 돌려준다.
    /// 에디터의 MainWindow.Items(AddItem·RemoveItem·PlaceItem·AutoScaleItem → Rebuild)와 같은 순서.
    /// </summary>
    string RebuildItems(string avatar, List<JNode> ops, string baseDir, SceneProblems p, JNode reports)
    {
        var s = Session.IsSessionFile(avatar) ? Session.Read(avatar) : null;
        var ir = s?.IrPath ?? avatar;
        var scene = IrScene.Load(ir);
        var recipe = Recipe.From(scene) ?? (s != null ? Recipe.FromSource(s.Source) : null);
        if (recipe == null) { p.Error("이 아바타는 아이템을 붙일 재료가 없어요 (예전에 가져온 것·샘플). 에디터나 import로 다시 가져와 주세요"); return ""; }
        var missing = recipe.MissingSources();
        if (missing.Count > 0) { p.Error("아바타를 만든 파일이 없어요 (옮기거나 지운 것 같아요): " + string.Join(", ", missing)); return ""; }
        var items = recipe.Items.ToList();
        var more = new List<string>();
        var changes = s != null && s.Changeset.Kind == JKind.Array ? s.Changeset.Clone() : JNode.Arr();
        var dropMoves = new HashSet<string>();
        var touched = new HashSet<string>();   // 이번에 붙이거나 다시 붙인 아이템 (프리팹 GUID)

        OutfitMerge.Spec? Current(IrOutfit o, string where)
        {
            var cur = items.FirstOrDefault(x => x.Prefab == o.Prefab);
            if (cur == null) p.Error($"{where}{o.Name}은(는) 아바타에 원래 있던 것이라 다시 붙일 수 없어요");
            return cur;
        }

        foreach (var e in ops)
        {
            if (e.Has("addItem"))
            {
                var where = "addItem: ";
                var files = (e["addItem"].Kind == JKind.Array ? e["addItem"].Items.Select(x => x.AsString) : new[] { e["addItem"].AsString }).Select(f => Path.GetFullPath(Path.Combine(baseDir, f))).ToList();
                var gone = files.Where(f => !File.Exists(f)).ToList();
                if (gone.Count > 0) { p.Error(where + "파일이 없어요: " + string.Join(", ", gone)); continue; }
                var notes = new List<string>();
                var pkgs = Packages.Expand(files, notes);
                foreach (var n in notes) p.Warn(where + n);
                var imp = Items.Open(recipe.Sources.Concat(more).Concat(pkgs).Distinct(StringComparer.OrdinalIgnoreCase));
                var set = new HashSet<string>(pkgs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                // 여러 아바타용 패키지(헤어 등)는 이름에 이 아바타 이름이 든 것을, 그다음 MA 프리팹을 앞에 (ItemWindow와 같은 순서)
                var prefabs = imp.PackagePrefabs().Where(x => set.Contains(x.Source) && !x.IsAvatar && x.Renderers > 0)
                    .OrderByDescending(x => scene.Name.Length > 0 && (x.Name + x.Path).IndexOf(scene.Name, StringComparison.OrdinalIgnoreCase) >= 0).ThenByDescending(x => x.HasMergeArmature).ToList();
                if (prefabs.Count == 0) { p.Error(where + "붙일 수 있는 프리팹이 없어요 (메시가 있는 프리팹, 아바타 제외)"); continue; }
                var want = e["prefab"].AsStringOrNull;
                var pick = want == null ? (prefabs.Count == 1 ? prefabs[0] : null)
                    : prefabs.FirstOrDefault(x => string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Path, want, StringComparison.OrdinalIgnoreCase));
                if (pick == null)
                {
                    var list = string.Join(", ", prefabs.Take(30).Select(x => x.Name + (x.HasMergeArmature ? " (MA)" : "")));
                    p.Error(want == null ? $"{where}프리팹이 {prefabs.Count}개예요. prefab으로 하나를 골라 주세요: {list}" : $"{where}그런 프리팹이 없어요: {want}{SceneSpec.Suggest(want, prefabs.Select(x => x.Name))} (목록: {list})");
                    continue;
                }
                var spec = new OutfitMerge.Spec { Prefab = pick.Guid };
                if (!Place(spec, e, where, p)) continue;
                items.RemoveAll(x => x.Prefab == spec.Prefab);
                items.Add(spec);
                more.AddRange(pkgs);
                touched.Add(spec.Prefab);
            }
            else if (e.Has("removeItem"))
            {
                if (DocEdit.Item(scene, e["removeItem"].AsString, "removeItem: ", p) is IrOutfit o && Current(o, "removeItem: ") != null)
                    items.RemoveAll(x => x.Prefab == o.Prefab);
            }
            else if (e.Has("placeItem"))
            {
                if (!(DocEdit.Item(scene, e["placeItem"].AsString, "placeItem: ", p) is IrOutfit o) || !(Current(o, "placeItem: ") is OutfitMerge.Spec cur)) continue;
                var spec = new OutfitMerge.Spec { Prefab = o.Prefab, Scale = cur.Scale, Kind = o.Kind == "outfit" || o.Kind == "hair" || o.Kind == "accessory" ? o.Kind : "accessory" };
                if (!Place(spec, e, "placeItem: ", p)) continue;
                items[items.IndexOf(cur)] = spec;
                touched.Add(spec.Prefab);
                dropMoves.Add(Addr.NodeTransform(o.Node).ToString());   // 새로 붙이는 자리가 핸들로 옮긴 자리를 대신한다
            }
            else if (e.Has("fitHead"))
            {
                if (!(DocEdit.Item(scene, e["fitHead"].AsString, "fitHead: ", p) is IrOutfit o) || !(Current(o, "fitHead: ") is OutfitMerge.Spec cur)) continue;
                if (o.HeadScale <= 0 || double.IsNaN(o.HeadScale) || double.IsInfinity(o.HeadScale)) { p.Error($"fitHead: {o.Name}에서 머리 크기 비율을 계산하지 못했어요"); continue; }
                cur.Scale = Math.Clamp(o.HeadScale, 0.01, 100);
                touched.Add(cur.Prefab);
            }
            else if (e.Has("itemScale"))
            {
                if (!(DocEdit.Item(scene, e["itemScale"].AsString, "itemScale: ", p) is IrOutfit o) || !(Current(o, "itemScale: ") is OutfitMerge.Spec cur)) continue;
                var k = e["scale"].AsDouble;
                if (k <= 0) { p.Error($"itemScale: scale(배율)을 적어 주세요"); continue; }
                cur.Scale = Math.Clamp(k, 0.01, 100);
                touched.Add(cur.Prefab);
            }
        }
        if (p.Errors.Count > 0) return "";

        if (dropMoves.Count > 0)
        {
            var kept = JNode.Arr();
            foreach (var op in changes.Items) if (!dropMoves.Contains(op["a"].AsString)) kept.Add(op);
            changes = kept;
        }
        Shoot.Log("아이템 다시 만들기: " + ir);
        var (path, warns, reps) = Items.Build(recipe.With(more, items), Path.GetDirectoryName(ir)!);
        foreach (var w in warns.Take(10)) p.Warn("다시 만들기: " + w);
        var next = IrScene.Load(path);
        var moved = Recompose.RemapChangeset(changes, scene, next);
        var variants = JNode.Arr();
        if (s != null) foreach (var v in s.Variants.Items) variants.Add(v.Clone().Set("changeset", Recompose.RemapChangeset(v["changeset"], scene, next)));
        // 이번 편집으로 붙이거나 다시 붙인 아이템만 알린다
        foreach (var r in reps.Where(x => x.AddedFrom.Length > 0 && touched.Contains(x.AddedFrom)))
        {
            var o = next.Outfits.FirstOrDefault(x => x.Prefab == r.AddedFrom);
            var fit = Items.FitText(r.Matched, r.Bones, r.MeanOffset, out bool bad);
            reports.Add(JNode.Obj().Set("name", o?.Name ?? r.AddedFrom).Set("kind", r.Kind).Set("method", Items.MethodLabel(r.Method)).Set("attachedTo", r.Target)
                .Set("fit", fit).Set("otherAvatarsItem", bad).Set("headScale", Math.Round(o?.HeadScale ?? 0, 4)).Set("scale", Math.Round(o?.Scale ?? 1, 4)));
        }
        foreach (var o in next.Outfits.Where(x => x.NeedsPlacement)) p.Warn($"{o.Name}: 어디에 붙일지 정하지 못해 아바타 루트에 그대로 있어요 (placeItem으로 붙일 곳을 정해 주세요)");

        var tmp = Path.Combine(Shoot.TempDir, $"items_{Guid.NewGuid():N}{Session.Extension}");
        var j = JNode.Obj().Set("format", Session.Format).Set("version", Session.Version).Set("ir", path).Set("changeset", moved)
            .Set("camera", s?.Camera ?? JNode.Null).Set("variants", variants).Set("source", next.Raw["source"]);
        if (s != null && !s.Studio.IsNull) j.Set("studio", s.Studio);
        File.WriteAllText(tmp, j.ToJson(indent: true));
        return tmp;
    }

    /// <summary>붙일 곳: kind(outfit·hair·accessory) · target(뼈 이름, 비우면 종류별 기본) · keepWorld · scale. 둘 다 없으면 자동.</summary>
    static bool Place(OutfitMerge.Spec spec, JNode e, string where, SceneProblems p)
    {
        if (e["kind"].AsStringOrNull is string kind)
        {
            if (kind != "outfit" && kind != "hair" && kind != "accessory" && kind != "auto") { p.Error($"{where}kind는 outfit·hair·accessory·auto예요: {kind}"); return false; }
            spec.Kind = kind;
        }
        if (e["target"].AsStringOrNull is string target)
        {
            if (spec.Kind == "auto") { p.Error($"{where}target을 쓰려면 kind(outfit·hair·accessory)도 적어 주세요"); return false; }
            spec.Target = target;
        }
        if (e.Has("keepWorld")) spec.KeepWorld = e["keepWorld"].AsBool;
        if (e.Has("scale")) spec.Scale = Math.Clamp(e["scale"].AsDouble, 0.01, 100);
        return true;
    }
}
