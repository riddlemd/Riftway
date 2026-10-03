#!/bin/bash
# Builds the native subprocess against the pinned CEF for one platform.
# Usage: src/Oriel.Subprocess.Native/build.sh <output-exe> [cef-platform]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$1"; platform="${2:-}"
cef="$("$here/../../eng/fetch-cef.sh" $platform)"
# The same API version the C# bindings were generated for.
version="$(grep -oE 'Version = [0-9]+' "$here/../Oriel.Cef/Interop/CefApiVersion.cs" | grep -oE '[0-9]+')"

mkdir -p "$(dirname "$out")"
# libcef sits beside the exe in the Linux layout, hence $ORIGIN.
${CC:-clang} -std=c11 -O2 -Wall -Werror \
  -DCEF_API_VERSION="$version" -I"$cef" \
  "$here/subprocess.c" \
  -L"$cef/Release" -lcef -Wl,-rpath,'$ORIGIN' \
  -o "$out"
echo "$out"
