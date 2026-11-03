// E2E "pbskirt" (2026-11-03): 치마 가닥이 위·아래 어디가 얼마나 움직이는지 관절마다 잰다
//   사용자 "VR에서 치마 맨 아래만 작은 움직임에도 날리고 위는 거의 안 움직여". 움직임마다 쉬는 상태에서 바뀐 각(°)·거리(cm)의 최대를 관절별로 적는다.
//   움직임: 몸 통째 흔들기(sway) · 다리만 조금 앞뒤로(legs: VR에서 엉덩이가 움직여 허벅지가 치마에 닿는 것) · 걷다 멈춤(vrwalk)
// 실행: dotnet bin/E2E.dll <렌더러> <아바타.ir.json> <출력> pbskirt [체인 루트 이름,…]  (E2E_WEIGHT=1.5,4,10)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class E2E
{
    static int PbSkirtTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, string[] roots)
    {
        var re = new Regex(@"^\s+(\S+?)(\[F\])? .*move ([\d.]+)cm angle (\d+)°");
        // 관절 이름 → (각, 거리)
        Dictionary<string, (double ang, double move)> Read()
        {
            var m = Msg.Make(Msg.SetPhysBones).Set("stats", true); var d = JNode.Arr(); foreach (var r in roots) d.Add(r); m.Set("describe", d);
            ch.Send(m);
            var res = new Dictionary<string, (double, double)>();
            string chain = "";
            foreach (var line in wait(Msg.PhysBoneStats, 10000)["detail"].Items.Select(x => x.AsString))
            {
                if (line.StartsWith("chain ")) { chain = line.Split(' ')[1]; continue; }
                var g = re.Match(line); if (!g.Success || g.Groups[2].Success) continue;
                res[chain + "/" + g.Groups[1].Value] = (double.Parse(g.Groups[4].Value, CultureInfo.InvariantCulture), double.Parse(g.Groups[3].Value, CultureInfo.InvariantCulture));
            }
            return res;
        }
        var weights = (Environment.GetEnvironmentVariable("E2E_WEIGHT") ?? "1.5,4,10").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 120000);
            ch.Send(Msg.Make(Msg.SetQuality).Set("maxFps", 90));
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("legGuard", true));
            foreach (var w in weights)
            {
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("weight", w).Set("reset", true));
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
                Thread.Sleep(2500);
                var rest = Read();
                foreach (var motion in new[] { "sway", "legs", "vrwalk" })
                {
                    var max = new Dictionary<string, (double ang, double move)>();
                    void Sample() { foreach (var (k, v) in Read()) { var r0 = rest.TryGetValue(k, out var r) ? r : (0, 0); var m0 = max.TryGetValue(k, out var o) ? o : (0, 0); max[k] = (Math.Max(m0.Item1, Math.Abs(v.ang - r0.Item1)), Math.Max(m0.Item2, v.move)); } }
                    var sw = Stopwatch.StartNew();
                    if (motion == "legs")
                    {
                        // 다리만 ±0.08 (근육 값) 1.2 Hz로 번갈아: VR에서 엉덩이가 조금 움직일 때 허벅지가 치마에 닿는 정도
                        while (sw.ElapsedMilliseconds < 3000)
                        {
                            double t = sw.ElapsedMilliseconds / 1000.0, a = 0.08 * Math.Sin(2 * Math.PI * 1.2 * t);
                            ch.Send(Msg.Make(Msg.SetPose).Set("pose", PoseValue.Make(new Dictionary<string, float> { ["Left Upper Leg Front-Back"] = (float)a, ["Right Upper Leg Front-Back"] = (float)-a })));
                            Thread.Sleep(33);
                            if (sw.ElapsedMilliseconds % 100 < 34) Sample();
                        }
                        ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
                    }
                    else
                    {
                        ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", motion));
                        while (sw.ElapsedMilliseconds < 3600) { Sample(); Thread.Sleep(60); }
                    }
                    Thread.Sleep(2500);
                    // 체인마다 한 줄: 관절 순서대로 각° / 거리cm
                    foreach (var g in max.GroupBy(k => k.Key.Split('/')[0]))
                        Console.WriteLine($"   w{w} {motion,-6} {g.Key,-14} " + string.Join("  ", g.Select(k => $"{k.Value.ang,3:0}°/{k.Value.move,4:0.0}")));
                }
            }
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
