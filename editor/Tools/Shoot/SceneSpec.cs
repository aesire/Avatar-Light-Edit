// 장면 파일 해석 (2026-11-01, Tools/Shoot): 포즈·표정·조명·환경·소품·카메라·촬영을 JSON 하나로 적으면 렌더러 메시지 값으로 바꾼다.
// 렌더러와 무관한 순수 계산이라 단위 테스트한다 (Tests/SceneSpecTests.cs). 형식 설명은 .claude/skills/avatar-shoot/SHOOT.md. 에디터에 들어가지 않게 Host/Core 밖에 둔다.
// 선언형: 적지 않은 항목은 기본값으로 돌아간다 — 띄워 둔 렌더러에 앞 장면이 남아도 결과가 같게.
// 틀린 이름은 렌더러에 보내기 전에 잡아 비슷한 이름과 함께 알린다 (렌더러는 모르는 값을 조용히 넘긴다).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    /// <summary>해석할 때 필요한 아바타 쪽 정보.</summary>
    public sealed class SceneContext
    {
        public IrScene Scene = new IrScene();
        public IReadOnlyDictionary<string, float> Rest = new Dictionary<string, float>();
        public string BaseDir = ".";
        /// <summary>렌더러 ready.environment. 없으면(Null) 환경 값 이름 검사를 건너뛴다.</summary>
        public JNode EnvSchema = JNode.Null;
        /// <summary>장면에 적지 않았을 때의 값 (세션의 값, 없으면 원본).</summary>
        public JNode BaseEnvironment = Document.DefaultEnvironment();
        public JNode BasePose = JNode.Null;
        public JNode BaseExpression = JNode.Arr();
        /// <summary>함께 넣어 둔 자세 파일 폴더 (Tools/Shoot/poses → bin/shoot-poses).</summary>
        public string? PoseDir;
        public JNode BaseFaceMap = JNode.Obj();
        public JNode BaseShapeKeys = JNode.Arr();
        /// <summary>에디터에 저장한 카메라 위치 (avhost.json studio.cameras).</summary>
        public JNode SavedCameras = JNode.Arr();
        /// <summary>이름만 적은 스튜디오 프리셋을 찾는 폴더 (에디터가 저장하는 곳).</summary>
        public string? PresetDir;
    }

    public sealed class SceneProblems
    {
        public readonly List<string> Errors = new List<string>(), Warnings = new List<string>();
        public void Error(string s) { if (!Errors.Contains(s)) Errors.Add(s); }
        public void Warn(string s) { if (!Warnings.Contains(s)) Warnings.Add(s); }
    }

    /// <summary>한 장을 찍는 데 필요한 값 전부.</summary>
    public sealed class SceneShot
    {
        public string Name = "shot";
        public int Width = 1920, Height = 1080, Supersample = 2;
        public bool Transparent;
        public double Ao = 0.5, Brightness, Contrast = 1, Saturation = 1;
        public int CaptureAa = 8;
        public JNode Environment = JNode.Arr();
        public JNode Pose = JNode.Null;
        public bool FloorFit = true;
        /// <summary>setExpression의 shapes (renderer·shape·weight).</summary>
        public JNode Expression = JNode.Arr();
        public float[] AvatarTrs = Addr.Identity;
        /// <summary>원본과 다르게 켜고 끌 렌더러.</summary>
        public Dictionary<string, bool> Renderers = new Dictionary<string, bool>();
        public JNode Background = JNode.Obj().Set("mode", "none");
        public double BgExposure = 1, BgRotation;
        public JNode Floor = JNode.Obj().Set("enabled", false);
        public JNode Props = JNode.Arr();
        public JNode Dof = JNode.Obj().Set("enabled", false);
        public JNode AvatarOnly = JNode.Obj().Set("on", false);
        public JNode Guests = JNode.Arr();
        public JNode Camera = JNode.Obj();
        public bool PhysBones = true;
        public double Settle = 0.8;
        public JNode Raw = JNode.Arr();
        /// <summary>있으면 사진 대신 영상 { seconds, fps, mode, turns, format }.</summary>
        public JNode Record = JNode.Null;
        public bool QualityStudio = true, EmbedSession;
        public string? SaveExpression, SaveStudioPreset;
        /// <summary>있으면 사진 대신 스티커 { spec, outline, color }.</summary>
        public JNode Sticker = JNode.Null;
        public JNode Tracking = JNode.Obj();
        public JNode Lip = JNode.Null;
        public JNode FaceMap = JNode.Obj();
        public JNode ShapeKeyDefs = JNode.Arr();
        public JNode ShapeLayer = Msg.Make(Msg.SetExpression).Set("layer", "keys").Set("shapes", JNode.Arr());
        public readonly Dictionary<int, JNode> ExpressionKeys = new Dictionary<int, JNode>();
        public JNode Overlay = JNode.Obj();
    }

    public static partial class SceneSpec
    {
        static readonly string[] TopKeys =
        {
            "avatar", "name", "brief", "out", "size", "width", "height", "transparent", "supersample", "quality", "adjust",
            "pose", "expression", "hide", "show", "place", "lighting", "lights", "post", "background", "floor", "avatarOnly",
            "props", "guests", "dof", "camera", "physBones", "raw", "record", "shots", "look",
            "studioPreset", "saveStudioPreset", "sticker", "tracking", "lip", "faceMap", "shapeKeys", "expressionKeys", "saveExpression", "embedSession", "overlay",
        };
        static readonly string[] CameraKeys = { "preset", "bone", "size", "angle", "pitch", "zoom", "distance", "fov", "ortho", "pivot", "up", "right", "saved", "state" };
        static readonly string[] CameraPresets = { "full", "upper", "face" };
        static readonly string[] PoseKeys = { "base", "time", "muscles", "add", "hands", "mirror", "bodyRotation", "bodyHeight", "floorFit" };
        static readonly string[] LightKeys = { "type", "color", "intensity", "range", "around", "height", "distance", "aim", "angle", "follow" };

        // ---------------- 펼치기 ----------------
        /// <summary>객체는 안쪽까지 합치고, 나머지(배열·값·null)는 over가 이긴다.</summary>
        public static JNode Merge(JNode a, JNode b)
        {
            if (a.Kind != JKind.Object || b.Kind != JKind.Object) return b.Clone();
            var o = a.Clone();
            foreach (var kv in b.Props)
                o.Set(kv.Key, o.Has(kv.Key) ? Merge(o[kv.Key], kv.Value) : kv.Value.Clone());
            return o;
        }

        /// <summary>장면 하나 → 장마다 합친 값. shots가 없으면 한 장. 이름이 없으면 shot1, shot2… 같은 이름은 뒤에 번호.</summary>
        public static List<JNode> Expand(JNode scene)
        {
            var bas = scene.Clone(); bas.Remove("shots");
            var list = new List<JNode>();
            if (scene["shots"].Kind != JKind.Array || scene["shots"].Count == 0) list.Add(bas);
            else foreach (var s in scene["shots"].Items) list.Add(Merge(bas, s.Kind == JKind.Object ? s : JNode.Obj()));
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                var name = SafeName(list[i]["name"].AsString);
                if (name.Length == 0 || list.Count > 1 && !scene["shots"][i].Has("name")) name = list.Count == 1 ? "shot" : $"shot{i + 1}";
                var n = name;
                for (int k = 2; !used.Add(n); k++) n = $"{name}_{k}";
                list[i].Set("name", n);
            }
            return list;
        }

        public static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        // ---------------- 장 하나 ----------------
        public static SceneShot Compile(JNode s, SceneContext ctx, SceneProblems p)
        {
            var shot = new SceneShot { Name = s["name"].AsString };
            string where = $"[{shot.Name}] ";
            s = WithStudioPreset(s, ctx, where, p);
            Unknown(s, TopKeys, where, "", p, allowPrivate: true);

            ReadSize(s, shot, where, p);
            shot.Transparent = s["transparent"].AsBool;
            shot.Supersample = s.Has("supersample") ? Math.Clamp(s["supersample"].AsInt, 1, 4) : shot.Width * shot.Height > 4_000_000 ? 1 : 2;
            var q = s["quality"];
            if (q.Has("ao")) shot.Ao = Math.Clamp(q["ao"].AsDouble, 0, 1);
            if (q.Has("aa")) shot.CaptureAa = Math.Clamp(q["aa"].AsInt, 1, 32);
            var adj = s["adjust"];
            if (adj.Has("brightness")) shot.Brightness = Math.Clamp(adj["brightness"].AsDouble, -1, 1);
            if (adj.Has("contrast")) shot.Contrast = Math.Clamp(adj["contrast"].AsDouble, 0, 2);
            if (adj.Has("saturation")) shot.Saturation = Math.Clamp(adj["saturation"].AsDouble, 0, 2);

            shot.Environment = Environment(s, ctx, where, p);
            if (s.Has("pose"))
            {
                shot.Pose = Pose(s["pose"], ctx, where, p);
                if (s["pose"].Has("floorFit")) shot.FloorFit = s["pose"]["floorFit"].AsBool;
            }
            else shot.Pose = ctx.BasePose;
            shot.Expression = s.Has("expression") ? Expression(s["expression"], ctx, where, p) : ctx.BaseExpression;
            ReadRenderers(s, shot, ctx, where, p);
            if (s.Has("place")) shot.AvatarTrs = Place(s["place"]);
            Studio(s, shot, ctx, where, p);
            shot.Guests = Guests(s["guests"], ctx, where, p);
            shot.Camera = Camera(s["camera"], ctx, where, p);

            var pb = s["physBones"];
            if (pb.Kind == JKind.Bool) shot.PhysBones = pb.AsBool;
            else if (pb.Kind == JKind.Object)
            {
                shot.PhysBones = !pb.Has("enabled") || pb["enabled"].AsBool;
                if (pb.Has("settle")) shot.Settle = Math.Clamp(pb["settle"].AsDouble, 0, 10);
            }
            if (s["raw"].Kind == JKind.Array) shot.Raw = s["raw"];
            StudioTabs(s, shot, ctx, where, p);
            return shot;
        }

        static void ReadSize(JNode s, SceneShot shot, string where, SceneProblems p)
        {
            var size = s["size"].AsString.Trim().ToLowerInvariant();
            switch (size)
            {
                case "": break;
                case "landscape": case "fhd": shot.Width = 1920; shot.Height = 1080; break;
                case "portrait": shot.Width = 1080; shot.Height = 1920; break;
                case "square": shot.Width = 1080; shot.Height = 1080; break;
                case "thumbnail": shot.Width = 1280; shot.Height = 720; break;
                case "4k": shot.Width = 3840; shot.Height = 2160; break;
                case "4k-portrait": shot.Width = 2160; shot.Height = 3840; break;
                default:
                    var a = size.Split('x');
                    if (a.Length == 2 && int.TryParse(a[0], out var w) && int.TryParse(a[1], out var h)) { shot.Width = w; shot.Height = h; }
                    else p.Error($"{where}size를 읽지 못했어요: {size} (예: 1080x1920, portrait, landscape, square, thumbnail, 4k, 4k-portrait)");
                    break;
            }
            if (s.Has("width")) shot.Width = s["width"].AsInt;
            if (s.Has("height")) shot.Height = s["height"].AsInt;
            if (shot.Width < 16 || shot.Height < 16 || shot.Width > 8192 || shot.Height > 8192) p.Error($"{where}크기는 16~8192 픽셀이에요: {shot.Width}x{shot.Height}");
        }

        // ---------------- 자세 ----------------
        /// <summary>쉬는 자세(불러온 자세)를 값으로.</summary>
        static JNode RestPose(SceneContext ctx) => PoseValue.Make(ctx.Rest);

        /// <summary>문자열(프리셋·rest·저장한 자세 이름·.anim·.pose.json) 또는 { base, muscles, add, hands, mirror, … }. Null = 불러온 그대로.</summary>
        public static JNode Pose(JNode spec, SceneContext ctx, string where, SceneProblems p, int depth = 0)
        {
            if (spec.IsNull) return JNode.Null;
            if (depth > 4) { p.Error($"{where}자세 파일이 서로를 너무 깊게 불러요"); return JNode.Null; }
            if (spec.Kind == JKind.String) return NamedPose(spec.AsString, 0, ctx, where, p, depth);
            if (spec.Kind != JKind.Object) { p.Error($"{where}pose는 이름이나 객체로 적어 주세요"); return JNode.Null; }
            Unknown(spec, PoseKeys, where, "pose.", p);

            JNode pose;
            if (!spec.Has("base")) pose = RestPose(ctx);
            else if (spec["base"].Kind == JKind.String) pose = NamedPose(spec["base"].AsString, spec["time"].AsFloat, ctx, where, p, depth);
            else pose = Pose(spec["base"], ctx, where, p, depth + 1);
            if (pose.IsNull) pose = RestPose(ctx);

            pose = PoseValue.Named(pose, "");
            foreach (var kv in spec["muscles"].Props)
                if (CheckMuscle(kv.Key, where, p)) pose = PoseValue.With(pose, kv.Key, Clamp1(kv.Value.AsFloat, kv.Key, where, p));
            foreach (var kv in spec["add"].Props)
                if (CheckMuscle(kv.Key, where, p))
                {
                    var now = PoseValue.MusclesOf(pose).TryGetValue(kv.Key, out var v) ? v : 0;
                    pose = PoseValue.With(pose, kv.Key, Clamp1(now + kv.Value.AsFloat, kv.Key, where, p));
                }
            foreach (var kv in spec["hands"].Props)
            {
                var side = kv.Key.ToLowerInvariant() switch { "left" => "Left", "right" => "Right", "both" => "Both", _ => "" };
                if (side.Length == 0) { p.Error($"{where}pose.hands는 left·right·both만 돼요: {kv.Key}"); continue; }
                if (kv.Value.Kind == JKind.Number) pose = HandPose.WithGrip(pose, side, kv.Value.AsFloat, ctx.Rest);
                else if (HandPose.Shapes.Any(h => h.Key == kv.Value.AsString)) pose = HandPose.WithShape(pose, side, kv.Value.AsString, ctx.Rest);
                else p.Error($"{where}손 모양을 모르겠어요: {kv.Value.AsString} (쓸 수 있는 것: {string.Join(", ", HandPose.Shapes.Select(h => h.Key))}, 또는 0~1 쥐기)");
            }
            if (spec.Has("bodyRotation"))
            {
                var e = spec["bodyRotation"].ToFloatArray();
                if (e.Length != 3) p.Error($"{where}pose.bodyRotation은 [x, y, z] 도 단위예요");
                else pose = WithBody(pose, Euler(e[0], e[1], e[2]), null);
            }
            if (spec.Has("bodyHeight")) pose = WithBody(pose, null, spec["bodyHeight"].AsFloat);
            if (spec.Has("mirror"))
            {
                switch (spec["mirror"].AsString)
                {
                    case "flip": pose = PoseValue.Mirrored(pose, PoseValue.Symmetry.Flip); break;
                    case "leftToRight": pose = PoseValue.Mirrored(pose, PoseValue.Symmetry.LeftToRight); break;
                    case "rightToLeft": pose = PoseValue.Mirrored(pose, PoseValue.Symmetry.RightToLeft); break;
                    default: p.Error($"{where}pose.mirror는 flip·leftToRight·rightToLeft만 돼요: {spec["mirror"].AsString}"); break;
                }
            }
            return pose;
        }

        static JNode NamedPose(string name, float time, SceneContext ctx, string where, SceneProblems p, int depth)
        {
            if (name == "rest") return JNode.Null;
            var preset = PosePresets.Find(name);
            if (preset != null) return preset.Value;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".anim" || ext == ".json")
            {
                var path = Full(name, ctx);
                if (!File.Exists(path)) { p.Error($"{where}자세 파일이 없어요: {path}"); return JNode.Null; }
                try
                {
                    if (ext == ".anim")
                    {
                        var clip = AnimClip.Load(path);
                        if (!clip.IsHumanoid) { p.Error($"{where}휴머노이드 .anim이 아니에요: {path}"); return JNode.Null; }
                        return clip.SamplePose(time);
                    }
                    return PoseFile(path, ctx, where, p, depth);
                }
                catch (Exception e) when (e is InvalidDataException || e is FormatException || e is IndexOutOfRangeException || e is IOException) { p.Error($"{where}자세 파일을 읽지 못했어요: {path} — {e.Message}"); return JNode.Null; }
            }
            // 저장한 자세(자세 페이지) → 함께 넣은 자세 (bin/shoot-poses)
            foreach (var (n, path) in PoseLibrary.List())
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return PoseFile(path, ctx, where, p, depth);
            var names = BundledPoses(ctx);
            if (ctx.PoseDir != null && names.Contains(name)) return PoseFile(Path.Combine(ctx.PoseDir, name + PoseLibrary.Extension), ctx, where, p, depth);
            var all = new[] { "rest" }.Concat(PosePresets.All.Select(x => x.Key)).Concat(names).Concat(PoseLibrary.List().Select(x => x.Name));
            p.Error($"{where}자세를 모르겠어요: {name} (쓸 수 있는 것: {string.Join(", ", all)})");
            return JNode.Null;
        }

        /// <summary>함께 넣은 자세 이름들.</summary>
        public static List<string> BundledPoses(SceneContext ctx)
        {
            if (ctx.PoseDir == null || !Directory.Exists(ctx.PoseDir)) return new List<string>();
            return Directory.GetFiles(ctx.PoseDir, "*" + PoseLibrary.Extension).Select(f => { var n = Path.GetFileName(f); return n.Substring(0, n.Length - PoseLibrary.Extension.Length); }).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>.pose.json: 자세 페이지가 저장한 { name, pose: { muscles… } } 또는 장면과 같은 { pose: { base, muscles, hands… } }.</summary>
        static JNode PoseFile(string path, SceneContext ctx, string where, SceneProblems p, int depth)
        {
            var n = JNode.Parse(File.ReadAllText(path));
            var spec = n.Has("pose") ? n["pose"] : n;
            // 머슬을 다 적은 값은 그대로, 조립식은 다른 자세처럼 만든다. 쉬는 자세 위에 덧쓰지 않는다
            if (!spec.Has("base") && !spec.Has("hands") && !spec.Has("add") && !spec.Has("mirror")) return spec.Clone();
            return Pose(spec, ctx, where, p, depth + 1);
        }

        static bool CheckMuscle(string name, string where, SceneProblems p)
        {
            if (Muscles.IsMuscle(name)) return true;
            p.Error($"{where}머슬 이름을 모르겠어요: \"{name}\"{Suggest(name, Muscles.Names)}");
            return false;
        }

        static float Clamp1(float v, string name, string where, SceneProblems p)
        {
            if (v < -1.5f || v > 1.5f) p.Warn($"{where}머슬 값은 보통 -1~1이에요: {name} = {v.ToString("0.##", CultureInfo.InvariantCulture)}");
            return v;
        }

        static JNode WithBody(JNode pose, float[]? rot, float? height)
        {
            var o = pose.Clone();
            if (rot != null) o.Set("bodyRotation", JNode.FloatArray(rot));
            if (height != null) o.Set("bodyHeight", height.Value);
            return o;
        }

        /// <summary>Unity Quaternion.Euler와 같은 순서 (Z → X → Y). [x, y, z, w]</summary>
        public static float[] Euler(float x, float y, float z)
        {
            double r = Math.PI / 360;
            double cx = Math.Cos(x * r), sx = Math.Sin(x * r), cy = Math.Cos(y * r), sy = Math.Sin(y * r), cz = Math.Cos(z * r), sz = Math.Sin(z * r);
            return new[]
            {
                (float)(cy * sx * cz + sy * cx * sz),
                (float)(sy * cx * cz - cy * sx * sz),
                (float)(cy * cx * sz - sy * sx * cz),
                (float)(cy * cx * cz + sy * sx * sz),
            };
        }

        // ---------------- 표정 ----------------
        /// <summary>문자열(아바타 기본 표정 이름·.anim·none) 또는 { name, anim, time, shapes: { 이름: 0~100 } }. setExpression의 shapes로.</summary>
        public static JNode Expression(JNode spec, SceneContext ctx, string where, SceneProblems p)
        {
            var arr = JNode.Arr();
            if (spec.IsNull || spec.AsString == "none") return arr;
            var scene = ctx.Scene;
            void AddBase(string name, float time)
            {
                JNode? e = null;
                if (name.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    var path = Full(name, ctx);
                    if (!File.Exists(path)) { p.Error($"{where}표정 파일이 없어요: {path}"); return; }
                    try { e = FaceExpression.FromClip(scene, AnimClip.Load(path), time, out int miss, out int _); if (miss > 0) p.Warn($"{where}표정 {name}: 이 아바타에 없는 메시 {miss}개는 건너뛰었어요"); }
                    catch (Exception x) when (x is InvalidDataException || x is IOException) { p.Error($"{where}표정 파일을 읽지 못했어요: {x.Message}"); return; }
                }
                else
                {
                    var ir = scene.Expressions.FirstOrDefault(x => x.Name == name) ?? scene.Expressions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (ir == null) { p.Error($"{where}표정을 모르겠어요: \"{name}\"{Suggest(name, scene.Expressions.Select(x => x.Name))}"); return; }
                    e = FaceExpression.FromIr(scene, ir);
                }
                foreach (var (r, sh, v) in FaceExpression.Shapes(e!)) Put(arr, r, sh, v);
            }
            if (spec.Kind == JKind.String) { AddBase(spec.AsString, 0); return arr; }
            if (spec.Kind != JKind.Object) { p.Error($"{where}expression은 이름이나 객체로 적어 주세요"); return arr; }
            Unknown(spec, new[] { "name", "anim", "time", "shapes", "renderer" }, where, "expression.", p);
            if (spec.Has("name")) AddBase(spec["name"].AsString, 0);
            if (spec.Has("anim")) AddBase(spec["anim"].AsString, spec["time"].AsFloat);
            var only = spec["renderer"].AsStringOrNull;
            foreach (var kv in spec["shapes"].Props)
            {
                float w = kv.Value.AsFloat;
                if (w < 0 || w > 100) p.Warn($"{where}블렌드쉐이프 값은 0~100이에요: {kv.Key} = {w.ToString("0.##", CultureInfo.InvariantCulture)}");
                int found = 0;
                foreach (var r in scene.Renderers)
                {
                    if (only != null && r.Id != only && RendererName(scene, r) != only) continue;
                    var m = scene.FindMesh(r.Mesh);
                    if (m == null || m.BlendShapeIndex(kv.Key) < 0) continue;
                    Put(arr, r.Id, kv.Key, w); found++;
                }
                if (found == 0) p.Error($"{where}블렌드쉐이프를 모르겠어요: \"{kv.Key}\"{Suggest(kv.Key, scene.Meshes.SelectMany(m => m.BlendShapes).Select(b => b.Name).Distinct())}");
            }
            return arr;
        }

        static void Put(JNode arr, string renderer, string shape, float weight)
        {
            foreach (var x in arr.Items)
                if (x["renderer"].AsString == renderer && x["shape"].AsString == shape) { x.Set("weight", weight); return; }
            arr.Add(JNode.Obj().Set("renderer", renderer).Set("shape", shape).Set("weight", weight));
        }

        public static string RendererName(IrScene s, IrRenderer r) => r.Node >= 0 && r.Node < s.Nodes.Count ? s.Nodes[r.Node].Name : r.Id;

        // ---------------- 아바타 ----------------
        static void ReadRenderers(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            void Read(string key, bool on)
            {
                foreach (var n in s[key].Items)
                {
                    var name = n.AsString;
                    var hits = ctx.Scene.Renderers.Where(r => r.Id == name || string.Equals(RendererName(ctx.Scene, r), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count == 0) p.Error($"{where}{key}: 렌더러를 모르겠어요: \"{name}\"{Suggest(name, ctx.Scene.Renderers.Select(r => RendererName(ctx.Scene, r)))}");
                    foreach (var r in hits) shot.Renderers[r.Id] = on;
                }
            }
            Read("hide", false);
            Read("show", true);
        }

        static float[] Place(JNode pl)
        {
            var pos = pl.Has("pos") ? pl["pos"].ToFloatArray() : new float[3];
            if (pos.Length != 3) pos = new float[3];
            var q = Euler(0, pl["yaw"].AsFloat, 0);
            return new[] { pos[0], pos[1], pos[2], q[0], q[1], q[2], q[3] };
        }

        // ---------------- 조명·환경 ----------------
        internal static JNode Environment(JNode s, SceneContext ctx, string where, SceneProblems p)
        {
            if (!s.Has("lighting") && !s.Has("lights") && !s.Has("post") && !s.Has("look")) return ctx.BaseEnvironment;
            var lighting = s["lighting"].Kind == JKind.String ? JNode.Obj().Set(LightingLayouts.Keys.Contains(s["lighting"].AsString) ? "layout" : "preset", s["lighting"].AsString) : s.Has("lighting") ? Colors(s["lighting"], where, p) : JNode.Obj();
            var layout = lighting["layout"].AsStringOrNull;
            lighting.Remove("layout");
            var look = s["look"].AsString;
            if (look.Length > 0 && look != "broadcast") p.Error($"{where}look은 broadcast만 돼요: {look}");
            if (look == "broadcast" && layout == null) layout = LightingLayouts.Keys[0];
            if (layout != null && !LightingLayouts.Keys.Contains(layout)) p.Error($"{where}lighting.layout을 모르겠어요: {layout} ({string.Join("·", LightingLayouts.Keys)})");
            JNode baseLight = JNode.Obj(), basePost = JNode.Obj();
            JNode? fill = null;
            void Into(JNode to, (string Name, JNode Value)[] ps) { foreach (var (n, v) in ps) to.Set(n, v.Clone()); }
            if (layout != null)
                foreach (var (prov, ps) in LightingLayouts.Get(layout, LightingLayouts.EyeHeight(ctx.Scene.ViewPosition)))
                {
                    if (prov == "UnityLighting") Into(baseLight, ps);
                    else if (prov == "Lights" && ps.Any(x => x.Name == "l1_on" && x.Value.AsBool)) { fill = JNode.Obj(); Into(fill, ps); }
                }
            if (look == "broadcast")
                foreach (var (prov, ps) in LightingLayouts.BroadcastPost) Into(prov == "PostProcess" ? basePost : baseLight, ps);
            lighting = Merge(baseLight, lighting);
            if (!lighting.Has("preset")) lighting.Set("preset", "bright");
            CheckEnv("UnityLighting", lighting, ctx, where, p);
            var list = JNode.Arr();
            list.Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", lighting));

            int offset = fill != null ? 1 : 0, total = s["lights"].Count + offset;
            if (total > 0)
            {
                if (total > 8) p.Error($"{where}lights는 조명 배치의 채움광까지 8개예요");
                var par = fill ?? JNode.Obj();
                for (int i = 1; i <= 8; i++) par.Set($"l{i}_on", i <= total);
                int n = offset;
                foreach (var l in s["lights"].Items)
                {
                    n++;
                    Unknown(l, LightKeys, where, $"lights[{n - offset - 1}].", p);
                    foreach (var kv in Colors(l, where, p).Props) par.Set($"l{n}_{kv.Key}", kv.Value);
                }
                CheckEnv("Lights", par, ctx, where, p);
                list.Add(JNode.Obj().Set("provider", "Lights").Set("version", 1).Set("params", par));
            }
            if (s.Has("post") || basePost.Count > 0)
            {
                var post = Merge(basePost, s.Has("post") ? Colors(s["post"], where, p) : JNode.Obj());
                CheckEnv("PostProcess", post, ctx, where, p);
                list.Add(JNode.Obj().Set("provider", "PostProcess").Set("version", 1).Set("params", post));
            }
            return list;
        }

        /// <summary>렌더러가 알려 준 스키마로 값 이름·범위·보기를 검사한다.</summary>
        static void CheckEnv(string provider, JNode par, SceneContext ctx, string where, SceneProblems p)
        {
            var schema = ctx.EnvSchema.Items.FirstOrDefault(e => e["id"].AsString == provider);
            if (schema == null) return;
            var items = schema["params"].Items.ToList();
            foreach (var kv in par.Props)
            {
                var it = items.FirstOrDefault(x => x["name"].AsString == kv.Key);
                if (it == null) { p.Error($"{where}{provider} 값 이름을 모르겠어요: \"{kv.Key}\"{Suggest(kv.Key, items.Select(x => x["name"].AsString))}"); continue; }
                var type = it["type"].AsString;
                if (type == "float" && (kv.Value.AsDouble < it["min"].AsDouble || kv.Value.AsDouble > it["max"].AsDouble))
                    p.Warn($"{where}{provider}.{kv.Key} = {kv.Value.AsString}: 범위 {it["min"].AsString}~{it["max"].AsString}를 벗어나요");
                if (type == "enum" && !it["options"].Items.Any(o => o.AsString == kv.Value.AsString))
                    p.Error($"{where}{provider}.{kv.Key} = \"{kv.Value.AsString}\": 쓸 수 있는 값은 {string.Join(", ", it["options"].Items.Select(o => o.AsString))}");
            }
        }

        /// <summary>값 안의 "#RRGGBB"를 [r, g, b]로.</summary>
        static JNode Colors(JNode par, string where, SceneProblems p)
        {
            var o = JNode.Obj();
            foreach (var kv in par.Props)
                o.Set(kv.Key, kv.Value.AsStringOrNull is string t && t.StartsWith("#") ? Color(t, where, p) : kv.Value.Clone());
            return o;
        }

        /// <summary>"#RGB"·"#RRGGBB" 또는 [r, g, b] (0~1, 감마 값).</summary>
        public static JNode Color(JNode c, string where, SceneProblems p)
        {
            if (c.Kind == JKind.Array) return c.Clone();
            var h = c.AsString.TrimStart('#');
            if (h.Length == 3) h = string.Concat(h.Select(x => new string(x, 2)));
            if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                return JNode.FloatArray(new[] { (v >> 16 & 255) / 255f, (v >> 8 & 255) / 255f, (v & 255) / 255f });
            p.Error($"{where}색을 읽지 못했어요: {c.AsString} (#RRGGBB 또는 [r, g, b])");
            return JNode.FloatArray(new[] { 1f, 1f, 1f });
        }

        // ---------------- 스튜디오 ----------------
        static void Studio(JNode s, SceneShot shot, SceneContext ctx, string where, SceneProblems p)
        {
            var bg = s["background"];
            if (bg.Kind == JKind.String)
            {
                var t = bg.AsString;
                bg = t.StartsWith("#") ? JNode.Obj().Set("mode", "gradient").Set("top", t).Set("bottom", t)
                    : t == "none" ? JNode.Obj().Set("mode", "none") : JNode.Obj().Set("path", t);
            }
            if (bg.Kind == JKind.Object)
            {
                var mode = bg["mode"].AsString;
                var path = bg["path"].AsStringOrNull;
                if (mode.Length == 0) mode = path == null ? (bg.Has("top") ? "gradient" : "none") : path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ? "panorama" : "image";
                var m = JNode.Obj().Set("mode", mode);
                if (mode == "image" || mode == "panorama")
                {
                    var full = path == null ? "" : Full(path, ctx);
                    if (!File.Exists(full)) p.Error($"{where}배경 이미지가 없어요: {full}");
                    m.Set("path", full);
                }
                else if (mode == "gradient")
                {
                    m.Set("top", Color(bg.Has("top") ? bg["top"] : "#BFD4F2", where, p)).Set("bottom", Color(bg.Has("bottom") ? bg["bottom"] : "#F2E6D8", where, p));
                }
                else if (mode != "none") p.Error($"{where}background.mode는 none·gradient·image·panorama만 돼요: {mode}");
                shot.Background = m;
                if (bg.Has("exposure")) shot.BgExposure = Math.Max(0, bg["exposure"].AsDouble);
                if (bg.Has("rotation")) shot.BgRotation = bg["rotation"].AsDouble;
            }

            var fl = s["floor"];
            if (fl.Kind == JKind.String) fl = JNode.Obj().Set("mode", fl.AsString);
            if (fl.Kind == JKind.Object)
            {
                var mode = fl.Has("mode") ? fl["mode"].AsString : "solid";
                if (mode == "none") shot.Floor = JNode.Obj().Set("enabled", false);
                else if (mode == "shadow" || mode == "solid")
                    shot.Floor = JNode.Obj().Set("enabled", true).Set("mode", mode).Set("strength", fl.Has("strength") ? fl["strength"].AsFloat : 0.6f)
                        .Set("color", Color(fl.Has("color") ? fl["color"] : "#CFCFD4", where, p));
                else p.Error($"{where}floor는 none·shadow·solid만 돼요: {mode}");
            }

            shot.Props = JNode.Arr();
            int i = 0;
            foreach (var pr in s["props"].Items)
            {
                var src = pr["path"].AsString;
                var full = Full(src, ctx);
                if (!src.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase)) p.Error($"{where}소품은 .ir.json만 돼요 (FBX는 에디터의 소품 추가로 먼저 가져와 주세요): {src}");
                else if (!File.Exists(full)) p.Error($"{where}소품 파일이 없어요: {full}");
                shot.Props.Add(JNode.Obj().Set("id", pr.Has("id") ? pr["id"].AsString : $"prop{i}").Set("path", full)
                    .Set("pos", pr.Has("pos") ? pr["pos"] : JNode.FloatArray(new float[3])).Set("rotY", pr["rotY"].AsFloat)
                    .Set("scale", pr.Has("scale") ? pr["scale"].AsFloat : 1f).Set("visible", !pr.Has("visible") || pr["visible"].AsBool));
                i++;
            }

            var d = s["dof"];
            if (d.Kind == JKind.Bool) d = d.AsBool ? JNode.Obj() : JNode.Null;
            else if (d.Kind == JKind.Number) d = JNode.Obj().Set("strength", d.AsFloat);
            if (d.Kind == JKind.Object)
            {
                bool auto = d.Has("auto") ? d["auto"].AsBool : !d.Has("distance");
                shot.Dof = JNode.Obj().Set("enabled", true).Set("strength", d.Has("strength") ? d["strength"].AsFloat : 0.25f).Set("auto", auto);
                if (d.Has("distance")) shot.Dof.Set("distance", d["distance"].AsFloat);
                if (d["show"].AsBool) shot.Dof.Set("show", true);
            }

            var only = s["avatarOnly"];
            if (only.Kind == JKind.String) shot.AvatarOnly = JNode.Obj().Set("on", true).Set("color", Color(only, where, p));
            else if (only.Kind == JKind.Bool && only.AsBool) shot.AvatarOnly = JNode.Obj().Set("on", true).Set("color", JNode.FloatArray(new[] { 0f, 1f, 0f }));
            // 후처리는 배경 단색에도 먹는다: 크로마키 배경이 얼룩져 깔끔히 빠지지 않는다
            if (shot.AvatarOnly["on"].AsBool && s.Has("post")) p.Warn($"{where}avatarOnly에 post를 함께 쓰면 단색 배경에도 후처리가 먹어요. 크로마키라면 이 장은 post를 빼 주세요");
        }

        static JNode Guests(JNode list, SceneContext ctx, string where, SceneProblems p)
        {
            var arr = JNode.Arr();
            int i = 0;
            foreach (var g in list.Items)
            {
                var full = Full(g["path"].AsString, ctx);
                if (!File.Exists(full)) p.Error($"{where}함께 세울 아바타 파일이 없어요: {full}");
                var m = JNode.Obj().Set("id", g.Has("id") ? g["id"].AsString : $"guest{i}").Set("path", full)
                    .Set("pos", g.Has("pos") ? g["pos"] : JNode.FloatArray(new float[3])).Set("rotY", g["yaw"].AsFloat)
                    .Set("scale", g.Has("scale") ? g["scale"].AsFloat : 1f).Set("visible", true);
                // 쉬는 자세 값을 모르므로 그 아바타는 프리셋·머슬을 그대로 쓴다
                var guestCtx = new SceneContext { Scene = ctx.Scene, BaseDir = ctx.BaseDir, PoseDir = ctx.PoseDir, Rest = PosePresets.Find("relaxed")!.Muscles };
                m.Set("pose", g.Has("pose") ? Pose(g["pose"], guestCtx, where, p) : JNode.Null);
                arr.Add(m);
                i++;
            }
            return arr;
        }

        // ---------------- 카메라 ----------------
        static JNode Camera(JNode c, SceneContext ctx, string where, SceneProblems p)
        {
            if (c.Kind == JKind.String) c = JNode.Obj().Set("preset", c.AsString);
            var o = c.Kind == JKind.Object ? c.Clone() : JNode.Obj();
            Unknown(o, CameraKeys, where, "camera.", p);
            if (o.Has("preset") && !CameraPresets.Contains(o["preset"].AsString)) p.Error($"{where}camera.preset을 모르겠어요: {o["preset"].AsString} (full·upper·face)");
            if (o.Has("bone") && !ctx.Scene.Humanoid.ContainsKey(o["bone"].AsString))
                p.Error($"{where}camera.bone을 이 아바타에서 찾지 못했어요: \"{o["bone"].AsString}\"{Suggest(o["bone"].AsString, ctx.Scene.Humanoid.Keys)}");
            if (o.Has("saved"))
            {
                var key = o["saved"].AsString;
                var cams = ctx.SavedCameras.Items.ToList();
                var hit = int.TryParse(key, out var num) && num >= 1 && num <= cams.Count ? cams[num - 1] : cams.FirstOrDefault(x => x["name"].AsString == key);
                if (hit == null) p.Error($"{where}camera.saved: 에디터에 저장한 위치를 찾지 못했어요: \"{key}\" (저장한 것: {string.Join(", ", cams.Select(x => x["name"].AsString))})");
                else o.Set("state", hit["state"]);
                o.Remove("saved");
            }
            if (o.Has("pivot") && o["pivot"].Count != 3) p.Error($"{where}camera.pivot은 [x, y, z]예요");
            if (o.Has("zoom") && o["zoom"].AsDouble <= 0) p.Error($"{where}camera.zoom은 0보다 커야 해요");
            return o;
        }

        /// <summary>
        /// 프리셋이 잡아 준 카메라 상태에 각도·거리 등을 더한다. angle 0 = 정면, + = 아바타 오른쪽에서 (렌더러 yaw = 180 + angle).
        /// pitch + = 위에서 내려다본다. zoom 2 = 거리 절반. up·right = 화면 기준으로 보는 점을 옮긴다 (m).
        /// </summary>
        public static JNode CameraState(JNode preset, JNode spec, bool keepYaw = false)
        {
            var pivot = preset["pivot"].ToFloatArray();
            if (pivot.Length != 3) pivot = new float[3];
            if (spec.Has("pivot")) pivot = spec["pivot"].ToFloatArray();
            double yaw = keepYaw && !spec.Has("angle") ? preset["yaw"].AsDouble : 180 + spec["angle"].AsDouble;
            double pitch = spec.Has("pitch") ? spec["pitch"].AsDouble : preset["pitch"].AsDouble;
            double distance = spec.Has("distance") ? spec["distance"].AsDouble : preset["distance"].AsDouble / (spec.Has("zoom") ? spec["zoom"].AsDouble : 1);
            double rad = yaw * Math.PI / 180;
            double right = spec["right"].AsDouble, up = spec["up"].AsDouble;
            pivot[0] += (float)(Math.Cos(rad) * right);
            pivot[1] += (float)up;
            pivot[2] += (float)(-Math.Sin(rad) * right);
            return JNode.Obj().Set("pivot", JNode.FloatArray(pivot)).Set("yaw", yaw).Set("pitch", pitch).Set("distance", Math.Max(0.05, distance))
                .Set("fov", spec.Has("fov") ? spec["fov"] : preset["fov"]).Set("ortho", spec.Has("ortho") ? spec["ortho"].AsBool : preset["ortho"].AsBool);
        }

        // ---------------- 도우미 ----------------
        static string Full(string path, SceneContext ctx) => Path.GetFullPath(Path.Combine(ctx.BaseDir, path));

        static void Unknown(JNode o, string[] allowed, string where, string prefix, SceneProblems p, bool allowPrivate = false)
        {
            foreach (var kv in o.Props)
                if (!allowed.Contains(kv.Key) && !(allowPrivate && kv.Key.StartsWith("_")))
                    p.Error($"{where}모르는 항목: \"{prefix}{kv.Key}\"{Suggest(kv.Key, allowed)}");
        }

        /// <summary>비슷한 이름 세 개까지 (글자 단위 편집 거리, 포함하면 먼저). 없으면 빈 글.</summary>
        public static string Suggest(string name, IEnumerable<string> candidates)
        {
            var low = name.ToLowerInvariant();
            var best = candidates.Distinct().Select(c => (c, d: c.ToLowerInvariant().Contains(low) || low.Contains(c.ToLowerInvariant()) ? 0 : Edit(low, c.ToLowerInvariant())))
                .Where(x => x.d <= Math.Max(2, name.Length / 2)).OrderBy(x => x.d).ThenBy(x => x.c.Length).Take(3).Select(x => x.c).ToList();
            return best.Count == 0 ? "" : $" — 혹시 {string.Join(", ", best)}?";
        }

        static int Edit(string a, string b)
        {
            var prev = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
            return prev[b.Length];
        }
    }
}
