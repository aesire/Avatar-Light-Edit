// 장면 파일 해석 (Tools/Shoot/SceneSpec.cs, 2026-11-01)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static SceneContext SpecContext()
    {
        var s = new IrScene { Name = "t" };
        s.Nodes.Add(new IrNode { Id = 0, Name = "t" });
        s.Nodes.Add(new IrNode { Id = 1, Name = "Body", Parent = 0 });
        s.Meshes.Add(new IrMesh { Id = "m", Name = "m", BlendShapes = { new IrBlendShape { Name = "smile" }, new IrBlendShape { Name = "blink" } } });
        s.Renderers.Add(new IrRenderer { Id = "r", Node = 1, Mesh = "m" });
        s.Expressions.Add(new IrExpression { Name = "happy", Shapes = { new IrExpressionShape { Path = "Body", Shape = "smile", Value = 80 } } });
        s.Humanoid["Head"] = 1;
        return new SceneContext { Scene = s, Rest = new Dictionary<string, float> { ["Head Nod Down-Up"] = 0.1f }, BaseDir = Path.GetTempPath() };
    }

    static SceneShot SpecShot(string json, SceneContext ctx, SceneProblems p)
    {
        var o = JNode.Parse(json);
        if (!o.Has("name")) o.Set("name", "a");
        return SceneSpec.Compile(o, ctx, p);
    }

    static void SceneSpecBasics()
    {
        // 펼치기: 객체는 합치고 배열·값은 장이 이긴다, 이름은 겹치지 않게
        var list = SceneSpec.Expand(JNode.Parse(@"{ ""lighting"": { ""preset"": ""night"", ""yaw"": 10 }, ""lights"": [ {}, {} ], ""shots"": [
            { ""name"": ""x"", ""lighting"": { ""yaw"": 20 }, ""lights"": [] }, { ""name"": ""x"" }, { } ] }"));
        Check(list.Count == 3 && list[0]["lighting"]["preset"].AsString == "night" && list[0]["lighting"]["yaw"].AsInt == 20, "shots merge objects");
        Check(list[0]["lights"].Count == 0 && list[1]["lights"].Count == 2, "shots replace arrays");
        Check(list[0]["name"].AsString == "x" && list[1]["name"].AsString == "x_2" && list[2]["name"].AsString == "shot3", "shot names are unique");
        Check(!list[0].Has("shots"), "shots key removed");
        Check(SceneSpec.Expand(JNode.Parse("{}"))[0]["name"].AsString == "shot", "single shot name");

        // 크기
        var p = new SceneProblems();
        var ctx = SpecContext();
        var sh = SpecShot(@"{ ""size"": ""portrait"" }", ctx, p);
        Check(sh.Width == 1080 && sh.Height == 1920 && sh.Supersample == 2, "size name");
        Check(SpecShot(@"{ ""size"": ""4k"" }", ctx, p).Supersample == 1, "large shots skip supersampling");
        Check(SpecShot(@"{ ""size"": ""640x480"", ""width"": 800 }", ctx, p).Width == 800, "size text + width");
        Check(p.Errors.Count == 0, "no errors: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""size"": ""big"" }", ctx, p);
        Check(p.Errors.Count == 1, "bad size");

        // 틀린 이름은 비슷한 이름과 함께 오류로
        p = new SceneProblems();
        SpecShot(@"{ ""lightning"": ""x"", ""camera"": { ""presett"": ""full"", ""bone"": ""Haed"" }, ""pose"": { ""muscles"": { ""Head Turn"": 1 } }, ""expression"": { ""name"": ""hapy"", ""shapes"": { ""smil"": 50 } }, ""hide"": [""Bodi""] }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("lightning") && e.Contains("lighting")), "unknown key suggests");
        Check(p.Errors.Any(e => e.Contains("presett") && e.Contains("preset")), "unknown camera key suggests");
        Check(p.Errors.Any(e => e.Contains("Haed") && e.Contains("Head")), "unknown bone suggests");
        Check(p.Errors.Any(e => e.Contains("Head Turn Left-Right")), "unknown muscle suggests");
        Check(p.Errors.Any(e => e.Contains("hapy") && e.Contains("happy")), "unknown expression suggests");
        Check(p.Errors.Any(e => e.Contains("smil") && e.Contains("smile")), "unknown shape suggests");
        Check(p.Errors.Any(e => e.Contains("Bodi") && e.Contains("Body")), "unknown renderer suggests");
        p = new SceneProblems();
        SpecShot(@"{ ""_memo"": ""자유 메모"", ""brief"": ""기획"" }", ctx, p);
        Check(p.Errors.Count == 0, "private keys and brief are allowed");

        // 자세: 프리셋·쉬는 자세 바탕·손·거울
        p = new SceneProblems();
        Check(SpecShot(@"{ ""pose"": ""rest"" }", ctx, p).Pose.IsNull, "rest = null");
        Check(SpecShot(@"{ }", ctx, p).Pose.IsNull, "no pose = base (null)");
        var pose = SpecShot(@"{ ""pose"": { ""muscles"": { ""Head Turn Left-Right"": 0.5 } } }", ctx, p).Pose;
        Check(Math.Abs(pose["muscles"]["Head Nod Down-Up"].AsFloat - 0.1f) < 1e-4 && Math.Abs(pose["muscles"]["Head Turn Left-Right"].AsFloat - 0.5f) < 1e-4, "muscles build on rest");
        pose = SpecShot(@"{ ""pose"": { ""base"": ""tpose"", ""add"": { ""Left Arm Down-Up"": 0.1 } } }", ctx, p).Pose;
        Check(Math.Abs(pose["muscles"]["Left Arm Down-Up"].AsFloat - 0.5f) < 1e-3, "add is relative to base");
        pose = SpecShot(@"{ ""pose"": { ""base"": ""tpose"", ""hands"": { ""right"": ""fist"", ""left"": 0 }, ""muscles"": { ""Left Arm Down-Up"": -0.5 }, ""mirror"": ""leftToRight"" } }", ctx, p).Pose;
        Check(Math.Abs(pose["muscles"]["Right Arm Down-Up"].AsFloat + 0.5f) < 1e-3, "mirror copies");
        pose = SpecShot(@"{ ""pose"": { ""base"": ""tpose"", ""bodyRotation"": [0, 90, 0] } }", ctx, p).Pose;
        Check(Math.Abs(pose["bodyRotation"][1].AsFloat - 0.7071f) < 1e-3 && Math.Abs(pose["bodyRotation"][3].AsFloat - 0.7071f) < 1e-3, "bodyRotation euler");
        Check(p.Errors.Count == 0, "pose errors: " + string.Join("; ", p.Errors));

        // 함께 넣은 자세 파일: 조립식은 다른 자세처럼 만든다
        var dir = Path.Combine(Path.GetTempPath(), "vrcproto-spec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "lean.pose.json"), @"{ ""pose"": { ""base"": ""tpose"", ""muscles"": { ""Spine Front-Back"": 0.3 } } }");
            var c2 = SpecContext(); c2.PoseDir = dir;
            pose = SpecShot(@"{ ""pose"": { ""base"": ""lean"", ""muscles"": { ""Neck Nod Down-Up"": 0.2 } } }", c2, p).Pose;
            Check(Math.Abs(pose["muscles"]["Spine Front-Back"].AsFloat - 0.3f) < 1e-3 && Math.Abs(pose["muscles"]["Left Arm Down-Up"].AsFloat - 0.4f) < 1e-3 && Math.Abs(pose["muscles"]["Neck Nod Down-Up"].AsFloat - 0.2f) < 1e-3, "pose file as base");
        }
        finally { Directory.Delete(dir, true); }

        // 표정: 기본 표정 + 이름으로 고친 값, 같은 값은 덮어쓴다
        p = new SceneProblems();
        var ex = SpecShot(@"{ ""expression"": { ""name"": ""happy"", ""shapes"": { ""blink"": 100 } } }", ctx, p).Expression;
        Check(ex.Count == 2 && ex.Items.Any(x => x["shape"].AsString == "smile" && x["weight"].AsInt == 80), "expression base");
        ex = SpecShot(@"{ ""expression"": { ""name"": ""happy"", ""shapes"": { ""smile"": 10 } } }", ctx, p).Expression;
        Check(ex.Count == 1 && ex[0]["weight"].AsInt == 10 && ex[0]["renderer"].AsString == "r", "expression shape overrides");
        Check(SpecShot(@"{ ""expression"": ""none"" }", ctx, p).Expression.Count == 0, "expression none");

        // 조명: 색은 #RRGGBB, 슬롯은 나머지를 끈다, 스키마로 이름 검사
        p = new SceneProblems();
        ctx.EnvSchema = JNode.Parse(@"[ { ""id"": ""UnityLighting"", ""params"": [ { ""name"": ""preset"", ""type"": ""enum"", ""options"": [""bright""] }, { ""name"": ""intensity"", ""type"": ""float"", ""min"": 0, ""max"": 3 } ] } ]");
        var env = SpecShot(@"{ ""lighting"": { ""intensity"": 2 }, ""lights"": [ { ""color"": ""#FF0000"", ""intensity"": 2 } ], ""post"": { ""bloom"": 0 } }", ctx, p).Environment;
        Check(env.Count == 3 && env[0]["params"]["preset"].AsString == "bright", "lighting default preset");
        Check(env[1]["params"]["l1_on"].AsBool && !env[1]["params"]["l2_on"].AsBool && env[1]["params"]["l1_color"][0].AsFloat == 1 && env[1]["params"]["l1_color"][1].AsFloat == 0, "lights slots");
        Check(env[2]["provider"].AsString == "PostProcess", "post provider");
        Check(p.Errors.Count == 0 && p.Warnings.Count == 0, "env clean: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""lighting"": { ""preset"": ""moon"", ""intensty"": 1, ""intensity"": 9 } }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("moon")) && p.Errors.Any(e => e.Contains("intensty")) && p.Warnings.Any(w => w.Contains("intensity")), "env schema checks");
        ctx.BaseEnvironment = JNode.Arr().Add(JNode.Obj().Set("provider", "Saved"));
        Check(SpecShot("{ }", ctx, p).Environment[0]["provider"].AsString == "Saved", "no lighting keeps session environment");

        // 배경·바닥·심도
        p = new SceneProblems();
        var st = SpecShot(@"{ ""background"": ""#102030"", ""floor"": ""shadow"", ""dof"": true }", ctx, p);
        Check(st.Background["mode"].AsString == "gradient" && st.Background["top"].Count == 3, "solid background");
        Check(st.Floor["enabled"].AsBool && st.Floor["mode"].AsString == "shadow", "floor text");
        Check(st.Dof["enabled"].AsBool && st.Dof["auto"].AsBool, "dof true");
        st = SpecShot(@"{ ""dof"": { ""distance"": 2 } }", ctx, p);
        Check(!st.Dof["auto"].AsBool && st.Dof["distance"].AsInt == 2, "dof distance is manual");
        Check(!SpecShot("{ }", ctx, p).Floor["enabled"].AsBool && SpecShot("{ }", ctx, p).Background["mode"].AsString == "none", "defaults off");
        Check(p.Errors.Count == 0, "studio errors: " + string.Join("; ", p.Errors));
        SpecShot(@"{ ""background"": ""없는 그림.png"", ""props"": [ { ""path"": ""x.fbx"" } ] }", ctx, p);
        Check(p.Errors.Any(e => e.Contains("없는 그림")) && p.Errors.Any(e => e.Contains(".ir.json")), "missing files");

        // 카메라: 0 = 정면, 각도·줌·옮기기
        var preset = JNode.Parse(@"{ ""pivot"": [0, 1, 0], ""yaw"": 77, ""pitch"": 3, ""distance"": 4, ""fov"": 30 }");
        var cam = SceneSpec.CameraState(preset, JNode.Obj());
        Check(cam["yaw"].AsInt == 180 && cam["pitch"].AsInt == 3 && cam["distance"].AsInt == 4 && cam["fov"].AsInt == 30, "camera front");
        cam = SceneSpec.CameraState(preset, JNode.Parse(@"{ ""angle"": 30, ""pitch"": -10, ""zoom"": 2 }"));
        Check(cam["yaw"].AsInt == 210 && cam["pitch"].AsInt == -10 && cam["distance"].AsInt == 2, "camera angle zoom");
        cam = SceneSpec.CameraState(preset, JNode.Parse(@"{ ""up"": 0.5, ""right"": 1 }"));
        // 정면에서 보면 화면 오른쪽 = 아바타 왼쪽 = −X
        Check(Math.Abs(cam["pivot"][1].AsFloat - 1.5f) < 1e-4 && Math.Abs(cam["pivot"][0].AsFloat + 1) < 1e-4, "camera offset");

        // 녹화
        p = new SceneProblems();
        var rec = SpecShot(@"{ ""record"": { ""seconds"": 2 } }", ctx, p).Record;
        Check(rec["mode"].AsString == "turntable" && rec["format"].AsString == "mp4" && rec["fps"].AsInt == 30, "record defaults");
        SpecShot(@"{ ""transparent"": true, ""record"": { } }", ctx, p);
        Check(p.Errors.Count == 1, "transparent mp4 refused");
    }
}
