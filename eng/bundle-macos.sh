#!/bin/bash
# Assembles a macOS .app around a published Riftway app: the CEF framework, and one helper .app per
# Chromium process type, each a renamed copy of the NativeAOT Riftway.Subprocess.
# Usage: eng/bundle-macos.sh <app-publish-dir> <exe-name> <subprocess-exe> <output-dir> [bundle-id]
set -euo pipefail
publish="$1"; name="$2"; subprocess="$3"; out="$4"; id="${5:-dev.riftway.$(echo "$name" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9.')}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
cef="$("$repo/eng/fetch-cef.sh")"

app="$out/$name.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"

cp -R "$publish/." "$app/Contents/MacOS/"

plist() { # <path> <executable> <bundle-id> <extra keys>
  cat > "$1" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>$2</string>
  <key>CFBundleIdentifier</key><string>$3</string>
  <key>CFBundleName</key><string>$2</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>0.1.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
$4
</dict>
</plist>
PLIST
}

plist "$app/Contents/Info.plist" "$name" "$id" ""

# APFS clone: instant, and no extra disk for the ~330 MB framework.
cp -c -R "$cef/Release/Chromium Embedded Framework.framework" "$app/Contents/Frameworks/"

# CEF finds helpers by these exact names; helpers must not show in the Dock.
for suffix in "" " (Alerts)" " (GPU)" " (Plugin)" " (Renderer)"; do
  helper_name="$name Helper$suffix"
  helper_app="$app/Contents/Frameworks/$helper_name.app"
  helper_id="$id.helper$(echo "$suffix" | tr -d ' ()' | tr '[:upper:]' '[:lower:]' | sed 's/^./.&/')"
  mkdir -p "$helper_app/Contents/MacOS"
  cp "$subprocess" "$helper_app/Contents/MacOS/$helper_name"
  plist "$helper_app/Contents/Info.plist" "$helper_name" "$helper_id" \
    "  <key>LSUIElement</key><string>1</string>
  <key>LSEnvironment</key><dict><key>MallocNanoZone</key><string>0</string></dict>"
done

echo "$app"
