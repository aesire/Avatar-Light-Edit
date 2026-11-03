// 스티커·이모지 규격 이미지 (스튜디오 "스티커 만들기"): 렌더러가 찍은 투명 PNG를 받아
//   1. 보정 (밝기·대비·채도 — 일반 촬영에도 쓴다)
//   2. 다듬기: 알파가 있는 부분만 남기고 잘라 (Trim) 규격 안쪽(여백·테두리 두께를 뺀 칸)에 비율 그대로 맞춘다 (면적 평균, 미리 곱한 알파)
//   3. 테두리: 아바타 둘레에 흰색·검정 테두리 (유클리드 거리 변환으로 두께를 재고 가장자리는 부드럽게)
//   4. PNG로 저장 (RGBA 8비트). 용량 제한이 있는 규격(Discord)은 넘으면 알려 준다.
// 규격 (2026-10 기준 각 서비스 안내):
//   LINE 스티커 370×320 이하·여백 10px · 메인 240×240 · 대화방 탭 96×74 / 카카오톡 이모티콘 360×360 /
//   Discord 스티커 320×320 512KB 이하 · 이모지 128×128 256KB 이하 / Twitch 이모트 112·56·28 정사각 세 장 / Telegram 스티커 512×512
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace VrcProto.Host.Core
{
    /// <summary>RGBA 8비트 이미지 (위 줄부터, 미리 곱하지 않은 알파).</summary>
    public sealed class Rgba
    {
        public readonly int W, H;
        public readonly byte[] Px;
        public Rgba(int w, int h, byte[]? px = null) { W = w; H = h; Px = px ?? new byte[w * h * 4]; }
        public byte A(int x, int y) => Px[(y * W + x) * 4 + 3];
    }

    public static class Sticker
    {
        public sealed class Spec
        {
            public string Key = "", Label = "";
            public int W, H, Margin;
            public int MaxKB;                       // 0 = 제한 없음
            public int[] Extra = Array.Empty<int>(); // 같은 비율로 더 만들 정사각 크기 (Twitch 56·28)
        }

        public static readonly Spec[] Specs =
        {
            new Spec { Key = "line", Label = "LINE 스티커 (370×320)", W = 370, H = 320, Margin = 10 },
            new Spec { Key = "lineMain", Label = "LINE 메인 이미지 (240×240)", W = 240, H = 240, Margin = 10 },
            new Spec { Key = "lineTab", Label = "LINE 대화방 탭 (96×74)", W = 96, H = 74, Margin = 2 },
            new Spec { Key = "kakao", Label = "카카오톡 이모티콘 (360×360)", W = 360, H = 360, Margin = 10 },
            new Spec { Key = "discordSticker", Label = "Discord 스티커 (320×320)", W = 320, H = 320, Margin = 6, MaxKB = 512 },
            new Spec { Key = "discordEmoji", Label = "Discord 이모지 (128×128)", W = 128, H = 128, Margin = 2, MaxKB = 256 },
            new Spec { Key = "twitch", Label = "Twitch 이모트 (112·56·28)", W = 112, H = 112, Margin = 2, Extra = new[] { 56, 28 } },
            new Spec { Key = "telegram", Label = "Telegram 스티커 (512×512)", W = 512, H = 512, Margin = 8 },
        };

        public static Spec Find(string key) => Array.Find(Specs, s => s.Key == key) ?? Specs[0];

        // ---------------- 보정 ----------------
        /// <summary>밝기 −1~1 (0 = 그대로), 대비·채도 0~2 (1 = 그대로). 알파는 그대로.</summary>
        public static bool IsNeutral(double brightness, double contrast, double saturation)
            => Math.Abs(brightness) < 1e-3 && Math.Abs(contrast - 1) < 1e-3 && Math.Abs(saturation - 1) < 1e-3;

        public static void Adjust(Rgba img, double brightness, double contrast, double saturation)
        {
            if (IsNeutral(brightness, contrast, saturation)) return;
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0 + brightness;
                v = (v - 0.5) * contrast + 0.5;
                lut[i] = Clamp8(v * 255);
            }
            var p = img.Px;
            for (int i = 0; i < p.Length; i += 4)
            {
                double r = lut[p[i]], g = lut[p[i + 1]], b = lut[p[i + 2]];
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                p[i] = Clamp8(l + (r - l) * saturation); p[i + 1] = Clamp8(l + (g - l) * saturation); p[i + 2] = Clamp8(l + (b - l) * saturation);
            }
        }

        static byte Clamp8(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));

        // ---------------- 잘라 맞추기 ----------------
        /// <summary>알파가 있는 범위 (없으면 null).</summary>
        public static (int X, int Y, int W, int H)? OpaqueBounds(Rgba img, byte threshold = 8)
        {
            int x0 = img.W, y0 = img.H, x1 = -1, y1 = -1;
            for (int y = 0; y < img.H; y++)
                for (int x = 0; x < img.W; x++)
                    if (img.A(x, y) > threshold) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            return x1 < 0 ? null : (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        /// <summary>
        /// 원본의 (sx, sy, sw, sh) 영역을 dw×dh로 다시 뽑는다 (면적 평균 — 줄일 때 계단이 생기지 않게, 늘릴 때도 같은 식으로 가장 가까운 칸).
        /// 미리 곱한 알파로 평균해 투명한 가장자리에 검은 테가 생기지 않게 한다.
        /// </summary>
        public static Rgba Resample(Rgba src, int sx, int sy, int sw, int sh, int dw, int dh)
        {
            var dst = new Rgba(dw, dh);
            double fx = (double)sw / dw, fy = (double)sh / dh;
            for (int y = 0; y < dh; y++)
            {
                double ya = sy + y * fy, yb = ya + fy;
                for (int x = 0; x < dw; x++)
                {
                    double xa = sx + x * fx, xb = xa + fx;
                    double r = 0, g = 0, b = 0, a = 0, wsum = 0;
                    for (int yy = (int)Math.Floor(ya); yy < Math.Ceiling(yb); yy++)
                    {
                        double wy = Math.Min(yb, yy + 1) - Math.Max(ya, yy);
                        if (wy <= 0 || yy < 0 || yy >= src.H) continue;
                        for (int xx = (int)Math.Floor(xa); xx < Math.Ceiling(xb); xx++)
                        {
                            double wx = Math.Min(xb, xx + 1) - Math.Max(xa, xx);
                            if (wx <= 0 || xx < 0 || xx >= src.W) continue;
                            double w = wx * wy; int i = (yy * src.W + xx) * 4;
                            double al = src.Px[i + 3] / 255.0;
                            r += src.Px[i] * al * w; g += src.Px[i + 1] * al * w; b += src.Px[i + 2] * al * w; a += al * w; wsum += w;
                        }
                    }
                    int o = (y * dw + x) * 4;
                    if (wsum <= 0 || a <= 1e-6) continue;
                    dst.Px[o] = Clamp8(r / a); dst.Px[o + 1] = Clamp8(g / a); dst.Px[o + 2] = Clamp8(b / a); dst.Px[o + 3] = Clamp8(a / wsum * 255);
                }
            }
            return dst;
        }

        /// <summary>긴 변이 maxSide를 넘으면 비율대로 줄인다 (면적 평균). 넘지 않으면 그대로.</summary>
        public static Rgba Resample2(Rgba src, int maxSide)
        {
            if (Math.Max(src.W, src.H) <= maxSide) return src;
            double k = (double)maxSide / Math.Max(src.W, src.H);
            return Resample(src, 0, 0, src.W, src.H, Math.Max(1, (int)(src.W * k)), Math.Max(1, (int)(src.H * k)));
        }

        /// <summary>보이는 부분만 잘라 w×h 칸 안쪽(margin + outline 만큼 띄움)에 비율 그대로 가운데 놓는다. 보이는 것이 없으면 null.</summary>
        public static Rgba? Fit(Rgba src, int w, int h, int margin, int outline)
        {
            var bb = OpaqueBounds(src);
            if (bb == null) return null;
            var (bx, by, bw, bh) = bb.Value;
            int innerW = Math.Max(1, w - 2 * (margin + outline)), innerH = Math.Max(1, h - 2 * (margin + outline));
            double s = Math.Min((double)innerW / bw, (double)innerH / bh);
            int dw = Math.Max(1, (int)Math.Round(bw * s)), dh = Math.Max(1, (int)Math.Round(bh * s));
            var small = Resample(src, bx, by, bw, bh, dw, dh);
            var dst = new Rgba(w, h);
            int ox = (w - dw) / 2, oy = (h - dh) / 2;
            for (int y = 0; y < dh; y++) Buffer.BlockCopy(small.Px, y * dw * 4, dst.Px, ((oy + y) * w + ox) * 4, dw * 4);
            return dst;
        }

        /// <summary>올리기 썸네일 (2026-11-01): 비율이 맞게 가운데를 잘라 w×h로 줄이고, 투명한 곳은 흰색으로 채운다.</summary>
        public static Rgba Cover(Rgba src, int w, int h)
        {
            int sw = src.W, sh = src.H;
            if ((long)src.W * h > (long)src.H * w) sw = Math.Max(1, (int)Math.Round((double)src.H * w / h));
            else sh = Math.Max(1, (int)Math.Round((double)src.W * h / w));
            var dst = Resample(src, (src.W - sw) / 2, (src.H - sh) / 2, sw, sh, w, h);
            for (int i = 0; i < dst.Px.Length; i += 4)
            {
                int a = dst.Px[i + 3];
                for (int c = 0; c < 3; c++) dst.Px[i + c] = (byte)((dst.Px[i + c] * a + 255 * (255 - a) + 127) / 255);
                dst.Px[i + 3] = 255;
            }
            return dst;
        }

        // ---------------- 테두리 ----------------
        /// <summary>보이는 부분 둘레에 두께 width(px)의 테두리를 그 뒤에 깐다 (색은 RGB). 가장자리 1px은 부드럽게.</summary>
        public static void Outline(Rgba img, int width, byte r, byte g, byte b)
        {
            if (width <= 0) return;
            int n = img.W * img.H;
            // 불투명(알파 절반 이상)까지의 거리²
            var f = new double[n];
            for (int i = 0; i < n; i++) f[i] = img.Px[i * 4 + 3] >= 128 ? 0 : 1e20;
            DistanceTransform(f, img.W, img.H);
            var p = img.Px;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Sqrt(f[i]);
                double oa = Math.Max(0, Math.Min(1, width + 0.5 - d));   // 테두리 덮임
                if (oa <= 0) continue;
                int o = i * 4;
                double sa = p[o + 3] / 255.0;
                // 원래 그림을 테두리 위에 얹는다 (over)
                double a = sa + oa * (1 - sa);
                if (a <= 0) continue;
                p[o] = Clamp8((p[o] * sa + r * oa * (1 - sa)) / a);
                p[o + 1] = Clamp8((p[o + 1] * sa + g * oa * (1 - sa)) / a);
                p[o + 2] = Clamp8((p[o + 2] * sa + b * oa * (1 - sa)) / a);
                p[o + 3] = Clamp8(a * 255);
            }
        }

        /// <summary>제곱 유클리드 거리 변환 (Felzenszwalb–Huttenlocher, 가로·세로 두 번).</summary>
        static void DistanceTransform(double[] f, int w, int h)
        {
            int m = Math.Max(w, h);
            var line = new double[m]; var outp = new double[m]; var v = new int[m]; var z = new double[m + 1];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) line[y] = f[y * w + x];
                Dt1(line, h, outp, v, z);
                for (int y = 0; y < h; y++) f[y * w + x] = outp[y];
            }
            for (int y = 0; y < h; y++)
            {
                Array.Copy(f, y * w, line, 0, w);
                Dt1(line, w, outp, v, z);
                Array.Copy(outp, 0, f, y * w, w);
            }
        }

        static void Dt1(double[] f, int n, double[] d, int[] v, double[] z)
        {
            int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    s = ((f[q] + q * (double)q) - (f[v[k]] + v[k] * (double)v[k])) / (2.0 * q - 2.0 * v[k]);
                    if (s <= z[k] && k > 0) { k--; continue; }
                    if (s <= z[k]) { s = z[k]; }
                    break;
                }
                k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // ---------------- 만들기 ----------------
        public sealed class Options
        {
            public string Spec = "line";
            public int OutlineWidth = 6;
            public byte[] OutlineColor = { 255, 255, 255 };
            public double Brightness, Contrast = 1, Saturation = 1;
        }

        /// <summary>찍은 이미지로 규격 이미지(들)를 만든다. 크기마다 (크기, 이미지). 보이는 것이 없으면 빈 목록.</summary>
        public static List<(int Size, Rgba Image)> Make(Rgba shot, Options o)
        {
            var spec = Find(o.Spec);
            var src = new Rgba(shot.W, shot.H, (byte[])shot.Px.Clone());
            Adjust(src, o.Brightness, o.Contrast, o.Saturation);
            var list = new List<(int, Rgba)>();
            foreach (var size in new[] { spec.W }.ConcatSizes(spec.Extra))
            {
                int w = size == spec.W ? spec.W : size, h = size == spec.W ? spec.H : size;
                double k = (double)w / spec.W;
                int outline = (int)Math.Round(o.OutlineWidth * k), margin = (int)Math.Round(spec.Margin * k);
                var img = Fit(src, w, h, margin, outline);
                if (img == null) return new List<(int, Rgba)>();
                Outline(img, outline, o.OutlineColor[0], o.OutlineColor[1], o.OutlineColor[2]);
                list.Add((size, img));
            }
            return list;
        }

        static IEnumerable<int> ConcatSizes(this int[] first, int[] extra) { foreach (var x in first) yield return x; foreach (var x in extra) yield return x; }

        // ---------------- PNG ----------------
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>PNG 쓰기 (RGBA 8비트, 줄마다 Paeth/Sub 중 작은 쪽 — 크기 제한 규격에 맞게 적당히 작게).</summary>
        public static byte[] EncodePng(Rgba img, CompressionLevel level = CompressionLevel.SmallestSize)
        {
            int stride = img.W * 4;
            var raw = new MemoryStream();
            var a = new byte[stride]; var b = new byte[stride];
            for (int y = 0; y < img.H; y++)
            {
                int row = y * stride;
                long sa = 0, sb = 0;
                for (int x = 0; x < stride; x++)
                {
                    int cur = img.Px[row + x];
                    int left = x >= 4 ? img.Px[row + x - 4] : 0, up = y > 0 ? img.Px[row - stride + x] : 0, ul = x >= 4 && y > 0 ? img.Px[row - stride + x - 4] : 0;
                    a[x] = (byte)(cur - left);
                    b[x] = (byte)(cur - Paeth(left, up, ul));
                    sa += (sbyte)a[x] < 0 ? -(sbyte)a[x] : a[x]; sb += (sbyte)b[x] < 0 ? -(sbyte)b[x] : b[x];
                }
                bool paeth = sb < sa;
                raw.WriteByte(paeth ? (byte)4 : (byte)1);
                raw.Write(paeth ? b : a, 0, stride);
            }
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, level, leaveOpen: true)) { raw.Position = 0; raw.CopyTo(zs); }
            var outp = new MemoryStream();
            outp.Write(Signature, 0, 8);
            var ihdr = new byte[13];
            BE(ihdr, 0, img.W); BE(ihdr, 4, img.H); ihdr[8] = 8; ihdr[9] = 6;
            Chunk(outp, "IHDR", ihdr);
            Chunk(outp, "IDAT", z.ToArray());
            Chunk(outp, "IEND", Array.Empty<byte>());
            return outp.ToArray();
        }

        static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
        static void BE(byte[] d, int o, int v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }

        internal static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, data.Length); s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
            var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c, 0, 4);
        }

        static uint[]? _crc;
        static uint Crc(uint c, byte[] d)
        {
            if (_crc == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint k = n; for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xEDB88320u ^ (k >> 1) : k >> 1; t[n] = k; }
                _crc = t;
            }
            foreach (var x in d) c = _crc[(c ^ x) & 0xFF] ^ (c >> 8);
            return c;
        }

        /// <summary>PNG 읽기: 8비트 RGB·RGBA, 인터레이스 없음 (렌더러 촬영 결과). 그 밖이면 null.</summary>
        public static Rgba? DecodePng(byte[] f)
        {
            if (f.Length < 8) return null;
            for (int i = 0; i < 8; i++) if (f[i] != Signature[i]) return null;
            int w = 0, h = 0, depth = 0, color = 0, interlace = 0, p = 8;
            var idat = new MemoryStream();
            while (p + 8 <= f.Length)
            {
                int len = (f[p] << 24) | (f[p + 1] << 16) | (f[p + 2] << 8) | f[p + 3];
                string type = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
                int d = p + 8;
                if (len < 0 || d + len > f.Length) return null;
                if (type == "IHDR") { w = (f[d] << 24) | (f[d + 1] << 16) | (f[d + 2] << 8) | f[d + 3]; h = (f[d + 4] << 24) | (f[d + 5] << 16) | (f[d + 6] << 8) | f[d + 7]; depth = f[d + 8]; color = f[d + 9]; interlace = f[d + 12]; }
                else if (type == "IDAT") idat.Write(f, d, len);
                else if (type == "IEND") break;
                p = d + len + 4;
            }
            if (w <= 0 || h <= 0 || depth != 8 || interlace != 0 || (color != 2 && color != 6)) return null;
            int bpp = color == 6 ? 4 : 3, stride = w * bpp;
            idat.Position = 0;
            var raw = new MemoryStream();
            using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
            var r = raw.ToArray();
            if (r.Length < (stride + 1) * h) return null;
            var cur = new byte[stride]; var prev = new byte[stride];
            var img = new Rgba(w, h);
            int q = 0;
            for (int y = 0; y < h; y++)
            {
                int ft = r[q++];
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                    int v = r[q++];
                    switch (ft) { case 1: v += a; break; case 2: v += b; break; case 3: v += (a + b) >> 1; break; case 4: v += Paeth(a, b, c); break; }
                    cur[x] = (byte)v;
                }
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    img.Px[o] = cur[x * bpp]; img.Px[o + 1] = cur[x * bpp + 1]; img.Px[o + 2] = cur[x * bpp + 2]; img.Px[o + 3] = bpp == 4 ? cur[x * bpp + 3] : (byte)255;
                }
                (cur, prev) = (prev, cur);
            }
            return img;
        }
    }
}
