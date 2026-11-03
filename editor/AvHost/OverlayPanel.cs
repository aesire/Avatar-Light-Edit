// 방송 오버레이 설정 칸: 게임 화면 위에 아바타만 띄운다 (디스코드·OBS에서 화면 전체를 공유하면 게임 위에 아바타가 함께 보인다).
// 상태와 켜고 끄기는 OverlayController 한 곳에 있고, 이 칸은 그것을 보여 주고 부르기만 한다. 스튜디오 페이지와 컨트롤 보드(OverlayBoard)에 하나씩 놓인다.
// 표정·웹캠 트래킹·자세·PhysBone은 그대로 움직인다. 카메라는 미리보기에서 맞춘 그대로 (조정 모드에서 오른쪽 끌기·휠로도).
using System;
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
        readonly ComboBox _fps = Ui.CellCombo(new ComboBox());
        readonly ComboBox _aspect = Ui.CellCombo(new ComboBox());
        readonly KeyButton _keyToggle, _keyAdjust;
        bool _quiet;

        /// <param name="openBoard">스튜디오 페이지에서: "컨트롤 보드 열기" 단추 (보드 안에서는 null).</param>
        public OverlayPanel(OverlayController c, Action? openBoard)
        {
            _c = c;
            _corner.ItemsSource = Array.ConvertAll(OverlayController.Corners, x => x.Label);
            _ss.ItemsSource = Array.ConvertAll(OverlayController.Supersamples, x => x.Label);
            _light.ItemsSource = Array.ConvertAll(OverlayController.Layouts, x => { var it = new ComboBoxItem { Content = x.Label }; ToolTip.SetTip(it, x.Tip); return it; });
            ToolTip.SetTip(_light, "주광과 채움광의 자리를 골라요. 고르면 바로 들어가고, 실행 취소로 되돌려요. 세부는 환경 페이지에서");
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

            var rows = new StackPanel();
            rows.Children.Add(Ui.PropRow(Ui.Text("오버레이"), _on.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("위치 조정"), _adjust.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("자리"), _corner, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("크기 (화면 높이 %)"), _size.Slider, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("비율"), _aspect, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("창"), _top, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("프레임 제한"), _fps, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("슈퍼샘플링"), _ss, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("조명 배치"), _light, null));
            var look = Ui.Button("방송 화질 프리셋", _c.ApplyBroadcastLook, "고른 조명 배치 + 반사 세게 · 블룸 약하게 · 노출·대비·채도 조금 — 쨍하고 반사광이 사는 방송용 조명·후처리를 넣어요 (환경 페이지 값, 실행 취소로 되돌려요)", Glyph.Environment);
            rows.Children.Add(Ui.PropRow(Ui.Text("화질"), look, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("단축키: 켜기·끄기"), _keyToggle.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("단축키: 위치 조정"), _keyAdjust.Control, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("상태"), _state, null));
            _state.TextWrapping = TextWrapping.Wrap;
            Children.Add(Ui.Card(rows));
            if (openBoard != null)
            {
                var board = Ui.Button("컨트롤 보드 열기", openBoard, "맨 위에 고정할 수 있고 방송 화면에는 보이지 않는 작은 창에서 오버레이·트래킹·표정·토글을 바로 다뤄요", Glyph.Pin);
                board.Margin = new Thickness(0, 8, 0, 0);
                Children.Add(board);
                Children.Add(Ui.InfoLink("게임 위에 아바타만 띄워요.", "게임 위에 아바타만 띄웁니다. Discord \"화면 공유\"에서 게임 창이 아니라 화면(모니터) 전체를 선택하면 게임과 아바타가 함께 표시됩니다. "
                    + "\"위치 조정\"을 켜면 아바타 창을 끌어서 이동하고 오른쪽 끌기·휠로 돌려 볼 수 있습니다. 끄면 클릭이 게임으로 갑니다. "
                    + "컨트롤 보드는 방송 화면에 잡히지 않아 게임 옆에 띄워 두고 사용합니다."));
            }

            _corner.SelectionChanged += (_, __) => { if (!_quiet && _corner.SelectedIndex >= 0) _c.SetCorner(OverlayController.Corners[_corner.SelectedIndex].Key); };
            _top.IsCheckedChanged += (_, __) => { if (!_quiet) _c.SetTopmost(_top.IsChecked == true); };
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
            _ss.SelectedIndex = Math.Max(0, Array.FindIndex(OverlayController.Supersamples, x => Math.Abs(x.Value - s.OverlaySupersample) < 0.01));
            int fi = Array.FindIndex(OverlayController.FpsChoices, x => x.Value == s.OverlayFps);
            _fps.SelectedIndex = fi >= 0 ? fi : 0;
            int ai = Array.FindIndex(OverlayController.Aspects, x => Math.Abs(x.Value - s.OverlayAspect) < 0.01);
            _aspect.SelectedIndex = ai >= 0 ? ai : OverlayController.Aspects.Length;
            _keyToggle.Show(s.OverlayKeyToggle); _keyAdjust.Show(s.OverlayKeyAdjust);
            _quiet = false;
            _state.Text = !_c.On ? "꺼짐" : _c.Adjusting ? "켜짐 · 위치 조정 중 (끌어서 옮기기)" : "켜짐 · 클릭은 게임으로 가요";
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
