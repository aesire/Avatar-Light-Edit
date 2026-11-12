// E2E "overlaygame" (2026-11-12): 방송 오버레이 "게임 화면과 합치기"·"OBS로 보내기" (렌더러 Overlay · WindowCapture · OverlaySupersample 합성)
//   E2E가 흰 바탕에 빨간 띠가 있는 "게임" 창을 띄우고, 렌더러 창을 게임 합치기로 켠다 → 렌더러 창 그림(PrintWindow)에서
//   ① 상자 밖은 게임 그림(흰색·빨간 띠) ② 오른쪽 아래 상자 안에 아바타(흰색이 아닌 픽셀) ③ 로그에 "게임 창 잡기 시작" ④ 자리를 바꾸면 상자도 옮겨 간다
//   ⑤ OBS 모드: 배경이 투명(알파 0 → 화면엔 검정)인 보통 창 ⑥ 끄면 렌더러는 살아 있고 게임 잡기 스레드가 끝난다.
// 실행: dotnet bin/E2E.dll <렌더러> <x.ir.json> <출력> overlaygame
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class E2E
{
    static int OverlayGameTest(PipeChannel ch, Func<string, int, JNode> wait, string ir, string outDir, Process proc)
    {
        IntPtr game = IntPtr.Zero;
        Thread? pump = null;
        try
        {
            ch.Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
            wait(Msg.SceneLoaded, 60000);
            ch.Send(Msg.Make(Msg.SetEnvironment).Set("providers", JNode.Arr().Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj().Set("preset", "bright")))));

            // "게임" 창: 흰 바탕, 위쪽 1/4에 빨간 띠 (자기 스레드에서 메시지를 돌린다)
            var ready = new ManualResetEventSlim();
            pump = new Thread(() =>
            {
                game = GameWin.Create(960, 540);
                ready.Set();
                GameWin.Pump();
            }) { IsBackground = true };
            pump.Start();
            ready.Wait(5000);
            Check(game != IntPtr.Zero, "test game window created");

            long hwnd = proc.MainWindowHandle.ToInt64();
            var unity = new IntPtr(hwnd);
            GameWin.Frame(unity, 120, 120, 960, 540, "ALE 방송 화면");
            JNode On(string mode, string corner) => Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd).Set("adjust", false).Set("supersample", 1f)
                .Set("mode", mode).Set("game", mode == "game" ? game.ToInt64() : 0).Set("box", JNode.Obj().Set("corner", corner).Set("size", 0.6f).Set("aspect", 0.75f));
            ch.Send(Msg.Make(Msg.SetCamera).Set("frameBone", "Hips").Set("size", 1.8));
            ch.Send(On("game", "br"));
            Thread.Sleep(2500);
            Check(!proc.HasExited, "renderer alive while compositing the game window");
            var shot = GameWin.Grab(unity);
            Check(shot != null, "grab composite window");
            if (shot != null)
            {
                var (w, h, px) = shot.Value;
                Save(Path.Combine(outDir, "overlaygame_br.png"), w, h, px);
                Console.WriteLine($"   composite window {w}×{h}");
                // ① 상자 밖 왼쪽 아래 (흰 바탕), 왼쪽 위 (빨간 띠)
                var white = Px(px, w, w / 10, h * 8 / 10); var red = Px(px, w, w / 10, h / 10);
                Console.WriteLine($"   left-bottom {white} · left-top {red}");
                Check(white.r > 220 && white.g > 220 && white.b > 220, $"game picture outside the avatar box (white {white})");
                Check(red.r > 180 && red.g < 80 && red.b < 80, $"game picture is not upside down (red band at the top {red})");
                // ② 상자 (오른쪽 아래, 높이 60%) 안에 흰색이 아닌 픽셀
                int notWhite = CountNotWhite(px, w, w * 6 / 10, h * 4 / 10, w, h);
                int leftNotWhite = CountNotWhite(px, w, 0, h * 4 / 10, w * 3 / 10, h);
                Console.WriteLine($"   non-white pixels: box {notWhite} · left {leftNotWhite}");
                Check(notWhite > 500 && leftNotWhite < 50, "avatar drawn inside the bottom-right box only");
            }
            // ④ 자리 바꾸기 → 왼쪽 아래
            ch.Send(On("game", "bl"));
            Thread.Sleep(800);
            var shot2 = GameWin.Grab(unity);
            if (shot2 != null)
            {
                var (w, h, px) = shot2.Value;
                Save(Path.Combine(outDir, "overlaygame_bl.png"), w, h, px);
                int left = CountNotWhite(px, w, 0, h * 4 / 10, w * 4 / 10, h), right = CountNotWhite(px, w, w * 7 / 10, h * 4 / 10, w, h);
                Console.WriteLine($"   after corner=bl non-white: left {left} · right {right}");
                Check(left > 500 && right < 50, "avatar box moves with the corner setting");
            }
            // ⑤ OBS 모드: 게임 그림 없이 검정(알파 0) 바탕
            ch.Send(On("obs", "br"));
            Thread.Sleep(1200);
            var shot3 = GameWin.Grab(unity);
            if (shot3 != null)
            {
                var (w, h, px) = shot3.Value;
                Save(Path.Combine(outDir, "overlay_obs.png"), w, h, px);
                var c = Px(px, w, 4, 4);
                Console.WriteLine($"   obs corner {c}");
                Check(c.r < 10 && c.g < 10 && c.b < 10, $"OBS window: transparent background shows black on screen ({c})");
            }
            ch.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", hwnd));
            Thread.Sleep(800);
            Check(!proc.HasExited, "renderer alive after turning the overlay off");
            ch.Send(Msg.Make(Msg.Quit));
            Check(proc.WaitForExit(15000), "renderer quits cleanly");
            var log = File.ReadAllText(Path.Combine(outDir, "renderer.log"));
            Check(log.Contains("게임 창 잡기 시작"), "window capture started (Windows.Graphics.Capture)");
            Check(log.Contains("게임 합성"), "composite present path ran");
            Check(!log.Contains("게임 창 잡기 실패"), "no capture errors");
        }
        catch (Exception e) { Check(false, "overlaygame: " + e.Message); }
        if (!proc.HasExited) proc.Kill();
        if (game != IntPtr.Zero) GameWin.Close(game);
        Console.WriteLine(_fail == 0 ? "E2E overlaygame: all ok" : $"E2E overlaygame: {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static (int r, int g, int b) Px(byte[] px, int w, int x, int y) { int i = (y * w + x) * 4; return (px[i], px[i + 1], px[i + 2]); }

    static int CountNotWhite(byte[] px, int w, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * w + x) * 4; if (px[i] < 200 || px[i + 1] < 200 || px[i + 2] < 200) n++; }
        return n;
    }

    static void Save(string path, int w, int h, byte[] rgba) => File.WriteAllBytes(path, Sticker.EncodePng(new Rgba(w, h, rgba), System.IO.Compression.CompressionLevel.Fastest));

    /// <summary>시험용 "게임" 창과 창 그림 잡기 (GDI).</summary>
    static class GameWin
    {
        static WndProc? _proc;   // GC가 거두지 않게
        public static IntPtr Create(int w, int h)
        {
            _proc = (hw, msg, wp, lp) =>
            {
                if (msg == 0x000F)   // WM_PAINT
                {
                    var dc = BeginPaint(hw, out var ps);
                    GetClientRect(hw, out var r);
                    var white = CreateSolidBrush(0xFFFFFF); var red = CreateSolidBrush(0x0000FF);
                    FillRect(dc, ref r, white);
                    var band = new RECT { Left = 0, Top = 0, Right = r.Right, Bottom = r.Bottom / 4 };
                    FillRect(dc, ref band, red);
                    DeleteObject(white); DeleteObject(red);
                    EndPaint(hw, ref ps);
                    return IntPtr.Zero;
                }
                return DefWindowProc(hw, msg, wp, lp);
            };
            var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "AleE2EGame", hInstance = GetModuleHandle(null) };
            RegisterClass(ref wc);
            var hwnd = CreateWindowEx(0, "AleE2EGame", "E2E 시험 게임", 0x10CF0000 /*OVERLAPPEDWINDOW|VISIBLE*/, 40, 40, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            return hwnd;
        }

        public static void Pump()
        {
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }

        public static void Close(IntPtr h) => PostMessage(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);

        /// <summary>렌더러 창을 Host처럼 제목 있는 보통 창으로 (UnityView.EnterOverlay와 같은 스타일).</summary>
        public static void Frame(IntPtr unity, int x, int y, int w, int h, string title)
        {
            int style = 0x00C00000 | 0x00040000 | 0x10000000, ex = 0x40000;
            SetWindowLongPtr(unity, -16, (IntPtr)style); SetWindowLongPtr(unity, -20, (IntPtr)ex);
            SetWindowText(unity, title);
            var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            AdjustWindowRectEx(ref r, style, false, ex);
            SetWindowPos(unity, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x20 | 0x40 | 0x04);
        }

        /// <summary>창 그림 칸을 RGBA로 (PrintWindow, PW_CLIENTONLY | PW_RENDERFULLCONTENT — DirectX 창도 잡힌다).</summary>
        public static (int w, int h, byte[] px)? Grab(IntPtr hwnd)
        {
            if (!GetClientRect(hwnd, out var r)) { Console.WriteLine($"   grab: no client rect 0x{hwnd.ToInt64():X}"); return null; }
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) { Console.WriteLine($"   grab: empty client {w}x{h}"); return null; }
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            var old = SelectObject(mem, dib);
            bool ok = PrintWindow(hwnd, mem, 1 | 2);
            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            if (!ok) { Console.WriteLine($"   grab: PrintWindow failed ({Marshal.GetLastWin32Error()})"); return null; }
            for (int i = 0; i < px.Length; i += 4) { (px[i], px[i + 2]) = (px[i + 2], px[i]); px[i + 3] = 255; }   // BGRA → RGBA
            return (w, h, px);
        }

        delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rc; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgb; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr h, string t);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int ex);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, int flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    }
}
