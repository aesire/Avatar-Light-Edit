// 빌드에 없는 셰이더를 쓰는 머티리얼의 "표시용" 셰이더. 원본 셰이더로 변환하거나 흉내 내지 않는다.
// _MainTex × _Color 에 사선 줄무늬를 얹어 미지원임을 화면에서 알 수 있게 한다.
Shader "Hidden/VrcProto/Unsupported"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.texcoord, _MainTex); return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                float stripe = step(0.5, frac((i.pos.x + i.pos.y) / 16.0));
                return fixed4(lerp(c.rgb, float3(1, 0, 1), stripe * 0.35), 1);
            }
            ENDCG
        }
    }
}
