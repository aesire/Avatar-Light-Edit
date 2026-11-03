// 스튜디오 주변 가림용 깊이 (StudioAo가 숨은 카메라의 대체 셰이더로 씀, 2026-10-23).
// 카메라 깊이 텍스처(_CameraDepthTexture)는 이 렌더러에서 주변 가림을 계산할 때 묶여 있지 않았고, 깊이·노멀 텍스처의 16비트 깊이는
// 비스듬한 면에 가로 줄무늬를 만들어 직접 그린다. 불투명·컷아웃만 (반투명은 가림 합성 뒤에 그려지므로 빼야 맞다).
// 시선 방향 깊이(m)를 쓴다: 지운 값 0 = 없음(배경).
Shader "Hidden/VrcProto/StudioAoDepth"
{
    Properties { _MainTex ("", 2D) = "white" {} _Color ("", Color) = (1, 1, 1, 1) _Cutoff ("", Float) = 0.5 }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color; float _Cutoff;
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float depth : TEXCOORD1; };
    v2f vert(appdata_base v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
        o.depth = -UnityObjectToViewPos(v.vertex).z;
        return o;
    }
    float4 fragOpaque(v2f i) : SV_Target { return i.depth; }
    float4 fragCutout(v2f i) : SV_Target { clip(tex2D(_MainTex, i.uv).a * _Color.a - _Cutoff); return i.depth; }
    ENDCG
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass { Cull Off CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOpaque
            ENDCG }
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" }
        Pass { Cull Off CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCutout
            ENDCG }
    }
}
