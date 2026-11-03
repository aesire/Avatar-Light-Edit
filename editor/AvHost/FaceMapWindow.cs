// 페이셜 트래킹 연결 직접 고치기 창: 트래킹 값(ARKit 52개·두 눈 감기·입 모양)마다 어느 블렌드쉐이프를 얼마나 움직일지.
//   자동 연결(렌더러가 이름으로 찾은 것)은 "자동"으로 보이고, 지우면 꺼지고(off) 세기를 바꿀 수 있다. +로 블렌드쉐이프를 더한다.
//   바꿀 때마다 Addr.FaceMap에 기록한다 (실행 취소·세션 포함, 미리보기 전용). 렌더러가 바로 다시 연결한다.
//   시험: 값 하나만 100%로 두고 연결된 블렌드쉐이프를 미리보기에 보여 준다 (트래킹이 꺼져 있어도). 창을 닫으면 끝난다.
//   트래킹이 켜져 있으면 값마다 지금 값 막대가 움직인다.
using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class FaceMapWindow : Window
    {
        readonly EditorContext _ctx;
        readonly Document _doc;
        readonly TextBox _search = new TextBox { Watermark = "값이나 블렌드쉐이프 이름으로 찾기" };
        readonly CheckBox _onlyLinked = new CheckBox { Content = "연결된 것만", IsChecked = false };
        readonly StackPanel _list = new StackPanel { Spacing = 2 };
        readonly TextBlock _state = Ui.Text("자동 연결을 불러오는 중…", 12, FontWeight.Normal, Ui.KText2);
        readonly Dictionary<string, ProgressBar> _bars = new Dictionary<string, ProgressBar>();
        readonly List<(string Id, string Name, List<string> Shapes)> _renderers = new List<(string, string, List<string>)>();
        List<(string Src, string R, string S)> _auto = new List<(string, string, string)>();
        bool _autoLoaded;
        string? _testing;
        readonly Dictionary<string, Button> _testButtons = new Dictionary<string, Button>();

        public FaceMapWindow(EditorContext ctx, Document doc)
        {
            _ctx = ctx; _doc = doc;
            Title = "트래킹 연결 직접 고치기";
            Width = 860; Height = 720; MinWidth = 640; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            foreach (var r in doc.Original.Renderers)
            {
                var m = doc.Original.FindMesh(r.Mesh);
                if (m == null || m.BlendShapes.Count == 0) continue;
                var name = r.Node >= 0 && r.Node < doc.Original.Nodes.Count ? doc.Original.Nodes[r.Node].Name : r.Id;
                _renderers.Add((r.Id, name, m.BlendShapes.Select(b => b.Name).ToList()));
            }

            var top = new StackPanel { Spacing = 8, Margin = new Thickness(24, 20, 24, 8) };
            top.Children.Add(Ui.DialogHeader(Title, "트래킹 값마다 움직일 블렌드쉐이프를 정해요.",
                "\"자동\"은 이름으로 찾은 연결입니다. 연결을 눌러 세기를 변경하거나 삭제하고, +로 추가합니다. "
                + "\"시험\"을 누르면 그 값만 100%일 때의 얼굴을 미리보기에 표시합니다. 변경한 항목은 바로 반영되고 실행 취소할 수 있습니다 (미리보기 전용, 업로드에는 들어가지 않습니다)."));
            var filter = new DockPanel();
            DockPanel.SetDock(_onlyLinked, Dock.Right); _onlyLinked.Margin = new Thickness(12, 0, 0, 0);
            filter.Children.Add(_onlyLinked); filter.Children.Add(_search);
            top.Children.Add(filter);
            top.Children.Add(_state);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 16), Spacing = 8 };
            var reset = Ui.Button("전부 자동으로 되돌리기", () =>
            {
                if (_doc.Get(Addr.FaceMap).Count == 0) return;
                _doc.Reset(Addr.FaceMap, "트래킹 연결: 자동으로");
                _ctx.Status("트래킹 연결을 자동 연결로 되돌렸어요 (실행 취소 가능).");
            }, "수동으로 더하거나 끈 연결을 모두 지워요", Glyph.Reset);
            var close = Ui.Button("닫기", Close, accent: true);
            foreach (var b in new[] { reset, close }) b.Margin = new Thickness(0);
            close.IsCancel = true;
            buttons.Children.Add(reset); buttons.Children.Add(close);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = _list, Margin = new Thickness(24, 0, 12, 0), Padding = new Thickness(0, 0, 12, 0) });
            Content = root;

            _search.TextChanged += (_, __) => Rebuild();
            _onlyLinked.IsCheckedChanged += (_, __) => Rebuild();
            _ctx.RendererMessage += OnRenderer;
            _doc.Changed += OnDocChanged;
            Closed += (_, __) =>
            {
                _ctx.RendererMessage -= OnRenderer; _doc.Changed -= OnDocChanged;
                if (_testing != null) _ctx.Render?.Send(Msg.Make(Msg.FaceTracking).Set("test", ""));
            };
            Rebuild();
            RequestAuto();
        }

        void RequestAuto()
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _state.Text = "미리보기가 켜지면 자동 연결을 볼 수 있어요. 수동 연결은 지금도 고칠 수 있어요."; return; }
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("links", true));
        }

        void OnDocChanged(IReadOnlyList<Addr> a) { if (a.Contains(Addr.FaceMap)) Rebuild(); }

        void OnRenderer(JNode m)
        {
            if (Msg.Type(m) == Msg.Ready) { _autoLoaded = false; _testing = null; RequestAuto(); return; }
            if (Msg.Type(m) != Msg.TrackingStatus) return;
            if (m["auto"].Kind == JKind.Array)
            {
                _auto = m["auto"].Items.Select(x => (x["src"].AsString, x["r"].AsString, x["s"].AsString)).ToList();
                _autoLoaded = true;
                Rebuild();
                return;
            }
            // 지금 값 막대 (좌우 바꾸기를 켜면 값 이름이 이미 바뀐 상태로 온다)
            if (m["values"].Kind == JKind.Object)
                foreach (var kv in _bars) kv.Value.Value = m["values"].Has(kv.Key) ? m["values"][kv.Key].AsDouble : 0;
        }

        string RendererName(string id) => _renderers.FirstOrDefault(r => r.Id == id).Name ?? id;

        void Rebuild()
        {
            _list.Children.Clear(); _bars.Clear(); _testButtons.Clear();
            var map = _doc.Get(Addr.FaceMap);
            var links = FaceMap.Effective(_auto, map);
            int manual = map["add"].Count, off = map["off"].Count;
            if (_autoLoaded) _state.Text = $"연결 {links.Count}개 (자동 {links.Count(l => l.Auto)} · 수동 {links.Count(l => !l.Auto)})" + (off > 0 ? $" · 끈 자동 연결 {off}개" : "") + (manual + off == 0 ? " · 아직 변경한 항목 없음" : "");
            var q = (_search.Text ?? "").Trim().ToLowerInvariant();
            // 목록에 없는 이름(both:… 등 자동 연결이 쓰는 합친 값)도 보인다
            var sources = FaceMap.Sources.Select(x => x.Name).ToList();
            foreach (var l in links) if (!sources.Contains(l.Src)) sources.Add(l.Src);
            foreach (var src in sources)
            {
                var mine = links.Where(l => l.Src == src).ToList();
                if (_onlyLinked.IsChecked == true && mine.Count == 0) continue;
                if (q.Length > 0 && !(src.ToLowerInvariant().Contains(q) || FaceMap.Label(src).Contains(q) || mine.Any(l => l.S.ToLowerInvariant().Contains(q)))) continue;
                _list.Children.Add(Row(src, mine));
            }
            if (_list.Children.Count == 0) _list.Children.Add(Ui.Wrap("맞는 값이 없어요."));
        }

        Control Row(string src, List<FaceMap.Link> mine)
        {
            var g = Ui.Columns(new GridLength(210), new GridLength(70), Ui.Star(), GridLength.Auto, GridLength.Auto);
            g.MinHeight = 40; g.Margin = new Thickness(0, 2);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Ui.Text(FaceMap.Label(src), 14));
            names.Children.Add(Ui.Text(src, 11, FontWeight.Normal, Ui.KText2));
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinWidth = 50, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            ToolTip.SetTip(bar, "트래킹이 켜져 있을 때 이 값의 지금 크기");
            _bars[src] = bar;
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var l in mine) chips.Children.Add(Chip(l));
            if (mine.Count == 0) chips.Children.Add(Ui.Text("연결 없음", 12, FontWeight.Normal, Ui.KText2));
            var add = Ui.IconButton(Glyph.Add, () => { }, "블렌드쉐이프 더하기");
            add.Flyout = AddMenu(src, mine);
            var test = Ui.Button(_testing == src ? "시험 끝" : "시험", () => ToggleTest(src), "이 값만 100%일 때의 얼굴을 미리보기에 보여 줘요 (다시 누르면 끝)");
            test.Margin = new Thickness(6, 0, 0, 0);
            _testButtons[src] = test;
            g.Children.Add(names.At(0)); g.Children.Add(bar.At(1)); g.Children.Add(chips.At(2)); g.Children.Add(add.At(3)); g.Children.Add(test.At(4));
            return g;
        }

        Control Chip(FaceMap.Link l)
        {
            var text = l.S + (Math.Abs(l.K - 1) > 1e-3 ? $" ×{l.K:0.##}" : "") + (l.Auto ? "  자동" : "");
            var b = new Button { Content = Ui.Text(text, 12), Padding = new Thickness(8, 3), Margin = new Thickness(0, 2, 6, 2) };
            ToolTip.SetTip(b, $"{RendererName(l.R)} / {l.S}" + (l.Auto ? "\n이름으로 찾은 자동 연결" : "\n직접 추가한 연결") + "\n눌러서 세기를 변경하거나 삭제합니다");
            if (!l.Auto) b.Classes.Add("accent");
            // 세기·지우기
            var panel = new StackPanel { Spacing = 8, Width = 260 };
            panel.Children.Add(Ui.Text($"{RendererName(l.R)} / {l.S}", 13, FontWeight.SemiBold));
            var k = l.K;
            var sl = new Ui.EditSlider(0, 2, v => k = (float)v, v => { k = (float)v; Set(FaceMap.Add(_doc.Get(Addr.FaceMap), l.Src, l.R, l.S, k), $"트래킹 연결: {l.S} 세기"); });
            sl.SetQuiet(l.K);
            panel.Children.Add(Ui.PropRow(Ui.Text("세기"), sl.Slider, null));
            panel.Children.Add(Ui.Wrap("1 = 값 그대로 (값 100% → 블렌드쉐이프 100). 2면 값 50%에서 100이 돼요."));
            panel.Children.Add(Ui.Button("이 연결 지우기", () => Set(FaceMap.Remove(_doc.Get(Addr.FaceMap), l.Src, l.R, l.S, l.Auto), $"트래킹 연결: {l.S} 지우기"), null, Glyph.Delete));
            b.Flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
            return b;
        }

        /// <summary>+ 메뉴: 메시 고르기 · 블렌드쉐이프 찾기 · 더하기.</summary>
        Flyout AddMenu(string src, List<FaceMap.Link> mine)
        {
            var panel = new StackPanel { Spacing = 8, Width = 340 };
            var fly = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
            if (_renderers.Count == 0) { panel.Children.Add(Ui.Wrap("블렌드쉐이프가 있는 메시가 없어요.")); return fly; }
            // 기본 메시: 이 값이 이미 이어진 메시 → 연결이 가장 많은 메시 → 블렌드쉐이프가 가장 많은 메시
            var all = FaceMap.Effective(_auto, _doc.Get(Addr.FaceMap));
            string Default() => mine.FirstOrDefault()?.R
                ?? all.GroupBy(l => l.R).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key
                ?? _renderers.OrderByDescending(r => r.Shapes.Count).First().Id;
            var mesh = Ui.CellCombo(new ComboBox { ItemsSource = _renderers.Select(r => $"{r.Name}  ({r.Shapes.Count})").ToList() });
            var find = new TextBox { Watermark = "블렌드쉐이프 찾기" };
            var box = new ListBox { Height = 260 };
            panel.Children.Add(Ui.Text($"{FaceMap.Label(src)}에 블렌드쉐이프 더하기", 13, FontWeight.SemiBold));
            panel.Children.Add(mesh); panel.Children.Add(find); panel.Children.Add(box);
            var addBtn = Ui.Button("더하기", () => { }, null, Glyph.Add, accent: true);
            panel.Children.Add(addBtn);
            void Fill()
            {
                if (mesh.SelectedIndex < 0) return;
                var q = (find.Text ?? "").Trim().ToLowerInvariant();
                var used = mine.Where(l => l.R == _renderers[mesh.SelectedIndex].Id).Select(l => l.S).ToHashSet();
                box.ItemsSource = _renderers[mesh.SelectedIndex].Shapes.Where(s => !used.Contains(s) && (q.Length == 0 || s.ToLowerInvariant().Contains(q))).ToList();
            }
            void Add()
            {
                if (mesh.SelectedIndex < 0 || box.SelectedItem is not string shape) { _ctx.Status("더할 블렌드쉐이프를 골라요."); return; }
                var r = _renderers[mesh.SelectedIndex].Id;
                fly.Hide();
                Set(FaceMap.Add(_doc.Get(Addr.FaceMap), src, r, shape), $"트래킹 연결: {FaceMap.Label(src)} → {shape}");
                _ctx.Status($"{FaceMap.Label(src)} → {shape} 연결을 더했어요. \"시험\"으로 확인해요.");
            }
            mesh.SelectionChanged += (_, __) => Fill();
            find.TextChanged += (_, __) => Fill();
            box.DoubleTapped += (_, __) => Add();
            addBtn.Click += (_, __) => Add();
            fly.Opened += (_, __) =>
            {
                var d = Default();
                mesh.SelectedIndex = Math.Max(0, _renderers.FindIndex(r => r.Id == d));
                find.Text = ""; Fill(); find.Focus();
            };
            return fly;
        }

        void Set(JNode map, string label)
        {
            _doc.Set(Addr.FaceMap, map, label);
        }

        void ToggleTest(string src)
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 시험할 수 있어요."); return; }
            _testing = _testing == src ? null : src;
            _ctx.Render.Send(Msg.Make(Msg.FaceTracking).Set("test", _testing ?? ""));
            foreach (var kv in _testButtons) kv.Value.Content = kv.Key == _testing ? "시험 끝" : "시험";
            if (_testing != null) _ctx.Status($"{FaceMap.Label(src)} 100%일 때의 얼굴이에요. 다시 누르면 끝나요.");
        }
    }
}
