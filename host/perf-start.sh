#!/bin/bash
# Starts the game on the measuring scene (game/perftest.txt must be there) with the given environment for the runtime
# (written to game/runtime.env; none given: the file is removed), and waits until the scene is set.
# usage: host/perf-start.sh [NAME=value ...]      e.g. host/perf-start.sh DOTNET_GCgen0size=0x1000000
#        A name with an empty value ("DOTNET_GCgen0size=") takes the variable out of the environment.
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell am force-stop $PKG
if [ $# -gt 0 ]; then
  printf '%s\n' "$@" > build/runtime.env
  "$ADB" push "$(cygpath -w build/runtime.env)" /data/local/tmp/runtime.env > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/runtime.env $GAME/runtime.env; chown $UID_:1078 $GAME/runtime.env'"
else
  "$ADB" shell "su -c 'rm -f $GAME/runtime.env'"
fi
"$ADB" shell "su -c 'rm -f $GAME/perfcmd.txt'"
"$ADB" logcat -c
"$ADB" shell am start -n $PKG/com.local.waylith.port.MainActivity > /dev/null
W=0
until [ $W -gt 120 ] || "$ADB" logcat -d -v time 2>/dev/null | grep -q -E "\] perf: [0-9.]+ fps, [0-9]+ tps"; do sleep 3; W=$((W + 1)); done
if [ $W -gt 120 ]; then echo "the scene did not come up"; exit 1; fi
echo "scene up after about $((W * 3)) s; environment: ${*:-default}"
