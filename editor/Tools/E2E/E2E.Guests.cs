// E2E "guests" (2026-10-25): 함께 세운 아바타 (렌더러 GuestAvatars)
//   같은 IR을 지금 아바타 + 함께 세운 아바타 둘로 → guestsState ok·휴머노이드 · 화면에 더 보임 · 핸들 화살표로 옮기기 → guestMoved ·
//   지금 아바타 자세 복사 → guestPoseEdited · 목록 자세 처음으로 · 하나 빼기 · 모두 빼면 처음 화면과 같음 · 깨끗한 종료.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> guests
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class E2E
{
    static int GuestsTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        void Shot(string file) { Thread.Sleep(250); Capture(ch, wait, P(file), false); }
        JNode Entry(string id, float x, JNode? pose = null)
        {
            var o = JNode.Obj().Set("id", id).Set("path", ir).Set("pos", JNode.FloatArray(new[] { x, 0f, 0f })).Set("rot", JNode.FloatArray(new[] { 0f, 0f, 0f, 1f })).Set("scale", 1f).Set("visible", true);
            if (pose != null) o.Set("pose", pose);
            return o;
        }
        JNode Send(params JNode[] list)
        {
            var arr = JNode.Arr(); foreach (var e in list) arr.Add(e);
            ch.Send(Msg.Make(Msg.Guests).Set("list", arr));
            return wait(Msg.GuestsState, 60000);
        }
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "front")); ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            // 세 아바타가 다 들어오게 정면 멀리서 (아바타는 +Z를 본다)
            var lo = loaded["bounds"]["min"].ToFloatArray(); var hi = loaded["bounds"]["max"].ToFloatArray();
            float h = Math.Max(0.5f, hi[1] - lo[1]);
            ch.Send(Msg.Make(Msg.SetCamera).Set("state", JNode.Obj().Set("pivot", JNode.FloatArray(new[] { 0f, h * 0.5f, 0f }))
                .Set("yaw", 180f).Set("pitch", 0f).Set("distance", h * 2.2f + 2f).Set("fov", 40f)));
            Shot("guests_00_alone.png");

            var st = Send(Entry("a", 0.9f), Entry("b", -0.9f));
            var gs = st["guests"].Items.ToList();
            Check(gs.Count == 2 && gs.All(g => g["ok"].AsBool), $"two guests loaded ({string.Join(" · ", gs.Select(g => $"{g["id"].AsString}: ok={g["ok"].AsBool} renderers {g["renderers"].AsInt} height {g["height"].AsDouble:0.00} {g["error"].AsString}"))})");
            bool humanoid = loaded["pose"]["ok"].AsBool;
            if (humanoid) Check(gs.All(g => g["humanoid"].AsBool), "guests are humanoid like the main avatar");
            Shot("guests_01_three.png");
            Check(Diff(P("guests_00_alone.png"), P("guests_01_three.png")) > 0.002, $"guests appear next to the avatar → image changed ({Diff(P("guests_00_alone.png"), P("guests_01_three.png")):P2})");

            // 같은 목록을 다시 보내도 다시 불러오지 않는다 (자리만)
            var again = Send(Entry("a", 1.2f), Entry("b", -0.9f));
            Check(again["guests"].Count == 2, "same list again → still two (moved, not reloaded)");

            // 핸들로 옮기기 (조작 모드)
            ch.Send(Msg.Make(Msg.SetView).Set("poseBones", true).Set("handleSpace", "world"));
            ch.Send(Msg.Make(Msg.Select).Set("kind", "guest").Set("id", "a"));
            JNode Sel(string kind) { for (int i = 0; i < 4; i++) { var m = wait(Msg.Selected, 10000); if (m["kind"].AsString == kind) return m; } return JNode.Obj(); }
            Check(Sel("guest")["kind"].AsString == "guest", "select guest → selected guest");
            ch.Send(Msg.Make(Msg.MoveTarget).Set("arrow", "z").Set("amount", 0.5f));
            var mv = wait(Msg.GuestMoved, 10000);
            var p = mv["pos"].ToFloatArray();
            Check(mv["id"].AsString == "a" && Math.Abs(p[0] - 1.2f) < 0.01f && Math.Abs(p[2] - 0.5f) < 0.01f, $"guest arrow z 0.5 → guestMoved a [{string.Join(", ", p.Select(v => v.ToString("0.###")))}]");
            ch.Send(Msg.Make(Msg.MoveTarget).Set("ring", "y").Set("angle", 90f));
            var rt = wait(Msg.GuestMoved, 10000);
            var q = rt["rot"].ToFloatArray();
            Check(Math.Abs(Math.Abs(q[1]) - 0.7071f) < 0.01f && Math.Abs(rt["pos"].ToFloatArray()[0] - 1.2f) < 0.01f, $"guest ring y 90° around its feet → rot y {q[1]:0.###}, x kept");
            ch.Send(Msg.Make(Msg.Select).Set("kind", ""));
            var aMoved = Entry("a", 0).Set("pos", rt["pos"]).Set("rot", rt["rot"]);   // Host처럼 옮긴 자리를 목록에 적는다

            if (humanoid)
            {
                // 지금 아바타 자세 → 함께 세운 아바타로 복사
                var sit = PoseValue.Make(new System.Collections.Generic.Dictionary<string, float> { ["Left Arm Down-Up"] = 0.6f, ["Right Arm Down-Up"] = -0.6f });
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", sit));
                Shot("guests_02_main_posed.png");
                ch.Send(Msg.Make(Msg.Guests).Set("copyPose", "b"));
                var pe = wait(Msg.GuestPoseEdited, 10000);
                wait(Msg.GuestsState, 10000);
                float M(JNode pose, string n) => PoseValue.MusclesOf(pose).TryGetValue(n, out var v) ? v : 0;
                Check(pe["id"].AsString == "b" && Math.Abs(M(pe["pose"], "Left Arm Down-Up") - 0.6f) < 0.02f, $"copy pose → guestPoseEdited b (Left Arm Down-Up {M(pe["pose"], "Left Arm Down-Up"):0.##})");
                Shot("guests_03_copied.png");
                Check(Diff(P("guests_02_main_posed.png"), P("guests_03_copied.png")) > 0.002, "guest b takes the pose → image changed");
                // 목록에 자세를 실어 보내면 그대로, 빼면 처음으로
                Send(aMoved, Entry("b", -0.9f, pe["pose"]));
                Shot("guests_04_listpose.png");
                Check(Diff(P("guests_03_copied.png"), P("guests_04_listpose.png")) < 0.002, "list with the same pose → no change");
                ch.Send(Msg.Make(Msg.SetPose).Set("pose", JNode.Null));
            }

            Send(Entry("b", -0.9f));
            Shot("guests_05_one.png");
            Send();
            Shot("guests_06_none.png");
            Check(Diff(P("guests_00_alone.png"), P("guests_06_none.png")) < 0.002, $"remove all → same as alone ({Diff(P("guests_00_alone.png"), P("guests_06_none.png")):P3})");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Check(proc.HasExited, "renderer quits cleanly");
        Console.WriteLine(_fail == 0 ? "E2E guests: all ok" : $"E2E guests: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
