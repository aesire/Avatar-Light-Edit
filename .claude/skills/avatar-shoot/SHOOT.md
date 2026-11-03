# 창 없는 촬영 도구 (Shoot)

장면 JSON 하나로 포즈·표정·조명·환경·소품·카메라·촬영을 한다. Claude 같은 자동화가 쓰라고 만들었다. 에디터와 별개의 창 없는 렌더러를 쓰므로 켜 둔 에디터 화면에는 영향이 없다.

```bash
dotnet bin/Shoot.dll avatars                      # 가져온 아바타 목록
dotnet bin/Shoot.dll inspect <아바타>              # 렌더러·표정·뼈·쓸 수 있는 자세
dotnet bin/Shoot.dll inspect <아바타> --shapes blink   # 블렌드쉐이프 이름 찾기 (글 없이 쓰면 전부)
dotnet bin/Shoot.dll run <장면.json> --draft      # 시안: 긴 변 960, 빠르게
dotnet bin/Shoot.dll run <장면.json>              # 완성본
dotnet bin/Shoot.dll import <파일> [--list] [--root 이름]  # 패키지·zip·프리팹·씬·FBX → IR (소품·아바타, 답의 path를 장면에)
dotnet bin/Shoot.dll overlay on <장면.json> [--only 장]    # 방송 오버레이: 화면 위에 아바타를 띄운다
dotnet bin/Shoot.dll overlay off                  # 끈다 (status로 자리 확인)
dotnet bin/Shoot.dll edit <장면.json> [--only 장] [--test] [--no-open]  # 그 장의 상태로 에디터를 연다 (사람이 이어 고치기)
dotnet bin/Shoot.dll schema                       # 조명·후처리 값 전체 (이름·범위·보기)
dotnet bin/Shoot.dll devices                      # 웹캠·마이크 이름 (장치를 켜지는 않는다)
dotnet bin/Shoot.dll facelinks <아바타>            # 트래킹 값 → 블렌드쉐이프 자동 연결
dotnet bin/Shoot.dll cameras                      # 에디터에 저장한 카메라 위치
dotnet bin/Shoot.dll look <아바타|세션>            # 지금 값 보기 (아이템·옷·머티리얼·블렌드쉐이프·바꾼 값)
dotnet bin/Shoot.dll apply <편집.json>            # 머티리얼·블렌드쉐이프·옷·아이템 등을 고쳐 세션으로 (../avatar-edit/EDIT.md)
dotnet bin/Shoot.dll status                       # 서버·렌더러 상태
dotnet bin/Shoot.dll stop                         # 렌더러 끝내기 (20분 쉬면 저절로 끝난다)
```

`editor` 폴더에서 실행한다. 빌드: `sh build.sh shoot` (Windows: `build.cmd shoot`). 빌드 스크립트는 띄워 둔 서버를 먼저 끝낸다.

`run` 옵션: `--draft` · `--only 장이름,장이름` · `--out 폴더` · `--session`(장마다 `.alesav`도 저장, 에디터에서 이어 고치기).

## 빠른 이유

처음 `run`은 렌더러를 띄우고 아바타를 올린다 (아바타 A 약 5~10초). 그 뒤로는 서버가 렌더러를 들고 있어 바뀐 값만 보낸다. 포즈가 바뀌면 PhysBone이 자리 잡는 시간(기본 0.8초)을 더해 장당 약 1초, 포즈가 같으면 시안 0.1초, 1200×1600 완성본도 2초 안쪽이다.

## 답

JSON 한 덩이로 답한다. `ok`, `shots[].path`, `shots[].camera`(최종 카메라 — 그대로 `camera.pivot`·`distance`에 넣어 고칠 수 있다), `sheet`(2장 이상이면 `_sheet.png`, 한눈에 비교), `warnings`, `seconds`. 틀린 이름은 찍기 전에 `errors`로 비슷한 이름과 함께 알린다. 실패하면 종료 코드 1.

출력 폴더 기본값은 `<장면 폴더>/shots/<장면 이름>/`, 시안은 그 아래 `draft/`. 같은 이름은 덮어쓴다.

## 장면 파일

적지 않은 항목은 기본값으로 돌아간다 (앞 장면이 남지 않는다). 세션을 `avatar`로 주면 적지 않은 포즈·표정·조명은 세션 값을 쓰고 옷·머티리얼 편집도 함께 올라간다. 상대 경로는 장면 파일 기준. `brief`와 `_`로 시작하는 항목은 메모로, 읽지 않는다.

```json
{
  "avatar": "C:/…/아바타 A.ir.json",
  "brief": "노을 프로필. 살짝 아래에서, 역광, 윙크.",
  "size": "1200x1600",
  "lighting": { "preset": "sunset", "yaw": 150, "front": 1.4, "shadowSoftness": 0.6 },
  "lights": [ { "type": "point", "color": "#FFD0A8", "intensity": 1.5, "around": 150, "height": 1.3, "distance": 1 } ],
  "post": { "bloom": 0.25, "tonemap": "neutral", "vignette": 0.25 },
  "background": { "top": "#FF9E7A", "bottom": "#5B4B8A" },
  "floor": { "mode": "shadow", "strength": 0.5 },
  "pose": "peace_cheek",
  "expression": { "shapes": { "eye_blink_1_R": 100 } },
  "camera": { "preset": "upper", "angle": -15, "pitch": -5, "fov": 24 },
  "adjust": { "saturation": 1.1 },
  "shots": [
    { "name": "profile" },
    { "name": "full", "size": "portrait", "camera": { "preset": "full", "angle": 20, "pitch": 3 }, "pose": "contrapposto" },
    { "name": "cutout", "transparent": true, "floor": "none" }
  ]
}
```

`shots`의 각 장은 위 값에 합쳐진다. 객체는 안쪽까지 합치고 배열·값은 장의 것이 이긴다. `shots`가 없으면 한 장이다.

| 항목 | 값 |
|---|---|
| `size` | `1080x1920` 또는 `landscape`(1920×1080, 기본) · `portrait` · `square` · `thumbnail`(1280×720) · `4k` · `4k-portrait`. `width`·`height`로 덮어쓰기 |
| `transparent` | 투명 배경 PNG. 후처리는 블룸만 빠진다 |
| `quality` | `{ "studio": true, "ao": 0~1 (0.5), "aa": 1~32 (8) }` 고화질·주변 가림·지터 누적 장 수. 시안은 aa 1 |
| `supersample` | 1~4. 기본 400만 픽셀 이하 2, 넘으면 1 |
| `adjust` | 찍은 뒤 보정 `{ "brightness": -1~1, "contrast": 0~2, "saturation": 0~2 }` |
| `pose` | 아래 "자세" |
| `expression` | 아바타 표정 이름 · `.anim` · `"none"` · `{ "name", "anim", "time", "shapes": { 이름: 0~100 }, "renderer" }`. 이름 끝의 값이 위에 덮인다 |
| `hide` · `show` | 렌더러 이름 목록 (이번 장만) |
| `place` | 아바타 자리 `{ "pos": [x,y,z], "yaw": 도 }` |
| `lighting` | 프리셋·배치 이름 또는 값 객체. 프리셋: `bright` · `indoor` · `sunset` · `night` · `dark` · `flat` · `none`. 배치(`layout`): `topfill` · `front` · `butterfly` · `side45` · `rim` (주광 + 채움광, 다른 값은 그 위에 덮는다). 값: `intensity` `yaw` `pitch` `sunColor` `front` `shadows` `shadowStrength` `shadowSoftness` `accessoryShadows` `ambient` `ambientColor` `reflection` `bgCustom` `bgColor` |
| `lights` | 추가 조명 8개까지 `{ "type": point·spot, "color", "intensity", "range", "around": 둘레 각도(0 = 정면), "height", "distance", "aim", "angle", "follow" }` |
| `look` | `"broadcast"` 방송 화질: 배치(없으면 `topfill`) + 반사·블룸·노출·대비·채도. `post`가 그 위에 덮인다. 밝은 배경에서는 하얗게 날아가기 쉽다 |
| `post` | 후처리 `bloom`(적지 않으면 0.6) `bloomThreshold` `bloomScatter` `bloomTint` `tonemap`(none·neutral·aces) `exposure` `temperature` `tint` `contrast` `saturation` `vignette` |
| `background` | `"#색"` · 이미지 경로(`.hdr`이면 파노라마) · `{ "mode", "path", "top", "bottom", "exposure", "rotation" }` |
| `floor` | `none`(기본) · `shadow` · `solid` · `{ "mode", "color", "strength" }` |
| `avatarOnly` | 아바타만 단색 위에 `"#00FF00"` · `true`. 크로마키에는 `post`를 쓰지 않는다 |
| `props` | 소품 `{ "path": x.ir.json, "pos", "rotY", "scale", "visible" }`. FBX는 에디터 소품 추가로 먼저 IR을 만든다 |
| `guests` | 함께 세울 아바타 `{ "path", "pos", "yaw", "scale", "pose" }` |
| `dof` | `true` · 세기 0~1 · `{ "strength"(0.25), "distance": m(적으면 수동 초점), "show": true(초점 영역 보기: 앞 빨강·뒤 파랑) }`. 초점은 기본 아바타 머리. 얼굴 클로즈업은 0.1부터 |
| `camera` | 아래 "카메라" |
| `physBones` | `true`(기본) · `false` · `{ "settle": 초 }` 포즈를 바꾼 뒤 자리 잡는 시간 |
| `record` | 사진 대신 영상 `{ "seconds": 6, "fps": 30, "motion": turntable(기본)·fixed·in·out, "to": 끝 카메라(위 `camera` 형식, 장의 카메라에 합친다), "turns": 1, "format": mp4·png }`. `lip`이 켜져 있으면 소리도 담는다 (png는 `audio.wav`) |
| `sticker` | 사진 대신 스티커: 규격 이름 · `{ "spec", "outline": 0~30 (6), "color": white·black·none }`. 규격 `line` `lineMain` `lineTab` `kakao` `discordSticker` `discordEmoji` `twitch`(112·56·28) `telegram`. 답의 `stickers[]`에 용량, 제한을 넘으면 `overLimitKB`. `background`·`floor`는 빼고 찍는다 |
| `studioPreset` | 에디터 스튜디오 세팅 프리셋: 경로 또는 저장한 이름. 그 값 위에 장면 값이 덮인다 (배경·바닥·소품·심도·화질·아바타만·투명·보정·크기) |
| `saveStudioPreset` | 이 장의 스튜디오 값을 `.studio.json`으로 저장 (에디터에서 불러올 수 있다) |
| `embedSession` | `true`면 PNG에 세션을 넣는다 (에디터에 끌어 놓으면 같은 상태로) |
| `saveExpression` | 이 장의 표정을 `.anim`으로 저장. 다음 `run`부터 `expression`에 그 경로를 쓸 수 있다 |
| `tracking` | 아래 "트래킹" |
| `lip` | 말하기 → 입: 소리 파일 경로 · `{ "wav" 또는 "mic": 이름, "gain": 0.25~4 }`. 소리는 또렷한 모음·말소리여야 입이 잘 움직인다 |
| `faceMap` | 트래킹 값 연결 고치기 `{ "add": [ { "src", "shape", "k": 0~2 } ], "off": [ { "src", "shape" } ] }`. `src`는 `eyeBlinkLeft` `jawOpen` 등 (`facelinks`) |
| `shapeKeys` | 블렌드쉐이프 단축키: 메뉴 이름(`inspect`의 `menuShapes`, 켠다) · `{ "name", "menu"?, "shapes": { 이름: 값 · { "on", "off" } }, "mode": toggle·cycle, "steps", "group", "key": "Ctrl+Alt+B", "level": 0 정의만·1 켬·cycle 단계 }`. 같은 `group`은 하나만 켜진다. 정의는 세션에 저장 |
| `expressionKeys` | 표정 단축키 `{ "F1": 표정(위 `expression` 형식), … "F9" }`. 세션에 저장 (`--session`·`embedSession`) |
| `overlay` | `overlay on`의 값. 아래 "오버레이" |
| `raw` | 렌더러 메시지 목록 (`editor/Protocol/Messages.cs`). 찍기 직전에 보낸다. 쓴 뒤 다음 `run`은 아바타를 다시 올린다 |

색은 `"#RRGGBB"` 또는 `[r, g, b]`(0~1).

### 카메라

| 값 | 뜻 |
|---|---|
| `preset` | `full`(기본) · `upper` · `face`. 사진 비율·지금 자세에 맞춰 잡는다 |
| `bone`, `size` | 뼈 하나에 맞춤 (`RightHand`, size m). 뼈 이름은 `inspect`의 `bones` |
| `angle` | 0 = 정면, + = 아바타 오른쪽에서, 180 = 뒤 |
| `pitch` | + = 위에서 내려다봄, − = 아래에서 올려다봄 |
| `zoom` | 2 = 거리 절반. `distance`로 직접(m) |
| `up` · `right` | 화면 기준으로 보는 점을 옮긴다 (m) |
| `pivot` | 보는 점 `[x,y,z]` 직접 |
| `fov` | 세로 화각 (기본 30). 인물은 20~30, 넓게는 45~. 바꿔도 크기는 프리셋이 다시 맞춘다 |
| `ortho` | 직교 |
| `saved` | 에디터에 저장한 위치 (이름 또는 번호). 방향까지 그대로 쓰고 `zoom`·`pitch` 등만 고친다 |
| `state` | 답의 `camera` 값을 그대로 (`pivot` `yaw` `pitch` `distance` `fov` `ortho`). 같은 구도를 다시 쓸 때 |

### 트래킹

```json
"tracking": { "source": "webcam", "camera": "Integrated Camera", "wait": 4, "mirror": true, "smoothing": 0.5, "fill": true, "gain": 1,
  "head": true, "move": false, "body": false, "arms": false, "eye": { "sensitivity": 0.5, "sharp": 0.5, "link": true }, "groups": { "입": 1 },
  "calibrate": true, "freeze": true, "recordAnim": { "path": "face.anim", "seconds": 5 }, "test": "jawOpen" }
```

| 값 | 뜻 |
|---|---|
| `source` | `off` · `webcam`(`camera`) · `vmc`(`port` 39539) · `osc`(`port` 8888) · `image`(`path`, 사진 한 장을 웹캠처럼) |
| `wait` | 얼굴·값을 기다리는 초 (기본 4). 답의 `shots[].tracking`에 상태 |
| `calibrate` | 힘 뺀 얼굴을 기준으로 1.5초 재서 답의 `neutral`로. 다음엔 `tracking.neutral`에 넣는다 |
| `freeze` | 지금 얼굴을 표정으로 고정하고 트래킹을 끈다 |
| `recordAnim` | 움직이는 얼굴을 `.anim`으로 저장 |
| `test` | 트래킹 없이 값 하나를 1로 시험 |

웹캠·마이크는 사용자 허락 없이 켜지 않는다. 시험은 `osc`·`vmc`로 값을 보내거나 `lip.wav`로 한다.

### 오버레이

`overlay on`은 창 있는 렌더러로 바꿔(아바타를 다시 올림) 게임·바탕 화면 위에 아바타만 투명하게 띄운다. 끌 때까지 남고 20분 규칙도 적용되지 않는다. 켠 동안의 `run`은 같은 창에 바로 보인다 (포즈·표정을 방송 중에 바꾸기). 사용자 화면에 보이므로 시험할 때 미리 말한다.

`"overlay": { "corner": br·bl·tr·tl·center, "pos": [x, y](아래 가운데 점, 물리 픽셀), "monitor": 0(주 모니터), "size": 0.15~1 (0.45, 화면 높이 비율), "aspect": "3:4" 또는 숫자, "top": true, "fps": 30, "supersample": 1~2, "adjust": false(true면 끌어 옮기기·✕) }`

### 자세

이름: `rest`(불러온 그대로) · 프리셋 `tpose` `apose` `attention` `relaxed` `wave` `hips` · 함께 넣은 자세(`editor/Tools/Shoot/poses`) `contrapposto` `peace_cheek` `sit_floor` `sit_chair` `hands_front` · 에디터에 저장한 자세 이름 · `.anim`·`.pose.json` 경로.

조립:

```json
{ "base": "relaxed", "time": 0.5,
  "muscles": { "Head Turn Left-Right": 0.3 },
  "add": { "Spine Front-Back": 0.1 },
  "hands": { "right": "peace", "left": 0.4 },
  "mirror": "flip",
  "bodyRotation": [0, 20, 0], "bodyHeight": 0.9, "floorFit": true }
```

`base`가 없으면 불러온 자세 위에 쌓는다. `muscles`는 그 값으로, `add`는 지금 값에 더한다. `hands`는 `open` `relax` `fist` `peace` `point` `thumbsUp` 또는 쥐기 0~1. `mirror`는 `flip` `leftToRight` `rightToLeft`. `floorFit`(기본 켬)은 가장 낮은 곳을 바닥에 맞춘다. `time`은 `.anim` 바탕의 시각.

머슬 95개 이름은 `editor/Host/Core/HumanPose.cs`의 `Muscles`. 아바타 A에서 확인한 값:

| 머슬 | 값 |
|---|---|
| `Upper Leg Front-Back` | 0.6 선 다리, −0.5 의자에 앉은 다리, −1 다리를 앞으로 뻗음 |
| `Lower Leg Stretch` | 1 편 무릎, −0.2 의자에 앉은 무릎 |
| `Upper Leg In-Out` | − 가 안쪽 (다리 꼬기 −0.4) |
| `Upper Leg Twist In-Out` | −0.9면 무릎은 안쪽, 발끝은 바깥으로 (꼰 다리의 발을 바깥으로 빼기) |

손을 턱 옆으로 올리기: `Arm Down-Up` −0.35, `Arm Front-Back` 0, `Arm Twist` 0.55, `Forearm Stretch` −0.7. `Arm Front-Back`을 앞으로 보낼수록 두 손이 얼굴 앞에서 겹친다.

| `Arm Down-Up` | 0.4 수평, 1 위로, −0.5 내린 팔 |
| `Arm Front-Back` | **− 가 앞**: −0.7 앞으로 뻗음, 0.3 몸 옆, 1 뒤로 |
| `Arm Twist In-Out` | 1 굽힌 아래팔이 위로, −1 아래로 |
| `Forearm Stretch` | 1 편 팔, −0.9 끝까지 굽힘 |
| `Head Nod Down-Up` | **− 가 숙임**: −0.5 숙임, +0.8 위를 봄 |
| `Head Tilt Left-Right` | + 아바타 오른쪽으로 기울임 |
| `Spine Front-Back`·`Chest Front-Back` | **− 가 앞**: −0.5 앞으로 숙임 |
| `Arm Twist` + 앞으로 뻗은 팔 | −0.5 + `Forearm Stretch` −0.3: 아래팔이 안쪽으로 모인다 (무릎 안기) |

바닥에 앉기: `bodyRotation` [−20, 0, 0]으로 몸을 뒤로 눕히고 척추를 숙이면 엉덩이가 바닥 쪽으로 간다. `floorFit`이 발·꼬리를 기준으로 맞추면 뜰 수 있으니 `place.pos`의 y로 내린다. 앉은 포즈는 치마 속이 보이지 않는 각도(옆 −65° 근처)로 찍는다.

좌우가 있는 머슬(`Left …`·`Right …`)은 값이 같으면 대칭이다. 머슬 값은 아바타 체형에 따라 결과가 달라지므로 시안으로 확인한다.

## 스튜디오 탭과 대응

| 탭 | 장면·명령 |
|---|---|
| 배경·소품 | `background` `floor` `props` (`import`) |
| 카메라·촬영 | `avatarOnly` `camera`(`saved`) `dof` `quality` `size` `transparent` `embedSession` `adjust` |
| 스티커 | `sticker` |
| 녹화 | `record` |
| 표정·트래킹 | `expression` `saveExpression` `expressionKeys` `tracking` `lip` `faceMap` (`devices` `facelinks`) |
| 단축키 | `shapeKeys` |
| 오버레이 | `overlay on·off·status` `overlay` `lighting.layout` `look` |

## 안 되는 것

- 켜져 있는 에디터 화면을 직접 조종하지는 않는다. 사람에게 넘길 때는 `edit`로 새 창을 연다. 포즈·표정·조명·자리·카메라·켜고 끈 렌더러는 세션 값으로, 배경·바닥·소품·심도·화질·보정은 세션의 `studio`로 넘어가 에디터 설정에 들어간다 (평소 모드면 사용자의 지난 배경 설정을 덮어쓴다. 시험은 `--test`). 트래킹·말하기·오버레이는 넘어가지 않는다. 에디터가 다시 저장한 세션에는 `studio`가 없다 (설정에는 남는다).
- 에디터 설정(`avhost.json`)에 쓰지 않는다: 카메라 위치 저장은 장면의 `camera.state`로 대신한다. 에디터 저장 위치는 읽기만 한다.
- 사람이 누르는 것은 없다: 전역 단축키 실제 등록(`key`는 세션에 적어 둘 뿐, 에디터에서 그 세션을 열면 쓰인다), 컨트롤 보드, 오버레이 끌어 옮기기(`adjust`로 사람이 할 수는 있다). 대신 값으로 바로 정한다.

## 숨김 원칙

사람에게 이 기능이 있다는 것이 보이지 않게 한다 (2026-11-01 사용자 요청). Claude만 쓴다.

- 앱 화면에 넣지 않는다. 배포 zip에는 넣되(2026-11-02 사용자 결정) 사람이 읽는 곳에 적지 않고, AI가 부탁받았을 때 찾아 쓴다: 프로그램 폴더의 `Shoot.exe`·`AGENTS.md`·`CLAUDE.md`·`SHOOT.md`, 설정 폴더의 `AGENTS.md`(프로그램 위치). 해석 코드는 `Host/Core`가 아니라 `Tools/Shoot`에 둔다 — `Host/Core`는 에디터 dll에 함께 컴파일된다.
- `README`·`CHANGELOG`·`CLAUDE.md`에 적지 않는다. 변경 기록은 아래에 적는다.
- 스킬은 `user-invocable: false`로 `/` 메뉴에서 뺀다.

## 작업 기록

### 2026-11-01

- **장면 JSON 하나로 촬영** ([Shoot.cs](../../../editor/Tools/Shoot/Shoot.cs), [ShootServer.cs](../../../editor/Tools/Shoot/ShootServer.cs)): `dotnet bin/Shoot.dll run 장면.json [--draft]`. 포즈(프리셋·조립·`.anim`)·표정·렌더러 켜고 끄기·아바타 자리·조명·추가 조명·후처리·배경·바닥·소품·함께 세운 아바타·심도·카메라·보정·투명·크로마키·턴테이블 영상을 적고, `shots`로 여러 장을 한 번에 찍어 `_sheet.png`로 비교한다. 에디터와 별개의 창 없는 렌더러를 쓴다.
- **빠르게**: 렌더러를 든 서버를 알아서 띄워 두고 바뀐 값만 보낸다. 아바타 A 기준 처음 약 5~10초(아바타 올리기), 그 뒤 포즈를 바꾼 장 약 1초, 포즈가 같은 시안 0.1초. 20분 쉬면 끝나고, 빌드 스크립트가 먼저 끝낸다 ([build.sh](../../../editor/build.sh), [build_renderer.sh](../../../editor/build_renderer.sh)).
- **틀린 이름은 찍기 전에**: 렌더러는 모르는 값을 조용히 넘기므로 도구가 항목·머슬·블렌드쉐이프·표정·뼈·렌더러·조명 값을 미리 검사해 비슷한 이름과 함께 알린다 ([SceneSpec.cs](../../../editor/Tools/Shoot/SceneSpec.cs), 테스트 [SceneSpecTests.cs](../../../editor/Tests/SceneSpecTests.cs)).
- **에디터로 넘기기**: `--session`이면 장마다 세션도 저장한다 (포즈·표정·조명·자리·카메라). 배경·소품·심도는 에디터 설정에 있어 담기지 않는다.
- **avatar-shoot 스킬** ([SKILL.md](SKILL.md)): 기획 → 시안 → 고치기 → 완성본 순서와 볼 것. 시험한 자세 5개를 함께 넣었다 ([poses](../../../editor/Tools/Shoot/poses)) — `contrapposto` `peace_cheek` `sit_floor` `sit_chair` `hands_front`.
- **렌더러**: `setCamera`의 `preset`·`frameBone`이 사진 비율(`aspect`)을 받는다. 비율을 주면 가로 폭을 뼈로 잰다 — 창 비율로 잡으면 세로 사진에서 잘리고, 렌더러 범위로 재면 아바타가 작아졌다 ([OrbitCamera.cs](../../../editor/Unity/Assets/Renderer/OrbitCamera.cs)). 에디터는 비율을 보내지 않아 그대로.
- **메시지 설명**: `studio`의 `shadowFloor`·`gradient`·`dof.show`, `setView`의 `avatarOnly`·`frameAspect` 등 빠져 있던 값을 적었다 ([Messages.cs](../../../editor/Protocol/Messages.cs)).
- 확인: 단위 테스트 758개, E2E 기본·`studio` 통과. 아바타 A로 시안·완성본·투명·크로마키·턴테이블 MP4·세션 다시 불러오기를 찍어 봤다. 에디터 창에서 세션을 여는 것은 아직 안 해 봤다.
- 안 되는 것: 켜져 있는 에디터 화면 직접 조종, FBX 소품 바로 넣기, 스티커, 달리 영상.

### 2026-11-01 (스튜디오 탭 전부)

- 스티커, 달리 녹화(`in`·`out`·`to`), 트래킹(`calibrate`·`freeze`·`recordAnim`·`test`), 말하기, 트래킹 값 연결, 블렌드쉐이프·표정 단축키, 조명 배치·방송 화질, 초점 보기, 고화질 끄기, PNG에 세션 넣기, 표정 `.anim` 저장, 저장한 카메라 위치, 오버레이, 패키지·FBX 가져오기(`import`)를 더했다 ([SceneSpec.Studio.cs](../../../editor/Tools/Shoot/SceneSpec.Studio.cs), [ShootServer.Studio.cs](../../../editor/Tools/Shoot/ShootServer.Studio.cs), [Shoot.Import.cs](../../../editor/Tools/Shoot/Shoot.Import.cs)).
- 조명 배치 값을 [LightingLayouts.cs](../../../editor/Host/Core/LightingLayouts.cs)로 옮겨 에디터 오버레이와 같이 쓴다 (에디터 동작 같음).
- 확인: 단위 테스트 799개, E2E `overlay`. 아바타 A로 스티커 2규격, 달리 PNG 프레임, 말하기 입 벌림, OSC 윙크·입 벌림·고정·`.anim`·캘리브레이션, 값 연결, 오버레이 화면 캡처(켠 동안 포즈 바꾸기 포함), 패키지 가져오기 + 소품. 실제 웹캠·마이크·VMC·FBX 파일 가져오기는 안 돌려 봤다.

### 2026-11-01 (사람이 이어 고치기)

- `edit` 명령과 세션의 `studio`: [StudioPanel.Session.cs](../../../editor/AvHost/StudioPanel.Session.cs), [Session.cs](../../../editor/Host/Core/Session.cs). `--session`·`embedSession`에도 들어간다. 에디터 화면에는 새 단추가 없다.
- 시험: `dotnet bin/av/VrcProtoAv.dll --test --test-session-edit <폴더> <세션>` ([MainWindow.TestEdit.cs](../../../editor/AvHost/MainWindow.TestEdit.cs), 테스트 모드만). 머슬 줄 값 고치기 · 배경 고치기 · 되돌리기·다시 하기 · 저장·다시 읽기를 코드로 돌린다. 마우스로 누른 것은 아니다.
- 스튜디오 값은 원래 실행 취소가 없다. 되돌리기는 포즈 등 문서 값만 돌린다.

### 2026-11-02 (배포 zip에 넣기)

- [MakeRelease.cs](../../../editor/Tools/Release/MakeRelease.cs)가 `Shoot.exe`(함께 넣은 .NET으로 돈다)와 안내를 넣는다. 배포용 SHOOT.md는 이 문서에서 숨김 원칙 뒤를 잘라 만든다. AI 안내 원본은 [release/AGENTS.md](../../../editor/release/AGENTS.md).
- 에디터가 켜질 때 설정 폴더에 프로그램 위치를 적는다 ([MainWindow.Safety.cs](../../../editor/AvHost/MainWindow.Safety.cs)).
