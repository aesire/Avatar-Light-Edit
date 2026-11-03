// 뷰포트 카메라: 왼쪽/오른쪽 드래그·WSAD = 회전, 가운데 드래그(또는 Shift+드래그)·화살표 = 이동, 휠 = 확대.
// "Unity처럼"(UnityNav): 오른쪽 드래그 = 둘러보기 + WASD·QE 날기, Alt+왼쪽 = 공전, Alt+오른쪽 = 확대, 가운데 = 이동 (Unity 씬 뷰와 같게).
// 원근/직교 전환, 축 방향으로 부드럽게 돌리기(방향 기즈모), 메시에 맞추기도 여기서 한다.
// 조작이 끝나면 상태를 Host로 보낸다 (세션에 저장되는 카메라).
using System;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Camera Camera { get; private set; }
        public event Action<JNode> Changed;
        /// <summary>마우스를 누른 위치가 뷰포트 위 UI(기즈모)면 true를 돌려 카메라 조작을 막는다.</summary>
        public Func<Vector2, bool> BlockInput;
        /// <summary>거울 보기에서는 화면이 좌우로 뒤집히므로 가로 회전도 뒤집는다.</summary>
        public bool Mirror;
        /// <summary>true면 WSAD·화살표 카메라 조작을 쉰다 (메시 편집: A = 모두 고르기).</summary>
        public Func<bool> BlockKeys;
        /// <summary>Unity 씬 뷰처럼 조작 (Host 설정 "조작 방식", setView nav = "unity").</summary>
        public bool UnityNav;
        /// <summary>Unity처럼: 오른쪽 버튼을 누르고 둘러보는 중 — WASD·QE가 날기라 Host 단축키로 보내지 않는다.</summary>
        public bool Flying { get; private set; }
        const float FlySpeed = 1.5f;   // m/s (Shift ×4, 날아다니는 중 휠로 ×0.05~20)
        float _flySpeed = 1, _flyTime;

        Vector3 _pivot = new Vector3(0, 1, 0);
        float _yaw = 180, _pitch = 5, _distance = 2.5f, _fov = 30;
        bool _ortho, _dragging, _blocked, _keyMoving;
        float _lastWheel;
        // 축 맞추기 애니메이션
        float _animT = -1, _fromYaw, _fromPitch, _toYaw, _toPitch;
        Bounds _bounds = new Bounds(new Vector3(0, 0.8f, 0), new Vector3(0.6f, 1.6f, 0.4f));
        Transform _head;

        public bool Orthographic => _ortho;
        public float Yaw => _yaw;
        public float Pitch => _pitch;

        void Awake()
        {
            Camera = gameObject.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.nearClipPlane = 0.01f; Camera.farClipPlane = 100;
            Camera.allowMSAA = true;
            Apply();
        }

        public void OnSceneLoaded(SceneBuilder scene)
        {
            _bounds = scene.WorldBounds();
            _head = scene.FindHumanoidHead();
        }

        void Update()
        {
            bool anyDown = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
            bool anyHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (anyDown) _blocked = BlockInput != null && BlockInput(Input.mousePosition);
            if (!anyHeld) _blocked = false;

            var md = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
            if (Mirror) md.x = -md.x;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool lmb = Input.GetMouseButton(0), rmb = Input.GetMouseButton(1), mmb = Input.GetMouseButton(2);
            bool pan, orbit, look = false, dolly = false;
            if (UnityNav)
            {
                // Unity 씬 뷰: 가운데 드래그 = 이동 · Alt+왼쪽 = 공전 · Alt+오른쪽 = 확대 · 오른쪽 = 제자리에서 둘러보기 (왼쪽 드래그는 카메라가 아님)
                pan = !_blocked && mmb;
                orbit = !_blocked && !pan && alt && lmb;
                dolly = !_blocked && !pan && alt && rmb;
                look = !_blocked && !pan && !alt && rmb;
            }
            else
            {
                pan = !_blocked && (mmb || (lmb && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))));
                orbit = !_blocked && !pan && (lmb || rmb);
            }
            Flying = look;
            if (orbit)
            {
                _animT = -1;
                _yaw += md.x * 4f; _pitch = Mathf.Clamp(_pitch - md.y * 4f, -89, 89);
                _dragging = true; Apply();
            }
            else if (look)
            {
                // 카메라 자리는 그대로 두고 방향만 → 피벗이 앞으로 따라간다
                _animT = -1;
                var eye = transform.position;
                _yaw += md.x * 3f; _pitch = Mathf.Clamp(_pitch - md.y * 3f, -89, 89);
                _pivot = eye + Quaternion.Euler(_pitch, _yaw, 0) * Vector3.forward * _distance;
                _dragging = true; Apply();
            }
            else if (dolly)
            {
                _distance = Mathf.Clamp(_distance * Mathf.Pow(0.97f, md.x + md.y), 0.05f, 50);
                _dragging = true; Apply();
            }
            else if (pan)
            {
                var t = transform;
                float k = _distance * Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad) * 0.05f;
                _pivot -= (t.right * md.x + t.up * md.y) * k;
                _dragging = true; Apply();
            }
            else if (_dragging) { _dragging = false; Notify(); }

            float wheel = Input.mouseScrollDelta.y;
            if (Flying && Mathf.Abs(wheel) > 0.01f) _flySpeed = Mathf.Clamp(_flySpeed * Mathf.Pow(1.25f, wheel), 0.05f, 20);   // Unity처럼: 날아다니는 중 휠 = 빠르기
            else if (Mathf.Abs(wheel) > 0.01f && !(BlockInput != null && BlockInput(Input.mousePosition)))
            {
                _distance = Mathf.Clamp(_distance * Mathf.Pow(0.88f, wheel), 0.05f, 50);
                _lastWheel = Time.unscaledTime; Apply();
            }
            else if (_lastWheel > 0 && Time.unscaledTime - _lastWheel > 0.3f) { _lastWheel = 0; Notify(); }

            KeyMove();

            if (_animT >= 0)
            {
                _animT = Mathf.Min(1, _animT + Time.unscaledDeltaTime / 0.18f);
                float e = 1 - (1 - _animT) * (1 - _animT);
                _yaw = _fromYaw + Mathf.DeltaAngle(_fromYaw, _toYaw) * e;
                _pitch = Mathf.Lerp(_fromPitch, _toPitch, e);
                Apply();
                if (_animT >= 1) { _animT = -1; _yaw = _toYaw; _pitch = _toPitch; Apply(); Notify(); }
            }
        }

        // 기본: WSAD = 회전, 화살표 = 이동 (↑↓ 앞뒤 · ←→ 옆). Ctrl·Alt를 누르면 Host 단축키(Ctrl+S 등)라 움직이지 않는다.
        // Unity처럼: 오른쪽 버튼을 누른 동안만 WASD = 앞뒤·옆, Q·E = 아래·위 (Shift 빠르게) — 누르지 않은 WASD는 카메라가 아니다. 화살표는 같다.
        void KeyMove()
        {
            bool mod = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool blocked = mod || (BlockKeys != null && BlockKeys());
            float Axis(KeyCode neg, KeyCode pos) => (Input.GetKey(pos) ? 1 : 0) - (Input.GetKey(neg) ? 1 : 0);
            var rot = blocked || UnityNav ? Vector2.zero : new Vector2(Axis(KeyCode.A, KeyCode.D), Axis(KeyCode.S, KeyCode.W));
            var move = blocked ? Vector2.zero : new Vector2(Axis(KeyCode.LeftArrow, KeyCode.RightArrow), Axis(KeyCode.DownArrow, KeyCode.UpArrow));
            // 날기는 메시 편집 중에도 (오른쪽 버튼을 누른 동안 키는 Host로 보내지 않는다 — RenderServer)
            var fly = Flying && !mod ? new Vector3(Axis(KeyCode.A, KeyCode.D), Axis(KeyCode.Q, KeyCode.E), Axis(KeyCode.S, KeyCode.W)) : Vector3.zero;
            if (fly == Vector3.zero) _flyTime = 0;
            if (rot == Vector2.zero && move == Vector2.zero && fly == Vector3.zero)
            {
                if (_keyMoving) { _keyMoving = false; Notify(); }
                return;
            }
            if (Mirror) { rot.x = -rot.x; move.x = -move.x; fly.x = -fly.x; }
            if (fly != Vector3.zero)
            {
                // Unity처럼 누르고 있으면 조금씩 빨라진다 (1초에 걸쳐 ×2까지)
                _flyTime += Time.unscaledDeltaTime;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                float sp = FlySpeed * _flySpeed * (shift ? 4 : 1) * (1 + Mathf.Clamp01(_flyTime));
                var tf = transform;
                _pivot += (tf.right * fly.x + Vector3.up * fly.y + tf.forward * fly.z) * sp * Time.unscaledDeltaTime;
            }
            float dt = Time.unscaledDeltaTime;
            _animT = -1;
            // D = 오른쪽으로 끌기(마우스와 같게), W = 위에서 내려다보는 쪽 (2026-10-23 사용자: W·S를 바꿈)
            _yaw += rot.x * 90f * dt; _pitch = Mathf.Clamp(_pitch + rot.y * 60f * dt, -89, 89);
            var t = transform;
            float k = _distance * Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad) * dt;
            // ←→ = 옆으로, ↑↓ = 보는 쪽 앞뒤로 (바닥과 나란히, 2026-10-23 사용자)
            var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.ProjectOnPlane(t.up, Vector3.up).normalized;   // 바로 위·아래를 볼 때
            _pivot += (t.right * move.x + fwd * move.y) * k * 2;
            _keyMoving = true; Apply();
        }

        void Apply()
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            transform.rotation = rot;
            transform.position = _pivot - rot * Vector3.forward * _distance;
            if (Camera == null) return;
            Camera.fieldOfView = _fov;
            Camera.orthographic = _ortho;
            // 직교에서도 같은 거리에서 원근과 비슷한 크기로 보이게
            Camera.orthographicSize = _distance * Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad);
        }

        void Notify() => Changed?.Invoke(GetState());

        public JNode GetState() => JNode.Obj().Set("pivot", JNode.FloatArray(new[] { _pivot.x, _pivot.y, _pivot.z }))
            .Set("yaw", _yaw).Set("pitch", _pitch).Set("distance", _distance).Set("fov", _fov).Set("ortho", _ortho);

        public void SetState(JNode s)
        {
            if (s.Has("pivot")) { var p = s["pivot"].ToFloatArray(); _pivot = new Vector3(p[0], p[1], p[2]); }
            if (s.Has("yaw")) _yaw = s["yaw"].AsFloat;
            if (s.Has("pitch")) _pitch = s["pitch"].AsFloat;
            if (s.Has("distance")) _distance = s["distance"].AsFloat;
            if (s.Has("fov")) _fov = s["fov"].AsFloat;
            _ortho = s.Has("ortho") && s["ortho"].AsBool;
            _animT = -1;
            Apply();
        }

        public void SetOrthographic(bool on) { if (_ortho == on) return; _ortho = on; Apply(); Notify(); }

        /// <summary>방향 기즈모: 누른 축 쪽에서 장면 가운데를 바라보게 부드럽게 돌린다.</summary>
        public void LookFromAxis(string axis)
        {
            float yaw = _yaw, pitch = 0;
            switch (axis)
            {
                case "+x": yaw = -90; break;
                case "-x": yaw = 90; break;
                case "+z": yaw = 180; break;
                case "-z": yaw = 0; break;
                case "+y": pitch = 90; yaw = 180; break;
                case "-y": pitch = -90; yaw = 180; break;
                default: return;
            }
            _fromYaw = _yaw; _fromPitch = _pitch; _toYaw = yaw; _toPitch = pitch; _animT = 0;
        }

        /// <summary>렌더러 하나가 화면에 꽉 차게.</summary>
        public void Frame(Bounds b, float frameAspect = 0)
        {
            _pivot = b.center;
            float r = Mathf.Max(0.05f, b.extents.magnitude);
            float halfFov = _fov * 0.5f * Mathf.Deg2Rad;
            float aspect = Mathf.Max(0.3f, frameAspect > 0 ? frameAspect : Camera.aspect);
            _distance = r / Mathf.Sin(Mathf.Min(halfFov, Mathf.Atan(Mathf.Tan(halfFov) * aspect))) * 1.05f;
            _animT = -1;
            Apply(); Notify();
        }

        /// <summary>구도 프리셋 (전신/상반신/얼굴) 과 방향 프리셋 (정면/측면/후면). 아바타 정면은 +Z. frameAspect: 찍을 사진의 가로÷세로 (없으면 창 비율, 2026-11-01 Tools/Shoot 세로 사진).</summary>
        public void Preset(string name, SceneBuilder scene, float frameAspect = 0)
        {
            _bounds = scene.WorldBounds();
            _head = scene.FindHumanoidHead();
            float top = _bounds.max.y, bottom = _bounds.min.y, height = Mathf.Max(0.1f, top - bottom);
            // 전체 bounds에는 머리 장식·소품·손 프록시가 포함될 수 있다. 얼굴 구도는
            // 그 전체 bounds의 x/z 가운데가 아니라 실제 Head 본을 중심으로 잡아야 한다.
            float headY = _head != null ? _head.position.y + Mathf.Clamp(height * 0.06f, 0.02f, 0.12f) : top - height * 0.08f;
            float halfFov = _fov * 0.5f * Mathf.Deg2Rad;
            float aspect = Mathf.Max(0.3f, frameAspect > 0 ? frameAspect : Camera.aspect);
            float Fit(float h, float w) => Mathf.Max(h * 0.5f / Mathf.Tan(halfFov), w * 0.5f / (Mathf.Tan(halfFov) * aspect)) * 1.12f;
            // 사진 비율을 주면 (Tools/Shoot, 2026-11-01): 가로 폭은 뼈로 잰다. 렌더러 범위는 쉬는 자세의 팔·기믹까지 들어 있어 세로 사진에서 아바타가 작아졌다
            float Wide(float minY)
            {
                if (frameAspect <= 0) return _bounds.size.x;
                float w = 0; int n = 0;
                foreach (var b in new[] { "LeftHand", "RightHand", "LeftLowerArm", "RightLowerArm", "LeftUpperArm", "RightUpperArm", "LeftFoot", "RightFoot", "LeftLowerLeg", "RightLowerLeg" })
                {
                    var t = scene.HumanBone(b);
                    if (t == null || t.position.y < minY) continue;
                    w = Mathf.Max(w, Mathf.Abs(t.position.x - _bounds.center.x)); n++;
                }
                return n == 0 ? _bounds.size.x : w * 2 + height * 0.25f;   // 뼈 바깥의 손끝·소매·머리카락 몫
            }
            _animT = -1;
            switch (name)
            {
                case "full": _pivot = new Vector3(_bounds.center.x, bottom + height * 0.5f, _bounds.center.z); _distance = Fit(height, Wide(bottom)); _pitch = 3; break;
                case "upper": { float h = height * 0.5f; _pivot = new Vector3(_bounds.center.x, top - h * 0.5f, _bounds.center.z); _distance = Fit(h, Wide(top - h)); _pitch = 2; break; }
                case "face":
                    {
                        float h = Mathf.Clamp(height * 0.2f, 0.18f, 0.6f);
                        // Head가 있으면 장면 bounds의 중심에서 벗어난 액세서리/손을 무시한다.
                        // 비휴머노이드 아바타만 기존 전체 bounds 중심으로 되돌린다.
                        var c = _head != null ? _head.position : _bounds.center;
                        _pivot = new Vector3(c.x, headY, c.z);
                        _distance = Fit(h, h); _pitch = 0; break;
                    }
                case "front": _yaw = 180; break;
                case "side": _yaw = 90; break;
                case "back": _yaw = 0; break;
            }
            Apply(); Notify();
        }
    }
}
