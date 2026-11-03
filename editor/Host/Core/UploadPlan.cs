// VRChat 업로드 준비 (PLAN.md 3.10): Document → 업로드 프로젝트의 Unity 스크립트(VrcProtoUpload.cs)가 읽는 apply.json, 자동 설치 기믹 목록.
// 원본 파일은 고치지 않는다: Unity 쪽은 원본 패키지를 가져온 뒤 머티리얼·메시는 사본을 만들어 바꾸고, 아바타는 장면의 인스턴스만 바꾼다.
// 노드 경로는 아바타 루트 아래 이름 경로 ("Armature/Hips"), 같은 이름의 형제가 있으면 "이름[k]" (k = 같은 이름 중 몇 번째, 0부터).
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public sealed class Gimmick
    {
        public string Key = "", Label = "", PackageId = "", RepoUrl = "", License = "", LicenseUrl = "", Note = "";
        /// <summary>재배포·수정이 금지된 것 (공식 채널에서 원본 그대로 받기만).</summary>
        public bool Restricted;
    }

    public static class UploadPlan
    {
        public const string VrchatRepo = "https://vrchat.github.io/packages/index.json";
        public const string CuratedRepo = "https://vrchat-community.github.io/vpm-listing-curated/index.json";
        public const string SdkId = "com.vrchat.avatars";
        public const string SdkLicenseUrl = "https://hello.vrchat.com/legal/sdk";
        public const string LilToonShaderGuid = "df12117ecd77c31469c224178886498e";

        /// <summary>자주 쓰는 기믹 (공식 저장소에서 받기만, 툴에 넣지 않음). 라이선스는 2026-10-18 확인.</summary>
        public static readonly Gimmick[] Gimmicks =
        {
            new Gimmick { Key = "ma", Label = "Modular Avatar", PackageId = "nadena.dev.modular-avatar", RepoUrl = "https://vpm.nadena.dev/vpm.json", License = "MIT", LicenseUrl = "https://github.com/bdunderscore/modular-avatar/blob/main/LICENSE.md", Note = "의상을 MA 설정대로 합쳐요. 붙인 의상이 MA를 쓰면 자동으로 골라요" },
            new Gimmick { Key = "liltoon", Label = "lilToon", PackageId = "jp.lilxyzw.liltoon", RepoUrl = "https://lilxyzw.github.io/vpm-repos/vpm.json", License = "MIT", LicenseUrl = "https://github.com/lilxyzw/lilToon/blob/master/LICENSE", Note = "lilToon 머티리얼이 있고 패키지에 lilToon이 들어 있지 않으면 자동으로 골라요" },
            new Gimmick { Key = "poiyomi", Label = "Poiyomi Toon (무료판)", PackageId = "com.poiyomi.toon", RepoUrl = "https://poiyomi.github.io/vpm/index.json", License = "MIT", LicenseUrl = "https://github.com/poiyomi/PoiyomiToonShader/blob/master/LICENSE", Note = "Poiyomi 머티리얼이 있으면 자동으로 골라요 (Pro판은 자동 설치하지 않아요)" },
            new Gimmick { Key = "aao", Label = "Avatar Optimizer (AAO)", PackageId = "com.anatawa12.avatar-optimizer", RepoUrl = "https://vpm.anatawa12.com/vpm.json", License = "MIT", LicenseUrl = "https://github.com/anatawa12/AvatarOptimizer/blob/master/LICENSE", Note = "업로드할 때 메시·본을 자동으로 정리해요" },
            new Gimmick { Key = "gesture", Label = "Gesture Manager", PackageId = "vrchat.blackstartx.gesture-manager", RepoUrl = CuratedRepo, License = "MIT", LicenseUrl = "https://github.com/BlackStartx/VRC-Gesture-Manager/blob/master/LICENSE", Note = "Unity에서 표정·메뉴를 시험해요" },
            new Gimmick { Key = "vrcfury", Label = "VRCFury", PackageId = "com.vrcfury.vrcfury", RepoUrl = "https://vcc.vrcfury.com", License = "VRCFury 라이선스", LicenseUrl = "https://github.com/VRCFury/VRCFury/blob/main/LICENSE.md", Restricted = true, Note = "공식 저장소에서 원본 그대로 받기만 해요 (재배포·수정 금지 조건)" },
        };

        public static Gimmick? Find(string key) => Gimmicks.FirstOrDefault(g => g.Key == key);

        /// <summary>
        /// 자동으로 고를 기믹. sourceHas(guid): 원본 패키지에 그 GUID 에셋이 들어 있는지 (lilToon이 패키지에 들어 있으면 VPM판과 GUID가 겹쳐 넣지 않는다).
        /// </summary>
        public static List<string> AutoGimmicks(Document doc, Func<string, bool>? sourceHas = null)
        {
            var s = doc.Original;
            var keys = new List<string>();
            if (s.Outfits.Any(o => o.Method.StartsWith("ma-") || o.Method == "name")) keys.Add("ma");
            string Shader(IrMaterial m) => doc.Schema(m.Guid)?.Shader ?? m.ShaderName ?? "";
            if (s.Materials.Any(m => m.ShaderGuid == LilToonShaderGuid || Shader(m).IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0)
                && !(sourceHas?.Invoke(LilToonShaderGuid) ?? false)) keys.Add("liltoon");
            if (s.Materials.Any(m => Shader(m).IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0)) keys.Add("poiyomi");
            return keys;
        }

        /// <summary>아바타 루트 아래 경로 (루트 자신 = ""). 같은 이름 형제는 [k].</summary>
        public static string NodePath(IrScene s, int id) => PathNames(s)(id);

        /// <summary>
        /// 노드 id → 경로 함수. 노드마다 경로 한 칸의 이름("이름" 또는 "이름[k]")을 한 번만 셈해 둔다 (노드가 많아도 경로마다 전체를 다시 훑지 않게).
        /// </summary>
        public static Func<int, string> PathNames(IrScene s)
        {
            var seen = new Dictionary<(int, string), int>();
            var index = new int[s.Nodes.Count];
            for (int i = 0; i < s.Nodes.Count; i++)   // IR 노드 순서 = 형제 순서
            {
                var key = (s.Nodes[i].Parent, s.Nodes[i].Name);
                seen.TryGetValue(key, out var k);
                index[i] = k; seen[key] = k + 1;
            }
            var step = new string[s.Nodes.Count];
            for (int i = 0; i < s.Nodes.Count; i++)
            {
                var n = s.Nodes[i];
                step[i] = seen[(n.Parent, n.Name)] > 1 ? $"{n.Name}[{index[i]}]" : n.Name;
            }
            return id =>
            {
                var parts = new List<string>();
                for (int i = id; i >= 0 && i < s.Nodes.Count && s.Nodes[i].Parent >= 0; i = s.Nodes[i].Parent) parts.Add(step[i]);
                parts.Reverse();
                return string.Join("/", parts);
            };
        }

        /// <summary>
        /// 원본에 이미 들어 있는 눈 깜빡임 보정 컴포넌트 ("오브젝트 (스크립트)"). 가져오기가 IR existingBlinkFix에 남긴다 (예전 IR에는 없음 → Unity 쪽이 다시 본다).
        /// </summary>
        public static List<string> ExistingBlinkFix(IrScene s) =>
            s.Raw["existingBlinkFix"].Items.Select(e =>
            {
                int n = e["node"].AsInt;
                return (n >= 0 && n < s.Nodes.Count ? s.Nodes[n].Name : "?") + " (" + e["script"].AsString + ")";
            }).ToList();

        /// <summary>원본의 눈 깜빡임 보정 컴포넌트가 입 보정도 하는지 (입 블렌드쉐이프 목록이 채워짐).</summary>
        public static bool ExistingMouthFix(IrScene s) => s.Raw["existingBlinkFix"].Items.Any(e => e["mouth"].AsBool);

        /// <summary>에디터의 깜빡임 맞추기(또는 kind = mouthFix 입 맞추기)를 쓰는 메시가 있는지.</summary>
        public static bool UsesBlinkFix(Document doc, string kind = MeshOps.BlinkFix) =>
            doc.Overrides.Any(kv => kv.Key.Kind == AddrKind.MeshEdit && kv.Value.Items.Any(op => op["op"].AsString == kind));

        /// <summary>
        /// apply.json. 업로드에 필요 없는 것(자세·환경·카메라·아바타 전체 자리)은 넣지 않는다.
        /// excludeBlinkFix·excludeMouthFix: 깜빡임·입 맞추기(blinkFix·mouthFix) 연산을 뺀다 (원본에 이미 보정이 있을 때). exportDir: 기존 프로젝트로 내보내기 결과 폴더 ("Assets/…").
        /// </summary>
        public static JNode Build(Document doc, string avatarName, string? thumbnail = null, bool excludeBlinkFix = false, string? exportDir = null, bool excludeMouthFix = false)
        {
            var s = doc.Original;
            var path = PathNames(s);
            var src = s.Raw["source"];
            var root = JNode.Obj().Set("version", 1).Set("name", avatarName)
                .Set("avatar", JNode.Obj().Set("guid", src["guid"].AsString).Set("path", src["path"].AsString).Set("root", src["root"].AsString))
                .Set("sources", src["sources"].Count > 0 ? src["sources"].Clone() : JNode.Arr())
                .Set("project", src["project"].AsStringOrNull)
                .Set("ir", doc.IrPath);   // Unity 쪽이 IR 정점과 Unity 정점을 짝지어 메시 편집을 옮긴다 (VrcProtoUpload.cs TransferToUnity)

            // 붙인 아이템: IR에서 놓인 부모·로컬 TRS 그대로
            var outfits = JNode.Arr();
            foreach (var o in s.Outfits)
            {
                if (o.Node < 0 || o.Node >= s.Nodes.Count) continue;
                var n = s.Nodes[o.Node];
                var trs = doc.Get(Addr.NodeTransform(o.Node)).ToFloatArray();   // 핸들로 옮겼으면 그 자리
                outfits.Add(JNode.Obj().Set("prefab", o.Prefab).Set("name", o.Name).Set("kind", o.Kind).Set("method", o.Method).Set("target", o.Target)
                    .Set("parent", n.Parent >= 0 ? path(n.Parent) : "").Set("path", path(o.Node))
                    .Set("trs", JNode.FloatArray(trs)).Set("active", doc.Get(Addr.NodeActive(o.Node)).AsBool));
            }
            root.Set("outfits", outfits);

            var nodes = JNode.Arr(); var renderers = new Dictionary<string, JNode>(); var mats = new Dictionary<string, JNode>(); var meshes = JNode.Arr();
            var textures = JNode.Obj();
            JNode Ren(string id)
            {
                if (renderers.TryGetValue(id, out var j)) return j;
                var r = s.FindRenderer(id);
                j = JNode.Obj().Set("path", r != null ? path(r.Node) : "").Set("id", id);
                renderers[id] = j;
                return j;
            }
            JNode Mat(string key)
            {
                if (mats.TryGetValue(key, out var j)) return j;
                var m = s.FindMaterial(key);
                j = JNode.Obj().Set("key", key).Set("name", m?.Name ?? key);
                mats[key] = j;
                return j;
            }
            JNode Sub(JNode o, string name) { if (!o.Has(name)) o.Set(name, JNode.Obj()); return o[name]; }

            foreach (var kv in doc.Overrides.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            {
                var a = kv.Key; var v = kv.Value;
                switch (a.Kind)
                {
                    case AddrKind.NodeActive:
                        if (int.TryParse(a.Target, out var nid) && nid >= 0 && nid < s.Nodes.Count && !s.Outfits.Any(o => o.Node == nid))
                            nodes.Add(JNode.Obj().Set("path", path(nid)).Set("active", v.AsBool));
                        break;
                    case AddrKind.RendererEnabled: Ren(a.Target).Set("enabled", v.AsBool); break;
                    case AddrKind.BlendShape: Sub(Ren(a.Target), "blendShapes").Set(a.Name, v.AsFloat); break;
                    case AddrKind.MatFloat: Sub(Mat(a.Target), "floats").Set(a.Name, v); break;
                    case AddrKind.MatColor: Sub(Mat(a.Target), "colors").Set(a.Name, v); break;
                    case AddrKind.MatVector: Sub(Mat(a.Target), "vectors").Set(a.Name, v); break;
                    case AddrKind.MatTexST: Sub(Mat(a.Target), "texST").Set(a.Name, v); break;
                    case AddrKind.MatKeyword: Sub(Mat(a.Target), "keywords").Set(a.Name, v.AsBool); break;
                    case AddrKind.MatRenderQueue: Mat(a.Target).Set("renderQueue", v.AsInt); break;
                    case AddrKind.MatShader: Mat(a.Target).Set("shader", v.AsString); break;
                    case AddrKind.MatTex:
                        Sub(Mat(a.Target), "textures").Set(a.Name, v);
                        // 원본 패키지에 없는 텍스처(색 굽기 결과 등)는 파일을 프로젝트로 복사해 쓴다
                        if (v.AsStringOrNull is string tk && doc.Texture(tk) is IrTexture t && t.File.Length > 0)
                            textures.Set(tk, JNode.Obj().Set("file", t.File).Set("path", t.Path).Set("like", doc.Get(Addr.TexAsset(tk))["like"]));
                        break;
                    case AddrKind.MeshEdit:
                        {
                            var m = s.FindMesh(a.Target);
                            if (excludeBlinkFix || excludeMouthFix)
                            {
                                var keep = JNode.Arr();
                                foreach (var op in v.Items)
                                    if (!(excludeBlinkFix && op["op"].AsString == MeshOps.BlinkFix || excludeMouthFix && op["op"].AsString == MeshOps.MouthFix)) keep.Add(op);
                                v = keep;
                            }
                            if (v.Items.Any(MeshOps.IsOff)) { var on = JNode.Arr(); foreach (var op in v.Items) if (!MeshOps.IsOff(op)) on.Add(op); v = on; }   // 꺼 둔 다듬기는 올리지 않음
                            if (m == null || v.Count == 0) break;
                            meshes.Add(JNode.Obj().Set("key", a.Target).Set("name", m.Name).Set("vertexCount", m.VertexCount).Set("ops", v));
                            break;
                        }
                }
            }
            root.Set("nodes", nodes);
            var ra = JNode.Arr(); foreach (var r in renderers.Values) ra.Add(r); root.Set("renderers", ra);
            var ma = JNode.Arr(); foreach (var m in mats.Values) ma.Add(m); root.Set("materials", ma);
            root.Set("meshes", meshes).Set("textures", textures);
            root.Set("upload", JNode.Obj().Set("name", avatarName).Set("thumbnail", thumbnail).Set("releaseStatus", "private"));
            if (exportDir != null) root.Set("export", JNode.Obj().Set("dir", exportDir));
            return root;
        }
    }
}
