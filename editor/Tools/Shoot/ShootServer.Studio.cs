// 촬영 서버: 스튜디오 탭의 나머지 (2026-11-01) — 트래킹·말하기 · 스티커 · 오버레이 · 장치 목록.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

sealed partial class ShootServer
{
    // ---------------- 트래킹·말하기 ----------------
    void ApplyFace(SceneShot s, SceneProblems problems, JNode item)
    {
        if (Changed("faceMap", s.FaceMap.ToJson())) Send(Msg.Make(Msg.FaceTracking).Set("map", s.FaceMap));
        if (Changed("keys", s.ShapeLayer.ToJson())) Send(s.ShapeLayer);
        var lip = s.Lip.IsNull ? JNode.Obj().Set("on", false) : s.Lip;
        if (Changed("lip", lip.ToJson()))
        {
            Send(Msg.Make(Msg.FaceTracking).Set("lip", lip));
            if (!s.Lip.IsNull)
            {
                // 소리가 입 값으로 들어올 때까지 잠깐 (사진은 그 순간의 입)
                var st = TrackingStatus(x => x["lip"]["level"].AsDouble > 0.15, 1.5)["lip"];
                item.Set("lip", st);
                if (!st["ok"].AsBool) problems.Warn($"[{s.Name}] 말하기: {st["text"].AsString}");
            }
        }

        var t = s.Tracking;
        var src = t.Has("source") ? t["source"].AsString : "off";
        var options = Msg.Make(Msg.FaceTracking).Set("source", src);
        foreach (var kv in t.Props)
            if (kv.Key is not ("source" or "wait" or "freeze" or "recordAnim" or "calibrate" or "test")) options.Set(kv.Key, kv.Value);
        bool started = Changed("tracking", options.ToJson());
        if (started) Send(options);
        if (Changed("trackTest", t["test"].AsString)) Send(Msg.Make(Msg.FaceTracking).Set("test", t["test"].AsString));
        if (src == "off") return;

        // 얼굴을 찾을 때까지 (카메라를 여는 데 시간이 걸린다). 못 찾아도 그대로 찍고 알린다
        JNode status;
        if (started) status = TrackingStatus(x => x["ok"].AsBool && (x["face"].AsBool || src != "webcam" && src != "image" && x["fps"].AsDouble > 0), t.Has("wait") ? t["wait"].AsDouble : 4);
        else
        {
            // 이미 켜져 있다: 상태는 1초마다 오므로 바로 달라고 한다
            Send(Msg.Make(Msg.FaceTracking).Set("snapshot", true));
            status = TrackingStatus(x => x["snapshot"].AsBool, 2);
        }
        item.Set("tracking", JNode.Obj().Set("source", status["source"]).Set("ok", status["ok"]).Set("text", status["text"]).Set("face", status["face"]).Set("fps", status["fps"]).Set("mapped", status["mapped"]).Set("mode", status["mode"]));
        if (!status["ok"].AsBool || !status["face"].AsBool && (src == "webcam" || src == "image")) problems.Warn($"[{s.Name}] 트래킹: {status["text"].AsString}");

        if (t["calibrate"].AsBool)
        {
            Send(Msg.Make(Msg.FaceTracking).Set("calibrate", true));
            var c = TrackingStatus(x => x["neutral"].Kind == JKind.Object, 5);
            if (c["neutral"].Kind == JKind.Object) item.Set("neutral", c["neutral"]);
            else problems.Warn($"[{s.Name}] 캘리브레이션 답이 없어요");
        }
        if (t["recordAnim"].Kind == JKind.Object)
        {
            Send(Msg.Make(Msg.FaceTracking).Set("capture", true));
            Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(t["recordAnim"]["seconds"].AsDouble, 0.5, 600)));
            Send(Msg.Make(Msg.FaceTracking).Set("capture", false));
            var c = TrackingStatus(x => x.Has("capture"), 20)["capture"];
            var shapes = c["shapes"].Items.Select(x => (x["r"].AsString, x["s"].AsString)).ToList();
            var tracks = shapes.Count == 0 ? null : AnimWriter.FromSamples(_doc!.Original, shapes, c["times"].Items.Select(x => x.AsFloat).ToList(), c["values"].Items.Select(x => x.ToFloatArray()).ToList());
            if (tracks == null || tracks.Count == 0) problems.Warn($"[{s.Name}] 트래킹 녹화: 움직인 블렌드쉐이프가 없어요");
            else
            {
                var path = t["recordAnim"]["path"].AsString;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AnimWriter.Save(path, Path.GetFileNameWithoutExtension(path), tracks);
                item.Set("trackingAnim", path);
            }
        }
        if (t["freeze"].AsBool)
        {
            Send(Msg.Make(Msg.FaceTracking).Set("snapshot", true));
            var snap = TrackingStatus(x => x["snapshot"].AsBool, 5);
            var arr = JNode.Arr();
            foreach (var x in snap["shapes"].Items) arr.Add(JNode.Obj().Set("renderer", x["r"]).Set("shape", x["s"]).Set("weight", x["v"]));
            if (arr.Count == 0) { problems.Warn($"[{s.Name}] 고정할 얼굴 값이 아직 없어요"); return; }
            s.Expression = arr;
            Send(Msg.Make(Msg.SetExpression).Set("shapes", arr));
            _sent["expr"] = arr.ToJson();
            Send(Msg.Make(Msg.FaceTracking).Set("source", "off"));
            _sent["tracking"] = "";
        }
    }

    /// <summary>조건에 맞는 트래킹 상태가 올 때까지 (없으면 마지막 것).</summary>
    JNode TrackingStatus(Func<JNode, bool> ok, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var last = Msg.Make(Msg.TrackingStatus);
        while (DateTime.UtcNow < until)
        {
            try { last = Wait(Msg.TrackingStatus, Math.Max(100, (int)(until - DateTime.UtcNow).TotalMilliseconds)); }
            catch (TimeoutException) { break; }
            if (ok(last)) return last;
        }
        return last;
    }

    JNode Devices()
    {
        EnsureRenderer();
        Drain();
        Send(Msg.Make(Msg.FaceTracking).Set("devices", true));
        var cams = TrackingStatus(x => x.Has("devices"), 8)["devices"];
        Send(Msg.Make(Msg.FaceTracking).Set("mics", true));
        var mics = TrackingStatus(x => x.Has("mics"), 8)["mics"];
        return JNode.Obj().Set("ok", true).Set("webcams", cams).Set("mics", mics);
    }

    /// <summary>이 아바타의 자동 연결 (트래킹 값 → 블렌드쉐이프).</summary>
    JNode FaceLinks(string avatar)
    {
        var notes = new List<string>();
        EnsureRenderer();
        EnsureAvatar(Path.GetFullPath(avatar), notes);
        Drain();
        Send(Msg.Make(Msg.FaceTracking).Set("links", true));
        var st = TrackingStatus(x => x.Has("auto"), 8);
        var names = _doc!.Original.Renderers.ToDictionary(r => r.Id, r => SceneSpec.RendererName(_doc.Original, r));
        var auto = JNode.Arr();
        foreach (var a in st["auto"].Items) auto.Add(JNode.Obj().Set("src", a["src"]).Set("renderer", names.TryGetValue(a["r"].AsString, out var n) ? n : a["r"].AsString).Set("shape", a["s"]));
        return JNode.Obj().Set("ok", true).Set("mode", st["mode"]).Set("perfectSync", st["perfectSync"]).Set("ftLayers", st["ftLayers"]).Set("auto", auto);
    }

    // ---------------- 스티커 ----------------
    /// <summary>규격 비율로 크게 투명 촬영 → 아바타만 잘라 규격에 맞춘다 (에디터 스티커와 같은 계산). 크기가 여럿이면 name_크기.png.</summary>
    string? Stickers(SceneShot s, string outDir, JNode item, bool draft)
    {
        var spec = Sticker.Find(s.Sticker["spec"].AsString);
        double k = (draft ? 800.0 : 1600.0) / Math.Max(spec.W, spec.H);
        int cw = (int)Math.Round(spec.W * k), ch = (int)Math.Round(spec.H * k);
        var tmp = Path.Combine(Path.GetTempPath(), "VrcProtoShoot", $"sticker_{Guid.NewGuid():N}.png");
        Drain();
        Send(Msg.Make(Msg.Capture).Set("path", tmp).Set("width", cw).Set("height", ch).Set("transparent", true).Set("supersample", draft ? 1 : 2));
        var ans = Wait(Msg.CaptureDone, 300000);
        if (!ans["ok"].AsBool) return ans["error"].AsString;
        try
        {
            var shot = Sticker.DecodePng(File.ReadAllBytes(tmp));
            if (shot == null) return "찍은 PNG를 읽지 못했어요";
            var color = s.Sticker["color"].AsString;
            var list = Sticker.Make(shot, new Sticker.Options
            {
                Spec = spec.Key, OutlineWidth = color == "none" ? 0 : s.Sticker["outline"].AsInt,
                OutlineColor = color == "black" ? new byte[] { 0, 0, 0 } : new byte[] { 255, 255, 255 },
                Brightness = s.Brightness, Contrast = s.Contrast, Saturation = s.Saturation,
            });
            if (list.Count == 0) return "화면에 아바타가 보이지 않아요. 카메라를 맞춰 주세요";
            var files = JNode.Arr();
            foreach (var (size, img) in list)
            {
                var bytes = Sticker.EncodePng(img);
                var path = Path.Combine(outDir, list.Count == 1 ? s.Name + ".png" : $"{s.Name}_{size}.png");
                File.WriteAllBytes(path, bytes);
                var f = JNode.Obj().Set("path", path).Set("width", img.W).Set("height", img.H).Set("kb", Math.Round(bytes.Length / 1024.0, 1));
                if (spec.MaxKB > 0 && bytes.Length > spec.MaxKB * 1024) f.Set("overLimitKB", spec.MaxKB);
                files.Add(f);
            }
            item.Set("path", files[0]["path"]).Set("width", files[0]["width"]).Set("height", files[0]["height"]).Set("stickers", files);
            return null;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // ---------------- 오버레이 ----------------
    bool _wantWindow;
    public bool OverlayOn { get; private set; }
    volatile bool _overlayClosed;   // 조정 모드의 ✕

    /// <summary>방송 오버레이: 창 있는 렌더러로 다시 띄워 화면 위에 투명하게 올린다. 끌 때까지 렌더러를 남긴다. 켠 동안의 run은 같은 창에 바로 보인다.</summary>
    JNode OverlayCommand(JNode req)
    {
        CheckOverlayClosed();
        var action = req["action"].AsString;
        if (action == "off")
        {
            if (OverlayOn) { _wantWindow = false; Discard(); }
            return JNode.Obj().Set("ok", true).Set("on", false);
        }
        if (action == "status") return OverlayStatus();
        _wantWindow = true;
        var r = Prepare(req, out var shots, out _, out _);
        if (r != null) { if (!OverlayOn) _wantWindow = false; return r; }
        var shot = shots[0];
        var problems = new SceneProblems();
        var item = JNode.Obj();
        Apply(shot, problems, item);
        var ov = shot.Overlay;
        var state = Frame(shot.Camera, (float)ov["aspect"].AsDouble);
        var hwnd = WindowHandle();
        if (!Place(hwnd, ov)) { problems.Error("오버레이를 띄울 화면을 찾지 못했어요"); return Problems(problems); }
        Send(Msg.Make(Msg.Overlay).Set("enabled", true).Set("hwnd", hwnd.ToInt64()).Set("adjust", ov["adjust"].AsBool).Set("supersample", (float)ov["supersample"].AsDouble).Set("fps", ov["fps"].AsInt));
        OverlayOn = true;
        Thread.Sleep(500);
        var res = OverlayStatus().Set("camera", state);
        if (item.Has("tracking")) res.Set("tracking", item["tracking"]);
        var w = JNode.Arr(); foreach (var x in problems.Warnings.Concat(_rendererLog)) w.Add(x);
        return res.Set("warnings", w);
    }

    JNode OverlayStatus()
    {
        var o = JNode.Obj().Set("ok", true).Set("on", OverlayOn);
        if (OverlayOn && _proc != null && !_proc.HasExited && Win.GetWindowRect(WindowHandle(), out var rc))
            o.Set("rect", JNode.Obj().Set("x", rc.Left).Set("y", rc.Top).Set("w", rc.Right - rc.Left).Set("h", rc.Bottom - rc.Top));
        return o;
    }

    void CheckOverlayClosed()
    {
        if (!_overlayClosed) return;
        _overlayClosed = false; _wantWindow = false;
        Discard();
    }

    IntPtr WindowHandle()
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            _proc!.Refresh();
            if (_proc.MainWindowHandle != IntPtr.Zero) return _proc.MainWindowHandle;
            Thread.Sleep(100);
        }
        throw new TimeoutException("렌더러 창을 찾지 못했어요");
    }

    /// <summary>자리를 정한다 (에디터 OverlayController.Place·UnityView.EnterOverlay와 같은 규칙). pos = 아래 가운데 점.</summary>
    static bool Place(IntPtr hwnd, JNode ov)
    {
        var mons = Win.Monitors();
        if (mons.Count == 0) return false;
        var wa = mons[Math.Clamp(ov["monitor"].AsInt, 0, mons.Count - 1)];
        int H = wa.Bottom - wa.Top, W = wa.Right - wa.Left;
        int h = (int)(H * ov["size"].AsDouble), w = (int)(h * ov["aspect"].AsDouble);
        if (w > W) { h = (int)(h * (double)W / w); w = W; }
        const int m = 16;
        var corner = ov["corner"].AsString;
        int x, y;
        if (ov.Has("pos"))
        {
            x = Math.Clamp(ov["pos"][0].AsInt - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
            y = Math.Clamp(ov["pos"][1].AsInt - h, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        }
        else
        {
            x = corner is "bl" or "tl" ? wa.Left + m : corner == "center" ? wa.Left + (W - w) / 2 : wa.Right - w - m;
            y = corner is "tr" or "tl" ? wa.Top + m : corner == "center" ? wa.Top + (H - h) / 2 : wa.Bottom - h - m;
        }
        bool through = !ov["adjust"].AsBool;
        Win.SetWindowLongPtr(hwnd, Win.GWL_STYLE, new IntPtr(unchecked((int)(Win.WS_POPUP | Win.WS_VISIBLE))));
        Win.SetWindowLongPtr(hwnd, Win.GWL_EXSTYLE, new IntPtr(Win.WS_EX_TOOLWINDOW | (through ? Win.WS_EX_LAYERED | Win.WS_EX_TRANSPARENT | Win.WS_EX_NOACTIVATE : 0)));
        if (through) Win.SetLayeredWindowAttributes(hwnd, 0, 255, Win.LWA_ALPHA);
        return Win.SetWindowPos(hwnd, ov["top"].AsBool ? new IntPtr(-1) : new IntPtr(-2), x, y, w, h, Win.SWP_SHOWWINDOW | Win.SWP_FRAMECHANGED | Win.SWP_NOACTIVATE);
    }

    static class Win
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, LWA_ALPHA = 2;
        public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
        public const int SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20, SWP_NOACTIVATE = 0x10;
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        delegate bool MonitorEnum(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, int flags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, int flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        /// <summary>작업 영역 목록 (주 모니터가 먼저).</summary>
        public static List<RECT> Monitors()
        {
            var list = new List<(RECT Work, bool Primary)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (mon, _, __, ___) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(mon, ref mi)) list.Add((mi.rcWork, (mi.dwFlags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(x => x.Primary).Select(x => x.Work).ToList();
        }
    }

    /// <summary>창 좌표를 실제 픽셀로 쓴다 (에디터의 오버레이 좌표와 같게).</summary>
    public static void DpiAware() { try { Win.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { } }
}
