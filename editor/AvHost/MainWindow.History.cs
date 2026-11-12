// 메인 창: 원본과 비교 보기 · 작업 기록 창 (2026-11-12, 사용자 "1,2 …하고")
//   원본과 비교 (O · 미리보기 도구 줄 "원본"): 문서는 그대로 두고 렌더러에만 원본 값을 보낸다 (Host/Core/RenderSync.cs ShowOriginal).
//     값을 바꾸면 저절로 풀린다. 편집 화면·실행 취소 기록·저장 상태는 바뀌지 않는다.
//   작업 기록 (Ctrl+H · 실행 취소 단추 옆): 실행 취소 단계 이름을 차례로 보여 주고, 누른 시점으로 한 번에 간다 (Document.JumpTo).
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        Button? _origBtn;

        void ToggleOriginal()
        {
            if (_sync == null || _ctx.Doc == null) { SetStatus("아바타를 먼저 열어 주세요"); return; }
            if (!_sync.ShowingOriginal && _sync.LookChanges == 0) { SetStatus("아직 바꾼 것이 없어 원본과 같아요"); return; }
            _sync.ShowOriginal(!_sync.ShowingOriginal);
            RefreshOriginalButton();
            SetStatus(_sync.ShowingOriginal ? "원본을 보는 중이에요 (O를 다시 누르면 고친 모습)" : "고친 모습으로 돌아왔어요");
        }

        void RefreshOriginalButton()
        {
            if (_origBtn == null) return;
            if (_sync?.ShowingOriginal == true) _origBtn.Bg(Ui.KSelect); else _origBtn.Background = Brushes.Transparent;
        }

        // ---------------- 작업 기록 ----------------
        Window? _historyWindow;
        StackPanel? _historyList;
        ScrollViewer? _historyScroll;

        void ShowHistory()
        {
            if (_historyWindow != null) { _historyWindow.Activate(); return; }
            var p = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
            var head = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(Ui.Text("작업 기록", 17, FontWeight.SemiBold));
            head.Children.Add(Ui.Wrap("줄을 누르면 그 시점으로 돌아가요. 아래쪽 흐린 줄은 되돌린 것이라 누르면 다시 적용돼요. 되돌린 뒤 새로 바꾸면 흐린 줄은 사라져요.", Ui.KText2, 12));
            DockPanel.SetDock(head, Dock.Top);
            p.Children.Add(head);
            _historyList = new StackPanel { Spacing = 1 };
            _historyScroll = new ScrollViewer { Content = _historyList };
            p.Children.Add(_historyScroll);
            var win = _historyWindow = new Window
            {
                Title = "작업 기록", Width = 380, Height = 560, MinWidth = 300, MinHeight = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = FontFamily, FontSize = 14, Content = p,
            };
            win.Bg(Ui.KCard);
            win.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape) { win.Close(); return; }
                // 이 창에서도 Ctrl+Z · Ctrl+Y가 듣게
                bool ctrl = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0, shift = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0;
                if (ctrl && e.Key == Avalonia.Input.Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
                else if (ctrl && e.Key == Avalonia.Input.Key.Y) { Redo(); e.Handled = true; }
            };
            win.Closed += (_, __) => { _historyWindow = null; _historyList = null; _historyScroll = null; };
            RefreshHistory();
            win.Show(this);
        }

        /// <summary>기록 창이 열려 있으면 다시 채운다 (값이 바뀔 때마다 — 창이 닫혀 있으면 아무것도 하지 않는다).</summary>
        void RefreshHistory()
        {
            var list = _historyList;
            if (list == null) return;
            list.Children.Clear();
            var d = _ctx.Doc;
            if (d == null) { list.Children.Add(Ui.Wrap("열린 아바타가 없어요.", Ui.KText3, 13)); return; }
            var labels = d.HistoryLabels;
            int now = d.UndoCount;
            Control? current = null;
            Control Row(int count, string text, bool applied)
            {
                bool isNow = count == now;
                var num = Ui.Text(count == 0 ? "" : count.ToString(), 12, FontWeight.Normal, Ui.KText3);
                num.Width = 34; num.VerticalAlignment = VerticalAlignment.Center;
                var name = Ui.Text(text, 13, isNow ? FontWeight.SemiBold : FontWeight.Normal, applied ? Ui.KText : Ui.KText3);
                name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
                var row = new DockPanel();
                DockPanel.SetDock(num, Dock.Left);
                row.Children.Add(num);
                if (isNow)
                {
                    var here = Ui.Text("지금", 11, FontWeight.SemiBold, Ui.KAccentText);
                    here.VerticalAlignment = VerticalAlignment.Center; here.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(here, Dock.Right);
                    row.Children.Add(here);
                }
                row.Children.Add(name);
                var b = new Button
                {
                    Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(8, 5), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                };
                if (isNow) b.Bg(Ui.KSelect);
                ToolTip.SetTip(b, text);
                b.Click += (_, __) => JumpHistory(count);
                if (isNow) current = b;
                return b;
            }
            list.Children.Add(Row(0, "처음 상태 (연 직후)", true));
            for (int i = 0; i < labels.Count; i++)
                list.Children.Add(Row(i + 1, string.IsNullOrEmpty(labels[i]) ? "변경" : labels[i], i < now));
            if (labels.Count == 0) list.Children.Add(Ui.Wrap("아직 바꾼 것이 없어요.", Ui.KText3, 12));
            // 지금 줄이 보이게
            var cur = current;
            if (cur != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => cur.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        }

        void JumpHistory(int count)
        {
            var d = _ctx.Doc; if (d == null || count == d.UndoCount) return;
            int before = d.UndoCount;
            d.JumpTo(count);
            RefreshUndoButtons(); RefreshHistory();
            int moved = Math.Abs(d.UndoCount - before);
            SetStatus(d.UndoCount < before ? $"{moved}단계 되돌렸습니다 (다시 하기 Ctrl+Y)" : $"{moved}단계 다시 했습니다");
        }
    }
}
