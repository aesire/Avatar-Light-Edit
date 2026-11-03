// 창 없는 편집 (2026-11-02, Tools/Shoot): 편집 목록 JSON을 Document에 적용한다 — 에디터의 아바타·머티리얼·블렌드쉐이프·자세·환경 페이지에서
// 하는 값 바꾸기를 이름으로 적는다. 화면과 같은 Core 함수(Document.Set·Wardrobe·ToggleSet·ShapeSets·BlinkFixPlan·MeshEdits·LilToonRules)를 부른다 — 따로 계산하지 않는다.
// 아이템 붙이기·빼기(IR을 다시 만듦)는 ImportSim이 필요해 ShootServer.Edit.cs가 먼저 한다. 형식 설명은 .claude/skills/avatar-edit/EDIT.md.
// 틀린 이름은 모두 모아 비슷한 이름과 함께 알린다. 오류가 하나라도 있으면 부른 쪽이 저장하지 않는다.
// 에디터에 들어가지 않게 Host/Core 밖에 둔다 (SceneSpec과 같은 이유). 단위 테스트: Tests/DocEditTests.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    /// <summary>편집할 때 필요한 것.</summary>
    public sealed class EditEnv
    {
        /// <summary>자세·표정·조명 해석 (렌더러가 알려 준 기본 자세가 든 장면 정보). null이면 그 편집은 오류.</summary>
        public SceneContext? Scene;
        /// <summary>깜빡임·입 맞추기, 정점 합치기·줄이기 (원본 메시를 IR 버퍼에서 읽는다).</summary>
        public MeshEdits? Meshes;
        /// <summary>상대 경로(텍스처 파일)의 기준 폴더.</summary>
        public string BaseDir = ".";
    }

    public static class DocEdit
    {
        /// <summary>IR을 다시 만드는 편집 (ShootServer.Edit.cs가 먼저 한다).</summary>
        public static readonly string[] ItemKeys = { "addItem", "removeItem", "placeItem", "fitHead", "itemScale" };
        static readonly string[] Kinds = { "material", "shapes", "show", "hide", "wardrobe", "toggles", "weld", "decimate", "blinkFix", "mouthFix", "move", "pose", "expression", "lighting", "saveShapes", "loadShapes", "reset", "addr" };

        public static bool IsItemOp(JNode e) => e.Kind == JKind.Object && ItemKeys.Any(e.Has);

        /// <summary>편집 목록: [ … ] 또는 { edits: [ … ] }.</summary>
        public static List<JNode> List(JNode j) => (j.Kind == JKind.Array ? j : j["edits"]).Items.ToList();

        /// <summary>값 편집을 차례로 적용한다 (아이템 편집은 건너뜀). 한 일을 한 줄씩 돌려준다.</summary>
        public static List<string> Apply(Document doc, IEnumerable<JNode> edits, EditEnv env, SceneProblems p)
        {
            var done = new List<string>();
            int i = 0;
            foreach (var e in edits)
            {
                var where = $"edits[{i++}] ";
                if (e.Kind != JKind.Object) { p.Error(where + "편집은 { … } 로 적어 주세요"); continue; }
                if (IsItemOp(e)) continue;
                int errors = p.Errors.Count;
                var line = One(doc, e, env, where, p);
                if (line != null && p.Errors.Count == errors) done.Add(line);
            }
            return done;
        }

        static string? One(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            if (e.Has("material")) return Material(doc, e, env, where, p);
            if (e.Has("shapes")) return Shapes(doc, e, env, where, p);
            if (e.Has("show") || e.Has("hide")) return Visibility(doc, e, where, p);
            if (e.Has("wardrobe")) return WardrobeGroups(doc, e, where, p);
            if (e.Has("toggles")) return Toggles(doc, e, where, p);
            if (e.Has("weld") || e.Has("decimate")) return Mesh(doc, e, env, where, p);
            if (e.Has("blinkFix") || e.Has("mouthFix")) return Fix(doc, e, env, where, p);
            if (e.Has("move")) return Move(doc, e, where, p);
            if (e.Has("pose") || e.Has("expression") || e.Has("lighting") || e.Has("lights") || e.Has("post")) return Look(doc, e, env, where, p);
            if (e.Has("saveShapes") || e.Has("loadShapes")) return ShapeSet(doc, e, where, p);
            if (e.Has("reset")) return Reset(doc, e, where, p);
            if (e.Has("addr")) return Raw(doc, e, where, p);
            p.Error($"{where}무엇을 할지 모르겠어요. 쓸 수 있는 것: {string.Join(", ", Kinds.Concat(ItemKeys))}");
            return null;
        }

        static void Allow(JNode e, string where, SceneProblems p, params string[] keys)
        {
            foreach (var kv in e.Props)
                if (!keys.Contains(kv.Key) && !kv.Key.StartsWith("_") && kv.Key != "note")
                    p.Error($"{where}모르는 값: \"{kv.Key}\"{SceneSpec.Suggest(kv.Key, keys)} (쓸 수 있는 것: {string.Join(", ", keys)})");
        }

        // ---------------- 이름 찾기 ----------------
        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static IEnumerable<string> Names(JNode spec) => spec.Kind == JKind.Array ? spec.Items.Select(x => x.AsString) : new[] { spec.AsString };

        /// <summary>렌더러: 오브젝트 이름·경로·id. "*" = 전부.</summary>
        public static List<IrRenderer> Renderers(IrScene s, JNode spec, string where, SceneProblems p)
        {
            var list = new List<IrRenderer>();
            foreach (var name in Names(spec))
            {
                if (name == "*") { list.AddRange(s.Renderers); continue; }
                var hit = s.Renderers.Where(r => Eq(SceneSpec.RendererName(s, r), name) || r.Id == name).ToList();
                if (hit.Count == 0) hit = s.Renderers.Where(r => Eq(s.NodePath(r.Node), name) || s.NodePath(r.Node).EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 0) p.Error($"{where}그런 메시가 없어요: \"{name}\"{SceneSpec.Suggest(name, s.Renderers.Select(r => SceneSpec.RendererName(s, r)))}");
                list.AddRange(hit);
            }
            return list.Distinct().ToList();
        }

        /// <summary>오브젝트(노드): 이름 또는 경로 ("Armature/Hips/Hat"). 같은 이름이 여럿이면 모두.</summary>
        public static List<int> Nodes(IrScene s, string name, string where, SceneProblems p)
        {
            var hit = s.Nodes.Where(n => Eq(s.NodePath(n.Id), name) || s.NodePath(n.Id).EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) && name.Contains('/')).Select(n => n.Id).ToList();
            if (hit.Count == 0) hit = s.Nodes.Where(n => Eq(n.Name, name)).Select(n => n.Id).ToList();
            if (hit.Count == 0) p.Error($"{where}그런 오브젝트가 없어요: \"{name}\"{SceneSpec.Suggest(name, s.Nodes.Select(n => n.Name))}");
            return hit;
        }

        /// <summary>머티리얼: 이름·GUID·파일 이름. "*" = 전부 (renderers가 있으면 그 메시들이 쓰는 것만).</summary>
        public static List<IrMaterial> Materials(IrScene s, JNode spec, List<IrRenderer>? within, string where, SceneProblems p)
        {
            var pool = within == null ? s.Materials : within.SelectMany(r => r.Materials).Distinct().Select(s.FindMaterial).Where(m => m != null).Select(m => m!).ToList();
            var list = new List<IrMaterial>();
            foreach (var name in Names(spec))
            {
                if (name == "*") { list.AddRange(pool); continue; }
                var hit = pool.Where(m => Eq(m.Name, name) || m.Guid == name || Eq(Path.GetFileNameWithoutExtension(m.Path), name)).ToList();
                if (hit.Count == 0) p.Error($"{where}그런 머티리얼이 없어요: \"{name}\"{(within != null ? " (고른 메시가 쓰는 것 중에)" : "")}{SceneSpec.Suggest(name, pool.Select(m => m.Name))}");
                list.AddRange(hit);
            }
            return list.Distinct().ToList();
        }

        /// <summary>붙인 아이템: 이름, 프리팹 GUID, "node:번호" (look의 items[].id). 같은 이름이 여럿이면 id로 골라야 한다.</summary>
        public static IrOutfit? Item(IrScene s, string name, string where, SceneProblems p)
        {
            var hit = s.Outfits.Where(x => name.Length > 0 && (x.Prefab == name || name == ItemId(x))).ToList();
            if (hit.Count == 0) hit = s.Outfits.Where(x => Eq(x.Name, name)).ToList();
            if (hit.Count == 0) p.Error($"{where}그런 아이템이 없어요: \"{name}\"{(s.Outfits.Count == 0 ? " (붙인 아이템이 없어요)" : SceneSpec.Suggest(name, s.Outfits.Select(x => x.Name)))}");
            else if (hit.Count > 1) p.Error($"{where}\"{name}\" 아이템이 {hit.Count}개예요. id로 골라 주세요: {string.Join(", ", hit.Select(ItemId))}");
            return hit.Count == 1 ? hit[0] : null;
        }

        /// <summary>아이템을 가리키는 값: 붙인 것은 프리팹 GUID, 아바타에 원래 있던 것은 node:번호 (IR을 다시 만들면 번호가 바뀔 수 있다).</summary>
        public static string ItemId(IrOutfit o) => o.Prefab.Length > 0 ? o.Prefab : "node:" + o.Node;

        // ---------------- 머티리얼 ----------------
        static string? Material(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            Allow(e, where, p, "material", "renderer", "set", "keywords", "shader", "renderQueue", "renderMode", "transparentMode", "outline", "reset");
            var s = doc.Original;
            var within = e.Has("renderer") ? Renderers(s, e["renderer"], where, p) : null;
            var mats = Materials(s, e["material"], within, where, p);
            if (mats.Count == 0) return null;
            int n = 0;
            using (doc.Group("머티리얼 편집"))
                foreach (var m in mats)
                {
                    var g = m.Guid;
                    if (e["reset"].AsBool) doc.ResetWhere(a => a.IsMaterial && a.Target == g, "머티리얼 리셋");
                    if (e["shader"].AsStringOrNull is string sh) { doc.Set(Addr.MatShader(g), JNode.From(sh)); n++; }
                    if (e.Has("renderMode") || e.Has("transparentMode") || e.Has("outline")) n += RenderMode(doc, m, e, where, p);
                    if (e.Has("renderQueue")) { doc.Set(Addr.MatRenderQueue(g), JNode.From(e["renderQueue"].AsInt)); n++; }
                    foreach (var kv in e["keywords"].Props) { doc.Set(Addr.MatKeyword(g, kv.Key), JNode.From(kv.Value.AsBool)); n++; }
                    foreach (var kv in e["set"].Props)
                    {
                        var a = PropAddr(doc, m, kv.Key, where, p);
                        if (a == null) continue;
                        var v = PropValue(doc, a.Value, kv.Value, env, where, p);
                        if (v == null) continue;
                        doc.Set(a.Value, v);
                        n++;
                    }
                }
            return $"머티리얼 {string.Join(", ", mats.Select(m => m.Name))}: 값 {n}개";
        }

        static int RenderMode(Document doc, IrMaterial m, JNode e, string where, SceneProblems p)
        {
            var cur = LilToonRules.Detect(doc.Get(Addr.MatShader(m.Guid)).AsString, doc.Get(Addr.MatFloat(m.Guid, "_TransparentMode")).AsFloat);
            if (cur == null) { p.Error($"{where}{m.Name}: renderMode·transparentMode·outline은 lilToon 머티리얼만 돼요 (셰이더: {doc.Get(Addr.MatShader(m.Guid)).AsString})"); return 0; }
            var mode = cur.Mode; var trans = cur.Transparent; var outline = cur.Outline;
            if (e["renderMode"].AsStringOrNull is string rm && !Enum.TryParse(rm, true, out mode)) { p.Error($"{where}renderMode를 모르겠어요: {rm} ({string.Join("·", Enum.GetNames(typeof(LilRenderingMode)))})"); return 0; }
            if (e["transparentMode"].AsStringOrNull is string tm && !Enum.TryParse(tm, true, out trans)) { p.Error($"{where}transparentMode를 모르겠어요: {tm} ({string.Join("·", Enum.GetNames(typeof(LilTransparentMode)))})"); return 0; }
            if (e.Has("outline")) outline = e["outline"].AsBool;
            if (LilToonRules.Switch(doc, m.Guid, mode, trans, outline) == false) { p.Error($"{where}{m.Name}: 이 lilToon 종류({cur.Family})에는 그 모드가 없어요"); return 0; }
            return 1;
        }

        /// <summary>
        /// 프로퍼티 이름 → 주소. 렌더러가 보낸 셰이더 스키마로 종류(숫자·색·벡터·텍스처)를 정한다. 스키마가 없으면(지원하지 않는 셰이더) .mat에 적힌 값으로.
        /// "_MainTex_ST" = 텍스처 타일링·오프셋 [x, y, 오프셋 x, 오프셋 y].
        /// </summary>
        static Addr? PropAddr(Document doc, IrMaterial m, string prop, string where, SceneProblems p)
        {
            var schema = doc.Schema(m.Guid);
            if (prop.EndsWith("_ST"))
            {
                var tex = prop.Substring(0, prop.Length - 3);
                if (schema?.Find(tex)?.Type == "Texture" || m.TexEnvs.ContainsKey(tex)) return Addr.MatTexST(m.Guid, tex);
            }
            var info = schema?.Find(prop);
            if (info != null)
                switch (info.Type)
                {
                    case "Color": return Addr.MatColor(m.Guid, prop);
                    case "Vector": return Addr.MatVector(m.Guid, prop);
                    case "Texture": return Addr.MatTex(m.Guid, prop);
                    default: return Addr.MatFloat(m.Guid, prop);
                }
            if (schema == null || !schema.Supported)
            {
                if (m.Floats.ContainsKey(prop)) return Addr.MatFloat(m.Guid, prop);
                if (m.TexEnvs.ContainsKey(prop)) return Addr.MatTex(m.Guid, prop);
                if (m.Colors.ContainsKey(prop)) return prop.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0 ? Addr.MatColor(m.Guid, prop) : Addr.MatVector(m.Guid, prop);
            }
            var names = (schema?.Props.Select(x => x.Name) ?? Enumerable.Empty<string>()).Concat(m.Floats.Keys).Concat(m.Colors.Keys).Concat(m.TexEnvs.Keys);
            p.Error($"{where}{m.Name}에 그런 프로퍼티가 없어요: {prop}{SceneSpec.Suggest(prop, names)}");
            return null;
        }

        static JNode? PropValue(Document doc, Addr a, JNode v, EditEnv env, string where, SceneProblems p)
        {
            var label = $"{where}{a.Name}: ";
            switch (a.Kind)
            {
                case AddrKind.MatFloat:
                    if (v.Kind == JKind.Bool) return JNode.From(v.AsBool ? 1f : 0f);
                    if (v.Kind == JKind.Number) return JNode.From(v.AsFloat);
                    p.Error(label + "숫자를 적어 주세요"); return null;
                case AddrKind.MatColor:
                case AddrKind.MatVector:
                    {
                        var cur = doc.Get(a).ToFloatArray();
                        float alpha = cur.Length > 3 ? cur[3] : 1;
                        float[] c;
                        if (v.Kind == JKind.String) c = SceneSpec.Color(v, label, p).ToFloatArray();
                        else if (v.Kind == JKind.Array && v.Count >= 3 && v.Count <= 4) c = v.ToFloatArray();
                        else { p.Error(label + "\"#RRGGBB\" 또는 [r, g, b] / [r, g, b, a]로 적어 주세요"); return null; }
                        if (c.Length == 3) c = new[] { c[0], c[1], c[2], alpha };
                        return JNode.FloatArray(c);
                    }
                case AddrKind.MatTexST:
                    if (v.Kind == JKind.Array && v.Count == 4) return JNode.FloatArray(v.ToFloatArray());
                    p.Error(label + "[타일링 x, 타일링 y, 오프셋 x, 오프셋 y]로 적어 주세요"); return null;
                case AddrKind.MatTex:
                    return Texture(doc, a, v, env, label, p);
            }
            return null;
        }

        /// <summary>텍스처 값: null(비움) · IR 텍스처 이름·경로·GUID · 이미지 파일 경로 (새 텍스처로 더한다, 임포트 설정은 원래 텍스처를 따름).</summary>
        static JNode? Texture(Document doc, Addr a, JNode v, EditEnv env, string label, SceneProblems p)
        {
            if (v.IsNull) return JNode.Null;
            var name = v.AsString;
            var s = doc.Original;
            var hit = s.FindTexture(name) ?? s.Textures.FirstOrDefault(t => Eq(t.Path, name) || Eq(Path.GetFileNameWithoutExtension(t.Path), name) || Eq(Path.GetFileName(t.Path), name));
            if (hit != null) return JNode.From(hit.Guid);
            var file = Path.GetFullPath(Path.Combine(env.BaseDir, name));
            if (File.Exists(file))
            {
                var key = "file:" + Fnv(file.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(file).Ticks);
                var like = doc.Get(a).AsStringOrNull ?? "";
                doc.Set(Addr.TexAsset(key), JNode.Obj().Set("file", file).Set("path", Path.GetFileName(file)).Set("like", like));
                return JNode.From(key);
            }
            p.Error($"{label}텍스처를 찾지 못했어요: {name} (IR 텍스처 이름이나 이미지 파일 경로){SceneSpec.Suggest(name, s.Textures.Select(t => Path.GetFileNameWithoutExtension(t.Path)))}");
            return null;
        }

        static string Fnv(string s)
        {
            ulong h = 1469598103934665603UL;
            foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        // ---------------- 블렌드쉐이프 ----------------
        static string? Shapes(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            Allow(e, where, p, "shapes", "renderer");
            var s = doc.Original;
            var rens = e.Has("renderer") ? Renderers(s, e["renderer"], where, p) : s.Renderers;
            if (e["shapes"].Kind != JKind.Object) { p.Error($"{where}shapes는 {{ \"이름\": 0~100 }}로 적어 주세요"); return null; }
            int n = 0;
            var touched = new List<string>();
            using (doc.Group("블렌드쉐이프 편집"))
                foreach (var kv in e["shapes"].Props)
                {
                    if (kv.Value.Kind != JKind.Number) { p.Error($"{where}{kv.Key}: 0~100 숫자를 적어 주세요"); continue; }
                    var found = rens.Where(r => s.FindMesh(r.Mesh)?.BlendShapeIndex(kv.Key) >= 0).ToList();
                    if (found.Count == 0)
                    {
                        var all = rens.SelectMany(r => s.FindMesh(r.Mesh)?.BlendShapes.Select(b => b.Name) ?? Enumerable.Empty<string>());
                        p.Error($"{where}그런 블렌드쉐이프가 없어요: \"{kv.Key}\"{SceneSpec.Suggest(kv.Key, all)}");
                        continue;
                    }
                    foreach (var r in found) { doc.Set(Addr.BlendShape(r.Id, kv.Key), JNode.From(kv.Value.AsFloat)); touched.Add(r.Id); }
                    n++;
                }
            // 자동 Blink Fix는 눈 편집 값을 따라간다 (블렌드쉐이프 페이지와 같음)
            if (env.Meshes != null) BlinkFixPlan.Follow(doc, touched, env.Meshes.Original);
            return $"블렌드쉐이프 {n}개";
        }

        // ---------------- 켜고 끄기 ----------------
        static string? Visibility(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "show", "hide");
            var s = doc.Original;
            var names = new List<string>();
            using (doc.Group("켜고 끄기"))
                foreach (var (key, on) in new[] { ("show", true), ("hide", false) })
                    foreach (var name in e.Has(key) ? Names(e[key]) : Enumerable.Empty<string>())
                    {
                        names.Add((on ? "+" : "-") + name);
                        // 붙인 아이템 → 아이템 줄의 스위치, 메시 → 기본 옷 줄의 스위치, 그 밖의 오브젝트 → 켜기만
                        if (s.Outfits.Any(x => Eq(x.Name, name) || ItemId(x) == name))
                        {
                            if (Item(s, name, where, p) is IrOutfit o) Wardrobe.SetItem(doc, o, on);
                            continue;
                        }
                        var rens = s.Renderers.Where(r => Eq(SceneSpec.RendererName(s, r), name)).ToList();
                        if (rens.Count > 0) { foreach (var r in rens) Wardrobe.Show(doc, r.Node, r.Id, on); continue; }
                        foreach (var node in Nodes(s, name, where, p))
                        {
                            doc.Set(Addr.NodeActive(node), on);
                            if (on) foreach (var x in Wardrobe.EditorOnlyChain(s, node)) doc.Set(Addr.NodeActive(x), true);
                        }
                    }
            return "켜고 끄기: " + string.Join(" ", names);
        }

        static string? WardrobeGroups(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "wardrobe");
            var parts = Wardrobe.Parts(doc.Original, Wardrobe.WeightsFrom(doc.Original));
            var said = new List<string>();
            foreach (var kv in e["wardrobe"].Props)
            {
                if (!Wardrobe.Kinds.Contains(kv.Key)) { p.Error($"{where}wardrobe 종류를 모르겠어요: {kv.Key} ({string.Join("·", Wardrobe.Kinds)})"); continue; }
                Wardrobe.SetGroup(doc, parts, kv.Key, kv.Value.AsBool);
                said.Add($"기본 {Wardrobe.KindLabel(kv.Key)} {(kv.Value.AsBool ? "입힘" : "벗김")}");
            }
            return string.Join(", ", said);
        }

        static string? Toggles(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "toggles", "append");
            var s = doc.Original;
            var list = e["append"].AsBool ? doc.Get(Addr.Toggles).Items.ToList() : new List<JNode>();
            foreach (var t in e["toggles"].Items)
            {
                var nodes = t["objects"].Items.SelectMany(x => Nodes(s, x.AsString, where, p)).ToList();
                if (t["name"].AsString.Length == 0) p.Error($"{where}토글마다 name을 적어 주세요");
                list.Add(ToggleSet.Make(t["name"].AsString, nodes));
            }
            ToggleSet.Replace(doc, list, "토글 묶음 바꾸기");
            return $"토글 묶음 {list.Count}개";
        }

        // ---------------- 메시 편집 ----------------
        static string? Mesh(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            Allow(e, where, p, "weld", "decimate", "renderers", "keepSeams", "keepShapes");
            if (env.Meshes == null) { p.Error($"{where}메시 편집은 지금 쓸 수 없어요"); return null; }
            var s = doc.Original;
            var rens = e.Has("renderers") ? Renderers(s, e["renderers"], where, p) : s.Renderers;
            var ids = env.Meshes.MeshIds(rens).ToList();
            bool keepShapes = !e.Has("keepShapes") || e["keepShapes"].AsBool;
            if (e.Has("weld"))
            {
                // weld = 합칠 거리 (mm). 아바타 페이지의 정점 합치기와 같음
                float mm = e["weld"].AsFloat;
                if (mm <= 0 || mm > 10) { p.Error($"{where}weld는 합칠 거리(mm, 0~10)예요: {mm}"); return null; }
                var (dv, dt) = env.Meshes.WeldAll(ids, mm / 1000f, !e.Has("keepSeams") || e["keepSeams"].AsBool, keepShapes);
                return $"정점 합치기 {mm} mm: 정점 −{dv:N0}, 삼각형 −{dt:N0}";
            }
            var d = e["decimate"];
            if (d.Kind == JKind.Object && d.Has("triangles"))
            {
                var targets = env.Meshes.Distribute(ids, d["triangles"].AsInt);
                env.Meshes.SetDecimate(targets, keepShapes);
                return $"정점 줄이기: 삼각형 {d["triangles"].AsInt:N0}개로";
            }
            float ratio = d.Kind == JKind.Bool && !d.AsBool ? 1 : d.AsFloat;
            if (ratio <= 0 || ratio > 1) { p.Error($"{where}decimate는 남길 비율(0~1, 1 = 끄기) 또는 {{ \"triangles\": 수 }}예요"); return null; }
            env.Meshes.SetDecimate(ids, ratio, keepShapes);
            return ratio >= 0.999f ? "정점 줄이기 끔" : $"정점 줄이기 {ratio * 100:0}%";
        }

        static string? Fix(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            Allow(e, where, p, "blinkFix", "mouthFix", "renderer");
            if (env.Meshes == null) { p.Error($"{where}깜빡임·입 맞추기는 지금 쓸 수 없어요"); return null; }
            bool blink = e.Has("blinkFix");
            var kind = blink ? MeshOps.BlinkFix : MeshOps.MouthFix;
            bool on = (blink ? e["blinkFix"] : e["mouthFix"]).AsBool;
            var s = doc.Original;
            var rens = e.Has("renderer") ? Renderers(s, e["renderer"], where, p) : s.Renderers.ToList();
            var said = new List<string>();
            foreach (var r in rens)
            {
                if (r.Mesh.Length == 0) continue;
                if (!on)
                {
                    if (BlinkFixPlan.Current(doc, r.Mesh, kind) == null) continue;
                    BlinkFixPlan.Set(doc, r.Mesh, null, blink ? "Blink Fix 되돌리기" : "Mouth Fix 되돌리기", kind);
                    said.Add(SceneSpec.RendererName(s, r));
                    continue;
                }
                var mesh = env.Meshes.Original(r.Mesh);
                if (mesh == null) continue;
                var op = blink ? BlinkFixPlan.AutoOp(doc, r, mesh, BlinkFixPlan.Current(doc, r.Mesh, kind)) : BlinkFixPlan.MouthDefaultOp(doc, r, mesh);
                if (op == null) continue;
                BlinkFixPlan.Set(doc, r.Mesh, op, blink ? "깜빡임 자동 맞추기 켜기" : "입 맞추기", kind);
                said.Add($"{SceneSpec.RendererName(s, r)} (대상 {op["targets"].Count}, 편집 {op["sources"].Count})");
            }
            var what = blink ? "깜빡임 맞추기(Blink Fix)" : "입 맞추기(Mouth Fix)";
            if (said.Count == 0)
            {
                if (on) p.Warn($"{where}{what}: 고칠 것이 없어요 ({(blink ? "깜빡임 블렌드쉐이프가 없거나" : "입 블렌드쉐이프가 없거나")} 지금 값이 0이 아닌 편집 블렌드쉐이프가 없어요)");
                return null;
            }
            return $"{what} {(on ? "적용" : "되돌림")}: {string.Join(", ", said)}";
        }

        // ---------------- 오브젝트 옮기기 ----------------
        static string? Move(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "move", "position", "rotation", "scale", "nudge");
            var s = doc.Original;
            var nodes = Nodes(s, e["move"].AsString, where, p);
            foreach (var n in nodes)
            {
                var a = Addr.NodeTransform(n);
                var t = doc.Get(a).ToFloatArray();
                if (t.Length < 10) continue;
                if (e.Has("position")) Copy(e["position"], t, 0, 3, "position", where, p);
                if (e.Has("nudge") && e["nudge"].Count == 3) for (int i = 0; i < 3; i++) t[i] += e["nudge"][i].AsFloat;
                if (e.Has("rotation"))
                {
                    var r = e["rotation"];
                    if (r.Count == 3) Array.Copy(SceneSpec.Euler(r[0].AsFloat, r[1].AsFloat, r[2].AsFloat), 0, t, 3, 4);
                    else Copy(r, t, 3, 4, "rotation", where, p);
                }
                if (e.Has("scale"))
                {
                    if (e["scale"].Kind == JKind.Number) for (int i = 7; i < 10; i++) t[i] = e["scale"].AsFloat;
                    else Copy(e["scale"], t, 7, 3, "scale", where, p);
                }
                doc.Set(a, JNode.FloatArray(t), "옮기기: " + s.Nodes[n].Name);
            }
            return nodes.Count == 0 ? null : $"옮기기: {e["move"].AsString}";
        }

        static void Copy(JNode v, float[] to, int at, int n, string name, string where, SceneProblems p)
        {
            if (v.Kind != JKind.Array || v.Count != n) { p.Error($"{where}{name}은 숫자 {n}개예요"); return; }
            for (int i = 0; i < n; i++) to[at + i] = v[i].AsFloat;
        }

        // ---------------- 자세·표정·조명 (장면 JSON과 같은 형식) ----------------
        static string? Look(Document doc, JNode e, EditEnv env, string where, SceneProblems p)
        {
            Allow(e, where, p, "pose", "expression", "name", "lighting", "lights", "post", "look");
            if (env.Scene == null) { p.Error($"{where}자세·표정·조명은 지금 쓸 수 없어요"); return null; }
            var said = new List<string>();
            if (e.Has("pose"))
            {
                var pose = e["pose"].IsNull ? JNode.Null : SceneSpec.Pose(e["pose"], env.Scene, where, p);
                doc.Set(Addr.Pose, pose, "자세");
                said.Add("자세");
            }
            if (e.Has("expression"))
            {
                var shapes = SceneSpec.Expression(e["expression"], env.Scene, where, p);
                var name = e["name"].AsStringOrNull ?? (e["expression"].AsStringOrNull ?? "표정");
                doc.Set(Addr.Expression, shapes.Count == 0 ? JNode.Null : FaceExpression.Make(name, "anim", shapes.Items.Select(x => (x["renderer"].AsString, x["shape"].AsString, x["weight"].AsFloat))), "표정");
                said.Add("표정");
            }
            if (e.Has("lighting") || e.Has("lights") || e.Has("post") || e.Has("look"))
            {
                doc.Set(Addr.Environment, SceneSpec.Environment(e, env.Scene, where, p), "조명");
                said.Add("조명");
            }
            return string.Join("·", said);
        }

        // ---------------- 블렌드쉐이프 세팅 ----------------
        static string? ShapeSet(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "saveShapes", "loadShapes");
            if (e["saveShapes"].AsStringOrNull is string save)
            {
                var old = ShapeSets.All(doc).FirstOrDefault(v => Eq(v.Name, save));
                if (old != null) ShapeSets.Overwrite(doc, old); else ShapeSets.Add(doc, save);
                return $"블렌드쉐이프 세팅 저장: {save}";
            }
            var name = e["loadShapes"].AsString;
            var v = ShapeSets.All(doc).FirstOrDefault(x => Eq(x.Name, name));
            if (v == null) { p.Error($"{where}그런 블렌드쉐이프 세팅이 없어요: \"{name}\"{SceneSpec.Suggest(name, ShapeSets.All(doc).Select(x => x.Name))}"); return null; }
            return $"블렌드쉐이프 세팅 불러오기: {name} ({ShapeSets.Apply(doc, v)}개)";
        }

        // ---------------- 되돌리기 ----------------
        static readonly Dictionary<string, Func<Addr, bool>> ResetKinds = new Dictionary<string, Func<Addr, bool>>
        {
            ["all"] = a => true,
            ["materials"] = a => a.IsMaterial || a.Kind == AddrKind.TexAsset,
            ["shapes"] = a => a.Kind == AddrKind.BlendShape,
            ["visibility"] = a => a.Kind == AddrKind.NodeActive || a.Kind == AddrKind.RendererEnabled,
            ["meshes"] = a => a.Kind == AddrKind.MeshEdit,
            ["moves"] = a => a.Kind == AddrKind.NodeTransform,
            ["pose"] = a => a.Kind == AddrKind.Pose,
            ["expression"] = a => a.Kind == AddrKind.Expression,
            ["lighting"] = a => a.Kind == AddrKind.Environment,
            ["toggles"] = a => a.Kind == AddrKind.Toggles,
        };

        static string? Reset(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "reset");
            var said = new List<string>();
            foreach (var k in Names(e["reset"]))
            {
                if (!ResetKinds.TryGetValue(k, out var pred)) { p.Error($"{where}reset 종류를 모르겠어요: {k} ({string.Join("·", ResetKinds.Keys)}). 머티리얼 하나는 {{ \"material\": 이름, \"reset\": true }}"); continue; }
                doc.ResetWhere(pred, "되돌리기: " + k);
                said.Add(k);
            }
            return "원래대로: " + string.Join(", ", said);
        }

        /// <summary>주소를 그대로 (look --changes가 보여 주는 a 값): 위 편집으로 안 되는 것만.</summary>
        static string? Raw(Document doc, JNode e, string where, SceneProblems p)
        {
            Allow(e, where, p, "addr", "value");
            Addr a;
            try { a = Addr.Parse(e["addr"].AsString); }
            catch (FormatException) { p.Error($"{where}addr를 읽지 못했어요: {e["addr"].AsString} (종류|대상|이름, 종류: {string.Join("·", Enum.GetNames(typeof(AddrKind)))})"); return null; }
            doc.Set(a, e["value"].Clone());
            return "주소 " + a;
        }
    }

    /// <summary>지금 상태 보기 (Shoot look). 렌더러 없이 IR과 세션만으로.</summary>
    public static class DocLook
    {
        static JNode R(double v) => JNode.From(Math.Round(v, 4));
        static JNode Arr(IEnumerable<float> xs) { var a = JNode.Arr(); foreach (var x in xs) a.Add(R(x)); return a; }
        static JNode Strs(IEnumerable<string> xs) { var a = JNode.Arr(); foreach (var x in xs) a.Add(x); return a; }

        public static JNode Summary(Document doc)
        {
            var s = doc.Original;
            var o = JNode.Obj().Set("ok", true).Set("name", s.Name).Set("ir", doc.IrPath).Set("changes", doc.Overrides.Count);

            var st = AvatarStats.From(s);
            var perf = JNode.Obj().Set("pc", st.Overall(PerfPlatform.PC).ToString()).Set("quest", st.Overall(PerfPlatform.Mobile).ToString());
            foreach (var it in st.Items(PerfPlatform.PC).Where(x => x.Rank >= PerfRank.Poor)) perf.Set(it.Label, $"{it.Display} ({it.Rank})");
            o.Set("performance", perf);

            var items = JNode.Arr();
            foreach (var x in s.Outfits)
                items.Add(JNode.Obj().Set("name", x.Name).Set("id", DocEdit.ItemId(x)).Set("kind", x.Kind).Set("on", doc.Get(Addr.NodeActive(x.Node)).AsBool).Set("attachedTo", x.Target).Set("method", x.Method)
                    .Set("scale", R(x.Scale)).Set("headScale", R(x.HeadScale)).Set("needsPlacement", x.NeedsPlacement));
            o.Set("items", items);

            var ward = JNode.Obj();
            foreach (var g in Wardrobe.Parts(s, Wardrobe.WeightsFrom(s)).GroupBy(x => x.Kind))
            {
                var arr = JNode.Arr();
                foreach (var x in g) arr.Add(JNode.Obj().Set("name", x.Name).Set("visible", Wardrobe.Visible(doc, x.Node, x.Renderer)));
                ward.Set(g.Key, arr);
            }
            o.Set("wardrobe", ward);

            var rens = JNode.Arr();
            foreach (var r in s.Renderers)
            {
                var m = s.FindMesh(r.Mesh);
                var it = JNode.Obj().Set("name", SceneSpec.RendererName(s, r)).Set("visible", Wardrobe.Visible(doc, r.Node, r.Id))
                    .Set("materials", Strs(r.Materials.Select(g => s.FindMaterial(g)?.Name ?? g))).Set("shapes", m?.BlendShapes.Count ?? 0).Set("vertices", m?.VertexCount ?? 0);
                var fixes = doc.Get(Addr.MeshEdit(r.Mesh)).Items.Select(x => x["op"].AsString).Distinct().ToList();
                if (fixes.Count > 0) it.Set("meshEdits", Strs(fixes));
                rens.Add(it);
            }
            o.Set("renderers", rens);

            var mats = JNode.Arr();
            foreach (var m in s.Materials)
            {
                var users = s.Renderers.Where(r => r.Materials.Contains(m.Guid)).Select(r => SceneSpec.RendererName(s, r)).Distinct();
                var it = JNode.Obj().Set("name", m.Name).Set("shader", doc.Get(Addr.MatShader(m.Guid)).AsString).Set("usedBy", Strs(users));
                if (doc.IsMaterialModified(m.Guid)) it.Set("changed", true);
                mats.Add(it);
            }
            o.Set("materials", mats);

            var tg = JNode.Arr();
            foreach (var t in doc.Get(Addr.Toggles).Items)
                tg.Add(JNode.Obj().Set("name", t["name"].AsString).Set("objects", Strs(ToggleSet.Nodes(t).Where(n => n >= 0 && n < s.Nodes.Count).Select(n => s.Nodes[n].Name))).Set("on", ToggleSet.IsOn(doc, t)));
            o.Set("toggles", tg);
            o.Set("shapeSets", Strs(ShapeSets.All(doc).Select(v => v.Name)));
            o.Set("expression", FaceExpression.Name(doc.Get(Addr.Expression)));
            o.Set("posed", !doc.Get(Addr.Pose).IsNull);
            return o;
        }

        /// <summary>머티리얼 하나의 지금 값 (.mat에 적힌 프로퍼티 + 바꾼 것). 스키마가 없어 .mat에 없는 프로퍼티의 기본값은 보이지 않는다.</summary>
        public static JNode Material(Document doc, string name)
        {
            var p = new SceneProblems();
            var mats = DocEdit.Materials(doc.Original, JNode.From(name), null, "", p);
            if (p.Errors.Count > 0) return JNode.Obj().Set("ok", false).Set("errors", Strs(p.Errors));
            var list = JNode.Arr();
            foreach (var m in mats)
            {
                var g = m.Guid;
                var shader = doc.Get(Addr.MatShader(g)).AsString;
                var it = JNode.Obj().Set("name", m.Name).Set("guid", g).Set("file", m.Path).Set("shader", shader).Set("renderQueue", doc.Get(Addr.MatRenderQueue(g)).AsInt);
                var lil = LilToonRules.Detect(shader, doc.Get(Addr.MatFloat(g, "_TransparentMode")).AsFloat);
                if (lil != null) it.Set("lilToon", JNode.Obj().Set("renderMode", lil.Mode.ToString()).Set("transparentMode", lil.Transparent.ToString()).Set("outline", lil.Outline));
                var over = doc.Overrides.Keys.Where(a => a.IsMaterial && a.Target == g).ToList();
                var floats = JNode.Obj();
                foreach (var k in m.Floats.Keys.Concat(over.Where(a => a.Kind == AddrKind.MatFloat).Select(a => a.Name)).Distinct().OrderBy(x => x)) floats.Set(k, R(doc.Get(Addr.MatFloat(g, k)).AsDouble));
                var colors = JNode.Obj();
                foreach (var k in m.Colors.Keys.Concat(over.Where(a => a.Kind == AddrKind.MatColor || a.Kind == AddrKind.MatVector).Select(a => a.Name)).Distinct().OrderBy(x => x))
                {
                    var a = over.Where(x => x.Name == k && x.Kind == AddrKind.MatVector).Select(x => (Addr?)x).FirstOrDefault() ?? Addr.MatColor(g, k);
                    colors.Set(k, Arr(doc.Get(a).ToFloatArray()));
                }
                var texs = JNode.Obj();
                foreach (var k in m.TexEnvs.Keys.Concat(over.Where(a => a.Kind == AddrKind.MatTex).Select(a => a.Name)).Distinct().OrderBy(x => x))
                {
                    var key = doc.Get(Addr.MatTex(g, k)).AsStringOrNull;
                    var t = doc.Texture(key);
                    texs.Set(k, key == null ? JNode.Null : JNode.From(t != null ? Path.GetFileName(t.Path) : key));
                }
                var kws = m.Keywords.Concat(over.Where(a => a.Kind == AddrKind.MatKeyword).Select(a => a.Name)).Distinct().Where(k => doc.Get(Addr.MatKeyword(g, k)).AsBool);
                it.Set("floats", floats).Set("colors", colors).Set("textures", texs).Set("keywords", Strs(kws)).Set("changed", Strs(over.Select(a => a.Name.Length > 0 ? a.Name : a.Kind.ToString())));
                list.Add(it);
            }
            return JNode.Obj().Set("ok", true).Set("materials", list);
        }

        /// <summary>블렌드쉐이프 지금 값. find가 있으면 이름에 그 글이 든 것만, 없으면 0이 아닌 것만 (all이면 전부).</summary>
        public static JNode Shapes(Document doc, string? renderer, string? find, bool all)
        {
            var s = doc.Original;
            var p = new SceneProblems();
            var rens = renderer != null ? DocEdit.Renderers(s, JNode.From(renderer), "", p) : s.Renderers;
            if (p.Errors.Count > 0) return JNode.Obj().Set("ok", false).Set("errors", Strs(p.Errors));
            var list = JNode.Arr();
            foreach (var r in rens)
            {
                var m = s.FindMesh(r.Mesh);
                if (m == null || m.BlendShapes.Count == 0) continue;
                var shapes = JNode.Obj();
                foreach (var b in m.BlendShapes.Select(x => x.Name).Distinct())
                {
                    if (find != null && b.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var v = doc.Get(Addr.BlendShape(r.Id, b)).AsDouble;
                    if (find == null && !all && Math.Abs(v) < 1e-4) continue;
                    shapes.Set(b, R(v));
                }
                if (shapes.Count > 0 || renderer != null) list.Add(JNode.Obj().Set("renderer", SceneSpec.RendererName(s, r)).Set("count", m.BlendShapes.Count).Set("shapes", shapes));
            }
            return JNode.Obj().Set("ok", true).Set("renderers", list);
        }

        /// <summary>원본과 다른 값 전부를 읽기 쉬운 이름과 함께 (a = 편집의 addr로 그대로 쓸 수 있는 주소).</summary>
        public static JNode Changes(Document doc)
        {
            var s = doc.Original;
            var list = JNode.Arr();
            foreach (var kv in doc.Overrides.OrderBy(k => k.Key.Kind).ThenBy(k => k.Key.Target).ThenBy(k => k.Key.Name))
            {
                var a = kv.Key;
                string target = a.Target;
                if (a.IsMaterial) target = s.FindMaterial(a.Target)?.Name ?? a.Target;
                else if (a.Kind == AddrKind.BlendShape || a.Kind == AddrKind.RendererEnabled) target = s.FindRenderer(a.Target) is IrRenderer r ? SceneSpec.RendererName(s, r) : a.Target;
                else if ((a.Kind == AddrKind.NodeActive || a.Kind == AddrKind.NodeTransform) && int.TryParse(a.Target, out var n) && n >= 0 && n < s.Nodes.Count) target = s.Nodes[n].Name;
                else if (a.Kind == AddrKind.MeshEdit) target = s.FindMesh(a.Target)?.Name ?? a.Target;
                var what = a.Kind + (target.Length > 0 ? " " + target : "") + (a.Name.Length > 0 ? " " + a.Name : "");
                var v = kv.Value;
                // 자세·표정·메시 편집처럼 큰 값은 줄여 보인다
                var shown = v.Kind == JKind.Object || v.Kind == JKind.Array && v.Count > 10 ? JNode.From($"({(v.Kind == JKind.Array ? v.Count + "개" : "값")})") : v;
                list.Add(JNode.Obj().Set("what", what).Set("a", a.ToString()).Set("value", shown));
            }
            return JNode.Obj().Set("ok", true).Set("count", list.Count).Set("changes", list);
        }
    }
}
