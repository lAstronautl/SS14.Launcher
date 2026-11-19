#!/usr/bin/env bash
# Ahead-of-time compiles managed assemblies to native code for Mono on Android (arm64), so the game does not
# have to JIT them on the phone at every start. Produces <Assembly>.dll.so next to each input assembly's name;
# Mono loads such an image automatically when it loads the assembly from the same directory.
#
# Usage: aot-compile.sh <runtime-version> <out-dir> <ref-dir>... -- <assembly-or-dir|@framework>...
#   runtime-version  exact .NET version, e.g. 10.0.15 (images only load into that runtime)
#   ref-dir          directories with every assembly the inputs reference (framework, engine)
# Env: ANDROID_NDK_HOME, JOBS (default nproc). Downloads the Mono runtime + AOT cross compiler from NuGet.
set -euo pipefail

VER="$1"; OUT="$2"; shift 2
REFS=()
while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do REFS+=("$(readlink -f "$1")"); shift; done
[ "${1:-}" = "--" ] && shift
INPUTS=("$@")

NDK="${ANDROID_NDK_HOME:?set ANDROID_NDK_HOME}"
LLVM="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
CACHE="${AOT_CACHE:-$HOME/.cache/robust-android-aot}"
mkdir -p "$CACHE" "$OUT"
OUT="$(readlink -f "$OUT")"

fetch_pkg() { # id version -> extracted dir
  local id="$1" ver="$2" dir="$CACHE/$1/$2"
  if [ ! -d "$dir" ]; then
    mkdir -p "$dir"
    curl -fsSL "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg" -o "$dir.nupkg"
    unzip -q -o "$dir.nupkg" -d "$dir"
    rm "$dir.nupkg"
  fi
  echo "$dir"
}

RUNTIME=$(fetch_pkg microsoft.netcore.app.runtime.mono.android-arm64 "$VER")
CROSS=$(fetch_pkg microsoft.netcore.app.runtime.aot.linux-x64.cross.android-arm64 "$VER")
AOTC="$CROSS/tools/mono-aot-cross"
chmod +x "$AOTC" "$CROSS"/tools/* 2>/dev/null || true

# Framework references: the exact assemblies of that runtime (CoreLib lives in native/).
FW="$CACHE/fw-$VER"
if [ ! -d "$FW" ]; then
  mkdir -p "$FW"
  cp "$RUNTIME"/runtimes/android-arm64/lib/net*/*.dll "$FW/"
  cp "$RUNTIME"/runtimes/android-arm64/native/System.Private.CoreLib.dll "$FW/"
fi
MONO_PATH_ALL="$FW"
for r in "${REFS[@]}"; do MONO_PATH_ALL="$MONO_PATH_ALL:$r"; done

# Expand directories into their .dll files.
FILES=()
for i in "${INPUTS[@]}"; do
  [ "$i" = "@framework" ] && i="$FW"  # the runtime's own framework assemblies
  if [ -d "$i" ]; then
    while IFS= read -r f; do FILES+=("$f"); done < <(find "$i" -maxdepth 1 -name '*.dll' | sort)
  else
    FILES+=("$(readlink -f "$i")")
  fi
done

compile_one() {
  local dll="$1" name tmp
  name="$(basename "$dll")"
  tmp="$(mktemp -d)"
  if MONO_PATH="$MONO_PATH_ALL" "$AOTC" --aot=asmonly,outfile="$tmp/a.s",mtriple=aarch64-linux-android "$dll" > "$tmp/log" 2>&1 \
     && "$LLVM/clang" -target aarch64-linux-android24 -c "$tmp/a.s" -o "$tmp/a.o" \
     && "$LLVM/ld.lld" -shared -o "$OUT/$name.so" "$tmp/a.o"; then
    "$LLVM/llvm-strip" --strip-debug "$OUT/$name.so" 2>/dev/null || true
    echo "ok   $name ($(du -h "$OUT/$name.so" | cut -f1))"
  else
    # Some assemblies (reference-only, resources, unsupported IL) cannot be AOT'd; they stay JIT.
    echo "skip $name: $(grep -m1 -iE 'error|exception' "$tmp/log" || tail -n1 "$tmp/log")"
  fi
  rm -rf "$tmp"
}
export -f compile_one
export AOTC LLVM OUT MONO_PATH_ALL

printf '%s\n' "${FILES[@]}" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'compile_one "$@"' _ {}
echo "AOT images in $OUT: $(ls "$OUT"/*.so 2>/dev/null | wc -l)"
