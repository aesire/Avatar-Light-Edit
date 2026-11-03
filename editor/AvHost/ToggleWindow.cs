// 토글 묶음 만들기·고치기 창: 이름 + 묶을 오브젝트 고르기 (붙인 아이템 통째로 · 기본 옷·헤어 · 메시 하나씩). 결과는 ToggleSet.Make 값.
// 토글은 오브젝트 켜고 끄기만 한다 (VRChat 메뉴·MA·VRCFury 토글을 재현하지 않는다).
using System;
using System.Collections.Generic;
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
    public sealed class ToggleWindow : Window
    {
        readonly TextBox _name = new TextBox { Watermark = "예: 겉옷, 모자" };
        readonly TextBox _search = new TextBox { Watermark = "찾기" };
        readonly StackPanel _list = new StackPanel();
        readonly TextBlock _count = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly HashSet<int> _chosen;
        readonly List<(CheckBox Box, int Node, string Text)> _rows = new List<(CheckBox, int, string)>();

        /// <param name="current">고칠 묶음 (새로 만들면 null)</param>
        public ToggleWindow(IrScene scene, List<WardrobePart> parts, JNode? current)
        {
            Title = current == null ? "토글 만들기" : "토글 고치기";
            Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);
            _chosen = current == null ? new HashSet<int>() : new HashSet<int>(ToggleSet.Nodes(current));
            _name.Text = current?["name"].AsString ?? "";

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.DialogHeader(Title, "한 번에 켜고 끌 오브젝트를 골라요.", "누르면 모두 켜져 있으면 끄고, 아니면 모두 켜요. 위에서부터 1~9번째 토글은 Shift+1~9로 눌러요."));
            p.Children.Add(Ui.PropRow(Ui.Text("이름"), _name, null));
            p.Children.Add(Ui.PropRow(Ui.Text("오브젝트"), _search, null));
            var scroll = new ScrollViewer { Height = 340, Content = _list };
            p.Children.Add(Ui.Card(scroll));
            p.Children.Add(_count);

            // 붙인 아이템은 통째로(루트), 기본 메시는 묶음 종류와 함께, 나머지 메시는 이름만
            var seen = new HashSet<int>();
            foreach (var o in scene.Outfits) Add(o.Node, o.Name.Length > 0 ? o.Name : scene.Nodes[o.Node].Name, "붙인 아이템 통째로");
            foreach (var kind in Wardrobe.Kinds)
                foreach (var part in parts.Where(x => x.Kind == kind)) Add(part.Node, part.Name, "기본 " + Wardrobe.KindLabel(kind));
            foreach (var r in scene.Renderers)
                if (r.Node >= 0 && r.Node < scene.Nodes.Count) Add(r.Node, scene.Nodes[r.Node].Name, "메시");
            foreach (var n in _chosen.Where(n => !seen.Contains(n) && n >= 0 && n < scene.Nodes.Count).ToList()) Add(n, scene.Nodes[n].Name, "오브젝트");
            void Add(int node, string name, string what)
            {
                if (!seen.Add(node)) return;
                var box = new CheckBox { IsChecked = _chosen.Contains(node), Margin = new Thickness(4, 0) };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Ui.Text(name, 14));
                row.Children.Add(Ui.Text(what, 12, FontWeight.Normal, Ui.KText2));
                ((TextBlock)row.Children[1]).VerticalAlignment = VerticalAlignment.Center;
                box.Content = row;
                ToolTip.SetTip(box, scene.NodePath(node));
                box.IsCheckedChanged += (_, __) => { if (box.IsChecked == true) _chosen.Add(node); else _chosen.Remove(node); Refresh(); };
                _rows.Add((box, node, (name + " " + what).ToLowerInvariant()));
                _list.Children.Add(box);
            }
            _search.TextChanged += (_, __) =>
            {
                var q = (_search.Text ?? "").Trim().ToLowerInvariant();
                foreach (var (box, _, text) in _rows) box.IsVisible = q.Length == 0 || text.Contains(q);
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var ok = Ui.Button(current == null ? "만들기" : "고치기", Done, accent: true);
            var cancel = Ui.Button("취소", () => Close(null));
            foreach (var b in new[] { ok, cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            ok.IsDefault = true; cancel.IsCancel = true;
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            p.Children.Add(buttons);
            Content = p;
            Opened += (_, __) => _name.Focus();
            Refresh();
        }

        void Refresh() => _count.Text = _chosen.Count == 0 ? "고른 오브젝트가 없어요" : $"오브젝트 {_chosen.Count}개";

        void Done()
        {
            var name = (_name.Text ?? "").Trim();
            if (_chosen.Count == 0) { _count.Text = "오브젝트를 하나 이상 골라 주세요"; _count.Fg(Ui.KCaution); return; }
            if (name.Length == 0) name = "토글";
            Close(ToggleSet.Make(name, _chosen.OrderBy(n => n)));
        }
    }
}
