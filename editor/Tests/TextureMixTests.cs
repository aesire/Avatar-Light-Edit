// 텍스처 합치기 (Host/Core/TextureMix.cs)
using System;
using System.Linq;
using VrcProto.Host.Core;

static partial class CoreTests
{
    static Rgba Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var img = new Rgba(w, h);
        for (int i = 0; i < img.Px.Length; i += 4) { img.Px[i] = r; img.Px[i + 1] = g; img.Px[i + 2] = b; img.Px[i + 3] = a; }
        return img;
    }

    static byte[] Pixel(Rgba img, int x, int y) => img.Px.Skip((y * img.W + x) * 4).Take(4).ToArray();
    static bool Near(byte[] p, int r, int g, int b, int a, int tol = 2) => Math.Abs(p[0] - r) <= tol && Math.Abs(p[1] - g) <= tol && Math.Abs(p[2] - b) <= tol && Math.Abs(p[3] - a) <= tol;
    static Rgba MixOne(Rgba bas, Rgba src, MixLayer l) => TextureMix.Compose(bas, new[] { (l, (Rgba?)src) });

    static void TextureMixBasics()
    {
        var bas = Solid(16, 16, 100, 100, 100);

        // 꽉 채운 불투명 레이어 = 레이어 그대로, 같은 크기면 픽셀이 정확히 같다
        var noisy = new Rgba(16, 16);
        for (int i = 0; i < noisy.Px.Length; i += 4) { noisy.Px[i] = (byte)(i * 7); noisy.Px[i + 1] = (byte)(i * 13); noisy.Px[i + 2] = (byte)(i * 31); noisy.Px[i + 3] = 255; }
        Check(MixOne(bas, noisy, new MixLayer()).Px.AsSpan().SequenceEqual(noisy.Px), "fill opaque = layer");
        Check(MixOne(bas, noisy, new MixLayer { Visible = false }).Px.AsSpan().SequenceEqual(bas.Px), "hidden layer ignored");
        Check(TextureMix.Compose(bas, new[] { (new MixLayer(), (Rgba?)null) }).Px.AsSpan().SequenceEqual(bas.Px), "missing image ignored");
        Check(bas.Px[0] == 100, "compose leaves the base image alone");

        // 섞는 방법 (바탕 100/255 ≈ 0.392, 레이어 200/255 ≈ 0.784)
        var top = Solid(16, 16, 200, 200, 200);
        byte Mode(string m, float op = 1) => MixOne(bas, top, new MixLayer { Mode = m, Opacity = op }).Px[0];
        Check(Math.Abs(Mode("normal") - 200) <= 1, "normal");
        Check(Math.Abs(Mode("normal", 0.5f) - 150) <= 1, "normal 50%");
        Check(Math.Abs(Mode("add") - 255) <= 1, "add clamps");
        Check(Math.Abs(Mode("multiply") - 78) <= 1, "multiply");
        Check(Math.Abs(Mode("screen") - 222) <= 1, "screen");
        Check(Math.Abs(Mode("overlay") - 157) <= 1, $"overlay {Mode("overlay")}");
        Check(Mode("softlight") > 100 && Mode("softlight") < Mode("overlay"), "softlight between base and overlay");

        // 투명 레이어는 바탕을 건드리지 않고, 숨은 색도 그대로 (Opaque 머티리얼이 쓴다)
        var hiddenBase = Solid(8, 8, 10, 20, 30, 0);
        var clear = Solid(8, 8, 200, 0, 0, 0);
        Check(MixOne(hiddenBase, clear, new MixLayer()).Px.AsSpan().SequenceEqual(hiddenBase.Px), "transparent layer keeps hidden color");
        // 투명한 바탕에 반투명 레이어: 색은 레이어 색, 알파는 레이어 알파
        Check(Near(Pixel(MixOne(hiddenBase, Solid(8, 8, 200, 100, 50, 128), new MixLayer()), 4, 4), 200, 100, 50, 128), "over transparent base");
        // 불투명 바탕 위 반투명 레이어: 알파는 그대로 불투명
        Check(Pixel(MixOne(bas, Solid(16, 16, 0, 0, 0, 128), new MixLayer()), 4, 4)[3] == 255, "opaque base stays opaque");

        // 자리: 왼쪽 위 1/4에 빨강 → 그 밖은 바이트까지 그대로
        var red = Solid(4, 4, 255, 0, 0);
        var placed = MixOne(bas, red, new MixLayer { X = 0.25f, Y = 0.25f, W = 0.5f, H = 0.5f });
        Check(Near(Pixel(placed, 2, 2), 255, 0, 0, 255), "placed inside");
        Check(Pixel(placed, 12, 12).SequenceEqual(new byte[] { 100, 100, 100, 255 }), "untouched outside (x,y)");
        Check(Pixel(placed, 12, 2).SequenceEqual(new byte[] { 100, 100, 100, 255 }), "untouched right side");
        Check(Pixel(placed, 2, 12).SequenceEqual(new byte[] { 100, 100, 100, 255 }), "y goes down");

        // 거울: 왼쪽에 놓으면 오른쪽에도, 그리고 그림이 뒤집혀 놓인다
        var half = new Rgba(2, 1, new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 });   // 왼쪽 빨강, 오른쪽 파랑
        var big = Solid(64, 64, 0, 0, 0);
        var mir = MixOne(big, half, new MixLayer { X = 0.25f, Y = 0.5f, W = 0.25f, H = 0.25f, Mirror = true });
        Check(Near(Pixel(mir, 10, 32), 255, 0, 0, 255, 5) && Near(Pixel(mir, 21, 32), 0, 0, 255, 255, 5), "left copy as drawn");
        Check(Near(Pixel(mir, 53, 32), 255, 0, 0, 255, 5) && Near(Pixel(mir, 42, 32), 0, 0, 255, 255, 5), "mirrored copy flipped");
        var flip = MixOne(big, half, new MixLayer { FlipX = true });
        Check(Near(Pixel(flip, 2, 32), 0, 0, 255, 255, 5) && Near(Pixel(flip, 61, 32), 255, 0, 0, 255, 5), "flipX");

        // 회전: 가로로 긴 막대를 90도 → 세로로 긴 막대
        var bar = Solid(32, 32, 0, 0, 0);
        var rot = MixOne(bar, Solid(4, 4, 255, 255, 255), new MixLayer { W = 0.75f, H = 0.125f, Rotation = 90 });
        Check(rot.Px[(5 * 32 + 16) * 4] > 200 && rot.Px[(16 * 32 + 5) * 4] == 0, "rotate 90");

        // 크게 줄여도 계단 없이 평균: 체크 무늬를 작게 → 회색
        var checker = new Rgba(256, 256);
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { int o = (y * 256 + x) * 4; byte v = (byte)((x + y) % 2 == 0 ? 255 : 0); checker.Px[o] = checker.Px[o + 1] = checker.Px[o + 2] = v; checker.Px[o + 3] = 255; }
        var small = MixOne(Solid(64, 64, 0, 0, 0), checker, new MixLayer { W = 0.25f, H = 0.25f });
        Check(Near(Pixel(small, 32, 32), 128, 128, 128, 255, 6), "minified checker averages");

        // 첫 자리: 같은 비율·절반 이상이면 꽉, 아니면 원래 픽셀 크기, 바탕보다 크면 안에 맞춘다
        var l1 = new MixLayer { W = 0, H = 0 }; TextureMix.PlaceDefault(l1, 2048, 2048, 1024, 1024); Check(l1.W == 1 && l1.H == 1, "same shape fills");
        var l2 = new MixLayer(); TextureMix.PlaceDefault(l2, 2048, 2048, 256, 256); Check(Math.Abs(l2.W - 0.125f) < 1e-4 && Math.Abs(l2.H - 0.125f) < 1e-4, "small keeps pixel size");
        var l3 = new MixLayer(); TextureMix.PlaceDefault(l3, 1024, 1024, 2048, 1024); Check(Math.Abs(l3.W - 1) < 1e-4 && Math.Abs(l3.H - 0.5f) < 1e-4, "wide fits inside");

        // 재료 왕복
        var layer = new MixLayer { Key = "k", File = "C:/a.png", Name = "a.png", Mode = "add", Opacity = 0.5f, X = 0.1f, Y = 0.2f, W = 0.3f, H = 0.4f, Rotation = 30, FlipX = true, Mirror = true, Visible = false };
        var recipe = TextureMix.RecipeToJson("base", "C:/b.png", new[] { layer });
        Eq(recipe["base"].AsString, "base", "recipe base");
        var back = TextureMix.LayersFromJson(VrcProto.Protocol.JNode.Parse(recipe.ToString())).Single();
        Check(back.Key == "k" && back.File == "C:/a.png" && back.Mode == "add" && back.Opacity == 0.5f && back.X == 0.1f && back.H == 0.4f && back.Rotation == 30 && back.FlipX && back.Mirror && !back.Visible, "layer round trip");
        Eq(MixLayer.FromJson(VrcProto.Protocol.JNode.Parse("{\"mode\":\"없는것\"}")).Mode, "normal", "unknown mode");
        Check(MixLayer.FromJson(VrcProto.Protocol.JNode.Parse("{}")).Visible, "visible by default");

        // 이름으로 찾기
        Check(TextureMix.NameLooksLike("eye", "Face_Eye_01.png") && TextureMix.NameLooksLike("eye", "瞳_blue.png") && TextureMix.NameLooksLike("eye", "hitomi.png"), "eye names");
        Check(!TextureMix.NameLooksLike("eye", "eyebrow.png") && !TextureMix.NameLooksLike("eye", "Monkey.png") && !TextureMix.NameLooksLike("eye", "눈썹.png"), "not eye");
        Check(TextureMix.NameLooksLike("face", "eyebrow.png"), "eyebrow is face");
        Check(TextureMix.NameLooksLike("effect", "Body_FX.png") && TextureMix.NameLooksLike("effect", "tattoo_red.png") && TextureMix.NameLooksLike("effect", "発光.png"), "effect names");
        Check(!TextureMix.NameLooksLike("effect", "Fox.png"), "fox is not fx");
        Check(TextureMix.NameLooksLike("", "anything.png"), "no kind = all");

        // 미리 보기 줄이기: 숨은 색을 지키고 큰 쪽을 maxSide에 맞춘다
        var sh = TextureMix.ShrinkBase(Solid(128, 64, 10, 20, 30, 0), 32);
        Check(sh.W == 32 && sh.H == 16 && Near(Pixel(sh, 3, 3), 10, 20, 30, 0, 0), "shrink keeps hidden color");
        Check(ReferenceEquals(TextureMix.ShrinkBase(bas, 32), bas), "small enough stays");
    }
}
