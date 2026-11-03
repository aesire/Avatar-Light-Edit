// 촬영 지터 누적 (Capture, 2026-10-23): 카메라를 서브픽셀만큼 흔들며 찍은 장들을 _Weight(= 1/장 수)씩 더한다 (Blend One One).
Shader "Hidden/VrcProto/Accumulate"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Weight ("Weight", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend One One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Weight;
            float4 frag(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv) * _Weight; }
            ENDCG
        }
    }
}
