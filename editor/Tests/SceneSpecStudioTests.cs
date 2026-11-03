// 장면 파일: 스튜디오 탭의 나머지 (Tools/Shoot/SceneSpec.Studio.cs, 2026-11-01)
using System;
using System.IO;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static void SceneSpecStudioTabs()
    {
        var ctx = SpecContext();
        var p = new SceneProblems();

        // 스티커
        var sh = SpecShot(@"{ ""sticker"": ""kakao"" }", ctx, p);
        Check(sh.Sticker["spec"].AsString == "kakao" && sh.Sticker["outline"].AsInt == 6 && sh.Sticker["color"].AsString == "white", "sticker defaults");
        Check(SpecShot(@"{ ""sticker"": false }", ctx, p).Sticker.IsNull, "sticker off");
        Check(p.Errors.Count == 0, "sticker clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""sticker"": { ""spec"": ""kakaoo"", ""color"": ""red"" } }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("kakaoo") && e.Contains("kakao")) && p.Errors.Any(e => e.Contains("red")), "sticker checks");
        p = new SceneProblems();
        SpecShot(@"{ ""sticker"": ""line"", ""record"": { } }", ctx, p);
        Check(p.Errors.Count == 1, "sticker and record refused");

        // 조명 배치 · 방송 화질: 장면 값이 이기고 추가 조명은 채움광 뒤에 붙는다
        p = new SceneProblems();
        ctx.EnvSchema = JNode.Null;
        var env = SpecShot(@"{ ""lighting"": { ""layout"": ""butterfly"", ""intensity"": 2 }, ""lights"": [ { ""intensity"": 3 } ] }", ctx, p).Environment;
        Check(env[0]["params"]["intensity"].AsInt == 2 && env[0]["params"]["yaw"].AsInt == 30 && !env[0]["params"].Has("layout"), "layout under scene values");
        Check(env[1]["params"]["l1_on"].AsBool && env[1]["params"]["l2_on"].AsBool && !env[1]["params"]["l3_on"].AsBool && env[1]["params"]["l2_intensity"].AsInt == 3, "lights follow the fill light");
        env = SpecShot(@"{ ""lighting"": ""rim"" }", ctx, p).Environment;
        Check(env.Count == 1 && env[0]["params"]["yaw"].AsInt == -150, "layout name, no fill light");
        env = SpecShot(@"{ ""look"": ""broadcast"", ""post"": { ""bloom"": 0 } }", ctx, p).Environment;
        Check(env[0]["params"]["reflection"].AsDouble == 1.5 && env.Items.Last()["provider"].AsString == "PostProcess" && env.Items.Last()["params"]["bloom"].AsInt == 0 && env.Items.Last()["params"]["saturation"].AsDouble == 0.12, "broadcast look under post");
        Check(p.Errors.Count == 0, "lighting clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""lighting"": { ""layout"": ""moon"" }, ""look"": ""tv"" }", ctx, p);
        Check(p.Errors.Count == 2, "layout and look checks");

        // 트래킹·말하기
        p = new SceneProblems();
        Check(SpecShot(@"{ ""tracking"": ""osc"" }", ctx, p).Tracking["source"].AsString == "osc", "tracking text");
        var tr = SpecShot(@"{ ""tracking"": { ""source"": ""vmc"", ""port"": 1, ""recordAnim"": { ""path"": ""a.anim"", ""seconds"": 1 } } }", ctx, p).Tracking;
        Check(Path.IsPathRooted(tr["recordAnim"]["path"].AsString), "tracking anim path");
        Check(p.Errors.Count == 0, "tracking clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""tracking"": { ""source"": ""kinect"", ""mirorr"": true } }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("kinect")) && p.Errors.Any(e => e.Contains("mirorr") && e.Contains("mirror")), "tracking checks");
        p = new SceneProblems();
        SpecShot(@"{ ""tracking"": { ""freeze"": true } }", ctx, p);
        Check(p.Errors.Count == 1, "freeze needs a source");
        p = new SceneProblems();
        Check(SpecShot(@"{ }", ctx, p).Lip.IsNull, "lip off by default");
        Check(SpecShot(@"{ ""lip"": { ""mic"": ""x"", ""gain"": 2 } }", ctx, p).Lip["on"].AsBool, "lip on");
        SpecShot(@"{ ""lip"": ""없는 소리.wav"" }", ctx, p);
        Check(p.Errors.Count == 1, "lip file checked");

        // 트래킹 값 연결
        p = new SceneProblems();
        var map = SpecShot(@"{ ""faceMap"": { ""add"": [ { ""src"": ""jawOpen"", ""shape"": ""smile"", ""k"": 0.5 } ], ""off"": [ { ""src"": ""eyeBlink"", ""shape"": ""blink"" } ] } }", ctx, p).FaceMap;
        Check(map["add"][0]["r"].AsString == "r" && map["add"][0]["s"].AsString == "smile" && map["off"][0]["s"].AsString == "blink", "face map");
        SpecShot(@"{ ""faceMap"": { ""add"": [ { ""src"": ""jawOpen"", ""shape"": ""nope"" } ] } }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("nope")), "face map shape checked");

        // 블렌드쉐이프 단축키: 메뉴에서 · 직접 · 묶음은 하나만
        p = new SceneProblems();
        ctx.Scene.MenuShapes.Add(new IrMenuShape { Name = "홍조", Type = "toggle", Shapes = { new IrMenuShapeValue { Path = "Body", Shape = "smile", On = 70, Off = 0 } } });
        var keys = SpecShot(@"{ ""shapeKeys"": [ ""홍조"", { ""name"": ""눈A"", ""shapes"": { ""blink"": 40 }, ""group"": ""눈"", ""key"": ""Ctrl+1"" }, { ""name"": ""눈B"", ""shapes"": { ""blink"": { ""on"": 90, ""off"": 10 } }, ""group"": ""눈"" }, { ""name"": ""정의만"", ""shapes"": { ""smile"": 1 }, ""level"": 0 } ] }", ctx, p);
        Check(keys.ShapeKeyDefs.Count == 4 && keys.ShapeKeyDefs[1]["key"].AsString == "Ctrl+1", "shape key definitions");
        var layer = keys.ShapeLayer["shapes"];
        Check(keys.ShapeLayer["layer"].AsString == "keys" && layer.Count == 2, "shape key layer: " + layer.ToJson());
        Check(layer.Items.Any(x => x["shape"].AsString == "smile" && x["weight"].AsInt == 70) && layer.Items.Any(x => x["shape"].AsString == "blink" && x["weight"].AsInt == 90), "pressed values, one per group");
        Check(p.Errors.Count == 0, "shape keys clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""shapeKeys"": [ ""홍저"" ] }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("홍저") && e.Contains("홍조")), "unknown menu item suggests");

        // 표정 단축키
        p = new SceneProblems();
        Check(SpecShot(@"{ ""expressionKeys"": { ""F3"": ""happy"" } }", ctx, p).ExpressionKeys[2].Count == 1, "expression key");
        SpecShot(@"{ ""expressionKeys"": { ""F10"": ""happy"" } }", ctx, p);
        Check(p.Errors.Count == 1, "expression key slot checked");

        // 오버레이
        p = new SceneProblems();
        var ov = SpecShot(@"{ ""overlay"": { ""aspect"": ""16:9"", ""size"": 5 } }", ctx, p).Overlay;
        Check(Math.Abs(ov["aspect"].AsDouble - 16.0 / 9) < 1e-6 && ov["size"].AsInt == 1 && ov["top"].AsBool && ov["fps"].AsInt == 30 && ov["corner"].AsString == "br", "overlay values");
        Check(Math.Abs(SpecShot("{ }", ctx, p).Overlay["aspect"].AsDouble - 0.75) < 1e-6, "overlay defaults");
        SpecShot(@"{ ""overlay"": { ""corner"": ""middle"", ""aspect"": ""wide"" } }", ctx, p);
        Check(p.Errors.Count == 2, "overlay checks");

        // 녹화: 달리
        p = new SceneProblems();
        var rec = SpecShot(@"{ ""record"": { ""motion"": ""in"" } }", ctx, p).Record;
        Check(rec["mode"].AsString == "fixed" && rec["motion"].AsString == "in", "dolly in");
        rec = SpecShot(@"{ ""camera"": { ""preset"": ""upper"", ""pitch"": 5 }, ""record"": { ""to"": { ""preset"": ""face"" } } }", ctx, p).Record;
        Check(rec["mode"].AsString == "fixed" && rec["to"]["preset"].AsString == "face" && rec["to"]["pitch"].AsInt == 5, "dolly to merges the shot camera");
        Check(SpecShot(@"{ ""record"": { } }", ctx, p).Record["mode"].AsString == "turntable", "record default turntable");
        Check(SpecShot(@"{ ""record"": { ""mode"": ""fixed"" } }", ctx, p).Record["motion"].AsString == "fixed", "old mode key");
        Check(p.Errors.Count == 0, "record clean: " + string.Join("; ", p.Errors));

        // 저장한 카메라 위치: 방향까지 그대로, 고칠 값만 고친다
        p = new SceneProblems();
        ctx.SavedCameras = JNode.Parse(@"[ { ""name"": ""위치 1"", ""state"": { ""pivot"": [0, 1, 0], ""yaw"": 77, ""pitch"": 3, ""distance"": 4, ""fov"": 40, ""ortho"": true } } ]");
        var cam = SpecShot(@"{ ""camera"": { ""saved"": ""위치 1"", ""zoom"": 2 } }", ctx, p).Camera;
        var st = SceneSpec.CameraState(cam["state"], cam, keepYaw: true);
        Check(st["yaw"].AsInt == 77 && st["distance"].AsInt == 2 && st["fov"].AsInt == 40 && st["ortho"].AsBool, "saved camera");
        Check(SpecShot(@"{ ""camera"": { ""saved"": ""1"" } }", ctx, p).Camera.Has("state"), "saved camera by number");
        Check(p.Errors.Count == 0, "camera clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""camera"": { ""saved"": ""9"" } }", ctx, p);
        Check(p.Errors.Count == 1, "saved camera checked");

        // 세션의 studio는 읽히고, 없으면 Null (예전 세션·에디터가 저장한 세션)
        var sess = Session.FromJson(JNode.Parse(@"{ ""format"": ""vrcproto-session"", ""version"": 1, ""ir"": ""x.ir.json"", ""studio"": { ""exposure"": 2 } }"), ".");
        Check(sess.Studio["exposure"].AsInt == 2, "session studio read");
        Check(Session.FromJson(JNode.Parse(@"{ ""format"": ""vrcproto-session"", ""version"": 1, ""ir"": ""x.ir.json"" }"), ".").Studio.IsNull, "old session has no studio");

        // 기타
        p = new SceneProblems();
        var q = SpecShot(@"{ ""quality"": { ""studio"": false }, ""embedSession"": true, ""saveExpression"": ""x.anim"", ""dof"": { ""show"": true } }", ctx, p);
        Check(!q.QualityStudio && q.EmbedSession && q.SaveExpression != null && Path.IsPathRooted(q.SaveExpression) && q.Dof["show"].AsBool, "quality, embed, save expression, focus view");
    }
}
