// 화면 배치 두 가지 (2026-10-21, 사용자 요청: 새 배치와 기존 배치를 둘 다 두고 고른다). 설정 창 "화면 배치".
//   classic  기존: 사이드바(아이콘 + 이름) · 작업 창 · 미리보기가 띄엄띄엄 놓인 둥근 카드
//   clean    새: Obsidian처럼 판을 가는 선으로 이어 붙인다 — 왼쪽 아이콘 막대(아이콘 아래 작은 이름) | 작업 창 | 미리보기.
//            둥근 모서리·바깥 여백을 없애 화면을 넓게 쓰고, 판 사이는 1px 선. 페이지 제목은 조금 작게.
//            이해하기 쉬움은 그대로: 아이콘 아래 이름, 페이지 제목·탭·도움말, 툴팁의 단축키.
// 컨트롤은 옮기지 않고 모양(여백·모서리·테두리·너비)만 바꾼다 → 언제든 되돌릴 수 있고 Unity 자식 창을 다시 붙이지 않는다.
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        static readonly List<Control> PageHeads = new List<Control>();
        Border? _statusBar;
        bool Clean => _settings.LayoutStyle == "clean";
        double SplitWidth => Clean ? 0 : 12;

        void ApplyLayoutStyle()
        {
            bool clean = Clean;
            Classes.Set("clean", clean);
            _body.Margin = clean ? new Thickness(0) : new Thickness(0, 6, 0, 12);   // 오른쪽 틈은 세밀 조정 막대(접힌 모양)가 맡는다
            _rail?.SetClean(clean);
            // 플랫: 판 사이에 틈 없이 (분할선 열 0px, 잡는 자리는 양옆 3px씩 겹쳐서)
            _splitter.Margin = clean ? new Thickness(-3, 0, -3, 0) : new Thickness(0);
            _splitter.ZIndex = clean ? 100 : 0;
            _side.Ribbon = clean;
            // 아이콘 막대 오른쪽 선
            _sideLine ??= new Border { Width = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, IsHitTestVisible = false }.Bg(Ui.KDivider);
            if (clean && !_body.Children.Contains(_sideLine)) { _body.Children.Add(_sideLine.At(0)); }
            else if (!clean) _body.Children.Remove(_sideLine);

            // 작업 창: 둥근 카드 ↔ 선으로 이어 붙인 판
            _paneBorder.CornerRadius = clean ? new CornerRadius(0) : new CornerRadius(10);
            _paneBorder.BorderThickness = clean ? (_settings.PaneOnRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0)) : new Thickness(1);
            if (clean) _paneBorder.Stroke(Ui.KDivider); else _paneBorder.Stroke(Ui.KCardStroke);
            // 미리보기
            if (_preview is Border pv)
            {
                pv.CornerRadius = clean ? new CornerRadius(0) : new CornerRadius(10);
                pv.BorderThickness = clean ? new Thickness(0) : new Thickness(1);
                if (clean) pv.Bg(Ui.KBase); else pv.Bg(Ui.KPanel);
            }
            if (_viewFrame != null) _viewFrame.Margin = clean ? new Thickness(0) : new Thickness(8, 0, 8, 0);
            if (_toolBar != null) { _toolBar.CornerRadius = clean ? new CornerRadius(0) : new CornerRadius(10, 10, 0, 0); _toolBar.Margin = clean ? new Thickness(0) : new Thickness(0, 0, 0, 8); }
            // 플랫: 미리보기 왼쪽(작업 창 선 바로 옆)이 빡빡해 보이지 않게 도구 모음·안내 줄 왼쪽 여백을 조금 더
            if (_toolBar?.Child is Control tb) tb.Margin = clean ? new Thickness(18, 6, 8, 6) : new Thickness(10, 6, 6, 6);
            if (_hintRow != null) _hintRow.Margin = clean ? new Thickness(18, 4, 8, 6) : new Thickness(14, 4, 8, 6);
            // 페이지 제목: 조금 작게, 여백 줄이기
            foreach (var h in PageHeads)
            {
                h.Margin = clean ? new Thickness(20, 14, 20, 8) : new Thickness(24, 18, 24, 12);
                var t = (h as TextBlock) ?? h.GetLogicalChildrenTexts().FirstOrDefault();
                if (t != null) t.FontSize = clean ? 18 : 22;
            }
            if (_statusBar != null) _statusBar.Stroke(clean ? Ui.KDivider : Ui.KCardStroke);
            // 분할선 열 너비 (작업 창 위치 다시 계산)
            ApplyPaneSide();
            FitToolsLater();
        }

        Border? _sideLine;

        /// <summary>다이얼 색을 바꾼 뒤 다이얼·세밀 조정 막대를 다시 그린다 (직접 그리는 컨트롤이라)</summary>
        void InvalidateDials()
        {
            foreach (var root in new Visual[] { this }.Concat(_floats.Windows))
                foreach (var v in root.GetVisualDescendants()) if (v is Dial or FineBar) v.InvalidateVisual();
        }
    }

    static class LayoutExt
    {
        /// <summary>페이지 제목(Titled: 글자 + ⓘ)에서 제목 글자를 찾는다</summary>
        public static IEnumerable<TextBlock> GetLogicalChildrenTexts(this Control c)
            => c is Panel p ? p.Children.OfType<TextBlock>() : Enumerable.Empty<TextBlock>();
    }
}
