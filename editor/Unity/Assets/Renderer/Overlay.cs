// 방송 오버레이 (2026-10-19, 2026-10-20 구조 정리): Host가 Unity 창을 미리보기에서 떼어 화면 위의 테두리 없는 창으로 띄우면(AvHost/UnityView.EnterOverlay),
// 여기서 그 창의 배경을 투명하게 만든다. 디스코드 등에서 화면 전체를 공유하면 게임 위에 아바타만 보인다.
//   DWM: DwmExtendFrameIntoClientArea(-1) → 창 전체가 "유리"가 되어 백버퍼의 알파로 합성된다 (빌드 설정 useFlipModelSwapchain = false 필요, Editor/BuildPlayer.cs)
//   카메라: 배경 (0,0,0,0) · 스튜디오 배경은 숨긴다. 후처리(블룸·노출·대비·채도)는 그대로 — 마지막 패스가 원래 알파를 지키고,
//         블룸은 알파 0 자리에 색만 남아 DWM(프리멀티플라이드)에서 게임 위에 더해져 은은하게 번진다 (2026-10-23, 예전엔 꺼 뒀음)
//   슈퍼샘플링 (OverlaySupersample): 창보다 1.5·2배 크게 그려 줄여 보낸다 — 머리카락 끝·반사광 띠·외곽선 계단이 줄어든다
//   오버레이가 켜져 있으면 ViewAids는 격자·본·방향 기즈모·핸들·PhysBone 표시를 그리지 않고 조작도 받지 않는다 (여기의 Tick·DrawGui만)
//   알파 바로잡기 (2026-10-24): 반투명까지 그린 뒤 불투명이 덮은 픽셀의 알파를 1로 (Hidden/VrcProto/OverlayAlpha) — 셰이더 알파 그대로면 게임이 비쳐 보였다
//   프레임 제한 (2026-10-24): 켜져 있는 동안 오버레이 전용 제한(Host 기본 30 fps, RenderQuality.SetOverlayFps) — 게임과 GPU를 나눠 쓰므로
// 조정 모드 (Host에서 "클릭 통과"를 끈 상태):
//   왼쪽 끌기 = 창 옮기기 (커서를 전역으로 읽어 창을 직접 옮긴다 — 창 밖으로 커서가 나가도 따라간다), 오른쪽 끌기·휠 = 카메라 (OrbitCamera 그대로)
//   오른쪽 아래 손잡이 끌기 = 창 크기 (비율도 자유롭게, 2026-10-24) → Host로 { moved: {x, y, w, h}, resized: true }
//   테두리·안내 글·오른쪽 위 ✕(끄기)를 그린다. 클릭 통과 상태에서는 아무것도 그리지 않는다 (방송 화면에 아바타만)
//   옮기기를 마치면 Host로 { t: overlay, moved: {x, y} }, ✕를 누르면 { t: overlay, close: true }
// 보내는 곳 (2026-11-12, mode): screen = 위의 투명 창 (화면 전체 공유) ·
//   obs = 제목 있는 보통 창, 배경 알파 0을 그대로 (DWM 유리는 끔 — 내 화면엔 검게). OBS "게임 캡처 → 이 창 · 투명도 허용"이 알파째 가져간다 ·
//   game = 고른 게임 창을 잡아(WindowCapture) 배경으로 깔고 아바타를 그 안의 상자(자리·크기·비율)에 합친 창. 디스코드에서 게임 대신 이 창을 방송한다
//   (OverlaySupersample 합성). obs 배경 크로마키 (2026-11-12, Chroma): 후처리까지 끝난 뒤 알파 0 자리에 고른 색을 깐다 (OverlayAlpha 패스 1) —
//   OBS "윈도우 캡처"는 알파를 못 읽어 투명 배경이 검게 보이므로, 그때는 크로마키 필터로 뺀다. 창으로 띄우는 두 곳은 조정 모드가 없고, 오른쪽 끌기·휠로 카메라만 돌린다. Alt+F4로 닫으면 렌더러를 끝내지 않고 오버레이를 끈다
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public static class Overlay
    {
        public static bool On { get; private set; }
        /// <summary>조정 모드: 클릭을 받아 끌어서 옮기고, 테두리를 그린다.</summary>
        public static bool Adjust { get; private set; }
        /// <summary>방송 창 슈퍼샘플링 배율 (1 = 끔, 1.5, 2).</summary>
        public static float Supersample { get; private set; } = 1;
        /// <summary>알파 바로잡기 (시험에서 끄고 비교할 수 있게 — overlay 메시지 alphaFix: false).</summary>
        public static bool AlphaFix { get; set; } = true;
        /// <summary>Host로 보내기 (RenderServer가 넣는다).</summary>
        public static Action<JNode> Send;
        /// <summary>보내는 곳: screen · obs · game (위 설명).</summary>
        public static string Mode { get; private set; } = "screen";
        /// <summary>제목 있는 보통 창으로 띄운 경우 (obs · game).</summary>
        public static bool Framed => On && Mode != "screen";
        /// <summary>게임 화면과 합치는 중.</summary>
        public static bool Composite => On && Mode == "game";
        /// <summary>합칠 때 아바타 상자: 자리(br·bl·tr·tl·center), 크기(창 높이에 대한 비율), 비율(너비/높이).</summary>
        public static string BoxCorner = "br";
        public static float BoxSize = 0.45f, BoxAspect = 0.75f;
        /// <summary>잡고 있는 게임 창 (game 모드에서만).</summary>
        public static WindowCapture Game { get; private set; }
        /// <summary>OBS로 보낼 때 배경 크로마키 색 (null = 투명 알파 그대로). 2026-11-12: OBS 윈도우 캡처는 알파를 못 읽어 배경이 검게 보였다.</summary>
        public static Color? Chroma;
        /// <summary>지금 크로마키 배경을 깔아야 하는가 (OBS 창일 때만).</summary>
        public static bool Keyed => On && Mode == "obs" && Chroma.HasValue;
        static bool _glass;

        static IntPtr _hwnd;
        static bool _dragging, _resizing;
        static Vector2Int _cursor0, _win0, _size0;
        static Material _mat;
        static GUIStyle _hint;

        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, int flags);
        const int VK_LBUTTON = 1, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        const int MinSize = 120;

        /// <summary>켜고 끈다. 실패하면 오류 문구 (창은 떠도 배경이 검게 보인다). fps = 켜 있는 동안의 프레임 제한 (0 = 평소 제한 그대로).
        /// mode·game: 보내는 곳과 잡을 게임 창 (위 설명).</summary>
        public static string Set(bool on, long hwnd, bool adjust, float supersample = 1, int fps = 0, string mode = "screen", long game = 0)
        {
            On = on;
            Mode = mode == "obs" || mode == "game" ? mode : "screen";
            Supersample = Mathf.Clamp(supersample, 1, 2); Adjust = on && adjust && !Framed; _dragging = _resizing = false;
            RenderQuality.SetOverlayFps(on ? fps : 0);
            // 게임 창 잡기: 합치는 동안 고른 창만
            long want = Composite ? game : 0;
            if (Game != null && Game.Hwnd != want) { Game.Dispose(); Game = null; }
            if (want != 0 && Game == null) Game = new WindowCapture(want);
            _hwnd = new IntPtr(hwnd);
            if (hwnd == 0) return on ? "창 핸들이 없어요" : null;
            bool glass = on && Mode == "screen";   // 투명 창은 화면 위에 띄울 때만 (보통 창은 OBS가 백버퍼 알파를 직접 읽는다)
            if (glass == _glass) return Composite && game == 0 ? "합칠 게임 창을 골라 주세요" : null;
            _glass = glass;
            try
            {
                var m = glass ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
                int hr = DwmExtendFrameIntoClientArea(_hwnd, ref m);
                return hr < 0 ? $"투명 배경을 켜지 못했어요 (DWM 0x{hr:X8})" : null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>합칠 때 아바타 상자 (픽셀, 왼쪽 아래 원점). 창 W×H 안에서 자리·크기·비율대로.</summary>
        public static RectInt BoxPixels(int W, int H)
        {
            int h = Mathf.Max(16, Mathf.RoundToInt(H * Mathf.Clamp(BoxSize, 0.15f, 1))), w = Mathf.Max(16, Mathf.RoundToInt(h * Mathf.Clamp(BoxAspect, 0.2f, 5)));
            if (w > W) { h = Mathf.Max(16, Mathf.RoundToInt(h * (float)W / w)); w = W; }
            h = Mathf.Min(h, H);
            int m = Mathf.RoundToInt(H * 0.02f);
            int x = BoxCorner == "bl" || BoxCorner == "tl" ? m : BoxCorner == "center" ? (W - w) / 2 : W - w - m;
            int y = BoxCorner == "tl" || BoxCorner == "tr" ? H - h - m : BoxCorner == "center" ? (H - h) / 2 : m;
            return new RectInt(Mathf.Clamp(x, 0, Mathf.Max(0, W - w)), Mathf.Clamp(y, 0, Mathf.Max(0, H - h)), w, h);
        }

        static float S => Screen.dpi > 0 ? Mathf.Clamp(Screen.dpi / 96f, 1, 3) : 1;
        static Rect CloseRect => new Rect(Screen.width - 30 * S, 6 * S, 24 * S, 24 * S);   // GUI 좌표 (왼쪽 위 원점)
        static Rect GripRect => new Rect(Screen.width - 22 * S, Screen.height - 22 * S, 22 * S, 22 * S);   // 크기 손잡이 (오른쪽 아래)
        static Vector2 GuiMouse() { var m = Input.mousePosition; return new Vector2(m.x, Screen.height - m.y); }

        /// <summary>카메라가 왼쪽 버튼을 쓰지 않게 (왼쪽 = 창 옮기기). 클릭 통과 상태에서는 입력 자체가 오지 않는다.</summary>
        public static bool BlocksOrbit() => Input.GetMouseButton(0) || Input.GetMouseButtonDown(0) || _dragging || _resizing;

        /// <summary>ViewAids.Update에서 매 프레임 (켜져 있을 때만).</summary>
        public static void Tick()
        {
            if (!Adjust || _hwnd == IntPtr.Zero) { _dragging = _resizing = false; return; }
            if (_resizing)
            {
                GetCursorPos(out var p);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, Mathf.Max(MinSize, _size0.x + p.X - _cursor0.x), Mathf.Max(MinSize, _size0.y + p.Y - _cursor0.y), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                {
                    _resizing = false;
                    GetWindowRect(_hwnd, out var r);
                    Send?.Invoke(Msg.Make(Msg.Overlay).Set("moved", JNode.Obj().Set("x", r.Left).Set("y", r.Top).Set("w", r.Right - r.Left).Set("h", r.Bottom - r.Top)).Set("resized", true));
                }
                return;
            }
            if (_dragging)
            {
                GetCursorPos(out var p);
                SetWindowPos(_hwnd, IntPtr.Zero, _win0.x + p.X - _cursor0.x, _win0.y + p.Y - _cursor0.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                {
                    _dragging = false;
                    GetWindowRect(_hwnd, out var r);
                    Send?.Invoke(Msg.Make(Msg.Overlay).Set("moved", JNode.Obj().Set("x", r.Left).Set("y", r.Top).Set("w", r.Right - r.Left).Set("h", r.Bottom - r.Top)));
                }
                return;
            }
            if (!Input.GetMouseButtonDown(0)) return;
            if (CloseRect.Contains(GuiMouse())) { Send?.Invoke(Msg.Make(Msg.Overlay).Set("close", true)); return; }
            GetCursorPos(out var c); GetWindowRect(_hwnd, out var w);
            _cursor0 = new Vector2Int(c.X, c.Y); _win0 = new Vector2Int(w.Left, w.Top); _size0 = new Vector2Int(w.Right - w.Left, w.Bottom - w.Top);
            if (GripRect.Contains(GuiMouse())) _resizing = true; else _dragging = true;
        }

        /// <summary>ViewAids.OnGUI에서 (Repaint). 조정 모드에서만 테두리·안내·✕.</summary>
        public static void DrawGui(Font font)
        {
            if (!Adjust || Event.current.type != EventType.Repaint) return;
            if (_mat == null) _mat = new Material(Shader.Find("Hidden/VrcProto/Overlay")) { hideFlags = HideFlags.HideAndDontSave };
            if (_mat.HasProperty("_ZTest")) _mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            float s = S, w = Screen.width, h = Screen.height, t = 2 * s;
            var accent = new Color(0.36f, 0.62f, 1f, 0.95f);
            bool overClose = CloseRect.Contains(GuiMouse());
            GL.PushMatrix(); GL.LoadPixelMatrix(0, w, h, 0); _mat.SetPass(0);
            GL.Begin(GL.QUADS);
            Quad(0, 0, w, t, accent); Quad(0, h - t, w, h, accent); Quad(0, 0, t, h, accent); Quad(w - t, 0, w, h, accent);
            Quad(0, 0, w, 36 * s, new Color(0, 0, 0, 0.45f));   // 위쪽 안내 띠 (옮기기 손잡이처럼 보이게)
            var cr = CloseRect;
            Quad(cr.xMin, cr.yMin, cr.xMax, cr.yMax, overClose ? new Color(0.9f, 0.25f, 0.25f, 0.95f) : new Color(1, 1, 1, 0.18f));
            GL.End();
            // 크기 손잡이: 오른쪽 아래 삼각형
            var g = GripRect;
            GL.Begin(GL.TRIANGLES);
            GL.Color(_resizing || g.Contains(GuiMouse()) ? accent : new Color(1, 1, 1, 0.55f));
            GL.Vertex3(g.xMax, g.yMin, 0); GL.Vertex3(g.xMax, g.yMax, 0); GL.Vertex3(g.xMin, g.yMax, 0);
            GL.End();
            // ✕
            GL.Begin(GL.QUADS);
            Bar(cr.center, 45, cr.width * 0.55f, 2 * s, Color.white); Bar(cr.center, -45, cr.width * 0.55f, 2 * s, Color.white);
            GL.End();
            GL.PopMatrix();
            if (_hint == null)
            {
                _hint = new GUIStyle { font = font, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                _hint.normal.textColor = new Color(1, 1, 1, 0.95f);
            }
            _hint.fontSize = Mathf.RoundToInt(12 * s);
            GUI.Label(new Rect(10 * s, 0, w - 50 * s, 36 * s), "끌어서 옮기기 · 오른쪽 아래: 크기 · 오른쪽 끌기: 돌려 보기 · 휠: 확대", _hint);
        }

        static void Quad(float x0, float y0, float x1, float y1, Color c)
        {
            GL.Color(c);
            GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0);
        }

        static void Bar(Vector2 c, float deg, float len, float thick, Color col)
        {
            float r = deg * Mathf.Deg2Rad;
            var d = new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * (len * 0.5f);
            var n = new Vector2(-d.y, d.x).normalized * (thick * 0.5f);
            GL.Color(col);
            GL.Vertex3(c.x - d.x + n.x, c.y - d.y + n.y, 0); GL.Vertex3(c.x + d.x + n.x, c.y + d.y + n.y, 0);
            GL.Vertex3(c.x + d.x - n.x, c.y + d.y - n.y, 0); GL.Vertex3(c.x - d.x - n.x, c.y - d.y - n.y, 0);
        }
    }

    /// <summary>
    /// 오버레이 중 카메라 일 두 가지.
    /// 알파 (2026-10-24): 반투명이 끝난 뒤(AfterForwardAlpha) Hidden/VrcProto/OverlayAlpha로 불투명이 덮은 곳의 알파를 1로. 슈퍼샘플링 텍스처에 그릴 때도, 촬영(Capture)도 같은 카메라라 함께 들어간다.
    /// 방송 창 슈퍼샘플링: 오버레이가 켜져 있고 배율이 1보다 크면 미리보기 카메라를 창 × 배율 크기 텍스처(MSAA 그대로, 2배면 4×까지)에 그리고,
    /// 그 뒤에 도는 표시 카메라가 창 크기로 줄여(2배면 겹선형 한 번 = 정확히 2×2 평균, 1.5배는 네 탭 평균 Hidden/VrcProto/OverlayDownsample) 백버퍼에 옮긴다. 알파도 그대로 (투명 배경).
    /// 조정 모드의 테두리·✕(OnGUI)는 카메라들 다음이라 그 위에 그려진다. 끄면 카메라를 다시 화면으로.
    /// 게임 화면과 합치기 (2026-11-12, Overlay.Composite): 배율과 상관없이 미리보기 카메라를 아바타 상자(× 배율) 텍스처에 그리고,
    /// 표시 카메라가 Hidden/VrcProto/OverlayComposite로 잡은 게임 그림을 창 전체에 깐 뒤 그 위 상자 자리에 아바타를 프리멀티플라이드로 얹는다 (알파 1).
    /// </summary>
    public sealed class OverlaySupersample : MonoBehaviour
    {
        Camera _main, _present;
        RenderTexture _rt;
        CommandBuffer _cb, _alphaCb, _keyCb;
        Material _alphaMat, _downMat, _compMat;
        bool _alphaOn, _keyOn, _cbComposite;

        public void Init(Camera main)
        {
            _main = main;
            var go = new GameObject("OverlayPresent");
            go.transform.SetParent(transform, false);
            _present = go.AddComponent<Camera>();
            _present.clearFlags = CameraClearFlags.Nothing;
            _present.cullingMask = 0;
            _present.allowHDR = false; _present.allowMSAA = false;
            _present.depth = main.depth + 10;
            _present.enabled = false;
        }

        void LateUpdate()
        {
            if (_main == null) return;
            SetAlphaFix(Overlay.On && Overlay.AlphaFix);
            SetKey(Overlay.Keyed);
            bool composite = Overlay.Composite;
            if (composite) Overlay.Game?.Update();
            if (composite && Overlay.Game?.TakeError() is string err) Overlay.Send?.Invoke(Msg.Make(Msg.Notice).Set("text", err));
            bool want = Overlay.On && (composite || Overlay.Supersample > 1.01f) && _main.enabled && Screen.width > 0 && Screen.height > 0;
            if (!want) { Off(); return; }
            // 그릴 크기: 합칠 때는 아바타 상자, 아니면 창 전체 (× 배율)
            var box = composite ? Overlay.BoxPixels(Screen.width, Screen.height) : new RectInt(0, 0, Screen.width, Screen.height);
            int w = Mathf.Min(8192, Mathf.RoundToInt(box.width * Overlay.Supersample)), h = Mathf.Min(8192, Mathf.RoundToInt(box.height * Overlay.Supersample));
            int aa = Mathf.Max(1, QualitySettings.antiAliasing);
            if (Overlay.Supersample >= 2) aa = Mathf.Min(aa, 4);
            if (_rt == null || _rt.width != w || _rt.height != h || _rt.antiAliasing != aa || _cbComposite != composite)
            {
                Release();
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = aa, filterMode = FilterMode.Bilinear, name = "OverlaySS" };
                _rt.Create();
                _cb = new CommandBuffer { name = "OverlaySS present" };
                _cbComposite = composite;
                bool exact = Mathf.Abs(Overlay.Supersample - 2) < 0.01f;
                if (composite)
                {
                    if (_compMat == null) { var s = Shader.Find("Hidden/VrcProto/OverlayComposite"); if (s != null) _compMat = new Material(s) { hideFlags = HideFlags.HideAndDontSave }; else Debug.LogWarning("[VrcProto] 합성 셰이더가 빌드에 없음"); }
                    if (_compMat != null) _cb.Blit(_rt, BuiltinRenderTextureType.CameraTarget, _compMat);
                }
                else
                {
                    if (!exact && _downMat == null) { var s = Shader.Find("Hidden/VrcProto/OverlayDownsample"); if (s != null) _downMat = new Material(s) { hideFlags = HideFlags.HideAndDontSave }; }
                    if (!exact && _downMat != null) { _downMat.SetFloat("_Ratio", Overlay.Supersample); _cb.Blit(_rt, BuiltinRenderTextureType.CameraTarget, _downMat); }
                    else _cb.Blit(_rt, BuiltinRenderTextureType.CameraTarget);
                }
                _present.AddCommandBuffer(CameraEvent.AfterEverything, _cb);
                Debug.Log($"[VrcProto] 방송 창 {(composite ? "게임 합성" : "슈퍼샘플링")} ×{Overlay.Supersample:0.#}: {w}×{h} (MSAA {aa}) → {Screen.width}×{Screen.height}");
            }
            if (composite && _compMat != null)
            {
                // 상자(창 UV, 왼쪽 아래 원점) · 줄이는 배율 · 게임 그림 — 머티리얼 값은 명령을 실행할 때 읽힌다
                float W = Screen.width, H = Screen.height;
                _compMat.SetVector("_Box", new Vector4(box.x / W, box.y / H, box.width / W, box.height / H));
                _compMat.SetFloat("_Ratio", Overlay.Supersample);
                var g = Overlay.Game?.Texture;
                _compMat.SetTexture("_GameTex", g != null ? (Texture)g : Texture2D.blackTexture);
                _compMat.SetFloat("_HasGame", g != null ? 1 : 0);
            }
            if (_main.targetTexture == null) _main.targetTexture = _rt;
            _present.enabled = true;
        }

        void Off()
        {
            if (_main != null && _rt != null && _main.targetTexture == _rt) _main.targetTexture = null;
            if (_present != null) _present.enabled = false;
            Release();
        }

        void Release()
        {
            if (_cb != null) { _present.RemoveCommandBuffer(CameraEvent.AfterEverything, _cb); _cb.Release(); _cb = null; }
            if (_rt != null)
            {
                if (_main != null && _main.targetTexture == _rt) _main.targetTexture = null;
                if (RenderTexture.active == _rt) RenderTexture.active = null;
                _rt.Release(); Destroy(_rt); _rt = null;
            }
        }

        /// <summary>알파 바로잡기 명령을 메인 카메라에 붙이고 뗀다.</summary>
        bool AlphaMat()
        {
            if (_alphaMat != null) return true;
            var s = Shader.Find("Hidden/VrcProto/OverlayAlpha");
            if (s == null) { Debug.LogWarning("[VrcProto] 오버레이 알파 셰이더가 빌드에 없음"); return false; }
            _alphaMat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        /// <summary>크로마키 배경 (OBS로 보낼 때): 메인 카메라가 후처리까지 마친 뒤 알파 0 자리에 키 색을 깐다.
        /// 슈퍼샘플링 중이면 큰 텍스처에 깔고 줄이므로 가장자리도 키 색과 섞인 채로 줄어든다.</summary>
        void SetKey(bool on)
        {
            if (on && Overlay.Chroma is Color k && _alphaMat != null) _alphaMat.SetColor("_Key", k);   // 색만 바뀌면 값만 (명령을 실행할 때 읽힌다)
            if (on == _keyOn) return;
            if (on && !AlphaMat()) return;
            _keyOn = on;
            if (on)
            {
                if (Overlay.Chroma is Color c) _alphaMat.SetColor("_Key", c);
                _keyCb = new CommandBuffer { name = "Overlay chroma key" };
                _keyCb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                _keyCb.DrawProcedural(Matrix4x4.identity, _alphaMat, 1, MeshTopology.Triangles, 3);
                _main.AddCommandBuffer(CameraEvent.AfterEverything, _keyCb);
            }
            else if (_keyCb != null)
            {
                _main.RemoveCommandBuffer(CameraEvent.AfterEverything, _keyCb);
                _keyCb.Release(); _keyCb = null;
            }
        }

        void SetAlphaFix(bool on)
        {
            if (on == _alphaOn) return;
            if (on && !AlphaMat()) return;
            _alphaOn = on;
            if (on)
            {
                _alphaCb = new CommandBuffer { name = "Overlay alpha fix" };
                _alphaCb.DrawProcedural(Matrix4x4.identity, _alphaMat, 0, MeshTopology.Triangles, 3);
                _main.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _alphaCb);
            }
            else if (_alphaCb != null)
            {
                _main.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _alphaCb);
                _alphaCb.Release(); _alphaCb = null;
            }
        }

        void OnDestroy() { Off(); if (_main != null) { SetAlphaFix(false); SetKey(false); } }
    }
}
