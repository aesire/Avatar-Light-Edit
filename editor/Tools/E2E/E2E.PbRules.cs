// E2E "pbrules" (2026-11-01): PhysBone 값의 규칙이 VRChat 문서대로인지 작은 시험 체인으로 본다
//   Stretch Motion(0이면 움직임으로 안 늘어남, 잡으면 늘어남, 놓으면 돌아옴) · Version(1.0 Gravity는 Pull 없이도 처지고 1.1은 Pull이 있어야 함, 1.0은 움직임으로 안 늘어남)
//   꺼진 오브젝트(돌지 않음, Reset When Disabled면 쉬는 자세로) · Is Animated(밖에서 바꾼 뼈 값이 쉬는 자세가 됨)
// 주어진 IR에 체인을 더한 사본을 출력 폴더에 만들어 불러온다 (버퍼 파일도 복사).
// 실행: dotnet bin/E2E.dll <렌더러> sample/sample.ir.json <출력> pbrules
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
    static int PbRulesTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        try
        {
            var root = JNode.Parse(File.ReadAllText(ir));
            var nodes = root["nodes"]; var bones = JNode.Arr();
            int Node(string name, int parent, float x, float y, bool active = true)
            {
                int id = nodes.Count;
                nodes.Add(JNode.Obj().Set("id", id).Set("name", name).Set("parent", parent).Set("position", JNode.FloatArray(new[] { x, y, 0f })).Set("active", active));
                return id;
            }
            var mid = new Dictionary<string, int>(); var comps = new Dictionary<string, int>();
            int slot = 0;
            void Chain(string name, Action<JNode> values, bool compActive = true)
            {
                float x = 3 + slot++ * 0.5f;   // 아바타 옆에 줄지어 (서로 만나지 않게)
                // 살짝 기울여 둔다 (곧게 선 뼈는 아래로 미는 힘이 뼈 방향이라 돌지 않는다)
                int r = Node(name, 1, x, 1), m = Node(name + "_1", r, 0.02f, 0.1f); Node(name + "_2", m, 0.02f, 0.1f);
                int comp = Node(name + "_comp", 1, x, 0, compActive);
                mid[name] = m; comps[name] = comp;
                var v = JNode.Obj().Set("pull", 0.2).Set("spring", 0.5).Set("version", 1);
                values(v);
                bones.Add(JNode.Obj().Set("id", bones.Count).Set("node", comp).Set("root", r).Set("enabled", true).Set("values", v).Set("vectors", JNode.Obj()).Set("curves", JNode.Obj()));
            }
            Chain("StretchM0", v => v.Set("maxStretch", 1).Set("stretchMotion", 0));
            Chain("StretchM1", v => v.Set("maxStretch", 1).Set("stretchMotion", 1));
            Chain("V10Stretch", v => v.Set("maxStretch", 1).Set("maxSquish", 0.5).Set("stretchMotion", 1).Set("version", 0));
            Chain("GravV11NoPull", v => v.Set("gravity", 1).Set("pull", 0));
            Chain("GravV10NoPull", v => v.Set("gravity", 1).Set("pull", 0).Set("version", 0));
            Chain("OffKeep", v => v.Set("gravity", 0.5));
            Chain("OffReset", v => v.Set("gravity", 0.5).Set("resetWhenDisabled", 1));
            Chain("Dormant", v => v.Set("gravity", 0.5), compActive: false);
            Chain("Anim", v => v.Set("isAnimated", 1));
            Chain("NoAnim", v => v.Set("isAnimated", 0));
            Chain("GrabStretch", v => v.Set("maxStretch", 1).Set("stretchMotion", 0));
            Chain("GrabFixed", v => v.Set("maxStretch", 0));
            root.Set("physBones", bones);
            string copy = Path.Combine(outDir, "pbrules.ir.json");
            File.WriteAllText(copy, root.ToJson());
            File.Copy(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ir))!, root["buffers"].AsString), Path.Combine(outDir, root["buffers"].AsString), true);

            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", copy));
            var loaded = wait(Msg.SceneLoaded, 60000);
            Check(loaded["physBones"]["chains"].AsInt == bones.Count, $"chains {loaded["physBones"]["chains"].AsInt} (Dormant 포함 {bones.Count})");

            // 체인 끝 관절 (시험 체인은 곧은 두 마디)
            (float len, float[] at, bool active) End(string name)
            {
                var names = JNode.Arr().Add(name);
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("stats", true).Set("describe", names));
                var lines = wait(Msg.PhysBoneStats, 10000)["detail"].Items.Select(l => l.AsString).ToList();
                int head = lines.FindIndex(l => l.StartsWith("chain " + name + " "));
                var last = Regex.Match(lines[head + 2], @"len ([\d.]+) at ([-\d.]+),([-\d.]+),([-\d.]+)");
                float F(int g) => float.Parse(last.Groups[g].Value, CultureInfo.InvariantCulture);
                return (F(1), new[] { F(2), F(3), F(4) }, lines[head].Contains("active True"));
            }
            float Dist(float[] a, float[] b) => (float)Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());

            // 쉬는 자리
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false)); Thread.Sleep(300);
            var rest = new Dictionary<string, float[]>();
            foreach (var n in mid.Keys) rest[n] = End(n).at;
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("weight", 1)); Thread.Sleep(2000);

            // Version과 Gravity
            Check(Dist(End("GravV11NoPull").at, rest["GravV11NoPull"]) < 0.001f, "Version 1.1: Pull이 0이면 Gravity로 처지지 않음");
            float fell = Dist(End("GravV10NoPull").at, rest["GravV10NoPull"]);
            Check(fell > 0.02f, $"Version 1.0: Pull이 0이어도 Gravity로 처짐 ({fell * 100:0.0} cm)");

            // 꺼진 오브젝트
            var dormant = End("Dormant");
            Check(!dormant.active && Dist(dormant.at, rest["Dormant"]) < 0.001f, "처음부터 꺼진 오브젝트의 PhysBone은 돌지 않음");
            var bentKeep = End("OffKeep").at; var bentReset = End("OffReset").at;
            Check(Dist(bentKeep, rest["OffKeep"]) > 0.01f && Dist(bentReset, rest["OffReset"]) > 0.01f, "켜져 있을 때는 Gravity로 처짐");
            foreach (var n in new[] { "OffKeep", "OffReset" }) ch.Send(Msg.Make(Msg.SetNodeActive).Set("node", comps[n]).Set("active", false));
            Thread.Sleep(300);
            Check(Dist(End("OffKeep").at, bentKeep) < 0.002f, "끄면 그 자리에 남음");
            Check(Dist(End("OffReset").at, rest["OffReset"]) < 0.001f, "Reset When Disabled: 끄면 쉬는 자세로");
            foreach (var n in new[] { "OffKeep", "OffReset" }) ch.Send(Msg.Make(Msg.SetNodeActive).Set("node", comps[n]).Set("active", true));
            Thread.Sleep(1500);
            Check(Dist(End("OffReset").at, bentReset) < 0.005f, "다시 켜면 다시 돎");

            // Is Animated: 가운데 뼈를 Z축으로 45° (로컬 위치·크기는 그대로)
            var trs = JNode.FloatArray(new[] { 0.02f, 0.1f, 0f, 0f, 0f, 0.38268f, 0.92388f, 1f, 1f, 1f });
            foreach (var n in new[] { "Anim", "NoAnim" }) ch.Send(Msg.Make(Msg.SetNodeTransform).Set("node", mid[n]).Set("trs", trs));
            Thread.Sleep(1500);
            float anim = Dist(End("Anim").at, rest["Anim"]), noAnim = Dist(End("NoAnim").at, rest["NoAnim"]);
            Check(anim > 0.03f, $"Is Animated: 밖에서 바꾼 뼈 값을 따라감 ({anim * 100:0.0} cm)");
            Check(noAnim < 0.002f, $"Is Animated 끔: 밖에서 바꾼 뼈 값을 덮어씀 ({noAnim * 100:0.00} cm)");

            // Stretch Motion: 움직임 중 가장 길어진 값
            float M0 = 0, M1 = 0, V10 = 0;
            foreach (var motion in new[] { "jump", "vrwalk" })
            {
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("motion", motion));
                for (int i = 0; i < 12; i++)
                {
                    Thread.Sleep(100);
                    M0 = Math.Max(M0, End("StretchM0").len); M1 = Math.Max(M1, End("StretchM1").len); V10 = Math.Max(V10, End("V10Stretch").len);
                }
                Thread.Sleep(3500);
            }
            Check(M0 < 1.002f, $"Stretch Motion 0: 움직임으로 안 늘어남 (최대 ×{M0:0.000})");
            Check(M1 > M0 + 0.01f, $"Stretch Motion 1: 움직임으로 늘어남 (최대 ×{M1:0.000})");
            Check(V10 < 1.002f, $"Version 1.0: Stretch Motion이 있어도 움직임으로 안 늘어남 (최대 ×{V10:0.000})");

            // 잡아서 늘이기: 곧게 선 체인(길이 20 cm) 끝을 30 cm 위로
            foreach (var n in new[] { "GrabStretch", "GrabFixed" })
            {
                ch.Send(Msg.Make(Msg.SetPhysBones).Set("testGrab", n).Set("testGrabY", 0.3)); Thread.Sleep(800);
                var held = End(n).len;
                if (n == "GrabStretch")
                {
                    Check(held > 1.2f, $"잡으면 Max Stretch까지 늘어남 (×{held:0.00})");
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("testGrab", "")); Thread.Sleep(30);   // 두세 프레임 뒤 (0.1초면 이미 거의 돌아와 있다)
                    var soon = End(n).len; Thread.Sleep(1500);
                    var back = End(n).len;
                    Check(soon > 1.02f && back < 1.005f, $"놓으면 한 번에 튀지 않고 돌아옴 (0.03초 ×{soon:0.00} → 1.5초 ×{back:0.000})");
                }
                else
                {
                    Check(held < 1.002f, $"Max Stretch 0이면 잡아도 안 늘어남 (×{held:0.000})");
                    ch.Send(Msg.Make(Msg.SetPhysBones).Set("testGrab", ""));
                }
            }
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
