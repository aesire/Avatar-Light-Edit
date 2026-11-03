// VR 그립으로 PhysBone 관절 잡기·고정 (2026-10-25, 사용자가 쓰는 Avatar Pose System의 PB 고정과 VRChat Posing을 참고 — 코드는 옮기지 않음).
// 손마다 하나씩 잡는다 (마우스 잡기 _grab과 따로). 잡는 동안 트리거 = 그 자리에 고정, 고정한 관절을 다시 잡으면 풀린다 (마우스와 같다).
// 고정은 마우스 고정과 같은 목록(_pins)이라 미리보기 "PhysBone 고정 모두 풀기"로도 풀린다.
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed partial class PhysBoneSim
    {
        readonly Hold[] _vrGrab = new Hold[2];
        readonly Vector3[] _vrTarget = new Vector3[2];

        /// <summary>선분 a–b(컨트롤러 몸통 선)에서 radius 안의 가장 가까운 입자. other 손이 잡은 것은 뺀다.</summary>
        Particle VrPick(Vector3 a, Vector3 b, float radius, int other, out Chain chain, out float dist)
        {
            Particle best = null; chain = null; dist = radius;
            if (!Enabled) return null;
            var held = _vrGrab[other]?.P;
            foreach (var ch in _chains)
            {
                if (!ch.Grabbable || !ch.Active) continue;
                for (int i = 1; i < ch.P.Count; i++)
                {
                    var p = ch.P[i];
                    if (p.Fixed || p == held) continue;
                    float d = SegDist(Position(p), a, b);
                    if (d <= dist) { dist = d; best = p; chain = ch; }
                }
            }
            return best;
        }

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float l = ab.sqrMagnitude;
            float t = l < 1e-10f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, ab) / l);
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>잡을 수 있는 입자가 있으면 거리와 자리.</summary>
        public bool VrNearest(Vector3 a, Vector3 b, float radius, int other, out float dist, out Vector3 pos)
        {
            var p = VrPick(a, b, radius, other, out _, out dist);
            pos = p != null ? Position(p) : default;
            return p != null;
        }

        public bool VrGrab(int h, Vector3 a, Vector3 b, float radius, int other)
        {
            var p = VrPick(a, b, radius, other, out var ch, out _);
            if (p == null) return false;
            _pins.RemoveAll(x => x.P == p);   // 고정한 관절을 다시 잡으면 고정이 풀린다
            _vrGrab[h] = new Hold(ch, p);
            _vrTarget[h] = Position(p);
            return true;
        }

        public bool VrHolding(int h) => _vrGrab[h] != null;
        public Vector3 VrHeldPos(int h) => _vrGrab[h] != null ? Position(_vrGrab[h].P) : default;
        public void VrDrag(int h, Vector3 target) { if (_vrGrab[h] != null) _vrTarget[h] = target; }
        public void VrRelease(int h) => _vrGrab[h] = null;

        /// <summary>잡고 있는 관절을 지금 끄는 자리에 고정하고 놓는다. 결과 안내 문구 (잡은 것이 없으면 null).</summary>
        public string VrPin(int h)
        {
            var g = _vrGrab[h];
            if (g == null) return null;
            _vrGrab[h] = null;
            var name = g.Ch.Root.name;
            if (!g.Ch.Posable) return $"{name}: 원본 PhysBone에서 고정(Allow Posing)이 꺼져 있어 고정할 수 없어요";
            g.Local = g.Ch.Root.InverseTransformPoint(_vrTarget[h]);
            _pins.Add(g);
            return name + " 고정";
        }

        /// <summary>고정한 관절 자리 (VR에서 표시).</summary>
        public void PinPositions(List<Vector3> o) { foreach (var h in _pins) if (h.Ch.Active) o.Add(Position(h.P)); }
    }
}
