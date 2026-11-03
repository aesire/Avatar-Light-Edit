// 블렌드쉐이프 페이지의 "다문 입 다듬기": mouth_straight처럼 입을 다물어 선으로 만드는 블렌드쉐이프의 선을, 끝으로 갈수록 가늘고
// 살짝 웃는 곡선으로 고친다 (Protocol/MouthLine.cs의 shapeMove — 그 블렌드쉐이프의 델타만 바꿈, 원본 메시·다른 표정은 그대로).
//   블렌드쉐이프  입 선 띠를 움직이는 입 블렌드쉐이프 (지금 값이 0이 아닌 것 먼저, 기본 mouth_straight)
//   끝 두께 · 가운데 두께 (%)  선 두께 비율 (100 = 그대로)   ·   끝 올리기 (mm)  + 웃는 곡선, − 처진 곡선
//   가늘어지기 시작 (%)  가운데 0 ~ 끝 100 (낮출수록 길고 완만하게)   ·   끝 둥글기 (%)  0 = 위아래로만 눌러 뾰족하게, 100 = 둥근 끝 그대로 작게
//   다듬기는 모양만 고친다: 그 블렌드쉐이프 값이 0이면 보이지 않으므로 알리고 "100으로 켜기"를 둔다
// 값을 놓을 때마다 화면에 미리 보여 주고(기록 없음), "적용"으로 메시 편집에 기록한다 (실행 취소 한 단계). 입 맞추기보다 앞에 두어 벌린 입도 맞는다.
// "다듬기 켜기" 토글: 끄면 값은 남긴 채 적용하지 않는다 (연산의 enabled: false — 화면·올리기·내보내기 모두). 켜면 남긴 값으로 다시 (없으면 지금 값으로 적용).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        static bool _lineOpen;   // 처음엔 접힘 (2026-10-20)
        static string? _lineShape;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshData, List<string>> LineCandidates = new System.Runtime.CompilerServices.ConditionalWeakTable<MeshData, List<string>>();
        IrRenderer? _lineRenderer;
        bool _linePreviewing, _lineQuiet;
        CheckBox? _lineOnBox;
        Button? _lineOff;
        readonly TextBlock _lineState = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);

        Control? MouthLineSection()
        {
            EndLinePreview();
            _lineRenderer = null;
            if (Doc == null || _ctx.Meshes == null || _renderer == null) return null;
            var r = Doc.Original.FindRenderer(_renderer);
            var mesh = r == null ? null : _ctx.Meshes.Original(r.Mesh);
            if (r == null || mesh == null) return null;
            // 입 선 띠를 움직이는 입 블렌드쉐이프: 지금 값이 0이 아닌 것 먼저.
            // 후보 찾기(입 선을 실제로 만들어 봄)는 원본 메시에만 달려 메시마다 한 번만 한다 — 몸 메시는 다시 그릴 때마다 0.3초 걸렸다
            if (!LineCandidates.TryGetValue(mesh, out var found))
            {
                found = MeshOps.MouthLineShapes(mesh, mesh.Shapes.Select(s => s.Name).Where(n => MeshOps.IsMouthName(n) && !n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)));
                LineCandidates.Add(mesh, found);
            }
            var shapes = found.OrderByDescending(n => Math.Abs(Doc.Get(Addr.BlendShape(r.Id, n)).AsFloat) > 0.01f).ThenByDescending(n => n.ToLowerInvariant().Contains("straight")).ToList();
            if (shapes.Count == 0) return null;
            _lineRenderer = r;

            var fold = new Ui.FoldSection("다문 입 다듬기", _lineOpen, help: ("mouth_straight처럼 입을 다물어 선으로 만드는 블렌드쉐이프는 선 양 끝이 뭉툭하게 두꺼워 보일 수 있어요. 끝으로 갈수록 가늘고 살짝 웃는 곡선이 되게 그 블렌드쉐이프만 고쳐요. 원본 메시와 다른 표정은 그대로고, 올리기에도 들어가요."));
            fold.Toggled += o => _lineOpen = o;
            var body = fold.Body;

            var pick = Ui.CellCombo(new ComboBox { ItemsSource = shapes, MinWidth = 160 });
            pick.SelectedIndex = Math.Max(0, shapes.IndexOf(_lineShape != null && shapes.Contains(_lineShape) ? _lineShape : shapes[0]));
            string Shape() => pick!.SelectedItem as string ?? shapes![0];
            JNode? Cur() => Doc!.Get(Addr.MeshEdit(r.Mesh)).Items.FirstOrDefault(op => op["op"].AsString == MeshOps.ShapeMove && op["shape"].AsString == Shape());
            var cur = Cur();
            double ends = 30, center = 130, curve = 0.8, taper = 25, round = 0;
            void Load(JNode? c)
            {
                if (c == null || !c.Has("tidy")) return;
                var t = c["tidy"];
                ends = t["ends"].AsDouble * 100; center = t["center"].AsDouble * 100; curve = t["curve"].AsDouble * 1000;
                taper = t.Has("taper") ? t["taper"].AsDouble * 100 : 25; round = t.Has("round") ? t["round"].AsDouble * 100 : 0;
            }
            Load(cur);

            var rows = new StackPanel();
            var onBox = new CheckBox { Content = "다듬기 켜기", IsChecked = cur != null && !MeshOps.IsOff(cur) };
            _lineOnBox = onBox;
            ToolTip.SetTip(onBox, "끄면 원래 선으로 돌아가요. 고른 값은 남아 있어서 다시 켜면 그대로 적용돼요 (실행 취소 가능)");
            rows.Children.Add(Ui.HelpRow("켜기", "다문 입 다듬기를 켜고 꺼요 (값은 남아요)", onBox));
            rows.Children.Add(Ui.HelpRow("블렌드쉐이프", "고칠 다문 입 블렌드쉐이프 (선이 생기는 것)", pick, wide: true));
            (Control Row, Action<double> Set) Num(string label, string desc, double min, double max, Func<double> get, Action<double> set)
            {
                var box = Ui.NumberCell(v => { set(Math.Clamp(v, min, max)); Preview(); }, () => { });
                var sl = new Ui.EditSlider(min, max, v => { set(v); box.Text = Ui.F(v); }, v => { set(v); box.Text = Ui.F(v); Preview(); });
                sl.SetQuiet(get()); box.Text = Ui.F(get());
                return (Ui.HelpRow(label, desc, Ui.NumberWithSlider(box, sl.Slider), wide: true), v => { sl.SetQuiet(v); box.Text = Ui.F(v); });
            }
            var nEnds = Num("끝 두께 (%)", "선 양 끝의 두께 (100 = 그대로). 낮출수록 끝이 뾰족하게 가늘어져요", 5, 100, () => ends, v => ends = v);
            var nCenter = Num("가운데 두께 (%)", "선 가운데의 두께 (100 = 그대로)", 50, 200, () => center, v => center = v);
            var nCurve = Num("끝 올리기 (mm)", "+ 이면 양 끝이 올라가 웃는 입, − 이면 처진 입", -2, 2, () => curve, v => curve = v);
            var nTaper = Num("가늘어지기 시작 (%)", "선이 가늘어지기 시작하는 곳 (0 = 가운데부터, 100 = 끝). 낮출수록 길고 완만하게 가늘어져요", 0, 90, () => taper, v => taper = v);
            var nRound = Num("끝 둥글기 (%)", "0이면 끝을 위아래로만 눌러 뾰족한 끝, 올릴수록 끝의 둥근 모양을 살려 작고 부드러운 끝", 0, 100, () => round, v => round = v);
            rows.Children.Add(nEnds.Row); rows.Children.Add(nCenter.Row); rows.Children.Add(nCurve.Row); rows.Children.Add(nTaper.Row); rows.Children.Add(nRound.Row);
            body.Children.Add(Ui.Card(rows));

            JNode? Make() => MeshOps.MakeMouthLine(mesh, Shape(), (float)(ends / 100), (float)(center / 100), (float)(curve / 1000), (float)(taper / 100), (float)(round / 100));
            // 메시 편집 목록: 같은 블렌드쉐이프의 다듬기를 바꾸고 맨 앞에 (깜빡임·입 맞추기가 다듬은 델타를 쓰게)
            JNode With(JNode? op)
            {
                var list = JNode.Arr();
                if (op != null) list.Add(op);
                foreach (var x in Doc!.Get(Addr.MeshEdit(r.Mesh)).Items) if (!(x["op"].AsString == MeshOps.ShapeMove && x["shape"].AsString == Shape())) list.Add(x);
                return BlinkFixPlan.Ordered(list);
            }
            void Preview()
            {
                // 그 블렌드쉐이프가 0이면 보이지 않으므로 미리보기 동안 100으로
                Doc!.Preview(Addr.MeshEdit(r.Mesh), With(Make()));
                if (Math.Abs(Doc.Get(Addr.BlendShape(r.Id, Shape())).AsFloat) < 0.01f) Doc.Preview(Addr.BlendShape(r.Id, Shape()), JNode.From(100));
                _linePreviewing = true; _lineShape = Shape();
                _lineState.Text = "미리보기 중 (기록 안 됨) — 마음에 들면 \"적용\"을 눌러요"; _lineState.Fg(Ui.KCaution);
            }
            bool quiet = false;
            pick.SelectionChanged += (_, __) =>
            {
                EndLinePreview(); _lineShape = Shape();
                ends = 30; center = 130; curve = 0.8; taper = 25; round = 0; var c = Cur(); Load(c);
                nEnds.Set(ends); nCenter.Set(center); nCurve.Set(curve); nTaper.Set(taper); nRound.Set(round);
                quiet = true; onBox.IsChecked = c != null && !MeshOps.IsOff(c); quiet = false;
                RefreshLineState();
            };
            void Apply(string label)
            {
                var op = Make();
                if (op == null) { _ctx.Status("입 선을 찾지 못했어요."); return; }
                EndLinePreview();
                Doc!.Set(Addr.MeshEdit(r.Mesh), With(op), label);
                quiet = true; onBox.IsChecked = true; quiet = false;
                _ctx.Status($"{Shape()}의 다문 입 선을 다듬었습니다 (끝 {ends:0}% · 가운데 {center:0}% · 끝 올리기 {curve:0.##}mm · 가늘어지기 시작 {taper:0}% · 끝 둥글기 {round:0}%)."
                    + (Math.Abs(Doc!.Get(Addr.BlendShape(r.Id, Shape())).AsFloat) < 0.01f ? $" {Shape()} 값이 0이라 지금은 보이지 않습니다." : ""));
            }
            onBox.IsCheckedChanged += (_, __) =>
            {
                if (quiet || _lineQuiet) return;
                bool on = onBox.IsChecked == true;
                var c = Cur();
                if (c == null) { if (on) Apply("다문 입 다듬기 켜기"); return; }
                EndLinePreview();
                var flipped = c.Clone().Set("enabled", on);
                var list = JNode.Arr();
                foreach (var x in Doc!.Get(Addr.MeshEdit(r.Mesh)).Items) list.Add(x["op"].AsString == MeshOps.ShapeMove && x["shape"].AsString == Shape() ? flipped : x);
                Doc.Set(Addr.MeshEdit(r.Mesh), list, on ? "다문 입 다듬기 켜기" : "다문 입 다듬기 끄기");
                _ctx.Status(on ? $"{Shape()} 다문 입 다듬기를 켰어요." : $"{Shape()} 다문 입 다듬기를 껐어요 (값은 남아 있어요).");
            };

            var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            btns.Children.Add(Ui.Button("미리보기", Preview, "지금 값으로 화면에만 보여 줘요 (그 블렌드쉐이프가 0이면 100으로)", Glyph.View));
            btns.Children.Add(Ui.Button("적용", () => Apply("다문 입 다듬기"), "고른 값으로 그 블렌드쉐이프를 고쳐요 (켜기도 켜져요, 실행 취소 가능)", Glyph.Pin));
            btns.Children.Add(Ui.Button("되돌리기", () =>
            {
                EndLinePreview();
                if (Cur() == null) return;
                Doc!.Set(Addr.MeshEdit(r.Mesh), With(null), "다문 입 다듬기 되돌리기");
                quiet = true; onBox.IsChecked = false; quiet = false;
                _ctx.Status($"{Shape()}을 다듬기 전으로 되돌렸어요 (실행 취소로 다시 적용).");
            }, "다듬은 블렌드쉐이프를 원래대로 되돌려요", Glyph.Reset));
            body.Children.Add(btns);
            // 다듬기는 모양만 고친다 — 값이 0이면 화면·올리기에서 보이지 않고, 입 맞추기도 뺄 편집이 없다
            _lineOff = Ui.Button($"{Shape()} 100으로 켜기", () =>
            {
                EndLinePreview();
                Doc!.Set(Addr.BlendShape(r.Id, Shape()), JNode.From(100), $"{Shape()} 켜기");
                _ctx.Status($"{Shape()}을 100으로 켰어요. 입 맞추기를 쓰면 \"다시 맞추기\"를 눌러요.");
            }, "다듬은 다문 입이 보이게 그 블렌드쉐이프 값을 100으로 (실행 취소 가능)", Glyph.View);
            _lineOff.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(_lineOff);
            _lineState.Margin = new Thickness(0, 6, 0, 0); _lineState.TextWrapping = TextWrapping.Wrap;
            if (_lineState.Parent is Panel old) old.Children.Remove(_lineState);
            body.Children.Add(_lineState);
            _lineShape = Shape();
            RefreshLineState();
            return fold;
        }

        void EndLinePreview()
        {
            if (!_linePreviewing || Doc == null || _lineRenderer == null) { _linePreviewing = false; return; }
            var a = Addr.MeshEdit(_lineRenderer.Mesh); Doc.Preview(a, Doc.Get(a));
            if (_lineShape != null) { var b = Addr.BlendShape(_lineRenderer.Id, _lineShape); Doc.Preview(b, Doc.Get(b)); }
            _linePreviewing = false;
        }

        void RefreshLineState()
        {
            if (Doc == null || _lineRenderer == null || _linePreviewing) return;
            var cur = Doc.Get(Addr.MeshEdit(_lineRenderer.Mesh)).Items.FirstOrDefault(op => op["op"].AsString == MeshOps.ShapeMove && op["shape"].AsString == _lineShape);
            // 실행 취소 등으로 바뀌어도 토글이 기록과 맞게
            if (_lineOnBox != null) { _lineQuiet = true; _lineOnBox.IsChecked = cur != null && !MeshOps.IsOff(cur); _lineQuiet = false; }
            bool zero = _lineShape != null && Math.Abs(Doc.Get(Addr.BlendShape(_lineRenderer.Id, _lineShape)).AsFloat) < 0.01f;
            if (_lineOff != null) _lineOff.IsVisible = cur != null && !MeshOps.IsOff(cur) && zero;
            if (cur == null) { _lineState.Text = "꺼짐 (원래 선)"; _lineState.Fg(Ui.KText2); return; }
            var t = cur["tidy"];
            string More() => (t.Has("taper") ? $" · 가늘어지기 시작 {t["taper"].AsDouble * 100:0}%" : "") + (t.Has("round") ? $" · 끝 둥글기 {t["round"].AsDouble * 100:0}%" : "");
            if (MeshOps.IsOff(cur)) { _lineState.Text = $"꺼짐 · 값은 남아 있어요 (끝 {t["ends"].AsDouble * 100:0}% · 가운데 {t["center"].AsDouble * 100:0}% · 끝 올리기 {t["curve"].AsDouble * 1000:0.##}mm{More()})"; _lineState.Fg(Ui.KText2); return; }
            _lineState.Text = $"켜짐 · {_lineShape} · 끝 {t["ends"].AsDouble * 100:0}% · 가운데 {t["center"].AsDouble * 100:0}% · 끝 올리기 {t["curve"].AsDouble * 1000:0.##}mm{More()}"
                + (zero ? $"\n{_lineShape} 값이 0이라 다듬은 입이 보이지 않습니다. 다듬기는 모양만 고치고 켜 주지는 않습니다 — 아래 버튼이나 BlendShape 목록에서 값을 올립니다." : "");
            _lineState.Fg(zero ? Ui.KCaution : Ui.KText2);
        }
    }
}
