// 이미지를 클립보드에 (2026-10-27, 촬영 뒤 "복사"): Avalonia 클립보드는 Windows에서 그림을 못 넣어 Win32로 직접 넣는다.
//   PNG(투명 유지 — 디스코드·브라우저·포토샵이 먼저 읽음) + CF_DIB(그림판 등, 알파 없는 앱) + CF_HDROP(파일로 붙이기).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    static class ImageClipboard
    {
        /// <summary>PNG 파일을 클립보드에. 실패하면 이유를 돌려준다 (null = 성공).</summary>
        public static string? CopyPng(string path)
        {
            if (!OperatingSystem.IsWindows()) return "Windows에서만 돼요";
            byte[] png;
            try { png = File.ReadAllBytes(path); } catch (Exception e) { return e.Message; }
            var img = Sticker.DecodePng(png);
            if (img == null) return "PNG를 읽지 못했어요";
            if (!OpenClipboard(IntPtr.Zero)) return "다른 프로그램이 클립보드를 쓰는 중이에요";
            try
            {
                EmptyClipboard();
                Put(RegisterClipboardFormat("PNG"), png);
                Put(CF_DIB, Dib(img));
                Put(CF_HDROP, Drop(Path.GetFullPath(path)));
                return null;
            }
            finally { CloseClipboard(); }
        }

        /// <summary>BITMAPINFOHEADER + 아래→위 BGR(A) 32비트. 알파를 모르는 앱을 위해 흰 바탕에 합친다.</summary>
        static byte[] Dib(Rgba img)
        {
            int w = img.W, h = img.H;
            var b = new byte[40 + w * h * 4];
            BitConverter.GetBytes(40).CopyTo(b, 0); BitConverter.GetBytes(w).CopyTo(b, 4); BitConverter.GetBytes(h).CopyTo(b, 8);
            BitConverter.GetBytes((short)1).CopyTo(b, 12); BitConverter.GetBytes((short)32).CopyTo(b, 14);
            BitConverter.GetBytes(w * h * 4).CopyTo(b, 20);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4, o = 40 + ((h - 1 - y) * w + x) * 4;
                    int a = img.Px[s + 3];
                    b[o] = (byte)((img.Px[s + 2] * a + 255 * (255 - a)) / 255);
                    b[o + 1] = (byte)((img.Px[s + 1] * a + 255 * (255 - a)) / 255);
                    b[o + 2] = (byte)((img.Px[s] * a + 255 * (255 - a)) / 255);
                    b[o + 3] = 255;
                }
            return b;
        }

        /// <summary>DROPFILES(20바이트, 넓은 문자) + 경로 + 빈 글자 둘.</summary>
        static byte[] Drop(string path)
        {
            var name = Encoding.Unicode.GetBytes(path + "\0\0");
            var b = new byte[20 + name.Length];
            BitConverter.GetBytes(20).CopyTo(b, 0);
            BitConverter.GetBytes(1).CopyTo(b, 16);   // fWide
            name.CopyTo(b, 20);
            return b;
        }

        static void Put(uint format, byte[] data)
        {
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero) return;
            var p = GlobalLock(h);
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            if (SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h);   // 성공하면 클립보드가 가진다
        }

        const uint CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 2;
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
    }
}
