// 드롭다운·입력 칸 모양과 "길게 눌러 큰 창으로 고르기" (2026-10-22).
//   모양  색은 App 팔레트(Program.cs)의 TextControl*·ComboBox* 키 — 카드 위에 옅게 밝힌 칸 + 가는 테두리(반투명이라 테마 바탕이 바뀌어도 어울림).
//         모서리는 단추와 같은 ControlCornerRadius(다이얼 모드면 4·5), 펼친 목록은 카드처럼(카드 색 · 카드 테두리 · OverlayCornerRadius),
//         고른 줄은 페이지 안 탭처럼 KSelect 바탕 + 강조색 글자. 다이얼 모드(DialLook)는 고른 줄 왼쪽에 하이라이트 색 막대(탭의 LED처럼) + 옅은 하이라이트 바탕.
//         다이얼 모드 "UI 전체 통일"이면 칸 색은 Hardware가 앱 리소스로 덮어써 움푹한 장비 칸이 된다.
//   큰 창 드롭다운을 0.45초 길게 누르면 목록 대신 큰 항목으로 된 선택 창이 에디터 가운데 뜬다 — VR 오버레이(레이저 포인터)·터치에서 작은 목록을 고르기 어려워서.
//         떠 있는 창과 같은 틀(제목 줄 · 구분선 · 작업 창 색), 항목은 페이지 안 탭과 같은 칩(SubTabs.TabChip — 다이얼 모드면 장비 키 + LED).
//         에디터 창 안(Embed)에 띄워 창 공유·창 캡처형 VR 오버레이에도 보인다. 고르면 닫히고, Esc·✕·바깥 누르기로도 닫힌다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public static class FieldStyles
    {
        public static void Install(Application app)
        {
            Style S(Func<Selector?, Selector> sel, params (AvaloniaProperty P, object V)[] setters)
            {
                var st = new Style(sel);
                foreach (var (p, v) in setters) st.Setters.Add(new Setter(p, v));
                return st;
            }
            // 모서리는 단추와 같게 (Fluent 기본이 ControlCornerRadius — 따로 정하지 않는다)
            app.Styles.Add(S(x => x.OfType<ComboBox>(), (TemplatedControl.PaddingProperty, new Thickness(10, 5, 0, 5))));
            // 펼친 목록: 카드처럼 (카드 색 · 카드 테두리) + 위아래 여백, 줄은 단추 모서리로 둥글게 안쪽으로
            app.Styles.Add(S(x => x.OfType<ComboBox>().Template().OfType<Border>().Name("PopupBorder"),
                (Border.CornerRadiusProperty, new DynamicResourceExtension("OverlayCornerRadius")), (Border.PaddingProperty, new Thickness(0, 4)),
                (Border.BackgroundProperty, new DynamicResourceExtension(Ui.KCard)), (Border.BorderBrushProperty, new DynamicResourceExtension(Ui.KCardStroke))));
            app.Styles.Add(S(x => x.OfType<ComboBoxItem>(),
                (TemplatedControl.CornerRadiusProperty, new DynamicResourceExtension("ControlCornerRadius")), (Layoutable.MarginProperty, new Thickness(4, 1)), (TemplatedControl.PaddingProperty, new Thickness(12, 7)), (Layoutable.MinHeightProperty, 34.0)));
            // 고른 줄: 바탕은 고른 탭과 같은 KSelect(팔레트) + 글자는 진하게 강조색
            app.Styles.Add(S(x => x.OfType<ComboBoxItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                (ContentPresenter.ForegroundProperty, new DynamicResourceExtension(Ui.KAccentText)), (ContentPresenter.FontWeightProperty, FontWeight.SemiBold)));
            // 다이얼 모드: 고른 줄 왼쪽에 하이라이트 색 막대 (탭의 LED처럼), 글자는 보통 색 — 막대만큼 왼쪽 여백을 줄여 글자 자리는 그대로
            _dialStyles = new Styles
            {
                S(x => x.OfType<ComboBoxItem>().Class(":selected"), (TemplatedControl.PaddingProperty, new Thickness(9, 7, 12, 7))),
                S(x => x.OfType<ComboBoxItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                    (ContentPresenter.BorderThicknessProperty, new Thickness(3, 0, 0, 0)), (ContentPresenter.BorderBrushProperty, new DynamicResourceExtension(DialBar)),
                    (ContentPresenter.ForegroundProperty, new DynamicResourceExtension(Ui.KText))),
            };

            InputElement.PointerPressedEvent.AddClassHandler<ComboBox>(OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerMovedEvent.AddClassHandler<ComboBox>(OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerReleasedEvent.AddClassHandler<ComboBox>(OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerCaptureLostEvent.AddClassHandler<ComboBox>((c, _) => { if (c == _press) Cancel(); });
        }

        const string DialBar = "VP.DialBar";
        static Styles? _dialStyles;
        static readonly string[] DialKeys = { DialBar, "OverlayCornerRadius", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed" };

        /// <summary>다이얼 모드의 드롭다운 목록 (Hardware.ButtonResources·ClearButtonResources에서): 고른 줄은 하이라이트 막대 + 옅은 하이라이트 바탕(플랫 탭과 같은 0.12), 목록 모서리는 단추에 맞춰 작게</summary>
        public static void DialLook(Application app, bool on, Color hl = default)
        {
            if (_dialStyles == null) return;
            if (!on)
            {
                app.Styles.Remove(_dialStyles);
                foreach (var k in DialKeys) app.Resources.Remove(k);
                return;
            }
            app.Resources[DialBar] = new SolidColorBrush(hl);
            app.Resources["OverlayCornerRadius"] = new CornerRadius(Hardware.Flat ? 5 : 6);
            app.Resources["ComboBoxItemBackgroundSelected"] = new SolidColorBrush(hl, 0.12);
            app.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = new SolidColorBrush(hl, 0.18);
            app.Resources["ComboBoxItemBackgroundSelectedPressed"] = new SolidColorBrush(hl, 0.09);
            if (!app.Styles.Contains(_dialStyles)) app.Styles.Add(_dialStyles);
        }

        // ---------------- 길게 누르기 ----------------
        public const int HoldMs = 450;
        static ComboBox? _press;
        static Point _pressAt;
        static bool _fired;
        static DispatcherTimer? _timer;

        static void OnPressed(ComboBox c, PointerPressedEventArgs e)
        {
            if (!c.IsEnabled || c.IsDropDownOpen || c.ItemCount == 0) return;
            if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
            // 펼친 목록 안을 누른 것은 아니다 (목록은 다른 창이라 여기 오지 않지만 혹시)
            if (e.Source is Visual v && v.FindAncestorOfType<ComboBoxItem>(true) != null) return;
            Cancel();
            _press = c; _pressAt = e.GetPosition(c); _fired = false;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(HoldMs), DispatcherPriority.Input, (_, __) => Fire());
            _timer.Start();
        }

        static void OnMoved(ComboBox c, PointerEventArgs e)
        {
            if (c != _press || _fired) return;
            var p = e.GetPosition(c);
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 10) Cancel();
        }

        static void OnReleased(ComboBox c, PointerReleasedEventArgs e)
        {
            if (c != _press) return;
            if (_fired) e.Handled = true;   // 큰 창을 띄웠으면 보통 목록은 열지 않는다
            Cancel();
        }

        static void Cancel() { _timer?.Stop(); _timer = null; if (!_fired) _press = null; }

        static void Fire()
        {
            _timer?.Stop(); _timer = null;
            var c = _press;
            if (c == null) return;
            _fired = true;
            Dispatcher.UIThread.Post(() => { _press = null; _fired = false; }, DispatcherPriority.Background);   // 손을 뗀 뒤 정리 (놓기 처리보다 늦게)
            _press = c;
            ShowPicker(c);
        }

        // ---------------- 큰 선택 창 ----------------
        public static Window? Current { get; private set; }

        /// <summary>항목 글자: 문자열·ComboBoxItem(글자 또는 TextBlock 내용)·그 밖은 ToString</summary>
        public static string ItemText(object? item)
        {
            if (item is ComboBoxItem ci) item = ci.Content;
            return item switch
            {
                null => "",
                string s => s,
                TextBlock tb => tb.Text ?? "",
                Control ctl => string.Join(" ", ctl.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t))) is string j && j.Length > 0 ? j : ctl.ToString() ?? "",
                _ => item.ToString() ?? "",
            };
        }

        /// <summary>줄 이름: 같은 줄(PropRow)의 이름 글자, 없으면 툴팁, 없으면 "고르기"</summary>
        static string Title(ComboBox c)
        {
            for (Visual? v = c; v != null && v is not Window; v = v.GetVisualParent())
                if (v.GetVisualParent() is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 0 && t != v) is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text!;
            return ToolTip.GetTip(c) as string ?? Texts.T("고르기");
        }

        public static void ShowPicker(ComboBox c)
        {
            Current?.Close();
            var top = TopLevel.GetTopLevel(c) as Window;
            if (top == null) return;
            // 떠 있는 창(에디터 안 자식 창) 안의 드롭다운이면 에디터 창 기준으로
            var owner = Embed.IsAttached(top) && top.Owner is Window o ? o : top;
            var items = c.Items.Cast<object?>().ToList();
            int n = items.Count;
            int cols = n <= 6 ? 1 : n <= 16 ? 2 : 3;
            double width = Math.Min(Math.Max(320, owner.ClientSize.Width - 80), cols == 1 ? 440 : cols == 2 ? 640 : 860);

            var w = new Window
            {
                SystemDecorations = SystemDecorations.None, ShowInTaskbar = false, CanResize = false, SizeToContent = SizeToContent.Height,
                Width = width, MaxHeight = Math.Max(240, owner.ClientSize.Height - 80), WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = owner.FontFamily, FontSize = owner.FontSize, Title = Title(c),
            };
            Ui.AddModeStyles(w);
            // 제목 줄: 떠 있는 창과 같은 모양 (높이만 VR에서 누르기 쉽게 조금 크게)
            var bar = new DockPanel { Height = 44 };
            var close = Ui.IconButton(Glyph.Close, () => w.Close(), "닫기 (Esc)");
            close.Width = 36; close.Height = 36; close.Margin = new Thickness(0, 0, 4, 0);
            ((TextBlock)close.Content!).FontSize = 14;
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);
            var title = Ui.Text(w.Title ?? "", 14, FontWeight.SemiBold); title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(16, 0, 8, 0);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            bar.Children.Add(title);
            var head = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }.Stroke(Ui.KDivider);

            // 항목: 페이지 안 탭과 같은 칩 (고른 것은 KSelect 바탕 + 강조색 테두리, 다이얼 모드면 장비 키 + 켜진 LED)
            var grid = new UniformGrid { Columns = cols, Margin = new Thickness(10, 10, 10, 12) };
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                bool sel = i == c.SelectedIndex;
                bool enabled = !(items[i] is ComboBoxItem ci && !ci.IsEnabled);
                var label = Ui.Text(ItemText(items[i]), 16, sel ? FontWeight.SemiBold : FontWeight.Normal, sel && !Hardware.On ? Ui.KAccentText : Ui.KText);
                label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
                var chip = new SubTabs.TabChip(label) { Framed = true, On = sel };   // 고르지 않은 칩도 누를 곳이 보이게
                chip.Inner.Padding = new Thickness(14, 0, 14, 0); chip.Inner.MinHeight = 50;
                chip.Led.Width = chip.Led.Height = 8; chip.Led.Margin = new Thickness(0, 0, 10, 0);
                chip.Outer.Margin = new Thickness(4);
                if (!enabled) { chip.Outer.Opacity = 0.4; chip.Outer.Cursor = Cursor.Default; }
                else chip.Outer.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left || !chip.Outer.IsPointerOver) return;
                    c.SelectedIndex = idx; w.Close(); e.Handled = true;
                };
                grid.Children.Add(chip.Outer);
            }
            var body = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);
            body.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            // 바탕·테두리는 떠 있는 창과 같게 (작업 창 색 + 카드 테두리)
            w.Content = new Border { Child = body, BorderThickness = new Thickness(1) }.Stroke(Ui.KCardStroke).Bg(Ui.KPanel);
            w.Bg(Ui.KPanel);
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } };

            // 바깥(에디터)을 누르면 닫는다. 따로 뜬 창이면 포커스를 잃을 때
            EventHandler<PointerPressedEventArgs> outside = (_, __) => w.Close();
            owner.AddHandler(InputElement.PointerPressedEvent, outside, RoutingStrategies.Tunnel, handledEventsToo: true);
            w.Deactivated += (_, __) => { if (!Embed.IsAttached(w)) w.Close(); };
            w.Closed += (_, __) => { owner.RemoveHandler(InputElement.PointerPressedEvent, outside); if (Current == w) Current = null; };
            Current = w;
            w.Show(owner);
            Embed.Attach(w, owner);
            Embed.Focus(w);
        }
    }
}
