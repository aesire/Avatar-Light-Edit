VrcProto 기본 셰이더 팩 (렌더러와 함께 배포)
================================================

사용자 PC에 Unity가 없어도 아래 셰이더로 아바타를 볼 수 있도록 미리 컴파일한 AssetBundle입니다.
만드는 법: editor/build_shaderpacks.sh (변형 목록: Tools/ShaderPack/bundled/poiyomi.spec)

들어 있는 것 (모두 MIT 라이선스, 전문은 licenses 폴더)
  · poiyomi_<버전>.bundle   Poiyomi Toon 무료판 8.0 · 8.1 · 9.0 · 9.1 · 9.2 · 9.3 · 10.0
                            (Poiyomi Labs. lilToon·LTCGI·RED_SIM·Silent·Uzumore 코드 일부 포함)
                            기능 키워드가 많아 자주 쓰는 조합만 들어 있다 → 그 밖의 조합은 가장 가까운 것으로 그리고
                            "근사 표시"로 알린다. Unity가 있으면 원본 프로젝트의 셰이더로 정확한 팩을 만들 수 있다.
  · vrm_mtoon.bundle        VRM/MToon (Masataka SUMI / VRM Consortium), UniGLTF/UniUnlit (ousttrue)

렌더러 빌드에 직접 들어 있는 것 (팩 없음)
  · lilToon (lilxyzw, MIT)
  · Unity 기본 셰이더 (Standard, Unlit, Legacy 등 — Unity 런타임의 일부)

넣지 않은 것 (재배포 허락이 확인되지 않음 → 사용자가 자기 Unity로 팩을 만든다)
  · 유료·개별 라이선스 셰이더
  · Poiyomi Pro (Patreon 전용)
