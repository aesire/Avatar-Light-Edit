// 떠 있는 다이얼 (2026-10-22, 사용자 요청): 세밀 조정 값 하나를 떼어 에디터 위에 다이얼 블록으로 띄운다. 여러 개 띄울 수 있다.
//   블록     머리(이름 · 막대 켜고 끄기 · 닫기, 머리를 잡고 끌어 옮김) + 다이얼 + 값. 막대를 끄면 정사각형, 켜면 세로 막대가 붙어 길쭉하다.
//   자석     끄는 창이 다른 다이얼 창 가장자리 가까이(14px) 가면 착 붙고, 그대로 놓으면 한 줄로 합쳐진다 (왼쪽에 놓으면 앞에, 오른쪽이면 뒤에).
//            합쳐진 줄에서 블록 머리를 잡고 끌면 그 블록만 떼어진다 (블록처럼 붙이고 떼기).
//   묶음 바  블록이 둘 이상이면 위에 바: 묶음 이름 · 켜고 끄기(끄면 잠가서 실수로 바뀌지 않게) · 맨 위 고정 · 프리셋 저장 · 모두 닫기. 바를 잡고 끌면 묶음째로.
//   프리셋   저장되는 값(쉐이프키·머슬 — Ui.FineTarget.Kind/Key)만 avhost.json `dialPresets`에. 세밀 조정 막대 머리의 단추로 다시 연다.
// 창은 에디터에 딸린 테두리 없는 창 (Unity 미리보기 위에도 뜬다), 에디터 안쪽에서만 움직인다. 끌기는 커서를 직접 읽는다 (블록을 다른 창으로 옮겨도 끌기가 끊기지 않게).
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
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class DialBlock : Border
    {
        public const double Size = 112, BarHeight = 176;
        public readonly Ui.FineTarget T;
        public bool BarOn { get; private set; }
        public DialWindow? Host;
        readonly Dial _dial = new Dial { Width = 70, Height = 70, HorizontalAlignment = HorizontalAlignment.Center };
        readonly FineBar _bar = new FineBar { Height = BarHeight - 10, Margin = new Thickness(18, 4, 18, 2), Enabled = true };
        readonly TextBlock _name, _val = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        readonly Button _barBtn;
        readonly DispatcherTimer _commit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        bool _drag;
        double _start;
        double? _pending;

        public DialBlock(Ui.FineTarget t, bool bar)
        {
            T = t;
            _name = new TextBlock { Text = t.Name, FontSize = 10.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            _name.Fg(Ui.KText); _val.Fg(Ui.KText3);
            ToolTip.SetTip(_name, t.Name);
            var close = Ui.IconButton(Glyph.Close, () => DialBoard.CloseBlock(this), "닫기");
            _barBtn = Ui.IconButton("", () => SetBar(!BarOn), "막대 켜기·끄기");
            foreach (var b in new[] { close, _barBtn }) { b.Width = 20; b.Height = 20; }
            var head = new DockPanel { Height = 22, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
            DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(_barBtn, Dock.Right);
            head.Children.Add(close); head.Children.Add(_barBtn); head.Children.Add(_name);
            head.PointerPressed += (_, e) => { if (e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) { DialBoard.BeginBlockDrag(this); e.Handled = true; } };

            Child = new StackPanel { Children = { head, _dial, _bar, _val } };
            Width = Size; CornerRadius = new CornerRadius(6); BorderThickness = new Thickness(1); Padding = new Thickness(3, 1, 3, 4); Margin = new Thickness(2);
            this.Bg(Ui.KCard).Stroke(Ui.KCardStroke);

            _dial.DragStarted += () => { Flush(); _drag = true; _start = T.Get(); };
            _dial.Changing += v => { if (!_drag && _pending == null) _start = T.Get(); Show(v); T.Preview(v); if (!_drag) _pending = v; };
            _dial.Committed += v => { _drag = false; _pending = null; if (Math.Abs(v - _start) > 1e-12) T.Commit(Snap(v)); Range(); };
            _bar.Pressed += () => { Flush(); _drag = true; _start = T.Get(); };
            _bar.Dragged += v => { v = Snap(Math.Clamp(v, T.Min, T.Max)); Show(v); T.Preview(v); };
            _bar.Released += () => { _drag = false; var v = _bar.Value; if (Math.Abs(v - _start) > 1e-12) T.Commit(v); Range(); };
            _bar.Wheel += d =>
            {
                if (_pending == null) _start = T.Get();
                var v = Snap(Math.Clamp((_pending ?? T.Get()) + d * T.Step, T.Min, T.Max));
                _pending = v; Show(v); T.Preview(v);
                _commit.Stop(); _commit.Start();
            };
            _commit.Tick += (_, __) => Flush();
            SetBar(bar);
            Range();
        }

        double Snap(double v) => T.IsInt ? Math.Round(v) : Math.Round(v, Math.Clamp((int)Math.Ceiling(-Math.Log10(T.Step)) + 2, 0, 6));

        void Flush()
        {
            _commit.Stop();
            if (_pending is double p && Math.Abs(p - _start) > 1e-12) T.Commit(p);
            _pending = null;
        }

        /// <summary>다이얼·막대 범위: 범위 있는 값은 전체, 없는 값은 지금 값 ± 50칸 (막대는 돌리는 모양)</summary>
        void Range()
        {
            double v = SafeGet();
            if (T.Bounded) { _dial.Minimum = T.Min; _dial.Maximum = T.Max; _bar.Lo = T.Min; _bar.Hi = T.Max; _bar.Jog = false; }
            else { _dial.Minimum = v - 50 * T.Step; _dial.Maximum = v + 50 * T.Step; _bar.Jog = true; _bar.PerPixel = T.Step; }
            Show(v);
        }

        double SafeGet() { try { return T.Get(); } catch { return 0; } }

        void Show(double v)
        {
            _dial.Value = v; _bar.Value = v; _bar.InvalidateVisual();
            _val.Text = Ui.F(Math.Round(v, T.IsInt ? 0 : 3));
        }

        public void SetBar(bool on)
        {
            BarOn = on;
            _bar.IsVisible = on;
            Height = on ? Size + BarHeight : Size;
            _barBtn.Opacity = on ? 1 : 0.55;
            Host?.Relayout();
        }

        public void Refresh()
        {
            if (_drag || _pending != null) return;
            var v = SafeGet();
            if (Math.Abs(v - _dial.Value) > 1e-9) { if (!T.Bounded) Range(); else Show(v); }
        }

        public void SetLocked(bool locked) { IsEnabled = !locked; _dial.IsEnabled = !locked; _bar.Enabled = !locked; Opacity = locked ? 0.45 : 1; _bar.InvalidateVisual(); }
    }

    /// <summary>
    /// 다이얼 블록 묶음을 담는 테두리 없는 창. 블록은 여러 줄로 놓인다 (좌우로 붙이면 그 줄에, 위아래로 붙이면 새 줄).
    /// 블록이 둘 이상이면 위에 묶음 바. 바의 빈 곳 어디를 잡아도 묶음째로 끈다 (이름은 두 번 눌러 고친다).
    /// </summary>
    public sealed class DialWindow : Window
    {
        readonly List<List<DialBlock>> _rows = new List<List<DialBlock>>();
        readonly StackPanel _rowsPanel = new StackPanel();
        readonly DockPanel _top;
        readonly TextBlock _nameText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0) };
        readonly TextBox _nameBox = new TextBox { FontSize = 12, MinWidth = 90, MinHeight = 0, Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        readonly ToggleSwitch _power;
        readonly Button _pinBtn;
        public IEnumerable<DialBlock> Blocks => _rows.SelectMany(r => r);
        public int Count => _rows.Sum(r => r.Count);
        public IReadOnlyList<IReadOnlyList<DialBlock>> Rows => _rows;
        public string GroupName { get => _nameText.Text ?? ""; set { _nameText.Text = value; _nameBox.Text = value; } }
        public bool Pinned { get; private set; }
        public bool Locked => _power.IsChecked != true;

        public DialWindow()
        {
            SystemDecorations = SystemDecorations.None; ShowInTaskbar = false; CanResize = false; ShowActivated = !FloatHost.Quiet;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "다이얼";
            this.Bg(Ui.KBase);
            GroupName = "다이얼 묶음";
            _nameText.Fg(Ui.KText);
            _power = new ToggleSwitch { IsChecked = true, OnContent = "", OffContent = "", MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(_power, "켜기·끄기 (끄면 잠가요)");
            _power.IsCheckedChanged += (_, __) => { foreach (var b in Blocks) b.SetLocked(Locked); };
            _pinBtn = Ui.IconButton(Glyph.Pin, () => SetPinned(!Pinned), "맨 위에 고정");
            var save = Ui.IconButton(Glyph.Save, () => DialBoard.SavePreset(this), "프리셋으로 저장");
            var close = Ui.IconButton(Glyph.Close, () => DialBoard.CloseWindow(this), "모두 닫기");
            var grip = Ui.Icon(Glyph.Move, 11, Ui.KText3);
            grip.Margin = new Thickness(6, 0, 2, 0);
            _top = new DockPanel { Height = 30, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll), LastChildFill = true };
            foreach (var c in new Control[] { close, save, _pinBtn, _power }) { DockPanel.SetDock(c, Dock.Right); _top.Children.Add(c); }
            _power.Margin = new Thickness(4, 0, 2, 0);
            DockPanel.SetDock(grip, Dock.Left); DockPanel.SetDock(_nameText, Dock.Left); DockPanel.SetDock(_nameBox, Dock.Left);
            _top.Children.Add(grip); _top.Children.Add(_nameText); _top.Children.Add(_nameBox);
            _top.Children.Add(new Border { Background = Brushes.Transparent });   // 남은 빈 곳 (잡고 끌기)
            // 이름: 두 번 누르면 고치기 (Enter·다른 곳 누르면 끝). 한 번 누르고 끌면 묶음째로 옮긴다
            _nameText.DoubleTapped += (_, __) => { _nameBox.Text = GroupName; _nameText.IsVisible = false; _nameBox.IsVisible = true; _nameBox.Focus(); _nameBox.SelectAll(); };
            void EndEdit() { if (!_nameBox.IsVisible) return; GroupName = (_nameBox.Text ?? "").Trim().Length > 0 ? _nameBox.Text!.Trim() : GroupName; _nameBox.IsVisible = false; _nameText.IsVisible = true; }
            _nameBox.LostFocus += (_, __) => EndEdit();
            _nameBox.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { EndEdit(); e.Handled = true; } };
            _top.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(_top).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
                // 단추·스위치·이름 고치는 칸은 그대로 (나머지는 끌기)
                for (var v = e.Source as Visual; v != null && v != _top; v = v.GetVisualParent())
                    if (v is Button or ToggleSwitch or TextBox) return;
                DialBoard.BeginDrag(this);
            }, RoutingStrategies.Tunnel);
            var root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = new StackPanel { Children = { _top, _rowsPanel } } };
            root.Bg(Ui.KPanel).Stroke(Ui.KCardStroke);
            Content = root;
            Relayout();
        }

        public void SetPinned(bool on)
        {
            Pinned = on;
            // 에디터 안에 넣은 창(자식 창)은 Topmost 대신 형제 중 맨 위로 — 다른 창을 누를 때마다 DialBoard가 다시 올린다
            if (Embed.IsAttached(this)) { if (on) Embed.ToTop(this); } else Topmost = on;
            _pinBtn.Content = Ui.Icon(on ? Glyph.Unpin : Glyph.Pin, 12, on ? Ui.KAccentText : Ui.KText2);
            ToolTip.SetTip(_pinBtn, on ? "고정 풀기" : "맨 위에 고정");
        }

        /// <summary>row 줄의 index 자리에 (row가 줄 수와 같으면 새 줄을 아래에, -1이면 새 줄을 맨 위에)</summary>
        public void Insert(int row, int index, DialBlock b)
        {
            (b.Parent as Panel)?.Children.Remove(b);
            b.Host?.Detach(b);
            if (row < 0) { _rows.Insert(0, new List<DialBlock>()); row = 0; }
            while (row >= _rows.Count) _rows.Add(new List<DialBlock>());
            var r = _rows[row];
            r.Insert(Math.Clamp(index, 0, r.Count), b);
            b.Host = this;
            b.SetLocked(Locked);
            Relayout();
        }

        /// <summary>맨 아래 줄 끝에</summary>
        public void Add(DialBlock b) => Insert(Math.Max(0, _rows.Count - 1), int.MaxValue, b);

        public void Remove(DialBlock b) { Detach(b); }

        internal void Detach(DialBlock b)
        {
            foreach (var r in _rows) r.Remove(b);
            _rows.RemoveAll(r => r.Count == 0);
            (b.Parent as Panel)?.Children.Remove(b);
            if (b.Host == this) b.Host = null;
            Relayout();
        }

        public (int Row, int Index) Find(DialBlock b)
        {
            for (int i = 0; i < _rows.Count; i++) { int j = _rows[i].IndexOf(b); if (j >= 0) return (i, j); }
            return (-1, -1);
        }

        public void Relayout()
        {
            _top.IsVisible = Count >= 2;
            // 줄 패널을 다시 짠다 (블록은 그대로 옮긴다)
            foreach (var p in _rowsPanel.Children.OfType<StackPanel>()) p.Children.Clear();
            _rowsPanel.Children.Clear();
            foreach (var r in _rows)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var b in r) { b.VerticalAlignment = VerticalAlignment.Top; p.Children.Add(b); }
                _rowsPanel.Children.Add(p);
            }
        }

        /// <summary>화면 좌표 y가 어느 줄 높이에 있는지 (좌우로 붙일 때)</summary>
        public int RowAt(int screenY)
        {
            for (int i = 0; i < _rowsPanel.Children.Count; i++)
            {
                var p = _rowsPanel.Children[i];
                var top = p.PointToScreen(new Point(0, 0)).Y;
                var bottom = p.PointToScreen(new Point(0, p.Bounds.Height)).Y;
                if (screenY < bottom || i == _rowsPanel.Children.Count - 1) return i;
            }
            return 0;
        }

        /// <summary>화면 픽셀 사각형</summary>
        public PixelRect ScreenRect
        {
            get
            {
                var s = DesktopScaling;
                return new PixelRect(Position, new PixelSize((int)Math.Round(Bounds.Width * s), (int)Math.Round(Bounds.Height * s)));
            }
        }
    }

    public static class DialBoard
    {
        static Window? _owner;
        static Settings? _settings;
        static readonly List<DialWindow> _wins = new List<DialWindow>();
        static readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        static readonly DispatcherTimer _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
        static DialWindow? _dragWin;
        static PixelPoint _grab, _pressAt;
        static bool _moved;
        public enum Side { Left, Right, Top, Bottom }
        static (DialWindow Win, Side Side)? _snap;
        public static Action<string>? Status;
        public static IReadOnlyList<DialWindow> Windows => _wins;

        public static void Init(Window owner, Settings settings)
        {
            _owner = owner; _settings = settings;
            _poll.Tick += (_, __) => { foreach (var w in _wins) foreach (var b in w.Blocks) b.Refresh(); };
            _dragTimer.Tick += (_, __) => DragTick();
            // 에디터 안에 넣은 창: 다른 창을 누르면 그 창이 앞으로 오므로 고정한 다이얼 창을 다시 위로
            Embed.Pressed += pressed => { foreach (var w in _wins) if (w != pressed && w.Pinned && Embed.IsAttached(w)) Embed.ToTop(w); };
            // 에디터와 같이 옮기기
            var last = owner.Position;
            owner.PositionChanged += (_, e) =>
            {
                var d = e.Point - last; last = e.Point;
                foreach (var w in _wins) if (!Embed.IsAttached(w)) w.Position += d;   // 자식 창은 저절로 따라온다
            };
        }

        /// <summary>값 하나를 다이얼 블록 창으로 띄운다 (에디터 오른쪽 위 근처, 띄운 것이 있으면 조금씩 비켜서)</summary>
        public static DialWindow? Open(Ui.FineTarget t, bool bar = false, PixelPoint? at = null)
        {
            if (_owner == null) return null;
            var w = NewWindow();
            w.Add(new DialBlock(t, bar));
            var s = _owner.DesktopScaling;
            var o = _owner.PointToScreen(new Point(0, 0));
            w.Position = at ?? new PixelPoint(o.X + (int)((_owner.ClientSize.Width - 360) * s) - _wins.Count * (int)(24 * s), o.Y + (int)(90 * s) + _wins.Count * (int)(24 * s));
            w.Show(_owner);
            Embed.Attach(w, _owner);   // 에디터 창 안으로 (창 공유에 함께 찍히게)
            _poll.Start();
            return w;
        }

        static DialWindow NewWindow()
        {
            var w = new DialWindow();
            w.Closed += (_, __) => { _wins.Remove(w); if (_wins.Count == 0) _poll.Stop(); };
            _wins.Add(w);
            return w;
        }

        public static void CloseBlock(DialBlock b)
        {
            var w = b.Host;
            if (w == null) return;
            w.Remove(b);
            if (w.Count == 0) w.Close();
        }

        public static void CloseWindow(DialWindow w) => w.Close();

        // ---------------- 끌기 · 자석 · 합치기 ----------------
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        static PixelPoint Cursor() { GetCursorPos(out var p); return new PixelPoint(p.X, p.Y); }
        static bool LeftDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

        public static void BeginDrag(DialWindow w)
        {
            _dragWin = w; _pressAt = Cursor(); _grab = _pressAt - w.Position; _snap = null; _moved = false;
            _dragTimer.Start();
        }

        /// <summary>블록 머리를 잡았을 때: 혼자면 창째로, 묶음 안에 있으면 그 블록만 떼어 새 창으로 끈다</summary>
        public static void BeginBlockDrag(DialBlock b)
        {
            var w = b.Host;
            if (w == null) return;
            if (w.Count == 1) { BeginDrag(w); return; }
            var at = b.PointToScreen(new Point(0, 0));
            var anchor = w.Blocks.First(x => x != b);
            var anchorAt = anchor.PointToScreen(new Point(0, 0));
            w.Remove(b);
            Keep(w, anchor, anchorAt);
            var nw = NewWindow();
            nw.Add(b);
            nw.Position = at - new PixelPoint((int)(4 * w.DesktopScaling), (int)(4 * w.DesktopScaling));
            nw.Show(_owner!);
            Embed.Attach(nw, _owner!);
            BeginDrag(nw);
        }

        static void DragTick()
        {
            var w = _dragWin;
            if (w == null) { _dragTimer.Stop(); return; }
            if (!LeftDown()) { EndDrag(); return; }
            var cur = Cursor();
            if (!_moved && Math.Abs(cur.X - _pressAt.X) + Math.Abs(cur.Y - _pressAt.Y) < 3) return;
            _moved = true;
            var p = cur - _grab;
            var me = new PixelRect(p, w.ScreenRect.Size);
            int mag = (int)(16 * w.DesktopScaling);
            _snap = null;
            foreach (var o in _wins)
            {
                if (o == w || !o.IsVisible) continue;
                var r = o.ScreenRect;
                bool vOverlap = me.Y < r.Bottom && me.Bottom > r.Y;
                bool hOverlap = me.X < r.Right && me.Right > r.X;
                if (vOverlap && Math.Abs(me.X - r.Right) < mag) { p = new PixelPoint(r.Right, Math.Abs(me.Y - r.Y) < mag ? r.Y : p.Y); _snap = (o, Side.Right); break; }
                if (vOverlap && Math.Abs(me.Right - r.X) < mag) { p = new PixelPoint(r.X - me.Width, Math.Abs(me.Y - r.Y) < mag ? r.Y : p.Y); _snap = (o, Side.Left); break; }
                if (hOverlap && Math.Abs(me.Y - r.Bottom) < mag) { p = new PixelPoint(Math.Abs(me.X - r.X) < mag ? r.X : p.X, r.Bottom); _snap = (o, Side.Bottom); break; }
                if (hOverlap && Math.Abs(me.Bottom - r.Y) < mag) { p = new PixelPoint(Math.Abs(me.X - r.X) < mag ? r.X : p.X, r.Y - me.Height); _snap = (o, Side.Top); break; }
            }
            Embed.MoveTo(w, Clamp(p, me.Size));
        }

        static PixelPoint Clamp(PixelPoint p, PixelSize size)
        {
            if (_owner == null) return p;
            var s = _owner.DesktopScaling;
            var o = _owner.PointToScreen(new Point(0, 0));
            int maxX = o.X + (int)(_owner.ClientSize.Width * s) - size.Width, maxY = o.Y + (int)(_owner.ClientSize.Height * s) - size.Height;
            return new PixelPoint(Math.Clamp(p.X, o.X, Math.Max(o.X, maxX)), Math.Clamp(p.Y, o.Y, Math.Max(o.Y, maxY)));
        }

        static void EndDrag()
        {
            _dragTimer.Stop();
            var w = _dragWin; _dragWin = null;
            if (w == null || _snap is not { } s) return;
            Merge(w, s.Win, s.Side);
        }

        /// <summary>
        /// 끌던 창의 블록을 붙은 창으로 옮긴다. 좌우: 끌던 창의 줄마다 붙은 창의 같은 높이 줄 앞/뒤에. 위아래: 끌던 창의 줄을 새 줄로 위/아래에.
        /// 붙은 창은 블록이 화면에서 제자리에 있게 위치를 고친다 (앞이나 위에 붙으면 창이 그만큼 늘어나므로).
        /// </summary>
        public static void Merge(DialWindow from, DialWindow into, Side side)
        {
            if (from == into) return;
            var fromPos = from.Position;
            var anchor = into.Blocks.First();
            var anchorAt = anchor.PointToScreen(new Point(0, 0));
            var rows = from.Rows.Select(r => r.ToList()).ToList();
            int startRow = side is Side.Left or Side.Right ? into.RowAt(fromPos.Y + (int)(40 * from.DesktopScaling)) : 0;
            if (side == Side.Top) rows.Reverse();   // 위에 새 줄을 하나씩 만들므로 아래 줄부터
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                switch (side)
                {
                    case Side.Left: for (int j = 0; j < row.Count; j++) into.Insert(startRow + i, j, row[j]); break;
                    case Side.Right: foreach (var b in row) into.Insert(startRow + i, int.MaxValue, b); break;
                    case Side.Bottom: { int target = into.Rows.Count; foreach (var b in row) into.Insert(target, int.MaxValue, b); } break;
                    case Side.Top: into.Insert(-1, 0, row[0]); for (int j = 1; j < row.Count; j++) into.Insert(0, j, row[j]); break;
                }
            }
            if (from.Pinned) into.SetPinned(true);
            from.Close();
            Keep(into, anchor, anchorAt);
        }

        /// <summary>블록을 넣고 빼서 창 모양이 바뀐 뒤에도 기준 블록이 화면에서 제자리에 있게 창을 옮긴다</summary>
        static void Keep(DialWindow w, DialBlock anchor, PixelPoint was)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (anchor.Host != w || !w.IsVisible) return;
                var now = anchor.PointToScreen(new Point(0, 0));
                Embed.MoveTo(w, w.Position + (was - now));
            }, DispatcherPriority.Loaded);
        }

        // ---------------- 프리셋 ----------------
        public static IEnumerable<string> Presets => _settings?.DialPresets.Props.Select(p => p.Key) ?? Enumerable.Empty<string>();

        public static void SavePreset(DialWindow w)
        {
            if (_settings == null) return;
            var all = w.Blocks.ToList();
            int keyed = all.Count(b => b.T.Kind.Length > 0 && b.T.Key.Length > 0);
            if (keyed == 0) { Status?.Invoke("이 다이얼들은 프리셋으로 저장할 수 없어요 (쉐이프키·머슬 값만 저장돼요)."); return; }
            var name = w.GroupName.Trim().Length > 0 ? w.GroupName.Trim() : "다이얼 묶음";
            var rows = JNode.Arr();
            foreach (var r in w.Rows)
            {
                var arr = JNode.Arr();
                foreach (var b in r.Where(b => b.T.Kind.Length > 0 && b.T.Key.Length > 0)) arr.Add(JNode.Obj().Set("kind", b.T.Kind).Set("key", b.T.Key).Set("bar", b.BarOn));
                if (arr.Count > 0) rows.Add(arr);
            }
            _settings.DialPresets.Set(name, JNode.Obj().Set("pinned", w.Pinned).Set("rows", rows));
            _settings.Save();
            Status?.Invoke($"다이얼 프리셋 \"{name}\"을 저장했습니다 ({keyed}개" + (keyed < all.Count ? $", 저장할 수 없는 {all.Count - keyed}개는 빼고" : "") + ").");
        }

        public static DialWindow? OpenPreset(string name)
        {
            if (_settings == null || !_settings.DialPresets.Has(name)) return null;
            var p = _settings.DialPresets[name];
            // 예전 모양(blocks 한 줄)도 읽는다
            var rows = p.Has("rows") ? p["rows"].Items.ToList() : new List<JNode> { p["blocks"] };
            DialWindow? w = null;
            int missing = 0, rowIndex = 0;
            foreach (var row in rows)
            {
                bool any = false;
                foreach (var b in row.Items)
                {
                    var t = Ui.ResolveFine(b["kind"].AsString, b["key"].AsString);
                    if (t == null) { missing++; continue; }
                    if (w == null) { w = Open(t, b["bar"].AsBool); if (w == null) return null; any = true; continue; }
                    w.Insert(rowIndex, int.MaxValue, new DialBlock(t, b["bar"].AsBool));
                    any = true;
                }
                if (any) rowIndex = w!.Rows.Count;
            }
            if (w == null) { Status?.Invoke($"\"{name}\"의 값을 이 아바타에서 찾지 못했어요."); return null; }
            w.GroupName = name;
            w.SetPinned(p["pinned"].AsBool);
            if (missing > 0) Status?.Invoke($"\"{name}\"에서 {missing}개는 이 아바타에 없어 빼고 열었어요.");
            return w;
        }

        public static void DeletePreset(string name)
        {
            if (_settings == null) return;
            _settings.DialPresets.Remove(name);
            _settings.Save();
        }
    }
}
