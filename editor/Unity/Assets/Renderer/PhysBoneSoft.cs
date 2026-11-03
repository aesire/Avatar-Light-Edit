// 말랑 모드: 가슴 PhysBone을 말랑하게 (눌리면 납작해지며 옆으로 퍼지고, 중력에 늘어지고, 크게 출렁인다).
// 이 에디터 안에서만 쓰는 미리보기 효과다. VRChat의 PhysBone 동작이 아니고, 다른 제품의 코드·설정을 옮긴 것도 아니다.
// 상세 값은 내보이지 않고 프리셋만 고른다 (Host PhysBone 메뉴 → setPhysBones { soft: 프리셋 id }).
//   길이: 뼈가 프리셋 범위 안에서 줄고 늘어난다 (원래 Max Squish·Max Stretch보다 넓으면 이쪽)
//   중력: 아래를 향하면 길어지고(숙이면 늘어짐), 위를 향하면 짧아진다(누우면 납작)
//   부피: 줄어든 만큼 뼈를 옆으로 넓혀(늘어나면 좁혀) 부피를 대충 유지한다 → 눌리면 옆으로 퍼진다
//   출렁임: Pull 상한·Spring 하한으로 원래 값보다 더 출렁이게
//   맞닿음: 말랑 체인끼리 서로 밀어낸다 (양쪽이 부딪혀 눌림)
//   누르기: PhysBone 표시에서 잡아 몸 쪽으로 밀면 눌린다 (Pull)
// 대상은 뼈 이름으로 고른다 (breast · bust · boob · oppai · mune · 胸 · おっぱい).
using System;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed partial class PhysBoneSim
    {
        sealed class SoftPreset
        {
            public float Squish, Stretch, Sag, PullMax, SpringMin, Bulge;
        }

        static readonly (string Id, SoftPreset P)[] SoftPresets =
        {
            ("light", new SoftPreset { Squish = 0.25f, Stretch = 0.12f, Sag = 0.10f, PullMax = 0.2f, SpringMin = 0.6f, Bulge = 0.6f }),
            ("normal", new SoftPreset { Squish = 0.45f, Stretch = 0.25f, Sag = 0.22f, PullMax = 0.12f, SpringMin = 0.85f, Bulge = 1f }),
            ("smooth", new SoftPreset { Squish = 0.3f, Stretch = 0.3f, Sag = 0.18f, PullMax = 0.07f, SpringMin = 0.94f, Bulge = 0.8f }),
        };

        static readonly string[] SoftNames = { "breast", "bust", "boob", "oppai", "mune", "胸", "おっぱい" };

        SoftPreset _soft;

        /// <summary>말랑 모드 대상 체인 수 (이름으로 찾은 것).</summary>
        public int SoftChainCount { get { int n = 0; foreach (var c in _chains) if (c.SoftTarget) n++; return n; } }

        /// <summary>프리셋 id (light · normal · smooth). 그 밖의 값(off 등)이면 끈다.</summary>
        public void SetSoft(string id)
        {
            _soft = null;
            foreach (var (pid, p) in SoftPresets) if (pid == id) _soft = p;
        }

        static bool IsSoftTarget(Transform root)
        {
            var name = root.name.ToLowerInvariant();
            foreach (var k in SoftNames) if (name.Contains(k)) return true;
            return false;
        }

        SoftPreset SoftOf(Chain ch) => ch.SoftTarget ? _soft : null;

        /// 중력 쪽으로 향한 만큼 길게, 반대쪽이면 짧게
        static float SagLength(SoftPreset s, Vector3 dir) => 1 + s.Sag * Vector3.Dot(dir, Vector3.down);

        static float SoftRadius(Particle p, float scale) => Mathf.Max(p.Radius, 0.02f) * scale;

        /// 다른 말랑 체인의 입자와 겹치면 밀어낸다. 쉬는 자세에서 이미 겹쳐 있으면 그 거리까지만 (쉬는 자세에서 서로 밀어 떨리지 않게)
        void Contact(Chain ch, Particle p, ref Vector3 pos, float r)
        {
            float scale = Mathf.Abs(ch.Root.lossyScale.x);
            foreach (var o in _chains)
            {
                if (o == ch || !o.SoftTarget || !o.Active) continue;
                for (int i = 1; i < o.P.Count; i++)
                {
                    var q = o.P[i];
                    float min = Mathf.Min(r + SoftRadius(q, scale), (p.Target - q.Target).magnitude * 0.98f);
                    var d = pos - q.Pos; float l = d.magnitude;
                    if (l < min && l > 1e-6f) pos = q.Pos + d / l * min;
                }
            }
        }

        /// 뼈 길이가 변한 만큼 뼈 방향 축은 늘이고 나머지 두 축은 반대로 (부피 유지). 뼈 방향에 가장 가까운 로컬 축을 쓴다
        static void Bulge(Particle p, Particle c, SoftPreset soft)
        {
            float restLen = (c.Target - p.Target).magnitude;
            if (restLen < 1e-6f) return;
            var sim = c.Show - p.T.position;
            float s = Mathf.Clamp(sim.magnitude / restLen, 0.3f, 2f);
            var d = p.T.InverseTransformDirection(sim);
            float ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            int k = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
            float across = Mathf.Pow(s, -0.5f * soft.Bulge);
            var sc = p.RestLocalScale;
            for (int i = 0; i < 3; i++) sc[i] *= i == k ? s : across;
            p.T.localScale = sc;
        }
    }
}
