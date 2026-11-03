// VR 에디터 창 (PLAN 3.12, 2026-10-24 사용자 결정): VR 전용 패널을 따로 만들지 않고 지금 Host 창을 크게 키워 헤드셋 안에 띄운다.
//   - 켜지는 때: VR로 보기 중(렌더러가 장면 앱으로 보는 중) + 설정 "VR에 에디터 창 띄우기"(vrPanels, 기본 켬).
//   - 크기: 창 내용 전체를 LayoutTransformControl로 "VR 글자 크기"(vrUiScale, 기본 1.5배)만큼 키운다 — VR 중에만, 데스크톱 창도 같이 커진다.
//   - 미리보기 접기(vrFoldPreview, 기본 켬): 3D 미리보기 열을 숨기고 작업 창이 넓어진다 (VR에서는 장면을 직접 본다). 미리보기 도구 모음의 VR 단추는 그동안 제목 줄로 옮긴다.
//     Unity 창은 숨겨도 렌더러가 계속 돈다 (미리 띄우기도 보이지 않는 부모에서 돈다).
//   - 화면: VrPanelFeed가 창을 RenderTargetBitmap으로 그려 공유 메모리로 보낸다 → 렌더러 VrPanels가 SteamVR 오버레이로. 입력은 레이저 → vrPointer → 이 창에 마우스 메시지.
//   - 떠 있는 창 (②): 보이는 FloatPanel마다 화면을 따로 보낸다 (id "float.<키>"). 내용은 키우지 않고 VR 안 너비를 VR 글자 크기만큼 넓혀
//     메인 창과 같은 크기로 보이게 한다. VR 안 자리는 렌더러가 정해 알려 주면(vrPanelPlaced) 창마다 vrPanelPlaces에 저장하고 다음에 돌려준다.
//     창이 숨거나(다른 페이지) 닫히면 그 오버레이도 없앤다. 떠 있는 창 끌기·크기 바꾸기는 레이저로는 하지 않는다 (VR 안에서는 그립으로 잡아 옮긴다).
//   - VR 안 조작 (③, 렌더러 VrPanels): 잡아 옮기기·두 손 크기·붙이기(방·손목·머리)·창 아래 막대·손목 창 목록. 여기서는 창 상태(자리·배율·붙이기·투명도·숨김)를
//     창마다 vrPanelPlaces에 저장해 화면마다 돌려주고, 막대의 "닫기"(vrPanelAction close)면 떠 있는 창을 작업 창에 다시 붙인다.
//   - ④: 팝업은 Program.cs에서 OverlayPopups로 창 안에 그린다(vrOverlayPopups, 시작할 때만) → 드롭다운·메뉴도 VR에 보인다.
//     레이저로 입력 칸을 누르면 SteamVR 키보드를 띄우고(vrKeyboard), 친 글을 입력 칸에 바로 넣는다(vrKeyboardText) — 끝내면 Enter와 같게 값을 확정, 닫으면 처음 글로.
//     트리거 반 당김 커서 멈춤은 렌더러 VrPanels (vrTriggerFreeze).
//   - 열고 닫기 (2026-10-24 사용자, VRChat처럼): VR 안에서 B/Y 짧게 = 에디터 창, 길게 = 손에 붙는 빠른 설정(VrHandMenu, id "hand").
//     렌더러가 vrPanelsOpen { editor, hand }로 알려 주면 닫힌 창은 그리지 않는다 (VR로 보기를 켜면 둘 다 닫힌 채).
//   다음 단계: 잡아 옮기기·두 손 크기(③), 팝업을 창 안에(OverlayPopups)·가상 키보드(④).
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        LayoutTransformControl _uiScaleBox = null!;   // 창 내용 전체 (생성자에서 감싼다)
        Button _vrButton = null!;
        Panel? _vrButtonHome;
        int _vrButtonIndex;
        bool _vrPanelMode, _vrFolded;
        readonly Dictionary<string, VrPanelFeed> _vrFeeds = new Dictionary<string, VrPanelFeed>();
        DispatcherTimer? _vrFloatTimer;
        VrHandMenu? _vrHand;
        bool _vrEditorOpen, _vrHandOpen;
        const string VrMainId = "main";

        bool VrPanelWanted => _settings.VrPanels && _vrView && _vrStatus["state"].AsString == "ok" && _vrStatus["view"].AsBool;

        /// <summary>vrStatus·설정이 바뀔 때: VR 창 모드(배율 · 미리보기 접기 · 화면 보내기)를 켜고 끈다.</summary>
        void UpdateVrPanelMode() => SetVrPanelMode(VrPanelWanted);

        void SetVrPanelMode(bool want)
        {
            if (want == _vrPanelMode) return;
            _vrPanelMode = want;
            ApplyVrUiScale();
            ApplyVrFold();
            if (want)
            {
                _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", "*").Set("faceMe", _settings.VrPanelFaceMe).Set("freeze", _settings.VrTriggerFreeze));
                _vrHand?.Close(); _vrCamMenu?.Close();
                _vrEditorOpen = _vrHandOpen = false;   // VR 안에서 B/Y로 연다
                StartVrFeed(VrMainId, this, () => _settings.VrPanelMeters);
                _vrHand = new VrHandMenu(this, VrHandItems(), VrHandHead);
                _vrHand.Show();
                StartVrFeed("hand", _vrHand, () => 0.05 * _vrHand.Columns);   // 칸 하나 5 cm
                _vrCamMenu = new VrCameraMenu(this, () => _settings, () => _vrCamState, VrCameraAct, SetVrCameraFov);
                _vrCamMenu.Show();
                StartVrFeed("camera", _vrCamMenu, () => 0.12);   // 카메라 몸체(14 cm) 옆에 12 cm
                ApplyVrOpen();
                SyncVrFloats();
                _vrFloatTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, __) => SyncVrFloats());
                _vrFloatTimer.Start();
                SetStatus("VR 안에서 B(또는 Y)를 짧게 누르면 에디터 창, 길게 누르면 손에 빠른 설정이 나와요. 레이저로 가리키고 트리거로 눌러요.");
            }
            else
            {
                _vrFloatTimer?.Stop();
                foreach (var f in _vrFeeds.Values) f.Dispose();
                _vrFeeds.Clear();
                _vrHand?.Close(); _vrHand = null;
                _vrCamMenu?.Close(); _vrCamMenu = null;
            }
        }

        VrPanelFeed StartVrFeed(string id, Window win, Func<double> meters)
        {
            var feed = new VrPanelFeed(win, id, m => _ctx.Render?.Send(m), AppendLog)
            {
                Meters = meters,
                Density = () => 2048.0 / Math.Max(0.3, _settings.VrPanelMeters),   // 메인 창과 같은 m당 픽셀
                State = () => _settings.VrPanelPlaces[id] is var s && s.Kind == JKind.Object && s["pos"].Count == 3 ? s : null,
            };
            _vrFeeds[id] = feed;
            return feed;
        }

        static string VrFloatId(FloatPanel f) => "float." + string.Concat(f.Key.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));

        /// <summary>떠 있는 창: VR 안 너비 = 창 너비 × VR 글자 크기 × (메인 창 m ÷ 메인 창 너비) — 메인 창의 키운 글자와 같은 크기로 보인다.</summary>
        double VrFloatMeters(FloatPanel f) => ClientSize.Width > 0 ? f.ClientSize.Width * Math.Clamp(_settings.VrUiScale, 1, 2) * _settings.VrPanelMeters / ClientSize.Width : 0.4;

        /// <summary>보이는 떠 있는 창마다 화면을 보낸다 (0.3초마다 맞춘다: 새로 뜬 창은 더하고, 숨거나 닫힌 창은 뺀다).</summary>
        void SyncVrFloats()
        {
            if (!_vrPanelMode) return;
            var want = new Dictionary<string, FloatPanel>();
            foreach (var f in _floats.Floats) if (f.IsVisible) want[VrFloatId(f)] = f;
            foreach (var id in _vrFeeds.Keys.Where(k => k.StartsWith("float.") && !want.ContainsKey(k)).ToList()) { _vrFeeds[id].Dispose(); _vrFeeds.Remove(id); }
            foreach (var (id, f) in want)
                if (!_vrFeeds.TryGetValue(id, out var feed) || feed.Window != f)
                {
                    feed?.Dispose();
                    StartVrFeed(id, f, () => VrFloatMeters(f)).Paused = !_vrEditorOpen;
                }
        }

        /// <summary>렌더러가 VR 안 창 상태(자리·배율·붙이기·투명도·숨김)를 바꿨을 때: 창마다 저장 (다음에 띄울 때 그대로).</summary>
        void OnVrPanelPlaced(JNode m)
        {
            var id = m["id"].AsString;
            var s = m["state"];
            if (id.Length == 0 || s.Kind != JKind.Object || s["pos"].Count != 3 || s["rot"].Count != 4) return;
            _settings.VrPanelPlaces.Set(id, s.Clone());
            _settings.Save();
        }

        /// <summary>VR 창 아래 막대에서 누른 것: 닫기 = 떠 있는 창을 작업 창에 다시 붙이기 (창의 닫기 단추와 같다).</summary>
        void OnVrPanelAction(JNode m)
        {
            if (m["action"].AsString != "close") return;
            var f = _floats.Floats.FirstOrDefault(x => VrFloatId(x) == m["id"].AsString);
            if (f == null) return;
            f.RequestDock();
            SetStatus($"\"{f.Title}\" 창을 닫았어요 (작업 창에 다시 붙어요).");
        }

        void SetVrPanelFaceMe(bool on)
        {
            _settings.VrPanelFaceMe = on; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", "*").Set("faceMe", on));
            SetStatus(on ? "VR 창을 놓으면 나를 보게 돌려요." : "VR 창을 놓으면 기울기만 없애고 방향은 그대로 둬요.");
        }

        void SetVrTriggerFreeze(bool on)
        {
            _settings.VrTriggerFreeze = on; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", "*").Set("freeze", on));
            SetStatus(on ? "트리거를 반쯤 당기면 레이저 커서가 멈춰요 (끝까지 당기면 그 자리를 눌러요)." : "트리거 반 당김 커서 멈춤을 껐어요.");
        }

        void SetVrOverlayPopups(bool on)
        {
            _settings.VrOverlayPopups = on; _settings.Save();
            SetStatus(on ? "팝업을 창 안에 그려요 — 프로그램을 다시 시작하면 적용돼요." : "팝업을 따로 뜬 창으로 그려요 — 다시 시작하면 적용돼요 (VR·창 공유에는 목록이 안 보여요).");
        }

        /// <summary>렌더러: VR 안에서 에디터 창·빠른 설정이 열리고 닫힘 → 닫힌 창은 그리지 않는다.</summary>
        void OnVrPanelsOpen(JNode m)
        {
            _vrEditorOpen = m["editor"].AsBool; _vrHandOpen = m["hand"].AsBool;
            ApplyVrOpen();
        }

        void ApplyVrOpen()
        {
            foreach (var (id, f) in _vrFeeds) f.Paused = id == "hand" ? !_vrHandOpen : id == "camera" ? !_settings.VrCamera : !_vrEditorOpen;
            _vrHand?.Refresh(); _vrCamMenu?.Refresh();
        }

        void VrToggleEditor()
        {
            _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", "*").Set("open", !_vrEditorOpen));
            SetStatus(_vrEditorOpen ? "VR 에디터 창을 닫았어요 (B 짧게로 다시 열어요)." : "VR 에디터 창을 열었어요.");
        }

        string VrHandHead() => "빠른 설정";

        /// <summary>빠른 설정 칸 (4열): 자리 맞추기 · 거울 · 개인 거울 · 에디터 창 / 카메라 셋 / 실행 취소 · 다시 실행 (2026-11-03 몸·크기·캘리브레이션·자세 찍기는 뺌).</summary>
        List<VrHandMenu.Item> VrHandItems() => new List<VrHandMenu.Item>
        {
            new("height", Glyph.Refresh, () => "자리 맞추기", VrRecenter),
            new("mirror", Glyph.Mirror, () => "거울", () => SetVrMirror(!_settings.VrMirror, null), () => _settings.VrMirror),
            new("personal", Glyph.Mirror, () => "개인 거울", () => SetVrMirror(null, !_settings.VrPersonalMirror), () => _settings.VrPersonalMirror),
            new("editor", Glyph.Edit, () => "에디터 창", VrToggleEditor, () => _vrEditorOpen),
            new("camera", Glyph.Camera, () => "카메라", () => SetVrCamera(!_settings.VrCamera), () => _settings.VrCamera),
            new("cameraHere", Glyph.Move, () => "카메라\n내 앞으로", VrCameraSummon),
            new("cameraLock", Glyph.Pin, () => _settings.VrCameraLock ? "카메라\n고정됨" : "카메라\n고정", () => VrCameraAct("lock"), () => _settings.VrCameraLock),
            // 자세를 손으로 다듬다가 바로 되돌리게 (2026-10-27)
            new("undo", Glyph.Undo, () => "실행 취소", Undo),
            new("redo", Glyph.Redo, () => "다시 실행", Redo),
        };

        void ApplyVrUiScale()
        {
            double s = _vrPanelMode ? Math.Clamp(_settings.VrUiScale, 1, 2) : 1;
            _uiScaleBox.LayoutTransform = s > 1.001 ? new ScaleTransform(s, s) : null;
        }

        /// <summary>미리보기 열을 접고 펴기. VR 단추는 접힌 동안 제목 줄 끝으로 (VR을 끄거나 설정을 바꿀 수 있게).</summary>
        void ApplyVrFold()
        {
            bool fold = _vrPanelMode && _settings.VrFoldPreview;
            if (fold == _vrFolded) return;
            _vrFolded = fold;
            _preview.IsVisible = !fold;
            _splitter.IsVisible = !fold;
            _fileText.IsVisible = !fold;   // 제목 줄에 VR 단추 자리 (키운 글자로는 창 단추와 겹쳤다)
            var title = _undoBtn.Parent as Panel;
            if (fold && _vrButton.Parent is Panel home && title != null)
            {
                _vrButtonHome = home; _vrButtonIndex = home.Children.IndexOf(_vrButton);
                home.Children.Remove(_vrButton);
                title.Children.Add(_vrButton);
            }
            else if (!fold && _vrButtonHome != null && _vrButton.Parent is Panel now)
            {
                now.Children.Remove(_vrButton);
                _vrButtonHome.Children.Insert(Math.Clamp(_vrButtonIndex, 0, _vrButtonHome.Children.Count), _vrButton);
                _vrButtonHome = null;
            }
            ApplyPaneSide();
        }

        void SetVrPanels(bool on)
        {
            _settings.VrPanels = on; _settings.Save();
            UpdateVrPanelMode();
            if (!on) SetStatus("헤드셋 안의 에디터 창을 껐어요.");
            else if (!_vrPanelMode) SetStatus("VR로 보기를 켜면 헤드셋 안에 에디터 창이 떠요.");
        }

        void SetVrUiScale(double s)
        {
            _settings.VrUiScale = s; _settings.Save();
            ApplyVrUiScale();
            SetStatus($"VR 글자 크기 {s * 100:0}% (VR 중에는 데스크톱 창도 같이 커져요).");
        }

        void SetVrPanelMeters(double m)
        {
            _settings.VrPanelMeters = m; _settings.Save();   // 창들이 다음 화면에서 읽는다
            SetStatus($"VR 안 에디터 창 너비 {m:0.0#} m.");
        }

        void SetVrFoldPreview(bool on)
        {
            _settings.VrFoldPreview = on; _settings.Save();
            ApplyVrFold();
        }

        void VrPanelSummon()
        {
            _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", VrMainId).Set("summon", true));
            foreach (var id in _vrFeeds.Keys.Where(k => k != VrMainId)) _ctx.Render?.Send(Msg.Make(Msg.VrPanel).Set("id", id).Set("beside", true));   // 떠 있는 창은 메인 창 옆으로
            SetStatus(_vrFeeds.Count > 1 ? "에디터 창을 내 앞으로 부르고, 떠 있는 창은 그 옆에 놓았어요." : "에디터 창을 내 앞으로 불렀어요.");
        }

        /// <summary>VR 메뉴(VR로 보기 중) 끝에 에디터 창 항목들.</summary>
        void AddVrPanelMenu()
        {
            _vrMenu.Items.Add(new Separator());
            var on = new MenuItem { Header = "VR에 에디터 창 띄우기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrPanels };
            ToolTip.SetTip(on, "이 창을 헤드셋 안에 띄워요. B(또는 Y) 짧게 = 열고 닫기, 길게 = 손에 빠른 설정. 레이저로 가리키고 트리거로 누르고, 그 손 스틱 위·아래로 스크롤해요. 창 아래 손잡이를 트리거로 끌면 옮기고(끄는 동안 스틱 위·아래 = 멀리·가까이, 좌·우 = 크기), 오른쪽 아래 모서리를 끌면 크기, 왼쪽 구석은 닫기·머리 따라오기예요. 그립으로 잡아도 옮겨요");
            on.Click += (_, __) => SetVrPanels(!_settings.VrPanels);
            _vrMenu.Items.Add(on);
            var summon = new MenuItem { Header = "에디터 창 내 앞으로 (떠 있는 창은 옆에)", IsEnabled = _vrPanelMode };
            summon.Click += (_, __) => VrPanelSummon();
            _vrMenu.Items.Add(summon);
            var scale = new MenuItem { Header = $"VR 글자 크기: {_settings.VrUiScale * 100:0}%" };
            ToolTip.SetTip(scale, "VR 중 에디터 창의 글자·단추를 키워요. 데스크톱 창도 같이 커져요");
            foreach (var v in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                var mi = new MenuItem { Header = $"{v * 100:0}%" + (v == 1.5 ? " (기본)" : ""), ToggleType = MenuItemToggleType.Radio, GroupName = "vrUiScale", IsChecked = Math.Abs(_settings.VrUiScale - v) < 0.001 };
                mi.Click += (_, __) => SetVrUiScale(v);
                scale.Items.Add(mi);
            }
            _vrMenu.Items.Add(scale);
            var width = new MenuItem { Header = $"VR 창 너비: {_settings.VrPanelMeters:0.0#} m" };
            foreach (var v in new[] { 0.7, 0.85, 1.0, 1.2, 1.5 })
            {
                var mi = new MenuItem { Header = $"{v:0.0#} m" + (v == 1.0 ? " (기본)" : ""), ToggleType = MenuItemToggleType.Radio, GroupName = "vrPanelM", IsChecked = Math.Abs(_settings.VrPanelMeters - v) < 0.001 };
                mi.Click += (_, __) => SetVrPanelMeters(v);
                width.Items.Add(mi);
            }
            _vrMenu.Items.Add(width);
            var toggle = new MenuItem { Header = _vrEditorOpen ? "VR 에디터 창 닫기 (VR 안: B 짧게)" : "VR 에디터 창 열기 (VR 안: B 짧게)", IsEnabled = _vrPanelMode };
            ToolTip.SetTip(toggle, "VR 안에서는 B(또는 Y)를 짧게 누르면 열고 닫아요 (VRChat 빠른 메뉴처럼 열 때마다 내 앞으로). 길게 누르면 손에 빠른 설정(거울·카메라 등)이 나와요");
            toggle.Click += (_, __) => VrToggleEditor();
            _vrMenu.Items.Add(toggle);
            var faceMe = new MenuItem { Header = "VR 창을 놓으면 나를 보게", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrPanelFaceMe };
            ToolTip.SetTip(faceMe, "그립으로 잡아 옮긴 창을 놓을 때 내 쪽으로 돌려 세워요. 끄면 기울기만 없애고 방향은 놓은 그대로예요");
            faceMe.Click += (_, __) => SetVrPanelFaceMe(!_settings.VrPanelFaceMe);
            _vrMenu.Items.Add(faceMe);
            var freeze = new MenuItem { Header = "트리거 반 당김 = 커서 멈춤", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrTriggerFreeze };
            ToolTip.SetTip(freeze, "트리거를 반쯤 당기면 레이저 커서가 그 자리에 멈춰서, 끝까지 당길 때 손이 떨려도 작은 단추를 정확히 눌러요. 레이저를 크게 움직이면 다시 따라와요");
            freeze.Click += (_, __) => SetVrTriggerFreeze(!_settings.VrTriggerFreeze);
            _vrMenu.Items.Add(freeze);
            var pops = new MenuItem { Header = "팝업을 창 안에 그리기 (다시 시작하면 적용)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrOverlayPopups };
            ToolTip.SetTip(pops, "드롭다운 목록·메뉴·툴팁을 따로 뜬 창이 아니라 창 안에 그려요. 켜야 VR 에디터 창과 디스코드 창 공유에 목록이 보여요. 대신 창 끝에서는 목록이 창 안으로 밀려요");
            pops.Click += (_, __) => SetVrOverlayPopups(!_settings.VrOverlayPopups);
            _vrMenu.Items.Add(pops);
            var fold = new MenuItem { Header = "VR 중 미리보기 접기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrFoldPreview };
            ToolTip.SetTip(fold, "VR 중에는 3D 미리보기 자리를 접고 작업 창을 넓게 써요 (헤드셋에서는 장면을 직접 봐요)");
            fold.Click += (_, __) => SetVrFoldPreview(!_settings.VrFoldPreview);
            _vrMenu.Items.Add(fold);
            if (_vrStatus["panelProblem"].AsString.Length > 0) _vrMenu.Items.Add(new MenuItem { Header = _vrStatus["panelProblem"].AsString, IsEnabled = false });
        }

        void OnVrPanelShown(JNode m) { if (_vrFeeds.TryGetValue(m["id"].AsString, out var f)) f.Shown(m["frame"].AsInt); }

        void OnVrPointer(JNode m) { if (_vrFeeds.TryGetValue(m["id"].AsString, out var f)) f.Pointer(m["kind"].AsString, m["u"].AsFloat, m["v"].AsFloat, m["delta"].AsFloat); }
    }

    /// <summary>
    /// 창 하나를 VR 오버레이로: 바뀌면(레이아웃·레이저 입력 뒤, 그 밖에는 0.5초마다) 창을 그려 공유 메모리에 쓰고 vrPanel을 보낸다.
    /// 렌더러가 vrPanelShown으로 받았다고 해야 다음 화면을 쓴다 (1초 안에 답이 없으면 다시). 레이저 입력은 이 창에 마우스 메시지로 바로 보낸다(SendMessage — 진짜 커서는 그대로).
    /// </summary>
    sealed class VrPanelFeed : IDisposable
    {
        /// <summary>레이저로 넣는 마우스 메시지를 처리하는 중 (제목 줄 끌기처럼 진짜 마우스를 따라가는 동작을 막는다).</summary>
        public static bool Injecting;
        /// <summary>VR 안 너비 m · m당 픽셀(없으면 너비 2048 px까지 2배) · 저장해 둔 창 상태 (화면마다 같이 보낸다 — 렌더러를 다시 띄워도 그 자리).</summary>
        public Func<double> Meters = () => 1;
        public Func<double>? Density;
        public Func<JNode?> State = () => null;
        public Window Window => _win;
        /// <summary>시험용: 마지막으로 보낸 vrPanel · vrKeyboard.</summary>
        public JNode? LastSent, LastKeyboard;
        TextBox? _kbBox;
        string _kbOriginal = "";

        readonly Window _win;
        readonly string _id;
        readonly Action<JNode> _send;
        readonly Action<string, string> _log;
        readonly DispatcherTimer _timer;
        readonly Win32Properties.CustomWndProcHookCallback _hook;
        RenderTargetBitmap? _bmp;
        MemoryMappedFile? _map;
        MemoryMappedViewAccessor? _view;
        string _mapName = "";
        long _cap;
        int _gen, _seq, _waitSeq = -1;
        DateTime _sentAt, _hotUntil, _drawnAt;
        bool _dirty = true, _moved, _inside, _down, _logged, _paused;

        /// <summary>VR 안에서 닫혀 있으면 그리지 않는다 (다시 열면 바로 그린다).</summary>
        public bool Paused { get => _paused; set { if (_paused == value) return; _paused = value; if (!value) { _dirty = true; Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background); } } }
        double _sentMeters = -1;

        const int MaxWidth = 2048, MaxSide = 4096;
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A, WM_MOUSELEAVE = 0x2A3, MK_LBUTTON = 1;

        public VrPanelFeed(Window win, string id, Action<JNode> send, Action<string, string> log)
        {
            _win = win; _id = id; _send = send; _log = log;
            _win.LayoutUpdated += MarkDirty;
            _hook = Hook;
            Win32Properties.AddWndProcHookCallback(_win, _hook);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, __) => Tick());   // 레이저로 누른 뒤 빨리 보이게 (예전 50 ms)
            _timer.Start();
        }

        void MarkDirty(object? s, EventArgs e) => _dirty = true;

        /// <summary>레이저가 창 안에 있는 동안 진짜 커서가 창 밖이라 Windows가 보내는 "마우스 나감"은 버린다 (안 버리면 누르는 중 상태가 풀린다).</summary>
        IntPtr Hook(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_MOUSELEAVE && _inside && !Injecting) handled = true;
            return IntPtr.Zero;
        }

        void Tick()
        {
            var now = DateTime.UtcNow;
            if (_paused) return;
            if (_waitSeq >= 0 && (now - _sentAt).TotalSeconds < 1) { if (_dirty) Log("tick: waiting ack " + _waitSeq); return; }   // 렌더러가 아직 올리는 중
            // 바뀜(레이아웃·누르기)은 바로, 누른 뒤 0.3초는 30 fps, 레이저를 움직이기만 하면(hover) 15 fps, 그 밖에는 0.5초마다
            double since = (now - _drawnAt).TotalSeconds;
            bool due = _dirty || (now < _hotUntil && since >= 0.033) || (_moved && since >= 0.066) || since >= 0.5 || Math.Abs(_sentMeters - Meters()) > 1e-6;
            if (!due) return;
            Log("draw start dirty=" + _dirty);
            Draw(now);
            Log("draw end seq " + _seq);
        }

        void Draw(DateTime now)
        {
            var size = _win.ClientSize;
            if (size.Width < 50 || size.Height < 50) return;
            double meters = Meters();
            double k = Density != null ? Math.Clamp(Density() * meters / size.Width, 1, 3) : Math.Min(2.0, MaxWidth / size.Width);
            int pw = Math.Min(MaxSide, (int)Math.Round(size.Width * k)), ph = Math.Min(MaxSide, (int)Math.Round(size.Height * k));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (_bmp == null || _bmp.PixelSize.Width != pw || _bmp.PixelSize.Height != ph)
            {
                _bmp?.Dispose();
                _bmp = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96 * k, 96 * k));
            }
            _bmp.Render(_win);
            long need = (long)pw * ph * 4;
            if (_map == null || _cap < need)
            {
                _view?.Dispose(); _map?.Dispose();
                _cap = need;
                _mapName = $"VrcProtoVrPanel_{Environment.ProcessId}_{_id}_{++_gen}";
                _map = MemoryMappedFile.CreateNew(_mapName, _cap, MemoryMappedFileAccess.ReadWrite);
                _view = _map.CreateViewAccessor(0, _cap, MemoryMappedFileAccess.ReadWrite);
            }
            var ptr = _view!.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)_view.PointerOffset;
            _bmp.CopyPixels(new PixelRect(0, 0, pw, ph), ptr, (int)Math.Min(int.MaxValue, _cap), pw * 4);
            _seq++;
            var msg = Msg.Make(Msg.VrPanel).Set("id", _id).Set("map", _mapName).Set("width", pw).Set("height", ph).Set("frame", _seq).Set("meters", (float)meters);
            if (State() is JNode state) msg.Set("state", state);   // 렌더러는 아직 안 놓은 창에만 쓴다 (다시 띄워도 그 자리)
            _send(LastSent = msg);
            _sentMeters = meters;
            _waitSeq = _seq; _sentAt = _drawnAt = now;
            _dirty = _moved = false;
            DrawMs = watch.Elapsed.TotalMilliseconds;
            if (!_logged) { _logged = true; _log("info", $"VR 에디터 창 {_id}: {pw}×{ph} · {meters:0.00} m · 그리기 {watch.Elapsed.TotalMilliseconds:0} ms"); }
        }

        /// <summary>시험용: 렌더러가 화면을 받았다고 답할 때마다.</summary>
        public Action<int>? ShownHook;
        /// <summary>시험용: 마지막 그리기 시간 · 렌더러 답까지 걸린 시간 (ms).</summary>
        public double DrawMs, AckMs;
        public List<string>? Trace;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        void Log(string s) { if (Trace != null && Trace.Count < 400) Trace.Add($"{Clock.ElapsedMilliseconds} {s}"); }

        public void Shown(int seq) { Log("ack " + seq); if (seq == _waitSeq) { _waitSeq = -1; AckMs = (DateTime.UtcNow - _sentAt).TotalMilliseconds; } ShownHook?.Invoke(seq); }

        /// <summary>레이저 → 이 창의 마우스 메시지 (창 안 0~1 좌표 → 클라이언트 물리 픽셀).</summary>
        public void Pointer(string kind, float u, float v, float delta)
        {
            var hwnd = _win.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            double sc = _win.RenderScaling;
            int x = (int)Math.Round(Math.Clamp(u, 0, 1) * _win.ClientSize.Width * sc), y = (int)Math.Round(Math.Clamp(v, 0, 1) * _win.ClientSize.Height * sc);
            var lp = (IntPtr)((y << 16) | (x & 0xFFFF));
            if (kind == "move") _moved = true;   // hover 모양은 조금 늦어도 된다 (그리기가 클릭 처리를 막지 않게)
            else
            {
                // 누르기·떼기·휠: 처리가 끝나면(레이아웃 뒤) 바로 그린다
                _hotUntil = DateTime.UtcNow.AddSeconds(0.3);
                _dirty = true;
                Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
            }
            Injecting = true;
            try
            {
                switch (kind)
                {
                    case "move": _inside = true; SendMessage(hwnd, WM_MOUSEMOVE, (IntPtr)(_down ? MK_LBUTTON : 0), lp); break;
                    case "down": _inside = true; _down = true; SendMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp); break;
                    case "up": Log("up"); _down = false; SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp); Dispatcher.UIThread.Post(CheckKeyboard, DispatcherPriority.Background); break;
                    case "leave": _inside = false; if (!_down) SendMessage(hwnd, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero); break;
                    case "wheel":
                        var pt = new POINT { X = x, Y = y };
                        ClientToScreen(hwnd, ref pt);
                        short d = (short)Math.Clamp(Math.Round(delta * 120), short.MinValue, short.MaxValue);
                        SendMessage(hwnd, WM_MOUSEWHEEL, (IntPtr)((d << 16) | (_down ? MK_LBUTTON : 0)), (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                        break;
                }
            }
            finally { Injecting = false; }
        }

        /// <summary>레이저로 누른 뒤: 입력 칸에 포커스가 가 있으면 SteamVR 키보드를 띄운다 (다른 곳을 누르면 닫는다).</summary>
        void CheckKeyboard()
        {
            var fe = _win.FocusManager?.GetFocusedElement();
            if (fe is TextBox tb && !tb.IsReadOnly && tb.IsEffectivelyVisible && tb.IsEffectivelyEnabled)
            {
                if (tb == _kbBox) return;
                _kbBox = tb; _kbOriginal = tb.Text ?? "";
                _send(LastKeyboard = Msg.Make(Msg.VrKeyboard).Set("id", _id).Set("show", true).Set("text", _kbOriginal)
                    .Set("multiline", tb.AcceptsReturn).Set("password", tb.PasswordChar != '\0').Set("title", tb.Watermark?.ToString() ?? ""));
            }
            else if (_kbBox != null)
            {
                _kbBox = null;
                _send(LastKeyboard = Msg.Make(Msg.VrKeyboard).Set("id", _id).Set("hide", true));
            }
        }

        /// <summary>SteamVR 키보드에서 친 글: 입력 칸에 바로 넣고, 끝내면(done) Enter와 같게 확정, 닫으면(cancel) 처음 글로.</summary>
        public void KeyboardText(string text, bool done, bool cancel)
        {
            var tb = _kbBox;
            if (tb == null) return;
            if (cancel) { tb.Text = _kbOriginal; _kbBox = null; return; }
            tb.Text = text;
            tb.CaretIndex = text.Length;
            _dirty = true; _hotUntil = DateTime.UtcNow.AddSeconds(0.4);
            if (!done) return;
            _kbBox = null;
            tb.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = tb });
            tb.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Enter, Source = tb });
        }

        public void Dispose()
        {
            if (_kbBox != null) { _kbBox = null; _send(Msg.Make(Msg.VrKeyboard).Set("id", _id).Set("hide", true)); }
            _timer.Stop();
            _win.LayoutUpdated -= MarkDirty;
            Win32Properties.RemoveWndProcHookCallback(_win, _hook);
            if (_down) Pointer("up", 0, 0, 0);
            _inside = false;
            _send(Msg.Make(Msg.VrPanel).Set("id", _id).Set("remove", true));
            _bmp?.Dispose(); _bmp = null;
            _view?.Dispose(); _map?.Dispose(); _view = null; _map = null;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    }
}
