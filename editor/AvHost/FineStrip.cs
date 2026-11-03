// 렌더러 보기 바로 아래의 가로 세밀 조정 막대 + 입력 바 (2026-10-22, 사용자 요청).
//   평소: 오른쪽 세로 막대(FineRail)와 같은 일 — 슬라이더·숫자 이름·숫자 칸을 누르면 그 값이 와서 큰 가로 막대로 조정된다.
//     [핀] 이름 · 값 | − 막대 + | 1× 10× 100× | ⌨ 접기. 막대: 엄지를 끌면 상대 이동, 빈 곳을 누르면 그 자리, 휠·−/+는 한 칸 (누르고 있으면 되풀이).
//   입력 바: 입력 칸을 길게 누르면(InputBar) 같은 줄이 입력 키로 바뀐다 — 이름·값 자리는 그대로, 가운데·오른쪽만 키로. 끝나면 막대로 돌아온다.
//     ⌨를 누르면 지금 잡은 값을 키로 입력한다.
//   핀: 꽂으면 다른 슬라이더·숫자를 눌러도 이 값을 그대로 잡고 있다 (세로 막대와 따로). 길게 눌러 여는 입력 키는 핀과 상관없이 그 칸.
//   모든 키는 InputBar.KeyCap — 같은 높이·한 칸 너비·간격. 접으면 숨고, 렌더러 아래 안내 줄 오른쪽 단추로 다시 연다 (설정 fineStripOpen, 기본 닫힘).
using System;
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
    public sealed class FineStrip : Border
    {
        static readonly int[] Zooms = { 1, 10, 100 };

        bool _open, _pinned;
        Ui.FineTarget? _t;
        int _zoom = 1;
        double _shown, _start;
        double? _pending;
        bool _dragging;
        InputBar.Session? _ses;
        EventHandler<PointerPressedEventArgs>? _outside;

        readonly DockPanel _row;
        readonly Border _pin;
        readonly TextBlock _name = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Width = 104, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        readonly TextBox _value;
        readonly ContentControl _valueSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        readonly ContentControl _middleSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        readonly ContentControl _rightSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        readonly Control _barMiddle, _barRight;
        readonly FineBar _bar = new FineBar(horizontal: true) { Height = InputBar.KeyH };
        readonly Border[] _zoomKeys = new Border[Zooms.Length];
        readonly DispatcherTimer _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        public event Action<bool>? OpenChanged;
        public Ui.FineTarget? Target => _t;
        public bool IsOpen => _open;
        public bool InKeys => _ses != null;

        public FineStrip(bool open)
        {
            // ---- 왼쪽 (막대·입력 키 공통): 핀 · 이름 · 값 ----
            _pin = InputBar.KeyCap(null, () => { _pinned = !_pinned; PaintPin(); }, glyph: Glyph.Pin, led: true);
            _value = Ui.NumberCell(v => { if (_t != null) { Flush(); Apply(v, commit: true); } }, ShowValue);
            _value.FontSize = 17; _value.Width = 120; _value.Height = InputBar.KeyH; _value.MinHeight = InputBar.KeyH; _value.TextAlignment = TextAlignment.Center;
            _value.Margin = new Thickness(InputBar.KeyGap / 2, 0); _value.VerticalContentAlignment = VerticalAlignment.Center;
            _valueSlot.Content = _value;
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _pin, _name, _valueSlot, InputBar.Sep() } };

            // ---- 가운데 (막대): − 막대 + ----
            var minus = InputBar.KeyCap(null, () => Nudge(-1), glyph: "", tip: "한 칸 줄이기 (누르고 있으면 계속)", repeat: true);
            var plus = InputBar.KeyCap(null, () => Nudge(+1), glyph: Glyph.Add, tip: "한 칸 늘리기 (누르고 있으면 계속)", repeat: true);
            DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(plus, Dock.Right);
            _bar.Margin = new Thickness(4, 0);
            _barMiddle = new DockPanel { Children = { minus, plus, _bar } };

            // ---- 오른쪽 (막대): 배율 | 키로 입력 · 접기 ----
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(InputBar.Sep());
            for (int i = 0; i < Zooms.Length; i++)
            {
                int z = Zooms[i];
                right.Children.Add(_zoomKeys[i] = InputBar.KeyCap(z + "×", () => SetZoom(z), 1.25, tip: z == 1 ? "전체 범위" : $"범위를 {z}분의 1로 좁혀 세밀하게", led: true, fontSize: 13));
            }
            right.Children.Add(InputBar.Sep());
            right.Children.Add(InputBar.KeyCap(null, KeysForTarget, glyph: Glyph.Keyboard, tip: "이 값을 키로 입력"));
            right.Children.Add(InputBar.KeyCap(null, () => SetOpen(false), glyph: Glyph.ChevronDown, tip: "가로 세밀 조정 막대 접기"));
            _barRight = right;

            _middleSlot.Content = _barMiddle; _rightSlot.Content = _barRight;
            DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(_rightSlot, Dock.Right);
            _row = new DockPanel { Margin = new Thickness(0, 6, 0, 2), Children = { left, _rightSlot, _middleSlot } };

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
            Margin = new Thickness(8, 0, 8, 0);
            _open = open;
            Show();
            Clear();
            PaintPin();
        }

        public void SetOpen(bool open)
        {
            if (_open == open) return;
            if (!open) _ses?.Close();
            _open = open;
            Show();
            OpenChanged?.Invoke(open);
        }

        void Show()
        {
            // 접으면 줄을 통째로 숨긴다 — 여는 단추는 아래 안내 줄 오른쪽(MainWindow.Preview)에 있어 따로 손잡이 줄을 두지 않는다 (2026-10-22 사용자: 2단일 필요 없음)
            Child = _row;
            IsVisible = _open;
            if (_open) _poll.Start(); else _poll.Stop();
        }

        void PaintPin()
        {
            InputBar.ChipOf(_pin).On = _pinned;
            ToolTip.SetTip(_pin, _pinned ? "핀 뽑기 (다시 누르는 값을 따라가요)" : "핀 꽂기: 다른 슬라이더·숫자를 눌러도 이 값을 계속 잡고 있어요");
        }

        /// <summary>값을 잊는다 (다른 아바타를 열었을 때)</summary>
        public void Clear()
        {
            Flush();
            _t = null; _pinned = false; PaintPin();
            _value.Text = ""; _value.IsEnabled = false;
            _bar.Enabled = false; _bar.InvalidateVisual();
            ShowName();
            PaintZoom();
        }

        void ShowName()
        {
            if (_ses != null) return;
            if (_t == null) { _name.Text = Texts.T("슬라이더·숫자를 누르면 여기서 조정"); _name.Fg(Ui.KText3); _name.FontWeight = FontWeight.Normal; ToolTip.SetTip(_name, null); }
            else { _name.Text = _t.Name; _name.Fg(Ui.KText); _name.FontWeight = FontWeight.SemiBold; ToolTip.SetTip(_name, _t.Name); }
        }

        public void SetTarget(Ui.FineTarget t)
        {
            if (_dragging) return;
            Flush();
            _t = t;
            ShowName();
            _value.IsEnabled = true; _bar.Enabled = true;
            _shown = t.Get();
            if (!t.Bounded && _zoom == 100) _zoom = 10;
            ShowValue();
            Frame(true);
            PaintZoom();
        }

        void SetZoom(int z) { _zoom = z; Frame(true); PaintZoom(); }

        void PaintZoom()
        {
            for (int i = 0; i < Zooms.Length; i++) InputBar.ChipOf(_zoomKeys[i]).On = Zooms[i] == _zoom;
        }

        double StepNow => _t == null ? 0.01 : _t.IsInt ? 1 : _t.Step / _zoom;
        int Digits => _t != null && _t.IsInt ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(StepNow)) + 1, 0, 6);

        /// <summary>막대가 보여 주는 범위 (배율에 따라 지금 값 둘레로 좁힌다). force가 아니면 값이 범위를 벗어났을 때만 옮긴다</summary>
        void Frame(bool force)
        {
            if (_t == null) return;
            if (!_t.Bounded) { _bar.Jog = true; _bar.PerPixel = _t.Step / _zoom; ToolTip.SetTip(_bar, $"좌우로 끌어 돌리기 · 한 칸 {Ui.F(StepNow)}"); }
            else
            {
                _bar.Jog = false;
                double span = (_t.Max - _t.Min) / _zoom;
                if (force || _shown < _bar.Lo || _shown > _bar.Hi || _bar.Hi - _bar.Lo != span)
                {
                    double lo = Math.Clamp(_shown - span / 2, _t.Min, _t.Max - span);
                    _bar.Lo = lo; _bar.Hi = lo + span;
                }
                ToolTip.SetTip(_bar, $"{Ui.F(Math.Round(_bar.Lo, Digits))} ~ {Ui.F(Math.Round(_bar.Hi, Digits))} · Shift는 천천히 · 휠은 한 칸");
            }
            _bar.Value = _shown;
            _bar.InvalidateVisual();
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

        /// <summary>−/+·휠로 바꾼 값을 한 번에 저장 (실행 취소 한 단계)</summary>
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
            if (_t == null || _dragging || _pending != null) return;
            double v;
            try { v = _t.Get(); } catch { return; }
            if (Math.Abs(v - _shown) < 1e-9) return;
            _shown = v;
            ShowValue();
            Frame(false);
        }

        // ---------------- 입력 키 (InputBar) ----------------
        /// <summary>같은 줄을 입력 키로 바꾼다: 이름·값 자리 = 입력할 칸, 가운데·오른쪽 = 키. 확인·취소·바깥 누르기로 막대로 돌아온다</summary>
        public void ShowKeys(InputBar.Spec spec)
        {
            _ses?.Close();
            bool wasClosed = !_open;   // 접혀 있었으면 입력이 끝난 뒤 다시 접는다
            if (wasClosed) SetOpen(true);
            var top = TopLevel.GetTopLevel(this);
            var ses = _ses = InputBar.Build(spec, top);
            _name.Text = spec.Name; _name.Fg(Ui.KText); _name.FontWeight = FontWeight.SemiBold; ToolTip.SetTip(_name, spec.Name);
            _valueSlot.Content = ses.Preview;
            _middleSlot.Content = ses.Middle;
            _rightSlot.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { InputBar.Sep(), ses.Right } };
            // 이 줄 바깥을 누르면 취소 (다른 칸을 길게 누르면 그 칸으로 새로 연다)
            _outside = (_, e) => { if (e.Source is Visual v && (v == this || this.IsVisualAncestorOf(v))) return; ses.Close(); };
            top?.AddHandler(InputElement.PointerPressedEvent, _outside, RoutingStrategies.Tunnel, handledEventsToo: true);
            ses.Ended += () =>
            {
                if (_outside != null) top?.RemoveHandler(InputElement.PointerPressedEvent, _outside);
                _outside = null;
                if (_ses != ses) return;
                _ses = null;
                _valueSlot.Content = _value; _middleSlot.Content = _barMiddle; _rightSlot.Content = _barRight;
                ShowName();
                if (wasClosed) SetOpen(false);
            };
        }

        /// <summary>⌨: 지금 잡은 값을 키로 입력</summary>
        void KeysForTarget()
        {
            if (_t is not Ui.FineTarget t) return;
            ShowKeys(new InputBar.Spec
            {
                Name = t.Name, Number = true, Get = () => Ui.F(Math.Round(_shown, Math.Max(Digits, 4))),
                Set = s => { if (Ui.TryParse(s, out var v) && _t == t) { Flush(); Apply(v, commit: true); } },
            });
        }

        // ---------------- 화면 확인 모드 시험 ----------------
        public string SelfTest()
        {
            if (_t == null) return "FAIL strip no target";
            double v0 = _t.Get();
            Nudge(+1); Nudge(+1); Flush();
            double v1 = _t.Get();
            bool up = v1 > v0 || (_t.Bounded && v0 >= _t.Max);
            Apply(v0, commit: true);
            return $"{(up ? "ok" : "FAIL")} strip nudge {_t.Name}: {Ui.F(v0)} → {Ui.F(v1)}";
        }
    }
}
