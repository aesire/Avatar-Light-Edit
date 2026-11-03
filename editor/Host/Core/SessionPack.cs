// 파일을 함께 담은 세션 (.alexp, 2026-11-03): 세션 + 가져온 아바타 IR(.ir.json·.ir.bin) + IR·바꾼 값이 가리키는 파일(텍스처와 그 .meta, 색 굽기 결과 등)을 zip 하나로.
// 다른 PC에서 원본 없이 열고 고치고 찍을 수 있게. 원본 패키지·Unity 프로젝트(IR·세션의 source)는 넣지 않는다 — 크고, 산 에셋 원본이 남에게 넘어가지 않게.
//   그래서 올리기·내보내기는 여전히 원본이 있는 PC에서.
// zip 구성: pack.json(형식·버전) · session.json(세션, ir = avatar/<이름>.ir.json) · avatar/<IR·버퍼> · files/<번호>/<파일 이름>(+ .meta)
//   안의 경로는 "@pack/files/…" 꼴로 적고, 열 때 푼 폴더의 절대 경로로 바꿔 IR·세션에 다시 쓴다 (IR 읽기 코드는 그대로).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class SessionPack
    {
        public const string Extension = ".alexp";
        public const string Format = "aledit-pack";
        public const int Version = 1;
        const string Token = "@pack/";

        /// <summary>담지 않는 파일: 원본 패키지·씬·프리팹 (source 밖에 적혀 있어도).</summary>
        static readonly string[] Skip = { ".unitypackage", ".unity", ".prefab", ".zip" };

        public static bool IsPack(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>담은 결과 (저장 화면에 알릴 수).</summary>
        public sealed class Result { public int Files; public long Bytes; public List<string> Missing = new List<string>(); }

        /// <summary>
        /// 세션 묶음을 쓴다. 담을 파일 = IR 버퍼 + IR·바꾼 값·변형에 적힌 절대 경로 중 있는 파일 (원본 패키지 제외, IR source 제외).
        /// 텍스처 옆 .meta(압축 설정)도 같이. 쓰는 동안 실패하면 원래 파일은 그대로 둔다.
        /// </summary>
        public static Result Save(string path, Document doc, JNode? camera)
        {
            var res = Save(path, Session.ToJson(doc, camera), doc.IrPath, doc.Original.Name);
            doc.MarkSaved();
            return res;
        }

        /// <summary>
        /// 문서를 읽지 않는 쪽 (창에서는 세션 JSON을 UI 스레드에서 만들고 이것만 뒤에서 돌린다). session = Session.ToJson 결과.
        /// </summary>
        public static Result Save(string path, JNode session, string irPathIn, string name)
        {
            var res = new Result();
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 원본 절대 경로 → zip 안 이름
            string Collect(string s)
            {
                if (!Bundleable(s)) return s;
                var full = Path.GetFullPath(s);
                if (!File.Exists(full)) { if (!res.Missing.Contains(full)) res.Missing.Add(full); return s; }
                if (!files.TryGetValue(full, out var entry))
                {
                    entry = $"files/{files.Count:D4}/{Path.GetFileName(full)}";
                    files[full] = entry;
                }
                return Token + entry;
            }

            var irPath = Path.GetFullPath(irPathIn);
            var irDir = Path.GetDirectoryName(irPath) ?? ".";
            var ir = JNode.Parse(File.ReadAllText(irPath));
            var irOut = Map(ir, Collect, skipRootKey: "source");
            var buffer = ir["buffers"].AsString;
            var bufferFull = buffer.Length > 0 ? (Path.IsPathRooted(buffer) ? buffer : Path.Combine(irDir, buffer)) : null;
            var irName = Path.GetFileName(irPath);
            if (bufferFull != null) irOut.Set("buffers", Path.GetFileName(bufferFull));

            var sessOut = JNode.Obj();
            foreach (var kv in session.Props)
                sessOut.Set(kv.Key, kv.Key is "changeset" or "variants" ? Map(kv.Value, Collect) : kv.Value);
            sessOut.Set("ir", "avatar/" + irName);

            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                Text(zip, "pack.json", JNode.Obj().Set("format", Format).Set("version", Version).Set("name", name).Set("created", DateTime.Now.ToString("o")));
                Text(zip, "session.json", sessOut);
                Text(zip, "avatar/" + irName, irOut);
                if (bufferFull != null) { zip.CreateEntryFromFile(bufferFull, "avatar/" + Path.GetFileName(bufferFull), CompressionLevel.Fastest); res.Bytes += new FileInfo(bufferFull).Length; }
                foreach (var kv in files)
                {
                    // 이미 압축된 이미지는 다시 압축해도 거의 줄지 않는다
                    var level = Path.GetExtension(kv.Key).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    zip.CreateEntryFromFile(kv.Key, kv.Value, level);
                    res.Files++; res.Bytes += new FileInfo(kv.Key).Length;
                    if (File.Exists(kv.Key + ".meta")) zip.CreateEntryFromFile(kv.Key + ".meta", kv.Value + ".meta", CompressionLevel.Fastest);
                }
            }
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return res;
        }

        /// <summary>
        /// 묶음을 outDir에 풀고(있던 것은 덮어씀) 경로를 그 폴더로 바꿔 쓴 뒤, 세션처럼 읽는다. IrPath = 푼 IR.
        /// zip 안 경로는 outDir 밖으로 나가지 못한다.
        /// </summary>
        public static Session.Loaded Open(string path, string outDir)
        {
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);
            using (var zip = ZipFile.OpenRead(path))
            {
                var pack = Entry(zip, "pack.json") ?? throw new InvalidDataException("ALEdit 묶음 파일이 아닙니다");
                if (pack["format"].AsString != Format) throw new InvalidDataException("ALEdit 묶음 파일이 아닙니다");
                if (pack["version"].AsInt > Version) throw new InvalidDataException("더 새 버전의 묶음 파일입니다. 프로그램을 업데이트해 주세요.");
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    var dst = Path.GetFullPath(Path.Combine(outDir, e.FullName));
                    if (!dst.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // ../ 막기
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst, true);
                }
            }
            string Resolve(string s) => s.StartsWith(Token, StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(outDir, s.Substring(Token.Length).Replace('/', Path.DirectorySeparatorChar))) : s;

            var sessPath = Path.Combine(outDir, "session.json");
            if (!File.Exists(sessPath)) throw new InvalidDataException("묶음에 세션이 없어요");
            var sess = JNode.Parse(File.ReadAllText(sessPath));
            var fixedSess = JNode.Obj();
            foreach (var kv in sess.Props) fixedSess.Set(kv.Key, kv.Key is "changeset" or "variants" ? Map(kv.Value, Resolve) : kv.Value);
            var loaded = Session.FromJson(fixedSess, outDir);
            if (!File.Exists(loaded.IrPath)) throw new InvalidDataException("묶음에 아바타 파일이 없어요");
            var ir = JNode.Parse(File.ReadAllText(loaded.IrPath));
            File.WriteAllText(loaded.IrPath, Map(ir, Resolve, skipRootKey: "source").ToJson());
            return loaded;
        }

        /// <summary>열 때 풀 폴더: 가져오기 폴더 아래 &lt;파일 이름&gt;_&lt;경로 해시&gt; (같은 파일을 다시 열면 같은 곳).</summary>
        public static string ExtractDir(string importsRoot, string packPath)
        {
            var full = Path.GetFullPath(packPath).ToLowerInvariant();
            uint h = 2166136261;
            foreach (var c in full) { h ^= c; h *= 16777619; }
            var stem = string.Concat(Path.GetFileNameWithoutExtension(packPath).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (stem.Length > 40) stem = stem.Substring(0, 40);
            return Path.Combine(importsRoot, "pack_" + stem + "_" + h.ToString("x8"));
        }

        static bool Bundleable(string s) =>
            s.Length > 3 && s.Length < 500 && s.IndexOf('\n') < 0 && Path.IsPathFullyQualified(s)
            && !Skip.Contains(Path.GetExtension(s).ToLowerInvariant())
            && !s.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase) && !s.EndsWith(".ir.bin", StringComparison.OrdinalIgnoreCase);

        /// <summary>문자열 값만 f로 바꾼 사본 (키는 그대로). skipRootKey: 맨 위의 이 키는 그대로 둔다.</summary>
        static JNode Map(JNode n, Func<string, string> f, string? skipRootKey = null)
        {
            switch (n.Kind)
            {
                case JKind.String: return JNode.From(f(n.AsString));
                case JKind.Array: { var a = JNode.Arr(); foreach (var x in n.Items) a.Add(Map(x, f)); return a; }
                case JKind.Object:
                {
                    var o = JNode.Obj();
                    foreach (var kv in n.Props) o.Set(kv.Key, kv.Key == skipRootKey ? kv.Value : Map(kv.Value, f));
                    return o;
                }
                default: return n;
            }
        }

        static void Text(ZipArchive zip, string name, JNode j)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
            w.Write(j.ToJson());
        }

        static JNode? Entry(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            using var r = new StreamReader(e.Open());
            return JNode.Parse(r.ReadToEnd());
        }
    }
}
