// 스튜디오 주변 가림 (SSAO, RenderQuality → StudioAo, 2026-10-23). 스튜디오 고화질에서만.
//   0 가림: 숨은 카메라가 그린 깊이(_VrcProtoAoDepth, Hidden/VrcProto/StudioAoDepth — 불투명·컷아웃의 시선 깊이 m, 0 = 배경)와
//      이웃 깊이로 다시 만든 노멀에서 화면 점마다 반구 표본 12개 (Unity Post Processing의 Scalable AO와 같은 식,
//      다만 접평면 가까운 표본은 빼고 대비 곡선을 완만하게 — 넓은 면이 통째로 어두워지지 않게)
//   1·2 흐림: 가로·세로 9탭, 깊이가 많이 다른 이웃은 빼서 아바타 가장자리에서 배경으로 번지지 않게
//   3 합성: 불투명 물체·하늘을 그린 직후(반투명 전) 화면에 곱한다 (Blend DstColor Zero, 알파는 그대로 — 방송 오버레이의 투명 창)
//   깊이에는 불투명·컷아웃만 들어가므로 배경(깊이 없음)과 반투명 머리카락에는 가림이 생기지 않는다.
//   촬영 지터 누적에서는 장마다 _VrcProtoAoSeed를 바꿔 잡티가 평균으로 사라진다.
Shader "Hidden/VrcProto/StudioAo"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Radius ("Radius (m)", Float) = 0.06
        _Intensity ("Intensity", Float) = 1.5
        _Strength ("Strength", Range(0, 1)) = 0.5
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex; float4 _MainTex_TexelSize;
    sampler2D_float _VrcProtoAoDepth; float4 _VrcProtoAoDepth_TexelSize;
    sampler2D _VrcProtoAoTex;
    float _Radius, _Intensity, _Strength, _VrcProtoAoSeed;

    static const int Samples = 12;

    float3 ViewPos(float2 uv, float depth, float2 p11_22, float2 p13_31)
    {
        return float3((uv * 2 - 1 - p13_31) / p11_22 * depth, depth);
    }

    float Depth(float2 uv)
    {
        float d = tex2Dlod(_VrcProtoAoDepth, float4(uv, 0, 0)).r;
        return d <= 0 ? 1e6 : d;
    }

    /// 이웃 깊이로 노멀 (좌우·위아래 중 깊이 차이가 작은 쪽 — 가장자리에서 뒤쪽 물체와 섞이지 않게). z는 앞(카메라에서 멀어지는 쪽)이 +
    float3 Normal(float2 uv, float3 c, float2 p11_22, float2 p13_31)
    {
        float2 dx = float2(_VrcProtoAoDepth_TexelSize.x, 0), dy = float2(0, _VrcProtoAoDepth_TexelSize.y);
        float3 l = ViewPos(uv - dx, Depth(uv - dx), p11_22, p13_31), r = ViewPos(uv + dx, Depth(uv + dx), p11_22, p13_31);
        float3 b = ViewPos(uv - dy, Depth(uv - dy), p11_22, p13_31), t = ViewPos(uv + dy, Depth(uv + dy), p11_22, p13_31);
        float3 hx = abs(r.z - c.z) < abs(c.z - l.z) ? r - c : c - l;
        float3 hy = abs(t.z - c.z) < abs(c.z - b.z) ? t - c : c - b;
        float3 n = normalize(cross(hy, hx));
        return n.z > 0 ? -n : n;
    }

    float Noise(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); }

    float3 SamplePoint(float2 px, int i)
    {
        float s = (float)i / Samples;
        float gn = Noise(px + _VrcProtoAoSeed * float2(5.3, 7.1));
        float u = frac(Noise(float2(0, i) + _VrcProtoAoSeed) + gn) * 2 - 1;
        float th = (Noise(float2(1, i) + _VrcProtoAoSeed * 3.7) + gn) * UNITY_PI * 2;
        float3 v = float3(float2(cos(th), sin(th)) * sqrt(1 - u * u), u);
        return v * sqrt(s + 1.0 / Samples);   // 가까운 표본을 더 많이
    }

    float4 FragAo(v2f_img i) : SV_Target
    {
        float depth = Depth(i.uv);
        if (depth > 1e5) return 1;
        float2 p11_22 = float2(unity_CameraProjection._11, unity_CameraProjection._22);
        float2 p13_31 = float2(unity_CameraProjection._13, unity_CameraProjection._23);
        float3x3 proj = (float3x3)unity_CameraProjection;
        float3 vpos = ViewPos(i.uv, depth, p11_22, p13_31);
        float3 n = Normal(i.uv, vpos, p11_22, p13_31);
        float2 px = i.uv * _ScreenParams.xy;
        float ao = 0;
        for (int s = 0; s < Samples; s++)
        {
            float3 v1 = SamplePoint(px, s);
            v1 = faceforward(v1, -n, v1);
            float3 vp1 = vpos + v1 * _Radius;
            float3 sp1 = mul(proj, vp1);
            float2 uv1 = (sp1.xy / vp1.z + 1) * 0.5;
            float d2 = Depth(uv1);
            if (any(uv1 < 0) || any(uv1 > 1) || d2 > 1e5) continue;
            float3 v2 = ViewPos(uv1, d2, p11_22, p13_31) - vpos;
            // 접평면 바로 위(약 9° 아래)의 표본은 빼서 평평한 면이 어두워지지 않게,
            // 1.2 cm보다 덜 솟은 것도 빼서 애니메 얼굴의 눈가·콧등처럼 얕게 오목한 곳이 지저분해지지 않게 (옷단·양말 위·발밑 같은 접촉만)
            float a1 = max(dot(v2, n) - 0.15 * length(v2) - 0.002 * depth - 0.012, 0);
            float a2 = dot(v2, v2) + 1e-4;
            ao += a1 / a2;
        }
        ao *= _Radius;
        ao = pow(saturate(ao * _Intensity / Samples), 0.85);
        return 1 - ao;
    }

    float4 Blur(float2 uv, float2 dir)
    {
        float depth = Depth(uv);
        static const float w[5] = { 0.2270270, 0.1945946, 0.1216216, 0.0540540, 0.0162162 };
        float sum = tex2D(_MainTex, uv).r * w[0], wsum = w[0];
        for (int k = 1; k < 5; k++)
        {
            [unroll] for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                float2 u = uv + dir * _MainTex_TexelSize.xy * k * sgn;
                float d2 = Depth(u);
                float ww = w[k] * saturate(1 - abs(d2 - depth) / (0.03 + depth * 0.01));
                sum += tex2D(_MainTex, u).r * ww; wsum += ww;
            }
        }
        return sum / max(wsum, 1e-4);
    }

    float4 FragBlurH(v2f_img i) : SV_Target { return Blur(i.uv, float2(1.5, 0)); }
    float4 FragBlurV(v2f_img i) : SV_Target { return Blur(i.uv, float2(0, 1.5)); }

    struct appdata { float4 vertex : POSITION; };
    struct v2fq { float4 pos : SV_POSITION; float4 scr : TEXCOORD0; };
    v2fq VertQuad(appdata v)
    {
        v2fq o;
        o.pos = float4(v.vertex.xy, UNITY_NEAR_CLIP_VALUE, 1);
        o.scr = ComputeScreenPos(o.pos);
        return o;
    }
    float4 FragComposite(v2fq i) : SV_Target
    {
        float ao = tex2D(_VrcProtoAoTex, i.scr.xy / i.scr.w).r;
        return float4((lerp(1, ao, _Strength)).xxx, 1);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragAo
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragBlurH
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragBlurV
            #pragma target 3.0
            ENDCG }
        Pass
        {
            Blend DstColor Zero, Zero One
            CGPROGRAM
            #pragma vertex VertQuad
            #pragma fragment FragComposite
            ENDCG
        }
    }
}
