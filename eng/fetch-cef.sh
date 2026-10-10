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
    MINGW*-x86_64 | MSYS*-x86_64) platform=windows64 ;;
    MINGW*-aarch64 | MSYS*-aarch64) platform=windowsarm64 ;;
    *) echo "unknown platform; pass one explicitly" >&2; exit 1 ;;
  esac
fi

# Git Bash's python3 is the Windows one: it can't open /c/... paths and ends lines with CRLF.
config="$root/cef.json"
if command -v cygpath >/dev/null; then config="$(cygpath -m "$config")"; fi

read -r version cdn sha1 < <(python3 -c "
import json,sys
c=json.load(open('$config'))
print(c['version'], c['cdn'], c['minimal']['$platform'])" | tr -d '\r')

name="cef_binary_${version}_${platform}_minimal"
cache="${ORIEL_CEF_CACHE:-$HOME/.cache/oriel/cef}"
# A C:\ cache path reads as a remote host to tar and gets its hash "\"-escaped by sha1sum.
if command -v cygpath >/dev/null; then cache="$(cygpath -u "$cache")"; fi
dir="$cache/$name"

if [ ! -f "$dir/.complete" ]; then
  mkdir -p "$cache"
  archive="$cache/$name.tar.bz2"
  url="$cdn/$(python3 -c "import urllib.parse;print(urllib.parse.quote('$name.tar.bz2'))")"
  curl -fsSL -o "$archive" "$url"
  # Git for Windows ships sha1sum but not shasum (a Perl script).
  if command -v shasum >/dev/null; then hasher=(shasum -a 1); else hasher=(sha1sum); fi
  actual="$("${hasher[@]}" "$archive" | cut -d' ' -f1)"
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
