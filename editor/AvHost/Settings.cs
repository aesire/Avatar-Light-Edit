// 화면 배치 설정 (작업 창 위치·폭, 사이드바 접힘, 3D 미리보기 보조 표시). %APPDATA%\ALEdit\avhost.json 에 저장한다.
// 문서 데이터와는 무관한 사용자 취향이라 세션 파일에 넣지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class Settings
    {
        public bool PaneOnRight;
        public double PaneWidth = 470;
        public bool SidebarCollapsed;
        public bool ExprAvatarOpen = true; // 표정 페이지의 "아바타 기본 표정" 펼침
        public bool ExpertMode; // 전문가 모드: 세부 설정까지 모두 보이기 (기본은 쉬운 모드)
        // 테마 (Theme.cs): 프리셋 키(system·dark·light·nord …·custom), 직접 고른 바탕·단추·강조 색 ("#RRGGBB", 빈 값 = 프리셋 그대로)
        public string ThemeKey = "system", ThemeBase = "", ThemeButton = "", ThemeAccent = "";
        // 화면 배치 (MainWindow.Layout.cs): classic = 둥근 카드 배치(기존) · clean = 이어 붙인 판 배치(새, Obsidian 비슷). 둘 다 두고 사용자가 고른다
        public string LayoutStyle = "clean";
        public bool FineRailOpen;
        public bool FineStripOpen;      // 렌더러 보기 아래 가로 세밀 조정 막대 + 입력 바 (FineStrip.cs). 기본 닫힘 (2026-10-23 사용자 요청, 예전 키 fineStrip은 읽지 않음)
        public JNode DialPresets = JNode.Obj();   // 떠 있는 다이얼 묶음 프리셋 { 이름: { pinned, blocks: [{kind, key, bar}] } } (DialFloat.cs)
        public string SliderStyle = "bar";   // 슬라이더 모양: bar(막대) · dial(다이얼, Dial.cs)
        public string DialColor = "neon";
        public string HardwareLook = "flat";   // 다이얼 모드 단추: flat(납작, 기본) · skeuo(입체 — 그라데이션·음영)
        public bool HardwareUnify;             // 다이얼 모드: 카드·입력 칸·콤보·체크 상자도 장비 모양으로 통일    // 다이얼·세밀 조정 하이라이트: neon(형광 노랑) · accent(강조색) · #RRGGBB   // 오른쪽 세밀 조정 막대 펼침 (FineRail.cs)
        public bool LayoutChosen;   // 설정 창에서 직접 고른 적이 있는지 (없으면 기본 = 플랫, 2026-10-22)
        // 블렌드쉐이프 즐겨찾기: { 메시(노드) 이름: [블렌드쉐이프 이름…] } — 같은 아바타를 다시 가져와도 이어진다
        public JNode ShapeFavorites = JNode.Obj();
        // 블렌드쉐이프 이름 보기 (화면 표시만, Host/Core/ShapeNameTr.cs): "ko" 한국어(기본) · "en" 영어 · "" 원본. 구글 번역 = 표에 없는 이름을 구글 번역으로 (MachineNames.cs, 기본 끔)
        public string ShapeNameLang = "ko";
        // 화면 언어 (Texts.cs, 2026-11-02): "" Windows 언어 따르기 · ko · en · ja
        public string UiLang = "";
        public bool ShapeNameGoogle;
        public string GoogleTranslateKey = "";   // 공식 Cloud Translation API 키 (2026-10-27, 사용자가 넣음)
        // 텍스처 디스크 캐시 (2026-10-23 로드 시간): "off" · "orig"(원본 설정대로 압축, 2026-11-02 기본) · "rgba"(압축 안 함) · "dxt"(모두 GPU 압축). 렌더러 TexCache.cs
        // 저장 열쇠는 "textureMode" — 예전 "textureCache"의 rgba는 그때 기본값이라 orig로 옮긴다.
        public string TextureCache = "orig";
        // 3D 미리보기 보조 표시 (렌더러에 setView로 보낸다)
        public bool ViewGizmo = true, ViewGrid = true, ViewBones, ViewMirror, ViewPhysBones;
        /// <summary>구도 안내선 (3분할, 촬영 크기의 비율 틀) — 미리보기에만 (2026-10-23)</summary>
        public bool ViewThirds;
        public bool ViewFloorFit = true; // 자세를 바꾸면 가장 낮은 곳을 불러온 자세의 바닥 높이에 맞춘다 (자세 페이지 "바닥에 맞추기")
        public bool ViewPoseBones; // 뷰포트 조작 모드 (켜 두면 왼쪽 드래그가 PhysBone 대신 휴머노이드 본·고른 아이템·아바타 핸들을 잡는다)
        public string ViewHandleSpace = "local"; // 핸들 축: local(고른 것 기준) | world
        public string ViewDraw = "shaded", ViewBackground = "env";
        // "아바타만" 카메라 (2026-10-27): 소품·바닥·함께 세운 아바타·스튜디오 배경을 빼고 고른 색으로 지운다 (촬영·녹화도). 켜짐은 기억하지 않고 색만
        public bool ViewAvatarOnly;
        public string AvatarOnlyColor = "#00FF00";
        public string ViewHandleStyle = "bold", ViewAxisStyle = "disc"; // 핸들 모양 line|bold|solid|soft · 방향 기즈모 disc|cube|mini
        public double ViewHandleSize = 1;
        public string ViewNav = "default"; // 뷰포트 카메라 조작 방식: default(드래그 공전·WSAD 회전) | unity(Unity 씬 뷰처럼: 오른쪽 드래그+WASD 날기, Alt+드래그 공전)
        public bool PhysBones = true; // PhysBone 미리보기 (렌더러에 setPhysBones로 보낸다)
        public string PhysBoneSoft = "off"; // 말랑 모드 프리셋 (off · light · normal · smooth)
        public bool VrSmoothTurn = false;   // 부드러운 회전 (끄면 스냅 30°)
        public bool VrBoneGrab = true;      // 아바타 밖에서 그립으로 관절을 잡아 자세 다듬기
        public bool VrControllers = true;   // VR에 손 공 (컨트롤러 끝 = 잡는 점)
        // 본 잡기 설정 (Avatar Pose System 참고, 2026-10-25): 다른 관절은 제자리 · 관절 잠금(아바타째만) · 손잡이 크기 배율(1 보통 · 0.6 가늘게 · 0.35 더 가늘게)
        public bool VrBonePinOthers = true;
        public bool VrBoneLock = false;
        public double VrBoneHandle = 1;
        public double VrResolution = 1;     // VR 눈 텍스처 = SteamVR 권장 × 이 값 (0.5~1.5)
        public bool VrMirror = false, VrPersonalMirror = false;   // VR 거울: 월드 거울(아바타 정면) · 개인 거울(내 앞, 잡아서 옮김)
        public bool VrCamera = false;          // VR 카메라 (손에 드는 카메라, 그립으로 잡고 트리거로 찍기)
        public bool VrCamera4K = false;        // VR 카메라 사진 크기: 3840×2160 (끄면 1920×1080)
        public string VrCameraFolder = "";     // VR 카메라 사진 폴더 ("" = 사진/ALEdit VR)
        // VR 카메라 조작판 (2026-10-25, VRChat 카메라처럼)
        public bool VrCameraVideo = false;     // 영상 모드 (끄면 사진)
        public bool VrCameraLock = false;      // 고정: 그 자리(월드)에 박혀 잡히지 않음
        public float VrCameraScale = 1;        // 카메라 크기 (몸체·화면 함께, 0.5~3)
        public int VrCameraTimer = 0;          // 타이머 초 (0·3·5·10)
        public bool VrCameraFlip = false;      // 셀카 (렌즈를 화면 쪽으로)
        public bool VrCameraLookAt = false;    // 나를 보기 (렌즈가 늘 내 머리를)
        public int VrCameraSmooth = 0;         // 손떨림 보정 0~3
        public string VrCameraAnchor = "room"; // 놓으면: room(나와 함께) | world(월드에 고정) | follow(나를 따라옴)
        public bool VrCameraGrid = false;      // 화면 3분할 격자
        public bool VrCameraPortrait = false;  // 세로로 찍기
        public int VrCameraFps = 30;           // 영상 초당 장 수 (30·60)
        public bool VrCameraStream = false;    // 스트림: 카메라 화면을 미리보기 프로그램 창(데스크톱)에
        public double VrCameraDof = 0;          // 피사계 심도 세기 0~1 (0 = 끔, 초점은 아바타 머리 자동) — 2026-10-27
        public bool VrCameraTransparent = false; // 투명 배경 사진 (영상은 그대로)
        public bool VrCameraAdjust = false;     // 찍은 사진에 스튜디오 보정(밝기·대비·채도)을 넣기
        public JNode VrCameraMarks = JNode.Arr(); // 카메라 자리 저장 3칸 [{ pos, rot } | null]
        public bool VrItemGrab = true;          // 아바타 밖에서 그립으로 붙인 악세서리 잡아 옮기기 (2026-10-27)
        public bool VrItemSnap = false, VrItemSurface = false, VrItemMirror = false;   // 잡아 옮길 때 1 cm·15° 스냅 · 놓을 때 표면에 붙이기 · 짝 아이템 좌우 대칭 (2026-11-01)
        public bool VrCameraMic = false;        // VR 카메라 영상에 마이크 소리 (2026-11-01, 마이크는 말하기와 같은 LipMic)
        /// <summary>렌더러 vr { cameraOpts }와 저장에 같이 쓰는 꼴 (줌은 빼고 — 줌은 VR 안에서 바꾼 값이 이긴다).</summary>
        public JNode VrCameraOpts() => JNode.Obj().Set("video", VrCameraVideo).Set("lock", VrCameraLock).Set("scale", VrCameraScale).Set("timer", VrCameraTimer).Set("flip", VrCameraFlip).Set("lookAt", VrCameraLookAt)
            .Set("smooth", VrCameraSmooth).Set("anchor", VrCameraAnchor).Set("grid", VrCameraGrid).Set("portrait", VrCameraPortrait).Set("fps", VrCameraFps).Set("stream", VrCameraStream)
            .Set("dof", (float)VrCameraDof).Set("transparent", VrCameraTransparent).Set("mic", VrCameraMic).Set("micName", LipMic);
        public double VrMirrorQuality = 0.75;  // 거울 반사 텍스처 = 눈 텍스처 × 이 값 (0.25~1)
        public bool VrPanels = true;        // VR 에디터 창: VR로 보기 중 에디터 창을 헤드셋 안에 띄우고 레이저로 누르기 (PLAN 3.12)
        public double VrUiScale = 1.5;      // VR 중 에디터 창 글자·단추 배율 (1~2, 데스크톱 창도 함께 커진다)
        public double VrPanelMeters = 1.0;  // VR 안 에디터 창 너비 m (0.5~2.5)
        public bool VrFoldPreview = true;   // VR 중 메인 창의 3D 미리보기 자리를 접기 (VR에서는 장면을 직접 본다)
        public JNode VrPanelPlaces = JNode.Obj();   // VR 에디터 창 상태 (창 id → { pos, rot, scale, alpha, hidden, mode, localPos?, localRot? }, SteamVR 방 기준) — 렌더러가 알려 준다
        public bool VrPanelFaceMe = true;   // VR 창을 잡았다 놓으면 나를 보게 (끄면 기울기만 없앰)
        public bool VrOverlayPopups = true; // 팝업(드롭다운 목록·메뉴·툴팁)을 창 안에 그리기 — VR 에디터 창·창 공유에 보이게 (시작할 때만 적용)
        public bool VrTriggerFreeze = true; // VR 레이저: 트리거를 반쯤 당기면 커서가 멈춘다 (누를 때 떨림 없이)
        public string VrDesktop = "eye";    // VR로 보기 중 PC 화면: eye(헤드셋 왼눈, VRChat처럼) · orbit(미리보기 카메라)
        public bool PhysBoneLegGuard = true; // 다리 뚫림 막기 (에디터 전용 다리 콜라이더)
        public double PhysBoneWeight = 4;  // 무게감 (1.5 = 가볍게 · 4 = 보통 · 10 = 무겁게)
        public JNode PhysBoneFeel = JNode.Obj();  // 느낌 값 { 열쇠: 값 } (PhysBoneFeel.Items, 없는 열쇠 = 기본)
        // 스튜디오: 저장한 카메라 위치 [{ name, state }] (최대 9개, Alt+1~9), 마지막 배경·촬영 크기
        public JNode Cameras = JNode.Arr();
        public string StudioBgMode = "none", StudioBgPath = "";
        /// <summary>그라데이션 배경의 위·아래 색 (#RRGGBB), 그림자 바닥 (2026-10-23)</summary>
        public string StudioBgTop = "#BFD4F2", StudioBgBottom = "#F2E6D8";
        /// <summary>단색 배경 색 (2026-11-03, StudioBgMode "solid" — 렌더러에는 위·아래가 같은 그라데이션으로 보낸다)</summary>
        public string StudioBgColor = "#FFFFFF";
        // 바닥 (2026-10-27): "none" | "shadow"(그림자만, 투명 촬영용) | "solid"(단색 바닥, 기본). 스튜디오 페이지를 보는 동안만 보인다
        public string StudioFloor = "solid", StudioFloorColor = "#CFCFD4";
        public double ShotShadowStrength = 0.6;
        public double StudioExposure = 1, StudioRotation = 0;
        public int ShotWidth = 3840, ShotHeight = 2160;
        public bool ShotTransparent;
        // 촬영 PNG에 세션 넣기 (StudioPanel 촬영 카드)
        public bool ShotEmbedSession = true;
        // 촬영 보정 (밝기 −1~1, 대비·채도 0~2) · 스티커 만들기 (규격 키, 테두리 두께 px, 테두리 색 white/black/none)
        public double ShotBrightness, ShotContrast = 1, ShotSaturation = 1;
        public string StickerSpec = "line", StickerOutlineColor = "white";
        public int StickerOutline = 6;
        // 스튜디오 2차: 소품 [{ id, name, path(IR), pos, rotY, scale, visible }], 심도 { enabled, strength, auto, distance }, 녹화 { mode, seconds, fps, size, format, turns, transparent }
        public JNode StudioProps = JNode.Arr(), StudioDof = JNode.Obj(), StudioRecord = JNode.Obj();
        // 함께 세운 아바타 (2026-10-25): [{ id, name, path(IR), pos(월드), rot[x,y,z,w], scale, visible, pose(setPose 형식 | 없음) }] — 실행 취소 없이 여기에 기억
        public JNode Guests = JNode.Arr();
        // 렌더 품질 (2026-10-23, 렌더러 setQuality): 프레임 제한(0 = 없음, -1 = 모니터 주사율) · 스튜디오 고화질(스튜디오 페이지·방송 오버레이 동안) · 주변 가림 세기 · 촬영 지터 누적 장 수
        public int RenderMaxFps = 60;
        public bool StudioQuality = true;
        public double StudioAo = 0.5;
        public int StudioCaptureAa = 8;

        // 온라인 업데이트 (Updater.cs): GitHub 저장소 "owner/name", 시작할 때 확인, 마지막 확인 시각(UTC ISO), 건너뛴 버전 태그
        public string UpdateRepo = Updater.DefaultRepo;
        public bool UpdateAuto = true;
        public string UpdateLastCheck = "", UpdateSkip = "";
        // 방송 오버레이 (OverlayController.cs): 자리(br·bl·tr·tl·center·custom), 크기(화면 높이에 대한 비율), 클릭 통과(끄면 조정 모드), 맨 위
        //   OverlayX·Y: 마지막 자리의 아래 가운데 점(물리 픽셀). custom이면 그 자리, 모서리면 그 점이 든 모니터의 모서리 (0,0 = 아직 없음 → 프로그램 창의 모니터)
        public string OverlayCorner = "br";
        public double OverlaySize = 0.45;
        public bool OverlayClickThrough = true, OverlayTopmost = true;
        public int OverlayX, OverlayY;
        public string OverlayLighting = "topfill";   // 방송 조명 배치 프리셋 (OverlayController.Layouts)
        public double OverlaySupersample = 1;   // 방송 창 슈퍼샘플링 배율 (1 = 끔, 1.5, 2)
        // (2026-10-24) 켜 있는 동안 프레임 제한(0 = 평소 제한) · 창 비율(너비/높이) · 오버레이 전용 카메라(마지막으로 끌 때의 카메라, 없으면 미리보기 그대로) · 전역 단축키
        public int OverlayFps = 30;
        public double OverlayAspect = 0.75;
        public JNode OverlayCamera = JNode.Null;
        public string OverlayKeyToggle = "Ctrl+Alt+O", OverlayKeyAdjust = "Ctrl+Alt+P";
        // 컨트롤 보드 (OverlayBoard.cs): 맨 위에 고정 · 방송 화면에서 숨기기 · 오버레이를 켜면 같이 열기 · 창 자리(물리 픽셀, 없으면 화면 가운데 오른쪽)
        public bool BoardPinned = true, BoardHideFromCapture = true, BoardAutoOpen = true;
        // 보드를 열면 에디터 창을 숨긴다 (보드의 "에디터 열기"로 다시)
        public bool BoardHideEditor = true;
        // 떠 있는 창·다이얼 창을 에디터 창 안(자식 창)에 넣기 — 디스코드·OBS의 창 공유에도 함께 찍힌다 (Embed.cs). 끄면 예전처럼 따로 뜬 창
        public bool EmbedFloats = true;
        public string LastFile = "";           // 마지막으로 연·저장한 파일 (세션이면 세션, 아니면 IR) — 켤 때 다시 연다
        public bool OpenLastOnStart = true;    // 설정 "켤 때 마지막 파일 열기"
        public List<string> RecentFiles = new List<string>();     // 최근 연·저장한 파일 (새것 먼저, 최대 RecentMax) — 시작 화면·열기 메뉴·Ctrl+K
        public const int RecentMax = 8;
        public List<string> RecentCommands = new List<string>();  // Ctrl+K에서 최근 고른 명령 이름 (새것 먼저) — 빈 칸일 때 맨 위에
        public JNode ImportChoices = JNode.Obj();                 // 가져오기 창 선택 { 원본 경로(소문자): { root, colorVariants } } — 같은 아바타를 다시 가져올 때 채움
        public JNode UploadThumbs = JNode.Obj();                  // 올리기 썸네일로 고른 사진 { 아바타 열쇠(Upload.AvatarKey): 파일 경로, "" = 안 고름 }
        public int BoardX = int.MinValue, BoardY = int.MinValue;
        // 보드 꾸미기: 구역 순서(구역 id) · 숨긴 구역·단추("sec:구역", "구역:이름") · 한 줄 단추 수(1~3)
        public List<string> BoardOrder = new List<string>();
        public HashSet<string> BoardHidden = new HashSet<string>();
        public int BoardColumns = 2;
        // 페이셜 트래킹 캘리브레이션 기준 (FaceTracker 보정, 머리 위치 headX·headY 포함): { 소스: { ARKit 이름: 0~1 } }. 사람마다 다르고 아바타와는 상관없어 여기 둔다
        public JNode FaceNeutral = JNode.Obj();
        // 페이셜 트래킹 부위별 감도 { eye, gaze, brow, mouth, cheek, head: 0~2 } · 머리 회전 따라가기
        public JNode FaceGroups = JNode.Obj();
        public bool FaceHead = true;
        // 눈 감기 곡선 { sensitivity, sharp: 0~1, link } · 몸도 따라 움직이기 · 팔 자연스럽게 내리기 (트래킹 중)
        public JNode FaceEye = JNode.Obj();
        public double FaceSmoothing = 0.5;   // 페이셜 트래킹 부드럽게 0~1
        public bool FaceFill = true;          // 끊김 메우기 (트래킹 fps가 낮을 때 결과 사이를 이어 붙임)
        public bool FaceBody = true, FaceArms = true, FaceMove = true;   // FaceMove: 머리 위치 따라가기 (2026-10-24)
        // 말하기 → 입 모양 (립싱크): 마이크 이름("" = 기본) · 감도 0.25~4. 켜고 끄기는 저장하지 않는다 (켤 때마다 마이크를 바로 열지 않게)
        public string LipMic = "";
        public double LipGain = 1;
        public int OscPort = 8888;   // 트래커 앱(Babble·VRCFT식 OSC) 받을 포트

        /// <summary>설정 묶음 이름 ("" = 평소, "test" = 테스트 모드). 첫 Load 전에 정한다.</summary>
        public static string Profile = "";
        /// <summary>설정 파일이 든 폴더 (설정 창 "폴더 열기").</summary>
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALEdit", Profile);
        public static string FilePath => Path.Combine(Folder, "avhost.json");

        /// 예전 무게감 프리셋(1 · 2.5 · 5)을 지금 프리셋(1.5 · 4 · 10)으로 (2026-10-20 더 무겁게 바꿈)
        static double OldWeight(double w) => w == 1 ? 1.5 : w == 2.5 ? 4 : w == 5 ? 10 : w;

        /// <summary>블렌드쉐이프 이름 보기 기본값: 화면 언어를 따른다 (일본어는 원본 — 이름이 대개 일본어라서)</summary>
        public static string DefaultShapeNameLang(string uiLang) => Texts.Resolve(uiLang) switch { "ko" => "ko", "en" => "en", _ => "" };

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.ShapeNameLang = DefaultShapeNameLang(""); return s; }
                var j = JNode.Parse(File.ReadAllText(FilePath));
                if (j.Has("paneOnRight")) s.PaneOnRight = j["paneOnRight"].AsBool;
                if (j.Has("embedFloats")) s.EmbedFloats = j["embedFloats"].AsBool;
                s.LastFile = j["lastFile"].AsString;
                if (j.Has("openLast")) s.OpenLastOnStart = j["openLast"].AsBool;
                if (j["recentFiles"].Kind == JKind.Array) s.RecentFiles = j["recentFiles"].Items.Select(x => x.AsString).Where(x => x.Length > 0).Take(RecentMax).ToList();
                if (j["recentCommands"].Kind == JKind.Array) s.RecentCommands = j["recentCommands"].Items.Select(x => x.AsString).Where(x => x.Length > 0).Take(5).ToList();
                if (j["importChoices"].Kind == JKind.Object) s.ImportChoices = j["importChoices"].Clone();
                if (j["uploadThumbs"].Kind == JKind.Object) s.UploadThumbs = j["uploadThumbs"].Clone();
                if (j.Has("paneWidth")) s.PaneWidth = Math.Clamp(j["paneWidth"].AsDouble, 360, 1200);
                if (j.Has("sidebarCollapsed")) s.SidebarCollapsed = j["sidebarCollapsed"].AsBool;
                if (j.Has("expertMode")) s.ExpertMode = j["expertMode"].AsBool;
                if (j["theme"].Kind == JKind.Object)
                {
                    var t = j["theme"];
                    s.ThemeKey = t["key"].AsString.Length > 0 ? t["key"].AsString : "system";
                    s.ThemeBase = t["base"].AsString; s.ThemeButton = t["button"].AsString; s.ThemeAccent = t["accent"].AsString;
                }
                if (j["shapeFavorites"].Kind == JKind.Object) s.ShapeFavorites = j["shapeFavorites"];
                if (j["uiLang"].Kind == JKind.String && j["uiLang"].AsString is "" or "ko" or "en" or "ja") s.UiLang = j["uiLang"].AsString;
                if (j["shapeNameLang"].Kind == JKind.String && j["shapeNameLang"].AsString is "" or "ko" or "en") s.ShapeNameLang = j["shapeNameLang"].AsString;
                else if (j.Has("shapeNamesKo") && !j["shapeNamesKo"].AsBool) s.ShapeNameLang = "";   // 예전 "한국어 이름" 끔
                else s.ShapeNameLang = DefaultShapeNameLang(s.UiLang);
                s.ShapeNameGoogle = j["shapeNameGoogle"].AsBool;
                s.GoogleTranslateKey = j["googleTranslateKey"].AsString;
                var tc = j.Has("textureMode") ? j["textureMode"].AsString : j["textureCache"].AsString;
                s.TextureCache = tc == "off" || tc == "dxt" || tc == "rgba" && j.Has("textureMode") ? tc : "orig";
                s.LayoutChosen = j["layoutChosen"].AsBool;
                s.FineRailOpen = j["fineRail"].AsBool;
                s.FineStripOpen = j["fineStripOpen"].AsBool;
                if (j["dialPresets"].Kind == JKind.Object) s.DialPresets = j["dialPresets"];
                if (j["sliderStyle"].AsString is "bar" or "dial") s.SliderStyle = j["sliderStyle"].AsString;
                if (j["dialColor"].AsString.Length > 0) s.DialColor = j["dialColor"].AsString;
                if (j["hardwareLook"].AsString is "flat" or "skeuo") s.HardwareLook = j["hardwareLook"].AsString;
                s.HardwareUnify = j["hardwareUnify"].AsBool;
                if (s.LayoutChosen && j["layoutStyle"].AsString is "classic" or "clean") s.LayoutStyle = j["layoutStyle"].AsString;
                if (j.Has("exprAvatarOpen")) s.ExprAvatarOpen = j["exprAvatarOpen"].AsBool;
                var v = j["view"];
                if (v.Has("gizmo")) s.ViewGizmo = v["gizmo"].AsBool;
                if (v.Has("grid")) s.ViewGrid = v["grid"].AsBool;
                if (v.Has("bones")) s.ViewBones = v["bones"].AsBool;
                if (v.Has("mirror")) s.ViewMirror = v["mirror"].AsBool;
                if (v.Has("thirds")) s.ViewThirds = v["thirds"].AsBool;
                if (v.Has("draw")) s.ViewDraw = v["draw"].AsString;
                if (v.Has("background")) s.ViewBackground = v["background"].AsString;
                if (v["avatarOnly"].Has("hex")) s.AvatarOnlyColor = v["avatarOnly"]["hex"].AsString;
                if (v.Has("physBoneGizmos")) s.ViewPhysBones = v["physBoneGizmos"].AsBool;
                if (v.Has("poseBones")) s.ViewPoseBones = v["poseBones"].AsBool;
                if (v["handleStyle"].AsString is "line" or "bold" or "solid" or "soft") s.ViewHandleStyle = v["handleStyle"].AsString;
                if (v["axisStyle"].AsString is "disc" or "cube" or "mini") s.ViewAxisStyle = v["axisStyle"].AsString;
                if (v["nav"].AsString is "default" or "unity") s.ViewNav = v["nav"].AsString;
                if (v.Has("handleSize")) s.ViewHandleSize = Math.Clamp(v["handleSize"].AsDouble, 0.5, 2);
                if (v.Has("floorFit")) s.ViewFloorFit = v["floorFit"].AsBool;
                if (v.Has("handleSpace")) s.ViewHandleSpace = v["handleSpace"].AsString == "world" ? "world" : "local";
                if (j.Has("physBones")) s.PhysBones = j["physBones"].AsBool;
                if (j.Has("vrMirror")) s.VrMirror = j["vrMirror"].AsBool;
                if (j.Has("vrPersonalMirror")) s.VrPersonalMirror = j["vrPersonalMirror"].AsBool;
                if (j.Has("vrCamera")) s.VrCamera = j["vrCamera"].AsBool;
                if (j.Has("vrCamera4K")) s.VrCamera4K = j["vrCamera4K"].AsBool;
                if (j.Has("vrCameraFolder")) s.VrCameraFolder = j["vrCameraFolder"].AsString;
                if (j["vrCameraOpts"].Kind == JKind.Object)
                {
                    var o = j["vrCameraOpts"];
                    if (o.Has("video")) s.VrCameraVideo = o["video"].AsBool;
                    if (o.Has("lock")) s.VrCameraLock = o["lock"].AsBool;
                    if (o.Has("scale")) s.VrCameraScale = Math.Clamp(o["scale"].AsFloat, 0.5f, 3f);
                    if (o.Has("timer")) s.VrCameraTimer = Math.Clamp(o["timer"].AsInt, 0, 30);
                    if (o.Has("flip")) s.VrCameraFlip = o["flip"].AsBool;
                    if (o.Has("lookAt")) s.VrCameraLookAt = o["lookAt"].AsBool;
                    if (o.Has("smooth")) s.VrCameraSmooth = Math.Clamp(o["smooth"].AsInt, 0, 3);
                    if (o.Has("anchor")) s.VrCameraAnchor = o["anchor"].AsString is "world" or "follow" ? o["anchor"].AsString : "room";
                    if (o.Has("grid")) s.VrCameraGrid = o["grid"].AsBool;
                    if (o.Has("portrait")) s.VrCameraPortrait = o["portrait"].AsBool;
                    if (o.Has("fps")) s.VrCameraFps = o["fps"].AsInt >= 45 ? 60 : 30;
                    if (o.Has("stream")) s.VrCameraStream = o["stream"].AsBool;
                    if (o.Has("dof")) s.VrCameraDof = Math.Clamp(o["dof"].AsDouble, 0, 1);
                    if (o.Has("transparent")) s.VrCameraTransparent = o["transparent"].AsBool;
                    if (o.Has("mic")) s.VrCameraMic = o["mic"].AsBool;
                }
                if (j.Has("vrMirrorQuality")) s.VrMirrorQuality = Math.Clamp(j["vrMirrorQuality"].AsDouble, 0.25, 1);
                // 미세 조정이 없던 설정의 "원래 크기"(옛 기본값)는 새 기본값(내 키에 맞추기)으로 — 사용자: 아바타가 내 몸보다 작아 보임 (2026-10-23)
                if (j.Has("vrSmoothTurn")) s.VrSmoothTurn = j["vrSmoothTurn"].AsBool;
                if (j.Has("vrBoneGrab")) s.VrBoneGrab = j["vrBoneGrab"].AsBool;
                if (j.Has("vrItemGrab")) s.VrItemGrab = j["vrItemGrab"].AsBool;
                if (j.Has("vrItemSnap")) s.VrItemSnap = j["vrItemSnap"].AsBool;
                if (j.Has("vrItemSurface")) s.VrItemSurface = j["vrItemSurface"].AsBool;
                if (j.Has("vrItemMirror")) s.VrItemMirror = j["vrItemMirror"].AsBool;
                if (j.Has("vrCameraAdjust")) s.VrCameraAdjust = j["vrCameraAdjust"].AsBool;
                if (j["vrCameraMarks"].Kind == JKind.Array) s.VrCameraMarks = j["vrCameraMarks"].Clone();
                if (j.Has("vrControllers")) s.VrControllers = j["vrControllers"].AsBool;
                if (j.Has("vrBonePinOthers")) s.VrBonePinOthers = j["vrBonePinOthers"].AsBool;
                if (j.Has("vrBoneLock")) s.VrBoneLock = j["vrBoneLock"].AsBool;
                if (j.Has("vrBoneHandle")) s.VrBoneHandle = Math.Clamp(j["vrBoneHandle"].AsDouble, 0.2, 2);
                if (j["guests"].Kind == JKind.Array) s.Guests = j["guests"].Clone();
                if (j.Has("vrResolution")) s.VrResolution = Math.Clamp(j["vrResolution"].AsDouble, 0.5, 1.5);
                if (j.Has("vrPanels")) s.VrPanels = j["vrPanels"].AsBool;
                if (j.Has("vrUiScale")) s.VrUiScale = Math.Clamp(j["vrUiScale"].AsDouble, 1, 2);
                if (j.Has("vrPanelMeters")) s.VrPanelMeters = Math.Clamp(j["vrPanelMeters"].AsDouble, 0.5, 2.5);
                if (j.Has("vrFoldPreview")) s.VrFoldPreview = j["vrFoldPreview"].AsBool;
                if (j["vrPanelPlaces"].Kind == JKind.Object) s.VrPanelPlaces = j["vrPanelPlaces"].Clone();
                if (j.Has("vrPanelFaceMe")) s.VrPanelFaceMe = j["vrPanelFaceMe"].AsBool;
                if (j.Has("vrOverlayPopups")) s.VrOverlayPopups = j["vrOverlayPopups"].AsBool;
                if (j.Has("vrTriggerFreeze")) s.VrTriggerFreeze = j["vrTriggerFreeze"].AsBool;
                if (j.Has("vrDesktop")) s.VrDesktop = j["vrDesktop"].AsString == "orbit" ? "orbit" : "eye";
                if (j.Has("physBoneSoft")) s.PhysBoneSoft = j["physBoneSoft"].AsString switch { "marshmallow" => "normal", "bouncy" => "smooth", var id => id };   // 2026-10-22 이름 바꿈
                if (j.Has("physBoneLegGuard")) s.PhysBoneLegGuard = j["physBoneLegGuard"].AsBool;
                if (j.Has("physBoneWeight")) s.PhysBoneWeight = OldWeight(j["physBoneWeight"].AsDouble);
                if (j["physBoneFeel"].Kind == JKind.Object) s.PhysBoneFeel = j["physBoneFeel"].Clone();
                var st = j["studio"];
                if (st["cameras"].Kind == JKind.Array) s.Cameras = st["cameras"].Clone();
                if (st.Has("bgMode")) s.StudioBgMode = st["bgMode"].AsString;
                if (st.Has("bgPath")) s.StudioBgPath = st["bgPath"].AsString;
                if (st.Has("bgTop")) s.StudioBgTop = st["bgTop"].AsString;
                if (st.Has("bgBottom")) s.StudioBgBottom = st["bgBottom"].AsString;
                if (st.Has("bgColor")) s.StudioBgColor = st["bgColor"].AsString;
                if (st.Has("floor")) s.StudioFloor = st["floor"].AsString is "none" or "shadow" or "solid" ? st["floor"].AsString : "solid";
                else if (st.Has("shadowFloor")) s.StudioFloor = st["shadowFloor"].AsBool ? "shadow" : "solid";   // 예전 설정: 그림자 바닥 체크
                if (st.Has("floorColor")) s.StudioFloorColor = st["floorColor"].AsString;
                if (st.Has("shadowStrength")) s.ShotShadowStrength = Math.Clamp(st["shadowStrength"].AsDouble, 0, 1);
                if (st.Has("exposure")) s.StudioExposure = st["exposure"].AsDouble;
                if (st.Has("rotation")) s.StudioRotation = st["rotation"].AsDouble;
                if (st.Has("shotWidth")) s.ShotWidth = Math.Clamp(st["shotWidth"].AsInt, 64, 8192);
                if (st.Has("shotHeight")) s.ShotHeight = Math.Clamp(st["shotHeight"].AsInt, 64, 8192);
                if (st.Has("shotTransparent")) s.ShotTransparent = st["shotTransparent"].AsBool;
                if (st.Has("embedSession")) s.ShotEmbedSession = st["embedSession"].AsBool;
                if (st.Has("shotBrightness")) s.ShotBrightness = Math.Clamp(st["shotBrightness"].AsDouble, -1, 1);
                if (st.Has("shotContrast")) s.ShotContrast = Math.Clamp(st["shotContrast"].AsDouble, 0, 2);
                if (st.Has("shotSaturation")) s.ShotSaturation = Math.Clamp(st["shotSaturation"].AsDouble, 0, 2);
                if (st.Has("stickerSpec")) s.StickerSpec = st["stickerSpec"].AsString;
                if (st.Has("stickerOutline")) s.StickerOutline = Math.Clamp(st["stickerOutline"].AsInt, 0, 30);
                if (st.Has("stickerOutlineColor")) s.StickerOutlineColor = st["stickerOutlineColor"].AsString;
                if (st["props"].Kind == JKind.Array) s.StudioProps = st["props"].Clone();
                if (st["dof"].Kind == JKind.Object) s.StudioDof = st["dof"].Clone();
                if (st["record"].Kind == JKind.Object) s.StudioRecord = st["record"].Clone();
                if (st.Has("quality")) s.StudioQuality = st["quality"].AsBool;
                if (st.Has("ao")) s.StudioAo = Math.Clamp(st["ao"].AsDouble, 0, 1);
                if (st.Has("captureAa")) s.StudioCaptureAa = Math.Clamp(st["captureAa"].AsInt, 1, 32);
                if (j["render"].Has("maxFps")) s.RenderMaxFps = Math.Clamp(j["render"]["maxFps"].AsInt, -1, 1000);
                var ov = j["overlay"];
                if (ov.Has("corner")) s.OverlayCorner = ov["corner"].AsString;
                if (ov.Has("size")) s.OverlaySize = Math.Clamp(ov["size"].AsDouble, 0.15, 1);
                if (ov.Has("clickThrough")) s.OverlayClickThrough = ov["clickThrough"].AsBool;
                if (ov.Has("topmost")) s.OverlayTopmost = ov["topmost"].AsBool;
                if (ov.Has("x")) s.OverlayX = ov["x"].AsInt;
                if (ov.Has("y")) s.OverlayY = ov["y"].AsInt;
                if (ov.Has("lighting")) s.OverlayLighting = ov["lighting"].AsString;
                if (ov.Has("supersample")) s.OverlaySupersample = Math.Clamp(ov["supersample"].AsDouble, 1, 2);
                if (ov.Has("fps")) s.OverlayFps = Math.Clamp(ov["fps"].AsInt, 0, 240);
                if (ov.Has("aspect")) s.OverlayAspect = Math.Clamp(ov["aspect"].AsDouble, 0.2, 5);
                if (ov["camera"].Kind == JKind.Object) s.OverlayCamera = ov["camera"].Clone();
                if (ov.Has("keyToggle")) s.OverlayKeyToggle = ov["keyToggle"].AsString;
                if (ov.Has("keyAdjust")) s.OverlayKeyAdjust = ov["keyAdjust"].AsString;
                var bd = j["board"];
                if (bd.Has("pinned")) s.BoardPinned = bd["pinned"].AsBool;
                if (bd.Has("hideFromCapture")) s.BoardHideFromCapture = bd["hideFromCapture"].AsBool;
                if (bd.Has("autoOpen")) s.BoardAutoOpen = bd["autoOpen"].AsBool;
                if (bd.Has("hideEditor")) s.BoardHideEditor = bd["hideEditor"].AsBool;
                if (bd.Has("x") && bd.Has("y")) { s.BoardX = bd["x"].AsInt; s.BoardY = bd["y"].AsInt; }
                if (bd["order"].Kind == JKind.Array) s.BoardOrder = bd["order"].Items.Select(x => x.AsString).ToList();
                if (bd["hidden"].Kind == JKind.Array) s.BoardHidden = new HashSet<string>(bd["hidden"].Items.Select(x => x.AsString));
                if (bd.Has("columns")) s.BoardColumns = Math.Clamp(bd["columns"].AsInt, 1, 3);
                var up = j["update"];
                if (up.Has("repo") && up["repo"].AsString.Length > 0) s.UpdateRepo = up["repo"].AsString;   // 빈 값이면 앱 폴더의 저장소 (Updater.DefaultRepo)
                if (up.Has("auto")) s.UpdateAuto = up["auto"].AsBool;
                if (up.Has("lastCheck")) s.UpdateLastCheck = up["lastCheck"].AsString;
                if (up.Has("skip")) s.UpdateSkip = up["skip"].AsString;
                if (j["faceNeutral"].Kind == JKind.Object) s.FaceNeutral = j["faceNeutral"].Clone();
                if (j["faceGroups"].Kind == JKind.Object) s.FaceGroups = j["faceGroups"].Clone();
                if (j.Has("faceHead")) s.FaceHead = j["faceHead"].AsBool;
                if (j["faceEye"].Kind == JKind.Object) s.FaceEye = j["faceEye"].Clone();
                if (j.Has("faceSmoothing")) s.FaceSmoothing = Math.Clamp(j["faceSmoothing"].AsDouble, 0, 1);
                if (j.Has("faceFill")) s.FaceFill = j["faceFill"].AsBool;
                if (j.Has("faceBody")) s.FaceBody = j["faceBody"].AsBool;
                if (j.Has("faceArms")) s.FaceArms = j["faceArms"].AsBool;
                if (j.Has("faceMove")) s.FaceMove = j["faceMove"].AsBool;
                if (j.Has("lipMic")) s.LipMic = j["lipMic"].AsString;
                if (j.Has("lipGain")) s.LipGain = Math.Clamp(j["lipGain"].AsDouble, 0.25, 4);
                if (j.Has("oscPort")) s.OscPort = Math.Clamp(j["oscPort"].AsInt, 1024, 65535);
            }
            catch (Exception e)
            {
                // 깨진 설정 파일은 기본값으로 — 다음 저장이 덮어쓰기 전에 옆에 남겨 두고 알린다 (MainWindow.SetupSafety)
                s = new Settings();
                try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
                LoadError = e.Message;
            }
            return s;
        }

        /// <summary>설정 파일을 읽지 못해 기본값으로 시작했으면 그 이유 (깨진 파일은 avhost.json.broken으로 남김)</summary>
        public static string? LoadError;

        /// <summary>setView 메시지 본문과 같은 모양.</summary>
        public JNode ViewMessage() => JNode.Obj().Set("gizmo", ViewGizmo).Set("grid", ViewGrid).Set("bones", ViewBones)
            .Set("mirror", ViewMirror).Set("draw", ViewDraw).Set("background", ViewBackground).Set("physBoneGizmos", ViewPhysBones).Set("poseBones", ViewPoseBones).Set("handleSpace", ViewHandleSpace).Set("floorFit", ViewFloorFit)
            .Set("handleStyle", ViewHandleStyle).Set("handleSize", ViewHandleSize).Set("axisStyle", ViewAxisStyle).Set("nav", ViewNav)
            .Set("thirds", ViewThirds).Set("frameAspect", ShotHeight > 0 ? (double)ShotWidth / ShotHeight : 0)
            .Set("avatarOnly", JNode.Obj().Set("on", ViewAvatarOnly).Set("color", JNode.FloatArray(HexColor(AvatarOnlyColor) ?? new[] { 0f, 1f, 0f })).Set("hex", AvatarOnlyColor));

        /// "#RRGGBB" → [r, g, b] 0~1 (감마 값), 아니면 null
        public static float[]? HexColor(string? s)
        {
            var t = (s ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
            return new[] { ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f };
        }

        JNode BoardMessage()
        {
            var b = JNode.Obj().Set("pinned", BoardPinned).Set("hideFromCapture", BoardHideFromCapture).Set("autoOpen", BoardAutoOpen).Set("hideEditor", BoardHideEditor);
            if (BoardX != int.MinValue) b.Set("x", BoardX).Set("y", BoardY);
            var order = JNode.Arr(); foreach (var o in BoardOrder) order.Add(o);
            var hidden = JNode.Arr(); foreach (var h in BoardHidden.OrderBy(x => x)) hidden.Add(h);
            b.Set("order", order).Set("hidden", hidden).Set("columns", BoardColumns);
            return b;
        }

        /// <summary>--ui-shots: 시험이 사용자 설정 파일을 덮어쓰지 않게 저장하지 않는다</summary>
        public static bool ReadOnly;

        public void Save()
        {
            if (ReadOnly) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var j = JNode.Obj().Set("paneOnRight", PaneOnRight).Set("embedFloats", EmbedFloats).Set("lastFile", LastFile).Set("openLast", OpenLastOnStart).Set("paneWidth", Math.Round(PaneWidth)).Set("sidebarCollapsed", SidebarCollapsed).Set("expertMode", ExpertMode).Set("exprAvatarOpen", ExprAvatarOpen)
                    .Set("theme", JNode.Obj().Set("key", ThemeKey).Set("base", ThemeBase).Set("button", ThemeButton).Set("accent", ThemeAccent)).Set("shapeFavorites", ShapeFavorites).Set("shapeNameLang", ShapeNameLang).Set("uiLang", UiLang).Set("shapeNameGoogle", ShapeNameGoogle).Set("googleTranslateKey", GoogleTranslateKey).Set("textureMode", TextureCache).Set("layoutStyle", LayoutStyle).Set("layoutChosen", LayoutChosen).Set("fineRail", FineRailOpen).Set("fineStripOpen", FineStripOpen).Set("dialPresets", DialPresets).Set("sliderStyle", SliderStyle).Set("dialColor", DialColor).Set("hardwareLook", HardwareLook).Set("hardwareUnify", HardwareUnify)
                    .Set("view", ViewMessage()).Set("physBones", PhysBones).Set("physBoneSoft", PhysBoneSoft).Set("vrDesktop", VrDesktop).Set("vrPanels", VrPanels).Set("vrUiScale", VrUiScale).Set("vrPanelMeters", VrPanelMeters).Set("vrFoldPreview", VrFoldPreview).Set("vrPanelPlaces", VrPanelPlaces).Set("vrPanelFaceMe", VrPanelFaceMe).Set("vrOverlayPopups", VrOverlayPopups).Set("vrTriggerFreeze", VrTriggerFreeze).Set("vrResolution", VrResolution).Set("vrMirror", VrMirror).Set("vrPersonalMirror", VrPersonalMirror).Set("vrMirrorQuality", VrMirrorQuality).Set("vrCamera", VrCamera).Set("vrCamera4K", VrCamera4K).Set("vrCameraFolder", VrCameraFolder).Set("vrCameraOpts", VrCameraOpts()).Set("vrSmoothTurn", VrSmoothTurn).Set("vrBoneGrab", VrBoneGrab).Set("vrItemGrab", VrItemGrab).Set("vrItemSnap", VrItemSnap).Set("vrItemSurface", VrItemSurface).Set("vrItemMirror", VrItemMirror).Set("vrCameraAdjust", VrCameraAdjust).Set("vrCameraMarks", VrCameraMarks).Set("vrControllers", VrControllers).Set("vrBonePinOthers", VrBonePinOthers).Set("vrBoneLock", VrBoneLock).Set("vrBoneHandle", VrBoneHandle).Set("guests", Guests).Set("physBoneLegGuard", PhysBoneLegGuard).Set("physBoneWeight", PhysBoneWeight).Set("physBoneFeel", PhysBoneFeel)
                    .Set("studio", JNode.Obj().Set("cameras", Cameras).Set("bgMode", StudioBgMode).Set("bgPath", StudioBgPath).Set("bgTop", StudioBgTop).Set("bgBottom", StudioBgBottom).Set("bgColor", StudioBgColor).Set("floor", StudioFloor).Set("floorColor", StudioFloorColor).Set("shadowStrength", ShotShadowStrength).Set("exposure", StudioExposure).Set("rotation", StudioRotation)
                        .Set("shotWidth", ShotWidth).Set("shotHeight", ShotHeight).Set("shotTransparent", ShotTransparent).Set("embedSession", ShotEmbedSession)
                        .Set("shotBrightness", ShotBrightness).Set("shotContrast", ShotContrast).Set("shotSaturation", ShotSaturation)
                        .Set("stickerSpec", StickerSpec).Set("stickerOutline", StickerOutline).Set("stickerOutlineColor", StickerOutlineColor)
                        .Set("props", StudioProps).Set("dof", StudioDof).Set("record", StudioRecord)
                        .Set("quality", StudioQuality).Set("ao", StudioAo).Set("captureAa", StudioCaptureAa))
                    .Set("render", JNode.Obj().Set("maxFps", RenderMaxFps))
                    .Set("overlay", JNode.Obj().Set("corner", OverlayCorner).Set("size", OverlaySize).Set("clickThrough", OverlayClickThrough).Set("topmost", OverlayTopmost).Set("x", OverlayX).Set("y", OverlayY).Set("supersample", OverlaySupersample).Set("lighting", OverlayLighting)
                        .Set("fps", OverlayFps).Set("aspect", OverlayAspect).Set("camera", OverlayCamera).Set("keyToggle", OverlayKeyToggle).Set("keyAdjust", OverlayKeyAdjust))
                    .Set("board", BoardMessage())
                    .Set("update", JNode.Obj().Set("repo", UpdateRepo).Set("auto", UpdateAuto).Set("lastCheck", UpdateLastCheck).Set("skip", UpdateSkip))
                    .Set("faceNeutral", FaceNeutral).Set("faceGroups", FaceGroups).Set("faceHead", FaceHead)
                    .Set("faceEye", FaceEye).Set("faceSmoothing", FaceSmoothing).Set("faceFill", FaceFill).Set("faceBody", FaceBody).Set("faceArms", FaceArms).Set("faceMove", FaceMove)
                    .Set("lipMic", LipMic).Set("lipGain", LipGain).Set("oscPort", OscPort);
                var recent = JNode.Arr(); foreach (var f in RecentFiles) recent.Add(f);
                var cmds = JNode.Arr(); foreach (var c in RecentCommands) cmds.Add(c);
                j.Set("recentFiles", recent).Set("recentCommands", cmds).Set("importChoices", ImportChoices).Set("uploadThumbs", UploadThumbs);
                File.WriteAllText(FilePath, j.ToJson(indent: true));
            }
            catch { } // 저장 실패해도 편집에는 지장 없음
        }
    }
}
