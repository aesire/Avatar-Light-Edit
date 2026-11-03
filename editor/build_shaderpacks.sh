#!/bin/sh
# 배포용 기본 셰이더 팩 (sh build_shaderpacks.sh [poiyomi|mtoon|all] [버전…]) → Unity/Assets/StreamingAssets/ShaderPacks
# 렌더러 빌드(build_renderer.sh)가 이 폴더를 그대로 플레이어에 넣는다 → 사용자 PC에 Unity가 없어도 쓸 수 있다.
# 재배포가 허용된 셰이더만 넣는다 (라이선스: ShaderPacks/licenses, 목록: ShaderPacks/README.txt).
#   · Poiyomi Toon (무료판, MIT)   8.0 · 8.1 · 9.0 · 9.1 · 9.2 · 9.3 · 10.0 (Two Pass는 9.3·10.0, Outline은 8.x)
#   · VRM MToon · UniUnlit (UniVRM, MIT)
#   · lilToon(MIT)과 Unity 기본 셰이더는 렌더러 빌드에 직접 들어 있어 팩이 필요 없다.
# 원본 (읽기만 함, 셰이더 파일만 복사):
#   POIYOMI_PKG      Poiyomi 9.3~10.0 unitypackage (기본: ~/Downloads/poi_Toon_9.3_UpTo_10.0.21.zip 을 풀어 씀)
#   POIYOMI_OLD_PKG  Poiyomi 7.3~9.3 unitypackage  (기본: ~/Downloads/poi_Toon_7.3.50_UpTo_9.3.64.zip)
#   UNIVRM_DIR       UniVRM 패키지 폴더 (com.vrmc.univrm, com.vrmc.gltf 가 든 Library/PackageCache 등, mtoon 팩에 필요)
# Poiyomi는 기능 키워드가 많아 전부 컴파일할 수 없으므로 Tools/ShaderPack/bundled/poiyomi.spec 의 조합만 넣는다
# (버전 하나에 5~10분, 셰이더 하나에 10MB 안팎). 렌더러가 머티리얼마다 가장 가까운 조합을 고른다.
set -e
cd "$(dirname "$0")"
OUT="${SHADERPACK_OUT:-Unity/Assets/StreamingAssets/ShaderPacks}"
WHAT="${1:-all}"; [ $# -gt 0 ] && shift
DL="${DOWNLOADS:-$HOME/Downloads}"
TMP="${TMPDIR:-/tmp}/vrcproto-shaderpacks"
UNIVRM_DIR="${UNIVRM_DIR:-}"
[ -f bin/av/VrcProtoAv.dll ] || sh build.sh av
mkdir -p "$OUT" "$TMP"

# zip 안의 unitypackage (다운로드 원본은 그대로 두고 임시 폴더에 푼다)
unzip_pkg() { # $1 zip, $2 이름
  if [ ! -f "$TMP/$2" ]; then rm -rf "$TMP/$2.d" && mkdir -p "$TMP/$2.d" && unzip -oq "$1" -d "$TMP/$2.d" && mv -f "$TMP/$2.d"/*.unitypackage "$TMP/$2" && rm -rf "$TMP/$2.d"; fi
  echo "$TMP/$2"
}

# dotnet(Windows 프로그램)에 넘기는 인자는 MSYS 경로 변환을 끄고 Windows 경로로 직접 바꿔 준다 (";"가 든 셰이더 이름 목록이 망가지지 않게)
pack() { # $1 원본, $2 팩 이름, $3 셰이더 이름들(;), $4 변형 목록(선택)
  echo "== $2"
  if [ -n "${4:-}" ]; then MSYS2_ARG_CONV_EXCL='*' dotnet bin/av/VrcProtoAv.dll --build-shader-pack "$(cygpath -w "$1")" --name "$2" --only "$3" --out "$(cygpath -w "$OUT")" --spec "$(cygpath -w "$4")"
  else MSYS2_ARG_CONV_EXCL='*' dotnet bin/av/VrcProtoAv.dll --build-shader-pack "$(cygpath -w "$1")" --name "$2" --only "$3" --out "$(cygpath -w "$OUT")"; fi
}

poiyomi() {
  NEW="${POIYOMI_PKG:-$(unzip_pkg "$DL/poi_Toon_9.3_UpTo_10.0.21.zip" poi_new.unitypackage)}"
  OLD="${POIYOMI_OLD_PKG:-$(unzip_pkg "$DL/poi_Toon_7.3.50_UpTo_9.3.64.zip" poi_old.unitypackage)}"
  SPEC=Tools/ShaderPack/bundled/poiyomi.spec
  VERS="${*:-10.0 9.3 9.2 9.1 9.0 8.1 8.0}"
  for v in $VERS; do
    case $v in
      10.0) pack "$NEW" poiyomi_10.0 ".poiyomi/Poiyomi Toon;.poiyomi/Poiyomi Toon Two Pass" $SPEC ;;
      9.3)  pack "$NEW" poiyomi_9.3 ".poiyomi/Old Versions/9.3/Poiyomi Toon;.poiyomi/Old Versions/9.3/Poiyomi Toon Two Pass" $SPEC ;;
      9.[012]) pack "$OLD" poiyomi_$v ".poiyomi/Old Versions/$v/Poiyomi Toon" $SPEC ;;   # Two Pass 는 Toon 으로 대신 (alias)
      8.1)  pack "$OLD" poiyomi_8.1 ".poiyomi/Old Versions/8.1/Poiyomi Toon;.poiyomi/Old Versions/8.1/Poiyomi Toon Outline" $SPEC ;;
      8.0)  pack "$OLD" poiyomi_8.0 ".poiyomi/Old Versions/8.0/Poiyomi Toon;.poiyomi/Old Versions/8.0/Poiyomi Outline" $SPEC ;;
      *) echo "모르는 Poiyomi 버전: $v"; exit 1 ;;
    esac
  done
}

mtoon() {
  # MToon·UniUnlit 은 기능을 multi_compile 로 고르므로 변형 목록 없이 전부 들어간다
  [ -n "$UNIVRM_DIR" ] || { echo "UNIVRM_DIR(UniVRM 패키지 폴더)를 정해 주세요" >&2; exit 1; }
  VRM=$(ls -d "$UNIVRM_DIR"/com.vrmc.univrm@* | tail -1); GLTF=$(ls -d "$UNIVRM_DIR"/com.vrmc.gltf@* | tail -1)
  rm -rf "$TMP/vrm" && mkdir -p "$TMP/vrm" && cp -r "$VRM/MToon" "$TMP/vrm/" && cp -r "$GLTF/UniUnlit" "$TMP/vrm/"
  pack "$TMP/vrm" vrm_mtoon "VRM/MToon;UniGLTF/UniUnlit"
}

case $WHAT in
  poiyomi) poiyomi "$@" ;;
  mtoon) mtoon ;;
  all) mtoon; poiyomi ;;
  *) echo "사용: sh build_shaderpacks.sh [poiyomi|mtoon|all] [Poiyomi 버전…]"; exit 1 ;;
esac
cp -r Tools/ShaderPack/bundled/licenses "$OUT/" && cp Tools/ShaderPack/bundled/README.txt "$OUT/"
ls -la "$OUT"
