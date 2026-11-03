#!/bin/sh
# Unity 2022.3에 들어 있는 Roslyn 컴파일러로 빌드 (.NET SDK 불필요).
# 결과: bin/ImportSim.Core.dll  라이브러리 (src/ + ir/, netstandard 2.1) — 에디터 Host도 이것을 참조한다
#       bin/importsim.dll       명령줄 (+ runtimeconfig) — 실행: dotnet bin/importsim.dll ... 또는 bin/importsim.cmd
set -e
cd "$(dirname "$0")"
UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data}"
DOTNET="${DOTNET:-dotnet}"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
NETSTD="$(cygpath -w "$UNITY/NetStandard/ref/2.1.0/netstandard.dll" 2>/dev/null || echo "$UNITY/NetStandard/ref/2.1.0/netstandard.dll")"
COMMON="-nologo -langversion:9 -nullable:enable -optimize+ -nowarn:CS8632"
# IR 출력(ir/)은 에디터와 같은 IR 정의를 쓴다: ../editor/bin/Protocol.dll (없거나 오래됐으면 에디터 쪽 proto 만 빌드)
PROTO=../editor/bin/Protocol.dll
if [ ! -f "$PROTO" ] || [ -n "$(find ../editor/Protocol -name '*.cs' -newer "$PROTO" 2>/dev/null)" ]; then sh ../editor/build.sh proto; fi
mkdir -p bin
"$DOTNET" "$CSC" $COMMON -target:library -r:"$NETSTD" -r:"$PROTO" -out:bin/ImportSim.Core.dll src/*.cs ir/*.cs
"$DOTNET" "$CSC" $COMMON -target:exe -r:"$NETSTD" -r:"$PROTO" -r:bin/ImportSim.Core.dll -out:bin/importsim.dll cli/*.cs
cp "$PROTO" bin/
# GC는 에디터 Host와 같게 서버 GC + DATAS (병렬 FBX 해석이 워크스테이션 GC에서 크게 느림 — ../editor/build.sh 참고)
cat > bin/importsim.runtimeconfig.json <<'EOF'
{ "runtimeOptions": { "tfm": "net6.0", "rollForward": "Major",
  "framework": { "name": "Microsoft.NETCore.App", "version": "6.0.0" },
  "configProperties": { "System.GC.Server": true, "System.GC.DynamicAdaptationMode": 1 } } }
EOF
printf '@dotnet "%%~dp0importsim.dll" %%*\r\n' > bin/importsim.cmd
echo "built bin/ImportSim.Core.dll, bin/importsim.dll"
