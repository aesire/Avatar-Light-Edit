// 방송 오버레이 알파 바로잡기 (2026-10-24, Overlay.cs OverlayAlphaFix): 반투명까지 다 그린 뒤 불투명이 덮은 픽셀의 알파만 1로 덮어쓴다.
// 셰이더 알파는 믿을 수 없다 — 불투명 재질도 텍스처 알파(마스크 등)를 그대로 쓰거나, 반투명(SrcAlpha OneMinusSrcAlpha)이 불투명 위에서
// 알파를 a² + (1 − a)로 깎아 게임이 비쳐 보였다(앞머리가 잘려 보이던 것).
// 화면 전체 삼각형을 먼 평면(깊이 끝)에 두고 ZTest Greater: 깊이 버퍼에 무엇이 그려진 곳(불투명·컷아웃·깊이를 쓰는 반투명)에서만 통과한다.
// 지운 채로인 곳은 깊이가 같아 통과하지 않는다. 역 Z 플랫폼에서는 Unity가 ZTest를 뒤집으므로 먼 평면 값만 맞춘다.
// 패스 1 크로마키 (2026-11-12, OBS로 보내기 · 배경 크로마키): 후처리까지 끝난 그림 밑에 _Key 색을 깐다 — 색 = 그림 + (1 − 알파) × 키, 알파 1.
//   그림은 투명 배경 위에 그린 것이라 이미 알파를 곱한 색이다. OBS 윈도우 캡처처럼 알파를 못 읽는 곳에서 크로마키 필터로 뺀다.
Shader "Hidden/VrcProto/OverlayAlpha"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Greater ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            float4 vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
            #if UNITY_REVERSED_Z
                return float4(uv * 2 - 1, 0, 1);
            #else
                return float4(uv * 2 - 1, 1, 1);
            #endif
            }

            fixed4 frag() : SV_Target { return fixed4(0, 0, 0, 1); }
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend OneMinusDstAlpha One, One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            fixed4 _Key;

            float4 vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2 - 1, UNITY_NEAR_CLIP_VALUE, 1);
            }

            fixed4 frag() : SV_Target { return fixed4(_Key.rgb, 1); }
            ENDCG
        }
    }
}
