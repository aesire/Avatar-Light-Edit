// 촬영 PNG 세션 넣기 (Host/Core/PngText.cs) · 변형 (Document.Variants · SwapChangeset) · 세션 JSON
using System;
using System.IO;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class CoreTests
{
    static void SheetCore()
    {
        // iTXt: 넣고 읽기 (한글·긴 글), 픽셀 그대로, 같은 키워드는 바꾸기
        var img = Blob(12, 9, 2, 1, 5, 6);
        var png = Sticker.EncodePng(img);
        var text = "{\"이름\":\"초콜릿\",\"x\":\"" + new string('a', 5000) + "\"}";
        var with = PngText.Insert(png, "k", text);
        Eq(PngText.Read(with, "k"), text, "itxt round trip");
        Eq(PngText.Read(png, "k"), null, "no chunk → null");
        Eq(PngText.Read(with, "other"), null, "other keyword → null");
        var back = Sticker.DecodePng(with);
        Check(back != null && back.Px.AsSpan().SequenceEqual(img.Px), "pixels unchanged after insert");
        var twice = PngText.Insert(with, "k", "새 글");
        Eq(PngText.Read(twice, "k"), "새 글", "same keyword replaced");
        Check(twice.Length < with.Length, "old chunk removed");
        Check(PngText.Insert(new byte[] { 1, 2, 3 }, "k", "x").Length == 3, "not png → unchanged");

        // 변형 · SwapChangeset: 실행 취소 기록과 저장 상태는 그대로, 되돌리면 같은 값
        var doc = new Document(SampleScene(), "x.ir.json");
        var cut = Addr.MatFloat("matA", "_Cutoff");
        var smile = Addr.BlendShape("r0", "smile");
        doc.Set(cut, 0.7, "a");
        doc.MarkSaved();
        doc.Variants.Add(new Document.Variant { Name = "웃음", Changeset = JNode.Arr().Add(JNode.Obj().Set("op", "set").Set("a", smile.ToString()).Set("v", 80.0)) });
        bool replaying = true; doc.Changed += _ => replaying &= doc.Replaying;
        var saved = doc.SwapChangeset(doc.Variants[0].Changeset);
        Eq(doc.Get(smile).AsFloat, 80f, "variant applied");
        Check(!doc.IsModified(cut), "variant replaces all overrides");
        Check(replaying, "swap raises Changed as replay");
        doc.SwapChangeset(saved);
        Eq(doc.Get(cut).AsFloat, 0.7f, "restored value");
        Check(!doc.IsModified(smile), "variant value gone after restore");
        Check(!doc.IsDirty && doc.CanUndo, "saved state and undo kept");
        doc.Undo();
        Eq(doc.Get(cut).AsFloat, 0.5f, "undo still works after swap");

        // 세션 JSON: 변형 왕복, 상대 IR 경로는 기준 폴더로
        var j = Session.ToJson(doc, JNode.Obj().Set("yaw", 30));
        var l = Session.FromJson(JNode.Parse(j.ToJson()), "C:\\base");
        Eq(l.IrPath, Path.Combine("C:\\base", "x.ir.json"), "relative ir path");
        var doc2 = new Document(SampleScene(), "x.ir.json");
        doc2.LoadVariants(l.Variants);
        Eq(doc2.Variants.Count, 1, "variants loaded");
        Eq(doc2.Variants[0].Name, "웃음", "variant name");
        Eq(l.Camera["yaw"].AsInt, 30, "camera kept");
        bool threw = false;
        try { Session.FromJson(JNode.Obj().Set("format", "nope"), "."); } catch (InvalidDataException) { threw = true; }
        Check(threw, "not a session → throws");

        // 파일: 넣고 다시 읽기
        var tmp = Path.Combine(Path.GetTempPath(), "vrcproto_pngtext_test.png");
        File.WriteAllBytes(tmp, png);
        PngText.EmbedSession(tmp, doc, JNode.Null);
        var rs = PngText.ReadSession(tmp);
        Check(rs != null && rs.Variants.Count == 1, "embedded session read back");
        File.Delete(tmp);

        ShapeSetsCore();
    }

    // 블렌드쉐이프 세팅 (Host/Core/ShapeSets.cs): 블렌드쉐이프만 담기 · 비교 · 되돌리기 한 단계 · 세션 왕복(사진·종류)
    static void ShapeSetsCore()
    {
        var doc = new Document(SampleScene(), "x.ir.json");
        var smile = Addr.BlendShape("r0", "smile"); var aa = Addr.BlendShape("r0", "vrc.v_aa");
        var cut = Addr.MatFloat("matA", "_Cutoff");
        doc.Set(smile, 80, "a"); doc.Set(cut, 0.7, "b");
        var v = ShapeSets.Add(doc, "", "cGhvdG8=");
        Eq(v.Name, "저장된 블렌드쉐이프 1", "default name");
        Eq(v.Changeset.Count, 1, "only blendshapes saved");
        Eq(ShapeSets.Count(doc, v), 1, "count");
        Eq(ShapeSets.NextName(doc), "저장된 블렌드쉐이프 2", "next name");
        var named = ShapeSets.Add(doc, "웃는 얼굴");
        Eq(ShapeSets.NextName(doc), "저장된 블렌드쉐이프 2", "named sets not counted");
        ShapeSets.Remove(doc, named);
        Eq(ShapeSets.Compare(doc, v).Count, 0, "same as now → no diff");

        doc.Set(smile, 10, "c"); doc.Set(aa, 50, "d"); doc.Set(cut, 0.9, "e");
        var diffs = ShapeSets.Compare(doc, v);
        Eq(diffs.Count, 2, "two diffs (changed + extra override)");
        var dAa = diffs.Find(d => d.Addr.Equals(aa))!;
        Check(dAa != null && dAa.Now == 50 && dAa.Saved == 0, "extra override → saved = original");
        Eq(ShapeSets.Apply(doc, v), 2, "apply changes two");
        Eq(doc.Get(smile).AsFloat, 80f, "smile back");
        Check(!doc.IsModified(aa), "extra override reset");
        Eq(doc.Get(cut).AsFloat, 0.9f, "material untouched");
        Eq(doc.UndoLabel, "BlendShape 프리셋 불러오기: 저장된 블렌드쉐이프 1", "one undo step");
        doc.Undo();
        Eq(doc.Get(smile).AsFloat, 10f, "undo restores smile");
        Eq(doc.Get(aa).AsFloat, 50f, "undo restores aa");
        Eq(ShapeSets.Apply(doc, v), 2, "apply again");
        Eq(ShapeSets.Apply(doc, v), 0, "nothing to change");

        // 지금 IR에 없는 블렌드쉐이프는 비교에서 빼기
        v.Changeset.Add(JNode.Obj().Set("op", "set").Set("a", Addr.BlendShape("r0", "gone").ToString()).Set("v", 30.0));
        Eq(ShapeSets.Compare(doc, v).Count, 0, "missing shape ignored");

        // 세션 왕복: 종류·사진·id 그대로, 예전 변형은 세팅 목록에 안 나옴
        doc.Variants.Add(new Document.Variant { Name = "옛 변형", Changeset = JNode.Arr() });
        var doc2 = new Document(SampleScene(), "x.ir.json");
        doc2.LoadVariants(Session.FromJson(JNode.Parse(Session.ToJson(doc, null).ToJson()), ".").Variants);
        var sets = new System.Collections.Generic.List<Document.Variant>(ShapeSets.All(doc2));
        Check(sets.Count == 1 && sets[0].Photo == "cGhvdG8=" && sets[0].Id == v.Id && sets[0].Time == v.Time, "set round trip");
        Eq(doc2.Variants.Count, 2, "old variant kept");
        ShapeSets.Overwrite(doc2, sets[0]);
        Eq(sets[0].Changeset.Count, 0, "overwrite with no overrides");
        ShapeSets.Remove(doc2, sets[0]);
        Eq(doc2.Variants.Count, 1, "removed");
    }
}
