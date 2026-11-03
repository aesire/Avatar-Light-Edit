// 텍스처 디스크 캐시 (2026-10-23 로드 시간): 원본 PNG를 풀고 maxSize로 줄이고 밉맵까지 만든 결과를 그대로 저장해 두고,
// 다음에 같은 파일·같은 임포트 설정이면 PNG를 풀지 않고 읽어 바로 GPU에 올린다. 원본 파일은 건드리지 않는다 (PLAN 3.1).
//   모드 (Host 설정 "텍스처 캐시", loadScene의 texCache):
//     off  = 캐시 없음 (예전처럼 매번 디코드, 압축 안 함)
//     orig = 원본 설정대로 (2026-11-02 기본): 원본이 PC에서 DXT로 압축하는 텍스처(IrTextureImporter.Gpu, TextureGpu)만 DXT1/DXT5로.
//            VRChat에서 보이는 것과 같고 가볍다. 원본이 압축 안 함·고품질(BC7)이면 rgba 그대로.
//     rgba = 줄인 결과 그대로 (RGBA32 + 밉맵, 화면은 캐시 없을 때와 같다) — 압축 안 함
//     dxt  = 모두 GPU 압축(DXT1/DXT5)으로 저장·사용: 디스크·GPU 메모리 1/4~1/8, 조금 덜 선명하다.
//   노멀맵·가로세로가 4의 배수가 아닌 텍스처는 늘 rgba 그대로.
//   위치: %LOCALAPPDATA%\ALEdit\texcache\<키 해시>.vptc — 키 = 원본 경로·크기·수정 시각·임포트 설정·모드. 원본이 바뀌면 키가 달라져 새로 만든다.
//   크기 제한: 모두 합쳐 LimitBytes를 넘으면 오래 안 쓴 것부터 지운다 (읽을 때마다 수정 시각을 지금으로).
// 밉맵·DXT 인코딩은 작업 스레드에서 관리되지 않는 메모리로 한다 (Unity API를 쓰지 않음).
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public static class TexCache
    {
        public const string Off = "off", Orig = "orig", Rgba = "rgba", Dxt = "dxt";
        /// <summary>지금 모드 (loadScene마다 Host가 정한다)</summary>
        public static volatile string Mode = Rgba;

        /// <summary>이 텍스처를 DXT로 압축할지 (크기가 4의 배수인지는 만들 때 따로 본다).</summary>
        public static bool Compress(IrTextureImporter imp, string mode) =>
            !imp.NormalMap && (mode == Dxt || mode == Orig && VrcProto.Protocol.TextureGpu.IsDxt(imp.Gpu));

        /// <summary>알파가 있어도 DXT1로 (원본이 DXT1을 고른 경우 — Unity처럼 알파를 버린다).</summary>
        public static bool ForceDxt1(IrTextureImporter imp, string mode) => mode == Orig && imp.Gpu == VrcProto.Protocol.TextureGpu.Dxt1;

        /// <summary>RGBA 밉맵 사슬을 압축 (DXT5 = 알파 있음). 원본이 정한 형식이 있으면 그대로, 없으면 알파가 있는지 보고.</summary>
        public static NativeArray<byte> EncodeFor(IrTextureImporter imp, string mode, NativeArray<byte> rgbaChain, int w, int h, int mips, out Fmt fmt)
        {
            bool alpha = ForceDxt1(imp, mode) ? false : mode == Orig && imp.Gpu == VrcProto.Protocol.TextureGpu.Dxt5 || HasAlpha(rgbaChain, w, h);
            fmt = alpha ? Fmt.Dxt5 : Fmt.Dxt1;
            return EncodeDxt(rgbaChain, w, h, mips, alpha);
        }
        public static long LimitBytes = 4L << 30;
        public static int Hits, Misses;
        public enum Fmt { Rgba32 = 0, Dxt1 = 1, Dxt5 = 2 }
        const uint Magic = 0x43545056;   // "VPTC"
        const int Version = 1, HeaderSize = 32;

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "texcache");

        /// <summary>이 텍스처의 캐시 파일 경로 (모드가 off거나 원본이 없으면 null).</summary>
        public static string FileFor(IrTexture t, string mode)
        {
            if (mode == Off) return null;
            var ext = Path.GetExtension(t.File).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && !VrcProto.Protocol.ImageDecode.Handles(ext)) return null;   // 아직 못 읽는 형식은 캐시도 없다
            var fi = new FileInfo(t.File);
            if (!fi.Exists) return null;
            var imp = t.Importer;
            // 압축 여부만 키에 (orig로 압축한 것과 dxt 모드가 같은 파일을 쓴다), DXT1을 강제한 것은 따로
            string fmt = !Compress(imp, mode) ? Rgba : ForceDxt1(imp, mode) ? "dxt1" : Dxt;
            var key = $"{Version}|{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{imp.MaxSize}|{imp.Mipmaps}|{fmt}";
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(40);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(Dir, sb.ToString() + ".vptc");
            }
        }

        public static int MipCount(int w, int h) { int n = 1, s = Math.Max(w, h); while (s > 1) { s >>= 1; n++; } return n; }

        public static long LevelBytes(Fmt f, int w, int h) => f == Fmt.Rgba32 ? (long)w * h * 4 : (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (f == Fmt.Dxt1 ? 8 : 16);

        public static long ChainBytes(Fmt f, int w, int h, int mips)
        {
            long s = 0;
            for (int i = 0; i < mips; i++) s += LevelBytes(f, Math.Max(1, w >> i), Math.Max(1, h >> i));
            return s;
        }

        // ---------------- 읽기·쓰기 ----------------
        /// <summary>캐시 파일을 관리되지 않는 배열(Persistent, 부른 쪽이 Dispose)로 읽는다. 없거나 깨졌으면 false.</summary>
        public static unsafe bool TryRead(string file, out Fmt fmt, out int w, out int h, out int mips, out NativeArray<byte> data)
        {
            fmt = Fmt.Rgba32; w = h = mips = 0; data = default;
            if (file == null || !File.Exists(file)) return false;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    var head = new byte[HeaderSize];
                    if (fs.Read(head, 0, HeaderSize) != HeaderSize) return false;
                    if (BitConverter.ToUInt32(head, 0) != Magic || BitConverter.ToInt32(head, 4) != Version) return false;
                    fmt = (Fmt)BitConverter.ToInt32(head, 8); w = BitConverter.ToInt32(head, 12); h = BitConverter.ToInt32(head, 16); mips = BitConverter.ToInt32(head, 20);
                    long len = BitConverter.ToInt64(head, 24);
                    if (w <= 0 || h <= 0 || mips <= 0 || len != ChainBytes(fmt, w, h, mips) || fs.Length != HeaderSize + len) return false;
                    data = new NativeArray<byte>((int)len, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    // 관리되지 않는 배열에 바로 읽는다 (중간 버퍼·복사 없음)
                    var span = new Span<byte>(NativeArrayUnsafeUtility.GetUnsafePtr(data), (int)len);
                    while (span.Length > 0)
                    {
                        int n = fs.Read(span);
                        if (n <= 0) { data.Dispose(); data = default; return false; }
                        span = span.Slice(n);
                    }
                }
                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (Exception) { }   // 오래 안 쓴 것부터 지우는 기준
                return true;
            }
            catch (Exception) { if (data.IsCreated) data.Dispose(); data = default; return false; }
        }

        /// <summary>캐시 파일로 쓴다 (임시 파일 → 바꿔 넣기). 실패하면 조용히 넘어간다 (다음에 다시 디코드).</summary>
        public static unsafe void Write(string file, Fmt fmt, int w, int h, int mips, byte* data, long len)
        {
            if (file == null) return;
            var tmp = file + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                {
                    var head = new byte[HeaderSize];
                    BitConverter.GetBytes(Magic).CopyTo(head, 0); BitConverter.GetBytes(Version).CopyTo(head, 4);
                    BitConverter.GetBytes((int)fmt).CopyTo(head, 8); BitConverter.GetBytes(w).CopyTo(head, 12); BitConverter.GetBytes(h).CopyTo(head, 16);
                    BitConverter.GetBytes(mips).CopyTo(head, 20); BitConverter.GetBytes(len).CopyTo(head, 24);
                    fs.Write(head, 0, HeaderSize);
                    var chunk = new byte[1 << 20];
                    for (long off = 0; off < len; off += chunk.Length)
                    {
                        int n = (int)Math.Min(chunk.Length, len - off);
                        fixed (byte* c = chunk) UnsafeUtility.MemCpy(c, data + off, n);
                        fs.Write(chunk, 0, n);
                    }
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } }
        }

        /// <summary>합친 크기가 제한을 넘으면 오래 안 쓴 것부터 지운다 (작업 스레드에서).</summary>
        public static void PruneLater()
        {
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(Dir)) return;
                    var files = new DirectoryInfo(Dir).GetFiles("*.vptc").OrderBy(f => f.LastWriteTimeUtc).ToList();
                    long total = files.Sum(f => f.Length);
                    foreach (var f in files)
                    {
                        if (total <= LimitBytes) break;
                        try { total -= f.Length; f.Delete(); } catch (Exception) { }
                    }
                    foreach (var t in new DirectoryInfo(Dir).GetFiles("*.tmp")) if (t.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) try { t.Delete(); } catch (Exception) { }
                }
                catch (Exception) { }
            });
        }

        // ---------------- 밉맵 (상자 필터, 저장된 값 그대로 평균 — Unity Apply(updateMipmaps)와 같은 방식) ----------------
        /// <summary>RGBA32 한 장(lvl0)에서 밉맵 사슬 전체를 새 배열로 만든다. lvl0는 그대로 둔다 (부른 쪽이 Dispose).</summary>
        public static unsafe NativeArray<byte> BuildMips(NativeArray<byte> lvl0, int w, int h, out int mips)
        {
            mips = MipCount(w, h);
            var chain = new NativeArray<byte>((int)ChainBytes(Fmt.Rgba32, w, h, mips), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            byte* dst = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(chain);
            UnsafeUtility.MemCpy(dst, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(lvl0), (long)w * h * 4);
            byte* src = dst; int sw = w, sh = h;
            byte* o = dst + (long)w * h * 4;
            for (int m = 1; m < mips; m++)
            {
                int nw = Math.Max(1, sw >> 1), nh = Math.Max(1, sh >> 1);
                for (int y = 0; y < nh; y++)
                {
                    int y0 = Math.Min(2 * y, sh - 1), y1 = Math.Min(2 * y + 1, sh - 1);
                    byte* r0 = src + (long)y0 * sw * 4, r1 = src + (long)y1 * sw * 4, d = o + (long)y * nw * 4;
                    for (int x = 0; x < nw; x++, d += 4)
                    {
                        int x0 = Math.Min(2 * x, sw - 1) * 4, x1 = Math.Min(2 * x + 1, sw - 1) * 4;
                        for (int c = 0; c < 4; c++) d[c] = (byte)((r0[x0 + c] + r0[x1 + c] + r1[x0 + c] + r1[x1 + c] + 2) >> 2);
                    }
                }
                src = o; sw = nw; sh = nh;
                o += (long)nw * nh * 4;
            }
            return chain;
        }

        public static unsafe bool HasAlpha(NativeArray<byte> rgba, int w, int h)
        {
            byte* p = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rgba);
            long n = (long)w * h * 4;
            for (long i = 3; i < n; i += 4) if (p[i] != 255) return true;
            return false;
        }

        // ---------------- DXT1/DXT5 (빠른 방식: 블록마다 색 범위 + 안쪽으로 조금 당긴 끝점, 가장 가까운 색) ----------------
        /// <summary>RGBA32 밉맵 사슬을 DXT1(alpha=false) 또는 DXT5로. 블록 안의 줄 순서는 원본 줄 순서 그대로.</summary>
        public static unsafe NativeArray<byte> EncodeDxt(NativeArray<byte> rgbaChain, int w, int h, int mips, bool alpha)
        {
            var f = alpha ? Fmt.Dxt5 : Fmt.Dxt1;
            var outArr = new NativeArray<byte>((int)ChainBytes(f, w, h, mips), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            byte* src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rgbaChain), dst = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(outArr);
            byte* block = stackalloc byte[64];
            for (int m = 0; m < mips; m++)
            {
                int lw = Math.Max(1, w >> m), lh = Math.Max(1, h >> m);
                int bw = Math.Max(1, (lw + 3) / 4), bh = Math.Max(1, (lh + 3) / 4);
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        for (int py = 0; py < 4; py++)
                        {
                            int y = Math.Min(by * 4 + py, lh - 1);
                            for (int px = 0; px < 4; px++)
                            {
                                int x = Math.Min(bx * 4 + px, lw - 1);
                                byte* s = src + ((long)y * lw + x) * 4, b = block + (py * 4 + px) * 4;
                                b[0] = s[0]; b[1] = s[1]; b[2] = s[2]; b[3] = s[3];
                            }
                        }
                        if (alpha) { AlphaBlock(block, dst); dst += 8; }
                        ColorBlock(block, dst, !alpha); dst += 8;
                    }
                src += (long)lw * lh * 4;
            }
            return outArr;
        }

        static unsafe void AlphaBlock(byte* px, byte* o)
        {
            int lo = 255, hi = 0;
            for (int i = 0; i < 16; i++) { int a = px[i * 4 + 3]; if (a < lo) lo = a; if (a > hi) hi = a; }
            o[0] = (byte)hi; o[1] = (byte)lo;
            ulong bits = 0;
            if (hi > lo)
            {
                // 8단계: 0 = hi, 1 = lo, 2~7 = hi에서 lo로 6/7 … 1/7
                int* v = stackalloc int[8];
                v[0] = hi; v[1] = lo;
                for (int i = 1; i <= 6; i++) v[i + 1] = ((7 - i) * hi + i * lo + 3) / 7;
                for (int i = 0; i < 16; i++)
                {
                    int a = px[i * 4 + 3], best = 0, bd = int.MaxValue;
                    for (int k = 0; k < 8; k++) { int d = Math.Abs(a - v[k]); if (d < bd) { bd = d; best = k; } }
                    bits |= (ulong)best << (3 * i);
                }
            }
            for (int i = 0; i < 6; i++) o[2 + i] = (byte)(bits >> (8 * i));
        }

        static unsafe void ColorBlock(byte* px, byte* o, bool dxt1)
        {
            int r0 = 255, g0 = 255, b0 = 255, r1 = 0, g1 = 0, b1 = 0;
            for (int i = 0; i < 16; i++)
            {
                byte* p = px + i * 4;
                if (dxt1 || p[3] > 0)   // DXT5: 완전히 투명한 픽셀의 색은 범위에서 뺀다
                {
                    if (p[0] < r0) r0 = p[0]; if (p[1] < g0) g0 = p[1]; if (p[2] < b0) b0 = p[2];
                    if (p[0] > r1) r1 = p[0]; if (p[1] > g1) g1 = p[1]; if (p[2] > b1) b1 = p[2];
                }
            }
            if (r0 > r1) { r0 = r1 = px[0]; g0 = g1 = px[1]; b0 = b1 = px[2]; }
            // 끝점을 범위 안쪽으로 1/16 당긴다 (튀는 픽셀 하나에 끝점이 끌려가지 않게)
            int ir = (r1 - r0) >> 4, ig = (g1 - g0) >> 4, ib = (b1 - b0) >> 4;
            r0 += ir; g0 += ig; b0 += ib; r1 -= ir; g1 -= ig; b1 -= ib;
            int c0 = To565(r1, g1, b1), c1 = To565(r0, g0, b0);   // c0 = 밝은 쪽 (c0 > c1이면 4색 모드)
            uint idx = 0;
            if (c0 != c1)
            {
                int* pal = stackalloc int[12];
                Expand(c0, pal); Expand(c1, pal + 3);
                for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k] + 1) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k] + 1) / 3; }
                for (int i = 0; i < 16; i++)
                {
                    byte* p = px + i * 4;
                    int best = 0, bd = int.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        int dr = p[0] - pal[k * 3], dg = p[1] - pal[k * 3 + 1], db = p[2] - pal[k * 3 + 2];
                        int d = dr * dr + dg * dg + db * db;
                        if (d < bd) { bd = d; best = k; }
                    }
                    // 팔레트 순서 0, 1, 2(=2/3 c0), 3(=1/3 c0)
                    idx |= (uint)best << (2 * i);
                }
            }
            o[0] = (byte)c0; o[1] = (byte)(c0 >> 8); o[2] = (byte)c1; o[3] = (byte)(c1 >> 8);
            o[4] = (byte)idx; o[5] = (byte)(idx >> 8); o[6] = (byte)(idx >> 16); o[7] = (byte)(idx >> 24);
        }

        static int To565(int r, int g, int b) => ((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255);
        static unsafe void Expand(int c, int* o) { int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31; o[0] = r << 3 | r >> 2; o[1] = g << 2 | g >> 4; o[2] = b << 3 | b >> 2; }
    }
}
