// PNG 디코더 (작업 스레드용): Unity의 Texture2D.LoadImage는 메인 스레드에서만 돌아 큰 텍스처가 많으면 장면 로드가 길어진다
// (큰 시험 장면: 4096² PNG 76장 = 디코드만 13초). 여기서는 zlib(DeflateStream) + PNG 필터를 직접 풀어 여러 스레드에서 동시에 디코드한다.
//   지원: 비트 깊이 1·2·4·8·16, 색 형식 0(회색)·2(RGB)·3(팔레트)·4(회색+알파)·6(RGBA), tRNS(팔레트 알파·투명 색). 16비트는 위 8비트만.
//   지원하지 않음: 인터레이스(Adam7) → false (호출하는 쪽이 LoadImage로 디코드)
// 결과는 RGBA32, Unity 텍스처 순서(아래 줄부터). 감마·색 프로필 청크(gAMA·iCCP)는 LoadImage처럼 무시한다.
// 메모리 (2026-10-20): 풀어 놓은 줄·RGBA·줄인 결과는 모두 관리되지 않는 메모리(NativeArray·UnsafeUtility.Malloc)에 둔다.
//   예전엔 byte[]였는데 4096² 한 장에 170 MB쯤을 12개 스레드가 동시에 잡아 Mono 힙이 2.2 GB까지 커졌고,
//   Unity의 Mono GC는 커진 힙을 Windows에 돌려주지 않아 렌더러가 장면을 불러온 뒤에도 그만큼을 계속 차지했다 (큰 시험 장면: 전용 4.6 GB).
//   관리되지 않는 메모리는 텍스처를 올린 뒤 바로 풀려 돌아간다. 파일도 관리되지 않는 버퍼로 읽고(ReadFile), IDAT는 복사하지 않고 거기서 바로 읽는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace VrcProto.Viewer
{
    public static class PngDecoder
    {
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>파일을 관리되지 않는 버퍼로 읽는다 (Persistent, 부른 쪽이 UnsafeUtility.Free). 64 KB 조각 버퍼만 관리 메모리에 잡는다.</summary>
        public static unsafe byte* ReadFile(string path, out long length)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                length = fs.Length;
                var p = (byte*)UnsafeUtility.Malloc(Math.Max(1, length), 16, Allocator.Persistent);
                try
                {
                    var chunk = new byte[65536];
                    long got = 0;
                    while (got < length)
                    {
                        int n = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, length - got));
                        if (n <= 0) throw new EndOfStreamException();
                        fixed (byte* c = chunk) UnsafeUtility.MemCpy(p + got, c, n);
                        got += n;
                    }
                    return p;
                }
                catch { UnsafeUtility.Free(p, Allocator.Persistent); throw; }
            }
        }

        public static unsafe bool IsPng(byte* f, long length)
        {
            if (length < 8) return false;
            for (int i = 0; i < 8; i++) if (f[i] != Signature[i]) return false;
            return true;
        }

        /// <summary>디코드하고 maxSize(0 = 그대로)로 줄인다. 성공하면 rgba(Persistent)를 부른 쪽이 Dispose한다.</summary>
        public static unsafe bool TryDecode(byte* f, long fLength, int maxSize, out int width, out int height, out NativeArray<byte> rgba)
        {
            width = height = 0; rgba = default;
            if (!IsPng(f, fLength)) return false;
            int depth = 0, color = 0, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new List<(long Off, int Len)>();
            long p = 8;
            while (p + 8 <= fLength)
            {
                int len = BigInt(f + p); string type = new string(new[] { (char)f[p + 4], (char)f[p + 5], (char)f[p + 6], (char)f[p + 7] });
                long data = p + 8;
                if (len < 0 || data + len > fLength) return false;
                switch (type)
                {
                    case "IHDR":
                        width = BigInt(f + data); height = BigInt(f + data + 4);
                        depth = f[data + 8]; color = f[data + 9]; interlace = f[data + 12];
                        break;
                    case "PLTE": palette = new byte[len]; fixed (byte* d = palette) UnsafeUtility.MemCpy(d, f + data, len); break;
                    case "tRNS": trns = new byte[len]; fixed (byte* d = trns) UnsafeUtility.MemCpy(d, f + data, len); break;
                    case "IDAT": idat.Add((data, len)); break;
                }
                if (type == "IEND") break;
                p = data + len + 4;   // CRC
            }
            if (width <= 0 || height <= 0 || interlace != 0) return false;
            int channels = color == 0 ? 1 : color == 2 ? 3 : color == 3 ? 1 : color == 4 ? 2 : color == 6 ? 4 : 0;
            if (channels == 0 || depth != 1 && depth != 2 && depth != 4 && depth != 8 && depth != 16) return false;
            if (color == 3 && palette == null) return false;
            int bitsPerPixel = channels * depth;
            int stride = (width * bitsPerPixel + 7) / 8;
            int bpp = Math.Max(1, bitsPerPixel / 8);   // 필터가 비교하는 앞 픽셀까지의 바이트

            long rawLen = (long)(stride + 1) * height;
            byte* raw = (byte*)UnsafeUtility.Malloc(rawLen, 16, Allocator.Persistent);
            var outArr = default(NativeArray<byte>);
            try
            {
                // zlib: 2바이트 머리 뒤가 deflate. 조각 버퍼(64 KB)로 읽어 관리되지 않는 줄 버퍼에 옮긴다
                using (var z = new DeflateStream(new ChunkStream(f, idat, 2), CompressionMode.Decompress))
                {
                    var chunk = new byte[65536];
                    long got = 0;
                    while (got < rawLen)
                    {
                        int n = z.Read(chunk, 0, (int)Math.Min(chunk.Length, rawLen - got));
                        if (n <= 0) break;
                        fixed (byte* c = chunk) UnsafeUtility.MemCpy(raw + got, c, n);
                        got += n;
                    }
                    if (got < rawLen) return false;
                }

                // 필터 풀기 (제자리에서, 줄마다 앞 줄을 참조). 줄 앞 bpp바이트(왼쪽 픽셀 없음)와 첫 줄(위 줄 없음)을 따로 돌려
                // 바이트마다 하던 조건 검사를 뺐다 — 첫 줄의 Paeth는 Sub, 줄 앞의 Paeth는 Up과 같다
                for (int y = 0; y < height; y++)
                {
                    byte* cur = raw + (long)y * (stride + 1) + 1, prev = cur - (stride + 1);
                    int filter = cur[-1];
                    bool first = y == 0;
                    int head = Math.Min(bpp, stride);
                    switch (filter)
                    {
                        case 0: break;
                        case 1: for (int i = bpp; i < stride; i++) cur[i] += cur[i - bpp]; break;
                        case 2: if (!first) for (int i = 0; i < stride; i++) cur[i] += prev[i]; break;
                        case 3:
                            if (first) { for (int i = bpp; i < stride; i++) cur[i] += (byte)(cur[i - bpp] >> 1); break; }
                            for (int i = 0; i < head; i++) cur[i] += (byte)(prev[i] >> 1);
                            for (int i = bpp; i < stride; i++) cur[i] += (byte)((cur[i - bpp] + prev[i]) >> 1);
                            break;
                        case 4:
                            if (first) { for (int i = bpp; i < stride; i++) cur[i] += cur[i - bpp]; break; }
                            for (int i = 0; i < head; i++) cur[i] += prev[i];
                            for (int i = bpp; i < stride; i++)
                            {
                                int a = cur[i - bpp], b = prev[i], c = prev[i - bpp];
                                int pa = b - c, pb = a - c, pc = pa + pb;
                                if (pa < 0) pa = -pa; if (pb < 0) pb = -pb; if (pc < 0) pc = -pc;
                                cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                            }
                            break;
                        default: return false;
                    }
                }

                // RGBA32, 아래 줄부터
                outArr = new NativeArray<byte>(width * height * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                byte* o = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(outArr);
                int scale = depth < 8 ? 255 / ((1 << depth) - 1) : 1;
                bool key = trns != null && (color == 0 || color == 2);
                int kr = -1, kg = -1, kb = -1;
                if (key)
                {
                    // 투명 색: 16비트 값 (비트 깊이 단위). 8비트로 맞춰 비교한다
                    int K(int i) { int v = trns.Length > i + 1 ? (trns[i] << 8 | trns[i + 1]) : 0; return depth == 16 ? v >> 8 : depth < 8 ? v * scale : v; }
                    if (color == 0) kr = kg = kb = K(0); else { kr = K(0); kg = K(2); kb = K(4); }
                }
                for (int y = 0; y < height; y++)
                {
                    byte* src = raw + (long)y * (stride + 1) + 1;
                    byte* dst = o + (long)(height - 1 - y) * width * 4;
                    // 흔한 형식(8비트 RGBA·RGB)은 픽셀마다 형식을 가리지 않고 줄째로
                    if (depth == 8 && color == 6) { UnsafeUtility.MemCpy(dst, src, (long)width * 4); continue; }
                    if (depth == 8 && color == 2 && !key)
                    {
                        for (int x = 0; x < width; x++, dst += 4, src += 3) { dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2]; dst[3] = 255; }
                        continue;
                    }
                    for (int x = 0; x < width; x++, dst += 4)
                    {
                        byte r, g, b, a = 255;
                        if (depth < 8)
                        {
                            int bit = x * depth, v = src[bit >> 3] >> (8 - depth - (bit & 7)) & ((1 << depth) - 1);
                            if (color == 3)
                            {
                                int pi = v * 3;
                                r = pi + 2 < palette.Length ? palette[pi] : (byte)0; g = pi + 2 < palette.Length ? palette[pi + 1] : (byte)0; b = pi + 2 < palette.Length ? palette[pi + 2] : (byte)0;
                                if (trns != null && v < trns.Length) a = trns[v];
                            }
                            else { r = g = b = (byte)(v * scale); if (key && r == kr) a = 0; }
                        }
                        else
                        {
                            int step = depth == 16 ? 2 : 1;   // 16비트는 위 바이트
                            byte* q = src + x * channels * step;
                            switch (color)
                            {
                                case 0: r = g = b = q[0]; if (key && r == kr) a = 0; break;
                                case 2: r = q[0]; g = q[step]; b = q[2 * step]; if (key && r == kr && g == kg && b == kb) a = 0; break;
                                case 3:
                                    {
                                        int v = q[0], pi = v * 3;
                                        r = pi + 2 < palette.Length ? palette[pi] : (byte)0; g = pi + 2 < palette.Length ? palette[pi + 1] : (byte)0; b = pi + 2 < palette.Length ? palette[pi + 2] : (byte)0;
                                        if (trns != null && v < trns.Length) a = trns[v];
                                        break;
                                    }
                                case 4: r = g = b = q[0]; a = q[step]; break;
                                default: r = q[0]; g = q[step]; b = q[2 * step]; a = q[3 * step]; break;
                            }
                        }
                        dst[0] = r; dst[1] = g; dst[2] = b; dst[3] = a;
                    }
                }
            }
            catch { if (outArr.IsCreated) outArr.Dispose(); throw; }
            finally { UnsafeUtility.Free(raw, Allocator.Persistent); }
            if (!outArr.IsCreated) return false;
            if (maxSize > 0 && Math.Max(width, height) > maxSize) outArr = Downscale(outArr, width, height, maxSize, out width, out height);
            rgba = outArr;
            return true;
        }

        /// <summary>
        /// Unity maxTextureSize처럼 긴 변을 maxSize로 줄인다 (비율 유지). 반으로 나눌 수 있는 만큼은 2×2 평균(밉맵과 같은 상자 필터)으로,
        /// 남은 비율은 면적 평균으로. RGBA32, 줄 순서 그대로. src는 Dispose하고 새 배열을 돌려준다 (크기가 같으면 src 그대로).
        /// </summary>
        static unsafe NativeArray<byte> Downscale(NativeArray<byte> srcArr, int w, int h, int maxSize, out int ow, out int oh)
        {
            float s = (float)maxSize / Math.Max(w, h);
            ow = Math.Max(1, (int)Math.Round(w * s)); oh = Math.Max(1, (int)Math.Round(h * s));
            while (w >= ow * 2 && h >= oh * 2 && w % 2 == 0 && h % 2 == 0)
            {
                int nw = w / 2, nh = h / 2;
                var dArr = new NativeArray<byte>(nw * nh * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                byte* src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(srcArr), d = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(dArr);
                for (int y = 0; y < nh; y++)
                {
                    byte* r0 = src + (long)(2 * y) * w * 4, r1 = src + (long)(2 * y + 1) * w * 4, o = d + (long)y * nw * 4;
                    for (int x = 0; x < nw; x++, o += 4)
                    {
                        byte* a = r0 + x * 8, b = r1 + x * 8;
                        for (int c = 0; c < 4; c++) o[c] = (byte)((a[c] + a[4 + c] + b[c] + b[4 + c] + 2) >> 2);
                    }
                }
                srcArr.Dispose(); srcArr = dArr; w = nw; h = nh;
            }
            if (w == ow && h == oh) return srcArr;
            // 면적 평균 (줄이기) — 남은 비율이 2 미만이거나 홀수 크기일 때
            var dstArr = new NativeArray<byte>(ow * oh * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            byte* sp = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(srcArr), dp = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(dstArr);
            float fx = (float)w / ow, fy = (float)h / oh;
            for (int y = 0; y < oh; y++)
            {
                int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, Math.Min(h, (int)Math.Ceiling((y + 1) * fy)));
                for (int x = 0; x < ow; x++)
                {
                    int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, Math.Min(w, (int)Math.Ceiling((x + 1) * fx)));
                    int n = (x1 - x0) * (y1 - y0), r = 0, g = 0, b = 0, a = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) { byte* q = sp + ((long)yy * w + xx) * 4; r += q[0]; g += q[1]; b += q[2]; a += q[3]; }
                    byte* o = dp + ((long)y * ow + x) * 4;
                    o[0] = (byte)(r / n); o[1] = (byte)(g / n); o[2] = (byte)(b / n); o[3] = (byte)(a / n);
                }
            }
            srcArr.Dispose();
            return dstArr;
        }

        /// <summary>파일 버퍼 안의 IDAT 조각들을 이어 읽는 스트림 (복사 없음). skip = 맨 앞에서 건너뛸 바이트 (zlib 머리).</summary>
        unsafe sealed class ChunkStream : Stream
        {
            readonly byte* _f; readonly List<(long Off, int Len)> _parts; int _i, _pos;
            public ChunkStream(byte* f, List<(long Off, int Len)> parts, int skip)
            {
                _f = f; _parts = parts;
                while (skip > 0 && _i < _parts.Count) { int k = Math.Min(skip, _parts[_i].Len - _pos); _pos += k; skip -= k; if (_pos >= _parts[_i].Len) { _i++; _pos = 0; } }
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (count > 0 && _i < _parts.Count)
                {
                    var (off, len) = _parts[_i];
                    int k = Math.Min(count, len - _pos);
                    fixed (byte* d = buffer) UnsafeUtility.MemCpy(d + offset, _f + off + _pos, k);
                    offset += k; count -= k; total += k; _pos += k;
                    if (_pos >= len) { _i++; _pos = 0; }
                }
                return total;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        static unsafe int BigInt(byte* b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
    }
}
