// PhysBone 미리보기의 손 조작·표시·시험 (PhysBoneSim.cs에서 2026-11-12에 나눔: 시뮬레이션 핵심과 따로 읽게).
//   잡기·고정: 관절을 마우스로 끌고 오른쪽 클릭으로 고정 (VR 손은 PhysBoneVrGrab.cs)
//   표시: 체인·콜라이더 기즈모 (ViewAids가 GL.LINES를 연 상태에서 부른다)
//   움직임 시험: 아바타 전체를 잠깐 흔들어 반응을 본다
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed partial class PhysBoneSim
    {
        // ---------------- 잡기 · 고정 ----------------
        // 미리보기에서 관절을 마우스로 끌어 본다 (VRChat에서 손으로 잡는 것과 비슷하게). 놓으면 물리대로 돌아간다.
        // 잡은 채 오른쪽 클릭하면 그 자리에 고정(VRChat의 Posing)한다. 고정은 체인 루트 기준 위치로 기억해 아바타가 움직이면 같이 움직인다.
        // 고정한 관절을 오른쪽 클릭하면 풀린다.

        sealed class Hold
        {
            public Chain Ch; public Particle P;
            public readonly List<Particle> Path = new List<Particle>(); // 루트 → 잡은 입자
            public Vector3 Local;                                       // 고정: 체인 루트 기준 위치
            public Hold(Chain ch, Particle p) { Ch = ch; P = p; for (var q = p; ; q = ch.P[q.Parent]) { Path.Insert(0, q); if (q.Fixed) break; } }
        }

        Hold _grab; Vector3 _grabTarget; float _grabDepth;
        readonly List<Hold> _pins = new List<Hold>();
        public bool IsGrabbing => _grab != null;

        /// <summary>화면 좌표(픽셀, 왼쪽 아래 원점) 근처에서 잡을 수 있는 입자. 없으면 null.</summary>
        Particle Pick(Camera cam, Vector2 mouse, float radiusPx, out float depth) => Pick(cam, mouse, radiusPx, out depth, out _);

        Particle Pick(Camera cam, Vector2 mouse, float radiusPx, out float depth, out Chain chain)
        {
            Particle best = null; float bestD = radiusPx * radiusPx; depth = 0; chain = null;
            if (!Enabled) return null;
            foreach (var ch in _chains)
            {
                if (!ch.Grabbable || !ch.Active) continue;
                for (int i = 1; i < ch.P.Count; i++)
                {
                    if (ch.P[i].Fixed) continue;
                    var sp = cam.WorldToScreenPoint(Position(ch.P[i]));
                    if (sp.z <= 0) continue;
                    float d = (new Vector2(sp.x, sp.y) - mouse).sqrMagnitude;
                    if (d <= bestD) { bestD = d; best = ch.P[i]; depth = sp.z; chain = ch; }
                }
            }
            return best;
        }

        public bool CanGrab(Camera cam, Vector2 mouse, float radiusPx) => Pick(cam, mouse, radiusPx, out _) != null;

        public bool Grab(Camera cam, Vector2 mouse, float radiusPx)
        {
            var p = Pick(cam, mouse, radiusPx, out _grabDepth, out var ch);
            if (p == null) return false;
            _pins.RemoveAll(h => h.P == p); // 고정한 관절을 다시 잡으면 고정이 풀린다
            _grab = new Hold(ch, p);
            _grabTarget = Position(p);
            return true;
        }

        public void DragTo(Camera cam, Vector2 mouse)
        {
            if (_grab != null) _grabTarget = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, _grabDepth));
        }

        // 놓은 직후 잠깐은 오른쪽 클릭으로 고정할 수 있다 (왼쪽을 먼저 떼고 오른쪽을 누르는 경우)
        const float PinGrace = 0.5f;
        Hold _released; Vector3 _releasedTarget; float _releasedAt = -10;
        public bool RecentlyReleased => _released != null && Time.unscaledTime - _releasedAt < PinGrace;

        public void Release()
        {
            if (_grab != null) { _released = _grab; _releasedTarget = _grabTarget; _releasedAt = Time.unscaledTime; }
            _grab = null;
        }

        /// <summary>잡고 있는 관절을 지금 자리에 고정한다 (allowPosing이 꺼진 PhysBone은 안 됨). 결과 안내 문구를 돌려준다.</summary>
        public string PinGrabbed()
        {
            if (_grab == null && RecentlyReleased) { _grab = _released; _grabTarget = _releasedTarget; }
            _released = null;
            if (_grab == null) return null;
            var name = _grab.Ch.Root.name;
            if (!_grab.Ch.Posable) return $"{name}: 원본 PhysBone에서 고정(Allow Posing)이 꺼져 있어 고정할 수 없어요";
            _grab.Local = _grab.Ch.Root.InverseTransformPoint(_grabTarget);
            _pins.Add(_grab);
            _grab = null;
            return $"{name} 고정 · 고정한 관절을 오른쪽 클릭하면 풀려요";
        }

        public bool IsPinnedAt(Camera cam, Vector2 mouse, float radiusPx) => PinAt(cam, mouse, radiusPx) != null;

        Hold PinAt(Camera cam, Vector2 mouse, float radiusPx)
        {
            Hold best = null; float bestD = radiusPx * radiusPx;
            foreach (var h in _pins)
            {
                if (!h.Ch.Active) continue;
                var sp = cam.WorldToScreenPoint(Position(h.P));
                if (sp.z <= 0) continue;
                float d = (new Vector2(sp.x, sp.y) - mouse).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = h; }
            }
            return best;
        }

        public string UnpinAt(Camera cam, Vector2 mouse, float radiusPx) { var h = PinAt(cam, mouse, radiusPx); return h != null && _pins.Remove(h) ? h.Ch.Root.name + " 고정 풀림" : null; }
        public void UnpinAll() => _pins.Clear();

        /// 스텝마다: 고정한 관절, 잡은 관절 순으로 체인을 굽힌다
        void Holds(Chain ch)
        {
            var soft = SoftOf(ch);
            foreach (var h in _pins) if (h.Ch == ch) Pull(h, ch.Root.TransformPoint(h.Local), 1, soft);
            if (_grab != null && _grab.Ch == ch) Pull(_grab, _grabTarget, Mathf.Lerp(0.2f, 1f, ch.GrabMovement), soft);
            for (int h = 0; h < 2; h++) if (_vrGrab[h] != null && _vrGrab[h].Ch == ch) Pull(_vrGrab[h], _vrTarget[h], Mathf.Lerp(0.2f, 1f, ch.GrabMovement), soft);   // VR 손 (PhysBoneVrGrab.cs)
        }

        /// <summary>
        /// 관절을 목표 쪽으로 끌고 루트까지의 뼈를 따라 굽힌다 (FABRIK: 끝에서 루트로, 루트에서 끝으로 뼈 길이 맞추기).
        /// follow: 한 스텝에 목표까지 가는 비율 (잡기는 Grab Movement, 고정은 1). 경로 밖의 가지는 다음 스텝에 부모를 따라간다.
        /// 말랑 모드면 잡은 관절 바로 위 뼈가 프리셋 범위 안에서 줄고 늘어난다 (몸 쪽으로 밀면 눌린다).
        /// 그 밖에는 닿지 않는 곳을 잡으면 경로의 뼈가 Max Stretch까지 늘고, 부모 쪽으로 밀면 잡은 관절 바로 위 뼈가 Max Squish까지 준다.
        /// </summary>
        static void Pull(Hold h, Vector3 target, float follow, SoftPreset soft)
        {
            var path = h.Path; int n = path.Count;
            if (n < 2) return;
            var len = new float[n]; float total = 0;
            for (int i = 1; i < n; i++) { len[i] = (path[i].Target - path[i - 1].Target).magnitude; total += len[i]; }
            var goal = Vector3.Lerp(h.P.Pos, target, follow);
            float last = len[n - 1], want = (goal - path[n - 2].Pos).magnitude;
            if (soft != null) len[n - 1] = Mathf.Clamp(want, last * (1 - soft.Squish), last * (1 + soft.Stretch));
            else
            {
                float over = total > 1e-6f ? (goal - path[0].Pos).magnitude / total : 1;
                if (over > 1) for (int i = 1; i < n; i++) len[i] *= Mathf.Min(over, 1 + path[i].MaxStretch);
                else if (want < last) len[n - 1] = Mathf.Max(want, last * (1 - h.P.MaxSquish));
            }
            var root = path[0].Pos;
            for (int it = 0; it < 4; it++)
            {
                path[n - 1].Pos = goal;
                for (int i = n - 2; i > 0; i--) path[i].Pos = path[i + 1].Pos + (path[i].Pos - path[i + 1].Pos).normalized * len[i + 1];
                for (int i = 1; i < n; i++) path[i].Pos = path[i - 1].Pos + (path[i].Pos - path[i - 1].Pos).normalized * len[i];
                path[0].Pos = root;
            }
            for (int i = 1; i < n; i++)
            {
                var p = path[i]; var par = path[i - 1];
                p.Prev = Vector3.Lerp(p.Prev, p.Pos, 0.5f); // 끌던 속도를 반만 남겨 놓았을 때 살짝 튕기게
                var restVec = par.Rot * (p.Target - par.Target);
                if (restVec.sqrMagnitude > 1e-12f) { p.Rot = Quaternion.FromToRotation(restVec, p.Pos - par.Pos) * par.Rot; p.LenRatio = (p.Pos - par.Pos).magnitude / restVec.magnitude; }
            }
        }

        // ---------------- 표시 (기즈모) ----------------
        // ViewAids가 GL.LINES를 연 상태에서 부른다: 체인(뼈 사이 선, 관절, 반지름 원), 콜라이더(구·캡슐·평면)

        /// 지금 보이는 위치: 뼈는 Transform, 끝점은 시뮬레이션 위치 (꺼져 있으면 쉬는 자리)
        Vector3 Position(Particle p) => p.T != null ? p.T.position : Enabled ? p.Show : p.Owner.TransformPoint(p.EndLocal);

        public void DrawGizmos(Camera cam, Vector2 hoverMouse, float hoverPx)
        {
            var right = cam.transform.right; var up = cam.transform.up;
            var hover = _grab?.P ?? Pick(cam, hoverMouse, hoverPx, out _);
            var pinned = new HashSet<Particle>(); foreach (var h in _pins) pinned.Add(h.P);
            var pinCol = new Color(1f, 0.3f, 0.45f, 1f);
            var chain = new Color(0.45f, 1f, 0.55f, 0.9f);
            var hot = new Color(1f, 0.85f, 0.2f, 1f);
            foreach (var ch in _chains)
            {
                if (!ch.Active) continue;
                float scale = Mathf.Abs(ch.Root.lossyScale.x);
                for (int i = 0; i < ch.P.Count; i++)
                {
                    var p = ch.P[i]; var pos = Position(p);
                    if (p.Parent >= 0) { GL.Color(chain); GL.Vertex(Position(ch.P[p.Parent])); GL.Vertex(pos); }
                    float r = p.Radius * scale;
                    float mark = Mathf.Max(0.004f, Vector3.Distance(cam.transform.position, pos) * 0.008f);
                    var c = p == hover ? hot : pinned.Contains(p) ? pinCol : chain;
                    ViewAids.Circle(pos, right, up, r > 0 ? r : mark, i == 0 ? new Color(c.r, c.g, c.b, 0.45f) : c, r > 0 ? 20 : 8);
                }
            }
            foreach (var c in _cols)
            {
                Prepare(c);
                if (c.T == null) continue;
                if (c.Auto && !LegGuard) continue;
                var col = !c.Active ? new Color(0.6f, 0.6f, 0.6f, 0.35f) : c.Auto ? new Color(0.3f, 0.85f, 1f, 0.8f) : c.Inside ? new Color(0.75f, 0.5f, 1f, 0.9f) : new Color(1f, 0.55f, 0.2f, 0.9f);
                switch (c.Shape)
                {
                    case 2: // 평면: 사각형 + 법선
                        {
                            var t = Vector3.Cross(c.N, Mathf.Abs(c.N.y) < 0.9f ? Vector3.up : Vector3.right).normalized * 0.15f;
                            var b = Vector3.Cross(c.N, t);
                            GL.Color(col);
                            var q = new[] { c.C + t + b, c.C - t + b, c.C - t - b, c.C + t - b };
                            for (int k = 0; k < 4; k++) { GL.Vertex(q[k]); GL.Vertex(q[(k + 1) % 4]); }
                            GL.Vertex(c.C); GL.Vertex(c.C + c.N * 0.1f);
                            break;
                        }
                    case 1: // 캡슐: 양 끝 원 + 옆선 (화면 쪽에서 본 윤곽)
                        {
                            ViewAids.Circle(c.A, right, up, c.R, col, 20); ViewAids.Circle(c.B, right, up, c.R, col, 20);
                            var side = Vector3.Cross(c.B - c.A, cam.transform.forward);
                            if (side.sqrMagnitude > 1e-10f)
                            {
                                side = side.normalized * c.R;
                                GL.Color(col);
                                GL.Vertex(c.A + side); GL.Vertex(c.B + side); GL.Vertex(c.A - side); GL.Vertex(c.B - side);
                            }
                            break;
                        }
                    default: // 구
                        ViewAids.Circle(c.C, right, up, c.R, col, 24);
                        break;
                }
            }
        }

        // ---------------- 움직임 시험 ----------------
        // 아바타 전체(장면 루트)를 잠깐 흔들어 PhysBone 반응을 본다. 끝나면 제자리로.

        string _motion; float _mt; Vector3 _basePos, _baseScale; Quaternion _baseRot;

        public void StartMotion(string kind)
        {
            StopMotion();
            if (_sceneRoot == null || string.IsNullOrEmpty(kind) || kind == "none") return;
            _motion = kind; _mt = 0; _basePos = _sceneRoot.localPosition; _baseRot = _sceneRoot.localRotation; _baseScale = _sceneRoot.localScale;
        }

        void StopMotion()
        {
            if (_motion != null && _sceneRoot != null) { _sceneRoot.localPosition = _basePos; _sceneRoot.localRotation = _baseRot; _sceneRoot.localScale = _baseScale; }
            _motion = null;
        }

        /// <summary>쉬는 자리(중력 처짐 포함 전)에서 보이는 관절이 벗어난 거리 평균·최대 (아바타 크기 1 기준 cm, 시험용).</summary>
        public (float mean, float max) Swing()
        {
            float sum = 0, max = 0; int n = 0;
            foreach (var ch in _chains)
            {
                if (!ch.Active) continue;
                float scale = Mathf.Max(1e-6f, Mathf.Abs(ch.Root.lossyScale.x));
                foreach (var p in ch.P)
                {
                    if (p.Fixed || p.Parent < 0) continue;
                    float d = (Position(p) - p.Target).magnitude / scale * 100;
                    sum += d; n++; max = Mathf.Max(max, d);
                }
            }
            return (n > 0 ? sum / n : 0, max);
        }

        void UpdateMotion(float dt)
        {
            if (_motion == null || _sceneRoot == null) return;
            _mt += dt;
            float dur = _motion == "jump" ? 1.6f : _motion == "vrwalk" ? 3.5f : 3f;
            if (_mt >= dur) { StopMotion(); return; }
            float env = Mathf.Sin(Mathf.PI * _mt / dur); // 부드럽게 시작·끝
            var off = Vector3.zero; var rot = Quaternion.identity;
            switch (_motion)
            {
                // VR처럼 (시험용): 4 m/s로 0.8초 걷다 바로 멈춤 · 30° 스냅 회전 0 → 30 → 60 → 30 → 0 · 방 크기 배율 1 → 1.5 → 1
                case "vrwalk": off.z = 4f * Mathf.Min(_mt, 0.8f); break;
                case "snap": rot = Quaternion.Euler(0, 30f * (_mt < 0.6f ? 1 : _mt < 1.2f ? 2 : _mt < 1.8f ? 1 : 0), 0); break;
                case "resize": _sceneRoot.localScale = _baseScale * (_mt < 1.2f ? 1.5f : 1f); break;
                case "sway": off.x = 0.15f * env * Mathf.Sin(2 * Mathf.PI * 1.2f * _mt); break;
                case "jump": { float ph = _mt % 0.8f; off.y = ph < 0.5f ? 0.3f * Mathf.Sin(Mathf.PI * ph / 0.5f) : 0; break; }
                case "turn": rot = Quaternion.Euler(0, 70f * env * Mathf.Sin(2 * Mathf.PI * 0.7f * _mt), 0); break;
                case "walk": off.z = 0.35f * env; off.y = 0.03f * Mathf.Abs(Mathf.Sin(2 * Mathf.PI * 1.8f * _mt)); break;
            }
            _sceneRoot.localPosition = _basePos + off;
            _sceneRoot.localRotation = _baseRot * rot;
        }
    }
}
