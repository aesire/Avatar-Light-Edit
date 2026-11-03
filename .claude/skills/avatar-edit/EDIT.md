# 창 없는 편집 (Shoot look · apply)

에디터 페이지(아바타·머티리얼·블렌드쉐이프·자세·환경·최적화)에서 하는 값 바꾸기와 아이템 붙이기를 편집 JSON 하나로 한다. 결과는 세션(`.alesav`)이다. 그 세션을 `run`으로 찍어 확인하고, 에디터에서 열거나 내보낸다. 에디터 화면과 같은 Core 함수를 쓰므로 결과가 에디터에서 한 것과 같다.

```bash
dotnet bin/Shoot.dll look <아바타|세션>                       # 요약: 아이템·기본 옷·메시·머티리얼·토글·퍼포먼스 (렌더러 없이, 0.3초)
dotnet bin/Shoot.dll look <아바타|세션> --material 이름         # 머티리얼 값 (.mat에 적힌 것 + 바꾼 것)
dotnet bin/Shoot.dll look <아바타|세션> --shapes [메시] [--find 글] [--all]   # 블렌드쉐이프 지금 값 (기본: 0이 아닌 것만)
dotnet bin/Shoot.dll look <세션> --changes                     # 원본과 다른 값 전부 (a = 편집의 addr로 그대로 쓸 수 있다)
dotnet bin/Shoot.dll apply <편집.json> [--avatar 파일] [--out 세션]   # 적용해 세션으로
```

`editor` 폴더에서 실행한다. 빌드는 `sh build.sh shoot`.

## 순서

1. `look`으로 이름을 확인한다. 이름이 틀리면 `apply`가 비슷한 이름을 알려 주니 대충 맞춰도 된다.
2. 편집 JSON을 쓴다. `apply`가 세션을 저장한다 (기본: 편집 파일 이름 + `.alesav`).
3. 장면 JSON의 `avatar`에 그 세션을 넣고 `run --draft`로 찍는다. **그림을 직접 보고** 판단한다.
4. 고칠 것이 있으면 `--avatar 세션`으로 편집을 더 쌓거나, 원래 아바타에서 편집 파일을 고쳐 다시 `apply`.
5. 사람에게 넘기기: `dotnet bin/av/VrcProtoAv.dll <세션>` (에디터로 열기), `--export <세션> <Unity 프로젝트>`, `--upload-prepare <세션>`.

## 빠르기

`apply`는 렌더러에 아바타를 올린다. 렌더러가 셰이더 프로퍼티 종류(숫자·색·벡터·텍스처)와 기본 자세를 알려 주기 때문이다. 처음 약 5~10초, 그 뒤로는 0.1초 안팎이다. 이어서 같은 아바타의 세션을 `run`하면 다시 올리지 않고 바뀐 값만 보낸다 (장당 약 1초). 아이템 편집은 IR을 다시 만들어 20~30초 걸린다.

## 편집 파일

```json
{
  "avatar": "avatar.ir.json",
  "out": "pink.alesav",
  "edits": [
    { "material": "Hair", "set": { "_Color": "#ffb0d0", "_MainTexHSVG": [0.05, 1, 1, 1] } },
    { "shapes": { "eye_smile_1": 60 } },
    { "hide": ["Cloth"] },
    { "wardrobe": { "outfit": true } },
    { "blinkFix": true },
    { "pose": "relaxed", "expression": "none", "lighting": "indoor" }
  ]
}
```

`[ … ]`만 써도 된다 (그때는 `--avatar`로). 상대 경로는 편집 파일 기준이다. 편집은 위에서 아래로 적용된다. **하나라도 틀리면 아무것도 바꾸지 않고** `errors`로 알린다. 아이템 편집은 IR을 다시 만들어야 해서 값 편집보다 먼저 한다 (적힌 자리와 상관없이). `_`로 시작하는 항목과 `note`는 메모로 본다.

### 머티리얼

| 키 | 값 |
|---|---|
| `material` | 이름 · GUID · 파일 이름, 여럿이면 배열, `"*"` = 전부. 같은 이름의 머티리얼이 여럿이면 모두 바뀐다 |
| `renderer` | 그 메시가 쓰는 머티리얼 중에서만 고른다 (`"material": "*", "renderer": "Hair"`) |
| `set` | `{ 프로퍼티: 값 }`. 숫자 · `true/false` · 색 `"#RRGGBB"`(알파는 그대로) 또는 `[r, g, b(, a)]` 0~1 감마 값 · 텍스처 이름·경로·GUID, 이미지 파일 경로(새 텍스처로 더함), `null`(비움) · `"_MainTex_ST": [타일 x, 타일 y, 오프셋 x, 오프셋 y]` |
| `keywords` | `{ "_EMISSION": true }` |
| `renderMode` | lilToon만: `Opaque` `Cutout` `Transparent` `Refraction` `RefractionBlur` `Fur` `FurCutout` `FurTwoPass` `Gem` (외곽선·반투명 방식은 그에 맞게 정리) |
| `transparentMode` · `outline` | lilToon만: `Normal` `OnePass` `TwoPass` · `true/false` |
| `shader` · `renderQueue` | 셰이더 이름 · 큐 번호 |
| `reset` | `true` = 그 머티리얼을 원래대로 (다른 값보다 먼저) |

프로퍼티 이름은 `look --material`의 이름 그대로다. lilToon 색 바꾸기는 `_Color`(곱하는 색)나 `_MainTexHSVG`([색상 −0.5~0.5, 채도, 명도, 감마])가 편하다. 색 조화처럼 어울리는 색을 고르는 일은 직접 계산해 값으로 넣는다.

### 블렌드쉐이프

`{ "shapes": { "이름": 0~100 }, "renderer": "Body" }`. `renderer`를 빼면 그 이름을 가진 메시 전부. 자동 Blink Fix가 켜져 있으면 눈 값에 맞춰 따라 고친다 (에디터와 같음).

`{ "saveShapes": "이름" }` · `{ "loadShapes": "이름" }`: 블렌드쉐이프 세팅 저장·불러오기 (에디터 "세팅 저장·비교"와 같은 목록).

### 켜고 끄기

| 편집 | 하는 일 |
|---|---|
| `{ "show": [...], "hide": [...] }` | 붙인 아이템 이름이면 Bone Proxy로 옮긴 부분까지, 메시 이름이면 렌더러·EditorOnly 조상까지, 그 밖의 오브젝트 이름·경로면 켜기·끄기만 |
| `{ "wardrobe": { "hair": false, "outfit": true } }` | 아바타에 원래 있던 헤어·옷·악세서리·몸 묶음 (`look`의 `wardrobe`) |
| `{ "toggles": [ { "name": "모자", "objects": ["Hat", "Brim"] } ], "append": false }` | 에디터의 토글 묶음 (Shift+1~9). 기본은 목록을 통째로 바꾼다 |

같은 이름의 아이템이 둘이면 `look`의 `items[].id`(프리팹 GUID 또는 `node:번호`)로 고른다.

### 아이템 (IR을 다시 만든다)

| 편집 | 하는 일 |
|---|---|
| `{ "addItem": "옷.unitypackage" 또는 [패키지, 머티리얼 패키지], "prefab": "이름" }` | 붙이기. BOOTH zip도 된다. 프리팹이 여럿이면 `prefab`이 없을 때 목록을 알려 준다 (이 아바타 이름이 든 것·MA 프리팹이 앞) |
| `kind` · `target` · `keepWorld` · `scale` | `addItem`·`placeItem`에 함께: 종류(`outfit` `hair` `accessory` `auto`), 붙일 뼈(`Head` 등, `@root`), 위치 유지, 크기. 둘 다 없으면 자동 |
| `{ "placeItem": "이름", "target": "Head", "kind": "accessory" }` | 붙일 곳 바꾸기 (핸들로 옮긴 자리는 버림) |
| `{ "fitHead": "이름" }` | 머리 크기에 맞춤 (`look`의 `headScale`) |
| `{ "itemScale": "이름", "scale": 1.1 }` | 아이템 전체 크기 |
| `{ "removeItem": "이름" }` | 빼기 (그 아이템에 바꾼 값도 없어진다) |

답의 `items`에 붙은 방법·맞춘 본 수가 나온다. `otherAvatarsItem: true`면 다른 아바타용일 수 있다 — 찍어서 확인한다. 아바타를 만든 패키지를 옮기거나 지웠으면 아이템 편집은 안 된다 (에디터도 같음).

### 메시

| 편집 | 하는 일 |
|---|---|
| `{ "blinkFix": true, "renderer": "Body" }` | 깜빡임 맞추기 자동 모드 (에디터 "자동"과 같음). `false`면 되돌림. `renderer`를 빼면 고칠 것이 있는 메시 전부 |
| `{ "mouthFix": true }` | 입 맞추기 (에디터가 처음 골라 두는 대상·편집 그대로) |
| `{ "weld": 0.1, "renderers": "*", "keepSeams": true, "keepShapes": true }` | 정점 합치기, mm |
| `{ "decimate": 0.5 }` · `{ "decimate": { "triangles": 70000 } }` | 정점 줄이기 (남길 비율, `1` = 끄기 · 삼각형 수를 메시에 나눠서) |

### 자세·표정·조명·옮기기

- `pose` · `expression` · `lighting` · `lights` · `post` · `look`: 장면 JSON과 같은 형식 (avatar-shoot의 SHOOT.md). 세션에 남으니 `run`에서 적지 않으면 이 값을 쓴다. `"pose": null` = 원래대로.
- `{ "move": "Hat", "position": [x, y, z], "rotation": [x, y, z](도) 또는 쿼터니언, "scale": 1.2 또는 [x, y, z], "nudge": [dx, dy, dz] }`: 오브젝트의 부모 기준 위치.

### 되돌리기·주소

- `{ "reset": "all" }` 또는 `["materials", "shapes", "visibility", "meshes", "moves", "pose", "expression", "lighting", "toggles"]`
- `{ "addr": "MatFloat|GUID|_Prop", "value": 1 }`: 위 편집으로 안 되는 값만. `look --changes`의 `a`를 그대로 쓴다.

## 답

`ok`, `session`(저장한 파일), `done`(한 일 한 줄씩), `changes`(원본과 다른 값 수), `items`(아이템 편집 때), `warnings`, `seconds`. 실패하면 `errors`와 종료 코드 1, 세션은 쓰지 않는다.

들어온 세션의 카메라·스튜디오(배경·소품) 값은 그대로 넘어간다.

## 안 되는 것

- 색 굽기(렌더러의 굽기 셰이더가 필요), 텍스처 후보 필터, 이름 번역, 즐겨찾기, 비교 시트 — 화면을 돕는 기능이라 값으로 대신한다.
- 표정·블렌드쉐이프 단축키(F1~F9 등): 장면 JSON의 `expressionKeys`·`shapeKeys` + `run --session`.
- 실행 취소 기록은 세션에 남지 않는다. 앞 세션 파일을 두거나 `reset`을 쓴다.
- 켜 둔 에디터 화면을 바로 바꾸지 않는다. 세션을 열어 넘긴다.
- 셰이더를 바꾼 같은 편집 파일 안에서 새 셰이더의 프로퍼티를 바꾸면, 프로퍼티 종류를 옛 셰이더 기준으로 본다. 두 번에 나눠 `apply` 한다.

## 작업 기록

### 2026-11-02

- **`look`·`apply`** ([DocEdit.cs](../../../editor/Tools/Shoot/DocEdit.cs), [ShootServer.Edit.cs](../../../editor/Tools/Shoot/ShootServer.Edit.cs), [Shoot.cs](../../../editor/Tools/Shoot/Shoot.cs)): 에디터 페이지의 값 바꾸기·아이템 붙이기를 이름으로 적어 세션으로 만든다. 틀린 이름은 비슷한 이름과 함께 모두 모아 알리고, 하나라도 틀리면 아무것도 바꾸지 않는다.
- **에디터와 같은 함수를 쓰게 옮김**: 기본 옷·아이템 켜고 끄기를 [Wardrobe.cs](../../../editor/Host/Core/Wardrobe.cs)로(`Visible`·`Show`·`SetGroup`·`SetItem`, [AvatarPanel.cs](../../../editor/AvHost/AvatarPanel.cs)가 부름), lilToon 렌더링 모드 바꾸기를 [LilToonRules.cs](../../../editor/Host/Core/LilToonRules.cs) `Switch`로([MaterialPanel.cs](../../../editor/AvHost/MaterialPanel.cs)가 부름), Mouth Fix 처음 고르기 기준을 [BlinkFixPlan.cs](../../../editor/Host/Core/BlinkFixPlan.cs) `DefaultOverlap`로. 아이템 다시 만들기는 [Items.cs](../../../editor/AvHost/Items.cs)를 Shoot에 함께 컴파일한다 (화면과 무관한 코드). 에디터 동작은 같다.
- **촬영 서버가 같은 IR을 다시 올리지 않음** ([ShootServer.cs](../../../editor/Tools/Shoot/ShootServer.cs) `EnsureAvatar`): 같은 아바타의 다른 세션·IR은 값만 바꿔 보낸다 (RenderSync). 편집 → 촬영 → 원본 비교가 장당 약 1초.
- 확인: 단위 테스트 909개 ([DocEditTests.cs](../../../editor/Tests/DocEditTests.cs)). 큰 시험 장면으로 머티리얼 색·렌더링 모드·블렌드쉐이프·아이템 숨기기·기본 옷 입히기·Blink Fix·자세·조명·토글·세팅 저장을 한 번에 적용해 찍어 봤고, IR 사본에 Li-ne 헤어를 `addItem`으로 붙여 찍어 봤다. 결과 세션을 에디터 창에서 열어 보지는 않았다.
