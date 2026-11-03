// VR 거울 (VrMirror, 2026-10-23): 반사 카메라가 그린 텍스처를 화면 좌표로 붙인다 (눈마다 같은 투영이라 거울 자리에 그대로 맞는다).
//   테두리(_Border, UV 비율)는 _FrameColor — 개인 거울은 손이 닿아 잡을 수 있을 때 밝아진다. 뒷면은 어두운 판.
Shader "Hidden/VrcProto/Mirror"
{
    Properties
    {
        _MainTex ("Reflection", 2D) = "black" {}
        _FrameColor ("Frame", Color) = (0.1, 0.1, 0.12, 1)
        _Border ("Border (uv x, y)", Vector) = (0.01, 0.01, 0, 0)
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _FrameColor, _Border;
            struct v2f { float4 pos : SV_POSITION; float4 sp : TEXCOORD0; float2 uv : TEXCOORD1; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.sp = ComputeScreenPos(o.pos);
                o.uv = v.texcoord.xy;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                if (any(i.uv < _Border.xy) || any(i.uv > 1 - _Border.xy)) return _FrameColor;
                return fixed4(tex2Dproj(_MainTex, UNITY_PROJ_COORD(i.sp)).rgb, 1);
            }
            ENDCG
        }
        Pass
        {
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _FrameColor;
            float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
            fixed4 frag() : SV_Target { return fixed4(_FrameColor.rgb * 0.6, 1); }
            ENDCG
        }
    }
}
