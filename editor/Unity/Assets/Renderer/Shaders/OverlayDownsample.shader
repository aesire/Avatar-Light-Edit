// 방송 창 슈퍼샘플링 줄이기 (2026-10-24, OverlaySupersample): 배율이 정수가 아닐 때(1.5배) 겹선형 한 번이면 원본 픽셀 일부를 건너뛴다.
// 출력 픽셀 하나가 덮는 원본 범위(_Ratio × _Ratio)를 네 겹선형 탭(가운데에서 ±_Ratio/4)으로 고르게 평균한다. 알파도 같이 (프리멀티플라이드 그대로).
Shader "Hidden/VrcProto/OverlayDownsample"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Ratio;

            half4 frag(v2f_img i) : SV_Target
            {
                float2 d = _MainTex_TexelSize.xy * _Ratio * 0.25;
                return (tex2D(_MainTex, i.uv + float2(-d.x, -d.y)) + tex2D(_MainTex, i.uv + float2(d.x, -d.y))
                      + tex2D(_MainTex, i.uv + float2(-d.x, d.y)) + tex2D(_MainTex, i.uv + float2(d.x, d.y))) * 0.25;
            }
            ENDCG
        }
    }
}
