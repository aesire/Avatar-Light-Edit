using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ImportSim
{
    public sealed class AssetEntry
    {
        public string Guid = "", Path = "";
        public string Source = "";           // 패키지 파일 또는 프로젝트 폴더
        public string? FilePath;             // 프로젝트 폴더 소스일 때 원본 파일의 절대 경로
        internal Func<byte[]>? Loader;
        internal Func<string>? MetaLoader;
        byte[]? data;
        string? meta;

        public string Meta { get => meta ??= MetaLoader?.Invoke() ?? ""; set => meta = value; }

        public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public byte[]? Data => data ??= Loader?.Invoke();
        internal void SetData(byte[] d) => data = d;
    }

    /// <summary>guid -> 에셋. .unitypackage 와 Unity 프로젝트 Assets 폴더를 소스로 쓴다 (나중에 추가한 쪽이 우선).</summary>
    public sealed class AssetDb
    {
        public readonly Dictionary<string, AssetEntry> ByGuid = new Dictionary<string, AssetEntry>();

        /// 패키지를 열 때 본문까지 바로 읽어 두는 확장자 (나머지는 필요할 때 다시 읽음)
        static readonly HashSet<string> EagerExt = new HashSet<string> { ".prefab", ".fbx", ".mat", ".unity", ".asset" };

        public AssetEntry? Get(string guid) => ByGuid.TryGetValue(guid, out var a) ? a : null;

        // FBX 기하 해석 결과 (guid → 모델·오류). 본 합치기(종류 가늠)와 IR 쓰기가 같은 FBX를 한 번만 해석하도록 나눠 쓴다.
        // 여러 스레드에서 불러도 FBX마다 한 번만 해석한다. 기하는 크므로 IR을 다 쓰면 ClearGeometry로 비운다.
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(SimFile? Model, string? Error)>> _geometry =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(SimFile? Model, string? Error)>>();

        public (SimFile? Model, string? Error) Geometry(AssetEntry a) =>
            _geometry.GetOrAdd(a.Guid, _ => new Lazy<(SimFile?, string?)>(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { return (ModelSim.Build(a, geometry: true), null); }
                catch (Exception e) { return (null, $"{e.GetType().Name}: {e.Message}"); }
                finally { Timing.Detail($"FBX {a.Path}", sw.ElapsedMilliseconds); }
            })).Value;

        public void ClearGeometry() => _geometry.Clear();

        public void AddPackage(string packagePath)
        {
            var found = new Dictionary<string, AssetEntry>();
            AssetEntry E(string g) => found.TryGetValue(g, out var a) ? a : found[g] = new AssetEntry { Guid = g, Source = packagePath };
            static (string guid, string kind) Split(string name)
            {
                var p = name.TrimStart('.', '/').Split('/');
                return p.Length == 2 ? (p[0], p[1]) : ("", "");
            }

            // 1차: pathname / meta 만 (에셋 본문은 건너뜀)
            TarGz.Read(packagePath, (name, read) =>
            {
                var (g, kind) = Split(name);
                if (kind == "pathname") E(g).Path = Encoding.UTF8.GetString(read()).Split('\n')[0].Trim();
                else if (kind == "asset.meta") E(g).Meta = Encoding.UTF8.GetString(read());
            }, name => !name.EndsWith("/asset", StringComparison.Ordinal));

            // 2차: 구조 해석에 필요한 작은 에셋만 본문을 읽어 둔다
            TarGz.Read(packagePath, (name, read) =>
            {
                var (g, _) = Split(name);
                found[g].SetData(read());
            }, name =>
            {
                var (g, kind) = Split(name);
                return kind == "asset" && found.TryGetValue(g, out var a) && EagerExt.Contains(a.Ext);
            });

            foreach (var a in found.Values)
            {
                if (a.Path.Length == 0) continue;
                var g = a.Guid;
                a.Loader = () => TarGz.ReadOne(packagePath, g + "/asset");
                ByGuid[g] = a;
            }
        }

        /// <summary>
        /// 에셋 본문을 하나씩 넘긴다 (캐시하지 않음). 패키지 소스는 패키지마다 한 번만 훑는다.
        /// 큰 파일(텍스처 등)을 메모리에 쌓지 않고 처리할 때 쓴다.
        /// </summary>
        public void ReadEach(IEnumerable<AssetEntry> entries, Action<AssetEntry, byte[]> onData)
        {
            var byPackage = new Dictionary<string, Dictionary<string, AssetEntry>>();
            foreach (var a in entries)
            {
                if (a.FilePath != null) { onData(a, File.ReadAllBytes(a.FilePath)); continue; }
                if (a.Loader == null) continue;
                if (!byPackage.TryGetValue(a.Source, out var want)) byPackage[a.Source] = want = new Dictionary<string, AssetEntry>();
                want[a.Guid + "/asset"] = a;
            }
            foreach (var kv in byPackage)
                TarGz.Read(kv.Key, (name, read) => onData(kv.Value[name.TrimStart('.', '/')], read()),
                           name => kv.Value.ContainsKey(name.TrimStart('.', '/')));
        }

        static readonly string[] LooseImageExt = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".exr" };

        /// <summary>
        /// Unity 프로젝트 밖의 FBX 하나 (스튜디오 소품, 2026-10-27). .meta가 없으므로 Unity 기본 임포트 설정으로 읽고,
        /// guid는 경로에서 만든다. 텍스처는 FBX 폴더와 그 아래 폴더의 이미지에서 이름으로 찾는다 (내장 머티리얼 규칙과 같음).
        /// 다운로드 폴더처럼 큰 폴더에 있어도 오래 걸리지 않게 아래 폴더는 이미지 4000개까지만 본다.
        /// </summary>
        public void AddLooseModel(string fbxPath)
        {
            var full = System.IO.Path.GetFullPath(fbxPath);
            var dir = System.IO.Path.GetDirectoryName(full)!;
            var parent = System.IO.Path.GetDirectoryName(dir) ?? dir;
            void Add(string file)
            {
                var guid = LooseGuid(file);
                ByGuid[guid] = new AssetEntry
                {
                    Guid = guid, Path = System.IO.Path.GetRelativePath(parent, file).Replace('\\', '/'), Source = full, FilePath = file,
                    Loader = () => File.ReadAllBytes(file), MetaLoader = () => "",
                };
            }
            Add(full);
            // FBX 폴더부터 4단계 아래까지 너비 우선 (가까운 폴더가 먼저)
            var opts = new EnumerationOptions { IgnoreInaccessible = true };
            var level = new List<string> { dir };
            int count = 0;
            for (int depth = 0; depth <= 4 && level.Count > 0 && count < 4000; depth++)
            {
                var next = new List<string>();
                foreach (var d in level)
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(d, "*", opts))
                            if (LooseImageExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()) && count++ < 4000) Add(f);
                        next.AddRange(Directory.EnumerateDirectories(d, "*", opts));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                level = next;
            }
        }

        /// 프로젝트 밖 파일의 guid: 전체 경로(소문자)의 해시 두 개 (32자리)
        public static string LooseGuid(string file)
        {
            var b = Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(file).ToLowerInvariant());
            return Xxh64.Hash(b).ToString("x16") + Xxh64.Hash(b, 0x9E3779B97F4A7C15).ToString("x16");
        }

        static readonly Regex GuidRe = new Regex(@"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);

        /// <summary>Unity 프로젝트의 Assets 폴더 (읽기 전용).</summary>
        public void AddProjectFolder(string assetsDir)
        {
            var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(assetsDir).TrimEnd('\\', '/'))!;
            // 폴더를 한 번만 훑어 .meta와 짝 파일 유무·수정 시각을 함께 얻는다 (파일마다 File.Exists를 부르지 않음).
            var all = new DirectoryInfo(assetsDir).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
            var present = new HashSet<string>(all.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
            var metaFiles = all.Where(f => f.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
            var metas = metaFiles.Select(f => f.FullName).ToArray();
            var guids = new string?[metas.Length];
            // .meta의 guid 색인 캐시 (2026-10-21): 같은 프로젝트를 다시 가져올 때 바뀌지 않은 .meta(수정 시각·크기 같음)는 열지 않는다.
            // 2만~3만 개를 여는 데 2~3초 걸렸다. 캐시는 %LOCALAPPDATA%\ALEdit\meta-index\<폴더 해시>.tsv
            var cache = MetaIndexCache.Load(assetsDir);
            var fresh = new (long Ticks, long Len)[metas.Length];
            bool changed = cache.Count == 0;
            for (int i = 0; i < metas.Length; i++)
            {
                fresh[i] = (metaFiles[i].LastWriteTimeUtc.Ticks, metaFiles[i].Length);
                if (cache.TryGetValue(metas[i], out var c) && c.Ticks == fresh[i].Ticks && c.Len == fresh[i].Len) guids[i] = c.Guid;
            }
            // .meta 머리 읽기는 파일마다 독립이라 병렬로 (큰 프로젝트 2만~3만 개, 순서대로 읽으면 10초 넘게 걸렸다).
            // 같은 guid가 둘이면 예전처럼 뒤에 나온 것이 이기도록 결과는 찾은 순서대로 넣는다.
            System.Threading.Tasks.Parallel.For(0, metas.Length, () => new byte[512], (i, _, buf) =>
            {
                if (guids[i] != null) return buf;
                var meta = metas[i];
                changed = true;
                // guid 는 .meta 앞부분에 있으므로 앞 512바이트만 읽고, 전체는 필요할 때 읽는다
                try
                {
                    using var fs = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                    var m = GuidRe.Match(Encoding.UTF8.GetString(buf, 0, fs.Read(buf, 0, buf.Length)));
                    if (m.Success) guids[i] = m.Groups[1].Value;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return buf;
            }, _ => { });
            if (changed || cache.Count != metas.Length) MetaIndexCache.Save(assetsDir, metas, fresh, guids);
            // 짝 파일이 없는 .meta(폴더 .meta 등)는 빼고 넣는다 (캐시에는 남겨 둔다)
            for (int i = 0; i < metas.Length; i++)
                if (guids[i] == "" || !present.Contains(metas[i].Substring(0, metas[i].Length - 5))) guids[i] = null;
            for (int i = 0; i < metas.Length; i++)
            {
                if (guids[i] is not string guid) continue;
                string meta = metas[i], file = meta.Substring(0, meta.Length - 5);
                var rel = file.Substring(root.Length + 1).Replace('\\', '/');
                ByGuid[guid] = new AssetEntry
                {
                    Guid = guid, Path = rel, Source = assetsDir, FilePath = System.IO.Path.GetFullPath(file),
                    Loader = () => File.ReadAllBytes(file), MetaLoader = () => File.ReadAllText(meta),
                };
            }
        }
    }

    /// <summary>
    /// 프로젝트 폴더 .meta → guid 색인 캐시. 한 줄 = 수정 시각(UTC ticks) · 크기 · guid(못 읽었으면 빈 칸) · .meta 전체 경로 (탭으로 나눔).
    /// 읽기·쓰기에 실패하면 캐시 없이 동작한다 (가져오기 결과는 캐시와 무관하게 같다).
    /// </summary>
    static class MetaIndexCache
    {
        static string FileFor(string assetsDir)
        {
            var key = System.IO.Path.GetFullPath(assetsDir).TrimEnd('\\', '/').ToLowerInvariant();
            var hash = Xxh64.Hash(Encoding.UTF8.GetBytes(key)).ToString("x16");
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "meta-index");
            return System.IO.Path.Combine(dir, hash + ".tsv");
        }

        public static Dictionary<string, (long Ticks, long Len, string Guid)> Load(string assetsDir)
        {
            var d = new Dictionary<string, (long, long, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = FileFor(assetsDir);
                if (!File.Exists(path)) return d;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length == 4 && long.TryParse(p[0], out var t) && long.TryParse(p[1], out var n)) d[p[3]] = (t, n, p[2]);
                }
            }
            catch (Exception) { d.Clear(); }
            return d;
        }

        public static void Save(string assetsDir, string[] metas, (long Ticks, long Len)[] stamp, string?[] guids)
        {
            try
            {
                var path = FileFor(assetsDir);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var sb = new StringBuilder(metas.Length * 120);
                for (int i = 0; i < metas.Length; i++)
                    sb.Append(stamp[i].Ticks).Append('\t').Append(stamp[i].Len).Append('\t').Append(guids[i] ?? "").Append('\t').Append(metas[i]).Append('\n');
                var tmp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception) { }
        }
    }

    /// <summary>최소 tar.gz 리더 (ustar / GNU 긴 이름 / pax path).</summary>
    public static class TarGz
    {
        public static void Read(string path, Action<string, Func<byte[]>> onEntry, Func<string, bool>? want = null)
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var buf = new BufferedStream(gz, 1 << 16);
            var hdr = new byte[512];
            string? longName = null;
            while (ReadFull(buf, hdr, 512))
            {
                if (hdr[0] == 0) break;
                var name = Str(hdr, 0, 100);
                var prefix = Str(hdr, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
                long size = Convert.ToInt64(Str(hdr, 124, 12).Trim(' ', '\0').PadLeft(1, '0'), 8);
                char type = (char)hdr[156];
                long padded = (size + 511) / 512 * 512;

                if (type == 'L' || type == 'x')
                {
                    var body = new byte[padded];
                    ReadFull(buf, body, (int)padded);
                    var s = Encoding.UTF8.GetString(body, 0, (int)size);
                    if (type == 'L') longName = s.TrimEnd('\0');
                    else foreach (var rec in s.Split('\n'))
                        {
                            var i = rec.IndexOf(" path=", StringComparison.Ordinal);
                            if (i >= 0) longName = rec.Substring(i + 6);
                        }
                    continue;
                }
                if (longName != null) { name = longName; longName = null; }

                bool consumed = false;
                if ((type == '0' || type == '\0') && (want == null || want(name)))
                {
                    onEntry(name, () =>
                    {
                        consumed = true;
                        var d = new byte[size];
                        ReadFull(buf, d, (int)size);
                        Skip(buf, padded - size);
                        return d;
                    });
                }
                if (!consumed) Skip(buf, padded);
            }
        }

        public static byte[] ReadOne(string path, string entryName)
        {
            byte[]? result = null;
            try
            {
                Read(path, (n, read) => { if (n.TrimStart('.', '/') == entryName) { result = read(); throw new StopException(); } });
            }
            catch (StopException) { }
            return result ?? throw new FileNotFoundException(entryName);
        }

        sealed class StopException : Exception { }

        static string Str(byte[] b, int off, int len)
        {
            int end = off;
            while (end < off + len && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, off, end - off);
        }

        static bool ReadFull(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
            return true;
        }

        static readonly byte[] SkipBuf = new byte[1 << 16];
        static void Skip(Stream s, long n)
        {
            while (n > 0) { int r = s.Read(SkipBuf, 0, (int)Math.Min(n, SkipBuf.Length)); if (r <= 0) return; n -= r; }
        }
    }
}
