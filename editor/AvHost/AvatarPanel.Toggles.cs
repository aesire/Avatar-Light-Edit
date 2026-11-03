// 아바타 페이지 "내 토글": 사용자가 묶은 오브젝트를 한 번에 켜고 끄기 (Core.ToggleSet, Addr.Toggles). Shift+1~9.
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class AvatarPanel
    {
        void ToggleSection(IrScene scene)
        {
            var toggles = Doc!.Get(Addr.Toggles);
            var add = Ui.Button("만들기…", () => EditToggle(-1), "켜고 끌 오브젝트를 묶어 토글을 만들어요 (위에서부터 Shift+1~9)", Glyph.Add);
            add.Margin = new Thickness(0);
            // "내 토글" 탭: 제목 줄(만들기) + 목록
            var head = new TitleRow();
            DockPanel.SetDock(add, Dock.Right);
            head.Children.Add(add);
            head.Children.Add(Ui.SectionTitle("내 토글", "옷·악세서리를 내 마음대로 묶어 한 번에 켜고 꺼요. 예: \"겉옷\" = 재킷 + 모자. 오브젝트 켜고 끄기만 하고, 세션에 저장되고 실행 취소돼요. 위에서부터 1~9번째는 Shift+1~9로 눌러요."));
            _page.Children.Add(head);
            if (toggles.Count == 0)
                _page.Children.Add(Ui.Wrap("아직 없어요. \"만들기…\"로 옷·악세서리를 묶어 보세요."));
            else
            {
                var rows = new StackPanel();
                for (int i = 0; i < toggles.Count; i++) rows.Children.Add(ToggleRow(toggles, i));
                _page.Children.Add(Ui.Card(rows));
            }
        }

        Control ToggleRow(JNode toggles, int i)
        {
            var t = toggles[i];
            int count = ToggleSet.Nodes(t).Count;
            var name = Ui.Text(t["name"].AsString);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
            var key = Ui.Text(i < ToggleSet.MaxKeys ? $"Shift+{i + 1}" : "", 12, FontWeight.Normal, Ui.KText2);
            key.VerticalAlignment = VerticalAlignment.Center;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(name); label.Children.Add(key);
            ToolTip.SetTip(label, $"오브젝트 {count}개: " + string.Join(", ", ToggleSet.Nodes(t).Where(n => n >= 0 && n < Doc!.Original.Nodes.Count).Select(n => Doc!.Original.Nodes[n].Name)));
            var sw = new Ui.Switch(on => { ToggleSet.Set(Doc!, Doc!.Get(Addr.Toggles)[i], on); });
            var menu = new MenuFlyout();
            void Item(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, __) => act(); menu.Items.Add(mi); }
            Item("고치기…", () => EditToggle(i));
            if (i > 0) Item("위로", () => Move(i, i - 1));
            if (i < toggles.Count - 1) Item("아래로", () => Move(i, i + 1));
            Item("지우기", () => { var list = Doc!.Get(Addr.Toggles).Items.Where((_, k) => k != i).ToList(); ToggleSet.Replace(Doc!, list, "토글 삭제: " + t["name"].AsString); });
            var more = Ui.IconButton(Glyph.More, () => { }, "고치기 · 순서 · 지우기");
            more.Flyout = menu;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(more); right.Children.Add(sw.Control);
            var g = Ui.Columns(Ui.Star(), GridLength.Auto);
            g.MinHeight = 38;
            g.Children.Add(label.At(0)); g.Children.Add(right.At(1));
            void Refresh()
            {
                var cur = Doc!.Get(Addr.Toggles);
                if (i >= cur.Count) return;
                bool on = ToggleSet.IsOn(Doc!, cur[i]);
                sw.SetQuiet(on);
                name.Opacity = on ? 1 : 0.6;
            }
            _meshRows["toggle:" + i] = Refresh;
            Refresh();
            return g;
        }

        void Move(int from, int to)
        {
            var list = Doc!.Get(Addr.Toggles).Items.ToList();
            var t = list[from]; list.RemoveAt(from); list.Insert(to, t);
            ToggleSet.Replace(Doc!, list, "토글 순서");
        }

        async void EditToggle(int i)
        {
            if (Doc == null || TopLevel.GetTopLevel(this) is not Window w) return;
            var list = Doc.Get(Addr.Toggles).Items.ToList();
            var r = await new ToggleWindow(Doc.Original, Parts(), i >= 0 ? list[i] : null).ShowDialog<JNode?>(w);
            if (r == null) return;
            if (i >= 0) list[i] = r; else list.Add(r);
            ToggleSet.Replace(Doc, list, (i >= 0 ? "토글 편집: " : "토글 만들기: ") + r["name"].AsString);
            int n = i >= 0 ? i : list.Count - 1;
            _ctx.Status($"토글 \"{r["name"].AsString}\" — 오브젝트 {ToggleSet.Nodes(r).Count}개" + (n < ToggleSet.MaxKeys ? $" · Shift+{n + 1}" : ""));
        }
    }
}
