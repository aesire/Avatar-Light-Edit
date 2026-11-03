// 넓으면 여러 열 (2026-10-22, 사용자 요청): 줄들을 왼쪽→오른쪽, 위→아래 차례로 칸에 채운다.
// 열 수 = 너비 / MinColumnWidth (1 ~ MaxColumns). 한 줄 높이는 그 줄에서 가장 높은 칸.
using System;
using Avalonia;
using Avalonia.Controls;

namespace VrcProto.AvHost
{
    public sealed class FlowColumns : Panel
    {
        public double MinColumnWidth { get; set; } = 340;
        public int MaxColumns { get; set; } = 3;
        public double ColumnGap { get; set; } = 28;

        int ColumnsFor(double width)
        {
            if (double.IsInfinity(width) || width <= 0) return 1;
            int n = (int)((width + ColumnGap) / (MinColumnWidth + ColumnGap));
            return Math.Clamp(n, 1, MaxColumns);
        }

        double CellWidth(double width, int cols) => cols == 1 ? width : (width - ColumnGap * (cols - 1)) / cols;

        protected override Size MeasureOverride(Size available)
        {
            int cols = ColumnsFor(available.Width);
            double cw = CellWidth(available.Width, cols), h = 0, rowH = 0, maxW = 0;
            int i = 0;
            foreach (var c in Children)
            {
                if (!c.IsVisible) continue;
                c.Measure(new Size(cw, double.PositiveInfinity));
                if (c.DesiredSize.Height <= 0) continue;   // 속이 숨은 칸 (예: 쉬운 모드에서 숨긴 줄을 감싼 Border)은 자리를 차지하지 않는다
                rowH = Math.Max(rowH, c.DesiredSize.Height);
                maxW = Math.Max(maxW, c.DesiredSize.Width);
                if (++i % cols == 0) { h += rowH; rowH = 0; }
            }
            h += rowH;
            return new Size(double.IsInfinity(available.Width) ? maxW : available.Width, h);
        }

        protected override Size ArrangeOverride(Size final)
        {
            int cols = ColumnsFor(final.Width);
            double cw = CellWidth(final.Width, cols), y = 0;
            var row = new Control[cols];
            int n = 0;
            void Flush()
            {
                double rh = 0;
                for (int k = 0; k < n; k++) rh = Math.Max(rh, row[k].DesiredSize.Height);
                for (int k = 0; k < n; k++) row[k].Arrange(new Rect(k * (cw + ColumnGap), y, cw, rh));
                y += rh; n = 0;
            }
            foreach (var c in Children)
            {
                if (!c.IsVisible || c.DesiredSize.Height <= 0) { c.Arrange(default); continue; }
                row[n++] = c;
                if (n == cols) Flush();
            }
            if (n > 0) Flush();
            return final;
        }
    }
}
