// 블렌드쉐이프 이름 번역 (2026-10-23 사용자 요청): 일본어 쉐이프키 이름(あ·まばたき·ウィンク右·笑い·怒り·困る…)을 화면에서 한국어·영어로 보여 준다.
// 화면 표시·검색 전용이다. 원본 이름은 바꾸지 않는다 — Changeset 주소·올리기·내보내기·즐겨찾기는 모두 원본 이름을 쓴다.
// 이 파일의 표가 먼저다:
//   1. 이름 전체가 Whole 표에 있으면 그것 (MMD 표준 모프 등 자주 쓰는 이름)
//   2. 아니면 글자 종류(일본어·영문·숫자·기호)로 나눠 조각마다 옮긴다
//      일본어 조각: 조각 전체가 표에 있으면 그것, 아니면 Parts 표에서 가장 긴 것부터 맞춰 옮긴다 ("笑い目" → "웃음 눈" / "Smile Eye")
//      영문 조각 (한국어로): 단어(밑줄·camelCase로 나눔)가 모두 표에 있을 때만 옮긴다 (Shrink_upper_arm처럼 반만 옮기면 더 헷갈린다)
//      영문 조각 (영어로): 일본어 로마자(nagomi·jitome·tsuri…)만 옮기고 나머지 영어 낱말은 그대로
//   3. 표로 다 옮기지 못한 이름(Complete = false)은 기계 번역 결과가 있으면 그것 (Machine — AvHost/MachineNames.cs, 구글 번역을 켰을 때만)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VrcProto.Host.Core
{
    public static class ShapeNameTr
    {
        public const string Ko = "ko", En = "en";

        /// <summary>표로 다 옮기지 못한 이름의 기계 번역 (이름, 언어) → 결과 또는 null. 없으면 표 결과만.</summary>
        public static Func<string, string, string?>? Machine;

        static readonly ConcurrentDictionary<(string, string), (string Text, bool Complete)> Cache = new ConcurrentDictionary<(string, string), (string, bool)>();

        /// <summary>화면에 보일 이름. lang = "ko" · "en" (그 밖 = 원본). 옮길 것이 없으면 원본 그대로, vrc.로 시작하는 이름도 그대로.</summary>
        public static string Tr(string name, string lang)
        {
            if (!IsLang(lang) || string.IsNullOrEmpty(name) || name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)) return name;
            var t = Table(name, lang);
            if (!t.Complete && Machine?.Invoke(name, lang) is string m && m.Length > 0) return m;
            return t.Text;
        }

        /// <summary>표만으로 옮긴 결과 (기계 번역 없이).</summary>
        public static string TableOnly(string name, string lang) =>
            !IsLang(lang) || string.IsNullOrEmpty(name) || name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) ? name : Table(name, lang).Text;

        /// <summary>표로 다 옮기지 못해 기계 번역에 맡길 이름인지 (일본어가 남았거나, 한국어로 옮길 때 영문 이름을 옮기지 못함).</summary>
        public static bool NeedsMachine(string name, string lang)
        {
            if (!IsLang(lang) || string.IsNullOrWhiteSpace(name) || name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Norm(name).Any(c => IsJp(c) || (c < 128 && char.IsLetter(c)))) return false;   // 숫자·기호뿐
            return !Table(name, lang).Complete;
        }

        /// <summary>검색: 원본 이름이나 옮긴 이름 어느 쪽에 들어 있어도 걸린다 (옮긴 이름은 띄어쓰기를 무시).</summary>
        public static bool Matches(string name, string query, string lang)
        {
            if (query.Length == 0 || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var t = Tr(name, lang);
            return t != name && t.Replace(" ", "").IndexOf(query.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsLang(string lang) => lang == Ko || lang == En;

        static (string Text, bool Complete) Table(string name, string lang) => Cache.GetOrAdd((name, lang), k => Translate(k.Item1, k.Item2 == En));

        static (string, bool) Translate(string name, bool en)
        {
            var n = Norm(name);
            if (Whole.TryGetValue(n.Trim(), out var w)) return (en ? w.En : w.Ko, true);
            var outp = new List<string>();
            bool any = false, latinAll = true, jpLeft = false, jp = false;
            foreach (var (text, kind) in Split(n))
            {
                bool hit = false;
                if (kind == Kind.Jp)
                {
                    jp = true;
                    var t = Japanese(text, en, out hit);
                    jpLeft |= t.Any(IsJp);
                    outp.Add(t);
                }
                else if (kind == Kind.Latin)
                {
                    bool all;
                    outp.Add(en ? LatinEn(text, out hit) : LatinKo(text, out hit, out all));
                    if (!en) latinAll &= hit;
                }
                else outp.Add(kind == Kind.Sep ? " " : text);
                any |= hit;
            }
            if (en)
            {
                // 영어로: 일본어가 없으면 이미 영어 (표에서 할 일 없음, 다 된 것으로)
                if (!any) return (name, !jp);
                return (Tidy(string.Concat(outp)), !jpLeft);
            }
            // 한국어로: 영문만 있는 이름은 단어가 모두 옮겨질 때만 (일본어가 섞인 이름은 옮긴 만큼이라도)
            if (!any || (!jp && !latinAll)) return (name, false);
            return (Tidy(string.Concat(outp)), !jpLeft && latinAll);
        }

        enum Kind { Jp, Latin, Digit, Sep, Other }

        // 전각 영숫자(Ａ·２)·반각 가타카나를 보통 글자로
        static string Norm(string s) { try { return s.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { return s; } }

        static bool IsJp(char c) =>
            (c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || c == '々' || c == '〆';
        static bool IsSep(char c) => c == '_' || c == '.' || c == ' ' || c == '-' || c == '・' || c == '　';
        static Kind KindOf(char c) => IsJp(c) ? Kind.Jp : (c < 128 && char.IsLetter(c)) ? Kind.Latin : char.IsDigit(c) ? Kind.Digit : IsSep(c) ? Kind.Sep : Kind.Other;

        static IEnumerable<(string, Kind)> Split(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                var k = KindOf(s[i]);
                int j = i + 1;
                while (j < s.Length && k != Kind.Other && KindOf(s[j]) == k) j++;
                yield return (s.Substring(i, j - i), k);
                i = j;
            }
        }

        static string Japanese(string run, bool en, out bool hit)
        {
            hit = false;
            if (Whole.TryGetValue(run, out var w)) { hit = true; return " " + (en ? w.En : w.Ko) + " "; }
            var sb = new StringBuilder();
            int i = 0;
            while (i < run.Length)
            {
                string? key = null;
                for (int len = Math.Min(MaxPart, run.Length - i); len >= 1; len--)
                {
                    var k = run.Substring(i, len);
                    if (Parts.ContainsKey(k)) { key = k; break; }
                }
                if (key == null) { sb.Append(run[i]); i++; continue; }   // 옮기지 못한 글자는 그대로 (앞의 번역과는 띄운 채)
                hit = true;
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(en ? Parts[key].En : Parts[key].Ko).Append(' ');
                i += key.Length;
            }
            return " " + sb.ToString() + " ";
        }

        /// <summary>camelCase 나누기: eyeClose → eye Close, EyeL → Eye L</summary>
        static List<string> Words(string run)
        {
            var words = new List<string>();
            int start = 0;
            for (int i = 1; i <= run.Length; i++)
                if (i == run.Length || (char.IsUpper(run[i]) && (char.IsLower(run[i - 1]) || (i + 1 < run.Length && char.IsLower(run[i + 1])))))
                { words.Add(run.Substring(start, i - start)); start = i; }
            return words;
        }

        static string LatinKo(string run, out bool hit, out bool all)
        {
            hit = all = false;
            if (English.TryGetValue(run, out var w)) { hit = all = true; return w; }
            if (Romaji.TryGetValue(run, out var r)) { hit = all = true; return r.Ko; }
            var words = Words(run);
            string? Ko(string x) => English.TryGetValue(x, out var e) ? e : Romaji.TryGetValue(x, out var ro) ? ro.Ko : null;
            if (words.Count < 2 || words.Any(x => Ko(x) == null)) return run;
            hit = all = true;
            return string.Join(" ", words.Select(x => Ko(x)!));
        }

        static string LatinEn(string run, out bool hit)
        {
            hit = false;
            if (Romaji.TryGetValue(run, out var r)) { hit = true; return r.En; }
            var words = Words(run);
            if (!words.Any(Romaji.ContainsKey)) return run;
            hit = true;
            return string.Join(" ", words.Select(x => Romaji.TryGetValue(x, out var ro) ? ro.En : x));
        }

        /// <summary>띄어쓰기를 한 칸으로, 바로 겹친 낱말은 하나로 ("eye_blink" → "눈 눈 감기" → "눈 감기")</summary>
        static string Tidy(string s)
        {
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = words.Count - 1; i > 0; i--)
                if (string.Equals(words[i], words[i - 1], StringComparison.OrdinalIgnoreCase) && words[i].Any(char.IsLetter)) words.RemoveAt(i);
            return string.Join(" ", words);
        }

        /// <summary>이름 전체(또는 일본어 조각 전체)로 맞추는 것. MMD 표준 모프와 자주 쓰는 이름. (한국어, 영어)</summary>
        static readonly Dictionary<string, (string Ko, string En)> Whole = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // 입 (MMD 립싱크 모음)
            ["あ"] = ("아", "A"), ["い"] = ("이", "I"), ["う"] = ("우", "U"), ["え"] = ("에", "E"), ["お"] = ("오", "O"), ["ん"] = ("음", "N"), ["ワ"] = ("와", "Wa"),
            ["あ2"] = ("아 2", "A 2"), ["ア"] = ("아", "A"), ["イ"] = ("이", "I"), ["ウ"] = ("우", "U"), ["エ"] = ("에", "E"), ["オ"] = ("오", "O"),
            ["▲"] = ("▲ 입", "▲ Mouth"), ["∧"] = ("∧ 입", "∧ Mouth"), ["ω"] = ("ω 입", "ω Mouth"), ["ω□"] = ("ω□ 입", "ω□ Mouth"), ["□"] = ("□ 입", "□ Mouth"),
            // 눈
            ["まばたき"] = ("눈 감기", "Blink"), ["瞬き"] = ("눈 감기", "Blink"), ["笑い"] = ("웃음", "Smile"), ["はぅ"] = ("질끈 감기", "Squeeze Eyes Shut"), ["はう"] = ("질끈 감기", "Squeeze Eyes Shut"),
            ["ウィンク"] = ("윙크", "Wink"), ["ウインク"] = ("윙크", "Wink"), ["ウィンク右"] = ("오른쪽 윙크", "Wink Right"), ["ウィンク左"] = ("왼쪽 윙크", "Wink Left"),
            ["ウィンク2"] = ("윙크 2", "Wink 2"), ["ウィンク2右"] = ("오른쪽 윙크 2", "Wink 2 Right"), ["ウィンク2左"] = ("왼쪽 윙크 2", "Wink 2 Left"),
            ["ウィンク右2"] = ("오른쪽 윙크 2", "Wink 2 Right"), ["ウィンク左2"] = ("왼쪽 윙크 2", "Wink 2 Left"),
            ["なごみ"] = ("나른한 눈", "Relaxed Eyes"), ["びっくり"] = ("깜짝 놀람", "Surprised"), ["じと目"] = ("게슴츠레한 눈", "Half-lidded Eyes"), ["ジト目"] = ("게슴츠레한 눈", "Half-lidded Eyes"),
            ["キリッ"] = ("또렷한 눈", "Sharp Eyes"), ["はぁと"] = ("하트 눈", "Heart Eyes"), ["星目"] = ("별 눈", "Star Eyes"), ["はちゅ目"] = ("세로 눈동자", "Slit Pupils"),
            ["恐ろしい子!"] = ("흰자 눈 (충격)", "Blank White Eyes (Shock)"), ["はんっ!"] = ("흥!", "Hmph!"), ["光下"] = ("아래 빛", "Light Below"),
            // 눈썹
            ["真面目"] = ("진지", "Serious"), ["困る"] = ("곤란", "Troubled"), ["にこり"] = ("생긋", "Cheerful"), ["怒り"] = ("화남", "Angry"),
            // 입
            ["にやり"] = ("씩 웃기", "Smirk"), ["にやり2"] = ("씩 웃기 2", "Smirk 2"), ["にっこり"] = ("방긋", "Beam"), ["ぺろっ"] = ("메롱", "Tongue Out"), ["てへぺろ"] = ("메롱 윙크", "Tongue Out Wink"), ["てへぺろ2"] = ("메롱 윙크 2", "Tongue Out Wink 2"),
            ["口角上げ"] = ("입꼬리 올리기", "Mouth Corners Up"), ["口角下げ"] = ("입꼬리 내리기", "Mouth Corners Down"), ["口横広げ"] = ("입 옆으로 벌리기", "Mouth Wide"), ["口角広げ"] = ("입꼬리 넓히기", "Mouth Corners Wide"),
            // 구분선 이름 (구글 번역은 "M M D"를 "남 월 일"로 옮겼다)
            ["MMD"] = ("MMD", "MMD"), ["M M D"] = ("MMD", "MMD"), ["FT Viseme"] = ("페이셜 트래킹 립싱크", "FT Viseme"),
            ["むー"] = ("뿌루퉁", "Pout"), ["むっ"] = ("뿌루퉁", "Pout"), ["ぷくー"] = ("볼 부풀리기", "Puffed Cheeks"), ["ぷくっ"] = ("볼 부풀리기", "Puffed Cheeks"), ["への字"] = ("ㅅ자 입", "Frown Mouth"),
        };

        /// <summary>일본어 단어 조각: 가장 긴 것부터 맞춘다. 한 글자 가나는 넣지 않는다 (다른 낱말을 잘못 쪼갠다). (한국어, 영어)</summary>
        static readonly Dictionary<string, (string Ko, string En)> Parts = new Dictionary<string, (string, string)>
        {
            // 얼굴 부위
            ["目"] = ("눈", "Eye"), ["眼"] = ("눈", "Eye"), ["瞳"] = ("눈동자", "Pupil"), ["瞳孔"] = ("동공", "Pupil"), ["白目"] = ("흰자위", "Eye White"), ["黒目"] = ("검은자위", "Iris"), ["虹彩"] = ("홍채", "Iris"),
            ["まぶた"] = ("눈꺼풀", "Eyelid"), ["瞼"] = ("눈꺼풀", "Eyelid"), ["上まぶた"] = ("윗눈꺼풀", "Upper Eyelid"), ["下まぶた"] = ("아랫눈꺼풀", "Lower Eyelid"), ["上瞼"] = ("윗눈꺼풀", "Upper Eyelid"), ["下瞼"] = ("아랫눈꺼풀", "Lower Eyelid"),
            ["目尻"] = ("눈꼬리", "Outer Eye Corner"), ["目頭"] = ("눈머리", "Inner Eye Corner"), ["まつげ"] = ("속눈썹", "Eyelash"), ["まつ毛"] = ("속눈썹", "Eyelash"), ["睫毛"] = ("속눈썹", "Eyelash"),
            ["眉"] = ("눈썹", "Brow"), ["まゆ"] = ("눈썹", "Brow"), ["眉毛"] = ("눈썹", "Brow"), ["口"] = ("입", "Mouth"), ["くち"] = ("입", "Mouth"), ["唇"] = ("입술", "Lips"), ["口角"] = ("입꼬리", "Mouth Corner"),
            ["舌"] = ("혀", "Tongue"), ["べろ"] = ("혀", "Tongue"), ["ベロ"] = ("혀", "Tongue"), ["歯"] = ("이", "Teeth"), ["八重歯"] = ("덧니", "Snaggletooth"), ["牙"] = ("송곳니", "Fang"), ["キバ"] = ("송곳니", "Fang"), ["ギザ歯"] = ("톱니 이", "Jagged Teeth"),
            ["頬"] = ("볼", "Cheek"), ["ほほ"] = ("볼", "Cheek"), ["ほっぺ"] = ("볼", "Cheek"), ["鼻"] = ("코", "Nose"), ["耳"] = ("귀", "Ear"), ["顎"] = ("턱", "Jaw"), ["あご"] = ("턱", "Jaw"), ["額"] = ("이마", "Forehead"), ["おでこ"] = ("이마", "Forehead"),
            ["顔"] = ("얼굴", "Face"), ["輪郭"] = ("윤곽", "Contour"), ["表情"] = ("표정", "Expression"), ["ハイライト"] = ("하이라이트", "Highlight"), ["映り込み"] = ("반사", "Reflection"), ["光"] = ("빛", "Light"),
            // 몸
            ["胸"] = ("가슴", "Chest"), ["バスト"] = ("가슴", "Bust"), ["腰"] = ("허리", "Waist"), ["ウエスト"] = ("허리", "Waist"), ["お腹"] = ("배", "Belly"), ["腹"] = ("배", "Belly"), ["尻"] = ("엉덩이", "Hip"), ["お尻"] = ("엉덩이", "Hip"), ["ヒップ"] = ("엉덩이", "Hip"),
            ["肩"] = ("어깨", "Shoulder"), ["腕"] = ("팔", "Arm"), ["手"] = ("손", "Hand"), ["指"] = ("손가락", "Finger"), ["首"] = ("목", "Neck"), ["足"] = ("다리", "Leg"), ["脚"] = ("다리", "Leg"), ["太もも"] = ("허벅지", "Thigh"), ["太腿"] = ("허벅지", "Thigh"),
            ["ふくらはぎ"] = ("종아리", "Calf"), ["膝"] = ("무릎", "Knee"), ["背中"] = ("등", "Back"), ["体"] = ("몸", "Body"), ["身体"] = ("몸", "Body"), ["髪"] = ("머리카락", "Hair"), ["前髪"] = ("앞머리", "Bangs"), ["後ろ髪"] = ("뒷머리", "Back Hair"), ["横髪"] = ("옆머리", "Side Hair"), ["アホ毛"] = ("바보털", "Ahoge"),
            ["靴"] = ("신발", "Shoes"), ["ヒール"] = ("힐", "Heels"), ["服"] = ("옷", "Clothes"), ["袖"] = ("소매", "Sleeve"), ["スカート"] = ("치마", "Skirt"), ["パンツ"] = ("바지", "Pants"), ["靴下"] = ("양말", "Socks"), ["ニーソ"] = ("니삭스", "Knee Socks"), ["手袋"] = ("장갑", "Gloves"), ["しっぽ"] = ("꼬리", "Tail"), ["尻尾"] = ("꼬리", "Tail"), ["ケモミミ"] = ("동물 귀", "Animal Ears"),
            // 표정·감정
            ["まばたき"] = ("눈 감기", "Blink"), ["瞬き"] = ("눈 감기", "Blink"), ["ウィンク"] = ("윙크", "Wink"), ["ウインク"] = ("윙크", "Wink"), ["笑い"] = ("웃음", "Smile"), ["笑顔"] = ("웃는 얼굴", "Smiling Face"), ["笑"] = ("웃음", "Smile"), ["微笑み"] = ("미소", "Gentle Smile"), ["微笑"] = ("미소", "Gentle Smile"),
            ["怒り"] = ("화남", "Angry"), ["怒"] = ("화남", "Angry"), ["困る"] = ("곤란", "Troubled"), ["困り"] = ("곤란", "Troubled"), ["悲しみ"] = ("슬픔", "Sad"), ["悲しい"] = ("슬픔", "Sad"), ["悲"] = ("슬픔", "Sad"), ["哀"] = ("슬픔", "Sad"), ["喜び"] = ("기쁨", "Joy"), ["喜"] = ("기쁨", "Joy"), ["楽"] = ("즐거움", "Fun"),
            ["驚き"] = ("놀람", "Surprised"), ["驚"] = ("놀람", "Surprised"), ["びっくり"] = ("깜짝 놀람", "Surprised"), ["照れ"] = ("부끄러움", "Shy"), ["恥ずかしい"] = ("부끄러움", "Embarrassed"), ["赤面"] = ("빨개짐", "Blush"), ["頬染め"] = ("볼 붉힘", "Blush"), ["青ざめ"] = ("창백", "Pale"),
            ["泣き"] = ("울음", "Cry"), ["泣"] = ("울음", "Cry"), ["涙"] = ("눈물", "Tears"), ["汗"] = ("땀", "Sweat"), ["真面目"] = ("진지", "Serious"), ["真顔"] = ("무표정", "Straight Face"), ["にこり"] = ("생긋", "Cheerful"), ["にっこり"] = ("방긋", "Beam"), ["にやり"] = ("씩 웃기", "Smirk"),
            ["じと目"] = ("게슴츠레한 눈", "Half-lidded Eyes"), ["ジト目"] = ("게슴츠레한 눈", "Half-lidded Eyes"), ["なごみ"] = ("나른한 눈", "Relaxed Eyes"), ["半目"] = ("반쯤 감은 눈", "Half-closed Eyes"), ["見開き"] = ("크게 뜨기", "Wide Open"), ["ぐるぐる"] = ("빙글빙글", "Swirl"), ["はぁと"] = ("하트", "Heart"), ["ハート"] = ("하트", "Heart"), ["星"] = ("별", "Star"),
            ["キス"] = ("뽀뽀", "Kiss"), ["ぺろ"] = ("메롱", "Tongue Out"), ["舌出し"] = ("혀 내밀기", "Tongue Out"), ["歯無し"] = ("이 숨기기", "Hide Teeth"), ["しいたけ"] = ("표고 눈", "Shiitake Eyes"),
            // 동작·정도
            ["閉じ"] = ("감기", "Close"), ["目閉じ"] = ("눈 감기", "Eyes Closed"), ["口閉じ"] = ("입 다물기", "Mouth Closed"), ["開き"] = ("벌리기", "Open"), ["開け"] = ("벌리기", "Open"), ["上げ"] = ("올리기", "Up"), ["下げ"] = ("내리기", "Down"), ["寄せ"] = ("모으기", "Together"), ["広げ"] = ("넓히기", "Wide"), ["すぼめ"] = ("오므리기", "Pucker"), ["つり"] = ("치켜뜬", "Upturned"), ["たれ"] = ("처진", "Droopy"), ["タレ"] = ("처진", "Droopy"),
            ["消し"] = ("숨기기", "Hide"), ["隠し"] = ("숨기기", "Hide"), ["非表示"] = ("숨기기", "Hide"), ["表示"] = ("보이기", "Show"), ["縮小"] = ("줄이기", "Shrink"), ["縮め"] = ("줄이기", "Shrink"), ["拡大"] = ("키우기", "Enlarge"), ["調整"] = ("조정", "Adjust"), ["補正"] = ("보정", "Fix"), ["変形"] = ("변형", "Deform"),
            ["大きく"] = ("크게", "Bigger"), ["小さく"] = ("작게", "Smaller"), ["細く"] = ("가늘게", "Thinner"), ["太く"] = ("굵게", "Thicker"), ["長く"] = ("길게", "Longer"), ["短く"] = ("짧게", "Shorter"), ["丸"] = ("둥근", "Round"), ["細"] = ("가는", "Thin"), ["大"] = ("크게", "Big"), ["小"] = ("작게", "Small"), ["太"] = ("굵은", "Thick"), ["長"] = ("긴", "Long"), ["短"] = ("짧은", "Short"),
            // 방향·좌우
            ["左"] = ("왼쪽", "Left"), ["右"] = ("오른쪽", "Right"), ["両"] = ("양쪽", "Both"), ["片"] = ("한쪽", "One Side"), ["上"] = ("위", "Up"), ["下"] = ("아래", "Down"), ["前"] = ("앞", "Front"), ["後"] = ("뒤", "Back"), ["後ろ"] = ("뒤", "Back"), ["横"] = ("옆", "Side"), ["内"] = ("안", "Inner"), ["外"] = ("바깥", "Outer"), ["中"] = ("가운데", "Middle"),
            ["ひだり"] = ("왼쪽", "Left"), ["みぎ"] = ("오른쪽", "Right"),
        };
        static readonly int MaxPart = Parts.Keys.Max(k => k.Length);

        /// <summary>영문 단어 → 한국어 (대소문자 무시). 한국어로 옮길 때 이름의 단어가 모두 여기(또는 Romaji)에 있어야 옮긴다.</summary>
        static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blink"] = "눈 감기", ["wink"] = "윙크", ["smile"] = "웃음", ["laugh"] = "웃음", ["grin"] = "씩 웃기", ["joy"] = "기쁨", ["happy"] = "기쁨", ["fun"] = "즐거움",
            ["angry"] = "화남", ["anger"] = "화남", ["sad"] = "슬픔", ["sorrow"] = "슬픔", ["surprise"] = "놀람", ["surprised"] = "놀람", ["shock"] = "놀람", ["shy"] = "부끄러움", ["blush"] = "홍조",
            ["cry"] = "울음", ["tear"] = "눈물", ["tears"] = "눈물", ["sweat"] = "땀", ["frown"] = "찡그림", ["pout"] = "삐죽", ["kiss"] = "뽀뽀", ["serious"] = "진지", ["worried"] = "걱정", ["worry"] = "걱정", ["trouble"] = "곤란",
            ["eye"] = "눈", ["eyes"] = "눈", ["pupil"] = "눈동자", ["iris"] = "홍채", ["lid"] = "눈꺼풀", ["eyelid"] = "눈꺼풀", ["lash"] = "속눈썹", ["eyelash"] = "속눈썹", ["highlight"] = "하이라이트",
            ["brow"] = "눈썹", ["eyebrow"] = "눈썹", ["mouth"] = "입", ["lip"] = "입술", ["lips"] = "입술", ["tongue"] = "혀", ["teeth"] = "이", ["tooth"] = "이", ["fang"] = "송곳니", ["cheek"] = "볼", ["nose"] = "코", ["ear"] = "귀", ["face"] = "얼굴", ["jaw"] = "턱",
            ["breast"] = "가슴", ["chest"] = "가슴", ["waist"] = "허리", ["belly"] = "배", ["hip"] = "엉덩이", ["leg"] = "다리", ["thigh"] = "허벅지", ["arm"] = "팔", ["hand"] = "손", ["foot"] = "발", ["neck"] = "목", ["shoulder"] = "어깨",
            ["close"] = "감기", ["closed"] = "감기", ["open"] = "벌리기", ["wide"] = "크게", ["narrow"] = "가늘게", ["up"] = "위", ["down"] = "아래", ["upper"] = "위", ["lower"] = "아래", ["in"] = "안", ["out"] = "바깥", ["front"] = "앞", ["back"] = "뒤",
            ["big"] = "크게", ["small"] = "작게", ["shrink"] = "줄이기", ["hide"] = "숨기기", ["off"] = "끄기", ["puff"] = "부풀리기", ["heart"] = "하트", ["star"] = "별",
            ["left"] = "왼쪽", ["right"] = "오른쪽", ["l"] = "왼쪽", ["r"] = "오른쪽", ["both"] = "양쪽",
            ["middle"] = "가운데", ["inner"] = "안쪽", ["outer"] = "바깥쪽", ["inside"] = "안쪽", ["outside"] = "바깥쪽", ["under"] = "아래", ["forward"] = "앞으로", ["far"] = "멀리", ["look"] = "시선",
            ["long"] = "길게", ["short"] = "짧게", ["thick"] = "두껍게", ["thin"] = "얇게", ["sharp"] = "날카롭게", ["round"] = "둥글게", ["straight"] = "일자", ["mini"] = "작게", ["size"] = "크기", ["width"] = "너비", ["height"] = "높이",
            ["line"] = "선", ["tail"] = "꼬리", ["sub"] = "보조", ["main"] = "메인", ["option"] = "옵션", ["all"] = "모두", ["simple"] = "단순", ["shadow"] = "그림자", ["dark"] = "어둡게", ["pale"] = "창백", ["sleepy"] = "졸림", ["cat"] = "고양이", ["elf"] = "엘프",
            ["joyful"] = "기쁨", ["squint"] = "찡그리기", ["dilation"] = "커지기", ["constrict"] = "작아지기", ["pinch"] = "모으기", ["lowerer"] = "내리기", ["raiser"] = "올리기", ["sneer"] = "찡그리기", ["suck"] = "빨아들이기",
            ["funnel"] = "오므리기", ["pucker"] = "오므리기", ["stretch"] = "늘이기", ["roll"] = "말기", ["correction"] = "보정", ["mix"] = "섞기", ["delete"] = "숨기기", ["thinner"] = "가늘게",
            ["facial"] = "페이셜", ["tracking"] = "트래킹", ["viseme"] = "립싱크", ["custom"] = "커스텀", ["ft"] = "페이셜 트래킹",
            // 모음 (mouth_a 등)
            ["a"] = "아", ["i"] = "이", ["u"] = "우", ["e"] = "에", ["o"] = "오", ["n"] = "음", ["x"] = "×",
        };

        /// <summary>일본어 로마자 (eye_nagomi·jitome·tsuri 등 일본 아바타의 영문 이름에 흔하다). (한국어, 영어)</summary>
        static readonly Dictionary<string, (string Ko, string En)> Romaji = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["nagomi"] = ("나른한", "Relaxed"), ["jitome"] = ("게슴츠레한 눈", "Half-lidded"), ["tsuri"] = ("치켜뜬", "Upturned"), ["tare"] = ("처진", "Droopy"), ["maru"] = ("동그란", "Round"), ["marushiro"] = ("동그란 흰자", "Round White"),
            ["yaeba"] = ("덧니", "Snaggletooth"), ["bero"] = ("혀", "Tongue"), ["pero"] = ("메롱", "Tongue Out"), ["tehepero"] = ("메롱 윙크", "Tongue Out Wink"), ["puku"] = ("볼 부풀리기", "Puffed Cheeks"), ["muu"] = ("뿌루퉁", "Pout"),
            ["kirakira"] = ("반짝반짝", "Sparkle"), ["yodare"] = ("침", "Drool"), ["uru"] = ("글썽", "Teary"), ["shiitake"] = ("표고 눈", "Shiitake Eyes"), ["guruguru"] = ("빙글빙글", "Swirl"), ["pokan"] = ("멍", "Blank"), ["hawa"] = ("당황", "Flustered"),
            ["gizaba"] = ("톱니 이", "Jagged Teeth"), ["maromayu"] = ("둥근 눈썹", "Round Brows"), ["hoppe"] = ("볼", "Cheek"), ["hachume"] = ("세로 눈동자", "Slit Pupils"), ["niyari"] = ("씩 웃기", "Smirk"), ["nikkori"] = ("방긋", "Beam"),
            ["bikkuri"] = ("깜짝 놀람", "Surprised"), ["mabataki"] = ("눈 감기", "Blink"), ["warai"] = ("웃음", "Smile"), ["wa"] = ("와", "Wa"),
        };
    }
}
