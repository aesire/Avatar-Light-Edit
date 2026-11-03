// 환경 페이지: 렌더러가 알려 준 환경 Provider 스키마로 조명·분위기 설정 화면을 만든다.
// 스키마 항목의 "group"이 같은 줄은 접는 구역 하나로 묶는다 (추가 조명·라이트 볼륨의 조명 1·2·3). 첫 줄이 켜기 스위치면 접힌 제목에 켜짐·꺼짐.
// "file" 항목은 경로 칸 + 찾기 단추 (AudioLink 오디오 파일).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class EnvironmentPanel : DockPanel
    {
        readonly EditorContext _ctx;
        // 페이지 안 탭 (2026-10-21): 효과(조명·후처리 …)마다 탭 하나. 맨 위 줄에 켜고 끄기
        readonly SubTabs _tabs = new SubTabs("environment", "환경");
        bool _suppress;
        string _schemaKey = "";   // 마지막으로 그릴 때의 환경 스키마

        public EnvironmentPanel(EditorContext ctx)
        {
            _ctx = ctx;
            var reset = Ui.IconButton(Glyph.Reset, () => Commit(Document.DefaultEnvironment(), "환경 되돌리기", false), "기본 환경으로 되돌리기: 모든 효과의 값을 처음대로");
            _tabs.AddAction(reset);
            Children.Add(_tabs);
            _ctx.DocumentOpened += () => { if (_ctx.Doc != null) _ctx.Doc.Changed += a => { if (a.Contains(Addr.Environment)) Later.Run(this, Rebuild); }; Rebuild(); };
            // 렌더러 ready(스키마가 들어옴)·sceneLoaded(범위만) 둘 다 온다 — 스키마가 바뀔 때만 (장면이 보인 순간 0.1~0.25초 멈추던 것)
            _ctx.RendererInfoChanged += () => { var k = _ctx.EnvironmentSchema.ToJson(); if (k == _schemaKey) return; Later.Run(this, Rebuild); };
            Rebuild();
        }

        // 쉬운 모드에서 숨기는 세부 값 (주광 높이·색, 그림자 진하기, 환경광 색, 반사, 블룸 세부, 톤 매핑, 틴트)
        static readonly HashSet<string> ExpertParams = new HashSet<string> { "pitch", "sunColor", "shadowStrength", "ambientColor", "reflection", "bloomThreshold", "bloomScatter", "bloomTint", "tonemap", "tint", "theme0", "theme1" };
        // 조명 슬롯(l1_ …)에서 쉬운 모드에 숨기는 값: 겨누는 높이 · 빛 크기 · 가장자리 부드럽기
        static readonly string[] ExpertSuffix = { "_aim", "_size", "_falloff" };
        static bool IsExpert(string name) => ExpertParams.Contains(name) || ExpertSuffix.Any(name.EndsWith);
        // 접는 구역(group)의 열림 상태: "provider/group" → 열림 (다시 그려도 유지)
        static readonly Dictionary<string, bool> GroupOpen = new Dictionary<string, bool>();

        JNode Current() => _ctx.Doc?.Get(Addr.Environment) ?? Document.DefaultEnvironment();

        void Commit(JNode list, string label, bool preview)
        {
            if (_ctx.Doc == null) { _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", list)); return; }
            if (preview) _ctx.Doc.Preview(Addr.Environment, list);
            else _ctx.Doc.Set(Addr.Environment, list, label);
        }

        static JNode? Entry(JNode list, string id) => list.Items.FirstOrDefault(e => e["provider"].AsString == id);
        /// 항목이 있고 enabled가 false가 아니면 켜짐 (enabled가 없는 예전 세션 항목은 켜짐)
        static bool IsOn(JNode? e) => e != null && (!e.Has("enabled") || e["enabled"].AsBool);

        void Rebuild()
        {
            if (_suppress) return;
            _schemaKey = _ctx.EnvironmentSchema.ToJson();
            var cur = Current();
            _tabs.BeginUpdate();
            if (_ctx.EnvironmentSchema.Count == 0) _tabs.Add("none", "환경", Ui.Wrap("미리보기가 켜지면 조명·후처리 설정이 나와요."));
            foreach (var prov in _ctx.EnvironmentSchema.Items)
            {
                var id = prov["id"].AsString;
                var entry = Entry(cur, id);
                bool active = IsOn(entry);
                var title = prov["label"].AsString.Length > 0 ? prov["label"].AsString : id;
                var sw = new Ui.Switch(on =>
                {
                    // 끌 때 항목을 지우지 않고 enabled만 바꾼다 → 다시 켜면 바꿔 둔 값이 그대로 돌아온다
                    var list = Current().Clone();
                    var e = Entry(list, id);
                    if (e == null) list.Add(JNode.Obj().Set("provider", id).Set("version", prov["version"].AsInt).Set("params", JNode.Obj()));
                    else if (on) e.Remove("enabled");
                    else e.Set("enabled", false);
                    Commit(list, (on ? "켜기: " : "끄기: ") + id, false);
                });
                sw.SetQuiet(active);
                var tab = new StackPanel();
                tab.Children.Add(Ui.Card(Ui.DescRow(title + " 사용", active ? "켜져 있어요" : "꺼져 있어요 · 켜면 아래 값이 미리보기에 들어가요", sw.Control)));
                if (id == "UnityLighting")
                {
                    // 조명 배치판: 주광·정면광·추가 조명 1~3을 위에서 본 판에서 끌어 옮긴다 (LightPad.cs)
                    var pad = new StackPanel();
                    pad.Children.Add(Ui.Wrap("조명 배치 — 위에서 본 아바타 둘레, 점을 끌어 옮겨요", Ui.KText2, 12));
                    pad.Children.Add(new LightPad(Current, (list, label, preview) => { if (preview) { _suppress = true; Commit(list, label, true); _suppress = false; } else Commit(list, label, false); }, () => _ctx.Camera));
                    tab.Children.Add(Ui.Card(pad));
                }
                var rows = new StackPanel();
                Ui.FoldSection? fold = null;
                string curGroup = "";
                foreach (var p in prov["params"].Items)
                {
                    var row = ParamRow(id, p, entry);
                    Control r = IsExpert(p["name"].AsString) ? Ui.Expert(row) : row;
                    var g = p["group"].AsString;
                    if (g.Length == 0) { rows.Children.Add(r); fold = null; curGroup = ""; continue; }
                    if (fold == null || g != curGroup)
                    {
                        curGroup = g;
                        var key = id + "/" + g;
                        // 첫 줄이 켜기 스위치면: 켜진 구역은 처음에 펼치고, 접힌 제목 옆에 켜짐·꺼짐
                        bool? on = p["type"].AsString == "bool" ? (entry != null && entry["params"].Has(p["name"].AsString) ? entry["params"][p["name"].AsString] : p["default"]).AsBool : (bool?)null;
                        fold = new Ui.FoldSection(g, GroupOpen.TryGetValue(key, out var o) ? o : on ?? false);
                        if (on != null) fold.Hint = on.Value ? "켜짐" : "꺼짐";
                        fold.Toggled += open => GroupOpen[key] = open;
                        rows.Children.Add(fold);
                    }
                    fold.Body.Children.Add(r);
                }
                if (rows.Children.Count > 0)
                {
                    rows.IsEnabled = active;
                    rows.Opacity = active ? 1 : 0.5;
                    tab.Children.Add(Ui.Card(rows));
                }
                _tabs.Add(id, title, tab, state: active);   // 꺼짐: 글씨, 다이얼 모드는 LED
            }
            _tabs.EndUpdate();
        }

        // 색 고르기 판: 자주 쓰는 배경·조명 색 (감마 값). 누르면 바로 적용하고 닫는다
        static readonly string[] PaletteHex =
        {
            "#FFFFFF", "#D9D9D9", "#A6A6A6", "#6B6B70", "#3A3A3E", "#1A1A1A", "#000000", "#00FF00",
            "#8C99AD", "#BFD4F2", "#7FB2E5", "#3D5A80", "#1B2A44", "#0047FF", "#FF00FF", "#00B140",
            "#F2E6D8", "#F7C6C7", "#FFD7A8", "#FFF1A8", "#C8E6C9", "#B39DDB", "#9E6B4F", "#E07A5F",
        };

        static Flyout Palette(Action<float[]> pick)
        {
            var grid = new WrapPanel { Width = 8 * 30 };
            var fly = new Flyout { Content = grid, Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var hex in PaletteHex)
            {
                TryHex(hex, out var c);
                var b = new Button { Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = new SolidColorBrush(Ui.ToColor(c)) };
                ToolTip.SetTip(b, hex);
                b.Click += (_, __) => { fly.Hide(); pick(c); };
                grid.Children.Add(b);
            }
            return fly;
        }

        /// "#RRGGBB" 또는 "RRGGBB" → 0~1 감마 값
        static bool TryHex(string? s, out float[] c)
        {
            c = new float[3];
            var t = (s ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
            c[0] = ((v >> 16) & 255) / 255f; c[1] = ((v >> 8) & 255) / 255f; c[2] = (v & 255) / 255f;
            return true;
        }

        Control ParamRow(string id, JNode p, JNode? entry)
        {
            var name = p["name"].AsString;
            var label = Ui.Text(p["label"].AsString.Length > 0 ? p["label"].AsString : name);
            ToolTip.SetTip(label, name);
            var val = entry != null && entry["params"].Has(name) ? entry["params"][name] : p["default"];

            JNode With(JNode v)
            {
                var list = Current().Clone();
                var e = Entry(list, id);
                if (e == null) { e = JNode.Obj().Set("provider", id).Set("version", 1).Set("params", JNode.Obj()); list.Add(e); }
                if (e["params"].Kind != JKind.Object) e.Set("params", JNode.Obj());
                e["params"].Set(name, v);
                return list;
            }

            // 기본값과 다르면 이름을 강조하고 그 줄만 되돌리는 단추 (예전엔 "기본 환경으로 되돌리기"로 전부만 되돌릴 수 있었다)
            bool modified = entry != null && entry["params"].Has(name) && entry["params"][name].ToJson() != p["default"].ToJson();
            Ui.MarkModified(label, modified);
            if (modified) ToolTip.SetTip(label, name + "\n기본값: " + p["default"].ToJson());
            Grid Row(Control value)
            {
                Button? reset = null;
                if (modified)
                    reset = Ui.IconButton(Glyph.Reset, () =>
                    {
                        var list = Current().Clone();
                        Entry(list, id)?["params"].Remove(name);
                        Commit(list, "환경 되돌리기: " + (p["label"].AsString.Length > 0 ? p["label"].AsString : name), false);
                    }, "기본값으로 되돌리기");
                return Ui.PropRow(label, value, reset);
            }

            switch (p["type"].AsString)
            {
                case "enum":
                    {
                        var combo = Ui.CellCombo(new ComboBox());
                        var opts = p["options"].Items.Select(o => o.AsString).ToList();
                        var labels = p["optionLabels"].Items.Select(o => o.AsString).ToList();
                        for (int i = 0; i < opts.Count; i++) combo.Items.Add(new ComboBoxItem { Content = i < labels.Count ? labels[i] : opts[i], Tag = opts[i] });
                        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == val.AsString);
                        combo.SelectionChanged += (_, __) => { if (combo.SelectedItem is ComboBoxItem it) Commit(With((string)it.Tag!), "환경: " + name, false); };
                        return Row(combo);
                    }
                case "color":
                    {
                        // [r, g, b] 0~1 (감마 값). 견본(누르면 색 고르기 판) + "r, g, b" 또는 #RRGGBB 칸
                        var cur = val.Kind == JKind.Array ? val.ToFloatArray() : new float[] { 1, 1, 1 };
                        void Set(float[] v)
                        {
                            var list = With(JNode.FloatArray(v.Select(x => Math.Clamp(x, 0f, 1f)).Take(3)));
                            // 배경 색을 고르면 "직접 고르기"도 켠다 (꺼 둔 채 고르면 아무것도 안 바뀌어 보여서)
                            if (name == "bgColor") Entry(list, id)!["params"].Set("bgCustom", true);
                            Commit(list, "환경: " + p["label"].AsString, false);
                        }
                        var swatch = new Button { Width = 32, Height = 24, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Ui.ToColor(cur)), BorderBrush = Brushes.Gray };
                        ToolTip.SetTip(swatch, "색 고르기");
                        swatch.Flyout = Palette(Set);
                        TextBox box = null!;
                        box = Ui.Cell(s =>
                        {
                            if (TryHex(s, out var hv)) { Set(hv); return; }
                            if (!Ui.TryParseList(s, 3, cur, out var v)) { box.Text = Ui.FormatList(cur, 3); return; }
                            Set(v);
                        }, () => box.Text = Ui.FormatList(cur, 3));
                        box.Text = Ui.FormatList(cur, 3);
                        ToolTip.SetTip(box, "R, G, B (0~1) 또는 #RRGGBB");
                        var row = new DockPanel();
                        DockPanel.SetDock(swatch, Dock.Left);
                        row.Children.Add(swatch); row.Children.Add(box);
                        return Row(row);
                    }
                case "file":
                    {
                        // 경로 칸(직접 붙여넣기도 됨) + 찾기 · 비우기
                        var path = val.AsString;
                        var box = Ui.Cell(t => Commit(With((t ?? "").Trim().Trim('"')), "환경: " + p["label"].AsString, false), () => { });
                        box.Text = path;
                        ToolTip.SetTip(box, path.Length > 0 ? path : "파일 경로");
                        var pick = Ui.Button("찾기…", async () =>
                        {
                            var top = TopLevel.GetTopLevel(this);
                            if (top == null) return;
                            var exts = p["filter"].Items.Select(x => "*." + x.AsString).ToArray();
                            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                            {
                                Title = p["label"].AsString, AllowMultiple = false,
                                FileTypeFilter = exts.Length > 0 ? new[] { new FilePickerFileType(p["label"].AsString) { Patterns = exts } } : null,
                            });
                            if (files.Count > 0 && files[0].TryGetLocalPath() is string fp) Commit(With(fp), "환경: " + p["label"].AsString, false);
                        }, "파일 고르기");
                        pick.Margin = new Thickness(6, 0, 0, 0);
                        var row = new DockPanel();
                        DockPanel.SetDock(pick, Dock.Right);
                        row.Children.Add(pick); row.Children.Add(box);
                        return Row(row);
                    }
                case "bool":
                    {
                        var sw = new Ui.Switch(v => Commit(With(v), "환경: " + name, false));
                        sw.SetQuiet(val.AsBool);
                        return Row(sw.Control);
                    }
                default:
                    {
                        var box = Ui.NumberCell(v => Commit(With(v), "환경: " + name, false));
                        box.Text = Ui.F(val.AsDouble);
                        var sl = new Ui.EditSlider(p["min"].AsDouble, p["max"].AsDouble,
                            v => { _suppress = true; Commit(With(Math.Round(v, 3)), "", true); _suppress = false; box.Text = Ui.F(v); },
                            v => Commit(With(Math.Round(v, 3)), "환경: " + name, false));
                        sl.SetQuiet(val.AsDouble);
                        double min = p["min"].AsDouble, max = p["max"].AsDouble;
                        if (max > min)
                            Ui.Scrub(label, () => Entry(Current(), id) is { } e && e["params"].Has(name) ? e["params"][name].AsDouble : p["default"].AsDouble,
                                v => { _suppress = true; Commit(With(v), "", true); _suppress = false; box.Text = Ui.F(v); sl.SetQuiet(v); },
                                v => Commit(With(v), "환경: " + name, false), (max - min) / 300, min, max);
                        return Row(Ui.NumberWithSlider(box, sl.Slider));
                    }
            }
        }
    }
}
