// 다른 색 텍스처 찾기 → IR 선택 항목 textureVariants (editor/Protocol/Ir.cs IrTextureVariant).
//   textureVariants: [{ of(IR 텍스처 키), guid, path(에셋 경로), file(풀어 둔 이미지, IR 기준 상대 또는 절대), label(다른 색 이름), ofLabel(지금 텍스처 쪽 이름) }]
// 헤어·의상 패키지는 같은 UV에 색만 다른 텍스처를 함께 넣는 일이 많다 (Hair_brown.png · Hair_pink.png, Black/Hair.png · Pink/Hair.png).
// 머티리얼이 쓰는 텍스처마다 같은 패키지 폴더에서 이름이 한두 마디만 다른 이미지를 찾는다. 노멀·마스크처럼 쓰임이 다른 것은 짝짓지 않는다.
// 머티리얼·프리팹은 바꾸지 않는다 (에디터가 머티리얼 텍스처 값만 바꿔 미리 본다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VrcProto.Protocol;

namespace ImportSim
{
    public static class TextureVariants
    {
        public sealed class Found
        {
            public string Of = "", Label = "", OfLabel = "";
            public AssetEntry Asset = null!;
        }

        const int MaxPerTexture = 32;
        static readonly HashSet<string> ImageExt = new HashSet<string> { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".exr", ".hdr" };
        // 텍스처 쓰임을 말하는 마디: 이것이 다르면 다른 색이 아니라 다른 맵이다
        static readonly HashSet<string> RoleWords = new HashSet<string>
        {
            "normal", "nrm", "nor", "norm", "n", "mask", "msk", "shadow", "sdw", "emission", "emi", "emissive", "emit", "matcap", "mc", "outline", "ol", "rim",
            "ao", "occlusion", "alpha", "spec", "specular", "metallic", "metal", "roughness", "rough", "smooth", "smoothness", "height", "bump", "sdf", "line",
            "hilight", "highlight", "glitter", "anisotropy", "aniso", "detail", "noise", "id", "uv",
        };
        static readonly Regex Split = new Regex(@"[_\-\s\.]+|(?<=[a-zA-Z])(?=[0-9])|(?<=[0-9])(?=[a-zA-Z])", RegexOptions.Compiled);

        static List<string> Tokens(string name) => Split.Split(name).Where(t => t.Length > 0).ToList();
        static string Low(string s) => s.ToLowerInvariant();
        static string Dir(string path) { var i = path.LastIndexOf('/'); return i < 0 ? "" : path.Substring(0, i); }
        static bool IsNormal(AssetEntry a) => Regex.IsMatch(a.Meta, @"^\s*textureType:\s*1\s*$", RegexOptions.Multiline);

        /// <summary>keys(IR 텍스처 키 = guid 또는 guid:fileID)마다 다른 색 후보. 같은 이미지가 여러 텍스처의 후보면 각각 넣는다.</summary>
        public static List<Found> Find(AssetDb db, IEnumerable<string> keys)
        {
            var images = db.ByGuid.Values.Where(a => ImageExt.Contains(a.Ext)).ToList();
            var outList = new List<Found>();
            foreach (var key in keys.Distinct())
            {
                var t = db.Get(key.Split(':')[0]);
                if (t == null || !ImageExt.Contains(t.Ext)) continue;
                var dir = Dir(t.Path);
                // 같은 패키지 폴더: 텍스처 폴더의 부모까지 (Hair/Texture/a.png → Hair/ 아래 전부). Assets 바로 아래면 그 폴더만
                var fam = dir.Count(ch => ch == '/') >= 2 ? Dir(dir) : dir;
                var tTok = Tokens(t.Name);
                var tRole = new HashSet<string>(tTok.Select(Low).Where(RoleWords.Contains));
                bool tNormal = IsNormal(t);
                var found = new List<Found>();
                foreach (var c in images)
                {
                    if (c.Guid == t.Guid || !c.Path.StartsWith(fam + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = Match(t, tTok, tRole, c, dir);
                    if (m == null || IsNormal(c) != tNormal) continue;
                    found.Add(new Found { Of = key, Asset = c, Label = m.Value.Label, OfLabel = m.Value.OfLabel });
                }
                // 같은 이름이 여러 개면(같은 이미지를 두 곳에) 하나만
                foreach (var f in found.GroupBy(x => Low(x.Label)).Select(g => g.OrderBy(x => x.Asset.Path.Length).First()).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).Take(MaxPerTexture))
                    outList.Add(f);
            }
            return outList;
        }

        /// 이름이 한두 마디만 다르거나(같은 폴더 근처), 이름은 같고 폴더만 다르면 짝 — (다른 색 이름, 지금 쪽 이름)
        static (string Label, string OfLabel)? Match(AssetEntry t, List<string> tTok, HashSet<string> tRole, AssetEntry c, string tDir)
        {
            var cDir = Dir(c.Path);
            if (string.Equals(c.Name, t.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(cDir, tDir, StringComparison.OrdinalIgnoreCase)) return null;   // 확장자만 다른 같은 그림
                // 폴더 이름이 색: Black/Hair.png ↔ Pink/Hair.png → 다른 마디
                var a = tDir.Split('/'); var b = cDir.Split('/');
                if (a.Length != b.Length) return null;
                var diffA = new List<string>(); var diffB = new List<string>();
                for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) { diffA.Add(a[i]); diffB.Add(b[i]); }
                if (diffB.Count == 0 || diffB.Count > 2) return null;
                return (string.Join(" ", diffB), string.Join(" ", diffA));
            }
            var cTok = Tokens(c.Name);
            var cRole = new HashSet<string>(cTok.Select(Low).Where(RoleWords.Contains));
            if (!cRole.SetEquals(tRole)) return null;
            if (cTok.Count == tTok.Count)
            {
                var dc = new List<string>(); var dt = new List<string>(); int same = 0;
                for (int i = 0; i < cTok.Count; i++)
                    if (string.Equals(cTok[i], tTok[i], StringComparison.OrdinalIgnoreCase)) same++;
                    else { dc.Add(cTok[i]); dt.Add(tTok[i]); }
                if (same == 0 || dc.Count == 0 || dc.Count > 2) return null;
                return (string.Join(" ", dc), string.Join(" ", dt));
            }
            // 기본 이름에 색 마디 하나가 붙은 것: Hair ↔ Hair_Pink (어느 쪽이든)
            if (Math.Abs(cTok.Count - tTok.Count) == 1)
            {
                var (longer, shorter) = cTok.Count > tTok.Count ? (cTok, tTok) : (tTok, cTok);
                for (int skip = 0; skip < longer.Count; skip++)
                {
                    bool ok = true;
                    for (int i = 0, j = 0; i < longer.Count; i++)
                    {
                        if (i == skip) continue;
                        if (!string.Equals(longer[i], shorter[j++], StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
                    }
                    if (!ok || shorter.Count == 0) continue;
                    var extra = longer[skip];
                    return cTok.Count > tTok.Count ? (extra, "기본") : ("기본", extra);
                }
            }
            return null;
        }

        /// <summary>
        /// 찾은 후보를 dir에 풀어 두고(패키지 안 이미지는 guid + 확장자, 프로젝트 파일은 그 자리 그대로) JSON 배열로.
        /// relTo: 파일 경로를 이 폴더 기준 상대 경로로 적는다 (IR 안에 쓸 때). null이면 절대 경로.
        /// </summary>
        public static JNode Extract(AssetDb db, List<Found> found, string dir, string? relTo, List<string> warnings)
        {
            var arr = JNode.Arr();
            var need = new Dictionary<string, AssetEntry>();
            foreach (var f in found) if (f.Asset.FilePath == null) need[f.Asset.Guid] = f.Asset;
            var done = new HashSet<string>();
            if (need.Count > 0)
            {
                Directory.CreateDirectory(dir);
                db.ReadEach(need.Values, (a, data) =>
                {
                    var file = Path.Combine(dir, a.Guid + a.Ext);
                    if (!File.Exists(file) || new FileInfo(file).Length != data.Length) File.WriteAllBytes(file, data);
                    lock (done) done.Add(a.Guid);
                });
            }
            foreach (var f in found)
            {
                string file;
                if (f.Asset.FilePath != null) file = f.Asset.FilePath;
                else if (done.Contains(f.Asset.Guid)) file = Path.Combine(dir, f.Asset.Guid + f.Asset.Ext);
                else { warnings.Add($"다른 색 텍스처를 패키지에서 꺼내지 못함: {f.Asset.Path}"); continue; }
                if (relTo != null && f.Asset.FilePath == null) file = Path.GetRelativePath(relTo, file).Replace('\\', '/');
                arr.Add(JNode.Obj().Set("of", f.Of).Set("guid", f.Asset.Guid).Set("path", f.Asset.Path).Set("file", file).Set("label", f.Label).Set("ofLabel", f.OfLabel));
            }
            return arr;
        }
    }
}
