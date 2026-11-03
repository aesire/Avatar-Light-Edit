// 후처리 (PostProcessProvider + PostFx). Linear HDR 버퍼에서 계산한다.
//   0 Prefilter  밝은 부분만 남기며 1/2로 줄이기 (블룸 문턱값 + 부드러운 무릎)
//   1 Down       1/2로 줄이기 (4탭 상자)
//   2 Up         한 단계 위로 키우며 더하기 (텐트 필터, 퍼짐 정도 _Scatter)
//   3 Final      블룸 더하기 → 노출 → 화이트 밸런스 → 대비·채도 → 톤 매핑 → 비네트
// 톤 매핑 식: Neutral은 John Hable 계열 곡선(Unity 후처리 스택과 같은 계수), ACES는 Narkowicz의 근사식.
Shader "Hidden/VrcProto/PostProcess"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex, _BaseTex, _BloomTex;
    float4 _MainTex_TexelSize;
    float4 _Threshold;          // x 문턱값, y 무릎(문턱값×soft), z 2·무릎, w 1/(4·무릎)
    float _Scatter, _BloomIntensity, _Exposure, _Contrast, _Saturation, _Tonemap, _Vignette;
    float3 _BloomTint, _WhiteBalance;

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

    half3 Box4(float2 uv)
    {
        float4 d = _MainTex_TexelSize.xyxy * float4(-1, -1, 1, 1);
        half3 s = tex2D(_MainTex, uv + d.xy).rgb + tex2D(_MainTex, uv + d.zy).rgb + tex2D(_MainTex, uv + d.xw).rgb + tex2D(_MainTex, uv + d.zw).rgb;
        return s * 0.25;
    }

    half3 Tent9(float2 uv)
    {
        float4 d = _MainTex_TexelSize.xyxy * float4(1, 1, -1, 0);
        half3 s = tex2D(_MainTex, uv - d.xy).rgb;
        s += tex2D(_MainTex, uv - d.wy).rgb * 2;
        s += tex2D(_MainTex, uv - d.zy).rgb;
        s += tex2D(_MainTex, uv + d.zw).rgb * 2;
        s += tex2D(_MainTex, uv).rgb * 4;
        s += tex2D(_MainTex, uv + d.xw).rgb * 2;
        s += tex2D(_MainTex, uv + d.zy).rgb;
        s += tex2D(_MainTex, uv + d.wy).rgb * 2;
        s += tex2D(_MainTex, uv + d.xy).rgb;
        return s / 16;
    }

    half4 FragPrefilter(v2f i) : SV_Target
    {
        half3 c = min(Box4(i.uv), 64); // 아주 밝은 점 하나가 번쩍이지 않게
        half br = max(c.r, max(c.g, c.b));
        half soft = clamp(br - _Threshold.x + _Threshold.y, 0, _Threshold.z);
        soft = soft * soft * _Threshold.w;
        half k = max(soft, br - _Threshold.x) / max(br, 1e-5);
        return half4(c * k, 1);
    }

    half4 FragDown(v2f i) : SV_Target { return half4(Box4(i.uv), 1); }

    half4 FragUp(v2f i) : SV_Target { return half4(tex2D(_BaseTex, i.uv).rgb + Tent9(i.uv) * _Scatter, 1); }

    float3 NeutralCurve(float3 x, float a, float b, float c, float d, float e, float f)
    {
        return ((x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f)) - e / f;
    }
    float3 Neutral(float3 x)
    {
        const float a = 0.2, b = 0.29, c = 0.24, d = 0.272, e = 0.02, f = 0.3, white = 5.3;
        float3 ws = 1.0 / NeutralCurve(white, a, b, c, d, e, f);
        return NeutralCurve(x * ws, a, b, c, d, e, f) * ws;
    }
    float3 Aces(float3 x)
    {
        x *= 0.6;
        return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
    }

    half4 FragFinal(v2f i) : SV_Target
    {
        half4 src = tex2D(_MainTex, i.uv);
        float3 c = src.rgb;
        if (_BloomIntensity > 0) c += tex2D(_BloomTex, i.uv).rgb * _BloomIntensity * _BloomTint;
        c *= _Exposure * _WhiteBalance;
        // 대비: 중간 회색(0.18) 기준 로그 공간에서 늘리고 줄인다
        c = 0.18 * pow(max(c, 1e-6) / 0.18, _Contrast);
        float l = dot(c, float3(0.2126, 0.7152, 0.0722));
        c = max(lerp(l.xxx, c, _Saturation), 0);
        if (_Tonemap > 1.5) c = Aces(c);
        else if (_Tonemap > 0.5) c = Neutral(c);
        if (_Vignette > 0)
        {
            float2 d = (i.uv - 0.5) * float2(_MainTex_TexelSize.y * _MainTex_TexelSize.z, 1); // 화면비 보정 (원형)
            c *= lerp(1, smoothstep(0.85, 0.2, length(d)), _Vignette);
        }
        return half4(c, src.a);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragPrefilter
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragDown
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragUp
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragFinal
            ENDCG }
    }
}
