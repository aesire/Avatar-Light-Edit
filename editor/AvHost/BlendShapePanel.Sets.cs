// 블렌드쉐이프 세팅 저장·비교·되돌리기 (2026-10-23): 페이지 안 "저장한 세팅" 탭.
// 지금 블렌드쉐이프 값(모든 메시)을 이름 붙여 저장하고, 저장할 때 얼굴을 정면 클로즈업으로 찍어 썸네일로 붙인다 (세션 파일에 PNG base64로).
// 비교는 떠 있는 창: 저장한 사진 | 지금 사진 + 다른 값 목록, "이 세팅으로 되돌리기"(실행 취소 한 단계) · 값 하나만 되돌리기.
// 값 다루기는 Host/Core/ShapeSets.cs, 저장은 Document.Variants(Kind "shapes")라 세션·복구·아이템 다시 만들기를 그대로 따른다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        const int PhotoSize = 256;
        readonly StackPanel _setsTab = new StackPanel(), _setsList = new StackPanel { Spacing = 8 };
        readonly TextBox _setName = new TextBox { Watermark = "이름 (비우면 \"저장된 블렌드쉐이프 N\")", MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        Button? _setSave;
        bool _setBusy;
        // 비교 창: 세팅 id → 창과 다시 그리기
        readonly Dictionary<string, (FloatPanel Float, Action Refresh)> _setFloats = new Dictionary<string, (FloatPanel, Action)>();
        readonly Dictionary<string, Bitmap?> _photoCache = new Dictionary<string, Bitmap?>();
        Action? _setsLater;

        /// <summary>목록 탭 다음에 "저장한 세팅" 탭 (문서가 있을 때만). Rebuild의 탭 다시 만들기 안에서 부른다.</summary>
        void AddSetsTab()
        {
            if (Doc == null) return;
            if (_setsTab.Children.Count == 0) BuildSetsTab();
            RefreshSets();
            int n = ShapeSets.All(Doc).Count();
            _tabs.Add("sets", "저장한 세팅", _setsTab, badge: n > 0 ? n.ToString() : "");
        }

        void BuildSetsTab()
        {
            _setsTab.Children.Add(Ui.Wrap("지금 블렌드쉐이프 값(모든 메시)을 이름을 붙여 저장해요. 저장할 때 얼굴을 정면에서 찍어 썸네일로 붙이고, 세션 파일에 함께 저장돼요. 되돌리기는 블렌드쉐이프만 바꿔요 (Ctrl+Z로 취소)."));
            // 오른쪽 단추를 DockPanel에 겹쳐 넣으면 좁은 패널에서 이름 입력 칸과
            // 단추가 같은 공간을 차지할 수 있다. Grid의 Auto 열로 단추 폭을 먼저
            // 확보하고 이름 칸은 남은 공간만 사용하게 한다.
            var row = new Grid { Margin = new Thickness(0, 8, 0, 10), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            _setSave = Ui.Button("지금 값 저장", () => SaveSet(), "지금 블렌드쉐이프 값을 새 세팅으로 저장하고 얼굴 사진을 찍어요", Glyph.Add, accent: true);
            _setSave.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(_setSave, 2);
            if (SubTabs.Host != null)
            {
                var slides = Ui.Button("슬라이드 쇼", () => ShowSlides(), "저장한 세팅의 사진을 한 장씩 크게 넘겨 보며 비교해요 (←/→ · 휠 · 자동 넘기기)", GSlides);
                slides.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(slides, 1);
                row.Children.Add(slides);
            }
            Grid.SetColumn(_setName, 0);
            row.Children.Add(_setSave); row.Children.Add(_setName);
            _setName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveSet(); e.Handled = true; } };
            _setsTab.Children.Add(row);
            _setsTab.Children.Add(_setsList);
            _setsLater = Ui.Debounce(() => { RefreshSetInfo(); foreach (var f in _setFloats.Values) f.Refresh(); _slides?.Refresh(); }, 400);
        }

        /// <summary>블렌드쉐이프 값이 바뀌면 (OnChanged): 목록의 "지금과 다른 값"과 비교 창을 조금 뒤에 다시.</summary>
        void SetsChanged() => _setsLater?.Invoke();

        async void SaveSet()
        {
            if (Doc == null || _setBusy) return;
            var doc = Doc;
            _setBusy = true; SetSaveEnabled(false);
            try
            {
                var photo = await FacePhoto();
                if (doc != Doc) return;   // 찍는 동안 다른 아바타를 열었다
                var v = ShapeSets.Add(doc, _setName.Text ?? "", photo);
                _setName.Text = "";
                _ctx.Status($"\"{v.Name}\"을(를) 저장했습니다 · BlendShape {ShapeSets.Count(doc, v)}개" + (photo.Length == 0 ? " (미리보기가 없어 사진을 촬영하지 못했습니다)" : ""));
                RefreshSets();
                _tabs.SetBadge("sets", ShapeSets.All(doc).Count().ToString());
            }
            finally { _setBusy = false; SetSaveEnabled(true); }
        }

        void SetSaveEnabled(bool on) { if (_setSave != null) _setSave.IsEnabled = on; if (_quickSave != null) _quickSave.IsEnabled = on; }

        /// <summary>
        /// 얼굴 정면 클로즈업을 찍어 PNG base64로 (렌더러가 없거나 실패하면 ""). 카메라를 얼굴 구도로 옮겨 찍고 바로 원래 자리로 돌린다 —
        /// 렌더러는 한 번에 받은 메시지를 같은 프레임에 처리하므로 미리보기에는 보이지 않는다.
        /// </summary>
        async Task<string> FacePhoto()
        {
            var r = _ctx.Render;
            if (r == null || !r.IsConnected) return "";
            var path = Path.Combine(Path.GetTempPath(), "VrcProto_shapeset_" + Guid.NewGuid().ToString("N") + ".png");
            var old = _ctx.Camera;
            var tcs = new TaskCompletionSource<bool>();
            void On(JNode m) { if (Msg.Type(m) == Msg.CaptureDone && m["path"].AsString == path) tcs.TrySetResult(m["ok"].AsBool); }
            _ctx.RendererMessage += On;
            try
            {
                // 얼굴 프리셋은 전체 장면 bounds의 가운데를 기준으로 잡는다. 머리 장식·손 소품처럼
                // 아바타에서 떨어진 렌더러가 있으면 그 가운데가 얼굴에서 멀어져 엉뚱한 곳을 찍을 수
                // 있다. 먼저 얼굴/정면 구도를 만든 뒤, 휴머노이드 Head 본을 알고 있으면 그 본을
                // 기준으로 다시 프레임한다. Head 매핑이 없는 아바타에서는 face 프리셋이 안전한
                // 대체 경로로 남는다.
                r.Send(Msg.Make(Msg.SetCamera).Set("preset", "face"));
                r.Send(Msg.Make(Msg.SetCamera).Set("preset", "front"));
                r.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Head").Set("size", 0.34));
                r.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", PhotoSize).Set("height", PhotoSize).Set("transparent", false).Set("supersample", 2));
                if (!old.IsNull) r.Send(Msg.Make(Msg.SetCamera).Set("state", old));
                var done = await Task.WhenAny(tcs.Task, Task.Delay(30_000));
                if (!old.IsNull) _ctx.Camera = old;   // 구도 프리셋이 알린 얼굴 카메라를 지운다 (돌려놓기는 알리지 않는다)
                if (done != tcs.Task || !tcs.Task.Result || !File.Exists(path)) return "";
                return Convert.ToBase64String(await File.ReadAllBytesAsync(path));
            }
            catch (Exception e) { _ctx.Status("얼굴 사진을 촬영하지 못했습니다: " + e.Message); return ""; }
            finally
            {
                _ctx.RendererMessage -= On;
                try { File.Delete(path); } catch (Exception) { }
            }
        }

        Bitmap? Photo(string b64)
        {
            if (b64.Length == 0) return null;
            var key = b64.Length + ":" + b64.GetHashCode();
            if (_photoCache.TryGetValue(key, out var bmp)) return bmp;
            try { bmp = new Bitmap(new MemoryStream(Convert.FromBase64String(b64))); } catch (Exception) { bmp = null; }
            return _photoCache[key] = bmp;
        }

        Control PhotoBox(string b64, double size, string empty)
        {
            var bmp = Photo(b64);
            Control inner = bmp != null
                ? new Image { Source = bmp, Stretch = Stretch.UniformToFill }
                : Ui.Text(empty, 11, FontWeight.Normal, Ui.KText3);
            if (inner is TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; }
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = inner, BorderThickness = new Thickness(1) }.Bg(Ui.KBase).Stroke(Ui.KCardStroke);
        }

        // 줄마다 "지금과 다른 값" 글 (값이 바뀔 때 줄을 다시 만들지 않고 글만)
        readonly List<(Document.Variant Set, TextBlock Info)> _setInfos = new List<(Document.Variant, TextBlock)>();

        void RefreshSets()
        {
            _setsList.Children.Clear(); _setInfos.Clear();
            var doc = Doc;
            if (doc == null) return;
            // 지운 세팅·다른 아바타의 비교 창은 닫는다
            foreach (var id in _setFloats.Keys.Where(id => !ShapeSets.All(doc).Any(v => v.Id == id)).ToList()) CloseSetFloat(id);
            var sets = ShapeSets.All(doc).ToList();
            if (sets.Count == 0)
            {
                _slides?.Draw();
                var hint = Ui.Wrap("저장한 세팅이 없어요. 블렌드쉐이프를 맞춘 뒤 \"지금 값 저장\"을 눌러요.");
                hint.Margin = new Thickness(0, 4, 0, 4);
                _setsList.Children.Add(hint);
                return;
            }
            foreach (var v in sets) _setsList.Children.Add(SetRow(doc, v));
            RefreshSetInfo();
            _slides?.Draw();
        }

        Control SetRow(Document doc, Document.Variant v)
        {
            var name = new TextBox { Text = v.Name, MinWidth = 100, FontSize = 13 };
            name.LostFocus += (_, __) =>
            {
                var t = (name.Text ?? "").Trim();
                if (t.Length == 0) { name.Text = v.Name; return; }
                if (t == v.Name) return;
                v.Name = t; doc.MarkUnsaved();
                if (_setFloats.TryGetValue(v.Id, out var f)) f.Float.SetTitle(SetFloatTitle(v));
            };
            var info = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            info.TextWrapping = TextWrapping.Wrap;
            _setInfos.Add((v, info));
            var mid = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            mid.Children.Add(name); mid.Children.Add(info);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            if (SubTabs.Host != null) tools.Children.Add(Ui.IconButton(Glyph.PopOut, () => CompareSet(v), "지금과 비교: 저장한 사진과 지금 사진, 다른 값을 떠 있는 창으로 봐요"));
            tools.Children.Add(Ui.IconButton(Glyph.Undo, () => ApplySet(v), "이 세팅으로 되돌리기: 블렌드쉐이프만 바꿔요 (Ctrl+Z로 취소)"));
            tools.Children.Add(Ui.IconButton(Glyph.Save, () => OverwriteSet(v), "지금 값으로 덮어쓰기 (사진도 새로 찍어요)"));
            tools.Children.Add(Ui.IconButton(Glyph.Delete, () => DeleteSet(v), "지우기"));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var photo = PhotoBox(v.Photo, 72, "사진 없음");
            photo.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            photo.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) { if (SubTabs.Host != null) ShowSlides(v.Id); else CompareSet(v); } };
            ToolTip.SetTip(photo, "눌러서 슬라이드 쇼로 크게 보기");
            Grid.SetColumn(photo, 0); Grid.SetColumn(mid, 1); Grid.SetColumn(tools, 2);
            grid.Children.Add(photo); grid.Children.Add(mid); grid.Children.Add(tools);
            return new Border { Child = grid, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }.Bg(Ui.KCard).Stroke(Ui.KCardStroke);
        }

        void RefreshSetInfo()
        {
            var doc = Doc;
            if (doc == null) return;
            foreach (var (v, info) in _setInfos)
            {
                int diff = ShapeSets.Compare(doc, v).Count;
                info.Text = $"BlendShape {ShapeSets.Count(doc, v)}개" + (v.Time.Length > 0 ? " · " + v.Time : "") + " · " + (diff == 0 ? "지금과 같아요" : $"지금과 다른 값 {diff}개");
                info.Fg(diff == 0 ? Ui.KSuccess : Ui.KText2);
            }
        }

        void ApplySet(Document.Variant v)
        {
            if (Doc == null) return;
            int n = ShapeSets.Apply(Doc, v);
            _ctx.Status(n == 0 ? $"\"{v.Name}\"과(와) 지금 값이 같아요." : $"\"{v.Name}\"(으)로 되돌렸어요 · 블렌드쉐이프 {n}개 (Ctrl+Z로 취소)");
        }

        async void OverwriteSet(Document.Variant v)
        {
            if (Doc == null || _setBusy || TopLevel.GetTopLevel(this) is not Window w) return;
            var doc = Doc;
            if (await Dialog.Ask(w, "지금 값으로 덮어쓸까요?", $"\"{v.Name}\"에 저장한 값과 사진을 지금 것으로 바꿔요. 되돌릴 수 없어요.", "덮어쓰기", "취소") != 0) return;
            _setBusy = true;
            try
            {
                var photo = await FacePhoto();
                if (doc != Doc) return;
                ShapeSets.Overwrite(doc, v, photo.Length > 0 ? photo : null);
                _ctx.Status($"\"{v.Name}\"을(를) 지금 값으로 덮어썼어요.");
                RefreshSets();
                if (_setFloats.TryGetValue(v.Id, out var f)) f.Refresh();
            }
            finally { _setBusy = false; }
        }

        async void DeleteSet(Document.Variant v)
        {
            if (Doc == null || TopLevel.GetTopLevel(this) is not Window w) return;
            var doc = Doc;
            if (await Dialog.Ask(w, "세팅을 지울까요?", $"\"{v.Name}\"을(를) 지워요. 되돌릴 수 없어요.", "지우기", "취소") != 0 || doc != Doc) return;
            ShapeSets.Remove(doc, v);
            CloseSetFloat(v.Id);
            RefreshSets();
            int n = ShapeSets.All(doc).Count();
            _tabs.SetBadge("sets", n > 0 ? n.ToString() : "");
        }

        // ---------------- 비교 창 ----------------
        static string SetFloatTitle(Document.Variant v) => "설정 비교 · " + v.Name;

        void CompareSet(Document.Variant v)
        {
            var host = SubTabs.Host;
            if (host == null || Doc == null) return;
            if (_setFloats.TryGetValue(v.Id, out var open)) { open.Float.Front(); open.Refresh(); return; }
            var body = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
            var view = new ScrollViewer { Content = body };
            string livePhoto = "";
            bool shooting = false;
            var photos = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
            // 제목과 되돌리기 단추가 좁은 비교 창에서 서로 덮이지 않도록
            // 버튼은 Auto 열, 제목은 남은 열에 배치한다.
            var diffHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var diffList = new StackPanel();
            var apply = Ui.Button("이 세팅으로 되돌리기", () => ApplySet(v), "블렌드쉐이프만 바꿔요 (Ctrl+Z로 취소)", Glyph.Undo, accent: true);
            apply.Margin = new Thickness(0);
            Grid.SetColumn(apply, 1);
            var diffTitle = Ui.Text("", 14, FontWeight.SemiBold);
            diffTitle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(diffTitle, 0);
            diffHead.Children.Add(diffTitle); diffHead.Children.Add(apply);
            body.Children.Add(photos); body.Children.Add(diffHead); body.Children.Add(Ui.Card(diffList));

            Control Side(string title, string b64, string empty, Control? act)
            {
                var p = new StackPanel { Spacing = 4 };
                var head = new DockPanel { Height = 28 };   // 다시 찍기 단추가 있는 쪽과 없는 쪽의 사진 높이를 맞춘다
                if (act != null) { DockPanel.SetDock(act, Dock.Right); head.Children.Add(act); }
                var t = Ui.Text(title, 12, FontWeight.SemiBold, Ui.KText2); t.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(t);
                p.Children.Add(head);
                var box = PhotoBox(b64, 0, empty);
                box.Width = double.NaN; box.Height = double.NaN;
                p.Children.Add(new Viewbox { Child = new Border { Width = PhotoSize, Height = PhotoSize, Child = box }, Stretch = Stretch.Uniform, MaxHeight = 280 });
                return p;
            }

            void DrawPhotos()
            {
                photos.Children.Clear();
                var saved = Side("저장한 것", v.Photo, "사진 없음", null);
                var now = Side("지금", livePhoto, shooting ? "찍는 중…" : "미리보기가 없어요", Ui.IconButton(Glyph.Refresh, Shoot, "지금 얼굴 다시 찍기"));
                Grid.SetColumn(saved, 0); Grid.SetColumn(now, 2);
                photos.Children.Add(saved); photos.Children.Add(now);
            }

            async void Shoot()
            {
                if (shooting) return;
                shooting = true; DrawPhotos();
                try { livePhoto = await FacePhoto(); }
                finally { shooting = false; DrawPhotos(); }
            }

            void DrawDiffs()
            {
                diffList.Children.Clear();
                if (Doc == null) return;
                var diffs = ShapeSets.Compare(Doc, v);
                diffTitle.Text = diffs.Count == 0 ? "지금과 같아요" : $"다른 값 {diffs.Count}개";
                apply.IsEnabled = diffs.Count > 0;
                if (diffs.Count == 0) { diffList.Children.Add(new Border { Padding = new Thickness(0, 10), Child = Ui.Wrap("블렌드쉐이프 값이 이 세팅과 모두 같아요.") }); return; }
                bool many = diffs.Select(d => d.Addr.Target).Distinct().Count() > 1;
                foreach (var d in diffs.Take(200))
                {
                    var label = Ui.Text((many ? MeshName(d.Addr.Target) + " · " : "") + Disp(d.Addr.Name), 13);
                    label.TextTrimming = TextTrimming.CharacterEllipsis;
                    ToolTip.SetTip(label, d.Addr.Name);
                    var vals = Ui.Text($"지금 {Ui.F(d.Now)} → {Ui.F(d.Saved)}", 12, FontWeight.Normal, Ui.KText2);
                    vals.VerticalAlignment = VerticalAlignment.Center; vals.Margin = new Thickness(8, 0, 4, 0);
                    var one = Ui.IconButton(Glyph.Undo, () => { if (Doc != null) Doc.Set(d.Addr, JNode.From(d.Saved), "BlendShape 하나 되돌리기: " + d.Addr.Name); }, "이 값만 세팅대로");
                    var row = new DockPanel { MinHeight = 32 };
                    DockPanel.SetDock(one, Dock.Right); DockPanel.SetDock(vals, Dock.Right);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(one); row.Children.Add(vals); row.Children.Add(label);
                    diffList.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, 1, 0, 0) }.Stroke(Ui.KDivider));
                }
                if (diffs.Count > 200) diffList.Children.Add(Ui.Wrap($"… 그 밖에 {diffs.Count - 200}개"));
            }

            void Refresh() { DrawDiffs(); Shoot(); }
            DrawPhotos(); DrawDiffs();
            var f = host.Show("shapes/set:" + v.Id, "shapes", SetFloatTitle(v), view, canDock: false, new Size(520, 640));
            _setFloats[v.Id] = (f, Refresh);
            f.DockRequested += () => CloseSetFloat(v.Id);
            f.Closed += (_, __) => _setFloats.Remove(v.Id);
            Shoot();
        }

        void CloseSetFloat(string id)
        {
            if (!_setFloats.Remove(id, out var f)) return;
            SubTabs.Host?.Close(f.Float);
        }

        /// <summary>
        /// 화면 확인 (--ui-shots): "save:이름" = 지금 값 저장(사진 기다림), "change" = 몇 개 바꾸기, "compare" = 첫 세팅 비교 창, "apply" = 되돌리기 확인,
        /// "end" = 바꾼 것·세팅·창 정리. 돌려주는 글은 시험 결과 한 줄.
        /// </summary>
        internal async Task<string?> SetsShot(string step)
        {
            var doc = Doc;
            if (doc == null || _renderer == null) return "FAIL 문서 없음";
            var names = ShapeNames().Where(n => !n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)).ToList();
            if (step.StartsWith("save:"))
            {
                _tabs.Select("sets");
                foreach (var n in names.Take(3)) doc.Set(Addr.BlendShape(_renderer, n), JNode.From(70.0), "시험");
                _setName.Text = step.Substring(5);
                SaveSet();
                for (int i = 0; i < 100 && _setBusy; i++) await Task.Delay(100);
                var v = ShapeSets.All(doc).LastOrDefault();
                return v == null ? "FAIL 저장 안 됨" : $"{(v.Photo.Length > 0 ? "ok" : "FAIL")} 저장 \"{v.Name}\" · 블렌드쉐이프 {ShapeSets.Count(doc, v)}개 · 사진 {v.Photo.Length * 3 / 4 / 1024} KB · 카메라 그대로 {!_ctx.Camera.IsNull}";
            }
            if (step == "change")
            {
                foreach (var n in names.Skip(1).Take(4)) doc.Set(Addr.BlendShape(_renderer, n), JNode.From(20.0), "시험");
                RefreshSetInfo();
                var v = ShapeSets.All(doc).Last();
                var diffs = ShapeSets.Compare(doc, v).Count;
                return $"{(diffs > 0 ? "ok" : "FAIL")} 바꾼 뒤 다른 값 {diffs}개 · 목록 글 \"{_setInfos.LastOrDefault().Info?.Text}\"";
            }
            if (step == "compare")
            {
                var v = ShapeSets.All(doc).Last();
                CompareSet(v);
                await Task.Delay(1500);
                return _setFloats.ContainsKey(v.Id) ? $"ok 비교 창 \"{SetFloatTitle(v)}\"" : "FAIL 비교 창 없음";
            }
            if (step == "apply")
            {
                var v = ShapeSets.All(doc).Last();
                int n = ShapeSets.Apply(doc, v);
                int left = ShapeSets.Compare(doc, v).Count;
                doc.Undo();
                int back = ShapeSets.Compare(doc, v).Count;
                ShapeSets.Apply(doc, v);
                RefreshSetInfo();
                return $"{(n > 0 && left == 0 && back == n ? "ok" : "FAIL")} 되돌리기 {n}개 → 남은 차이 {left}, 실행 취소 → {back}, 다시 되돌림";
            }
            if (step == "save2")
            {
                foreach (var n in names.Take(2)) doc.Set(Addr.BlendShape(_renderer, n), JNode.From(0.0), "시험");
                foreach (var n in names.Skip(5).Take(3)) doc.Set(Addr.BlendShape(_renderer, n), JNode.From(100.0), "시험");
                _setName.Text = "다른 얼굴";
                SaveSet();
                for (int i = 0; i < 100 && _setBusy; i++) await Task.Delay(100);
                return $"ok 둘째 세팅 · 세팅 {ShapeSets.All(doc).Count()}개";
            }
            if (step == "slides")
            {
                ShowSlides();
                await Task.Delay(1500);
                var first = ShapeSets.All(doc).First();
                _slideGo?.Invoke(first.Id);
                await Task.Delay(300);
                return _slides != null ? $"ok 슬라이드 쇼 창 · \"{first.Name}\"로 넘김 · 지금 사진 {(_livePhoto.Length > 0 ? "있음" : "없음")}" : "FAIL 슬라이드 쇼 창 없음";
            }
            if (step == "quicksave")
            {
                _tabs.Select("list");
                int before = ShapeSets.All(doc).Count();
                doc.Set(Addr.BlendShape(_renderer, names[0]), JNode.From(55.0), "시험");
                _quickSave?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 100 && _setBusy; i++) await Task.Delay(100);
                var v = ShapeSets.All(doc).LastOrDefault();
                return $"{(ShapeSets.All(doc).Count() == before + 1 && v != null && v.Photo.Length > 0 ? "ok" : "FAIL")} 목록 탭 \"현재 세팅 저장\" → \"{v?.Name}\" · 세팅 {ShapeSets.All(doc).Count()}개";
            }
            if (step == "menu")
            {
                _tabs.Select("list");
                var menu = new MenuFlyout();
                FillSetsMenu(menu);
                var items = menu.Items.OfType<MenuItem>().ToList();
                var target = ShapeSets.All(doc).First();
                var pick = items.FirstOrDefault(i => i.IsEnabled && i.Icon != null && i.Header is StackPanel);
                pick?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                int left = ShapeSets.Compare(doc, target).Count;
                return $"{(pick != null && left == 0 ? "ok" : "FAIL")} \"세팅\" 메뉴 항목 {items.Count}개 · 첫 세팅 불러오기 → 남은 차이 {left}";
            }
            foreach (var id in _setFloats.Keys.ToList()) CloseSetFloat(id);
            if (_slides is { } sl) SubTabs.Host?.Close(sl.Float);
            foreach (var v in ShapeSets.All(doc).ToList()) ShapeSets.Remove(doc, v);
            doc.ResetWhere(a => a.Kind == AddrKind.BlendShape, "시험 정리");
            RefreshSets();
            _tabs.Select("list");
            return null;
        }
    }
}
