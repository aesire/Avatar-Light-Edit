// 이동·회전 핸들. 본·아이템·아바타 전체가 같은 핸들을 쓴다.
//   이동 화살표  X(빨강)·Y(초록)·Z(파랑). 화살표를 끌면 그 축 위로만 움직인다 (마우스 광선과 축의 가장 가까운 점)
//   회전 고리    X·Y·Z 고리와 화면 축 고리(바깥 흰색). 앞쪽 반만 잡힌다. 잡은 점을 고리의 접선 방향으로 끄는 만큼 돈다
// 축은 Frame(로컬이면 대상의 회전, 월드면 단위 회전) 기준. 그리기는 ViewAids가 연 GL.LINES 안에서 한다.
// 이 클래스는 계산과 그리기만 한다. 실제로 무엇을 옮기고 돌릴지는 쓰는 쪽(BonePoser·ObjectMover)이 정한다.
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class TransformGizmo
    {
        /// <summary>핸들 번호: 0~2 이동 X·Y·Z, 3~5 회전 X·Y·Z, 6 화면 축 회전.</summary>
        public const int MoveX = 0, RingX = 3, RingView = 6, Count = 7;
        public static bool IsMove(int h) => h >= 0 && h < 3;
        public static bool IsRing(int h) => h >= 3 && h < Count;

        public Vector3 Center;
        public Quaternion Frame = Quaternion.identity;
        public bool ShowMove = true, ShowRotate = true;

        /// <summary>핸들 모양 (setView handleStyle): line 얇은 선(예전) | bold 굵은 선(기본) | solid 입체 | soft 미니멀.</summary>
        public static string Style = "bold";
        /// <summary>핸들 크기 배율 (setView handleSize). 잡는 범위도 같이 커진다.</summary>
        public static float Size = 1f;

        int _active = -1;
        Vector3 _axis;            // 잡을 때의 축 (끄는 동안 고정)
        Vector2 _tangent;         // 고리: 잡은 점의 접선 (화면)
        float _px;                // 고리: 화면 반지름
        Vector3 _origin;          // 화살표: 잡을 때의 중심
        float _t0;                // 화살표: 잡을 때 축 위의 매개변수
        const int Segments = 48;

        static readonly Color[] Colors = { new Color(1f, 0.32f, 0.32f, 1f), new Color(0.4f, 0.95f, 0.35f, 1f), new Color(0.35f, 0.6f, 1f, 1f), new Color(0.9f, 0.9f, 0.9f, 1f) };
        static readonly Color HotColor = new Color(1f, 0.9f, 0.2f, 1f);
        public static readonly string[] Names = { "X", "Y", "Z", "화면" };

        public int Active => _active;
        public Vector3 ActiveAxis => _axis;

        /// <summary>핸들 h의 축 (월드). 화면 고리는 보는 방향.</summary>
        public Vector3 Axis(Camera cam, int h)
        {
            if (h == RingView) return cam.transform.forward;
            int k = h % 3;
            return Frame * (k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward);
        }

        float Dist(Camera cam) => Mathf.Max(0.05f, Vector3.Distance(cam.transform.position, Center));
        float RingRadius(Camera cam, int h) => Dist(cam) * (h == RingView ? 0.11f : 0.09f) * Size;
        float ArrowLength(Camera cam) => Dist(cam) * 0.15f * Size;

        Vector3 RingPoint(Camera cam, int h, int s)
        {
            var axis = Axis(cam, h);
            var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(axis, u);
            float a = s * Mathf.PI * 2 / Segments;
            return Center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * RingRadius(cam, h);
        }

        /// <summary>고리 점이 카메라 쪽(앞쪽 반)인가. 화면 고리는 늘 앞.</summary>
        bool Front(Camera cam, int h, Vector3 p) => h == RingView || Vector3.Dot(p - Center, cam.transform.position - Center) >= 0;

        /// <summary>화살표가 화면에서 거의 점으로 보이면(축이 보는 방향과 거의 같으면) 잡지도 그리지도 않는다.</summary>
        bool ArrowVisible(Camera cam, int h) => Mathf.Abs(Vector3.Dot(Axis(cam, h), (Center - cam.transform.position).normalized)) < 0.985f;

        static float SegDist(Camera cam, Vector3 p0, Vector3 p1, Vector2 mouse, out float t)
        {
            t = 0;
            Vector3 a = cam.WorldToScreenPoint(p0), b = cam.WorldToScreenPoint(p1);
            if (a.z <= 0 || b.z <= 0) return float.MaxValue;
            Vector2 a2 = a, b2 = b, ab = b2 - a2;
            t = Mathf.Clamp01(Vector2.Dot(mouse - a2, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (a2 + ab * t - mouse).magnitude;
        }

        /// <summary>마우스 아래 핸들 (없으면 -1). hit = 잡은 점 (월드).</summary>
        public int Pick(Camera cam, Vector2 mouse, float px, out Vector3 hit)
        {
            hit = Center;
            int best = -1; float bestD = px * 0.6f;
            if (ShowMove)
                for (int h = 0; h < 3; h++)
                {
                    if (!ArrowVisible(cam, h)) continue;
                    var axis = Axis(cam, h); float len = ArrowLength(cam);
                    var p0 = Center + axis * len * 0.3f; var p1 = Center + axis * len * 1.08f;
                    float d = SegDist(cam, p0, p1, mouse, out var t) * 0.8f;   // 화살표를 조금 더 잘 잡히게
                    if (d < bestD) { bestD = d; best = h; hit = Vector3.Lerp(p0, p1, t); }
                }
            if (ShowRotate)
                for (int h = RingX; h < Count; h++)
                {
                    var prev = RingPoint(cam, h, 0);
                    for (int s = 1; s <= Segments; s++)
                    {
                        var cur = RingPoint(cam, h, s);
                        if (Front(cam, h, (prev + cur) * 0.5f))
                        {
                            float d = SegDist(cam, prev, cur, mouse, out var t);
                            if (d < bestD) { bestD = d; best = h; hit = Vector3.Lerp(prev, cur, t); }
                        }
                        prev = cur;
                    }
                }
            return best;
        }

        /// <summary>핸들 h를 잡는다. 끄는 동안 축은 잡을 때의 것으로 고정한다.</summary>
        public void Begin(Camera cam, int h, Vector3 hit, Vector2 mouse)
        {
            _active = h;
            _axis = Axis(cam, h);
            if (IsMove(h))
            {
                _origin = Center;
                _t0 = AxisParam(cam, mouse);
                return;
            }
            var tangent = Vector3.Cross(_axis, hit - Center).normalized;
            Vector2 sc = cam.WorldToScreenPoint(Center), sh = cam.WorldToScreenPoint(hit), st = cam.WorldToScreenPoint(hit + tangent * RingRadius(cam, h) * 0.1f);
            _tangent = (st - sh).sqrMagnitude > 1e-6f ? (st - sh).normalized : Vector2.right;
            _px = Mathf.Max(20f, (sh - sc).magnitude);
        }

        public void End() => _active = -1;

        /// <summary>마우스 광선과 (잡을 때의 중심, 축) 직선의 가장 가까운 점의 축 위 매개변수 (m).</summary>
        float AxisParam(Camera cam, Vector2 mouse)
        {
            var ray = cam.ScreenPointToRay(mouse);
            Vector3 w0 = _origin - ray.origin;
            float a = 1, b = Vector3.Dot(_axis, ray.direction), c = 1, d = Vector3.Dot(_axis, w0), e = Vector3.Dot(ray.direction, w0);
            float den = a * c - b * b;
            if (den < 1e-4f) return 0;   // 축이 보는 방향과 거의 같다
            return (b * e - c * d) / den;
        }

        /// <summary>화살표: 잡은 뒤 지금까지 축 방향으로 움직인 거리 (월드 벡터, 잡을 때 기준 누적).</summary>
        public Vector3 MoveFromStart(Camera cam, Vector2 mouse) => _axis * (AxisParam(cam, mouse) - _t0);

        /// <summary>고리: 마우스가 last → mouse로 움직였을 때 돌 각도(도).</summary>
        public float Angle(Vector2 last, Vector2 mouse) => Vector2.Dot(mouse - last, _tangent) / _px * Mathf.Rad2Deg;

        /// <summary>GL.LINES 안에서 부른다. 잡은 핸들(또는 마우스 아래 핸들)은 노랑, 고리의 뒤쪽 반은 흐리게.
        /// line 말고는 화면 좌표의 면으로 그리므로 잠깐 GL.LINES를 닫고 끝나면 다시 연다.</summary>
        public void Draw(Camera cam, Vector2 mouse, float px)
        {
            int hot = _active >= 0 ? _active : Pick(cam, mouse, px, out _);
            if (Style != "line")
            {
                GL.End();
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, cam.pixelWidth, 0, cam.pixelHeight);
                DrawFlat(cam, hot, px / 12f);
                GL.PopMatrix();
                GL.Begin(GL.LINES);
                return;
            }
            if (ShowRotate)
                for (int h = RingX; h < Count; h++)
                {
                    if (_active >= 0 && h != _active && IsRing(_active)) continue;   // 돌리는 동안은 그 고리만
                    var col = h == hot ? HotColor : Colors[h - RingX];
                    var back = new Color(col.r, col.g, col.b, 0.22f);
                    var prev = RingPoint(cam, h, 0);
                    for (int s = 1; s <= Segments; s++)
                    {
                        var cur = RingPoint(cam, h, s);
                        GL.Color(Front(cam, h, (prev + cur) * 0.5f) ? col : back);
                        GL.Vertex(prev); GL.Vertex(cur);
                        prev = cur;
                    }
                }
            if (ShowMove)
            {
                float len = ArrowLength(cam);
                var right = cam.transform.right; var up = cam.transform.up;
                for (int h = 0; h < 3; h++)
                {
                    if (!ArrowVisible(cam, h)) continue;
                    if (_active >= 0 && h != _active && IsMove(_active)) continue;
                    var col = h == hot ? HotColor : Colors[h];
                    var axis = Axis(cam, h);
                    var tip = Center + axis * len;
                    GL.Color(col);
                    GL.Vertex(Center + axis * len * 0.2f); GL.Vertex(tip);
                    // 화살촉: 축에 수직인 두 방향으로 네 줄
                    var s1 = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, up)) < 0.9f ? up : right).normalized;
                    var s2 = Vector3.Cross(axis, s1);
                    var b = tip - axis * len * 0.18f; float w = len * 0.06f;
                    GL.Vertex(tip); GL.Vertex(b + s1 * w); GL.Vertex(tip); GL.Vertex(b - s1 * w);
                    GL.Vertex(tip); GL.Vertex(b + s2 * w); GL.Vertex(tip); GL.Vertex(b - s2 * w);
                    GL.Vertex(b + s1 * w); GL.Vertex(b + s2 * w); GL.Vertex(b + s2 * w); GL.Vertex(b - s1 * w);
                    GL.Vertex(b - s1 * w); GL.Vertex(b - s2 * w); GL.Vertex(b - s2 * w); GL.Vertex(b + s1 * w);
                }
            }
        }

        // ---------------- 굵은 모양 (bold · solid · soft) ----------------
        // 화면 좌표(왼쪽 아래 원점, 픽셀)의 삼각형으로 그린다. k = 화면 배율(DPI). 어두운 테두리를 먼저 깔아 밝은 옷·피부 위에서도 보이게.
        static readonly Color Halo = new Color(0.04f, 0.05f, 0.07f, 0.5f);
        readonly Vector2[] _ring = new Vector2[Segments + 1];
        readonly bool[] _ringFront = new bool[Segments + 1];
        readonly int[] _arrowOrder = new int[3];

        static Color Pastel(Color c) => Color.Lerp(c, Color.white, 0.25f);
        static Color WithA(Color c, float a) => new Color(c.r, c.g, c.b, a);
        static Vector2 Scr(Camera cam, Vector3 p) => cam.WorldToScreenPoint(p);

        void DrawFlat(Camera cam, int hot, float k)
        {
            bool solid = Style == "solid", soft = Style == "soft";
            float ringW = (solid ? 3.6f : soft ? 2f : 2.6f) * k, shaftW = (solid ? 3.2f : soft ? 2f : 2.8f) * k, edge = (solid ? 1.2f : 1f) * k;
            if (Vector3.Dot(Center - cam.transform.position, cam.transform.forward) <= 0) return;
            GL.Begin(GL.TRIANGLES);
            Vector2 c2 = Scr(cam, Center);
            if (ShowRotate)
            {
                for (int h = RingX; h < Count; h++)
                {
                    if (_active >= 0 && h != _active && IsRing(_active)) continue;
                    bool isHot = h == hot;
                    var col = isHot ? HotColor : soft ? Pastel(Colors[h - RingX]) : Colors[h - RingX];
                    float w = (isHot ? ringW + 1.6f * k : ringW) * (h == RingView ? 0.8f : 1f);
                    for (int s = 0; s <= Segments; s++) _ring[s] = Scr(cam, RingPoint(cam, h, s));
                    for (int s = 1; s <= Segments; s++) _ringFront[s] = Front(cam, h, (RingPoint(cam, h, s - 1) + RingPoint(cam, h, s)) * 0.5f);
                    // 미니멀: 잡거나 마우스 아래인 고리의 면을 옅게 채운다
                    if (soft && isHot && h != RingView)
                        for (int s = 1; s <= Segments; s++) Tri(c2, _ring[s - 1], _ring[s], WithA(col, 0.10f));
                    // 뒤쪽 반: 가늘고 옅게 (미니멀은 안 그림)
                    if (!soft)
                        for (int s = 1; s <= Segments; s++)
                            if (!_ringFront[s]) Seg(_ring[s - 1], _ring[s], 1.2f * k, WithA(col, isHot ? 0.45f : 0.25f));
                    // 앞쪽 반: 테두리 → 색 → (입체) 가운데 밝은 줄
                    var rim = solid ? WithA(col * 0.35f, 1f) : Halo;
                    for (int s = 1; s <= Segments; s++) if (_ringFront[s]) Seg(_ring[s - 1], _ring[s], w + edge * 2, rim);
                    for (int s = 1; s <= Segments; s++) if (_ringFront[s]) Seg(_ring[s - 1], _ring[s], w, col);
                    if (solid)
                        for (int s = 1; s <= Segments; s++) if (_ringFront[s]) Seg(_ring[s - 1], _ring[s], Mathf.Max(1f, w * 0.3f), WithA(Color.Lerp(col, Color.white, 0.55f), 0.9f));
                }
            }
            if (ShowMove)
            {
                // 먼 화살표부터 (가까운 것이 위에 오게)
                for (int i = 0; i < 3; i++) _arrowOrder[i] = i;
                var eye = cam.transform.position;
                System.Array.Sort(_arrowOrder, (a, b) => Vector3.Distance(eye, Center + Axis(cam, b)).CompareTo(Vector3.Distance(eye, Center + Axis(cam, a))));
                float len = ArrowLength(cam);
                foreach (int h in _arrowOrder)
                {
                    if (!ArrowVisible(cam, h)) continue;
                    if (_active >= 0 && h != _active && IsMove(_active)) continue;
                    bool isHot = h == hot;
                    var col = isHot ? HotColor : soft ? Pastel(Colors[h]) : Colors[h];
                    var axis = Axis(cam, h);
                    var tip = Center + axis * len;
                    var headBase = tip - axis * len * (solid ? 0.26f : 0.22f);
                    float w = isHot ? shaftW + 1.4f * k : shaftW;
                    Vector2 a2 = Scr(cam, Center + axis * len * 0.2f), t2 = Scr(cam, tip), hb2 = Scr(cam, headBase);
                    if (soft)
                    {
                        // 미니멀: 가는 막대 끝에 동그란 손잡이
                        float r = (isHot ? 6.5f : 5f) * k;
                        Seg(a2, t2, w + edge * 2, Halo); Disc(t2, r + edge, Halo);
                        Seg(a2, t2, w, col); Disc(t2, r, col);
                        continue;
                    }
                    Seg(a2, hb2, w + edge * 2, solid ? WithA(col * 0.35f, 1f) : Halo);
                    Seg(a2, hb2, w, col);
                    var right = cam.transform.right;
                    float coneR = len * (solid ? 0.085f : 0.075f);
                    if (solid) Cone(cam, tip, headBase, axis, coneR, col, edge);
                    else
                    {
                        // 굵은 선: 화면에 펼친 꽉 찬 삼각형 촉
                        var d = t2 - hb2;
                        float sr = Mathf.Max(5f * k, (Scr(cam, headBase + right * coneR) - hb2).magnitude);
                        var n = d.sqrMagnitude > 1e-4f ? new Vector2(-d.y, d.x).normalized : Vector2.up;
                        var dn = d.sqrMagnitude > 1e-4f ? d.normalized : Vector2.right;
                        float e = edge * 1.8f;
                        Tri(t2 + dn * e, hb2 + n * (sr + e) - dn * e, hb2 - n * (sr + e) - dn * e, Halo);
                        Tri(t2, hb2 + n * sr, hb2 - n * sr, col);
                    }
                }
            }
            // 가운데 점
            if (solid) { Disc(c2, 5.5f * k, new Color(0.15f, 0.16f, 0.18f, 0.9f)); Disc(c2, 4.5f * k, new Color(0.78f, 0.8f, 0.84f, 1)); Disc(c2 + new Vector2(-1.2f, 1.2f) * k, 2f * k, Color.white); }
            else if (soft) Disc(c2, 3f * k, new Color(1, 1, 1, 0.85f));
            else { Disc(c2, 4f * k, Halo); Disc(c2, 3f * k, new Color(0.95f, 0.95f, 0.95f, 1)); }
            GL.End();
        }

        /// <summary>입체 화살촉: 카메라 쪽을 향한 원뿔 면만 빛 방향에 따라 밝기를 바꿔 그린다 (볼록하므로 겹치지 않는다).</summary>
        static void Cone(Camera cam, Vector3 tip, Vector3 bottom, Vector3 axis, float r, Color col, float edge)
        {
            const int n = 16;
            var eye = cam.transform.position;
            var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(axis, u);
            var light = (cam.transform.up * 0.7f - cam.transform.right * 0.35f - cam.transform.forward * 0.6f).normalized;
            float slope = r / Mathf.Max(1e-5f, Vector3.Distance(tip, bottom));
            Vector2 t2 = Scr(cam, tip), b2 = Scr(cam, bottom);
            // 테두리: 화면의 원뿔 외곽을 조금 크게
            var outline = new Vector2[n];
            for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2 / n; outline[i] = Scr(cam, bottom + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r); }
            var dark = WithA(col * 0.35f, 1f);
            for (int i = 0; i < n; i++)
            {
                var p = outline[i]; var q = outline[(i + 1) % n];
                Tri(Grow(t2, b2, edge * 1.5f), Grow(p, b2, edge), Grow(q, b2, edge), dark);
                Tri(b2, Grow(p, b2, edge), Grow(q, b2, edge), dark);
            }
            // 밑면
            if (Vector3.Dot(-axis, eye - bottom) > 0)
                for (int i = 0; i < n; i++) Tri(b2, outline[i], outline[(i + 1) % n], col * 0.55f + new Color(0, 0, 0, 0.45f));
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n, am = (a0 + a1) * 0.5f;
                var radial = u * Mathf.Cos(am) + v * Mathf.Sin(am);
                var normal = (radial + axis * slope).normalized;
                var mid = bottom + radial * r * 0.5f + (tip - bottom) * 0.5f;
                if (Vector3.Dot(normal, eye - mid) <= 0) continue;
                float shade = 0.55f + 0.6f * Mathf.Max(0, Vector3.Dot(normal, light));
                var c = new Color(Mathf.Min(1, col.r * shade), Mathf.Min(1, col.g * shade), Mathf.Min(1, col.b * shade), 1);
                Tri(t2, outline[i], outline[(i + 1) % n], c);
            }
        }

        static Vector2 Grow(Vector2 p, Vector2 from, float by) { var d = p - from; return d.sqrMagnitude > 1e-6f ? p + d.normalized * by : p; }

        static void Tri(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); GL.Vertex3(c.x, c.y, 0);
        }

        static void Seg(Vector2 a, Vector2 b, float w, Color col)
        {
            var d = b - a; if (d.sqrMagnitude < 1e-6f) return;
            var n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            // 이음매가 벌어지지 않게 길이 방향으로도 조금 늘린다 (반투명은 겹친 곳이 진해지므로 그대로)
            var t = col.a >= 0.99f ? d.normalized * (w * 0.25f) : Vector2.zero;
            Tri(a - t + n, b + t + n, b + t - n, col);
            Tri(a - t + n, b + t - n, a - t - n, col);
        }

        static void Disc(Vector2 c, float r, Color col)
        {
            const int seg = 18;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                Tri(c, c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, col);
            }
        }
    }
}
