// 스티커 규격 이미지 (Host/Core/Sticker.cs)
using System;
using VrcProto.Host.Core;

static partial class CoreTests
{
    static Rgba Blob(int w, int h, int x0, int y0, int bw, int bh, byte r = 200, byte g = 100, byte b = 50)
    {
        var img = new Rgba(w, h);
        for (int y = y0; y < y0 + bh; y++)
            for (int x = x0; x < x0 + bw; x++) { int o = (y * w + x) * 4; img.Px[o] = r; img.Px[o + 1] = g; img.Px[o + 2] = b; img.Px[o + 3] = 255; }
        return img;
    }

    static void StickerImages()
    {
        // PNG 왕복
        var src = Blob(40, 30, 10, 5, 8, 20);
        src.Px[3] = 77; src.Px[0] = 9;
        var back = Sticker.DecodePng(Sticker.EncodePng(src));
        Check(back != null && back.W == 40 && back.H == 30, "png size");
        Check(back != null && back.Px.AsSpan().SequenceEqual(src.Px), "png pixels round trip");

        // 보이는 범위·맞추기: 세로로 긴 덩어리는 높이에 맞고 가운데
        Eq(Sticker.OpaqueBounds(src)?.ToString(), "(0, 0, 18, 25)", "bounds (0,0 픽셀 알파 77 포함)");
        var big = Blob(400, 400, 100, 50, 100, 300);
        var fit = Sticker.Fit(big, 370, 320, 10, 0)!;
        var fb = Sticker.OpaqueBounds(fit)!.Value;
        Check(fb.H >= 299 && fb.H <= 300 && fb.Y >= 10 && fb.Y + fb.H <= 310, $"fit height 300 inside margin: {fb}");
        Check(Math.Abs(fb.X + fb.W / 2.0 - 185) <= 1, $"fit centered: {fb}");
        Check(Math.Abs(fb.W - 100) <= 1, $"fit keeps aspect 1:3: {fb}");
        Eq(fit.Px[((160) * 370 + 185) * 4], (byte)200, "fit color kept (premultiplied average)");

        // 테두리: 덩어리 둘레 6px가 흰색으로, 바깥은 투명
        Sticker.Outline(fit, 6, 255, 255, 255);
        int cy = 160, edge = fb.X;
        Eq(fit.A(edge - 3, cy), (byte)255, "outline covers 3px outside");
        Eq(fit.Px[(cy * 370 + edge - 3) * 4 + 1], (byte)255, "outline is white");
        Eq(fit.A(edge - 9, cy), (byte)0, "beyond outline transparent");
        Eq(fit.Px[(cy * 370 + edge + 2) * 4], (byte)200, "inside unchanged");

        // 보정: 채도 0 = 회색, 기본값은 그대로
        var gray = Blob(4, 4, 0, 0, 4, 4);
        Sticker.Adjust(gray, 0, 1, 0);
        Check(gray.Px[0] == gray.Px[1] && gray.Px[1] == gray.Px[2], "saturation 0 → gray");
        var same = Blob(4, 4, 0, 0, 4, 4);
        Sticker.Adjust(same, 0, 1, 1);
        Eq(same.Px[0], (byte)200, "neutral adjust untouched");

        // 올리기 썸네일: 가운데만 남고 크기가 맞으며, 투명한 곳은 흰색
        var wide = new Rgba(400, 100);
        for (int x = 0; x < 400; x++) for (int y = 0; y < 100; y++)
        {
            int o = (y * 400 + x) * 4;
            bool mid = x >= 134 && x < 266;   // 가운데 133칸이 남는다 (양쪽 끝 1칸은 섞일 수 있어 안쪽만 칠함과 비교)
            wide.Px[o] = (byte)(mid ? 0 : 255); wide.Px[o + 2] = (byte)(mid ? 255 : 0); wide.Px[o + 3] = 255;
        }
        var cw = Sticker.Cover(wide, 120, 90);
        Check(cw.W == 120 && cw.H == 90, "cover size");
        Eq(cw.Px[(45 * 120 + 60) * 4], (byte)0, "cover keeps center, red gone");
        Eq(cw.Px[(45 * 120 + 60) * 4 + 2], (byte)255, "cover keeps center blue");
        Eq(cw.Px[(45 * 120 + 3) * 4 + 2], (byte)255, "cover cuts left side (wide)");
        var tall = new Rgba(100, 400);   // 위쪽 절반은 투명, 아래는 검정 불투명
        for (int y = 200; y < 400; y++) for (int x = 0; x < 100; x++) tall.Px[(y * 100 + x) * 4 + 3] = 255;
        var ct = Sticker.Cover(tall, 120, 90);
        Check(ct.W == 120 && ct.H == 90, "cover size tall");
        Eq(ct.Px[(5 * 120 + 60) * 4], (byte)255, "transparent → white");
        Eq(ct.Px[(5 * 120 + 60) * 4 + 3], (byte)255, "cover opaque");
        Eq(ct.Px[(85 * 120 + 60) * 4], (byte)0, "opaque part stays");

        // 규격 만들기: Twitch는 세 장, 빈 이미지는 없음
        var list = Sticker.Make(big, new Sticker.Options { Spec = "twitch", OutlineWidth = 4 });
        Eq(string.Join(",", list.ConvertAll(x => x.Image.W + "x" + x.Image.H)), "112x112,56x56,28x28", "twitch sizes");
        Eq(Sticker.Make(new Rgba(10, 10), new Sticker.Options()).Count, 0, "empty shot → nothing");
        Eq(Sticker.Make(big, new Sticker.Options { Spec = "line" })[0].Image.W, 370, "line width");
    }
}
