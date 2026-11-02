#!/bin/bash
# Sends lines to the running measuring scene (game/perfcmd.txt; mobile/Perf.cs reads it within its report interval).
# usage: host/perf-cmd.sh "raid=Miho_Faction_Supremacist:2500" speed=1 ...
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
printf '%s\n' "$@" > build/perfcmd.txt
echo "# $(date +%T)" >> build/perfcmd.txt
"$ADB" push "$(cygpath -w build/perfcmd.txt)" /data/local/tmp/perfcmd.txt > /dev/null
"$ADB" shell "su -c 'cp /data/local/tmp/perfcmd.txt $GAME/perfcmd.txt; chown $UID_:1078 $GAME/perfcmd.txt'"
