// 에디터 화면 글 바꾸기 (2026-10-19). 코드의 글(원문)은 그대로 두고, 글 표(texts/ko.tsv)에 "원문 → 바꿀 글"을 적으면 화면에 바꿔 보인다.
//   · 모든 TextBlock의 글이 정해질 때 한 곳에서 바꾼다 (단추·체크박스·메뉴·도움말·목록 칸·상태 표시줄도 결국 TextBlock으로 그려진다). 창 제목도.
//   · 표 한 줄: 원문 \t 바꿀 글 \t 메모. 바꿀 글이 비어 있으면 원문 그대로. \n·\t·\\ 는 줄바꿈·탭·역슬래시.
//   · 원문에 {0} {1} …이 있으면 틀이다 (코드의 $"…{값}…"). 바꿀 글에도 {0} {1}로 같은 값을 넣는다. 예: "프레임 {0} / {1}" → "{0}번째 프레임 (모두 {1})"
//   · 테스트 모드(--test)는 표 파일을 지켜보다 저장하면 바로 다시 읽어 열린 화면 글을 모두 다시 바꾼다.
// 화면 언어 (2026-11-02): 표는 언어마다 하나 — texts/ko.tsv(한국어 다듬기) · en.tsv(영어) · ja.tsv(일본어). 설정 "화면 › 언어"에서 바꾸면 바로 다시 바꾼다.
//   "" = Windows 언어를 따른다 (한국어·일본어가 아니면 영어). 원문이 한국어라 ko 표가 비어 있어도 한국어로 보인다.
// 사용자가 넣은 이름(메시·머티리얼 이름 등)이 표의 원문과 똑같으면 그것도 바뀐다 — 원문은 문장 단위로 적혀 있어 실제로는 거의 겹치지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public static class Texts
    {
        sealed class Orig { public string Text = ""; }
        sealed class Template { public string Prefix = ""; public string Suffix = ""; public Regex Re = null!; public string Repl = ""; public int Literal; public Regex? Open; }

        static Dictionary<string, string> _exact = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _memo = new Dictionary<string, string>();   // 틀로 찾은 결과 (표를 다시 읽으면 비운다)
        static List<Template> _templates = new List<Template>(), _openTemplates = new List<Template>();
        static List<KeyValuePair<string, string>> _heads = new List<KeyValuePair<string, string>>(), _tails = new List<KeyValuePair<string, string>>();
        static readonly ConditionalWeakTable<AvaloniaObject, Orig> _orig = new ConditionalWeakTable<AvaloniaObject, Orig>();
        static bool _busy, _hooked;
        static int _depth;
        static FileSystemWatcher? _watcher;
        static string? _folder;
        public static string? FilePath { get; private set; }
        /// <summary>지금 화면 언어 (ko · en · ja)</summary>
        public static string Lang { get; private set; } = "ko";
        public static readonly string[] Langs = { "ko", "en", "ja" };
        /// <summary>언어를 바꾼 뒤 (글꼴처럼 글 표 밖에서 맞출 것)</summary>
        public static event Action? LangChanged;
        public static int Count => _exact.Count + _templates.Count;
        /// <summary>끄면 원문으로 보인다 (테스트 모드에서 비교용).</summary>
        public static bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; Apply(); } }
        static bool _enabled = true;
        /// <summary>다시 읽은 뒤 (바꾼 줄 수, 오류).</summary>
        public static event Action<int, string?>? Reloaded;

        /// <summary>설정 값("" · ko · en · ja) → 실제 언어. ""는 Windows 화면 언어</summary>
        public static string Resolve(string? lang)
        {
            if (lang != null && Array.IndexOf(Langs, lang) >= 0) return lang;
            var sys = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return sys == "ko" || sys == "ja" ? sys : "en";
        }

        /// <summary>표 폴더(그 안의 ko.tsv · en.tsv · ja.tsv)를 정하고 화면 글 바꾸기를 건다. 파일이 없으면 아무것도 바꾸지 않는다 (훅은 걸어 둔다).</summary>
        public static void Init(string folder, string lang, bool watch)
        {
            _folder = folder;
            Lang = Resolve(lang);
            VrcProto.Host.Shared.LilLocalization.Lang = Lang;   // lilToon 프로퍼티 이름도 같은 언어로
            FilePath = Path.Combine(folder, Lang + ".tsv");
            Load();
            if (!_hooked)
            {
                _hooked = true;
                TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) => OnChanged(tb, TextBlock.TextProperty, e.NewValue as string));
                Window.TitleProperty.Changed.AddClassHandler<Window>((w, e) => OnChanged(w, Window.TitleProperty, e.NewValue as string));
            }
            if (watch && Directory.Exists(folder))
            {
                _watcher = new FileSystemWatcher(folder, "*.tsv") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };   // 편집기가 여러 번 써도 한 번만
                later.Tick += (_, __) => { later.Stop(); Reload(); };
                void Changed(object? s, FileSystemEventArgs a) => Dispatcher.UIThread.Post(() => { if (!string.Equals(a.FullPath, FilePath, StringComparison.OrdinalIgnoreCase)) return; later.Stop(); later.Start(); });
                _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Renamed += (s, a) => Changed(s, a);
                _watcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>표를 다시 읽고 지금까지 만든 화면 글을 모두 다시 바꾼다.</summary>
        public static void Reload()
        {
            string? err = Load();
            Apply();
            Reloaded?.Invoke(Count, err);
        }

        /// <summary>화면 언어를 바꾸고 지금까지 만든 화면 글을 모두 다시 바꾼다.</summary>
        public static void SetLang(string lang)
        {
            var l = Resolve(lang);
            if (l == Lang) return;
            Lang = l;
            VrcProto.Host.Shared.LilLocalization.Lang = l;
            if (_folder != null) FilePath = Path.Combine(_folder, l + ".tsv");
            Load();
            Apply();
            LangChanged?.Invoke();
        }

        static void Apply()
        {
            _busy = true;
            try
            {
                foreach (var kv in _orig)
                {
                    var t = T(kv.Value.Text);
                    if (kv.Key is TextBlock tb) tb.Text = t;
                    else if (kv.Key is Window w) w.Title = t;
                }
            }
            finally { _busy = false; }
        }

        static void OnChanged(AvaloniaObject o, AvaloniaProperty prop, string? s)
        {
            if (_busy) return;
            if (s == null) { _orig.Remove(o); return; }
            _orig.AddOrUpdate(o, new Orig { Text = s });
            if (Count == 0) return;
            var t = T(s);
            if (t == s) return;
            _busy = true;
            try { o.SetValue(prop, t); } finally { _busy = false; }
        }

        /// <summary>바꾸기 전 원문 (코드가 넣은 글). 바꾼 적 없으면 null</summary>
        public static string? Original(AvaloniaObject o) => _orig.TryGetValue(o, out var v) ? v.Text : null;

        /// <summary>원문 → 바꿀 글 (표에 없으면 원문).</summary>
        public static string T(string s)
        {
            // 표의 원문은 모두 한글을 품는다: 숫자·영문 값은 틀을 돌지 않게 바로 돌려보낸다 — 슬라이더를 끄는 동안 값 글이 계속 바뀐다 (2026-11-02)
            if (!_enabled || Count == 0 || s.Length == 0 || !HasHangul(s)) return s;
            if (_exact.TryGetValue(s, out var r)) return r;
            if (_memo.TryGetValue(s, out r)) return r;
            r = FromTemplates(s);
            if (_memo.Count >= 20000) _memo.Clear();   // 이름·값이 든 글이 끝없이 쌓이지 않게
            _memo[s] = r;
            return r;
        }

        static string FromTemplates(string s)
        {
            foreach (var t in _templates)
            {
                if (!s.StartsWith(t.Prefix, StringComparison.Ordinal) || !s.EndsWith(t.Suffix, StringComparison.Ordinal)) continue;
                var m = t.Re.Match(s);
                if (m.Success) return Fill(t, m);
            }
            // 코드가 + 로 이어 붙인 글 (2026-11-02): 표에 있는 앞 조각("저장하지 못했어요: " 등)·뒤 조각(" (실행 취소로 되돌려요)" 등)을 바꾸고 나머지는 다시 찾는다
            if (_depth >= 4) return s;
            foreach (var h in _heads)
                if (s.Length > h.Key.Length && s.StartsWith(h.Key, StringComparison.Ordinal) && (HeadEnd(h.Key[^1]) || TailStart(s[h.Key.Length])))
                    return h.Value + Value(s.Substring(h.Key.Length));
            foreach (var t in _openTemplates)
            {
                if (!s.StartsWith(t.Prefix, StringComparison.Ordinal)) continue;
                var m = t.Open!.Match(s);
                if (m.Success && m.Length < s.Length) return Fill(t, m) + Value(s.Substring(m.Length));
            }
            foreach (var k in _tails)
                if (s.Length > k.Key.Length && s.EndsWith(k.Key, StringComparison.Ordinal) && (TailStart(k.Key[0]) || HeadEnd(s[s.Length - k.Key.Length - 1])))
                    return Value(s.Substring(0, s.Length - k.Key.Length)) + k.Value;
            return s;
        }

        static string Fill(Template t, Match m) =>
            Regex.Replace(t.Repl, @"\{(\d+)\}", x => int.TryParse(x.Groups[1].Value, out var i) && i + 1 < m.Groups.Count ? Value(m.Groups[i + 1].Value) : x.Value);

        /// <summary>틀에 들어간 값·이어 붙인 나머지도 표로 바꾼다 — 코드가 이어 붙인 글("높이 약 1.2 m · " 같은)이 값 자리에 들어오기 때문 (2026-11-02, 네 겹까지)</summary>
        static string Value(string v)
        {
            if (_depth >= 4 || v.Length == 0) return v;
            _depth++;
            try { return T(v); } finally { _depth--; }
        }

        static bool HasHangul(string s)
        {
            foreach (var c in s) if ((c >= '가' && c <= '힣') || (c >= 'ㄱ' && c <= 'ㆎ')) return true;
            return false;
        }

        // 이어 붙인 글의 조각: 조각 끝(앞 조각)·처음(뒤 조각)이나 이어지는 글자가 띄어쓰기·문장부호일 때만 (낱말 중간에서 자르지 않게)
        static bool HeadEnd(char c) => c == ' ' || c == ':' || c == '·' || c == '\n' || c == '(';
        static bool TailStart(char c) => c == ' ' || c == '\n' || c == ',' || c == '.' || c == '(' || c == '·';

        static string? Load()
        {
            var exact = new Dictionary<string, string>();
            var templates = new List<Template>();
            string? err = null;
            if (FilePath != null && File.Exists(FilePath))
            {
                try
                {
                    int n = 0;
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        n++;
                        if (line.Length == 0 || line == "#" || line.StartsWith("# ")) continue;   // 주석은 "# " (원문이 #RRGGBB로 시작하는 줄이 있다)
                        var cols = line.Split('\t');
                        if (cols.Length < 2) continue;
                        var from = Unescape(cols[0]); var to = Unescape(cols[1]);
                        if (from.Length == 0 || to.Length == 0 || from == to) continue;
                        if (Regex.IsMatch(from, @"\{\d+\}")) templates.Add(MakeTemplate(from, to));
                        else exact[from] = to;
                    }
                }
                catch (Exception e) { err = e.Message; }
            }
            // 긴 틀이 먼저 (짧은 틀이 긴 문장을 먼저 삼키지 않게). 머리가 같으면 고정 글이 많은 틀이 먼저 ("{0} · …"처럼 값으로 시작하는 틀끼리)
            templates.Sort((a, b) => a.Prefix.Length != b.Prefix.Length ? b.Prefix.Length.CompareTo(a.Prefix.Length) : b.Literal.CompareTo(a.Literal));
            // 조각은 긴 것부터
            _heads = exact.Where(kv => kv.Key.Length >= 2 && HasHangul(kv.Key)).OrderByDescending(kv => kv.Key.Length).ToList();
            _tails = _heads;
            _openTemplates = templates.Where(t => t.Open != null).ToList();
            _exact = exact; _templates = templates; _memo.Clear();
            return err;
        }

        static Template MakeTemplate(string from, string to)
        {
            var parts = Regex.Split(from, @"\{\d+\}");
            var sb = new StringBuilder("^");
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append("(.*?)");
                sb.Append(Regex.Escape(parts[i]));
            }
            var last = parts[^1];
            // 끝이 띄어쓰기·문장부호인 틀("높이 약 {0} m · ")은 이어 붙인 글의 앞 조각으로도 쓴다
            var open = last.Length > 0 && HeadEnd(last[^1]) && HasHangul(from) ? new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant) : null;
            sb.Append('$');
            return new Template { Prefix = parts[0], Suffix = parts[parts.Length - 1], Re = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), Repl = to, Literal = parts.Sum(p => p.Length), Open = open };
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
            }
            return sb.ToString();
        }
    }
}
