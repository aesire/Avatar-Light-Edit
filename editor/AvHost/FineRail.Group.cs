// 세밀 조정 막대의 "쉐이프키 묶음 다이얼" (2026-10-22, 사용자 요청): 블렌드쉐이프 페이지에서 특정 쉐이프키를 고르지 않았을 때는
// 마지막으로 바꾸거나 고르거나 열어 본 묶음(기타·Eye·Eyelid … 접히는 묶음 하나)의 쉐이프키를 세로로 쌓은 다이얼로 조정한다.
// 모양은 하드웨어 랙처럼: 눈금 숫자를 두른 다이얼 + 아래 이름 + 값. 묶음 이름을 누르면 다른 묶음으로 바꿀 수 있다.
// 쉐이프키 줄(이름·슬라이더·숫자 칸)을 누르면 다시 막대(한 값 세밀 조정)로, 머리의 묶음 단추로 다이얼로 돌아온다.
// 다이얼을 끄는 동안 미리보기, 놓으면(휠은 잠깐 쉬면) 저장 — 고른 쉐이프키가 여럿이면 BlendShapePanel이 고른 것 모두에.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed partial class FineRail
    {
        public sealed class Shape
        {
            public string Name = "";
            public Func<double> Get = () => 0;
            public Action<double> Preview = _ => { }, Commit = _ => { };
            public Action Reset = () => { };
            public Func<bool> Modified = () => false;
            /// <summary>이 쉐이프키 하나만 (고르기와 상관없이) — 길게 눌러 막대로·따로 띄우기</summary>
            public Func<Ui.FineTarget>? Fine;
        }

        public sealed class Group
        {
            public string Title = "";
            public double Min = 0, Max = 100;
            public List<Shape> Items = new List<Shape>();
            public List<string> Others = new List<string>();
            public Func<string, string>? Label;   // Others 메뉴에 보일 이름 (블렌드쉐이프 이름 번역)
            public Action<string>? Switch;
            public bool IsFavorites;
            public Action? ToFavorites, ToGroups;
        }

        sealed class Cell { public Shape S = null!; public Dial D = null!; public TextBlock Name = null!, Val = null!; }

        Group? _group;
        string _page = "";
        bool _forceGroup, _tIsShape, _groupDrag;
        TextBlock _title = null!;
        Button _groupBtn = null!;
        DockPanel _barView = null!, _groupView = null!;
        readonly StackPanel _groupList = new StackPanel { Spacing = 6 };
        readonly TextBlock _groupTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _groupEmpty = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        Button _groupPick = null!, _popBtn = null!, _presetBtn = null!;
        readonly List<Cell> _cells = new List<Cell>();

        public Group? CurrentGroup => _group;
        public bool InGroupView => _groupView.IsVisible;

        void BuildGroupView()
        {
            var chev = Ui.Icon(Glyph.ChevronDown, 9, Ui.KText2);
            DockPanel.SetDock(chev, Dock.Right);
            var pickContent = new DockPanel { Children = { chev, _groupTitle } };
            _groupPick = new Button { Content = pickContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 5), MinHeight = 0, Margin = new Thickness(0, 4, 0, 8) };

            _groupPick.Click += (_, __) =>
            {
                if (_group == null || _group.Others.Count == 0) return;
                var menu = new MenuFlyout();
                foreach (var k in _group.Others)
                {
                    var key = k;
                    var mi = new MenuItem { Header = _group.Label?.Invoke(key) ?? key };
                    mi.Click += (_, __) => _group?.Switch?.Invoke(key);
                    menu.Items.Add(mi);
                }
                menu.ShowAt(_groupPick);
            };
            _groupTitle.Fg(Ui.KText); _groupEmpty.Fg(Ui.KText3);

            // 묶음 | 즐겨찾기
            _segGroups = Seg("묶음", () => _group?.ToGroups?.Invoke());
            _segFav = Seg("즐겨찾기", () => _group?.ToFavorites?.Invoke());
            var seg = new UniformGrid { Columns = 2, Children = { _segGroups, _segFav } };
            var top = new StackPanel { Children = { seg, _groupPick } };
            DockPanel.SetDock(top, Dock.Top);
            var scroll = new ScrollViewer { Content = new StackPanel { Children = { _groupList, _groupEmpty } }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(-4, 0, -6, 0), Padding = new Thickness(0, 0, 6, 0) };
            _groupView = new DockPanel { Children = { top, scroll }, IsVisible = false };
        }

        Border _segGroups = null!, _segFav = null!;

        Border Seg(string label, Action click)
        {
            var b = new Border { CornerRadius = new CornerRadius(5), Margin = new Thickness(1, 0), Padding = new Thickness(0, 4, 0, 5), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand), Child = new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center } };
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) click(); };
            return b;
        }

        void PaintSeg()
        {
            foreach (var (b, on) in new[] { (_segGroups, _group != null && !_group.IsFavorites), (_segFav, _group != null && _group.IsFavorites) })
            {
                b.Bg(on ? Ui.KSelect : Ui.KCard); b.Stroke(on ? Ui.KAccent : Ui.KCardStroke);
                ((TextBlock)b.Child!).Fg(on ? Ui.KText : Ui.KText2);
            }
            _groupPick.IsVisible = _group != null && !_group.IsFavorites;
        }

        public void SetPage(string key) { _page = key; UpdateView(); }

        /// <summary>BlendShapePanel이 알려 준 묶음 (null = 메시를 바꿔 묶음이 없음). 같은 묶음·같은 쉐이프키면 다이얼을 다시 만들지 않는다</summary>
        public void SetGroup(Group? g)
        {
            bool same0 = g != null && _group != null && g.IsFavorites != _group.IsFavorites;
            bool same = g != null && _group != null && g.Title == _group.Title && g.Items.Select(x => x.Name).SequenceEqual(_group.Items.Select(x => x.Name));
            _group = g;
            if (same && !same0)
            {
                for (int i = 0; i < _cells.Count; i++) _cells[i].S = g!.Items[i];
            }
            else FillGroup();
            UpdateView();
        }

        void FillGroup()
        {
            _groupList.Children.Clear(); _cells.Clear();
            PaintSeg();
            _groupTitle.Text = _group == null ? "" : $"{_group.Title}  {_group.Items.Count}";
            _groupEmpty.Text = _group != null && _group.Items.Count == 0 ? "없음" : "";
            _groupEmpty.IsVisible = _groupEmpty.Text.Length > 0;
            if (_group == null) return;
            foreach (var s in _group.Items)
            {
                var c = new Cell
                {
                    S = s,
                    D = new Dial { Width = 92, Height = 86, ShowScale = true, Minimum = _group.Min, Maximum = _group.Max, HorizontalAlignment = HorizontalAlignment.Center },
                    Name = new TextBlock { Text = s.Name, FontSize = 10.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch },
                    Val = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch },
                };
                c.Val.Fg(Ui.KText3);

                c.D.LongPressed += () => { _groupDrag = false; if (c.S.Fine != null) Ui.OfferFine(c.S.Fine()); };
                var menu = new ContextMenu();
                var fine = new MenuItem { Header = "이것만 세밀 조정 (막대)" };
                fine.Click += (_, __) => { if (c.S.Fine != null) Ui.OfferFine(c.S.Fine()); };
                var pop = new MenuItem { Header = "따로 띄우기 (떠 있는 다이얼)" };
                pop.Click += (_, __) => { if (c.S.Fine != null) DialBoard.Open(c.S.Fine()); };
                menu.Items.Add(fine); menu.Items.Add(pop);
                c.D.ContextMenu = menu;
                ToolTip.SetTip(c.Name, s.Name);
                c.D.DragStarted += () => _groupDrag = true;
                c.D.Changing += v => { _groupDrag = true; c.S.Preview(v); c.Val.Text = Ui.F(Math.Round(v, 1)); };
                c.D.Committed += v => { _groupDrag = false; c.S.Commit(v); };
                c.D.ResetRequested += () => c.S.Reset();
                var cell = new Border { Padding = new Thickness(2, 2, 2, 6), CornerRadius = new CornerRadius(6), Child = new StackPanel { Children = { c.D, c.Name, c.Val } } };
                cell.Bg(Ui.KCard);
                _groupList.Children.Add(cell);
                _cells.Add(c);
                Paint(c);
            }
        }

        void Paint(Cell c)
        {
            double v;
            try { v = c.S.Get(); } catch { return; }
            if (Math.Abs(c.D.Value - v) > 1e-9) c.D.Value = v;
            var t = Ui.F(Math.Round(v, 1));
            if (c.Val.Text != t) c.Val.Text = t;
            c.Name.Fg(c.S.Modified() ? Ui.KAccentText : Ui.KText);
        }

        void PollGroup()
        {
            if (!_groupView.IsVisible || _groupDrag) return;
            foreach (var c in _cells) Paint(c);
        }

        static bool IsShapeSource(Control? c)
            => c != null && (c.GetVisualAncestors().OfType<StyledElement>().Any(a => a.Classes.Contains("shaperow")));

        bool GroupMode => _group != null && _page == "shapes" && !(_pinned && _t != null && !_forceGroup) && (_forceGroup || _t == null || !_tIsShape);

        void UpdateView()
        {
            if (_groupView == null) return;
            bool g = GroupMode;
            _barView.IsVisible = !g;
            _groupView.IsVisible = g;
            _title.Text = g ? "쉐이프키 묶음" : "세밀 조정";
            _groupBtn.IsVisible = !g && _group != null && _page == "shapes";
            _popBtn.IsVisible = !g && _t != null;
            if (g) PollGroup();
        }

        void ShowPresets()
        {
            var menu = new MenuFlyout();
            var names = DialBoard.Presets.ToList();
            if (names.Count == 0) menu.Items.Add(new MenuItem { Header = "저장한 프리셋 없음", IsEnabled = false });
            foreach (var n in names)
            {
                var name = n;
                var mi = new MenuItem { Header = name };
                var open = new MenuItem { Header = "열기" };
                open.Click += (_, __) => DialBoard.OpenPreset(name);
                var del = new MenuItem { Header = "지우기" };
                del.Click += (_, __) => DialBoard.DeletePreset(name);
                mi.Items.Add(open); mi.Items.Add(del);
                menu.Items.Add(mi);
            }
            menu.ShowAt(_presetBtn);
        }

        // ---------------- 화면 확인 모드 시험 ----------------
        public Dial? FirstGroupDial => _cells.FirstOrDefault()?.D;
        public Shape? FirstGroupShape => _cells.FirstOrDefault()?.S;
    }
}
