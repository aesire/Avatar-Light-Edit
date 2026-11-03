// 다이얼 (2026-10-22, 사용자 요청): 가상 악기 프로그램의 노브처럼 둥근 손잡이 + 바깥 호(지금 값만큼 하이라이트 색).
// 납작한 모양에 아주 옅은 음영(위 밝고 아래 어두운 그라데이션·살짝 그림자)으로 입체감만 낸다.
//   손잡이 가운데를 잡고 위·오른쪽으로 끌면 커지고 아래·왼쪽이면 작아진다 (200px = 전체 범위, Shift는 10배 천천히).
//   바깥 테(호 쪽)를 잡으면 실제로 돌리듯이: 가운데를 중심으로 돈 각도만큼 (270° = 전체). 휠은 한 칸(범위의 1%)씩.
//   ShowScale이면 호 바깥에 눈금과 숫자(0 · 25 · 50 …)를 그린다 (쉐이프키 묶음 다이얼, 하드웨어 랙 모양).
//   두 번 누르면 ResetRequested (되돌리기). 끄는 동안 Changing, 놓을 때(휠은 잠깐 쉬면) Committed — 실행 취소 한 단계.
//   범위가 0을 가로지르면(-1 ~ 1 머슬 등) 호는 가운데(0)부터 칠한다.
// 하이라이트 색은 Ui.KDial (테마 설정 "다이얼 색", 기본 형광 노랑) — 없으면 강조색. 세밀 조정 막대도 같은 색.
// 설정 "슬라이더 모양 → 다이얼"이면 Ui.EditSlider의 슬라이더 템플릿이 이 다이얼로 바뀐다 (DialStyle).
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public sealed class Dial : Control
    {
        public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Dial, double>(nameof(Value));
        public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Dial, double>(nameof(Minimum), 0);
        public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Dial, double>(nameof(Maximum), 1);
        static Dial() { AffectsRender<Dial>(ValueProperty, MinimumProperty, MaximumProperty, IsEnabledProperty); }

        public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

        /// <summary>끄는 동안 / 휠 한 칸마다 (미리보기)</summary>
        public event Action<double>? Changing;
        /// <summary>놓을 때 / 휠을 멈추면 (저장)</summary>
        public event Action<double>? Committed;
        public event Action? DragStarted, ResetRequested;
        /// <summary>움직이지 않고 0.5초 누르고 있으면 (끌기는 취소된다). 구독한 곳이 있을 때만 기다린다</summary>
        public event Action? LongPressed;
        readonly DispatcherTimer _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        Point _pressAt;
        IPointer? _pointer;
        /// <summary>false면 끌기를 놓을 때 Committed를 내지 않는다 (슬라이더 템플릿: EditSlider가 놓을 때 저장한다)</summary>
        public bool CommitOnDrag = true;
        /// <summary>호 바깥에 눈금 숫자 (다섯 군데)</summary>
        public bool ShowScale;
        bool _turn;   // 바깥 테를 잡아 돌리는 중
        double _lastAngle;

        const double Start = 135, Sweep = 270;   // 왼쪽 아래에서 시계 방향으로 오른쪽 아래까지
        bool _down;
        Point _last;
        double _acc;
        readonly DispatcherTimer _wheelDone = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };

        public Dial()
        {
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            Focusable = false;
            _wheelDone.Tick += (_, __) => { _wheelDone.Stop(); Committed?.Invoke(Value); };
            _hold.Tick += (_, __) =>
            {
                _hold.Stop();
                if (!_down) return;
                _down = false;   // 끌기 취소 (값은 그대로)
                _pointer?.Capture(null);
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                LongPressed?.Invoke();
            };
            ActualThemeVariantChanged += (_, __) => InvalidateVisual();
            ResourcesChanged += (_, __) => InvalidateVisual();
        }

        double Range => Maximum - Minimum;
        double Frac(double v) => Range > 0 ? Math.Clamp((v - Minimum) / Range, 0, 1) : 0;

        static Point On(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static Geometry Arc(Point c, double r, double from, double to)
        {
            if (to < from) (from, to) = (to, from);
            if (to - from < 0.01) to = from + 0.01;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(On(c, r, from), false);
                ctx.ArcTo(On(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            return g;
        }

        Color C(string key, Color fallback)
            => this.TryFindResource(key, ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : fallback;

        /// <summary>다이얼·세밀 조정 막대의 하이라이트 색 (테마 "다이얼 색", 없으면 강조색)</summary>
        public static Color Highlight(Control c)
        {
            if (c.TryFindResource(Ui.KDial, c.ActualThemeVariant, out var o) && o is ISolidColorBrush b) return b.Color;
            return c.TryFindResource(Ui.KAccent, c.ActualThemeVariant, out var a) && a is ISolidColorBrush ab ? ab.Color : Color.FromRgb(0xD9, 0xF5, 0x3B);
        }

        public override void Render(DrawingContext ctx)
        {
            double size = Math.Min(Bounds.Width, Bounds.Height);
            if (size <= 4) return;
            var c = Center;
            double R = Radius;
            if (ShowScale) DrawScale(ctx, c, R);
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            double dim = IsEnabled ? 1 : 0.45;
            var card = C(Ui.KCard, dark ? Color.FromRgb(0x2B, 0x2B, 0x31) : Colors.White);

            // 바깥 호: 회색 길 + 지금 값만큼 하이라이트
            double thick = Math.Max(2.5, R * 0.11), ringR = R - thick / 2 - 0.5;
            var trackCol = dark ? Color.FromArgb((byte)(110 * dim), 0x8A, 0x90, 0x9A) : Color.FromArgb((byte)(150 * dim), 0x8A, 0x90, 0x9A);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(trackCol), thick, lineCap: PenLineCap.Round), Arc(c, ringR, Start, Start + Sweep));
            double f = Frac(Value);
            double zero = Minimum < 0 && Maximum > 0 ? Frac(0) : 0;
            if (Math.Abs(f - zero) > 0.002)
            {
                var hl = Highlight(this);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(hl, dim), thick, lineCap: PenLineCap.Round), Arc(c, ringR, Start + Sweep * zero, Start + Sweep * f));
            }

            // 손잡이: 살짝 그림자 → 바깥 테(어두운 고리) → 얼굴(위 밝고 아래 어두운) → 얼굴 테두리 한 줄
            double knobR = R * 0.70, faceR = R * 0.52;
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(dark ? 90 : 45), 0, 0, 0)), null, new Point(c.X, c.Y + R * 0.06), knobR + 0.8, knobR + 0.8);
            var ringTop = dark ? UiTheme.Lerp(card, Colors.Black, 0.35) : UiTheme.Lerp(card, Colors.Black, 0.55);
            var ringBot = UiTheme.Lerp(ringTop, Colors.Black, 0.35);
            ctx.DrawEllipse(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(ringTop, 0), new GradientStop(ringBot, 1) },
            }, null, c, knobR, knobR);
            var faceTop = UiTheme.Lerp(ringTop, Colors.White, dark ? 0.10 : 0.12);
            var faceBot = UiTheme.Lerp(ringTop, Colors.Black, 0.18);
            ctx.DrawEllipse(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.3, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.7, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(faceTop, 0), new GradientStop(faceBot, 1) },
            }, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Math.Max(1, R * 0.03)), c, faceR, faceR);

            // 눈금 막대: 얼굴 가장자리에서 바깥 테까지, 밝은 색
            double ang = Start + Sweep * f;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(235 * dim), 0xF4, 0xF4, 0xF4)), Math.Max(1.8, R * 0.09), lineCap: PenLineCap.Round), On(c, faceR * 0.82, ang), On(c, knobR * 0.98, ang));
        }

        Point Center { get { double size = Math.Min(Bounds.Width, Bounds.Height); return new Point(Bounds.Width / 2, Bounds.Height / 2 + size * 0.03 + (ShowScale ? 2 : 0)); } }
        double Radius => Math.Min(Bounds.Width, Bounds.Height) / 2 - (ShowScale ? 13 : 0);

        void DrawScale(DrawingContext ctx, Point c, double R)
        {
            var col = C(Ui.KText3, Colors.Gray);
            var pen = new Pen(new SolidColorBrush(col), 1);
            for (int i = 0; i <= 10; i++)
            {
                double a = Start + Sweep * i / 10;
                ctx.DrawLine(pen, On(c, R + 1.5, a), On(c, R + (i % 5 == 0 ? 5 : 3.5), a));
            }
            for (int i = 0; i <= 4; i++)
            {
                double v = Minimum + Range * i / 4, a = Start + Sweep * i / 4;
                var ft = new FormattedText(Ui.F(Math.Round(v, Range >= 10 ? 0 : 2)), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 8.5, new SolidColorBrush(col));
                var p = On(c, R + 10, a);
                ctx.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
            }
        }

        double AngleAt(Point p) { var c = Center; return Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI; }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetCurrentPoint(this);
            if (!IsEnabled || !pt.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { ResetRequested?.Invoke(); e.Handled = true; return; }
            _wheelDone.Stop();
            _down = true; _last = e.GetPosition(null); _acc = Value;
            // 바깥 테(손잡이 얼굴 밖)를 잡으면 돌리기, 가운데면 위아래 끌기
            var lp = e.GetPosition(this);
            double dist = Math.Sqrt(Math.Pow(lp.X - Center.X, 2) + Math.Pow(lp.Y - Center.Y, 2));
            _turn = dist > Radius * 0.56;
            _pressAt = e.GetPosition(null); _pointer = e.Pointer;
            if (LongPressed != null) { _hold.Stop(); _hold.Start(); }
            _lastAngle = AngleAt(lp);
            Cursor = new Cursor(_turn ? StandardCursorType.Hand : StandardCursorType.SizeNorthSouth);
            DragStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_down) return;
            var p = e.GetPosition(null);
            if (_hold.IsEnabled && Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 4) _hold.Stop();
            double slow = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1;
            if (_turn)
            {
                double ang = AngleAt(e.GetPosition(this));
                double da = ang - _lastAngle;
                if (da > 180) da -= 360; else if (da < -180) da += 360;   // 9시 방향을 넘을 때
                _lastAngle = ang;
                _acc = Math.Clamp(_acc + da / Sweep * Range * slow, Minimum, Maximum);
            }
            else
            {
                double d = (p.X - _last.X) - (p.Y - _last.Y);
                _acc = Math.Clamp(_acc + d * Range / 200 * slow, Minimum, Maximum);
            }
            _last = p;
            if (_acc == Value) return;
            Value = _acc;
            Changing?.Invoke(_acc);
        }

        void End()
        {
            _hold.Stop();
            if (!_down) return;
            _down = false;
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            if (CommitOnDrag) Committed?.Invoke(Value);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); End(); e.Pointer.Capture(null); }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); End(); }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (!IsEnabled || _down || Range <= 0) return;
            double step = Range / 100 * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1);
            var v = Math.Clamp(Value + Math.Sign(e.Delta.Y) * step, Minimum, Maximum);
            e.Handled = true;
            if (v == Value) return;
            Value = v;
            Changing?.Invoke(v);
            _wheelDone.Stop(); _wheelDone.Start();
        }

        // ---------------- 설정 "슬라이더 모양 → 다이얼" ----------------
        static Style? _sliderStyle;

        /// <summary>EditSlider의 슬라이더(클래스 "vp")를 다이얼 모양으로 (앱 전체 스타일 — 에디터·떠 있는 창 모두)</summary>
        public static void UseForSliders(bool on)
        {
            var app = Application.Current;
            if (app == null) return;
            _sliderStyle ??= new Style(x => x.OfType<Slider>().Class("vp"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Slider>((s, _) => Templated(s))),
                    new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 34.0),
                },
            };
            if (on && !app.Styles.Contains(_sliderStyle)) app.Styles.Add(_sliderStyle);
            else if (!on) app.Styles.Remove(_sliderStyle);
        }

        static Control Templated(Slider s)
        {
            var d = new Dial { Width = 34, Height = 34, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, CommitOnDrag = false };
            d.Bind(ValueProperty, s.GetObservable(RangeBase.ValueProperty));
            d.Bind(MinimumProperty, s.GetObservable(RangeBase.MinimumProperty));
            d.Bind(MaximumProperty, s.GetObservable(RangeBase.MaximumProperty));
            // 끄는 동안: EditSlider의 미리보기 (놓을 때 저장은 EditSlider가 슬라이더 포인터 뗌으로 한다). 휠: 미리보기 + 쉬면 저장
            d.Changing += v => { if (Ui.EditOf(s) is { } es) es.Fine().Preview(v); else s.Value = v; };
            d.Committed += v => { if (Ui.EditOf(s) is { } es) es.Fine().Commit(v); };
            ToolTip.SetTip(d, "위·오른쪽으로 끌면 커지고 아래·왼쪽이면 작아져요 (Shift: 천천히 · 휠: 한 칸)");
            return d;
        }
    }
}
