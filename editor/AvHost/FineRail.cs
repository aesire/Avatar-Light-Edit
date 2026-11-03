// 세밀 조정 막대 (2026-10-22, 사용자 요청): 에디터 맨 오른쪽의 접는 막대. 슬라이더를 누르거나 숫자 이름(끌어 바꾸는 글자)·숫자 칸을 누르면
// 그 값이 여기로 와서 큼지막한 세로 막대로 조정된다 — VR에서 PC 화면을 조작할 때 작은 슬라이더를 잡기 어렵고, 세밀한 수치를 맞추기 위해.
//   막대: 엄지를 끌면 상대 이동(튀지 않게), 빈 곳을 누르면 그 자리로. Shift는 10배 천천히, 휠은 한 칸씩.
//   1× / 10× / 100×: 막대가 보여 주는 범위를 전체·10분의 1·100분의 1로 좁혀 세밀하게 (지금 값 둘레).
//   범위가 없는 값(위치 같은 숫자)은 막대를 '돌리는' 모양: 위로 끌면 늘고 아래로 끌면 준다.
//   끄는 동안은 미리보기, 놓을 때 한 번 저장 (실행 취소 한 단계). +/− 단추는 연달아 눌러도 잠깐 쉬면 한 번 저장.
// 접혀 있을 때는 얇은 여백(열기 손잡이만)으로 남아 플랫 배치의 오른쪽이 빡빡해 보이지 않게 한다.
// 핀 (2026-10-22 사용자 요청): 위쪽 아이콘 줄의 핀을 꽂으면 다른 슬라이더·숫자를 눌러도 이 값을 그대로 잡고 있다 (꽂으면 아이콘만 강조색 — 막대 모양은 그대로).
//   렌더러 아래 가로 막대(FineStrip)의 핀과는 따로.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed partial class FineRail : Border
    {
        const double OpenWidth = 112, ClosedClean = 14, ClosedCard = 12;
        static readonly int[] Zooms = { 1, 10, 100 };

        bool _open, _clean = true;
        Ui.FineTarget? _t;
        int _zoom = 1;
        double _shown, _start;
        double? _pending;   // +/−·휠로 바꾸고 아직 저장하지 않은 값
        bool _dragging;

        readonly Border _closed;
        readonly DockPanel _panel;
        readonly TextBlock _name = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 6) };
        readonly TextBox _value;
        readonly TextBlock _range = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
        readonly FineBar _bar = new FineBar();
        readonly Dial _dial = new Dial { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        readonly RepeatButton _plus, _minus;
        readonly Border[] _zoomChips = new Border[Zooms.Length];
        readonly DispatcherTimer _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        public event Action<bool>? OpenChanged;
        public Ui.FineTarget? Target => _t;
        public bool IsOpen => _open;

        /// <summary>열 너비 계산용 (레이아웃 전에도): 막대가 차지하는 가로 크기</summary>
        public double Outer => _open ? OpenWidth + (_clean ? 0 : 24) : (_clean ? ClosedClean : ClosedCard);

        public FineRail(bool open)
        {
            // ---- 접힌 모양: 세로 가운데 작은 열기 손잡이. 줄 전체 어디를 눌러도 열린다 (2026-10-22 사용자 요청) ----
            var grip = new Border { Width = 10, Height = 44, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Ui.Icon(Glyph.ChevronRight, 7, Ui.KText3), IsHitTestVisible = false };
            ((TextBlock)grip.Child!).RenderTransform = new ScaleTransform(-1, 1);   // ◀ (펼치기)
            grip.Bg(Ui.KCard);
            _closed = new Border { Child = grip, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            _closed.PointerEntered += (_, __) => { _closed.Bg(Ui.KHover); grip.Bg(Ui.KSelect); };
            _closed.PointerExited += (_, __) => { _closed.Background = Brushes.Transparent; grip.Bg(Ui.KCard); };
            _closed.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SetOpen(true); };
            ToolTip.SetTip(_closed, "세밀 조정 막대 열기 (이 줄 어디를 눌러도 돼요)\n슬라이더나 숫자 이름을 누르면 그 값을 큰 세로 막대로 조정해요 (VR·세밀한 값)");

            // ---- 펼친 모양 ----
            var title = _title = Ui.Text("세밀 조정", 11, FontWeight.Normal, Ui.KText3);
            var close = Ui.IconButton(Glyph.ChevronRight, () => SetOpen(false), "세밀 조정 막대 접기");
            close.Width = 19; close.Height = 22;
            _groupBtn = Ui.IconButton(Glyph.Shapes, () => { _forceGroup = true; UpdateView(); }, "쉐이프키 묶음 다이얼로 돌아가기");
            _groupBtn.Width = 19; _groupBtn.Height = 22;
            _popBtn = Ui.IconButton(Glyph.PopOut, () => { if (_t != null) DialBoard.Open(_t, bar: true); }, "떼어 띄우기");
            _presetBtn = Ui.IconButton("\uE8F1", ShowPresets, "다이얼 프리셋");
            _pinBtn = Ui.IconButton(Glyph.Pin, () => { _pinned = !_pinned; PaintPin(); UpdateView(); }, "");
            foreach (var b in new[] { _popBtn, _presetBtn, _pinBtn }) { b.Width = 19; b.Height = 22; }   // 아이콘 다섯 개(핀 포함)가 막대 폭 안에 들어가게 칸만 조금 좁힘
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var c in new Control[] { close, _groupBtn, _popBtn, _presetBtn, _pinBtn }) { DockPanel.SetDock(c, Dock.Right); head.Children.Add(c); }
            PaintPin();
            title.IsVisible = false;   // 글자는 두지 않는다 (아이콘만)
            head.Children.Add(title);

            _value = Ui.NumberCell(v => { if (_t != null) { Flush(); Apply(v, commit: true); } }, ShowValue);
            _value.FontSize = 15; _value.MinWidth = 0; _value.TextAlignment = TextAlignment.Center; _value.HorizontalAlignment = HorizontalAlignment.Stretch; _value.Padding = new Thickness(4, 5);


            _plus = Step(Glyph.Add, +1, null);
            _minus = Step("", -1, null);

            var zoomRow = new UniformGrid { Columns = Zooms.Length, Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < Zooms.Length; i++)
            {
                int z = Zooms[i];
                var chip = _zoomChips[i] = new Border { CornerRadius = new CornerRadius(5), Margin = new Thickness(1, 0), Padding = new Thickness(0, 4, 0, 5), Cursor = new Cursor(StandardCursorType.Hand), BorderThickness = new Thickness(1), Child = new TextBlock { Text = z + "×", FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center } };
                chip.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SetZoom(z); };

                zoomRow.Children.Add(chip);
            }

            var top = new StackPanel();
            top.Children.Add(_name); top.Children.Add(_value);
            _plus.Margin = new Thickness(0, 8, 0, 6);
            top.Children.Add(_plus);
            var bottom = new StackPanel();
            _minus.Margin = new Thickness(0, 6, 0, 0);
            bottom.Children.Add(_minus); bottom.Children.Add(_dial); bottom.Children.Add(zoomRow);

            _dial.DragStarted += () => { Flush(); _dragging = true; _start = _shown; };
            _dial.Changing += v =>
            {
                if (!_dragging && _pending == null) _start = _shown;
                Apply(v, commit: false);
                if (!_dragging) _pending = _shown;
            };
            _dial.Committed += _ =>
            {
                _dragging = false; _pending = null;
                if (_t != null && Math.Abs(_shown - _start) > 1e-12) _t.Commit(_shown);
                Frame(true);
            };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            _barView = new DockPanel { Children = { top, bottom, _bar } };
            BuildGroupView();
            DockPanel.SetDock(head, Dock.Top);
            _panel = new DockPanel { Margin = new Thickness(8, 8, 8, 10), Children = { head, new Panel { Children = { _barView, _groupView } } } };
            _bar.Margin = new Thickness(6, 0);
            _name.Fg(Ui.KText); _range.Fg(Ui.KText3);

            _bar.Dragged += v => Apply(v, commit: false);
            _bar.Pressed += () => { Flush(); _dragging = true; _start = _shown; };
            _bar.Released += () =>
            {
                _dragging = false;
                if (_t != null && Math.Abs(_shown - _start) > 1e-12) _t.Commit(_shown);
                Frame(true);
            };
            _bar.Wheel += d => Nudge(d);
            _commitTimer.Tick += (_, __) => Flush();
            _poll.Tick += (_, __) => Poll();

            Ui.FineTargeted += t => { if (!(_pinned && _t != null)) SetTarget(t); };   // 핀: 잡은 값을 놓지 않는다
            _open = open;
            Show();
            Clear();
        }

        RepeatButton Step(string glyph, int dir, string? tip)
        {
            var b = new RepeatButton { Content = Ui.Icon(glyph, 14), Height = 38, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Delay = 400, Interval = 70, Padding = new Thickness(0) };
            b.Click += (_, __) => Nudge(dir);
            if (tip != null) ToolTip.SetTip(b, tip);
            return b;
        }

        bool _pinned;
        readonly Button _pinBtn;
        public bool Pinned => _pinned;

        /// <summary>핀 아이콘: 꽂으면 강조색 (바탕은 다른 아이콘처럼 비워 둔다)</summary>
        void PaintPin()
        {
            ((TextBlock)_pinBtn.Content!).Fg(_pinned ? Ui.KAccentText : Ui.KText2);
            ToolTip.SetTip(_pinBtn, _pinned ? "핀 뽑기 (다시 누르는 값을 따라가요)" : "핀 꽂기: 다른 슬라이더·숫자를 눌러도 이 값을 계속 잡고 있어요");
        }

        public void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            Show();
            OpenChanged?.Invoke(open);
        }

        public void SetClean(bool clean) { _clean = clean; Show(); }

        void Show()
        {
            Child = _open ? _panel : _closed;
            Width = _open ? OpenWidth : (_clean ? ClosedClean : ClosedCard);
            if (_open && _clean) { Margin = new Thickness(0); CornerRadius = new CornerRadius(0); BorderThickness = new Thickness(1, 0, 0, 0); this.Bg(Ui.KPanel).Stroke(Ui.KDivider); }
            else if (_open) { Margin = new Thickness(12, 0, 12, 0); CornerRadius = new CornerRadius(10); BorderThickness = new Thickness(1); this.Bg(Ui.KPanel).Stroke(Ui.KCardStroke); }
            else { Margin = new Thickness(0); CornerRadius = new CornerRadius(0); BorderThickness = new Thickness(0); Background = Brushes.Transparent; }
            if (_open) _poll.Start(); else _poll.Stop();
        }

        /// <summary>값을 잊는다 (다른 아바타를 열었을 때: 예전 슬라이더의 편집 함수가 남지 않게)</summary>
        public void Clear()
        {
            Flush();
            _t = null; _pinned = false; PaintPin();
            // 값을 잡기 전에는 빈 칸과 + − 만 있어 무엇을 하는 막대인지 알 수 없었다 (2026-11-01, FineStrip과 같은 안내)
            _name.Text = Texts.T("슬라이더·숫자를\n누르면 조정해요"); _name.Fg(Ui.KText3);
            ToolTip.SetTip(_name, null);
            _value.Text = ""; _value.IsEnabled = false; _plus.IsEnabled = false; _minus.IsEnabled = false;
            _bar.Enabled = false; _bar.InvalidateVisual();
            _dial.IsEnabled = false;
            _range.Text = "";
            PaintZoom();
            _tIsShape = false;
            UpdateView();
        }

        public void SetTarget(Ui.FineTarget t)
        {
            if (_dragging) return;
            Flush();
            _t = t;
            _tIsShape = t.Kind == "shape" || IsShapeSource(t.Source);
            _forceGroup = false;
            UpdateView();
            _name.Text = t.Name; _name.Fg(Ui.KText);

            _value.IsEnabled = _plus.IsEnabled = _minus.IsEnabled = _dial.IsEnabled = true;
            _bar.Enabled = true;
            _shown = t.Get();
            if (!t.Bounded && _zoom == 100) _zoom = 10;
            ShowValue();
            Frame(true);
            PaintZoom();
        }

        void SetZoom(int z)
        {
            _zoom = z;
            Frame(true);
            PaintZoom();
        }

        void PaintZoom()
        {
            for (int i = 0; i < Zooms.Length; i++)
            {
                bool on = Zooms[i] == _zoom;
                var c = _zoomChips[i];
                c.Bg(on ? Ui.KSelect : Ui.KCard);
                c.Stroke(on ? Ui.KAccent : Ui.KCardStroke);
                ((TextBlock)c.Child!).Fg(on ? Ui.KText : Ui.KText2);
            }
        }

        /// <summary>한 칸: 값의 기본 칸 ÷ 배율 (정수 값은 1)</summary>
        double StepNow => _t == null ? 0.01 : _t.IsInt ? 1 : _t.Step / _zoom;
        int Digits => _t != null && _t.IsInt ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(StepNow)) + 1, 0, 6);

        /// <summary>막대가 보여 주는 범위 (배율에 따라 지금 값 둘레로 좁힌다). force가 아니면 값이 범위를 벗어났을 때만 옮긴다</summary>
        void Frame(bool force)
        {
            if (_t == null) return;
            if (!_t.Bounded)
            {
                _bar.Jog = true; _bar.PerPixel = _t.Step / _zoom;
                _range.Text = $"한 칸 {Ui.F(StepNow)}";
            }
            else
            {
                _bar.Jog = false;
                double span = (_t.Max - _t.Min) / _zoom;
                if (force || _shown < _bar.Lo || _shown > _bar.Hi || _bar.Hi - _bar.Lo != span)
                {
                    double lo = Math.Clamp(_shown - span / 2, _t.Min, _t.Max - span);
                    _bar.Lo = lo; _bar.Hi = lo + span;
                }
                _range.Text = $"{Ui.F(Math.Round(_bar.Lo, Digits))} ~ {Ui.F(Math.Round(_bar.Hi, Digits))}";
            }
            _bar.Value = _shown;
            _bar.InvalidateVisual();
            // 다이얼: 막대와 같은 범위 (범위 없는 값은 지금 값 ± 50칸)
            if (!_dragging)
            {
                if (_t.Bounded) { _dial.Minimum = _bar.Lo; _dial.Maximum = _bar.Hi; }
                else { _dial.Minimum = _shown - 50 * StepNow; _dial.Maximum = _shown + 50 * StepNow; }
                _dial.Value = _shown;
            }
        }

        double Snap(double v)
        {
            if (_t == null) return v;
            v = Math.Clamp(v, _t.Min, _t.Max);
            return _t.IsInt ? Math.Round(v) : Math.Round(v, Digits);
        }

        void Apply(double v, bool commit)
        {
            if (_t == null) return;
            v = Snap(v);
            if (v != _shown || commit)
            {
                _shown = v;
                if (commit) _t.Commit(v); else _t.Preview(v);
            }
            ShowValue();
            _bar.Value = _shown;
            if (!_dragging) _dial.Value = _shown;
            if (commit) Frame(false);
            _bar.InvalidateVisual();
        }

        void Nudge(int dir)
        {
            if (_t == null || _dragging) return;
            if (_pending == null) _start = _shown;
            Apply(_shown + dir * StepNow, commit: false);
            _pending = _shown;
            Frame(false);
            _commitTimer.Stop(); _commitTimer.Start();
        }

        /// <summary>+/−·휠로 바꾼 값을 한 번에 저장 (실행 취소 한 단계)</summary>
        void Flush()
        {
            _commitTimer.Stop();
            if (_pending is double p && _t != null && Math.Abs(p - _start) > 1e-12) _t.Commit(p);
            _pending = null;
        }

        void ShowValue()
        {
            if (_t == null || _value.IsFocused) return;
            _value.Text = Ui.F(Math.Round(_shown, Math.Max(Digits, 4)));
        }

        /// <summary>실행 취소·다른 곳에서 값이 바뀌면 따라간다</summary>
        void Poll()
        {
            PollGroup();
            if (_t == null || _dragging || _pending != null) return;
            double v;
            try { v = _t.Get(); } catch { return; }
            if (Math.Abs(v - _shown) < 1e-9) return;
            _shown = v;
            ShowValue();
            Frame(false);
        }

        // ---------------- 화면 확인 모드 시험 ----------------
        public string SelfTest()
        {
            if (_t == null) return "FAIL no target";
            double v0 = _t.Get();
            Nudge(+1); Nudge(+1); Flush();
            double v1 = _t.Get();
            bool up = v1 > v0 || (_t.Bounded && v0 >= _t.Max);
            Apply(v0, commit: true);
            return $"{(up ? "ok" : "FAIL")} nudge {_t.Name}: {Ui.F(v0)} → {Ui.F(v1)} (한 칸 {Ui.F(StepNow)})";
        }
    }

    /// <summary>큰 세로 막대: 엄지(넓은 손잡이)·눈금·아래부터 채움. 끄는 동안 Dragged(값), 놓으면 Released.
    /// horizontal이면 가로 막대 (렌더러 보기 아래 FineStrip): 왼쪽부터 채우고 오른쪽으로 끌면 는다.</summary>
    sealed class FineBar : Control
    {
        public double Lo, Hi = 1, Value, PerPixel = 0.01;
        public bool Jog, Enabled;
        public event Action<double>? Dragged;
        public event Action? Pressed, Released;
        public event Action<int>? Wheel;

        const double Thumb = 26, Pad = 14;
        bool _down;
        double _lastY, _acc, _phase;

        public readonly bool Horizontal;

        public FineBar(bool horizontal = false)
        {
            Horizontal = horizontal;
            Cursor = new Cursor(horizontal ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth); ClipToBounds = false;
            ActualThemeVariantChanged += (_, __) => InvalidateVisual();
            ResourcesChanged += (_, __) => InvalidateVisual();
        }

        double Usable => Math.Max(1, (Horizontal ? Bounds.Width : Bounds.Height) - Pad * 2);
        double Frac(double v) => Hi > Lo ? Math.Clamp((v - Lo) / (Hi - Lo), 0, 1) : 0.5;
        /// <summary>값 → 막대 축 위치 (세로는 위가 큰 값, 가로는 오른쪽이 큰 값)</summary>
        double YOf(double v) => Horizontal ? Pad + Frac(v) * Usable : Pad + (1 - Frac(v)) * Usable;
        double VOf(double y) { var f = Math.Clamp((y - Pad) / Usable, 0, 1); return Lo + (Horizontal ? f : 1 - f) * (Hi - Lo); }
        double Coord(PointerEventArgs e) { var p = e.GetPosition(this); return Horizontal ? p.X : p.Y; }

        IBrush B(string key, double opacity = 1)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var o) && o is ISolidColorBrush s)
                return opacity >= 1 ? s : new SolidColorBrush(s.Color, opacity);
            return Brushes.Gray;
        }

        public override void Render(DrawingContext ctx)
        {
            var w = Bounds.Width; var h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            if (Horizontal) { RenderH(ctx, w, h); return; }
            double tw = Math.Min(40, w - 12), x = (w - tw) / 2;
            var track = new Rect(x, 4, tw, h - 8);
            ctx.DrawRectangle(B(Ui.KCard), new Pen(B(Ui.KCardStroke), 1), track, 9, 9);
            var tick = new Pen(B(Ui.KDivider), 1);
            if (!Enabled) return;
            var hl = Dial.Highlight(this);
            double ty;
            if (Jog)
            {
                // 돌리는 막대: 눈금이 끄는 만큼 흘러 움직임이 보이게, 엄지는 가운데
                double gap = 12, off = ((_phase % gap) + gap) % gap;
                for (double y = Pad + off; y < h - Pad; y += gap) ctx.DrawLine(tick, new Point(x + 8, y), new Point(x + tw - 8, y));
                ty = h / 2;
            }
            else
            {
                for (int i = 0; i <= 10; i++)
                {
                    double y = Pad + Usable * i / 10;
                    double inset = i % 5 == 0 ? 5 : 10;
                    ctx.DrawLine(tick, new Point(x + inset, y), new Point(x + tw - inset, y));
                }
                ty = YOf(Value);
                var fill = new Rect(x + 1, ty, tw - 2, Math.Max(0, track.Bottom - 1 - ty));
                ctx.DrawRectangle(new SolidColorBrush(hl, 0.30), null, fill, 8, 8);
            }
            // 엄지: 다이얼과 같은 하이라이트 색, 아주 옅은 위→아래 음영
            var thumb = new Rect(x - 4, ty - Thumb / 2, tw + 8, Thumb);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, thumb.Translate(new Vector(0, 1.5)), 7, 7);
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(UiTheme.Lerp(hl, Colors.White, 0.18), 0), new GradientStop(UiTheme.Lerp(hl, Colors.Black, 0.12), 1) },
            }, null, thumb, 7, 7);
            var grip = new Pen(new SolidColorBrush(Colors.Black, 0.45), 1.5);
            for (int i = -1; i <= 1; i++) ctx.DrawLine(grip, new Point(w / 2 - 9, ty + i * 4), new Point(w / 2 + 9, ty + i * 4));
        }

        /// <summary>가로 막대: 세로 막대와 같은 모양을 눕힌 것 (트랙 · 눈금 · 왼쪽부터 채움 · 하이라이트 엄지)</summary>
        void RenderH(DrawingContext ctx, double w, double h)
        {
            double th = Math.Min(30, h - 10), y = (h - th) / 2;
            var track = new Rect(4, y, w - 8, th);
            ctx.DrawRectangle(B(Ui.KCard), new Pen(B(Ui.KCardStroke), 1), track, 8, 8);
            var tick = new Pen(B(Ui.KDivider), 1);
            if (!Enabled) return;
            var hl = Dial.Highlight(this);
            double tx;
            if (Jog)
            {
                double gap = 12, off = ((_phase % gap) + gap) % gap;
                for (double x = Pad + off; x < w - Pad; x += gap) ctx.DrawLine(tick, new Point(x, y + 7), new Point(x, y + th - 7));
                tx = w / 2;
            }
            else
            {
                for (int i = 0; i <= 10; i++)
                {
                    double x = Pad + Usable * i / 10;
                    double inset = i % 5 == 0 ? 5 : 9;
                    ctx.DrawLine(tick, new Point(x, y + inset), new Point(x, y + th - inset));
                }
                tx = YOf(Value);
                ctx.DrawRectangle(new SolidColorBrush(hl, 0.30), null, new Rect(track.X + 1, y + 1, Math.Max(0, tx - track.X - 1), th - 2), 7, 7);
            }
            var thumb = new Rect(tx - Thumb / 2, y - 4, Thumb, th + 8);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, thumb.Translate(new Vector(0, 1.5)), 7, 7);
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(UiTheme.Lerp(hl, Colors.White, 0.18), 0), new GradientStop(UiTheme.Lerp(hl, Colors.Black, 0.12), 1) },
            }, null, thumb, 7, 7);
            var grip = new Pen(new SolidColorBrush(Colors.Black, 0.45), 1.5);
            for (int i = -1; i <= 1; i++) ctx.DrawLine(grip, new Point(tx + i * 4, h / 2 - 8), new Point(tx + i * 4, h / 2 + 8));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!Enabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var y = Coord(e);
            Pressed?.Invoke();
            _down = true; _lastY = y; _acc = Value;
            // 빈 곳을 누르면 그 자리로 (엄지를 잡으면 튀지 않고 끄는 만큼만)
            if (!Jog && Math.Abs(y - YOf(Value)) > Thumb / 2) { _acc = VOf(y); Dragged?.Invoke(_acc); }
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_down) return;
            var y = Coord(e);
            double dy = y - _lastY; _lastY = y;
            double slow = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1;
            double per = Jog ? PerPixel : (Hi - Lo) / Usable;
            _acc += (Horizontal ? dy : -dy) * per * slow;   // 세로는 위로, 가로는 오른쪽으로 끌면 는다
            if (!Jog) _acc = Math.Clamp(_acc, Lo, Hi);
            _phase += dy * slow;
            Dragged?.Invoke(_acc);
            InvalidateVisual();
        }

        void End()
        {
            if (!_down) return;
            _down = false;
            Released?.Invoke();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); End(); e.Pointer.Capture(null); }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); End(); }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (!Enabled || _down) return;
            Wheel?.Invoke(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
    }
}
