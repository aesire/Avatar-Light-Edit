// 메시 광선 검사 (렌더러 빌드에 Physics 모듈이 없어서 직접 한다): 커서로 놓기·메시 편집·오브젝트 고르기가 같이 쓴다.
//   스킨드 메시는 지금 자세로 구워(BakeMesh) 월드 삼각형으로 바꾼 뒤 Möller–Trumbore로 맞춘다.
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public static class MeshPick
    {
        /// <summary>렌더러의 지금 모양을 월드 정점·삼각형으로. baked = 스킨드 메시를 구울 임시 메시 (부른 쪽이 Destroy).</summary>
        public static bool WorldTriangles(Renderer ren, Mesh baked, out Vector3[] v, out int[] tri)
        {
            if (ren is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                smr.BakeMesh(baked, true);
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                v = baked.vertices; for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                tri = smr.sharedMesh.triangles;
                return true;
            }
            if (ren is MeshRenderer && ren.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh)
            {
                var m = ren.transform.localToWorldMatrix;
                v = mesh.vertices; for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                tri = mesh.triangles;
                return true;
            }
            v = null; tri = null;
            return false;
        }

        /// <summary>광선에 가장 먼저 맞는 보이는 렌더러 (범위 상자로 먼저 거른다). 없으면 null.</summary>
        public static Renderer RendererAt(IEnumerable<Renderer> renderers, Ray ray)
        {
            float best = float.MaxValue; Renderer hit = null;
            var baked = new Mesh();
            try
            {
                foreach (var ren in renderers)
                {
                    if (ren == null || !ren.enabled || !ren.gameObject.activeInHierarchy || !ren.bounds.IntersectRay(ray, out var enter) || enter > best) continue;
                    if (!WorldTriangles(ren, baked, out var v, out var tri)) continue;
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                        if (RayTri(ray, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]], out float d) && d < best) { best = d; hit = ren; }
                }
            }
            finally { Object.Destroy(baked); }
            return hit;
        }

        /// Möller–Trumbore (양면)
        public static bool RayTri(Ray r, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a;
            var p = Vector3.Cross(r.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1 / det;
            var s = r.origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(r.direction, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 1e-5f;
        }
    }
}
