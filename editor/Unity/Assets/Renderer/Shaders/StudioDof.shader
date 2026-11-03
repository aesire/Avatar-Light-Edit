// 스튜디오 피사계 심도 (StudioDof). 카메라 깊이 텍스처로 흐림 반경(CoC)을 구해 반 해상도에서 원판 모양으로 모은다.
//   0 CoC        깊이 → 부호 있는 흐림 반경 (픽셀, 음수 = 초점보다 앞)
//   1 Prefilter  1/2로 줄이며 색 + CoC(네 점 중 절댓값이 큰 것)
//   2 Bokeh      원판 표본 81개 (고리 4개). 뒤쪽은 초점 맞은 앞 물체 위로 번지지 않게, 앞쪽은 따로 모아 덮는다
//   3 Post       텐트 필터로 고르게
//   4 Combine    원본과 흐린 결과를 CoC로 섞는다 (알파는 원본 그대로 → 투명 배경 촬영도 된다)
// 깊이는 StudioDof가 Hidden/VrcProto/StudioDepth로 따로 그린 1/깊이(_DepthTex)다. 아무것도 없는 곳(스튜디오 배경·빈 하늘) = 0 = 아주 먼 곳.
Shader "Hidden/VrcProto/StudioDof"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex, _CocTex, _DofTex;
    sampler2D_float _DepthTex;
    float4 _MainTex_TexelSize;
    float _Focus, _CocScale, _BokehRadius, _ShowFocus;

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

    half4 FragCoc(v2f i) : SV_Target
    {
        float2 duv = i.uv;
        #if UNITY_UV_STARTS_AT_TOP
        if (_MainTex_TexelSize.y < 0) duv.y = 1 - duv.y;
        #endif
        float d = 1 / max(tex2D(_DepthTex, duv).r, 1e-5);
        float coc = clamp((d - _Focus) / max(d, 1e-3) * _CocScale, -1, 1);
        return coc * _BokehRadius;
    }

    half4 FragPrefilter(v2f i) : SV_Target
    {
        float4 o = _MainTex_TexelSize.xyxy * float2(-0.5, 0.5).xxyy;
        half c0 = tex2D(_CocTex, i.uv + o.xy).r, c1 = tex2D(_CocTex, i.uv + o.zy).r, c2 = tex2D(_CocTex, i.uv + o.xw).r, c3 = tex2D(_CocTex, i.uv + o.zw).r;
        half mn = min(min(c0, c1), min(c2, c3)), mx = max(max(c0, c1), max(c2, c3));
        half coc = mx >= -mn ? mx : mn;
        half3 col = (tex2D(_MainTex, i.uv + o.xy).rgb + tex2D(_MainTex, i.uv + o.zy).rgb + tex2D(_MainTex, i.uv + o.xw).rgb + tex2D(_MainTex, i.uv + o.zw).rgb) * 0.25;
        return half4(min(col, 64), coc);
    }

    half Weigh(half coc, half r) { return saturate((coc - r + 2) / 2); }

    half4 FragBokeh(v2f i) : SV_Target
    {
        half4 c = tex2D(_MainTex, i.uv);
        half c0 = c.a;
        half3 bg = 0, fg = 0; half bgW = 0, fgW = 0;
        const int Rings = 4;
        [loop] for (int ring = 0; ring <= Rings; ring++)
        {
            int n = ring == 0 ? 1 : ring * 8;
            half r = _BokehRadius * ring / Rings;
            [loop] for (int k = 0; k < n; k++)
            {
                float a = 6.2831853 * k / n + ring * 0.5;
                float2 off = float2(cos(a), sin(a)) * r;
                half4 s = tex2Dlod(_MainTex, float4(i.uv + off * _MainTex_TexelSize.xy, 0, 0));
                half bw = Weigh(max(0, min(c0, s.a)), r);
                bg += s.rgb * bw; bgW += bw;
                half fw = Weigh(-s.a, r);
                fg += s.rgb * fw; fgW += fw;
            }
        }
        bg /= bgW + (bgW == 0);
        fg /= fgW + (fgW == 0);
        half alpha = min(1, fgW * 3.14159265 / 81);
        return half4(lerp(bg, fg, alpha), alpha);
    }

    half4 FragPost(v2f i) : SV_Target
    {
        float4 o = _MainTex_TexelSize.xyxy * float2(-0.5, 0.5).xxyy;
        return (tex2D(_MainTex, i.uv + o.xy) + tex2D(_MainTex, i.uv + o.zy) + tex2D(_MainTex, i.uv + o.xw) + tex2D(_MainTex, i.uv + o.zw)) * 0.25;
    }

    half4 FragCombine(v2f i) : SV_Target
    {
        half4 src = tex2D(_MainTex, i.uv);
        half coc = tex2D(_CocTex, i.uv).r;
        half4 dof = tex2D(_DofTex, i.uv);
        if (_ShowFocus > 0.5)   // 초점 영역 보기: 초점 맞은 곳은 원래 색, 앞은 빨강, 뒤는 파랑
        {
            half f = saturate(abs(coc) / max(_BokehRadius, 1));
            return half4(lerp(src.rgb, coc < 0 ? half3(1, 0.1, 0.1) : half3(0.1, 0.3, 1), f * 0.85), src.a);
        }
        half t = smoothstep(0.1, 1, abs(coc));
        half k = t + dof.a - t * dof.a;
        return half4(lerp(src.rgb, dof.rgb, k), src.a);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragCoc
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragPrefilter
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragBokeh
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragPost
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragCombine
            ENDCG }
    }
}
