// 업데이트가 남기는 파일 치우기 (2026-11-12): 업데이트는 새 파일을 덮어쓰기만 해서, 새 판에서 빠진 파일이 프로그램 폴더에 남았다.
// 배포 zip에 파일 목록(files.txt, 한 줄에 상대 경로 하나)을 넣어 두고, "지금 판의 목록에는 있는데 새 판의 목록에는 없는 파일"만 지운다.
// 목록에 없는 파일(사용자가 넣은 셰이더 팩 등)은 건드리지 않는다. 지금 판에 목록이 없으면(0.1.1까지) 아무것도 지우지 않는다.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcProto.Host.Core
{
    public static class UpdateFiles
    {
        public const string ListName = "files.txt";

        /// <summary>목록 한 줄을 프로그램 폴더 안의 상대 경로로 (역슬래시). 폴더 밖을 가리키거나 이상하면 null.</summary>
        public static string? Clean(string? line)
        {
            var s = (line ?? "").Trim().Replace('/', '\\');
            if (s.Length == 0 || s.StartsWith("#") || s.StartsWith("\\") || s.Contains(':')) return null;
            var parts = s.Split('\\');
            if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(" ") || p.EndsWith("."))) return null;
            if (s.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', '%', '&', '^' }) >= 0) return null;   // 배치 파일에 그대로 적는다
            return s;
        }

        /// <summary>지울 파일: 옛 목록에만 있는 것. 새 목록이 비어 있으면(목록 없는 판) 아무것도 지우지 않는다.</summary>
        public static List<string> Stale(IEnumerable<string>? oldList, IEnumerable<string>? newList)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in newList ?? Array.Empty<string>()) if (Clean(l) is string c) keep.Add(c);
            var gone = new List<string>();
            if (keep.Count == 0) return gone;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in oldList ?? Array.Empty<string>())
                if (Clean(l) is string c && !keep.Contains(c) && seen.Add(c)) gone.Add(c);
            return gone;
        }
    }
}
