// 렌더러 왕복 테스트: Host 역할을 대신해 파이프로 렌더러를 띄우고 명령을 보낸 뒤 캡처 PNG를 남긴다.
// 사용: dotnet bin/E2E.dll <VrcProtoRenderer.exe> <sample.ir.json> <출력 폴더>
// 렌더러는 -batchmode로 띄워 화면에 창을 만들지 않는다.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class E2E
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) _fail++; }

    static int Main(string[] args)
    {
        var exe = Path.GetFullPath(args[0]); var ir = Path.GetFullPath(args[1]); var outDir = Path.GetFullPath(args[2]);
        // 세션(.alesav·예전 .vrcproto)을 주면 그 세션이 가리키는 IR로 (2026-10-24). 세션의 바뀐 값(changeset)은 렌더러 시험에 넣지 않는다
        if (VrcProto.Host.Core.Session.IsSessionFile(ir)) { ir = VrcProto.Host.Core.Session.Read(ir).IrPath; Console.WriteLine($"   session → IR {ir}"); }
        Directory.CreateDirectory(outDir);
        var name = PipeChannel.NewPipeName();
        using var server = new PipeChannel.Server(name);
        var accept = server.AcceptAsync(CancellationToken.None);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // gizmos: 창 화면(OnGUI 포함)을 찍으므로 창을 띄운다
        if (!(args.Length > 3 && (args[3] == "gizmos" || args[3] == "quality" || args[3] == "dofwindow" || args[3] == "overlaygame"))) psi.ArgumentList.Add("-batchmode");   // quality: 미리보기 창 화면도 찍는다
        foreach (var a in new[] { "-pipe", name, "-logFile", Path.Combine(outDir, "renderer.log"), "-screen-width", "960", "-screen-height", "720", "-screen-fullscreen", "0" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        if (!accept.Wait(30000)) { Console.WriteLine("FAIL renderer did not connect"); proc.Kill(); return 1; }
        var ch = accept.Result;
        var inbox = new BlockingCollection<JNode>();
        ch.OnMessage += m => inbox.Add(m);
        ch.Start();
        JNode Wait(string type, int ms = 60000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
                if (inbox.TryTake(out var m, 200))
                {
                    if (Msg.Type(m) == Msg.Log) { Console.WriteLine($"   [{m["level"].AsString}] {m["text"].AsString.Split('\n')[0]}"); continue; }
                    if (Msg.Type(m) == type || type.IndexOf('|') > 0 && type.Split('|').Contains(Msg.Type(m))) return m;
                }
            throw new TimeoutException("waiting for " + type);
        }

        try
        {
            ch.Send(Msg.Make(Msg.Hello).Set("protocolVersion", Msg.ProtocolVersion));
            var ready = Wait(Msg.Ready);
            Check(ready["shaders"].Items.Any(s => s.AsString == "lilToon"), $"ready: Unity {ready["unityVersion"].AsString}, shaders {ready["shaders"].Count}, env providers {ready["environment"].Count}");

            // PhysBone 시험 (4번째 인자 physbones): 시간을 멈추지 않고 움직임 시험 중 캡처
            if (args.Length > 3 && args[3] == "physbones") return PhysBones(ch, Wait, ir, outDir, proc);
            if (args.Length > 3 && args[3] == "collide") return CollideTest(ch, Wait, ir, outDir, proc);
            if (args.Length > 3 && args[3] == "pbfeel") return FeelTest(ch, Wait, ir, outDir, proc);
            if (args.Length > 3 && args[3] == "pbvr") return VrMoveTest(ch, Wait, ir, outDir, proc);
            if (args.Length > 3 && args[3] == "pbskirt") return PbSkirtTest(ch, Wait, ir, outDir, proc, (args.Length > 4 ? args[4] : "skirt_1.001,skirt_6.001,skirt_11.001,skirt_16.001").Split(','));
            if (args.Length > 3 && args[3] == "pbrules")return PbRulesTest(ch, Wait, ir, outDir, proc);
            if (args.Length > 3 && args[3] == "pbmove") return PbMoveTest(ch, Wait, ir, outDir, proc, args.Length > 4 ? int.Parse(args[4]) : 12);
            if (args.Length > 3 && args[3] == "pbrest") return PbRestTest(ch, Wait, ir, outDir, proc, args.Length > 4 ? int.Parse(args[4]) : 30);
            if (args.Length > 3 && args[3] == "pose") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return PoseTest(ch, Wait, ir, outDir, proc, args.Skip(4).ToArray()); }
            if (args.Length > 3 && args[3] == "face") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return FaceTest(ch, Wait, ir, outDir, proc, args.Length > 4 ? args[4] : null); }
            if (args.Length > 3 && args[3] == "faceosc") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return FaceOscTest(ch, Wait, ir, outDir, proc, args.Skip(4).ToArray()); }
            if (args.Length > 3 && args[3] == "mix") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return MixTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "bake") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return BakeTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "sticker") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return StickerTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "mouthline") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return MouthLineTest(ch, Wait, ir, outDir, proc, args.Skip(4).ToArray()); }
            if (args.Length > 3 && args[3] == "mouth") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return MouthTest(ch, Wait, ir, outDir, proc, args.Skip(4).ToArray()); }
            if (args.Length > 3 && args[3] == "quality") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return QualityTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "studio") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return StudioTest(ch, Wait, ir, outDir, proc, args.Length > 4 && args[4] != "-" ? args[4] : null, args.Length > 5 ? args[5] : null); }
            if (args.Length > 3 && args[3] == "gizmos") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return GizmoTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "handles") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return HandlesTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "guests") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return GuestsTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "limits") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return LimitsTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "steps") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return StepsTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "mesh") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return MeshTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "dofwindow") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return DofWindowTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "envplus") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return EnvPlusTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "overlay") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return OverlayTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "overlaygame") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return OverlayGameTest(ch, Wait, ir, outDir, proc); }
            if (args.Length > 3 && args[3] == "vrlink") return VrLinkTest(ch, Wait, ir, outDir, proc, args.Length > 4 && args[4] == "steamvr");
            if (args.Length > 3 && args[3] == "env") { ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); return EnvTest(ch, Wait, ir, outDir, proc); }

            // 반짝임·깜빡임 같은 시간 효과가 캡처마다 달라지지 않게 셰이더 시간을 멈춘다
            ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true));
            var doc = new Document(IrScene.Load(ir), ir);
            var sink = new Sink(ch);
            var sync = new RenderSync(doc, sink);
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = Wait(Msg.SceneLoaded);
            var mats = loaded["materials"].Items.ToList();
            foreach (var w in loaded["warnings"].Items) Console.WriteLine("   warn: " + w.AsString);
            if (doc.Original.FindMaterial("mat_hair") == null)
            {
                // 샘플이 아닌 IR (importsim 출력 등): 로드·머티리얼 보고·캡처 + 조작(끄기·색·블렌드쉐이프·렌더링 모드)과 실행 취소 확인
                Check(mats.Count == doc.Original.Materials.Count, $"sceneLoaded: {mats.Count}/{doc.Original.Materials.Count} materials, {mats.Count(m => m["supported"].AsBool)} supported, {mats.Count(m => m["approx"].AsBool)} approx");
                foreach (var m in mats.Where(m => m["approx"].AsBool))
                    Console.WriteLine($"   approx: {doc.Original.FindMaterial(m["guid"].AsString)?.Name} ({m["shader"].AsString}) {m["approxReason"].AsString}");
                doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
                sync.SendAllOverrides();
                var s = doc.Original;
                string P(string n) => Path.Combine(outDir, n);
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
                Capture(ch, Wait, P("01_original.png"), false);
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
                Capture(ch, Wait, P("05_face.png"), false);

                // 보이는 렌더러 중 정점이 가장 많은 것 (켜져 있고 조상까지 활성)
                bool Visible(IrRenderer r) { if (!r.Enabled) return false; for (int i = r.Node; i >= 0; i = s.Nodes[i].Parent) if (!s.Nodes[i].Active) return false; return true; }
                var visible = s.Renderers.Where(r => Visible(r) && s.FindMesh(r.Mesh) != null).OrderByDescending(r => s.FindMesh(r.Mesh)!.VertexCount).ToList();
                var lilMats = mats.Where(m => m["supported"].AsBool && LilToonRules.IsLilToon(m["shader"].AsString)).Select(m => m["guid"].AsString).ToList();

                // 1) 블렌드쉐이프 0 → 100 (전신 카메라, 이름이 구분선이 아닌 첫 셰이프)
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
                // 정점 이동이 가장 큰 셰이프 (가려진 부위의 셰이프를 고르면 화면이 안 바뀌므로)
                var shapeCands = new List<(double d, IrRenderer r, string Name)>();
                using (var buf = new IrBuffers(s.BufferFile))
                {
                    foreach (var r in visible)
                    {
                        var m = s.FindMesh(r.Mesh)!;
                        for (int i = 0; i < m.BlendShapes.Count; i++)
                        {
                            if (i < r.BlendShapeWeights.Count && r.BlendShapeWeights[i] != 0) continue;
                            var fr = m.BlendShapes[i].Frames.LastOrDefault();
                            if (fr == null || fr.DeltaPos.IsEmpty) continue;
                            var d = buf.Floats(fr.DeltaPos); double mx = 0;
                            for (int k = 0; k + 2 < d.Length; k += 3) mx = Math.Max(mx, d[k] * d[k] + d[k + 1] * d[k + 1] + d[k + 2] * d[k + 2]);
                            if (mx > 0) shapeCands.Add((mx, r, m.BlendShapes[i].Name));
                        }
                    }
                }
                // 이동이 큰 순서로 최대 5개: 다른 메시에 가려진 셰이프(옷 속 몸 등)는 화면이 안 바뀌므로 다음 것을 본다
                var tried = new List<string>();
                bool shapeChanged = false;
                foreach (var c in shapeCands.OrderByDescending(x => x.d).Take(5))
                {
                    doc.Set(Addr.BlendShape(c.r.Id, c.Name), 100);
                    Capture(ch, Wait, P("06_shape100.png"), false);
                    shapeChanged = !SameImage(P("01_original.png"), P("06_shape100.png"));
                    while (doc.CanUndo) doc.Undo();
                    tried.Add($"{s.NodePath(c.r.Node)}:{c.Name}");
                    if (shapeChanged) break;
                }
                if (tried.Count > 0) Check(shapeChanged, $"blend shape 100 → image changed ({string.Join(", ", tried)})");

                // 2) lilToon 머티리얼 색 바꾸기 (보이는 메시 중 가장 큰 것의 첫 lilToon 머티리얼)
                var colorMat = visible.SelectMany(r => r.Materials).FirstOrDefault(lilMats.Contains);
                if (colorMat != null)
                {
                    doc.Set(Addr.MatColor(colorMat, "_Color"), JNode.FloatArray(new[] { 1f, 0.2f, 0.2f, 1f }));
                    Capture(ch, Wait, P("08_color.png"), false);
                    Check(!SameImage(P("01_original.png"), P("08_color.png")), $"material {s.FindMaterial(colorMat)?.Name} _Color → image changed");
                }
                // 3) 가장 큰 메시 끄기
                if (visible.Count > 0)
                {
                    doc.Set(Addr.NodeActive(visible[0].Node), false);
                    Capture(ch, Wait, P("07_hidden.png"), false);
                    Check(!SameImage(P(colorMat != null ? "08_color.png" : "01_original.png"), P("07_hidden.png")), $"hide {s.NodePath(visible[0].Node)} → image changed");
                }
                // 4) 렌더링 모드 바꾸기 (셰이더 교체)
                var modeMat = lilMats.FirstOrDefault();
                if (modeMat != null)
                {
                    var cur = LilToonRules.Detect(mats.First(m => m["guid"].AsString == modeMat)["shader"].AsString)!;
                    var target = new LilShaderState { Family = cur.Family, Mode = cur.Mode == LilRenderingMode.Cutout ? LilRenderingMode.Opaque : LilRenderingMode.Cutout, Outline = cur.Outline };
                    using (doc.Group("mode")) foreach (var kv in LilToonRules.Apply(modeMat, target, -1)) doc.Set(kv.Key, kv.Value);
                    var switched = Wait(Msg.MaterialInfo);
                    Check(switched["shader"].AsString == LilToonRules.ShaderFor(target), $"shader switch {cur} → {switched["shader"].AsString}");
                }
                // 5) 전부 실행 취소 → 원래 화면
                while (doc.CanUndo) doc.Undo();
                if (modeMat != null) Wait(Msg.MaterialInfo);
                Capture(ch, Wait, P("04_undone.png"), false);
                Check(SameImage(P("01_original.png"), P("04_undone.png")), "undo all → same pixels as original capture (±2/255)");
                ch.Send(Msg.Make(Msg.Quit));
                if (!proc.WaitForExit(10000)) proc.Kill();
                Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
                return _fail == 0 ? 0 : 1;
            }
            Check(mats.Count == 4, "sceneLoaded: 4 materials");
            Check(mats.Count(m => m["supported"].AsBool) == 3, "3 supported (lilToon), 1 unsupported");
            Check(mats.First(m => m["guid"].AsString == "mat_hair")["shader"].AsString == "Hidden/lilToonOutline", "hair resolved to Hidden/lilToonOutline via GUID table");
            var body = mats.First(m => m["guid"].AsString == "mat_body");
            var bodySchema = loaded["schemas"][body["shader"].AsString];
            Check(bodySchema.Count > 200, $"lilToon schema has {bodySchema.Count} properties (셰이더마다 한 번)");
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            sync.SendAllOverrides(); // Host가 sceneLoaded 직후 하는 것과 같게 (환경 포함)

            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Capture(ch, Wait, Path.Combine(outDir, "01_original.png"), false);
            Capture(ch, Wait, Path.Combine(outDir, "02_original_transparent.png"), true);

            // 편집: 블렌드쉐이프, 색, 렌더링 모드(셰이더 교체), 오브젝트 숨김, 환경
            doc.Set(Addr.BlendShape("r_head", "head_big"), 100);
            doc.Set(Addr.MatColor("mat_cloth", "_Color"), JNode.FloatArray(new[] { 1f, 0.55f, 0.6f, 1f }));
            using (doc.Group("mode"))
                foreach (var kv in LilToonRules.Apply("mat_hair", new LilShaderState { Family = LilFamily.Normal, Mode = LilRenderingMode.Cutout, Outline = false }, -1))
                    doc.Set(kv.Key, kv.Value);
            var info = Wait(Msg.MaterialInfo);
            Check(info["guid"].AsString == "mat_hair" && info["shader"].AsString == "Hidden/lilToonCutout", "shader switch → materialInfo Hidden/lilToonCutout");
            doc.Set(Addr.NodeActive(doc.Original.Nodes.FindIndex(n => n.Name == "Ribbon")), false);
            doc.Set(Addr.Environment, JNode.Arr().Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj().Set("preset", "indoor"))));
            Capture(ch, Wait, Path.Combine(outDir, "03_edited.png"), false);

            // 실행 취소 전부 → 원본과 같은 화면
            while (doc.CanUndo) doc.Undo();
            Wait(Msg.MaterialInfo);
            Capture(ch, Wait, Path.Combine(outDir, "04_undone.png"), false);
            Check(SameImage(Path.Combine(outDir, "01_original.png"), Path.Combine(outDir, "04_undone.png")), "undo all → same pixels as original capture (±2/255)");

            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
            Capture(ch, Wait, Path.Combine(outDir, "05_face.png"), false);
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// PhysBone 시험: 시간을 흐르게 두고 쉬는 자세 → 움직임 시험(sway·turn·jump) 중 → 끝나고 가라앉은 뒤를 찍는다.
    /// 움직이는 동안은 눈으로 보고, 숫자로는 "가라앉으면 처음 자세로 돌아온다"와 "끄면 쉬는 자세"를 확인한다.
    /// </summary>
    static int PhysBones(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            var pb = loaded["physBones"];
            Check(pb["chains"].AsInt > 0, $"physBones: chains {pb["chains"].AsInt}, bones {pb["bones"].AsInt}");
            Thread.Sleep(2500); // 중력으로 가라앉기
            Capture(ch, wait, P("pb_01_rest.png"), false);
            Thread.Sleep(1000);
            Capture(ch, wait, P("pb_02_rest_later.png"), false);
            Console.WriteLine($"   rest drift: {Diff(P("pb_01_rest.png"), P("pb_02_rest_later.png")):P3} pixels changed");
            foreach (var (motion, times) in new[] { ("sway", new[] { 700, 600, 600 }), ("turn", new[] { 900, 700 }), ("jump", new[] { 250, 300 }) })
            {
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", motion));
                int i = 0;
                foreach (var t in times) { Thread.Sleep(t); Capture(ch, wait, P($"pb_{motion}_{++i}.png"), false); }
                Thread.Sleep(4000);
            }
            Capture(ch, wait, P("pb_03_settled.png"), false);
            double back = Diff(P("pb_01_rest.png"), P("pb_03_settled.png"));
            Check(back < 0.01, $"after motions → back to rest pose ({back:P3} pixels changed)");
            if (pb["soft"].AsInt > 0) SoftTest(ch, wait, P);
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            Thread.Sleep(500);
            Capture(ch, wait, P("pb_04_off.png"), false);
            Console.WriteLine($"   PhysBone off vs rest: {Diff(P("pb_01_rest.png"), P("pb_04_off.png")):P3} pixels changed (중력·Immobile이 있으면 다름)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 말랑 모드: 쉬는 자세가 거의 그대로인지, 점프가 오류 없이 도는지(화면 차이는 참고용: 아바타 전체가 움직여 비교 기준이 못 된다).
    /// 끝나면 끄고 쉬는 자세로 돌아오는지 본다.
    /// </summary>
    static void SoftTest(PipeChannel ch, Func<string, int, JNode> wait, Func<string, string> P)
    {
        ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
        Thread.Sleep(2500);
        Capture(ch, wait, P("soft_00_rest.png"), false);
        double Jump(string preset)
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("soft", preset));
            Thread.Sleep(2500);
            Capture(ch, wait, P($"soft_{preset}_rest.png"), false);
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", "jump"));
            double sum = 0;
            for (int i = 1; i <= 4; i++)
            {
                Thread.Sleep(200);
                Capture(ch, wait, P($"soft_{preset}_{i}.png"), false);
                sum += Diff(P($"soft_{preset}_rest.png"), P($"soft_{preset}_{i}.png"));
            }
            Thread.Sleep(4000);
            return sum;
        }
        double off = Jump("off"), soft = Jump("normal");
        Console.WriteLine($"   jump frames vs rest: off {off:P2}, normal {soft:P2} (아바타 전체가 움직여 참고용)");
        double restDiff = Diff(P("soft_00_rest.png"), P("soft_normal_rest.png"));
        Check(restDiff < 0.02, $"normal at rest ≈ normal rest ({restDiff:P3} pixels changed)");
        ch.Send(Msg.Make(Msg.SetPhysBones).Set("soft", "off"));
        Thread.Sleep(2500);
        Capture(ch, wait, P("soft_99_off_again.png"), false);
        double back = Diff(P("soft_00_rest.png"), P("soft_99_off_again.png"));
        Check(back < 0.01, $"soft off → back to normal rest ({back:P3} pixels changed)");
        ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
    }

    /// <summary>
    /// 환경 시험: 조명 파라미터(환경광 색·반사·주광 높이·그림자)와 후처리(블룸·톤 매핑·색)가 화면을 바꾸는지,
    /// 후처리를 켠 채 투명 배경으로 찍어도 후처리 없는 투명 촬영과 같은지(촬영 때 끔) 확인한다.
    /// </summary>
    static int EnvTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        JNode Light(Func<JNode, JNode> f) => JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", f(JNode.Obj().Set("preset", "bright")));
        void Env(string file, params JNode[] providers)
        {
            var arr = JNode.Arr(); foreach (var p in providers) arr.Add(p);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", arr));
            Thread.Sleep(300);
            Capture(ch, wait, P(file), false);
        }
        try
        {
            // PhysBone을 끈다: 머리카락이 미세하게 흔들려 촬영마다 몇 픽셀씩 달라지면 투명 촬영 비교(±2)가 어긋난다
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Env("env_01_default.png", Light(p => p));
            Env("env_02_ambient_red.png", Light(p => p.Set("ambientColor", JNode.FloatArray(new[] { 1f, 0.4f, 0.4f }))));
            Env("env_05_low_sun_noshadow.png", Light(p => p.Set("pitch", -40).Set("shadows", false)));
            Env("env_05b_back_sun.png", Light(p => p.Set("yaw", 180)));
            // 그림자: 앞에서 비추면 그림자가 몸 뒤로 떨어져 안 보이므로 머리 위에서 비춰 켜고 끈 두 장을 견준다 (정보만 출력).
            // lilToon은 _ShadowReceive가 0보다 큰 머티리얼만 그림자를 받아 아바타마다 차이가 0~0.03%로 작다 (렌더러 그림자 설정은 정상: Ultra, All, 20 m)
            Env("env_05c_top_shadow.png", Light(p => p.Set("pitch", 35)));
            Env("env_05d_top_noshadow.png", Light(p => p.Set("pitch", 35).Set("shadows", false)));
            Env("env_06_sunset.png", Light(p => p.Set("preset", "sunset")));
            var post = JNode.Obj().Set("provider", "PostProcess").Set("version", 1).Set("params",
                JNode.Obj().Set("bloom", 2).Set("bloomThreshold", 0.6).Set("tonemap", "aces").Set("temperature", 30).Set("saturation", 0.2).Set("vignette", 0.6));
            Env("env_07_post.png", Light(p => p), post);
            foreach (var (f, what) in new[] { ("env_02_ambient_red.png", "ambient color"), ("env_05_low_sun_noshadow.png", "sun pitch −40 + no shadow"),
                                              ("env_05b_back_sun.png", "sun from behind (yaw +180)"), ("env_06_sunset.png", "sunset preset"), ("env_07_post.png", "post-processing") })
            {
                // 배경(화면의 75~80%)은 조명과 상관없이 그대로이므로 아바타 픽셀 중 바뀐 비율로 본다.
                // 화면 전체로 재면 검은 옷이 많은 아바타 A는 환경광 색이 0.7%만 바뀐다
                double d = AvatarDiff(P("env_01_default.png"), P(f));
                Check(d > 0.01, $"{what} → avatar pixels changed ({d:P2})");
            }
            double shadow = AvatarDiff(P("env_05c_top_shadow.png"), P("env_05d_top_noshadow.png"));
            Console.WriteLine($"   sun overhead: shadows off → {shadow:P2} avatar pixels changed");
            // 투명 촬영은 후처리를 끄고 찍어 알파를 구한 뒤 색 보정만 다시 넣는다 (2026-10-24, PostFx.Grade) →
            // 후처리 켠 투명 촬영 = 알파는 후처리 없는 것과 똑같고(블룸이 배경에 번지지 않음), 색은 색 보정(ACES·색온도·채도·비네트)만큼 달라야 한다
            Capture(ch, wait, P("env_08_post_transparent.png"), true);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", JNode.Arr().Add(Light(p => p))));
            Thread.Sleep(300);
            Capture(ch, wait, P("env_09_transparent.png"), true);
            {
                var (_, _, pa) = DecodePng(P("env_08_post_transparent.png")); var (_, _, pb) = DecodePng(P("env_09_transparent.png"));
                int alphaDiff = 0; long rgb = 0; int n = 0;
                for (int i = 0; i < pa.Length; i += 4)
                {
                    if (Math.Abs(pa[i + 3] - pb[i + 3]) > 1) alphaDiff++;
                    if (pb[i + 3] == 255) { rgb += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]); n++; }
                }
                double mean = (double)rgb / Math.Max(1, n * 3);
                Check(alphaDiff == 0, $"transparent capture with post: same alpha as without (bloom left out, {alphaDiff} pixels differ)");
                Check(mean > 3, $"transparent capture with post: color grading applied (opaque pixels mean diff {mean:F1})");
            }
            Env("env_10_default_again.png", Light(p => p));
            Check(Diff(P("env_01_default.png"), P("env_10_default_again.png")) < 0.001, "removing post-processing restores the default image");
            // 배경 색 직접 고르기 (2026-10-20): 촬영 배경(왼쪽 위 픽셀)이 고른 색, 끄면 프리셋 배경으로
            Env("env_11_bg_magenta.png", Light(p => p.Set("bgCustom", true).Set("bgColor", JNode.FloatArray(new[] { 1f, 0f, 1f }))));
            {
                var (bw, bh, bp) = DecodePng(P("env_11_bg_magenta.png")); int bc = bp.Length / (bw * bh);
                Check(bp[0] > 230 && bp[1] < 25 && bp[2] > 230, $"custom background color → corner pixel ({bp[0]}, {bp[1]}, {bp[2]})");
            }
            Env("env_12_bg_off.png", Light(p => p.Set("bgCustom", false).Set("bgColor", JNode.FloatArray(new[] { 1f, 0f, 1f }))));
            Check(Diff(P("env_01_default.png"), P("env_12_bg_off.png")) < 0.001, "bgCustom off → preset background");
            // 반사 세기: lilToon 머티리얼의 반사·금속성을 켜고 찍는다 (다른 셰이더는 프로퍼티가 없어 무시된다).
            // 맨 끝에 하는 이유: 금속성 1이면 확산 명암이 반사로 덮여 주광 방향·그림자가 하이라이트에만 남는다
            // (예전엔 처음에 켜서 아바타 A의 "주광 높이 + 그림자 끔"이 0.2~0.65%만 바뀌어 실패했다)
            foreach (var m in loaded["materials"].Items)
                foreach (var (prop, v) in new[] { ("_UseReflection", 1.0), ("_ApplyReflection", 1.0), ("_Metallic", 1.0), ("_Smoothness", 0.9), ("_Reflectance", 1.0) })
                    ch.Send(Msg.Make(Msg.SetMatProp).Set("mat", m["guid"].AsString).Set("prop", prop).Set("kind", Msg.KindFloat).Set("value", v));
            Env("env_03_reflection0.png", Light(p => p.Set("reflection", 0)));
            Env("env_04_reflection2.png", Light(p => p.Set("reflection", 2)));
            double refl = Diff(P("env_03_reflection0.png"), P("env_04_reflection2.png"));
            // 반사를 켤 수 있는 셰이더(lilToon)가 있을 때만 확인한다. Poiyomi 기본 설정은 환경 반사를 쓰지 않는다
            if (loaded["materials"].Items.Any(m => m["shader"].AsString.Contains("lilToon"))) Check(refl > 0.01, $"reflection 0 vs ×2 → image changed ({refl:P2})");
            else Console.WriteLine($"   reflection 0 vs ×2: {refl:P2} pixels changed (lilToon 머티리얼 없음)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 원본 근육 범위·비틀림 분배 (IR humanoidSettings): 같은 IR에 위팔 근육 범위를 넓히고 위팔 비틀림 분배를 바꾼 사본을 만들어,
    /// 같은 머슬 값의 자세가 달라지는지(범위가 넓으면 같은 −1이 더 많이 돈다), 쉬는 자세는 그대로인지 본다.
    /// </summary>
    /// <summary>
    /// 이동·회전 핸들과 선택: 끌기와 같은 경로(poseBone arrow·moveTarget)로
    /// 본 관절 옮기기 → 머슬, 아바타 전체 옮기기·돌리기 → Addr.AvatarTransform, 아이템 옮기기 → Addr.NodeTransform(업로드·다시 만들기에도 남는지),
    /// 실행 취소하면 처음 화면, 화면 점 고르기, 뼈를 합친 옷은 고르지 않는지.
    /// </summary>
    /// <summary>
    /// 표정·페이셜 트래킹: 아바타 기본 표정(IR expressions) → 화면 바뀜 · 표정 밑의 사용자 블렌드쉐이프 값은 표정을 빼면 보임 · 실행 취소 → 처음 화면,
    /// VMC(OSC UDP)로 ARKit 값 보내기 → 얼굴 메시가 움직임 · 고정(snapshot), 웹캠과 같은 경로로 사진 한 장(인자) → 얼굴 찾음·값.
    /// </summary>
    /// <summary>스튜디오: 시작 전엔 아무것도 없음, 평면 이미지·360° HDR 배경 → 화면 바뀜(아바타는 그대로 앞), 투명 촬영엔 배경 없음, 끄면 처음 화면.</summary>
    static int StudioTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string? image, string? propIr = null)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file, bool transparent = false) { Thread.Sleep(250); Capture(ch, wait, P(file), transparent); }
        JNode Studio(JNode m) { ch.Send(m.Set("t", Msg.Studio)); return wait(Msg.StudioState, 20000); }
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Shot("studio_00_rest.png");
            var st = Studio(JNode.Obj().Set("enabled", true));
            Check(st["enabled"].AsBool && st["background"].AsString == "none", "studio start → enabled, no background yet");
            Shot("studio_01_started.png");
            Check(Diff(P("studio_00_rest.png"), P("studio_01_started.png")) < 0.0005, "starting the studio alone changes nothing");
            // 360° HDR: 위는 하늘색, 아래는 주황 (작은 RLE 없는 .hdr를 직접 쓴다)
            var hdr = P("studio_test.hdr");
            WriteHdr(hdr, 64, 32, (x, y) => y < 16 ? (0.4f, 0.7f, 1.6f) : (1.4f, 0.5f, 0.1f));
            st = Studio(JNode.Obj().Set("background", JNode.Obj().Set("mode", "panorama").Set("path", hdr)));
            Check(st["background"].AsString == "panorama" && st["width"].AsInt == 64, $"HDR panorama loaded ({st["width"].AsInt}×{st["height"].AsInt}) {st["error"].AsString}");
            Shot("studio_02_panorama.png");
            double pano = Diff(P("studio_00_rest.png"), P("studio_02_panorama.png"));
            Check(pano > 0.3, $"panorama background fills the empty area ({pano:P1} pixels changed)");
            st = Studio(JNode.Obj().Set("rotation", 90f).Set("exposure", 0.5f));
            Shot("studio_03_dim.png");
            Check(Diff(P("studio_02_panorama.png"), P("studio_03_dim.png")) > 0.3, "exposure 0.5 → darker background");
            if (image != null)
            {
                st = Studio(JNode.Obj().Set("exposure", 1f).Set("background", JNode.Obj().Set("mode", "image").Set("path", Path.GetFullPath(image))));
                Check(st["background"].AsString == "image", $"flat image background ({st["width"].AsInt}×{st["height"].AsInt}) {st["error"].AsString}");
                Shot("studio_04_image.png");
                Check(Diff(P("studio_00_rest.png"), P("studio_04_image.png")) > 0.3, "image background shows");
            }
            Shot("studio_05_transparent.png", true);
            Capture(ch, wait, P("studio_05_ref_transparent.png"), true);
            var (tw, th, tp) = DecodePng(P("studio_05_transparent.png"));
            int ch4 = tp.Length / (tw * th);
            Check(ch4 == 4 && tp[3] == 0, "transparent capture hides the studio background (corner alpha 0)");
            // ---- 2차: 심도 · 소품 · 녹화 ----
            st = Studio(JNode.Obj().Set("exposure", 1f).Set("rotation", 0f).Set("background", JNode.Obj().Set("mode", "panorama").Set("path", hdr)));
            Shot("studio_10_nodof.png");
            st = Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("strength", 1f).Set("auto", true)));
            float focus = st["focus"].AsFloat;
            Check(focus > 0.5f && focus < 10f, $"depth of field on, auto focus on the head ({focus:F2} m)");
            Shot("studio_11_dof.png");
            Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("show", true)));
            Shot("studio_11_focus_view.png");
            st = Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("show", false)));
            double dof = Diff(P("studio_10_nodof.png"), P("studio_11_dof.png"));
            Check(dof > 0.01, $"depth of field blurs the background ({dof:P1} pixels changed)");
            st = Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("strength", 1f).Set("auto", false).Set("distance", 30f)));
            Shot("studio_12_dof_far.png");
            double far = Diff(P("studio_10_nodof.png"), P("studio_12_dof_far.png"));
            Check(far > 0.01 && Diff(P("studio_11_dof.png"), P("studio_12_dof_far.png")) > 0.01, $"focus 30 m → avatar blurred instead ({far:P1})");
            Shot("studio_13_dof_transparent.png", true);
            var (dw, dh, dp) = DecodePng(P("studio_13_dof_transparent.png"));
            Check(dp.Length == dw * dh * 4 && dp[3] == 0, "transparent capture with depth of field keeps alpha 0 in the corner");
            st = Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", false)));
            Shot("studio_14_dof_off.png");
            Check(Diff(P("studio_10_nodof.png"), P("studio_14_dof_off.png")) < 0.0005, "depth of field off → same as before");

            // 소품: 같은 IR(또는 6번째 인자의 소품 IR — 예: 프로젝트 밖 FBX에서 가져온 것)을 옆에 하나 더 세운다 (아바타 문서와 무관)
            var prop = JNode.Obj().Set("id", "p1").Set("path", propIr ?? ir).Set("pos", JNode.FloatArray(new[] { 0.35f, 0f, -0.8f })).Set("rotY", -30f).Set("scale", 0.8f);
            st = Studio(JNode.Obj().Set("props", JNode.Arr().Add(prop)));
            var pst = st["props"][0];
            Check(pst["ok"].AsBool && pst["renderers"].AsInt > 0, $"prop loaded ({pst["renderers"].AsInt} renderers, size {string.Join("×", pst["size"].ToFloatArray().Select(v => v.ToString("F2")))}) {pst["error"].AsString}");
            Shot("studio_20_prop.png");
            double pd = Diff(P("studio_10_nodof.png"), P("studio_20_prop.png"));
            Check(pd > 0.02, $"prop shows next to the avatar ({pd:P1})");
            prop.Set("pos", JNode.FloatArray(new[] { -0.35f, 0f, -0.8f })).Set("rotY", 30f);
            st = Studio(JNode.Obj().Set("props", JNode.Arr().Add(prop)));
            Shot("studio_21_prop_moved.png");
            Check(Diff(P("studio_20_prop.png"), P("studio_21_prop_moved.png")) > 0.02, "prop moved to the other side");

            // 아바타만 (2026-10-27): 파노라마 배경·소품·단색 바닥을 빼고 초록 단색 위에 아바타만
            JNode AvatarOnly(bool on) => Msg.Make(Msg.SetView).Set("avatarOnly", JNode.Obj().Set("on", on).Set("color", JNode.FloatArray(new[] { 0f, 1f, 0f })));
            Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("mode", "solid").Set("color", JNode.FloatArray(new[] { 0.9f, 0.1f, 0.1f }))));
            Shot("studio_24_floor.png");
            ch.Send(AvatarOnly(true));
            Shot("studio_25_avatar_only.png");
            {
                var (w, h, px) = DecodePng(P("studio_25_avatar_only.png")); int c = px.Length / (w * h);
                int tl = (2 * w + 2) * c, bl = ((h - 3) * w + 2) * c;
                Check(px[tl] < 20 && px[tl + 1] > 235 && px[tl + 2] < 20 && px[bl] < 20 && px[bl + 1] > 235 && px[bl + 2] < 20,
                    $"avatar only → corners are the chosen green (top {px[tl]},{px[tl + 1]},{px[tl + 2]} · bottom {px[bl]},{px[bl + 1]},{px[bl + 2]})");
            }
            var hiddenProp = prop.Clone().Set("visible", false);
            Studio(JNode.Obj().Set("props", JNode.Arr().Add(hiddenProp)).Set("shadowFloor", JNode.Obj().Set("enabled", false)));
            Shot("studio_26_avatar_only_noscene.png");
            double ao = Diff(P("studio_25_avatar_only.png"), P("studio_26_avatar_only_noscene.png"));
            Check(ao < 0.0005, $"avatar only ignores the prop and floor ({ao:P3} differs from no prop · no floor)");
            Shot("studio_27_avatar_only_transparent.png", true);
            {
                var (w, h, px) = DecodePng(P("studio_27_avatar_only_transparent.png"));
                Check(px.Length == w * h * 4 && px[3] == 0, "transparent capture in avatar only keeps alpha 0 in the corner");
            }
            Studio(JNode.Obj().Set("props", JNode.Arr().Add(prop)));
            ch.Send(AvatarOnly(false));
            Shot("studio_28_avatar_only_off.png");
            Check(Diff(P("studio_21_prop_moved.png"), P("studio_28_avatar_only_off.png")) < 0.0005, "avatar only off → panorama and prop come back");
            prop.Set("visible", false);
            Studio(JNode.Obj().Set("props", JNode.Arr().Add(prop)));
            Shot("studio_22_prop_hidden.png");
            Check(Diff(P("studio_10_nodof.png"), P("studio_22_prop_hidden.png")) < 0.0005, "hidden prop → gone");
            st = Studio(JNode.Obj().Set("props", JNode.Arr().Add(prop.Set("visible", true))));
            st = Studio(JNode.Obj().Set("props", JNode.Arr()));
            Check(st["props"].Count == 0, "prop removed");
            Shot("studio_23_prop_removed.png");
            Check(Diff(P("studio_10_nodof.png"), P("studio_23_prop_removed.png")) < 0.0005, "removed prop → same as before");

            // 녹화: 턴테이블 → MP4 (Media Foundation), 고정 → PNG 연속 이미지
            var mp4 = P("studio_30_turntable.mp4");
            var job = new RecordJob(mp4, true, 320, 240, 10, P("rec_tmp"));
            string? jobErr = "not finished";
            job.Finished = e => jobErr = e;
            ch.Send(Msg.Make(Msg.Record).Set("folder", job.FrameFolder).Set("format", "raw").Set("width", 320).Set("height", 240).Set("fps", 10).Set("seconds", 1.5f).Set("mode", "turntable"));
            int frames = 0; JNode done;
            while (true)
            {
                var r = wait(Msg.RecordFrame + "|" + Msg.RecordDone, 60000);
                if (Msg.Type(r) == Msg.RecordDone) { done = r; break; }
                if (frames == 0) File.Copy(r["path"].AsString, P("studio_31_frame0.raw"), true);
                if (r["index"].AsInt == 7) File.Copy(r["path"].AsString, P("studio_31_frame7.raw"), true);
                job.Add(r["path"].AsString); frames++;
            }
            job.Complete();
            Check(done["ok"].AsBool && done["frames"].AsInt == 15 && frames == 15, $"turntable recorded {done["frames"].AsInt} frames (1.5 s × 10 fps) {done["error"].AsString}");
            Check(job.Wait(30000) && jobErr == null, $"MP4 written by Media Foundation ({job.Encoded} frames) {jobErr}");
            var (mw, mh, samples, secs) = Mp4Info(mp4);
            Check(mw == 320 && mh == 240 && samples == 15 && Math.Abs(secs - 1.5) < 0.05, $"MP4: {mw}×{mh}, {samples} samples, {secs:F2} s, {new FileInfo(mp4).Length / 1024} KB");
            Check(!Directory.Exists(job.FrameFolder), "raw frames deleted after encoding");
            var f0 = File.ReadAllBytes(P("studio_31_frame0.raw")); var f7 = File.ReadAllBytes(P("studio_31_frame7.raw"));
            int differ = 0; for (int i = 0; i < f0.Length; i += 4) if (Math.Abs(f0[i] - f7[i]) + Math.Abs(f0[i + 1] - f7[i + 1]) + Math.Abs(f0[i + 2] - f7[i + 2]) > 24) differ++;
            Check(differ > f0.Length / 4 / 20, $"turntable: frame 7 (half turn) differs from frame 0 ({100.0 * differ / (f0.Length / 4):F1}% pixels)");
            Shot("studio_32_after_record.png");
            Check(Diff(P("studio_10_nodof.png"), P("studio_32_after_record.png")) < 0.0005, "camera back where it was after the turntable");

            // 파이프 녹화 (2026-10-24): 파일 없이 파이프로 MP4 · 실시간 (같은 장 되풀이) · 말하기 소리(합성 모음 WAV)를 AAC로
            {
                const int R = 16000; int n = R * 2; var wav = P("rec_tone.wav");
                using (var ww = new BinaryWriter(File.Create(wav)))
                {
                    ww.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); ww.Write(36 + n * 2); ww.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); ww.Write(16); ww.Write((short)1); ww.Write((short)1);
                    ww.Write(R); ww.Write(R * 2); ww.Write((short)2); ww.Write((short)16); ww.Write(System.Text.Encoding.ASCII.GetBytes("data")); ww.Write(n * 2);
                    for (int i = 0; i < n; i++) ww.Write((short)(Math.Sin(2 * Math.PI * 220 * i / R) * 0.3 * 32767));
                }
                ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", true).Set("wav", wav)));
                Thread.Sleep(300);
                var pmp4 = P("studio_35_pipe.mp4");
                var pjob = RecordJob.Pipe(pmp4);
                pjob.DumpFirstFrame = P("studio_35_pipe_frame0.bgra");
                string? perr = "not finished";
                pjob.Finished = e => perr = e;
                var sw0 = Stopwatch.StartNew();
                ch.Send(Msg.Make(Msg.Record).Set("format", "pipe").Set("pipe", pjob.PipeName).Set("width", 640).Set("height", 480).Set("fps", 30).Set("seconds", 2f).Set("mode", "turntable"));
                int pframes = 0, repeats = 0; JNode pdone;
                while (true)
                {
                    var r = wait(Msg.RecordFrame + "|" + Msg.RecordDone, 60000);
                    if (Msg.Type(r) == Msg.RecordDone) { pdone = r; break; }
                    pframes++; if (r["repeat"].AsInt > 1) repeats++;
                }
                double wall = sw0.Elapsed.TotalSeconds;
                pjob.Complete(cancel: !pdone["ok"].AsBool);
                Check(pdone["ok"].AsBool && pdone["frames"].AsInt == 60 && pdone["realtime"].AsBool, $"pipe recording, realtime (lip on): {pdone["frames"].AsInt} frames from {pframes} pictures ({repeats} repeated) in {wall:F1} s {pdone["error"].AsString}");
                Check(wall > 1.8, $"realtime: 2 s of video took real time ({wall:F1} s)");
                Check(pdone["audioSeconds"].AsDouble > 1.5, $"lip audio taped ({pdone["audioSeconds"].AsDouble:F2} s)");
                Check(pjob.Wait(30000) && perr == null, $"pipe MP4 finished ({pjob.Encoded} frames, audio {pjob.AudioSeconds:F2} s) {perr}");
                var (pw, ph, psamples, psecs) = Mp4Info(pmp4);
                Check(pw == 640 && ph == 480 && psamples == 60 && Math.Abs(psecs - 2) < 0.15, $"pipe MP4: {pw}×{ph}, {psamples} video samples, {psecs:F2} s, {new FileInfo(pmp4).Length / 1024} KB");
                Check(Mp4Handlers(pmp4).Contains("soun"), $"pipe MP4 has a sound track ({string.Join(",", Mp4Handlers(pmp4))})");
                // 파이프 첫 프레임 = 같은 카메라의 raw(동기 읽기) 첫 프레임과 같은 그림이어야 한다 (뒤집기·sRGB·BGRA가 같다)
                var pf = File.ReadAllBytes(P("studio_35_pipe_frame0.bgra"));
                var rf = File.ReadAllBytes(P("studio_31_frame0.raw"));
                long dsum = 0; int nonOpaque = 0; long luma = 0;
                for (int i = 0; i < pf.Length; i += 4) { if (pf[i + 3] != 255) nonOpaque++; luma += pf[i] + pf[i + 1] + pf[i + 2]; }
                Console.WriteLine($"   pipe frame0: {pf.Length} bytes, mean {luma / 3.0 / (pf.Length / 4):F1}, alpha≠255 {nonOpaque}");
                Check(pf.Length == 640 * 480 * 4 && nonOpaque == 0 && luma / 3.0 / (pf.Length / 4) > 20, "pipe frame carries the picture (not black, opaque)");
                Check(rf.Length == 320 * 240 * 4, "raw frame size");
                // 크기가 달라(640×480 ↔ 320×240) 2×2 평균으로 맞춰 비교
                for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) for (int c = 0; c < 3; c++)
                {
                    int s = 0; for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) s += pf[((y * 2 + dy) * 640 + x * 2 + dx) * 4 + c];
                    dsum += Math.Abs(s / 4 - rf[(y * 320 + x) * 4 + c]);
                }
                double pd2 = (double)dsum / (320 * 240 * 3);
                Check(pd2 < 6, $"pipe frame0 ≈ raw frame0 (same orientation & colors, mean diff {pd2:F2})");
                ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", false)));
                Thread.Sleep(200);
                // 트래킹이 꺼져 있으면 예전처럼 장마다 1/fps초 (실시간 아님, 되풀이 없음)
                var qjob = RecordJob.Pipe(P("studio_36_pipe_fixed.mp4"));
                string? qerr = "not finished"; qjob.Finished = e => qerr = e;
                ch.Send(Msg.Make(Msg.Record).Set("format", "pipe").Set("pipe", qjob.PipeName).Set("width", 320).Set("height", 240).Set("fps", 10).Set("seconds", 1f).Set("mode", "fixed"));
                var qdone = wait(Msg.RecordDone, 60000);
                qjob.Complete(cancel: !qdone["ok"].AsBool);
                Check(qdone["ok"].AsBool && !qdone["realtime"].AsBool && qdone["frames"].AsInt == 10 && qjob.Wait(30000) && qerr == null && !Mp4Handlers(P("studio_36_pipe_fixed.mp4")).Contains("soun"),
                    $"pipe recording without tracking: not realtime, {qdone["frames"].AsInt} frames, no sound track {qerr}");
                // 취소: 끝 레코드 없이 끊기면 MP4를 남기지 않는다
                var cjob = RecordJob.Pipe(P("studio_37_pipe_cancel.mp4"));
                bool cfin = false; cjob.Finished = e => cfin = true;
                ch.Send(Msg.Make(Msg.Record).Set("format", "pipe").Set("pipe", cjob.PipeName).Set("width", 320).Set("height", 240).Set("fps", 30).Set("seconds", 20f).Set("mode", "turntable"));
                wait(Msg.RecordFrame, 30000);
                ch.Send(Msg.Make(Msg.Record).Set("cancel", true));
                var cdone = wait(Msg.RecordDone, 30000);
                cjob.Complete(cancel: true);
                Check(cdone["cancelled"].AsBool && cjob.Wait(10000) && cfin && !File.Exists(P("studio_37_pipe_cancel.mp4")), "pipe recording cancelled → no MP4 left");
            }

            var seq = P("studio_33_sequence");
            if (Directory.Exists(seq)) Directory.Delete(seq, true);
            ch.Send(Msg.Make(Msg.Record).Set("folder", seq).Set("format", "png").Set("width", 256).Set("height", 256).Set("fps", 8).Set("seconds", 0.5f).Set("mode", "fixed").Set("transparent", true));
            done = wait(Msg.RecordDone, 60000);
            var pngs = Directory.GetFiles(seq, "frame_*.png").OrderBy(f => f).ToArray();
            Check(done["ok"].AsBool && pngs.Length == 4, $"PNG sequence: {pngs.Length} frames");
            var (sw, sh, sp) = DecodePng(pngs[0]);
            Check(sw == 256 && sp.Length == sw * sh * 4 && sp[3] == 0, "sequence frames are transparent PNG");
            Check(Diff(pngs[0], pngs[3]) < 0.01, "fixed camera, physbones off → frames nearly the same");
            ch.Send(Msg.Make(Msg.Record).Set("folder", P("studio_34_cancel")).Set("format", "png").Set("width", 256).Set("height", 256).Set("fps", 30).Set("seconds", 20f).Set("mode", "turntable"));
            wait(Msg.RecordFrame, 30000);
            ch.Send(Msg.Make(Msg.Record).Set("cancel", true));
            done = wait(Msg.RecordDone, 30000);
            Check(done["cancelled"].AsBool && !done["ok"].AsBool && done["frames"].AsInt < 600, $"recording cancelled after {done["frames"].AsInt} frames");

            st = Studio(JNode.Obj().Set("background", JNode.Obj().Set("mode", "none")));
            st = Studio(JNode.Obj().Set("enabled", false));
            Check(!st["enabled"].AsBool, "studio stop");
            Shot("studio_06_stopped.png");
            double back = Diff(P("studio_00_rest.png"), P("studio_06_stopped.png"));
            Check(back < 0.0005, $"stopped → same as start ({back:P3})");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 렌더 품질 시험 (2026-10-23): VRChat 품질 ↔ 스튜디오 고화질(SSAO·그림자·지터 누적)을 같은 자리에서 찍어 비교하고,
    /// 고화질을 끄면 처음 화면으로 돌아오는지, 투명 촬영의 배경 알파가 0인지, 프레임 제한 메시지가 받아지는지 본다.
    /// </summary>
    static int QualityTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file, bool transparent = false) { Thread.Sleep(300); Capture(ch, wait, P(file), transparent); }
        void Q(JNode m) => ch.Send(m.Set("t", Msg.SetQuality));
        JNode Studio(JNode m) { ch.Send(m.Set("t", Msg.Studio)); return wait(Msg.StudioState, 20000); }
        void Window(string file)
        {
            ch.Send(Msg.Make(Msg.SetView).Set("gizmo", false).Set("grid", false));
            Thread.Sleep(500);
            ch.Send(Msg.Make(Msg.ScreenShot).Set("path", P(file)));
            var r = wait(Msg.CaptureDone, 20000);
            Check(r["ok"].AsBool && File.Exists(P(file)), $"window {file} {r["error"].AsString}");
        }
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", Document.DefaultEnvironment()));
            Q(JNode.Obj().Set("maxFps", 30));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Shot("q_00_vrchat.png");
            Q(JNode.Obj().Set("studio", true).Set("ao", 0.6f).Set("captureAa", 8));
            Shot("q_01_studio.png");
            double d = AvatarDiff(P("q_00_vrchat.png"), P("q_01_studio.png"));
            Check(d > 0.001, $"studio quality changes the avatar ({d:P2})");
            var (b0, b1) = (Bbox(P("q_00_vrchat.png")), Bbox(P("q_01_studio.png")));
            Check(Math.Abs(b0.x0 - b1.x0) <= 2 && Math.Abs(b0.x1 - b1.x1) <= 2 && Math.Abs(b0.y0 - b1.y0) <= 2 && Math.Abs(b0.y1 - b1.y1) <= 2, $"same silhouette box: {b0} vs {b1}");
            Q(JNode.Obj().Set("ao", 0f));
            Shot("q_02_studio_noao.png");
            d = AvatarDiff(P("q_01_studio.png"), P("q_02_studio_noao.png"));
            Check(d > 0.0005, $"ambient occlusion darkens something ({d:P2})");
            Q(JNode.Obj().Set("ao", 0.6f));
            Shot("q_03_studio_transparent.png", transparent: true);
            var (tw, th, tp) = DecodePng(P("q_03_studio_transparent.png"));
            Check(tp.Length == tw * th * 4 && tp[3] == 0, $"transparent studio shot: corner alpha {(tp.Length == tw * th * 4 ? tp[3] : -1)}");
            ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Head"));
            Shot("q_04_face_studio.png");
            Window("q_04w_window_studio.png");   // 미리보기 창 (MSAA 8× + 주변 가림, 지터 누적 없음)
            Q(JNode.Obj().Set("studio", false));
            Shot("q_05_face_vrchat.png");
            Window("q_05w_window_vrchat.png");
            Check(Diff(P("q_04w_window_studio.png"), P("q_05w_window_vrchat.png")) > 0.001, "preview window changes with studio quality");
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Shot("q_06_back.png");
            d = Diff(P("q_00_vrchat.png"), P("q_06_back.png"));
            Check(d < 0.0005, $"studio quality off → same as VRChat quality ({d:P3})");
            // 그림자 바닥 + 그라데이션 배경 (보기용)
            Q(JNode.Obj().Set("studio", true));
            Studio(JNode.Obj().Set("enabled", true).Set("background", JNode.Obj().Set("mode", "gradient").Set("top", JNode.FloatArray(new[] { 0.75f, 0.83f, 0.95f })).Set("bottom", JNode.FloatArray(new[] { 0.95f, 0.9f, 0.85f }))).Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("strength", 0.6f)));
            Shot("q_07_floor_studio.png");
            Q(JNode.Obj().Set("studio", false));
            Shot("q_08_floor_vrchat.png");
            Studio(JNode.Obj().Set("enabled", false));
            Q(JNode.Obj().Set("maxFps", 60));
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        var log = File.Exists(P("renderer.log")) ? File.ReadAllText(P("renderer.log")) : "";
        Check(log.Contains("프레임 제한: 30 fps") && log.Contains("스튜디오 고화질 켬"), "renderer logged frame limit and studio quality");
        foreach (var line in log.Split('\n').Where(l => l.Contains("rror") && (l.Contains("Shader") || l.Contains("셰이더") || l.Contains("StudioAo"))).Take(5)) Console.WriteLine("   log: " + line.Trim());
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>MP4 트랙마다 처리기 종류 (hdlr: vide·soun …).</summary>
    static List<string> Mp4Handlers(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        var b = File.ReadAllBytes(path);
        for (int o = 4; o + 16 <= b.Length; o++)
            if (b[o] == 'h' && b[o + 1] == 'd' && b[o + 2] == 'l' && b[o + 3] == 'r') list.Add(System.Text.Encoding.ASCII.GetString(b, o + 12, 4));
        return list;
    }

    /// <summary>MP4 상자에서 첫 트랙의 너비·높이(tkhd), 표본 수(stsz), 길이(mvhd).</summary>
    static (int w, int h, int samples, double seconds) Mp4Info(string path)
    {
        var b = File.ReadAllBytes(path);
        int w = 0, h = 0, samples = -1; double secs = 0;
        uint U32(int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        void Walk(int start, int end)
        {
            for (int o = start; o + 8 <= end;)
            {
                long size = U32(o); var type = System.Text.Encoding.ASCII.GetString(b, o + 4, 4);
                int hdr = 8;
                if (size == 1) { size = (long)((ulong)U32(o + 8) << 32 | U32(o + 12)); hdr = 16; }
                if (size == 0) size = end - o;
                if (size < hdr) break;
                int body = o + hdr, stop = (int)Math.Min(end, o + size);
                switch (type)
                {
                    case "moov": case "trak": case "mdia": case "minf": case "stbl": Walk(body, stop); break;
                    case "mvhd":
                        if (b[body] == 1) secs = (double)((ulong)U32(body + 24) << 32 | U32(body + 28)) / U32(body + 20);
                        else secs = (double)U32(body + 16) / U32(body + 12);
                        break;
                    case "tkhd": if (w == 0) { int off = b[body] == 1 ? 88 : 76; w = (int)(U32(body + off) >> 16); h = (int)(U32(body + off + 4) >> 16); } break;
                    case "stsz": if (samples < 0) samples = (int)U32(body + 8); break;
                }
                o = (int)(o + size);
            }
        }
        Walk(0, b.Length);
        return (w, h, samples, secs);
    }

    /// <summary>시험용 Radiance HDR (RLE 없이).</summary>
    static void WriteHdr(string path, int w, int h, Func<int, int, (float R, float G, float B)> color)
    {
        using var f = File.Create(path);
        var head = System.Text.Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n");
        f.Write(head, 0, head.Length);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y);
                float m = Math.Max(r, Math.Max(g, b));
                int e = (int)Math.Ceiling(Math.Log(m, 2) + 1e-6);
                float sc = (float)Math.Pow(2, -e) * 256;
                f.WriteByte((byte)Math.Min(255, r * sc)); f.WriteByte((byte)Math.Min(255, g * sc)); f.WriteByte((byte)Math.Min(255, b * sc)); f.WriteByte((byte)(e + 128));
            }
    }

    static int FaceTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string? photo)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(250); Capture(ch, wait, P(file), false); }
        JNode Status(Func<JNode, bool> ok, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds); JNode last = JNode.Obj();
            while (DateTime.UtcNow < until)
            {
                try { last = wait(Msg.TrackingStatus, 3000); } catch (TimeoutException) { continue; }
                if (ok(last)) return last;
            }
            return last;
        }
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
            Shot("face_00_rest.png");

            // 아바타 기본 표정
            var exprs = doc.Original.Expressions;
            Check(exprs.Count > 0, $"avatar default expressions in IR: {exprs.Count} ({string.Join(", ", exprs.Take(8).Select(e => FaceExpression.GroupLabel(e.Layer) + "/" + e.Name))})");
            if (exprs.Count > 0)
            {
                var e0 = exprs[0];
                var ex = FaceExpression.FromIr(doc.Original, e0);
                Check(ex["shapes"].Count == e0.Shapes.Count, $"expression {e0.Name}: {ex["shapes"].Count}/{e0.Shapes.Count} shapes found on this avatar");
                // 표정이 덮는 블렌드쉐이프에 사용자 값을 먼저 넣어 둔다 (표정을 빼면 보여야 한다)
                var (r0, s0, v0) = FaceExpression.Shapes(ex).First(x => x.Value > 0);
                doc.Set(Addr.BlendShape(r0, s0), 0f == v0 ? 50f : 0f, "test");
                Shot("face_01_user.png");
                doc.Set(Addr.Expression, ex, "표정");
                Shot("face_02_expression.png");
                Check(Diff(P("face_01_user.png"), P("face_02_expression.png")) > 0.002, $"expression {e0.Name} changes the face ({Diff(P("face_01_user.png"), P("face_02_expression.png")):P2})");
                doc.Set(Addr.BlendShape(r0, s0), 30f, "user under expression");
                Shot("face_03_user_under.png");
                Check(Diff(P("face_02_expression.png"), P("face_03_user_under.png")) < 0.0005, "user blendshape edit stays hidden under the expression");
                doc.Reset(Addr.Expression, "표정 빼기");
                Shot("face_04_no_expression.png");
                Check(Diff(P("face_02_expression.png"), P("face_04_no_expression.png")) > 0.002, "removing the expression shows the user value again");
                doc.Undo(); doc.Undo(); doc.Undo(); doc.Undo();
                Shot("face_05_undo.png");
                double back = Diff(P("face_00_rest.png"), P("face_05_undo.png"));
                Check(back < 0.0005, $"undo all → same as start ({back:P3})");
            }

            // VMC (외부 앱): ARKit 값을 OSC로
            ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "vmc").Set("port", 39539).Set("smoothing", 0f));
            using (var udp = new System.Net.Sockets.UdpClient())
            {
                var to = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 39539);
                var vals = new Dictionary<string, float> { ["eyeBlinkLeft"] = 1, ["eyeBlinkRight"] = 1, ["jawOpen"] = 0.8f };
                var stop = DateTime.UtcNow.AddSeconds(4);
                var sender = new Thread(() =>
                {
                    while (DateTime.UtcNow < stop)
                    {
                        var msgs = vals.Select(kv => Osc("/VMC/Ext/Blend/Val", kv.Key, kv.Value)).Append(Osc("/VMC/Ext/Blend/Apply", null, 0)).ToList();
                        var b = Bundle(msgs);
                        udp.Send(b, b.Length, to);
                        Thread.Sleep(33);
                    }
                }) { IsBackground = true };
                sender.Start();
                var st = Status(m => m["face"].AsBool, 3);
                Check(st["face"].AsBool && st["mapped"].AsInt > 0, $"VMC: receiving ({st["fps"].AsFloat:0} fps), mapped shapes {st["mapped"].AsInt}, perfect sync {st["perfectSync"].AsBool}, eyeBlinkLeft {st["values"]["eyeBlinkLeft"].AsFloat:0.##}");
                Shot("face_06_vmc.png");
                Check(Diff(P("face_00_rest.png"), P("face_06_vmc.png")) > 0.002, $"VMC values move the face ({Diff(P("face_00_rest.png"), P("face_06_vmc.png")):P2})");
                ch.Send(Msg.Make(Msg.FaceTracking).Set("snapshot", true));
                var snap = Status(m => m["snapshot"].AsBool, 3);
                Check(snap["shapes"].Count > 0, $"snapshot → {snap["shapes"].Count} shapes ({string.Join(", ", snap["shapes"].Items.Where(x => x["v"].AsFloat > 1).Take(4).Select(x => x["s"].AsString + " " + x["v"].AsFloat))})");
                sender.Join();
            }
            // 눈 감기 곡선: 반쯤 감은 값(0.6)이 또렷하게 1이면 거의 감김, 0이면 그보다 덜
            float BlinkAt(float sharp)
            {
                ch.Send(Msg.Make(Msg.FaceTracking).Set("eye", JNode.Obj().Set("sensitivity", 0.5f).Set("sharp", sharp).Set("link", true)));
                using var udp = new System.Net.Sockets.UdpClient();
                var to = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 39539);
                var until = DateTime.UtcNow.AddSeconds(2.5);
                while (DateTime.UtcNow < until)
                {
                    var b = Bundle(new List<byte[]> { Osc("/VMC/Ext/Blend/Val", "eyeBlinkLeft", 0.6f), Osc("/VMC/Ext/Blend/Val", "eyeBlinkRight", 0.55f), Osc("/VMC/Ext/Blend/Apply", null, 0) });
                    udp.Send(b, b.Length, to); Thread.Sleep(33);
                }
                // 쌓인 상태를 다 읽고 마지막 값 (보내기를 멈춘 뒤에는 값이 그대로 남는다)
                JNode last = JNode.Obj(); var end = DateTime.UtcNow.AddSeconds(1.5);
                while (DateTime.UtcNow < end) { try { last = wait(Msg.TrackingStatus, 500); } catch (TimeoutException) { } }
                return last["values"]["eyeBlinkLeft"].AsFloat;
            }
            float soft = BlinkAt(0f), sharpV = BlinkAt(1f);
            Check(sharpV > 0.95f && soft < sharpV, $"eye curve: blink 0.6 → sharp 0 {soft:0.##}, sharp 1 {sharpV:0.##}");
            ch.Send(Msg.Make(Msg.FaceTracking).Set("eye", JNode.Obj().Set("sensitivity", 0.5f).Set("sharp", 0.5f).Set("link", true)));
            ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "off"));
            Shot("face_07_off.png");
            Check(Diff(P("face_00_rest.png"), P("face_07_off.png")) < 0.0005, "tracking off → face back to rest");

            // 수동 연결 (Addr.FaceMap): 자동 연결 목록 → 값 하나 시험 → 자동 연결 끄기 → 직접 더하기 → 시험 끝
            ch.Send(Msg.Make(Msg.FaceTracking).Set("links", true));
            var ls = Status(m => m["auto"].Kind == JKind.Array, 5);
            var auto = ls["auto"].Items.Select(x => (x["src"].AsString, x["r"].AsString, x["s"].AsString)).ToList();
            // 설치된 FT 애니메이터를 쓰면(mode ft) 이름으로 잇는 자동 연결은 비어 있다
            Check(auto.Count > 0 || ls["mode"].AsString == "ft", $"auto links listed: {auto.Count} ({string.Join(", ", auto.Take(4).Select(a => a.Item1 + "→" + a.Item3))}), mode {ls["mode"].AsString}");
            if (auto.Count > 0)
            {
                var a0 = auto.FirstOrDefault(a => a.Item1 == "jawOpen"); if (a0.Item1 == null) a0 = auto[0];
                ch.Send(Msg.Make(Msg.FaceTracking).Set("test", a0.Item1));
                Shot("face_08_test_auto.png");
                double dAuto = Diff(P("face_00_rest.png"), P("face_08_test_auto.png"));
                Check(dAuto > 0.002, $"test {a0.Item1} = 100% moves {a0.Item3} with tracking off ({dAuto:P2})");
                doc.Set(Addr.FaceMap, FaceMap.Remove(doc.Get(Addr.FaceMap), a0.Item1, a0.Item2, a0.Item3, auto: true), "off");
                Shot("face_09_test_off.png");
                double dOff = Diff(P("face_00_rest.png"), P("face_09_test_off.png"));
                Check(dOff < dAuto * 0.5, $"turning the auto link off removes its movement ({dOff:P2})");
                var mesh = doc.Original.FindMesh(doc.Original.FindRenderer(a0.Item2)!.Mesh)!;
                var other = mesh.BlendShapes.Select(b => b.Name).FirstOrDefault(n => n == "vrc.v_aa") ?? mesh.BlendShapes.Select(b => b.Name).First(n => n != a0.Item3 && !n.StartsWith("-") && !n.StartsWith("="));
                doc.Set(Addr.FaceMap, FaceMap.Add(doc.Get(Addr.FaceMap), a0.Item1, a0.Item2, other), "add");
                Shot("face_10_manual.png");
                double dMan = Diff(P("face_09_test_off.png"), P("face_10_manual.png"));
                Check(dMan > 0.002, $"manual link {a0.Item1} → {other} moves the face ({dMan:P2})");
                ch.Send(Msg.Make(Msg.FaceTracking).Set("test", ""));
                doc.Reset(Addr.FaceMap, "auto");
                Shot("face_11_test_end.png");
                Check(Diff(P("face_00_rest.png"), P("face_11_test_end.png")) < 0.0005, "test ended, links back to auto → rest face");
            }

            // 연결 방식 (설치된 FT 애니메이터 → ft, ARKit·Unified 이름 → sync, MMD 모프 → mmd)
            ch.Send(Msg.Make(Msg.FaceTracking).Set("links", true));
            var modeSt = Status(m => m.Has("mode"), 5);
            string mode = modeSt["mode"].AsString;
            Console.WriteLine($"   face mode {mode}, FT layers {modeSt["ftLayers"].AsInt}, FT shapes {modeSt["ftShapes"].AsInt}, auto links {modeSt["auto"].Count}, IR faceTracking {(doc.Original.Raw["faceTracking"]["layers"].Count)}");
            if (doc.Original.Raw["faceTracking"]["layers"].Count > 0)
                Check(mode == "ft" && modeSt["ftShapes"].AsInt > 0, $"installed FT animator is used ({modeSt["ftLayers"].AsInt} layers, {modeSt["ftShapes"].AsInt} shapes)");

            // 트래커 앱 (Babble · VRCFT식 OSC): Babble 이름(경로 없음) + VRCFT v2 파라미터
            int FaceOsc(string name, Dictionary<string, float> vals, double seconds = 2.5)
            {
                ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "osc").Set("port", 8888).Set("smoothing", 0f));
                Thread.Sleep(500);
                using var udp = new System.Net.Sockets.UdpClient();
                var to = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 8888);
                var until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until)
                {
                    foreach (var kv in vals) { var b = OscF(kv.Key, kv.Value); udp.Send(b, b.Length, to); }
                    Thread.Sleep(33);
                }
                var st = Status(m => m["face"].AsBool, 3);
                Shot(name);
                Console.WriteLine($"   OSC {name}: {st["text"].AsString} · mapped {st["mapped"].AsInt} · " + string.Join(", ", st["values"].Props.Where(kv => kv.Value.AsFloat > 0.05f).Take(8).Select(kv => $"{kv.Key} {kv.Value.AsFloat:0.##}")));
                return st["face"].AsBool ? 1 : 0;
            }
            int got = FaceOsc("face_14_osc_rest.png", new Dictionary<string, float> { ["/jawOpen"] = 0, ["/avatar/parameters/v2/EyeLidLeft"] = 0.75f, ["/avatar/parameters/v2/EyeLidRight"] = 0.75f });
            Check(got == 1, "OSC source receives values");
            FaceOsc("face_15_osc_babble.png", new Dictionary<string, float> { ["/jawOpen"] = 0.7f, ["/mouthSmileLeft"] = 0.8f, ["/mouthSmileRight"] = 0.8f, ["/mouthFunnel"] = 0f,
                ["/avatar/parameters/v2/EyeLidLeft"] = 0f, ["/avatar/parameters/v2/EyeLidRight"] = 0f });
            double dOsc = Diff(P("face_14_osc_rest.png"), P("face_15_osc_babble.png"));
            Check(dOsc > 0.002, $"Babble mouth + VRCFT v2 eyelids move the face ({dOsc:P2})");
            ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "off"));
            Shot("face_16_osc_off.png");
            Check(Diff(P("face_00_rest.png"), P("face_16_osc_off.png")) < 0.0005, "OSC off → face back to rest");

            // 말하기: 합성한 모음(아·이)을 마이크 대신 넣는다
            string Vowel(string name, (double F, double B)[] forms)
            {
                const int R = 16000; int n = R * 2; var x = new double[n]; var rnd = new Random(1);
                for (int i = 0; i < n; i++) x[i] = (i % (R / 120) == 0 ? 1 : 0) + (rnd.NextDouble() - 0.5) * 0.02;
                foreach (var (F, B) in forms)
                {
                    double r = Math.Exp(-Math.PI * B / R), th = 2 * Math.PI * F / R, a1 = 2 * r * Math.Cos(th), a2 = -r * r;
                    var y = new double[n];
                    for (int i = 0; i < n; i++) y[i] = x[i] + a1 * (i > 0 ? y[i - 1] : 0) + a2 * (i > 1 ? y[i - 2] : 0);
                    x = y;
                }
                double mx = x.Max(v => Math.Abs(v));
                var path = P(name);
                using var w = new BinaryWriter(File.Create(path));
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(R); w.Write(R * 2); w.Write((short)2); w.Write((short)16); w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
                foreach (var v in x) w.Write((short)(v / mx * 0.3 * 32767));
                return path;
            }
            JNode LipRun(string wav, string shot)
            {
                ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", true).Set("wav", wav).Set("gain", 1f)));
                var st = Status(m => m["lip"]["level"].AsFloat > 0.3f, 4);
                Thread.Sleep(300);
                st = Status(m => m["lip"].Kind == JKind.Object, 3);
                Shot(shot);
                var v = st["values"];
                Console.WriteLine($"   lip {Path.GetFileName(wav)}: {st["lip"].ToJson()} · aa {v["lip.aa"].AsFloat:0.##} ih {v["lip.ih"].AsFloat:0.##} ou {v["lip.ou"].AsFloat:0.##} e {v["lip.e"].AsFloat:0.##} oh {v["lip.oh"].AsFloat:0.##}");
                return st;
            }
            var la = LipRun(Vowel("vowel_a.wav", new[] { (730.0, 90.0), (1090, 110), (2440, 170) }), "face_17_lip_a.png");
            var li = LipRun(Vowel("vowel_i.wav", new[] { (270.0, 60.0), (2290, 100), (3010, 150) }), "face_18_lip_i.png");
            float Top(JNode s, string k) => s["values"][k].AsFloat;
            Check(la["lip"]["level"].AsFloat > 0.3f && Top(la, "lip.aa") > Top(la, "lip.ih") && Top(la, "lip.aa") > Top(la, "lip.oh"), "lip sync: vowel a → lip.aa strongest");
            Check(Top(li, "lip.ih") > Top(li, "lip.aa") && Top(li, "lip.ih") > Top(li, "lip.ou"), "lip sync: vowel i → lip.ih strongest");
            double dLip = Diff(P("face_00_rest.png"), P("face_17_lip_a.png"));
            Check(dLip > 0.002, $"lip sync moves the mouth ({dLip:P2})");
            ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", false)));
            Shot("face_19_lip_off.png");
            Check(Diff(P("face_00_rest.png"), P("face_19_lip_off.png")) < 0.0005, "lip sync off → face back to rest");

            // 웹캠과 같은 경로: 사진 한 장
            if (photo != null)
            {
                ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "image").Set("path", Path.GetFullPath(photo)).Set("smoothing", 0f));
                var st = Status(m => m["face"].AsBool, 15);
                var v = st["values"];
                Check(st["face"].AsBool, $"webcam pipeline on photo: face found ({st["text"].AsString}, {st["fps"].AsFloat:0} fps)");
                Console.WriteLine($"   head (yaw left+, pitch up+, roll) {st["headDeg"].ToJson()}, head bones {st["head"].AsBool}, gaze bones {st["gazeBones"].AsBool}, mapped {st["mapped"].AsInt}");
                Console.WriteLine("   values: " + string.Join(", ", v.Props.Where(kv => kv.Value.AsFloat > 0.05f).Select(kv => $"{kv.Key} {kv.Value.AsFloat:0.##}")));
                Check(v["eyeBlinkLeft"].AsFloat < 0.5f && v["eyeBlinkRight"].AsFloat < 0.5f, "open eyes in the photo → not blinking");
                Check(v["mouthSmileLeft"].AsFloat + v["mouthSmileRight"].AsFloat > 0.2f || v["jawOpen"].AsFloat > 0.1f, "smiling, open mouth in the photo → smile or jaw value");
                // 팔: 트래킹 중 T자 팔을 내린다 (끄면 원래대로)
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
                ch.Send(Msg.Make(Msg.FaceTracking).Set("arms", true).Set("body", true));
                Shot("face_12_arms_on.png");
                ch.Send(Msg.Make(Msg.FaceTracking).Set("arms", false));
                Shot("face_13_arms_off.png");
                double dArms = Diff(P("face_12_arms_on.png"), P("face_13_arms_off.png"));
                Check(dArms > 0.002, $"relaxed arms change the full-body view ({dArms:P2})");
                ch.Send(Msg.Make(Msg.FaceTracking).Set("arms", true));
                ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "off"));
            }
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static byte[] Osc(string address, string? s, float f)
    {
        var o = new List<byte>();
        void Str(string x) { var b = System.Text.Encoding.UTF8.GetBytes(x); o.AddRange(b); int pad = 4 - b.Length % 4; for (int i = 0; i < pad; i++) o.Add(0); }
        Str(address);
        if (s == null) { Str(","); return o.ToArray(); }
        Str(",sf"); Str(s);
        var fb = BitConverter.GetBytes(f); Array.Reverse(fb); o.AddRange(fb);
        return o.ToArray();
    }

    /// <summary>OSC 메시지 하나: 주소 + float 하나 (Babble 앱이 보내는 모양).</summary>
    static byte[] OscF(string address, float f)
    {
        var o = new List<byte>();
        void Str(string x) { var b = System.Text.Encoding.UTF8.GetBytes(x); o.AddRange(b); int pad = 4 - b.Length % 4; for (int i = 0; i < pad; i++) o.Add(0); }
        Str(address); Str(",f");
        var fb = BitConverter.GetBytes(f); Array.Reverse(fb); o.AddRange(fb);
        return o.ToArray();
    }

    static byte[] Bundle(List<byte[]> msgs)
    {
        var o = new List<byte>();
        o.AddRange(System.Text.Encoding.ASCII.GetBytes("#bundle\0"));
        o.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        foreach (var m in msgs) { var n = BitConverter.GetBytes(m.Length); Array.Reverse(n); o.AddRange(n); o.AddRange(m); }
        return o.ToArray();
    }

    /// <summary>핸들·방향 기즈모 모양 (setView handleStyle·axisStyle). 창 화면을 그대로 찍어야 하므로 -batchmode 없이 띄운다.</summary>
    static int GizmoTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetView).Set("poseBones", true).Set("handleSpace", "local").Set("grid", true).Set("gizmo", true));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
            Thread.Sleep(300);
            ch.Send(Msg.Make(Msg.SetCamera).Set("state", JNode.Obj().Set("yaw", 145f).Set("pitch", 14f)));
            ch.Send(Msg.Make(Msg.Select).Set("kind", "bone").Set("bone", "Chest"));
            wait(Msg.Selected, 10000);
            var shots = new[] { ("line", "disc", 1.0), ("bold", "disc", 1.0), ("solid", "cube", 1.0), ("soft", "mini", 1.0), ("bold", "cube", 1.25) };
            string prev = null;
            foreach (var (style, axis, size) in shots)
            {
                ch.Send(Msg.Make(Msg.SetView).Set("handleStyle", style).Set("axisStyle", axis).Set("handleSize", size));
                Thread.Sleep(400);
                var path = P($"gizmo_{style}_{axis}_{size:0.##}.png");
                ch.Send(Msg.Make(Msg.ScreenShot).Set("path", path));
                var r = wait(Msg.CaptureDone, 20000);
                Check(r["ok"].AsBool && File.Exists(path), $"screenshot {Path.GetFileName(path)} {r["error"].AsString}");
                if (prev != null) Check(Diff(prev, path) > 0.0005, $"{style}/{axis} looks different from the previous shot ({Diff(prev, path):P2})");
                prev = path;
            }
            // 회색 배경 + 원 (방향 기즈모가 밝은 배경에서도 보이는지)
            ch.Send(Msg.Make(Msg.SetView).Set("handleStyle", "bold").Set("axisStyle", "disc").Set("handleSize", 1.0).Set("background", "gray"));
            Thread.Sleep(400);
            ch.Send(Msg.Make(Msg.ScreenShot).Set("path", P("gizmo_disc_gray.png")));
            wait(Msg.CaptureDone, 20000);
            ch.Send(Msg.Make(Msg.SetView).Set("background", "env"));
            // 아바타 전체를 고르면 발밑 핸들 (굵은 선)
            ch.Send(Msg.Make(Msg.SetView).Set("handleStyle", "solid").Set("axisStyle", "cube").Set("handleSize", 1.0));
            ch.Send(Msg.Make(Msg.Select).Set("kind", "avatar"));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Thread.Sleep(600);
            ch.Send(Msg.Make(Msg.ScreenShot).Set("path", P("gizmo_avatar_solid.png")));
            wait(Msg.CaptureDone, 20000);
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static int HandlesTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file)
        {
            Thread.Sleep(250);
            Capture(ch, wait, P(file), false);
        }
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            var rest = loaded["pose"]["rest"].Props.ToDictionary(kv => kv.Key, kv => kv.Value.AsFloat);
            ch.Send(Msg.Make(Msg.SetView).Set("poseBones", true).Set("handleSpace", "local"));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            var cam = JNode.Null;
            Shot("handles_00_rest.png");

            // 본: 화살표로 왼손을 위로 → 팔이 굽는다 (머슬로 기록)
            ch.Send(Msg.Make(Msg.Select).Set("kind", "bone").Set("bone", "LeftHand"));
            var sel = wait(Msg.Selected, 10000);
            Check(sel["kind"].AsString == "bone" && sel["bone"].AsString == "LeftHand", $"select bone from list → selected {sel["kind"].AsString} {sel["bone"].AsString}");
            ch.Send(Msg.Make(Msg.SetView).Set("handleSpace", "world"));   // 월드 Y = 위 (손의 로컬 Y는 팔 방향이라 T 포즈에서는 더 펴지지 않는다)
            ch.Send(Msg.Make(Msg.PoseBone).Set("bone", "LeftHand").Set("arrow", "y").Set("amount", 0.15f));
            var ed = wait(Msg.PoseEdited, 10000);
            float M(JNode pose, string n) => PoseValue.MusclesOf(pose).TryGetValue(n, out var v) ? v : 0;
            Check(Math.Abs(M(ed["pose"], "Left Forearm Stretch") - rest["Left Forearm Stretch"]) > 0.05f || Math.Abs(M(ed["pose"], "Left Arm Down-Up") - rest["Left Arm Down-Up"]) > 0.05f,
                $"bone arrow moves the hand by IK (Left Arm Down-Up {rest["Left Arm Down-Up"]:0.##} → {M(ed["pose"], "Left Arm Down-Up"):0.##}, Forearm Stretch {rest["Left Forearm Stretch"]:0.##} → {M(ed["pose"], "Left Forearm Stretch"):0.##})");
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
            ch.Send(Msg.Make(Msg.SetView).Set("handleSpace", "local"));

            // 아바타 전체: 고르면 본 선택이 풀리고, 화살표·고리 → avatarMoved → Host 기록 → 같은 자리로 다시 그림
            ch.Send(Msg.Make(Msg.Select).Set("kind", "avatar"));
            JNode Sel(string kind) { for (int i = 0; i < 4; i++) { var m = wait(Msg.Selected, 10000); if (m["kind"].AsString == kind) return m; } return JNode.Obj(); }
            Check(Sel("avatar")["kind"].AsString == "avatar", "select avatar → selected avatar");
            ch.Send(Msg.Make(Msg.MoveTarget).Set("arrow", "x").Set("amount", 0.4f));
            var mv = wait(Msg.AvatarMoved, 10000);
            var t1 = mv["trs"].ToFloatArray();
            Check(t1.Length == 7 && Math.Abs(t1[0] - 0.4f) < 0.01f && Math.Abs(t1[1]) < 0.001f, $"avatar arrow x 0.4 → avatarMoved [{string.Join(", ", t1.Select(x => x.ToString("0.###")))}]");
            doc.Set(Addr.AvatarTransform, mv["trs"], "아바타 옮기기");
            ch.Send(Msg.Make(Msg.MoveTarget).Set("ring", "y").Set("angle", 90f));
            var rot = wait(Msg.AvatarMoved, 10000);
            var t2 = rot["trs"].ToFloatArray();
            Check(Math.Abs(Math.Abs(t2[4]) - 0.7071f) < 0.01f && Math.Abs(t2[0] - 0.4f) < 0.01f, $"avatar ring y 90° around its feet → rotation y {t2[4]:0.###}, position kept x {t2[0]:0.###}");
            doc.Set(Addr.AvatarTransform, rot["trs"], "아바타 돌리기");
            Shot("handles_01_avatar.png");
            Check(Diff(P("handles_00_rest.png"), P("handles_01_avatar.png")) > 0.01, "avatar moved and turned → image changed");
            // 바닥 맞추기는 아바타 공간에서: 높이 올린 채 앉아도 가장 낮은 곳이 아바타 발밑 높이
            ch.Send(Msg.Make(Msg.MoveTarget).Set("arrow", "y").Set("amount", 0.5f));
            var up = wait(Msg.AvatarMoved, 10000);
            doc.Set(Addr.AvatarTransform, up["trs"], "아바타 올리기");
            ch.Send(Msg.Make(Msg.SetView).Set("floorFit", true));
            var sit = PoseValue.Make(new Dictionary<string, float> { ["Left Upper Leg Front-Back"] = -1f, ["Right Upper Leg Front-Back"] = -1f, ["Left Lower Leg Stretch"] = -1f, ["Right Lower Leg Stretch"] = -1f });
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", sit).Set("report", true));
            var fl = wait(Msg.PoseApplied, 10000);
            Check(Math.Abs(fl["floorNow"].AsFloat - fl["floorRest"].AsFloat) < 0.002f, $"floor fit follows the moved avatar (rest {fl["floorRest"].AsFloat:0.###}, now {fl["floorNow"].AsFloat:0.###}, avatar raised {up["trs"].ToFloatArray()[1]:0.##} m)");
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
            doc.Undo(); doc.Undo(); doc.Undo();
            Check(!doc.IsModified(Addr.AvatarTransform), "undo ×3 → avatar back at origin");

            // 아이템: 뼈를 합친 옷은 고르지 않고, 뼈 하나에 붙은 악세서리는 옮겨진다
            var merged = doc.Original.Outfits.FirstOrDefault(o => o.Method == "ma-merge" || o.Method == "name");
            if (merged != null)
            {
                ch.Send(Msg.Make(Msg.Select).Set("kind", "item").Set("node", merged.Node));
                var n = wait(Msg.Notice, 10000);
                Check(n["text"].AsString.Contains("옮길 수 없어요"), $"merged outfit {merged.Name} is not selectable: {n["text"].AsString.Split('(')[0].Trim()}");
            }
            // 보이는 메시가 있는 것만, 기믹은 빼고 (기믹 메시는 꺼져 있거나 그림자 전용처럼 화면에 안 보여 옮겨도 그대로다 — 큰 시험 장면의 그림자 기믹)
            var nodes = doc.Original.Nodes;
            bool Shown(int root) => doc.Original.Renderers.Any(r =>
            {
                if (!r.Enabled) return false;
                for (int x = r.Node; x >= 0 && x < nodes.Count; x = nodes[x].Parent) { if (!nodes[x].Active) return false; if (x == root) return true; }
                return false;
            });
            var acc = doc.Original.Outfits.FirstOrDefault(o => (o.Method == "manual" || o.Method == "none" || o.Method == "ma-proxy") && o.Kind != "gimmick" && Shown(o.Node));
            if (acc == null) Console.WriteLine("   (이 IR에는 뼈 하나에 붙은 아이템이 없어 아이템 옮기기는 건너뜀)");
            else
            {
                Shot("handles_02_before_item.png");
                ch.Send(Msg.Make(Msg.Select).Set("kind", "item").Set("node", acc.Node));
                var si = Sel("item");
                Check(si["node"].AsInt == acc.Node, $"select item {acc.Name} → selected item {si["node"].AsInt}");
                ch.Send(Msg.Make(Msg.MoveTarget).Set("arrow", "y").Set("amount", 0.06f));
                var nm = wait(Msg.NodeMoved, 10000);
                var before = doc.Get(Addr.NodeTransform(acc.Node)).ToFloatArray();
                doc.Set(Addr.NodeTransform(acc.Node), nm["trs"], "옮기기");
                ch.Send(Msg.Make(Msg.MoveTarget).Set("ring", "z").Set("angle", 45f));
                var nr = wait(Msg.NodeMoved, 10000);
                doc.Set(Addr.NodeTransform(acc.Node), nr["trs"], "돌리기");
                var after = doc.Get(Addr.NodeTransform(acc.Node)).ToFloatArray();
                double moved = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (after[i] - before[i]) * (after[i] - before[i])));
                Check(after.Length == 10 && moved > 0.001, $"item arrow + ring → nodeMoved, local position moved {moved * 100:0.#} cm (parent scale)");
                Shot("handles_03_item.png");
                Check(Diff(P("handles_02_before_item.png"), P("handles_03_item.png")) > 0.0002, $"item moved → image changed ({Diff(P("handles_02_before_item.png"), P("handles_03_item.png")):P3})");
                // 업로드 계획·다시 만들기(같은 IR로 옮기기)에도 옮긴 자리가 남는다
                var plan = UploadPlan.Build(doc, "test");
                var po = plan["outfits"].Items.First(o => o["prefab"].AsString == acc.Prefab && o["name"].AsString == acc.Name);
                Check(Values.Equal(po["trs"], doc.Get(Addr.NodeTransform(acc.Node))), "upload plan uses the moved transform");
                var remapped = Recompose.RemapChangeset(doc.ChangesetToJson(), doc.Original, doc.Original);
                Check(remapped.Items.Any(op => op["a"].AsString == Addr.NodeTransform(acc.Node).ToString()), "rebuild keeps the moved transform");
                doc.Undo(); doc.Undo();
                Shot("handles_04_item_undo.png");
                double back = Diff(P("handles_02_before_item.png"), P("handles_04_item_undo.png"));
                Check(back < 0.001, $"undo → item back ({back:P3} pixels changed)");
            }

            // 화면 점 고르기 (빈 곳 더블클릭과 같은 경로): 가운데는 아바타 메시, 구석은 빈 곳
            ch.Send(Msg.Make(Msg.Select).Set("kind", ""));
            // 고른 것이 풀렸다는 응답(조작·본 조작 양쪽에서 올 수 있음)을 모두 비운다 — 남아 있으면 아래 화면 고르기가 그것을 받는다
            try { while (true) wait(Msg.Selected, 500); } catch (TimeoutException) { }
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Thread.Sleep(300);
            ch.Send(Msg.Make(Msg.Select).Set("screen", JNode.FloatArray(new[] { 480f, 300f })));
            var sp = wait(Msg.Selected, 10000);
            Check(sp["kind"].AsString == "avatar" || sp["kind"].AsString == "item", $"pick at screen centre → selected {sp["kind"].AsString} {(sp.Has("node") ? sp["node"].AsInt.ToString() : "")}");
            ch.Send(Msg.Make(Msg.Select).Set("screen", JNode.FloatArray(new[] { 5f, 5f })));
            var sn = wait(Msg.Selected, 10000);
            Check(sn["kind"].AsString == "", "pick at empty corner → selection cleared");
            Shot("handles_05_end.png");
            double end = Diff(P("handles_00_rest.png"), P("handles_05_end.png"));
            Check(end < 0.001, $"everything undone → same as start ({end:P3} pixels changed)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static int LimitsTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        var copy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ir))!, Path.GetFileNameWithoutExtension(ir) + ".limits_e2e.json");
        try
        {
            var j = JNode.Parse(File.ReadAllText(ir));
            j.Set("humanoidSettings", JNode.Obj().Set("armTwist", 0f)
                .Set("limits", JNode.Obj().Set("LeftUpperArm", JNode.Obj().Set("min", JNode.FloatArray(new[] { -90f, -100f, -120f })).Set("max", JNode.FloatArray(new[] { 90f, 100f, 120f }))
                    .Set("center", JNode.FloatArray(new[] { 0f, 0f, 0f })).Set("axisLength", 0f))));
            File.WriteAllText(copy, j.ToJson());
            var pose = PoseValue.Make(new Dictionary<string, float> { ["Left Arm Down-Up"] = -1f, ["Right Arm Down-Up"] = -1f, ["Left Arm Twist In-Out"] = 1f, ["Right Arm Twist In-Out"] = 1f,
                ["Left Forearm Stretch"] = 0.5f, ["Right Forearm Stretch"] = 0.5f, ["Left Upper Leg Front-Back"] = 0.6f, ["Right Upper Leg Front-Back"] = 0.6f, ["Left Lower Leg Stretch"] = 1f, ["Right Lower Leg Stretch"] = 1f });
            JNode Load(string path, string tag)
            {
                ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", path));
                var loaded = wait(Msg.SceneLoaded, 60000);
                Check(loaded["pose"]["ok"].AsBool, $"{tag}: pose ready {loaded["pose"]["problem"].AsString}");
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
                Thread.Sleep(200); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full")); Thread.Sleep(300);
                Capture(ch, wait, P($"limits_{tag}_rest.png"), false);
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose));
                Thread.Sleep(200); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full")); Thread.Sleep(300);
                Capture(ch, wait, P($"limits_{tag}_pose.png"), false);
                return loaded["pose"]["rest"];
            }
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            var r0 = Load(ir, "default");
            var r1 = Load(copy, "custom");
            Console.WriteLine($"   rest Left Arm Down-Up: default {r0["Left Arm Down-Up"].AsFloat:0.###}, custom {r1["Left Arm Down-Up"].AsFloat:0.###} (범위가 바뀌면 같은 T 포즈의 머슬 값이 바뀐다)");
            double rest = Diff(P("limits_default_rest.png"), P("limits_custom_rest.png"));
            Check(rest < 0.001, $"custom limits keep the loaded pose ({rest:P3} pixels changed)");
            double posed = Diff(P("limits_default_pose.png"), P("limits_custom_pose.png"));
            Check(posed > 0.005, $"custom limits change the same muscle pose ({posed:P2} pixels changed)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        finally { try { File.Delete(copy); } catch { } }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 자세 시험: 렌더러의 머슬 이름이 Host 표(Muscles.Names)와 같은지, 프리셋·.anim(추가 인자)이 화면을 바꾸는지,
    /// 자세를 빼면 처음 화면으로 돌아오는지. PhysBone은 꺼서(시간을 멈추면 흔들림 뼈가 따라오지 않는다) 자세만 본다.
    /// </summary>
    static int PoseTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string[] anims)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Pose(JNode pose, string file, string cam = "full")
        {
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose));
            Thread.Sleep(200);   // 스키닝 범위가 새 자세로 바뀐 뒤 카메라를 맞춘다
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", cam));
            Thread.Sleep(200);
            Capture(ch, wait, P(file), false);
        }
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            var pose = loaded["pose"];
            Check(pose["ok"].AsBool, $"pose ready (humanoid {loaded["humanoid"].AsInt}) {pose["problem"].AsString}");
            if (!pose["ok"].AsBool) { ch.Send(Msg.Make(Msg.Quit)); goto end; }
            var rest = pose["rest"].Props.ToDictionary(kv => kv.Key, kv => kv.Value.AsFloat);
            var missing = Muscles.Names.Where(n => !rest.ContainsKey(n)).ToList();
            var extra = rest.Keys.Where(n => !Muscles.IsMuscle(n)).ToList();
            Check(missing.Count == 0 && extra.Count == 0, $"muscle names match Unity ({rest.Count}) " + string.Join(", ", missing.Select(x => "-" + x).Concat(extra.Select(x => "+" + x))));
            Console.WriteLine("   rest: " + string.Join(", ", rest.Where(kv => Math.Abs(kv.Value) > 0.05f).Select(kv => $"{kv.Key}={kv.Value:0.##}")));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Capture(ch, wait, P("pose_00_rest.png"), false);
            Pose(PoseValue.Make(rest), "pose_01_rest_muscles.png");
            Console.WriteLine($"   rest muscles re-applied vs loaded: {Diff(P("pose_00_rest.png"), P("pose_01_rest_muscles.png")):P2} pixels changed");
            foreach (var pr in PosePresets.All)
            {
                Pose(pr.Value, $"pose_p_{pr.Key}.png");
                double d = Diff(P("pose_00_rest.png"), P($"pose_p_{pr.Key}.png"));
                // 쉬는 자세가 T 포즈인 아바타가 많아 T 포즈는 그대로일 수 있다
                if (pr.Key == "tpose") Console.WriteLine($"   preset tpose vs rest: {d:P2} pixels changed");
                else Check(d > 0.01, $"preset {pr.Key} → image changed ({d:P1})");
            }
            int ai = 0;
            foreach (var a in anims)
            {
                var clip = AnimClip.Load(a);
                Console.WriteLine($"   anim {clip.Name}: curves {clip.Curves.Count}, muscles {clip.MuscleCurves.Count()}, length {clip.Length:0.##}s");
                Pose(clip.SamplePose(0), $"pose_a{++ai}_{clip.Name}.png");
            }
            // 뷰포트 본 조작 (끌기와 같은 경로의 poseBone): 머슬 값으로 돌아오고, Host가 그 값을 다시 보내면 같은 화면인지
            Pose(JNode.Null, "pose_b0_rest.png");
            JNode Bone(JNode m, string file)
            {
                ch.Send(m.Set("t", Msg.PoseBone));
                var ed = wait(Msg.PoseEdited, 10000);
                Thread.Sleep(200);   // Pose()와 같이 새 자세의 범위로 카메라를 맞춘 뒤 찍는다
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
                Thread.Sleep(200);
                Capture(ch, wait, P(file), false);
                return ed;
            }
            float M(JNode pose, string n) => PoseValue.MusclesOf(pose).TryGetValue(n, out var v) ? v : 0;
            var e1 = Bone(JNode.Obj().Set("bone", "LeftUpperArm").Set("axis", JNode.FloatArray(new[] { 0f, 0f, 1f })).Set("angle", 50f), "pose_b1_arm.png");
            Check(e1["bone"].AsString == "LeftUpperArm" && Math.Abs(M(e1["pose"], "Left Arm Down-Up") - rest["Left Arm Down-Up"]) > 0.1f,
                $"bone drag → muscles (Left Arm Down-Up {rest["Left Arm Down-Up"]:0.##} → {M(e1["pose"], "Left Arm Down-Up"):0.##})");
            Check(Diff(P("pose_b0_rest.png"), P("pose_b1_arm.png")) > 0.005, "bone drag changed image");
            var rec1 = PoseValue.FromViewport(e1["pose"], rest);
            Check(rec1 != null, "bone drag recorded as pose");
            Pose(rec1 ?? JNode.Null, "pose_b2_host.png");
            double same = Diff(P("pose_b1_arm.png"), P("pose_b2_host.png"));
            // 0도 돌리기: 머슬을 다시 읽어 적용해도 건드리지 않은 자세가 흐르지 않는지 (GetHumanPose는 SetHumanPose의 정확한 역이 아니다)
            Bone(JNode.Obj().Set("bone", "LeftUpperArm").Set("axis", JNode.FloatArray(new[] { 0f, 0f, 1f })).Set("angle", 0f), "pose_b2_noop.png");
            double noop = Diff(P("pose_b2_host.png"), P("pose_b2_noop.png"));
            Check(noop < 0.001, $"no-op bone edit keeps pose ({noop:P3} pixels changed)");
            Check(same < 0.003, $"Host re-sends recorded muscles → same image as drag preview ({same:P3} pixels changed)");
            var e2 = Bone(JNode.Obj().Set("bone", "RightHand").Set("move", JNode.FloatArray(new[] { -0.25f, -0.05f, 0.12f })), "pose_b3_ik.png");
            Check(Math.Abs(M(e2["pose"], "Right Forearm Stretch") - M(e1["pose"], "Right Forearm Stretch")) > 0.1f,
                $"hand IK bends arm (Right Arm Down-Up {M(e1["pose"], "Right Arm Down-Up"):0.##} → {M(e2["pose"], "Right Arm Down-Up"):0.##}, Forearm Stretch {M(e1["pose"], "Right Forearm Stretch"):0.##} → {M(e2["pose"], "Right Forearm Stretch"):0.##})");
            Check(Math.Abs(M(e2["pose"], "Left Arm Down-Up") - M(e1["pose"], "Left Arm Down-Up")) < 0.01f, "IK keeps earlier bone edit");
            var e3 = Bone(JNode.Obj().Set("bone", "Hips").Set("axis", JNode.FloatArray(new[] { 0f, 1f, 0f })).Set("angle", 30f), "pose_b4_hips.png");
            Check(e3["pose"].Has("bodyRotation"), "hips rotation → bodyRotation");
            // 대칭: Host가 뒤집은 머슬을 렌더러가 적용한 뒤 다시 읽으면 왼쪽에 준 값이 오른쪽에 있어야 한다 (Unity 머슬은 양쪽 같은 값 = 대칭)
            var rec3 = PoseValue.FromViewport(e3["pose"], rest)!;
            Pose(PoseValue.Mirrored(rec3, PoseValue.Symmetry.Flip), "pose_b5_flip.png");
            var e4 = Bone(JNode.Obj().Set("bone", "RightUpperArm").Set("axis", JNode.FloatArray(new[] { 0f, 0f, 1f })).Set("angle", 0f), "pose_b5_flip_read.png");
            Check(Math.Abs(M(e4["pose"], "Right Arm Down-Up") - M(rec3, "Left Arm Down-Up")) < 0.02f && Math.Abs(M(e4["pose"], "Left Arm Down-Up") - M(rec3, "Right Arm Down-Up")) < 0.02f,
                $"flip swaps arms in renderer (L {M(rec3, "Left Arm Down-Up"):0.###} → R {M(e4["pose"], "Right Arm Down-Up"):0.###})");
            var q0 = rec3["bodyRotation"].ToFloatArray(); var q1 = e4["pose"]["bodyRotation"].ToFloatArray();
            Check(q1.Length == 4 && Math.Abs(q1[1] + q0[1]) < 0.02f, $"flip mirrors body rotation (y {q0[1]:0.###} → {(q1.Length == 4 ? q1[1] : 0):0.###})");
            Check(Diff(P("pose_b4_hips.png"), P("pose_b5_flip.png")) > 0.005, "flip changed image");
            ch.Send(JNode.Obj().Set("t", Msg.PoseBone).Set("bone", "LeftLowerArm").Set("select", true));
            Check(wait(Msg.PoseSelected, 5000)["bone"].AsString == "LeftLowerArm", "select bone → poseSelected");
            // 회전 고리: 본 로컬 축마다 25° 돌리면 머슬이 바뀌는지 (어느 머슬이 가장 바뀌었는지 찍는다 — 축과 머슬의 짝은 리그마다 다르다)
            Pose(JNode.Null, "pose_r0_rest.png");
            var prevPose = PoseValue.Make(rest);
            foreach (var ax in new[] { "x", "y", "z" })
            {
                var er = Bone(JNode.Obj().Set("bone", "LeftUpperArm").Set("ring", ax).Set("angle", 25f), $"pose_r_{ax}.png");
                var before = PoseValue.MusclesOf(prevPose); var after = PoseValue.MusclesOf(er["pose"]);
                var top = after.Select(kv => (kv.Key, d: Math.Abs(kv.Value - (before.TryGetValue(kv.Key, out var b) ? b : 0)))).OrderByDescending(x => x.d).First();
                Console.WriteLine($"   ring {ax}: {top.Key} {top.d:0.###}");
                Check(top.d > 0.03f && top.Key.StartsWith("Left "), $"ring {ax} rotates left upper arm ({top.Key} {top.d:0.###})");
                prevPose = er["pose"];
            }

            // 손 모양: 쉬는 자세에서 손 모양마다 상반신을 찍어 편 손과 다른지 (모양은 사진으로 눈으로 본다)
            // 팔을 앞으로 모아 손이 상반신 사진에 들어오게
            var restPose = PoseValue.Make(rest);
            void Hand(JNode pose, string file)
            {
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose));
                Thread.Sleep(200);
                ch.Send(Msg.Make(Msg.SetCamera).Set("axis", "+y"));   // 위에서 (손등·손가락 벌림이 보이게)
                Thread.Sleep(700);
                ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "RightHand"));
                Thread.Sleep(300);
                Capture(ch, wait, P(file), false);
            }
            Hand(restPose, "pose_h0_open.png");
            foreach (var (key, _, _) in HandPose.Shapes)
            {
                if (key == "open") continue;
                Hand(HandPose.WithShape(restPose, "Both", key, rest), $"pose_h_{key}.png");
                Check(Diff(P("pose_h0_open.png"), P($"pose_h_{key}.png")) > 0.0005, $"hand shape {key} changed image");
            }
            var half = HandPose.WithGrip(restPose, "Left", 0.5f, rest);
            Check(Math.Abs(HandPose.Grip(half, "Left", rest) - 0.5f) < 0.01f && HandPose.Grip(half, "Right", rest) < 0.01f, "grip one hand");
            Hand(HandPose.WithGrip(restPose, "Right", 0.5f, rest), "pose_h_grip_half.png");

            // 바닥 맞추기: 다리를 앞으로 편 채 앉으면 끄면 공중에 뜨고, 켜면 가장 낮은 곳이 불러온 자세와 같은 높이. 한 다리만 들면 거의 그대로
            (float rest, float now) Floor(JNode pose, bool fit, string file)
            {
                ch.Send(Msg.Make(Msg.SetView).Set("floorFit", fit));
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose).Set("report", true));
                var a = wait(Msg.PoseApplied, 5000);
                Console.WriteLine($"   floor {file}: rest {a["floorRest"].AsFloat:0.###} now {a["floorNow"].AsFloat:0.###} ({a["floorPart"].AsString})");
                Thread.Sleep(200);
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "side"));
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
                Thread.Sleep(200);
                Capture(ch, wait, P(file), false);
                return (a["floorRest"].AsFloat, a["floorNow"].AsFloat);
            }
            var sitD = new Dictionary<string, float>(rest) { ["Left Upper Leg Front-Back"] = -1f, ["Right Upper Leg Front-Back"] = -1f, ["Left Lower Leg Stretch"] = 1f, ["Right Lower Leg Stretch"] = 1f };
            var sitP = PoseValue.Make(sitD);
            var off = Floor(sitP, false, "pose_f1_sit_off.png");
            var on = Floor(sitP, true, "pose_f2_sit_on.png");
            Check(off.now - off.rest > 0.15f, $"sitting without floor fit floats ({(off.now - off.rest) * 100:0.#} cm)");
            Check(Math.Abs(on.now - on.rest) < 0.002f, $"floor fit puts sitting pose on the floor ({(on.now - on.rest) * 1000:0.#} mm)");
            // 한 다리를 들면 Unity는 몸 중심(bodyPosition)을 그대로 두어 선 발이 바닥 밑으로 내려간다 → 켜면 선 발이 바닥에
            var oneLeg = PoseValue.Make(new Dictionary<string, float>(rest) { ["Left Upper Leg Front-Back"] = -1f, ["Left Lower Leg Stretch"] = -1f });
            var ol = Floor(oneLeg, false, "pose_f3_oneleg_off.png");
            var ol2 = Floor(oneLeg, true, "pose_f4_oneleg_on.png");
            Console.WriteLine($"   one leg up without floor fit: lowest {(ol.now - ol.rest) * 1000:0.#} mm from floor");
            Check(Math.Abs(ol2.now - ol2.rest) < 0.002f, $"floor fit keeps the standing foot on the floor ({(ol2.now - ol2.rest) * 1000:0.#} mm)");
            var restFit = Floor(JNode.Null, true, "pose_f0_rest.png");
            Check(Math.Abs(restFit.now - restFit.rest) < 0.001f, $"rest pose lowest point = rest ({(restFit.now - restFit.rest) * 1000:0.##} mm)");
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front"));
            Pose(JNode.Null, "pose_99_back.png");
            double back = Diff(P("pose_00_rest.png"), P("pose_99_back.png"));
            Check(back < 0.001, $"pose null → back to loaded pose ({back:P3} pixels changed)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        end:
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 콜라이더 시험: 다리를 앞뒤로 벌리고 무릎을 굽힌 자세에서, 치마처럼 늘어진 PhysBone이 콜라이더(원본 + 다리 뚫림 막기)를
    /// 얼마나 파고드는지 잰다. 다리 뚫림 막기를 켜면 줄어야 하고, 쉬는 자세에서는 거의 없어야 한다. 옆·앞에서 찍어 눈으로도 본다.
    /// </summary>
    /// <summary>
    /// PhysBone 떨림·무게감: 치마가 허벅지에 얹히는 자세(앉기)와 쉬는 자세에서 가만히 두고 관절 떨림(프레임 사이 가속)을 잰다.
    /// 예전 방식(밀어낸 만큼이 속도로 남음)과 비교해 줄어야 한다. 무게감 1 / 2.5 / 5로 흔들기 중간을 찍어 눈으로 본다.
    /// </summary>
    static int FeelTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        (float mean, float max) J(string what)
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true));
            var r = wait(Msg.PhysBoneStats, 10000);
            Console.WriteLine($"   {what}: jitter mean {r["jitter"].AsDouble:0.000} mm/frame², max {r["jitterMax"].AsDouble:0.000} ({r["jitterWorst"].AsString}), penetrating {r["penetrations"].AsInt}/{r["bones"].AsInt}");
            return ((float)r["jitter"].AsDouble, (float)r["jitterMax"].AsDouble);
        }
        var sit = new Dictionary<string, float>
        {
            ["Left Upper Leg Front-Back"] = 1f, ["Right Upper Leg Front-Back"] = 1f, ["Left Lower Leg Stretch"] = -0.2f, ["Right Lower Leg Stretch"] = -0.2f,
            ["Left Arm Down-Up"] = -0.6f, ["Right Arm Down-Up"] = -0.6f,
        };
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("legGuard", true));
            foreach (var (name, pose) in new[] { ("rest", (Dictionary<string, float>?)null), ("sit", sit) })
            {
                var res = new Dictionary<bool, (float mean, float max)>();
                foreach (var inel in new[] { false, true })
                {
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("inelastic", inel).Set("smooth", inel).Set("weight", double.TryParse(Environment.GetEnvironmentVariable("E2E_WEIGHT"), out var ew) ? ew : 4).Set("reset", true));
                    ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose == null ? JNode.Null : PoseValue.Make(pose)));
                    Thread.Sleep(5000);  // 자리 잡기 + 떨림 평균 (약 1초 창)
                    res[inel] = J($"{name} {(inel ? "new (contact settles + smoothing)" : "old")}");
                }
                // 0.05 mm/프레임² 아래는 눈에 안 보이는 잔떨림 (큰 시험 장면 쉬는 자세는 예전 방식도 0.001이라 비율만으로는 잡음에 걸린다)
                Check(res[true].mean <= res[false].mean * 1.05f + 0.005f, $"{name}: mean jitter not worse ({res[false].mean:0.000} → {res[true].mean:0.000})");
                Check(res[true].max <= res[false].max * 1.05f + 0.05f, $"{name}: max jitter not worse ({res[false].max:0.000} → {res[true].max:0.000})");
            }
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            foreach (var w in new[] { 1.5, 4.0, 10.0 })
            {
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("inelastic", true).Set("weight", w).Set("reset", true));
                Thread.Sleep(1500);
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", "turn"));
                foreach (var (t, k) in new[] { (550, 1), (500, 2), (600, 3) }) { Thread.Sleep(t); Capture(ch, wait, P($"feel_w{w:0.0}_{k}.png"), false); }
                Thread.Sleep(2500);
            }
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// VR처럼 빠른 움직임 (2026-10-23): 90 fps에서 4 m/s로 걷다 멈추기·30° 스냅 회전·방 크기 1.5배 동안 관절이 쉬는 자리에서 벗어난 거리(cm)를
    /// 잰다. 일정하게 걸으면 몸을 따라가고, 멈추면 쉬는 자세로 돌아오고, 스냅 회전·크기 바꾸기에 휘둘리지 않는지 본다.
    /// </summary>
    static int VrMoveTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        (double mean, double max) S()
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true));
            var r = wait(Msg.PhysBoneStats, 10000);
            return (r["swing"].AsDouble, r["swingMax"].AsDouble);
        }
        // 움직임마다 (재는 때 ms, 이름): 시작부터 잰 시각
        var plan = new (string motion, int total, (int at, string what)[] marks)[]
        {
            ("vrwalk", 3600, new[] { (400, "walking"), (600, "walking"), (900, "stop+0.1s"), (1100, "stop+0.3s"), (2300, "stop+1.5s") }),
            ("snap", 2600, new[] { (100, "snap 30°+0.1s"), (300, "+0.3s"), (700, "snap 60°+0.1s"), (1300, "snap 30°+0.1s"), (2500, "back 0°+0.7s") }),
            ("resize", 3000, new[] { (100, "×1.5 +0.1s"), (600, "×1.5 +0.6s"), (1300, "×1 +0.1s"), (2900, "×1 +1.7s") }),
        };
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var pb = wait(Msg.SceneLoaded, 60000)["physBones"];
            Check(pb["chains"].AsInt > 0, $"physBones: chains {pb["chains"].AsInt}, bones {pb["bones"].AsInt}");
            // 시간을 멈추고 90 fps(헤드셋 주기) 프레임을 바로 돌린다 — 기다리지 않고 매번 같은 결과 (2026-11-12, 예전에는 실제 시간으로 기다리며 예전 방식과 견줬다)
            ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true));
            void Run(double seconds, string motion = null)
            {
                var m = Msg.Make(Msg.SetPhysBones).Set("advance", seconds).Set("advanceFps", 90);
                if (motion != null) m.Set("motion", motion);
                ch.Send(m);
            }
            var n = new Dictionary<string, (double mean, double max)>();
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("reset", true));
            Run(3);
            n["rest"] = S();
            Console.WriteLine($"   rest: mean {n["rest"].mean:0.00} cm, max {n["rest"].max:0.00} cm");
            foreach (var (motion, total, marks) in plan)
            {
                int now = 0;
                foreach (var (at, what) in marks)
                {
                    Run((at - now) / 1000.0, now == 0 ? motion : null); now = at;
                    var v = n[$"{motion} {what}"] = S();
                    Console.WriteLine($"   {motion} {what}: mean {v.mean:0.00} cm, max {v.max:0.00} cm");
                }
                Run((total - now) / 1000.0 + 1.5);
            }
            n["after"] = S();
            Console.WriteLine($"   after all: mean {n["after"].mean:0.00} cm, max {n["after"].max:0.00} cm");
            Check(n["vrwalk walking"].mean <= n["rest"].mean + 2.5, $"walking 4 m/s: mean swing {n["vrwalk walking"].mean:0.00} cm (일정한 속도면 몸을 따라감, 쉬는 자세 {n["rest"].mean:0.00})");
            Check(n["vrwalk stop+1.5s"].mean <= n["rest"].mean + 0.5, $"1.5 s after stop ≈ rest ({n["vrwalk stop+1.5s"].mean:0.00} vs {n["rest"].mean:0.00} cm)");
            Check(n["snap snap 30°+0.1s"].max <= n["rest"].max + 3, $"snap turn: max swing {n["snap snap 30°+0.1s"].max:0.00} cm (쉬는 자세 {n["rest"].max:0.00})");
            Check(n["resize ×1.5 +0.1s"].max <= n["rest"].max * 1.5 + 3, $"resize ×1.5: max swing {n["resize ×1.5 +0.1s"].max:0.00} cm (쉬는 자세 {n["rest"].max:0.00})");
            Check(Math.Abs(n["after"].mean - n["rest"].mean) < 0.3, $"after motions → rest ({n["after"].mean:0.00} vs {n["rest"].mean:0.00} cm)");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static int CollideTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        (int pen, int bones) Stats()
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true));
            var r = wait(Msg.PhysBoneStats, 10000);
            foreach (var d in r["detail"].Items.Take(30)) Console.WriteLine("      " + d.AsString);
            return (r["penetrations"].AsInt, r["bones"].AsInt);
        }
        var stride = new Dictionary<string, float>
        {
            ["Left Upper Leg Front-Back"] = 1f, ["Right Upper Leg Front-Back"] = -0.2f,
            ["Left Lower Leg Stretch"] = 0.2f, ["Right Lower Leg Stretch"] = 1f,
            ["Left Arm Down-Up"] = -0.6f, ["Right Arm Down-Up"] = -0.6f, ["Left Arm Front-Back"] = 0.3f, ["Right Arm Front-Back"] = 0.3f,
            ["Left Forearm Stretch"] = 1f, ["Right Forearm Stretch"] = 1f,
        };
        var spread = new Dictionary<string, float>(stride) { ["Left Upper Leg Front-Back"] = 0.6f, ["Right Upper Leg Front-Back"] = 0.6f, ["Left Upper Leg In-Out"] = 1f, ["Right Upper Leg In-Out"] = 1f, ["Left Lower Leg Stretch"] = 1f };
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            var pb = loaded["physBones"];
            Check(pb["chains"].AsInt > 0, $"physBones: chains {pb["chains"].AsInt}, leg guard chains {pb["legGuard"].AsInt}");
            ch.Send(Msg.Make(Msg.SetView).Set("physBoneGizmos", true));
            Thread.Sleep(2500);
            if (Environment.GetEnvironmentVariable("E2E_DESCRIBE") is string desc)
            {
                foreach (var lg in new[] { false, true })
                {
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("legGuard", lg)); Thread.Sleep(2500);
                    var arr = JNode.Arr(); foreach (var x in desc.Split(',')) arr.Add(x);
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true).Set("describe", arr));
                    Console.WriteLine($"   -- legGuard {lg}");
                    foreach (var l in wait(Msg.PhysBoneStats, 10000)["detail"].Items) Console.WriteLine("      " + l.AsString);
                }
            }
            var rest = Stats();
            Console.WriteLine($"   rest pose: {rest.pen}/{rest.bones} bones penetrating");
        var sit = new Dictionary<string, float>(stride) { ["Left Upper Leg Front-Back"] = 1f, ["Right Upper Leg Front-Back"] = 1f, ["Left Lower Leg Stretch"] = -0.2f, ["Right Lower Leg Stretch"] = -0.2f };
            foreach (var (name, pose) in new[] { ("stride", stride), ("spread", spread), ("sit", sit) })
            {
                var res = new Dictionary<bool, int>();
                foreach (var guard in new[] { false, true })
                {
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("legGuard", guard).Set("reset", true));
                    ch.Send(Msg.Make(Msg.SetPose).Set("pose", PoseValue.Make(pose)));
                    Thread.Sleep(3000);
                    var st = Stats();
                    res[guard] = st.pen;
                    Console.WriteLine($"   {name} legGuard {(guard ? "on " : "off")}: {st.pen}/{st.bones} bones penetrating");
                    foreach (var cam in new[] { "side", "front" })
                    {
                        ch.Send(Msg.Make(Msg.SetCamera).Set("preset", cam));
                        Thread.Sleep(300);
                        Capture(ch, wait, P($"col_{name}_{cam}_{(guard ? "on" : "off")}.png"), false);
                    }
                }
                Check(res[true] <= res[false], $"{name}: leg guard does not add penetration ({res[false]} → {res[true]})");
                Check(res[true] <= 3, $"{name}: few penetrations with leg guard ({res[true]})");
            }
            ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
            Thread.Sleep(2500);
            var back = Stats();
            Check(back.pen <= Math.Max(3, rest.pen), $"back to rest: {back.pen} penetrating");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 메시 편집 (PLAN 3.9): 정점 합치기(겹친 정점만)는 화면을 거의 바꾸지 않고 정점 수를 줄이는지, 큰 거리 합치기·삭제·이동은 화면을 바꾸는지,
    /// 편집을 비우면 처음 화면으로 돌아오는지, 뷰포트 편집 모드의 전체 선택 → 삭제가 연산으로 돌아오는지. 기록은 Host 코어(Document·MeshEdits)로.
    /// </summary>
    static int MeshTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var me = new MeshEdits(doc);
            void SendEdits() { foreach (var id in me.MeshIds()) ch.Send(RenderSync.ToMessage(Addr.MeshEdit(id), doc.Get(Addr.MeshEdit(id)))!); Thread.Sleep(400); }
            (int v, int t) Totals() { int v = 0, t = 0; foreach (var id in me.MeshIds()) { var c = me.Counts(id); v += c.verts; t += c.tris; } return (v, t); }

            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 120000);
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Thread.Sleep(500);
            Capture(ch, wait, P("mesh_01_base.png"), false);
            var (v0, t0) = Totals();

            var sw = Stopwatch.StartNew();
            var (dv, dt) = me.WeldAll(me.MeshIds(), 0.0001f, true, true);
            Console.WriteLine($"   weld 0.1 mm: {sw.ElapsedMilliseconds} ms (Host 계산)");
            SendEdits();
            Capture(ch, wait, P("mesh_02_weld_small.png"), false);
            double small = Diff(P("mesh_01_base.png"), P("mesh_02_weld_small.png"));
            Check(dv > 0, $"weld 0.1 mm keeps seams/shapes: vertices {v0:N0} → {v0 - dv:N0} (−{dv:N0}), triangles −{dt:N0}");
            Check(small < 0.005, $"weld 0.1 mm → image almost unchanged ({small:P3})");

            doc.ResetWhere(a => a.Kind == AddrKind.MeshEdit, "reset");
            var (dv2, dt2) = me.WeldAll(me.MeshIds(), 0.01f, false, false);
            SendEdits();
            Capture(ch, wait, P("mesh_03_weld_10mm.png"), false);
            Check(dt2 > 0, $"weld 10 mm (seams·shapes off): vertices −{dv2:N0}, triangles {t0:N0} → {t0 - dt2:N0}");
            Console.WriteLine($"   weld 10 mm image change: {Diff(P("mesh_01_base.png"), P("mesh_03_weld_10mm.png")):P2}");

            doc.ResetWhere(a => a.Kind == AddrKind.MeshEdit, "reset");
            SendEdits();
            Capture(ch, wait, P("mesh_04_reset.png"), false);
            double back = Diff(P("mesh_01_base.png"), P("mesh_04_reset.png"));
            Check(back < 0.001, $"empty edit list → original mesh ({back:P3})");

            // 정점 줄이기 슬라이더 (decimate 50%): Host와 렌더러(Mono)가 같은 결과인지, 화면은 비슷한지
            var sw2 = Stopwatch.StartNew();
            var skip = (Environment.GetEnvironmentVariable("E2E_DEC_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            me.SetDecimate(me.MeshIds().Where(id => !skip.Contains(doc.Original.FindMesh(id)!.Name)), 0.5f, true);
            var (v50, t50) = Totals();
            Console.WriteLine($"   decimate 50%: Host {sw2.ElapsedMilliseconds} ms, vertices {v0:N0} → {v50:N0}, triangles {t0:N0} → {t50:N0}");
            Check(t50 <= t0 * 0.52 && t50 >= t0 * 0.3, $"decimate 50% halves triangles ({t50:N0} / {t0:N0})");
            var want = me.MeshIds().Where(id => doc.Get(Addr.MeshEdit(id)).Count > 0).ToList();
            foreach (var id in want) ch.Send(RenderSync.ToMessage(Addr.MeshEdit(id), doc.Get(Addr.MeshEdit(id)))!);
            int same = 0, got = 0;
            var sw3 = Stopwatch.StartNew();
            while (got < want.Count)
            {
                var rb = wait(Msg.MeshRebuilt, 120000);
                got++;
                var c = me.Counts(rb["mesh"].AsString);
                if (c.verts == rb["vertices"].AsInt && c.tris == rb["triangles"].AsInt) same++;
                else Console.WriteLine($"   differs: {rb["mesh"].AsString} Host {c.verts}/{c.tris} renderer {rb["vertices"].AsInt}/{rb["triangles"].AsInt}");
            }
            Console.WriteLine($"   renderer rebuilt {got} meshes in {sw3.ElapsedMilliseconds} ms (background)");
            Check(same == want.Count, $"renderer = Host counts ({same}/{want.Count})");
            Thread.Sleep(300);
            Capture(ch, wait, P("mesh_04b_decimate50.png"), false);
            double dec = Diff(P("mesh_01_base.png"), P("mesh_04b_decimate50.png"));
            Console.WriteLine($"   decimate 50% image change: {dec:P2}");
            Check(dec < 0.15, $"decimate 50% keeps the look ({dec:P2} pixels changed)");
            // 슬라이더 경로 (삼각형 수): 고른 메시들을 합계 3개까지, 그리고 합계 1,000개로 나눠 줄이기
            doc.ResetWhere(a => a.Kind == AddrKind.MeshEdit, "reset");
            SendEdits();
            var pick = me.MeshIds().OrderByDescending(id => doc.Original.FindMesh(id)!.VertexCount).Take(3).ToList();
            foreach (var total in new[] { 3, 1000 })
            {
                var targets = me.Distribute(pick, total);
                me.SetDecimate(targets, true);
                foreach (var id in pick) ch.Send(RenderSync.ToMessage(Addr.MeshEdit(id), doc.Get(Addr.MeshEdit(id)))!);
                // 앞서 보낸 편집의 늦은 알림이 섞일 수 있어, 메시마다 Host 수와 같은 알림이 올 때까지 읽는다
                var done = new Dictionary<string, int>();
                var until = DateTime.UtcNow.AddMinutes(3);
                while (done.Count < pick.Count && DateTime.UtcNow < until)
                {
                    var rb = wait(Msg.MeshRebuilt, 120000);
                    var rid = rb["mesh"].AsString;
                    if (pick.Contains(rid) && rb["triangles"].AsInt == me.Counts(rid).tris) done[rid] = rb["triangles"].AsInt;
                }
                int sumR = done.Values.Sum(), sumH = pick.Sum(id => me.Counts(id).tris);
                Console.WriteLine($"   slider total {total}: targets {string.Join("+", targets.Values)} → renderer {sumR}, Host {sumH}");
                Check(done.Count == pick.Count && sumR == sumH && sumR <= total + 2 * pick.Count, $"slider to {total} triangles (got {sumR}, renderer matched {done.Count}/{pick.Count})");
            }
            Capture(ch, wait, P("mesh_04c_slider1000.png"), false);
            doc.ResetWhere(a => a.Kind == AddrKind.MeshEdit, "reset");
            SendEdits();

            // 뷰포트 편집 모드: 가장 큰 보이는 메시를 골라 전체 선택 → 삭제
            var target = doc.Original.Renderers.Where(r => r.Enabled && doc.Original.FindMesh(r.Mesh) != null && (r.Node < 0 || doc.Original.Nodes[r.Node].Active))
                .OrderByDescending(r => doc.Original.FindMesh(r.Mesh)!.VertexCount).First();
            ch.Send(Msg.Make(Msg.MeshTool).Set("enabled", true).Set("renderer", target.Id));
            var sel0 = wait(Msg.MeshSelection, 30000);
            ch.Send(Msg.Make(Msg.MeshTool).Set("action", "selectAll"));
            var sel = wait(Msg.MeshSelection, 30000);
            Check(sel["count"].AsInt == sel["vertices"].AsInt && sel["count"].AsInt > 0, $"meshTool selectAll: {sel["count"].AsInt:N0} / {sel["vertices"].AsInt:N0} ({doc.Original.Nodes[target.Node].Name})");
            ch.Send(Msg.Make(Msg.MeshTool).Set("action", "delete"));
            var edited = wait(Msg.MeshEdited, 30000);
            Check(edited["op"]["op"].AsString == "delete" && edited["op"]["sel"].Count == sel["count"].AsInt, $"meshEdited: {edited["label"].AsString}");
            me.Append(edited["mesh"].AsString, edited["op"], edited["label"].AsString);
            Check(me.Counts(edited["mesh"].AsString).tris == 0, "delete all → no triangles");
            SendEdits();
            var after = wait(Msg.MeshSelection, 30000);
            Check(after["vertices"].AsInt == 0 && after["count"].AsInt == 0, "renderer rebuilt mesh, selection cleared");
            Capture(ch, wait, P("mesh_05_deleted.png"), false);
            Check(Diff(P("mesh_01_base.png"), P("mesh_05_deleted.png")) > 0.01, "deleted mesh disappears");

            // 이동: 원본 메시의 정점 전부를 위로 5 cm (메시 공간) → 화면이 바뀜, Undo로 원래대로
            doc.Undo();
            var mid = target.Mesh; var n = doc.Original.FindMesh(mid)!.VertexCount;
            var all = JNode.Arr(); for (int i = 0; i < n; i++) all.Add(i);
            me.Append(mid, JNode.Obj().Set("op", "move").Set("sel", all).Set("delta", JNode.FloatArray(new[] { 0f, 0.05f, 0f })), "move");
            SendEdits();
            Capture(ch, wait, P("mesh_06_moved.png"), false);
            Check(Diff(P("mesh_01_base.png"), P("mesh_06_moved.png")) > 0.005, "move op changes the image");
            doc.Undo();
            SendEdits();
            Capture(ch, wait, P("mesh_07_undo.png"), false);
            Check(Diff(P("mesh_01_base.png"), P("mesh_07_undo.png")) < 0.001, "undo → original");
            ch.Send(Msg.Make(Msg.MeshTool).Set("enabled", false));
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>정점 줄이기 단계별 사진 (설명용): 원본과 전체 메시를 80·50·25·10·3%로 줄인 결과를 전신(셰이딩)·얼굴(와이어프레임)로 찍는다.</summary>
    static int StepsTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var me = new MeshEdits(doc);
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 120000);
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.SetView).Set("gizmo", false).Set("grid", false).Set("background", "gray"));
            var ids = me.MeshIds().ToList();
            int full = ids.Sum(me.BaseTris);
            var lines = new List<string>();
            foreach (var pct in new[] { 100, 80, 50, 25, 10, 3 })
            {
                int total = (int)Math.Round(full * pct / 100.0);
                me.SetDecimate(me.Distribute(ids, total), true);
                var changed = ids.Where(id => true).ToList();
                foreach (var id in changed) ch.Send(RenderSync.ToMessage(Addr.MeshEdit(id), doc.Get(Addr.MeshEdit(id)))!);
                // 줄인 메시마다 Host 수와 같은 알림이 올 때까지 (정점 줄이기는 렌더러가 백그라운드에서 계산)
                var want = ids.Where(id => doc.Get(Addr.MeshEdit(id)).Count > 0).ToHashSet();
                var done = new HashSet<string>();
                var until = DateTime.UtcNow.AddMinutes(3);
                while (done.Count < want.Count && DateTime.UtcNow < until)
                {
                    JNode rb;
                    try { rb = wait(Msg.MeshRebuilt, 20000); } catch (TimeoutException) { break; }
                    var rid = rb["mesh"].AsString;
                    if (want.Contains(rid) && rb["triangles"].AsInt == me.Counts(rid).tris) done.Add(rid);
                }
                Thread.Sleep(500);
                int v = ids.Sum(id => me.Counts(id).verts), t = ids.Sum(id => me.Counts(id).tris);
                ch.Send(Msg.Make(Msg.SetView).Set("draw", "shaded"));
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full")); Thread.Sleep(300);
                Capture(ch, wait, P($"step_{pct:000}_full.png"), false);
                ch.Send(Msg.Make(Msg.SetView).Set("draw", "shadedWire"));
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face")); Thread.Sleep(300);
                Capture(ch, wait, P($"step_{pct:000}_face.png"), false);
                var line = $"{pct}	{v}	{t}	{done.Count}/{want.Count}";
                lines.Add(line);
                Console.WriteLine($"   {pct}%: vertices {v:N0}, triangles {t:N0} (renderer {done.Count}/{want.Count})");
            }
            File.WriteAllLines(P("steps.tsv"), lines);
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>배경(왼쪽 위 픽셀 색)과 8/255 넘게 다른 픽셀의 상자.</summary>
    static (int x0, int y0, int x1, int y1) Bbox(string path)
    {
        var (w, h, p) = DecodePng(path);
        int ch = p.Length / (w * h), x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * ch; bool fg = false;
                for (int c = 0; c < ch; c++) if (Math.Abs(p[i + c] - p[c]) > 8) fg = true;
                if (fg) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            }
        return (x0, y0, x1, y1);
    }

    /// 다른 픽셀 비율 (채널 차이 > 8/255)
    static double Diff(string a, string b)
    {
        var (wa, ha, pa) = DecodePng(a); var (wb, hb, pb) = DecodePng(b);
        if (wa != wb || ha != hb) return 1;
        int n = 0, px = wa * ha, ch = pa.Length / px;
        for (int i = 0; i < px; i++)
            for (int c = 0; c < ch; c++) if (Math.Abs(pa[i * ch + c] - pb[i * ch + c]) > 8) { n++; break; }
        return n / (double)px;
    }

    /// <summary>
    /// 입 맞추기 시험: 입 편집 블렌드쉐이프(기본 mouth_straight 100)를 켠 채 입 벌리는 블렌드쉐이프를 하나씩 100으로 캡처 — 고치기 전과 뒤.
    ///   E2E.dll &lt;exe&gt; &lt;ir&gt; &lt;out&gt; mouth [편집=mouth_straight] [대상,대상,…]
    /// </summary>
    static int MouthTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string[] a)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(300); Capture(ch, wait, P(file), false); }
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            var r = doc.Original.Renderers.Where(x => doc.Original.FindMesh(x.Mesh) != null).OrderByDescending(x => doc.Original.FindMesh(x.Mesh)!.BlendShapes.Count).First();
            var mesh = doc.Original.FindMesh(r.Mesh)!;
            var names = mesh.BlendShapes.Select(b => b.Name).ToList();
            var edit = a.Length > 0 ? a[0] : "mouth_straight";
            var targets = a.Length > 1 ? a[1].Split(',').ToList() : names.Where(n => MeshOps.IsMouthOpenName(n)).ToList();
            Console.WriteLine($"   mesh {mesh.Name}: edit {edit}, targets {targets.Count}: {string.Join(", ", targets.Take(30))}");
            ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Head").Set("size", 0.12));
            Shot("mouth_00_rest.png");
            doc.Set(Addr.BlendShape(r.Id, edit), 100);
            Shot("mouth_01_edit.png");
            var show = (a.Length > 2 ? a[2].Split(',') : new[] { "vrc.v_aa", "mouth_A_1", "vrc.v_oh", "あ" }).Where(names.Contains).ToList();
            foreach (var t in show) { doc.Set(Addr.BlendShape(r.Id, t), 100); Shot($"mouth_before_{Safe(t)}.png"); doc.Set(Addr.BlendShape(r.Id, t), 0); }
            // 화면(블렌드쉐이프 페이지)과 같은 고르기: 대상 후보 중 기본 체크, 편집은 겹침 20% 이상
            var md = new MeshEdits(doc).Original(r.Mesh)!;
            var cand = BlinkFixPlan.MouthTargets(doc, r, md);
            var picked = a.Length > 1 ? targets : cand.Where(MeshOps.IsMouthOpenName).ToList();
            var srcs = BlinkFixPlan.Sources(doc, r, md, cand);
            Console.WriteLine($"   후보 {cand.Count}개, 기본 대상 {picked.Count}개: {string.Join(", ", picked)}");
            Console.WriteLine($"   입 편집: {string.Join(", ", srcs.Select(s => $"{s.Name} {s.Weight:0} ({s.Overlap:P0})"))}");
            Check(!cand.Contains(edit) && srcs.Any(s => s.Name == edit && s.Overlap >= 0.2f), $"{edit}는 대상이 아니라 입 편집으로 골라짐");
            BlinkFixPlan.Set(doc, r.Mesh, MeshOps.MakeMouthFix(picked, srcs.Where(s => s.Overlap >= 0.2f).Select(s => (s.Name, s.Weight))), "입 맞추기", MeshOps.MouthFix);
            wait(Msg.MeshRebuilt, 60000);
            foreach (var t in show) { doc.Set(Addr.BlendShape(r.Id, t), 100); Shot($"mouth_after_{Safe(t)}.png"); doc.Set(Addr.BlendShape(r.Id, t), 0); }
            foreach (var t in show) { doc.Set(Addr.BlendShape(r.Id, t), 50); Shot($"mouth_after50_{Safe(t)}.png"); doc.Set(Addr.BlendShape(r.Id, t), 0); }
            Shot("mouth_after_rest.png");
            Check(SameImage(P("mouth_01_edit.png"), P("mouth_after_rest.png")), "입 맞추기 뒤에도 다문 입(편집 상태)은 그대로");
            Console.WriteLine(_fail == 0 ? "PASS" : $"FAIL ({_fail})");
            return _fail == 0 ? 0 : 1;
        }
        finally { try { ch.Send(Msg.Make(Msg.Quit)); proc.WaitForExit(10000); } catch { } }

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_'));
    }

    /// <summary>
    /// 다문 입 선 다듬기 시험: mouth_straight 100을 입 가까이서 찍고, 다듬기(shapeMove)를 적용해 다시 찍는다. 원래 입·벌린 입(입 맞추기)도 확인.
    ///   E2E.dll &lt;exe&gt; &lt;ir&gt; &lt;out&gt; mouthline [끝 두께=0.3] [가운데=1.3] [끝 올리기 mm=0.8] [편집=mouth_straight]
    /// </summary>
    /// <summary>
    /// 색 굽기 (M3): 메인 색·HSVG를 바꾼 화면(실시간) ≈ 구워서 텍스처를 바꾸고 값을 되돌린 화면. 원본 화면과는 달라야 한다.
    /// 실행 취소 한 번이면 굽기 전(실시간 조정)으로 돌아간다.
    /// </summary>
    static int BakeTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(400); ch.Send(Msg.Make(Msg.Capture).Set("path", P(file)).Set("width", 600).Set("height", 600).Set("transparent", false).Set("supersample", 1)); Check(wait(Msg.CaptureDone, 60000)["ok"].AsBool, "capture " + file); }
        double Diff(string a, string b)
        {
            var (wa, ha, pa) = DecodePng(P(a)); var (wb, hb, pb) = DecodePng(P(b));
            if (wa != wb || ha != hb) return 999;
            long sum = 0; for (int i = 0; i < pa.Length; i++) if ((i & 3) != 3) sum += Math.Abs(pa[i] - pb[i]);
            return sum / (pa.Length * 0.75);
        }
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", Document.DefaultEnvironment()));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
            // 가장 큰 메시의 머티리얼 중 PNG 메인 텍스처가 있는 lilToon
            string? mat = null; IrTexture? main = null;
            foreach (var r in doc.Original.Renderers.OrderByDescending(x => doc.Original.FindMesh(x.Mesh)?.VertexCount ?? 0))
                foreach (var g in r.Materials)
                {
                    var t = doc.Texture(doc.Get(Addr.MatTex(g, "_MainTex")).AsStringOrNull);
                    if (mat == null && doc.Schema(g)?.Find("_MainTexHSVG") != null && t != null && t.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { mat = g; main = t; }
                }
            Check(mat != null, "lilToon 머티리얼 (PNG 메인 텍스처) " + (main?.Path ?? ""));
            if (mat == null) return 1;
            Addr Col(string n) => doc.Schema(mat)!.Find(n)!.Type == "Vector" ? Addr.MatVector(mat, n) : Addr.MatColor(mat, n);
            Shot("bake_0_original.png");
            float[] color = { 1f, 0.8f, 0.85f, 1f }, hsvg = { 0.35f, 1.3f, 0.95f, 1.1f };
            doc.Set(Col("_Color"), JNode.FloatArray(color), "색");
            doc.Set(Col("_MainTexHSVG"), JNode.FloatArray(hsvg), "HSVG");
            Shot("bake_1_live.png");
            var outFile = P("baked_main.png");
            ch.Send(Msg.Make(Msg.BakeColor).Set("out", outFile).Set("main", main!.File).Set("color", JNode.FloatArray(color)).Set("hsvg", JNode.FloatArray(hsvg)).Set("gradationStrength", 0f));
            var done = wait(Msg.BakeDone, 60000);
            Check(done["ok"].AsBool && File.Exists(outFile), $"bakeDone {done["width"].AsInt}×{done["height"].AsInt} {done["error"].AsString}");
            using (doc.Group("색 굽기"))
            {
                doc.Set(Addr.TexAsset("baked:e2e"), JNode.Obj().Set("file", outFile).Set("path", "baked_main.png").Set("like", main.Guid));
                doc.Set(Addr.MatTex(mat, "_MainTex"), JNode.From("baked:e2e"));
                doc.Set(Col("_Color"), JNode.FloatArray(new float[] { 1, 1, 1, 1 }));
                doc.Set(Col("_MainTexHSVG"), JNode.FloatArray(new float[] { 0, 1, 1, 1 }));
            }
            Shot("bake_2_baked.png");
            double live = Diff("bake_1_live.png", "bake_0_original.png"), baked = Diff("bake_2_baked.png", "bake_1_live.png");
            Console.WriteLine($"   원본↔실시간 평균 차 {live:0.00}, 실시간↔구운 것 {baked:0.00} (0~255)");
            Check(live > 3, "실시간 색 조정이 화면을 바꿈");
            Check(baked < 1.5, "구운 화면 ≈ 실시간 화면");
            var plan = UploadPlan.Build(doc, "t");
            Check(plan["textures"]["baked:e2e"]["file"].AsString == outFile && plan["textures"]["baked:e2e"]["like"].AsString == main.Guid, "올리기 계획에 구운 텍스처 파일·원본 설정");
            doc.Undo();
            Shot("bake_3_undo.png");
            Check(Diff("bake_3_undo.png", "bake_1_live.png") < 0.5, "실행 취소 한 번 = 굽기 전 (실시간 조정)");
            // 렌더러 다시 시작처럼: 장면을 다시 불러와 덮어쓰기를 모두 보내도 구운 텍스처가 보인다 (TexAsset을 먼저)
            doc.Redo();
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            sync.SendAllOverrides();
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
            Shot("bake_4_reload.png");
            Check(Diff("bake_4_reload.png", "bake_2_baked.png") < 0.5, "다시 불러와도 구운 텍스처");
            Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }
        finally { try { ch.Send(Msg.Make(Msg.Quit)); proc.WaitForExit(10000); } catch { } }
    }

    /// <summary>텍스처 합치기 (2026-11-02): 미리 보기(기록 없음) → 되돌리기 → 적용(문서) → 실행 취소 → 장면 다시 불러오기. 창과 같은 메시지 순서.</summary>
    static int MixTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(400); ch.Send(Msg.Make(Msg.Capture).Set("path", P(file)).Set("width", 600).Set("height", 600).Set("transparent", false).Set("supersample", 1)); Check(wait(Msg.CaptureDone, 60000)["ok"].AsBool, "capture " + file); }
        double Diff(string a, string b)
        {
            var (wa, ha, pa) = DecodePng(P(a)); var (wb, hb, pb) = DecodePng(P(b));
            if (wa != wb || ha != hb) return 999;
            long sum = 0; for (int i = 0; i < pa.Length; i++) if ((i & 3) != 3) sum += Math.Abs(pa[i] - pb[i]);
            return sum / (pa.Length * 0.75);
        }
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", Document.DefaultEnvironment()));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
            // 가장 큰 메시의 PNG 메인 텍스처
            string? mat = null; IrTexture? main = null;
            foreach (var r in doc.Original.Renderers.OrderByDescending(x => doc.Original.FindMesh(x.Mesh)?.VertexCount ?? 0))
                foreach (var g in r.Materials)
                {
                    var t = doc.Texture(doc.Get(Addr.MatTex(g, "_MainTex")).AsStringOrNull);
                    if (mat == null && t != null && t.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { mat = g; main = t; }
                }
            Check(mat != null, "PNG 메인 텍스처 머티리얼 " + (main?.Path ?? ""));
            if (mat == null) return 1;
            var bas = Sticker.DecodePng(File.ReadAllBytes(main!.File));
            Check(bas != null, "바탕 읽기");
            if (bas == null) return 1;
            var a = Addr.MatTex(mat, "_MainTex");
            var layer = new Rgba(4, 4);
            for (int i = 0; i < layer.Px.Length; i += 4) { layer.Px[i] = 255; layer.Px[i + 1] = 0; layer.Px[i + 2] = 200; layer.Px[i + 3] = 255; }
            Rgba Mix(MixLayer l) => TextureMix.Compose(bas, new[] { (l, (Rgba?)layer) });
            string Write(string name, Rgba img) { var f = P(name); File.WriteAllBytes(f, Sticker.EncodePng(img)); return f; }

            Shot("mix_0_original.png");
            // 미리 보기: 문서는 그대로, 키마다 새 파일
            var f1 = Write("mix_prev1.png", Mix(new MixLayer { Opacity = 0.9f }));
            doc.Preview(Addr.TexAsset("mixprev:1"), JNode.Obj().Set("file", f1).Set("path", "p").Set("like", main.Guid));
            doc.Preview(a, JNode.From("mixprev:1"));
            Shot("mix_1_preview.png");
            Check(Diff("mix_1_preview.png", "mix_0_original.png") > 3, "미리 보기가 아바타를 바꿈");
            Check(!doc.IsDirty && doc.Overrides.Count == 0, "미리 보기는 문서에 남지 않음");
            var f2 = Write("mix_prev2.png", Mix(new MixLayer { Opacity = 0.9f, Mode = "multiply" }));
            doc.Preview(Addr.TexAsset("mixprev:2"), JNode.Obj().Set("file", f2).Set("path", "p").Set("like", main.Guid));
            doc.Preview(a, JNode.From("mixprev:2"));
            Shot("mix_2_preview2.png");
            Check(Diff("mix_2_preview2.png", "mix_1_preview.png") > 1, "다시 미리 보기하면 새 그림");   // 파랑만 조금 다른 두 그림이라 화면 평균으로는 작다 (같은 그림끼리는 0.5 아래)
            // 닫기: 문서 값으로
            doc.Preview(a, doc.Get(a));
            Shot("mix_3_restored.png");
            Check(Diff("mix_3_restored.png", "mix_0_original.png") < 0.5, "닫으면 원래대로");
            // 적용
            var applied = Write("mix_applied.png", Mix(new MixLayer { Opacity = 0.9f }));
            using (doc.Group("텍스처 합치기"))
            {
                var recipe = TextureMix.RecipeToJson(main.Guid, main.File, new[] { new MixLayer { Opacity = 0.9f } });
                doc.Set(Addr.TexAsset("mix:e2e"), JNode.Obj().Set("file", applied).Set("path", "mix.png").Set("like", main.Guid).Set("mix", recipe));
                doc.Set(a, JNode.From("mix:e2e"));
            }
            Shot("mix_4_applied.png");
            Check(Diff("mix_4_applied.png", "mix_1_preview.png") < 0.5, "적용한 화면 = 미리 보기");
            var plan = UploadPlan.Build(doc, "t");
            Check(plan["textures"]["mix:e2e"]["file"].AsString == applied && plan["textures"]["mix:e2e"]["like"].AsString == main.Guid, "올리기 계획에 합친 텍스처 파일·원본 설정");
            doc.Undo();
            Shot("mix_5_undo.png");
            Check(Diff("mix_5_undo.png", "mix_0_original.png") < 0.5, "실행 취소 = 원본");
            doc.Redo();
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            sync.SendAllOverrides();
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "upper"));
            Shot("mix_6_reload.png");
            Check(Diff("mix_6_reload.png", "mix_4_applied.png") < 0.5, "다시 불러와도 합친 텍스처");
            Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }
        finally { try { ch.Send(Msg.Make(Msg.Quit)); proc.WaitForExit(10000); } catch { } }
    }

    /// <summary>스티커 만들기: 상반신을 투명 배경으로 찍어 규격 이미지로 (Host/Core/Sticker.cs). 모서리는 투명, 아바타가 칸을 채움, 용량 제한.</summary>
    static int StickerTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", Document.DefaultEnvironment()));
            foreach (var (preset, spec) in new[] { ("upper", "line"), ("face", "discordEmoji"), ("face", "twitch") })
            {
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", preset));
                Thread.Sleep(500);
                var sp = Sticker.Find(spec);
                double k = 1600.0 / Math.Max(sp.W, sp.H);
                var raw = P($"sticker_raw_{spec}.png");
                ch.Send(Msg.Make(Msg.Capture).Set("path", raw).Set("width", (int)Math.Round(sp.W * k)).Set("height", (int)Math.Round(sp.H * k)).Set("transparent", true).Set("supersample", 2));
                Check(wait(Msg.CaptureDone, 60000)["ok"].AsBool, "capture " + spec);
                var shot = Sticker.DecodePng(File.ReadAllBytes(raw));
                Check(shot != null, $"decode renderer PNG ({spec})");
                if (shot == null) continue;
                var list = Sticker.Make(shot, new Sticker.Options { Spec = spec, OutlineWidth = spec == "line" ? 6 : 3, Saturation = 1.1 });
                Check(list.Count == 1 + sp.Extra.Length, $"{spec}: {list.Count}장");
                foreach (var (size, img) in list)
                {
                    var bytes = Sticker.EncodePng(img);
                    File.WriteAllBytes(P($"sticker_{spec}_{size}.png"), bytes);
                    var bb = Sticker.OpaqueBounds(img)!.Value;
                    bool fills = bb.W >= img.W - 2 * sp.Margin * img.W / sp.W - 4 || bb.H >= img.H - 2 * sp.Margin * img.H / sp.H - 4;
                    Check(img.A(0, 0) == 0 && img.A(img.W - 1, img.H - 1) == 0, $"{spec} {size}: 모서리 투명");
                    Check(fills, $"{spec} {size}: 칸을 채움 {bb} / {img.W}×{img.H}");
                    Check(sp.MaxKB == 0 || bytes.Length <= sp.MaxKB * 1024, $"{spec} {size}: {bytes.Length / 1024.0:0.0} KB ≤ {sp.MaxKB} KB");
                    Console.WriteLine($"   {spec} {img.W}×{img.H} {bytes.Length / 1024.0:0.0} KB, 보이는 범위 {bb}");
                }
            }
            Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }
        finally { try { ch.Send(Msg.Make(Msg.Quit)); proc.WaitForExit(10000); } catch { } }
    }

    static int MouthLineTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string[] a)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(300); ch.Send(Msg.Make(Msg.Capture).Set("path", P(file)).Set("width", 800).Set("height", 400).Set("transparent", false).Set("supersample", 2)); var r = wait(Msg.CaptureDone, 60000); Check(r["ok"].AsBool, "capture " + file); }
        float F(int i, float d) => a.Length > i ? float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture) : d;
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            var r = doc.Original.Renderers.Where(x => doc.Original.FindMesh(x.Mesh) != null).OrderByDescending(x => doc.Original.FindMesh(x.Mesh)!.BlendShapes.Count).First();
            var edit = a.Length > 3 ? a[3] : "mouth_straight";
            var md = new MeshEdits(doc).Original(r.Mesh)!;
            // 입 가까이 (띠의 가운데, 앞에서)
            var probe = MeshOps.MakeMouthLine(md, edit, 1f, 1f, 0f);
            Check(probe != null, "입 선 띠를 찾음");
            var pts = MeshOps.MakeMouthLine(md, edit, 0.999f, 0.999f, 0f)!["points"].ToFloatArray();
            float cx = 0, cy = 0, cz = 0; int k = 0; for (int i = 0; i + 5 < pts.Length; i += 6) { cx += pts[i]; cy += pts[i + 1]; cz += pts[i + 2]; k++; }
            ch.Send(Msg.Make(Msg.SetCamera).Set("state", JNode.Obj().Set("pivot", JNode.FloatArray(new[] { cx / k, cy / k, cz / k })).Set("yaw", 180).Set("pitch", 0).Set("distance", 0.07).Set("fov", 30)));
            Console.WriteLine($"   띠 정점 {k}개, 가운데 ({cx / k:0.0000}, {cy / k:0.0000}, {cz / k:0.0000})");
            Shot("line_00_rest.png");
            JNode Cam(float dist) => JNode.Obj().Set("pivot", JNode.FloatArray(new[] { cx / k, cy / k, cz / k })).Set("yaw", 180).Set("pitch", 0).Set("distance", dist).Set("fov", 30);
            doc.Set(Addr.BlendShape(r.Id, edit), 100);
            Shot("line_01_before.png");
            ch.Send(Msg.Make(Msg.SetCamera).Set("state", Cam(0.22f))); Shot("line_face_before.png"); ch.Send(Msg.Make(Msg.SetCamera).Set("state", Cam(0.07f)));
            var op = MeshOps.MakeMouthLine(md, edit, F(0, 0.3f), F(1, 1.3f), F(2, 0.8f) / 1000f)!;
            Console.WriteLine($"   다듬기 점 {op["points"].Count / 6}개");
            doc.Set(Addr.MeshEdit(r.Mesh), JNode.Arr().Add(op));
            wait(Msg.MeshRebuilt, 60000);
            Shot("line_02_after.png");
            doc.Set(Addr.MeshEdit(r.Mesh), JNode.Arr().Add(op.Clone().Set("enabled", false)));   // 토글 끔 = 다듬기 전
            wait(Msg.MeshRebuilt, 60000);
            Shot("line_02_off.png");
            Check(SameImage(P("line_01_before.png"), P("line_02_off.png")), "다듬기 끄면 다듬기 전과 같음");
            doc.Set(Addr.MeshEdit(r.Mesh), JNode.Arr().Add(op));
            wait(Msg.MeshRebuilt, 60000);
            ch.Send(Msg.Make(Msg.SetCamera).Set("state", Cam(0.22f))); Shot("line_face_after.png"); ch.Send(Msg.Make(Msg.SetCamera).Set("state", Cam(0.07f)));
            doc.Set(Addr.BlendShape(r.Id, edit), 50);
            Shot("line_03_after50.png");
            doc.Set(Addr.BlendShape(r.Id, edit), 0);
            Shot("line_04_rest_after.png");
            Check(SameImage(P("line_00_rest.png"), P("line_04_rest_after.png")), "편집 0에서는 원래 입 그대로");
            // 벌린 입: 입 맞추기와 같이 (다듬기 다음에 적용)
            doc.Set(Addr.BlendShape(r.Id, edit), 100);
            var cand = BlinkFixPlan.MouthTargets(doc, r, md);
            doc.Set(Addr.MeshEdit(r.Mesh), JNode.Arr().Add(op).Add(MeshOps.MakeMouthFix(cand.Where(MeshOps.IsMouthOpenName), new[] { (edit, 100f) })));
            wait(Msg.MeshRebuilt, 60000);
            doc.Set(Addr.BlendShape(r.Id, "vrc.v_aa"), 100);
            Shot("line_05_aa.png");
            Console.WriteLine(_fail == 0 ? "PASS" : $"FAIL ({_fail})");
            return _fail == 0 ? 0 : 1;
        }
        finally { try { ch.Send(Msg.Make(Msg.Quit)); proc.WaitForExit(10000); } catch { } }
    }

    static void Capture(PipeChannel ch, Func<string, int, JNode> wait, string path, bool transparent)
    {
        ch.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", 600).Set("height", 800).Set("transparent", transparent).Set("supersample", 2));
        var r = wait(Msg.CaptureDone, 60000);
        Check(r["ok"].AsBool && File.Exists(path), $"capture {Path.GetFileName(path)}" + (r["ok"].AsBool ? "" : " — " + r["error"].AsString));
    }

    // 소프트 섀도·MSAA 반올림으로 ±1 차이가 날 수 있어 채널당 2 이하는 같은 것으로 본다
    /// <summary>a에서 배경(왼쪽 위 픽셀 색)이 아닌 픽셀 중 b에서 바뀐 비율 (Diff와 같은 기준 ±8).</summary>
    static double AvatarDiff(string a, string b)
    {
        var (wa, ha, pa) = DecodePng(a); var (wb, hb, pb) = DecodePng(b);
        if (wa != wb || ha != hb) return 1;
        int n = 0, m = 0, px = wa * ha, ch = pa.Length / px;
        for (int i = 0; i < px; i++)
        {
            bool fg = false, changed = false;
            for (int c = 0; c < ch; c++)
            {
                if (Math.Abs(pa[i * ch + c] - pa[c]) > 8) fg = true;
                if (Math.Abs(pa[i * ch + c] - pb[i * ch + c]) > 8) changed = true;
            }
            if (fg) { m++; if (changed) n++; }
        }
        return m == 0 ? 0 : n / (double)m;
    }

    static bool SameImage(string a, string b)
    {
        var (wa, ha, pa) = DecodePng(a); var (wb, hb, pb) = DecodePng(b);
        if (wa != wb || ha != hb) return false;
        for (int i = 0; i < pa.Length; i++) if (Math.Abs(pa[i] - pb[i]) > 2) return false;
        return true;
    }

    /// <summary>RGBA8 PNG 디코더 (Unity EncodeToPNG 출력 검증용, 필터 0~4).</summary>
    static (int w, int h, byte[] px) DecodePng(string path)
    {
        var d = File.ReadAllBytes(path); int i = 8, w = 0, h = 0; var idat = new MemoryStream();
        while (i < d.Length)
        {
            int n = d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
            var t = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (t == "IHDR") { w = d[i + 8] << 24 | d[i + 9] << 16 | d[i + 10] << 8 | d[i + 11]; h = d[i + 12] << 24 | d[i + 13] << 16 | d[i + 14] << 8 | d[i + 15]; }
            if (t == "IDAT") idat.Write(d, i + 8, n);
            i += 12 + n;
        }
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(raw);
        var r = raw.ToArray(); int stride = w * 4; var px = new byte[stride * h]; int p = 0;
        for (int y = 0; y < h; y++)
        {
            int f = r[p++];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 4 ? px[y * stride + x - 4] : 0, b = y > 0 ? px[(y - 1) * stride + x] : 0, c = x >= 4 && y > 0 ? px[(y - 1) * stride + x - 4] : 0;
                int v = r[p++];
                switch (f)
                {
                    case 1: v += a; break;
                    case 2: v += b; break;
                    case 3: v += (a + b) >> 1; break;
                    case 4: { int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; break; }
                }
                px[y * stride + x] = (byte)v;
            }
        }
        return (w, h, px);
    }

    sealed class Sink : IRenderSink
    {
        readonly PipeChannel _c; public Sink(PipeChannel c) { _c = c; }
        public bool IsConnected => _c.IsOpen;
        public void Send(JNode m) => _c.Send(m);
    }
}
