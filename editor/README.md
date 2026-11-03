# Avatar Light Edit — 에디터

에디터 부분이다. 임포터(importsim)와는 **IR 파일**로만 연결된다.

```
Avalonia Host (.NET 8)  ── named pipe 2개 ──  Unity 2022.3 렌더 서버 (자식 창, -parentHWND)
  Document = 원본 IR + Changeset                씬 구성 · lilToon/Poiyomi 렌더 · 카메라 · 캡처 · 환경 · PhysBone
  Undo/Redo · 세션 · 작업 창 · 단축키           (상태 없음: Host가 보낸 값대로 그린다)
```


### 블렌드쉐이프 세팅 사진

세팅을 저장하거나 비교할 때 찍는 얼굴 썸네일은 휴머노이드 `Head` 본을 기준으로 정면 촬영한다. 장면 bounds에 포함된 손 프록시·머리 장식·소품 때문에 사진이 옆이나 아래를 향하지 않으며, `Head` 매핑이 없는 아바타에서는 얼굴 프리셋을 사용한다. 비교/슬라이드 쇼 부동 창은 이미 열린 창과 겹치지 않는 위치를 자동으로 찾는다.

## 빌드

.NET SDK 없이 Unity에 들어 있는 Roslyn으로 빌드한다 (importsim과 같은 방식). 실행에는 .NET 8 런타임이 필요하다.
Avalonia NuGet 패키지는 `packages/`에 미리 펼쳐 둔다.

```bash
python -I Tools/NuGet/fetch_nuget.py packages Avalonia=11.3.13 Avalonia.Desktop=11.3.13 Avalonia.Themes.Fluent=11.3.13
sh build.sh              # Protocol.dll, HostTests, MakeSampleIr·E2E, Host(bin/av) — 일부만: sh build.sh proto|tests|tools|av
dotnet bin/HostTests.dll # 코어 단위 테스트
sh build_renderer.sh     # Unity 렌더 서버 → Unity/Build/VrcProtoRenderer.exe (처음엔 셰이더 컴파일로 오래 걸림)
sh build_shaderpacks.sh  # 기본 셰이더 팩 (Poiyomi·MToon, 필요할 때만)
```

PowerShell·명령 프롬프트에는 `sh`가 없으므로 `editor\build.cmd`를 쓴다 (Git for Windows의 `sh.exe`를 찾아 위 스크립트를 부른다, 탐색기에서 더블클릭해도 된다).

| 명령 | 빌드하는 것 |
|---|---|
| `build.cmd` | 에디터 전부 + 렌더러 |
| `build.cmd av` | 에디터만 |
| `build.cmd renderer` | 렌더러만 |

- 빌드 전에 에디터·렌더러를 닫는다 (실행 중이면 파일이 잠긴다).
- 빌드는 한 번에 하나씩 돌린다 (`obj/*.rsp`를 같이 쓴다).
- `sh build.sh av`는 `../importsim/bin/ImportSim.Core.dll`이 없거나 오래됐으면 importsim을 먼저 빌드해 함께 복사한다.
- 렌더러는 새 `.cs` 파일이 생긴 뒤 첫 빌드에서 "script errors"가 나면 한 번 더 빌드한다. `build_renderer.sh`는 로그에 `error CS`가 있으면 실패로 알리는데, 패키지 구성(manifest)을 바꾼 직후에는 거짓 경보일 수 있으니 `[VrcProto] build Succeeded`와 `Build/VrcProtoRenderer_Data/Managed/Assembly-CSharp.dll` 시각을 확인한다.
- 렌더러 스플래시는 빌드하는 PC의 Unity 라이선스가 Pro(학생 플랜 포함)일 때만 꺼진다. Personal로 빌드하면 다시 켜진다.
- 배포 zip은 아래 "배포" 절.

## 배포

```powershell
.\release.cmd --version 0.1.0 --repo owner/name   # 저장소가 아직 없으면 --no-repo, 렌더러를 다시 빌드하지 않으려면 --skip-renderer
```

에디터·도구 빌드 → 단위 테스트 → 렌더러 빌드 → `Tools/Release/MakeRelease`가 `dist/ALEdit-<버전>.zip`(약 154 MB)과 `.sha256`을 만든다. 처음 한 번 apphost 패키지를 nuget.org에서 받는다.

- zip 맨 위 폴더 안에 앱 폴더 통째: `ALEdit.exe`(apphost + 매니페스트, 콘솔 없음) · .NET 8 런타임 · `Renderer/` · `version.txt` · `update-repo.txt` · `README.txt`·`LICENSE.txt`·`THIRD_PARTY_NOTICES.txt`(원본은 `release/`) · `licenses/`
- 점검에 걸리면 실패한다: 시험 도구·샘플·PDB·VRChat SDK·기본이 아닌 셰이더 팩·빌드 PC 사용자 이름이 있거나 필수 파일이 없을 때. Unity 어셈블리의 PDB 경로는 지운다.
- **렌더러 보안 패치**: Unity 2022.3.22f1 런타임에는 CVE-2025-59489가 있다. Unity 공지(unity.com/security/sept-2025-01)의 Unity Application Patcher(1.3.3에서 확인)로 고친 `UnityPlayer.dll`을 `Unity/PatchedPlayer/`에 둔다 (저장소에 없음, 빌드 PC마다 한 번):
  ```powershell
  UnityApplicationPatcherCLI -windows -unityPlayerLibrary <editor\Unity\Build\UnityPlayer.dll>
  ```
  그 뒤 `editor\Unity\Build\UnityPlayer.dll`을 `editor\Unity\PatchedPlayer\`로 복사. `build_renderer.sh`는 빌드가 원본 DLL을 다시 넣으면 이 사본으로 덮고, `MakeRelease`는 원본 DLL이 들어가면 실패한다.
- **올리기**: 공개 GitHub 저장소를 하나 만들어 **릴리스만** 쓴다 (소스를 올리지 않아도 된다). 태그 `v0.1.0`, 첨부는 zip 하나, 프리릴리스·초안이 아니어야 업데이터가 본다. 업데이터는 GitHub가 주는 sha256 값으로 받은 파일을 확인한다.
- 켜지는지 시험: **프로그램 폴더 안에서** `ALEdit.exe --test --ui-shots <x.ir.json> <폴더>` (배포본의 `--ui-shots`는 `--test`와 함께만). 다른 폴더에서만 시험하면 더블클릭 문제를 지나칠 수 있다 (`deps.json`, MakeRelease.cs). 업데이트 시험: `--test --test-update <가짜 API 주소>`.

## 실행

```bash
dotnet bin/MakeSampleIr.dll sample          # 테스트용 IR
dotnet bin/av/VrcProtoAv.dll sample/sample.ir.json
```

- 렌더러는 `VRCPROTO_RENDERER` 환경 변수 → `bin/av/Renderer/VrcProtoRenderer.exe` → `Unity/Build/VrcProtoRenderer.exe` 순서로 찾는다. 없어도 편집·세션 저장은 된다 (IR에 적힌 값만 표시).
- `VRCPROTO_THEME=light|dark`로 테마를 고정할 수 있다.
- 파일 없이 켜면 마지막에 연 파일(IR·세션)을 연다 (설정 "저장 위치 › 켤 때 마지막 파일 열기").
- `.unity`/`.prefab` 경로를 인자로 주면 Unity 씬·프리팹 가져오기로 연다.

> **경로 길이 주의**: Unity 플레이어는 데이터 폴더 경로가 260자를 넘으면 시작하지 못한다. 프로젝트를 짧은 경로(예: `D:\vrcproto`)에 두거나,
> 빌드된 `Unity/Build` 폴더를 짧은 경로로 복사한 뒤 `VRCPROTO_RENDERER`로 지정한다.

### 테스트 모드

```bash
sh run_test.sh [--build] [x.ir.json | 세션.alesav]     # Windows에서는 run_test.cmd (두 번 누르기도 됨)
```

- `--test`로 띄운다: 설정을 `%APPDATA%\ALEdit\test\avhost.json`에 따로 둬 평소 설정을 건드리지 않고, 창 제목에 `[테스트]`. 파일을 주지 않으면 `sample/sample.ir.json`.
- 소스가 빌드보다 새로우면 먼저 `sh build.sh av`를 한다.
- 화면 글 표 `texts/<화면 언어>.tsv`를 저장할 때마다 다시 읽어 열린 화면에 반영한다. `Ctrl+Shift+R` 다시 읽기, `Ctrl+Shift+T` 원문 ↔ 바꾼 글.

### 창 없이 쓰는 명령

| 명령 | 하는 일 |
|---|---|
| `--ui-shots <x.ir.json> <출력 폴더> [--ui-sizes 1000x600,…]` | 페이지마다 쉬운·전문가 모드 화면을 PNG로 찍고 닫는다 |
| `--test --test-timing [--test-pages <세션>]` | 시작·페이지 열기 시간을 설정 폴더 `test-timing.txt`에 |
| `--build-shader-pack <.unitypackage\|폴더> [--name …] [--ir …]` | 셰이더 팩 만들기 |
| `--build-project-shaders <x.ir.json> [GUID …]` | 원본 프로젝트의 셰이더로 팩 만들기 |
| `--export <x.ir.json\|세션> <Unity 프로젝트> [--name …] [--blinkfix]` | Unity 프로젝트로 내보내기 |
| `--upload-prepare <x.ir.json\|세션> [--gimmicks ma,liltoon] [--weld mm]` | VRChat 올리기 준비 |
| `--no-prestart` | 렌더러를 미리 띄우지 않음 (시험용) |

모두 `dotnet bin/av/VrcProtoAv.dll` 뒤에 붙인다.

## 화면

설정 화면형: 이름이 보이는 사이드바(접기 가능), 페이지 제목과 설명, 둥근 카드 안 "이름 · 값" 줄, 상태 표시줄의 색 점(렌더러·장면·변경). 창 위치·폭·사이드바 접힘 등은 `%APPDATA%/ALEdit/avhost.json`에 기억한다.

왼쪽 섹션은 위에서 아래로 작업하는 순서다 (`Ctrl+1`~`Ctrl+9`):

| 페이지 | 하는 일 |
|---|---|
| 아바타 | 아이템(의상·헤어·악세서리) 붙이기, 머리 크기 자동 맞춤·아이템 전체 크기 수동 조절, 파츠별 돋보기 윤곽 토글, 파츠별 머티리얼 바로가기, 기본 옷, 메시 표시, 퍼포먼스 등급 예상 |
| 머티리얼 | lilToon·Poiyomi 등 머티리얼 값 편집, 섹션 탭, 색 조화, 색상 프로퍼티 팝업의 색상환, 색 굽기, 헤어·옷에 맞는 텍스처 후보 필터 |
| 블렌드쉐이프 | 값 편집, 이름 번역, 세팅 저장·비교, 즐겨찾기, 단축키 |
| 자세 | 휴머노이드 머슬 자세, 프리셋·`.anim`, 뷰포트 본 조작, 대칭, 손 모양 |
| 환경 | 조명 프리셋·배치판·추가 조명, 후처리 |
| 스튜디오 | 배경·소품·카메라 위치·심도·촬영·녹화, 표정·페이셜 트래킹, 방송 오버레이, 세팅 프리셋 파일(`.studio.json`) 저장·불러오기 |
| 최적화 | 퍼포먼스 등급, 정점 합치기·폴리곤 줄이기 |
| 올리기 | 무엇이 올라가는지 + VRChat 올리기 준비, Unity 프로젝트로 내보내기 |

- **쉬운 모드 · 전문가 모드**: 사이드바 왼쪽 아래 단추. 쉬운 모드는 자주 쓰지 않는 세부 설정을 숨길 뿐 기능·값은 같다.
- **3D 미리보기 보조 표시**: 방향 기즈모·원근/직교(`5`)·바닥 격자(`G`)·본 표시·와이어프레임·거울(`M`)·배경 덮어쓰기·조작 핸들(`P`). 화면에만 그리고 촬영에는 넣지 않는다.
- **PhysBone 미리보기**: VRChat 문서의 파라미터 의미에 맞춘 근사 시뮬레이션, 잡기·고정, 움직임 시험, 느낌 조절 창.
- **세밀 조정 막대 · 다이얼**: 슬라이더 값을 큰 막대·다이얼로 세밀하게 (VR에서 조작하기 쉽게).
- **SteamVR**: "VR로 보기", 에디터 창을 헤드셋 안 오버레이로 띄우고 레이저로 조작.
- **화면 글**: 코드의 원문은 그대로 두고 `texts/ko.tsv`(한국어 다듬기) · `en.tsv`(영어) · `ja.tsv`(일본어)에 바꿀 글을 적는다. 언어는 설정 › 화면 › 언어, 이번만은 `--lang en`. 표 새로 뽑기는 `python -I Tools/Texts/extract_texts.py` (세 표 함께).

## 검증

```bash
dotnet bin/E2E.dll <VrcProtoRenderer.exe> sample/sample.ir.json e2e_out [모드]
```

Host 역할로 렌더러를 띄워(창 없이 -batchmode) 장면 로드 → 셰이더 해석·스키마 → 편집(블렌드쉐이프·색·렌더링 모드·표시·환경) →
캡처(일반/투명) → 전부 실행 취소 후 원본과 픽셀 비교까지 확인한다. 샘플이 아닌 IR(importsim 출력 등)을 주면 아바타에 맞춰 고른 대상으로 조작을 확인한다.

| 모드 | 확인하는 것 |
|---|---|
| (없음) | 기본 왕복 |
| `physbones` · `collide` · `pbfeel` · `pbvr` · `pbrules` | PhysBone 되돌아오기 · 다리 뚫림 · 떨림·무게감 · VR 움직임 · 값 규칙(시험 체인, `sample.ir.json`으로) |
| `env` · `envplus` | 조명·후처리·배경 색 · 추가 조명·라이트 볼륨·미러·AudioLink |
| `pose` · `limits` · `handles` | 자세·본 조작·대칭·손·바닥 맞추기 · 원본 근육 범위 · 이동·회전 핸들 |
| `face [얼굴 사진]` | 표정·트래킹·연결 방식·말하기 |
| `studio [이미지]` · `quality` | 스튜디오 배경·심도·녹화 · 고화질·주변 가림 |
| `mesh` · `gizmos` · `vrlink [steamvr]` | 메시 편집 · 기즈모 모양 · SteamVR 연결 |

시작할 때 `setView {pauseTime: true}`로 셰이더 시간을 멈춰 캡처마다 결과가 같게 한다.

## 구성

| 경로 | 내용 |
|---|---|
| `Protocol/` | netstandard2.1, Host·Unity·importsim 공유: JSON DOM, IR 스키마·리더(조회 색인 포함), 메시지, 파이프 채널 |
| `Host/Core/` | UI 무관 코어: `Document`(원본+덮어쓰기, Undo 그룹, 머티리얼 스키마), `RenderSync`(Document→Unity 명령), `Session`, `AvatarStats`, `LilToonRules`(lilToon 렌더링 모드 전환 규칙, MIT 이식), `PropertySections`, `BlendShapeGroups`, `Wardrobe`(기본 옷·헤어 가늠) |
| `Host/Shared/` | UI 프레임워크와 무관한 Host 공용: 렌더러 실행·감시·재기동(`RenderHost`), 편집 상태(`EditorContext`), lilToon 번역(.po) |
| `AvHost/` | Avalonia 11.3 Host. `MainWindow.*.cs`(셸·렌더러 메시지·단축키·미리보기·파일), `Shell.cs`(상태 표시줄·사이드바·대화상자), 페이지별 패널(`AvatarPanel`·`MaterialPanel`·`BlendShapePanel`·`PosePanel`·`EnvironmentPanel`·`StudioPanel` …), 창(`ImportWindow`·`ShaderPackWindow`·`UploadWindow`·`ExportWindow` …), `Ui`(코드 UI 도우미) |
| `Unity/` | 렌더 서버 프로젝트. lilToon 2.3.4를 `Packages/`에 포함, `LILTOON_DISABLE_OPTIMIZATION`으로 전 기능 유지 |
| `Tools/MakeSampleIr` | 테스트용 IR 생성기 |
| `Tools/E2E` | 렌더러 왕복 확인 (위 "검증"의 모드들) |
| `Tools/ShaderPack` | 셰이더 팩 빌더(Unity batchmode) · 기본 팩 목록·라이선스(`bundled/`) |
| `Tools/Texts` | 화면 글 표 뽑기 |
| `texts/` | 화면 글 표 (`ko.tsv` · `en.tsv` · `ja.tsv`) |
| `Tests/` | 코어 테스트 |

WPF로 만든 첫 Host는 2026-10-17에 정리하면서 뺐다 (`../old/wpf-host`에 보관, 빌드하지 않음).

## IR (임포터 출력 형식, v1)

`<name>.ir.json` + 버퍼 파일. 버퍼 참조는 `{ "off": 바이트, "count": 원소 수 }` (리틀 엔디언 float32/int32).
스키마는 [Protocol/Ir.cs](Protocol/Ir.cs) 참고 — `nodes`, `meshes`(positions/normals/tangents/uv0-3/boneIndices/boneWeights/bindposes/submeshes/blendShapes), `renderers`, `materials`(GUID, shaderGuid, floats/colors/texEnvs/keywords/renderQueue), `textures`(원본 파일 경로 + 임포트 설정).
선택 항목: `physBones`·`physBoneColliders`, `humanoid`·`humanoidSettings`, `expressions`, `faceTracking`.
좌표는 Unity 공간(임포터가 FBX 단위·축 변환을 끝낸 값). fileID는 64비트 전 범위라 문자열.

## 원칙

- 모든 편집은 `Document.Set(주소, 값)` → Changeset. 원본과 같은 값이면 덮어쓰기를 지운다 → "원본과 다른 값" 표시·리셋·세션이 정확하다.
- Unity가 죽으면 Host가 다시 띄우고 `loadScene` + 덮어쓰기 전부를 다시 보내 복원한다.
- 미지원 셰이더는 다른 셰이더로 값을 옮기지 않고 줄무늬 표시용 셰이더로 그린다. 원본 값은 패널에서 보이고 편집도 기록된다.
- 텍스처는 원본 파일을 디코드만 한다. `.meta` 임포트 설정(sRGB·노멀맵·wrap/filter·밉맵·maxSize)은 Unity와 같게 반영. 가져오기 창에서 같은 계열의 다른 색상 텍스처를 함께 가져올지 고를 수 있고, 머티리얼 텍스처 칸의 `호환만`으로 헤어·옷 후보를 빠르게 좁힐 수 있다.
- 조명·후처리는 `EnvironmentManager` + Provider(`UnityLighting`, `PostProcess`)로만 바꾼다. 환경은 `[ {provider, version, params} ]` 목록으로 저장.
- 원본 프로젝트에는 쓰지 않는다 (가져오기 결과·캐시·셰이더 팩은 `%LOCALAPPDATA%\ALEdit` 아래).

## 아직 안 한 것

- lilToonMulti(ShaderVariantCollection 필요), 32비트·ZIP 압축 PSD·TIF·EXR 텍스처, 뷰포트 선택 하이라이트
- VR: 팝업(드롭다운·메뉴) 표시, 창 잡아 옮기기·크기, 가상 키보드
