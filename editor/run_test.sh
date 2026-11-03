#!/bin/sh
# 에디터를 테스트 모드로 띄운다 (RunMode.Test, AvHost/Program.cs).
#   · 설정은 %APPDATA%\ALEdit\test\avhost.json에 따로 둔다 — 평소 쓰는 설정(쉬운/전문가 모드·카메라 위치 등)을 건드리지 않는다.
#   · 화면 글 표는 저장소의 texts/ko·en·ja.tsv를 바로 읽고, 저장할 때마다 다시 읽어 화면에 반영한다 (빌드 필요 없음).
#     Ctrl+Shift+R 다시 읽기 · Ctrl+Shift+T 원문 ↔ 바꾼 글. 창 제목에 [테스트].
#   · 소스가 빌드보다 새로우면 먼저 Host를 빌드한다 (--build면 항상).
# 사용: sh run_test.sh [--build] [x.ir.json | 세션.alesav]   (파일을 주지 않으면 sample/sample.ir.json)
set -e
cd "$(dirname "$0")"
if [ "$1" = "--build" ]; then shift; sh build.sh av
elif [ ! -f bin/av/VrcProtoAv.dll ] || [ -n "$(find AvHost Host Protocol -name '*.cs' -newer bin/av/VrcProtoAv.dll 2>/dev/null | head -1)" ]; then
  echo "[run_test] 소스가 바뀌어 Host를 빌드해요"; sh build.sh av
fi
mkdir -p bin/av/texts && cp texts/*.tsv bin/av/texts/
FILE="${1:-sample/sample.ir.json}"
echo "[run_test] 테스트 모드: $FILE · 글 표 texts/(언어).tsv"
exec dotnet bin/av/VrcProtoAv.dll --test --texts texts "$FILE"
