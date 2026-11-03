// Blink Fix: 눈 모양을 바꾸는 블렌드쉐이프(눈 크게·눈매 등)를 켜 두면 깜빡임 블렌드쉐이프가 원래 얼굴 기준이라
// 눈이 덜 감기거나 눈꺼풀이 눈을 뚫는다. 깜빡임(눈을 감는 블렌드쉐이프)에서 지금 켜 둔 눈 편집의 영향을 빼서,
// 깜빡임 100 + 눈 편집 = 원래 감은 눈이 되게 한다 (감은 동안에는 눈 편집이 사라진다).
//   blinkFix { targets: [깜빡임 블렌드쉐이프 이름], sources: [{ name, weight: 0~100 }], strength = 1, falloff = 0 }
//   edit     = Σ (weight 에서의 source 델타)   — 위치·노멀·탄젠트 모두
//   target' = target − strength × side × local × edit   (프레임이 여럿이면 프레임 가중치 비율만큼)
//   side     한쪽 눈만 감는 대상(…_L, ウィンク 등)은 그쪽 절반에서만 뺀다. 다른 쪽 눈의 편집이 윙크 때 사라지지 않게.
//            쪽은 대상이 움직이는 양이 메시 가운데(x)의 어느 쪽에 몰려 있는지로 가린다 (한쪽이 다른 쪽의 15% 미만이면 한쪽)
//   local    falloff > 0 이면 대상이 많이 움직이는 곳에서만 뺀다 (예전 방식, 전문가). 0이면 쪽 전체
//   eye      left·right = 아바타 왼쪽·오른쪽 눈의 세기 배수 (없으면 1). leftX = 왼쪽 눈이 있는 메시 x 방향 (+1/−1, 없으면 −1)
//            두 값이 다르면 메시 가운데를 기준으로 정점마다 그쪽 배수를 곱한다 (가운데 가까이는 부드럽게 섞는다)
//   auto     true면 호스트가 눈 편집 값이 바뀔 때마다 대상·편집을 다시 골라 이 연산을 바꾼다 (BlinkFixPlan.Auto). 적용에는 쓰지 않는다
// 정점 번호를 쓰지 않으므로 앞뒤의 정점 합치기·줄이기와 섞여도 된다.
using System;
using System.Collections.Generic;

namespace VrcProto.Protocol
{
    public static partial class MeshOps
    {
        public const string BlinkFix = "blinkFix";

        /// <summary>눈을 감는 블렌드쉐이프처럼 보이는 이름 (Blink Fix 대상 후보). 깜빡임·윙크와 눈 감은 표정(笑い·なごみ·はぅ·eye_smile …).</summary>
        public static bool IsBlinkName(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.StartsWith("vrc.v_")) return false;
            foreach (var k in BlinkWords) if (n.Contains(k)) return true;
            return n == "笑い" || n == "はぅ" || n == "なごみ" || n == "なごみω";
        }

        static readonly string[] BlinkWords =
        {
            "blink", "brink", "wink", "mabataki", "eye_close", "eyeclose", "close_eye", "eye_smile", "eyesmile", "eye_nagomi", "nagomi", "eye_joy",
            "まばたき", "ウィンク", "ｳｨﾝｸ", "目閉じ", "눈감", "눈 감", "윙크",
        };   // brink = 아바타 D 등의 eye_brink_1 (blink 오타)

        /// <summary>깜빡임 대상의 기본 선택: 눈 감은 표정(웃는 눈 등)은 후보로만 두고 깜빡임·윙크만 고른다.</summary>
        public static bool IsPlainBlink(string name)
        {
            var n = name.ToLowerInvariant();
            return IsBlinkName(name) && (n.Contains("blink") || n.Contains("brink") || n.Contains("mabataki") || n.Contains("まばたき") || n.Contains("wink") || n.Contains("ウィンク") || n.Contains("ｳｨﾝｸ")
                || n.Contains("eyeclosed") && !n.Contains("joy") || n.Contains("eye_close") || n.Contains("눈감") || n.Contains("윙크"));
        }

        /// <summary>이름이 눈 감기 같고, 실제로 위아래(앞뒤)로 움직이는지 (옆으로 주로 움직이면 눈 사이 거리 같은 얼굴 편집 — 예: Face_Custom의 eye_close).</summary>
        public static bool IsClosingShape(MeshData d, MeshData.Shape sh)
        {
            if (!IsBlinkName(sh.Name) || sh.Frames.Count == 0 || sh.Frames[sh.Frames.Count - 1].DPos is not float[] p) return false;
            double side = 0, all = 0;
            for (int i = 0; i + 2 < p.Length; i += 3) { side += Math.Abs(p[i]); all += Math.Abs(p[i]) + Math.Abs(p[i + 1]) + Math.Abs(p[i + 2]); }
            return all > 0 && side / all < 0.5;
        }

        public static JNode MakeBlinkFix(IEnumerable<string> targets, IEnumerable<(string Name, float Weight)> sources, float strength = 1f, float falloff = 0f)
        {
            var t = JNode.Arr(); foreach (var x in targets) t.Add(x);
            var s = JNode.Arr(); foreach (var (n, w) in sources) s.Add(JNode.Obj().Set("name", n).Set("weight", w));
            return JNode.Obj().Set("op", BlinkFix).Set("targets", t).Set("sources", s).Set("strength", strength).Set("falloff", falloff);
        }

        /// <summary>눈마다 세기 배수 (left·right: 아바타 기준, leftX: 왼쪽 눈의 메시 x 방향 +1/−1).</summary>
        public static JNode WithEyes(JNode op, float left, float right, int leftX)
        {
            if (Math.Abs(left - 1) < 1e-4f && Math.Abs(right - 1) < 1e-4f) return op;
            return op.Set("left", left).Set("right", right).Set("leftX", leftX < 0 ? -1 : 1);
        }

        /// <summary>
        /// 이름의 좌우 표시: −1 왼쪽, +1 오른쪽, 0 없음. Blink_L · eye.R · EyeLeft · BlinkR · L_eye · 左 · ウィンク右 · 왼눈 …
        /// (대소문자를 보는 것은 끝의 L/R 하나뿐: "BlinkL"은 왼쪽, "eyeball"은 아님)
        /// </summary>
        public static int SideOfName(string name)
        {
            var n = name.Trim().ToLowerInvariant();
            if (n.Contains("left") || n.Contains("左") || n.Contains("왼")) return -1;
            if (n.Contains("right") || n.Contains("右") || n.Contains("오른")) return 1;
            var parts = System.Text.RegularExpressions.Regex.Split(n, @"[\s_\.\-\(\)\[\]]+");
            foreach (var p in parts) { if (p == "l") return -1; if (p == "r") return 1; }
            var raw = name.Trim();
            if (raw.Length >= 2 && char.IsLower(raw[raw.Length - 2]))
            {
                if (raw[raw.Length - 1] == 'L') return -1;
                if (raw[raw.Length - 1] == 'R') return 1;
            }
            return 0;
        }

        /// <summary>눈 둘레를 바꾸는 블렌드쉐이프 이름 (자동 Blink Fix의 눈 편집 후보).</summary>
        public static bool IsEyeName(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.StartsWith("vrc.")) return false;
            // 눈썹(eyebrow·眉·눈썹)은 눈꺼풀이 아니다: 빼면 깜빡일 때 눈썹이 원래대로 돌아간다
            if (n.Contains("brow") || n.Contains("眉") || n.Contains("눈썹") || n.Contains("mayu")) return false;
            foreach (var k in EyeWords) if (n.Contains(k)) return true;
            return false;
        }

        /// <summary>구분용 이름 (---------Eye---------, === FT Viseme ===) 이면 묶음 이름, 아니면 null.</summary>
        public static string? SeparatorName(string name)
        {
            var t = name.Trim();
            if (t.Length < 4) return null;
            char c = t[0];
            if (c != '-' && c != '=' && c != '*' && c != '#' && c != '_' && c != '~') return null;
            if (!t.StartsWith(new string(c, 3))) return null;
            var inner = t.Trim(c, ' ', '\t').Trim();
            return inner.Length == 0 ? null : inner;
        }

        static readonly string[] EyeWords = { "eye", "目", "瞳", "まぶた", "瞼", "눈", "lid", "lash", "まつ毛", "まつげ" };

        /// <summary>블렌드쉐이프를 값 w(0~100)에서 계산한 델타 (프레임 사이는 직선, 마지막 프레임 밖은 늘림). sel = 0 위치, 1 노멀, 2 탄젠트.</summary>
        static float[]? ShapeAt(MeshData.Shape sh, float w, int sel, int len)
        {
            float[]? Get(MeshData.Frame f) => sel == 0 ? f.DPos : sel == 1 ? f.DNrm : f.DTan;
            var frames = sh.Frames;
            int hi = 0;
            while (hi < frames.Count - 1 && frames[hi].Weight < w) hi++;
            var b = frames[hi]; var bd = Get(b);
            if (bd == null) return null;
            float bw = b.Weight == 0 ? 100 : b.Weight;
            var r = new float[len];
            if (hi == 0 || w >= bw)
            {
                float k = w / bw;
                for (int i = 0; i < len && i < bd.Length; i++) r[i] = bd[i] * k;
                return r;
            }
            var a = frames[hi - 1]; var ad = Get(a);
            float t = (w - a.Weight) / Math.Max(1e-4f, b.Weight - a.Weight);
            for (int i = 0; i < len && i < bd.Length; i++) r[i] = (ad != null && i < ad.Length ? ad[i] : 0) * (1 - t) + bd[i] * t;
            return r;
        }

        static void DoBlinkFix(MeshData d, JNode op)
        {
            float strength = op.Has("strength") ? op["strength"].AsFloat : 1f;
            float falloff = op.Has("falloff") ? op["falloff"].AsFloat : 0f;
            int n = d.VertexCount, len = n * 3;
            var targets = new HashSet<string>();
            foreach (var x in op["targets"].Items) targets.Add(x.AsString);
            // 지금 켜 둔 눈 편집의 합: 위치·노멀·탄젠트
            var edit = new float[3][];
            bool any = false;
            foreach (var src in op["sources"].Items)
            {
                var name = src["name"].AsString; float w = src["weight"].AsFloat;
                if (targets.Contains(name) || Math.Abs(w) < 1e-4f) continue;
                var sh = d.Shapes.Find(x => x.Name == name);
                if (sh == null || sh.Frames.Count == 0) continue;
                for (int sel = 0; sel < 3; sel++)
                {
                    var at = ShapeAt(sh, w, sel, len);
                    if (at == null) continue;
                    var e = edit[sel] ??= new float[len];
                    for (int i = 0; i < len; i++) e[i] += at[i];
                    any |= sel == 0;
                }
            }
            if (!any) return;
            // 메시 가운데 (좌우를 가르는 x)
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int v = 0; v < n && v * 3 < d.Pos.Length; v++) { minX = Math.Min(minX, d.Pos[v * 3]); maxX = Math.Max(maxX, d.Pos[v * 3]); }
            float midX = n > 0 ? (minX + maxX) / 2 : 0;
            // 눈마다 세기: 정점이 왼쪽 눈 쪽이면 left, 오른쪽이면 right (가운데 폭 4%에서 섞는다)
            float eyeL = op.Has("left") ? op["left"].AsFloat : 1f, eyeR = op.Has("right") ? op["right"].AsFloat : 1f;
            int leftX = op.Has("leftX") && op["leftX"].AsFloat > 0 ? 1 : -1;
            float[]? eye = null;
            if (Math.Abs(eyeL - eyeR) > 1e-4f || Math.Abs(eyeL - 1) > 1e-4f)
            {
                eye = new float[n];
                float band = Math.Max(1e-6f, (maxX - minX) * 0.02f);
                for (int v = 0; v < n; v++)
                {
                    float t = Math.Min(1f, Math.Max(0f, 0.5f + (d.Pos[v * 3] - midX) * leftX / (2 * band)));
                    t = t * t * (3 - 2 * t);   // 1 = 왼쪽 눈 쪽
                    eye[v] = eyeL * t + eyeR * (1 - t);
                }
            }
            foreach (var sh in d.Shapes)
            {
                if (!targets.Contains(sh.Name) || sh.Frames.Count == 0) continue;
                var lastFrame = sh.Frames[sh.Frames.Count - 1];
                var last = lastFrame.DPos;
                if (last == null) continue;
                var mask = Mask(d, last, midX, falloff);
                if (mask == null) continue;
                foreach (var f in sh.Frames)
                {
                    float frameK = lastFrame.Weight == 0 ? 1f : f.Weight / lastFrame.Weight;
                    var outs = new[] { f.DPos, f.DNrm, f.DTan };
                    for (int sel = 0; sel < 3; sel++)
                    {
                        var e = edit[sel];
                        if (e == null) continue;
                        var o = outs[sel];
                        if (o == null)
                        {
                            if (sel == 0) continue;
                            o = new float[len];   // 노멀·탄젠트 델타가 없던 프레임은 새로 만든다
                            if (sel == 1) f.DNrm = o; else f.DTan = o;
                        }
                        for (int v = 0; v < n; v++)
                        {
                            float c = strength * frameK * mask[v] * (eye != null ? eye[v] : 1f);
                            if (c == 0) continue;
                            o[v * 3] -= c * e[v * 3]; o[v * 3 + 1] -= c * e[v * 3 + 1]; o[v * 3 + 2] -= c * e[v * 3 + 2];
                        }
                    }
                }
            }
        }

        /// <summary>대상 블렌드쉐이프 하나에서 뺄 정도 (정점마다 0~1). 한쪽 눈만 움직이면 그쪽 절반만, falloff > 0이면 많이 움직이는 곳만.</summary>
        static float[]? Mask(MeshData d, float[] last, float midX, float falloff)
        {
            int n = d.VertexCount;
            double left = 0, right = 0, sx = 0, sw = 0; float max = 0;
            for (int v = 0; v < n && v * 3 + 2 < last.Length; v++)
            {
                float l = Len(last, v);
                if (l <= 0) continue;
                max = Math.Max(max, l);
                float x = d.Pos[v * 3] - midX;
                if (x >= 0) right += l; else left += l;
                sx += x * l; sw += l;
            }
            if (max <= 0) return null;
            var m = new float[n];
            int side = 0; float band = 1;
            if (Math.Min(left, right) < 0.15 * Math.Max(left, right))
            {
                side = right > left ? 1 : -1;
                band = Math.Max(1e-6f, (float)Math.Abs(sx / sw) * 0.2f);   // 눈 가운데까지 거리의 20%에 걸쳐 부드럽게
            }
            for (int v = 0; v < n; v++)
            {
                float k = 1;
                if (side != 0)
                {
                    float t = Math.Min(1f, Math.Max(0f, 0.5f + (d.Pos[v * 3] - midX) * side / (2 * band)));
                    k = t * t * (3 - 2 * t);
                }
                if (falloff > 0 && k > 0)
                {
                    float t = Math.Min(1f, Len(last, v) / (falloff * max));
                    k *= t * t * (3 - 2 * t);
                }
                m[v] = k;
            }
            return m;
        }

        static float Len(float[] a, int v) => (float)Math.Sqrt(a[v * 3] * a[v * 3] + a[v * 3 + 1] * a[v * 3 + 1] + a[v * 3 + 2] * a[v * 3 + 2]);
    }
}
