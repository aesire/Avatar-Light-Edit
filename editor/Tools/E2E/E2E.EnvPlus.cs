// E2E "envplus" (2026-10-23): 환경 Provider 추가분과 촬영 추가분
//   추가 조명(Unity 포인트 라이트) · 라이트 볼륨(lilToon 전역 값) → 아바타가 바뀌고 끄면 처음 화면,
//   VRChat 시점(_VRChatMirrorMode) · AudioLink(_AudioTexture)는 켰을 때 렌더러가 멀쩡한지 (화면 차이는 정보),
//   스튜디오 그라데이션 배경 → 위·아래 모서리 색, 그림자 바닥 → 투명 촬영에 반투명 픽셀(그림자)이 생김.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> envplus
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VrcProto.Protocol;

static partial class E2E
{
    static int EnvPlusTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        string P(string n) => Path.Combine(outDir, n);
        JNode Light(Func<JNode, JNode>? f = null) => JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", (f ?? (p => p))(JNode.Obj().Set("preset", "bright")));
        JNode Prov(string id, JNode p) => JNode.Obj().Set("provider", id).Set("version", 1).Set("params", p);
        void Env(string file, params JNode[] providers)
        {
            var arr = JNode.Arr(); foreach (var p in providers) arr.Add(p);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", arr));
            Thread.Sleep(400);
            Capture(ch, wait, P(file), false);
        }
        JNode Studio(JNode m) { ch.Send(m.Set("t", Msg.Studio)); return wait(Msg.StudioState, 20000); }
        // 앞쪽 가까이 둔 진한 빨강 포인트 조명 (슬롯 1만)
        JNode RedSlot() => JNode.Obj().Set("l1_on", true).Set("l1_type", "point").Set("l1_color", JNode.FloatArray(new[] { 1f, 0.1f, 0.1f }))
            .Set("l1_intensity", 3).Set("l1_range", 5).Set("l1_around", 0).Set("l1_height", 1.0).Set("l1_distance", 0.8);
        try
        {
            ch.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", false));
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            var loaded = wait(Msg.SceneLoaded, 60000);
            bool lil = loaded["materials"].Items.Any(m => m["shader"].AsString.Contains("lilToon"));
            ch.Send(Msg.Make(Msg.SetCamera).Set("preset", "full"));
            Env("ep_00_default.png", Light());

            Env("ep_01_point_light.png", Light(), Prov("Lights", RedSlot()));
            double d = AvatarDiff(P("ep_00_default.png"), P("ep_01_point_light.png"));
            Check(d > 0.01, $"추가 조명 (빨간 포인트 라이트) → avatar pixels changed ({d:P2})");
            Env("ep_01b_spot_light.png", Light(), Prov("Lights", RedSlot().Set("l1_type", "spot").Set("l1_angle", 30).Set("l1_aim", 1.0)));
            Check(Diff(P("ep_01_point_light.png"), P("ep_01b_spot_light.png")) > 0.001, "spot light differs from point light");

            Env("ep_02_light_volume.png", Light(), Prov("VRCLightVolumes", RedSlot()));
            d = AvatarDiff(P("ep_00_default.png"), P("ep_02_light_volume.png"));
            if (lil) Check(d > 0.01, $"라이트 볼륨 (빨간 포인트 볼륨) → lilToon avatar pixels changed ({d:P2})");
            else Console.WriteLine($"   light volume: {d:P2} avatar pixels changed (lilToon 머티리얼 없음)");
            Env("ep_02b_volume_spot.png", Light(), Prov("VRCLightVolumes", RedSlot().Set("l1_type", "spot").Set("l1_angle", 30)));
            Console.WriteLine($"   light volume spot vs point: {Diff(P("ep_02_light_volume.png"), P("ep_02b_volume_spot.png")):P2} pixels changed");
            Env("ep_03_back_to_default.png", Light());
            Check(Diff(P("ep_00_default.png"), P("ep_03_back_to_default.png")) < 0.001, "removing lights / light volumes restores the default image");

            Env("ep_04_mirror_mode.png", Light(), Prov("VRChatGlobals", JNode.Obj().Set("mirrorMode", "1").Set("cameraMode", "0")));
            Console.WriteLine($"   _VRChatMirrorMode 1: {AvatarDiff(P("ep_00_default.png"), P("ep_04_mirror_mode.png")):P2} avatar pixels changed");
            Env("ep_05_audiolink.png", Light(), Prov("AudioLink", JNode.Obj().Set("source", "pattern").Set("bpm", 120)));
            Thread.Sleep(500);
            Capture(ch, wait, P("ep_05b_audiolink_later.png"), false);
            Console.WriteLine($"   AudioLink pattern: {AvatarDiff(P("ep_00_default.png"), P("ep_05_audiolink.png")):P2} avatar pixels vs default, {Diff(P("ep_05_audiolink.png"), P("ep_05b_audiolink_later.png")):P2} changed after 0.5 s");
            Env("ep_06_default_again.png", Light());
            Check(Diff(P("ep_00_default.png"), P("ep_06_default_again.png")) < 0.001, "removing VRChat globals / AudioLink restores the default image");

            // 스튜디오 그라데이션: 위 빨강, 아래 파랑
            var st = Studio(JNode.Obj().Set("enabled", true).Set("exposure", 1f)
                .Set("background", JNode.Obj().Set("mode", "gradient").Set("top", JNode.FloatArray(new[] { 1f, 0f, 0f })).Set("bottom", JNode.FloatArray(new[] { 0f, 0f, 1f }))));
            Check(st["background"].AsString == "gradient", $"gradient background set {st["error"].AsString}");
            Thread.Sleep(250);
            Capture(ch, wait, P("ep_07_gradient.png"), false);
            {
                var (w, h, px) = DecodePng(P("ep_07_gradient.png")); int c = px.Length / (w * h);
                int top = 2 * w * c, bottom = (h - 3) * w * c;
                Check(px[top] > 200 && px[top + 2] < 60, $"gradient top-left is red ({px[top]}, {px[top + 1]}, {px[top + 2]})");
                Check(px[bottom + 2] > 200 && px[bottom] < 60, $"gradient bottom-left is blue ({px[bottom]}, {px[bottom + 1]}, {px[bottom + 2]})");
            }
            Studio(JNode.Obj().Set("background", JNode.Obj().Set("mode", "none")));

            // 그림자 바닥: 머리 위에서 비추고 투명 촬영 → 바닥에 반투명(그림자) 픽셀
            Env("ep_08_top_sun.png", Light(p => p.Set("pitch", 35)));
            Capture(ch, wait, P("ep_08b_transparent_nofloor.png"), true);
            Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("strength", 0.8f)));
            Thread.Sleep(250);
            Capture(ch, wait, P("ep_09_transparent_floor.png"), true);
            Capture(ch, wait, P("ep_09b_floor_opaque.png"), false);
            int Partial(string f)
            {
                var (w, h, px) = DecodePng(f); int c = px.Length / (w * h), n = 0;
                if (c < 4) return 0;
                // 아래쪽 1/4만 (발밑)
                for (int y = h * 3 / 4; y < h; y++) for (int x = 0; x < w; x++) { int a = px[(y * w + x) * c + 3]; if (a > 10 && a < 245) n++; }
                return n;
            }
            int before = Partial(P("ep_08b_transparent_nofloor.png")), after = Partial(P("ep_09_transparent_floor.png"));
            Check(after > before + 200, $"shadow floor → semi-transparent shadow pixels under the feet ({before} → {after})");
            Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", false)));
            Thread.Sleep(250);
            Capture(ch, wait, P("ep_10_transparent_floor_off.png"), true);
            Check(Math.Abs(Partial(P("ep_10_transparent_floor_off.png")) - before) < 50, "shadow floor off → back to no floor shadow");

            // 단색 바닥 (2026-10-27): 빨강 바닥 → 발밑 아래 모서리가 빨강, 투명 촬영에도 바닥은 불투명, 위 모서리는 그대로 투명, 끄면 처음 화면
            Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("mode", "solid").Set("strength", 0.8f).Set("color", JNode.FloatArray(new[] { 0.9f, 0.1f, 0.1f }))));
            Thread.Sleep(250);
            Capture(ch, wait, P("ep_11_solid_floor.png"), false);
            Capture(ch, wait, P("ep_11b_solid_floor_transparent.png"), true);
            {
                var (w, h, px) = DecodePng(P("ep_11_solid_floor.png")); int c = px.Length / (w * h);
                int bl = ((h - 3) * w + 2) * c;
                Check(px[bl] > px[bl + 1] + 60 && px[bl] > px[bl + 2] + 60, $"solid floor bottom-left is red ({px[bl]}, {px[bl + 1]}, {px[bl + 2]})");
                var (tw, th, tp) = DecodePng(P("ep_11b_solid_floor_transparent.png")); int tc = tp.Length / (tw * th);
                int tbl = ((th - 3) * tw + 2) * tc;
                Check(tc == 4 && tp[tbl + 3] > 240 && tp[3] == 0, $"transparent capture: floor opaque (alpha {(tc == 4 ? tp[tbl + 3] : -1)}), top corner clear (alpha {(tc == 4 ? tp[3] : -1)})");
                // 그림자: 같은 바닥에서 그림자 진하기 0 → 발밑이 밝아진다
                Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", true).Set("mode", "solid").Set("strength", 0f).Set("color", JNode.FloatArray(new[] { 0.9f, 0.1f, 0.1f }))));
                Thread.Sleep(250);
                Capture(ch, wait, P("ep_12_solid_noshadow.png"), false);
                double sd = Diff(P("ep_11_solid_floor.png"), P("ep_12_solid_noshadow.png"));
                Check(sd > 0.002, $"solid floor receives the avatar shadow ({sd:P2} pixels change with shadow strength 0)");
            }
            Studio(JNode.Obj().Set("shadowFloor", JNode.Obj().Set("enabled", false)));
            Thread.Sleep(250);
            Env("ep_13_solid_off.png", Light(p => p.Set("pitch", 35)));
            Check(Diff(P("ep_08_top_sun.png"), P("ep_13_solid_off.png")) < 0.001, "solid floor off → same as before");
            ch.Send(Msg.Make(Msg.Quit));
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.Message); _fail++; }
        if (!proc.WaitForExit(10000)) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E passed" : $"E2E: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
