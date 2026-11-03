// 업로드 준비 (Core.Vpm, Core.UploadPlan) 테스트
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static void VpmVersions()
    {
        bool S(string range, string v) => VersionRange.Parse(range).Satisfies(SemVer.Parse(v));
        Check(SemVer.Parse("1.0.0").CompareTo(SemVer.Parse("1.0.0-beta.2")) > 0, "release > prerelease");
        Check(SemVer.Parse("1.0.0-beta.10").CompareTo(SemVer.Parse("1.0.0-beta.2")) > 0, "prerelease numeric compare");
        Check(SemVer.Parse("3.10.5").CompareTo(SemVer.Parse("3.9.9")) > 0, "minor numeric");
        Check(S("3.1.11", "3.1.11") && !S("3.1.11", "3.1.12"), "exact");
        Check(S(">=3.4.0", "3.10.5") && !S(">=3.4.0", "3.3.9"), ">=");
        Check(S(">=1.4.0-rc.0 <2.0.0-a", "1.9.4") && !S(">=1.4.0-rc.0 <2.0.0-a", "2.0.0"), "range with prerelease bounds");
        Check(S("^1.5.0", "1.14.8") && !S("^1.5.0", "2.0.0") && !S("^1.5.0", "1.4.9"), "caret");
        Check(S("^0.3.1", "0.3.9") && !S("^0.3.1", "0.4.0"), "caret 0.x");
        Check(S("~1.2.3", "1.2.9") && !S("~1.2.3", "1.3.0"), "tilde");
        Check(S("^1.10.0 || ^2.0.0", "2.3.4") && S("^1.10.0 || ^2.0.0", "1.10.2") && !S("^1.10.0 || ^2.0.0", "1.9.0"), "or");
        Check(S("^3.1.x", "3.5.0") && S("3.1.x", "3.1.7") && !S("3.1.x", "3.2.0"), "x wildcard");
        Check(!S("^1.5.0", "1.6.0-beta.1"), "prerelease excluded unless named");
        Check(S(">=3.7.0 <3.11.0", "3.10.5") && !S(">=3.7.0 <3.11.0", "3.11.0"), "AAO sdk range");
        Check(S("", "9.9.9") && S("*", "1.0.0"), "any");
        Check(S(">= 1.0", "1.2.0"), "spaced operator");

        string Repo(string id, string body) => "\uFEFF{\"id\":\"" + id + "\",\"url\":\"u\",\"packages\":{" + body + "}}";
        string Pkg(string id, params (string v, string deps, string unity)[] vs) =>
            "\"" + id + "\":{\"versions\":{" + string.Join(",", vs.Select(x => $"\"{x.v}\":{{\"name\":\"{id}\",\"version\":\"{x.v}\",\"url\":\"https://x/{id}-{x.v}.zip\",\"unity\":\"{x.unity}\",\"vpmDependencies\":{{{x.deps}}}}}")) + "}}";
        var vrc = VpmRepo.Parse(Repo("vrc", Pkg("com.vrchat.base", ("3.10.5", "", "2022.3"), ("3.11.0-beta.1", "", "2022.3"), ("3.9.0", "", "2022.3"))
            + "," + Pkg("com.vrchat.avatars", ("3.10.5", "\"com.vrchat.base\":\"3.10.5\"", "2022.3"), ("3.9.0", "\"com.vrchat.base\":\"3.9.0\"", "2022.3"), ("4.0.0", "\"com.vrchat.base\":\"3.10.5\"", "6000.0"))));
        var ma = VpmRepo.Parse(Repo("ma", Pkg("nadena.dev.modular-avatar", ("1.13.0", "\"com.vrchat.avatars\":\">=3.4.0\",\"nadena.dev.ndmf\":\">=1.4.0 <2.0.0-a\"", "2022.3"), ("1.14.0-rc.1", "\"nadena.dev.ndmf\":\"^1.5\"", "2022.3"))
            + "," + Pkg("nadena.dev.ndmf", ("1.9.4", "", "2022.3"), ("2.0.0", "", "2022.3"))));
        var aao = VpmRepo.Parse(Repo("aao", Pkg("com.anatawa12.avatar-optimizer", ("1.9.20", "\"com.vrchat.avatars\":\">=3.7.0 <3.10.0\",\"nadena.dev.ndmf\":\">=1.8.0 <2.0.0\"", "2022.3"))));
        Eq(vrc.Packages["com.vrchat.base"][0].Version, "3.11.0-beta.1", "versions sorted newest first (BOM ok)");

        var (p1, e1) = VpmResolver.Resolve(new Dictionary<string, string> { ["com.vrchat.avatars"] = "" }, new[] { vrc }, "2022.3");
        Eq(string.Join(",", p1.Select(x => x.Id + "@" + x.Version)), "com.vrchat.avatars@3.10.5,com.vrchat.base@3.10.5", "SDK: newest stable for Unity 2022.3, deps");
        Eq(e1.Count, 0, "no errors");

        var (p2, e2) = VpmResolver.Resolve(new Dictionary<string, string> { ["com.vrchat.avatars"] = "", ["nadena.dev.modular-avatar"] = "" }, new[] { vrc, ma }, "2022.3");
        Eq(string.Join(",", p2.Select(x => x.Id + "@" + x.Version)), "com.vrchat.avatars@3.10.5,com.vrchat.base@3.10.5,nadena.dev.modular-avatar@1.13.0,nadena.dev.ndmf@1.9.4", "MA + NDMF range");

        // AAO가 SDK를 3.10 미만으로 묶으면 SDK가 3.9.0으로 내려가고 base도 따라간다
        var (p3, e3) = VpmResolver.Resolve(new Dictionary<string, string> { ["com.vrchat.avatars"] = "", ["com.anatawa12.avatar-optimizer"] = "" }, new[] { vrc, ma, aao }, "2022.3");
        Eq(string.Join(",", p3.Select(x => x.Id + "@" + x.Version)), "com.anatawa12.avatar-optimizer@1.9.20,com.vrchat.avatars@3.9.0,com.vrchat.base@3.9.0,nadena.dev.ndmf@1.9.4", "constraint lowers SDK, base follows");
        Eq(e3.Count, 0, "AAO resolves");

        var (_, e4) = VpmResolver.Resolve(new Dictionary<string, string> { ["nope"] = "" }, new[] { vrc }, "2022.3");
        Check(e4.Any(e => e.Contains("nope")), "missing package reported");

        var man = VpmResolver.Manifest(p2, new[] { "com.vrchat.avatars", "nadena.dev.modular-avatar" });
        Eq(man["dependencies"]["com.vrchat.avatars"]["version"].AsString, "3.10.5", "vpm-manifest dependencies");
        Eq(man["locked"]["nadena.dev.modular-avatar"]["dependencies"]["nadena.dev.ndmf"].AsString, ">=1.4.0 <2.0.0-a", "vpm-manifest locked deps");
    }

    static void UploadPlanBuild()
    {
        // 0 Avatar ─ 1 Armature ─ 2 Hips ; 0 ─ 3 Body(렌더러) ; 0 ─ 4 Acc ; 0 ─ 5 Acc (같은 이름) ; 6 Hat(의상, 부모 2)
        var s = IrScene.FromJson(JNode.Parse(@"{
          ""irVersion"": 1, ""name"": ""Ava"", ""buffers"": """",
          ""source"": { ""guid"": ""avguid"", ""path"": ""Assets/A/Ava.prefab"", ""sources"": [ ""C:/x/a.unitypackage"" ], ""root"": ""123"" },
          ""nodes"": [ { ""id"": 0, ""name"": ""Ava"" }, { ""id"": 1, ""name"": ""Armature"", ""parent"": 0 }, { ""id"": 2, ""name"": ""Hips"", ""parent"": 1 },
                       { ""id"": 3, ""name"": ""Body"", ""parent"": 0 }, { ""id"": 4, ""name"": ""Acc"", ""parent"": 0 }, { ""id"": 5, ""name"": ""Acc"", ""parent"": 0 },
                       { ""id"": 6, ""name"": ""Hat"", ""parent"": 2, ""position"": [0, 0.1, 0] } ],
          ""meshes"": [ { ""id"": ""mg:1"", ""name"": ""Body"", ""vertexCount"": 4 } ],
          ""renderers"": [ { ""id"": ""r1"", ""node"": 3, ""mesh"": ""mg:1"", ""materials"": [ ""m1"" ] } ],
          ""materials"": [ { ""guid"": ""m1"", ""name"": ""Skin"", ""shaderGuid"": ""df12117ecd77c31469c224178886498e"" } ],
          ""textures"": [ { ""guid"": ""t9"", ""path"": ""Assets/T.png"", ""file"": ""C:/x/T.png"" } ],
          ""outfits"": [ { ""prefab"": ""hatguid"", ""kind"": ""accessory"", ""method"": ""manual"", ""target"": ""Hips"", ""node"": 6, ""name"": ""Hat"" },
                         { ""prefab"": ""coat"", ""kind"": ""outfit"", ""method"": ""ma-merge"", ""node"": 4, ""name"": ""Acc"" } ] }"));
        Eq(UploadPlan.NodePath(s, 2), "Armature/Hips", "path under root");
        Eq(UploadPlan.NodePath(s, 5), "Acc[1]", "same-name sibling index");
        Eq(UploadPlan.NodePath(s, 0), "", "root itself");
        var doc = new Document(s, "");
        doc.Set(Addr.MatFloat("m1", "_Cutoff"), 0.3);
        doc.Set(Addr.MatTex("m1", "_MainTex"), "t9");
        doc.Set(Addr.BlendShape("r1", "Smile"), 50);
        doc.Set(Addr.NodeActive(5), false);
        doc.Set(Addr.NodeActive(6), false);   // 아이템 노드는 outfits.active로
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(MeshEdits.WeldOp(0.0001f)));
        doc.Set(Addr.Pose, JNode.Obj().Set("muscles", JNode.Obj()));   // 업로드에 들어가지 않음
        var j = UploadPlan.Build(doc, "My Ava", "C:/t.png");
        Eq(j["avatar"]["guid"].AsString, "avguid", "avatar guid");
        Eq(j["sources"][0].AsString, "C:/x/a.unitypackage", "sources");
        Eq(j["outfits"].Count, 2, "outfits");
        Eq(j["outfits"][0]["parent"].AsString, "Armature/Hips", "outfit parent path");
        Eq(j["outfits"][0]["trs"][1].AsFloat, 0.1f, "outfit local position");
        Check(!j["outfits"][0]["active"].AsBool && j["outfits"][1]["active"].AsBool, "outfit active from doc");
        Eq(j["nodes"].Count, 1, "node toggles (outfit roots excluded)");
        Eq(j["nodes"][0]["path"].AsString, "Acc[1]", "node path");
        Eq(j["renderers"][0]["path"].AsString, "Body", "renderer path");
        Eq(j["renderers"][0]["blendShapes"]["Smile"].AsFloat, 50f, "blend shape");
        Eq(j["materials"][0]["floats"]["_Cutoff"].AsDouble, 0.3, "material float");
        Eq(j["materials"][0]["textures"]["_MainTex"].AsString, "t9", "material texture key");
        Eq(j["textures"]["t9"]["file"].AsString, "C:/x/T.png", "texture file for copy");

        // 색 굽기: 문서에 더한 텍스처(TexAsset)를 가리키면 그 파일을 복사하고 원본(like)의 임포트 설정을 따른다
        using (doc.Group("색 굽기"))
        {
            doc.Set(Addr.TexAsset("baked:1"), JNode.Obj().Set("file", "C:/b/T_baked.png").Set("path", "T_baked.png").Set("like", "t9"));
            doc.Set(Addr.MatTex("m1", "_MainTex"), "baked:1");
        }
        Eq(doc.Texture("baked:1")?.File, "C:/b/T_baked.png", "doc texture from TexAsset");
        Eq(doc.Texture("t9")?.Path, "Assets/T.png", "doc texture from IR");
        Check(doc.Texture("nope") == null, "unknown texture");
        var jb = UploadPlan.Build(doc, "My Ava");
        Eq(jb["materials"][0]["textures"]["_MainTex"].AsString, "baked:1", "material points to baked key");
        Eq(jb["textures"]["baked:1"]["file"].AsString, "C:/b/T_baked.png", "baked file for copy");
        Eq(jb["textures"]["baked:1"]["like"].AsString, "t9", "baked importer like");
        var bs = new FakeSink(); var bsync = new RenderSync(doc, bs);
        bsync.SendAllOverrides();
        int iAdd = bs.Sent.FindIndex(m => Msg.Type(m) == Msg.AddTexture), iTex = bs.Sent.FindIndex(m => Msg.Type(m) == Msg.SetMatProp && m["prop"].AsString == "_MainTex");
        Check(iAdd >= 0 && iTex > iAdd && bs.Sent[iAdd]["file"].AsString == "C:/b/T_baked.png", $"addTexture before material texture ({iAdd} < {iTex})");
        doc.Undo();
        Eq(doc.Get(Addr.MatTex("m1", "_MainTex")).AsString, "t9", "undo bake = one step");
        Check(doc.Texture("baked:1") == null, "undo removes baked texture");
        Eq(j["meshes"][0]["vertexCount"].AsInt, 4, "mesh edit with vertex count guard");
        Check(!j.ToJson().Contains("muscles"), "pose not included");
        Eq(j["upload"]["releaseStatus"].AsString, "private", "private by default");

        var auto = UploadPlan.AutoGimmicks(doc);
        Check(auto.Contains("ma") && auto.Contains("liltoon"), "auto: MA (ma-merge outfit) + lilToon (shader guid)");
        Check(!UploadPlan.AutoGimmicks(doc, g => g == UploadPlan.LilToonShaderGuid).Contains("liltoon"), "lilToon in package → not auto");
        Check(!j.Has("export"), "upload has no export dir");

        // 원본에 눈 깜빡임 보정이 있으면 에디터의 blinkFix를 뺀다 (나머지 메시 편집은 그대로)
        Eq(UploadPlan.ExistingBlinkFix(s).Count, 0, "no existing blink fix in IR");
        Check(!UploadPlan.UsesBlinkFix(doc), "blinkFix not used yet");
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(MeshEdits.WeldOp(0.0001f)).Add(MeshOps.MakeBlinkFix(new[] { "blink" }, new[] { ("smile", 0.5f) })));
        Check(UploadPlan.UsesBlinkFix(doc), "blinkFix used");
        var keep = UploadPlan.Build(doc, "My Ava");
        Eq(keep["meshes"][0]["ops"].Count, 2, "blinkFix kept by default");
        var ex = UploadPlan.Build(doc, "My Ava", null, excludeBlinkFix: true, exportDir: "Assets/VrcProto/Export/x");
        Eq(ex["meshes"][0]["ops"].Count, 1, "blinkFix excluded, weld kept");
        Eq(ex["meshes"][0]["ops"][0]["op"].AsString, MeshOps.Weld, "remaining op is weld");
        Eq(ex["export"]["dir"].AsString, "Assets/VrcProto/Export/x", "export dir");
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(MeshOps.MakeBlinkFix(new[] { "blink" }, new[] { ("smile", 0.5f) })));
        Eq(UploadPlan.Build(doc, "My Ava", null, excludeBlinkFix: true)["meshes"].Count, 0, "mesh with only blinkFix dropped when excluded");
        var withFix = IrScene.FromJson(JNode.Parse(@"{ ""irVersion"": 1, ""name"": ""B"", ""buffers"": """", ""nodes"": [ { ""id"": 0, ""name"": ""B"" }, { ""id"": 1, ""name"": ""Face"", ""parent"": 0 } ],
          ""meshes"": [], ""renderers"": [], ""materials"": [], ""textures"": [], ""existingBlinkFix"": [ { ""node"": 1, ""script"": ""SomeEyeFix"" } ] }"));
        Eq(string.Join(",", UploadPlan.ExistingBlinkFix(withFix)), "Face (SomeEyeFix)", "existing blink fix from IR");
        Check(!UploadPlan.ExistingMouthFix(withFix), "existing fix without mouth list");

        // 입 맞추기는 원본 보정이 입도 고칠 때만 뺀다
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(MeshOps.MakeBlinkFix(new[] { "blink" }, new[] { ("smile", 0.5f) })).Add(MeshOps.MakeMouthFix(new[] { "vrc.v_aa" }, new[] { ("mouth_straight", 100f) })));
        Check(UploadPlan.UsesBlinkFix(doc, MeshOps.MouthFix), "mouthFix used");
        var onlyBlink = UploadPlan.Build(doc, "My Ava", null, excludeBlinkFix: true);
        Eq(string.Join(",", onlyBlink["meshes"][0]["ops"].Items.Select(o => o["op"].AsString)), MeshOps.MouthFix, "excludeBlinkFix keeps mouthFix");
        Eq(UploadPlan.Build(doc, "My Ava", null, excludeBlinkFix: true, excludeMouthFix: true)["meshes"].Count, 0, "both excluded");
        var withMouth = IrScene.FromJson(JNode.Parse(@"{ ""irVersion"": 1, ""name"": ""B"", ""buffers"": """", ""nodes"": [ { ""id"": 0, ""name"": ""B"" } ],
          ""meshes"": [], ""renderers"": [], ""materials"": [], ""textures"": [], ""existingBlinkFix"": [ { ""node"": 0, ""script"": ""X"", ""mouth"": true } ] }"));
        Check(UploadPlan.ExistingMouthFix(withMouth), "existing fix with mouth list");

        // 꺼 둔 다문 입 다듬기는 올리지 않는다
        var tidyOff = JNode.Obj().Set("op", MeshOps.ShapeMove).Set("shape", "mouth_straight").Set("points", JNode.Arr()).Set("enabled", false);
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(tidyOff).Add(MeshEdits.WeldOp(0.0001f)));
        Eq(string.Join(",", UploadPlan.Build(doc, "My Ava")["meshes"][0]["ops"].Items.Select(o => o["op"].AsString)), MeshOps.Weld, "disabled tidy not uploaded");
        doc.Set(Addr.MeshEdit("mg:1"), JNode.Arr().Add(tidyOff));
        Eq(UploadPlan.Build(doc, "My Ava")["meshes"].Count, 0, "mesh with only disabled tidy dropped");
    }
}
