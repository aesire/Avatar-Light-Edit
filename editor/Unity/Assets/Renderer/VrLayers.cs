// VR 레이어 (VrFirstPerson.cs에서 옮김 — 2026-11-03 아바타 안으로 들어가기를 빼면서). 1인칭·3인칭 레이어는 이제 쓰는 곳이 없지만
// 미리보기·촬영·거울이 마스크로 빼는 값이라 그대로 둔다.
using UnityEngine;

namespace VrcProto.Viewer
{
    public static class VrLayers
    {
        /// <summary>VrOnly: VR 눈 카메라에만 보이는 것(바닥 격자) · ThirdPerson: 눈 카메라에서 빼는 원본 · FirstPerson: 눈 카메라 전용 1인칭 사본.</summary>
        public const int VrOnly = 28, ThirdPerson = 29, FirstPerson = 30;
        public const int VrOnlyMask = 1 << VrOnly, FirstPersonMask = 1 << FirstPerson, ThirdPersonMask = 1 << ThirdPerson;
        /// <summary>미리보기·촬영 카메라가 빼는 레이어.</summary>
        public const int HiddenFromPreview = FirstPersonMask | VrOnlyMask;
        /// <summary>Scenery (2026-10-27): 지금 아바타가 아닌 장면 물체(스튜디오 소품·바닥·함께 세운 아바타). "아바타만" 카메라가 뺀다.</summary>
        public const int Scenery = 27, SceneryMask = 1 << Scenery;

        public static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
