// 창 없는 촬영 도구 (2026-11-01): Claude 같은 자동화가 포즈·표정·조명·환경·카메라·촬영을 장면 JSON 하나로 한다. 형식은 .claude/skills/avatar-shoot/SHOOT.md.
//   dotnet bin/Shoot.dll run <장면.json> [--draft] [--only a,b] [--out 폴더] [--session]
//   dotnet bin/Shoot.dll inspect <아바타> [--shapes [찾을 글]] · avatars · schema · status · stop
// 창 없는 편집 (2026-11-02): look <아바타|세션> (지금 값 보기, 렌더러 없이) · apply <편집.json> (에디터 페이지의 값 바꾸기·아이템 붙이기 → 세션). 형식은 .claude/skills/avatar-edit/EDIT.md.
// 아바타를 렌더러에 올리는 데 시간이 걸리므로 렌더러를 든 서버(serve)를 알아서 띄워 두고, 다음 run은 바뀐 값만 보낸다. 20분 쉬면 스스로 끝난다.
// 에디터와는 별개의 렌더러(창 없음)라 켜 둔 에디터 화면에는 영향이 없다.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class Shoot
{
    const string PipeName = "vrcproto-shoot";
    const string AliveName = @"Local\VrcProtoShootServer";
    static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(20);

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var cmd = args.Length > 0 ? args[0] : "";
        try
        {
            switch (cmd)
            {
                case "serve": return Serve();
                case "run": return RunCommand(args);
                case "import": return Import(args);
                case "devices": return Print(Ask(JNode.Obj().Set("cmd", "devices"), true));
                case "facelinks":
                    if (args.Length < 2) throw new InvalidDataException("아바타 파일을 적어 주세요: facelinks <아바타>");
                    return Print(Ask(JNode.Obj().Set("cmd", "facelinks").Set("avatar", Path.GetFullPath(args[1])), true));
                case "overlay": return OverlayCommand(args);
                case "cameras": return Cameras();
                case "edit": return Edit(args);
                case "inspect": return Inspect(args);
                case "look": return Look(args);
                case "apply": return ApplyEdits(args);
                case "avatars": return Avatars();
                case "schema": return Print(Ask(JNode.Obj().Set("cmd", "schema"), true));
                case "status":
                    if (!ServerAlive()) { Console.WriteLine(JNode.Obj().Set("ok", true).Set("running", false).ToJson(true)); return 0; }
                    return Print(Ask(JNode.Obj().Set("cmd", "status"), false));
                case "stop":
                    if (!ServerAlive()) { Console.WriteLine(JNode.Obj().Set("ok", true).Set("running", false).ToJson(true)); return 0; }
                    return Print(Ask(JNode.Obj().Set("cmd", "stop"), false));
                default:
                    Console.WriteLine("사용: Shoot run <장면.json> [--draft] [--only a,b] [--out 폴더] [--session]\n      inspect <아바타> [--shapes [찾을 글]] [--muscles] | avatars | import <파일> [--list] [--root 이름] | schema | devices | facelinks <아바타> | cameras | overlay on <장면> | overlay off | overlay status | edit <장면> [--only 장] [--test] [--no-open] | look <아바타|세션> [--material 이름] [--shapes [메시]] [--find 글] [--all] [--changes] | apply <편집.json> [--avatar 파일] [--out 세션] | status | stop\n자세한 설명: .claude/skills/avatar-shoot/SHOOT.md · 편집: .claude/skills/avatar-edit/EDIT.md");
                    return cmd.Length == 0 ? 0 : 2;
            }
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is TimeoutException || e is UnauthorizedAccessException)
        {
            Console.WriteLine(JNode.Obj().Set("ok", false).Set("errors", JNode.Arr().Add(e.Message)).ToJson(true));
            return 1;
        }
    }

    static int Print(JNode r) { Console.WriteLine(r.ToJson(true)); return r["ok"].AsBool ? 0 : 1; }

    static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : null; }

    /// <summary>overlay on <장면> [--only 장] · overlay off · overlay status</summary>
    static int OverlayCommand(string[] a)
    {
        var action = a.Length > 1 ? a[1] : "status";
        var req = JNode.Obj().Set("cmd", "overlay").Set("action", action);
        if (action == "on")
        {
            if (a.Length < 3) throw new InvalidDataException("장면 파일을 적어 주세요: overlay on <장면.json> [--only 장]");
            req.Set("scene", Path.GetFullPath(a[2]));
            if (Opt(a, "--only") is string only) req.Set("only", only);
        }
        else if (action != "off" && action != "status") throw new InvalidDataException("overlay는 on·off·status예요");
        else if (!ServerAlive()) { Console.WriteLine(JNode.Obj().Set("ok", true).Set("on", false).ToJson(true)); return 0; }
        return Print(Ask(req, action == "on"));
    }

    /// <summary>
    /// edit <장면> [--only 장] [--out 폴더] [--test] [--no-open]: 그 장의 상태를 세션으로 만들어 에디터로 연다 (사람이 이어 고치기).
    /// --test = 설정을 따로 둔 에디터 시험 모드. 평소 모드는 배경 등 스튜디오 값이 그 PC의 에디터 설정으로 남는다.
    /// </summary>
    static int Edit(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("장면 파일을 적어 주세요: edit <장면.json> [--only 장] [--test] [--no-open]");
        var req = JNode.Obj().Set("cmd", "edit").Set("scene", Path.GetFullPath(a[1]));
        if (Opt(a, "--only") is string only) req.Set("only", only);
        if (Opt(a, "--out") is string dir) req.Set("out", Path.GetFullPath(dir));
        var r = Ask(req, true);
        if (r["ok"].AsBool && !a.Contains("--no-open"))
        {
            var test = a.Contains("--test") ? "--test " : "";
            var exe = Path.Combine(AppContext.BaseDirectory, "ALEdit.exe");   // 배포본
            var dll = Path.Combine(AppContext.BaseDirectory, "av", "VrcProtoAv.dll");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = $"{test}\"{r["session"].AsString}\"" });
            else if (File.Exists(dll)) Process.Start(new ProcessStartInfo("dotnet") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = $"\"{dll}\" {test}\"{r["session"].AsString}\"" });
            else throw new IOException("에디터를 찾지 못했어요: " + exe);
            r.Set("opened", true);
        }
        return Print(r);
    }

    /// <summary>apply <편집.json> [--avatar 파일] [--out 세션]: 편집 목록을 적용해 세션으로 (렌더러가 셰이더 스키마를 알려 줘야 해서 서버에서).</summary>
    static int ApplyEdits(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("편집 파일을 적어 주세요: apply <편집.json> [--avatar 파일] [--out 세션]");
        var req = JNode.Obj().Set("cmd", "apply").Set("edits", Path.GetFullPath(a[1]));
        if (Opt(a, "--avatar") is string av) req.Set("avatar", Path.GetFullPath(av));
        if (Opt(a, "--out") is string o) req.Set("out", Path.GetFullPath(o));
        return Print(Ask(req, true));
    }

    /// <summary>look <아바타|세션> [--material 이름] [--shapes [메시]] [--find 글] [--all] [--changes]: 지금 값 (렌더러 없이).</summary>
    static int Look(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("아바타 파일을 적어 주세요: look <x.ir.json | 세션.alesav>");
        var (scene, session) = LoadAvatar(a[1]);
        var doc = new Document(scene, session?.IrPath ?? Path.GetFullPath(a[1]));
        if (session != null && session.Changeset.Kind == JKind.Array) doc.LoadChangeset(session.Changeset);
        if (session != null && session.Variants.Kind == JKind.Array) doc.LoadVariants(session.Variants);
        JNode r;
        if (Opt(a, "--material") is string mat) r = DocLook.Material(doc, mat);
        else if (a.Contains("--shapes")) r = DocLook.Shapes(doc, Opt(a, "--shapes"), Opt(a, "--find"), a.Contains("--all"));
        else if (a.Contains("--changes")) r = DocLook.Changes(doc);
        else r = DocLook.Summary(doc);
        return Print(r);
    }

    static int Cameras()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", "avhost.json");
        var cams = File.Exists(path) ? JNode.Parse(File.ReadAllText(path))["studio"]["cameras"] : JNode.Arr();
        Console.WriteLine(JNode.Obj().Set("ok", true).Set("cameras", cams).ToJson(true));
        return 0;
    }

    static int RunCommand(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("장면 파일을 적어 주세요: run <장면.json>");
        var req = JNode.Obj().Set("cmd", "run").Set("scene", Path.GetFullPath(a[1])).Set("draft", a.Contains("--draft")).Set("session", a.Contains("--session"));
        if (Opt(a, "--only") is string only) req.Set("only", only);
        if (Opt(a, "--out") is string dir) req.Set("out", Path.GetFullPath(dir));
        return Print(Ask(req, true));
    }

    // ---------------- 서버에 묻기 ----------------
    static bool ServerAlive() { if (Mutex.TryOpenExisting(AliveName, out var m)) { m.Dispose(); return true; } return false; }

    /// <summary>요청 한 줄을 보내고 답 한 줄을 받는다. 서버가 없으면 띄운다 (start). 다른 요청을 처리 중이면 끝날 때까지 기다린다.</summary>
    static JNode Ask(JNode req, bool start)
    {
        if (!ServerAlive())
        {
            if (!start) throw new IOException("촬영 서버가 떠 있지 않아요");
            StartServer();
        }
        var until = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            try { pipe.Connect(2000); }
            catch (TimeoutException)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("촬영 서버에 연결하지 못했어요");
                continue;
            }
            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(pipe, new UTF8Encoding(false));
            w.WriteLine(req.ToJson());
            var line = r.ReadLine();
            if (line == null) throw new IOException($"촬영 서버가 답 없이 끝났어요. 기록: {LogPath}");
            return JNode.Parse(line);
        }
    }

    /// <summary>
    /// UseShellExecute로 띄운다: 그래야 부른 쪽의 표준 출력 파이프를 물려받지 않아, 서버가 도는 동안 부른 프로그램의 출력 읽기가 끝난다.
    /// </summary>
    static void StartServer()
    {
        // 배포본은 Shoot.exe(함께 넣은 .NET), 개발 중에는 dotnet bin/Shoot.dll
        var self = Environment.ProcessPath ?? "dotnet";
        bool host = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        Process.Start(new ProcessStartInfo(self) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = host ? $"\"{typeof(Shoot).Assembly.Location}\" serve" : "serve" });
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!ServerAlive())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"촬영 서버가 뜨지 않았어요. 기록: {LogPath}");
            Thread.Sleep(100);
        }
    }

    // ---------------- 서버 ----------------
    internal static string TempDir { get { var d = Path.Combine(Path.GetTempPath(), "VrcProtoShoot"); Directory.CreateDirectory(d); return d; } }
    static string LogPath => Path.Combine(TempDir, "server.log");
    internal static void Log(string s) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {s}\n"); } catch (IOException) { } }

    static int Serve()
    {
        using var alive = new Mutex(true, AliveName, out bool created);
        if (!created) return 0;   // 이미 떠 있다
        try { File.WriteAllText(LogPath, ""); } catch (IOException) { }
        ShootServer.DpiAware();
        var studio = new ShootServer();
        try
        {
            while (true)
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { pipe.WaitForConnectionAsync().Wait(IdleLimit); } catch (AggregateException) { continue; }
                if (!pipe.IsConnected)
                {
                    if (studio.OverlayOn) continue;   // 오버레이는 끌 때까지 화면에 남긴다
                    Log("오래 쉬어서 끝냄");
                    return 0;
                }
                var r = new StreamReader(pipe, new UTF8Encoding(false));
                var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var line = r.ReadLine();
                if (line == null) continue;
                JNode reply;
                bool stop = false;
                try
                {
                    var req = JNode.Parse(line);
                    stop = req["cmd"].AsString == "stop";
                    if (stop) studio.Discard();   // 렌더러까지 끝낸 뒤 답한다 — 바로 이어지는 빌드가 exe·dll 잠금에 걸리지 않게
                    reply = stop ? JNode.Obj().Set("ok", true).Set("stopped", true) : studio.Handle(req);
                }
                catch (Exception e)
                {
                    Log(e.ToString());
                    studio.Discard();   // 렌더러 상태를 믿을 수 없다 — 다음 요청에 새로 띄운다
                    reply = JNode.Obj().Set("ok", false).Set("errors", JNode.Arr().Add(e.Message));
                }
                try { w.WriteLine(reply.ToJson()); pipe.WaitForPipeDrain(); } catch (IOException) { }
                if (stop) return 0;
            }
        }
        finally { studio.Dispose(); }
    }

    // ---------------- 아바타 보기 (렌더러 없이) ----------------
    static (IrScene Scene, Session.Loaded? Session) LoadAvatar(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new IOException("아바타 파일이 없어요: " + path);
        if (Session.IsSessionFile(path))
        {
            var s = Session.Read(path);
            return (IrScene.Load(s.IrPath), s);
        }
        return (IrScene.Load(path), null);
    }

    static int Inspect(string[] a)
    {
        if (a.Length < 2) throw new InvalidDataException("아바타 파일을 적어 주세요: inspect <x.ir.json | 세션.alesav>");
        var (s, _) = LoadAvatar(a[1]);
        bool shapes = a.Contains("--shapes");
        var filter = Opt(a, "--shapes");
        if (a.Contains("--muscles")) { Console.WriteLine(JNode.Obj().Set("ok", true).Set("muscles", Names(Muscles.Names)).ToJson()); return 0; }
        var o = JNode.Obj().Set("ok", true).Set("name", s.Name).Set("humanoidBones", s.Humanoid.Count);
        var bones = JNode.Arr(); foreach (var b in s.Humanoid.Keys.OrderBy(x => x)) bones.Add(b);
        o.Set("bones", bones);
        var rens = JNode.Arr();
        foreach (var r in s.Renderers)
        {
            var m = s.FindMesh(r.Mesh);
            var it = JNode.Obj().Set("name", SceneSpec.RendererName(s, r)).Set("id", r.Id).Set("enabled", r.Enabled).Set("vertices", m?.VertexCount ?? 0).Set("shapeCount", m?.BlendShapes.Count ?? 0);
            if (shapes && m != null)
            {
                var names = JNode.Arr();
                foreach (var b in m.BlendShapes)
                    if (filter == null || b.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(b.Name);
                if (names.Count > 0 || filter == null) it.Set("shapes", names);
            }
            rens.Add(it);
        }
        o.Set("renderers", rens);
        var exprs = JNode.Arr();
        foreach (var e in s.Expressions) exprs.Add(JNode.Obj().Set("name", e.Name).Set("group", FaceExpression.GroupLabel(e.Layer)).Set("shapes", e.Shapes.Count));
        o.Set("expressions", exprs);
        o.Set("posePresets", Names(PosePresets.All.Select(p => p.Key)));
        o.Set("bundledPoses", Names(SceneSpec.BundledPoses(new SceneContext { PoseDir = PoseDir })));
        o.Set("savedPoses", Names(PoseLibrary.List().Select(p => p.Name)));
        o.Set("handShapes", Names(HandPose.Shapes.Select(h => h.Key)));
        o.Set("stickerSpecs", Names(Sticker.Specs.Select(x => x.Key)));
        o.Set("lightingLayouts", Names(LightingLayouts.Keys));
        var menu = JNode.Arr();
        foreach (var m in s.MenuShapes) menu.Add(JNode.Obj().Set("name", m.Name).Set("type", m.Type).Set("shapes", m.Shapes.Count));
        o.Set("menuShapes", menu);
        Console.WriteLine(o.ToJson(true));
        return 0;
    }

    internal static string PoseDir => Path.Combine(AppContext.BaseDirectory, "shoot-poses");

    static JNode Names(System.Collections.Generic.IEnumerable<string> xs) { var a = JNode.Arr(); foreach (var x in xs) a.Add(x); return a; }

    /// <summary>가져온 아바타 목록: 폴더마다 가장 최근 IR (옷을 입혀 다시 만들면 이름이 아바타 A+해시로 늘어난다).</summary>
    static int Avatars()
    {
        var list = JNode.Arr();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports");
        if (Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var ir = Directory.GetFiles(d, "*.ir.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (ir != null) list.Add(JNode.Obj().Set("name", Path.GetFileName(d)).Set("path", ir).Set("time", File.GetLastWriteTime(ir).ToString("yyyy-MM-dd HH:mm")));
            }
        Console.WriteLine(JNode.Obj().Set("ok", true).Set("avatars", list).ToJson(true));
        return 0;
    }
}
