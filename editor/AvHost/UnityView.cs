// Unity Player를 자식 창으로 품는 NativeControlHost (-parentHWND).
// 빈 Win32 자식 창을 만들고 Unity가 -parentHWND로 그 안에 자기 창을 만든다. 크기는 레이아웃이 끝난 뒤 맞춘다.
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using VrcProto.Host.Shared;

namespace VrcProto.AvHost
{
    public sealed class UnityView : NativeControlHost, IUnityViewport
    {
        IntPtr _host, _unity;
        public IntPtr HostHandle => _host;
        public event Action? HandleReady;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _host = Win32.CreateWindowEx(0, "static", "", Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN,
                0, 0, 100, 100, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            // 레이아웃이 바뀌어 자식 창을 다시 만들었으면 (렌더러는 그대로) Unity 창을 새 자식 창으로 옮긴다. 오버레이 중이면 끌 때 붙인다
            if (_unity != IntPtr.Zero && Win32.IsWindow(_unity))
            {
                if (!_overlay) Dispatcher.UIThread.Post(() => Attach(), DispatcherPriority.Background);
                return new PlatformHandle(_host, "HWND");
            }
            Dispatcher.UIThread.Post(() => HandleReady?.Invoke());
            return new PlatformHandle(_host, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control) => Win32.DestroyWindow(control.Handle);

        public bool FindUnityWindow()
        {
            if (_overlay && Win32.IsWindow(_unity)) return true;   // 오버레이로 나가 있으면 자식 창 안에는 없다
            _unity = IntPtr.Zero;
            if (_host == IntPtr.Zero) return false;
            Win32.EnumChildWindows(_host, (h, _) => { _unity = h; return false; }, IntPtr.Zero);
            // 미리 띄운 렌더러(Prestart)의 Unity 창이 임시 부모에 있으면 이리로 옮긴다
            if (_unity == IntPtr.Zero && Prestart.ParkedChild() is var parked && parked != IntPtr.Zero) { _unity = parked; Attach(); Prestart.Done(); return true; }
            if (_unity != IntPtr.Zero) { FitUnity(); Activate(); }
            return _unity != IntPtr.Zero;
        }

        public void ForgetUnityWindow() { _unity = IntPtr.Zero; _overlay = false; }

        // ---------------- 방송 오버레이 ----------------
        // Unity 창을 이 자식 창에서 떼어 화면 위의 테두리 없는 창으로 띄운다 (투명 배경은 렌더러가 DWM으로 켠다). 끄면 다시 붙인다.
        // 상태는 여기 한 곳 (_overlay). OverlayController가 켜고 끄며, 렌더러가 죽으면 ForgetUnityWindow로 꺼진다.
        bool _overlay;
        public bool InOverlay => _overlay;
        public IntPtr UnityHandle => _unity;

        /// <summary>화면 좌표(물리 픽셀) 사각형에 띄운다. clickThrough면 마우스가 아래 창(게임)으로 간다.</summary>
        public bool EnterOverlay(int x, int y, int w, int h, bool topmost, bool clickThrough)
        {
            if (_unity == IntPtr.Zero || !Win32.IsWindow(_unity)) return false;
            if (!_overlay)
            {
                Win32.SetParent(_unity, IntPtr.Zero);
                Win32.SetWindowLong(_unity, Win32.GWL_STYLE, Win32.WS_POPUP | Win32.WS_VISIBLE);
                // 자식 창이던 동안 두 프로세스의 입력 큐가 묶여 있었다 → 풀어 둔다 (묶인 채면 마우스 잡기·포커스가 서로 넘어가 프로그램 창 클릭이 끊긴다)
                Win32.DetachInput(_unity);
                _overlay = true;
            }
            // 작업 표시줄·Alt+Tab에 나오지 않게(TOOLWINDOW). 클릭 통과일 때는 게임에서 포커스를 뺏지 않게(NOACTIVATE), 조정 모드는 클릭을 받아야 하므로 뺀다
            int ex = Win32.WS_EX_TOOLWINDOW | (clickThrough ? Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE : 0);
            Win32.SetWindowLong(_unity, Win32.GWL_EXSTYLE, ex);
            if (clickThrough) Win32.SetLayeredWindowAttributes(_unity, 0, 255, Win32.LWA_ALPHA);
            Win32.SetWindowPos(_unity, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, x, y, w, h, Win32.SWP_SHOWWINDOW | Win32.SWP_FRAMECHANGED | Win32.SWP_NOACTIVATE);
            return true;
        }

        /// <summary>미리보기로 되돌린다. 미리보기 자식 창이 없으면(닫는 중 등) 숨기기만 하고, 자식 창이 다시 생기면 붙인다.</summary>
        public void ExitOverlay()
        {
            if (!_overlay) return;
            _overlay = false;
            if (_unity == IntPtr.Zero || !Win32.IsWindow(_unity)) return;
            // 맨 위 표시는 스타일 값만 지워서는 남는다 → 먼저 풀고 붙인다
            Win32.SetWindowPos(_unity, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            Win32.SetWindowLong(_unity, Win32.GWL_EXSTYLE, 0);
            if (_host == IntPtr.Zero || !Win32.IsWindow(_host)) { Win32.ShowWindow(_unity, Win32.SW_HIDE); return; }
            Attach();
        }

        void Attach()
        {
            if (_unity == IntPtr.Zero || _host == IntPtr.Zero || _overlay) return;
            Win32.SetWindowLong(_unity, Win32.GWL_STYLE, Win32.WS_CHILD | Win32.WS_VISIBLE);
            Win32.SetParent(_unity, _host);
            Win32.SetWindowPos(_unity, IntPtr.Zero, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
            FitUnity(); Activate();
        }

        /// <summary>프로그램을 닫을 때: 오버레이 창이 화면에 남지 않게 바로 숨긴다 (렌더러는 곧 끝난다).</summary>
        public void HideOverlayNow()
        {
            if (_overlay && _unity != IntPtr.Zero) Win32.ShowWindow(_unity, Win32.SW_HIDE);
            _overlay = false;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            // NativeControlHost가 자식 창을 옮긴 뒤에 맞춘다
            if (change.Property == BoundsProperty) Dispatcher.UIThread.Post(FitUnity, DispatcherPriority.Background);
        }

        void FitUnity()
        {
            if (_unity == IntPtr.Zero || _host == IntPtr.Zero || _overlay) return;
            Win32.GetClientRect(_host, out var r);
            Win32.MoveWindow(_unity, 0, 0, r.Right - r.Left, r.Bottom - r.Top, true);
        }

        // 자식 창인 Unity에 "활성" 알림 (키보드·마우스를 받게). 오버레이로 나가 있으면 보내지 않는다 —
        // 따로 떠 있는 창이 활성 알림을 받으면 마우스를 가져가 프로그램 창의 단추 클릭(누르고 떼기)이 끊긴다
        public void Activate() { if (_unity != IntPtr.Zero && !_overlay) Win32.SendMessage(_unity, Win32.WM_ACTIVATE, (IntPtr)Win32.WA_ACTIVE, IntPtr.Zero); }
        public void Deactivate() { if (_unity != IntPtr.Zero && !_overlay) Win32.SendMessage(_unity, Win32.WM_ACTIVATE, (IntPtr)Win32.WA_INACTIVE, IntPtr.Zero); }
    }

    static class Win32
    {
        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        public const int WM_ACTIVATE = 0x0006, WA_ACTIVE = 1, WA_INACTIVE = 0;
        public const int WS_POPUP = unchecked((int)0x80000000), GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, LWA_ALPHA = 2;
        public const int SW_HIDE = 0, SWP_NOACTIVATE = 0x10;
        public const int SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        public static void SetWindowLong(IntPtr hwnd, int index, int value) => SetWindowLongPtr(hwnd, index, (IntPtr)(long)(uint)value);   // 스타일은 32비트 값 (부호 확장하지 않게)
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, int flags);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        public static void DetachInput(IntPtr other)
        {
            uint t = GetWindowThreadProcessId(other, out _), me = GetCurrentThreadId();
            if (t != 0 && t != me) { AttachThreadInput(me, t, false); AttachThreadInput(t, me, false); }
        }
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, int flags);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    }
}
