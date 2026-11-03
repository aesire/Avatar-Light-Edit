// 이미지 내보내기 (M1 간단 스크린샷, PLAN 3.7).
// 투명 배경: 셰이더 알파는 믿을 수 없으므로 검정·흰 배경으로 두 번 렌더하고 차이로 알파를 계산한다 (difference matting).
// 계산은 Linear 공간 반정밀 RT에서 한다 (블렌딩이 Linear에서 일어나므로).
// 투명 배경일 때는 후처리(PostFx)를 끄고 찍는다: 톤 매핑·블룸은 배경색에 따라 결과가 달라져 차이로 알파를 구할 수 없다.
//   대신 알파를 구한 뒤 색 보정(노출·대비·채도·톤 매핑)만 다시 넣는다 (PostFx.Grade, 2026-10-24) — 블룸만 빠진다.
// 지터 누적 (2026-10-23, 스튜디오 고화질): Samples > 1이면 카메라를 서브픽셀만큼(Halton 2·3) 흔들며 여러 번 찍어 평균한다 (Hidden/VrcProto/Accumulate).
//   MSAA가 못 잡는 셰이더 안 계단(외곽선·림·스페큘러)과 SSAO 잡티까지 사라진다. 같은 프레임 안에서 찍으므로 흔들림·표정은 같다.
// GPU 마무리 (2026-10-24): 알파 구하기·줄이기·sRGB 변환을 Hidden/VrcProto/CaptureResolve에서 하고 8비트 결과만 읽는다.
//   예전엔 장마다 부동소수 Color[]를 4~5개(검정·흰·결과·줄임·최종) 새로 만들어 4K 투명이면 수백 MB — 녹화 중 GC로 멈칫거렸다.
//   녹화(MP4)는 위아래 뒤집기·BGRA를 GPU 복사에서 하고, 파이프 녹화는 AsyncGPUReadback으로 기다리지 않고 읽는다.
using System;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VrcProto.Viewer
{
    public sealed class Capture
    {
        readonly Camera _cam;
        readonly EnvironmentManager _env;
        public Capture(Camera cam, EnvironmentManager env) { _cam = cam; _env = env; }
        /// <summary>한 장을 몇 번 흔들어 찍어 평균할지 (1 = 한 번, RenderQuality가 정한다).</summary>
        public int Samples = 1;
        Material _accum, _resolve;
        /// <summary>투명 배경으로 찍는 동안 스튜디오 배경을 뺀다 (StudioManager.Hide).</summary>
        public Action<bool> HideBackground;

        const int PassMatte = 0, PassOpaque = 1, PassRaw = 2, PassPack = 3;

        /// <summary>성공하면 null, 실패하면 오류 문구. rawAlpha = 화면에 내보내는 알파 그대로 (방송 오버레이 시험).</summary>
        public string Render(string path, int width, int height, bool transparent, int supersample, bool rawAlpha = false)
        {
            try
            {
                var png = EncodePng(Pixels(width, height, transparent, supersample, rawAlpha), width, height);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, png);
                Debug.Log($"[VrcProto] 캡처 저장: {path} ({width}×{height}, 투명={transparent}, SS={supersample}{(rawAlpha ? ", 화면 알파" : "")})");
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>RGBA 8비트 sRGB (Unity 순서: 아래 줄부터) → PNG. 어느 스레드에서나 불러도 된다 (녹화의 PNG 쓰기 스레드).</summary>
        public static byte[] EncodePng(byte[] rgba, int width, int height) =>
            ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);

        // 8비트 결과를 읽을 텍스처 (크기가 같으면 다시 쓴다)
        Texture2D _readTex;

        /// <summary>sRGB 8비트 RGBA (Unity 순서: 아래 줄부터). 돌려준 배열은 새것이라 다른 스레드에 넘겨도 된다.</summary>
        public byte[] Pixels(int width, int height, bool transparent, int supersample, bool rawAlpha = false)
        {
            var fx = _cam.GetComponent<PostFx>();
            if (fx != null) fx.Bypass = transparent;
            if (transparent) HideBackground?.Invoke(true);
            RenderTexture black = null, white = null, lin = null, graded = null, srgb = null;
            var prev = RenderTexture.active;
            try
            {
                if (!EnsureResolve()) throw new InvalidOperationException("촬영 셰이더가 빌드에 없어요 (Hidden/VrcProto/CaptureResolve)");
                int s = Mathf.Max(1, supersample), w = width * s, h = height * s;
                black = Linear(w, h, 24, Samples > 1 ? 1 : 8);
                RenderInto(black, transparent ? Color.black : rawAlpha ? Color.clear : ViewAids.AvatarOnly ? ViewAids.AvatarOnlyColor : _env.Background);
                if (transparent) { white = Linear(w, h, 24, Samples > 1 ? 1 : 8); RenderInto(white, Color.white); }
                lin = Linear(width, height, 0, 1);
                _resolve.SetInt("_Scale", s);
                if (white != null) _resolve.SetTexture("_White", white);
                Graphics.Blit(black, lin, _resolve, transparent ? PassMatte : rawAlpha ? PassRaw : PassOpaque);
                if (transparent && fx != null)
                {
                    graded = Linear(width, height, 0, 1);
                    if (fx.Grade(lin, graded)) { var t = lin; lin = graded; graded = t; }
                }
                srgb = RenderTexture.GetTemporary(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = true });
                Graphics.Blit(lin, srgb);   // Linear → sRGB는 GPU가 쓸 때
                if (_readTex == null || _readTex.width != width || _readTex.height != height)
                {
                    if (_readTex != null) UnityEngine.Object.Destroy(_readTex);
                    _readTex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                }
                RenderTexture.active = srgb;
                _readTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return _readTex.GetRawTextureData<byte>().ToArray();
            }
            finally
            {
                RenderTexture.active = prev;
                foreach (var rt in new[] { black, white, lin, graded, srgb }) if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (fx != null) fx.Bypass = false;
                if (transparent) HideBackground?.Invoke(false);
            }
        }

        static RenderTexture Linear(int w, int h, int depth, int msaa) =>
            RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, depth) { msaaSamples = msaa, sRGB = false });

        bool EnsureResolve()
        {
            if (_resolve != null) return true;
            var s = Shader.Find("Hidden/VrcProto/CaptureResolve");
            if (s == null) return false;
            _resolve = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // 녹화용 버퍼: 크기가 같으면 프레임마다 다시 쓴다 (4K면 프레임당 수백 MB를 새로 만들지 않게)
        RenderTexture _bgraRt;
        Texture2D _bgraTex;

        /// <summary>
        /// 녹화(MP4)용: 불투명 장면을 위에서 아래로 BGRA 8비트(sRGB 값) — 뒤집기·sRGB·채널 순서는 CaptureResolve의 Pack 패스가 한다 (예전엔 CPU가 픽셀마다).
        /// 텍스처는 일반 RGBA8(UNorm)이라 바이트 순서가 그대로 B,G,R,A다.
        /// </summary>
        RenderTexture RenderBgra(int width, int height)
        {
            if (!EnsureResolve()) throw new InvalidOperationException("촬영 셰이더가 빌드에 없어요 (Hidden/VrcProto/CaptureResolve)");
            if (_bgraRt == null || _bgraRt.width != width || _bgraRt.height != height)
            {
                if (_bgraRt != null) { _bgraRt.Release(); UnityEngine.Object.Destroy(_bgraRt); }
                _bgraRt = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = false }) { name = "RecordBGRA" };
                _bgraRt.Create();
            }
            var fx = _cam.GetComponent<PostFx>();
            if (fx != null) fx.Bypass = false;
            var src = Linear(width, height, 24, Samples > 1 ? 1 : 8);
            try
            {
                RenderInto(src, _env.Background);
                Graphics.Blit(src, _bgraRt, _resolve, PassPack);
            }
            finally { RenderTexture.ReleaseTemporary(src); }
            return _bgraRt;
        }

        /// <summary>녹화(raw 파일)용: 위에서 아래로 BGRA 바이트 (알파 255). 돌려준 배열은 다음 호출에 다시 쓰므로 바로 써야 한다.</summary>
        public byte[] RawBgra(int width, int height)
        {
            var rt = RenderBgra(width, height);
            if (_bgraTex == null || _bgraTex.width != width || _bgraTex.height != height)
            {
                if (_bgraTex != null) UnityEngine.Object.Destroy(_bgraTex);
                _bgraTex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            _bgraTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = prev;
            return _bgraTex.GetRawTextureData<byte>().ToArray();
        }

        /// <summary>
        /// 녹화(파이프)용: 찍고 GPU가 다 그리기를 기다리지 않고 읽기를 건다. done은 몇 프레임 뒤 메인 스레드에서 불린다 (순서대로).
        /// 읽기는 건 시점의 내용을 복사하므로 다음 프레임에 같은 텍스처를 다시 써도 된다.
        /// </summary>
        public void RequestBgra(int width, int height, Action<NativeArray<byte>, string> done)
        {
            var rt = RenderBgra(width, height);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, r =>
            {
                if (r.hasError) done(default, "GPU에서 프레임을 읽지 못했어요");
                else done(r.GetData<byte>(), null);
            });
        }

        /// <summary>카메라를 rt에 렌더한다. Samples > 1이면 흔들어 찍은 평균 (rt는 MSAA 없는 ARGBHalf).</summary>
        void RenderInto(RenderTexture rt, Color bg)
        {
            int n = Mathf.Max(1, Samples);
            if (n == 1 || !EnsureAccum()) { RenderOne(rt, bg, Vector2.zero, 0); return; }
            var one = RenderTexture.GetTemporary(new RenderTextureDescriptor(rt.width, rt.height, RenderTextureFormat.ARGBHalf, 24) { msaaSamples = 8, sRGB = false });
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt; GL.Clear(true, true, Color.clear); RenderTexture.active = prev;
                _accum.SetFloat("_Weight", 1f / n);
                for (int i = 0; i < n; i++)
                {
                    // 픽셀 하나 안의 고른 점 (Halton 2·3, 가운데 0)
                    var j = new Vector2(Halton(i + 1, 2) - 0.5f, Halton(i + 1, 3) - 0.5f);
                    RenderOne(one, bg, j, i);
                    Graphics.Blit(one, rt, _accum);
                }
            }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(one); Shader.SetGlobalFloat("_VrcProtoAoSeed", 0); }
        }

        bool EnsureAccum()
        {
            if (_accum != null) return true;
            var s = Shader.Find("Hidden/VrcProto/Accumulate");
            if (s == null) { Debug.LogWarning("[VrcProto] 누적 셰이더가 빌드에 없음 — 한 번만 찍음"); return false; }
            _accum = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        static float Halton(int i, int b)
        {
            float f = 1, r = 0;
            while (i > 0) { f /= b; r += f * (i % b); i /= b; }
            return r;
        }

        /// <summary>카메라를 rt에 한 번 렌더한다 (jitter = 픽셀 단위 투영 이동, 카메라 설정은 원래대로 돌려놓는다).</summary>
        void RenderOne(RenderTexture rt, Color bg, Vector2 jitter, int seed)
        {
            var prevTarget = _cam.targetTexture; var prevBg = _cam.backgroundColor; var prevFlags = _cam.clearFlags; var prevMask = _cam.cullingMask;
            try
            {
                // 뷰포트 보조 표시(거울 반전·와이어프레임만 보기)는 촬영에 섞지 않는다. 다음 프레임에 ViewAids가 다시 적용한다
                _cam.ResetProjectionMatrix();
                if (jitter != Vector2.zero)
                {
                    // 고정 투영 행렬은 화면 비율을 따라가지 않으므로 촬영 크기의 비율로 만든 뒤 흔든다
                    _cam.aspect = (float)rt.width / rt.height;
                    var p = _cam.projectionMatrix;
                    p.m02 += jitter.x * 2f / rt.width; p.m12 += jitter.y * 2f / rt.height;
                    _cam.projectionMatrix = p;
                }
                Shader.SetGlobalFloat("_VrcProtoAoSeed", seed);   // 장마다 SSAO 잡티를 다르게 → 평균으로 사라진다
                _cam.cullingMask = ViewAids.PreviewMask;   // VR 1인칭 사본·격자는 빼고 ("아바타만"이면 소품·바닥·함께 세운 아바타도)
                _cam.targetTexture = rt;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = bg; // Linear 프로젝트: 배경색은 감마로 지정 → 검정·흰은 같다
                _cam.Render();
            }
            finally
            {
                _cam.targetTexture = prevTarget; _cam.backgroundColor = prevBg; _cam.clearFlags = prevFlags; _cam.cullingMask = prevMask;
                if (jitter != Vector2.zero) { _cam.ResetProjectionMatrix(); _cam.ResetAspect(); }
            }
        }
    }
}
