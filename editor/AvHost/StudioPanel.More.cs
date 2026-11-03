// "스튜디오" 2차 (2026-10-19): 소품 · 심도 · 녹화. 모두 쓸 때만 렌더러에 올라간다 (지연 로드 — 켤 때 지난 소품은 "다시 세우기"로, 심도는 꺼진 채 시작).
//   소품  가진 패키지·프리팹·FBX(또는 IR)를 가져와 아바타 옆에 세운다. FBX는 Unity 프로젝트 밖의 것도 된다 (2026-10-27, 기본 임포트 설정·내장 머티리얼). 아바타 문서·올리기와 무관한 촬영용이라 실행 취소 없이 설정(avhost.json)에 기억한다.
//   심도  배경 흐림. 초점은 자동(얼굴) 또는 거리. "초점 영역 보기"로 앞(빨강)·뒤(파랑)를 확인한다.
//   녹화  지금 카메라 그대로(흔들림·표정·트래킹), 원하면 카메라 달리(다가가기·물러나기·저장한 위치까지). MP4는 Windows Media Foundation으로 묶고, PNG 연속 이미지는 투명 배경도 된다.
//         렌더러가 시간을 프레임마다 고르게 흘리며 찍으므로 실시간보다 오래 걸릴 수 있다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class StudioPanel
    {
        // ---------------- 소품 ----------------
        Ui.FoldSection? _propSection;
        readonly StackPanel _propList = new StackPanel { Spacing = 8 };
        readonly Dictionary<string, TextBlock> _propState = new Dictionary<string, TextBlock>();

        void BuildProps()
        {
            var add = Ui.Button("추가…", AddProp, "패키지(.unitypackage·.zip)·프리팹·씬·FBX 모델에서 가져오거나 이미 가져온 파일(.ir.json)을 골라요", Glyph.Add);
            add.Margin = new Thickness(0);
            _propSection = new Ui.FoldSection("소품", _settings.StudioProps.Count > 0, add, "의자·배경 소품처럼 가진 모델(Unity 패키지·프리팹, FBX 파일)을 아바타 옆에 세워요. 촬영용이라 아바타에는 들어가지 않고 올리기와도 무관해요. 위치는 아바타 발밑 가운데가 0이에요.");
            _reloadProps = Ui.Button("다시 세우기", () => { _propsLoaded = true; RefreshProps(); SendProps(); }, "지난번에 세운 소품을 미리보기에 다시 올려요", Glyph.Undo);
            _reloadProps.Margin = new Thickness(0, 0, 0, 4);
            _reloadProps.HorizontalAlignment = HorizontalAlignment.Left;
            _propSection.Body.Children.Add(_reloadProps);
            _propSection.Body.Children.Add(_propList);
            _main.Children.Add(_propSection);
            RefreshProps();
        }

        Button _reloadProps = null!;

        JNode PropsMessage()
        {
            var arr = JNode.Arr();
            foreach (var p in _settings.StudioProps.Items)
                arr.Add(JNode.Obj().Set("id", p["id"]).Set("path", p["path"]).Set("pos", p["pos"]).Set("rotY", p["rotY"]).Set("scale", p["scale"]).Set("visible", p["visible"]));
            return arr;
        }

        /// 소품을 올린 뒤에만 보낸다 (올리기 전에 고친 위치·크기는 설정에만 남고 다시 세울 때 쓰인다)
        void SendProps() { if (_propsLoaded) Send(JNode.Obj().Set("props", PropsMessage())); }

        async void AddProp()
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("소품 가져오기 (패키지·프리팹·씬·FBX·가져온 파일)"), AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Unity 패키지·프리팹·씬, FBX 모델, 가져온 파일") { Patterns = new[] { "*.unitypackage", "*.zip", "*.prefab", "*.unity", "*.fbx", "*.ir.json" } } },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
            if (paths.Length == 0) return;
            string ir;
            var ownIr = paths.FirstOrDefault(p => p.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase));
            if (ownIr != null) ir = ownIr;
            else
            {
                var src = paths;
                if (src.Any(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    var notes = new List<string>();
                    try { src = await Task.Run(() => Packages.Expand(paths, notes)); }
                    catch (Exception e) { _ctx.Status("zip을 열지 못했습니다: " + e.Message); return; }
                    if (src.Length == 0) { _ctx.Status("zip 안에 패키지가 없어요."); return; }
                }
                if (!src.Any(Packages.IsPackage)) src = new[] { src[0] };   // 씬·프리팹·FBX는 하나만
                var r = await new ImportWindow(src) { Models = true }.ShowDialog<ImportResult?>(w);
                if (r == null) { _ctx.Status("소품 가져오기를 취소했어요."); return; }
                ir = r.IrPath;
            }
            var name = Path.GetFileName(ownIr ?? paths[0]);
            foreach (var ext in new[] { ".ir.json", ".unitypackage", ".zip", ".prefab", ".unity", ".fbx" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - ext.Length); break; }
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            _settings.StudioProps.Add(JNode.Obj().Set("id", id).Set("name", name).Set("path", ir)
                .Set("pos", JNode.FloatArray(new[] { 0.8f, 0f, 0f })).Set("rotY", 0f).Set("scale", 1f).Set("visible", true));
            _settings.Save();
            _propsLoaded = true;   // 새 소품을 세우면 목록의 다른 소품도 함께 올라간다
            RefreshProps();
            if (_propSection != null) _propSection.IsOpen = true;
            SendProps();
            _ctx.Status($"소품 {name}을(를) 세웠어요. 위치·회전·크기는 소품 카드에서 맞춰요.");
        }

        void RefreshProps()
        {
            _propList.Children.Clear(); _propState.Clear();
            var list = _settings.StudioProps;
            if (_propSection != null) _propSection.Hint = list.Count == 0 ? "없음" : $"{list.Count}개";
            _reloadProps.IsVisible = list.Count > 0 && !_propsLoaded;
            foreach (var p in list.Items)
            {
                var prop = p;
                var id = prop["id"].AsString;
                var card = new StackPanel();
                var head = new DockPanel();
                var del = Ui.Button("지우기", () => RemoveProp(id), "이 소품을 내려요 (가져온 파일은 남아요)", Glyph.Delete);
                del.Margin = new Thickness(0);
                var vis = new CheckBox { Content = "보이기", IsChecked = prop["visible"].IsNull || prop["visible"].AsBool, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                vis.IsCheckedChanged += (_, __) => { prop.Set("visible", vis.IsChecked == true); _settings.Save(); SendProps(); };
                DockPanel.SetDock(del, Dock.Right); DockPanel.SetDock(vis, Dock.Right);
                head.Children.Add(del); head.Children.Add(vis);
                var title = Ui.Text(prop["name"].AsString, 14, FontWeight.SemiBold); title.VerticalAlignment = VerticalAlignment.Center;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                head.Children.Add(title);
                card.Children.Add(head);
                var pos = prop["pos"].ToFloatArray();
                void Row(string label, double min, double max, double value, Action<double> set)
                {
                    var s = new Ui.EditSlider(min, max, v => { set(v); SendProps(); }, v => { set(v); _settings.Save(); SendProps(); });
                    s.SetQuiet(value);
                    card.Children.Add(Ui.PropRow(Ui.Text(label), s.Slider, null));
                }
                Row("좌우 (m)", -3, 3, pos[0], v => { pos[0] = (float)v; prop.Set("pos", JNode.FloatArray(pos)); });
                Row("앞뒤 (m)", -3, 3, pos[2], v => { pos[2] = (float)v; prop.Set("pos", JNode.FloatArray(pos)); });
                Row("높이 (m)", -1, 3, pos[1], v => { pos[1] = (float)v; prop.Set("pos", JNode.FloatArray(pos)); });
                Row("돌리기 (도)", -180, 180, prop["rotY"].AsDouble, v => prop.Set("rotY", (float)v));
                Row("크기", 0.1, 3, prop["scale"].IsNull ? 1 : prop["scale"].AsDouble, v => prop.Set("scale", (float)v));
                var st = Ui.Text(_propsLoaded ? "불러오는 중…" : "아직 세우지 않았어요 (위 \"다시 세우기\")", 12, FontWeight.Normal, Ui.KText2);
                st.TextWrapping = TextWrapping.Wrap;
                _propState[id] = st;
                card.Children.Add(st);
                _propList.Children.Add(Ui.Card(card));
            }
        }

        /// <summary>VR로 잡아 놓은 소품 (VrItemGrab, 2026-11-01): 값을 적고 카드를 다시 그린다.</summary>
        public void OnVrPropMoved(JNode m)
        {
            var id = m["id"].AsString;
            foreach (var p in _settings.StudioProps.Items)
            {
                if (p["id"].AsString != id) continue;
                p.Set("pos", m["pos"].Clone()).Set("rotY", m["rotY"].AsFloat).Set("scale", Math.Clamp(m["scale"].AsFloat, 0.1f, 3f));
                _settings.Save();
                RefreshProps();
                SendProps();   // 크기 범위를 렌더러에도 맞추고, 카드 상태 글을 받는다
                _ctx.Status($"소품 {p["name"].AsString}을(를) 옮겼어요.");
                return;
            }
        }

        void RemoveProp(string id)
        {
            var arr = JNode.Arr();
            foreach (var p in _settings.StudioProps.Items) if (p["id"].AsString != id) arr.Add(p);
            _settings.StudioProps = arr; _settings.Save();
            RefreshProps();
            SendProps();
        }

        void OnPropsState(JNode props)
        {
            foreach (var p in props.Items)
                if (_propState.TryGetValue(p["id"].AsString, out var t))
                {
                    if (p["ok"].AsBool)
                    {
                        var sz = p["size"].ToFloatArray();
                        t.Text = $"메시 {p["renderers"].AsInt}개 · 크기 {sz[0]:F2} × {sz[1]:F2} × {sz[2]:F2} m";
                        t.Fg(Ui.KText2);
                    }
                    else { t.Text = "불러오지 못했습니다: " + p["error"].AsString; t.Fg(Ui.KCaution); }
                }
        }

        // ---------------- 심도 ----------------
        readonly CheckBox _dofOn = new CheckBox { Content = "켜기" }, _dofAuto = new CheckBox { Content = "자동 (얼굴에 맞춤)" }, _dofShow = new CheckBox { Content = "초점 영역 보기 (앞 빨강 · 뒤 파랑)" };
        Ui.EditSlider? _dofStrength, _dofDistance;
        readonly TextBlock _dofState = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        Ui.FoldSection? _dofSection;

        void BuildDof()
        {
            var d = _settings.StudioDof;
            d.Set("enabled", false);   // 매 프레임 깊이 패스가 드는 일이라 켤 때는 꺼진 채 시작한다 (세기·초점은 기억)
            _dofSection = new Ui.FoldSection("심도 (배경 흐림)", false);
            _dofStrength = new Ui.EditSlider(0.05, 1, v => { d.Set("strength", (float)v); SendDof(); }, v => { d.Set("strength", (float)v); _settings.Save(); SendDof(); });
            _dofDistance = new Ui.EditSlider(0.2, 20, v => { d.Set("distance", (float)v); SendDof(); }, v => { d.Set("distance", (float)v); _settings.Save(); SendDof(); });
            _dofOn.IsChecked = d["enabled"].AsBool;
            _dofAuto.IsChecked = !d.Has("auto") || d["auto"].AsBool;
            _dofShow.IsChecked = false;   // 보기는 기억하지 않는다 (촬영에 섞이지 않게)
            _dofStrength.SetQuiet(d.Has("strength") ? d["strength"].AsDouble : 0.5);
            _dofDistance.SetQuiet(d.Has("distance") ? d["distance"].AsDouble : 2.5);
            _dofDistance.Slider.IsEnabled = _dofAuto.IsChecked != true;
            _syncUi.Add(() =>
            {
                _dofOn.IsChecked = d["enabled"].AsBool; _dofAuto.IsChecked = !d.Has("auto") || d["auto"].AsBool;
                _dofStrength.SetQuiet(d.Has("strength") ? d["strength"].AsDouble : 0.5); _dofDistance.SetQuiet(d.Has("distance") ? d["distance"].AsDouble : 2.5);
                _dofDistance.Slider.IsEnabled = _dofAuto.IsChecked != true;
            });
            _dofOn.IsCheckedChanged += (_, __) => { if (_quiet) return; d.Set("enabled", _dofOn.IsChecked == true); _settings.Save(); SendDof(); };
            _dofAuto.IsCheckedChanged += (_, __) => { d.Set("auto", _dofAuto.IsChecked == true); _dofDistance.Slider.IsEnabled = _dofAuto.IsChecked != true; _settings.Save(); SendDof(); };
            _dofShow.IsCheckedChanged += (_, __) => SendDof();
            var c = new StackPanel();
            c.Children.Add(Ui.PropRow(Ui.Text("심도"), _dofOn, null));
            c.Children.Add(Ui.PropRow(Ui.Text("흐림 세기"), _dofStrength.Slider, null));
            // 쉬운 모드: 초점은 자동(얼굴)으로 두고 세기만
            c.Children.Add(Ui.Expert(Ui.PropRow(Ui.Text("초점"), _dofAuto, null)));
            c.Children.Add(Ui.Expert(Ui.PropRow(Ui.Text("초점 거리 (m)"), _dofDistance.Slider, null)));
            c.Children.Add(Ui.Expert(Ui.PropRow(Ui.Text("확인"), _dofShow, null)));
            c.Children.Add(Ui.PropRow(Ui.Text("지금"), _dofState, null));
            _dofSection.Body.Children.Add(Ui.Card(c));
            _dofSection.Hint = d["enabled"].AsBool ? "켜짐" : "꺼짐";
            _main.Children.Add(_dofSection);
        }

        // ---------------- 고화질 (2026-10-23) ----------------
        /// <summary>고화질 값을 바꿨을 때 (MainWindow.SendQuality — 스튜디오 페이지를 보는 동안만 렌더러가 고화질).</summary>
        public Action? QualityChanged;

        void BuildQuality()
        {
            var on = new Ui.Switch(v => { _settings.StudioQuality = v; _settings.Save(); QualityChanged?.Invoke(); });
            on.SetQuiet(_settings.StudioQuality);
            var ao = new Ui.EditSlider(0, 1, v => { _settings.StudioAo = v; QualityChanged?.Invoke(); }, v => { _settings.StudioAo = v; _settings.Save(); QualityChanged?.Invoke(); });
            ao.SetQuiet(_settings.StudioAo);
            var aaOpts = new[] { (1, "끄기 (한 번만)"), (4, "4장"), (8, "8장 (기본)"), (16, "16장"), (32, "32장 (아주 느림)") };
            var aa = Ui.CellCombo(new ComboBox { ItemsSource = aaOpts.Select(x => x.Item2).ToArray() });
            aa.SelectedIndex = Math.Max(0, Array.FindIndex(aaOpts, x => x.Item1 == _settings.StudioCaptureAa));
            _syncUi.Add(() => { on.SetQuiet(_settings.StudioQuality); ao.SetQuiet(_settings.StudioAo); aa.SelectedIndex = Math.Max(0, Array.FindIndex(aaOpts, x => x.Item1 == _settings.StudioCaptureAa)); });
            aa.SelectionChanged += (_, __) => { _settings.StudioCaptureAa = aaOpts[Math.Max(0, aa.SelectedIndex)].Item1; _settings.Save(); QualityChanged?.Invoke(); };
            var c = new StackPanel();
            c.Children.Add(Ui.HelpRow("고화질", "스튜디오 페이지를 보는 동안(방송 오버레이 포함) VRChat과 똑같이 보이는 것은 신경 쓰지 않고 화질을 최대로 올려요: 안티에일리어싱 8×, 그림자 8K·장면에 딱 맞춘 범위, 픽셀 라이트 제한 없음, 모든 텍스처 이방성 필터, 주변 가림. 다른 페이지로 가면 VRChat과 같은 품질로 돌아가요", on.Control));
            c.Children.Add(Ui.HelpRow("주변 가림", "옷이 겹친 곳·목 아래·발밑에 은은한 그늘을 넣어 입체감을 줘요 (0 = 끄기)", ao.Slider, wide: true));
            c.Children.Add(Ui.HelpRow("촬영 매끄럽게", "촬영·녹화 한 장을 카메라를 아주 조금씩 흔들며 여러 번 찍어 평균해요. 외곽선·머리카락 끝의 계단과 주변 가림 잡티가 사라져요. 장 수만큼 오래 걸려요", aa));
            _main.Children.Add(Ui.SectionTitle("화질"));
            _main.Children.Add(Ui.Card(c));
        }

        JNode DofMessage()
        {
            var d = _settings.StudioDof;
            return JNode.Obj().Set("enabled", d["enabled"].AsBool).Set("strength", d.Has("strength") ? d["strength"].AsFloat : 0.5f)
                .Set("auto", !d.Has("auto") || d["auto"].AsBool).Set("distance", d.Has("distance") ? d["distance"].AsFloat : 2.5f).Set("show", _dofShow.IsChecked == true);
        }

        void SendDof()
        {
            if (_dofSection != null) _dofSection.Hint = _settings.StudioDof["enabled"].AsBool ? "켜짐" : "꺼짐";
            if (_studioOn || _settings.StudioDof["enabled"].AsBool) Send(JNode.Obj().Set("dof", DofMessage()));   // 꺼진 채 고치는 값은 렌더러를 깨우지 않는다
        }

        // ---------------- 녹화 ----------------
        static readonly (string Label, int W, int H)[] RecordSizes =
        {
            ("FHD 1920×1080", 1920, 1080), ("세로 1080×1920", 1080, 1920), ("정사각 1080×1080", 1080, 1080),
            ("HD 1280×720", 1280, 720), ("QHD 2560×1440", 2560, 1440), ("4K 3840×2160", 3840, 2160),
        };
        static readonly int[] RecordSeconds = { 3, 5, 8, 10, 15, 20, 30, 60 };
        static readonly int[] RecordFps = { 24, 30, 60 };
        // 카메라 달리: 설정 값 "fixed" | "in" | "out" | "cam:N"(N번째 저장한 위치까지). 콤보 항목은 저장한 위치가 바뀌면 다시 만든다
        readonly ComboBox _recMotion = Ui.CellCombo(new ComboBox());
        readonly List<string> _motionKeys = new List<string>();
        const float DollyIn = 0.6f, DollyOut = 1.6f;   // 다가가기·물러나기: 끝 거리 = 지금 거리 × 이 값
        readonly ProgressBar _recProgress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
        readonly ComboBox _recSeconds = Ui.CellCombo(new ComboBox { ItemsSource = RecordSeconds.Select(s => s + "초").ToList() });
        readonly ComboBox _recFps = Ui.CellCombo(new ComboBox { ItemsSource = RecordFps.Select(f => f + " fps").ToList() });
        readonly ComboBox _recSize = Ui.CellCombo(new ComboBox { ItemsSource = RecordSizes.Select(s => s.Label).ToList() });
        readonly ComboBox _recFormat = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "MP4 동영상", "PNG 연속 이미지" } });
        readonly CheckBox _recTransparent = new CheckBox { Content = "투명 배경 (PNG만)" };
        readonly TextBlock _recState = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        Button? _recButton;
        RecordJob? _job;
        int _recTotal;

        void BuildRecord()
        {
            var r = _settings.StudioRecord;
            var sec = new Ui.FoldSection("녹화", false, help: "MP4는 Windows에 들어 있는 H.264 인코더로 만듭니다. 투명 배경이 필요하면 PNG 연속 이미지로 녹화해 편집 프로그램에서 묶습니다. "
                + "웹캠 트래킹·립싱크가 켜져 있으면 실제 시간 그대로 녹화하고(그리기가 늦으면 같은 장을 되풀이), 립싱크 마이크 소리도 함께 담습니다 (PNG는 옆에 audio.wav).");
            int Idx<T>(T[] arr, T v, int def) { int i = Array.IndexOf(arr, v); return i >= 0 ? i : def; }
            RefreshMotions();
            _recSeconds.SelectedIndex = Idx(RecordSeconds, r.Has("seconds") ? r["seconds"].AsInt : 8, 2);
            _recFps.SelectedIndex = Idx(RecordFps, r.Has("fps") ? r["fps"].AsInt : 30, 1);
            int si = Array.FindIndex(RecordSizes, s => s.W == r["width"].AsInt && s.H == r["height"].AsInt);
            _recSize.SelectedIndex = si >= 0 ? si : 0;
            _recFormat.SelectedIndex = r["format"].AsString == "png" ? 1 : 0;
            _recTransparent.IsChecked = r["transparent"].AsBool;
            _recTransparent.IsEnabled = _recFormat.SelectedIndex == 1;
            void Save()
            {
                if (_quietMotion) return;
                var s = RecordSizes[Math.Max(0, _recSize.SelectedIndex)];
                _settings.StudioRecord = JNode.Obj().Set("motion", MotionKey())
                    .Set("seconds", RecordSeconds[Math.Max(0, _recSeconds.SelectedIndex)]).Set("fps", RecordFps[Math.Max(0, _recFps.SelectedIndex)])
                    .Set("width", s.W).Set("height", s.H).Set("format", _recFormat.SelectedIndex == 1 ? "png" : "mp4").Set("transparent", _recTransparent.IsChecked == true);
                _settings.Save();
                _recTransparent.IsEnabled = _recFormat.SelectedIndex == 1;
                sec.Hint = $"{(MotionKey() == "fixed" ? "고정" : "달리")} · {RecordSeconds[Math.Max(0, _recSeconds.SelectedIndex)]}초 · {(_recFormat.SelectedIndex == 1 ? "PNG" : "MP4")}";
            }
            foreach (var cb in new[] { _recMotion, _recSeconds, _recFps, _recSize, _recFormat }) cb.SelectionChanged += (_, __) => Save();
            _recTransparent.IsCheckedChanged += (_, __) => Save();
            var c = new StackPanel();
            var motionRow = Ui.PropRow(Ui.Text("카메라"), _recMotion, null);
            ToolTip.SetTip(motionRow, "고정: 지금 카메라 그대로 · 다가가기·물러나기: 녹화하는 동안 천천히 · 저장한 위치까지: 지금 자리에서 그 위치로 옮겨 가며");
            c.Children.Add(motionRow);
            c.Children.Add(Ui.PropRow(Ui.Text("길이"), _recSeconds, null));
            c.Children.Add(Ui.Expert(Ui.PropRow(Ui.Text("초당 프레임"), _recFps, null)));
            c.Children.Add(Ui.PropRow(Ui.Text("크기"), _recSize, null));
            c.Children.Add(Ui.PropRow(Ui.Text("형식"), _recFormat, null));
            c.Children.Add(Ui.PropRow(Ui.Text("배경"), _recTransparent, null));
            sec.Body.Children.Add(Ui.Card(c));
            _recButton = Ui.WideButton("녹화…", ToggleRecord, "지금 카메라·배경·소품·심도 그대로 녹화해요 (한 프레임씩 고르게 찍어서 실시간보다 오래 걸릴 수 있어요)", Glyph.Record, accent: true);
            _recButton.Margin = new Thickness(0, 10, 0, 0);
            sec.Body.Children.Add(_recButton);
            sec.Body.Children.Add(_recProgress);
            _recState.TextWrapping = TextWrapping.Wrap; _recState.Margin = new Thickness(0, 6, 0, 0);
            sec.Body.Children.Add(_recState);
            Save();
            _syncUi.Add(() =>
            {
                var v = _settings.StudioRecord;
                _quietMotion = true;
                _recSeconds.SelectedIndex = Idx(RecordSeconds, v.Has("seconds") ? v["seconds"].AsInt : 8, 2);
                _recFps.SelectedIndex = Idx(RecordFps, v.Has("fps") ? v["fps"].AsInt : 30, 1);
                int k = Array.FindIndex(RecordSizes, s => s.W == v["width"].AsInt && s.H == v["height"].AsInt);
                _recSize.SelectedIndex = k >= 0 ? k : 0;
                _recFormat.SelectedIndex = v["format"].AsString == "png" ? 1 : 0;
                _recTransparent.IsChecked = v["transparent"].AsBool;
                _quietMotion = false;
                RefreshMotions();
                Save();
            });
            _main.Children.Add(sec);
        }

        bool _quietMotion;

        string MotionKey() => _recMotion.SelectedIndex >= 0 && _recMotion.SelectedIndex < _motionKeys.Count ? _motionKeys[_recMotion.SelectedIndex] : "fixed";

        /// <summary>카메라 달리 목록 (저장한 위치가 바뀌면 다시). 고른 것은 설정 값으로 되살린다.</summary>
        void RefreshMotions()
        {
            var want = _settings.StudioRecord["motion"].AsStringOrNull ?? "fixed";
            var items = new List<string> { "고정 (지금 카메라 그대로)", "다가가기 (달리 인)", "물러나기 (달리 아웃)" };
            _motionKeys.Clear(); _motionKeys.AddRange(new[] { "fixed", "in", "out" });
            var cams = _settings.Cameras;
            for (int i = 0; i < cams.Count; i++) { items.Add($"{cams[i]["name"].AsString}까지 옮겨 가기"); _motionKeys.Add("cam:" + (i + 1)); }
            _quietMotion = true;
            _recMotion.ItemsSource = items;
            int idx = _motionKeys.IndexOf(want);
            _recMotion.SelectedIndex = idx >= 0 ? idx : 0;
            _quietMotion = false;
        }

        /// <summary>달리의 끝 카메라 상태 (고정이면 null).</summary>
        JNode? DollyTarget(string motion)
        {
            var cur = _ctx.Camera;
            if (motion == "fixed" || cur.IsNull) return null;
            if (motion == "in" || motion == "out") return cur.Clone().Set("distance", cur["distance"].AsDouble * (motion == "in" ? DollyIn : DollyOut));
            if (motion.StartsWith("cam:") && int.TryParse(motion.Substring(4), out var n) && n >= 1 && n <= _settings.Cameras.Count) return _settings.Cameras[n - 1]["state"];
            return null;
        }

        async void ToggleRecord()
        {
            if (_job != null)
            {
                // 두 번째 누름: 렌더러가 답하지 않아도 여기서 끝낸다 (렌더러가 멈췄거나 연결이 끊긴 경우)
                if (_stopAsked) { _recState.Text = "녹화를 끝냈어요."; _job.Complete(cancel: true); return; }
                _stopAsked = true;
                Send(JNode.Obj().Set("t", Msg.Record).Set("cancel", true));
                _recState.Text = "멈추는 중… (다시 누르면 바로 끝내요)";
                return;
            }
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 녹화할 수 있어요."); return; }
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var r = _settings.StudioRecord;
            bool mp4 = r["format"].AsString != "png";
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var baseName = (_ctx.Doc?.Original.Name is string n && n.Length > 0 ? n : "avatar") + "_" + stamp;
            string output;
            if (mp4)
            {
                var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Texts.T("녹화 저장 (MP4)"), SuggestedFileName = baseName + ".mp4", DefaultExtension = "mp4",
                    FileTypeChoices = new[] { new FilePickerFileType("MP4 동영상") { Patterns = new[] { "*.mp4" } } },
                });
                if (file?.TryGetLocalPath() is not string p) return;
                output = p;
            }
            else
            {
                var dirs = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Texts.T("PNG 연속 이미지를 넣을 폴더 (안에 새 폴더를 만들어요)") });
                if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not string d) return;
                output = Path.Combine(d, baseName);
            }
            int width = r["width"].AsInt, height = r["height"].AsInt, fps = r["fps"].AsInt;
            // MP4는 파이프로 바로 받는다 (2026-10-24, 예전엔 임시 폴더의 raw 파일). PNG는 렌더러가 고른 폴더에 바로 쓴다
            try { _job = mp4 ? RecordJob.Pipe(output) : new RecordJob(output, false, width, height, fps); }
            catch (Exception e) { _ctx.Status("녹화를 시작하지 못했습니다: " + e.Message); return; }
            var job = _job;
            job.Finished = err => Dispatcher.UIThread.Post(() => OnJobFinished(job, err));
            _recTotal = Math.Max(1, (int)Math.Round(fps * r["seconds"].AsDouble));
            _recRate = fps; _stopAsked = false;
            _recState.Text = "녹화 준비 중…";
            _recProgress.Value = 0; _recProgress.IsIndeterminate = false; _recProgress.IsVisible = true;
            if (_recButton != null) _recButton.Content = RecordButtonContent("녹화 멈추기");
            var msg = JNode.Obj().Set("t", Msg.Record).Set("format", mp4 ? "pipe" : "png").Set("width", width).Set("height", height)
                .Set("fps", fps).Set("seconds", r["seconds"].AsFloat).Set("mode", "fixed").Set("transparent", !mp4 && r["transparent"].AsBool);
            if (mp4) msg.Set("pipe", job.PipeName); else msg.Set("folder", job.FrameFolder);
            if (DollyTarget(r["motion"].AsStringOrNull ?? "fixed") is JNode to) msg.Set("to", to);
            EnsureStudio();   // 렌더러 녹화는 스튜디오 틀 위에서 돈다
            Send(msg);
            _ctx.Status($"녹화 중 · {width}×{height} {fps}fps {_recTotal}프레임");
        }

        static object RecordButtonContent(string label) => Ui.IconLabel(label == "녹화…" ? Glyph.Record : Glyph.Stop, label);
        int _recRate = 30;
        bool _stopAsked;
        string _recNote = "";
        DispatcherTimer? _encodeTimer;

        static string Sec(double s) => s < 60 ? $"{s:0.0}초" : $"{(int)s / 60}:{s % 60:00.0}";

        void OnRecordMessage(JNode m)
        {
            if (_job == null) return;
            if (Msg.Type(m) == Msg.RecordFrame)
            {
                _job.Add(m["path"].AsString);
                int i = m["index"].AsInt + 1, total = Math.Max(1, m["total"].AsInt);
                _recProgress.Value = (double)i / total;
                _recState.Text = $"{Sec((double)i / _recRate)} / {Sec((double)total / _recRate)} · 프레임 {i}/{total}" + (_job.Mp4 ? $" · MP4에 넣음 {_job.Encoded}" : "");
                return;
            }
            // recordDone
            bool ok = m["ok"].AsBool;
            int frames = m["frames"].AsInt;
            // 트래킹·말하기 중이면 렌더러가 실시간으로 녹화한다 (늦으면 같은 장을 되풀이), 말하기 소리도 함께
            _recNote = (m["realtime"].AsBool ? " · 실시간" : "") + (m["audioSeconds"].AsDouble > 0 ? $" · 소리 {m["audioSeconds"].AsDouble:0.0}초" : "");
            if (!ok && !m["cancelled"].AsBool) _recState.Text = "녹화하지 못했습니다: " + m["error"].AsString;
            else if (_job.Mp4 && ok)
            {
                // 남은 프레임을 MP4에 넣는 동안: 진행 바 = 넣은 프레임 / 찍은 프레임
                var job = _job;
                _encodeTimer?.Stop();
                _encodeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _encodeTimer.Tick += (_, __) =>
                {
                    if (_job != job) { _encodeTimer?.Stop(); return; }
                    _recProgress.Value = frames > 0 ? Math.Min(1, (double)job.Encoded / frames) : 1;
                    _recState.Text = $"MP4 마무리 중… {job.Encoded}/{frames}프레임";
                };
                _encodeTimer.Start();
            }
            else if (_job.Mp4) _recState.Text = "멈췄어요 · 정리 중…";
            _job.Complete(cancel: !ok);
        }

        void OnJobFinished(RecordJob job, string? err)
        {
            if (_job != job) return;
            _job = null;
            _encodeTimer?.Stop(); _encodeTimer = null;
            _recProgress.IsVisible = false;
            if (_recButton != null) _recButton.Content = RecordButtonContent("녹화…");
            if (err != null) { _recState.Text = "MP4를 만들지 못했습니다: " + err; _ctx.Status(_recState.Text); return; }
            bool exists = job.Mp4 ? File.Exists(job.Output) : Directory.Exists(job.Output) && Directory.EnumerateFiles(job.Output, "*.png").Any();
            if (!exists) { if (!_recState.Text!.StartsWith("녹화하지")) _recState.Text = "멈췄어요."; return; }
            _recState.Text = "저장했습니다" + _recNote + " → " + job.Output;
            _ctx.Status(_recState.Text);
        }

        /// <summary>렌더러가 다시 시작되면 하던 녹화는 끝난 것으로 친다.</summary>
        void AbortRecording()
        {
            if (_job == null) return;
            _recState.Text = "미리보기가 다시 시작되어 녹화를 멈췄어요.";
            _job.Complete(cancel: true);
        }
    }
}
