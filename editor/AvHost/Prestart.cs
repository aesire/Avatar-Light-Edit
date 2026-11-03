// 렌더러 미리 띄우기 (2026-10-22, 성능 개선): 에디터 창이 뜬 뒤에 Unity 렌더러를 켜면 창이 뜨는 1.5초를 그냥 기다렸다.
// 프로그램이 켜지자마자(Main, Avalonia 준비 전) 보이지 않는 임시 부모 창(따로 스레드에서 메시지를 돌린다)에 렌더러를 띄워 두고,
// 미리보기 자식 창(UnityView)이 생기면 Unity 창을 그 안으로 옮긴다 — 미리보기 자식 창을 다시 만들 때 옮기던 것과 같은 길.
//   · 그동안 온 렌더러 메시지(ready 등)는 쌓아 두었다가 MainWindow가 받을 준비가 되면(Go) 차례대로 UI 스레드로 넘긴다.
//   · 렌더러 실행 파일이 없거나 Windows가 아니면 아무것도 하지 않는다 (MainWindow가 예전처럼 띄운다).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VrcProto.Host.Shared;

namespace VrcProto.AvHost
{
    public static class Prestart
    {
        static RenderHost? _host;
        static IntPtr _park;
        static uint _parkThread;
        static readonly object _lock = new object();
        static readonly List<Action> _queue = new List<Action>();
        static Action<Action>? _real;

        public static void Begin()
        {
            if (!OperatingSystem.IsWindows()) return;
            var exe = RenderHost.FindRenderer();
            if (exe == null) return;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                _parkThread = GetCurrentThreadId();
                // 보이지 않는 임시 부모 (크기는 흔한 미리보기 크기 — 옮긴 뒤 UnityView가 맞춘다)
                _park = CreateWindowEx(0, "static", "VrcProto renderer park", WS_POPUP | WS_CLIPCHILDREN, 0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                ready.Set();
                while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
            }) { IsBackground = true, Name = "renderer park" };
            t.Start();
            ready.Wait(2000);
            if (_park == IntPtr.Zero) return;
            _host = new RenderHost(Post, new ParkView(), exe);
            _host.Start();
        }

        /// <summary>지금 처리 중인 렌더러 메시지가 실제로 온 시각 (쌓였다가 넘어온 것이면 쌓인 때) — 시작 시간 재기용, 처리 중이 아니면 default</summary>
        public static DateTime PostedAt { get; private set; }

        static void Post(Action inner)
        {
            var at = DateTime.Now;
            Action a = () => { PostedAt = at; try { inner(); } finally { PostedAt = default; } };
            Action<Action>? real;
            lock (_lock) { real = _real; if (real == null) { _queue.Add(a); return; } }
            real(a);
        }

        /// <summary>미리 띄운 렌더러를 넘겨받는다 (없으면 null). 받은 쪽이 이벤트를 단 뒤 Go를 불러야 쌓인 메시지가 흐른다</summary>
        public static RenderHost? Take(IUnityViewport view)
        {
            var h = _host; _host = null;
            h?.Rebind(Post, view);
            return h;
        }

        /// <summary>이제 UI 스레드로 넘긴다: 쌓인 것부터 차례로</summary>
        public static void Go(Action<Action> postToUi)
        {
            List<Action> q;
            lock (_lock) { _real = postToUi; q = new List<Action>(_queue); _queue.Clear(); }
            foreach (var a in q) postToUi(a);
        }

        /// <summary>임시 부모 안의 Unity 창 (없으면 0)</summary>
        public static IntPtr ParkedChild()
        {
            if (_park == IntPtr.Zero) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(_park, (h, _) => { found = h; return false; }, IntPtr.Zero);
            return found;
        }

        /// <summary>Unity 창을 옮긴 뒤 임시 부모를 없앤다 (메시지 스레드도 끝난다)</summary>
        public static void Done()
        {
            if (_park == IntPtr.Zero) return;
            PostMessage(_park, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);   // static 창은 WM_CLOSE에 DestroyWindow
            PostThreadMessage(_parkThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _park = IntPtr.Zero;
        }

        /// <summary>창을 넘겨받지 못하고 끝날 때 (시작 중 오류): 렌더러를 닫는다</summary>
        public static void Dispose() { _host?.Dispose(); _host = null; Done(); }

        sealed class ParkView : IUnityViewport
        {
            public IntPtr HostHandle => _park;
            public bool FindUnityWindow() => ParkedChild() != IntPtr.Zero;
            public void ForgetUnityWindow() { }
        }

        const int WS_POPUP = unchecked((int)0x80000000), WS_CLIPCHILDREN = 0x02000000;
        const int WM_CLOSE = 0x0010, WM_QUIT = 0x0012;
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    }
}
