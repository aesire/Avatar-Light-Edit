// 촬영 마무리 (2026-10-24, Capture.cs): 예전엔 CPU에서 픽셀마다 부동소수 배열로 하던 일을 GPU에서 한다 (4K 투명 한 장에 수백 MB를 새로 만들었다).
// 입력은 Linear 반정밀 렌더 결과(_MainTex = 검정 배경 또는 보통 배경, _White = 흰 배경), 출력 픽셀 하나 = 원본 _Scale × _Scale 칸 평균.
//   0 Matte   검정·흰 차이로 알파 (C = c·a + bg·(1−a) → 흰 − 검정 = 1 − a), 칸마다 프리멀티플라이드로 더해 평균한 뒤 일반 알파로 편다
//   1 Opaque  평균, 알파 1
//   2 Raw     평균, 알파 그대로 (방송 오버레이가 화면에 내보내는 알파를 보는 시험용)
// 원본 칸 가운데를 겹선형으로 읽으면 정확히 그 텍셀이다 (uv라 플랫폼마다 줄 순서가 달라도 같다).
Shader "Hidden/VrcProto/CaptureResolve"
{
    Properties { _MainTex ("", 2D) = "black" {} _White ("", 2D) = "white" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex, _White;
    float4 _MainTex_TexelSize;
    int _Scale;

    float2 Cell(float2 uv) { return floor(uv * _MainTex_TexelSize.zw / _Scale) * _Scale; }
    float2 Uv(float2 cell, int x, int y) { return (cell + float2(x, y) + 0.5) * _MainTex_TexelSize.xy; }

    float4 FragMatte(v2f_img i) : SV_Target
    {
        float2 cell = Cell(i.uv);
        float3 pc = 0; float pa = 0;
        [loop] for (int y = 0; y < _Scale; y++)
        [loop] for (int x = 0; x < _Scale; x++)
        {
            float4 uv = float4(Uv(cell, x, y), 0, 0);
            float3 b = tex2Dlod(_MainTex, uv).rgb, w = tex2Dlod(_White, uv).rgb;
            pa += saturate(1 - dot(w - b, 1.0 / 3));
            pc += b;   // b = c·a (프리멀티플라이드)
        }
        float n = _Scale * _Scale;
        pc /= n; pa /= n;
        return pa > 1e-4 ? float4(saturate(pc / pa), pa) : 0;
    }

    float4 Average(float2 uv0)
    {
        float2 cell = Cell(uv0);
        float4 s = 0;
        [loop] for (int y = 0; y < _Scale; y++)
        [loop] for (int x = 0; x < _Scale; x++) s += tex2Dlod(_MainTex, float4(Uv(cell, x, y), 0, 0));
        return s / (_Scale * _Scale);
    }

    float4 FragOpaque(v2f_img i) : SV_Target { return float4(Average(i.uv).rgb, 1); }
    float4 FragRaw(v2f_img i) : SV_Target { return Average(i.uv); }

    // 3 Pack  녹화(MP4)용: 위아래 뒤집기 · Linear → sRGB · BGRA 순서로 일반(UNorm) RGBA8 텍스처에 → 메모리가 위 줄부터 B,G,R,A.
    //         sRGB BGRA 텍스처는 GPU마다 비동기 읽기를 못 하는 경우가 있어(B8G8R8A8_SRGB) 변환을 여기서 한다.
    float3 ToSrgb(float3 c) { c = saturate(c); return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1 / 2.4) - 0.055; }
    float4 FragPack(v2f_img i) : SV_Target
    {
        float3 c = ToSrgb(tex2D(_MainTex, float2(i.uv.x, 1 - i.uv.y)).rgb);
        return float4(c.b, c.g, c.r, 1);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragMatte
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragOpaque
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragRaw
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPack
            #pragma target 3.0
            ENDCG }
    }
}
