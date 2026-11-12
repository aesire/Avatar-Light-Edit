// Host ↔ Unity 메시지 정의. 모든 메시지는 { "t": 종류, "seq": 번호, ... } 형태의 JSON 객체.
// 상태의 단일 원천은 Host이므로 Unity로 가는 메시지는 "현재 값으로 설정하라"는 멱등 명령이다.
// (Unity 재기동 시 Host는 loadScene 후 원본과 다른 값 전부를 다시 보내 복원한다.)
using System.Collections.Generic;

namespace VrcProto.Protocol
{
    public static class Msg
    {
        public const int ProtocolVersion = 1;

        // ---- Host → Unity ----
        public const string Hello = "hello";                   // { protocolVersion }
        public const string LoadScene = "loadScene";           // { irPath }
        public const string SetMatProp = "setMatProp";         // { mat, prop, kind, value }
        public const string SetMatKeyword = "setMatKeyword";   // { mat, keyword, on }
        public const string SetMatRenderQueue = "setMatRQ";    // { mat, value }
        public const string SetMatShader = "setMatShader";     // { mat, shader }  (lilToon 렌더링 모드 전환 = 셰이더 교체)
        public const string SetBlendShape = "setBlendShape";   // { renderer, name, weight }
        public const string SetNodeActive = "setNodeActive";   // { node, active }
        public const string SetRendererEnabled = "setRendererEnabled"; // { renderer, enabled }
        public const string SetEnvironment = "setEnvironment"; // { providers: [ {provider, version, params, enabled?} ] }  (enabled: false = 꺼 둔 항목, 값만 보관)
        public const string SetCamera = "setCamera";           // { preset, aspect?: 찍을 사진의 가로÷세로 } | { state } | { axis: "+x".."-z" } | { ortho } | { frame: renderer } | { frameBone: 휴머노이드 이름, size?, aspect? }  (없는 렌더러·뼈는 답 없이 넘어간다) → cameraChanged { state }
        public const string SetView = "setView";               // { gizmo, grid, bones, mirror, draw: shaded|shadedWire|wire, background: env|gray|white|black|green, physBoneGizmos, poseBones, handleSpace: local|world, handleStyle: line|bold|solid|soft, handleSize: 0.5~2, axisStyle: disc|cube|mini, floorFit, pauseTime, avatarOnly: { on, color: [r,g,b] }, frameAspect }  (poseBones = 뷰포트 조작 모드(본·아이템·아바타 핸들), handleSpace = 핸들 축, floorFit = 자세의 가장 낮은 곳을 불러온 자세의 바닥 높이에)
        public const string LoadProgress = "loadProgress";   // 렌더러 → Host { stage: start|parse|renderers|textures|done, done, total }  장면 로드 진행 (sceneLoaded 전)
        public const string Overlay = "overlay";               // { enabled, hwnd, adjust, supersample? }  방송 오버레이: Host가 Unity 창을 떼어 화면 위에 띄운 동안 배경을 투명(DWM 알파)으로, 보조 표시·조작·스튜디오 배경은 끈다(후처리는 그대로). adjust = 끌어서 옮기기·테두리, supersample = 창보다 크게 그려 줄이는 배율(1~2). 렌더러 → Host: { moved: {x,y,w,h} } · { close: true }
        public const string SetSelection = "setSelection";     // { renderer?, mat? }  (Host의 렌더러·머티리얼 선택; 윤곽과 별개)
        public const string SetOutline = "setOutline";         // { renderer?: string, on: bool }  (아바타 파츠 bounds 윤곽 표시)
        public const string SetPhysBones = "setPhysBones";     // { enabled?, reset?, unpinAll?, motion?: none|sway|jump|turn|walk|vrwalk|snap|resize, soft?: off|light|normal|smooth, legGuard?: bool, stats?: bool, advance?: 초, advanceFps?: 60, resetPeaks?: bool, feel?: { follow, swing, pull, damp, gravity, falloff, slop(mm), friction, smooth(mm) } }  (PhysBone 미리보기, feel = 느낌 값(Host 설정), advance = 화면을 그리지 않고 그 시간만큼 바로 돌리기(시간이 멈춰 있어도, 촬영 자리 잡기·시험), resetPeaks = 관절별 최대 벗어남 다시 재기(시험용), soft = 말랑 모드 프리셋, legGuard = 다리 뚫림 막기, stats → physBoneStats)
        public const string SetPose = "setPose";               // { pose: null | { muscles: { 머슬 이름: 값 }, bodyRotation?: [x,y,z,w], bodyHeight? } }  (null = 불러온 그대로), report?: true → poseApplied
        public const string PoseBone = "poseBone";             // { bone: 휴머노이드 이름, axis?: [x,y,z] 월드 축, angle?: 도, move?: [x,y,z] 손·발 IK 이동, ring?: "x"|"y"|"z" 본 로컬 축(회전 고리와 같은 축)으로 angle만큼, arrow?: "x"|"y"|"z" + amount 관절을 그 축(이동 화살표)으로, select?: true 고르기만 }  (뷰포트 끌기와 같은 경로의 스크립트 조작, 시험용) → poseEdited / poseSelected
        public const string SetNodeTransform = "setNodeTransform"; // { node, trs: [위치3, 회전4(xyzw), 크기3] }  IR 부모 기준 로컬 (핸들로 옮긴 아이템)
        public const string SetAvatarTransform = "setAvatarTransform"; // { trs: [위치3, 회전4] }  아바타 전체의 자리 (장면 루트, 원본 = 원점에 정면)
        public const string SetExpression = "setExpression";   // { shapes: [ { renderer, shape, weight } ], layer?: "keys" }  표정 레이어 (layer=keys면 블렌드쉐이프 단축키 레이어, 가장 위) (빈 목록 = 없음, 사용자 블렌드쉐이프 값 위에 덮는 미리보기 전용)
        public const string FaceTracking = "faceTracking";     // { source: "off"|"webcam"|"vmc"|"osc"(Babble·VRCFT식 OSC, 기본 8888), port?, camera?, mirror?, smoothing?, gain? } | { lip: { on?, mic?, gain?, wav?(시험) } } 말하기 → 입 모양 | { mics } 마이크 목록 | { snapshot } | { devices } | { capture: true|false } | { calibrate } | { neutral: { 이름: 0~1 } } | { map: { add, off } } | { links } | { test: 이름|"" } · 옵션 head·move·body·arms·eye{sensitivity,sharp,link}·groups  페이셜 트래킹 켜고 끄기 · 지금 값 · 웹캠 목록 · 녹화 시작/끝 · 캘리브레이션(1.5초) · 캘리브레이션 기준 넣기 · 수동 연결 · 자동 연결 목록 요청 · 값 하나를 1로 시험(트래킹이 꺼져 있어도) → trackingStatus
        public const string Vr = "vr";                         // { enabled?: SteamVR에 붙기(백그라운드 앱, 끊기면 몇 초마다 다시), view?: VR로 보기(장면 앱으로 다시 붙어 눈마다 그려 보냄, SteamVR이 꺼져 있으면 켬 — enabled도 켬), launch?: false 시험용(SteamVR을 켜지 않음), resolution?: 눈 텍스처 = SteamVR 권장 × 0.5~1.5, desktop?: "eye"(PC 화면 = 왼눈) | "orbit"(미리보기 카메라), recenter?: true 관찰자 자리 다시 맞추기, status?: true 지금 상태를 다시 보내기 } → vrStatus  (2026-11-03 body·avatarSize·height·sizeTweak·fbt·pose·record는 뺌)
        public const string VrPanel = "vrPanel";               // { id, map?: 공유 메모리 이름, width?, height?: 픽셀(BGRA, 위에서 아래로), frame?: 새 화면 번호(→ vrPanelShown, "seq"는 파이프 번호라 못 씀), meters?: 너비 m, show?: bool, summon?: true 내 앞으로, beside?: true 메인 창 옆으로, state?: 저장해 둔 창 상태(vrPanelPlaced), remove?: true } · id "*": { open?: 에디터 창 열기·닫기, hand?: 빠른 설정 열기·닫기, faceMe?, freeze?: 트리거 반 당김 커서 멈춤, dump?: 시험용 그림 폴더 } · id "hand" = 빠른 설정 창  id "main" = 메인 창, 나머지 = 떠 있는 창  VR 에디터 창 (Host 창을 그린 화면을 SteamVR 오버레이로)
        public const string VrKeyboard = "vrKeyboard";         // { id, show?: true, text?: 지금 글, multiline?, password?, title? } | { id, hide: true }  VR 에디터 창의 입력 칸을 레이저로 눌렀을 때 SteamVR 키보드
        public const string Studio = "studio";                // { enabled?, background?: { mode: "none"|"panorama"|"image"|"gradient", path?, top?: [r,g,b], bottom?: [r,g,b] }, exposure?, rotation?, shadowFloor?: { enabled, mode: "shadow"|"solid", strength, color: [r,g,b] }, props?: [ { id, path: IR, pos: [x,y,z], rotY, scale, visible } ] (전체 목록), dof?: { enabled, strength 0~1, auto, distance m, show?: 초점 확인 화면 } }  스튜디오 (켜야 만든다) → studioState
        public const string Record = "record";                 // { folder, format: "png"|"raw", width, height, fps, seconds, mode: "turntable"|"fixed", turns?, to?: 카메라 상태(달리: 지금 자리 → to), transparent? } | { cancel: true }  녹화 (스튜디오를 켠 뒤) → recordFrame… recordDone
        public const string Guests = "guests";                 // { list?: [ { id, path: IR, pos: [x,y,z] 월드, rot?: [x,y,z,w] | rotY?: 도, scale, visible, pose?: setPose 형식 | null } ] (전체 목록), copyPose?: id 지금 아바타 자세를 그 아바타에 }  함께 세운 아바타 (자리·자세·PhysBone만, 편집은 지금 아바타만) → guestsState
        public const string Select = "select";               // { kind: "bone"|"item"|"avatar"|"", bone?, node?, screen?: [x, y] 화면 점 아래를 고르기(빈 곳 더블클릭과 같음) }  조작 모드의 선택 → selected
        public const string MoveTarget = "moveTarget";         // { arrow?: "x"|"y"|"z", amount?: m, ring?: "x"|"y"|"z", angle?: 도 }  고른 아이템·아바타를 핸들과 같은 경로로 (시험용) → nodeMoved / avatarMoved
        public const string PlaceItem = "placeItem";           // { node } 커서로 놓기 시작 (아이템 루트 노드) | { cancel: true }
        public const string SetMeshEdits = "setMeshEdits";     // { mesh, ops: [MeshOps 연산...] }  원본 메시에 ops를 차례로 적용한 결과로 바꾼다 (빈 목록 = 원본)
        public const string MeshTool = "meshTool";             // { enabled?, renderer?, action?: delete|merge|face|selectAll|selectNone|selectLinked, brush?: { radius, strength } }  뷰포트 정점 편집 모드 (선택은 렌더러의 임시 상태)
        public const string Capture = "capture";               // { path, width, height, transparent, supersample }
        public const string SetQuality = "setQuality";         // { maxFps?: 0 제한 없음 | -1 모니터 주사율(VSync) | n, studio?: bool 고화질(VRChat과 별개로 품질 제한을 푼다), ao?: 0~1 주변 가림 세기, captureAa?: 1~32 촬영·녹화 지터 누적 장 수, hidden?: bool 에디터 창 최소화 (프레임을 줄인다) }
        public const string ScreenShot = "screenShot";         // { path }  창에 보이는 그대로(핸들·방향 기즈모 포함) 다음 프레임 끝에 저장 → captureDone (시험용, -batchmode 아닐 때만)
        public const string AddTexture = "addTexture";         // { key, file, path, like }  IR에 없는 텍스처(색 굽기 결과)를 렌더러 장면 텍스처 목록에 더한다 (임포트 설정은 like 텍스처를 따름)
        public const string BakeColor = "bakeColor";           // { out, main: 파일, gradation?: 파일, mask?: 파일, color: [4], hsvg: [4], gradationStrength }  lilToon 색 굽기 (Hidden/ltsother_baker) → bakeDone
        public const string Quit = "quit";

        // ---- Unity → Host ----
        public const string Ready = "ready";                   // { protocolVersion, unityVersion, shaders[] }
        public const string SceneLoaded = "sceneLoaded";       // { materials[]: {guid, shader, supported, approx, approxReason}, schemas: {셰이더 이름: schema[]}, warnings[], bounds: { min, max }, physBones: { chains, bones, soft: 말랑 모드 대상 체인 수, legGuard: 다리 뚫림 막기 대상 체인 수 }, humanoid: 찾은 휴머노이드 뼈 수, pose: { ok, problem, rest: { 머슬 이름: 쉬는 자세 값 } } }
        public const string MaterialInfo = "materialInfo";     // 셰이더 교체 후 { guid, shader, supported, schema[] }
        public const string Key = "key";                       // { key, ctrl, shift, alt } (뷰포트 포커스 중 단축키 전달)
        public const string CameraChanged = "cameraChanged";   // { position, rotation, fov, pivot }
        public const string CaptureDone = "captureDone";       // { path, ok, error }
        public const string BakeDone = "bakeDone";             // { out, ok, error, width, height }
        public const string PhysBoneStats = "physBoneStats";   // { penetrations, bones, jitter, jitterMax, jitterWorst, swing, swingMax, tickMs } 콜라이더를 파고든 뼈 수 (setPhysBones { stats: true }의 답, 시험용)
        public const string PoseEdited = "poseEdited";         // { bone: 휴머노이드 이름, pose: { muscles, bodyRotation?, bodyHeight? } }  뷰포트에서 본을 돌리고 놓았을 때 (Host가 Addr.Pose에 기록)
        public const string PoseApplied = "poseApplied";       // { floorRest, floorNow, floorPart: "렌더러 / 본" }  setPose { report: true }의 답 (시험용: 불러온 자세·지금 자세의 가장 낮은 높이, 바닥 맞추기 확인)
        public const string PoseSelected = "poseSelected";     // { bone: 휴머노이드 이름 | "" }  뷰포트 본 조작에서 본을 고르거나 풀었을 때 (Host 자세 페이지의 "고른 본")
        public const string TrackingStatus = "trackingStatus"; // { source, ok, text, fps, face: 얼굴 찾음, mapped: 움직이는 블렌드쉐이프 수, mode: ft|sync|mmd|basic, ftLayers, values?: { ARKit 이름: 0~1 }, lip?: { on, ok, text, level, f1, f2, visemes }, mics?: [이름] }  트래킹 상태 (1초마다) · 고정 요청의 답(snapshot) · 녹화 결과(capture: { shapes: [{r, s}], times, values: [[0~100…]] }) · 캘리브레이션 결과(neutral: { 이름: 0~1, headYaw·headPitch·headRoll·headX·headY }) · 자동 연결(auto: [{ src, r, s }], sources: [쓸 수 있는 값 이름])
        public const string VrStatus = "vrStatus";             // { state: off|waiting|ok|error, text, wanted, view: 장면 앱으로 보는 중, wantView, desktop, fps?: 제출 프레임(10 단위), body?: 아바타 안, scale?: 방 크기(아바타 눈높이÷내 눈높이), avatarEye?, myEye?: m, measures?: "내 키 재기"로 잰 횟수(늘면 Host가 measuredHeight m를 설정에 저장), measuredHeight?, avatarSize?, resolution?, gpuMs?: 1초 평균 GPU, reprojected?: 재투영 %, dropped?: 놓친 프레임, bodyProblem?, inputProblem?, error?: 제출 실패, poseIn?: 자세 찍기까지 남은 초(올림), poseKind?, record?: off|countdown|on, recordIn?: 녹화 시작까지 남은 초, recordSeconds?: 녹화한 초, poseProblem?, devices: [ { index, kind: hmd|controller|tracker|base|other, role: left|right|"", model, maker, type, tracked } ] }  SteamVR 연결 상태 (바뀔 때만)
        public const string VrPanelShown = "vrPanelShown";     // { id, frame }  vrPanel 화면을 텍스처로 올림 (Host는 이걸 받고 다음 화면을 쓴다)
        public const string VrPanelPlaced = "vrPanelPlaced";   // { id, state: { pos, rot, scale, alpha, hidden, mode: world|wrist|head, localPos?, localRot? } }  VR 에디터 창 상태가 바뀜 (방 기준, Host가 창마다 저장해 vrPanel { state }로 돌려준다)
        public const string VrPanelsOpen = "vrPanelsOpen";     // { editor, hand }  VR 안에서 에디터 창(B/Y 짧게)·빠른 설정(B/Y 길게)이 열리고 닫힘 (Host는 닫힌 창을 그리지 않는다)
        public const string VrPanelAction = "vrPanelAction";   // { id, action: "close" }  VR 창 아래 막대에서 누른 것 (닫기 = 떠 있는 창을 작업 창에 다시 붙이기)
        public const string VrKeyboardText = "vrKeyboardText"; // { id, text: 키보드에 친 지금 글 전체, done?: 끝냄(Enter처럼 확정), cancel?: 닫음(처음 글로) }
        public const string VrCameraShot = "vrCameraShot";     // { path, ok, error?, width?, height? }  VR 카메라로 찍은 사진을 PNG로 씀 (vr { camera, cameraSummon, cameraFolder, cameraWidth, cameraHeight, cameraOpts: { video, lock, scale: 카메라 크기 0.5~3, timer, flip, lookAt, smooth, anchor: room|world|follow, grid, portrait, fps, stream, fov? }, cameraShutter?, cameraPipe?: 영상용 녹화 파이프(RecordJob.Pipe) })
        public const string VrCameraState = "vrCameraState";   // { on, video, held, hand, recording, seconds: 녹화한 초, countdown: 타이머 남은 초(올림, 0 = 없음), fov, shots, videos, error? }  VR 카메라 상태 (바뀔 때만 — 조작판 표시용)
        public const string VrCameraBookmark = "vrCameraBookmark"; // { slot, pos: [3], rot: [4] }  VR 카메라 자리 저장 (vr { cameraOpts: { bookmark: 칸 } }의 답, 월드 기준) — 불러오기는 vr { cameraGoto: { pos, rot } } (2026-10-27)
        public const string VrPropMoved = "vrPropMoved";       // { id, pos: [3], rotY, scale }  VR로 잡아 놓은 스튜디오 소품 (2026-11-01)
        public const string VrItemPlaced = "vrItemPlaced";    // { node, bone: 휴머노이드 이름, offset: [위치3, 회전4, 크기3] (그 뼈 기준) } | { node, trs } (같은 뼈면 옮기기만)  VR로 잡아 놓은 아이템 (vr { itemGrab }, 2026-10-27)
        public const string VrCameraVideo = "vrCameraVideo";  // { started } | { stopped, frames, seconds, ok, error? }  VR 카메라 영상 녹화 시작·끝 (MP4는 Host의 RecordJob이 파이프로 받아 마무리)
        public const string VrPointer = "vrPointer";           // { id, kind: move|down|up|leave|wheel, u, v: 창 안 0~1 (왼쪽 위 원점), delta?: 휠 칸 수 }  VR 레이저 → Host가 그 창에 마우스 입력으로
        public const string StudioState = "studioState";      // { enabled, background, width, height, error?, props: [ { id, ok, error?, renderers, size: [x,y,z] } ], focus: 지금 초점 거리 m }  studio의 답
        public const string RecordFrame = "recordFrame";       // { index, total, path }  녹화 프레임 하나를 썼을 때 (raw = 위에서 아래로 BGRA, 너비×높이×4 바이트)
        public const string RecordDone = "recordDone";         // { ok, frames, cancelled?, error? }
        public const string Selected = "selected";             // { kind: "bone"|"item"|"avatar"|"", bone?, node? }  조작 모드에서 고르거나 풀었을 때 (Host 도구 모음 "고른 것")
        public const string NodeMoved = "nodeMoved";           // { node, trs: [위치3, 회전4, 크기3] }  핸들로 아이템을 옮기고 놓았을 때 (Host가 Addr.NodeTransform에 기록)
        public const string AvatarMoved = "avatarMoved";       // { trs: [위치3, 회전4] }  핸들로 아바타 전체를 옮기고 놓았을 때 (Host가 Addr.AvatarTransform에 기록)
        public const string GuestsState = "guestsState";       // { guests: [ { id, ok, error?, renderers, humanoid, physBones, height?: m (크기 1 기준) } ] }  guests의 답
        public const string GuestMoved = "guestMoved";         // { id, pos: [x,y,z], rot: [x,y,z,w], scale }  함께 세운 아바타를 핸들·VR 그립으로 옮기고 놓았을 때 (Host가 목록에 저장)
        public const string GuestPoseEdited = "guestPoseEdited"; // { id, pose: setPose 형식 }  함께 세운 아바타의 본을 VR로 잡아 놓았거나 자세를 복사했을 때 (Host가 목록에 저장)
        public const string ItemPlaced = "itemPlaced";        // { node, bone: 휴머노이드 뼈 이름, offset: [위치3, 회전4, 크기3] (뼈 기준 로컬) } | { node, cancelled: true }
        public const string MeshEdited = "meshEdited";         // { mesh, op, label }  뷰포트 정점 편집으로 확정된 연산 하나 (Host가 Addr.MeshEdit 목록 끝에 붙여 기록)
        public const string MeshRebuilt = "meshRebuilt";       // { mesh, vertices, triangles }  setMeshEdits를 화면에 반영함 (정점 줄이기는 백그라운드에서 계산해 늦게 온다)
        public const string MeshSelection = "meshSelection";   // { renderer, mesh, count, vertices, triangles }  정점 편집 모드의 선택 상태 (표시용)
        public const string Notice = "notice";                 // { text } 뷰포트 조작 결과 안내 (상태 표시줄)
        public const string Log = "log";                       // { level, text }

        // 머티리얼 프로퍼티 값 종류
        public const string KindFloat = "float", KindColor = "color", KindVector = "vector", KindTexture = "texture", KindTexST = "texST";

        public static JNode Make(string type) => JNode.Obj().Set("t", type);
        public static string Type(JNode m) => m["t"].AsString;
    }

    /// <summary>셰이더 프로퍼티 스키마 한 줄 (Unity가 Shader API로 읽어 Host에 보냄).</summary>
    public sealed class ShaderPropInfo
    {
        public string Name = "", Description = "", Type = "Float"; // Float, Range, Color, Vector, Texture, Int
        public float Min, Max;
        public float[] Default = { 0, 0, 0, 0 };
        public string? DefaultTexture;
        public string TexDim = "2D";
        public List<string> Attributes = new List<string>();
        public bool Hidden, HDR, Normal, NoScaleOffset;

        public JNode ToJson()
        {
            var o = JNode.Obj().Set("name", Name).Set("desc", Description).Set("type", Type)
                .Set("min", Min).Set("max", Max).Set("default", JNode.FloatArray(Default));
            if (DefaultTexture != null) o.Set("defaultTex", DefaultTexture);
            if (Type == "Texture") o.Set("dim", TexDim);
            var a = JNode.Arr(); foreach (var s in Attributes) a.Add(s); o.Set("attrs", a);
            if (Hidden) o.Set("hidden", true);
            if (HDR) o.Set("hdr", true);
            if (Normal) o.Set("normal", true);
            if (NoScaleOffset) o.Set("noScaleOffset", true);
            return o;
        }

        public static ShaderPropInfo FromJson(JNode n)
        {
            var p = new ShaderPropInfo
            {
                Name = n["name"].AsString, Description = n["desc"].AsString, Type = n["type"].AsString,
                Min = n["min"].AsFloat, Max = n["max"].AsFloat, DefaultTexture = n["defaultTex"].AsStringOrNull,
                TexDim = n.Has("dim") ? n["dim"].AsString : "2D",
                Hidden = n["hidden"].AsBool, HDR = n["hdr"].AsBool, Normal = n["normal"].AsBool, NoScaleOffset = n["noScaleOffset"].AsBool,
            };
            if (n.Has("default")) p.Default = n["default"].ToFloatArray();
            foreach (var a in n["attrs"].Items) p.Attributes.Add(a.AsString);
            return p;
        }
    }
}
