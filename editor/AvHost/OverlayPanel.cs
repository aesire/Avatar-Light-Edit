// 방송 오버레이 설정 칸: 게임 화면 위에 아바타만 띄운다 (디스코드·OBS에서 화면 전체를 공유하면 게임 위에 아바타가 함께 보인다).
// 상태와 켜고 끄기는 OverlayController 한 곳에 있고, 이 칸은 그것을 보여 주고 부르기만 한다. 스튜디오 페이지와 컨트롤 보드(OverlayBoard)에 하나씩 놓인다.
// 표정·웹캠 트래킹·자세·PhysBone은 그대로 움직인다. 카메라는 미리보기에서 맞춘 그대로 (조정 모드에서 오른쪽 끌기·휠로도).
// (2026-11-12) "보내는 곳": 게임 위 · OBS · 게임 화면과 합치기(디스코드 게임 방송). 합치기는 "게임 창" 목록(펼칠 때마다 새로 읽음)에서 고른다.
//   (2026-11-12) OBS 배경: 투명 · 크로마키(초록·파랑·분홍 또는 #RRGGBB) — OBS로 보낼 때만 보인다
//   (2026-11-12) 쉬운 모드 · 전문가 모드로 나눔: 위 카드 = 켜기 · 보내는 곳 · 배경 · 자리·크기 · 조명(환경 탭 그대로 / 전용 + 가져오기) · 단축키 · 상태,
//     아래 "세부 설정" 카드(전문가 모드에서만) = 비율 · 맨 위 · 프레임 제한 · 슈퍼샘플링 · 조명 배치 · 위치 조정 단축키. "방송 화질 프리셋" 단추는 뺐다
//   보내는 곳에 따라 맞지 않는 줄은 숨긴다 (창으로 띄우면 위치 조정·맨 위 없음, OBS는 자리·크기 없음 — 창 크기 고정)
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    public sealed class OverlayPanel : StackPanel
    {
        readonly OverlayController _c;
        readonly Ui.Switch _on, _adjust;
        readonly TextBlock _state = Ui.Text("꺼짐", 12, FontWeight.Normal, Ui.KText2);
        readonly ComboBox _corner = Ui.CellCombo(new ComboBox());
        readonly Ui.EditSlider _size;
        readonly CheckBox _top = new CheckBox { Content = "항상 맨 위" };
        readonly ComboBox _ss = Ui.CellCombo(new ComboBox());
        readonly ComboBox _light = Ui.CellCombo(new ComboBox());
        readonly ComboBox _lightSrc = Ui.CellCombo(new ComboBox());
        readonly ComboBox _fps = Ui.CellCombo(new ComboBox());
        readonly ComboBox _aspect = Ui.CellCombo(new ComboBox());
        readonly KeyButton _keyToggle, _keyAdjust;
        readonly ComboBox _output = Ui.CellCombo(new ComboBox());
        readonly ComboBox _game = Ui.CellCombo(new ComboBox());
        readonly ComboBox _obsBg = Ui.CellCombo(new ComboBox());
        readonly ComboBox _chroma = Ui.CellCombo(new ComboBox());
        readonly TextBox _chromaHex;
        readonly Border _chromaSwatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        List<(IntPtr Hwnd, string Title)> _games = new List<(IntPtr, string)>();
        Control _rowObsBg = null!, _rowChroma = null!, _rowGame = null!, _rowAdjust = null!, _rowCorner = null!, _rowSize = null!, _rowTop = null!, _keyAdjustRow = null!;
        readonly TextBlock _sizeName = Ui.Text("크기 (화면 높이 %)");
        bool _quiet;

        /// <param name="openBoard">스튜디오 페이지에서: "컨트롤 보드 열기" 단추 (보드 안에서는 null).</param>
        public OverlayPanel(OverlayController c, Action? openBoard)
        {
            _c = c;
            _corner.ItemsSource = Array.ConvertAll(OverlayController.Corners, x => x.Label);
            _ss.ItemsSource = Array.ConvertAll(OverlayController.Supersamples, x => x.Label);
            _light.ItemsSource = Array.ConvertAll(OverlayController.Layouts, x => { var it = new ComboBoxItem { Content = x.Label }; ToolTip.SetTip(it, x.Tip); return it; });
            ToolTip.SetTip(_light, "주광과 채움광의 자리를 골라요. 오버레이 전용 조명이면 그 조명에, 아니면 환경 탭 조명에 들어가요 (환경 탭이면 실행 취소로 되돌려요)");
            _lightSrc.ItemsSource = Array.ConvertAll(OverlayController.LightSources, x => { var it = new ComboBoxItem { Content = x.Label }; ToolTip.SetTip(it, x.Tip); return it; });
            ToolTip.SetTip(_lightSrc, "방송에 쓸 조명. 환경 탭 조명을 그대로 쓰거나, 오버레이가 켜 있는 동안만 따로 저장한 조명을 써요");
            ToolTip.SetTip(_ss, "방송 창을 이만큼 크게 그려 줄여요. 머리카락 끝·반사광 띠·외곽선이 깨끗해지는 대신 GPU를 더 써요");
            _size = new Ui.EditSlider(15, 100, v => _c.SetSize(v / 100, false), v => _c.SetSize(v / 100, true));
            _on = new Ui.Switch(on => _c.SetOn(on));
            _adjust = new Ui.Switch(a => _c.SetAdjust(a));
            ToolTip.SetTip(_adjust.Control, "켜면 오버레이 창을 끌어서 옮기고, 오른쪽 아래 손잡이로 크기를, 오른쪽 끌기·휠로 카메라를 돌려요 (테두리가 보여요). 다 맞췄으면 꺼서 클릭이 게임으로 가게 해요");
            _fps.ItemsSource = Array.ConvertAll(OverlayController.FpsChoices, x => x.Label);
            ToolTip.SetTip(_fps, "오버레이가 켜 있는 동안만 이만큼으로 그려요. 게임과 GPU를 나눠 쓰므로 낮을수록 게임이 덜 느려져요 (디스코드 화면 공유는 보통 30 fps)");
            _aspect.ItemsSource = Array.ConvertAll(OverlayController.Aspects, x => x.Label).Append("직접 맞춘 비율").ToArray();
            ToolTip.SetTip(_aspect, "오버레이 창의 가로·세로 비율. 위치 조정에서 오른쪽 아래 손잡이를 끌면 비율도 자유롭게 바뀌어요");
            _keyToggle = new KeyButton(k => _c.SetKey("toggle", k));
            _keyAdjust = new KeyButton(k => _c.SetKey("adjust", k));

            _output.ItemsSource = Array.ConvertAll(OverlayController.Outputs, x => { var it = new ComboBoxItem { Content = x.Label }; ToolTip.SetTip(it, x.Tip); return it; });
            ToolTip.SetTip(_output, "아바타를 어디로 보낼지 골라요. 디스코드에서 게임 하나만 방송하거나 OBS에서 게임 캡처를 쓰면 게임 위 투명 창은 잡히지 않아요");
            ToolTip.SetTip(_game, "아바타와 합칠 게임 창. 게임을 먼저 켜고 목록을 펼쳐 골라요 (펼칠 때마다 새로 읽어요)");
            _game.DropDownOpened += (_, __) => FillGames();
            _obsBg.ItemsSource = Array.ConvertAll(OverlayController.ObsBackgrounds, x => { var it = new ComboBoxItem { Content = x.Label }; ToolTip.SetTip(it, x.Tip); return it; });
            ToolTip.SetTip(_obsBg, "OBS에서 '게임 캡처'로 잡으면 투명, '윈도우 캡처'로 잡으면 크로마키를 고르세요 (윈도우 캡처는 투명을 못 읽어 배경이 검게 나와요)");
            _chroma.ItemsSource = Array.ConvertAll(OverlayController.ChromaColors, x => x.Label).Append("직접 고른 색").ToArray();
            ToolTip.SetTip(_chroma, "배경에 칠할 색. 아바타·옷에 없는 색을 고르면 크로마키로 깔끔하게 빠져요");
            _chromaHex = Ui.Cell(t => { if (!_c.SetChroma(t)) Refresh(); }, Refresh);
            ToolTip.SetTip(_chromaHex, "#RRGGBB — OBS 크로마 키 필터의 '사용자 지정 색'에 같은 값을 넣으세요");
            var chromaBox = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,90") };
            chromaBox.Children.Add(_chroma);
            Grid.SetColumn(_chromaSwatch, 1); chromaBox.Children.Add(_chromaSwatch);
            Grid.SetColumn(_chromaHex, 2); chromaBox.Children.Add(_chromaHex);

            // 쉬운 모드에서도 보이는 것
            var rows = new StackPanel();
            rows.Children.Add(Ui.PropRow(Ui.Text("오버레이"), _on.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("보내는 곳"), _output, null));
            rows.Children.Add(_rowObsBg = Ui.PropRow(Ui.Text("배경"), _obsBg, null));
            rows.Children.Add(_rowChroma = Ui.PropRow(Ui.Text("크로마키 색"), chromaBox, null));
            rows.Children.Add(_rowGame = Ui.PropRow(Ui.Text("게임 창"), _game, null));
            rows.Children.Add(_rowAdjust = Ui.PropRow(Ui.Text("위치 조정"), _adjust.Control, null));
            rows.Children.Add(_rowCorner = Ui.PropRow(Ui.Text("자리"), _corner, null));
            rows.Children.Add(_rowSize = Ui.PropRow(_sizeName, _size.Slider, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("조명"), _lightSrc, null));
            var import = Ui.Button("환경 탭 조명 가져오기", _c.ImportEnvironment, "지금 환경 탭의 조명·후처리를 오버레이 전용 조명으로 저장해요. 오버레이가 켜 있는 동안만 이 조명을 쓰고, 끄면 환경 탭 조명으로 돌아와요", Glyph.Environment);
            import.Margin = new Thickness(0);
            rows.Children.Add(Ui.PropRow(Ui.Text(""), import, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("단축키: 켜기·끄기"), _keyToggle.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("상태"), _state, null));
            _state.TextWrapping = TextWrapping.Wrap;
            Children.Add(Ui.Card(rows));
            // 전문가 모드에서만: 화질·창 세부 (안의 줄은 보내는 곳에 따라 따로 숨긴다 — 바깥 카드에 클래스를 달아 둘이 겹치지 않게)
            var more = new StackPanel();
            more.Children.Add(Ui.SectionTitle("세부 설정"));
            more.Children.Add(Ui.PropRow(Ui.Text("비율"), _aspect, null));
            more.Children.Add(_rowTop = Ui.PropRow(Ui.Text("창"), _top, null));
            more.Children.Add(Ui.PropRow(Ui.Text("프레임 제한"), _fps, null));
            more.Children.Add(Ui.PropRow(Ui.Text("슈퍼샘플링"), _ss, null));
            more.Children.Add(Ui.PropRow(Ui.Text("조명 배치"), _light, null));
            more.Children.Add(_keyAdjustRow = Ui.PropRow(Ui.Text("단축키: 위치 조정"), _keyAdjust.Control, null));
            var moreCard = Ui.Expert(Ui.Card(more));
            moreCard.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(moreCard);
            Children.Add(Ui.EasyOnly(Ui.Wrap("비율·프레임 제한·슈퍼샘플링·조명 배치는 전문가 모드에서 바꿔요.", Ui.KText2, 12)));
            if (openBoard != null)
            {
                var board = Ui.Button("컨트롤 보드 열기", openBoard, "맨 위에 고정할 수 있고 방송 화면에는 보이지 않는 작은 창에서 오버레이·트래킹·표정·토글을 바로 다뤄요", Glyph.Pin);
                board.Margin = new Thickness(0, 8, 0, 0);
                Children.Add(board);
                Children.Add(Ui.InfoLink("게임 위에 아바타만 띄워요.", "게임 위에 아바타만 띄웁니다. Discord \"화면 공유\"에서 게임 창이 아니라 화면(모니터) 전체를 선택하면 게임과 아바타가 함께 표시됩니다. "
                    + "\"위치 조정\"을 켜면 아바타 창을 끌어서 이동하고 오른쪽 끌기·휠로 돌려 볼 수 있습니다. 끄면 클릭이 게임으로 갑니다. "
                    + "컨트롤 보드는 방송 화면에 잡히지 않아 게임 옆에 띄워 두고 사용합니다."));
                Children.Add(Ui.InfoLink("OBS로 보내기", "\"보내는 곳\"을 \"OBS로 보내기\"로 하고 오버레이를 켜면 'ALE 방송 출력 (OBS)' 창이 뜹니다. "
                    + "배경이 \"투명\"이면 OBS에서 소스 추가 → \"게임 캡처\" → 모드 \"특정 창 캡처\" → 창 'ALE 방송 출력 (OBS)'을 고르고 \"투명도 허용\"을 켜면 배경 없이 아바타만 들어갑니다. "
                    + "\"윈도우 캡처\"로 잡으면 투명을 읽지 못해 배경이 검게 나오므로, 그때는 배경을 \"크로마키\"로 하고 윈도우 캡처 소스에 필터 → \"크로마 키\"를 넣어 같은 색(사용자 지정 색에 #RRGGBB)을 빼세요. "
                    + "창은 게임 뒤로 보내도 되지만 최소화하면 멈춥니다. 창 안에서 오른쪽 끌기·휠로 카메라를 돌립니다."));
                Children.Add(Ui.InfoLink("디스코드에서 게임 하나만 방송할 때", "디스코드가 게임 창 하나만 잡으면 그 위에 띄운 아바타 창은 보이지 않습니다. "
                    + "\"보내는 곳\"을 \"게임 화면과 합치기\"로 하고 \"게임 창\"에서 게임을 고른 뒤 오버레이를 켜면, 게임 화면 위에 아바타를 얹은 'ALE 방송 화면' 창이 뜹니다. "
                    + "디스코드 화면 공유에서 게임 대신 이 창을 고르세요. 자리·크기·비율은 그 화면 안 아바타의 자리입니다. "
                    + "디스코드는 고른 창의 프로그램 소리를 보내므로, 게임 소리가 필요하면 화면 공유의 소리 설정을 확인하세요. Windows 10 1903 이상에서 됩니다."));
            }

            _output.SelectionChanged += (_, __) => { if (!_quiet && _output.SelectedIndex >= 0) _c.SetOutput(OverlayController.Outputs[_output.SelectedIndex].Key); };
            _obsBg.SelectionChanged += (_, __) => { if (!_quiet && _obsBg.SelectedIndex >= 0) _c.SetObsBackground(OverlayController.ObsBackgrounds[_obsBg.SelectedIndex].Key); };
            _chroma.SelectionChanged += (_, __) =>
            {
                if (_quiet || _chroma.SelectedIndex < 0) return;
                if (_chroma.SelectedIndex < OverlayController.ChromaColors.Length) _c.SetChroma(OverlayController.ChromaColors[_chroma.SelectedIndex].Hex);
                else _chromaHex.Focus();   // 직접 고른 색: 옆 칸에 #RRGGBB
            };
            _game.SelectionChanged += (_, __) =>
            {
                if (_quiet || _game.SelectedIndex < 0 || _game.SelectedIndex >= _games.Count) return;
                var g = _games[_game.SelectedIndex];
                if (g.Hwnd != IntPtr.Zero) _c.SetGame(g.Hwnd, g.Title);
            };
            _corner.SelectionChanged += (_, __) => { if (!_quiet && _corner.SelectedIndex >= 0) _c.SetCorner(OverlayController.Corners[_corner.SelectedIndex].Key); };
            _top.IsCheckedChanged += (_, __) => { if (!_quiet) _c.SetTopmost(_top.IsChecked == true); };
            _lightSrc.SelectionChanged += (_, __) => { if (!_quiet && _lightSrc.SelectedIndex >= 0) _c.SetLightSource(OverlayController.LightSources[_lightSrc.SelectedIndex].Key); };
            _light.SelectionChanged += (_, __) => { if (!_quiet && _light.SelectedIndex >= 0) _c.SetLighting(OverlayController.Layouts[_light.SelectedIndex].Key); };
            _ss.SelectionChanged += (_, __) => { if (!_quiet && _ss.SelectedIndex >= 0) _c.SetSupersample(OverlayController.Supersamples[_ss.SelectedIndex].Value); };
            _fps.SelectionChanged += (_, __) => { if (!_quiet && _fps.SelectedIndex >= 0) _c.SetFps(OverlayController.FpsChoices[_fps.SelectedIndex].Value); };
            _aspect.SelectionChanged += (_, __) => { if (!_quiet && _aspect.SelectedIndex >= 0 && _aspect.SelectedIndex < OverlayController.Aspects.Length) _c.SetAspect(OverlayController.Aspects[_aspect.SelectedIndex].Value); };
            _c.Changed += Refresh;
            DetachedFromVisualTree += (_, __) => _c.Changed -= Refresh;
            AttachedToVisualTree += (_, __) => { _c.Changed -= Refresh; _c.Changed += Refresh; Refresh(); };
            Refresh();
        }

        void Refresh()
        {
            var s = _c.Settings;
            _quiet = true;
            _on.SetQuiet(_c.On);
            _adjust.SetQuiet(_c.Adjusting);
            _corner.SelectedIndex = Math.Max(0, Array.FindIndex(OverlayController.Corners, x => x.Key == s.OverlayCorner));
            _size.SetQuiet(s.OverlaySize * 100);
            _top.IsChecked = s.OverlayTopmost;
            _light.SelectedIndex = Math.Max(0, Array.FindIndex(OverlayController.Layouts, x => x.Key == s.OverlayLighting));
            _lightSrc.SelectedIndex = _c.LightSource == "own" ? 1 : 0;
            _ss.SelectedIndex = Math.Max(0, Array.FindIndex(OverlayController.Supersamples, x => Math.Abs(x.Value - s.OverlaySupersample) < 0.01));
            int fi = Array.FindIndex(OverlayController.FpsChoices, x => x.Value == s.OverlayFps);
            _fps.SelectedIndex = fi >= 0 ? fi : 0;
            int ai = Array.FindIndex(OverlayController.Aspects, x => Math.Abs(x.Value - s.OverlayAspect) < 0.01);
            _aspect.SelectedIndex = ai >= 0 ? ai : OverlayController.Aspects.Length;
            _keyToggle.Show(s.OverlayKeyToggle); _keyAdjust.Show(s.OverlayKeyAdjust);
            var output = _c.Output;
            _output.SelectedIndex = Math.Max(0, Array.FindIndex(OverlayController.Outputs, x => x.Key == output));
            if (!_game.IsDropDownOpen)
            {
                // 접힌 동안은 고른 게임 제목만 (목록은 펼칠 때 읽는다)
                _games = new List<(IntPtr, string)> { (IntPtr.Zero, s.OverlayGameTitle.Length > 0 ? s.OverlayGameTitle : "목록을 펼쳐 게임 창을 고르세요") };
                _game.ItemsSource = new[] { _games[0].Title };
                _game.SelectedIndex = 0;
            }
            _obsBg.SelectedIndex = _c.ObsBackground == "chroma" ? 1 : 0;
            int ci = Array.FindIndex(OverlayController.ChromaColors, x => x.Hex.Equals(s.OverlayChroma, StringComparison.OrdinalIgnoreCase));
            _chroma.SelectedIndex = ci >= 0 ? ci : OverlayController.ChromaColors.Length;
            _chromaHex.Text = s.OverlayChroma;
            var rgb = OverlayController.ParseHex(s.OverlayChroma) ?? new[] { 0f, 1f, 0f };
            _chromaSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(rgb[0] * 255), (byte)(rgb[1] * 255), (byte)(rgb[2] * 255)));
            bool screen = output == "screen";
            _rowObsBg.IsVisible = output == "obs";
            _rowChroma.IsVisible = output == "obs" && _c.ObsBackground == "chroma";
            _rowGame.IsVisible = output == "game";
            _rowAdjust.IsVisible = _rowTop.IsVisible = _keyAdjustRow.IsVisible = screen;
            _rowCorner.IsVisible = _rowSize.IsVisible = output != "obs";
            _sizeName.Text = output == "game" ? "크기 (방송 화면 높이 %)" : "크기 (화면 높이 %)";
            _quiet = false;
            _state.Text = !_c.On ? "꺼짐"
                : output == "obs" && _c.ObsBackground == "chroma" ? $"켜짐 · OBS 윈도우 캡처에서 '{Texts.T(OverlayController.ObsTitle)}' 창을 고르고 크로마 키 {s.OverlayChroma}"
                : output == "obs" ? $"켜짐 · OBS 게임 캡처에서 '{Texts.T(OverlayController.ObsTitle)}' 창을 고르고 투명도 허용"
                : output == "game" ? $"켜짐 · 디스코드에서 '{Texts.T(OverlayController.GameTitle)}' 창을 방송하세요"
                : _c.Adjusting ? "켜짐 · 위치 조정 중 (끌어서 옮기기)" : "켜짐 · 클릭은 게임으로 가요";
        }

        /// <summary>게임 창 목록을 새로 읽는다 (펼칠 때). 고른 제목이 있으면 그 줄을 고른 채로.</summary>
        void FillGames()
        {
            _quiet = true;
            _games = _c.GameWindows();
            var cur = _c.Game();
            var titles = _games.ConvertAll(x => x.Title);
            if (_games.Count == 0) { _games.Add((IntPtr.Zero, "보이는 창이 없어요 (게임을 먼저 켜세요)")); titles.Add(_games[0].Title); }
            _game.ItemsSource = titles;
            _game.SelectedIndex = Math.Max(0, _games.FindIndex(x => x.Hwnd == cur && cur != IntPtr.Zero));
            if (cur == IntPtr.Zero) _game.SelectedIndex = -1;
            _quiet = false;
        }
    }

    /// <summary>전역 단축키 정하기 단추: 누른 다음 키 조합을 누른다 (Esc = 취소, Backspace = 지우기). 게임 중에도 눌린다 (GlobalHotkeys).</summary>
    public sealed class KeyButton
    {
        readonly Button _btn;
        readonly Action<string> _set;
        string _key = "";
        bool _capturing;
        public Control Control => _btn;

        public KeyButton(Action<string> set)
        {
            _set = set;
            _btn = Ui.Button("눌러서 정하기", Start, "누른 다음 쓸 키 조합을 누르세요 (Esc = 취소, Backspace = 지우기). 게임 중에도 눌려요", Glyph.Keyboard);
            _btn.Margin = new Thickness(0);
            _btn.AddHandler(InputElement.KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _btn.LostFocus += (_, __) => { if (_capturing) { _capturing = false; Show(_key); } };
        }

        public void Show(string key)
        {
            _key = key ?? "";
            if (_capturing) return;
            _btn.Content = Ui.IconLabel(Glyph.Keyboard, _key.Length == 0 ? "없음 (눌러서 정하기)" : GlobalHotkeys.Text(_key));
        }

        void Start()
        {
            _capturing = true;
            _btn.Content = Ui.IconLabel(Glyph.Keyboard, "쓸 키를 누르세요…");
            _btn.Focus();
        }

        void OnKey(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (!_capturing) return;
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.Escape) { _capturing = false; Show(_key); return; }
            if (e.Key == Avalonia.Input.Key.Back && e.KeyModifiers == Avalonia.Input.KeyModifiers.None) { _capturing = false; _set(""); Show(""); return; }
            var k = GlobalHotkeys.FromKeyEvent(e.Key, e.KeyModifiers);
            if (k == null) return;   // 조합 키만 누른 것: 다음 키를 기다린다
            _capturing = false;
            _set(k); Show(k);
        }
    }
}
