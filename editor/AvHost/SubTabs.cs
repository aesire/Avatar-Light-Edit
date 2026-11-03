// 페이지 안 탭 (2026-10-21, 사용자 요청): 긴 페이지나 서로 큰 연관이 없는 부분은 접기 대신 탭으로 나눈다. 페이지 제목 아래에 탭 막대.
// 탭 막대 오른쪽의 "띄우기"로 지금 탭을 떼어 에디터 위에 띄운다 (FloatPanel) — 떠 있는 동안 그 탭 자리에는 안내가 보이고, 닫거나 "다시 붙이기"로 돌아온다.
// 탭 내용은 ScrollViewer에 싸서 탭마다 따로 스크롤된다 (떠 있는 창으로 옮길 때도 이 ScrollViewer째 옮긴다).
// 내용을 다시 만드는 페이지(머티리얼 섹션·환경 효과)는 BeginUpdate → Add… → EndUpdate: 같은 키는 그 탭을 그대로 두고 내용만 바꾼다 (고른 탭·떠 있는 창 유지).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed class SubTabs : DockPanel
    {
        /// <summary>떠 있는 창 관리자 (MainWindow가 페이지를 만들기 전에 정한다). 없으면 띄우기 단추를 숨긴다.</summary>
        public static FloatHost? Host;
        static readonly Dictionary<string, string> LastTab = new Dictionary<string, string>();

        sealed class Tab
        {
            public string Key = "", Title = "";
            public bool Expert, More;
            public bool? State;      // 켜짐/꺼짐이 있는 탭 (머티리얼 섹션 등). null = 상태 없음
            public string BadgeText = "";
            public ScrollViewer View = null!;
            public Border Button = null!;
            public TextBlock Label = null!, Badge = null!, FloatMark = null!;
            public FloatPanel? Float;
            public bool Seen;
            public Func<Control>? Build;   // 아직 만들지 않은 내용 (AddLazy) — 보일 때·예열할 때·쉴 때 만든다
        }

        // 내용을 아직 만들지 않은 탭이 있는 SubTabs (쉬는 동안 하나씩 만든다 — BuildOnePending)
        static readonly HashSet<SubTabs> _pending = new HashSet<SubTabs>();

        readonly string _page, _pageTitle;
        readonly List<Tab> _tabs = new List<Tab>();
        readonly WrapPanel _bar = new WrapPanel { Orientation = Orientation.Horizontal };
        readonly StackPanel _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        // 탭 내용: 모든 탭의 View를 겹쳐 붙여 두고 보이기만 바꾼다 (2026-10-24, 탭을 다시 열 때 스타일·배치를 새로 하지 않게). 띄운 탭의 View는 떠 있는 창에
        readonly Panel _views = new Panel();
        readonly ContentControl _notice = new ContentControl { IsVisible = false };   // 지금 탭이 떠 있을 때의 안내
        readonly Button _floatBtn;
        readonly Border _moreBtn;
        readonly TextBlock _moreLabel = Ui.Text("기타", 13, FontWeight.SemiBold, Ui.KText2);
        string? _current;
        bool _updating, _easySeen, _refreshing;
        public event Action<string>? Changed;

        /// <param name="top">탭 위에 늘 보이는 것 (예: 메시 고르기). 페이지 제목 바로 아래</param>
        readonly bool _canFloat;

        /// <param name="canFloat">false = 띄우기 단추 없음 (떠 있는 창 안의 탭)</param>
        public SubTabs(string page, string pageTitle, Control? top = null, bool canFloat = true)
        {
            _page = page; _pageTitle = pageTitle; _canFloat = canFloat && Host != null;
            var head = new StackPanel { Margin = new Thickness(24, 0, 24, 10) };
            if (top != null) head.Children.Add(top);
            var row = new DockPanel();
            _floatBtn = Ui.IconButton(Glyph.PopOut, () => { if (_current != null) Float(_current); }, "지금 탭을 떼어 띄우기: 에디터 위 어디로든 옮기고 핀으로 맨 위에 고정해요");
            _floatBtn.IsVisible = _canFloat;
            _actions.Children.Add(_floatBtn);
            // 단추들도 탭과 같은 줄에 흘려 둔다 (오른쪽에 따로 세우면 좁은 작업 창에서 탭 막대가 좁아져 한 줄에 하나씩 쌓였다)
            _actions.Margin = new Thickness(2, 0, 0, 3);
            row.Children.Add(_bar);
            // 자주 쓰지 않는 탭(More)은 "더 보기 ▾" 메뉴로 (머티리얼 섹션처럼 탭이 많을 때 막대가 몇 줄씩 되지 않게). 고르면 그동안 막대 끝에 보인다
            var moreContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            moreContent.Children.Add(_moreLabel); moreContent.Children.Add(Ui.Icon(Glyph.ChevronDown, 9, Ui.KText2));
            _moreBtn = TabLook(moreContent);
            _moreBtn.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) ShowMoreMenu(); };
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row, Padding = new Thickness(0, 0, 0, 6) }.Stroke(Ui.KDivider);
            head.Children.Add(line);
            SetDock(head, Dock.Top);
            Children.Add(head);
            _views.Children.Add(_notice);
            Children.Add(_views);
            // 기타 단추와 오른쪽 단추는 늘 막대 끝에 두고 보이기만 바꾼다 (Refresh마다 빼고 다시 넣으면 누르는 중에 단추가 빠져 메뉴가 안 열렸다)
            _bar.Children.Add(_moreBtn); _bar.Children.Add(_actions);
            // 창의 Classes는 :pointerover 같은 의사 클래스로 마우스가 움직일 때마다 바뀐다 — 쉬운 모드가 실제로 바뀔 때만
            if (Host != null) { _easySeen = Easy; Host.Owner.Classes.CollectionChanged += (_, __) => { if (Easy != _easySeen) { _easySeen = Easy; FixExpert(); } }; }
            Hardware.Changed += Refresh;   // 다이얼 모드: 꺼짐 글씨 대신 LED
        }

        public string? Current => _current;
        /// <summary>떼어 띄운 탭이 있는지 (페이지가 숨어도 그 탭은 보인다 — Later)</summary>
        public bool HasFloated => _tabs.Any(t => t.Float != null);
        /// <summary>지금 보이는 것 (예열 확인용): 탭 머리 줄과 지금 탭 내용</summary>
        public IEnumerable<Control> Shown => _tabs.Where(t => t.Float == null && t.View.IsVisible).Select(t => (Control)t.View).Prepend((Control)Children[0]);
        public IEnumerable<string> Keys => _tabs.Select(t => t.Key);
        /// <summary>지금 모드에서 탭 막대에 보이는지 (쉬운 모드의 전문가 탭은 아님)</summary>
        public bool IsShown(string key) => _tabs.Any(t => t.Key == key && !(t.Expert && Easy));
        /// <summary>찾기(Ctrl+K)용: 지금 모드에서 보이는 탭과 그 내용</summary>
        public IEnumerable<(string Key, string Title, Control? Content)> Entries(bool easy)
        {
            var list = _tabs.Where(t => !(t.Expert && easy)).ToList();
            foreach (var t in list) Materialize(t);   // 찾기가 탭 안의 글도 보므로 (보통은 쉬는 동안 이미 만들어져 있다)
            return list.Select(t => (t.Key, t.Title, t.View.Content as Control)).ToList();
        }
        /// <summary>"더 보기" 메뉴에 든 탭인지</summary>
        public bool IsMore(string key) => _tabs.Any(t => t.Key == key && t.More);

        /// <summary>쉬운 모드의 "기타" 메뉴 맨 아래 안내 (예: "전문가 모드에서 섹션 N개가 더 보여요"). 페이지에 한 줄을 늘 차지하지 않게 메뉴로 옮겼다.</summary>
        public string MoreNote { get; set; } = "";

        /// <summary>탭 막대 오른쪽(띄우기 단추 앞)에 단추를 더한다.</summary>
        public void AddAction(Control c) { c.VerticalAlignment = VerticalAlignment.Center; _actions.Children.Insert(_actions.Children.Count - 1, c); }

        /// <summary>탭을 더한다 (같은 키가 있으면 내용·제목만 바꾼다). badge는 제목 옆 작은 글씨 (예: 개수).</summary>
        /// <param name="more">자주 쓰지 않는 탭: 막대 대신 "더 보기" 메뉴에 (고른 동안은 막대 끝에)</param>
        /// <param name="state">켜짐/꺼짐이 있는 탭: 꺼지면 제목 옆에 "꺼짐", 다이얼 모드에서는 글씨 대신 왼쪽 LED가 켜고 꺼진다</param>
        public void Add(string key, string title, Control content, bool expert = false, string badge = "", bool accentBadge = false, bool more = false, bool? state = null)
            => Add(key, title, content, null, expert, badge, accentBadge, more, state);

        /// <summary>
        /// 내용은 처음 보일 때(또는 예열·쉬는 동안·찾기) 만드는 탭 (2026-10-27, 불러오기 속도): 머티리얼 섹션 27개를 모두 만들어 붙이면
        /// 스타일 입히기까지 1.1초였고, 그중 2/3은 쉬운 모드에서 보이지 않는 전문가 섹션이었다.
        /// </summary>
        public void AddLazy(string key, string title, Func<Control> build, bool expert = false, string badge = "", bool accentBadge = false, bool more = false, bool? state = null)
            => Add(key, title, null, build, expert, badge, accentBadge, more, state);

        void Add(string key, string title, Control? content, Func<Control>? build, bool expert, string badge, bool accentBadge, bool more, bool? state)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key);
            if (t == null)
            {
                t = new Tab { Key = key };
                t.View = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
                _views.Children.Add(t.View);
                t.Label = Ui.Text(title, 13, FontWeight.SemiBold);
                t.Badge = Ui.Text("", 11, FontWeight.Normal, Ui.KText3);
                t.FloatMark = Ui.Icon(Glyph.PopOut, 10, Ui.KText2);
                var c = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                c.Children.Add(t.Label); c.Children.Add(t.Badge); c.Children.Add(t.FloatMark);
                var tab = t;
                t.Button = TabLook(c);
                t.Button.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) Select(tab.Key); };
                _bar.Children.Insert(_bar.Children.IndexOf(_moreBtn), t.Button);
                _tabs.Add(t);
            }
            else if (_updating) { _bar.Children.Remove(t.Button); _bar.Children.Insert(_bar.Children.IndexOf(_moreBtn), t.Button); _tabs.Remove(t); _tabs.Add(t); }   // 새로 더한 차례대로
            t.Title = title; t.Expert = expert; t.More = more; t.Seen = true; t.State = state;
            t.Label.Text = title;
            t.BadgeText = badge; t.Badge.Fg(accentBadge ? Ui.KAccentText : Ui.KText3);
            ShowBadge(t);

            t.Build = build;
            if (content != null) { content.Margin = new Thickness(24, t.Float != null ? 12 : 4, 24, 24); t.View.Content = content; }
            else { t.View.Content = null; _pending.Add(this); }
            if (t.Float != null) Materialize(t);
            t.Float?.SetTitle(FloatTitle(t));
            if (!_updating) { if (_current == null) Select(Remembered() ?? key); else Refresh(); }
        }

        /// <summary>아직 만들지 않은 내용을 만든다 (AddLazy)</summary>
        void Materialize(Tab t)
        {
            if (t.Build == null) return;
            var build = t.Build; t.Build = null;
            var content = build();
            content.Margin = new Thickness(24, t.Float != null ? 12 : 4, 24, 24);
            t.View.Content = content;
        }

        /// <summary>
        /// 아직 만들지 않은 탭 내용 하나를 만든다 (불러오기 끝 MainWindow.FinishLoad·쉬는 동안 IdleTick). 했으면 true.
        /// 쉬운 모드에서 숨은 전문가 탭은 만들지 않는다 — 전문가 모드로 바꾸면 예열(WarmOneDirty)이나 처음 볼 때 만든다
        /// </summary>
        public static bool BuildOnePending()
        {
            foreach (var st in _pending.ToList())
            {
                if (!st._tabs.Any(x => x.Build != null)) { _pending.Remove(st); continue; }
                var t = st._tabs.FirstOrDefault(x => x.Build != null && !(x.Expert && st.Easy));
                if (t == null) continue;
                st.Materialize(t);
                return true;
            }
            return false;
        }

        /// <summary>탭 제목 옆 작은 글씨만 바꾼다 (예: 저장한 자세 개수).</summary>
        public void SetBadge(string key, string badge, bool accent = false)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key);
            if (t == null) return;
            t.BadgeText = badge; t.Badge.Fg(accent ? Ui.KAccentText : Ui.KText3);
            ShowBadge(t);
        }

        /// <summary>켜짐/꺼짐만 바꾼다 (탭 위치는 그대로 — 켜고 끄는 동안 탭이 "기타"로 사라지지 않게).</summary>
        public void SetState(string key, bool on)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key);
            if (t == null || t.State == on) return;
            t.State = on;
            ShowBadge(t);
        }

        /// <summary>제목 옆 글씨: 받은 badge, 없으면 꺼진 탭은 "꺼짐" (다이얼 모드는 LED가 대신). LED 상태도 여기서.</summary>
        void ShowBadge(Tab t)
        {
            string text = t.BadgeText.Length > 0 ? t.BadgeText : t.State == false && !Hardware.On ? "꺼짐" : "";
            t.Badge.Text = text; t.Badge.IsVisible = text.Length > 0;
            ((TabChip)t.Button.Tag!).Lit = t.State;
        }

        public void BeginUpdate() { _updating = true; foreach (var t in _tabs) t.Seen = false; }

        public void EndUpdate()
        {
            _updating = false;
            foreach (var t in _tabs.Where(x => !x.Seen).ToList())
            {
                if (t.Float != null) { var f = t.Float; t.Float = null; f.Release(); Host?.Close(f); }
                _bar.Children.Remove(t.Button);
                _views.Children.Remove(t.View);
                t.View.Content = null;   // 내용을 놓아 준다 (늘 같은 컨트롤을 넣는 탭이 다시 생길 때 "이미 부모가 있다" 오류가 나지 않게)
                t.Build = null;
                _tabs.Remove(t);
            }
            var want = _current != null && _tabs.Any(x => x.Key == _current) ? _current : Remembered() ?? _tabs.FirstOrDefault()?.Key;
            _current = null;
            if (want != null) Select(want); else { ShowView(null); Refresh(); }
        }

        string? Remembered() => LastTab.TryGetValue(_page, out var k) && _tabs.Any(x => x.Key == k) ? k : null;

        bool Easy => Host?.Owner.Classes.Contains("easy") == true;

        public void Select(string key)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key) ?? _tabs.FirstOrDefault();
            if (t == null) return;
            if (t.Expert && Easy) t = _tabs.FirstOrDefault(x => !x.Expert) ?? t;
            bool changed = _current != t.Key;
            _current = t.Key;
            LastTab[_page] = t.Key;
            ShowView(t);
            if (t.Float != null) t.Float.Front();
            Refresh();
            if (changed) Changed?.Invoke(t.Key);
        }

        /// <summary>그 탭의 내용만 보이게 (떠 있으면 안내). null = 아무것도</summary>
        void ShowView(Tab? cur)
        {
            foreach (var t in _tabs) { t.View.IsVisible = t.Float != null || t == cur; if (t.View.IsVisible) Materialize(t); }   // 떠 있는 탭은 제 창에서 늘 보인다
            _notice.IsVisible = cur?.Float != null;
            _notice.Content = cur?.Float != null ? FloatedNotice(cur) : null;
            MainWindow.DropHiddenFocus(this);
        }

        /// <summary>
        /// 예열 (MainWindow.WarmUp): 안 보이는 탭을 투명하게 잠깐 보이게 해 배치까지 해 둔다 — 처음 누를 때 스타일·템플릿·배치를 하느라 멈추지 않게.
        /// 겹쳐 쌓은 칸이라 다른 것의 자리는 바뀌지 않고, 그리기 전에 되돌리므로 화면에는 나오지 않는다. 안쪽 탭(탭 안의 탭)도 같이.
        /// </summary>
        /// 탭 막대에 보이는 탭만: "기타" 메뉴의 잘 안 쓰는 탭과 쉬운 모드에서 숨긴 전문가 탭은 하지 않는다 (머티리얼 섹션을 모두 하면 요소 2만 개 · 2초 · 200 MB였다).
        public void WarmTabs(Action layout)
        {
            foreach (var t in _tabs.ToList()) if (Warmable(t)) WarmTab(t, layout);
        }

        bool Warmable(Tab t) => t.Float == null && !(t.Expert && Easy) && !(t.More && t.Key != _current);

        void WarmTab(Tab t, Action layout)
        {
            Materialize(t);
            bool hidden = !t.View.IsVisible;
            if (hidden) { t.View.Opacity = 0; t.View.IsHitTestVisible = false; t.View.IsVisible = true; layout(); }
            foreach (var inner in Inner(t.View)) inner.WarmTabs(layout);
            if (hidden) { t.View.IsVisible = false; t.View.Opacity = 1; t.View.IsHitTestVisible = true; }
        }

        /// <summary>
        /// 쉬는 동안 예열 (MainWindow.IdleTick): 다시 그려져 배치가 안 된(또는 한 번도 안 보인) 숨은 탭 하나만 예열한다 — 한 번에 멈추는 시간이 탭 하나 몫이 되게.
        /// 지금 탭 안의 안쪽 탭도 본다. 했으면(layout이 null이면 할 것이 있으면) true. dirty = 아래에 배치가 안 된 것이 있는지
        /// </summary>
        public bool WarmOneDirty(Action? layout, Func<Visual, bool> dirty)
        {
            foreach (var t in _tabs.ToList())
            {
                if (!Warmable(t)) continue;
                if (!t.View.IsVisible)
                {
                    if (t.Build != null || !t.View.GetVisualChildren().Any() || dirty(t.View)) { if (layout != null) WarmTab(t, layout); return true; }
                }
                else foreach (var inner in Inner(t.View)) if (inner.WarmOneDirty(layout, dirty)) return true;
            }
            return false;
        }

        /// <summary>이 컨트롤 아래 가장 바깥쪽 SubTabs들 (그 안의 SubTabs는 그 SubTabs가 맡는다)</summary>
        public static IEnumerable<SubTabs> Inner(Visual root)
        {
            foreach (var c in root.GetVisualChildren())
            {
                if (c is SubTabs st) yield return st;
                else foreach (var x in Inner(c)) yield return x;
            }
        }

        void FixExpert()
        {
            var t = _tabs.FirstOrDefault(x => x.Key == _current);
            if (t != null && t.Expert && Easy) Select(t.Key);
            else Refresh();
        }

        /// <summary>
        /// 탭 모양: 옅은 바탕(누를 곳이 보이게) + 글자, 고른 탭은 조금 더 진한 바탕과 강조색 둥근 테두리, 마우스를 올리면 밝게. 네 모서리 모두 둥글게 (2026-10-22).
        /// 단추 대신 Border (Fluent 단추 바탕이 깔리지 않게). 바탕은 두 겹: 바깥 = 카드 색(고정), 안쪽 = 마우스·고름 덧칠
        /// 큰 선택 창(FieldStyles.ShowPicker)의 항목도 같은 모양으로 쓴다.
        /// </summary>
        internal sealed class TabChip
        {
            public readonly Border Outer, Inner;
            bool _hover, _on;
            IDisposable? _fill, _line;
            public TabChip(Control content)
            {
                // 다이얼 모드(Hardware.On): 장비 키 모양 + 왼쪽 작은 LED (고른 탭은 하이라이트 색으로 켜짐)
                _led = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                DockPanel.SetDock(_led, Dock.Left);
                Inner = new Border { Child = new DockPanel { Children = { _led, content } }, Padding = new Thickness(9, 3, 9, 3), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
                Outer = new Border { Child = Inner, Margin = new Thickness(0, 0, 3, 3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), BorderBrush = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Tag = this };
                Outer.PointerEntered += (_, __) => { _hover = true; Paint(); };
                Outer.PointerExited += (_, __) => { _hover = false; Paint(); };
                // 다이얼 모드를 켜고 끌 때 다시 칠한다 (보일 때만 구독 — 큰 선택 창처럼 잠깐 쓰고 버리는 칩이 남지 않게)
                Outer.AttachedToVisualTree += (_, __) => { Hardware.Changed -= Paint; Hardware.Changed += Paint; Paint(); };
                Outer.DetachedFromVisualTree += (_, __) => Hardware.Changed -= Paint;
                Paint();
            }
            readonly Border _led;
            public Border Led => _led;
            /// <summary>고르지 않아도 카드 테두리를 그린다 (큰 선택 창 — 탭은 줄 안에 있어 테두리가 없다)</summary>
            public bool Framed { get; set; }
            /// <summary>다이얼 모드 LED를 보일지 (입력 바의 숫자 키처럼 켜고 끄는 뜻이 없는 키는 끈다)</summary>
            public bool ShowLed { get; set; } = true;
            /// <summary>주된 동작 키 (입력 바의 확인): 강조색으로 채운다 (다이얼 모드면 하이라이트 색). 글자색은 쓰는 쪽에서</summary>
            public bool Accent { get => _accent; set { _accent = value; Paint(); } }
            bool _accent;
            IDisposable? _bg;
            public bool On { get => _on; set { _on = value; Paint(); } }
            bool? _lit;
            /// <summary>다이얼 모드 LED: 켜짐/꺼짐이 있는 탭은 그 상태, 없으면 고른 탭만 켜진다.</summary>
            public bool? Lit { get => _lit; set { _lit = value; Paint(); } }
            void Paint()
            {
                _fill?.Dispose(); _fill = null; _line?.Dispose(); _line = null;
                _led.IsVisible = Hardware.On && ShowLed;
                if (Hardware.On)
                {
                    _bg?.Dispose(); _bg = null;
                    if (_accent)
                    {
                        var h = Dial.Highlight(Outer);
                        Outer.Background = new SolidColorBrush(_hover ? UiTheme.Lerp(h, Colors.White, 0.12) : h);
                        Outer.BorderBrush = new SolidColorBrush(UiTheme.Lerp(h, Colors.Black, 0.25));
                        Outer.CornerRadius = new CornerRadius(Hardware.Flat ? 4 : 5); Inner.CornerRadius = new CornerRadius(Hardware.Flat ? 3 : 4);
                        Inner.Background = Brushes.Transparent; _led.IsVisible = false;
                        return;
                    }
                    Outer.Background = Hardware.KeyBrush(Outer, _hover);
                    Outer.BorderBrush = _on ? new SolidColorBrush(Dial.Highlight(Outer), Hardware.Flat ? 0.7 : 0.55) : Hardware.KeyBorder(Outer);
                    Outer.CornerRadius = new CornerRadius(Hardware.Flat ? 4 : 5); Inner.CornerRadius = new CornerRadius(Hardware.Flat ? 3 : 4);
                    // 플랫: 고른 탭은 바탕에 하이라이트 색을 옅게 깔고, LED는 작은 네모
                    Inner.Background = Hardware.Flat && _on ? new SolidColorBrush(Dial.Highlight(Outer), 0.12) : Brushes.Transparent;
                    _led.CornerRadius = new CornerRadius(Hardware.Flat ? 1.5 : 3);
                    _led.Background = (_lit ?? _on) ? new SolidColorBrush(Dial.Highlight(Outer)) : new SolidColorBrush(Color.FromRgb(0x3C, 0x3F, 0x46));
                    return;
                }
                Outer.CornerRadius = new CornerRadius(7); Inner.CornerRadius = new CornerRadius(6);
                _bg ??= Outer.Bind(Border.BackgroundProperty, Outer.GetResourceObservable(Ui.KCard));
                if (_accent)
                {
                    _fill = Inner.Bind(Border.BackgroundProperty, Inner.GetResourceObservable(Ui.KAccent));
                    _line = Outer.Bind(Border.BorderBrushProperty, Outer.GetResourceObservable(Ui.KAccent));
                    Inner.Opacity = _hover ? 0.88 : 1;
                    return;
                }
                if (_on || _hover) _fill = Inner.Bind(Border.BackgroundProperty, Inner.GetResourceObservable(_on ? Ui.KSelect : Ui.KHover)); else Inner.Background = Brushes.Transparent;
                if (_on) _line = Outer.Bind(Border.BorderBrushProperty, Outer.GetResourceObservable(Ui.KAccent)); else if (Framed) _line = Outer.Bind(Border.BorderBrushProperty, Outer.GetResourceObservable(Ui.KCardStroke)); else Outer.BorderBrush = Brushes.Transparent;
            }
        }

        static Border TabLook(Control content) => new TabChip(content).Outer;

        /// <summary>기타 메뉴를 연 횟수 (시험용)</summary>
        public int MoreMenuOpens { get; private set; }

        void ShowMoreMenu()
        {
            MoreMenuOpens++;
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var t in _tabs.Where(x => x.More && !(x.Expert && Easy)))
            {
                string note = t.BadgeText.Length > 0 ? t.BadgeText : t.State == false ? "꺼짐" : "";
                var mi = new MenuItem { Header = t.Title + (note.Length > 0 ? "  · " + note : "") };
                var key = t.Key;
                mi.Click += (_, __) => Select(key);
                menu.Items.Add(mi);
            }
            if (MoreNote.Length > 0 && Easy)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = MoreNote, IsEnabled = false });
            }
            menu.ShowAt(_moreBtn);
        }

        void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;
            try { RefreshCore(); } finally { _refreshing = false; }
        }

        void RefreshCore()
        {
            bool easy = Easy;
            foreach (var t in _tabs)
            {
                bool on = t.Key == _current;
                t.Button.IsVisible = !(t.Expert && easy) && (!t.More || on || t.Float != null);
                // 밑줄: 테마 색 바인딩을 끊고 다시 단다 (지역 값으로 덮어도 바인딩이 살아 있어, 탭을 다시 붙일 때 고른 적 있는 탭마다 밑줄이 돌아왔다)
                ((TabChip)t.Button.Tag!).On = on;
                t.Label.Fg(on ? Ui.KText : Ui.KText2);
                t.FloatMark.IsVisible = t.Float != null;
                ToolTip.SetTip(t.Button, t.Float != null ? t.Title + " · 플로팅 창에 있습니다 (누르면 그 창으로)" : t.Title);
            }
            var cur = _tabs.FirstOrDefault(x => x.Key == _current);
            _floatBtn.IsVisible = _canFloat && cur != null && cur.Float == null;
            int more = _tabs.Count(x => x.More && !(x.Expert && easy));
            _moreBtn.IsVisible = more > 0;
            _moreLabel.Text = $"기타 {more}";
            foreach (var t in _tabs) ShowBadge(t);
            // 탭이 하나뿐이면 탭 단추는 숨기고 오른쪽 단추(띄우기 등)만 남긴다
            if (_tabs.Count(x => !(x.Expert && easy)) <= 1 && !_tabs.Any(x => x.Float != null))
                foreach (var t in _tabs) t.Button.IsVisible = false;
        }

        string FloatTitle(Tab t) => _pageTitle + " · " + t.Title;

        /// <summary>탭을 떼어 띄운다 (이미 떠 있으면 앞으로).</summary>
        public void Float(string key)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key);
            if (t == null || Host == null) return;
            if (t.Float != null) { t.Float.Front(); return; }
            Materialize(t);
            _views.Children.Remove(t.View);
            t.View.IsVisible = true;
            if (t.View.Content is Control c) c.Margin = new Thickness(20, 12, 20, 20);
            var f = Host.Show(_page + "/" + key, _page, FloatTitle(t), t.View, canDock: true, new Size(Math.Max(380, Bounds.Width), 560));
            t.Float = f;
            f.DockRequested += () => DockBack(key);
            Select(key);
        }

        /// <summary>떠 있는 탭을 작업 창에 다시 붙인다.</summary>
        public void DockBack(string key)
        {
            var t = _tabs.FirstOrDefault(x => x.Key == key);
            if (t?.Float == null) return;
            var f = t.Float; t.Float = null;
            f.Release();
            Host?.Close(f);
            if (t.View.Content is Control c) c.Margin = new Thickness(24, 4, 24, 24);
            _views.Children.Add(t.View);
            ShowView(_tabs.FirstOrDefault(x => x.Key == _current));
            Refresh();
        }

        Control FloatedNotice(Tab t)
        {
            var p = new StackPanel { Margin = new Thickness(24, 24), Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
            p.Children.Add(Ui.Wrap($"\"{t.Title}\"은(는) 떠 있는 창에 있어요. 에디터 위 어디로든 옮길 수 있고, 핀을 누르면 다른 떠 있는 창보다 위에, 다른 페이지에서도 보여요.", Ui.KText2, 13));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Button("창 보기", () => t.Float?.Front(), null, Glyph.PopOut));
            row.Children.Add(Ui.Button("다시 붙이기", () => DockBack(t.Key), "떠 있는 창을 닫고 여기로 돌려요", Glyph.DockBack));
            p.Children.Add(row);
            return p;
        }
    }
}
