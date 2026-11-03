// 파일을 함께 담은 세션 (Host/Core/SessionPack.cs, 2026-11-03): 원본 없이 다른 PC에서 열기
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    static void SessionPackRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "aledit_pack_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            var cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
            var tex = Path.Combine(src, "a.png"); File.WriteAllBytes(tex, new byte[] { 1, 2, 3 });
            File.WriteAllText(tex + ".meta", "guid: texA\n");
            var baked = Path.Combine(src, "baked.png"); File.WriteAllBytes(baked, new byte[] { 4, 5 });
            var pkg = Path.Combine(src, "avatar.unitypackage"); File.WriteAllBytes(pkg, new byte[] { 9 });
            var irPath = Path.Combine(cache, "t.ir.json");
            File.WriteAllBytes(Path.Combine(cache, "t.ir.bin"), new byte[16]);
            var ir = JNode.Parse(@"{ ""irVersion"": 1, ""name"": ""t"", ""buffers"": ""t.ir.bin"", ""nodes"": [ { ""id"": 0, ""name"": ""Avatar"" } ],
              ""meshes"": [], ""renderers"": [], ""materials"": [], ""textures"": [ { ""guid"": ""texA"", ""path"": ""Assets/a.png"", ""importer"": {} } ] }");
            ir["textures"][0].Set("file", tex);
            ir.Set("source", JNode.Obj().Set("sources", JNode.Arr().Add(pkg)).Set("guid", "abc"));
            File.WriteAllText(irPath, ir.ToJson());

            var doc = new Document(IrScene.Load(irPath), irPath);
            doc.Set(Addr.TexAsset("bake1"), JNode.Obj().Set("file", baked).Set("path", "Baked/a"));
            var pack = Path.Combine(root, "out" + SessionPack.Extension);
            var res = SessionPack.Save(pack, doc, null);
            Eq(res.Files, 2, "texture + baked file packed");
            Check(!doc.IsDirty, "saved marks document clean");
            using (var z = ZipFile.OpenRead(pack))
            {
                var names = z.Entries.Select(e => e.FullName).ToList();
                Check(names.Contains("pack.json") && names.Contains("session.json") && names.Contains("avatar/t.ir.json") && names.Contains("avatar/t.ir.bin"), "pack layout");
                Check(names.Any(n => n.EndsWith("/a.png")) && names.Any(n => n.EndsWith("/a.png.meta")), "texture with .meta");
                Check(!names.Any(n => n.EndsWith(".unitypackage")), "original package not packed");
            }

            // 다른 PC처럼: 원본·캐시를 지우고 연다
            Directory.Delete(src, true); Directory.Delete(cache, true);
            var ex = SessionPack.ExtractDir(Path.Combine(root, "imports"), pack);
            Eq(SessionPack.ExtractDir(Path.Combine(root, "imports"), pack), ex, "same pack → same folder");
            var loaded = SessionPack.Open(pack, ex);
            Check(loaded.IrPath.StartsWith(Path.GetFullPath(ex)) && File.Exists(loaded.IrPath), "IR extracted");
            var scene = IrScene.Load(loaded.IrPath);
            Check(File.Exists(scene.Textures[0].File) && scene.Textures[0].File.StartsWith(Path.GetFullPath(ex)), "texture path rewritten to extracted file");
            Check(File.Exists(scene.Textures[0].File + ".meta"), ".meta next to texture");
            Check(File.Exists(scene.BufferFile), "buffer found next to IR");
            var back = new Document(scene, loaded.IrPath);
            back.LoadChangeset(loaded.Changeset);
            var bakedBack = back.Get(Addr.TexAsset("bake1"))["file"].AsString;
            Check(File.Exists(bakedBack) && bakedBack.StartsWith(Path.GetFullPath(ex)), "changeset file path rewritten");
            Eq(loaded.Source["sources"][0].AsString, pkg, "source (original package path) kept as written");
            Eq(scene.Raw["source"]["sources"][0].AsString, pkg, "IR source kept as written");

            // 다시 열어도 (이미 푼 폴더) 같은 결과
            var again = SessionPack.Open(pack, ex);
            Check(File.Exists(IrScene.Load(again.IrPath).Textures[0].File), "reopen over extracted folder");

            // 묶음이 아닌 zip
            var bad = Path.Combine(root, "bad.alexp");
            using (var z = ZipFile.Open(bad, ZipArchiveMode.Create)) z.CreateEntry("x.txt");
            bool threw = false; try { SessionPack.Open(bad, Path.Combine(root, "bad")); } catch (InvalidDataException) { threw = true; }
            Check(threw, "non-pack zip rejected");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
