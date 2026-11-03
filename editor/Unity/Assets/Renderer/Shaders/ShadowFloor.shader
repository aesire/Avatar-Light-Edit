// 그림자 바닥 (StudioManager, 2026-10-23): 발밑 넓은 판이 주광 그림자만 화면에 곱한다 (Blend DstColor Zero).
//   그림자가 없는 곳은 1을 곱해 보이지 않고, 그림자 진 곳은 (1 − 진하기)를 곱한다.
//   투명 촬영(검정·흰 배경 두 번 → 차이로 알파)에서는 검정 배경 = 0, 흰 배경 = 1 − 그림자 → 그림자만 반투명 검정으로 남는다.
//   배경(스튜디오 배경 이미지·그라데이션)은 불투명 물체보다 먼저 그려져 있으므로 그 위에 곱해진다. ZWrite Off — 다른 것을 가리지 않는다.
//   ShadowCaster 패스는 카메라 깊이 텍스처(화면 공간 그림자)에 바닥 깊이를 넣으려고 둔다 (렌더러는 그림자를 드리우지 않게 꺼 둔다).
// 단색 바닥 (2026-10-27, _Solid 1): 같은 판을 색으로 칠한다 — 환경광 + 주광(그림자 진하기만큼 가림). 가운데에서 멀어지면 알파로 배경에 녹아 판 끝이 보이지 않는다.
//   블렌드·깊이 쓰기는 StudioManager가 머티리얼 값으로 바꾼다 (그림자: DstColor Zero · 깊이 안 씀 / 단색: 알파 · 깊이 씀).
Shader "Hidden/VrcProto/ShadowFloor"
{
    Properties
    {
        _Strength ("Strength", Range(0, 1)) = 0.6
        _Solid ("Solid", Float) = 0
        _Color ("Color", Color) = (0.8, 0.8, 0.82, 1)
        _Fade ("Fade start, end (m)", Vector) = (8, 30, 0, 0)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst", Float) = 0
        _ZWrite ("ZWrite", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "Lighting.cginc"
            float _Strength, _Solid;
            fixed4 _Color;
            float4 _Fade;
            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD1; SHADOW_COORDS(0) };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                TRANSFER_SHADOW(o);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float lit = SHADOW_ATTENUATION(i);
                float shade = 1 - _Strength * (1 - lit);
                if (_Solid < 0.5) return fixed4(shade.xxx, 1);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 light = ShadeSH9(float4(0, 1, 0, 1)) + _LightColor0.rgb * saturate(l.y) * shade;
                float d = length(i.world.xz - unity_ObjectToWorld._m03_m23);
                float a = 1 - smoothstep(_Fade.x, _Fade.y, d);
                return fixed4(_Color.rgb * light, a);
            }
            ENDCG
        }
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            struct v2f { V2F_SHADOW_CASTER; };
            v2f vert(appdata_base v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag(v2f i) : SV_Target { SHADOW_CASTER_FRAGMENT(i) }
            ENDCG
        }
    }
}
