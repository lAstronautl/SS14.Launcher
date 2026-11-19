#!/usr/bin/env bash
# Builds the AOT packs listed in bundled.txt into aot/bundled/<release tag>.zip (packaged as APK assets).
# Env: ANDROID_NDK_HOME. Skips packs that already exist.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/bundled"; mkdir -p "$OUT"
WORK="${AOT_CACHE:-$HOME/.cache/robust-android-aot}/bundle"; mkdir -p "$WORK"

pin() { grep "^$1=" "$HERE/runtime-versions.txt" | cut -d= -f2; }

grep -v '^#' "$HERE/bundled.txt" | while read -r kind ver; do
  [ -z "${kind:-}" ] && continue
  case "$kind" in
    runtime)
      zip="$OUT/android-runtime-aot-v$ver.zip"
      [ -f "$zip" ] && { echo "have $zip"; continue; }
      rm -rf "$WORK/rt-$ver"
      "$HERE/aot-compile.sh" "$ver" "$WORK/rt-$ver" -- @framework
      (cd "$WORK/rt-$ver" && zip -q -9 "$zip" *.dll.so) ;;
    official)
      zip="$OUT/android-official-aot-v$ver.zip"
      [ -f "$zip" ] && { echo "have $zip"; continue; }
      url=$(curl -fsSL https://robust-builds.cdn.spacestation14.com/manifest.json |
        python3 -c "import json,sys;print(json.load(sys.stdin)['$ver']['platforms']['linux-arm64']['url'])")
      eng="$WORK/engine-$ver"; rm -rf "$eng"; mkdir -p "$eng"
      curl -fsSL "$url" -o "$eng.zip" && unzip -q -o "$eng.zip" -d "$eng"
      major=$(python3 -c "import json;o=json.load(open('$eng/Robust.Client.runtimeconfig.json'))['runtimeOptions'];print((o.get('framework') or [f for f in o['frameworks'] if f['name']=='Microsoft.NETCore.App'][0])['version'].split('.')[0])")
      rt=$(pin "$major")
      rm -rf "$WORK/eng-$ver"
      "$HERE/aot-compile.sh" "$rt" "$WORK/eng-$ver" "$eng" -- "$eng"
      (cd "$WORK/eng-$ver" && zip -q -9 "$zip" *.dll.so) ;;
  esac
done
ls -la "$OUT"
