// lilToon 렌더링 모드 전환 규칙.
// lilToon 2.3.4 Editor/lilMaterialUtils.cs SetupMaterialWithRenderingMode (MIT, (c) lilxyzw) 의 동작을
// 표 형태로 옮긴 것. 에디터 코드는 플레이어에서 돌 수 없으므로 같은 결과(셰이더 + 값 집합)를 Host가 계산해
// 일반 편집과 똑같이 Changeset 한 단계로 기록한다.
using System.Collections.Generic;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public enum LilRenderingMode { Opaque, Cutout, Transparent, Refraction, RefractionBlur, Fur, FurCutout, FurTwoPass, Gem }
    public enum LilTransparentMode { Normal, OnePass, TwoPass }
    public enum LilFamily { Normal, Lite, Tessellation, Multi }

    public sealed class LilShaderState
    {
        public LilFamily Family; public LilRenderingMode Mode; public LilTransparentMode Transparent; public bool Outline;
        public override string ToString() => $"{Family}/{Mode}/{Transparent}/{(Outline ? "outline" : "-")}";
    }

    public static class LilToonRules
    {
        // UnityEngine.Rendering.BlendMode / BlendOp 값
        const int Zero = 0, One = 1, SrcAlpha = 5, OneMinusSrcAlpha = 10;
        const int OpAdd = 0, OpMax = 4;

        static readonly Dictionary<string, LilShaderState> ByName = new Dictionary<string, LilShaderState>();
        static readonly Dictionary<string, string> NameByKey = new Dictionary<string, string>();

        static string Key(LilFamily f, LilRenderingMode m, LilTransparentMode t, bool o) => $"{f}/{m}/{t}/{o}";

        static void Reg(string name, LilFamily f, LilRenderingMode m, bool outline, LilTransparentMode t = LilTransparentMode.Normal)
        {
            var s = new LilShaderState { Family = f, Mode = m, Outline = outline, Transparent = t };
            ByName[name] = s;
            var k = Key(f, m, t, outline);
            if (!NameByKey.ContainsKey(k)) NameByKey[k] = name;
        }

        static LilToonRules()
        {
            foreach (var o in new[] { false, true })
            {
                string O = o ? "Outline" : "";
                Reg(o ? "Hidden/lilToonOutline" : "lilToon", LilFamily.Normal, LilRenderingMode.Opaque, o);
                Reg("Hidden/lilToonCutout" + O, LilFamily.Normal, LilRenderingMode.Cutout, o);
                Reg("Hidden/lilToonTransparent" + O, LilFamily.Normal, LilRenderingMode.Transparent, o, LilTransparentMode.Normal);
                Reg("Hidden/lilToonOnePassTransparent" + O, LilFamily.Normal, LilRenderingMode.Transparent, o, LilTransparentMode.OnePass);
                Reg("Hidden/lilToonTwoPassTransparent" + O, LilFamily.Normal, LilRenderingMode.Transparent, o, LilTransparentMode.TwoPass);

                Reg("Hidden/lilToonLite" + O, LilFamily.Lite, LilRenderingMode.Opaque, o);
                Reg("Hidden/lilToonLiteCutout" + O, LilFamily.Lite, LilRenderingMode.Cutout, o);
                Reg("Hidden/lilToonLiteTransparent" + O, LilFamily.Lite, LilRenderingMode.Transparent, o, LilTransparentMode.Normal);
                Reg("Hidden/lilToonLiteOnePassTransparent" + O, LilFamily.Lite, LilRenderingMode.Transparent, o, LilTransparentMode.OnePass);
                Reg("Hidden/lilToonLiteTwoPassTransparent" + O, LilFamily.Lite, LilRenderingMode.Transparent, o, LilTransparentMode.TwoPass);

                Reg("Hidden/lilToonTessellation" + O, LilFamily.Tessellation, LilRenderingMode.Opaque, o);
                Reg("Hidden/lilToonTessellationCutout" + O, LilFamily.Tessellation, LilRenderingMode.Cutout, o);
                Reg("Hidden/lilToonTessellationTransparent" + O, LilFamily.Tessellation, LilRenderingMode.Transparent, o, LilTransparentMode.Normal);
                Reg("Hidden/lilToonTessellationOnePassTransparent" + O, LilFamily.Tessellation, LilRenderingMode.Transparent, o, LilTransparentMode.OnePass);
                Reg("Hidden/lilToonTessellationTwoPassTransparent" + O, LilFamily.Tessellation, LilRenderingMode.Transparent, o, LilTransparentMode.TwoPass);
            }
            Reg("Hidden/lilToonRefraction", LilFamily.Normal, LilRenderingMode.Refraction, false);
            Reg("Hidden/lilToonRefractionBlur", LilFamily.Normal, LilRenderingMode.RefractionBlur, false);
            Reg("Hidden/lilToonFur", LilFamily.Normal, LilRenderingMode.Fur, false);
            Reg("Hidden/lilToonFurCutout", LilFamily.Normal, LilRenderingMode.FurCutout, false);
            Reg("Hidden/lilToonFurTwoPass", LilFamily.Normal, LilRenderingMode.FurTwoPass, false);
            Reg("Hidden/lilToonGem", LilFamily.Normal, LilRenderingMode.Gem, false);
            // Multi: 모드는 셰이더가 아니라 _TransparentMode 값으로 정해진다 (아래 Apply 참고)
            Reg("_lil/lilToonMulti", LilFamily.Multi, LilRenderingMode.Opaque, false);
            Reg("Hidden/lilToonMultiOutline", LilFamily.Multi, LilRenderingMode.Opaque, true);
            Reg("Hidden/lilToonMultiRefraction", LilFamily.Multi, LilRenderingMode.Refraction, false);
            Reg("Hidden/lilToonMultiFur", LilFamily.Multi, LilRenderingMode.Fur, false);
            Reg("Hidden/lilToonMultiGem", LilFamily.Multi, LilRenderingMode.Gem, false);
        }

        public static bool IsLilToon(string shaderName) => ByName.ContainsKey(shaderName);

        /// <summary>셰이더 이름(+Multi는 _TransparentMode)으로 현재 상태를 판정한다.</summary>
        public static LilShaderState? Detect(string shaderName, float multiTransparentMode = 0)
        {
            if (!ByName.TryGetValue(shaderName, out var s)) return null;
            var r = new LilShaderState { Family = s.Family, Mode = s.Mode, Outline = s.Outline, Transparent = s.Transparent };
            if (s.Family == LilFamily.Multi) r.Mode = MultiMode((int)multiTransparentMode);
            return r;
        }

        static LilRenderingMode MultiMode(int tp)
        {
            switch (tp)
            {
                case 1: return LilRenderingMode.Cutout;
                case 2: return LilRenderingMode.Transparent;
                case 3: return LilRenderingMode.Refraction;
                case 4: return LilRenderingMode.Fur;
                case 5: return LilRenderingMode.FurCutout;
                case 6: return LilRenderingMode.Gem;
                default: return LilRenderingMode.Opaque;
            }
        }

        static int MultiTp(LilRenderingMode m)
        {
            switch (m)
            {
                case LilRenderingMode.Cutout: return 1;
                case LilRenderingMode.Transparent: return 2;
                case LilRenderingMode.Refraction: return 3;
                case LilRenderingMode.Fur: return 4;
                case LilRenderingMode.FurCutout: return 5;
                case LilRenderingMode.Gem: return 6;
                default: return 0;
            }
        }

        /// <summary>대상 상태에서 쓸 셰이더 이름. 해당 조합의 셰이더가 없으면 null (예: Lite + Fur).</summary>
        public static string? ShaderFor(LilShaderState s)
        {
            if (s.Family == LilFamily.Multi)
            {
                switch (s.Mode)
                {
                    case LilRenderingMode.Refraction: return "Hidden/lilToonMultiRefraction";
                    case LilRenderingMode.Fur: case LilRenderingMode.FurCutout: return "Hidden/lilToonMultiFur";
                    case LilRenderingMode.Gem: return "Hidden/lilToonMultiGem";
                    case LilRenderingMode.RefractionBlur: case LilRenderingMode.FurTwoPass: return null;
                    default: return s.Outline ? "Hidden/lilToonMultiOutline" : "_lil/lilToonMulti";
                }
            }
            bool modeHasVariants = s.Mode == LilRenderingMode.Opaque || s.Mode == LilRenderingMode.Cutout || s.Mode == LilRenderingMode.Transparent;
            // 굴절·퍼·젬은 Lite/Tessellation 변형이 없고 lilToon도 일반 셰이더로 바꾼다
            if (!modeHasVariants)
                return NameByKey.TryGetValue(Key(LilFamily.Normal, s.Mode, LilTransparentMode.Normal, false), out var n0) ? n0 : null;
            var t = s.Mode == LilRenderingMode.Transparent ? s.Transparent : LilTransparentMode.Normal;
            return NameByKey.TryGetValue(Key(s.Family, s.Mode, t, s.Outline), out var n) ? n : null;
        }

        /// <summary>
        /// 머티리얼의 렌더링 모드를 바꾼다 (실행 취소 한 단계). 외곽선은 불투명·컷아웃·반투명에만, 반투명 방식은 반투명에만 둔다.
        /// lilToon이 아니면 null, 맞는 셰이더가 없으면 false. (2026-11-02 MaterialPanel에서 옮김 — 창 없는 편집과 같이 쓴다)
        /// </summary>
        public static bool? Switch(Document doc, string mat, LilRenderingMode mode, LilTransparentMode transparent, bool outline)
        {
            var cur = Detect(doc.Get(Addr.MatShader(mat)).AsString, doc.Get(Addr.MatFloat(mat, "_TransparentMode")).AsFloat);
            if (cur == null) return null;
            var target = new LilShaderState { Family = cur.Family, Mode = mode, Transparent = transparent, Outline = outline };
            if (target.Mode > LilRenderingMode.Transparent) target.Outline = false;
            if (target.Mode != LilRenderingMode.Transparent) target.Transparent = LilTransparentMode.Normal;
            if (ShaderFor(target) == null) return false;
            var ops = Apply(mat, target, doc.Get(Addr.MatRenderQueue(mat)).AsInt);
            using (doc.Group("렌더링 모드: " + target))
                foreach (var kv in ops) doc.Set(kv.Key, kv.Value);
            return true;
        }

        /// <summary>
        /// 렌더링 모드 전환 시 바뀌는 값 전부 (셰이더 포함). 반환 목록을 Document.Group 안에서 Set 하면 된다.
        /// currentRenderQueue: Multi가 아닌 경우 원래 렌더 큐를 유지한다 (lilToon과 같음).
        /// </summary>
        public static List<KeyValuePair<Addr, JNode>> Apply(string mat, LilShaderState target, int currentRenderQueue)
        {
            var o = new List<KeyValuePair<Addr, JNode>>();
            void F(string p, float v) => o.Add(new KeyValuePair<Addr, JNode>(Addr.MatFloat(mat, p), JNode.From(v)));

            var shader = ShaderFor(target);
            if (shader == null) return o;
            o.Add(new KeyValuePair<Addr, JNode>(Addr.MatShader(mat), JNode.From(shader)));
            bool multi = target.Family == LilFamily.Multi, outl = target.Outline;
            if (multi) F("_TransparentMode", MultiTp(target.Mode));

            int rq = currentRenderQueue;
            switch (target.Mode)
            {
                case LilRenderingMode.Opaque:
                    if (multi) rq = -1;
                    F("_SrcBlend", One); F("_DstBlend", Zero); F("_AlphaToMask", 0);
                    if (outl) { F("_OutlineSrcBlend", One); F("_OutlineDstBlend", Zero); F("_OutlineAlphaToMask", 0); }
                    break;
                case LilRenderingMode.Cutout:
                    if (multi) rq = 2450;
                    F("_SrcBlend", One); F("_DstBlend", Zero); F("_AlphaToMask", 1);
                    if (outl) { F("_OutlineSrcBlend", One); F("_OutlineDstBlend", Zero); F("_OutlineAlphaToMask", 1); }
                    break;
                case LilRenderingMode.Transparent:
                    if (multi) rq = 2460; // BiRP
                    F("_SrcBlend", One); F("_DstBlend", OneMinusSrcAlpha); F("_AlphaToMask", 0);
                    if (outl) { F("_OutlineSrcBlend", SrcAlpha); F("_OutlineDstBlend", OneMinusSrcAlpha); F("_OutlineAlphaToMask", 0); }
                    break;
                case LilRenderingMode.Refraction:
                case LilRenderingMode.RefractionBlur:
                    if (multi) rq = -1;
                    F("_SrcBlend", One); F("_DstBlend", Zero); F("_AlphaToMask", 0);
                    break;
                case LilRenderingMode.Fur:
                    if (multi) rq = 3000;
                    F("_SrcBlend", SrcAlpha); F("_DstBlend", OneMinusSrcAlpha); F("_AlphaToMask", 0);
                    F("_FurSrcBlend", SrcAlpha); F("_FurDstBlend", OneMinusSrcAlpha); F("_FurZWrite", 0); F("_FurAlphaToMask", 0);
                    break;
                case LilRenderingMode.FurCutout:
                    if (multi) rq = 2450;
                    F("_SrcBlend", One); F("_DstBlend", Zero); F("_AlphaToMask", 1);
                    F("_FurSrcBlend", One); F("_FurDstBlend", Zero); F("_FurZWrite", 1); F("_FurAlphaToMask", 1);
                    break;
                case LilRenderingMode.FurTwoPass:
                    F("_SrcBlend", SrcAlpha); F("_DstBlend", OneMinusSrcAlpha); F("_AlphaToMask", 0);
                    F("_FurSrcBlend", SrcAlpha); F("_FurDstBlend", OneMinusSrcAlpha); F("_FurZWrite", 0); F("_FurAlphaToMask", 0);
                    break;
                case LilRenderingMode.Gem:
                    if (multi) rq = -1;
                    F("_SrcBlend", One); F("_DstBlend", One); F("_AlphaToMask", 0);
                    break;
            }
            o.Add(new KeyValuePair<Addr, JNode>(Addr.MatRenderQueue(mat), JNode.From(rq)));

            if (target.Mode == LilRenderingMode.Gem) { F("_Cull", 0); F("_ZWrite", 0); }
            else F("_ZWrite", 1);
            if (target.Transparent != LilTransparentMode.TwoPass) F("_ZTest", 4);
            F("_OffsetFactor", 0); F("_OffsetUnits", 0); F("_ColorMask", 15);
            F("_SrcBlendAlpha", One); F("_DstBlendAlpha", OneMinusSrcAlpha); F("_BlendOp", OpAdd); F("_BlendOpAlpha", OpAdd);
            F("_SrcBlendFA", One); F("_DstBlendFA", One); F("_SrcBlendAlphaFA", Zero); F("_DstBlendAlphaFA", One);
            F("_BlendOpFA", OpMax); F("_BlendOpAlphaFA", OpMax);
            if (outl)
            {
                F("_OutlineCull", 1); F("_OutlineZWrite", 1); F("_OutlineZTest", 2); F("_OutlineOffsetFactor", 0); F("_OutlineOffsetUnits", 0);
                F("_OutlineColorMask", 15); F("_OutlineSrcBlendAlpha", One); F("_OutlineDstBlendAlpha", OneMinusSrcAlpha);
                F("_OutlineBlendOp", OpAdd); F("_OutlineBlendOpAlpha", OpAdd); F("_OutlineSrcBlendFA", One); F("_OutlineDstBlendFA", One);
                F("_OutlineSrcBlendAlphaFA", Zero); F("_OutlineDstBlendAlphaFA", One); F("_OutlineBlendOpFA", OpMax); F("_OutlineBlendOpAlphaFA", OpMax);
            }
            if (target.Mode == LilRenderingMode.Fur || target.Mode == LilRenderingMode.FurCutout || target.Mode == LilRenderingMode.FurTwoPass)
            {
                F("_FurZTest", 4); F("_FurOffsetFactor", 0); F("_FurOffsetUnits", 0); F("_FurColorMask", 15);
                F("_FurSrcBlendAlpha", One); F("_FurDstBlendAlpha", OneMinusSrcAlpha); F("_FurBlendOp", OpAdd); F("_FurBlendOpAlpha", OpAdd);
                F("_FurSrcBlendFA", One); F("_FurDstBlendFA", One); F("_FurSrcBlendAlphaFA", Zero); F("_FurDstBlendAlphaFA", One);
                F("_FurBlendOpFA", OpMax); F("_FurBlendOpAlphaFA", OpMax);
            }
            return o;
        }
    }
}
