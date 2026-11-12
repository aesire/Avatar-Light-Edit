// 시험용 (테스트 모드만, 2026-11-01): --test-session-edit <폴더> — 스튜디오 값이 든 세션을 연 뒤 사람이 고치는 경로를 차례로 돌려 본다.
//   같은 상태로 열렸는지 → 머슬 줄 값 고치기(PosePanel.Commit) · 배경 고치기(StudioPanel.ApplyBackground) → 되돌리기·다시 하기 → 세션 저장 → 다시 읽기. 결과는 session_edit.txt, 장면은 PNG.
//   창은 화면 밖에 활성화 없이 띄워 사용자 작업을 방해하지 않는다 (--ui-shots와 같게). 설정은 테스트 프로필에만 남는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        string? TestEditDir()
        {
            int i = Array.IndexOf(_args, "--test-session-edit");
            return RunMode.Test && i >= 0 && i + 1 < _args.Length ? Path.GetFullPath(_args[i + 1]) : null;
        }

        string? TestPresetDir()
        {
            int i = Array.IndexOf(_args, "--test-studio-preset");
            return RunMode.Test && i >= 0 && i + 1 < _args.Length ? Path.GetFullPath(_args[i + 1]) : null;
        }

        /// <summary>--test-studio-preset <폴더>: 세팅을 저장 → 다 바꿈 → 불러오기 → 되돌리기 → 끌어다 놓기·잘못된 파일. 결과는 studio_preset.txt.</summary>
        async Task TestStudioPreset(string dir)
        {
            Directory.CreateDirectory(dir);
            _shotsDir = dir;
            var lines = new List<string>();
            int fail = 0;
            void Check(bool ok, string what) { lines.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) fail++; }
            async Task Shot(string name)
            {
                await Task.Delay(1500);
                var path = Path.Combine(dir, name + ".png");
                var done = new TaskCompletionSource<bool>();
                void On(JNode m) { if (Msg.Type(m) == Msg.CaptureDone && m["path"].AsString == path) done.TrySetResult(m["ok"].AsBool); }
                _ctx.RendererMessage += On;
                _ctx.Render?.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", 450).Set("height", 600).Set("supersample", 1));
                var first = await Task.WhenAny(done.Task, Task.Delay(30000));
                _ctx.RendererMessage -= On;
                Check(first == done.Task && done.Task.Result, "촬영 " + name);
            }
            for (int i = 0; i < 240 && _ctx.Doc?.HasSchemas != true; i++) await Task.Delay(500);
            await Task.Delay(1500);
            _side.Select("studio");
            var panel = _studioPanel!;
            var s = _settings;
            string V() => $"{s.StudioBgMode}|{s.StudioBgTop}|{s.StudioBgBottom}|{s.StudioExposure}|{s.StudioFloor}|{s.StudioFloorColor}|{s.ShotShadowStrength}|{s.StudioQuality}|{s.StudioAo}|{s.StudioCaptureAa}|{s.ViewAvatarOnly}|{s.AvatarOnlyColor}|{s.ShotTransparent}|{s.ShotBrightness}|{s.ShotSaturation}|{s.ShotWidth}x{s.ShotHeight}|{s.StickerSpec}|{s.StickerOutlineColor}|{s.StickerOutline}|{s.StudioDof["enabled"].AsBool}|{s.StudioDof["strength"].AsDouble}|{s.Cameras.Count}|{s.StudioRecord["seconds"].AsInt}|{s.StudioRecord["format"].AsString}";

            // A 세팅: 파란 그라데이션 + 빨간 바닥
            panel.ApplyStudio(JNode.Parse(@"{ ""background"": { ""mode"": ""gradient"", ""top"": [0.2, 0.4, 0.9], ""bottom"": [0.8, 0.9, 1] }, ""exposure"": 1.5, ""floor"": { ""enabled"": true, ""mode"": ""solid"", ""strength"": 0.7, ""color"": [0.8, 0.2, 0.2] },
                ""dof"": { ""enabled"": true, ""strength"": 0.3 }, ""quality"": { ""studio"": true, ""ao"": 0.2, ""aa"": 4 }, ""avatarOnly"": { ""on"": false, ""color"": [0, 1, 0] }, ""transparent"": false,
                ""adjust"": { ""brightness"": 0.1, ""contrast"": 1.1, ""saturation"": 1.2 }, ""shot"": { ""width"": 1080, ""height"": 1920, ""embed"": false }, ""sticker"": { ""spec"": ""kakao"", ""color"": ""black"", ""outline"": 9 },
                ""record"": { ""motion"": ""fixed"", ""seconds"": 5, ""fps"": 24, ""width"": 1280, ""height"": 720, ""format"": ""png"", ""transparent"": true },
                ""cameras"": [ { ""name"": ""위치 A"", ""state"": { ""pivot"": [0, 1, 0], ""yaw"": 180, ""pitch"": 0, ""distance"": 3, ""fov"": 30 } } ] }"));
            // 조명 (2026-11-12): 프리셋에 조명도 적고, "조명도 가져오기"가 켜져 있으면 불러올 때 넣는다
            void SetPreset(string preset)
            {
                var env = (_ctx.Doc!.Get(Addr.Environment)).Clone();
                var e = env.Items.FirstOrDefault(x => x["provider"].AsString == "UnityLighting");
                if (e == null) { e = JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj()); env.Add(e); }
                e["params"].Set("preset", preset);
                _ctx.Doc.Set(Addr.Environment, env, "시험 조명");
            }
            string L() => _ctx.Doc!.Get(Addr.Environment).Items.FirstOrDefault(x => x["provider"].AsString == "UnityLighting")?["params"]["preset"].AsString ?? "";
            s.StudioPresetLighting = true;
            SetPreset("bright");
            var a = V();
            await Shot("a_saved");
            Save(_pages["studio"], "ui_a.png", Ui.KPanel);
            var file = Path.Combine(Settings.Folder, "studio-presets", "시험 A.studio.json");
            panel.SavePreset(file);
            Check(File.Exists(file), "저장: " + file);
            Check(StudioPreset.List(Path.Combine(Settings.Folder, "studio-presets")).Any(x => x.Name == "시험 A"), "목록에 나옴");

            // B 세팅: 전부 다르게 (프리셋 없이 칸을 고친 것과 같게)
            panel.ApplyStudio(JNode.Parse(@"{ ""background"": { ""mode"": ""none"", ""top"": [1, 1, 1], ""bottom"": [0, 0, 0] }, ""exposure"": 1, ""floor"": { ""enabled"": false, ""mode"": ""none"", ""strength"": 0.1, ""color"": [1, 1, 1] },
                ""dof"": { ""enabled"": false, ""strength"": 0.9 }, ""quality"": { ""studio"": false, ""ao"": 0.9, ""aa"": 16 }, ""avatarOnly"": { ""on"": true, ""color"": [1, 0, 1] }, ""transparent"": true,
                ""adjust"": { ""brightness"": 0, ""contrast"": 1, ""saturation"": 1 }, ""shot"": { ""width"": 3840, ""height"": 2160, ""embed"": true }, ""sticker"": { ""spec"": ""line"", ""color"": ""white"", ""outline"": 2 },
                ""record"": { ""motion"": ""fixed"", ""seconds"": 8, ""fps"": 30, ""width"": 1920, ""height"": 1080, ""format"": ""mp4"" }, ""cameras"": [] }"));
            SetPreset("night");
            var b = V();
            Check(a != b, "B 세팅은 A와 다름");
            await Shot("b_other");

            // 불러오기 → A로
            panel.LoadPreset(file);
            Check(V() == a, "불러오면 A 세팅: " + V());
            Check(L() == "bright", "불러오면 A 조명: " + L());
            await Shot("c_loaded");
            Save(_pages["studio"], "ui_loaded.png", Ui.KPanel);
            // 되돌리기 → B로
            panel.UndoPreset();
            Check(V() == b, "불러오기 전으로: " + V());
            Check(L() == "night", "불러오기 전 조명으로: " + L());
            // 조명도 가져오기 끔 → 조명은 그대로
            s.StudioPresetLighting = false;
            panel.LoadPreset(file);
            Check(V() == a && L() == "night", "조명 빼고 불러오기: " + L());
            panel.UndoPreset();
            Check(V() == b && L() == "night", "조명 빼고 되돌리기: " + L());
            s.StudioPresetLighting = true;
            await Shot("d_undo");
            // 끌어다 놓기와 같은 길
            Check(OpenPathAction(file) == "스튜디오 세팅 불러오기", "끌어다 놓기 안내");
            OpenPath(file, ask: false);
            await Task.Delay(300);
            Check(V() == a && _pageKey == "studio", "끌어다 놓기로 불러오기");
            // 잘못된 파일은 세팅을 건드리지 않는다
            var bad = Path.Combine(dir, "bad.studio.json");
            File.WriteAllText(bad, "{ \"format\": \"다른 것\" }");
            panel.LoadPreset(bad);
            Check(V() == a, "잘못된 파일은 세팅을 바꾸지 않음");
            // 없는 이미지
            var miss = Path.Combine(dir, "miss.studio.json");
            StudioPreset.Save(miss, JNode.Parse(@"{ ""background"": { ""mode"": ""image"", ""path"": ""C:/없는 폴더/없는 배경.png"" } }"));
            panel.LoadPreset(miss);
            Check(s.StudioBgMode == "image", "이미지가 없어도 값은 들어감");
            Check(StudioPreset.MissingFiles(StudioPreset.Load(miss)).Count == 1, "없는 파일을 알림");
            await Task.Delay(800);
            await Shot("e_missing_image");
            Save(_pages["studio"], "ui_missing.png", Ui.KPanel);
            lines.Add(fail == 0 ? "ALL OK" : $"{fail} failed");
            File.WriteAllLines(Path.Combine(dir, "studio_preset.txt"), lines);
            try { File.Delete(file); } catch (IOException) { }
            _closeConfirmed = true;
            Close();
        }

        void PrepareTestEdit()
        {
            if (TestEditDir() == null && TestPresetDir() == null) return;
            ShowActivated = false;
            FloatHost.Quiet = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-30000, -30000);
            Width = 1400; Height = 900;
        }

        async Task TestSessionEdit(string dir)
        {
            Directory.CreateDirectory(dir);
            _shotsDir = dir;
            var lines = new List<string>();
            int fail = 0;
            void Check(bool ok, string what) { lines.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) fail++; }
            async Task Shot(string name)
            {
                await Task.Delay(1500);   // 스킨드 메시·PhysBone이 값을 따라오게
                var path = Path.Combine(dir, name + ".png");
                var done = new TaskCompletionSource<bool>();
                void On(JNode m) { if (Msg.Type(m) == Msg.CaptureDone && m["path"].AsString == path) done.TrySetResult(m["ok"].AsBool); }
                _ctx.RendererMessage += On;
                _ctx.Render?.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", 600).Set("height", 800).Set("supersample", 2));
                var first = await Task.WhenAny(done.Task, Task.Delay(30000));
                _ctx.RendererMessage -= On;
                Check(first == done.Task && done.Task.Result, "촬영 " + name);
            }
            float Muscle(JNode pose, string n) => pose["muscles"][n].AsFloat;
            const string Head = "Head Turn Left-Right";

            for (int i = 0; i < 240 && _ctx.Doc?.HasSchemas != true; i++) await Task.Delay(500);
            var doc = _ctx.Doc;
            if (doc == null || !doc.HasSchemas) { File.WriteAllText(Path.Combine(dir, "session_edit.txt"), "FAIL 장면을 불러오지 못했어요\n"); _closeConfirmed = true; Close(); return; }
            await Task.Delay(2500);

            // 1) 같은 상태로 열렸는가
            var opened = doc.Get(Addr.Pose);
            Check(!opened.IsNull && opened.Has("bodyRotation"), "자세가 세션 값 (몸 방향 포함)");
            Check(!doc.Get(Addr.Expression).IsNull, "표정이 세션 값: " + FaceExpression.Name(doc.Get(Addr.Expression)));
            Check(doc.Get(Addr.Environment)[0]["params"]["intensity"].AsDouble > 0, "조명이 세션 값");
            Check(doc.Original.Renderers.Any(r => !doc.Get(Addr.RendererEnabled(r.Id)).AsBool && r.Enabled), "숨긴 렌더러가 숨겨짐");
            Check(_pageKey == "studio", "스튜디오 페이지로 열림: " + _pageKey);
            // 위·아래가 같으면 단색으로 들어온다 (2026-11-03)
            Check(_settings.StudioBgMode == "gradient" && _settings.StudioBgTop == "#FBFAFC" || _settings.StudioBgMode == "solid" && _settings.StudioBgColor == "#FBFAFC", $"배경이 세션 값: {_settings.StudioBgMode} {_settings.StudioBgTop} {_settings.StudioBgColor}");
            Check(_settings.StudioFloor == "shadow" && Math.Abs(_settings.ShotShadowStrength - 0.5) < 1e-6, $"바닥이 세션 값: {_settings.StudioFloor} {_settings.ShotShadowStrength}");
            Check(!_ctx.Camera.IsNull && Math.Abs(_ctx.Camera["yaw"].AsDouble - 112) < 1, "카메라가 세션 값: yaw " + _ctx.Camera["yaw"].AsDouble);
            Check(!doc.IsDirty, "열었을 때는 저장할 것이 없음");
            await Shot("1_opened");
            Save(_pages["studio"], "ui_studio.png", Ui.KPanel);

            // 2) 사람이 고치는 경로: 머슬 줄 · 배경
            float before = Muscle(opened, Head);
            _posePanel!.TestCommit(Head, 0.5);
            Check(Math.Abs(Muscle(doc.Get(Addr.Pose), Head) - 0.5f) < 1e-3 && doc.IsDirty, $"머슬 고치기: {before} → {Muscle(doc.Get(Addr.Pose), Head)}");
            Check(doc.Get(Addr.Pose).Has("bodyRotation"), "고쳐도 몸 방향이 남음");
            await Shot("2_pose_edited");
            _studioPanel!.TestGradient("#FFD6E4", "#FFF4E0");
            Check(_settings.StudioBgTop == "#FFD6E4", "배경 고치기");
            await Shot("3_background_edited");

            // 3) 되돌리기·다시 하기
            doc.Undo();
            Check(Math.Abs(Muscle(doc.Get(Addr.Pose), Head) - before) < 1e-3, "되돌리기: 머슬이 세션 값으로");
            await Shot("4_undo");
            doc.Redo();
            Check(Math.Abs(Muscle(doc.Get(Addr.Pose), Head) - 0.5f) < 1e-3, "다시 하기");

            // 4) 저장·다시 읽기
            var saved = Path.Combine(dir, "edited" + Session.Extension);
            Session.Save(saved, doc, _ctx.Camera);
            var back = Session.Read(saved);
            var d2 = new Document(doc.Original, back.IrPath);
            d2.LoadChangeset(back.Changeset);
            Check(Math.Abs(Muscle(d2.Get(Addr.Pose), Head) - 0.5f) < 1e-3 && d2.Get(Addr.Pose).Has("bodyRotation"), "저장한 세션에 고친 자세");
            Check(back.Studio.IsNull, "에디터가 저장한 세션에는 studio를 적지 않음 (설정에 남음)");
            Check(!doc.IsDirty, "저장하면 깨끗함");

            lines.Add(fail == 0 ? "ALL OK" : $"{fail} failed");
            File.WriteAllLines(Path.Combine(dir, "session_edit.txt"), lines);
            _closeConfirmed = true;
            Close();
        }
    }
}
