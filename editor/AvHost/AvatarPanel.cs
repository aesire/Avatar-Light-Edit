// "아바타" 작업 창: 크기 · 붙인 아이템 · 기본 옷·헤어 묶음 켜고 끄기(Core.Wardrobe) · 내 토글(AvatarPanel.Toggles.cs) · 메시 표시 켜기/끄기 · 메시에 카메라 맞추기.
// 같은 클래스가 section "optimize"로 "최적화" 작업 창도 그린다: 퍼포먼스 등급 예상치(VRChat 기준표) · 정점 합치기 · 폴리곤 줄이기.
// 등급은 Core.AvatarStats가 IR만으로 계산한다. 범위(Bounds)는 렌더러가 sceneLoaded로 보내 준 값을 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class AvatarPanel : DockPanel
    {
        readonly EditorContext _ctx;
        // 페이지 안 탭 (2026-10-21): 아바타 = 붙인 것 · 기본 옷 · 내 토글 · 메시(전문가), 최적화 = 등급 · 정점 줄이기
        readonly SubTabs _tabs;
        readonly StackPanel _tItems = new StackPanel(), _tWardrobe = new StackPanel(), _tToggles = new StackPanel(), _tMeshes = new StackPanel(), _tPerf = new StackPanel(), _tOpt = new StackPanel();
        StackPanel _page;   // Rebuild가 지금 채우는 탭
        readonly Dictionary<string, Action> _meshRows = new Dictionary<string, Action>();
        readonly Dictionary<string, Button> _outlineButtons = new Dictionary<string, Button>();
        PerfPlatform _platform = PerfPlatform.PC;
        static readonly Dictionary<string, bool> FoldOpen = new Dictionary<string, bool>();   // 묶음·아이템 부품 펼침 (창을 다시 그려도 유지)
        List<WardrobePart>? _parts; IrScene? _partsFor;
        string _outlineRenderer = "";

        readonly string _section;

        /// <param name="section">"avatar" = 정보·아이템·옷·메시 표시, "optimize" = 퍼포먼스 등급·최적화</param>
        public AvatarPanel(EditorContext ctx, string section = "avatar")
        {
            _ctx = ctx; _section = section;
            _page = _tItems;
            _tabs = new SubTabs(section, section == "optimize" ? "최적화" : "아바타");
            if (section == "optimize")
            {
                _tabs.Add("perf", "퍼포먼스 등급", _tPerf);
                _tabs.Add("reduce", "정점 줄이기", _tOpt);
            }
            else
            {
                _tabs.Add("items", "붙인 것", _tItems);
                _tabs.Add("wardrobe", "기본 옷·헤어", _tWardrobe);
                _tabs.Add("toggles", "내 토글", _tToggles);
                _tabs.Add("meshes", "메시 하나씩", _tMeshes, expert: true);
                var files = Ui.Button("가져온 파일…", () => OpenFiles?.Invoke(), "이 아바타를 만든 재료(Unity 프로젝트·패키지)와 붙인 아이템, 이 PC에 저장된 가져오기를 보고 관리해요", Glyph.Package);
                files.Margin = new Thickness(0, 0, 4, 0); files.Padding = new Thickness(10, 3); files.MinHeight = 28;
                _tabs.AddAction(files);
                // 머티리얼·에셋 창 (2026-10-27, 사용자 요청): 머티리얼 페이지 단추와 같은 창
                var assets = Ui.Button("머티리얼·에셋…", () => OpenAssets?.Invoke(), "이 아바타의 머티리얼·텍스처·메시를 폴더로 보고 관리해요", Glyph.Material);
                assets.Margin = new Thickness(0, 0, 4, 0); assets.Padding = new Thickness(10, 3); assets.MinHeight = 28;
                _tabs.AddAction(assets);
            }
            Children.Add(_tabs);
            _ctx.DocumentOpened += () =>
            {
                _outlineRenderer = "";
                _outlineButtons.Clear();
                _ctx.Render?.Send(Msg.Make(Msg.SetOutline).Set("renderer", "").Set("on", false));
                if (_ctx.Doc != null) _ctx.Doc.Changed += OnChanged;
                Rebuild();
            };
            _ctx.RendererInfoChanged += OnRendererInfo; // sceneLoaded 뒤 (범위가 들어온다)
            Rebuild();
        }

        /// <summary>문서와 따로 그리는 탭 (함께 세운 아바타 — MainWindow가 더한다).</summary>
        public void AddTab(string key, string title, Control content) => _tabs.Add(key, title, content);

        TextBlock? _info;          // 아바타: 맨 위 한 줄 요약 (높이·등급)
        string _boundsKey = "";    // 마지막으로 그릴 때의 범위

        /// <summary>
        /// 렌더러 ready·sceneLoaded 뒤: 범위가 바뀌었을 때만, 바뀐 곳만 고친다 — 아바타는 요약 한 줄, 최적화는 등급 탭.
        /// (예전엔 두 페이지가 장면을 불러올 때마다 통째로 다시 그렸다: 큰 시험 장면에서 0.1초)
        /// </summary>
        void OnRendererInfo()
        {
            if (Doc == null) return;
            var key = _ctx.SceneBounds.IsNull ? "" : _ctx.SceneBounds.ToJson();
            if (_outlineRenderer.Length > 0)
                _ctx.Render?.Send(Msg.Make(Msg.SetOutline).Set("renderer", _outlineRenderer).Set("on", true));
            if (key == _boundsKey) return;
            _boundsKey = key;
            if (_section == "optimize") { _tPerf.Children.Clear(); _page = _tPerf; PerfSection(Doc.Original, BoundsSize(), Stats(BoundsSize())); }
            else RefreshInfo();
        }

        string _optKey = "";
        /// <summary>최적화 페이지에 보이는 것: 메시마다 정점·삼각형 수·정점 줄이기 목표, 되돌리기 단추가 보일지 (세어 둔 값만 쓴다 — RecountAll 뒤)</summary>
        string OptKey()
        {
            var me = _ctx.Meshes;
            if (me == null || Doc == null) return "";
            return string.Join(";", me.MeshIds().Select(id => $"{me.Counts(id)}{me.DecimateTarget(id)}"))
                + "|" + Doc.Overrides.Keys.Any(a => a.Kind == AddrKind.MeshEdit);
        }

        AvatarStats Stats(float[]? size)
        {
            var meshes = _ctx.Meshes;
            return AvatarStats.From(Doc!.Original, size, meshes != null ? id => meshes.Counts(id).tris : (Func<string, int>?)null);
        }

        void RefreshInfo()
        {
            if (_info == null || Doc == null) return;
            var scene = Doc.Original;
            var size = BoundsSize();
            var height = size != null ? $"높이 약 {size[1]:0.00} m · " : "";
            // 한 틀로 (화면 언어 글 표가 문장째 찾게, 2026-11-02)
            _info.Text = $"{(scene.Name.Length > 0 ? scene.Name : "(이름 없음)")} · {height}메시 {scene.Renderers.Count}개 · 머티리얼 {scene.Materials.Count}개 · 등급 {AvatarStats.Label(Stats(size).Overall(_platform))} (PC 예상)";
        }

        Document? Doc => _ctx.Doc;

        float[]? BoundsSize()
        {
            var b = _ctx.SceneBounds;
            if (b.IsNull || !b.Has("min")) return null;
            var mn = b["min"].ToFloatArray(); var mx = b["max"].ToFloatArray();
            if (mn.Length < 3 || mx.Length < 3) return null;
            return new[] { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
        }

        void Rebuild()
        {
            foreach (var t in new[] { _tItems, _tWardrobe, _tToggles, _tMeshes, _tPerf, _tOpt }) t.Children.Clear();
            _meshRows.Clear();
            _outlineButtons.Clear();
            _info = null;
            _boundsKey = _ctx.SceneBounds.IsNull ? "" : _ctx.SceneBounds.ToJson();
            if (Doc == null) return;
            var scene = Doc.Original;
            if (_outlineRenderer.Length > 0 && scene.FindRenderer(_outlineRenderer) == null) ClearOutline(_outlineRenderer);
            var size = BoundsSize();
            var stats = Stats(size);

            if (_section == "optimize")
            {
                _optKey = OptKey();
                _page = _tPerf;
                PerfSection(scene, size, stats);

                // ---- 최적화 (정점 합치기) ----
                _page = _tOpt;
                OptimizeSection(scene);
                return;
            }

            // ---- 정보: 한 줄 요약 (등급은 "최적화" 페이지) ----
            _page = _tItems;
            var info = _info = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
            RefreshInfo();
            info.Margin = new Thickness(2, 0, 0, 4); info.TextWrapping = TextWrapping.Wrap;
            _page.Children.Add(info);
            BuildAvatarTabs(scene);
        }

        /// <summary>최적화 페이지의 퍼포먼스 등급 탭 (_page에)</summary>
        void PerfSection(IrScene scene, float[]? size, AvatarStats stats)
        {
            var head = new TitleRow();
            var seg = Segmented(("PC", PerfPlatform.PC), ("모바일 (Quest)", PerfPlatform.Mobile));
            DockPanel.SetDock(seg, Dock.Right);
            head.Children.Add(seg);
            head.Children.Add(Ui.SectionTitle("퍼포먼스 등급 (예상)", "VRChat 공개 기준표로 계산한 예상치예요. VRChat처럼 꺼 둔 메시도 포함해요 (범위만 불러올 때 보이는 메시로 재요). 텍스처 메모리·파티클·컨스트레인트·Contact는 가져온 파일에 정보가 없어 빠져 있어 실제 등급은 더 낮을 수 있어요. 항목에 마우스를 올리면 등급별 기준이 보여요."));
            _page.Children.Add(head);

            var overall = stats.Overall(_platform);
            var sum = new DockPanel { Margin = new Thickness(0, 12) };
            var big = RankChip(overall, 16);
            DockPanel.SetDock(big, Dock.Right);
            sum.Children.Add(big);
            var sumText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sumText.Children.Add(Ui.Text("전체 등급", 15, FontWeight.SemiBold));
            sumText.Children.Add(Ui.Wrap("가장 나쁜 항목의 등급이 전체 등급이 돼요", Ui.KText2, 12));
            sum.Children.Add(sumText);
            var rows = new StackPanel();
            rows.Children.Add(sum);
            foreach (var it in stats.Items(_platform))
            {
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
                // 범위는 값이 길어 등급 칸이 "Exce"로 잘렸다 → 값은 이름 아래로 (2026-11-01)
                bool isBounds = it.Key == "bounds";
                if (!isBounds) right.Children.Add(Ui.Text(it.Display, 14, FontWeight.SemiBold));
                right.Children.Add(RankChip(it.Rank, 12));
                var hint = AvatarStats.Hint(it);
                var limits = it.Limits.Length == 4 ? $"Excellent ≤{it.Limits[0]:N0} · Good ≤{it.Limits[1]:N0} · Medium ≤{it.Limits[2]:N0} · Poor ≤{it.Limits[3]:N0}" : "Excellent 2.5m · Good 4m · Medium·Poor 5×6×5m 이내";
                var row = Ui.DescRow(it.Label, isBounds ? it.Display + (hint != null ? " · " + hint : "") : hint ?? "", right);
                ToolTip.SetTip(row, isBounds ? "가로 × 높이 × 깊이\n" + limits : limits);
                rows.Children.Add(row);
            }
            if (size == null) rows.Children.Add(Ui.DescRow("범위", "미리보기가 켜지면 계산돼요", Ui.Text("—", 14, FontWeight.Normal, Ui.KText2)));
            _page.Children.Add(Ui.Card(rows));
        }

        /// <summary>아바타 페이지의 탭들: 붙인 것 · 기본 옷 · 내 토글 · 메시 하나씩</summary>
        void BuildAvatarTabs(IrScene scene)
        {
            // ---- 의상·헤어·악세서리 ----
            var itemHead = new TitleRow { Margin = new Thickness(0, 8, 0, 0) };
            var addBtn = Ui.Button("붙이기…", () => AddItem?.Invoke(), "BOOTH 등에서 받은 .unitypackage의 의상·헤어·악세서리를 아바타에 붙여요 (Ctrl+Shift+A)", Glyph.Add, accent: true);
            addBtn.Margin = new Thickness(0);
            DockPanel.SetDock(addBtn, Dock.Right);
            itemHead.Children.Add(addBtn);
            itemHead.Children.Add(Ui.SectionTitle("의상·헤어·악세서리", "Modular Avatar 설정이 있으면 그대로 따라 아바타 본에 붙여요. 설정이 없으면 본 이름으로 맞추고, 그래도 모르면 어디에 붙일지 물어요. 스위치로 입혀 보고 벗겨 봐요."));
            _page.Children.Add(itemHead);
            if (scene.Outfits.Count == 0)
                _page.Children.Add(Ui.Card(Ui.Wrap("붙인 것이 없어요. \"붙이기…\"로 패키지를 골라 보세요.", Ui.KText2, 13)));
            else
            {
                var items = new StackPanel();
                foreach (var o in scene.Outfits) items.Children.Add(ItemRow(scene, o));
                _page.Children.Add(Ui.Card(items));
            }
            // ---- 설치된 기믹 (단추를 눌러야 펼침, AvatarPanel.Gimmicks.cs) ----
            GimmickSection(scene);

            // ---- 기본 옷·헤어 ----
            _page = _tWardrobe;
            var parts = Parts();
            if (parts.Count == 0) _page.Children.Add(Ui.Wrap("아바타에 처음부터 들어 있는 옷·헤어로 볼 메시가 없어요."));
            if (parts.Count > 0)
            {
                var wHead = new DockPanel();
                wHead.Children.Add(Ui.SectionTitle("기본 옷·헤어", "아바타에 처음부터 들어 있는 메시를 이름과 따라가는 뼈로 묶었어요. 묶음 스위치로 한 번에 벗기고 입혀요 (오브젝트 켜고 끄기, 실행 취소 한 번). 펼치면 하나씩 켜고 꺼요."));
                _page.Children.Add(wHead);
                var groups = new StackPanel();
                foreach (var kind in Wardrobe.Kinds)
                {
                    var kindParts = parts.Where(p => p.Kind == kind).ToList();
                    if (kindParts.Count > 0) groups.Children.Add(GroupFold(scene, kind, kindParts));
                }
                _page.Children.Add(groups);
            }

            // ---- 내 토글 ----
            _page = _tToggles;
            ToggleSection(scene);

            // ---- 메시 하나씩 (전문가 탭): 묶음·아이템 부품으로 모자랄 때만 ----
            _page = _tMeshes;
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var showAll = Ui.Button("모두 보이기", ShowAll, "꺼 둔 메시를 모두 켜요", Glyph.Front);
            var reset = Ui.Button("되돌리기", ResetAll, "메시 표시를 원본 상태로", Glyph.Reset);
            showAll.Margin = new Thickness(0, 0, 6, 0); reset.Margin = new Thickness(0);
            tools.Children.Add(showAll); tools.Children.Add(reset);
            int hidden = scene.Renderers.Count(r => !Doc!.Get(Addr.NodeActive(r.Node)).AsBool || !Doc.Get(Addr.RendererEnabled(r.Id)).AsBool);
            var meshHead = new TitleRow();
            DockPanel.SetDock(tools, Dock.Right);
            meshHead.Children.Add(tools);
            meshHead.Children.Add(Ui.SectionTitle($"메시 {scene.Renderers.Count}개" + (hidden > 0 ? $" · {hidden}개 꺼짐" : ""), "메시를 하나씩 켜고 꺼요. 원본은 바뀌지 않고 세션에만 기록돼요. 돋보기를 누르면 그 메시로 카메라를 맞춰요."));
            _page.Children.Add(meshHead);
            var list = new StackPanel();
            foreach (var r in scene.Renderers) list.Children.Add(MeshRow(scene, r));
            _page.Children.Add(Ui.Card(list));
        }

        Control Segmented(params (string Label, PerfPlatform Value)[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (label, value) in items)
            {
                var b = Ui.Button(label, () => { _platform = value; Rebuild(); }, accent: value == _platform);
                b.Margin = new Thickness(0); b.Padding = new Thickness(12, 4); b.MinHeight = 28;
                p.Children.Add(b);
            }
            return p;
        }

        static string RankKey(PerfRank r) => r switch
        {
            PerfRank.Excellent => Ui.KSuccess, PerfRank.Good => Ui.KSuccess, PerfRank.Medium => Ui.KCaution, PerfRank.Poor => Ui.KCaution, _ => Ui.KCritical,
        };

        static Border RankChip(PerfRank r, double size)
        {
            var key = RankKey(r);
            var b = new Border
            {
                Child = Ui.Text(AvatarStats.Label(r), size, FontWeight.SemiBold, key),
                Padding = size > 14 ? new Thickness(14, 4) : new Thickness(8, 1), CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center, MinWidth = size > 14 ? 0 : 84,
            };
            ((TextBlock)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
            return b.Stroke(key);
        }

        // ---------------- 기본 옷·헤어 ----------------
        /// <summary>아이템에 들지 않은 메시를 가늠한 결과 (IR이 바뀔 때만 다시 계산 — 버퍼에서 가중치를 읽는다)</summary>
        public List<WardrobePart> Parts()
        {
            if (Doc == null) return new List<WardrobePart>();
            if (_parts == null || _partsFor != Doc.Original)
            {
                _partsFor = Doc.Original;
                _parts = Wardrobe.Parts(Doc.Original, Wardrobe.WeightsFrom(Doc.Original));
            }
            return _parts;
        }

        // EditorOnly 조상(업로드에서 빠지는 묶음 오브젝트)이 꺼져 있으면 자신이 켜져 있어도 안 보인다
        bool PartVisible(int node, string renderer) => Wardrobe.Visible(Doc!, node, renderer);

        /// <summary>그 종류의 기본 메시를 모두 켜거나 끈다 (실행 취소 한 번). 바뀐 것이 없으면 false.</summary>
        public bool SetGroup(string kind, bool on) => Doc != null && Wardrobe.SetGroup(Doc, Parts(), kind, on);

        /// <summary>그 종류의 기본 메시 중 보이는 것이 있는지</summary>
        public bool AnyVisible(string kind) => Doc != null && Parts().Any(p => p.Kind == kind && PartVisible(p.Node, p.Renderer));

        void SetPart(int node, string renderer, bool on) => Wardrobe.Show(Doc!, node, renderer, on);

        Control GroupFold(IrScene scene, string kind, List<WardrobePart> list)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text("기본 " + Wardrobe.KindLabel(kind), 15, FontWeight.SemiBold);
            var count = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            title.VerticalAlignment = VerticalAlignment.Center; count.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title); header.Children.Add(count);
            var sw = new Ui.Switch(on => SetGroup(kind, on));
            ToolTip.SetTip(sw.Control, kind == "body" ? "몸 메시를 모두 켜고 꺼요" : $"기본 {Wardrobe.KindLabel(kind)}를 모두 입히고 벗겨요");
            var body = new StackPanel();
            foreach (var p in list) body.Children.Add(PartRow(scene, p.Node, p.Renderer, p.Name, p.Reason == "이름" ? "" : p.Reason));            var key = "kind:" + kind;
            var fold = new Ui.Fold(header, body, FoldOpen.TryGetValue(key, out var o) && o, sw.Control);
            fold.Expanded += () => FoldOpen[key] = true;
            fold.Collapsed += () => FoldOpen[key] = false;
            void Refresh()
            {
                int on = list.Count(p => PartVisible(p.Node, p.Renderer));
                sw.SetQuiet(on == list.Count);
                count.Text = on == list.Count ? $"{list.Count}개" : $"{list.Count}개 중 {on}개 보임";
                // 묶음 제목은 늘 같은 모양 (다 벗기면 제목이 흐려져 다른 제목과 달라 보였다). 상태는 개수 글씨와 스위치로
                count.Fg(on == 0 ? Ui.KText3 : Ui.KText2);
            }
            _meshRows[key] = Refresh;
            Refresh();
            return fold;
        }

        /// <summary>메시 하나를 켜고 끄는 작은 줄 (묶음·아이템 부품 목록에 쓴다)</summary>
        Control PartRow(IrScene scene, int node, string renderer, string name, string note)
        {
            var label = Ui.Text(name);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            // 태그 EditorOnly: 처음엔 꺼져 있고 VRChat 업로드에서 빠진다
            bool editorOnly = Wardrobe.EditorOnlyChain(scene, node).Length > 0;
            if (editorOnly) note = (note.Length > 0 ? note + "\n" : "") + "EditorOnly 태그라 VRChat에 업로드하면 제외됩니다. 처음엔 꺼져 있습니다.";
            ToolTip.SetTip(label, scene.NodePath(node) + (note.Length > 0 ? "\n" + note : ""));
            var sw = new Ui.Switch(on =>
            {
                using (Doc!.Group((on ? "보이기: " : "숨기기: ") + name)) SetPart(node, renderer, on);
                if (!on) ClearOutline(renderer);
            });
            var frame = OutlineButton(scene, renderer);
            var material = Ui.IconButton(Glyph.Material, () => OpenRendererMaterial(scene, renderer), "이 파츠의 머티리얼 탭 열기");
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (editorOnly)
            {
                var tag = Ui.Text("업로드 안 됨", 11, FontWeight.Normal, Ui.KText2);
                tag.VerticalAlignment = VerticalAlignment.Center;
                right.Children.Add(tag);
            }
            if (OpenMaterial != null) right.Children.Add(material);
            right.Children.Add(frame); right.Children.Add(sw.Control);
            var g = Ui.Columns(Ui.Star(), GridLength.Auto);
            g.MinHeight = 36;
            g.Children.Add(label.At(0)); g.Children.Add(right.At(1));
            void Refresh()
            {
                bool on = PartVisible(node, renderer);
                sw.SetQuiet(on);
                Ui.MarkModified(label, Doc!.IsModified(Addr.NodeActive(node)) || Doc.IsModified(Addr.RendererEnabled(renderer)));
                label.Opacity = on && !ParentInactive(scene, node) ? 1 : 0.6;
            }
            _meshRows["part:" + renderer + ":" + _meshRows.Count] = Refresh;
            Refresh();
            return g;
        }

        void OpenRendererMaterial(IrScene scene, string renderer)
        {
            var r = scene.FindRenderer(renderer);
            if (r == null) return;
            OpenMaterial?.Invoke(r.Id, r.Materials.FirstOrDefault());
        }

        Button OutlineButton(IrScene scene, string renderer)
        {
            Button? button = null;
            button = Ui.IconButton(Glyph.Search, () => ToggleOutline(scene, renderer), "윤곽 보기·끄기 (누르면 박스를 표시하고 카메라를 이 파츠에 맞춰요, 다시 누르면 숨김)");
            _outlineButtons[renderer] = button;
            PaintOutlineButton(button, _outlineRenderer == renderer);
            return button;
        }

        void ToggleOutline(IrScene scene, string renderer)
        {
            if (scene.FindRenderer(renderer) == null) return;
            _outlineRenderer = _outlineRenderer == renderer ? "" : renderer;
            _ctx.Render?.Send(Msg.Make(Msg.SetOutline).Set("renderer", _outlineRenderer).Set("on", _outlineRenderer.Length > 0));
            // 켤 때만 카메라를 그 파츠에 맞춘다 (끌 때는 지금 시점 그대로)
            if (_outlineRenderer.Length > 0) _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("frame", renderer));
            foreach (var kv in _outlineButtons) PaintOutlineButton(kv.Value, kv.Key == _outlineRenderer);
        }

        void ClearOutline(string renderer)
        {
            if (_outlineRenderer != renderer) return;
            _outlineRenderer = "";
            _ctx.Render?.Send(Msg.Make(Msg.SetOutline).Set("renderer", "").Set("on", false));
            foreach (var kv in _outlineButtons) PaintOutlineButton(kv.Value, false);
        }

        static void PaintOutlineButton(Button button, bool on)
        {
            button.Bg(on ? Ui.KSelect : null);
            if (button.Content is TextBlock icon) icon.Fg(on ? Ui.KAccentText : Ui.KText2);
        }

        // ---------------- 메시 ----------------
        /// <summary>
        /// 보이기 = 오브젝트 켜짐(NodeActive) 그리고 렌더러 켜짐(RendererEnabled). VRChat 토글이 주로 오브젝트를 켜고 끄므로
        /// 끌 때는 오브젝트를 끄고, 켤 때는 둘 다 켠다. 상위 오브젝트가 꺼져 있으면 그렇게 알려 준다.
        /// </summary>
        Control MeshRow(IrScene scene, IrRenderer r)
        {
            var nodeName = r.Node >= 0 && r.Node < scene.Nodes.Count ? scene.Nodes[r.Node].Name : r.Id;
            var mesh = scene.FindMesh(r.Mesh);
            int tris0 = mesh != null ? AvatarStats.TriangleCount(mesh) : 0;
            var name = Ui.Text(nodeName);
            var desc = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8) };
            label.Children.Add(name); label.Children.Add(desc);
            ToolTip.SetTip(label, scene.NodePath(r.Node));

            var nodeAddr = Addr.NodeActive(r.Node);
            var renAddr = Addr.RendererEnabled(r.Id);
            var sw = new Ui.Switch(on =>
            {
                using (Doc!.Group((on ? "보이기: " : "숨기기: ") + nodeName))
                {
                    if (on) { Doc.Set(nodeAddr, true); if (!Doc.Get(renAddr).AsBool) Doc.Set(renAddr, true); }
                    else Doc.Set(nodeAddr, false);
                }
                if (!on) ClearOutline(r.Id);
            });
            var frame = OutlineButton(scene, r.Id);
            var material = Ui.IconButton(Glyph.Material, () => OpenRendererMaterial(scene, r.Id), "이 메시의 머티리얼 탭 열기");
            var resetBtn = Ui.IconButton(Glyph.Reset, () => { using (Doc!.Group("되돌리기: " + nodeName)) { Doc.Reset(nodeAddr); Doc.Reset(renAddr); } }, "원본 상태로 되돌리기");
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (OpenMaterial != null) right.Children.Add(material);
            right.Children.Add(resetBtn); right.Children.Add(frame); right.Children.Add(sw.Control);

            var g = Ui.Columns(Ui.Star(), GridLength.Auto);
            g.MinHeight = Ui.RowHeight;
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(Ui.KDivider);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line); g.Children.Add(label.At(0)); g.Children.Add(right.At(1));

            void Refresh()
            {
                bool active = Doc!.Get(nodeAddr).AsBool, enabled = Doc.Get(renAddr).AsBool;
                bool parentOff = ParentInactive(scene, r.Node);
                sw.SetQuiet(active && enabled);
                bool mod = Doc.IsModified(nodeAddr) || Doc.IsModified(renAddr);
                Ui.MarkModified(name, mod);
                resetBtn.IsVisible = mod;
                var kind = r.Type == "MeshRenderer" ? "일반 메시" : "스킨드 메시";
                var state = parentOff ? " · 상위 오브젝트가 꺼져 있어 표시되지 않습니다" : active && !enabled ? " · 메시가 꺼져 있습니다" : "";
                int tris = mesh != null && _ctx.Meshes != null ? _ctx.Meshes.Counts(mesh.Id).tris : tris0;
                var triText = tris != tris0 ? $"삼각형 {tris0:N0} → {tris:N0}" : $"삼각형 {tris:N0}";
                desc.Text = $"{kind} · {triText} · 머티리얼 {r.Materials.Count}{state}";
                desc.Fg(parentOff ? Ui.KCaution : Ui.KText2);
                name.Opacity = active && enabled && !parentOff ? 1 : 0.6;
            }
            _meshRows[r.Id] = Refresh;
            Refresh();
            return g;
        }

        /// <summary>메인 창이 연결한다: 붙이기 · 빼기(프리팹 guid) · 붙일 곳 바꾸기(프리팹 guid) · 가져온 파일 창</summary>
        public Action? AddItem, OpenFiles, OpenAssets;
        public Action<string>? RemoveItem, PlaceItem, CursorPlaceItem, AutoScaleItem, ScaleItem;
        public Action<string, string?>? OpenMaterial;
        /// <summary>미리보기에서 이 아이템을 고르고 핸들을 띄운다 (아이템 루트 노드).</summary>
        public Action<int>? MoveItem;

        /// <summary>
        /// 아이템 한 줄: 종류 · 붙인 방법 · 이 아바타용인지, 입혀 보기 스위치(아이템 루트 오브젝트 켜고 끄기).
        /// 가져올 때 붙인 것(Prefab 있음)만 빼거나 붙일 곳을 바꿀 수 있다 (씬에 원래 있던 의상은 원본의 일부).
        /// </summary>
        Control ItemRow(IrScene scene, IrOutfit o)
        {
            var name = Ui.Text(o.Name);
            var desc = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            desc.TextWrapping = TextWrapping.Wrap; desc.MaxLines = 2;
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8) };
            // 종류 표시를 앞에 두고 이름은 넘치면 말줄임 (긴 이름이 오른쪽 단추 밑으로 들어가지 않게)
            var top = new DockPanel();
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var chip = new Border { Child = Ui.Text(Items.KindLabel(o.Kind), 11, FontWeight.SemiBold, o.NeedsPlacement ? Ui.KCaution : Ui.KAccentText), Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center }.Stroke(Ui.KCardStroke);
            chip.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(chip, Dock.Left);
            top.Children.Add(chip); top.Children.Add(name);
            // 경고(붙이지 않음·다른 아바타용)는 따로 한 줄, 잘리지 않게 (설명 두 줄 끝에서 "…"로 잘려 못 읽던 것)
            var warn = Ui.Text("", 12, FontWeight.Normal, Ui.KCaution);
            warn.TextWrapping = TextWrapping.Wrap; warn.TextTrimming = TextTrimming.None; warn.Margin = new Thickness(0, 2, 0, 0);
            label.Children.Add(top); label.Children.Add(desc); label.Children.Add(warn);

            var fit = Items.FitText(o.Matched, o.Bones, o.OffsetMean, out var bad);
            var parts = new List<string>();
            var warns = new List<string>();
            if (o.NeedsPlacement) warns.Add("어디에도 붙이지 않았어요 · 붙일 곳을 골라 주세요");
            else parts.Add(Items.MethodLabel(o.Method) + (o.Target.Length > 0 ? " → " + Items.BoneLabel(o.Target) : ""));
            if (o.Reason.Length > 0 && o.Method != "manual") parts.Add(o.Reason);
            if (fit.Length > 0)
            {
                // "본 n개 맞춤 · 평균 x cm 어긋남 — …": 맞춘 개수는 설명에, 어긋남은 경고 줄에
                var cut = fit.IndexOf(" · ", StringComparison.Ordinal);
                if (bad && cut > 0) { parts.Add(fit.Substring(0, cut)); warns.Add(fit.Substring(cut + 3)); }
                else parts.Add(fit);
            }
            if (Math.Abs(o.Scale - 1) > 0.005) parts.Add($"크기 ×{o.Scale:0.###}");
            if (o.Prefab.Length == 0) parts.Add("씬에 원래 있던 것");
            desc.Text = string.Join(" · ", parts);
            desc.IsVisible = parts.Count > 0;
            warn.Text = string.Join(" · ", warns);
            warn.IsVisible = warns.Count > 0;
            ToolTip.SetTip(label, string.Join("\n", warns.Concat(parts)) + "\n장착 방식: " + Items.MethodFull(o.Method) + "\n" + scene.NodePath(o.Node));

            var addr = Addr.NodeActive(o.Node);
            // Bone Proxy로 아바타 뼈 아래에 옮겨진 부분(Parts)도 함께: 끄면 끄고, 켜면 원래 상태로 (처음부터 꺼져 있던 손잡이 메시 등은 꺼진 채)
            var mine = scene.Renderers.Where(r => r.Node != o.Node && Under(scene, r.Node, o.Node)).ToList();
            var itemRenderer = scene.Renderers.FirstOrDefault(r => r.Node == o.Node) ?? mine.FirstOrDefault();
            var sw = new Ui.Switch(on =>
            {
                Wardrobe.SetItem(Doc!, o, on);
                if (!on)
                {
                    if (itemRenderer != null) ClearOutline(itemRenderer.Id);
                    foreach (var part in mine) ClearOutline(part.Id);
                }
            });
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (OpenMaterial != null && itemRenderer != null) right.Children.Add(Ui.IconButton(Glyph.Material, () => OpenRendererMaterial(scene, itemRenderer.Id), "이 아이템의 머티리얼 탭 열기"));
            if (itemRenderer != null && (scene.Renderers.Any(r => r.Node == o.Node) || mine.Count == 0))
                right.Children.Add(OutlineButton(scene, itemRenderer.Id));
            // 핸들로 옮기기: 아이템 루트가 통째로 뼈 하나에 붙은 것만 (MA·본 이름으로 뼈를 합친 옷은 루트를 옮겨도 따라오지 않는다)
            if (o.Method == "manual" || o.Method == "none" || o.Method == "ma-proxy")
                right.Children.Add(Ui.IconButton(Glyph.Move, () => MoveItem?.Invoke(o.Node), "옮기기: 미리보기에 이 아이템의 이동 화살표·회전 고리를 띄워요 (조작 모드가 켜져요)"));
            if (o.Prefab.Length > 0)
            {
                // 이미 그 배율로 맞춰 둔 아이템에는 띄우지 않는다 (HeadScale은 배율을 적용하기 전 크기로 잰다)
                if (o.HeadScale > 0.01 && Math.Abs(o.HeadScale - 1) > 0.005 && Math.Abs(o.Scale - o.HeadScale) > 0.005)
                    right.Children.Add(Ui.IconButton(Glyph.Optimize, () => AutoScaleItem?.Invoke(o.Prefab), $"머리 크기 자동 맞춤 (현재 아이템 머리 대비 ×{o.HeadScale:0.###})"));
                right.Children.Add(Ui.IconButton(Glyph.Edit, () => ScaleItem?.Invoke(o.Prefab), "아이템 전체 크기를 직접 입력해요. 아이템 루트·붙인 본·부품에 함께 적용돼요."));
                // 커서로 놓기: 뼈에 그대로 붙이는 아이템(직접 고른 곳·붙이지 못한 것)만. MA·본 이름으로 합친 옷은 뼈대째 붙어 있어 옮기지 않는다
                if (o.Method == "manual" || o.Method == "none")
                    right.Children.Add(Ui.IconButton(Glyph.Place, () => CursorPlaceItem?.Invoke(o.Prefab), "커서로 놓기: 미리보기에서 아바타 표면을 누르면 그 자리 바로 위에 붙어요 (휠로 돌리기)"));
                right.Children.Add(Ui.IconButton(Glyph.Pin, () => PlaceItem?.Invoke(o.Prefab), "붙일 곳 바꾸기 (종류·뼈 직접 고르기)"));
                right.Children.Add(Ui.IconButton(Glyph.Delete, () => RemoveItem?.Invoke(o.Prefab), "아바타에서 빼기"));
            }
            right.Children.Add(sw.Control);

            var g = Ui.Columns(Ui.Star(), GridLength.Auto);
            g.MinHeight = Ui.RowHeight;
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(Ui.KDivider);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line); g.Children.Add(label.At(0)); g.Children.Add(right.At(1));
            void Refresh()
            {
                bool on = Doc!.Get(addr).AsBool;
                sw.SetQuiet(on);
                Ui.MarkModified(name, Doc.IsModified(addr) || Doc.IsModified(Addr.NodeTransform(o.Node)));
                name.Opacity = on ? 1 : 0.6;
            }
            _meshRows["item:" + o.Node] = Refresh;
            Refresh();

            // 아이템 부품: 아이템 아래 메시 (헤어밴드·옆머리처럼 원래 꺼 둔 부품을 켜 볼 수 있게)
            if (mine.Count == 0) return g;
            var key = "item:" + (o.Prefab.Length > 0 ? o.Prefab : o.Name);
            var partsPanel = new StackPanel { Margin = new Thickness(28, 0, 0, 6) };
            foreach (var r in mine) partsPanel.Children.Add(PartRow(scene, r.Node, r.Id, scene.Nodes[r.Node].Name, ""));
            partsPanel.IsVisible = FoldOpen.TryGetValue(key, out var open) && open;
            Button? chevron = null;
            chevron = Ui.IconButton(partsPanel.IsVisible ? Glyph.ChevronDown : Glyph.ChevronRight, () =>
            {
                partsPanel.IsVisible = !partsPanel.IsVisible; FoldOpen[key] = partsPanel.IsVisible;
                if (chevron!.Content is TextBlock t) t.Text = partsPanel.IsVisible ? Glyph.ChevronDown : Glyph.ChevronRight;
            }, $"부품 {mine.Count}개 펼치기 (하나씩 켜고 끄기)");
            right.Children.Insert(0, chevron);
            var box = new StackPanel();
            box.Children.Add(g); box.Children.Add(partsPanel);
            return box;
        }

        static bool Under(IrScene scene, int node, int ancestor)
        {
            for (int x = node; x >= 0 && x < scene.Nodes.Count; x = scene.Nodes[x].Parent) if (x == ancestor) return true;
            return false;
        }

        bool ParentInactive(IrScene scene, int node)
        {
            if (node < 0 || node >= scene.Nodes.Count) return false;
            for (int p = scene.Nodes[node].Parent; p >= 0 && p < scene.Nodes.Count; p = scene.Nodes[p].Parent)
                if (!Doc!.Get(Addr.NodeActive(p)).AsBool) return true;
            return false;
        }

        void OnChanged(IReadOnlyList<Addr> addrs)
        {
            if (addrs.Any(a => a.Kind == AddrKind.MeshEdit))   // 등급·최적화 수치가 바뀐다
            {
                RecountThenRebuild(addrs.Any(a => a.Kind == AddrKind.MeshEdit && MeshEdits.ChangesCounts(Doc!.Get(a))));
                return;
            }
            if (addrs.Any(a => a.Kind == AddrKind.Toggles)) { if (_section == "avatar") Rebuild(); return; }
            if (!addrs.Any(a => a.Kind == AddrKind.NodeActive || a.Kind == AddrKind.RendererEnabled || a.Kind == AddrKind.NodeTransform)) return;
            foreach (var r in _meshRows.Values) r();
        }

        // ---------------- 최적화 ----------------
        int _recount;
        /// <summary>
        /// 정점 줄이기는 메시마다 수백 ms 걸려 화면이 멈추지 않게 백그라운드에서 미리 센 뒤 고친다. 아바타·최적화 페이지가 같은 셈을 함께 기다린다 (MeshEdits.RecountAll).
        /// 최적화는 다시 그리고, 아바타는 숫자가 나오는 곳(요약 한 줄·메시 줄)만 고친다.
        /// </summary>
        /// <param name="counting">새로 세야 하는 편집(정점 합치기·줄이기 등)이 있다 — 상태 줄에 알린다</param>
        void RecountThenRebuild(bool counting = true)
        {
            var me = _ctx.Meshes;
            if (me == null) { Rebuild(); return; }
            int my = ++_recount;
            bool opt = _section == "optimize";
            if (opt && counting) _ctx.Status("정점·삼각형 수를 다시 세는 중…");
            me.RecountAll().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (my != _recount || me != _ctx.Meshes) return;
                // 깜빡임 맞추기·입 선 다듬기처럼 수가 그대로인 편집(블렌드쉐이프를 바꿀 때마다 붙는다)에는 다시 그리지 않는다
                if (opt)
                {
                    if (OptKey() != _optKey) Rebuild();
                    if (counting) _ctx.Status("정점·삼각형 수를 다시 셌어요");
                }
                else { RefreshInfo(); foreach (var r in _meshRows.Values) r(); }
            }));
        }

        static string _weldMm = "0.1";
        static bool _keepSeams = true, _keepShapes = true;
        static readonly HashSet<string> _decimateSel = new HashSet<string>();   // 정점 줄이기 슬라이더가 줄일 메시 (창을 다시 그려도 유지)

        /// <summary>정점 합치기 (PLAN.md 3.9 weld): 모든 메시에 한 번에, 실행 취소 한 단계. 원본 메시는 그대로고 세션에 연산으로 기록된다.</summary>
        void OptimizeSection(IrScene scene)
        {
            var me = _ctx.Meshes;
            if (me == null || scene.Meshes.Count == 0) return;
            _page.Children.Add(Ui.SectionTitle("최적화", "정점·삼각형 수를 줄여요. 원본 파일은 그대로예요."));
            int v0 = 0, t0 = 0, v1 = 0, t1 = 0;
            foreach (var id in me.MeshIds())
            {
                var m = scene.FindMesh(id)!;
                v0 += m.VertexCount; t0 += AvatarStats.TriangleCount(m);
                var (v, t) = me.Counts(id); v1 += v; t1 += t;
            }
            var rows = new StackPanel();
            string Delta(int a, int b) => a == b ? $"{a:N0}" : $"{a:N0} → {b:N0} (−{a - b:N0})";
            rows.Children.Add(Ui.HelpRow("정점", "아바타가 쓰는 메시 전부 (같은 메시는 한 번)", Ui.Text(Delta(v0, v1), 14, FontWeight.SemiBold)));
            rows.Children.Add(Ui.DescRow("삼각형", "", Ui.Text(Delta(t0, t1), 14, FontWeight.SemiBold)));

            var box = Ui.Cell(t => _weldMm = t);
            box.Text = _weldMm; box.Width = 72;
            var unit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            unit.Children.Add(box); unit.Children.Add(Ui.Text("mm", 13, FontWeight.Normal, Ui.KText2));
            rows.Children.Add(Ui.HelpRow("합칠 거리", "이 거리 안의 정점을 하나로 합쳐요 (보통 0.1 mm)", unit));

            CheckBox Opt(string label, bool v, Action<bool> set, string tip)
            {
                var c = new CheckBox { Content = label, IsChecked = v, Margin = new Thickness(0, 0, 16, 0) };
                c.IsCheckedChanged += (_, __) => set(c.IsChecked == true);
                ToolTip.SetTip(c, tip);
                return c;
            }
            var opts = new WrapPanel();
            opts.Children.Add(Opt("UV 이음매 지키기", _keepSeams, x => _keepSeams = x, "UV·노멀·정점 색이 다른 정점은 합치지 않아요. 끄면 텍스처가 이음매에서 어긋날 수 있어요"));
            opts.Children.Add(Opt("블렌드쉐이프·본 가중치 지키기", _keepShapes, x => _keepShapes = x, "블렌드쉐이프나 본 가중치가 다른 정점은 합치지 않아요. 끄면 표정·자세를 바꿀 때 찢어질 수 있어요"));
            rows.Children.Add(Ui.Expert(Ui.BlockRow("지킬 것", null, opts)));   // 쉬운 모드는 기본값(둘 다 지킴)

            // 정점 줄이기: 줄일 메시를 체크하고 슬라이더로 남길 삼각형 비율을 정한다 (끄는 동안은 숫자만 미리 세고, 놓으면 기록·미리보기)
            List<string> allIds = me.MeshIds().ToList();
            _decimateSel.RemoveWhere(id => !allIds.Contains(id));
            List<string> Sel() => allIds.Where(_decimateSel.Contains).ToList();
            // 슬라이더 = 고른 메시의 삼각형 합. 왼쪽 끝 3개 ~ 오른쪽 끝 전체, 로그 눈금 (적은 쪽도 세밀하게)
            int Full() => Sel().Sum(me.BaseTris);
            int Now() { var s = Sel(); return s.Sum(id => me.DecimateTarget(id) ?? me.BaseTris(id)); }
            const double SMax = 1000;
            double ToS(int tris, int full) => full <= 3 ? SMax : Math.Max(0, Math.Min(SMax, Math.Log(Math.Max(3, tris) / 3.0) / Math.Log(full / 3.0) * SMax));
            int ToTris(double s, int full) => full <= 3 ? full : (int)Math.Round(3 * Math.Pow(full / 3.0, s / SMax));
            var count = Ui.Cell(t => { });
            count.Width = 96;
            var ofText = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
            var est = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
            int ticket = 0;
            void ShowIdle()
            {
                var s = Sel();
                if (s.Count == 0) { count.Text = ""; ofText.Text = ""; est.Text = "아래에서 줄일 메시를 먼저 체크하세요"; return; }
                int full = Full(), now = Now();
                count.Text = now.ToString("N0"); ofText.Text = $"/ {full:N0}";
                est.Text = $"고른 메시 {s.Count}개 · 삼각형 3개 ~ {full:N0}개";
            }
            void Estimate(double sv)
            {
                var s = Sel();
                if (s.Count == 0) { est.Text = "아래에서 줄일 메시를 먼저 체크하세요"; return; }
                int full = Full(), tris = ToTris(sv, full);
                count.Text = tris.ToString("N0");
                int my = ++ticket;
                est.Text = "계산 중…";
                var targets = me.Distribute(s, tris); bool keep = _keepShapes;
                System.Threading.Tasks.Task.Delay(250).ContinueWith(_ =>
                {
                    if (my != ticket) return;
                    var (pv, pt) = me.DecimatePreview(targets, keep, () => my != ticket);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (my == ticket) est.Text = $"놓으면 정점 {pv:N0} · 삼각형 {pt:N0}"; });
                });
            }
            void Apply(int tris)
            {
                ticket++;
                var s = Sel();
                if (s.Count == 0) { est.Text = "아래에서 줄일 메시를 먼저 체크하세요"; return; }
                est.Text = "적용 중…";
                me.SetDecimate(me.Distribute(s, Math.Max(3, tris)), _keepShapes);
            }
            var slider = new Ui.EditSlider(0, SMax, Estimate, sv => Apply(ToTris(sv, Full())));
            slider.Slider.SmallChange = 5; slider.Slider.LargeChange = 50;
            void SyncSlider() => slider.SetQuiet(Sel().Count == 0 ? SMax : ToS(Now(), Full()));
            count.LostFocus += (_, __) => Commit();
            count.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
            void Commit()
            {
                if (Sel().Count == 0) { ShowIdle(); return; }
                var txt = (count.Text ?? "").Replace(",", "").Trim();
                if (!int.TryParse(txt, out var n)) { ShowIdle(); return; }
                n = Math.Max(3, Math.Min(Full(), n));
                if (n == Now()) { ShowIdle(); return; }
                Apply(n);
            }
            SyncSlider(); ShowIdle();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            top.Children.Add(count); top.Children.Add(ofText);
            var sbox = new StackPanel { Spacing = 2 };
            sbox.Children.Add(top); sbox.Children.Add(slider.Slider); sbox.Children.Add(est);
            rows.Children.Add(Ui.BlockRow("정점 줄이기 (삼각형 수)", "아래에서 고른 메시를 슬라이더 값까지 줄여요. 손을 떼면 적용돼요 (실행 취소 가능)", sbox));

            // 줄일 메시 목록: 체크 · 이름 · 삼각형 원본 → 지금 · 되돌리기
            var meshList = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var allBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            var checks = new List<CheckBox>();
            var pickAll = Ui.Button("모두 고르기", () => { foreach (var c in checks) c.IsChecked = true; });
            var pickNone = Ui.Button("모두 풀기", () => { foreach (var c in checks) c.IsChecked = false; });
            pickAll.Margin = pickNone.Margin = new Thickness(0); pickAll.MinHeight = pickNone.MinHeight = 28; pickAll.Padding = pickNone.Padding = new Thickness(10, 2);
            allBtns.Children.Add(pickAll); allBtns.Children.Add(pickNone);
            meshList.Children.Add(allBtns);
            foreach (var id in allIds.OrderByDescending(i => AvatarStats.TriangleCount(scene.FindMesh(i)!)))
            {
                var m = scene.FindMesh(id)!;
                var users = scene.Renderers.Where(r => r.Mesh == id).Select(r => r.Node >= 0 && r.Node < scene.Nodes.Count ? scene.Nodes[r.Node].Name : r.Id).Distinct().ToList();
                int t0m = AvatarStats.TriangleCount(m);
                var (_, tNow) = me.Counts(id);
                float ratioNow = me.DecimateRatio(id);
                var cb = new CheckBox { IsChecked = _decimateSel.Contains(id), Content = string.Join(", ", users) + (users.Count == 1 && users[0] != m.Name ? $" ({m.Name})" : "") };
                cb.IsCheckedChanged += (_, __) =>
                {
                    if (cb.IsChecked == true) _decimateSel.Add(id); else _decimateSel.Remove(id);
                    SyncSlider(); ShowIdle();
                };
                checks.Add(cb);
                var info = Ui.Text(tNow != t0m ? $"삼각형 {t0m:N0} → {tNow:N0}"  : $"삼각형 {t0m:N0}", 12, FontWeight.Normal, tNow != t0m ? Ui.KAccentText : Ui.KText2);
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(info);
                if (ratioNow < 0.999f)
                    right.Children.Add(Ui.IconButton(Glyph.Reset, () => me.SetDecimate(new Dictionary<string, int?> { [id] = null }, _keepShapes), "이 메시의 정점 줄이기를 없애요"));
                // 바로 가기 (2026-10-27): 무거운 메시가 어느 파츠인지 보고, 그 머티리얼로 바로 간다
                if (scene.Renderers.FirstOrDefault(r => r.Mesh == id) is { } user)
                {
                    right.Children.Add(OutlineButton(scene, user.Id));
                    if (OpenMaterial != null) right.Children.Add(Ui.IconButton(Glyph.Material, () => OpenRendererMaterial(scene, user.Id), "이 파츠의 머티리얼 탭 열기"));
                }
                var line = Ui.Columns(Ui.Star(), GridLength.Auto);
                line.MinHeight = 32;
                line.Children.Add(cb.At(0)); line.Children.Add(right.At(1));
                meshList.Children.Add(line);
            }
            var foldHead = Ui.Text($"줄일 메시 고르기 ({allIds.Count}개)", 14, FontWeight.Normal);
            rows.Children.Add(new Ui.Fold(foldHead, meshList, FoldOpen.TryGetValue("decimate", out var fo) ? fo : true, null));

            var weld = Ui.Button("정점 합치기", () =>
            {
                if (!float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm) || mm < 0 || mm > 100)
                { _ctx.Status("합칠 거리는 0~100 mm 사이 숫자로 적어 주세요"); return; }
                _weldMm = box.Text ?? "0.1";
                var (dv, dt) = me.WeldAll(me.MeshIds(), mm / 1000f, _keepSeams, _keepShapes);
                _ctx.Status(dv == 0 && dt == 0 ? "합칠 정점이 없어요" : $"정점 {dv:N0}개, 삼각형 {dt:N0}개를 줄였어요 (실행 취소로 되돌려요)");
            }, "모든 메시에 정점 합치기를 적용해요 (실행 취소 한 번으로 되돌림)", Glyph.Add, accent: true);
            weld.Margin = new Thickness(0);
            var resetEdits = Ui.Button("메시 편집 되돌리기", () => Doc!.ResetWhere(a => a.Kind == AddrKind.MeshEdit, "메시 편집 되돌리기"), "정점 합치기와 정점 편집을 모두 지우고 원본 메시로", Glyph.Reset);
            resetEdits.Margin = new Thickness(0);
            resetEdits.IsVisible = Doc!.Overrides.Keys.Any(a => a.Kind == AddrKind.MeshEdit);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 10, 0, 4) };
            btns.Children.Add(weld); btns.Children.Add(resetEdits);
            rows.Children.Add(btns);
            _page.Children.Add(Ui.Card(rows));
        }

        void ShowAll()
        {
            if (Doc == null) return;
            using (Doc.Group("메시 모두 보이기"))
                foreach (var r in Doc.Original.Renderers)
                {
                    // 상위 오브젝트까지 켜야 실제로 보인다
                    for (int n = r.Node; n >= 0 && n < Doc.Original.Nodes.Count; n = Doc.Original.Nodes[n].Parent)
                        if (!Doc.Get(Addr.NodeActive(n)).AsBool) Doc.Set(Addr.NodeActive(n), true);
                    if (!Doc.Get(Addr.RendererEnabled(r.Id)).AsBool) Doc.Set(Addr.RendererEnabled(r.Id), true);
                }
        }

        void ResetAll()
        {
            if (Doc == null) return;
            Doc.ResetWhere(a => a.Kind == AddrKind.NodeActive || a.Kind == AddrKind.RendererEnabled, "메시 표시 되돌리기");
        }
    }
}
