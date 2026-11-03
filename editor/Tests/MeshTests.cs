// 메시 편집 연산 (Protocol.MeshOps)과 Host 쪽 기록 (Core.MeshEdits) 테스트
using System;
using System.IO;
using System.Linq;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static partial class CoreTests
{
    // 사각형 하나를 삼각형 두 개로, 공유 모서리 정점을 따로 둔 메시 (정점 6개: 0 1 2 | 3 4 5, 3=2·5=0 자리)
    static MeshData Quad6(bool seamUv = false)
    {
        var d = new MeshData
        {
            VertexCount = 6,
            Pos = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0 },
            Nrm = Enumerable.Range(0, 6).SelectMany(_ => new float[] { 0, 0, -1 }).ToArray(),
            BoneIdx = Enumerable.Range(0, 24).Select(i => i % 4 == 0 ? new[] { 0, 1, 2, 2, 4, 0 }[i / 4] : 0).ToArray(),   // 겹친 정점은 같은 본
            BoneW = Enumerable.Range(0, 24).Select(i => i % 4 == 0 ? 1f : 0f).ToArray(),
        };
        d.Uv[0] = new float[] { 0, 0, 1, 0, 1, 1, seamUv ? 0.5f : 1, 1, 0, 1, 0, 0 };
        d.Subs.Add(new MeshData.Sub { Indices = new[] { 0, 1, 2, 3, 4, 5 } });
        var sh = new MeshData.Shape { Name = "s" };
        sh.Frames.Add(new MeshData.Frame { Weight = 100, DPos = new float[18] });
        d.Shapes.Add(sh);
        return d;
    }

    static JNode Op(string op) => JNode.Obj().Set("op", op);
    static JNode Ints(params int[] xs) { var a = JNode.Arr(); foreach (var x in xs) a.Add(x); return a; }

    static void MeshOpsBasics()
    {
        var q = Quad6();
        Eq(q.Triangles, 2, "quad tris");

        // 노멀 다시 계산 (2026-10-23): 평평하면 원래 노멀(0,0,−1) 그대로, 한 모서리를 들면 기울고 같은 자리 정점(2·3)은 같은 노멀
        var flat = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Normals)));
        Eq(Math.Round(flat.Nrm![2], 4), -1.0, "normals: 평평한 사각형은 −Z");
        var lifted = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Move).Set("sel", Ints(2, 3)).Set("delta", JNode.FloatArray(new[] { 0f, 0f, -0.5f }))).Add(Op(MeshOps.Normals)));
        Eq(Math.Abs(lifted.Nrm![2 * 3 + 2] - (-1)) > 0.01, true, "normals: 옮긴 뒤 기울어짐");
        Eq(Math.Round(lifted.Nrm[2 * 3], 4) == Math.Round(lifted.Nrm[3 * 3], 4) && Math.Round(lifted.Nrm[2 * 3 + 2], 4) == Math.Round(lifted.Nrm[3 * 3 + 2], 4), true, "normals: 이음매 정점은 같은 노멀");
        var only = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Move).Set("sel", Ints(2, 3)).Set("delta", JNode.FloatArray(new[] { 0f, 0f, -0.5f }))).Add(Op(MeshOps.Normals).Set("sel", Ints(0))));
        Eq(only.Nrm![1 * 3 + 2], -1f, "normals: 고른 정점만 (정점 1은 그대로)");

        var w = MeshOps.Apply(q, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f)));
        Eq(w.VertexCount, 4, "weld: 겹친 정점 2쌍 합침");
        Eq(w.Triangles, 2, "weld: 삼각형 유지");
        Eq(string.Join(",", w.Subs[0].Indices), "0,1,2,2,3,0", "weld: 인덱스 다시 매김");
        Eq(w.Shapes[0].Frames[0].DPos!.Length, 12, "weld: 셰이프 델타도 줄어듦");
        Eq(w.BoneIdx![3 * 4], 4, "weld: 새 정점 3 = 옛 정점 4의 본");
        Eq(q.VertexCount, 6, "Apply는 원본을 바꾸지 않음");

        var seam = Quad6(seamUv: true);
        Eq(MeshOps.Apply(seam, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f))).VertexCount, 5, "keepSeams: UV가 다른 쌍은 남김");
        Eq(MeshOps.Apply(seam, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f, keepSeams: false))).VertexCount, 4, "keepSeams 끔: UV 달라도 합침");

        var shp = Quad6(); shp.Shapes[0].Frames[0].DPos![3 * 3] = 0.1f; // 정점 3만 셰이프로 움직임
        Eq(MeshOps.Apply(shp, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f))).VertexCount, 5, "keepShapes: 셰이프 델타가 다른 쌍은 남김");
        Eq(MeshOps.Apply(shp, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f, keepShapes: false))).VertexCount, 4, "keepShapes 끔");

        var bw = Quad6(); bw.BoneIdx![3 * 4] = 7;   // 정점 3만 다른 본
        Eq(MeshOps.Apply(bw, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f))).VertexCount, 5, "본 가중치가 다른 쌍은 남김");
        var tw = Quad6(); tw.Tan = Enumerable.Range(0, 24).Select(i => i % 4 == 3 ? (i / 4 == 3 ? -1f : 1f) : 0f).ToArray();
        Eq(MeshOps.Apply(tw, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f))).VertexCount, 5, "탄젠트 방향이 다른 쌍은 남김 (미러 이음매)");

        // 큰 거리: 사각형이 한 점으로 → 삼각형 0 (삼각형 수 감소)
        var big = MeshOps.Apply(q, JNode.Arr().Add(MeshEdits.WeldOp(2f, keepSeams: false, keepShapes: false)));
        Eq(big.VertexCount, 1, "weld 큰 거리: 한 점"); Eq(big.Triangles, 0, "넓이 0 삼각형 삭제");

        // 같은 면 중복 삭제 (같은 감김의 회전), 뒤집힌 면은 남김
        var dup = Quad6(); dup.Subs[0].Indices = new[] { 0, 1, 2, 1, 2, 0, 2, 1, 0 };
        Eq(MeshOps.Apply(dup, JNode.Arr().Add(MeshEdits.WeldOp(0))).Triangles, 2, "중복 면 하나 삭제, 뒤집힌 면 유지");

        // sel: 고른 정점만
        var part = MeshOps.Apply(q, JNode.Arr().Add(MeshEdits.WeldOp(0.0001f).Set("sel", Ints(2, 3))));
        Eq(part.VertexCount, 5, "weld sel: 고른 쌍만");

        var del = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Delete).Set("sel", Ints(4))));
        Eq(del.VertexCount, 5, "delete: 정점 하나"); Eq(del.Triangles, 1, "delete: 그 정점을 쓰는 면 삭제");
        Eq(string.Join(",", del.Subs[0].Indices), "0,1,2", "delete: 남은 면");

        var mg = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Merge).Set("sel", Ints(1, 2)).Set("at", "center")));
        Eq(mg.VertexCount, 5, "merge center"); Eq(mg.Pos[1 * 3 + 1], 0.5f, "merge center: 가운데 위치");
        Eq(mg.Triangles, 1, "merge: 넓이 0 면 삭제");
        var ml = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Merge).Set("sel", Ints(0, 2)).Set("at", "last")));
        Eq(ml.Pos[0 * 3], 1f, "merge last: 마지막 정점 자리"); Eq(ml.BoneIdx![0], 2, "merge last: 속성도 마지막 정점 것");

        var face = MeshOps.Apply(del, JNode.Arr().Add(Op(MeshOps.Face).Set("sel", Ints(0, 2, 3, 4))));
        Eq(face.Triangles, 3, "face: 4개 → 부채꼴 2개 추가");

        var mv = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Move).Set("sel", Ints(0, 5)).Set("delta", JNode.FloatArray(new[] { 0f, 0, 0.5f }))));
        Check(mv.Pos[2] == 0.5f && mv.Pos[17] == 0.5f && mv.Pos[5] == 0, "move: 같은 델타");
        var mv2 = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Move).Set("sel", Ints(1, 2)).Set("delta", JNode.FloatArray(new[] { 1f, 0, 0, 0, 2, 0 }))));
        Check(mv2.Pos[3] == 2 && mv2.Pos[7] == 3, "move: 정점마다 델타 (브러시)");

        // 연산은 차례로: delete 뒤 번호 기준으로 weld
        var seq = MeshOps.Apply(q, JNode.Arr().Add(Op(MeshOps.Delete).Set("sel", Ints(0))).Add(MeshEdits.WeldOp(0.0001f)));
        Eq(seq.VertexCount, 4, "연산 순서대로 적용");
    }

    static void MeshEditsDoc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrcproto-meshtest");
        Directory.CreateDirectory(dir);
        var q = Quad6();
        JNode mesh;
        using (var w = new IrBufferWriter(Path.Combine(dir, "m.bin")))
            mesh = JNode.Obj().Set("id", "g:1").Set("name", "Q").Set("vertexCount", 6)
                .Set("positions", w.Write(q.Pos).ToJson()).Set("normals", w.Write(q.Nrm!).ToJson())
                .Set("uv0", w.Write(q.Uv[0]!).ToJson())
                .Set("submeshes", JNode.Arr().Add(JNode.Obj().Set("indices", w.Write(q.Subs[0].Indices).ToJson())));
        var root = JNode.Obj().Set("irVersion", 1).Set("name", "m").Set("buffers", "m.bin")
            .Set("nodes", JNode.Arr().Add(JNode.Obj().Set("id", 0).Set("name", "R")))
            .Set("meshes", JNode.Arr().Add(mesh))
            .Set("renderers", JNode.Arr().Add(JNode.Obj().Set("id", "r").Set("node", 0).Set("type", "MeshRenderer").Set("mesh", "g:1").Set("materials", JNode.Arr())));
        File.WriteAllText(Path.Combine(dir, "m.ir.json"), root.ToJson());
        var s = IrScene.Load(Path.Combine(dir, "m.ir.json"));
        Eq(s.Meshes[0].Uvs[0].Count, 12, "IR uv0 키");
        var doc = new Document(s, "");
        var me = new MeshEdits(doc);
        Eq(me.Counts("g:1"), (6, 2), "편집 전 = IR 값");
        var (dv, dt) = me.WeldAll(me.MeshIds(), 0.0001f, true, true);
        Eq((dv, dt), (2, 0), "WeldAll: 줄어든 수");
        Eq(me.Counts("g:1"), (4, 2), "편집 후 수");
        Eq(AvatarStats.From(s, null, id => me.Counts(id).tris).Triangles, 2, "등급은 편집 결과로");
        Eq(doc.UndoLabel, "정점 합치기 (0.1 mm)", "Undo 한 단계");
        var (dv2, _) = me.WeldAll(me.MeshIds(), 0.0001f, true, true);
        Eq(dv2, 0, "다시 합쳐도 줄 것 없음");
        Eq(doc.Get(Addr.MeshEdit("g:1")).Count, 1, "줄지 않으면 연산을 붙이지 않음");
        var msg = RenderSync.ToMessage(Addr.MeshEdit("g:1"), doc.Get(Addr.MeshEdit("g:1")))!;
        Eq(Msg.Type(msg), Msg.SetMeshEdits, "렌더러 메시지"); Eq(msg["ops"][0]["op"].AsString, "weld", "ops 전달");
        doc.Undo();
        Eq(me.Counts("g:1"), (6, 2), "Undo하면 원본");
        Check(!doc.IsModified(Addr.MeshEdit("g:1")), "빈 목록 = 원본 (덮어쓰기 없음)");

        // 세션 저장·불러오기 왕복
        doc.Redo();
        var cs = doc.ChangesetToJson();
        var doc2 = new Document(s, ""); doc2.LoadChangeset(cs);
        Eq(new MeshEdits(doc2).Counts("g:1"), (4, 2), "Changeset 왕복");
        // 아이템을 바꿔 IR을 다시 만들어도 같은 메시면 편집 유지
        Eq(Recompose.RemapChangeset(cs, s, s).Count, 1, "Recompose: 같은 메시는 유지");

        // 수를 바꾸지 않는 편집(옮기기·블렌드쉐이프 연산)은 버퍼를 읽지 않고 IR 값 (2026-10-24 열기 시간)
        var doc3 = new Document(s, "");
        doc3.Set(Addr.MeshEdit("g:1"), JNode.Arr().Add(JNode.Obj().Set("op", MeshOps.Move).Set("sel", JNode.Arr().Add(0)).Set("delta", JNode.Arr().Add(0.1f).Add(0f).Add(0f))));
        Check(!MeshEdits.ChangesCounts(doc3.Get(Addr.MeshEdit("g:1"))), "move는 수를 바꾸지 않음");
        Check(MeshEdits.ChangesCounts(JNode.Arr().Add(MeshEdits.WeldOp(0.001f))), "weld는 수를 바꿈");
        var s3 = IrScene.Load(Path.Combine(dir, "m.ir.json"));
        s3.BufferFile = Path.Combine(dir, "없음.bin");   // 읽으려 하면 null → IR 값으로 떨어지지만, 읽지 않아야 맞다
        var me3 = new MeshEdits(new Document(s3, ""));
        Eq(me3.Counts("g:1"), (6, 2), "move만: IR 값");

        // 함께 세기: 두 페이지가 같은 편집에 불러도 한 번 (같은 Task), 끝나면 Counts·BaseTris가 바로 답함
        var me4 = new MeshEdits(doc2);
        var t1 = me4.RecountAll(); var t2 = me4.RecountAll();
        Check(ReferenceEquals(t1, t2), "RecountAll: 같은 편집이면 같은 Task");
        t1.Wait();
        Eq(me4.Counts("g:1"), (4, 2), "RecountAll 뒤 수");
        Eq(me4.BaseTris("g:1"), 2, "RecountAll 뒤 BaseTris");
        // 세느라 읽은 원본은 들고 있지 않는다 (Original로 부른 것만)
        Check(me4.Original("g:1") != null && ReferenceEquals(me4.Original("g:1"), me4.Original("g:1")), "Original은 들고 있음");
    }
}
