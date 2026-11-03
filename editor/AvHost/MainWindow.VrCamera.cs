// 메인 창: VR 카메라 (PLAN 3.12, 2026-10-24 → 2026-10-25 VRChat 카메라처럼 · 영상 · 고정) — VR 메뉴·빠른 설정·조작판(VrCameraMenu, VR 창 "camera").
// 잡기·찍기·줌은 렌더러(VrCamera)가 VR 안에서 한다. 여기서는 켜고 끄기·조작판 값(cameraOpts)·사진 폴더·크기를 보내고, 찍힌 사진(vrCameraShot)·상태(vrCameraState)를 알린다.
// 영상: 영상 모드인 동안 녹화 파이프(Core.RecordJob.Pipe)를 하나 미리 열어 두고 이름을 렌더러에 준다(cameraPipe) — VR 안에서 셔터를 누르면 바로 연결해 보낸다.
//   끝나면(파이프 끝 레코드 → MP4 마무리) 임시 이름 VR_recording_*.mp4를 VR_날짜.mp4로 바꾸고 다음 파이프를 연다.
using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        int _vrShots, _vrVideos;
        JNode _vrCamState = JNode.Obj();
        VrCameraMenu? _vrCamMenu;
        RecordJob? _vrVideoJob;
        bool _vrVideoStarted;
        DateTime _vrVideoStart;

        string VrCameraFolder => _settings.VrCameraFolder.Length > 0 ? _settings.VrCameraFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ALEdit VR");

        JNode WithVrCamera(JNode m)
        {
            m.Set("camera", _settings.VrCamera).Set("cameraFolder", VrCameraFolder)
                .Set("cameraWidth", _settings.VrCamera4K ? 3840 : 1920).Set("cameraHeight", _settings.VrCamera4K ? 2160 : 1080)
                .Set("cameraOpts", _settings.VrCameraOpts());
            var pipe = EnsureVrVideoJob();
            if (pipe != null) m.Set("cameraPipe", pipe);
            return m;
        }

        void SendVrCamera()
        {
            _ctx.Render?.Send(WithVrCamera(Msg.Make(Msg.Vr)));
            _vrHand?.Refresh(); _vrCamMenu?.Refresh();
        }

        void SetVrCamera(bool on)
        {
            _settings.VrCamera = on; _settings.Save();
            SendVrCamera();
            ApplyVrOpen();
            SetStatus(on ? "VR 카메라를 내 앞에 놓았어요. 그립으로 잡고, 잡은 손 트리거로 찍어요 (잡은 손 스틱 위·아래 = 줌). 옆 조작판에서 영상·타이머·셀카·고정을 골라요." : "VR 카메라를 껐어요.");
        }

        void VrCameraSummon()
        {
            if (!_settings.VrCamera) { SetVrCamera(true); return; }
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraSummon", true));
            SetStatus("VR 카메라를 내 앞으로 불렀어요.");
        }

        void SetVrCamera4K(bool on)
        {
            _settings.VrCamera4K = on; _settings.Save();
            SendVrCamera();
            SetStatus(on ? "VR 카메라 사진 크기: 3840×2160 (4K)." : "VR 카메라 사진 크기: 1920×1080.");
        }

        void OpenVrCameraFolder()
        {
            Directory.CreateDirectory(VrCameraFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", VrCameraFolder) { UseShellExecute = true });
        }

        /// <summary>조작판·메뉴 단추 (VrCameraMenu의 키).</summary>
        void VrCameraAct(string key)
        {
            var s = _settings;
            switch (key)
            {
                case "shutter": _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraShutter", true)); return;
                case "summon": VrCameraSummon(); return;
                case "off": SetVrCamera(false); return;
                case "folder": OpenVrCameraFolder(); return;
                case "4k": SetVrCamera4K(!s.VrCamera4K); return;
                case "photo": s.VrCameraVideo = false; break;
                case "video": s.VrCameraVideo = true; break;
                case "lock": s.VrCameraLock = !s.VrCameraLock; break;
                case "bigger": s.VrCameraScale = NextVrCameraScale(s.VrCameraScale, 1); break;
                case "smaller": s.VrCameraScale = NextVrCameraScale(s.VrCameraScale, -1); break;
                case "timer": s.VrCameraTimer = s.VrCameraTimer switch { 0 => 3, 3 => 5, 5 => 10, _ => 0 }; break;
                case "flip": s.VrCameraFlip = !s.VrCameraFlip; if (s.VrCameraFlip) s.VrCameraLookAt = false; break;
                case "lookAt": s.VrCameraLookAt = !s.VrCameraLookAt; if (s.VrCameraLookAt) s.VrCameraFlip = false; break;
                case "smooth": s.VrCameraSmooth = (s.VrCameraSmooth + 1) % 4; break;
                case "anchor": s.VrCameraAnchor = s.VrCameraAnchor switch { "room" => "world", "world" => "follow", _ => "room" }; break;
                case "grid": s.VrCameraGrid = !s.VrCameraGrid; break;
                case "portrait": s.VrCameraPortrait = !s.VrCameraPortrait; break;
                case "fps": s.VrCameraFps = s.VrCameraFps >= 60 ? 30 : 60; break;
                case "stream": s.VrCameraStream = !s.VrCameraStream; break;
                case "mic": s.VrCameraMic = !s.VrCameraMic; break;
                default: return;
            }
            s.Save();
            SendVrCamera();
            SetStatus(key switch
            {
                "photo" => "VR 카메라: 사진 모드.",
                "video" => "VR 카메라: 영상 모드 — 트리거(또는 셔터)로 녹화를 시작하고 다시 눌러 멈춰요.",
                "lock" => s.VrCameraLock ? "VR 카메라를 이 자리에 고정했어요 (잡아도 안 움직여요)." : "VR 카메라 고정을 풀었어요.",
                "bigger" or "smaller" => $"VR 카메라 크기 {s.VrCameraScale * 100:0}% (화면 {14 * s.VrCameraScale:0.#} cm 너비 몸체).",
                "timer" => s.VrCameraTimer > 0 ? $"VR 카메라 타이머 {s.VrCameraTimer}초." : "VR 카메라 타이머를 껐어요.",
                "stream" => s.VrCameraStream ? "카메라 화면을 미리보기 프로그램 창에 띄워요 (OBS로 잡을 수 있어요)." : "스트림을 껐어요.",
                "mic" => s.VrCameraMic ? "VR 카메라 영상에 마이크 소리를 넣어요." : "VR 카메라 영상을 소리 없이 찍어요.",
                _ => "VR 카메라 설정을 바꿨어요.",
            });
        }

        /// <summary>카메라 크기 단계 (50·75·100·125·150·200·250·300%) — 지금 값에서 한 칸 위·아래.</summary>
        internal static readonly float[] VrCameraScales = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 2.5f, 3f };
        static float NextVrCameraScale(float now, int dir)
        {
            if (dir > 0) { foreach (var v in VrCameraScales) if (v > now + 0.01f) return v; return VrCameraScales[^1]; }
            for (int i = VrCameraScales.Length - 1; i >= 0; i--) if (VrCameraScales[i] < now - 0.01f) return VrCameraScales[i];
            return VrCameraScales[0];
        }

        void SetVrCameraScale(float v) { _settings.VrCameraScale = v; _settings.Save(); SendVrCamera(); SetStatus($"VR 카메라 크기 {v * 100:0}%."); }

        void SetVrCameraFov(double fov) => _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraOpts", JNode.Obj().Set("fov", (float)Math.Clamp(fov, 20, 100))));

        void OnVrCameraState(JNode m) { _vrCamState = m; _vrCamMenu?.Refresh(); }

        void OnVrCameraShot(JNode m)
        {
            if (!m["ok"].AsBool) { SetStatus("VR 카메라: 사진을 저장하지 못했습니다 — " + m["error"].AsString); AppendLog("warn", "VR 카메라 저장 실패: " + m["error"].AsString); return; }
            _vrShots++;
            SetStatus($"VR 카메라로 찍었어요: {Path.GetFileName(m["path"].AsString)} ({m["width"].AsInt}×{m["height"].AsInt}) · 이번에 {_vrShots}장 · 폴더 {VrCameraFolder}");
            AppendLog("info", "VR 카메라 사진: " + m["path"].AsString);
            ShowSaved(m["path"].AsString);
            AdjustVrShot(m["path"].AsString);
        }

        // ---- 영상 ----

        /// <summary>영상 모드면 녹화 파이프를 하나 열어 두고 그 이름 (아니면 열어 둔 것을 닫고 null). 녹화 중인 것은 건드리지 않는다.</summary>
        string? EnsureVrVideoJob()
        {
            bool want = _settings.VrCamera && _settings.VrCameraVideo;
            if (_vrVideoJob != null)
            {
                if (_vrVideoStarted) return null;   // 녹화 중 — 끝나면 새로 연다
                if (want) return _vrVideoJob.PipeName;
                var old = _vrVideoJob; _vrVideoJob = null;
                old.Complete(cancel: true);
                return null;
            }
            if (!want) return null;
            try
            {
                Directory.CreateDirectory(VrCameraFolder);
                var job = RecordJob.Pipe(Path.Combine(VrCameraFolder, "VR_recording_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4"));
                job.Finished = err => Dispatcher.UIThread.Post(() => OnVrVideoFinished(job, err));
                _vrVideoJob = job; _vrVideoStarted = false;
                return job.PipeName;
            }
            catch (Exception e) { AppendLog("warn", "VR 카메라 영상 파이프를 열지 못했습니다: " + e.Message); return null; }
        }

        void OnVrCameraVideo(JNode m)
        {
            if (m["started"].AsBool)
            {
                _vrVideoStarted = true; _vrVideoStart = DateTime.Now;
                SetStatus($"VR 카메라 녹화 중 · {m["width"].AsInt}×{m["height"].AsInt} {m["fps"].AsInt}fps — 다시 누르면 멈춰요.");
                return;
            }
            if (!m["stopped"].AsBool) return;
            if (!m["ok"].AsBool)
            {
                SetStatus("VR 카메라 영상: " + (m["error"].AsString.Length > 0 ? m["error"].AsString : "저장하지 못했어요"));
                // 연결도 못 했으면 파이프는 그대로 기다리는 중 — 이름을 다시 준다 (렌더러는 한 번 쓰면 비운다)
                if (!_vrVideoStarted && _vrVideoJob != null) _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraPipe", _vrVideoJob.PipeName));
                return;
            }
            SetStatus($"VR 카메라 녹화를 멈췄어요 ({m["seconds"].AsDouble:0.0}초) · MP4 마무리 중…");
        }

        /// <summary>파이프 녹화가 끝남 (MP4 마무리 · 취소 · 렌더러 끊김): 파일 이름을 날짜로 바꾸고 다음 파이프를 연다.</summary>
        void OnVrVideoFinished(RecordJob job, string? err)
        {
            if (job != _vrVideoJob) return;   // 영상 모드를 끄며 닫은 것
            _vrVideoJob = null;
            bool started = _vrVideoStarted; _vrVideoStarted = false;
            if (err != null) { SetStatus("VR 카메라 영상을 만들지 못했습니다: " + err); AppendLog("warn", "VR 카메라 영상 실패: " + err); }
            else if (File.Exists(job.Output))
            {
                var stamp = (started ? _vrVideoStart : DateTime.Now).ToString("yyyyMMdd_HHmmss");
                var path = Path.Combine(Path.GetDirectoryName(job.Output)!, $"VR_{stamp}.mp4");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(Path.GetDirectoryName(job.Output)!, $"VR_{stamp}_{i}.mp4");
                try { File.Move(job.Output, path); } catch (Exception e) { path = job.Output; AppendLog("warn", "VR 영상 이름 변경 실패: " + e.Message); }
                _vrVideos++;
                SetStatus($"VR 카메라 영상을 저장했어요: {Path.GetFileName(path)} · 이번에 {_vrVideos}개 · 폴더 {VrCameraFolder}");
                AppendLog("info", "VR 카메라 영상: " + path);
            }
            if (EnsureVrVideoJob() is string pipe) _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraPipe", pipe));
        }

        /// <summary>VR 메뉴의 "카메라" 묶음.</summary>
        void AddVrCameraMenu()
        {
            _vrMenu.Items.Add(new Separator());
            var cam = new MenuItem { Header = "VR 카메라", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCamera };
            ToolTip.SetTip(cam, "VRChat 카메라처럼 손에 드는 카메라를 내 앞에 놓아요. 그립으로 잡고 잡은 손 트리거로 찍어요(영상 모드면 녹화). 잡은 손 스틱 위·아래로 줌. 옆 조작판에서 사진/영상·타이머·셀카·나를 보기·손떨림 보정·고정을 골라요");
            cam.Click += (_, __) => SetVrCamera(!_settings.VrCamera);
            _vrMenu.Items.Add(cam);
            var summon = new MenuItem { Header = "카메라 내 앞으로" };
            summon.Click += (_, __) => VrCameraSummon();
            _vrMenu.Items.Add(summon);
            var video = new MenuItem { Header = "영상으로 찍기 (MP4)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraVideo };
            ToolTip.SetTip(video, "켜면 셔터가 녹화 시작·멈춤이에요 (1920×1080, 30·60fps, 30분까지). 소리는 VR 카메라 더 보기에서 켜요");
            video.Click += (_, __) => VrCameraAct(_settings.VrCameraVideo ? "photo" : "video");
            _vrMenu.Items.Add(video);
            var lockItem = new MenuItem { Header = "카메라 고정", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraLock };
            ToolTip.SetTip(lockItem, "카메라를 지금 자리(월드)에 고정해요 — 그립으로 잡아도 움직이지 않아요");
            lockItem.Click += (_, __) => VrCameraAct("lock");
            _vrMenu.Items.Add(lockItem);
            var size = new MenuItem { Header = "카메라 크기" };
            ToolTip.SetTip(size, "몸체와 뒷면 화면을 함께 키워 화면을 크게 봐요 (찍히는 사진·영상 크기와는 상관없어요)");
            foreach (var v in VrCameraScales)
            {
                var it = new MenuItem { Header = $"{v * 100:0}%", ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(_settings.VrCameraScale - v) < 0.01f };
                var val = v;
                it.Click += (_, __) => SetVrCameraScale(val);
                size.Items.Add(it);
            }
            _vrMenu.Items.Add(size);
            var k4 = new MenuItem { Header = "4K로 찍기 (3840×2160)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCamera4K };
            ToolTip.SetTip(k4, "사진 크기예요. 끄면 1920×1080이에요. 4K는 찍는 순간 조금 더 무거워요");
            k4.Click += (_, __) => SetVrCamera4K(!_settings.VrCamera4K);
            _vrMenu.Items.Add(k4);
            var stream = new MenuItem { Header = "카메라 화면을 미리보기 프로그램 창에 (스트림)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraStream };
            stream.Click += (_, __) => VrCameraAct("stream");
            _vrMenu.Items.Add(stream);
            var folder = new MenuItem { Header = "VR 사진·영상 폴더 열기" };
            ToolTip.SetTip(folder, VrCameraFolder);
            folder.Click += (_, __) => OpenVrCameraFolder();
            _vrMenu.Items.Add(folder);
        }
    }
}
