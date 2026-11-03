// 자세 페이지 · VR 포즈 캡처 (PLAN 3.12, 2026-10-24): VR에서 찍은 자세를 기록하고, 녹화한 모션(.anim)을 "애니메이션" 탭에 불러와 재생한다.
// 재생은 미리보기만 (Doc.Preview) — 멈추면 그 순간을 자세로 기록한다 (실행 취소 한 단계).
using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class PosePanel
    {
        DispatcherTimer? _playTimer;
        DateTime _playFrom;
        double _playStart;
        Button _playBtn = null!, _stopBtn = null!;

        Control BuildPlay()
        {
            _playBtn = Ui.Button("재생", StartPlay, "애니메이션을 처음부터(또는 지금 시간부터) 미리보기로 돌려요. 멈추면 그 순간이 자세가 돼요", Glyph.Play);
            _stopBtn = Ui.Button("멈추기", () => StopPlay(true), "멈춘 순간을 자세로 남겨요", Glyph.Stop);
            _stopBtn.IsVisible = false;
            var row = new WrapPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            row.Children.Add(_playBtn); row.Children.Add(_stopBtn);
            row.Children.Add(Ui.Button("VR 녹화 폴더", () =>
            {
                Directory.CreateDirectory(MotionLibrary.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", MotionLibrary.Folder) { UseShellExecute = true });
            }, MotionLibrary.Folder, Glyph.Folder));
            return row;
        }

        void StartPlay()
        {
            if (_clip == null || Doc == null || _rest == null || _clip.Length <= 0) return;
            double t = _animTime.Slider.Value;
            _playStart = t >= _clip.Length - 0.01 ? 0 : t;
            _playFrom = DateTime.UtcNow;
            _playTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, __) => PlayTick());
            _playTimer.Start();
            _playBtn.IsVisible = false; _stopBtn.IsVisible = true;
        }

        double PlayTime => _clip == null ? 0 : (_playStart + (DateTime.UtcNow - _playFrom).TotalSeconds) % Math.Max(0.01, _clip.Length);   // 끝나면 처음부터

        void PlayTick()
        {
            if (_clip == null || Doc == null) { StopPlay(); return; }
            double t = PlayTime;
            Doc.Preview(Addr.Pose, _clip.SamplePose((float)t));
            _animTime.SetQuiet(t);
            if (!_animBox.IsFocused) _animBox.Text = Ui.F(Math.Round(t, 2));
        }

        /// <summary>재생을 멈춘다. keep = 그 순간을 자세로 기록 (아니면 기록된 자세로 미리보기를 되돌림).</summary>
        void StopPlay(bool keep = false)
        {
            if (_playTimer == null || !_playTimer.IsEnabled) return;
            double t = PlayTime;
            _playTimer.Stop();
            _playBtn.IsVisible = true; _stopBtn.IsVisible = false;
            if (_clip == null || Doc == null) return;
            if (keep) { Apply(_clip.SamplePose((float)Math.Round(t, 2)), "자세: " + _clip.Name); RefreshAnim(); }
            else Doc.Preview(Addr.Pose, Doc.Get(Addr.Pose));
        }

        /// <summary>VR에서 녹화한 모션(.anim 파일)을 "애니메이션" 탭에 불러온다.</summary>
        public bool LoadMotion(string path)
        {
            if (Doc == null || _rest == null) return false;
            AnimClip clip;
            try { clip = AnimClip.Load(path); }
            catch (Exception e) { _ctx.Status("녹화한 모션을 읽지 못했습니다: " + e.Message); return false; }
            if (!clip.IsHumanoid) return false;
            UseClip(clip);
            _tabs.Select("anim");
            return true;
        }
    }
}
