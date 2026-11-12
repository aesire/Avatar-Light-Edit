// 작업 기록(Document.JumpTo · HistoryLabels) · 원본과 비교 보기(RenderSync.ShowOriginal) — 2026-11-12
using System.Collections.Generic;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;

static partial class CoreTests
{
    sealed class ListSink : IRenderSink
    {
        public readonly List<JNode> Sent = new List<JNode>();
        public void Send(JNode msg) => Sent.Add(msg);
        public bool IsConnected => true;
    }

    static void HistoryJump()
    {
        var doc = new Document(SampleScene(), "x.ir.json");
        var cut = Addr.MatFloat("matA", "_Cutoff");
        var smile = Addr.BlendShape("r0", "smile");
        doc.Set(cut, 0.7, "a");
        doc.Set(smile, 40.0, "b");
        doc.Set(cut, 0.9, "c");
        Eq(string.Join(",", doc.HistoryLabels), "a,b,c", "labels oldest first");
        Eq(doc.UndoCount, 3, "three steps");

        int raised = 0; bool replay = true;
        doc.Changed += _ => { raised++; replay &= doc.Replaying; };
        doc.JumpTo(1);
        Eq(doc.UndoCount, 1, "jump back to 1");
        Eq(doc.Get(cut).AsFloat, 0.7f, "value at step 1");
        Check(!doc.IsModified(smile), "step 2 undone");
        Eq(raised, 1, "one Changed for the whole jump");
        Check(replay, "jump raises Changed as replay");
        Eq(string.Join(",", doc.HistoryLabels), "a,b,c", "undone steps stay listed");
        Eq(doc.RedoLabel, "b", "next redo is b");

        doc.JumpTo(3);
        Eq(doc.Get(cut).AsFloat, 0.9f, "jump forward to 3");
        Eq(doc.Get(smile).AsFloat, 40f, "step 2 redone");
        doc.JumpTo(0);
        Check(doc.Overrides.Count == 0 && !doc.CanUndo && doc.CanRedo, "jump to the start");
        doc.JumpTo(99);
        Eq(doc.UndoCount, 3, "clamped to the end");
        doc.JumpTo(2);
        doc.Set(smile, 10.0, "d");
        Eq(string.Join(",", doc.HistoryLabels), "a,b,d", "new edit drops undone steps");

        // 저장 상태: 저장한 시점으로 돌아오면 다시 깨끗하다
        doc.MarkSaved();
        doc.JumpTo(1);
        Check(doc.IsDirty, "dirty away from saved step");
        doc.JumpTo(3);
        Check(!doc.IsDirty, "clean back at saved step");
    }

    static void OriginalView()
    {
        var doc = new Document(SampleScene(), "x.ir.json");
        var sink = new ListSink();
        var sync = new RenderSync(doc, sink);
        var cut = Addr.MatFloat("matA", "_Cutoff");
        var smile = Addr.BlendShape("r0", "smile");
        Eq(sync.LookChanges, 0, "nothing to compare yet");
        doc.Set(cut, 0.7, "a");
        doc.Set(smile, 40.0, "b");
        doc.Set(Addr.Environment, JNode.Arr(), "env");
        Eq(sync.LookChanges, 2, "environment is not part of the look");
        float origCut = doc.GetOriginal(cut).AsFloat;

        sink.Sent.Clear();
        sync.ShowOriginal(true);
        Check(sync.ShowingOriginal, "showing original");
        Eq(sink.Sent.Count, 2, "only look values sent");
        Eq(sink.Sent.First(m => m["t"].AsString == Msg.SetMatProp)["value"].AsFloat, origCut, "original material value sent");
        Eq(sink.Sent.First(m => m["t"].AsString == Msg.SetBlendShape)["weight"].AsFloat, 25f, "original blendshape sent");
        Eq(doc.Get(cut).AsFloat, 0.7f, "document untouched");
        Check(doc.CanUndo && doc.UndoCount == 3, "undo history untouched");

        sink.Sent.Clear();
        sync.ShowOriginal(false);
        Eq(sink.Sent.First(m => m["t"].AsString == Msg.SetMatProp)["value"].AsFloat, 0.7f, "edited value restored");

        // 원본을 보는 중에 값을 바꾸면 저절로 풀리고, 고친 값이 먼저 돌아온 뒤 새 값이 간다
        sync.ShowOriginal(true);
        bool ended = false; sync.OriginalEnded += () => ended = true;
        sink.Sent.Clear();
        doc.Set(smile, 60.0, "c");
        Check(ended && !sync.ShowingOriginal, "editing ends the original view");
        Eq(sink.Sent.Last(m => m["t"].AsString == Msg.SetBlendShape)["weight"].AsFloat, 60f, "new value sent last");
        Check(sink.Sent.Any(m => m["t"].AsString == Msg.SetMatProp && m["value"].AsFloat == 0.7f), "other edited values restored");
    }
}
