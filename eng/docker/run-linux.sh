#!/bin/bash
# Runs inside the linux-dev container: builds Oriel for Linux, starts it under Xvfb + openbox,
# screenshots the screen, then closes the window the way a user would and reports how it exited.
# Usage: run-linux.sh [--sandbox] [url]
#   --sandbox  run as a non-root user with Chromium's sandbox on (setuid chrome-sandbox); the
#              container needs --cap-add SYS_ADMIN for the sandbox's namespaces. For meaningful
#              sandbox.txt evidence also pass --cap-add SYS_PTRACE (to read other users' /proc/*/ns)
#              and --security-opt seccomp=unconfined (Docker otherwise filters every process).
# Output lands in artifacts/linux/ in the mounted repo.
set -euo pipefail
sandbox=0
if [ "${1:-}" = "--sandbox" ]; then sandbox=1; shift; fi
url="${1:-https://example.com}"
out=/src/artifacts/linux
mkdir -p "$out"

# Host bin/obj are macOS builds; building from a copy keeps the two from fighting.
rsync -a --delete --exclude bin --exclude obj --exclude artifacts --exclude .git /src/ /work/
cd /work

src/Oriel.Subprocess.Native/build.sh /work/out/subprocess/Oriel.Subprocess linuxarm64 > "$out/build-subprocess.log" 2>&1
dotnet publish samples/Oriel.Sample -r linux-arm64 -c Release --self-contained -o /work/out/sample > "$out/publish-sample.log" 2>&1
eng/layout-linux.sh /work/out/sample /work/out/subprocess/Oriel.Subprocess linuxarm64 > /dev/null

Xvfb :99 -screen 0 1600x1000x24 > "$out/xvfb.log" 2>&1 &
export DISPLAY=:99
sleep 1
openbox > "$out/openbox.log" 2>&1 &
sleep 1

if [ "$sandbox" = 1 ]; then
  # The setuid helper must be root-owned and 4755, or Chromium refuses to use it.
  chown root:root /work/out/sample/chrome-sandbox
  chmod 4755 /work/out/sample/chrome-sandbox
  chmod -R a+rX /work/out
  runuser -u oriel -- env DISPLAY=:99 /work/out/sample/Oriel.Sample "$url" --enable-sandbox > "$out/app.log" 2>&1 &
else
  /work/out/sample/Oriel.Sample "$url" > "$out/app.log" 2>&1 &
fi
app=$!

for _ in $(seq 1 60); do wmctrl -l | grep -q "Oriel Sample" && break; sleep 1; done
sleep 5
wmctrl -lG > "$out/windows.txt" || true
import -window root "$out/screen.png"
ps -eo pid,user,args | grep -E "Oriel\.(Sample|Subprocess)" | grep -v grep | cut -c1-160 > "$out/processes.txt" || true

# Sandbox evidence per process: seccomp mode (2 = filter) and whether its PID/net namespaces
# differ from the browser's.
main_pid="$(pgrep -f '^/work/out/sample/Oriel.Sample' | head -1)"
{
  printf '%-8s %-14s %-8s %-8s %s\n' pid type seccomp own-pid-ns own-net-ns
  for pid in $(pgrep -f 'Oriel.Subprocess --type='); do
    # Chromium rewrites process titles, so --type= is not its own argv entry.
    type="$(tr '\0' ' ' < /proc/$pid/cmdline | grep -oE -- '--type=[a-z-]+' | head -1 | cut -d= -f2)"
    seccomp="$(awk '/^Seccomp:/ {print $2}' /proc/$pid/status)"
    [ "$(readlink /proc/$pid/ns/pid)" != "$(readlink /proc/$main_pid/ns/pid)" ] && pidns=yes || pidns=no
    [ "$(readlink /proc/$pid/ns/net)" != "$(readlink /proc/$main_pid/ns/net)" ] && netns=yes || netns=no
    printf '%-8s %-14s %-8s %-8s %s\n' "$pid" "$type" "$seccomp" "$pidns" "$netns"
  done
} > "$out/sandbox.txt" 2>&1 || true

start=$(date +%s.%N)
wmctrl -c "Oriel Sample" || true
status=0
wait $app || status=$?
echo "exit=$status after $(python3 -c "print(round($(date +%s.%N) - $start, 2))")s" > "$out/exit.txt"
cat "$out/exit.txt"
