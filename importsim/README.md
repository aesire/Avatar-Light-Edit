# importsim

Unity 없이 `.unitypackage`나 Unity 프로젝트의 `Assets` 폴더를 **Unity가 임포트하고 로드한 것처럼** 해석하는 도구다.
FBX와 프리팹(중첩 프리팹, Variant, 오버라이드)을 GameObject 트리로 재구성하고,
각 노드의 컴포넌트, 메시·머티리얼 참조, 블렌드쉐이프 값을 낸다.

- 결과물은 `bin/ImportSim.Core.dll`(라이브러리, `src/` + `ir/`)과 `bin/importsim.dll`(명령줄)이다. IR 출력 때문에 `Protocol.dll`(에디터 IR 정의)에 의존한다. 실행에는 .NET 6 이상 런타임이 필요하다.
- 소스는 약 1,450줄이고 netstandard 2.1 / C# 9로 작성했다. 나중에 Unity로 빌드한 뷰어 앱에 소스를 그대로 넣을 수 있다.

## 빌드

.NET SDK가 없어도 된다. Unity 2022.3에 들어 있는 Roslyn 컴파일러로 빌드한다.

```bash
sh build.sh
```

## 사용

```bash
# 패키지 안의 프리팹 해석 (의존 에셋이 다른 곳에 있으면 프로젝트 폴더를 앞에 함께 준다)
dotnet bin/importsim.dll "E:/프로젝트/Assets" a.unitypackage b.unitypackage --only-from-packages --out out/

# 목록만
dotnet bin/importsim.dll a.unitypackage --list

# 특정 프리팹만
dotnet bin/importsim.dll a.unitypackage --prefab Outfit --out out/

# FBX 마다 Transform 값 + 메시 데이터 (정점·노멀·UV·컬러·서브메시·본 가중치·바인드포즈·블렌드쉐이프)
dotnet bin/importsim.dll a.unitypackage --geometry-out geo/ [--model Outfit]

# 에디터 IR v1 장면 (프리팹 하나 = 장면 하나). --prefab 필수
dotnet bin/importsim.dll "E:/프로젝트/Assets" mats.unitypackage a.unitypackage --prefab Outfit.prefab --ir-out ir/
```

- 소스는 여러 개를 줄 수 있고, 뒤에 준 것이 같은 guid를 덮어쓴다.
- `--out` 폴더에는 프리팹마다 `<guid>.json`이 생긴다. 노드마다 `name`, `active`, `p`/`r`/`s`(localPosition / localRotation / localScale), `components`(type / enabled / mesh / materials / blendShapes / avatar / script / SMR 의 bones·rootBone), `children`이 들어 있다.
- `--geometry-out` 폴더에는 FBX 마다 `<guid>.json`(노드 Transform 값, 메시 데이터)이 생긴다. 형식은 `tests/unity/GeometryDumper.cs`와 같다. 환경 변수 `IMPORTSIM_DEBUG_CP=1`이면 정점마다 FBX 제어점 번호(`cp`)도 쓴다(비교·조사용).
- `--outfit <프리팹>`(여러 번): 아바타 루트 아래에 의상·헤어·악세서리 프리팹을 넣고(`Resolver.Compose`, Unity에서 끌어다 놓은 것과 같음) MA 설정대로 본을 합친다(`src/OutfitMerge.cs`). 씬에 원래 있던 MA 의상도 합친다. IR 선택 항목 `outfits`(아이템마다 종류·방법·맞은 본 수·위치 차이)와 `outfitRules`(Shape Changer·Blendshape Sync, `ir/IrOutfits.cs`). 의상 노드는 아바타 노드 뒤에 온다 (아바타 노드 번호가 그대로).
- IR 선택 항목 `humanoid`: Animator의 Avatar가 모델(FBX) 안 Avatar이면 모델 .meta의 `humanDescription.human`(boneName ↔ humanName)을 읽어 Animator 오브젝트 아래에서 이름으로 노드를 찾는다 (`ir/IrHumanoid.cs`). 따로 만든 Avatar 에셋(`.asset`, UniVRM 등)은 본문의 `m_HumanDescription`을 같은 방식으로 읽는다 (2026-10-23).
- IR 선택 항목 `expressions` (2026-10-19, `ir/IrExpressions.cs`): 아바타 루트의 VRCAvatarDescriptor → `baseAnimationLayers`의 FX(type 5) 컨트롤러 → 레이어별 상태(하위 상태 머신·블렌드 트리 포함)의 `.anim` 중 얼굴 메시(`VisemeSkinnedMesh`, 없으면 `customEyeLookSettings.eyelidsSkinnedMesh`)의 블렌드쉐이프를 0이 아닌 값으로 바꾸는 클립 → `[{ name: 상태 이름, layer, clip, guid, shapes: [{ path: 아바타 루트 기준, shape, value: 첫 키 }] }]`. 같은 클립은 한 번만, 바이너리 `.anim`은 건너뜀. 없으면 쓰지 않는다.
- IR 선택 항목 `humanoidSettings` (2026-10-19, `IrHumanoid.cs`의 `HumanoidSettings`): 같은 `humanDescription`의 `armTwist`·`foreArmTwist`·`upperLegTwist`·`legTwist`·`armStretch`·`legStretch`·`feetSpacing`·`hasTranslationDoF`와, 기본값을 바꾼 뼈의 근육 범위 `limits { 휴머노이드 이름: { min, max, center, axisLength } }`(도; serializedVersion 3은 `modified: 1`, 예전 형식은 `useDefaultValues: 0`인 뼈만). 모두 Unity 기본값이면 쓰지 않는다 → 기본값 아바타의 IR은 그대로.
- `--ir-out` 폴더에는 고른 프리팹마다 `<이름>.ir.json`, `<이름>.ir.bin`이 생긴다. 형식은 `../editor/Protocol/Ir.cs`이고, 쓴 뒤 `IrScene.Load`로 다시 읽어 확인한다.
  - 노드 = 프리팹의 GameObject (깊이 우선, source = {프리팹 guid, GameObject fileID}). 렌더러 = SkinnedMeshRenderer / MeshRenderer(+MeshFilter).
  - 메시는 FBX 메시(`ModelSim` 기하)와 메시 에셋(`.asset`, `src/MeshAsset.cs` — UniVRM·굽기 도구가 만든 텍스트 직렬화 메시: `_typelessdata` 정점 stream, 인덱스, 서브메시, 블렌드쉐이프, 바인드포즈를 그대로 읽음. 압축 메시·바깥 데이터·바이너리 직렬화는 경고)를 낸다. 블렌드쉐이프 델타는 정점 수 × 3 조밀 배열이고, 델타가 없는 채널은 비운다. 메시 bounds는 정점 AABB. SMR bounds는 `m_AABB`가 있으면 그대로, 없으면(FBX에서 바로 온 SMR) Unity처럼 뼈마다 바인드 자세 범위 상자를 임포트 자세 뼈로 옮겨 루트 뼈 공간에서 합친다(`IrExport.SkinnedBounds`, Unity 정답 비교: 블렌드쉐이프 없는 메시 85개 중 84개 일치, 블렌드쉐이프가 있으면 Unity가 조금 더 크다).
  - 머티리얼은 `.mat`(또는 Material 하나를 담은 `.asset`) YAML(`m_SavedProperties` v2·v3, `m_ValidKeywords`/`m_ShaderKeywords`, `m_CustomRenderQueue`, 베리언트 `m_Parent`)을 읽고 원문을 `rawYaml`에 둔다. `m_Ints`는 숫자 그대로 `floats`에 넣는다.
  - 머티리얼 키: `.mat`은 GUID, FBX 내장 머티리얼·Default-Material은 `guid:fileID` (IR 버전은 그대로). 내장 머티리얼은 Unity처럼 Standard 셰이더에 FBX 머티리얼 속성(Diffuse 색·텍스처, 불투명도, Bump/NormalMap, Emissive, ReflectionFactor, Shininess)을 옮긴다(`ir/IrFbxMaterials.cs`). 텍스처는 파일 이름으로 FBX 옆 → 옆·위 폴더의 `Textures` → 프로젝트 전체 순서로 찾는다. 확인: `tests/unity/MaterialDumper.cs` 정답과 아바타 B의 FBX 머티리얼 5개 텍스처·색 일치.
  - 셰이더 키(`shaderGuid`): `.shader` 에셋은 GUID, Unity 기본 셰이더처럼 한 GUID에 여럿이 있는 것은 `guid:fileID` (예: Standard = `0000000000000000f000000000000000:46`). 렌더러 셰이더 표와 같은 키다.
  - 텍스처 설정은 `.meta`의 TextureImporter에서 읽는다. 최대 크기는 Standalone 오버라이드 → DefaultTexturePlatform → `maxTextureSize` 순. 폴더 소스는 원본 파일의 절대 경로를 쓰고, 패키지 안 텍스처는 `<이름>.textures/<guid>.<확장자>`로 바이트 그대로 풀어 둔다.
  - 빌드에 `../editor/bin/Protocol.dll`이 필요하다. 없거나 `editor/Protocol/*.cs`보다 오래됐으면 `build.sh`가 에디터의 `build.sh proto`를 먼저 돌린다.
- 해석할 수 없는 부분은 경고로 남는다. 예: 원본 에셋이 없음, 대상이 없는 오버라이드(끊긴 참조).
- (2026-10-21) IR 노드에 `editorOnly: true`(태그 EditorOnly), `outfits[]`에 `parts`(Bone Proxy로 아바타 뼈 아래에 옮겨진 아이템 노드), 아이템 종류 `gimmick`(이름이 기믹이거나 기본으로 꺼진 메시만 1~2개 뼈에 붙음)을 더했다.
- 속도 (2026-10-21): `.meta` 색인 병렬, FBX 기하는 `AssetDb.Geometry`가 guid마다 한 번만 해석(병렬로 미리 채우고 IR을 쓴 뒤 비움), 블렌드쉐이프 노멀·탄젠트 델타는 프레임 병렬. 결과는 직렬과 바이트까지 같다. 2차: FBX 하나 안의 메시도 병렬(`ModelSim.Build`), 프로젝트 폴더 `.meta`의 guid는 `%LOCALAPPDATA%\ALEdit\meta-index\`에 캐시(수정 시각·크기가 같으면 다시 열지 않음, 지워도 다시 만든다). 환경 변수 `VRCPROTO_IMPORT_TIMING=1`이면 단계·FBX·메시마다 걸린 시간을 표준 오류로 낸다 (`src/Timing.cs`).

## 구조

| 파일 | 역할 |
|---|---|
| `src/AssetDb.cs` | `.unitypackage` 읽기(최소 tar.gz 리더, 2회 순회, 텍스처는 필요할 때 읽음)와 프로젝트 폴더 인덱싱 |
| `src/FbxBinary.cs` | 최소 FBX 바이너리 파서 (구조만, 또는 기하 배열까지) |
| `src/ModelSim.cs` | ModelImporter 시뮬레이션: 계층, fileID, Transform 값, 렌더러, 머티리얼 슬롯, 블렌드쉐이프, m_Bones / m_RootBone |
| `src/Space.cs`, `src/Math.cs` | FBX 공간 → Unity 공간 (단위 스케일, 축 변환, X 반전, 피벗·Pre/Post 회전·회전 순서) |
| `src/MeshSim.cs` | 메시 데이터: 삼각형 분할, 정점 분리·병합, 노멀, UV, 컬러, 탄젠트, 스킨 가중치, 바인드포즈, 블렌드쉐이프 델타 |
| `src/MeshAsset.cs` | 메시 에셋(`.asset`, 텍스트 직렬화 Mesh) 읽기: 정점 stream·인덱스·서브메시·블렌드쉐이프·바인드포즈 (2026-10-23) |
| `src/MikkTSpace.cs` | MikkTSpace(mikktspace.c) 이식. 삼각형·사각형, float 계산 |
| `src/NormalCalc.cs` | 블렌드쉐이프 노멀 Calculate 모드 근사 (코너 단위) |
| `src/ExportGeometry.cs` | 기하 JSON 출력 |
| `src/Yaml.cs` | Unity YAML 부분집합 파서 |
| `src/Resolver.cs` | 프리팹 해석: PrefabInstance 펼치기, 오버라이드 적용, 추가·제거 처리 |
| `src/Export.cs` | 트리 JSON 출력 |
| `ir/UnityImport.cs` | 에디터용 API: Assets 색인 → 씬·프리팹 해석 → 루트 목록 → 루트 하나를 IR로 |
| `ir/IrFbxMaterials.cs` | FBX 내장 머티리얼 → Standard 값 (Unity FBXMaterialDescriptionPreprocessor 규칙), 텍스처 파일 이름 찾기 |
| `ir/IrExport.cs` | 에디터 IR v1 출력 (노드·메시·렌더러·머티리얼·텍스처). `Protocol.dll`을 참조하므로 `src/`와 따로 둔다 |
| `src/Xxh64.cs` | XXH64와 fileID 계산 |
| `tests/` | 검증 도구. Unity 정답 덤프(`unity/PrefabDumper.cs`, `unity/GeometryDumper.cs`), 스테이징(`stage.py`), 비교(`compare.py`, `compare_geo.py`, `dn_error.py`) |

## 재현하는 Unity 규칙

모델(FBX) 쪽 규칙은 `research/fileid/RULES.md`에 있다. 프리팹 쪽에서 실험으로 확인한 규칙은 다음과 같다.

- 인스턴스 객체의 fileID는 `(원본 fileID XOR PrefabInstance fileID) & 0x7FFFFFFFFFFFFFFF`다. 실제 프리팹 13,936건에서 확인했다.
- 프리팹 에셋의 루트 이름은 항상 파일 이름이다.
- 오버라이드는 `.Array.size`를 먼저 적용한다. 배열 범위 밖의 `data[i]`는 무시한다. 배열을 늘리면 새 칸은 기본값(0 / None)이다.
- 추가된 자식은 `m_AddedGameObjects` 순서대로 붙는다(`insertIndex: -1` = 맨 뒤). 기록이 없는 구버전 프리팹에서는 원래 자식들 뒤에 `m_RootOrder` 순으로 붙는다.
- SkinnedMeshRenderer의 블렌드쉐이프 가중치 배열은 메시의 블렌드쉐이프 개수에 맞춰진다(모자라면 0).
- FBX의 `DeformPercent`는 `.meta`에 `importBlendShapeDeformPercent: 1`이 있을 때만 초기 가중치가 된다. 키가 없는 구버전 `.meta`에서는 0이다.
- `importBlendShapes: 0`이면 블렌드쉐이프가 없다.
- Animator는 Humanoid이거나, Generic이면서 `avatarSetup: 1`일 때 붙는다.
- `externalObjects`로 외부 `.mat`에 다시 연결된 머티리얼을 따른다.
- 씬(`.unity`)은 stripped 객체 fileID를 XOR 규칙과 다르게 매긴다. stripped 문서의 `m_PrefabInstance`·`m_CorrespondingSourceObject`로 계산한 인스턴스 fileID로 바꿔 읽는다(프리팹 파일에서는 둘이 같다). `--scene <씬> --root <이름> --ir-out <폴더>`로 씬 루트 하나를 IR로 낸다.
- 원본이 없는 PrefabInstance는 `이름 (Missing Prefab with guid: …)` 빈 객체로 둔다. 원본이 없어도 Transform 오버라이드는 이 객체에 적용된다.

### Transform 값 (FBX → Unity)
- FBX 로컬 행렬 = `T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹`. 회전 순서는 `RotationOrder`를 따르고, `RotationActive: 0`이면 Pre/Post 회전과 순서를 무시한다.
- 단위: 위치에만 `s = UnitScaleFactor / 100 × globalScale`(`useFileScale: 1`)을 곱한다. localScale은 그대로다. Blender "FBX All" 방식의 스케일 100도 그대로 남는다.
- 축: 파일 축 체계(Up/Front/Coord)를 Y-up으로 돌리는 회전을 최상위 노드에만 건다(`bakeAxisConversion: 0`). Z-up FBX(의상 B)에서 확인했다.
- 손잡이: X를 뒤집는다. Unity 로컬 = `D·L·D⁻¹`, `D = diag(-s, s, s)`.
- 루트로 접힌 노드는 그 Transform 값이 루트에 들어간다.

### 메시
- 폴리곤은 부채꼴로 나눈다(사각형 포함). X 반전 때문에 감김 방향을 뒤집는다.
- 정점은 위치(float)·최종 스킨 가중치·블렌드쉐이프 델타·노멀·UV·컬러가 모두 같으면 하나로 병합된다(`weldVertices`). 서로 다른 제어점이어도 마찬가지다.
- **허용 오차 weld**: 탄젠트와 블렌드쉐이프 노멀(Calculate)까지 정한 뒤, 위 서명·컬러·탄젠트 w·블렌드쉐이프 노멀 델타가 같고 노멀과 탄젠트가 각각 **2.75°** 안(cos ≥ 0.9988484), **UV 거리(채널마다 L2)가 0.001** 안이면 **먼저 나온 정점** 하나로 합친다(값도 그 정점 것). Unity 결과에서 탄젠트는 합쳐진 쌍 최대 2.7495°, 나뉜 쌍 최소 2.7510°, 노멀은 2.740° 쌍이 합쳐졌다(이전 기준 2.736°는 조금 좁아 아바타 A 등에서 정점이 남았다). UV 는 0.00099 차이까지 합쳐지고, 성분마다 0.001 안이어도 거리가 0.00103 인 쌍(Eku Jacket)은 나뉜다. 노멀이 코너마다 미세하게 다르게 저장된 FBX(아바타 F·아바타 E 본체 등)에서 정점 수가 맞게 된다.
- 서브메시 순서는 폴리곤에서 머티리얼 인덱스가 처음 나오는 순서다.
- 노멀은 FBX 값을 쓴다(`normalImportMode: 0`). 위치·노멀·블렌드쉐이프 델타는 노드의 지오메트릭 변환을 거친 뒤 위 공간 변환을 따른다.
- 스킨: 가중치가 `minBoneWeight`(0.001) 미만인 영향은 버리고(전부 미만이면 가장 큰 하나는 남긴다), 큰 순서로 4개(`maxBonesPerVertex`)를 남겨 합 1로 정규화한다. **어느 클러스터에도 없는 정점은 m_Bones 0번 본에 가중치 1**이다.
- **탄젠트**(`tangentImportMode: 3`): MikkTSpace 를 **원래 폴리곤 단위**(삼각형·사각형, 오각형 이상은 부채꼴 삼각형)로, Unity 공간(뒤집은 감김)에서 돌린다. 코너마다 탄젠트가 다르면 정점을 나눈다. 삼각형으로 나눈 뒤 돌리면 정점의 27%가 0.01~0.03 어긋난다.
- **m_Bones** = 모든 클러스터 본(가중치 0 포함)을 FBX 계층 깊이 우선 순서로 정렬한 것. **m_RootBone** = 자기 아래(자신 포함)에 m_Bones 본이 가장 많은 본, 같으면 m_Bones 순서상 마지막. (단순히 "가장 얕은 본 중 마지막"은 Type_A처럼 최상위 본이 여럿이고 아래 본 수가 다를 때 틀린다.)
- **바인드포즈** = `inv(Unity 공간으로 바꾼 클러스터 TransformLink) × Unity 계층의 메시 노드 월드`.
- 블렌드쉐이프 프레임 가중치는 `FullWeights`, 델타는 FBX Shape의 `Vertices`를 위와 같이 변환한 값이다.
- 블렌드쉐이프 노멀: Import(0)는 FBX 값, None(2)은 0, Calculate(1)는 근사다. Unity 는 셰이프마다 **메시 전체**의 노멀을 다시 계산한다: 셰이프가 움직이지 않은 곳도 델타 = 계산 노멀 − 임포트 노멀이다(의상 B Jacket 의 Breasts_big 은 dv 3,360개, dn 9,712개). **코너 단위로** 노멀을 다시 계산(면적·각도 가중, 스무딩 묶음은 기본 형상의 면 각도로 정함)하고, Unity 처럼 코너마다 델타가 다르면 정점을 나눈다(움직이지 않은 곳의 60° 넘는 모서리도 나뉜다). 그 뒤에 허용 오차 weld 를 한다.
- **탄젠트 모드 0·2**: Unity 값은 `0 = Import`, `1 = Calculate Legacy`, `2 = None`, `3 = MikkTSpace`다. 0(Import)은 FBX에 탄젠트 레이어가 없으면 **탄젠트를 만들지 않는다**(아바타 F·아바타 E 본체가 이 경우). 2는 항상 없다. 프로젝트에서 0인 FBX 4개는 모두 탄젠트 레이어가 없다.
- **탄젠트 모드 1 (레거시, 근사)**: 삼각형 탄젠트·바이탄젠트를 UV 행렬식의 **부호만** 곱한 방향으로 구하고 코너 각도로 가중한다. 정점의 코너를 탄젠트·바이탄젠트가 90° 이내인 묶음으로 나눠 정점을 나누고, 묶음 탄젠트는 **같은 위치(UV·노멀 경계 무시)의 모든 코너 중 묶음 방향과 90° 미만인 것**의 합을 노멀에 직교화한다. w는 묶음 바이탄젠트 합의 방향 부호.
- **`legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes: 1`이면 블렌드쉐이프 노멀·탄젠트 델타가 없다**(모드와 관계없이 0). 프로젝트 FBX 821개 중 455개가 이 설정이다. 키가 없는 구버전 meta 는 1로 본다.
- 블렌드쉐이프 탄젠트 델타: 변형된 위치와 노멀(기본 + 델타)로 MikkTSpace 를 다시 돌려 기본 탄젠트와의 차이(xyz)를 쓴다. 노멀 모드 None 이거나 위 레거시 설정이면 없다.

## 검증 결과 (Unity 2022.3.22f1 실제 로드와 비교)

| 데이터 | 결과 |
|---|---|
| 받은 패키지 4개의 프리팹 (`.unitypackage` 직접 입력) | **71 / 71 완전 일치** (GameObject 14,966개) |
| 패키지와 프로젝트 표본 (seed 7) | 162 / 164 일치 (GameObject 30,917개) |
| 처음 보는 프로젝트 표본 250개와 그 의존 프리팹 (seed 99) | 338 / 340 일치 (GameObject 56,857개) |

비교 항목은 계층(이름·순서), active, **localPosition / localRotation / localScale**, 컴포넌트 목록과 순서, enabled, 메시 참조, 머티리얼 참조, 블렌드쉐이프 이름과 값, Avatar 참조, **SMR m_Bones / m_RootBone**이다.
불일치 4건은 모두 원본 프리팹이 없는(Missing Prefab) 경우로, Unity가 만드는 자리표시 자식(`Placeholder for referenced Transform…`)과 그 순서까지는 재현하지 않았다.

### 모델 Transform 값과 메시 데이터 (FBX 30개: 실제 21 + 경계 사례 9)

Unity가 정점·삼각형 순서를 최적화하므로, 정점은 위치·노멀·UV0·탄젠트의 허용 오차 최근접으로 매칭해 비교했다.

| 항목 | 결과 |
|---|---|
| 노드 localPosition / localRotation / localScale | **4,606 / 4,606** (Z-up, 스케일 100 방식 포함) |
| m_Bones / m_RootBone | **306 / 306**, **306 / 306** |
| 서브메시 수, 블렌드쉐이프 이름·순서, 프레임 가중치 | 315 / 315, 315 / 315, 4,468 / 4,468 |
| 정점 (위치·노멀·UV0·탄젠트) | Unity 정점 1,304,773개 중 1,297,100개(99.4%)가 대응 (이전 1,295,318). 대응 없는 쪽은 탄젠트가 다른 정점 |
| 정점 수가 같은 메시 | **247 / 315** (weld 기준 2.75°·UV 거리·메시 전체 Calculate 전 203, 허용 오차 weld 전 151) |
| **탄젠트** | 대응된 1,221,391개 전부 일치 (탄젠트 있는 정점 기준 약 99.4%) |
| 본 가중치 | 1,297,411 / 1,297,423 (99.999%) |
| 바인드포즈 | 72,932 / 73,172 (99.7%). 나머지는 비균일 스케일 본에서 0.002 이내 차이 |
| 블렌드쉐이프 델타 정점 | **6,130,057 / 6,130,057** (대응 정점 수에 따라 전체가 조금 바뀐다. 비교 도구가 겹친 정점을 델타로 짝짓게 고친 뒤. 이전 99.94%의 차이는 윗·아랫입술처럼 같은 자리 정점을 잘못 짝지은 비교 오류) |
| 삼각형 (감김 포함) | 1,882,247 / 1,911,095 (98.5%, 이전 98.4%, 허용 오차 weld 전 95.4%). 나머지는 같은 위치의 다른 정점 번호를 가리키는 차이 |
| 블렌드쉐이프 델타 노멀·탄젠트 유무 | Unity 에 없는 메시(레거시 설정 89개)에 sim 이 넣은 경우 **0** |
| 블렌드쉐이프 델타 노멀 (Calculate 근사) | 오차 50 / 75 / 90%: **0.008 / 0.028 / 0.081** (메시 전체 계산 전 0.011 / 0.053 / 0.47, 0으로 둘 때 0.080 / 0.60 / 0.83) |
| 블렌드쉐이프 델타 탄젠트 (근사) | 오차 50 / 75 / 90%: **0.004 / 0.015 / 0.049** (메시 전체 계산 전 0.005 / 0.026 / 0.19, 0으로 둘 때 0.054 / 0.23 / 0.71) |

### 탄젠트 모드 0·1·2 (프로젝트 FBX 9개: 아바타 F·아바타 E 본체 포함)

| 항목 | 결과 |
|---|---|
| 모드 0·2 (7개) 탄젠트 유무 | Unity·sim 모두 탄젠트 없음, **일치** |
| 모드 1 탄젠트 유무 | 4개 메시 **일치** |
| 모드 1 탄젠트 오차 50 / 90 / 99% | **0.00013 / 0.054 / 0.79** (처음 단순 구현 0.0072 / 0.32 / 1.92). VRChat SDK 예제 블록 2개는 완전 일치 |
| 모드 1 w 부호 | 10,289개 중 145개 다름 (대부분 Type_A) |
| 모드 1 정점 수 | WH-TACTIX: Unity 4,387 / sim 4,333 (Unity가 54개 더 나눔. weld 기준을 2.75°·UV 거리로 넓히기 전 4,337) |
| 원래 30개 FBX 출력 | 변경 전과 **완전히 같음** (회귀 없음) |
| 모드 0·2 메시 정점 수 (허용 오차 weld 후) | 아바타 F Body: Unity 7,433 / sim 7,433 (weld 전 8,727) |
| 정점 수가 같은 메시 (9개 FBX) | **58 / 61** (weld 기준 2.75°·UV 거리 전 48) |
| 델타 정점 (9개 FBX) | **1,673,625 / 1,673,625** |
| Type_A 본 가중치·m_RootBone | 가중치 없던 5,556개 포함 본 가중치 전부 일치, m_RootBone 59 / 59 |

### 업로드 패키지 (아바타 패키지 + 의상 패키지, 메시 32개)

`VrcProtoUpload` 업로드 프로젝트의 Unity 임포트와 IR 출력 비교. 아바타 A 은 `blendShapeNormalImportMode: 2`(None), 의상 B 은 1(Calculate).

| 항목 | 결과 |
|---|---|
| 정점 수가 같은 메시 | **26 / 32** (이전 12). Body_base 14,691, Body 7,251 등 아바타 A 메시는 Socks(+2) 빼고 전부 |
| 남은 차이 (의상 B) | Jacket 9,882 / 9,926 (이전 9,077), T_shirt 1,586 / 1,491, Collar −4, Jacket_wing +5, Tie −1. 아래 Unity 노멀 계산 축 벡터 때문 |

### 스킨 경계 사례 (Blender로 만든 FBX 3개, `research/fileid/blender/make_skin_cases.py`)

가중치 없는 정점, 최상위 본 여럿(이름순과 파일 순서가 반대), 가중치 없는 최상위 본 아래 자식 본만 가중치가 있는 경우. 정점·삼각형·본 가중치·m_Bones·m_RootBone **전부 일치**.

정점 1:1 이 아닌 부분:
- sim 쪽에만 남은 정점 10,457개(0.8%, 이전 12,170개, 허용 오차 weld 전 42,826개): 주로 Calculate 모드 블렌드쉐이프가 많은 메시에서 sim 이 코너 노멀 차이로 정점을 Unity 보다 더 나눈 것. 그려지는 결과는 같고 정점 수만 많다.
- Unity 쪽에만 있는 중복 563개(0.04%, 이전 2,333개. 메시 전체 Calculate 로 줄었다).
- **Unity 의 노멀 계산 축 벡터**: Unity 의 노멀 계산(Calculate)은 일부 코너에 정확한 축 벡터 (±1,0,0)·(0,±1,0)·(0,0,±1) 를 낸다(`normalImportMode: 1`로 기본 메시를 가져와도 의상 B Shoes 582개, Jacket 370개). 같은 삼각형의 코너마다 다른 축이 나오기도 하고, 면 크기·각도로는 갈리지 않아 규칙을 찾지 못했다. Calculate 블렌드쉐이프 메시(의상 B)의 남은 정점 수 차이는 대부분 이것이다 (의상 B T_shirt Unity 1,586 / sim 1,491, Jacket 9,882 / 9,926).

속도: 30개 모델 기하 출력에 약 50초가 걸린다(대부분 JSON 쓰기). 프리팹 구조만 해석할 때는 기하 배열을 풀지 않는다.

속도: 패키지 4개(372MB 이상)와 프로젝트 폴더(에셋 27,000개)를 로드하고 프리팹 71개를 해석하는 데 약 6초가 걸렸다.

## 아직 하지 않은 것
- Calculate 모드 블렌드쉐이프 노멀·탄젠트의 정확한 재현 (상위 5% 정점은 오차가 큼. Unity 노멀 계산의 축 벡터 포함). 해당 메시는 레거시 설정이 아닌 경우뿐이다(테스트 메시 15개)
- lilToon 실제 렌더링 비교 (뷰어 단계에서)
- 탄젠트 0.6% 불일치, 정점 병합 차이(sim 쪽 0.9% 많음), 가중치 경계값(0.001 근처) 처리 10여 개
- 허용 오차 weld 기준(2.75°, UV 거리 0.001)은 Unity 결과에서 잰 경계값이다. 아바타 F Outer 에서 54° 차이인 쌍 4개, 아바타 A Socks 에서 탄젠트 2.764° 쌍이 합쳐지는 예외는 설명하지 못했다. 합쳐진 정점 값을 Unity 가 먼저 나온 것이 아닌 쪽으로 고르는 경우(Simple Twintail 등 수십 개)도 있다
- **정점 순서**: Unity 정점 순서는 최적화된 삼각형 목록에서 처음 쓰인 순서이고(`meshOptimizationFlags: -1`), 삼각형 순서는 Unity 의 vertex cache 최적화 결과라 재현하지 않았다(Forsyth 기본형과도 다름). `meshOptimizationFlags: 0`이어도 정점 순서는 FBX 순서가 아니다. sim 은 FBX 폴리곤 순서다. 정점 번호로 Unity 메시와 맞출 때는 속성으로 짝지어야 한다(`tests/compare_geo.py`의 `match_vertices`)
- Unity 델타 정점은 길이 1e-5 미만이 대부분 없다(의상 B 은 1e-6 도 있음). sim 은 작은 델타도 내지만 그리는 결과에는 영향이 없다
- 탄젠트 모드 1(레거시)의 정확한 재현: 상위 1% 정점은 방향이 크게 다르고, 정점 나눔도 일부 다르다. 해당 FBX는 프로젝트에서 아바타·의상 2개(WH-TACTIX, Celeste Garter Type_A)와 SDK 예제 블록 20개
- 모드 0에서 FBX에 탄젠트 레이어가 있는 경우(그 값을 가져와야 함): 프로젝트에 해당 파일이 없어 미구현
- 노멀 계산 모드(`normalImportMode: 1`, 프로젝트 FBX 821개 중 1개), `bakeAxisConversion: 1`(2개), 스무딩 그룹 기반 노멀 재계산 옵션
- ASCII FBX, 구버전 `fileIDToRecycleName` 표 조회, Missing Prefab 자리표시의 세부 동작
- MonoBehaviour(Modular Avatar, VRC 컴포넌트)는 스크립트 guid와 원시 필드만 유지한다.
