// 촬영 서버 본체: 렌더러를 하나 띄워 두고 장면을 차례로 적용해 찍는다.
// 항목마다 마지막으로 보낸 값을 기억해 바뀐 것만 보낸다 (_sent) — 조명만 바꿔 다시 찍을 때 아바타를 다시 올리지 않게.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

sealed partial class ShootServer : IDisposable
{
    PipeChannel? _ch;
    bool _windowed;
    Process? _proc;
    readonly BlockingCollection<JNode> _inbox = new BlockingCollection<JNode>();
    JNode _ready = JNode.Null;

    // 지금 렌더러에 올라간 아바타
    string _avatarKey = "";
    string _irKey = "";   // 올라간 IR (경로|시각): 같은 IR의 다른 세션은 다시 올리지 않고 값만 바꾼다
    Document? _doc;
    JNode _loaded = JNode.Null;
    bool _dirty;   // raw로 무엇이 바뀌었는지 모른다 → 다음 run에 다시 올린다

    readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
    readonly List<string> _rendererLog = new List<string>();

    public void Dispose() => Discard();

    /// <summary>렌더러를 끝낸다. 다음 요청에 새로 띄운다.</summary>
    public void Discard()
    {
        try { _ch?.Send(Msg.Make(Msg.Quit)); } catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException) { }
        try { if (_proc != null && !_proc.WaitForExit(3000)) _proc.Kill(); } catch (InvalidOperationException) { }
        OverlayOn = false;
        _ch = null; _proc = null; _ready = JNode.Null; _avatarKey = ""; _irKey = ""; _doc = null; _sent.Clear();
        while (_inbox.TryTake(out _)) { }
    }

    public JNode Handle(JNode req)
    {
        switch (req["cmd"].AsString)
        {
            case "run": CheckOverlayClosed(); return Run(req);
            case "overlay": return OverlayCommand(req);
            case "edit": CheckOverlayClosed(); return EditCommand(req);
            case "apply": CheckOverlayClosed(); return ApplyCommand(req);
            case "devices": CheckOverlayClosed(); return Devices();
            case "facelinks": CheckOverlayClosed(); return FaceLinks(req["avatar"].AsString);
            case "schema":
                EnsureRenderer();
                return JNode.Obj().Set("ok", true).Set("environment", _ready["environment"]);
            case "status":
                return JNode.Obj().Set("ok", true).Set("running", true).Set("renderer", _proc != null && !_proc.HasExited).Set("avatar", _doc?.IrPath ?? "");
            default: return JNode.Obj().Set("ok", false).Set("errors", JNode.Arr().Add("모르는 명령: " + req["cmd"].AsString));
        }
    }

    // ---------------- 렌더러 ----------------
    static string FindRenderer()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { "../Unity/Build/VrcProtoRenderer.exe", "av/Renderer/VrcProtoRenderer.exe", "Renderer/VrcProtoRenderer.exe" })
        {
            var p = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(p)) return p;
        }
        throw new IOException("렌더러를 찾지 못했어요. editor 폴더에서 build.cmd renderer로 빌드해 주세요");
    }

    void EnsureRenderer()
    {
        if (_ch != null && _ch.IsOpen && _proc != null && !_proc.HasExited && _windowed == _wantWindow) return;
        Discard();
        var name = PipeChannel.NewPipeName();
        var server = new PipeChannel.Server(name);
        var accept = server.AcceptAsync(CancellationToken.None);
        var psi = new ProcessStartInfo(FindRenderer()) { UseShellExecute = false, CreateNoWindow = true };
        _windowed = _wantWindow;
        if (!_windowed) psi.ArgumentList.Add("-batchmode");   // 오버레이만 창이 있어야 한다
        foreach (var a in new[] { "-pipe", name, "-logFile", Path.Combine(Path.GetTempPath(), "VrcProtoShoot", "renderer.log"), "-screen-width", "1280", "-screen-height", "720", "-screen-fullscreen", "0" }) psi.ArgumentList.Add(a);
        _proc = Process.Start(psi)!;
        if (!accept.Wait(60000)) { _proc.Kill(); throw new TimeoutException("렌더러가 연결되지 않았어요"); }
        _ch = accept.Result;
        var proc = _proc;
        _ch.OnMessage += m =>
        {
            // 조정 모드의 ✕: 요청을 기다리지 않고 바로 숨긴다. 다음 요청에서 정리한다
            if (Msg.Type(m) == Msg.Overlay && m["close"].AsBool) { _overlayClosed = true; try { proc.Kill(); } catch (InvalidOperationException) { } return; }
            _inbox.Add(m);
        };
        _ch.OnClosed += _ => _inbox.Add(Msg.Make("_closed"));
        _ch.Start();
        _ch.Send(Msg.Make(Msg.Hello).Set("protocolVersion", Msg.ProtocolVersion));
        _ready = Wait(Msg.Ready, 60000);
        Send(Msg.Make(Msg.SetView).Set("pauseTime", true));
    }

    void Send(JNode m) => _ch!.Send(m);

    /// <summary>그 종류의 메시지가 올 때까지. 렌더러 경고·오류는 모아 둔다 (결과의 warnings로).</summary>
    JNode Wait(string type, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
            if (_inbox.TryTake(out var m, 100))
            {
                var t = Msg.Type(m);
                if (t == "_closed") throw new IOException("렌더러가 끝나 버렸어요");
                if (t == Msg.Log) { Keep(m); continue; }
                if (t == type) return m;
            }
        throw new TimeoutException($"렌더러 답을 기다리다 시간이 지났어요: {type}");
    }

    void Keep(JNode log)
    {
        var level = log["level"].AsString;
        if (level != "warn" && level != "error") return;
        var text = log["text"].AsString.Split('\n')[0];
        if (text.StartsWith("[VrcProto] ")) text = text.Substring(11);
        if (text.StartsWith("Releasing render texture")) return;   // 촬영 때마다 나오는 Unity 안내 — 결과와 무관
        if (!_rendererLog.Contains(text) && _rendererLog.Count < 30) _rendererLog.Add(text);
    }

    /// <summary>받아 둔 것을 비운다 (이전 카메라 알림을 답으로 착각하지 않게).</summary>
    void Drain() { while (_inbox.TryTake(out var m)) { if (Msg.Type(m) == Msg.Log) Keep(m); if (Msg.Type(m) == "_closed") throw new IOException("렌더러가 끝나 버렸어요"); } }

    bool Changed(string key, string value)
    {
        if (_sent.TryGetValue(key, out var old) && old == value) return false;
        _sent[key] = value;
        return true;
    }

    // ---------------- 아바타 올리기 ----------------
    void EnsureAvatar(string path, List<string> notes)
    {
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (key == _avatarKey && !_dirty && _doc != null) return;
        var sw = Stopwatch.StartNew();
        Session.Loaded? session = null;
        var ir = path;
        if (Session.IsSessionFile(path)) { session = Session.Read(path); ir = session.IrPath; }
        if (!File.Exists(ir)) throw new IOException("아바타 IR이 없어요: " + ir);
        var changes = session != null && session.Changeset.Kind == JKind.Array ? session.Changeset : JNode.Arr();
        var variants = session != null && session.Variants.Kind == JKind.Array ? session.Variants : JNode.Arr();
        var irKey = $"{ir}|{File.GetLastWriteTimeUtc(ir).Ticks}";
        // 같은 IR이면 다시 올리지 않는다 (2026-11-02 편집 ↔ 촬영을 오갈 때): 값만 바꾸면 RenderSync가 바뀐 주소를 보낸다
        if (irKey == _irKey && !_dirty && _doc != null)
        {
            _doc.LoadChangeset(changes);
            _doc.LoadVariants(variants);
            _avatarKey = key;
            _sent.Clear();
            Shoot.Log($"값만 바꿈 {sw.ElapsedMilliseconds} ms: {path}");
            return;
        }
        var doc = new Document(IrScene.Load(ir), ir);
        Drain();
        Send(Msg.Make(Msg.LoadScene).Set("irPath", ir));
        _loaded = Wait(Msg.SceneLoaded, 300000);
        doc.SetSchemas(MaterialSchema.ReadAll(_loaded), true);
        doc.LoadChangeset(changes);
        doc.LoadVariants(variants);
        // 늘 붙여 둔다: 편집(apply)·값 바꾸기가 렌더러에 바로 보인다
        new RenderSync(doc, new Sink(_ch!)).SendAllOverrides();
        foreach (var w in _loaded["warnings"].Items) if (notes.Count < 10) notes.Add("불러오기: " + w.AsString);
        if (!_loaded["pose"]["ok"].AsBool && _loaded["pose"].Has("problem")) notes.Add("이 아바타는 자세를 바꾸지 못해요: " + _loaded["pose"]["problem"].AsString);
        _doc = doc; _avatarKey = key; _irKey = irKey; _dirty = false;
        _sent.Clear();
        Shoot.Log($"아바타 올림 {sw.ElapsedMilliseconds} ms: {path}");
    }

    sealed class Sink : IRenderSink
    {
        readonly PipeChannel _c; public Sink(PipeChannel c) { _c = c; }
        public bool IsConnected => _c.IsOpen;
        public void Send(JNode m) => _c.Send(m);
    }

    SceneContext Context(string baseDir)
    {
        var doc = _doc!;
        var rest = new Dictionary<string, float>();
        foreach (var kv in _loaded["pose"]["rest"].Props) rest[kv.Key] = kv.Value.AsFloat;
        var expr = JNode.Arr();
        foreach (var (r, s, v) in FaceExpression.Shapes(doc.Get(Addr.Expression))) expr.Add(JNode.Obj().Set("renderer", r).Set("shape", s).Set("weight", v));
        return new SceneContext
        {
            Scene = doc.Original, Rest = rest, BaseDir = baseDir, EnvSchema = _ready["environment"], PoseDir = Shoot.PoseDir,
            BaseEnvironment = doc.Get(Addr.Environment), BasePose = doc.Get(Addr.Pose), BaseExpression = expr,
            BaseFaceMap = doc.Get(Addr.FaceMap), BaseShapeKeys = doc.Get(Addr.ShapeKeys), SavedCameras = SavedCameras(),
            PresetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", "studio-presets"),
        };
    }

    /// <summary>에디터에 저장한 카메라 위치 (읽기만). 읽지 못하면 빈 목록.</summary>
    static JNode SavedCameras()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", "avhost.json");
            return File.Exists(path) ? JNode.Parse(File.ReadAllText(path))["studio"]["cameras"] : JNode.Arr();
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is FormatException) { return JNode.Arr(); }
    }

    JNode Problems(SceneProblems problems)
    {
        var e = JNode.Arr(); foreach (var x in problems.Errors) e.Add(x);
        var w = JNode.Arr(); foreach (var x in problems.Warnings) w.Add(x);
        return JNode.Obj().Set("ok", false).Set("errors", e).Set("warnings", w);
    }

    /// <summary>장면을 읽어 렌더러·아바타를 준비하고 장들을 검사한다. 문제가 있으면 답을 돌려준다 (없으면 null).</summary>
    JNode? Prepare(JNode req, out List<SceneShot> shots, out JNode scene, out string baseDir)
    {
        shots = new List<SceneShot>(); scene = JNode.Null; baseDir = "";
        var problems = new SceneProblems();
        var scenePath = req["scene"].AsString;
        if (!File.Exists(scenePath)) { problems.Error("장면 파일이 없어요: " + scenePath); return Problems(problems); }
        try { scene = JNode.Parse(File.ReadAllText(scenePath)); }
        catch (Exception e) when (e is InvalidDataException || e is FormatException || e is IndexOutOfRangeException) { problems.Error($"장면 JSON을 읽지 못했어요: {e.Message}"); return Problems(problems); }
        baseDir = Path.GetDirectoryName(scenePath)!;
        var avatarRel = scene["avatar"].AsString;
        if (avatarRel.Length == 0) { problems.Error("장면에 avatar가 없어요 (x.ir.json 또는 세션.alesav). 목록: dotnet bin/Shoot.dll avatars"); return Problems(problems); }
        var avatar = Path.GetFullPath(Path.Combine(baseDir, avatarRel));
        if (!File.Exists(avatar)) { problems.Error("아바타 파일이 없어요: " + avatar); return Problems(problems); }

        _rendererLog.Clear();
        _notes.Clear();
        EnsureRenderer();
        EnsureAvatar(avatar, _notes);
        var ctx = Context(baseDir);
        var only = req.Has("only") ? new HashSet<string>(req["only"].AsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase) : null;
        var expanded = SceneSpec.Expand(scene);
        if (only != null)
        {
            foreach (var n in only) if (!expanded.Any(s => string.Equals(s["name"].AsString, n, StringComparison.OrdinalIgnoreCase))) problems.Error($"--only: 그런 장이 없어요: \"{n}\"{SceneSpec.Suggest(n, expanded.Select(s => s["name"].AsString))}");
            expanded = expanded.Where(s => only.Contains(s["name"].AsString)).ToList();
        }
        // 찍기 전에 전부 검사한다 — 몇 장 찍고 나서 틀린 이름에 멈추지 않게
        shots = expanded.Select(s => SceneSpec.Compile(s, ctx, problems)).ToList();
        if (problems.Errors.Count > 0) return Problems(problems);
        _prepareWarnings = problems.Warnings;
        return null;
    }
    readonly List<string> _notes = new List<string>();
    List<string> _prepareWarnings = new List<string>();

    // ---------------- 실행 ----------------
    JNode Run(JNode req)
    {
        var total = Stopwatch.StartNew();
        var failed = Prepare(req, out var shots, out var scene, out var baseDir);
        if (failed != null) return failed;
        var problems = new SceneProblems();
        foreach (var w in _prepareWarnings) problems.Warn(w);
        var notes = _notes;
        var scenePath = req["scene"].AsString;
        bool draft = req["draft"].AsBool;

        var outBase = req.Has("out") ? req["out"].AsString : scene.Has("out") ? Path.GetFullPath(Path.Combine(baseDir, scene["out"].AsString)) : Path.Combine(baseDir, "shots", Path.GetFileNameWithoutExtension(scenePath));
        var outDir = draft ? Path.Combine(outBase, "draft") : outBase;
        Directory.CreateDirectory(outDir);

        var results = JNode.Arr();
        var photos = new List<string>();
        bool anyFailed = false;
        foreach (var shot in shots)
        {
            var sw = Stopwatch.StartNew();
            if (draft) Draft(shot);
            var item = JNode.Obj().Set("name", shot.Name).Set("width", shot.Width).Set("height", shot.Height);
            Apply(shot, problems, item);
            var state = Frame(shot.Camera, shot.Sticker.IsNull ? (float)shot.Width / shot.Height : (float)SpecAspect(shot));
            string? err;
            if (!shot.Sticker.IsNull) err = Stickers(shot, outDir, item, draft);
            else if (shot.Record.IsNull)
            {
                var path = Path.Combine(outDir, shot.Name + ".png");
                err = Photo(shot, path);
                item.Set("path", path);
                if (err == null) photos.Add(path);
            }
            else
            {
                err = Video(shot, outDir, state, out var path);
                item.Set("path", path);
            }
            if (shot.SaveStudioPreset != null && err == null) { StudioPreset.Save(shot.SaveStudioPreset, StudioJson(shot)); item.Set("studioPreset", shot.SaveStudioPreset); }
            if (shot.SaveExpression != null && err == null) item.Set("expressionAnim", SaveExpressionAnim(shot, shot.SaveExpression, problems));
            if (shot.EmbedSession && shot.Record.IsNull && shot.Sticker.IsNull && err == null) PngText.EmbedSession(item["path"].AsString, SessionDoc(shot), state, StudioJson(shot));
            if (err != null) { item.Set("error", err); anyFailed = true; }
            item.Set("camera", state).Set("seconds", Math.Round(sw.Elapsed.TotalSeconds, 2));
            if (req["session"].AsBool && err == null) item.Set("session", SaveSession(shot, state, outDir));
            results.Add(item);
            if (shot.Raw.Count > 0) _dirty = true;
        }

        var result = JNode.Obj().Set("ok", !anyFailed).Set("draft", draft).Set("out", outDir).Set("shots", results);
        if (photos.Count > 1)
            try { var sheet = Path.Combine(outDir, "_sheet.png"); ContactSheet.Save(photos, sheet); result.Set("sheet", sheet); }
            catch (Exception e) when (e is IOException || e is InvalidDataException) { problems.Warn("한눈에 보기를 만들지 못했어요: " + e.Message); }
        var warnings = JNode.Arr();
        foreach (var w in problems.Warnings.Concat(notes).Concat(_rendererLog.Where(l => !notes.Contains("불러오기: " + l)))) warnings.Add(w);
        result.Set("warnings", warnings).Set("avatarBounds", _loaded["bounds"]).Set("seconds", Math.Round(total.Elapsed.TotalSeconds, 2));
        return result;
    }

    /// <summary>시안: 긴 변 960, 지터 누적 없이 — 구도·포즈·조명을 빠르게 보는 용도.</summary>
    static void Draft(SceneShot s)
    {
        double k = Math.Min(1, 960.0 / Math.Max(s.Width, s.Height));
        s.Width = Math.Max(16, (int)Math.Round(s.Width * k) & ~1); s.Height = Math.Max(16, (int)Math.Round(s.Height * k) & ~1);
        s.Supersample = 1; s.CaptureAa = 1;
    }

    static double SpecAspect(SceneShot s) { var sp = Sticker.Find(s.Sticker["spec"].AsString); return (double)sp.W / sp.H; }

    void Apply(SceneShot s, SceneProblems problems, JNode item)
    {
        if (Changed("env", s.Environment.ToJson())) Send(Msg.Make(Msg.SetEnvironment).Set("providers", s.Environment));
        if (Changed("quality", $"{s.QualityStudio}|{s.Ao}|{s.CaptureAa}")) Send(Msg.Make(Msg.SetQuality).Set("studio", s.QualityStudio).Set("ao", s.Ao).Set("captureAa", s.CaptureAa));
        if (Changed("avatarOnly", s.AvatarOnly.ToJson())) Send(Msg.Make(Msg.SetView).Set("avatarOnly", s.AvatarOnly));
        if (Changed("floorFit", s.FloorFit.ToString())) Send(Msg.Make(Msg.SetView).Set("floorFit", s.FloorFit));
        if (Changed("phys", s.PhysBones.ToString())) Send(Msg.Make(Msg.SetPhysBones).Set("enabled", s.PhysBones).Set("legGuard", true));

        // 스튜디오: 처음에 틀을 만들고 바뀐 부분만 (props는 렌더러가 같은 파일을 다시 불러오지 않는다)
        var st = Msg.Make(Msg.Studio);
        bool studio = false;
        if (Changed("studio", "on")) { st.Set("enabled", true); studio = true; }
        if (Changed("bg", s.Background.ToJson())) { st.Set("background", s.Background); studio = true; }
        if (Changed("bgExp", $"{s.BgExposure}|{s.BgRotation}")) { st.Set("exposure", s.BgExposure).Set("rotation", s.BgRotation); studio = true; }
        if (Changed("floor", s.Floor.ToJson())) { st.Set("shadowFloor", s.Floor); studio = true; }
        if (Changed("props", s.Props.ToJson())) { st.Set("props", s.Props); studio = true; }
        if (Changed("dof", s.Dof.ToJson())) { st.Set("dof", s.Dof); studio = true; }
        if (studio)
        {
            Drain();
            Send(st);
            var ans = Wait(Msg.StudioState, 120000);
            if (ans["error"].AsStringOrNull is string e) problems.Warn($"[{s.Name}] 스튜디오: {e}");
            foreach (var pr in ans["props"].Items) if (!pr["ok"].AsBool) problems.Warn($"[{s.Name}] 소품 {pr["id"].AsString}: {pr["error"].AsString}");
        }

        var doc = _doc!;
        foreach (var r in doc.Original.Renderers)
        {
            bool on = s.Renderers.TryGetValue(r.Id, out var v) ? v : doc.Get(Addr.RendererEnabled(r.Id)).AsBool;
            if (Changed("ren:" + r.Id, on.ToString())) Send(Msg.Make(Msg.SetRendererEnabled).Set("renderer", r.Id).Set("enabled", on));
        }

        bool moved = false;
        if (Changed("place", string.Join(",", s.AvatarTrs))) { Send(Msg.Make(Msg.SetAvatarTransform).Set("trs", JNode.FloatArray(s.AvatarTrs))); moved = true; }
        if (Changed("pose", s.Pose.ToJson())) { Send(Msg.Make(Msg.SetPose).Set("pose", s.Pose)); moved = true; }
        if (Changed("expr", s.Expression.ToJson())) Send(Msg.Make(Msg.SetExpression).Set("shapes", s.Expression));

        if (Changed("guests", s.Guests.ToJson()))
        {
            Drain();
            Send(Msg.Make(Msg.Guests).Set("list", s.Guests));
            var ans = Wait(Msg.GuestsState, 120000);
            foreach (var g in ans["guests"].Items) if (!g["ok"].AsBool) problems.Warn($"[{s.Name}] 함께 세운 아바타 {g["id"].AsString}: {g["error"].AsString}");
            moved = true;
        }

        // PhysBone은 시간이 흘러야 자세를 따라간다 (pauseTime이면 멈춰 있다): 머리카락·치마가 자리 잡게 그 시간만큼 바로 돌린다.
        // (2026-11-12: 예전에는 시간을 풀고 실제로 기다려 장마다 0.8초가 걸리고 결과가 프레임 시간에 따라 조금씩 달랐다)
        if (moved && s.PhysBones && s.Settle > 0)
        {
            Thread.Sleep(100);   // 자세·바닥 맞추기가 뼈에 들어간 다음 프레임부터
            Send(Msg.Make(Msg.SetPhysBones).Set("reset", true).Set("advance", s.Settle));
        }
        else if (moved) Thread.Sleep(100);   // 스킨드 메시 범위가 새 자세로 바뀐 뒤에 구도를 잡게
        ApplyFace(s, problems, item);
        foreach (var raw in s.Raw.Items) Send(raw);
    }

    /// <summary>구도를 잡고 최종 카메라 상태를 돌려준다 (결과에 넣어, 다음 장면에서 그대로 쓰거나 고칠 수 있게).</summary>
    JNode Frame(JNode c, float aspect)
    {
        if (c.Has("state"))
        {
            var direct = SceneSpec.CameraState(c["state"], c, keepYaw: true);
            Send(Msg.Make(Msg.SetCamera).Set("state", direct));
            return direct;
        }
        double fov = c.Has("fov") ? c["fov"].AsDouble : 30;
        Send(Msg.Make(Msg.SetCamera).Set("state", JNode.Obj().Set("fov", fov).Set("ortho", false)));
        Drain();
        if (c.Has("bone")) Send(Msg.Make(Msg.SetCamera).Set("frameBone", c["bone"].AsString).Set("size", c.Has("size") ? c["size"].AsFloat : 0.3f).Set("aspect", aspect));
        else Send(Msg.Make(Msg.SetCamera).Set("preset", c.Has("preset") ? c["preset"].AsString : "full").Set("aspect", aspect));
        var state = Wait(Msg.CameraChanged, 10000)["state"];
        var final = SceneSpec.CameraState(state, c);
        Send(Msg.Make(Msg.SetCamera).Set("state", final));
        return final;
    }

    string? Photo(SceneShot s, string path)
    {
        Drain();
        Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", s.Width).Set("height", s.Height).Set("transparent", s.Transparent).Set("supersample", s.Supersample));
        var ans = Wait(Msg.CaptureDone, 300000);
        if (!ans["ok"].AsBool) return ans["error"].AsString;
        if (!Sticker.IsNeutral(s.Brightness, s.Contrast, s.Saturation))
        {
            var img = Sticker.DecodePng(File.ReadAllBytes(path));
            if (img == null) return "찍은 PNG를 보정하려고 읽지 못했어요";
            Sticker.Adjust(img, s.Brightness, s.Contrast, s.Saturation);
            File.WriteAllBytes(path, Sticker.EncodePng(img));
        }
        return null;
    }

    string? Video(SceneShot s, string outDir, JNode start, out string path)
    {
        var r = s.Record;
        bool mp4 = r["format"].AsString == "mp4";
        path = Path.Combine(outDir, s.Name + (mp4 ? ".mp4" : ""));
        int fps = r["fps"].AsInt;
        var msg = Msg.Make(Msg.Record).Set("format", mp4 ? "pipe" : "png").Set("width", s.Width & ~1).Set("height", s.Height & ~1)
            .Set("fps", fps).Set("seconds", r["seconds"].AsFloat).Set("mode", r["mode"].AsString).Set("turns", r["turns"].AsFloat).Set("transparent", s.Transparent);
        // 달리: 지금 자리 → 끝 자리 (에디터와 같은 배율, 또는 record.to). 끝 자리를 잰 뒤 시작 자리로 돌아온다
        var motion = r["motion"].AsString;
        if (motion == "in" || motion == "out") msg.Set("to", start.Clone().Set("distance", start["distance"].AsDouble * (motion == "in" ? 0.6 : 1.6)));
        else if (r.Has("to"))
        {
            msg.Set("to", Frame(r["to"], (float)s.Width / s.Height));
            Send(Msg.Make(Msg.SetCamera).Set("state", start));
        }
        using var done = new ManualResetEventSlim();
        string? jobError = null;
        RecordJob job;
        try { job = mp4 ? RecordJob.Pipe(path) : new RecordJob(path, false, s.Width, s.Height, fps); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return "녹화를 시작하지 못했어요: " + e.Message; }
        job.Finished = e => { jobError = e; done.Set(); };
        if (mp4) msg.Set("pipe", job.PipeName); else { Directory.CreateDirectory(path); msg.Set("folder", job.FrameFolder); }
        Drain();
        Send(Msg.Make(Msg.SetView).Set("pauseTime", false));   // 녹화는 시간이 흘러야 PhysBone·셰이더가 움직인다
        Send(msg);
        JNode ans;
        try { ans = Wait(Msg.RecordDone, Math.Max(120000, (int)(r["seconds"].AsDouble * fps * 3000))); }
        finally { Send(Msg.Make(Msg.SetView).Set("pauseTime", true)); }
        job.Complete(cancel: !ans["ok"].AsBool);
        if (!ans["ok"].AsBool) return "녹화하지 못했어요: " + ans["error"].AsString;
        done.Wait(TimeSpan.FromMinutes(10));
        return jobError;
    }

    /// <summary>
    /// 에디터에서 이어 고칠 수 있게 세션으로: 포즈·표정·조명·아바타 자리·카메라. 배경·소품·심도는 세션이 아니라 에디터 설정에 두므로 담기지 않는다.
    /// </summary>
    string SaveSession(SceneShot s, JNode camera, string outDir)
    {
        var path = Path.Combine(outDir, s.Name + Session.Extension);
        File.WriteAllText(path, Session.ToJson(SessionDoc(s), camera).Set("studio", StudioJson(s)).ToJson(indent: true));
        return path;
    }

    Document SessionDoc(SceneShot s)
    {
        var from = _doc!;
        var d = new Document(from.Original, from.IrPath);
        d.LoadChangeset(from.ChangesetToJson());
        d.Set(Addr.Environment, s.Environment);
        d.Set(Addr.Pose, s.Pose);
        d.Set(Addr.AvatarTransform, JNode.FloatArray(s.AvatarTrs));
        d.Set(Addr.Expression, ExpressionValue(s.Name, s.Expression));
        d.Set(Addr.ShapeKeys, s.ShapeKeyDefs);
        d.Set(Addr.FaceMap, s.FaceMap);
        foreach (var kv in s.ExpressionKeys) ToggleSet.SetExpressionKey(d, kv.Key, ExpressionValue(s.Name + " F" + (kv.Key + 1), kv.Value));
        foreach (var kv in s.Renderers) d.Set(Addr.RendererEnabled(kv.Key), kv.Value);
        return d;
    }

    /// <summary>세션의 studio: 에디터 스튜디오 페이지가 같은 상태로 열리게 (StudioPanel.ApplySessionStudio).</summary>
    static JNode StudioJson(SceneShot s) => JNode.Obj().Set("background", s.Background).Set("exposure", s.BgExposure).Set("rotation", s.BgRotation).Set("floor", s.Floor).Set("props", s.Props)
        .Set("dof", s.Dof).Set("quality", JNode.Obj().Set("studio", s.QualityStudio).Set("ao", s.Ao).Set("aa", s.CaptureAa)).Set("avatarOnly", s.AvatarOnly).Set("transparent", s.Transparent)
        .Set("adjust", JNode.Obj().Set("brightness", s.Brightness).Set("contrast", s.Contrast).Set("saturation", s.Saturation)).Set("shot", JNode.Obj().Set("width", s.Width).Set("height", s.Height)).Set("floorFit", s.FloorFit);

    /// <summary>장면의 한 장을 에디터에서 이어 고칠 세션으로 만든다. 여는 것은 부른 쪽(Shoot.Edit)이 한다.</summary>
    JNode EditCommand(JNode req)
    {
        var failed = Prepare(req, out var shots, out _, out var baseDir);
        if (failed != null) return failed;
        var shot = shots[0];
        var problems = new SceneProblems();
        foreach (var w in _prepareWarnings) problems.Warn(w);
        Apply(shot, problems, JNode.Obj());
        var camera = Frame(shot.Camera, (float)shot.Width / shot.Height);
        var outDir = req.Has("out") ? req["out"].AsString : Path.Combine(baseDir, "shots", Path.GetFileNameWithoutExtension(req["scene"].AsString));
        Directory.CreateDirectory(outDir);
        var warnings = JNode.Arr();
        foreach (var w in problems.Warnings) warnings.Add(w);
        if (shot.Tracking.Has("source") || !shot.Lip.IsNull) warnings.Add("트래킹·말하기는 켜진 상태로 넘어가지 않아요. 에디터에서 켜 주세요");
        return JNode.Obj().Set("ok", true).Set("name", shot.Name).Set("session", SaveSession(shot, camera, outDir)).Set("warnings", warnings);
    }

    static JNode ExpressionValue(string name, JNode shapes) =>
        shapes.Count == 0 ? JNode.Null : FaceExpression.Make(name, "anim", shapes.Items.Select(x => (x["renderer"].AsString, x["shape"].AsString, x["weight"].AsFloat)));

    string? SaveExpressionAnim(SceneShot s, string path, SceneProblems problems)
    {
        var tracks = s.Expression.Count == 0 ? null : AnimWriter.FromExpression(_doc!.Original, ExpressionValue(s.Name, s.Expression));
        if (tracks == null || tracks.Count == 0) { problems.Warn($"[{s.Name}] 저장할 표정이 없어요"); return null; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AnimWriter.Save(path, Path.GetFileNameWithoutExtension(path), tracks);
        return path;
    }
}

/// <summary>여러 장을 한 장에 — 시안을 한 번에 비교한다.</summary>
static class ContactSheet
{
    const int Cell = 480, Gap = 8;

    public static void Save(IReadOnlyList<string> files, string path)
    {
        var imgs = new List<Rgba>();
        foreach (var f in files)
        {
            var src = Sticker.DecodePng(File.ReadAllBytes(f)) ?? throw new InvalidDataException("PNG를 읽지 못했어요: " + f);
            double k = Math.Min(1, (double)Cell / Math.Max(src.W, src.H));
            int w = Math.Max(1, (int)(src.W * k)), h = Math.Max(1, (int)(src.H * k));
            imgs.Add(Sticker.Resample(src, 0, 0, src.W, src.H, w, h));
        }
        int cols = (int)Math.Ceiling(Math.Sqrt(imgs.Count)), rows = (int)Math.Ceiling((double)imgs.Count / cols);
        int cw = imgs.Max(i => i.W), ch = imgs.Max(i => i.H);
        var sheet = new Rgba(cols * cw + (cols + 1) * Gap, rows * ch + (rows + 1) * Gap);
        for (int i = 0; i < sheet.Px.Length; i += 4) { sheet.Px[i] = sheet.Px[i + 1] = sheet.Px[i + 2] = 40; sheet.Px[i + 3] = 255; }
        for (int n = 0; n < imgs.Count; n++)
        {
            var img = imgs[n];
            int ox = Gap + n % cols * (cw + Gap) + (cw - img.W) / 2, oy = Gap + n / cols * (ch + Gap) + (ch - img.H) / 2;
            for (int y = 0; y < img.H; y++)
                for (int x = 0; x < img.W; x++)
                {
                    int s = (y * img.W + x) * 4, t = ((oy + y) * sheet.W + ox + x) * 4, a = img.Px[s + 3];
                    for (int c = 0; c < 3; c++) sheet.Px[t + c] = (byte)((img.Px[s + c] * a + sheet.Px[t + c] * (255 - a)) / 255);
                }
        }
        File.WriteAllBytes(path, Sticker.EncodePng(sheet));
    }
}
