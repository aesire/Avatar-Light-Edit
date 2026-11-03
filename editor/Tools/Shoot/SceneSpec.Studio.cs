// 장면 파일: 스튜디오 탭의 나머지 (2026-11-01) — 스티커 · 표정·트래킹·말하기 · 블렌드쉐이프 단축키 · 표정 단축키 · 오버레이 · 달리 녹화.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static partial class SceneSpec
    {
        static readonly string[] TrackingKeys = { "source", "port", "camera", "path", "mirror", "smoothing", "fill", "gain", "head", "body", "arms", "move", "eye", "groups", "neutral", "calibrate", "wait", "freeze", "recordAnim", "test" };
        static readonly string[] OverlayKeys = { "corner", "size", "aspect", "top", "fps", "supersample", "adjust", "pos", "monitor" };
        static readonly string[] OverlayCorners = { "br", "bl", "tr", "tl", "center" };

        /// <summary>studioPreset: 경로 또는 이름. 프리셋 값을 장면 항목으로 바꿔 깔고 그 위에 장면 값을 덮는다.</summary>
        static JNode WithStudioPreset(JNode s, SceneContext ctx, string where, SceneProblems p)
        {
            if (!s.Has("studioPreset")) return s;
            var name = s["studioPreset"].AsString;
            var path = Full(StudioPreset.WithExtension(name), ctx);
            if (!File.Exists(path) && ctx.PresetDir != null) path = Path.Combine(ctx.PresetDir, StudioPreset.WithExtension(name));
            JNode v;
            try { v = StudioPreset.Load(path); }
            catch (Exception e) when (e is IOException || e is InvalidDataException) { p.Error($"{where}스튜디오 프리셋을 읽지 못했어요: \"{name}\" ({e.Message})"); return s; }
            var o = JNode.Obj();
            if (v.Has("background"))
            {
                var bg = v["background"].Clone();
                if (bg["mode"].AsString is not ("image" or "panorama")) bg.Remove("path");
                if (v.Has("exposure")) bg.Set("exposure", v["exposure"]);
                if (v.Has("rotation")) bg.Set("rotation", v["rotation"]);
                o.Set("background", bg);
            }
            if (v.Has("floor")) o.Set("floor", v["floor"]["enabled"].AsBool ? JNode.Obj().Set("mode", v["floor"]["mode"]).Set("strength", v["floor"]["strength"]).Set("color", v["floor"]["color"]) : JNode.From("none"));
            if (v.Has("props")) o.Set("props", v["props"]);
            if (v.Has("dof") && v["dof"]["enabled"].AsBool) { var d = v["dof"].Clone(); d.Remove("enabled"); o.Set("dof", d); }
            if (v.Has("quality")) o.Set("quality", v["quality"]);
            if (v["avatarOnly"]["on"].AsBool) o.Set("avatarOnly", ColorText(v["avatarOnly"]["color"]));
            if (v.Has("transparent")) o.Set("transparent", v["transparent"]);
            if (v.Has("adjust")) o.Set("adjust", v["adjust"]);
            if (v["shot"].Has("width")) { o.Set("width", v["shot"]["width"]).Set("height", v["shot"]["height"]); }
            var merged = Merge(o, s);
            // 장면이 size를 적었으면 프리셋의 크기를 쓰지 않는다 (width·height가 size를 덮으므로)
            if (s.Has("size")) { merged.Remove("width"); merged.Remove("height"); if (s.Has("width")) merged.Set("width", s["width"]); if (s.Has("height")) merged.Set("height", s["height"]); }
            merged.Remove("studioPreset");
            return merged;
        }

        static JNode ColorText(JNode c)
        {
            if (c.Kind != JKind.Array || c.Count < 3) return JNode.From("#00FF00");
            int B(int i) => (int)Math.Round(Math.Clamp(c[i].AsDouble, 0, 1) * 255);
            return JNode.From($"#{B(0):X2}{B(1):X2}{B(2):X2}");
        }

        static void StudioTabs(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            shot.QualityStudio = !s["quality"].Has("studio") || s["quality"]["studio"].AsBool;
            shot.EmbedSession = s["embedSession"].AsBool;
            if (s.Has("saveExpression")) shot.SaveExpression = Full(s["saveExpression"].AsString, ctx);
            if (s.Has("saveStudioPreset")) shot.SaveStudioPreset = Full(StudioPreset.WithExtension(s["saveStudioPreset"].AsString), ctx);
            Sticker_(s["sticker"], shot, where, p);
            Tracking(s["tracking"], shot, ctx, where, p);
            Lip(s["lip"], shot, ctx, where, p);
            ReadFaceMap(s, shot, ctx, where, p);
            ReadShapeKeys(s, shot, ctx, where, p);
            foreach (var kv in s["expressionKeys"].Props)
            {
                int slot = kv.Key.Length == 2 && (kv.Key[0] == 'F' || kv.Key[0] == 'f') ? kv.Key[1] - '1' : -1;
                if (slot < 0 || slot > 8) { p.Error($"{where}expressionKeys는 F1~F9만 돼요: {kv.Key}"); continue; }
                shot.ExpressionKeys[slot] = Expression(kv.Value, ctx, where, p);
            }
            Overlay_(s["overlay"], shot, where, p);
            Record(s, shot, ctx, where, p);
        }

        // ---------------- 스티커 ----------------
        static void Sticker_(JNode n, SceneShot shot, string where, SceneProblems p)
        {
            if (n.IsNull || n.Kind == JKind.Bool && !n.AsBool) return;
            var o = n.Kind == JKind.String ? JNode.Obj().Set("spec", n.AsString) : n.Kind == JKind.Object ? n : JNode.Obj();
            Unknown(o, new[] { "spec", "outline", "color" }, where, "sticker.", p);
            var spec = o.Has("spec") ? o["spec"].AsString : "line";
            if (!Sticker.Specs.Any(x => x.Key == spec)) p.Error($"{where}스티커 규격을 모르겠어요: \"{spec}\"{Suggest(spec, Sticker.Specs.Select(x => x.Key))} (쓸 수 있는 것: {string.Join(", ", Sticker.Specs.Select(x => x.Key))})");
            var color = o.Has("color") ? o["color"].AsString : "white";
            if (color != "white" && color != "black" && color != "none") p.Error($"{where}sticker.color는 white·black·none만 돼요: {color}");
            shot.Sticker = JNode.Obj().Set("spec", spec).Set("outline", o.Has("outline") ? Math.Clamp(o["outline"].AsInt, 0, 30) : 6).Set("color", color);
        }

        // ---------------- 트래킹·말하기 ----------------
        static void Tracking(JNode n, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            if (n.IsNull) return;
            var o = n.Kind == JKind.String ? JNode.Obj().Set("source", n.AsString) : n.Kind == JKind.Object ? n.Clone() : JNode.Obj();
            Unknown(o, TrackingKeys, where, "tracking.", p);
            var src = o.Has("source") ? o["source"].AsString : "off";
            if (!new[] { "off", "webcam", "vmc", "osc", "image" }.Contains(src)) p.Error($"{where}tracking.source는 off·webcam·vmc·osc·image만 돼요: {src}");
            if (src == "image")
            {
                var path = Full(o["path"].AsString, ctx);
                if (!File.Exists(path)) p.Error($"{where}tracking.path 사진이 없어요: {path}");
                o.Set("path", path);
            }
            if (o["recordAnim"].Kind == JKind.Object) o["recordAnim"].Set("path", Full(o["recordAnim"]["path"].AsString, ctx));
            else if (o.Has("recordAnim")) p.Error($"{where}tracking.recordAnim은 {{ \"path\", \"seconds\" }}예요");
            if (src == "off" && (o["freeze"].AsBool || o["calibrate"].AsBool || o.Has("recordAnim"))) p.Error($"{where}freeze·calibrate·recordAnim은 tracking.source를 켜야 돼요");
            shot.Tracking = o;
        }

        static void Lip(JNode n, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            if (n.IsNull || n.Kind == JKind.Bool && !n.AsBool) return;
            var o = n.Kind == JKind.String ? JNode.Obj().Set("wav", n.AsString) : n.Kind == JKind.Object ? n.Clone() : JNode.Obj();
            Unknown(o, new[] { "wav", "mic", "gain" }, where, "lip.", p);
            if (o.Has("wav"))
            {
                var w = Full(o["wav"].AsString, ctx);
                if (!File.Exists(w)) p.Error($"{where}lip.wav 파일이 없어요: {w}");
                o.Set("wav", w);
            }
            shot.Lip = o.Set("on", true);
        }

        static void ReadFaceMap(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            shot.FaceMap = ctx.BaseFaceMap;
            var n = s["faceMap"];
            if (n.IsNull) return;
            Unknown(n, new[] { "add", "off" }, where, "faceMap.", p);
            var map = ctx.BaseFaceMap.Clone();
            void Each(JNode item, Action<string, string, string> act)
            {
                var src = item["src"].AsString; var shape = item["shape"].AsString;
                if (!FaceMap.Sources.Any(x => x.Name == src)) p.Warn($"{where}faceMap: 목록에 없는 트래킹 값이에요: \"{src}\"{Suggest(src, FaceMap.Sources.Select(x => x.Name))}");
                int found = 0;
                foreach (var r in ctx.Scene.Renderers)
                {
                    var m = ctx.Scene.FindMesh(r.Mesh);
                    if (m == null || m.BlendShapeIndex(shape) < 0) continue;
                    act(src, r.Id, shape); found++;
                }
                if (found == 0) p.Error($"{where}faceMap: 블렌드쉐이프를 모르겠어요: \"{shape}\"{Suggest(shape, ctx.Scene.Meshes.SelectMany(m => m.BlendShapes).Select(b => b.Name).Distinct())}");
            }
            foreach (var a in n["add"].Items) Each(a, (src, r, sh) => map = FaceMap.Add(map, src, r, sh, a.Has("k") ? a["k"].AsFloat : 1));
            foreach (var a in n["off"].Items) Each(a, (src, r, sh) => map = FaceMap.Remove(map, src, r, sh, true));
            shot.FaceMap = map;
        }

        // ---------------- 블렌드쉐이프 단축키 ----------------
        /// <summary>
        /// 항목: 이름(지금 정의·아바타 메뉴에서 찾아 켠다) 또는 { name, menu?, shapes?: { 이름: 값 | { on, off } }, mode, group, steps, key, level }.
        /// level: 0 = 정의만, toggle 1 = 켬, cycle = 단계 번호. 켠 값은 표정 위의 단축키 레이어로, 정의는 세션에 저장한다.
        /// </summary>
        static void ReadShapeKeys(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            var defs = new List<JNode>(ctx.BaseShapeKeys.Items.Select(x => x.Clone()));
            var levels = new Dictionary<string, int>();
            foreach (var item in s["shapeKeys"].Items)
            {
                var o = item.Kind == JKind.String ? JNode.Obj().Set("name", item.AsString) : item;
                Unknown(o, new[] { "name", "menu", "shapes", "mode", "group", "steps", "key", "level" }, where, "shapeKeys.", p);
                var name = o["name"].AsString;
                JNode? def = null;
                if (o.Has("shapes"))
                {
                    var list = new List<(string, string, float, float)>();
                    foreach (var kv in o["shapes"].Props)
                    {
                        float on = kv.Value.Kind == JKind.Object ? kv.Value["on"].AsFloat : kv.Value.AsFloat, off = kv.Value["off"].AsFloat;
                        int found = 0;
                        foreach (var r in ctx.Scene.Renderers)
                        {
                            var m = ctx.Scene.FindMesh(r.Mesh);
                            if (m == null || m.BlendShapeIndex(kv.Key) < 0) continue;
                            list.Add((r.Id, kv.Key, on, off)); found++;
                        }
                        if (found == 0) p.Error($"{where}shapeKeys: 블렌드쉐이프를 모르겠어요: \"{kv.Key}\"{Suggest(kv.Key, ctx.Scene.Meshes.SelectMany(m => m.BlendShapes).Select(b => b.Name).Distinct())}");
                    }
                    def = ShapeKeys.Make(name, o["key"].AsString, o["mode"].AsString, o["group"].AsString, list, o["steps"].Kind == JKind.Array ? o["steps"].ToFloatArray() : null);
                }
                else
                {
                    var menu = o.Has("menu") ? o["menu"].AsString : name;
                    if (!defs.Any(d => d["name"].AsString == name) || o.Has("menu"))
                    {
                        var ms = ctx.Scene.MenuShapes.FirstOrDefault(x => x.Name == menu);
                        if (ms == null) { p.Error($"{where}shapeKeys: 아바타 메뉴에서 찾지 못했어요: \"{menu}\"{Suggest(menu, ctx.Scene.MenuShapes.Select(x => x.Name).Concat(defs.Select(d => d["name"].AsString)))}"); continue; }
                        def = ShapeKeys.FromMenu(ctx.Scene, ms);
                        if (o.Has("name")) def.Set("name", name);
                    }
                    if (o.Has("key") && def != null) def.Set("key", o["key"].AsString);
                }
                if (def != null)
                {
                    int at = defs.FindIndex(d => d["name"].AsString == name);
                    if (at >= 0) defs[at] = def; else defs.Add(def);
                }
                levels[name] = o.Has("level") ? o["level"].AsInt : 1;
            }
            var arr = JNode.Arr();
            foreach (var d in defs) arr.Add(d);
            shot.ShapeKeyDefs = arr;
            var run = new ShapeKeyRunner();
            run.Sync(arr);
            for (int i = 0; i < arr.Count; i++)
                if (levels.TryGetValue(arr[i]["name"].AsString, out var lv)) run.Set(i, lv);
            shot.ShapeLayer = run.Message();
        }

        // ---------------- 오버레이 ----------------
        static void Overlay_(JNode n, SceneShot shot, string where, SceneProblems p)
        {
            var o = n.Kind == JKind.Object ? n : JNode.Obj();
            Unknown(o, OverlayKeys, where, "overlay.", p);
            var corner = o.Has("corner") ? o["corner"].AsString : "br";
            if (!OverlayCorners.Contains(corner)) p.Error($"{where}overlay.corner는 {string.Join("·", OverlayCorners)}만 돼요: {corner}");
            double aspect = 0.75;
            if (o["aspect"].Kind == JKind.Number) aspect = o["aspect"].AsDouble;
            else if (o["aspect"].Kind == JKind.String)
            {
                var a = o["aspect"].AsString.Split(':');
                if (a.Length == 2 && double.TryParse(a[0], out var w) && double.TryParse(a[1], out var h) && h > 0) aspect = w / h;
                else p.Error($"{where}overlay.aspect를 읽지 못했어요: {o["aspect"].AsString} (예: 3:4, 16:9)");
            }
            shot.Overlay = JNode.Obj().Set("corner", corner).Set("size", o.Has("size") ? Math.Clamp(o["size"].AsDouble, 0.15, 1) : 0.45).Set("aspect", Math.Clamp(aspect, 0.2, 5))
                .Set("top", !o.Has("top") || o["top"].AsBool).Set("fps", o.Has("fps") ? o["fps"].AsInt : 30).Set("supersample", o.Has("supersample") ? Math.Clamp(o["supersample"].AsDouble, 1, 2) : 1)
                .Set("adjust", o["adjust"].AsBool).Set("monitor", o["monitor"].AsInt);
            if (o["pos"].Count == 2) shot.Overlay.Set("pos", o["pos"]);
        }

        // ---------------- 녹화 ----------------
        static void Record(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            if (s["record"].Kind != JKind.Object) return;
            var r = s["record"];
            Unknown(r, new[] { "seconds", "fps", "mode", "motion", "to", "turns", "format" }, where, "record.", p);
            var motion = r.Has("motion") ? r["motion"].AsString : r.Has("mode") ? r["mode"].AsString : r.Has("to") ? "fixed" : "turntable";
            if (!new[] { "fixed", "turntable", "in", "out" }.Contains(motion)) p.Error($"{where}record.motion은 fixed·turntable·in·out만 돼요: {motion}");
            var fmt = r.Has("format") ? r["format"].AsString : "mp4";
            if (fmt != "mp4" && fmt != "png") p.Error($"{where}record.format은 mp4·png만 돼요: {fmt}");
            if (fmt == "mp4" && shot.Transparent) p.Error($"{where}투명 영상은 record.format을 png로 해 주세요");
            shot.Record = JNode.Obj().Set("seconds", r.Has("seconds") ? r["seconds"].AsDouble : 6).Set("fps", r.Has("fps") ? r["fps"].AsInt : 30)
                .Set("mode", motion == "turntable" ? "turntable" : "fixed").Set("motion", motion).Set("turns", r.Has("turns") ? r["turns"].AsDouble : 1).Set("format", fmt);
            if (r.Has("to")) shot.Record.Set("to", Merge(shot.Camera, Camera(r["to"], ctx, where, p)));
            if (!shot.Sticker.IsNull) p.Error($"{where}sticker와 record는 함께 쓸 수 없어요");
        }
    }
}
