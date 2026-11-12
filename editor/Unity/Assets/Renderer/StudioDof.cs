// 스튜디오 피사계 심도 (2026-10-19 2차). 스튜디오를 켜고 "심도"를 켰을 때만 카메라에 붙인다 (Hidden/VrcProto/StudioDof).
// 깊이: 같은 시점의 숨은 카메라가 Hidden/VrcProto/StudioDepth 대체 셰이더로 1/깊이를 그린다
//   (Unity 깊이 텍스처는 ShadowCaster 패스에 기대고 반투명 머리카락이 빠져 머리카락 끝이 배경처럼 흐려진다).
//   (2026-11-12) 깊이는 카메라가 그리기 직전(Camera.onPreRender)에 그린다 — 주변 가림(StudioAo)과 같은 자리.
//   예전엔 OnRenderImage(화면 효과를 그리는 도중) 안에서 깊이 카메라를 또 그려서, 심도를 켜 두면 렌더러가 가끔 튕겼다
//   (렌더 도중 다른 카메라 그리기는 Unity가 받치지 않는 방식 — 고화질 MSAA·촬영 지터 누적처럼 한 프레임에 여러 번 그릴 때 잘 터졌다).
//   OnRenderImage에서는 그려 둔 깊이를 읽기만 하고 다 쓰면 돌려준다 (OnRenderImage는 onPostRender보다 뒤라 거기서 돌려주면 안 된다).
// 초점: 자동이면 아바타 머리(없으면 장면 가운데)까지의 시선 방향 거리, 아니면 사용자가 정한 거리.
// 흐림 세기는 화면 높이에 비례한다 (미리보기와 4K 촬영이 같은 모양으로 흐려진다).
// 후처리(PostFx)보다 뒤에 붙어 그 결과를 흐린다. 투명 배경 촬영에서도 켜 둔다 (흐림은 배경색에 대해 선형이라 검정·흰 차이 알파가 그대로 맞는다).
using UnityEngine;

namespace VrcProto.Viewer
{
    [RequireComponent(typeof(Camera))]
    public sealed class StudioDof : MonoBehaviour
    {
        public float Strength = 0.5f;   // 0~1
        public float Focus = 2;         // m (자동이면 매 프레임 정한다)
        public bool Auto = true;
        public bool ShowFocus;   // 초점 영역 보기 (앞 빨강 · 뒤 파랑)
        public SceneBuilder Scene;
        Material _mat;
        Camera _cam, _depthCam;
        Shader _depthShader;
        RenderTexture _depth;   // 이번에 그리는 동안의 깊이 (onPreRender에서 그려 OnRenderImage 끝에서 돌려준다)
        bool _drawing;          // 깊이 카메라를 그리는 중 (그 카메라의 onPreRender로 다시 들어오지 않게)

        void OnEnable()
        {
            _cam = GetComponent<Camera>();
            if (_depthShader == null) _depthShader = Shader.Find("Hidden/VrcProto/StudioDepth");
            if (_mat == null)
            {
                var s = Shader.Find("Hidden/VrcProto/StudioDof");
                if (s != null) _mat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                else Debug.LogWarning("[VrcProto] 심도 셰이더가 빌드에 없음");
            }
            Camera.onPreRender += PreRender;
        }

        void OnDisable()
        {
            Camera.onPreRender -= PreRender;
            ReleaseDepth();
        }

        void OnDestroy()
        {
            ReleaseDepth();
            if (_mat != null) Destroy(_mat);
            if (_depthCam != null) Destroy(_depthCam.gameObject);
        }

        bool Active => _mat != null && _depthShader != null && Strength > 0.001f;

        /// <summary>이 카메라가 그리기 직전: 지금 시점·투영 그대로 1/깊이를 그린다 (거울 보기의 뒤집힌 투영·촬영 지터도 따른다).</summary>
        void PreRender(Camera c)
        {
            if (c != _cam || _drawing || !Active) return;
            ReleaseDepth();
            if (_depthCam == null)
            {
                var go = new GameObject("StudioDepthCamera") { hideFlags = HideFlags.HideAndDontSave };
                _depthCam = go.AddComponent<Camera>();
                _depthCam.enabled = false;
            }
            _drawing = true;
            try
            {
                _depthCam.CopyFrom(_cam);
                _depthCam.projectionMatrix = _cam.projectionMatrix;
                _depthCam.clearFlags = CameraClearFlags.SolidColor;
                _depthCam.backgroundColor = Color.clear;
                _depthCam.allowHDR = false; _depthCam.allowMSAA = false;
                _depthCam.depthTextureMode = DepthTextureMode.None;   // 스튜디오 고화질의 SSAO 깊이·노멀 패스까지 따라 그리지 않게
                _depthCam.renderingPath = RenderingPath.Forward;
                _depth = RenderTexture.GetTemporary(Mathf.Max(1, _cam.pixelWidth), Mathf.Max(1, _cam.pixelHeight), 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                _depth.filterMode = FilterMode.Point;
                _depthCam.targetTexture = _depth;
                _depthCam.RenderWithShader(_depthShader, "");
                _depthCam.targetTexture = null;
            }
            finally { _drawing = false; }
        }

        void ReleaseDepth()
        {
            if (_depth == null) return;
            RenderTexture.ReleaseTemporary(_depth);
            _depth = null;
        }

        /// <summary>자동 초점 거리: 머리(없으면 보이는 범위 가운데)까지 카메라 시선 방향 거리.</summary>
        public float AutoFocus()
        {
            if (Scene == null || _cam == null) return Focus;
            var head = Scene.HumanBone("Head") ?? Scene.FindHumanoidHead();
            var p = head != null ? head.position : Scene.WorldBounds().center;
            return Mathf.Max(0.05f, Vector3.Dot(p - _cam.transform.position, _cam.transform.forward));
        }

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            // 깊이가 없으면(이번 그리기 전에 켜졌거나 세기 0) 그대로 넘긴다
            if (!Active || _depth == null) { Graphics.Blit(src, dst); ReleaseDepth(); return; }
            if (Auto) Focus = AutoFocus();
            int w = Mathf.Max(1, src.width / 2), h = Mathf.Max(1, src.height / 2);
            float radius = Mathf.Max(1, Strength * h * 0.025f);   // 반 해상도 픽셀 (세기 1 = 화면 높이의 2.5%)
            _mat.SetFloat("_Focus", Focus);
            _mat.SetFloat("_CocScale", 1.5f);
            _mat.SetFloat("_BokehRadius", radius);
            _mat.SetFloat("_ShowFocus", ShowFocus ? 1 : 0);
            var coc = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
            var half0 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            var half1 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            coc.filterMode = FilterMode.Bilinear; half0.filterMode = half1.filterMode = FilterMode.Bilinear;
            _mat.SetTexture("_DepthTex", _depth);
            Graphics.Blit(src, coc, _mat, 0);
            _mat.SetTexture("_CocTex", coc);
            Graphics.Blit(src, half0, _mat, 1);
            Graphics.Blit(half0, half1, _mat, 2);
            Graphics.Blit(half1, half0, _mat, 3);
            _mat.SetTexture("_DofTex", half0);
            Graphics.Blit(src, dst, _mat, 4);
            _mat.SetTexture("_DepthTex", null); _mat.SetTexture("_CocTex", null); _mat.SetTexture("_DofTex", null);   // 돌려준 임시 텍스처를 붙잡고 있지 않게
            RenderTexture.ReleaseTemporary(coc); RenderTexture.ReleaseTemporary(half0); RenderTexture.ReleaseTemporary(half1);
            ReleaseDepth();
        }
    }
}
