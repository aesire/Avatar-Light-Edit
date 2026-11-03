// Mouth Fix: 입 모양을 바꾸는 블렌드쉐이프(mouth_straight 같은 다문 입 편집)를 켜 두면, 입 벌리기·모음 블렌드쉐이프가 원래 입 기준이라
// 둘이 더해져 입술이 겹치거나 찢어져 보인다. 입 벌리는 블렌드쉐이프에서 지금 켜 둔 입 편집의 영향을 빼서,
// 입 벌리기 100 + 입 편집 = 원래 벌린 입이 되게 한다 (입을 벌리는 만큼 입 편집이 줄어들고, 다물면 그대로).
//   mouthFix { targets: [입 벌리는 블렌드쉐이프], sources: [{ name, weight }], strength = 1, falloff = 0 }  — 계산은 blinkFix와 같다 (BlinkFix.cs)
// 모음 블렌드쉐이프는 보통 하나씩, 섞여도 합이 100 안팎이라 뺀 양도 그만큼만 쌓인다.
using System.Collections.Generic;

namespace VrcProto.Protocol
{
    public static partial class MeshOps
    {
        public const string MouthFix = "mouthFix";

        /// <summary>블렌드쉐이프 델타만 바꾸는 연산 (깜빡임·입 맞추기, 입 선 다듬기) (정점 번호를 쓰지 않음: 올리기에서 정점 짝 맞추기 뒤에 바로 적용).</summary>
        public static bool IsShapeFix(string op) => op == BlinkFix || op == MouthFix || op == ShapeMove;

        /// <summary>입을 움직이는 블렌드쉐이프처럼 보이는 이름 (Mouth Fix 대상 후보): 모음·입 모양·턱.</summary>
        public static bool IsMouthName(string name)
        {
            var n = name.ToLowerInvariant();
            if (n == "vrc.v_sil") return false;
            if (n.StartsWith("vrc.v_")) return true;
            foreach (var k in MouthWords) if (n.Contains(k)) return true;
            return MouthKana.Contains(name);
        }

        static readonly string[] MouthWords = { "mouth", "jaw", "lip", "viseme", "口", "입" };
        static readonly HashSet<string> MouthKana = new HashSet<string> { "あ", "い", "う", "え", "お", "ん", "あ2", "ワ", "ω", "ω□", "□", "▲", "Λ", "にやり", "ぺろっ" };

        /// <summary>대상의 기본 선택: 입을 벌리는 것 (모음·あいうえお·입 A/I/U/E/O·턱 벌리기).</summary>
        public static bool IsMouthOpenName(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("correction")) return false;   // 얼굴 추적용 보정 (MUU-JawOpenCorrection …): JawOpen과 함께 쓰므로 두 번 빼게 된다
            if (n.StartsWith("vrc.v_"))
            {
                if (n.EndsWith(".ft")) n = n.Substring(0, n.Length - 3);   // 얼굴 추적용 모음 (vrc.v_aa.FT …)
                return n is "vrc.v_aa" or "vrc.v_oh" or "vrc.v_ou" or "vrc.v_e" or "vrc.v_ih" or "vrc.v_ch" or "vrc.v_dd" or "vrc.v_kk" or "vrc.v_ss" or "vrc.v_nn" or "vrc.v_rr" or "vrc.v_th";
            }
            if (name is "あ" or "い" or "う" or "え" or "お" or "あ2") return true;
            if (IsFtMouthName(name)) return true;
            if (n.Contains("jawopen") || n.Contains("jaw_open") || n.Contains("mouthopen") || n.Contains("mouth_open")) return true;
            // mouth_A_1 · mouth_I_2_open · mouth_O_1 … (モデルの母音 쉐이프)
            if (n.StartsWith("mouth_") && n.Length > 6)
            {
                var rest = n.Substring(6);
                return rest.Length >= 1 && "aiueo".IndexOf(rest[0]) >= 0 && (rest.Length == 1 || rest[1] == '_');
            }
            return false;
        }

        /// <summary>
        /// 얼굴 추적(Unified Expressions · ARKit)의 입술 모양. 입 편집을 켜 둔 채 추적하면 이것과 섞여 깨진다.
        /// MouthClosed·MouthClose는 JawOpen과 늘 함께 쓰는 보정이라(두 번 빼게 된다), 혀·볼·턱 좌우는 입술 선과 상관이 적어 뺀다.
        /// 여러 개가 함께 크게 움직이면 편집이 그만큼 더 빠진다 (블렌드쉐이프는 더해지므로).
        /// </summary>
        public static bool IsFtMouthName(string name) => FtMouth.Contains(name.ToLowerInvariant());

        static readonly HashSet<string> FtMouth = new HashSet<string>
        {
            // Unified Expressions
            "jawopen", "mouthsmile", "mouthsmileleft", "mouthsmileright", "mouthsad", "mouthsadleft", "mouthsadright",
            "mouthfrown", "mouthfrownleft", "mouthfrownright", "mouthstretch", "mouthstretchleft", "mouthstretchright",
            "mouthupperup", "mouthupperupleft", "mouthupperupright", "mouthlowerdown", "mouthlowerdownleft", "mouthlowerdownright",
            "mouthraiser", "mouthraiserupper", "mouthraiserlower", "mouthpout", "mouthpress", "mouthpressleft", "mouthpressright",
            "mouthdimple", "mouthdimpleleft", "mouthdimpleright", "mouthtightener", "mouthtightenerleft", "mouthtightenerright",
            "lipfunnel", "lipfunnelupper", "lipfunnellower", "lippucker", "lippuckerupper", "lippuckerlower",
            "lipsuckupper", "lipsucklower",
            // ARKit
            "mouthfunnel", "mouthpucker", "mouthrollupper", "mouthrolllower", "mouthshrugupper", "mouthshruglower",
        };

        public static JNode MakeMouthFix(IEnumerable<string> targets, IEnumerable<(string Name, float Weight)> sources, float strength = 1f, float falloff = 0f)
        {
            var j = MakeBlinkFix(targets, sources, strength, falloff);
            return j.Set("op", MouthFix);
        }
    }
}
