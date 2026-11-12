// 컨트롤 보드 (2026-10-20 사용자 요청): 방송하며 게임 옆에 띄워 두는 작은 창. 자주 누르는 것만 큰 단추로 (2차: 설명·세부 설정은 뺐다 — 그건 스튜디오 페이지에).
//   맨 위에 고정(핀) · 방송 화면에서 숨기기(SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE — 디스코드·OBS 화면 공유에 잡히지 않고 내 화면에만 보인다, Windows 10 2004 이상)
//   에디터 숨기기: 보드를 열면 에디터 창을 숨기고(설정 board.hideEditor), 보드의 "에디터 열기"로 다시 연다. 보드를 닫으면 에디터가 다시 보인다.
//   큰 단추: 오버레이 켜기·위치 조정 · 블렌드쉐이프 단축키 · 표정 F1~F9 · 자세(불러온 그대로·프리셋·저장한 자세) · 트래킹(끔·웹캠·외부 앱, 캘리브레이션) · 내 토글 · 카메라 위치
//   꾸미기(3차): 구역 순서(▲▼)·구역 숨기기·단추 하나씩 숨기기·한 줄 단추 수(1~3). 설정 board.order/hidden/columns. 꾸미는 중에는 단추를 눌러도 동작하지 않고 숨김만 바뀐다.
// 값과 동작은 모두 원래 것(OverlayController·ShapeKeyRunner·ExpressionPanel·ToggleSet·StudioPanel.Recall)을 부른다. 이 창은 따로 상태를 갖지 않는다.
// 프로그램 창의 자식 창이 아니라(owner 없음) 프로그램을 최소화·숨겨도 남는다. 프로그램을 닫으면 같이 닫힌다 (MainWindow).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class OverlayBoard : Window
    {
        /// <summary>보드의 구역 하나. 단추 숨김 키 = "Id:단추 이름", 구역 숨김 키 = "sec:Id".</summary>
        sealed class Sec
        {
            public string Id = "", Title = "";
            public readonly UniformGrid Grid = new UniformGrid();
            public readonly StackPanel Box = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            public readonly DockPanel Head = new DockPanel { Margin = new Thickness(4, 0, 0, 4) };
            public readonly TextBlock Empty = Ui.Text("지금은 단추가 없어요", 11, FontWeight.Normal, Ui.KText3);
            public readonly List<Control> Extra = new List<Control>();   // 단추 아래 (크기 슬라이더·트래킹 상태)
        }

        static readonly string[] DefaultOrder = { "overlay", "keys", "expr", "pose", "track", "toggle", "cam" };
        const string SizeKey = "overlay:크기";

        readonly EditorContext _ctx;
        readonly Settings _settings;
        readonly OverlayController _overlay;
        readonly ExpressionPanel _expr;
        readonly ShapeKeyRunner _keys;
        readonly PosePanel _pose;
        readonly Func<int, bool> _recall;
        readonly Func<bool> _editorVisible;
        readonly Action<bool> _showEditor;
        readonly Button _pin, _hide, _editor, _custom;
        readonly Dictionary<string, Sec> _secs = new Dictionary<string, Sec>();
        readonly StackPanel _body = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        readonly Control _editBar;
        readonly StackPanel _colRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        readonly Ui.EditSlider _size;
        readonly DockPanel _sizeRow = new DockPanel { Margin = new Thickness(4, 4, 4, 8), MinHeight = 34 };
        readonly TextBlock _sizeValue = Ui.Text("", 12, FontWeight.SemiBold);
        readonly Button _sizeEye;
        readonly TextBlock _trackState = Ui.Text("", 11, FontWeight.Normal, Ui.KText3);
        Document? _doc;
        bool _editing;

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

        public OverlayBoard(EditorContext ctx, Settings settings, OverlayController overlay, ExpressionPanel expr, ShapeKeyRunner keys, PosePanel pose,
            Func<int, bool> recallCamera, Func<bool> editorVisible, Action<bool> showEditor)
        {
            _ctx = ctx; _settings = settings; _overlay = overlay; _expr = expr; _keys = keys; _pose = pose; _recall = recallCamera;
            _editorVisible = editorVisible; _showEditor = showEditor;
            Title = "컨트롤 보드";
            Width = 340; Height = 720; MinWidth = 260; MinHeight = 300;
            ShowInTaskbar = true;
            Topmost = settings.BoardPinned;
            this.Bg(Ui.KCard);
            if (settings.BoardX != int.MinValue) { WindowStartupLocation = WindowStartupLocation.Manual; Position = new PixelPoint(settings.BoardX, settings.BoardY); }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // ---- 머리: 에디터 · 꾸미기 · 방송에서 숨기기 · 핀 ----
            _editor = Ui.Button("에디터", () => { _showEditor(!_editorVisible()); RefreshHead(); }, null, Glyph.OpenPane);
            _editor.Margin = new Thickness(0, 0, 4, 0);
            _custom = Ui.IconButton(Glyph.Edit, () => SetEditing(!_editing), "");
            _pin = Ui.IconButton(Glyph.Pin, () => { _settings.BoardPinned = !_settings.BoardPinned; _settings.Save(); Topmost = _settings.BoardPinned; RefreshHead(); }, "");
            _hide = Ui.IconButton(Glyph.View, () => { _settings.BoardHideFromCapture = !_settings.BoardHideFromCapture; _settings.Save(); ApplyCapture(); RefreshHead(); }, "");
            var head = new DockPanel { Margin = new Thickness(12, 10, 8, 6) };
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            icons.Children.Add(_editor); icons.Children.Add(_custom); icons.Children.Add(_hide); icons.Children.Add(_pin);
            DockPanel.SetDock(icons, Dock.Right);
            head.Children.Add(icons);
            head.Children.Add(Ui.Text("컨트롤 보드", 15, FontWeight.SemiBold));

            // ---- 구역 ----
            AddSec("overlay", "방송 오버레이"); AddSec("keys", "블렌드쉐이프 단축키"); AddSec("expr", "표정"); AddSec("pose", "자세");
            AddSec("track", "페이셜 트래킹"); AddSec("toggle", "내 토글"); AddSec("cam", "카메라");
            _size = new Ui.EditSlider(15, 100, v => _overlay.SetSize(v / 100, false), v => _overlay.SetSize(v / 100, true));
            // 크기 줄: 이름 · 값(%) · 슬라이더. 다이얼 모드면 다이얼을 줄 오른쪽 끝에 두어 이름에 붙지 않게 (2026-10-22)
            var sizeLabel = Ui.Text("크기", 12, FontWeight.Normal, Ui.KText2); DockPanel.SetDock(sizeLabel, Dock.Left);
            sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeLabel.Margin = new Thickness(0, 0, 8, 0);
            _sizeValue.VerticalAlignment = VerticalAlignment.Center; _sizeValue.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(_sizeValue, Dock.Left);
            _sizeEye = Ui.IconButton(Glyph.View, () => ToggleHidden(SizeKey), "크기 슬라이더 숨기기·보이기"); DockPanel.SetDock(_sizeEye, Dock.Right);
            _sizeEye.VerticalAlignment = VerticalAlignment.Center;
            _size.Slider.VerticalAlignment = VerticalAlignment.Center;
            _size.Slider.ValueChanged += (_, e) => _sizeValue.Text = $"{e.NewValue:0}%";
            void Look() => _size.Slider.HorizontalAlignment = Hardware.On ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            Look();
            Hardware.Changed += Look;
            Closed += (_, __) => Hardware.Changed -= Look;
            _sizeRow.Children.Add(sizeLabel); _sizeRow.Children.Add(_sizeValue); _sizeRow.Children.Add(_sizeEye); _sizeRow.Children.Add(_size.Slider);
            _secs["overlay"].Extra.Add(_sizeRow);
            _trackState.Margin = new Thickness(4, 2, 4, 0); _trackState.TextWrapping = TextWrapping.Wrap; _trackState.MaxLines = 2;
            _secs["track"].Extra.Add(_trackState);
            _editBar = EditBar();

            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            root.Children.Add(new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 4, 0) });
            Content = root;

            Opened += (_, __) => { ApplyCapture(); if (_settings.BoardHideEditor) _showEditor(false); RefreshHead(); };
            Activated += (_, __) => { RefreshAll(); RefreshHead(); };   // 스튜디오에서 카메라 위치를 저장·지운 것도 반영
            PositionChanged += (_, __) => { if (WindowState == WindowState.Normal) { _settings.BoardX = Position.X; _settings.BoardY = Position.Y; } };
            Closing += (_, __) => _settings.Save();

            _overlay.Changed += RefreshOverlay;
            _keys.Changed += RefreshKeys;
            _expr.TrackingChanged += RefreshTracking;
            _pose.QuickChanged += RefreshPose;
            _ctx.DocumentOpened += Bind;
            Closed += (_, __) =>
            {
                _overlay.Changed -= RefreshOverlay; _keys.Changed -= RefreshKeys; _expr.TrackingChanged -= RefreshTracking; _pose.QuickChanged -= RefreshPose; _ctx.DocumentOpened -= Bind;
                if (_doc != null) _doc.Changed -= OnDocChanged;
                if (!_editorVisible()) _showEditor(true);   // 보드를 닫았는데 에디터가 숨어 있으면 다시 보인다
            };
            Layout();
            Bind();
            RefreshHead();
        }

        void AddSec(string id, string title)
        {
            var s = new Sec { Id = id, Title = title };
            s.Empty.Margin = new Thickness(4, 0, 0, 4);
            _secs[id] = s;
        }

        // ---------------- 꾸미기 ----------------
        Control EditBar()
        {
            var p = new StackPanel { Spacing = 6 };
            p.Children.Add(Ui.Wrap("단추를 누르면 숨기기·보이기가 바뀌어요. 구역은 ▲▼로 순서를 바꾸고 눈 아이콘으로 통째로 숨겨요.", Ui.KText2, 12));
            var cols = new DockPanel();
            var lab = Ui.Text("한 줄에 단추", 12, FontWeight.Normal, Ui.KText2); lab.VerticalAlignment = VerticalAlignment.Center; lab.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(lab, Dock.Left); cols.Children.Add(lab); cols.Children.Add(_colRow);
            p.Children.Add(cols);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            btns.Children.Add(Ui.Button("처음대로", ResetLayout, "구역 순서·숨김·단추 수를 처음으로 돌려요", Glyph.Reset));
            btns.Children.Add(Ui.Button("다 됐어요", () => SetEditing(false), null, accent: true));
            p.Children.Add(btns);
            var card = Ui.Card(p);
            card.Margin = new Thickness(0, 4, 0, 4);
            return card;
        }

        void SetEditing(bool on)
        {
            _editing = on;
            Layout();
            RefreshHead();
        }

        void RefreshColumns()
        {
            _colRow.Children.Clear();
            for (int n = 1; n <= 3; n++)
            {
                int c = n;
                var b = Ui.Button(n + "개", () => { _settings.BoardColumns = c; _settings.Save(); Layout(); }, null, accent: _settings.BoardColumns == n);
                b.MinWidth = 44; b.HorizontalContentAlignment = HorizontalAlignment.Center;
                _colRow.Children.Add(b);
            }
        }

        bool Hidden(string key) => _settings.BoardHidden.Contains(key);

        void ToggleHidden(string key)
        {
            if (!_settings.BoardHidden.Remove(key)) _settings.BoardHidden.Add(key);
            _settings.Save();
            if (key.StartsWith("sec:", StringComparison.Ordinal)) Layout(); else RefreshAll();
        }

        List<string> Order()
        {
            var o = _settings.BoardOrder.Where(_secs.ContainsKey).Distinct().ToList();
            foreach (var id in DefaultOrder) if (!o.Contains(id)) o.Add(id);
            return o;
        }

        void Move(string id, int dir)
        {
            var o = Order();
            int i = o.IndexOf(id), j = i + dir;
            if (i < 0 || j < 0 || j >= o.Count) return;
            (o[i], o[j]) = (o[j], o[i]);
            _settings.BoardOrder = o; _settings.Save();
            Layout();
        }

        void ResetLayout()
        {
            _settings.BoardOrder.Clear(); _settings.BoardHidden.Clear(); _settings.BoardColumns = 2; _settings.Save();
            Layout();
        }

        /// <summary>구역을 설정 순서대로 다시 놓는다 (꾸미기 모드면 맨 위에 꾸미기 줄, 구역 머리에 ▲▼·눈).</summary>
        void Layout()
        {
            _body.Children.Clear();
            if (_editing) { RefreshColumns(); _body.Children.Add(_editBar); }
            var order = Order();
            for (int i = 0; i < order.Count; i++)
            {
                var s = _secs[order[i]];
                BuildHead(s, i == 0, i == order.Count - 1);
                s.Grid.Columns = Math.Clamp(_settings.BoardColumns, 1, 3);
                s.Box.Children.Clear();
                s.Box.Children.Add(s.Head); s.Box.Children.Add(s.Grid); s.Box.Children.Add(s.Empty);
                foreach (var x in s.Extra) s.Box.Children.Add(x);
                _body.Children.Add(s.Box);
            }
            RefreshAll();
        }

        void BuildHead(Sec s, bool first, bool last)
        {
            s.Head.Children.Clear();
            var t = Ui.Text(s.Title, 12, FontWeight.SemiBold, Ui.KText2); t.VerticalAlignment = VerticalAlignment.Center;
            if (_editing)
            {
                bool hidden = Hidden("sec:" + s.Id);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                var up = Ui.IconButton(Glyph.ChevronUp, () => Move(s.Id, -1), "위로"); up.IsEnabled = !first;
                var down = Ui.IconButton(Glyph.ChevronDown, () => Move(s.Id, 1), "아래로"); down.IsEnabled = !last;
                var eye = Ui.IconButton(hidden ? Glyph.Hide : Glyph.View, () => ToggleHidden("sec:" + s.Id), hidden ? "숨긴 구역 (눌러서 보이기)" : "구역 숨기기");
                tools.Children.Add(up); tools.Children.Add(down); tools.Children.Add(eye);
                DockPanel.SetDock(tools, Dock.Right);
                s.Head.Children.Add(tools);
                if (hidden) t.Text = s.Title + "  (숨김)";
            }
            s.Head.Children.Add(t);
        }

        /// <summary>구역이 보일지: 꾸미는 중이면 늘 (숨긴 구역은 흐리게), 아니면 숨기지 않았고 단추가 있을 때.</summary>
        void RefreshSec(Sec s, bool hasExtra = false)
        {
            bool hidden = Hidden("sec:" + s.Id);
            bool any = s.Grid.Children.Count > 0 || hasExtra;
            s.Box.IsVisible = _editing || !hidden && any;
            s.Box.Opacity = _editing && hidden ? 0.45 : 1;
            s.Empty.IsVisible = _editing && !any;
        }

        /// <summary>큰 단추를 구역에 더한다. 숨긴 단추는 꾸미는 중에만 흐리게 보인다. 꾸미는 중에 누르면 숨기기·보이기.</summary>
        void Tile(Sec s, string title, string sub, Action click, bool active, string? tip = null)
        {
            var key = s.Id + ":" + title;
            bool hidden = Hidden(key);
            if (hidden && !_editing) return;
            if (_editing) { sub = hidden ? "숨김" : sub; tip = hidden ? "눌러서 보이기" : "눌러서 숨기기"; click = () => ToggleHidden(key); active = active && !hidden; }
            var content = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
            var t = Ui.Text(title, 14, FontWeight.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(t);
            if (sub.Length > 0) { var st = Ui.Text(sub, 11, FontWeight.Normal, active ? Ui.KText : Ui.KText2); st.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(st); }
            var b = new Button { Content = content, MinHeight = 56, Margin = new Thickness(3), Padding = new Thickness(6, 4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            if (active) b.Classes.Add("accent");
            if (hidden) b.Opacity = 0.4;
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, __) => click();
            s.Grid.Children.Add(b);
        }

        // ---------------- 내용 ----------------
        void Bind()
        {
            if (_doc != null) _doc.Changed -= OnDocChanged;
            _doc = _ctx.Doc;
            if (_doc != null) _doc.Changed += OnDocChanged;
            RefreshAll();
        }

        void OnDocChanged(IReadOnlyList<Addr> _) => RefreshDoc();

        void RefreshAll() { RefreshOverlay(); RefreshKeys(); RefreshTracking(); RefreshDoc(); RefreshPose(); }

        void RefreshPose()
        {
            var s = _secs["pose"];
            s.Grid.Children.Clear();
            foreach (var (name, active, apply) in _pose.QuickPoses()) Tile(s, name, "", apply, active);
            RefreshSec(s);
        }

        /// <summary>방송 화면에서 숨기기. 지원하지 않는 Windows면 툴팁에 알린다.</summary>
        bool _captureOk = true;
        void ApplyCapture()
        {
            var h = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (h == IntPtr.Zero) return;
            _captureOk = SetWindowDisplayAffinity(h, _settings.BoardHideFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }

        void RefreshHead()
        {
            _pin.Opacity = _settings.BoardPinned ? 1 : 0.45;
            ToolTip.SetTip(_pin, _settings.BoardPinned ? "맨 위에 고정됨 (눌러서 풀기)" : "맨 위에 고정하기");
            _hide.Opacity = _settings.BoardHideFromCapture ? 1 : 0.45;
            ToolTip.SetTip(_hide, !_settings.BoardHideFromCapture ? "방송 화면에도 보여요 (눌러서 숨기기)"
                : _captureOk ? "방송 화면(화면 공유·녹화)에 보이지 않아요 (눌러서 보이게)" : "이 Windows에서는 방송 화면에서 숨기지 못했어요 (Windows 10 2004 이상 필요)");
            _custom.Opacity = _editing ? 1 : 0.6;
            ToolTip.SetTip(_custom, _editing ? "꾸미기 끝내기" : "보드 꾸미기 (구역 순서·숨기기, 단추 숨기기, 한 줄 단추 수)");
            bool shown = _editorVisible();
            _editor.Content = Ui.IconLabel(shown ? Glyph.ClosePane : Glyph.OpenPane, shown ? "에디터 숨기기" : "에디터 열기");
            _editor.Classes.Set("accent", !shown);
            ToolTip.SetTip(_editor, shown ? "에디터 창을 숨기고 이 보드만 남겨요" : "숨긴 에디터 창을 다시 열어요");
        }

        void RefreshOverlay()
        {
            var s = _secs["overlay"];
            s.Grid.Children.Clear();
            bool on = _overlay.On;
            Tile(s, "오버레이", on ? "켜짐" : "꺼짐", () => _overlay.SetOn(!on), on, "게임 위에 아바타를 띄워요");
            if (_overlay.Output == "screen")   // OBS·게임 합치기 창에는 위치 조정이 없다 (2026-11-12)
                Tile(s, "위치 조정", _overlay.Adjusting ? "끌어서 옮기기" : "클릭은 게임으로", () => _overlay.SetAdjust(!_overlay.Adjusting), _overlay.Adjusting, "켜면 오버레이를 끌어 옮기고 오른쪽 끌기·휠로 돌려요");
            _size.SetQuiet(_settings.OverlaySize * 100);
            _sizeValue.Text = $"{_settings.OverlaySize * 100:0}%";
            bool sizeHidden = Hidden(SizeKey);
            _sizeRow.IsVisible = _editing || !sizeHidden;
            _sizeRow.Opacity = _editing && sizeHidden ? 0.4 : 1;
            _sizeEye.IsVisible = _editing;
            _sizeEye.Content = Ui.Icon(sizeHidden ? Glyph.Hide : Glyph.View, 12, Ui.KText2);
            RefreshSec(s, _sizeRow.IsVisible);
        }

        void RefreshKeys()
        {
            var s = _secs["keys"];
            s.Grid.Children.Clear();
            var keys = _keys.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                int k = i;
                var key = keys[i]["key"].AsString;
                var sub = (key.Length > 0 ? GlobalHotkeys.Text(key) + " · " : "") + _keys.Describe(i);
                Tile(s, keys[i]["name"].AsString, sub, () => _keys.Press(k), _keys.IsOn(i));
            }
            RefreshSec(s);
        }

        void RefreshTracking()
        {
            var s = _secs["track"];
            s.Grid.Children.Clear();
            int src = Math.Max(0, _expr.TrackingSource);
            Tile(s, "끔", "", () => _expr.TrackingSource = 0, src == 0);
            Tile(s, "캘리브레이션", "1.5초 정면", _expr.CalibrateNow, false, "힘을 빼고 편한 자리에서 카메라를 정면으로 보세요");
            Tile(s, "웹캠", "", () => _expr.TrackingSource = 1, src == 1);
            Tile(s, "외부 앱", "VMC", () => _expr.TrackingSource = 2, src == 2);
            Tile(s, "트래커 앱", "Babble · OSC", () => _expr.TrackingSource = 3, src == 3);
            Tile(s, "말하기", "마이크로 입", () => { _expr.LipOn = !_expr.LipOn; RefreshTracking(); }, _expr.LipOn);
            _trackState.Text = src == 0 ? "" : _expr.TrackingStatus;
            _trackState.IsVisible = src != 0;
            RefreshSec(s);
        }

        void RefreshDoc()
        {
            Sec es = _secs["expr"], ts = _secs["toggle"], cs = _secs["cam"];
            es.Grid.Children.Clear(); ts.Grid.Children.Clear(); cs.Grid.Children.Clear();
            var d = _doc;
            if (d != null)
            {
                var cur = d.Get(Addr.Expression);
                bool anyExpr = false;
                for (int i = 0; i < ToggleSet.MaxKeys; i++)
                {
                    var e = ToggleSet.ExpressionKey(d, i);
                    if (e.IsNull) continue;
                    int slot = i;
                    bool active = !cur.IsNull && FaceExpression.Name(cur) == FaceExpression.Name(e);
                    Tile(es, FaceExpression.Name(e), $"F{slot + 1}", () => { if (_doc != null) ToggleSet.PressExpression(_doc, slot); }, active);
                    anyExpr = true;
                }
                if (anyExpr || !cur.IsNull)
                    Tile(es, "표정 빼기", cur.IsNull ? "" : FaceExpression.Name(cur), _expr.ClearExpression, false);
                var list = d.Get(Addr.Toggles);
                for (int i = 0; i < list.Count; i++)
                {
                    int k = i;
                    Tile(ts, list[i]["name"].AsString, i < 9 ? $"Shift+{i + 1}" : "", () => { if (_doc != null) ToggleSet.Press(_doc, k); }, ToggleSet.IsOn(d, list[i]));
                }
            }
            var cams = _settings.Cameras;
            for (int i = 0; i < cams.Count; i++)
            {
                int n = i + 1;
                Tile(cs, cams[i]["name"].AsString, $"Alt+{n}", () => _recall(n), false);
            }
            RefreshSec(es); RefreshSec(ts); RefreshSec(cs);
        }
    }
}
