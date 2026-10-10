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

dir_of() { if [ "$1" = cef ]; then echo "$src/cef"; else echo "$src"; fi; }

# Later patches change the context of earlier ones in the same file, so a patch's state is only known in sequence:
# copy the files the patches touch, take the applied ones out newest first, then put them back in order.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for entry in "${patches[@]}"; do
  repo="${entry%%:*}"; patch="${entry#*:}"; dir="$(dir_of "$repo")"
  sed -n 's#^diff --git a/\([^ ]*\) b/.*#\1#p' "$patch" | while read -r file; do
    if [ -e "$dir/$file" ] && [ ! -e "$work/$repo/$file" ]; then
      mkdir -p "$work/$repo/$(dirname "$file")"
      cp "$dir/$file" "$work/$repo/$file"
    fi
  done
  mkdir -p "$work/$repo"
done

states=()
for ((i = ${#patches[@]} - 1; i >= 0; i--)); do
  repo="${patches[i]%%:*}"; patch="${patches[i]#*:}"
  if git -C "$work/$repo" apply --reverse --check "$patch" 2> /dev/null; then
    git -C "$work/$repo" apply --reverse "$patch"
    states[i]=applied
  else
    states[i]=unapplied
  fi
done
for ((i = 0; i < ${#patches[@]}; i++)); do
  repo="${patches[i]%%:*}"; patch="${patches[i]#*:}"
  if [ "${states[i]}" = applied ]; then
    git -C "$work/$repo" apply "$patch"
  elif git -C "$work/$repo" apply --check "$patch" 2> /dev/null; then
    git -C "$work/$repo" apply "$patch"
    states[i]=applicable
  else
    states[i]=conflicting
  fi
done

order=()
for ((i = 0; i < ${#patches[@]}; i++)); do order+=("$i"); done
if [ "$mode" = --revert ]; then
  reversed=()
  for ((i = ${#order[@]} - 1; i >= 0; i--)); do reversed+=("${order[i]}"); done
  order=("${reversed[@]}")
fi

failed=0
for i in "${order[@]}"; do
  repo="${patches[i]%%:*}"; patch="${patches[i]#*:}"; name="$(basename "$patch")"
  dir="$(dir_of "$repo")"; state="${states[i]}"
  case "$mode:$state" in
    --check:*) echo "$state  $repo/$name" ;;
    apply:applied | --revert:applicable | --revert:conflicting) echo "skip  $repo/$name ($state)" ;;
    apply:applicable) git -C "$dir" apply "$patch" && echo "applied  $repo/$name" ;;
    --revert:applied) git -C "$dir" apply --reverse "$patch" && echo "reverted  $repo/$name" ;;
    apply:conflicting)
      echo "CONFLICT  $repo/$name does not apply to $dir:" >&2
      git -C "$work/$repo" apply --check "$patch" >&2 || true
      failed=1 ;;
  esac
done
[ "$failed" = 0 ] || exit 1

if [ "$mode" = apply ]; then
  echo "Next: translate the headers and build, e.g. automate-git.py ... --no-update --force-build (runs cef/tools/gclient_hook.py),"
  echo "then compare cef/cef_api_untracked.json's 999998 hashes with src/Oriel.Cef/CefPatchedApi.cs (eng/cef/README.md)."
fi
