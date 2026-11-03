// 색 조화 (Host/Core/ColorHarmony.cs)
using System;
using System.Linq;
using VrcProto.Host.Core;

static partial class CoreTests
{
    static void ColorHarmonyBasics()
    {
        // 변환 왕복
        Eq(ColorHarmony.Hex(ColorHarmony.ParseHex("#FF8000")!.Value), "#FF8000", "hex round trip");
        Eq(ColorHarmony.Hex(ColorHarmony.ParseHex("0af")!.Value), "#00AAFF", "short hex");
        Eq(ColorHarmony.ParseHex("zz") == null, true, "bad hex");
        var red = ColorHarmony.FromRgb(1, 0, 0);
        Eq(red.H, 0d, "red hue"); Eq(red.S, 1d, "red sat");
        Eq(Math.Round(ColorHarmony.FromRgb(1, 0, 1).H), 300d, "magenta hue (음수 각도 감기)");

        // 규칙
        var b = new Hsv(30, 0.8, 0.9);
        var tri = ColorHarmony.Make(HarmonyRule.Triad, b);
        Eq(string.Join(",", tri.Select(c => Math.Round(c.H))), "30,150,270", "triad hues");
        Eq(Math.Round(ColorHarmony.Make(HarmonyRule.Complementary, new Hsv(300, 1, 1))[1].H), 120d, "complement wraps");
        Eq(ColorHarmony.Make(HarmonyRule.Square, b).Length, 4, "square count");
        Check(ColorHarmony.Make(HarmonyRule.Shades, b).All(c => Math.Abs(c.H - 30) < 1e-9 && Math.Abs(c.S - 0.8) < 1e-9), "shades keep hue/sat");

        // 묶음째 돌리기: 삼각의 두 번째 색을 200°로 끌면 기본 색은 80°
        var moved = ColorHarmony.DragTo(HarmonyRule.Triad, b, 1, 200, 0.5);
        Eq(Math.Round(moved.H), 80d, "drag rotates set"); Eq(moved.S, 0.5, "drag sets sat"); Eq(moved.V, 0.9, "drag keeps value");

        // 대표 색: 빨강 3/4 · 파랑 1/4 · 투명 픽셀은 빠진다
        var px = new byte[400 * 4];
        for (int i = 0; i < 400; i++)
        {
            int o = i * 4;
            if (i < 300) { px[o] = 255; px[o + 3] = 255; }
            else if (i < 380) { px[o + 2] = 255; px[o + 3] = 255; }
            else { px[o + 1] = 255; px[o + 3] = 0; }   // 투명 초록
        }
        var dom = ColorHarmony.Dominant(px, 4);
        Eq(dom.Count, 2, "dominant count (같은 색만 남으면 더 만들지 않음)");
        Eq(ColorHarmony.Hex(dom[0].Color), "#FF0000", "dominant first = red");
        Eq(ColorHarmony.Hex(dom[1].Color), "#0000FF", "dominant second = blue");
        Check(Math.Abs(dom[0].Share - 300 / 380.0) < 1e-9, "dominant share");
    }
}
