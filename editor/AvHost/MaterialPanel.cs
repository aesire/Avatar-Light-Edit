// 머티리얼 페이지: 메시·머티리얼 고르기, lilToon 렌더링 모드, 셰이더 프로퍼티 편집 (스키마로 자동 생성).
// 데이터와 규칙은 Core(Document·LilToonRules·PropertySections)를 그대로 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class MaterialPanel : DockPanel
    {
        // 페이지 안 탭 (2026-10-21): 위에 메시·머티리얼·셰이더·모드와 찾기(늘 보임), 아래 탭 = lilToon 섹션마다 하나 (예전엔 접는 구역)
        readonly SubTabs _tabs;
        readonly ColorHarmonyPanel _palette;
        readonly EditorContext _ctx;
        readonly ComboBox _matPick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        readonly TextBlock _shaderPath = Ui.Text("", 12, FontWeight.Normal, Ui.KText2), _shaderName = Ui.Text("", 13, FontWeight.SemiBold);
        readonly ContentControl _shaderNote = new ContentControl();
        readonly List<Control> _modeRows = new List<Control>();
        readonly ComboBox _mode = Ui.CellCombo(new ComboBox()), _transMode = Ui.CellCombo(new ComboBox());
        readonly Ui.Switch _outline;
        readonly TextBox _search = Ui.SearchBox("프로퍼티 이름으로 찾기");
        readonly CheckBox _showHidden = new CheckBox { Content = "숨김 보기", Margin = new Thickness(0, 0, 12, 0) };
        readonly CheckBox _onlyModified = new CheckBox { Content = "바꾼 것만 보기" };
        readonly StackPanel _props = new StackPanel();   // 섹션 위 안내 (지원하지 않는 셰이더 등)
        readonly StackPanel _find;
        readonly Control _shaderRow;
        readonly TextBlock _modeName = Ui.Text("렌더링 모드");
        // 잘 안 쓰는 항목을 펼친 섹션 (이번 실행 동안 기억)
        static readonly HashSet<string> RareOpen = new HashSet<string>();
        bool _built;
        readonly Dictionary<Addr, Action> _refreshers = new Dictionary<Addr, Action>();
        static List<KeyValuePair<Addr, JNode>>? _clipboard; static string _clipboardFrom = "";
        // 쉬운 모드에서 보이는 lilToon 섹션 (나머지는 바꾼 값이 있거나 찾기 중일 때만)
        static readonly HashSet<string> EasySections = new HashSet<string> { "Main", "Shadow", "Emission", "Normal Map", "Reflection", "MatCap", "Rim Light", "Outline" };
        string? _mat;
        /// <summary>MainWindow가 연결한다: 머티리얼·에셋 관리 창 열기</summary>
        public Action? OpenAssets;
        string _builtForShader = "";
        bool _suppress;

        public MaterialPanel(EditorContext ctx)
        {
            _ctx = ctx;
            _outline = new Ui.Switch(_ => ApplyMode());
            _ctx.RendererMessage += m => { if (Msg.Type(m) == Msg.BakeDone) OnBakeDone(m); };
            var page = new StackPanel();

            // 무엇을 고칠지. 섹션 편집 칸이 넓게 보이도록 위쪽은 낮게 (2026-10-22): 줄 높이를 줄이고, 넓으면 두 열.
            // lilToon이면 셰이더 줄은 숨기고 이름은 렌더링 모드 이름의 툴팁으로 (모드 줄이 lilToon임을 보여 준다)
            var pick = new FlowColumns { MinColumnWidth = 260, MaxColumns = 2, ColumnGap = 20 };
            Control Compact(Grid g) { g.MinHeight = 36; return g; }
            pick.Children.Add(Compact(Ui.PropRow(Ui.Text("메시"), new RendererPicker(ctx, onlyWithShapes: false), null)));
            pick.Children.Add(Compact(Ui.PropRow(Ui.Text("머티리얼"), _matPick, null)));
            var shader = new StackPanel { Margin = new Thickness(0, 4) };
            _shaderName.TextWrapping = TextWrapping.Wrap; _shaderPath.TextWrapping = TextWrapping.Wrap;
            _shaderPath.IsVisible = false;   // 경로는 셰이더 이름에 마우스를 올리면 보인다 (UI 정리)
            shader.Children.Add(_shaderName); shader.Children.Add(_shaderPath);
            _shaderRow = Compact(Ui.PropRow(Ui.Text("셰이더"), shader, null));
            pick.Children.Add(_shaderRow);
            foreach (var m in Enum.GetNames(typeof(LilRenderingMode))) _mode.Items.Add(m);
            foreach (var m in Enum.GetNames(typeof(LilTransparentMode))) _transMode.Items.Add(m);
            // 외곽선 스위치는 렌더링 모드 줄 오른쪽에 (따로 한 줄을 쓰지 않게)
            var modeValue = new DockPanel();
            var outlineBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 0, 0) };
            outlineBox.Children.Add(Ui.Text("외곽선", 12, FontWeight.Normal, Ui.KText2)); outlineBox.Children.Add(_outline.Control);
            DockPanel.SetDock(outlineBox, Dock.Right);
            modeValue.Children.Add(outlineBox); modeValue.Children.Add(_mode);
            var modeRow = Ui.PropRow(_modeName, modeValue, null);
            // 외곽선과 같이 있어 좁은 작업 창에서는 값 칸을 넓게 ("Opaque"가 "Op"로 잘렸다, 2026-11-01). 넓으면 다른 줄과 칸을 맞춘다
            modeRow.SizeChanged += (_, e) => modeRow.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 350 ? 0.6 : 1, GridUnitType.Star);
            _modeRows.Add(Compact(modeRow));
            _modeRows.Add(Compact(Ui.PropRow(Ui.Text("투명 방식"), _transMode, null)));
            foreach (var r in _modeRows) pick.Children.Add(r == _modeRows[1] ? Ui.ExpertBox(r) : r);   // 투명 방식은 전문가 모드
            var pickCard = Ui.Card(pick);
            pickCard.Margin = new Thickness(0, 0, 0, 8);
            page.Children.Add(pickCard);
            page.Children.Add(_shaderNote);

            // 찾기 (찾으면 맞는 프로퍼티가 있는 섹션 탭만): 늘 한 줄을 차지하던 칸을 탭 막대의 돋보기 단추로 (2026-10-22). 찾는 말이 있으면 계속 보인다
            _find = Ui.SearchRow(_search);   // 숨김 보기·바꾼 것만 보기 체크 상자는 뺐다 (2026-10-22, 바꾼 섹션은 탭의 "바꿈 N"으로 보인다)
            _find.Margin = new Thickness(0, 0, 0, 8);
            _find.IsVisible = false;
            page.Children.Add(_find);
            page.Children.Add(_props);
            _tabs = new SubTabs("material", "머티리얼", page);
            _tabs.AddAction(Ui.IconButton(Glyph.Search, ToggleFind, "프로퍼티 이름으로 찾기"));
            _tabs.AddAction(Ui.IconButton(Glyph.Copy, Copy, "값 복사: 이 머티리얼의 값을 모두 복사"));
            _tabs.AddAction(Ui.IconButton(Glyph.Paste, Paste, "붙여넣기: 복사한 값 중 이 셰이더에 있는 프로퍼티만 적용"));
            _tabs.AddAction(Ui.IconButton(Glyph.Reset, ResetMaterial, "모두 되돌리기: 이 머티리얼에서 바꾼 값을 모두 원본으로"));
            _tabs.AddAction(Ui.IconButton(Glyph.Package, () => OpenAssets?.Invoke(), "머티리얼·에셋 관리: 머티리얼·텍스처·메시를 한눈에 보는 창"));
            Children.Add(_tabs);

            // 색 조화 탭 (2026-10-24): 섹션 탭 뒤에 늘 하나. 머티리얼을 바꿔도 같은 패널이라 고른 색·이미지가 남는다
            _palette = new ColorHarmonyPanel(_ctx.Status)
            {
                ColorProps = () => Doc == null || _mat == null ? new List<(string, string)>()
                    : PropsFor(_mat, out _).Where(p => p.Type == "Color" && !p.Hidden).Select(p => (p.Name, LilLocalization.Describe(p.Description).Label)).ToList(),
                ReadProp = name => Doc == null || _mat == null ? null : Doc.Get(Addr.MatColor(_mat, name)).ToFloatArray(),
                WriteProp = (name, label, rgb) =>
                {
                    if (Doc == null || _mat == null) return;
                    var a = Addr.MatColor(_mat, name);
                    var cur = Doc.Get(a).ToFloatArray();
                    Doc.Set(a, JNode.FloatArray(new[] { rgb[0], rgb[1], rgb[2], cur.Length > 3 ? cur[3] : 1f }), "색 조화: " + label);
                },
                MainTextureFile = () => Doc == null || _mat == null ? null : Doc.Texture(Doc.Get(Addr.MatTex(_mat, "_MainTex")).AsStringOrNull)?.File,
            };

            _matPick.SelectionChanged += (_, __) =>
            {
                if (_suppress) return;
                // 고르기 처리(FillPicker가 이 콤보의 목록을 다시 채움)는 선택 바뀜 알림이 끝난 뒤에: 알림 도중 목록을 비우면 Avalonia 선택 모델이 범위 밖 오류로 튕긴다
                if (_matPick.SelectedItem is ComboBoxItem it) { var guid = (string)it.Tag!; Dispatcher.UIThread.Post(() => _ctx.Select(_ctx.SelectedRenderer, guid)); }
            };
            _mode.SelectionChanged += (_, __) => ApplyMode();
            _transMode.SelectionChanged += (_, __) => ApplyMode();
            var searchLater = Ui.Debounce(Rebuild); // 입력이 멈추면 한 번만 다시 만든다
            _search.TextChanged += (_, __) => searchLater();
            _showHidden.Click += (_, __) => Rebuild();
            _onlyModified.Click += (_, __) => Rebuild();
            // 안 보이면 다시 그리기를 미룬다 (블렌드쉐이프 페이지에서 메시를 고를 때 0.3초 멈추지 않게 — Later)
            _ctx.SelectionChanged += () => { FillPicker(); if (_ctx.SelectedMaterial != _mat) { _mat = _ctx.SelectedMaterial; Later.Run(this, Rebuild); } };
            _ctx.DocumentOpened += () =>
            {
                if (_ctx.Doc != null)
                {
                    _ctx.Doc.Changed += OnChanged;
                    _ctx.Doc.SchemasChanged += () => Dispatcher.UIThread.Post(() => Later.Run(this, RebuildIfSchemaChanged));
                }
                _mat = _ctx.SelectedMaterial; FillPicker(); Rebuild();
            };
        }

        Document? Doc => _ctx.Doc;

        void ToggleFind()
        {
            bool show = !_find.IsVisible;
            if (!show) _search.Text = "";   // 다시 누르면 찾는 말을 지우고 닫는다
            _find.IsVisible = show;
            if (show) Dispatcher.UIThread.Post(() => _search.Focus());
        }

        void FillPicker()
        {
            _suppress = true;
            try
            {
                _matPick.Items.Clear();
                if (Doc == null) return;
                var r = _ctx.SelectedRenderer == null ? null : Doc.Original.FindRenderer(_ctx.SelectedRenderer);
                IEnumerable<(string guid, string label)> list = r != null
                    ? r.Materials.Select((g, i) => (g, $"슬롯 {i}: {Doc.Original.FindMaterial(g)?.Name ?? "(없음 " + g + ")"}"))
                    : Doc.Original.Materials.Select(m => (m.Guid, m.Name));
                foreach (var (guid, label) in list)
                {
                    var it = new ComboBoxItem { Content = label + (Doc.IsMaterialModified(guid) ? "  •" : ""), Tag = guid };
                    _matPick.Items.Add(it);
                    if (guid == _ctx.SelectedMaterial) _matPick.SelectedItem = it;
                }
            }
            finally { _suppress = false; }
        }

        void RebuildIfSchemaChanged()
        {
            if (_mat == null || Doc == null) return;
            var s = Doc.Schema(_mat);
            if ((s?.Shader ?? "") != _builtForShader || !_built) Rebuild();
            else RefreshHeader();
        }

        List<ShaderPropInfo> PropsFor(string mat, out bool fromRenderer)
        {
            var s = Doc!.Schema(mat);
            fromRenderer = s != null;
            if (s != null && s.Supported) return s.Props;
            var m = Doc.Original.FindMaterial(mat);
            var list = new List<ShaderPropInfo>();
            if (m == null) return list;
            foreach (var t in m.TexEnvs.Keys) list.Add(new ShaderPropInfo { Name = t, Description = t, Type = "Texture" });
            foreach (var c in m.Colors.Keys) list.Add(new ShaderPropInfo { Name = c, Description = c, Type = "Color" });
            foreach (var f in m.Floats.Keys) list.Add(new ShaderPropInfo { Name = f, Description = f, Type = "Float" });
            return list;
        }

        void Rebuild()
        {
            _props.Children.Clear(); _refreshers.Clear(); _built = false; _stamp++;
            _tabs.BeginUpdate();
            if (Doc == null || _mat == null) { _shaderName.Text = "메시와 머티리얼을 고르세요"; _shaderPath.Text = ""; _shaderNote.Content = null; foreach (var r in _modeRows) r.IsVisible = false; _shaderRow.IsVisible = true; _tabs.MoreNote = ""; AddPaletteTab(); _tabs.EndUpdate(); return; }
            // 렌더러가 장면을 불러오는 중이면 셰이더 정보(스키마)가 곧 온다 — 원본 값으로 그렸다가(큰 시험 장면에서 0.8초) 곧 다시 그리지 않게 기다린다.
            // 스키마가 오면 RebuildIfSchemaChanged가 그리고, 장면 불러오기 단계 안에서 예열까지 한다 (MainWindow.FinishLoad)
            if (!Doc.HasSchemas && _ctx.Render?.IsConnected == true)
            {
                _shaderName.Text = "셰이더 정보를 불러오는 중…"; _shaderPath.Text = ""; _shaderNote.Content = null;
                foreach (var r in _modeRows) r.IsVisible = false;
                _tabs.MoreNote = "";
                _tabs.Add("none", "프로퍼티", Ui.Wrap("장면을 불러오면 프로퍼티가 나와요.", Ui.KText2, 13));
                AddPaletteTab();
                _tabs.EndUpdate();
                return;
            }
            _built = true;
            var props = PropsFor(_mat, out bool fromRenderer);
            var shader = Doc.Get(Addr.MatShader(_mat)).AsString;
            _builtForShader = Doc.Schema(_mat)?.Shader ?? "";
            bool lil = LilToonRules.IsLilToon(shader);
            RefreshHeader();

            var q = (_search.Text ?? "").Trim();
            var bySection = new Dictionary<string, List<ShaderPropInfo>>();
            foreach (var p in props)
            {
                if (p.Hidden && _showHidden.IsChecked != true) continue;
                // lilToon의 "이게 보이면 스크립트가 고장" 안내용 가짜 프로퍼티 (2026-11-01): Other 탭에 숫자 칸과 함께 나와 고장처럼 보였다
                if (p.Name == "_DummyProperty") continue;
                var label = LilLocalization.Describe(p.Description).Label;
                if (q.Length > 0 && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_onlyModified.IsChecked == true && !AddrsFor(p).Any(Doc.IsModified)) continue;
                var sec = PropertySections.Classify(p.Name, lil);
                if (!bySection.TryGetValue(sec, out var l)) bySection[sec] = l = new List<ShaderPropInfo>();
                l.Add(p);
            }
            if (fromRenderer && Doc.Schema(_mat)?.Supported == false)
                _props.Children.Add(Ui.Notice("미리보기에서 지원하지 않는 셰이더라, 바꾼 값은 저장되지만 화면에는 보이지 않아요."));
            else if (!fromRenderer)
                _props.Children.Add(Ui.Notice("미리보기가 켜지면 슬라이더 범위와 선택 목록이 나와요.", Glyph.Info));

            var order = lil ? PropertySections.Order : new[] { "Properties" };
            // 외곽선은 켜기 프로퍼티 대신 셰이더 종류(위 "외곽선" 스위치)로 켜고 끈다
            bool outlineOn = LilToonRules.Detect(shader, Doc.Get(Addr.MatFloat(_mat, "_TransparentMode")).AsFloat)?.Outline ?? true;
            int expertOnly = 0;
            foreach (var sec in order.Concat(bySection.Keys.Except(order)))
            {
                if (!bySection.TryGetValue(sec, out var list)) continue;
                var toggleProp = lil ? PropertySections.ToggleProperty(sec) : null;
                bool hasToggle = toggleProp != null && props.Any(p => p.Name == toggleProp);
                // 본문: 넓으면 2~3열. 잘 안 쓰는 항목(UV 애니메이션·그라데이션 맵 등, PropertySections.IsRarelyUsed)은 아래 "더 보기"로 접는다 —
                // 바꾼 값이 있거나 찾는 중이면 보통 줄에. 섹션 전체가 잘 안 쓰는 항목이면 접지 않는다
                var common = new List<ShaderPropInfo>(); var rare = new List<ShaderPropInfo>();
                bool foldRare = lil && q.Length == 0 && _onlyModified.IsChecked != true;
                // 켜기 프로퍼티는 탭 맨 위 "… 쓰기" 줄이 맡으므로 본문에는 다시 두지 않는다
                foreach (var p in list)
                {
                    if (hasToggle && p.Name == toggleProp) continue;
                    (foldRare && PropertySections.IsRarelyUsed(p.Name) && !AddrsFor(p).Any(Doc.IsModified) ? rare : common).Add(p);
                }
                if (common.Count == 0) { common = rare; rare = new List<ShaderPropInfo>(); }
                var key = "sec:" + sec;
                bool on = true;
                var ta = hasToggle ? Addr.MatFloat(_mat, toggleProp!) : default;
                if (hasToggle)
                {
                    on = Doc.Get(ta).AsFloat != 0;
                    AddRefresh(ta, () => _tabs.SetState(key, Doc!.Get(ta).AsFloat != 0));   // 내용을 아직 만들지 않았어도 탭 상태는 따라간다
                }
                // 섹션 내용은 탭이 처음 보일 때·예열할 때·쉬는 동안 만든다 (SubTabs.AddLazy — 2026-10-27, 불러오기 속도)
                var mat = _mat; int stamp = _stamp; var secName = sec;
                Control Build()
                {
                    // 그 사이 머티리얼이 바뀌었으면 (안 보이는 페이지라 다시 그리기를 미뤘을 때) 곧 다시 그려지므로 비워 둔다
                    if (stamp != _stamp || mat != _mat || Doc == null) return new StackPanel();
                    var added = new List<Action>();
                    _collect = added;
                    try
                    {
                        var body = new StackPanel();
                        var cols = new FlowColumns();
                        foreach (var p in common) cols.Children.Add(MakeRow(p));
                        body.Children.Add(cols);
                        if (rare.Count > 0) body.Children.Add(RareBlock(secName, rare));
                        if (lil && secName == "Main" && q.Length == 0 && _onlyModified.IsChecked != true && props.Any(p => p.Name == "_MainTexHSVG")) { body.Children.Add(BakeRow(mat)); body.Children.Add(MixRow(mat)); }
                        var tab = new StackPanel();
                        if (hasToggle)
                        {
                            // 섹션 켜고 끄기: 탭 맨 위 줄. 바꾸면 설명·탭 상태(꺼짐 글씨 / 다이얼 모드 LED)도 바로 따라간다
                            var sw = new Ui.Switch(v => Doc!.Set(ta, v ? 1 : 0, (v ? "켜기: " : "끄기: ") + secName));
                            ToolTip.SetTip(sw.Control, toggleProp);
                            var desc = new TextBlock();
                            void Show(bool v) { sw.SetQuiet(v); desc.Text = v ? "켜져 있어요" : "꺼져 있어요 · 켜면 아래 값이 화면에 보여요"; _tabs.SetState(key, v); }
                            Show(Doc.Get(ta).AsFloat != 0);
                            AddRefresh(ta, () => Show(Doc!.Get(ta).AsFloat != 0));
                            tab.Children.Add(Ui.Card(Ui.DescRow(SectionLabel(secName) + " 사용", desc, sw.Control)));
                        }
                        tab.Children.Add(Ui.Card(body));
                        return tab;
                    }
                    finally { _collect = null; foreach (var r in added) r(); }   // 새로 만든 줄에 지금 값을 채운다
                }
                int modified = list.Sum(p => AddrsFor(p).Count(Doc.IsModified));
                bool easy = !lil || q.Length > 0 || modified > 0 || EasySections.Contains(sec);
                if (!easy) expertOnly++;
                // 탭 상태: 켜기 프로퍼티가 있으면 그 값, 외곽선은 셰이더 종류, 나머지는 늘 켜짐 (다이얼 모드 LED가 켜진 섹션을 보여 준다)
                bool state = hasToggle ? on : lil && sec == "Outline" ? outlineOn : true;
                // 탭 막대에는 쓰는 섹션만: 전문가 섹션, 그리고 꺼져 있고 바꾼 적 없는 섹션은 "기타" 메뉴에 (찾는 중에는 모두 막대에)
                bool idle = !state && modified == 0;
                _tabs.AddLazy(key, SectionLabel(sec), Build, expert: !easy, badge: modified > 0 ? $"바꿈 {modified}" : "", accentBadge: modified > 0,
                    more: q.Length == 0 && (!easy || idle), state: state);
            }
            // "전문가 모드에서 더 보여요" 안내는 한 줄을 늘 차지해 "기타" 메뉴 아래로 옮겼다 (2026-10-22)
            _tabs.MoreNote = expertOnly > 0 ? $"전문가 모드에서 섹션 {expertOnly}개가 더 보여요." : "";
            if (bySection.Count == 0) _tabs.Add("none", "프로퍼티", Ui.Wrap(q.Length > 0 || _onlyModified.IsChecked == true ? "맞는 프로퍼티가 없어요." : "프로퍼티가 없어요.", Ui.KText2, 13));
            AddPaletteTab();
            _tabs.EndUpdate();
            foreach (var r in _refreshers.Values.ToList()) r();
        }

        void AddPaletteTab()
        {
            _tabs.Add("palette", "색 조화", _palette);
            _palette.MaterialChanged();
        }

        // 자주 보는 lilToon 섹션은 한국어로 (나머지는 lilToon 이름 그대로)
        static readonly Dictionary<string, string> SectionLabels = new Dictionary<string, string>
        {
            ["Main"] = "메인", ["Shadow"] = "그림자", ["Emission"] = "발광", ["Normal Map"] = "노멀 맵", ["Reflection"] = "반사",
            ["MatCap"] = "매트캡", ["Rim Light"] = "림 라이트", ["Outline"] = "외곽선", ["Properties"] = "프로퍼티", ["Other"] = "그 밖",
        };
        static string SectionLabel(string sec) => SectionLabels.TryGetValue(sec, out var l) ? l : sec;

        /// <summary>잘 안 쓰는 항목: 섹션 아래 "잘 안 쓰는 항목 N개" 단추로 펼치고 접는다 (펼친 섹션은 이번 실행 동안 기억).</summary>
        Control RareBlock(string sec, List<ShaderPropInfo> rare)
        {
            var p = new StackPanel();
            var cols = new FlowColumns { IsVisible = RareOpen.Contains(sec) };
            foreach (var r in rare) cols.Children.Add(MakeRow(r));
            var label = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            var chevron = Ui.Icon(Glyph.ChevronDown, 9, Ui.KText2);
            void Show() { label.Text = cols.IsVisible ? "잘 안 쓰는 항목 접기" : $"잘 안 쓰는 항목 {rare.Count}개 더 보기"; chevron.Text = cols.IsVisible ? Glyph.ChevronUp : Glyph.ChevronDown; }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(label); content.Children.Add(chevron);
            var btn = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 8), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(btn, "UV 애니메이션·그라데이션 맵처럼 바꾸는 일이 드문 항목 (바꾼 값이 있으면 위에 보여요)");
            btn.Click += (_, __) =>
            {
                cols.IsVisible = !cols.IsVisible;
                if (cols.IsVisible) RareOpen.Add(sec); else RareOpen.Remove(sec);
                Show();
            };
            Show();
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(Ui.KDivider);
            p.Children.Add(line); p.Children.Add(btn); p.Children.Add(cols);
            return p;
        }


        void RefreshHeader()
        {
            if (Doc == null || _mat == null) return;
            var m = Doc.Original.FindMaterial(_mat);
            var s = Doc.Schema(_mat);
            var shader = Doc.Get(Addr.MatShader(_mat)).AsString;
            var origShader = Doc.GetOriginal(Addr.MatShader(_mat)).AsString;
            _shaderPath.Text = m?.Path ?? _mat;
            ToolTip.SetTip(_shaderName, (shader.Length > 0 ? "셰이더: " + shader + "\n" : "") + "머티리얼 파일: " + (m?.Path ?? _mat));
            // lilToon은 모드마다 셰이더가 따로라 "Hidden/lilToonOutline" 같은 내부 이름이 보였다. 모드는 아래 줄이 보여 주니 이름은 lilToon으로
            var lilState = LilToonRules.Detect(shader, Doc.Get(Addr.MatFloat(_mat, "_TransparentMode")).AsFloat);
            _shaderName.Text = shader.Length == 0 ? "셰이더 정보 없음 (미리보기 꺼짐)"
                : lilState != null ? lilState.Family switch { LilFamily.Multi => "lilToon Multi", LilFamily.Lite => "lilToon Lite", LilFamily.Tessellation => "lilToon (테셀레이션)", _ => "lilToon" }
                : shader;
            if (s != null && !s.Supported) _shaderNote.Content = Ui.Notice($"미리보기에서 지원하지 않는 셰이더예요 (GUID {m?.ShaderGuid}). 표시만 대신하고 원본 값은 그대로 보존돼요.");
            else if (shader != origShader) _shaderNote.Content = Ui.Notice("셰이더를 변경했습니다. 원본: " + origShader, Glyph.Info);
            else _shaderNote.Content = null;

            var state = LilToonRules.Detect(shader, Doc.Get(Addr.MatFloat(_mat, "_TransparentMode")).AsFloat);
            foreach (var r in _modeRows) r.IsVisible = state != null;
            _shaderRow.IsVisible = state == null;
            ToolTip.SetTip(_modeName, "셰이더: " + _shaderName.Text + (shader.Length > 0 ? " (" + shader + ")" : ""));
            if (state != null)
            {
                _suppress = true;
                _mode.SelectedIndex = (int)state.Mode;
                _transMode.SelectedIndex = (int)state.Transparent;
                _transMode.IsEnabled = state.Mode == LilRenderingMode.Transparent && state.Family != LilFamily.Multi;
                _outline.SetQuiet(state.Outline);
                _outline.Control.IsEnabled = state.Mode <= LilRenderingMode.Transparent;
                _suppress = false;
            }
            FillPicker();
        }

        IEnumerable<Addr> AddrsFor(ShaderPropInfo p)
        {
            switch (p.Type)
            {
                case "Color": yield return Addr.MatColor(_mat!, p.Name); break;
                case "Vector": yield return Addr.MatVector(_mat!, p.Name); break;
                case "Texture": yield return Addr.MatTex(_mat!, p.Name); if (!p.NoScaleOffset) yield return Addr.MatTexST(_mat!, p.Name); break;
                default: yield return Addr.MatFloat(_mat!, p.Name); break;
            }
        }

        Control MakeRow(ShaderPropInfo p)
        {
            var (label, options) = LilLocalization.Describe(p.Description);
            string[]? components = null;
            if (p.Type == "Vector" && options.Length > 0) { components = new[] { label }.Concat(options).ToArray(); label = string.Join(" · ", components.Select(Texts.T)); }   // 이름마다 따로 바꿔 잇는다 (2026-11-12: 이은 글은 표로 못 찾아 "Size · 오프셋"처럼 섞였다)
            var name = Ui.Text(label);
            var tip = p.Name + (p.Attributes.Count > 0 ? "\n[" + string.Join("][", p.Attributes) + "]" : "");
            var addrs = (p.Type == "Texture" ? new[] { Addr.MatTex(_mat!, p.Name) } : AddrsFor(p)).ToList();
            var reset = Ui.IconButton(Glyph.Reset, () => { using (Doc!.Group("되돌리기: " + label)) foreach (var a in addrs) Doc.Reset(a); }, "원본 값으로 되돌리기");
            Decorate(name, reset, addrs, tip);
            if (p.Type == "Vector")
            {
                // 칸마다 따로 (위에 성분 이름). 성분 이름이 있으면 줄 이름이 길어 이름은 위 한 줄, 칸은 아래 한 줄을 다 쓴다
                var caps = components != null ? components.Take(4).ToArray() : new[] { "X", "Y", "Z", "W" };
                if (caps.Length < 4) caps = caps.Concat(new[] { "X", "Y", "Z", "W" }.Skip(caps.Length)).ToArray();
                var cells = VectorEditor(Addr.MatVector(_mat!, p.Name), caps, label);
                return components != null ? Ui.BlockRow(name, reset, cells) : Ui.PropRow(name, cells, reset);
            }
            var g = Ui.PropRow(name, MakeEditor(p, label, options, components, name), reset);
            if (p.Type != "Texture" || p.NoScaleOffset) return g;

            // 텍스처의 타일링·오프셋은 바로 아래 줄
            var st = Addr.MatTexST(_mat!, p.Name);
            var stName = Ui.Text("타일링 · 오프셋", 13, FontWeight.Normal, Ui.KText2);
            var stReset = Ui.IconButton(Glyph.Reset, () => Doc!.Reset(st), "원본 값으로 되돌리기");
            var stRow = Ui.PropRow(stName, VectorEditor(st, new[] { "타일 X", "타일 Y", "위치 X", "위치 Y" }, label + " 타일링"), stReset);
            stName.Margin = new Thickness(14, 0, 8, 0);
            Decorate(stName, stReset, new List<Addr> { st }, "Tiling X, Tiling Y, Offset X, Offset Y");
            var both = new StackPanel();
            both.Children.Add(g); both.Children.Add(stRow);
            return both;
        }

        void Decorate(TextBlock name, Button reset, List<Addr> addrs, string tip)
        {
            void RefreshDecor()
            {
                bool mod = addrs.Any(Doc!.IsModified);
                Ui.MarkModified(name, mod);
                reset.IsVisible = mod;
                ToolTip.SetTip(name, mod ? tip + "\n원본: " + string.Join(" / ", addrs.Select(a => Doc.GetOriginal(a).ToJson())) : tip);
            }
            foreach (var a in addrs) AddRefresh(a, RefreshDecor);
        }

        TextBox ListCell(Addr a, int count, string label, string tip, Func<float[], float[]>? clamp = null)
        {
            TextBox box = null!;
            void Refresh() { if (!box.IsFocused) box.Text = Ui.FormatList(Doc!.Get(a).ToFloatArray(), count); }
            box = Ui.Cell(s =>
            {
                var cur = Doc!.Get(a).ToFloatArray();
                if (!Ui.TryParseList(s, count, cur, out var v)) { Refresh(); return; }
                if (clamp != null) v = clamp(v);
                if (!v.SequenceEqual(cur)) Doc.Set(a, JNode.FloatArray(v), label);
                else Refresh();
            }, Refresh);
            ToolTip.SetTip(box, tip);
            AddRefresh(a, Refresh);
            return box;
        }

        /// <summary>벡터·타일링: 칸 네 개 (칸 위 이름을 끌어도 바뀐다).</summary>
        Control VectorEditor(Addr a, string[] captions, string label)
        {
            var cells = new Ui.VectorCells(captions, () => Doc!.Get(a).ToFloatArray(), v => Doc!.Preview(a, JNode.FloatArray(v)), v => Doc!.Set(a, JNode.FloatArray(v), label));
            AddRefresh(a, cells.Refresh);
            return cells.Control;
        }

        Control MakeEditor(ShaderPropInfo p, string label, string[] options, string[]? components, TextBlock name)
        {
            var mat = _mat!;
            switch (p.Type)
            {
                case "Color": return ColorEditor(Addr.MatColor(mat, p.Name), label, p.HDR);
                case "Texture": return TextureEditor(p, mat, label);
            }
            var a = Addr.MatFloat(mat, p.Name);
            var toggleAttr = p.Attributes.FirstOrDefault(x => x == "lilToggle" || x == "lilToggleLeft" || x == "ToggleUI" || x == "MaterialToggle" || x.StartsWith("Toggle") || x.StartsWith("MaterialToggle"));
            if (toggleAttr != null)
            {
                var kw = KeywordOfToggle(toggleAttr, p.Name);
                var sw = new Ui.Switch(on =>
                {
                    using (Doc!.Group(label))
                    {
                        Doc.Set(a, on ? 1 : 0, label);
                        if (kw != null) Doc.Set(Addr.MatKeyword(mat, kw), on, label);
                    }
                });
                AddRefresh(a, () => sw.SetQuiet(Doc!.Get(a).AsFloat != 0));
                return sw.Control;
            }
            var en = EnumOptions(p, options);
            if (en != null)
            {
                var combo = Ui.CellCombo(new ComboBox());
                foreach (var (n, v) in en) combo.Items.Add(new ComboBoxItem { Content = n, Tag = v });
                combo.SelectionChanged += (_, __) =>
                {
                    if (_suppress || combo.SelectedItem is not ComboBoxItem it) return;
                    Doc!.Set(a, (double)it.Tag!, label);
                };
                AddRefresh(a, () =>
                {
                    var v = Doc!.Get(a).AsDouble;
                    _suppress = true;
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag! - v) < 1e-4);
                    _suppress = false;
                });
                return combo;
            }
            // 숫자: 범위(Range)가 있는 값만 슬라이더
            bool isInt = p.Type == "Int" || p.Attributes.Contains("IntRange");
            TextBox nb = null!;
            void RefreshBox() { if (!nb.IsFocused) nb.Text = Ui.F(Doc!.Get(a).AsDouble); }
            nb = Ui.NumberCell(v => { v = isInt ? Math.Round(v) : v; if (v != Doc!.Get(a).AsDouble) Doc.Set(a, v, label); else RefreshBox(); }, RefreshBox);
            AddRefresh(a, RefreshBox);
            // 이름을 좌우로 끌어 바꾸기: 범위가 있으면 범위의 1/300씩, 없으면 0.01씩 (정수는 10px에 1)
            bool ranged = p.Type == "Range" && p.Max > p.Min;
            Ui.Scrub(name, () => Doc!.Get(a).AsDouble, v => { nb.Text = Ui.F(v); Doc!.Preview(a, v); }, v => Doc!.Set(a, v, label),
                isInt ? 0.1 : ranged ? (p.Max - p.Min) / 300 : 0.01, ranged ? p.Min : double.NegativeInfinity, ranged ? p.Max : double.PositiveInfinity, isInt);
            if (p.Type != "Range") return nb;
            var sl = new Ui.EditSlider(p.Min, p.Max, v => Doc!.Preview(a, isInt ? Math.Round(v) : v), v => Doc!.Set(a, isInt ? Math.Round(v) : Math.Round(v, 4), label));
            if (isInt) { sl.Slider.IsSnapToTickEnabled = true; sl.Slider.TickFrequency = 1; }
            ToolTip.SetTip(sl.Slider, $"{Ui.F(p.Min)} ~ {Ui.F(p.Max)}");
            AddRefresh(a, () => sl.SetQuiet(Doc!.Get(a).AsDouble));
            return Ui.NumberWithSlider(nb, sl.Slider);
        }

        /// <summary>색: 견본(누르면 R·G·B·A 슬라이더 창) + 숫자 칸.</summary>
        Control ColorEditor(Addr a, string label, bool hdr)
        {
            var row = new DockPanel();
            var swatch = new Border { Width = 40, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) }.Stroke(Ui.KStroke);
            var pickBtn = new Button { Content = swatch, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(pickBtn, "눌러서 색 조절");
            pickBtn.Flyout = ColorFlyout(a, label, hdr);
            DockPanel.SetDock(pickBtn, Dock.Left);
            row.Children.Add(pickBtn);
            Func<float[], float[]> clamp = c => c.Select(x => hdr ? Math.Max(0, x) : Math.Clamp(x, 0, 1)).ToArray();
            row.Children.Add(ListCell(a, 4, label, hdr ? "R, G, B, A (HDR: 1보다 커도 돼요)" : "R, G, B, A (0~1)", clamp));
            AddRefresh(a, () =>
            {
                var c = Doc!.Get(a).ToFloatArray();
                if (c.Length < 4) Array.Resize(ref c, 4);
                var max = Math.Max(1f, c.Take(3).Max());
                swatch.Background = new SolidColorBrush(Ui.ToColor(new[] { c[0] / max, c[1] / max, c[2] / max, 1f }));
            });
            return row;
        }

        Flyout ColorFlyout(Addr a, string label, bool hdr)
        {
            var panel = new StackPanel { Width = 270, Spacing = 2 };
            panel.Children.Add(Ui.Text(label, 14, FontWeight.SemiBold));
            var big = new Border { Height = 44, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 8), BorderThickness = new Thickness(1) }.Stroke(Ui.KStroke);
            panel.Children.Add(big);
            var wheel = new HarmonyWheel { Width = 210, Height = 210, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(Ui.Text("색상환", 12, FontWeight.Normal, Ui.KText2));
            panel.Children.Add(wheel);
            float[] cur = new float[4];
            var sliders = new Ui.EditSlider[4];
            var boxes = new TextBlock[4];
            int wheelDrag = 0;
            float[] wheelStart = Array.Empty<float>();
            Hsv WheelValue()
            {
                var scale = hdr ? Math.Max(1f, cur.Take(3).Max()) : 1f;
                return ColorHarmony.FromRgb(cur[0] / scale, cur[1] / scale, cur[2] / scale);
            }
            void SyncWheel()
            {
                var c = WheelValue();
                wheel.Set = new[] { c }; wheel.Sel = 0; wheel.SetValue(c.V); wheel.InvalidateVisual();
            }
            void Paint()
            {
                var max = Math.Max(1f, cur.Take(3).Max());
                big.Background = new SolidColorBrush(Ui.ToColor(new[] { cur[0] / max, cur[1] / max, cur[2] / max, 1f }));
                for (int i = 0; i < 4; i++) boxes[i].Text = Ui.F(Math.Round(cur[i], 3));
                SyncWheel();
            }
            void WheelApply(Point p)
            {
                var old = WheelValue();
                var (h, s) = wheel.HueSatAt(p);
                var rgb = ColorHarmony.ToRgb(new Hsv(h, s, old.V));
                var scale = hdr ? Math.Max(1f, cur.Take(3).Max()) : 1f;
                cur[0] = (float)(rgb.R * scale); cur[1] = (float)(rgb.G * scale); cur[2] = (float)(rgb.B * scale);
                Paint();
                Doc!.Preview(a, JNode.FloatArray(cur));
            }
            string[] names = { "빨강", "초록", "파랑", "불투명도" };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                double max = hdr && i < 3 ? 4 : 1;
                sliders[i] = new Ui.EditSlider(0, max,
                    v => { cur[idx] = (float)v; Paint(); Doc!.Preview(a, JNode.FloatArray(cur)); },
                    v => { cur[idx] = (float)Math.Round(v, 4); Paint(); Doc!.Set(a, JNode.FloatArray((float[])cur.Clone()), label); });
                boxes[i] = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
                boxes[i].Width = 44; boxes[i].TextAlignment = TextAlignment.Right;
                var g = Ui.Columns(Ui.Px(64), Ui.Star(), GridLength.Auto);
                g.Children.Add(Ui.Text(names[i], 13).At(0)); g.Children.Add(sliders[i].Slider.At(1)); g.Children.Add(boxes[i].At(2));
                g.MinHeight = 36;
                panel.Children.Add(g);
            }
            wheel.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(wheel).Properties.IsLeftButtonPressed) return;
                wheelDrag = 1; wheelStart = (float[])cur.Clone(); e.Pointer.Capture(wheel); WheelApply(e.GetPosition(wheel));
            };
            wheel.PointerMoved += (_, e) => { if (wheelDrag != 0) WheelApply(e.GetPosition(wheel)); };
            wheel.PointerReleased += (_, e) =>
            {
                if (wheelDrag == 0) return;
                wheelDrag = 0; e.Pointer.Capture(null);
                if (!wheelStart.SequenceEqual(cur)) Doc!.Set(a, JNode.FloatArray((float[])cur.Clone()), label);
            };
            wheel.PointerCaptureLost += (_, __) =>
            {
                if (wheelDrag == 0) return;
                wheelDrag = 0;
                if (!wheelStart.SequenceEqual(cur)) Doc!.Set(a, JNode.FloatArray((float[])cur.Clone()), label);
            };
            var f = new Flyout { Content = panel };
            f.Opening += (_, __) =>
            {
                // Unity 화면(네이티브 창)은 팝업을 덮으므로 미리보기 반대쪽으로 펼친다:
                // 작업 창이 왼쪽이면 견본에서 왼쪽 아래로, 오른쪽이면 오른쪽 아래로
                bool paneOnRight = f.Target is Visual t && TopLevel.GetTopLevel(t) is TopLevel top
                    && t.TranslatePoint(default, top) is Point pt && pt.X > top.Bounds.Width / 2;
                f.Placement = paneOnRight ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedRight;
                var c = Doc!.Get(a).ToFloatArray();
                cur = new float[4]; for (int i = 0; i < 4; i++) cur[i] = i < c.Length ? c[i] : 1;
                for (int i = 0; i < 4; i++) sliders[i].SetQuiet(cur[i]);
                Paint();
            };
            return f;
        }

        Control TextureEditor(ShaderPropInfo p, string mat, string label)
        {
            var a = Addr.MatTex(mat, p.Name);
            var combo = Ui.CellCombo(new ComboBox());
            var compatible = new CheckBox { Content = "호환만", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            ComboBoxItem Item(string? guid)
            {
                if (guid == null) return new ComboBoxItem { Content = "(없음)", Tag = null };
                var t = Doc!.Texture(guid);
                if (t == null) return new ComboBoxItem { Content = "(파일 없음) " + guid, Tag = guid }.Fg(Ui.KCaution);
                var it = new ComboBoxItem { Content = System.IO.Path.GetFileName(t.Path), Tag = guid };
                ToolTip.SetTip(it, t.Path);
                return it;
            }
            void Select(string? guid)
            {
                _suppress = true;
                var it = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == guid);
                if (it == null) combo.Items.Add(it = Item(guid));
                combo.SelectedItem = it;
                _suppress = false;
            }
            // 텍스처 전체 목록은 처음 펼치려 할 때 채운다 (Poiyomi는 텍스처 프로퍼티가 수백 개라 줄마다 전부 넣으면 페이지가 무겁다)
            bool filled = false;
            void Fill()
            {
                if (filled || Doc == null) return;
                filled = true;
                _suppress = true;
                combo.Items.Clear();
                combo.Items.Add(Item(null));
                IEnumerable<IrTexture> textures = Doc.Original.Textures.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase);
                if (compatible.IsChecked == true) { var ok = CompatibleTextures(mat); textures = textures.Where(ok).ToList(); }
                foreach (var t in textures) combo.Items.Add(Item(t.Guid));
                foreach (var k in Doc.Overrides.Keys.Where(k => k.Kind == AddrKind.TexAsset)) combo.Items.Add(Item(k.Target));   // 구운 텍스처
                _suppress = false;
                Select(Doc.Get(a).AsStringOrNull);
            }
            compatible.IsCheckedChanged += (_, __) => { filled = false; Fill(); };
            combo.AddHandler(InputElement.PointerPressedEvent, (_, __) => Fill(), RoutingStrategies.Tunnel);
            combo.GotFocus += (_, __) => Fill();
            combo.SelectionChanged += (_, __) =>
            {
                if (_suppress || combo.SelectedItem is not ComboBoxItem it) return;
                Doc!.Set(a, JNode.From((string?)it.Tag), label);
            };
            AddRefresh(a, () => Select(Doc!.Get(a).AsStringOrNull));
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(compatible, Dock.Left);
            row.Children.Add(compatible);
            if (p.TexDim == "2D")
            {
                var mix = Ui.IconButton(Glyph.Add, () => OpenMix(mat, p.Name, label), "텍스처 합치기: 이 텍스처 위에 다른 이미지를 올려요");
                mix.Margin = new Thickness(4, 0, 0, 0);
                DockPanel.SetDock(mix, Dock.Right);
                row.Children.Add(mix);
            }
            row.Children.Add(combo);
            ToolTip.SetTip(compatible, "이 머티리얼과 같은 폴더 묶음에 있거나, 파일 이름에 머티리얼·메시·아이템 이름이 든 텍스처만 보여 줘요. 다른 텍스처가 필요하면 끄세요.");
            return row;
        }

        /// <summary>
        /// "호환만" 거르개: 머티리얼과 같은 폴더 묶음(Materials 폴더면 그 위 폴더)에 있거나, 파일 이름에 머티리얼·렌더러·아이템 이름의 낱말이 든 텍스처.
        /// 경로 전체로 비교하면 "assets"처럼 모든 텍스처에 있는 낱말에 다 걸려서, 이름 비교는 파일 이름에만 한다. 목록을 채울 때 한 번 만든다.
        /// </summary>
        Func<IrTexture, bool> CompatibleTextures(string mat)
        {
            var material = Doc?.Original.FindMaterial(mat);
            if (Doc == null || material == null) return _ => true;
            var scene = Doc.Original;
            var names = new HashSet<string>();
            AddWords(names, material.Name);
            foreach (var r in scene.Renderers.Where(r => r.Materials.Contains(mat)))
            {
                if (r.Node >= 0 && r.Node < scene.Nodes.Count) AddWords(names, scene.Nodes[r.Node].Name);
                foreach (var o in scene.Outfits)
                    if (Under(scene, r.Node, o.Node)) AddWords(names, o.Name);
            }
            var home = HomeDir(material.Path);
            return t =>
            {
                if (home.Length > 0 && NormalizeDir(t.Path).StartsWith(home, StringComparison.Ordinal)) return true;
                var file = System.IO.Path.GetFileNameWithoutExtension(t.Path).ToLowerInvariant();
                return names.Any(x => file.Contains(x, StringComparison.Ordinal));
            };
        }

        static readonly HashSet<string> CommonWords = new HashSet<string> { "material", "materials", "texture", "textures", "main", "base", "color", "colour", "tex", "mat", "mesh", "body" };

        static void AddWords(HashSet<string> into, string text)
        {
            foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9가-힣ぁ-んァ-ヶー一-龯]+"))
            {
                var s = m.Value;
                if (s.Length >= 3 && !CommonWords.Contains(s) && !s.All(char.IsDigit)) into.Add(s);
            }
        }

        static string NormalizeDir(string path)
        {
            var p = path.Replace('\\', '/');
            var i = p.LastIndexOf('/');
            return i < 0 ? "" : p.Substring(0, i).ToLowerInvariant() + "/";
        }

        /// 머티리얼이 든 폴더 묶음: Materials·Mat 폴더면 그 위 (보통 옆에 Textures 폴더가 있다). "assets/" 하나뿐이면 너무 넓어 쓰지 않는다
        static string HomeDir(string materialPath)
        {
            var dir = NormalizeDir(materialPath);
            var parts = dir.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 0 && (parts[^1] is "material" or "materials" or "mat" or "mats")) parts.RemoveAt(parts.Count - 1);
            return parts.Count >= 2 ? string.Join("/", parts) + "/" : "";
        }

        static bool Under(IrScene scene, int node, int ancestor)
        {
            for (int x = node; x >= 0 && x < scene.Nodes.Count; x = scene.Nodes[x].Parent) if (x == ancestor) return true;
            return false;
        }

        void AddRefresh(Addr a, Action r) { _refreshers[a] = (_refreshers.TryGetValue(a, out var prev) ? prev : null) + r; _collect?.Add(r); }
        List<Action>? _collect;   // 나중에 만드는 섹션(AddLazy)의 새 줄: 만든 뒤 지금 값으로 한 번 채운다
        int _stamp;               // Rebuild마다 하나씩 — 예전 Rebuild의 섹션 만들기를 알아본다

        static string? KeywordOfToggle(string attr, string prop)
        {
            var m = Regex.Match(attr, @"^(Material)?Toggle\((\w+)\)$");
            if (m.Success) return m.Groups[2].Value;
            if (attr == "Toggle" || attr == "MaterialToggle") return prop.TrimStart('_').ToUpperInvariant() + "_ON";
            return null;
        }

        static readonly Dictionary<string, (string, double)[]> UnityEnums = new Dictionary<string, (string, double)[]>
        {
            ["UnityEngine.Rendering.CullMode"] = new[] { ("Off", 0d), ("Front", 1d), ("Back", 2d) },
            ["UnityEngine.Rendering.BlendMode"] = new[] { ("Zero", 0d), ("One", 1d), ("DstColor", 2d), ("SrcColor", 3d), ("OneMinusDstColor", 4d), ("SrcAlpha", 5d), ("OneMinusSrcColor", 6d), ("DstAlpha", 7d), ("OneMinusDstAlpha", 8d), ("SrcAlphaSaturate", 9d), ("OneMinusSrcAlpha", 10d) },
            ["UnityEngine.Rendering.CompareFunction"] = new[] { ("Disabled", 0d), ("Never", 1d), ("Less", 2d), ("Equal", 3d), ("LessEqual", 4d), ("Greater", 5d), ("NotEqual", 6d), ("GreaterEqual", 7d), ("Always", 8d) },
            ["UnityEngine.Rendering.BlendOp"] = new[] { ("Add", 0d), ("Subtract", 1d), ("ReverseSubtract", 2d), ("Min", 3d), ("Max", 4d) },
            ["UnityEngine.Rendering.StencilOp"] = new[] { ("Keep", 0d), ("Zero", 1d), ("Replace", 2d), ("IncrementSaturate", 3d), ("DecrementSaturate", 4d), ("Invert", 5d), ("IncrementWrap", 6d), ("DecrementWrap", 7d) },
        };

        static List<(string, double)>? EnumOptions(ShaderPropInfo p, string[] descOptions)
        {
            foreach (var attr in p.Attributes)
            {
                var m = Regex.Match(attr, @"^Enum\((.*)\)$");
                if (m.Success)
                {
                    var arg = m.Groups[1].Value.Trim();
                    if (UnityEnums.TryGetValue(arg, out var known)) return known.ToList();
                    var parts = arg.Split(',').Select(s => s.Trim()).ToArray();
                    if (parts.Length >= 2 && parts.Length % 2 == 0)
                    {
                        var r = new List<(string, double)>();
                        for (int i = 0; i < parts.Length; i += 2) if (Ui.TryParse(parts[i + 1], out var v)) r.Add((parts[i], v));
                        return r;
                    }
                    return null;
                }
                var k = Regex.Match(attr, @"^KeywordEnum\((.*)\)$");
                if (k.Success) return k.Groups[1].Value.Split(',').Select((s, i) => (s.Trim(), (double)i)).ToList();
                if ((attr == "lilEnum" || attr == "lilEnumLabel") && descOptions.Length > 0)
                    return descOptions.Select((s, i) => (s, (double)i)).ToList();
            }
            return null;
        }

        void OnChanged(IReadOnlyList<Addr> addrs)
        {
            if (_mat == null) return;
            bool header = false;
            foreach (var a in addrs)
            {
                if (a.Target != _mat || !a.IsMaterial) continue;
                if (a.Kind == AddrKind.MatShader || a.Name == "_TransparentMode") header = true;
                if (_refreshers.TryGetValue(a, out var r)) r();
            }
            if (header) RefreshHeader();
            if (addrs.Any(a => a.IsMaterial)) FillPicker();
        }

        void ApplyMode()
        {
            if (_suppress || Doc == null || _mat == null || _mode.SelectedIndex < 0) return;
            var ok = LilToonRules.Switch(Doc, _mat, (LilRenderingMode)_mode.SelectedIndex, (LilTransparentMode)Math.Max(0, _transMode.SelectedIndex), _outline.Control.IsChecked == true);
            if (ok == false) RefreshHeader();
        }

        void ResetMaterial()
        {
            if (Doc == null || _mat == null) return;
            var m = _mat;
            Doc.ResetWhere(a => a.IsMaterial && a.Target == m, "머티리얼 리셋");
        }

        void Copy()
        {
            if (Doc == null || _mat == null) return;
            var props = PropsFor(_mat, out _);
            _clipboard = props.SelectMany(AddrsFor).Select(a => new KeyValuePair<Addr, JNode>(a, Doc.Get(a))).ToList();
            _clipboardFrom = Doc.Original.FindMaterial(_mat)?.Name ?? _mat;
            _ctx.Status("복사됨 · " + _clipboardFrom);
        }

        void Paste()
        {
            if (Doc == null || _mat == null || _clipboard == null) return;
            var props = new HashSet<string>(PropsFor(_mat, out _).Select(p => p.Name));
            var mat = _mat;
            using (Doc.Group("값 붙여넣기 ← " + _clipboardFrom))
                foreach (var kv in _clipboard)
                {
                    if (kv.Key.Kind == AddrKind.MatShader || !props.Contains(kv.Key.Name)) continue;
                    Doc.Set(new Addr(kv.Key.Kind, mat, kv.Key.Name), kv.Value);
                }
        }
    }
}
