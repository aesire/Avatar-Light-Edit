// 스튜디오 주변 가림 (SSAO, 2026-10-23): RenderQuality가 스튜디오 고화질일 때 메인 카메라에 단다 (Hidden/VrcProto/StudioAo).
//   깊이: 메인 카메라가 그리기 직전(Camera.onPreRender) 같은 시점·투영의 숨은 카메라가 불투명·컷아웃의 시선 깊이를 RFloat에 그린다
//     (Hidden/VrcProto/StudioAoDepth). Unity 카메라 깊이 텍스처는 가림을 계산할 때 묶여 있지 않아 0만 읽혔고,
//     깊이·노멀 텍스처의 16비트 깊이는 비스듬한 면(다리)에 가로 줄무늬를 만들었다. 노멀은 셰이더가 이웃 깊이로 만든다.
//   불투명 물체와 하늘을 그린 직후, 반투명 전(BeforeImageEffectsOpaque)에 한 번에: 가림을 화면 크기 R8에 계산하고 가로·세로로 흐린 뒤 화면에 곱한다.
//   반투명 머리카락·배경은 어두워지지 않는다.
//   OnRenderImage([ImageEffectOpaque])를 쓰지 않는 것은 그러면 반투명이 MSAA 없이 그려지기 때문이다.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VrcProto.Viewer
{
    public sealed class StudioAo : IDisposable
    {
        const CameraEvent AoEvent = CameraEvent.BeforeImageEffectsOpaque;
        static readonly int AoTex = Shader.PropertyToID("_VrcProtoAoTex"), AoBlur = Shader.PropertyToID("_VrcProtoAoBlur"), AoDepth = Shader.PropertyToID("_VrcProtoAoDepth");
        readonly Camera _cam;
        readonly Material _mat;
        readonly Mesh _quad;
        readonly CommandBuffer _ao;
        readonly Shader _depthShader;
        Camera _depthCam;
        RenderTexture _depth;
        bool _on;

        public StudioAo(Camera cam)
        {
            _cam = cam;
            var s = Shader.Find("Hidden/VrcProto/StudioAo");
            if (s == null) { Debug.LogWarning("[VrcProto] 주변 가림 셰이더가 빌드에 없음"); return; }
            _depthShader = Shader.Find("Hidden/VrcProto/StudioAoDepth");
            if (_depthShader == null) { Debug.LogWarning("[VrcProto] 주변 가림 깊이 셰이더가 빌드에 없음"); return; }
            _mat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            _quad = new Mesh { name = "StudioAoQuad", vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) }, triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            _ao = new CommandBuffer { name = "VrcProto Studio AO" };
            _ao.GetTemporaryRT(AoTex, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            _ao.GetTemporaryRT(AoBlur, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            _ao.Blit(Texture2D.blackTexture, AoTex, _mat, 0);
            _ao.Blit(AoTex, AoBlur, _mat, 1);
            _ao.Blit(AoBlur, AoTex, _mat, 2);
            _ao.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _ao.DrawMesh(_quad, Matrix4x4.identity, _mat, 0, 3);
            _ao.ReleaseTemporaryRT(AoBlur);
            _ao.ReleaseTemporaryRT(AoTex);
        }

        public float Strength { set { if (_mat != null) _mat.SetFloat("_Strength", Mathf.Clamp01(value)); } }

        public bool Enabled
        {
            get => _on;
            set
            {
                if (_mat == null || value == _on || _cam == null) return;
                _on = value;
                if (_on)
                {
                    Camera.onPreRender += PreRender;
                    Camera.onPostRender += PostRender;
                    _cam.AddCommandBuffer(AoEvent, _ao);
                }
                else
                {
                    _cam.RemoveCommandBuffer(AoEvent, _ao);
                    Camera.onPreRender -= PreRender;
                    Camera.onPostRender -= PostRender;
                    ReleaseDepth();
                }
            }
        }

        /// <summary>메인 카메라가 그리기 직전: 같은 시점·투영(거울 보기·촬영 지터 포함)으로 깊이를 그린다.</summary>
        void PreRender(Camera c)
        {
            if (c != _cam) return;
            if (_depthCam == null)
            {
                var go = new GameObject("StudioAoDepthCamera") { hideFlags = HideFlags.HideAndDontSave };
                _depthCam = go.AddComponent<Camera>();
                _depthCam.enabled = false;
            }
            ReleaseDepth();
            _depthCam.CopyFrom(_cam);
            _depthCam.projectionMatrix = _cam.projectionMatrix;
            _depthCam.clearFlags = CameraClearFlags.SolidColor;
            _depthCam.backgroundColor = Color.clear;
            _depthCam.allowHDR = false; _depthCam.allowMSAA = false;
            _depthCam.renderingPath = RenderingPath.Forward;
            _depthCam.depthTextureMode = DepthTextureMode.None;
            _depth = RenderTexture.GetTemporary(_cam.pixelWidth, _cam.pixelHeight, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            _depth.filterMode = FilterMode.Point;
            _depthCam.targetTexture = _depth;
            _depthCam.RenderWithShader(_depthShader, "RenderType");
            _depthCam.targetTexture = null;
            Shader.SetGlobalTexture(AoDepth, _depth);
        }

        void PostRender(Camera c) { if (c == _cam) ReleaseDepth(); }

        void ReleaseDepth()
        {
            if (_depth == null) return;
            RenderTexture.ReleaseTemporary(_depth);
            _depth = null;
        }

        public void Dispose()
        {
            Enabled = false;
            if (_depthCam != null) UnityEngine.Object.Destroy(_depthCam.gameObject);
            _ao?.Release();
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
            if (_quad != null) UnityEngine.Object.Destroy(_quad);
        }
    }
}
