// 이미지 파일 디코드 (2026-10-27): TGA · PSD(합성 이미지) · BMP → RGBA32. 렌더러(텍스처)와 에디터(에셋 창 썸네일·색 뽑기)가 같이 쓴다.
//   PNG·JPG는 각자 쓰던 디코더(렌더러 LoadImage·PngDecoder, 에디터 Avalonia)를 그대로 쓴다.
//   PSD: Photoshop이 파일 끝에 넣어 두는 합성 이미지만 읽는다 (Unity 임포터와 같음 — 레이어는 합치지 않음).
//     RGB·회색조·CMYK·인덱스, 8·16·32비트(부동소수는 선형 → sRGB), 무압축·RLE·ZIP. "호환성 최대화"를 끄고 저장한 PSD는 합성 이미지가 비어 있을 수 있다.
//   BMP: 1·4·8비트(팔레트) · 16·24·32비트, 무압축·BITFIELDS. 32비트인데 알파가 모두 0이면 불투명으로 본다.
//   TIF·EXR은 ImageDecode.Tif.cs·ImageDecode.Exr.cs (2026-11-01).
using System;
using System.IO;

namespace VrcProto.Protocol
{
    public static partial class ImageDecode
    {
        const long MaxPixels = 16384L * 16384L;

        /// <summary>이 디코더가 읽는 확장자인지 (소문자, 점 포함).</summary>
        public static bool Handles(string ext) => ext == ".tga" || ext == ".psd" || ext == ".bmp" || ext == ".tif" || ext == ".tiff" || ext == ".exr";

        /// <summary>RGBA32 픽셀. bottomUp이면 첫 줄이 아래 줄 (Unity 텍스처 순서), 아니면 위 줄부터.</summary>
        public static byte[] Decode(byte[] file, string ext, bool bottomUp, out int width, out int height)
        {
            switch (ext)
            {
                case ".tga": return Tga(file, bottomUp, out width, out height);
                case ".psd": return Psd(file, bottomUp, out width, out height);
                case ".bmp": return Bmp(file, bottomUp, out width, out height);
                case ".tif": case ".tiff": return Tif(file, bottomUp, out width, out height);
                case ".exr": return Exr(file, bottomUp, out width, out height);
                default: throw new InvalidDataException("읽을 수 없는 이미지 형식 " + ext);
            }
        }

        static byte[] Alloc(int w, int h)
        {
            if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) throw new InvalidDataException($"이미지 크기가 이상해요 ({w}×{h})");
            return new byte[w * h * 4];
        }

        static int Row(int yTop, int h, bool bottomUp) => bottomUp ? h - 1 - yTop : yTop;

        // ---------------- TGA (무압축·RLE, 트루컬러·회색조) ----------------
        static byte[] Tga(byte[] b, bool bottomUp, out int w, out int h)
        {
            if (b.Length < 18) throw new InvalidDataException("TGA 머리가 짧아요");
            int idLen = b[0], cmapType = b[1], type = b[2];
            w = b[12] | b[13] << 8; h = b[14] | b[15] << 8;
            int bpp = b[16], desc = b[17];
            if (cmapType != 0 || (type != 2 && type != 10 && type != 3 && type != 11)) throw new InvalidDataException("지원하지 않는 TGA 형식 " + type);
            int cmapLen = b[5] | b[6] << 8, cmapEntry = b[7];
            int p = 18 + idLen + cmapLen * ((cmapEntry + 7) / 8);
            int bytesPP = bpp / 8; bool gray = type == 3 || type == 11;
            if (bytesPP < 1 || bytesPP > 4) throw new InvalidDataException("지원하지 않는 TGA 비트 수 " + bpp);
            var px = Alloc(w, h);
            bool topOrigin = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0;
            int total = w * h, count = 0, W = w, H = h;
            byte r = 0, g = 0, bl = 0, a = 255;
            void Read(ref int q)
            {
                if (gray) { r = g = bl = b[q]; a = bytesPP == 2 ? b[q + 1] : (byte)255; }
                else if (bytesPP == 2) { int v = b[q] | b[q + 1] << 8; r = (byte)((v >> 10 & 31) * 255 / 31); g = (byte)((v >> 5 & 31) * 255 / 31); bl = (byte)((v & 31) * 255 / 31); a = 255; }
                else { r = b[q + 2]; g = b[q + 1]; bl = b[q]; a = bytesPP == 4 ? b[q + 3] : (byte)255; }
                q += bytesPP;
            }
            void Put()
            {
                int x = count % W, row = count / W;
                if (rightToLeft) x = W - 1 - x;
                int yTop = topOrigin ? row : H - 1 - row;
                int i = (Row(yTop, H, bottomUp) * W + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = bl; px[i + 3] = a;
                count++;
            }
            if (type == 2 || type == 3) while (count < total) { Read(ref p); Put(); }
            else
                while (count < total)
                {
                    int hdr = b[p++], n = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0) { Read(ref p); for (int i = 0; i < n && count < total; i++) Put(); }
                    else for (int i = 0; i < n && count < total; i++) { Read(ref p); Put(); }
                }
            return px;
        }

        // ---------------- PSD (합성 이미지) ----------------
        static int U16(byte[] b, int p) => b[p] << 8 | b[p + 1];
        static int I32(byte[] b, int p) => b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
        static long I64(byte[] b, int p) => (long)(uint)I32(b, p) << 32 | (uint)I32(b, p + 4);

        static byte[] Psd(byte[] b, bool bottomUp, out int w, out int h)
        {
            if (b.Length < 26 || b[0] != '8' || b[1] != 'B' || b[2] != 'P' || b[3] != 'S') throw new InvalidDataException("PSD 파일이 아니에요");
            int version = U16(b, 4);   // 1 = PSD, 2 = PSB
            if (version != 1 && version != 2) throw new InvalidDataException("지원하지 않는 PSD 버전 " + version);
            bool psb = version == 2;
            int channels = U16(b, 12);
            h = I32(b, 14); w = I32(b, 18);
            int depth = U16(b, 22), mode = U16(b, 24);
            if (depth != 8 && depth != 16 && depth != 32) throw new InvalidDataException($"{depth}비트 PSD는 못 읽어요 (8·16·32비트만)");
            if (mode != 1 && mode != 2 && mode != 3 && mode != 4) throw new InvalidDataException("지원하지 않는 PSD 색 모드 " + mode);
            int p = 26;
            int cmLen = I32(b, p); p += 4;
            int palette = p; p += cmLen;
            int resLen = I32(b, p); p += 4 + resLen;
            long layerLen = psb ? I64(b, p) : (uint)I32(b, p); p += psb ? 8 : 4;
            if (p + layerLen > b.Length) throw new InvalidDataException("PSD 파일이 잘렸어요");
            p += (int)layerLen;
            int comp = U16(b, p); p += 2;
            int bps = depth / 8, rowBytes = w * bps;
            // 쓸 채널: RGB 3 · CMYK 4 · 회색조·인덱스 1, 그다음 하나가 알파(있으면)
            int color = mode == 3 ? 3 : mode == 4 ? 4 : 1;
            int use = Math.Min(channels, color + 1);
            if (channels < color) throw new InvalidDataException("PSD 채널 수가 모자라요");
            var planes = new byte[use][];
            if (comp == 0)
            {
                for (int c = 0; c < use; c++) { planes[c] = new byte[rowBytes * h]; Copy(b, p + (long)c * rowBytes * h, planes[c]); }
            }
            else if (comp == 1)
            {
                // 줄마다 압축된 길이 표 (모든 채널 × 줄), 다음에 채널 순서대로 PackBits
                int lenSize = psb ? 4 : 2;
                long data = p + (long)channels * h * lenSize;
                int li = p;
                for (int c = 0; c < use; c++)
                {
                    planes[c] = new byte[rowBytes * h];
                    for (int y = 0; y < h; y++)
                    {
                        int len = psb ? I32(b, li) : U16(b, li); li += lenSize;
                        UnpackBits(b, (int)data, len, planes[c], y * rowBytes, rowBytes);
                        data += len;
                    }
                }
            }
            else if (comp == 2 || comp == 3)
            {
                // ZIP: 모든 채널을 이은 zlib 하나 (3 = 줄마다 예측값 차이)
                var raw = Inflate(b, p, b.Length - p, (long)channels * rowBytes * h);
                for (int c = 0; c < use; c++)
                {
                    planes[c] = new byte[rowBytes * h];
                    Buffer.BlockCopy(raw, c * rowBytes * h, planes[c], 0, planes[c].Length);
                    if (comp == 3) for (int y = 0; y < h; y++) Unpredict(planes[c], y * rowBytes, rowBytes, bps, bigEndian: true);
                }
            }
            else throw new InvalidDataException("지원하지 않는 PSD 압축 " + comp);
            if (depth == 32)
            {
                // 부동소수(선형) → 8비트 sRGB. 알파는 그대로 비율
                for (int c = 0; c < use; c++)
                {
                    var o8 = new byte[w * h];
                    bool alpha = c >= color;
                    for (int i = 0; i < o8.Length; i++)
                    {
                        float v = Float(planes[c], i * 4, bigEndian: true);
                        o8[i] = alpha ? Unit8(v) : Lin8(v);
                    }
                    planes[c] = o8;
                }
                bps = 1;
            }
            var px = Alloc(w, h);
            for (int y = 0; y < h; y++)
            {
                int o = Row(y, h, bottomUp) * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    int s = (y * w + x) * bps;   // 16비트는 큰 쪽 바이트만
                    byte V(int c) => planes[c][s];
                    byte r, g, bl;
                    if (mode == 3) { r = V(0); g = V(1); bl = V(2); }
                    else if (mode == 4)
                    {
                        // PSD의 CMYK는 반전된 값으로 저장된다 (255 = 잉크 없음)
                        int k = V(3);
                        r = (byte)(V(0) * k / 255); g = (byte)(V(1) * k / 255); bl = (byte)(V(2) * k / 255);
                    }
                    else if (mode == 2) { int i = V(0); r = b[palette + i]; g = b[palette + 256 + i]; bl = b[palette + 512 + i]; }
                    else r = g = bl = V(0);
                    px[o] = r; px[o + 1] = g; px[o + 2] = bl;
                    px[o + 3] = use > color ? V(color) : (byte)255;
                }
            }
            return px;
        }

        static void Copy(byte[] src, long at, byte[] dst)
        {
            if (at + dst.Length > src.Length) throw new InvalidDataException("PSD 파일이 잘렸어요");
            Buffer.BlockCopy(src, (int)at, dst, 0, dst.Length);
        }

        static void UnpackBits(byte[] src, int p, int len, byte[] dst, int o, int count)
        {
            int end = Math.Min(src.Length, p + len), stop = o + count;
            while (p < end && o < stop)
            {
                int n = (sbyte)src[p++];
                if (n >= 0) { for (int i = 0; i <= n && p < end && o < stop; i++) dst[o++] = src[p++]; }
                else if (n != -128) { byte v = src[p++]; for (int i = 0; i < 1 - n && o < stop; i++) dst[o++] = v; }
            }
        }

        // ---------------- BMP ----------------
        static int L16(byte[] b, int p) => b[p] | b[p + 1] << 8;
        static int L32(byte[] b, int p) => b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24;

        static byte[] Bmp(byte[] b, bool bottomUp, out int w, out int h)
        {
            if (b.Length < 54 || b[0] != 'B' || b[1] != 'M') throw new InvalidDataException("BMP 파일이 아니에요");
            int dataAt = L32(b, 10), hdr = L32(b, 14);
            if (hdr < 40) throw new InvalidDataException("오래된 BMP 형식(OS/2)은 못 읽어요");
            w = L32(b, 18); int rawH = L32(b, 22);
            bool topDown = rawH < 0; h = Math.Abs(rawH);
            int bpp = L16(b, 28), comp = L32(b, 30), used = L32(b, 46);
            if (comp != 0 && comp != 3 && comp != 6) throw new InvalidDataException("압축된 BMP는 못 읽어요 (" + comp + ")");
            uint mr, mg, mb, ma;
            if (comp == 0) { if (bpp == 16) { mr = 0x7C00; mg = 0x03E0; mb = 0x001F; ma = 0; } else { mr = 0xFF0000; mg = 0xFF00; mb = 0xFF; ma = bpp == 32 ? 0xFF000000 : 0; } }
            else
            {
                int m = hdr >= 52 ? 14 + 40 : 14 + hdr;   // V2 이상은 머리 안, 아니면 머리 바로 뒤
                mr = (uint)L32(b, m); mg = (uint)L32(b, m + 4); mb = (uint)L32(b, m + 8);
                ma = hdr >= 56 || comp == 6 ? (uint)L32(b, m + 12) : 0;
            }
            int palAt = 14 + hdr + (comp == 3 && hdr == 40 ? 12 : comp == 6 && hdr == 40 ? 16 : 0);
            int palCount = bpp <= 8 ? (used > 0 ? used : 1 << bpp) : 0;
            int stride = (w * bpp + 31) / 32 * 4;
            if (dataAt + (long)stride * h > b.Length) throw new InvalidDataException("BMP 파일이 잘렸어요");
            var px = Alloc(w, h);
            bool anyAlpha = false;
            for (int y = 0; y < h; y++)
            {
                int src = dataAt + y * stride;
                int yTop = topDown ? y : h - 1 - y;
                int o = Row(yTop, h, bottomUp) * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    if (bpp <= 8)
                    {
                        int bit = x * bpp, v = b[src + bit / 8] >> (8 - bpp - bit % 8) & ((1 << bpp) - 1);
                        if (v >= palCount) v = 0;
                        int q = palAt + v * 4;
                        px[o] = b[q + 2]; px[o + 1] = b[q + 1]; px[o + 2] = b[q]; px[o + 3] = 255;
                        continue;
                    }
                    uint pv = bpp == 16 ? (uint)L16(b, src + x * 2) : bpp == 24 ? (uint)(b[src + x * 3] | b[src + x * 3 + 1] << 8 | b[src + x * 3 + 2] << 16) : (uint)L32(b, src + x * 4);
                    px[o] = Mask(pv, mr); px[o + 1] = Mask(pv, mg); px[o + 2] = Mask(pv, mb);
                    px[o + 3] = ma != 0 ? Mask(pv, ma) : (byte)255;
                    if (ma != 0 && px[o + 3] != 0) anyAlpha = true;
                }
            }
            if (ma != 0 && !anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
        }

        static byte Mask(uint v, uint m)
        {
            if (m == 0) return 0;
            int shift = 0; while ((m >> shift & 1) == 0) shift++;
            uint max = m >> shift, x = (v & m) >> shift;
            return (byte)(x * 255 / max);
        }
    }
}
