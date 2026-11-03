// VRChat식 이동 (PLAN 3.12, VrBody.cs에서 옮김 — 2026-11-03 아바타 안으로 들어가기를 빼면서): 방(리그)을 움직인다.
//   VRChat 월드 기본값: 왼쪽 스틱 머리 방향 기준 — 앞 달리기 4 m/s(기울기에 비례) · 뒤·옆 2 m/s, 오른쪽 스틱 스냅 회전 30°(또는 부드러운 회전),
//   A 점프(점프 3 m/s, 중력 1 = 9.81 m/s²), 바닥은 아바타가 선 높이.
using UnityEngine;

namespace VrcProto.Viewer
{
    /// <summary>VRChat식 이동 (관찰자): 방(리그)을 움직인다 — 왼쪽 스틱 이동, 오른쪽 스틱 스냅 회전, A 점프·중력.
    /// 속도는 월드 미터라 방 크기와 상관없이 같다 (VRChat도 아바타 크기와 상관없이 같은 속도).</summary>
    public sealed class VrLocomotion
    {
        public const float RunSpeed = 4, WalkSpeed = 2, StrafeSpeed = 2, JumpImpulse = 3, Gravity = 9.81f, SnapAngle = 30;
        float _vy;
        bool _snapHeld;
        /// <summary>부드러운 회전 (VRChat Comfort Turning 끔과 같은 것). 빠르기 ⓘ VRChat 값 확인 못 함.</summary>
        public bool Smooth;
        public const float SmoothTurnSpeed = 120;   // 도/초, 스틱 끝까지

        public void Reset() { _vy = 0; _snapHeld = false; }

        /// <summary>움직였으면(스틱을 기울였으면) true.</summary>
        public bool Tick(Transform rig, VrInput input, VrLink.Device head, float floor, float dt)
        {
            dt = Mathf.Min(dt, 0.1f);
            var face = Quaternion.Euler(0, Flat(rig.rotation * head.Rotation), 0);

            // 이동: 앞은 기울기에 비례해 달리기까지, 뒤·옆은 걷기
            var mv = input.Move;
            if (mv.magnitude < 0.15f) mv = Vector2.zero;
            var local = new Vector3(mv.x * StrafeSpeed, 0, mv.y * (mv.y > 0 ? RunSpeed : WalkSpeed));
            rig.position += face * local * dt;

            // 회전 (머리를 축으로): 스냅(기본) 또는 부드러운 회전
            float tx = input.Turn.x;
            if (Smooth)
            {
                if (Mathf.Abs(tx) > 0.15f) rig.RotateAround(rig.TransformPoint(head.Position), Vector3.up, tx * SmoothTurnSpeed * dt);
            }
            else if (!_snapHeld && Mathf.Abs(tx) > 0.7f)
            {
                _snapHeld = true;
                rig.RotateAround(rig.TransformPoint(head.Position), Vector3.up, Mathf.Sign(tx) * SnapAngle);
            }
            else if (_snapHeld && Mathf.Abs(tx) < 0.3f) _snapHeld = false;

            // 점프·중력
            var p = rig.position;
            bool grounded = p.y <= floor + 1e-4f;
            if (grounded && input.JumpDown) _vy = JumpImpulse;
            if (!grounded || _vy > 0)
            {
                _vy -= Gravity * dt;
                p.y += _vy * dt;
                if (p.y <= floor) { p.y = floor; _vy = 0; }
                rig.position = p;
            }
            return mv != Vector2.zero;
        }

        /// <summary>회전의 가로 방향(Y축 각도). 위·아래를 보고 있어도 앞 방향으로 잰다.</summary>
        public static float Flat(Quaternion q)
        {
            var f = q * Vector3.forward;
            if (new Vector2(f.x, f.z).sqrMagnitude < 1e-4f) f = q * Vector3.up * -Mathf.Sign(f.y);   // 바로 위·아래를 볼 때는 정수리 방향
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
