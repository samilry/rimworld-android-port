#!/bin/bash
# Runs the game on the phone and collects its log: writes <game>/args.txt from the arguments given (default:
# windowed, verbose), clears logcat, starts the activity, waits for the process to end or for the time limit, then
# saves the filtered log to logs/phone-run-<n>.log and prints a short summary.
# usage: host/run.sh [seconds] [launcher arguments...]      e.g. host/run.sh 90 -NoCrashDialog -Verbose
set -e
cd "$(dirname "$0")/.."
. host/env.sh  # ADB
export MSYS_NO_PATHCONV=1
PKG=com.local.waylith.port
SECONDS_LIMIT=${1:-90}
shift || true
ARGS=("$@")
if [ ${#ARGS[@]} -eq 0 ]; then ARGS=(-NoCrashDialog -Verbose); fi
GAME=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
printf '%s\n' "${ARGS[@]}" > build/args.txt
"$ADB" push build/args.txt /data/local/tmp/args.txt > /dev/null
"$ADB" shell "su -c 'cp /data/local/tmp/args.txt $GAME/args.txt; chown $UID_:1078 $GAME/args.txt'"
# Vulkan interposer options, from the VULKAN environment variable ("identity=1 coherent=1 flush=0 frames=5").
if [ -n "$VULKAN" ]; then
  printf "%s\n" $VULKAN > build/vulkan.txt
  "$ADB" push build/vulkan.txt /data/local/tmp/vulkan.txt > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/vulkan.txt $GAME/vulkan.txt; chown $UID_:1078 $GAME/vulkan.txt'"
else
  "$ADB" shell "su -c 'rm -f $GAME/vulkan.txt'"
fi
mkdir -p logs
N=1
while [ -f "logs/phone-run-$N.log" ] || [ -f "logs/phone-run-$N.clean.log" ]; do N=$((N + 1)); done
OUT="logs/phone-run-$N.log"
"$ADB" shell am force-stop $PKG
"$ADB" logcat -c
"$ADB" shell am start -n $PKG/com.local.waylith.port.MainActivity > /dev/null
START=$(date +%s)
PID=""
while :; do
  sleep 2
  PID=$("$ADB" shell pidof $PKG | tr -d '\r')
  NOW=$(date +%s)
  if [ -z "$PID" ] && [ $((NOW - START)) -gt 6 ]; then echo "process ended after $((NOW - START)) s"; break; fi
  if [ $((NOW - START)) -ge "$SECONDS_LIMIT" ]; then echo "still running after $SECONDS_LIMIT s (pid $PID)"; break; fi
done
"$ADB" logcat -d -v time 2>/dev/null | grep -E "port17|DOTNET|DEBUG|AndroidRuntime|libc |dotnet|vulkan|Adreno|Vulkan" | sed -E 's/(sk-|key=|Bearer )[A-Za-z0-9_-]+/\1***/g' > "$OUT"
"$ADB" pull /data/media/0/Android/data/$PKG/files/logs/stderr.txt "logs/phone-run-$N.stderr.txt" > /dev/null 2>&1 || true
echo "log: $OUT ($(wc -l < "$OUT") lines); stderr: $([ -f "logs/phone-run-$N.stderr.txt" ] && wc -c < "logs/phone-run-$N.stderr.txt" || echo 0) bytes"
grep -E "Exception|error|Error|FATAL|Fatal|SIGSEGV|SIGABRT|signal [0-9]|Unhandled|glfw|GLFW|Vulkan|surface|Surface|swapchain|Window|render thread|Render thread|Bootstrap|exit code" "$OUT" | grep -v "^$" | head -60
