// 안 보이는 페이지의 다시 그리기를 미룬다 (2026-10-24, 잔렉 줄이기).
// 예: 블렌드쉐이프 페이지에서 메시를 고르면 머티리얼 페이지도 다시 그려야 하는데(0.3초), 보이지 않으니 그 순간에 하지 않고
//   ① 그 페이지가 보일 때(MainWindow.ShowPage → Flush) 또는 ② 입력이 잠시 멎었을 때(MainWindow.Warm.cs의 Idle) 한다.
// 같은 주인(패널)의 일은 마지막 것 하나만 남는다 (여러 번 바뀌어도 한 번만 그린다). 떼어 띄운 탭이 있으면 보이는 것으로 친다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace VrcProto.AvHost
{
    public static class Later
    {
        static readonly Dictionary<Control, Action> _q = new Dictionary<Control, Action>();

        /// <summary>주인이 보이면 바로, 아니면 미룬다.</summary>
        public static void Run(Control owner, Action work)
        {
            if (Visible(owner)) { _q.Remove(owner); work(); }
            else _q[owner] = work;
        }

        public static bool Pending => _q.Count > 0;

        static bool Visible(Control owner) => owner.IsEffectivelyVisible || SubTabs.Inner(owner).Any(st => st.HasFloated);

        /// <summary>이제 보이는 주인의 일을 모두 (페이지가 보일 때)</summary>
        public static void Flush()
        {
            foreach (var kv in _q.ToList())
                if (Visible(kv.Key) && _q.Remove(kv.Key)) kv.Value();
        }

        /// <summary>남은 일 하나 (입력이 멎었을 때 하나씩). 했으면 true</summary>
        public static bool RunOne()
        {
            if (_q.Count == 0) return false;
            var kv = _q.First();
            _q.Remove(kv.Key);
            kv.Value();
            return true;
        }

        /// <summary>문서를 새로 열면 미룬 일은 버린다 (페이지마다 새 문서로 다시 그린다)</summary>
        public static void Clear() => _q.Clear();
    }
}
