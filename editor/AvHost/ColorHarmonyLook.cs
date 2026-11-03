// 색 조화 탭의 그림 (2026-10-24): 색상환 · 규칙 단추 · 견본을 직접 그려 테마와 조작 모양을 따른다.
//   · 보통: 카드 위의 깔끔한 원판 (얇은 테두리·옅은 그림자), 고른 것은 강조색.
//   · 다이얼 모드 플랫: 다이얼처럼 어두운 테(베젤)에 끼운 원판, 단추는 장비 키(Hardware.KeyBrush) + 고르면 LED, 견본은 움푹한 칸. 고른 것은 하이라이트 색(Ui.KDial).
//   · 다이얼 모드 입체: 위가 밝은 베젤 그라데이션·그림자, 원판 위 유리 반사와 안쪽 그림자, 점은 반들거리는 구슬.
// 테마·조작 모양이 바뀌면 다시 그린다 (ActualThemeVariant·리소스 바뀜 · Hardware.Changed는 패널이 InvalidateVisual).
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    /// <summary>색 조화 그림들이 같이 쓰는 색.</summary>
    static class HarmonyLook
    {
        public static Color Res(Control c, string key, Color fallback)
            => c.TryFindResource(key, c.ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : fallback;
        public static bool Dark(Control c) => c.ActualThemeVariant != ThemeVariant.Light;
        public static Color Card(Control c) => Res(c, Ui.KCard, Dark(c) ? Color.FromRgb(0x2B, 0x2B, 0x31) : Colors.White);
        /// <summary>고른 것 표시 색: 다이얼 모드는 하이라이트 색, 보통은 강조색</summary>
        public static Color Mark(Control c) => Hardware.On ? Dial.Highlight(c) : Res(c, Ui.KAccent, Color.FromRgb(0x3B, 0x82, 0xF6));
        /// <summary>다이얼 모드의 어두운 테 (다이얼 손잡이 바깥 테와 같은 계열)</summary>
        public static Color Bezel(Control c) => Dark(c) ? UiTheme.Lerp(Card(c), Colors.Black, 0.35) : UiTheme.Lerp(Card(c), Colors.Black, 0.45);
        /// <summary>움푹한 칸 바탕</summary>
        public static Color Inset(Control c) => Dark(c) ? UiTheme.Lerp(Card(c), Colors.Black, 0.45) : UiTheme.Lerp(Card(c), Colors.Black, 0.18);
        public static IBrush V(Color top, Color bottom, double end = 1) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, end, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        public static Color A(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
        public static Color ToColor(Hsv c) { var (r, g, b) = ColorHarmony.ToBytes(c); return Color.FromRgb(r, g, b); }

        public static void Hook(Control c)
        {
            c.ActualThemeVariantChanged += (_, __) => c.InvalidateVisual();
            c.ResourcesChanged += (_, __) => c.InvalidateVisual();
        }
    }

    /// <summary>색상환: 원판(밝기는 SetValue) + 묶음의 점들. 점 위치·색상/채도 변환도 여기서.</summary>
    sealed class HarmonyWheel : Control
    {
        public const double Size = 264;
        const int Px = 480;   // 고해상도 화면에서도 또렷하게 두 배로 그린다
        readonly WriteableBitmap _bmp = new WriteableBitmap(new PixelSize(Px, Px), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        readonly float[] _rgb = new float[Px * Px * 3];   // 밝기 1일 때 색 (밝기만 바뀌면 곱하기만)
        readonly byte[] _alpha = new byte[Px * Px];
        double _v = -1;
        public Hsv[] Set = Array.Empty<Hsv>();
        public int Sel;

        public HarmonyWheel()
        {
            Width = Size; Height = Size;
            Cursor = new Cursor(StandardCursorType.Hand);
            HarmonyLook.Hook(this);
            double c = Px / 2.0, rad = c - 1;
            for (int y = 0; y < Px; y++)
                for (int x = 0; x < Px; x++)
                {
                    double dx = x + 0.5 - c, dy = y + 0.5 - c, d = Math.Sqrt(dx * dx + dy * dy);
                    int i = y * Px + x;
                    _alpha[i] = (byte)Math.Round(Math.Clamp(rad - d + 0.5, 0, 1) * 255);   // 가장자리 1px 부드럽게
                    var (r, g, b) = ColorHarmony.ToRgb(new Hsv(Math.Atan2(-dy, dx) * 180 / Math.PI, Math.Min(1, d / rad), 1));
                    _rgb[i * 3] = (float)r; _rgb[i * 3 + 1] = (float)g; _rgb[i * 3 + 2] = (float)b;
                }
        }

        /// <summary>테 두께: 다이얼 모드는 베젤 자리를 남긴다 (모드를 바꿔도 크기는 그대로, 원판만 조금 작아진다)</summary>
        static double Pad => Hardware.On ? 15 : 5;
        Point C => new Point(Bounds.Width / 2, Bounds.Height / 2);
        double R => Math.Min(Bounds.Width, Bounds.Height) / 2 - Pad;

        public void SetValue(double v)
        {
            if (Math.Abs(_v - v) < 1e-4) return;
            _v = v;
            var buf = new byte[Px * Px * 4];
            float fv = (float)v;
            for (int i = 0; i < _alpha.Length; i++)
            {
                float a = _alpha[i] / 255f;
                buf[i * 4] = (byte)(_rgb[i * 3 + 2] * fv * a * 255 + 0.5f);
                buf[i * 4 + 1] = (byte)(_rgb[i * 3 + 1] * fv * a * 255 + 0.5f);
                buf[i * 4 + 2] = (byte)(_rgb[i * 3] * fv * a * 255 + 0.5f);
                buf[i * 4 + 3] = _alpha[i];
            }
            using (var fb = _bmp.Lock())
                for (int y = 0; y < Px; y++) Marshal.Copy(buf, y * Px * 4, fb.Address + y * fb.RowBytes, Px * 4);
            InvalidateVisual();
        }

        public Point PosOf(Hsv c)
        {
            double a = c.H * Math.PI / 180, r = R - 1;
            return new Point(C.X + Math.Cos(a) * c.S * r, C.Y - Math.Sin(a) * c.S * r);
        }

        public (double H, double S) HueSatAt(Point p)
        {
            double dx = p.X - C.X, dy = p.Y - C.Y;
            return (Math.Atan2(-dy, dx) * 180 / Math.PI, Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (R - 1)));
        }

        public override void Render(DrawingContext ctx)
        {
            if (Bounds.Width <= 0) return;
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // 원판 밖 모서리도 누를 수 있게
            bool dark = HarmonyLook.Dark(this), hw = Hardware.On, skeuo = hw && !Hardware.Flat;
            var c = C; double r = R;
            var black = Colors.Black; var white = Colors.White;

            // ---- 바탕: 다이얼 모드는 베젤, 보통은 옅은 그림자 ----
            if (hw)
            {
                double outer = r + Pad - 1;
                var top = HarmonyLook.Bezel(this); var bottom = UiTheme.Lerp(top, black, 0.35);
                if (skeuo)
                {
                    ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.A(black, dark ? 0.38 : 0.2)), null, new Point(c.X, c.Y + 2.5), outer + 1, outer + 1);
                    ctx.DrawEllipse(HarmonyLook.V(top, bottom), null, c, outer, outer);
                    ctx.DrawEllipse(null, new Pen(HarmonyLook.V(HarmonyLook.A(white, 0.22), HarmonyLook.A(white, 0), 0.6), 1), c, outer - 0.5, outer - 0.5);   // 위 테두리 빛
                }
                else ctx.DrawEllipse(new SolidColorBrush(top), new Pen(new SolidColorBrush(UiTheme.Lerp(top, black, 0.3)), 1), c, outer - 0.5, outer - 0.5);
                // 베젤 눈금: 30°마다 (빨강·노랑·초록… 기본 색상 자리), 0°는 길게
                var tick = new Pen(new SolidColorBrush(HarmonyLook.A(white, skeuo ? 0.45 : 0.32)), 1.2, lineCap: PenLineCap.Round);
                for (int i = 0; i < 12; i++)
                {
                    double a = i * Math.PI / 6, r0 = r + 4, r1 = r + (i % 3 == 0 ? Pad - 4 : Pad - 7);
                    ctx.DrawLine(tick, new Point(c.X + Math.Cos(a) * r0, c.Y - Math.Sin(a) * r0), new Point(c.X + Math.Cos(a) * r1, c.Y - Math.Sin(a) * r1));
                }
                ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.A(black, 0.6)), null, c, r + 1.8, r + 1.8);   // 원판이 끼워진 홈
            }
            else ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.A(black, dark ? 0.35 : 0.1)), null, new Point(c.X, c.Y + 1.5), r + 1.5, r + 1.5);

            // ---- 원판 ----
            ctx.DrawImage(_bmp, new Rect(0, 0, Px, Px), new Rect(c.X - r, c.Y - r, r * 2, r * 2));
            if (skeuo)
            {
                ctx.DrawEllipse(HarmonyLook.V(HarmonyLook.A(white, 0.16), HarmonyLook.A(white, 0), 0.55), null, c, r, r);   // 유리 반사
                ctx.DrawEllipse(null, new Pen(HarmonyLook.V(HarmonyLook.A(black, 0.5), HarmonyLook.A(black, 0.05)), 2.5), c, r - 1.2, r - 1.2);   // 안쪽 그림자 (위가 어둡다)
            }
            else if (hw) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(HarmonyLook.A(black, 0.45)), 1), c, r - 0.5, r - 0.5);
            else ctx.DrawEllipse(null, new Pen(new SolidColorBrush(HarmonyLook.Res(this, Ui.KCardStroke, Colors.Gray)), 1), c, r + 0.5, r + 0.5);

            // ---- 가운데에서 점까지 선 (어두운 밑줄을 깔아 밝은 곳에서도 보이게) ----
            var under = new Pen(new SolidColorBrush(HarmonyLook.A(black, 0.28)), 4, lineCap: PenLineCap.Round);
            var line = new Pen(new SolidColorBrush(HarmonyLook.A(white, 0.92)), 2, lineCap: PenLineCap.Round);
            foreach (var s in Set) ctx.DrawLine(under, c, PosOf(s));
            foreach (var s in Set) ctx.DrawLine(line, c, PosOf(s));

            // ---- 가운데 축: 다이얼 모드는 작은 손잡이 ----
            if (hw)
            {
                var hub = HarmonyLook.Bezel(this);
                ctx.DrawEllipse(skeuo ? HarmonyLook.V(UiTheme.Lerp(hub, white, 0.25), UiTheme.Lerp(hub, black, 0.3)) : new SolidColorBrush(hub),
                    new Pen(new SolidColorBrush(HarmonyLook.A(white, 0.55)), 1), c, 6, 6);
            }
            else ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(HarmonyLook.A(black, 0.35)), 1), c, 4, 4);

            // ---- 점: 기본 색이 맨 위에 오게 거꾸로 ----
            var mark = HarmonyLook.Mark(this);
            for (int i = Set.Length - 1; i >= 0; i--)
            {
                var p = PosOf(Set[i]);
                double rad = i == 0 ? 13 : 10;
                ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.A(black, hw && Hardware.Flat ? 0.25 : 0.38)), null, new Point(p.X, p.Y + 1.5), rad + 1, rad + 1);
                if (i == Sel) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(mark), 2.5), p, rad + 3.5, rad + 3.5);
                ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.ToColor(Set[i])), new Pen(Brushes.White, i == Sel ? 3 : 2.2), p, rad, rad);
                if (skeuo) ctx.DrawEllipse(HarmonyLook.V(HarmonyLook.A(white, 0.5), HarmonyLook.A(white, 0), 0.55), null, new Point(p.X, p.Y - rad * 0.12), rad * 0.72, rad * 0.62);   // 구슬 반사
                if (i == 0) ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 4, 4);
            }
        }
    }

    /// <summary>조화 규칙 단추: 작은 색상환 그림. 다이얼 모드는 장비 키 + 고르면 LED.</summary>
    sealed class RuleChip : Control
    {
        public readonly HarmonyRule Rule;
        bool _sel, _hover;
        public bool Selected { get => _sel; set { if (_sel == value) return; _sel = value; InvalidateVisual(); } }

        public RuleChip(HarmonyRule rule)
        {
            Rule = rule;
            Width = 40; Height = 40; Margin = new Thickness(0, 0, 6, 6);
            Cursor = new Cursor(StandardCursorType.Hand);
            HarmonyLook.Hook(this);
            PointerEntered += (_, __) => { _hover = true; InvalidateVisual(); };
            PointerExited += (_, __) => { _hover = false; InvalidateVisual(); };
        }

        public override void Render(DrawingContext ctx)
        {
            var b = new Rect(Bounds.Size).Deflate(0.5);
            if (b.Width <= 0) return;
            bool hw = Hardware.On;
            var mark = HarmonyLook.Mark(this);
            double rad = hw ? (Hardware.Flat ? 4 : 5) : 6;
            IBrush bg = hw ? Hardware.KeyBrush(this, _hover)
                : new SolidColorBrush(HarmonyLook.Res(this, _sel ? Ui.KSelect : _hover ? Ui.KHover : Ui.KCard, HarmonyLook.Card(this)));
            IBrush edge = _sel && !hw ? new SolidColorBrush(mark) : hw ? Hardware.KeyBorder(this) : new SolidColorBrush(HarmonyLook.Res(this, Ui.KStroke, Colors.Gray));
            ctx.DrawRectangle(bg, new Pen(edge, _sel && !hw ? 1.5 : 1), b, rad, rad);
            if (hw && !Hardware.Flat) ctx.DrawLine(new Pen(new SolidColorBrush(HarmonyLook.A(Colors.White, 0.14)), 1), new Point(b.X + 4, b.Y + 1), new Point(b.Right - 4, b.Y + 1));   // 키 윗줄 빛
            if (hw)
            {
                // 왼쪽 위 LED: 고르면 하이라이트 색 (페이지 안 탭과 같은 말)
                var led = new Point(b.X + 6, b.Y + 6);
                if (_sel) ctx.DrawEllipse(new SolidColorBrush(HarmonyLook.A(mark, 0.3)), null, led, 4, 4);
                ctx.DrawEllipse(new SolidColorBrush(_sel ? mark : Color.FromRgb(0x2E, 0x30, 0x36)), null, led, 2, 2);
            }
            // 그림: 원 + 가운데에서 점까지 (단색·음영은 색상이 같아 채도×밝기로 반지름을 나눈다)
            var c = new Point(b.Center.X, b.Center.Y + (hw ? 1 : 0)); double R = 12;
            var t2 = HarmonyLook.Res(this, Ui.KText2, Colors.Gray); var t3 = HarmonyLook.Res(this, Ui.KText3, Colors.Gray);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(t3), 1), c, R, R);
            var dot = _sel ? mark : HarmonyLook.Res(this, Ui.KText, Colors.White);
            foreach (var h in ColorHarmony.Make(Rule, new Hsv(90, 1, 1)))
            {
                double rr = (R - 1) * h.S * h.V, a = h.H * Math.PI / 180;
                var p = new Point(c.X + Math.Cos(a) * rr, c.Y - Math.Sin(a) * rr);
                ctx.DrawLine(new Pen(new SolidColorBrush(t2), 1), c, p);
                ctx.DrawEllipse(new SolidColorBrush(dot), null, p, 2.6, 2.6);
            }
        }
    }

    /// <summary>견본 한 칸. 다이얼 모드는 움푹한 칸 안의 색 (입체면 위쪽 안 그림자와 유리 반사), 고르면 테두리가 하이라이트 색.</summary>
    sealed class SwatchFace : Control
    {
        Color _color; bool _sel;
        public Color Fill { get => _color; set { _color = value; InvalidateVisual(); } }
        public bool Selected { get => _sel; set { _sel = value; InvalidateVisual(); } }

        public SwatchFace() { HarmonyLook.Hook(this); }

        public override void Render(DrawingContext ctx)
        {
            var b = new Rect(Bounds.Size).Deflate(0.5);
            if (b.Width <= 2 || b.Height <= 2) return;
            bool hw = Hardware.On, skeuo = hw && !Hardware.Flat;
            var mark = HarmonyLook.Mark(this);
            var col = new SolidColorBrush(_color);
            if (!hw)
            {
                if (_sel)
                {
                    ctx.DrawRectangle(col, new Pen(new SolidColorBrush(mark), 2.5), b.Deflate(1), 6, 6);
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(HarmonyLook.A(Colors.White, 0.85)), 1), b.Deflate(3), 4, 4);   // 어떤 색 위에서도 테두리가 보이게
                }
                else ctx.DrawRectangle(col, new Pen(new SolidColorBrush(HarmonyLook.Res(this, Ui.KStroke, Colors.Gray)), 1), b, 6, 6);
                return;
            }
            double rad = Hardware.Flat ? 4 : 5;
            var inset = HarmonyLook.Inset(this);
            ctx.DrawRectangle(new SolidColorBrush(inset), new Pen(_sel ? new SolidColorBrush(mark) : new SolidColorBrush(UiTheme.Lerp(inset, Colors.Black, 0.35)), _sel ? 2 : 1), b, rad, rad);
            var face = b.Deflate(_sel ? 4 : 3.5);
            ctx.DrawRectangle(col, null, face, rad - 2, rad - 2);
            if (skeuo)
            {
                ctx.DrawRectangle(HarmonyLook.V(HarmonyLook.A(Colors.White, 0.2), HarmonyLook.A(Colors.White, 0), 0.5), null, face, rad - 2, rad - 2);   // 유리 반사
                ctx.DrawLine(new Pen(new SolidColorBrush(HarmonyLook.A(Colors.Black, 0.35)), 1.5), new Point(face.X + 2, face.Y + 0.75), new Point(face.Right - 2, face.Y + 0.75));   // 안쪽 그림자
            }
        }
    }
}
