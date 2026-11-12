// 게임 창 잡기 (2026-11-12, 방송 오버레이 "게임 화면과 합치기"): 디스코드에서 게임 하나만 방송하면 그 게임 창만 잡혀서
// 위에 띄운 투명 오버레이 창이 보이지 않는다. 그래서 렌더러가 게임 창을 직접 잡아 배경으로 깔고 그 위에 아바타를 그린 창을 만든다
// (OverlaySupersample의 합성). 디스코드에서는 게임 대신 그 창("ALE 방송 화면")을 고른다.
//   Windows.Graphics.Capture (Windows 10 1903 이상): WinRT 투영이 없으므로 COM vtable을 직접 부른다 (아래 번호는 각 인터페이스의 메서드 순서)
//   자체 D3D11 장치에서 프레임 → 스테이징 텍스처 복사 → CPU 버퍼 (작업 스레드). 메인 스레드는 새 프레임이 있으면 Texture2D에 올린다.
//   Unity 장치를 같이 쓰지 않는 까닭: 렌더 스레드와 즉시 컨텍스트를 나눠 쓰면 위험하다. 1080p 30 fps면 복사·업로드가 몇 ms라 충분하다.
//   노란 테두리는 끌 수 있으면 끈다(Windows 11, IsBorderRequired), 커서는 잡지 않는다. 창이 최소화되면 마지막 그림이 남는다.
//   창 잡기는 제목 표시줄·테두리까지 들어오므로 그림 칸(클라이언트)만 잘라 쓴다 — 프레임은 DWM 보이는 테두리(EXTENDED_FRAME_BOUNDS) 기준.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class WindowCapture : IDisposable
    {
        public readonly long Hwnd;
        /// <summary>마지막으로 올린 게임 그림 (행이 위에서부터라 위아래가 뒤집혀 있다 — 합성 셰이더가 뒤집어 읽는다). 아직 없으면 null.</summary>
        public Texture2D Texture { get; private set; }
        /// <summary>실패했을 때 사람이 읽을 문구 (한 번 읽으면 지운다).</summary>
        public string TakeError() { var e = _error; _error = null; return e; }

        readonly Thread _thread;
        volatile bool _stop;
        volatile string _error;
        readonly object _lock = new object();
        IntPtr _buf; int _bufW, _bufH, _bufSize, _seq, _seen;

        public WindowCapture(long hwnd)
        {
            Hwnd = hwnd;
            _thread = new Thread(Run) { IsBackground = true, Name = "WindowCapture" };
            _thread.Start();
        }

        /// <summary>메인 스레드에서 매 프레임: 새 프레임이 있으면 텍스처에 올린다.</summary>
        public void Update()
        {
            lock (_lock)
            {
                if (_seq == _seen || _buf == IntPtr.Zero) return;
                _seen = _seq;
                if (Texture == null || Texture.width != _bufW || Texture.height != _bufH)
                {
                    if (Texture != null) UnityEngine.Object.Destroy(Texture);
                    Texture = new Texture2D(_bufW, _bufH, TextureFormat.BGRA32, false, false) { name = "GameCapture", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                }
                Texture.LoadRawTextureData(_buf, _bufSize);
                Texture.Apply(false);
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (!_thread.Join(2000)) Debug.LogWarning("[VrcProto] 게임 창 잡기 스레드가 늦게 끝남");
            lock (_lock) { if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; } }
            if (Texture != null) { UnityEngine.Object.Destroy(Texture); Texture = null; }
        }

        // ---------------- 작업 스레드 ----------------
        void Run()
        {
            IntPtr dev = IntPtr.Zero, ctx = IntPtr.Zero, dxgi = IntPtr.Zero, rtDev = IntPtr.Zero, rtDev2 = IntPtr.Zero, interop = IntPtr.Zero, item = IntPtr.Zero,
                statics = IntPtr.Zero, pool = IntPtr.Zero, session = IntPtr.Zero, staging = IntPtr.Zero;
            bool ro = false;
            try
            {
                ro = RoInitialize(1) >= 0;   // MTA
                try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }   // 창 좌표를 물리 픽셀로 (자르기 계산)
                if (!IsWindow(new IntPtr(Hwnd))) { _error = "고른 게임 창이 없어요. 창 목록을 새로 고쳐 다시 골라 주세요."; return; }
                Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20 /*BGRA*/, IntPtr.Zero, 0, 7, out dev, out _, out ctx), "D3D11 장치");
                Check(Marshal.QueryInterface(dev, ref IID_IDXGIDevice, out dxgi), "DXGI 장치");
                Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out rtDev), "WinRT 장치");
                Check(Marshal.QueryInterface(rtDev, ref IID_IDirect3DDevice, out rtDev2), "IDirect3DDevice");

                interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
                Check(Fn<CreateForWindowFn>(interop, 3)(interop, new IntPtr(Hwnd), ref IID_IGraphicsCaptureItem, out item), "창 잡기 항목");
                Check(Fn<GetSizeFn>(item, 7)(item, out var size), "창 크기");
                statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_IDirect3D11CaptureFramePoolStatics2);
                Check(Fn<CreateFreeThreadedFn>(statics, 6)(statics, rtDev2, PixelFormat, 2, size, out pool), "프레임 풀");
                Check(Fn<CreateSessionFn>(pool, 10)(pool, item, out session), "잡기 세션");
                SetBool(session, IID_IGraphicsCaptureSession2, false);   // 커서 빼기
                SetBool(session, IID_IGraphicsCaptureSession3, false);   // 노란 테두리 빼기 (Windows 11, 안 되면 그대로)
                Check(Fn<NoArgFn>(session, 6)(session), "잡기 시작");
                Debug.Log($"[VrcProto] 게임 창 잡기 시작: {size.W}×{size.H}");

                var poolSize = size;
                int stW = 0, stH = 0;
                while (!_stop)
                {
                    if (!IsWindow(new IntPtr(Hwnd))) { _error = "게임 창이 닫혔어요."; break; }
                    if (Fn<TryGetFrameFn>(pool, 7)(pool, out var frame) < 0 || frame == IntPtr.Zero) { Thread.Sleep(4); continue; }
                    try
                    {
                        Check(Fn<GetSizeFn>(frame, 8)(frame, out var content), "프레임 크기");
                        if (content.W > 0 && content.H > 0 && (content.W != poolSize.W || content.H != poolSize.H))
                        {
                            // 게임 창 크기가 바뀌었다 → 풀을 새 크기로 (이번 프레임은 버린다)
                            poolSize = content;
                            Fn<RecreateFn>(pool, 6)(pool, rtDev2, PixelFormat, 2, poolSize);
                            continue;
                        }
                        Check(Fn<GetPtrFn>(frame, 6)(frame, out var surface), "프레임 그림");
                        IntPtr access = IntPtr.Zero, tex = IntPtr.Zero;
                        try
                        {
                            Check(Marshal.QueryInterface(surface, ref IID_IDirect3DDxgiInterfaceAccess, out access), "DXGI 접근");
                            Check(Fn<GetInterfaceFn>(access, 3)(access, ref IID_ID3D11Texture2D, out tex), "D3D11 텍스처");
                            var desc = new D3D11_TEXTURE2D_DESC();
                            Fn<GetDescFn>(tex, 10)(tex, ref desc);
                            int w = (int)desc.Width, h = (int)desc.Height;
                            if (staging == IntPtr.Zero || stW != w || stH != h)
                            {
                                if (staging != IntPtr.Zero) { Marshal.Release(staging); staging = IntPtr.Zero; }
                                var sd = new D3D11_TEXTURE2D_DESC { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/ };
                                Check(Fn<CreateTexture2DFn>(dev, 5)(dev, ref sd, IntPtr.Zero, out staging), "복사용 텍스처");
                                stW = w; stH = h;
                            }
                            Fn<CopyResourceFn>(ctx, 47)(ctx, staging, tex);
                            Check(Fn<MapFn>(ctx, 14)(ctx, staging, 0, 1 /*READ*/, 0, out var mapped), "복사용 텍스처 읽기");
                            int cw = Math.Min(w, content.W > 0 ? content.W : w), ch = Math.Min(h, content.H > 0 ? content.H : h);
                            var (cx, cy, ccw, cch) = ClientCrop(cw, ch);
                            try { CopyOut(mapped, cx, cy, ccw, cch); }
                            finally { Fn<UnmapFn>(ctx, 15)(ctx, staging, 0); }
                        }
                        finally
                        {
                            if (tex != IntPtr.Zero) Marshal.Release(tex);
                            if (access != IntPtr.Zero) Marshal.Release(access);
                            Marshal.Release(surface);
                        }
                    }
                    finally
                    {
                        // 프레임을 닫아 풀에 돌려준다 (IClosable)
                        if (Marshal.QueryInterface(frame, ref IID_IClosable, out var cl) >= 0) { Fn<NoArgFn>(cl, 6)(cl); Marshal.Release(cl); }
                        Marshal.Release(frame);
                    }
                }
            }
            catch (Exception e)
            {
                _error = "게임 창을 잡지 못했어요: " + e.Message;
                Debug.LogWarning("[VrcProto] 게임 창 잡기 실패: " + e);
            }
            finally
            {
                if (session != IntPtr.Zero && Marshal.QueryInterface(session, ref IID_IClosable, out var sc) >= 0) { Fn<NoArgFn>(sc, 6)(sc); Marshal.Release(sc); }
                if (pool != IntPtr.Zero && Marshal.QueryInterface(pool, ref IID_IClosable, out var pc) >= 0) { Fn<NoArgFn>(pc, 6)(pc); Marshal.Release(pc); }
                foreach (var p in new[] { staging, session, pool, statics, item, interop, rtDev2, rtDev, dxgi, ctx, dev })
                    if (p != IntPtr.Zero) Marshal.Release(p);
                if (ro) RoUninitialize();
            }
        }

        /// <summary>프레임(w×h, 창의 보이는 테두리 기준) 안에서 그림 칸의 자리. 못 구하면 프레임 전체.</summary>
        (int X, int Y, int W, int H) ClientCrop(int w, int h)
        {
            var hw = new IntPtr(Hwnd);
            if (DwmGetWindowAttribute(hw, 9 /*EXTENDED_FRAME_BOUNDS*/, out var fr, 16) < 0 || !GetClientRect(hw, out var cr)) return (0, 0, w, h);
            var p = new POINT();
            if (!ClientToScreen(hw, ref p)) return (0, 0, w, h);
            int x = Math.Max(0, p.X - fr.Left), y = Math.Max(0, p.Y - fr.Top);
            int cw = Math.Min(cr.Right - cr.Left, w - x), ch = Math.Min(cr.Bottom - cr.Top, h - y);
            return cw >= 16 && ch >= 16 ? (x, y, cw, ch) : (0, 0, w, h);
        }

        unsafe void CopyOut(D3D11_MAPPED_SUBRESOURCE m, int x0, int y0, int w, int h)
        {
            lock (_lock)
            {
                int size = w * h * 4;
                if (_buf == IntPtr.Zero || _bufSize != size)
                {
                    if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                    _buf = Marshal.AllocHGlobal(size); _bufSize = size;
                }
                _bufW = w; _bufH = h;
                byte* src = (byte*)m.pData, dst = (byte*)_buf;
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(src + (long)(y + y0) * m.RowPitch + x0 * 4, dst + (long)y * w * 4, w * 4, w * 4);
                _seq++;
            }
        }

        static void Check(int hr, string what) { if (hr < 0) throw new Exception($"{what} (0x{hr:X8})"); }

        static IntPtr Factory(string cls, Guid iid)
        {
            Check(WindowsCreateString(cls, cls.Length, out var hs), "문자열");
            try { Check(RoGetActivationFactory(hs, ref iid, out var f), cls); return f; }
            finally { WindowsDeleteString(hs); }
        }

        static void SetBool(IntPtr obj, Guid iid, bool v)
        {
            if (Marshal.QueryInterface(obj, ref iid, out var p) < 0) return;
            try { Fn<PutBoolFn>(p, 7)(p, (byte)(v ? 1 : 0)); } finally { Marshal.Release(p); }
        }

        /// <summary>COM 객체의 vtable n번째 메서드.</summary>
        static T Fn<T>(IntPtr obj, int n) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), n * IntPtr.Size));

        const int PixelFormat = 87;   // DirectXPixelFormat.B8G8R8A8UIntNormalized

        [StructLayout(LayoutKind.Sequential)] struct SizeInt32 { public int W, H; }
        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_TEXTURE2D_DESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PutBoolFn(IntPtr self, byte v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateForWindowFn(IntPtr self, IntPtr hwnd, ref Guid iid, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSizeFn(IntPtr self, out SizeInt32 size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFreeThreadedFn(IntPtr self, IntPtr device, int format, int buffers, SizeInt32 size, out IntPtr pool);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RecreateFn(IntPtr self, IntPtr device, int format, int buffers, SizeInt32 size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSessionFn(IntPtr self, IntPtr item, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int TryGetFrameFn(IntPtr self, out IntPtr frame);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPtrFn(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInterfaceFn(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);

        static Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        static Guid IID_IDirect3DDevice = new Guid("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
        static Guid IID_IGraphicsCaptureItemInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        static Guid IID_IGraphicsCaptureItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        static Guid IID_IDirect3D11CaptureFramePoolStatics2 = new Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
        static Guid IID_IGraphicsCaptureSession2 = new Guid("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
        static Guid IID_IGraphicsCaptureSession3 = new Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
        static Guid IID_IDirect3DDxgiInterfaceAccess = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        static Guid IID_IClosable = new Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("combase.dll")] static extern int RoInitialize(int type);
        [DllImport("combase.dll")] static extern void RoUninitialize();
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int len, out IntPtr hs);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hs);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    }
}
