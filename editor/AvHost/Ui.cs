// Avalonia Host: XAML 없이 코드로 UI를 만들기 위한 도우미.
// 설정 화면형 디자인: 회색 바탕 위 둥근 카드, 카드 안은 "이름 왼쪽 · 값 오른쪽" 줄, 줄 사이 구분선.
// 색은 App의 테마 사전(VP.* 키, 라이트/다크 두 벌)을 GetResourceObservable로 참조한다 → 테마가 바뀌면 바로 반영.
using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace VrcProto.AvHost
{
    public static class Ui
    {
        public const string KText = "VP.Text", KText2 = "VP.Text2", KText3 = "VP.Text3", KAccent = "VP.Accent", KAccentText = "VP.AccentText";
        public const string KBase = "VP.Base", KPanel = "VP.Panel", KCard = "VP.Card", KCardStroke = "VP.CardStroke";
        public const string KDivider = "VP.Divider", KHover = "VP.Hover", KSelect = "VP.Select", KStroke = "VP.Stroke";
        public const string KTitleBar = "VP.TitleBar", KToolbar = "VP.Toolbar";
        public const string KDial = "VP.Dial";   // 다이얼 호·세밀 조정 막대의 하이라이트 (기본 형광 노랑, 설정 "다이얼 색"). 없으면 강조색   // 최상단 바 · 에디터 상단 바 (바탕보다 살짝 연하고 아주 옅은 파랑)
        /// <summary>화면 글꼴 (2026-11-02): 일본어 화면은 일본 글꼴을 맨 앞에 — 없는 글자는 Windows 대체 글꼴(한국어 Windows면 맑은 고딕, 가나가 넓다)로 가서, 뒤에 적은 이름은 쓰이지 않는다</summary>
        public static FontFamily Font => new FontFamily(Texts.Lang == "ja" ? "Yu Gothic UI, Meiryo UI, Segoe UI Variable Text, Segoe UI" : "Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        public const string KCaution = "VP.Caution", KCritical = "VP.Critical", KSuccess = "VP.Success", KCautionBg = "VP.CautionBg";

        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public const double RowHeight = 44;

        public static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        public static bool TryParse(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        public static T Fg<T>(this T c, string key) where T : Control
        {
            c.Bind(TextElement.ForegroundProperty, c.GetResourceObservable(key));
            return c;
        }

        public static T Bg<T>(this T c, string? key) where T : Control
        {
            AvaloniaProperty p = c is Border ? Border.BackgroundProperty : c is Panel ? Panel.BackgroundProperty : TemplatedControl.BackgroundProperty;
            if (key == null) { c.ClearValue(p); if (c is Panel or Border) c.SetValue(p, Brushes.Transparent); }
            else c.Bind(p, c.GetResourceObservable(key));
            return c;
        }

        public static T Stroke<T>(this T b, string key) where T : Border
        {
            b.Bind(Border.BorderBrushProperty, b.GetResourceObservable(key));
            return b;
        }

        public static TextBlock Text(string s, double size = 14, FontWeight weight = FontWeight.Normal, string fg = KText)
            => new TextBlock { Text = s, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Fg(fg);

        /// <summary>
        /// 쉬운 모드에서 숨길 것 (자주 쓰지 않는 세부 설정). 창에 "easy" 클래스가 있으면 스타일이 숨긴다 (MainWindow.SetExpert) —
        /// 코드가 IsVisible을 직접 정하는 컨트롤은 지역 값이 이기므로 Border로 감싸 그 Border를 표시한다(ExpertBox).
        /// </summary>
        public static T Expert<T>(T c) where T : Control { c.Classes.Add("expert"); return c; }

        /// <summary>창에 쉬운/전문가 모드 스타일을 단다 (에디터 창과 떠 있는 창이 같이 쓴다). 창의 "easy" 클래스로 바뀐다.</summary>
        public static void AddModeStyles(Window w)
        {
            w.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Window>().Class("easy").Descendant().Class("expert")) { Setters = { new Avalonia.Styling.Setter(Visual.IsVisibleProperty, false) } });
            w.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Window>().Not(y => y.Class("easy")).Descendant().Class("easyonly")) { Setters = { new Avalonia.Styling.Setter(Visual.IsVisibleProperty, false) } });
        }
        public static Border ExpertBox(Control c) => Expert(new Border { Child = c });
        /// <summary>쉬운 모드에서만 보일 것 (예: "전문가 모드에서 더 보여요" 안내).</summary>
        public static T EasyOnly<T>(T c) where T : Control { c.Classes.Add("easyonly"); return c; }

        public static TextBlock Wrap(string s, string fg = KText2, double size = 13)
            => new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) }.Fg(fg);

        public static TextBlock Icon(string glyph, double size = 16, string fg = KText)
            => new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }.Fg(fg);

        public static StackPanel IconLabel(string glyph, string label, double iconSize = 14)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            p.Children.Add(Icon(glyph, iconSize));
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            t.Classes.Add("tblabel");   // 좁은 도구 모음(.compact)에서는 숨겨 아이콘만 (툴팁은 그대로)
            p.Children.Add(t);
            return p;
        }

        /// <summary>
        /// 도움말 단추 (ⓘ): 마우스를 올리면 설명이 뜨고, 누르면 같은 설명을 말풍선으로 띄운다.
        /// 긴 설명을 화면에 늘어놓으면 좁은 창에서 줄이 깨지고 읽기 어려워져서, 설명은 이것으로 옮긴다.
        /// </summary>
        public static Button Help(string text)
        {
            var b = new Button
            {
                Content = Icon(Glyph.Info, 13, KText3), Width = 24, Height = 24, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Help),
            };
            b.Classes.Add("help");
            ToolTip.SetShowDelay(b, 150);
            SetHelp(b, text);
            return b;
        }

        /// <summary>도움말 단추의 설명을 바꾼다.</summary>
        public static void SetHelp(Button b, string text)
        {
            ToolTip.SetTip(b, text);
            b.Flyout = new Flyout { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, FontSize = 13, LineHeight = 19 }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        }

        /// <summary>
        /// 한 줄 요약 + "자세히" 링크: 누르면 전체 설명을 말풍선으로 (제목이 없는 자리의 긴 안내를 줄일 때).
        /// </summary>
        public static Control InfoLink(string summary, string full)
        {
            var p = new DockPanel { Margin = new Thickness(2, 4, 0, 8) };
            var link = new Button
            {
                Content = Text("자세히", 12, FontWeight.Normal, KAccentText), Padding = new Thickness(6, 2), MinHeight = 0,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
                Flyout = new Flyout { Content = new TextBlock { Text = full, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 13, LineHeight = 19 }, Placement = PlacementMode.BottomEdgeAlignedLeft },
            };
            ToolTip.SetTip(link, full);
            DockPanel.SetDock(link, Dock.Right);
            p.Children.Add(link);
            var t = Text(summary, 12, FontWeight.Normal, KText2);
            ToolTip.SetTip(t, full);
            p.Children.Add(t);
            return p;
        }

        /// <summary>제목 글자 + (설명이 있으면) 도움말 단추를 한 줄로.</summary>
        static Control Titled(TextBlock t, string? help)
        {
            if (string.IsNullOrEmpty(help)) return t;
            var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            t.Margin = new Thickness(0);
            p.Children.Add(t); p.Children.Add(Help(help!));
            return p;
        }

        /// <summary>대화상자 제목 (+도움말) 과 한 줄 요약.</summary>
        public static StackPanel DialogHeader(string title, string summary, string? help = null)
        {
            var p = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
            p.Children.Add(Titled(Text(title, 17, FontWeight.SemiBold), help));
            if (summary.Length > 0) { var c = Caption(summary); c.FontSize = 13; p.Children.Add(c); }
            return p;
        }

        /// <summary>페이지 제목. 설명은 제목 옆 도움말 단추로 (좁은 창에서 잘리지 않게).</summary>
        public static Control PageHeader(string title, string subtitle)
            => Titled(Text(title, 22, FontWeight.SemiBold), subtitle);

        /// <summary>카드 위의 구역 제목 (+ 도움말).</summary>
        public static Control SectionTitle(string s, string? help = null)
        {
            var t = Text(s, 15, FontWeight.SemiBold);
            var c = Titled(t, help);
            c.Margin = new Thickness(2, 8, 0, 8);
            return c;
        }

        public static Button Button(string label, Action onClick, string? tip = null, string? glyph = null, bool accent = false)
        {
            var b = new Button { Content = glyph == null ? label : IconLabel(glyph, label), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6), MinHeight = 32 };
            if (accent) b.Classes.Add("accent");
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, __) => onClick();
            return b;
        }

        /// <summary>배경 없는 도구 모음 버튼.</summary>
        public static Button Subtle(string glyph, string? label, Action onClick, string tip)
        {
            var b = new Button
            {
                Content = label == null ? Icon(glyph, 14) : IconLabel(glyph, label),
                Padding = label == null ? new Thickness(9, 6) : new Thickness(10, 6, 12, 6), Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            };
            b.Classes.Add("subtle");
            ToolTip.SetTip(b, tip);
            b.Click += (_, __) => onClick();
            return b;
        }

        /// <summary>도구 모음의 펼침 메뉴 단추 (아이콘 · 이름 · ▾). label은 바꿀 수 있게 돌려준다.</summary>
        public static Button DropButton(string glyph, string label, MenuFlyout menu, out TextBlock labelText, string? tip = null)
        {
            var content = IconLabel(glyph, label);
            labelText = (TextBlock)content.Children[content.Children.Count - 1];
            content.Children.Add(Icon(Glyph.ChevronDown, 9, KText2));
            var b = new Button
            {
                Content = content, Flyout = menu, Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            };
            b.Classes.Add("subtle");
            if (tip != null) ToolTip.SetTip(b, tip);
            return b;
        }

        /// <summary>마지막 호출 뒤 ms 동안 조용하면 한 번 실행 (검색 칸 입력마다 목록 전체를 다시 만들지 않게).</summary>
        public static Action Debounce(Action act, int ms = 180)
        {
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, __) => { timer.Stop(); act(); };
            return () => { timer.Stop(); timer.Start(); };
        }

        /// <summary>시작 안내의 가로로 꽉 찬 큰 단추.</summary>
        public static Button WideButton(string label, Action onClick, string tip, string glyph, bool accent = false)
        {
            var b = Button(label, onClick, tip, glyph, accent);
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(0, accent ? 0 : 4, 0, 0); b.MinHeight = 38;
            return b;
        }

        public static Button IconButton(string glyph, Action onClick, string tip)
        {
            var b = new Button { Content = Icon(glyph, 12, KText2), Width = 28, Height = 28, Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, __) => onClick();
            return b;
        }

        /// <summary>
        /// 둥근 카드. 안의 줄은 위쪽 구분선을 갖고, 첫 줄의 선은 카드가 1px 잘라 숨긴다 (줄 목록이 바뀌어도 신경 쓸 것 없음).
        /// </summary>
        public static Border Card(Control child, Thickness? padding = null)
        {
            var clip = new Border { ClipToBounds = true, Child = new Border { Child = child, Margin = new Thickness(0, -1, 0, 0) } };
            var b = new Border
            {
                Child = clip, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = padding ?? new Thickness(16, 0), Margin = new Thickness(0, 0, 0, 12),
            };
            return b.Bg(KCard).Stroke(KCardStroke);
        }

        /// <summary>안내 막대 (주의).</summary>
        public static Border Notice(string text, string glyph = Glyph.Warning)
        {
            var p = new DockPanel();
            var i = Icon(glyph, 14, KCaution); i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 2, 10, 0);
            DockPanel.SetDock(i, Dock.Left);
            p.Children.Add(i);
            p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 }.Fg(KText));
            return new Border { Child = p, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 12) }.Bg(KCautionBg);
        }

        /// <summary>바뀐 값: 이름을 강조색으로.</summary>
        public static void MarkModified(TextBlock name, bool modified)
        {
            name.Fg(modified ? KAccentText : KText);
            name.FontWeight = modified ? FontWeight.SemiBold : FontWeight.Normal;
        }

        // ---------------- 설정 줄 ----------------
        /// <summary>카드 안 한 줄: 이름(왼쪽) · 되돌리기 · 값(오른쪽). 위쪽 구분선.</summary>
        public static Grid PropRow(TextBlock name, Control value, Control? reset, double nameRatio = 1)
        {
            var g = Columns(Star(nameRatio), GridLength.Auto, Star(1.35));
            g.MinHeight = RowHeight;
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(KDivider);
            Grid.SetColumnSpan(line, 3);
            g.Children.Add(line);
            name.Margin = new Thickness(0, 4, 8, 4);
            // 긴 이름은 두 줄까지 (한 줄 말줄임이면 "각도 · UV 애니메이…"처럼 뜻이 잘렸다). 더 길면 말줄임 + 툴팁
            name.TextWrapping = TextWrapping.Wrap; name.MaxLines = 2;
            if (ToolTip.GetTip(name) == null && name.Text is { Length: > 0 } nt) ToolTip.SetTip(name, nt);
            g.Children.Add(name.At(0));
            if (reset != null) { reset.Margin = new Thickness(0, 0, 6, 0); g.Children.Add(reset.At(1)); }
            value.VerticalAlignment = VerticalAlignment.Center; value.Margin = new Thickness(0, 6);
            g.Children.Add(value.At(2));
            return g;
        }

        /// <summary>상태가 붙은 줄 (이름 아래 작은 글씨 — 두 줄까지, 넘치면 말줄임과 툴팁).</summary>
        public static Grid DescRow(string name, string desc, Control value)
        {
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8) };
            label.Children.Add(Text(name));
            if (desc.Length > 0) label.Children.Add(Caption(desc));
            return Row(label, value);
        }

        /// <summary>DescRow인데 아래 글씨를 코드가 바꾼다 (예: 지금 자세 이름).</summary>
        public static Grid DescRow(string name, TextBlock desc, Control value)
        {
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8) };
            label.Children.Add(Text(name));
            desc.FontSize = 12; desc.Fg(KText2); desc.TextWrapping = TextWrapping.Wrap; desc.MaxLines = 2; desc.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Children.Add(desc);
            return Row(label, value);
        }

        /// <summary>설명은 도움말 단추로 숨긴 줄: 이름 ⓘ · 값.</summary>
        /// <param name="wide">슬라이더·목록처럼 넓게 쓰는 값: 이름과 값을 PropRow처럼 나눠 쓴다</param>
        public static Grid HelpRow(string name, string help, Control value, bool wide = false)
        {
            var t = Text(name);
            var label = Titled(t, help);
            label.Margin = new Thickness(0, 8);
            return Row(label, value, wide);
        }

        /// <summary>
        /// 목록처럼 넓은 값을 담는 줄: 이름(+도움말)이 위, 값은 아래 한 줄을 다 쓴다 (체크 상자 여러 개가 옆으로 넘치지 않게).
        /// </summary>
        public static StackPanel BlockRow(string name, string? help, Control content)
        {
            var p = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false, Margin = new Thickness(0, -10, 0, 9) }.Stroke(KDivider);
            p.Children.Add(line);
            p.Children.Add(Titled(Text(name), help));
            content.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(content);
            return p;
        }

        /// <summary>BlockRow인데 이름 글자·되돌리기 단추를 밖에서 준다 (바뀐 값 표시용): 이름 · ↺ 위, 값은 아래 한 줄.</summary>
        public static StackPanel BlockRow(TextBlock name, Control? reset, Control content)
        {
            var p = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false, Margin = new Thickness(0, -10, 0, 9) }.Stroke(KDivider);
            p.Children.Add(line);
            var head = new DockPanel();
            if (reset != null) { DockPanel.SetDock(reset, Dock.Right); head.Children.Add(reset); }
            name.TextWrapping = TextWrapping.Wrap;
            head.Children.Add(name);
            p.Children.Add(head);
            content.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(content);
            return p;
        }

        /// <summary>작은 보조 글씨: 두 줄까지 보이고, 넘치면 말줄임 + 툴팁으로 전체.</summary>
        public static TextBlock Caption(string s, string fg = KText2)
        {
            var d = Text(s, 12, FontWeight.Normal, fg);
            d.TextWrapping = TextWrapping.Wrap; d.MaxLines = 2; d.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(d, s);
            return d;
        }

        static Grid Row(Control label, Control value, bool wide = false)
        {
            var g = wide ? Columns(Star(), Star(1.35)) : Columns(Star(), GridLength.Auto);
            g.MinHeight = RowHeight;
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(KDivider);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line);
            g.Children.Add(label.At(0));
            value.VerticalAlignment = VerticalAlignment.Center; value.Margin = new Thickness(12, 6, 0, 6);
            // 값 칸이 이름을 밀어내지 않게: 값은 줄 너비의 60%까지 (넘치면 값 쪽이 줄어든다)
            if (!wide) g.SizeChanged += (_, e) => value.MaxWidth = Math.Max(120, e.NewSize.Width * 0.6);
            g.Children.Add(value.At(1));
            return g;
        }

        /// <summary>값 입력 칸: 첫 클릭은 전체 선택, Enter·포커스 이탈에 확정, Esc는 되돌림.</summary>
        public static TextBox Cell(Action<string> commit, Action? revert = null)
        {
            var t = new TextBox { MinHeight = 32, Padding = new Thickness(8, 5), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
            bool cancel = false;
            void Leave() => TopLevel.GetTopLevel(t)?.FocusManager?.ClearFocus();
            t.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { commit(t.Text ?? ""); Leave(); e.Handled = true; }
                else if (e.Key == Key.Escape) { cancel = true; Leave(); cancel = false; revert?.Invoke(); e.Handled = true; }
            };
            t.LostFocus += (_, __) => { if (!cancel) commit(t.Text ?? ""); };
            t.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (t.IsFocused) return;
                t.Focus(); t.SelectAll(); e.Handled = true;
            }, RoutingStrategies.Tunnel);
            return t;
        }

        /// <summary>숫자 칸 ("num" 클래스 — 길게 누르면 입력 바가 숫자 키로 뜬다, InputBar)</summary>
        public static TextBox NumberCell(Action<double> commit, Action? revert = null)
        {
            var t = Cell(s => { if (TryParse(s, out var v)) commit(v); else revert?.Invoke(); }, revert);
            t.Classes.Add("num");
            return t;
        }

        public static bool TryParseList(string s, int count, float[] current, out float[] result)
        {
            result = (float[])current.Clone();
            if (result.Length < count) Array.Resize(ref result, count);
            var parts = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > count) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParse(parts[i], out var v)) return false;
                result[i] = (float)v;
            }
            return true;
        }

        public static string FormatList(float[] c, int count)
        {
            var parts = new string[count];
            for (int i = 0; i < count; i++) parts[i] = F(i < c.Length ? c[i] : 0);
            return string.Join(", ", parts);
        }

        public static ComboBox CellCombo(ComboBox c)
        {
            c.HorizontalAlignment = HorizontalAlignment.Stretch; c.FontSize = 13; c.MinHeight = 32;
            // 좁은 작업 창에서 고른 값이 잘려도 마우스를 올리면 전부 보이게 (2026-11-01). 따로 단 도움말이 있으면 그것을 둔다
            string? ours = null;
            c.SelectionChanged += (_, __) =>
            {
                var now = ToolTip.GetTip(c);
                if (now != null && !Equals(now, ours)) return;
                ours = c.SelectedItem is ComboBoxItem i ? i.Content?.ToString() : c.SelectedItem?.ToString();
                ToolTip.SetTip(c, ours);
            };
            return c;
        }

        /// <summary>켬/끔 스위치. 코드에서 값을 맞출 때는 SetQuiet (변경 이벤트 없음).</summary>
        public sealed class Switch
        {
            public readonly ToggleSwitch Control;
            bool _quiet;

            public Switch(Action<bool> changed)
            {
                Control = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
                Control.IsCheckedChanged += (_, __) => { if (!_quiet) changed(Control.IsChecked == true); };
            }

            public void SetQuiet(bool v)
            {
                _quiet = true;
                try { Control.IsChecked = v; } finally { _quiet = false; }
            }
        }

        /// <summary>
        /// 누르고 끄는 동안은 preview, 놓을 때 commit (드래그 하나 = Undo 한 단계). 키보드 조작은 바로 commit.
        /// Avalonia Slider는 트랙을 눌러도 드래그가 시작되므로 Thumb 이벤트 대신 포인터 누름/뗌으로 구분한다.
        /// </summary>
        public sealed class EditSlider
        {
            public readonly Slider Slider;
            bool _pressed, _suppress;
            double _start;
            readonly Action<double> _preview, _commit;
            /// <summary>떠 있는 다이얼 프리셋에 저장할 수 있게: 값의 종류("shape"·"muscle")와 열쇠 (Ui.FineResolvers가 다시 만든다)</summary>
            public string Kind = "", Key = "";

            public EditSlider(double min, double max, Action<double> preview, Action<double> commit)
            {
                _preview = preview; _commit = commit;
                Slider = new Slider { Minimum = min, Maximum = max, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, Margin = new Thickness(10, -8, 2, -8) };
                Edits.AddOrUpdate(Slider, this);
                Slider.Classes.Add("vp");   // 설정 "슬라이더 모양 → 다이얼"이면 다이얼 템플릿 (Dial.UseForSliders)
                Slider.AddHandler(InputElement.PointerPressedEvent, (_, __) => { _pressed = true; _start = Slider.Value; OfferFine(Fine()); }, RoutingStrategies.Tunnel, handledEventsToo: true);
                Slider.GotFocus += (_, __) => OfferFine(Fine());
                void Release()
                {
                    if (!_pressed) return;
                    _pressed = false;
                    if (Math.Abs(Slider.Value - _start) > 1e-9) commit(Slider.Value);
                }
                Slider.AddHandler(InputElement.PointerReleasedEvent, (_, __) => Release(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                Slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, __) => Release(), RoutingStrategies.Bubble, handledEventsToo: true);
                Slider.ValueChanged += (_, e) =>
                {
                    if (_suppress) return;
                    // 사용자 조작만 편집으로 친다: 원본 값이 범위 밖이면 Slider가 스스로 값을 범위 안으로 당기는데
                    // (예: 0인데 최솟값 0.001) 그것까지 기록하면 열자마자 '변경됨'이 된다
                    if (_pressed) preview(e.NewValue);
                    else if (Slider.IsKeyboardFocusWithin) commit(e.NewValue);
                };
            }

            public void SetQuiet(double v)
            {
                if (_pressed) return;
                _suppress = true;
                try { Slider.Value = v; } finally { _suppress = false; }
            }

            /// <summary>세밀 조정 막대가 이 슬라이더를 움직일 때: 슬라이더 모양도 같이 맞추고 preview/commit은 막대가 부른다</summary>
            public FineTarget Fine()
            {
                double range = Slider.Maximum - Slider.Minimum;
                double step = range > 0 ? Math.Pow(10, Math.Floor(Math.Log10(range / 100))) : 0.01;
                void Show(double v) { _suppress = true; try { Slider.Value = v; } finally { _suppress = false; } }
                return new FineTarget
                {
                    Name = GuessName(Slider), Get = () => Slider.Value, Min = Slider.Minimum, Max = Slider.Maximum, Step = step,
                    IsInt = Slider.IsSnapToTickEnabled && Slider.TickFrequency >= 1, Source = Slider, Kind = Kind, Key = Key,
                    Preview = v => { Show(v); _preview(v); }, Commit = v => { Show(v); _commit(v); },
                };
            }
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slider, EditSlider> Edits = new();
        public static EditSlider? EditOf(Slider s) => Edits.TryGetValue(s, out var e) ? e : null;

        // ---------------- 세밀 조정 막대 (FineRail.cs) ----------------
        /// <summary>오른쪽 세밀 조정 막대에 넘기는 값 하나: 슬라이더를 누르거나 숫자 이름을 누르면 그 값이 막대로 간다.</summary>
        public sealed class FineTarget
        {
            public string Name = "값";
            public Func<double> Get = () => 0;
            public Action<double> Preview = _ => { }, Commit = _ => { };
            public double Min = double.NegativeInfinity, Max = double.PositiveInfinity, Step = 0.01;
            public bool IsInt;
            public Control? Source;
            /// <summary>프리셋으로 저장해 다시 열 수 있는 값이면 종류와 열쇠 (빈 값 = 저장 안 됨)</summary>
            public string Kind = "", Key = "";
            public bool Bounded => !double.IsInfinity(Min) && !double.IsInfinity(Max) && Max > Min;
        }
        public static event Action<FineTarget>? FineTargeted;
        /// <summary>종류마다 열쇠 → 값 (떠 있는 다이얼 프리셋을 다시 열 때). BlendShapePanel("shape")·PosePanel("muscle")이 등록한다</summary>
        public static readonly System.Collections.Generic.Dictionary<string, Func<string, FineTarget?>> FineResolvers = new();
        public static FineTarget? ResolveFine(string kind, string key) => FineResolvers.TryGetValue(kind, out var f) ? f(key) : null;
        public static void OfferFine(FineTarget t) => FineTargeted?.Invoke(t);

        /// <summary>컨트롤이 놓인 줄의 이름 글자 (PropRow·DescRow·BlockRow의 왼쪽/위 글자). 못 찾으면 "값"</summary>
        public static string GuessName(Control c)
        {
            Control? child = c;
            for (var p = c.Parent as Control; p != null && child != null; child = p, p = p.Parent as Control)
            {
                if (p is not Panel panel) continue;
                foreach (var k in panel.Children)
                {
                    if (k == child) continue;
                    var tb = k as TextBlock ?? (k as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
                    if (tb?.Text is { Length: > 0 } s && !TryParse(s, out _) && tb.FontFamily != IconFont) return s;
                }
                if (p is Window) break;
            }
            return "값";
        }

        /// <summary>
        /// 이름 글자를 좌우로 끌어 숫자 바꾸기 (Unity·Blender처럼). 1px = step, Shift는 10배 느리게, Ctrl은 10배 빠르게.
        /// 끄는 동안은 preview, 놓을 때 commit 한 번 (실행 취소 한 단계). 3px 안 움직이면 아무것도 하지 않는다 (툴팁·클릭 그대로).
        /// </summary>
        public static void Scrub(Control handle, Func<double> get, Action<double> preview, Action<double> commit, double step,
            double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool isInt = false)
        {
            handle.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            if (handle is TextBlock tb) tb.Background = Brushes.Transparent;   // 글자 사이 빈 곳도 잡히게
            int digits = isInt ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step)) + 1, 0, 6);
            bool down = false, moved = false;
            double start = 0, startX = 0, last = 0;
            handle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                OfferFine(new FineTarget { Name = handle is TextBlock nt && nt.Text is { Length: > 0 } ? nt.Text : GuessName(handle), Get = get, Preview = preview, Commit = commit, Step = step, Min = min, Max = max, IsInt = isInt, Source = handle });
                down = true; moved = false; start = last = get(); startX = e.GetPosition(null).X;
                e.Pointer.Capture(handle);
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (!down) return;
                var dx = e.GetPosition(null).X - startX;
                if (!moved && Math.Abs(dx) < 3) return;
                moved = true;
                var s = step * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1) * ((e.KeyModifiers & KeyModifiers.Control) != 0 ? 10 : 1);
                var v = Math.Clamp(start + dx * s, min, max);
                v = isInt ? Math.Round(v) : Math.Round(v, digits);
                if (v == last) return;
                last = v;
                preview(v);
            };
            void End(bool commitIt)
            {
                if (!down) return;
                down = false;
                if (commitIt && moved && last != start) commit(last);
            }
            handle.PointerReleased += (_, e) => { End(true); e.Pointer.Capture(null); };
            handle.PointerCaptureLost += (_, __) => End(true);
            if (ToolTip.GetTip(handle) is string t && !t.Contains("좌우로 끌면")) ToolTip.SetTip(handle, t + "\n이름을 좌우로 끌면 값이 변경됩니다 (Shift: 천천히)");
        }

        /// <summary>
        /// 여러 숫자(벡터)를 칸마다 따로: 칸 위에 작은 이름(끌어서 바꾸기). "0.07, 0, 0.685, 1.394"처럼 쉼표로 한 칸에 쓰면
        /// 어느 숫자가 무엇인지 몰라 고치기 어려웠다. commit/preview는 전체 배열을 받는다. Refresh로 값을 다시 읽는다.
        /// </summary>
        public sealed class VectorCells
        {
            public readonly Grid Control;
            readonly TextBox[] _boxes;
            readonly Func<float[]> _get;

            public VectorCells(string[] captions, Func<float[]> get, Action<float[]> preview, Action<float[]> commit, double step = 0.01, Func<float[], float[]>? clamp = null)
            {
                _get = get;
                int n = captions.Length;
                Control = new Grid { ColumnSpacing = 4 };
                _boxes = new TextBox[n];
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    Control.ColumnDefinitions.Add(new ColumnDefinition(Star()));
                    float[] With(double v) { var c = Cur(); c[idx] = (float)v; return clamp != null ? clamp(c) : c; }
                    TextBox box = null!;
                    box = NumberCell(v => { var c = With(v); if (!c.SequenceEqual(Cur())) commit(c); else Refresh(); }, Refresh);
                    box.MinWidth = 0; box.Padding = new Thickness(6, 4); box.MinHeight = 28;
                    var cap = Text(captions[i], 11, FontWeight.Normal, KText3);
                    cap.Margin = new Thickness(2, 0, 0, 2); cap.HorizontalAlignment = HorizontalAlignment.Stretch;
                    ToolTip.SetTip(cap, captions[i]); ToolTip.SetTip(box, captions[i]);
                    Scrub(cap, () => Cur()[idx], v => { var c = With(v); box.Text = F(c[idx]); preview(c); }, v => commit(With(v)), step);
                    var col = new StackPanel();
                    col.Children.Add(cap); col.Children.Add(box);
                    Grid.SetColumn(col, i);
                    Control.Children.Add(col);
                    _boxes[i] = box;
                }
                Refresh();
            }

            float[] Cur()
            {
                var c = (float[])_get().Clone();
                if (c.Length < _boxes.Length) Array.Resize(ref c, _boxes.Length);
                return c;
            }

            public void Refresh()
            {
                var c = Cur();
                for (int i = 0; i < _boxes.Length; i++) if (!_boxes[i].IsFocused) _boxes[i].Text = F(c[i]);
            }
        }

        /// <summary>숫자 칸 + 슬라이더 한 쌍.</summary>
        public static Grid NumberWithSlider(TextBox box, Slider slider, double boxWidth = 64)
        {
            var g = Columns(Px(boxWidth), Star());
            g.Children.Add(box.At(0)); g.Children.Add(slider.At(1));
            box.GotFocus += (_, __) => { if (EditOf(slider) is { } es) OfferFine(es.Fine()); };   // 숫자 칸을 눌러도 세밀 조정 막대로
            return g;
        }

        /// <summary>
        /// 접는 페이지 구역 (UI 정리, 2026-10-19): 구역 제목(SectionTitle 모양) 앞에 화살표, 접혀 있으면 제목 옆에 요약(hint).
        /// 자주 쓰지 않는 구역(머슬 하나씩·애니메이션·저장한 자세·메시 하나씩)을 처음엔 접어 두어 페이지를 짧게 한다. 내용은 Body에 넣는다.
        /// </summary>
        public sealed class FoldSection : StackPanel
        {
            readonly TextBlock _chevron = Icon(Glyph.ChevronRight, 10, KText2);
            readonly TextBlock _hint = Text("", 12, FontWeight.Normal, KText3);
            bool _open;
            public readonly StackPanel Body = new StackPanel();
            public event Action<bool>? Toggled;

            public readonly string TitleText;
            string? _helpText;

            /// <summary>
            /// 접지 않는 모양으로 바꿔 돌려준다 (페이지 안 탭 하나로 쓸 때): 구역 제목(+도움말) 아래 내용. 이 FoldSection은 비워진다.
            /// </summary>
            public Control Flatten()
            {
                if (_right != null) (_right.Parent as Panel)?.Children.Remove(_right);
                Children.Clear();
                Body.IsVisible = true;
                var p = new StackPanel();
                var head = new DockPanel();
                if (_right != null) { DockPanel.SetDock(_right, Dock.Right); _right.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(_right); }
                head.Children.Add(SectionTitle(TitleText, _helpText));
                p.Children.Add(head);
                p.Children.Add(Body);
                return p;
            }
            readonly Control? _right;

            public FoldSection(string title, bool open, Control? right = null, string? help = null)
            {
                TitleText = title; _helpText = help; _right = right;
                var head = new DockPanel { MinHeight = 36, Background = Brushes.Transparent, Margin = new Thickness(0, 8, 0, 4) };
                _chevron.Width = 20; _chevron.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(_chevron, Dock.Left);
                head.Children.Add(_chevron);
                if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); head.Children.Add(right); }
                var t = Text(title, 15, FontWeight.SemiBold); t.VerticalAlignment = VerticalAlignment.Center;
                _hint.VerticalAlignment = VerticalAlignment.Center; _hint.Margin = new Thickness(10, 0, 8, 0);
                // 요약은 남는 너비 안에서 말줄임 (오른쪽 단추 밑으로 겹치지 않게)
                var row = new DockPanel();
                // 제목도 좁으면 말줄임 (오른쪽 단추가 넓으면 제목 위로 겹쳐 그려지던 것). ⓘ는 제목 바로 뒤
                var titled = new DockPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                _help = Help(help ?? "");
                _help.IsVisible = !string.IsNullOrEmpty(help);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                DockPanel.SetDock(_help, Dock.Right);
                titled.Children.Add(_help); titled.Children.Add(t);
                DockPanel.SetDock(titled, Dock.Left);
                row.Children.Add(titled); row.Children.Add(_hint);
                head.Children.Add(row);
                head.Cursor = new Cursor(StandardCursorType.Hand);
                // 오른쪽 단추는 자기 클릭을 처리하므로(Handled) 여기서 접힘이 바뀌지 않는다
                head.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !e.Handled) { IsOpen = !IsOpen; e.Handled = true; } };
                Children.Add(head);
                Children.Add(Body);
                _open = !open; IsOpen = open;
            }

            readonly Button _help;

            /// <summary>제목 옆 도움말 (ⓘ). null이면 숨긴다.</summary>
            public string? HelpText
            {
                set { _helpText = value; _help.IsVisible = !string.IsNullOrEmpty(value); if (!string.IsNullOrEmpty(value)) SetHelp(_help, value!); }
            }

            /// <summary>제목 옆 요약 (접혀 있을 때만 보인다). 예: "95개"</summary>
            public string Hint { get => _hint.Text ?? ""; set => _hint.Text = value; }

            public bool IsOpen
            {
                get => _open;
                set
                {
                    if (_open == value) return;
                    _open = value;
                    Body.IsVisible = value;
                    _hint.IsVisible = !value;
                    _chevron.Text = value ? Glyph.ChevronDown : Glyph.ChevronRight;
                    Toggled?.Invoke(value);
                }
            }
        }

        /// <summary>접는 구역: 제목 줄(왼쪽 화살표 · 제목 · 오른쪽 부가 컨트롤) + 카드.</summary>
        public sealed class Fold : DockPanel
        {
            readonly TextBlock _chevron = Icon(Glyph.ChevronRight, 10, KText2);
            readonly Control _body;
            bool _open;
            public event Action? Expanded, Collapsed;

            public Fold(Control header, Control body, bool open, Control? right = null)
            {
                _body = Card(body);
                var head = new DockPanel { MinHeight = 36, Background = Brushes.Transparent };
                _chevron.Width = 24;
                SetDock(_chevron, Dock.Left);
                head.Children.Add(_chevron);
                if (right != null) { SetDock(right, Dock.Right); head.Children.Add(right); }
                head.Children.Add(header);
                var bar = new Border { Child = head, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 0, 8, 0), Margin = new Thickness(0, 0, 0, 4), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
                bar.PointerEntered += (_, __) => bar.Bg(KHover);
                bar.PointerExited += (_, __) => bar.Background = Brushes.Transparent;
                // 머리글 오른쪽 스위치는 자기 클릭을 처리하므로(Handled) 여기서 접힘이 바뀌지 않는다
                bar.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { IsExpanded = !IsExpanded; e.Handled = true; } };
                SetDock(bar, Dock.Top);
                Children.Add(bar);
                Children.Add(_body);
                _open = !open; IsExpanded = open;
            }

            public bool IsExpanded
            {
                get => _open;
                set
                {
                    if (_open == value) return;
                    _open = value;
                    _body.IsVisible = value;
                    _chevron.Text = value ? Glyph.ChevronDown : Glyph.ChevronRight;
                    if (value) Expanded?.Invoke(); else Collapsed?.Invoke();
                }
            }
        }

        /// <summary>검색 칸 + 보기 옵션: 검색은 한 줄을 다 쓰고 옵션은 그 아래 (좁은 작업 창에서 검색 칸이 짓눌리지 않게).</summary>
        public static StackPanel SearchRow(TextBox search, params Control[] options)
        {
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(search);
            var opts = new WrapPanel { Margin = new Thickness(2, 0, 0, 0) };
            foreach (var o in options) { o.Margin = new Thickness(0, 0, 16, 0); opts.Children.Add(o); }
            if (opts.Children.Count > 0) p.Children.Add(opts);
            return p;
        }

        public static TextBox SearchBox(string placeholder)
        {
            var i = Icon(Glyph.Search, 13, KText3); i.Margin = new Thickness(10, 0, 0, 0);
            return new TextBox { Watermark = placeholder, MinHeight = 34, Padding = new Thickness(8, 6), InnerLeftContent = i, VerticalContentAlignment = VerticalAlignment.Center };
        }

        public static Grid Columns(params GridLength[] cols)
        {
            var g = new Grid();
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition(c));
            return g;
        }

        public static T At<T>(this T e, int col, int row = 0) where T : Control
        {
            Grid.SetColumn(e, col); Grid.SetRow(e, row);
            return e;
        }

        public static GridLength Px(double v) => new GridLength(v);
        public static GridLength Star(double v = 1) => new GridLength(v, GridUnitType.Star);

        public static Color ToColor(float[] c)
        {
            byte B(float x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
            return Color.FromArgb(B(c.Length > 3 ? c[3] : 1), B(c[0]), B(c[1]), B(c[2]));
        }
    }

    /// <summary>Segoe Fluent Icons 글리프.</summary>
    public static class Glyph
    {
        public const string Scene = "\uE80A", Material = "\uE790", Shapes = "\uE9E9", Environment = "\uE706", Camera = "\uE722", Log = "\uE9D9";
        public const string Import = "\uE8B5", Open = "\uE8E5", Save = "\uE74E", SaveAs = "\uE792", Undo = "\uE7A7", Redo = "\uE7A6", Refresh = "\uE72C", Reset = "\uE777";
        public const string Copy = "\uE8C8", Paste = "\uE77F", Search = "\uE721", Person = "\uE77B", Contact = "\uE8D4", Front = "\uE7B3", Side = "\uE8AB", Back = "\uE72B";
        public const string Connected = "\uE703", Disconnected = "\uEB5E", Folder = "\uE838", Document = "\uE8A5", Warning = "\uE7BA", Info = "\uE946";
        public const string ChevronUp = "\uE70E", Hide = "\uED1A", Headset = "\uE7F6";
        public const string ChevronDown = "\uE70D", ChevronRight = "\uE76C", ClosePane = "\uE89F", OpenPane = "\uE8A0", DockLeft = "\uE90C", DockRight = "\uE90D", View = "\uE890", Physics = "\uE945", Mirror = "\uE8B9", Avatar = "\uE77B", Pose = "\uE805";
        public const string Add = "\uE710", Delete = "\uE74D", Pin = "\uE718", Place = "\uE81D", Move = "\uE7C2", Emoji = "\uE76E",Optimize = "\uE9F5", Edit = "\uE70F", Upload = "\uE898", Record = "\uE7C8", Stop = "\uE71A", Play = "\uE768", More = "\uE712", Keyboard = "\uE765", Settings = "\uE713";
        public const string Unpin = "\uE77A", Close = "\uE711", PopOut = "\uE8A7", DockBack = "\uE73F", Package = "\uE7B8", Link = "\uE71B";
    }
}
