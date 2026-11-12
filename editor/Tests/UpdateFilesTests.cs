// 업데이트가 남기는 파일 치우기 (Host/Core/UpdateFiles.cs, 2026-11-12)
using VrcProto.Host.Core;

static partial class CoreTests
{
    static void UpdateFilesStale()
    {
        var oldList = new[] { "ALEdit.exe", "Old.dll", "Renderer/Data/old.bundle", "texts\\en.tsv", "old.dll", "", "# 주석" };
        var newList = new[] { "ALEdit.exe", "texts/en.tsv", "New.dll" };
        Eq(string.Join("|", UpdateFiles.Stale(oldList, newList)), "Old.dll|Renderer\\Data\\old.bundle", "only files gone from the new list (case-insensitive, once)");
        Eq(UpdateFiles.Stale(oldList, new string[0]).Count, 0, "new version without a list → delete nothing");
        Eq(UpdateFiles.Stale(null, newList).Count, 0, "current version without a list → delete nothing");

        // 프로그램 폴더 밖을 가리키거나 배치 파일에서 뜻이 달라지는 줄은 버린다
        foreach (var bad in new[] { "..\\x.dll", "a\\..\\..\\x", "C:\\Windows\\x.dll", "\\\\server\\x", "\\x.dll", "a\\\\b", "x%TEMP%.dll", "a&b.dll", "*.dll", "a\\b.", "a \\b" })
            Eq(UpdateFiles.Clean(bad), null, "rejected: " + bad);
        Eq(UpdateFiles.Clean(" Renderer/a b/c.dll "), "Renderer\\a b\\c.dll", "normal path kept");
        Eq(UpdateFiles.Stale(new[] { "..\\outside.dll", "gone.dll" }, newList).Count, 1, "bad lines in the old list are skipped");
    }
}
