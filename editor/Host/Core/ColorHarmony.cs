// 색 조화 (머티리얼 페이지 "색 조화" 탭, 2026-10-24 사용자 요청):
//   · 기본 색 하나에서 색상환 규칙(유사·단색·삼각·보색·분할 보색·정사각형·직사각형·음영)으로 어울리는 색 묶음을 만든다.
//   · 이미지에서 대표 색 뽑기 (k-평균, 같은 입력이면 같은 결과).
// 색상환은 보통 HSV(빨강 0°, 노랑 60°, 초록 120° …). 값은 sRGB 그대로 — Unity 머티리얼 색도 감마 공간 값으로 저장된다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VrcProto.Host.Core
{
    public enum HarmonyRule { Analogous, Monochromatic, Triad, Complementary, SplitComplementary, Square, Rectangle, Shades }

    /// <summary>HSV: H 0~360(도), S·V 0~1.</summary>
    public readonly struct Hsv
    {
        public readonly double H, S, V;
        public Hsv(double h, double s, double v) { H = Wrap(h); S = Math.Clamp(s, 0, 1); V = Math.Clamp(v, 0, 1); }
        public static double Wrap(double h) { h %= 360; return h < 0 ? h + 360 : h; }
        public override string ToString() => $"({H:0.#}, {S:0.###}, {V:0.###})";
    }

    public static class ColorHarmony
    {
        public static readonly HarmonyRule[] Rules = (HarmonyRule[])Enum.GetValues(typeof(HarmonyRule));

        public static string Name(HarmonyRule r) => r switch
        {
            HarmonyRule.Analogous => "유사",
            HarmonyRule.Monochromatic => "단색",
            HarmonyRule.Triad => "삼각 (3색 대비)",
            HarmonyRule.Complementary => "보색",
            HarmonyRule.SplitComplementary => "분할 보색",
            HarmonyRule.Square => "정사각형",
            HarmonyRule.Rectangle => "직사각형",
            _ => "음영",
        };

        /// <summary>기본 색(맨 앞)과 규칙에 맞는 색들.</summary>
        public static Hsv[] Make(HarmonyRule rule, Hsv b)
        {
            Hsv At(double dh, double s = -1, double v = -1) => new Hsv(b.H + dh, s < 0 ? b.S : s, v < 0 ? b.V : v);
            switch (rule)
            {
                case HarmonyRule.Analogous: return new[] { At(0), At(-30), At(30), At(-60), At(60) };
                case HarmonyRule.Monochromatic:
                    return new[] { At(0), At(0, b.S * 0.55), At(0, -1, b.V * 0.65), At(0, b.S * 0.3, Math.Min(1, b.V + 0.25)), At(0, Math.Min(1, b.S * 1.3 + 0.1), b.V * 0.4) };
                case HarmonyRule.Triad: return new[] { At(0), At(120), At(240) };
                case HarmonyRule.Complementary: return new[] { At(0), At(180) };
                case HarmonyRule.SplitComplementary: return new[] { At(0), At(150), At(210) };
                case HarmonyRule.Square: return new[] { At(0), At(90), At(180), At(270) };
                case HarmonyRule.Rectangle: return new[] { At(0), At(60), At(180), At(240) };
                default: return new[] { At(0), At(0, -1, b.V * 0.8), At(0, -1, b.V * 0.6), At(0, -1, b.V * 0.4), At(0, -1, b.V * 0.2) };
            }
        }

        /// <summary>
        /// 색상환에서 i번째 색을 (h, s)로 끌었을 때의 새 기본 색: 규칙의 색상 차이와 채도 비율을 그대로 두고 묶음째 돈다 (밝기는 그대로).
        /// </summary>
        public static Hsv DragTo(HarmonyRule rule, Hsv b, int i, double h, double s)
        {
            var set = Make(rule, b);
            if (i <= 0 || i >= set.Length) return new Hsv(h, s, b.V);
            var m = set[i];
            double ratio = m.S > 1e-4 ? b.S / m.S : 1;
            return new Hsv(h - (m.H - b.H), m.S > 1e-4 ? s * ratio : s, b.V);
        }

        // ---------------- 변환 ----------------
        public static Hsv FromRgb(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = 0;
            if (d > 1e-9)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            return new Hsv(h, max > 1e-9 ? d / max : 0, max);
        }

        public static (double R, double G, double B) ToRgb(Hsv c)
        {
            double h = c.H / 60, x = c.V * c.S * (1 - Math.Abs(h % 2 - 1)), ch = c.V * c.S, m = c.V - ch;
            var (r, g, b) = (int)Math.Floor(h) switch
            {
                0 => (ch, x, 0d), 1 => (x, ch, 0d), 2 => (0d, ch, x), 3 => (0d, x, ch), 4 => (x, 0d, ch), _ => (ch, 0d, x),
            };
            return (r + m, g + m, b + m);
        }

        public static (byte R, byte G, byte B) ToBytes(Hsv c)
        {
            var (r, g, b) = ToRgb(c);
            static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
            return (B(r), B(g), B(b));
        }

        public static string Hex(Hsv c) { var (r, g, b) = ToBytes(c); return $"#{r:X2}{g:X2}{b:X2}"; }

        /// <summary>"#RRGGBB", "RRGGBB", "#RGB". 못 읽으면 null.</summary>
        public static Hsv? ParseHex(string s)
        {
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
            if (s.Length == 8) s = s.Substring(0, 6);   // 알파는 버린다
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
            return FromRgb(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
        }

        // ---------------- 이미지에서 대표 색 ----------------
        /// <summary>
        /// RGBA(위 줄부터) 픽셀의 대표 색 k개 (많이 쓰인 차례). 알파가 반 넘게 빠진 픽셀은 뺀다. 큰 이미지는 고르게 건너뛰어 많아야 약 2만 픽셀만 본다.
        /// k-평균: 처음 중심은 k-평균++를 고정 씨앗으로 (같은 이미지는 늘 같은 결과), 거리는 사람 눈에 맞춘 가중 RGB.
        /// </summary>
        public static List<(Hsv Color, double Share)> Dominant(byte[] rgba, int k = 6, int maxSamples = 20000)
        {
            int n = rgba.Length / 4;
            int step = Math.Max(1, n / maxSamples);
            var pts = new List<double[]>();
            for (int i = 0; i < n; i += step)
            {
                int o = i * 4;
                if (rgba[o + 3] < 128) continue;
                pts.Add(new double[] { rgba[o] / 255.0, rgba[o + 1] / 255.0, rgba[o + 2] / 255.0 });
            }
            var result = new List<(Hsv, double)>();
            if (pts.Count == 0) return result;
            static double D(double[] a, double[] b)
            {
                double dr = a[0] - b[0], dg = a[1] - b[1], db = a[2] - b[2], rm = (a[0] + b[0]) / 2;
                return (2 + rm) * dr * dr + 4 * dg * dg + (3 - rm) * db * db;
            }
            var rnd = new Random(12345);
            var centers = new List<double[]> { (double[])pts[pts.Count / 2].Clone() };
            var best = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) best[i] = D(pts[i], centers[0]);
            while (centers.Count < k)
            {
                double sum = best.Sum();
                if (sum < 1e-9) break;   // 남은 점이 모두 기존 중심과 같은 색
                double pick = rnd.NextDouble() * sum; int j = 0;
                for (; j < pts.Count - 1; j++) { pick -= best[j]; if (pick <= 0) break; }
                var c = (double[])pts[j].Clone();
                centers.Add(c);
                for (int i = 0; i < pts.Count; i++) best[i] = Math.Min(best[i], D(pts[i], c));
            }
            var owner = new int[pts.Count];
            var counts = new int[centers.Count];
            for (int iter = 0; iter < 12; iter++)
            {
                var sums = new double[centers.Count, 3];
                Array.Clear(counts, 0, counts.Length);
                bool moved = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    int bi = 0; double bd = double.MaxValue;
                    for (int c = 0; c < centers.Count; c++) { double d = D(pts[i], centers[c]); if (d < bd) { bd = d; bi = c; } }
                    if (owner[i] != bi) moved = true;
                    owner[i] = bi; counts[bi]++;
                    for (int ch = 0; ch < 3; ch++) sums[bi, ch] += pts[i][ch];
                }
                for (int c = 0; c < centers.Count; c++)
                    if (counts[c] > 0) for (int ch = 0; ch < 3; ch++) centers[c][ch] = sums[c, ch] / counts[c];
                if (!moved && iter > 0) break;
            }
            for (int c = 0; c < centers.Count; c++)
                if (counts[c] > 0) result.Add((FromRgb(centers[c][0], centers[c][1], centers[c][2]), counts[c] / (double)pts.Count));
            return result.OrderByDescending(x => x.Item2).ToList();
        }
    }
}
