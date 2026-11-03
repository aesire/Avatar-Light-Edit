// 원본 TextureImporter가 PC(Windows)에서 GPU에 올리는 형식 (2026-11-02, 렌더러 메모리 줄이기).
// VRChat은 Unity가 빌드한 그대로 — 대부분 DXT1/DXT5로 압축해 올린다. 미리보기도 같은 텍스처를 같게 압축하면 가볍고 모습도 같다.
//   Standalone 칸이 overridden이면 그 textureFormat, 아니면 DefaultTexturePlatform의 textureFormat(-1 = 자동)과 textureCompression을 본다.
//   자동: textureCompression 0 = 압축 안 함, 2 = 고품질(BC7), 1·3 = DXT1(알파 없음)/DXT5(알파 있음).
//   값: none(압축 안 함) · auto(알파에 따라 DXT1/DXT5) · dxt1(알파를 버림) · dxt5 · hq(BC7 등 — 미리보기는 압축 없이, 화면은 거의 같다).
// 가져오기(importsim IrExport.Importer)가 IR에 "gpu"로 적고, 예전 IR은 IR을 읽을 때 원본 옆 .meta에서 바로 읽는다 (Ir.cs).
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace VrcProto.Protocol
{
    public static class TextureGpu
    {
        public const string None = "none", Auto = "auto", Dxt1 = "dxt1", Dxt5 = "dxt5", Hq = "hq";

        /// <summary>DXT로 압축해 올리는 값인지 (none·hq는 RGBA 그대로).</summary>
        public static bool IsDxt(string? gpu) => gpu == Auto || gpu == Dxt1 || gpu == Dxt5;

        /// <summary>.meta 글에서. 읽을 수 없으면 Unity 기본(자동·보통 압축)인 auto.</summary>
        public static string FromMeta(string meta)
        {
            if (string.IsNullOrEmpty(meta)) return Auto;
            string? defFmt = null, defComp = null, saFmt = null, saComp = null; bool saOver = false;
            // platformSettings 항목: "  - serializedVersion: n" 다음 줄들 (buildTarget·textureFormat·textureCompression·overridden)
            var blocks = Regex.Split(meta, @"\r?\n\s*- serializedVersion:[^\n]*");
            for (int i = 1; i < blocks.Length; i++)
            {
                var b = blocks[i];
                var target = Val(b, "buildTarget");
                if (target == "DefaultTexturePlatform") { defFmt = Val(b, "textureFormat"); defComp = Val(b, "textureCompression"); }
                else if (target == "Standalone") { saFmt = Val(b, "textureFormat"); saComp = Val(b, "textureCompression"); saOver = Val(b, "overridden") == "1"; }
            }
            if (saOver) return FromFormat(saFmt, saComp ?? defComp);
            if (defFmt != null || defComp != null) return FromFormat(defFmt, defComp);
            return FromFormat(Val(meta, "textureFormat"), Val(meta, "textureCompression"));   // 아주 예전 .meta (Unity 5): 맨 위에 있다
        }

        /// <summary>원본 이미지 파일 옆 .meta에서 (없으면 auto).</summary>
        public static string FromFile(string imageFile)
        {
            try
            {
                var m = imageFile + ".meta";
                return File.Exists(m) ? FromMeta(File.ReadAllText(m)) : Auto;
            }
            catch { return Auto; }
        }

        // TextureImporterFormat 번호: -1 자동 · 10 DXT1 · 12 DXT5 · 28/29 DXT1/DXT5 Crunched · 24 BC6H · 25 BC7 · 그 밖(RGBA32·RGB24·Alpha8·R8·BC4·BC5 …)은 압축 안 함으로 본다
        static string FromFormat(string? fmt, string? comp)
        {
            switch (fmt ?? "-1")
            {
                case "-1":
                case "": return comp == "0" ? None : comp == "2" ? Hq : Auto;
                case "10": case "28": return Dxt1;
                case "12": case "29": return Dxt5;
                case "24": case "25": return Hq;
                default: return None;
            }
        }

        static string? Val(string block, string key)
        {
            var m = Regex.Match(block, @"^\s*" + key + @":\s*(-?\w+)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
