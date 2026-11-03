// 원본 TextureImporter의 PC 형식 (Protocol/TextureGpu.cs, 2026-11-02): 실제 아바타 .meta 모양을 줄여서 확인
using VrcProto.Protocol;

static partial class CoreTests
{
    static string Meta(string def, string sa, string top = "") =>
        "TextureImporter:\n  serializedVersion: 12\n  textureType: 0\n" + top + "  platformSettings:\n"
        + "  - serializedVersion: 3\n    buildTarget: DefaultTexturePlatform\n    maxTextureSize: 2048\n" + def
        + "  - serializedVersion: 3\n    buildTarget: Standalone\n    maxTextureSize: 2048\n" + sa
        + "  spriteSheet:\n    serializedVersion: 2\n";

    static string Plat(int fmt, int comp, int over) => $"    textureFormat: {fmt}\n    textureCompression: {comp}\n    crunchedCompression: 0\n    overridden: {over}\n";

    static void TextureGpuBasics()
    {
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(-1, 1, 0))), TextureGpu.Auto, "보통 압축");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 0, 0), Plat(-1, 1, 0))), TextureGpu.None, "압축 안 함 (Standalone 오버라이드 없음)");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 2, 0), Plat(-1, 1, 0))), TextureGpu.Hq, "고품질");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 3, 0), Plat(-1, 1, 0))), TextureGpu.Auto, "낮은 품질");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 2, 0), Plat(10, 1, 1))), TextureGpu.Dxt1, "Standalone DXT1");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 0, 0), Plat(25, 1, 1))), TextureGpu.Hq, "Standalone BC7");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(12, 1, 1))), TextureGpu.Dxt5, "Standalone DXT5");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(4, 1, 1))), TextureGpu.None, "Standalone RGBA32");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(10, 1, 0))), TextureGpu.Auto, "오버라이드가 꺼진 Standalone은 무시");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(-1, 0, 1))), TextureGpu.None, "Standalone 자동 + 압축 안 함");
        Eq(TextureGpu.FromMeta("TextureImporter:\n  textureFormat: -1\n  textureCompression: 0\n"), TextureGpu.None, "Unity 5 맨 위 값");
        Eq(TextureGpu.FromMeta(""), TextureGpu.Auto, "빈 .meta = Unity 기본");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 1, 0), Plat(-1, 1, 0)).Replace("\n", "\r\n")), TextureGpu.Auto, "CRLF");
        Eq(TextureGpu.FromMeta(Meta(Plat(-1, 2, 0), Plat(10, 1, 1)).Replace("\n", "\r\n")), TextureGpu.Dxt1, "CRLF 오버라이드");
        Check(TextureGpu.IsDxt(TextureGpu.Auto) && TextureGpu.IsDxt(TextureGpu.Dxt1) && TextureGpu.IsDxt(TextureGpu.Dxt5), "DXT 값");
        Check(!TextureGpu.IsDxt(TextureGpu.None) && !TextureGpu.IsDxt(TextureGpu.Hq) && !TextureGpu.IsDxt(null), "DXT가 아닌 값");
    }
}
