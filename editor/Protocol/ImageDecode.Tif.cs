// TIFF 디코드 (2026-11-01): 줄 묶음(strip)·픽셀 이어 쓰기(planar 1). 무압축·LZW·Deflate·PackBits, 예측 1·2·3.
//   회색조·RGB·팔레트, 8·16비트 정수·32비트 부동소수(선형 → sRGB), 알파(추가 샘플). 16비트는 큰 쪽 바이트만.
//   타일·CMYK·YCbCr(JPEG 압축 포함)은 못 읽는다고 알린다. ZIP·예측 공용 함수도 여기에 둔다 (PSD·EXR이 같이 쓴다).
using System;
using System.IO;
using System.IO.Compression;

namespace VrcProto.Protocol
{
    public static partial class ImageDecode
    {
        /// <summary>zlib 스트림을 푼다. expect 바이트가 안 나오면 오류.</summary>
        static byte[] Inflate(byte[] b, int p, int len, long expect)
        {
            if (expect <= 0 || expect > int.MaxValue) throw new InvalidDataException("압축을 푼 크기가 이상해요");
            if (len < 2) throw new InvalidDataException("압축 자료가 잘렸어요");
            var o = new byte[expect];
            using var z = new DeflateStream(new MemoryStream(b, p + 2, len - 2), CompressionMode.Decompress);
            int at = 0;
            while (at < o.Length)
            {
                int n = z.Read(o, at, o.Length - at);
                if (n <= 0) throw new InvalidDataException("압축을 푼 자료가 모자라요");
                at += n;
            }
            return o;
        }

        /// <summary>줄 하나의 예측 되돌리기: 8·16비트는 값 차이, 32비트(부동소수)는 바이트 평면 차이 → 값 순서로.</summary>
        static void Unpredict(byte[] d, int o, int rowBytes, int bytesPerSample, bool bigEndian, int samples = 1)
        {
            if (bytesPerSample == 1) { for (int i = samples; i < rowBytes; i++) d[o + i] += d[o + i - samples]; return; }
            if (bytesPerSample == 2)
            {
                int hi = bigEndian ? 0 : 1, lo = 1 - hi, st = samples * 2;
                for (int i = st; i + 1 < rowBytes; i += 2)
                {
                    int v = (d[o + i + hi] << 8 | d[o + i + lo]) + (d[o + i - st + hi] << 8 | d[o + i - st + lo]);
                    d[o + i + hi] = (byte)(v >> 8); d[o + i + lo] = (byte)v;
                }
                return;
            }
            for (int i = samples; i < rowBytes; i++) d[o + i] += d[o + i - samples];
            int count = rowBytes / 4;
            var t = new byte[rowBytes];
            for (int k = 0; k < count; k++)
                for (int b = 0; b < 4; b++)
                    t[k * 4 + (bigEndian ? b : 3 - b)] = d[o + b * count + k];
            Buffer.BlockCopy(t, 0, d, o, rowBytes);
        }

        static float Float(byte[] b, int p, bool bigEndian)
        {
            int v = bigEndian ? b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3] : b[p + 3] << 24 | b[p + 2] << 16 | b[p + 1] << 8 | b[p];
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        /// <summary>선형 값 → 8비트 sRGB (1 넘는 값은 잘린다).</summary>
        static byte Lin8(float v)
        {
            if (!(v > 0)) return 0;
            if (v >= 1) return 255;
            double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)(s * 255 + 0.5);
        }

        /// <summary>0~1 값 → 8비트 그대로 (알파).</summary>
        static byte Unit8(float v) => !(v > 0) ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

        static byte[] Tif(byte[] f, bool bottomUp, out int w, out int h)
        {
            if (f.Length < 8) throw new InvalidDataException("TIFF 머리가 짧아요");
            bool be = f[0] == 'M' && f[1] == 'M';
            if (!be && !(f[0] == 'I' && f[1] == 'I')) throw new InvalidDataException("TIFF 파일이 아니에요");
            int U2(int p) => be ? f[p] << 8 | f[p + 1] : f[p] | f[p + 1] << 8;
            long U4(int p) => be ? (uint)(f[p] << 24 | f[p + 1] << 16 | f[p + 2] << 8 | f[p + 3]) : (uint)(f[p] | f[p + 1] << 8 | f[p + 2] << 16 | f[p + 3] << 24);
            if (U2(2) != 42) throw new InvalidDataException("지원하지 않는 TIFF 형식 (BigTIFF)");
            int ifd = (int)U4(4);
            int entries = U2(ifd);
            long[] Vals(int e)
            {
                int type = U2(e + 2); long n = U4(e + 4);
                int sz = type == 3 ? 2 : type == 4 ? 4 : type == 1 ? 1 : 0;
                if (sz == 0 || n > 1 << 24) return new long[0];
                int at = n * sz <= 4 ? e + 8 : (int)U4(e + 8);
                var v = new long[n];
                for (int i = 0; i < n; i++) v[i] = sz == 2 ? U2(at + i * 2) : sz == 4 ? U4(at + i * 4) : f[at + i];
                return v;
            }
            long[] bits = { 1 }, offs = new long[0], sizes = new long[0], cmap = new long[0], format = { 1 };
            int comp = 1, photo = -1, spp = 1, planar = 1, pred = 1; long rps = long.MaxValue; w = h = 0;
            for (int i = 0; i < entries; i++)
            {
                int e = ifd + 2 + i * 12; var v = Vals(e);
                long first = v.Length > 0 ? v[0] : 0;
                switch (U2(e))
                {
                    case 256: w = (int)first; break;
                    case 257: h = (int)first; break;
                    case 258: bits = v; break;
                    case 259: comp = (int)first; break;
                    case 262: photo = (int)first; break;
                    case 273: offs = v; break;
                    case 277: spp = (int)first; break;
                    case 278: rps = first; break;
                    case 279: sizes = v; break;
                    case 284: planar = (int)first; break;
                    case 317: pred = (int)first; break;
                    case 320: cmap = v; break;
                    case 322: throw new InvalidDataException("타일로 저장한 TIFF는 못 읽어요");
                    case 339: format = v; break;
                }
            }
            var px = Alloc(w, h);
            int depth = (int)bits[0]; bool isFloat = format.Length > 0 && format[0] == 3;
            if (planar != 1) throw new InvalidDataException("채널을 나눠 저장한 TIFF는 못 읽어요");
            if (photo != 0 && photo != 1 && photo != 2 && photo != 3) throw new InvalidDataException("지원하지 않는 TIFF 색 방식 " + photo);
            if (isFloat ? depth != 32 : depth != 8 && depth != 16) throw new InvalidDataException($"{depth}비트 TIFF는 못 읽어요");
            if (offs.Length == 0 || offs.Length != sizes.Length) throw new InvalidDataException("TIFF 줄 묶음 표가 이상해요");
            int bytes = depth / 8, rowBytes = checked(w * spp * bytes);
            var raw = new byte[checked(rowBytes * h)];
            if (rps <= 0 || rps > h) rps = h;
            for (int s = 0; s < offs.Length; s++)
            {
                int y0 = (int)(s * rps);
                if (y0 >= h) break;
                int rows = Math.Min((int)rps, h - y0), want = rows * rowBytes, at = (int)offs[s], len = (int)sizes[s];
                if (at < 0 || len < 0 || at + len > f.Length) throw new InvalidDataException("TIFF 파일이 잘렸어요");
                switch (comp)
                {
                    case 1: Buffer.BlockCopy(f, at, raw, y0 * rowBytes, Math.Min(want, len)); break;
                    case 5: Lzw(f, at, len, raw, y0 * rowBytes, want); break;
                    case 8: case 32946: Buffer.BlockCopy(Inflate(f, at, len, want), 0, raw, y0 * rowBytes, want); break;
                    case 32773: UnpackBits(f, at, len, raw, y0 * rowBytes, want); break;
                    default: throw new InvalidDataException("지원하지 않는 TIFF 압축 " + comp);
                }
            }
            if (pred == 2 || pred == 3) for (int y = 0; y < h; y++) Unpredict(raw, y * rowBytes, rowBytes, bytes, be, spp);
            int color = photo == 2 ? 3 : 1;
            if (spp < color) throw new InvalidDataException("TIFF 샘플 수가 모자라요");
            bool hasAlpha = spp > color;   // 추가 샘플의 첫 칸. 실제 파일 다수가 "미지정"(ExtraSamples 0)으로 알파를 적어 값은 가리지 않는다
            byte Sample(int at, bool linearColor)
            {
                if (bytes == 1) return raw[at];
                if (bytes == 2) return raw[at + (be ? 0 : 1)];
                float v = Float(raw, at, be);
                return linearColor ? Lin8(v) : Unit8(v);
            }
            for (int y = 0; y < h; y++)
            {
                int o = Row(y, h, bottomUp) * w * 4, s = y * rowBytes;
                for (int x = 0; x < w; x++, o += 4, s += spp * bytes)
                {
                    if (photo == 2) { px[o] = Sample(s, true); px[o + 1] = Sample(s + bytes, true); px[o + 2] = Sample(s + bytes * 2, true); }
                    else if (photo == 3)
                    {
                        if (cmap.Length < 768 || bytes != 1) throw new InvalidDataException("TIFF 팔레트가 이상해요");
                        int i = raw[s];
                        px[o] = (byte)(cmap[i] >> 8); px[o + 1] = (byte)(cmap[256 + i] >> 8); px[o + 2] = (byte)(cmap[512 + i] >> 8);
                    }
                    else
                    {
                        byte g = Sample(s, true);
                        px[o] = px[o + 1] = px[o + 2] = photo == 0 ? (byte)(255 - g) : g;
                    }
                    px[o + 3] = hasAlpha ? Sample(s + color * bytes, false) : (byte)255;
                }
            }
            return px;
        }

        /// <summary>TIFF LZW (앞 비트부터, 표가 찰 때 한 칸 먼저 코드 길이를 늘림).</summary>
        static void Lzw(byte[] src, int p, int len, byte[] dst, int o, int count)
        {
            const int Clear = 256, End = 257;
            var prefix = new short[4097]; var suffix = new byte[4097]; var length = new short[4097];
            for (int i = 0; i < 256; i++) { suffix[i] = (byte)i; length[i] = 1; prefix[i] = -1; }
            int next = 258, width = 9, prev = -1, stop = o + count, end = p + len;
            int acc = 0, have = 0;
            while (o < stop)
            {
                while (have < width && p < end) { acc = acc << 8 | src[p++]; have += 8; }
                if (have < width) return;
                int code = acc >> (have - width) & ((1 << width) - 1);
                have -= width; acc &= (1 << have) - 1;
                if (code == End) return;
                if (code == Clear) { next = 258; width = 9; prev = -1; continue; }
                int cur = code;
                if (code > next || (code == next && prev < 0)) throw new InvalidDataException("TIFF LZW 자료가 이상해요");
                int n = code < next ? length[code] : length[prev] + 1;
                int write = o + n - 1;
                if (code == next) { if (write < stop) dst[write] = First(prev); write--; cur = prev; }
                while (cur >= 0)
                {
                    if (write < stop) dst[write] = suffix[cur];
                    write--; cur = prefix[cur];
                }
                if (prev >= 0 && next < 4096)
                {
                    prefix[next] = (short)prev; length[next] = (short)(length[prev] + 1);
                    suffix[next] = code < next ? First(code) : First(prev);
                    next++;
                }
                o += n; prev = code;
                if (next + 1 >= 1 << width && width < 12) width++;
            }
            byte First(int c) { while (prefix[c] >= 0) c = prefix[c]; return suffix[c]; }
        }
    }
}
