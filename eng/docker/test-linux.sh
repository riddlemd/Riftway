#!/bin/bash
# Runs inside the linux-dev container: builds a self-checking Oriel test app and runs it under
# Xvfb. The exit code is the app's: the number of failed checks (non-zero = failure).
# Usage: test-linux.sh [--sandbox] <project-dir> <exe-name>
#   --sandbox  run as the non-root user with Chromium's sandbox on (needs --cap-add SYS_ADMIN).
set -euo pipefail
sandbox=0
if [ "${1:-}" = "--sandbox" ]; then sandbox=1; shift; fi
project="$1"; exe="$2"

# Host bin/obj are macOS builds; building from a copy keeps the two from fighting.
rsync -a --delete --exclude bin --exclude obj --exclude artifacts --exclude .git /src/ /work/
cd /work

src/Oriel.Subprocess.Native/build.sh /work/out/subprocess/Oriel.Subprocess linuxarm64 > /dev/null
dotnet publish "$project" -r linux-arm64 -c Release --self-contained -o /work/out/app > /work/publish.log 2>&1 || { cat /work/publish.log; exit 1; }
eng/layout-linux.sh /work/out/app /work/out/subprocess/Oriel.Subprocess linuxarm64 > /dev/null

Xvfb :99 -screen 0 1600x1000x24 > /dev/null 2>&1 &
export DISPLAY=:99
sleep 1

if [ "$sandbox" = 1 ]; then
  chown root:root /work/out/app/chrome-sandbox
  chmod 4755 /work/out/app/chrome-sandbox
  chmod -R a+rX /work/out
  exec runuser -u oriel -- env DISPLAY=:99 "/work/out/app/$exe" --enable-sandbox 2> /work/app.err
else
  exec "/work/out/app/$exe" 2> /work/app.err
fi
