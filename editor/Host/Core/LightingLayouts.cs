// 조명 배치 프리셋과 방송 화질 값 (2026-10-23 OverlayController에서 2026-11-01에 옮김: 에디터 오버레이와 창 없는 촬영이 같은 값을 쓴다).
// 주광은 환경 "밝은 월드"(방향 150° · 높이 50°)에 더하는 값: 방향 +30 = 아바타 정면, −150 = 뒤. 앞 채움은 환경의 정면광(정면 10° 방향광, 배율 front),
// 자리가 중요한 채움(턱 아래·반대쪽 옆)만 추가 조명 1번(그림자 없는 흰 포인트).
// 채움광 높이는 아바타 눈높이(IR viewPosition, 없으면 1.2 m) 기준. 그림자는 부드럽게, 머리 장식 그림자는 끈다
// (위에서 비추면 머리 장식이 머리카락에 지저분한 그림자를 떨어뜨리고, lilToon은 채움광으로 그 그림자를 거의 밝히지 않는다).
using System;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class LightingLayouts
    {
        public static readonly string[] Keys = { "topfill", "front", "butterfly", "side45", "rim" };

        /// <summary>IR viewPosition의 높이, 없으면 1.2 m.</summary>
        public static float EyeHeight(float[]? viewPosition) => viewPosition != null && viewPosition.Length >= 2 && viewPosition[1] > 0.3f ? viewPosition[1] : 1.2f;

        public static (string Provider, (string Name, JNode Value)[] Params)[] Get(string key, float eyeHeight)
        {
            float h = eyeHeight;
            (string, JNode)[] Sun(double yaw, double pitch, bool shadows, double front, double strength = 0.4, double intensity = 1.3) => new[]
            {
                ("preset", JNode.From("bright")), ("intensity", JNode.From(intensity)), ("yaw", JNode.From(yaw)), ("pitch", JNode.From(pitch)), ("front", JNode.From(front)),
                ("shadows", JNode.From(shadows)), ("shadowStrength", JNode.From(strength)), ("shadowSoftness", JNode.From(0.6)), ("accessoryShadows", JNode.From(false)), ("ambient", JNode.From(1.0)),
            };
            (string, JNode)[] Fill(bool on, double around = 0, double dh = 0.1, double dist = 1.5, double intensity = 1.5) => new[]
            {
                ("l1_on", JNode.From(on)), ("l1_type", JNode.From("point")), ("l1_color", JNode.FloatArray(new float[] { 1, 1, 1 })), ("l1_intensity", JNode.From(intensity)),
                ("l1_range", JNode.From(6.0)), ("l1_around", JNode.From(around)), ("l1_height", JNode.From(Math.Round(h + dh, 2))), ("l1_distance", JNode.From(dist)),
            };
            return key switch
            {
                "front" => new[] { ("UnityLighting", Sun(30, -20, false, 0.5, intensity: 1.2)), ("Lights", Fill(false)) },
                "butterfly" => new[] { ("UnityLighting", Sun(30, 15, true, 0.6, 0.35)), ("Lights", Fill(true, 0, -0.45, 1.2, 1.2)) },
                "side45" => new[] { ("UnityLighting", Sun(75, -10, true, 0.3, 0.6)), ("Lights", Fill(true, -50, 0, 1.5, 1.0)) },
                "rim" => new[] { ("UnityLighting", Sun(-150, -20, false, 2.0, intensity: 1.5)), ("Lights", Fill(false)) },
                _ => new[] { ("UnityLighting", Sun(30, 0, true, 1.3, 0.4)), ("Lights", Fill(false)) },
            };
        }

        /// <summary>방송 화질 프리셋 = 고른 조명 배치 + 반사 세게 + 블룸 약하게 + 노출·대비·채도 조금.</summary>
        public static readonly (string Provider, (string Name, JNode Value)[] Params)[] BroadcastPost =
        {
            ("UnityLighting", new[] { ("reflection", JNode.From(1.5d)) }),
            ("PostProcess", new[] { ("bloom", JNode.From(0.5d)), ("bloomThreshold", JNode.From(0.9d)), ("bloomScatter", JNode.From(0.6d)), ("tonemap", JNode.From("none")),
                ("exposure", JNode.From(0.2d)), ("contrast", JNode.From(0.1d)), ("saturation", JNode.From(0.12d)), ("vignette", JNode.From(0d)) }),
        };
    }
}
