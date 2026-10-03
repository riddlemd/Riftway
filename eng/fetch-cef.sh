#!/bin/bash
# Downloads the pinned stock CEF minimal distribution for one platform and prints its directory.
# Cached outside the repo so every worktree shares one copy.
# Usage: eng/fetch-cef.sh [platform]   (default: this machine; e.g. macosarm64, linux64, windows64)
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"

platform="${1:-}"
if [ -z "$platform" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) platform=macosarm64 ;;
    Darwin-x86_64) platform=macosx64 ;;
    Linux-x86_64) platform=linux64 ;;
    Linux-aarch64) platform=linuxarm64 ;;
    *) echo "unknown platform; pass one explicitly" >&2; exit 1 ;;
  esac
fi

read -r version cdn sha1 < <(python3 -c "
import json,sys
c=json.load(open('$root/cef.json'))
print(c['version'], c['cdn'], c['minimal']['$platform'])")

name="cef_binary_${version}_${platform}_minimal"
cache="${ORIEL_CEF_CACHE:-$HOME/.cache/oriel/cef}"
dir="$cache/$name"

if [ ! -f "$dir/.complete" ]; then
  mkdir -p "$cache"
  archive="$cache/$name.tar.bz2"
  url="$cdn/$(python3 -c "import urllib.parse;print(urllib.parse.quote('$name.tar.bz2'))")"
  curl -fsSL -o "$archive" "$url"
  actual="$(shasum -a 1 "$archive" | cut -d' ' -f1)"
  if [ "$actual" != "$sha1" ]; then
    echo "sha1 mismatch for $name: expected $sha1, got $actual" >&2
    rm -f "$archive"
    exit 1
  fi
  rm -rf "$dir"
  tar xjf "$archive" -C "$cache"
  rm -f "$archive"
  touch "$dir/.complete"
fi

echo "$dir"
