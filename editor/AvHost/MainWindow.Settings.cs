// 설정 창 (2026-10-20): 사이드바 아래 "설정" · Ctrl+, . 여기저기 흩어진 사용자 취향(avhost.json)을 한곳에서 바꾼다.
// 값은 바꾸는 즉시 저장·적용한다 (확인 단추 없음). 같은 값을 다른 곳(미리보기 PhysBone 메뉴, 컨트롤 보드 머리 단추)에서도 바꿀 수 있다.
// 온라인 업데이트의 저장소·자동 확인·지금 확인도 여기서 연다 (사이드바 "업데이트"는 새 버전을 찾았을 때만 보인다).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        Window? _settingsWin;

        void SettingsSetup()
        {
            var (_, _, item) = _side.AddBottomAction(Glyph.Settings, "설정", SettingsDialog);
            ToolTip.SetTip(item, "화면·미리보기·방송·업데이트 설정 (Ctrl+,)");
        }

        void SettingsDialog()
        {
            if (_settingsWin != null) { _settingsWin.Activate(); return; }
            var w = SettingsWindow();
            _settingsWin = w;
            w.Closed += (_, __) => _settingsWin = null;
            w.Show(this);
        }

        /// <summary>설정 창 "테마" 줄: 프리셋 + 바탕·단추·강조 색 (직접 고르기에서 바꾸면 그 값, 프리셋에서 바꾸면 직접 고르기로 넘어간다)</summary>
        StackPanel ThemeRows()
        {
            var rows = new StackPanel();
            var preset = Ui.CellCombo(new ComboBox { ItemsSource = UiTheme.Presets.Select(x => x.Label).ToList() });
            preset.SelectedIndex = Math.Max(0, Array.FindIndex(UiTheme.Presets, x => x.Key == _settings.ThemeKey));
            rows.Children.Add(Ui.PropRow(Ui.Text("프리셋"), preset, null));
            bool quiet = false;
            var cells = new List<(TextBox Box, Border Swatch)>();
            void Show()
            {
                var (_, b, bt, ac) = UiTheme.Resolve(_settings);
                var vals = new[] { b, bt, ac };
                quiet = true;
                for (int i = 0; i < cells.Count; i++)
                {
                    cells[i].Box.Text = vals[i];
                    cells[i].Swatch.Background = UiTheme.TryHex(vals[i], out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                }
                quiet = false;
            }
            void Changed()
            {
                UiTheme.Apply(_settings); _settings.Save(); Show();
            }
            Control ColorRow(string name, string help, Func<string> get, Action<string> set)
            {
                var swatch = new Border { Width = 28, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Stroke(Ui.KStroke);
                TextBox box = null!;
                box = Ui.Cell(v =>
                {
                    if (quiet) return;
                    v = v.Trim();
                    if (v.Length > 0 && !UiTheme.TryHex(v, out _)) { Show(); return; }
                    // 프리셋을 쓰다가 한 색을 바꾸면: 지금 프리셋 색을 그대로 옮겨 놓고 직접 고르기로
                    if (_settings.ThemeKey != "custom")
                    {
                        var (_, b, bt, ac) = UiTheme.Resolve(_settings);
                        _settings.ThemeBase = b; _settings.ThemeButton = bt; _settings.ThemeAccent = ac;
                        _settings.ThemeKey = "custom";
                        preset.SelectedIndex = Array.FindIndex(UiTheme.Presets, x => x.Key == "custom");
                    }
                    set(v.Length > 0 && !v.StartsWith("#") ? "#" + v : v);
                    Changed();
                }, Show);
                box.Watermark = "#RRGGBB (비우면 기본)";
                cells.Add((box, swatch));
                var row = new DockPanel();
                DockPanel.SetDock(swatch, Dock.Left);
                row.Children.Add(swatch); row.Children.Add(box);
                return Ui.HelpRow(name, help, row, wide: true);
            }
            rows.Children.Add(ColorRow("바탕 색", "창 바탕 색이에요. 작업 창·카드 색은 여기서 조금씩 밝게(어두운 바탕) 또는 하얗게(밝은 바탕) 만들어요.", () => _settings.ThemeBase, v => _settings.ThemeBase = v));
            rows.Children.Add(ColorRow("단추 색", "보통 단추의 바탕 색", () => _settings.ThemeButton, v => _settings.ThemeButton = v));
            rows.Children.Add(ColorRow("강조 색", "고른 것·스위치·슬라이더·강조 단추 색 (비우면 Windows 강조색)", () => _settings.ThemeAccent, v => _settings.ThemeAccent = v));
            preset.SelectionChanged += (_, __) =>
            {
                if (preset.SelectedIndex < 0) return;
                var k = UiTheme.Presets[preset.SelectedIndex].Key;
                if (k == _settings.ThemeKey) return;
                if (k == "custom")
                {
                    // 직접 고르기로 바꾸면 지금 보이는 색에서 시작
                    var (_, b, bt, ac) = UiTheme.Resolve(_settings);
                    if (_settings.ThemeBase.Length == 0 && _settings.ThemeButton.Length == 0 && _settings.ThemeAccent.Length == 0) { _settings.ThemeBase = b; _settings.ThemeButton = bt; _settings.ThemeAccent = ac; }
                }
                _settings.ThemeKey = k;
                Changed();
            };
            Show();
            return rows;
        }

        /// <summary>설정 창을 만든다 (띄우지는 않음 — 화면 확인용 찍기도 쓴다).</summary>
        Window SettingsWindow()
        {
            var w = new Window
            {
                Title = "설정", Width = 560, Height = 640, MinWidth = 440, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = Ui.Font, FontSize = 14,
            };
            w.Bg(Ui.KBase);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
            p.Children.Add(Ui.DialogHeader("설정", "바꾸면 바로 저장돼요. 아바타·세션 파일과는 상관없는 이 PC의 설정이에요."));

            Ui.Switch Sw(bool v, Action<bool> set) { var s = new Ui.Switch(set); s.SetQuiet(v); return s; }
            StackPanel Rows(params Control[] rows) { var sp = new StackPanel(); foreach (var r in rows) sp.Children.Add(r); return sp; }

            // ---- 화면 ----
            // 언어 (2026-11-02): 언어 이름은 그 언어로 적어 어느 화면에서도 알아보게
            var langs = new[] { ("", "Windows 언어 따르기"), ("ko", "한국어"), ("en", "English"), ("ja", "日本語") };
            var lang = Ui.CellCombo(new ComboBox { ItemsSource = langs.Select(x => x.Item2).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(langs, x => x.Item1 == _settings.UiLang)) });
            lang.SelectionChanged += (_, __) =>
            {
                var k = langs[Math.Max(0, lang.SelectedIndex)].Item1;
                if (k == _settings.UiLang) return;
                _settings.UiLang = k; _settings.Save();
                Texts.SetLang(k);
            };
            var expert = Sw(_settings.ExpertMode, on => SetExpert(on));
            var right = Sw(_settings.PaneOnRight, on => { if (on != _settings.PaneOnRight) TogglePaneSide(); });
            p.Children.Add(Ui.SectionTitle("화면"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("언어", "화면 글의 언어예요. 바꾸면 바로 바뀌어요 · Language · 言語", lang),
                Ui.HelpRow("전문가 모드", "끄면 쉬운 모드: 자주 쓰는 것만 보여요. 숨긴 세부 설정의 값과 기능은 그대로예요 (사이드바 왼쪽 아래 단추와 같아요)", expert.Control),
                Ui.HelpRow("작업 창을 오른쪽에", "편집 창을 미리보기 오른쪽에 둬요 (제목 표시줄의 \"작업 창\" 단추와 같아요)", right.Control))));

            // ---- 화면 배치 ----
            var layout = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "플랫 (기본, 이어 붙인 판)", "카드 (예전 배치, 둥근 카드)" }, SelectedIndex = _settings.LayoutStyle == "clean" ? 0 : 1 });
            layout.SelectionChanged += (_, __) => { _settings.LayoutStyle = layout.SelectedIndex == 0 ? "clean" : "classic"; _settings.LayoutChosen = true; _settings.Save(); ApplyLayoutStyle(); };
            p.Children.Add(Ui.SectionTitle("화면 배치", "플랫(기본): 왼쪽 아이콘 막대와 판들을 가는 선으로 이어 붙여 화면을 넓게 써요. 카드: 예전처럼 사이드바·작업 창·미리보기가 떨어진 둥근 카드예요. 기능은 같고 언제든 바꿀 수 있어요."));
            p.Children.Add(Ui.Card(Ui.PropRow(Ui.Text("배치"), layout, null)));

            // ---- 슬라이더 모양 · 다이얼 색 ----
            var knob = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "기본 (막대 슬라이더)", "다이얼 모드" }, SelectedIndex = _settings.SliderStyle == "dial" ? 1 : 0 });
            knob.SelectionChanged += (_, __) => { _settings.SliderStyle = knob.SelectedIndex == 1 ? "dial" : "bar"; _settings.Save(); Hardware.Apply(_settings.SliderStyle == "dial", _settings); InvalidateDials(); };
            var look = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "플랫 (기본)", "입체 (스큐어모픽)" }, SelectedIndex = _settings.HardwareLook == "skeuo" ? 1 : 0 });
            look.SelectionChanged += (_, __) => { _settings.HardwareLook = look.SelectedIndex == 1 ? "skeuo" : "flat"; _settings.Save(); Hardware.Apply(_settings.SliderStyle == "dial", _settings); InvalidateDials(); };
            var unify = Sw(_settings.HardwareUnify, on => { _settings.HardwareUnify = on; _settings.Save(); Hardware.Apply(_settings.SliderStyle == "dial", _settings); InvalidateDials(); });
            var dialPresets = new[] { ("neon", "형광 노랑 (기본)"), ("accent", "강조색 (테마)") };
            var dialColor = Ui.CellCombo(new ComboBox { ItemsSource = dialPresets.Select(x => x.Item2).ToArray(), SelectedIndex = _settings.DialColor == "accent" ? 1 : 0 });
            dialColor.SelectionChanged += (_, __) => { _settings.DialColor = dialPresets[Math.Max(0, dialColor.SelectedIndex)].Item1; _settings.Save(); UiTheme.ApplyDial(_settings); InvalidateDials(); };
            p.Children.Add(Ui.SectionTitle("조작 모양 · 다이얼", "다이얼 모드: 슬라이더는 가상 악기의 노브 같은 다이얼, 단추와 탭은 장비의 키, 토글은 로커 스위치가 돼요. 다이얼은 바깥 테를 잡고 돌리거나 가운데를 위아래로 끌고, 휠로도 돌려요 (Shift: 천천히). 하이라이트 색은 다이얼 호·탭 LED·스위치·세밀 조정 막대에 같이 쓰여요 — 형광 노랑이 테마와 너무 튀면 강조색으로 바꿔요."));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("조작 모양", "기본 또는 다이얼 모드. 값과 기능은 같아요", knob),
                Ui.HelpRow("다이얼 모드 단추", "플랫: 납작하게 다른 UI와 어우러지게 · 입체: 그라데이션과 음영", look),
                Ui.HelpRow("UI 전체 통일", "다이얼 모드에서 카드·입력 칸·콤보·체크 상자도 장비 모양으로 맞춰요", unify.Control),
                Ui.HelpRow("하이라이트 색", "다이얼 호·탭 LED·스위치·세밀 조정 막대. 밝은 테마에서 형광 노랑은 조금 진하게 보여요", dialColor))));

            // ---- 미리보기 카메라 조작 방식 (2026-10-23) ----
            var navs = new[] { ("default", "기본 (드래그 공전 · WSAD 회전)"), ("unity", "Unity처럼 (씬 뷰)") };
            var nav = Ui.CellCombo(new ComboBox { ItemsSource = navs.Select(x => x.Item2).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(navs, x => x.Item1 == _settings.ViewNav)) });
            nav.SelectionChanged += (_, __) => { _settings.ViewNav = navs[Math.Max(0, nav.SelectedIndex)].Item1; ViewChanged(); RefreshHint(); };
            p.Children.Add(Ui.SectionTitle("미리보기 카메라", "3D 미리보기를 돌리고 옮기는 방법이에요. 화살표 키(↑↓ 앞뒤 · ←→ 옆)와 휠 확대는 둘 다 같아요."));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("조작 방식", "기본: 왼쪽·오른쪽 드래그로 공전, Shift+드래그·가운데 드래그로 이동, WSAD로 회전 · Unity처럼: Unity 씬 뷰와 같아요 — 오른쪽 드래그로 둘러보고 누른 채 WASD(앞뒤·옆)·QE(아래·위)로 날아다녀요(Shift 빠르게, 휠로 빠르기), Alt+왼쪽 드래그 공전, Alt+오른쪽 드래그 확대, 가운데 드래그 이동. 왼쪽 드래그는 카메라를 돌리지 않아요", nav))));

            // ---- 기즈모 (뷰포트 핸들 · 방향 기즈모) ----
            var handleStyles = new[] { ("bold", "굵은 선 (기본)"), ("solid", "입체"), ("soft", "미니멀"), ("line", "얇은 선 (예전)") };
            var handleStyle = Ui.CellCombo(new ComboBox { ItemsSource = handleStyles.Select(x => x.Item2).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(handleStyles, x => x.Item1 == _settings.ViewHandleStyle)) });
            handleStyle.SelectionChanged += (_, __) => { _settings.ViewHandleStyle = handleStyles[Math.Max(0, handleStyle.SelectedIndex)].Item1; ViewChanged(); };
            var handleSizes = new[] { (0.8, "작게"), (1.0, "보통"), (1.25, "크게"), (1.5, "아주 크게 (VR)") };
            var handleSize = Ui.CellCombo(new ComboBox { ItemsSource = handleSizes.Select(x => x.Item2).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(handleSizes, x => Math.Abs(x.Item1 - _settings.ViewHandleSize) < 0.01)) });
            handleSize.SelectionChanged += (_, __) => { _settings.ViewHandleSize = handleSizes[Math.Max(0, handleSize.SelectedIndex)].Item1; ViewChanged(); };
            var axisStyles = new[] { ("disc", "원 (기본)"), ("cube", "큐브"), ("mini", "미니") };
            var axisStyle = Ui.CellCombo(new ComboBox { ItemsSource = axisStyles.Select(x => x.Item2).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(axisStyles, x => x.Item1 == _settings.ViewAxisStyle)) });
            axisStyle.SelectionChanged += (_, __) => { _settings.ViewAxisStyle = axisStyles[Math.Max(0, axisStyle.SelectedIndex)].Item1; ViewChanged(); };
            p.Children.Add(Ui.SectionTitle("기즈모", "미리보기에서 본·아이템·아바타를 옮기고 돌리는 핸들과 오른쪽 위 방향 기즈모의 모양이에요. 잡는 방법은 같아요."));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("핸들 모양", "굵은 선: 어두운 테두리로 어디서나 잘 보여요 · 입체: 음영 있는 원뿔과 관 · 미니멀: 가늘고 동그란 손잡이 · 얇은 선: 예전 모양", handleStyle),
                Ui.HelpRow("핸들 크기", "크게 하면 잡는 범위도 같이 커져요", handleSize),
                Ui.HelpRow("방향 기즈모", "원: 축 끝 원 · 큐브: 면을 눌러 정면·옆·위로 · 미니: 축 세 줄만", axisStyle))));

            // ---- 테마 ----
            p.Children.Add(Ui.SectionTitle("테마", "바탕·단추·강조 색을 바꿔요. 프리셋을 고르거나 \"직접 고르기\"로 #RRGGBB를 넣어요. 바꾸면 바로 보이고 저장돼요."));
            p.Children.Add(Ui.Card(ThemeRows()));

            // ---- 미리보기 (PhysBone) ----
            void Pb() { _settings.Save(); SendPhysBones(); RefreshPhysBones(); }
            var pbOn = Sw(_settings.PhysBones, on => { _settings.PhysBones = on; Pb(); });
            var guard = Sw(_settings.PhysBoneLegGuard, on => { _settings.PhysBoneLegGuard = on; Pb(); });
            var weight = new ComboBox { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, _) in WeightPresets) weight.Items.Add(label);
            int wi = Array.FindIndex(WeightPresets, x => Math.Abs(x.W - _settings.PhysBoneWeight) < 0.01);
            if (wi < 0) { weight.Items.Add($"{_settings.PhysBoneWeight:0.#}"); wi = WeightPresets.Length; }
            weight.SelectedIndex = wi;
            weight.SelectionChanged += (_, __) =>
            {
                if (weight.SelectedIndex < 0 || weight.SelectedIndex >= WeightPresets.Length) return;
                _settings.PhysBoneWeight = WeightPresets[weight.SelectedIndex].W; Pb();
            };
            p.Children.Add(Ui.SectionTitle("미리보기 PhysBone", "미리보기 도구 줄의 \"PhysBone\" 메뉴와 같은 값이에요. 이 에디터에서만 쓰여요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("PhysBone 켜기", "흔들리는 머리카락·옷을 미리보기에서 움직여요", pbOn.Control),
                Ui.HelpRow("무게감", "무거울수록 몸을 늦게 따라오고 천천히 돌아오며, 빠르게 휙휙 튀지 않아요", weight),
                Ui.HelpRow("다리 뚫림 막기", "치마·코트 자락이 다리를 뚫지 않게 허벅지·종아리에 콜라이더를 더해요", guard.Control),
                Ui.DescRow("느낌", "관성·복원력·흔들림 빠르기·중력·닿았을 때를 따로 창에서 보며 바꿔요", Ui.Button("느낌 조절…", PhysBoneFeelWindow, "PhysBone 느낌 창을 열어요. 열어 둔 채 미리보기를 보며 바꿀 수 있어요")))));


            // ---- 렌더러 프레임 제한 (2026-10-23) ----
            var fpsOpts = new[] { (30, "30"), (60, "60 (기본)"), (90, "90"), (120, "120"), (144, "144"), (240, "240"), (-1, "모니터 주사율에 맞춤 (VSync)"), (0, "제한 없음") };
            var fps = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = fpsOpts.Select(x => x.Item2).ToArray() };
            fps.SelectedIndex = Math.Max(0, Array.FindIndex(fpsOpts, x => x.Item1 == _settings.RenderMaxFps));
            fps.SelectionChanged += (_, __) => { _settings.RenderMaxFps = fpsOpts[Math.Max(0, fps.SelectedIndex)].Item1; _settings.Save(); SendQuality(); };
            p.Children.Add(Ui.SectionTitle("미리보기 속도", "3D 미리보기를 그리는 속도예요. 스튜디오 고화질은 스튜디오 페이지에서 켜고 꺼요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("프레임 제한", "1초에 몇 장까지 그릴지예요. 낮추면 GPU·전기를 덜 써요 — 방송 오버레이로 게임과 같이 쓸 때 30~60을 권해요. 제한 없음은 GPU를 끝까지 써요. 촬영·녹화 결과에는 영향이 없어요", fps))));

            // ---- 방송 ----
            var autoBoard = Sw(_settings.BoardAutoOpen, on => { _settings.BoardAutoOpen = on; _settings.Save(); });
            var hideEd = Sw(_settings.BoardHideEditor, on => { _settings.BoardHideEditor = on; _settings.Save(); });
            var embed = Sw(_settings.EmbedFloats, on => { _settings.EmbedFloats = on; Embed.Enabled = on; _settings.Save(); SetStatus("떠 있는 창을 다시 띄우면 바뀌어요."); });
            p.Children.Add(Ui.SectionTitle("방송 오버레이 · 컨트롤 보드", "맨 위 고정·방송 화면에서 숨기기는 컨트롤 보드 위쪽 단추로 바꿔요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("오버레이를 켜면 보드도 열기", "방송 오버레이를 켤 때 컨트롤 보드를 같이 띄워요", autoBoard.Control),
                Ui.HelpRow("보드를 열면 에디터 숨기기", "보드만 남기고 에디터 창을 숨겨요. 보드의 \"에디터 열기\"로 다시 보여요", hideEd.Control),
                Ui.HelpRow("떠 있는 창을 화면 공유에 보이기", "떠 있는 창·다이얼 창을 에디터 창 안에 넣어, 디스코드·OBS에서 에디터 \"창\"만 공유해도 함께 보여요. 끄면 예전처럼 따로 뜬 창이라 화면 전체 공유에서만 보여요 (새로 띄우는 창부터)", embed.Control))));

            // ---- 블렌드쉐이프 이름 번역: 공식 Cloud Translation API 키 (2026-10-27) ----
            var trKey = new TextBox { Text = _settings.GoogleTranslateKey, PasswordChar = '•', Watermark = "AIza…", MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
            trKey.LostFocus += (_, __) =>
            {
                var v = (trKey.Text ?? "").Trim();
                if (v == _settings.GoogleTranslateKey) return;
                _settings.GoogleTranslateKey = v; _settings.Save(); MachineNames.Retry();
                SetStatus(v.Length > 0 ? "구글 번역 API 키를 저장했어요. 블렌드쉐이프 페이지에서 \"구글 번역\"을 켜면 써요." : "구글 번역 API 키를 지웠어요.");
            };
            p.Children.Add(Ui.SectionTitle("블렌드쉐이프 이름 번역", "번역 표에 없는 이름은 구글 Cloud Translation API로 옮길 수 있어요. 블렌드쉐이프 페이지의 \"구글 번역\"을 켰을 때만 이름이 구글로 보내져요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("API 키", "Google Cloud 콘솔에서 Cloud Translation API를 켜고 만든 API 키예요. 쓴 만큼 Google Cloud에 요금이 붙을 수 있어요 (매달 무료 사용량 있음). 이 PC의 설정 파일에만 저장해요", trKey, wide: true))));

            // ---- 업데이트 ----
            var repo = new TextBox { Text = _settings.UpdateRepo, Watermark = "owner/name", MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
            repo.LostFocus += (_, __) => { var v = (repo.Text ?? "").Trim(); if (v != _settings.UpdateRepo) { _settings.UpdateRepo = v; _settings.Save(); } };
            var auto = Sw(_settings.UpdateAuto, on => { _settings.UpdateAuto = on; _settings.Save(); });
            var check = Ui.Button("지금 확인…", () => { _settings.UpdateRepo = (repo.Text ?? "").Trim(); _settings.Save(); UpdateDialog(); }, "최신 릴리스를 확인하고, 새 버전이면 받아요", Glyph.Refresh);
            string ver = Updater.CurrentText ?? "개발 빌드 (version.txt 없음)";
            string last = DateTime.TryParse(_settings.UpdateLastCheck, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lt) ? lt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "아직 없음";
            p.Children.Add(Ui.SectionTitle("업데이트", "GitHub 릴리스에서 새 버전을 받아요. 새 버전이 있을 때만 사이드바에 \"업데이트\"가 보여요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.DescRow("지금 버전", $"{ver} · 마지막 확인 {last}", check),
                Ui.HelpRow("저장소", "업데이트를 받을 GitHub 저장소 (owner/name)", repo, wide: true),
                Ui.HelpRow("시작할 때 확인", "프로그램을 열 때마다 새 버전이 있는지 몰래 확인해요 (개발 빌드는 확인하지 않아요)", auto.Control))));

            // ---- 불러오기 속도: 텍스처 캐시 (2026-10-23) ----
            string texCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "texcache");
            string[] cacheKeys = { "orig", "rgba", "dxt", "off" };
            var cacheMode = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = new[] { "원본 설정대로 압축 (기본)", "압축 안 함 (가장 선명)", "모두 압축 (가장 가벼움)", "끄기" } };
            cacheMode.SelectedIndex = Math.Max(0, Array.IndexOf(cacheKeys, _settings.TextureCache));
            cacheMode.SelectionChanged += (_, __) =>
            {
                var v = cacheKeys[Math.Max(0, cacheMode.SelectedIndex)];
                if (v == _settings.TextureCache) return;
                _settings.TextureCache = v; _settings.Save();
                SetStatus("텍스처 캐시: 다음에 아바타를 열 때부터 바뀌어요.");
            };
            var cacheBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            var cacheDesc = new TextBlock { Text = "크기 재는 중…" };
            var cacheRow = Ui.DescRow("캐시 크기", cacheDesc, cacheBtns);
            void MeasureCache() => _ = System.Threading.Tasks.Task.Run(() =>
            {
                long bytes = 0; int n = 0;
                try { if (Directory.Exists(texCache)) foreach (var f in Directory.EnumerateFiles(texCache, "*.vptc")) { bytes += new FileInfo(f).Length; n++; } } catch { }
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (cacheDesc != null) { cacheDesc.Text = $"텍스처 {n}장 · {bytes / 1073741824.0:0.##} GB (4 GB를 넘으면 오래 안 쓴 것부터 지워요) · {texCache}"; ToolTip.SetTip(cacheDesc, cacheDesc.Text); } });
            });
            cacheBtns.Children.Add(Open(texCache));
            cacheBtns.Children.Add(Ui.Button("비우기", () =>
            {
                try { if (Directory.Exists(texCache)) foreach (var f in Directory.EnumerateFiles(texCache)) try { File.Delete(f); } catch { } } catch { }
                SetStatus("텍스처 캐시를 비웠어요. 다음에 열 때 다시 만들어요.");
                MeasureCache();
            }, "저장해 둔 텍스처를 모두 지워요 (원본은 그대로)", Glyph.Delete));
            p.Children.Add(Ui.SectionTitle("불러오기 속도", "텍스처 캐시: 원본 PNG를 풀고 줄인 결과를 저장해 두고 다음엔 바로 읽어요. 원본 파일은 바뀌지 않아요"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("텍스처 캐시", "원본 설정대로: 원본 Unity 설정이 압축하는 텍스처만 VRChat처럼 압축해요 — 보이는 모습은 VRChat과 같고 메모리는 줄어요 · 압축 안 함: 모두 원본 그대로 선명하게 (메모리를 더 써요) · 모두 압축: 메모리가 가장 적지만 조금 덜 선명해요 · 끄기: 저장하지 않고 매번 원본을 풀어요. 노멀맵은 늘 그대로예요", cacheMode),
                cacheRow)));
            MeasureCache();

            // ---- 저장 위치 ----
            string imports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports");
            Button Open(string dir) => Ui.Button("폴더 열기", () => { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }, dir, Glyph.Folder);
            var importsRow = Ui.DescRow("가져온 아바타", "크기 재는 중…", Open(imports));
            var openLast = Sw(_settings.OpenLastOnStart, on => { _settings.OpenLastOnStart = on; _settings.Save(); });
            p.Children.Add(Ui.SectionTitle("저장 위치"));
            p.Children.Add(Ui.Card(Rows(
                Ui.HelpRow("켤 때 마지막 파일 열기", "에디터를 켜면 지난번에 열었거나 저장한 세션(없으면 가져온 아바타)을 바로 열어요", openLast.Control),
                Ui.DescRow("설정 파일", Settings.Folder, Open(Settings.Folder)),
                importsRow)));
            // 가져온 IR·텍스처는 수 GB일 수 있어 작업 스레드에서 잰다
            var desc = importsRow.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                long bytes = 0; int n = 0;
                try { if (Directory.Exists(imports)) { foreach (var f in Directory.EnumerateFiles(imports, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length; n = Directory.GetDirectories(imports).Length; } } catch { }
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (desc != null) { desc.Text = $"{n}개 · {bytes / 1073741824.0:0.##} GB · {imports}"; ToolTip.SetTip(desc, desc.Text); } });
            });

            // ---- 정보 (2026-11-01, 배포 전) ----
            Control Doc(string text, string file, Func<string>? pick = null) => Ui.Button(text, () =>
            {
                if (pick != null) file = pick();
                var path = Path.Combine(Updater.AppDir, file);
                if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else SetStatus(file + " 파일이 없습니다.");
            }, file);
            var docs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            // 사용 안내는 화면 언어 판으로 (2026-11-12, README.en.txt · README.ja.txt). 없으면 한국어 판
            //   누를 때 고른다 — 이 창에서 언어를 바꾼 바로 뒤에도 맞게
            string Readme() { var f = Texts.Lang == "ko" ? "README.txt" : $"README.{Texts.Lang}.txt"; return File.Exists(Path.Combine(Updater.AppDir, f)) ? f : "README.txt"; }
            docs.Children.Add(Doc("사용 안내", "README.txt", Readme));
            docs.Children.Add(Doc("이용 조건", "LICENSE.txt"));
            docs.Children.Add(Doc("라이선스", "THIRD_PARTY_NOTICES.txt"));
            p.Children.Add(Ui.SectionTitle("정보"));
            p.Children.Add(Ui.Card(Rows(
                Ui.DescRow("Avatar Light Edit", "무료 프로그램 · " + ver + " · VRChat이나 Unity가 만들거나 승인한 도구가 아닙니다", new Panel()),
                Ui.DescRow("문서", "사용법·조건", docs),
                Ui.DescRow("문제 알리기", "꺼지거나 오류가 나면 최근 기록 파일을 보내 주세요. 아바타 이름·PC 경로가 적혀 있을 수 있어요", Open(Crash.Folder)))));

            var close = Ui.Button("닫기", () => w.Close());
            close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 4, 24, 16);
            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(new ScrollViewer { Content = p, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            w.Content = root;
            return w;
        }
    }
}
