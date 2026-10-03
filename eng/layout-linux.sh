#!/bin/bash
# Lays out a published Oriel app for Linux: CEF's libraries and resources, and the subprocess exe,
# all beside the app's own executable (where CefLayout looks for them).
# Usage: eng/layout-linux.sh <app-publish-dir> <subprocess-exe> [cef-platform]
set -euo pipefail
publish="$1"; subprocess="$2"; platform="${3:-}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
cef="$("$repo/eng/fetch-cef.sh" $platform)"

cp -R "$cef/Release/." "$publish/"
cp -R "$cef/Resources/." "$publish/"
cp "$subprocess" "$publish/Oriel.Subprocess"
echo "$publish"
