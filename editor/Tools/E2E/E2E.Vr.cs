// E2E "vrlink" (2026-10-23): SteamVR 연결 0단계 (렌더러 VrLink)
//   켜기 → vrStatus(wanted, 상태 off가 아님) · 상태 다시 요청 → 같은 상태 · 끄기 → off · 빠르게 켜고 끄기를 되풀이해도 렌더러가 살아 있음.
//   SteamVR이 꺼져 있으면 "기다리는 중", 켜져 있고 헤드셋이 있으면 "연결됨"과 장치 목록 (5번째 인자 steamvr면 연결됨을 요구).
//   VR로 보기(launch: false): SteamVR 없이 기다리는 중 · 그동안 촬영이 되고 끄면 연결만 남음. steamvr면 보기 시작 · 4초 뒤 30 fps 이상 제출 · 오류 없음.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> vrlink [steamvr]
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using VrcProto.Host.Core;

static partial class E2E
{
    static int VrLinkTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc, bool needSteamVr)
    {
        JNode Vr(JNode m) { ch.Send(m.Set("t", Msg.Vr)); return wait(Msg.VrStatus, 15000); }
        void Drain() { try { while (true) wait(Msg.VrStatus, 400); } catch (TimeoutException) { } }   // 쌓인 상태는 버린다
        try
        {
            var st = Vr(JNode.Obj().Set("enabled", true));
            Console.WriteLine($"   first status: {st["state"].AsString} · {st["text"].AsString}");
            Check(st["wanted"].AsBool && st["state"].AsString != "off", $"vr on → status {st["state"].AsString}");
            // 기다리는 중이면 연결 시도 결과(연결됨·기다리는 중·오류)가 한 번 더 올 수 있다
            if (st["state"].AsString == "waiting" && st["text"].AsString.Contains("연결하는 중"))
            {
                st = wait(Msg.VrStatus, 15000);
                Console.WriteLine($"   after try: {st["state"].AsString} · {st["text"].AsString}");
            }
            Check(st["state"].AsString is "waiting" or "ok", $"connect attempt reports waiting or ok ({st["state"].AsString}: {st["text"].AsString})");
            foreach (var d in st["devices"].Items)
                Console.WriteLine($"   device {d["index"].AsInt}: {d["kind"].AsString} {d["role"].AsString} {d["maker"].AsString} {d["model"].AsString} tracked={d["tracked"].AsBool}");
            if (needSteamVr)
            {
                Check(st["state"].AsString == "ok", "SteamVR connected");
                Check(st["devices"].Items.Any(d => d["kind"].AsString == "hmd"), "headset listed");
            }
            var again = Vr(JNode.Obj().Set("status", true));
            Check(again["state"].AsString == st["state"].AsString, $"status request repeats the state ({again["state"].AsString})");

            // 연결된 동안은 장치의 추적 여부가 바뀔 때마다 상태가 오므로, 끄기 전에 쌓인 것을 버리고 off가 올 때까지 본다
            Drain();
            var off = Vr(JNode.Obj().Set("enabled", false));
            for (int i = 0; i < 5 && off["state"].AsString != "off"; i++) off = wait(Msg.VrStatus, 5000);
            Check(off["state"].AsString == "off" && !off["wanted"].AsBool, $"vr off → {off["state"].AsString}");

            // 빠르게 켜고 끄기 (연결 시도 중 끊기 포함)
            for (int i = 0; i < 4; i++)
            {
                ch.Send(Msg.Make(Msg.Vr).Set("enabled", true));
                Thread.Sleep(i * 120);
                ch.Send(Msg.Make(Msg.Vr).Set("enabled", false));
            }
            Thread.Sleep(1500);
            Drain();
            var last = Vr(JNode.Obj().Set("status", true));
            Check(last["state"].AsString == "off", $"after toggling: {last["state"].AsString}");

            // VR로 보기 (1단계). SteamVR이 꺼져 있으면 켜지 않게 launch: false — 기다리는 중이어야 하고 렌더러는 그대로 돈다
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            Drain();
            var vs = Vr(JNode.Obj().Set("view", true).Set("launch", needSteamVr).Set("desktop", "eye"));
            Check(vs["wanted"].AsBool && vs["wantView"].AsBool, "view on → wanted + wantView");
            if (needSteamVr)
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!(vs["state"].AsString == "ok" && vs["view"].AsBool) && DateTime.UtcNow < until) vs = wait(Msg.VrStatus, 30000);
                Check(vs["state"].AsString == "ok" && vs["view"].AsBool, $"VR view running ({vs["text"].AsString})");
                Thread.Sleep(4000);
                Drain();
                var run = Vr(JNode.Obj().Set("status", true));
                Console.WriteLine($"   view: fps {run["fps"].AsInt} · GPU {run["gpuMs"].AsFloat} ms · 재투영 {run["reprojected"].AsInt}% · 놓침 {run["dropped"].AsInt} · {run["error"].AsString}");
                Check(run["fps"].AsInt >= 30 && run["error"].AsString.Length == 0, $"frames submitted ({run["fps"].AsInt} fps)");
                Console.WriteLine($"   input: {run["inputProblem"].AsString}");
                Check(run["inputProblem"].AsString.Length == 0, $"SteamVR input actions loaded {run["inputProblem"].AsString}");
                ch.Send(Msg.Make(Msg.Vr).Set("smoothTurn", true)); Thread.Sleep(300);   // (2026-11-03 아바타 안으로 들어가기 시험은 뺌)
                // 2-3: 월드 거울 + 개인 거울 → 둘 다 켜짐, 반사를 그려도 fps 유지 · 끄면 0
                Drain();
                ch.Send(Msg.Make(Msg.Vr).Set("mirror", true).Set("personalMirror", true).Set("mirrorQuality", 0.75f));
                Thread.Sleep(3000);
                Drain();
                var mir = Vr(JNode.Obj().Set("status", true));
                Console.WriteLine($"   mirrors: {mir["mirrors"].AsInt} · fps {mir["fps"].AsInt} · GPU {mir["gpuMs"].AsFloat} ms · 재투영 {mir["reprojected"].AsInt}% · {mir["error"].AsString}");
                Check(mir["mirrors"].AsInt == 2 && mir["fps"].AsInt >= 30 && mir["error"].AsString.Length == 0, $"mirrors on ({mir["mirrors"].AsInt}, {mir["fps"].AsInt} fps)");
                ch.Send(Msg.Make(Msg.Vr).Set("mirrorSummon", true)); Thread.Sleep(300);
                ch.Send(Msg.Make(Msg.Vr).Set("mirror", false).Set("personalMirror", false)); Thread.Sleep(500);
                Drain();
                var nomir = Vr(JNode.Obj().Set("status", true));
                Check(nomir["mirrors"].AsInt == 0, $"mirrors off ({nomir["mirrors"].AsInt})");
                ch.Send(Msg.Make(Msg.Vr).Set("recenter", true).Set("desktop", "orbit"));
                Thread.Sleep(1000);
                Capture(ch, wait, System.IO.Path.Combine(outDir, "vr_orbit_desktop.png"), false);
                // 3단계 ① VR 에디터 창: 공유 메모리 화면(회색 판) → 렌더러가 받았다고 답하고 오버레이가 하나 뜬다 · 지우면 0
                const int pw = 512, ph = 320;
                using (var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("VrcProtoE2EPanel", pw * ph * 4))
                {
                    using (var view = mm.CreateViewAccessor()) { var px = new byte[pw * ph * 4]; for (int i = 0; i < px.Length; i++) px[i] = (byte)(i % 4 == 3 ? 255 : 90); view.WriteArray(0, px, 0, px.Length); }
                    ch.Send(Msg.Make(Msg.VrPanel).Set("id", "e2e").Set("map", "VrcProtoE2EPanel").Set("width", pw).Set("height", ph).Set("frame", 1).Set("meters", 0.8f).Set("show", true));
                    var ack = wait(Msg.VrPanelShown, 5000);
                    Check(ack["id"].AsString == "e2e" && ack["frame"].AsInt == 1, "editor panel frame acknowledged");
                    Thread.Sleep(1000);
                    Drain();
                    var pan = Vr(JNode.Obj().Set("status", true));
                    Console.WriteLine($"   panels: {pan["panels"].AsInt} {pan["panelProblem"].AsString}");
                    Check(pan["panels"].AsInt == 1 && pan["panelProblem"].AsString.Length == 0, $"editor panel overlay shown ({pan["panels"].AsInt}) {pan["panelProblem"].AsString}");
                    ch.Send(Msg.Make(Msg.VrPanel).Set("id", "e2e").Set("remove", true));
                    Thread.Sleep(500);
                }
            }
            else
            {
                if (vs["text"].AsString.Contains("준비하는") || vs["text"].AsString.Contains("연결하는")) vs = wait(Msg.VrStatus, 15000);
                Check(vs["state"].AsString == "waiting" && !vs["view"].AsBool, $"view without SteamVR waits ({vs["text"].AsString})");
                // VR 에디터 창 (3단계): SteamVR 없이도 화면을 받아 답하고, 창 아래 막대·손목 목록 그림을 그린다 (위 = 빨강, 아래 = 파랑인 창 두 개)
                const int pw = 320, ph = 200;
                using (var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("VrcProtoE2EPanel2", pw * ph * 4))
                {
                    using (var view = mm.CreateViewAccessor())
                    {
                        var px = new byte[pw * ph * 4];
                        for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) { int i = (y * pw + x) * 4; bool top = y < ph / 2; px[i] = (byte)(top ? 40 : 230); px[i + 1] = 40; px[i + 2] = (byte)(top ? 230 : 40); px[i + 3] = 255; }   // BGRA
                        view.WriteArray(0, px, 0, px.Length);
                    }
                    foreach (var id in new[] { "main", "float.e2e" })
                    {
                        ch.Send(Msg.Make(Msg.VrPanel).Set("id", id).Set("map", "VrcProtoE2EPanel2").Set("width", pw).Set("height", ph).Set("frame", 1).Set("meters", 0.8f));
                        var ack = wait(Msg.VrPanelShown, 5000);
                        Check(ack["id"].AsString == id, $"editor panel frame acknowledged without SteamVR ({id})");
                    }
                    var dumpDir = System.IO.Path.Combine(outDir, "vrpanels");
                    ch.Send(Msg.Make(Msg.VrPanel).Set("id", "*").Set("dump", dumpDir));
                    Thread.Sleep(1500);
                    foreach (var f in new[] { "vr_chrome_icons.png", "vr_chrome_icons_hover.png", "vr_chrome_handle.png", "vr_chrome_handle_hot.png", "vr_chrome_grip.png" })
                        Check(System.IO.File.Exists(System.IO.Path.Combine(dumpDir, f)), "drawn " + f);
                    foreach (var id in new[] { "main", "float.e2e" }) ch.Send(Msg.Make(Msg.VrPanel).Set("id", id).Set("remove", true));
                }
                ch.Send(Msg.Make(Msg.Vr).Set("recenter", true).Set("desktop", "orbit").Set("mirror", true).Set("personalMirror", true).Set("mirrorSummon", true));   // 보는 중이 아니면 아무 일도 없어야 한다
                Thread.Sleep(500);
                Capture(ch, wait, System.IO.Path.Combine(outDir, "vr_waiting_capture.png"), false);
                Check(!proc.HasExited, "renderer alive while waiting for SteamVR (capture works)");
                // VR 카메라 (8단계 1차): 헤드셋 없이 미리보기 카메라 자리에서 한 장 → PNG(크기 맞음, 까맣지 않음) + 모형 그림
                var camDir = System.IO.Path.Combine(outDir, "vrcamera");
                // 영상도 (2026-10-25): Host처럼 녹화 파이프를 열어 두고 이름을 주면 사진 다음에 1.2초 녹화 → MP4
                var camMp4 = System.IO.Path.Combine(outDir, "vr_camera_video.mp4");
                var camJob = RecordJob.Pipe(camMp4);
                string? camJobErr = null; var camJobDone = new ManualResetEventSlim();
                camJob.Finished = e => { camJobErr = e; camJobDone.Set(); };
                Check(Mp4Writer.WaitWarm(30000), "MP4 encoder warmed up before the shutter");   // 실제 사용: 영상 모드를 켜고 한참 뒤에 셔터
                ch.Send(Msg.Make(Msg.Vr).Set("cameraWidth", 1280).Set("cameraHeight", 720).Set("cameraPipe", camJob.PipeName).Set("cameraTest", camDir));
                JNode shot = JNode.Null, vStart = JNode.Null, vStop = JNode.Null;
                try
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while ((shot.IsNull || vStop.IsNull) && DateTime.UtcNow < until)
                    {
                        var m = wait(Msg.VrCameraShot + "|" + Msg.VrCameraVideo, 20000);
                        if (Msg.Type(m) == Msg.VrCameraShot) shot = m;
                        else if (m["started"].AsBool) vStart = m;
                        else if (m["stopped"].AsBool) vStop = m;
                    }
                }
                catch (TimeoutException) { }
                Check(vStart["started"].AsBool && vStart["width"].AsInt == 1920 && vStart["height"].AsInt == 1080, $"VR camera video started ({vStart["width"].AsInt}x{vStart["height"].AsInt} {vStart["fps"].AsInt}fps)");
                Check(vStop["ok"].AsBool && vStop["frames"].AsInt >= 20, $"VR camera video stopped ok ({vStop["frames"].AsInt} frames, {vStop["seconds"].AsDouble:0.00}s {vStop["error"].AsString})");
                Check(camJobDone.Wait(20000) && camJobErr == null && System.IO.File.Exists(camMp4) && new System.IO.FileInfo(camMp4).Length > 20000,
                    $"VR camera MP4 written ({(System.IO.File.Exists(camMp4) ? new System.IO.FileInfo(camMp4).Length / 1024 : 0)} KB, encoded {camJob.Encoded} {camJobErr})");
                Check(shot["ok"].AsBool && System.IO.File.Exists(shot["path"].AsString), $"VR camera shot saved ({shot["path"].AsString} {shot["error"].AsString})");
                if (shot["ok"].AsBool)
                {
                    var png = System.IO.File.ReadAllBytes(shot["path"].AsString);
                    int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                    Check(w == 1280 && h == 720 && png.Length > 20000, $"VR camera photo {w}x{h}, {png.Length / 1024} KB");
                }
                foreach (var f in new[] { "vr_camera_back.png", "vr_camera_front.png", "vr_camera_side.png" }) Check(System.IO.File.Exists(System.IO.Path.Combine(camDir, f)), "drawn " + f);
                // 영상 소리 (2026-11-01): 실제 마이크를 열지 않게 말하기를 시험용 WAV로 켜 둔다 → 그 마이크를 써서 MP4에 소리 트랙
                {
                    Drain();
                    const int Rate = 16000; int count = Rate * 3;
                    var wav = System.IO.Path.Combine(outDir, "vr_tone.wav");
                    using (var ww = new System.IO.BinaryWriter(System.IO.File.Create(wav)))
                    {
                        ww.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); ww.Write(36 + count * 2); ww.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); ww.Write(16); ww.Write((short)1); ww.Write((short)1);
                        ww.Write(Rate); ww.Write(Rate * 2); ww.Write((short)2); ww.Write((short)16); ww.Write(System.Text.Encoding.ASCII.GetBytes("data")); ww.Write(count * 2);
                        for (int i = 0; i < count; i++) ww.Write((short)(Math.Sin(2 * Math.PI * 220 * i / Rate) * 0.3 * 32767));
                    }
                    ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", true).Set("wav", wav)));
                    Thread.Sleep(300);
                    var micMp4 = System.IO.Path.Combine(outDir, "vr_camera_sound.mp4");
                    var micJob = RecordJob.Pipe(micMp4);
                    string? micErr = null; var micDone = new ManualResetEventSlim();
                    micJob.Finished = e => { micErr = e; micDone.Set(); };
                    ch.Send(Msg.Make(Msg.Vr).Set("cameraOpts", JNode.Obj().Set("mic", true)).Set("cameraPipe", micJob.PipeName).Set("cameraTest", System.IO.Path.Combine(outDir, "vrcamera_sound")));
                    JNode micStop = JNode.Null;
                    try
                    {
                        var until = DateTime.UtcNow.AddSeconds(20);
                        while (micStop.IsNull && DateTime.UtcNow < until)
                        {
                            var m = wait(Msg.VrCameraShot + "|" + Msg.VrCameraVideo, 20000);
                            if (Msg.Type(m) == Msg.VrCameraVideo && m["stopped"].AsBool) micStop = m;
                        }
                    }
                    catch (TimeoutException) { }
                    Check(micStop["ok"].AsBool && micDone.Wait(20000) && micErr == null, $"VR camera video with sound finished ({micStop["frames"].AsInt} frames, audio {micJob.AudioSeconds:F2} s {micStop["error"].AsString} {micErr})");
                    var tracks = System.IO.File.Exists(micMp4) ? Mp4Handlers(micMp4) : new System.Collections.Generic.List<string>();
                    Check(tracks.Contains("soun") && micJob.AudioSeconds > 0.5, $"VR camera MP4 has a sound track ({string.Join(",", tracks)}, {micJob.AudioSeconds:F2} s)");
                    ch.Send(Msg.Make(Msg.FaceTracking).Set("lip", JNode.Obj().Set("on", false)));
                    ch.Send(Msg.Make(Msg.Vr).Set("cameraOpts", JNode.Obj().Set("mic", false)));
                    Drain();
                }
                // 투명 배경 · 심도 (2026-10-27): 투명이면 모서리 알파 0, 심도는 찍히기만 하면 됨
                foreach (var (label, opts) in new[] { ("transparent", JNode.Obj().Set("transparent", true).Set("dof", 0f)), ("dof", JNode.Obj().Set("transparent", false).Set("dof", 0.6f)) })
                {
                    Drain();
                    ch.Send(Msg.Make(Msg.Vr).Set("cameraOpts", opts).Set("cameraTest", System.IO.Path.Combine(outDir, "vrcamera_" + label)));
                    JNode s2 = JNode.Null;
                    try { s2 = wait(Msg.VrCameraShot, 20000); } catch (TimeoutException) { }
                    bool ok2 = s2["ok"].AsBool && System.IO.File.Exists(s2["path"].AsString);
                    string extra = "";
                    if (ok2 && label == "transparent")
                    {
                        var img = Sticker.DecodePng(System.IO.File.ReadAllBytes(s2["path"].AsString));
                        int corner = img == null ? 255 : img.A(2, 2);
                        int opaque = 0; if (img != null) for (int i = 3; i < img.Px.Length; i += 4) if (img.Px[i] > 200) opaque++;
                        ok2 = img != null && corner == 0 && opaque > 1000 && s2["transparent"].AsBool;
                        extra = $"corner alpha {corner}, opaque px {opaque}";
                    }
                    Check(ok2, $"VR camera {label} shot ({s2["path"].AsString} {extra} {s2["error"].AsString})");
                }
                ch.Send(Msg.Make(Msg.Vr).Set("cameraOpts", JNode.Obj().Set("transparent", false).Set("dof", 0f)));
            }
            Drain();
            var back = Vr(JNode.Obj().Set("view", false));
            Check(back["wanted"].AsBool && !back["wantView"].AsBool && !back["view"].AsBool, $"view off → connection stays ({back["state"].AsString}: {back["text"].AsString})");
            Drain();
            last = Vr(JNode.Obj().Set("enabled", false));
            Check(last["state"].AsString == "off", $"vr off after view → {last["state"].AsString}");
            Check(!proc.HasExited, "renderer still running");
            ch.Send(Msg.Make(Msg.Quit));
            Check(proc.WaitForExit(15000), "renderer quits cleanly");
        }
        catch (Exception e) { Check(false, "vrlink: " + e.Message); }
        if (!proc.HasExited) proc.Kill();
        Console.WriteLine(_fail == 0 ? "E2E vrlink: all ok" : $"E2E vrlink: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
