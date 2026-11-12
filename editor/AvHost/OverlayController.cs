// 방송 오버레이의 상태와 켜고 끄기를 한 곳에서 (2026-10-20 구조 정리). 화면(스튜디오 페이지의 OverlayPanel, 컨트롤 보드)은 이것만 부른다.
//   켜기: Unity 창을 미리보기에서 떼어(UnityView.EnterOverlay) 자리에 띄우고, 렌더러에 투명 배경·조정 모드를 알린다 (Msg.Overlay)
//   끄기: 렌더러에 알리고 미리보기로 되돌린다. 끄는 길은 모두 여기로 — 스위치, 컨트롤 보드, 조정 모드의 ✕, 렌더러 재시작, 프로그램 닫기
//   자리: 모서리 다섯 곳 또는 직접 끌어 옮긴 자리(custom). 모서리는 마지막으로 놓았던 모니터 기준이라 게임 모니터로 한 번 옮겨 두면 모서리도 그 모니터
//   조정 모드 = 클릭 통과를 끈 상태: 오버레이 창을 끌어 옮기고(렌더러 Overlay.Tick) 오른쪽 끌기·휠로 카메라를 돌린다. 테두리와 ✕가 보인다
//   화질 (2026-10-23): 슈퍼샘플링 배율(창보다 크게 그려 줄임, 설정 overlay.supersample) · ("방송 화질" 프리셋은 2026-11-12에 빼고 오버레이 전용 조명으로 바꿈, 아래)
//   (2026-10-24) 프레임 제한(켜 있는 동안만, 기본 30 fps) · 창 비율 프리셋과 조정 모드의 크기 손잡이(비율도 자유롭게) ·
//   오버레이 전용 카메라(켤 때 지난번 오버레이 카메라로, 끌 때 그 자리를 저장하고 미리보기 카메라로 되돌림) · 전역 단축키(켜기·위치 조정, MainWindow가 등록)
//   (2026-11-12) 보내는 곳 세 가지 (Outputs, 설정 overlay.output):
//     screen = 게임 위 투명 창 (디스코드·OBS에서 화면 전체를 공유할 때)
//     obs    = 제목 "ALE 방송 출력 (OBS)"인 보통 창, 배경은 투명 알파 그대로 — OBS "게임 캡처 → 특정 창 → 이 창 · 투명도 허용"으로 아바타만 가져간다
//     game   = 고른 게임 창을 렌더러가 잡아(WindowCapture) 아바타와 합친 창 "ALE 방송 화면" — 디스코드에서 게임 하나만 방송할 때 게임 대신 이 창을 고른다
//              자리·크기·비율은 그 창 안 아바타 상자. 창 크기는 게임 창 그림 칸(1920×1080 넘으면 줄임)
//     (2026-11-12) obs 배경: 투명(게임 캡처 "투명도 허용") · 크로마키(고른 색 — OBS "윈도우 캡처"는 알파를 못 읽어 투명 배경이 검게 보였다, 크로마키 필터로 뺀다)
//   (2026-11-12) 조명: env = 환경 탭 조명 그대로 · own = 오버레이 전용 조명(설정 overlay.environment). 전용이면 켜 있는 동안만 렌더러에 그 환경을 보내고
//     (문서는 그대로, RenderSync.EnvironmentOverride로 환경 탭 편집·장면 다시 불러오기도 덮지 않게), 끄면 문서 환경으로 되돌린다.
//     "환경 탭 조명 가져오기" = 지금 문서 환경을 전용 조명으로 복사. "방송 화질 프리셋" 단추는 뺐다 (사용자 2026-11-12)
//   창으로 띄우는 두 곳은 위치 조정이 없고(조정 모드 = 화면 위 창 전용), 켤 때만 자리를 잡는다 (사용자가 게임 뒤로 옮겨 둔 창을 다시 끌어오지 않게)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class OverlayController
    {
        readonly EditorContext _ctx;
        readonly Settings _settings;
        readonly UnityView _view;
        readonly Func<Window?> _owner;

        /// <summary>켜고 끄기·자리·모드가 바뀌면 (UI 스레드).</summary>
        public event Action? Changed;

        public static readonly (string Key, string Label)[] Corners = { ("br", "오른쪽 아래"), ("bl", "왼쪽 아래"), ("tr", "오른쪽 위"), ("tl", "왼쪽 위"), ("center", "가운데"), ("custom", "옮긴 자리") };

        public OverlayController(EditorContext ctx, Settings settings, UnityView view, Func<Window?> owner)
        {
            _ctx = ctx; _settings = settings; _view = view; _owner = owner;
            _ctx.RendererMessage += OnRenderer;
            RenderSync.EnvironmentOverride = () => On && LightSource == "own" && _settings.OverlayEnvironment.Kind == JKind.Array ? _settings.OverlayEnvironment : null;
        }

        public bool On => _view.InOverlay;
        public Settings Settings => _settings;
        /// <summary>조정 모드 (클릭 통과 끔): 끌어서 옮기고 카메라를 돌린다. 화면 위 투명 창일 때만.</summary>
        public bool Adjusting => !_settings.OverlayClickThrough && Output == "screen";

        // ---- 보내는 곳 (2026-11-12) ----
        public static readonly (string Key, string Label, string Tip)[] Outputs =
        {
            ("screen", "게임 위에 띄우기", "게임 위에 투명 창으로 아바타만 띄워요. 디스코드·OBS에서 화면(모니터) 전체를 공유할 때 써요"),
            ("obs", "OBS로 보내기", "아바타만 그린 창을 띄워요. 배경이 '투명'이면 OBS '게임 캡처' → '특정 창 캡처' → 'ALE 방송 출력 (OBS)' → '투명도 허용'으로, '크로마키'면 '윈도우 캡처'로 잡고 크로마키 필터로 배경 색을 빼요"),
            ("game", "게임 화면과 합치기", "디스코드에서 게임 하나만 방송할 때: 고른 게임 창을 잡아 그 위에 아바타를 얹은 창을 띄워요. 디스코드에서 게임 대신 'ALE 방송 화면' 창을 방송하면 게임과 아바타가 함께 보여요"),
        };
        public const string ObsTitle = "ALE 방송 출력 (OBS)", GameTitle = "ALE 방송 화면";
        public string Output => Array.FindIndex(Outputs, x => x.Key == _settings.OverlayOutput) >= 0 ? _settings.OverlayOutput : "screen";

        public void SetOutput(string key)
        {
            if (Array.FindIndex(Outputs, x => x.Key == key) < 0) key = "screen";
            if (key == _settings.OverlayOutput) return;
            bool wasOn = On;
            if (wasOn) SetOn(false);   // 창 모양이 달라지므로 끄고 다시 띄운다
            _settings.OverlayOutput = key; _settings.Save();
            if (wasOn) SetOn(true);
            Changed?.Invoke();
        }

        // ---- OBS 배경 (2026-11-12): 투명 · 크로마키 ----
        public static readonly (string Key, string Label, string Tip)[] ObsBackgrounds =
        {
            ("alpha", "투명 (게임 캡처)", "배경을 투명하게 보내요. OBS '게임 캡처'에서 '투명도 허용'을 켜야 해요 (내 화면에서는 배경이 검게 보여요). '윈도우 캡처'로 잡으면 검게 나오니 그때는 크로마키를 쓰세요"),
            ("chroma", "크로마키 (윈도우 캡처)", "배경을 고른 색 하나로 칠해요. OBS '윈도우 캡처'로 잡고 필터 → '크로마 키'에서 같은 색을 빼요. 아바타에 없는 색을 고르세요"),
        };
        public static readonly (string Hex, string Label)[] ChromaColors = { ("#00FF00", "초록"), ("#0000FF", "파랑"), ("#FF00FF", "분홍") };

        public string ObsBackground => _settings.OverlayObsBg == "chroma" ? "chroma" : "alpha";

        public void SetObsBackground(string key)
        {
            key = key == "chroma" ? "chroma" : "alpha";
            if (key == _settings.OverlayObsBg) return;
            _settings.OverlayObsBg = key; _settings.Save();
            if (On) SendState();
            _ctx.Status(key == "chroma" ? $"OBS 창 배경을 크로마키({_settings.OverlayChroma})로 칠해요. OBS '윈도우 캡처'에 필터 '크로마 키'를 넣고 색을 '사용자 지정' {_settings.OverlayChroma}로 맞추세요."
                : "OBS 창 배경을 투명하게 보내요. OBS '게임 캡처'에서 '투명도 허용'을 켜세요.");
            Changed?.Invoke();
        }

        /// <summary>크로마키 색 (#RRGGBB). 잘못된 값이면 false.</summary>
        public bool SetChroma(string hex)
        {
            var c = ParseHex(hex);
            if (c == null) return false;
            hex = "#" + hex.Trim().TrimStart('#').ToUpperInvariant();
            if (hex == _settings.OverlayChroma) return true;
            _settings.OverlayChroma = hex; _settings.Save();
            if (On) SendState();
            Changed?.Invoke();
            return true;
        }

        /// <summary>#RRGGBB → [r, g, b] (0~1). 아니면 null.</summary>
        public static float[]? ParseHex(string? s)
        {
            var t = (s ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
            return new[] { ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f };
        }

        IntPtr _game;   // 합칠 게임 창 (설정에는 제목만)

        /// <summary>합칠 수 있는 창 목록 (보이는 최상위 창, 이 프로그램과 렌더러 창은 뺀다).</summary>
        public List<(IntPtr Hwnd, string Title)> GameWindows() => TopWindows.List(_view.UnityHandle);

        public void SetGame(IntPtr hwnd, string title)
        {
            _game = hwnd;
            _settings.OverlayGameTitle = title; _settings.Save();
            if (On && Output == "game") { Place(refit: true); SendState(); }
            Changed?.Invoke();
        }

        /// <summary>고른 게임 창 (핸들이 죽었으면 같은 제목의 창을 다시 찾는다).</summary>
        public IntPtr Game()
        {
            if (_game != IntPtr.Zero && TopWindows.IsWindow(_game)) return _game;
            _game = IntPtr.Zero;
            var t = _settings.OverlayGameTitle;
            if (t.Length == 0) return IntPtr.Zero;
            var list = GameWindows();
            foreach (var (h, title) in list) if (title == t) { _game = h; break; }
            if (_game == IntPtr.Zero) foreach (var (h, title) in list) if (title.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(title, StringComparison.Ordinal)) { _game = h; break; }
            return _game;
        }

        JNode _previewCamera = JNode.Null;   // 오버레이를 켜기 전 미리보기 카메라 (끌 때 되돌린다)

        public void SetOn(bool on)
        {
            if (on == On) { Changed?.Invoke(); return; }
            if (on)
            {
                if (_ctx.Render == null || !_ctx.Render.IsConnected || _view.UnityHandle == IntPtr.Zero) { _ctx.Status("미리보기가 켜져야 오버레이를 켤 수 있어요."); Changed?.Invoke(); return; }
                if (Output == "game" && Game() == IntPtr.Zero) { _ctx.Status("합칠 게임 창을 먼저 골라 주세요 (게임을 켠 뒤 '게임 창' 목록에서)."); Changed?.Invoke(); return; }
                if (!Place(enter: true)) { _ctx.Status("오버레이를 띄울 화면을 찾지 못했어요."); Changed?.Invoke(); return; }
                _previewCamera = _ctx.Camera.Clone();
                if (_settings.OverlayCamera.Kind == JKind.Object) _ctx.Render.Send(Msg.Make(Msg.SetCamera).Set("state", _settings.OverlayCamera));
                SendState();
                SendEnvironment();
                if (Output == "obs" && ObsBackground == "chroma") _ctx.Status($"OBS용 창 '{Texts.T(ObsTitle)}'을 띄웠어요 (배경 {_settings.OverlayChroma}). OBS에서 '윈도우 캡처'로 이 창을 고르고 필터 '크로마 키'의 색을 {_settings.OverlayChroma}로 맞추세요. 창은 게임 뒤로 보내도 돼요 (최소화는 안 돼요).");
                else if (Output == "obs") _ctx.Status($"OBS용 창 '{Texts.T(ObsTitle)}'을 띄웠어요. OBS에서 '게임 캡처' → '특정 창 캡처'로 이 창을 고르고 '투명도 허용'을 켜세요 ('윈도우 캡처'로는 배경이 검게 나와요 — 그때는 배경을 크로마키로). 창은 게임 뒤로 보내도 돼요 (최소화는 안 돼요).");
                else if (Output == "game") _ctx.Status($"게임과 합친 창 '{Texts.T(GameTitle)}'을 띄웠어요. 디스코드 화면 공유에서 게임 대신 이 창을 고르세요. 창은 게임 뒤로 보내도 돼요 (최소화는 안 돼요).");
                else _ctx.Status(Adjusting ? "방송 오버레이를 켰어요 (조정 모드: 끌어서 옮기고, 다 되면 조정을 끄세요)." : $"방송 오버레이를 켰어요. 끄려면 컨트롤 보드·스튜디오 페이지{KeyHint(_settings.OverlayKeyToggle)}에서 끄세요.");
            }
            else
            {
                // 오버레이에서 맞춘 카메라는 다음에 켤 때 쓰고, 미리보기는 켜기 전 카메라로
                if (_ctx.Camera.Kind == JKind.Object) { _settings.OverlayCamera = _ctx.Camera.Clone(); _settings.Save(); }
                _ctx.Render?.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", _view.UnityHandle.ToInt64()));
                if (_previewCamera.Kind == JKind.Object) _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("state", _previewCamera));
                _previewCamera = JNode.Null;
                _view.ExitOverlay();
                if (LightSource == "own") SendEnvironment();   // 전용 조명이었으면 환경 탭 조명으로 되돌린다
                _ctx.Status("오버레이를 끄고 미리보기로 돌아왔어요.");
            }
            Changed?.Invoke();
        }

        static string KeyHint(string key) => string.IsNullOrEmpty(key) ? "" : $" · {GlobalHotkeys.Text(key)}";

        /// <summary>전역 단축키로 눌렀을 때 (MainWindow). 처리했으면 상태 글.</summary>
        public string? PressKey(string key)
        {
            if (key == _settings.OverlayKeyToggle) { SetOn(!On); return On ? "오버레이 켬" : "오버레이 끔"; }
            if (key == _settings.OverlayKeyAdjust) { SetAdjust(!Adjusting); return Adjusting ? "오버레이 위치 조정 켬" : "오버레이 위치 조정 끔 (클릭은 게임으로)"; }
            return null;
        }

        /// <summary>단축키 바꾸기 (which: "toggle" | "adjust"). MainWindow가 KeysChanged로 다시 등록한다.</summary>
        public void SetKey(string which, string key)
        {
            if (which == "toggle") _settings.OverlayKeyToggle = key; else _settings.OverlayKeyAdjust = key;
            _settings.Save();
            KeysChanged?.Invoke();
            Changed?.Invoke();
        }
        public event Action? KeysChanged;
        public string[] Keys => new[] { _settings.OverlayKeyToggle, _settings.OverlayKeyAdjust };

        public static readonly (int Value, string Label)[] FpsChoices = { (30, "30 fps (디스코드 공유)"), (60, "60 fps"), (0, "평소 제한 그대로") };

        /// <summary>켜 있는 동안의 프레임 제한 (0 = 설정의 렌더러 제한 그대로).</summary>
        public void SetFps(int fps)
        {
            if (_settings.OverlayFps == fps) return;
            _settings.OverlayFps = fps; _settings.Save();
            if (On) SendState();
            Changed?.Invoke();
        }

        public static readonly (double Value, string Label)[] Aspects = { (0.75, "세로 3:4 (전신)"), (1, "정사각 1:1 (상반신)"), (16.0 / 9, "가로 16:9"), (9.0 / 16, "세로 9:16 (쇼츠)") };

        /// <summary>창 비율 (너비/높이). 조정 모드의 크기 손잡이로 끌면 그 비율이 된다.</summary>
        public void SetAspect(double aspect)
        {
            aspect = Math.Clamp(aspect, 0.2, 5);
            if (Math.Abs(_settings.OverlayAspect - aspect) < 1e-3) return;
            _settings.OverlayAspect = aspect; _settings.Save();
            Place(refit: true);
            if (On && Output == "game") SendState();
            Changed?.Invoke();
        }

        public void SetAdjust(bool adjust)
        {
            _settings.OverlayClickThrough = !adjust; _settings.Save();
            if (On) { Place(); SendState(); }
            Changed?.Invoke();
        }

        public void SetCorner(string key)
        {
            if (_settings.OverlayCorner == key) return;
            _settings.OverlayCorner = key; _settings.Save();
            Place();
            if (On && Output == "game") SendState();
            Changed?.Invoke();
        }

        /// <summary>크기 (화면 높이에 대한 비율). save = 끌기를 마쳤을 때.</summary>
        public void SetSize(double ratio, bool save)
        {
            _settings.OverlaySize = Math.Clamp(ratio, 0.15, 1);
            if (save) _settings.Save();
            Place();
            if (On && Output == "game") SendState();
            if (save) Changed?.Invoke();
        }

        public void SetTopmost(bool top)
        {
            _settings.OverlayTopmost = top; _settings.Save();
            Place();
            Changed?.Invoke();
        }

        /// <summary>설정을 직접 바꾼 뒤 화면을 다시 그리게 (화면 확인 --ui-shots).</summary>
        internal void NotifyChanged() => Changed?.Invoke();

        /// <summary>렌더러를 다시 시작하기 전 등 (켜져 있으면 끈다).</summary>
        public void Off() { if (On) SetOn(false); }

        /// <summary>프로그램을 닫을 때: 미리보기 자식 창이 이미 없을 수 있으므로 오버레이 창을 숨기기만 한다.</summary>
        public void HideForClose() => _view.HideOverlayNow();

        void SendState() => _ctx.Render?.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", _view.UnityHandle.ToInt64()).Set("adjust", Adjusting)
            .Set("supersample", (float)_settings.OverlaySupersample).Set("fps", _settings.OverlayFps)
            .Set("mode", Output).Set("game", Output == "game" ? Game().ToInt64() : 0)
            .Set("chroma", Output == "obs" && ObsBackground == "chroma" && ParseHex(_settings.OverlayChroma) is float[] key ? JNode.FloatArray(key) : JNode.Null)
            .Set("box", JNode.Obj().Set("corner", _settings.OverlayCorner == "custom" ? "br" : _settings.OverlayCorner).Set("size", (float)_settings.OverlaySize).Set("aspect", (float)_settings.OverlayAspect)));

        public static readonly (double Value, string Label)[] Supersamples = { (1, "끔"), (1.5, "1.5배"), (2, "2배 (가장 깨끗, GPU 많이)") };

        /// <summary>슈퍼샘플링 배율: 창보다 이만큼 크게 그려 줄인다 (1 = 끔).</summary>
        public void SetSupersample(double v)
        {
            v = Math.Clamp(v, 1, 2);
            if (Math.Abs(_settings.OverlaySupersample - v) < 1e-3) return;
            _settings.OverlaySupersample = v; _settings.Save();
            if (On) SendState();
            _ctx.Status(v > 1 ? $"방송 창을 {v:0.#}배로 그려 줄여요. 머리카락 끝·반사광이 깨끗해지는 대신 GPU를 더 써요." : "방송 창 슈퍼샘플링을 껐어요.");
            Changed?.Invoke();
        }

        // ---- 조명 배치 프리셋 (2026-10-23 사용자: 조명 위치를 고르게, 기본 몇 가지) ----
        // 값은 Core.LightingLayouts
        public static readonly (string Key, string Label, string Tip)[] Layouts =
        {
            ("topfill", "위·앞 + 채움광 (기본)", "정면 위에서 주광, 앞에서 채움광 — 입체감이 있으면서 얼굴이 밝아요"),
            ("front", "정면 고르게 (그림자 없음)", "정면에서 낮게, 그림자 없이 — 가장 깨끗하고 납작한 애니메이션 느낌"),
            ("butterfly", "나비 (위 정면 + 아래 채움)", "높은 정면 주광과 턱 아래 채움광 — 뷰티 촬영에서 많이 쓰는 배치"),
            ("side45", "45° 옆 (입체)", "비스듬한 옆에서 주광, 반대쪽에서 약한 채움광 — 얼굴 굴곡이 살아요"),
            ("rim", "역광 + 앞 채움 (윤곽 빛)", "뒤에서 비춰 머리카락 윤곽이 빛나고, 앞 채움광이 얼굴을 밝혀요"),
        };

        float EyeHeight() => LightingLayouts.EyeHeight(_ctx.Doc?.Original.ViewPosition);

        (string Provider, (string Name, JNode Value)[] Params)[] Layout(string key) => LightingLayouts.Get(key, EyeHeight());

        // ---- 조명: 환경 탭 그대로 · 오버레이 전용 (2026-11-12) ----
        public static readonly (string Key, string Label, string Tip)[] LightSources =
        {
            ("env", "환경 탭 조명 그대로", "미리보기와 같은 조명을 써요. 환경 탭에서 바꾸면 방송에도 바로 들어가요"),
            ("own", "오버레이 전용 조명", "오버레이가 켜 있는 동안만 따로 저장한 조명을 써요. 끄면 환경 탭 조명으로 돌아와요. '환경 탭 조명 가져오기'로 지금 조명을 저장해요"),
        };
        public string LightSource => _settings.OverlayLightSource == "own" ? "own" : "env";
        /// <summary>전용 조명을 저장해 둔 적이 있는가.</summary>
        public bool HasOwnLighting => _settings.OverlayEnvironment.Kind == JKind.Array;

        JNode DocEnvironment() => (_ctx.Doc?.Get(Addr.Environment) ?? Document.DefaultEnvironment()).Clone();

        public void SetLightSource(string key)
        {
            key = key == "own" ? "own" : "env";
            if (key == LightSource) return;
            if (key == "own" && !HasOwnLighting) _settings.OverlayEnvironment = DocEnvironment();   // 처음이면 지금 환경 탭 조명에서 시작
            _settings.OverlayLightSource = key; _settings.Save();
            SendEnvironment();
            _ctx.Status(key == "own" ? "오버레이 전용 조명을 써요. 켜 있는 동안만 들어가고, 끄면 환경 탭 조명으로 돌아와요." : "오버레이도 환경 탭 조명을 그대로 써요.");
            Changed?.Invoke();
        }

        /// <summary>지금 환경 탭의 조명(환경 목록 전체 — 조명·후처리)을 오버레이 전용 조명으로 복사한다.</summary>
        public void ImportEnvironment()
        {
            _settings.OverlayEnvironment = DocEnvironment();
            _settings.OverlayLightSource = "own"; _settings.Save();
            SendEnvironment();
            _ctx.Status("환경 탭의 조명을 오버레이 전용 조명으로 가져왔어요. 오버레이가 켜 있는 동안 이 조명을 써요.");
            Changed?.Invoke();
        }

        /// <summary>렌더러에 지금 써야 할 환경을 보낸다: 켜 있고 전용 조명이면 그것, 아니면 문서(환경 탭).</summary>
        void SendEnvironment()
        {
            var env = RenderSync.EnvironmentOverride?.Invoke() ?? DocEnvironment();
            _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", env));
        }

        /// 조명 배치 프리셋 (위 Layouts): 전용 조명이면 그 안에, 아니면 환경 탭(문서, 실행 취소 한 번)에 넣는다.
        /// <summary>조명 배치 고르기: 주광 방향·높이·그림자와 채움광(추가 조명 1번)을 바꾼다.</summary>
        public void SetLighting(string key)
        {
            if (Array.FindIndex(Layouts, x => x.Key == key) < 0) key = Layouts[0].Key;
            _settings.OverlayLighting = key; _settings.Save();
            var label = Layouts.First(x => x.Key == key).Label;
            if (LightSource == "own")
            {
                _settings.OverlayEnvironment = Merge(HasOwnLighting ? _settings.OverlayEnvironment.Clone() : DocEnvironment(), Layout(key));
                _settings.Save();
                SendEnvironment();
                _ctx.Status($"오버레이 전용 조명에 배치 '{label}'를 넣었어요.");
            }
            else
            {
                var list = Merge(DocEnvironment(), Layout(key));
                if (_ctx.Doc != null) _ctx.Doc.Set(Addr.Environment, list, "조명 배치: " + label);
                else _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", list));
                _ctx.Status($"조명 배치 '{label}'. 환경 탭에서 방향·높이·세기를 더 다듬을 수 있어요.");
            }
            Changed?.Invoke();
        }

        /// <summary>환경 목록의 효과마다 값을 덮어쓴다 (없으면 만들고, 꺼 둔 효과는 켠다). 다른 값·다른 효과는 그대로.</summary>
        JNode Merge(JNode list, (string Provider, (string Name, JNode Value)[] Params)[] sets)
        {
            foreach (var (prov, ps) in sets)
            {
                var e = list.Items.FirstOrDefault(x => x["provider"].AsString == prov);
                if (e == null)
                {
                    int ver = _ctx.EnvironmentSchema.Items.FirstOrDefault(x => x["id"].AsString == prov)?["version"].AsInt ?? 1;
                    e = JNode.Obj().Set("provider", prov).Set("version", Math.Max(1, ver)).Set("params", JNode.Obj());
                    list.Add(e);
                }
                e.Remove("enabled");
                if (!e.Has("params") || e["params"].Kind != JKind.Object) e.Set("params", JNode.Obj());
                foreach (var (n, v) in ps) e["params"].Set(n, v.Clone());
            }
            return list;
        }

        void OnRenderer(JNode m)
        {
            switch (Msg.Type(m))
            {
                case Msg.Ready:
                    // 렌더러가 다시 시작되면 새 창은 미리보기 안에 있다 (UnityView.ForgetUnityWindow가 오버레이 상태를 지웠다)
                    Changed?.Invoke();
                    break;
                case Msg.Overlay:
                    if (m["close"].AsBool) { SetOn(false); break; }
                    if (m["moved"].Kind == JKind.Object)
                    {
                        var r = m["moved"];
                        _settings.OverlayX = r["x"].AsInt + r["w"].AsInt / 2; _settings.OverlayY = r["y"].AsInt + r["h"].AsInt;
                        if (m["resized"].AsBool && r["h"].AsInt > 0 && TargetScreen() is Screen sc)
                        {
                            // 크기 손잡이로 끌었다: 높이 → 화면 높이 비율, 너비/높이 → 비율
                            _settings.OverlayAspect = Math.Clamp((double)r["w"].AsInt / r["h"].AsInt, 0.2, 5);
                            _settings.OverlaySize = Math.Clamp((double)r["h"].AsInt / sc.WorkingArea.Height, 0.15, 1);
                        }
                        _settings.OverlayCorner = "custom"; _settings.Save();
                        Place();   // 화면 밖으로 나갔으면 안으로 당긴다
                        Changed?.Invoke();
                    }
                    break;
            }
        }

        /// <summary>마지막 자리가 든 모니터 (없으면 프로그램 창의 모니터).</summary>
        Screen? TargetScreen()
        {
            var w = _owner();
            if (w == null) return null;
            if (_settings.OverlayX != 0 || _settings.OverlayY != 0)
                if (w.Screens.ScreenFromPoint(new PixelPoint(_settings.OverlayX, _settings.OverlayY - 1)) is Screen s) return s;
            return w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
        }

        /// <summary>화면에서의 자리와 크기를 정해 띄운다 (켜져 있을 때만, 처음 켤 때도).
        /// 창으로 띄우는 곳(obs·game)은 켤 때(enter)와 창 크기가 바뀔 때(refit)만 — refit은 지금 자리를 지킨다.</summary>
        bool Place(bool enter = false, bool refit = false)
        {
            var screen = TargetScreen();
            if (screen == null) return false;
            var wa = screen.WorkingArea;   // 물리 픽셀
            if (Output != "screen")
            {
                if (!enter && !(On && refit)) return true;
                int fw, fh;
                var g = Output == "game" ? Game() : IntPtr.Zero;
                var cs = g != IntPtr.Zero ? TopWindows.ClientSize(g) : (W: 0, H: 0);
                if (Output == "game" && cs.W > 0 && cs.H > 0)
                {
                    // 게임 그림 칸 크기 그대로, 1920×1080을 넘으면 비율을 지켜 줄인다 (디스코드 방송은 1080p까지)
                    double k = Math.Min(1, Math.Min(1920.0 / cs.W, 1080.0 / cs.H));
                    fw = (int)(cs.W * k); fh = (int)(cs.H * k);
                }
                else if (Output == "game") { fw = 1280; fh = 720; }
                else
                {
                    // OBS: 높이 1080(화면이 작으면 화면에 맞춤) × 비율 — OBS가 이 해상도로 가져간다
                    fh = Math.Min(1080, wa.Height - (int)(80 * screen.Scaling)); fw = (int)(fh * Math.Clamp(_settings.OverlayAspect, 0.2, 5));
                    if (fw > wa.Width) { fh = (int)(fh * (double)wa.Width / fw); fw = wa.Width; }
                }
                int fx = wa.X + (wa.Width - fw) / 2, fy = wa.Y + Math.Max(0, (wa.Height - fh) / 2);
                if (!enter && TopWindows.ClientOrigin(_view.UnityHandle) is var o && o.Ok) { fx = o.X; fy = o.Y; }   // 크기만 바꿀 때는 지금 자리 그대로
                return _view.EnterOverlay(fx, fy, fw, fh, false, false, Texts.T(Output == "obs" ? ObsTitle : GameTitle));   // 창 제목도 화면 언어로 (2026-11-12, 안내 글과 맞게)
            }
            int h = (int)(wa.Height * Math.Clamp(_settings.OverlaySize, 0.15, 1)), w = (int)(h * Math.Clamp(_settings.OverlayAspect, 0.2, 5));
            if (w > wa.Width) { h = (int)(h * (double)wa.Width / w); w = wa.Width; }   // 가로로 넓으면 화면 너비에 맞춰 줄인다
            int m = (int)(16 * screen.Scaling);
            int x, y;
            if (_settings.OverlayCorner == "custom" && (_settings.OverlayX != 0 || _settings.OverlayY != 0))
            {
                // 아래 가운데 점을 기준으로 (크기를 바꿔도 발밑이 그 자리에 남게), 모니터 밖으로는 나가지 않게
                x = Math.Clamp(_settings.OverlayX - w / 2, wa.X, Math.Max(wa.X, wa.Right - w));
                y = Math.Clamp(_settings.OverlayY - h, wa.Y, Math.Max(wa.Y, wa.Bottom - h));
            }
            else
            {
                x = _settings.OverlayCorner switch { "bl" or "tl" => wa.X + m, "center" => wa.X + (wa.Width - w) / 2, _ => wa.Right - w - m };
                y = _settings.OverlayCorner switch { "tr" or "tl" => wa.Y + m, "center" => wa.Y + (wa.Height - h) / 2, _ => wa.Bottom - h - m };
            }
            if (!On && !enter) return true;   // 꺼져 있으면 자리만 기억
            return _view.EnterOverlay(x, y, w, h, _settings.OverlayTopmost, _settings.OverlayClickThrough);
        }
    }

    /// <summary>게임 창 고르기용 최상위 창 목록 (2026-11-12, OverlayController "게임 화면과 합치기").</summary>
    static class TopWindows
    {
        public static List<(IntPtr Hwnd, string Title)> List(IntPtr unity)
        {
            uint me = (uint)Environment.ProcessId, rend = 0;
            if (unity != IntPtr.Zero) GetWindowThreadProcessId(unity, out rend);
            var list = new List<(IntPtr, string)>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
                if ((GetWindowLongPtr(h, -20).ToInt64() & 0x80 /*TOOLWINDOW*/) != 0) return true;
                if (DwmGetWindowAttribute(h, 14 /*CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;   // 다른 가상 데스크톱·숨은 UWP 창
                GetWindowThreadProcessId(h, out var pid);
                if (pid == me || (rend != 0 && pid == rend)) return true;
                var sb = new StringBuilder(256);
                if (GetWindowText(h, sb, sb.Capacity) <= 0) return true;
                var t = sb.ToString();
                if (t == "Program Manager") return true;
                list.Add((h, t));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static (int W, int H) ClientSize(IntPtr h) => GetClientRect(h, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

        /// <summary>그림 칸 왼쪽 위의 화면 좌표.</summary>
        public static (bool Ok, int X, int Y) ClientOrigin(IntPtr h)
        {
            var p = new POINT();
            return h != IntPtr.Zero && ClientToScreen(h, ref p) ? (true, p.X, p.Y) : (false, 0, 0);
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    }
}
