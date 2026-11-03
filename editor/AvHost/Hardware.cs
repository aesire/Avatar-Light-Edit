// 다이얼 모드의 하드웨어 모양 (2026-10-22, 사용자 요청): 설정 "조작 모양 → 다이얼 모드"이면 슬라이더는 다이얼(Dial.UseForSliders),
// 단추 모양은 플랫(기본: 단색 · 얇은 테두리 · 작은 모서리, 다른 UI와 어우러지게) 또는 입체(그라데이션 · 음영).
// "UI 전체 통일"이면 카드(장비 앞판처럼 조금 어둡게) · 입력 칸·콤보(움푹한 어두운 칸) · 체크 상자(켜지면 하이라이트 색)도 같은 모양으로.
// 단추·페이지 안 탭은 장비의 키처럼 (위 밝고 아래 어두운 그라데이션 · 어두운 테두리, 탭은 왼쪽 작은 LED가 하이라이트 색으로 켜짐),
// 토글(ToggleSwitch)은 가로 로커 스위치 (눌린 면이 꺼지면 왼쪽·켜지면 오른쪽으로 미끄러지고 하이라이트가 따라간다 — 꺼짐은 옅게, 켜짐은 하이라이트 색 그대로).
// 하이라이트 색은 다이얼과 같은 Ui.KDial.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;

namespace VrcProto.AvHost
{
    public static class Hardware
    {
        public static bool On { get; private set; }
        public static bool Flat { get; private set; } = true;
        public static bool Unify { get; private set; }
        /// <summary>켜고 끌 때 (페이지 안 탭이 다시 그린다)</summary>
        public static event Action? Changed;
        static Style? _toggleStyle, _checkStyle;
        static readonly string[] Keys = { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
            "ToggleButtonBackground", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundPressed",
            "ControlCornerRadius", "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
            "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckGlyphForegroundChecked",
            "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked" };

        public static void Apply(bool on, Settings s)
        {
            On = on; Flat = s.HardwareLook != "skeuo"; Unify = on && s.HardwareUnify;
            Dial.UseForSliders(on);
            var app = Application.Current;
            if (app == null) return;
            _toggleStyle ??= new Style(x => x.OfType<ToggleSwitch>())
            {
                Setters = { new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ToggleSwitch>((ts, scope) => RockerTemplate(ts, scope))) },
            };
            if (on && !app.Styles.Contains(_toggleStyle)) app.Styles.Add(_toggleStyle);
            else if (!on) app.Styles.Remove(_toggleStyle);
            // 체크 상자: 테마(플랫·입체)에 맞춘 네모 (켜지면 면 전체가 하이라이트 색)
            _checkStyle ??= new Style(x => x.OfType<CheckBox>())
            {
                Setters = { new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<CheckBox>((cb, scope) => CheckTemplate(cb, scope))) },
            };
            if (on && !app.Styles.Contains(_checkStyle)) app.Styles.Add(_checkStyle);
            else if (!on) app.Styles.Remove(_checkStyle);
            UiTheme.Apply(s);   // 단추 색을 테마 값으로 되돌린 뒤 (켜져 있으면 ButtonResources가 덮는다)
            Changed?.Invoke();
        }

        /// <summary>단추 바탕을 장비 키 모양으로 (UiTheme.Apply 끝에서도 부른다 — 테마를 바꿔도 유지)</summary>
        public static void ButtonResources(Application app)
        {
            if (!On) return;
            bool dark = app.ActualThemeVariant != ThemeVariant.Light;
            var baseCol = dark ? Color.FromRgb(0x30, 0x32, 0x38) : Color.FromRgb(0xE4, 0xE6, 0xEA);
            if (app.Resources.ThemeDictionaries.TryGetValue(dark ? ThemeVariant.Dark : ThemeVariant.Light, out var d) && d is Avalonia.Controls.ResourceDictionary rd && rd.TryGetValue(Ui.KCard, out var c) && c is ISolidColorBrush cb)
                baseCol = dark ? UiTheme.Lerp(cb.Color, Colors.White, 0.04) : UiTheme.Lerp(cb.Color, Colors.Black, 0.05);
            IBrush Grad(Color top, Color bottom) => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
            };
            var top = UiTheme.Lerp(baseCol, Colors.White, dark ? 0.07 : 0.5);
            var bottom = UiTheme.Lerp(baseCol, Colors.Black, dark ? 0.3 : 0.08);
            IBrush normal, hover, press;
            if (Flat)
            {
                // 플랫: 단색 (카드보다 살짝 밝게), 마우스 올림은 조금 더 밝게, 누름은 어둡게
                var f = dark ? UiTheme.Lerp(baseCol, Colors.White, 0.03) : UiTheme.Lerp(baseCol, Colors.White, 0.35);
                normal = new SolidColorBrush(f); hover = new SolidColorBrush(UiTheme.Lerp(f, Colors.White, dark ? 0.06 : 0.3)); press = new SolidColorBrush(UiTheme.Lerp(f, Colors.Black, 0.15));
            }
            else
            {
                normal = Grad(top, bottom);
                hover = Grad(UiTheme.Lerp(top, Colors.White, 0.05), UiTheme.Lerp(bottom, Colors.White, 0.04));
                press = Grad(UiTheme.Lerp(bottom, Colors.Black, 0.1), UiTheme.Lerp(top, Colors.Black, 0.1));   // 눌리면 음영이 뒤집힌다
            }
            var border = new SolidColorBrush(dark ? UiTheme.Lerp(baseCol, Colors.Black, 0.32) : UiTheme.Lerp(baseCol, Colors.Black, 0.25));
            app.Resources["ButtonBackground"] = normal; app.Resources["ButtonBackgroundPointerOver"] = hover; app.Resources["ButtonBackgroundPressed"] = press;
            app.Resources["ToggleButtonBackground"] = normal; app.Resources["ToggleButtonBackgroundPointerOver"] = hover; app.Resources["ToggleButtonBackgroundPressed"] = press;
            app.Resources["ButtonBorderBrush"] = border; app.Resources["ButtonBorderBrushPointerOver"] = border; app.Resources["ButtonBorderBrushPressed"] = border;
            app.Resources["ControlCornerRadius"] = new CornerRadius(Flat ? 4 : 5);
            if (Unify) UnifyResources(app, dark);
            // 드롭다운 목록: 고른 줄에 하이라이트 막대 (탭의 LED처럼)
            var hlc = app.Resources.ThemeDictionaries.TryGetValue(dark ? ThemeVariant.Dark : ThemeVariant.Light, out var hd) && hd is Avalonia.Controls.ResourceDictionary hrd && hrd.TryGetValue(Ui.KDial, out var ho) && ho is ISolidColorBrush hb
                ? hb.Color : app.Resources.TryGetValue(Ui.KAccent, out var ao) && ao is ISolidColorBrush ab ? ab.Color : UiTheme.Neon;
            FieldStyles.DialLook(app, true, hlc);
        }

        /// <summary>UI 전체 통일: 카드는 장비 앞판처럼 조금 어둡게 · 테두리 진하게, 입력 칸·콤보는 움푹한 어두운 칸, 체크 상자는 켜지면 하이라이트 색</summary>
        static void UnifyResources(Application app, bool dark)
        {
            var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            if (!app.Resources.ThemeDictionaries.TryGetValue(variant, out var dd) || dd is not Avalonia.Controls.ResourceDictionary d) return;
            Color Col(string k, Color f) => d.TryGetValue(k, out var o) && o is ISolidColorBrush b ? b.Color : f;
            var card = Col(Ui.KCard, dark ? Color.FromRgb(0x2B, 0x2B, 0x31) : Colors.White);
            var panel = Col(Ui.KPanel, card);
            d[Ui.KCard] = new SolidColorBrush(dark ? UiTheme.Lerp(card, Colors.Black, 0.10) : UiTheme.Lerp(card, Color.FromRgb(0xE8, 0xEA, 0xEE), 0.6));
            d[Ui.KCardStroke] = new SolidColorBrush(dark ? UiTheme.Lerp(card, Colors.Black, 0.3) : UiTheme.Lerp(card, Colors.Black, 0.2));
            d[Ui.KDivider] = new SolidColorBrush(dark ? UiTheme.Lerp(card, Colors.Black, 0.35) : UiTheme.Lerp(card, Colors.Black, 0.12));
            var inset = dark ? UiTheme.Lerp(panel, Colors.Black, 0.45) : UiTheme.Lerp(card, Color.FromRgb(0xDA, 0xDD, 0xE2), 0.8);
            var insetHover = UiTheme.Lerp(inset, dark ? Colors.White : Colors.Black, 0.04);
            var line = new SolidColorBrush(dark ? UiTheme.Lerp(panel, Colors.Black, 0.3) : UiTheme.Lerp(card, Colors.Black, 0.25));
            var hl = d.TryGetValue(Ui.KDial, out var o) && o is ISolidColorBrush hb ? hb.Color : (app.Resources.TryGetValue(Ui.KAccent, out var a) && a is ISolidColorBrush ab ? ab.Color : UiTheme.Neon);
            app.Resources["TextControlBackground"] = new SolidColorBrush(inset); app.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(insetHover); app.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(inset);
            app.Resources["TextControlBorderBrush"] = line; app.Resources["TextControlBorderBrushPointerOver"] = line; app.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(hl, 0.8);
            app.Resources["ComboBoxBackground"] = new SolidColorBrush(inset); app.Resources["ComboBoxBackgroundPointerOver"] = new SolidColorBrush(insetHover); app.Resources["ComboBoxBackgroundPressed"] = new SolidColorBrush(inset);
            app.Resources["ComboBoxBorderBrush"] = line; app.Resources["ComboBoxBorderBrushPointerOver"] = line;
            var hlB = new SolidColorBrush(hl); var dark1 = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18));
            foreach (var k in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked" }) app.Resources[k] = hlB;
            foreach (var k in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed" }) app.Resources[k] = dark1;
        }

        /// <summary>끌 때 지울 단추 키 (UiTheme.Apply가 테마 값으로 다시 넣는다)</summary>
        public static void ClearButtonResources(Application app)
        {
            foreach (var k in Keys) app.Resources.Remove(k);
            FieldStyles.DialLook(app, false);
        }

        /// <summary>장비 키 바탕 (페이지 안 탭)</summary>
        public static IBrush KeyBrush(Control c, bool hover)
        {
            var card = c.TryFindResource(Ui.KCard, c.ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : Color.FromRgb(0x2B, 0x2B, 0x31);
            bool dark = c.ActualThemeVariant != ThemeVariant.Light;
            if (Flat) return new SolidColorBrush(UiTheme.Lerp(card, dark ? Colors.White : Colors.Black, dark ? (hover ? 0.09 : 0.04) : (hover ? 0.06 : 0.03)));
            var top = UiTheme.Lerp(card, Colors.White, dark ? (hover ? 0.13 : 0.08) : 0.5);
            var bottom = UiTheme.Lerp(card, Colors.Black, dark ? 0.3 : 0.08);
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
            };
        }

        public static IBrush KeyBorder(Control c)
        {
            var card = c.TryFindResource(Ui.KCard, c.ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : Color.FromRgb(0x2B, 0x2B, 0x31);
            return new SolidColorBrush(UiTheme.Lerp(card, Colors.Black, c.ActualThemeVariant != ThemeVariant.Light ? 0.32 : 0.2));
        }

        static Control CheckTemplate(CheckBox cb, INameScope scope)
        {
            var face = new CheckFace { Width = 18, Height = 18, State = cb.IsChecked, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            cb.IsCheckedChanged += (_, __) => face.State = cb.IsChecked;
            cb.PointerEntered += (_, __) => face.Hover = true;
            cb.PointerExited += (_, __) => face.Hover = false;
            var presenter = new ContentPresenter { Name = "PART_ContentPresenter", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            presenter.Bind(ContentPresenter.ContentProperty, cb.GetObservable(ContentControl.ContentProperty));
            presenter.RegisterInNameScope(scope);
            void Vis() => presenter.IsVisible = cb.Content != null && !(cb.Content is string str && str.Length == 0);   // 글 없는 체크 상자(블렌드쉐이프 줄 고르기)는 네모만
            Vis();
            cb.PropertyChanged += (_, e) => { if (e.Property == ContentControl.ContentProperty) Vis(); };
            Avalonia.Controls.DockPanel.SetDock(face, Avalonia.Controls.Dock.Left);
            return new Avalonia.Controls.DockPanel { Background = Brushes.Transparent, Children = { face, presenter } };
        }

        static Control RockerTemplate(ToggleSwitch ts, INameScope scope)
        {
            // ToggleSwitch가 템플릿에서 찾는 부품 (없으면 예외) — 보이지 않게 둔다
            var knobs = new Canvas { Name = "PART_MovingKnobs", IsVisible = false };
            var knob = new Canvas { Name = "PART_SwitchKnob", IsVisible = false };
            knobs.RegisterInNameScope(scope); knob.RegisterInNameScope(scope);
            var face = new RockerFace { Width = 48, Height = 24, On = ts.IsChecked == true };
            ts.IsCheckedChanged += (_, __) => face.On = ts.IsChecked == true;
            return new Panel { Background = Brushes.Transparent, Children = { knobs, knob, face } };
        }
    }

    /// <summary>가로 로커 스위치 그림. 눌린 면이 꺼지면 왼쪽, 켜지면 오른쪽으로 미끄러지고 하이라이트도 따라간다 (꺼짐은 옅게, 켜짐은 하이라이트 색 그대로)</summary>
    sealed class RockerFace : Control
    {
        /// <summary>눌린 면 자리: 0 = 왼쪽(꺼짐), 1 = 오른쪽(켜짐). On을 바꾸면 짧게 미끄러진다</summary>
        public static readonly StyledProperty<double> PosProperty = AvaloniaProperty.Register<RockerFace, double>(nameof(Pos));
        static RockerFace() { AffectsRender<RockerFace>(PosProperty); }
        public double Pos { get => GetValue(PosProperty); set => SetValue(PosProperty, value); }

        bool _on;
        public bool On { get => _on; set { _on = value; Pos = value ? 1 : 0; } }

        public RockerFace()
        {
            Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = PosProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new Avalonia.Animation.Easings.CubicEaseOut() } };
            ActualThemeVariantChanged += (_, __) => InvalidateVisual();
            ResourcesChanged += (_, __) => InvalidateVisual();
            PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty || e.Property == IsEffectivelyEnabledProperty) InvalidateVisual(); };
        }

        static IBrush V(Color a, Color b) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };

        public override void Render(DrawingContext ctx)
        {
            var w = Bounds.Width; var h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            double dim = IsEffectivelyEnabled ? 1 : 0.45;
            // 틀: 아주 어두운 홈 + 바깥 한 줄
            var frame = new Rect(0.5, 0.5, w - 1, h - 1);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x26), dim), new Pen(new SolidColorBrush(Color.FromArgb((byte)(150 * dim), 0x5E, 0x62, 0x6B)), 1), frame, 5, 5);
            var inner = frame.Deflate(2.5);
            double half = inner.Width / 2, pos = Math.Clamp(Pos, 0, 1);
            // 올라온 면을 통째로 깔고 (위가 밝은 그라데이션 + 윗줄 빛), 눌린 면이 그 위를 Pos 자리로 미끄러진다
            if (Hardware.Flat) ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44), dim), null, inner, 3, 3);
            else
            {
                ctx.DrawRectangle(V(Color.FromArgb((byte)(255 * dim), 0x4A, 0x4D, 0x55), Color.FromArgb((byte)(255 * dim), 0x26, 0x28, 0x2D)), null, inner, 3, 3);
                ctx.DrawLine(new Pen(new SolidColorBrush(Colors.White, 0.18 * dim), 1), new Point(inner.X + 3, inner.Y + 1), new Point(inner.Right - 3, inner.Y + 1));
            }
            // 가운데 이음매
            ctx.DrawLine(new Pen(new SolidColorBrush(Colors.Black, 0.3 * dim), 1), new Point(inner.X + half, inner.Y + 1), new Point(inner.X + half, inner.Bottom - 1));
            var pressed = new Rect(inner.X + half * pos, inner.Y, half, inner.Height);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x29, 0x2B, 0x31), dim), null, pressed, 3, 3);
            // 눌린 면 전체가 빛난다 (작은 점보다 잘 보이게): 꺼짐(왼쪽)은 어두운 회색에 하이라이트를 조금, 켜짐(오른쪽)은 하이라이트 색 그대로
            var hl = Dial.Highlight(this);
            var col = UiTheme.Lerp(UiTheme.Lerp(Color.FromRgb(0x2E, 0x30, 0x36), hl, 0.3), hl, pos);
            var face = pressed.Deflate(1);
            Color A(Color c) => Color.FromArgb((byte)(255 * dim), c.R, c.G, c.B);
            if (Hardware.Flat) ctx.DrawRectangle(new SolidColorBrush(col, dim), null, face, 2.5, 2.5);
            else ctx.DrawRectangle(V(A(UiTheme.Lerp(col, Colors.White, 0.25)), A(UiTheme.Lerp(col, Colors.Black, 0.15))), null, face, 2.5, 2.5);
        }
    }

    /// <summary>다이얼 모드 체크 상자: 꺼지면 움푹한 어두운 네모, 켜지면 면 전체가 하이라이트 색 + 어두운 체크 (입체면 위가 밝은 음영)</summary>
    sealed class CheckFace : Control
    {
        bool? _state; bool _hover;
        public bool? State { get => _state; set { _state = value; InvalidateVisual(); } }
        public bool Hover { get => _hover; set { _hover = value; InvalidateVisual(); } }

        public CheckFace()
        {
            ActualThemeVariantChanged += (_, __) => InvalidateVisual();
            ResourcesChanged += (_, __) => InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
            if (r.Width <= 0) return;
            double dim = IsEffectivelyEnabled ? 1 : 0.45;
            bool dark = ActualThemeVariant != ThemeVariant.Light;
            var card = this.TryFindResource(Ui.KCard, ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : Color.FromRgb(0x2B, 0x2B, 0x31);
            var hl = Dial.Highlight(this);
            double rad = Hardware.Flat ? 3 : 4;
            if (_state == false)
            {
                var fill = dark ? UiTheme.Lerp(card, Colors.Black, _hover ? 0.2 : 0.28) : UiTheme.Lerp(card, Colors.Black, _hover ? 0.04 : 0.07);
                var edge = dark ? UiTheme.Lerp(card, Colors.White, _hover ? 0.28 : 0.2) : UiTheme.Lerp(card, Colors.Black, 0.3);
                IBrush f = Hardware.Flat ? new SolidColorBrush(fill, dim) : new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(UiTheme.Lerp(fill, Colors.Black, 0.2), 0), new GradientStop(UiTheme.Lerp(fill, Colors.White, 0.06), 1) },   // 움푹: 위가 어둡다
                };
                ctx.DrawRectangle(f, new Pen(new SolidColorBrush(edge, dim), 1), r, rad, rad);
                return;
            }
            IBrush on = Hardware.Flat ? new SolidColorBrush(hl, dim) : new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(UiTheme.Lerp(hl, Colors.White, 0.25), 0), new GradientStop(UiTheme.Lerp(hl, Colors.Black, 0.15), 1) },
            };
            ctx.DrawRectangle(on, new Pen(new SolidColorBrush(UiTheme.Lerp(hl, Colors.Black, 0.25), dim), 1), r, rad, rad);
            var ink = new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1A), dim), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            double w = r.Width, h = r.Height;
            if (_state == null) { ctx.DrawLine(ink, new Point(r.X + w * 0.28, r.Y + h / 2), new Point(r.X + w * 0.72, r.Y + h / 2)); return; }
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(r.X + w * 0.25, r.Y + h * 0.52), false);
                c.LineTo(new Point(r.X + w * 0.43, r.Y + h * 0.7));
                c.LineTo(new Point(r.X + w * 0.76, r.Y + h * 0.32));
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, ink, g);
        }
    }
}
