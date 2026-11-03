// 세션에 재료(IR source) 적기 (Host/Core/Session.cs, 2026-10-27): IR을 잃어도 재료로 다시 만들 수 있게
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class CoreTests
{
    static void SessionSource()
    {
        var scene = SampleScene();
        var src = JNode.Obj().Set("guid", "abc").Set("root", "123").Set("sources", JNode.Arr().Add("C:/pkg/a.unitypackage"));
        scene.Raw = JNode.Obj().Set("source", src);
        var doc = new Document(scene, "C:/cache/x/x.ir.json");
        var back = Session.FromJson(JNode.Parse(Session.ToJson(doc, null).ToJson()), "C:/");
        Eq(back.Source["guid"].AsString, "abc", "source guid kept");
        Eq(back.Source["sources"].Count, 1, "source list kept");

        // 예전 세션 (source 없음) · 재료 없는 IR
        var old = Session.FromJson(JNode.Obj().Set("format", Session.Format).Set("version", 1).Set("ir", "a.ir.json"), "C:/");
        Check(old.Source.IsNull, "old session → no source");
        var none = Session.FromJson(JNode.Parse(Session.ToJson(new Document(SampleScene(), "y.ir.json"), null).ToJson()), "C:/");
        Check(none.Source.IsNull, "ir without source → null source");

        // 확장자 (2026-11-03: .vrcproto → .alesav, 예전 것도 연다)
        Check(Session.IsSessionFile("a.alesav") && Session.IsSessionFile("B.VRCPROTO") && !Session.IsSessionFile("a.alexp") && !Session.IsSessionFile("a.ir.json"), "session file extensions");
    }
}
