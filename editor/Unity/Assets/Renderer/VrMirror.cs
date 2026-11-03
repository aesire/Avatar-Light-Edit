// VR 거울 (PLAN 3.12): VRChat 거울처럼 실제 반사(좌우 반전) — 눈마다 반사 카메라로 그려 거울 판에 화면 좌표로 붙인다.
//   - 월드 거울: VRChat 월드의 큰 거울처럼 아바타를 불러온 자리 정면 WorldDistance m에 아바타 쪽을 보고 선다 (월드 크기, 움직이지 않는다).
//   - 개인 거울: VRChat 개인 거울처럼 내 앞에 나온다. 방(리그)에 붙어 걸어 다니면 따라오고(ⓘ VRChat 기본 동작 확인 못 함),
//     그립으로 잡아 옮기고(손이 거울 판 가까이 — 닿으면 테두리가 밝아진다), 두 손으로 잡고 벌리면 크기가 바뀐다. "내 앞으로"로 다시 부른다.
//   - 거울에는 내 머리가 보인다 (1인칭 사본이 아니라 원본 레이어). 거울 판·바닥 격자는 눈 카메라 전용 레이어라 미리보기·촬영·다른 거울에는 안 보인다.
//   - 반사 텍스처 = 눈 텍스처 × 화질(0.5~1), MSAA 2×. 내가 거울 뒤에 있거나 눈에 안 보이면 그리지 않는다.
//   미리보기 전용 — Changeset에 남지 않는다.
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class VrMirror
    {
        public const float WorldDistance = 2.0f, PersonalDistance = 0.6f, PersonalWidth = 0.6f, PersonalHeight = 0.9f;
        const float GrabMargin = 0.1f, GrabDepth = 0.15f, FrameWidth = 0.02f;   // 실제 m
        static readonly Color Frame = new Color(0.1f, 0.1f, 0.12f), FrameNear = new Color(0.3f, 0.55f, 1f), FrameHeld = new Color(0.75f, 0.88f, 1f);

        sealed class Glass
        {
            public GameObject Go;
            public Renderer R;
            public Material Mat;
            public RenderTexture Tex;
        }

        Glass _world, _personal;
        Camera _cam;
        Vector3 _anchorPos;
        Quaternion _anchorRot = Quaternion.identity;
        float _anchorH = 2.4f;
        readonly Plane[] _planes = new Plane[6];
        readonly VrLink.Device[] _hands = new VrLink.Device[2];   // 매 프레임 배열을 만들지 않게 (GC 끊김)
        readonly Glass[] _all = new Glass[2];
        // 잡기: 잡은 손(0 왼손 · 1 오른손, -1 없음), 손 기준 거울 자리, 두 손 크기
        int _held = -1;
        bool _two;
        Vector3 _offPos;
        Quaternion _offRot;
        float _twoDist, _twoScale;
        Vector3 _scale0;

        public int Count => (_world != null && _world.Go.activeSelf ? 1 : 0) + (_personal != null && _personal.Go.activeSelf ? 1 : 0);
        public bool Held => _held >= 0;

        /// <summary>월드 거울 자리: 아바타를 불러온 자리(몸 모드로 루트가 움직이기 전)의 정면.</summary>
        public void Anchor(SceneBuilder scene)
        {
            var root = scene.Root;
            var fwd = root != null ? root.forward : Vector3.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var b = scene.WorldBounds();
            float floor = root != null ? root.position.y : 0;
            var basePos = root != null ? new Vector3(root.position.x, floor, root.position.z) : Vector3.zero;
            // 키 큰 아바타(크기를 키운 것 포함)도 다 비치게 — 이상하게 큰 범위(꺼진 핸들 메시 등)는 6 m에서 자른다
            _anchorH = Mathf.Clamp(b.size.y * 1.2f, 2.4f, 6f);
            _anchorPos = basePos + fwd * WorldDistance + Vector3.up * (_anchorH * 0.5f);
            _anchorRot = Quaternion.LookRotation(fwd);   // 판의 앞면(-forward)이 아바타 쪽
            if (_world != null) PlaceWorld();
        }

        /// <summary>VrView.Pre에서 매 프레임: 켜고 끄기, 개인 거울 부르기·잡기.</summary>
        public void Tick(bool world, bool personal, bool summon, Transform rig, VrInput input, VrLink.Device head, VrLink.Device left, VrLink.Device right)
        {
            if (world && _world == null) { _world = Make("VrMirrorWorld", null); PlaceWorld(); }
            if (_world != null) _world.Go.SetActive(world);

            if (personal && _personal == null) { _personal = Make("VrMirrorPersonal", rig); summon = true; }
            if (_personal == null) return;
            if (_personal.Go.activeSelf != personal) { _personal.Go.SetActive(personal); if (personal) summon = true; }
            if (!personal) { _held = -1; return; }
            if (summon) { Summon(head); _held = -1; }
            Grab(input, left, right);
        }

        /// <summary>개인 거울을 내 앞(실제 PersonalDistance m, 가슴 높이)에 나를 보게 세운다.</summary>
        void Summon(VrLink.Device head)
        {
            var t = _personal.Go.transform;
            var f = head.Rotation * Vector3.forward; f.y = 0;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            t.localPosition = head.Position + f * PersonalDistance + Vector3.down * 0.15f;
            t.localRotation = Quaternion.LookRotation(f);
            t.localScale = new Vector3(PersonalWidth, PersonalHeight, 1);
            SetBorder(_personal);
        }

        void Grab(VrInput input, VrLink.Device left, VrLink.Device right)
        {
            var t = _personal.Go.transform;
            var hands = _hands;
            hands[0] = left; hands[1] = right;
            bool near = false;
            for (int h = 0; h < 2; h++)
            {
                var d = hands[h];
                if (d == null || !d.Tracked) continue;
                bool touch = Near(t, d.Position);
                near |= touch;
                if (!input.GrabDown[h] || !touch) continue;
                if (_held < 0) Hold(h, d);
                else if (_held != h && !_two)
                {
                    // 두 번째 손: 두 손 사이 거리로 크기
                    var o = hands[_held];
                    if (o != null) { _two = true; _twoDist = Mathf.Max(0.05f, Vector3.Distance(o.Position, d.Position)); _twoScale = 1; _scale0 = t.localScale; }
                }
            }
            if (_held >= 0)
            {
                var d = hands[_held];
                int other = 1 - _held;
                if (!input.Grab[_held] || d == null)
                {
                    // 먼저 잡은 손을 놓으면 다른 손이 이어 잡는다
                    _held = -1;
                    if (_two && input.Grab[other] && hands[other] != null) Hold(other, hands[other]);
                    _two = false;
                }
                else
                {
                    if (_two)
                    {
                        var o = hands[other];
                        if (!input.Grab[other] || o == null) _two = false;
                        else
                        {
                            float k = Vector3.Distance(d.Position, o.Position) / _twoDist;
                            k = Mathf.Clamp(_scale0.x * k, 0.2f, 3f) / _scale0.x;   // 너비 0.2~3 m
                            t.localScale = new Vector3(_scale0.x * k, _scale0.y * k, 1);
                            _twoScale = k;
                            SetBorder(_personal);
                        }
                    }
                    var off = _two ? _offPos * _twoScale : _offPos;
                    t.localPosition = d.Position + d.Rotation * off;
                    t.localRotation = d.Rotation * _offRot;
                }
            }
            _personal.Mat.SetColor("_FrameColor", _held >= 0 ? FrameHeld : near ? FrameNear : Frame);
        }

        void Hold(int h, VrLink.Device d)
        {
            var t = _personal.Go.transform;
            _held = h;
            _two = false;
            _offPos = Quaternion.Inverse(d.Rotation) * (t.localPosition - d.Position);
            _offRot = Quaternion.Inverse(d.Rotation) * t.localRotation;
        }

        /// <summary>손(방 좌표)이 개인 거울 판 가까이 있는지 — 판 크기 + GrabMargin, 판에서 GrabDepth 안.</summary>
        static bool Near(Transform t, Vector3 hand)
        {
            var p = Quaternion.Inverse(t.localRotation) * (hand - t.localPosition);
            var s = t.localScale;
            return Mathf.Abs(p.x) < s.x * 0.5f + GrabMargin && Mathf.Abs(p.y) < s.y * 0.5f + GrabMargin && Mathf.Abs(p.z) < GrabDepth;
        }

        void PlaceWorld()
        {
            var t = _world.Go.transform;
            t.SetPositionAndRotation(_anchorPos, _anchorRot);
            t.localScale = new Vector3(Mathf.Max(3f, _anchorH * 1.1f), _anchorH, 1);
            SetBorder(_world);
        }

        static void SetBorder(Glass g)
        {
            var s = g.Go.transform.localScale;
            g.Mat.SetVector("_Border", new Vector4(FrameWidth / Mathf.Max(0.01f, s.x), FrameWidth / Mathf.Max(0.01f, s.y), 0, 0));
        }

        Glass Make(string name, Transform parent)
        {
            var go = new GameObject(name) { layer = VrLayers.VrOnly };
            if (parent != null) go.transform.SetParent(parent, false);
            else Object.DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = QuadMesh();
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Hidden/VrcProto/Mirror")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor("_FrameColor", Frame);
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return new Glass { Go = go, R = r, Mat = mat };
        }

        static Mesh _quad;

        /// <summary>1×1 판 (Unity 기본 Quad와 같다: 앞면이 -Z, 판의 -forward 쪽에서 보인다). 렌더러에 Physics 모듈이 없어 CreatePrimitive를 쓰지 않는다.</summary>
        static Mesh QuadMesh()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "VrMirrorQuad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _quad.triangles = new[] { 0, 3, 1, 3, 0, 2 };
            _quad.RecalculateNormals();
            _quad.RecalculateBounds();
            return _quad;
        }

        /// <summary>VrView.Render에서 눈 카메라를 자리에 놓은 뒤, 그 눈을 그리기 전에: 보이는 거울마다 이 눈의 반사를 그린다.</summary>
        public void RenderFor(Camera eye, int mask, int width, int height, float quality)
        {
            if (Count == 0) return;
            bool planes = false;
            _all[0] = _world; _all[1] = _personal;
            foreach (var g in _all)
            {
                if (g == null || !g.Go.activeSelf) continue;
                var t = g.Go.transform;
                var n = -t.forward;
                var p = t.position;
                var eyePos = eye.transform.position;
                if (Vector3.Dot(eyePos - p, n) <= 1e-3f) { g.Mat.SetTexture("_MainTex", Texture2D.blackTexture); continue; }   // 뒤에서는 뒷면만
                if (!planes) { GeometryUtility.CalculateFrustumPlanes(eye, _planes); planes = true; }
                if (!GeometryUtility.TestPlanesAABB(_planes, g.R.bounds)) continue;
                EnsureTexture(g, width, height, quality);
                if (_cam == null) _cam = MakeCamera();

                var plane = new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, p));
                var refl = Reflection(plane);
                _cam.transform.SetPositionAndRotation(refl.MultiplyPoint(eyePos), eye.transform.rotation);   // 위치는 조명(반사광) 계산용
                _cam.worldToCameraMatrix = eye.worldToCameraMatrix * refl;
                _cam.projectionMatrix = eye.projectionMatrix;
                _cam.projectionMatrix = _cam.CalculateObliqueMatrix(ClipPlane(_cam.worldToCameraMatrix, p, n));   // 거울 뒤는 자른다
                _cam.nearClipPlane = eye.nearClipPlane; _cam.farClipPlane = eye.farClipPlane;
                _cam.clearFlags = eye.clearFlags;
                _cam.backgroundColor = eye.backgroundColor;
                _cam.cullingMask = mask;
                _cam.targetTexture = g.Tex;
                bool inv = GL.invertCulling;
                GL.invertCulling = !inv;   // 반사 행렬은 앞뒤가 바뀐다
                _cam.Render();
                GL.invertCulling = inv;
                g.Mat.SetTexture("_MainTex", g.Tex);
            }
        }

        static void EnsureTexture(Glass g, int width, int height, float quality)
        {
            quality = Mathf.Clamp(quality, 0.25f, 1f);
            int w = Mathf.Max(64, Mathf.RoundToInt(width * quality)), h = Mathf.Max(64, Mathf.RoundToInt(height * quality));
            if (g.Tex != null && g.Tex.width == w && g.Tex.height == h) return;
            if (g.Tex != null) { g.Tex.Release(); Object.Destroy(g.Tex); }
            g.Tex = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 2, name = g.Go.name + "Tex" };
            g.Tex.Create();
        }

        static Camera MakeCamera()
        {
            var go = new GameObject("VrMirrorCam");
            Object.DontDestroyOnLoad(go);
            var c = go.AddComponent<Camera>();
            c.enabled = false;
            c.allowHDR = false;
            c.allowMSAA = true;
            return c;
        }

        /// <summary>평면 (n, d)에 대한 반사 행렬.</summary>
        static Matrix4x4 Reflection(Vector4 p)
        {
            var m = Matrix4x4.identity;
            m.m00 = 1 - 2 * p.x * p.x; m.m01 = -2 * p.x * p.y;     m.m02 = -2 * p.x * p.z;     m.m03 = -2 * p.w * p.x;
            m.m10 = -2 * p.y * p.x;     m.m11 = 1 - 2 * p.y * p.y; m.m12 = -2 * p.y * p.z;     m.m13 = -2 * p.w * p.y;
            m.m20 = -2 * p.z * p.x;     m.m21 = -2 * p.z * p.y;     m.m22 = 1 - 2 * p.z * p.z; m.m23 = -2 * p.w * p.z;
            return m;
        }

        /// <summary>거울 평면을 카메라 공간으로 (경사 근평면용, 거울 앞쪽이 남는다).</summary>
        static Vector4 ClipPlane(Matrix4x4 worldToCamera, Vector3 pos, Vector3 normal)
        {
            var cpos = worldToCamera.MultiplyPoint(pos + normal * 0.001f);
            var cn = worldToCamera.MultiplyVector(normal).normalized;
            return new Vector4(cn.x, cn.y, cn.z, -Vector3.Dot(cpos, cn));
        }

        /// <summary>VR로 보기를 끝낼 때: 거울·텍스처·카메라를 지운다.</summary>
        public void End()
        {
            RenderTexture.active = null;
            foreach (var g in new[] { _world, _personal })
            {
                if (g == null) continue;
                if (g.Tex != null) { g.Tex.Release(); Object.Destroy(g.Tex); }
                Object.Destroy(g.Mat);
                Object.Destroy(g.Go);
            }
            _world = _personal = null;
            if (_cam != null) Object.Destroy(_cam.gameObject);
            _cam = null;
            _held = -1; _two = false;
        }
    }
}
