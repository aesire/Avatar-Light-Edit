// 전역 단축키 (2026-10-20): 게임에 포커스가 있어도 눌리는 단축키 (Win32 RegisterHotKey). 블렌드쉐이프 단축키(Core.ShapeKeys)가 쓴다.
// 전용 스레드 하나가 메시지 루프를 돌며 등록·해제를 하고(RegisterHotKey는 부른 스레드의 큐로 WM_HOTKEY를 보낸다), 눌리면 UI 스레드로 넘긴다.
// 다른 프로그램이 이미 쓰는 조합은 등록되지 않는다 → Failed에 남겨 화면에 알린다.
// 키 글: "Ctrl+Alt+Shift+Win+<키>" (<키> = Avalonia Key 이름: A~Z, D0~D9, NumPad0~9, F1~F24, Space 등). 화면에는 Text()로 ("Ctrl+Alt+1").
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public sealed class GlobalHotkeys : IDisposable
    {
        const int WM_HOTKEY = 0x0312, WM_APP = 0x8000, WM_QUIT = 0x0012;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        readonly Thread _thread;
        uint _threadId;
        readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        readonly object _lock = new object();
        List<string> _pending = new List<string>();
        List<string> _keys = new List<string>();   // 등록한 순서 = id
        public List<string> Failed { get; private set; } = new List<string>();

        /// <summary>눌린 키 글 (UI 스레드).</summary>
        public event Action<string>? Pressed;
        /// <summary>등록을 마쳤을 때 (실패한 키 목록, UI 스레드).</summary>
        public event Action<List<string>>? Registered;

        public GlobalHotkeys()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "GlobalHotkeys" };
            _thread.Start();
            _ready.Wait(2000);
        }

        /// <summary>등록할 키 목록을 통째로 바꾼다 (빈 글·같은 키는 하나로).</summary>
        public void Set(IEnumerable<string> keys)
        {
            lock (_lock) _pending = keys.Where(k => Parse(k).Vk != 0).Distinct().ToList();
            if (_threadId != 0) PostThreadMessage(_threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        void Loop()
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // 이 스레드의 메시지 큐 만들기
            _threadId = GetCurrentThreadId();
            _ready.Set();
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == WM_APP) Apply();
                else if (m.message == WM_HOTKEY)
                {
                    int id = m.wParam.ToInt32();
                    string? key; lock (_lock) key = id >= 0 && id < _keys.Count ? _keys[id] : null;
                    if (key != null) Dispatcher.UIThread.Post(() => Pressed?.Invoke(key));
                }
            }
            for (int i = 0; i < _keys.Count; i++) UnregisterHotKey(IntPtr.Zero, i);
        }

        void Apply()
        {
            List<string> want; lock (_lock) want = _pending;
            for (int i = 0; i < _keys.Count; i++) UnregisterHotKey(IntPtr.Zero, i);
            var failed = new List<string>();
            for (int i = 0; i < want.Count; i++)
            {
                var (mods, vk) = Parse(want[i]);
                if (!RegisterHotKey(IntPtr.Zero, i, mods | MOD_NOREPEAT, vk)) failed.Add(want[i]);
            }
            lock (_lock) { _keys = want; Failed = failed; }
            Dispatcher.UIThread.Post(() => Registered?.Invoke(failed));
        }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // ---------------- 키 글 ----------------
        /// <summary>키 입력을 키 글로 (조합 키만 누른 것은 null).</summary>
        public static string? FromKeyEvent(Key key, KeyModifiers mods)
        {
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return null;
            if (Vk(key) == 0) return null;
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>화면에 보일 글 ("Ctrl+Alt+1", "Num1").</summary>
        public static string Text(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return string.Join("+", key.Split('+').Select(p =>
                p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) ? p.Substring(1) :
                p.StartsWith("NumPad", StringComparison.Ordinal) ? "Num" + p.Substring(6) : p));
        }

        /// <summary>조합 키 없이 글자·숫자 하나만이면 게임 입력을 빼앗으므로 알린다.</summary>
        public static bool RisksGameInput(string key)
        {
            var (mods, vk) = Parse(key);
            bool fkey = vk >= 0x70 && vk <= 0x87, num = vk >= 0x60 && vk <= 0x6F;
            return mods == 0 && !fkey && !num;
        }

        static (uint Mods, uint Vk) Parse(string key)
        {
            if (string.IsNullOrEmpty(key)) return (0, 0);
            uint mods = 0; uint vk = 0;
            foreach (var p in key.Split('+'))
                switch (p)
                {
                    case "Ctrl": mods |= MOD_CONTROL; break;
                    case "Alt": mods |= MOD_ALT; break;
                    case "Shift": mods |= MOD_SHIFT; break;
                    case "Win": mods |= MOD_WIN; break;
                    default: if (Enum.TryParse<Key>(p, out var k)) vk = Vk(k); break;
                }
            return (mods, vk);
        }

        static uint Vk(Key k)
        {
            if (k >= Key.A && k <= Key.Z) return (uint)(0x41 + (k - Key.A));
            if (k >= Key.D0 && k <= Key.D9) return (uint)(0x30 + (k - Key.D0));
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return (uint)(0x60 + (k - Key.NumPad0));
            if (k >= Key.F1 && k <= Key.F24) return (uint)(0x70 + (k - Key.F1));
            switch (k)
            {
                case Key.Space: return 0x20;
                case Key.PageUp: return 0x21; case Key.PageDown: return 0x22; case Key.End: return 0x23; case Key.Home: return 0x24;
                case Key.Left: return 0x25; case Key.Up: return 0x26; case Key.Right: return 0x27; case Key.Down: return 0x28;
                case Key.Insert: return 0x2D; case Key.Delete: return 0x2E;
                case Key.Multiply: return 0x6A; case Key.Add: return 0x6B; case Key.Subtract: return 0x6D; case Key.Decimal: return 0x6E; case Key.Divide: return 0x6F;
                case Key.OemMinus: return 0xBD; case Key.OemPlus: return 0xBB; case Key.OemComma: return 0xBC; case Key.OemPeriod: return 0xBE;
                case Key.OemOpenBrackets: return 0xDB; case Key.OemCloseBrackets: return 0xDD; case Key.OemSemicolon: return 0xBA; case Key.OemQuotes: return 0xDE;
                default: return 0;
            }
        }
    }
}
