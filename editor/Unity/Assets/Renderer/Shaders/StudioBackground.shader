// 스튜디오 배경 (StudioManager): 화면 전체 사각형을 먼 평면 깊이로 그려, 아바타가 없는 곳(깊이가 비어 있는 곳)에만 배경이 보인다.
//   _Mode 0 = 360° 파노라마(등장방형 이미지·HDRI): 화면 점의 시선 방향으로 이미지를 고른다 (_Rotation 도, 세로축)
//   _Mode 1 = 평면 이미지: 화면에 꽉 차게(가로세로 비율 유지, 넘치는 쪽을 자름)
//   _Mode 2 = 그라데이션: 화면 아래 _Bottom → 위 _Top (선형 색, 2026-10-23)
// 시선 방향은 C#이 카메라마다 _Fwd·_Right·_Up(시야 반각이 곱해진 축)으로 넣는다. 렌더 텍스처(촬영)는 _ProjectionParams.x로 위아래를 맞춘다.
Shader "Hidden/VrcProto/StudioBackground"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Mode ("Mode", Float) = 0
        _Exposure ("Exposure", Float) = 1
        _Rotation ("Rotation", Float) = 0
        _Aspect ("Image aspect / screen aspect", Float) = 1
        _Top ("Top", Color) = (1, 1, 1, 1)
        _Bottom ("Bottom", Color) = (0.5, 0.5, 0.5, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Plane" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Mode, _Exposure, _Rotation, _Aspect;
            float4 _Fwd, _Right, _Up, _Top, _Bottom;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                #if defined(UNITY_REVERSED_Z)
                o.pos = float4(v.vertex.xy, 0.0, 1.0);
                #else
                o.pos = float4(v.vertex.xy, 1.0, 1.0);
                #endif
                o.ndc = float2(v.vertex.x, v.vertex.y * _ProjectionParams.x);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float3 col;
                if (_Mode < 0.5)
                {
                    float3 d = normalize(_Fwd.xyz + i.ndc.x * _Right.xyz + i.ndc.y * _Up.xyz);
                    float lon = atan2(d.x, d.z) + radians(_Rotation);
                    float lat = asin(clamp(d.y, -1, 1));
                    float2 uv = float2(frac(lon / (2 * UNITY_PI) + 0.5), lat / UNITY_PI + 0.5);
                    col = tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb;
                }
                else if (_Mode > 1.5)
                {
                    col = lerp(_Bottom.rgb, _Top.rgb, saturate(i.ndc.y * 0.5 + 0.5));
                }
                else
                {
                    float2 uv = i.ndc * 0.5;
                    // 비율 유지하며 꽉 채우기: 이미지가 화면보다 넓으면 가로를 자른다
                    if (_Aspect > 1) uv.x /= _Aspect; else uv.y *= _Aspect;
                    col = tex2D(_MainTex, uv + 0.5).rgb;
                }
                return float4(col * _Exposure, 1);
            }
            ENDCG
        }
    }
}
