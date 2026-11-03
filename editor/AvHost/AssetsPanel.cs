// 머티리얼·에셋 관리 (2026-10-27, 사용자 요청): 머티리얼 페이지 탭 막대의 단추·명령 찾기로 에디터 위에 떠 있는 창(FloatPanel)으로 연다.
// 목록이 아니라 폴더형 (같은 날, 사용자 요청): Unity 프로젝트 창처럼 왼쪽 폴더 나무 + 오른쪽 타일(썸네일) + 아래 고른 것의 정보·단추.
//   폴더는 머티리얼·텍스처의 Unity 경로(Assets/…)로 만든다. 경로가 없는 것은 가상 폴더에:
//     메시           렌더러마다 메시 (IR에 FBX 경로가 없다)
//     FBX 안의 머티리얼 · 구운 텍스처(색 굽기, Addr.TexAsset) · 못 찾은 텍스처(머티리얼이 가리키지만 가져올 때 패키지에 없던 것)
//   처음 열면 Assets 아래 한 줄로만 이어진 폴더를 건너뛴 곳(예: Assets/Shop/Avatar)을 보여 준다.
//   타일: 텍스처는 썸네일(png·jpg·bmp·tga·psd, 작업 스레드에서 줄여 읽음), 머티리얼은 _MainTex 썸네일 + 머티리얼 표시. 바꾼 머티리얼 •, 안 쓰는 텍스처는 흐리게, 없는 파일·미지원 셰이더는 주의 색.
//   두 번 누르기: 폴더 = 들어가기, 머티리얼·메시 = 머티리얼 페이지, 텍스처 = 탐색기. 정보 칸의 "쓰는 곳" 단추로 머티리얼 ↔ 텍스처를 오간다.
//   찾기에 글자가 있으면 모든 폴더에서 맞는 것을 한 화면에.
// 문서를 바꾸지 않는다 (머티리얼 되돌리기만 예외). 크기 재기·탐색기 열기는 가져온 파일 창(ImportedFilesPanel)과 같다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class AssetsPanel : DockPanel
    {
        enum Kind { Folder, Material, Texture, Mesh }

        sealed class Entry
        {
            public Kind Kind; public string Name = "", Key = "", Folder = "";
            public IrMaterial? Mat; public IrTexture? Tex; public IrRenderer? Ren; public IrMesh? Mesh;
            public bool Modified, Warn, Dim;
            public string Desc = "", Tip = "", Thumb = "";   // Thumb: 썸네일로 쓸 이미지 파일
            public List<Entry> Links = new List<Entry>();    // 머티리얼 → 텍스처, 텍스처 → 머티리얼, 메시 → 머티리얼
            public List<string> LinkProps = new List<string>();
        }

        sealed class Folder
        {
            public string Name = "", Path = "";
            public bool Virtual; public string Glyph = AvHost.Glyph.Folder;
            public SortedDictionary<string, Folder> Subs = new SortedDictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
            public List<Entry> Items = new List<Entry>();
            public Folder? Parent;
            public int Count => Items.Count + Subs.Values.Sum(s => s.Count);
        }

        const string MeshRoot = "\u0001메시", FbxMats = "\u0001FBX 안의 머티리얼", BakedRoot = "\u0001구운 텍스처", LostRoot = "\u0001못 찾은 텍스처";

        readonly EditorContext _ctx;
        readonly StackPanel _tree = new StackPanel();
        readonly WrapPanel _tiles = new WrapPanel { Orientation = Orientation.Horizontal };
        readonly StackPanel _crumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBox _find = Ui.SearchBox("찾기");
        readonly ContentControl _detail = new ContentControl();
        readonly TextBlock _sum = Ui.Text("", 12, FontWeight.Normal, Ui.KText3);
        readonly Button _up;
        readonly HashSet<string> _open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 펼친 폴더 (다시 그려도 기억)
        static readonly Dictionary<string, Bitmap?> Thumbs = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);
        Folder _root = new Folder();
        string _cur = "";
        Entry? _sel;
        bool _initial = true;

        /// <summary>MainWindow가 연결한다.</summary>
        public Action<string?, string>? OpenMaterial;              // (렌더러, 머티리얼) → 머티리얼 페이지
        public Func<string, string, string, Task<bool>>? Confirm;   // (제목, 설명, 단추) → 예

        public AssetsPanel(EditorContext ctx)
        {
            _ctx = ctx;
            // ---- 위: 위로 · 경로 · 찾기 · 다시 읽기 ----
            _up = Ui.IconButton(Glyph.ChevronUp, () => { var f = Find(_cur); if (f?.Parent != null) Go(f.Parent.Path); }, "위 폴더로");
            var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            _find.Width = 170; _find.MinHeight = 30;
            right.Children.Add(_find);
            right.Children.Add(Ui.IconButton(Glyph.Refresh, Refresh, "다시 읽기"));
            DockPanel.SetDock(right, Dock.Right); DockPanel.SetDock(_up, Dock.Left);
            bar.Children.Add(right); bar.Children.Add(_up);
            _crumbs.Margin = new Thickness(4, 0); _crumbs.ClipToBounds = true;
            bar.Children.Add(_crumbs);
            var top = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }.Stroke(Ui.KDivider);
            DockPanel.SetDock(top, Dock.Top);
            Children.Add(top);

            // ---- 아래: 고른 것 정보 + 합계 ----
            var bottom = new StackPanel { Margin = new Thickness(12, 8, 12, 8), Spacing = 4 };
            bottom.Children.Add(_detail);
            _sum.TextWrapping = TextWrapping.Wrap;
            bottom.Children.Add(_sum);
            var bottomBorder = new Border { Child = bottom, BorderThickness = new Thickness(0, 1, 0, 0) }.Stroke(Ui.KDivider);
            DockPanel.SetDock(bottomBorder, Dock.Bottom);
            Children.Add(bottomBorder);

            // ---- 가운데: 폴더 나무 | 타일 ----
            var mid = Ui.Columns(new GridLength(190), new GridLength(1), Ui.Star());
            var treeView = new ScrollViewer { Content = _tree, Padding = new Thickness(4, 6), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }.Bg(Ui.KDivider);
            _tiles.Margin = new Thickness(8);
            var tileView = new ScrollViewer { Content = _tiles };
            // 빈 곳을 누르면 고른 것 풀기
            tileView.PointerPressed += (_, e) => { if (e.Source == tileView || e.Source == _tiles) Pick(null); };
            mid.Children.Add(treeView.At(0)); mid.Children.Add(split.At(1)); mid.Children.Add(tileView.At(2));
            Children.Add(mid);

            var findLater = Ui.Debounce(ShowTiles);
            _find.TextChanged += (_, __) => findLater();

            _ctx.DocumentOpened += () =>
            {
                // 값을 바꾸면 (머티리얼 페이지 등) 바꾼 표시·텍스처 쓰임이 달라진다. 안 보이면 미룬다
                if (_ctx.Doc != null)
                {
                    var doc = _ctx.Doc;
                    doc.Changed += addrs => { if (_ctx.Doc == doc && addrs.Any(a => a.IsMaterial || a.Kind == AddrKind.TexAsset)) Dispatcher.UIThread.Post(() => Later.Run(this, Refresh)); };
                    doc.SchemasChanged += () => Dispatcher.UIThread.Post(() => { if (_ctx.Doc == doc) Later.Run(this, Refresh); });
                }
                _initial = true; _open.Clear();
                Refresh();
            };
            Refresh();
        }

        // ================= 모델: IR → 폴더 =================
        public void Refresh()
        {
            var selKey = _sel?.Key;
            _root = Build(out var summary);
            _sum.Text = summary;
            if (_initial || Find(_cur) == null) { _cur = StartFolder(); _initial = false; }
            for (var f = Find(_cur); f != null; f = f.Parent) _open.Add(f.Path);
            _sel = selKey == null ? null : All(_root).FirstOrDefault(e => e.Key == selKey);
            ShowTree(); ShowTiles(); ShowDetail();
        }

        Folder Build(out string summary)
        {
            var root = new Folder { Name = "전체", Path = "" };
            var doc = _ctx.Doc;
            summary = "";
            if (doc == null) return root;
            var s = doc.Original;
            var mats = new Dictionary<string, Entry>();
            var texs = new Dictionary<string, Entry>();

            // 머티리얼
            var users = new Dictionary<string, List<IrRenderer>>();
            foreach (var r in s.Renderers)
                foreach (var g in r.Materials.Distinct())
                {
                    if (!users.TryGetValue(g, out var l)) users[g] = l = new List<IrRenderer>();
                    l.Add(r);
                }
            int modified = 0, unsupported = 0;
            foreach (var m in s.Materials)
            {
                var schema = doc.Schema(m.Guid);
                bool unsup = schema != null && !schema.Supported;
                var e = new Entry { Kind = Kind.Material, Name = m.Name, Key = "m:" + m.Guid, Mat = m, Modified = doc.IsMaterialModified(m.Guid), Warn = unsup };
                if (e.Modified) modified++;
                if (unsup) unsupported++;
                var used = users.TryGetValue(m.Guid, out var ul) ? ul : new List<IrRenderer>();
                var meshNames = used.Select(r => NodeName(s, r)).Distinct().ToList();
                e.Ren = used.FirstOrDefault();
                e.Desc = ShaderLabel(m, schema) + (unsup ? " (미리보기 미지원)" : "") + " · "
                    + (meshNames.Count == 0 ? "쓰는 메시 없음" : "메시 " + (meshNames.Count <= 3 ? string.Join(", ", meshNames) : $"{string.Join(", ", meshNames.Take(3))} 외 {meshNames.Count - 3}개"));
                e.Dim = used.Count == 0;
                e.Tip = (m.Path.Length > 0 ? m.Path : "FBX 안의 머티리얼") + "\nGUID " + m.Guid;
                mats[m.Guid] = e;
                Place(root, m.Path.Length > 0 ? Dir(m.Path) : FbxMats, e);
            }

            // 텍스처 (지금 값 = 바꾼 값 포함으로 쓰는 곳을 잇는다)
            foreach (var t in s.Textures) texs[t.Guid] = TexEntry(t, false);
            foreach (var a in doc.Overrides.Keys.Where(a => a.Kind == AddrKind.TexAsset))
                if (s.FindTexture(a.Target) == null && doc.Texture(a.Target) is IrTexture bt) texs[bt.Guid] = TexEntry(bt, true);
            foreach (var m in s.Materials)
            {
                var props = new HashSet<string>(m.TexEnvs.Keys);
                foreach (var a in doc.Overrides.Keys) if (a.Kind == AddrKind.MatTex && a.Target == m.Guid) props.Add(a.Name);
                foreach (var p in props.OrderBy(p => p == "_MainTex" ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
                {
                    if (!(doc.Get(Addr.MatTex(m.Guid, p)).AsStringOrNull is string k) || k.Length == 0) continue;
                    if (!texs.TryGetValue(k, out var te))
                        texs[k] = te = new Entry { Kind = Kind.Texture, Name = "GUID " + (k.Length > 12 ? k.Substring(0, 12) + "…" : k), Key = "t:" + k, Warn = true, Folder = LostRoot, Tip = k, Desc = "가져올 때 찾지 못했어요 (패키지에 없던 텍스처)" };
                    var me = mats[m.Guid];
                    if (!me.Links.Contains(te)) { me.Links.Add(te); me.LinkProps.Add(p); }
                    te.Links.Add(me); te.LinkProps.Add(p);
                    if (me.Thumb.Length == 0 && p == "_MainTex" && te.Tex != null) me.Thumb = te.Tex.File;
                }
            }
            int missing = 0, unused = 0, unreadable = 0;
            var files = new List<string>();
            foreach (var te in texs.Values)
            {
                if (te.Links.Count == 0) { te.Dim = true; unused++; }
                if (te.Warn) { if (te.Tex != null && File.Exists(te.Tex.File)) unreadable++; else missing++; }
                if (te.Tex != null && !te.Warn) files.Add(te.Tex.File);
                var useText = te.Links.Count == 0 ? "어느 머티리얼도 안 써요" : "머티리얼 " + string.Join(", ", te.Links.Select((l, i) => $"{l.Name}({te.LinkProps[i]})").Take(3)) + (te.Links.Count > 3 ? $" 외 {te.Links.Count - 3}곳" : "");
                te.Desc = te.Desc.Length > 0 ? te.Desc + " · " + useText : useText;
                Place(root, te.Folder, te);
            }
            foreach (var me in mats.Values)
                me.Desc += me.Links.Count > 0 ? $" · 텍스처 {me.Links.Count}" : "";

            // 메시 (렌더러마다)
            long tris = 0; int verts = 0;
            foreach (var r in s.Renderers)
            {
                var mesh = s.FindMesh(r.Mesh);
                long t = Tris(mesh); tris += t; verts += mesh?.VertexCount ?? 0;
                var name = NodeName(s, r);
                bool on = r.Enabled && r.Node >= 0 && r.Node < s.Nodes.Count && s.Nodes[r.Node].Active;
                var e = new Entry { Kind = Kind.Mesh, Name = name, Key = "r:" + r.Id, Ren = r, Mesh = mesh, Dim = !on };
                var parts = new List<string>();
                if (mesh == null) parts.Add("메시 없음");
                else
                {
                    if (mesh.Name != name) parts.Add(mesh.Name);
                    parts.Add($"폴리곤 {t:N0} · 정점 {mesh.VertexCount:N0}");
                    if (mesh.BlendShapes.Count > 0) parts.Add($"블렌드쉐이프 {mesh.BlendShapes.Count}");
                }
                if (r.Type != "SkinnedMeshRenderer") parts.Add("고정 메시");
                if (!on) parts.Add("처음에 꺼짐");
                e.Desc = string.Join(" · ", parts);
                for (int i = 0; i < r.Materials.Count; i++)
                    if (mats.TryGetValue(r.Materials[i], out var me)) { e.Links.Add(me); e.LinkProps.Add("슬롯 " + i); }
                e.Thumb = e.Links.Select(l => l.Thumb).FirstOrDefault(x => x.Length > 0) ?? "";
                e.Tip = string.Join("\n", r.Materials.Select((g, i) => $"슬롯 {i}: {s.FindMaterial(g)?.Name ?? g}"));
                Place(root, MeshRoot, e);
            }
            // 메시는 폴리곤 많은 순
            if (root.Subs.TryGetValue(MeshRoot, out var mf)) mf.Items = mf.Items.OrderByDescending(x => Tris(x.Mesh)).ToList();

            summary = $"머티리얼 {s.Materials.Count} (변경한 항목 {modified}" + (unsupported > 0 ? $", 미지원 {unsupported}" : "") + ")"
                + $" · 텍스처 {texs.Count}" + (missing > 0 ? $" (없는 파일 {missing})" : "") + (unreadable > 0 ? $" (읽을 수 없는 형식 {unreadable})" : "") + (unused > 0 ? $" · 사용하지 않는 텍스처 {unused}" : "")
                + $" · 사용하는 곳 {s.Renderers.Count} · 폴리곤 {tris:N0} · 정점 {verts:N0}";
            // 텍스처 원본 크기 합 (같은 파일을 두 텍스처가 가리킬 수 있어 한 번만)
            var distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var head = summary;
            if (distinct.Count > 0)
                _ = Task.Run(() =>
                {
                    long total = 0;
                    foreach (var f in distinct) { try { total += new FileInfo(f).Length; } catch { } }
                    Dispatcher.UIThread.Post(() => { if (_ctx.Doc == doc && _sum.Text == Texts.T(head)) _sum.Text = head + " · 텍스처 파일 " + ImportedFilesPanel.Size(total); });
                });
            return root;
        }

        Entry TexEntry(IrTexture t, bool baked)
        {
            bool exists = t.File.Length > 0 && File.Exists(t.File);
            bool readable = !exists || ImageFiles.PreviewReads(t.File);   // 미리보기가 못 읽는 형식 (EXR·TIF 등, 2026-10-27)
            var imp = t.Importer;
            var e = new Entry
            {
                Kind = Kind.Texture, Tex = t, Key = "t:" + t.Guid, Warn = !exists || !readable,
                Name = Path.GetFileName(t.Path.Length > 0 ? t.Path : t.File),
                Folder = baked ? BakedRoot : t.Path.Length > 0 ? Dir(t.Path) : LostRoot,
                Thumb = exists ? t.File : "",
                Tip = (t.Path.Length > 0 ? t.Path + "\n" : "") + (t.File.Length > 0 ? t.File : "(파일 없음)"),
                Desc = (baked ? "베이크된 텍스처 · " : "") + $"최대 {imp.MaxSize}" + (imp.NormalMap ? " · 노멀 맵" : !imp.SRgb ? " · 선형" : "") + (exists ? "" : " · 파일이 없습니다") + (readable ? "" : " · 미리보기에서 읽을 수 없는 형식"),
            };
            return e;
        }

        // 문화권 비교의 StartsWith(string)는 \u0001을 무시해 모든 경로가 맞는다 → 글자로 본다
        static bool IsVirtual(string path) => path.Length > 0 && path[0] == '\u0001';

        static string Dir(string unityPath) { var i = unityPath.LastIndexOf('/'); return i > 0 ? unityPath.Substring(0, i) : ""; }

        static void Place(Folder root, string dir, Entry e)
        {
            e.Folder = dir;
            var f = root;
            if (IsVirtual(dir))
            {
                if (!root.Subs.TryGetValue(dir, out var v))
                    root.Subs[dir] = v = new Folder { Name = dir.Substring(1), Path = dir, Virtual = true, Parent = root, Glyph = dir == MeshRoot ? Glyph.Shapes : dir == LostRoot ? Glyph.Warning : Glyph.Folder };
                f = v;
            }
            else if (dir.Length > 0)
                foreach (var part in dir.Split('/'))
                {
                    var path = f.Path.Length == 0 ? part : f.Path + "/" + part;
                    if (!f.Subs.TryGetValue(part, out var sub)) f.Subs[part] = sub = new Folder { Name = part, Path = path, Parent = f };
                    f = sub;
                }
            f.Items.Add(e);
        }

        Folder? Find(string path)
        {
            if (path.Length == 0) return _root;
            if (IsVirtual(path)) return _root.Subs.TryGetValue(path, out var v) ? v : null;
            var f = _root;
            foreach (var part in path.Split('/')) if (!f.Subs.TryGetValue(part, out f!)) return null;
            return f;
        }

        /// <summary>Assets 아래 한 줄로만 이어진 폴더(하위 폴더 하나, 항목 없음)를 건너뛴 곳</summary>
        string StartFolder()
        {
            if (!_root.Subs.TryGetValue("Assets", out var f)) return "";
            while (f.Items.Count == 0 && f.Subs.Count == 1) f = f.Subs.Values.First();
            return f.Path;
        }

        static IEnumerable<Entry> All(Folder f) => f.Items.Concat(f.Subs.Values.SelectMany(All));

        // ================= 화면 =================
        void Go(string path)
        {
            _cur = path;
            for (var f = Find(path); f != null; f = f.Parent) _open.Add(f.Path);
            if (_find.Text?.Length > 0) _find.Text = "";   // 바뀜 알림이 ShowTiles를 부른다
            ShowTree(); ShowTiles();
        }

        void ShowTree()
        {
            _tree.Children.Clear();
            if (_ctx.Doc == null) return;
            // 진짜 폴더 먼저, 가상 폴더는 아래에
            foreach (var f in _root.Subs.Values.OrderBy(x => x.Virtual)) TreeRow(f, 0);
        }

        void TreeRow(Folder f, int depth)
        {
            bool open = _open.Contains(f.Path), cur = f.Path == _cur;
            var row = new DockPanel { Height = 26, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            var chev = Ui.Icon(f.Subs.Count == 0 ? "" : open ? Glyph.ChevronDown : Glyph.ChevronRight, 9, Ui.KText3);
            chev.Width = 16; chev.TextAlignment = TextAlignment.Center; chev.VerticalAlignment = VerticalAlignment.Center;
            chev.Margin = new Thickness(depth * 12, 0, 0, 0);
            var chevBox = new Border { Child = chev, Background = Brushes.Transparent };
            chevBox.PointerPressed += (_, e) =>
            {
                if (f.Subs.Count == 0) return;
                if (!_open.Remove(f.Path)) _open.Add(f.Path);
                e.Handled = true; ShowTree();
            };
            var icon = Ui.Icon(f.Glyph, 13, f.Path == LostRoot ? Ui.KCaution : cur ? Ui.KAccentText : Ui.KText2);
            icon.Margin = new Thickness(2, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            var label = Ui.Text(f.Name, 13, cur ? FontWeight.SemiBold : FontWeight.Normal, cur ? Ui.KAccentText : Ui.KText);
            label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var count = Ui.Text(f.Count.ToString(), 11, FontWeight.Normal, Ui.KText3);
            count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(6, 0, 4, 0);
            DockPanel.SetDock(chevBox, Dock.Left); DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(chevBox); row.Children.Add(icon); row.Children.Add(count); row.Children.Add(label);
            var b = new Border { Child = row, CornerRadius = new CornerRadius(4) };
            if (cur) b.Bg(Ui.KSelect);
            b.PointerEntered += (_, __) => { if (f.Path != _cur) b.Bg(Ui.KHover); };
            b.PointerExited += (_, __) => { if (f.Path != _cur) b.Background = null; };
            b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) Go(f.Path); };
            ToolTip.SetTip(b, f.Virtual ? f.Name : f.Path);
            _tree.Children.Add(b);
            if (open) foreach (var s in f.Subs.Values) TreeRow(s, depth + 1);
        }

        void ShowCrumbs(bool searching)
        {
            _crumbs.Children.Clear();
            var cur = Find(_cur);
            _up.IsEnabled = !searching && cur?.Parent != null;
            if (searching) { _crumbs.Children.Add(Ui.Text("찾기 결과 (모든 폴더)", 13, FontWeight.SemiBold)); return; }
            var chain = new List<Folder>();
            for (var f = cur; f != null; f = f.Parent) chain.Insert(0, f);
            for (int i = 0; i < chain.Count; i++)
            {
                var f = chain[i];
                // 깊으면 가운데를 "…"로 (전체 › … › 아바타 A › Texture): 끝(지금 폴더)이 늘 보이게. "…"는 그 바로 위 폴더로
                if (chain.Count > 4 && i > 0 && i < chain.Count - 2)
                {
                    if (i != 1) continue;
                    var up = chain[chain.Count - 3];
                    f = new Folder { Name = "…", Path = up.Path };
                }
                if (i > 0) _crumbs.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center }.Fg(Ui.KText3));
                bool last = i == chain.Count - 1;
                var t = Ui.Text(f.Name, 13, last ? FontWeight.SemiBold : FontWeight.Normal, last ? Ui.KText : Ui.KText2);
                var btn = new Button { Content = t, Padding = new Thickness(4, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0 };
                btn.Click += (_, __) => Go(f.Path);
                _crumbs.Children.Add(btn);
            }
        }

        void ShowTiles()
        {
            _tiles.Children.Clear();
            var q = _find.Text?.Trim() ?? "";
            ShowCrumbs(q.Length > 0);
            if (_ctx.Doc == null) { _tiles.Children.Add(Ui.Wrap("아바타를 열면 머티리얼·텍스처·메시가 폴더로 나와요.", Ui.KText2, 13)); return; }
            if (q.Length > 0)
            {
                var hits = All(_root).Where(e => Match(q, e)).OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var e in hits) _tiles.Children.Add(Tile(e));
                if (hits.Count == 0) _tiles.Children.Add(Ui.Wrap("맞는 것이 없어요.", Ui.KText2, 13));
                return;
            }
            var f = Find(_cur) ?? _root;
            foreach (var s in f.Subs.Values.OrderBy(x => x.Virtual)) _tiles.Children.Add(FolderTile(s));
            // 머티리얼 → 텍스처 순, 같은 종류는 이름 순 (메시 폴더는 폴리곤 순 그대로)
            var items = f.Path == MeshRoot ? f.Items : f.Items.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var e in items) _tiles.Children.Add(Tile(e));
            if (f.Subs.Count + f.Items.Count == 0) _tiles.Children.Add(Ui.Wrap("빈 폴더예요.", Ui.KText2, 13));
        }

        static bool Match(string q, Entry e) =>
            e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.Tip.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        const double TileW = 92, ThumbSize = 64;

        Control FolderTile(Folder f)
        {
            var icon = Ui.Icon(f.Glyph, 40, f.Path == LostRoot ? Ui.KCaution : Ui.KAccentText);
            icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
            var tile = TileFrame(new Border { Width = ThumbSize, Height = ThumbSize, Child = icon }, null, f.Name, $"{f.Count}개", false, false, f.Virtual ? f.Name : f.Path);
            tile.DoubleTapped += (_, __) => Go(f.Path);
            tile.Tapped += (_, __) => Pick(null);
            return tile;
        }

        Control Tile(Entry e)
        {
            var thumb = new Grid { Width = ThumbSize, Height = ThumbSize };
            var glyph = e.Kind switch { Kind.Material => Glyph.Material, Kind.Mesh => Glyph.Shapes, _ => Glyph.Document };
            var fallback = Ui.Icon(glyph, 32, e.Warn ? Ui.KCaution : Ui.KText2);
            fallback.HorizontalAlignment = HorizontalAlignment.Center; fallback.VerticalAlignment = VerticalAlignment.Center;
            thumb.Children.Add(fallback);
            if (e.Thumb.Length > 0)
            {
                var img = new Image { Stretch = Stretch.Uniform, Width = ThumbSize, Height = ThumbSize };
                thumb.Children.Add(img);
                LoadThumb(e.Thumb, bmp => { img.Source = bmp; fallback.IsVisible = bmp == null; });
            }
            if (e.Kind != Kind.Texture && e.Thumb.Length > 0)
            {
                // 썸네일이 텍스처인 머티리얼·메시: 구석에 종류 표시
                var mark = Ui.Icon(glyph, 11, Ui.KText);
                mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center;
                thumb.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = mark }.Bg(Ui.KCard));
            }
            var kind = e.Kind switch { Kind.Material => "머티리얼", Kind.Mesh => "메시", _ => Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant() is var x && x.Length > 0 ? x : "텍스처" };
            var tile = TileFrame(thumb, e, e.Name + (e.Modified ? " •" : ""), kind, e.Warn, e.Dim, e.Tip);
            tile.Tapped += (_, __) => Pick(e);
            tile.DoubleTapped += (_, __) => Activate(e);
            return tile;
        }

        Border TileFrame(Control thumb, Entry? entry, string name, string sub, bool warn, bool dim, string tip)
        {
            var p = new StackPanel { Spacing = 3, Width = TileW - 8 };
            thumb.HorizontalAlignment = HorizontalAlignment.Center;
            p.Children.Add(new Border { Child = thumb, CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center });
            var n = Ui.Text(name, 12, FontWeight.Normal, warn ? Ui.KCaution : Ui.KText);
            n.TextWrapping = TextWrapping.Wrap; n.MaxLines = 2; n.TextTrimming = TextTrimming.CharacterEllipsis; n.TextAlignment = TextAlignment.Center;
            p.Children.Add(n);
            var k = Ui.Text(sub, 10, FontWeight.Normal, Ui.KText3); k.TextAlignment = TextAlignment.Center; k.HorizontalAlignment = HorizontalAlignment.Center;
            p.Children.Add(k);
            var b = new Border { Child = p, Width = TileW, Padding = new Thickness(4, 6), Margin = new Thickness(2), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Opacity = dim ? 0.5 : 1, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            b.Tag = entry;
            Mark(b);
            b.PointerEntered += (_, __) => { if (entry == null || entry != _sel) b.Bg(Ui.KHover); };
            b.PointerExited += (_, __) => Mark(b);
            ToolTip.SetTip(b, tip);
            return b;
        }

        /// <summary>썸네일을 작업 스레드에서 작게 읽는다 (png·jpg·bmp, 10-27부터 tga·psd도; exr·tif 등은 아이콘). 한 번 읽은 것은 기억.</summary>
        internal static void LoadThumb(string file, Action<Bitmap?> done)
        {
            if (Thumbs.TryGetValue(file, out var have)) { done(have); return; }
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!ImageFiles.CanRead(ext)) { Thumbs[file] = null; done(null); return; }
            _ = Task.Run(() =>
            {
                Bitmap? bmp = null;
                try
                {
                    if (ext is ".tga" or ".psd" or ".tif" or ".tiff" or ".exr") bmp = ImageFiles.LoadSmall(file, (int)(ThumbSize * 2));
                    else { using var fs = File.OpenRead(file); bmp = Bitmap.DecodeToWidth(fs, (int)(ThumbSize * 2), BitmapInterpolationMode.MediumQuality); }
                }
                catch { }
                Dispatcher.UIThread.Post(() => { Thumbs[file] = bmp; done(bmp); });
            });
        }

        void Pick(Entry? e)
        {
            if (_sel == e) return;
            _sel = e;
            // 타일을 다시 만들지 않고 표시만 (다시 만들면 두 번 누르기가 떨어진 타일로 간다)
            foreach (var t in _tiles.Children.OfType<Border>()) Mark(t);
            ShowDetail();
        }

        void Mark(Border t)
        {
            if (t.Tag != null && t.Tag == _sel) t.Bg(Ui.KSelect).Stroke(Ui.KAccent);
            else { t.Background = Brushes.Transparent; t.BorderBrush = Brushes.Transparent; }
        }

        // 화면 확인(--ui-shots)용
        internal void ShotGo(string path) => Go(path);
        internal string ShotStart => StartFolder();
        internal void ShotPick(string kind) { var e = All(_root).FirstOrDefault(x => x.Kind.ToString() == kind && x.Links.Count > 0); if (e != null) Reveal(e); }
        internal void ShotFind(string q) => _find.Text = q;

        /// <summary>쓰는 곳 단추: 그 항목이 있는 폴더로 가서 고른다</summary>
        void Reveal(Entry e)
        {
            _sel = e;
            Go(e.Folder);
            ShowDetail();
        }

        void Activate(Entry e)
        {
            switch (e.Kind)
            {
                case Kind.Material: OpenMaterial?.Invoke(e.Ren?.Id, e.Mat!.Guid); break;
                case Kind.Mesh: if (e.Ren != null && e.Ren.Materials.Count > 0) OpenMaterial?.Invoke(e.Ren.Id, e.Ren.Materials[0]); break;
                case Kind.Texture: if (e.Tex != null && !e.Warn) ImportedFilesPanel.Reveal(e.Tex.File); break;
            }
        }

        void ShowDetail()
        {
            var e = _sel;
            if (e == null) { _detail.Content = Ui.Text("하나를 누르면 정보가 여기에 나와요. 두 번 누르면 열어요.", 12, FontWeight.Normal, Ui.KText3); return; }
            var p = new StackPanel { Spacing = 4 };
            var head = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            switch (e.Kind)
            {
                case Kind.Material:
                    buttons.Children.Add(Ui.IconButton(Glyph.Edit, () => Activate(e), "머티리얼 페이지에서 열기"));
                    if (e.Modified) buttons.Children.Add(Ui.IconButton(Glyph.Reset, () => _ = ResetMaterial(e.Mat!), "바꾼 값 모두 되돌리기 (실행 취소로 되살릴 수 있어요)"));
                    buttons.Children.Add(Ui.IconButton(Glyph.Copy, () => CopyPath(e.Mat!.Path.Length > 0 ? e.Mat.Path : e.Mat.Guid), "Unity 경로 복사"));
                    break;
                case Kind.Texture:
                    if (e.Tex != null && !e.Warn) buttons.Children.Add(Ui.IconButton(Glyph.Folder, () => ImportedFilesPanel.Reveal(e.Tex.File), "탐색기에서 보기"));
                    if (e.Tex != null && e.Tex.File.Length > 0) buttons.Children.Add(Ui.IconButton(Glyph.Copy, () => CopyPath(e.Tex.File), "파일 경로 복사"));
                    break;
                case Kind.Mesh:
                    if (e.Ren != null && e.Ren.Materials.Count > 0) buttons.Children.Add(Ui.IconButton(Glyph.Material, () => Activate(e), "이 메시의 머티리얼 열기"));
                    break;
            }
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            var name = Ui.Text(e.Name + (e.Modified ? "  •" : ""), 14, FontWeight.SemiBold, e.Warn ? Ui.KCaution : Ui.KText);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(name, e.Tip);
            head.Children.Add(name);
            p.Children.Add(head);
            var desc = Ui.Text(e.Desc, 12, FontWeight.Normal, Ui.KText2);
            desc.TextWrapping = TextWrapping.Wrap;
            if (e.Kind == Kind.Texture && e.Tex != null && !e.Warn)
            {
                var basic = e.Desc;
                ImportedFilesPanel.Measure(e.Tex.File, n => { if (_sel == e) desc.Text = ImportedFilesPanel.Size(n) + " · " + basic; });
            }
            p.Children.Add(desc);
            // 쓰는 곳: 누르면 그 항목으로
            if (e.Links.Count > 0)
            {
                var links = new WrapPanel();
                links.Children.Add(new TextBlock { Text = e.Kind == Kind.Material ? "텍스처" : "머티리얼", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }.Fg(Ui.KText3));
                for (int i = 0; i < e.Links.Count; i++)
                {
                    var l = e.Links[i];
                    // 글을 TextBlock으로: 문자열 그대로면 _가 단축키 표시로 사라진다 (Body_Main → BodyMain)
                    var chip = new Button { Content = new TextBlock { Text = $"{l.Name} · {e.LinkProps[i]}" }, FontSize = 11, Padding = new Thickness(8, 2), MinHeight = 0, Margin = new Thickness(0, 2, 4, 2) };
                    ToolTip.SetTip(chip, "이 항목으로 가기");
                    chip.Click += (_, __) => Reveal(l);
                    links.Children.Add(chip);
                }
                p.Children.Add(links);
            }
            _detail.Content = p;
        }

        static string NodeName(IrScene s, IrRenderer r) => r.Node >= 0 && r.Node < s.Nodes.Count ? s.Nodes[r.Node].Name : r.Id;

        static string ShaderLabel(IrMaterial m, MaterialSchema? schema)
        {
            var name = schema?.Shader is string sh && sh.Length > 0 ? sh : m.ShaderName;
            return string.IsNullOrEmpty(name) ? "셰이더 " + (m.ShaderGuid.Length > 8 ? m.ShaderGuid.Substring(0, 8) + "…" : m.ShaderGuid) : name!;
        }

        static long Tris(IrMesh? m) => m == null ? 0 : m.Submeshes.Where(sm => sm.Topology == "Triangles").Sum(sm => (long)sm.Indices.Count / 3);

        void CopyPath(string path) => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(path);

        async Task ResetMaterial(IrMaterial m)
        {
            var doc = _ctx.Doc;
            if (doc == null) return;
            if (Confirm != null && !await Confirm($"{m.Name} 되돌리기", "이 머티리얼에서 바꾼 값을 모두 원본으로 돌려요. 실행 취소(Ctrl+Z)로 되살릴 수 있어요.", "되돌리기")) return;
            doc.ResetWhere(a => a.IsMaterial && a.Target == m.Guid, $"{m.Name} 모두 되돌리기");
            _ctx.Status($"{m.Name}을(를) 원본으로 되돌렸어요.");
        }
    }
}
