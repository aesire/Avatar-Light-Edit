// 아이템(의상·헤어·악세서리) 붙이기의 재료와 다시 만들기.
// 가져오기가 IR source에 남긴 재료(sources · guid · root · outfits)로 아바타를 다시 해석하고, 아이템 목록을 바꿔 IR을 새로 쓴다.
// 원본 프로젝트·패키지는 읽기만 한다. 결과는 %LOCALAPPDATA%\ALEdit\imports\ 아래 (지금 IR과 같은 폴더).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ImportSim;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class Recipe
    {
        public List<string> Sources = new List<string>();
        public string Guid = "";
        public long Root;
        public bool IncludeColorVariants;
        public List<OutfitMerge.Spec> Items = new List<OutfitMerge.Spec>();

        /// <summary>IR에 다시 만들 재료가 없으면 null (예전 가져오기, 샘플 IR)</summary>
        public static Recipe? From(IrScene s) => FromSource(s.Raw["source"]);

        /// <summary>IR의 source (또는 세션에 적어 둔 것)에서. 재료가 없으면 null</summary>
        public static Recipe? FromSource(JNode src)
        {
            if (src.IsNull) return null;
            if (!src.Has("sources") || !src.Has("root") || src["sources"].Count == 0) return null;
            var r = new Recipe { Guid = src["guid"].AsString, Root = long.TryParse(src["root"].AsString, out var id) ? id : 0 };
            r.IncludeColorVariants = src.Has("includeColorVariants") && src["includeColorVariants"].AsBool;
            foreach (var x in src["sources"].Items) r.Sources.Add(x.AsString);
            foreach (var x in src["outfits"].Items) r.Items.Add(UnityImport.SpecFrom(x));
            return r.Root == 0 ? null : r;
        }

        public Recipe With(IEnumerable<string> moreSources, IEnumerable<OutfitMerge.Spec> items)
        {
            var srcs = new List<string>(Sources);
            foreach (var s in moreSources) if (!srcs.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) srcs.Add(s);
            return new Recipe { Sources = srcs, Guid = Guid, Root = Root, Items = items.ToList(), IncludeColorVariants = IncludeColorVariants };
        }

        public List<string> MissingSources() => Sources.Where(s => !File.Exists(s) && !Directory.Exists(s)).ToList();
    }

    /// <summary>
    /// BOOTH 배포 zip 안의 .unitypackage를 꺼낸다. 꺼낸 파일은 %LOCALAPPDATA%\ALEdit\packages\&lt;zip 이름&gt;_&lt;해시&gt;\ 에 두고
    /// 가져오기 재료(IR source)는 그 경로를 가리킨다 (zip을 지워도 다시 만들기가 된다). zip의 다른 파일은 꺼내지 않는다.
    /// </summary>
    public static class Packages
    {
        public static bool IsPackage(string p) => p.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "packages");

        /// <summary>zip은 안의 .unitypackage들로 바꾼다. 패키지가 없는 zip은 notes에 적고 뺀다.</summary>
        public static string[] Expand(IEnumerable<string> files, List<string> notes)
        {
            var list = new List<string>();
            foreach (var f in files)
            {
                if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { list.Add(f); continue; }
                var got = Unzip(f);
                if (got.Count == 0) notes.Add($"{Path.GetFileName(f)}: 안에 .unitypackage가 없어요");
                list.AddRange(got);
            }
            return list.ToArray();
        }

        static List<string> Unzip(string zip)
        {
            var info = new FileInfo(zip);
            var key = Fnv(info.FullName.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            var dir = Path.Combine(CacheDir, Safe(Path.GetFileNameWithoutExtension(zip)) + "_" + key);
            var outList = new List<string>();
            using var z = ZipFile.OpenRead(zip);
            foreach (var e in z.Entries)
            {
                if (!e.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) || e.Length == 0) continue;
                // 경로는 버리고 파일 이름만 (zip 안의 ../ 경로가 밖으로 나가지 않게)
                var dst = Path.Combine(dir, Safe(Path.GetFileName(e.FullName.Replace('\\', '/'))));
                if (!File.Exists(dst) || new FileInfo(dst).Length != e.Length)
                {
                    Directory.CreateDirectory(dir);
                    var tmp = dst + ".part";
                    e.ExtractToFile(tmp, true);
                    File.Move(tmp, dst, true);
                }
                outList.Add(dst);
            }
            return outList;
        }

        static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "package" : s.Trim();
        }

        static string Fnv(string s)
        {
            ulong h = 1469598103934665603UL;   // FNV-1a 64 (캐시 폴더 이름용)
            foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("x16").Substring(0, 8);
        }
    }

    public static class Items
    {
        static string? _key;
        static UnityImport? _imp;

        /// <summary>같은 소스 조합이면 색인을 다시 하지 않는다 (큰 프로젝트·패키지는 수십 초 걸린다). 한 번에 하나만 쓴다 (스레드 하나에서).</summary>
        public static UnityImport Open(IEnumerable<string> sources)
        {
            var list = sources.ToList();
            var key = string.Join("|", list).ToLowerInvariant();
            if (_imp != null && _key == key) return _imp;
            _imp = null;
            _imp = UnityImport.Open(list);
            _key = key;
            return _imp;
        }

        /// <summary>재료대로 IR을 다시 쓴다. 반환: 새 IR 경로, 경고, 아이템 보고.</summary>
        public static (string Path, List<string> Warnings, List<OutfitMerge.Report> Reports) Build(Recipe r, string outDir)
        {
            ImportSim.Timing.Start();
            var imp = Open(r.Sources);
            var f = imp.ResolveGuid(r.Guid);
            return imp.Export(f, r.Root, outDir, r.Items, r.IncludeColorVariants);
        }

        public static string KindLabel(string kind) => kind switch
        {
            "outfit" => "의상", "hair" => "헤어", "accessory" => "악세서리", "gimmick" => "기믹", _ => "모름",
        };

        public static string MethodLabel(string method) => method switch
        {
            "ma-merge" => "뼈대 합침 (MA)", "ma-proxy" => "뼈에 붙음 (MA)", "name" => "본 이름으로 맞춤", "manual" => "직접 고른 곳", _ => "붙이지 않음",
        };

        /// <summary>툴팁용 원래 이름 (MA 컴포넌트 이름 그대로)</summary>
        public static string MethodFull(string method) => method switch
        {
            "ma-merge" => "Modular Avatar Merge Armature", "ma-proxy" => "Modular Avatar Bone Proxy", _ => MethodLabel(method),
        };

        /// <summary>이 아바타용인지 가늠한 한 줄 (짝 찾은 본·위치 차이)</summary>
        public static string FitText(int matched, int bones, double offsetMean, out bool bad)
        {
            // 3 cm: 아바타에 맞춰 루트를 옮기고 키운 헤어(아바타 A 전용 헤어, 머리 1.2 cm)는 넘기고, 다른 아바타 의상(아바타 B → 아바타 A, 10 cm)은 잡는다
            bad = matched > 0 && offsetMean > 0.03;
            if (matched == 0) return "";
            var s = $"본 {matched}개 맞춤";
            if (bad) s += $" · 평균 {offsetMean * 100:0.0} cm 어긋남 — 다른 아바타용 같습니다";
            return s;
        }

        static readonly Dictionary<string, string> BoneNames = new Dictionary<string, string>
        {
            ["Head"] = "머리", ["Neck"] = "목", ["Jaw"] = "턱", ["LeftEye"] = "왼눈", ["RightEye"] = "오른눈",
            ["UpperChest"] = "윗가슴", ["Chest"] = "가슴", ["Spine"] = "허리", ["Hips"] = "엉덩이 (Hips)",
            ["LeftShoulder"] = "왼어깨", ["RightShoulder"] = "오른어깨", ["LeftUpperArm"] = "왼팔 위", ["RightUpperArm"] = "오른팔 위",
            ["LeftLowerArm"] = "왼팔 아래", ["RightLowerArm"] = "오른팔 아래", ["LeftHand"] = "왼손", ["RightHand"] = "오른손",
            ["LeftUpperLeg"] = "왼다리 위", ["RightUpperLeg"] = "오른다리 위", ["LeftLowerLeg"] = "왼다리 아래", ["RightLowerLeg"] = "오른다리 아래",
            ["LeftFoot"] = "왼발", ["RightFoot"] = "오른발", ["LeftToes"] = "왼발가락", ["RightToes"] = "오른발가락",
        };

        /// <summary>붙일 곳 고르기 목록: 많이 쓰는 뼈부터 (손가락은 뒤에)</summary>
        public static List<(string Id, string Label)> BoneChoices(IEnumerable<string> humanBones)
        {
            var have = new HashSet<string>(humanBones);
            var list = new List<(string, string)>();
            foreach (var kv in BoneNames) if (have.Contains(kv.Key)) list.Add((kv.Key, kv.Value));
            foreach (var b in Humanoid.BodyBones) if (have.Contains(b) && !BoneNames.ContainsKey(b)) list.Add((b, b));
            list.Add(("@root", "아바타 루트 (붙이지 않고 만들어진 위치 그대로)"));
            return list;
        }

        public static string BoneLabel(string id)
        {
            if (id == "@root") return "아바타 루트";
            if (BoneNames.TryGetValue(id, out var s)) return s;
            // 손가락: "Right Middle Proximal" / "RightMiddleProximal" → "오른손 중지 1마디"
            var m = FingerId.Match(id);
            if (m.Success)
                return (m.Groups[1].Value == "Left" ? "왼손 " : "오른손 ") + Fingers[m.Groups[2].Value] + " " + Joints[m.Groups[3].Value];
            return id;
        }

        static readonly System.Text.RegularExpressions.Regex FingerId = new System.Text.RegularExpressions.Regex(@"^(Left|Right)\s*(Thumb|Index|Middle|Ring|Little)\s*(Proximal|Intermediate|Distal)$");
        static readonly Dictionary<string, string> Fingers = new Dictionary<string, string> { ["Thumb"] = "엄지", ["Index"] = "검지", ["Middle"] = "중지", ["Ring"] = "약지", ["Little"] = "새끼" };
        static readonly Dictionary<string, string> Joints = new Dictionary<string, string> { ["Proximal"] = "1마디", ["Intermediate"] = "2마디", ["Distal"] = "3마디" };
    }
}
