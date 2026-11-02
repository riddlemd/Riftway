#!/bin/bash
# Regenerates src/Riftway.Cef/Interop from the pinned CEF's C API headers (eng/cef.json).
# Platform-specific types (cef_main_args_t, cef_window_info_t, handles) differ per OS, so they are
# excluded here and written by hand in src/Riftway.Cef/Platform.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cef="$("$repo/eng/fetch-cef.sh" macosarm64)"
out="$repo/src/Riftway.Cef/Interop"

rm -rf "$out"
mkdir -p "$out"

# --traverse takes files, not directories, so every header under include/ is listed.
traverse=()
for h in "$cef"/include/*.h "$cef"/include/internal/*.h "$cef"/include/capi/*.h "$cef"/include/capi/views/*.h; do
  traverse+=(--traverse "$h")
done

# CEF names every struct _cef_x_t and typedefs it as cef_x_t; bindings use the typedef names.
remaps=()
for tag in $(cat "$cef"/include/*.h "$cef"/include/internal/*.h "$cef"/include/capi/*.h "$cef"/include/capi/views/*.h \
           | grep -oE "typedef struct _[a-z0-9_]+" | awk "{print \$3}" | sort -u); do
  remaps+=("$tag=${tag#_}")
done

cd "$repo"
log="$(mktemp)"
# ClangSharp's exit code counts diagnostics, warnings included; only "Error" lines fail the run.
dotnet tool run ClangSharpPInvokeGenerator -- \
  --language c \
  --file "$repo/tools/cef-interop/cef_all.h" \
  --include-directory "$cef" \
  "${traverse[@]}" \
  --define-macro CEF_API_VERSION=15400 \
  --additional "--target=arm64-apple-macos" "-isysroot" "$(xcrun --show-sdk-path)" \
  --namespace Riftway.Cef.Interop \
  --method-class-name CefNative \
  --library-path libcef \
  --output "$out" \
  --config codegen=latest file=multi types=unix \
  --generate file-scoped-namespaces helper-types disable-runtime-marshalling com-proxies=false \
  --log exclusions \
  --remap char16_t=char cef_platform_thread_handle_t=nint "${remaps[@]}" \
  --exclude cef_main_args_t _cef_main_args_t cef_window_info_t _cef_window_info_t \
            cef_accelerated_paint_info_t _cef_accelerated_paint_info_t \
            cef_cursor_handle_t cef_event_handle_t cef_window_handle_t cef_shared_texture_handle_t \
  "$@" > "$log" 2>&1 || true
if grep -qE "^\s+Error" "$log"; then
  grep -E "^\s+Error" "$log" >&2
  exit 1
fi
rm -f "$log"

# The per-OS API hashes for the pinned version, checked at startup so a mismatched libcef
# (e.g. a codec variant from another commit) fails loudly instead of corrupting memory.
hash() { awk -v os="$1" '
  /#define CEF_API_VERSION_15400 / { found = 1 }
  found && $0 ~ "defined\\(OS_" os "\\)" { getline; gsub(/"/, "", $3); print $3; exit }' "$cef/include/cef_api_versions.h"; }
cat > "$out/CefApiVersion.cs" <<CS
namespace Riftway.Cef.Interop;

/// <summary>The CEF API version these bindings were generated for, and its per-OS hashes.</summary>
public static class CefApiVersion
{
    public const int Version = 15400;
    public const string HashWindows = "$(hash WIN)";
    public const string HashMac = "$(hash MAC)";
    public const string HashLinux = "$(hash LINUX)";
}
CS
