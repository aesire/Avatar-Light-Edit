// 블렌드쉐이프 목록 정리 (PLAN 3.2): 구분선 이름(=== Eye === 등)으로 묶고, 없으면 접두사로 묶는다.
// vrc.v_* (비셈)는 기본 숨김.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VrcProto.Host.Core
{
    public sealed class BlendShapeEntry
    {
        public int Index; public string Name = ""; public string Group = ""; public bool IsSeparator, HiddenByDefault;
    }

    public static class BlendShapeGroups
    {
        static readonly Regex Separator = new Regex(@"^\s*[=\-_*#~・+―ー<>\[\]【】]{2,}|[=\-_*#~・+―ー<>\[\]【】]{3,}\s*$", RegexOptions.Compiled);
        static readonly char[] Trim = "=-_*#~・+―ー<>[]【】 \t".ToCharArray();

        public static bool IsHiddenByDefault(string name) => name.StartsWith("vrc.v_", StringComparison.OrdinalIgnoreCase);

        public static List<BlendShapeEntry> Build(IReadOnlyList<string> names)
        {
            var list = new List<BlendShapeEntry>();
            bool hasSeparators = names.Any(n => Separator.IsMatch(n));
            string current = "";
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i];
                var e = new BlendShapeEntry { Index = i, Name = n, HiddenByDefault = IsHiddenByDefault(n) };
                if (hasSeparators && Separator.IsMatch(n))
                {
                    current = n.Trim(Trim);
                    if (current.Length == 0) current = n.Trim();
                    e.IsSeparator = true;
                    e.Group = current;
                }
                else if (hasSeparators) e.Group = current.Length > 0 ? current : "기타";
                else e.Group = PrefixOf(n);
                list.Add(e);
            }
            // 대소문자만 다른 묶음은 하나로 ("=== Eye ===" 와 "=== eye ===", 접두사 Eye_ 와 eye_). 이름은 먼저 나온 쪽
            var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
            {
                if (first.TryGetValue(e.Group, out var g)) e.Group = g;
                else first[e.Group] = e.Group;
            }
            if (!hasSeparators)
            {
                // 혼자인 접두사 그룹은 "기타"로 모은다
                var counts = list.GroupBy(e => e.Group).ToDictionary(g => g.Key, g => g.Count());
                foreach (var e in list) if (counts[e.Group] < 2) e.Group = "기타";
            }
            return list;
        }

        static string PrefixOf(string n)
        {
            if (n.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)) return "vrc";
            int cut = n.IndexOfAny(new[] { '_', '.', ' ', '-' });
            return cut > 0 ? n.Substring(0, cut) : "기타";
        }
    }
}
