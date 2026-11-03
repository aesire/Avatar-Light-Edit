// 떠 있는 작업 창 (2026-10-21, 사용자 요청): 페이지 안 탭(SubTabs)을 떼어 에디터 위에 띄우고 자유롭게 옮기고, 핀으로 맨 위에 고정한다.
// 가져온 파일 관리 창도 같은 창을 쓴다.
//
// 미리보기는 Unity 자식 창(UnityView, 네이티브 HWND)이라 Avalonia가 그 위에 그릴 수 없다 → 떠 있는 창은 테두리 없는 "소유된" 창이다.
// 윈도우 창 관리에는 맡기지 않는다: 제목 표시줄·끌기·크기 바꾸기를 직접 처리하고(Aero 스냅·최대화 없음), 작업 표시줄에 없고,
// 늘 에디터 창 안쪽에만 있게 자리를 맞춘다 (에디터를 옮기면 같이 옮겨지고, 줄이면 안으로 밀린다). 에디터를 최소화하면 같이 숨는다.
// 핀 = 맨 위 고정: 다른 떠 있는 창보다 늘 위, 그리고 다른 페이지로 가도 계속 보인다. 핀이 없으면 자기 페이지에서만 보인다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed class FloatPanel : Window
    {
        public readonly string Key;
        /// <summary>핀이 없으면 이 페이지에서만 보인다 (null = 어느 페이지에서나)</summary>
        public readonly string? Page;
        public bool Pinned { get; private set; }
        /// <summary>다시 붙이기·닫기 (내용을 돌려받을 쪽이 처리한다)</summary>
        public event Action? DockRequested;
        public event Action? PinChanged;
        readonly Button _pin;
        readonly TextBlock _title;
        readonly FloatHost _host;

        public FloatPanel(FloatHost host, string key, string? page, string title, Control content, bool canDock)
        {
            _host = host; Key = key; Page = page;
            SystemDecorations = SystemDecorations.None;
            ShowInTaskbar = false; CanResize = false; ShowActivated = !FloatHost.Quiet;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 280; MinHeight = 180;
            FontFamily = host.Owner.FontFamily; FontSize = host.Owner.FontSize;
            Title = title;
            Ui.AddModeStyles(this);
            Classes.Set("easy", host.Owner.Classes.Contains("easy"));

            // ---- 제목 줄: 끌어서 옮기기 · 핀 · 다시 붙이기 · 닫기 ----
            var bar = new DockPanel { Height = 36, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _pin = Ui.IconButton(Glyph.Pin, () => SetPinned(!Pinned), "");
            buttons.Children.Add(_pin);
            if (canDock) buttons.Children.Add(Ui.IconButton(Glyph.DockBack, () => DockRequested?.Invoke(), "작업 창에 다시 붙이기"));
            buttons.Children.Add(Ui.IconButton(Glyph.Close, () => DockRequested?.Invoke(), canDock ? "닫기 (작업 창에 다시 붙어요)" : "닫기"));
            foreach (var b in buttons.Children) b.Cursor = new Cursor(StandardCursorType.Arrow);
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            var grip = Ui.Icon(Glyph.Move, 11, Ui.KText3); grip.Margin = new Thickness(12, 0, 8, 0);
            DockPanel.SetDock(grip, Dock.Left);
            bar.Children.Add(grip);
            _title = Ui.Text(title, 13, FontWeight.SemiBold);
            bar.Children.Add(_title);
            DragToMove(bar);
            bar.DoubleTapped += (_, e) => { if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return; SetPinned(!Pinned); };

            var head = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }.Stroke(Ui.KDivider);
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            // 오른쪽 아래 모서리: 끌어서 크기 바꾸기
            var resize = new Border { Width = 18, Height = 18, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.BottomRightCorner), Child = Ui.Text("◢", 10, FontWeight.Normal, Ui.KText3) };
            ((TextBlock)resize.Child).HorizontalAlignment = HorizontalAlignment.Right;
            DragToResize(resize);
            var body = new Grid();
            _slot.Content = content;
            body.Children.Add(_slot);
            body.Children.Add(resize);
            root.Children.Add(body);
            // 바탕은 내용 Border에 (창 바탕만 칠하면 내용만 찍을 때 투명이 된다)
            Content = new Border { Child = root, BorderThickness = new Thickness(1) }.Stroke(Ui.KCardStroke).Bg(Ui.KPanel);
            this.Bg(Ui.KPanel);
            RefreshPin();

            // 떠 있는 창에서 누른 단축키(Ctrl+Z 등)도 에디터처럼
            AddHandler(KeyDownEvent, (_, e) => { if (host.HandleKey(this, e)) e.Handled = true; }, RoutingStrategies.Tunnel);
            Activated += (_, __) => host.OnFloatActivated(this);
        }

        public void SetTitle(string t) { Title = t; _title.Text = t; }

        /// <summary>닫기 단추와 같다 (VR 창 아래 막대의 닫기).</summary>
        public void RequestDock() => DockRequested?.Invoke();

        /// <summary>앞으로 (화면 확인 모드에서는 사용자 데스크톱의 포커스를 빼앗지 않게 하지 않는다)</summary>
        public void Front() { if (FloatHost.Quiet) return; if (Embed.IsAttached(this)) { Embed.ToTop(this); _host.Restack(); } else Activate(); }

        readonly ContentControl _slot = new ContentControl();
        /// <summary>내용을 떼어 낸다 (작업 창에 다시 붙이기 전에 — 컨트롤은 부모가 하나뿐이다).</summary>
        public void Release() => _slot.Content = null;
        /// <summary>내용을 바꾼다 (다시 그린 묶음 등)</summary>
        public void SetContent(Control c) => _slot.Content = c;

        public void SetPinned(bool on)
        {
            if (Pinned == on) return;
            Pinned = on;
            RefreshPin();
            PinChanged?.Invoke();
            _host.Restack();
        }

        void RefreshPin()
        {
            if (_pin.Content is TextBlock t) { t.Text = Pinned ? Glyph.Pin : Glyph.Unpin; t.Fg(Pinned ? Ui.KAccentText : Ui.KText2); }
            ToolTip.SetTip(_pin, Pinned ? "고정 풀기 (자기 페이지에서만 보여요)" : "맨 위에 고정: 다른 떠 있는 창보다 위에, 다른 페이지에서도 보여요 (제목 두 번 눌러도 돼요)");
        }

        // ---------------- 옮기기·크기 (윈도우의 끌기 대신 직접: 에디터 안쪽에만) ----------------
        void DragToMove(Control handle)
        {
            bool down = false; PixelPoint startPos = default; PixelPoint startMouse = default;
            handle.PointerPressed += (_, e) =>
            {
                if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (VrPanelFeed.Injecting) return;   // VR 레이저: 데스크톱 자리를 옮기면 VR 안 좌표가 밀려 계속 미끄러진다 (VR 안 자리는 따로)
                down = true; startPos = Position; startMouse = this.PointToScreen(e.GetPosition(this));
                e.Pointer.Capture(handle); e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (!down) return;
                var cur = this.PointToScreen(e.GetPosition(this));
                Embed.MoveTo(this, _host.Clamp(this, new PixelPoint(startPos.X + cur.X - startMouse.X, startPos.Y + cur.Y - startMouse.Y)));
            };
            handle.PointerReleased += (_, e) => { if (!down) return; down = false; e.Pointer.Capture(null); _host.Remember(this); };
            handle.PointerCaptureLost += (_, __) => down = false;
        }

        void DragToResize(Control handle)
        {
            bool down = false; Size startSize = default; PixelPoint startMouse = default;
            handle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (VrPanelFeed.Injecting) return;
                down = true; startSize = new Size(Width, Height); startMouse = this.PointToScreen(e.GetPosition(this));
                e.Pointer.Capture(handle); e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (!down) return;
                var cur = this.PointToScreen(e.GetPosition(this));
                var s = RenderScaling;
                var max = _host.MaxSize(this);
                Width = Math.Clamp(startSize.Width + (cur.X - startMouse.X) / s, MinWidth, Math.Max(MinWidth, max.Width));
                Height = Math.Clamp(startSize.Height + (cur.Y - startMouse.Y) / s, MinHeight, Math.Max(MinHeight, max.Height));
            };
            handle.PointerReleased += (_, e) => { if (!down) return; down = false; e.Pointer.Capture(null); _host.Remember(this); };
            handle.PointerCaptureLost += (_, __) => down = false;
        }

        internal IntPtr Hwnd => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    /// <summary>
    /// 떠 있는 창들을 에디터 창에 묶는다: 에디터 안쪽에 두기, 에디터와 같이 옮기기, 페이지에 따라 보이고 숨기기, 핀 창을 위로 쌓기.
    /// 자리·크기는 이 실행 동안 키마다 기억한다 (다시 띄우면 그 자리).
    /// </summary>
    public sealed class FloatHost
    {
        public readonly Window Owner;
        /// <summary>--ui-shots: 떠 있는 창도 활성화하지 않는다 (사용자 데스크톱 포커스를 빼앗지 않게)</summary>
        public static bool Quiet;
        readonly List<FloatPanel> _floats = new List<FloatPanel>();
        public IEnumerable<Window> Windows => _floats;
        readonly Dictionary<string, (PixelPoint Offset, Size Size)> _places = new Dictionary<string, (PixelPoint, Size)>();
        readonly Func<FloatPanel, KeyEventArgs, bool> _keys;
        PixelPoint _ownerPos;
        string? _page;

        public FloatHost(Window owner, Func<FloatPanel, KeyEventArgs, bool> keys)
        {
            Owner = owner; _keys = keys;
            _ownerPos = owner.Position;
            owner.PositionChanged += (_, e) =>
            {
                var d = new PixelPoint(e.Point.X - _ownerPos.X, e.Point.Y - _ownerPos.Y);
                _ownerPos = e.Point;
                foreach (var f in _floats) if (!Embed.IsAttached(f)) f.Position = new PixelPoint(f.Position.X + d.X, f.Position.Y + d.Y);   // 자식 창은 저절로 따라온다
            };
            owner.SizeChanged += (_, __) => Dispatcher.UIThread.Post(() => { foreach (var f in _floats) { FitInside(f); } });
            owner.Closing += (_, __) => { foreach (var f in _floats.ToList()) f.Close(); };
            owner.Classes.CollectionChanged += (_, __) => { foreach (var f in _floats) f.Classes.Set("easy", owner.Classes.Contains("easy")); };
            Embed.Pressed += w => { if (w is FloatPanel f && _floats.Contains(f)) OnFloatActivated(f); };   // 누른 창이 앞으로 온 뒤 핀 창을 다시 위로
        }

        public IReadOnlyList<FloatPanel> Floats => _floats;
        public FloatPanel? Find(string key) => _floats.FirstOrDefault(f => f.Key == key);

        /// <summary>띄운다. 이미 떠 있으면 앞으로. size는 처음 띄울 때 크기(DIP).</summary>
        public FloatPanel Show(string key, string? page, string title, Control content, bool canDock, Size size)
        {
            var f = Find(key);
            if (f != null) { f.Front(); return f; }
            f = new FloatPanel(this, key, page, title, content, canDock);
            var s = Owner.RenderScaling;
            var origin = Owner.PointToScreen(new Point(0, 0));
            if (_places.TryGetValue(key, out var place)) { f.Width = place.Size.Width; f.Height = place.Size.Height; f.Position = new PixelPoint(origin.X + place.Offset.X, origin.Y + place.Offset.Y); }
            else
            {
                f.Width = size.Width; f.Height = size.Height;
                int n = _floats.Count;
                f.Position = new PixelPoint(origin.X + (int)((Owner.ClientSize.Width - size.Width) / 2 * s) + n * (int)(28 * s), origin.Y + (int)(80 * s) + n * (int)(28 * s));
            }
            // 같은 위치에 28 DIP씩만 밀면 두 번째 비교/슬라이드 창이 앞 창을 덮는다.
            // 기억해 둔 자리도 예전 버전에서 겹쳐 저장됐을 수 있으므로, 새 창을 띄울 때
            // 현재 보이는 창들과 겹치지 않는 후보를 먼저 찾는다.
            if (OverlapsVisible(f)) f.Position = FindFreePlace(f, origin, s);
            _floats.Add(f);
            f.Closed += (_, __) => _floats.Remove(f);
            f.Show(Owner);
            Embed.Attach(f, Owner);   // 에디터 창 안으로 (창 공유에 함께 찍히게)
            FitInside(f, avoidOverlap: true);
            Restack();
            return f;
        }

        public void Close(FloatPanel f) { Remember(f); f.Close(); }

        /// <summary>자리를 에디터 왼쪽 위 기준으로 기억한다.</summary>
        public void Remember(FloatPanel f)
        {
            var origin = Owner.PointToScreen(new Point(0, 0));
            _places[f.Key] = (new PixelPoint(f.Position.X - origin.X, f.Position.Y - origin.Y), new Size(f.Width, f.Height));
        }

        /// <summary>에디터 창의 안쪽(제목 표시줄 아래)에 들어오게 자리를 자른다.</summary>
        public PixelPoint Clamp(FloatPanel f, PixelPoint p)
        {
            var s = Owner.RenderScaling;
            var tl = Owner.PointToScreen(new Point(0, 40));   // 제목 표시줄(창 단추)은 가리지 않는다
            var br = Owner.PointToScreen(new Point(Owner.ClientSize.Width, Owner.ClientSize.Height));
            int w = (int)(f.Bounds.Width > 0 ? f.Bounds.Width * s : f.Width * s), h = (int)(f.Bounds.Height > 0 ? f.Bounds.Height * s : f.Height * s);
            int x = Math.Clamp(p.X, tl.X, Math.Max(tl.X, br.X - w));
            int y = Math.Clamp(p.Y, tl.Y, Math.Max(tl.Y, br.Y - h));
            return new PixelPoint(x, y);
        }

        /// <summary>지금 자리에서 오른쪽·아래로 늘릴 수 있는 크기 (DIP)</summary>
        public Size MaxSize(FloatPanel f)
        {
            var s = Owner.RenderScaling;
            var br = Owner.PointToScreen(new Point(Owner.ClientSize.Width, Owner.ClientSize.Height));
            return new Size((br.X - f.Position.X) / s, (br.Y - f.Position.Y) / s);
        }

        /// avoidOverlap: 처음 띄울 때만. 에디터 크기를 바꾸거나 페이지를 오갈 때는 사용자가 일부러 겹쳐 둔 자리를 옮기지 않는다
        void FitInside(FloatPanel f, bool avoidOverlap = false)
        {
            var s = Owner.RenderScaling;
            double maxW = Owner.ClientSize.Width - 16, maxH = Owner.ClientSize.Height - 56;
            if (f.Width > maxW) f.Width = Math.Max(f.MinWidth, maxW);
            if (f.Height > maxH) f.Height = Math.Max(f.MinHeight, maxH);
            var place = Clamp(f, f.Position);
            if (avoidOverlap && OverlapsVisible(f))
            {
                var origin = Owner.PointToScreen(new Point(0, 0));
                place = FindFreePlace(f, origin, s);
            }
            Embed.MoveTo(f, place);
        }

        bool OverlapsVisible(FloatPanel f)
        {
            var r = Rect(f.Position, f.Width, f.Height, Owner.RenderScaling);
            return _floats.Any(o => o != f && o.IsVisible && Intersects(r, Rect(o.Position, o.Width, o.Height, Owner.RenderScaling), 10));
        }

        PixelPoint FindFreePlace(FloatPanel f, PixelPoint origin, double s)
        {
            int margin = (int)Math.Round(16 * s), gap = (int)Math.Round(12 * s);
            int ownerW = (int)Math.Round(Owner.ClientSize.Width * s), ownerH = (int)Math.Round(Owner.ClientSize.Height * s);
            int w = Math.Max(1, (int)Math.Round(f.Width * s)), h = Math.Max(1, (int)Math.Round(f.Height * s));
            int maxX = Math.Max(margin, ownerW - w - margin), maxY = Math.Max((int)Math.Round(56 * s), ownerH - h - margin);
            int centerX = Math.Clamp((ownerW - w) / 2, margin, maxX), topY = Math.Clamp((int)Math.Round(80 * s), margin, maxY);
            var candidates = new List<PixelPoint>
            {
                new PixelPoint(centerX, topY),
                // 같은 높이에서 가운데 창의 바로 왼쪽/오른쪽을 먼저 시도해
                // 오른쪽 도구 레일 끝까지 밀려나지 않게 한다.
                new PixelPoint(Math.Clamp(centerX - w - gap, margin, maxX), topY),
                new PixelPoint(Math.Clamp(centerX + w + gap, margin, maxX), topY),
                new PixelPoint(maxX, topY),
                new PixelPoint(margin, topY),
                new PixelPoint(maxX, maxY),
                new PixelPoint(margin, maxY),
                new PixelPoint(centerX, maxY),
            };
            // 창이 많은 경우에도 일정한 간격으로 빈 칸을 찾는다.
            int firstY = Math.Max(margin, (int)Math.Round(56 * s));
            for (int y = firstY; y <= maxY; y += Math.Max(1, (int)Math.Round(36 * s)))
                for (int x = margin; x <= maxX; x += Math.Max(1, (int)Math.Round(36 * s)))
                    candidates.Add(new PixelPoint(x, y));
            foreach (var c in candidates)
            {
                var p = new PixelPoint(origin.X + c.X, origin.Y + c.Y);
                var r = Rect(p, f.Width, f.Height, s);
                if (!_floats.Any(o => o != f && o.IsVisible && Intersects(r, Rect(o.Position, o.Width, o.Height, s), gap))) return p;
            }
            // 공간이 정말 부족하면 최소 겹침 후보를 선택해 창 전체가 보이게 한다.
            var best = candidates[0]; int score = int.MaxValue;
            foreach (var c in candidates)
            {
                var r = Rect(new PixelPoint(origin.X + c.X, origin.Y + c.Y), f.Width, f.Height, s);
                int n = _floats.Where(o => o != f && o.IsVisible).Sum(o => Area(Intersection(r, Rect(o.Position, o.Width, o.Height, s))));
                if (n < score) { score = n; best = c; }
            }
            return new PixelPoint(origin.X + best.X, origin.Y + best.Y);
        }

        static (int X, int Y, int W, int H) Rect(PixelPoint p, double wDip, double hDip, double s) =>
            (p.X, p.Y, Math.Max(1, (int)Math.Round(wDip * s)), Math.Max(1, (int)Math.Round(hDip * s)));
        static bool Intersects((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b, int gap) =>
            a.X < b.X + b.W + gap && a.X + a.W + gap > b.X && a.Y < b.Y + b.H + gap && a.Y + a.H + gap > b.Y;
        static (int X, int Y, int W, int H) Intersection((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b)
        {
            int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y), r = Math.Min(a.X + a.W, b.X + b.W), bot = Math.Min(a.Y + a.H, b.Y + b.H);
            return (x, y, Math.Max(0, r - x), Math.Max(0, bot - y));
        }
        static int Area((int X, int Y, int W, int H) r) => r.W * r.H;

        /// <summary>페이지를 바꿨을 때: 핀 없는 창은 자기 페이지에서만 보인다.</summary>
        public void PageChanged(string page)
        {
            _page = page;
            foreach (var f in _floats) ApplyVisibility(f);
            Restack();
        }

        void ApplyVisibility(FloatPanel f)
        {
            bool show = f.Pinned || f.Page == null || f.Page == _page;
            if (show && !f.IsVisible) { f.Show(); Embed.Attach(f, Owner); FitInside(f); }
            else if (!show && f.IsVisible) f.Hide();
        }

        public void OnFloatActivated(FloatPanel f) => Dispatcher.UIThread.Post(Restack);

        /// <summary>핀 창을 다른 떠 있는 창보다 위로 (활성화는 빼앗지 않는다). 에디터 위라는 것은 소유 관계가 지킨다.</summary>
        public void Restack()
        {
            foreach (var f in _floats) ApplyVisibility(f);
            foreach (var f in _floats.Where(x => x.Pinned && x.IsVisible)) Embed.ToTop(f);
        }

        public bool AnyActive => _floats.Any(f => f.IsActive);

        public bool HandleKey(FloatPanel f, KeyEventArgs e) => _keys(f, e);

    }
}
