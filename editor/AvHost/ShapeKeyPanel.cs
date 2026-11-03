// 스튜디오 페이지 "블렌드쉐이프 단축키": 방송 중 홍조·얼굴 이펙트·눈동자 모양 같은 블렌드쉐이프를 단축키로 (Core.ShapeKeys).
//   아바타 기능 메뉴에서 가져오기 (IR menuShapes — 메뉴의 토글·라디얼 중 블렌드쉐이프를 움직이는 것) · 직접 만들기 (ShapeKeyWindow)
//   줄마다 누르기(지금 상태) · 단축키 · 고치기 · 빼기. 단축키는 전역(GlobalHotkeys)이라 게임 중에도 눌린다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class ShapeKeyPanel : StackPanel
    {
        readonly EditorContext _ctx;
        readonly ShapeKeyRunner _run;
        readonly StackPanel _list = new StackPanel();
        readonly Button _import;
        readonly TextBlock _note = Ui.Text("", 12, FontWeight.Normal, Ui.KCaution);

        public ShapeKeyPanel(EditorContext ctx, ShapeKeyRunner run)
        {
            _ctx = ctx; _run = run;
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _import = Ui.Button("아바타 메뉴에서 가져오기", () => { }, "아바타 기능 메뉴(Expressions Menu)의 토글·라디얼 중 블렌드쉐이프를 움직이는 것을 단축키로 가져와요", Glyph.Import);
            _import.Flyout = ImportMenu();
            tools.Children.Add(_import);
            tools.Children.Add(Ui.Button("직접 만들기…", () => Edit(-1), "메시와 블렌드쉐이프를 골라 단축키를 만들어요", Glyph.Add));
            Children.Add(tools);
            Children.Add(_list);
            _note.TextWrapping = TextWrapping.Wrap;
            Children.Add(_note);
            _run.Changed += Refresh;
            _ctx.DocumentOpened += Refresh;
            Refresh();
        }

        Document? Doc => _ctx.Doc;
        Window? Owner => TopLevel.GetTopLevel(this) as Window;

        /// <summary>등록하지 못한 단축키 (다른 프로그램이 쓰는 조합).</summary>
        public void ShowFailed(List<string> failed) =>
            _note.Text = failed.Count == 0 ? "" : "다른 프로그램이 이미 사용하는 단축키라 등록하지 못했습니다: " + string.Join(", ", failed.Select(GlobalHotkeys.Text));

        MenuFlyout ImportMenu()
        {
            var menu = new MenuFlyout();
            menu.Opening += (_, __) =>
            {
                menu.Items.Clear();
                var s = Doc?.Original;
                if (s == null || s.MenuShapes.Count == 0)
                {
                    menu.Items.Add(new MenuItem { Header = s == null ? "먼저 아바타를 열어 주세요" : "메뉴에서 블렌드쉐이프 조작을 찾지 못했어요 (예전에 가져온 파일이면 다시 가져오세요)", IsEnabled = false });
                    return;
                }
                var have = new HashSet<string>(Doc!.Get(Addr.ShapeKeys).Items.Select(k => k["name"].AsString));
                var fresh = s.MenuShapes.Where(m => !have.Contains(m.Name)).ToList();
                var all = new MenuItem { Header = $"아직 없는 것 모두 ({fresh.Count}개)", IsEnabled = fresh.Count > 0 };
                all.Click += (_, __) => Add(fresh);
                menu.Items.Add(all);
                menu.Items.Add(new Separator());
                foreach (var m in s.MenuShapes)
                {
                    var mi = new MenuItem { Header = $"{m.Menu}  ·  {(m.Type == "radial" ? "라디얼" : m.Type == "button" ? "버튼" : "토글")} · BlendShape {m.Shapes.Count}개" + (have.Contains(m.Name) ? "  (있음)" : "") };
                    var one = m;
                    mi.Click += (_, __) => Add(new List<IrMenuShape> { one });
                    menu.Items.Add(mi);
                }
            };
            menu.Items.Add(new MenuItem { Header = "…" });   // Opening에서 채운다
            return menu;
        }

        /// <summary>메뉴 후보를 정의로 더한다. 눈동자 모양(eye_*)처럼 같이 켜면 안 되는 것은 묶음 "눈동자"로.</summary>
        void Add(List<IrMenuShape> items)
        {
            if (Doc == null || items.Count == 0) return;
            var list = Doc.Get(Addr.ShapeKeys).Items.Select(x => x.Clone()).ToList();
            foreach (var m in items)
            {
                var k = ShapeKeys.FromMenu(Doc.Original, m);
                if (k["shapes"].Count == 0) continue;
                if (m.Type != "radial" && m.Shapes.All(x => IsEyeShape(x.Shape))) k.Set("group", "눈동자");
                list.Add(k);
            }
            ShapeKeys.Replace(Doc, list, items.Count == 1 ? $"단축키 가져오기: {items[0].Name}" : $"단축키 {items.Count}개 가져오기");
            _ctx.Status($"메뉴에서 {items.Count}개를 가져왔어요. 고치기(연필)에서 단축키를 정해요.");
        }

        static bool IsEyeShape(string s)
        {
            var l = s.ToLowerInvariant();
            return l.StartsWith("eye_") || l.StartsWith("eyes_") || l.StartsWith("pupil") || l.StartsWith("hitomi") || l.StartsWith("瞳") || l.StartsWith("目_");
        }

        async void Edit(int i)
        {
            if (Doc == null) { _ctx.Status("먼저 아바타를 열어 주세요."); return; }
            if (Owner is not Window w) return;
            var list = Doc.Get(Addr.ShapeKeys).Items.Select(x => x.Clone()).ToList();
            var r = await new ShapeKeyWindow(Doc.Original, i >= 0 && i < list.Count ? list[i] : null).ShowDialog<JNode?>(w);
            if (r == null || Doc == null) return;
            if (i >= 0 && i < list.Count) list[i] = r; else list.Add(r);
            ShapeKeys.Replace(Doc, list, (i >= 0 ? "단축키 편집: " : "단축키 만들기: ") + r["name"].AsString);
        }

        void Remove(int i)
        {
            if (Doc == null) return;
            var list = Doc.Get(Addr.ShapeKeys).Items.Select(x => x.Clone()).ToList();
            if (i < 0 || i >= list.Count) return;
            var name = list[i]["name"].AsString;
            list.RemoveAt(i);
            ShapeKeys.Replace(Doc, list, "단축키 제거: " + name);
        }

        void Refresh()
        {
            _list.Children.Clear();
            var keys = _run.Keys;
            if (Doc == null || keys.Count == 0)
            {
                _list.Children.Add(Ui.Wrap(Doc == null ? "아바타를 열면 쓸 수 있어요." : "아직 없어요. 아바타 메뉴에서 가져오거나 직접 만들어요."));
                return;
            }
            var rows = new StackPanel();
            for (int i = 0; i < keys.Count; i++)
            {
                int k = i;
                var def = keys[i];
                bool on = _run.IsOn(i);
                var press = Ui.Button(_run.Describe(i), () => _ctx.Status($"{def["name"].AsString} {_run.Press(k)}"), "누르기 (단축키와 같아요)", accent: on);
                press.MinWidth = 64; press.HorizontalContentAlignment = HorizontalAlignment.Center; press.Margin = new Thickness(0, 0, 8, 0);
                var name = Ui.Text(def["name"].AsString); name.VerticalAlignment = VerticalAlignment.Center;
                var sub = def["mode"].AsString == "cycle" ? "단계 " + ShapeKeys.StepsText(ShapeKeys.Steps(def)) : "켜고 끄기";
                if (def["group"].AsString.Length > 0) sub += " · 그룹 " + def["group"].AsString;
                ToolTip.SetTip(name, string.Join("\n", ShapeKeys.Shapes(def).Select(x => $"{x.Shape}: {x.Off:0.#} → {x.On:0.#}")) + "\n" + sub);
                var key = Ui.Text(def["key"].AsString.Length > 0 ? GlobalHotkeys.Text(def["key"].AsString) : "단축키 없음", 12, FontWeight.SemiBold, def["key"].AsString.Length > 0 ? Ui.KText2 : Ui.KText3);
                key.Margin = new Thickness(8, 0); key.VerticalAlignment = VerticalAlignment.Center;
                var edit = Ui.IconButton(Glyph.Edit, () => Edit(k), "고치기 (단축키·값·묶음)");
                var del = Ui.IconButton(Glyph.Delete, () => Remove(k), "빼기");
                var g = Ui.Columns(GridLength.Auto, Ui.Star(), GridLength.Auto, GridLength.Auto, GridLength.Auto);
                g.MinHeight = 40;
                g.Children.Add(press.At(0)); g.Children.Add(name.At(1)); g.Children.Add(key.At(2)); g.Children.Add(edit.At(3)); g.Children.Add(del.At(4));
                rows.Children.Add(g);
            }
            _list.Children.Add(Ui.Card(rows));
        }
    }
}
