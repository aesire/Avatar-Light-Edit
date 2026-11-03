// 메인 창: 방금 저장한 사진 칸 (2026-10-27, 사용자 "소소한 불편 고치기")
//   촬영 결과가 6초 뒤 흐려지는 상태 글에만 있어 탐색기를 거쳐야 했다 → 상태 표시줄 오른쪽에 파일 이름 + 열기·폴더·복사 단추.
//   1분 뒤 또는 × 로 숨긴다. 미리보기(자식 창) 위에는 그릴 수 없어 상태 표시줄에 둔다.
using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        readonly StackPanel _savedChip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Margin = new Thickness(12, 0, 0, 0) };
        DispatcherTimer? _savedHide;

        void ShowSaved(string path)
        {
            if (_shotsDir != null || !File.Exists(path)) return;
            _savedChip.Children.Clear();
            var name = Ui.Caption("사진 " + Path.GetFileName(path));
            name.VerticalAlignment = VerticalAlignment.Center; name.MaxWidth = 220; name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, path);
            name.Margin = new Thickness(0, 0, 4, 0);
            _savedChip.Children.Add(name);
            _savedChip.Children.Add(Small(Glyph.Open, () => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { SetStatus("열지 못했습니다: " + e.Message); } }, "열기 (기본 사진 앱)"));
            _savedChip.Children.Add(Small(Glyph.Folder, () => ImportedFilesPanel.Reveal(path), "탐색기에서 보기"));
            bool png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            if (png) _savedChip.Children.Add(Small(Glyph.Copy, () => SetStatus(ImageClipboard.CopyPng(path) is string err ? "복사하지 못했습니다: " + err : "사진을 복사했어요 · 디스코드·메신저에 바로 붙여 넣을 수 있어요 (Ctrl+V)"), "이미지 복사 (붙여 넣기용)"));
            _savedChip.Children.Add(Small(Glyph.Close, HideSaved, "닫기"));
            _savedChip.IsVisible = true;
            _savedHide ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, __) => HideSaved());
            _savedHide.Stop(); _savedHide.Start();
        }

        void HideSaved() { _savedHide?.Stop(); _savedChip.IsVisible = false; _savedChip.Children.Clear(); }

        static Button Small(string glyph, Action act, string tip)
        {
            var b = Ui.IconButton(glyph, act, tip);
            b.Padding = new Thickness(5, 2); b.MinHeight = 0; b.Height = 22;
            return b;
        }
    }
}
