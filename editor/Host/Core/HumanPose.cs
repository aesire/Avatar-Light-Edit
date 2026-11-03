// 휴머노이드 자세 (PLAN 3.5): Unity 머슬 이름표, 내장 자세 프리셋, 휴머노이드 .anim 읽기, 저장한 자세 파일.
// 자세 값은 Document의 Addr.Pose 하나에 { muscles: { 머슬 이름: 값 }, bodyRotation?, bodyHeight? }로 둔다.
// 머슬 값은 아바타에 묶이지 않아 다른 아바타에도 그대로 쓸 수 있다. 렌더러가 HumanPoseHandler로 적용한다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class Muscles
    {
        static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };   // Names보다 먼저 초기화되어야 한다

        /// <summary>Unity HumanTrait.MuscleName과 같은 이름·순서 (95개). 렌더러가 모르는 이름이 오면 로그에 알린다.</summary>
        public static readonly IReadOnlyList<string> Names = BuildNames();

        static List<string> BuildNames()
        {
            var n = new List<string>();
            foreach (var p in new[] { "Spine", "Chest", "UpperChest" })
                foreach (var m in new[] { "Front-Back", "Left-Right", "Twist Left-Right" }) n.Add(p + " " + m);
            foreach (var p in new[] { "Neck", "Head" })
                foreach (var m in new[] { "Nod Down-Up", "Tilt Left-Right", "Turn Left-Right" }) n.Add(p + " " + m);
            n.AddRange(new[] { "Left Eye Down-Up", "Left Eye In-Out", "Right Eye Down-Up", "Right Eye In-Out", "Jaw Close", "Jaw Left-Right" });
            foreach (var s in new[] { "Left", "Right" })
                foreach (var m in new[] { "Upper Leg Front-Back", "Upper Leg In-Out", "Upper Leg Twist In-Out", "Lower Leg Stretch", "Lower Leg Twist In-Out", "Foot Up-Down", "Foot Twist In-Out", "Toes Up-Down" })
                    n.Add(s + " " + m);
            foreach (var s in new[] { "Left", "Right" })
                foreach (var m in new[] { "Shoulder Down-Up", "Shoulder Front-Back", "Arm Down-Up", "Arm Front-Back", "Arm Twist In-Out", "Forearm Stretch", "Forearm Twist In-Out", "Hand Down-Up", "Hand In-Out" })
                    n.Add(s + " " + m);
            foreach (var s in new[] { "Left", "Right" })
                foreach (var f in Fingers)
                    foreach (var m in new[] { "1 Stretched", "Spread", "2 Stretched", "3 Stretched" }) n.Add($"{s} {f} {m}");
            return n;
        }

        /// <summary>자세 페이지의 묶음 이름.</summary>
        public static string Group(string name)
        {
            string side = name.StartsWith("Left ") ? "왼" : name.StartsWith("Right ") ? "오른" : "";
            if (Fingers.Any(f => name.Contains(" " + f + " "))) return side + "손가락";
            if (name.Contains("Leg") || name.Contains("Foot") || name.Contains("Toes")) return side + "다리";
            if (name.Contains("Shoulder") || name.Contains("Arm") || name.Contains("Hand")) return side + "팔";
            return "몸통 · 머리";
        }

        public static readonly string[] GroupOrder = { "몸통 · 머리", "왼팔", "오른팔", "왼손가락", "오른손가락", "왼다리", "오른다리" };

        static readonly (string En, string Ko)[] Parts =
        {
            ("UpperChest", "윗가슴"), ("Chest", "가슴"), ("Spine", "척추"), ("Neck", "목"), ("Head", "머리"), ("Eye", "눈"), ("Jaw", "턱"),
            ("Upper Leg", "허벅지"), ("Lower Leg", "종아리"), ("Foot", "발목"), ("Toes", "발가락"),
            ("Shoulder", "어깨"), ("Forearm", "아래팔"), ("Arm", "위팔"), ("Hand", "손목"),
            ("Thumb", "엄지"), ("Index", "검지"), ("Middle", "중지"), ("Ring", "약지"), ("Little", "새끼"),
        };
        static readonly (string En, string Ko)[] Moves =
        {
            ("Twist Left-Right", "비틀기"), ("Twist In-Out", "비틀기"), ("Nod Down-Up", "끄덕이기"), ("Tilt Left-Right", "갸웃하기"), ("Turn Left-Right", "돌리기"),
            ("Front-Back", "앞뒤"), ("Left-Right", "좌우"), ("Down-Up", "아래위"), ("Up-Down", "위아래"), ("In-Out", "안팎"),
            ("1 Stretched", "첫째 마디 펴기"), ("2 Stretched", "둘째 마디 펴기"), ("3 Stretched", "셋째 마디 펴기"), ("Spread", "벌리기"),
            ("Stretch", "펴기"), ("Close", "다물기"),
        };

        /// <summary>"Left Forearm Stretch" → "왼 아래팔 펴기".</summary>
        public static string Label(string name)
        {
            string rest = name, side = "";
            if (rest.StartsWith("Left ")) { side = "왼 "; rest = rest.Substring(5); }
            else if (rest.StartsWith("Right ")) { side = "오른 "; rest = rest.Substring(6); }
            var part = Parts.FirstOrDefault(p => rest.StartsWith(p.En + " "));
            if (part.En == null) return name;
            rest = rest.Substring(part.En.Length + 1);
            var move = Moves.FirstOrDefault(m => rest == m.En);
            return side + part.Ko + " " + (move.En != null ? move.Ko : rest);
        }

        /// <summary>.anim 커브 속성 이름 → 머슬 이름. 손가락은 "LeftHand.Index.1 Stretched" → "Left Index 1 Stretched". 머슬이 아니면 null.</summary>
        public static string? FromAnimAttribute(string attr)
        {
            var m = Regex.Match(attr, @"^(Left|Right)Hand\.(\w+)\.(\d Stretched|Spread)$");
            var name = m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}" : attr;
            return NameSet.Contains(name) ? name : null;
        }

        /// <summary>머슬 이름 → .anim 커브 속성 (FromAnimAttribute의 거꾸로). 손가락 "Left Index 1 Stretched" → "LeftHand.Index.1 Stretched". 머슬이 아니면 null.</summary>
        public static string? ToAnimAttribute(string name)
        {
            if (!IsMuscle(name)) return null;
            var m = Regex.Match(name, @"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (\d Stretched|Spread)$");
            return m.Success ? $"{m.Groups[1].Value}Hand.{m.Groups[2].Value}.{m.Groups[3].Value}" : name;
        }

        static readonly HashSet<string> NameSet = new HashSet<string>(Names);
        public static bool IsMuscle(string name) => NameSet.Contains(name);

        /// <summary>
        /// 좌우 거울: Left ↔ Right 이름 (값 그대로 — 머슬은 양쪽이 같은 값이면 대칭 자세다).
        /// 가운데 머슬 중 좌우로 움직이는 것(Left-Right가 든 이름: 척추 좌우·비틀기, 머리 갸웃·돌리기, 턱 좌우)은 자기 자신에 부호를 뒤집는다.
        /// </summary>
        public static (string Name, float Sign) Mirror(string name)
        {
            if (name.StartsWith("Left ")) return ("Right " + name.Substring(5), 1);
            if (name.StartsWith("Right ")) return ("Left " + name.Substring(6), 1);
            return (name, name.Contains("Left-Right") ? -1 : 1);
        }

        public static bool IsSided(string name) => name.StartsWith("Left ") || name.StartsWith("Right ");

        /// <summary>휴머노이드 본 이름(HumanBodyBones) → 그 본을 움직이는 머슬. 예: LeftUpperArm → Left Arm 아래위·앞뒤·비틀기. Hips는 머슬이 없다 (몸 방향·높이).</summary>
        public static IReadOnlyList<string> OfBone(string bone)
        {
            string side = bone.StartsWith("Left") ? "Left" : bone.StartsWith("Right") ? "Right" : "";
            var rest = side.Length == 0 ? bone : bone.Substring(side.Length);
            foreach (var f in Fingers)
            {
                if (rest == f + "Proximal") return new[] { $"{side} {f} 1 Stretched", $"{side} {f} Spread" };
                if (rest == f + "Intermediate") return new[] { $"{side} {f} 2 Stretched" };
                if (rest == f + "Distal") return new[] { $"{side} {f} 3 Stretched" };
            }
            string? prefix = rest switch
            {
                "Spine" => "Spine", "Chest" => "Chest", "UpperChest" => "UpperChest", "Neck" => "Neck", "Head" => "Head", "Jaw" => "Jaw",
                "Eye" => "Eye", "Shoulder" => "Shoulder", "UpperArm" => "Arm", "LowerArm" => "Forearm", "Hand" => "Hand",
                "UpperLeg" => "Upper Leg", "LowerLeg" => "Lower Leg", "Foot" => "Foot", "Toes" => "Toes", _ => null,
            };
            if (prefix == null) return Array.Empty<string>();
            var p = (side.Length > 0 ? side + " " : "") + prefix + " ";
            return Names.Where(n => n.StartsWith(p)).ToList();
        }

        /// <summary>LeftUpperArm ↔ RightUpperArm. 가운데 본은 그대로.</summary>
        public static string OppositeBone(string bone) =>
            bone.StartsWith("Left") ? "Right" + bone.Substring(4) : bone.StartsWith("Right") ? "Left" + bone.Substring(5) : bone;
    }

    /// <summary>자세 값 { name?, muscles, bodyRotation?, bodyHeight? } 만들기·읽기. name은 화면 표시용 (프리셋·.anim·저장한 자세 이름).</summary>
    public static class PoseValue
    {
        public static JNode Make(IReadOnlyDictionary<string, float> muscles, float[]? bodyRotation = null, float? bodyHeight = null, string? name = null)
        {
            var m = JNode.Obj();
            foreach (var mu in Muscles.Names)
                if (muscles.TryGetValue(mu, out var v) && Math.Abs(v) > 1e-5f) m.Set(mu, Math.Round(v, 4));
            var o = JNode.Obj();
            if (!string.IsNullOrEmpty(name)) o.Set("name", name);
            o.Set("muscles", m);
            if (bodyRotation != null) o.Set("bodyRotation", JNode.FloatArray(bodyRotation));
            if (bodyHeight != null) o.Set("bodyHeight", Math.Round(bodyHeight.Value, 4));
            return o;
        }

        public static Dictionary<string, float> MusclesOf(JNode pose)
        {
            var d = new Dictionary<string, float>();
            foreach (var kv in pose["muscles"].Props) d[kv.Key] = kv.Value.AsFloat;
            return d;
        }

        public static string Name(JNode pose) => pose["name"].AsString;

        /// <summary>이름만 바꾼 자세.</summary>
        public static JNode Named(JNode pose, string name)
        {
            var o = JNode.Parse(pose.ToJson());
            if (name.Length > 0) o.Set("name", name); else o.Remove("name");
            return o;
        }

        /// <summary>쉬는 자세(불러온 그대로)와 같은 자세인가: 몸 방향·높이가 없고 모든 머슬이 rest와 거의 같다.</summary>
        public static bool IsRest(JNode pose, IReadOnlyDictionary<string, float> rest)
        {
            if (pose.Has("bodyRotation") || pose.Has("bodyHeight")) return false;
            var m = MusclesOf(pose);
            return Muscles.Names.All(n => Math.Abs((m.TryGetValue(n, out var a) ? a : 0) - (rest.TryGetValue(n, out var b) ? b : 0)) < 1e-3);
        }

        /// <summary>
        /// 뷰포트 본 조작 결과(렌더러 poseEdited)를 기록할 값으로: 머슬 이름은 Host 표에 있는 것만(값은 Make가 4자리로 반올림 — 3자리면 화면이 끌 때와 조금 달라진다), 이름 없음(직접 바꾼 자세).
        /// 쉬는 자세와 같으면 null (= 덮어쓰기 지우기).
        /// </summary>
        public static JNode? FromViewport(JNode pose, IReadOnlyDictionary<string, float> rest)
        {
            var d = new Dictionary<string, float>();
            foreach (var kv in pose["muscles"].Props) if (Muscles.IsMuscle(kv.Key)) d[kv.Key] = kv.Value.AsFloat;
            float[]? rot = pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            var v = Make(d, rot, h);
            return IsRest(v, rest) ? null : v;
        }

        /// <summary>머슬 하나만 바꾼 새 자세 (나머지·몸 방향은 그대로, 이름은 뗀다: 직접 바꾼 자세).</summary>
        public static JNode With(JNode pose, string muscle, float value)
        {
            var d = MusclesOf(pose);
            d[muscle] = value;
            float[]? rot = pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            return Make(d, rot, h);
        }

        public enum Symmetry { LeftToRight, RightToLeft, Flip }

        /// <summary>
        /// 대칭: LeftToRight/RightToLeft는 한쪽 팔·다리·손가락·눈을 반대쪽에 그대로 복사한다 (몸통·머리·몸 방향은 그대로).
        /// Flip은 거울에 비춘 자세 — 양쪽을 맞바꾸고 가운데 좌우 머슬과 몸 방향을 뒤집는다. 이름은 뗀다 (직접 바꾼 자세).
        /// </summary>
        public static JNode Mirrored(JNode pose, Symmetry mode)
        {
            var d = MusclesOf(pose);
            var next = new Dictionary<string, float>(d);
            float[]? rot = pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            foreach (var n in Muscles.Names)
            {
                var (from, sign) = Muscles.Mirror(n);
                float v = d.TryGetValue(from, out var x) ? x : 0;
                if (mode == Symmetry.Flip) next[n] = v * sign;
                else if (mode == Symmetry.LeftToRight ? n.StartsWith("Right ") : n.StartsWith("Left ")) next[n] = v;
            }
            // 몸 방향을 YZ 평면(좌우)에 비추면 쿼터니언 (x, y, z, w) → (x, −y, −z, w)
            if (mode == Symmetry.Flip && rot != null && rot.Length == 4) rot = new[] { rot[0], -rot[1], -rot[2], rot[3] };
            return Make(next, rot, h);
        }

        /// <summary>본 하나의 머슬만 쉬는 자세(rest) 값으로. Hips는 몸 방향·높이를 지운다.</summary>
        public static JNode ResetBone(JNode pose, string bone, IReadOnlyDictionary<string, float> rest)
        {
            var d = MusclesOf(pose);
            foreach (var n in Muscles.OfBone(bone)) d[n] = rest.TryGetValue(n, out var r) ? r : 0;
            bool hips = bone == "Hips";
            float[]? rot = !hips && pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = !hips && pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            return Make(d, rot, h);
        }

        /// <summary>본 하나의 머슬을 반대쪽 본에 복사한다 (왼위팔 → 오른위팔). 가운데 본이면 그대로.</summary>
        public static JNode CopyBoneToOpposite(JNode pose, string bone)
        {
            var d = MusclesOf(pose);
            foreach (var n in Muscles.OfBone(bone))
            {
                if (!Muscles.IsSided(n)) continue;
                d[Muscles.Mirror(n).Name] = d.TryGetValue(n, out var v) ? v : 0;
            }
            float[]? rot = pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            return Make(d, rot, h);
        }

        /// <summary>본 하나가 쉬는 자세와 다른가 (되돌리기 단추를 켤지).</summary>
        public static bool BoneModified(JNode pose, string bone, IReadOnlyDictionary<string, float> rest)
        {
            if (bone == "Hips") return pose.Has("bodyRotation") || pose.Has("bodyHeight");
            var d = MusclesOf(pose);
            return Muscles.OfBone(bone).Any(n => Math.Abs((d.TryGetValue(n, out var a) ? a : 0) - (rest.TryGetValue(n, out var b) ? b : 0)) > 1e-3);
        }
    }

    /// <summary>
    /// 손 모양: 손가락 여러 개를 한 번에 쥐기(0 = 불러온 자세의 편 손, 1 = 주먹)와 손 모양 프리셋.
    /// side는 "Left" · "Right" · "Both". 편 손은 쉬는 자세(rest) 값 — 쉬는 자세에 손가락 머슬이 없으면 프리셋의 편 손.
    /// </summary>
    public static class HandPose
    {
        static readonly string[] Four = { "Index", "Middle", "Ring", "Little" };
        static readonly string[] Joints = { "1 Stretched", "2 Stretched", "3 Stretched" };
        // 주먹: 손가락 마디를 다 굽히고 벌림 0, 엄지는 손가락 위로 접는다 (아바타 A에서 눈으로 맞춘 값)
        static readonly float[] FistFinger = { -0.8f, -1f, -1f };
        static readonly Dictionary<string, float> FistThumb = new Dictionary<string, float> { ["1 Stretched"] = -0.3f, ["Spread"] = -0.2f, ["2 Stretched"] = -0.4f, ["3 Stretched"] = -0.6f };

        public static readonly (string Key, string Name, string Tip)[] Shapes =
        {
            ("open", "편 손", "손가락을 모두 펴요 (불러온 자세의 손)"),
            ("relax", "편한 손", "손가락을 가볍게 굽혀요"),
            ("fist", "주먹", "손가락을 모두 쥐어요"),
            ("peace", "브이", "검지·중지만 펴서 벌려요"),
            ("point", "가리키기", "검지만 펴요"),
            ("thumbsUp", "엄지 척", "엄지만 펴요"),
        };

        public static IEnumerable<string> SidesOf(string side) => side == "Both" ? new[] { "Left", "Right" } : new[] { side };

        static float Rest(IReadOnlyDictionary<string, float> rest, string muscle, float fallback) => rest.TryGetValue(muscle, out var v) ? v : fallback;

        /// <summary>편 손의 머슬 값 (쉬는 자세, 없으면 프리셋 바탕의 편 손).</summary>
        static float Open(IReadOnlyDictionary<string, float> rest, string muscle)
        {
            var stand = PosePresets.Find("tpose")!.Muscles;
            return Rest(rest, muscle, stand.TryGetValue(muscle, out var s) ? s : 0);
        }

        static float Fist(string s, string finger, string joint) =>
            finger == "Thumb" ? FistThumb[joint] : joint == "Spread" ? 0 : FistFinger[Array.IndexOf(Joints, joint)];

        static IEnumerable<(string Finger, string Joint)> AllJoints()
        {
            foreach (var f in new[] { "Thumb" }.Concat(Four))
            {
                foreach (var j in Joints) yield return (f, j);
                yield return (f, "Spread");
            }
        }

        static JNode Rebuild(JNode pose, Dictionary<string, float> d)
        {
            float[]? rot = pose.Has("bodyRotation") ? pose["bodyRotation"].ToFloatArray() : null;
            float? h = pose.Has("bodyHeight") ? pose["bodyHeight"].AsFloat : (float?)null;
            return PoseValue.Make(d, rot, h);
        }

        /// <summary>쥐기: 엄지까지 모든 손가락을 편 손과 주먹 사이 g(0~1)로.</summary>
        public static JNode WithGrip(JNode pose, string side, float g, IReadOnlyDictionary<string, float> rest)
        {
            g = Math.Clamp(g, 0, 1);
            var d = PoseValue.MusclesOf(pose);
            foreach (var s in SidesOf(side))
                foreach (var (f, j) in AllJoints())
                {
                    var n = $"{s} {f} {j}";
                    float open = Open(rest, n);
                    d[n] = open + (Fist(s, f, j) - open) * g;
                }
            return Rebuild(pose, d);
        }

        /// <summary>지금 쥔 정도 (엄지 뺀 네 손가락 마디의 평균, 0 = 편 손, 1 = 주먹). 양손이면 평균.</summary>
        public static float Grip(JNode pose, string side, IReadOnlyDictionary<string, float> rest)
        {
            var d = PoseValue.MusclesOf(pose);
            float sum = 0; int n = 0;
            foreach (var s in SidesOf(side))
                foreach (var f in Four)
                    foreach (var j in Joints)
                    {
                        var name = $"{s} {f} {j}";
                        float open = Open(rest, name), fist = Fist(s, f, j);
                        if (Math.Abs(open - fist) < 1e-3f) continue;
                        sum += Math.Clamp(((d.TryGetValue(name, out var v) ? v : 0) - open) / (fist - open), 0, 1); n++;
                    }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>손 모양 프리셋 (Shapes의 Key).</summary>
        public static JNode WithShape(JNode pose, string side, string shape, IReadOnlyDictionary<string, float> rest)
        {
            var d = PoseValue.MusclesOf(pose);
            foreach (var s in SidesOf(side))
            {
                // 펴 둘 손가락 (나머지는 주먹)
                string[] open = shape switch
                {
                    "open" => new[] { "Thumb", "Index", "Middle", "Ring", "Little" },
                    "peace" => new[] { "Index", "Middle" },
                    "point" => new[] { "Index" },
                    "thumbsUp" => new[] { "Thumb" },
                    _ => Array.Empty<string>(),
                };
                foreach (var (f, j) in AllJoints())
                {
                    var n = $"{s} {f} {j}";
                    d[n] = open.Contains(f) ? Open(rest, n) : Fist(s, f, j);
                }
                if (shape == "peace") { d[$"{s} Index Spread"] = 1f; d[$"{s} Middle Spread"] = -1f; }
                if (shape == "relax")
                {
                    foreach (var f in Four)
                    {
                        d[$"{s} {f} 1 Stretched"] = 0.3f; d[$"{s} {f} 2 Stretched"] = 0.2f; d[$"{s} {f} 3 Stretched"] = 0.3f; d[$"{s} {f} Spread"] = 0f;
                    }
                    d[$"{s} Thumb 1 Stretched"] = -0.4f; d[$"{s} Thumb Spread"] = Open(rest, $"{s} Thumb Spread"); d[$"{s} Thumb 2 Stretched"] = 0.2f; d[$"{s} Thumb 3 Stretched"] = 0.3f;
                }
            }
            return Rebuild(pose, d);
        }
    }

    /// <summary>내장 자세 프리셋. 머슬 값이라 어느 아바타에도 쓸 수 있다 (적히지 않은 머슬은 0).</summary>
    public static class PosePresets
    {
        public sealed class Preset { public string Key = "", Name = ""; public Dictionary<string, float> Muscles = new Dictionary<string, float>(); public JNode Value => PoseValue.Make(Muscles, name: Name); }

        static Preset P(string key, string name, Action<Dictionary<string, float>> build)
        {
            var d = new Dictionary<string, float>();
            Stand(d);
            build(d);
            return new Preset { Key = key, Name = name, Muscles = d };
        }

        static void Both(Dictionary<string, float> d, string muscle, float v) { d["Left " + muscle] = v; d["Right " + muscle] = v; }

        // 기준 값은 휴머노이드 T 포즈(Unity가 아바타의 T 포즈를 머슬로 읽은 값)에서 잡았다:
        // 위팔 아래위 0.4 = 수평(+1 ≈ 100° 위, −1 ≈ 60° 아래), 위팔 앞뒤 0.3 = 몸 옆면, 허벅지 앞뒤 0.6·종아리 1 = 똑바로 선 다리.
        // 위팔 비틀기는 + 쪽이 바깥(굽힌 아래팔이 위로), − 쪽이 안(아래로)이다.

        /// <summary>똑바로 선 다리, 편 손가락 (모든 프리셋의 바탕).</summary>
        static void Stand(Dictionary<string, float> d)
        {
            Both(d, "Upper Leg Front-Back", 0.6f);
            Both(d, "Lower Leg Stretch", 1f);
            Both(d, "Arm Front-Back", 0.3f);
            Both(d, "Forearm Stretch", 1f);
            foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
            {
                Both(d, $"{f} 1 Stretched", 0.67f); Both(d, $"{f} 2 Stretched", 0.81f); Both(d, $"{f} 3 Stretched", 0.81f);
            }
            Both(d, "Index Spread", -0.48f); Both(d, "Middle Spread", -0.6f); Both(d, "Ring Spread", -0.64f); Both(d, "Little Spread", -0.48f);
            Both(d, "Thumb 1 Stretched", -0.73f); Both(d, "Thumb Spread", 0.5f); Both(d, "Thumb 2 Stretched", 0.64f); Both(d, "Thumb 3 Stretched", 0.64f);
        }

        /// <summary>손가락을 가볍게 굽힌 편한 손.</summary>
        static void RelaxHand(Dictionary<string, float> d, string side)
        {
            foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
            {
                d[$"{side} {f} 1 Stretched"] = 0.3f; d[$"{side} {f} 2 Stretched"] = 0.2f; d[$"{side} {f} 3 Stretched"] = 0.3f; d[$"{side} {f} Spread"] = 0f;
            }
            d[$"{side} Thumb 1 Stretched"] = -0.4f; d[$"{side} Thumb 2 Stretched"] = 0.2f; d[$"{side} Thumb 3 Stretched"] = 0.3f;
        }

        /// <summary>팔을 내린 차렷 자세 (side = "Left"/"Right").</summary>
        static void ArmDown(Dictionary<string, float> d, string side)
        {
            d[side + " Arm Down-Up"] = -0.6f; d[side + " Arm Front-Back"] = 0.35f; d[side + " Forearm Stretch"] = 0.9f;
            RelaxHand(d, side);
        }

        public static readonly IReadOnlyList<Preset> All = new[]
        {
            P("tpose", "T 포즈", d => Both(d, "Arm Down-Up", 0.4f)),
            P("apose", "A 포즈", d => Both(d, "Arm Down-Up", 0f)),
            P("attention", "차렷", d => { ArmDown(d, "Left"); ArmDown(d, "Right"); }),
            P("relaxed", "편히 서기", d =>
            {
                ArmDown(d, "Left"); ArmDown(d, "Right");
                Both(d, "Arm Down-Up", -0.5f); Both(d, "Arm Front-Back", 0.45f); Both(d, "Forearm Stretch", 0.6f);
                Both(d, "Upper Leg In-Out", 0.05f);
                d["Head Tilt Left-Right"] = 0.1f; d["Spine Left-Right"] = -0.05f;
            }),
            P("wave", "손 흔들기", d =>
            {
                ArmDown(d, "Left");
                d["Right Arm Down-Up"] = 0.45f; d["Right Arm Front-Back"] = 0.3f; d["Right Arm Twist In-Out"] = 1f; d["Right Forearm Stretch"] = 0.2f;
                d["Head Tilt Left-Right"] = -0.15f;
            }),
            P("hips", "허리에 손", d =>
            {
                Both(d, "Arm Down-Up", -0.1f); Both(d, "Arm Front-Back", 0.25f); Both(d, "Arm Twist In-Out", -1f); Both(d, "Forearm Stretch", -0.3f);
                RelaxHand(d, "Left"); RelaxHand(d, "Right");
            }),
        };

        public static Preset? Find(string key) => All.FirstOrDefault(p => p.Key == key);
    }

    /// <summary>
    /// Unity .anim(텍스트 YAML)의 float 커브. 휴머노이드 머슬 커브(classID 95, path "")에서 자세를 샘플링한다.
    /// 블렌드쉐이프 커브(blendShape.*)도 함께 읽어 둔다 (표정 프리셋용).
    /// </summary>
    public sealed class AnimClip
    {
        public struct Key { public float Time, Value, InSlope, OutSlope; }
        public sealed class Curve
        {
            public string Path = "", Attribute = ""; public int ClassId;
            public List<Key> Keys = new List<Key>();

            public float Evaluate(float t)
            {
                if (Keys.Count == 0) return 0;
                if (t <= Keys[0].Time) return Keys[0].Value;
                var last = Keys[Keys.Count - 1];
                if (t >= last.Time) return last.Value;
                int i = 1; while (Keys[i].Time < t) i++;
                var a = Keys[i - 1]; var b = Keys[i];
                float dt = b.Time - a.Time;
                if (dt <= 0) return b.Value;
                // 무한 기울기 = 계단 (Unity의 Constant 탄젠트)
                if (float.IsInfinity(a.OutSlope) || float.IsInfinity(b.InSlope)) return a.Value;
                float s = (t - a.Time) / dt, s2 = s * s, s3 = s2 * s;
                return (2 * s3 - 3 * s2 + 1) * a.Value + (s3 - 2 * s2 + s) * a.OutSlope * dt + (-2 * s3 + 3 * s2) * b.Value + (s3 - s2) * b.InSlope * dt;
            }
        }

        public string Name = "";
        public readonly List<Curve> Curves = new List<Curve>();
        public float Length => Curves.Count == 0 ? 0 : Curves.Max(c => c.Keys.Count == 0 ? 0 : c.Keys[c.Keys.Count - 1].Time);
        public IEnumerable<Curve> MuscleCurves => Curves.Where(c => c.Path.Length == 0 && Muscles.FromAnimAttribute(c.Attribute) != null);
        public bool IsHumanoid => MuscleCurves.Any();

        public static AnimClip Load(string path)
        {
            var text = File.ReadAllText(path);
            if (!text.StartsWith("%YAML")) throw new InvalidDataException("텍스트(YAML) .anim이 아니에요 (바이너리로 저장된 애니메이션은 읽지 못해요)");
            var clip = Parse(text);
            if (clip.Name.Length == 0) clip.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            return clip;
        }

        /// <summary>m_FloatCurves만 읽는다 (m_EditorCurves는 같은 내용의 편집기용 사본).</summary>
        public static AnimClip Parse(string text)
        {
            var clip = new AnimClip();
            var lines = text.Replace("\r", "").Split('\n');
            bool inFloat = false;
            Curve? cur = null; Key k = default; bool inKey = false;
            void EndKey() { if (inKey && cur != null) cur.Keys.Add(k); inKey = false; k = default; }
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                var t = line.TrimStart();
                int indent = line.Length - t.Length;
                if (indent == 2 && !t.StartsWith("- "))
                {
                    // 최상위 필드 (AnimationClip 바로 아래)
                    EndKey();
                    if (t.StartsWith("m_Name:")) clip.Name = t.Substring(7).Trim();
                    inFloat = t == "m_FloatCurves:";
                    cur = null;
                    continue;
                }
                if (!inFloat) continue;
                // 항목 시작: 예전 형식은 "- curve:", 새 형식은 "- serializedVersion: 2" 다음 줄에 "curve:"
                if (indent == 2 && t.StartsWith("- ")) { EndKey(); cur = new Curve(); continue; }
                if (cur == null) continue;
                if (t.StartsWith("- serializedVersion:") && indent >= 6) { EndKey(); inKey = true; continue; }
                int colon = t.IndexOf(':');
                if (colon < 0) continue;
                var key = t.Substring(0, colon).TrimStart('-', ' '); var val = t.Substring(colon + 1).Trim();
                if (inKey)
                {
                    switch (key)
                    {
                        case "time": k.Time = Num(val); break;
                        case "value": k.Value = Num(val); break;
                        case "inSlope": k.InSlope = Num(val); break;
                        case "outSlope": k.OutSlope = Num(val); break;
                        case "m_PreInfinity": case "m_PostInfinity": case "m_RotationOrder": EndKey(); break;
                    }
                }
                if (indent == 4)
                {
                    EndKey();
                    switch (key)
                    {
                        case "attribute": cur.Attribute = val; break;
                        case "path": cur.Path = val; break;
                        case "classID": cur.ClassId = (int)Num(val); clip.Curves.Add(cur); break;
                    }
                }
            }
            EndKey();
            return clip;
        }

        static float Num(string s)
        {
            if (s == "Infinity") return float.PositiveInfinity;
            if (s == "-Infinity") return float.NegativeInfinity;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        /// <summary>시간 t의 자세. RootQ가 있으면 몸 방향, RootT.y가 있으면 몸 높이(휴머노이드 단위)도 넣는다. RootT의 x·z(이동)는 쓰지 않는다.</summary>
        public JNode SamplePose(float t)
        {
            var d = new Dictionary<string, float>();
            foreach (var c in MuscleCurves) d[Muscles.FromAnimAttribute(c.Attribute)!] = c.Evaluate(t);
            float[]? rot = null;
            var q = new[] { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" }.Select(a => Curves.FirstOrDefault(c => c.Path.Length == 0 && c.Attribute == a)).ToArray();
            if (q.All(c => c != null))
            {
                rot = q.Select(c => c!.Evaluate(t)).ToArray();
                float len = (float)Math.Sqrt(rot.Sum(x => x * x));
                if (len > 1e-4f) rot = rot.Select(x => x / len).ToArray(); else rot = null;
            }
            var ty = Curves.FirstOrDefault(c => c.Path.Length == 0 && c.Attribute == "RootT.y");
            return PoseValue.Make(d, rot, ty?.Evaluate(t), t > 0 ? $"{Name} ({t.ToString("0.##", CultureInfo.InvariantCulture)}초)" : Name);
        }
    }

    /// <summary>VR 모션 녹화: %APPDATA%/ALEdit/motions/<이름>.anim (휴머노이드 .anim — 자세 페이지 "애니메이션"에서 다시 불러온다).</summary>
    public static class MotionLibrary
    {
        public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", "motions");

        /// <summary>vrMotion { fps, muscles, frames } → .anim 파일. 이름은 "VR 녹화 날짜 시각" (같으면 뒤에 번호). 저장한 경로를 돌려준다.</summary>
        public static string Save(JNode motion, string? folder = null, DateTime? now = null)
        {
            folder ??= Folder;
            Directory.CreateDirectory(folder);
            var names = motion["muscles"].Items.Select(n => n.AsString).ToList();
            var frames = motion["frames"].Items.Select(f => f.ToFloatArray()).ToList();
            float fps = motion["fps"].AsFloat > 0 ? motion["fps"].AsFloat : 30;
            var baseName = "VR 녹화 " + (now ?? DateTime.Now).ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            var name = baseName;
            for (int i = 2; File.Exists(System.IO.Path.Combine(folder, name + ".anim")); i++) name = $"{baseName} ({i})";
            var path = System.IO.Path.Combine(folder, name + ".anim");
            AnimWriter.Save(path, name, AnimWriter.FromMotion(names, frames, fps));
            return path;
        }
    }

    /// <summary>저장한 자세: %APPDATA%/ALEdit/poses/<이름>.pose.json = { name, pose }.</summary>
    public static class PoseLibrary
    {
        public const string Extension = ".pose.json";
        public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", "poses");

        public static List<(string Name, string Path)> List()
        {
            if (!Directory.Exists(Folder)) return new List<(string, string)>();
            return Directory.GetFiles(Folder, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => (System.IO.Path.GetFileName(p).Substring(0, System.IO.Path.GetFileName(p).Length - Extension.Length), p)).ToList();
        }

        public static string Save(string name, JNode pose, string? folder = null)
        {
            folder ??= Folder;
            Directory.CreateDirectory(folder);
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = System.IO.Path.Combine(folder, name + Extension);
            File.WriteAllText(path, JNode.Obj().Set("name", name).Set("pose", PoseValue.Named(pose, name)).ToJson());
            return path;
        }

        public static JNode Load(string path)
        {
            var n = JNode.Parse(File.ReadAllText(path));
            var pose = n["pose"];
            if (!pose.Has("muscles")) throw new InvalidDataException("자세 파일이 아니에요");
            return pose;
        }
    }
}
