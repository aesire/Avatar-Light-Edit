// 가져온 파일 관리 (2026-10-21, 사용자 요청): 아바타 페이지의 "가져온 파일…"로 에디터 위에 떠 있는 창(FloatPanel)으로 연다.
//   재료       이 아바타를 만든 재료 (IR source.sources: Unity 프로젝트 폴더·씬·프리팹·.unitypackage). 있는지·크기, 탐색기에서 보기,
//              옮긴 파일 다시 찾기(경로 바꾸고 다시 만들기), 붙인 아이템용 패키지 빼기(다시 만들기). 첫 재료(아바타 자신)는 뺄 수 없다.
//   붙인 아이템  가져올 때 붙인 의상·헤어·악세서리 (붙일 곳 바꾸기·빼기는 아바타 페이지와 같은 동작)
//   이 PC      %LOCALAPPDATA%\ALEdit의 가져오기(imports)·꺼낸 패키지(packages)·구운 텍스처(baked). 크기, 열기, 지우기 (지금 열린 것·쓰는 것은 못 지움)
// 다시 만들기는 MainWindow.Rebuild (바꾼 값은 새 IR로 옮겨진다). 지우기는 되돌릴 수 없어 묻고 지운다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;

namespace VrcProto.AvHost
{
    public sealed class ImportedFilesPanel : DockPanel
    {
        readonly EditorContext _ctx;
        readonly SubTabs _tabs = new SubTabs("files", "가져온 파일", canFloat: false);
        readonly StackPanel _sources = new StackPanel(), _items = new StackPanel(), _cache = new StackPanel();

        /// <summary>MainWindow가 연결한다.</summary>
        public Func<string, string, Task>? ReplaceSource;   // (옛 경로, 새 경로) → 다시 만들기
        public Func<string, Task>? RemoveSource;
        public Action<string>? OpenIr, PlaceItem, RemoveItem;
        public Action? AddItem;
        public Func<string, string, string, Task<bool>>? Confirm;   // (제목, 설명, 단추) → 예
        public Func<string, Task<string?>>? PickFile;               // (제목) → 고른 경로

        static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit");

        public ImportedFilesPanel(EditorContext ctx)
        {
            _ctx = ctx;
            _tabs.Add("sources", "재료", _sources);
            _tabs.Add("items", "붙인 아이템", _items);
            _tabs.Add("cache", "이 PC에 저장된 것", _cache);
            _tabs.AddAction(Ui.IconButton(Glyph.Refresh, Refresh, "다시 읽기"));
            Children.Add(_tabs);
            _ctx.DocumentOpened += Refresh;
            Refresh();
        }

        public void Refresh()
        {
            BuildSources(); BuildItems(); BuildCache();
        }

        // ---------------- 재료 ----------------
        void BuildSources()
        {
            _sources.Children.Clear();
            var doc = _ctx.Doc;
            if (doc == null) { _sources.Children.Add(Ui.Wrap("아바타를 열면 이 아바타를 만든 파일이 여기에 나와요.")); return; }
            var recipe = Recipe.From(doc.Original);
            _sources.Children.Add(Ui.SectionTitle("이 아바타를 만든 파일", "가져올 때 읽은 Unity 프로젝트·씬·프리팹과 붙인 아이템의 패키지예요. 의상을 붙이거나 뺄 때 이 파일들로 아바타를 다시 만들어서, 옮기거나 지우면 \"다시 찾기\"로 새 자리를 알려 줘야 해요."));
            var rows = new StackPanel();
            if (recipe == null) rows.Children.Add(Ui.Wrap("이 파일에는 재료 정보가 없어요 (예전에 가져온 파일이나 샘플). 아이템을 붙이려면 Unity에서 다시 가져와 주세요.", Ui.KText2, 13));
            else
                for (int i = 0; i < recipe.Sources.Count; i++) rows.Children.Add(SourceRow(recipe.Sources[i], i == 0));
            _sources.Children.Add(Ui.Card(rows));

            _sources.Children.Add(Ui.SectionTitle("편집기가 만든 파일", "가져오기 결과와 텍스처예요. 지워도 원본은 그대로지만 다시 가져와야 열 수 있어요."));
            var ir = new StackPanel();
            ir.Children.Add(PathRow("가져온 파일", doc.IrPath, "열려 있음"));
            _sources.Children.Add(Ui.Card(ir));
        }

        Control SourceRow(string path, bool isAvatar)
        {
            bool exists = File.Exists(path) || Directory.Exists(path);
            string kind = Directory.Exists(path) ? "Unity 프로젝트 폴더" : path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) ? "패키지" : Path.GetExtension(path).TrimStart('.') switch { "unity" => "씬", "prefab" => "프리팹", var e => e };
            var desc = Ui.Text((isAvatar ? "아바타 · " : "아이템 소스 파일 · ") + kind + (exists ? " · 크기 계산 중…" : ""), 12, FontWeight.Normal, exists ? Ui.KText2 : Ui.KCaution);
            if (!exists) desc.Text = (isAvatar ? "아바타 · " : "아이템 소스 파일 · ") + kind + " · 없습니다 (이동하거나 삭제한 것 같습니다)";
            // Unity 프로젝트 폴더는 수만 개 파일이라 크기를 재지 않는다
            if (exists && !Directory.Exists(path)) Measure(path, n => desc.Text = (isAvatar ? "아바타 · " : "아이템 소스 파일 · ") + kind + " · " + Size(n));
            else if (exists) desc.Text = (isAvatar ? "아바타 · " : "아이템 소스 파일 · ") + kind;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            if (exists) right.Children.Add(Ui.IconButton(Glyph.Folder, () => Reveal(path), "탐색기에서 보기"));
            right.Children.Add(Ui.IconButton(Glyph.Copy, () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(path), "경로 복사"));
            right.Children.Add(Ui.IconButton(Glyph.Link, () => _ = Relink(path), exists ? "다른 파일로 바꾸기 (같은 패키지의 새 버전 등) · 아바타를 다시 만들어요" : "다시 찾기: 옮긴 자리를 골라요 · 아바타를 다시 만들어요"));
            if (!isAvatar) right.Children.Add(Ui.IconButton(Glyph.Delete, () => _ = Remove(path), "재료에서 빼기 (이 패키지에서 붙인 아이템이 있으면 다시 만들 수 없어요 — 먼저 아이템을 빼세요)"));
            return Row(Path.GetFileName(path.TrimEnd('\\', '/')), desc, path, right);
        }

        async Task Relink(string old)
        {
            if (PickFile == null || ReplaceSource == null) return;
            var picked = await PickFile($"{Path.GetFileName(old)}의 새 자리 고르기");
            if (picked == null || string.Equals(picked, old, StringComparison.OrdinalIgnoreCase)) return;
            await ReplaceSource(old, picked);
        }

        async Task Remove(string path)
        {
            if (RemoveSource == null || Confirm == null) return;
            if (!await Confirm($"{Path.GetFileName(path)} 빼기", "재료에서 빼고 아바타를 다시 만들어요. 이 패키지에서 붙인 아이템이 아직 있으면 다시 만들지 못하고 그대로 둬요. 파일은 지우지 않아요.", "빼기")) return;
            await RemoveSource(path);
        }

        // ---------------- 붙인 아이템 ----------------
        void BuildItems()
        {
            _items.Children.Clear();
            var doc = _ctx.Doc;
            var head = new TitleRow();
            var add = Ui.Button("붙이기…", () => AddItem?.Invoke(), "BOOTH 등에서 받은 .unitypackage의 의상·헤어·악세서리를 붙여요", Glyph.Add, accent: true);
            add.Margin = new Thickness(0);
            DockPanel.SetDock(add, Dock.Right);
            head.Children.Add(add);
            head.Children.Add(Ui.SectionTitle("가져와 붙인 아이템", "가져올 때 붙인 것만 있어요 (씬에 원래 있던 의상은 아바타의 일부라 아바타 페이지에서 켜고 꺼요)."));
            _items.Children.Add(head);
            if (doc == null) { _items.Children.Add(Ui.Wrap("아바타를 열면 붙인 아이템이 나와요.")); return; }
            var list = doc.Original.Outfits.Where(o => o.Prefab.Length > 0).ToList();
            if (list.Count == 0) { _items.Children.Add(Ui.Card(Ui.Wrap("붙인 아이템이 없어요. \"붙이기…\"로 패키지를 골라 보세요.", Ui.KText2, 13))); return; }
            var rows = new StackPanel();
            foreach (var o in list)
            {
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(Ui.IconButton(Glyph.Pin, () => PlaceItem?.Invoke(o.Prefab), "붙일 곳 바꾸기 (종류·뼈 직접 고르기)"));
                right.Children.Add(Ui.IconButton(Glyph.Delete, () => RemoveItem?.Invoke(o.Prefab), "아바타에서 빼기"));
                var desc = Ui.Text($"{Items.KindLabel(o.Kind)} · {Items.MethodLabel(o.Method)}" + (o.Target.Length > 0 ? " → " + Items.BoneLabel(o.Target) : ""), 12, FontWeight.Normal, o.NeedsPlacement ? Ui.KCaution : Ui.KText2);
                rows.Children.Add(Row(o.Name, desc, "프리팹 guid " + o.Prefab, right));
            }
            _items.Children.Add(Ui.Card(rows));
        }

        // ---------------- 이 PC ----------------
        void BuildCache()
        {
            _cache.Children.Clear();
            var cur = _ctx.Doc?.IrPath;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_ctx.Doc != null && Recipe.From(_ctx.Doc.Original) is Recipe r) foreach (var s in r.Sources) used.Add(Path.GetFullPath(s));

            Section("가져온 아바타", "imports", "Unity에서 가져온 아바타와 텍스처예요. 지우면 다시 가져와야 열 수 있어요 (세션은 열 때 재료로 다시 만들 수 있어요). 원본 Unity 파일은 그대로예요.",
                dir => cur != null && Path.GetFullPath(cur).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "지금 열려 있어 지울 수 없어요" : null,
                dir => Directory.EnumerateFiles(dir, "*.ir.json").FirstOrDefault());
            Section("꺼낸 패키지", "packages", "BOOTH zip에서 꺼낸 .unitypackage예요. 이 아바타의 재료로 쓰는 것은 지울 수 없어요.",
                dir => Directory.EnumerateFiles(dir, "*.unitypackage", SearchOption.AllDirectories).Any(f => used.Contains(Path.GetFullPath(f))) ? "이 아바타의 재료라 지울 수 없어요" : null, null);
            var baked = Path.Combine(Root, "baked");
            var bake = new StackPanel();
            bake.Children.Add(PathRow("구운 텍스처", baked, null));
            _cache.Children.Add(Ui.SectionTitle("구운 텍스처", "머티리얼 페이지 \"색 굽기\"로 만든 PNG예요. 세션이 가리키고 있으니 지우지 않는 편이 좋아요."));
            _cache.Children.Add(Ui.Card(bake));
        }

        void Section(string title, string sub, string help, Func<string, string?> locked, Func<string, string?>? irOf)
        {
            var root = Path.Combine(Root, sub);
            var dirs = Directory.Exists(root) ? new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.LastWriteTime).ToList() : new List<DirectoryInfo>();
            var head = new TitleRow();
            var open = Ui.IconButton(Glyph.Folder, () => Reveal(root), root);
            open.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(open, Dock.Right);
            head.Children.Add(open);
            var total = Ui.Text("", 12, FontWeight.Normal, Ui.KText3); total.Margin = new Thickness(8, 0, 0, 0);
            var t = new StackPanel { Orientation = Orientation.Horizontal };
            t.Children.Add(Ui.SectionTitle($"{title} {dirs.Count}개", help)); t.Children.Add(total);
            head.Children.Add(t);
            _cache.Children.Add(head);
            if (dirs.Count == 0) { _cache.Children.Add(Ui.Card(Ui.Wrap("없어요.", Ui.KText2, 13))); return; }
            if (Directory.Exists(root)) Measure(root, n => total.Text = Size(n));
            var rows = new StackPanel();
            foreach (var d in dirs)
            {
                var why = locked(d.FullName);
                var desc = Ui.Text($"{d.LastWriteTime:yyyy-MM-dd HH:mm} · 크기 계산 중…" + (why != null ? " · " + why : ""), 12, FontWeight.Normal, Ui.KText2);
                Measure(d.FullName, n => desc.Text = $"{d.LastWriteTime:yyyy-MM-dd HH:mm} · {Size(n)}" + (why != null ? " · " + why : ""));
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                var ir = irOf?.Invoke(d.FullName);
                if (ir != null && why == null) right.Children.Add(Ui.IconButton(Glyph.Open, () => OpenIr?.Invoke(ir), "이 아바타 열기"));
                right.Children.Add(Ui.IconButton(Glyph.Folder, () => Reveal(d.FullName), "탐색기에서 보기"));
                if (why == null) right.Children.Add(Ui.IconButton(Glyph.Delete, () => _ = Delete(d.FullName), "지우기 (되돌릴 수 없어요)"));
                rows.Children.Add(Row(d.Name, desc, d.FullName, right));
            }
            _cache.Children.Add(Ui.Card(rows));
        }

        async Task Delete(string dir)
        {
            if (Confirm == null) return;
            if (!await Confirm($"{Path.GetFileName(dir)} 지우기", $"이 폴더를 지워요. 휴지통을 거치지 않고 되돌릴 수 없어요.\n{dir}", "지우기")) return;
            try { await Task.Run(() => Directory.Delete(dir, true)); }
            catch (Exception e) { _ctx.Status("삭제하지 못했습니다: " + e.Message); BuildCache(); return; }
            _ctx.Status($"{Path.GetFileName(dir)}을(를) 지웠어요.");
            BuildCache();
        }

        // ---------------- 공통 ----------------
        Control PathRow(string name, string path, string? note)
        {
            bool exists = File.Exists(path) || Directory.Exists(path);
            var desc = Ui.Text(path, 12, FontWeight.Normal, Ui.KText2);
            if (exists) Measure(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, n => desc.Text = (note != null ? note + " · " : "") + Size(n) + " · " + path);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(Ui.IconButton(Glyph.Folder, () => Reveal(path), "탐색기에서 보기"));
            return Row(name, desc, path, right);
        }

        internal static Grid Row(string name, TextBlock desc, string tip, Control right)
        {
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8) };
            var n = Ui.Text(name);
            label.Children.Add(n);
            desc.TextWrapping = TextWrapping.Wrap; desc.MaxLines = 2;
            label.Children.Add(desc);
            ToolTip.SetTip(label, tip);
            var g = Ui.Columns(Ui.Star(), GridLength.Auto);
            g.MinHeight = Ui.RowHeight;
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }.Stroke(Ui.KDivider);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line); g.Children.Add(label.At(0)); g.Children.Add(right.At(1));
            return g;
        }

        internal static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else { var d = Directory.Exists(path) ? path : Path.GetDirectoryName(path); if (d != null && Directory.Exists(d)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true }); }
            }
            catch { }
        }

        /// <summary>파일·폴더 크기를 작업 스레드에서 재고 UI 스레드로 알려 준다 (가져온 아바타는 수 GB일 수 있다).</summary>
        internal static void Measure(string path, Action<long> done)
        {
            _ = Task.Run(() =>
            {
                long bytes = 0;
                try
                {
                    if (File.Exists(path)) bytes = new FileInfo(path).Length;
                    else if (Directory.Exists(path)) foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { try { bytes += new FileInfo(f).Length; } catch { } }
                }
                catch { }
                Dispatcher.UIThread.Post(() => done(bytes));
            });
        }

        internal static string Size(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:0.##} GB" : b >= 1L << 20 ? $"{b / 1048576.0:0.#} MB" : $"{Math.Max(1, b / 1024)} KB";
    }
}
