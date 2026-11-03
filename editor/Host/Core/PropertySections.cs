// 자동 생성된 프로퍼티 목록을 lilToon 인스펙터와 비슷한 섹션으로 묶는 레이어 (PLAN 3.1).
// lilToon이 아닌 셰이더는 "Properties" 한 섹션으로 둔다.
using System;
using System.Collections.Generic;

namespace VrcProto.Host.Core
{
    public static class PropertySections
    {
        // (섹션, 접두사들) — 긴/구체적인 접두사가 먼저 오도록 정렬돼 있어야 한다.
        static readonly (string Section, string[] Prefixes)[] Lil =
        {
            ("Main 2nd", new[] { "Main2nd", "Color2nd" }),
            ("Main 3rd", new[] { "Main3rd", "Color3rd" }),
            ("Main", new[] { "MainTex", "MainGradation", "MainColorAdjust", "Color", "Main" }),
            ("Alpha Mask", new[] { "AlphaMask" }),
            ("Shadow", new[] { "Shadow" }),
            ("Rim Shade", new[] { "RimShade" }),
            ("Emission 2nd", new[] { "Emission2nd" }),
            ("Emission", new[] { "Emission" }),
            ("Normal Map", new[] { "Bump2nd", "BumpMap", "BumpScale", "Bump" }),
            ("Anisotropy", new[] { "Anisotropy" }),
            ("Backlight", new[] { "Backlight" }),
            ("Reflection", new[] { "Reflection", "Reflectance", "Smoothness", "Metallic", "GSAA", "ApplySpecular", "SpecularToon", "SpecularNormal", "SpecularBlur", "ApplyReflection" }),
            ("MatCap 2nd", new[] { "MatCap2nd" }),
            ("MatCap", new[] { "MatCap" }),
            ("Rim Light", new[] { "Rim" }),
            ("Glitter", new[] { "Glitter" }),
            ("Outline", new[] { "Outline" }),
            ("Parallax", new[] { "Parallax", "POM" }),
            ("Distance Fade", new[] { "DistanceFade" }),
            ("AudioLink", new[] { "AudioLink" }),
            ("Dissolve", new[] { "Dissolve" }),
            ("ID Mask", new[] { "IDMask" }),
            ("UDIM Discard", new[] { "UDIMDiscard" }),
            ("Fur", new[] { "Fur" }),
            ("Refraction", new[] { "Refraction" }),
            ("Gem", new[] { "Gem" }),
            ("Tessellation", new[] { "Tess" }),
            ("Lighting", new[] { "LightMinLimit", "LightMaxLimit", "MonochromeLighting", "AsUnlit", "VertexLight", "LightDirection", "lilDirectionalLight", "BeforeExposureLimit", "AlphaBoostFA", "ShadowEnvStrength", "lilShadowCasterBias" }),
            ("Rendering", new[] { "Cull", "ZWrite", "ZTest", "SrcBlend", "DstBlend", "BlendOp", "Offset", "ColorMask", "AlphaToMask", "Stencil", "TransparentMode", "AsOverlay", "Invisible", "Cutoff", "SubpassCutoff", "FlipNormal", "ShiftBackfaceUV", "BackfaceForceShadow", "BackfaceColor", "lilToonVersion", "TriMask", "UseDither", "Dither", "AlphaMode" }),
        };

        public static readonly string[] Order;
        static PropertySections()
        {
            var o = new List<string>();
            foreach (var s in new[] { "Main", "Main 2nd", "Main 3rd", "Alpha Mask", "Shadow", "Rim Shade", "Emission", "Emission 2nd", "Normal Map",
                "Anisotropy", "Backlight", "Reflection", "MatCap", "MatCap 2nd", "Rim Light", "Glitter", "Outline", "Parallax", "Distance Fade",
                "AudioLink", "Dissolve", "ID Mask", "UDIM Discard", "Fur", "Refraction", "Gem", "Tessellation", "Lighting", "Rendering", "Other" }) o.Add(s);
            Order = o.ToArray();
        }

        public static string Classify(string propName, bool isLilToon)
        {
            if (!isLilToon) return "Properties";
            var n = propName.StartsWith("_") ? propName.Substring(1) : propName;
            if (n.StartsWith("Use", StringComparison.Ordinal) && n.Length > 3 && char.IsUpper(n[3])) n = n.Substring(3);
            // 렌더링 섹션에 걸리는 Outline/Fur 변형(_OutlineSrcBlend 등)은 각 기능 섹션의 고급 값이므로 Rendering으로 보낸다
            if ((n.StartsWith("Outline") || n.StartsWith("Fur")) && (n.Contains("Blend") || n.Contains("ZWrite") || n.Contains("ZTest") || n.Contains("ColorMask") || n.Contains("Offset") || n.Contains("Stencil") || n.Contains("AlphaToMask") || n.EndsWith("Cull")))
                return "Rendering";
            foreach (var (section, prefixes) in Lil)
                foreach (var p in prefixes)
                    if (n.StartsWith(p, StringComparison.Ordinal)) return section;
            return "Other";
        }

        /// <summary>섹션을 켜고 끄는 프로퍼티 (_UseShadow 등). 꺼져 있으면 섹션을 접어 둔다.</summary>
        public static string? ToggleProperty(string section)
        {
            switch (section)
            {
                case "Main 2nd": return "_UseMain2ndTex";
                case "Main 3rd": return "_UseMain3rdTex";
                case "Shadow": return "_UseShadow";
                case "Rim Shade": return "_UseRimShade";
                case "Emission": return "_UseEmission";
                case "Emission 2nd": return "_UseEmission2nd";
                case "Normal Map": return "_UseBumpMap";
                case "Anisotropy": return "_UseAnisotropy";
                case "Backlight": return "_UseBacklight";
                case "Reflection": return "_UseReflection";
                case "MatCap": return "_UseMatCap";
                case "MatCap 2nd": return "_UseMatCap2nd";
                case "Rim Light": return "_UseRim";
                case "Glitter": return "_UseGlitter";
                case "Parallax": return "_UseParallax";
                case "AudioLink": return "_UseAudioLink";
                default: return null;
            }
        }

        /// <summary>
        /// 잘 안 쓰는 lilToon 프로퍼티 (UV 애니메이션·그라데이션 맵·데칼·스텐실 등): 머티리얼 탭이 기본으로 접어 둔다 (2026-10-22).
        /// 사용자 Unity 프로젝트의 lilToon 머티리얼 7,996개에서 셰이더 기본값을 바꾼 비율이 5% 미만인 것
        /// (섹션 켜기 프로퍼티가 있으면 그 섹션을 켠 머티리얼 중에서). 발광 그라데이션 키(_egc0 등)도 같이.
        /// </summary>
        public static bool IsRarelyUsed(string propName)
        {
            var n = propName.StartsWith("_") ? propName.Substring(1) : propName;
            return Rare.Contains(n) || System.Text.RegularExpressions.Regex.IsMatch(n, @"^e2?g[ac][i0-7]$");
        }

        static readonly HashSet<string> Rare = new HashSet<string>((
            " AAStrength AlphaBoostFA AlphaMaskScale AlphaToMask ApplyReflection ApplySpecular ApplySpecularFA AsOverlay AsUnlit AudioLink2Emission " +
            "AudioLink2Emission2nd AudioLink2Emission2ndGrad AudioLink2EmissionGrad AudioLink2Main2nd AudioLink2Main3rd AudioLink2Vertex " +
            "AudioLinkAsLocal AudioLinkDefaultValue AudioLinkLocalMap AudioLinkLocalMapParams AudioLinkMask AudioLinkMask_ScrollRotate " +
            "AudioLinkMask_UVMode AudioLinkStart AudioLinkUVMode AudioLinkUVParams AudioLinkVertexStart AudioLinkVertexStrength AudioLinkVertexUVMode " +
            "AudioLinkVertexUVParams BacklightBackfaceMask BacklightReceiveShadow BacklightViewStrength BeforeExposureLimit BlendOp BlendOpAlpha " +
            "BlendOpAlphaFA BlendOpFA Bump2ndMap_UVMode ColorMask DissolveColor DissolveMask DissolveNoiseMask DissolveNoiseMask_ScrollRotate " +
            "DissolveNoiseStrength DissolveParams DissolvePos DistanceFade DistanceFadeColor DistanceFadeMode DistanceFadeRimColor " +
            "DistanceFadeRimFresnelPower DitherMaxValue DitherTex DstBlendAlpha DstBlendAlphaFA DstBlendFA EmissionBlend EmissionFluorescence " +
            "EmissionMap_ScrollRotate EmissionMap_UVMode EmissionParallaxDepth EmissionUseGrad EnvRimBlur EnvRimBorder GlitterAngleRandomize " +
            "GlitterApplyShape GlitterApplyTransparency GlitterAtras GlitterColorTex_UVMode GlitterPostContrast GlitterShadowMask GlitterShapeTex " +
            "GlitterUVMode IDMask1 IDMask2 IDMask3 IDMask4 IDMask5 IDMask6 IDMask7 IDMask8 IDMaskCompile IDMaskControlsDissolve IDMaskFrom IDMaskIndex1 " +
            "IDMaskIndex2 IDMaskIndex3 IDMaskIndex4 IDMaskIndex5 IDMaskIndex6 IDMaskIndex7 IDMaskIndex8 IDMaskIsBitmap IDMaskPrior1 IDMaskPrior2 " +
            "IDMaskPrior3 IDMaskPrior4 IDMaskPrior5 IDMaskPrior6 IDMaskPrior7 IDMaskPrior8 Invisible LightMaxLimit Main2ndDissolveColor " +
            "Main2ndDissolveMask Main2ndDissolveNoiseMask Main2ndDissolveNoiseMask_ScrollRotate Main2ndDissolveNoiseStrength Main2ndDissolveParams " +
            "Main2ndDissolvePos Main2ndDistanceFade Main2ndEnableLighting Main2ndTexAlphaMode Main2ndTexAngle Main2ndTexDecalAnimation " +
            "Main2ndTexDecalSubParam Main2ndTexIsDecal Main2ndTexIsLeftOnly Main2ndTexIsMSDF Main2ndTexIsRightOnly Main2ndTexShouldCopy " +
            "Main2ndTexShouldFlipCopy Main2ndTexShouldFlipMirror Main2ndTex_Cull Main2ndTex_ScrollRotate Main2ndTex_UVMode Main3rdDissolveColor " +
            "Main3rdDissolveMask Main3rdDissolveNoiseMask Main3rdDissolveNoiseMask_ScrollRotate Main3rdDissolveNoiseStrength Main3rdDissolveParams " +
            "Main3rdDissolvePos Main3rdDistanceFade Main3rdEnableLighting Main3rdTexAlphaMode Main3rdTexDecalAnimation Main3rdTexDecalSubParam " +
            "Main3rdTexIsLeftOnly Main3rdTexIsMSDF Main3rdTexIsRightOnly Main3rdTexShouldCopy Main3rdTexShouldFlipCopy Main3rdTexShouldFlipMirror " +
            "Main3rdTex_Cull Main3rdTex_ScrollRotate Main3rdTex_UVMode MainColorAdjustMask MainGradationStrength MainGradationTex MainTex_ScrollRotate " +
            "MatCap2ndApplyTransparency MatCap2ndBackfaceMask MatCap2ndBlend MatCap2ndBlendUV1 MatCap2ndBumpMap MatCap2ndBumpScale MatCap2ndCustomNormal " +
            "MatCap2ndPerspective MatCap2ndVRParallaxStrength MatCap2ndZRotCancel MatCapApplyTransparency MatCapBlendUV1 MatCapBumpMap MatCapBumpScale " +
            "MatCapCustomNormal MatCapPerspective MatCapVRParallaxStrength MatCapZRotCancel MonochromeLighting OffsetFactor OffsetUnits " +
            "OutlineAlphaToMask OutlineBlendOp OutlineBlendOpAlpha OutlineBlendOpAlphaFA OutlineBlendOpFA OutlineColorMask OutlineCull OutlineDeleteMesh " +
            "OutlineDisableInVR OutlineDstBlendAlpha OutlineDstBlendAlphaFA OutlineDstBlendFA OutlineEnableLighting OutlineLitApplyTex OutlineLitOffset " +
            "OutlineLitScale OutlineLitShadowReceive OutlineOffsetFactor OutlineOffsetUnits OutlineSrcBlendAlpha OutlineSrcBlendAlphaFA " +
            "OutlineSrcBlendFA OutlineStencilComp OutlineStencilFail OutlineStencilPass OutlineStencilReadMask OutlineStencilRef OutlineStencilWriteMask " +
            "OutlineStencilZFail OutlineTexHSVG OutlineTex_ScrollRotate OutlineVectorScale OutlineVectorTex OutlineVectorUVMode OutlineVertexR2Width " +
            "OutlineZClip OutlineZTest OutlineZWrite Parallax ParallaxMap ParallaxOffset Ramp ReflectionApplyTransparency ReflectionBlendMode " +
            "ReflectionCubeEnableLighting ReflectionNormalStrength RimApplyTransparency RimShadeMask RimVRParallaxStrength Shadow2ndColorTex " +
            "Shadow3rdColorTex ShadowBlurMask ShadowBlurMaskLOD ShadowBorderMaskLOD ShadowColorTex ShadowColorType ShadowFlatBlur ShadowFlatBorder " +
            "ShadowMaskType ShadowStrengthMask ShadowStrengthMaskLOD ShiftBackfaceUV SrcBlend SrcBlendAlpha SrcBlendAlphaFA SrcBlendFA StencilComp " +
            "StencilFail StencilPass StencilReadMask StencilRef StencilWriteMask StencilZFail SubpassCutoff TessEdge TessFactorMax TessShrink " +
            "TessStrength TransparentMode UDIMDiscardCompile UDIMDiscardMode UDIMDiscardRow0_0 UDIMDiscardRow0_1 UDIMDiscardRow0_2 UDIMDiscardRow0_3 " +
            "UDIMDiscardRow1_0 UDIMDiscardRow1_1 UDIMDiscardRow1_2 UDIMDiscardRow1_3 UDIMDiscardRow2_0 UDIMDiscardRow2_1 UDIMDiscardRow2_2 " +
            "UDIMDiscardRow2_3 UDIMDiscardRow3_0 UDIMDiscardRow3_1 UDIMDiscardRow3_2 UDIMDiscardRow3_3 UDIMDiscardUV ZClip ZTest ZWrite " +
            "lilDirectionalLightStrength lilShadowCasterBias"
            ).Split(' '), StringComparer.Ordinal);
    }
}
