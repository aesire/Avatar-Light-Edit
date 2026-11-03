// Avalonia Host 메인 창 (설정 화면형 레이아웃).
//   ┌ 제목 표시줄: 앱 이름 · 파일 이름 · 열기/저장/실행 취소 · 창 단추                       ┐
//   │ 사이드바(아이콘+이름) │ 작업 창 (페이지 제목 + 카드) ║ 3D 미리보기 (카메라 단추 + Unity) │
//   └ 상태 표시줄: ● 렌더러 · ● 장면 · 변경 n개 · 안내 문구                                    ┘
// 파일이 없을 때는 작업 창에 "아바타 열기" 안내를 보여 준다.
// 나머지는 partial 파일: MainWindow.Preview.cs(3D 미리보기 도구), MainWindow.Files.cs(열기·가져오기·저장·셰이더 팩).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow : Window
    {
        readonly EditorContext _ctx = new EditorContext();
        readonly UnityView _viewport = new UnityView();
        OverlayController _overlay = null!;   // 방송 오버레이 (켜고 끄기는 여기 한 곳). 닫을 때 오버레이 창을 숨긴다
        OverlayBoard? _board;                  // 컨트롤 보드 (맨 위 고정 · 방송 화면에서 숨김)
        ExpressionPanel _exprPanel = null!;
        // 블렌드쉐이프 단축키 (Core.ShapeKeys): 누른 상태(기록하지 않음) · 전역 단축키 · 스튜디오 페이지 칸
        readonly ShapeKeyRunner _shapeRun = new ShapeKeyRunner();
        GlobalHotkeys? _hotkeys;
        ShapeKeyPanel _shapePanel = null!;
        readonly TextBlock _status = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly StatusItem _rendererItem = new StatusItem("미리보기"), _sceneItem = new StatusItem("장면"), _changesItem = new StatusItem("변경 없음");
        // 장면 불러오기 진행 (렌더러 loadProgress): 상태 표시줄의 막대 + 퍼센트. 3D 미리보기는 Unity 창이라 그 위에는 그릴 수 없다
        readonly ProgressBar _loadBar = new ProgressBar { Width = 240, Height = 6, Minimum = 0, Maximum = 100, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _loadText = Ui.Text("", 12, FontWeight.SemiBold, Ui.KText);
        readonly System.Diagnostics.Stopwatch _loadWatch = new System.Diagnostics.Stopwatch();
        int _loadRenderers, _loadRenderersTotal, _loadTextures, _loadTexturesTotal;
        readonly TextBlock _fileText = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly TextBox _log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        readonly Button _undoBtn, _redoBtn, _saveBtn, _sideBtn;
        readonly Settings _settings = Settings.Load();
        readonly Grid _body = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        readonly Border _paneBorder;
        readonly Control _preview;
        readonly GridSplitter _splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        readonly Sidebar _side = new Sidebar();
        readonly FloatHost _floats;
        // 작업 창: 페이지를 모두 겹쳐 붙여 두고 보이기만 바꾼다 (2026-10-24). 갈아 끼우면 다시 열 때마다 스타일·배치를 새로 해 0.05~0.2초씩 걸렸다
        readonly Panel _pane = new Panel();
        readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();
        PosePanel? _posePanel;
        StudioPanel? _studioPanel;
        readonly Control _welcome;
        readonly string[] _args;
        RenderSync? _sync;
        string? _sessionPath;
        bool _sceneLoadedOnce, _closeConfirmed;
        string _unityVersion = "2022.3.22f1";   // 렌더러가 ready로 알려 주는 Unity 버전 (셰이더 팩은 같은 버전으로 만들어야 열린다)

        public MainWindow(string[] args)
        {
            _args = args;
            Boot("창 만들기 시작");
            Title = "Avatar Light Edit";
            Width = 1600; Height = 960; MinWidth = 1000; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Ui.Font;
            FontSize = 14;
            this.Bg(Ui.KBase);
            UiTheme.Apply(_settings);   // 테마 (설정 창 "화면 › 테마")
            Hardware.Apply(_settings.SliderStyle == "dial", _settings);   // 설정 "조작 모양 → 다이얼 모드" (다이얼 · 장비 키 단추 · 로커 스위치)
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 40;
            DragDrop.SetAllowDrop(this, true);

            var root = new DockPanel();
            Content = _uiScaleBox = new LayoutTransformControl { Child = root };   // VR 중 글자·단추 배율 (MainWindow.VrPanel.cs)

            // ---- 제목 표시줄: 앱 이름 · 파일 · 명령 ----
            // 막대 자체는 배경 없음(클릭이 통과): 오른쪽 창 단추 자리를 Avalonia가 가로채면 닫기·최대화가 끌기로 바뀐다
            var bar = new DockPanel { Height = 40 };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            // 앱 로고 (2026-11-02) — 리소스가 없으면 예전 아바타 아이콘
            using (var logo = App.Logo("ALEdit-64.png"))
            {
                Control appIcon = logo != null
                    ? new Image { Source = new Avalonia.Media.Imaging.Bitmap(logo), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center }
                    : Ui.Icon(Glyph.Avatar, 15, Ui.KAccentText);
                appIcon.Margin = new Thickness(0, 0, 8, 0);
                left.Children.Add(appIcon);
            }
            left.Children.Add(Ui.Text("Avatar Light Edit", 13, FontWeight.SemiBold));
            _fileText.Margin = new Thickness(10, 0, 18, 0); _fileText.MaxWidth = 320;
            left.Children.Add(_fileText);
            var openBtn = MenuButton(Glyph.Open, "열기", ("Unity 씬·프리팹·패키지 가져오기…", "Ctrl+I", ImportUnityDialog), ("의상·헤어·악세서리 붙이기…", "Ctrl+Shift+A", AddItemDialog), ("가져온 파일 열기…", "Ctrl+O", OpenIrDialog), ("세션 열기…", "Ctrl+Shift+O", OpenSessionDialog), ("셰이더 팩 추가…", "Ctrl+Shift+P", ShaderPackDialog));
            if (openBtn.Flyout is MenuFlyout openMenu)
            {
                var recentMenu = new MenuItem { Header = "최근 파일" };
                openMenu.Items.Add(new Separator());
                openMenu.Items.Add(recentMenu);
                openMenu.Opening += (_, __) => FillRecentMenu(recentMenu);
            }
            left.Children.Add(openBtn);
            _saveBtn = MenuButton(Glyph.Save, "저장", ("저장", "Ctrl+S", () => _ = SaveSession(false)), ("다른 이름으로 저장…", "Ctrl+Shift+S", () => _ = SaveSession(true)),
                ("파일까지 담아 저장…", "", () => _ = SaveSession(true, pack: true)));
            ToolTip.SetTip(_saveBtn, "세션(.alesav)은 바꾼 값만 담아요 · 파일까지 담아 저장(.alexp)은 아바타 파일·텍스처도 함께 담아 다른 PC에서 원본 없이 열 수 있어요 (올리기는 원본이 있어야 해요)");
            left.Children.Add(_saveBtn);
            var upBtn = MenuButton(Glyph.Upload, "올리기", ("VRChat에 올리기…", "Ctrl+U", UploadDialog), ("Unity 프로젝트로 내보내기…", "Ctrl+Shift+U", ExportDialog));
            ToolTip.SetTip(upBtn, "VRChat에 올리기: 패키지에서 가져온 아바타는 업로드용 Unity 프로젝트를 만들고, Unity 프로젝트에서 가져온 아바타는 그 프로젝트에서 올려요 · Unity 프로젝트로 내보내기: 내 프로젝트에 새 장면으로 만들어요 (원본은 그대로)");
            left.Children.Add(upBtn);
            left.Children.Add(Divider());
            _undoBtn = Ui.Subtle(Glyph.Undo, "실행 취소", Undo, "실행 취소 (Ctrl+Z)");
            _redoBtn = Ui.Subtle(Glyph.Redo, null, Redo, "다시 실행 (Ctrl+Y)");
            left.Children.Add(_undoBtn); left.Children.Add(_redoBtn);
            left.Children.Add(Divider());
            _sideBtn = Ui.Subtle(Glyph.DockRight, null, TogglePaneSide, "");   // 아이콘만 (글자 "작업 창 오른쪽"은 무엇을 하는지 잘 안 보였다). 설명은 툴팁
            left.Children.Add(_sideBtn);
            left.Children.Add(Ui.Subtle(Glyph.Search, "찾기", OpenSearch, "페이지·탭·설정 찾기 (Ctrl+K) · 단축키 목록은 Ctrl+/"));
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);
            bar.Children.Add(new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 140, 0) }); // 빈 곳 = 창 끌기, 오른쪽 140px는 창 단추
            bar.PointerPressed += (_, e) =>
            {
                if (VrPanelFeed.Injecting) return;   // VR 레이저로 누른 것: 창 끌기는 진짜 마우스를 따라가므로 하지 않는다
                if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else BeginMoveDrag(e);
            };
            var barBg = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(0, 0, 0, 1) }.Bg(Ui.KTitleBar).Stroke(Ui.KDivider);
            var barHost = new Panel { Children = { barBg, bar } };
            DockPanel.SetDock(barHost, Dock.Top);
            root.Children.Add(barHost);

            // ---- 상태 표시줄 ----
            var statusRow = new DockPanel { Margin = new Thickness(12, 0, 12, 0), Height = 28 };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            dots.Children.Add(_rendererItem); dots.Children.Add(_sceneItem); dots.Children.Add(_vrItem);
            var load = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _loadText.VerticalAlignment = VerticalAlignment.Center; _loadText.IsVisible = false;
            load.Children.Add(_loadBar); load.Children.Add(_loadText);
            dots.Children.Add(load);
            dots.Children.Add(_changesItem);
            dots.Children.Add(_checkItem);   // 올리기 전에 볼 것 (MainWindow.Check.cs)
            SetupCheckItem();
            DockPanel.SetDock(dots, Dock.Left);
            statusRow.Children.Add(dots);
            DockPanel.SetDock(_savedChip, Dock.Right);
            statusRow.Children.Add(_savedChip);   // 방금 저장한 사진: 열기·폴더·복사 (MainWindow.Saved.cs)
            _status.Margin = new Thickness(24, 0, 0, 0); _status.HorizontalAlignment = HorizontalAlignment.Right;
            statusRow.Children.Add(_status);
            var status = _statusBar = new Border { Child = statusRow, BorderThickness = new Thickness(0, 1, 0, 0) }.Stroke(Ui.KCardStroke);
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);

            // ---- 본문: 사이드바 | 작업 창 | 분할선 | 3D 미리보기 ----
            root.Children.Add(_body);

            // 떠 있는 작업 창 (페이지 안 탭을 떼어 띄운 것·가져온 파일 창): 페이지보다 먼저 (SubTabs가 쓴다)
            Embed.Enabled = _settings.EmbedFloats;
            _floats = new FloatHost(this, HandleFloatKey);
            SubTabs.Host = _floats;

            // 왼쪽 섹션은 위에서 아래로 작업하는 순서: 입히기 → 머티리얼 → 블렌드쉐이프 → 자세 → 환경(보기) → 스튜디오(표정·촬영) → 최적화 → 올리기
            Boot("페이지 만들기 시작");
            _pages["avatar"] = Page("아바타", "의상·헤어·악세서리를 붙이고, 기본 옷과 메시를 켜고 꺼 봐요.", (_avatarPanel = new AvatarPanel(_ctx) { AddItem = AddItemDialog, RemoveItem = RemoveItem, PlaceItem = PlaceItem, CursorPlaceItem = CursorPlaceItem, AutoScaleItem = AutoScaleItem, ScaleItem = ScaleItem, OpenMaterial = OpenMaterialForRenderer, MoveItem = node => SelectTarget("item", node, ""), OpenFiles = OpenFilesWindow, OpenAssets = OpenAssetsWindow }));
            BuildGuests();   // 아바타 페이지 "함께 세우기" 탭
            _overlay = new OverlayController(_ctx, _settings, _viewport, () => this);
            _overlay.Changed += OnOverlayChanged;
            _pages["material"] = Page("머티리얼", "색·텍스처·셰이더 값을 바꿔 봐요. 원본 파일은 그대로 남아요.", new MaterialPanel(_ctx) { OpenAssets = OpenAssetsWindow });
            _pages["shapes"] = Page("블렌드쉐이프", "얼굴 표정과 체형 값을 슬라이더로 조절해요.", _blendPanel = new BlendShapePanel(_ctx, _settings));
            _pages["pose"] = Page("자세", "프리셋·애니메이션·머슬 값으로 아바타의 자세를 바꿔요. 머슬 값이라 다른 아바타에도 같은 자세로 옮겨져요.", _posePanel = new PosePanel(_ctx) { FloorFit = _settings.ViewFloorFit });
            _posePanel.FloorFitChanged += on => { _settings.ViewFloorFit = on; ViewChanged(); SetStatus(on ? "자세를 바닥에 맞춰요." : "바닥에 맞추기를 껐어요 (몸 높이는 자세 값 그대로)."); };
            _pages["studio"] = Page("스튜디오", "표정·페이셜 트래킹을 쓰고, 배경을 깔고 카메라 위치를 저장해 두었다가 고해상도로 촬영·녹화해요.", _studioPanel = new StudioPanel(_ctx, _settings, _exprPanel = new ExpressionPanel(_ctx, _settings), new OverlayPanel(_overlay, OpenBoard), _shapePanel = new ShapeKeyPanel(_ctx, _shapeRun)));
            _studioPanel.QualityChanged = SendQuality;
            _studioPanel.ViewChanged = ViewChanged;
            _pages["environment"] = Page("환경", "월드의 조명과 분위기를 바꿔 아바타가 어떻게 보이는지 확인해요.", new EnvironmentPanel(_ctx));
            _pages["optimize"] = Page("최적화", "VRChat 퍼포먼스 등급을 예상하고 정점·폴리곤을 줄여요. 원본 메시는 그대로 남아요.", new AvatarPanel(_ctx, "optimize") { OpenMaterial = OpenMaterialForRenderer });
            _pages["upload"] = Page("올리기", "편집한 아바타를 VRChat에 올릴 Unity 프로젝트를 준비해요.", UploadPage());
            _pages["log"] = Page("로그", "미리보기 프로그램과 주고받은 기록이에요. 문제가 생기면 여기를 확인하세요.", Ui.Card(_log, new Thickness(12, 8)));
            _welcome = Welcome();
            foreach (var p in _pages.Values.Append(_welcome)) { p.IsVisible = false; _pane.Children.Add(p); }
            Boot("페이지 다 만듦");
            _side.Add("avatar", Glyph.Avatar, "아바타", "Ctrl 1");
            _side.Add("material", Glyph.Material, "머티리얼", "Ctrl 2");
            _side.Add("shapes", Glyph.Shapes, "블렌드쉐이프", "Ctrl 3");
            _side.Add("pose", Glyph.Pose, "자세", "Ctrl 4");
            _side.Add("environment", Glyph.Environment, "환경", "Ctrl 5");
            _side.Add("studio", Glyph.Camera, "스튜디오", "Ctrl 6");
            _side.Add("optimize", Glyph.Optimize, "최적화", "Ctrl 7");
            _side.Add("upload", Glyph.Upload, "올리기", "Ctrl 8");
            _side.AddBottom("log", Glyph.Log, "로그", null);
            ModeSwitch();
            SettingsSetup();
            UpdateSetup();
            _side.Selected += ShowPage;
            _side.Collapsed = _settings.SidebarCollapsed;
            _side.CollapsedChanged += c => { _settings.SidebarCollapsed = c; _settings.Save(); };
            _body.Children.Add(_side.At(0));

            _paneBorder = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = _pane, ClipToBounds = true };
            _paneBorder.Bg(Ui.KPanel).Stroke(Ui.KCardStroke);
            _preview = Preview();
            _body.Children.Add(_paneBorder); _body.Children.Add(_splitter); _body.Children.Add(_preview);
            // 맨 오른쪽 세밀 조정 막대 (접으면 얇은 여백)
            _rail = new FineRail(_settings.FineRailOpen);
            _rail.OpenChanged += open => { _settings.FineRailOpen = open; _settings.Save(); FitLayout(); };
            _body.Children.Add(_rail.At(4));
            _ctx.DocumentOpened += () => { _rail.Clear(); _strip?.Clear(); };
            _blendPanel.GroupFocused += g => _rail.SetGroup(g);   // 쉐이프키 묶음 다이얼
            DialBoard.Init(this, _settings);   // 떠 있는 다이얼 (DialFloat.cs)
            DialBoard.Status = SetStatus;
            _splitter.DragCompleted += (_, __) => { _settings.PaneWidth = _paneBorder.Bounds.Width; _settings.Save(); };
            SizeChanged += (_, __) => FitLayout();
            ApplyPaneSide();
            ApplyLayoutStyle();

            _side.Select("avatar");
            WarmSetup();
            _ctx.StatusRequested += SetStatus;
            _ctx.FileSaved += ShowSaved;
            _ctx.DocumentOpened += () => { if (_side.Current != null) ShowPage(_side.Current); };
            _ctx.DocumentOpened += RefreshCheckItem;
            SetupShapeKeys();
            _viewport.HandleReady += StartRenderer;
            Activated += (_, __) => _viewport.Activate();
            // 떠 있는 창으로 포커스가 옮겨 가도 미리보기는 활성으로 둔다 (에디터의 일부)
            Deactivated += (_, __) => Dispatcher.UIThread.Post(() => { if (!_floats.AnyActive && !IsActive) _viewport.Deactivate(); });
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, (_, __) => Embed.TakeFocusBack(this), RoutingStrategies.Tunnel, handledEventsToo: true);   // 떠 있는 창(자식 창)에 남은 키보드 포커스를 에디터로
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, (_, __) => _dragPath = null);
            Closing += OnClosing;
            // 닫을 때: 미리보기 자식 창은 이미 없을 수 있어 오버레이 창은 숨기기만 하고(렌더러는 곧 끝난다), 컨트롤 보드도 닫는다
            Closed += (_, __) => { if (!_keepRecovery) ClearRecovery(); _overlay.HideForClose(); _board?.Close(); _hotkeys?.Dispose(); _ctx.Render?.Dispose(); };
            // Opened는 숨겼다(컨트롤 보드의 에디터 숨기기) 다시 보일 때도 온다 → 시작 파일은 처음 한 번만 연다 (다시 열면 바꾼 값이 사라진다)
            bool firstOpen = true;
            Opened += async (_, __) => { if (!firstOpen) return; firstOpen = false; PopupHoles.Attach(this); Boot("창 열림"); await StartupAsync(); };
            RefreshTitle(); RefreshUndoButtons(); RefreshViewMenu();
            SetStatus("아바타 파일을 열거나 창에 끌어다 놓으세요.");
            _rendererItem.Set(StatusItem.State.Busy, "미리보기 준비 중");
            _sceneItem.Set(StatusItem.State.Off, "장면 없음");
            PrepareShots(args);
            PrepareTestEdit();
            // 설정 "화면 › 언어": 열린 창 모두 글꼴을 맞춘다 — 떼어 낸 패널 창도 (2026-11-02)
            Texts.LangChanged += () =>
            {
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                    foreach (var w in d.Windows) w.FontFamily = Ui.Font;
                else FontFamily = Ui.Font;
            };
            Texts.Reloaded += (n, err) => SetStatus(err != null ? "글 표를 읽지 못했습니다: " + err : $"글 표를 다시 읽었어요 · 바꾼 글 {n}줄 ({Texts.FilePath})");
            SetupSafety();
            Boot("창 만들기 끝");
            if (RunMode.Test) AppendLog("info", $"테스트 모드 · 설정 {Settings.Profile} · 글 표 {Texts.FilePath} ({Texts.Count}줄, 저장하면 바로 반영)");
        }

        // ---------------- 셸 구성 요소 ----------------
        /// <summary>작업 창 한 페이지: 큰 제목 + 설명, 아래는 내용 (내용이 스스로 스크롤).</summary>
        static Control Page(string title, string subtitle, Control content)
        {
            var p = new DockPanel();
            var head = Ui.PageHeader(title, subtitle);
            head.Margin = new Thickness(24, 18, 24, 12);
            PageHeads.Add(head);
            // 에디터 상단 바: 제목 줄에 옅은 파란 바탕 + 아래 선 (작업 창 위쪽이 구분되게)
            var headBar = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 10) }.Bg(Ui.KToolbar).Stroke(Ui.KDivider);
            DockPanel.SetDock(headBar, Dock.Top);
            p.Children.Add(headBar);
            if (content is Border) content.Margin = new Thickness(24, 0, 24, 20);
            p.Children.Add(content);
            return p;
        }

        /// <summary>파일이 없을 때 보이는 시작 안내.</summary>
        Control Welcome()
        {
            var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, Spacing = 6 };
            var circle = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12), Child = Ui.Icon(Glyph.Avatar, 30, Ui.KAccentText) };
            p.Children.Add(circle.Bg(Ui.KSelect));
            var t = Ui.Text("아바타를 열어 시작하세요", 20, FontWeight.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
            p.Children.Add(t);
            var d = Ui.Wrap("Unity 씬·프리팹·.unitypackage를 가져오거나 파일을 이 창에 끌어다 놓아요.", Ui.KText2, 13);
            d.TextAlignment = TextAlignment.Center; d.Margin = new Thickness(0, 0, 0, 14);
            p.Children.Add(d);
            p.Children.Add(Ui.WideButton("Unity 씬·프리팹·패키지 가져오기", ImportUnityDialog, "Ctrl+I", Glyph.Import, accent: true));
            p.Children.Add(Ui.WideButton("가져온 파일 열기", OpenIrDialog, "Ctrl+O", Glyph.Open));
            p.Children.Add(Ui.WideButton("세션 열기", OpenSessionDialog, "Ctrl+Shift+O", Glyph.Folder));
            p.Children.Add(_recentBox);   // 최근 파일 (MainWindow.Recent.cs)
            RefreshRecent();
            return p;
        }

        /// <summary>"올리기" 페이지: 올리기 전 점검 + 올리기·내보내기 두 갈래. 단계 설명은 도움말 단추로.</summary>
        Control UploadPage()
        {
            var p = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
            p.Children.Add(Ui.SectionTitle("올리기 전에 확인", "올리기 전에 한 번 보면 좋은 것들이에요. 항목을 누르면 고칠 수 있는 페이지로 가요."));
            p.Children.Add(_uploadCheck);
            p.Children.Add(Ui.SectionTitle("올리기"));
            p.Children.Add(ActionCard(Glyph.Upload, "VRChat에 올리기", "업로드용 Unity 프로젝트를 만들어 편집을 적용해요",
                "1. 프로젝트 준비: 업로드용 Unity 프로젝트를 만들고 VRChat SDK와 선택한 기믹을 공식 저장소에서 설치합니다.\n"
                + "2. 편집 적용: 머티리얼·BlendShape·켜고 끄기·메시 편집·아이템 위치를 원본 아바타에 적용합니다.\n"
                + "3. Unity에서 업로드: 열린 Unity의 VRChat SDK 창에서 직접 로그인한 뒤 \"ALEdit 업로드\" 창으로 업로드합니다.\n\n"
                + "이 프로그램은 VRChat 계정 정보를 다루지 않습니다.",
                Ui.Button("올리기 준비…", UploadDialog, "Ctrl+U", Glyph.Upload, accent: true)));
            p.Children.Add(ActionCard(Glyph.Save, "Unity 프로젝트로 내보내기", "내 프로젝트에 새 장면으로 만들어요",
                "원본 장면·프리팹·기믹 설정은 그대로 두고, 바뀐 사본만 더해 새 장면을 만들어요. 내 Unity 프로젝트에서 직접 고치거나 올릴 때 써요.",
                Ui.Button("내보내기…", ExportDialog, "Ctrl+Shift+U", Glyph.Save)));
            var note = Ui.Caption("자세·환경·아바타 전체 자리·카메라는 미리보기 전용이라 올라가지 않아요.", Ui.KText3);
            note.Margin = new Thickness(4, 2, 0, 0);
            p.Children.Add(note);
            return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        /// <summary>아이콘 · 제목(+도움말) · 한 줄 설명 · 오른쪽 단추로 된 카드. 좁으면 단추가 아래 줄로 내려간다.</summary>
        static Control ActionCard(string glyph, string title, string caption, string help, Button action)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 14) };
            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Child = Ui.Icon(glyph, 18, Ui.KAccentText), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center }.Bg(Ui.KSelect);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Text(title, 15, FontWeight.SemiBold)); head.Children.Add(Ui.Help(help));
            text.Children.Add(head);
            text.Children.Add(Ui.Caption(caption));
            action.Margin = new Thickness(12, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; action.MinWidth = 120; action.HorizontalContentAlignment = HorizontalAlignment.Center;
            g.Children.Add(icon.At(0)); g.Children.Add(text.At(1)); g.Children.Add(action.At(2));
            // 좁으면 단추를 설명 아래로
            g.SizeChanged += (_, e) =>
            {
                bool narrow = e.NewSize.Width < 380;
                Grid.SetRow(action, narrow ? 1 : 0); Grid.SetColumn(action, narrow ? 1 : 2);
                action.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
                action.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            };
            return Ui.Card(g);
        }

        static Border Divider() => new Border { Width = 1, Height = 18, Margin = new Thickness(8, 0) }.Bg(Ui.KDivider);

        static Button MenuButton(string glyph, string label, params (string Text, string Gesture, Action Act)[] items)
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (text, gesture, act) in items)
            {
                var mi = new MenuItem { Header = text, InputGesture = gesture.Length > 0 ? KeyGesture.Parse(gesture) : null };
                mi.Click += (_, __) => act();
                menu.Items.Add(mi);
            }
            return Ui.DropButton(glyph, label, menu, out _);
        }

        /// <summary>
        /// 작업 창(머티리얼·블렌드쉐이프·환경 편집)을 미리보기 왼쪽 또는 오른쪽에 둔다.
        /// 열 정의만 다시 만들고 컨트롤은 옮기지 않는다 (Unity 자식 창을 다시 붙이지 않게).
        /// </summary>
        void ApplyPaneSide()
        {
            bool right = _settings.PaneOnRight;
            var cols = _body.ColumnDefinitions;
            cols.Clear();
            cols.Add(new ColumnDefinition(GridLength.Auto));
            // VR 중 미리보기 접기: 작업 창이 남은 너비를 다 쓴다 (MainWindow.VrPanel.cs)
            var pane = _paneCol = new ColumnDefinition(_vrFolded ? new GridLength(1, GridUnitType.Star) : new GridLength(_settings.PaneWidth)) { MinWidth = PaneMin };
            var view = _vrFolded ? new ColumnDefinition(new GridLength(0)) : new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = PreviewMin };
            cols.Add(right ? view : pane);
            cols.Add(new ColumnDefinition(new GridLength(_vrFolded ? 0 : SplitWidth)));
            cols.Add(right ? pane : view);
            cols.Add(new ColumnDefinition(GridLength.Auto));   // 세밀 조정 막대
            _paneBorder.At(right ? 3 : 1);
            _preview.At(right ? 1 : 3);
            _splitter.At(2);
            _sideBtn.Content = Ui.Icon(right ? Glyph.DockLeft : Glyph.DockRight, 14);
            ToolTip.SetTip(_sideBtn, right ? "작업 창을 미리보기 왼쪽으로 옮기기" : "작업 창을 미리보기 오른쪽으로 옮기기");
            if (Clean) _paneBorder.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);   // 깔끔한 배치: 미리보기 쪽 선 하나
            FitLayout();
        }

        // ---------------- 창 크기 맞추기 ----------------
        // 좁은 창에서도 줄이 깨지지 않게: 사이드바는 아이콘만, 작업 창은 미리보기가 최소 너비를 지키는 만큼만 (설정의 너비는 그대로 기억).
        const double PaneMin = 360, PreviewMin = 420, NarrowWindow = 1240;
        ColumnDefinition? _paneCol;
        FineRail? _rail;
        BlendShapePanel _blendPanel = null!;

        void FitLayout()
        {
            double w = ClientSize.Width;
            if (w <= 0 || _paneCol == null) return;
            _side.Narrow = w < NarrowWindow;
            double side = _side.Width + _side.Margin.Left + _side.Margin.Right;
            double avail = w - side - _body.Margin.Left - _body.Margin.Right - SplitWidth - (_rail?.Outer ?? 0);
            if (_vrFolded) return;   // 미리보기를 접은 동안은 작업 창이 * 너비
            double pane = Math.Clamp(_settings.PaneWidth, PaneMin, Math.Max(PaneMin, avail - PreviewMin));
            if (Math.Abs(_paneCol.Width.Value - pane) > 0.5) _paneCol.Width = new GridLength(pane);
        }

        void TogglePaneSide()
        {
            _settings.PaneWidth = _paneBorder.Bounds.Width > 0 ? _paneBorder.Bounds.Width : _settings.PaneWidth;
            _settings.PaneOnRight = !_settings.PaneOnRight;
            _settings.Save();
            ApplyPaneSide();
        }

        string _pageKey = "";

        void ShowPage(string key)
        {
            _pageKey = key;
            SendQuality();
            if (key == "upload") RefreshUploadCheck();
            bool needsDoc = key == "avatar" || key == "material" || key == "shapes" || key == "pose" || key == "optimize" || key == "upload";
            var show = needsDoc && _ctx.Doc == null ? _welcome : _pages[key];
            foreach (var c in _pane.Children) c.IsVisible = c == show;
            DropHiddenFocus(this);
            _rail?.SetPage(key);
            _floats.PageChanged(key);
            _ctx.RaisePageShown(key);
            Later.Flush();
        }

        /// <summary>아바타·메시 목록에서 누른 렌더러를 머티리얼 페이지에서 바로 연다.</summary>
        void OpenMaterialForRenderer(string renderer, string? material)
        {
            var doc = _ctx.Doc;
            var r = doc?.Original.FindRenderer(renderer);
            if (r == null) return;
            _ctx.Select(r.Id, material ?? r.Materials.FirstOrDefault());
            _side.Select("material");
            _viewport.Activate();
        }

        /// <summary>숨긴 페이지·탭 안에 키보드 포커스가 남아 있으면 푼다 (예전엔 떼어 내면서 저절로 풀렸다 — 안 보이는 칸에 글자가 들어가지 않게)</summary>
        internal static void DropHiddenFocus(Visual from)
        {
            var fm = TopLevel.GetTopLevel(from)?.FocusManager;
            if (fm?.GetFocusedElement() is Visual v && !v.IsEffectivelyVisible) fm.ClearFocus();
        }

        // ---------------- 렌더러 ----------------
        /// <summary>렌더러를 넘겨받았거나 띄웠을 때 (시작 파일 열기가 기다린다 — StartupAsync)</summary>
        readonly TaskCompletionSource<bool> _rendererStarted = new TaskCompletionSource<bool>();

        void StartRenderer()
        {
            Boot("미리보기 창 준비");
            try { StartRendererCore(); }
            finally { _rendererStarted.TrySetResult(true); }
        }

        void StartRendererCore()
        {
            if (_ctx.Render != null) return;   // 미리보기 자식 창을 다시 만들어도 렌더러는 하나 (UnityView가 Unity 창을 옮긴다)
            // 프로그램이 켜질 때 미리 띄워 둔 렌더러가 있으면 넘겨받아 Unity 창을 미리보기로 옮긴다 (Prestart.cs)
            var pre = Prestart.Take(_viewport);
            RenderHost r;
            if (pre != null) r = pre;
            else
            {
                var exe = RenderHost.FindRenderer();
                if (exe == null) { _rendererItem.Set(StatusItem.State.Error, "미리보기를 켜지 못했어요 · 편집만 돼요"); return; }
                r = new RenderHost(a => Dispatcher.UIThread.Post(a), _viewport, exe);
            }
            r.Status += s => { _rendererItem.Set(r.IsConnected ? StatusItem.State.On : StatusItem.State.Busy, s); AppendLog("info", s); };
            r.Message += OnRendererMessage;
            _ctx.Render = r;
            if (pre == null) { r.Start(); return; }
            Prestart.Go(a => Dispatcher.UIThread.Post(a));
            // Unity 창이 임시 부모에 생길 때까지 찾아서 옮긴다 (UnityView.FindUnityWindow, 0.1초 간격 최대 30초)
            int tries = 0;
            var find = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            find.Tick += (_, __) => { if (_viewport.FindUnityWindow() || ++tries > 300) find.Stop(); };
            find.Start();
        }

        void RestartRenderer() { if (_ctx.Render != null) _ctx.Render.Restart(); else StartRenderer(); }

        void OnRendererMessage(JNode m)
        {
            if (OnGuestMessage(m)) return;
            switch (Msg.Type(m))
            {
                case Msg.Ready:
                    if (Prestart.PostedAt != default) Boot("미리보기 준비됨", Prestart.PostedAt);   // 미리 띄운 렌더러가 실제로 준비된 때 (창이 넘겨받기 전일 수 있다)
                    Boot("렌더러 연결");
                    _rendererItem.Set(StatusItem.State.On, $"미리보기 연결됨 · Unity {m["unityVersion"].AsString}");
                    if (m["unityVersion"].AsString.Length > 0) _unityVersion = m["unityVersion"].AsString;
                    if (m["environment"].Kind == JKind.Array) _ctx.EnvironmentSchema = m["environment"];
                    _ctx.AvailableShaders.Clear();
                    foreach (var s in m["shaders"].Items) _ctx.AvailableShaders.Add(s.AsString);
                    _ctx.RaiseRendererInfoChanged();
                    SendView();
                    SendQuality();
                    SendVrOnReady();
                    if (_ctx.Doc != null) SendLoadScene();
                    else _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", Document.DefaultEnvironment()));
                    _guestsSent = false; SendGuests();   // 함께 세운 아바타는 지금 아바타 다음에 (렌더러가 차례로 처리한다)
                    break;
                case Msg.LoadProgress:
                    OnLoadProgress(m);
                    break;
                case Msg.SceneLoaded:
                    if (_ctx.Doc == null) break;
                    if (Prestart.PostedAt != default) Boot("장면 도착", Prestart.PostedAt);   // 렌더러가 다 불러온 때 (2026-10-27 — UI 스레드가 바빠 늦게 처리하는지 보려고)
                    Boot("장면 처리 시작");
                    var renderMs = _loadWatch.ElapsedMilliseconds;
                    ShowLoad(97, "편집 값 적용 중");
                    _ctx.Doc.SetSchemas(MaterialSchema.ReadAll(m), isInitialLoad: true);
                    _sync?.SendAllOverrides();
                    _ctx.Render?.Send(_shapeRun.Message());   // 블렌드쉐이프 단축키 레이어 (장면을 다시 만들면 렌더러 쪽 레이어는 비어 있다)
                    if (!_ctx.Camera.IsNull) _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("state", _ctx.Camera));
                    else if (!_sceneLoadedOnce) CameraPreset("full");
                    _sceneLoadedOnce = true;
                    if (_meshEditOn) SendMeshTool(JNode.Obj().Set("enabled", true));
                    foreach (var w in m["warnings"].Items) AppendLog("warn", w.AsString);
                    int unsupported = m["materials"].Items.Count(x => !x["supported"].AsBool);
                    int approx = m["materials"].Items.Count(x => x["approx"].AsBool);   // 대신 그린 셰이더 · 가까운 키워드 조합
                    _sceneItem.Set(unsupported > 0 ? StatusItem.State.Warn : StatusItem.State.On,
                        $"씬 · 메시 {_ctx.Doc.Original.Renderers.Count} · 머티리얼 {_ctx.Doc.Original.Materials.Count}"
                        + (unsupported > 0 ? $" · 미지원 셰이더 {unsupported}" : "") + (approx > 0 ? $" · 근사 표시 {approx}" : ""));
                    _ctx.SceneBounds = m["bounds"];
                    _pbChains = m["physBones"]["chains"].AsInt;
                    _pbSoft = m["physBones"]["soft"].AsInt;
                    _pbLegGuard = m["physBones"]["legGuard"].AsInt;
                    SendPhysBones(); RefreshPhysBones();
                    _ctx.RaiseRendererInfoChanged();
                    _infoTiming = "씬 뒤 페이지 갱신 " + EditorContext.TimingLine(_ctx.InfoTimings);
                    AppendLog("info", _infoTiming);
                    RefreshCheckItem();   // 셰이더 지원 여부는 이제 안다
                    if (unsupported + approx > 0) OfferProjectShaders(m);
                    if (_loadWatch.IsRunning)
                    {
                        AppendLog("info", $"장면 불러오기 {_loadWatch.ElapsedMilliseconds / 1000.0:0.0}초 (미리보기 {renderMs / 1000.0:0.0}초 · 편집기 값 적용 {(_loadWatch.ElapsedMilliseconds - renderMs) / 1000.0:0.0}초)");
                        _loadWatch.Reset();
                    }
                    // 스키마가 와서 다시 그릴 페이지(머티리얼)를 그리고 예열까지 끝낸 뒤 불러오기 끝 — 장면이 보인 뒤 처음 누를 때 멈추지 않게 (MainWindow.Warm.cs)
                    ShowLoad(-1, "");
                    SetStatus("편집 창을 준비하는 중…");
                    Dispatcher.UIThread.Post(FinishLoad);   // 보통 우선순위: 위 SetSchemas가 건 머티리얼 다시 그리기 다음 (Background는 화면 확인 모드에서 끝내 차례가 오지 않았다)
                    break;
                case Msg.MaterialInfo:
                    _ctx.Doc?.SetSchema(MaterialSchema.FromJson(m), isInitialLoad: false);
                    break;
                case Msg.CameraChanged:
                    _ctx.Camera = m["state"];
                    break;
                case Msg.Key:
                    HandleShortcut(m["key"].AsString, m["ctrl"].AsBool, m["shift"].AsBool, m["alt"].AsBool);
                    break;
                case Msg.ItemPlaced:
                    OnItemPlaced(m);
                    break;
                case Msg.Selected:
                    OnSelected(m);
                    break;
                case Msg.NodeMoved:
                    {
                        var node = m["node"].AsInt;
                        var name = _ctx.Doc?.Original.Outfits.FirstOrDefault(o => o.Node == node)?.Name ?? "아이템";
                        _ctx.Doc?.Set(Addr.NodeTransform(node), m["trs"], $"옮기기: {name}");
                        break;
                    }
                case Msg.AvatarMoved:
                    _ctx.Doc?.Set(Addr.AvatarTransform, m["trs"], "아바타 옮기기");
                    break;
                case Msg.MeshEdited:
                    _ctx.Meshes?.Append(m["mesh"].AsString, m["op"], m["label"].AsString);
                    break;
                case Msg.MeshSelection:
                    OnMeshSelection(m);
                    break;
                case Msg.VrStatus:
                    OnVrStatus(m);
                    break;
                case Msg.VrCameraShot:
                    OnVrCameraShot(m);
                    break;
                case Msg.VrCameraBookmark:
                    OnVrCameraBookmark(m);
                    break;
                case Msg.VrPropMoved:
                    _studioPanel?.OnVrPropMoved(m);
                    break;
                case Msg.VrItemPlaced:
                    OnVrItemPlaced(m);
                    break;
                case Msg.VrCameraState:
                    OnVrCameraState(m);
                    break;
                case Msg.VrCameraVideo:
                    OnVrCameraVideo(m);
                    break;
                case Msg.VrPanelShown:
                    OnVrPanelShown(m);
                    break;
                case Msg.VrPanelPlaced:
                    OnVrPanelPlaced(m);
                    break;
                case Msg.VrPanelAction:
                    OnVrPanelAction(m);
                    break;
                case Msg.VrPanelsOpen:
                    OnVrPanelsOpen(m);
                    break;
                case Msg.VrKeyboardText:
                    if (_vrFeeds.TryGetValue(m["id"].AsString, out var kf)) kf.KeyboardText(m["text"].AsString, m["done"].AsBool, m["cancel"].AsBool);
                    break;
                case Msg.VrPointer:
                    OnVrPointer(m);
                    break;
                case Msg.Notice:
                    SetStatus(m["text"].AsString);
                    AppendLog("info", m["text"].AsString);
                    break;
                case Msg.Log:
                    AppendLog(m["level"].AsString, m["text"].AsString);
                    break;
            }
            _ctx.RaiseRendererMessage(m);
        }

        /// <summary>doc: 아직 페이지에 채우기 전의 문서 (OpenIr가 페이지를 채우는 동안 렌더러가 먼저 불러오게). 없으면 지금 문서</summary>
        void SendLoadScene(Document? doc = null)
        {
            doc ??= _ctx.Doc;
            if (doc == null || _ctx.Render == null) return;
            _ctx.Render.Send(Msg.Make(Msg.LoadScene).Set("irPath", doc.IrPath).Set("texCache", _settings.TextureCache));
            _sceneItem.Set(StatusItem.State.Busy, "장면 불러오는 중");
            _loadRenderers = _loadTextures = 0; _loadRenderersTotal = doc.Original.Renderers.Count; _loadTexturesTotal = doc.Original.Textures.Count;
            _loadWatch.Restart();
            ShowLoad(0, "장면 불러오는 중");
        }

        /// <summary>
        /// 렌더러가 보내는 로드 진행 → 퍼센트. IR 읽기 5%, 메시·렌더러 35%, 텍스처 55% (텍스처는 렌더러를 만드는 동안 함께 올라간다),
        /// sceneLoaded 뒤 편집 값 적용이 나머지.
        /// </summary>
        void OnLoadProgress(JNode m)
        {
            int done = m["done"].AsInt, total = Math.Max(1, m["total"].AsInt);
            // 장면을 다 불러온 뒤에도 렌더러는 새 텍스처를 올릴 때 textures 진행을 보낸다 — 불러오는 중이 아니면 무시 (2026-11-03: 다 불러왔는데 94% 바가 다시 뜨고 안 꺼지던 것)
            if (!_loadWatch.IsRunning && m["stage"].AsString != "start") return;
            switch (m["stage"].AsString)
            {
                case "renderers": _loadRenderers = done; _loadRenderersTotal = total; break;
                case "textures": _loadTextures = done; _loadTexturesTotal = total; break;
                case "done": ShowLoad(95, "장면 마무리 중"); return;
            }
            if (!_loadWatch.IsRunning) _loadWatch.Restart();   // 렌더러가 다시 시작해 스스로 다시 불러올 때 ("start"부터 온다)
            double pct = (m["stage"].AsString == "start" ? 0 : 5)
                + 35.0 * _loadRenderers / Math.Max(1, _loadRenderersTotal)
                + 55.0 * _loadTextures / Math.Max(1, _loadTexturesTotal);
            var what = _loadTexturesTotal > 0 && _loadTextures < _loadTexturesTotal ? $"텍스처 {_loadTextures}/{_loadTexturesTotal}" : $"메시 {_loadRenderers}/{_loadRenderersTotal}";
            ShowLoad(Math.Min(94, pct), "씬 불러오는 중 · " + what);
        }

        /// <summary>pct &lt; 0 이면 숨긴다.</summary>
        void ShowLoad(double pct, string text)
        {
            bool on = pct >= 0;
            _loadBar.IsVisible = _loadText.IsVisible = on;
            if (!on) return;
            _loadBar.Value = pct;
            _loadText.Text = $"{pct:0}%";
            _sceneItem.Set(StatusItem.State.Busy, text);
        }

        // ---------------- 입력 ----------------
        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // 물리 키를 쓰면 한국어 IME 상태와 관계없이 Ctrl+Z 등이 같은 키로 온다
            var key = KeyName(e.PhysicalKey) ?? e.Key.ToString();
            var m = e.KeyModifiers;
            if (HandleShortcut(key, m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Alt)))
                e.Handled = true;
        }

        static string? KeyName(PhysicalKey k)
        {
            if (k >= PhysicalKey.A && k <= PhysicalKey.Z) return ((char)('A' + (k - PhysicalKey.A))).ToString();
            if (k >= PhysicalKey.Digit1 && k <= PhysicalKey.Digit9) return "D" + (1 + (k - PhysicalKey.Digit1));
            if (k >= PhysicalKey.NumPad1 && k <= PhysicalKey.NumPad9) return "NumPad" + (1 + (k - PhysicalKey.NumPad1));
            return null;
        }

        /// <summary>떠 있는 창에서 누른 키: 에디터 단축키처럼 (입력 칸 확인은 그 창의 포커스로).</summary>
        bool HandleFloatKey(FloatPanel f, KeyEventArgs e)
        {
            var key = KeyName(e.PhysicalKey) ?? e.Key.ToString();
            var m = e.KeyModifiers;
            return HandleShortcut(key, m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Alt), f.FocusManager);
        }

        bool HandleShortcut(string key, bool ctrl, bool shift, bool alt, Avalonia.Input.IFocusManager? focus = null)
        {
            // 텍스트 입력 중에는 글자와 그 칸의 Ctrl+Z/Y를 우선한다
            if ((focus ?? FocusManager)?.GetFocusedElement() is TextBox tb && !tb.IsReadOnly)
            {
                if (!ctrl) return false;
                if (key == "Z" && (shift ? tb.CanRedo : tb.CanUndo)) return false;
                if (key == "Y" && tb.CanRedo) return false;
            }
            if (ctrl)
            {
                // Ctrl+Shift+1~9: 최근 파일 (MainWindow.Recent.cs)
                if (shift && key.Length == 2 && key[0] == 'D' && key[1] is >= '1' and <= '9') return OpenRecentAt(key[1] - '1');
                switch (key)
                {
                    case "Z": if (shift) Redo(); else Undo(); return true;
                    case "R" when shift && RunMode.Test: Texts.Reload(); return true;
                    case "T" when shift && RunMode.Test: Texts.Enabled = !Texts.Enabled; SetStatus(Texts.Enabled ? "글 표의 바꾼 글로 보여요" : "코드의 원문으로 보여요 (Ctrl+Shift+T로 되돌리기)"); return true;
                    case "Y": Redo(); return true;
                    case "S": _ = SaveSession(shift); return true;
                    case "O": if (shift) OpenSessionDialog(); else OpenIrDialog(); return true;
                    case "I": ImportUnityDialog(); return true;
                    case "U": if (shift) ExportDialog(); else UploadDialog(); return true;
                    case "A": if (shift) { AddItemDialog(); return true; } return false;
                    case "P": if (shift) { ShaderPackDialog(); return true; } return false;
                    case "OemComma": SettingsDialog(); return true;
                    case "K": OpenSearch(); return true;
                    case "OemQuestion": case "Oem2": case "Divide": ShowShortcuts(); return true;   // Ctrl+/ 단축키 목록 (MainWindow.Keys.cs)
                    case "D1": _side.Select("avatar"); return true;
                    case "D2": _side.Select("material"); return true;
                    case "D3": _side.Select("shapes"); return true;
                    case "D4": _side.Select("pose"); return true;
                    case "D5": _side.Select("environment"); return true;
                    case "D6": _side.Select("studio"); return true;
                    case "D7": _side.Select("optimize"); return true;
                    case "D8": _side.Select("upload"); return true;
                }
                return false;
            }
            // Alt+1~9: 스튜디오에 저장한 카메라 위치
            if (alt && key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return _studioPanel?.Recall(key[1] - '0') ?? false;
            if (alt && key.StartsWith("NumPad") && key.Length == 7 && char.IsDigit(key[6])) return _studioPanel?.Recall(key[6] - '0') ?? false;
            if (alt) return false;
            // Shift+1~9: 내 토글 (아바타 페이지), F1~F9: 표정 단축키 (표정 페이지)
            if (shift && key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) && key[1] != '0') return PressToggle(key[1] - '1');
            if (!shift && key.Length == 2 && key[0] == 'F' && char.IsDigit(key[1]) && key[1] != '0') return PressExpressionKey(key[1] - '1');
            if (_meshEditOn && MeshEditKey(key)) return true;
            if (_settings.ViewPoseBones && _posePanel != null)
                switch (key)
                {
                    case "R": ResetSelected(); return true;
                    case "C": _posePanel.CopySelectedBone(); return true;
                    case "L": SetHandleSpace(_settings.ViewHandleSpace == "world" ? "local" : "world"); return true;
                }
            switch (key)
            {
                case "D1": case "NumPad1": CameraPreset("full"); return true;
                case "D2": case "NumPad2": CameraPreset("upper"); return true;
                case "D3": case "NumPad3": CameraPreset("face"); return true;
                case "F": CameraPreset("front"); return true;
                case "B": CameraPreset("back"); return true;
                case "D5": case "NumPad5": SetOrtho(!IsOrtho()); return true;
                case "G": _settings.ViewGrid = !_settings.ViewGrid; ViewChanged(); _ctx.Status(_settings.ViewGrid ? "바닥 격자를 켰어요 (G)" : "바닥 격자를 껐어요 (G)"); return true;
                case "M": _settings.ViewMirror = !_settings.ViewMirror; ViewChanged(); _ctx.Status(_settings.ViewMirror ? "거울을 켰어요 (M)" : "거울을 껐어요 (M)"); return true;
                case "H": _settings.ViewAvatarOnly = !_settings.ViewAvatarOnly; ViewChanged(); _ctx.Status(_settings.ViewAvatarOnly ? "아바타만 보여요 (H로 되돌리기)" : "장면을 다시 보여요"); return true;
                case "P": TogglePoseBones(); return true;
                case "E": ToggleMeshEdit(); return true;
            }
            return false;
        }

        // ---------------- 쉬운 모드 · 전문가 모드 ----------------
        // 쉬운 모드는 자주 쓰지 않는 세부 설정(Ui.Expert로 표시한 것)을 숨긴다. 기능·값은 그대로고 보이는 것만 다르다.
        TextBlock? _modeIcon, _modeLabel;

        void ModeSwitch()
        {
            Ui.AddModeStyles(this);
            // 배경 없는 단추는 꺼져 있어도 회색 상자 없이 흐리게만 (Fluent 기본은 꺼진 단추에 배경을 깐다)
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("subtle").Class(":disabled").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent), new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, Brushes.Transparent) },
            });
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("subtle").Class(":disabled")) { Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.45) } });
            // 좁은 도구 모음: 글자 숨김(.compact) · 방향·거울은 "더 보기"로(.tight)
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<DockPanel>().Class("toolbar").Class("compact").Descendant().Class("tblabel")) { Setters = { new Avalonia.Styling.Setter(Visual.IsVisibleProperty, false) } });
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<DockPanel>().Class("toolbar").Class("tight").Descendant().Class("tbopt")) { Setters = { new Avalonia.Styling.Setter(Visual.IsVisibleProperty, false) } });
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<DockPanel>().Class("toolbar").Not(y => y.Class("tight")).Descendant().Class("tbmore")) { Setters = { new Avalonia.Styling.Setter(Visual.IsVisibleProperty, false) } });
            var (icon, label, item) = _side.AddBottomAction(Glyph.View, "", () => SetExpert(!_settings.ExpertMode));   // 톱니는 "설정"이 쓴다
            _modeIcon = icon; _modeLabel = label;
            ToolTip.SetTip(item, "쉬운 모드: 자주 쓰는 것만 · 전문가 모드: 세부 설정까지 모두 (눌러서 바꾸기)");
            SetExpert(_settings.ExpertMode, save: false);
        }

        void SetExpert(bool on, bool save = true)
        {
            _settings.ExpertMode = on;
            Classes.Set("easy", !on);
            _warmCheck = true;   // 전문가 모드로 바꾸면 새로 보이는 탭(아직 안 만든 전문가 섹션 포함)을 쉬는 동안 예열
            FitToolsLater();   // 메시 편집 단추가 생기거나 사라진다
            if (_modeLabel != null) _side.SetLabel(_modeLabel, on ? "전문가 모드" : "쉬운 모드");
            _modeIcon?.Fg(on ? Ui.KAccentText : Ui.KText2);
            if (!save) return;
            _settings.Save();
            SetStatus(on ? "전문가 모드: 세부 설정까지 모두 보여요." : "쉬운 모드: 자주 쓰는 것만 보여요. 바꾼 값은 그대로예요.");
        }

        // ---------------- 블렌드쉐이프 단축키 ----------------
        void SetupShapeKeys()
        {
            try { _hotkeys = new GlobalHotkeys(); }
            catch (Exception e) { AppendLog("warn", "전역 단축키를 사용할 수 없습니다: " + e.Message); }
            if (_hotkeys != null)
            {
                _hotkeys.Pressed += key =>
                {
                    if (_overlay?.PressKey(key) is string done) { SetStatus($"{done} ({GlobalHotkeys.Text(key)})"); return; }   // 방송 오버레이 단축키
                    var keys = _shapeRun.Keys;
                    for (int i = 0; i < keys.Count; i++)
                        if (keys[i]["key"].AsString == key) SetStatus($"{keys[i]["name"].AsString} {_shapeRun.Press(i)} ({GlobalHotkeys.Text(key)})");
                };
                _hotkeys.Registered += failed => _shapePanel.ShowFailed(failed);
            }
            _shapeRun.Changed += () => { if (_ctx.Render?.IsConnected == true) _ctx.Render.Send(_shapeRun.Message()); };
            void SyncKeys()
            {
                var keys = _ctx.Doc?.Get(Addr.ShapeKeys) ?? JNode.Arr();
                _shapeRun.Sync(keys);
                _hotkeys?.Set(keys.Items.Select(k => k["key"].AsString).Concat(_overlay?.Keys ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)));
            }
            if (_overlay != null) _overlay.KeysChanged += SyncKeys;
            SyncKeys();   // 문서를 열기 전에도 오버레이 단축키는 등록
            _ctx.DocumentOpened += () =>
            {
                _shapeRun.Clear();
                if (_ctx.Doc != null) _ctx.Doc.Changed += a => { if (a.Contains(Addr.ShapeKeys)) SyncKeys(); };
                SyncKeys();
            };
        }

        // ---------------- 방송 오버레이 · 컨트롤 보드 ----------------
        bool _overlayWasOn;

        void OnOverlayChanged()
        {
            bool on = _overlay.On;
            // 미리보기 자리에는 안내와 끄기 단추 (Unity 창이 나가 있는 동안 빈 검은 칸 대신)
            _viewport.IsVisible = !on;
            if (_overlayAway != null) _overlayAway.IsVisible = on;
            if (on && !_overlayWasOn && _settings.BoardAutoOpen) OpenBoard();
            _overlayWasOn = on;
            SendQuality();
        }

        void OpenBoard()
        {
            if (_board != null) { _board.Activate(); return; }
            _board = new OverlayBoard(_ctx, _settings, _overlay, _exprPanel, _shapeRun, _posePanel!, n => _studioPanel?.Recall(n) ?? false,
                () => IsVisible, show => { if (show) { Show(); Activate(); } else Hide(); });
            _board.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);   // F1~F9·Shift+1~9·Alt+1~9가 보드에서도
            _board.Closed += (_, __) => _board = null;
            _board.Show();
        }

        bool PressToggle(int i)
        {
            if (_ctx.Doc == null) return false;
            var list = _ctx.Doc.Get(Addr.Toggles);
            if (i >= list.Count) { SetStatus($"Shift+{i + 1}에 토글이 없어요. 아바타 페이지 \"내 토글\"에서 만들어요."); return true; }
            bool on = ToggleSet.Press(_ctx.Doc, i) == true;
            SetStatus($"토글 {list[i]["name"].AsString} {(on ? "켬" : "끔")} (Shift+{i + 1})");
            return true;
        }

        bool PressExpressionKey(int slot)
        {
            if (_ctx.Doc == null) return false;
            var r = ToggleSet.PressExpression(_ctx.Doc, slot);
            SetStatus(r == null ? $"F{slot + 1}에 표정이 없어요. 표정 페이지에서 표정 단추를 오른쪽 클릭해 넣어요." : r.Length == 0 ? $"표정을 뺐어요 (F{slot + 1})" : $"{r} 표정 (F{slot + 1})");
            return true;
        }

        static string[] DroppedFiles(DragEventArgs e)
        {
            #pragma warning disable CS0618
            return e.Data.GetFiles()?.Select(x => x.TryGetLocalPath()).Where(x => x != null).Select(x => x!).ToArray() ?? Array.Empty<string>();
            #pragma warning restore CS0618
        }

        // 끌고 있는 동안 (2026-10-27): 놓을 수 있는 파일인지 커서로, 놓으면 무엇을 하는지 상태 줄로. 같은 파일은 한 번만 가린다 (PNG는 세션을 읽어 봐야 한다)
        string? _dragPath, _dragAct;
        void OnDragOver(object? sender, DragEventArgs e)
        {
            var all = DroppedFiles(e);
            var first = all.FirstOrDefault(Packages.IsPackage) ?? all.FirstOrDefault();
            if (first == null) { e.DragEffects = DragDropEffects.None; return; }
            if (first != _dragPath)
            {
                _dragPath = first;
                _dragAct = OpenPathAction(first);
                SetStatus(_dragAct != null ? $"놓으면: {_dragAct} · {Path.GetFileName(first)}" : $"이 파일은 놓을 수 없어요 · {Path.GetFileName(first)}");
            }
            e.DragEffects = _dragAct != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDrop(object? sender, DragEventArgs e)
        {
            _dragPath = null;
            var all = DroppedFiles(e);
            var pkgs = all.Where(Packages.IsPackage).ToArray();
            if (pkgs.Length > 0) OnPackagesDropped(pkgs);
            else if (all.Length > 0) OpenPath(all[0]);
        }

        // 알림은 6초 뒤 흐려진다 (지난 알림이 계속 새것처럼 남아 있었다). 마우스를 올리면 전체 글
        DispatcherTimer? _statusFade;
        void SetStatus(string s)
        {
            _status.Text = s; ToolTip.SetTip(_status, s.Length > 0 ? s : null);
            _status.Opacity = 1;
            _statusFade ??= new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, __) => { _statusFade!.Stop(); _status.Opacity = 0.45; });
            _statusFade.Stop(); _statusFade.Start();
        }

        // 로그는 모아 두었다가 한 번에 화면에 반영한다 (경고가 수백 줄 몰려와도 줄마다 TextBox 전체를 다시 그리지 않게)
        readonly System.Text.StringBuilder _logBuf = new System.Text.StringBuilder();
        bool _logPending;

        void AppendLog(string level, string text)
        {
            _logBuf.Append(DateTime.Now.ToString("HH:mm:ss")).Append(" [").Append(level).Append("] ").Append(text).Append('\n');
            if (_logBuf.Length > 200_000) _logBuf.Remove(0, _logBuf.Length / 2);
            if (_logPending) return;
            _logPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _logPending = false;
                _log.Text = _logBuf.ToString();
                _log.CaretIndex = _log.Text.Length;
            }, DispatcherPriority.Background);
        }
    }
}
