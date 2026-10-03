#!/bin/bash
# Runs inside the linux-dev container: builds a self-checking Oriel test app and runs it under
# Xvfb. The exit code is the app's: the number of failed checks (non-zero = failure).
# Usage: test-linux.sh [--sandbox] <project-dir> <exe-name>
#   ORIEL_EXTRA_ARGS: extra switches for the app (e.g. Chromium flags while diagnosing).
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

# Apple M4+ hosts give the VM SME without SVE, which crashes Chromium's libyuv; see hide-sme.c.
if grep -qw sme /proc/cpuinfo && ! grep -qw sve /proc/cpuinfo; then
  clang -shared -fPIC -O2 -o /work/out/hide-sme.so eng/docker/hide-sme.c -ldl
  export LD_PRELOAD=/work/out/hide-sme.so
fi

Xvfb :99 -screen 0 1600x1000x24 > /dev/null 2>&1 &
export DISPLAY=:99
sleep 1

status=0
if [ "$sandbox" = 1 ]; then
  chown root:root /work/out/app/chrome-sandbox
  chmod 4755 /work/out/app/chrome-sandbox
  chmod -R a+rX /work/out
  runuser -u oriel -- env DISPLAY=:99 LD_PRELOAD="${LD_PRELOAD:-}" ORIEL_TRACE_DEVTOOLS="${ORIEL_TRACE_DEVTOOLS:-}" "/work/out/app/$exe" --enable-sandbox ${ORIEL_EXTRA_ARGS:-} 2> /work/app.err || status=$?
else
  "/work/out/app/$exe" ${ORIEL_EXTRA_ARGS:-} 2> /work/app.err || status=$?
fi

mkdir -p /src/artifacts/linux && cp /work/app.err "/src/artifacts/linux/$exe.err"

# A failing run's stderr is the only place Chromium explains itself.
if [ "$status" != 0 ]; then
  echo "--- stderr (last 60 lines) ---"
  tail -60 /work/app.err
fi
exit $status
