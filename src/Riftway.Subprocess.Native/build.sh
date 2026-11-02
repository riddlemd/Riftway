#!/bin/bash
# Builds the native subprocess against the pinned CEF for one platform.
# Usage: src/Riftway.Subprocess.Native/build.sh <output-exe> [cef-platform]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$1"; platform="${2:-}"
cef="$("$here/../../eng/fetch-cef.sh" $platform)"
# The same API version the C# bindings were generated for.
version="$(grep -oE 'Version = [0-9]+' "$here/../Riftway.Cef/Interop/CefApiVersion.cs" | grep -oE '[0-9]+')"

mkdir -p "$(dirname "$out")"
case "$platform" in
  macosarm64|macosx64)
    # macOS: the framework is opened at run time, after the sandbox (subprocess.c), so nothing is linked.
    arch=arm64; [ "$platform" = macosx64 ] && arch=x86_64
    ${CC:-clang} -std=c11 -O2 -Wall -Werror -arch "$arch" -mmacosx-version-min=12.0 \
      -DCEF_API_VERSION="$version" -I"$cef" "$here/subprocess.c" -o "$out"
    echo "$out"
    exit 0 ;;
esac
# libcef sits beside the exe in the Linux layout, hence $ORIGIN.
${CC:-clang} -std=c11 -O2 -Wall -Werror \
  -DCEF_API_VERSION="$version" -I"$cef" \
  "$here/subprocess.c" \
  -L"$cef/Release" -lcef -ldl -Wl,-rpath,'$ORIGIN' -Wl,--export-dynamic-symbol=getauxval \
  -o "$out"
echo "$out"
