// 이미지 디코드 (Protocol/ImageDecode*.cs, 2026-11-01): PSD 32비트·ZIP · TIFF · EXR을 손으로 만든 작은 파일로 확인
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using VrcProto.Protocol;

static partial class CoreTests
{
    static byte[] ZlibOf(byte[] data)
    {
        using var m = new MemoryStream();
        using (var z = new ZLibStream(m, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
        return m.ToArray();
    }

    static void ImageDecodeMore()
    {
        foreach (var ext in new[] { ".tif", ".tiff", ".exr" }) Check(ImageDecode.Handles(ext), "handles " + ext);

        // PSD: 무압축 16·32비트, ZIP, ZIP 예측
        Same(ImageDecode.Decode(PsdMore(8, 2), ".psd", false, out var w, out var h), TopDown2x2, "psd zip");
        Eq(w, 2, "psd zip w"); Eq(h, 2, "psd zip h");
        Same(ImageDecode.Decode(PsdMore(8, 3), ".psd", false, out _, out _), TopDown2x2, "psd zip predict 8");
        Same(ImageDecode.Decode(PsdMore(16, 3), ".psd", false, out _, out _), TopDown2x2, "psd zip predict 16");
        Same(ImageDecode.Decode(PsdMore(32, 0), ".psd", false, out _, out _), TopDown2x2, "psd 32 raw");
        Same(ImageDecode.Decode(PsdMore(32, 3), ".psd", false, out _, out _), TopDown2x2, "psd 32 zip predict");

        // TIFF: 바이트 순서·압축·예측·줄 묶음
        var raw = (byte[])TopDown2x2.Clone();
        Same(ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 1, 1, raw, alpha: true), ".tif", false, out w, out h), TopDown2x2, "tif II raw");
        Eq(w, 2, "tif w"); Eq(h, 2, "tif h");
        Same(ImageDecode.Decode(Tiff(true, 2, 2, 4, 8, 2, 1, 1, raw, alpha: true), ".tiff", false, out _, out _), TopDown2x2, "tif MM raw");
        var up = ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 1, 1, raw, alpha: true), ".tif", true, out _, out _);
        Check(up[2] == 255 && up[0] == 0, "tif bottom-up first row is bottom (blue)");
        Same(ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 8, 1, ZlibOf(raw), alpha: true), ".tif", false, out _, out _), TopDown2x2, "tif deflate");
        var packed = new List<byte> { 15 }; packed.AddRange(raw);
        Same(ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 32773, 1, packed.ToArray(), alpha: true), ".tif", false, out _, out _), TopDown2x2, "tif packbits");
        var delta = (byte[])raw.Clone();
        for (int y = 0; y < 2; y++) for (int i = 7; i >= 4; i--) delta[y * 8 + i] = (byte)(raw[y * 8 + i] - raw[y * 8 + i - 4]);
        Same(ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 1, 2, delta, alpha: true), ".tif", false, out _, out _), TopDown2x2, "tif predictor 2");
        // ExtraSamples가 없거나 "미지정"(Photoshop 등 실제 파일)이어도 넷째 샘플은 알파 (2026-11-01 실제 Smoke.tif로 찾음)
        Same(ImageDecode.Decode(Tiff(false, 2, 2, 4, 8, 2, 1, 1, raw), ".tif", false, out _, out _), TopDown2x2, "tif alpha without extra tag");
        Same(ImageDecode.Decode(Tiff(true, 2, 2, 4, 8, 2, 1, 1, raw, extraUnspecified: true), ".tif", false, out _, out _), TopDown2x2, "tif alpha with extra 0");
        // 줄 묶음 두 개
        var strips = Tiff(false, 2, 2, 4, 8, 2, 1, 1, raw, alpha: true, rowsPerStrip: 1);
        Same(ImageDecode.Decode(strips, ".tif", false, out _, out _), TopDown2x2, "tif two strips");

        // LZW: 큰 그림으로 코드 길이가 9 → 12비트까지 늘고 표를 비우는 것까지
        int lw = 256, lh = 256;
        var big = new byte[lw * lh * 3];
        var rnd = new Random(7);
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 3 == 0 ? rnd.Next(256) : i / 300);
        var lzw = TiffLzw(big);
        var got = ImageDecode.Decode(Tiff(false, lw, lh, 3, 8, 2, 5, 1, lzw), ".tif", false, out _, out _);
        bool same = true;
        for (int i = 0, o = 0; i < big.Length && same; i += 3, o += 4) same = got[o] == big[i] && got[o + 1] == big[i + 1] && got[o + 2] == big[i + 2] && got[o + 3] == 255;
        Check(same, "tif lzw big image");
        // LZW: 같은 값이 이어져 아직 표에 없는 코드(KwKwK)가 나오는 경우
        var flatRgb = new byte[32 * 32 * 3];
        var gotFlat = ImageDecode.Decode(Tiff(false, 32, 32, 3, 8, 2, 5, 1, TiffLzw(flatRgb)), ".tif", false, out _, out _);
        Check(gotFlat[0] == 0 && gotFlat[gotFlat.Length - 2] == 0 && gotFlat[gotFlat.Length - 1] == 255, "tif lzw repeated bytes");

        // 회색조·반전·16비트·부동소수·팔레트
        Eq(ImageDecode.Decode(Tiff(false, 1, 1, 1, 8, 1, 1, 1, new byte[] { 200 }), ".tif", false, out _, out _)[1], (byte)200, "tif gray");
        Eq(ImageDecode.Decode(Tiff(false, 1, 1, 1, 8, 0, 1, 1, new byte[] { 200 }), ".tif", false, out _, out _)[1], (byte)55, "tif white-is-zero");
        Eq(ImageDecode.Decode(Tiff(false, 1, 1, 1, 16, 1, 1, 1, new byte[] { 0x34, 0xAB }), ".tif", false, out _, out _)[0], (byte)0xAB, "tif 16 II high byte");
        Eq(ImageDecode.Decode(Tiff(true, 1, 1, 1, 16, 1, 1, 1, new byte[] { 0xAB, 0x34 }), ".tif", false, out _, out _)[0], (byte)0xAB, "tif 16 MM high byte");
        var half = BitConverter.GetBytes(0.5f);
        Eq(ImageDecode.Decode(Tiff(false, 1, 1, 1, 32, 1, 1, 1, half, sampleFloat: true), ".tif", false, out _, out _)[0], (byte)188, "tif float linear → sRGB");
        var pal = new ushort[768]; pal[5] = 0xFF00; pal[256 + 5] = 0x8000; pal[512 + 5] = 0x1000;
        var pp = ImageDecode.Decode(Tiff(false, 1, 1, 1, 8, 3, 1, 1, new byte[] { 5 }, palette: pal), ".tif", false, out _, out _);
        Check(pp[0] == 0xFF && pp[1] == 0x80 && pp[2] == 0x10, "tif palette");

        // TIFF 못 읽는 것은 알린다
        Rejects(new byte[] { 1, 2, 3 }, ".tif", "not tiff");
        Rejects(Tiff(false, 1, 1, 1, 8, 1, 7, 1, new byte[] { 1 }), ".tif", "tiff jpeg compression");
        Rejects(Tiff(false, 1, 1, 4, 8, 5, 1, 1, new byte[4]), ".tif", "tiff cmyk");

        // EXR: 무압축·RLE·ZIPS·ZIP, 절반·부동소수, 회색
        Same(ImageDecode.Decode(Exr2x2(0, 1), ".exr", false, out w, out h), TopDown2x2, "exr raw half");
        Eq(w, 2, "exr w"); Eq(h, 2, "exr h");
        Same(ImageDecode.Decode(Exr2x2(0, 2), ".exr", false, out _, out _), TopDown2x2, "exr raw float");
        Same(ImageDecode.Decode(Exr2x2(1, 1), ".exr", false, out _, out _), TopDown2x2, "exr rle");
        Same(ImageDecode.Decode(Exr2x2(2, 1), ".exr", false, out _, out _), TopDown2x2, "exr zips");
        Same(ImageDecode.Decode(Exr2x2(3, 1), ".exr", false, out _, out _), TopDown2x2, "exr zip");
        var ub = ImageDecode.Decode(Exr2x2(0, 1), ".exr", true, out _, out _);
        Check(ub[2] == 255 && ub[0] == 0, "exr bottom-up first row is bottom (blue)");
        // 줄 16개 묶음을 넘는 그림: 묶음마다 같은 결과
        Same(ImageDecode.Decode(ExrGradient(3), ".exr", false, out _, out _), ImageDecode.Decode(ExrGradient(0), ".exr", false, out _, out _), "exr zip many blocks");
        Same(ImageDecode.Decode(ExrGradient(2), ".exr", false, out _, out _), ImageDecode.Decode(ExrGradient(0), ".exr", false, out _, out _), "exr zips many lines");
        Same(ImageDecode.Decode(ExrGradient(1), ".exr", false, out _, out _), ImageDecode.Decode(ExrGradient(0), ".exr", false, out _, out _), "exr rle many lines");
        // 회색 Y, 1 넘는 값은 잘림
        var yy = ImageDecode.Decode(Exr(1, 1, new[] { ("Y", 2) }, 0, (_, _, _) => 0.5f), ".exr", false, out _, out _);
        Check(yy[0] == 188 && yy[1] == 188 && yy[2] == 188 && yy[3] == 255, "exr gray Y");
        Eq(ImageDecode.Decode(Exr(1, 1, new[] { ("B", 2), ("G", 2), ("R", 2) }, 0, (_, _, _) => 9f), ".exr", false, out _, out _)[0], (byte)255, "exr over 1 clamps");
        // 못 읽는 압축
        Rejects(Exr(1, 1, new[] { ("B", 1), ("G", 1), ("R", 1) }, 4, (_, _, _) => 0f), ".exr", "exr piz");
    }

    static void Rejects(byte[] file, string ext, string what)
    {
        bool threw = false;
        try { ImageDecode.Decode(file, ext, false, out _, out _); } catch (InvalidDataException) { threw = true; }
        Check(threw, what + " → InvalidDataException");
    }

    // TopDown2x2와 같은 그림의 채널 값 (0~1, 선형으로 1·0이라 sRGB에서도 같다)
    static float Plane2x2(int x, int y, int c) => c == 3 ? TopDown2x2[(y * 2 + x) * 4 + 3] / 255f : TopDown2x2[(y * 2 + x) * 4 + c] / 255f;

    static byte[] PsdMore(int depth, int comp)
    {
        int bytes = depth / 8, row = 2 * bytes;
        var planes = new List<byte>();
        for (int c = 0; c < 4; c++)
        {
            for (int y = 0; y < 2; y++)
            {
                var line = new byte[row];
                for (int x = 0; x < 2; x++)
                {
                    if (depth == 32) { var f = BitConverter.GetBytes(Plane2x2(x, y, c)); for (int b = 0; b < 4; b++) line[x * 4 + b] = f[3 - b]; }
                    else line[x * bytes] = TopDown2x2[(y * 2 + x) * 4 + c];
                }
                if (comp == 3)
                {
                    if (depth == 8) for (int i = row - 1; i > 0; i--) line[i] -= line[i - 1];
                    else if (depth == 16)
                    {
                        int a = line[0] << 8 | line[1], b = line[2] << 8 | line[3], d = b - a;
                        line[2] = (byte)(d >> 8); line[3] = (byte)d;
                    }
                    else
                    {
                        // 바이트 평면으로 모은 뒤 앞 바이트와의 차이
                        var t = new byte[row];
                        for (int k = 0; k < 2; k++) for (int b = 0; b < 4; b++) t[b * 2 + k] = line[k * 4 + b];
                        for (int i = row - 1; i > 0; i--) t[i] -= t[i - 1];
                        line = t;
                    }
                }
                planes.AddRange(line);
            }
        }
        var data = comp == 0 ? planes.ToArray() : ZlibOf(planes.ToArray());
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        void U32(int v) { U16(v >> 16 & 0xFFFF); U16(v & 0xFFFF); }
        o.AddRange(new[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S' }); U16(1); o.AddRange(new byte[6]);
        U16(4); U32(2); U32(2); U16(depth); U16(3);
        U32(0); U32(0); U32(0);
        U16(comp);
        o.AddRange(data);
        return o.ToArray();
    }

    /// <summary>줄 묶음 TIFF. rowsPerStrip이 있으면 data(무압축)를 줄마다 나눈다.</summary>
    static byte[] Tiff(bool be, int w, int h, int spp, int bits, int photo, int comp, int pred, byte[] data, bool alpha = false, int rowsPerStrip = 0, bool sampleFloat = false, ushort[]? palette = null, bool extraUnspecified = false)
    {
        var o = new List<byte>();
        void U16(int v) { if (be) { o.Add((byte)(v >> 8)); o.Add((byte)v); } else { o.Add((byte)v); o.Add((byte)(v >> 8)); } }
        void U32(long v) { if (be) { U16((int)(v >> 16 & 0xFFFF)); U16((int)(v & 0xFFFF)); } else { U16((int)(v & 0xFFFF)); U16((int)(v >> 16 & 0xFFFF)); } }
        o.Add((byte)(be ? 'M' : 'I')); o.Add((byte)(be ? 'M' : 'I')); U16(42); U32(0);   // IFD 자리는 뒤에 고친다
        // 부동소수·16비트 값은 받은 바이트 순서 그대로 (호출한 쪽이 맞춰 준다)
        var stripAt = new List<long>(); var stripLen = new List<long>();
        if (rowsPerStrip > 0)
        {
            int rowBytes = w * spp * bits / 8;
            for (int y = 0; y < h; y += rowsPerStrip)
            {
                stripAt.Add(o.Count); stripLen.Add(rowBytes * rowsPerStrip);
                for (int i = 0; i < rowBytes * rowsPerStrip; i++) o.Add(data[y * rowBytes + i]);
            }
        }
        else { stripAt.Add(o.Count); stripLen.Add(data.Length); o.AddRange(data); }
        if (o.Count % 2 == 1) o.Add(0);
        long Arr(int size, IList<long> v)
        {
            long at = o.Count;
            foreach (var x in v) { if (size == 2) U16((int)x); else U32(x); }
            return at;
        }
        var tags = new SortedDictionary<int, (int Type, long Count, long Value)>();
        void One(int tag, long v, int type = 4) => tags[tag] = (type, 1, v);
        void Many(int tag, int type, IList<long> v) { if (v.Count == 1) One(tag, v[0], type); else tags[tag] = (type, v.Count, Arr(type == 3 ? 2 : 4, v)); }
        One(256, w); One(257, h);
        var bl = new long[spp]; Array.Fill(bl, bits);
        Many(258, 3, bl);
        One(259, comp, 3); One(262, photo, 3);
        Many(273, 4, stripAt);
        One(277, spp, 3);
        One(278, rowsPerStrip > 0 ? rowsPerStrip : h);
        Many(279, 4, stripLen);
        if (pred != 1) One(317, pred, 3);
        if (palette != null) { var pl = new long[palette.Length]; for (int i = 0; i < pl.Length; i++) pl[i] = palette[i]; Many(320, 3, pl); }
        if (alpha) One(338, 2, 3);
        if (extraUnspecified) One(338, 0, 3);
        if (sampleFloat) One(339, 3, 3);
        if (o.Count % 2 == 1) o.Add(0);
        int ifd = o.Count;
        U16(tags.Count);
        foreach (var kv in tags)
        {
            U16(kv.Key); U16(kv.Value.Type); U32(kv.Value.Count);
            if (kv.Value.Type == 3 && kv.Value.Count == 1) { U16((int)kv.Value.Value); U16(0); }
            else U32(kv.Value.Value);
        }
        U32(0);
        var all = o.ToArray();
        if (be) { all[4] = (byte)(ifd >> 24); all[5] = (byte)(ifd >> 16); all[6] = (byte)(ifd >> 8); all[7] = (byte)ifd; }
        else { all[4] = (byte)ifd; all[5] = (byte)(ifd >> 8); all[6] = (byte)(ifd >> 16); all[7] = (byte)(ifd >> 24); }
        return all;
    }

    /// <summary>TIFF 6.0 규격 그대로의 LZW 인코더: 표에 511·1023·2047을 넣은 뒤 코드 길이를 늘리고, 4094를 넣으면 Clear.</summary>
    static byte[] TiffLzw(byte[] data)
    {
        var o = new List<byte>();
        int acc = 0, have = 0, width = 9, next = 258;
        void Put(int code)
        {
            acc = acc << width | code; have += width;
            while (have >= 8) { o.Add((byte)(acc >> (have - 8))); have -= 8; }
            acc &= (1 << have) - 1;
        }
        var table = new Dictionary<(int, byte), int>();
        Put(256);
        int cur = -1;
        foreach (var b in data)
        {
            if (cur < 0) { cur = b; continue; }
            if (table.TryGetValue((cur, b), out var c)) { cur = c; continue; }
            Put(cur);
            table[(cur, b)] = next++;
            if (next == 512) width = 10; else if (next == 1024) width = 11; else if (next == 2048) width = 12;
            if (next == 4095) { Put(256); table.Clear(); next = 258; width = 9; }
            cur = b;
        }
        if (cur >= 0) Put(cur);
        // 끝: 마지막 코드도 표를 한 칸 늘린 것으로 친다
        next++;
        if (next == 512) width = 10; else if (next == 1024) width = 11; else if (next == 2048) width = 12;
        Put(257);
        if (have > 0) o.Add((byte)(acc << (8 - have)));
        return o.ToArray();
    }

    static byte[] Exr2x2(int comp, int type)
        => Exr(2, 2, new[] { ("A", type), ("B", type), ("G", type), ("R", type) }, comp, (x, y, name) => Plane2x2(x, y, name == "R" ? 0 : name == "G" ? 1 : name == "B" ? 2 : 3));

    static byte[] ExrGradient(int comp)
        => Exr(32, 40, new[] { ("B", 1), ("G", 1), ("R", 1) }, comp, (x, y, name) => name == "R" ? x / 31f : name == "G" ? y / 39f : 0.25f);

    /// <summary>줄 저장 EXR (comp: 0 무압축, 1 RLE, 2 ZIPS, 3 ZIP, 4 = PIZ 표시만). channels는 이름 순서.</summary>
    static byte[] Exr(int w, int h, (string Name, int Type)[] channels, int comp, Func<int, int, string, float> value)
    {
        var o = new List<byte> { 0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0 };
        void I32(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
        void Str(string s) { o.AddRange(System.Text.Encoding.UTF8.GetBytes(s)); o.Add(0); }
        Str("channels"); Str("chlist");
        int sizeAt = o.Count; I32(0);
        foreach (var (name, type) in channels) { Str(name); I32(type); I32(0); I32(1); I32(1); }
        o.Add(0);
        int chSize = o.Count - sizeAt - 4;
        o[sizeAt] = (byte)chSize; o[sizeAt + 1] = (byte)(chSize >> 8);
        Str("compression"); Str("compression"); I32(1); o.Add((byte)comp);
        Str("dataWindow"); Str("box2i"); I32(16); I32(0); I32(0); I32(w - 1); I32(h - 1);
        o.Add(0);
        int lines = comp == 3 ? 16 : 1, blocks = (h + lines - 1) / lines;
        int table = o.Count;
        o.AddRange(new byte[blocks * 8]);
        for (int k = 0; k < blocks; k++)
        {
            int y0 = k * lines, rows = Math.Min(lines, h - y0);
            var raw = new List<byte>();
            for (int y = y0; y < y0 + rows; y++)
                foreach (var (name, type) in channels)
                    for (int x = 0; x < w; x++)
                    {
                        float v = value(x, y, name);
                        raw.AddRange(type == 1 ? BitConverter.GetBytes((Half)v) : BitConverter.GetBytes(v));
                    }
            var body = comp == 0 || comp == 4 ? raw.ToArray() : comp == 1 ? ExrRleEncode(ExrFilter(raw.ToArray())) : ZlibOf(ExrFilter(raw.ToArray()));
            if (body.Length >= raw.Count) body = raw.ToArray();   // OpenEXR처럼: 줄지 않으면 원본 그대로
            int at = o.Count;
            for (int b = 0; b < 8; b++) o[table + k * 8 + b] = b < 4 ? (byte)(at >> (b * 8)) : (byte)0;
            I32(y0); I32(body.Length);
            o.AddRange(body);
        }
        return o.ToArray();
    }

    static byte[] ExrFilter(byte[] raw)
    {
        var t = new byte[raw.Length];
        int half = (raw.Length + 1) / 2;
        for (int i = 0; i < raw.Length; i++) t[i % 2 == 0 ? i / 2 : half + i / 2] = raw[i];
        for (int i = t.Length - 1; i > 0; i--) t[i] = (byte)(t[i] - t[i - 1] + 128);
        return t;
    }

    static byte[] ExrRleEncode(byte[] t)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < t.Length)
        {
            int run = 1;
            while (i + run < t.Length && t[i + run] == t[i] && run < 127) run++;
            if (run >= 3) { o.Add((byte)(run - 1)); o.Add(t[i]); i += run; continue; }
            int start = i;
            while (i < t.Length && i - start < 127 && !(i + 2 < t.Length && t[i] == t[i + 1] && t[i] == t[i + 2])) i++;
            o.Add((byte)(sbyte)-(i - start));
            for (int k = start; k < i; k++) o.Add(t[k]);
        }
        return o.ToArray();
    }
}
