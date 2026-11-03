// "올리기" 페이지의 올리기 전 점검 (UI 개선 3번, 2026-10-21): 페이지를 열 때마다 다시 본다.
// 등급 예상 · 붙일 곳을 못 정한 아이템 · 다른 아바타용 같은 아이템 · 미리보기에서 지원하지 않는 셰이더 · 미리보기가 못 읽는 텍스처 형식 · 없어진 재료 파일 · 저장하지 않은 바꾼 값.
// 줄을 누르면 고칠 수 있는 곳(페이지·가져온 파일 창)으로 간다. 막지는 않는다 — 올리기 창이 따로 확인한다.
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        readonly StackPanel _uploadCheck = new StackPanel();

        void RefreshUploadCheck()
        {
            _uploadCheck.Children.Clear();
            var doc = _ctx.Doc;
            if (doc == null) { _uploadCheck.Children.Add(Ui.Card(Ui.Wrap("아바타를 열면 올리기 전에 볼 것이 여기에 나와요.", Ui.KText2, 13))); return; }
            var scene = doc.Original;
            var rows = new StackPanel();

            // 등급
            float[]? size = null;
            var b = _ctx.SceneBounds;
            if (!b.IsNull && b.Has("min"))
            {
                var mn = b["min"].ToFloatArray(); var mx = b["max"].ToFloatArray();
                if (mn.Length >= 3 && mx.Length >= 3) size = new[] { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
            }
            var meshes = _ctx.Meshes;
            var stats = AvatarStats.From(scene, size, meshes != null ? id => meshes.Counts(id).tris : (Func<string, int>?)null);
            var pc = stats.Overall(PerfPlatform.PC); var quest = stats.Overall(PerfPlatform.Mobile);
            rows.Children.Add(Check(pc <= PerfRank.Poor ? 0 : 1, $"퍼포먼스 등급 예상: PC {AvatarStats.Label(pc)} · 모바일 {AvatarStats.Label(quest)}",
                pc <= PerfRank.Poor ? "PC는 올릴 수 있는 등급이에요" : "Very Poor도 올릴 수 있지만, 보는 사람 설정에 따라 가려질 수 있어요", () => _side.Select("optimize")));

            foreach (var (level, title, desc, go) in UploadIssues(doc)) rows.Children.Add(Check(level, title, desc, go));

            // 저장
            int n = doc.Overrides.Count;
            rows.Children.Add(doc.IsDirty
                ? Check(1, $"저장하지 않은 바꾼 값 {n}개", "올리기는 지금 값으로 하지만, 세션을 저장해 두면 나중에 이어서 고칠 수 있어요 (Ctrl+S)", () => _ = SaveSession(false))
                : Check(0, n == 0 ? "바꾼 값 없음" : $"바꾼 값 {n}개 · 저장됨", n == 0 ? "원본 그대로 올라가요" : "세션에 저장돼 있어요", null));

            _uploadCheck.Children.Add(Ui.Card(rows));
            RefreshCheckItem();
        }

        /// <summary>
        /// 고칠 만한 것 (아이템 · 셰이더 · 재료): 올리기 페이지와 상태 표시줄 "올리기 점검" 칸이 같이 쓴다.
        /// 등급·저장은 빼 둔다 (등급 계산은 무겁고, 저장 여부는 "바꾼 값" 칸에 이미 보인다).
        /// </summary>
        System.Collections.Generic.List<(int Level, string Title, string Desc, Action? Go)> UploadIssues(Document doc)
        {
            var list = new System.Collections.Generic.List<(int, string, string, Action?)>();
            var scene = doc.Original;
            // 아이템
            var unplaced = scene.Outfits.Where(o => o.NeedsPlacement).Select(o => o.Name).ToList();
            if (unplaced.Count > 0) list.Add((2, $"붙일 곳을 정하지 못한 아이템 {unplaced.Count}개", string.Join(", ", unplaced) + " · 아바타 루트에 그대로 있습니다", () => _side.Select("avatar")));
            var off = scene.Outfits.Where(o => { Items.FitText(o.Matched, o.Bones, o.OffsetMean, out var bad); return bad; }).Select(o => o.Name).ToList();
            if (off.Count > 0) list.Add((1, $"다른 아바타용 같은 아이템 {off.Count}개", string.Join(", ", off) + " · 본 위치가 평균 3 cm 넘게 어긋납니다", () => _side.Select("avatar")));

            // 셰이더
            var unsupported = scene.Materials.Where(m => doc.Schema(m.Guid)?.Supported == false).Select(m => m.Name).ToList();
            if (unsupported.Count > 0) list.Add((1, $"미리보기에서 지원하지 않는 셰이더 {unsupported.Count}개", "미리보기만 다르게 보이고 업로드하면 원래 셰이더로 표시됩니다 · " + string.Join(", ", unsupported.Take(4)) + (unsupported.Count > 4 ? " …" : ""), () => _side.Select("material")));

            // 텍스처 (2026-10-27): 미리보기가 못 읽는 형식은 흰색으로 보인다 (올리면 Unity가 읽으니 원래대로)
            var odd = scene.Textures.Where(t => t.File.Length > 0 && System.IO.File.Exists(t.File) && !ImageFiles.PreviewReads(t.File)).Select(t => System.IO.Path.GetFileName(t.File)).ToList();
            if (odd.Count > 0) list.Add((1, $"미리보기에서 못 읽는 텍스처 {odd.Count}개", "미리보기에서는 텍스처 없이 보이고, 업로드하면 원래대로 표시됩니다 · " + string.Join(", ", odd.Take(4)) + (odd.Count > 4 ? " …" : ""), OpenAssetsWindow));

            // 재료
            var recipe = Recipe.From(scene);
            var missing = recipe?.MissingSources() ?? new System.Collections.Generic.List<string>();
            if (missing.Count > 0) list.Add((1, $"없어진 재료 파일 {missing.Count}개", "아이템을 장착·제거하려면 다시 지정해야 합니다 ·" + string.Join(", ", missing.Select(System.IO.Path.GetFileName)), OpenFilesWindow));
            return list;
        }

        // 상태 표시줄 "올리기 점검" 칸 (2026-10-27 동선 줄이기 2번): 올리기 페이지에 가기 전에 고칠 것이 보이게. 누르면 올리기 페이지
        readonly StatusItem _checkItem = new StatusItem("올리기 점검") { IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand) };

        void SetupCheckItem()
        {
            _checkItem.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) _side.Select("upload"); };
        }

        void RefreshCheckItem()
        {
            var doc = _ctx.Doc;
            if (doc == null) { _checkItem.IsVisible = false; return; }
            var issues = UploadIssues(doc);
            _checkItem.IsVisible = issues.Count > 0;
            if (issues.Count == 0) return;
            _checkItem.Set(issues.Any(i => i.Level >= 2) ? StatusItem.State.Error : StatusItem.State.Warn, $"올리기 전에 볼 것 {issues.Count}개");
            ToolTip.SetTip(_checkItem, string.Join("\n", issues.Select(i => "· " + i.Title)) + "\n\n누르면 업로드 페이지로 갑니다");
        }

        /// <summary>점검 한 줄: 상태 아이콘(0 좋음 · 1 주의 · 2 고칠 것) · 제목 · 설명. 누르면 go</summary>
        static Control Check(int level, string title, string desc, Action? go)
        {
            var icon = Ui.Icon(level == 0 ? "" : level == 1 ? Glyph.Warning : "", 14, level == 0 ? Ui.KSuccess : level == 1 ? Ui.KCaution : Ui.KCritical);
            icon.Width = 28; icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 6, 0);
            var text = new StackPanel { Spacing = 1 };
            var t = Ui.Text(title); t.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(t);
            text.Children.Add(Ui.Caption(desc));
            var row = new DockPanel { Margin = new Thickness(0, 10) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (go != null)
            {
                var arrow = Ui.Icon(Glyph.ChevronRight, 10, Ui.KText3); arrow.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(arrow, Dock.Right);
                row.Children.Add(arrow);
            }
            row.Children.Add(text);
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row, Background = Brushes.Transparent }.Stroke(Ui.KDivider);
            if (go != null)
            {
                line.Cursor = new Cursor(StandardCursorType.Hand);
                line.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) go(); };
            }
            return line;
        }
    }
}
