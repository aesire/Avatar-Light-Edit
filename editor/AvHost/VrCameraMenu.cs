// VR 카메라 조작판 (2026-10-25 사용자 "VRC 카메라와 비슷하게, 영상 기능, 카메라 고정"): 카메라 몸체 옆에 붙어 나오는 작은 창 (VRChat 카메라의 옆 메뉴 자리).
// 데스크톱에는 보이지 않게 화면 밖에 띄워 두고, VrPanelFeed가 다른 VR 창과 같은 길로 그려 보낸다 (id "camera" — 렌더러 VrPanels가 카메라 옆에 붙인다).
//   위: 사진/영상 · 상태(녹화 시간·타이머·줌) / 가운데: 내 앞으로 · 셔터(사진 = 흰 원, 영상 = 빨간 원, 녹화 중 = 빨간 사각) · 끄기
//   칸: 고정 · 타이머 · 셀카 · 나를 보기 / 보정 · 놓으면 · 격자 · 세로 / 4K · 60fps · 스트림 · 폴더 / 아래: 줌 막대(mm) · 카메라 크기(−/+, 50~300%).
// 누르면 MainWindow가 설정을 바꿔 렌더러에 보낸다 (Act 키). 상태는 렌더러 vrCameraState.
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    sealed class VrCameraMenu : Window
    {
        /// <summary>칸 하나: 키 · 아이콘(글리프 또는 짧은 글자) · 이름 · 켜짐 표시.</summary>
        sealed record Cell(string Key, string Icon, bool TextIcon, Func<string> Label, Func<bool> On);

        readonly Func<Settings> _settings;
        readonly Func<JNode> _state;
        readonly Action<string> _act;
        readonly Action<double> _fov;
        readonly List<(Cell Cell, Button Button, TextBlock Label)> _cells = new List<(Cell, Button, TextBlock)>();
        readonly Button _photo, _video, _shutter;
        readonly Ellipse _shutterRing;
        readonly Border _shutterDot;
        readonly TextBlock _status, _info, _focal, _sizeText;
        Button _bigger = null!, _smaller = null!;
        readonly Slider _zoom;
        bool _suppress;

        static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(232, 48, 48)), White = new SolidColorBrush(Color.FromRgb(240, 241, 244)), Amber = new SolidColorBrush(Color.FromRgb(255, 196, 40));

        public VrCameraMenu(Window owner, Func<Settings> settings, Func<JNode> state, Action<string> act, Action<double> fov)
        {
            _settings = settings; _state = state; _act = act; _fov = fov;
            SystemDecorations = SystemDecorations.None;
            ShowInTaskbar = false; ShowActivated = false; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-32000, -32000);   // 데스크톱에는 안 보이게 (VR에만)
            Width = 360; Height = 548;
            FontFamily = owner.FontFamily; FontSize = 14;
            this.Bg(Ui.KBase);

            var root = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
            // 위: 사진/영상 · 상태
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            _photo = SegButton("사진", "photo", new CornerRadius(14, 0, 0, 14));
            _video = SegButton("영상", "video", new CornerRadius(0, 14, 14, 0));
            seg.Children.Add(_photo); seg.Children.Add(_video);
            top.Children.Add(seg);
            _status = Ui.Text("", 15, FontWeight.SemiBold);
            _status.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_status, 1);
            top.Children.Add(_status);
            root.Children.Add(top);

            // 가운데: 내 앞으로 · 셔터 · 끄기
            var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Height = 96 };
            var here = Round(Glyph.Move, false, "내 앞으로", "summon", 52);
            here.HorizontalAlignment = HorizontalAlignment.Center;
            mid.Children.Add(here);
            _shutterRing = new Ellipse { Width = 84, Height = 84, StrokeThickness = 5, Stroke = White };
            _shutterDot = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var face = new Grid { Width = 84, Height = 84 };
            face.Children.Add(_shutterRing); face.Children.Add(_shutterDot);
            _shutter = new Button { Content = face, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(42), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _shutter.Click += (_, __) => { _act("shutter"); Refresh(); };
            ToolTip.SetTip(_shutter, "셔터 — 카메라를 쥔 손 트리거와 같아요");
            Grid.SetColumn(_shutter, 1);
            mid.Children.Add(_shutter);
            var off = Round(Glyph.Close, false, "끄기", "off", 52);
            off.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(off, 2);
            mid.Children.Add(off);
            root.Children.Add(mid);

            // 칸 4×3
            var s = settings;
            var cells = new[]
            {
                new Cell("lock", Glyph.Pin, false, () => s().VrCameraLock ? "고정됨" : "고정", () => s().VrCameraLock),
                new Cell("timer", "⏲", true, () => s().VrCameraTimer > 0 ? $"타이머 {s().VrCameraTimer}초" : "타이머", () => s().VrCameraTimer > 0),
                new Cell("flip", Glyph.Mirror, false, () => "셀카", () => s().VrCameraFlip),
                new Cell("lookAt", Glyph.Person, false, () => "나를 보기", () => s().VrCameraLookAt),
                new Cell("smooth", "≋", true, () => "보정 " + (s().VrCameraSmooth switch { 1 => "약", 2 => "중", 3 => "강", _ => "끔" }), () => s().VrCameraSmooth > 0),
                new Cell("anchor", Glyph.Place, false, () => s().VrCameraAnchor switch { "world" => "월드에 둠", "follow" => "따라오기", _ => "나와 함께" }, () => s().VrCameraAnchor != "room"),
                new Cell("grid", "#", true, () => "격자", () => s().VrCameraGrid),
                new Cell("portrait", "▯", true, () => s().VrCameraPortrait ? "세로" : "가로", () => s().VrCameraPortrait),
                new Cell("4k", "4K", true, () => "사진 4K", () => s().VrCamera4K),
                new Cell("fps", "60", true, () => $"영상 {s().VrCameraFps}fps", () => s().VrCameraFps >= 60),
                new Cell("stream", Glyph.View, false, () => "스트림", () => s().VrCameraStream),
                new Cell("folder", Glyph.Folder, false, () => "폴더 열기", () => false),
            };
            var grid = new UniformGrid { Columns = 4 };
            foreach (var c in cells)
            {
                var icon = c.TextIcon ? Ui.Text(c.Icon, 22, FontWeight.Bold) : Ui.Icon(c.Icon, 22);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var circle = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(25), Child = icon, HorizontalAlignment = HorizontalAlignment.Center };
                var label = Ui.Text("", 12, FontWeight.SemiBold);
                label.HorizontalAlignment = HorizontalAlignment.Center; label.TextTrimming = TextTrimming.None;
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(circle); stack.Children.Add(label);
                var b = new Button { Content = stack, Margin = new Thickness(2), Padding = new Thickness(2, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(10), Tag = circle };
                var key = c.Key;
                b.Click += (_, __) => { _act(key); Refresh(); };
                ToolTip.SetTip(b, Tip(key));
                _cells.Add((c, b, label));
                grid.Children.Add(b);
            }
            root.Children.Add(grid);

            // 줌
            var zoom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var minus = Small("−", -10);
            zoom.Children.Add(minus);
            _zoom = new Slider { Minimum = 0, Maximum = 80, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, -6, 8, -6) };   // 값 = 100 − 화각 (오른쪽 = 당겨 찍기)
            _zoom.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_suppress) { _fov(100 - _zoom.Value); UpdateFocal(100 - _zoom.Value); } };
            Grid.SetColumn(_zoom, 1); zoom.Children.Add(_zoom);
            var plus = Small("+", 10);
            Grid.SetColumn(plus, 2); zoom.Children.Add(plus);
            _focal = Ui.Text("", 14, FontWeight.SemiBold, Ui.KText2);
            _focal.MinWidth = 64; _focal.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(_focal, 3); zoom.Children.Add(_focal);
            root.Children.Add(zoom);

            // 카메라 크기 (몸체·화면 함께)
            var size = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var smaller = _smaller = Small("−", 0); smaller.Click += (_, __) => { _act("smaller"); Refresh(); };
            ToolTip.SetTip(smaller, "카메라(화면)를 작게");
            size.Children.Add(smaller);
            _sizeText = Ui.Text("", 14, FontWeight.SemiBold, Ui.KText2);
            _sizeText.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(_sizeText, 1); size.Children.Add(_sizeText);
            var bigger = _bigger = Small("+", 0); bigger.Click += (_, __) => { _act("bigger"); Refresh(); };
            ToolTip.SetTip(bigger, "카메라(화면)를 크게 — 찍히는 크기와는 상관없어요");
            Grid.SetColumn(bigger, 2); size.Children.Add(bigger);
            root.Children.Add(size);

            _info = Ui.Text("", 12, FontWeight.Normal, Ui.KText3);
            _info.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(_info);
            Content = new Border { Child = root, CornerRadius = new CornerRadius(16) };
            Refresh();
        }

        /// <summary>시험용: 크기 −/+ 단추.</summary>
        public Button SizeButton(bool bigger) => bigger ? _bigger : _smaller;

        /// <summary>시험용: 칸 단추 (키로).</summary>
        public Button? ButtonOf(string key) => _cells.Find(c => c.Cell.Key == key).Button;

        static string Tip(string key) => key switch
        {
            "lock" => "카메라를 지금 자리(월드)에 고정해요. 그립으로 잡아도 움직이지 않아요 — 삼각대처럼. 다시 누르면 풀려요",
            "timer" => "셔터를 누르면 3 → 5 → 10초 뒤에 찍어요 (영상은 녹화 시작). 세는 중에 다시 누르면 취소",
            "flip" => "렌즈를 화면 쪽으로 — 화면을 보며 나를 찍어요",
            "lookAt" => "렌즈가 늘 내 머리를 향해요 (카메라를 어디에 두든)",
            "smooth" => "손떨림 보정: 렌즈가 부드럽게 따라가요 (끔 → 약 → 중 → 강)",
            "anchor" => "손에서 놓으면: 나와 함께(걸으면 같이 옴) → 월드에 둠(그 자리에 남음) → 따라오기(고개 방향 기준 그 자리로 천천히)",
            "grid" => "화면에 3분할 격자 (찍히는 그림에는 안 나와요)",
            "portrait" => "세로 사진·영상 (1080×1920)",
            "4k" => "사진 크기 3840×2160 (끄면 1920×1080)",
            "fps" => "영상 초당 장 수 30 ↔ 60",
            "stream" => "카메라 화면을 미리보기 프로그램 창(데스크톱)에 띄워요 — OBS로 잡아 방송·녹화",
            "folder" => "VR 사진·영상 폴더를 데스크톱에서 열어요",
            _ => "",
        };

        Button SegButton(string text, string key, CornerRadius r)
        {
            var b = new Button { Content = Ui.Text(text, 14, FontWeight.SemiBold), Padding = new Thickness(16, 6), CornerRadius = r, BorderThickness = new Thickness(0) };
            b.Click += (_, __) => { _act(key); Refresh(); };
            return b;
        }

        Button Round(string glyph, bool text, string tip, string key, double size)
        {
            var b = new Button { Content = text ? Ui.Text(glyph, 20, FontWeight.Bold) : Ui.Icon(glyph, 20), Width = size, Height = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(size / 2), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0) };
            b.Bg(Ui.KCard);
            ToolTip.SetTip(b, tip);
            b.Click += (_, __) => { _act(key); Refresh(); };
            return b;
        }

        Button Small(string text, double step)
        {
            var b = new Button { Content = Ui.Text(text, 18, FontWeight.Bold), Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0) };
            b.Bg(Ui.KCard);
            if (step != 0) b.Click += (_, __) => _zoom.Value = Math.Clamp(_zoom.Value + step, _zoom.Minimum, _zoom.Maximum);
            return b;
        }

        /// <summary>세로 24 mm 필름 기준 초점 거리 (화각 60° ≈ 21 mm).</summary>
        void UpdateFocal(double fov) => _focal.Text = $"{12 / Math.Tan(fov * Math.PI / 360):0} mm";

        static string Clock(int s) => $"{s / 60}:{s % 60:00}";

        /// <summary>설정·렌더러 상태를 지금 값으로.</summary>
        public void Refresh()
        {
            var s = _settings(); var st = _state();
            bool video = s.VrCameraVideo, rec = st["recording"].AsBool;
            int count = st["countdown"].AsInt;
            _photo.Bg(video ? Ui.KCard : Ui.KAccent); _video.Bg(video ? Ui.KAccent : Ui.KCard);
            ((TextBlock)_photo.Content!).Fg(video ? Ui.KText2 : Ui.KAccentText); ((TextBlock)_video.Content!).Fg(video ? Ui.KAccentText : Ui.KText2);
            // 셔터: 사진 = 흰 원 · 영상 = 빨간 원 · 녹화 중 = 빨간 사각 · 타이머 중 = 노란 고리
            _shutterRing.Stroke = count > 0 ? Amber : White;
            _shutterDot.Background = video || rec ? Red : White;
            _shutterDot.Width = _shutterDot.Height = rec ? 34 : 64;
            _shutterDot.CornerRadius = new CornerRadius(rec ? 6 : 32);
            var err = st["error"].AsString;
            if (rec) { _status.Text = "● REC " + Clock(st["seconds"].AsInt); _status.Foreground = Red; }
            else if (count > 0) { _status.Text = $"{count}초 뒤에 " + (video ? "녹화" : "찍어요"); _status.Foreground = Amber; }
            else if (err.Length > 0) { _status.Text = err; _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(Ui.KCaution)); }
            else { _status.Text = st["held"].AsBool ? "트리거 = " + (video ? "녹화" : "찍기") : s.VrCameraLock ? "고정됨" : "그립으로 잡아요"; _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(Ui.KText2)); }
            _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.MaxWidth = 200;
            foreach (var (cell, button, label) in _cells)
            {
                label.Text = cell.Label();
                bool on = cell.On();
                ((Border)button.Tag!).Bg(on ? Ui.KAccent : Ui.KCard);
                if (button.Content is StackPanel sp && sp.Children[0] is Border bd && bd.Child is TextBlock icon) icon.Fg(on ? Ui.KAccentText : Ui.KText);
            }
            if (st.Has("fov"))
            {
                double fov = Math.Clamp(st["fov"].AsDouble, 20, 100);
                if (Math.Abs(100 - fov - _zoom.Value) > 0.6) { _suppress = true; _zoom.Value = 100 - fov; _suppress = false; }
                UpdateFocal(fov);
            }
            else UpdateFocal(100 - _zoom.Value);
            string size = video ? (s.VrCameraPortrait ? "1080×1920" : "1920×1080") + $" {s.VrCameraFps}fps" : s.VrCamera4K ? (s.VrCameraPortrait ? "2160×3840" : "3840×2160") : (s.VrCameraPortrait ? "1080×1920" : "1920×1080");
            _sizeText.Text = $"카메라 크기 {s.VrCameraScale * 100:0}%";
            _info.Text = $"사진 {st["shots"].AsInt}장 · 영상 {st["videos"].AsInt}개 · {size}";
        }
    }
}
