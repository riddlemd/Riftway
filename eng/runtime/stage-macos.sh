#!/bin/bash
# Stages Riftway.Runtime.<rid> for macOS: CEF's framework under <out>/framework, the helper executable every Chromium
# child runs (Riftway.Subprocess.Native) under <out>/helper, and CEF's licence as <out>/LICENSE.txt.
# Riftway's build targets put both into the app bundle. eng/runtime/Riftway.Runtime.csproj packs it.
# Usage: eng/runtime/stage-macos.sh <out-dir> [osx-arm64|osx-x64]   (on a Mac; default osx-arm64)
set -euo pipefail
out="$1"; rid="${2:-osx-arm64}"
case "$rid" in
  osx-arm64) platform=macosarm64 ;;
  osx-x64) platform=macosx64 ;;
  *) echo "rid must be osx-arm64 or osx-x64, not $rid" >&2; exit 1 ;;
esac
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cef="$("$repo/eng/fetch-cef.sh" "$platform")"

rm -rf "$out"
mkdir -p "$out/framework" "$out/helper"
cp -c -R "$cef/Release/Chromium Embedded Framework.framework" "$out/framework/" 2>/dev/null \
  || cp -R "$cef/Release/Chromium Embedded Framework.framework" "$out/framework/"
cp "$cef/LICENSE.txt" "$out/LICENSE.txt"
# The native helper: it enters Chromium's sandbox before loading the framework (EnableSandbox).
"$repo/src/Riftway.Subprocess.Native/build.sh" "$out/helper/Riftway.Subprocess" "$platform" > /dev/null
# NuGet keeps no symlinks; CEF's macOS framework has none, and this keeps it that way.
if [ -n "$(find "$out/framework" -type l | head -1)" ]; then echo "the framework has symlinks NuGet would drop" >&2; exit 1; fi
du -sh "$out/framework" "$out/helper/Riftway.Subprocess"
