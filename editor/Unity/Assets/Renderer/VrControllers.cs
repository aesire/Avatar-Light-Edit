// VR 손 표시 (2026-10-25 사용자 "내 손 위치를 알 수 없는 문제"): 손마다 공 하나.
//   처음엔 SteamVR 렌더 모델(컨트롤러 모양)을 그렸지만 헤드셋에서 제대로 보이지 않아(사용자 확인) 공 하나로 바꿨다 — 모양·막대는 그리지 않는다.
//   - 공 자리 = 잡는 점 (컨트롤러 끝: aim 자세 /pose/tip, 없으면 손잡이에서 앞으로 7 cm) — VrBoneGrab이 관절 거리를 재는 선의 끝과 같다.
//   - 색: 흰색, 잡을 수 있는 것이 있으면 노랑(조금 커짐), 잡는 중이면 주황 (VrBoneGrab이 Hot에 넣는다).
//   - 방(리그)의 자식이라 몸 모드 크기 배율과 상관없이 내 손에 실제 크기로 보인다. 눈 전용 레이어(거울·사진·PC 미리보기에는 안 보임).
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class VrControllers
    {
        public const float TipForward = 0.07f;   // aim 자세가 없을 때 손잡이에서 앞으로 이만큼을 끝으로
        const float Size = 0.022f, HotSize = 0.03f;

        readonly GameObject[] _balls = new GameObject[2];
        readonly Material[] _mats = new Material[2];
        Mesh _ball;

        public bool Enabled = true;
        /// <summary>손마다: 0 없음 · 1 잡을 수 있음 · 2 잡는 중 (VrBoneGrab이 넣는다).</summary>
        public readonly int[] Hot = new int[2];

        /// <summary>잡는 점 (방 기준): aim 자세, 없으면 손잡이에서 앞으로.</summary>
        public static Vector3 Tip(int h, VrInput input, VrLink.Device d) =>
            input.HasAim[h] ? input.AimPos[h] : d.Position + d.Rotation * new Vector3(0, 0, TipForward);

        /// <summary>VrView.Render에서 눈을 그리기 전에 (그릴 때의 자세로).</summary>
        public void Update(Transform rig, VrInput input, VrLink.Device left, VrLink.Device right)
        {
            for (int h = 0; h < 2; h++)
            {
                var d = h == 0 ? left : right;
                bool show = Enabled && d != null && d.Tracked;
                if (!show) { if (_balls[h] != null) _balls[h].SetActive(false); continue; }
                if (_balls[h] == null) Make(h, rig);
                var t = _balls[h].transform;
                _balls[h].SetActive(true);
                t.localPosition = Tip(h, input, d);
                t.localScale = Vector3.one * (Hot[h] > 0 ? HotSize : Size);
                _mats[h].color = Hot[h] == 2 ? new Color(1f, 0.55f, 0.15f, 0.95f) : Hot[h] == 1 ? new Color(1f, 0.9f, 0.2f, 0.95f) : new Color(0.92f, 0.95f, 1f, 0.85f);
            }
        }

        void Make(int h, Transform rig)
        {
            if (_ball == null) _ball = MakeBall();
            var go = new GameObject(h == 0 ? "VrHandBallLeft" : "VrHandBallRight") { layer = VrLayers.VrOnly };
            go.transform.SetParent(rig, false);
            go.AddComponent<MeshFilter>().sharedMesh = _ball;
            var mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);   // 몸 속에 손을 넣어도 보이게
            mat.renderQueue = 3102;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _balls[h] = go; _mats[h] = mat;
        }

        /// <summary>VR 끝.</summary>
        public void End()
        {
            for (int h = 0; h < 2; h++)
            {
                if (_balls[h] != null) Object.Destroy(_balls[h]);
                if (_mats[h] != null) Object.Destroy(_mats[h]);
                _balls[h] = null; _mats[h] = null; Hot[h] = 0;
            }
        }

        /// <summary>지름 1 공 (위도 8 × 경도 12). 손 공·VrBoneGrab 관절 공 (2026-11-03 VrFbt에서 옮김).</summary>
        internal static Mesh MakeBall()
        {
            const int lat = 8, lon = 12;
            var v = new List<Vector3>();
            var idx = new List<int>();
            for (int i = 0; i <= lat; i++)
            {
                float th = Mathf.PI * i / lat;
                for (int j = 0; j <= lon; j++)
                {
                    float ph = 2 * Mathf.PI * j / lon;
                    v.Add(0.5f * new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)));
                }
            }
            for (int i = 0; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    int a = i * (lon + 1) + j, b = a + lon + 1;
                    idx.Add(a); idx.Add(a + 1); idx.Add(b);
                    idx.Add(b); idx.Add(a + 1); idx.Add(b + 1);
                }
            var m = new Mesh { name = "VrBall" };
            m.SetVertices(v); m.SetTriangles(idx, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
