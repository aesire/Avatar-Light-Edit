// 블렌드쉐이프 좌우 짝 (2026-10-21, 사용자 요청): "기능"·"기능L"·"기능R"처럼 한 표정이 양쪽·왼쪽·오른쪽 세 개로 나뉜 것을 찾는다.
// 블렌드쉐이프 페이지의 "L·R로 나누기"(양쪽 값을 왼쪽·오른쪽으로 옮기고 양쪽은 원본으로)와 "L·R 합치기"(되돌리기)가 쓴다.
// 붙는 글자: _L/_R · .L/.R · -L/-R · " L"/" R" · L/R · _Left/_Right · Left/Right · _left/_right · 左/右 · _左/_右. 앞쪽 짝을 먼저 본다.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcProto.Host.Core
{
    public static class BlendShapeSides
    {
        public sealed record Side(string Base, string Left, string Right);

        static readonly (string L, string R)[] Suffixes =
        {
            ("_L", "_R"), (".L", ".R"), ("-L", "-R"), (" L", " R"), ("_l", "_r"), ("_Left", "_Right"), ("_left", "_right"),
            ("Left", "Right"), ("L", "R"), ("_左", "_右"), ("左", "右"),
        };

        /// <summary>양쪽·왼쪽·오른쪽 셋이 모두 있는 것 (이름 차례대로). 한 이름은 한 짝에만 든다.</summary>
        public static List<Side> Find(IReadOnlyList<string> names)
        {
            var set = new HashSet<string>(names);
            var used = new HashSet<string>();
            var list = new List<Side>();
            foreach (var b in names)
            {
                if (used.Contains(b)) continue;
                foreach (var (sl, sr) in Suffixes)
                {
                    string l = b + sl, r = b + sr;
                    if (!set.Contains(l) || !set.Contains(r) || used.Contains(l) || used.Contains(r)) continue;
                    list.Add(new Side(b, l, r));
                    used.Add(b); used.Add(l); used.Add(r);
                    break;
                }
            }
            return list;
        }

        /// <summary>나눌 것: 양쪽 값이 원본과 다른 짝 (only가 있으면 그중 셋 중 하나라도 든 짝만).</summary>
        public static List<Side> SplitCandidates(IEnumerable<Side> sides, Func<string, bool> modified, ISet<string>? only = null)
            => sides.Where(s => modified(s.Base) && (only == null || only.Count == 0 || only.Contains(s.Base) || only.Contains(s.Left) || only.Contains(s.Right))).ToList();

        /// <summary>합칠 것: 양쪽은 원본 그대로이고 왼쪽·오른쪽이 같은 값으로 바뀐 짝. 값이 서로 다른 짝은 differ로.</summary>
        public static List<Side> MergeCandidates(IEnumerable<Side> sides, Func<string, bool> modified, Func<string, double> value, out List<Side> differ, ISet<string>? only = null)
        {
            differ = new List<Side>();
            var list = new List<Side>();
            foreach (var s in sides)
            {
                if (only != null && only.Count > 0 && !only.Contains(s.Base) && !only.Contains(s.Left) && !only.Contains(s.Right)) continue;
                if (modified(s.Base) || !modified(s.Left) || !modified(s.Right)) continue;
                if (Math.Abs(value(s.Left) - value(s.Right)) < 0.01) list.Add(s); else differ.Add(s);
            }
            return list;
        }
    }
}
