// 제목 줄 (2026-11-03, 사용자 "일부 언어의 특정 창 크기에서 화면이 깨짐"): 제목 + 오른쪽 버튼.
// DockPanel 자리에 그대로 쓴다 — DockPanel.SetDock(x, Dock.Right)인 자식이 오른쪽 칸, 나머지 첫 자식이 제목.
// 한 줄에 다 들어가면 DockPanel처럼 놓고, 모자라면(영어·일본어처럼 글이 길고 패널이 좁을 때) 오른쪽 칸을 제목 아래 줄로 내린다.
// 예전 DockPanel은 버튼이 자리를 먼저 차지해 제목 위에 겹쳐 그려졌다.
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace VrcProto.AvHost
{
    public sealed class TitleRow : Panel
    {
        public double RowGap { get; set; } = 2;
        double _titleW, _rightW;   // 줄이지 않은 너비

        Control? Right => Children.FirstOrDefault(c => c.IsVisible && DockPanel.GetDock(c) == Dock.Right);
        Control? Title => Children.FirstOrDefault(c => c.IsVisible && DockPanel.GetDock(c) != Dock.Right);

        bool Stacked(double width) => !double.IsInfinity(width) && _titleW + _rightW > width;

        protected override Size MeasureOverride(Size available)
        {
            Control? title = Title, right = Right;
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            title?.Measure(inf); right?.Measure(inf);
            _titleW = title?.DesiredSize.Width ?? 0; _rightW = right?.DesiredSize.Width ?? 0;
            if (Stacked(available.Width))
            {
                var col = new Size(available.Width, double.PositiveInfinity);
                title?.Measure(col); right?.Measure(col);   // 오른쪽 칸이 WrapPanel이면 여기서 줄을 바꾼다
                double th = title?.DesiredSize.Height ?? 0, rh = right?.DesiredSize.Height ?? 0;
                return new Size(available.Width, th + rh + (th > 0 && rh > 0 ? RowGap : 0));
            }
            return new Size(_titleW + _rightW, Math.Max(title?.DesiredSize.Height ?? 0, right?.DesiredSize.Height ?? 0));
        }

        protected override Size ArrangeOverride(Size final)
        {
            Control? title = Title, right = Right;
            foreach (var c in Children) if (c != title && c != right) c.Arrange(default);
            if (Stacked(final.Width))
            {
                double th = title?.DesiredSize.Height ?? 0;
                title?.Arrange(new Rect(0, 0, final.Width, th));
                // 아래 줄은 왼쪽부터 원래 너비로 (늘이지 않음)
                if (right != null) right.Arrange(new Rect(0, th + (th > 0 ? RowGap : 0), Math.Min(right.DesiredSize.Width, final.Width), right.DesiredSize.Height));
                return final;
            }
            double rw = Math.Min(_rightW, final.Width);
            title?.Arrange(new Rect(0, 0, Math.Max(0, final.Width - rw), final.Height));
            right?.Arrange(new Rect(final.Width - rw, 0, rw, final.Height));
            return final;
        }
    }
}
