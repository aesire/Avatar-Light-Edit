#!/bin/sh
# .NET SDK 없이 Unity 2022.3에 들어 있는 Roslyn 컴파일러로 빌드한다 (importsim과 같은 방식).
#   bin/Protocol.dll           netstandard2.1 — Host와 Unity 양쪽이 쓰는 메시지·IR 정의 (Unity/Assets/Plugins에도 복사)
#   bin/HostTests.dll          Document/Changeset/Undo/프로토콜 단위 테스트 — 실행: dotnet bin/HostTests.dll
#   bin/MakeSampleIr.dll       테스트용 IR 생성기, bin/E2E.dll 렌더러 왕복 확인
#   bin/av/VrcProtoAv.dll      Avalonia Host — 실행: bin/av/VrcProtoAv.cmd
set -e
# 사용: sh build.sh [proto] [tests] [tools] [shoot] [av]  (인자 없으면 전부)
WANT="${*:-proto tests tools shoot av}"
want() { case " $WANT " in *" $1 "*) return 0;; esac; return 1; }
cd "$(dirname "$0")"
UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data}"
DOTNET="${DOTNET:-dotnet}"
NETVER="${NETVER:-8.0.27}"
SHARED="${SHARED:-/c/Program Files/dotnet/shared}"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
w() { cygpath -w "$1" 2>/dev/null || echo "$1"; }

mkdir -p bin obj
# 띄워 둔 촬영 서버(Tools/Shoot)는 Protocol.dll·Shoot.dll을 잠그므로 먼저 끝낸다
if [ -f bin/Shoot.dll ]; then "$DOTNET" bin/Shoot.dll stop >/dev/null 2>&1 || true; fi

# 관리 어셈블리만 참조 목록에 넣는다 (네이티브 DLL 제외)
refs() {
  for f in "$1"/*.dll; do
    case "$(basename "$f")" in
      *Native*|*_cor3.dll|clr*.dll|coreclr.dll|hostpolicy.dll|mscor*.dll|msquic.dll) ;;
      *) echo "-r:\"$(w "$f")\"" ;;
    esac
  done
}
# 응답 파일은 임시 파일에 쓴 뒤 바꿔 넣는다 (빌드 두 개가 겹쳐도 csc가 반쯤 쓴 파일을 읽지 않게)
refs "$SHARED/Microsoft.NETCore.App/$NETVER" > obj/net.rsp.$$ && mv -f obj/net.rsp.$$ obj/net.rsp

COMMON="-nologo -langversion:9 -nullable:enable -optimize+ -nowarn:CS8632,CS1701,CS1702"

# GC: 서버 GC + DATAS(힙 수를 부하에 맞춤). 가져오기의 FBX 병렬 해석이 워크스테이션 GC에서는 큰 배열 할당에 막혀
# 두 배 가까이 느렸다 (큰 시험 장면: FBX 단계 4.1 → 3.0초, 전체 10.7 → 8.8초, 최고 메모리 같음 1.8 GB — 2026-10-21).
# DATAS를 적어 두어야 .NET 8에서도 코어마다 힙을 만들지 않는다 (.NET 9부터는 기본).
runtimeconfig() { # $1 = 출력 경로(확장자 없이)
  cat > "$1.runtimeconfig.json" <<EOF
{ "runtimeOptions": { "tfm": "net8.0", "rollForward": "LatestMajor",
  "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
  "configProperties": { "System.GC.Server": true, "System.GC.DynamicAdaptationMode": 1 } } }
EOF
}
netexe() { # $1 = 출력 이름, 나머지 = 소스
  name="$1"; shift
  "$DOTNET" "$CSC" $COMMON -target:exe @obj/net.rsp -r:bin/Protocol.dll -out:"bin/$name.dll" "$@"
  runtimeconfig "bin/$name"
}

if want proto || [ ! -f bin/Protocol.dll ]; then
echo "[proto] Protocol.dll (netstandard2.1)"
"$DOTNET" "$CSC" $COMMON -target:library \
  -r:"$(w "$UNITY/NetStandard/ref/2.1.0/netstandard.dll")" \
  -out:bin/Protocol.dll Protocol/*.cs
mkdir -p Unity/Assets/Plugins
cp bin/Protocol.dll Unity/Assets/Plugins/VrcProto.Protocol.dll
fi

if want tests; then
echo "[tests] HostTests.dll"
netexe HostTests Host/Core/*.cs Tools/Shoot/SceneSpec*.cs Tools/Shoot/DocEdit.cs Tests/*.cs
fi

if want tools; then
echo "[tools] MakeSampleIr.dll, E2E.dll"
netexe MakeSampleIr Tools/MakeSampleIr/*.cs
netexe E2E Host/Core/*.cs Tools/E2E/*.cs
netexe MakeRelease Tools/Release/*.cs
fi

# 창 없는 촬영 도구 (2026-11-01). 배포본(bin/av)에 넣지 않는다
if want shoot; then
echo "[shoot] Shoot.dll"
# 가져오기(import)에 importsim 라이브러리를 쓴다
SHOOT_IMP=../importsim/bin/ImportSim.Core.dll
if [ ! -f "$SHOOT_IMP" ]; then sh ../importsim/build.sh; fi
"$DOTNET" "$CSC" $COMMON -target:exe @obj/net.rsp -r:bin/Protocol.dll -r:"$SHOOT_IMP" -out:bin/Shoot.dll Host/Core/*.cs Tools/Shoot/*.cs AvHost/Items.cs
runtimeconfig bin/Shoot
cp "$SHOOT_IMP" bin/
mkdir -p bin/shoot-poses && cp Tools/Shoot/poses/*.pose.json bin/shoot-poses/ 2>/dev/null || true
fi

# Avalonia Host. NuGet 패키지는 packages/ 에 미리 펼쳐 둔다:
#   python -I Tools/NuGet/fetch_nuget.py packages Avalonia=11.3.13 Avalonia.Desktop=11.3.13 Avalonia.Themes.Fluent=11.3.13
if want av; then
echo "[av] bin/av/VrcProtoAv.dll (Avalonia 11.3)"
mkdir -p bin/av
for f in packages/lib/*.dll; do echo "-r:\"$(w "$f")\""; done > obj/av.rsp.$$ && mv -f obj/av.rsp.$$ obj/av.rsp
# Unity 씬·프리팹 가져오기는 importsim 라이브러리를 쓴다 (없거나 소스가 더 새로우면 importsim 을 먼저 빌드)
IMPORTSIM=../importsim/bin/ImportSim.Core.dll
if [ ! -f "$IMPORTSIM" ] || [ -n "$(find ../importsim/src ../importsim/ir bin/Protocol.dll -newer "$IMPORTSIM" 2>/dev/null)" ]; then sh ../importsim/build.sh; fi
# 앱 로고(branding/)는 dll 리소스로 — 창 아이콘·제목 표시줄 (2026-11-02)
"$DOTNET" "$CSC" $COMMON -target:winexe @obj/net.rsp @obj/av.rsp -r:bin/Protocol.dll -r:"$IMPORTSIM" \
  -resource:branding/ALEdit.ico,ALEdit.ico -resource:branding/png/logo-64.png,ALEdit-64.png \
  -out:bin/av/VrcProtoAv.dll AvHost/*.cs Host/Core/*.cs Host/Shared/*.cs
cp packages/lib/*.dll packages/native/*.dll bin/Protocol.dll "$IMPORTSIM" bin/av/
# 화면 글 표 (AvHost/Texts.cs). 고치는 곳은 editor/texts/ko·en·ja.tsv (화면 언어별) — 테스트 모드는 그 파일을 바로 읽는다
mkdir -p bin/av/texts && cp texts/*.tsv bin/av/texts/
# 사용 안내·이용 조건·오픈소스 고지 (설정 › 정보에서 연다)
cp release/README.txt release/README.en.txt release/README.ja.txt release/LICENSE.txt release/THIRD_PARTY_NOTICES.txt bin/av/
cp ../LICENSE bin/av/COPYING.txt   # GPLv3 전문 (2026-11-02, 배포판에 함께 넣어야 한다)
runtimeconfig bin/av/VrcProtoAv
printf '@start "" dotnet "%%~dp0VrcProtoAv.dll" %%*\r\n' > bin/av/VrcProtoAv.cmd
# 셰이더 팩 빌더 (Host가 전용 Unity 프로젝트에 복사해 batchmode로 부른다)
mkdir -p bin/av/ShaderPack && cp Tools/ShaderPack/ShaderPackBuilder.cs bin/av/ShaderPack/
# 업로드 프로젝트용 에디터 스크립트 (Host가 업로드 프로젝트의 Assets/VrcProto/Editor에 복사해 batchmode로 부른다)
mkdir -p bin/av/Upload && cp Tools/Upload/VrcProtoUpload.cs bin/av/Upload/
# lilToon 번역 파일 (MIT) — 머티리얼 페이지의 프로퍼티 이름 표시
LIL=Unity/Packages/jp.lilxyzw.liltoon/Editor
mkdir -p bin/av/Localization
cp "$LIL/Localization/en-US.po" "$LIL/Localization/ko-KR.po" "$LIL/Localization/ja-JP.po" bin/av/Localization/ 2>/dev/null || true
# lilToon이 코드에서 조합하는 표시 이름 (sHSVGs → Hue|Saturation|Value|Gamma 등)
grep -o 'loc\["s[A-Za-z0-9]*"\] *= *BuildParams(.*);' "$LIL/lilLanguageManager.cs" 2>/dev/null | sed -E 's/loc\["([^"]+)"\] *= *BuildParams\((.*)\);/\1\t\2/; s/GetLoc\("([^"]*)"\)/\1/g; s/"([^"]*)"/=\1/g; s/, */|/g' > bin/av/Localization/compound.txt || true
fi
echo "done"
