// 이미지 파일을 작게 읽기 (2026-10-27): 에셋 창 썸네일·색 조화 "이미지에서 뽑기"가 같이 쓴다.
//   PNG·JPG·BMP·WebP는 Avalonia 디코더, TGA·PSD는 렌더러와 같은 공용 디코더(Protocol/ImageDecode)로 읽어 줄인다.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    static class ImageFiles
    {
        /// <summary>썸네일·미리보기로 읽을 수 있는 확장자 (소문자, 점 포함).</summary>
        public static bool CanRead(string ext) => ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tga" or ".psd" or ".tif" or ".tiff" or ".exr";

        /// <summary>렌더러가 텍스처로 읽는 형식인지 (TextureLoader와 같게: PNG·JPG + 공용 디코더).</summary>
        public static bool PreviewReads(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" || ImageDecode.Handles(ext);
        }

        /// <summary>긴 변이 maxSide 이하가 되게 줄여 읽는다 (작업 스레드에서 불러도 된다). 못 읽으면 예외.</summary>
        public static Bitmap LoadSmall(string file, int maxSide)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".tga" or ".psd" or ".tif" or ".tiff" or ".exr") return Decoded(File.ReadAllBytes(file), ext, maxSide);
            using var full = new Bitmap(file);
            var ps = full.PixelSize;
            double k = Math.Min(1, (double)maxSide / Math.Max(ps.Width, ps.Height));
            if (k >= 1) return full.CreateScaledBitmap(ps, BitmapInterpolationMode.MediumQuality);
            return full.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(ps.Width * k)), Math.Max(1, (int)(ps.Height * k))), BitmapInterpolationMode.MediumQuality);
        }

        /// <summary>
        /// 전체 크기로 읽는다 (텍스처 합치기, 작업 스레드에서). 못 읽으면 예외.
        /// 투명한 곳의 숨은 색을 지켜야 해서 PNG는 직접 읽는다. Avalonia 디코더는 곱한 알파라 그 색이 지워진다 — 8비트 RGB·RGBA가 아닌 PNG와 JPG·WebP만 쓴다.
        /// </summary>
        public static Rgba LoadFull(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ImageDecode.Handles(ext))
            {
                var px = ImageDecode.Decode(File.ReadAllBytes(file), ext, false, out int w, out int h);
                return new Rgba(w, h, px);
            }
            if (ext == ".png" && Sticker.DecodePng(File.ReadAllBytes(file)) is Rgba png) return png;
            using var bmp = new Bitmap(file);
            var ps = bmp.PixelSize;
            var buf = new byte[ps.Width * ps.Height * 4];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(ps), pin.AddrOfPinnedObject(), buf.Length, ps.Width * 4); }
            catch (NotSupportedException)
            {
                // 회색조·팔레트 PNG 등은 Avalonia가 픽셀을 내주지 않는다 ("CopyPixels is not supported for this bitmap type", 2026-11-12) → Skia로 바로 RGBA(곱하지 않은 알파)
                using var sk = SkiaSharp.SKBitmap.Decode(file) ?? throw new InvalidDataException("이미지를 읽지 못했어요: " + Path.GetFileName(file));
                using var pm = sk.PeekPixels();
                var info = new SkiaSharp.SKImageInfo(sk.Width, sk.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                var rgba = new byte[sk.Width * sk.Height * 4];
                var pin2 = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                try { if (!pm.ReadPixels(info, pin2.AddrOfPinnedObject(), sk.Width * 4)) throw new InvalidDataException("이미지를 읽지 못했어요: " + Path.GetFileName(file)); }
                finally { pin2.Free(); }
                return new Rgba(sk.Width, sk.Height, rgba);
            }
            finally { pin.Free(); }
            bool bgra = bmp.Format == null || bmp.Format == PixelFormat.Bgra8888, premul = bmp.AlphaFormat == AlphaFormat.Premul;
            for (int i = 0; i < buf.Length; i += 4)
            {
                if (bgra) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
                int a = buf[i + 3];
                if (premul && a > 0 && a < 255)
                    for (int c = 0; c < 3; c++) buf[i + c] = (byte)Math.Min(255, buf[i + c] * 255 / a);
            }
            return new Rgba(ps.Width, ps.Height, buf);
        }

        /// <summary>공용 디코더 결과를 칸 평균으로 줄여 Bgra(곱한 알파) 비트맵으로.</summary>
        static Bitmap Decoded(byte[] bytes, string ext, int maxSide)
        {
            var src = ImageDecode.Decode(bytes, ext, false, out int w, out int h);
            double k = Math.Min(1, (double)maxSide / Math.Max(w, h));
            int tw = Math.Max(1, (int)(w * k)), th = Math.Max(1, (int)(h * k));
            var wb = new WriteableBitmap(new PixelSize(tw, th), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = wb.Lock();
            var row = new byte[tw * 4];
            for (int y = 0; y < th; y++)
            {
                int y0 = y * h / th, y1 = Math.Max(y0 + 1, (y + 1) * h / th);
                for (int x = 0; x < tw; x++)
                {
                    int x0 = x * w / tw, x1 = Math.Max(x0 + 1, (x + 1) * w / tw);
                    // 칸이 크면 4×4 표본만 (4096² 텍스처도 금방)
                    int sy = Math.Max(1, (y1 - y0) / 4), sx = Math.Max(1, (x1 - x0) / 4);
                    long r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy += sy)
                        for (int xx = x0; xx < x1; xx += sx)
                        {
                            int i = (yy * w + xx) * 4;
                            int al = src[i + 3];
                            r += src[i] * al; g += src[i + 1] * al; b += src[i + 2] * al; a += al; n++;
                        }
                    int o = x * 4;
                    row[o] = (byte)(b / (255 * n)); row[o + 1] = (byte)(g / (255 * n)); row[o + 2] = (byte)(r / (255 * n)); row[o + 3] = (byte)(a / n);
                }
                Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
            return wb;
        }
    }
}
