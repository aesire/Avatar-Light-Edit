// E2E "faceosc" (2026-10-24): 트래커 앱(OSC) 소스 — Baballonia 눈 값 · 받는 양이 많을 때 화면 fps · 입 값 조합 사진
//   1. Baballonia가 보내는 모양(/LeftEyeX·/LeftEyeLid … + Babble 입 45개, 메시지마다 UDP 하나)으로 눈 감기·시선이 값에 들어오는지
//   2. 화면 fps: 같은 시간 동안 주소 하나만 빠르게 보낼 때 vs Baballonia처럼 10 ms마다 51개 (소스 fps = 새 값이 온 화면 프레임 수 ≈ 화면 fps)
//   3. 입 값 조합마다 얼굴 사진 (mouth_<이름>.png) — 웹캠이 내는 조합에서 입이 깨지는지 눈으로 본다
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> faceosc [sweep | 이름=값,이름=값 …]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class E2E
{
    static int FaceOscTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string[] a)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(300); Capture(ch, wait, P(file), false); }
        JNode Last(double seconds)
        {
            JNode last = JNode.Obj(); var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end) { try { last = wait(Msg.TrackingStatus, 500); } catch (TimeoutException) { } }
            return last;
        }
        var to = new IPEndPoint(IPAddress.Loopback, 8888);
        // 보내기: 값 목록을 주기마다 메시지 하나씩 (Baballonia처럼 번들 없이)
        void Stream(Func<IEnumerable<(string, float)>> vals, int periodMs, double seconds, Action? during = null)
        {
            using var udp = new UdpClient();
            var stop = DateTime.UtcNow.AddSeconds(seconds);
            var th = new Thread(() =>
            {
                var sw = Stopwatch.StartNew(); long next = 0;
                while (DateTime.UtcNow < stop)
                {
                    foreach (var (a, v) in vals()) { var b = OscF(a, v); udp.Send(b, b.Length, to); }
                    next += periodMs;
                    int wait = (int)(next - sw.ElapsedMilliseconds);
                    if (periodMs <= 2) { while (sw.ElapsedMilliseconds < next) Thread.SpinWait(50); }   // Sleep은 15 ms씩이라 1 kHz가 안 된다
                    else if (wait > 0) Thread.Sleep(wait);
                }
            }) { IsBackground = true };
            th.Start();
            during?.Invoke();
            th.Join();
        }
        var babbleMouth = new[] { "cheekPuffLeft", "cheekPuffRight", "cheekSuckLeft", "cheekSuckRight", "jawOpen", "jawForward", "jawLeft", "jawRight", "noseSneerLeft", "noseSneerRight",
            "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight", "mouthRollUpper", "mouthRollLower", "mouthShrugUpper", "mouthShrugLower", "mouthClose", "mouthSmileLeft", "mouthSmileRight",
            "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight", "mouthUpperUpLeft", "mouthUpperUpRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight",
            "mouthStretchLeft", "mouthStretchRight", "tongueOut", "tongueUp", "tongueDown", "tongueLeft", "tongueRight", "tongueRoll", "tongueBendDown", "tongueCurlUp", "tongueSquish", "tongueFlat", "tongueTwistLeft", "tongueTwistRight" };
        try
        {
            var doc = new Document(IrScene.Load(ir), ir);
            var sync = new RenderSync(doc, new Sink(ch));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            doc.SetSchemas(MaterialSchema.ReadAll(loaded), true);
            // hide:<머티리얼 guid> = 그 머티리얼을 투명하게 (어느 머티리얼이 그리는지 가려 보기)
            foreach (var h in a.Where(x => x.StartsWith("hide:"))) doc.Set(Addr.MatColor(h.Substring(5), "_Color"), JNode.FloatArray(new[] { 1f, 1f, 1f, 0f }));
            // float:<머티리얼 guid>:<속성>=<값>
            foreach (var h in a.Where(x => x.StartsWith("float:"))) { var q = h.Substring(6).Split(':'); var kv = q[1].Split('='); doc.Set(Addr.MatFloat(q[0], kv[0]), float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)); }
            // set:<블렌드쉐이프>=<값> = 얼굴 메시(블렌드쉐이프가 가장 많은 것)의 사용자 값
            var face = doc.Original.Renderers.Where(x => doc.Original.FindMesh(x.Mesh) != null).OrderByDescending(x => doc.Original.FindMesh(x.Mesh)!.BlendShapes.Count).First();
            foreach (var h in a.Where(x => x.StartsWith("set:"))) { var kv = h.Substring(4).Split('='); doc.Set(Addr.BlendShape(face.Id, kv[0]), float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)); }
            ch.Send(Msg.Make(Msg.SetQuality).Set("maxFps", 0));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
            Shot("osc_00_rest.png");
            ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "osc").Set("port", 8888).Set("smoothing", 0f));
            Thread.Sleep(500);
            if (a.Contains("hostopts"))   // 앱(ExpressionPanel.Options)이 보내는 기본 옵션 그대로
            {
                var groups = JNode.Obj(); foreach (var g in new[] { "eye", "gaze", "brow", "mouth", "cheek", "head" }) groups.Set(g, 1f);
                ch.Send(Msg.Make(Msg.FaceTracking).Set("mirror", false).Set("smoothing", 0.5f).Set("fill", true).Set("gain", 1f).Set("head", true).Set("groups", groups)
                    .Set("body", true).Set("arms", true).Set("move", true).Set("eye", JNode.Obj().Set("sensitivity", 0.5f).Set("sharp", 0.5f).Set("link", true)));
            }
            if (a.Contains("useropts"))   // 사용자 로그에서 역산한 옵션 (세기 × 눈 감도 0.44 · 감도 0.72 · 또렷하게 0) — 다 감으면 그래도 100이어야
                ch.Send(Msg.Make(Msg.FaceTracking).Set("gain", 0.5f).Set("groups", JNode.Obj().Set("eye", 0.88f)).Set("eye", JNode.Obj().Set("sensitivity", 0.72f).Set("sharp", 0f).Set("link", true)));

            // 1. Baballonia 눈: 감기(Lid 0) · 시선 오른쪽 위
            var rnd = new Random(1);
            bool wink = a.Contains("wink");
            IEnumerable<(string, float)> Baballonia(float lid, float x, float y)
            {
                yield return ("/LeftEyeX", x); yield return ("/LeftEyeY", y); yield return ("/LeftEyeLid", lid);
                yield return ("/RightEyeX", x); yield return ("/RightEyeY", y); yield return ("/RightEyeLid", wink && lid < 0.5f ? 1f : lid);   // wink: 왼눈만 감기
                foreach (var m in babbleMouth) yield return ("/" + m, m == "jawOpen" ? 0.1f + (float)rnd.NextDouble() * 0.02f : 0.01f);
            }
            // gx=·gy= : 감은 눈 단계의 시선 (감은 눈에서 트래커가 시선을 튀게 내는 경우)
            float GArg(string k) { var x = a.FirstOrDefault(y => y.StartsWith(k + "=")); return x == null ? 0 : float.Parse(x.Substring(k.Length + 1), System.Globalization.CultureInfo.InvariantCulture); }
            Stream(() => Baballonia(0f, GArg("gx"), GArg("gy")), 10, 2.5);
            var st = Last(1.5);
            var v = st["values"];
            Console.WriteLine($"   Baballonia closed: {st["text"].AsString} · blink {v["eyeBlinkLeft"].AsFloat:0.##}/{v["eyeBlinkRight"].AsFloat:0.##} · mode {st["mode"].AsString}");
            Shot("osc_01_baballonia_closed.png");
            // cam:x,y,z,yaw,pitch,dist[,name] = 감은 눈을 그 자리에서 한 장 더 (800×400)
            foreach (var c in a.Where(x => x.StartsWith("cam:")))
            {
                var q = c.Substring(4).Split(',');
                float F(int i) => float.Parse(q[i], System.Globalization.CultureInfo.InvariantCulture);
                ch.Send(Msg.Make(Msg.SetCamera).Set("state", JNode.Obj().Set("pivot", JNode.FloatArray(new[] { F(0), F(1), F(2) })).Set("yaw", F(3)).Set("pitch", F(4)).Set("distance", F(5)).Set("fov", 30)));
                Thread.Sleep(300);
                ch.Send(Msg.Make(Msg.Capture).Set("path", P($"osc_cam_{(q.Length > 6 ? q[6] : "x")}.png")).Set("width", 800).Set("height", 400).Set("transparent", false).Set("supersample", 2));
                wait(Msg.CaptureDone, 60000);
            }
            if (a.Any(x => x.StartsWith("cam:"))) { ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face")); }
            if (a.Contains("big"))   // 감은 눈 크게 (화면 1500×2000)
            {
                ch.Send(Msg.Make(Msg.Capture).Set("path", P("osc_01_big.png")).Set("width", 1500).Set("height", 2000).Set("transparent", false).Set("supersample", 1));
                wait(Msg.CaptureDone, 60000);
            }
            ch.Send(Msg.Make(Msg.FaceTracking).Set("snapshot", true));
            JNode snap = JNode.Obj(); var sEnd = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < sEnd) { try { snap = wait(Msg.TrackingStatus, 1000); if (snap["snapshot"].AsBool) break; } catch (TimeoutException) { } }
            Console.WriteLine("   closed-eye shapes: " + string.Join(", ", snap["shapes"].Items.Where(x => x["v"].AsFloat > 0.5f).Select(x => $"{x["s"].AsString} {x["v"].AsFloat}")));
            Check(v["eyeBlinkLeft"].AsFloat > 0.9f && v["eyeBlinkRight"].AsFloat > 0.9f,"Baballonia /LeftEyeLid 0 → eyes closed");
            Check(Diff(P("osc_00_rest.png"), P("osc_01_baballonia_closed.png")) > 0.002, $"closed eyes change the face ({Diff(P("osc_00_rest.png"), P("osc_01_baballonia_closed.png")):P2})");
            Stream(() => Baballonia(1f, 0.6f, 0.5f), 10, 2.5);
            st = Last(1.5); v = st["values"];
            Console.WriteLine($"   Baballonia gaze: blink {v["eyeBlinkLeft"].AsFloat:0.##} · inL {v["eyeLookInLeft"].AsFloat:0.##} outR {v["eyeLookOutRight"].AsFloat:0.##} upL {v["eyeLookUpLeft"].AsFloat:0.##} · gaze bones {st["gazeBones"].AsBool}");
            Shot("osc_02_baballonia_gaze.png");
            Check(v["eyeBlinkLeft"].AsFloat < 0.1f && v["eyeLookInLeft"].AsFloat > 0.5f && v["eyeLookOutRight"].AsFloat > 0.5f && v["eyeLookUpLeft"].AsFloat > 0.4f, "Baballonia gaze right/up → look in (left) · out (right) · up");
            Check(Diff(P("osc_00_rest.png"), P("osc_02_baballonia_gaze.png")) > 0.0005, $"gaze moves the eyes ({Diff(P("osc_00_rest.png"), P("osc_02_baballonia_gaze.png")):P2})");

            // 2. 화면 fps: 주소 하나만 1 ms마다 vs Baballonia 51개를 10 ms마다
            // 상태는 렌더러 시간으로 1초마다 오므로, 이어 온 상태 4개의 프레임 수 차이 / 3 = 화면 fps
            (float fps, int frames, int gc) Measure(Func<IEnumerable<(string, float)>> vals, int period)
            {
                var got = new List<JNode>();
                Stream(vals, period, 6.5, () =>
                {
                    Last(1.5);
                    for (int i = 0; i < 4; i++) got.Add(wait(Msg.TrackingStatus, 3000));
                });
                var (a, b) = (got[0], got[3]);
                return (b["fps"].AsFloat, (b["frame"].AsInt - a["frame"].AsInt) / 3, b["gc"].AsInt - a["gc"].AsInt);
            }
            var light = Measure(() => new[] { ("/jawOpen", 0.1f + (float)rnd.NextDouble() * 0.02f) }, 1);
            var heavy = Measure(() => Baballonia(1f, 0.1f, 0f), 10);
            var stress = Measure(() => Baballonia(1f, 0.1f, 0f), 2);
            Console.WriteLine($"   screen fps (GC in 3 s): one address 1 kHz {light.frames} ({light.gc}) · Baballonia 5.1k msg/s {heavy.frames} ({heavy.gc}) · 5× Baballonia {stress.frames} ({stress.gc})");
            Check(heavy.frames > light.frames * 0.85f && stress.frames > light.frames * 0.7f, $"Baballonia-sized stream keeps the frame rate ({heavy.frames} / {stress.frames} vs {light.frames} fps)");

            // 3. 입 값 조합 (경로 있는 이름 = 배율 없이 그대로)
            var combos = new (string Name, (string, float)[] Vals)[]
            {
                ("jaw15", new[] { ("jawOpen", 0.15f) }),
                ("jaw15_close40", new[] { ("jawOpen", 0.15f), ("mouthClose", 0.4f) }),
                ("jaw05_close30", new[] { ("jawOpen", 0.05f), ("mouthClose", 0.3f) }),
                ("jaw20_roll50", new[] { ("jawOpen", 0.2f), ("mouthRollUpper", 0.5f), ("mouthRollLower", 0.5f) }),
                ("jaw20_funnel50_pucker50", new[] { ("jawOpen", 0.2f), ("mouthFunnel", 0.5f), ("mouthPucker", 0.5f) }),
                ("jaw20_shrug50", new[] { ("jawOpen", 0.2f), ("mouthShrugUpper", 0.5f), ("mouthShrugLower", 0.5f) }),
                ("jaw20_press50", new[] { ("jawOpen", 0.2f), ("mouthPressLeft", 0.5f), ("mouthPressRight", 0.5f) }),
                ("jaw20_updown50", new[] { ("jawOpen", 0.2f), ("mouthUpperUpLeft", 0.5f), ("mouthUpperUpRight", 0.5f), ("mouthLowerDownLeft", 0.5f), ("mouthLowerDownRight", 0.5f) }),
                ("jaw20_stretch50", new[] { ("jawOpen", 0.2f), ("mouthStretchLeft", 0.5f), ("mouthStretchRight", 0.5f) }),
                ("jaw20_dimple50", new[] { ("jawOpen", 0.2f), ("mouthDimpleLeft", 0.5f), ("mouthDimpleRight", 0.5f) }),
                ("jaw20_frown50", new[] { ("jawOpen", 0.2f), ("mouthFrownLeft", 0.5f), ("mouthFrownRight", 0.5f) }),
                ("jaw20_smile50", new[] { ("jawOpen", 0.2f), ("mouthSmileLeft", 0.5f), ("mouthSmileRight", 0.5f) }),
                ("jaw20_funnel60_rolllow40", new[] { ("jawOpen", 0.2f), ("mouthFunnel", 0.6f), ("mouthRollLower", 0.4f) }),
                ("jaw10_pucker60", new[] { ("jawOpen", 0.1f), ("mouthPucker", 0.6f) }),
            };
            // sweep: 입 값 하나씩 (좌우는 함께) 0.6 + 입 벌림 0.15 — 어느 쉐이프키에서 깨지는지
            if (a.Contains("sweep"))
                combos = babbleMouth.Where(m => !m.EndsWith("Right") && m != "jawOpen").Select(m => m.EndsWith("Left") ? m.Substring(0, m.Length - 4) : m)
                    .Select(b => (b, new[] { ("jawOpen", 0.15f), (b, 0.6f), (b + "Left", 0.6f), (b + "Right", 0.6f) })).ToArray();
            // 직접 고른 조합: "jawOpen=0.15,mouthUpperUp=0.15" (좌우 값은 이름 하나로 둘 다)
            var custom = a.Where(x => x.Contains('=') && !x.StartsWith("hide:") && !x.StartsWith("set:") && !x.StartsWith("float:") && !x.StartsWith("cam:") && x != "big" && x != "wink" && x != "hostopts" && x != "useropts" && !x.StartsWith("gx=") && !x.StartsWith("gy=")).ToList();
            if (custom.Count > 0)
                combos = custom.Select(spec => (spec.Replace('=', '_').Replace(',', '-'), spec.Split(',').SelectMany(kv =>
                {
                    var p = kv.Split('='); float f = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    return new[] { (p[0], f), (p[0] + "Left", f), (p[0] + "Right", f) };
                }).ToArray())).ToArray();
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
            foreach (var (name, vals) in combos)
            {
                var set = vals.ToDictionary(x => x.Item1, x => x.Item2);
                Stream(() => babbleMouth.Select(m => ("/x/" + m, set.TryGetValue(m, out var x) ? x : 0f)), 20, 1.2);
                Shot($"mouth_{name}.png");
            }
            // 입 둘레만 2배로 모아 한 장 (mouth_all.png, 조합 순서대로 3열)
            const int cx = 180, cy = 620, cw = 240, chh = 140, cols = 3;
            int rows = (combos.Length + cols - 1) / cols;
            var all = new Rgba(cw * 2 * cols, chh * 2 * rows);
            for (int k = 0; k < combos.Length; k++)
            {
                var img = Sticker.DecodePng(File.ReadAllBytes(P($"mouth_{combos[k].Name}.png")))!;
                int ox = k % cols * cw * 2, oy = k / cols * chh * 2;
                for (int y = 0; y < chh * 2; y++)
                    for (int x = 0; x < cw * 2; x++)
                    {
                        int s = ((cy + y / 2) * img.W + cx + x / 2) * 4, d = ((oy + y) * all.W + ox + x) * 4;
                        for (int c = 0; c < 4; c++) all.Px[d + c] = (x < 2 || y < 2) ? (byte)255 : img.Px[s + c];
                    }
            }
            File.WriteAllBytes(P("mouth_all.png"), Sticker.EncodePng(all));
            Console.WriteLine("   mouth_all.png: " + string.Join(" | ", combos.Select(c => c.Name)));
            ch.Send(Msg.Make(Msg.FaceTracking).Set("source", "off"));
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
