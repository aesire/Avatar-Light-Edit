// 떠 있는 창을 에디터 창의 자식 창으로 (2026-10-22).
// 디스코드·OBS의 "창" 공유는 고른 창 하나(와 그 자식 창)만 찍는다 — 따로 뜬 최상위 창(소유된 창이어도)은 빠져서 떠 있는 창이 상대에게 안 보였다.
// 떠 있는 창은 어차피 에디터 안쪽에만 있으므로, 보인 뒤 WS_CHILD로 바꿔 에디터 창 안에 넣는다 → 창 공유에도 함께 찍힌다.
//   · 자리: 자식 창은 부모 기준 좌표로 옮겨야 한다. Avalonia의 Position은 읽기는 화면 좌표지만 쓰기는 그대로 SetWindowPos라
//     자식이면 어긋난다 → 옮길 때는 MoveTo(화면 좌표)를 쓴다. 에디터를 옮기면 자식은 저절로 따라오므로 따로 옮기지 않는다.
//   · 포커스: 자식 창은 활성화되지 않으므로 누를 때 키보드 포커스를 직접 넘기고(SetFocus), 에디터를 누르면 돌려준다.
//   · 쌓는 순서: 미리보기(Unity 자식 창)보다 위 = 형제 중 맨 위(HWND_TOP). WS_CLIPSIBLINGS로 서로 덮어 그리지 않게.
// 설정 "떠 있는 창을 에디터 안에 넣기"(기본 켬)를 끄면 예전처럼 따로 뜬 창으로 둔다 (문제가 생길 때 되돌릴 길).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VrcProto.AvHost
{
    public static class Embed
    {
        /// <summary>새로 띄우는 창을 에디터 안에 넣을지 (Settings.EmbedFloats)</summary>
        public static bool Enabled = true;
        static readonly HashSet<Window> _attached = new HashSet<Window>();
        static readonly HashSet<Window> _hooked = new HashSet<Window>();

        public static bool IsAttached(Window w) => _attached.Contains(w);
        /// <summary>에디터 안에 넣은 창을 눌렀을 때 (앞으로 올린 뒤 — 핀 창을 다시 위로 쌓는 데 쓴다)</summary>
        public static event Action<Window>? Pressed;

        static IntPtr Hwnd(TopLevel w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        /// <summary>Show(owner) 뒤에 부른다: owner의 자식 창으로 바꾸고 맨 위로. 다시 보일 때(Show) 다시 불러도 된다.</summary>
        public static void Attach(Window w, Window owner)
        {
            if (!Enabled || !OperatingSystem.IsWindows()) return;
            IntPtr h = Hwnd(w), parent = Hwnd(owner);
            if (h == IntPtr.Zero || parent == IntPtr.Zero) return;
            GetWindowRect(h, out var r);
            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            long want = (style & ~WS_POPUP) | WS_CHILD | WS_CLIPSIBLINGS;
            if (style != want) SetWindowLongPtr(h, GWL_STYLE, new IntPtr(want));
            if (GetParent(h) != parent) SetParent(h, parent);
            // 화면 좌표였던 자리를 부모 기준으로
            var p = new POINT { X = r.Left, Y = r.Top };
            ScreenToClient(parent, ref p);
            SetWindowPos(h, IntPtr.Zero, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            _attached.Add(w);
            if (_hooked.Add(w))
            {
                // 누르면 키보드 포커스를 이 창으로 (자식 창은 활성화되지 않아 TextBox에 글을 칠 수 없었다)
                // 누르면 앞으로 (자식 창은 눌러도 순서가 바뀌지 않는다) + 키보드 포커스를 이 창으로 (활성화되지 않아 TextBox에 글을 칠 수 없었다)
                w.AddHandler(InputElement.PointerPressedEvent, (_, __) =>
                {
                    var hh = Hwnd(w);
                    if (hh == IntPtr.Zero || !_attached.Contains(w)) return;
                    ToTop(w); SetFocus(hh); Pressed?.Invoke(w);
                }, RoutingStrategies.Tunnel, handledEventsToo: true);
                w.Closed += (_, __) => { _attached.Remove(w); _hooked.Remove(w); };
            }
        }

        /// <summary>에디터 쪽을 눌렀을 때: 키보드 포커스가 떠 있는 창에 남아 있으면 에디터로 돌린다.</summary>
        public static void TakeFocusBack(Window owner)
        {
            if (_attached.Count == 0) return;
            var focus = GetFocus();
            foreach (var w in _attached)
                if (Hwnd(w) == focus) { SetFocus(Hwnd(owner)); return; }
        }

        /// <summary>키보드 포커스를 이 창으로 (자식 창은 활성화로 포커스를 받지 못한다). 따로 뜬 창이면 Activate</summary>
        public static void Focus(Window w)
        {
            if (!_attached.Contains(w)) { w.Activate(); return; }
            var h = Hwnd(w);
            if (h != IntPtr.Zero) SetFocus(h);
        }

        /// <summary>화면 좌표로 옮긴다 (자식이면 부모 기준으로 바꿔서).</summary>
        public static void MoveTo(Window w, PixelPoint screen)
        {
            if (!_attached.Contains(w)) { w.Position = screen; return; }
            var h = Hwnd(w); var parent = GetParent(h);
            var p = new POINT { X = screen.X, Y = screen.Y };
            ScreenToClient(parent, ref p);
            SetWindowPos(h, IntPtr.Zero, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>형제(미리보기·다른 떠 있는 창) 중 맨 위로</summary>
        public static void ToTop(Window w)
        {
            var h = Hwnd(w);
            if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        const int GWL_STYLE = -16;
        const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CLIPSIBLINGS = 0x04000000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
    }
}
