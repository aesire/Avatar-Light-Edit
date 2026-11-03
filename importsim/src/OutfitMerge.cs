// 의상·헤어·악세서리 붙이기: 아바타 아래에 들어간 아이템을 Modular Avatar가 빌드할 때처럼 아바타 본에 붙인다.
// MA 컴포넌트는 스크립트 코드 없이 직렬화된 필드 이름으로 알아본다 (VRChat SDK 컴포넌트와 같은 방식):
//   Merge Armature = mergeTarget{referencePath, targetObject} + prefix + suffix
//   Bone Proxy     = boneReference(HumanBodyBones 번호) + subPath (+ target, attachmentMode)
// 본 짝 찾기 (부모부터 차례로, 대상 본의 자식 중에서):
//   MA Merge Armature: 의상 본 이름에서 prefix·suffix를 뗀 이름이 같은 것 (MA와 같은 규칙)
//   MA 설정이 없는 아이템: 이름이 같은 것 → 대소문자·기호를 뺀 이름이 같은 것 → 휴머노이드 뼈 이름(양쪽 FBX .meta)이 같은 것
// 짝을 찾은 본:
//   위치·회전·크기가 같고 다른 컴포넌트가 없는 본: 의상 본을 없애고, 그 본을 가리키던 참조(스키닝 본, PhysBone 등)를 아바타 본으로 바꾼다
//   어긋난 본(또는 컴포넌트가 붙은 본): 아바타 본의 자식으로 옮긴다 (월드 위치 유지) → 아바타 본을 따라 움직인다
//   짝이 없는 본(의상 전용 본): 부모와 함께 옮겨진다
// MA 설정이 없는 아이템(Resolver.Compose로 넣은 것)은 Spec에 따라:
//   auto: 아바타 Hips와 짝이 되는 본이 있으면 거기서부터 합치고(의상), 없으면 Head(헤어). 둘 다 없으면 "어디에 붙일지 모름"으로 남긴다
//   수동(kind + target): target 뼈와 짝이 되는 본이 있으면 거기서부터 합치고, 없으면 아이템 루트를 target 아래로 옮긴다 (Bone Proxy처럼)
// 종류(의상·헤어·악세서리)는 아이템의 스킨드 메시가 결국 어떤 아바타 뼈를 따라가는지와 이름으로 가늠한다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImportSim
{
    public static class OutfitMerge
    {
        /// <summary>넣을 아이템 하나와 붙이는 방법 (가져오기 재료로 IR source.outfits에 남는다).</summary>
        public sealed class Spec
        {
            public string Prefab = "";
            public string Kind = "auto";     // auto · outfit · hair · accessory
            public string Target = "";       // 휴머노이드 뼈 이름(Head 등) 또는 아바타 루트 기준 경로. 빈 값 = 종류별 기본 (의상 Hips, 헤어·악세서리 Head), "@root" = 아바타 루트에 그대로
            public bool KeepWorld = true;    // 루트를 옮길 때 지금 위치 유지 (false = 뼈 위치로)
            /// <summary>붙일 뼈 기준 로컬 위치(3)·회전(4, xyzw)·크기(3). 있으면 위 두 방법 대신 이 값으로 둔다 (에디터에서 커서로 놓은 자리).</summary>
            public double[]? Offset;
            /// <summary>아이템 전체 크기 배율. 아이템 루트와 붙인 본·Bone Proxy 부품에 함께 적용한다.</summary>
            public double Scale = 1;
        }

        public sealed class Report
        {
            public long OutfitRoot;          // 아바타 루트 바로 아래의 아이템 오브젝트 (GameObject fileID)
            public string AddedFrom = "";    // Resolver.Compose 로 넣은 프리팹 guid (씬에 원래 있던 것이면 "")
            public string Method = "none";   // ma-merge · ma-proxy · name · manual · none
            public bool MergeArmature => Method == "ma-merge";
            public string Kind = "unknown";  // outfit · hair · accessory · gimmick · unknown
            public string Reason = "";       // 종류를 그렇게 본 까닭 (사용자에게 보여 준다)
            public bool NeedsPlacement;      // 어디에 붙일지 정하지 못함 (아바타 루트에 그대로 있다)
            public bool NearOrigin;          // 붙이지 못한 아이템의 메시가 원점(발밑) 근처에 있다 → 뼈 위치로 옮기는 편이 맞다
            public string Target = "";       // 붙인 아바타 뼈 (경로 또는 휴머노이드 이름)
            public int Bones, Matched, Merged;   // 의상 본 수 · 짝을 찾은 본 · 완전히 합친 본
            public double MeanOffset, MaxOffset; // 짝을 찾은 본의 위치 차이 (월드 단위, m)
            public HashSet<long> Attached = new HashSet<long>();   // 아바타 본의 자식으로 옮긴 의상 본 (Transform fileID)
            // Bone Proxy로 아이템 밖(아바타 뼈 아래)으로 옮긴 오브젝트 (GameObject fileID). 아이템을 켜고 끄거나 종류를 가늠할 때 아이템의 일부로 본다
            public List<long> Moved = new List<long>();
            // 아바타 머리 뼈 월드 크기 ÷ 아이템에서 그 뼈와 짝지은 뼈의 월드 크기 (짝지은 머리 뼈가 없으면 0).
            // 아바타 머리 크기를 바꿔 둔 아바타에 원래 크기용 헤어를 붙이면 1이 아니다 → 에디터가 아이템 크기를 이만큼 맞출 수 있다
            public double HeadScale;
            /// <summary>Spec 또는 자동 맞춤으로 적용한 아이템 크기 배율.</summary>
            public double Scale = 1;
            // 아이템 크기를 바꿀 때 함께 키울 오브젝트 (GameObject fileID → 원래 프리팹 안 경로, 아이템 루트 기준이고 루트는 ""):
            // 아이템 루트 · 아바타 뼈 아래로 옮긴 아이템 본 · Bone Proxy로 옮긴 오브젝트. 아바타 뼈와 완전히 합친 본은 없어져 빠진다
            public Dictionary<long, string> ScaleRoots = new Dictionary<long, string>();
            internal List<double> Offsets = new List<double>();
        }

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static readonly string[] HairWords = { "hair", "wig", "bang", "ponytail", "twintail", "ヘア", "髪", "ウィッグ", "前髪", "後ろ髪", "헤어", "머리카락", "가발" };
        static readonly string[] AccWords = { "acc", "ring", "earring", "pierce", "hat", "cap", "glasses", "megane", "necklace", "choker", "halo", "ribbon", "bag", "tail", "ears", "horn", "wing", "crown", "mask",
                                              "アクセ", "ピアス", "イヤリング", "帽子", "メガネ", "眼鏡", "ネックレス", "チョーカー", "リボン", "しっぽ", "尻尾", "耳", "角", "羽", "악세", "귀걸이", "모자", "안경", "목걸이", "리본", "꼬리", "날개" };
        static readonly string[] GimmickWords = { "system", "gimmick", "ギミック", "システム", "기믹", "시스템", "posesystem", "toy", "meter" };
        static readonly string[] OutfitWords = { "outfit", "cloth", "costume", "dress", "shirt", "skirt", "pants", "jacket", "hoodie", "coat", "shoes", "衣装", "服", "의상", "옷" };

        sealed class Ctx
        {
            public SimFile F = null!; public AssetDb? Db;
            public long RootTr;
            public Dictionary<long, List<long>> Kids = new Dictionary<long, List<long>>();
            public Dictionary<long, Mat4> World = new Dictionary<long, Mat4>();
            public HashSet<long> Under = new HashSet<long>();
            public Dictionary<long, long> Remap = new Dictionary<long, long>();
            public HashSet<long> Delete = new HashSet<long>();
            public HashSet<long> ItemTrs = new HashSet<long>();          // 아이템 쪽 Transform (아바타 뼈 짝 후보에서 뺀다)
            public Dictionary<string, long> Human = new Dictionary<string, long>();   // 아바타 휴머노이드 이름 → Transform
            public Dictionary<long, string> HumanOf = new Dictionary<long, string>();
            public Dictionary<long, long> Parent = new Dictionary<long, long>();     // 붙이기 전 부모 (원래 경로를 적을 때)
        }

        /// <summary>avatarRoot 아래의 아이템을 붙인다. f를 바꾼다 (Resolver 캐시가 아닌 Compose 결과에만 쓴다). specs: Compose로 넣은 프리팹 guid → 붙이는 방법.</summary>
        public static List<Report> Apply(SimFile f, SimObject avatarRoot, AssetDb? db = null, IEnumerable<Spec>? specs = null)
        {
            var reports = new List<Report>();
            var c = new Ctx { F = f, Db = db, RootTr = Tr(f, avatarRoot) };
            if (c.RootTr == 0) return reports;
            var specOf = new Dictionary<string, Spec>();
            foreach (var s in specs ?? Enumerable.Empty<Spec>()) specOf[s.Prefab] = s;

            // 원래 계층·월드 행렬을 먼저 찍어 둔다 (붙이는 동안 바뀌어도 짝 찾기·위치 계산은 이것으로)
            void Walk(long tr, Mat4 parent)
            {
                c.Under.Add(tr);
                var o = f.Get(tr)!;
                var w = parent * Local(o.Props);
                c.World[tr] = w;
                var list = new List<long>();
                foreach (var ch in L(o.Props, "m_Children")) { var id = Ref.FileId(ch); if (f.Get(id) != null) { list.Add(id); c.Parent[id] = tr; } }
                c.Kids[tr] = list;
                foreach (var ch in list) Walk(ch, w);
            }
            Walk(c.RootTr, Mat4.Identity());
            if (db != null) c.Human = Humanoid.AvatarBones(db, f, avatarRoot);
            foreach (var kv in c.Human) c.HumanOf[kv.Value] = kv.Key;
            Timing.Step("본 합치기: 계층·휴머노이드");

            var items = c.Kids[c.RootTr].Where(t => IsItem(c, t)).ToList();
            foreach (var t in items) Collect(c, t, c.ItemTrs);
            var byItem = new Dictionary<long, Report>();
            Report ReportFor(long itemTr)
            {
                if (byItem.TryGetValue(itemTr, out var r)) return r;
                var go = Ref.Local(f.Get(itemTr)!.Props, "m_GameObject");
                r = new Report { OutfitRoot = go, AddedFrom = f.AddedFrom.TryGetValue(go, out var g) ? g : "" };
                byItem[itemTr] = r; reports.Add(r);
                return r;
            }

            // 1) MA 설정
            foreach (var o in f.Objects.Values.ToList())
            {
                if (o.ClassId != 114) continue;
                var go = f.Get(Ref.Local(o.Props, "m_GameObject"));
                long xTr = go == null ? 0 : Tr(f, go);
                if (xTr == 0 || !c.Under.Contains(xTr) || !c.ItemTrs.Contains(xTr)) continue;
                var item = TopUnder(f, xTr, c.RootTr);
                if (o.Props.GetValueOrDefault("mergeTarget") is Map mt && o.Props.ContainsKey("prefix") && o.Props.ContainsKey("suffix"))
                {
                    var rep = ReportFor(item);
                    var path = S(mt, "referencePath");
                    long tTr = Ref.Local(mt, "targetObject") is long tg && tg != 0 && f.Get(tg) is SimObject tgo ? Tr(f, tgo) : Find(f, c.RootTr, path);
                    if (tTr == 0 || c.ItemTrs.Contains(tTr)) { f.Warnings.Add($"Merge Armature 대상을 찾지 못함: {Name(f, xTr)} → \"{path}\""); continue; }
                    rep.Method = "ma-merge"; rep.Target = path;
                    string prefix = S(o.Props, "prefix"), suffix = S(o.Props, "suffix");
                    Pair(c, xTr, tTr, rep, root: true);
                    MergeChildren(c, xTr, tTr, rep, (x, cands) =>
                    {
                        var n = Name(f, x);
                        if (n.Length < prefix.Length + suffix.Length || !n.StartsWith(prefix, StringComparison.Ordinal) || !n.EndsWith(suffix, StringComparison.Ordinal)) return 0;
                        var bn = n.Substring(prefix.Length, n.Length - prefix.Length - suffix.Length);
                        return cands.FirstOrDefault(k => Name(f, k) == bn);
                    });
                }
                else if (o.Props.ContainsKey("boneReference") && o.Props.ContainsKey("subPath"))
                {
                    var rep = ReportFor(item);
                    long tTr = 0;
                    if (int.TryParse(S(o.Props, "boneReference"), NumberStyles.Integer, C, out var hb) && hb >= 0 && hb < Humanoid.BodyBones.Length && c.Human.TryGetValue(Humanoid.BodyBones[hb], out var hTr))
                    {
                        var sub = S(o.Props, "subPath");
                        tTr = sub.Length == 0 ? hTr : FindUnder(f, hTr, sub);
                        rep.Target = Humanoid.BodyBones[hb] + (sub.Length > 0 ? "/" + sub : "");
                    }
                    else if (Ref.Local(o.Props, "target") is long tid && tid != 0 && f.Get(tid) is SimObject tobj) { tTr = tobj.ClassId == 4 ? tobj.Id : Tr(f, tobj); rep.Target = Name(f, tTr); }
                    if (tTr == 0 || c.ItemTrs.Contains(tTr)) { f.Warnings.Add($"Bone Proxy 대상을 찾지 못함: {Name(f, xTr)}"); continue; }
                    if (rep.Method == "none") rep.Method = "ma-proxy";
                    // attachmentMode: 1 뼈 위치로, 그 밖(0 기본·2 월드 유지·3 위치 유지·4 회전 유지)은 지금 위치 유지로 둔다
                    var mode = S(o.Props, "attachmentMode");
                    if (mode == "1") SetParentAtRoot(f, xTr, tTr); else SetParent(c, xTr, tTr);
                    if (xTr != item) rep.Moved.Add(go!.Id);
                }
            }

            Timing.Step("본 합치기: MA 설정");
            // 2) MA 설정이 없고 Compose로 넣은 아이템: Spec대로
            foreach (var item in items)
            {
                var rgo = Ref.Local(f.Get(item)!.Props, "m_GameObject");
                if (!f.AddedFrom.TryGetValue(rgo, out var guid)) continue;
                if (byItem.ContainsKey(item))
                {
                    if (specOf.TryGetValue(guid, out var configured)) byItem[item].Scale = ValidScale(configured.Scale);
                    continue;
                }
                var rep = ReportFor(item);
                specOf.TryGetValue(guid, out var spec);
                spec ??= new Spec { Prefab = guid };
                rep.Scale = ValidScale(spec.Scale);
                var outfitHuman = OutfitHuman(c, item);
                if (spec.Kind == "auto")
                {
                    foreach (var anchor in new[] { "Hips", "Head" })
                        if (c.Human.TryGetValue(anchor, out var aTr) || (aTr = FindByName(c, anchor)) != 0)
                            if (Anchor(c, item, aTr, outfitHuman) is long x && x != 0)
                            {
                                rep.Method = "name"; rep.Target = anchor;
                                Pair(c, x, aTr, rep, root: false);
                                MergeChildren(c, x, aTr, rep, (b, cands) => Relaxed(c, b, cands, outfitHuman));
                                break;
                            }
                    if (rep.Method == "none") rep.NeedsPlacement = true;
                }
                else
                {
                    var target = spec.Target.Length > 0 ? spec.Target : spec.Kind == "outfit" ? "Hips" : "Head";
                    rep.Method = "manual"; rep.Target = target; rep.Kind = spec.Kind;
                    if (target == "@root") continue;   // 붙이지 않고 아바타 루트 아래 그대로 (만들어진 위치)
                    long tTr = c.Human.TryGetValue(target, out var h) ? h : Find(f, c.RootTr, target);
                    if (tTr == 0) tTr = FindByName(c, target);
                    if (tTr == 0) { f.Warnings.Add($"{Name(f, item)}: 붙일 뼈 \"{target}\"를 찾지 못함"); rep.NeedsPlacement = true; continue; }
                    if (spec.Offset != null && spec.Offset.Length >= 10)
                    {
                        var o = spec.Offset;
                        f.Get(item)!.Props["m_Father"] = Ref.Make(tTr);
                        SetLocal(f.Get(item)!, new Vec3(o[0], o[1], o[2]), new Quat(o[3], o[4], o[5], o[6]), new Vec3(o[7], o[8], o[9]));
                    }
                    else if (Anchor(c, item, tTr, outfitHuman) is long x && x != 0)
                    {
                        Pair(c, x, tTr, rep, root: false);
                        MergeChildren(c, x, tTr, rep, (b, cands) => Relaxed(c, b, cands, outfitHuman));
                    }
                    else if (spec.KeepWorld) SetParent(c, item, tTr);
                    else MoveToBone(c, item, tTr);
                }
            }

            Timing.Step("본 합치기: 직접 붙인 아이템");
            foreach (var kv in byItem)
            {
                ScaleRootsOf(c, kv.Key, kv.Value);
                ApplyScale(c, kv.Value);
            }
            if (c.Remap.Count > 0)
                foreach (var o in f.Objects.Values.ToList())
                    o.Props = (Map)Yaml.DeepCopy(o.Props, id => c.Remap.TryGetValue(id, out var r) ? r : id)!;
            foreach (var id in c.Delete) f.Objects.Remove(id);
            if (reports.Count > 0) Resolver.Relink(f);
            Timing.Step("본 합치기: 참조 고치기");
            // 종류 가늠은 아이템 메시의 FBX 기하를 읽는다: 아바타 전체의 FBX를 한꺼번에 병렬로 해석해 둔다
            // (IR 쓰기가 아바타 몸 FBX까지 같은 것을 다시 쓰므로, 여기서 한 번에 하면 가장 큰 FBX 하나만큼만 기다린다)
            if (db != null && reports.Count > 0)
            {
                var fbx = new HashSet<string>();
                foreach (var go in Humanoid.Walk(f, avatarRoot))
                    foreach (var comp in Humanoid.Comps(f, go))
                        if ((comp.ClassId == 137 || comp.ClassId == 33) && Ref.Guid(comp.Props.GetValueOrDefault("m_Mesh")) is string g && db.Get(g)?.Ext == ".fbx") fbx.Add(g);
                System.Threading.Tasks.Parallel.ForEach(fbx, g => db.Geometry(db.Get(g)!));
                Timing.Step("본 합치기: FBX 해석 (병렬)");
            }
            foreach (var r in reports)
            {
                if (r.Offsets.Count > 0) { r.MeanOffset = r.Offsets.Average(); r.MaxOffset = r.Offsets.Max(); }
                Classify(c, r);
            }
            return reports;
        }

        // ---------------- 종류 가늠 ----------------

        /// 처음에 보이는 오브젝트인지: 자기와 조상(아바타 루트까지)이 모두 켜져 있다
        static bool ShownByDefault(SimFile f, SimObject go)
        {
            for (SimObject? g = go; g != null; )
            {
                if (S(g.Props, "m_IsActive") == "0") return false;
                var tr = Tr(f, g);
                var father = tr == 0 ? 0 : Ref.Local(f.Get(tr)?.Props ?? new Map(), "m_Father");
                g = father == 0 ? null : f.Get(Ref.Local(f.Get(father)!.Props, "m_GameObject"));
            }
            return true;
        }

        /// 아이템의 스킨드 메시가 결국 따라가는 아바타 뼈로 종류를 가늠한다. 수동으로 정한 종류는 그대로 둔다
        static void Classify(Ctx c, Report r)
        {
            var f = c.F;
            var root = f.Get(r.OutfitRoot);
            if (root == null) return;
            // 오브젝트 이름을 먼저 보고, 단서가 없을 때만 프리팹 경로를 본다 (헤어 패키지의 "…/Point ribbon hair/…/Earring_.prefab"이 헤어로 잡히지 않게)
            static string? Hint(string s) => HairWords.Any(w => s.Contains(w.ToLowerInvariant())) ? "hair" : OutfitWords.Any(w => s.Contains(w.ToLowerInvariant())) ? "outfit" : AccWords.Any(w => s.Contains(w.ToLowerInvariant())) ? "accessory" : null;
            string? hint = Hint(S(root.Props, "m_Name").ToLowerInvariant())
                           ?? Hint((r.AddedFrom.Length > 0 && c.Db?.Get(r.AddedFrom) is AssetEntry a ? a.Path : "").ToLowerInvariant());
            if (r.Method == "manual") { r.Reason = "직접 고름"; return; }

            // 메시가 따라가는 아바타 뼈 (아이템 본이면 아바타 쪽으로 올라간다)
            // 스킨드 메시는 가중치가 실린 본만 센다 (헤어 FBX는 m_Bones에 Hips·Spine을 가중치 0으로 넣어 두는 일이 많다)
            var used = new HashSet<long>();
            int skinned = 0, rigid = 0, shown = 0;
            var walk = Humanoid.Walk(f, root).Concat(r.Moved.Select(id => f.Get(id)).OfType<SimObject>().SelectMany(m => Humanoid.Walk(f, m))).Distinct().ToList();
            foreach (var go in walk)
                foreach (var comp in Humanoid.Comps(f, go))
                {
                    if (comp.ClassId != 23 && comp.ClassId != 137) continue;
                    if (ShownByDefault(f, go)) shown++;
                    if (comp.ClassId == 23) { rigid++; used.Add(AvatarAncestor(c, Tr(f, go))); continue; }
                    skinned++;
                    var bones = L(comp.Props, "m_Bones");
                    var weighted = WeightedBones(c, comp.Props.GetValueOrDefault("m_Mesh"));
                    for (int i = 0; i < bones.Count; i++)
                    {
                        if (weighted != null && !weighted.Contains(i)) continue;
                        var t = Ref.FileId(bones[i]); if (f.Get(t) != null) used.Add(AvatarAncestor(c, t));
                    }
                }
            used.Remove(0);
            used.Remove(c.RootTr);
            // 기믹(포즈 시스템·그림자·장난감 등): 이름이 기믹이거나, 메시가 있어도 기본으로 모두 꺼져 있고 옷·헤어·악세서리 단서가 없는 것
            var words = S(root.Props, "m_Name").ToLowerInvariant();
            if (hint == null && (GimmickWords.Any(w => words.Contains(w)) || skinned + rigid > 0 && shown == 0 && used.Count <= 2))
            {
                r.Kind = "gimmick";
                r.Reason = GimmickWords.Any(w => words.Contains(w)) ? "이름이 기믹" : "기본으로 꺼진 메시만 있음";
                return;
            }
            if (used.Count == 0)
            {
                r.Kind = hint ?? "unknown";
                r.NeedsPlacement = true;
                r.NearOrigin = NearOrigin(c, root);
                r.Reason = skinned + rigid == 0 ? "메시가 없음" : "아바타 뼈에 붙지 않음" + (hint != null ? $" (이름으로는 {KindName(hint)})" : "");
                return;
            }
            long head = c.Human.TryGetValue("Head", out var h) ? h : FindByName(c, "Head");
            bool InHead(long t) { for (var x = t; x != 0; x = Ref.Local(f.Get(x)?.Props ?? new Map(), "m_Father")) if (x == head) return true; return false; }
            var body = used.Where(t => !InHead(t)).ToList();
            var humans = new HashSet<string>(used.Select(t => c.HumanOf.TryGetValue(t, out var hn) ? hn : "").Where(s => s.Length > 0));
            if (head != 0 && body.Count == 0)
            {
                r.Kind = hint == "accessory" ? "accessory" : "hair";
                r.Reason = r.Kind == "hair" ? "머리 뼈만 따라감" : "머리 뼈만 따라감 · 이름이 악세서리";
            }
            else if (humans.Count(x => x.Contains("Leg") || x.Contains("Arm") || x == "Hips" || x == "Spine" || x == "Chest" || x == "UpperChest") >= 2)
            { r.Kind = "outfit"; r.Reason = "몸통·팔다리 뼈를 따라감"; }
            else if (used.Count <= 2 || skinned == 0)
            { r.Kind = hint == "hair" ? "hair" : "accessory"; r.Reason = $"뼈 {used.Count}개에만 붙음"; }
            else { r.Kind = hint ?? "outfit"; r.Reason = $"아바타 뼈 {used.Count}개를 따라감"; }
        }

        /// 아이템 메시(정점 범위 가운데, 쉬는 자세)가 아바타 원점 근처이고 아바타 머리는 높이 있으면 원점에 만들어 둔 소품으로 본다
        static bool NearOrigin(Ctx c, SimObject root)
        {
            if (c.Db == null) return false;
            long head = c.Human.TryGetValue("Head", out var h) ? h : FindByName(c, "Head");
            if (head == 0 || Pos(c.World[head]).Y < 0.5) return false;
            Vec3 lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            bool any = false;
            foreach (var go in Humanoid.Walk(c.F, root))
            {
                var tr = Tr(c.F, go);
                if (tr == 0 || !c.World.TryGetValue(tr, out var w)) continue;
                var comps = Humanoid.Comps(c.F, go).ToList();
                var mesh = comps.FirstOrDefault(x => x.ClassId == 137)?.Props.GetValueOrDefault("m_Mesh")
                           ?? (comps.Any(x => x.ClassId == 23) ? comps.FirstOrDefault(x => x.ClassId == 33)?.Props.GetValueOrDefault("m_Mesh") : null);
                if (!(Ref.Guid(mesh) is string g) || c.Db.Get(g) is not AssetEntry a || a.Ext != ".fbx") continue;
                var model = c.Db.Geometry(a).Model;
                if (model == null || !model.Meshes.TryGetValue(Ref.FileId(mesh), out var m)) continue;
                foreach (var v in m.Vertices)
                {
                    var p = w.MulPoint(v);
                    lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                    hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                    any = true;
                }
            }
            if (!any) return false;
            var center = (lo + hi) * 0.5 - Pos(c.World[c.RootTr]);
            return center.Length < 0.3;
        }

        /// 메시에서 가중치가 실린 본 번호 (m_Bones 순서). 메시를 읽지 못하면 null (모든 본을 센다)
        static HashSet<int>? WeightedBones(Ctx c, object? meshRef)
        {
            if (c.Db == null || !(Ref.Guid(meshRef) is string g) || c.Db.Get(g) is not AssetEntry a || a.Ext != ".fbx") return null;
            var model = c.Db.Geometry(a).Model;
            if (model == null || !model.Meshes.TryGetValue(Ref.FileId(meshRef), out var m) || m.BoneIndices == null || m.BoneWeights == null) return null;
            var set = new HashSet<int>();
            for (int v = 0; v < m.BoneIndices.Count; v++)
                for (int k = 0; k < 4; k++)
                    if (m.BoneWeights[v][k] > 0) set.Add(m.BoneIndices[v][k]);
            return set;
        }

        static string KindName(string k) => k == "hair" ? "헤어" : k == "outfit" ? "의상" : k == "accessory" ? "악세서리" : "알 수 없음";

        static long AvatarAncestor(Ctx c, long tr)
        {
            for (var x = tr; x != 0; x = Ref.Local(c.F.Get(x)?.Props ?? new Map(), "m_Father"))
                if (!c.ItemTrs.Contains(x) || c.Remap.ContainsKey(x)) return c.Remap.TryGetValue(x, out var m) ? m : x;
            return 0;
        }

        // ---------------- 짝 찾기 ----------------

        /// 아이템 아래에서 아바타 뼈 target과 짝이 되는 본 (위에서부터 먼저 만나는 것)
        static long Anchor(Ctx c, long item, long target, Dictionary<string, string> outfitHuman)
        {
            var q = new Queue<long>(); q.Enqueue(item);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                if (t != item && Relaxed(c, t, new List<long> { target }, outfitHuman) != 0) return t;
                foreach (var k in c.Kids[t]) q.Enqueue(k);
            }
            return 0;
        }

        /// 이름이 같은 것 → 대소문자·기호를 뺀 이름이 같은 것 → 휴머노이드 뼈 이름이 같은 것
        static long Relaxed(Ctx c, long x, List<long> cands, Dictionary<string, string> outfitHuman)
        {
            var n = Name(c.F, x);
            var y = cands.FirstOrDefault(k => Name(c.F, k) == n);
            if (y != 0) return y;
            var nn = Norm(n);
            if (nn.Length > 0) { y = cands.FirstOrDefault(k => Norm(Name(c.F, k)) == nn); if (y != 0) return y; }
            if (outfitHuman.TryGetValue(n, out var hn)) y = cands.FirstOrDefault(k => c.HumanOf.TryGetValue(k, out var ah) && ah == hn);
            return y;
        }

        static string Norm(string s)
        {
            var b = new StringBuilder();
            foreach (var ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) b.Append(ch);
            return b.ToString();
        }

        /// 아이템 스킨드 메시가 쓰는 FBX의 .meta → 본 이름 → 휴머노이드 이름
        static Dictionary<string, string> OutfitHuman(Ctx c, long item)
        {
            var d = new Dictionary<string, string>();
            if (c.Db == null || c.F.Get(Ref.Local(c.F.Get(item)!.Props, "m_GameObject")) is not SimObject go) return d;
            var seen = new HashSet<string>();
            foreach (var o in Humanoid.Walk(c.F, go))
                foreach (var comp in Humanoid.Comps(c.F, o))
                    if (comp.ClassId == 137 && Ref.Guid(comp.Props.GetValueOrDefault("m_Mesh")) is string g && seen.Add(g))
                        foreach (var kv in Humanoid.ModelBones(c.Db.Get(g))) d[kv.Key] = kv.Value;
            return d;
        }

        /// 아바타 쪽에서 이름으로 뼈 찾기 (휴머노이드 정보가 없을 때)
        static long FindByName(Ctx c, string name)
        {
            var q = new Queue<long>(); q.Enqueue(c.RootTr);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                if (t != c.RootTr && !c.ItemTrs.Contains(t) && Name(c.F, t) == name) return t;
                foreach (var k in c.Kids[t]) if (!c.ItemTrs.Contains(k)) q.Enqueue(k);
            }
            return 0;
        }

        static void MergeChildren(Ctx c, long x, long t, Report rep, Func<long, List<long>, long> match)
        {
            foreach (var ch in c.Kids[x])
            {
                rep.Bones++;
                var cands = c.Kids[t].Where(k => !c.ItemTrs.Contains(k)).ToList();
                var y = match(ch, cands);
                if (y == 0) { CountAll(c, ch, rep); continue; }
                rep.Matched++;
                rep.Offsets.Add((Pos(c.World[ch]) - Pos(c.World[y])).Length);
                Pair(c, ch, y, rep, root: false);
                MergeChildren(c, ch, y, rep, match);
            }
        }

        static void CountAll(Ctx c, long t, Report rep) { foreach (var k in c.Kids[t]) { rep.Bones++; CountAll(c, k, rep); } }

        /// 크기를 바꿀 때 키울 오브젝트: 루트 · 아바타 뼈에 붙은 본 · Bone Proxy (없어질 본은 뺀다). 경로는 붙이기 전 계층으로
        static void ScaleRootsOf(Ctx c, long itemTr, Report rep)
        {
            string PathOf(long tr)
            {
                var parts = new List<string>();
                for (long t = tr; t != itemTr && c.Parent.TryGetValue(t, out var p); t = p) parts.Add(Name(c.F, t));
                parts.Reverse();
                return string.Join("/", parts);
            }
            void Add(long tr)
            {
                if (c.Delete.Contains(tr) || c.F.Get(tr) == null) return;
                var go = Ref.Local(c.F.Get(tr)!.Props, "m_GameObject");
                if (go != 0 && !rep.ScaleRoots.ContainsKey(go)) rep.ScaleRoots[go] = tr == itemTr ? "" : PathOf(tr);
            }
            Add(itemTr);
            foreach (var t in rep.Attached) Add(t);
            foreach (var go in rep.Moved) if (c.F.Get(go) is SimObject g) Add(Tr(c.F, g));
        }

        static double ValidScale(double value) => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? 1 : Math.Max(0.01, Math.Min(100, value));

        /// <summary>아이템의 스킨 본을 합친 뒤에도 루트·붙인 본·Bone Proxy 부품이 같은 배율을 받게 한다.</summary>
        static void ApplyScale(Ctx c, Report rep)
        {
            var factor = ValidScale(rep.Scale);
            if (Math.Abs(factor - 1) < 1e-9) return;
            foreach (var go in rep.ScaleRoots.Keys.ToList())
            {
                if (c.F.Get(go) is not SimObject obj) continue;
                var tr = Tr(c.F, obj);
                if (tr == 0 || c.Delete.Contains(tr)) continue;
                var scale = c.F.Get(tr)?.Props.GetValueOrDefault("m_LocalScale") as Map;
                if (scale == null) continue;
                scale["x"] = F(D(scale, "x", 1) * factor);
                scale["y"] = F(D(scale, "y", 1) * factor);
                scale["z"] = F(D(scale, "z", 1) * factor);
            }
        }

        /// 월드 행렬의 크기 (세 축 길이의 평균)
        static double ScaleOf(Mat4 m)
        {
            double sum = 0;
            for (int col = 0; col < 3; col++) sum += Math.Sqrt(m.M[0, col] * m.M[0, col] + m.M[1, col] * m.M[1, col] + m.M[2, col] * m.M[2, col]);
            return sum / 3;
        }

        /// 의상 본 x를 아바타 본 t에 붙인다
        static void Pair(Ctx c, long x, long t, Report rep, bool root)
        {
            var f = c.F;
            if (c.HumanOf.TryGetValue(t, out var hn) && hn == "Head" && ScaleOf(c.World[x]) > 1e-6) rep.HeadScale = ScaleOf(c.World[t]) / ScaleOf(c.World[x]);
            var xgo = f.Get(Ref.Local(f.Get(x)!.Props, "m_GameObject"));
            bool onlyTransform = xgo != null && L(xgo.Props, "m_Component").Count == 1;
            if (!root && onlyTransform && Same(c.World[x], c.World[t]))
            {
                // 완전히 합침: 의상 본을 없애고 자식은 아바타 본 아래로.
                // 맞는 아바타 본이 없는 자식(의상의 NameTag_root 같은 빈 오브젝트)도 아바타 본에 붙은 것이라 Attached에 넣는다
                // — 아바타 PhysBone 루트에 자식이 늘어 Multi Child Ignore로 루트가 안 돌던 문제 (아바타 A 왼쪽 가슴만 흔들림). 맞는 자식은 뒤에서 다시 Pair된다.
                foreach (var ch in c.Kids[x]) { SetParent(c, ch, t); rep.Attached.Add(ch); }
                c.Remap[x] = t;
                c.Remap[xgo!.Id] = Ref.Local(f.Get(t)!.Props, "m_GameObject");
                c.Delete.Add(x); c.Delete.Add(xgo.Id);
                rep.Merged++;
            }
            else { SetParent(c, x, t); if (!root) rep.Attached.Add(x); }
        }

        /// 월드 위치를 그대로 두고 부모를 바꾼다
        static void SetParent(Ctx c, long child, long parent)
        {
            var o = c.F.Get(child)!;
            o.Props["m_Father"] = Ref.Make(parent);
            (c.World[parent].Inverse() * c.World[child]).Decompose(out var t, out var q, out var s);
            SetLocal(o, t, q, s);
        }

        /// 뼈 위치로 옮기되 월드 회전·크기는 그대로 둔다 (원점에 만들어 둔 소품: 뼈 축이 기울어 있어도 똑바로 붙는다)
        static void MoveToBone(Ctx c, long child, long parent)
        {
            var w = c.World[child];
            var moved = Mat4.Identity();
            for (int r = 0; r < 3; r++) for (int col = 0; col < 3; col++) moved.M[r, col] = w.M[r, col];
            var bp = Pos(c.World[parent]);
            moved.M[0, 3] = bp.X; moved.M[1, 3] = bp.Y; moved.M[2, 3] = bp.Z;
            var o = c.F.Get(child)!;
            o.Props["m_Father"] = Ref.Make(parent);
            (c.World[parent].Inverse() * moved).Decompose(out var t, out var q, out var s);
            SetLocal(o, t, q, s);
        }

        /// 뼈 위치로 옮긴다 (로컬 위치·회전 0, 크기는 그대로) — MA Bone Proxy "As child; at root"
        static void SetParentAtRoot(SimFile f, long child, long parent)
        {
            var o = f.Get(child)!;
            o.Props["m_Father"] = Ref.Make(parent);
            SetLocal(o, new Vec3(0, 0, 0), new Quat(0, 0, 0, 1), V(o.Props, "m_LocalScale", 1));
        }

        static void SetLocal(SimObject o, Vec3 t, Quat q, Vec3 s)
        {
            o.Props["m_LocalPosition"] = new Map { ["x"] = F(t.X), ["y"] = F(t.Y), ["z"] = F(t.Z) };
            o.Props["m_LocalRotation"] = new Map { ["x"] = F(q.X), ["y"] = F(q.Y), ["z"] = F(q.Z), ["w"] = F(q.W) };
            o.Props["m_LocalScale"] = new Map { ["x"] = F(s.X), ["y"] = F(s.Y), ["z"] = F(s.Z) };
        }

        static bool Same(Mat4 a, Mat4 b)
        {
            for (int r = 0; r < 3; r++)
                for (int col = 0; col < 4; col++)
                    if (Math.Abs(a.M[r, col] - b.M[r, col]) > (col == 3 ? 1e-4 : 1e-3)) return false;
            return true;
        }

        static Vec3 Pos(Mat4 m) => new Vec3(m.M[0, 3], m.M[1, 3], m.M[2, 3]);

        /// <summary>아바타 루트 기준 경로 ("Armature/Hips", 빈 문자열 = 루트) → Transform fileID (없으면 0)</summary>
        public static long Find(SimFile f, long rootTr, string path) => string.IsNullOrEmpty(path) ? rootTr : FindUnder(f, rootTr, path);

        static long FindUnder(SimFile f, long from, string path)
        {
            long cur = from;
            foreach (var part in path.Split('/'))
            {
                long next = 0;
                foreach (var ch in L(f.Get(cur)!.Props, "m_Children"))
                    if (f.Get(Ref.FileId(ch)) is SimObject ct && Name(f, ct.Id) == part) { next = ct.Id; break; }
                if (next == 0) return 0;
                cur = next;
            }
            return cur;
        }

        public static long Tr(SimFile f, SimObject go)
        {
            foreach (var ch in L(go.Props, "m_Component"))
            {
                var id = Ref.FileId((ch as Map)?.GetValueOrDefault("component"));
                if (f.Get(id) is SimObject o && (o.ClassId == 4 || o.ClassId == 224)) return id;
            }
            return 0;
        }

        /// 아바타 루트 바로 아래의 아이템: Compose로 넣은 것, 또는 씬에 원래 있던 것 중 아래에 MA Merge Armature·Bone Proxy가 있는 것
        static bool IsItem(Ctx c, long tr)
        {
            var go = Ref.Local(c.F.Get(tr)!.Props, "m_GameObject");
            if (c.F.AddedFrom.ContainsKey(go)) return true;
            var stack = new Stack<long>(); stack.Push(tr);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (c.F.Get(Ref.Local(c.F.Get(t)!.Props, "m_GameObject")) is SimObject g)
                    foreach (var comp in Humanoid.Comps(c.F, g))
                        if (comp.ClassId == 114 && (comp.Props.ContainsKey("mergeTarget") || comp.Props.ContainsKey("boneReference"))) return true;
                foreach (var k in c.Kids[t]) stack.Push(k);
            }
            return false;
        }

        static void Collect(Ctx c, long tr, HashSet<long> set)
        {
            set.Add(tr);
            foreach (var k in c.Kids[tr]) Collect(c, k, set);
        }

        static long TopUnder(SimFile f, long tr, long rootTr)
        {
            while (true)
            {
                var p = Ref.Local(f.Get(tr)!.Props, "m_Father");
                if (p == 0 || p == rootTr) return tr;
                tr = p;
            }
        }

        static Mat4 Local(Map p) => Mat4.TRS(V(p, "m_LocalPosition", 0), Q(p), V(p, "m_LocalScale", 1));

        static Vec3 V(Map p, string k, double def)
        {
            var m = p.GetValueOrDefault(k) as Map;
            return new Vec3(D(m, "x", def), D(m, "y", def), D(m, "z", def));
        }

        static Quat Q(Map p)
        {
            var m = p.GetValueOrDefault("m_LocalRotation") as Map;
            return new Quat(D(m, "x", 0), D(m, "y", 0), D(m, "z", 0), D(m, "w", 1));
        }

        static double D(Map? m, string k, double def) =>
            m != null && m.GetValueOrDefault(k) is string s && double.TryParse(s, NumberStyles.Float, C, out var v) ? v : def;

        static string F(double v) => ((float)v).ToString("R", C);
        static string Name(SimFile f, long tr) => f.Get(Ref.Local(f.Get(tr)?.Props ?? new Map(), "m_GameObject"))?.Props.GetValueOrDefault("m_Name") as string ?? "";
        static string S(Map m, string k) => m.GetValueOrDefault(k) as string ?? "";
        static List<object?> L(Map m, string k) => m.GetValueOrDefault(k) as List<object?> ?? new List<object?>();
    }
}
