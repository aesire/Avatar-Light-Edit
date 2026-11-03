// 와이어프레임 보기용 대체 셰이더 (Camera.SetReplacementShader + GL.wireframe).
// 원본 머티리얼은 건드리지 않고, 보조 카메라가 같은 메시를 이 셰이더로 한 번 더 그린다.
Shader "Hidden/VrcProto/Wire"
{
    Properties
    {
        _WireColor ("Color", Color) = (0.1, 0.1, 0.12, 0.75)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _VrcProtoWireColor;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            float4 frag(v2f i) : SV_Target { return _VrcProtoWireColor; }
            ENDCG
        }
    }
}
