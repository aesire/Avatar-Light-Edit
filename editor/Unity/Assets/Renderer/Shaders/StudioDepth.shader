// 스튜디오 심도용 깊이 (StudioDof가 대체 셰이더로 씀). 카메라 깊이 텍스처는 ShadowCaster 패스에 기대고 반투명 머리카락을 빼므로 직접 그린다.
// 모든 물체를 그리고, 원래 머티리얼의 _MainTex 알파 × _Color 알파가 0.5보다 작은 곳은 버린다 (컷아웃·반투명 머리카락 끝).
// 1/깊이(m)를 쓴다: 지운 값 0 = 아주 먼 곳.
Shader "Hidden/VrcProto/StudioDepth"
{
    Properties { _MainTex ("", 2D) = "white" {} _Color ("", Color) = (1, 1, 1, 1) }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float depth : TEXCOORD1; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.depth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a * _Color.a - 0.5);
                return 1 / max(i.depth, 1e-4);
            }
            ENDCG
        }
    }
}
