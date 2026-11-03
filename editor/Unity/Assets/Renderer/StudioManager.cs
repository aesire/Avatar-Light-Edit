// 스튜디오 (2026-10-19 1차): Host "스튜디오" 페이지에서 시작했을 때만 만든다 (지연 로드, 꺼 두면 아무것도 하지 않는다).
//   배경  사용자가 가진 이미지를 불러와 배경으로: 360° 파노라마(등장방형 PNG·JPG·HDR) 또는 평면 이미지(화면에 꽉 차게).
//         직접 만든 배경은 넣지 않는다 (불러오기만). 노출·회전.
//         (2026-10-23) 그라데이션 = 위·아래 두 색 (이미지 없이).
//   그리기  메인 카메라의 CommandBuffer(BeforeForwardOpaque)로 화면 전체 사각형을 먼 평면 깊이에 그린다 → 아바타가 그 위를 덮어 아바타가 없는 곳만 배경,
//         그림자 바닥이 배경 위에 그림자를 곱할 수 있게 불투명 물체보다 먼저 그린다 (예전엔 BeforeForwardAlpha),
//         반투명 머티리얼은 그 위에 섞인다. 투명 배경 촬영 때는 끈다(Capture.HideBackground).
//   소품  (2차) 사용자가 가져온 IR(패키지·프리팹에서 만든 것)을 아바타와 따로 세운다. 바닥 가운데가 소품 원점, 위치·세로축 회전·크기만.
//         아바타 문서·올리기와는 무관한 미리보기·촬영 전용이다. 머티리얼은 소품마다 따로, 셰이더 표·팩은 아바타와 같이 쓴다.
//   심도  (2차) StudioDof를 카메라에 붙인다 (켰을 때만).
//   녹화  (2차) Time.captureFramerate로 시간을 프레임마다 1/fps씩 흘리며 프레임 끝마다 Capture로 찍는다 (실시간보다 느려도 결과는 고르다).
//         턴테이블 = 카메라가 피벗을 한 바퀴(또는 여러 바퀴) 돌고 끝나면 제자리, 고정 = 카메라 그대로 (PhysBone·표정·트래킹이 움직인다).
//         PNG 연속 이미지(투명 가능) 또는 raw(BGRA, Host가 MP4로 묶고 지운다).
//         (2026-10-24) 트래킹·말하기 중에는 실시간 녹화(실제 시각으로 장 번호), 말하기 소리도 함께, MP4는 파일 대신 파이프(RecordOut.cs).
// 카메라 위치 저장·고해상도 촬영은 Host가 기존 setCamera.state·capture로 한다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Viewer
{
    public sealed class StudioManager
    {
        readonly OrbitCamera _orbit;
        readonly SceneBuilder _scene;
        readonly MaterialFactory _materials;
        readonly Capture _capture;
        Camera Cam => _orbit.Camera;
        Material _mat;
        const CameraEvent BgEvent = CameraEvent.BeforeForwardOpaque;
        // 그림자 바닥 (2026-10-23): 발밑(y = 0) 넓은 판에 주광 그림자만 곱한다 (Hidden/VrcProto/ShadowFloor)
        GameObject _floor;
        Material _floorMat;
        Mesh _quad;
        CommandBuffer _cmd;
        Texture2D _tex;
        string _mode = "none";
        bool _hidden, _attached;   // _attached: 배경 그리기 명령이 카메라에 붙어 있음
        /// 배경을 그릴 때: 배경이 있고, 투명 촬영·오버레이로 숨기지 않았고, "아바타만" 카메라가 아닐 때
        bool Shown => _mode != "none" && !_hidden && !ViewAids.AvatarOnly;
        public bool Enabled { get; private set; }
        public Action<string> Notice;

        sealed class Prop { public string Id, Path, Error; public SceneBuilder Scene; public GameObject Holder; public Vector3 Size; public int Renderers; }
        readonly Dictionary<string, Prop> _props = new Dictionary<string, Prop>();
        StudioDof _dof;
        bool _recording, _cancel;

        public StudioManager(OrbitCamera orbit, SceneBuilder scene, MaterialFactory materials, Capture capture) { _orbit = orbit; _scene = scene; _materials = materials; _capture = capture; }

        public JNode Handle(JNode m)
        {
            if (m.Has("enabled")) { if (m["enabled"].AsBool) Start(); else Stop(); }
            if (!Enabled) return Msg.Make(Msg.StudioState).Set("enabled", false);
            string error = null;
            if (m.Has("background"))
            {
                var b = m["background"];
                try { SetBackground(b["mode"].AsString, b["path"].AsStringOrNull); }
                catch (Exception e) { error = e.Message; SetBackground("none", null); }
            }
            if (m.Has("background") && m["background"]["mode"].AsString == "gradient" && error == null)
            {
                _mat.SetColor("_Top", EnvColor.Read(m["background"]["top"], Color.white).linear);
                _mat.SetColor("_Bottom", EnvColor.Read(m["background"]["bottom"], Color.gray).linear);
            }
            if (m.Has("shadowFloor")) SetShadowFloor(m["shadowFloor"]);
            if (m.Has("exposure")) _mat.SetFloat("_Exposure", Mathf.Max(0, m["exposure"].AsFloat));
            if (m.Has("rotation")) _mat.SetFloat("_Rotation", m["rotation"].AsFloat);
            if (m.Has("props")) SetProps(m["props"]);
            if (m.Has("dof")) SetDof(m["dof"]);
            var props = JNode.Arr();
            foreach (var p in _props.Values)
                props.Add(JNode.Obj().Set("id", p.Id).Set("ok", p.Error == null).Set("error", p.Error).Set("renderers", p.Renderers)
                    .Set("size", JNode.FloatArray(new[] { p.Size.x, p.Size.y, p.Size.z })));
            return Msg.Make(Msg.StudioState).Set("enabled", true).Set("background", _mode)
                .Set("width", _tex != null ? _tex.width : 0).Set("height", _tex != null ? _tex.height : 0).Set("error", error)
                .Set("props", props).Set("focus", _dof == null ? 0 : _dof.Auto ? _dof.AutoFocus() : _dof.Focus);
        }

        /// <summary>세운 소품 전체의 범위 (없으면 null) — 스튜디오 고화질의 그림자 거리 맞추기.</summary>
        public Bounds? PropsBounds()
        {
            Bounds? b = null;
            foreach (var p in _props.Values)
            {
                if (p.Scene?.Root == null || !p.Holder.activeSelf) continue;
                var pb = p.Scene.AllBounds();
                if (b == null) b = pb; else { var x = b.Value; x.Encapsulate(pb); b = x; }
            }
            return b;
        }

        // ---------------- 소품 ----------------
        /// <summary>VR로 잡을 수 있는 소품 (id, 자리, 범위 — VrItemGrab, 2026-11-01). "아바타만"이면 보이지 않으니 뺀다.</summary>
        public void PropTargets(Action<string, Transform, Bounds> add)
        {
            if (ViewAids.AvatarOnly) return;
            foreach (var p in _props.Values)
                if (p.Scene?.Root != null && p.Holder.activeSelf) add(p.Id, p.Holder.transform, p.Scene.AllBounds());
        }

        /// <summary>전체 목록으로 맞춘다: 새 id·바뀐 경로는 불러오고, 빠진 것은 내리고, 나머지는 자리만 바꾼다.</summary>
        void SetProps(JNode list)
        {
            var keep = new HashSet<string>();
            foreach (var p in list.Items)
            {
                var id = p["id"].AsString; var path = p["path"].AsString;
                keep.Add(id);
                if (!_props.TryGetValue(id, out var pr) || pr.Path != path)
                {
                    if (pr != null) RemoveProp(pr);
                    _props[id] = pr = LoadProp(id, path);
                }
                var pos = p.Has("pos") ? p["pos"].ToFloatArray() : new float[3];
                var t = pr.Holder.transform;
                t.SetPositionAndRotation(new Vector3(pos[0], pos[1], pos[2]), Quaternion.Euler(0, p["rotY"].AsFloat, 0));
                t.localScale = Vector3.one * Mathf.Max(0.01f, p.Has("scale") ? p["scale"].AsFloat : 1);
                pr.Holder.SetActive(!p.Has("visible") || p["visible"].AsBool);
            }
            foreach (var id in new List<string>(_props.Keys)) if (!keep.Contains(id)) { RemoveProp(_props[id]); _props.Remove(id); }
        }

        Prop LoadProp(string id, string path)
        {
            var pr = new Prop { Id = id, Path = path, Holder = new GameObject("StudioProp " + id) };
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { pr.Error = "IR 파일이 없어요"; return pr; }
            pr.Scene = new SceneBuilder(_materials.ForProps());
            var res = pr.Scene.Load(path);
            if (pr.Scene.Root == null) { pr.Error = res.Warnings.Count > 0 ? res.Warnings[0].AsString : "불러오지 못했어요"; return pr; }
            // 바닥 가운데를 소품 원점으로 (원점에 두면 아바타 발밑 가운데)
            var b = pr.Scene.AllBounds();
            pr.Scene.Root.SetParent(pr.Holder.transform, false);
            pr.Scene.Root.localPosition = -new Vector3(b.center.x, b.min.y, b.center.z);
            pr.Size = b.size;
            VrLayers.SetLayer(pr.Holder, VrLayers.Scenery);   // "아바타만" 카메라가 뺀다
            foreach (var _ in pr.Scene.Renderers) pr.Renderers++;
            Debug.Log($"[VrcProto] 소품 {id}: {Path.GetFileName(path)} 렌더러 {pr.Renderers}, 크기 {b.size}");
            return pr;
        }

        void RemoveProp(Prop p)
        {
            p.Scene?.Unload();
            if (p.Holder != null) UnityEngine.Object.Destroy(p.Holder);
        }

        // ---------------- 심도 ----------------
        void SetDof(JNode d)
        {
            bool on = d["enabled"].AsBool;
            if (on && _dof == null) { _dof = Cam.gameObject.AddComponent<StudioDof>(); _dof.Scene = _scene; }
            if (!on && _dof != null) { UnityEngine.Object.Destroy(_dof); _dof = null; }
            if (_dof == null) return;
            if (d.Has("strength")) _dof.Strength = Mathf.Clamp01(d["strength"].AsFloat);
            if (d.Has("auto")) _dof.Auto = d["auto"].AsBool;
            if (d.Has("distance") && !_dof.Auto) _dof.Focus = Mathf.Max(0.05f, d["distance"].AsFloat);
            if (d.Has("show")) _dof.ShowFocus = d["show"].AsBool;
        }

        // ---------------- 녹화 ----------------
        public void CancelRecording() { if (_recording) _cancel = true; }
        public bool Recording => _recording;

        /// <summary>트래킹·말하기가 켜져 있는지 (켜져 있으면 실시간으로 녹화한다, RenderServer가 넣는다).</summary>
        public Func<bool> Realtime;
        /// <summary>말하기 마이크 (켜져 있으면 소리를 함께 담는다).</summary>
        public Func<LipSync> Lip;

        /// <summary>
        /// RenderServer가 코루틴으로 돌린다. 프레임마다 recordFrame, 끝나면 recordDone.
        /// 형식: png(폴더에 PNG, 투명 가능) · raw(폴더에 BGRA 파일, Host가 묶고 지운다 — 예전 방식·시험용) · pipe(Host가 연 파이프로 바로, RecordPipe).
        /// 실시간 녹화 (2026-10-24): 웹캠 트래킹·말하기는 실제 시간으로 움직이므로, 켜져 있으면 Time.captureFramerate(장마다 1/fps초)를 쓰지 않고
        /// 실제 시각으로 장 번호를 정한다 — 그리기가 느려 늦어지면 같은 그림을 그만큼 되풀이하고, 빠르면 다음 장 시각까지 기다린다.
        /// 예전엔 4K처럼 초당 10장만 찍히면 3초 동안 지은 표정이 영상에서 1초에 지나갔다. 말하기가 켜져 있으면 녹음도 같이 (pipe = MP4 소리, png = audio.wav).
        /// </summary>
        public IEnumerator Record(JNode m, Action<JNode> send)
        {
            if (!Enabled || _recording) { send(Msg.Make(Msg.RecordDone).Set("ok", false).Set("frames", 0).Set("error", Enabled ? "이미 녹화 중이에요" : "스튜디오를 먼저 시작해 주세요")); yield break; }
            var folder = m["folder"].AsStringOrNull;
            var format = m["format"].AsString;
            bool raw = format == "raw", pipe = format == "pipe", png = !raw && !pipe;
            int w = Mathf.Clamp(m["width"].AsInt, 16, 8192), h = Mathf.Clamp(m["height"].AsInt, 16, 8192);
            int fps = Mathf.Clamp(m.Has("fps") ? m["fps"].AsInt : 30, 1, 120);
            int total = Mathf.Max(1, Mathf.RoundToInt(fps * Mathf.Max(0.05f, m["seconds"].AsFloat)));
            bool turntable = m["mode"].AsString == "turntable";
            float turns = m.Has("turns") ? m["turns"].AsFloat : 1;
            bool transparent = png && m["transparent"].AsBool;
            bool realtime = m.Has("realtime") ? m["realtime"].AsBool : Realtime?.Invoke() == true;
            var lip = Lip?.Invoke();
            bool audio = lip != null && lip.Ok && !raw;
            string error = null;
            RecordPipe rp = null; PngWriter pw = null;
            try
            {
                if (!pipe) Directory.CreateDirectory(folder);
                if (pipe) rp = new RecordPipe(m["pipe"].AsString, w, h, fps, audio);
                if (png) pw = new PngWriter();
            }
            catch (Exception e) { error = e.Message; }
            if (error != null) { send(Msg.Make(Msg.RecordDone).Set("ok", false).Set("frames", 0).Set("error", error)); yield break; }

            _recording = true; _cancel = false;
            int prevRate = Time.captureFramerate; float prevScale = Time.timeScale;
            if (!realtime) Time.captureFramerate = fps;
            Time.timeScale = 1;
            var start = _orbit.GetState(); float yaw0 = _orbit.Yaw;
            JNode dollyTo = !turntable && m["to"].Kind == JKind.Object ? m["to"] : null;   // 카메라 달리: 지금 자리 → to (시작·끝은 천천히)
            bool moves = turntable || dollyTo != null;
            Debug.Log($"[VrcProto] 녹화 시작: {w}×{h} {fps}fps {total}프레임 {(turntable ? "턴테이블" : dollyTo != null ? "달리" : "고정")} {format}{(realtime ? " · 실시간" : "")}{(audio ? " · 소리" : "")} → {folder ?? m["pipe"].AsString}");
            // 뒤처지면 기다린다: raw는 Host가 MP4에 넣고 지운 파일 수(약 2 GB, 적어도 8장), pipe·png는 보내기·쓰기 큐 (몇 장이면 충분하다)
            int maxPending = Mathf.Max(8, (int)(2L * 1024 * 1024 * 1024 / ((long)w * h * 4)));
            int inflight = 0, written = 0;
            bool Behind() => raw ? PendingRaw(folder) >= maxPending : pipe ? rp.Pending + inflight >= 4 : pw.Pending >= 4;
            if (audio) lip.StartTape();
            float t0 = Time.realtimeSinceStartup;
            string Path1(int i) => Path.Combine(folder, $"frame_{i:D5}." + (raw ? "raw" : "png"));
            while (written < total && !_cancel && Enabled && error == null)
            {
                if (Behind())
                {
                    if (!realtime) Time.timeScale = 0;   // 기다리는 동안 시간(흔들림·애니메이션)이 흐르지 않게 (실시간이면 흐른 만큼 되풀이)
                    while (!_cancel && Enabled && error == null && Behind()) { if (pipe && rp.Error != null) error = rp.Error; yield return null; }
                    Time.timeScale = 1;
                    continue;
                }
                if (realtime && Mathf.FloorToInt((Time.realtimeSinceStartup - t0) * fps) < written) { yield return null; continue; }   // 다음 장 시각 전
                if (turntable) _orbit.SetState(start.Clone().Set("yaw", yaw0 + 360f * turns * written / total));
                else if (dollyTo != null) _orbit.SetState(LerpCamera(start, dollyTo, Mathf.SmoothStep(0, 1, total <= 1 ? 1 : (float)written / (total - 1))));
                yield return new WaitForEndOfFrame();
                // 이번 그림이 맡을 장 수: 실시간이면 지금 시각까지 밀린 만큼
                int count = realtime ? Mathf.Clamp(Mathf.FloorToInt((Time.realtimeSinceStartup - t0) * fps) + 1 - written, 1, total - written) : 1;
                try
                {
                    if (raw)
                    {
                        var b = _capture.RawBgra(w, h);
                        for (int c = 0; c < count; c++) File.WriteAllBytes(Path1(written + c), b);
                    }
                    else if (pipe)
                    {
                        inflight++;
                        int cnt = count;
                        _capture.RequestBgra(w, h, (data, err) =>
                        {
                            inflight--;
                            if (err != null) { error = err; return; }
                            var buf = rp.Rent();
                            data.CopyTo(buf);
                            rp.Frame(buf, cnt);
                        });
                    }
                    else
                    {
                        var paths = new string[count];
                        for (int c = 0; c < count; c++) paths[c] = Path1(written + c);
                        pw.Add(paths, _capture.Pixels(w, h, transparent, 1), w, h);
                    }
                }
                catch (Exception e) { error = e.Message; }
                if (error != null) break;
                written += count;
                send(Msg.Make(Msg.RecordFrame).Set("index", written - 1).Set("total", total).Set("path", pipe ? "" : Path1(written - 1)).Set("repeat", count));
            }
            while (inflight > 0) yield return null;   // 걸어 둔 GPU 읽기
            Time.captureFramerate = prevRate; Time.timeScale = prevScale;
            if (moves) _orbit.SetState(start);
            var tape = audio ? lip.StopTape() : null;
            bool cancelled = _cancel || !Enabled;
            bool keep = error == null && !cancelled;
            int samples = 0;
            if (rp != null)
            {
                if (keep)
                {
                    if (tape != null && tape.Length > 0) { var pcm = Wav.To48k(tape); samples = pcm.Length; rp.Audio(pcm, Wav.OutRate); }
                    rp.Finish();
                    while (!rp.Done) yield return null;
                    error = rp.Error;
                }
                else rp.Abort();
            }
            if (pw != null)
            {
                pw.Finish();
                while (!pw.Done) yield return null;
                error ??= pw.Error;
                if (keep && tape != null && tape.Length > 0)
                {
                    var pcm = Wav.To48k(tape); samples = pcm.Length;
                    try { Wav.Write(Path.Combine(folder, "audio.wav"), pcm, Wav.OutRate); } catch (Exception e) { error ??= e.Message; }
                }
            }
            _recording = false; _cancel = false;
            Debug.Log($"[VrcProto] 녹화 끝: {written}프레임" + (samples > 0 ? $" · 소리 {samples / (float)Wav.OutRate:0.0}초" : "") + (error != null ? " · " + error : cancelled ? " (멈춤)" : ""));
            send(Msg.Make(Msg.RecordDone).Set("ok", error == null && !cancelled).Set("frames", written).Set("cancelled", cancelled).Set("error", error)
                .Set("realtime", realtime).Set("audioSeconds", samples / (float)Wav.OutRate));
        }

        /// <summary>카메라 상태 사이 (t = 0~1). 돌기는 짧은 쪽으로, 거리는 비율로 (다가가는 속도가 고르게).</summary>
        static JNode LerpCamera(JNode a, JNode b, float t)
        {
            var pa = a["pivot"].ToFloatArray(); var pb = b.Has("pivot") ? b["pivot"].ToFloatArray() : pa;
            float F(string k) => b.Has(k) ? b[k].AsFloat : a[k].AsFloat;
            float da = Mathf.Max(1e-4f, a["distance"].AsFloat), db = Mathf.Max(1e-4f, F("distance"));
            return a.Clone()
                .Set("pivot", JNode.FloatArray(new[] { Mathf.Lerp(pa[0], pb[0], t), Mathf.Lerp(pa[1], pb[1], t), Mathf.Lerp(pa[2], pb[2], t) }))
                .Set("yaw", a["yaw"].AsFloat + Mathf.DeltaAngle(a["yaw"].AsFloat, F("yaw")) * t)
                .Set("pitch", Mathf.Lerp(a["pitch"].AsFloat, F("pitch"), t))
                .Set("distance", da * Mathf.Pow(db / da, t))
                .Set("fov", Mathf.Lerp(a["fov"].AsFloat, F("fov"), t));
        }

        static int PendingRaw(string folder)
        {
            try { int n = 0; foreach (var _ in Directory.EnumerateFiles(folder, "*.raw")) n++; return n; }
            catch (IOException) { return 0; }
        }

        void Start()
        {
            if (Enabled) return;
            var shader = Shader.Find("Hidden/VrcProto/StudioBackground");
            if (shader == null) throw new InvalidOperationException("스튜디오 배경 셰이더가 빌드에 없어요");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _quad = new Mesh { name = "StudioQuad", vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) }, triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            _cmd = new CommandBuffer { name = "VrcProto Studio Background" };
            _cmd.DrawMesh(_quad, Matrix4x4.identity, _mat, 0, 0);
            Camera.onPreRender += OnPreRender;
            Enabled = true;
            Apply();
        }

        public void Stop()
        {
            if (!Enabled) return;
            _cancel = true;
            foreach (var p in _props.Values) RemoveProp(p);
            _props.Clear();
            if (_dof != null) { UnityEngine.Object.Destroy(_dof); _dof = null; }
            if (_floor != null) { UnityEngine.Object.Destroy(_floor); UnityEngine.Object.Destroy(_floorMat); _floor = null; }
            Camera.onPreRender -= OnPreRender;
            if (Cam != null && _attached) Cam.RemoveCommandBuffer(BgEvent, _cmd);
            _attached = false;
            _cmd.Release();
            UnityEngine.Object.Destroy(_mat); UnityEngine.Object.Destroy(_quad);
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
            _tex = null; _mode = "none"; _hidden = false;
            Enabled = false;
        }

        /// <summary>투명 배경으로 촬영하는 동안 배경을 뺀다.</summary>
        public void Hide(bool hide)
        {
            if (!Enabled || _hidden == hide) return;
            _hidden = hide;
            Apply();
        }

        /// <summary>"아바타만" 카메라를 켜고 끌 때 (ViewAids.AvatarOnly): 배경을 빼거나 다시 붙인다. 바닥·소품은 배경 레이어라 카메라가 뺀다.</summary>
        public void AvatarOnlyChanged() { if (Enabled) Apply(); }

        void SetBackground(string mode, string path)
        {
            if (_attached) { Cam.RemoveCommandBuffer(BgEvent, _cmd); _attached = false; }
            if (_tex != null) { UnityEngine.Object.Destroy(_tex); _tex = null; }
            _mode = "none";
            if (mode == "panorama" || mode == "image")
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new FileNotFoundException("배경 이미지가 없어요", path);
                _tex = LoadImage(path);
                _tex.wrapModeU = mode == "panorama" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                _tex.wrapModeV = TextureWrapMode.Clamp;
                _mat.mainTexture = _tex;
                _mat.SetFloat("_Mode", mode == "panorama" ? 0 : 1);
                _mode = mode;
            }
            else if (mode == "gradient") { _mat.SetFloat("_Mode", 2); _mode = mode; }
            Apply();
        }

        void Apply()
        {
            bool want = Shown;
            if (want && !_attached) Cam.AddCommandBuffer(BgEvent, _cmd);
            if (!want && _attached) Cam.RemoveCommandBuffer(BgEvent, _cmd);
            _attached = want;
        }

        void SetShadowFloor(JNode f)
        {
            bool on = f["enabled"].AsBool;
            if (!on) { if (_floor != null) _floor.SetActive(false); return; }
            if (_floor == null)
            {
                var shader = Shader.Find("Hidden/VrcProto/ShadowFloor");
                if (shader == null) throw new InvalidOperationException("그림자 바닥 셰이더가 빌드에 없어요");
                _floorMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                // 물리 모듈이 빌드에 없어 CreatePrimitive(콜라이더를 붙임)를 쓰지 않고 판을 직접 만든다. 위(+Y)를 보는 80 m 정사각형 (단색 바닥은 30 m까지 배경에 녹는다)
                _floor = new GameObject("Studio.ShadowFloor");
                UnityEngine.Object.DontDestroyOnLoad(_floor);
                var mesh = new Mesh { name = "ShadowFloor", vertices = new[] { new Vector3(-40, 0, -40), new Vector3(-40, 0, 40), new Vector3(40, 0, 40), new Vector3(40, 0, -40) },
                    normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
                _floor.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = _floor.AddComponent<MeshRenderer>();
                r.sharedMaterial = _floorMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // 받기만 (깊이 텍스처에는 셰이더의 ShadowCaster 패스로 들어간다)
                r.receiveShadows = true;
                _floor.layer = VrLayers.Scenery;
            }
            _floorMat.SetFloat("_Strength", Mathf.Clamp01(f.Has("strength") ? f["strength"].AsFloat : 0.6f));
            // 단색 바닥 (2026-10-27): 색을 칠하고 깊이를 쓴다. 그림자만이면 화면에 곱하기만
            bool solid = f["mode"].AsStringOrNull == "solid";
            _floorMat.SetFloat("_Solid", solid ? 1 : 0);
            _floorMat.SetFloat("_SrcBlend", (float)(solid ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.DstColor));
            _floorMat.SetFloat("_DstBlend", (float)(solid ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
            _floorMat.SetFloat("_ZWrite", solid ? 1 : 0);
            if (solid) _floorMat.SetColor("_Color", EnvColor.Read(f["color"], new Color(0.8f, 0.8f, 0.82f)).linear);
            _floor.SetActive(true);
        }

        /// <summary>카메라마다 시선 축을 넣는다 (시야 반각·화면 비율이 곱해진 오른쪽·위).</summary>
        void OnPreRender(Camera c)
        {
            if (c != Cam || _mode == "none") return;
            var t = c.transform;
            float tanY = c.orthographic ? 0.001f : Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = c.targetTexture != null ? (float)c.targetTexture.width / c.targetTexture.height : c.aspect;
            float mirror = _orbit.Mirror && c.targetTexture == null ? -1 : 1;   // 거울 보기는 화면만 뒤집는다 (촬영은 그대로)
            _mat.SetVector("_Fwd", t.forward);
            _mat.SetVector("_Right", t.right * (tanY * aspect * mirror));
            _mat.SetVector("_Up", t.up * tanY);
            if (_tex != null) _mat.SetFloat("_Aspect", ((float)_tex.width / _tex.height) / aspect);
        }

        static Texture2D LoadImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".hdr") return HdrImage.Load(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); throw new InvalidDataException("PNG·JPG·HDR 이미지만 쓸 수 있어요"); }
            tex.anisoLevel = 4;
            return tex;
        }
    }

    /// <summary>Radiance .hdr (RGBE, 가로줄 RLE) → 반정밀 선형 텍스처.</summary>
    public static class HdrImage
    {
        public static Texture2D Load(string path)
        {
            var b = File.ReadAllBytes(path);
            int p = 0;
            string Line()
            {
                int s = p;
                while (p < b.Length && b[p] != '\n') p++;
                var l = System.Text.Encoding.ASCII.GetString(b, s, p - s).TrimEnd('\r');
                p++;
                return l;
            }
            var first = Line();
            if (!first.StartsWith("#?")) throw new InvalidDataException("Radiance HDR 파일이 아니에요");
            while (p < b.Length && Line().Length > 0) { }
            var res = Line().Split(' ');   // "-Y h +X w"
            if (res.Length < 4 || res[0] != "-Y" || res[2] != "+X") throw new InvalidDataException("지원하지 않는 HDR 방향이에요: " + string.Join(" ", res));
            int h = int.Parse(res[1]), w = int.Parse(res[3]);
            var px = new Color[w * h];
            var scan = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                if (w >= 8 && w < 32768 && b[p] == 2 && b[p + 1] == 2 && ((b[p + 2] << 8) | b[p + 3]) == w)
                {
                    p += 4;
                    for (int c = 0; c < 4; c++)
                        for (int x = 0; x < w;)
                        {
                            int n = b[p++];
                            if (n > 128) { n -= 128; byte v = b[p++]; for (int k = 0; k < n; k++) scan[(x++) * 4 + c] = v; }
                            else for (int k = 0; k < n; k++) scan[(x++) * 4 + c] = b[p++];
                        }
                }
                else { Array.Copy(b, p, scan, 0, w * 4); p += w * 4; }
                int row = h - 1 - y;   // 텍스처는 아래에서 위로
                for (int x = 0; x < w; x++)
                {
                    int e = scan[x * 4 + 3];
                    float f = e == 0 ? 0 : Mathf.Pow(2, e - 136);
                    px[row * w + x] = new Color(scan[x * 4] * f, scan[x * 4 + 1] * f, scan[x * 4 + 2] * f, 1);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, true, true);
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }
    }
}
