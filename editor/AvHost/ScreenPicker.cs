// 화면 스포이드 (색 조화 탭): 모니터마다 거의 투명한 창을 맨 위에 덮고, 커서 아래 화면 픽셀 색을 읽는다.
// 덮는 창이 누르기를 받으므로 아래 창(에디터 단추·3D 미리보기)에는 클릭이 가지 않는다. 왼쪽 클릭 = 고르기, 오른쪽 클릭·Esc = 그만.
// 색은 Win32 화면 DC의 GetPixel로 읽는다 (합성된 화면이라 Unity 미리보기도 읽힌다). 덮는 창은 알파 1/255라 색이 거의 바뀌지 않는다.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public static class ScreenPicker
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

        static bool _active;

        /// <summary>지금 커서 아래 화면 색.</summary>
        public static Color? Sample()
        {
            if (!OperatingSystem.IsWindows() || !GetCursorPos(out var p)) return null;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                uint c = GetPixel(dc, p.X, p.Y);
                if (c == 0xFFFFFFFF) return null;   // CLR_INVALID (화면 밖)
                return Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        /// <summary>스포이드 시작. 고르면 done(색), 그만두면 done(null). hover는 커서가 움직일 때마다.</summary>
        public static void Pick(TopLevel owner, Action<Color?> done, Action<Color>? hover = null)
        {
            if (_active) return;
            if (!OperatingSystem.IsWindows() || owner.Screens is not { } screens) { done(null); return; }   // 화면 목록이 없을 때 _active를 켠 채 멈추지 않게 (2026-11-01)
            _active = true;
            var wins = new List<Window>();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            bool finished = false;
            Color last = Colors.Black;
            void Finish(Color? c)
            {
                if (finished) return;
                finished = true; _active = false;
                timer.Stop();
                foreach (var w in wins) w.Close();
                done(c);
            }
            // 커서 옆 읽기 표시 (견본 + 색 코드). 각 창이 하나씩 갖고, 커서가 있는 창 것만 보인다
            var tags = new List<(Window W, Border Tag, Border Swatch, TextBlock Hex)>();
            foreach (var screen in screens.All)
            {
                var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
                var hex = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(swatch); row.Children.Add(hex);
                var help = new TextBlock { Text = "클릭: 고르기 · 오른쪽 클릭·Esc: 그만", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
                var inner = new StackPanel(); inner.Children.Add(row); inner.Children.Add(help);
                var tag = new Border { Child = inner, Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 28)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), IsVisible = false, IsHitTestVisible = false };
                var canvas = new Canvas(); canvas.Children.Add(tag);
                var w = new Window
                {
                    SystemDecorations = SystemDecorations.None, Topmost = true, ShowInTaskbar = false, CanResize = false,
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                    WindowStartupLocation = WindowStartupLocation.Manual, Position = screen.Bounds.Position,
                    Width = screen.Bounds.Width / screen.Scaling, Height = screen.Bounds.Height / screen.Scaling,
                    Cursor = new Cursor(StandardCursorType.Cross), Content = canvas, Title = "스포이드",
                };
                w.PointerMoved += (_, e) =>
                {
                    var p = e.GetPosition(canvas);
                    foreach (var t in tags) t.Tag.IsVisible = t.W == w;
                    // 커서 오른쪽 아래, 화면 끝이면 반대쪽으로
                    double x = p.X + 18, y = p.Y + 18;
                    if (x + 200 > w.Bounds.Width) x = p.X - 210;
                    if (y + 60 > w.Bounds.Height) y = p.Y - 64;
                    Canvas.SetLeft(tag, x); Canvas.SetTop(tag, y);
                };
                w.PointerPressed += (_, e) =>
                {
                    var k = e.GetCurrentPoint(w).Properties.PointerUpdateKind;
                    e.Handled = true;
                    if (k == PointerUpdateKind.LeftButtonPressed) Finish(Sample() ?? last);
                    else Finish(null);
                };
                w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
                w.Closed += (_, __) => Finish(null);   // Alt+F4 등
                tags.Add((w, tag, swatch, hex));
                wins.Add(w);
            }
            timer.Tick += (_, __) =>
            {
                if (Sample() is not Color c) return;
                last = c;
                foreach (var t in tags) { t.Swatch.Background = new SolidColorBrush(c); t.Hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
                hover?.Invoke(c);
            };
            foreach (var w in wins) w.Show();
            // 커서가 있는 모니터 창에 키보드 초점 (Esc)
            if (GetCursorPos(out var cp))
                foreach (var w in wins)
                    if (new PixelRect(w.Position, PixelSize.FromSize(w.ClientSize, w.RenderScaling)).Contains(new PixelPoint(cp.X, cp.Y))) w.Activate();
            timer.Start();
        }
    }
}
