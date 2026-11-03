// 이미지 디코드 (Protocol/ImageDecode.cs, 2026-10-27): TGA · PSD 합성 이미지 · BMP를 손으로 만든 작은 파일로 확인
using System;
using System.Collections.Generic;
using VrcProto.Protocol;

static partial class CoreTests
{
    // 2×2 그림: 위 줄 빨강·초록, 아래 줄 파랑·반투명 흰색
    static readonly byte[] TopDown2x2 = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128 };

    static void ImageDecodeBasics()
    {
        // TGA 무압축 32비트, 기본 아래→위
        var tga = new List<byte> { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 32, 8 };
        foreach (var (r, g, b, a) in new[] { (0, 0, 255, 255), (255, 255, 255, 128), (255, 0, 0, 255), (0, 255, 0, 255) }) tga.AddRange(new[] { (byte)b, (byte)g, (byte)r, (byte)a });
        Same(ImageDecode.Decode(tga.ToArray(), ".tga", false, out var w, out var h), TopDown2x2, "tga top-down");
        Eq(w, 2, "tga w"); Eq(h, 2, "tga h");
        var up = ImageDecode.Decode(tga.ToArray(), ".tga", true, out _, out _);
        Check(up[0] == 0 && up[2] == 255, "tga bottom-up first row is bottom (blue)");

        // BMP 32비트 BI_RGB, 아래→위 (높이 양수)
        var bmp = new List<byte>();
        void L16(int v) { bmp.Add((byte)v); bmp.Add((byte)(v >> 8)); }
        void L32(int v) { L16(v & 0xFFFF); L16(v >> 16 & 0xFFFF); }
        bmp.Add((byte)'B'); bmp.Add((byte)'M'); L32(54 + 16); L32(0); L32(54);
        L32(40); L32(2); L32(2); L16(1); L16(32); L32(0); L32(16); L32(0); L32(0); L32(0); L32(0);
        foreach (var (r, g, b, a) in new[] { (0, 0, 255, 255), (255, 255, 255, 128), (255, 0, 0, 255), (0, 255, 0, 255) }) bmp.AddRange(new[] { (byte)b, (byte)g, (byte)r, (byte)a });
        Same(ImageDecode.Decode(bmp.ToArray(), ".bmp", false, out _, out _), TopDown2x2, "bmp 32");
        // 알파가 모두 0인 32비트 BMP는 불투명
        var flat = bmp.ToArray(); for (int i = 54 + 3; i < flat.Length; i += 4) flat[i] = 0;
        Eq(ImageDecode.Decode(flat, ".bmp", false, out _, out _)[3], (byte)255, "bmp zero alpha → opaque");

        // PSD RGB+알파 8비트, 무압축과 RLE
        Same(ImageDecode.Decode(Psd(false), ".psd", false, out w, out h), TopDown2x2, "psd raw");
        Eq(w, 2, "psd w");
        Same(ImageDecode.Decode(Psd(true), ".psd", false, out _, out _), TopDown2x2, "psd rle");

        bool threw = false;
        try { ImageDecode.Decode(new byte[] { 1, 2, 3 }, ".psd", false, out _, out _); } catch (System.IO.InvalidDataException) { threw = true; }
        Check(threw, "not psd → InvalidDataException");
        Check(ImageDecode.Handles(".psd") && !ImageDecode.Handles(".png"), "handles");
    }

    static byte[] Psd(bool rle)
    {
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        void U32(int v) { U16(v >> 16 & 0xFFFF); U16(v & 0xFFFF); }
        o.AddRange(new[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S' }); U16(1); o.AddRange(new byte[6]);
        U16(4); U32(2); U32(2); U16(8); U16(3);
        U32(0); U32(0); U32(0);   // 색 모드 · 리소스 · 레이어 없음
        U16(rle ? 1 : 0);
        var planes = new byte[4][];
        for (int c = 0; c < 4; c++) { planes[c] = new byte[4]; for (int i = 0; i < 4; i++) planes[c][i] = TopDown2x2[i * 4 + c]; }
        if (!rle) { foreach (var p in planes) o.AddRange(p); return o.ToArray(); }
        // 줄마다 "리터럴 2개" PackBits (1바이트 머리 + 2) → 길이 3
        for (int i = 0; i < 4 * 2; i++) U16(3);
        foreach (var p in planes) for (int y = 0; y < 2; y++) { o.Add(1); o.Add(p[y * 2]); o.Add(p[y * 2 + 1]); }
        return o.ToArray();
    }

    static void Same(byte[] got, byte[] want, string what) => Check(got.AsSpan().SequenceEqual(want), what + ": " + string.Join(",", got));
}
