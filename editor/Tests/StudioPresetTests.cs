// 스튜디오 세팅 프리셋 파일 (Host/Core/StudioPreset.cs, 2026-11-01)
using System;
using System.IO;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class CoreTests
{
    static void StudioPresets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrcproto-preset-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            Check(StudioPreset.WithExtension(Path.Combine(dir, "a")).EndsWith("a.studio.json"), "extension added");
            Check(StudioPreset.WithExtension(Path.Combine(dir, "a.json")).EndsWith("a.studio.json"), "plain json fixed");
            Check(StudioPreset.WithExtension(Path.Combine(dir, "a.studio.json")).EndsWith("a.studio.json") && !StudioPreset.WithExtension(Path.Combine(dir, "a.studio.json")).Contains("studio.studio"), "extension kept");
            Check(StudioPreset.NameOf(@"C:\x\노을 빛.studio.json") == "노을 빛", "name from file");
            Check(StudioPreset.List(Path.Combine(dir, "없는 폴더")).Count == 0, "missing folder is empty");

            // 저장·읽기·덮어쓰기
            var path = Path.Combine(dir, "노을.studio.json");
            StudioPreset.Save(path, JNode.Obj().Set("exposure", 2));
            Check(StudioPreset.Load(path)["exposure"].AsInt == 2, "round trip");
            StudioPreset.Save(path, JNode.Obj().Set("exposure", 3));
            Check(StudioPreset.Load(path)["exposure"].AsInt == 3 && !File.Exists(path + ".tmp"), "overwrite leaves no temp file");
            StudioPreset.Save(Path.Combine(dir, "가을.studio.json"), JNode.Obj());
            File.WriteAllText(Path.Combine(dir, "다른.json"), "{}");
            var list = StudioPreset.List(dir);
            Check(list.Count == 2 && list[0].Name == "가을" && list[1].Name == "노을", "list only presets, sorted");

            // 잘못된 파일
            bool Bad(string text)
            {
                var f = Path.Combine(dir, "bad.studio.json");
                File.WriteAllText(f, text);
                try { StudioPreset.Load(f); return false; } catch (InvalidDataException) { return true; } finally { File.Delete(f); }
            }
            Check(Bad("{"), "broken json refused");
            Check(Bad(@"{ ""format"": ""vrcproto-session"", ""version"": 1, ""studio"": {} }"), "session file refused");
            Check(Bad(@"{ ""format"": ""vrcproto-studio-preset"", ""version"": 99, ""studio"": {} }"), "newer version refused");
            Check(Bad(@"{ ""format"": ""vrcproto-studio-preset"", ""version"": 1 }"), "no values refused");

            // 없는 파일 찾기
            var real = Path.Combine(dir, "bg.png");
            File.WriteAllText(real, "x");
            var st = JNode.Obj().Set("background", JNode.Obj().Set("mode", "image").Set("path", Path.Combine(dir, "없음.png")));
            st.Set("props", JNode.Arr().Add(JNode.Obj().Set("path", real)).Add(JNode.Obj().Set("path", Path.Combine(dir, "없는소품.ir.json"))));
            Check(StudioPreset.MissingFiles(st).Count == 2, "missing files listed");
            Check(StudioPreset.MissingFiles(JNode.Obj().Set("background", JNode.Obj().Set("mode", "gradient").Set("path", "없음.png"))).Count == 0, "gradient ignores stale path");

            // 장면에서 쓰기: 프리셋 값 위에 장면 값이 덮인다
            var ctx = SpecContext();
            ctx.BaseDir = dir;
            StudioPreset.Save(Path.Combine(dir, "장면용.studio.json"), JNode.Parse(@"{ ""background"": { ""mode"": ""gradient"", ""top"": [1, 0, 0], ""bottom"": [0, 0, 1] }, ""exposure"": 2,
                ""floor"": { ""enabled"": true, ""mode"": ""solid"", ""strength"": 0.3, ""color"": [0, 1, 0] }, ""dof"": { ""enabled"": true, ""strength"": 0.4 },
                ""quality"": { ""studio"": false, ""ao"": 0.1, ""aa"": 4 }, ""avatarOnly"": { ""on"": false }, ""transparent"": true,
                ""adjust"": { ""saturation"": 1.5 }, ""shot"": { ""width"": 640, ""height"": 480 } }"));
            var p = new SceneProblems();
            var sh = SpecShot(@"{ ""studioPreset"": ""장면용.studio.json"" }", ctx, p);
            Check(p.Errors.Count == 0, "preset in scene: " + string.Join("; ", p.Errors));
            Check(sh.Background["top"][0].AsInt == 1 && sh.BgExposure == 2 && sh.Floor["mode"].AsString == "solid" && sh.Floor["strength"].AsDouble == 0.3, "preset values");
            Check(sh.Dof["enabled"].AsBool && !sh.QualityStudio && sh.CaptureAa == 4 && sh.Transparent && sh.Saturation == 1.5 && sh.Width == 640, "preset values 2");
            sh = SpecShot(@"{ ""studioPreset"": ""장면용"", ""floor"": ""none"", ""transparent"": false, ""size"": ""square"" }", ctx, p);
            Check(!sh.Floor["enabled"].AsBool && !sh.Transparent && sh.Width == 1080, "scene values over preset");
            SpecShot(@"{ ""studioPreset"": ""없는 프리셋"" }", ctx, p);
            Check(p.Errors.Any(e => e.Contains("없는 프리셋")), "missing preset reported");
        }
        finally { Directory.Delete(dir, true); }
    }
}
