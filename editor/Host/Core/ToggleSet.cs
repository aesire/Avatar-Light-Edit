// 사용자 토글 묶음과 단축키 (2026-10-19). 토글은 오브젝트 켜고 끄기(NodeActive)만 한다 —
// VRChat Expression Menu·Modular Avatar·VRCFury 토글은 재현하지 않는다.
//   토글   Addr.Toggles = [{ name, nodes: [노드 id] }]. 누르면 묶음이 모두 켜져 있으면 끄고, 아니면 모두 켠다. Shift+1~9 = 1~9번째 묶음.
//   표정   Addr.ExpressionKeys = [표정 값 | null] (F1~F9). 누르면 그 표정을 쓰고, 이미 그 표정이면 뺀다 (표정 페이지 단추와 같다).
// 둘 다 Document에 기록되어 세션에 저장되고 실행 취소된다. 토글을 누른 결과(NodeActive)는 다른 켜고 끄기처럼 올리기에도 반영된다.
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class ToggleSet
    {
        public const int MaxKeys = 9;

        public static JNode Make(string name, IEnumerable<int> nodes)
        {
            var arr = JNode.Arr();
            foreach (var n in nodes.Distinct()) arr.Add(n);
            return JNode.Obj().Set("name", name).Set("nodes", arr);
        }

        public static List<int> Nodes(JNode toggle) => toggle["nodes"].Items.Select(x => x.AsInt).ToList();

        /// <summary>묶음의 오브젝트가 모두 켜져 있으면 켜짐 (빈 묶음은 꺼짐).</summary>
        public static bool IsOn(Document doc, JNode toggle)
        {
            var nodes = Nodes(toggle);
            return nodes.Count > 0 && nodes.All(n => doc.Get(Addr.NodeActive(n)).AsBool);
        }

        /// <summary>i번째(0부터) 토글을 누른다. 없으면 null, 있으면 누른 뒤 켜졌는지.</summary>
        public static bool? Press(Document doc, int i)
        {
            var list = doc.Get(Addr.Toggles);
            if (i < 0 || i >= list.Count) return null;
            return Set(doc, list[i], !IsOn(doc, list[i]));
        }

        public static bool Set(Document doc, JNode toggle, bool on)
        {
            using (doc.Group($"토글 {toggle["name"].AsString} {(on ? "켜기" : "끄기")}"))
                foreach (var n in Nodes(toggle))
                    if (doc.Get(Addr.NodeActive(n)).AsBool != on) doc.Set(Addr.NodeActive(n), on);
            return on;
        }

        /// <summary>목록 전체를 바꿔 기록한다 (추가·고치기·지우기·순서).</summary>
        public static void Replace(Document doc, IEnumerable<JNode> toggles, string label)
        {
            var arr = JNode.Arr();
            foreach (var t in toggles) arr.Add(t);
            doc.Set(Addr.Toggles, arr, label);
        }

        // ---------------- 표정 단축키 ----------------
        public static JNode ExpressionKey(Document doc, int slot)
        {
            var keys = doc.Get(Addr.ExpressionKeys);
            return slot >= 0 && slot < keys.Count ? keys[slot] : JNode.Null;
        }

        /// <summary>slot(0부터)에 표정을 넣는다 (null이면 비운다). 다른 칸에 같은 표정(이름·출처)이 있으면 그 칸은 비운다.</summary>
        public static void SetExpressionKey(Document doc, int slot, JNode expression)
        {
            if (slot < 0 || slot >= MaxKeys) return;
            var old = doc.Get(Addr.ExpressionKeys);
            var arr = JNode.Arr();
            int n = System.Math.Max(old.Count, slot + 1);
            for (int i = 0; i < n; i++)
            {
                var v = i == slot ? expression : i < old.Count ? old[i] : JNode.Null;
                if (i != slot && !expression.IsNull && Same(v, expression)) v = JNode.Null;
                arr.Add(v);
            }
            int last = arr.Count - 1;
            while (last >= 0 && arr[last].IsNull) last--;
            if (last < arr.Count - 1) { var t = JNode.Arr(); for (int i = 0; i <= last; i++) t.Add(arr[i]); arr = t; }
            doc.Set(Addr.ExpressionKeys, arr, expression.IsNull ? $"F{slot + 1} 비우기" : $"F{slot + 1}에 표정 {FaceExpression.Name(expression)}");
        }

        /// <summary>F 단축키: 그 칸의 표정을 쓰거나(이미 쓰고 있으면) 뺀다. 빈 칸이면 null, 아니면 쓴 표정 이름 또는 "".</summary>
        public static string? PressExpression(Document doc, int slot)
        {
            var e = ExpressionKey(doc, slot);
            if (e.IsNull) return null;
            var cur = doc.Get(Addr.Expression);
            if (Same(cur, e))
            {
                doc.Reset(Addr.Expression, "표정 빼기");
                return "";
            }
            doc.Set(Addr.Expression, e, "표정: " + FaceExpression.Name(e));
            return FaceExpression.Name(e);
        }

        /// <summary>같은 표정 = 이름과 출처(avatar·anim·tracking)가 같다. 아바타 기본 "Smile"과 .anim "Smile"은 다른 표정이다.</summary>
        static bool Same(JNode a, JNode b) =>
            !a.IsNull && !b.IsNull && FaceExpression.Name(a) == FaceExpression.Name(b) && a["source"].AsString == b["source"].AsString;
    }
}
