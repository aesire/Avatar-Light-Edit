// 아바타에 처음부터 들어 있는 메시(기본 옷·헤어·악세서리·몸)를 알아보고 묶는다. 묶음 토글은 오브젝트 켜고 끄기(NodeActive)만 한다.
// 붙인 아이템(IrScene.Outfits) 아래 메시와 Bone Proxy로 아바타 뼈 아래에 옮겨진 아이템 부분(IrOutfit.Parts)은 빼고 본다 (아이템은 아이템 줄에서 켜고 끈다).
// 태그 EditorOnly(업로드에서 빠지는 조각·위치 확인용 더미)는 묶음에 넣되 처음부터 꺼져 있다 (IR에서 Active = false). 켜려면 EditorOnly 조상도 함께 켠다 (WardrobePart.EditorOnly).
// 가늠 순서: 이름 낱말 → 비셈 블렌드쉐이프(vrc.v_) → 가중치가 실린 뼈 (머리 아래만 = 헤어, 몸통·팔다리 = 의상, 뼈 한두 개 = 악세서리).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public sealed class WardrobePart
    {
        public string Renderer = ""; public int Node; public string Name = "";
        public string Kind = "outfit";   // body · hair · outfit · accessory
        public string Reason = "";
        public int[] EditorOnly = Array.Empty<int>();   // 태그 EditorOnly인 자신·조상 노드 (업로드에서 빠짐). 보이려면 모두 켜져야 한다
    }

    public static class Wardrobe
    {
        public static readonly string[] Kinds = { "hair", "outfit", "accessory", "body" };

        public static string KindLabel(string kind) => kind switch
        {
            "hair" => "헤어", "outfit" => "옷", "accessory" => "악세서리", "body" => "몸", _ => kind,
        };

        // 낱말(이름을 _·공백·대소문자 경계로 나눈 조각) 단위로 맞춘다: "Underwear"의 ear, "Shirt"의 hair 같은 오인을 막는다
        static readonly string[] BodyWords = { "body", "face", "head", "skin", "kao", "karada", "素体", "体", "顔", "몸", "얼굴", "eye", "eyes", "teeth", "tongue", "mouth", "tooth", "eyelash", "eyebrow", "matsuge", "mayu" };
        static readonly string[] HairWords = { "hair", "hairs", "wig", "bang", "bangs", "maegami", "ahoge", "ponytail", "twintail", "twintails", "kami", "髪", "前髪", "後ろ髪", "アホ毛", "ヘア", "헤어", "머리카락", "앞머리", "뒷머리" };
        static readonly string[] AccWords = { "acc", "accessory", "ring", "earring", "earrings", "pierce", "piercing", "hat", "cap", "glasses", "megane", "necklace", "choker", "halo", "wing", "wings", "horn", "horns", "crown", "mask", "hairpin", "pin", "hairclip", "clip", "kamidome", "barrette", "crystal", "headphone", "headphones", "bag",
                                              "アクセ", "髪留め", "ヘアピン", "ピアス", "イヤリング", "帽子", "メガネ", "眼鏡", "ネックレス", "チョーカー", "角", "羽", "악세", "귀걸이", "모자", "안경", "목걸이", "날개" };
        // 동물 귀·꼬리는 아바타 몸의 일부로 본다 (악세서리 묶음으로 한꺼번에 꺼지지 않게). 헤어 낱말이 함께 있으면 헤어 ("Hair_TwinTail")
        static readonly string[] KemonoWords = { "ear", "ears", "kemomimi", "kemono", "mimi", "nekomimi", "tail", "tails", "shippo", "けも耳", "猫耳", "しっぽ", "尻尾", "耳", "꼬리", "귀", "동물귀" };
        static readonly string[] OutfitWords = { "outfit", "cloth", "clothes", "costume", "dress", "shirt", "tshirt", "blouse", "skirt", "pants", "shorts", "jacket", "hoodie", "parka", "coat", "vest", "shoes", "shoe", "boots", "loafer", "loafers", "sneaker", "sneakers", "heel", "heels", "sandal", "sandals",
                                                 "socks", "sock", "tights", "stocking", "stockings", "kneesocks", "underwear", "bra", "pantsu", "panty", "panties", "bikini", "swimsuit", "collar", "sailor", "tie", "necktie", "ribbon", "belt", "glove", "gloves", "armwarmer", "warmer", "sleeve", "sleeves", "apron", "cape", "cardigan", "sweater", "knit", "bandage", "harness", "garter", "frill", "tops", "bottoms", "uniform",
                                                 "衣装", "服", "スカート", "靴", "靴下", "의상", "옷", "치마", "신발", "양말" };

        /// <summary>아이템에 들지 않은 렌더러를 하나씩 가늠한다. weighted: 렌더러의 메시에 가중치가 실린 뼈 노드 (없으면 null = 모름).</summary>
        public static List<WardrobePart> Parts(IrScene s, Func<IrRenderer, IEnumerable<int>?>? weighted = null)
        {
            var itemNodes = new HashSet<int>(s.Outfits.SelectMany(o => o.Parts.Prepend(o.Node)));
            bool InItem(int n) { for (int x = n; x >= 0 && x < s.Nodes.Count; x = s.Nodes[x].Parent) if (itemNodes.Contains(x)) return true; return false; }
            int head = s.Humanoid.TryGetValue("Head", out var h) ? h : -1;
            var humanOf = new Dictionary<int, string>();
            foreach (var kv in s.Humanoid) humanOf[kv.Value] = kv.Key;

            var list = new List<WardrobePart>();
            foreach (var r in s.Renderers)
            {
                if (r.Node < 0 || r.Node >= s.Nodes.Count || InItem(r.Node)) continue;
                var name = s.Nodes[r.Node].Name;
                var p = new WardrobePart { Renderer = r.Id, Node = r.Node, Name = name, EditorOnly = EditorOnlyChain(s, r.Node) };
                var words = Words(name);
                var mesh = s.FindMesh(r.Mesh);
                bool visemes = mesh != null && mesh.BlendShapes.Any(b => b.Name.StartsWith("vrc.v_", StringComparison.OrdinalIgnoreCase) || b.Name.StartsWith("vrc.blink", StringComparison.OrdinalIgnoreCase));
                if (Has(words, KemonoWords) && !Has(words, HairWords) && !Has(words, AccWords)) { p.Kind = "body"; p.Reason = "동물 귀·꼬리"; }
                else if (Has(words, AccWords) && !Has(words, BodyWords)) { p.Kind = Has(words, HairWords) && !words.Any(w => w.Contains("pin") || w.Contains("clip") || w == "ribbon") ? "hair" : "accessory"; p.Reason = "이름"; }
                else if (Has(words, HairWords)) { p.Kind = "hair"; p.Reason = "이름"; }
                else if (Has(words, OutfitWords)) { p.Kind = "outfit"; p.Reason = "이름"; }
                else if (Has(words, BodyWords)) { p.Kind = "body"; p.Reason = "이름"; }
                else if (visemes) { p.Kind = "body"; p.Reason = "입 모양 블렌드쉐이프"; }
                else
                {
                    // 뼈로: 가중치가 실린 뼈가 결국 어느 휴머노이드 뼈를 따라가는지
                    var bones = (weighted?.Invoke(r) ?? r.Bones).Where(b => b >= 0 && b < s.Nodes.Count).ToList();
                    if (r.Type == "MeshRenderer" || bones.Count == 0) bones = new List<int> { r.Node };
                    var humans = new HashSet<string>();
                    bool allHead = head >= 0;
                    foreach (var b in bones)
                    {
                        string? hn = null; bool underHead = false;
                        for (int x = b; x >= 0 && x < s.Nodes.Count; x = s.Nodes[x].Parent)
                        {
                            if (hn == null && humanOf.TryGetValue(x, out var nm)) hn = nm;
                            if (x == head) { underHead = true; break; }
                        }
                        if (hn != null) humans.Add(hn);
                        if (!underHead) allHead = false;
                    }
                    if (allHead) { p.Kind = "hair"; p.Reason = "머리 뼈만 따라감"; }
                    else if (humans.Count(x => x.Contains("Leg") || x.Contains("Arm") || x == "Hips" || x == "Spine" || x == "Chest" || x == "UpperChest") >= 2) { p.Kind = "outfit"; p.Reason = "몸통·팔다리 뼈를 따라감"; }
                    else { p.Kind = "accessory"; p.Reason = $"뼈 {Math.Max(1, humans.Count)}개에만 붙음"; }
                }
                list.Add(p);
            }
            return list;
        }

        /// <summary>노드 자신과 조상 중 태그 EditorOnly인 것 (가까운 것부터)</summary>
        public static int[] EditorOnlyChain(IrScene s, int node)
        {
            var list = new List<int>();
            for (int x = node; x >= 0 && x < s.Nodes.Count; x = s.Nodes[x].Parent) if (s.Nodes[x].EditorOnly) list.Add(x);
            return list.Count == 0 ? Array.Empty<int>() : list.ToArray();
        }

        // ---------------- 켜고 끄기 (아바타 페이지·창 없는 편집 공통, 2026-11-02 AvatarPanel에서 옮김) ----------------

        /// <summary>메시 하나가 보이는지: 오브젝트·렌더러가 켜져 있고 EditorOnly 조상도 모두 켜져 있다.</summary>
        public static bool Visible(Document doc, int node, string renderer) => doc.Get(Addr.NodeActive(node)).AsBool && doc.Get(Addr.RendererEnabled(renderer)).AsBool
            && EditorOnlyChain(doc.Original, node).All(x => doc.Get(Addr.NodeActive(x)).AsBool);

        /// <summary>메시 하나를 켜거나 끈다. 켤 때는 EditorOnly 조상과 꺼 둔 렌더러도 함께 켠다.</summary>
        public static void Show(Document doc, int node, string renderer, bool on)
        {
            if (on)
            {
                doc.Set(Addr.NodeActive(node), true);
                foreach (var x in EditorOnlyChain(doc.Original, node)) if (!doc.Get(Addr.NodeActive(x)).AsBool) doc.Set(Addr.NodeActive(x), true);
                if (!doc.Get(Addr.RendererEnabled(renderer)).AsBool) doc.Set(Addr.RendererEnabled(renderer), true);
            }
            else doc.Set(Addr.NodeActive(node), false);
        }

        /// <summary>그 종류의 기본 메시를 모두 켜거나 끈다 (실행 취소 한 번). 바뀐 것이 없으면 false.</summary>
        public static bool SetGroup(Document doc, IEnumerable<WardrobePart> parts, string kind, bool on)
        {
            var list = parts.Where(p => p.Kind == kind && Visible(doc, p.Node, p.Renderer) != on).ToList();
            if (list.Count == 0) return false;
            using (doc.Group("기본 " + KindLabel(kind) + (on ? " 착용" : " 해제")))
                foreach (var p in list) Show(doc, p.Node, p.Renderer, on);
            return true;
        }

        /// <summary>
        /// 붙인 아이템을 입히거나 벗긴다. Bone Proxy로 아바타 뼈 아래에 옮겨진 부분(Parts)도 함께:
        /// 끄면 끄고, 켜면 원래 상태로 (처음부터 꺼져 있던 손잡이 메시 등은 꺼진 채).
        /// </summary>
        public static void SetItem(Document doc, IrOutfit o, bool on)
        {
            using (doc.Group((on ? "착용: " : "해제: ") + o.Name))
            {
                doc.Set(Addr.NodeActive(o.Node), on);
                foreach (var p in o.Parts) { if (on) doc.Reset(Addr.NodeActive(p)); else doc.Set(Addr.NodeActive(p), false); }
            }
        }

        /// <summary>IR 버퍼에서 가중치가 실린 뼈 노드를 읽는다 (Parts의 weighted로 넘긴다). 버퍼를 열지 못하면 null.</summary>
        public static Func<IrRenderer, IEnumerable<int>?> WeightsFrom(IrScene s)
        {
            IrBuffers? buf = null;
            try { if (System.IO.File.Exists(s.BufferFile)) buf = new IrBuffers(s.BufferFile); } catch (Exception) { buf = null; }
            var cache = new Dictionary<string, HashSet<int>?>();
            return r =>
            {
                if (buf == null || r.Type != "SkinnedMeshRenderer") return null;
                if (!cache.TryGetValue(r.Mesh, out var idx))
                {
                    idx = null;
                    var m = s.FindMesh(r.Mesh);
                    if (m != null && !m.BoneIndices.IsEmpty && !m.BoneWeights.IsEmpty)
                    {
                        try
                        {
                            var bi = buf.Ints(m.BoneIndices); var bw = buf.Floats(m.BoneWeights);
                            idx = new HashSet<int>();
                            for (int i = 0; i < bi.Length && i < bw.Length; i++) if (bw[i] > 0) idx.Add(bi[i]);
                        }
                        catch (Exception) { idx = null; }
                    }
                    cache[r.Mesh] = idx;
                }
                return idx?.Where(i => i >= 0 && i < r.Bones.Length).Select(i => r.Bones[i]);
            };
        }

        /// <summary>이름을 낱말로: 기호·숫자·공백·대소문자 경계(ArmWarmer → arm, warmer)에서 나눈다. 한·중·일 글자는 덩어리 그대로 둔다.</summary>
        public static List<string> Words(string name)
        {
            var words = new List<string>();
            var b = new StringBuilder();
            void Flush() { if (b.Length > 0) { words.Add(b.ToString().ToLowerInvariant()); b.Clear(); } }
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetter(c)) { Flush(); continue; }
                bool ascii = c < 128;
                if (b.Length > 0)
                {
                    var prev = b[b.Length - 1];
                    bool prevAscii = prev < 128;
                    if (ascii != prevAscii) Flush();
                    else if (ascii && char.IsUpper(c) && char.IsLower(prev)) Flush();
                    else if (ascii && char.IsUpper(prev) && char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]) && b.Length > 1) Flush();
                }
                b.Append(c);
            }
            Flush();
            return words;
        }

        static bool Has(List<string> words, string[] dict)
        {
            foreach (var w in words)
                foreach (var d in dict)
                {
                    if (w == d) return true;
                    // 한·중·일 낱말은 덩어리 안에 들어 있으면 (後ろ髪 → 髪)
                    if (d.Length > 0 && d[0] >= 128 && w.Contains(d)) return true;
                }
            return false;
        }
    }
}
