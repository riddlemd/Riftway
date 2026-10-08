#!/bin/bash
# Applies Oriel's CEF patches to a CEF checkout made by automate-git.py; running it again changes nothing.
# Usage: eng/cef/apply-patches.sh <download-dir> [--check | --revert]
#   <download-dir>  automate-git's --download-dir. patches/cef/*.patch go into <download-dir>/chromium/src/cef,
#                   patches/chromium/*.patch into <download-dir>/chromium/src, in name order.
#   --check         report each patch as applied, applicable or conflicting, and change nothing.
#   --revert        take the applied patches back out, newest first (before automate-git updates the checkout).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
download="${1:?usage: apply-patches.sh <download-dir> [--check | --revert]}"
mode="${2:-apply}"
src="$download/chromium/src"
case "$mode" in apply | --check | --revert) ;; *) echo "unknown mode $mode" >&2; exit 2 ;; esac

# git apply takes paths from the root of the repository it runs in, so src/cef must be its own checkout.
[ -e "$src/cef/.git" ] || { echo "no CEF checkout at $src/cef (run automate-git.py --no-build first)" >&2; exit 1; }

shopt -s nullglob
patches=()
for p in "$here"/patches/cef/*.patch; do patches+=("cef:$p"); done
for p in "$here"/patches/chromium/*.patch; do patches+=("chromium:$p"); done
if [ "$mode" = --revert ]; then
  reversed=()
  for ((i = ${#patches[@]} - 1; i >= 0; i--)); do reversed+=("${patches[i]}"); done
  patches=("${reversed[@]}")
fi

failed=0
for entry in "${patches[@]}"; do
  repo="${entry%%:*}"; patch="${entry#*:}"; name="$(basename "$patch")"
  dir="$src"; [ "$repo" = cef ] && dir="$src/cef"
  if git -C "$dir" apply --reverse --check "$patch" 2> /dev/null; then state=applied
  elif git -C "$dir" apply --check "$patch" 2> /dev/null; then state=applicable
  else state=conflicting
  fi
  case "$mode:$state" in
    --check:*) echo "$state  $repo/$name" ;;
    apply:applied | --revert:applicable) echo "skip  $repo/$name ($state)" ;;
    apply:applicable) git -C "$dir" apply "$patch" && echo "applied  $repo/$name" ;;
    --revert:applied) git -C "$dir" apply --reverse "$patch" && echo "reverted  $repo/$name" ;;
    *:conflicting)
      echo "CONFLICT  $repo/$name does not apply to $dir:" >&2
      git -C "$dir" apply --check "$patch" >&2 || true
      failed=1 ;;
  esac
done
[ "$failed" = 0 ] || exit 1

if [ "$mode" = apply ]; then
  echo "Next: translate the headers and build, e.g. automate-git.py ... --no-update --force-build (runs cef/tools/gclient_hook.py),"
  echo "then compare cef/cef_api_untracked.json's 999998 hashes with src/Oriel.Cef/CefPatchedApi.cs (eng/cef/README.md)."
fi
