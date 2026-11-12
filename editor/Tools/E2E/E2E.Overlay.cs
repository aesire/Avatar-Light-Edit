// E2E "overlay" (2026-10-23): 방송 오버레이의 후처리·슈퍼샘플링 (렌더러 Overlay · OverlaySupersample)
//   노출 +1.5 후처리를 넣고 찍기(오버레이 끔) → 오버레이 켬(렌더러 자기 창, 슈퍼샘플링 2배) → 찍기: 후처리가 그대로라 같은 그림이어야 한다
//   (예전엔 오버레이 중 후처리를 꺼서 어두워졌다). 슈퍼샘플링 중에도 찍기가 되고, 1.5배로 바꾸고 끄면 처음 그림, 렌더러는 살아 있다.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> overlay
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VrcProto.Protocol;

static partial class E2E
{
    static int OverlayTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            var env = JNode.Arr()
                .Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj().Set("preset", "bright")))
                .Add(JNode.Obj().Set("provider", "PostProcess").Set("version", 1).Set("params", JNode.Obj().Set("bloom", 0.0).Set("exposure", 1.5)));
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", env));
            Thread.Sleep(500);
            string a = Path.Combine(outDir, "overlay_off.png"), b = Path.Combine(outDir, "overlay_ss2.png"), c = Path.Combine(outDir, "overlay_after.png");
            Capture(ch, wait, a, false);

            long hwnd = proc.MainWindowHandle.ToInt64();
            Console.WriteLine($"   renderer window 0x{hwnd:X}");
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd).Set("adjust", false).Set("supersample", 2f));
            Thread.Sleep(1500);
            Check(!proc.HasExited, "renderer alive with overlay + 2× supersampling");
            Capture(ch, wait, b, false);
            double d = MeanDiff(a, b);
            Console.WriteLine($"   mean diff off vs overlay: {d:0.00}");
            Check(d < 2, $"post-processing stays on in overlay (same picture, diff {d:0.00})");

            // 알파 바로잡기 (2026-10-24): 화면에 내보내는 알파(capture alpha: raw)를 검정·흰 차이로 구한 실제 덮임(투명 촬영)과 비교 —
            // 실제로는 불투명(알파 ≥ 0.99)인데 화면 알파가 0.9 아래면 게임이 비쳐 보이는 "구멍"
            string truth = Path.Combine(outDir, "overlay_alpha_truth.png"), rawOff = Path.Combine(outDir, "overlay_alpha_nofix.png"), rawOn = Path.Combine(outDir, "overlay_alpha_fix.png");
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", hwnd));
            Thread.Sleep(300);
            Capture(ch, wait, truth, true);
            void RawShot(string path, bool fix)
            {
                ch.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd).Set("adjust", false).Set("supersample", 1f).Set("alphaFix", fix));
                Thread.Sleep(600);
                ch.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", 600).Set("height", 800).Set("supersample", 2).Set("alpha", "raw"));
                var r = wait(Msg.CaptureDone, 60000);
                Check(r["ok"].AsBool, $"capture {Path.GetFileName(path)} (screen alpha) {r["error"].AsString}");
            }
            RawShot(rawOff, false);
            RawShot(rawOn, true);
            var (tw, th, tp) = DecodePng(truth);
            int Holes(string path, out int opaque)
            {
                var (w2, h2, p2) = DecodePng(path);
                int holes = 0; opaque = 0;
                if (w2 != tw || h2 != th) return -1;
                for (int i = 3; i < tp.Length; i += 4)
                    if (tp[i] >= 252) { opaque++; if (p2[i] < 230) holes++; }
                return holes;
            }
            void Compare(string tag)
            {
                int hOff = Holes(rawOff, out int opq), hOn = Holes(rawOn, out _);
                Console.WriteLine($"   overlay alpha holes {tag} (opaque pixels with screen alpha < 0.9): without fix {hOff} / {opq} ({100.0 * hOff / Math.Max(1, opq):F2}%), with fix {hOn}");
                Check(hOn >= 0 && hOn <= Math.Max(20, opq / 2000), $"alpha fix {tag}: opaque avatar pixels stay opaque on the overlay ({hOn} holes, was {hOff})");
                // 배경은 그대로 투명: 실제로 빈 곳(알파 0)은 화면 알파도 0
                var (_, _, pOn) = DecodePng(rawOn);
                int leak = 0, empty = 0;
                for (int i = 3; i < tp.Length; i += 4) if (tp[i] == 0) { empty++; if (pOn[i] > 8) leak++; }
                Check(empty > 0 && leak <= empty / 1000, $"alpha fix {tag}: empty background stays transparent ({leak} of {empty} pixels)");
            }
            Compare("full body");

            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd).Set("adjust", true).Set("supersample", 1.5f).Set("fps", 30));
            Thread.Sleep(800);
            Check(!proc.HasExited, "renderer alive after 1.5× + adjust mode");
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", hwnd));
            Thread.Sleep(800);
            Capture(ch, wait, c, false);
            double d2 = MeanDiff(a, c);
            Check(d2 < 2, $"overlay off → same picture (diff {d2:0.00})");
            // 후처리가 정말 들어가는지: 노출 0으로 찍으면 달라야 한다
            env[1]["params"].Set("exposure", 0.0);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", env));
            Thread.Sleep(500);
            string e0 = Path.Combine(outDir, "overlay_ev0.png");
            Capture(ch, wait, e0, false);
            double d3 = MeanDiff(a, e0);
            Check(d3 > 3, $"exposure actually changes the picture (diff {d3:0.00})");
            // 알파 바로잡기, 얼굴 가까이 (앞머리·눈 위 반투명이 많은 곳)
            ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Head").Set("size", 0.3));
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", hwnd));
            Thread.Sleep(400);
            Capture(ch, wait, truth, true);
            (tw, th, tp) = DecodePng(truth);
            RawShot(rawOff, false);
            RawShot(rawOn, true);
            Compare("face close-up");
            // 크로마키 배경 (2026-11-12, OBS로 보내기): 빈 곳은 키 색·알파 1, 아바타(불투명)는 투명 배경일 때와 같은 색
            void Keyed(float[] key, float ss)
            {
                string kp = Path.Combine(outDir, $"overlay_chroma_{(int)(key[0] * 255):X2}{(int)(key[1] * 255):X2}{(int)(key[2] * 255):X2}_ss{ss:0.#}.png");
                ch.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd).Set("adjust", false).Set("supersample", ss).Set("mode", "obs").Set("chroma", JNode.FloatArray(key)));
                Thread.Sleep(800);
                ch.Send(Msg.Make(Msg.Capture).Set("path", kp).Set("width", 600).Set("height", 800).Set("supersample", 2).Set("alpha", "raw"));
                var r = wait(Msg.CaptureDone, 60000);
                Check(r["ok"].AsBool, $"capture {Path.GetFileName(kp)} {r["error"].AsString}");
                var (kw, kh, kpx) = DecodePng(kp);
                var (_, _, pOn) = DecodePng(rawOn);
                if (kw != tw || kh != th) { Check(false, "chroma shot size"); return; }
                int bg = 0, bgBad = 0, op = 0, opBad = 0;
                for (int i = 0; i < tp.Length; i += 4)
                {
                    if (tp[i + 3] == 0)
                    {
                        bg++;
                        if (kpx[i + 3] < 250 || Math.Abs(kpx[i] - key[0] * 255) > 6 || Math.Abs(kpx[i + 1] - key[1] * 255) > 6 || Math.Abs(kpx[i + 2] - key[2] * 255) > 6) bgBad++;
                    }
                    else if (tp[i + 3] >= 252)
                    {
                        op++;
                        if (kpx[i + 3] < 250 || Math.Abs(kpx[i] - pOn[i]) + Math.Abs(kpx[i + 1] - pOn[i + 1]) + Math.Abs(kpx[i + 2] - pOn[i + 2]) > 12) opBad++;
                    }
                }
                Console.WriteLine($"   chroma ×{ss:0.#}: background {bg - bgBad}/{bg} key color, avatar {op - opBad}/{op} unchanged");
                Check(bg > 0 && bgBad <= bg / 500, $"chroma ×{ss:0.#}: empty background is the key color, opaque ({bgBad} of {bg} off)");
                Check(op > 0 && opBad <= op / 200, $"chroma ×{ss:0.#}: avatar keeps its colors ({opBad} of {op} changed)");
            }
            Keyed(new float[] { 0, 1, 0 }, 1);
            Keyed(new float[] { 0, 0, 1 }, 2);
            // 투명으로 되돌리면 다시 알파 0
            RawShot(rawOn, true);
            { var (_, _, pBack) = DecodePng(rawOn); int left = 0; for (int i = 3; i < tp.Length; i += 4) if (tp[i] == 0 && pBack[i] > 8) left++; Check(left == 0, $"chroma off → background transparent again ({left} pixels left)"); }
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", hwnd));
            // 방송 화질 조명 비교 (눈으로 보는 용, 스튜디오 고화질에서): 위 주광 또렷한 그림자 · 위 주광 부드러운 그림자 + 머리 장식 그림자 끔 + 앞 채움광 · 정면 그림자 없음
            ch.Send(Msg.Make(Msg.SetQuality).Set("studio", true).Set("ao", 0.0));
            ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Head").Set("size", 0.35));
            Thread.Sleep(300);
            var post = JNode.Obj().Set("provider", "PostProcess").Set("version", 1).Set("params", JNode.Obj().Set("bloom", 0.5).Set("bloomThreshold", 0.9).Set("exposure", 0.2).Set("contrast", 0.1).Set("saturation", 0.12));
            JNode Sun(double yaw, double pitch, bool shadows, double strength, double soft, bool acc) => JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj()
                .Set("preset", "bright").Set("intensity", 1.3).Set("yaw", yaw).Set("pitch", pitch).Set("shadows", shadows).Set("shadowStrength", strength)
                .Set("shadowSoftness", soft).Set("accessoryShadows", acc).Set("reflection", 1.5));
            var fill = JNode.Obj().Set("provider", "Lights").Set("version", 1).Set("params", JNode.Obj().Set("l1_on", true).Set("l1_type", "point")
                .Set("l1_color", JNode.FloatArray(new float[] { 1, 1, 1 })).Set("l1_intensity", 1.5).Set("l1_range", 6.0).Set("l1_around", 0.0).Set("l1_height", 1.1).Set("l1_distance", 1.5));
            foreach (var (name, list) in new[] {
                ("face_sharp", JNode.Arr().Add(Sun(30, 0, true, 0.8, 0, true)).Add(post.Clone())),
                ("face_topfill", JNode.Arr().Add(Sun(30, 0, true, 0.4, 0.6, false)).Add(fill.Clone()).Add(post.Clone())),
                ("face_front", JNode.Arr().Add(Sun(30, -20, false, 0.4, 0.6, false)).Add(post.Clone())) })
            {
                ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", list)); Thread.Sleep(1200);
                Capture(ch, wait, Path.Combine(outDir, name + ".png"), false);
            }
            ch.Send(Msg.Make(Msg.SetQuality).Set("studio", false));
            Check(!proc.HasExited, "renderer still running");
            ch.Send(Msg.Make(Msg.Quit));
            Check(proc.WaitForExit(15000), "renderer quits cleanly");
            var log = File.ReadAllText(Path.Combine(outDir, "renderer.log"));
            Check(log.Contains("오버레이 프레임 제한: 30 fps"), "overlay frame limit 30 fps while on");
            Check(log.Contains("슈퍼샘플링 ×1.5"), "1.5× supersampling (four-tap downsample) ran");
        }
        catch (Exception e) { Check(false, "overlay: " + e.Message); }
        if (!proc.HasExited) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E overlay: all ok" : $"E2E overlay: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>두 PNG의 채널 평균 차이 (0~255).</summary>
    static double MeanDiff(string a, string b)
    {
        var (w1, h1, p1) = DecodePng(a); var (w2, h2, p2) = DecodePng(b);
        if (w1 != w2 || h1 != h2) return 255;
        long sum = 0;
        for (int i = 0; i < p1.Length; i++) sum += Math.Abs(p1[i] - p2[i]);
        return (double)sum / p1.Length;
    }
}
