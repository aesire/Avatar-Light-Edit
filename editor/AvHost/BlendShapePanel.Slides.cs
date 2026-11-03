// 블렌드쉐이프 세팅 슬라이드 쇼 (2026-10-23 사용자 요청: "슬라이드 쇼처럼 각 설정의 사진을 보고 빠르게 비교"): 떠 있는 창 하나에 큰 사진 한 장.
// 첫 장은 "지금"(값이 바뀌면 다시 찍음), 그 뒤로 저장한 세팅. ◀ ▶ · ←/→ · 휠로 넘기고, 자동 넘기기, 아래 사진 줄을 눌러 바로 가기.
// 사진을 누르고 있는 동안은 "지금" 사진을 보여 줘서 같은 자리에서 앞뒤를 비교한다. "불러오기"는 그 세팅으로 되돌리기(실행 취소 한 단계).
// 목록 탭의 "세팅 ▾" 단추(빠른 불러오기)도 여기에 둔다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        const string GPrev = "", GNext = "", GPlay = "", GPause = "", GSlides = "";
        (FloatPanel Float, Action Draw, Action Refresh)? _slides;
        string _livePhoto = "";

        /// <summary>슬라이드 쇼 창을 연다 (이미 열려 있으면 앞으로). startId = 처음 보일 세팅 (null = 지금).</summary>
        void ShowSlides(string? startId = null)
        {
            var host = SubTabs.Host;
            if (host == null || Doc == null) return;
            if (_slides is { } open) { open.Float.Front(); if (startId != null) _slideGo?.Invoke(startId); return; }

            int index = 0;
            bool holdNow = false, shooting = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };

            var photo = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) }.Bg(Ui.KBase).Stroke(Ui.KCardStroke);
            ToolTip.SetTip(photo, "누르고 있으면 지금 사진 · ←/→ 또는 휠로 넘기기");
            var big = new Viewbox { Child = new Border { Width = PhotoSize, Height = PhotoSize, Child = photo }, Stretch = Stretch.Uniform };
            Action<int> step = _ => { };   // 아래 Step (단추를 만들 때는 아직 없는 컨트롤을 쓰므로 나중에 잇는다)
            var prev = Ui.IconButton(GPrev, () => step(-1), "앞 사진 (←)");
            var next = Ui.IconButton(GNext, () => step(1), "다음 사진 (→)");
            foreach (var b in new[] { prev, next }) { b.Width = 40; b.Height = 64; b.VerticalAlignment = VerticalAlignment.Center; }
            var stage = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(prev, 0); Grid.SetColumn(big, 1); Grid.SetColumn(next, 2);
            stage.Children.Add(prev); stage.Children.Add(big); stage.Children.Add(next);

            var name = Ui.Text("", 16, FontWeight.SemiBold);
            var count = Ui.Text("", 12, FontWeight.Normal, Ui.KText3);
            var info = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            count.VerticalAlignment = VerticalAlignment.Center;
            var nameRow = new DockPanel();
            DockPanel.SetDock(count, Dock.Right);
            nameRow.Children.Add(count); nameRow.Children.Add(name);

            Button play = null!;
            play = Ui.Button("자동 넘기기", () => { timer.IsEnabled = !timer.IsEnabled; RefreshPlay(); }, "1.6초마다 다음 사진으로 (다시 누르면 멈춤)", GPlay);
            var load = Ui.Button("불러오기", () => { if (Current() is Document.Variant v) { timer.Stop(); RefreshPlay(); ApplySet(v); } }, "이 세팅으로 되돌리기: 블렌드쉐이프만 바꿔요 (Ctrl+Z로 취소)", Glyph.Undo, accent: true);
            var detail = Ui.Button("자세히", () => { if (Current() is Document.Variant v) CompareSet(v); }, "다른 값 목록과 함께 지금과 나란히 비교", Glyph.PopOut);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            acts.Children.Add(play); acts.Children.Add(detail); acts.Children.Add(load);
            load.Margin = new Thickness(0);

            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var stripView = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            var body = new DockPanel { Margin = new Thickness(12) };
            var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
            bottom.Children.Add(nameRow); bottom.Children.Add(info); bottom.Children.Add(acts); bottom.Children.Add(stripView);
            DockPanel.SetDock(bottom, Dock.Bottom);
            body.Children.Add(bottom); body.Children.Add(stage);
            var root = new Border { Child = body, Focusable = true, Background = Brushes.Transparent };

            List<Document.Variant?> Slides() => new List<Document.Variant?> { null }.Concat(Doc == null ? Enumerable.Empty<Document.Variant>() : ShapeSets.All(Doc)).ToList();
            Document.Variant? Current() { var s = Slides(); return s[Math.Clamp(index, 0, s.Count - 1)]; }
            void RefreshPlay()
            {
                play.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Icon(timer.IsEnabled ? GPause : GPlay, 12), Ui.Text(timer.IsEnabled ? "멈춤" : "자동 넘기기", 13) } };
            }
            void Step(int d)
            {
                var n = Slides().Count;
                index = ((index + d) % n + n) % n;
                Draw();
            }
            void Go(string id)
            {
                var i = Slides().FindIndex(v => v?.Id == id);
                if (i >= 0) { index = i; Draw(); }
            }

            void Draw()
            {
                var slides = Slides();
                index = Math.Clamp(index, 0, slides.Count - 1);
                var v = slides[index];
                bool now = v == null || holdNow;
                var b64 = now ? _livePhoto : v!.Photo;
                var bmp = Photo(b64);
                photo.Child = bmp != null ? new Image { Source = bmp, Stretch = Stretch.UniformToFill }
                    : new TextBlock { Text = now ? (shooting ? "찍는 중…" : "미리보기가 없어요") : "사진 없음", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Fg(Ui.KText3);
                name.Text = v == null ? "지금" : holdNow ? v.Name + " → 지금 사진" : v.Name;
                count.Text = $"{index + 1} / {slides.Count}";
                if (v == null) info.Text = "지금 블렌드쉐이프 상태 · 값이 바뀌면 다시 찍어요";
                else
                {
                    int diff = Doc == null ? 0 : ShapeSets.Compare(Doc, v).Count;
                    info.Text = $"BlendShape {(Doc == null ? 0 : ShapeSets.Count(Doc, v))}개" + (v.Time.Length > 0 ? " · " + v.Time : "") + " · " + (diff == 0 ? "지금과 같아요" : $"지금과 다른 값 {diff}개");
                }
                load.IsEnabled = v != null && Doc != null && ShapeSets.Compare(Doc, v).Count > 0;
                detail.IsEnabled = v != null;
                prev.IsEnabled = next.IsEnabled = slides.Count > 1;

                // 아래 사진 줄: 고른 것은 강조 테두리
                strip.Children.Clear();
                for (int i = 0; i < slides.Count; i++)
                {
                    int at = i;
                    var s = slides[i];
                    var thumb = PhotoBox(s == null ? _livePhoto : s.Photo, 56, s == null ? "지금" : "없음");
                    var cell = new StackPanel { Spacing = 2, Width = 64 };
                    var frame = new Border { Child = thumb, Padding = new Thickness(2), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand) };
                    if (at == index) frame.Stroke(Ui.KAccent); else frame.BorderBrush = Brushes.Transparent;
                    var label = Ui.Text(s == null ? "지금" : s.Name, 11, FontWeight.Normal, at == index ? Ui.KText : Ui.KText3);
                    label.TextTrimming = TextTrimming.CharacterEllipsis; label.HorizontalAlignment = HorizontalAlignment.Center;
                    cell.Children.Add(frame); cell.Children.Add(label);
                    frame.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { index = at; Draw(); root.Focus(); } };
                    strip.Children.Add(cell);
                }
                if (strip.Children.Count > index) Dispatcher.UIThread.Post(() => strip.Children[index].BringIntoView());
            }

            async void Shoot()
            {
                if (shooting) return;
                shooting = true;
                try { var p = await FacePhoto(); if (p.Length > 0) _livePhoto = p; }
                finally { shooting = false; if (_slides != null) Draw(); }
            }

            // 넘기기: 키·휠, 사진을 누르고 있으면 지금 사진
            root.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Left || e.Key == Key.Up) { Step(-1); e.Handled = true; }
                else if (e.Key == Key.Right || e.Key == Key.Down) { Step(1); e.Handled = true; }
                else if (e.Key == Key.Space) { timer.IsEnabled = !timer.IsEnabled; RefreshPlay(); e.Handled = true; }
                else if (e.Key == Key.Enter && Current() is Document.Variant v) { ApplySet(v); e.Handled = true; }
            };
            stage.PointerWheelChanged += (_, e) => { Step(e.Delta.Y > 0 ? -1 : 1); e.Handled = true; };
            photo.PointerPressed += (_, e) =>
            {
                root.Focus();
                if (!e.GetCurrentPoint(photo).Properties.IsLeftButtonPressed || Current() == null) return;
                holdNow = true; e.Pointer.Capture(photo); Draw();
            };
            void Release() { if (!holdNow) return; holdNow = false; Draw(); }
            photo.PointerReleased += (_, __) => Release();
            photo.PointerCaptureLost += (_, __) => Release();
            timer.Tick += (_, __) => { if (Slides().Count > 1) Step(1); };

            step = Step;
            RefreshPlay();
            index = startId == null ? 0 : Math.Max(0, Slides().FindIndex(v => v?.Id == startId));
            Draw();
            var f = host.Show("shapes/slides", "shapes", "세팅 슬라이드 쇼", root, canDock: false, new Size(460, 620));
            _slides = (f, Draw, () => { Draw(); Shoot(); });
            _slideGo = Go;
            f.DockRequested += () => SubTabs.Host?.Close(f);
            f.Closed += (_, __) => { timer.Stop(); _slides = null; _slideGo = null; };
            Dispatcher.UIThread.Post(() => root.Focus());
            Shoot();
        }
        Action<string>? _slideGo;

        // ---------------- 목록 탭의 "현재 세팅 저장" + "세팅 ▾" (2026-10-23 사용자 요청: 블렌드쉐이프를 바꾸는 탭에서 저장 단추를 바로 누를 수 있게) ----------------
        Button? _quickSave;

        /// <summary>찾기 칸 아래 보기 옵션 줄 끝: 누르면 바로 저장(이름 "저장된 블렌드쉐이프 N" + 얼굴 사진), 옆 ▾는 불러오기·슬라이드 쇼 메뉴.</summary>
        Control SetsQuickButtons()
        {
            _quickSave = Ui.Button("현재 세팅 저장", () => SaveSet(), "지금 블렌드쉐이프 값을 \"저장된 블렌드쉐이프 N\"으로 바로 저장하고 얼굴 사진을 찍어요 (이름은 \"저장한 세팅\" 탭에서 바꿔요)", Glyph.Save, accent: true);
            _quickSave.MinHeight = 28; _quickSave.Padding = new Thickness(10, 2); _quickSave.Margin = new Thickness(0, 0, 4, 0);
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            var more = Ui.DropButton(Glyph.Undo, "세팅", menu, out _, "저장한 세팅 불러오기 · 슬라이드 쇼 · 저장한 세팅 탭");
            // 테두리 있는 보통 단추로 (도구 모음용 투명 단추는 눈에 덜 띈다)
            more.Classes.Remove("subtle");
            more.ClearValue(Button.BackgroundProperty); more.ClearValue(Button.BorderThicknessProperty);
            more.MinHeight = 28; more.Padding = new Thickness(10, 2); more.Margin = new Thickness(0);
            menu.Opening += (_, __) => FillSetsMenu(menu);
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { _quickSave, more } };
        }

        void FillSetsMenu(MenuFlyout menu)
        {
            menu.Items.Clear();
            var doc = Doc;
            if (doc == null) return;
            var sets = ShapeSets.All(doc).ToList();
            foreach (var v in sets)
            {
                int diff = ShapeSets.Compare(doc, v).Count;
                var item = new MenuItem
                {
                    Header = new StackPanel { Children = { Ui.Text(v.Name, 13), Ui.Text(diff == 0 ? "지금과 같아요" : $"불러오기 · 다른 값 {diff}개", 11, FontWeight.Normal, diff == 0 ? Ui.KSuccess : Ui.KText3) } },
                    Icon = PhotoBox(v.Photo, 36, ""),
                    IsEnabled = diff > 0,
                };
                item.Click += (_, __) => ApplySet(v);
                menu.Items.Add(item);
            }
            if (sets.Count == 0) menu.Items.Add(new MenuItem { Header = "저장한 세팅이 없어요", IsEnabled = false });
            menu.Items.Add(new Separator());
            var save = new MenuItem { Header = "지금 값 저장", Icon = Ui.Icon(Glyph.Add, 12) };
            save.Click += (_, __) => SaveSet();
            menu.Items.Add(save);
            if (SubTabs.Host != null && sets.Count > 0)
            {
                var show = new MenuItem { Header = "슬라이드 쇼로 비교…", Icon = Ui.Icon(GSlides, 12) };
                show.Click += (_, __) => ShowSlides();
                menu.Items.Add(show);
            }
            var manage = new MenuItem { Header = "저장한 세팅 탭 열기", Icon = Ui.Icon(Glyph.Edit, 12) };
            manage.Click += (_, __) => _tabs.Select("sets");
            menu.Items.Add(manage);
        }
    }
}
