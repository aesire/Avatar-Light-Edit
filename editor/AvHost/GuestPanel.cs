// 아바타 페이지 "함께 세우기" 탭 (2026-10-25 사용자 "여러 아바타를 한 공간 안에 배치"): 지금 연 아바타 옆에 다른 아바타를 더 세운다.
//   범위 (사용자 결정): 자리·방향·크기, 자세(지금 아바타 자세 복사·처음으로 · VR에서 본 잡기), PhysBone(지금 아바타 설정을 따름)까지.
//   머티리얼·블렌드쉐이프·의상 편집과 올리기는 지금 연 아바타 하나만이다. 목록은 설정(avhost.json)에 기억하고 실행 취소는 없다 (소품과 같다).
//   옮기기: 카드의 슬라이더 · "옮기기"(미리보기 핸들, 조작 모드) · 미리보기에서 메시 더블클릭 · VR 그립 → guestMoved로 돌아와 목록에 적는다.
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
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class GuestPanel : StackPanel
    {
        readonly EditorContext _ctx;
        readonly Settings _settings;
        readonly StackPanel _list = new StackPanel { Spacing = 8 };
        readonly Dictionary<string, TextBlock> _state = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, Action> _syncCard = new Dictionary<string, Action>();
        JNode _lastState = JNode.Arr();

        /// <summary>MainWindow가 연결한다: 목록 보내기 · 미리보기에서 고르기(핸들) · 지금 아바타 자세 복사.</summary>
        public Action? SendList;
        public Action<string>? Select, CopyPose;

        public GuestPanel(EditorContext ctx, Settings settings)
        {
            _ctx = ctx; _settings = settings;
            Spacing = 8;
            var add = Ui.Button("추가…", () => _ = Add(), "가진 아바타의 패키지(.unitypackage·.zip)·프리팹·씬에서 가져오거나 이미 가져온 파일(.ir.json)을 골라요", Glyph.Add);
            var twin = Ui.Button("이 아바타 하나 더", AddTwin, "지금 연 아바타를 하나 더 세워요 (편집하기 전 원본 모습)", Glyph.Add);
            // 11-03: 긴 언어·좁은 창에서 버튼이 제목을 덮었다 — 모자라면 제목 아래 줄로 내리고(TitleRow) 그래도 넘치면 줄바꿈한다.
            add.Margin = new Thickness(0, 2, 6, 2); twin.Margin = new Thickness(0, 2, 0, 2);
            var buttons = new WrapPanel();
            buttons.Children.Add(add); buttons.Children.Add(twin);
            var head = new TitleRow();
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            head.Children.Add(Ui.SectionTitle("함께 세운 아바타", "다른 아바타를 같은 공간에 세워 함께 보고 찍어요. 자리·방향·크기와 자세, PhysBone까지 돼요. 머티리얼·블렌드쉐이프·의상 편집과 올리기는 지금 연 아바타만이에요. 목록은 이 PC에 기억되고 실행 취소는 없어요. VR에서는 그립으로 아바타째 잡아 옮기거나(관절이 없는 곳, 또는 트리거를 당긴 채) 관절을 잡아 자세를 바꿔요."));
            Children.Add(head);
            Children.Add(_list);
            Refresh();
        }

        JNode Guests => _settings.Guests;

        /// <summary>렌더러에 보낼 목록 (이름은 빼고).</summary>
        public JNode ListMessage()
        {
            var arr = JNode.Arr();
            foreach (var g in Guests.Items)
            {
                var o = JNode.Obj().Set("id", g["id"]).Set("path", g["path"]).Set("pos", g["pos"]).Set("rot", g["rot"]).Set("scale", g["scale"]).Set("visible", g["visible"]);
                if (!g["pose"].IsNull) o.Set("pose", g["pose"]);
                arr.Add(o);
            }
            return arr;
        }

        void Changed(bool save = true) { if (save) _settings.Save(); SendList?.Invoke(); }

        async Task Add()
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("함께 세울 아바타 가져오기 (패키지·프리팹·씬·가져온 파일)"), AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Unity 패키지·프리팹·씬, 가져온 파일") { Patterns = new[] { "*.unitypackage", "*.zip", "*.prefab", "*.unity", "*.ir.json" } } },
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
                if (!src.Any(Packages.IsPackage)) src = new[] { src[0] };   // 씬·프리팹은 하나만
                var r = await new ImportWindow(src).ShowDialog<ImportResult?>(w);
                if (r == null) { _ctx.Status("아바타 가져오기를 취소했어요."); return; }
                ir = r.IrPath;
            }
            AddEntry(ir, NameOf(ownIr ?? paths[0]));
        }

        void AddTwin()
        {
            var doc = _ctx.Doc;
            if (doc == null || string.IsNullOrEmpty(doc.IrPath)) { _ctx.Status("먼저 아바타를 열어 주세요."); return; }
            AddEntry(doc.IrPath, (doc.Original.Name.Length > 0 ? doc.Original.Name : "아바타") + " (하나 더)");
        }

        static string NameOf(string path)
        {
            var name = Path.GetFileName(path);
            foreach (var ext in new[] { ".ir.json", ".unitypackage", ".zip", ".prefab", ".unity" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - ext.Length);
            return name;
        }

        /// <summary>지금 아바타 오른쪽·왼쪽으로 번갈아 0.8 m씩 (같은 방향을 봄).</summary>
        void AddEntry(string ir, string name)
        {
            int n = Guests.Count;
            float x = 0.8f * (n / 2 + 1) * (n % 2 == 0 ? 1 : -1);
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            Guests.Add(JNode.Obj().Set("id", id).Set("name", name).Set("path", ir)
                .Set("pos", JNode.FloatArray(new[] { x, 0f, 0f })).Set("rot", JNode.FloatArray(new[] { 0f, 0f, 0f, 1f })).Set("scale", 1f).Set("visible", true));
            Refresh();
            Changed();
            _ctx.Status($"{name}을(를) 옆에 세웠어요. 불러오는 동안 잠깐 멈출 수 있어요. 자리는 카드의 슬라이더·\"옮기기\"로, VR에서는 그립으로 잡아 옮겨요.");
        }

        JNode? Find(string id) => Guests.Items.FirstOrDefault(g => g["id"].AsString == id);

        void Remove(string id)
        {
            var arr = JNode.Arr();
            foreach (var g in Guests.Items) if (g["id"].AsString != id) arr.Add(g);
            _settings.Guests = arr;
            Refresh();
            Changed();
        }

        static float Yaw(JNode rot)
        {
            var q = rot.ToFloatArray();
            if (q.Length != 4) return 0;
            float fx = 2 * (q[0] * q[2] + q[3] * q[1]), fz = 1 - 2 * (q[0] * q[0] + q[1] * q[1]);
            return (float)(Math.Atan2(fx, fz) * 180 / Math.PI);
        }

        static JNode YawRot(double deg)
        {
            double h = deg * Math.PI / 360;
            return JNode.FloatArray(new[] { 0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h) });
        }

        public void Refresh()
        {
            _list.Children.Clear(); _state.Clear(); _syncCard.Clear();
            if (Guests.Count == 0) { _list.Children.Add(Ui.Wrap("아직 없어요. \"추가…\"로 가진 아바타를 고르거나 \"이 아바타 하나 더\"로 지금 아바타를 하나 더 세워 보세요.")); return; }
            foreach (var g in Guests.Items) _list.Children.Add(Ui.Card(CardOf(g)));
            OnState(_lastState);
        }

        Control CardOf(JNode g)
        {
            var id = g["id"].AsString;
            var card = new StackPanel();
            var head = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var vis = new CheckBox { Content = "보이기", IsChecked = g["visible"].IsNull || g["visible"].AsBool, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            vis.IsCheckedChanged += (_, __) => { g.Set("visible", vis.IsChecked == true); Changed(); };
            right.Children.Add(vis);
            right.Children.Add(Ui.IconButton(Glyph.Move, () => Select?.Invoke(id), "옮기기: 미리보기에 이동 화살표·회전 고리를 띄워요 (조작 모드가 켜져요). 미리보기에서 이 아바타를 더블클릭해도 골라져요"));
            right.Children.Add(Ui.IconButton(Glyph.Delete, () => Remove(id), "내리기 (가져온 파일은 남아요)"));
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            var title = Ui.Text(g["name"].AsString, 14, FontWeight.SemiBold); title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            card.Children.Add(head);

            var sliders = new List<(Ui.EditSlider S, Func<double> Get)>();
            void Row(string label, double min, double max, Func<double> get, Action<double> set)
            {
                var s = new Ui.EditSlider(min, max, v => { set(v); Changed(false); }, v => { set(v); Changed(); });
                s.SetQuiet(get());
                sliders.Add((s, get));
                card.Children.Add(Ui.PropRow(Ui.Text(label), s.Slider, null));
            }
            float P(int k) => g["pos"].ToFloatArray() is var p && p.Length == 3 ? p[k] : 0;
            void SetP(int k, double v) { var p = g["pos"].ToFloatArray(); if (p.Length != 3) p = new float[3]; p[k] = (float)v; g.Set("pos", JNode.FloatArray(p)); }
            Row("좌우 (m)", -5, 5, () => P(0), v => SetP(0, v));
            Row("앞뒤 (m)", -5, 5, () => P(2), v => SetP(2, v));
            Row("높이 (m)", -1, 3, () => P(1), v => SetP(1, v));
            Row("돌리기 (도)", -180, 180, () => Yaw(g["rot"]), v => g.Set("rot", YawRot(v)));
            Row("크기", 0.2, 3, () => g["scale"].IsNull ? 1 : g["scale"].AsDouble, v => g.Set("scale", (float)v));
            _syncCard[id] = () => { foreach (var (s, get) in sliders) s.SetQuiet(get()); };

            var poseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            var copy = Ui.Button("지금 아바타 자세 복사", () => CopyPose?.Invoke(id), "자세 페이지에서 만든 지금 아바타의 자세를 이 아바타에 그대로 (머슬 값이라 몸이 달라도 같은 자세)", Glyph.Front);
            var reset = Ui.Button("자세 처음으로", () => { g.Remove("pose"); Changed(); }, "불러온 그대로의 자세로", Glyph.Reset);
            copy.Margin = reset.Margin = new Thickness(0);
            poseRow.Children.Add(copy); poseRow.Children.Add(reset);
            card.Children.Add(poseRow);

            var st = Ui.Text("불러오는 중…", 12, FontWeight.Normal, Ui.KText2);
            st.TextWrapping = TextWrapping.Wrap;
            st.Margin = new Thickness(0, 4, 0, 0);
            _state[id] = st;
            card.Children.Add(st);
            return card;
        }

        /// <summary>guestsState: 카드마다 불러온 결과.</summary>
        public void OnState(JNode guests)
        {
            _lastState = guests;
            foreach (var s in guests.Items)
                if (_state.TryGetValue(s["id"].AsString, out var t))
                {
                    if (s["ok"].AsBool)
                    {
                        t.Text = $"키 약 {s["height"].AsDouble:0.00} m · 메시 {s["renderers"].AsInt}개 · PhysBone {s["physBones"].AsInt}개" + (s["humanoid"].AsBool ? "" : " · Humanoid가 아니므로 자세를 변경할 수 없습니다");
                        t.Fg(Ui.KText2);
                    }
                    else { t.Text = "불러오지 못했습니다: " + s["error"].AsString; t.Fg(Ui.KCaution); }
                }
        }

        /// <summary>guestMoved: 핸들·VR 그립으로 옮긴 자리를 목록에 (렌더러는 이미 그 자리라 다시 보내지 않는다).</summary>
        public void OnMoved(JNode m)
        {
            var g = Find(m["id"].AsString);
            if (g == null) return;
            g.Set("pos", m["pos"]).Set("rot", m["rot"]).Set("scale", m["scale"]);
            _settings.Save();
            if (_syncCard.TryGetValue(m["id"].AsString, out var sync)) sync();
            _ctx.Status($"{g["name"].AsString}을(를) 옮겼어요.");
        }

        /// <summary>guestPoseEdited: VR로 본을 잡아 놓았거나 자세를 복사함.</summary>
        public void OnPoseEdited(JNode m)
        {
            var g = Find(m["id"].AsString);
            if (g == null) return;
            g.Set("pose", m["pose"]);
            _settings.Save();
        }
    }
}
