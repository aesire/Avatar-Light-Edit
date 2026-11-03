#!/bin/sh
# Unity 렌더 서버 빌드 (처음엔 셰이더 컴파일로 오래 걸린다). 결과: Unity/Build/VrcProtoRenderer.exe
set -e
cd "$(dirname "$0")"
# 띄워 둔 촬영 서버(Tools/Shoot)의 렌더러가 Build 폴더를 잠그므로 먼저 끝낸다
if [ -f bin/Shoot.dll ]; then dotnet bin/Shoot.dll stop >/dev/null 2>&1 || true; fi
UNITY_EXE="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Unity.exe}"
[ -f Unity/Assets/Plugins/VrcProto.Protocol.dll ] || sh build.sh proto  # Host 실행 중엔 bin/Protocol.dll이 잠겨 있으므로 필요할 때만
"$UNITY_EXE" -batchmode -nographics -quit -projectPath "$(cygpath -w "$PWD/Unity")" \
  -executeMethod VrcProto.Editor.BuildPlayer.Build -logFile "$(cygpath -w "$PWD/Unity/build.log")" || { echo "build failed — see Unity/build.log"; exit 1; }
# 스크립트 컴파일 오류가 나도 Unity는 이전 어셈블리로 "Succeeded"를 낼 수 있다 (새 파일을 가져오기 전에 컴파일한 경우 등)
if grep -q "error CS" Unity/build.log; then grep "error CS" Unity/build.log | sort -u; echo "script errors — see Unity/build.log (새 파일이면 한 번 더 빌드)"; exit 1; fi
# 보안 패치 (2026-11-03, CVE-2025-59489): 빌드는 고치기 전 UnityPlayer.dll을 다시 넣으므로, 패처로 고쳐 둔 사본이 있으면 덮는다
UNPATCHED=c2871ee449bef29e658380bf47f1a23839abde7d1e4dcb9bc1ada7a93ae08da6  # 2022.3.22f1 win64 mono 원본
if [ -f Unity/PatchedPlayer/UnityPlayer.dll ] && [ "$(sha256sum Unity/Build/UnityPlayer.dll | cut -d' ' -f1)" = "$UNPATCHED" ]; then
  cp Unity/PatchedPlayer/UnityPlayer.dll Unity/Build/UnityPlayer.dll && echo "patched UnityPlayer.dll (Unity/PatchedPlayer)"
fi
echo "built Unity/Build/VrcProtoRenderer.exe"
