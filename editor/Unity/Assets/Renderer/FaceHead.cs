// 페이셜 트래킹의 머리·몸·팔·시선 (FaceTracker가 매 프레임 부름, 미리보기 전용).
//   머리   소스가 준 머리 각도(도: yaw 왼쪽으로 돌면 +, pitch 위를 보면 +, roll 왼쪽 눈이 올라가면 +, 사람 기준)를 아바타 기준 축(장면 루트: 위 = Y, 앞 = Z)으로 더한다.
//   몸     body가 켜져 있으면 각도의 30%를 몸(Spine·Chest·UpperChest)이 천천히(약 0.3초 늦게) 따라가고, 나머지를 목 40% · 머리 60%가 맡는다.
//          머리가 가리키는 방향은 같고, 고개를 돌리면 어깨·가슴이 살짝 뒤따른다. 끄면 목 40% · 머리 60%만
//   팔     arms가 켜져 있고 팔이 T자처럼 옆으로 뻗어 있으면 (2D 방송용 VRM처럼) 위팔을 70° 내리고 조금 앞으로, 아래팔을 살짝 굽힌다.
//          자세를 불러와 팔이 이미 내려가 있으면 건드리지 않는다. 가슴은 4.5초 주기로 아주 조금 숨쉬듯 움직인다
//   위치   (2026-10-24) 머리가 좌우로 옮겨 가면 몸(Spine·Chest·UpperChest)을 그쪽으로 기울이고(아래 0.5 m를 축으로 본 각도, ±25°),
//          위아래로 옮겨 가면 Hips를 그만큼 올리고 내린다(아바타 몸통 길이에 맞춰 늘림, 사람 기준 ±12 cm). 머리 방향은 기울인 만큼 빼서 트래킹 각도 그대로
//   시선   얼굴 메시에 시선 블렌드쉐이프(eyeLook…)가 없을 때만 휴머노이드 눈 본(LeftEye·RightEye)을 돌린다 (좌우 ±15°, 위아래 ±10°)
// 본의 기준 회전은 처음 만질 때 읽고, 그 뒤 다른 것(자세 불러오기·본 조작)이 본을 바꾸면 그 값을 새 기준으로 삼는다. 끄면 기준 회전으로 되돌린다.
// 순서: 몸 → 목·머리 → 팔 (부모를 먼저 돌려야 자식의 기준이 맞다).
using System.Collections.Generic;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class FaceHead
    {
        sealed class Driven { public Transform T; public Quaternion Base, Written; public bool Touched; public bool Relax; }

        readonly SceneBuilder _scene;
        readonly Driven _neck, _head, _eyeL, _eyeR;
        readonly List<(Driven D, float W)> _body = new List<(Driven, float)>();   // Spine·Chest·UpperChest와 몸 몫 안의 비율
        readonly Driven _upL, _upR, _lowL, _lowR;
        readonly Transform _lowLT, _lowRT;
        Vector3 _bodyNow;   // 몸이 따라간 각도 (천천히)
        Vector3 _lean, _bodyDeg;   // 머리 위치로 기울인 각도 · 몸 본이 지금 맡은 각도 (_bodyNow + _lean, 숨쉬기가 그 위에 더함)
        readonly Transform _hips;
        Vector3 _hipsBase, _hipsWritten; bool _hipsTouched;
        public const float GazeYaw = 15f, GazePitch = 10f, BodyShare = 0.3f;
        public const float LeanPivot = 0.5f, LeanMax = 25f, MoveUpMax = 0.12f, HumanTorso = 0.6f;
        public const float ArmDown = 70f, ArmForward = 12f, ElbowBend = 12f;

        public FaceHead(SceneBuilder scene)
        {
            _scene = scene;
            Driven D(string human) { var t = scene.HumanBone(human); return t != null ? new Driven { T = t } : null; }
            _neck = D("Neck"); _head = D("Head") ?? (scene.FindHumanoidHead() is Transform h ? new Driven { T = h } : null);
            _eyeL = D("LeftEye"); _eyeR = D("RightEye");
            var spine = new[] { ("Spine", 0.3f), ("Chest", 0.35f), ("UpperChest", 0.35f) };
            float sum = 0;
            foreach (var (n, w) in spine) if (D(n) is Driven d) { _body.Add((d, w)); sum += w; }
            for (int i = 0; i < _body.Count; i++) _body[i] = (_body[i].D, _body[i].W / sum);
            _upL = D("LeftUpperArm"); _upR = D("RightUpperArm"); _lowL = D("LeftLowerArm"); _lowR = D("RightLowerArm");
            _lowLT = _lowL?.T; _lowRT = _lowR?.T;
            _hips = scene.HumanBone("Hips");
        }

        public bool HasHead => _head != null;
        public bool HasEyes => _eyeL != null || _eyeR != null;

        /// <summary>머리 각도(도, 사람 기준)를 더한다. mirror면 좌우(yaw·roll)를 뒤집는다. body면 몸이 일부를 늦게 따라간다.</summary>
        public void Head(Vector3 deg, bool mirror, bool body, float dt)
        {
            if (_head == null) return;
            if (mirror) { deg.x = -deg.x; deg.z = -deg.z; }
            var bodyWant = body && _body.Count > 0 ? deg * BodyShare : Vector3.zero;
            _bodyNow = Vector3.Lerp(_bodyNow, bodyWant, 1 - Mathf.Exp(-dt * 3.5f));
            _bodyDeg = (body ? _bodyNow : Vector3.zero) + _lean;
            if (!body && _lean == Vector3.zero) RestoreBody();
            else foreach (var (d, w) in _body) Turn(d, Euler(_bodyDeg * w));
            var rest = deg - (_body.Count > 0 ? _bodyDeg : Vector3.zero);   // 목·머리가 맡는 나머지 (머리가 가리키는 방향은 그대로)
            var add = Euler(rest);
            if (_neck != null) { Turn(_neck, Quaternion.Slerp(Quaternion.identity, add, 0.4f)); Turn(_head, Quaternion.Slerp(Quaternion.identity, add, 0.6f)); }
            else Turn(_head, add);
        }

        /// <summary>
        /// 머리 위치(미터, 사람 기준: x 왼쪽으로 옮기면 +, y 위로 +)를 몸 기울기와 Hips 높이로. Head보다 먼저 부른다 (기울기는 Head가 몸 본에 넣는다).
        /// mirror면 좌우를 뒤집는다.
        /// </summary>
        public void Move(Vector2 m, bool mirror)
        {
            if (_head == null) return;
            if (mirror) m.x = -m.x;
            // 왼쪽으로 기울이기 = 왼쪽 눈이 내려가는 쪽 (roll −)
            float lean = Mathf.Clamp(Mathf.Atan2(m.x, LeanPivot) * Mathf.Rad2Deg, -LeanMax, LeanMax);
            _lean = _body.Count > 0 ? new Vector3(0, 0, -lean) : Vector3.zero;
            if (_hips == null || _head.T == null) return;
            if (!_hipsTouched || (_hips.localPosition - _hipsWritten).sqrMagnitude > 1e-10f) { _hipsBase = _hips.localPosition; _hipsTouched = true; }
            float scale = (_head.T.position - _hips.position).magnitude / HumanTorso;   // 사람 몸통 길이 → 아바타 (월드 단위)
            var up = _scene.Root != null ? _scene.Root.up : Vector3.up;
            var world = up * (Mathf.Clamp(m.y, -MoveUpMax, MoveUpMax) * scale);
            _hips.localPosition = _hipsBase + (_hips.parent != null ? _hips.parent.InverseTransformVector(world) : world);
            _hipsWritten = _hips.localPosition;
        }

        /// <summary>머리 위치 따라가기를 끈다 (기울기 0, Hips 높이 되돌림).</summary>
        public void RestoreMove()
        {
            _lean = Vector3.zero;
            if (_hips != null && _hipsTouched && (_hips.localPosition - _hipsWritten).sqrMagnitude <= 1e-10f) _hips.localPosition = _hipsBase;
            _hipsTouched = false;
        }

        /// <summary>사람이 왼쪽으로 돌면 아바타도 자기 왼쪽으로 (Unity Y+ = 오른쪽으로 돌기), 위를 보면 X−,
        /// 왼쪽 눈(아바타 −X 쪽)이 올라가면 Z− (Unity Z+ 회전은 +X 쪽을 올린다).</summary>
        static Quaternion Euler(Vector3 deg) => Quaternion.Euler(-deg.y, -deg.x, -deg.z);

        /// <summary>
        /// 트래킹 중 팔·숨쉬기 (머리 다음에 부른다). arms가 꺼지면 되돌린다.
        /// 팔은 기준 자세에서 위팔이 거의 수평(T자·A자 위쪽)일 때만 내린다. 숨쉬기는 몸 본이 머리 각도로 이미 돌았으면 그 위에 더한다.
        /// </summary>
        public void Idle(bool arms, float time)
        {
            if (!arms) { RestoreArms(); return; }
            // 숨쉬기: 가장 위 몸 본(UpperChest 또는 Chest)을 앞뒤로 0.6°
            if (_body.Count > 0)
            {
                var top = _body[_body.Count - 1].D;
                float b = Mathf.Sin(time * Mathf.PI * 2 / 4.5f) * 0.6f;
                // 몸이 머리를 따라 돌지 않을 때도 숨은 쉰다: Turn은 기준에서 다시 계산하므로 몸 각도와 합쳐 준다
                float w = _body[_body.Count - 1].W;
                Turn(top, Euler(new Vector3(_bodyDeg.x * w, _bodyDeg.y * w + b, _bodyDeg.z * w)));
            }
            Arm(_upL, _lowL, _lowLT, -1);
            Arm(_upR, _lowR, _lowRT, +1);
        }

        /// <summary>side −1 = 왼팔(아바타 −X 쪽), +1 = 오른팔.</summary>
        void Arm(Driven up, Driven low, Transform lowT, int side)
        {
            if (up == null || up.T == null || lowT == null) return;
            bool fresh = !up.Touched || Quaternion.Angle(up.T.localRotation, up.Written) > 0.01f;
            if (fresh)
            {
                // 기준 자세의 위팔 방향 (아바타 기준): 수평에 가까우면 T자 → 내린다
                var root = _scene.Root != null ? _scene.Root : null;
                var dir = lowT.position - up.T.position;
                if (root != null) dir = root.InverseTransformDirection(dir);
                up.Relax = dir.sqrMagnitude > 1e-8f && Mathf.Abs(dir.normalized.y) < 0.5f;
            }
            if (!up.Relax) { if (up.Touched) Restore(up); if (low != null && low.Touched) Restore(low); return; }
            // 왼팔(−X)을 내리려면 Z+, 오른팔(+X)은 Z− (Unity: Z+ 회전이 +X를 위로, −X를 아래로). 앞으로는 X−
            Turn(up, Quaternion.Euler(-ArmForward, 0, -side * ArmDown));
            // 아래팔: 팔꿈치를 앞으로 살짝 굽힘 (내린 팔 기준에서 손이 앞으로)
            if (low != null) Turn(low, Quaternion.Euler(-ElbowBend, 0, 0));
        }

        /// <summary>시선 (각 눈의 좌우·위아래 -1~1, +는 사람의 왼쪽·위). 아바타 기준 축으로 돌린다.</summary>
        public void Eyes(Vector2 left, Vector2 right)
        {
            if (_eyeL != null) Turn(_eyeL, Quaternion.Euler(-left.y * GazePitch, -left.x * GazeYaw, 0));
            if (_eyeR != null) Turn(_eyeR, Quaternion.Euler(-right.y * GazePitch, -right.x * GazeYaw, 0));
        }

        public void RestoreHead() { RestoreBody(); Restore(_neck); Restore(_head); _bodyNow = _bodyDeg = Vector3.zero; }
        void RestoreBody() { foreach (var (d, _) in _body) Restore(d); }
        public void RestoreEyes() { Restore(_eyeL); Restore(_eyeR); }
        public void RestoreArms() { Restore(_upL); Restore(_upR); Restore(_lowL); Restore(_lowR); }
        public void Restore() { RestoreArms(); RestoreMove(); RestoreHead(); RestoreEyes(); }

        /// <summary>기준 회전에 아바타 기준 축의 회전 add를 더한다 (본 자리에서).</summary>
        void Turn(Driven d, Quaternion add)
        {
            if (d == null || d.T == null) return;
            if (!d.Touched || Quaternion.Angle(d.T.localRotation, d.Written) > 0.01f) { d.Base = d.T.localRotation; d.Touched = true; }
            var parent = d.T.parent != null ? d.T.parent.rotation : Quaternion.identity;
            var root = _scene.Root != null ? _scene.Root.rotation : Quaternion.identity;
            var baseWorld = parent * d.Base;
            var world = root * add * Quaternion.Inverse(root) * baseWorld;
            d.T.localRotation = Quaternion.Inverse(parent) * world;
            d.Written = d.T.localRotation;
        }

        static void Restore(Driven d)
        {
            if (d == null || d.T == null || !d.Touched) return;
            // 그 사이 다른 것이 본을 바꿨으면 그 값을 둔다
            if (Quaternion.Angle(d.T.localRotation, d.Written) <= 0.01f) d.T.localRotation = d.Base;
            d.Touched = false;
        }

        /// <summary>ARKit 시선 값 → 각 눈의 (좌우, 위아래). 왼쪽 눈은 바깥(Out)이 사람의 왼쪽, 오른쪽 눈은 안(In)이 사람의 왼쪽.</summary>
        public static (Vector2 L, Vector2 R) Gaze(Dictionary<string, float> v)
        {
            float G(string n) => v.TryGetValue(n, out var x) ? x : 0;
            var l = new Vector2(G("eyeLookOutLeft") - G("eyeLookInLeft"), G("eyeLookUpLeft") - G("eyeLookDownLeft"));
            var r = new Vector2(G("eyeLookInRight") - G("eyeLookOutRight"), G("eyeLookUpRight") - G("eyeLookDownRight"));
            return (l, r);
        }
    }
}
