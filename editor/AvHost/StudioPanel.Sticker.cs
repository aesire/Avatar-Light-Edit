// 스튜디오: 촬영 보정(밝기·대비·채도)과 스티커 만들기 (Host/Core/Sticker.cs).
//   보정은 촬영 PNG를 받은 뒤 Host가 고쳐 다시 저장한다 (값이 기본이면 손대지 않음).
//   스티커는 지금 카메라로 투명 배경을 크게 찍어(임시 파일) → 보이는 부분만 잘라 규격 칸에 맞추고 테두리를 둘러 PNG로 저장한다.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class StudioPanel
    {
        string? _pendingSticker, _stickerOut;
        readonly Image _stickerPreview = new Image { MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
        readonly TextBlock _stickerState = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        static readonly (string Label, string Key)[] OutlineColors = { ("흰색", "white"), ("검정", "black"), ("없음", "none") };

        /// <summary>촬영 카드에 보정 줄 세 개 (밝기·대비·채도).</summary>
        void AddAdjustRows(StackPanel card)
        {
            Control Row(string label, string help, double min, double max, Func<double> get, Action<double> set)
            {
                var box = Ui.NumberCell(v => { set(Math.Clamp(v, min, max)); _settings.Save(); }, () => { });
                var sl = new Ui.EditSlider(min, max, v => box.Text = Ui.F(Math.Round(v, 2)), v => { set(Math.Round(v, 2)); box.Text = Ui.F(Math.Round(v, 2)); _settings.Save(); });
                sl.SetQuiet(get()); box.Text = Ui.F(get());
                _syncUi.Add(() => { sl.SetQuiet(get()); box.Text = Ui.F(get()); });
                return Ui.HelpRow(label, help, Ui.NumberWithSlider(box, sl.Slider), wide: true);
            }
            card.Children.Add(Row("밝기", "0 = 그대로. 찍은 PNG와 스티커에 함께 쓰여요", -1, 1, () => _settings.ShotBrightness, v => _settings.ShotBrightness = v));
            card.Children.Add(Row("대비", "1 = 그대로", 0, 2, () => _settings.ShotContrast, v => _settings.ShotContrast = v));
            card.Children.Add(Row("채도", "1 = 그대로 · 0 = 흑백", 0, 2, () => _settings.ShotSaturation, v => _settings.ShotSaturation = v));
        }

        void BuildSticker()
        {
            var sec = new Ui.FoldSection("스티커 만들기", false, help: "지금 카메라로 투명 배경을 촬영해, 아바타만 잘라 메신저 스티커·이모지 규격 크기에 맞추고 테두리를 둘러 PNG로 저장합니다. "
                + "카메라를 얼굴·상반신에 맞추고 표정을 선택한 뒤 누르세요. 위 촬영 보정(밝기·대비·채도)도 함께 사용됩니다.");
            var rows = new StackPanel();
            var spec = Ui.CellCombo(new ComboBox { ItemsSource = Sticker.Specs.Select(s => s.Label).ToList() });
            spec.SelectedIndex = Math.Max(0, Array.FindIndex(Sticker.Specs, s => s.Key == _settings.StickerSpec));
            spec.SelectionChanged += (_, __) => { _settings.StickerSpec = Sticker.Specs[Math.Max(0, spec.SelectedIndex)].Key; _settings.Save(); };
            rows.Children.Add(Ui.PropRow(Ui.Text("규격"), spec, null));
            var color = Ui.CellCombo(new ComboBox { ItemsSource = OutlineColors.Select(c => c.Label).ToList() });
            color.SelectedIndex = Math.Max(0, Array.FindIndex(OutlineColors, c => c.Key == _settings.StickerOutlineColor));
            color.SelectionChanged += (_, __) => { _settings.StickerOutlineColor = OutlineColors[Math.Max(0, color.SelectedIndex)].Key; _settings.Save(); };
            rows.Children.Add(Ui.PropRow(Ui.Text("테두리"), color, null));
            var wBox = Ui.NumberCell(v => { _settings.StickerOutline = (int)Math.Clamp(Math.Round(v), 0, 30); _settings.Save(); }, () => { });
            var wSl = new Ui.EditSlider(0, 30, v => wBox.Text = Ui.F(Math.Round(v)), v => { _settings.StickerOutline = (int)Math.Round(v); wBox.Text = Ui.F(_settings.StickerOutline); _settings.Save(); });
            wSl.SetQuiet(_settings.StickerOutline); wBox.Text = Ui.F(_settings.StickerOutline);
            rows.Children.Add(Ui.HelpRow("테두리 두께 (px)", "규격 크기 기준 픽셀이에요. 작은 규격(Twitch 56·28)은 비율대로 줄어요", Ui.NumberWithSlider(wBox, wSl.Slider), wide: true));
            _syncUi.Add(() =>
            {
                spec.SelectedIndex = Math.Max(0, Array.FindIndex(Sticker.Specs, s => s.Key == _settings.StickerSpec));
                color.SelectedIndex = Math.Max(0, Array.FindIndex(OutlineColors, c => c.Key == _settings.StickerOutlineColor));
                wSl.SetQuiet(_settings.StickerOutline); wBox.Text = Ui.F(_settings.StickerOutline);
            });
            sec.Body.Children.Add(Ui.Card(rows));
            var make = Ui.WideButton("스티커 만들기…", MakeSticker, "지금 카메라로 찍어 고른 규격의 투명 PNG로 저장해요", Glyph.Emoji, accent: true);
            make.Margin = new Thickness(0, 4, 0, 0);
            sec.Body.Children.Add(make);
            _stickerState.TextWrapping = TextWrapping.Wrap; _stickerState.Margin = new Thickness(2, 8, 0, 4);
            sec.Body.Children.Add(_stickerState);
            var frame = new Border { Child = _stickerPreview, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
            // 투명한 곳이 보이게 회색 바탕
            frame.Bg(Ui.KHover);
            _stickerPreview.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) frame.IsVisible = _stickerPreview.Source != null; };
            sec.Body.Children.Add(frame);
            _main.Children.Add(sec);
        }

        async void MakeSticker()
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 만들 수 있어요."); return; }
            if (_pendingSticker != null) { _ctx.Status("스티커를 만드는 중이에요."); return; }
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var spec = Sticker.Find(_settings.StickerSpec);
            var name = (_ctx.Doc?.Original.Name is string n && n.Length > 0 ? n : "avatar") + "_" + spec.Key + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Texts.T("스티커 저장"), SuggestedFileName = name, DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG 이미지") { Patterns = new[] { "*.png" } } },
            });
            if (file?.TryGetLocalPath() is not string path) return;
            _stickerOut = path;
            _pendingSticker = Path.Combine(Path.GetTempPath(), $"vrcproto_sticker_{Environment.ProcessId}_{DateTime.Now.Ticks}.png");
            // 규격 비율로 크게 찍는다 (잘라 낸 뒤에도 충분히 크게): 긴 쪽 1600, 2배 슈퍼샘플
            double k = 1600.0 / Math.Max(spec.W, spec.H);
            int cw = (int)Math.Round(spec.W * k), ch = (int)Math.Round(spec.H * k);
            _stickerState.Text = "찍는 중…"; _stickerState.Fg(Ui.KText2);
            _ctx.Render.Send(Msg.Make(Msg.Capture).Set("path", _pendingSticker).Set("width", cw).Set("height", ch).Set("transparent", true).Set("supersample", 2));
        }

        /// <summary>captureDone: 스티커 임시 촬영이면 만들어 저장하고 true.</summary>
        bool OnStickerCaptured(JNode m)
        {
            if (_pendingSticker == null || m["path"].AsString != _pendingSticker) return false;
            var tmp = _pendingSticker; var outPath = _stickerOut!;
            _pendingSticker = null;
            if (!m["ok"].AsBool) { _stickerState.Text = "촬영하지 못했습니다: " + m["error"].AsString; _stickerState.Fg(Ui.KCaution); return true; }
            var opt = new Sticker.Options
            {
                Spec = _settings.StickerSpec, OutlineWidth = _settings.StickerOutlineColor == "none" ? 0 : _settings.StickerOutline,
                OutlineColor = _settings.StickerOutlineColor == "black" ? new byte[] { 0, 0, 0 } : new byte[] { 255, 255, 255 },
                Brightness = _settings.ShotBrightness, Contrast = _settings.ShotContrast, Saturation = _settings.ShotSaturation,
            };
            _stickerState.Text = "다듬는 중…";
            _ = Task.Run(() =>
            {
                try
                {
                    var shot = Sticker.DecodePng(File.ReadAllBytes(tmp));
                    if (shot == null) return ("찍은 PNG를 읽지 못했어요.", (string?)null, false);
                    var list = Sticker.Make(shot, opt);
                    if (list.Count == 0) return ("화면에 아바타가 보이지 않아요. 카메라를 맞춘 뒤 다시 눌러 주세요.", null, false);
                    var spec = Sticker.Find(opt.Spec);
                    string? first = null; var parts = new System.Collections.Generic.List<string>(); bool over = false;
                    foreach (var (size, img) in list)
                    {
                        var bytes = Sticker.EncodePng(img);
                        var p = list.Count == 1 ? outPath : Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath) + "_" + size + ".png");
                        File.WriteAllBytes(p, bytes);
                        first ??= p;
                        bool big = spec.MaxKB > 0 && bytes.Length > spec.MaxKB * 1024;
                        over |= big;
                        parts.Add($"{img.W}×{img.H} · {bytes.Length / 1024.0:0.#} KB" + (big ? $" (제한 {spec.MaxKB} KB 넘음)" : ""));
                    }
                    return ($"저장했습니다 → {Path.GetFileName(first)}" + (list.Count > 1 ? $" 외 {list.Count - 1}장" : "") + "\n" + string.Join(" / ", parts)
                        + (over ? "\n용량이 넘으면 테두리를 줄이거나 채도를 낮추세요." : ""), first, over);
                }
                catch (Exception e) { return ("만들지 못했습니다: " + e.Message, null, true); }
                finally { try { File.Delete(tmp); } catch { } }
            }).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var (text, first, warn) = t.Result;
                _stickerState.Text = text; _stickerState.Fg(warn ? Ui.KCaution : Ui.KText2);
                if (first != null)
                {
                    try { using var fs = File.OpenRead(first); _stickerPreview.Source = new Bitmap(fs); } catch { _stickerPreview.Source = null; }
                    _ctx.Status("스티커를 만들었습니다 → " + first);
                }
            }));
            return true;
        }

        /// <summary>일반 촬영 뒤 보정 (값이 기본이면 아무것도 하지 않음). 끝나면 상태를 알린다.</summary>
        /// <param name="after">보정 뒤 작업 스레드에서 할 일 (세션 넣기)</param>
        void AdjustShot(string path, Action? after = null)
        {
            double b = _settings.ShotBrightness, c = _settings.ShotContrast, s = _settings.ShotSaturation;
            bool neutral = Sticker.IsNeutral(b, c, s);
            if (neutral && after == null) { _ctx.Status($"촬영했어요 → {path}"); _ctx.Saved(path); return; }
            _ctx.Status(neutral ? "촬영 마무리 중…" : "촬영 보정 중…");
            _ = Task.Run(() =>
            {
                string msg = $"촬영했어요 → {path}";
                try
                {
                    if (!neutral)
                    {
                        var img = Sticker.DecodePng(File.ReadAllBytes(path));
                        if (img == null) msg = "촬영했습니다 (보정은 이 PNG 형식에 사용할 수 없었습니다) → " + path;
                        else { Sticker.Adjust(img, b, c, s); File.WriteAllBytes(path, Sticker.EncodePng(img)); msg = $"촬영하고 보정했어요 → {path}"; }
                    }
                }
                catch (Exception e) { msg = "촬영했지만 보정하지 못했습니다: " + e.Message; }
                try { after?.Invoke(); }
                catch (Exception e) { msg += " (세션을 넣지 못했습니다: " + e.Message + ")"; }
                return msg;
            }).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() => { _ctx.Status(t.Result); _ctx.Saved(path); }));
        }
    }
}
