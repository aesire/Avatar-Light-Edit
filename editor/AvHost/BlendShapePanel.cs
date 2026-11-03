// 블렌드쉐이프 페이지: 메시별 셰이프를 묶음(BlendShapeGroups)으로 나눠 슬라이더로 조절. 깜빡임 맞추기(Blink Fix)·입 맞추기(Mouth Fix)는 BlendShapePanel.BlinkFix.cs, 다문 입 다듬기는 BlendShapePanel.MouthLine.cs.
// 여러 개 고르기·함께 바꾸기·즐겨찾기·L·R 나누기·묶음 띄우기는 BlendShapePanel.Select.cs, 세팅 저장·비교·되돌리기는 BlendShapePanel.Sets.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel : DockPanel
    {
        readonly EditorContext _ctx;
        readonly TextBlock _summary = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly TextBox _search = Ui.SearchBox("블렌드쉐이프 이름으로 찾기");
        readonly CheckBox _showVisemes = new CheckBox { Content = "립싱크 보기", Margin = new Thickness(0, 0, 12, 0) };
        readonly CheckBox _onlyModified = new CheckBox { Content = "바꾼 것만 보기" };
        // 이름 번역 (2026-10-23): 쉐이프키 이름을 한국어·영어로 보이기 (+ 표에 없는 것은 구글 번역). 화면 표시·검색만 — 주소·즐겨찾기·올리기는 원본 이름 그대로
        static readonly string[] LangKeys = { "ko", "en", "" };
        readonly ComboBox _lang = new ComboBox { ItemsSource = new[] { "한국어 이름", "English", "원본 이름" }, MinHeight = 28, FontSize = 12, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox _google = new CheckBox { Content = "구글 번역" };
        // 목록 (반응성, 2026-10-21): 몸 메시는 블렌드쉐이프가 700개가 넘어 줄을 모두 만들면 다시 그릴 때마다 1~2초 멈췄다.
        // 묶음 머리·블렌드쉐이프 줄을 항목 목록으로 펼쳐 VirtualizingStackPanel로 보이는 줄만 만든다 (탭의 ScrollViewer 안).
        // 페이지 안 탭 (2026-10-21): "블렌드쉐이프" 목록 탭 + 다듬기 도구마다 탭 (깜빡임 맞추기 · 입 맞추기 · 다문 입 다듬기)
        readonly SubTabs _tabs;
        readonly StackPanel _note = new StackPanel(), _listTab = new StackPanel();
        readonly ItemsControl _items = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        // 지금 만들어져 화면에 붙은 줄만 (이름마다 여럿일 수 있다: 즐겨찾기 묶음·떠 있는 묶음 창에도 같은 블렌드쉐이프 줄)
        readonly Dictionary<string, List<Row>> _rows = new Dictionary<string, List<Row>>();
        readonly Settings _settings;
        readonly List<GroupItem> _groups = new List<GroupItem>();
        ScrollViewer? _scroll => _items.FindAncestorOfType<ScrollViewer>();
        bool _filtering;
        string? _renderer;

        sealed class Row { public Grid Grid = null!; public TextBlock Name = null!; public Ui.EditSlider Slider = null!; public TextBox Box = null!; public Button Reset = null!; public CheckBox Pick = null!; public Button Star = null!; }
        sealed class GroupItem { public string Key = ""; public List<string> Names = new List<string>(); public bool Open, Favorites; }
        sealed class ShapeItem { public string Name = ""; public bool First, Last; }

        public BlendShapePanel(EditorContext ctx, Settings settings)
        {
            _ctx = ctx; _settings = settings;
            RegisterResolver();
            Button Small(string label, Action a)
            {
                var b = Ui.Button(label, a); b.Padding = new Thickness(10, 2); b.MinHeight = 26; b.Margin = new Thickness(0, 0, 4, 0); b.FontSize = 12;
                b.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Text(label, 12) } };
                return b;
            }
            _splitBtn = Small("L·R로 나누기", () => SplitLR());
            _mergeBtn = Small("L·R 합치기", MergeLR);
            AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && _sel.Count > 0 && FocusManagerFocused() is not TextBox) { ClearSelection(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var picker = Ui.Card(Ui.PropRow(Ui.Text("메시"), new RendererPicker(ctx, onlyWithShapes: true), null));
            picker.Margin = new Thickness(0, 0, 0, 10);
            // 고른 블렌드쉐이프를 함께 바꾸는 막대: 탭 위(늘 보이는 곳)에, 고른 것이 있을 때만
            var top = new StackPanel();
            top.Children.Add(picker);
            SelectionBar();   // 함께 바꾸기 막대는 화면에 두지 않는다 (2026-10-22 사용자 요청: 필요 없음). 고른 줄을 바꾸면 고른 것 모두에 그대로
            _tabs = new SubTabs("shapes", "블렌드쉐이프", top);
            var list = _listTab;
            // 검색 + 보기 옵션 한 줄, 요약 + 되돌리기 한 줄 (UI 정리)
            var find = Ui.SearchRow(_search, _lang, _google, _showVisemes, _onlyModified, SetsQuickButtons());   // 끝: 현재 세팅 저장 · 세팅 ▾ (BlendShapePanel.Slides.cs)
            find.Margin = new Thickness(0, 0, 0, 2);
            list.Children.Add(find);
            var tools = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            right.Children.Add(_splitBtn); right.Children.Add(_mergeBtn);
            right.Children.Add(Ui.IconButton(Glyph.Pin, FavoriteModified, "바꾼 것을 모두 즐겨찾기에 넣기"));
            right.Children.Add(Ui.IconButton(Glyph.Reset, ResetAll, "모두 되돌리기: 이 메시의 블렌드쉐이프를 모두 원본으로"));
            SetDock(right, Dock.Right);
            tools.Children.Add(right);
            _summary.VerticalAlignment = VerticalAlignment.Center;
            tools.Children.Add(_summary);
            list.Children.Add(tools);
            _items.ItemTemplate = new FuncDataTemplate<object>((o, _) => o switch { GroupItem g => GroupHeader(g), ShapeItem s => ShapeRow(s), _ => new Control() });
            list.Children.Add(_note); list.Children.Add(_items);
            _tabs.Add("list", "블렌드쉐이프", list);
            Children.Add(_tabs);

            var searchLater = Ui.Debounce(Rebuild); // 입력이 멈추면 한 번만 다시 만든다
            _search.TextChanged += (_, __) => searchLater();
            InputBar.SetChoices(_search, () => Doc != null && _renderer != null ? ShapeNames().Where(n => !n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(n, @"^[\W_]{2,}")).Take(60) : Array.Empty<string>());   // 입력 바(길게 누르기): 키보드 없이 이름 고르기
            _showVisemes.Click += (_, __) => Rebuild();
            _onlyModified.Click += (_, __) => Rebuild();
            _lang.SelectedIndex = Math.Max(0, Array.IndexOf(LangKeys, settings.ShapeNameLang));
            ToolTip.SetTip(_lang, "블렌드쉐이프 이름을 한국어·영어로 보여 줘요 (화면에만, 원본 이름은 그대로). 원본 이름은 이름에 마우스를 올리면 보여요");
            _lang.SelectionChanged += (_, __) =>
            {
                var l = LangKeys[Math.Max(0, _lang.SelectedIndex)];
                if (l == _settings.ShapeNameLang) return;
                _settings.ShapeNameLang = l; _settings.Save(); _google.IsEnabled = l.Length > 0; RedrawFloats(); Rebuild();
            };
            _google.IsChecked = settings.ShapeNameGoogle; _google.IsEnabled = settings.ShapeNameLang.Length > 0;
            ToolTip.SetTip(_google, "번역 표에 없는 이름을 구글 번역(Cloud Translation API)으로 옮겨요. API 키는 설정 › 블렌드쉐이프 이름 번역에 넣어요. 켜면 이 메시의 블렌드쉐이프 이름이 구글로 보내져요 (인터넷 필요). 한 번 옮긴 이름은 저장해 두고 다시 묻지 않아요");
            MachineNames.ApiKey = () => _settings.GoogleTranslateKey;
            _google.Click += (_, __) => { _settings.ShapeNameGoogle = _google.IsChecked == true; _settings.Save(); if (_settings.ShapeNameGoogle) MachineNames.Retry(); RedrawFloats(); Rebuild(); };
            ShapeNameTr.Machine = (n, l) => _settings.ShapeNameGoogle ? MachineNames.Lookup(n, l) : null;
            // 안 보이면 다시 그리기를 미룬다 (머티리얼 페이지에서 메시를 고를 때 블렌드쉐이프 목록·깜빡임 맞추기까지 그리느라 멈추지 않게 — Later)
            _ctx.SelectionChanged += () => { if (_ctx.SelectedRenderer != _renderer) { _renderer = _ctx.SelectedRenderer; ClearSelection(); Later.Run(this, () => { Rebuild(); RefocusGroup(); }); } };
            _ctx.DocumentOpened += () =>
            {
                if (_ctx.Doc != null) _ctx.Doc.Changed += OnChanged;
                _renderer = _ctx.SelectedRenderer; Rebuild(); RefocusGroup();
            };
            Rebuild();
        }

        Document? Doc => _ctx.Doc;
        /// <summary>화면에 보일 이름 (번역을 켰으면 한국어). 주소·기록에는 늘 원본 이름을 쓴다</summary>
        string Disp(string name) => ShapeNameTr.Tr(name, _settings.ShapeNameLang);

        /// <summary>구글 번역을 켰으면 표에 없는 이름(이 메시의 블렌드쉐이프·묶음 이름)을 묻고, 새로 옮긴 것이 있으면 다시 그린다.</summary>
        async void AskMachine(IEnumerable<string> names)
        {
            var lang = _settings.ShapeNameLang;
            if (!_settings.ShapeNameGoogle || lang.Length == 0) return;
            var todo = names.Where(n => ShapeNameTr.NeedsMachine(n, lang) && MachineNames.Lookup(n, lang) == null).ToList();
            if (todo.Count == 0) return;
            _ctx.Status($"구글 번역: 이름 {todo.Count}개를 옮기는 중…");
            var (added, error) = await MachineNames.Fetch(todo, lang);
            if (error != null) { _ctx.Status(error); return; }
            if (added == 0) return;
            _ctx.Status($"구글 번역: 이름 {added}개를 옮겼어요.");
            if (_settings.ShapeNameLang == lang && _settings.ShapeNameGoogle) { RedrawFloats(); Rebuild(); }
        }
        object? FocusManagerFocused() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        /// 펼친 묶음 이름 (처음엔 모두 접힘 — 2026-10-20). 검색·값 바꿀 때마다 다시 그려도 펼친 것은 그대로 둔다
        readonly HashSet<string> _openGroups = new HashSet<string>();

        List<string> ShapeNames()
        {
            var r = Doc?.Original.FindRenderer(_renderer ?? "");
            var m = r == null ? null : Doc!.Original.FindMesh(r.Mesh);
            return m?.BlendShapes.Select(b => b.Name).ToList() ?? new List<string>();
        }

        void Rebuild()
        {
            _note.Children.Clear(); _groups.Clear();
            var q = (_search.Text ?? "").Trim();
            // 다듬기 도구 탭은 메시·문서가 바뀔 때 다시 만든다 (찾기·"바꾼 것만"은 목록 탭에만). 미리보기는 섹션을 다시 그릴 때 이어 간다 (FixSection)
            bool fixesToo = q.Length == 0 && _onlyModified.IsChecked != true;
            if (fixesToo) { EndFixPreviews(); EndLinePreview(); }
            void NoFixes() { if (!fixesToo) return; _tabs.BeginUpdate(); _tabs.Add("list", "블렌드쉐이프", ListTab()); AddSetsTab(); _tabs.EndUpdate(); }
            if (Doc == null) { _summary.Text = ""; _note.Children.Add(Ui.Wrap("아바타를 열면 메시별 블렌드쉐이프가 여기에 나와요.")); NoFixes(); Refill(); RefreshFloats(); return; }
            if (_renderer == null) { _summary.Text = ""; _note.Children.Add(Ui.Wrap("위에서 메시를 고르세요.")); NoFixes(); Refill(); RefreshFloats(); return; }
            var names = ShapeNames();
            UpdateSummary(names);
            if (names.Count == 0) { _note.Children.Add(Ui.Wrap("이 메시에는 블렌드쉐이프가 없어요.")); NoFixes(); Refill(); RefreshFloats(); return; }
            if (fixesToo)
            {
                // 깜빡임·입 맞추기는 한 탭에 (사용자 요청), 다문 입 다듬기는 따로
                var fixes = FixSections().OfType<Ui.FoldSection>().ToList();
                _tabs.BeginUpdate();
                _tabs.Add("list", "블렌드쉐이프", ListTab());
                AddSetsTab();   // 저장한 세팅 (BlendShapePanel.Sets.cs)
                if (fixes.Count > 0)
                {
                    var both = new StackPanel { Spacing = 12 };
                    foreach (var fx in fixes) both.Children.Add(fx.Flatten());
                    _tabs.Add("fix:match", "깜빡임·입 맞추기", both);
                }
                if (MouthLineSection() is Ui.FoldSection ml) _tabs.Add("fix:line", ShortTitle(ml.TitleText), ml.Flatten());
                _tabs.EndUpdate();
            }
            // 검색 중이거나 "바꾼 것만"이면 찾은 것이 보이게 펼친다 (기억하는 펼침은 바꾸지 않음)
            _filtering = q.Length > 0 || _onlyModified.IsChecked == true;
            bool Shown(string n, bool hiddenByDefault) =>
                (_showVisemes.IsChecked == true || !hiddenByDefault) &&
                ShapeNameTr.Matches(n, q, _settings.ShapeNameLang) &&
                (_onlyModified.IsChecked != true || Doc.IsModified(Addr.BlendShape(_renderer, n)));
            // 즐겨찾기 묶음이 맨 위 (처음엔 펼침)
            var favs = Favorites();
            var favNames = names.Where(n => favs.Contains(n) && Shown(n, false)).ToList();
            if (favNames.Count > 0) _groups.Add(new GroupItem { Key = FavKey, Names = favNames, Favorites = true, Open = _filtering || !_closedFav });
            foreach (var g in BlendShapeGroups.Build(names).Where(e => !e.IsSeparator).GroupBy(e => e.Group))
            {
                var visible = g.Where(e => Shown(e.Name, e.HiddenByDefault)).Select(e => e.Name).ToList();
                if (visible.Count > 0) _groups.Add(new GroupItem { Key = g.Key, Names = visible, Open = _filtering || _openGroups.Contains(g.Key) });
            }
            _sides = BlendShapeSides.Find(names);
            AskMachine(BlendShapeGroups.Build(names).Select(e => e.IsSeparator ? e.Group : e.Name));
            RefreshSideButtons();
            Refill();
            RefreshFloats();
        }

        Control ListTab() => _listTab;

        /// <summary>"깜빡임 맞추기 (Blink Fix)" → 탭에는 "깜빡임 맞추기"</summary>
        static string ShortTitle(string t) { var i = t.IndexOf(" (", StringComparison.Ordinal); return i > 0 ? t.Substring(0, i) : t; }

        /// <summary>묶음의 펼침대로 항목 목록을 다시 만든다 (줄 컨트롤은 화면에 보일 때 ItemTemplate이 만든다).</summary>
        void Refill()
        {
            var items = new List<object>();
            foreach (var g in _groups)
            {
                items.Add(g);
                if (!g.Open || IsFloated(g.Key)) continue;
                for (int i = 0; i < g.Names.Count; i++) items.Add(new ShapeItem { Name = g.Names[i], First = i == 0, Last = i == g.Names.Count - 1 });
            }
            _items.ItemsSource = items;
        }

        /// <summary>묶음 머리 (Ui.Fold의 머리와 같은 모양): 누르면 펼치고 접는다.</summary>
        Control GroupHeader(GroupItem g)
        {
            var chevron = Ui.Icon(g.Open ? Glyph.ChevronDown : Glyph.ChevronRight, 10, Ui.KText2);
            chevron.Width = 24; DockPanel.SetDock(chevron, Dock.Left);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Ui.Text(g.Favorites ? g.Key : Disp(g.Key), 15, FontWeight.SemiBold, g.Favorites ? Ui.KAccentText : Ui.KText));
            var count = Ui.Text($"{g.Names.Count}" + (IsFloated(g.Key) ? " · 플로팅 창에 있습니다" : ""), 12, FontWeight.Normal, Ui.KText3); count.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(count);
            var head = new DockPanel { MinHeight = 36, Background = Brushes.Transparent };
            // 오른쪽: 이 묶음 모두 고르기 · 떼어 띄우기
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            acts.Children.Add(Ui.IconButton("\uE73A", () => SelectMany(g.Names, add: !g.Names.All(_sel.Contains)), "이 묶음을 모두 고르기 (다시 누르면 풀기)"));
            if (SubTabs.Host != null) acts.Children.Add(Ui.IconButton(Glyph.PopOut, () => FloatGroup(g.Key), "이 묶음을 떼어 띄우기: 에디터 위 어디로든 옮기고 핀으로 맨 위에 고정해요"));
            DockPanel.SetDock(acts, Dock.Right);
            head.Children.Add(acts);
            head.Children.Add(chevron); head.Children.Add(title);
            var bar = new Border { Child = head, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 0, 8, 0), Margin = new Thickness(0, 0, 0, 4), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
            bar.PointerEntered += (_, __) => bar.Bg(Ui.KHover);
            bar.PointerExited += (_, __) => bar.Background = Brushes.Transparent;
            bar.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
                e.Handled = true;
                if (IsFloated(g.Key)) { FrontGroup(g.Key); return; }
                g.Open = !g.Open;
                if (g.Open) FocusGroup(g.Key);   // 열어 본 묶음 → 세밀 조정 막대의 묶음 다이얼
                if (!_filtering)
                {
                    if (g.Favorites) _closedFav = !g.Open;
                    else if (g.Open) _openGroups.Add(g.Key); else _openGroups.Remove(g.Key);
                }
                Refill();
            };
            return bar;
        }

        /// <summary>블렌드쉐이프 한 줄 (카드 모양을 줄마다 나눠 그린다: 첫 줄은 위 모서리, 끝 줄은 아래 모서리).</summary>
        Control ShapeRow(ShapeItem s)
        {
            var grid = MakeRow(s.Name, out _);
            var inner = new Border { Child = grid, Margin = new Thickness(0, s.First ? -1 : 0, 0, 0) };   // 첫 줄의 구분선은 숨긴다 (Ui.Card와 같게)
            var card = new Border
            {
                Child = new Border { ClipToBounds = true, Child = inner }, Padding = new Thickness(16, 0),
                BorderThickness = new Thickness(1, s.First ? 1 : 0, 1, s.Last ? 1 : 0),
                CornerRadius = new CornerRadius(s.First ? 8 : 0, s.First ? 8 : 0, s.Last ? 8 : 0, s.Last ? 8 : 0),
                Margin = new Thickness(0, 0, 0, s.Last ? 12 : 0),
            };
            RefreshRow(s.Name);
            return card.Bg(Ui.KCard).Stroke(Ui.KCardStroke);
        }

        void UpdateSummary(List<string> names)
        {
            int modified = names.Count(n => Doc!.IsModified(Addr.BlendShape(_renderer!, n)));
            _summary.Text = $"BlendShape {names.Count}개" + (modified > 0 ? $" · {modified}개 변경됨" : "");
        }

        /// <summary>
        /// 블렌드쉐이프 한 줄: [고르기 칸] 이름 ↺ [값 · 슬라이더] 핀(즐겨찾기). 고른 줄의 슬라이더·값을 바꾸면 고른 것 모두 같은 값으로 (실행 취소 한 번).
        /// 이름 Ctrl+클릭 = 고르기 더하기/빼기, Shift+클릭 = 마지막 고른 줄부터 여기까지. 화면에 붙어 있는 동안만 _rows에 든다 (화면에서 떨어지면 빠지고 다시 붙으면 값을 새로 읽는다).
        /// </summary>
        Control MakeRow(string name, out Row row)
        {
            var a = Addr.BlendShape(_renderer!, name);
            var r = row = new Row();
            r.Name = Ui.Text(Disp(name)); ToolTip.SetTip(r.Name, name);
            r.Slider = new Ui.EditSlider(0, 100, v => PreviewShape(name, v), v => SetShape(name, Math.Round(v, 2))) { Kind = "shape", Key = _renderer + "\u001f" + name };
            r.Box = Ui.NumberCell(v => { if (v != Doc!.Get(a).AsDouble || Many(name)) SetShape(name, v); else RefreshRow(name); }, () => RefreshRow(name));
            r.Reset = Ui.IconButton(Glyph.Reset, () => ResetShape(name), "원본 값으로 되돌리기 (고른 줄이면 고른 것 모두)");
            r.Pick = new CheckBox { MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(r.Pick, "고르기: 여러 개를 골라 함께 바꿔요 (Shift: 여기까지 이어서)");
            // 체크 상자 자체의 켜고 끄기 대신 고르기 목록이 정한다 (Shift 이어 고르기)
            r.Pick.AddHandler(InputElement.PointerPressedEvent, (_, e) => { PickClicked(name, e.KeyModifiers); e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            r.Star = Ui.IconButton(Glyph.Unpin, () => ToggleFavorite(name), "");
            r.Name.Background = Brushes.Transparent;
            r.Name.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) { PickClicked(name, e.KeyModifiers); e.Handled = true; }
                else { Ui.OfferFine(r.Slider.Fine()); FocusGroupOf(name); }   // 이름을 누르면 세밀 조정 막대로
            };
            r.Grid = Ui.PropRow(r.Name, Ui.NumberWithSlider(r.Box, r.Slider.Slider), r.Reset, 0.8);
            r.Grid.Classes.Add("shaperow");   // 세밀 조정 막대: 쉐이프키 줄에서 온 값이면 묶음 다이얼 대신 막대
            // 줄을 누르거나 값을 바꾸면 세밀 조정 막대의 묶음 다이얼이 이 묶음으로 (BlendShapePanel.Dials.cs)
            r.Slider.Slider.AddHandler(InputElement.PointerPressedEvent, (_, __) => FocusGroupOf(name), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            r.Box.GotFocus += (_, __) => FocusGroupOf(name);
            // PropRow 열 앞에 고르기 칸, 뒤에 즐겨찾기
            r.Grid.ColumnDefinitions.Insert(0, new ColumnDefinition(GridLength.Auto));
            r.Grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            foreach (var c in r.Grid.Children) Grid.SetColumn(c, Grid.GetColumn(c) + 1);
            foreach (var c in r.Grid.Children.OfType<Border>()) if (!c.IsHitTestVisible) { Grid.SetColumn(c, 0); Grid.SetColumnSpan(c, 5); }
            Grid.SetColumn(r.Pick, 0); Grid.SetColumn(r.Star, 4);
            r.Star.Margin = new Thickness(4, 0, 0, 0);
            r.Grid.Children.Add(r.Pick); r.Grid.Children.Add(r.Star);
            r.Grid.DetachedFromVisualTree += (_, __) => { if (_rows.TryGetValue(name, out var l)) { l.Remove(r); if (l.Count == 0) _rows.Remove(name); } };
            r.Grid.AttachedToVisualTree += (_, __) => { if (!_rows.TryGetValue(name, out var l)) _rows[name] = l = new List<Row>(); if (!l.Contains(r)) l.Add(r); RefreshRow(name); };
            if (!_rows.TryGetValue(name, out var list)) _rows[name] = list = new List<Row>();
            list.Add(r);
            return r.Grid;
        }

        void RefreshRow(string name)
        {
            if (Doc == null || _renderer == null || !_rows.TryGetValue(name, out var rows)) return;
            var a = Addr.BlendShape(_renderer, name);
            var v = Doc.Get(a).AsDouble;
            bool mod = Doc.IsModified(a), sel = _sel.Contains(name), fav = Favorites().Contains(name);
            foreach (var row in rows)
            {
                row.Slider.SetQuiet(v);
                if (!row.Box.IsFocused) row.Box.Text = Ui.F(v);
                Ui.MarkModified(row.Name, mod);
                row.Reset.IsVisible = mod;
                ToolTip.SetTip(row.Name, (mod ? $"{name}\n원본: {Ui.F(Doc.GetOriginal(a).AsDouble)}" : name) + "\nCtrl+클릭: 선택 · Shift+클릭: 여기까지 선택");
                row.Pick.IsChecked = sel;
                row.Grid.ClearValue(Panel.BackgroundProperty);
                if (sel) row.Grid.Bind(Panel.BackgroundProperty, row.Grid.GetResourceObservable(Ui.KSelect)); else row.Grid.Background = Brushes.Transparent;
                if (row.Star.Content is TextBlock st) { st.Text = fav ? Glyph.Pin : Glyph.Unpin; st.Fg(fav ? Ui.KAccentText : Ui.KText3); }
                ToolTip.SetTip(row.Star, fav ? "즐겨찾기에서 빼기" : "즐겨찾기: 맨 위 \"즐겨찾기\" 묶음에 모아 봐요");
            }
        }

        void OnChanged(IReadOnlyList<Addr> addrs)
        {
            bool any = false;
            // 자동 Blink Fix: 블렌드쉐이프 값을 바꾼 단계에 맞춘 깜빡임을 덧붙인다 (보이는 렌더러가 아니어도)
            if (Doc != null && _ctx.Meshes != null && !Doc.Replaying)
            {
                var rids = addrs.Where(a => a.Kind == AddrKind.BlendShape).Select(a => a.Target).ToList();
                if (rids.Count > 0) BlinkFixPlan.Follow(Doc, rids, id => _ctx.Meshes.Original(id));
            }
            foreach (var a in addrs)
                if (a.Kind == AddrKind.BlendShape && a.Target == _renderer) { RefreshRow(a.Name); any = true; }
            if (addrs.Any(a => a.Kind == AddrKind.BlendShape)) SetsChanged();
            if (any) { UpdateSummary(ShapeNames()); RefreshFixStates(); RefreshLineState(); RefreshSideButtons(); RefreshSelBar(); }
            if (addrs.Any(a => a.Kind == AddrKind.MeshEdit)) { RefreshFixStates(); RefreshLineState(); }
        }

        /// <summary>
        /// 반응 속도 재기 (개발용, --ui-shots … --shapes-bench): 이 페이지가 보이는 상태에서 렌더러 고르기·검색·묶음 펼치기·값 바꾸기·끌기 미리보기가
        /// 화면 배치까지 몇 ms 걸리는지. 바꾼 값은 실행 취소로 되돌린다.
        /// </summary>
        internal List<string> Bench(string renderer)
        {
            var log = new List<string>();
            var top = TopLevel.GetTopLevel(this);
            double Ms(Action a, int n = 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) { a(); top?.UpdateLayout(); }
                return sw.Elapsed.TotalMilliseconds / n;
            }
            void Line(string what, double ms) => log.Add($"{what}: {ms:0.0} ms");
            if (Doc == null) { log.Add("문서 없음"); return log; }
            _ctx.Select(null, null);
            Line($"메시 고르기 ({renderer}, 블렌드쉐이프 {Doc.Original.FindMesh(Doc.Original.FindRenderer(renderer)!.Mesh)!.BlendShapes.Count}개)", Ms(() => _ctx.Select(renderer, null)));
            Line("다시 그리기", Ms(Rebuild, 3));
            Line("  그중 보정 섹션 (Blink·Mouth Fix)", Ms(() => FixSections().ToList()));
            Line("  그중 입 선 보정 섹션", Ms(() => MouthLineSection()));
            Line("  그중 그룹 나누기", Ms(() => BlendShapeGroups.Build(ShapeNames()).ToList()));
            Line("검색 'e'", Ms(() => { _search.Text = "e"; Rebuild(); }));
            Line("검색 지우기", Ms(() => { _search.Text = ""; Rebuild(); }));
            Line($"묶음 {_groups.Count}개 모두 펼치기", Ms(() => { foreach (var g in _groups) { g.Open = true; _openGroups.Add(g.Key); } Refill(); }));
            Line("펼친 채 다시 그리기", Ms(Rebuild));
            // 스크롤: 한 화면씩 내려가며 그때 보이는 줄만 만들어지는지
            var sv = _scroll!;
            int pages = 0, most = 0;
            Line("한 화면 스크롤 (평균)", Ms(() => { sv.Offset = new Vector(0, sv.Offset.Y + sv.Viewport.Height); pages++; most = Math.Max(most, _rows.Count); }, 20));
            log.Add($"  스크롤 {pages}번 뒤 위치 {sv.Offset.Y:0} / {sv.Extent.Height:0}, 만들어진 줄 최대 {most}개, 지금 줄 {string.Join(", ", _rows.Keys.Take(3))}…");
            sv.Offset = new Vector(0, 0); top?.UpdateLayout();
            var names = ShapeNames();
            var shape = names.FirstOrDefault(n => !n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)) ?? names[0];
            var a = Addr.BlendShape(renderer, shape);
            int undo = 0;
            Line($"값 바꾸기 ({shape}, 놓을 때 기록)", Ms(() => { Doc.Set(a, JNode.From(10 + undo * 5), "재기"); undo++; }, 10));
            Line("끌기 미리보기 한 번", Ms(() => Doc.Preview(a, JNode.From(42)), 30));
            Line("'바꾼 것만' 켜고 끄기", Ms(() => { _onlyModified.IsChecked = !_onlyModified.IsChecked; Rebuild(); }, 2));
            for (int i = 0; i < undo; i++) Doc.Undo();
            // 찍어 볼 수 있게 앞 묶음 둘만 펼쳐 둔다
            _openGroups.Clear(); foreach (var g in _groups.Take(2)) _openGroups.Add(g.Key);
            Rebuild();
            return log;
        }

        /// <summary>
        /// 화면 확인 (--ui-shots): 이름 번역. "open:ko|en" = 그 언어로 번역되는 이름이 가장 많은 묶음을 펼침, "search:글" = 찾기, "raw" = 원본 이름,
        /// "google" = 구글 번역을 켜고 표에 없는 이름이 가장 많은 묶음을 펼침, "googlecheck" = 옮겨진 수, "end" = 처음대로. 돌려주는 글은 시험 결과 한 줄 (없으면 null).
        /// </summary>
        internal string? TranslateShot(string step)
        {
            void Lang(string l) { _lang.SelectedIndex = Array.IndexOf(LangKeys, l); }
            GroupItem? OpenMost(Func<string, bool> pick)
            {
                _search.Text = ""; _onlyModified.IsChecked = false; Rebuild();
                var g = _groups.Where(x => !x.Favorites).OrderByDescending(x => x.Names.Count(pick)).FirstOrDefault();
                _openGroups.Clear(); if (g != null) _openGroups.Add(g.Key);
                Rebuild();
                return g;
            }
            if (step.StartsWith("open:"))
            {
                var l = step.Substring(5);
                Lang(l); _google.IsChecked = _settings.ShapeNameGoogle = false;
                var g = OpenMost(n => n.Any(c => c > 0x3000) && ShapeNameTr.TableOnly(n, l) != n);
                return g == null ? "FAIL 번역할 묶음 없음" : $"ok [{l}] 묶음 \"{g.Key}\" 펼침 → 표시 {Disp(g.Key)}, 이름 {g.Names.Count}개 중 옮김 {g.Names.Count(n => Disp(n) != n)}개: {string.Join(", ", g.Names.Take(5).Select(n => n + "=" + Disp(n)))}";
            }
            if (step.StartsWith("search:"))
            {
                _search.Text = step.Substring(7); Rebuild();
                var found = _groups.Where(x => !x.Favorites).SelectMany(x => x.Names).ToList();
                return $"{(found.Count > 0 ? "ok" : "FAIL")} 찾기 \"{_search.Text}\" → {found.Count}개: {string.Join(", ", found.Take(6).Select(n => n + "=" + Disp(n)))}";
            }
            if (step == "raw") { Lang(""); _search.Text = ""; Rebuild(); return null; }
            if (step == "google")
            {
                Lang("ko"); _google.IsChecked = _settings.ShapeNameGoogle = true; MachineNames.Retry();
                var g = OpenMost(n => ShapeNameTr.NeedsMachine(n, "ko"));
                return g == null ? "FAIL 표에 없는 이름이 없음" : $"ok 구글 번역 켬, 묶음 \"{g.Key}\" 펼침 (표에 없는 이름 {g.Names.Count(n => ShapeNameTr.NeedsMachine(n, "ko"))}개)";
            }
            if (step == "googlecheck")
            {
                var need = ShapeNames().Where(n => ShapeNameTr.NeedsMachine(n, "ko")).ToList();
                var got = need.Where(n => MachineNames.Lookup(n, "ko") != null).ToList();
                return $"{(got.Count > 0 ? "ok" : "FAIL")} 구글 번역 {got.Count}/{need.Count}개: {string.Join(", ", got.Take(6).Select(n => n + "=" + Disp(n)))}";
            }
            Lang("ko"); _google.IsChecked = _settings.ShapeNameGoogle = false; _search.Text = ""; _openGroups.Clear(); Rebuild();
            return null;
        }

        void ResetAll()
        {
            if (Doc == null || _renderer == null) return;
            var r = _renderer;
            Doc.ResetWhere(a => a.Kind == AddrKind.BlendShape && a.Target == r, "블렌드쉐이프 모두 리셋");
        }
    }
}
