// 게임 화면과 합치기 (2026-11-12, Overlay.cs OverlaySupersample): 잡은 게임 그림(_GameTex)을 창 전체에 깔고,
// 그 위 상자(_Box = 창 UV x, y, 너비, 높이 — 왼쪽 아래 원점)에 아바타 텍스처(_MainTex)를 프리멀티플라이드로 얹는다. 결과 알파는 1 (보통 창).
// 게임 그림은 D3D 행 순서(위부터)로 올렸으므로 위아래를 뒤집어 읽는다. 아바타는 슈퍼샘플링 배율(_Ratio)이 1.5배면 네 탭 평균(OverlayDownsample과 같음).
Shader "Hidden/VrcProto/OverlayComposite"
{
    Properties { _MainTex ("", 2D) = "black" {} _GameTex ("", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _GameTex;
            float4 _MainTex_TexelSize;
            float4 _Box;
            float _Ratio, _HasGame;

            half4 frag(v2f_img i) : SV_Target
            {
                half3 c = _HasGame > 0.5 ? tex2D(_GameTex, float2(i.uv.x, 1 - i.uv.y)).rgb : half3(0.08, 0.08, 0.1);
                float2 l = (i.uv - _Box.xy) / _Box.zw;
                if (l.x >= 0 && l.y >= 0 && l.x <= 1 && l.y <= 1)
                {
                    half4 a;
                    if (_Ratio > 1.01 && _Ratio < 1.99)
                    {
                        float2 d = _MainTex_TexelSize.xy * _Ratio * 0.25;
                        a = (tex2D(_MainTex, l + float2(-d.x, -d.y)) + tex2D(_MainTex, l + float2(d.x, -d.y))
                           + tex2D(_MainTex, l + float2(-d.x, d.y)) + tex2D(_MainTex, l + float2(d.x, d.y))) * 0.25;
                    }
                    else a = tex2D(_MainTex, l);
                    c = a.rgb + c * (1 - saturate(a.a));
                }
                return half4(c, 1);
            }
            ENDCG
        }
    }
}
