// 블렌드쉐이프 단축키 만들기·고치기 창 (Core.ShapeKeys). 이름 · 전역 단축키(눌러서 정하기) · 켜고 끄기/단계 · 묶음 · 움직일 블렌드쉐이프(메시·이름·켰을 때·껐을 때).
// 아바타 기능 메뉴에서 가져온 것도 여기서 고친다. 결과는 ShapeKeys.Make 값.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class ShapeKeyWindow : Window
    {
        readonly IrScene _scene;
        readonly TextBox _name = new TextBox { Watermark = "예: 홍조, 하트 눈" };
        readonly Button _keyBtn;
        readonly TextBlock _keyNote = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly ComboBox _mode = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "켜고 끄기", "단계 바꾸기 (누를 때마다 다음 세기)" } });
        readonly TextBox _steps = new TextBox { Watermark = "0, 50, 100" };
        readonly TextBox _group = new TextBox { Watermark = "비우면 묶음 없음 (예: 눈동자)" };
        readonly StackPanel _rows = new StackPanel { Spacing = 6 };
        readonly TextBlock _msg = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly Grid _stepsRow;
        readonly List<(string Id, string Name, List<string> Shapes)> _renderers = new List<(string, string, List<string>)>();
        readonly List<Row> _shapeRows = new List<Row>();
        readonly string _source;
        string _key = "";
        bool _capturing;

        sealed class Row { public ComboBox Ren = null!, Shape = null!; public TextBox On = null!, Off = null!; public Control Box = null!; }

        /// <param name="current">고칠 정의 (새로 만들면 null)</param>
        public ShapeKeyWindow(IrScene scene, JNode? current)
        {
            _scene = scene;
            InputBar.SetChoices(_steps, "0, 100", "0, 50, 100", "0, 25, 50, 75, 100", "0, 33, 66, 100");   // 입력 바(길게 누르기)의 자주 쓰는 단계
            Title = current == null ? "블렌드쉐이프 단축키 만들기" : "블렌드쉐이프 단축키 고치기";
            Width = 640; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);
            _source = current?["source"].AsStringOrNull ?? "manual";

            foreach (var r in scene.Renderers)
            {
                var m = scene.FindMesh(r.Mesh);
                if (m == null || m.BlendShapes.Count == 0) continue;
                var name = r.Node >= 0 && r.Node < scene.Nodes.Count ? scene.Nodes[r.Node].Name : r.Id;
                _renderers.Add((r.Id, name, m.BlendShapes.Select(b => b.Name).ToList()));
            }

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.DialogHeader(Title, "단축키로 블렌드쉐이프 값을 바꿔요 (방송 중 홍조·얼굴 이펙트·눈동자 모양 등).",
                "단축키는 게임 창에 포커스가 있어도 눌려요. 다른 키와 겹치지 않게 Ctrl·Alt 같은 조합이나 숫자 패드·F13~F24를 권해요. 누른 결과는 미리보기·방송 전용이라 실행 취소나 올리기에 들어가지 않아요."));
            p.Children.Add(Ui.PropRow(Ui.Text("이름"), _name, null));
            _keyBtn = Ui.Button("눌러서 정하기", StartCapture, "누른 다음 쓸 키 조합을 누르세요 (Esc = 취소, Backspace = 지우기)", Glyph.Keyboard);
            _keyBtn.Margin = new Thickness(0);
            var keyBox = new StackPanel { Spacing = 2 };
            keyBox.Children.Add(_keyBtn); keyBox.Children.Add(_keyNote);
            _keyNote.TextWrapping = TextWrapping.Wrap;
            p.Children.Add(Ui.PropRow(Ui.Text("단축키"), keyBox, null));
            p.Children.Add(Ui.PropRow(Ui.Text("누르면"), _mode, null));
            _stepsRow = Ui.PropRow(Ui.Text("단계 (%)"), _steps, null);
            ToolTip.SetTip(_steps, "껐을 때(0)에서 켰을 때(100)까지 몇 %로 갈지, 누를 때마다 차례로. 쉼표로 나눠요");
            p.Children.Add(_stepsRow);
            p.Children.Add(Ui.PropRow(Ui.Text("묶음"), _group, null));
            ToolTip.SetTip(_group, "같은 묶음 이름의 단축키는 하나만 켜져요 (눈동자 모양 여러 개처럼 같이 켜면 안 되는 것)");

            var shapeHead = new TitleRow { Margin = new Thickness(0, 8, 0, 0) };
            var add = Ui.Button("블렌드쉐이프 더하기", () => AddRow(null, null, 100, 0), null, Glyph.Add);
            add.Margin = new Thickness(0);
            DockPanel.SetDock(add, Dock.Right);
            shapeHead.Children.Add(add);
            shapeHead.Children.Add(Ui.SectionTitle("움직일 블렌드쉐이프", "켰을 때·껐을 때 값(0~100). 끄면 단축키가 덮던 값이 빠지고 원래 값으로 돌아가요 (껐을 때 값은 단계 바꾸기에서 0%의 값)."));
            p.Children.Add(shapeHead);
            var head = Ui.Columns(Ui.Star(1.3), Ui.Star(1.6), Ui.Px(70), Ui.Px(70), Ui.Px(32));
            var hs = new[] { "메시", "블렌드쉐이프", "켰을 때", "껐을 때" };
            for (int i = 0; i < hs.Length; i++) head.Children.Add(Ui.Text(hs[i], 12, FontWeight.Normal, Ui.KText2).At(i));
            p.Children.Add(head);
            p.Children.Add(new ScrollViewer { MaxHeight = 260, Content = _rows });
            p.Children.Add(_msg);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var ok = Ui.Button(current == null ? "만들기" : "고치기", Done, accent: true);
            var cancel = Ui.Button("취소", () => Close(null));
            foreach (var b in new[] { ok, cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            ok.IsDefault = true; cancel.IsCancel = true;
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            p.Children.Add(buttons);
            Content = p;

            // 값 채우기
            _name.Text = current?["name"].AsString ?? "";
            _key = current?["key"].AsString ?? "";
            _mode.SelectedIndex = current?["mode"].AsString == "cycle" ? 1 : 0;
            _steps.Text = string.Join(", ", (current == null ? ShapeKeys.DefaultSteps : ShapeKeys.Steps(current)).Select(x => (x * 100).ToString("0", CultureInfo.InvariantCulture)));
            _group.Text = current?["group"].AsString ?? "";
            if (current != null) foreach (var (r, s, on, off) in ShapeKeys.Shapes(current)) AddRow(r, s, on, off);
            if (_shapeRows.Count == 0) AddRow(null, null, 100, 0);
            _mode.SelectionChanged += (_, __) => _stepsRow.IsVisible = _mode.SelectedIndex == 1;
            _stepsRow.IsVisible = _mode.SelectedIndex == 1;
            ShowKey();

            AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            Opened += (_, __) => _name.Focus();
        }

        void AddRow(string? renderer, string? shape, float on, float off)
        {
            var row = new Row
            {
                Ren = Ui.CellCombo(new ComboBox { ItemsSource = _renderers.Select(r => r.Name).ToList() }),
                Shape = Ui.CellCombo(new ComboBox()),
                On = new TextBox { Text = on.ToString("0.##", CultureInfo.InvariantCulture) },
                Off = new TextBox { Text = off.ToString("0.##", CultureInfo.InvariantCulture) },
            };
            row.Ren.SelectionChanged += (_, __) =>
            {
                var i = row.Ren.SelectedIndex;
                row.Shape.ItemsSource = i >= 0 ? _renderers[i].Shapes : new List<string>();
            };
            int ri = renderer == null ? Math.Max(0, _renderers.FindIndex(r => r.Shapes.Count > 50)) : _renderers.FindIndex(r => r.Id == renderer);
            if (ri < 0 && renderer != null) ri = 0;
            if (_renderers.Count > 0) row.Ren.SelectedIndex = Math.Max(0, ri);
            if (shape != null && row.Shape.ItemsSource is List<string> names) row.Shape.SelectedIndex = names.IndexOf(shape);
            var del = Ui.IconButton(Glyph.Delete, () => { _shapeRows.Remove(row); _rows.Children.Remove(row.Box); }, "이 줄 빼기");
            var g = Ui.Columns(Ui.Star(1.3), Ui.Star(1.6), Ui.Px(70), Ui.Px(70), Ui.Px(32));
            g.ColumnSpacing = 6;
            g.Children.Add(row.Ren.At(0)); g.Children.Add(row.Shape.At(1)); g.Children.Add(row.On.At(2)); g.Children.Add(row.Off.At(3)); g.Children.Add(del.At(4));
            row.Box = g;
            _shapeRows.Add(row);
            _rows.Children.Add(g);
        }

        // ---------------- 단축키 정하기 ----------------
        void StartCapture()
        {
            _capturing = true;
            _keyBtn.Content = Ui.IconLabel(Glyph.Keyboard, "쓸 키를 누르세요…");
            _keyBtn.Focus();
        }

        void OnKey(object? sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            e.Handled = true;
            if (e.Key == Key.Escape) { _capturing = false; ShowKey(); return; }
            if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None) { _key = ""; _capturing = false; ShowKey(); return; }
            var k = GlobalHotkeys.FromKeyEvent(e.Key, e.KeyModifiers);
            if (k == null) return;   // 조합 키만 누른 것: 다음 키를 기다린다
            _key = k; _capturing = false;
            ShowKey();
        }

        void ShowKey()
        {
            _keyBtn.Content = Ui.IconLabel(Glyph.Keyboard, _key.Length == 0 ? "눌러서 정하기" : GlobalHotkeys.Text(_key));
            _keyNote.Text = _key.Length == 0 ? "없으면 컨트롤 보드·스튜디오 페이지의 단추로만 눌러요."
                : GlobalHotkeys.RisksGameInput(_key) ? "조합 키 없이 글자 하나면 게임에서 그 키를 못 써요. Ctrl·Alt를 같이 누르는 것을 권해요."
                : "게임 중에도 눌려요.";
            _keyNote.Fg(_key.Length > 0 && GlobalHotkeys.RisksGameInput(_key) ? Ui.KCaution : Ui.KText2);
        }

        void Done()
        {
            var shapes = new List<(string, string, float, float)>();
            foreach (var r in _shapeRows)
            {
                if (r.Ren.SelectedIndex < 0 || r.Shape.SelectedItem is not string s) continue;
                if (!Ui.TryParse(r.On.Text ?? "", out var on) || !Ui.TryParse(r.Off.Text ?? "", out var off)) { Fail("켰을 때·껐을 때 값은 숫자(0~100)로 적어 주세요"); return; }
                shapes.Add((_renderers[r.Ren.SelectedIndex].Id, s, (float)Math.Clamp(on, 0, 100), (float)Math.Clamp(off, 0, 100)));
            }
            if (shapes.Count == 0) { Fail("움직일 블렌드쉐이프를 하나 이상 골라 주세요"); return; }
            float[]? steps = null;
            if (_mode.SelectedIndex == 1)
            {
                var parts = (_steps.Text ?? "").Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var list = new List<float>();
                foreach (var x in parts) if (Ui.TryParse(x.TrimEnd('%'), out var v)) list.Add((float)Math.Clamp(v / 100, 0, 1));
                if (list.Count < 2) { Fail("단계는 두 개 이상 적어 주세요 (예: 0, 50, 100)"); return; }
                steps = list.ToArray();
            }
            var name = (_name.Text ?? "").Trim();
            if (name.Length == 0) name = shapes[0].Item2;
            Close(ShapeKeys.Make(name, _key, _mode.SelectedIndex == 1 ? "cycle" : "toggle", (_group.Text ?? "").Trim(), shapes, steps, _source));
        }

        void Fail(string s) { _msg.Text = s; _msg.Fg(Ui.KCaution); }
    }
}
