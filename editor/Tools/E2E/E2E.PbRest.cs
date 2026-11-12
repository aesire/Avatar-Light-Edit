// E2E "pbrest" (2026-11-12): 쉬는 자세에서 PhysBone 관절이 원래 자리(불러온 자세)에서 얼마나 벗어나는지 체인마다 잰다
//   사용자 "일부 PB에서 넥타이·의상 악세서리가 제 위치를 잡지 못하고 위로 떠오른다". 원본은 불러온 자세가 쉬는 자세이므로
//   가만히 서 있을 때 크게 벗어난 관절(특히 위로)은 시뮬레이션 쪽 문제다. 다리 뚫림 막기를 끄고 켠 차이도 본다.
// 실행: dotnet bin/E2E.dll <렌더러> <아바타.ir.json> <출력> pbrest [가장 큰 것 몇 줄, 기본 30]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VrcProto.Protocol;

static partial class E2E
{
    static readonly Regex PbJointLine = new Regex(@"^\s\s(.+?)(\[F\])? r [-\d.]+cm move .*? at ([-\d.eE]+),([-\d.eE]+),([-\d.eE]+) restOverlap\[(.*)\]$");

    /// <summary>모든 체인의 관절 자리: "체인#번호/관절" → (x, y, z, 고정인지, 쉬는 자세 겹침 글). 같은 이름 체인이 여럿이면 나온 순서로 번호.</summary>
    static Dictionary<string, (double x, double y, double z, bool fix, string ov)> PbJoints(PipeChannel ch, Func<string, int, JNode> wait, IEnumerable<string> roots)
    {
        var m = Msg.Make(Msg.SetPhysBones).Set("stats", true); var d = JNode.Arr(); foreach (var r in roots) d.Add(r); m.Set("describe", d);
        ch.Send(m);
        var res = new Dictionary<string, (double, double, double, bool, string)>();
        var seen = new Dictionary<string, int>();
        string chain = "";
        double F(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        foreach (var line in wait(Msg.PhysBoneStats, 20000)["detail"].Items.Select(x => x.AsString))
        {
            if (line.StartsWith("chain "))
            {
                int cut = line.LastIndexOf(" parts ", StringComparison.Ordinal);
                var nm = cut > 6 ? line.Substring(6, cut - 6) : line.Substring(6);
                seen[nm] = seen.TryGetValue(nm, out var n) ? n + 1 : 0;
                chain = seen[nm] > 0 ? $"{nm}#{seen[nm]}" : nm;
                continue;
            }
            var g = PbJointLine.Match(line);
            if (!g.Success) continue;
            string key = chain + "/" + g.Groups[1].Value; int dup = 1;
            while (res.ContainsKey(key)) key = chain + "/" + g.Groups[1].Value + "~" + ++dup;
            res[key] = (F(g.Groups[3].Value), F(g.Groups[4].Value), F(g.Groups[5].Value), g.Groups[2].Success, g.Groups[6].Value.Trim());
        }
        return res;
    }

    static List<string> PbRootNames(string ir)
    {
        var root = JNode.Parse(File.ReadAllText(ir));
        var nodes = root["nodes"]; var names = new List<string>();
        foreach (var pb in root["physBones"].Items)
        {
            int r = pb.Has("root") && pb["root"].AsInt >= 0 ? pb["root"].AsInt : pb["node"].AsInt;
            if (r < 0 || r >= nodes.Count) continue;
            var nm = nodes.Items.ElementAt(r)["name"].AsString;
            if (!names.Contains(nm)) names.Add(nm);
        }
        return names;
    }

    static int PbRestTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, int top)
    {
        try
        {
            var roots = PbRootNames(ir);
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 180000);
            Console.WriteLine($"   chains {loaded["physBones"]["chains"].AsInt} bones {loaded["physBones"]["bones"].AsInt} legGuard {loaded["physBones"]["legGuard"].AsInt} · 루트 이름 {roots.Count}");
            // 자세: 불러온 자세 · 팔 내리고 서기 · 앉기 · 숙이기. 자세마다 PhysBone을 끈 자리(몸에 붙어 따라간 자리)와 켜고 가라앉은 자리를 견준다
            var arms = new Dictionary<string, float>
            {
                ["Left Arm Down-Up"] = -0.6f, ["Right Arm Down-Up"] = -0.6f, ["Left Arm Front-Back"] = 0.3f, ["Right Arm Front-Back"] = 0.3f,
                ["Left Forearm Stretch"] = 1f, ["Right Forearm Stretch"] = 1f,
            };
            var sit = new Dictionary<string, float>(arms) { ["Left Upper Leg Front-Back"] = 1f, ["Right Upper Leg Front-Back"] = 1f, ["Left Lower Leg Stretch"] = -0.2f, ["Right Lower Leg Stretch"] = -0.2f };
            var bow = new Dictionary<string, float>(arms) { ["Spine Front-Back"] = 0.8f, ["Chest Front-Back"] = 0.8f, ["Head Nod Down-Up"] = 0.6f };
            var only = Environment.GetEnvironmentVariable("E2E_POSES")?.Split(',');
            foreach (var (pname, pose) in new (string, Dictionary<string, float>?)[] { ("bind", null), ("stand", arms), ("sit", sit), ("bow", bow) })
            foreach (var guard in new[] { true, false })
            {
                if (only != null && !only.Contains(pname) || !guard && pname != "bind" && pname != "sit") continue;
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", pose == null ? JNode.Null : VrcProto.Host.Core.PoseValue.Make(pose)));
                Thread.Sleep(600);
                var rest = PbJoints(ch, wait, roots);
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("legGuard", guard).Set("reset", true));
                PbSettle(ch, 3);
                var now = PbJoints(ch, wait, roots);
                Console.WriteLine($"## 자세 {pname}");
                var rows = new List<(string key, double dist, double dy, string ov)>();
                foreach (var (k, v) in now)
                {
                    if (v.fix || !rest.TryGetValue(k, out var r)) continue;
                    double dx = v.x - r.x, dy = v.y - r.y, dz = v.z - r.z;
                    rows.Add((k, Math.Sqrt(dx * dx + dy * dy + dz * dz) * 100, dy * 100, v.ov));
                }
                int up = rows.Count(r => r.dy > 1), far = rows.Count(r => r.dist > 2);
                Console.WriteLine($"== 다리 뚫림 막기 {(guard ? "켬" : "끔")}: 관절 {rows.Count} · 2 cm 넘게 벗어남 {far} · 1 cm 넘게 위로 {up} · 평균 {rows.Average(r => r.dist):0.00} cm · 최대 {rows.Max(r => r.dist):0.0} cm");
                foreach (var r in rows.OrderByDescending(r => r.dist).Take(top))
                    Console.WriteLine($"   {r.dist,5:0.0} cm (위로 {r.dy,5:0.0})  {r.key}{(r.ov.Length > 0 ? "  겹침[" + r.ov + "]" : "")}");
                // 체인마다 가장 큰 관절 하나씩 (파일로)
                var byChain = rows.GroupBy(r => r.key.Split('/')[0]).Select(g => g.OrderByDescending(r => r.dist).First()).OrderByDescending(r => r.dist);
                File.WriteAllLines(Path.Combine(outDir, $"pbrest_{pname}_{(guard ? "guard" : "noguard")}.txt"), byChain.Select(r => $"{r.dist:0.00}\t{r.dy:0.00}\t{r.key}\t{r.ov}"));
            }
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static readonly Regex PbMoveRe = new Regex(@" move ([\d.]+)cm "), PbPeakRe = new Regex(@" peak ([-\d.]+)/([-\d.]+) ");

    /// <summary>관절마다 (쉬는 자리에서 지금 벗어난 cm, 가장 높이 뜬 cm, 가장 멀리 벗어난 cm). 고정 관절은 뺀다.</summary>
    static Dictionary<string, (double move, double up, double far)> PbPeaks(PipeChannel ch, Func<string, int, JNode> wait, IEnumerable<string> roots)
    {
        var m = Msg.Make(Msg.SetPhysBones).Set("stats", true); var d = JNode.Arr(); foreach (var r in roots) d.Add(r); m.Set("describe", d);
        ch.Send(m);
        var res = new Dictionary<string, (double, double, double)>();
        var seen = new Dictionary<string, int>();
        string chain = "";
        double F(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        foreach (var line in wait(Msg.PhysBoneStats, 30000)["detail"].Items.Select(x => x.AsString))
        {
            if (line.StartsWith("chain "))
            {
                int cut = line.LastIndexOf(" parts ", StringComparison.Ordinal);
                var nm = cut > 6 ? line.Substring(6, cut - 6) : line.Substring(6);
                seen[nm] = seen.TryGetValue(nm, out var n) ? n + 1 : 0;
                chain = seen[nm] > 0 ? $"{nm}#{seen[nm]}" : nm;
                continue;
            }
            var g = PbJointLine.Match(line); var mv = PbMoveRe.Match(line); var pk = PbPeakRe.Match(line);
            if (!g.Success || !mv.Success || g.Groups[2].Success) continue;
            string key = chain + "/" + g.Groups[1].Value; int dup = 1;
            while (res.ContainsKey(key)) key = chain + "/" + g.Groups[1].Value + "~" + ++dup;
            res[key] = (F(mv.Groups[1].Value), pk.Success ? F(pk.Groups[1].Value) : 0, pk.Success ? F(pk.Groups[2].Value) : 0);
        }
        return res;
    }

    /// <summary>
    /// E2E "pbmove" (2026-11-12): 움직임 시험마다 관절이 가장 높이 뜬 높이·가장 멀리 벗어난 거리와, 끝난 뒤 제자리로 돌아왔는지를 잰다.
    /// 시간을 멈추고 바로 돌리기(setPhysBones { advance })로만 돌려 매번 같은 결과가 나온다. 프레임률마다 돌려 서로 견준다 (E2E_FPS=30,60,90,144).
    /// 실행: dotnet bin/E2E.dll <렌더러> <아바타.ir.json> <출력> pbmove [가장 큰 것 몇 줄]
    /// </summary>
    static int PbMoveTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, int top)
    {
        try
        {
            var roots = PbRootNames(ir);
            ch.Send(Msg.Make(Msg.SetView).Set("pauseTime", true));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 180000);
            Console.WriteLine($"   chains {loaded["physBones"]["chains"].AsInt} bones {loaded["physBones"]["bones"].AsInt}");
            var fpsList = (Environment.GetEnvironmentVariable("E2E_FPS") ?? "60").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var motions = (Environment.GetEnvironmentVariable("E2E_MOTIONS") ?? "sway,jump,turn,walk,vrwalk,snap").Split(',');
            var summary = new List<string>();
            if (Environment.GetEnvironmentVariable("E2E_TIME") == "1")
            {
                // 계산 시간: 걷다 멈추는 움직임 20초(60fps 1200프레임)를 바로 돌리는 데 걸린 시간 ÷ 프레임 수 (파이프 왕복·통계 포함이라 조금 크게 나온다)
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("reset", true).Set("advance", 1));
                PbPeaks(ch, wait, roots.Take(1));
                foreach (var (fps, motion) in new[] { (60, "vrwalk"), (30, "vrwalk"), (60, "none") })
                {
                    if (motion == "none") { ch.Send(Msg.Make(Msg.SetPhysBones).Set("advance", 4)); PbPeaks(ch, wait, roots.Take(1)); }   // 가라앉힌 뒤부터
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 5; i++) ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", motion).Set("advance", 4).Set("advanceFps", fps));
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true));
                    var st = wait(Msg.PhysBoneStats, 30000);
                    var line = $"계산 시간 {fps}fps {(motion == "none" ? "가만히" : "걷다 멈추기")}: 프레임마다 {sw.Elapsed.TotalMilliseconds / (20 * fps):0.000} ms ({20 * fps}프레임) · Tick {st["tickMs"].AsDouble:0.000} ms ({st["tickParts"].AsString})";
                    Console.WriteLine("== " + line); summary.Add(line);
                }
            }
            foreach (var motion in motions)
            {
                if (motion == "none") continue;
                var perFps = new Dictionary<double, Dictionary<string, (double move, double up, double far)>>();
                foreach (var fps in fpsList)
                {
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("reset", true).Set("advance", 3).Set("advanceFps", fps));
                    var rest = PbPeaks(ch, wait, roots);
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", motion).Set("resetPeaks", true).Set("advance", 3.6).Set("advanceFps", fps));
                    var peak = PbPeaks(ch, wait, roots);            // 움직임이 막 끝난 때까지의 최대
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("advance", 3).Set("advanceFps", fps));
                    var after = PbPeaks(ch, wait, roots);
                    perFps[fps] = peak;
                    var stuck = after.Where(kv => rest.TryGetValue(kv.Key, out var r) && Math.Abs(kv.Value.move - r.move) > 0.5).OrderByDescending(kv => kv.Value.move).ToList();
                    var line = $"{motion,-6} {fps,3}fps: 가장 멀리 평균 {peak.Values.Average(v => v.far):0.00} · 최대 {peak.Values.Max(v => v.far):0.0} cm · 3 cm 넘게 뜬 관절 {peak.Values.Count(v => v.up > 3)} (최대 {peak.Values.Max(v => v.up):0.0} cm) · 제자리로 안 돌아온 관절 {stuck.Count}";
                    Console.WriteLine("== " + line); summary.Add(line);
                    foreach (var kv in peak.OrderByDescending(kv => kv.Value.up).Take(top))
                        Console.WriteLine($"   위로 {kv.Value.up,5:0.0} · 멀리 {kv.Value.far,5:0.0} cm  {kv.Key}");
                    foreach (var kv in stuck.Take(8)) Console.WriteLine($"   안 돌아옴: {kv.Key} 지금 {kv.Value.move:0.0} cm (처음 {rest[kv.Key].move:0.0})");
                    File.WriteAllLines(Path.Combine(outDir, $"pbmove_{motion}_{fps}.txt"), peak.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\t{kv.Value.up:0.0}\t{kv.Value.far:0.0}"));
                }
                // 프레임률끼리: 관절마다 가장 멀리 벗어난 거리의 차이 (첫 프레임률 기준)
                if (fpsList.Length > 1)
                {
                    var a = perFps[fpsList[0]];
                    foreach (var fps in fpsList.Skip(1))
                    {
                        var b = perFps[fps];
                        var diffs = a.Where(kv => b.ContainsKey(kv.Key) && kv.Value.far > 0.5).Select(kv => b[kv.Key].far / kv.Value.far).ToList();
                        if (diffs.Count == 0) continue;
                        var line = $"{motion,-6} {fps}fps ÷ {fpsList[0]}fps 가장 멀리 벗어난 거리 비율: 평균 {diffs.Average():0.00} · 가운데 {diffs.OrderBy(x => x).ElementAt(diffs.Count / 2):0.00} · 범위 {diffs.Min():0.00}~{diffs.Max():0.00} (관절 {diffs.Count})";
                        Console.WriteLine("== " + line); summary.Add(line);
                    }
                }
            }
            File.WriteAllLines(Path.Combine(outDir, "pbmove_summary.txt"), summary);
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>PhysBone이 자리 잡게 둔다 (초). 렌더러가 바로 돌리기를 알면(E2E_ADVANCE=1) 기다리지 않고 그만큼 돌린다.</summary>
    static void PbSettle(PipeChannel ch, double seconds)
    {
        if (Environment.GetEnvironmentVariable("E2E_ADVANCE") == "1") { ch.Send(Msg.Make(Msg.SetPhysBones).Set("advance", seconds)); Thread.Sleep(200); }
        else Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }
}
