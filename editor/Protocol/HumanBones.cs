// 휴머노이드 본 이름(Unity HumanBodyBones 이름)의 한국어 표시 이름. 렌더러 안내(뷰포트 본 조작)와 Host 실행 취소 이름이 같이 쓴다.
using System.Collections.Generic;

namespace VrcProto.Protocol
{
    public static class HumanBones
    {
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["Hips"] = "엉덩이", ["Spine"] = "허리", ["Chest"] = "가슴", ["UpperChest"] = "윗가슴", ["Neck"] = "목", ["Head"] = "머리",
            ["Jaw"] = "턱", ["LeftEye"] = "왼눈", ["RightEye"] = "오른눈",
            ["LeftShoulder"] = "왼어깨", ["LeftUpperArm"] = "왼위팔", ["LeftLowerArm"] = "왼아래팔", ["LeftHand"] = "왼손",
            ["RightShoulder"] = "오른어깨", ["RightUpperArm"] = "오른위팔", ["RightLowerArm"] = "오른아래팔", ["RightHand"] = "오른손",
            ["LeftUpperLeg"] = "왼허벅지", ["LeftLowerLeg"] = "왼종아리", ["LeftFoot"] = "왼발", ["LeftToes"] = "왼발가락",
            ["RightUpperLeg"] = "오른허벅지", ["RightLowerLeg"] = "오른종아리", ["RightFoot"] = "오른발", ["RightToes"] = "오른발가락",
        };
        static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static readonly string[] FingerNames = { "엄지", "검지", "중지", "약지", "새끼" };
        static readonly string[] Joints = { "Proximal", "Intermediate", "Distal" };

        /// <summary>예: LeftUpperArm → 왼위팔, RightIndexIntermediate → 오른검지 2. 모르는 이름은 그대로.</summary>
        public static string Label(string human)
        {
            if (string.IsNullOrEmpty(human)) return "";
            if (Names.TryGetValue(human, out var n)) return n;
            string side = human.StartsWith("Left") ? "왼" : human.StartsWith("Right") ? "오른" : "";
            var rest = side.Length == 0 ? human : human.Substring(side == "왼" ? 4 : 5);
            for (int f = 0; f < Fingers.Length; f++)
                if (rest.StartsWith(Fingers[f]))
                    for (int j = 0; j < Joints.Length; j++)
                        if (rest == Fingers[f] + Joints[j]) return side + FingerNames[f] + " " + (j + 1);
            return human;
        }
    }
}
