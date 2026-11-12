// Unity 렌더 서버 진입점. Host가 -parentHWND, -pipe 인자로 실행한다.
// 상태는 Host가 가지며 여기서는 받은 명령대로 장면을 구성하고 그린다 (PLAN 1장).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed class RenderServer : MonoBehaviour
    {
        PipeChannel _channel;
        readonly ConcurrentQueue<JNode> _inbox = new ConcurrentQueue<JNode>();
        SceneBuilder _scene;
        MaterialFactory _materials;
        EnvironmentManager _env;
        OrbitCamera _camera;
        Capture _capture;
        ViewAids _aids;
        readonly PhysBoneSim _phys = new PhysBoneSim();
        readonly HumanPoser _poser = new HumanPoser();
        BonePoser _bonePose;
        ItemPlacer _placer;
        ObjectMover _mover;
        FaceTracker _face;
        VrLink _vr;
        VrView _vrView;
        GuestAvatars _guests;
        StudioManager _studio;
        RenderQuality _quality;
        MeshEditor _meshEdit;
        bool _quitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var go = new GameObject("RenderServer");
            DontDestroyOnLoad(go);
            go.AddComponent<RenderServer>();
        }

        static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        // 시작 시간 재기 (2026-10-23): Awake의 단계마다 ms — "준비됨"을 보낸 뒤 로그 한 줄
        readonly System.Diagnostics.Stopwatch _bootLap = new System.Diagnostics.Stopwatch();
        readonly List<string> _bootLaps = new List<string>();
        float _bootAt;
        void Lap(string what) { _bootLaps.Add($"{what} {_bootLap.Elapsed.TotalMilliseconds:0}"); _bootLap.Restart(); }

        void Awake()
        {
            // 방송 창(OBS·게임 합치기)은 제목 있는 보통 창이라 Alt+F4로 닫힐 수 있다 → 렌더러를 끝내지 않고 오버레이만 끈다 (2026-11-12)
            Application.wantsToQuit += () =>
            {
                if (_quitting || !Overlay.Framed) return true;
                Send(Msg.Make(Msg.Overlay).Set("close", true));
                return false;
            };
            _bootAt = Time.realtimeSinceStartup; _bootLap.Start();
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Application.logMessageReceivedThreaded += OnLog;

            _materials = new MaterialFactory();
            Lap("셰이더 표·팩");
            _scene = new SceneBuilder(_materials);
            _env = new EnvironmentManager();
            _env.Register(new UnityLightingProvider());
            _env.Register(new PostProcessProvider());
            _env.Register(new LightsProvider());
            _env.Register(new LightVolumesProvider());
            _env.Register(new VRChatGlobalsProvider());
            _env.Register(new AudioLinkProvider());
            Lap("환경");
            var camGo = new GameObject("ViewportCamera");
            _camera = camGo.AddComponent<OrbitCamera>();
            _camera.Changed += s => Send(Msg.Make(Msg.CameraChanged).Set("state", s));
            _capture = new Capture(_camera.Camera, _env);
            _aids = camGo.AddComponent<ViewAids>();
            _aids.Init(_camera, _scene, _env, _phys);
            Lap("카메라·보조 표시");
            _aids.Notice = t => Send(Msg.Make(Msg.Notice).Set("text", t));
            _bonePose = new BonePoser(_poser) { Notice = _aids.Notice, Selected = b => Send(Msg.Make(Msg.PoseSelected).Set("bone", b)) };
            _bonePose.Edited = (bone, pose) => Send(Msg.Make(Msg.PoseEdited).Set("bone", bone).Set("pose", pose));
            _aids.BonePose = _bonePose;
            _mover = new ObjectMover(_scene) { Notice = _aids.Notice, Send = m => Send(m) };
            _mover.Selected = (kind, node) => { var s = Msg.Make(Msg.Selected).Set("kind", kind); if (node >= 0) s.Set("node", node); Send(s); };
            _bonePose.PickObject = (cam, mouse) => _mover.PickAt(cam, mouse);
            _bonePose.BoneChosen = () => _mover.Clear();
            var poseSelected = _bonePose.Selected;
            _bonePose.Selected = b => { poseSelected(b); if (b.Length > 0 || !_mover.HasTarget) Send(Msg.Make(Msg.Selected).Set("kind", b.Length > 0 ? "bone" : "").Set("bone", b)); };
            _aids.Mover = _mover;
            _face = new FaceTracker(_scene) { Send = m => Send(m) };
            _vr = new VrLink { Send = m => Send(m) };
            var rig = new GameObject("VrRig");
            _vrView = rig.AddComponent<VrView>();
            _vrView.Init(_vr, _camera, _scene, () => _aids.Grid);   // 바닥 격자는 미리보기 "바닥 격자"를 따른다
            _guests = new GuestAvatars(_materials) { Send = m => Send(m) };
            _mover.Guests = _guests;
            _vrView.Bones.Init(_poser, _phys, _bonePose.Edited, _scene, m => Send(m), _guests);   // VR 본·아바타 잡기: 본을 놓으면 데스크톱 끌기와 같은 poseEdited, 아바타는 avatarMoved·guestMoved
            Lap("자세·옮기기·얼굴");
            _studio = new StudioManager(_camera, _scene, _materials, _capture) { Notice = _aids.Notice, Realtime = () => _face.Active, Lip = () => _face.Mic };
            _capture.HideBackground = h => _studio.Hide(h);
            _vrView.Items.Studio = _studio;   // VR로 소품 잡기
            _vrView.HandCamera.Lip = () => _face.Mic;   // VR 카메라 영상 소리: 말하기가 켜져 있으면 그 마이크
            camGo.AddComponent<OverlaySupersample>().Init(_camera.Camera);
            _quality = camGo.AddComponent<RenderQuality>();
            _quality.Scene = _scene; _quality.Capture = _capture; _quality.PropsBounds = () => Join(_studio.PropsBounds(), _guests.Bounds());   // 그림자 거리: 소품 + 함께 세운 아바타
            _placer = new ItemPlacer(_scene) { Notice = _aids.Notice, Send = m => Send(m) };
            _aids.Placer = _placer;
            _meshEdit = new MeshEditor(_scene) { Notice = _aids.Notice, Send = m => Send(m) };
            Overlay.Send = m => Send(m);
            _aids.MeshEdit = _meshEdit;
            _camera.BlockKeys = () => _meshEdit.Enabled;
            Lap("스튜디오·놓기·메시 편집");
            _scene.MeshChanged += id =>
            {
                _meshEdit.OnMeshChanged(id);
                var d = _scene.CurrentData(id);
                Send(Msg.Make(Msg.MeshRebuilt).Set("mesh", id).Set("vertices", d?.VertexCount ?? 0).Set("triangles", d?.Triangles ?? 0));
            };
            _env.Apply(new JNode[0], _camera.Camera);
            Lap("환경 적용");

            var pipe = Arg("-pipe");
            if (pipe != null)
            {
                try
                {
                    _channel = PipeChannel.Connect(pipe, 10000);
                    _channel.OnMessage += m => _inbox.Enqueue(m);
                    _channel.OnClosed += e => _inbox.Enqueue(Msg.Make("_closed"));
                    _channel.Start();
                    Lap("파이프 연결");
                }
                catch (Exception e)
                {
                    Debug.LogError("[VrcProto] pipe connect failed: " + e);
                    Application.Quit(2);
                    return;
                }
                SendReady();
            }
            else
            {
                // Host 없이 단독 실행: -ir <경로> 로 장면만 띄워 본다 (디버깅용)
                var ir = Arg("-ir");
                if (ir != null) Load(ir);
            }
        }

        void SendReady()
        {
            var shaders = JNode.Arr();
            foreach (var s in _materials.KnownShaderNames()) shaders.Add(s);
            Send(Msg.Make(Msg.Ready).Set("protocolVersion", Msg.ProtocolVersion).Set("unityVersion", Application.unityVersion)
                .Set("shaders", shaders).Set("environment", _env.Schema()));
            Lap("준비됨 보내기");
            foreach (var line in _materials.PackLog) Debug.Log("[VrcProto] " + line);
            Debug.Log($"[VrcProto] 시작 시간: 엔진 시작 뒤 {_bootAt * 1000:0} ms에 Awake · " + string.Join(" · ", _bootLaps) + " (ms)");
        }

        void Send(JNode m) => _channel?.Send(m);

        void OnLog(string text, string stack, LogType type)
        {
            if (_channel == null || type == LogType.Log && !text.StartsWith("[VrcProto]")) return;
            var level = type == LogType.Log ? "info" : type == LogType.Warning ? "warn" : "error";
            _channel.Send(Msg.Make(Msg.Log).Set("level", level).Set("text", type == LogType.Exception ? text + "\n" + stack : text));
        }

        void Update()
        {
            int budget = 2000; // 슬라이더 드래그 폭주 시에도 프레임이 멈추지 않게
            while (budget-- > 0 && !_capturing && _inbox.TryDequeue(out var m))
            {
                _handled++;
                try { Handle(m); }
                catch (Exception e) { Debug.LogException(e); }
            }
            _scene.PumpMeshEdits();
            TextureLoader.PumpAll(6);   // 장면을 보인 뒤 바꾼 텍스처(편집·소품)는 준비되는 대로 프레임마다 조금씩 올린다
            _face.Update();
            _vr.Update();
            ForwardKeys();
            _env.Update(Time.unscaledDeltaTime); // 셰이더 시간을 멈춰도(pauseTime) 환경 전환은 진행
        }

        // PhysBone은 다른 갱신(메시지 처리·움직임 시험)이 끝난 뒤 자세에 반영한다
        void LateUpdate()
        {
            _vrView.Pre();      // VR로 보기: 포즈 기다리기 · 몸·이동 (PhysBone이 그 움직임에 반응하게 먼저)
            _phys.Tick(Time.deltaTime);
            _guests.Tick(Time.deltaTime, _phys);   // 함께 세운 아바타 PhysBone (지금 아바타 설정을 따른다)
            _vrView.Render();   // PhysBone까지 반영한 자세를 눈마다 그려 SteamVR로
            IdleCheck();
        }

        // 가만히 있으면 프레임 줄이기 (2026-11-02): 아무것도 안 해도 초당 60장을 그려 CPU를 늘 10%쯤 썼다.
        // 메시지·키·마우스·PhysBone 흔들림·움직임 시험·얼굴 트래킹·AudioLink·텍스처 올리기가 IdleAfter초 동안 없으면 RenderQuality.IdleFps로.
        // 하나라도 생기면 그 프레임에 바로 원래 제한으로 (20 fps에서 첫 반응까지 최대 0.05초 늦다).
        const float IdleAfter = 1.5f, PhysMoving = 0.005f;   // PhysBone: 아바타 크기 1 기준 5 mm/s보다 빠르게 흔들리면 움직이는 중
        int _handled;
        float _busyAt;
        Vector3 _lastMouse;

        void IdleCheck()
        {
            // 마우스는 미리보기 위에서 움직일 때만 (다른 창에서 움직여도 Input.mousePosition은 바뀐다)
            var mouse = Input.mousePosition;
            bool over = mouse.x >= 0 && mouse.y >= 0 && mouse.x < Screen.width && mouse.y < Screen.height;
            bool busy = _handled > 0 || Input.anyKey || over && mouse != _lastMouse || Input.mouseScrollDelta.sqrMagnitude > 0
                || _phys.Motion > PhysMoving || _guests.Motion > PhysMoving || _phys.Testing
                || _face.Active || _env.Animating || TextureLoader.Busy;
            _handled = 0; _lastMouse = mouse;
            float now = Time.realtimeSinceStartup;
            if (busy) _busyAt = now;
            RenderQuality.SetRest(now - _busyAt > IdleAfter, _capturing || _studio.Recording);
        }

        void Handle(JNode m)
        {
            switch (Msg.Type(m))
            {
                case "_closed": Quit(); break;
                case Msg.Quit: Quit(); break;
                case Msg.Hello: break;
                case Msg.LoadScene:
                    {
                        // 텍스처 캐시 모드 (Host 설정, 없으면 rgba)
                        var mode = m["texCache"].AsStringOrNull;
                        TexCache.Mode = mode == TexCache.Off || mode == TexCache.Dxt || mode == TexCache.Orig ? mode : TexCache.Rgba;
                        Load(m["irPath"].AsString);
                        break;
                    }
                case Msg.SetMatProp: _materials.SetProp(m["mat"].AsString, m["prop"].AsString, m["kind"].AsString, m["value"]); break;
                case Msg.SetMatKeyword: _materials.SetKeyword(m["mat"].AsString, m["keyword"].AsString, m["on"].AsBool); break;
                case Msg.SetMatRenderQueue: _materials.SetRenderQueue(m["mat"].AsString, m["value"].AsInt); break;
                case Msg.SetMatShader:
                    {
                        var info = _materials.SetShader(m["mat"].AsString, m["shader"].AsString);
                        if (info != null) Send(info.Set("t", Msg.MaterialInfo));
                        break;
                    }
                case Msg.SetBlendShape: _scene.SetBlendShape(m["renderer"].AsString, m["name"].AsString, m["weight"].AsFloat); break;
                case Msg.SetNodeActive: _scene.SetNodeActive(m["node"].AsInt, m["active"].AsBool); break;
                case Msg.SetRendererEnabled: _scene.SetRendererEnabled(m["renderer"].AsString, m["enabled"].AsBool); break;
                case Msg.SetNodeTransform: _scene.SetNodeTransform(m["node"].AsInt, m["trs"].ToFloatArray()); break;
                case Msg.SetAvatarTransform: _scene.SetAvatarTransform(m["trs"].ToFloatArray()); break;
                case Msg.Select: Select(m); break;
                case Msg.SetExpression:
                    {
                        var list = new List<(string, string, float)>();
                        foreach (var x in m["shapes"].Items) list.Add((x["renderer"].AsString, x["shape"].AsString, x["weight"].AsFloat));
                        bool keys = m["layer"].AsString == "keys";   // 블렌드쉐이프 단축키 (Host Core.ShapeKeys)
                        int miss = _scene.SetLayer(keys ? SceneBuilder.KeyLayer : SceneBuilder.ExpressionLayer, list);
                        if (miss > 0) Debug.LogWarning($"[VrcProto] {(keys ? "단축키" : "표정")}: 이 아바타에 없는 블렌드쉐이프 {miss}개는 건너뜀");
                        break;
                    }
                case Msg.FaceTracking: _face.Handle(m); break;
                case Msg.Vr: _vr.Handle(m); break;
                case Msg.VrPanel: _vrView.Panels.Handle(m); break;
                case Msg.VrKeyboard: _vrView.Panels.Keyboard(m); break;
                case Msg.SetQuality: _quality.Handle(m); break;
                case Msg.Studio:
                    {
                        JNode st;
                        try { st = _studio.Handle(m); }
                        catch (Exception e) { st = Msg.Make(Msg.StudioState).Set("enabled", false).Set("error", e.Message); }
                        Send(st);
                        break;
                    }
                case Msg.Record:
                    if (m["cancel"].AsBool) _studio.CancelRecording();
                    else { TextureLoader.FlushAll(); StartCoroutine(_studio.Record(m, Send)); }
                    break;
                case Msg.Guests:
                    {
                        if (m.Has("list") && _mover.GuestId.Length > 0) _mover.Clear();   // 다시 불러오면 고른 아바타가 없어질 수 있다
                        try { _guests.Handle(m, _poser); }
                        catch (Exception e) { Debug.LogException(e); Send(_guests.State()); }
                        break;
                    }
                case Msg.MoveTarget: { var err = _mover.Scripted(m); if (err != null) Debug.LogWarning("[VrcProto] moveTarget: " + err); break; }
                case Msg.SetEnvironment:
                    {
                        _env.Apply(new List<JNode>(m["providers"].Items).ToArray(), _camera.Camera);
                        break;
                    }
                case Msg.SetCamera:
                    if (m.Has("preset")) _camera.Preset(m["preset"].AsString, _scene, m["aspect"].AsFloat);
                    else if (m.Has("state")) _camera.SetState(m["state"]);
                    else if (m.Has("axis")) _camera.LookFromAxis(m["axis"].AsString);
                    else if (m.Has("ortho")) _camera.SetOrthographic(m["ortho"].AsBool);
                    else if (m.Has("frame") && _scene.TryRendererBounds(m["frame"].AsString, out var fb)) _camera.Frame(fb);
                    else if (m.Has("frameBone") && _scene.HumanBone(m["frameBone"].AsString) is Transform fbt)
                    {
                        // 손처럼 작은 본: 본과 그 자식 본들을 감싸는 범위 (최소 크기 size, 기본 0.15 m).
                        // 0.5 m 넘게 떨어진 자식은 뺀다 — Bone Proxy로 손 아래에 붙은 기믹(포즈 시스템 손잡이 등)이 멀리 있으면 손이 점처럼 찍혔다
                        float size = m.Has("size") ? m["size"].AsFloat : 0.15f;
                        var bb = new Bounds(fbt.position, Vector3.one * size);
                        foreach (var c in fbt.GetComponentsInChildren<Transform>())
                            if ((c.position - fbt.position).sqrMagnitude < 0.25f) bb.Encapsulate(c.position);
                        _camera.Frame(bb, m["aspect"].AsFloat);
                    }
                    break;
                case Msg.SetSelection: break; // 머티리얼·렌더러 선택은 Host UI 상태. 윤곽은 setOutline으로만 켠다.
                case Msg.SetOutline: _aids.SetOutline(m["renderer"].AsStringOrNull, m["on"].AsBool); break;
                case Msg.Overlay:
                    {
                        bool on = m["enabled"].AsBool;
                        Overlay.AlphaFix = !m.Has("alphaFix") || m["alphaFix"].AsBool;
                        // 보내는 곳·게임 창·합칠 때 아바타 상자 (2026-11-12)
                        if (m["box"].Kind == JKind.Object)
                        {
                            var b = m["box"];
                            if (b.Has("corner")) Overlay.BoxCorner = b["corner"].AsString;
                            if (b.Has("size")) Overlay.BoxSize = b["size"].AsFloat;
                            if (b.Has("aspect")) Overlay.BoxAspect = b["aspect"].AsFloat;
                        }
                        // 크로마키 색 [r, g, b] (0~1, 2026-11-12): OBS로 보낼 때 투명 대신 이 색을 배경으로. 없으면 투명
                        var key = m["chroma"];
                        Overlay.Chroma = key.Kind == JKind.Array && key.Count >= 3 ? new Color(key[0].AsFloat, key[1].AsFloat, key[2].AsFloat, 1) : (Color?)null;
                        var err = Overlay.Set(on, (long)m["hwnd"].AsDouble, m["adjust"].AsBool, m.Has("supersample") ? m["supersample"].AsFloat : 1, m["fps"].AsInt,
                            m["mode"].AsStringOrNull ?? "screen", (long)m["game"].AsDouble);
                        _studio.Hide(on);
                        if (err != null) Send(Msg.Make(Msg.Notice).Set("text", err));
                        Debug.Log($"[VrcProto] 방송 오버레이 {(on ? "켬" : "끔")}" + (err != null ? " · " + err : ""));
                        break;
                    }
                case Msg.SetView:
                    _aids.Set(m);
                    if (m.Has("avatarOnly")) _studio.AvatarOnlyChanged();
                    if (m.Has("floorFit")) _poser.SetFloorFit(m["floorFit"].AsBool);
                    // 셰이더 시간(_Time) 멈춤: 반짝임·깜빡임 같은 시간 효과를 고정해 촬영·비교 결과가 매번 같게
                    if (m.Has("pauseTime")) Time.timeScale = m["pauseTime"].AsBool ? 0f : 1f;
                    break;
                case Msg.SetPhysBones:
                    if (m.Has("enabled")) _phys.SetEnabled(m["enabled"].AsBool);
                    if (m["reset"].AsBool) _phys.Reset();
                    if (m["unpinAll"].AsBool) _phys.UnpinAll();
                    if (m.Has("motion")) _phys.StartMotion(m["motion"].AsString);
                    if (m.Has("soft")) _phys.SetSoft(m["soft"].AsString);
                    if (m.Has("legGuard")) _phys.SetLegGuard(m["legGuard"].AsBool);
                    if (m.Has("weight")) _phys.Weight = Mathf.Clamp((float)m["weight"].AsDouble, 1, 30);
                    if (m.Has("inelastic")) _phys.Inelastic = m["inelastic"].AsBool;
                    if (m.Has("smooth")) _phys.Smoothing = m["smooth"].AsBool;
                    if (m.Has("feel")) _phys.SetFeel(m["feel"]);
                    if (m["resetPeaks"].AsBool) _phys.ResetPeaks();
                    if (m.Has("advance"))   // 바로 돌리기 (촬영 자리 잡기 · E2E): 시간이 멈춰 있어도 돈다
                    {
                        float sec = (float)m["advance"].AsDouble, fps = m.Has("advanceFps") ? (float)m["advanceFps"].AsDouble : 60;
                        _phys.Advance(sec, fps); _guests.Advance(sec, fps, _phys);
                    }
                    if (m.Has("testGrab")) _phys.TestGrab(m["testGrab"].AsString, new Vector3(0, (float)m["testGrabY"].AsDouble, 0));   // 시험용 (E2E pbrules)
                    if (m["stats"].AsBool)
                    {
                        var detail = new List<string>();
                        var (pen, bones) = _phys.Penetrations(detail);
                        if (m.Has("describe")) foreach (var nm in m["describe"].Items) _phys.Describe(nm.AsString, detail);
                        var arr = JNode.Arr(); foreach (var d in detail) arr.Add(d);
                        var (jm, jx, jw) = _phys.Jitter();
                        var (sm, sx) = _phys.Swing();
                        Send(Msg.Make(Msg.PhysBoneStats).Set("penetrations", pen).Set("bones", bones).Set("detail", arr).Set("jitter", jm).Set("jitterMax", jx).Set("jitterWorst", jw).Set("swing", sm).Set("swingMax", sx).Set("tickMs", _phys.TickMs).Set("tickParts", _phys.TickParts));
                    }
                    break;
                case Msg.SetPose:
                    _poser.Apply(m["pose"]);
                    if (m["report"].AsBool) { var (fr, fn, fw) = _poser.FloorHeights(); Send(Msg.Make(Msg.PoseApplied).Set("floorRest", fr).Set("floorNow", fn).Set("floorPart", fw)); }
                    break;
                case Msg.SetMeshEdits:
                    {
                        var id = m["mesh"].AsString;
                        _scene.SetMeshEdits(id, m["ops"]);   // 반영되면 MeshChanged
                        break;
                    }
                case Msg.MeshTool: _meshEdit.Handle(m); break;
                case Msg.PlaceItem:
                    if (m["cancel"].AsBool) _placer.Cancel(true); else _placer.Start(m["node"].AsInt);
                    break;
                case Msg.PoseBone: { var err = _bonePose.Scripted(m); if (err != null) Debug.LogWarning("[VrcProto] poseBone: " + err); break; }
                case Msg.BakeColor:
                    {
                        var err = ColorBaker.Bake(m, out int bw, out int bh);
                        Send(Msg.Make(Msg.BakeDone).Set("out", m["out"].AsString).Set("ok", err == null).Set("error", err).Set("width", bw).Set("height", bh));
                        break;
                    }
                case Msg.AddTexture:
                    {
                        var sc = _scene.Scene;
                        if (sc == null) break;
                        var key = m["key"].AsString;
                        if (sc.FindTexture(key) != null) break;
                        var like = sc.FindTexture(m["like"].AsString);
                        var imp = like?.Importer ?? new IrTextureImporter();
                        sc.Textures.Add(new IrTexture
                        {
                            Guid = key, Path = m["path"].AsString, File = m["file"].AsString,
                            Importer = new IrTextureImporter { SRgb = imp.SRgb, NormalMap = imp.NormalMap, Mipmaps = imp.Mipmaps, AlphaIsTransparency = imp.AlphaIsTransparency, MaxSize = imp.MaxSize, Wrap = imp.Wrap, Filter = imp.Filter, Aniso = imp.Aniso },
                        });
                        break;
                    }
                case Msg.Capture:
                    _capturing = true;
                    StartCoroutine(CaptureNextFrame(m));
                    break;
                case Msg.ScreenShot: StartCoroutine(ScreenShot(m["path"].AsString)); break;
                default: Debug.LogWarning("[VrcProto] unknown message " + Msg.Type(m)); break;
            }
        }

        /// <summary>
        /// 촬영은 한 프레임 뒤에 (2026-10-23): 같은 프레임에 바꾼 블렌드쉐이프는 스킨드 메시가 다음 프레임에야 반영해,
        /// 바로 찍으면 앞 표정이 찍혔다 (블렌드쉐이프 세팅 사진이 실행마다 달랐다). 찍을 때까지 다음 메시지는 처리하지 않는다 —
        /// 촬영 뒤에 보낸 카메라 되돌리기 등이 먼저 적용되지 않게. -batchmode에서도 돌도록 WaitForEndOfFrame 대신 다음 프레임.
        /// </summary>
        System.Collections.IEnumerator CaptureNextFrame(JNode m)
        {
            yield return null;
            try
            {
                var path = m["path"].AsString;
                TextureLoader.FlushAll();   // 아직 올리지 않은 텍스처까지 넣고 찍는다 (찍은 결과가 늘 같게)
                string error;
                try { error = _capture.Render(path, m["width"].AsInt, m["height"].AsInt, m["transparent"].AsBool, Math.Max(1, m["supersample"].AsInt), m["alpha"].AsString == "raw"); }
                catch (Exception e) { error = e.Message; Debug.LogException(e); }
                Send(Msg.Make(Msg.CaptureDone).Set("path", path).Set("ok", error == null).Set("error", error));
            }
            finally { _capturing = false; }
        }
        bool _capturing;

        /// <summary>창에 보이는 그대로 저장 (OnGUI의 방향 기즈모까지). 두 프레임 기다려 마우스 위치·선택이 그려진 뒤에 찍는다.</summary>
        System.Collections.IEnumerator ScreenShot(string path)
        {
            TextureLoader.FlushAll();
            yield return null;
            yield return new WaitForEndOfFrame();
            string error = null;
            try
            {
                // ScreenCapture 모듈은 빼 두었으므로 프레임 끝의 백 버퍼를 직접 읽는다
                var prev = RenderTexture.active; RenderTexture.active = null;
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Destroy(tex);
            }
            catch (Exception e) { error = e.Message; }
            Send(Msg.Make(Msg.CaptureDone).Set("path", path).Set("ok", error == null).Set("error", error));
        }

        /// <summary>Host 목록에서 고르기 (또는 화면 점 아래 고르기). 조작 모드가 꺼져 있으면 무시된다 — Host가 먼저 켠다.</summary>
        void Select(JNode m)
        {
            if (m.Has("screen"))
            {
                var sp = m["screen"].ToFloatArray();
                _mover.PickAt(_camera.Camera, new Vector2(sp[0], sp[1]));
                _bonePose.Deselect();
                return;
            }
            switch (m["kind"].AsString)
            {
                case "bone":
                    _mover.Clear();
                    { var err = _bonePose.Scripted(JNode.Obj().Set("bone", m["bone"].AsString).Set("select", true)); if (err != null) _aids.Notice(err); }
                    break;
                case "item":
                    {
                        _bonePose.Deselect();
                        var why = _mover.SelectItem(m["node"].AsInt);
                        if (why != null) { _mover.Clear(); _aids.Notice(why); }
                        break;
                    }
                case "avatar": _bonePose.Deselect(); _mover.SelectAvatar(); break;
                case "guest": _bonePose.Deselect(); if (!_mover.SelectGuest(m["id"].AsString)) _mover.Clear(); break;
                default: _bonePose.Deselect(); _mover.Clear(); break;
            }
        }

        static Bounds? Join(Bounds? a, Bounds? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var x = a.Value; x.Encapsulate(b.Value); return x;
        }

        float _progressAt;

        /// <summary>장면 로드 진행을 Host에 알린다 (메인 스레드가 로드 중이라도 파이프에 바로 쓴다). 너무 잦으면 0.05초마다만.</summary>
        void Progress(string stage, int done, int total, bool force = false)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now - _progressAt < 0.05f && done < total) return;
            _progressAt = now;
            Send(Msg.Make(Msg.LoadProgress).Set("stage", stage).Set("done", done).Set("total", total));
        }

        /// <summary>메모리 나눠 보기 (MB): 프로세스 전용 · Mono 힙(쓰는 것/잡아 둔 것) · Unity 할당/예약 · 텍스처·메시 추정. 성능 조사용 로그</summary>
        public static string MemoryReport()
        {
            long M(long b) => b >> 20;
            long priv = 0; try { priv = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64; } catch { }
            long tex = 0, mesh = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) tex += (long)(t.width * (long)t.height * 4 * (t.mipmapCount > 1 ? 4 / 3.0 : 1));
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>()) mesh += (long)m.vertexCount * (12 + 12 + 16 + 8 + 4 + 32) + (m.isReadable ? (long)m.vertexCount * 84 : 0) + (long)m.blendShapeCount * m.vertexCount * 36;
            return $"전용 {M(priv)} · Mono 힙 {M(UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong())}/{M(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong())}"
                + $" · Unity 할당 {M(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong())}/예약 {M(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong())}"
                + $" · 텍스처 추정 {M(tex)} · 메시 추정 {M(mesh)}";
        }

        void Load(string irPath)
        {
            _scene.LoadProgress = (s, d, t) => Progress(s, d, t, d == 0 || d == t);
            _materials.Textures.Progress = (d, t) => Progress("textures", d, t, d == t);
            Progress("start", 0, 1, true);
            _placer?.Cancel(false);
            _meshEdit?.Clear();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = _scene.Load(irPath);
            double tScene = sw.Elapsed.TotalMilliseconds;
            _phys.Build(_scene);
            double tPhys = sw.Elapsed.TotalMilliseconds - tScene;
            _poser.Build(_scene, _phys.MovingBones());
            double tPose = sw.Elapsed.TotalMilliseconds - tScene - tPhys;
            // 텍스처를 모두 넣은 뒤 장면을 보인다 (2026-10-23 사용자: 먼저 보이고 나중에 채우기는 되돌림).
            // 머티리얼은 기다리지 않고 부탁만 해 두었으므로 PhysBone·자세 준비가 디코드와 겹친다
            _materials.Textures.Flush();
            double tTex = sw.Elapsed.TotalMilliseconds - tScene - tPhys - tPose;
            _bonePose.Clear();
            _mover.Clear();
            _camera.OnSceneLoaded(_scene);
            _face.OnSceneLoaded();
            Debug.Log($"[VrcProto] 장면 로드 시간 {sw.Elapsed.TotalMilliseconds:0} ms: IR {_scene.TParse:0} · 노드 {_scene.TNodes:0} · 메시 {_scene.TMesh:0} · 머티리얼 {_scene.TMat:0}"
                + $" · 나머지 {_scene.TRest:0} · PhysBone {tPhys:0} · 자세 {tPose:0} · 텍스처 마저 기다림 {tTex:0} — 텍스처 {TextureLoader.Decoded}개 (작업 스레드 기다림 {TextureLoader.WaitMs:0} · 올리기 {TextureLoader.UploadMs:0}) · 캐시 {TexCache.Mode} 읽음 {TexCache.Hits} · 새로 {TexCache.Misses}");
            TextureLoader.WaitMs = TextureLoader.UploadMs = 0; TextureLoader.Decoded = 0;
            Debug.Log("[VrcProto] 메모리 (장면 로드 뒤): " + MemoryReport());
            Progress("done", 1, 1, true);
            var b = _scene.AllBounds();
            var bounds = JNode.Obj().Set("min", JNode.FloatArray(new[] { b.min.x, b.min.y, b.min.z })).Set("max", JNode.FloatArray(new[] { b.max.x, b.max.y, b.max.z }));
            Send(Msg.Make(Msg.SceneLoaded).Set("materials", result.Materials).Set("schemas", result.Schemas).Set("warnings", result.Warnings).Set("bounds", bounds)
                .Set("physBones", JNode.Obj().Set("chains", _phys.ChainCount).Set("bones", _phys.BoneCount).Set("soft", _phys.SoftChainCount).Set("legGuard", _phys.LegGuardChains))
                .Set("humanoid", _scene.Scene?.Humanoid.Count ?? 0)
                .Set("pose", JNode.Obj().Set("ok", _poser.Ready).Set("problem", _poser.Problem).Set("rest", _poser.RestMuscles())));
        }

        // 뷰포트에 포커스가 있을 때 누른 단축키는 Host가 처리한다 (PLAN 1장: 단축키는 Host 한 곳)
        static readonly KeyCode[] Forward =
        {
            KeyCode.Z, KeyCode.Y, KeyCode.S, KeyCode.O, KeyCode.P, KeyCode.F, KeyCode.B, KeyCode.G, KeyCode.M,
            KeyCode.E, KeyCode.X, KeyCode.A, KeyCode.L, KeyCode.R, KeyCode.C, KeyCode.U, KeyCode.I, KeyCode.Delete,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
            // Keypad1~9 모두: Alt+NumPad1~9 = 스튜디오에 저장한 카메라 위치
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9,
        };

        void ForwardKeys()
        {
            if (_channel == null) return;
            // Unity처럼 조작: 오른쪽 버튼을 누른 동안 WASD·QE는 날기 (E = 메시 편집, S·A 등이 Host로 가지 않게)
            if (_camera.UnityNav && (Input.GetMouseButton(1) || _camera.Flying)) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            foreach (var k in Forward)
            {
                if (!Input.GetKeyDown(k)) continue;
                // Host 키 이름과 맞춘다 (D1, NumPad1 …)
                string name = k.ToString();
                if (name.StartsWith("Alpha")) name = "D" + name.Substring(5);
                else if (name.StartsWith("Keypad")) name = "NumPad" + name.Substring(6);
                Send(Msg.Make(Msg.Key).Set("key", name).Set("ctrl", ctrl).Set("shift", shift).Set("alt", alt));
            }
        }

        void Quit()
        {
            if (_quitting) return;
            _quitting = true;
            Application.Quit();
        }

        void OnApplicationQuit() { _face?.StopAll(); _vr?.Shutdown(); _channel?.Dispose(); }
    }
}
