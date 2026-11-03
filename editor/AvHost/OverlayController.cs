// 방송 오버레이의 상태와 켜고 끄기를 한 곳에서 (2026-10-20 구조 정리). 화면(스튜디오 페이지의 OverlayPanel, 컨트롤 보드)은 이것만 부른다.
//   켜기: Unity 창을 미리보기에서 떼어(UnityView.EnterOverlay) 자리에 띄우고, 렌더러에 투명 배경·조정 모드를 알린다 (Msg.Overlay)
//   끄기: 렌더러에 알리고 미리보기로 되돌린다. 끄는 길은 모두 여기로 — 스위치, 컨트롤 보드, 조정 모드의 ✕, 렌더러 재시작, 프로그램 닫기
//   자리: 모서리 다섯 곳 또는 직접 끌어 옮긴 자리(custom). 모서리는 마지막으로 놓았던 모니터 기준이라 게임 모니터로 한 번 옮겨 두면 모서리도 그 모니터
//   조정 모드 = 클릭 통과를 끈 상태: 오버레이 창을 끌어 옮기고(렌더러 Overlay.Tick) 오른쪽 끌기·휠로 카메라를 돌린다. 테두리와 ✕가 보인다
//   화질 (2026-10-23): 슈퍼샘플링 배율(창보다 크게 그려 줄임, 설정 overlay.supersample) · "방송 화질" 프리셋(환경의 조명·후처리를 쨍하게, 실행 취소 한 번)
//   (2026-10-24) 프레임 제한(켜 있는 동안만, 기본 30 fps) · 창 비율 프리셋과 조정 모드의 크기 손잡이(비율도 자유롭게) ·
//   오버레이 전용 카메라(켤 때 지난번 오버레이 카메라로, 끌 때 그 자리를 저장하고 미리보기 카메라로 되돌림) · 전역 단축키(켜기·위치 조정, MainWindow가 등록)
using System;
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
        }

        public bool On => _view.InOverlay;
        public Settings Settings => _settings;
        /// <summary>조정 모드 (클릭 통과 끔): 끌어서 옮기고 카메라를 돌린다.</summary>
        public bool Adjusting => !_settings.OverlayClickThrough;

        JNode _previewCamera = JNode.Null;   // 오버레이를 켜기 전 미리보기 카메라 (끌 때 되돌린다)

        public void SetOn(bool on)
        {
            if (on == On) { Changed?.Invoke(); return; }
            if (on)
            {
                if (_ctx.Render == null || !_ctx.Render.IsConnected || _view.UnityHandle == IntPtr.Zero) { _ctx.Status("미리보기가 켜져야 오버레이를 켤 수 있어요."); Changed?.Invoke(); return; }
                if (!Place(enter: true)) { _ctx.Status("오버레이를 띄울 화면을 찾지 못했어요."); Changed?.Invoke(); return; }
                _previewCamera = _ctx.Camera.Clone();
                if (_settings.OverlayCamera.Kind == JKind.Object) _ctx.Render.Send(Msg.Make(Msg.SetCamera).Set("state", _settings.OverlayCamera));
                SendState();
                _ctx.Status(Adjusting ? "방송 오버레이를 켰어요 (조정 모드: 끌어서 옮기고, 다 되면 조정을 끄세요)." : $"방송 오버레이를 켰어요. 끄려면 컨트롤 보드·스튜디오 페이지{KeyHint(_settings.OverlayKeyToggle)}에서 끄세요.");
            }
            else
            {
                // 오버레이에서 맞춘 카메라는 다음에 켤 때 쓰고, 미리보기는 켜기 전 카메라로
                if (_ctx.Camera.Kind == JKind.Object) { _settings.OverlayCamera = _ctx.Camera.Clone(); _settings.Save(); }
                _ctx.Render?.Send(Msg.Make(Msg.Overlay).Set("enabled", false).Set("hwnd", _view.UnityHandle.ToInt64()));
                if (_previewCamera.Kind == JKind.Object) _ctx.Render?.Send(Msg.Make(Msg.SetCamera).Set("state", _previewCamera));
                _previewCamera = JNode.Null;
                _view.ExitOverlay();
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
            Place();
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
            Changed?.Invoke();
        }

        /// <summary>크기 (화면 높이에 대한 비율). save = 끌기를 마쳤을 때.</summary>
        public void SetSize(double ratio, bool save)
        {
            _settings.OverlaySize = Math.Clamp(ratio, 0.15, 1);
            if (save) _settings.Save();
            Place();
            if (save) Changed?.Invoke();
        }

        public void SetTopmost(bool top)
        {
            _settings.OverlayTopmost = top; _settings.Save();
            Place();
            Changed?.Invoke();
        }

        /// <summary>렌더러를 다시 시작하기 전 등 (켜져 있으면 끈다).</summary>
        public void Off() { if (On) SetOn(false); }

        /// <summary>프로그램을 닫을 때: 미리보기 자식 창이 이미 없을 수 있으므로 오버레이 창을 숨기기만 한다.</summary>
        public void HideForClose() => _view.HideOverlayNow();

        void SendState() => _ctx.Render?.Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", _view.UnityHandle.ToInt64()).Set("adjust", Adjusting)
            .Set("supersample", (float)_settings.OverlaySupersample).Set("fps", _settings.OverlayFps));

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

        /// <summary>조명 배치 고르기: 주광 방향·높이·그림자와 채움광(추가 조명 1번)을 바꾼다. 실행 취소 한 번.</summary>
        public void SetLighting(string key)
        {
            if (Array.FindIndex(Layouts, x => x.Key == key) < 0) key = Layouts[0].Key;
            _settings.OverlayLighting = key; _settings.Save();
            var label = Layouts.First(x => x.Key == key).Label;
            MergeEnvironment(Layout(key), "조명 배치: " + label);
            _ctx.Status($"조명 배치 '{label}'. 환경 페이지에서 방향·높이·세기를 더 다듬을 수 있어요.");
            Changed?.Invoke();
        }

        /// <summary>환경(조명·후처리)을 방송 화질 값으로. 문서가 있으면 실행 취소 한 번으로 되돌린다.</summary>
        public void ApplyBroadcastLook()
        {
            MergeEnvironment(Layout(_settings.OverlayLighting).Concat(LightingLayouts.BroadcastPost).ToArray(), "방송 화질 프리셋");
            _ctx.Status("방송 화질 프리셋을 넣었어요: 조명 배치 + 반사·블룸·채도. 환경 페이지에서 더 다듬을 수 있고, 실행 취소로 되돌려요.");
        }

        /// <summary>환경 목록의 효과마다 값을 덮어쓴다 (없으면 만들고, 꺼 둔 효과는 켠다). 다른 값·다른 효과는 그대로.</summary>
        void MergeEnvironment((string Provider, (string Name, JNode Value)[] Params)[] sets, string label)
        {
            var list = (_ctx.Doc?.Get(Addr.Environment) ?? Document.DefaultEnvironment()).Clone();
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
            if (_ctx.Doc != null) _ctx.Doc.Set(Addr.Environment, list, label);
            else _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", list));
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

        /// <summary>화면에서의 자리와 크기를 정해 띄운다 (켜져 있을 때만, 처음 켤 때도).</summary>
        bool Place(bool enter = false)
        {
            var screen = TargetScreen();
            if (screen == null) return false;
            var wa = screen.WorkingArea;   // 물리 픽셀
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
}
