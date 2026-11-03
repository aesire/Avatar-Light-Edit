// VR 빠른 설정 (2026-10-24 사용자): B/Y를 길게 누르면 그 손에 붙어 나오는 작은 사각 창 — 몸 재기·크기 등 간단한 설정 (VRChat 액션 메뉴 자리, 원형 대신 사각 격자).
// 데스크톱에는 보이지 않게 화면 밖에 띄워 두고, VrPanelFeed가 다른 VR 창과 같은 길로 그려 보낸다 (id "hand"). 단추는 MainWindow의 VR 메뉴와 같은 일을 한다.
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    sealed class VrHandMenu : Window
    {
        /// <summary>칸 하나: 키 · 아이콘(글리프 또는 짧은 글자) · 이름 · 누르면 · 켜짐 표시(있으면 강조).</summary>
        public sealed record Item(string Key, string Icon, Func<string> Label, Action Act, Func<bool>? On = null, bool TextIcon = false);

        readonly List<(Item Item, Button Button, TextBlock Label)> _cells = new List<(Item, Button, TextBlock)>();
        readonly TextBlock _head;
        readonly Func<string> _headText;

        public VrHandMenu(Window owner, IReadOnlyList<Item> items, Func<string> head)
        {
            _headText = head;
            SystemDecorations = SystemDecorations.None;
            ShowInTaskbar = false; ShowActivated = false; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-32000, -32000);   // 데스크톱에는 안 보이게 (VR에만)
            Columns = items.Count > 12 ? 4 : 3;
            Width = 140 * Columns; Height = 140 * ((items.Count + Columns - 1) / Columns) + 20;   // 칸마다 140 (3×3 = 420×440)
            FontFamily = owner.FontFamily; FontSize = 15;
            this.Bg(Ui.KBase);

            var root = new DockPanel { Margin = new Thickness(10) };
            _head = Ui.Text("", 16, FontWeight.SemiBold, Ui.KText2);
            _head.HorizontalAlignment = HorizontalAlignment.Center; _head.Margin = new Thickness(0, 2, 0, 8);
            DockPanel.SetDock(_head, Dock.Top);
            root.Children.Add(_head);
            var grid = new UniformGrid { Columns = Columns };
            foreach (var it in items)
            {
                var icon = it.TextIcon ? Ui.Text(it.Icon, 34, FontWeight.Bold) : Ui.Icon(it.Icon, 30);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var label = Ui.Text("", 15, FontWeight.SemiBold);
                label.TextAlignment = TextAlignment.Center; label.TextWrapping = TextWrapping.Wrap; label.HorizontalAlignment = HorizontalAlignment.Center;
                var stack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(icon); stack.Children.Add(label);
                var b = new Button { Content = stack, Margin = new Thickness(5), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(10) };
                var item = it;
                b.Click += (_, __) => { item.Act(); Refresh(); };
                _cells.Add((it, b, label));
                grid.Children.Add(b);
            }
            root.Children.Add(grid);
            Content = root;
            Refresh();
        }

        /// <summary>열 수 (12칸까지 3, 넘으면 4). VR 안 너비도 이 비율로 (MainWindow).</summary>
        public int Columns { get; }

        public Button? ButtonOf(string key) => _cells.Find(c => c.Item.Key == key).Button;

        /// <summary>이름·켜짐 표시를 지금 값으로 (VR 상태·설정이 바뀔 때).</summary>
        public void Refresh()
        {
            _head.Text = _headText();
            foreach (var (item, button, label) in _cells)
            {
                label.Text = item.Label();
                bool on = item.On?.Invoke() == true;
                button.Bg(on ? Ui.KSelect : Ui.KCard);
                // 켜진 칸은 강조색 테두리 (바탕색만으로는 어두운 테마에서 잘 안 보였다)
                button.BorderThickness = new Thickness(on ? 3 : 0);
                if (on) button.Bind(BorderBrushProperty, button.GetResourceObservable(Ui.KAccent)); else button.BorderBrush = null;
            }
        }
    }
}
