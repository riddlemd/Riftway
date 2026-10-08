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

case "$(uname -m)" in
  x86_64) cef_platform=linux64; rid=linux-x64 ;;
  *) cef_platform=linuxarm64; rid=linux-arm64 ;;
esac

src/Oriel.Subprocess.Native/build.sh /work/out/subprocess/Oriel.Subprocess "$cef_platform" > /dev/null
dotnet publish "$project" -r "$rid" -c Release --self-contained -o /work/out/app > /work/publish.log 2>&1 || { cat /work/publish.log; exit 1; }
eng/layout-linux.sh /work/out/app /work/out/subprocess/Oriel.Subprocess "$cef_platform" > /dev/null

Xvfb :99 -screen 0 1600x1000x24 > /dev/null 2>&1 &
export DISPLAY=:99
# Chromium's X11 desktop capturer dies (SIGILL) on this Xvfb; tests skip real screen capture under it.
export ORIEL_XVFB=1
sleep 1
# Maximize, minimize and full screen are window-manager operations.
openbox > /dev/null 2>&1 &
sleep 1

# A session bus of the run's own, as a desktop has: the launcher entry checks read its signals back.
# A run that never exits hangs the job instead of failing it; the cap is generous next to the slowest suite.
limit="timeout -k 10 ${ORIEL_RUN_LIMIT:-1800}"
status=0
if [ "$sandbox" = 1 ]; then
  chown root:root /work/out/app/chrome-sandbox
  chmod 4755 /work/out/app/chrome-sandbox
  chmod -R a+rX /work/out
  runuser -u oriel -- env DISPLAY=:99 ORIEL_XVFB=1 ORIEL_TRACE_DEVTOOLS="${ORIEL_TRACE_DEVTOOLS:-}" $limit dbus-run-session -- "/work/out/app/$exe" --enable-sandbox ${ORIEL_EXTRA_ARGS:-} 2> /work/app.err || status=$?
else
  $limit dbus-run-session -- "/work/out/app/$exe" ${ORIEL_EXTRA_ARGS:-} 2> /work/app.err || status=$?
fi

if [ "$status" = 124 ] || [ "$status" = 137 ]; then echo "FAIL  the app exits by itself within ${ORIEL_RUN_LIMIT:-1800} s  [killed]"; fi

mkdir -p /src/artifacts/linux && cp /work/app.err "/src/artifacts/linux/$exe.err"

# A failing run's stderr is the only place Chromium explains itself.
if [ "$status" != 0 ]; then
  echo "--- stderr (last 60 lines) ---"
  tail -60 /work/app.err
fi
exit $status
