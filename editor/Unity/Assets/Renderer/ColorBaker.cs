// 색 굽기 (PLAN 3.3): lilToon 인스펙터의 "굽기"와 같은 계산 — Hidden/ltsother_baker 머티리얼에 메인 색·HSVG·그라데이션·조정 마스크를 넣고
// 원본 이미지(전체 해상도, 줄이지 않은 파일)를 Graphics.Blit → ReadPixels → PNG. 값은 Host가 문서의 지금 값으로 보낸다.
// 텍스처는 lilToon처럼 new Texture2D + LoadImage (sRGB)로 읽는다 — 미리보기에서 쓰는 줄인 텍스처를 쓰면 구운 결과가 흐려진다.
using System;
using System.IO;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public static class ColorBaker
    {
        /// <summary>구워서 m["out"]에 PNG로 쓴다. 성공하면 null, 아니면 오류 글.</summary>
        public static string Bake(JNode m, out int width, out int height)
        {
            width = height = 0;
            var shader = Shader.Find("Hidden/ltsother_baker");
            if (shader == null) return "굽기 셰이더(Hidden/ltsother_baker)가 렌더러에 없어요";
            var made = new System.Collections.Generic.List<UnityEngine.Object>();
            Texture2D Load(string file)
            {
                if (string.IsNullOrEmpty(file)) return null;
                if (!File.Exists(file)) throw new FileNotFoundException("텍스처 파일 없음: " + file);
                var t = new Texture2D(2, 2);
                if (!t.LoadImage(File.ReadAllBytes(file))) throw new InvalidDataException("이미지를 읽지 못함 (PNG·JPG만): " + Path.GetFileName(file));
                made.Add(t);
                return t;
            }
            var mat = new Material(shader); made.Add(mat);
            var bufRT = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                var src = Load(m["main"].AsString);
                if (src == null) return "메인 텍스처가 없어요";
                float[] c = m["color"].ToFloatArray(), h = m["hsvg"].ToFloatArray();
                if (c.Length >= 3) mat.SetColor("_Color", new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1));
                if (h.Length >= 4) mat.SetVector("_MainTexHSVG", new Vector4(h[0], h[1], h[2], h[3]));
                mat.SetFloat("_MainGradationStrength", m["gradationStrength"].AsFloat);
                mat.SetTexture("_MainTex", src);
                if (Load(m["gradation"].AsString) is Texture2D g) mat.SetTexture("_MainGradationTex", g);
                if (Load(m["mask"].AsString) is Texture2D k) mat.SetTexture("_MainColorAdjustMask", k);
                width = src.width; height = src.height;
                rt = RenderTexture.GetTemporary(width, height);
                Graphics.Blit(src, rt, mat);
                RenderTexture.active = rt;
                var outTex = new Texture2D(width, height); made.Add(outTex);
                outTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                outTex.Apply();
                var path = m["out"].AsString;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, outTex.EncodeToPNG());
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally
            {
                RenderTexture.active = bufRT;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                foreach (var o in made) UnityEngine.Object.Destroy(o);
            }
        }
    }
}
