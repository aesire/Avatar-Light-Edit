// 원본에 이미 들어 있는 눈 깜빡임 보정 컴포넌트 → IR existingBlinkFix (선택 항목, editor/Protocol/Ir.cs).
// mouth: 그 컴포넌트에 입 블렌드쉐이프 목록도 채워져 있음 (입 보정도 함).
// 에디터의 깜빡임 맞추기와 겹치면 같은 보정이 두 번 들어가므로, 내보내기·올리기가 이것을 보고 에디터 쪽 보정을 뺀다.
// 스크립트 이름·GUID는 도구마다 달라 필드 이름으로 알아본다: 왼눈·오른눈 블렌드쉐이프 목록을 함께 가진 MonoBehaviour,
// 또는 스크립트 파일 이름이 BlinkFix·EyeFix 꼴인 것.
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VrcProto.Protocol;

namespace ImportSim
{
    public sealed partial class IrExport
    {
        static readonly Regex LeftEyeShapes = new Regex(@"left_?eye.*blend_?shape|blend_?shape.*left_?eye", RegexOptions.IgnoreCase);
        static readonly Regex RightEyeShapes = new Regex(@"right_?eye.*blend_?shape|blend_?shape.*right_?eye", RegexOptions.IgnoreCase);
        static readonly Regex MouthShapes = new Regex(@"mouth.*blend_?shape|blend_?shape.*mouth", RegexOptions.IgnoreCase);
        public static readonly Regex BlinkFixScript = new Regex(@"(blink|eye)[ _\-]?fix", RegexOptions.IgnoreCase);

        /// <summary>필드 이름만으로 보는 판정 (Unity 쪽 VrcProtoUpload.cs의 같은 규칙과 맞춘다).</summary>
        public static bool LooksLikeBlinkFix(IEnumerable<string> fields, string scriptName)
        {
            var f = fields.ToList();
            return f.Any(LeftEyeShapes.IsMatch) && f.Any(RightEyeShapes.IsMatch) || BlinkFixScript.IsMatch(scriptName);
        }

        JNode BlinkFixJson(SimFile f, List<SimObject> nodeGos)
        {
            var arr = JNode.Arr();
            for (int id = 0; id < nodeGos.Count; id++)
                foreach (var c in Components(f, nodeGos[id]))
                {
                    if (c.ClassId != 114) continue;
                    var script = db.Get(Ref.Guid(c.Props.GetValueOrDefault("m_Script")) ?? "");
                    var name = script?.Name ?? "";
                    if (!LooksLikeBlinkFix(c.Props.Keys, name)) continue;
                    // 입 편집 블렌드쉐이프 목록이 채워져 있으면 입 보정도 한다 (에디터의 입 맞추기도 뺀다). custom… 목록은 고칠 대상이라 보지 않는다
                    bool mouth = c.Props.Any(kv => MouthShapes.IsMatch(kv.Key) && kv.Key.IndexOf("custom", System.StringComparison.OrdinalIgnoreCase) < 0 && kv.Value is List<object?> l && l.Count > 0);
                    arr.Add(JNode.Obj().Set("node", id).Set("script", name.Length > 0 ? name : "(스크립트 이름 모름)").Set("mouth", mouth));
                }
            return arr;
        }
    }
}
