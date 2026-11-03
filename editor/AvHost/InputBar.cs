// 키보드 없이 입력하는 입력 바 (2026-10-22, 사용자 요청 — 맥북 터치바처럼, VR 오버레이·터치용).
//   입력 칸을 0.45초 길게 누르면 한 줄짜리 입력 키가 뜬다. 메인 창의 칸이면 렌더러 보기 아래 가로 세밀 조정 막대(FineStrip)가 이 키로 바뀌고,
//   대화상자 같은 다른 창이면 그 창 아래쪽 끝에 바가 붙는다(에디터 안 Embed — 창 공유에도 보임). 왼쪽 칸 이름 · 지금 값, 가운데 누를 것, 오른쪽 ⌫ · 모두 지우기 · 확인 · 닫기.
//   · 숫자 칸(Ui.NumberCell의 "num" 클래스, 또는 값·예시가 숫자 목록인 칸): 1~0 · 소수점 · 빼기 · 쉼표 키 (+ 값이 숫자 하나면 −1 −0.1 +0.1 +1).
//   · 글 칸: 자주 쓰는 글 칩 — 그 칸에 정해 둔 선택지(SetChoices) + 그 칸에 최근 입력한 글(칸 이름별 기록, 설정 폴더 input-history.json)
//     + 예시 글(워터마크 "예: 겉옷, 모자"). 칩을 누르면 그 글로 바꾼다. 붙여넣기 · 123(숫자 키 보이기)도.
//   처음 누르는 키는 값을 통째로 바꾼다(칸을 눌렀을 때 전체 선택되는 것과 같게), 그 뒤로는 끝에 붙는다.
//   확인을 눌러야 칸에 들어간다 (칸에 넣은 뒤 포커스를 빼서 Ui.Cell의 확정이 돈다). Esc·✕·바깥 누르기는 취소.
//   모양: 모든 키가 같은 높이·한 칸 너비·간격(Key), 묶음 사이는 세로 구분선. 키는 페이지 안 탭과 같은 칩(다이얼 모드면 장비 키), 확인만 강조색 두 칸.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public static class InputBar
    {
        public static void Install()
        {
            InputElement.PointerPressedEvent.AddClassHandler<TextBox>(OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerMovedEvent.AddClassHandler<TextBox>(OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerReleasedEvent.AddClassHandler<TextBox>(OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            InputElement.PointerCaptureLostEvent.AddClassHandler<TextBox>((t, _) => { if (t == _press && !_fired) Cancel(); });
            // 글 칸 기록: 포커스를 받을 때 값을 기억했다가, 바뀐 채 떠나면 그 칸 이름으로 남긴다
            InputElement.GotFocusEvent.AddClassHandler<TextBox>((t, _) => _focusText[t] = t.Text ?? "");
            InputElement.LostFocusEvent.AddClassHandler<TextBox>((t, _) =>
            {
                if (_focusText.TryGetValue(t, out var before) && before != (t.Text ?? "")) Remember(t, t.Text ?? "");
                _focusText.Remove(t);
            });
        }

        // ---------------- 칸마다 정해 둘 수 있는 것 ----------------
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Func<IEnumerable<string>>> _choices = new();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, string> _keys = new();
        static readonly Dictionary<TextBox, string> _focusText = new();

        /// <summary>이 칸의 자주 쓰는 글(입력 바의 칩). 열 때마다 부른다.</summary>
        public static void SetChoices(TextBox t, Func<IEnumerable<string>> choices) { _choices.Remove(t); _choices.Add(t, choices); }
        public static void SetChoices(TextBox t, params string[] choices) => SetChoices(t, () => choices);
        /// <summary>기록을 묶을 이름 (없으면 워터마크 → 줄 이름)</summary>
        public static void SetKey(TextBox t, string key) { _keys.Remove(t); _keys.Add(t, key); }

        /// <summary>바에 보일 칸 이름: 찾기 칸(돋보기 아이콘)은 워터마크(괄호 속 예시는 뺀다), 그 밖은 줄 이름 → 워터마크</summary>
        static string FieldName(TextBox t)
        {
            var wm = (t.Watermark ?? "").Trim();
            int p = wm.IndexOf(" (", StringComparison.Ordinal);
            if (p > 0) wm = wm.Substring(0, p);
            var n = Ui.GuessName(t);
            return (t.InnerLeftContent != null || n == "값") && wm.Length > 0 ? wm : n;
        }
        static string HistoryKey(TextBox t) => _keys.TryGetValue(t, out var k) ? k : !string.IsNullOrWhiteSpace(t.Watermark) ? "w:" + t.Watermark : "n:" + Ui.GuessName(t);

        /// <summary>숫자 칸: NumberCell, 값이나 예시가 숫자(쉼표 목록 포함)</summary>
        public static bool IsNumberField(TextBox t)
        {
            if (t.Classes.Contains("num")) return true;
            static bool Nums(string? s) => !string.IsNullOrWhiteSpace(s) && s!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries) is var p && p.Length > 0 && p.All(x => Ui.TryParse(x, out _));
            return string.IsNullOrEmpty(t.Text) ? Nums(t.Watermark) : Nums(t.Text);
        }

        // ---------------- 최근 입력 기록 ----------------
        const int HistoryMax = 12;
        static Dictionary<string, List<string>>? _history;
        static string HistoryPath => Path.Combine(Settings.Folder, "input-history.json");

        static Dictionary<string, List<string>> History()
        {
            if (_history != null) return _history;
            _history = new Dictionary<string, List<string>>();
            try
            {
                if (File.Exists(HistoryPath))
                    foreach (var kv in JNode.Parse(File.ReadAllText(HistoryPath)).Props)
                        _history[kv.Key] = kv.Value.Items.Select(x => x.AsString).Where(x => x.Length > 0).Take(HistoryMax).ToList();
            }
            catch { }   // 깨진 기록은 버린다 (다음 저장 때 새로 쓴다)
            return _history;
        }

        static void Remember(TextBox t, string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.Length > 80 || t.IsReadOnly || t.AcceptsReturn || t.PasswordChar != default || IsNumberField(t)) return;
            var h = History();
            var key = HistoryKey(t);
            if (!h.TryGetValue(key, out var list)) h[key] = list = new List<string>();
            list.Remove(text); list.Insert(0, text);
            if (list.Count > HistoryMax) list.RemoveRange(HistoryMax, list.Count - HistoryMax);
            try
            {
                var j = JNode.Obj();
                foreach (var kv in h) { var a = JNode.Arr(); foreach (var s in kv.Value) a.Add(s); j.Set(kv.Key, a); }
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(HistoryPath, j.ToJson(indent: true));
            }
            catch { }
        }

        /// <summary>칩에 띄울 글: 정해 둔 선택지 → 최근 입력 → 예시(워터마크 "예: a, b"), 겹치면 한 번만</summary>
        static List<string> Suggestions(TextBox t)
        {
            var r = new List<string>();
            void Add(IEnumerable<string> xs) { foreach (var x in xs) { var s = x.Trim(); if (s.Length > 0 && !r.Contains(s)) r.Add(s); } }
            if (_choices.TryGetValue(t, out var f)) { try { Add(f()); } catch { } }
            if (History().TryGetValue(HistoryKey(t), out var h)) Add(h);
            var wm = t.Watermark ?? "";
            int i = wm.IndexOf("예:", StringComparison.Ordinal);
            if (i >= 0) Add(wm.Substring(i + 2).TrimEnd(')', ' ').Split(','));
            return r;
        }

        // ---------------- 길게 누르기 ----------------
        static TextBox? _press;
        static Point _pressAt;
        static bool _fired;
        static DispatcherTimer? _timer;

        static void OnPressed(TextBox t, PointerPressedEventArgs e)
        {
            if (!t.IsEnabled || t.IsReadOnly || t.AcceptsReturn || t.PasswordChar != default) return;
            if (!e.GetCurrentPoint(t).Properties.IsLeftButtonPressed) return;
            Cancel();
            _press = t; _pressAt = e.GetPosition(t); _fired = false;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(FieldStyles.HoldMs), DispatcherPriority.Input, (_, __) => Fire());
            _timer.Start();
        }

        static void OnMoved(TextBox t, PointerEventArgs e)
        {
            if (t != _press || _fired) return;
            var p = e.GetPosition(t);
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 10) Cancel();   // 끌어서 글 고르기
        }

        static void OnReleased(TextBox t, PointerReleasedEventArgs e)
        {
            if (t != _press) return;
            if (_fired) { e.Handled = true; _press = null; _fired = false; }
            Cancel();
        }

        static void Cancel() { _timer?.Stop(); _timer = null; if (!_fired) _press = null; }

        static void Fire()
        {
            _timer?.Stop(); _timer = null;
            if (_press is not TextBox t) return;
            _fired = true;
            Show(t);
        }

        // ---------------- 키 (입력 바·가로 세밀 조정 막대가 같이 쓴다) ----------------
        // 모든 키는 같은 높이(KeyH)·같은 한 칸 너비(KeyW)·같은 간격(KeyGap). 넓은 키는 칸 수로 (확인 = 2칸), 글 칩만 글 길이대로.
        public const double KeyH = 44, KeyW = 46, KeyGap = 4;

        /// <summary>키 한 개: 페이지 안 탭과 같은 칩(다이얼 모드면 장비 키). repeat면 누르고 있는 동안 되풀이(−/+). Tag = SubTabs.TabChip</summary>
        public static Border KeyCap(string? text, Action act, double units = 1, string? glyph = null, string? tip = null, bool on = false, bool accent = false, bool repeat = false, bool led = false, double fontSize = 15)
        {
            Control content;
            if (glyph != null) content = Ui.Icon(glyph, 15, Ui.KText);
            else
            {
                var tb = Ui.Text(text ?? "", fontSize, on || accent ? FontWeight.SemiBold : FontWeight.Normal, on && !Hardware.On ? Ui.KAccentText : Ui.KText);
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                content = tb;
            }
            if (accent && content is TextBlock at) at.Foreground = Hardware.On ? new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1A)) : Brushes.White;
            content.VerticalAlignment = VerticalAlignment.Center; content.HorizontalAlignment = HorizontalAlignment.Center;
            var chip = new SubTabs.TabChip(content) { Framed = true, On = on, Accent = accent, ShowLed = led };
            chip.Inner.Padding = new Thickness(8, 0);
            var o = chip.Outer;
            o.Height = KeyH; o.Margin = new Thickness(KeyGap / 2, 0); o.VerticalAlignment = VerticalAlignment.Center;
            if (units > 0) o.Width = units * KeyW + (units - 1) * KeyGap;
            else if (units < 0) { o.MinWidth = 30; o.HorizontalAlignment = HorizontalAlignment.Stretch; }   // 남은 너비를 나눠 갖는 키 (숫자 키)
            else o.MinWidth = KeyW;
            if (tip != null) ToolTip.SetTip(o, tip);
            DispatcherTimer? rep = null;
            o.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(o).Properties.IsLeftButtonPressed) return;
                e.Pointer.Capture(o); e.Handled = true;
                if (!repeat) return;
                act();
                rep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                rep.Tick += (_, __) => { rep!.Interval = TimeSpan.FromMilliseconds(70); act(); };
                rep.Start();
            };
            void Stop() { rep?.Stop(); rep = null; }
            o.PointerReleased += (_, e) =>
            {
                e.Pointer.Capture(null);
                bool wasRepeat = rep != null; Stop();
                if (e.InitialPressMouseButton != MouseButton.Left || repeat || wasRepeat) return;
                if (new Rect(o.Bounds.Size).Contains(e.GetPosition(o))) act();   // 잡은 포인터는 IsPointerOver가 맞지 않아 위치로
                e.Handled = true;
            };
            o.PointerCaptureLost += (_, __) => Stop();
            return o;
        }

        internal static SubTabs.TabChip ChipOf(Border key) => (SubTabs.TabChip)key.Tag!;

        /// <summary>키 묶음 사이 세로 구분선</summary>
        public static Control Sep() => new Border { Width = 1, Height = KeyH - 16, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center }.Bg(Ui.KDivider);

        /// <summary>입력 칸과 같은 바탕·테두리의 값 상자 (키와 같은 높이)</summary>
        public static Border ValueBox(Control child, double width)
        {
            var b = new Border { Child = child, BorderThickness = new Thickness(1), Padding = new Thickness(8, 0), Height = KeyH, Width = width, ClipToBounds = true, Margin = new Thickness(KeyGap / 2, 0), VerticalAlignment = VerticalAlignment.Center };
            b.Bind(Border.BackgroundProperty, b.GetResourceObservable("TextControlBackground"));
            b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("TextControlBorderBrush"));
            b.Bind(Border.CornerRadiusProperty, b.GetResourceObservable("ControlCornerRadius"));
            return b;
        }

        // ---------------- 입력 내용 (어디에 띄우든 같다) ----------------
        /// <summary>무엇을 입력하는지: 이름 · 지금 값 · 넣기 · 숫자인지 · 자주 쓰는 글</summary>
        public sealed class Spec
        {
            public string Name = "값";
            public Func<string> Get = () => "";
            public Action<string> Set = _ => { };
            public bool Number;
            public Func<List<string>> Suggestions = () => new List<string>();
        }

        public static Spec SpecFor(TextBox t) => new Spec
        {
            Name = FieldName(t), Get = () => t.Text ?? "", Number = IsNumberField(t), Suggestions = () => Suggestions(t),
            Set = s =>
            {
                t.Text = s;
                // Ui.Cell은 포커스를 떠날 때 확정한다 → 잠깐 포커스를 줬다가 뺀다 (보통 칸은 Text만 바뀌면 된다)
                t.Focus();
                TopLevel.GetTopLevel(t)?.FocusManager?.ClearFocus();
                Remember(t, s);
            },
        };

        /// <summary>입력 한 번: 값 상자(Preview) · 가운데 키(Middle) · 오른쪽 키(Right). 확인·취소하면 Ended (한 번만)</summary>
        public sealed class Session
        {
            public Control Preview = null!, Middle = null!, Right = null!;
            public Spec Spec = null!;
            public event Action? Ended;
            internal Action? Detach;
            bool _ended;
            public bool IsEnded => _ended;
            public void Close() { if (_ended) return; _ended = true; Detach?.Invoke(); Ended?.Invoke(); }
        }

        /// <summary>키보드 입력은 keyHost(창)에서 받는다: 글자 · Backspace · Enter 확인 · Esc 취소</summary>
        public static Session Build(Spec spec, TopLevel? keyHost)
        {
            var ses = new Session { Spec = spec };
            bool number = spec.Number;
            string buffer = spec.Get();
            bool fresh = true;   // 처음 누르는 키는 통째로 바꾼다
            bool showDigits = number;

            // 값 상자: 지금 값 (처음 키 전에는 전체 선택된 것처럼 옅게) + 끝에 커서
            var value = new TextBlock { FontSize = 17, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.LeadingCharacterEllipsis };
            var valueSel = new Border { Child = value, CornerRadius = new CornerRadius(3), Padding = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
            var caret = new Border { Width = 2, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 0, 0) }.Bg(Ui.KAccent);
            var box = ValueBox(new DockPanel { LastChildFill = false, Children = { valueSel, caret } }, 120);
            box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("TextControlBorderBrushFocused"));
            ses.Preview = box;

            // 숫자 키는 남은 너비를 고르게 나눠 갖는다 (좁은 창에서 잘리거나 넘기지 않게)
            var digits = new UniformGrid { Rows = 1, Columns = 13, VerticalAlignment = VerticalAlignment.Center, MinWidth = 13 * 34 };
            var nudges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            ScrollViewer? chipScroll = null;
            bool roomForNudges = true;   // 좁으면 한 칸씩 키는 숨기고 숫자 키를 지킨다
            var nudgeSep = Sep();
            void Paint()
            {
                value.Text = buffer;
                value.Fg(fresh ? Ui.KText2 : Ui.KText);
                caret.IsVisible = !fresh;
                valueSel.Bg(fresh && buffer.Length > 0 ? Ui.KSelect : null);
                nudges.IsVisible = nudgeSep.IsVisible = number && roomForNudges && Ui.TryParse(buffer, out _);
                digits.IsVisible = showDigits;
                if (chipScroll != null) chipScroll.MaxWidth = showDigits ? 320 : double.PositiveInfinity;
            }
            void Type(string s) { if (fresh) buffer = ""; fresh = false; buffer += s; Paint(); }
            void Replace(string s) { buffer = s; fresh = false; Paint(); }
            void Back() { if (fresh) buffer = ""; else if (buffer.Length > 0) buffer = buffer.Substring(0, buffer.Length - 1); fresh = false; Paint(); }
            void Nudge(double d) { if (Ui.TryParse(buffer, out var v)) Replace(Ui.F(Math.Round(v + d, 6))); }
            void Apply() { var s = buffer; ses.Close(); spec.Set(s); }

            // 가운데: 자주 쓰는 글(넘기기) · 숫자 키(남은 너비) · 한 칸씩
            var sug = spec.Suggestions();
            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var s in sug) chips.Children.Add(KeyCap(s, () => Replace(s), 0, tip: s, on: s == buffer));
            if (!number && sug.Count == 0)
                chips.Children.Add(new TextBlock { Text = Texts.T("자주 쓰는 글이 아직 없어요 · 입력하면 여기 남아요"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) }.Fg(Ui.KText3));
            foreach (var d in "1234567890") { var s = d.ToString(); digits.Children.Add(KeyCap(s, () => Type(s), -1)); }
            digits.Children.Add(KeyCap(".", () => Type("."), -1, tip: "소수점"));
            digits.Children.Add(KeyCap("−", () => Type("-"), -1, tip: "빼기 부호"));
            digits.Children.Add(KeyCap(",", () => Type(", "), -1, tip: "쉼표 (여러 값)"));
            foreach (var (label, d) in new[] { ("−1", -1.0), ("−0.1", -0.1), ("+0.1", 0.1), ("+1", 1.0) }) nudges.Children.Add(KeyCap(label, () => Nudge(d), 1.25, repeat: true));
            var middle = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(nudges, Dock.Right); DockPanel.SetDock(nudgeSep, Dock.Right);
            middle.Children.Add(nudges); middle.Children.Add(nudgeSep);
            if (chips.Children.Count > 0)
            {
                chipScroll = new ScrollViewer { Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(chipScroll, Dock.Left);
                middle.Children.Add(chipScroll);
            }
            middle.Children.Add(new ScrollViewer { Content = digits, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center });
            middle.SizeChanged += (_, e) =>
            {
                bool room = e.NewSize.Width >= digits.MinWidth + 4 * (1.25 * KeyW + KeyGap) + 16 + (chipScroll?.Bounds.Width ?? 0);
                if (room != roomForNudges) { roomForNudges = room; Paint(); }
            };
            ses.Middle = middle;

            // 오른쪽: (글 칸) 붙여넣기 · 123 | ⌫ · 모두 지우기 | 확인 · 닫기 — 모두 한 칸 키, 확인만 두 칸
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!number)
            {
                right.Children.Add(KeyCap(null, async () =>
                {
                    var cb = keyHost?.Clipboard;
                    var s = cb != null ? await cb.TryGetTextAsync() : null;
                    if (!string.IsNullOrEmpty(s)) Type(s.Replace("\r", "").Replace("\n", " "));
                }, glyph: Glyph.Paste, tip: "붙여넣기"));
                right.Children.Add(KeyCap("123", () => { showDigits = !showDigits; Paint(); }, tip: "숫자 키 보이기·숨기기"));
                right.Children.Add(Sep());
            }
            right.Children.Add(KeyCap(null, Back, glyph: "", tip: "한 글자 지우기", repeat: true));
            right.Children.Add(KeyCap(null, () => Replace(""), glyph: "", tip: "모두 지우기"));
            right.Children.Add(Sep());
            right.Children.Add(KeyCap(Texts.T("확인"), Apply, 2, accent: true, tip: "넣기 (Enter)"));
            right.Children.Add(KeyCap(null, ses.Close, glyph: Glyph.Close, tip: "닫기 (Esc)"));
            ses.Right = right;
            Paint();

            if (keyHost != null)
            {
                EventHandler<KeyEventArgs> keys = (_, e) =>
                {
                    if (e.Key == Key.Escape) { ses.Close(); e.Handled = true; }
                    else if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
                    else if (e.Key == Key.Back) { Back(); e.Handled = true; }
                };
                EventHandler<TextInputEventArgs> text = (_, e) => { if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0])) { Type(e.Text); e.Handled = true; } };
                keyHost.AddHandler(InputElement.KeyDownEvent, keys, RoutingStrategies.Tunnel);
                keyHost.AddHandler(InputElement.TextInputEvent, text, RoutingStrategies.Tunnel);
                ses.Detach = () => { keyHost.RemoveHandler(InputElement.KeyDownEvent, keys); keyHost.RemoveHandler(InputElement.TextInputEvent, text); };
            }
            return ses;
        }

        // ---------------- 띄우기 ----------------
        /// <summary>메인 창이면 렌더러 아래 가로 막대(FineStrip)에 띄운다 (MainWindow가 정한다). 없으면 따로 뜬 바</summary>
        public static Func<Window, FineStrip?>? Inline;
        public static Window? Current { get; private set; }
        public const double BarHeight = KeyH + 16;

        public static void Show(TextBox target)
        {
            var top = TopLevel.GetTopLevel(target) as Window;
            if (top == null) return;
            var owner = Embed.IsAttached(top) && top.Owner is Window o ? o : top;
            FieldStyles.Current?.Close();
            var spec = SpecFor(target);
            if (Inline?.Invoke(owner) is FineStrip strip && strip.IsEffectivelyVisible) { Current?.Close(); strip.ShowKeys(spec); return; }
            ShowWindow(spec, owner);
        }

        /// <summary>대화상자처럼 메인 창이 아닌 곳: 그 창 아래쪽 끝에 붙는 바 (에디터 안 자식 창)</summary>
        static void ShowWindow(Spec spec, Window owner)
        {
            Current?.Close();
            var w = new Window
            {
                SystemDecorations = SystemDecorations.None, ShowInTaskbar = false, CanResize = false, ShowActivated = false,
                Width = owner.ClientSize.Width, Height = BarHeight, WindowStartupLocation = WindowStartupLocation.Manual,
                FontFamily = owner.FontFamily, FontSize = owner.FontSize, Title = spec.Name,
            };
            Ui.AddModeStyles(w);
            w.Position = owner.PointToScreen(new Point(0, owner.ClientSize.Height - BarHeight));
            var ses = Build(spec, w);
            var name = new TextBlock { Text = spec.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, Width = 120, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) }.Fg(Ui.KText2);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, ses.Preview, Sep() } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { Sep(), ses.Right } };
            var row = new DockPanel { Margin = new Thickness(10, 8) };
            DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(left); row.Children.Add(right); row.Children.Add(ses.Middle);
            // 바탕은 떠 있는 창과 같은 작업 창 색, 위쪽에 카드 테두리 한 줄
            w.Content = new Border { Child = row, BorderThickness = new Thickness(0, 1, 0, 0) }.Stroke(Ui.KCardStroke).Bg(Ui.KPanel);
            w.Bg(Ui.KPanel);

            // 바깥(그 창)을 누르면 닫는다. 창 크기가 바뀌면 자리가 어긋나므로 닫는다
            EventHandler<PointerPressedEventArgs> outside = (_, __) => ses.Close();
            EventHandler<SizeChangedEventArgs> resized = (_, __) => ses.Close();
            owner.AddHandler(InputElement.PointerPressedEvent, outside, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.SizeChanged += resized;
            ses.Ended += () => w.Close();
            w.Deactivated += (_, __) => { if (!Embed.IsAttached(w)) ses.Close(); };
            w.Closed += (_, __) => { ses.Close(); owner.RemoveHandler(InputElement.PointerPressedEvent, outside); owner.SizeChanged -= resized; if (Current == w) Current = null; };
            Current = w;
            w.Show(owner);
            Embed.Attach(w, owner);
            Embed.Focus(w);
        }
    }
}
