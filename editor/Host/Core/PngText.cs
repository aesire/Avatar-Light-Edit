// PNG 글 청크 (PLAN 3.7 "PNG tEXt 청크에 세션 임베드", 2026-10-20): 촬영 PNG에 세션(IR 경로 · Changeset · 카메라)을 넣어 두고,
// 그 이미지를 창에 끌어 놓으면 같은 상태로 다시 연다. 한글·긴 JSON이라 tEXt(Latin-1) 대신 압축한 iTXt(UTF-8)를 쓴다.
// 픽셀은 건드리지 않고 IHDR 바로 뒤에 청크 하나만 끼운다 (같은 키워드가 있으면 바꾼다). 다른 프로그램은 모르는 청크를 무시한다.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class PngText
    {
        /// <summary>세션을 넣는 iTXt 키워드.</summary>
        public const string SessionKeyword = "vrcproto-session";
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        static bool IsPng(byte[] f) { if (f.Length < 8) return false; for (int i = 0; i < 8; i++) if (f[i] != Signature[i]) return false; return true; }
        static int BE(byte[] f, int p) => (f[p] << 24) | (f[p + 1] << 16) | (f[p + 2] << 8) | f[p + 3];

        /// <summary>keyword의 iTXt(압축) 청크를 넣은 PNG. 같은 키워드의 iTXt·tEXt·zTXt는 뺀다. PNG가 아니면 그대로.</summary>
        public static byte[] Insert(byte[] png, string keyword, string text)
        {
            if (!IsPng(png)) return png;
            var outp = new MemoryStream(png.Length + text.Length / 2 + 64);
            outp.Write(Signature, 0, 8);
            int p = 8; bool done = false;
            while (p + 12 <= png.Length)
            {
                int len = BE(png, p); string type = Encoding.ASCII.GetString(png, p + 4, 4);
                if (len < 0 || p + 12 + len > png.Length) break;
                bool skip = (type == "iTXt" || type == "tEXt" || type == "zTXt") && KeywordOf(png, p + 8, len) == keyword;
                if (!skip) outp.Write(png, p, 12 + len);
                if (type == "IHDR" && !done) { Sticker.Chunk(outp, "iTXt", ITxt(keyword, text)); done = true; }
                p += 12 + len;
                if (type == "IEND") break;
            }
            return outp.ToArray();
        }

        /// <summary>keyword의 글 (iTXt 압축·비압축, tEXt). 없으면 null.</summary>
        public static string? Read(byte[] png, string keyword)
        {
            if (!IsPng(png)) return null;
            int p = 8;
            while (p + 12 <= png.Length)
            {
                int len = BE(png, p); string type = Encoding.ASCII.GetString(png, p + 4, 4);
                if (len < 0 || p + 12 + len > png.Length) return null;
                int d = p + 8;
                if ((type == "iTXt" || type == "tEXt") && KeywordOf(png, d, len) == keyword)
                {
                    int k = Array.IndexOf(png, (byte)0, d, len);
                    if (type == "tEXt") return Encoding.Latin1.GetString(png, k + 1, d + len - k - 1);
                    // iTXt: 키워드 0 압축여부 압축방식 언어태그 0 번역키워드 0 글
                    bool compressed = png[k + 1] == 1;
                    int lang = Array.IndexOf(png, (byte)0, k + 3, d + len - k - 3);
                    int trans = lang < 0 ? -1 : Array.IndexOf(png, (byte)0, lang + 1, d + len - lang - 1);
                    if (trans < 0) return null;
                    int start = trans + 1, n = d + len - start;
                    if (!compressed) return Encoding.UTF8.GetString(png, start, n);
                    using var z = new ZLibStream(new MemoryStream(png, start, n), CompressionMode.Decompress);
                    var ms = new MemoryStream(); z.CopyTo(ms);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
                if (type == "IDAT" || type == "IEND") return null;   // 글 청크는 IDAT 앞에 둔다 (뒤에 둔 것은 찾지 않음)
                p += 12 + len;
            }
            return null;
        }

        static string KeywordOf(byte[] f, int d, int len)
        {
            int k = Array.IndexOf(f, (byte)0, d, Math.Min(len, 80));
            return k < 0 ? "" : Encoding.Latin1.GetString(f, d, k - d);
        }

        static byte[] ITxt(string keyword, string text)
        {
            var ms = new MemoryStream();
            var kw = Encoding.Latin1.GetBytes(keyword);
            ms.Write(kw, 0, kw.Length);
            ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(0);   // 끝 · 압축함 · zlib
            ms.WriteByte(0); ms.WriteByte(0);                     // 언어 태그 없음 · 번역 키워드 없음
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) { var b = Encoding.UTF8.GetBytes(text); zs.Write(b, 0, b.Length); }
            z.Position = 0; z.CopyTo(ms);
            return ms.ToArray();
        }

        // ---------------- 세션 ----------------

        /// <summary>PNG 파일에 세션을 넣는다 (같은 파일을 덮어씀).</summary>
        public static void EmbedSession(string pngPath, Document doc, JNode? camera, JNode? studio = null)
        {
            var f = File.ReadAllBytes(pngPath);
            var tmp = pngPath + ".tmp";
            File.WriteAllBytes(tmp, Insert(f, SessionKeyword, (studio == null ? Session.ToJson(doc, camera) : Session.ToJson(doc, camera).Set("studio", studio)).ToJson(indent: false)));
            File.Replace(tmp, pngPath, null);
        }

        /// <summary>PNG에 든 세션 (없으면 null). IR 경로가 상대면 이미지 폴더 기준.</summary>
        public static Session.Loaded? ReadSession(string pngPath)
        {
            byte[] f;
            try { f = File.ReadAllBytes(pngPath); } catch { return null; }
            var s = Read(f, SessionKeyword);
            if (s == null) return null;
            try { return Session.FromJson(JNode.Parse(s), Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? "."); }
            catch { return null; }
        }
    }
}
