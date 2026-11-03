// 텍스처 합치기 (2026-11-02): 바탕 텍스처 위에 다른 이미지(눈·이펙트·문양 등)를 레이어로 올려 새 PNG 하나로 만든다.
//   계산은 여기서 (이미지 → 이미지, 화면·렌더러와 무관). 창은 AvHost/TextureMixWindow.cs, 결과는 색 굽기처럼 Addr.TexAsset으로 문서에 더한다.
//   자리 값은 바탕 크기에 대한 비율이라 해상도에 묶이지 않는다 (작은 그림으로 미리 보고 전체 크기로 만든다). y는 그림의 위에서 아래로.
//   색은 sRGB 값 그대로 섞는다 (이미지 편집 프로그램과 같은 결과). 합치는 식은 W3C Compositing.
//   레이어가 닿지 않는 픽셀은 바이트까지 그대로 둔다 — 투명한 곳에 숨은 색도 원본 그대로 (Opaque 머티리얼이 그 색을 쓴다).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public sealed class MixLayer
    {
        public string Key = "", File = "", Name = "";   // Key: 문서의 텍스처 키 (없으면 "" = 밖에서 고른 파일)
        public string Mode = "normal";
        public float Opacity = 1;
        public float X = 0.5f, Y = 0.5f, W = 1, H = 1;  // 가운데 자리·크기 (바탕에 대한 비율)
        public float Rotation;                          // 도, 화면에서 시계 방향
        public bool FlipX;
        public bool Mirror;                             // 좌우 거울 자리에도 한 장 더 (두 눈)
        public bool Visible = true;

        public MixLayer Clone() => (MixLayer)MemberwiseClone();

        public JNode ToJson() => JNode.Obj().Set("key", Key).Set("file", File).Set("name", Name).Set("mode", Mode).Set("opacity", Opacity)
            .Set("x", X).Set("y", Y).Set("w", W).Set("h", H).Set("rot", Rotation).Set("flipX", FlipX).Set("mirror", Mirror).Set("visible", Visible);

        public static MixLayer FromJson(JNode n) => new MixLayer
        {
            Key = n["key"].AsString, File = n["file"].AsString, Name = n["name"].AsString,
            Mode = TextureMix.Modes.Any(m => m.Key == n["mode"].AsString) ? n["mode"].AsString : "normal",
            Opacity = n.Has("opacity") ? n["opacity"].AsFloat : 1,
            X = n.Has("x") ? n["x"].AsFloat : 0.5f, Y = n.Has("y") ? n["y"].AsFloat : 0.5f, W = n.Has("w") ? n["w"].AsFloat : 1, H = n.Has("h") ? n["h"].AsFloat : 1,
            Rotation = n["rot"].AsFloat, FlipX = n["flipX"].AsBool, Mirror = n["mirror"].AsBool, Visible = !n.Has("visible") || n["visible"].AsBool,
        };
    }

    public static class TextureMix
    {
        public static readonly (string Key, string Label, string Tip)[] Modes =
        {
            ("normal", "보통", "그대로 위에 올려요. 눈·무늬처럼 투명 배경이 있는 그림에 써요."),
            ("add", "더하기 (빛나게)", "밝기를 더해요. 검은 배경의 빛·반짝임 이펙트에 써요."),
            ("screen", "스크린 (밝게)", "더하기보다 부드럽게 밝아져요."),
            ("multiply", "곱하기 (어둡게)", "어둡게 물들어요. 그림자·문신·선에 써요."),
            ("overlay", "오버레이", "밝은 곳은 더 밝게, 어두운 곳은 더 어둡게 해요. 바탕의 음영이 살아요."),
            ("softlight", "소프트 라이트", "오버레이보다 은은해요."),
        };

        // ---------------- 자리 ----------------
        /// <summary>새 레이어의 첫 자리: 바탕과 비율이 같고 절반 이상 크기면 같은 UV에 그린 그림으로 보고 꽉 채우고, 아니면 원래 픽셀 크기로 가운데.</summary>
        public static void PlaceDefault(MixLayer l, int baseW, int baseH, int srcW, int srcH)
        {
            bool same = Math.Abs((double)srcW * baseH - (double)srcH * baseW) <= 0.01 * srcW * baseH;
            if (same && srcW * 2 >= baseW) PlaceFill(l); else PlaceNative(l, baseW, baseH, srcW, srcH);
        }

        public static void PlaceFill(MixLayer l) { l.X = l.Y = 0.5f; l.W = l.H = 1; l.Rotation = 0; }

        /// <summary>원래 픽셀 크기 (바탕보다 크면 비율을 지켜 바탕 안에 맞춘다). 지금 자리는 그대로.</summary>
        public static void PlaceNative(MixLayer l, int baseW, int baseH, int srcW, int srcH)
        {
            double w = (double)srcW / baseW, h = (double)srcH / baseH, k = Math.Min(1, 1 / Math.Max(w, h));
            l.W = (float)(w * k); l.H = (float)(h * k);
        }

        // ---------------- 합치기 ----------------
        /// <summary>레이어를 아래부터 차례로 그린 새 이미지. 크기는 바탕과 같다. 그림이 없는(null) 레이어는 건너뛴다.</summary>
        public static Rgba Compose(Rgba bas, IEnumerable<(MixLayer Layer, Rgba? Image)> bottomUp)
        {
            var dst = new Rgba(bas.W, bas.H, (byte[])bas.Px.Clone());
            foreach (var (l, img) in bottomUp) if (img != null) Draw(dst, img, l);
            return dst;
        }

        public static void Draw(Rgba dst, Rgba src, MixLayer l)
        {
            if (!l.Visible || l.Opacity <= 0 || l.W <= 0 || l.H <= 0) return;
            // 많이 줄이면 한 점 집기로는 계단이 생긴다 → 먼저 그릴 크기로 줄인다 (면적 평균)
            int dw = Math.Max(1, (int)Math.Ceiling(l.W * dst.W)), dh = Math.Max(1, (int)Math.Ceiling(l.H * dst.H));
            int tw = dw < src.W * 3 / 4 ? dw : src.W, th = dh < src.H * 3 / 4 ? dh : src.H;
            var s = tw != src.W || th != src.H ? Sticker.Resample(src, 0, 0, src.W, src.H, tw, th) : src;
            DrawOne(dst, s, l, l.X, l.Rotation, l.FlipX);
            if (l.Mirror) DrawOne(dst, s, l, 1 - l.X, -l.Rotation, !l.FlipX);
        }

        static void DrawOne(Rgba dst, Rgba src, MixLayer l, float x, float rotation, bool flip)
        {
            double cx = x * dst.W, cy = l.Y * dst.H, dw = l.W * dst.W, dh = l.H * dst.H;
            double rad = rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
            double ex = (Math.Abs(dw * cos) + Math.Abs(dh * sin)) / 2 + 1, ey = (Math.Abs(dw * sin) + Math.Abs(dh * cos)) / 2 + 1;
            int x0 = Math.Max(0, (int)Math.Floor(cx - ex)), x1 = Math.Min(dst.W, (int)Math.Ceiling(cx + ex));
            int y0 = Math.Max(0, (int)Math.Floor(cy - ey)), y1 = Math.Min(dst.H, (int)Math.Ceiling(cy + ey));
            if (x0 >= x1 || y0 >= y1) return;
            int sw = src.W, sh = src.H, W = dst.W;
            byte[] d = dst.Px, p = src.Px;
            float opacity = Math.Min(1, l.Opacity);
            var blend = BlendFor(l.Mode);
            Parallel.For(y0, y1, y =>
            {
                for (int px = x0; px < x1; px++)
                {
                    double ox = px + 0.5 - cx, oy = y + 0.5 - cy;
                    double rx = ox * cos + oy * sin, ry = -ox * sin + oy * cos;
                    // 가장자리 1픽셀은 덮임으로 부드럽게 (돌렸을 때 계단이 없게)
                    double ex2 = dw / 2 - Math.Abs(rx) + 0.5, ey2 = dh / 2 - Math.Abs(ry) + 0.5;
                    if (ex2 <= 0 || ey2 <= 0) continue;
                    double cover = Math.Min(1, ex2) * Math.Min(1, ey2);
                    double u = rx / dw + 0.5, v = ry / dh + 0.5;
                    if (flip) u = 1 - u;
                    double sx = Math.Max(0, Math.Min(sw - 1, u * sw - 0.5)), sy = Math.Max(0, Math.Min(sh - 1, v * sh - 0.5));
                    int ix = (int)sx, iy = (int)sy, nx = Math.Min(sw - 1, ix + 1), ny = Math.Min(sh - 1, iy + 1);
                    float fx = (float)(sx - ix), fy = (float)(sy - iy);
                    // 두 줄 사이 보간: 곱한 알파로 (투명한 쪽의 숨은 색이 번지지 않게)
                    int i00 = (iy * sw + ix) * 4, i10 = (iy * sw + nx) * 4, i01 = (ny * sw + ix) * 4, i11 = (ny * sw + nx) * 4;
                    float w00 = (1 - fx) * (1 - fy) * p[i00 + 3], w10 = fx * (1 - fy) * p[i10 + 3], w01 = (1 - fx) * fy * p[i01 + 3], w11 = fx * fy * p[i11 + 3];
                    float wsum = w00 + w10 + w01 + w11;
                    if (wsum <= 0) continue;
                    float sa = wsum / 255f * opacity * (float)cover;
                    if (sa <= 0.5f / 255f) continue;
                    int o = (y * W + px) * 4;
                    float ab = d[o + 3] / 255f, ao = sa + ab * (1 - sa);
                    for (int c = 0; c < 3; c++)
                    {
                        float cs = (w00 * p[i00 + c] + w10 * p[i10 + c] + w01 * p[i01 + c] + w11 * p[i11 + c]) / wsum / 255f;
                        float cb = d[o + c] / 255f;
                        float co = cs * sa * (1 - ab) + ab * sa * blend(cb, cs) + (1 - sa) * ab * cb;
                        d[o + c] = (byte)(Math.Min(1f, co / ao) * 255f + 0.5f);
                    }
                    d[o + 3] = (byte)(ao * 255f + 0.5f);
                }
            });
        }

        static Func<float, float, float> BlendFor(string mode)
        {
            switch (mode)
            {
                case "add": return (b, s) => Math.Min(1f, b + s);
                case "screen": return (b, s) => b + s - b * s;
                case "multiply": return (b, s) => b * s;
                case "overlay": return (b, s) => b < 0.5f ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s);
                case "softlight": return (b, s) => (1 - 2 * s) * b * b + 2 * s * b;
                default: return (b, s) => s;
            }
        }

        /// <summary>미리 보기용 바탕 줄이기: 색과 알파를 따로 평균한다 (Sticker.Resample과 달리 투명한 곳의 숨은 색을 지키므로 Opaque 머티리얼도 제 색으로 보인다).</summary>
        public static Rgba ShrinkBase(Rgba src, int maxSide)
        {
            if (Math.Max(src.W, src.H) <= maxSide) return src;
            double k = (double)maxSide / Math.Max(src.W, src.H);
            int tw = Math.Max(1, (int)(src.W * k)), th = Math.Max(1, (int)(src.H * k));
            var dst = new Rgba(tw, th);
            Parallel.For(0, th, y =>
            {
                int y0 = y * src.H / th, y1 = Math.Max(y0 + 1, (y + 1) * src.H / th);
                for (int x = 0; x < tw; x++)
                {
                    int x0 = x * src.W / tw, x1 = Math.Max(x0 + 1, (x + 1) * src.W / tw);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int i = (yy * src.W + xx) * 4;
                            r += src.Px[i]; g += src.Px[i + 1]; b += src.Px[i + 2]; a += src.Px[i + 3]; n++;
                        }
                    int o = (y * tw + x) * 4;
                    dst.Px[o] = (byte)(r / n); dst.Px[o + 1] = (byte)(g / n); dst.Px[o + 2] = (byte)(b / n); dst.Px[o + 3] = (byte)(a / n);
                }
            });
            return dst;
        }

        // ---------------- 문서에 적는 값 ----------------
        /// <summary>TexAsset 값의 "mix": 다시 열어 레이어를 고칠 수 있게 재료를 적어 둔다 (렌더러·올리기는 쓰지 않는다).</summary>
        public static JNode RecipeToJson(string baseKey, string baseFile, IEnumerable<MixLayer> bottomUp)
        {
            var arr = JNode.Arr();
            foreach (var l in bottomUp) arr.Add(l.ToJson());
            return JNode.Obj().Set("base", baseKey).Set("baseFile", baseFile).Set("layers", arr);
        }

        public static List<MixLayer> LayersFromJson(JNode recipe) => recipe["layers"].Items.Select(MixLayer.FromJson).ToList();

        // ---------------- 찾기 ----------------
        static readonly Regex EyeWords = new Regex(@"(?<![a-z])eye(?!brow|lash|lid|line)|iris|pupil|hitomi|目|瞳|ハイライト|アイ(?!テム|コン)|눈(?!썹)|동공|홍채", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex FaceWords = new Regex(@"face|cheek|blush|makeup|make_up|lip|mouth|eyebrow|eyeline|頬|チーク|メイク|眉|얼굴|볼|화장|입술|눈썹", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex EffectWords = new Regex(@"effect|(?<![a-z])fx(?![a-z])|glow|emis|emit|spark|star|pattern|tattoo|mark|decal|crest|overlay|エフェクト|発光|模様|紋|タトゥー|이펙트|문양|문신|무늬|발광|반짝", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>파일 이름으로 종류 짐작 ("eye"·"face"·"effect"). 이름에서만 찾는다 — 경로 전체면 폴더 이름에 모두 걸린다.</summary>
        public static bool NameLooksLike(string kind, string fileName)
        {
            switch (kind)
            {
                case "eye": return EyeWords.IsMatch(fileName);
                case "face": return FaceWords.IsMatch(fileName);
                case "effect": return EffectWords.IsMatch(fileName);
                default: return true;
            }
        }
    }
}
