// PhysBone 느낌 창 (2026-10-24 사용자: "피직스본 느낌 부분은 따로 버튼으로 두고 팝업 윈도우로 열어서만 수정 가능하게").
// 설정 › 미리보기 PhysBone › "느낌 조절…"로 연다. 모달이 아니라 열어 둔 채 미리보기(VR 포함)를 보며 끌 수 있다. 하나만 열린다.
// 끄는 동안 setPhysBones { feel }로 바로 보내고, 놓으면 avhost.json `physBoneFeel`에 저장.
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        Window? _feelWindow;

        void PhysBoneFeelWindow()
        {
            if (_feelWindow != null) { _feelWindow.Activate(); return; }
            var w = new Window
            {
                Title = "PhysBone 느낌", Width = 620, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = Ui.Font, FontSize = 14,
            };
            w.Bg(Ui.KCard);
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 4 };
            p.Children.Add(Ui.Text("PhysBone 느낌", 17, FontWeight.SemiBold));
            p.Children.Add(Ui.Wrap("흔들림의 관성·복원력·빠르기·중력·닿았을 때를 바꿔요. 끄는 동안 미리보기(VR 포함)에 바로 보여요. 원본 PhysBone 값에 곱하는 미리보기 전용 값이라 VRChat에는 영향이 없어요."));

            void Feel(bool save) { _ctx.Render?.Send(Msg.Make(Msg.SetPhysBones).Set("feel", PhysBoneFeel.Message(_settings.PhysBoneFeel))); if (save) _settings.Save(); }
            var shows = new List<Action>();
            foreach (var it in PhysBoneFeel.Items)
            {
                void Set(double v, bool save) { _settings.PhysBoneFeel.Set(it.Key, Math.Clamp(v, it.Min, it.Max)); Feel(save); }
                var box = Ui.NumberCell(v => { Set(v, true); shows.ForEach(a => a()); }, () => shows.ForEach(a => a()));
                var sl = new Ui.EditSlider(it.Min, it.Max, v => { Set(v, false); box.Text = Ui.F(v); }, v => { Set(v, true); box.Text = Ui.F(v); });
                void Show() { var v = PhysBoneFeel.Get(_settings.PhysBoneFeel, it); sl.SetQuiet(v); box.Text = Ui.F(v); }
                Show(); shows.Add(Show);
                p.Children.Add(Ui.HelpRow(it.Label, $"{it.Help} (기본 {Ui.F(it.Default)})", Ui.NumberWithSlider(box, sl.Slider), wide: true));
            }

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(Ui.Button("기본값으로", () => { _settings.PhysBoneFeel = JNode.Obj(); Feel(true); shows.ForEach(a => a()); }, "느낌 값을 모두 처음 값으로 되돌려요", Glyph.Refresh));
            btns.Children.Add(Ui.Button("닫기", () => w.Close()));
            p.Children.Add(btns);
            w.Content = p;
            w.Closed += (_, __) => _feelWindow = null;
            _feelWindow = w;
            w.Show(this);
        }
    }
}
