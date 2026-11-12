// 메인 창: 오른쪽 3D 미리보기 카드 (카메라 단추 · 표시 메뉴 · PhysBone 메뉴).
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
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        /// <summary>오른쪽 3D 미리보기 카드: 카메라 단추 줄 + Unity 화면.</summary>
        Control Preview()
        {
            var card = new DockPanel();
            // 도구 모음은 한 줄: 미리보기가 좁으면 글자를 숨겨 아이콘만(.compact), 더 좁으면 방향·거울을 "더 보기" 메뉴로(.tight)
            var tools = new DockPanel { Margin = new Thickness(10, 6, 6, 6), ClipToBounds = true };
            tools.Classes.Add("toolbar");
            var cams = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var label = Ui.Text("카메라", 13, FontWeight.SemiBold); label.Margin = new Thickness(4, 0, 10, 0); label.Classes.Add("tblabel");
            cams.Children.Add(label);
            cams.Children.Add(Ui.Subtle(Glyph.Person, "전신", () => CameraPreset("full"), "전신 (1)"));
            cams.Children.Add(Ui.Subtle(Glyph.Contact, "상반신", () => CameraPreset("upper"), "상반신 (2)"));
            cams.Children.Add(Ui.Subtle(Glyph.Search, "얼굴", () => CameraPreset("face"), "얼굴 (3)"));
            Control Opt(Control c) { c.Classes.Add("tbopt"); return c; }
            cams.Children.Add(Opt(Divider()));
            cams.Children.Add(Opt(Ui.Subtle(Glyph.Front, "정면", () => CameraPreset("front"), "정면 (F)")));
            cams.Children.Add(Opt(Ui.Subtle(Glyph.Side, "측면", () => CameraPreset("side"), "측면")));
            cams.Children.Add(Opt(Ui.Subtle(Glyph.Back, "후면", () => CameraPreset("back"), "후면 (B)")));
            cams.Children.Add(Divider());
            _mirrorBtn = Ui.Subtle(Glyph.Mirror, "거울", () => { _settings.ViewMirror = !_settings.ViewMirror; ViewChanged(); }, "거울 보기: VRChat 거울처럼 좌우를 뒤집어요 (M)");
            cams.Children.Add(Opt(_mirrorBtn));
            _poseBtn = Ui.Subtle(Glyph.Pose, "조작", TogglePoseBones, "조작 (P): 관절을 끌어 자세를 바꿔요 · 더블클릭으로 본·아이템·아바타 고르기 → 이동 화살표·회전 고리로 옮기고 돌리기 · 잡은 채 휠로 돌리기(양쪽 버튼+휠: 대각선 축, Shift+휠: 비틀기). 켜 두면 PhysBone 관절 잡기는 쉬어요");
            cams.Children.Add(_poseBtn);
            _meshBtn = Ui.Subtle(Glyph.Edit, "메시 편집", ToggleMeshEdit, "메시 편집 (E): 정점을 골라 지우기(X)·합치기(M)·면 만들기(F)·끌어 옮기기, 누르기 브러시(B). 편집할 메시는 더블클릭으로 골라요");
            cams.Children.Add(Ui.Expert(_meshBtn));   // 정점 편집은 전문가 모드에서만
            _origBtn = Ui.Subtle(Glyph.Hide, "원본", ToggleOriginal, "원본과 비교 (O): 고치기 전 모습을 잠깐 봐요. 다시 누르거나 값을 바꾸면 고친 모습으로 돌아와요.\n머티리얼·블렌드쉐이프·켜고 끄기·메시·아이템 위치만 원본으로 보이고, 자세·표정·조명은 그대로예요.");
            cams.Children.Add(Opt(_origBtn));
            cams.Children.Add(MoreMenu());
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(VrMenu());
            right.Children.Add(PhysBoneMenu());
            right.Children.Add(ViewMenu());
            right.Children.Add(Ui.Subtle(Glyph.Refresh, null, RestartRenderer, "미리보기 다시 시작"));
            DockPanel.SetDock(right, Dock.Right);
            tools.Children.Add(right);
            tools.Children.Add(cams);
            // 에디터 상단 바: 옅은 파란 바탕 + 아래 선. 카드 배치에서는 위 모서리만 둥글게 (MainWindow.Layout.cs)
            _toolBar = new Border { Child = tools, BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(10, 10, 0, 0), Margin = new Thickness(0, 0, 0, 8) }.Bg(Ui.KToolbar).Stroke(Ui.KDivider);
            DockPanel.SetDock(_toolBar, Dock.Top);
            card.Children.Add(_toolBar);
            // 넘치면 왼쪽 묶음은 잘리기만 하고 오른쪽 단추 위로 겹쳐 그리지 않게
            cams.ClipToBounds = true;
            // 단계는 고정 너비가 아니라 실제로 재서 고른다 (전문가 모드·고른 것 이름·PhysBone 개수에 따라 너비가 바뀐다)
            _fitTools = () =>
            {
                double avail = tools.Bounds.Width;
                if (avail <= 0) return;
                foreach (var (c, t) in new[] { (false, false), (true, false), (true, true) })
                {
                    tools.Classes.Set("compact", c); tools.Classes.Set("tight", t);
                    // 글자를 숨긴 단추 안쪽만 무효가 되어 바깥은 예전 크기를 돌려주므로, 전부 무효로 하고 다시 잰다
                    foreach (var v in tools.GetVisualDescendants()) if (v is Layoutable l) l.InvalidateMeasure();
                    cams.Measure(Size.Infinity); right.Measure(Size.Infinity);
                    if (cams.DesiredSize.Width + right.DesiredSize.Width <= avail) break;
                }
                tools.InvalidateMeasure();
            };
            tools.SizeChanged += (_, __) => _fitTools();
            FitToolsLater = () => Dispatcher.UIThread.Post(() => _fitTools?.Invoke(), DispatcherPriority.Background);
            // 아래 안내: 짧은 한 줄 + 오른쪽 키보드 단추(전체 조작법)
            var hintRow = _hintRow = new DockPanel { Margin = new Thickness(14, 4, 8, 6) };
            _hintHelp = Ui.Help("");
            _hintHelp.Content = Ui.Icon(Glyph.Keyboard, 14, Ui.KText3);
            DockPanel.SetDock(_hintHelp, Dock.Right);
            hintRow.Children.Add(_hintHelp);
            // 지금 켜진 도구 이름 (조작·메시 편집·브러시): 도구 모음이 좁아 아이콘만 보일 때도 무엇이 켜졌는지 보이게
            _toolChip = new Border { Child = _toolChipText, Padding = new Thickness(8, 1), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false }.Bg(Ui.KSelect);
            DockPanel.SetDock(_toolChip, Dock.Left);
            hintRow.Children.Add(_toolChip);
            var hint = _hint = Ui.Text("", 12, FontWeight.Normal, Ui.KText3);
            hintRow.Children.Add(hint);
            RefreshHint();
            DockPanel.SetDock(hintRow, Dock.Bottom);
            card.Children.Add(hintRow);
            // 렌더러 보기 바로 아래: 가로 세밀 조정 막대 + 입력 바 (FineStrip.cs)
            _strip = new FineStrip(_settings.FineStripOpen);
            _strip.OpenChanged += open => { _settings.FineStripOpen = open; _settings.Save(); };
            DockPanel.SetDock(_strip, Dock.Bottom);
            card.Children.Add(_strip);
            InputBar.Inline = w => w == this ? _strip : null;
            // 접혀 있을 때 여는 단추: 안내 줄 오른쪽(키보드 단추 옆) — 손잡이 줄을 따로 두지 않아 한 줄로 끝난다
            var stripOpen = Ui.IconButton(Glyph.ChevronUp, () => _strip.SetOpen(true), "가로 세밀 조정 막대 열기\n슬라이더나 숫자를 누르면 그 값을 큰 가로 막대로 조정해요 · 입력 칸을 길게 누르면 여기에 입력 키가 떠요");
            stripOpen.Width = 24; stripOpen.Height = 24; stripOpen.VerticalAlignment = VerticalAlignment.Center;
            stripOpen.IsVisible = !_strip.IsOpen;
            _strip.OpenChanged += open => stripOpen.IsVisible = !open;
            DockPanel.SetDock(stripOpen, Dock.Right);
            hintRow.Children.Insert(1, stripOpen);
            // 방송 오버레이 중에는 Unity 창이 밖에 나가 있으므로 이 자리에 안내와 끄기 단추를 보인다 (MainWindow.OnOverlayChanged)
            var away = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
            away.Children.Add(Ui.Text("3D 미리보기는 방송 오버레이로 나가 있어요", 14, FontWeight.SemiBold, Ui.KText2));
            var awayBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
            awayBtns.Children.Add(Ui.Button("오버레이 끄기", () => _overlay.SetOn(false), "미리보기로 되돌려요", Glyph.ClosePane, accent: true));
            awayBtns.Children.Add(Ui.Button("컨트롤 보드", OpenBoard, "방송 화면에 보이지 않는 작은 창", Glyph.Pin));
            away.Children.Add(awayBtns);
            _overlayAway = new Border { Child = away, IsVisible = false };
            _viewFrame = new Border { Child = new Panel { Children = { _viewport, _overlayAway } }, Margin = new Thickness(8, 0, 8, 0), Background = Brushes.Black };
            card.Children.Add(_viewFrame);
            var b = new Border { Child = card, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            return b.Bg(Ui.KPanel).Stroke(Ui.KCardStroke);
        }

        Border? _overlayAway, _toolChip, _viewFrame, _toolBar;
        DockPanel? _hintRow;
        FineStrip? _strip;
        readonly TextBlock _toolChipText = Ui.Text("", 11, FontWeight.SemiBold, Ui.KAccentText);

        // ---------------- 3D 미리보기 보조 표시 ----------------
        Button _mirrorBtn = null!, _poseBtn = null!, _meshBtn = null!;
        bool _meshEditOn, _meshBrush;
        string _meshSel = "";

        // ---------------- 메시 편집 (PLAN 3.9) ----------------
        void ToggleMeshEdit()
        {
            if (_ctx.Doc == null) { SetStatus("먼저 아바타를 열어 주세요."); return; }
            _meshEditOn = !_meshEditOn;
            if (!_meshEditOn) _meshBrush = false;
            var m = JNode.Obj().Set("enabled", _meshEditOn);
            if (_meshEditOn && _ctx.SelectedRenderer != null) m.Set("renderer", _ctx.SelectedRenderer);
            SendMeshTool(m);
            SetStatus(_meshEditOn ? "메시 편집을 켰어요. 편집할 메시를 더블클릭한 뒤 정점을 고르세요. 실행 취소(Ctrl+Z)로 되돌려요." : "메시 편집을 껐어요.");
            RefreshViewMenu();
        }

        void SendMeshTool(JNode m) { m.Set("t", Msg.MeshTool); _ctx.Render?.Send(m); }

        /// 메시 편집 중에는 Blender와 같은 글쇠를 먼저 쓴다 (F·B·M 카메라·거울 단축키 대신)
        bool MeshEditKey(string key)
        {
            string? action = key switch
            {
                "X" or "Delete" => "delete", "M" => "merge", "F" => "face", "N" => "normals", "A" => "selectAll", "L" => "selectLinked", "B" => "toggleBrush", _ => null,
            };
            if (action == null) return false;
            if (action == "toggleBrush") { _meshBrush = !_meshBrush; RefreshHint(); }
            SendMeshTool(JNode.Obj().Set("action", action));
            return true;
        }

        void OnMeshSelection(JNode m)
        {
            var name = _ctx.Doc?.Original.FindRenderer(m["renderer"].AsString) is IrRenderer r && r.Node >= 0 && r.Node < _ctx.Doc.Original.Nodes.Count ? _ctx.Doc.Original.Nodes[r.Node].Name : "";
            _meshSel = name.Length == 0 ? "" : $"{name} · 정점 {m["vertices"].AsInt:N0} · 삼각형 {m["triangles"].AsInt:N0} · 고른 정점 {m["count"].AsInt:N0}";
            _meshBrush = m["brush"].AsBool;
            if (_meshSel.Length > 0) SetStatus("메시 편집: " + _meshSel);
            RefreshHint();
        }

        TextBlock _hint = null!;
        Button _hintHelp = null!;

        /// <summary>좁은 미리보기에서만 보이는 "더 보기": 정면·측면·후면·거울.</summary>
        Button MoreMenu()
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            // 측면은 단축키 없음: S는 WSAD 카메라 회전에 쓴다
            foreach (var (text, gesture, act) in new (string, string?, Action)[]
            {
                ("정면", "F", () => CameraPreset("front")), ("측면", null, () => CameraPreset("side")), ("후면", "B", () => CameraPreset("back")),
                ("거울 보기 (좌우 반전)", "M", () => { _settings.ViewMirror = !_settings.ViewMirror; ViewChanged(); }),
            })
            {
                var mi = new MenuItem { Header = text, InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null };
                mi.Click += (_, __) => act();
                menu.Items.Add(mi);
            }
            var b = Ui.Subtle(Glyph.More, null, () => { }, "더 보기: 정면 · 측면 · 후면 · 거울");
            b.Flyout = menu;
            b.Classes.Add("tbmore");
            return b;
        }

        void TogglePoseBones()
        {
            _settings.ViewPoseBones = !_settings.ViewPoseBones;
            ViewChanged();
            if (!_settings.ViewPoseBones) OnSelected(JNode.Obj().Set("kind", ""));
            SetStatus(_settings.ViewPoseBones ? "조작을 켰어요. 관절을 끌거나, 본·아이템·아바타를 더블클릭으로 고른 뒤 화살표·고리를 끌어 보세요. 실행 취소(Ctrl+Z)로 되돌려요." : "조작을 껐어요.");
        }

        // ---------------- 선택 ----------------
        // 조작 모드에서 고른 것 하나에만 핸들을 그린다: 본(머슬로 기록) · 아이템(Addr.NodeTransform) · 아바타 전체(Addr.AvatarTransform).
        // 뷰포트 더블클릭이나 이 메뉴·아바타 페이지 아이템 줄의 "옮기기"로 고른다. 고른 상태의 원천은 렌더러(selected 메시지)다.
        string _selKind = "", _selBone = "";
        int _selNode = -1;
        string _selText = "";   // 고른 것 이름 (미리보기 아래 도구 이름 옆에 보인다 — 도구 모음의 "고른 것" 메뉴는 2026-10-21 뺐다)

        /// <summary>목록에서 고르기: 조작 모드를 켜고 렌더러에 고를 것을 보낸다 (결과는 selected로 돌아온다).</summary>
        void SelectTarget(string kind, int node, string bone)
        {
            if (_ctx.Doc == null) return;
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { SetStatus("미리보기가 켜져야 고를 수 있어요."); return; }
            if (kind.Length > 0 && !_settings.ViewPoseBones) { _settings.ViewPoseBones = true; ViewChanged(); }
            var m = Msg.Make(Msg.Select).Set("kind", kind);
            if (node >= 0) m.Set("node", node);
            if (bone.Length > 0) m.Set("bone", bone);
            _ctx.Render.Send(m);
            _viewport.Activate();
        }

        Action? _fitTools;
        Action FitToolsLater = () => { };

        void OnSelected(JNode m)
        {
            _selKind = m["kind"].AsString; _selNode = m.Has("node") ? m["node"].AsInt : -1; _selBone = m["bone"].AsString;
            var doc = _ctx.Doc;
            _selText = _selKind switch
            {
                "avatar" => "아바타 전체",
                "guest" => "함께 세운 아바타",
                "item" => doc?.Original.Outfits.FirstOrDefault(o => o.Node == _selNode)?.Name ?? "아이템",
                "bone" => HumanBones.Label(_selBone),
                _ => "",
            };
            RefreshHint();
        }

        void SetHandleSpace(string space)
        {
            _settings.ViewHandleSpace = space;
            ViewChanged();
            SetStatus(space == "world" ? "핸들 축: 월드 (X 오른쪽 · Y 위 · Z 앞)" : "핸들 축: 고른 것 기준 (로컬)");
        }

        /// <summary>R: 고른 본은 불러온 자세로, 아이템은 붙인 자리로, 아바타는 원점으로.</summary>
        void ResetSelected()
        {
            var doc = _ctx.Doc;
            if (doc == null) return;
            switch (_selKind)
            {
                case "item":
                    if (!doc.IsModified(Addr.NodeTransform(_selNode))) { SetStatus("이 아이템은 옮기지 않았어요."); return; }
                    doc.Reset(Addr.NodeTransform(_selNode), "아이템 자리 되돌리기"); SetStatus("아이템을 붙인 자리로 되돌렸어요."); break;
                case "avatar":
                    if (!doc.IsModified(Addr.AvatarTransform)) { SetStatus("아바타는 원점에 있어요."); return; }
                    doc.Reset(Addr.AvatarTransform, "아바타 자리 되돌리기"); SetStatus("아바타를 원점으로 되돌렸어요."); break;
                default: _posePanel?.ResetSelectedBone(); break;
            }
        }

        void RefreshHint()
        {
            var (shortText, full) = _meshEditOn
                ? (_meshBrush
                    ? ("누르기 브러시 · 끌기: 누르기 · Shift+끌기: 당기기 · B: 끄기",
                       "누르기 브러시\n끌기: 안쪽으로 누르기 · Shift+끌기: 당기기\nCtrl+휠: 반지름 · Ctrl+Shift+휠: 세기\nB: 브러시 끄기 · 오른쪽 드래그: 카메라 · E: 끝내기")
                    : ("메시 편집 · 더블클릭: 메시 고르기 · E: 끝내기",
                       "메시 편집\n더블클릭: 메시 고르기\n클릭/끌기: 정점 고르기 (Shift 더하기, Ctrl 빼기)\n고른 정점 끌기: 옮기기\nX 삭제 · M 합치기 · F 면 · N 노멀 · A 전체 · L 이어진 것 · B 브러시\n오른쪽 드래그: 카메라 · E: 끝내기"))
                : _settings.ViewPoseBones
                ? ("조작 중 · 관절 끌기 · 더블클릭: 고르기 · P: 끄기",
                   "조작 중\n관절 끌기: 옮기기 (손·발은 팔·다리 전체)\n더블클릭: 본(관절)·아이템·아바타(메시) 고르기\n화살표: 그 축으로 옮기기 · 고리: 그 축으로 돌리기 (빨강 X·초록 Y·파랑 Z·흰색 화면)\nL: 로컬/월드 축 · 끄는 중 휠: 화면 축 · Shift+휠: 비틀기\nR: 고른 것 되돌리기 · C: 반대쪽 본에 복사\n오른쪽 드래그: 카메라 · P: 끄기")
                : _settings.ViewNav == "unity"
                ? ("오른쪽 드래그+WASD·QE: 날기 · Alt+드래그: 공전 · 휠: 확대",
                   "Unity처럼 조작\n오른쪽 드래그: 둘러보기 · 누른 채 WASD 앞뒤·옆, Q·E 아래·위 (Shift 빠르게, 휠로 빠르기)\nAlt+왼쪽 드래그: 공전 · Alt+오른쪽 드래그: 확대 · 가운데 드래그·화살표: 이동 · 휠: 확대\n오른쪽 위 축을 누르면 그 방향에서 봐요\n1 전신 · 2 상반신 · 3 얼굴 · F 정면 · B 후면\n5: 원근/직교 · G: 격자 · M: 거울\nP: 조작 (본·아이템·아바타) · E: 메시 편집\n조작 방식은 설정에서 바꿔요")
                : ("드래그·WSAD: 회전 · Shift+드래그·화살표: 이동 · 휠: 확대",
                   "드래그·WSAD: 회전 · Shift+드래그·휠 버튼·화살표: 이동 · 휠: 확대\n오른쪽 위 축을 누르면 그 방향에서 봐요\n1 전신 · 2 상반신 · 3 얼굴 · F 정면 · B 후면\n5: 원근/직교 · G: 격자 · M: 거울\nP: 조작 (본·아이템·아바타) · E: 메시 편집");
            _hint.Text = shortText;
            Ui.SetHelp(_hintHelp, full);
            var tool = _meshEditOn ? (_meshBrush ? "누르기 브러시" : "메시 편집") : _settings.ViewPoseBones ? (_selText.Length > 0 ? "조작 · " + _selText : "조작") : null;
            if (_toolChip != null) { _toolChip.IsVisible = tool != null; _toolChipText.Text = tool ?? ""; }
        }
        readonly List<(MenuItem Item, Func<bool> IsOn)> _viewItems = new List<(MenuItem, Func<bool>)>();

        /// <summary>"표시" 메뉴: 기즈모·격자·본, 그리기 방식, 투영, 배경. 고른 값은 avhost.json에 기억한다.</summary>
        Button ViewMenu()
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

            MenuItem Check(string text, Func<bool> isOn, Action toggle, string? gesture = null)
            {
                var mi = new MenuItem { Header = text, ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true };
                if (gesture != null) mi.InputGesture = KeyGesture.Parse(gesture);
                mi.Click += (_, __) => { toggle(); ViewChanged(); };
                _viewItems.Add((mi, isOn));
                return mi;
            }
            // 하나만 고르는 묶음: 값(value)을 설정 항목에 그대로 넣는다
            MenuItem Group(string text, Func<string> get, Action<string> set, params (string Label, string Value)[] options)
            {
                var g = new MenuItem { Header = text };
                foreach (var (label, value) in options)
                {
                    var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = text };
                    mi.Click += (_, __) => { set(value); ViewChanged(); };
                    _viewItems.Add((mi, () => get() == value));
                    g.Items.Add(mi);
                }
                return g;
            }

            menu.Items.Add(Check("방향 기즈모", () => _settings.ViewGizmo, () => _settings.ViewGizmo = !_settings.ViewGizmo));
            menu.Items.Add(Check("바닥 격자", () => _settings.ViewGrid, () => _settings.ViewGrid = !_settings.ViewGrid, "G"));
            menu.Items.Add(Check("본 표시", () => _settings.ViewBones, () => _settings.ViewBones = !_settings.ViewBones));
            menu.Items.Add(Check("거울 보기 (좌우 반전)", () => _settings.ViewMirror, () => _settings.ViewMirror = !_settings.ViewMirror, "M"));
            menu.Items.Add(Check("구도 안내선 (3분할 · 촬영 비율)", () => _settings.ViewThirds, () => _settings.ViewThirds = !_settings.ViewThirds));
            menu.Items.Add(new Separator());
            menu.Items.Add(Check("직교 투영", IsOrtho, () => SetOrtho(!IsOrtho()), "D5"));
            menu.Items.Add(Group("그리기 방식", () => _settings.ViewDraw, v => _settings.ViewDraw = v,
                ("셰이딩", "shaded"), ("셰이딩 + 와이어프레임", "shadedWire"), ("와이어프레임", "wire")));
            menu.Items.Add(Check("아바타만 보기 (소품·바닥·배경 빼고 단색)", () => _settings.ViewAvatarOnly, () => _settings.ViewAvatarOnly = !_settings.ViewAvatarOnly, "H"));
            menu.Items.Add(Group("배경", () => _settings.ViewBackground, v => _settings.ViewBackground = v,
                ("환경 설정 그대로", "env"), ("회색", "gray"), ("흰색", "white"), ("검정", "black"), ("초록 (크로마키)", "green")));
            menu.Opening += (_, __) => RefreshViewMenu();
            return Ui.DropButton(Glyph.View, "표시", menu, out _, "미리보기에 보조 표시를 켜고 꺼요 (촬영에는 들어가지 않아요)");
        }

        // ---------------- PhysBone 미리보기 ----------------
        MenuItem _pbToggle = null!, _softMenu = null!, _legGuard = null!;
        TextBlock _pbLabel = null!;
        int _pbChains, _pbSoft, _pbLegGuard;
        readonly List<(MenuItem Item, string Id)> _softItems = new List<(MenuItem, string)>();
        static readonly (string Label, double W)[] WeightPresets = { ("가볍게", 1.5), ("보통", 4), ("무겁게", 10) };
        MenuItem _weightMenu = null!;
        readonly List<(MenuItem, double)> _weightItems = new List<(MenuItem, double)>();
        static readonly (string Label, string Id)[] SoftPresets = { ("끔", "off"), ("단단함", "light"), ("중간", "normal"), ("부드러움", "smooth") };

        /// <summary>"PhysBone" 메뉴: 켜기·끄기, 다시 놓기, 아바타를 흔들어 반응 보기 (렌더러 PhysBoneSim).</summary>
        Button PhysBoneMenu()
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            _pbToggle = new MenuItem { Header = "PhysBone 켜기", ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true };
            _pbToggle.Click += (_, __) => { _settings.PhysBones = !_settings.PhysBones; _settings.Save(); SendPhysBones(); RefreshPhysBones(); };
            menu.Items.Add(_pbToggle);
            var gizmos = new MenuItem { Header = "체인·콜라이더 표시 (끌어 잡기 · 오른쪽 클릭 고정)", ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true };
            ToolTip.SetTip(gizmos, "켜면 흔들리는 뼈(초록)와 콜라이더(주황·보라)가 보여요. 관절을 왼쪽 버튼으로 끌면 손으로 잡은 것처럼 당겨지고, 잡은 채 오른쪽 클릭하면 그 자리에 고정돼요(빨강). 고정한 관절을 오른쪽 클릭하면 풀려요");
            gizmos.Click += (_, __) => { _settings.ViewPhysBones = !_settings.ViewPhysBones; ViewChanged(); };
            _viewItems.Add((gizmos, () => _settings.ViewPhysBones));
            menu.Items.Add(gizmos);
            var unpin = new MenuItem { Header = "고정 모두 풀기" };
            unpin.Click += (_, __) => _ctx.Render?.Send(Msg.Make(Msg.SetPhysBones).Set("unpinAll", true));
            menu.Items.Add(unpin);
            var reset = new MenuItem { Header = "제자리에 다시 놓기 (고정도 풀기)" };
            reset.Click += (_, __) => _ctx.Render?.Send(Msg.Make(Msg.SetPhysBones).Set("reset", true));
            menu.Items.Add(reset);
            // 말랑 모드: 가슴 PhysBone을 눌리고 늘어지고 출렁이게 (이 에디터 미리보기 전용, 프리셋만)
            _softMenu = new MenuItem();
            ToolTip.SetTip(_softMenu, "가슴 PhysBone(이름에 breast·bust·胸 등)에 가슴 물리를 더해요: 눌리면 납작해지며 옆으로 퍼지고, 누우면 납작·숙이면 늘어지고, 더 크게 출렁여요. 체인을 잡아 몸 쪽으로 밀면 눌려요. 이 미리보기에서만 쓰는 효과예요");
            foreach (var (label, id) in SoftPresets)
            {
                var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "pbSoft" };
                mi.Click += (_, __) => { _settings.PhysBoneSoft = id; _settings.Save(); SendPhysBones(); RefreshPhysBones(); };
                _softItems.Add((mi, id));
                _softMenu.Items.Add(mi);
            }
            menu.Items.Add(_softMenu);
            // 다리 뚫림 막기: 엉덩이 아래로 늘어진 체인(치마·코트 자락·꼬리)에 허벅지·종아리 캡슐을 더한다 (에디터 전용)
            _legGuard = new MenuItem { ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true };
            ToolTip.SetTip(_legGuard, "치마·코트 자락처럼 엉덩이 아래로 늘어진 PhysBone이 다리를 뚫지 않게 허벅지·종아리에 콜라이더(하늘색)를 더해요. 크기는 다리 메시로 재요. 이 미리보기에서만 쓰는 보조라 VRChat에서는 원본에 넣은 콜라이더만 쓰여요");
            _legGuard.Click += (_, __) => { _settings.PhysBoneLegGuard = !_settings.PhysBoneLegGuard; _settings.Save(); SendPhysBones(); RefreshPhysBones(); };
            menu.Items.Add(_legGuard);
            // 무게감: 같은 값이라도 무거울수록 천천히 처지고 크게 휘어 돌아온다 (출렁이는 정도는 그대로)
            _weightMenu = new MenuItem();
            ToolTip.SetTip(_weightMenu, "머리카락·옷이 얼마나 묵직하게 움직일지예요. 무거울수록 몸을 따라 늦게 오고 천천히 제자리로 돌아오며, 빠르게 휙휙 튀지 않아요 (출렁이는 정도는 원본 값 그대로). 이 미리보기에서만 쓰는 값이에요");
            foreach (var (label, w) in WeightPresets)
            {
                var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "pbWeight" };
                mi.Click += (_, __) => { _settings.PhysBoneWeight = w; _settings.Save(); SendPhysBones(); RefreshPhysBones(); };
                _weightItems.Add((mi, w));
                _weightMenu.Items.Add(mi);
            }
            menu.Items.Add(_weightMenu);
            menu.Items.Add(new Separator());
            foreach (var (label, motion) in new[] { ("좌우로 흔들기", "sway"), ("몸 돌리기", "turn"), ("점프", "jump"), ("앞으로 걷기", "walk") })
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, __) =>
                {
                    if (!_settings.PhysBones) { _settings.PhysBones = true; _settings.Save(); RefreshPhysBones(); }
                    _ctx.Render?.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", true).Set("motion", motion));
                };
                menu.Items.Add(mi);
            }
            menu.Opening += (_, __) => { RefreshPhysBones(); RefreshViewMenu(); };
            return Ui.DropButton(Glyph.Physics, "PhysBone", menu, out _pbLabel, "흔들리는 머리카락·옷(VRChat PhysBone)을 미리 봐요. VRChat과 같은 계산이 아니라 비슷하게 흉내 낸 거예요");
        }

        void SendPhysBones() => _ctx.Render?.Send(Msg.Make(Msg.SetPhysBones).Set("enabled", _settings.PhysBones).Set("soft", _settings.PhysBoneSoft).Set("legGuard", _settings.PhysBoneLegGuard).Set("weight", _settings.PhysBoneWeight).Set("feel", PhysBoneFeel.Message(_settings.PhysBoneFeel)));

        void RefreshPhysBones()
        {
            _pbToggle.IsChecked = _settings.PhysBones;
            _pbLabel.Text = _pbChains > 0 ? $"PhysBone {_pbChains}" : "PhysBone";
            FitToolsLater();
            bool none = _ctx.Doc != null && _pbSoft == 0;
            var cur = Array.Find(SoftPresets, p => p.Id == _settings.PhysBoneSoft).Label ?? "끔";
            _softMenu.Header = none ? "가슴 물리 활성화 (가슴 PhysBone을 찾지 못했어요)" : $"가슴 물리 활성화: {cur}" + (_pbSoft > 0 ? $" (체인 {_pbSoft}개)" : "");
            _softMenu.IsEnabled = !none;
            foreach (var (item, id) in _softItems) item.IsChecked = id == _settings.PhysBoneSoft;
            _legGuard.IsChecked = _settings.PhysBoneLegGuard;
            foreach (var (item, w) in _weightItems) item.IsChecked = Math.Abs(w - _settings.PhysBoneWeight) < 0.01;
            _weightMenu.Header = "무게감: " + (Array.Find(WeightPresets, p => Math.Abs(p.W - _settings.PhysBoneWeight) < 0.01).Label ?? $"{_settings.PhysBoneWeight:0.#}");
            _legGuard.Header = "다리 관통 방지 (에디터 전용)" + (_ctx.Doc != null ? _pbLegGuard > 0 ? $" · 체인 {_pbLegGuard}개" : " · 해당 체인 없음" : "");
        }

        void RefreshViewMenu()
        {
            foreach (var (item, isOn) in _viewItems) item.IsChecked = isOn();
            // 도구 모음 단추는 기본이 투명 배경 (Bg(null)은 Fluent 기본 회색으로 돌아가므로 직접 지정)
            if (_settings.ViewMirror) _mirrorBtn.Bg(Ui.KSelect); else _mirrorBtn.Background = Brushes.Transparent;
            if (_settings.ViewPoseBones) _poseBtn.Bg(Ui.KSelect); else _poseBtn.Background = Brushes.Transparent;
            if (_meshEditOn) _meshBtn.Bg(Ui.KSelect); else _meshBtn.Background = Brushes.Transparent;
            RefreshHint();
        }

        void ViewChanged()
        {
            _settings.Save();
            SendView();
            RefreshViewMenu();
            _studioPanel?.ShowAvatarOnly();
        }

        /// <summary>렌더 품질: 프레임 제한 + 스튜디오 고화질 (스튜디오 페이지를 보거나 방송 오버레이 중일 때만 — 나머지 페이지는 VRChat과 같은 품질).</summary>
        void SendQuality()
        {
            bool studio = _settings.StudioQuality && (_pageKey == "studio" || (_overlay?.On ?? false));
            _ctx.Render?.Send(Msg.Make(Msg.SetQuality).Set("maxFps", _settings.RenderMaxFps).Set("studio", studio)
                .Set("ao", (float)_settings.StudioAo).Set("captureAa", _settings.StudioCaptureAa).Set("hidden", WindowState == WindowState.Minimized));
            _studioPanel?.SetShowing(_pageKey == "studio" && !(_overlay?.On ?? false));   // 스튜디오 바닥도 같은 때에만 (오버레이는 투명 창이라 뺀다)
        }

        // 창을 최소화하면 렌더러가 초당 5장만 그린다 (2026-11-02, RenderQuality.SetRest). VR·방송 오버레이·녹화는 그대로.
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty) _ctx?.Render?.Send(Msg.Make(Msg.SetQuality).Set("hidden", WindowState == WindowState.Minimized));
        }

        void SendView()
        {
            var m = _settings.ViewMessage();
            m.Set("t", Msg.SetView);
            _ctx.Render?.Send(m);
        }

        bool IsOrtho() => !_ctx.Camera.IsNull && _ctx.Camera["ortho"].AsBool;

        void SetOrtho(bool on)
        {
            // 카메라 상태의 원천은 렌더러가 보내는 cameraChanged. 미리 반영해 두어 메뉴가 바로 맞게 보이게 한다
            if (!_ctx.Camera.IsNull) _ctx.Camera.Set("ortho", on);
            _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("ortho", on));
        }

        void CameraPreset(string preset) => _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("preset", preset));
    }
}
