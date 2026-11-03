// 블렌드쉐이프 페이지 (2026-10-21, 사용자 요청):
//   여러 개 고르기   줄 앞 칸 · 이름 Ctrl+클릭(더하기/빼기) · Shift+클릭(마지막 고른 줄부터 여기까지) · 묶음 머리 "모두 고르기"
//   함께 바꾸기     고른 줄의 슬라이더·값을 바꾸면 고른 것 모두 같은 값 (실행 취소 한 번). 탭 위 막대에서 값·0·100·되돌리기·즐겨찾기·풀기
//   즐겨찾기        줄 끝 핀. 맨 위 "즐겨찾기" 묶음에 모은다. 메시(노드) 이름마다 avhost.json에 (같은 아바타를 다시 가져와도 이어진다)
//   L·R 나누기      "기능"·"기능L"·"기능R" 셋이 있는 것 중 "기능"만 바꾼 것 → 그 값을 L·R로 옮기고 "기능"은 원본으로. 합치기는 그 반대 (L·R 값이 같을 때만)
//   묶음 띄우기     묶음 머리 ⧉ → 그 묶음의 줄을 떠 있는 창으로 (FloatHost). 목록에서는 머리만 남는다
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        const string FavKey = "즐겨찾기";
        static bool _closedFav;
        readonly HashSet<string> _sel = new HashSet<string>();
        string? _anchor;
        List<BlendShapeSides.Side> _sides = new List<BlendShapeSides.Side>();
        readonly Button _splitBtn, _mergeBtn;
        readonly Dictionary<string, FloatPanel> _floatGroups = new Dictionary<string, FloatPanel>();

        // ---------------- 함께 바꾸기 ----------------
        readonly Border _selBar = new Border { IsVisible = false, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
        readonly TextBlock _selCount = Ui.Text("", 13, FontWeight.SemiBold, Ui.KAccentText);
        Ui.EditSlider? _selSlider;
        TextBox? _selBox;

        Control SelectionBar()
        {
            _selSlider = new Ui.EditSlider(0, 100, v => { foreach (var n in _sel) Doc?.Preview(Addr.BlendShape(_renderer!, n), v); }, v => SetMany(_sel, Math.Round(v, 2)));
            _selBox = Ui.NumberCell(v => SetMany(_sel, v), RefreshSelBar);
            var head = new DockPanel();
            var close = Ui.IconButton(Glyph.Close, ClearSelection, "고르기 풀기 (Esc)");
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(_selCount);
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            Button Small(string label, Action a, string tip, string? glyph = null)
            {
                var b = Ui.Button(label, a, tip, glyph); b.Padding = new Thickness(10, 3); b.MinHeight = 28; b.Margin = new Thickness(0, 0, 6, 4);
                return b;
            }
            buttons.Children.Add(Small("0", () => SetMany(_sel, 0), "고른 것을 모두 0으로"));
            buttons.Children.Add(Small("100", () => SetMany(_sel, 100), "고른 것을 모두 100으로"));
            buttons.Children.Add(Small("되돌리기", () => ResetMany(_sel), "고른 것을 모두 원본 값으로", Glyph.Reset));
            buttons.Children.Add(Small("즐겨찾기", () => { var f = Favorites(); bool all = _sel.All(f.Contains); foreach (var n in _sel) if (all) f.Remove(n); else f.Add(n); SaveFavorites(f); }, "고른 것을 즐겨찾기에 넣기 (모두 들어 있으면 빼기)", Glyph.Pin));
            buttons.Children.Add(Small("L·R로 나누기", () => SplitLR(onlySelected: true), "고른 것 중 \"기능\"만 바꾼 것을 \"기능L\"·\"기능R\"로 나눠요"));
            var p = new StackPanel();
            p.Children.Add(head);
            p.Children.Add(Ui.NumberWithSlider(_selBox, _selSlider.Slider));
            p.Children.Add(buttons);
            _selBar.Child = p;
            ToolTip.SetTip(_selCount, "고른 줄의 슬라이더를 움직여도 고른 것 모두 같은 값으로 바뀌어요");
            return _selBar.Bg(Ui.KCard).Stroke(Ui.KAccent);
        }

        void RefreshSelBar()
        {
            _selBar.IsVisible = _sel.Count > 0 && Doc != null && _renderer != null;
            if (!_selBar.IsVisible) return;
            _selCount.Text = $"블렌드쉐이프 {_sel.Count}개 고름 · 함께 바꿔요";
            var vals = _sel.Select(n => Doc!.Get(Addr.BlendShape(_renderer!, n)).AsDouble).ToList();
            bool same = vals.All(v => Math.Abs(v - vals[0]) < 1e-6);
            _selSlider?.SetQuiet(same ? vals[0] : vals.Average());
            if (_selBox != null && !_selBox.IsFocused) _selBox.Text = same ? Ui.F(vals[0]) : "";
            if (_selBox != null) _selBox.Watermark = same ? "" : "여러 값";
        }

        bool Many(string name) => _sel.Count > 1 && _sel.Contains(name);
        IEnumerable<string> Targets(string name) => Many(name) ? _sel.ToList() : new List<string> { name };

        void PreviewShape(string name, double v) { foreach (var n in Targets(name)) Doc?.Preview(Addr.BlendShape(_renderer!, n), v); }
        void SetShape(string name, double v) => SetMany(Targets(name), v);
        void ResetShape(string name) => ResetMany(Targets(name));

        void SetMany(IEnumerable<string> names, double v)
        {
            if (Doc == null || _renderer == null) return;
            var list = names.ToList();
            if (list.Count == 0) return;
            FocusGroupOf(list[0]);
            v = Math.Clamp(v, 0, 100);
            if (list.Count == 1) { Doc.Set(Addr.BlendShape(_renderer, list[0]), v, "BlendShape " + list[0]); return; }
            using (Doc.Group($"블렌드쉐이프 {list.Count}개 → {Ui.F(v)}"))
                foreach (var n in list) Doc.Set(Addr.BlendShape(_renderer, n), v);
        }

        void ResetMany(IEnumerable<string> names)
        {
            if (Doc == null || _renderer == null) return;
            var list = names.Where(n => Doc.IsModified(Addr.BlendShape(_renderer, n))).ToList();
            if (list.Count == 0) return;
            using (Doc.Group(list.Count == 1 ? "되돌리기: " + list[0] : $"블렌드쉐이프 {list.Count}개 되돌리기"))
                foreach (var n in list) Doc.Reset(Addr.BlendShape(_renderer, n));
        }

        // ---------------- 고르기 ----------------
        /// <summary>화면 차례 (묶음 차례대로, 즐겨찾기 묶음은 빼고) — Shift 이어 고르기</summary>
        List<string> Order() => _groups.Where(g => !g.Favorites).SelectMany(g => g.Names).ToList();

        void PickClicked(string name, KeyModifiers mods)
        {
            if ((mods & KeyModifiers.Shift) != 0 && _anchor != null)
            {
                var order = Order();
                int a = order.IndexOf(_anchor), b = order.IndexOf(name);
                if (a >= 0 && b >= 0) { SelectMany(order.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1), add: true); return; }
            }
            if (!_sel.Remove(name)) _sel.Add(name);
            _anchor = name;
            OfferRow(name);
            SelectionChanged(new[] { name });
        }

        void SelectMany(IEnumerable<string> names, bool add)
        {
            var list = names.ToList();
            foreach (var n in list) { if (add) _sel.Add(n); else _sel.Remove(n); }
            if (add && list.Count > 0) _anchor = list[^1];
            SelectionChanged(list);
        }

        void ClearSelection()
        {
            var was = _sel.ToList();
            _sel.Clear(); _anchor = null;
            SelectionChanged(was);
        }

        void SelectionChanged(IEnumerable<string> changed)
        {
            foreach (var n in changed) RefreshRow(n);
            RefreshSelBar();
        }

        // ---------------- 즐겨찾기 ----------------
        string MeshKey()
        {
            var r = Doc?.Original.FindRenderer(_renderer ?? "");
            return r != null && r.Node >= 0 && r.Node < Doc!.Original.Nodes.Count ? Doc.Original.Nodes[r.Node].Name : _renderer ?? "";
        }

        HashSet<string>? _favCache; string? _favFor;

        HashSet<string> Favorites()
        {
            var k = MeshKey();
            if (_favCache != null && _favFor == k) return _favCache;
            _favFor = k;
            return _favCache = new HashSet<string>(_settings.ShapeFavorites[k].Items.Select(x => x.AsString));
        }

        void SaveFavorites(HashSet<string> set)
        {
            var arr = JNode.Arr();
            foreach (var n in set.OrderBy(x => x)) arr.Add(n);
            var k = MeshKey();
            if (set.Count == 0) _settings.ShapeFavorites.Remove(k); else _settings.ShapeFavorites.Set(k, arr);
            _settings.Save();
            _favCache = null;
            Rebuild();
        }

        void ToggleFavorite(string name)
        {
            var f = Favorites();
            if (!f.Remove(name)) f.Add(name);
            SaveFavorites(f);
        }

        void FavoriteModified()
        {
            if (Doc == null || _renderer == null) return;
            var f = Favorites();
            int before = f.Count;
            foreach (var n in ShapeNames()) if (Doc.IsModified(Addr.BlendShape(_renderer, n))) f.Add(n);
            if (f.Count == before) { _ctx.Status("바꾼 블렌드쉐이프가 없거나 이미 모두 즐겨찾기에 있어요."); return; }
            SaveFavorites(f);
            _ctx.Status($"바꾼 블렌드쉐이프 {f.Count - before}개를 즐겨찾기에 넣었어요.");
        }

        // ---------------- L·R 나누기 ----------------
        bool Mod(string n) => Doc!.IsModified(Addr.BlendShape(_renderer!, n));
        double Val(string n) => Doc!.Get(Addr.BlendShape(_renderer!, n)).AsDouble;

        void RefreshSideButtons()
        {
            if (Doc == null || _renderer == null) { _splitBtn.IsVisible = _mergeBtn.IsVisible = false; return; }
            _splitBtn.IsVisible = _mergeBtn.IsVisible = _sides.Count > 0;
            if (_sides.Count == 0) return;
            int split = BlendShapeSides.SplitCandidates(_sides, Mod).Count;
            int merge = BlendShapeSides.MergeCandidates(_sides, Mod, Val, out var differ).Count;
            _splitBtn.IsEnabled = split > 0; _mergeBtn.IsEnabled = merge > 0;
            ((TextBlock)((StackPanel)_splitBtn.Content!).Children[^1]).Text = split > 0 ? $"L·R로 나누기 {split}" : "L·R로 나누기";
            ((TextBlock)((StackPanel)_mergeBtn.Content!).Children[^1]).Text = merge > 0 ? $"L·R 합치기 {merge}" : "L·R 합치기";
            ToolTip.SetTip(_splitBtn, $"좌우 짝 {_sides.Count}개 (\"기능\"·\"기능L\"·\"기능R\") 중 \"기능\"만 바꾼 {split}개: 그 값을 L·R로 옮기고 \"기능\"은 원본으로 (실행 취소 한 번)");
            ToolTip.SetTip(_mergeBtn, $"L·R로 나눈 것을 되돌립니다: L·R 값이 같은 {merge}개를 \"기능\" 하나로" + (differ.Count > 0 ? $" (L·R 값이 다른 {differ.Count}개는 그대로)" : ""));
        }

        void SplitLR(bool onlySelected = false)
        {
            if (Doc == null || _renderer == null) return;
            var list = BlendShapeSides.SplitCandidates(_sides, Mod, onlySelected ? _sel : null);
            if (list.Count == 0) { _ctx.Status(onlySelected ? "고른 것 중에 나눌 것이 없어요 (\"기능\"만 바꾼 좌우 짝이 없어요)." : "나눌 것이 없어요."); return; }
            using (Doc.Group($"L·R로 나누기 {list.Count}개"))
                foreach (var s in list)
                {
                    var v = Val(s.Base);
                    Doc.Set(Addr.BlendShape(_renderer, s.Left), v);
                    Doc.Set(Addr.BlendShape(_renderer, s.Right), v);
                    Doc.Reset(Addr.BlendShape(_renderer, s.Base));
                }
            _ctx.Status($"{list.Count}개를 L·R로 나눴습니다: " + string.Join(", ", list.Take(4).Select(s => s.Base)) + (list.Count > 4 ? " …" : "") + " (실행 취소로 되돌립니다)");
        }

        void MergeLR()
        {
            if (Doc == null || _renderer == null) return;
            var list = BlendShapeSides.MergeCandidates(_sides, Mod, Val, out var differ, _sel.Count > 0 ? _sel : null);
            if (list.Count == 0) { _ctx.Status(differ.Count > 0 ? $"L·R 값이 서로 달라 합치지 못했어요 ({differ.Count}개)." : "합칠 것이 없어요."); return; }
            using (Doc.Group($"L·R 합치기 {list.Count}개"))
                foreach (var s in list)
                {
                    var v = Val(s.Left);
                    Doc.Set(Addr.BlendShape(_renderer, s.Base), v);
                    Doc.Reset(Addr.BlendShape(_renderer, s.Left));
                    Doc.Reset(Addr.BlendShape(_renderer, s.Right));
                }
            _ctx.Status($"{list.Count}개를 합쳤습니다" + (differ.Count > 0 ? $" · L·R 값이 다른 {differ.Count}개는 그대로" : "") + ".");
        }

        /// <summary>
        /// 화면 확인 모드 시험 (--ui-shots, shapes_test.txt): 셋 고르기 → 함께 바꾸기(실행 취소 한 번에 셋 다) · 즐겨찾기 묶음 · L·R 나누기·합치기 · 묶음 띄우기.
        /// 바꾼 값은 실행 취소로, 즐겨찾기는 원래대로 돌린다.
        /// </summary>
        internal List<string> SelfTest()
        {
            var log = new List<string>();
            void Line(bool ok, string s) => log.Add((ok ? "ok   " : "FAIL ") + s);
            if (Doc == null || _renderer == null) { log.Add("FAIL 문서·메시 없음"); return log; }
            var names = ShapeNames().Where(n => !n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            var addr = names.Select(n => Addr.BlendShape(_renderer, n)).ToList();
            ClearSelection();
            SelectMany(names, add: true);
            Line(_sel.Count == 3 && _selBar.IsVisible, $"셋 고르기 → 함께 바꾸기 막대 ({string.Join(", ", names)})");
            SetShape(names[0], 40);   // 고른 줄 하나를 바꾸면
            Line(addr.All(a => Math.Abs(Doc.Get(a).AsDouble - 40) < 1e-6), "고른 줄 하나를 40으로 → 셋 다 40");
            Doc.Undo();
            Line(addr.All(a => !Doc.IsModified(a)), "실행 취소 한 번에 셋 다 원래대로");
            ClearSelection();
            Line(_sel.Count == 0 && !_selBar.IsVisible, "고르기 풀기");

            var favBefore = new HashSet<string>(Favorites());
            ToggleFavorite(names[1]);
            Line(_groups.FirstOrDefault()?.Favorites == true && _groups[0].Names.Contains(names[1]), "즐겨찾기 → 맨 위 즐겨찾기 묶음");
            SaveFavorites(favBefore);

            log.Add($"좌우 짝 {_sides.Count}개" + (_sides.Count > 0 ? ": " + string.Join(", ", _sides.Take(5).Select(s => $"{s.Base}/{s.Left}/{s.Right}")) : ""));
            if (_sides.Count > 0)
            {
                var s = _sides[0];
                Doc.Set(Addr.BlendShape(_renderer, s.Base), 60, "시험");
                SplitLR();
                Line(Math.Abs(Val(s.Left) - 60) < 1e-6 && Math.Abs(Val(s.Right) - 60) < 1e-6 && !Mod(s.Base), $"L·R로 나누기: {s.Base} 60 → {s.Left}·{s.Right} 60, {s.Base} 원본");
                MergeLR();
                Line(Math.Abs(Val(s.Base) - 60) < 1e-6 && !Mod(s.Left) && !Mod(s.Right), "L·R 합치기로 되돌림");
                Doc.Undo(); Doc.Undo(); Doc.Undo();
                Line(!Mod(s.Base) && !Mod(s.Left) && !Mod(s.Right), "실행 취소 세 번으로 처음대로");
            }
            var g = _groups.FirstOrDefault(x => !x.Favorites);
            if (g != null && SubTabs.Host != null)
            {
                FloatGroup(g.Key);
                Line(IsFloated(g.Key) && SubTabs.Host.Find("shapes/group:" + FK(_renderer!, g.Key)) != null, $"묶음 \"{g.Key}\" 띄우기");
            }
            return log;
        }

        /// <summary>시험·찍기용: 떠 있는 묶음 창 (없으면 null)</summary>
        internal FloatPanel? FloatedGroup() => _floatGroups.Values.FirstOrDefault();
        /// <summary>시험 (--test-pinfloat): 묶음을 띄워 핀을 꽂고 다른 메시를 고른 뒤 창이 원래 메시에 남는지, 돌아오면 보통 줄로 돌아오는지</summary>
        internal async System.Threading.Tasks.Task<string> TestPinnedFloat(Action<string> select)
        {
            var g = _groups.FirstOrDefault(x => !x.Favorites);
            if (g == null || _renderer == null || Doc == null) return "FAIL 묶음 없음";
            var rid0 = _renderer;
            FloatGroup(g.Key);
            var f = _floatGroups[FK(rid0, g.Key)];
            f.SetPinned(true);
            var other = Doc.Original.Renderers.OrderByDescending(r => Doc.Original.FindMesh(r.Mesh)?.BlendShapes.Count ?? 0).FirstOrDefault(r => r.Id != rid0);   // 블렌드쉐이프가 있는 다른 메시가 먼저 (샘플은 Head만 있어 없는 메시로)
            if (other == null) return "FAIL 다른 메시 없음";
            select(other.Id);
            await System.Threading.Tasks.Task.Delay(800);
            var info = _floatInfo.TryGetValue(f, out var i) ? i : null;
            bool stay = info != null && info.Rid == rid0 && info.Bound;
            select(rid0);
            await System.Threading.Tasks.Task.Delay(800);
            bool back = info != null && info.Rid == rid0 && !info.Bound;
            select(other.Id);
            await System.Threading.Tasks.Task.Delay(500);
            return $"{(stay ? "ok" : "FAIL")} 핀 꽂은 \"{g.Key}\" 창이 {MeshName(rid0)}에 남음 ({MeshName(other.Id)} 고른 뒤, 제목 {f.Title}) · {(back ? "ok" : "FAIL")} 돌아오면 보통 줄";
        }

        internal void DockAllGroups() { foreach (var f in _floatInfo.Keys.ToList()) DockFloat(f); }

        // ---------------- 묶음 띄우기 ----------------
        // 떠 있는 묶음 창은 메시마다 따로 (2026-10-22 고침): 예전에는 묶음 이름(Face 등)으로만 기억해서,
        // 다른 메시를 고르면 그 메시의 같은 이름 묶음으로 내용이 바뀌었다 — 핀으로 고정해 둔 창도.
        //   · 핀을 꽂은 창: 원래 메시를 그대로 보인다 (BoundView — 그 메시 값을 바로 바꾸는 간단한 줄). 그 메시로 돌아오면 다시 보통 줄.
        //   · 핀이 없는 창: 예전처럼 새 메시의 같은 묶음을 따라간다 (없으면 붙인다).
        static string FK(string rid, string key) => rid + "\u001f" + key;
        sealed class FloatGroupInfo { public string Rid = "", Key = ""; public bool Bound; }
        readonly Dictionary<FloatPanel, FloatGroupInfo> _floatInfo = new Dictionary<FloatPanel, FloatGroupInfo>();

        bool IsFloated(string key) => _renderer != null && _floatGroups.ContainsKey(FK(_renderer, key));
        void FrontGroup(string key) { if (_renderer != null && _floatGroups.TryGetValue(FK(_renderer, key), out var f)) f.Front(); }

        /// <summary>메시 이름 (노드 이름) — 떠 있는 창 제목에</summary>
        string MeshName(string rid)
        {
            var r = Doc?.Original.FindRenderer(rid);
            return r != null && r.Node >= 0 && r.Node < Doc!.Original.Nodes.Count ? Doc.Original.Nodes[r.Node].Name : rid;
        }

        string FloatTitle(string rid, string key) => $"블렌드쉐이프 · {MeshName(rid)} · {(key == FavKey ? key : Disp(key))}";

        /// <summary>이름 번역을 켜고 끌 때: 다른 메시에 고정된 창도 다시 만든다 (지금 메시의 창은 Rebuild → RefreshFloats가)</summary>
        void RedrawFloats() { foreach (var info in _floatInfo.Values) info.Bound = false; }

        void FloatGroup(string key)
        {
            var host = SubTabs.Host;
            var g = _groups.FirstOrDefault(x => x.Key == key);
            if (host == null || g == null || _renderer == null) return;
            var fk = FK(_renderer, key);
            if (_floatGroups.TryGetValue(fk, out var old)) { old.Front(); return; }
            var f = host.Show("shapes/group:" + fk, "shapes", FloatTitle(_renderer, key), GroupView(g), canDock: true, new Size(420, 560));
            _floatGroups[fk] = f;
            _floatInfo[f] = new FloatGroupInfo { Rid = _renderer, Key = key };
            f.DockRequested += () => DockFloat(f);
            Refill();
        }

        void DockGroup(string fk) { if (_floatGroups.TryGetValue(fk, out var f)) DockFloat(f); }

        void DockFloat(FloatPanel f)
        {
            if (!_floatInfo.Remove(f, out var info)) return;
            _floatGroups.Remove(FK(info.Rid, info.Key));
            f.Release();
            SubTabs.Host?.Close(f);
            Refill();
        }

        /// <summary>다시 그릴 때: 떠 있는 묶음 창의 줄도 새로. 지금 메시의 창은 보통 줄, 다른 메시의 창은 핀이면 그 메시 그대로, 핀이 없으면 새 메시를 따라간다</summary>
        void RefreshFloats()
        {
            foreach (var (f, info) in _floatInfo.ToList())
            {
                if (Doc?.Original.FindRenderer(info.Rid) == null) { DockFloat(f); continue; }   // 다른 아바타를 열었다
                if (info.Rid == _renderer)
                {
                    var g = _groups.FirstOrDefault(x => x.Key == info.Key);
                    if (g == null) { DockFloat(f); continue; }
                    f.SetContent(GroupView(g)); info.Bound = false;
                    f.SetTitle(FloatTitle(info.Rid, info.Key));
                    continue;
                }
                if (f.Pinned)
                {
                    if (!info.Bound) { f.SetContent(BoundView(info.Rid, info.Key)); info.Bound = true; f.SetTitle(FloatTitle(info.Rid, info.Key)); }
                    continue;
                }
                // 핀 없음: 새 메시의 같은 묶음으로 옮긴다 (이미 그 메시에 같은 묶음 창이 있거나 묶음이 없으면 붙인다)
                var ng = _renderer == null ? null : _groups.FirstOrDefault(x => x.Key == info.Key);
                if (ng == null || _floatGroups.ContainsKey(FK(_renderer!, info.Key))) { DockFloat(f); continue; }
                _floatGroups.Remove(FK(info.Rid, info.Key));
                info.Rid = _renderer!; info.Bound = false;
                _floatGroups[FK(info.Rid, info.Key)] = f;
                f.SetContent(GroupView(ng));
                f.SetTitle(FloatTitle(info.Rid, info.Key));
            }
        }

        /// <summary>떠 있는 창의 내용: 모두 고르기 + 줄 카드 (가상화하지 않는다 — 묶음 하나는 많아야 백몇십 줄)</summary>
        Control GroupView(GroupItem g)
        {
            var p = new StackPanel { Margin = new Thickness(16, 10, 16, 16) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var all = Ui.Button("모두 고르기", () => SelectMany(g.Names, add: !g.Names.All(_sel.Contains)), "이 묶음을 모두 고르기 (다시 누르면 풀기)", "");
            all.Padding = new Thickness(10, 3); all.MinHeight = 28; all.Margin = new Thickness(0);
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
            head.Children.Add(Ui.Text($"{g.Names.Count}개", 12, FontWeight.Normal, Ui.KText3));
            p.Children.Add(head);
            var rows = new StackPanel();
            foreach (var n in g.Names) rows.Children.Add(MakeRow(n, out _));
            p.Children.Add(Ui.Card(rows));
            return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        /// <summary>
        /// 핀으로 고정한 다른 메시의 묶음: 그 메시 값을 바로 바꾸는 줄 (이름 · 값 · 슬라이더 · 되돌리기). 고르기·즐겨찾기 같은 목록 기능은
        /// 지금 메시에만 있으므로 여기서는 뺀다. 값은 문서가 바뀔 때 따라간다. 세밀 조정 막대로도 보낼 수 있다(이름 누르기).
        /// </summary>
        Control BoundView(string rid, string key)
        {
            var doc = Doc!;
            var r = doc.Original.FindRenderer(rid);
            var m = r == null ? null : doc.Original.FindMesh(r.Mesh);
            var all = m?.BlendShapes.Select(b => b.Name).ToList() ?? new List<string>();
            List<string> names;
            if (key == FavKey)
            {
                var fav = new HashSet<string>(_settings.ShapeFavorites[MeshName(rid)].Items.Select(x => x.AsString));
                names = all.Where(fav.Contains).ToList();
            }
            else names = BlendShapeGroups.Build(all).Where(e => !e.IsSeparator && e.Group == key).Select(e => e.Name).ToList();

            var p = new StackPanel { Margin = new Thickness(16, 10, 16, 16) };
            p.Children.Add(new TextBlock { Text = $"{names.Count}개 · {MeshName(rid)} 메시에 고정됨 (핀을 뽑으면 지금 고른 메시를 따라가요)", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }.Fg(Ui.KText3));
            var rows = new StackPanel();
            var refresh = new Dictionary<string, Action>();
            foreach (var n in names)
            {
                var name = n;
                var a = Addr.BlendShape(rid, name);
                var label = Ui.Text(Disp(name)); ToolTip.SetTip(label, name);
                var slider = new Ui.EditSlider(0, 100, v => doc.Preview(a, v), v => doc.Set(a, Math.Clamp(Math.Round(v, 2), 0, 100), "BlendShape " + name)) { Kind = "shape", Key = rid + "\u001f" + name };
                TextBox box = null!;
                Action show = () => { };
                box = Ui.NumberCell(v => doc.Set(a, Math.Clamp(v, 0, 100), "BlendShape " + name), () => show());
                var reset = Ui.IconButton(Glyph.Reset, () => { if (doc.IsModified(a)) doc.Set(a, doc.GetOriginal(a).AsDouble, "BlendShape " + name + " 되돌리기"); }, "원본 값으로 되돌리기");
                show = () =>
                {
                    var v = doc.Get(a).AsDouble; bool mod = doc.IsModified(a);
                    slider.SetQuiet(v);
                    if (!box.IsFocused) box.Text = Ui.F(v);
                    Ui.MarkModified(label, mod);
                    reset.IsVisible = mod;
                };
                label.Background = Brushes.Transparent;
                label.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Ui.OfferFine(slider.Fine()); };
                rows.Children.Add(Ui.PropRow(label, Ui.NumberWithSlider(box, slider.Slider), reset, 0.8));
                refresh[name] = show;
                show();
            }
            p.Children.Add(Ui.Card(rows));
            var view = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // 실행 취소·세밀 조정 막대 등 다른 곳에서 바뀐 값도 따라간다 (창이 보이는 동안만 구독)
            Action<IReadOnlyList<Addr>> changed = addrs => { foreach (var x in addrs) if (x.Kind == AddrKind.BlendShape && x.Target == rid && refresh.TryGetValue(x.Name, out var s)) s(); };
            view.AttachedToVisualTree += (_, __) => { doc.Changed -= changed; doc.Changed += changed; foreach (var s in refresh.Values) s(); };
            view.DetachedFromVisualTree += (_, __) => doc.Changed -= changed;
            return view;
        }
    }
}
