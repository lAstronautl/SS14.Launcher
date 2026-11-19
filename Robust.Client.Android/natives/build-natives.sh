#!/usr/bin/env bash
# Builds the native libraries the engine needs on Android, for each ABI.
# Output: natives/out/<abi>/*.so  (picked up by Robust.Client.Android.csproj)
# Requires: ANDROID_NDK_HOME, cmake, ninja, curl, make.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${NATIVES_WORK:-$HERE/work}"
OUT="$HERE/out"
API=24
ABIS="${ABIS:-arm64-v8a x86_64}"
NDK="${ANDROID_NDK_HOME:?set ANDROID_NDK_HOME}"
TOOLCHAIN="$NDK/build/cmake/android.toolchain.cmake"
LLVM="$NDK/toolchains/llvm/prebuilt/linux-x86_64"

SDL_VER=3.4.18
OPENAL_VER=1.24.3
FREETYPE_VER=2.13.3
SODIUM_VER=1.0.20
ZSTD_VER=1.5.7

mkdir -p "$WORK/src" "$OUT"
cd "$WORK/src"

fetch() { # url dir  (release tarballs; GitHub source archives are not always reachable)
  if [ ! -d "$2" ]; then
    curl -fsSL "$1" -o "$2.tar"
    mkdir "$2" && tar -xf "$2.tar" -C "$2" --strip-components=1
    rm "$2.tar"
  fi
}

fetch "https://openal-soft.org/openal-releases/openal-soft-$OPENAL_VER.tar.bz2" openal
fetch "https://download.savannah.nongnu.org/releases/freetype/freetype-$FREETYPE_VER.tar.gz" freetype
fetch "https://download.libsodium.org/libsodium/releases/libsodium-$SODIUM_VER.tar.gz" sodium
fetch "https://github.com/facebook/zstd/releases/download/v$ZSTD_VER/zstd-$ZSTD_VER.tar.gz" zstd

# SDL3 comes prebuilt (AAR with libSDL3.so + classes.jar).
if [ ! -f "$WORK/sdl/classes.jar" ]; then
  mkdir -p "$WORK/sdl" && cd "$WORK/sdl"
  curl -fsSL "https://github.com/libsdl-org/SDL/releases/download/release-$SDL_VER/SDL3-devel-$SDL_VER-android.zip" -o sdl.zip
  unzip -q -o sdl.zip && unzip -q -o "SDL3-$SDL_VER.aar"
  cd "$WORK/src"
fi
mkdir -p "$OUT/java"
cp "$WORK/sdl/classes.jar" "$OUT/java/sdl3.jar"

cmake_build() { # name srcdir abi extra-args...
  local name=$1 src=$2 abi=$3; shift 3
  local b="$WORK/build/$name-$abi"
  cmake -S "$src" -B "$b" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$API" \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON "$@" >/dev/null
  cmake --build "$b" >/dev/null
  echo "$b"
}

for abi in $ABIS; do
  echo "=== $abi"
  o="$OUT/$abi"; mkdir -p "$o"
  case $abi in
    arm64-v8a) triple=aarch64-linux-android ;;
    x86_64) triple=x86_64-linux-android ;;
    *) echo "unsupported abi $abi"; exit 1 ;;
  esac

  cp "$WORK/sdl/prefab/modules/SDL3-shared/libs/android.$abi/libSDL3.so" "$o/"

  b=$(cmake_build openal "$WORK/src/openal" $abi -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF -DALSOFT_TESTS=OFF \
      -DALSOFT_BACKEND_OBOE=OFF -DALSOFT_BACKEND_OPENSL=ON -DALSOFT_REQUIRE_OPENSL=ON -DALSOFT_INSTALL=OFF)
  cp "$b/libopenal.so" "$o/libopenal.so"

  # SharpFont P/Invokes "freetype6".
  b=$(cmake_build freetype "$WORK/src/freetype" $abi -DFT_DISABLE_ZLIB=OFF -DFT_DISABLE_BZIP2=ON \
      -DFT_DISABLE_PNG=ON -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON)
  cp "$(ls $b/libfreetype.so* | head -1)" "$o/libfreetype6.so"
  "$LLVM/bin/llvm-objcopy" --remove-section=.note.gnu.build-id "$o/libfreetype6.so" 2>/dev/null || true

  b=$(cmake_build zstd "$WORK/src/zstd/build/cmake" $abi -DZSTD_BUILD_PROGRAMS=OFF -DZSTD_BUILD_STATIC=OFF -DZSTD_BUILD_TESTS=OFF)
  cp "$(readlink -f $b/lib/libzstd.so)" "$o/libzstd.so"

  # libsodium uses autotools.
  sb="$WORK/build/sodium-$abi"
  if [ ! -f "$sb/lib/libsodium.so" ]; then
    rm -rf "$sb.src" && cp -r "$WORK/src/sodium" "$sb.src"
    ( cd "$sb.src" && CC="$LLVM/bin/${triple}${API}-clang" AR="$LLVM/bin/llvm-ar" RANLIB="$LLVM/bin/llvm-ranlib" \
        CFLAGS="-Os" ./configure --host=$triple --prefix="$sb" --disable-soname-versions --disable-static \
        --with-pic >/dev/null && make -j"$(nproc)" install >/dev/null )
  fi
  cp "$sb/lib/libsodium.so" "$o/libsodium.so"

  # Native host: SDL_main → downloaded Mono runtime → Robust.Android.GameHost.
  "$LLVM/bin/${triple}${API}-clang" -shared -fPIC -O2 -Wall -o "$o/libmain.so" "$HERE/robust_host.c" -llog -ldl

  "$LLVM/bin/llvm-strip" --strip-unneeded "$o"/*.so
  ls -la "$o"
done
