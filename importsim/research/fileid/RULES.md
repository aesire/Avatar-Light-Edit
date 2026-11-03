# Unity ModelImporter 재현 규칙 (검증 결과)

FBX + `.meta`만으로 Unity 2022.3.22f1(VRChat 지정 버전)이 만드는 모델 프리팹 계층과 fileID를 재현한다.
구현: `../importsim/` (C# 독립 CLI, 정식 구현). `../proto/unity_model.py`는 Python 프로토타입이다.
프리팹(중첩, Variant, 오버라이드) 규칙과 검증 결과는 `../importsim/README.md`에 있다.

## 검증 범위

| 검증 | 결과 |
|---|---|
| Unity 실제 임포트와 대조 (FBX 30개: 실제 아바타/의상/헤어 21개 + Blender 경계 사례 9개) | 객체 **9,929개 전부 일치**, 렌더러 머티리얼 슬롯 **315개 전부 일치** |
| 받은 패키지 3개 (Unity 없이 패키지에서 직접) | 프리팹 → FBX 참조 68건 전부 해석 |
| 프로젝트 전체 (FBX 808개, 프리팹/씬 4,938개) | FBX 참조 213,443건 중 **98.98% 해석**. 나머지는 Unity 실제 임포트에도 없는 끊긴 참조(FBX 업데이트 후 남은 오버라이드). 실패가 많은 FBX 7개를 Unity로 확인한 결과 우리 재구성과 Unity 결과가 완전히 같았다. |

## 1. fileID 계산

```
fileID = signed int64( XXH64( UTF-8("Type:{타입}->{식별자}0"), seed = 0 ) )
```

| 대상 | 식별자 |
|---|---|
| GameObject | `//RootNode/root/{Unity 경로}` (루트는 항상 `root`, 즉 루트 = `//RootNode/root`) |
| 컴포넌트 | `{GameObject 식별자}/{타입}` (Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, Animator 확인) |
| Mesh | FBX 노드 이름 |
| Material (내장) | 머티리얼 이름 |
| Avatar | 고정 `9000000` |
| 모델 프리팹 자체 (`m_SourcePrefab`) | 고정 `100100000` |

- 루트 GameObject는 항상 `919132149155446097`, 루트 Transform은 항상 `-8679921383154817045`이다.
- 일본어와 기호는 UTF-8 그대로 해시한다.

## 2. FBX → Unity 계층

1. FBX `Model` 노드 하나가 GameObject 하나가 된다. 부모 관계는 `Connections`의 OO 연결을 따른다.
2. **최상위 Model이 하나뿐이면 루트로 접힌다**(`preserveHierarchy: 0`일 때). 그 노드의 컴포넌트는 루트에 붙고, 자식은 루트의 자식이 된다. 루트의 이름은 파일 이름이다.
3. **같은 부모 아래 이름이 겹치면 연결 순서상 뒤에 오는 쪽이 `이름 1`, `이름 2`…로 바뀐다.** 부모가 다르면 겹쳐도 그대로 둔다.
4. Unity 계층의 형제 순서는 이름순이다. 이 순서는 fileID에 영향을 주지 않는다.

## 3. 컴포넌트와 하위 에셋

- Mesh 지오메트리가 달린 노드:
  - Skin 또는 BlendShape 디포머가 있으면 SkinnedMeshRenderer가 붙는다. 스킨이 없어도 블렌드쉐이프만 있으면 이쪽이다.
  - 둘 다 없으면 MeshFilter와 MeshRenderer가 붙는다.
- **Mesh 이름은 FBX 노드 이름이다.** 루트로 접혔거나 ` 1`이 붙어 이름이 바뀌어도 원래 이름을 쓴다. FBX 안 지오메트리의 이름은 쓰지 않는다.
- Animator는 Humanoid(`animationType: 3`)이거나, Generic(`2`)이면서 `avatarSetup: 1`일 때 루트에 붙는다.
- 블렌드쉐이프 초기 가중치는 FBX `DeformPercent`다. 단, `.meta`에 `importBlendShapeDeformPercent: 1`이 있을 때만이고, 키가 없는 구버전 `.meta`에서는 0이다. `importBlendShapes: 0`이면 블렌드쉐이프가 없다.
- 멀티 머티리얼 메시는 Mesh 하나에 서브메시 여러 개로 들어간다(`_MeshPart` 분할 없음).

## 4. 머티리얼 슬롯과 서브메시 (프리팹의 `m_Materials[i]` 매핑)

- **서브메시 순서는 폴리곤 배열(`LayerElementMaterial/Materials`)에서 머티리얼 인덱스가 처음 나오는 순서다.** 슬롯 순서가 아니다.
- 폴리곤이 쓰지 않는 슬롯은 버려진다.
- `MappingInformationType: AllSame`이면 첫 슬롯 하나만 남는다.
- 머티리얼이 없으면 `Default-Material` 슬롯 하나가 된다.
- `materialImportMode: 0`이면 슬롯 수는 같고, 모든 슬롯이 `Default-Material`이다.
- **내장 Material 하위 에셋**은 폴리곤이 실제로 쓰는 머티리얼만 만들어진다. `.meta`의 `externalObjects`로 외부 머티리얼에 다시 연결된 것은 제외된다.

## 5. 구버전 `.meta` 표
- `internalIDToNameTable`: 특정 객체의 ID만 덮어쓰는 표다(예: AnimationClip `74`). 나머지 객체는 해시 규칙을 따른다.
- `fileIDToRecycleName`(2019.2 이전 임포트): `클래스ID × 100000 + 2n` 형식의 ID와 **노드 이름**(경로가 아님)의 표다. 프로젝트 FBX 813개 중 2개(아바타 아님)만 해당한다. 표 조회는 아직 구현하지 않았다.

## 아직 확인하지 않은 것
- 노드 이름이 같은 Mesh 두 개(서로 다른 부모 아래)의 Mesh 이름 처리 (재구성 시 경고 출력)
- `_MeshPart` 분할 조건 (아바타에서는 관찰되지 않음)
- ASCII FBX: 프로젝트에 2개. 최종 툴에서 ufbx를 쓰면 해결된다.

## 파일
- `../fileid_hash.py` — XXH64와 fileID 계산
- `unity/FileIdDumper.cs` — Unity 기준값 덤프 (batchmode)
- `blender/make_edge_cases.py` — 경계 사례 FBX 생성
- `verify.py`, `check_prefab_refs.py` — 1차 규칙 검증 (Unity 덤프 기반)
- `dump/` — Unity 기준값 (`FileIdDumper.cs`로 각자 만든다. 받은 에셋에서 나온 것이라 저장소에는 넣지 않는다)
- `../proto/` — Unity 없이 재구성하는 프로토타입 (아래)
  - `unitypackage.py` — 패키지 인덱싱과 지연 읽기
  - `fbx_binary.py` — 최소 FBX 바이너리 파서
  - `unity_model.py` — FBX + meta → Unity 계층과 fileID
  - `compare_dump.py`, `compare_materials.py` — Unity 덤프와 대조
  - `resolve_package.py` — 패키지 안 프리팹 참조 해석
  - `scan_project.py` — 프로젝트 전체 검증 (읽기 전용)
