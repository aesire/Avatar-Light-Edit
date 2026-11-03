// 블렌드쉐이프 이름의 구글 번역 (2026-10-23 사용자 요청): 번역 표(Host/Core/ShapeNameTr.cs)로 다 옮기지 못한 이름만, 블렌드쉐이프 페이지에서 "구글 번역"을 켰을 때만 보낸다.
// 공식 Cloud Translation API v2 (2026-10-27 사용자 결정 — 예전엔 키 없는 비공식 translate.googleapis.com client=gtx). 키는 사용자가 설정 창에 넣는다(settings.GoogleTranslateKey).
// 키가 없거나 실패하면 표 결과 그대로 보인다.
// 보내는 것은 블렌드쉐이프 이름뿐이다. 결과는 설정 폴더 name-translations.json { "ko": { 원본: 번역 }, "en": {…} } 에 모아 두고 다시 묻지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public static class MachineNames
    {
        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ALEdit");
            return h;
        }

        static readonly object Gate = new object();
        static Dictionary<string, Dictionary<string, string>>? _cache;
        static readonly HashSet<string> Pending = new HashSet<string>();
        static bool _offline;   // 한 번 연결에 실패하면 이번 실행 동안은 다시 묻지 않는다 (켜고 끄면 다시)

        /// <summary>Cloud Translation API 키 (BlendShapePanel이 설정 값으로 이어 둔다). 비어 있으면 묻지 않는다</summary>
        public static Func<string> ApiKey = () => "";
        public static bool HasKey => ApiKey().Trim().Length > 0;

        static string FilePath => Path.Combine(Settings.Folder, "name-translations.json");

        static Dictionary<string, Dictionary<string, string>> Cache()
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, Dictionary<string, string>>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var lang in JNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)).Props)
                        _cache[lang.Key] = lang.Value.Props.ToDictionary(p => p.Key, p => p.Value.AsString);
            }
            catch (Exception) { _cache.Clear(); }   // 깨진 파일: 처음부터 다시 모은다
            return _cache;
        }

        /// <summary>모아 둔 번역 (없으면 null).</summary>
        public static string? Lookup(string name, string lang)
        {
            lock (Gate) return Cache().TryGetValue(lang, out var d) && d.TryGetValue(name, out var t) ? t : null;
        }

        /// <summary>"구글 번역"을 다시 켜면 실패 표시를 지우고 다시 묻는다.</summary>
        public static void Retry() { lock (Gate) _offline = false; }

        /// <summary>
        /// 아직 모르는 이름을 구글 번역에 묻는다 (40개씩 한 번에, 키가 없으면 오류 글). 돌려주는 값: 새로 옮긴 개수, 실패하면 오류 글.
        /// 이미 묻는 중이거나 모아 둔 이름은 다시 보내지 않는다.
        /// </summary>
        public static async Task<(int Added, string? Error)> Fetch(IEnumerable<string> names, string lang)
        {
            List<string> todo;
            lock (Gate)
            {
                if (_offline) return (0, null);
                if (!HasKey) { _offline = true; return (0, "구글 번역을 쓰려면 설정 › 블렌드쉐이프 이름 번역에 Cloud Translation API 키를 넣어 주세요. 지금은 번역 표로만 보여요."); }
                var known = Cache().TryGetValue(lang, out var d) ? d : null;
                todo = names.Distinct().Where(n => (known == null || !known.ContainsKey(n)) && Pending.Add(lang + "\u001f" + n)).ToList();
            }
            if (todo.Count == 0) return (0, null);
            int added = 0;
            string? error = null;
            try
            {
                // 원문 언어를 나눠 보낸다: 일본어와 영어 이름을 한 번에 보내면 구글이 한 언어로 짐작해 나머지를 옮기지 않는다
                var groups = todo.GroupBy(n => n.Any(IsJp) ? "ja" : "en").SelectMany(g => Batches(g.ToList()).Select(b => (From: g.Key, Names: b))).ToList();
                foreach (var (from, batch) in groups)
                {
                    var (got, err) = await Request(batch, from, lang);
                    if (got == null) { error = err; lock (Gate) _offline = true; break; }
                    lock (Gate)
                    {
                        var c = Cache();
                        if (!c.TryGetValue(lang, out var d)) c[lang] = d = new Dictionary<string, string>();
                        foreach (var (n, t) in got) { d[n] = t; added++; }
                    }
                }
                if (added > 0) Save();
            }
            finally { lock (Gate) foreach (var n in todo) Pending.Remove(lang + "\u001f" + n); }
            return (added, error);
        }

        static IEnumerable<List<string>> Batches(List<string> names)
        {
            var b = new List<string>(); int len = 0;
            foreach (var n in names)
            {
                if (b.Count > 0 && (b.Count >= 40 || len + n.Length > 1500)) { yield return b; b = new List<string>(); len = 0; }
                b.Add(n); len += n.Length + 1;
            }
            if (b.Count > 0) yield return b;
        }

        static bool IsJp(char c) => (c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿') || (c >= 'ｦ' && c <= 'ﾟ');

        /// <summary>
        /// 한 묶음을 한 번에 묻는다 (q 배열 — 결과도 같은 순서). 429·5xx는 세 번까지 기다렸다 다시 한다.
        /// 끝내 안 되면 (null, 오류 글): 키 문제(400·401·403)와 연결 문제를 나눠 알린다.
        /// </summary>
        static async Task<(List<(string, string)>? Got, string Error)> Request(List<string> names, string from, string lang)
        {
            const string Net = "구글 번역에 연결하지 못했어요 (인터넷 연결을 확인하거나 나중에 다시 켜 주세요). 지금은 번역 표로만 보여요.";
            var url = "https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(ApiKey().Trim());
            var q = JNode.Arr();
            foreach (var n in names) q.Add(JNode.From(Prepare(n)));
            var json = JNode.Obj().Set("q", q).Set("source", from).Set("target", lang).Set("format", "text").ToJson(false);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000 * attempt);
                try
                {
                    using var body = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await Http.PostAsync(url, body);
                    int code = (int)resp.StatusCode;
                    if (code == 429 || code >= 500) continue;
                    if (code is 400 or 401 or 403) return (null, "구글 번역 API 키가 맞지 않거나 Cloud Translation API가 켜져 있지 않아요 (설정 › 블렌드쉐이프 이름 번역). 지금은 번역 표로만 보여요.");
                    if (!resp.IsSuccessStatusCode) return (null, Net);
                    // { "data": { "translations": [ { "translatedText": … }, … ] } } — 보낸 순서대로
                    var tr = JNode.Parse(await resp.Content.ReadAsStringAsync())["data"]["translations"].Items.ToList();
                    if (tr.Count != names.Count) return (null, Net);
                    var res = new List<(string, string)>();
                    for (int i = 0; i < names.Count; i++)
                    {
                        var t = System.Net.WebUtility.HtmlDecode(tr[i]["translatedText"].AsString).Trim();
                        if (t.Length > 0) res.Add((names[i], t));
                    }
                    return (res, "");
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }   // 시간 초과
                catch (Exception) { return (null, Net); }  // 응답 모양이 다름
            }
            return (null, Net);
        }

        /// <summary>보내기 전에 밑줄·점·camelCase를 띄어 쓴다 (eye_blink_L → "eye blink L", EyeLookUp → "Eye Look Up")</summary>
        static string Prepare(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c == '_' || c == '.') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static void Save()
        {
            try
            {
                JNode root = JNode.Obj();
                lock (Gate)
                    foreach (var (lang, d) in Cache())
                    {
                        var o = JNode.Obj();
                        foreach (var (k, v) in d.OrderBy(x => x.Key, StringComparer.Ordinal)) o.Set(k, v);
                        root.Set(lang, o);
                    }
                Directory.CreateDirectory(Settings.Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToJson(true), new UTF8Encoding(false));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception) { }   // 다음에 다시 물으면 된다
        }
    }
}
