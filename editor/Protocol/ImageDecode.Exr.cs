// OpenEXR 디코드 (2026-11-01): 줄 저장 한 부분. 무압축·RLE·ZIPS·ZIP, 절반·부동소수·정수 채널. R·G·B·A 또는 Y를 쓴다.
//   값은 선형이라 8비트 sRGB로 바꾼다 (1 넘는 값은 잘린다 — 미리보기 텍스처용). 타일·여러 부분·PIZ 등 다른 압축은 못 읽는다고 알린다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VrcProto.Protocol
{
    public static partial class ImageDecode
    {
        sealed class ExrChannel { public string Name = ""; public int Type; public int Size => Type == 1 ? 2 : 4; }

        static byte[] Exr(byte[] b, bool bottomUp, out int w, out int h)
        {
            if (b.Length < 8 || b[0] != 0x76 || b[1] != 0x2F || b[2] != 0x31 || b[3] != 0x01) throw new InvalidDataException("EXR 파일이 아니에요");
            int flags = L32(b, 4);
            if ((flags & 0x200) != 0) throw new InvalidDataException("타일로 저장한 EXR은 못 읽어요");
            if ((flags & 0x1000) != 0) throw new InvalidDataException("여러 부분으로 저장한 EXR은 못 읽어요");
            int p = 8, comp = -1, xMin = 0, yMin = 0, xMax = -1, yMax = -1;
            var chans = new List<ExrChannel>();
            string Str()
            {
                int s = p;
                while (b[p] != 0) p++;
                var t = Encoding.UTF8.GetString(b, s, p - s);
                p++;
                return t;
            }
            while (b[p] != 0)
            {
                var name = Str(); Str();
                int size = L32(b, p); p += 4;
                int at = p;
                if (name == "compression") comp = b[at];
                else if (name == "dataWindow") { xMin = L32(b, at); yMin = L32(b, at + 4); xMax = L32(b, at + 8); yMax = L32(b, at + 12); }
                else if (name == "channels")
                {
                    int q = at, keep = p;
                    while (b[q] != 0)
                    {
                        p = q; var ch = Str(); q = p;
                        int type = L32(b, q);
                        if (L32(b, q + 8) != 1 || L32(b, q + 12) != 1) throw new InvalidDataException("채널 해상도가 다른 EXR은 못 읽어요");
                        chans.Add(new ExrChannel { Name = ch, Type = type });
                        q += 16;
                    }
                    p = keep;
                }
                p = at + size;
            }
            p++;
            w = xMax - xMin + 1; h = yMax - yMin + 1;
            var px = Alloc(w, h);
            int linesPerBlock = comp == 0 || comp == 1 || comp == 2 ? 1 : comp == 3 ? 16 : 0;
            if (linesPerBlock == 0) throw new InvalidDataException("지원하지 않는 EXR 압축 " + comp + " (무압축·RLE·ZIP만)");
            ExrChannel? R = null, G = null, B = null, A = null, Y = null;
            int pixelBytes = 0;
            foreach (var c in chans)
            {
                pixelBytes += c.Size;
                if (c.Name == "R") R = c; else if (c.Name == "G") G = c; else if (c.Name == "B") B = c; else if (c.Name == "A") A = c; else if (c.Name == "Y") Y = c;
            }
            if (Y == null && (R == null || G == null || B == null)) throw new InvalidDataException("R·G·B(또는 Y) 채널이 없는 EXR이에요");
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            int rowBytes = checked(w * pixelBytes);
            int blocks = (h + linesPerBlock - 1) / linesPerBlock;
            for (int k = 0; k < blocks; k++)
            {
                int at = (int)(L32(b, p + k * 8) & 0xFFFFFFFFL);
                int y0 = L32(b, at), size = L32(b, at + 4), data = at + 8;
                if (size < 0 || data + size > b.Length) throw new InvalidDataException("EXR 파일이 잘렸어요");
                int rows = Math.Min(linesPerBlock, yMax - y0 + 1);
                if (rows <= 0 || y0 < yMin) throw new InvalidDataException("EXR 줄 번호가 이상해요");
                int want = rows * rowBytes;
                byte[] d; int off;
                if (comp == 0 || size == want) { d = b; off = data; }
                else
                {
                    var t = comp == 1 ? ExrRle(b, data, size, want) : Inflate(b, data, size, want);
                    d = ExrUnfilter(t); off = 0;
                }
                for (int r = 0; r < rows; r++)
                {
                    int yTop = y0 - yMin + r, o = Row(yTop, h, bottomUp) * w, q = off + r * rowBytes;
                    foreach (var c in chans)
                    {
                        int dst = c == R || (c == Y && R == null) ? 0 : c == G ? 1 : c == B ? 2 : c == A ? 3 : -1;
                        if (dst >= 0)
                            for (int x = 0; x < w; x++)
                            {
                                float v = ExrValue(d, q + x * c.Size, c.Type);
                                int i = (o + x) * 4;
                                if (dst == 3) px[i + 3] = Unit8(v);
                                else
                                {
                                    px[i + dst] = Lin8(v);
                                    if (c == Y && R == null) { px[i + 1] = px[i]; px[i + 2] = px[i]; }
                                }
                            }
                        q += w * c.Size;
                    }
                }
            }
            return px;
        }

        static float ExrValue(byte[] b, int p, int type)
        {
            if (type == 2) return Float(b, p, bigEndian: false);
            if (type == 0) return (uint)L32(b, p) / 4294967295f;
            int v = b[p] | b[p + 1] << 8, e = v >> 10 & 31, m = v & 1023;
            float f = e == 0 ? m / 16777216f : e == 31 ? (m == 0 ? float.PositiveInfinity : float.NaN) : (float)((1 + m / 1024.0) * Math.Pow(2, e - 15));
            return (v & 0x8000) != 0 ? -f : f;
        }

        /// <summary>EXR RLE: 음수 = 그대로 -n바이트, 0 이상 = 다음 바이트를 n+1번.</summary>
        static byte[] ExrRle(byte[] b, int p, int len, int want)
        {
            var o = new byte[want];
            int end = p + len, at = 0;
            while (p < end && at < want)
            {
                int n = (sbyte)b[p++];
                if (n < 0) { n = -n; if (p + n > end || at + n > want) throw new InvalidDataException("EXR RLE 자료가 이상해요"); Buffer.BlockCopy(b, p, o, at, n); p += n; at += n; }
                else { if (p >= end || at + n + 1 > want) throw new InvalidDataException("EXR RLE 자료가 이상해요"); byte v = b[p++]; for (int i = 0; i <= n; i++) o[at++] = v; }
            }
            if (at != want) throw new InvalidDataException("EXR RLE 자료가 모자라요");
            return o;
        }

        /// <summary>RLE·ZIP 공통: 앞 값과의 차이 되돌리기 → 앞·뒤 반으로 나뉜 바이트 번갈아 잇기.</summary>
        static byte[] ExrUnfilter(byte[] t)
        {
            for (int i = 1; i < t.Length; i++) t[i] = (byte)(t[i - 1] + t[i] - 128);
            var o = new byte[t.Length];
            int half = (t.Length + 1) / 2;
            for (int i = 0; i < t.Length; i++) o[i] = i % 2 == 0 ? t[i / 2] : t[half + i / 2];
            return o;
        }
    }
}
