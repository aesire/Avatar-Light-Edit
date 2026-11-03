// 표정 (2026-10-19 결정: 프리셋은 만들지 않는다). 2026-10-19 2차: 따로 있던 "표정" 작업 창을 스튜디오 페이지의 한 묶음으로 옮겼다.
//   아바타 기본 표정  IR expressions (importsim이 아바타의 FX 레이어에서 읽은 표정 애니메이션): 손 제스처 · 옵션 표정 등 묶음별 단추
//   .anim 불러오기     사용자가 가진 표정 애니메이션(블렌드쉐이프 커브)의 첫 순간. 이번 실행 동안 목록에 남는다
//   페이셜 트래킹      자체 웹캠(렌더러 WebcamFace) 또는 외부 앱(VMC 프로토콜)으로 얼굴을 따라 움직인다. "지금 표정으로 고정"하면 그 값이 표정으로 기록된다
// 표정은 Document의 Addr.Expression 하나로 기록된다 (실행 취소·세션 포함, 미리보기 전용 — 업로드에는 쓰지 않는다).
// 트래킹 값은 기록하지 않는 실시간 레이어(렌더러)이고, 표정·사용자 블렌드쉐이프 값보다 위에 보인다.
// 단축키 F1~F9: 표정 단추를 오른쪽 클릭하거나 "지금 표정"을 단축키에 넣는다 (Addr.ExpressionKeys, Core.ToggleSet). 누르면 쓰고, 다시 누르면 뺀다.
// .anim 저장 (Core.AnimWriter): 지금 표정(트래킹에서 고정한 것 포함)을 한 장면 애니메이션으로, 트래킹 녹화는 시간에 따라 움직이는 애니메이션으로.
//   저장한 파일은 "가진 표정 애니메이션" 목록에 들어가 표정 프리셋처럼 다시 쓴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class ExpressionPanel : StackPanel
    {
        readonly EditorContext _ctx;
        readonly StackPanel _body = new StackPanel();
        readonly TextBlock _current = Ui.Text("", 14, FontWeight.SemiBold);
        readonly Button _resetBtn;
        readonly StackPanel _avatarList = new StackPanel();
        readonly WrapPanel _animList = new WrapPanel();
        readonly StackPanel _keyList = new StackPanel();
        static readonly List<(string Path, AnimClip Clip)> LoadedClips = new List<(string, AnimClip)>();   // 이번 실행 동안 불러온 .anim

        // 트래킹
        readonly ComboBox _source = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "끔", "웹캠 (이 프로그램 · 비권장)", "외부 앱 (VMC)", "트래커 앱 (Babble · VRCFT식 OSC)" }, SelectedIndex = 0 });
        // 말하기 → 입 모양 (립싱크, 렌더러 LipSync.cs): 트래킹과 따로 켠다
        readonly CheckBox _lipOn = new CheckBox { Content = "말할 때 입 움직이기 (마이크)" };
        readonly ComboBox _mic = Ui.CellCombo(new ComboBox());
        Ui.EditSlider _lipGain = null!;
        readonly TextBlock _lipStatus = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
        readonly StackPanel _lipRows = new StackPanel();
        readonly ComboBox _camera = Ui.CellCombo(new ComboBox());
        readonly TextBox _port;
        readonly CheckBox _mirror = new CheckBox { Content = "좌우 바꾸기" };
        readonly CheckBox _headOn = new CheckBox { Content = "머리 회전도 따라가기" };
        readonly CheckBox _bodyOn = new CheckBox { Content = "몸도 따라 움직이기" }, _armsOn = new CheckBox { Content = "팔 자연스럽게 내리기" };
        readonly CheckBox _moveOn = new CheckBox { Content = "머리 위치도 따라가기" };
        readonly CheckBox _eyeLink = new CheckBox { Content = "두 눈 같이 감기 (윙크가 아니면)" };
        readonly CheckBox _fill = new CheckBox { Content = "끊김 메우기 (fps가 낮으면 이어 붙이기)" };
        Ui.EditSlider _eyeSens = null!, _eyeSharp = null!;
        static readonly (string Key, string Label, string Tip)[] Groups =
        {
            ("eye", "눈 감기·크게", "깜빡임·눈 찡그림·눈 크게 뜨기"), ("gaze", "시선", "눈동자 방향 (시선 블렌드쉐이프나 눈 본)"),
            ("brow", "눈썹", "눈썹 올리기·내리기"), ("mouth", "입", "입 벌림·웃음·오므리기·혀 등 입과 턱"),
            ("cheek", "볼·코", "볼 부풀리기·볼 올리기·코 찡그림"), ("head", "머리 회전", "고개 돌리기·끄덕이기·기울이기 각도"),
        };
        readonly Dictionary<string, Ui.EditSlider> _groupSliders = new Dictionary<string, Ui.EditSlider>();
        readonly Ui.EditSlider _smooth, _gain;
        readonly TextBlock _status = Ui.Text("꺼짐", 13, FontWeight.Normal, Ui.KText2);
        readonly Button _freeze, _capture;
        bool _capturing;
        readonly Grid _cameraRow, _portRow;
        readonly StackPanel _trackOn = new StackPanel();   // 트래킹을 켰을 때만 보이는 것
        static bool _detailOpen;
        int _portValue = 39539;
        bool _quiet;
        readonly Settings _settings;
        readonly Button _calibrate, _calClear;

        public ExpressionPanel(EditorContext ctx, Settings settings)
        {
            _ctx = ctx; _settings = settings;
            Children.Add(_body);

            _resetBtn = Ui.Button("표정 빼기", () => Doc?.Reset(Addr.Expression, "표정 빼기"), "표정을 빼고 블렌드쉐이프 값 그대로 보여요", Glyph.Reset);
            _resetBtn.Margin = new Thickness(0);
            var keyBtn = Ui.IconButton(Glyph.Keyboard, () => { }, "지금 표정을 단축키 F1~F9에 넣기");
            keyBtn.Flyout = KeyMenu(() => Doc?.Get(Addr.Expression) is JNode cur && !cur.IsNull ? cur : null);
            keyBtn.Margin = new Thickness(0, 0, 6, 0);
            var now = new DockPanel();
            DockPanel.SetDock(_resetBtn, Dock.Right);
            now.Children.Add(_resetBtn);
            DockPanel.SetDock(keyBtn, Dock.Right);
            now.Children.Add(keyBtn);
            _current.VerticalAlignment = VerticalAlignment.Center;
            now.Children.Add(_current);
            _body.Children.Add(Ui.Card(Ui.PropRow(Ui.Text("지금 표정"), now, null)));
            _body.Children.Add(Ui.InfoLink("표정은 미리보기·촬영 전용이에요.", "표정은 미리보기·촬영에만 쓰여요. 블렌드쉐이프 페이지에서 바꾼 값 위에 덮여 보이고, 표정을 빼면 그 값이 다시 보여요. 업로드에는 들어가지 않아요."));
            _body.Children.Add(_keyList);

            // 묶음이 많아 길어지므로 접을 수 있게 (접힘은 설정에 남는다)
            var avatarFold = new Ui.FoldSection("아바타 기본 표정", _settings.ExprAvatarOpen, help: "아바타의 FX 레이어에서 읽은 표정 애니메이션이에요. 손 제스처·옵션 표정 묶음별로 눌러서 보여요.");
            avatarFold.Toggled += o => { _settings.ExprAvatarOpen = o; _settings.Save(); };
            avatarFold.Body.Children.Add(_avatarList);
            _body.Children.Add(avatarFold);

            var animHead = new TitleRow { Margin = new Thickness(0, 8, 0, 0) };
            var load = Ui.Button(".anim 불러오기…", LoadAnimDialog, "가지고 있는 표정 애니메이션(블렌드쉐이프 커브가 든 .anim)의 첫 순간을 표정으로 써요", Glyph.Open);
            load.Margin = new Thickness(0);
            DockPanel.SetDock(load, Dock.Right);
            animHead.Children.Add(load);
            animHead.Children.Add(Ui.SectionTitle("가진 표정 애니메이션"));
            _body.Children.Add(animHead);
            _body.Children.Add(_animList);

            // ---- 페이셜 트래킹 ----
            _body.Children.Add(Ui.SectionTitle("페이셜 트래킹", "웹캠: 이 프로그램이 직접 얼굴을 따라갑니다 (MediaPipe 얼굴 모델, 이 PC 안에서만 계산하고 영상은 저장·전송하지 않습니다). 눈·시선·눈썹·볼·입까지 ARKit 52개 값을 만듭니다. 가만히 있어도 눈이나 입이 움직여 보이면 \"캘리브레이션\"을 누르세요.\n"
                + "외부 앱: iFacialMocap·Waidayo(iPhone), XR Animator·VSeeFace(웹캠) 등에서 VMC 보내기를 이 PC의 위 포트로 켜면 ARKit 52개 값(Perfect Sync)을 받습니다.\n"
                + "트래커 앱: Project Babble(입)·Baballonia·EyeTrackVR(눈) 같은 VR 페이셜 트래커가 보내는 OSC를 VRCFaceTracking 대신 바로 받습니다 (기본 8888, VRCFT는 꺼 두세요). VRCFT v2 파라미터도 받습니다.\n"
                + "연결: 아바타에 페이셜 트래킹 설정(VRCFT용 FX Animator)이 설치되어 있으면 그 연결을 VRChat에서처럼 그대로 사용합니다. 없으면 ARKit·Unified 이름의 BlendShape(Perfect Sync), 그것도 없으면 MMD BlendShape(まばたき·あいうえお·笑い·困る …)로 움직입니다."));
            _port = Ui.NumberCell(v => { SetPort(Math.Clamp((int)v, 1024, 65535)); _port!.Text = PortValue.ToString(); Restart(); }, () => _port!.Text = PortValue.ToString());
            _port.Text = _portValue.ToString();
            _smooth = new Ui.EditSlider(0, 1, v => SendOptions(), v => { _settings.FaceSmoothing = v; _settings.Save(); SendOptions(); }) { };
            _smooth.Slider.Value = _settings.FaceSmoothing;
            _gain = new Ui.EditSlider(0.5, 2, v => SendOptions(), v => SendOptions());
            _gain.Slider.Value = 1;
            var rows = new StackPanel();
            rows.Children.Add(Ui.PropRow(Ui.Text("트래킹"), _source, null));
            var camRefresh = Ui.IconButton(Glyph.Refresh, RefreshDevices, "웹캠 목록 새로고침 (방금 꽂은 카메라 찾기)");
            camRefresh.Margin = new Thickness(6, 0, 0, 0);
            var camBox = new DockPanel();
            DockPanel.SetDock(camRefresh, Dock.Right);
            camBox.Children.Add(camRefresh); camBox.Children.Add(_camera);
            _cameraRow = Ui.PropRow(Ui.Text("카메라"), camBox, null);
            _portRow = Ui.PropRow(Ui.Text("받을 포트 (UDP)"), _port, null);
            rows.Children.Add(_cameraRow); rows.Children.Add(_portRow);
            rows.Children.Add(Ui.PropRow(Ui.Text("상태"), _status, null));
            _body.Children.Add(Ui.Card(rows));
            // 트래킹을 켰을 때만 (사용성, 2026-10-21): 꺼져 있어도 따라가기·감도 15줄이 늘 보여 페이지가 길었다.
            // 자주 바꾸는 따라가기는 카드로, 감도·눈 조정은 "세부 조정"으로 접어 둔다 (접힘은 이 실행 동안 기억)
            var follow = new StackPanel();
            follow.Children.Add(Ui.PropRow(Ui.Text("거울처럼"), _mirror, null));
            follow.Children.Add(Ui.PropRow(Ui.Text("머리"), _headOn, null));
            follow.Children.Add(Ui.PropRow(Ui.Text("위치"), _moveOn, null));
            follow.Children.Add(Ui.PropRow(Ui.Text("몸"), _bodyOn, null));
            follow.Children.Add(Ui.PropRow(Ui.Text("팔"), _armsOn, null));
            _trackOn.Children.Add(Ui.Card(follow));
            var detail = new Ui.FoldSection("세부 조정", _detailOpen, help: "부드럽게·세기·눈 감기와 부위별 감도. 눈이나 입이 덜 움직이거나 너무 움직일 때 바꿔요. 바꾼 값은 저장되어 다음에도 쓰여요.");
            detail.Toggled += o => _detailOpen = o;
            rows = new StackPanel();
            rows.Children.Add(Ui.PropRow(Ui.Text("부드럽게"), _smooth.Slider, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("끊김"), _fill, null));
            rows.Children.Add(Ui.Expert(Ui.PropRow(Ui.Text("세기"), _gain.Slider, null)));
            // 눈 감기: 반쯤 감긴 채로 머무는 것을 줄인다
            double EyeVal(string k, double d) => _settings.FaceEye.Has(k) ? _settings.FaceEye[k].AsDouble : d;
            void SaveEye(string k, double v) { _settings.FaceEye.Set(k, Math.Round(v, 2)); _settings.Save(); SendOptions(); }
            _eyeSens = new Ui.EditSlider(0, 1, v => SendOptions(), v => SaveEye("sensitivity", v));
            _eyeSens.SetQuiet(EyeVal("sensitivity", 0.5));
            _eyeSharp = new Ui.EditSlider(0, 1, v => SendOptions(), v => SaveEye("sharp", v));
            _eyeSharp.SetQuiet(EyeVal("sharp", 0.5));
            var sensName = Ui.Text("눈 감기 감도"); ToolTip.SetTip(sensName, "올리면 눈을 조금만 감아도 아바타 눈이 감겨요. 눈을 다 감아도 덜 감기면 올리고, 뜬 눈이 감겨 보이면 내려요");
            var sharpName = Ui.Text("눈 또렷하게"); ToolTip.SetTip(sharpName, "올리면 반쯤 감긴 상태가 줄고 뜨거나 감거나 둘 중 하나에 가까워져요. 0이면 트래킹 값 그대로 부드럽게");
            rows.Children.Add(Ui.PropRow(sensName, _eyeSens.Slider, null));
            rows.Children.Add(Ui.PropRow(sharpName, _eyeSharp.Slider, null));
            rows.Children.Add(Ui.PropRow(Ui.Text("눈"), _eyeLink, null));
            foreach (var (key, label, tip) in Groups)
            {
                var k = key;
                var sl = new Ui.EditSlider(0, 2, v => SendOptions(), v => { _settings.FaceGroups.Set(k, Math.Round(v, 2)); _settings.Save(); SendOptions(); });
                sl.SetQuiet(_settings.FaceGroups.Has(k) ? _settings.FaceGroups[k].AsDouble : 1);
                _groupSliders[k] = sl;
                var name = Ui.Text("감도 · " + label); ToolTip.SetTip(name, tip + " (1 = 그대로, 0 = 끔)");
                rows.Children.Add(Ui.Expert(Ui.PropRow(name, sl.Slider, null)));
            }
            detail.Body.Children.Add(Ui.Card(rows));
            _trackOn.Children.Add(detail);
            ToolTip.SetTip(_mirror, "켜면 내 왼쪽 눈을 감을 때 아바타의 오른쪽 눈이 감겨요 (거울을 보는 것처럼)");
            ToolTip.SetTip(_headOn, "고개를 돌리거나 끄덕이면 아바타의 목·머리도 따라 움직여요 (웹캠, 또는 머리 본을 보내는 외부 앱)");
            _headOn.IsChecked = _settings.FaceHead;
            _headOn.IsCheckedChanged += (_, __) => { _settings.FaceHead = _headOn.IsChecked == true; _settings.Save(); SendOptions(); };
            ToolTip.SetTip(_moveOn, "카메라 안에서 머리를 좌우로 옮기면 아바타가 몸을 그쪽으로 기울이고, 위아래로 옮기면 몸이 같이 올라가고 내려가요 (웹캠). 기준 자리는 캘리브레이션 때의 자리예요");
            _moveOn.IsChecked = _settings.FaceMove;
            _moveOn.IsCheckedChanged += (_, __) => { _settings.FaceMove = _moveOn.IsChecked == true; _settings.Save(); SendOptions(); };
            ToolTip.SetTip(_bodyOn, "고개를 돌리면 가슴·어깨가 조금 늦게 따라와요 (머리가 가리키는 방향은 같아요)");
            ToolTip.SetTip(_armsOn, "트래킹하는 동안 T자로 벌린 팔을 2D 방송용 아바타처럼 자연스럽게 내리고, 가슴이 숨쉬듯 조금 움직여요. 자세를 불러와 팔이 이미 내려가 있으면 그대로 둬요");
            ToolTip.SetTip(_eyeLink, "두 눈 값이 비슷하면 같이 감아 한쪽만 반쯤 감기는 것을 막아요. 끄면 두 눈을 따로");
            _bodyOn.IsChecked = _settings.FaceBody; _armsOn.IsChecked = _settings.FaceArms;
            _eyeLink.IsChecked = !_settings.FaceEye.Has("link") || _settings.FaceEye["link"].AsBool;
            ToolTip.SetTip(_smooth.Slider, "클수록 얼굴·고개가 느리고 부드럽게 따라와요. 웹캠이 끊겨 보이면 올려요");
            ToolTip.SetTip(_fill, "웹캠 트래킹이 초당 몇 장 안 될 때 결과 사이를 화면 프레임마다 이어 붙여 부드럽게 보이게 해요 (조금 늦게 따라와요)");
            _fill.IsChecked = _settings.FaceFill;
            _fill.IsCheckedChanged += (_, __) => { _settings.FaceFill = _fill.IsChecked == true; _settings.Save(); SendOptions(); };
            _bodyOn.IsCheckedChanged += (_, __) => { _settings.FaceBody = _bodyOn.IsChecked == true; _settings.Save(); SendOptions(); };
            _armsOn.IsCheckedChanged += (_, __) => { _settings.FaceArms = _armsOn.IsChecked == true; _settings.Save(); SendOptions(); };
            _eyeLink.IsCheckedChanged += (_, __) => { _settings.FaceEye.Set("link", _eyeLink.IsChecked == true); _settings.Save(); SendOptions(); };
            ToolTip.SetTip(_source, "웹캠(이 프로그램)은 따로 설치할 것이 없지만 정확도가 외부 앱보다 낮아요 (비권장). 아이폰(iFacialMocap·Waidayo)이나 전용 앱의 VMC 보내기를 권해요");
            _status.TextWrapping = TextWrapping.Wrap;
            // 단추: 트래킹 중에만 쓰는 것(고정·녹화·캘리브레이션)은 켰을 때만, 저장·연결 고치기는 늘
            var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _freeze = Ui.Button("지금 표정으로 고정", Freeze, "트래킹으로 만든 지금 얼굴을 표정으로 기록해요 (트래킹을 꺼도 남아요, 실행 취소 가능)", Glyph.Pin);
            tools.Children.Add(_freeze);
            _capture = Ui.Button("트래킹 녹화", ToggleCapture, "트래킹으로 움직이는 얼굴을 녹화해 Unity 애니메이션(.anim)으로 저장해요 (최대 10분)", Glyph.Record);
            tools.Children.Add(_capture);
            _calibrate = Ui.Button("캘리브레이션", Calibrate, "힘을 빼고 편한 자리에서 카메라를 정면으로 1.5초 보세요. 그때의 얼굴·고개 방향·머리 자리를 기준(0)으로 삼아 가만히 있어도 눈이 반쯤 감기거나 입이 벌어져 보이는 것, 몸이 기울어 보이는 것을 고쳐요. 기준은 저장되어 다음에도 쓰여요", Glyph.Emoji);
            tools.Children.Add(_calibrate);
            _calClear = Ui.Button("기준 지우기", ClearCalibration, "캘리브레이션 기준을 지우고 처음처럼 써요", Glyph.Reset);
            tools.Children.Add(Ui.Expert(_calClear));
            _trackOn.Children.Insert(0, tools);
            _body.Children.Add(_trackOn);

            // ---- 말하기 → 입 모양 ----
            _body.Children.Add(Ui.SectionTitle("말하기", "마이크 소리의 크기와 모음(아·이·우·에·오)을 이 PC 안에서 계산해 입을 움직입니다 (소리는 저장·전송하지 않습니다). 페이셜 트래킹과 따로 켜고, 함께 켜면 큰 쪽으로 합칩니다.\n"
                + "입 모양 BlendShape(あいうえお·vrc.v_*)가 있으면 그것을, 없으면(Perfect Sync·FT 설정) 입 벌림·오므림 값으로 움직입니다."));
            var lipRows = new StackPanel();
            lipRows.Children.Add(Ui.PropRow(Ui.Text("입"), _lipOn, null));
            var micRefresh = Ui.IconButton(Glyph.Refresh, () => _ctx.Render?.Send(Msg.Make(Msg.FaceTracking).Set("mics", true)), "마이크 목록 새로고침");
            micRefresh.Margin = new Thickness(6, 0, 0, 0);
            var micBox = new DockPanel();
            DockPanel.SetDock(micRefresh, Dock.Right);
            micBox.Children.Add(micRefresh); micBox.Children.Add(_mic);
            _lipGain = new Ui.EditSlider(0.25, 4, v => SendLip(false), v => { _settings.LipGain = Math.Round(v, 2); _settings.Save(); SendLip(false); });
            _lipGain.SetQuiet(_settings.LipGain);
            var gainName = Ui.Text("감도"); ToolTip.SetTip(gainName, "올리면 작은 목소리에도 입이 크게 벌어져요. 가만히 있어도 입이 움직이면 내려요");
            _lipRows.Children.Add(Ui.PropRow(Ui.Text("마이크"), micBox, null));
            _lipRows.Children.Add(Ui.PropRow(gainName, _lipGain.Slider, null));
            _lipStatus.TextWrapping = TextWrapping.Wrap;
            _lipRows.Children.Add(Ui.PropRow(Ui.Text("상태"), _lipStatus, null));
            lipRows.Children.Add(_lipRows);
            _lipRows.IsVisible = false;
            _body.Children.Add(Ui.Card(lipRows));
            _lipOn.IsCheckedChanged += (_, __) => { if (!_quiet) SendLip(true); };
            _mic.SelectionChanged += (_, __) =>
            {
                if (_quiet || _mic.SelectedItem is not string m) return;
                _settings.LipMic = m; _settings.Save();
                if (_lipOn.IsChecked == true) SendLip(true);
            };

            var always = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            always.Children.Add(Ui.Button("지금 표정 .anim으로 저장…", SaveExpressionAnim, "지금 표정(트래킹에서 고정한 것 포함)을 Unity 애니메이션으로 저장해요. 아바타 루트 기준 경로라 FX 레이어 등에 바로 쓸 수 있어요", Glyph.Save));
            always.Children.Add(Ui.Button("연결 직접 고치기…", OpenFaceMap, "트래킹 값마다 움직일 블렌드쉐이프를 직접 정해요 (자동 연결 끄기·세기·더하기, 값 하나씩 시험)", Glyph.Edit));
            _body.Children.Add(always);
            ShowSourceRows();


            _source.SelectionChanged += (_, __) => { if (!_quiet) Restart(); TrackingChanged?.Invoke(); };
            _status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) TrackingChanged?.Invoke(); };
            _capture.PropertyChanged += (_, e) => { if (e.Property == ContentControl.ContentProperty) TrackingChanged?.Invoke(); };
            _camera.SelectionChanged += (_, __) => { if (!_quiet && _source.SelectedIndex == 1) Restart(); };
            _mirror.IsCheckedChanged += (_, __) => SendOptions();

            _ctx.DocumentOpened += () =>
            {
                _faceMap?.Close();
                if (_ctx.Doc != null) _ctx.Doc.Changed += a => { if (a.Contains(Addr.Expression)) Refresh(); if (a.Contains(Addr.ExpressionKeys)) RefreshKeys(); };
                Rebuild();
            };
            _ctx.RendererMessage += OnRenderer;
            Rebuild();
        }

        Document? Doc => _ctx.Doc;
        Window? Owner => TopLevel.GetTopLevel(this) as Window;

        // ---------------- 컨트롤 보드(OverlayBoard)에서 쓰는 것 ----------------
        /// <summary>트래킹 소스·상태 글·녹화 상태가 바뀌면.</summary>
        public event Action? TrackingChanged;
        /// <summary>0 끔 · 1 웹캠 · 2 외부 앱. 바꾸면 이 페이지의 트래킹 칸과 같이 켜고 끈다.</summary>
        public int TrackingSource { get => _source.SelectedIndex; set { if (value != _source.SelectedIndex) _source.SelectedIndex = value; } }
        public string TrackingStatus => _status.Text ?? "";
        /// <summary>말하기(립싱크) 켜고 끄기 — 이 페이지의 체크와 같다.</summary>
        public bool LipOn { get => _lipOn.IsChecked == true; set => _lipOn.IsChecked = value; }
        public void CalibrateNow() => Calibrate();
        /// <summary>표정 빼기 (지금 표정이 있으면).</summary>
        public void ClearExpression() { if (Doc != null && !Doc.Get(Addr.Expression).IsNull) Doc.Reset(Addr.Expression, "표정 빼기"); }

        void Rebuild()
        {
            _avatarList.Children.Clear();
            var s = Doc?.Original;
            if (s == null) return;
            if (s.Expressions.Count == 0)
                _avatarList.Children.Add(Ui.Wrap("이 아바타에서 표정 애니메이션을 찾지 못했어요 (VRChat 아바타 디스크립터의 FX 레이어에 블렌드쉐이프 애니메이션이 없거나, 예전에 가져온 파일). 아래에서 .anim을 불러와 쓸 수 있어요."));
            foreach (var g in s.Expressions.GroupBy(e => FaceExpression.GroupLabel(e.Layer)))
            {
                var wrap = new WrapPanel();
                foreach (var e in g)
                {
                    var b = Ui.Button(e.Name, () => Toggle(FaceExpression.FromIr(Doc!.Original, e), "표정: " + e.Name), $"{e.Layer} · {e.Clip} · 블렌드쉐이프 {e.Shapes.Count}개\n오른쪽 클릭: 단축키 F1~F9에 넣기");
                    b.ContextFlyout = KeyMenu(() => FaceExpression.FromIr(Doc!.Original, e));
                    wrap.Children.Add(b);
                }
                var label = Ui.Text(g.Key, 12, FontWeight.Normal, Ui.KText2); label.Margin = new Thickness(2, 6, 0, 2);
                _avatarList.Children.Add(label);
                _avatarList.Children.Add(wrap);
            }
            RefreshAnims();
            Refresh();
            RefreshKeys();
        }

        // ---------------- 단축키 F1~F9 ----------------
        /// <summary>오른쪽 클릭 메뉴: F1~F9에 넣기 (지금 그 칸에 든 표정 이름도 보인다).</summary>
        MenuFlyout KeyMenu(Func<JNode?> make)
        {
            var menu = new MenuFlyout();
            menu.Opening += (_, __) =>
            {
                menu.Items.Clear();
                for (int i = 0; i < ToggleSet.MaxKeys; i++)
                {
                    int slot = i;
                    var cur = Doc == null ? JNode.Null : ToggleSet.ExpressionKey(Doc, slot);
                    var mi = new MenuItem { Header = $"F{slot + 1}에 넣기" + (cur.IsNull ? "" : $"  (지금: {FaceExpression.Name(cur)})") };
                    mi.Click += (_, __) =>
                    {
                        if (Doc == null || make() is not JNode e) { _ctx.Status("넣을 표정이 없어요."); return; }
                        ToggleSet.SetExpressionKey(Doc, slot, e);
                        _ctx.Status($"F{slot + 1}을 누르면 {FaceExpression.Name(e)} 표정이에요 (다시 누르면 빼요).");
                    };
                    menu.Items.Add(mi);
                }
            };
            menu.Items.Add(new MenuItem { Header = "F1" });   // Opening에서 채운다 (빈 메뉴는 열리지 않는다)
            return menu;
        }

        void RefreshKeys()
        {
            _keyList.Children.Clear();
            if (Doc == null) return;
            var keys = Doc.Get(Addr.ExpressionKeys);
            var rows = new StackPanel();
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].IsNull) continue;
                int slot = i;
                var name = Ui.Text(FaceExpression.Name(keys[i])); name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var key = Ui.Text($"F{slot + 1}", 13, FontWeight.SemiBold, Ui.KText2); key.Width = 36; key.VerticalAlignment = VerticalAlignment.Center;
                var clear = Ui.IconButton(Glyph.Delete, () => ToggleSet.SetExpressionKey(Doc!, slot, JNode.Null), $"F{slot + 1} 비우기");
                var g = Ui.Columns(GridLength.Auto, Ui.Star(), GridLength.Auto);
                g.MinHeight = 34;
                g.Children.Add(key.At(0)); g.Children.Add(name.At(1)); g.Children.Add(clear.At(2));
                rows.Children.Add(g);
            }
            if (rows.Children.Count == 0) return;
            _keyList.Children.Add(Ui.SectionTitle("단축키", "누르면 그 표정을 쓰고, 다시 누르면 빼요. 표정 단추를 오른쪽 클릭해 바꿔요."));
            _keyList.Children.Add(Ui.Card(rows));
        }

        void Refresh()
        {
            var e = Doc?.Get(Addr.Expression) ?? JNode.Null;
            _current.Text = e.IsNull ? "없음" : FaceExpression.Name(e) + (e["source"].AsString == "tracking" ? " · 트래킹 고정" : "");
            ToolTip.SetTip(_current, e.IsNull ? "표정 없음 (블렌드쉐이프 값 그대로)" : _current.Text);
            _resetBtn.IsEnabled = !e.IsNull;
        }

        /// <summary>같은 표정을 다시 누르면 뺀다.</summary>
        void Toggle(JNode expr, string label)
        {
            if (Doc == null) return;
            var cur = Doc.Get(Addr.Expression);
            if (!cur.IsNull && FaceExpression.Name(cur) == FaceExpression.Name(expr) && cur["source"].AsString == expr["source"].AsString) { Doc.Reset(Addr.Expression, "표정 빼기"); _ctx.Status("표정을 뺐어요."); return; }
            Doc.Set(Addr.Expression, expr, label);
            _ctx.Status($"{FaceExpression.Name(expr)} 표정 · 블렌드쉐이프 {expr["shapes"].Count}개");
        }

        // ---------------- .anim ----------------
        async void LoadAnimDialog()
        {
            var top = Owner;
            if (top == null || Doc == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("표정 애니메이션 (.anim)"), AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Unity 애니메이션") { Patterns = new[] { "*.anim" } } },
            });
            int added = 0; var problems = new List<string>();
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is not string path) continue;
                try
                {
                    var clip = AnimClip.Load(path);
                    if (!clip.Curves.Any(c => c.ClassId == 137 && c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))) { problems.Add($"{clip.Name}: 블렌드쉐이프 커브가 없어요"); continue; }
                    LoadedClips.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                    LoadedClips.Add((path, clip));
                    added++;
                    if (files.Count == 1) ApplyClip(clip);
                }
                catch (Exception e) { problems.Add($"{Path.GetFileName(path)}: {e.Message}"); }
            }
            RefreshAnims();
            if (problems.Count > 0) _ctx.Status(string.Join(" · ", problems));
            else if (added > 1) _ctx.Status($"표정 애니메이션 {added}개를 불러왔어요. 눌러서 써 보세요.");
        }

        void ApplyClip(AnimClip clip)
        {
            var expr = FaceExpression.FromClip(Doc!.Original, clip, 0, out var missing, out var count);
            if (expr["shapes"].Count == 0) { _ctx.Status($"{clip.Name}: 이 아바타에 맞는 메시가 없어요 (경로가 다른 아바타용 애니메이션)"); return; }
            Toggle(expr, "표정: " + clip.Name);
            if (missing > 0) _ctx.Status($"{clip.Name} · 이 아바타에 없는 메시 경로의 값 {missing}/{count}개는 건너뛰었어요");
        }

        void RefreshAnims()
        {
            _animList.Children.Clear();
            if (LoadedClips.Count == 0) { _animList.Children.Add(Ui.Wrap("아직 없어요. 아바타 패키지나 표정 에셋의 .anim을 불러와 보세요.")); return; }
            foreach (var (path, clip) in LoadedClips)
            {
                var b = Ui.Button(clip.Name, () => ApplyClip(clip), path + "\n오른쪽 클릭: 단축키 F1~F9에 넣기");
                b.ContextFlyout = KeyMenu(() => { var e = FaceExpression.FromClip(Doc!.Original, clip, 0, out _, out _); return e["shapes"].Count > 0 ? e : null; });
                _animList.Children.Add(b);
            }
        }

        // ---------------- 트래킹 ----------------
        string Source => _source.SelectedIndex switch { 1 => "webcam", 2 => "vmc", 3 => "osc", _ => "off" };

        /// <summary>받을 포트: 외부 앱(VMC)은 39539, 트래커 앱(OSC)은 설정 oscPort(기본 8888 = Babble 기본).</summary>
        int PortValue => Source == "osc" ? _settings.OscPort : _portValue;
        void SetPort(int p) { if (Source == "osc") { _settings.OscPort = p; _settings.Save(); } else _portValue = p; }

        /// <summary>트래킹 종류에 맞는 줄만: 카메라(웹캠)·포트(외부 앱·트래커 앱), 켰을 때만 따라가기·세부 조정·트래킹 단추.</summary>
        void ShowSourceRows()
        {
            _cameraRow.IsVisible = Source == "webcam";
            _portRow.IsVisible = Source is "vmc" or "osc";
            _port.Text = PortValue.ToString();
            _trackOn.IsVisible = Source != "off";
        }

        /// <summary>말하기 켜고 끄기(restart) 또는 감도만 보내기.</summary>
        void SendLip(bool restart)
        {
            bool on = _lipOn.IsChecked == true;
            _lipRows.IsVisible = on;
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { if (on) _lipStatus.Text = "미리보기가 켜지면 켤 수 있어요"; return; }
            var l = JNode.Obj().Set("gain", (float)_lipGain.Slider.Value);
            if (restart)
            {
                l.Set("on", on);
                if (_mic.SelectedItem is string mic) l.Set("mic", mic); else if (_settings.LipMic.Length > 0) l.Set("mic", _settings.LipMic);
                if (on && _mic.ItemCount == 0) _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("mics", true));
                _lipStatus.Text = on ? "켜는 중…" : "";
            }
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("lip", l));
        }

        void Restart()
        {
            ShowSourceRows();
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _status.Text = "미리보기가 켜지면 켤 수 있어요"; return; }
            var m = Options(Msg.Make(Msg.FaceTracking).Set("source", Source).Set("port", PortValue));
            if (_camera.SelectedItem is string cam) m.Set("camera", cam);
            m.Set("neutral", Source == "off" ? JNode.Obj() : _settings.FaceNeutral[Source].Kind == JKind.Object ? _settings.FaceNeutral[Source].Clone() : JNode.Obj());
            _ctx.Render.Send(m);
            if (Source == "webcam" && _camera.ItemCount == 0) _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("devices", true));
            _status.Text = Source == "off" ? "꺼짐" : "켜는 중…";
        }

        void SendOptions()
        {
            if (Source == "off" || _ctx.Render == null || !_ctx.Render.IsConnected) return;
            _ctx.Render.Send(Options(Msg.Make(Msg.FaceTracking)));
        }

        /// <summary>거울·부드럽게·세기·머리·부위별 감도.</summary>
        JNode Options(JNode m)
        {
            var groups = JNode.Obj();
            foreach (var kv in _groupSliders) groups.Set(kv.Key, (float)kv.Value.Slider.Value);
            return m.Set("mirror", _mirror.IsChecked == true).Set("smoothing", (float)_smooth.Slider.Value).Set("fill", _fill.IsChecked == true).Set("gain", (float)_gain.Slider.Value)
                .Set("head", _headOn.IsChecked == true).Set("groups", groups)
                .Set("body", _bodyOn.IsChecked == true).Set("arms", _armsOn.IsChecked == true).Set("move", _moveOn.IsChecked == true)
                .Set("eye", JNode.Obj().Set("sensitivity", (float)_eyeSens.Slider.Value).Set("sharp", (float)_eyeSharp.Slider.Value).Set("link", _eyeLink.IsChecked == true));
        }

        FaceMapWindow? _faceMap;

        void OpenFaceMap()
        {
            if (Doc == null) { _ctx.Status("아바타를 연 뒤 고칠 수 있어요."); return; }
            if (_faceMap != null) { _faceMap.Activate(); return; }
            _faceMap = new FaceMapWindow(_ctx, Doc);
            _faceMap.Closed += (_, __) => _faceMap = null;
            if (Owner is Window w) _faceMap.Show(w); else _faceMap.Show();
        }

        void Calibrate()
        {
            if (Source == "off" || _ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("트래킹을 켠 뒤 맞출 수 있어요."); return; }
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("calibrate", true));
            _ctx.Status("힘을 빼고 카메라를 정면으로 보세요… (1.5초)");
        }

        void ClearCalibration()
        {
            if (Source == "off") return;
            _settings.FaceNeutral.Remove(Source); _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.FaceTracking).Set("neutral", JNode.Obj()));
            _ctx.Status("캘리브레이션 기준을 지웠어요.");
        }

        void RefreshDevices()
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _status.Text = "미리보기가 켜지면 찾을 수 있어요"; return; }
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("devices", true));
            _ctx.Status("웹캠을 다시 찾는 중…");
        }

        // ---------------- .anim 저장 ----------------
        async System.Threading.Tasks.Task<string?> AskAnimPath(string name)
        {
            if (Owner is not Window w) return null;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var f = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Texts.T("표정 애니메이션 저장 (.anim)"), SuggestedFileName = name + ".anim", DefaultExtension = "anim",
                FileTypeChoices = new[] { new FilePickerFileType("Unity 애니메이션") { Patterns = new[] { "*.anim" } } },
            });
            return f?.TryGetLocalPath();
        }

        /// <summary>저장한 .anim을 목록에 넣는다 (표정 프리셋처럼 다시 누를 수 있게).</summary>
        void AddSaved(string path)
        {
            try
            {
                var clip = AnimClip.Load(path);
                LoadedClips.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                LoadedClips.Add((path, clip));
                RefreshAnims();
            }
            catch (Exception e) { _ctx.Status("저장했지만 다시 읽지 못했습니다: " + e.Message); }
        }

        async void SaveExpressionAnim()
        {
            if (Doc == null) return;
            var e = Doc.Get(Addr.Expression);
            if (e.IsNull) { _ctx.Status("저장할 표정이 없어요. 표정을 고르거나 트래킹을 \"지금 표정으로 고정\"한 뒤 저장해요."); return; }
            var tracks = AnimWriter.FromExpression(Doc.Original, e);
            if (tracks.Count == 0) { _ctx.Status("이 아바타에서 표정의 메시 경로를 찾지 못했어요."); return; }
            var name = FaceExpression.Name(e).Replace(':', '-');
            if (await AskAnimPath(name) is not string path) return;
            try { AnimWriter.Save(path, Path.GetFileNameWithoutExtension(path), tracks); }
            catch (Exception ex) { _ctx.Status("저장하지 못했습니다: " + ex.Message); return; }
            AddSaved(path);
            _ctx.Status($"표정을 저장했어요 → {path} (블렌드쉐이프 {tracks.Count}개)");
        }

        void ToggleCapture()
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 녹화할 수 있어요."); return; }
            if (!_capturing && Source == "off") { _ctx.Status("트래킹을 켠 뒤 녹화할 수 있어요."); return; }
            _capturing = !_capturing;
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("capture", _capturing));
            _capture.Content = Ui.IconLabel(_capturing ? Glyph.Stop : Glyph.Record, _capturing ? "트래킹 녹화 멈추기" : "트래킹 녹화");
            _ctx.Status(_capturing ? "얼굴 움직임을 녹화하는 중… 다시 누르면 멈추고 저장해요." : "녹화를 멈췄어요. 저장할 곳을 골라요.");
        }

        async void OnCaptured(JNode c)
        {
            _capturing = false;
            _capture.Content = Ui.IconLabel(Glyph.Record, "트래킹 녹화");
            if (Doc == null) return;
            var shapes = c["shapes"].Items.Select(x => (x["r"].AsString, x["s"].AsString)).ToList();
            var times = c["times"].Items.Select(x => x.AsFloat).ToList();
            var values = c["values"].Items.Select(x => x.ToFloatArray()).ToList();
            if (times.Count < 2 || shapes.Count == 0) { _ctx.Status("녹화한 얼굴 값이 없어요 (얼굴이 카메라에 보였는지 확인해요)."); return; }
            var tracks = AnimWriter.FromSamples(Doc.Original, shapes, times, values);
            if (tracks.Count == 0) { _ctx.Status("녹화한 동안 움직인 블렌드쉐이프가 없어요."); return; }
            if (await AskAnimPath("트래킹 " + DateTime.Now.ToString("yyyyMMdd_HHmmss")) is not string path) { _ctx.Status("녹화를 저장하지 않았어요."); return; }
            try { AnimWriter.Save(path, Path.GetFileNameWithoutExtension(path), tracks); }
            catch (Exception ex) { _ctx.Status("저장하지 못했습니다: " + ex.Message); return; }
            AddSaved(path);
            _ctx.Status($"트래킹을 저장했어요 → {path} ({times[times.Count - 1]:0.0}초, 블렌드쉐이프 {tracks.Count}개)");
        }

        void Freeze()
        {
            if (Source == "off") { _ctx.Status("트래킹을 켠 뒤 고정할 수 있어요."); return; }
            _ctx.Render?.Send(Msg.Make(Msg.FaceTracking).Set("snapshot", true));
        }

        /// <summary>연결 방식: 설치된 FT 세팅 · 퍼펙트 싱크 · MMD 쉐이프키 · 눈·입만.</summary>
        static string ModeText(JNode m) => m["mode"].AsString switch
        {
            "ft" => $" (설치된 페이셜 트래킹 설정 · 레이어 {m["ftLayers"].AsInt}개)",
            "sync" => " (Perfect Sync)",
            "mmd" => " (MMD BlendShape)",
            _ => m["perfectSync"].AsBool ? " (Perfect Sync)" : " (눈·입만)",
        };

        void OnRenderer(JNode m)
        {
            switch (Msg.Type(m))
            {
                case Msg.Ready:
                    // 렌더러를 다시 시작하면 트래킹은 꺼져 있다
                    _quiet = true; _source.SelectedIndex = 0; _quiet = false;
                    if (_capturing) { _capturing = false; _capture.Content = Ui.IconLabel(Glyph.Record, "트래킹 녹화"); }
                    ShowSourceRows();
                    _status.Text = "꺼짐";
                    _quiet = true; _lipOn.IsChecked = false; _quiet = false;
                    _lipRows.IsVisible = false; _lipStatus.Text = "";
                    break;
                case Msg.TrackingStatus:
                    if (m.Has("capture")) { OnCaptured(m["capture"]); break; }
                    // 웹캠 표정 모델은 VC++ 런타임이 필요하다 (2026-11-01, 없는 PC에서는 영어 오류만 보였다)
                    if (m.Has("ok") && !m["ok"].AsBool && m["source"].AsString == "webcam" && m["text"].AsString.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
                        m.Set("text", "웹캠 표정을 켜지 못했어요. Microsoft Visual C++ 재배포 패키지(x64)를 설치한 뒤 다시 켜 주세요");
                    if (m.Has("mics"))
                    {
                        var mics = m["mics"].Items.Select(x => x.AsString).ToList();
                        _quiet = true;
                        var want = _mic.SelectedItem as string ?? _settings.LipMic;
                        _mic.ItemsSource = mics;
                        _mic.SelectedItem = mics.Contains(want) ? want : mics.FirstOrDefault();
                        _quiet = false;
                        if (mics.Count == 0) _lipStatus.Text = "마이크를 찾지 못했어요";
                    }
                    if (m["lip"].Kind == JKind.Object)
                    {
                        var lip = m["lip"];
                        _lipStatus.Text = !lip["on"].AsBool ? lip["text"].AsString
                            : lip["text"].AsString + (lip["ok"].AsBool ? $" · 소리 {(int)(lip["level"].AsFloat * 100)}%" + (lip["visemes"].AsBool ? " · 입 모양 BlendShape" : " · 입 벌림 값으로") : "");
                        _lipStatus.Fg(lip["ok"].AsBool ? Ui.KText2 : Ui.KCaution);
                        if (!lip["on"].AsBool) { _quiet = true; _lipOn.IsChecked = false; _quiet = false; _lipRows.IsVisible = true; }
                    }
                    if (m["neutral"].Kind == JKind.Object && m["source"].AsString is "webcam" or "vmc" or "osc")
                    {
                        _settings.FaceNeutral.Set(m["source"].AsString, m["neutral"].Clone()); _settings.Save();
                        _ctx.Status($"캘리브레이션했어요 (기준 값 {m["neutral"].Count}개). 다음에 켤 때도 써요.");
                        break;
                    }
                    if (m.Has("devices"))
                    {
                        var list = m["devices"].Items.Select(x => x.AsString).ToList();
                        _quiet = true;
                        var cur = _camera.SelectedItem as string;
                        _camera.ItemsSource = list;
                        _camera.SelectedItem = list.Contains(cur ?? "") ? cur : list.FirstOrDefault();
                        _quiet = false;
                        if (list.Count == 0) _status.Text = "웹캠을 찾지 못했어요";
                        else _ctx.Status($"웹캠 {list.Count}개를 찾았어요.");
                    }
                    if (m["snapshot"].AsBool)
                    {
                        if (m["shapes"].Count == 0) { _ctx.Status("고정할 얼굴 값이 아직 없어요."); break; }
                        var shapes = m["shapes"].Items.Select(x => (x["r"].AsString, x["s"].AsString, x["v"].AsFloat));
                        Doc?.Set(Addr.Expression, FaceExpression.Make("트래킹 " + DateTime.Now.ToString("HH:mm:ss"), "tracking", shapes), "표정: 트래킹 고정");
                        _ctx.Status("지금 얼굴을 표정으로 고정했어요 (실행 취소로 되돌려요).");
                        break;
                    }
                    if (m["source"].AsString == "off") { _status.Text = m["text"].AsString.Length > 0 ? m["text"].AsString : "꺼짐"; break; }
                    var text = m["text"].AsString;
                    if (m["ok"].AsBool && m["face"].AsBool)
                        text += $" · {m["fps"].AsFloat:0} fps · BlendShape {m["mapped"].AsInt}개" + ModeText(m)
                            + (m["head"].AsBool ? " · 머리 회전" : "") + (m["gazeBones"].AsBool ? " · 시선(눈 본)" : "");
                    else if (m["ok"].AsBool && m["mapped"].AsInt == 0) text += " · 이 아바타에서 움직일 BlendShape를 찾지 못했습니다";
                    _status.Text = text;
                    _status.Fg(m["ok"].AsBool ? Ui.KText2 : Ui.KCaution);
                    break;
            }
        }
    }
}
