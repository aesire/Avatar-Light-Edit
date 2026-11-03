// 화면 확인용 찍기 (개발용): dotnet VrcProtoAv.dll --ui-shots <x.ir.json> <출력 폴더> [--ui-sizes 1000x600,1600x960] [--ui-pane 470]
// 창을 화면 밖에 활성화 없이 띄우고 렌더러가 장면을 불러오기를 기다린 뒤(머티리얼 스키마·환경 값이 있어야 페이지가 실제와 같다) 페이지마다 쉬운 모드·전문가 모드 내용을 끝까지 PNG로 저장한 뒤 닫는다.
// --ui-sizes를 주면 크기마다 창 전체(보이는 그대로)도 찍는다 (win_<크기>_<페이지>.png) — 좁은 창에서 글이 잘리거나 줄이 깨지는지 본다.
// --vr-panel-shots: VR 에디터 창 시험 (헤드셋 없이) — VR 창 모드를 켜고 창을 그려 렌더러에 보내 답을 받는지, 그린 화면(vr_panel.png),
//   레이저 입력(vrPointer와 같은 길)으로 사이드바 항목·단추를 눌러 실제로 바뀌는지 본다. 결과는 vr_panel.txt.
// 사용자 데스크톱에 키·클릭을 보내지 않고 UI를 눈으로 확인하려고 만들었다. 설정 파일은 바꾸지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        string? _shotsDir;
        readonly List<(int W, int H)> _shotSizes = new List<(int, int)>();

        /// <summary>생성자에서: --ui-shots면 화면 밖·비활성으로 띄울 준비를 한다.</summary>
        void PrepareShots(string[] args)
        {
            int i = Array.IndexOf(args, "--ui-shots");
            if (i < 0 || i + 2 >= args.Length) return;
            if (Updater.CurrentText != null && !RunMode.Test) return;   // 배포본은 --test를 함께 줄 때만 (2026-11-01)
            _shotsDir = Path.GetFullPath(args[i + 2]);
            int s = Array.IndexOf(args, "--ui-sizes");
            if (s >= 0 && s + 1 < args.Length)
                foreach (var part in args[s + 1].Split(','))
                {
                    var wh = part.Split('x');
                    if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h)) _shotSizes.Add((w, h));
                }
            int p = Array.IndexOf(args, "--ui-pane");
            if (p >= 0 && p + 1 < args.Length && double.TryParse(args[p + 1], out var pw)) { _settings.PaneWidth = pw; ApplyPaneSideLater = true; }
            ShowActivated = false;
            FloatHost.Quiet = true;
            Settings.ReadOnly = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-30000, -30000);
            Width = 1400; Height = 900;
            Opened += async (_, __) =>
            {
                if (ApplyPaneSideLater) ApplyPaneSide();
                await Task.Delay(1500);
                await TakeShots();
                File.WriteAllLines(Path.Combine(_shotsDir!, "clipped_text.txt"), _clipped);
                if (Texts.Lang != "ko") File.WriteAllLines(Path.Combine(_shotsDir!, "untranslated_text.txt"), _untranslated);
                Close();
            };
        }
        bool ApplyPaneSideLater;

        // 잘린 글자 찾기 (2026-11-01 사용성 점검): 말줄임이 걸렸거나 부모 밖으로 나가 가로로 잘린 글을 clipped_text.txt에 모은다
        readonly SortedSet<string> _clipped = new SortedSet<string>(StringComparer.Ordinal);
        void ScanClipped(Control root, string shot)
        {
            foreach (var t in root.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!t.IsEffectivelyVisible || string.IsNullOrWhiteSpace(t.Text) || t.Bounds.Width <= 0) continue;
                bool cut = t.TextLayout.TextLines.Any(l => l.HasCollapsed || l.HasOverflowed);
                if (!cut && t.TextWrapping == TextWrapping.NoWrap)
                {
                    // 콤보 상자처럼 칸이 좁아 말줄임 없이 그냥 잘리는 글 ("Opaque"가 "Op"로): 원래 너비와 비교
                    var whole = new TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight };
                    whole.Measure(Size.Infinity);
                    cut = whole.DesiredSize.Width > t.Bounds.Width + 1;
                }
                if (!cut)
                    foreach (var a in t.GetVisualAncestors().OfType<Control>())
                    {
                        if (a == root) break;
                        if (a is Avalonia.Controls.Presenters.ScrollContentPresenter) break;
                        if (!a.ClipToBounds) continue;
                        var right = t.TranslatePoint(new Point(t.Bounds.Width, 0), a);
                        if (right != null && right.Value.X > a.Bounds.Width + 1) cut = true;
                        break;
                    }
                if (cut) _clipped.Add($"{shot}\t{t.Text.Replace('\n', ' ')}");
            }
        }

        // 번역 안 된 글 찾기 (2026-11-03): 한국어가 아닌 화면에서 한글이 남은 글과 툴팁(보일 때 바뀔 글)을 untranslated_text.txt에 모은다
        readonly SortedSet<string> _untranslated = new SortedSet<string>(StringComparer.Ordinal);

        void ScanUntranslated(Control root, string shot)
        {
            if (Texts.Lang == "ko") return;
            static bool Hangul(string s) => s.Any(c => c >= '가' && c <= '힣');
            foreach (var c in root.GetVisualDescendants().OfType<Control>().Prepend(root))
            {
                if (!c.IsEffectivelyVisible) continue;
                if (c is TextBlock t && !string.IsNullOrEmpty(t.Text) && Hangul(t.Text)) _untranslated.Add($"{shot}\ttext\t{t.Text.Replace("\n", "\\n")}");
                if (ToolTip.GetTip(c) is string tip && Hangul(Texts.T(tip))) _untranslated.Add($"{shot}\ttip\t{tip.Replace("\n", "\\n")}");
            }
            File.WriteAllLines(Path.Combine(_shotsDir!, "untranslated_text.txt"), _untranslated);   // 찍기가 중간에 끝나도 남게
        }

        void Save(Control target, string name, string fill, int maxHeight = 2000)
        {
            ScanClipped(target, name);
            ScanUntranslated(target, name);
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(target.Bounds.Width)), Math.Clamp((int)Math.Ceiling(target.Bounds.Height), 1, maxHeight));   // 아주 긴 목록은 위쪽만
            using var bmp = new RenderTargetBitmap(size);
            // 투명 바탕에 그리면 글자가 깨져 보여 잠깐 작업 창 색을 깐다
            var panel = target as Panel; var oldBg = panel?.Background;
            if (panel != null && this.TryFindResource(fill, ActualThemeVariant, out var bg) && bg is IBrush b) panel.Background = b;
            bmp.Render(target);
            if (panel != null) panel.Background = oldBg;
            bmp.Save(Path.Combine(_shotsDir!, name));
        }

        static string Safe(string k) => string.Concat(k.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

        /// <summary>페이지(또는 탭)를 위쪽 공통 부분과 지금 탭 내용을 이어 붙여 끝까지 찍는다 (탭 내용은 스크롤 안이라 따로).</summary>
        void SaveTab(Control page, string name)
        {
            ScanClipped(page, name);
            ScanUntranslated(page, name);
            var sv = page.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible).OrderByDescending(x => x.Bounds.Width * x.Bounds.Height).FirstOrDefault();
            var head = page.GetVisualDescendants().OfType<SubTabs>().FirstOrDefault()?.Children.OfType<Control>().FirstOrDefault();
            if (sv?.Content is not Control body) { Save(page, name, Ui.KPanel, 6000); return; }
            if (head == null) { Save(body, name, Ui.KPanel, 6000); return; }
            // 두 장을 위아래로: 공통 부분 + 탭 내용
            int w = (int)Math.Ceiling(Math.Max(head.Bounds.Width, body.Bounds.Width));
            int h1 = (int)Math.Ceiling(head.Bounds.Height), h2 = Math.Min(6000, (int)Math.Ceiling(body.Bounds.Height));
            using var a = new RenderTargetBitmap(new PixelSize(Math.Max(1, w), Math.Max(1, h1)));
            using var b = new RenderTargetBitmap(new PixelSize(Math.Max(1, w), Math.Max(1, h2)));
            var panel = body as Panel; var oldBg = panel?.Background;
            if (panel != null && this.TryFindResource(Ui.KPanel, ActualThemeVariant, out var bg) && bg is IBrush br) panel.Background = br;
            a.Render(head); b.Render(body);
            if (panel != null) panel.Background = oldBg;
            using var all = new RenderTargetBitmap(new PixelSize(Math.Max(1, w), Math.Max(1, h1 + h2)));
            using (var ctx = all.CreateDrawingContext())
            {
                if (this.TryFindResource(Ui.KPanel, ActualThemeVariant, out var fill) && fill is IBrush fb) ctx.FillRectangle(fb, new Rect(0, 0, w, h1 + h2));
                ctx.DrawImage(a, new Rect(0, 0, w, h1));
                ctx.DrawImage(b, new Rect(0, h1, w, h2));
            }
            all.Save(Path.Combine(_shotsDir!, name));
        }

        async Task VrPanelShots()
        {
            var lines = new List<string>();
            int fail = 0;
            void Check(bool ok, string what) { lines.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) fail++; }
            int shown = 0;
            // VR 창 모드를 직접 켠다 (SteamVR 없이): 배율 · 미리보기 접기 · 화면 보내기 — 렌더러가 받으면 vrPanelShown으로 답한다
            SetVrPanelMode(true);
            OnVrPanelsOpen(JNode.Obj().Set("editor", true).Set("hand", true));   // VR 안에서 B/Y로 연 것처럼
            var feed = _vrFeeds[VrMainId];
            feed.ShownHook = seq => shown++;
            await Task.Delay(2500);
            var last = feed.LastSent;
            Check(_preview.IsVisible == false && _vrButton.Parent == _undoBtn.Parent, "preview folded, VR button in title bar");
            Check(_uiScaleBox.LayoutTransform is ScaleTransform st && Math.Abs(st.ScaleX - _settings.VrUiScale) < 1e-6, $"UI scale {_settings.VrUiScale}");
            Check(last != null && last["width"].AsInt > 0, $"frame sent {last?["width"].AsInt}x{last?["height"].AsInt} map {last?["map"].AsString}");
            Check(shown > 0, $"renderer acknowledged frames ({shown})");
            if (last != null) SavePanelPng(last["map"].AsString, last["width"].AsInt, last["height"].AsInt, Path.Combine(_shotsDir!, "vr_panel.png"));
            // 레이저로 사이드바 "머티리얼" 누르기 (Border의 PointerReleased)
            async Task ClickIn(VrPanelFeed f, Control c)
            {
                var w = f.Window;
                var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w) ?? default;
                float u = (float)(p.X / w.ClientSize.Width), v = (float)(p.Y / w.ClientSize.Height);
                f.Pointer("move", u, v, 0); await Task.Delay(80);
                f.Pointer("down", u, v, 0); await Task.Delay(80);
                f.Pointer("up", u, v, 0); await Task.Delay(300);
            }
            Task Click(Control c) => ClickIn(feed, c);
            var start = _side.Current;
            await Click(_side.ItemOf("material")!);
            Check(_side.Current == "material", $"laser click on sidebar: {start} → {_side.Current}");
            await Click(_side.ItemOf("pose")!);
            Check(_side.Current == "pose", $"laser click on sidebar again → {_side.Current}");
            // 진짜 단추 (Button.Click): 작업 창 왼쪽·오른쪽 바꾸기 → 두 번 눌러 되돌린다
            bool side = _settings.PaneOnRight;
            await Click(_sideBtn);
            Check(_settings.PaneOnRight != side, "laser click on a Button (pane side toggled)");
            await Click(_sideBtn);
            Check(_settings.PaneOnRight == side, "second click toggles back");
            // 휠: 지금 페이지의 스크롤이 내려간다
            ScrollViewer? Scrollable() => _pages[_side.Current!].GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible && x.Extent.Height > x.Viewport.Height + 10).OrderByDescending(x => x.Bounds.Width * x.Bounds.Height).FirstOrDefault();
            foreach (var key in new[] { "pose", "material", "shapes", "environment" })
            {
                if (_side.Current != key) await Click(_side.ItemOf(key)!);
                if (Scrollable() != null) break;
            }
            var sv = _pages[_side.Current!].GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible && x.Extent.Height > x.Viewport.Height + 10).OrderByDescending(x => x.Bounds.Width * x.Bounds.Height).FirstOrDefault();
            if (sv != null)
            {
                var c = sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2), this) ?? default;
                double before = sv.Offset.Y;
                feed.Pointer("move", (float)(c.X / ClientSize.Width), (float)(c.Y / ClientSize.Height), 0);
                feed.Pointer("wheel", (float)(c.X / ClientSize.Width), (float)(c.Y / ClientSize.Height), -3);
                await Task.Delay(300);
                Check(sv.Offset.Y > before, $"laser wheel scrolls ({before:0} → {sv.Offset.Y:0})");
            }
            else lines.Add("skip wheel (no scrollable list)");
            await Task.Delay(300);
            if (last != null) SavePanelPng(last["map"].AsString, last["width"].AsInt, last["height"].AsInt, Path.Combine(_shotsDir!, "vr_panel_after.png"));
            feed.Pointer("leave", 0, 0, 0);
            int before2 = shown;
            await Task.Delay(1200);
            Check(shown > before2, "keeps sending frames (idle 0.5 s refresh)");

            // ④ 팝업을 창 안에: 머티리얼 페이지의 드롭다운을 레이저로 누르면 목록이 창 안(오버레이 층)에 열리고 VR 화면에도 찍힌다
            if (_ctx.Doc != null && _ctx.Doc.Original.Renderers.Count > 0)
            {
                var r0 = _ctx.Doc.Original.Renderers.OrderByDescending(x => _ctx.Doc.Original.FindMesh(x.Mesh)?.VertexCount ?? 0).First();
                if (r0.Materials.Count > 0) _ctx.Select(r0.Id, r0.Materials[0]);
            }
            await Click(_side.ItemOf("material")!);
            await Task.Delay(400);
            var combo = _pages["material"].GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(x => x.IsEffectivelyVisible && x.IsEffectivelyEnabled);
            if (combo != null)
            {
                await Click(combo);
                await Task.Delay(500);
                var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(this);
                bool inWindow = combo.IsDropDownOpen && layer != null && layer.GetVisualDescendants().OfType<ComboBoxItem>().Any();
                Check(inWindow, $"dropdown opens inside the window (open {combo.IsDropDownOpen}, overlay popups {_settings.VrOverlayPopups})");
                await Task.Delay(700);
                if (feed.LastSent is JNode lp) SavePanelPng(lp["map"].AsString, lp["width"].AsInt, lp["height"].AsInt, Path.Combine(_shotsDir!, "vr_panel_popup.png"));
                // 목록 첫 항목을 레이저로 고르면 닫힌다
                var item = layer?.GetVisualDescendants().OfType<ComboBoxItem>().FirstOrDefault(x => x.IsEffectivelyVisible);
                if (item != null) await Click(item);
                Check(!combo.IsDropDownOpen, "laser click on a list item closes the dropdown");
            }
            else lines.Add("skip dropdown (no combo box on material page)");
            // ④ 가상 키보드: 입력 칸을 레이저로 누르면 키보드를 띄우라고 보내고, 친 글은 입력 칸에 바로 · 닫으면 처음 글로 · 끝내면 Enter
            // 가운데를 누르면 그 칸이 맞는 것만 (스크롤에 반쯤 가린 칸은 다른 것을 누른다)
            bool Pressable(TextBox x)
            {
                var c = x.TranslatePoint(new Point(x.Bounds.Width / 2, x.Bounds.Height / 2), this);
                return c is Point pt && Avalonia.Input.InputExtensions.InputHitTest(this, pt) is Visual v && (v == x || v.FindAncestorOfType<TextBox>() == x);
            }
            var box = _pages["material"].GetVisualDescendants().OfType<TextBox>().FirstOrDefault(x => x.IsEffectivelyVisible && x.IsEffectivelyEnabled && !x.IsReadOnly && Pressable(x));
            if (box != null)
            {
                var original = box.Text ?? "";
                await Click(box);
                await Task.Delay(300);
                var kb = feed.LastKeyboard;
                lines.Add($"info text box {box.Name}/{box.GetType().Name} '{original}' focused={box.IsFocused} within={box.IsKeyboardFocusWithin} manager={FocusManager?.GetFocusedElement()?.GetType().Name} parent={(box.Parent as Control)?.GetType().Name}");
                Check(kb != null && kb["show"].AsBool && kb["text"].AsString == original, $"laser click on a text box → SteamVR keyboard requested (\"{kb?["text"].AsString}\")");
                feed.KeyboardText(original + "7", false, false);
                Check(box.Text == original + "7", "keyboard text goes into the box while typing");
                feed.KeyboardText(original + "7", false, true);
                Check(box.Text == original, "keyboard closed → original text back");
                await Click(box);
                await Task.Delay(300);
                bool entered = false;
                box.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) entered = true; }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);   // 입력 칸이 Enter를 먼저 처리해도
                feed.KeyboardText(original, true, false);
                Check(entered && box.Text == original, "keyboard done → Enter commits");
                // 입력 칸이 아닌 곳을 누르면 키보드를 닫으라고 보낸다
                await Click(_side.ItemOf("pose")!);
                await Task.Delay(300);
                Check(feed.LastKeyboard?["hide"].AsBool == true || feed.LastKeyboard?["show"].AsBool == true, $"keyboard state after clicking elsewhere ({feed.LastKeyboard?.ToJson()})");
            }
            else lines.Add("skip keyboard (no text box on material page)");

            // 빠른 설정 (B/Y 길게): 손에 붙는 작은 창도 따로 보내고, 레이저로 "개인 거울"을 누르면 켜지고 다시 누르면 되돌림
            Check(_vrFeeds.TryGetValue("hand", out var hf) && _vrHand != null, "quick settings (hand menu) feed started");
            if (hf != null && _vrHand != null)
            {
                await Task.Delay(500);
                Check(hf.LastSent != null && Math.Abs(hf.LastSent["meters"].AsFloat - 0.05f * _vrHand.Columns) < 1e-4, $"hand menu frame {hf.LastSent?["width"].AsInt}x{hf.LastSent?["height"].AsInt}");
                bool before3 = _settings.VrPersonalMirror;
                await ClickIn(hf, _vrHand.ButtonOf("personal")!);
                Check(_settings.VrPersonalMirror != before3, $"laser click on hand menu \"개인 거울\" ({before3} → {_settings.VrPersonalMirror})");
                await ClickIn(hf, _vrHand.ButtonOf("personal")!);
                Check(_settings.VrPersonalMirror == before3, $"second click brings it back ({_settings.VrPersonalMirror})");
                await Task.Delay(400);
                if (hf.LastSent is JNode hl) SavePanelPng(hl["map"].AsString, hl["width"].AsInt, hl["height"].AsInt, Path.Combine(_shotsDir!, "vr_hand_menu.png"));
                // 닫히면 그리지 않는다
                OnVrPanelsOpen(JNode.Obj().Set("editor", true).Set("hand", false));
                int seqBefore = hf.LastSent?["frame"].AsInt ?? 0;
                await Task.Delay(800);
                Check((hf.LastSent?["frame"].AsInt ?? 0) == seqBefore, "closed hand menu is not drawn");
            }
            // VR 카메라 조작판 (2026-10-25): 카메라를 켜면 그려 보내고, 레이저로 "타이머"·"고정"을 누르면 설정이 바뀐다 · 끄면 안 그림
            Check(_vrFeeds.TryGetValue("camera", out var cf) && _vrCamMenu != null, "camera menu feed started");
            if (cf != null && _vrCamMenu != null)
            {
                bool camWas = _settings.VrCamera, lockWas = _settings.VrCameraLock; int timerWas = _settings.VrCameraTimer;
                SetVrCamera(true);
                _vrCamState = JNode.Obj().Set("on", true).Set("recording", true).Set("seconds", 75).Set("fov", 45).Set("shots", 3).Set("videos", 1);   // 녹화 중 모습으로
                _vrCamMenu.Refresh();
                await Task.Delay(600);
                Check(cf.LastSent != null && Math.Abs(cf.LastSent["meters"].AsFloat - 0.12f) < 1e-4, $"camera menu frame {cf.LastSent?["width"].AsInt}x{cf.LastSent?["height"].AsInt}");
                await ClickIn(cf, _vrCamMenu.ButtonOf("timer")!);
                Check(_settings.VrCameraTimer == (timerWas switch { 0 => 3, 3 => 5, 5 => 10, _ => 0 }), $"laser click on camera \"타이머\" ({timerWas} → {_settings.VrCameraTimer})");
                await ClickIn(cf, _vrCamMenu.ButtonOf("lock")!);
                Check(_settings.VrCameraLock != lockWas, $"laser click on camera \"고정\" ({lockWas} → {_settings.VrCameraLock})");
                float sizeWas = _settings.VrCameraScale;
                await ClickIn(cf, _vrCamMenu.SizeButton(true));
                Check(_settings.VrCameraScale > sizeWas, $"laser click on camera size + ({sizeWas:0.00} → {_settings.VrCameraScale:0.00})");
                await Task.Delay(400);
                if (cf.LastSent is JNode cl) SavePanelPng(cl["map"].AsString, cl["width"].AsInt, cl["height"].AsInt, Path.Combine(_shotsDir!, "vr_camera_menu.png"));
                _settings.VrCameraTimer = timerWas; _settings.VrCameraLock = lockWas; _settings.VrCameraScale = sizeWas;
                SetVrCamera(false);
                int cseq = cf.LastSent?["frame"].AsInt ?? 0;
                await Task.Delay(800);
                Check((cf.LastSent?["frame"].AsInt ?? 0) == cseq, "camera menu is not drawn while the camera is off");
                if (camWas) SetVrCamera(true);
                _vrCamState = JNode.Obj();
            }
            // 클릭 지연: 누르고 뗀 뒤 다음 화면을 보낼 때까지 — 아무것도 바뀌지 않는 클릭(지금 페이지 항목)으로 5번, 가운데 값 (예전 50 ms 주기 + 그리기)
            {
                var times = new List<long>();
                feed.Trace = new List<string>();
                var here = _side.ItemOf(_side.Current!)!;
                var c0 = here.TranslatePoint(new Point(here.Bounds.Width / 2, here.Bounds.Height / 2), this) ?? default;
                float cu = (float)(c0.X / ClientSize.Width), cv = (float)(c0.Y / ClientSize.Height);
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(700);   // 앞 화면이 다 올라간 뒤
                    int s0 = feed.LastSent?["frame"].AsInt ?? 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    feed.Pointer("down", cu, cv, 0);
                    feed.Pointer("up", cu, cv, 0);
                    while ((feed.LastSent?["frame"].AsInt ?? 0) == s0 && sw.ElapsedMilliseconds < 1000) await Task.Delay(1);
                    times.Add(sw.ElapsedMilliseconds);
                    await Task.Delay(100);
                    lines.Add($"info   draw {feed.DrawMs:0} ms · ack {feed.AckMs:0} ms");
                }
                if (feed.Trace != null) File.WriteAllLines(Path.Combine(_shotsDir!, "vr_trace.txt"), feed.Trace);
                times.Sort();
                lines.Add($"info click → next frame sent: {string.Join(", ", times)} ms");
                Check(times[2] < 120, $"click shows up quickly (median {times[2]} ms)");
            }

            // ② 떠 있는 창: 띄우면 따로 화면을 보내고(너비 = 창 너비 × 글자 배율 × 메인 m/너비), 레이저로 그 창 단추를 누르고, 제목 줄을 끌어도 데스크톱 자리는 그대로, 닫으면 빠진다
            int clicks = 0;
            var testBtn = new Button { Content = "VR 시험 단추", Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            testBtn.Click += (_, __) => clicks++;
            var fp = _floats.Show("vrtest", null, "VR 시험 창", new StackPanel { Children = { testBtn, new TextBlock { Text = "떠 있는 창도 VR에 따로 떠요", Margin = new Thickness(20, 0) } } }, false, new Size(420, 260));
            await Task.Delay(1500);
            var fid = VrFloatId(fp);
            Check(_vrFeeds.TryGetValue(fid, out var ff), $"float feed started ({fid})");
            if (ff != null)
            {
                int fshown = 0;
                ff.ShownHook = seq => fshown++;
                await Task.Delay(800);
                var fl = ff.LastSent;
                double expect = VrFloatMeters(fp);
                Check(fl != null && fl["width"].AsInt > 0 && Math.Abs(fl["meters"].AsFloat - expect) < 0.01, $"float frame {fl?["width"].AsInt}x{fl?["height"].AsInt} · {fl?["meters"].AsFloat:0.000} m (expect {expect:0.000})");
                Check(fshown > 0, $"renderer acknowledged float frames ({fshown})");
                await ClickIn(ff, testBtn);
                Check(clicks == 1, $"laser click on a button in the float ({clicks})");
                // 제목 줄 끌기는 레이저로는 하지 않는다 (데스크톱 자리 그대로)
                var pos = fp.Position;
                ff.Pointer("move", 0.3f, 18f / (float)fp.ClientSize.Height, 0);
                ff.Pointer("down", 0.3f, 18f / (float)fp.ClientSize.Height, 0);
                ff.Pointer("move", 0.6f, 0.6f, 0);
                ff.Pointer("up", 0.6f, 0.6f, 0);
                await Task.Delay(200);
                Check(fp.Position == pos, "laser drag on the float title bar does not move the desktop window");
                if (fl != null) SavePanelPng(fl["map"].AsString, fl["width"].AsInt, fl["height"].AsInt, Path.Combine(_shotsDir!, "vr_panel_float.png"));
                // 렌더러가 정한 자리를 저장하고 다음 화면에 place로 돌려준다
                OnVrPanelPlaced(JNode.Obj().Set("id", fid).Set("state", JNode.Obj().Set("pos", JNode.Arr().Add(0.5f).Add(1.3f).Add(-0.7f)).Set("rot", JNode.Arr().Add(0f).Add(0.2f).Add(0f).Add(0.98f))
                    .Set("scale", 1.4f).Set("alpha", 0.75f).Set("hidden", false).Set("mode", "wrist").Set("localPos", JNode.Arr().Add(0f).Add(0.1f).Add(0f)).Set("localRot", JNode.Arr().Add(0f).Add(0f).Add(0f).Add(1f))));
                await Task.Delay(800);
                var saved = ff.LastSent?["state"];
                Check(saved != null && Math.Abs(saved["pos"][0].AsFloat - 0.5f) < 1e-4 && saved["mode"].AsString == "wrist" && Math.Abs(saved["scale"].AsFloat - 1.4f) < 1e-4, "saved VR window state is sent back with frames");
                // 막대의 "닫기" → 떠 있는 창을 닫는다 (작업 창에 다시 붙이기와 같은 길)
                bool docked = false;
                fp.DockRequested += () => { docked = true; _floats.Close(fp); };
                OnVrPanelAction(JNode.Obj().Set("id", fid).Set("action", "close"));
                await Task.Delay(200);
                Check(docked, "VR bar close → float dock requested");
            }
            // 다른 페이지로 가면 (핀 없는 창은 자기 페이지에서만 — 이 창은 page 없음이라 그대로), 닫으면 빠진다
            if (_floats.Floats.Contains(fp)) _floats.Close(fp);
            await Task.Delay(700);
            Check(!_vrFeeds.ContainsKey(fid), "closed float → its VR panel removed");
            SetVrPanelMode(false);
            Check(_preview.IsVisible && _vrButton.Parent != _undoBtn.Parent && _uiScaleBox.LayoutTransform == null, "VR panel mode off → layout restored");
            lines.Add(fail == 0 ? "VR panel: all ok" : $"VR panel: {fail} failed");
            File.WriteAllLines(Path.Combine(_shotsDir!, "vr_panel.txt"), lines);
            File.WriteAllText(Path.Combine(_shotsDir!, "vr_log.txt"), _log.Text ?? "");
            foreach (var l in lines) Console.WriteLine(l);
        }

        /// <summary>공유 메모리의 BGRA 화면을 PNG로 (보낸 그대로인지 눈으로 본다).</summary>
        static void SavePanelPng(string map, int w, int h, string path)
        {
            using var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(map);
            using var view = mm.CreateViewAccessor(0, (long)w * h * 4);
            var bytes = new byte[(long)w * h * 4];
            view.ReadArray(0, bytes, 0, bytes.Length);
            using var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var fb = wb.Lock()) System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
            wb.Save(path);
        }

        async Task TakeShots()
        {
            Directory.CreateDirectory(_shotsDir!);
            for (int t = 0; t < 120 && (_ctx.SceneBounds.IsNull || _ctx.EnvironmentSchema.Count == 0); t++) await Task.Delay(500);
            await Task.Delay(1000);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--shapes-bench") >= 0) { await ShapesBench(); return; }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--vr-panel-shots") >= 0) { await VrPanelShots(); return; }
            if (_ctx.Doc != null && _ctx.Doc.Original.Renderers.Count > 0)
            {
                // 머티리얼 페이지: 가장 큰 메시(대개 몸)의 첫 머티리얼
                var r = _ctx.Doc.Original.Renderers.OrderByDescending(x => _ctx.Doc.Original.FindMesh(x.Mesh)?.VertexCount ?? 0).First();
                if (r.Materials.Count > 0) _ctx.Select(r.Id, r.Materials[0]);
            }
            bool keep = _settings.ExpertMode;
            foreach (var expert in new[] { false, true })
            {
                SetExpert(expert, save: false);
                foreach (var key in _pages.Keys.Where(k => k != "log"))
                {
                    _side.Select(key);
                    await Task.Delay(300);
                    var page = _pages[key];
                    // 페이지 안 탭이 있으면 탭마다 (탭 위 공통 부분 + 탭 내용)
                    var tabs = page.GetVisualDescendants().OfType<SubTabs>().FirstOrDefault();
                    if (tabs == null) { SaveTab(page, $"{key}_{(expert ? "expert" : "easy")}.png"); continue; }
                    var keepTab = tabs.Current;
                    foreach (var k in tabs.Keys.ToList())
                    {
                        if (!tabs.IsShown(k)) continue;
                        tabs.Select(k);
                        await Task.Delay(250);
                        SaveTab(page, $"{key}_{Safe(k)}_{(expert ? "expert" : "easy")}.png");
                    }
                    if (keepTab != null) tabs.Select(keepTab);
                }
                // 스튜디오 탭마다, 표정 탭은 트래킹을 켠 화면(외부 앱)도
                _side.Select("studio");
                var mode = expert ? "expert" : "easy";
                foreach (var t in StudioPanel.TabKeys)
                {
                    _studioPanel!.ShowTab(t);
                    await Task.Delay(500);
                    SaveTab(_studioPanel, $"studio_{t}_{mode}.png");
                }
                _studioPanel!.ShowTab("face");
                _exprPanel.TrackingSource = 2;
                await Task.Delay(600);
                SaveTab(_studioPanel, $"studio_face_on_{mode}.png");
                _exprPanel.TrackingSource = 0;
                _studioPanel.ShowTab("bg");
            }
            // 창 크기마다 창 전체 (전문가 모드 — 보이는 것이 가장 많다)
            SetExpert(true, save: false);
            // 화면 배치 두 가지 모두 (기존 classic · 새 clean) — 설정 파일은 바꾸지 않는다
            var layoutKeep = _settings.LayoutStyle;
            foreach (var style in new[] { "classic", "clean" })
            {
                _settings.LayoutStyle = style; ApplyLayoutStyle();
                foreach (var (w, h) in _shotSizes)
                {
                    Width = w; Height = h;
                    await Task.Delay(400);
                    foreach (var key in _pages.Keys)
                    {
                        _side.Select(key);
                        await Task.Delay(300);
                        Save((Control)Content!, style == "classic" ? $"win_{w}x{h}_{key}.png" : $"win_clean_{w}x{h}_{key}.png", Ui.KBase);
                    }
                }
            }
            // 세밀 조정 막대: 머티리얼 첫 슬라이더를 넘겨 두 배치에서 찍고, 한 칸 올리기 시험 (rail_test.txt)
            if (_rail != null)
            {
                var railKeep = _rail.IsOpen;
                var lines = new List<string>();
                _side.Select("material"); await Task.Delay(400);
                var sl = _pages["material"].GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => Ui.EditOf(s) != null && s.IsEffectivelyVisible);
                if (sl != null) Ui.OfferFine(Ui.EditOf(sl)!.Fine()); else lines.Add("FAIL no slider");
                _rail.SetOpen(true);
                foreach (var style in new[] { "classic", "clean" })
                {
                    _settings.LayoutStyle = style; ApplyLayoutStyle();
                    await Task.Delay(400);
                    Save((Control)Content!, $"rail_{style}.png", Ui.KBase);
                }
                if (sl != null) lines.Add(_rail.SelfTest());
                _rail.SetOpen(false); await Task.Delay(300);
                Save((Control)Content!, "rail_closed_clean.png", Ui.KBase);
                // 다이얼 모양: 머티리얼·블렌드쉐이프를 찍고, 머티리얼 다이얼 하나를 가짜 포인터로 끌어 값이 바뀌고 저장되는지 (Slider 안쪽 처리가 다이얼 템플릿에서 터지지 않는지)
                _rail.SetOpen(true);
                Hardware.Apply(true, _settings);   // 다이얼 모드 (다이얼 · 장비 키 · 로커 스위치)
                _side.Select("material"); await Task.Delay(500);
                Save((Control)Content!, "dial_material.png", Ui.KBase);
                try
                {
                    var dsl = _pages["material"].GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => Ui.EditOf(s) != null && s.IsEffectivelyVisible && s.Maximum > s.Minimum);
                    var dial = dsl?.GetVisualDescendants().OfType<Dial>().FirstOrDefault();
                    if (dsl == null || dial == null) lines.Add("FAIL no dial template");
                    else
                    {
                        var es = Ui.EditOf(dsl)!;
                        double v0 = es.Fine().Get();
                        var ptr = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                        var at = dial.TranslatePoint(new Point(17, 17), this) ?? new Point();
                        var down = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
                        var move = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.Other);
                        var up = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
                        bool dir = v0 < (dsl.Minimum + dsl.Maximum) / 2;   // 가운데보다 작으면 올리고 크면 내린다
                        var to = at + new Point(0, dir ? -40 : 40);
                        dial.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(dial, ptr, this, at, 0, down, Avalonia.Input.KeyModifiers.None, 1));
                        dial.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, dial, ptr, this, to, 1, move, Avalonia.Input.KeyModifiers.None));
                        dial.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(dial, ptr, this, to, 2, up, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
                        await Task.Delay(200);
                        double v1 = es.Fine().Get();
                        bool docChanged = _ctx.Doc != null && _ctx.Doc.CanUndo;
                        lines.Add($"{(Math.Abs(v1 - v0) > 1e-6 && docChanged ? "ok" : "FAIL")} dial drag {Ui.GuessName(dsl)}: {Ui.F(v0)} → {Ui.F(v1)} · 저장됨 {docChanged} · 세밀 막대 {_rail.Target?.Name}");
                        es.Fine().Commit(v0);
                    }
                }
                catch (Exception ex) { lines.Add("FAIL dial drag " + ex.GetType().Name + ": " + ex.Message); }
                Save((Control)Content!, "dial_material_after.png", Ui.KBase);
                _side.Select("shapes"); await Task.Delay(500);
                Save((Control)Content!, "dial_shapes.png", Ui.KBase);
                _side.Select("pose"); await Task.Delay(300);
                var ptabs = _pages["pose"].GetVisualDescendants().OfType<SubTabs>().FirstOrDefault();
                ptabs?.Select("dials"); await Task.Delay(400);
                Save((Control)Content!, "dial_pose.png", Ui.KBase);
                ptabs?.Select("presets");
                // 쉐이프키 묶음 다이얼: 블렌드쉐이프 페이지에서 쉐이프키를 고르지 않은 채 첫 묶음 → 다이얼, 휠 한 칸 → 미리보기 + 쉬면 저장
                _rail.Clear();
                _side.Select("shapes"); await Task.Delay(400);
                _blendPanel.FocusFirstGroupForTest(); await Task.Delay(400);
                Save((Control)Content!, "rail_group.png", Ui.KBase);
                try
                {
                    var gd = _rail.FirstGroupDial; var gs = _rail.FirstGroupShape;
                    if (!_rail.InGroupView || gd == null || gs == null) lines.Add($"FAIL group view {_rail.InGroupView} {gd != null}");
                    else
                    {
                        double g0 = gs.Get();
                        var ptr = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                        var at = gd.TranslatePoint(new Point(46, 43), this) ?? new Point();
                        var none = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.Other);
                        for (int i = 0; i < 3; i++) gd.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(gd, ptr, this, at, (ulong)i, none, Avalonia.Input.KeyModifiers.None, new Vector(0, 1)));
                        await Task.Delay(800);
                        double g1 = gs.Get();
                        lines.Add($"{(g1 > g0 ? "ok" : "FAIL")} group dial wheel {_rail.CurrentGroup?.Title}/{gs.Name}: {Ui.F(g0)} → {Ui.F(g1)} (다이얼 {_rail.CurrentGroup?.Items.Count}개)");
                        Save((Control)Content!, "rail_group_after.png", Ui.KBase);
                        gs.Reset();
                        // 쉐이프키 이름을 누른 것처럼: 줄 슬라이더를 넘기면 막대로
                        var row = _pages["shapes"].GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => Ui.EditOf(s) != null && s.GetVisualAncestors().OfType<StyledElement>().Any(a => a.Classes.Contains("shaperow")));
                        if (row != null) { Ui.OfferFine(Ui.EditOf(row)!.Fine()); lines.Add($"{(!_rail.InGroupView ? "ok" : "FAIL")} shape row → bar ({_rail.Target?.Name})"); }
                        else lines.Add("skip shape row → bar (열린 묶음 없음)");
                    }
                }
                catch (Exception ex) { lines.Add("FAIL group dial " + ex.GetType().Name + ": " + ex.Message); }
                _rail.SetOpen(false); await Task.Delay(300);
                Save((Control)Content!, "rail_closed_mid.png", Ui.KBase);
                // 떠 있는 다이얼: 쉐이프키 둘을 띄워 자석으로 합치고(묶음 바), 하나는 막대를 켜고, 프리셋 저장 → 닫기 → 다시 열기, 앞 블록 떼기
                try
                {
                    var names = _rail.CurrentGroup?.Items.Take(3).Select(x => x.Fine?.Invoke()).Where(x => x != null).ToList() ?? new List<Ui.FineTarget?>();
                    if (names.Count < 2) lines.Add("FAIL float: 쉐이프키 둘이 없음");
                    else
                    {
                        var w1 = DialBoard.Open(names[0]!)!; var w2 = DialBoard.Open(names[1]!, bar: true)!;
                        await Task.Delay(500);
                        DialBoard.Merge(w2, w1, DialBoard.Side.Right);
                        if (names.Count > 2) { var w3 = DialBoard.Open(names[2]!)!; await Task.Delay(300); DialBoard.Merge(w3, w1, DialBoard.Side.Bottom); }   // 세 번째는 아래 줄로
                        w1.GroupName = "UI 시험 묶음";
                        await Task.Delay(500);
                        Save((Control)w1.Content!, "float_dials.png", Ui.KBase);
                        bool merged = DialBoard.Windows.Count == 1 && w1.Count == names.Count && w1.Rows.Count == (names.Count > 2 ? 2 : 1);
                        // 묶음 안 두 번째·세 번째 블록의 다이얼을 가짜 포인터로 끌어 값이 바뀌는지 (첫 블록만 되던 문제)
                        foreach (var blk in w1.Blocks.Skip(1))
                        {
                            var dl = blk.GetVisualDescendants().OfType<Dial>().First();
                            double b0 = blk.T.Get();
                            var ptr = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                            var root = (Visual)w1;
                            var at = dl.TranslatePoint(new Point(35, 35), root) ?? new Point();
                            var down = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
                            var move = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.Other);
                            var up = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
                            dl.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(dl, ptr, root, at, 0, down, Avalonia.Input.KeyModifiers.None, 1));
                            dl.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, dl, ptr, root, at + new Point(0, -30), 1, move, Avalonia.Input.KeyModifiers.None));
                            dl.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(dl, ptr, root, at + new Point(0, -30), 2, up, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
                            await Task.Delay(200);
                            double b1 = blk.T.Get();
                            lines.Add($"{(b1 > b0 ? "ok" : "FAIL")} float block dial {blk.T.Name} (줄 {w1.Find(blk).Row}): {Ui.F(b0)} → {Ui.F(b1)}");
                            blk.T.Commit(b0);
                        }
                        DialBoard.SavePreset(w1);
                        DialBoard.CloseWindow(w1); await Task.Delay(300);
                        var w4 = DialBoard.OpenPreset("UI 시험 묶음");
                        await Task.Delay(500);
                        bool reopened = w4 != null && w4.Count == names.Count && w4.Blocks.ElementAt(1).BarOn && w4.Rows.Count == (names.Count > 2 ? 2 : 1);
                        w4?.Blocks.First().SetBar(false);
                        w4!.SetPinned(true);
                        await Task.Delay(300);
                        Save((Control)w4.Content!, "float_dials_preset.png", Ui.KBase);
                        lines.Add($"{(merged && reopened ? "ok" : "FAIL")} float dials: 합치기 {merged} · 프리셋 다시 열기 {reopened} ({string.Join(", ", w4.Blocks.Select(b => b.T.Name))})");
                        DialBoard.CloseWindow(w4);
                        DialBoard.DeletePreset("UI 시험 묶음");
                    }
                }
                catch (Exception ex) { lines.Add("FAIL float dials " + ex.GetType().Name + ": " + ex.Message); }
                // 로커 스위치·장비 키: 설정 창 (다이얼 모드)
                {
                    var sw = SettingsWindow();
                    sw.ShowActivated = false; sw.WindowStartupLocation = WindowStartupLocation.Manual; sw.Position = new PixelPoint(-30000, -30000);
                    sw.Show();
                    await Task.Delay(800);
                    var tg = sw.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
                    try
                    {
                        // 가짜 클릭 두 번: 켜짐이 바뀌고 로커 그림(눌린 쪽·하이라이트)도 따라 좌우로 옮겨 가는지
                        string Click()
                        {
                            var face = tg!.GetVisualDescendants().OfType<RockerFace>().FirstOrDefault();
                            var ptr = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                            var at = tg.TranslatePoint(new Point(10, 10), sw) ?? new Point();
                            var down = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
                            var up = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
                            var target = (Avalonia.Input.InputElement?)face ?? tg;
                            target.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(target, ptr, sw, at, 0, down, Avalonia.Input.KeyModifiers.None, 1));
                            target.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(target, ptr, sw, at, 1, up, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
                            return $"{tg.IsChecked}/{face?.On}";
                        }
                        if (tg != null) { var before = $"{tg.IsChecked}/{tg.GetVisualDescendants().OfType<RockerFace>().FirstOrDefault()?.On}"; var a = Click(); var b = Click(); lines.Add($"{(a != before && b == before && a.Split('/')[0] == a.Split('/')[1] ? "ok" : "FAIL")} rocker click {before} → {a} → {b}"); }
                        else lines.Add("FAIL rocker 없음");
                    }
                    catch (Exception ex) { lines.Add("FAIL rocker " + ex.Message); }
                    var sv = sw.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    Save(sv?.Content as Control ?? (Control)sw.Content!, "dlg_settings_dialmode.png", Ui.KBase);
                    sw.Close();
                }
                // 다이얼 모드 모양 셋: 플랫(기본) · 입체 · 플랫 + UI 전체 통일 (설정은 저장하지 않는다)
                {
                    var lookKeep = _settings.HardwareLook; var unifyKeep = _settings.HardwareUnify;
                    _side.Select("material"); 
                    foreach (var (look, unify, name) in new[] { ("flat", false, "dialmode_flat"), ("skeuo", false, "dialmode_skeuo"), ("flat", true, "dialmode_unify") })
                    {
                        _settings.HardwareLook = look; _settings.HardwareUnify = unify;
                        Hardware.Apply(true, _settings); InvalidateDials();
                        await Task.Delay(500);
                        Save((Control)Content!, name + ".png", Ui.KBase);
                        var sw2 = SettingsWindow();
                        sw2.ShowActivated = false; sw2.WindowStartupLocation = WindowStartupLocation.Manual; sw2.Position = new PixelPoint(-30000, -30000);
                        sw2.Show(); await Task.Delay(600);
                        var sv2 = sw2.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                        Save(sv2?.Content as Control ?? (Control)sw2.Content!, name + "_settings.png", Ui.KBase);
                        sw2.Close();
                    }
                    _settings.HardwareLook = lookKeep; _settings.HardwareUnify = unifyKeep;
                    // 다이얼 모드의 오버레이 칸: 스튜디오 "오버레이" 탭과 컨트롤 보드 (크기 다이얼이 다른 줄과 같은 서식인지)
                    Hardware.Apply(true, _settings); InvalidateDials();
                    _side.Select("studio"); _studioPanel!.ShowTab("overlay"); await Task.Delay(500);
                    SaveTab(_studioPanel, "dialmode_overlay_tab.png");
                    var bw = new OverlayBoard(_ctx, _settings, _overlay, _exprPanel, _shapeRun, _posePanel!, _ => false, () => true, _ => { })
                    { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-30000, -30000) };
                    bw.Show(); await Task.Delay(700);
                    Save((Control)bw.Content!, "dialmode_board.png", Ui.KBase);
                    bw.Close();
                }
                // 설치된 기믹: 아바타 › 붙인 것 맨 아래 단추로 펼친다 (예전 IR이면 원본에서 다시 찾는다)
                try
                {
                    _side.Select("avatar"); await Task.Delay(300);
                    _avatarPanel!.OpenGimmicks(true);
                    var t0 = DateTime.UtcNow;
                    while (!_avatarPanel.GimmickState.Done && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(90)) await Task.Delay(250);
                    await Task.Delay(400);
                    var gs = _avatarPanel.GimmickState;
                    var sv = _pages["avatar"].GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(x => x.IsEffectivelyVisible);
                    Save(sv?.Content as Control ?? _pages["avatar"], "avatar_gimmicks.png", Ui.KBase);
                    lines.Add($"{(gs.Done && gs.Error == null && gs.Prefabs > 0 ? "ok" : "FAIL")} gimmicks: 프리팹 {gs.Prefabs} · 도구 {gs.Tools} ({(DateTime.UtcNow - t0).TotalSeconds:0.0}초) {gs.Error}");
                    _avatarPanel.OpenGimmicks(false);
                }
                catch (Exception ex) { lines.Add("FAIL gimmicks " + ex.GetType().Name + ": " + ex.Message); }
                // 머티리얼 섹션 탭: 꺼진 섹션은 "기타"로, 켜고 끄면 설명·탭 상태(LED / 꺼짐 글씨)가 바로 따라가는지
                try
                {
                    Hardware.Apply(true, _settings); InvalidateDials();
                    _side.Select("material"); await Task.Delay(400);
                    var st = _pages["material"].GetVisualDescendants().OfType<SubTabs>().FirstOrDefault();
                    string? key = st?.Keys.FirstOrDefault(k => k == "sec:MatCap") ?? st?.Keys.FirstOrDefault(k => k == "sec:Emission");
                    if (st == null || key == null) lines.Add("FAIL section tabs: 머티리얼 탭이 없음");
                    else
                    {
                        // 머티리얼 콤보에서 다른 슬롯 고르기 (예전: 고르는 도중 목록을 다시 채워 선택 모델 범위 오류로 튕김)
                        var mp = _pages["material"].GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Items.Count > 1 && c.Items[0] is ComboBoxItem ci && (ci.Content as string ?? "").StartsWith("슬롯"));
                        if (mp != null)
                        {
                            var keepIdx = mp.SelectedIndex;
                            mp.SelectedIndex = (keepIdx + 1) % mp.Items.Count; await Task.Delay(400);
                            mp.SelectedIndex = keepIdx; await Task.Delay(400);
                        }
                        lines.Add($"ok material pick from combo ({(mp != null ? mp.Items.Count + " slots" : "콤보 없음")})");
                        bool wasMore = st.IsMore(key);
                        // 기타 단추: 창 클래스가 마우스마다 바뀌어도(예전엔 Refresh가 단추를 빼고 다시 넣어 튕김) 그대로 있고, 누르면 메뉴가 열린다
                        var moreLbl = st.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => (t.Text ?? "").StartsWith("기타 "));
                        var moreBtn = moreLbl?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Cursor != null);
                        for (int i = 0; i < 20; i++) { Classes.Add("shot-test"); Classes.Remove("shot-test"); }
                        bool menuOpen = false;
                        int opens0 = st.MoreMenuOpens;
                        if (moreBtn != null)
                        {
                            var ptr = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                            var at = moreBtn.TranslatePoint(new Point(10, 10), this) ?? new Point();
                            var down = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
                            var up = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
                            moreBtn.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(moreBtn, ptr, this, at, 0, down, Avalonia.Input.KeyModifiers.None, 1));
                            Classes.Add("shot-test"); Classes.Remove("shot-test");
                            moreBtn.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(moreBtn, ptr, this, at, 2, up, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
                            await Task.Delay(400);
                            menuOpen = st.MoreMenuOpens > opens0;
                            Save((Control)Content!, "material_more_menu.png", Ui.KBase);
                        }
                        lines.Add($"{(moreBtn != null && moreBtn.Parent != null && menuOpen ? "ok" : "FAIL")} more button survives class changes (찾음 {moreBtn != null}), 메뉴 열림 {menuOpen}");
                        Save((Control)Content!, "material_tabs_dial.png", Ui.KBase);
                        st.Select(key); await Task.Delay(300);
                        // 이 섹션의 "… 쓰기" 줄 스위치 (페이지 위쪽 외곽선 스위치가 아니라)
                        var head = st.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && (t.Text ?? "").EndsWith(" 쓰기"));
                        var tg = head?.GetVisualAncestors().OfType<Grid>().FirstOrDefault()?.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
                        bool before = tg?.IsChecked == true;
                        if (tg != null) tg.IsChecked = !before;
                        await Task.Delay(300);
                        string want = !before ? "켜져 있어요" : "꺼져 있어요";
                        bool desc = st.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith(want));
                        Save((Control)Content!, "material_tabs_toggled.png", Ui.KBase);
                        lines.Add($"{(tg != null && desc ? "ok" : "FAIL")} section toggle {key}: {before} → {!before}, 설명 따라감 {desc}, 기타에 있었음 {wasMore}");
                        if (tg != null) _ctx.Doc?.Undo();
                        await Task.Delay(200);
                    }
                    Hardware.Apply(false, _settings); InvalidateDials(); await Task.Delay(400);
                    Save((Control)Content!, "material_tabs.png", Ui.KBase);
                }
                catch (Exception ex) { lines.Add("FAIL section tabs " + ex.GetType().Name + ": " + ex.Message); }
                // 색 조화 탭: 삼각 규칙 · 메인 텍스처 대표 색 · 고른 색을 색 프로퍼티에 넣고 문서 값 확인 (그다음 실행 취소)
                try
                {
                    var st = _pages["material"].GetVisualDescendants().OfType<SubTabs>().FirstOrDefault();
                    st?.Select("palette"); await Task.Delay(300);
                    var pal = _pages["material"].GetVisualDescendants().OfType<ColorHarmonyPanel>().FirstOrDefault();
                    if (pal == null) lines.Add("FAIL palette: 탭이 없음");
                    else
                    {
                        var colAddr = Host.Core.Addr.MatColor(_ctx.SelectedMaterial ?? "", "_Color");
                        var before = _ctx.Doc?.Get(colAddr).ToJson();
                        var (line, hex) = pal.ShotTest(Host.Core.HarmonyRule.Triad);
                        await Task.Delay(400);
                        var after = _ctx.Doc?.Get(colAddr).ToJson();
                        bool applied = after != before;
                        lines.Add(line + $" · 넣기 {(applied ? "ok" : "FAIL")} {hex} → _Color {after}");
                        Save((Control)Content!, "material_palette.png", Ui.KBase);
                        if (applied) _ctx.Doc?.Undo();
                        // 조작 모양마다 (보통 · 다이얼 플랫 · 다이얼 입체 · 플랫 + UI 전체 통일). 설정은 저장하지 않는다
                        var lookKeep = _settings.HardwareLook; var unifyKeep = _settings.HardwareUnify;
                        foreach (var (look, unify, name) in new[] { ("flat", false, "flat"), ("skeuo", false, "skeuo"), ("flat", true, "unify") })
                        {
                            _settings.HardwareLook = look; _settings.HardwareUnify = unify;
                            Hardware.Apply(true, _settings); InvalidateDials();
                            await Task.Delay(500);
                            Save(pal, $"material_palette_{name}.png", Ui.KBase);
                        }
                        _settings.HardwareLook = lookKeep; _settings.HardwareUnify = unifyKeep;
                        Hardware.Apply(false, _settings); InvalidateDials();
                        await Task.Delay(300);
                        Save(pal, "material_palette_plain.png", Ui.KBase);
                    }
                }
                catch (Exception ex) { lines.Add("FAIL palette " + ex.GetType().Name + ": " + ex.Message); }
                Hardware.Apply(_settings.SliderStyle == "dial", _settings);
                _rail.SetOpen(railKeep);
                File.WriteAllLines(Path.Combine(_shotsDir!, "rail_test.txt"), lines);
            }
            _settings.LayoutStyle = layoutKeep; ApplyLayoutStyle();
            // 테마 프리셋 몇 가지 (설정 파일은 바꾸지 않는다)
            var themeKeep = _settings.ThemeKey;
            foreach (var k in new[] { "nord", "warm", "lavender" })
            {
                _settings.ThemeKey = k; UiTheme.Apply(_settings);
                _side.Select("shapes"); await Task.Delay(500);
                Save((Control)Content!, $"theme_{k}.png", Ui.KBase);
            }
            _settings.ThemeKey = themeKeep; UiTheme.Apply(_settings);
            // 대화상자: 화면 밖에 띄워 찍고 닫는다
            if (_ctx.Doc != null)
            {
                // VR 사진 창·올리기 썸네일: 임시 폴더의 그림으로
                var vrDir = Path.Combine(Path.GetTempPath(), "VrcProtoShots_vr");
                Directory.CreateDirectory(vrDir);
                var vrPng = Path.Combine(vrDir, "VR_test.png");
                var vrImg = new Rgba(320, 180);
                for (int i = 0; i < vrImg.Px.Length; i += 4) { vrImg.Px[i] = 90; vrImg.Px[i + 1] = 130; vrImg.Px[i + 2] = 200; vrImg.Px[i + 3] = 255; }
                File.WriteAllBytes(vrPng, Sticker.EncodePng(vrImg));
                var gallery = new VrGalleryPanel(() => vrDir, () => { }, _ => { }, _ => { }, _ => { });
                gallery.Refresh(); gallery.Show(vrPng);
                foreach (var (name, w) in new (string, Window)[] { ("dlg_upload", new UploadWindow(_ctx, null)), ("dlg_upload_picked", new UploadWindow(_ctx, null, vrPng)), ("float_vrgallery", new Window { Content = gallery, Width = 560, Height = 640 }), ("dlg_export", new ExportWindow(_ctx)), ("dlg_shaderpack", new ShaderPackWindow(_ctx.Doc.Original, _unityVersion)) })
                {
                    w.ShowActivated = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Position = new PixelPoint(-30000, -30000);
                    w.Show();
                    await Task.Delay(500);
                    Save((Control)w.Content!, name + ".png", Ui.KCard);
                    w.Close();
                }
            }
            {
                // 설정 창: 문서 없이도 찍는다. 스크롤 안 내용 전체
                var w = SettingsWindow();
                w.ShowActivated = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Position = new PixelPoint(-30000, -30000);
                w.Show();
                await Task.Delay(800);
                var sv = w.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                Save(sv?.Content as Control ?? (Control)w.Content!, "dlg_settings.png", Ui.KBase);
                if (sv?.Content is Panel { Children: { Count: > 0 } kids }) Save(kids[kids.Count - 1], "dlg_settings_info.png", Ui.KBase);   // 긴 창이라 위 그림에서 잘리는 맨 아래 칸 (2026-11-01)
                w.Close();
            }
            {
                // PhysBone 느낌 창 (설정 › 느낌 조절…)
                PhysBoneFeelWindow();
                var w = _feelWindow!;
                w.Position = new PixelPoint(-30000, -30000);
                await Task.Delay(600);
                Save((Control)w.Content!, "dlg_physbone_feel.png", Ui.KCard);
                w.Close();
            }
            await FloatTest();
            SetExpert(keep, save: false);
        }

        /// <summary>
        /// 떠 있는 창·가져온 파일 창·찾기 시험 (결과는 float_test.txt): 자세 "손 모양" 탭 띄우기 → 핀 없이 다른 페이지로 가면 숨고 돌아오면 보임 →
        /// 핀이면 다른 페이지에서도 보임 → 에디터 안쪽에 있는지 → 다시 붙이기. 가져온 파일 창 탭마다 찍기. 찾기 색인에서 "쥐기"·"그림자".
        /// </summary>
        async Task FloatTest()
        {
            var log = new List<string>();
            void Line(bool ok, string s) { log.Add((ok ? "ok   " : "FAIL ") + s); AppendLog("info", "[float-test] " + (ok ? "ok " : "FAIL ") + s); }
            bool Inside(Window f)
            {
                var tl = this.PointToScreen(new Point(0, 0)); var br = this.PointToScreen(new Point(ClientSize.Width, ClientSize.Height));
                return f.Position.X >= tl.X && f.Position.Y >= tl.Y && f.Position.X + f.Bounds.Width * RenderScaling <= br.X + 1 && f.Position.Y + f.Bounds.Height * RenderScaling <= br.Y + 1;
            }
            _side.Select("pose");
            await Task.Delay(300);
            var tabs = _pages["pose"].GetVisualDescendants().OfType<SubTabs>().First();
            tabs.Float("hands");
            await Task.Delay(800);
            var f = _floats.Find("pose/hands");
            Line(f != null && f.IsVisible, "자세 › 손 모양 띄우기");
            if (f != null)
            {
                Save((Control)f.Content!, "float_pose_hands.png", Ui.KPanel);
                Save((Control)Content!, "float_pose_page.png", Ui.KBase);
                Line(Inside(f), $"에디터 안쪽 (창 {f.Position} 크기 {f.Bounds.Width:0}×{f.Bounds.Height:0})");
                // 밖으로 보내도 (에디터 안에 넣은 창은 Position 쓰기가 부모 기준이라 Embed.MoveTo로 — 실제 끌기와 같게)
                Embed.MoveTo(f, _floats.Clamp(f, new PixelPoint(f.Position.X - 50000, f.Position.Y)));
                Line(Inside(f), "밖으로 옮겨도 안쪽으로 잘림");
                _side.Select("material"); await Task.Delay(300);
                Line(!f.IsVisible, "핀 없음: 다른 페이지에서 숨음");
                _side.Select("pose"); await Task.Delay(300);
                Line(f.IsVisible, "핀 없음: 자기 페이지로 돌아오면 보임");
                f.SetPinned(true);
                _side.Select("material"); await Task.Delay(300);
                Line(f.IsVisible, "핀: 다른 페이지에서도 보임");
                _side.Select("pose"); await Task.Delay(200);
                tabs.DockBack("hands");
                await Task.Delay(400);
                Line(_floats.Find("pose/hands") == null && tabs.Current == "hands", "다시 붙이기");
                Save((Control)Content!, "float_pose_docked.png", Ui.KBase);
            }
            // 가져온 파일 창
            _side.Select("avatar"); await Task.Delay(200);
            OpenFilesWindow();
            await Task.Delay(1500);   // 크기 재기
            var files = _floats.Find("files");
            Line(files != null && files.IsVisible, "가져온 파일 창");
            if (files != null && _filesPanel != null)
            {
                var ft = _filesPanel.GetVisualDescendants().OfType<SubTabs>().First();
                foreach (var k in ft.Keys.ToList()) { ft.Select(k); await Task.Delay(400); Save((Control)files.Content!, $"float_files_{k}.png", Ui.KPanel); }
                _side.Select("material"); await Task.Delay(200);
                Line(files.IsVisible, "가져온 파일 창은 어느 페이지에서나 보임");
                CloseFiles();
                await Task.Delay(200);
                Line(_floats.Find("files") == null, "가져온 파일 창 닫기");
            }
            // 머티리얼·에셋 창
            OpenAssetsWindow();
            await Task.Delay(1500);   // 텍스처 크기 재기
            var assets = _floats.Find("assets");
            Line(assets != null && assets.IsVisible, "머티리얼·에셋 창");
            if (assets != null && _assetsPanel != null)
            {
                async Task Shot(string name) { await Task.Delay(600); Save((Control)assets.Content!, $"float_assets_{name}.png", Ui.KPanel); }   // 썸네일 읽기
                await Shot("start");
                Line(_assetsPanel.ShotStart.Length > 0, "머티리얼·에셋: Assets 아래 한 줄 폴더 건너뛰기 → " + _assetsPanel.ShotStart);
                _assetsPanel.ShotPick("Material"); await Shot("material");
                _assetsPanel.ShotPick("Texture"); await Shot("texture");
                _assetsPanel.ShotGo("\u0001메시"); await Shot("meshes");
                _assetsPanel.ShotFind("Face"); await Task.Delay(300); await Shot("find");
                _assetsPanel.ShotFind("");
                CloseAssets();
                await Task.Delay(200);
                Line(_floats.Find("assets") == null, "머티리얼·에셋 창 닫기");
            }
            // 텍스처 합치기 (2026-11-02): 레이어 올리기 → 적용 → 다시 열기 → 실행 취소
            if (_ctx.Doc != null && _ctx.Doc.Original.Materials.FirstOrDefault(m => m.TexEnvs.TryGetValue("_MainTex", out var te) && _ctx.Doc.Texture(te.Texture) != null) is { } mixMat)
            {
                var doc = _ctx.Doc;
                var mixAddr = Addr.MatTex(mixMat.Guid, "_MainTex");
                var mixBase = doc.Get(mixAddr).AsStringOrNull;
                var mixSrc = Path.Combine(Path.GetTempPath(), "vrcproto_mix_test.png");
                var dot = new Rgba(128, 128);
                for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) { int o = (y * 128 + x) * 4; double d = Math.Sqrt((x - 64) * (x - 64) + (y - 64) * (y - 64)); dot.Px[o] = 40; dot.Px[o + 1] = 170; dot.Px[o + 2] = 255; dot.Px[o + 3] = (byte)(d < 56 ? 255 : d < 62 ? 120 : 0); }
                File.WriteAllBytes(mixSrc, Sticker.EncodePng(dot));
                // 직접 못 읽는 PNG(회색조)는 Avalonia 디코더로: 채널 순서·크기가 맞는지
                var grayFile = Path.Combine(Path.GetTempPath(), "vrcproto_mix_gray.png");
                using (var gs = new MemoryStream())
                {
                    gs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                    Sticker.Chunk(gs, "IHDR", new byte[] { 0, 0, 0, 2, 0, 0, 0, 1, 8, 0, 0, 0, 0 });
                    var zs = new MemoryStream();
                    using (var zl = new System.IO.Compression.ZLibStream(zs, System.IO.Compression.CompressionLevel.Fastest, true)) zl.Write(new byte[] { 0, 200, 50 });
                    Sticker.Chunk(gs, "IDAT", zs.ToArray());
                    Sticker.Chunk(gs, "IEND", Array.Empty<byte>());
                    File.WriteAllBytes(grayFile, gs.ToArray());
                }
                var gray = ImageFiles.LoadFull(grayFile);
                Line(gray.W == 2 && gray.H == 1 && gray.Px[0] == 200 && gray.Px[2] == 200 && gray.Px[3] == 255 && gray.Px[4] == 50, $"텍스처 합치기: 회색조 PNG 읽기 {gray.W}×{gray.H} {gray.Px[0]},{gray.Px[3]},{gray.Px[4]}");
                async Task<TextureMixWindow> MixOpen()
                {
                    var w = new TextureMixWindow(_ctx, doc, mixMat.Guid, "_MainTex", "메인 텍스처", _ => true);
                    w.ShowActivated = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Position = new PixelPoint(-30000, -30000);
                    w.Show();
                    await w.Loaded_;
                    return w;
                }
                var mix = await MixOpen();
                await Task.Delay(300);
                mix.ShowPicker(); await Task.Delay(800);
                Save((Control)mix.Content!, "dlg_texmix_find.png", Ui.KCard);
                mix.ShotLayers();
                await mix.ShotAdd(mixSrc, l => { l.X = 0.3f; l.Y = 0.4f; l.W = 0.2f; l.H = 0.2f; l.Mirror = true; });
                await mix.ShotAdd(mixSrc, l => { l.X = 0.5f; l.Y = 0.75f; l.W = 0.35f; l.H = 0.2f; l.Rotation = 25; l.Mode = "add"; l.Opacity = 0.8f; });
                await Task.Delay(300);
                var dragged = mix.ShotSelected!;
                float dx0 = dragged.X, dy0 = dragged.Y, dw0 = dragged.W, dh0 = dragged.H;
                await mix.ShotDrag(false, 40, -30);
                Line(Math.Abs(dragged.X - dx0) > 0.02f && dragged.Y < dy0 && dragged.W == dw0, $"텍스처 합치기: 끌어 옮기기 ({dx0:0.00},{dy0:0.00}) → ({dragged.X:0.00},{dragged.Y:0.00})");
                await mix.ShotDrag(true, 30, 15);
                Line(dragged.W > dw0 && Math.Abs(dragged.W / dragged.H - dw0 / dh0) < 0.01f, $"텍스처 합치기: 모서리로 크기 {dw0:0.00} → {dragged.W:0.00} (비율 그대로)");
                await Task.Delay(500);
                Save((Control)mix.Content!, "dlg_texmix.png", Ui.KCard);
                bool mixOk = await mix.ApplyAsync();
                var mixKey = doc.Get(mixAddr).AsStringOrNull ?? "";
                var mixFile = doc.Texture(mixKey)?.File ?? "";
                Line(mixOk && mixKey.StartsWith("mix:") && File.Exists(mixFile), "텍스처 합치기: 적용 → " + Path.GetFileName(mixFile));
                mix.Close();
                var mixAgain = await MixOpen();
                await Task.Delay(300);
                Save((Control)mixAgain.Content!, "dlg_texmix_reopen.png", Ui.KCard);
                Line(doc.Get(Addr.TexAsset(mixKey))["mix"]["layers"].Count == 2, "텍스처 합치기: 다시 열면 레이어 2개");
                mixAgain.Close();
                doc.Undo();
                Line(doc.Get(mixAddr).AsStringOrNull == mixBase && doc.Texture(mixKey) == null, "텍스처 합치기: 실행 취소하면 바탕으로");
            }
            // 블렌드쉐이프: 여러 개 고르기·함께 바꾸기·즐겨찾기·L·R·묶음 띄우기 (블렌드쉐이프가 가장 많은 메시)
            if (_ctx.Doc != null)
            {
                var doc = _ctx.Doc;
                var r = doc.Original.Renderers.OrderByDescending(x => doc.Original.FindMesh(x.Mesh)?.BlendShapes.Count ?? 0).First();
                _ctx.Select(r.Id, null);
                _side.Select("material"); await Task.Delay(400);
                var panel = _pages["shapes"].GetVisualDescendants().OfType<BlendShapePanel>().First();
                var res = panel.SelfTest();
                foreach (var l in res) { log.Add(l); AppendLog("info", "[shapes-test] " + l); }
                await Task.Delay(600);
                if (panel.FloatedGroup() is FloatPanel gf) Save((Control)gf.Content!, "float_shapes_group.png", Ui.KPanel);
                Save((Control)Content!, "shapes_after_test.png", Ui.KBase);
                panel.DockAllGroups();
                // 블렌드쉐이프 이름 번역: 펼친 묶음 · 한국어로 찾기 · 영어 · 원본 이름 · 구글 번역 (shapes_ko*.png · shapes_en.png · shapes_google.png)
                _side.Select("shapes");
                foreach (var (step, file) in new[] { ("open:ko", "shapes_ko.png"), ("search:눈 감기", "shapes_ko_search.png"), ("search:웃음", "shapes_ko_search2.png"), ("search:まばたき", ""), ("open:en", "shapes_en.png"), ("search:Blink", ""), ("raw", "shapes_ko_off.png"), ("google", ""), ("wait", ""), ("googlecheck", "shapes_google.png"), ("end", "") })
                {
                    if (step == "wait") { await Task.Delay(15000); continue; }   // 구글 번역 응답
                    var ko = panel.TranslateShot(step);
                    if (ko != null) { log.Add(ko); AppendLog("info", "[shapes-ko] " + ko); }
                    await Task.Delay(400);
                    if (file.Length > 0) Save((Control)Content!, file, Ui.KBase);
                }
                // 블렌드쉐이프 세팅 저장·비교·되돌리기 (shapes_sets.png · float_shapes_set.png)
                foreach (var (step, file) in new[] { ("save:웃는 얼굴", ""), ("change", "shapes_sets.png"), ("compare", ""), ("apply", ""), ("save2", ""), ("slides", ""), ("quicksave", ""), ("menu", "shapes_sets_menu.png"), ("end", "") })
                {
                    var res2 = await panel.SetsShot(step);
                    if (res2 != null) { log.Add(res2); AppendLog("info", "[shapes-sets] " + res2); }
                    await Task.Delay(400);
                    if (file.Length > 0) Save((Control)Content!, file, Ui.KBase);
                    if (step == "slides" && SubTabs.Host?.Find("shapes/slides") is FloatPanel ss) Save((Control)ss.Content!, "float_shapes_slides.png", Ui.KPanel);
                    if (step == "compare" && SubTabs.Host?.Floats.FirstOrDefault(f => f.Key.StartsWith("shapes/set:")) is FloatPanel sf) Save((Control)sf.Content!, "float_shapes_set.png", Ui.KPanel);
                }
            }
            // 찾기 색인
            var index = SearchIndex();
            foreach (var q in new[] { "쥐기", "그림자", "오버레이", "토글" })
            {
                var top = index.Select(e => (e, s: Score(e, q))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(3).Select(x => $"{x.e.Title} ({x.e.Where})").ToList();
                Line(top.Count > 0, $"찾기 \"{q}\" → {string.Join(" / ", top)}");
            }
            log.Insert(0, $"찾기 색인 {index.Count}개");
            File.WriteAllLines(Path.Combine(_shotsDir!, "float_test.txt"), log);
        }

        /// <summary>--ui-shots … --shapes-bench: 블렌드쉐이프 페이지 반응 속도 (블렌드쉐이프가 가장 많은 메시로). 결과는 shapes_bench.txt, 찍기는 하지 않는다.</summary>
        async Task ShapesBench()
        {
            var doc = _ctx.Doc;
            var lines = new List<string>();
            if (doc == null) lines.Add("문서 없음");
            else
            {
                var r = doc.Original.Renderers.OrderByDescending(x => doc.Original.FindMesh(x.Mesh)?.BlendShapes.Count ?? 0).First();
                _side.Select("shapes");
                await Task.Delay(500);
                var panel = _pages["shapes"].GetVisualDescendants().OfType<BlendShapePanel>().First();
                lines.AddRange(panel.Bench(r.Id));
                await Task.Delay(300);
                Save((Control)Content!, "shapes_bench.png", Ui.KBase);
            }
            foreach (var l in lines) AppendLog("info", "[shapes-bench] " + l);
            File.WriteAllLines(Path.Combine(_shotsDir!, "shapes_bench.txt"), lines);
        }
    }
}
