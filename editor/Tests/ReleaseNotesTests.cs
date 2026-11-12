// 릴리스 글에서 화면 언어 부분 고르기 (Host/Core/ReleaseNotes.cs, 2026-11-12)
using VrcProto.Host.Core;

static partial class CoreTests
{
    static void ReleaseNotesLang()
    {
        // 언어 제목이 없는 글(0.1.0 · 0.1.1)은 통째로
        Eq(ReleaseNotes.ForLang("고친 것\r\n- 환경 탭\r\n", "ja"), "고친 것\n- 환경 탭", "no language headings → whole body");
        Eq(ReleaseNotes.ForLang(null, "en"), "", "null body");
        Eq(ReleaseNotes.ForLang("## 고친 것\n- a", "en"), "## 고친 것\n- a", "ordinary heading is not a language");

        var body = "sha256: abc\n\n## 한국어\n고친 것\n- 가\n\n## English\nFixed\n- a\n\n### 日本語\n修正\n- あ\n";
        Eq(ReleaseNotes.ForLang(body, "ko"), "sha256: abc\n\n고친 것\n- 가", "ko part with common head");
        Eq(ReleaseNotes.ForLang(body, "en"), "sha256: abc\n\nFixed\n- a", "en part");
        Eq(ReleaseNotes.ForLang(body, "ja"), "sha256: abc\n\n修正\n- あ", "ja part (### heading)");

        // 맞는 언어가 없으면 영어, 영어도 없으면 첫 부분
        Eq(ReleaseNotes.ForLang("## 한국어\n가\n## English\na", "ja"), "a", "missing language → English");
        Eq(ReleaseNotes.ForLang("## 한국어\n가\n## 日本語\nあ", "en"), "가", "no English → first part");
        Eq(ReleaseNotes.ForLang("## **English**\na\n## Japanese\nあ", "ja"), "あ", "bold heading · English names");
        // 부분 안의 다른 제목은 그대로 남는다
        Eq(ReleaseNotes.ForLang("## English\n### Fixed\n- a\n## 한국어\n### 고친 것\n- 가", "ko"), "### 고친 것\n- 가", "inner headings kept");
    }
}
