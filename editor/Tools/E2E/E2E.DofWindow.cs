// E2E "dofwindow" (2026-11-12): 스튜디오 피사계 심도가 미리보기 창(백 버퍼)에도 들어가는지.
//   예전 E2E "studio"는 촬영(RenderTexture)만 봐서, 창에 보이는 미리보기에서 심도가 안 먹는 것을 못 잡았다.
//   심도 없음 · 심도 켬 · 초점 영역 보기 · 고화질(MSAA 8×) + 심도를 창 화면으로 찍어 비교한다.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> dofwindow
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class E2E
{
    static int DofWindowTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        JNode Studio(JNode m) { ch.Send(m.Set("t", Msg.Studio)); return wait(Msg.StudioState, 20000); }
        void Window(string file)
        {
            Thread.Sleep(600);
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
            ch.Send(Msg.Make(Msg.SetQuality).Set("maxFps", 30));
            ch.Send(Msg.Make(Msg.SetView).Set("gizmo", false).Set("grid", false));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Studio(JNode.Obj().Set("enabled", true).Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("mode", "solid").Set("strength", 0.6f).Set("color", JNode.FloatArray(new[] { 0.8f, 0.8f, 0.82f }))));
            Window("dw_00_nodof.png");
            var st = Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("strength", 1f).Set("auto", true)));
            Console.WriteLine($"   focus {st["focus"].AsDouble:F2} m");
            Window("dw_01_dof.png");
            double d = Diff(P("dw_00_nodof.png"), P("dw_01_dof.png"));
            Check(d > 0.01, $"window: depth of field changes the preview ({d:P1})");
            Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("show", true)));
            Window("dw_02_focus_view.png");
            Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", true).Set("show", false)));
            ch.Send(Msg.Make(Msg.SetQuality).Set("studio", true).Set("ao", 0.5f));
            Window("dw_03_dof_studio_quality.png");
            Studio(JNode.Obj().Set("dof", JNode.Obj().Set("enabled", false)));
            Window("dw_04_nodof_studio_quality.png");
            d = Diff(P("dw_03_dof_studio_quality.png"), P("dw_04_nodof_studio_quality.png"));
            Check(d > 0.01, $"window + studio quality: depth of field changes the preview ({d:P1})");
            // 튕김 시험 (2026-11-12, 사용자 "심도를 켜면 가끔 튕김"): 켜고 끄기·세기 바꾸기·고화질 누적 촬영을 빠르게 반복해도 렌더러가 살아 있는지
            ch.Send(Msg.Make(Msg.SetQuality).Set("studio", true).Set("ao", 0.5f).Set("captureAa", 8));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++)
            {
                ch.Send(Msg.Make(Msg.Studio).Set("dof", JNode.Obj().Set("enabled", i % 3 != 2).Set("strength", 0.1f + 0.9f * (i % 7) / 6f).Set("auto", i % 2 == 0).Set("distance", 1f + i % 5)));
                ch.Send(Msg.Make(Msg.SetCamera).Set("preset", i % 2 == 0 ? "full" : "front"));
                if (i % 8 == 0) Capture(ch, wait, P($"dw_10_stress_{i:00}.png"), i % 16 == 0);
                Thread.Sleep(30);
            }
            ch.Send(Msg.Make(Msg.Studio).Set("dof", JNode.Obj().Set("enabled", true).Set("strength", 1f).Set("auto", true)));
            Capture(ch, wait, P("dw_11_after_stress.png"), false);
            Check(!proc.HasExited && File.Exists(P("dw_11_after_stress.png")), $"renderer alive after 40 rapid depth-of-field changes + 6 accumulated shots ({sw.Elapsed.TotalSeconds:F1} s)");
            ch.Send(Msg.Make(Msg.SetQuality).Set("studio", false));
            Studio(JNode.Obj().Set("enabled", false));
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
