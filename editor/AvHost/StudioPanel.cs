// "스튜디오" 작업 창 (2026-10-19 1차): 배경 불러오기 · 카메라 위치 저장(Alt+1~9) · 고해상도 촬영.
// 2차: 소품 · 심도 · 녹화 (StudioPanel.More.cs).
// 지연 로드 (2026-10-22): 시작 단추 없이, 무거운 것(배경 텍스처·소품·심도)은 쓸 때만 렌더러에 올린다. 켤 때 지난 배경·소품은 자동으로 다시 올리지 않고
// "다시 불러오기"·"다시 세우기"로 올린다. "비우기"는 렌더러에서 스튜디오를 통째로 내린다. 렌더러 쪽 스튜디오 틀(머티리얼·사각형 하나)은 첫 메시지 때 만든다.
// 배경은 직접 만들지 않고 사용자가 가진 이미지만 불러온다 (360° 파노라마 PNG·JPG·HDR, 평면 이미지).
// 카메라 위치·마지막 배경·촬영 크기는 avhost.json에 기억한다 (아바타와 무관한 작업 환경이라 세션에 넣지 않는다).
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
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class StudioPanel : DockPanel
    {
        readonly EditorContext _ctx;
        readonly Settings _settings;
        readonly StackPanel _main = new StackPanel();   // 만드는 동안 모으는 곳 (끝에 탭마다 나눠 옮긴다)
        readonly TextBlock _bgState = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
        readonly ComboBox _bgMode = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "없음 (환경 그대로)", "360° 파노라마·HDRI", "평면 이미지", "단색", "그라데이션" } });
        readonly Ui.EditSlider _exposure, _rotation;
        readonly WrapPanel _cams = new WrapPanel();
        readonly ComboBox _size = Ui.CellCombo(new ComboBox());
        readonly CheckBox _transparent = new CheckBox { Content = "투명 배경 (PNG 알파)" };
        static readonly (string Label, int W, int H)[] Sizes =
        {
            ("4K 가로 3840×2160", 3840, 2160), ("4K 세로 2160×3840", 2160, 3840), ("QHD 2560×1440", 2560, 1440), ("FHD 1920×1080", 1920, 1080),
            ("정사각 2048×2048", 2048, 2048), ("세로 1080×1920", 1080, 1920), ("VRChat 썸네일 1200×900", 1200, 900),
        };
        // 바닥 (2026-10-27): 없음 · 그림자만 · 단색 바닥(기본). 스튜디오 페이지를 보는 동안만 렌더러에 깐다 (다른 페이지·방송 오버레이에는 없음)
        readonly ComboBox _floor = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "없음", "그림자만 (투명 촬영에 발밑 그림자만 남겨요)", "단색 바닥" } });
        static readonly string[] FloorModes = { "none", "shadow", "solid" };
        bool _showing, _floorOn;   // 스튜디오 페이지를 보는 중 · 렌더러에 바닥이 깔려 있음
        readonly CheckBox _embed = new CheckBox { Content = "PNG에 세션 넣기 (끌어 놓으면 같은 상태로 열기)" };
        bool _quiet;
        // 렌더러에 지금 올라가 있는 것 (Host가 보낸 것 기준 — 렌더러가 다시 시작하면 이 기준으로 다시 올린다)
        bool _studioOn, _bgLoaded, _propsLoaded;
        Action _showBgRows = () => { };
        Button _reloadBg = null!;
        string? _pendingShot;

        public StudioPanel(EditorContext ctx, Settings settings, ExpressionPanel expressions, OverlayPanel overlay, ShapeKeyPanel shapeKeys)
        {
            _ctx = ctx; _settings = settings;


            // ---- 시작한 뒤 ----
            var head = new TitleRow();
            var stop = Ui.Button("비우기", Clear, "배경·소품·심도를 미리보기에서 내려 메모리를 돌려줘요 (고른 배경·소품 목록·카메라 위치는 남아요)", Glyph.ClosePane);
            stop.Margin = new Thickness(0);
            DockPanel.SetDock(stop, Dock.Right);
            head.Children.Add(stop);
            head.Children.Add(Ui.SectionTitle("배경", "배경은 아바타 뒤에만 보이고, 조명은 환경 페이지 설정 그대로예요. 360° 파노라마는 가로:세로 2:1 등장방형 이미지를 써요."));
            _main.Children.Add(head);
            _exposure = new Ui.EditSlider(0, 4, v => Send(JNode.Obj().Set("exposure", (float)v)), v => { _settings.StudioExposure = v; _settings.Save(); Send(JNode.Obj().Set("exposure", (float)v)); });
            _rotation = new Ui.EditSlider(-180, 180, v => Send(JNode.Obj().Set("rotation", (float)v)), v => { _settings.StudioRotation = v; _settings.Save(); Send(JNode.Obj().Set("rotation", (float)v)); });
            var bg = new StackPanel();
            var pick = Ui.Button("이미지 고르기…", PickBackground, "PNG·JPG 이미지, 또는 HDR(.hdr) 파노라마", Glyph.Open);
            pick.Margin = new Thickness(0);
            bg.Children.Add(Ui.PropRow(Ui.Text("종류"), _bgMode, null));
            _reloadBg = Ui.Button("다시 불러오기", ApplyBackground, "지난번에 쓴 배경 이미지를 미리보기에 올려요", Glyph.Undo);
            _reloadBg.Margin = new Thickness(0);
            var picks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            picks.Children.Add(pick); picks.Children.Add(_reloadBg);
            var imageRow = Ui.PropRow(Ui.Text("이미지"), picks, null);
            bg.Children.Add(imageRow);
            // 밝기는 배경 이미지가 있을 때만, 돌리기는 파노라마일 때만 보인다
            var expRow = Ui.PropRow(Ui.Text("밝기"), _exposure.Slider, null);
            var rotRow = Ui.PropRow(Ui.Text("돌리기"), _rotation.Slider, null);
            // 그라데이션: 위·아래 색 (#RRGGBB)
            TextBox top = null!, bottom = null!;
            void SetGrad() { if (Hex(top.Text) != null && Hex(bottom.Text) != null) { _settings.StudioBgTop = top.Text!.Trim(); _settings.StudioBgBottom = bottom.Text!.Trim(); _settings.Save(); ApplyBackground(); } }
            top = Ui.Cell(_ => SetGrad()); top.Text = _settings.StudioBgTop;
            bottom = Ui.Cell(_ => SetGrad()); bottom.Text = _settings.StudioBgBottom;
            ToolTip.SetTip(top, "#RRGGBB"); ToolTip.SetTip(bottom, "#RRGGBB");
            _syncUi.Add(() => { top.Text = _settings.StudioBgTop; bottom.Text = _settings.StudioBgBottom; });
            var topRow = Ui.PropRow(Ui.Text("위 색"), top, null);
            var bottomRow = Ui.PropRow(Ui.Text("아래 색"), bottom, null);
            // 단색 (2026-11-03): 색 하나
            TextBox solid = null!;
            solid = Ui.Cell(_ => { if (Hex(solid.Text) != null) { _settings.StudioBgColor = solid.Text!.Trim(); _settings.Save(); ApplyBackground(); } });
            solid.Text = _settings.StudioBgColor;
            ToolTip.SetTip(solid, "#RRGGBB");
            _syncUi.Add(() => solid.Text = _settings.StudioBgColor);
            var solidRow = Ui.PropRow(Ui.Text("색"), solid, null);
            bg.Children.Add(expRow); bg.Children.Add(rotRow); bg.Children.Add(solidRow); bg.Children.Add(topRow); bg.Children.Add(bottomRow);
            bg.Children.Add(Ui.PropRow(Ui.Text("지금"), _bgState, null));
            _bgState.TextWrapping = TextWrapping.Wrap;
            BuildFloor(bg);
            _main.Children.Add(Ui.Card(bg));
            _showBgRows = () =>
            {
                expRow.IsVisible = _bgMode.SelectedIndex != 0; rotRow.IsVisible = _bgMode.SelectedIndex == 1;
                solidRow.IsVisible = _bgMode.SelectedIndex == 3;
                topRow.IsVisible = bottomRow.IsVisible = _bgMode.SelectedIndex == 4;
                imageRow.IsVisible = _bgMode.SelectedIndex is not (3 or 4);   // 없음에서도 고르면 종류를 짐작한다 (색 배경일 때만 줄째 숨김)
            };
            _bgMode.SelectionChanged += (_, __) => { _showBgRows(); if (!_quiet) ApplyBackground(); };
            BuildProps();
            int afterBg = _main.Children.Count;
            BuildAvatarOnly();

            var camHead = new TitleRow { Margin = new Thickness(0, 8, 0, 0) };
            var save = Ui.Button("지금 위치 저장", SaveCamera, "지금 카메라 위치·방향·확대를 저장해요 (Alt+1~9로 불러오기, 9개까지)", Glyph.Save);
            save.Margin = new Thickness(0);
            DockPanel.SetDock(save, Dock.Right);
            camHead.Children.Add(save);
            camHead.Children.Add(Ui.SectionTitle("카메라 위치"));
            _main.Children.Add(camHead);
            _main.Children.Add(_cams);
            BuildDof();
            BuildQuality();

            _main.Children.Add(Ui.SectionTitle("촬영"));
            _size.ItemsSource = Sizes.Select(s => s.Label).ToList();
            int si = Array.FindIndex(Sizes, s => s.W == _settings.ShotWidth && s.H == _settings.ShotHeight);
            _size.SelectedIndex = si >= 0 ? si : 0;
            _size.SelectionChanged += (_, __) =>
            {
                var s = Sizes[Math.Max(0, _size.SelectedIndex)]; _settings.ShotWidth = s.W; _settings.ShotHeight = s.H; _settings.Save();
                _ctx.Render?.Send(Msg.Make(Msg.SetView).Set("frameAspect", (double)s.W / s.H));   // 구도 안내선의 틀 비율
            };
            _transparent.IsChecked = _settings.ShotTransparent;
            _syncUi.Add(() => _transparent.IsChecked = _settings.ShotTransparent);
            _transparent.IsCheckedChanged += (_, __) => { _settings.ShotTransparent = _transparent.IsChecked == true; _settings.Save(); };
            var shot = new StackPanel();
            shot.Children.Add(Ui.PropRow(Ui.Text("크기"), _size, null));
            shot.Children.Add(Ui.PropRow(Ui.Text("배경"), _transparent, null));
            _embed.IsChecked = _settings.ShotEmbedSession;
            _embed.IsCheckedChanged += (_, __) => { _settings.ShotEmbedSession = _embed.IsChecked == true; _settings.Save(); };
            shot.Children.Add(Ui.PropRow(Ui.Text("세션"), _embed, null));
            _syncUi.Add(() =>
            {
                // 목록에 없는 크기는 칸을 그대로 둔다 (촬영은 칸의 크기를 쓴다)
                int k = Array.FindIndex(Sizes, s => s.W == _settings.ShotWidth && s.H == _settings.ShotHeight);
                if (k >= 0) _size.SelectedIndex = k;
                _embed.IsChecked = _settings.ShotEmbedSession;
            });
            AddAdjustRows(shot);
            _main.Children.Add(Ui.Card(shot));
            var shoot = Ui.WideButton("촬영…", Shoot, "지금 카메라로 고른 크기의 PNG를 저장해요 (보조 표시·핸들은 들어가지 않아요)", Glyph.Camera, accent: true);
            shoot.Margin = new Thickness(0, 10, 0, 0);
            _main.Children.Add(shoot);
            int afterCam = _main.Children.Count;
            BuildSticker();
            int afterSticker = _main.Children.Count;
            BuildRecord();

            // 탭 (사용성, 2026-10-21): 촬영·표정·방송을 한 페이지에 길게 이어 두어 원하는 것을 찾으려면 한참 내려야 했다.
            // 2차 (페이지 안 탭): 하는 일마다 탭 하나 — 배경·소품 · 카메라·촬영 · 스티커 · 녹화 · 표정·트래킹 · 단축키 · 오버레이.
            var parts = _main.Children.ToList();
            _main.Children.Clear();
            Control Part(int from, int to)
            {
                var p = new StackPanel();
                for (int k = from; k < to; k++)
                    p.Children.Add(parts[k] is Ui.FoldSection f ? f.Flatten() : parts[k]);   // 탭 하나에 한 가지라 접지 않는다
                return p;
            }
            _tabs.Add("bg", "배경·소품", Part(0, afterBg));
            _tabs.Add("camera", "카메라·촬영", Part(afterBg, afterCam));
            _tabs.Add("sticker", "스티커", Part(afterCam, afterSticker));
            _tabs.Add("record", "녹화", Part(afterSticker, parts.Count));
            _tabs.Add("face", "표정·트래킹", expressions);
            var keysTab = new StackPanel();
            keysTab.Children.Add(Ui.SectionTitle("블렌드쉐이프 단축키", "홍조·얼굴 이펙트·눈동자 모양처럼 블렌드쉐이프 값을 단축키로 바꿔요. 아바타 기능 메뉴에 있으면 가져오고, 없으면 직접 정해요. 단축키는 게임 창에 포커스가 있어도 눌려요. 누른 결과는 미리보기·방송 전용이에요."));
            keysTab.Children.Add(shapeKeys);
            _tabs.Add("keys", "단축키", keysTab);
            var overlayTab = new StackPanel();
            overlayTab.Children.Add(Ui.SectionTitle("방송 오버레이 (게임 위에 아바타)"));
            overlayTab.Children.Add(overlay);
            _tabs.Add("overlay", "오버레이", overlayTab);
            BuildPresets();
            DockPanel.SetDock(_presetSection!, Dock.Top);
            Children.Add(_presetSection!);
            Children.Add(_tabs);

            _ctx.RendererMessage += OnRenderer;
            RefreshCameras();
            ShowBgState();
        }

        // ---------------- 탭 ----------------
        readonly SubTabs _tabs = new SubTabs("studio", "스튜디오");
        public static readonly string[] TabKeys = { "bg", "camera", "sticker", "record", "face", "keys", "overlay" };

        /// <summary>탭 보이기 (키는 TabKeys).</summary>
        public void ShowTab(string key) => _tabs.Select(key);

        void Send(JNode m)
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 해요."); return; }
            if (!m.Has("t")) m.Set("t", Msg.Studio);   // 녹화 명령(t = record)은 그대로 보낸다 — 덮어쓰면 녹화가 시작·멈춤되지 않는다
            if (!_studioOn && m["t"].AsString == Msg.Studio)
            {
                // 처음 쓸 때 렌더러에 스튜디오 틀을 만든다 (가벼움 — 배경·소품은 따로 보낸 것만 올라간다)
                m.Set("enabled", true).Set("exposure", (float)_settings.StudioExposure).Set("rotation", (float)_settings.StudioRotation);
                _studioOn = true;
            }
            _ctx.Render.Send(m);
        }

        /// <summary>녹화처럼 렌더러 스튜디오가 있어야 하는 일 앞에 부른다.</summary>
        void EnsureStudio() { if (!_studioOn) Send(JNode.Obj()); }

        /// <summary>렌더러가 다시 시작했을 때: 올라가 있던 것만 다시 올린다.</summary>
        void Restore()
        {
            _studioOn = false;
            if (_ctx.Render == null || !_ctx.Render.IsConnected) return;
            bool dof = _settings.StudioDof["enabled"].AsBool;
            _floorOn = false;
            if (!_bgLoaded && !_propsLoaded && !dof && !FloorWanted) return;
            var m = JNode.Obj();
            if (_bgLoaded && BackgroundMessage() is JNode b) m.Set("background", b);
            if (FloorWanted) { m.Set("shadowFloor", FloorMessage()); _floorOn = true; }
            if (_propsLoaded) m.Set("props", PropsMessage());
            if (dof) m.Set("dof", DofMessage());
            Send(m);
        }

        /// <summary>비우기: 렌더러에서 스튜디오를 통째로 내린다 (설정에 기억한 배경·소품 목록·카메라 위치는 남는다).</summary>
        internal void Clear()
        {
            if (_job != null) Send(JNode.Obj().Set("t", Msg.Record).Set("cancel", true));
            _quiet = true;
            _settings.StudioDof.Set("enabled", false); _settings.Save();
            _dofOn.IsChecked = false;
            _quiet = false;
            if (_dofSection != null) _dofSection.Hint = "꺼짐";
            _dofState.Text = "꺼짐";
            if (_studioOn && _ctx.Render != null && _ctx.Render.IsConnected) _ctx.Render.Send(Msg.Make(Msg.Studio).Set("enabled", false));
            _studioOn = _bgLoaded = _propsLoaded = _floorOn = false;
            RefreshProps();
            ShowBgState();
            SendFloor();   // 바닥은 가벼워서 페이지를 보는 동안 그대로 둔다
            _ctx.Status("스튜디오를 비웠어요. 배경·소품은 \"다시 불러오기\"·\"다시 세우기\"로 다시 올려요.");
        }

        /// <summary>배경 칸을 설정 값으로 맞추고, 아직 올리지 않았으면 안내와 "다시 불러오기" 단추를 보인다.</summary>
        void ShowBgState()
        {
            _quiet = true;
            _bgMode.SelectedIndex = _settings.StudioBgMode switch { "panorama" => 1, "image" => 2, "solid" => 3, "gradient" => 4, _ => 0 };
            _exposure.Slider.Value = _settings.StudioExposure; _rotation.Slider.Value = _settings.StudioRotation;
            _quiet = false;
            _showBgRows();
            bool flat = _settings.StudioBgMode is "gradient" or "solid";   // 이미지 없이 색만
            bool saved = flat || _settings.StudioBgMode != "none" && File.Exists(_settings.StudioBgPath);
            _reloadBg.IsVisible = saved && !_bgLoaded;
            if (_bgLoaded) return;
            _bgState.Fg(Ui.KText2);
            _bgState.Text = !saved && _settings.StudioBgMode != "none" ? $"이미지를 찾지 못했어요 · {Path.GetFileName(_settings.StudioBgPath)}" : !saved ? "없음 (환경 설정의 배경)" : _settings.StudioBgMode == "solid" ? "단색 — 아직 올리지 않았어요" : _settings.StudioBgMode == "gradient" ? "그라데이션 — 아직 올리지 않았어요" : $"지난 배경 {Path.GetFileName(_settings.StudioBgPath)} — 아직 불러오지 않았어요";
        }

        string ModeOf(int i) => i switch { 1 => "panorama", 2 => "image", 3 => "solid", 4 => "gradient", _ => "none" };

        /// "#RRGGBB" → [r, g, b] 0~1 (감마 값), 아니면 null
        static float[]? Hex(string? s)
        {
            var t = (s ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
            return new[] { ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f };
        }

        /// 지금 설정의 배경 메시지 (이미지 파일이 없으면 null)
        JNode? BackgroundMessage()
        {
            var mode = _settings.StudioBgMode;
            if (mode == "solid")   // 렌더러에는 위·아래가 같은 그라데이션 (Shoot 장면의 "#RRGGBB" 배경과 같음)
            {
                var c = Hex(_settings.StudioBgColor) ?? new[] { 1f, 1f, 1f };
                return JNode.Obj().Set("mode", "gradient").Set("top", JNode.FloatArray(c)).Set("bottom", JNode.FloatArray(c));
            }
            if (mode == "gradient")
                return JNode.Obj().Set("mode", mode).Set("top", JNode.FloatArray(Hex(_settings.StudioBgTop) ?? new[] { 0.75f, 0.83f, 0.95f }))
                    .Set("bottom", JNode.FloatArray(Hex(_settings.StudioBgBottom) ?? new[] { 0.95f, 0.9f, 0.85f }));
            if (mode != "none" && File.Exists(_settings.StudioBgPath)) return JNode.Obj().Set("mode", mode).Set("path", _settings.StudioBgPath);
            return null;
        }

        // ---------------- 아바타만 (2026-10-27) ----------------
        // 3D 배경(소품·바닥·함께 세운 아바타·스튜디오 배경 이미지)을 무시하고 고른 단색 위에 아바타만 — 크로마키 합성·방송 캡처용.
        // 미리보기·촬영·녹화 모두 같은 카메라 설정을 쓴다 (렌더러 ViewAids.AvatarOnly). 미리보기 "표시" 메뉴·H 키와 같은 값
        /// <summary>MainWindow.ViewChanged — 설정을 저장하고 렌더러·메뉴에 알린다.</summary>
        public Action? ViewChanged;
        Ui.Switch? _avatarOnly;
        TextBox? _avatarOnlyHex;

        static readonly (string Label, string Hex)[] KeyColors = { ("초록", "#00FF00"), ("파랑", "#0000FF"), ("마젠타", "#FF00FF"), ("흰색", "#FFFFFF"), ("검정", "#000000") };

        void BuildAvatarOnly()
        {
            _avatarOnly = new Ui.Switch(v => { _settings.ViewAvatarOnly = v; ViewChanged?.Invoke(); });
            _avatarOnly.SetQuiet(_settings.ViewAvatarOnly);
            void SetColor(string hex)
            {
                if (Settings.HexColor(hex) == null) return;
                _settings.AvatarOnlyColor = hex.Trim().ToUpperInvariant();
                ViewChanged?.Invoke();
            }
            _avatarOnlyHex = Ui.Cell(t => SetColor(t));
            _avatarOnlyHex.Text = _settings.AvatarOnlyColor;
            ToolTip.SetTip(_avatarOnlyHex, "#RRGGBB");
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0, 0, 0) };
            foreach (var (label, hex) in KeyColors)
            {
                var c = Settings.HexColor(hex)!;
                var b = new Button { Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromRgb((byte)(c[0] * 255), (byte)(c[1] * 255), (byte)(c[2] * 255))), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)) };
                var h = hex;
                b.Click += (_, __) => { if (_avatarOnlyHex != null) _avatarOnlyHex.Text = h; SetColor(h); };
                ToolTip.SetTip(b, label + " " + hex);
                chips.Children.Add(b);
            }
            var colorRow = new DockPanel();
            DockPanel.SetDock(chips, Dock.Right);
            colorRow.Children.Add(chips); colorRow.Children.Add(_avatarOnlyHex);
            var c2 = new StackPanel();
            c2.Children.Add(Ui.HelpRow("아바타만 보기", "소품·바닥·함께 세운 아바타·배경 이미지를 모두 빼고 아래 색 위에 지금 아바타만 보여요. 촬영·녹화도 그대로 찍혀서 크로마키로 배경을 바꿀 때 써요. 조명은 환경 설정 그대로예요 (단축키 H)", _avatarOnly.Control));
            c2.Children.Add(Ui.PropRow(Ui.Text("배경 색"), colorRow, null));
            _main.Children.Add(Ui.SectionTitle("아바타만 찍기"));
            _main.Children.Add(Ui.Card(c2));
        }

        /// <summary>메뉴·H 키로 바꾼 값을 이 카드에 맞춘다 (MainWindow.ViewChanged).</summary>
        public void ShowAvatarOnly()
        {
            _avatarOnly?.SetQuiet(_settings.ViewAvatarOnly);
            if (_avatarOnlyHex != null && !_avatarOnlyHex.IsFocused) _avatarOnlyHex.Text = _settings.AvatarOnlyColor;
        }

        // ---------------- 바닥 ----------------
        void BuildFloor(StackPanel card)
        {
            // 그림자만 (2026-10-23): 주광 그림자만 남겨 투명 촬영이면 그림자만 반투명 검정으로 찍힌다
            // 단색 (2026-10-27, 기본): 색 판이 그림자를 받고, 멀리서는 배경에 녹는다
            var strength = new Ui.EditSlider(0, 1, v => { _settings.ShotShadowStrength = v; SendFloor(force: true); }, v => { _settings.ShotShadowStrength = v; _settings.Save(); SendFloor(force: true); });
            strength.SetQuiet(_settings.ShotShadowStrength);
            TextBox color = null!;
            color = Ui.Cell(_ => { if (Hex(color.Text) != null) { _settings.StudioFloorColor = color.Text!.Trim(); _settings.Save(); SendFloor(force: true); } });
            color.Text = _settings.StudioFloorColor;
            ToolTip.SetTip(color, "#RRGGBB");
            var colorRow = Ui.PropRow(Ui.Text("바닥 색"), color, null);
            var strengthRow = Ui.PropRow(Ui.Text("그림자 진하기"), strength.Slider, null);
            void Rows() { colorRow.IsVisible = _settings.StudioFloor == "solid"; strengthRow.IsVisible = _settings.StudioFloor != "none"; }
            _floor.SelectedIndex = Math.Max(0, Array.IndexOf(FloorModes, _settings.StudioFloor));
            _floor.SelectionChanged += (_, __) =>
            {
                _settings.StudioFloor = FloorModes[Math.Max(0, _floor.SelectedIndex)]; _settings.Save();
                Rows(); SendFloor(force: true);
            };
            Rows();
            _syncUi.Add(() => { strength.SetQuiet(_settings.ShotShadowStrength); color.Text = _settings.StudioFloorColor; _floor.SelectedIndex = Math.Max(0, Array.IndexOf(FloorModes, _settings.StudioFloor)); Rows(); });
            var row = Ui.PropRow(Ui.Text("바닥"), _floor, null);
            ToolTip.SetTip(row, "아바타 발밑(높이 0)에 까는 바닥이에요. 스튜디오 페이지를 보는 동안만 보여요");
            card.Children.Add(row); card.Children.Add(colorRow); card.Children.Add(strengthRow);
        }

        bool FloorWanted => _showing && _settings.StudioFloor != "none";

        JNode FloorMessage() => JNode.Obj().Set("enabled", true).Set("mode", _settings.StudioFloor).Set("strength", (float)_settings.ShotShadowStrength)
            .Set("color", JNode.FloatArray(Hex(_settings.StudioFloorColor) ?? new[] { 0.81f, 0.81f, 0.83f }));

        /// <summary>스튜디오 페이지를 보기 시작하거나 떠날 때 (MainWindow.SendQuality — 페이지 바꿈·렌더러 준비·오버레이 켜고 끔).</summary>
        public void SetShowing(bool on) { _showing = on; SendFloor(); }

        /// 바닥을 깔거나 걷는다. force: 값이 바뀌어 깔려 있으면 다시 보낸다. 바닥이 필요 없으면 렌더러를 깨우지 않는다
        void SendFloor(bool force = false)
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) return;
            bool want = FloorWanted;
            if (want == _floorOn && !(want && force)) return;
            _floorOn = want;
            Send(JNode.Obj().Set("shadowFloor", want ? FloorMessage() : JNode.Obj().Set("enabled", false)));
        }

        async void PickBackground()
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("배경 이미지 (PNG·JPG·HDR)"),
                FileTypeFilter = new[] { new FilePickerFileType("이미지") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.hdr" } } },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
            UseBackground(path);
        }

        /// <summary>이 이미지를 배경으로 (고르기 · 창에 끌어다 놓기 2026-10-27). 배경 탭을 연다.</summary>
        internal void UseBackground(string path)
        {
            ShowTab("bg");
            _settings.StudioBgPath = path;
            // 2:1 이미지·HDR은 파노라마로 짐작한다 (고른 종류가 없을 때)
            if (_bgMode.SelectedIndex == 0) { _quiet = true; _bgMode.SelectedIndex = path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ? 1 : 2; _quiet = false; }
            ApplyBackground();
        }

        void ApplyBackground()
        {
            var mode = ModeOf(_bgMode.SelectedIndex);
            if (mode is "panorama" or "image" && !File.Exists(_settings.StudioBgPath)) { _bgState.Text = "이미지를 골라 주세요"; return; }
            _settings.StudioBgMode = mode; _settings.Save();
            _bgLoaded = mode != "none";
            _reloadBg.IsVisible = false;
            Send(JNode.Obj().Set("background", BackgroundMessage() ?? JNode.Obj().Set("mode", "none")));
        }

        // ---------------- 카메라 위치 ----------------
        void SaveCamera()
        {
            if (_ctx.Camera.IsNull) { _ctx.Status("미리보기 카메라 상태를 아직 받지 못했어요."); return; }
            var list = _settings.Cameras;
            if (list.Count >= 9) { _ctx.Status("카메라 위치는 9개까지예요. 쓰지 않는 것을 오른쪽 클릭으로 지워 주세요."); return; }
            list.Add(JNode.Obj().Set("name", $"위치 {list.Count + 1}").Set("state", _ctx.Camera.Clone()));
            _settings.Save();
            RefreshCameras();
            _ctx.Status($"지금 카메라를 위치 {list.Count}로 저장했어요 (Alt+{list.Count}).");
        }

        /// <summary>Alt+숫자: n번째(1부터) 저장한 위치로.</summary>
        public bool Recall(int n)
        {
            var list = _settings.Cameras;
            if (n < 1 || n > list.Count) return false;
            _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("state", list[n - 1]["state"]));
            _ctx.Status($"{list[n - 1]["name"].AsString}(으)로 카메라를 옮겼어요.");
            return true;
        }

        void RefreshCameras()
        {
            _cams.Children.Clear();
            var list = _settings.Cameras;
            RefreshMotions();   // 녹화의 "저장한 위치까지" 목록
            if (list.Count == 0) { _cams.Children.Add(Ui.Wrap("저장한 위치가 없어요. 미리보기에서 카메라를 맞춘 뒤 \"지금 위치 저장\"을 눌러요.")); return; }
            for (int i = 0; i < list.Count; i++)
            {
                int n = i + 1;
                var b = Ui.Button($"{list[i]["name"].AsString}  Alt+{n}", () => Recall(n), "누르면 이 위치로 · 오른쪽 클릭: 이름 바꾸기·지우기");
                var menu = new MenuFlyout();
                var del = new MenuItem { Header = "지우기" };
                del.Click += (_, __) => { var arr = JNode.Arr(); for (int k = 0; k < list.Count; k++) if (k != n - 1) arr.Add(list[k]); _settings.Cameras = arr; _settings.Save(); RefreshCameras(); };
                var over = new MenuItem { Header = "지금 카메라로 덮어쓰기" };
                over.Click += (_, __) => { if (!_ctx.Camera.IsNull) { list[n - 1].Set("state", _ctx.Camera.Clone()); _settings.Save(); _ctx.Status($"위치 {n}을(를) 지금 카메라로 바꿨어요."); } };
                menu.Items.Add(over); menu.Items.Add(del);
                b.ContextFlyout = menu;
                _cams.Children.Add(b);
            }
        }

        // ---------------- 촬영 ----------------
        async void Shoot()
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 촬영할 수 있어요."); return; }
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var s = Sizes[Math.Max(0, _size.SelectedIndex)];
            var name = (_ctx.Doc?.Original.Name is string n && n.Length > 0 ? n : "avatar") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Texts.T("촬영 저장"), SuggestedFileName = name, DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG 이미지") { Patterns = new[] { "*.png" } } },
            });
            if (file?.TryGetLocalPath() is not string path) return;
            _pendingShot = path;
            _ctx.Status($"{s.W}×{s.H}로 찍는 중… (크면 몇 초 걸려요)");
            _ctx.Render.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", s.W).Set("height", s.H).Set("transparent", _transparent.IsChecked == true).Set("supersample", s.W * s.H > 4_000_000 ? 1 : 2));
        }

        void OnRenderer(JNode m)
        {
            switch (Msg.Type(m))
            {
                case Msg.Ready:
                    // 렌더러를 다시 시작하면 스튜디오도 다시 만든다 (하던 녹화는 끝난 것으로)
                    AbortRecording();
                    Restore();
                    break;
                case Msg.RecordFrame: case Msg.RecordDone: OnRecordMessage(m); break;
                case Msg.StudioState:
                    OnPropsState(m["props"]);
                    _dofState.Text = m["focus"].AsDouble > 0 ? $"초점 {m["focus"].AsDouble:F2} m" : "꺼짐";
                    if (!m["enabled"].AsBool || (m["background"].AsString == "none" && !_bgLoaded && m["error"].AsStringOrNull == null)) { ShowBgState(); break; }
                    if (m["error"].AsStringOrNull is string err && err.Length > 0) { _bgLoaded = false; _bgState.Text = "불러오지 못했습니다: " + err; _bgState.Fg(Ui.KCaution); break; }
                    _bgState.Fg(Ui.KText2);
                    _bgState.Text = m["background"].AsString switch
                    {
                        "panorama" => $"360° 파노라마 · {Path.GetFileName(_settings.StudioBgPath)} ({m["width"].AsInt}×{m["height"].AsInt})",
                        "image" => $"평면 이미지 · {Path.GetFileName(_settings.StudioBgPath)} ({m["width"].AsInt}×{m["height"].AsInt})",
                        "gradient" when _settings.StudioBgMode == "solid" => $"단색 · {_settings.StudioBgColor}",
                        "gradient" => $"그라데이션 ·{_settings.StudioBgTop} → {_settings.StudioBgBottom}",
                        _ => "없음 (환경 설정의 배경)",
                    };
                    break;
                case Msg.CaptureDone:
                    if (OnStickerCaptured(m)) break;
                    if (_pendingShot == null || m["path"].AsString != _pendingShot) break;
                    _pendingShot = null;
                    if (m["ok"].AsBool) FinishShot(m["path"].AsString); else _ctx.Status("촬영하지 못했습니다: " + m["error"].AsString);
                    break;
            }
        }

        /// 보통 촬영을 마무리: 보정(있으면) → 세션 넣기(켜져 있으면)
        void FinishShot(string path)
        {
            bool embed = _embed.IsChecked == true;
            var doc = _ctx.Doc; var cam = _ctx.Camera;
            AdjustShot(path, embed && doc != null ? () => PngText.EmbedSession(path, doc, cam) : null);
        }
    }
}
