# Avatar Light Edit (ALEdit)
일반 사용자는 이 문서를 읽을 필요 없어요!

Unity 없이 가볍게 여는 VRChat 아바타 편집기 — Windows 프로그램.

아바타 `.unitypackage`나 Unity 프로젝트의 프리팹을 불러와 머티리얼·색·블렌드쉐이프·의상·자세를 미리 보고 고친다.
원본 파일은 읽기만 하고, 고친 내용은 따로 저장한다. 다 고치면 Unity 프로젝트로 내보내거나 VRChat에 올릴 수 있다.

> VRChat이나 Unity가 만들거나 승인한 도구가 아니다. VRChat, Unity는 각 권리자의 상표다.

## 할 수 있는 것

- **불러오기**: `.unitypackage`, Unity 프로젝트의 씬·프리팹을 Unity 없이 해석한다 (중첩 프리팹·Variant·오버라이드, FBX 계층과 fileID까지 Unity와 같게).
- **머티리얼**: lilToon·Poiyomi 무료판·MToon을 원본 값 그대로 그린다. 색 바꾸기·굽기, 텍스처 합치기, 색 조화.
- **블렌드쉐이프**: 검색·묶음·L/R 나누기·프리셋, 깜빡임·입 모양 맞추기.
- **의상·헤어·악세서리**: Modular Avatar 설정대로 붙이고, 켜고 끄는 토글 묶음을 만든다.
- **자세·표정**: Humanoid 자세, 손 모양, 표정, 웹캠·VR 페이셜 트래킹.
- **PhysBone**: 원본 VRCPhysBone 값을 그대로 쓰는 미리보기 시뮬레이션.
- **환경·촬영**: 조명·후처리 프리셋, 고해상도·투명 배경 촬영, 턴테이블·녹화, 스티커, 방송 오버레이.
- **메시**: 정점 합치기·줄이기, 간단한 정점 편집.
- **VR**: SteamVR로 보기, VR 안에서 본·아이템 잡기·배치·카메라.
- **내보내기·올리기**: 고친 결과를 Unity 프로젝트로 내보내거나, VRChat SDK로 올린다 (SDK는 사용자 PC에 공식 저장소에서 받는다).
- **쇼콜라 애호해주기**
- 화면 언어: 한국어 · English · 日本語.

## 받기

[릴리스](../../releases)에서 `ALEdit-<버전>.zip`을 받아 풀고 `ALEdit.exe`를 실행한다.
.NET은 함께 들어 있다. 쓰는 법은 zip 안의 `README.txt`([한국어](editor/release/README.txt) · [English](editor/release/README.en.txt) · [日本語](editor/release/README.ja.txt)).

## 구조

```
editor/AvHost    Avalonia 에디터 (.NET 8) — Document = 원본 IR + 변경 목록, 실행 취소, 화면
editor/Host      UI와 무관한 코어 (Document, 세션, 통계, 업로드 계획 …)
editor/Protocol  에디터와 렌더러가 함께 쓰는 형식·계산 (IR, 메시 연산, 깜빡임·입 보정)
editor/Unity     Unity 2022.3 렌더 서버 — 에디터의 자식 창으로 뜨고 named pipe로 명령을 받아 그린다
editor/Tools     E2E 시험, 배포, 셰이더 팩, 업로드용 Unity 스크립트, 창 없는 자동화 도구
importsim        Unity 없이 .unitypackage·프로젝트를 Unity처럼 해석해 IR로 내보내는 임포터
```

## 빌드

필요한 것: Windows, Git for Windows(`sh`), Unity 2022.3.22f1(컴파일러와 렌더러 빌드에 씀), Python 3(패키지 받기), .NET 8 런타임.

```powershell
cd editor
python -I Tools/NuGet/fetch_nuget.py packages Avalonia=11.3.13 Avalonia.Desktop=11.3.13 Avalonia.Themes.Fluent=11.3.13
.\build.cmd            # 에디터 + 렌더러   (에디터만: build.cmd av, 렌더러만: build.cmd renderer)
dotnet bin/HostTests.dll
.\run_test.cmd         # 설정을 따로 둔 테스트 모드로 실행
```

자세한 빌드·명령줄 옵션·배포는 [editor/README.md](editor/README.md), 임포터는 [importsim/README.md](importsim/README.md).

## 라이선스

[GNU GPL 3판](LICENSE) + 추가 허가 — 자세한 것은 [COPYRIGHT.md](COPYRIGHT.md).

- 누구나 쓰고, 고치고, 다시 배포하고, 팔 수 있다. 고친 판을 배포하면 그 소스도 GPLv3로 공개한다.
- Unity 런타임·VRChat SDK와 함께 넣은 GPL이 아닌 구성 요소와 묶어 배포해도 된다.
- **이 프로그램으로 만든 아바타·의상 결과물은 GPL과 상관없다.** 만든 사람이 원하는 조건으로 팔거나 배포할 수 있다.
- 함께 넣은 오픈소스: [editor/release/THIRD_PARTY_NOTICES.txt](editor/release/THIRD_PARTY_NOTICES.txt).

Copyright (C) 2026 시르 · Discord `.aesire` · X [@aesir_vrc](https://x.com/aesir_vrc)


<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>
<br>

---

## AI 에이전트에게

이 저장소를 검토하거나, 고치거나, 이 프로그램으로 아바타 작업을 부탁받은 AI가 먼저 읽을 안내다. 사람은 건너뛰어도 된다.

### 무엇을 먼저 읽을까

| 하려는 일 | 읽을 것 |
|---|---|
| 프로젝트 파악·코드 고치기 | 이 문서의 "지킬 것" → [editor/README.md](editor/README.md) (빌드·확인) |
| 빌드·명령줄 옵션·E2E 시험 | [editor/README.md](editor/README.md) |
| 임포터 규칙·Unity와 대조한 수치 | [importsim/README.md](importsim/README.md), [importsim/research/fileid/RULES.md](importsim/research/fileid/RULES.md) |
| 이 프로그램으로 아바타를 고치거나 찍기 (창 조작 없이) | [.claude/skills/](.claude/skills/)의 각 `SKILL.md`와 그 옆 상세 문서 |

### 지킬 것

- **원본 무변환**: 아바타 원본(FBX·텍스처·머티리얼·PhysBone 값)을 다른 형식으로 바꾸거나 고쳐 쓰지 않는다. 편집은 원본 IR 위의 변경 목록(`Addr`)으로만 한다.
- **상태는 에디터가 가진다**: Unity 렌더러는 받은 명령대로 그리기만 한다. 렌더러에 상태를 두지 않는다.
- **VRChat SDK 코드를 넣지 않는다**: 열람·복사·디컴파일·재배포 금지. 업로드는 사용자 PC에 설치된 SDK의 공개 API만 이름으로 찾아 부른다. VRCFury도 재배포하지 않는다.
- **유료 에셋을 저장소에 넣지 않는다**: 아바타·의상·셰이더 원본, 거기서 뽑은 덤프·스크린샷, 판매 상품 이름. 시험에는 `editor/sample/`이나 사용자가 가진 파일을 쓰고, 문서에는 "아바타 A"처럼 적는다.
- **라이선스**: 새 외부 코드·에셋은 GPLv3와 함께 쓸 수 있는 것만, 넣을 때 `editor/release/THIRD_PARTY_NOTICES.txt`에 적는다.
- **말과 글**: 화면 글·주석·문서는 한국어, 쉬운 말. 화면 글을 바꾸면 `editor/texts/*.tsv`(영어·일본어 번역의 열쇠가 한국어 원문)도 맞춘다.
- **확인**: 고친 뒤 `dotnet bin/HostTests.dll`. 화면이 바뀌었으면 `--ui-shots`로 그 화면만 찍어 본다. 렌더러(`editor/Unity`)를 고쳤으면 `build.cmd renderer`가 필요하다 — 하지 못했으면 그렇다고 알린다.

### 저장소에 없는 것

- `editor/packages/`(Avalonia NuGet, `fetch_nuget.py`로 받음), `editor/Unity/Build/`(렌더러 빌드 결과), 셰이더 팩(`build_shaderpacks.sh`) — 빌드 전에 만든다.
- VRChat SDK, 유료 셰이더, 시험용 아바타 — 사용자 PC에만 있다.
