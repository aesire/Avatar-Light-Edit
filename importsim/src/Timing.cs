// 가져오기 단계별 시간 (2026-10-21): 어디서 오래 걸리는지 찾기 위한 기록.
//   Timing.Step("이름") = 앞 Step 뒤로 걸린 시간을 남긴다. Timing.Report()로 글로 꺼낸다.
//   환경 변수 VRCPROTO_IMPORT_TIMING=1이면 단계마다 표준 오류로도 출력한다 (CLI·에디터 모두).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ImportSim
{
    public static class Timing
    {
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        static long _last;
        static readonly object Lock = new object();
        static readonly bool Echo = Environment.GetEnvironmentVariable("VRCPROTO_IMPORT_TIMING") == "1";
        public static readonly List<(string Name, long Ms)> Steps = new List<(string, long)>();

        /// <summary>기록을 비우고 지금부터 잰다.</summary>
        public static void Start()
        {
            lock (Lock) { Steps.Clear(); _last = Sw.ElapsedMilliseconds; }
        }

        public static void Step(string name)
        {
            lock (Lock)
            {
                long now = Sw.ElapsedMilliseconds, ms = now - _last;
                _last = now;
                Steps.Add((name, ms));
                if (Echo) Console.Error.WriteLine($"[timing] {name}: {ms} ms");
            }
        }

        /// <summary>단계 안의 세부 시간 (병렬 작업 하나하나 등). 합계에는 넣지 않고 출력만 한다.</summary>
        public static void Detail(string name, long ms)
        {
            if (Echo) lock (Lock) Console.Error.WriteLine($"[timing]   · {name}: {ms} ms");
        }

        public static string Report()
        {
            lock (Lock) return string.Join("\n", Steps.Select(s => $"{s.Name}: {s.Ms} ms")) + $"\n합계: {Steps.Sum(s => s.Ms)} ms";
        }
    }
}
