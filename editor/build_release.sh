#!/bin/sh
# 배포 zip 만들기 (2026-11-01): 에디터·도구 빌드 → 단위 테스트 → 렌더러 빌드 → Tools/Release로 dist/ 에 zip.
#   sh build_release.sh --version 0.1.0 --repo owner/name   (저장소가 아직 없으면 --no-repo)
#   --skip-renderer  이미 빌드한 Unity/Build를 그대로 쓴다
# 렌더러는 Unity Application Patcher로 고친 UnityPlayer.dll이어야 한다 (editor/README.md "배포" — CVE-2025-59489).
#   Unity/PatchedPlayer에 고친 사본이 있으면 build_renderer.sh가 덮고, 원본이면 MakeRelease가 실패한다.
set -e
cd "$(dirname "$0")"
SKIP=0
for a in "$@"; do [ "$a" = "--skip-renderer" ] && SKIP=1; done
sh build.sh proto tests tools shoot av
dotnet bin/HostTests.dll
if [ "$SKIP" = 0 ]; then sh build_renderer.sh; fi
dotnet bin/MakeRelease.dll "$@"
