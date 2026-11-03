// 머티리얼 페이지 "색 조화" 탭 (2026-10-24 사용자 요청):
//   · 색상환 위의 기본 색 + 조화 규칙(유사·단색·삼각·보색·분할 보색·정사각형·직사각형·음영)으로 어울리는 색 묶음. 아무 점이나 끌면 묶음째 돈다.
//   · 기본 색 정하기: 색상환 · 색 코드 · 화면 스포이드(ScreenPicker) · 이미지/메인 텍스처에서 누르기 · 이미지 대표 색.
//   · 고른 색은 복사하거나 지금 머티리얼의 색 프로퍼티에 넣는다 (실행 취소 가능). 계산은 Core의 ColorHarmony.
// 이 탭은 머티리얼을 다시 그려도 그대로 두는 한 개짜리 (고른 규칙·색·이미지가 머티리얼을 바꿔도 남는다).
// 그림(색상환·규칙 단추·견본)은 ColorHarmonyLook.cs — 보통 / 다이얼 모드 플랫 / 입체에 맞춰 그린다. 밝기 슬라이더는 "vp"라 다이얼 모드에서 다이얼이 된다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed class ColorHarmonyPanel : StackPanel
    {
        /// <summary>지금 머티리얼의 색 프로퍼티 (이름, 보이는 이름). 머티리얼이 없으면 빈 목록.</summary>
        public Func<IReadOnlyList<(string Name, string Label)>>? ColorProps;
        public Func<string, float[]?>? ReadProp;
        /// <summary>(프로퍼티 이름, 보이는 이름, RGB 0~1) — 알파는 머티리얼 값 그대로 둔다.</summary>
        public Action<string, string, float[]>? WriteProp;
        /// <summary>지금 머티리얼의 메인 텍스처 파일 (없으면 null).</summary>
        public Func<string?>? MainTextureFile;

        readonly Action<string> _status;
        HarmonyRule _rule = HarmonyRule.Analogous;
        Hsv _base = new Hsv(200, 0.6, 0.9);
        int _sel, _drag = -1;
        Hsv[] _set = Array.Empty<Hsv>();

        readonly HarmonyWheel _wheel = new HarmonyWheel { Margin = new Thickness(0, 12, 20, 12), VerticalAlignment = VerticalAlignment.Top };
        readonly Slider _value = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        readonly Border _valueBar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 2) };   // 밝기 길 (보통 모양에서만)
        readonly TextBlock _valueText = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly TextBlock _ruleName = Ui.Text("", 13, FontWeight.SemiBold);
        readonly WrapPanel _ruleChips = new WrapPanel();
        readonly List<RuleChip> _chips = new List<RuleChip>();
        readonly Grid _swatches = new Grid { MinHeight = 76 };
        readonly TextBox _hex;
        readonly ComboBox _prop = Ui.CellCombo(new ComboBox { PlaceholderText = "색 프로퍼티" });
        readonly Image _image = new Image { Stretch = Stretch.Uniform, MaxHeight = 240, Cursor = new Cursor(StandardCursorType.Cross) };
        readonly Border _imageFrame = new Border { HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, IsVisible = false };
        readonly TextBlock _imageNote = Ui.Wrap("이미지를 열면 누른 곳의 색을 기본 색으로 삼고, 많이 쓰인 색을 아래에 뽑아요.");
        readonly Grid _dominant = new Grid { MinHeight = 0 };
        byte[]? _px; int _pxW, _pxH;
        bool _quiet;

        public ColorHarmonyPanel(Action<string> status)
        {
            _status = status;
            _hex = Ui.Cell(s => { if (ColorHarmony.ParseHex(s) is Hsv c) SetBase(c); else Show(); }, Show);
            _hex.Width = 110; _hex.FontFamily = new FontFamily("Consolas");
            ToolTip.SetTip(_hex, "기본 색 코드 (#RRGGBB)");

            // ---- 색상환 카드: 왼쪽 색상환, 오른쪽 규칙·밝기·기본 색 ----
            _wheel.PointerPressed += WheelPressed;
            _wheel.PointerMoved += (_, e) => { if (_drag >= 0) WheelDrag(e.GetPosition(_wheel)); };
            _wheel.PointerReleased += (_, __) => _drag = -1;
            _wheel.PointerCaptureLost += (_, __) => _drag = -1;

            var side = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12), MinWidth = 220 };
            var ruleHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            ruleHead.Children.Add(Ui.Text("색 조화:", 13, FontWeight.Normal, Ui.KText2)); ruleHead.Children.Add(_ruleName);
            side.Children.Add(ruleHead);
            foreach (var r in ColorHarmony.Rules)
            {
                var chip = new RuleChip(r);
                ToolTip.SetTip(chip, ColorHarmony.Name(r));
                chip.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { _rule = chip.Rule; _sel = 0; Show(); } };
                _chips.Add(chip); _ruleChips.Children.Add(chip);
            }
            side.Children.Add(_ruleChips);
            _value.Classes.Add("vp");   // 다이얼 모드에서는 다이얼 (Dial.UseForSliders)
            var valueCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            valueCol.Children.Add(_value); valueCol.Children.Add(_valueBar);
            var vRow = Ui.Columns(GridLength.Auto, Ui.Star(), Ui.Px(44));   // 이름 칸은 글 길이대로 (영어 "Brightness", 2026-11-02)
            _valueText.HorizontalAlignment = HorizontalAlignment.Right;
            var vLabel = Ui.Text("밝기", 13); vLabel.MinWidth = 44; vLabel.Margin = new Thickness(0, 0, 8, 0); vRow.Children.Add(vLabel.At(0)); vRow.Children.Add(valueCol.At(1)); vRow.Children.Add(_valueText.At(2));
            vRow.Margin = new Thickness(0, 6, 0, 0);
            side.Children.Add(vRow);
            _value.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_quiet) SetBase(new Hsv(_base.H, _base.S, _value.Value)); };
            var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            hexRow.Children.Add(Ui.Text("기본 색", 13)); hexRow.Children.Add(_hex);
            hexRow.Children.Add(Ui.Button("스포이드", Eyedropper, "화면 어디서든 색을 집어 기본 색으로 (3D 미리보기도 돼요). 클릭: 고르기 · 오른쪽 클릭·Esc: 그만"));
            side.Children.Add(hexRow);
            side.Children.Add(Ui.Wrap("색상환의 점을 끌면 묶음째 돌아요. 빈 곳을 누르면 기본 색이 그리로 가요.", Ui.KText3, 12));

            var top = new WrapPanel();
            top.Children.Add(_wheel); top.Children.Add(side);
            var wheelCard = new StackPanel();
            wheelCard.Children.Add(top);
            wheelCard.Children.Add(_swatches);

            // 고른 색으로 할 일: 복사 · 머티리얼 색 프로퍼티에 넣기/가져오기
            var act = new WrapPanel { Margin = new Thickness(0, 10, 0, 12) };
            act.Children.Add(Ui.Button("색 코드 복사", () => Copy(ColorHarmony.Hex(_set[_sel])), "고른 색의 #RRGGBB를 복사", Glyph.Copy));
            act.Children.Add(Ui.Button("모두 복사", () => Copy(string.Join(" ", _set.Select(ColorHarmony.Hex))), "묶음의 색 코드를 모두 복사"));
            _prop.MinWidth = 180; _prop.Margin = new Thickness(8, 0, 8, 0);
            _prop.DropDownOpened += (_, __) => FillProps();
            act.Children.Add(_prop);
            act.Children.Add(Ui.Button("넣기", ApplyToProp, "고른 색을 지금 머티리얼의 이 프로퍼티에 넣어요 (불투명도는 그대로, 실행 취소 가능)", accent: true));
            act.Children.Add(Ui.Button("가져오기", TakeFromProp, "이 프로퍼티의 색을 기본 색으로"));
            wheelCard.Children.Add(act);
            Children.Add(Ui.Card(wheelCard));

            // ---- 이미지에서 뽑기 ----
            var pick = new StackPanel { Margin = new Thickness(0, 12) };
            var btns = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            btns.Children.Add(Ui.Button("이미지 열기", OpenImage, "PNG·JPG 이미지에서 색 뽑기", Glyph.Open));
            btns.Children.Add(Ui.Button("메인 텍스처", () => LoadImage(MainTextureFile?.Invoke(), "메인 텍스처"), "지금 머티리얼의 메인 텍스처에서 색 뽑기"));
            pick.Children.Add(btns);
            pick.Children.Add(_imageNote);
            _image.PointerPressed += (_, e) => { e.Pointer.Capture(_image); PickImage(e.GetPosition(_image)); };
            _image.PointerMoved += (_, e) => { if (e.Pointer.Captured == _image) PickImage(e.GetPosition(_image)); };
            _image.PointerReleased += (_, e) => e.Pointer.Capture(null);
            ToolTip.SetTip(_image, "누르거나 끌어서 그 자리 색을 기본 색으로");
            _imageFrame.Child = _image;
            pick.Children.Add(_imageFrame);
            _dominant.Margin = new Thickness(0, 10, 0, 0);
            pick.Children.Add(_dominant);
            Children.Add(Ui.SectionTitle("이미지에서 뽑기"));
            Children.Add(Ui.Card(pick));

            // 조작 모양(다이얼 모드·플랫/입체)·테마가 바뀌면 그림과 틀을 다시
            Hardware.Changed += Restyle;
            ActualThemeVariantChanged += (_, __) => Restyle();
            Restyle();
            Show();
        }

        // ---------------- 상태 ----------------
        void SetBase(Hsv c) { _base = c; Show(); }

        void Show()
        {
            _set = ColorHarmony.Make(_rule, _base);
            if (_sel >= _set.Length) _sel = 0;
            _ruleName.Text = ColorHarmony.Name(_rule);
            foreach (var chip in _chips) chip.Selected = chip.Rule == _rule;
            _quiet = true; _value.Value = _base.V; _quiet = false;
            _valueText.Text = $"{_base.V * 100:0}%";
            _valueBar.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(HarmonyLook.ToColor(new Hsv(_base.H, _base.S, 1)), 1) },
            };
            if (!_hex.IsFocused) _hex.Text = ColorHarmony.Hex(_base);
            _wheel.Set = _set; _wheel.Sel = _sel;
            _wheel.SetValue(_base.V);
            _wheel.InvalidateVisual();
            DrawSwatches();
        }

        /// <summary>모양 바꿈: 다이얼 모드에서는 밝기 길을 숨기고(다이얼이 대신), 이미지는 움푹한 화면 틀 안에.</summary>
        void Restyle()
        {
            bool hw = Hardware.On;
            _valueBar.IsVisible = !hw;
            // 밝기 숫자: 슬라이더면 줄 끝, 다이얼(34px)이면 다이얼 바로 옆
            Grid.SetColumn(_valueText, hw ? 1 : 2);
            _valueText.HorizontalAlignment = hw ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _valueText.Margin = new Thickness(hw ? 44 : 0, 0, 0, 0);
            if (hw) _valueText.FontFamily = new FontFamily("Consolas"); else _valueText.ClearValue(TextBlock.FontFamilyProperty);
            if (hw)
            {
                var inset = HarmonyLook.Inset(this);
                _imageFrame.Background = new SolidColorBrush(inset);
                _imageFrame.BorderBrush = new SolidColorBrush(UiTheme.Lerp(inset, Colors.Black, 0.35));
                _imageFrame.BorderThickness = new Thickness(1); _imageFrame.Padding = new Thickness(4);
                _imageFrame.CornerRadius = new CornerRadius(Hardware.Flat ? 4 : 5);
            }
            else
            {
                _imageFrame.Background = Brushes.Transparent;
                _imageFrame.BorderBrush = new SolidColorBrush(HarmonyLook.Res(this, Ui.KCardStroke, Colors.Gray));
                _imageFrame.BorderThickness = new Thickness(1); _imageFrame.Padding = new Thickness(0);
                _imageFrame.CornerRadius = new CornerRadius(6);
            }
            _wheel.InvalidateVisual();
            foreach (var c in _chips) c.InvalidateVisual();
            DrawSwatches();
            foreach (var f in _dominant.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<SwatchFace>())) f.InvalidateVisual();
        }

        // ---------------- 색상환 ----------------
        void WheelPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_wheel).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(_wheel);
            // 가장 가까운 점 (기본 색이 겹치면 기본 색 먼저). 점 위가 아니면 기본 색을 그리로
            _drag = 0; double best = 16;
            for (int i = 0; i < _set.Length; i++)
            {
                var q = _wheel.PosOf(_set[i]);
                double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                if (d < best - (i == 0 ? 0 : 0.01)) { best = d; _drag = i; }
            }
            if (best >= 16) _drag = 0;
            _sel = _drag;
            e.Pointer.Capture(_wheel);
            WheelDrag(p);
        }

        void WheelDrag(Point p)
        {
            var (h, s) = _wheel.HueSatAt(p);
            SetBase(ColorHarmony.DragTo(_rule, _base, _drag, h, s));
        }

        // ---------------- 견본 ----------------
        void DrawSwatches()
        {
            _swatches.Children.Clear(); _swatches.ColumnDefinitions.Clear();
            for (int i = 0; i < _set.Length; i++)
            {
                int idx = i;
                bool sel = i == _sel;
                _swatches.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
                var sw = new SwatchFace { Height = 54, Fill = HarmonyLook.ToColor(_set[i]), Selected = sel };
                // 다이얼 모드: 색 코드는 장비 표시창처럼 고정폭 글꼴, 고른 칸은 하이라이트 색
                var label = Ui.Text(ColorHarmony.Hex(_set[i]) + (i == 0 ? " · 기본" : ""), 12, sel ? FontWeight.SemiBold : FontWeight.Normal,
                    sel ? (Hardware.On ? Ui.KDial : Ui.KAccentText) : Ui.KText2);
                if (Hardware.On) label.FontFamily = new FontFamily("Consolas");
                label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 5, 0, 0);
                cell.Children.Add(sw); cell.Children.Add(label);
                ToolTip.SetTip(cell, "눌러서 고르기 (복사·넣기에 쓰여요) · 두 번 누르면 이 색을 기본 색으로");
                cell.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { _sel = idx; Show(); } };
                cell.DoubleTapped += (_, __) => { var c = _set[idx]; _sel = 0; SetBase(c); };
                _swatches.Children.Add(cell.At(i));
            }
        }

        async void Copy(string text)
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb == null) return;
            await cb.SetTextAsync(text);
            _status("복사했습니다: " + text);
        }

        // ---------------- 머티리얼 ----------------
        /// <summary>머티리얼이 바뀌면 (MaterialPanel.Rebuild) 프로퍼티 목록을 다시 채운다 — 고른 프로퍼티 이름이 새 머티리얼에도 있으면 그대로.</summary>
        public void MaterialChanged() => FillProps();

        void FillProps()
        {
            var keep = (_prop.SelectedItem as ComboBoxItem)?.Tag as string ?? "_Color";
            _prop.Items.Clear();
            foreach (var (name, label) in ColorProps?.Invoke() ?? Array.Empty<(string, string)>())
            {
                var it = new ComboBoxItem { Content = label, Tag = name };
                ToolTip.SetTip(it, name);
                _prop.Items.Add(it);
                if (name == keep) _prop.SelectedItem = it;
            }
            if (_prop.SelectedItem == null && _prop.Items.Count > 0) _prop.SelectedIndex = 0;
            _prop.IsEnabled = _prop.Items.Count > 0;
        }

        (string Name, string Label)? ChosenProp()
        {
            if (_prop.Items.Count == 0) FillProps();
            if (_prop.SelectedItem is ComboBoxItem { Tag: string n } it) return (n, it.Content as string ?? n);
            _status("머티리얼을 고르면 색 프로퍼티에 넣을 수 있어요.");
            return null;
        }

        void ApplyToProp()
        {
            if (ChosenProp() is not var (name, label) || WriteProp == null) return;
            var (r, g, b) = ColorHarmony.ToRgb(_set[_sel]);
            // 색 코드와 같은 값이 들어가게 8비트로 맞춘다
            float Q(double x) => (float)Math.Round(Math.Round(x * 255) / 255, 4);
            WriteProp(name, label, new[] { Q(r), Q(g), Q(b) });
            _status($"{label}에 {ColorHarmony.Hex(_set[_sel])}을 넣었어요. 실행 취소로 되돌려요.");
        }

        void TakeFromProp()
        {
            if (ChosenProp() is not var (name, label)) return;
            var c = ReadProp?.Invoke(name);
            if (c == null || c.Length < 3) { _status(label + " 값이 없습니다."); return; }
            var max = Math.Max(1f, c.Take(3).Max());   // HDR 색은 밝기를 1로 눌러서
            _sel = 0;
            SetBase(ColorHarmony.FromRgb(c[0] / max, c[1] / max, c[2] / max));
        }

        // ---------------- 스포이드 ----------------
        void Eyedropper()
        {
            if (!OperatingSystem.IsWindows()) { _status("스포이드는 Windows에서만 돼요."); return; }
            if (TopLevel.GetTopLevel(this) is not TopLevel top) return;
            ScreenPicker.Pick(top, c =>
            {
                if (c is not Color col) return;
                _sel = 0;
                SetBase(ColorHarmony.FromRgb(col.R / 255.0, col.G / 255.0, col.B / 255.0));
                _status($"스포이드: #{col.R:X2}{col.G:X2}{col.B:X2}");
            });
        }

        // ---------------- 이미지 ----------------
        async void OpenImage()
        {
            if (TopLevel.GetTopLevel(this) is not TopLevel top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "색을 뽑을 이미지", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("이미지") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tga", "*.psd", "*.tif", "*.tiff", "*.exr" } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) LoadImage(path, System.IO.Path.GetFileName(path));
        }

        void LoadImage(string? file, string what)
        {
            if (file == null || !File.Exists(file)) { _status(what == "메인 텍스처" ? "지금 머티리얼에 메인 텍스처 파일이 없어요." : "파일이 없어요."); return; }
            Bitmap small;
            try { small = ImageFiles.LoadSmall(file, 512); }   // TGA·PSD도 (2026-10-27)
            catch (Exception)
            {
                _status($"{what}: 이 형식은 열 수 없어요 (PNG·JPG·BMP·WebP·TGA·PSD).");
                return;
            }
            var size = small.PixelSize;
            var buf = new byte[size.Width * size.Height * 4];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { small.CopyPixels(new PixelRect(size), h.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
            finally { h.Free(); }
            bool bgra = small.Format == null || small.Format == PixelFormat.Bgra8888;
            bool premul = small.AlphaFormat == AlphaFormat.Premul;
            for (int i = 0; i < buf.Length; i += 4)
            {
                if (bgra) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
                byte a = buf[i + 3];
                if (premul && a > 0 && a < 255) for (int c = 0; c < 3; c++) buf[i + c] = (byte)Math.Min(255, buf[i + c] * 255 / a);
            }
            _px = buf; _pxW = size.Width; _pxH = size.Height;
            _image.Source = small; _imageFrame.IsVisible = true;
            _imageNote.Text = what + " · 클릭하거나 드래그하면 해당 위치의 색을 기본 색으로 지정 · 아래는 많이 사용된 색 (더블클릭하면 기본 색으로 지정)";
            ShowDominant(ColorHarmony.Dominant(buf, 6));
        }

        void ShowDominant(List<(Hsv Color, double Share)> list)
        {
            _dominant.Children.Clear(); _dominant.ColumnDefinitions.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var (c, share) = list[i];
                _dominant.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand) };
                cell.Children.Add(new SwatchFace { Height = 38, Fill = HarmonyLook.ToColor(c) });
                var label = Ui.Text($"{ColorHarmony.Hex(c)} · {share * 100:0}%", 11, FontWeight.Normal, Ui.KText2);
                label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 3, 0, 0);
                cell.Children.Add(label);
                ToolTip.SetTip(cell, "눌러서 이 색을 기본 색으로");
                cell.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { _sel = 0; SetBase(c); } };
                _dominant.Children.Add(cell.At(i));
            }
        }

        /// <summary>--ui-shots 확인용: 규칙 고르기 · 메인 텍스처 뽑기 · 고른 색 넣기. (결과 줄, 넣은 색 코드)</summary>
        internal (string Line, string? Hex) ShotTest(HarmonyRule rule)
        {
            _rule = rule; _sel = 1; SetBase(new Hsv(20, 0.7, 0.9));
            LoadImage(MainTextureFile?.Invoke(), "메인 텍스처");
            int dom = _dominant.Children.Count;
            FillProps();
            if (_prop.Items.Count == 0) return ($"FAIL palette: 색 프로퍼티 없음 · 대표 색 {dom}", null);
            ApplyToProp();
            return ($"ok palette {ColorHarmony.Name(rule)} {string.Join(" ", _set.Select(ColorHarmony.Hex))} · 대표 색 {dom} · 프로퍼티 {_prop.Items.Count}", ColorHarmony.Hex(_set[_sel]));
        }

        void PickImage(Point p)
        {
            if (_px == null || _image.Bounds.Width <= 0) return;
            int x = (int)(p.X / _image.Bounds.Width * _pxW), y = (int)(p.Y / _image.Bounds.Height * _pxH);
            if (x < 0 || y < 0 || x >= _pxW || y >= _pxH) return;
            int o = (y * _pxW + x) * 4;
            _sel = 0;
            SetBase(ColorHarmony.FromRgb(_px[o] / 255.0, _px[o + 1] / 255.0, _px[o + 2] / 255.0));
        }
    }
}
