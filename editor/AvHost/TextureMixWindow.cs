// 텍스처 합치기 창 (2026-11-02): 바탕 텍스처 위에 눈·이펙트·문양 같은 이미지를 레이어로 올린다. 계산은 Host/Core/TextureMix.cs.
//   왼쪽 그림판에서 끌어 옮기고(모서리 = 크기, 위 손잡이 = 회전, 휠 = 크기), 오른쪽에서 레이어·섞는 방법을 고른다. "이미지 찾기"는 같은 자리에 목록을 연다.
//   창 안의 값은 "적용"까지 문서에 들어가지 않는다. 그동안 아바타에는 줄인 PNG를 Doc.Preview로 보여 주고(기록 없음), 닫으면 문서 값으로 돌아간다.
//   적용 = 전체 크기로 합친 PNG를 %LOCALAPPDATA%\ALEdit\baked\에 쓰고 색 굽기처럼 Addr.TexAsset + MatTex (실행 취소 한 단계).
//   재료는 TexAsset 값의 "mix"에 적어 둬서 합친 텍스처를 다시 열면 레이어를 이어서 고친다 (바탕은 처음 바탕 그대로).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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
using VrcProto.Host.Shared;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class TextureMixWindow : Window
    {
        const int WorkSide = 1024;   // 그림판·아바타 미리 보기의 크기 (긴 변)

        /// <summary>레이어 + 작업 크기 그림. 그림이 null이면 아직 읽는 중이거나 파일을 못 읽은 것 (Missing).</summary>
        sealed class Item { public MixLayer L = new MixLayer(); public Rgba? Small; public int FullW, FullH; public bool Missing; }
        sealed class Candidate { public string Key = "", File = "", Name = "", Dir = ""; public bool Near; }

        readonly EditorContext _ctx;
        readonly Document _doc;
        readonly string _mat, _prop;
        readonly Func<IrTexture, bool> _near;
        string _baseKey = "", _baseFile = "", _baseName = "";
        int _baseW, _baseH;
        Rgba? _baseSmall;
        readonly List<Item> _items = new List<Item>();   // 아래 레이어부터
        Item? _sel;

        readonly MixCanvas _canvas;
        readonly ContentControl _side = new ContentControl();
        readonly StackPanel _layerList = new StackPanel { Spacing = 2 };
        readonly StackPanel _props = new StackPanel { Spacing = 2 };
        readonly TextBlock _state = Ui.Text("불러오는 중…", 12, FontWeight.Normal, Ui.KText2);
        readonly CheckBox _live = new CheckBox { Content = "아바타에 바로 보기", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Button _apply;
        readonly Action _liveLater;
        readonly Control _layersView;
        bool _busy, _composing, _dirty, _previewing;
        bool _closed;

        readonly List<Candidate> _candidates = new List<Candidate>();
        readonly TextBox _find = Ui.SearchBox("파일 이름으로 찾기");
        readonly WrapPanel _tiles = new WrapPanel();
        string _kind = "";
        Control? _pickerView;
        readonly List<Button> _kindButtons = new List<Button>();

        // 프로퍼티 칸
        readonly ComboBox _mode = Ui.CellCombo(new ComboBox());
        readonly Ui.EditSlider _opacity, _size, _rotation, _x, _y;
        readonly TextBlock _opacityText = Small(), _sizeText = Small(), _rotationText = Small(), _xText = Small(), _yText = Small();
        readonly CheckBox _flip = new CheckBox { Content = "좌우 뒤집기", Margin = new Thickness(0, 0, 12, 0) }, _mirror = new CheckBox { Content = "반대쪽에도 (두 눈)" };
        bool _quiet;

        static TextBlock Small() { var t = Ui.Text("", 12, FontWeight.Normal, Ui.KText2); t.Width = 44; t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; return t; }

        static string BakedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "baked");
        static string PreviewDir => Path.Combine(BakedDir, "mix-preview");

        public TextureMixWindow(EditorContext ctx, Document doc, string mat, string prop, string label, Func<IrTexture, bool> near)
        {
            _ctx = ctx; _doc = doc; _mat = mat; _prop = prop; _near = near;
            Title = "텍스처 합치기";
            Width = 1080; Height = 760; MinWidth = 820; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"); FontSize = 14;
            this.Bg(Ui.KCard);

            _liveLater = Ui.Debounce(SendLivePreview, 350);
            _opacity = new Ui.EditSlider(0, 100, v => Edit(l => l.Opacity = (float)(v / 100)), v => Edit(l => l.Opacity = (float)(v / 100)));
            _size = new Ui.EditSlider(1, 200, SetSize, SetSize);
            _rotation = new Ui.EditSlider(-180, 180, v => Edit(l => l.Rotation = (float)v), v => Edit(l => l.Rotation = (float)v));
            _x = new Ui.EditSlider(0, 100, v => Edit(l => l.X = (float)(v / 100)), v => Edit(l => l.X = (float)(v / 100)));
            _y = new Ui.EditSlider(0, 100, v => Edit(l => l.Y = (float)(v / 100)), v => Edit(l => l.Y = (float)(v / 100)));
            foreach (var m in TextureMix.Modes) _mode.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Key });
            _mode.SelectionChanged += (_, __) =>
            {
                if (_quiet || _mode.SelectedItem is not ComboBoxItem it) return;
                Edit(l => l.Mode = (string)it.Tag!);
                ToolTip.SetTip(_mode, TextureMix.Modes.First(m => m.Key == (string)it.Tag!).Tip);
                FillLayers();
            };
            _flip.IsCheckedChanged += (_, __) => { if (!_quiet) Edit(l => l.FlipX = _flip.IsChecked == true); };
            _mirror.IsCheckedChanged += (_, __) => { if (!_quiet) Edit(l => l.Mirror = _mirror.IsChecked == true); };
            ToolTip.SetTip(_mirror, "그림의 왼쪽·오른쪽 거울 자리에 한 장 더 그려요. 한쪽 눈 그림으로 두 눈을 올릴 때 써요.");
            ToolTip.SetTip(_live, "고치는 동안 아바타에서 바로 확인해요. 조금 줄인 그림으로 보여 주고, \"적용\"하면 원래 크기로 만들어요.");
            _live.IsCheckedChanged += (_, __) => { if (_live.IsChecked == true) _liveLater(); else RestorePreview(); };

            _canvas = new MixCanvas(this);
            _layersView = BuildLayersView();
            _side.Content = _layersView;

            var top = new StackPanel { Margin = new Thickness(24, 18, 24, 8) };
            top.Children.Add(Ui.DialogHeader(Title, $"{doc.Original.FindMaterial(mat)?.Name ?? "머티리얼"} · {label}",
                "바탕 텍스처 위에 다른 이미지를 올려 새 텍스처 하나로 만들어요. 그림판의 자리는 메시의 UV 그대로예요 — 눈은 바탕 그림의 눈 자리에 놓으면 돼요. 원본 파일은 바뀌지 않아요."));
            var buttons = new DockPanel { Margin = new Thickness(24, 8, 24, 16) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _apply = Ui.Button("적용", () => _ = ApplyAsync(), "원래 크기로 합쳐 이 머티리얼에 넣어요 (실행 취소 가능)", null, accent: true);
            var close = Ui.Button("닫기", Close);
            _apply.Margin = close.Margin = new Thickness(0);
            close.IsCancel = true;
            right.Children.Add(_live); right.Children.Add(close); right.Children.Add(_apply);
            DockPanel.SetDock(right, Dock.Right);
            _state.VerticalAlignment = VerticalAlignment.Center; _state.TextTrimming = TextTrimming.CharacterEllipsis;
            buttons.Children.Add(right); buttons.Children.Add(_state);

            var body = new Grid { Margin = new Thickness(24, 0, 24, 0), ColumnDefinitions = new ColumnDefinitions("*,16,340") };
            var frame = new Border { Child = _canvas, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true }.Bg(Ui.KBase).Stroke(Ui.KCardStroke);
            body.Children.Add(frame.At(0)); body.Children.Add(_side.At(2));

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(buttons); root.Children.Add(body);
            Content = root;

            _find.TextChanged += (_, __) => FillTiles();
            _ctx.DocumentOpened += OnDocOpened;
            Closed += (_, __) =>
            {
                _closed = true;
                _ctx.DocumentOpened -= OnDocOpened;
                RestorePreview();
                try { if (Directory.Exists(PreviewDir)) Directory.Delete(PreviewDir, true); } catch (Exception) { }   // 렌더러가 읽는 중이면 다음 번에
            };
            Loaded_ = LoadStart();
        }

        /// <summary>처음 읽기가 끝나면 끝나는 작업 (화면 찍기 시험이 기다린다).</summary>
        public readonly Task Loaded_;

        void OnDocOpened() { if (_ctx.Doc != _doc) Close(); }

        // ---------------- 불러오기 ----------------
        async Task LoadStart()
        {
            // 합친 텍스처를 다시 열면: 처음 바탕 + 레이어로
            var cur = _doc.Get(Addr.MatTex(_mat, _prop)).AsStringOrNull;
            var recipe = cur == null ? JNode.Null : _doc.Get(Addr.TexAsset(cur))["mix"];
            var layers = new List<MixLayer>();
            _baseKey = cur ?? "";
            if (!recipe.IsNull)
            {
                var file = _doc.Texture(recipe["base"].AsString)?.File ?? recipe["baseFile"].AsString;
                if (File.Exists(file)) { _baseKey = recipe["base"].AsString; layers = TextureMix.LayersFromJson(recipe); }
                else _ctx.Status("합칠 때 쓴 바탕 파일이 없어 지금 텍스처를 바탕으로 해요.");
            }
            var t = _doc.Texture(_baseKey);
            _baseFile = t?.File ?? (!recipe.IsNull ? recipe["baseFile"].AsString : "");
            _baseName = t != null ? Path.GetFileName(t.Path) : Path.GetFileName(_baseFile);
            FillCandidatesFromDoc();
            var dirs = _candidates.Select(c => c.Dir).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
            var known = new HashSet<string>(_candidates.Select(c => c.File), StringComparer.OrdinalIgnoreCase) { _baseFile };
            var nearDir = Path.GetDirectoryName(_baseFile) ?? "";
            if (nearDir.Length > 0 && !dirs.Contains(nearDir, StringComparer.OrdinalIgnoreCase)) dirs.Insert(0, nearDir);
            try
            {
                var loaded = await Task.Run(() =>
                {
                    var full = ImageFiles.LoadFull(_baseFile);
                    return (W: full.W, H: full.H, Small: TextureMix.ShrinkBase(full, WorkSide));
                });
                _baseW = loaded.W; _baseH = loaded.H; _baseSmall = loaded.Small;
            }
            catch (Exception e)
            {
                _state.Text = $"바탕 텍스처를 읽지 못했어요: {e.Message}";
                _apply.IsEnabled = false;
                return;
            }
            foreach (var l in layers) { var it = new Item { L = l }; _items.Add(it); }
            _sel = _items.LastOrDefault();
            FillLayers(); FillProps();
            await Task.WhenAll(_items.ToList().Select(LoadItem));
            if (_closed) return;
            Changed(live: layers.Count > 0);
            _state.Text = $"바탕 {_baseW}×{_baseH} · 이미지를 찾아 올려요";
            // 쓰이지 않아 IR에 없는 이미지도 옆 폴더에서 찾는다 (눈 색 바꾸기 그림 등)
            var extra = await Task.Run(() => ScanFolders(dirs, known));
            foreach (var c in extra) { c.Near = string.Equals(c.Dir, nearDir, StringComparison.OrdinalIgnoreCase); _candidates.Add(c); }
            if (extra.Count > 0 && !_closed && _side.Content == _pickerView) FillTiles();
        }

        void FillCandidatesFromDoc()
        {
            foreach (var t in _doc.Original.Textures)
            {
                if (t.Importer.NormalMap || t.File.Length == 0 || t.Guid == _baseKey) continue;
                if (!ImageFiles.CanRead(Path.GetExtension(t.File).ToLowerInvariant())) continue;
                // 파일이 패키지 안 이름(guid 등)이면 옆 폴더 찾기에 쓰지 않는다
                bool realName = string.Equals(Path.GetFileName(t.File), Path.GetFileName(t.Path), StringComparison.OrdinalIgnoreCase);
                _candidates.Add(new Candidate { Key = t.Guid, File = t.File, Name = Path.GetFileName(t.Path), Dir = realName ? Path.GetDirectoryName(t.File) ?? "" : "", Near = _near(t) });
            }
            foreach (var k in _doc.Overrides.Keys.Where(k => k.Kind == AddrKind.TexAsset))
                if (k.Target != _baseKey && _doc.Texture(k.Target) is IrTexture t && t.File.Length > 0 && !k.Target.StartsWith("mixprev:", StringComparison.Ordinal))
                    _candidates.Add(new Candidate { Key = t.Guid, File = t.File, Name = Path.GetFileName(t.Path) });
        }

        static List<Candidate> ScanFolders(List<string> dirs, HashSet<string> known)
        {
            var list = new List<Candidate>();
            foreach (var d in dirs)
            {
                if (list.Count >= 400) break;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d).Take(600))
                    {
                        if (known.Contains(f) || !ImageFiles.CanRead(Path.GetExtension(f).ToLowerInvariant())) continue;
                        var name = Path.GetFileName(f);
                        if (name.Contains("normal", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new Candidate { File = f, Name = name, Dir = d });
                    }
                }
                catch (Exception) { }   // 없어진 폴더·권한
            }
            return list;
        }

        async Task LoadItem(Item it)
        {
            var file = it.L.File;
            if (!File.Exists(file) && it.L.Key.Length > 0 && _doc.Texture(it.L.Key)?.File is string again) { file = again; it.L.File = again; }
            try
            {
                var r = await Task.Run(() => { var full = ImageFiles.LoadFull(file); return (full.W, full.H, Small: Sticker.Resample2(full, WorkSide)); });
                it.Small = r.Small; it.FullW = r.W; it.FullH = r.H; it.Missing = false;
            }
            catch (Exception) { it.Missing = true; it.Small = null; }
            if (!_closed) FillLayers();
        }

        // ---------------- 레이어 ----------------
        async void AddCandidate(Candidate c)
        {
            await AddLayer(c.Key, c.File, c.Name);
            ShowLayers();
        }

        /// <summary>이미지 한 장을 맨 위 레이어로. 처음 자리는 그림 크기로 정한다.</summary>
        async Task AddLayer(string key, string file, string name)
        {
            if (_baseSmall == null) return;
            var it = new Item { L = new MixLayer { Key = key, File = file, Name = name } };
            await LoadItem(it);
            if (_closed) return;
            if (it.Small == null) { _state.Text = $"{name}을(를) 읽지 못했어요."; return; }
            TextureMix.PlaceDefault(it.L, _baseW, _baseH, it.FullW, it.FullH);
            _items.Add(it);
            Select(it);
            Changed();
            _state.Text = it.L.W >= 1 && it.L.H >= 1 ? $"{name}: 바탕과 같은 크기라 꽉 채웠어요. 작은 그림이면 크기를 줄여요." : $"{name}: 그림판에서 끌어 자리를 맞춰요.";
        }

        void Select(Item? it) { _sel = it; FillLayers(); FillProps(); _canvas.InvalidateVisual(); }

        void Edit(Action<MixLayer> f)
        {
            if (_sel == null) return;
            f(_sel.L);
            ShowValues();
            Changed();
        }

        void SetSize(double percent)
        {
            if (_sel == null) return;
            var l = _sel.L;
            float w = (float)(percent / 100);
            l.H = l.W > 0 ? l.H * w / l.W : w; l.W = w;
            ShowValues();
            Changed();
        }

        void FillLayers()
        {
            _layerList.Children.Clear();
            if (_items.Count == 0) _layerList.Children.Add(Ui.Wrap("아직 올린 이미지가 없어요. 위 단추로 찾아 올려요."));
            for (int i = _items.Count - 1; i >= 0; i--) _layerList.Children.Add(LayerRow(_items[i], i));
            var bas = new DockPanel { Margin = new Thickness(8, 6) };
            var text = Ui.Text($"바탕: {_baseName}", 12, FontWeight.Normal, Ui.KText2);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            bas.Children.Add(text);
            _layerList.Children.Add(bas);
        }

        Control LayerRow(Item it, int index)
        {
            var thumb = new Image { Width = 36, Height = 36, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (File.Exists(it.L.File)) AssetsPanel.LoadThumb(it.L.File, b => thumb.Source = b);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(it.L.Name, 13); name.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(name);
            var mode = TextureMix.Modes.FirstOrDefault(m => m.Key == it.L.Mode).Label ?? "";
            names.Children.Add(it.Missing ? Ui.Text("파일을 읽지 못했어요", 11, FontWeight.Normal, Ui.KCaution) : Ui.Text(mode, 11, FontWeight.Normal, Ui.KText2));
            var ctrl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var vis = Ui.IconButton(it.L.Visible ? Glyph.View : Glyph.Hide, () => { it.L.Visible = !it.L.Visible; FillLayers(); Changed(); }, it.L.Visible ? "이 레이어 숨기기" : "이 레이어 보이기");
            var up = Ui.IconButton(Glyph.ChevronUp, () => Move(index, +1), "위로"); up.IsEnabled = index < _items.Count - 1;
            var down = Ui.IconButton(Glyph.ChevronDown, () => Move(index, -1), "아래로"); down.IsEnabled = index > 0;
            var del = Ui.IconButton(Glyph.Delete, () => Remove(it), "이 레이어 빼기");
            foreach (var b in new[] { vis, up, down, del }) ctrl.Children.Add(b);
            var row = new DockPanel();
            DockPanel.SetDock(thumb, Dock.Left); DockPanel.SetDock(ctrl, Dock.Right);
            row.Children.Add(thumb); row.Children.Add(ctrl); row.Children.Add(names);
            var box = new Border { Child = row, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
            if (it == _sel) box.Bg(Ui.KSelect);
            box.PointerPressed += (_, __) => { if (_sel != it) Select(it); };
            return box;
        }

        void Move(int index, int by)
        {
            int to = index + by;
            if (to < 0 || to >= _items.Count) return;
            (_items[index], _items[to]) = (_items[to], _items[index]);
            FillLayers();
            Changed();
        }

        void Remove(Item it)
        {
            _items.Remove(it);
            if (_sel == it) _sel = _items.LastOrDefault();
            FillLayers(); FillProps();
            Changed();
        }

        // ---------------- 오른쪽 칸 ----------------
        Control BuildLayersView()
        {
            var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            var find = Ui.Button("이미지 찾기", ShowPicker, "이 아바타의 텍스처와 옆 폴더의 이미지에서 찾아요", Glyph.Search, accent: true);
            var file = Ui.Button("파일에서…", PickFile, "PC의 이미지 파일을 골라요", Glyph.Open);
            find.Margin = file.Margin = new Thickness(0);
            add.Children.Add(find); add.Children.Add(file);

            var fill = Ui.Button("꽉 채우기", () => { if (_sel != null) { TextureMix.PlaceFill(_sel.L); AfterPlace(); } }, "바탕 전체를 덮어요. 같은 UV에 그린 그림에 써요.");
            var native = Ui.Button("원래 크기", () => { if (_sel != null && _sel.FullW > 0) { TextureMix.PlaceNative(_sel.L, _baseW, _baseH, _sel.FullW, _sel.FullH); AfterPlace(); } }, "이미지의 픽셀 크기 그대로 (가로세로 비율도 원래대로)");
            var center = Ui.Button("가운데로", () => { if (_sel != null) { _sel.L.X = _sel.L.Y = 0.5f; AfterPlace(); } });
            var place = new WrapPanel();
            foreach (var b in new[] { fill, native, center }) { b.Margin = new Thickness(0, 4, 6, 4); b.Padding = new Thickness(10, 4); place.Children.Add(b); }
            var checks = new WrapPanel { Margin = new Thickness(0, 4) };
            checks.Children.Add(_flip); checks.Children.Add(_mirror);

            _props.Children.Add(Ui.PropRow(Ui.Text("섞는 방법"), _mode, null));
            _props.Children.Add(SliderRow("세기", _opacity, _opacityText));
            _props.Children.Add(SliderRow("크기", _size, _sizeText));
            _props.Children.Add(SliderRow("회전", _rotation, _rotationText));
            _props.Children.Add(SliderRow("가로 자리", _x, _xText));
            _props.Children.Add(SliderRow("세로 자리", _y, _yText));
            _props.Children.Add(checks);
            _props.Children.Add(place);

            var panel = new DockPanel();
            DockPanel.SetDock(add, Dock.Top); DockPanel.SetDock(_props, Dock.Bottom);
            _props.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(add); panel.Children.Add(_props);
            panel.Children.Add(new ScrollViewer { Content = _layerList });
            return panel;
        }

        static Control SliderRow(string name, Ui.EditSlider s, TextBlock value)
        {
            var d = new DockPanel();
            DockPanel.SetDock(value, Dock.Right);
            d.Children.Add(value); d.Children.Add(s.Slider);
            return Ui.PropRow(Ui.Text(name), d, null);
        }

        void AfterPlace() { FillProps(); Changed(); }

        void FillProps()
        {
            _props.IsVisible = _sel != null;
            if (_sel == null) return;
            var l = _sel.L;
            _quiet = true;
            _mode.SelectedItem = _mode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == l.Mode);
            _flip.IsChecked = l.FlipX; _mirror.IsChecked = l.Mirror;
            _quiet = false;
            ShowValues();
        }

        /// <summary>슬라이더·글자를 지금 값으로 (그림판에서 끌 때도).</summary>
        void ShowValues()
        {
            if (_sel == null) return;
            var l = _sel.L;
            _opacity.SetQuiet(l.Opacity * 100); _size.SetQuiet(l.W * 100); _rotation.SetQuiet(l.Rotation); _x.SetQuiet(l.X * 100); _y.SetQuiet(l.Y * 100);
            _opacityText.Text = $"{l.Opacity * 100:0}%"; _sizeText.Text = $"{l.W * 100:0.#}%"; _rotationText.Text = $"{l.Rotation:0}°"; _xText.Text = $"{l.X * 100:0.#}%"; _yText.Text = $"{l.Y * 100:0.#}%";
        }

        async void PickFile()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "올릴 이미지", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("이미지") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tga", "*.psd", "*.tif", "*.tiff", "*.exr" } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) await AddLayer("", path, Path.GetFileName(path));
        }

        // ---------------- 이미지 찾기 ----------------
        void ShowLayers() => _side.Content = _layersView;

        public void ShowPicker()
        {
            _pickerView ??= BuildPicker();
            _side.Content = _pickerView;
            FillTiles();
            _find.Focus();
        }

        Control BuildPicker()
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var back = Ui.IconButton(Glyph.Back, ShowLayers, "돌아가기");
            back.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(back, Dock.Left);
            head.Children.Add(back); head.Children.Add(_find);
            var kinds = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (key, label) in new[] { ("", "전체"), ("eye", "눈"), ("face", "얼굴 꾸미기"), ("effect", "이펙트·문양"), ("near", "이 머티리얼 옆") })
            {
                var b = new Button { Content = Ui.Text(label, 12), Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 4, 4), MinHeight = 0, Tag = key };
                b.Click += (_, __) => { _kind = key; FillTiles(); };
                _kindButtons.Add(b);
                kinds.Children.Add(b);
            }
            var top = new StackPanel();
            top.Children.Add(head); top.Children.Add(kinds);
            var panel = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
            panel.Children.Add(new ScrollViewer { Content = _tiles });
            return panel;
        }

        /// <summary>파일 이름을 낱말 종류·찾는 말로 거른다. 이 머티리얼과 가까운 것을 먼저, 그다음 이름 순. 한 번에 200개까지.</summary>
        void FillTiles()
        {
            foreach (var b in _kindButtons) { if ((string)b.Tag! == _kind) b.Classes.Add("accent"); else b.Classes.Remove("accent"); }
            _tiles.Children.Clear();
            var q = (_find.Text ?? "").Trim();
            var list = _candidates
                .Where(c => _kind == "near" ? c.Near : TextureMix.NameLooksLike(_kind, c.Name))
                .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Near).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var c in list.Take(200)) _tiles.Children.Add(Tile(c));
            if (list.Count == 0)
                _tiles.Children.Add(Ui.Wrap(_kind == "" && q.Length == 0 ? "고를 수 있는 이미지가 없어요. 돌아가서 \"파일에서…\"로 골라요."
                    : "이름이 맞는 이미지가 없어요. \"전체\"에서 찾거나, 돌아가서 \"파일에서…\"로 골라요."));
            else if (list.Count > 200)
                _tiles.Children.Add(Ui.Wrap($"{list.Count}개 중 200개만 보여요. 이름으로 찾아 줄여요."));
        }

        Control Tile(Candidate c)
        {
            var img = new Image { Width = 88, Height = 88, Stretch = Stretch.Uniform };
            var name = Ui.Text(c.Name, 11, FontWeight.Normal, Ui.KText2);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.Width = 92; name.TextAlignment = TextAlignment.Center;
            var p = new StackPanel { Spacing = 2 };
            p.Children.Add(img); p.Children.Add(name);
            var b = new Button { Content = p, Padding = new Thickness(4), Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0 };
            ToolTip.SetTip(b, c.Key.Length > 0 ? c.Name : c.File);
            b.Click += (_, __) => AddCandidate(c);
            AssetsPanel.LoadThumb(c.File, bmp => img.Source = bmp);
            return b;
        }

        // ---------------- 그리기 ----------------
        /// <summary>값이 바뀜: 그림판을 다시 합치고, 조용해지면 아바타에도.</summary>
        void Changed(bool live = true)
        {
            _dirty = true;
            if (!_composing) ComposeLoop();
            if (live) _liveLater();
        }

        async void ComposeLoop()
        {
            if (_baseSmall == null) return;
            _composing = true;
            while (_dirty && !_closed)
            {
                _dirty = false;
                var bas = _baseSmall;
                var snap = _items.Select(i => (i.L.Clone(), i.Small)).ToList();
                var img = await Task.Run(() => TextureMix.Compose(bas, snap));
                if (!_closed) _canvas.SetImage(img);
            }
            _composing = false;
        }

        // ---------------- 아바타 미리 보기 ----------------
        /// <summary>줄인 그림을 Doc.Preview로 보낸다 (문서·실행 취소에 남지 않는다). 키가 같으면 렌더러가 새 파일을 읽지 않아 매번 새 키.</summary>
        async void SendLivePreview()
        {
            if (_closed || _live.IsChecked != true || _baseSmall == null || _ctx.Render == null || !_ctx.Render.IsConnected) return;
            var a = Addr.MatTex(_mat, _prop);
            if (_items.Count == 0) { _doc.Preview(a, JNode.From(_baseKey)); _previewing = true; return; }
            var bas = _baseSmall;
            var snap = _items.Select(i => (i.L.Clone(), i.Small)).ToList();
            var stamp = DateTime.Now.Ticks.ToString("x");
            var file = Path.Combine(PreviewDir, stamp + ".png");
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(PreviewDir);
                    File.WriteAllBytes(file, Sticker.EncodePng(TextureMix.Compose(bas, snap), System.IO.Compression.CompressionLevel.Fastest));
                });
            }
            catch (Exception e) { _state.Text = "미리 보기를 만들지 못했어요: " + e.Message; return; }
            if (_closed || _live.IsChecked != true) return;
            var key = "mixprev:" + stamp;
            _doc.Preview(Addr.TexAsset(key), JNode.Obj().Set("file", file).Set("path", _baseName).Set("like", LikeKey()));
            _doc.Preview(a, JNode.From(key));
            _previewing = true;
        }

        void RestorePreview()
        {
            if (!_previewing) return;
            _previewing = false;
            var a = Addr.MatTex(_mat, _prop);
            _doc.Preview(a, _doc.Get(a));
        }

        /// <summary>임포트 설정을 따를 원본 (바탕이 구운·합친 텍스처면 그것의 원본).</summary>
        string LikeKey() => _doc.Original.FindTexture(_baseKey) != null ? _baseKey : _doc.Get(Addr.TexAsset(_baseKey))["like"].AsString;

        // ---------------- 적용 ----------------
        /// <summary>전체 크기로 합쳐 문서에 넣는다 (한 단계). 성공하면 true.</summary>
        public async Task<bool> ApplyAsync()
        {
            if (_busy || _baseSmall == null) return false;
            var a = Addr.MatTex(_mat, _prop);
            var matName = _doc.Original.FindMaterial(_mat)?.Name ?? "머티리얼";
            if (_items.Any(i => i.Missing)) { _state.Text = "읽지 못한 레이어가 있어요. 빼고 다시 적용해요."; return false; }
            if (_items.Count == 0)
            {
                // 레이어를 다 빼고 적용 = 바탕으로 돌아가기
                _doc.Set(a, JNode.From(_baseKey), "텍스처 합치기 풀기: " + matName);
                _previewing = false;
                _state.Text = "바탕 텍스처로 돌아갔어요.";
                return true;
            }
            var layers = _items.Select(i => i.L.Clone()).ToList();
            var recipe = TextureMix.RecipeToJson(_baseKey, _baseFile, layers);
            var like = LikeKey();
            var baseFile = _baseFile;
            // 같은 재료(파일 내용이 그대로)면 같은 파일 (다시 적용해도 쌓이지 않게)
            var sig = new StringBuilder(recipe.ToString());
            foreach (var f in layers.Select(l => l.File).Append(baseFile)) { try { sig.Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks); } catch (Exception) { } }
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sig.ToString()))).Substring(0, 16).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(_baseName);
            var file = Path.Combine(BakedDir, stem + "_mix_" + hash.Substring(0, 8) + ".png");
            _busy = true; _apply.IsEnabled = false;
            _state.Text = "합치는 중…";
            try
            {
                if (!File.Exists(file))
                    await Task.Run(() =>
                    {
                        var bas = ImageFiles.LoadFull(baseFile);
                        var img = bas;
                        foreach (var l in layers) if (l.Visible) img = TextureMix.Compose(img, new[] { (l, (Rgba?)ImageFiles.LoadFull(l.File)) });
                        Directory.CreateDirectory(BakedDir);
                        var tmp = file + ".tmp";
                        File.WriteAllBytes(tmp, Sticker.EncodePng(img));
                        File.Move(tmp, file, true);
                    });
            }
            catch (Exception e)
            {
                _state.Text = "합치지 못했어요: " + e.Message;
                return false;
            }
            finally { _busy = false; _apply.IsEnabled = true; }
            if (_closed || _ctx.Doc != _doc) return false;
            var key = "mix:" + hash;
            using (_doc.Group("텍스처 합치기: " + matName))
            {
                _doc.Set(Addr.TexAsset(key), JNode.Obj().Set("file", file).Set("path", stem + "_mix.png").Set("like", like).Set("mix", recipe));
                _doc.Set(a, JNode.From(key));
            }
            _previewing = false;   // 이제 문서 값이 아바타에 나온 값이다
            _state.Text = $"적용했어요 ({_baseW}×{_baseH}) → {Path.GetFileName(file)}. 계속 고치거나 닫아요.";
            _ctx.Status($"텍스처를 합쳤어요 → {Path.GetFileName(file)}. 실행 취소로 되돌려요.");
            return true;
        }

        // ---------------- 화면 찍기 시험 ----------------
        /// <summary>찾기 목록의 앞에서 맞는 것을 레이어로 올린다 (시험용).</summary>
        public Task ShotAdd(string file, Action<MixLayer>? place = null) => ShotAddCore(file, place);
        async Task ShotAddCore(string file, Action<MixLayer>? place)
        {
            await AddLayer("", file, Path.GetFileName(file));
            if (_sel != null && place != null) { place(_sel.L); FillProps(); Changed(); }
        }
        public void ShotLayers() => ShowLayers();
        public MixLayer? ShotSelected => _sel?.L;
        public int ShotCandidates(string kind) => _candidates.Count(c => TextureMix.NameLooksLike(kind, c.Name));

        /// <summary>고른 레이어의 가운데(또는 오른쪽 아래 모서리)를 창 메시지로 끈다 — 화면 밖 창도 진짜 포인터 경로를 탄다 (시험용).</summary>
        public async Task ShotDrag(bool corner, double dx, double dy)
        {
            if (_sel == null || TryGetPlatformHandle()?.Handle is not IntPtr hwnd) return;
            var from = _canvas.ShotPoint(_sel.L, corner);
            double k = RenderScaling;
            if (_canvas.TranslatePoint(from, this) is not Point p) return;
            IntPtr L(double x, double y) => (IntPtr)(((int)(y * k) << 16) | ((int)(x * k) & 0xFFFF));
            PostMessage(hwnd, 0x0200, IntPtr.Zero, L(p.X, p.Y)); await Task.Delay(50);
            PostMessage(hwnd, 0x0201, (IntPtr)1, L(p.X, p.Y)); await Task.Delay(50);
            PostMessage(hwnd, 0x0200, (IntPtr)1, L(p.X + dx / 2, p.Y + dy / 2)); await Task.Delay(50);
            PostMessage(hwnd, 0x0200, (IntPtr)1, L(p.X + dx, p.Y + dy)); await Task.Delay(50);
            PostMessage(hwnd, 0x0202, IntPtr.Zero, L(p.X + dx, p.Y + dy)); await Task.Delay(100);
        }
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // ---------------- 그림판 ----------------
        /// <summary>합친 그림(체크 무늬 위)과 고른 레이어의 틀·손잡이. 좌표는 그림판 ↔ 바탕 픽셀 ↔ 레이어 비율.</summary>
        sealed class MixCanvas : Control
        {
            readonly TextureMixWindow _w;
            WriteableBitmap? _bmp;
            enum Drag { None, Move, Scale, Rotate }
            Drag _drag;
            Point _start; float _x0, _y0, _w0, _h0, _rot0; double _dist0, _angle0;

            public MixCanvas(TextureMixWindow w) { _w = w; ClipToBounds = true; Focusable = true; }

            public void SetImage(Rgba img)
            {
                if (_bmp == null || _bmp.PixelSize.Width != img.W || _bmp.PixelSize.Height != img.H)
                {
                    _bmp?.Dispose();
                    _bmp = new WriteableBitmap(new PixelSize(img.W, img.H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                }
                using (var fb = _bmp.Lock())
                {
                    var row = new byte[img.W * 4];
                    for (int y = 0; y < img.H; y++)
                    {
                        for (int x = 0; x < img.W; x++)
                        {
                            int i = (y * img.W + x) * 4, o = x * 4, a = img.Px[i + 3];
                            int bg = ((x >> 4) + (y >> 4)) % 2 == 0 ? 90 : 60;   // 투명한 곳은 체크 무늬
                            row[o] = (byte)((img.Px[i + 2] * a + bg * (255 - a)) / 255);
                            row[o + 1] = (byte)((img.Px[i + 1] * a + bg * (255 - a)) / 255);
                            row[o + 2] = (byte)((img.Px[i] * a + bg * (255 - a)) / 255);
                            row[o + 3] = 255;
                        }
                        Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                    }
                }
                InvalidateVisual();
            }

            /// <summary>그림이 놓인 칸 (비율 지켜 가운데).</summary>
            Rect Area()
            {
                if (_w._baseW <= 0) return default;
                double pad = 16, aw = Math.Max(1, Bounds.Width - pad * 2), ah = Math.Max(1, Bounds.Height - pad * 2);
                double k = Math.Min(aw / _w._baseW, ah / _w._baseH);
                double w = _w._baseW * k, h = _w._baseH * k;
                return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
            }

            /// <summary>레이어의 네 모서리 (왼위·오른위·오른아래·왼아래). 회전은 바탕 픽셀 공간에서 — 그림판은 비율을 지키므로 그대로 쓴다.</summary>
            static Point[] Corners(Rect r, MixLayer l, float x, float rotation)
            {
                double cx = r.X + x * r.Width, cy = r.Y + l.Y * r.Height, hw = l.W * r.Width / 2, hh = l.H * r.Height / 2;
                double rad = rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
                Point P(double dx, double dy) => new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
                return new[] { P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh) };
            }

            public Point ShotPoint(MixLayer l, bool corner)
            {
                var r = Area();
                return corner ? Corners(r, l, l.X, l.Rotation)[2] : new Point(r.X + l.X * r.Width, r.Y + l.Y * r.Height);
            }

            static Point RotateHandle(Point[] c)
            {
                var top = new Point((c[0].X + c[1].X) / 2, (c[0].Y + c[1].Y) / 2);
                var mid = new Point((c[0].X + c[2].X) / 2, (c[0].Y + c[2].Y) / 2);
                double dx = top.X - mid.X, dy = top.Y - mid.Y, len = Math.Max(1e-3, Math.Sqrt(dx * dx + dy * dy));
                return new Point(top.X + dx / len * 22, top.Y + dy / len * 22);
            }

            public override void Render(DrawingContext g)
            {
                var r = Area();
                g.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));   // 그림 밖도 눌리게 (그려진 곳만 포인터를 받는다)
                if (_bmp != null) g.DrawImage(_bmp, new Rect(0, 0, _bmp.PixelSize.Width, _bmp.PixelSize.Height), r);
                var sel = _w._sel;
                if (sel == null || r.Width <= 0) return;
                var dark = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3);
                var line = new Pen(Brushes.White, 1.2);
                void Box(Point[] c, IPen pen) { for (int i = 0; i < 4; i++) g.DrawLine(pen, c[i], c[(i + 1) % 4]); }
                var cs = Corners(r, sel.L, sel.L.X, sel.L.Rotation);
                if (sel.L.Mirror)
                {
                    var ghost = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1, new DashStyle(new double[] { 3, 3 }, 0));
                    Box(Corners(r, sel.L, 1 - sel.L.X, -sel.L.Rotation), ghost);
                }
                Box(cs, dark); Box(cs, line);
                var fill = Brushes.White;
                foreach (var c in cs) g.DrawEllipse(fill, new Pen(Brushes.Black, 1), c, 5, 5);
                var h = RotateHandle(cs);
                var topMid = new Point((cs[0].X + cs[1].X) / 2, (cs[0].Y + cs[1].Y) / 2);
                g.DrawLine(dark, topMid, h); g.DrawLine(line, topMid, h);
                g.DrawEllipse(fill, new Pen(Brushes.Black, 1), h, 5, 5);
            }

            static bool Inside(Point[] c, Point p)
            {
                // 볼록 사각형: 모든 변의 같은 쪽
                for (int i = 0; i < 4; i++)
                {
                    var a = c[i]; var b = c[(i + 1) % 4];
                    if ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) < 0) return false;
                }
                return true;
            }

            static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var r = Area();
                var p = e.GetPosition(this);
                if (r.Width <= 0) return;
                var sel = _w._sel;
                _drag = Drag.None;
                if (sel != null)
                {
                    var cs = Corners(r, sel.L, sel.L.X, sel.L.Rotation);
                    if (Dist(p, RotateHandle(cs)) <= 9) _drag = Drag.Rotate;
                    else if (cs.Any(c => Dist(p, c) <= 9)) _drag = Drag.Scale;
                    else if (Inside(cs, p)) _drag = Drag.Move;
                }
                if (_drag == Drag.None)
                {
                    // 고른 레이어 밖을 누르면 그 자리의 맨 위 레이어를 고른다
                    var hit = Enumerable.Reverse(_w._items).FirstOrDefault(i => i.L.Visible && Inside(Corners(r, i.L, i.L.X, i.L.Rotation), p));
                    if (hit == null) return;
                    _w.Select(hit);
                    sel = hit; _drag = Drag.Move;
                }
                var l = sel!.L;
                _start = p; _x0 = l.X; _y0 = l.Y; _w0 = l.W; _h0 = l.H; _rot0 = l.Rotation;
                var center = new Point(r.X + l.X * r.Width, r.Y + l.Y * r.Height);
                _dist0 = Math.Max(1, Dist(p, center));
                _angle0 = Math.Atan2(p.Y - center.Y, p.X - center.X);
                e.Pointer.Capture(this);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                if (_drag == Drag.None || _w._sel == null) return;
                var r = Area();
                var p = e.GetPosition(this);
                var l = _w._sel.L;
                var center = new Point(r.X + _x0 * r.Width, r.Y + _y0 * r.Height);
                switch (_drag)
                {
                    case Drag.Move:
                        l.X = _x0 + (float)((p.X - _start.X) / r.Width);
                        l.Y = _y0 + (float)((p.Y - _start.Y) / r.Height);
                        break;
                    case Drag.Scale:
                        float k = (float)(Dist(p, center) / _dist0);
                        l.W = Math.Clamp(_w0 * k, 0.005f, 2f); l.H = _h0 * l.W / _w0;
                        break;
                    case Drag.Rotate:
                        double deg = _rot0 + (Math.Atan2(p.Y - center.Y, p.X - center.X) - _angle0) * 180 / Math.PI;
                        deg = (deg + 540) % 360 - 180;
                        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) deg = Math.Round(deg / 15) * 15;
                        l.Rotation = (float)deg;
                        break;
                }
                _w.ShowValues();
                _w.Changed();
                InvalidateVisual();
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                _drag = Drag.None;
                e.Pointer.Capture(null);
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                base.OnPointerWheelChanged(e);
                if (_w._sel == null) return;
                var l = _w._sel.L;
                float k = e.Delta.Y > 0 ? 1.05f : 1 / 1.05f, w = Math.Clamp(l.W * k, 0.005f, 2f);
                l.H = l.H * w / l.W; l.W = w;
                _w.ShowValues();
                _w.Changed();
                InvalidateVisual();
                e.Handled = true;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (_w._sel == null) return;
                // 화살표 = 한 칸 (Shift는 열 칸), 바탕 픽셀 기준
                float px = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1, dx = 0, dy = 0;
                switch (e.Key) { case Key.Left: dx = -px; break; case Key.Right: dx = px; break; case Key.Up: dy = -px; break; case Key.Down: dy = px; break; default: return; }
                _w._sel.L.X += dx / _w._baseW; _w._sel.L.Y += dy / _w._baseH;
                _w.ShowValues();
                _w.Changed();
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }
}
