// 창 없는 편집 (Tools/Shoot/DocEdit.cs, 2026-11-02)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static Document EditDoc()
    {
        var s = new IrScene { Name = "t" };
        s.Nodes.Add(new IrNode { Id = 0, Name = "t" });
        s.Nodes.Add(new IrNode { Id = 1, Name = "Body", Parent = 0 });
        s.Nodes.Add(new IrNode { Id = 2, Name = "Hat", Parent = 0, Position = new float[] { 0, 1, 0 } });
        s.Nodes.Add(new IrNode { Id = 3, Name = "Brim", Parent = 0 });
        s.Meshes.Add(new IrMesh { Id = "m", Name = "m", BlendShapes = { new IrBlendShape { Name = "smile" }, new IrBlendShape { Name = "eye_big" } } });
        s.Meshes.Add(new IrMesh { Id = "h", Name = "h" });
        s.Materials.Add(new IrMaterial { Guid = "g1", Name = "Skin", Floats = { ["_Cutoff"] = 0.5f }, Colors = { ["_Color"] = new float[] { 1, 1, 1, 0.5f }, ["_MainTexHSVG"] = new float[] { 0, 1, 1, 1 } },
            TexEnvs = { ["_MainTex"] = new IrTexEnv { Texture = "t1" } } });
        s.Materials.Add(new IrMaterial { Guid = "g2", Name = "HatMat" });
        s.Textures.Add(new IrTexture { Guid = "t1", Path = "Assets/Tex/skin.png" });
        s.Textures.Add(new IrTexture { Guid = "t2", Path = "Assets/Tex/skin_tan.png" });
        s.Renderers.Add(new IrRenderer { Id = "r", Node = 1, Mesh = "m", Materials = { "g1" }, BlendShapeWeights = { 0, 0 } });
        s.Renderers.Add(new IrRenderer { Id = "rh", Node = 2, Mesh = "h", Materials = { "g2" } });
        s.Outfits.Add(new IrOutfit { Node = 2, Name = "Cap", Prefab = "pf", Kind = "accessory", Parts = { 3 } });
        var doc = new Document(s, "t.ir.json");
        // g1만 렌더러가 스키마를 보냈다: _MainTexHSVG는 Vector
        doc.SetSchemas(new[] { new MaterialSchema { Guid = "g1", Shader = "x", Supported = true, Props = {
            new ShaderPropInfo { Name = "_Color", Type = "Color" }, new ShaderPropInfo { Name = "_MainTexHSVG", Type = "Vector" },
            new ShaderPropInfo { Name = "_Cutoff", Type = "Range" }, new ShaderPropInfo { Name = "_MainTex", Type = "Texture" }, new ShaderPropInfo { Name = "_EmissionColor", Type = "Color", Default = new float[] { 0, 0, 0, 1 } } } } }, true);
        return doc;
    }

    static (List<string> Done, SceneProblems P) Edit(Document doc, string json)
    {
        var p = new SceneProblems();
        var done = DocEdit.Apply(doc, DocEdit.List(JNode.Parse(json)), new EditEnv { BaseDir = Path.GetTempPath() }, p);
        return (done, p);
    }

    static void DocEditBasics()
    {
        // 머티리얼: 스키마로 종류를 정하고, 색은 #RRGGBB면 알파를 남긴다
        var doc = EditDoc();
        var (done, p) = Edit(doc, @"{ ""edits"": [ { ""material"": ""skin"", ""set"": { ""_Color"": ""#ff0000"", ""_MainTexHSVG"": [0.1, 1, 1, 1], ""_Cutoff"": 0.25, ""_MainTex"": ""skin_tan"", ""_MainTex_ST"": [2, 2, 0, 0], ""_EmissionColor"": [0, 1, 0] } } ] }");
        Check(p.Errors.Count == 0, "material edit: " + string.Join("; ", p.Errors));
        Check(done.Count == 1, "material edit done line");
        var c = doc.Get(Addr.MatColor("g1", "_Color")).ToFloatArray();
        Check(c.Length == 4 && c[0] == 1 && c[1] == 0 && c[3] == 0.5f, "hex color keeps alpha");
        Check(doc.IsModified(Addr.MatVector("g1", "_MainTexHSVG")) && !doc.IsModified(Addr.MatColor("g1", "_MainTexHSVG")), "schema says vector");
        Check(doc.Get(Addr.MatFloat("g1", "_Cutoff")).AsFloat == 0.25f, "float prop");
        Check(doc.Get(Addr.MatTex("g1", "_MainTex")).AsString == "t2", "texture by file name");
        Check(doc.Get(Addr.MatTexST("g1", "_MainTex")).ToFloatArray()[0] == 2, "texture tiling");
        Check(doc.Get(Addr.MatColor("g1", "_EmissionColor")).ToFloatArray()[3] == 1, "[r, g, b] keeps the shader default alpha");

        // 틀린 이름: 비슷한 이름과 함께, 하나라도 틀리면 오류 (저장은 부른 쪽이 막는다)
        (_, p) = Edit(doc, @"[ { ""material"": ""Skn"", ""set"": {} }, { ""material"": ""Skin"", ""set"": { ""_Colr"": ""#fff"" } }, { ""material"": ""Skin"", ""sett"": {} }, { ""jump"": 1 } ]");
        Check(p.Errors.Any(e => e.Contains("Skn") && e.Contains("Skin")), "unknown material suggests");
        Check(p.Errors.Any(e => e.Contains("_Colr") && e.Contains("_Color")), "unknown prop suggests");
        Check(p.Errors.Any(e => e.Contains("sett") && e.Contains("set")), "unknown key suggests");
        Check(p.Errors.Any(e => e.Contains("무엇을 할지")), "unknown edit");
        (_, p) = Edit(doc, @"[ { ""material"": ""Skin"", ""renderMode"": ""cutout"" } ]");
        Check(p.Errors.Any(e => e.Contains("lilToon")), "render mode needs lilToon");

        // 스키마가 없는 머티리얼은 .mat 값으로만, renderer로 고르면 그 메시의 것만
        (_, p) = Edit(doc, @"[ { ""material"": ""*"", ""renderer"": ""Hat"", ""set"": { ""_Cutoff"": 1 } } ]");
        Check(p.Errors.Any(e => e.Contains("HatMat") && e.Contains("_Cutoff")), "no schema → only .mat props");

        // 블렌드쉐이프: 메시를 적지 않으면 그 이름을 가진 메시를 찾는다
        (_, p) = Edit(doc, @"[ { ""shapes"": { ""smile"": 70 } }, { ""shapes"": { ""eye_bg"": 10 }, ""renderer"": ""Body"" } ]");
        Check(doc.Get(Addr.BlendShape("r", "smile")).AsFloat == 70, "shape without renderer");
        Check(p.Errors.Count == 1 && p.Errors[0].Contains("eye_big"), "unknown shape suggests");

        // 켜고 끄기: 아이템 이름이면 Bone Proxy 부분까지, 메시 이름이면 렌더러까지
        doc = EditDoc();
        (_, p) = Edit(doc, @"[ { ""hide"": [""Cap""] } ]");
        Check(!doc.Get(Addr.NodeActive(2)).AsBool && !doc.Get(Addr.NodeActive(3)).AsBool, "hide item hides parts");
        doc.Set(Addr.RendererEnabled("r"), false);
        (_, p) = Edit(doc, @"[ { ""show"": ""Cap"" }, { ""show"": [""Body""] } ]");
        Check(doc.Get(Addr.NodeActive(2)).AsBool && doc.Get(Addr.NodeActive(3)).AsBool, "show item");
        Check(doc.Get(Addr.RendererEnabled("r")).AsBool, "show mesh turns renderer on");
        (_, p) = Edit(doc, @"[ { ""wardrobe"": { ""hat"": false } } ]");
        Check(p.Errors.Any(e => e.Contains("hair")), "unknown wardrobe kind");

        // 토글·옮기기·되돌리기·주소
        (_, p) = Edit(doc, @"[ { ""toggles"": [ { ""name"": ""모자"", ""objects"": [""Hat"", ""Brim""] } ] }, { ""move"": ""Hat"", ""nudge"": [0, 0.1, 0], ""rotation"": [0, 90, 0], ""scale"": 2 },
            { ""addr"": ""BlendShape|r|eye_big"", ""value"": 30 } ]");
        Check(p.Errors.Count == 0, "toggles/move/addr: " + string.Join("; ", p.Errors));
        var tg = doc.Get(Addr.Toggles);
        Check(tg.Count == 1 && ToggleSet.Nodes(tg[0]).SequenceEqual(new[] { 2, 3 }), "toggle by object names");
        var t = doc.Get(Addr.NodeTransform(2)).ToFloatArray();
        Check(Math.Abs(t[1] - 1.1f) < 1e-5 && Math.Abs(t[4] - 0.7071f) < 1e-3 && t[7] == 2, "move nudges, rotates, scales");
        Check(doc.Get(Addr.BlendShape("r", "eye_big")).AsFloat == 30, "raw address");
        (_, p) = Edit(doc, @"[ { ""reset"": [""moves"", ""shapes""] } ]");
        Check(!doc.IsModified(Addr.NodeTransform(2)) && !doc.IsModified(Addr.BlendShape("r", "eye_big")) && doc.IsModified(Addr.Toggles), "reset kinds");

        // 렌더러가 필요한 편집은 없을 때 오류로
        (_, p) = Edit(doc, @"[ { ""blinkFix"": true }, { ""pose"": ""stand"" }, { ""weld"": 0.1 } ]");
        Check(p.Errors.Count == 3, "needs renderer/meshes");

        // 아이템 편집은 여기서 건너뛴다 (서버가 IR을 다시 만든다)
        (done, p) = Edit(doc, @"[ { ""removeItem"": ""Cap"" } ]");
        Check(p.Errors.Count == 0 && done.Count == 0 && DocEdit.IsItemOp(JNode.Parse(@"{ ""fitHead"": ""Cap"" }")), "item ops skipped");

        // 보기
        doc.Set(Addr.MatFloat("g1", "_Cutoff"), JNode.From(0.75f));
        var sum = DocLook.Summary(doc);
        Check(sum["items"].Count == 1 && sum["items"][0]["name"].AsString == "Cap" && sum["renderers"].Count == 2, "look summary");
        Check(sum["materials"].Items.First(m => m["name"].AsString == "Skin")["changed"].AsBool, "look marks changed material");
        var mat = DocLook.Material(doc, "Skin")["materials"][0];
        Check(mat["floats"]["_Cutoff"].AsDouble == 0.75 && mat["textures"]["_MainTex"].AsString == "skin.png" && mat["colors"]["_Color"].Count == 4, "look material");
        Check(DocLook.Material(doc, "nope")["errors"].Count == 1, "look unknown material");
        doc.Set(Addr.BlendShape("r", "smile"), JNode.From(40f));
        var sh = DocLook.Shapes(doc, null, null, false)["renderers"];
        Check(sh.Count == 1 && sh[0]["shapes"].Count == 1 && sh[0]["shapes"]["smile"].AsDouble == 40, "look shapes nonzero");
        Check(DocLook.Shapes(doc, "Body", "eye", false)["renderers"][0]["shapes"].Has("eye_big"), "look shapes find");
        var ch = DocLook.Changes(doc);
        Check(ch["count"].AsInt == doc.Overrides.Count && ch["changes"].Items.Any(x => x["what"].AsString.Contains("Skin _Cutoff")), "look changes");
        // 보기의 주소를 그대로 편집에 쓸 수 있다
        var a = ch["changes"].Items.First(x => x["what"].AsString.Contains("smile"))["a"].AsString;
        (_, p) = Edit(doc, $@"[ {{ ""addr"": ""{a}"", ""value"": 5 }} ]");
        Check(doc.Get(Addr.BlendShape("r", "smile")).AsFloat == 5, "look address round trip");
    }
}
