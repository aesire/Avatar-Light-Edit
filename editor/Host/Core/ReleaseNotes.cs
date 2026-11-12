// 릴리스 글에서 화면 언어에 맞는 부분만 고르기 (2026-11-12): 업데이트 창은 GitHub 릴리스 글을 그대로 보여 주는데,
// 글이 한국어뿐이면 영어·일본어 화면에서도 한국어로 나온다. 릴리스 글을 "## 한국어" · "## English" · "## 日本語" 제목으로 나눠 적으면
// 그 언어 부분만 보여 준다. 제목이 없는 글(0.1.0 · 0.1.1)은 통째로. 맞는 언어가 없으면 영어, 그것도 없으면 첫 부분.
// 언어 제목 앞의 글(sha256 등 모든 언어가 함께 보는 줄)은 고른 부분 위에 붙인다.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcProto.Host.Core
{
    public static class ReleaseNotes
    {
        /// <summary>제목 줄("## English" 등)이 가리키는 언어. 언어 제목이 아니면 null.</summary>
        static string? LangOf(string line)
        {
            var t = line.Trim();
            if (!t.StartsWith("#")) return null;
            t = t.TrimStart('#').Trim().Trim('*', '_', ':', ' ').ToLowerInvariant();
            return t switch
            {
                "한국어" or "korean" or "ko" => "ko",
                "english" or "영어" or "en" => "en",
                "日本語" or "japanese" or "일본어" or "ja" => "ja",
                _ => null,
            };
        }

        /// <summary>릴리스 글에서 lang("ko"·"en"·"ja") 부분. 언어 제목이 없으면 글 그대로.</summary>
        public static string ForLang(string? body, string lang)
        {
            var text = (body ?? "").Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var common = new List<string>();
            var parts = new List<(string Lang, List<string> Lines)>();
            foreach (var l in lines)
            {
                var k = LangOf(l);
                if (k != null) { parts.Add((k, new List<string>())); continue; }
                (parts.Count == 0 ? common : parts[^1].Lines).Add(l);
            }
            if (parts.Count == 0) return text.Trim();
            int at = parts.FindIndex(p => p.Lang == lang);
            if (at < 0) at = parts.FindIndex(p => p.Lang == "en");
            if (at < 0) at = 0;
            var head = string.Join("\n", common).Trim();
            var pick = string.Join("\n", parts[at].Lines).Trim();
            return head.Length == 0 ? pick : pick.Length == 0 ? head : head + "\n\n" + pick;
        }
    }
}
