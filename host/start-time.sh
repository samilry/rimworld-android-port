#!/bin/bash
# How long the game takes from process start to the main menu, as the game itself reports it once ("[mobile] main menu
# N s after the process started"). Starts the game with the given launcher arguments and runtime environment.
# usage: host/start-time.sh "<launcher arguments>" [NAME=value ...]      e.g. host/start-time.sh "-NoCrashDialog -Verbose"
# game/perftest.txt is set aside for the start (it would load a scene) and put back.
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
ARGS=$1; shift
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell am force-stop $PKG
printf '%s\n' $ARGS > build/args.txt
"$ADB" push "$(cygpath -w build/args.txt)" /data/local/tmp/args.txt > /dev/null
if [ $# -gt 0 ]; then
  printf '%s\n' "$@" > build/runtime.env
  "$ADB" push "$(cygpath -w build/runtime.env)" /data/local/tmp/runtime.env > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/runtime.env $GAME/runtime.env; chown $UID_:1078 $GAME/runtime.env'"
else
  "$ADB" shell "su -c 'rm -f $GAME/runtime.env'"
fi
"$ADB" shell "su -c 'cp /data/local/tmp/args.txt $GAME/args.txt; chown $UID_:1078 $GAME/args.txt; [ -f $GAME/perftest.txt ] && mv $GAME/perftest.txt $GAME/perftest.off; rm -f $GAME/perfcmd.txt'"
"$ADB" logcat -c
"$ADB" shell am start -n $PKG/com.local.waylith.port.MainActivity > /dev/null
W=0
until [ $W -gt 100 ] || "$ADB" logcat -d -v time 2>/dev/null | grep -q -F "[mobile] of that: "; do sleep 3; W=$((W + 1)); done
LINE=$("$ADB" logcat -d -v time 2>/dev/null | grep -F "[mobile] main menu " | head -1 | sed -E 's/^.*\[mobile\] //')
# What the runtime says about that time (mobile/Entry.cs): compiling, collector pauses, processor time.
PARTS=$("$ADB" logcat -d -v time 2>/dev/null | grep -F "[mobile] of that: " | head -1 | sed -E 's/^.*\[mobile\] of that: //')
echo "${LINE:-no main menu within 300 s} | arguments: $ARGS | environment: ${*:-default}" | tee -a logs/start-time.txt
[ -n "$PARTS" ] && echo "    $PARTS" | tee -a logs/start-time.txt
"$ADB" shell "su -c '[ -f $GAME/perftest.off ] && mv $GAME/perftest.off $GAME/perftest.txt'"
