// 원본 이미지 파일을 디코드만 해서 Texture2D로 만든다 (PLAN 3.1: 툴 자체의 재압축·변환 없음 — DXT 압축은 원본 임포트 설정이 압축하는 텍스처(기본 "원본 설정대로")나 사용자가 고른 경우만, TexCache).
// .meta 임포트 설정(sRGB, 노멀맵, wrap/filter/aniso, 밉맵, maxSize)은 Unity 에디터와 같게 반영한다.
// 지원: PNG/JPG(LoadImage), TGA(무압축·RLE)·PSD 합성 이미지·BMP(Protocol/ImageDecode, 2026-10-27).
//
// 로드 시간 (2026-10-19): 장면을 불러올 때 Prefetch가 장면의 텍스처를 작업 스레드 여러 개에서 미리 읽고 PNG를 디코드해
// maxSize로 줄여 둔다 (PngDecoder). JPG·TGA·인터레이스 PNG는 파일만 미리 읽고 메인 스레드에서 예전처럼 디코드한다.
// 로드 시간 2차 (2026-10-23):
//   · 머티리얼은 Request로 텍스처를 부탁만 하고(준비 안 됐으면 비워 둠), 장면 로드는 PhysBone·자세까지 준비한 뒤 Flush로 모두 넣고 보인다
//     (먼저 보이고 나중에 채우는 방식은 사용자 결정으로 되돌림 — 기다리는 동안 디코드가 PhysBone·자세 준비와 겹치는 것만 남김).
//     장면을 보인 뒤 바꾼 텍스처는 Pump가 프레임마다 시간 예산 안에서 올린다. 촬영·화면 저장 전에는 Flush로 모두 기다린다.
//   · 밉맵은 작업 스레드에서 만든다 (예전엔 메인 스레드 Apply(updateMipmaps)).
//   · 디스크 캐시 (TexCache): 줄이고 밉맵까지 만든 결과를 저장해 두고 다음엔 PNG를 풀지 않는다.
//   · 장면을 다시 불러와도(아이템 붙이기·떼기) 같은 텍스처는 그대로 둔다 (BeginScene/EndScene — 새 장면에 없는 것만 내린다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed class TextureLoader
    {
        /// <summary>로드 시간 재기 (RenderServer가 로그에 남기고 0으로).</summary>
        public static double WaitMs, UploadMs; public static int Decoded;
        /// <summary>기다리던 텍스처를 올릴 때마다 (올린 수, 기다리는 전체 수) — 로드 진행 표시.</summary>
        public Action<int, int> Progress;
        int _waitTotal, _waitDone;

        static readonly List<TextureLoader> Loaders = new List<TextureLoader>();
        public TextureLoader() { lock (Loaders) Loaders.Add(this); }

        /// <summary>작업 스레드에서 만든 것: 올릴 데이터(밉맵 사슬, RGBA32 또는 DXT) 또는 메인 스레드에서 디코드할 파일 바이트.</summary>
        sealed class Prepared { public NativeArray<byte> Data; public TexCache.Fmt Fmt; public int W, H, Mips; public byte[] File; public string CacheFile; public bool Fresh; public string Error; }   // Fresh: 새로 만든 것 → 올린 뒤 캐시에 쓴다   // Data: 관리되지 않는 메모리 — 올린 뒤 Dispose

        sealed class Entry
        {
            public IrTexture Ir; public Task<Prepared> Task; public Texture2D Tex; public bool Built;
            public List<Action<Texture2D>> Waiters;
        }
        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly HashSet<string> _used = new HashSet<string>();
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(Math.Max(2, Math.Min(12, Environment.ProcessorCount - 2)));   // 4096² PNG 하나에 잠깐 150 MB쯤(관리되지 않는 메모리) 쓰므로 12개까지

        /// <summary>같은 텍스처인지: guid·파일·임포트 설정이 모두 같아야 다시 쓴다</summary>
        static string KeyOf(IrTexture t)
        {
            var i = t.Importer;
            return $"{t.Guid}|{t.File}|{i.MaxSize}|{i.Mipmaps}|{i.SRgb}|{i.NormalMap}|{i.Wrap}|{i.Filter}|{i.Aniso}|{i.Gpu}";
        }

        /// <summary>모두 내린다 (스튜디오 소품을 내릴 때).</summary>
        public void Clear()
        {
            foreach (var e in _entries.Values) Drop(e);
            _entries.Clear(); _used.Clear();
            _waitTotal = _waitDone = 0;
        }

        static void Drop(Entry e)
        {
            if (e.Tex != null) UnityEngine.Object.Destroy(e.Tex);
            // 끝나지 않은 작업은 결과를 버린다 (준비한 픽셀은 관리되지 않는 메모리라 끝나는 대로 푼다)
            e.Task?.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion && t.Result.Data.IsCreated) t.Result.Data.Dispose(); });
            e.Waiters = null;
        }

        /// <summary>장면을 새로 불러오기 시작: 다음 Prefetch에 든 텍스처만 남긴다 (EndScene에서).</summary>
        public void BeginScene()
        {
            _used.Clear(); _waitTotal = _waitDone = 0; TexCache.Hits = TexCache.Misses = 0;
            foreach (var e in _entries.Values) e.Waiters = null;   // 앞 장면의 머티리얼은 이미 없다
        }

        /// <summary>장면을 다 만든 뒤: 새 장면에 없는 텍스처를 내린다.</summary>
        public void EndScene()
        {
            var gone = new List<string>();
            foreach (var kv in _entries) if (!_used.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone) { Drop(_entries[k]); _entries.Remove(k); }
            TexCache.PruneLater();
        }

        /// <summary>장면의 텍스처를 작업 스레드에서 미리 준비한다 (이미 있는 것은 그대로).</summary>
        public void Prefetch(IEnumerable<IrTexture> textures)
        {
            foreach (var t in textures) if (t != null) { _used.Add(KeyOf(t)); Ensure(t); }
        }

        Entry Ensure(IrTexture t)
        {
            var key = KeyOf(t);
            if (_entries.TryGetValue(key, out var e)) return e;
            var mode = TexCache.Mode;
            e = new Entry { Ir = t, Task = Task.Run(() => { Gate.Wait(); try { return Prepare(t, mode); } finally { Gate.Release(); } }) };
            _entries[key] = e;
            return e;
        }

        /// <summary>
        /// 텍스처를 부탁한다: 준비됐으면 바로 돌려주고, 아니면 null을 돌려주고 올린 뒤 ready를 부른다 (Pump·Flush에서, 한 번).
        /// 실패한 텍스처는 null (경고는 올릴 때 한 번).
        /// </summary>
        public Texture2D Request(IrTexture t, Action<string> warn, Action<Texture2D> ready)
        {
            if (t == null) return null;
            var e = Ensure(t);
            if (e.Built) return e.Tex;
            if (e.Waiters == null) { e.Waiters = new List<Action<Texture2D>>(); _waitTotal++; }
            e.Waiters.Add(ready);
            _warn = warn;
            return null;
        }
        Action<string> _warn = _ => { };

        /// <summary>기다리지 않고 지금 바로 (예전 방식, 이제 쓰는 곳 없음 — 남겨 둠).</summary>
        public Texture2D Get(IrTexture t, Action<string> warn)
        {
            if (t == null) return null;
            var e = Ensure(t);
            if (!e.Built) { _warn = warn; Build(e); }
            return e.Tex;
        }

        /// <summary>준비가 끝난 텍스처를 예산(ms) 안에서 올린다 (적어도 하나). 기다리는 쪽이 있는 것만.</summary>
        public void Pump(double budgetMs)
        {
            if (_waitDone >= _waitTotal) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<Entry> ready = null;
            foreach (var e in _entries.Values) if (!e.Built && e.Waiters != null && e.Task != null && e.Task.IsCompleted) (ready ??= new List<Entry>()).Add(e);
            if (ready == null) return;
            foreach (var e in ready)
            {
                Build(e);
                if (sw.Elapsed.TotalMilliseconds > budgetMs) break;
            }
        }

        /// <summary>기다리는 텍스처를 모두 올린다 (촬영·화면 저장 전: 찍은 결과가 늘 같게).</summary>
        public void Flush()
        {
            if (_waitDone >= _waitTotal) return;
            foreach (var e in new List<Entry>(_entries.Values)) if (!e.Built && e.Waiters != null) Build(e);
        }

        public static void PumpAll(double budgetMs) { lock (Loaders) foreach (var l in Loaders) l.Pump(budgetMs); }
        public static void FlushAll() { lock (Loaders) foreach (var l in Loaders) l.Flush(); }
        public static bool Busy { get { lock (Loaders) foreach (var l in Loaders) if (l._waitDone < l._waitTotal) return true; return false; } }

        void Build(Entry e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Prepared prep;
            try { prep = e.Task != null ? e.Task.Result : Prepare(e.Ir, TexCache.Mode); }
            catch (Exception ex) { prep = new Prepared { Error = $"텍스처 {e.Ir.Path}: {ex.Message}" }; }
            WaitMs += sw.Elapsed.TotalMilliseconds;
            var up = System.Diagnostics.Stopwatch.StartNew();
            Texture2D tex = null;
            try { tex = BuildTexture(e.Ir, prep, _warn); }
            catch (Exception ex) { _warn($"텍스처 {e.Ir.Path}: {ex.Message}"); }
            finally
            {
                if (prep.Data.IsCreated)
                {
                    // 새로 만든 것은 올린 뒤 작업 스레드에서 캐시에 쓰고 푼다 (LoadRawTextureData가 이미 복사했다)
                    if (prep.Fresh && prep.CacheFile != null && tex != null) WriteLater(prep);
                    else prep.Data.Dispose();
                }
            }
            UploadMs += up.Elapsed.TotalMilliseconds; Decoded++;
            e.Tex = tex; e.Built = true; e.Task = null;
            var waiters = e.Waiters; e.Waiters = null;
            if (waiters == null) return;
            foreach (var w in waiters) { try { w(tex); } catch (Exception ex) { Debug.LogException(ex); } }
            _waitDone++;
            Progress?.Invoke(_waitDone, _waitTotal);
        }

        static unsafe void WriteLater(Prepared p) => Task.Run(() =>
        {
            try { TexCache.Write(p.CacheFile, p.Fmt, p.W, p.H, p.Mips, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(p.Data), p.Data.Length); }
            finally { p.Data.Dispose(); }
        });

        /// <summary>작업 스레드: 캐시에서 읽거나, 파일을 읽고 PNG면 디코드·줄이기·밉맵(·DXT)까지 하고 캐시에 쓴다. Unity API는 쓰지 않는다.</summary>
        static unsafe Prepared Prepare(IrTexture t, string mode)
        {
            var p = new Prepared();
            try
            {
                if (!File.Exists(t.File)) { p.Error = "텍스처 파일 없음: " + t.Path; return p; }
                var imp = t.Importer;
                p.CacheFile = TexCache.FileFor(t, mode);
                if (TexCache.TryRead(p.CacheFile, out p.Fmt, out p.W, out p.H, out p.Mips, out p.Data)) { Interlocked.Increment(ref TexCache.Hits); return p; }
                if (p.CacheFile != null) Interlocked.Increment(ref TexCache.Misses);
                if (Path.GetExtension(t.File).ToLowerInvariant() == ".png")
                {
                    // PNG는 파일도 관리되지 않는 버퍼로 읽는다 (관리 메모리 byte[]는 Mono 힙을 키운 채 남는다)
                    byte* f = PngDecoder.ReadFile(t.File, out var len);
                    NativeArray<byte> rgba = default;
                    bool ok;
                    try { ok = PngDecoder.TryDecode(f, len, imp.MaxSize, out p.W, out p.H, out rgba); }
                    finally { UnsafeUtility.Free(f, Allocator.Persistent); }
                    if (ok)
                    {
                        if (imp.Mipmaps) { p.Data = TexCache.BuildMips(rgba, p.W, p.H, out p.Mips); rgba.Dispose(); }
                        else { p.Data = rgba; p.Mips = 1; }
                        p.Fmt = TexCache.Fmt.Rgba32;
                        if (TexCache.Compress(imp, mode) && p.W % 4 == 0 && p.H % 4 == 0)
                        {
                            var enc = TexCache.EncodeFor(imp, mode, p.Data, p.W, p.H, p.Mips, out p.Fmt);
                            p.Data.Dispose(); p.Data = enc;
                        }
                        p.Fresh = true;   // 캐시 쓰기는 GPU에 올린 뒤 따로 (장면 로드를 기다리게 하지 않게)
                        return p;
                    }
                }
                p.File = File.ReadAllBytes(t.File);   // JPG·TGA·PSD·BMP·인터레이스 PNG: 메인 스레드에서 디코드
            }
            catch (Exception e) { if (p.Data.IsCreated) p.Data.Dispose(); p.Data = default; p.Error = $"텍스처 {t.Path}: {e.Message}"; }
            return p;
        }

        /// <summary>메인 스레드: 준비한 데이터를 GPU에 올리거나, 파일 바이트를 예전처럼 디코드한다.</summary>
        static unsafe Texture2D BuildTexture(IrTexture t, Prepared p, Action<string> warn)
        {
            if (p.Error != null) { warn(p.Error); return null; }
            var imp = t.Importer;
            bool linear = !imp.SRgb || imp.NormalMap; // 노멀맵은 항상 Linear
            Texture2D tex;
            if (p.Data.IsCreated)
            {
                var tf = p.Fmt == TexCache.Fmt.Dxt1 ? TextureFormat.DXT1 : p.Fmt == TexCache.Fmt.Dxt5 ? TextureFormat.DXT5 : TextureFormat.RGBA32;
                tex = new Texture2D(p.W, p.H, tf, p.Mips, linear);
                tex.LoadRawTextureData(p.Data);
                Settings(tex, t);
                tex.Apply(false, true); // 밉맵은 이미 들어 있다. GPU 업로드 후 CPU 사본 해제
                return tex;
            }
            var ext = Path.GetExtension(t.File).ToLowerInvariant();
            switch (ext)
            {
                case ".png": case ".jpg": case ".jpeg":
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, imp.Mipmaps, linear);
                    if (!tex.LoadImage(p.File, false)) throw new InvalidDataException("디코드 실패");
                    break;
                case ".tga": case ".psd": case ".bmp": case ".tif": case ".tiff": case ".exr":
                    // 공용 디코더 (Protocol/ImageDecode.cs, 2026-10-27 PSD·BMP 더함 — 에디터 썸네일과 같은 코드)
                    var rgba = VrcProto.Protocol.ImageDecode.Decode(p.File, ext, true, out var w, out var h);
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, imp.Mipmaps, linear);
                    tex.SetPixelData(rgba, 0);
                    break;
                default:
                    warn($"아직 지원하지 않는 텍스처 형식 {ext}: {t.Path}");
                    return null;
            }
            if (imp.MaxSize > 0 && Math.Max(tex.width, tex.height) > imp.MaxSize) tex = Downscale(tex, imp.MaxSize, imp.Mipmaps, linear);
            Settings(tex, t);
            tex.Apply(imp.Mipmaps, false);
            // 메인 스레드에서 디코드한 것도 RGBA32면 캐시에 남긴다 (다음엔 작업 스레드에서 읽기만).
            // 압축할 것이면 작업 스레드에서 압축해 남긴다 (2026-11-02) — 이번에는 RGBA로 보이고 다음에 열 때부터 압축.
            if (p.CacheFile != null && tex.format == TextureFormat.RGBA32)
            {
                var raw = tex.GetRawTextureData<byte>();
                string mode = TexCache.Mode;
                if (TexCache.Compress(imp, mode) && tex.width % 4 == 0 && tex.height % 4 == 0)
                {
                    var copy = new NativeArray<byte>(raw, Allocator.Persistent);
                    int w = tex.width, h = tex.height, mips = tex.mipmapCount; string file = p.CacheFile;
                    Task.Run(() =>
                    {
                        try
                        {
                            var enc = TexCache.EncodeFor(imp, mode, copy, w, h, mips, out var fmt);
                            try { TexCache.Write(file, fmt, w, h, mips, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(enc), enc.Length); }
                            finally { enc.Dispose(); }
                        }
                        finally { copy.Dispose(); }
                    });
                }
                else TexCache.Write(p.CacheFile, TexCache.Fmt.Rgba32, tex.width, tex.height, tex.mipmapCount, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw), raw.Length);
            }
            tex.Apply(false, true); // CPU 사본 해제
            return tex;
        }

        static void Settings(Texture2D tex, IrTexture t)
        {
            var imp = t.Importer;
            tex.name = Path.GetFileName(t.Path);
            tex.wrapMode = imp.Wrap == 1 ? TextureWrapMode.Clamp : imp.Wrap == 2 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;
            tex.filterMode = imp.Filter == 0 ? FilterMode.Point : imp.Filter == 2 ? FilterMode.Trilinear : FilterMode.Bilinear;
            tex.anisoLevel = imp.Aniso;
        }

        /// <summary>Unity의 maxTextureSize와 같은 효과 (긴 변을 maxSize로, 비율 유지). 메인 스레드에서 디코드한 것(JPG·TGA 등)만.</summary>
        static Texture2D Downscale(Texture2D src, int maxSize, bool mips, bool linear)
        {
            float s = (float)maxSize / Math.Max(src.width, src.height);
            int w = Math.Max(1, Mathf.RoundToInt(src.width * s)), h = Math.Max(1, Mathf.RoundToInt(src.height * s));
            src.Apply(false, false);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, mips, linear);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(src);
            return dst;
        }
    }
}
