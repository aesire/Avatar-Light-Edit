// Host 코어 단위 테스트 (테스트 프레임워크 없이 실행: dotnet bin/HostTests.dll)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static int _fail, _pass;
    static void Check(bool ok, string what) { if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL: " + what); } }
    static void Eq(object? a, object? b, string what) => Check(Equals(a, b), $"{what}: expected [{b}] got [{a}]");

    static int Main()
    {
        Run(Json); Run(DocumentBasics); Run(UndoGroups); Run(ChangesetRoundTrip); Run(RenderSyncMessages);
        Run(LilRules); Run(Sections); Run(BlendGroups); Run(ShapeNames); Run(BlendSides); Run(PipeRoundTrip); Run(IrLoad); Run(PerfStats); Run(PhysBoneStats); Run(OutfitRecompose); Run(PoseCore); Run(AnimParse); Run(WardrobeParts); Run(Toggles); Run(ShapeKeyRun); Run(AnimWrite); Run(BlinkFixMath); Run(FaceMapMerge);
        Run(MeshOpsBasics); Run(MeshEditsDoc); Run(VpmVersions); Run(UploadPlanBuild); Run(DecimateBasics); Run(DecimateIslands); Run(DecimateToCount); Run(StickerImages); Run(SheetCore); Run(ColorHarmonyBasics); Run(ImageDecodeBasics); Run(ImageDecodeMore); Run(TextureGpuBasics); Run(SessionSource); Run(SceneSpecBasics); Run(SceneSpecStudioTabs); Run(StudioPresets); Run(DocEditBasics); Run(TextureMixBasics); Run(SessionPackRoundTrip); Run(ReleaseNotesLang); Run(HistoryJump); Run(OriginalView); Run(UpdateFilesStale);
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void PerfStats()
    {
        var s = IrScene.FromJson(JNode.Parse(@"{
          ""irVersion"": 1, ""name"": ""p"", ""buffers"": """",
          ""nodes"": [ { ""id"": 0, ""name"": ""A"" }, { ""id"": 1, ""name"": ""Hips"", ""parent"": 0 }, { ""id"": 2, ""name"": ""Spine"", ""parent"": 1 },
                       { ""id"": 3, ""name"": ""Body"", ""parent"": 0 }, { ""id"": 4, ""name"": ""Hat"", ""parent"": 0, ""active"": false }, { ""id"": 5, ""name"": ""Prop"", ""parent"": 0 } ],
          ""meshes"": [ { ""id"": ""m0"", ""name"": ""Body"", ""vertexCount"": 0, ""submeshes"": [ { ""indices"": { ""off"": 0, ""count"": 90000 } }, { ""indices"": { ""off"": 0, ""count"": 30 } } ] },
                        { ""id"": ""m1"", ""name"": ""Quad"", ""vertexCount"": 0, ""submeshes"": [ { ""indices"": { ""off"": 0, ""count"": 8 }, ""topology"": ""Quads"" }, { ""indices"": { ""off"": 0, ""count"": 6 }, ""topology"": ""Lines"" } ] } ],
          ""renderers"": [ { ""id"": ""r0"", ""node"": 3, ""mesh"": ""m0"", ""materials"": [ ""a"", ""b"" ], ""bones"": [ 1, 2, -1 ] },
                           { ""id"": ""r1"", ""node"": 4, ""mesh"": ""m0"", ""materials"": [ ""a"" ], ""bones"": [ 2 ], ""enabled"": false },
                           { ""id"": ""r2"", ""node"": 5, ""type"": ""MeshRenderer"", ""mesh"": ""m1"", ""materials"": [ ""c"" ] } ],
          ""materials"": [], ""textures"": [] }"));
        var st = AvatarStats.From(s, new[] { 0.6f, 1.5f, 0.4f });
        Eq(st.Triangles, 30010 * 2 + 4, "tris: 꺼 둔 렌더러 포함, Quads 4개 = 2삼각형×2, Lines 제외");
        Eq(st.SkinnedMeshes, 2, "skinned"); Eq(st.BasicMeshes, 1, "basic"); Eq(st.MaterialSlots, 4, "slots"); Eq(st.Bones, 2, "bones unique, -1 제외");
        var tri = st.Items(PerfPlatform.PC).First(i => i.Key == "triangles");
        Eq(tri.Rank, PerfRank.Good, "60,024 tris PC = Good");
        Eq(st.Items(PerfPlatform.PC).First(i => i.Key == "skinned").Rank, PerfRank.Good, "2 skinned PC = Good");
        Eq(st.Overall(PerfPlatform.PC), PerfRank.Good, "PC overall");
        Eq(st.Overall(PerfPlatform.Mobile), PerfRank.VeryPoor, "mobile overall");
        Eq(AvatarStats.RankOf(70001, new double[] { 32000, 70000, 70000, 70000 }), PerfRank.VeryPoor, "70,001 = Very Poor");
        Eq(AvatarStats.Hint(new PerfItem { Value = 80000, Limits = new double[] { 32000, 70000, 70000, 70000 }, Rank = PerfRank.VeryPoor }), "Good까지 10,000 줄이기", "hint jumps to best rank at that limit");
        Eq(AvatarStats.Hint(tri), "Excellent까지 28,024 줄이기", "hint good→excellent");
        Eq(AvatarStats.BoundsRank(new[] { 4.5f, 5.5f, 1f }), PerfRank.Medium, "bounds medium");
        Eq(AvatarStats.BoundsRank(new[] { 6f, 1f, 1f }), PerfRank.VeryPoor, "bounds very poor");
    }

    static void PhysBoneStats()
    {
        // 0 ─ 1(PB 루트) ─ 2 ─ 3      1 아래 4(무시), 5(다른 PB 루트) ─ 6
        var s = new IrScene();
        int[] parents = { -1, 0, 1, 2, 1, 1, 5 };
        for (int i = 0; i < parents.Length; i++) s.Nodes.Add(new IrNode { Id = i, Name = "n" + i, Parent = parents[i] });
        s.PhysBoneColliders.Add(new IrPhysBoneCollider { Node = 0 });
        s.PhysBoneColliders.Add(new IrPhysBoneCollider { Node = 0, Enabled = false });
        var a = new IrPhysBone { Node = 1 }; a.Ignore.Add(4); a.Colliders.Add(0); a.Colliders.Add(1);
        var b = new IrPhysBone { Node = 0, Root = 5 }; b.Colliders.Add(0); b.Values["allowCollision"] = 0;
        s.PhysBones.Add(a); s.PhysBones.Add(b);
        var st = AvatarStats.From(s);
        Eq(st.PhysBones, 2, "PhysBone components");
        Eq(st.PhysBoneColliders, 2, "colliders (disabled counted)");
        Eq(st.PhysBoneTransforms, 3 + 2, "transforms: 1,2,3 + 5,6 (ignore·other root excluded)");
        Eq(st.PhysBoneChecks, 3 * 2 + 2 * 1, "checks: listed colliders count even with allowCollision off");
        Eq(st.Items(PerfPlatform.Mobile).First(i => i.Key == "pbComponents").Rank, PerfRank.Good, "mobile: any PhysBone is at best Good");
    }

    static void Run(Action t)
    {
        try { t(); }
        catch (Exception e) { _fail++; Console.WriteLine($"FAIL: {t.Method.Name} threw {e}"); }
    }

    static IrScene SampleScene()
    {
        var root = JNode.Parse(@"{
          ""irVersion"": 1, ""name"": ""t"", ""buffers"": """",
          ""nodes"": [ { ""id"": 0, ""name"": ""Avatar"" }, { ""id"": 1, ""name"": ""Body"", ""parent"": 0, ""active"": true } ],
          ""meshes"": [ { ""id"": ""m0"", ""name"": ""Body"", ""vertexCount"": 0,
                         ""blendShapes"": [ { ""name"": ""smile"", ""frames"": [] }, { ""name"": ""vrc.v_aa"", ""frames"": [] } ] } ],
          ""renderers"": [ { ""id"": ""r0"", ""node"": 1, ""mesh"": ""m0"", ""materials"": [ ""matA"" ], ""blendShapeWeights"": [ 25, 0 ] } ],
          ""materials"": [ { ""guid"": ""matA"", ""path"": ""Assets/A.mat"", ""shaderGuid"": ""df12117ecd77c31469c224178886498e"",
                            ""floats"": { ""_Cutoff"": 0.5 }, ""colors"": { ""_Color"": [1, 0.5, 0.5, 1] },
                            ""texEnvs"": { ""_MainTex"": { ""texture"": ""texA"", ""scale"": [1,1], ""offset"": [0,0] } },
                            ""keywords"": [], ""renderQueue"": -1 } ],
          ""textures"": [ { ""guid"": ""texA"", ""path"": ""Assets/a.png"", ""file"": ""a.png"", ""importer"": { ""sRGB"": true } } ]
        }");
        return IrScene.FromJson(root);
    }

    static void Json()
    {
        var j = JNode.Parse(@"{""a"":[1,2.5,""x\né""],""b"":{""c"":null,""d"":true},""big"":""-8679921383154817045""}");
        Eq(j["a"][1].AsDouble, 2.5, "json number");
        Eq(j["a"][2].AsString, "x\né", "json string escape");
        Eq(j["big"].AsLong, -8679921383154817045L, "fileID as string");
        Check(j["b"]["c"].IsNull && j["b"]["d"].AsBool, "json null/bool");
        Eq(JNode.Parse(j.ToJson()).ToJson(), j.ToJson(), "json round trip");
        Eq(JNode.Parse(j.ToJson(true)).ToJson(), j.ToJson(), "json indent round trip");
        Eq(JNode.From(0.1f).ToJson().Length < 12, true, "float written compactly");
    }

    static void DocumentBasics()
    {
        var doc = new Document(SampleScene(), "x.ir.json");
        var cut = Addr.MatFloat("matA", "_Cutoff");
        Eq(doc.Get(cut).AsFloat, 0.5f, "original float");
        Eq(doc.Get(Addr.BlendShape("r0", "smile")).AsFloat, 25f, "original blendshape");
        Eq(doc.Get(Addr.MatTex("matA", "_MainTex")).AsString, "texA", "original texture");
        Eq(doc.Get(Addr.MatTexST("matA", "_MainTex")).ToJson(), "[1,1,0,0]", "original texST");

        int changed = 0; doc.Changed += a => changed += a.Count;
        doc.Set(cut, 0.7, "cutoff");
        Check(doc.IsModified(cut) && doc.IsDirty, "modified after set");
        Eq(changed, 1, "changed event");
        doc.Set(cut, 0.5, "cutoff back");
        Check(!doc.IsModified(cut), "setting original value removes override");
        Eq(doc.Overrides.Count, 0, "no overrides");

        // 원본이 없는 프로퍼티 → 스키마 기본값
        var schema = new MaterialSchema { Guid = "matA", Shader = "lilToon", Supported = true };
        schema.Props.Add(new ShaderPropInfo { Name = "_ShadowBorder", Type = "Range", Min = 0, Max = 1, Default = new float[] { 0.5f, 0, 0, 0 } });
        doc.SetSchema(schema, true);
        Eq(doc.Get(Addr.MatFloat("matA", "_ShadowBorder")).AsFloat, 0.5f, "schema default");
        Eq(doc.Get(Addr.MatShader("matA")).AsString, "lilToon", "original shader from schema");

        // 텍스처를 None으로 덮어쓰기 (null 값도 덮어쓰기)
        var tex = Addr.MatTex("matA", "_MainTex");
        doc.Set(tex, JNode.Null, "tex none");
        Check(doc.IsModified(tex) && doc.Get(tex).IsNull, "texture None override");
        doc.Reset(tex);
        Check(!doc.IsModified(tex), "reset texture");
    }

    static void UndoGroups()
    {
        var doc = new Document(SampleScene(), "x");
        var a = Addr.MatFloat("matA", "_Cutoff"); var b = Addr.BlendShape("r0", "smile");
        doc.Set(a, 0.1); doc.Set(a, 0.2);
        using (doc.Group("group")) { doc.Set(a, 0.3); doc.Set(b, 80); doc.Set(a, 0.4); }
        Eq(doc.UndoLabel, "group", "group label");
        doc.Undo();
        Eq(doc.Get(a).AsFloat, 0.2f, "undo group restores a");
        Eq(doc.Get(b).AsFloat, 25f, "undo group restores b");
        doc.Undo();
        Eq(doc.Get(a).AsFloat, 0.1f, "undo 2");
        doc.Undo();
        Check(!doc.IsModified(a) && !doc.CanUndo, "undo to original");
        doc.Redo(); doc.Redo(); doc.Redo();
        Eq(doc.Get(a).AsFloat, 0.4f, "redo all");
        Eq(doc.Get(b).AsFloat, 80f, "redo b");
        doc.Undo(); doc.Set(b, 10);
        Check(!doc.CanRedo, "new edit clears redo");

        // 아무 것도 바꾸지 않은 그룹은 기록하지 않음
        int before = 0; while (doc.CanUndo) { doc.Undo(); before++; }
        using (doc.Group("empty")) { doc.Set(a, 0.5); }
        Check(!doc.CanUndo, "no-op group not recorded");

        // ResetWhere
        doc.Set(a, 0.9); doc.Set(Addr.MatColor("matA", "_Color"), JNode.FloatArray(new float[] { 0, 0, 0, 1 }));
        doc.ResetWhere(x => x.Target == "matA", "reset material");
        Eq(doc.Overrides.Count, 0, "reset material clears");
        doc.Undo();
        Eq(doc.Overrides.Count, 2, "undo reset material");
    }

    static void ChangesetRoundTrip()
    {
        var doc = new Document(SampleScene(), "x");
        doc.Set(Addr.MatFloat("matA", "_Cutoff"), 0.25);
        doc.Set(Addr.MatShader("matA"), "Hidden/lilToonCutout");
        doc.Set(Addr.NodeActive(1), false);
        doc.Set(Addr.Environment, JNode.Arr().Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj().Set("preset", "dark"))));
        var path = Path.Combine(Path.GetTempPath(), "vrcproto-test" + Session.Extension);
        Session.Save(path, doc, JNode.Obj().Set("fov", 30));
        Check(!doc.IsDirty, "saved");
        doc.Set(Addr.NodeActive(1), true);
        Check(doc.IsDirty, "dirty after edit");
        doc.Undo();
        Check(!doc.IsDirty, "undo back to the saved state → clean");
        doc.Redo();
        Check(doc.IsDirty, "redo → dirty again");
        doc.Undo();
        doc.MarkUnsaved();
        Check(doc.IsDirty, "MarkUnsaved → dirty");
        doc.MarkSaved();
        var loaded = Session.Read(path);
        var doc2 = new Document(SampleScene(), loaded.IrPath);
        doc2.LoadChangeset(loaded.Changeset);
        Eq(doc2.Overrides.Count, 4, "loaded override count");
        Eq(doc2.Get(Addr.MatShader("matA")).AsString, "Hidden/lilToonCutout", "loaded shader");
        Eq(doc2.Get(Addr.NodeActive(1)).AsBool, false, "loaded node active");
        Eq(loaded.Changeset[0]["a"].AsString, Addr.MatShader("matA").ToString(), "shader op first");
        Eq(loaded.Camera["fov"].AsInt, 30, "camera saved");
        Check(!doc2.CanUndo && !doc2.IsDirty, "load resets undo");
        File.Delete(path);
    }

    sealed class FakeSink : IRenderSink
    {
        public List<JNode> Sent = new List<JNode>();
        public bool IsConnected => true;
        public void Send(JNode m) => Sent.Add(m);
    }

    static void RenderSyncMessages()
    {
        var doc = new Document(SampleScene(), "x");
        var sink = new FakeSink();
        var sync = new RenderSync(doc, sink);
        doc.Set(Addr.MatFloat("matA", "_Cutoff"), 0.3);
        Eq(Msg.Type(sink.Sent[0]), Msg.SetMatProp, "float → setMatProp");
        Eq(sink.Sent[0]["kind"].AsString, Msg.KindFloat, "kind");
        doc.Preview(Addr.BlendShape("r0", "smile"), 60);
        Eq(sink.Sent[1]["weight"].AsFloat, 60f, "preview sent");
        Check(!doc.IsModified(Addr.BlendShape("r0", "smile")), "preview not recorded");

        sink.Sent.Clear();
        doc.Set(Addr.MatShader("matA"), "Hidden/lilToonCutout");
        Eq(Msg.Type(sink.Sent[0]), Msg.SetMatShader, "shader first");
        Check(sink.Sent.Any(m => Msg.Type(m) == Msg.SetMatProp && m["prop"].AsString == "_Cutoff"), "material overrides resent after shader change");

        sink.Sent.Clear();
        doc.Undo(); // 셰이더 되돌림 → 원래 셰이더 이름(스키마 없음 → IR shaderName 없음 → "")
        Eq(Msg.Type(sink.Sent[0]), Msg.SetMatShader, "undo shader sends shader");

        sink.Sent.Clear();
        sync.SendAllOverrides();
        Eq(Msg.Type(sink.Sent[0]), Msg.SetEnvironment, "restore sends environment first");
        Check(sink.Sent.Count == 1 + doc.Overrides.Count, "restore sends every override");
    }

    static void LilRules()
    {
        var s = LilToonRules.Detect("Hidden/lilToonTransparentOutline")!;
        Check(s.Mode == LilRenderingMode.Transparent && s.Outline && s.Family == LilFamily.Normal, "detect transparent outline");
        Eq(LilToonRules.Detect("_lil/lilToonMulti", 2)!.Mode, LilRenderingMode.Transparent, "detect multi by _TransparentMode");
        Eq(LilToonRules.ShaderFor(new LilShaderState { Family = LilFamily.Lite, Mode = LilRenderingMode.Cutout, Outline = true }), "Hidden/lilToonLiteCutoutOutline", "lite cutout outline");
        Eq(LilToonRules.ShaderFor(new LilShaderState { Family = LilFamily.Normal, Mode = LilRenderingMode.Transparent, Transparent = LilTransparentMode.TwoPass }), "Hidden/lilToonTwoPassTransparent", "two pass");
        Eq(LilToonRules.ShaderFor(new LilShaderState { Family = LilFamily.Normal, Mode = LilRenderingMode.Opaque }), "lilToon", "opaque");
        Eq(LilToonRules.ShaderFor(new LilShaderState { Family = LilFamily.Lite, Mode = LilRenderingMode.Gem }), "Hidden/lilToonGem", "gem from lite → normal gem");

        var ops = LilToonRules.Apply("m", new LilShaderState { Family = LilFamily.Normal, Mode = LilRenderingMode.Cutout }, 2450);
        JNode V(Addr a) => ops.First(o => o.Key.Equals(a)).Value;
        Eq(V(Addr.MatShader("m")).AsString, "Hidden/lilToonCutout", "apply shader");
        Eq(V(Addr.MatFloat("m", "_AlphaToMask")).AsInt, 1, "cutout alpha to mask");
        Eq(V(Addr.MatRenderQueue("m")).AsInt, 2450, "non-multi keeps queue");
        var multi = LilToonRules.Apply("m", new LilShaderState { Family = LilFamily.Multi, Mode = LilRenderingMode.Transparent }, 2000);
        Eq(multi.First(o => o.Key.Equals(Addr.MatRenderQueue("m"))).Value.AsInt, 2460, "multi transparent queue");
        Eq(multi.First(o => o.Key.Equals(Addr.MatFloat("m", "_TransparentMode"))).Value.AsInt, 2, "multi _TransparentMode");
        Check(!LilToonRules.Apply("m", new LilShaderState { Family = LilFamily.Normal, Mode = LilRenderingMode.Transparent, Transparent = LilTransparentMode.TwoPass }, 3000)
            .Any(o => o.Key.Name == "_ZTest"), "two pass keeps _ZTest");
    }

    static void Sections()
    {
        Eq(PropertySections.Classify("_MainTex", true), "Main", "main tex");
        Eq(PropertySections.Classify("_Main2ndTex", true), "Main 2nd", "main 2nd");
        Eq(PropertySections.Classify("_UseShadow", true), "Shadow", "use shadow");
        Eq(PropertySections.Classify("_ShadowColor", true), "Shadow", "shadow color");
        Eq(PropertySections.Classify("_Emission2ndMap", true), "Emission 2nd", "emission 2nd");
        Eq(PropertySections.Classify("_RimShadeColor", true), "Rim Shade", "rim shade before rim");
        Eq(PropertySections.Classify("_RimColor", true), "Rim Light", "rim light");
        Eq(PropertySections.Classify("_OutlineWidth", true), "Outline", "outline width");
        Eq(PropertySections.Classify("_OutlineSrcBlend", true), "Rendering", "outline blend → rendering");
        Eq(PropertySections.Classify("_Cull", true), "Rendering", "cull");
        Eq(PropertySections.Classify("_Color", true), "Main", "color");
        Eq(PropertySections.Classify("_Color", false), "Properties", "non-lil");
    }

    static void BlendGroups()
    {
        var g = BlendShapeGroups.Build(new[] { "vrc.v_aa", "vrc.v_ch", "=== Eye ===", "eye_close", "eye_smile", "--- Mouth ---", "mouth_a" });
        Eq(g[0].Group, "기타", "before first separator");
        Check(g[0].HiddenByDefault && !g[3].HiddenByDefault, "vrc.v_ hidden");
        Check(g[2].IsSeparator && g[2].Group == "Eye", "separator name trimmed");
        Eq(g[4].Group, "Eye", "eye group");
        Eq(g[6].Group, "Mouth", "mouth group");
        var p = BlendShapeGroups.Build(new[] { "eye_close", "eye_smile", "mouth_a", "mouth_o", "Shrink" });
        Eq(p[0].Group, "eye", "prefix group");
        Eq(p[4].Group, "기타", "lone → 기타");
        var c = BlendShapeGroups.Build(new[] { "=== Eye ===", "eye_close", "=== Mouth ===", "mouth_a", "=== eye ===", "eyeball_up" });
        Eq(c[5].Group, "Eye", "case-only different separators merge into first");
        var q = BlendShapeGroups.Build(new[] { "Eye_a", "Eye_b", "eye_c" });
        Eq(q[2].Group, "Eye", "case-only different prefixes merge");
    }

    static void ShapeNames()
    {
        // 블렌드쉐이프 이름 번역: 화면 표시용 (원본은 그대로)
        Eq(ShapeNameTr.Tr("あ", "ko"), "아", "MMD vowel");
        Eq(ShapeNameTr.Tr("まばたき", "ko"), "눈 감기", "blink");
        Eq(ShapeNameTr.Tr("ウィンク右", "ko"), "오른쪽 윙크", "wink right (whole)");
        Eq(ShapeNameTr.Tr("ウィンク２右", "ko"), "오른쪽 윙크 2", "full-width digit");
        Eq(ShapeNameTr.Tr("笑い目", "ko"), "웃음 눈", "fragments");
        Eq(ShapeNameTr.Tr("目_笑い_L", "ko"), "눈 웃음 왼쪽", "separators + L");
        Eq(ShapeNameTr.Tr("怒り", "ko"), "화남", "angry");
        Eq(ShapeNameTr.Tr("困る", "ko"), "곤란", "troubled");
        Eq(ShapeNameTr.Tr("eye_close_L", "ko"), "눈 감기 왼쪽", "english all known");
        Eq(ShapeNameTr.Tr("SmileR", "ko"), "웃음 오른쪽", "camelCase L/R");
        Eq(ShapeNameTr.Tr("eye_blink_1_L", "ko"), "눈 감기 1 왼쪽", "doubled word merged");
        Eq(ShapeNameTr.Tr("瞳小", "ko"), "눈동자 작게", "kanji fragments");
        Eq(ShapeNameTr.Tr("Shrink_Hoge", "ko"), "Shrink_Hoge", "english partly unknown → original");
        Eq(ShapeNameTr.Tr("Body", "ko"), "Body", "unknown → original");
        Eq(ShapeNameTr.Tr("vrc.v_aa", "ko"), "vrc.v_aa", "vrc. untouched");
        Check(ShapeNameTr.Matches("まばたき", "눈감기", "ko") && ShapeNameTr.Matches("まばたき", "まば", "ko") && !ShapeNameTr.Matches("まばたき", "웃음", "ko"), "search by original or Korean");
        // 영어로
        Eq(ShapeNameTr.Tr("まばたき", "en"), "Blink", "en blink");
        Eq(ShapeNameTr.Tr("ウィンク右", "en"), "Wink Right", "en wink right");
        Eq(ShapeNameTr.Tr("笑い目_L", "en"), "Smile Eye L", "en fragments");
        Eq(ShapeNameTr.Tr("eye_nagomi_L", "en"), "eye Relaxed L", "en romaji");
        Eq(ShapeNameTr.Tr("eye_close_L", "en"), "eye_close_L", "en english untouched");
        Eq(ShapeNameTr.Tr("まばたき", ""), "まばたき", "original");
        Check(ShapeNameTr.Matches("まばたき", "blink", "en"), "search english");
        // 표에 없는 이름 → 기계 번역에 맡김
        Check(ShapeNameTr.NeedsMachine("JellyEye1", "ko") && !ShapeNameTr.NeedsMachine("JellyEye1", "en"), "english unknown: ko needs machine, en not");
        Check(ShapeNameTr.NeedsMachine("恐竜", "en") && !ShapeNameTr.NeedsMachine("まばたき", "ko") && !ShapeNameTr.NeedsMachine("vrc.v_aa", "ko") && !ShapeNameTr.NeedsMachine("---", "ko"), "needs machine");
        ShapeNameTr.Machine = (n, l) => n == "JellyEye1" && l == "ko" ? "젤리 눈 1" : null;
        Eq(ShapeNameTr.Tr("JellyEye1", "ko"), "젤리 눈 1", "machine result used");
        Eq(ShapeNameTr.Tr("まばたき", "ko"), "눈 감기", "table wins over machine");
        ShapeNameTr.Machine = null;
        Eq(ShapeNameTr.Tr("M M D", "ko"), "MMD", "MMD separator");
        Eq(ShapeNameTr.Tr("Facial Tracking", "ko"), "페이셜 트래킹", "facial tracking");
    }

    static void BlendSides()
    {
        var names = new[] { "Wink", "Wink_L", "Wink_R", "Smile", "SmileL", "SmileR", "eye_close", "eye_close.L", "eye_close.R", "mouth_a", "Brow_L", "Brow_R", "ウィンク", "ウィンク左", "ウィンク右" };
        var s = BlendShapeSides.Find(names);
        Eq(s.Count, 4, "four triples (Brow has no base)");
        Check(s.Any(x => x.Base == "Wink" && x.Left == "Wink_L" && x.Right == "Wink_R"), "_L/_R");
        Check(s.Any(x => x.Base == "Smile" && x.Left == "SmileL"), "L/R no separator");
        Check(s.Any(x => x.Base == "eye_close" && x.Right == "eye_close.R"), ".L/.R");
        Check(s.Any(x => x.Base == "ウィンク" && x.Left == "ウィンク左"), "左/右");
        var mod = new HashSet<string> { "Wink", "SmileL", "SmileR", "eye_close.L", "eye_close.R" };
        var val = new Dictionary<string, double> { ["SmileL"] = 50, ["SmileR"] = 50, ["eye_close.L"] = 30, ["eye_close.R"] = 70 };
        var split = BlendShapeSides.SplitCandidates(s, mod.Contains);
        Eq(split.Count, 1, "split only modified base");
        Eq(split[0].Base, "Wink", "split Wink");
        var merge = BlendShapeSides.MergeCandidates(s, mod.Contains, n => val.TryGetValue(n, out var v) ? v : 0, out var differ);
        Eq(merge.Count, 1, "merge equal L/R"); Eq(merge[0].Base, "Smile", "merge Smile");
        Eq(differ.Count, 1, "different L/R not merged"); Eq(differ[0].Base, "eye_close", "eye_close differs");
        Eq(BlendShapeSides.SplitCandidates(s, mod.Contains, new HashSet<string> { "Smile" }).Count, 0, "only filter");
    }

    static void PipeRoundTrip()
    {
        var name = PipeChannel.NewPipeName();
        using var server = new PipeChannel.Server(name);
        var got = new List<JNode>(); var gotEvt = new ManualResetEventSlim();
        var accept = server.AcceptAsync(CancellationToken.None);
        var client = PipeChannel.Connect(name, 2000);
        Check(accept.Wait(2000), "accepted");
        var sch = accept.Result;
        var echo = new ManualResetEventSlim(); JNode? echoed = null;
        client.OnMessage += m => { echoed = m; echo.Set(); };
        sch.OnMessage += m => { lock (got) got.Add(m); if (got.Count == 2) gotEvt.Set(); };
        sch.Start(); client.Start();
        client.Send(Msg.Make(Msg.Ready).Set("unityVersion", "2022.3.22f1"));
        client.Send(Msg.Make(Msg.Log).Set("text", new string('가', 100000)));
        Check(gotEvt.Wait(3000), "messages received");
        Eq(Msg.Type(got[0]), Msg.Ready, "first message type");
        Eq(got[1]["text"].AsString.Length, 100000, "large utf-8 message");
        Eq(got[1]["seq"].AsInt, 2, "seq increments");
        sch.Send(Msg.Make(Msg.Hello)); // 클라이언트가 읽기 대기 중인 상태에서 양방향 동시 사용
        Check(echo.Wait(3000) && Msg.Type(echoed!) == Msg.Hello, "host → unity direction");
        var closed = new ManualResetEventSlim();
        sch.OnClosed += _ => closed.Set();
        client.Dispose();
        Check(closed.Wait(3000), "server sees close");
    }

    static void IrLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrcproto-irtest");
        Directory.CreateDirectory(dir);
        BufRef pos;
        using (var w = new IrBufferWriter(Path.Combine(dir, "t.bin"))) { w.Write(new float[] { 9, 9 }); pos = w.Write(new float[] { 1, 2, 3 }); }
        var root = JNode.Obj().Set("irVersion", 1).Set("name", "t").Set("buffers", "t.bin")
            .Set("meshes", JNode.Arr().Add(JNode.Obj().Set("id", "m").Set("vertexCount", 1).Set("positions", pos.ToJson())))
            .Set("textures", JNode.Arr().Add(JNode.Obj().Set("guid", "g").Set("file", "a.png")));
        File.WriteAllText(Path.Combine(dir, "t.ir.json"), root.ToJson());
        var s = IrScene.Load(Path.Combine(dir, "t.ir.json"));
        Check(Path.IsPathRooted(s.BufferFile) && Path.IsPathRooted(s.Textures[0].File), "relative paths resolved");
        using (var b = new IrBuffers(s.BufferFile)) Eq(string.Join(",", b.Floats(s.Meshes[0].Positions)), "1,2,3", "buffer read at offset");
        var bad = JNode.Obj().Set("irVersion", 99);
        try { IrScene.FromJson(bad); Check(false, "newer IR rejected"); } catch (NotSupportedException) { _pass++; }
    }

    /// 아이템을 바꿔 IR을 다시 만들면 노드 번호가 바뀐다: 노드 켜고 끄기는 source로 다시 찾고, 없어진 대상의 덮어쓰기는 버린다
    /// <summary>사용자 토글 묶음 · 표정 단축키 (Core.ToggleSet).</summary>
    static void ShapeKeyRun()
    {
        var keys = JNode.Arr();
        keys.Add(ShapeKeys.Make("홍조", "Ctrl+Alt+B", "toggle", "", new[] { ("r0", "shy", 100f, 0f) }));
        keys.Add(ShapeKeys.Make("하트 눈", "", "toggle", "눈동자", new[] { ("r0", "eye_heart", 100f, 0f) }));
        keys.Add(ShapeKeys.Make("빙글 눈", "", "toggle", "눈동자", new[] { ("r0", "eye_guruguru", 80f, 0f) }));
        keys.Add(ShapeKeys.Make("가슴", "", "cycle", "", new[] { ("r0", "big", 100f, 20f) }, new[] { 0f, 0.5f, 1f }));
        var run = new ShapeKeyRunner();
        int changed = 0; run.Changed += () => changed++;
        run.Sync(keys);
        Eq(run.Layer().Count, 0, "처음엔 아무것도 덮지 않음");
        Eq(run.Press(0), "켬", "토글 켬");
        Check(run.Layer().Any(x => x.Shape == "shy" && x.Value == 100), "홍조 100");
        Eq(run.Press(0), "끔", "토글 끔");
        Check(!run.Layer().Any(x => x.Shape == "shy"), "끄면 레이어에서 빠짐 (원래 값으로)");
        run.Press(1); run.Press(2);
        Check(!run.IsOn(1) && run.IsOn(2), "같은 묶음은 하나만");
        Check(run.Layer().Any(x => x.Shape == "eye_guruguru" && x.Value == 80) && !run.Layer().Any(x => x.Shape == "eye_heart"), "묶음 레이어");
        Eq(run.Press(3), "50%", "단계 1"); Check(run.Layer().Any(x => x.Shape == "big" && Math.Abs(x.Value - 60) < 0.01f), "off 20 → on 100의 50% = 60");
        Eq(run.Press(3), "100%", "단계 2"); Eq(run.Press(3), "0%", "단계 처음으로");
        Check(!run.Layer().Any(x => x.Shape == "big"), "0단계는 덮지 않음");
        var msg = run.Message();
        Eq(msg["layer"].AsString, "keys", "메시지 레이어");
        // 정의가 바뀌면: 같은 자리·같은 이름만 상태를 남긴다
        var keys2 = JNode.Arr(); keys2.Add(keys[1]); keys2.Add(keys[2]);
        run.Sync(keys2);
        Check(!run.IsOn(0) && !run.IsOn(1), "자리가 바뀐 상태는 버림");
        Check(changed > 0, "Changed");
    }

    static void Toggles()
    {
        IrScene Scene(params (string Name, long FileId)[] nodes)
        {
            var arr = JNode.Arr();
            for (int i = 0; i < nodes.Length; i++)
                arr.Add(JNode.Obj().Set("id", i).Set("name", nodes[i].Name).Set("parent", i == 0 ? -1 : 0)
                    .Set("source", JNode.Obj().Set("guid", "av").Set("fileID", nodes[i].FileId.ToString())));
            return IrScene.FromJson(JNode.Obj().Set("irVersion", 1).Set("name", "s").Set("buffers", "").Set("nodes", arr).Set("materials", JNode.Arr()));
        }
        var sc = Scene(("Root", 1), ("Body", 2), ("Coat", 30), ("Hat", 40), ("Shoes", 50));
        var doc = new Document(sc, "x.ir.json");
        Eq(doc.Get(Addr.Toggles).Count, 0, "toggles original = empty");
        Check(ToggleSet.Press(doc, 0) == null, "press with no toggles → nothing");
        ToggleSet.Replace(doc, new[] { ToggleSet.Make("겉옷", new[] { 2, 3 }), ToggleSet.Make("신발", new[] { 4 }) }, "토글 만들기");
        Check(ToggleSet.IsOn(doc, doc.Get(Addr.Toggles)[0]), "new toggle is on (objects active)");
        Check(ToggleSet.Press(doc, 0) == false && !doc.Get(Addr.NodeActive(2)).AsBool && !doc.Get(Addr.NodeActive(3)).AsBool, "press → both objects off");
        Check(doc.Get(Addr.NodeActive(4)).AsBool, "other toggle untouched");
        doc.Set(Addr.NodeActive(3), true, "모자만");
        Check(!ToggleSet.IsOn(doc, doc.Get(Addr.Toggles)[0]), "partly on = off");
        Check(ToggleSet.Press(doc, 0) == true && doc.Get(Addr.NodeActive(2)).AsBool, "press partly-on → all on");
        doc.Undo();
        Check(!doc.Get(Addr.NodeActive(2)).AsBool && doc.Get(Addr.NodeActive(3)).AsBool, "one press = one undo step");
        Check(RenderSync.ToMessage(Addr.Toggles, doc.Get(Addr.Toggles)) == null, "toggle definitions send nothing to the renderer");

        // 다시 만들기: 노드 번호를 source로 따라가고 없어진 노드는 묶음에서 빠진다
        var after = Scene(("Root", 1), ("Body", 2), ("Hat", 40), ("Shoes", 50));   // Coat를 뺐다
        var r = Recompose.RemapChangeset(doc.ChangesetToJson(), sc, after);
        var t = r.Items.First(x => x["a"].AsString == Addr.Toggles.ToString())["v"];
        Check(t.Count == 2 && ToggleSet.Nodes(t[0]).SequenceEqual(new[] { 2 }) && ToggleSet.Nodes(t[1]).SequenceEqual(new[] { 3 }), "recompose remaps toggle nodes (Coat gone, Hat 3 → 2, Shoes 4 → 3)");

        // 표정 단축키
        var smile = FaceExpression.Make("Smile", "avatar", new[] { ("r1", "smile", 100f) });
        var fist = FaceExpression.Make("Fist", "avatar", new[] { ("r1", "fist", 100f) });
        Check(ToggleSet.PressExpression(doc, 0) == null, "empty F1 → nothing");
        ToggleSet.SetExpressionKey(doc, 2, smile);
        Eq(doc.Get(Addr.ExpressionKeys).Count, 3, "F3 set → list of 3 (F1·F2 empty)");
        Eq(ToggleSet.PressExpression(doc, 2), "Smile", "F3 → Smile");
        Eq(FaceExpression.Name(doc.Get(Addr.Expression)), "Smile", "expression applied");
        Eq(ToggleSet.PressExpression(doc, 2), "", "F3 again → removed");
        Check(doc.Get(Addr.Expression).IsNull, "expression removed");
        ToggleSet.SetExpressionKey(doc, 0, smile);
        Check(ToggleSet.ExpressionKey(doc, 2).IsNull && FaceExpression.Name(ToggleSet.ExpressionKey(doc, 0)) == "Smile", "same expression moves to the new key");
        ToggleSet.SetExpressionKey(doc, 1, fist);
        ToggleSet.PressExpression(doc, 0); ToggleSet.PressExpression(doc, 1);
        Eq(FaceExpression.Name(doc.Get(Addr.Expression)), "Fist", "F2 switches Smile → Fist");
        var animSmile = FaceExpression.Make("Smile", "anim", new[] { ("r1", "smile", 50f) });
        ToggleSet.SetExpressionKey(doc, 3, animSmile);
        Check(FaceExpression.Name(ToggleSet.ExpressionKey(doc, 0)) == "Smile" && ToggleSet.ExpressionKey(doc, 3)["source"].AsString == "anim", "same name, other source → both keys kept");
        ToggleSet.SetExpressionKey(doc, 3, JNode.Null);
        ToggleSet.SetExpressionKey(doc, 1, JNode.Null);
        Eq(doc.Get(Addr.ExpressionKeys).Count, 1, "clearing the last key trims the list");
        var r2 = Recompose.RemapChangeset(doc.ChangesetToJson(), sc, after);
        Check(r2.Items.Any(x => x["a"].AsString == Addr.ExpressionKeys.ToString()), "expression keys kept on recompose");
        var sdoc = new Document(sc, "x.ir.json");
        sdoc.LoadChangeset(doc.ChangesetToJson());
        Eq(sdoc.Get(Addr.Toggles).Count, 2, "toggles survive session save/load");
    }

    static void AnimWrite()
    {
        var sc = SampleScene();   // Avatar/Body (렌더러 r0)
        var smile = FaceExpression.Make("Smile", "tracking", new[] { ("r0", "smile", 80f), ("missing", "x", 10f) });
        var tracks = AnimWriter.FromExpression(sc, smile);
        Check(tracks.Count == 1 && tracks[0].Path == "Body" && tracks[0].Shape == "smile", "expression → one track at root-relative path (unknown renderer dropped)");
        var clip = AnimClip.Parse(AnimWriter.ToYaml("Smile", tracks));
        Check(clip.Name == "Smile" && clip.Curves.Count == 1 && clip.Curves[0].ClassId == 137 && clip.Curves[0].Attribute == "blendShape.smile" && clip.Curves[0].Path == "Body", "written .anim parses back");
        Check(Math.Abs(clip.Curves[0].Evaluate(0) - 80) < 1e-4, "constant value");
        var back = FaceExpression.FromClip(sc, clip, 0, out var miss, out _);
        Check(miss == 0 && FaceExpression.Shapes(back).Single().Value == 80, "saved expression loads as the same expression");

        // 녹화: 0 → 100 직선은 키 두 개로 줄고, 계속 0인 블렌드쉐이프는 빠진다
        var times = Enumerable.Range(0, 31).Select(i => i / 30f).ToList();
        var values = times.Select(t => new[] { t * 100, 0f }).ToList();
        var rec = AnimWriter.FromSamples(sc, new[] { ("r0", "smile"), ("r0", "vrc.v_aa") }, times, values);
        Check(rec.Count == 1 && rec[0].Keys.Count == 2, "linear ramp reduced to 2 keys, all-zero shape dropped");
        var rclip = AnimClip.Parse(AnimWriter.ToYaml("rec", rec));
        Check(Math.Abs(rclip.Curves[0].Evaluate(0.5f) - 50) < 0.5, "recorded curve evaluates mid value");
        Eq(AnimWriter.Crc32("123456789"), 0xCBF43926u, "crc32 check value");

        // VR 모션 녹화: 머슬 + RootT·RootQ → 휴머노이드 .anim → SamplePose로 다시 자세
        Check(Muscles.ToAnimAttribute("Left Index 1 Stretched") == "LeftHand.Index.1 Stretched" && Muscles.ToAnimAttribute("Spine Front-Back") == "Spine Front-Back" && Muscles.ToAnimAttribute("nope") == null, "muscle → anim attribute");
        foreach (var n in Muscles.Names) Check(Muscles.FromAnimAttribute(Muscles.ToAnimAttribute(n)!) == n, "anim attribute round trip " + n);
        var names = new[] { "Spine Front-Back", "Left Index 1 Stretched", "Left Arm Down-Up" };
        var q = new[] { 0f, 0.3826834f, 0f, 0.9238795f };   // y로 45°
        var frames = Enumerable.Range(0, 31).Select(i => new[] { i / 30f, -0.5f, 0.2f, 0, 0.9f + i / 300f, i / 30f, q[0], q[1], q[2], q[3] }).ToList();
        var motion = JNode.Obj().Set("fps", 30f).Set("muscles", JNode.Arr().Add("Spine Front-Back").Add("Left Index 1 Stretched").Add("Left Arm Down-Up"))
            .Set("frames", frames.Aggregate(JNode.Arr(), (a, f) => a.Add(JNode.FloatArray(f))));
        var dir = Path.Combine(Path.GetTempPath(), "vrcproto_motion_test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var mpath = MotionLibrary.Save(motion, dir, new DateTime(2026, 10, 24, 15, 30, 12));
        Check(Path.GetFileName(mpath) == "VR 녹화 2026-10-24 153012.anim", "motion file name");
        Check(Path.GetFileName(MotionLibrary.Save(motion, dir, new DateTime(2026, 10, 24, 15, 30, 12))) == "VR 녹화 2026-10-24 153012 (2).anim", "same time → numbered name");
        var mclip = AnimClip.Load(mpath);
        Check(mclip.IsHumanoid && Math.Abs(mclip.Length - 1) < 1e-4, "motion clip is humanoid, 1 s long");
        Check(mclip.Curves.First(c => c.Attribute == "LeftHand.Index.1 Stretched").Keys.Count == 2, "constant/linear muscle curve reduced");
        var mid = mclip.SamplePose(0.5f);
        var mm = PoseValue.MusclesOf(mid);
        Check(Math.Abs(mm["Spine Front-Back"] - 0.5f) < 0.01 && Math.Abs(mm["Left Index 1 Stretched"] + 0.5f) < 1e-3 && Math.Abs(mm["Left Arm Down-Up"] - 0.2f) < 1e-3, "sampled muscles");
        Check(Math.Abs(mid["bodyHeight"].AsFloat - 0.95f) < 0.01 && Math.Abs(mid["bodyRotation"][1].AsFloat - q[1]) < 1e-3, "sampled body height & rotation");
        Directory.Delete(dir, true);
    }

    static void FaceMapMerge()
    {
        var auto = new[] { ("eyeBlinkLeft", "r0", "EyeClosedLeft"), ("jawOpen", "r0", "JawOpen") };
        var m = JNode.Obj();
        Eq(FaceMap.Effective(auto, m).Count, 2, "no manual links → auto links as they are");
        m = FaceMap.Add(m, "eyeBlinkLeft", "r0", "eye_blink_1_L", 1.5f);
        var e = FaceMap.Effective(auto, m);
        Check(e.Count == 3 && e.Any(l => l.S == "eye_blink_1_L" && !l.Auto && Math.Abs(l.K - 1.5f) < 1e-5), "manual link added with strength");
        m = FaceMap.Add(m, "jawOpen", "r0", "JawOpen", 0.5f);
        e = FaceMap.Effective(auto, m);
        Check(e.Count == 3 && e.First(l => l.S == "JawOpen").Auto && Math.Abs(e.First(l => l.S == "JawOpen").K - 0.5f) < 1e-5, "auto link strength changed, still auto");
        m = FaceMap.Remove(m, "jawOpen", "r0", "JawOpen", auto: true);
        e = FaceMap.Effective(auto, m);
        Check(e.Count == 2 && !e.Any(l => l.S == "JawOpen") && m["off"].Count == 1 && m["add"].Count == 1, "removing an auto link turns it off (and drops its strength)");
        m = FaceMap.Add(m, "jawOpen", "r0", "JawOpen");
        Check(FaceMap.Effective(auto, m).Any(l => l.S == "JawOpen") && !m.Has("off"), "adding it back clears the off entry");
        m = FaceMap.Remove(m, "eyeBlinkLeft", "r0", "eye_blink_1_L", auto: false);
        m = FaceMap.Remove(m, "jawOpen", "r0", "JawOpen", auto: false);
        Check(m.Count == 0, "removing every manual change leaves the empty original");
        Eq(FaceMap.Label("eyeBlinkLeft"), "왼눈 감기", "label");
    }

    static void BlinkFixMath()
    {
        // 정점 0 = 오른쪽 눈꺼풀 (x +1), 정점 1 = 왼쪽 눈꺼풀 (x -1), 정점 2 = 입 (x 0)
        // Blink는 두 눈을 아래로 1, Blink_R은 오른쪽만. eye_big은 두 눈을 위로 0.5 (노멀도 0.2)
        MeshData.Shape Sh(string name, float[] d, float[]? nrm = null) => new MeshData.Shape { Name = name, Frames = { new MeshData.Frame { Weight = 100, DPos = d, DNrm = nrm } } };
        var m = new MeshData { VertexCount = 3, Pos = new float[] { 1, 0, 0, -1, 0, 0, 0, -1, 0 } };
        m.Shapes.Add(Sh("Blink", new float[] { 0, -1, 0, 0, -1, 0, 0, 0, 0 }));
        m.Shapes.Add(Sh("Blink_R", new float[] { 0, -1, 0, 0, 0, 0, 0, 0, 0 }));
        m.Shapes.Add(Sh("eye_big", new float[] { 0, 0.5f, 0, 0, 0.5f, 0, 0, 0, 0 }, new float[] { 0, 0.2f, 0, 0, 0.2f, 0, 0, 0, 0 }));
        m.Shapes.Add(Sh("vrc.v_aa", new float[] { 0, 0, 0, 0, 0, 0, 0, -1, 0 }));
        Check(MeshOps.IsBlinkName("Blink") && !MeshOps.IsBlinkName("vrc.v_aa") && MeshOps.IsBlinkName("まばたき") && !MeshOps.IsBlinkName("eye_big")
            && MeshOps.IsBlinkName("ｳｨﾝｸ２右") && MeshOps.IsBlinkName("笑い") && MeshOps.IsBlinkName("EyeClosedLeft"), "blink names");
        Check(MeshOps.IsBlinkName("eye_brink_1") && MeshOps.IsPlainBlink("eye_brink_1") && MeshOps.IsPlainBlink("mabataki"), "blink name typos (아바타 D eye_brink_1)");
        m.Shapes.Add(Sh("eye_close", new float[] { -0.3f, 0, 0, 0.3f, 0, 0, 0, 0, 0 }));   // 눈 사이를 좁히는 얼굴 편집 (옆으로)
        Check(MeshOps.IsClosingShape(m, m.Shapes[0]) && !MeshOps.IsClosingShape(m, m.Shapes[4]), "sideways eye_close is not a blink");
        m.Shapes.RemoveAt(4);
        Check(MeshOps.IsPlainBlink("eye_blink_1_L") && !MeshOps.IsPlainBlink("笑い") && !MeshOps.IsPlainBlink("EyeClosedJoyful"), "plain blink names (default targets)");
        var op = MeshOps.MakeBlinkFix(new[] { "Blink", "Blink_R" }, new[] { ("eye_big", 100f) });
        var r = MeshOps.Apply(m, JNode.Arr().Add(op));
        var b = r.Shapes.First(x => x.Name == "Blink").Frames[0];
        Check(Math.Abs(b.DPos![1] - -1.5f) < 1e-5 && Math.Abs(b.DPos[4] - -1.5f) < 1e-5, "both-eye blink: eye edit fully removed on both eyes");
        Check(Math.Abs(b.DNrm![1] - -0.2f) < 1e-5, "normal delta of the eye edit removed too");
        var br = r.Shapes.First(x => x.Name == "Blink_R").Frames[0].DPos!;
        Check(Math.Abs(br[1] - -1.5f) < 1e-5 && Math.Abs(br[4]) < 1e-6, "one-eye blink: only that eye, the other eye keeps its edit");
        Check(Math.Abs(m.Shapes[0].Frames[0].DPos![1] - -1f) < 1e-6, "original mesh untouched");
        var half = MeshOps.Apply(m, JNode.Arr().Add(MeshOps.MakeBlinkFix(new[] { "Blink" }, new[] { ("eye_big", 50f) }, 0.5f)));
        Check(Math.Abs(half.Shapes[0].Frames[0].DPos![1] - (-1 - 0.5f * 0.25f)) < 1e-5, "weight and strength scale the correction");
        // 깜빡임 100 + eye_big 100 = 원래 감은 눈 (편집 없는 깜빡임 100과 같다)
        float closed = r.Shapes[0].Frames[0].DPos![1] + 0.5f;
        Check(Math.Abs(closed - -1f) < 1e-5, "blink 100 with the edit lands where the original blink closes");

        // 눈마다 세기: 아바타 왼쪽 = −x (정점 1). 왼쪽 100% · 오른쪽 0%
        Check(MeshOps.SideOfName("Blink_L") == -1 && MeshOps.SideOfName("eye.R") == 1 && MeshOps.SideOfName("EyeBlinkLeft") == -1 && MeshOps.SideOfName("BlinkR") == 1
            && MeshOps.SideOfName("L_eye_big") == -1 && MeshOps.SideOfName("ｳｨﾝｸ２右") == 1 && MeshOps.SideOfName("eyeball") == 0 && MeshOps.SideOfName("Blink") == 0
            && MeshOps.SideOfName("눈 왼쪽 크게") == -1 && MeshOps.SideOfName("EYEBLINK") == 0, "side from names");
        Check(MeshOps.IsEyeName("eye_big") && MeshOps.IsEyeName("目_大") && !MeshOps.IsEyeName("vrc.blink") && !MeshOps.IsEyeName("mouth_a"), "eye edit names");
        Eq(BlinkFixPlan.LeftX(m), -1, "Blink_R moves at +x → left eye at −x");
        var mirrored = new MeshData { VertexCount = 3, Pos = m.Pos };
        mirrored.Shapes.Add(Sh("Blink_L", new float[] { 0, -1, 0, 0, 0, 0, 0, 0, 0 }));
        Eq(BlinkFixPlan.LeftX(mirrored), 1, "Blink_L moves at +x → left eye at +x");
        var oneEye = MeshOps.Apply(m, JNode.Arr().Add(MeshOps.WithEyes(MeshOps.MakeBlinkFix(new[] { "Blink" }, new[] { ("eye_big", 100f) }), 1f, 0f, -1)));
        var oe = oneEye.Shapes[0].Frames[0].DPos!;
        Check(Math.Abs(oe[4] - -1.5f) < 1e-5 && Math.Abs(oe[1] - -1f) < 1e-5, "left eye 100% fixed, right eye 0% untouched");
        Check(!MeshOps.WithEyes(JNode.Obj(), 1, 1, -1).Has("left"), "same 100% on both eyes adds nothing");

        // 자동 모드: 눈 편집 값을 바꾸면 깜빡임이 따라 바뀌고, 실행 취소 한 번에 둘 다 돌아간다
        var scene = IrScene.FromJson(JNode.Parse(@"{ ""irVersion"": 1, ""name"": ""A"", ""buffers"": """", ""nodes"": [ { ""id"": 0, ""name"": ""A"" } ],
          ""meshes"": [ { ""id"": ""mg:1"", ""name"": ""Body"", ""vertexCount"": 3 } ], ""renderers"": [ { ""id"": ""r1"", ""node"": 0, ""mesh"": ""mg:1"", ""materials"": [] } ] }"));
        var doc = new Document(scene, "");
        doc.Changed += addrs => BlinkFixPlan.Follow(doc, addrs.Where(a => a.Kind == AddrKind.BlendShape).Select(a => a.Target), id => id == "mg:1" ? m : null);
        var ir = scene.FindRenderer("r1")!;
        var first = BlinkFixPlan.AutoOp(doc, ir, m, null)!;
        Check(first["auto"].AsBool && first["targets"].Count == 2 && first["sources"].Count == 0, "auto: Blink·Blink_R targets, no eye edits yet");
        BlinkFixPlan.Set(doc, "mg:1", first, "auto on");
        doc.Set(Addr.BlendShape("r1", "eye_big"), 60, "eye big");
        var cur = BlinkFixPlan.Current(doc, "mg:1")!;
        Check(cur["sources"].Count == 1 && Math.Abs(cur["sources"][0]["weight"].AsFloat - 60) < 1e-4, "auto follows eye_big = 60");
        doc.Undo();
        Check(BlinkFixPlan.Current(doc, "mg:1")!["sources"].Count == 0 && doc.Get(Addr.BlendShape("r1", "eye_big")).AsFloat == 0, "one undo reverts the value and the follow");
        Eq(doc.UndoLabel, "auto on", "follow made no undo step of its own");
        doc.Redo();
        Check(BlinkFixPlan.Current(doc, "mg:1")!["sources"].Count == 1, "redo brings both back");

        // 입 맞추기: 다문 입 편집(mouth_straight)을 켠 채 입을 벌리면 원래 벌린 입, 다물면 편집 그대로
        Check(MeshOps.IsMouthName("mouth_straight") && MeshOps.IsMouthName("vrc.v_aa") && MeshOps.IsMouthName("あ") && MeshOps.IsMouthName("JawOpen")
            && !MeshOps.IsMouthName("vrc.v_sil") && !MeshOps.IsMouthName("eye_big"), "mouth names");
        Check(MeshOps.IsMouthOpenName("vrc.v_aa") && MeshOps.IsMouthOpenName("mouth_A_1") && MeshOps.IsMouthOpenName("mouth_I_2_open") && MeshOps.IsMouthOpenName("あ")
            && MeshOps.IsMouthOpenName("jawOpen") && !MeshOps.IsMouthOpenName("mouth_straight") && !MeshOps.IsMouthOpenName("mouth_angry_1") && !MeshOps.IsMouthOpenName("vrc.v_pp"),
            "mouth-open names (default targets)");
        var mm = new MeshData { VertexCount = 3, Pos = new float[] { 0.1f, 0, 0, -0.1f, 0, 0, 0, 0.5f, 0 } };   // 입술 양쪽 · 눈
        mm.Shapes.Add(Sh("mouth_straight", new float[] { 0, 0.2f, 0, 0, 0.2f, 0, 0, 0, 0 }));
        mm.Shapes.Add(Sh("vrc.v_aa", new float[] { 0, -1, 0, 0, -1, 0, 0, 0, 0 }));
        var mf = MeshOps.Apply(mm, JNode.Arr().Add(MeshOps.MakeMouthFix(new[] { "vrc.v_aa" }, new[] { ("mouth_straight", 100f) })));
        var aa = mf.Shapes.First(x => x.Name == "vrc.v_aa").Frames[0].DPos!;
        Check(Math.Abs(aa[1] + 0.2f - -1f) < 1e-5 && Math.Abs(aa[4] + 0.2f - -1f) < 1e-5, "aa 100 + mouth_straight 100 = original open mouth");
        Check(Math.Abs(mf.Shapes[0].Frames[0].DPos![1] - 0.2f) < 1e-6, "mouth edit itself untouched (closed mouth stays edited)");
        Check(MeshOps.IsShapeFix(MeshOps.MouthFix) && MeshOps.IsShapeFix(MeshOps.BlinkFix) && MeshOps.IsShapeFix(MeshOps.ShapeMove) && !MeshOps.IsShapeFix(MeshOps.Weld), "shape fix ops");

        // 다문 입 다듬기: 입 선 띠(가운데·끝 위아래 정점 6개 + UV 경계로 갈라진 같은 자리 정점 1개)와 피부(움직이지 않음)
        //   x = -1, 0, +1 에서 위·아래. 편집(straight)이 띠를 그대로 두께 0.2(가운데)~0.4(끝)로 만든다
        var ln = new MeshData { VertexCount = 9 };
        ln.Pos = new float[] { -1, 0.2f, 0, -1, -0.2f, 0, 0, 0.1f, 0, 0, -0.1f, 0, 1, 0.2f, 0, 1, -0.2f, 0, /* 6 = 4와 같은 자리 */ 1, 0.2f, 0, /* 피부 */ 0, 5, 0, 3, 5, 0 };
        ln.Subs.Add(new MeshData.Sub { Indices = new[] { 0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 7, 8, 7 } });
        var dz = new float[27]; for (int v = 0; v < 6; v++) dz[v * 3 + 2] = 0.01f;   // 띠만 움직임 (앞으로 조금)
        ln.Shapes.Add(new MeshData.Shape { Name = "mouth_straight", Frames = { new MeshData.Frame { Weight = 100, DPos = dz } } });
        var lineOp = MeshOps.MakeMouthLine(ln, "mouth_straight", 0.5f, 1f, 0.1f)!;
        Check(lineOp != null && lineOp["shape"].AsString == "mouth_straight", "mouth line band found");
        // 후보 찾기 (섬을 한 번만 나눔): MakeMouthLine이 띠를 찾는 것과 같아야 한다 — 피부만 움직이는 것·없는 이름은 빠진다
        var lc = ln.Clone();
        var skinOnly = new float[27]; skinOnly[7 * 3 + 1] = 0.1f; skinOnly[8 * 3 + 1] = 0.1f;
        lc.Shapes.Add(new MeshData.Shape { Name = "mouth_smile", Frames = { new MeshData.Frame { Weight = 100, DPos = skinOnly } } });
        var cands = MeshOps.MouthLineShapes(lc, new[] { "mouth_straight", "mouth_smile", "nope" });
        Check(cands.SequenceEqual(new[] { "mouth_straight" }) && MeshOps.MakeMouthLine(lc, "mouth_smile", 1, 1, 0) == null, "mouth line candidates = shapes MakeMouthLine accepts");
        var tidy = MeshOps.Apply(ln, JNode.Arr().Add(lineOp!)).Shapes[0].Frames[0].DPos!;
        float Y(int v) => ln.Pos[v * 3 + 1] + tidy[v * 3 + 1];
        Check(Math.Abs(Y(0) - Y(1) - 0.2f) < 1e-4 && Math.Abs(Y(4) - Y(5) - 0.2f) < 1e-4, "ends half as thick (0.4 → 0.2)");
        Check(Math.Abs((Y(0) + Y(1)) / 2 - 0.1f) < 1e-4, "ends raised by curve");
        Check(Math.Abs(Y(2) - 0.1f) < 1e-5 && Math.Abs(Y(3) + 0.1f) < 1e-5, "center unchanged (center 1, curve 0 at middle)");
        Check(Math.Abs(Y(6) - Y(4)) < 1e-6, "same-position vertex (UV seam) moves together");
        Check(Math.Abs(tidy[7 * 3 + 1]) < 1e-9 && Math.Abs(tidy[8 * 3 + 1]) < 1e-9, "skin not touched");
        Check(Math.Abs(ln.Shapes[0].Frames[0].DPos![1]) < 1e-9, "original shape untouched");
        var offTidy = MeshOps.Apply(ln, JNode.Arr().Add(lineOp!.Clone().Set("enabled", false))).Shapes[0].Frames[0].DPos!;
        Check(Enumerable.Range(0, 27).All(i => Math.Abs(offTidy[i] - dz[i]) < 1e-9) && MeshOps.IsOff(lineOp!.Clone().Set("enabled", false)) && !MeshOps.IsOff(lineOp!), "tidy toggled off = original shape (values kept)");
        // 가늘어지기 시작 · 끝 둥글기 · Z-up 메시 (플러그인 Core/FaceFix.cs MouthLine과 같은 계산)
        var tp0 = MeshOps.MakeMouthLine(ln, "mouth_straight", 0.5f, 1f, 0f, 0f)!;
        float Y0(int v) => ln.Pos[v * 3 + 1] + MeshOps.Apply(ln, JNode.Arr().Add(tp0)).Shapes[0].Frames[0].DPos![v * 3 + 1];
        Check(Math.Abs(Y0(0) - Y0(1) - 0.2f) < 1e-4 && tp0["tidy"]["taper"].AsFloat == 0f && lineOp!["tidy"]["taper"].AsFloat == 0.25f, "taper kept in tidy, ends still half");
        var rd = MeshOps.MakeMouthLine(ln, "mouth_straight", 0.5f, 1f, 0f, 0.25f, 1f)!;
        var rdp = rd["points"].ToFloatArray();
        bool anyDx = false; for (int i = 0; i + 5 < rdp.Length; i += 6) anyDx |= Math.Abs(rdp[i + 3]) > 1e-7f;
        Check(anyDx && rd["tidy"]["round"].AsFloat == 1f, "round ends move cap vertices sideways");
        var lz = ln.Clone();
        for (int v = 0; v < lz.VertexCount; v++) { var y = lz.Pos[v * 3 + 1]; lz.Pos[v * 3 + 1] = lz.Pos[v * 3 + 2]; lz.Pos[v * 3 + 2] = y; }
        var zd = new float[27]; for (int v = 0; v < 6; v++) zd[v * 3 + 1] = 0.01f;
        lz.Shapes[0].Frames[0].DPos = zd;
        lz.Pos[7 * 3 + 2] = 50; lz.Pos[8 * 3 + 2] = 50;   // 피부를 높이(z) 쪽으로 길게: Z-up
        var zop = MeshOps.MakeMouthLine(lz, "mouth_straight", 0.5f, 1f, 0f)!;
        var ztidy = MeshOps.Apply(lz, JNode.Arr().Add(zop)).Shapes[0].Frames[0].DPos!;
        float Z(int v) => lz.Pos[v * 3 + 2] + ztidy[v * 3 + 2];
        Check(MeshOps.UpAxis(lz) == 2 && MeshOps.UpAxis(ln) == 1 && Math.Abs(Z(0) - Z(1) - 0.2f) < 1e-4, "Z-up mesh thins along z");
        // 얼굴 추적 입술 모양은 기본 대상, 보정(*Correction)·MouthClosed·혀는 아님. 눈썹은 눈 편집이 아님
        Check(MeshOps.IsMouthOpenName("MouthSmileLeft") && MeshOps.IsMouthOpenName("LipFunnel") && MeshOps.IsMouthOpenName("MouthUpperUp") && MeshOps.IsMouthOpenName("vrc.v_aa.FT")
            && !MeshOps.IsMouthOpenName("MouthClosed") && !MeshOps.IsMouthOpenName("TongueOut") && !MeshOps.IsMouthOpenName("MUU-JawOpenCorrection"), "face tracking mouth names");
        Check(!MeshOps.IsEyeName("eyebrow_up") && !MeshOps.IsEyeName("眉_怒") && MeshOps.IsEyeName("eye_big"), "eyebrows are not eye edits");
        Check(MeshOps.SeparatorName("---------Face_Custom---------") == "Face_Custom" && MeshOps.SeparatorName("mouth_straight") == null, "separator names");
        var ordered = BlinkFixPlan.Ordered(JNode.Arr().Add(MeshEdits.WeldOp(0.001f)).Add(MeshOps.MakeMouthFix(new[] { "a" }, new[] { ("b", 1f) })).Add(lineOp!));
        Eq(string.Join(",", ordered.Items.Select(o => o["op"].AsString)), "shapeMove,mouthFix,weld", "mesh edit order: tidy → fixes → rest");
    }

    static void OutfitRecompose()
    {
        IrScene Scene(params (string Name, long FileId)[] nodes)
        {
            var arr = JNode.Arr();
            for (int i = 0; i < nodes.Length; i++)
                arr.Add(JNode.Obj().Set("id", i).Set("name", nodes[i].Name).Set("parent", i == 0 ? -1 : 0)
                    .Set("source", JNode.Obj().Set("guid", "av").Set("fileID", nodes[i].FileId.ToString())));
            return IrScene.FromJson(JNode.Obj().Set("irVersion", 1).Set("name", "s").Set("buffers", "").Set("nodes", arr)
                .Set("materials", JNode.Arr().Add(JNode.Obj().Set("guid", "m1").Set("shaderGuid", ""))));
        }
        var before = Scene(("Root", 1), ("Body", 2), ("Clip", 30), ("Coat", 40));
        var after = Scene(("Root", 1), ("Body", 2), ("Coat", 40));   // Clip을 뺐다
        var cs = JNode.Arr()
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.NodeActive(3).ToString()).Set("v", false))
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.NodeActive(2).ToString()).Set("v", false))
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.MatFloat("m1", "_X").ToString()).Set("v", 1))
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.MatFloat("gone", "_X").ToString()).Set("v", 1))
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.Environment.ToString()).Set("v", JNode.Arr()));
        var r = Recompose.RemapChangeset(cs, before, after);
        var addrs = r.Items.Select(x => x["a"].AsString).ToList();
        Check(addrs.Contains(Addr.NodeActive(2).ToString()) && r.Items.First(x => x["a"].AsString == Addr.NodeActive(2).ToString())["v"].AsBool == false, "Coat off follows source (3 → 2)");
        Eq(addrs.Count(a => a.StartsWith("NodeActive")), 1, "removed item's node override dropped");
        Check(addrs.Contains(Addr.MatFloat("m1", "_X").ToString()) && !addrs.Contains(Addr.MatFloat("gone", "_X").ToString()), "material overrides kept only if material exists");
        Check(addrs.Contains(Addr.Environment.ToString()), "environment kept");

        // 핸들로 옮긴 자리 (Addr.NodeTransform): 원본 = IR 로컬 TRS, 다시 만들면 source로 따라가고, 부모가 바뀌면 버린다. 아바타 자리는 그대로
        var doc = new Document(before, "x.ir.json");
        Eq(doc.Get(Addr.NodeTransform(3)).Count, 10, "node transform original = IR TRS (10 floats)");
        Check(Values.Equal(doc.Get(Addr.AvatarTransform), JNode.FloatArray(Addr.Identity)), "avatar transform original = identity");
        var moved = JNode.FloatArray(new float[] { 0, 0.1f, 0, 0, 0, 0, 1, 1, 1, 1 });
        doc.Set(Addr.NodeTransform(3), moved, "옮기기");
        Check(doc.IsModified(Addr.NodeTransform(3)), "node transform recorded");
        Eq(RenderSync.ToMessage(Addr.NodeTransform(3), moved)!["node"].AsInt, 3, "node transform → setNodeTransform");
        var cs2 = JNode.Arr()
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.NodeTransform(3).ToString()).Set("v", moved))
            .Add(JNode.Obj().Set("op", "set").Set("a", Addr.AvatarTransform.ToString()).Set("v", JNode.FloatArray(new float[] { 1, 0, 0, 0, 0, 0, 1 })));
        var r2 = Recompose.RemapChangeset(cs2, before, after).Items.Select(x => x["a"].AsString).ToList();
        Check(r2.Contains(Addr.NodeTransform(2).ToString()) && r2.Contains(Addr.AvatarTransform.ToString()), "moved transform follows source (3 → 2), avatar transform kept");
        var reparented = Scene(("Root", 1), ("Body", 2), ("Coat", 40));
        reparented.Nodes[2].Parent = 1;   // 붙일 곳을 바꿨다
        Check(!Recompose.RemapChangeset(cs2, before, reparented).Items.Any(x => x["a"].AsString.StartsWith("NodeTransform")), "moved transform dropped when the parent changes");
        doc.Undo();
        Check(!doc.IsModified(Addr.NodeTransform(3)), "undo clears node transform");
    }

    /// 자세: 머슬 표(Unity와 같은 95개), 한국어 이름, 자세 값 편집, Document·렌더러 메시지·Changeset
    static void WardrobeParts()
    {
        Check(Wardrobe.Words("ArmWarmer").SequenceEqual(new[] { "arm", "warmer" }), "words: camel case");
        Check(Wardrobe.Words("Hair_TwinTail").SequenceEqual(new[] { "hair", "twin", "tail" }), "words: underscore + camel");
        Check(Wardrobe.Words("TShirt").SequenceEqual(new[] { "tshirt" }), "words: leading capital kept");
        // 0 Root ─ 1 Armature ─ 2 Hips ─ 3 Spine ─ 4 Head ─ 5 HairBone,  2 ─ 6 LeftUpperLeg,  0 ─ 7.. 메시, 0 ─ 20 아이템 ─ 21 메시
        var s = new IrScene();
        int[] parents = { -1, 0, 1, 2, 3, 4, 2 };
        string[] names = { "Root", "Armature", "Hips", "Spine", "Head", "HairBone", "LeftUpperLeg" };
        for (int i = 0; i < parents.Length; i++) s.Nodes.Add(new IrNode { Id = i, Name = names[i], Parent = parents[i] });
        s.Humanoid["Hips"] = 2; s.Humanoid["Spine"] = 3; s.Humanoid["Head"] = 4; s.Humanoid["LeftUpperLeg"] = 6;
        var meshes = new (string Name, int[] Bones)[]
        {
            ("Body", new[] { 2, 3, 4, 6 }), ("Underwear", new[] { 2 }), ("Kemono_Ear", new[] { 4 }), ("Hair_TwinTail", new[] { 5 }),
            ("Hairpin", new[] { 4 }), ("Mesh_A", new[] { 5, 4 }), ("Mesh_B", new[] { 2, 6 }), ("Mesh_C", new[] { 6 }), ("Mesh_Face", new[] { 4 }), ("Mesh_D", new[] { 4 }),
        };
        foreach (var (n, bones) in meshes)
        {
            int node = s.Nodes.Count;
            s.Nodes.Add(new IrNode { Id = node, Name = n, Parent = 0 });
            s.Renderers.Add(new IrRenderer { Id = "r" + node, Node = node, Mesh = "m" + node, Bones = bones });
            var m = new IrMesh { Id = "m" + node };
            if (n == "Mesh_D") m.BlendShapes.Add(new IrBlendShape { Name = "vrc.v_aa" });
            s.Meshes.Add(m);
        }
        int item = s.Nodes.Count;
        s.Nodes.Add(new IrNode { Id = item, Name = "Item", Parent = 0 });
        s.Nodes.Add(new IrNode { Id = item + 1, Name = "Skirt", Parent = item });
        s.Renderers.Add(new IrRenderer { Id = "ri", Node = item + 1, Mesh = "mi", Bones = new[] { 2 } });
        s.Outfits.Add(new IrOutfit { Node = item, Name = "Item" });
        var parts = Wardrobe.Parts(s).ToDictionary(p => p.Name, p => p.Kind);
        Eq(parts.Count, meshes.Length, "item meshes excluded");
        Eq(parts["Body"], "body", "Body → body");
        Eq(parts["Underwear"], "outfit", "Underwear → outfit (not 'ear')");
        Eq(parts["Kemono_Ear"], "body", "Kemono_Ear → body (animal ears are part of the avatar)");
        Eq(parts["Hair_TwinTail"], "hair", "Hair_TwinTail → hair (tail word ignored)");
        Eq(parts["Hairpin"], "accessory", "Hairpin → accessory");
        Eq(parts["Mesh_A"], "hair", "only head bones → hair");
        Eq(parts["Mesh_B"], "outfit", "hips + leg → outfit");
        Eq(parts["Mesh_C"], "accessory", "one limb bone → accessory");
        Eq(parts["Mesh_Face"], "body", "Face word → body");
        Eq(parts["Mesh_D"], "body", "viseme blendshapes → body");
        // 가중치가 실린 뼈만: Mesh_B가 머리 뼈에만 가중치를 두면 헤어
        var w = Wardrobe.Parts(s, r => r.Id == s.Renderers.First(x => s.Nodes[x.Node].Name == "Mesh_B").Id ? new[] { 4 } : null).ToDictionary(p => p.Name, p => p.Kind);
        Eq(w["Mesh_B"], "hair", "weighted bones override listed bones");
        Eq(Wardrobe.KindLabel("outfit"), "옷", "kind label");
        // EditorOnly 조각도 묶음에 든다 (처음엔 꺼져 있음). 조상이 EditorOnly여도 사슬로 알린다
        int eo = s.Nodes.Count;
        s.Nodes.Add(new IrNode { Id = eo, Name = "Dummy", Parent = 0, EditorOnly = true });
        s.Nodes.Add(new IrNode { Id = eo + 1, Name = "Socks", Parent = eo });
        s.Renderers.Add(new IrRenderer { Id = "re", Node = eo + 1, Mesh = "me", Bones = new[] { 6 } });
        var socks = Wardrobe.Parts(s).Single(p => p.Name == "Socks");
        Eq(socks.Kind, "outfit", "EditorOnly part kept in wardrobe");
        Check(socks.EditorOnly.SequenceEqual(new[] { eo }), "EditorOnly ancestor chain");
        Eq(Wardrobe.Parts(s).Single(p => p.Name == "Body").EditorOnly.Length, 0, "no EditorOnly chain");
    }

    static void PoseCore()
    {
        Eq(Muscles.Names.Count, 95, "muscle count");
        Eq(Muscles.Names.Distinct().Count(), 95, "muscle names unique");
        Eq(Muscles.Names[0], "Spine Front-Back", "first muscle");
        Eq(Muscles.Names[94], "Right Little 3 Stretched", "last muscle");
        Eq(Muscles.Label("Left Forearm Stretch"), "왼 아래팔 펴기", "label forearm");
        Eq(Muscles.Label("Right Index 2 Stretched"), "오른 검지 둘째 마디 펴기", "label finger");
        Check(Muscles.Names.All(n => Muscles.Label(n) != n), "every muscle has a Korean label");
        Check(Muscles.Names.All(n => Muscles.GroupOrder.Contains(Muscles.Group(n))), "every muscle in a group");
        Eq(Muscles.FromAnimAttribute("LeftHand.Index.1 Stretched"), "Left Index 1 Stretched", "anim finger name");
        Eq(Muscles.FromAnimAttribute("RightHand.Thumb.Spread"), "Right Thumb Spread", "anim spread name");
        Eq(Muscles.FromAnimAttribute("RootT.y"), null, "root curve is not a muscle");
        Check(PosePresets.All.All(p => p.Muscles.Keys.All(Muscles.IsMuscle)), "presets use known muscles only");

        var p0 = PosePresets.Find("attention")!.Value;
        Eq(PoseValue.Name(p0), "차렷", "preset name in value");
        var p1 = PoseValue.With(p0, "Head Turn Left-Right", 0.5f);
        Eq(PoseValue.MusclesOf(p1)["Head Turn Left-Right"], 0.5f, "With sets muscle");
        Eq(PoseValue.Name(p1), "", "edited pose loses preset name");
        Eq(PoseValue.MusclesOf(p1)["Left Arm Down-Up"], PoseValue.MusclesOf(p0)["Left Arm Down-Up"], "With keeps other muscles");
        Check(!PoseValue.MusclesOf(PoseValue.Make(new Dictionary<string, float> { ["Jaw Close"] = 0f })).ContainsKey("Jaw Close"), "zero muscles omitted");

        var doc = new Document(new IrScene(), "x.ir.json");
        Check(doc.Get(Addr.Pose).IsNull, "original pose = null (as loaded)");
        doc.Set(Addr.Pose, p0, "pose");
        Check(doc.IsModified(Addr.Pose), "pose recorded");
        var cs = doc.ChangesetToJson();
        var doc2 = new Document(new IrScene(), "x.ir.json"); doc2.LoadChangeset(cs);
        Eq(PoseValue.Name(doc2.Get(Addr.Pose)), "차렷", "pose survives session round trip");
        doc.Undo();
        Check(doc.Get(Addr.Pose).IsNull, "undo back to loaded pose");
        var msg = RenderSync.ToMessage(Addr.Pose, p0)!;
        Eq(Msg.Type(msg), Msg.SetPose, "setPose message");
        Check(RenderSync.ToMessage(Addr.Pose, JNode.Null)!["pose"].IsNull, "null pose message = reset");
        var kept = Recompose.RemapChangeset(cs, new IrScene(), new IrScene());
        Check(kept.Items.Any(x => x["a"].AsString == Addr.Pose.ToString()), "pose kept when items change");

        // 뷰포트 본 조작 결과 (렌더러 poseEdited)
        Eq(HumanBones.Label("LeftUpperArm"), "왼위팔", "bone label");
        Eq(HumanBones.Label("RightIndexIntermediate"), "오른검지 2", "finger bone label");
        Eq(HumanBones.Label("Unknown"), "Unknown", "unknown bone label kept");
        var rest = new Dictionary<string, float> { ["Left Arm Down-Up"] = 0.4f, ["Right Arm Down-Up"] = 0.4f };
        var vpRest = JNode.Obj().Set("muscles", JNode.Obj().Set("Left Arm Down-Up", 0.40002).Set("Right Arm Down-Up", 0.4).Set("Spine Front-Back", 0.0002));
        Check(PoseValue.FromViewport(vpRest, rest) == null, "viewport pose equal to rest → reset (no override)");
        var vp = JNode.Obj().Set("muscles", JNode.Obj().Set("Left Arm Down-Up", -0.12345).Set("Right Arm Down-Up", 0.4).Set("Not A Muscle", 1));
        var rec = PoseValue.FromViewport(vp, rest)!;
        Eq(PoseValue.MusclesOf(rec)["Left Arm Down-Up"], -0.1235f, "viewport muscles rounded");
        Check(!PoseValue.MusclesOf(rec).ContainsKey("Not A Muscle"), "unknown muscles dropped");
        Eq(PoseValue.Name(rec), "", "viewport pose has no name");
        var vpRot = JNode.Obj().Set("muscles", JNode.Obj().Set("Left Arm Down-Up", 0.4).Set("Right Arm Down-Up", 0.4)).Set("bodyRotation", JNode.FloatArray(new[] { 0f, 0.1f, 0f, 0.995f }));
        Check(PoseValue.FromViewport(vpRot, rest)!.Has("bodyRotation"), "body rotation alone is not rest");
        doc.Set(Addr.Pose, rec, "자세: 왼위팔 돌리기");
        Eq(doc.UndoLabel, "자세: 왼위팔 돌리기", "viewport edit = one undo step");
        doc.Undo();
        Check(doc.Get(Addr.Pose).IsNull, "undo viewport edit");

        // 대칭·본 하나 되돌리기·반대쪽 복사
        Eq(string.Join(",", Muscles.OfBone("LeftUpperArm")), "Left Arm Down-Up,Left Arm Front-Back,Left Arm Twist In-Out", "muscles of upper arm");
        Eq(string.Join(",", Muscles.OfBone("RightIndexProximal")), "Right Index 1 Stretched,Right Index Spread", "muscles of finger proximal");
        Eq(string.Join(",", Muscles.OfBone("Chest")), "Chest Front-Back,Chest Left-Right,Chest Twist Left-Right", "chest muscles (not UpperChest)");
        Eq(string.Join(",", Muscles.OfBone("LeftEye")), "Left Eye Down-Up,Left Eye In-Out", "eye muscles");
        Eq(Muscles.OfBone("Hips").Count, 0, "hips has no muscles");
        Check(Muscles.Names.All(n => Muscles.Names.Contains(Muscles.Mirror(n).Name)), "every muscle has a mirror");
        Eq(Muscles.OppositeBone("LeftLowerLeg"), "RightLowerLeg", "opposite bone");
        var asym = JNode.Obj().Set("name", "x").Set("muscles", JNode.Obj().Set("Left Arm Down-Up", -0.6).Set("Right Arm Down-Up", 0.4).Set("Head Tilt Left-Right", 0.2).Set("Spine Front-Back", 0.1))
            .Set("bodyRotation", JNode.FloatArray(new[] { 0.1f, 0.2f, 0.3f, 0.927f }));
        var l2r = PoseValue.MusclesOf(PoseValue.Mirrored(asym, PoseValue.Symmetry.LeftToRight));
        Eq(l2r["Right Arm Down-Up"], -0.6f, "left → right copies arm");
        Eq(l2r["Head Tilt Left-Right"], 0.2f, "left → right keeps head");
        Eq(PoseValue.MusclesOf(PoseValue.Mirrored(asym, PoseValue.Symmetry.RightToLeft))["Left Arm Down-Up"], 0.4f, "right → left copies arm");
        var flip = PoseValue.Mirrored(asym, PoseValue.Symmetry.Flip);
        var fm = PoseValue.MusclesOf(flip);
        Check(fm["Left Arm Down-Up"] == 0.4f && fm["Right Arm Down-Up"] == -0.6f, "flip swaps sides");
        Eq(fm["Head Tilt Left-Right"], -0.2f, "flip negates sideways center muscle");
        Eq(fm["Spine Front-Back"], 0.1f, "flip keeps front-back");
        Eq(string.Join(",", flip["bodyRotation"].ToFloatArray()), "0.1,-0.2,-0.3,0.927", "flip mirrors body rotation");
        Eq(PoseValue.Name(flip), "", "symmetry edit drops name");
        Eq(PoseValue.MusclesOf(PoseValue.Mirrored(flip, PoseValue.Symmetry.Flip))["Head Tilt Left-Right"], 0.2f, "flip twice = original");
        var rest2 = new Dictionary<string, float> { ["Left Arm Down-Up"] = 0.4f, ["Right Arm Down-Up"] = 0.4f };
        Check(PoseValue.BoneModified(asym, "LeftUpperArm", rest2) && !PoseValue.BoneModified(asym, "RightUpperArm", rest2), "bone modified check");
        var rb = PoseValue.ResetBone(asym, "LeftUpperArm", rest2);
        Eq(PoseValue.MusclesOf(rb)["Left Arm Down-Up"], 0.4f, "reset bone → rest value");
        Eq(PoseValue.MusclesOf(rb)["Head Tilt Left-Right"], 0.2f, "reset bone keeps others");
        Check(rb.Has("bodyRotation") && !PoseValue.ResetBone(asym, "Hips", rest2).Has("bodyRotation"), "reset hips clears body rotation only");
        Eq(PoseValue.MusclesOf(PoseValue.CopyBoneToOpposite(asym, "LeftUpperArm"))["Right Arm Down-Up"], -0.6f, "copy bone to opposite");
        Eq(PoseValue.MusclesOf(PoseValue.CopyBoneToOpposite(asym, "Head"))["Head Tilt Left-Right"], 0.2f, "copy center bone = no change");

        // 손: 쥐기·손 모양
        var handRest = new Dictionary<string, float>(PosePresets.Find("tpose")!.Muscles);
        var open = PoseValue.Make(handRest);
        Check(HandPose.Grip(open, "Both", handRest) < 0.001f, "rest hand grip 0");
        var fist = HandPose.WithGrip(open, "Both", 1, handRest);
        Check(Math.Abs(HandPose.Grip(fist, "Both", handRest) - 1) < 0.001f, "full grip = fist");
        Eq(PoseValue.MusclesOf(fist)["Left Index 2 Stretched"], -1f, "fist curls fingers");
        var leftHalf = HandPose.WithGrip(open, "Left", 0.5f, handRest);
        Check(Math.Abs(HandPose.Grip(leftHalf, "Left", handRest) - 0.5f) < 0.001f && HandPose.Grip(leftHalf, "Right", handRest) < 0.001f, "grip one side only");
        Check(PoseValue.IsRest(HandPose.WithGrip(fist, "Both", 0, handRest), handRest), "grip 0 = back to rest");
        var point = PoseValue.MusclesOf(HandPose.WithShape(open, "Right", "point", handRest));
        Check(point["Right Index 2 Stretched"] == handRest["Right Index 2 Stretched"] && point["Right Middle 2 Stretched"] == -1f, "point: index open, others curled");
        Eq(point["Left Middle 2 Stretched"], handRest["Left Middle 2 Stretched"], "shape on one hand keeps the other");
        Check(PoseValue.IsRest(HandPose.WithShape(fist, "Both", "open", handRest), handRest), "open shape = rest hand");

        var dir = Path.Combine(Path.GetTempPath(), "vrcproto_pose_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = PoseLibrary.Save("내 자세", p1, dir);
            var back = PoseLibrary.Load(path);
            Eq(PoseValue.Name(back), "내 자세", "saved pose takes file name");
            Eq(PoseValue.MusclesOf(back)["Head Turn Left-Right"], 0.5f, "saved pose muscles");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// .anim(YAML) 읽기: 새 형식("- serializedVersion: 2" 다음 curve)과 예전 형식("- curve:"), 에르미트 보간, 계단, m_EditorCurves 무시
    static void AnimParse()
    {
        string Curve(bool newFormat, string attr, string path, int classId, params (float t, float v, string slope)[] keys)
        {
            string F(float x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var k = string.Concat(keys.Select(x => $"      - serializedVersion: 3\n        time: {F(x.t)}\n        value: {F(x.v)}\n        inSlope: {x.slope}\n        outSlope: {x.slope}\n        tangentMode: 0\n        weightedMode: 0\n        inWeight: 0.33333334\n        outWeight: 0.33333334\n"));
            var head = newFormat ? "  - serializedVersion: 2\n    curve:\n" : "  - curve:\n";
            return head + "      serializedVersion: 2\n      m_Curve:\n" + k + "      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n"
                + $"    attribute: {attr}\n    path: {path}\n    classID: {classId}\n    script: {{fileID: 0}}\n";
        }
        foreach (var nf in new[] { true, false })
        {
            var text = "%YAML 1.1\n--- !u!74 &7400000\nAnimationClip:\n  m_Name: Wave\n  m_PositionCurves: []\n  m_FloatCurves:\n"
                + Curve(nf, "Left Arm Down-Up", "", 95, (0, 0, "0"), (1, 1, "0"))
                + Curve(nf, "LeftHand.Index.1 Stretched", "", 95, (0, 0.5f, "0"))
                + Curve(nf, "blendShape.smile", "Body", 137, (0, 100, "Infinity"), (1, 0, "Infinity"))
                + Curve(nf, "RootQ.x", "", 95, (0, 0, "0")) + Curve(nf, "RootQ.y", "", 95, (0, 0, "0")) + Curve(nf, "RootQ.z", "", 95, (0, 0, "0")) + Curve(nf, "RootQ.w", "", 95, (0, 2, "0"))
                + "  m_PPtrCurves: []\n  m_EditorCurves:\n" + Curve(nf, "Jaw Close", "", 95, (0, 1, "0"));
            var clip = AnimClip.Parse(text);
            string f = nf ? "new format" : "old format";
            Eq(clip.Name, "Wave", f + ": name");
            Eq(clip.Curves.Count, 7, f + ": float curves only (editor curves ignored)");
            Check(clip.IsHumanoid, f + ": humanoid");
            Eq(clip.Length, 1f, f + ": length");
            var arm = clip.Curves[0];
            Check(Math.Abs(arm.Evaluate(0.5f) - 0.5f) < 1e-4, f + ": hermite midpoint (flat tangents)");
            Check(Math.Abs(arm.Evaluate(0.25f) - 0.15625f) < 1e-4, f + ": hermite quarter");
            Eq(clip.Curves[2].Evaluate(0.9f), 100f, f + ": infinite slope = step");
            var pose = clip.SamplePose(0.5f);
            var m = PoseValue.MusclesOf(pose);
            Check(Math.Abs(m["Left Arm Down-Up"] - 0.5f) < 1e-3 && Math.Abs(m["Left Index 1 Stretched"] - 0.5f) < 1e-3, f + ": sampled muscles");
            Check(!m.ContainsKey("Jaw Close"), f + ": editor curve not used");
            Eq(string.Join(",", pose["bodyRotation"].ToFloatArray()), "0,0,0,1", f + ": RootQ normalized");
            Eq(PoseValue.Name(pose), "Wave (0.5초)", f + ": pose named with time");
            if (!nf) continue;
            // 표정: 블렌드쉐이프 커브의 경로(아바타 루트 기준)를 IR 렌더러로, 없는 경로는 건너뛴다
            var sc = new IrScene();
            sc.Nodes.Add(new IrNode { Id = 0, Name = "Avatar" });
            sc.Nodes.Add(new IrNode { Id = 1, Name = "Body", Parent = 0 });
            sc.Renderers.Add(new IrRenderer { Id = "r1", Node = 1 });
            var ex = FaceExpression.FromClip(sc, clip, 0, out var missing, out var count);
            Eq(count, 1, "expression: blendshape curves counted");
            Eq(missing, 0, "expression: path Body → renderer");
            var sh = FaceExpression.Shapes(ex).ToList();
            Check(sh.Count == 1 && sh[0].Renderer == "r1" && sh[0].Shape == "smile" && sh[0].Value == 100, "expression: smile 100 on r1");
            Eq(FaceExpression.Name(ex), "Wave", "expression named after clip");
            Eq(RenderSync.ToMessage(Addr.Expression, ex)!["shapes"][0]["weight"].AsFloat, 100f, "expression → setExpression");
            Eq(RenderSync.ToMessage(Addr.Expression, JNode.Null)!["shapes"].Count, 0, "no expression → empty layer");
            sc.Nodes[1].Name = "Face";
            FaceExpression.FromClip(sc, clip, 0, out missing, out _);
            Eq(missing, 1, "expression: other avatar's path skipped");
            var ie = new IrExpression { Name = "Fist", Layer = "Left Hand" };
            ie.Shapes.Add(new IrExpressionShape { Path = "Face", Shape = "blink", Value = 100 });
            ie.Shapes.Add(new IrExpressionShape { Path = "Nope", Shape = "x", Value = 1 });
            Eq(FaceExpression.FromIr(sc, ie)["shapes"].Count, 1, "IR expression: unknown path dropped");
            Eq(FaceExpression.GroupLabel("Left Hand"), "손 제스처", "hand layers grouped");
            var doc = new Document(sc, "x.ir.json");
            Check(doc.Get(Addr.Expression).IsNull, "expression original = none");
            doc.Set(Addr.Expression, ex, "표정"); Check(doc.IsModified(Addr.Expression), "expression recorded");
            doc.Undo(); Check(!doc.IsModified(Addr.Expression), "expression undo");
        }
    }
}
