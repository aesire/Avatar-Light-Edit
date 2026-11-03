// lilToon 번역 파일(.po, MIT)을 읽어 프로퍼티 설명 키(sMainColor 등)를 표시 이름으로 바꾼다.
// 빌드 시 lilToon 패키지의 Editor/Localization/*.po 를 bin/av/Localization 으로 복사한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VrcProto.Host.Shared
{
    public static class LilLocalization
    {
        static Dictionary<string, string>? _map;
        static string _lang = "ko";
        /// <summary>화면 언어 (ko · en · ja, 2026-11-02). 바꾸면 다음 표시부터 그 언어의 .po로</summary>
        public static string Lang { get => _lang; set { if (_lang == value) return; _lang = value; _map = null; } }

        static Dictionary<string, string> Map
        {
            get
            {
                if (_map != null) return _map;
                _map = new Dictionary<string, string>();
                var dir = Path.Combine(AppContext.BaseDirectory, "Localization");
                // 영어를 먼저 읽고 화면 언어로 덮어쓴다 (그 언어에 없는 키는 영어)
                foreach (var f in _lang == "en" ? new[] { "en-US.po" } : new[] { "en-US.po", _lang == "ja" ? "ja-JP.po" : "ko-KR.po" })
                {
                    var p = Path.Combine(dir, f);
                    if (File.Exists(p)) Load(p, _map);
                }
                // lilToon이 코드에서 조합하는 이름: 키<TAB>부분|부분… ("=" 로 시작하면 번역하지 않는 문자열)
                var compound = Path.Combine(dir, "compound.txt");
                if (File.Exists(compound))
                    foreach (var line in File.ReadLines(compound))
                    {
                        var kv = line.Split('\t');
                        if (kv.Length != 2) continue;
                        var parts = kv[1].Split('|');
                        for (int i = 0; i < parts.Length; i++)
                            parts[i] = parts[i].StartsWith("=") ? parts[i].Substring(1) : _map.TryGetValue(parts[i], out var t) ? t : parts[i];
                        _map[kv[0]] = string.Join("|", parts);
                    }
                return _map;
            }
        }

        static void Load(string path, Dictionary<string, string> map)
        {
            string? id = null; var str = new StringBuilder(); string? mode = null;
            void Flush() { if (id != null && id.Length > 0 && str.Length > 0) map[id] = str.ToString(); id = null; str.Clear(); mode = null; }
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.StartsWith("msgid ")) { Flush(); id = Unquote(line.Substring(6)); mode = "id"; }
                else if (line.StartsWith("msgstr ")) { str.Append(Unquote(line.Substring(7))); mode = "str"; }
                else if (line.StartsWith("\""))
                {
                    if (mode == "id") id += Unquote(line);
                    else if (mode == "str") str.Append(Unquote(line));
                }
                else if (line.Length == 0) Flush();
            }
            Flush();
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        /// <summary>"sKey" 또는 "sKey|opt|opt" 또는 "Label|opt|opt" 형식의 설명을 번역해 (이름, 선택지) 로 돌려준다.</summary>
        public static (string Label, string[] Options) Describe(string desc)
        {
            var parts = desc.Split('|');
            for (int i = 0; i < parts.Length; i++) parts[i] = Tr(parts[i]);
            // 번역 결과 자체가 "이름|선택지…" 인 경우 (sCullModes 등)
            var expanded = new List<string>();
            foreach (var p in parts) expanded.AddRange(p.Split('|'));
            var label = expanded.Count > 0 ? expanded[0] : desc;
            return (label, expanded.Count > 1 ? expanded.GetRange(1, expanded.Count - 1).ToArray() : Array.Empty<string>());
        }

        public static string Tr(string key)
        {
            key = key.Trim();
            if (key.Length > 1 && key[0] == 's' && char.IsUpper(key[1])) return Map.TryGetValue(key, out var v) ? v : key;
            return Plain.TryGetValue(key, out var p) ? p : key;
        }

        /// <summary>
        /// lilToon 셰이더가 번역 키 없이 영어로 적어 둔 설명 ("Texture", "Gradation Strength" 등 — lilToon 에디터는 자기 코드로 이름을 붙여 .po에 없다).
        /// 머티리얼 페이지에 영어가 섞여 보이던 것. 스텐실·렌더링 모드 이름처럼 Unity 용어 그대로가 나은 것은 두지 않는다.
        /// </summary>
        static readonly Dictionary<string, string> Plain = new Dictionary<string, string>
        {
            ["Texture"] = "텍스처", ["Color"] = "색", ["Mask"] = "마스크", ["Noise"] = "노이즈", ["Shape"] = "모양", ["Channel"] = "채널",
            ["2nd Color"] = "두 번째 색", ["3rd Color"] = "세 번째 색", ["Main Color Strength"] = "메인 색 세기",
            ["Gradation Map"] = "그라데이션 맵", ["Gradation Texture"] = "그라데이션 텍스처", ["Gradation Strength"] = "그라데이션 세기", ["Gradation Speed"] = "그라데이션 속도",
            ["Hue"] = "색조", ["Saturation"] = "채도", ["Value"] = "명도", ["Gamma"] = "감마",
            ["Normal Map"] = "노멀 맵", ["Tangent Map"] = "탄젠트 맵", ["Local Map"] = "로컬 맵", ["Parallax Map"] = "시차 맵", ["Parallax Scale"] = "시차 크기",
            ["Metallic"] = "메탈릭", ["Smoothness"] = "매끄러움", ["Apply Specular"] = "반사광 적용", ["Specular Toon"] = "반사광 툰", ["Cubemap Fallback"] = "대체 큐브맵",
            ["Shadow Color"] = "그림자 색", ["Shadow 2nd Color"] = "그림자 두 번째 색", ["Shadow Ramp"] = "그림자 램프",
            ["Blend"] = "섞기", ["Blend Main"] = "메인과 섞기", ["Blend Mode"] = "섞는 방식", ["Normal"] = "보통", ["Add"] = "더하기", ["Screen"] = "스크린", ["Multiply"] = "곱하기",
            ["Cull Mode"] = "그릴 면", ["Off"] = "양면", ["Front"] = "앞면 숨김", ["Back"] = "뒷면 숨김",
            ["UV Mode"] = "UV 모드", ["Angle"] = "각도", ["Scale"] = "크기", ["Offset"] = "오프셋", ["Tiling"] = "타일링", ["Width"] = "두께", ["Border"] = "경계",
            ["Strength"] = "세기", ["Blink Strength"] = "깜빡임 세기", ["Blink Speed"] = "깜빡임 속도", ["Blink Threshold"] = "깜빡임 기준",
            ["Particle Size"] = "입자 크기", ["Contrast"] = "대비", ["Sensitivity"] = "감도", ["Max Value"] = "최댓값", ["Vector"] = "벡터", ["Vector scale"] = "벡터 크기",
            ["Dissolve Mask"] = "디졸브 마스크", ["Dissolve Noise Mask"] = "디졸브 노이즈 마스크", ["Dissolve Noise Strength"] = "디졸브 노이즈 세기", ["Dissolve Position"] = "디졸브 위치",
            ["Use Emission"] = "발광 쓰기", ["Use Main 2nd"] = "메인 2 쓰기", ["Use Main 3rd"] = "메인 3 쓰기", ["Use Outline"] = "외곽선 쓰기",
            ["Length Mask"] = "길이 마스크", ["Cutout Length"] = "컷아웃 길이", ["Adjust Mask"] = "조정 마스크", ["Scale Mask"] = "크기 마스크", ["Pre Cutoff"] = "미리 잘라내기 기준",
            ["As Decal"] = "데칼로", ["Copy"] = "복사", ["Flip Copy"] = "뒤집어 복사", ["Flip Mirror"] = "거울에서 뒤집기", ["Left Only"] = "왼쪽만", ["Right Only"] = "오른쪽만",
            ["Dither"] = "디더", ["Shadow Caster Bias"] = "그림자 바이어스", ["Z Bias"] = "Z 바이어스", ["Override"] = "덮어쓰기", ["Atras"] = "아틀라스",
        };
    }
}
