#!/bin/bash
# Stages Oriel.Runtime.<rid> for Linux: CEF's Release and Resources with their libraries stripped, and the native
# subprocess, under <out>/native; CEF's licence as <out>/LICENSE.txt. eng/runtime/Oriel.Runtime.csproj packs it.
# Usage: eng/runtime/stage-linux.sh <out-dir> [cef-platform]   (in the oriel-linux-dev image; default linux64)
set -euo pipefail
out="$1"; platform="${2:-linux64}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cef="$("$repo/eng/fetch-cef.sh" "$platform")"

rm -rf "$out"
mkdir -p "$out/native"
cp -R "$cef/Release/." "$out/native/"
cp -R "$cef/Resources/." "$out/native/"
cp "$cef/LICENSE.txt" "$out/LICENSE.txt"
# The distribution keeps its symbols (libcef.so alone is over 1 GB); an app has no use for them.
for so in "$out"/native/*.so "$out"/native/*.so.*; do
  [ -f "$so" ] && strip --strip-unneeded "$so"
done
"$repo/src/Oriel.Subprocess.Native/build.sh" "$out/native/Oriel.Subprocess" "$platform" > /dev/null
du -sh "$out/native" "$out/native/libcef.so"
