// 페이셜 트래킹 수동 연결 (미리보기 전용, 업로드에는 쓰지 않음): Addr.FaceMap = { add: [{ src, r, s, k }], off: [{ src, r, s }] }.
//   자동 연결(렌더러 FaceRetarget이 이름으로 찾은 것) 위에, 사용자가 더한 연결(add, 세기 k = 0~2)을 얹고 끈 자동 연결(off)을 뺀다.
//   src = 트래킹 값 이름 (ARKit 52개 + 혀·볼 빨기, eyeBlink = 두 눈, viseme.aa … = 입 모양, mmd.* = MMD 표정 모프), r = 렌더러 id, s = 블렌드쉐이프 이름.
//   아바타에 FT 애니메이터가 설치되어 있으면 자동 연결은 비고 그 애니메이터가 움직이며, 여기서 더한 연결이 그 위에 덮인다.
// 같은 (값, 블렌드쉐이프)를 더하면 세기만 바꾼다. 수동으로 더한 것을 지우면 add에서 빼고, 자동 연결을 지우면 off에 넣는다.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class FaceMap
    {
        public sealed class Link
        {
            public string Src = "", R = "", S = "";
            public float K = 1;
            public bool Auto;   // 자동 연결 (세기는 1, 지우면 off로)
        }

        /// <summary>트래킹 값 이름과 한국어 설명 (창의 목록 순서).</summary>
        public static readonly (string Name, string Label)[] Sources =
        {
            ("eyeBlinkLeft", "왼눈 감기"), ("eyeBlinkRight", "오른눈 감기"), ("eyeBlink", "두 눈 감기 (큰 쪽)"),
            ("eyeSquintLeft", "왼눈 찡그림"), ("eyeSquintRight", "오른눈 찡그림"), ("eyeWideLeft", "왼눈 크게"), ("eyeWideRight", "오른눈 크게"),
            ("eyeLookUpLeft", "왼눈 위 보기"), ("eyeLookUpRight", "오른눈 위 보기"), ("eyeLookDownLeft", "왼눈 아래 보기"), ("eyeLookDownRight", "오른눈 아래 보기"),
            ("eyeLookInLeft", "왼눈 안쪽(코) 보기"), ("eyeLookInRight", "오른눈 안쪽(코) 보기"), ("eyeLookOutLeft", "왼눈 바깥 보기"), ("eyeLookOutRight", "오른눈 바깥 보기"),
            ("browDownLeft", "왼 눈썹 내리기"), ("browDownRight", "오른 눈썹 내리기"), ("browInnerUp", "눈썹 안쪽 올리기"),
            ("browOuterUpLeft", "왼 눈썹 바깥 올리기"), ("browOuterUpRight", "오른 눈썹 바깥 올리기"),
            ("cheekPuff", "볼 부풀리기"), ("cheekSquintLeft", "왼 볼 올리기"), ("cheekSquintRight", "오른 볼 올리기"),
            ("noseSneerLeft", "왼 코 찡그림"), ("noseSneerRight", "오른 코 찡그림"),
            ("jawOpen", "입 벌리기 (턱)"), ("jawForward", "턱 앞으로"), ("jawLeft", "턱 왼쪽"), ("jawRight", "턱 오른쪽"),
            ("mouthClose", "입술 닫기 (턱 벌린 채)"), ("mouthFunnel", "입 깔때기 (오)"), ("mouthPucker", "입 오므리기 (우)"),
            ("mouthLeft", "입 왼쪽으로"), ("mouthRight", "입 오른쪽으로"),
            ("mouthSmileLeft", "왼쪽 웃음"), ("mouthSmileRight", "오른쪽 웃음"), ("mouthFrownLeft", "왼쪽 입꼬리 내리기"), ("mouthFrownRight", "오른쪽 입꼬리 내리기"),
            ("mouthDimpleLeft", "왼쪽 보조개"), ("mouthDimpleRight", "오른쪽 보조개"), ("mouthStretchLeft", "왼쪽 입 늘이기"), ("mouthStretchRight", "오른쪽 입 늘이기"),
            ("mouthRollLower", "아랫입술 말기"), ("mouthRollUpper", "윗입술 말기"), ("mouthShrugLower", "아랫입술 올리기"), ("mouthShrugUpper", "윗입술 올리기"),
            ("mouthPressLeft", "왼쪽 입술 누르기"), ("mouthPressRight", "오른쪽 입술 누르기"),
            ("mouthLowerDownLeft", "왼쪽 아랫입술 내리기"), ("mouthLowerDownRight", "오른쪽 아랫입술 내리기"),
            ("mouthUpperUpLeft", "왼쪽 윗입술 올리기"), ("mouthUpperUpRight", "오른쪽 윗입술 올리기"),
            ("tongueOut", "혀 내밀기"),
            // Unified Expressions·Project Babble에만 있는 값
            ("cheekSuckLeft", "왼 볼 빨아들이기"), ("cheekSuckRight", "오른 볼 빨아들이기"),
            ("tongueUp", "혀 위로"), ("tongueDown", "혀 아래로"), ("tongueLeft", "혀 왼쪽"), ("tongueRight", "혀 오른쪽"),
            ("tongueRoll", "혀 말기"), ("tongueBendDown", "혀 끝 내리기"), ("tongueCurlUp", "혀 끝 올리기"), ("tongueSquish", "혀 누르기"), ("tongueFlat", "혀 펴기"),
            ("tongueTwistLeft", "혀 왼쪽 비틀기"), ("tongueTwistRight", "혀 오른쪽 비틀기"),
            // 입 모양 (말하기 립싱크와 큰 쪽으로 합쳐짐)
            ("viseme.aa", "입 모양 아"), ("viseme.ih", "입 모양 이"), ("viseme.ou", "입 모양 우"), ("viseme.e", "입 모양 에"), ("viseme.oh", "입 모양 오"),
            // MMD 표정 모프로 옮기는 값 (페이셜 쉐이프키가 없는 아바타의 기본 연결)
            ("mmd.smileEye", "MMD 웃는 눈 (笑い)"), ("mmd.surprise", "MMD 놀람 (びっくり)"), ("mmd.browSad", "MMD 곤란 눈썹 (困る)"),
            ("mmd.browDown", "MMD 화난 눈썹 (怒り·下)"), ("mmd.browUp", "MMD 눈썹 올리기 (上)"), ("mmd.smile", "MMD 입꼬리 올리기"),
            ("mmd.frown", "MMD 입꼬리 내리기"), ("mmd.wide", "MMD 입 옆으로 (口横広げ)"), ("mmd.tongue", "MMD 혀 (ぺろっ)"), ("mmd.puff", "MMD 볼 부풀리기"),
        };

        public static string Label(string src)
        {
            foreach (var (n, l) in Sources) if (n == src) return l;
            return src.StartsWith("both:") ? Label(src.Substring(5) + "Left").Replace("왼", "양") : src;
        }

        static bool Same(JNode x, string src, string r, string s) => x["src"].AsString == src && x["r"].AsString == r && x["s"].AsString == s;

        /// <summary>자동 연결 + 수동 연결 = 실제로 쓰이는 연결 (off로 끈 자동 연결은 빠진다).</summary>
        public static List<Link> Effective(IEnumerable<(string Src, string R, string S)> auto, JNode map)
        {
            var list = new List<Link>();
            var off = map["off"].Items.ToList();
            foreach (var (src, r, s) in auto)
                if (!off.Any(x => Same(x, src, r, s))) list.Add(new Link { Src = src, R = r, S = s, Auto = true });
            foreach (var a in map["add"].Items)
            {
                var src = a["src"].AsString; var r = a["r"].AsString; var s = a["s"].AsString;
                float k = a.Has("k") ? a["k"].AsFloat : 1;
                var same = list.Find(l => l.Src == src && l.R == r && l.S == s);
                if (same != null) same.K = k;   // 자동 연결의 세기만 바꾼 것 (지우면 off로)
                else list.Add(new Link { Src = src, R = r, S = s, K = k });
            }
            return list;
        }

        /// <summary>연결을 더하거나 세기를 바꾼다 (끈 자동 연결이었으면 off에서도 뺀다).</summary>
        public static JNode Add(JNode map, string src, string r, string s, float k = 1)
        {
            var add = JNode.Arr(); var off = JNode.Arr();
            foreach (var x in map["add"].Items) if (!Same(x, src, r, s)) add.Add(x.Clone());
            foreach (var x in map["off"].Items) if (!Same(x, src, r, s)) off.Add(x.Clone());
            add.Add(JNode.Obj().Set("src", src).Set("r", r).Set("s", s).Set("k", (float)Math.Round(Math.Clamp(k, 0, 2), 2)));
            return Make(add, off);
        }

        /// <summary>연결을 지운다: 수동으로 더한 것은 add에서 빼고, 자동 연결은 off에 넣는다.</summary>
        public static JNode Remove(JNode map, string src, string r, string s, bool auto)
        {
            var add = JNode.Arr(); var off = JNode.Arr();
            foreach (var x in map["add"].Items) if (!Same(x, src, r, s)) add.Add(x.Clone());
            foreach (var x in map["off"].Items) off.Add(x.Clone());
            if (auto && !off.Items.Any(x => Same(x, src, r, s))) off.Add(JNode.Obj().Set("src", src).Set("r", r).Set("s", s));
            return Make(add, off);
        }

        /// <summary>빈 목록은 적지 않는다 (아무것도 바꾸지 않았으면 원본과 같은 빈 객체).</summary>
        static JNode Make(JNode add, JNode off)
        {
            var m = JNode.Obj();
            if (add.Count > 0) m.Set("add", add);
            if (off.Count > 0) m.Set("off", off);
            return m;
        }
    }
}
