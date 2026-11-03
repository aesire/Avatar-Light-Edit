// "아바타" 작업 창 › 붙인 것 탭 맨 아래: "설치된 기믹 보기" 단추를 눌러야 목록이 펼쳐진다 (2026-10-22, 사용자 요청).
// 목록은 가져올 때 importsim이 IR에 넣은 gimmicks (importsim/ir/IrGimmicks.cs). 그보다 먼저 가져온 아바타는 단추를 누를 때
// 원본 프로젝트·패키지에서 장면만 다시 해석해 찾는다 (IrExport.Scan, 작업 스레드). 재현이 아니라 "들어 있는 것"의 추정 목록이다.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class AvatarPanel
    {
        static bool _gimOpen;
        JNode _gim = JNode.Null; IrScene? _gimFor;
        string? _gimError; bool _gimScanning;

        void GimmickSection(IrScene scene)
        {
            if (_gimFor != scene) { _gimFor = scene; _gim = scene.Raw["gimmicks"]; _gimError = null; }
            var btn = Ui.Button(_gimOpen ? "설치된 기믹 숨기기" : "설치된 기믹 보기", () => { _gimOpen = !_gimOpen; Rebuild(); },
                "이 아바타에 들어 있는 기믹·의상 프리팹, Modular Avatar·VRCFury 같은 도구, 기능 메뉴, FX 레이어를 목록으로 보여요 (가져온 원본에서 찾은 추정)", Glyph.Search);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Margin = new Thickness(0, 16, 0, 0);
            _page.Children.Add(btn);
            if (!_gimOpen) return;

            if (_gim.IsNull)
            {
                if (_gimError != null) { _page.Children.Add(Ui.Card(Ui.Wrap("기믹을 찾지 못했습니다: " + _gimError, Ui.KText2, 13))); return; }
                var src = scene.Raw["source"];
                if (src.IsNull || src["guid"].AsString.Length == 0) { _page.Children.Add(Ui.Card(Ui.Wrap("원본 정보가 없는 아바타라 기믹을 찾을 수 없어요. 다시 가져오면 보여요.", Ui.KText2, 13))); return; }
                _page.Children.Add(Ui.Card(Ui.Wrap("원본 프로젝트에서 기믹을 찾는 중… (처음 가져온 뒤로 기믹 찾기가 생겨서 한 번 더 읽어요)", Ui.KText2, 13)));
                if (!_gimScanning) StartScan(scene, src);
                return;
            }
            FillGimmicks(_gim);
        }

        /// <summary>시험용: 목록을 펼치고, 찾기가 끝났는지 (오류면 그 문구).</summary>
        internal void OpenGimmicks(bool open) { _gimOpen = open; Rebuild(); }
        internal (bool Done, string? Error, int Prefabs, int Tools) GimmickState => (!_gim.IsNull || _gimError != null, _gimError, _gim["prefabs"].Count, _gim["tools"].Count);

        void StartScan(IrScene scene, JNode src)
        {
            _gimScanning = true;
            var sources = src["sources"].Items.Select(x => x.AsString).Where(x => x.Length > 0).ToList();
            string guid = src["guid"].AsString, root = src["root"].AsString;
            string? project = src["project"].AsStringOrNull;
            Task.Run(() =>
            {
                JNode result = JNode.Null; string? error = null;
                try { result = ImportSim.IrExport.Scan(sources, guid, root, project); }
                catch (Exception e) { error = e.Message; }
                Dispatcher.UIThread.Post(() =>
                {
                    _gimScanning = false;
                    if (_gimFor != scene) return;
                    _gim = result; _gimError = error;
                    if (_gimOpen) Rebuild();
                });
            });
        }

        void FillGimmicks(JNode g)
        {
            static string Join(JNode arr, int max) { var l = arr.Items.Select(x => x.AsString).ToList(); return string.Join(" · ", l.Take(max)) + (l.Count > max ? $" 외 {l.Count - max}개" : ""); }
            Control Count(int n) => Ui.Text(n.ToString(), 13, FontWeight.Normal, Ui.KText2);

            _page.Children.Add(Ui.Wrap("가져온 원본의 프리팹·컴포넌트·기능 메뉴로 찾은 추정이에요. Modular Avatar·VRCFury가 업로드할 때 만드는 메뉴와 레이어는 여기 없어요.", Ui.KText3, 12));

            // 넣은 프리팹: 기믹 / 의상·악세서리
            foreach (var (kind, title) in new[] { ("gimmick", "기믹"), ("outfit", "의상·악세서리 프리팹") })
            {
                var list = g["prefabs"].Items.Where(p => p["kind"].AsString == kind).ToList();
                if (list.Count == 0) continue;
                _page.Children.Add(Ui.SectionTitle($"{title} {list.Count}"));
                var rows = new StackPanel();
                foreach (var p in list)
                {
                    var nodes = p["nodes"].Items.Select(x => x.AsString).ToList();
                    var where = nodes.Count == 0 ? "" : nodes[0].Split('/').Last() == p["name"].AsString && nodes[0].Contains('/') ? nodes[0].Substring(0, nodes[0].LastIndexOf('/')) : nodes[0];
                    var tools = p["tools"].Items.Select(x => x.AsString).ToList();
                    var desc = (tools.Count > 0 ? string.Join(" · ", tools) : "도구 없음") + (where.Length > 0 && where != p["name"].AsString ? "  ·  " + where : "") + (nodes.Count > 1 ? $" 외 {nodes.Count - 1}곳" : "");
                    var row = Ui.DescRow(p["name"].AsString, desc, nodes.Count > 1 ? Ui.Text($"×{nodes.Count}", 13, FontWeight.Normal, Ui.KText2) : new Control());
                    ToolTip.SetTip(row, p["asset"].AsString + (p["components"].Count > 0 ? "\n" + Join(p["components"], 30) : ""));
                    rows.Children.Add(row);
                }
                _page.Children.Add(Ui.Card(rows));
            }

            // 도구 (컴포넌트 출처별)
            var toolList = g["tools"].Items.ToList();
            if (toolList.Count > 0)
            {
                _page.Children.Add(Ui.SectionTitle("도구 · 컴포넌트", "컴포넌트 스크립트가 들어 있는 패키지·폴더로 묶었어요. 개수는 컴포넌트 수예요."));
                var rows = new StackPanel();
                foreach (var t in toolList)
                {
                    var comps = t["components"].Props.Select(kv => $"{kv.Key} {kv.Value.AsInt}").ToList();
                    var desc = string.Join(" · ", comps.Take(6)) + (comps.Count > 6 ? $" 외 {comps.Count - 6}종" : "");
                    if (t["name"].AsString == "VRCFury" && g["vrcFury"].Props.Any()) desc += "  ·  기능: " + string.Join(" · ", g["vrcFury"].Props.Select(kv => $"{kv.Key} {kv.Value.AsInt}"));
                    var row = Ui.DescRow(t["name"].AsString, desc, Count(t["count"].AsInt));
                    ToolTip.SetTip(row, string.Join("\n", comps));
                    rows.Children.Add(row);
                }
                _page.Children.Add(Ui.Card(rows));
            }

            // 메뉴 · 레이어 · 파라미터
            var menu = g["menu"].Items.Select(x => x.AsString).ToList();
            var maMenu = g["maMenu"];
            var layers = g["fxLayers"];
            if (menu.Count > 0 || maMenu.Count > 0 || layers.Count > 0)
            {
                _page.Children.Add(Ui.SectionTitle("기능 메뉴 · 레이어"));
                var rows = new StackPanel();
                foreach (var top in menu.Where(m => !m.Contains('/')))
                {
                    var subs = menu.Where(m => m.StartsWith(top + "/", StringComparison.Ordinal)).Select(m => m.Substring(top.Length + 1)).ToList();
                    rows.Children.Add(Ui.DescRow(top, subs.Count > 0 ? string.Join(" · ", subs.Take(8)) + (subs.Count > 8 ? $" 외 {subs.Count - 8}개" : "") : "기능 메뉴 항목", subs.Count > 0 ? Count(subs.Count) : new Control()));
                }
                if (maMenu.Count > 0) rows.Children.Add(Ui.DescRow("MA 메뉴 항목", Join(maMenu, 12) + " (업로드할 때 메뉴에 추가해집니다)", Count(maMenu.Count)));
                if (layers.Count > 0) rows.Children.Add(Ui.DescRow("FX 레이어", Join(layers, 12), Count(layers.Count)));
                if (g.Has("params")) rows.Children.Add(Ui.DescRow("기능 파라미터", "아바타 디스크립터의 Expression Parameters (도구가 더하는 것 빼고)", Count(g["params"].AsInt)));
                _page.Children.Add(Ui.Card(rows));
            }
            if (g["prefabs"].Count == 0 && toolList.Count == 0 && menu.Count == 0)
                _page.Children.Add(Ui.Card(Ui.Wrap("찾은 기믹이 없어요.", Ui.KText2, 13)));
        }
    }
}
