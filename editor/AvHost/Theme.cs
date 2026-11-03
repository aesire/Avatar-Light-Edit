// 테마 (2026-10-21, 사용자 요청): UI 바탕·단추·강조 색을 바꾼다. 설정 창 "화면 › 테마".
// 프리셋(Windows 따라·어둡게·밝게·남색·슬레이트·따뜻한 갈색·라벤더) 또는 직접 고르기(#RRGGBB 세 가지).
// 바탕 색 하나에서 작업 창·카드·테두리·구분선 색을 만든다 (어두운 바탕이면 다크 테마 글자, 밝으면 라이트) — 카드 위 글자가 읽히게.
// 단추 색은 Fluent 단추 바탕 키(ButtonBackground…)를, 강조 색은 Fluent 팔레트(스위치·슬라이더·강조 단추)와 앱 강조 키를 바꾼다.
// 앱 전체 리소스라 에디터·떠 있는 창·대화상자에 같이 쓰인다. 바꾸면 바로 적용·저장.
using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace VrcProto.AvHost
{
    public static class UiTheme
    {
        public sealed record Preset(string Key, string Label, string Mode, string Base, string Button, string Accent);

        public static readonly Preset[] Presets =
        {
            new Preset("system", "Windows 설정 따라", "system", "", "", ""),
            new Preset("dark", "어둡게", "dark", "", "", ""),
            new Preset("light", "밝게", "light", "", "", ""),
            new Preset("nord", "남색 (노르드)", "dark", "#2E3440", "#3B4252", "#88C0D0"),
            new Preset("slate", "슬레이트", "dark", "#22252C", "#30343D", "#7AA2F7"),
            new Preset("warm", "따뜻한 갈색", "dark", "#2A2522", "#3A332E", "#E0A458"),
            new Preset("lavender", "라벤더 (밝게)", "light", "#F2F0F7", "#E4DFF2", "#7B61FF"),
            new Preset("custom", "직접 고르기", "dark", "", "", ""),
        };

        static readonly string[] ButtonKeys = { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ToggleButtonBackground", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundPressed" };

        /// <summary>지금 설정의 색 (custom이면 설정 값, 아니면 프리셋 값)</summary>
        public static (string Mode, string Base, string Button, string Accent) Resolve(Settings s)
        {
            var p = Presets.FirstOrDefault(x => x.Key == s.ThemeKey) ?? Presets[0];
            if (p.Key != "custom") return (p.Mode, p.Base, p.Button, p.Accent);
            bool? dark = TryHex(s.ThemeBase, out var b) ? Luma(b) < 0.5 : (bool?)null;
            return (dark == null ? "system" : dark.Value ? "dark" : "light", s.ThemeBase, s.ThemeButton, s.ThemeAccent);
        }

        public static void Apply(Settings s)
        {
            if (Application.Current is not App app) return;
            var (mode, bas, button, accent) = Resolve(s);
            app.ResetPalette();
            var variant = mode == "dark" ? ThemeVariant.Dark : mode == "light" ? ThemeVariant.Light : ThemeVariant.Default;

            if (TryHex(bas, out var b))
            {
                bool dark = Luma(b) < 0.5;
                variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var d = (Avalonia.Controls.ResourceDictionary)app.Resources.ThemeDictionaries[variant];
                d[Ui.KBase] = Brush(b);
                if (dark)
                {
                    d[Ui.KPanel] = Brush(Lerp(b, Colors.White, 0.035)); d[Ui.KCard] = Brush(Lerp(b, Colors.White, 0.075));
                    d[Ui.KCardStroke] = Brush(Lerp(b, Colors.White, 0.14)); d[Ui.KDivider] = Brush(Lerp(b, Colors.White, 0.12));
                    d[Ui.KTitleBar] = Brush(Lerp(Lerp(b, Colors.White, 0.03), Blue, 0.05)); d[Ui.KToolbar] = Brush(Lerp(Lerp(b, Colors.White, 0.055), Blue, 0.06));
                }
                else
                {
                    d[Ui.KPanel] = Brush(Lerp(b, Colors.White, 0.5)); d[Ui.KCard] = Brush(Lerp(b, Colors.White, 0.85));
                    d[Ui.KCardStroke] = Brush(Lerp(b, Colors.Black, 0.09)); d[Ui.KDivider] = Brush(Lerp(b, Colors.Black, 0.06));
                    d[Ui.KTitleBar] = Brush(Lerp(b, Blue, 0.05)); d[Ui.KToolbar] = Brush(Lerp(Lerp(b, Colors.White, 0.4), Blue, 0.04));
                }
            }
            app.RequestedThemeVariant = variant;
            ApplyDial(s);

            foreach (var k in ButtonKeys) app.Resources.Remove(k);
            Hardware.ClearButtonResources(app);
            if (TryHex(button, out var bt))
            {
                bool dark = Luma(bt) < 0.5;
                var hover = Lerp(bt, dark ? Colors.White : Colors.Black, 0.08);
                var press = Lerp(bt, dark ? Colors.Black : Colors.Black, 0.12);
                app.Resources["ButtonBackground"] = Brush(bt); app.Resources["ButtonBackgroundPointerOver"] = Brush(hover); app.Resources["ButtonBackgroundPressed"] = Brush(press);
                app.Resources["ToggleButtonBackground"] = Brush(bt); app.Resources["ToggleButtonBackgroundPointerOver"] = Brush(hover); app.Resources["ToggleButtonBackgroundPressed"] = Brush(press);
            }

            var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
            fluent?.Palettes.Clear();
            if (TryHex(accent, out var ac))
            {
                if (fluent != null)
                {
                    fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = ac };
                    fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = ac };
                }
                app.SetAccent(ac);
            }
            else app.ApplyAccent();
            Hardware.ButtonResources(app);   // 다이얼 모드: 장비 키 모양 단추
        }

        /// <summary>
        /// 다이얼·세밀 조정 하이라이트 색. 기본 형광 노랑 — 밝은 바탕에서는 같은 계열의 진한 연두 노랑(형광 노랑은 흰 바탕에서 거의 안 보인다).
        /// "강조색"이면 키를 비워 강조색을 쓴다 (Dial.Highlight).
        /// </summary>
        public static void ApplyDial(Settings s)
        {
            if (Application.Current is not App app) return;
            foreach (var v in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                var d = (Avalonia.Controls.ResourceDictionary)app.Resources.ThemeDictionaries[v];
                if (s.DialColor == "accent") d.Remove(Ui.KDial);
                else if (TryHex(s.DialColor, out var c)) d[Ui.KDial] = Brush(c);
                else d[Ui.KDial] = Brush(v == ThemeVariant.Dark ? Neon : NeonOnLight);
            }
        }
        public static readonly Color Neon = Color.FromRgb(0xD4, 0xF5, 0x2E), NeonOnLight = Color.FromRgb(0x9C, 0xC2, 0x00);

        public static bool TryHex(string? s, out Color c)
        {
            c = default;
            var t = (s ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        static readonly Color Blue = Color.FromRgb(0x5B, 0x8D, 0xEF);   // 상단 바에 아주 조금 섞는 파랑
        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        static SolidColorBrush Brush(Color c) => new SolidColorBrush(c);
        public static Color Lerp(Color a, Color b, double t)
            => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}
