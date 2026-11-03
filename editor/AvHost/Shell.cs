// 메인 창 구성 요소: 상태 표시줄 항목 · 사이드바 · 확인 대화상자.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{

    /// <summary>상태 표시줄 항목: 색 점 + 글자.</summary>
    public sealed class StatusItem : StackPanel
    {
        public enum State { Off, Busy, On, Warn, Error }
        readonly Ellipse _dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _text;

        public StatusItem(string text)
        {
            Orientation = Orientation.Horizontal; Spacing = 7;
            _text = Ui.Text(text, 12, FontWeight.Normal, Ui.KText2);
            _text.MaxWidth = 380;
            Children.Add(_dot); Children.Add(_text);
            Set(State.Off, text);
        }

        public void Set(State s, string text)
        {
            _text.Text = text;
            ToolTip.SetTip(this, text);
            var key = s switch { State.On => Ui.KSuccess, State.Warn => Ui.KCaution, State.Error => Ui.KCritical, State.Busy => Ui.KAccent, _ => Ui.KText3 };
            _dot.Bind(Shape.FillProperty, _dot.GetResourceObservable(key));
        }
    }

    /// <summary>왼쪽 사이드바: 아이콘 + 이름 (+ 단축키), 아래에 "메뉴 접기".</summary>
    public sealed class Sidebar : DockPanel
    {
        readonly StackPanel _top = new StackPanel { Spacing = 2 }, _bottom = new StackPanel { Spacing = 2 };
        readonly Dictionary<string, (Border Item, Border Pill, TextBlock Icon, TextBlock Label)> _items = new Dictionary<string, (Border, Border, TextBlock, TextBlock)>();
        readonly List<Control> _labels = new List<Control>();
        // 항목 모양을 바꿀 때 (깔끔한 배치의 아이콘 막대): 항목 · 줄 · 아이콘 · 이름 · 단축키 칩
        readonly List<(Border Item, DockPanel Row, TextBlock Icon, TextBlock Text, Control? Chip)> _parts = new List<(Border, DockPanel, TextBlock, TextBlock, Control?)>();
        bool _collapsed, _narrow, _ribbon;
        TextBlock _toggleIcon = null!;
        Control _toggle = null!;
        public string? Current { get; private set; }
        /// <summary>항목 칸 (시험용: VR 레이저 클릭 자리).</summary>
        public Control? ItemOf(string key) => _items.TryGetValue(key, out var v) ? v.Item : null;
        public event Action<bool>? CollapsedChanged;

        public bool Collapsed
        {
            get => _collapsed;
            set
            {
                if (_collapsed == value) return;
                _collapsed = value;
                Apply();
                CollapsedChanged?.Invoke(value);
            }
        }

        /// <summary>창이 좁을 때 저절로 아이콘만 (사용자 설정 Collapsed는 그대로 둔다). 좁은 동안은 접기 단추를 숨긴다.</summary>
        public bool Narrow
        {
            get => _narrow;
            set { if (_narrow == value) return; _narrow = value; Apply(); }
        }

        /// <summary>
        /// 아이콘 막대 (깔끔한 배치, 2026-10-21): 좁은 막대에 아이콘 + 그 아래 작은 이름 (Obsidian 리본처럼 좁지만 무엇인지는 글자로 보인다).
        /// 접기 단추·단축키 칩은 숨긴다 (단축키는 툴팁).
        /// </summary>
        public bool Ribbon
        {
            get => _ribbon;
            set { if (_ribbon == value) return; _ribbon = value; Apply(); }
        }

        /// <summary>아이콘 막대에 들어가게 줄인 이름. 영어는 페이지 이름이 길어 따로 (2026-11-02, 나머지 언어는 글 표가 바꾼다)</summary>
        static string Short(string label) => label switch
        {
            "블렌드쉐이프" => "쉐이프", "전문가 모드" => "전문가", "쉬운 모드" => "쉬운", "간편 모드" => "간편", "메뉴 접기" => "접기",   // 간편 모드: Apply가 이미 바뀐 글(글 표)을 읽을 때
            "환경" when Texts.Lang == "en" => "Env", "최적화" when Texts.Lang == "en" => "Optimize",
            _ => label,
        };

        /// <summary>항목 이름 바꾸기 (아이콘 막대면 줄인 이름으로 보인다)</summary>
        public void SetLabel(TextBlock text, string label)
        {
            text.Tag = label;
            text.Text = _ribbon ? Short(label) : label;
        }

        void Apply()
        {
            if (_ribbon)
            {
                Width = 66; Margin = new Thickness(0);
                foreach (var (item, row, icon, text, chip) in _parts)
                {
                    DockPanel.SetDock(icon, Dock.Top);
                    icon.Width = double.NaN; icon.Margin = new Thickness(0, 7, 0, 2); icon.FontSize = 17;
                    text.FontSize = 10.5; text.HorizontalAlignment = HorizontalAlignment.Center; text.TextAlignment = TextAlignment.Center; text.IsVisible = true;
                    text.TextTrimming = TextTrimming.CharacterEllipsis; text.MaxWidth = 60;
                    if (text.Tag is not string) text.Tag = Texts.Original(text) ?? text.Text;   // 원래 이름 (기본 배치로 돌아갈 때). 화면 언어로 바뀌기 전 원문
                    text.Text = Short((string)text.Tag!);
                    if (chip != null) chip.IsVisible = false;
                    item.Height = 52; item.Padding = new Thickness(0); item.Margin = new Thickness(5, 0);
                }
                _toggle.IsVisible = false;
                return;
            }
            Margin = new Thickness(10, 6, 12, 0);   // 카드 배치: 사이드바와 작업 창 사이 틈
            foreach (var (item, row, icon, text, chip) in _parts)
            {
                DockPanel.SetDock(icon, Dock.Left);
                icon.Width = 32; icon.Margin = new Thickness(0); icon.FontSize = 16;
                if (text.Tag is string full) text.Text = full;
                text.FontSize = 14; text.HorizontalAlignment = HorizontalAlignment.Stretch; text.TextAlignment = TextAlignment.Left; text.MaxWidth = double.PositiveInfinity;
                item.Height = 40; item.Padding = new Thickness(0, 0, 8, 0); item.Margin = new Thickness(0);
            }
            bool c = _collapsed || _narrow;
            Width = c ? 48 : 200;
            foreach (var l in _labels) l.IsVisible = !c;
            _toggleIcon.Text = c ? Glyph.OpenPane : Glyph.ClosePane;
            ToolTip.SetTip(_toggle, c ? "메뉴 펼치기" : null);
            _toggle.IsVisible = !_narrow;
        }
        public event Action<string>? Selected;

        public Sidebar()
        {
            Width = 200; Margin = new Thickness(8, 4, 8, 0);
            Texts.LangChanged += () => { if (_ribbon) Apply(); };   // 줄인 이름이 언어마다 다르다
            SetDock(_bottom, Dock.Bottom);
            // 창이 낮으면(VR 글자 크기 1.5배 등) 위 항목이 아래 항목(설정·로그) 밑으로 겹치지 않게 잘라 휠로 넘긴다
            Children.Add(_bottom); Children.Add(new ScrollViewer { Content = _top, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            var (toggle, toggleIcon, _) = MakeItem(Glyph.ClosePane, "메뉴 접기", null);
            _toggle = toggle; _toggleIcon = toggleIcon;
            toggle.PointerReleased += (_, __) => Collapsed = !Collapsed;
            _bottom.Children.Add(toggle);
        }

        public void Add(string key, string glyph, string label, string? hint) => AddTo(_top, key, glyph, label, hint);
        public void AddBottom(string key, string glyph, string label, string? hint) => _bottom.Children.Insert(0, Make(key, glyph, label, hint));

        /// <summary>페이지가 아닌 단추 (아래쪽, 예: 쉬운/전문가 모드). 아이콘·글자는 돌려준 것으로 바꾼다.</summary>
        public (TextBlock Icon, TextBlock Label, Control Item) AddBottomAction(string glyph, string label, Action onClick)
        {
            var (item, icon, text) = MakeItem(glyph, label, null);
            item.PointerReleased += (_, __) => onClick();
            _bottom.Children.Insert(0, item);
            return (icon, text, item);
        }
        void AddTo(StackPanel host, string key, string glyph, string label, string? hint) => host.Children.Add(Make(key, glyph, label, hint));

        (Border, TextBlock, TextBlock) MakeItem(string glyph, string label, string? hint)
        {
            var icon = Ui.Icon(glyph, 16, Ui.KText2); icon.Width = 32;
            var text = Ui.Text(label, 14);
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            Control? chipRef = null;
            if (hint != null)
            {
                var chip = new Border { Child = Ui.Text(hint, 11, FontWeight.Normal, Ui.KText3), Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center }.Stroke(Ui.KCardStroke);
                DockPanel.SetDock(chip, Dock.Right);
                row.Children.Add(chip);
                _labels.Add(chip);
                chipRef = chip;
            }
            row.Children.Add(text);
            _labels.Add(text);
            var item = new Border { Child = row, Height = 40, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            item.PointerEntered += (_, __) => { if (!IsSelected(item)) item.Bg(Ui.KHover); };
            item.PointerExited += (_, __) => { if (!IsSelected(item)) item.Background = Brushes.Transparent; };
            _parts.Add((item, row, icon, text, chipRef));
            return (item, icon, text);
        }

        bool IsSelected(Border item) => Current != null && _items.TryGetValue(Current, out var it) && it.Item == item;

        Control Make(string key, string glyph, string label, string? hint)
        {
            var (item, icon, text) = MakeItem(glyph, label, hint);
            var pill = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, IsHitTestVisible = false };
            pill.Bg(Ui.KAccent);
            var g = new Grid(); g.Children.Add(item); g.Children.Add(pill);
            ToolTip.SetTip(g, label + (hint != null ? $" ({hint.Replace(' ', '+')})" : ""));
            item.PointerReleased += (_, __) => Select(key);
            _items[key] = (item, pill, icon, text);
            return g;
        }

        public void Select(string key)
        {
            Current = key;
            foreach (var kv in _items)
            {
                bool sel = kv.Key == key;
                if (sel) kv.Value.Item.Bg(Ui.KSelect); else kv.Value.Item.Background = Brushes.Transparent;
                kv.Value.Pill.IsVisible = sel;
                kv.Value.Icon.Fg(sel ? Ui.KAccentText : Ui.KText2);
                kv.Value.Label.FontWeight = sel ? FontWeight.SemiBold : FontWeight.Normal;
            }
            Selected?.Invoke(key);
        }
    }

    /// <summary>간단한 확인 대화상자 (Avalonia에는 기본 메시지 상자가 없다).</summary>
    public static class Dialog
    {
        public static Task<int> Ask(Window owner, string title, string text, params string[] buttons)
        {
            var w = new Window
            {
                Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = owner.FontFamily, FontSize = 14,
                Topmost = true,   // 핀으로 고정한 떠 있는 창·컨트롤 보드 뒤에 숨지 않게 (닫기 확인이 안 보여 종료가 멈춘 것처럼 보였다)
            };
            w.Bg(Ui.KCard);
            int result = -1;
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.Text(title, 17, FontWeight.SemiBold));
            p.Children.Add(Ui.Wrap(text, Ui.KText2, 13));
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = Ui.Button(buttons[i], () => { result = idx; w.Close(); }, accent: i == 0);
                b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center;
                if (i == 0) b.IsDefault = true;
                if (i == buttons.Length - 1 && buttons.Length > 1) b.IsCancel = true;
                row.Children.Add(b);
            }
            p.Children.Add(row);
            w.Content = p;
            var tcs = new TaskCompletionSource<int>();
            w.Closed += (_, __) => tcs.TrySetResult(result == -1 ? buttons.Length - 1 : result);
            w.ShowDialog(owner);
            return tcs.Task;
        }

        /// <summary>한 줄 입력. 취소하면 null.</summary>
        public static Task<string?> Prompt(Window owner, string title, string text, string initial)
        {
            var w = new Window
            {
                Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = owner.FontFamily, FontSize = 14,
            };
            w.Bg(Ui.KCard);
            string? result = null;
            var box = new TextBox { Text = initial };
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.Text(title, 17, FontWeight.SemiBold));
            p.Children.Add(Ui.Wrap(text, Ui.KText2, 13));
            p.Children.Add(box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var ok = Ui.Button("확인", () => { if ((box.Text ?? "").Trim().Length > 0) { result = box.Text!.Trim(); w.Close(); } }, accent: true);
            var cancel = Ui.Button("취소", () => w.Close());
            foreach (var b in new[] { ok, cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; row.Children.Add(b); }
            ok.IsDefault = true; cancel.IsCancel = true;
            p.Children.Add(row);
            w.Content = p;
            w.Opened += (_, __) => { box.Focus(); box.SelectAll(); };
            var tcs = new TaskCompletionSource<string?>();
            w.Closed += (_, __) => tcs.TrySetResult(result);
            w.ShowDialog(owner);
            return tcs.Task;
        }
    }
}
