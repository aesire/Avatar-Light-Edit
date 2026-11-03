// 함께 세운 아바타 (2026-10-25 사용자 "여러 아바타를 한 공간 안에 배치"): 지금 연 아바타 말고 다른 아바타(IR)를 같은 장면에 더 세운다.
//   범위 (사용자 결정): 자리·방향·크기, 자세(본 잡기·지금 아바타 자세 복사·처음으로), PhysBone까지. 머티리얼·블렌드쉐이프·의상 편집과 올리기는 지금 연 아바타 하나만.
//   - Host가 전체 목록을 보낸다 (guests { list }): 새 id·바뀐 경로는 불러오고, 빠진 것은 내리고, 나머지는 자리·자세만 맞춘다. 목록은 Host 설정(avhost.json)에 — 실행 취소 없음.
//   - 아바타마다 SceneBuilder(머티리얼은 따로, 셰이더 표·팩은 같이) · HumanPoser(자세) · PhysBoneSim(켜짐·무게·느낌은 지금 아바타를 따른다).
//   - 자리는 월드 좌표 (지금 아바타를 불러온 자리와 같은 기준), 홀더 아래에 IR 루트를 불러온 그대로 둔다.
//   - 옮기기: 데스크톱 조작 모드(ObjectMover, 메시를 더블클릭) · VR 그립(VrBoneGrab) → guestMoved. 본을 잡아 놓으면 guestPoseEdited.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class GuestAvatars
    {
        public sealed class Guest
        {
            public string Id, Path, Error;
            public SceneBuilder Scene;
            public GameObject Holder;
            public readonly HumanPoser Poser = new HumanPoser();
            public readonly PhysBoneSim Phys = new PhysBoneSim();
            public int Renderers;
            public string PoseKey = "";
            public Transform Root => Holder != null ? Holder.transform : null;
            public bool Shown => Scene?.Root != null && Holder.activeSelf;
        }

        readonly MaterialFactory _materials;
        readonly Dictionary<string, Guest> _guests = new Dictionary<string, Guest>();
        readonly List<Guest> _list = new List<Guest>();
        public Action<JNode> Send;

        public GuestAvatars(MaterialFactory materials) { _materials = materials; }

        /// <summary>보이는(불러온) 아바타들 — 매 프레임 할당 없이 돌 수 있게 목록을 그대로 준다.</summary>
        public List<Guest> All => _list;

        public Guest Find(string id) => id != null && _guests.TryGetValue(id, out var g) ? g : null;

        /// <summary>guests { list?, copyPose?: id, pose?: { id, pose } } → guestsState.</summary>
        public void Handle(JNode m, HumanPoser main)
        {
            if (m.Has("list")) SetList(m["list"]);
            if (m.Has("copyPose") && Find(m["copyPose"].AsString) is Guest g && g.Poser.Ready && main.Ready)
            {
                var pose = main.CurrentPose();
                g.Poser.Apply(pose); g.Phys.Reset();
                PoseEdited(g, pose);
            }
            Send?.Invoke(State());
        }

        void SetList(JNode list)
        {
            var keep = new HashSet<string>();
            foreach (var p in list.Items)
            {
                var id = p["id"].AsString; var path = p["path"].AsString;
                keep.Add(id);
                if (!_guests.TryGetValue(id, out var g) || g.Path != path)
                {
                    if (g != null) Remove(g);
                    _guests[id] = g = Load(id, path);
                }
                var pos = p.Has("pos") ? p["pos"].ToFloatArray() : new float[3];
                var q = p.Has("rot") ? p["rot"].ToFloatArray() : null;
                var rot = q != null && q.Length == 4 ? new Quaternion(q[0], q[1], q[2], q[3]).normalized : Quaternion.Euler(0, p["rotY"].AsFloat, 0);
                var t = g.Holder.transform;
                t.SetPositionAndRotation(new Vector3(pos[0], pos[1], pos[2]), rot);
                t.localScale = Vector3.one * Mathf.Clamp(p.Has("scale") ? p["scale"].AsFloat : 1, 0.05f, 20);
                g.Holder.SetActive(!p.Has("visible") || p["visible"].AsBool);
                // 자세는 바뀌었을 때만 (본 잡기로 놓은 자세가 Host를 돌아 다시 와도 PhysBone을 흔들지 않게)
                var poseKey = p["pose"].IsNull ? "" : p["pose"].ToJson();
                if (poseKey != g.PoseKey && g.Poser.Ready) { g.Poser.Apply(p["pose"].IsNull ? null : p["pose"]); g.Phys.Reset(); }
                g.PoseKey = poseKey;
            }
            foreach (var id in new List<string>(_guests.Keys)) if (!keep.Contains(id)) { Remove(_guests[id]); _guests.Remove(id); }
            _list.Clear();
            foreach (var g in _guests.Values) if (g.Scene?.Root != null) _list.Add(g);
        }

        Guest Load(string id, string path)
        {
            var g = new Guest { Id = id, Path = path, Holder = new GameObject("GuestAvatar " + id) };
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { g.Error = "IR 파일이 없어요"; return g; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            g.Scene = new SceneBuilder(_materials.ForProps());
            var res = g.Scene.Load(path);
            if (g.Scene.Root == null) { g.Error = res.Warnings.Count > 0 ? res.Warnings[0].AsString : "불러오지 못했어요"; g.Scene = null; return g; }
            g.Scene.Root.SetParent(g.Holder.transform, false);
            VrLayers.SetLayer(g.Holder, VrLayers.Scenery);   // 함께 세운 아바타도 "아바타만" 카메라에서 빠진다
            g.Phys.Build(g.Scene);
            g.Poser.Build(g.Scene, g.Phys.MovingBones());
            foreach (var _ in g.Scene.Renderers) g.Renderers++;
            Debug.Log($"[VrcProto] 함께 세운 아바타 {id}: {System.IO.Path.GetFileName(path)} 렌더러 {g.Renderers} · PhysBone {g.Phys.ChainCount} · 휴머노이드 {(g.Poser.Ready ? "있음" : "없음")} ({sw.ElapsedMilliseconds} ms)");
            return g;
        }

        void Remove(Guest g)
        {
            g.Poser.Clear();
            g.Scene?.Unload();
            if (g.Holder != null) UnityEngine.Object.Destroy(g.Holder);
        }

        /// <summary>RenderServer.LateUpdate에서 지금 아바타 PhysBone 다음에.</summary>
        public void Tick(float dt, PhysBoneSim main)
        {
            Motion = 0;
            foreach (var g in _list)
            {
                if (!g.Holder.activeSelf) continue;
                g.Phys.CopyFrom(main);
                g.Phys.Tick(dt);
                Motion = Mathf.Max(Motion, g.Phys.Motion);
            }
        }

        /// <summary>함께 세운 아바타 PhysBone 중 가장 빠른 흔들림 (PhysBoneSim.Motion).</summary>
        public float Motion { get; private set; }

        /// <summary>렌더러 → 그 렌더러가 속한 아바타 (없으면 null).</summary>
        public Guest Owner(Renderer r)
        {
            if (r == null) return null;
            foreach (var g in _list) if (r.transform.IsChildOf(g.Holder.transform)) return g;
            return null;
        }

        /// <summary>보이는 아바타의 렌더러 전부 (광선 고르기용).</summary>
        public IEnumerable<Renderer> Renderers()
        {
            foreach (var g in _list)
                if (g.Holder.activeSelf) foreach (var r in g.Scene.Renderers) yield return r;
        }

        /// <summary>보이는 아바타 전체의 범위 (없으면 null) — 스튜디오 고화질의 그림자 거리 맞추기.</summary>
        public Bounds? Bounds()
        {
            Bounds? b = null;
            foreach (var g in _list)
            {
                if (!g.Holder.activeSelf) continue;
                var gb = g.Scene.AllBounds();
                if (b == null) b = gb; else { var x = b.Value; x.Encapsulate(gb); b = x; }
            }
            return b;
        }

        /// <summary>바뀐 자세 → Host (guestPoseEdited { id, pose }). Host가 목록으로 돌려보내도 다시 적용하지 않게 열쇠를 맞춰 둔다.</summary>
        public void PoseEdited(Guest g, JNode pose)
        {
            g.PoseKey = pose.ToJson();
            Send?.Invoke(Msg.Make(Msg.GuestPoseEdited).Set("id", g.Id).Set("pose", pose));
        }

        /// <summary>놓은 자리 → Host (guestMoved { id, pos, rot, scale }).</summary>
        public JNode Moved(Guest g)
        {
            var t = g.Holder.transform;
            Vector3 p = t.position; var q = t.rotation;
            return Msg.Make(Msg.GuestMoved).Set("id", g.Id).Set("pos", JNode.FloatArray(new[] { p.x, p.y, p.z }))
                .Set("rot", JNode.FloatArray(new[] { q.x, q.y, q.z, q.w })).Set("scale", t.localScale.x);
        }

        public JNode State()
        {
            var arr = JNode.Arr();
            foreach (var g in _guests.Values)
            {
                var o = JNode.Obj().Set("id", g.Id).Set("ok", g.Error == null).Set("error", g.Error).Set("renderers", g.Renderers)
                    .Set("humanoid", g.Poser.Ready).Set("physBones", g.Phys.ChainCount);
                if (g.Scene?.Root != null) { var b = g.Scene.AllBounds(); o.Set("height", b.size.y / Mathf.Max(1e-4f, g.Holder.transform.localScale.x)); }
                arr.Add(o);
            }
            return Msg.Make(Msg.GuestsState).Set("guests", arr);
        }

        public void Clear()
        {
            foreach (var g in _guests.Values) Remove(g);
            _guests.Clear(); _list.Clear();
        }
    }
}
