// 창 안에 그린 팝업이 자식 창에 가리지 않게 (2026-10-24).
// 팝업을 창 안에 그리면(OverlayPopups, VR·창 공유용) 드롭다운·메뉴가 에디터 창 표면에 그려진다 → 그 위에 있는 자식 창
// (미리보기의 Unity 창, 에디터 안에 넣은 떠 있는 창)이 덮어 목록이 안 보였다. 열린 팝업 자리만큼 자식 창마다 창 영역(SetWindowRgn)에
// 구멍을 내서 그 자리는 에디터 창이 보이게 한다. 팝업이 다 닫히면 영역을 지운다. 팝업을 따로 뜬 창으로 그리면 오버레이 층이 비어 아무것도 하지 않는다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public static class PopupHoles
    {
        /// <summary>창이 열린 뒤에 한 번 부른다.</summary>
        public static void Attach(Window w)
        {
            if (!OperatingSystem.IsWindows()) return;
            var layer = OverlayLayer.GetOverlayLayer(w);
            if (layer == null) return;
            var holed = new HashSet<IntPtr>();
            string last = "";
            bool queued = false;
            void Queue()
            {
                if (queued) return;
                queued = true;
                Dispatcher.UIThread.Post(() => { queued = false; Update(); }, DispatcherPriority.Background);
            }
            void Update()
            {
                IntPtr parent = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (parent == IntPtr.Zero) return;
                // 열린 팝업 자리 (화면 좌표, 물리 픽셀)
                var holes = new List<RECT>();
                foreach (var c in layer.Children)
                {
                    if (c is not OverlayPopupHost || !c.IsVisible || c.Bounds.Width <= 0) continue;
                    if (c.TranslatePoint(default, w) is not Point p) continue;
                    var a = w.PointToScreen(p);
                    var b = w.PointToScreen(p + new Point(c.Bounds.Width, c.Bounds.Height));
                    holes.Add(new RECT { Left = a.X, Top = a.Y, Right = b.X, Bottom = b.Y });
                }
                var kids = new List<IntPtr>();
                for (var h = GetWindow(parent, GW_CHILD); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
                    if (IsWindowVisible(h)) kids.Add(h);
                var key = string.Join(";", holes.Select(r => $"{r.Left},{r.Top},{r.Right},{r.Bottom}")) + "|" +
                          string.Join(";", kids.Select(h => { GetWindowRect(h, out var r); return $"{h},{r.Left},{r.Top},{r.Right},{r.Bottom}"; }));
                if (key == last) return;
                last = key;
                foreach (var h in holed.ToList())
                    if (holes.Count == 0 || !kids.Contains(h)) { if (IsWindow(h)) SetWindowRgn(h, IntPtr.Zero, true); holed.Remove(h); }
                if (holes.Count == 0) return;
                foreach (var h in kids)
                {
                    GetWindowRect(h, out var r);
                    bool hit = holes.Any(o => o.Left < r.Right && o.Right > r.Left && o.Top < r.Bottom && o.Bottom > r.Top);
                    if (!hit) { if (holed.Remove(h)) SetWindowRgn(h, IntPtr.Zero, true); continue; }
                    // 영역은 자식 창 왼쪽 위 기준
                    var rgn = CreateRectRgn(0, 0, r.Right - r.Left, r.Bottom - r.Top);
                    foreach (var o in holes)
                    {
                        var cut = CreateRectRgn(o.Left - r.Left, o.Top - r.Top, o.Right - r.Left, o.Bottom - r.Top);
                        CombineRgn(rgn, rgn, cut, RGN_DIFF);
                        DeleteObject(cut);
                    }
                    if (SetWindowRgn(h, rgn, true) != 0) holed.Add(h); else DeleteObject(rgn);   // 성공하면 영역은 시스템 것
                }
            }
            layer.Children.CollectionChanged += (_, __) => Queue();
            // 팝업 크기·자리는 열린 뒤 레이아웃에서 정해진다. 팝업이 없고 구멍도 없으면 아무것도 하지 않는다
            layer.LayoutUpdated += (_, __) => { if (layer.Children.Count > 0 || holed.Count > 0) Queue(); };
        }

        const int GW_CHILD = 5, GW_HWNDNEXT = 2, RGN_DIFF = 4;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }
}
