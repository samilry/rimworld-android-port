#!/bin/bash
# One step of a scheduling comparison on the measuring scene (mobile/Perf.cs with game/perftest.txt): writes the given
# settings to game/perfcmd.txt and has the game load the scene's save again, waits until the scene is set and the
# colony has got going, then for <seconds> counts the main thread's cycles and instructions (simpleperf) while the
# game reports frames and ticks. Prints one summary line and appends it to logs/sched-exp.txt.
# Every step starts from the same moment of the same save: at triple speed a game day passes in three minutes, and the
# colony asleep is a different load from the colony at work. Steps are still meant to be run back to back, alternating
# with a baseline: the phone's own game service moves the clock limits around, and so does the temperature.
# usage: host/sched-exp.sh <label> <seconds> [setting=value ...]     e.g. host/sched-exp.sh pin 30 pin=1 clear=1
#        RELOAD=0 keeps the scene running; WARM=<seconds> between "scene set" and the measurement (10)
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
LABEL=$1; SECS=$2; shift 2
now() { "$ADB" shell "date +%H:%M:%S" | tr -d '\r'; }
# Everything that is not given goes back to the game's default, so that a step never inherits the one before.
declare -A SET=([pin]=0 [clear]=0 [workers]=sys [render]=sys [batches]=0 [speed]=3 [hint]=0 [rate]=0)
for kv in "$@"; do SET[${kv%%=*}]=${kv#*=}; done
: > build/perfcmd.txt
for k in "${!SET[@]}"; do echo "$k=${SET[$k]}" >> build/perfcmd.txt; done
[ "${RELOAD:-1}" = 1 ] && echo "reload=$(date +%s)" >> build/perfcmd.txt
echo "# $LABEL $(date +%T)" >> build/perfcmd.txt
T0=$(now)
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" push "$(cygpath -w build/perfcmd.txt)" /data/local/tmp/perfcmd.txt > /dev/null
"$ADB" shell "su -c 'cp /data/local/tmp/perfcmd.txt $GAME/perfcmd.txt; chown $UID_:1078 $GAME/perfcmd.txt'"
# The game looks at the file at the end of each of its report intervals (5 s).
if [ "${RELOAD:-1}" = 1 ]; then
  W=0
  until [ $W -gt 45 ] || "$ADB" logcat -d -v time 2>/dev/null | grep -F "[mobile] perf: scene set" | awk -v a="$T0" '{ split($2, t, "."); if (t[1] > a) found = 1 } END { exit !found }'; do sleep 2; W=$((W + 1)); done
  [ $W -gt 45 ] && echo "$LABEL: the scene did not come back" && exit 1
  sleep ${WARM:-10}
else
  sleep 11
fi
START=$(now)
STAT=$("$ADB" shell "su -c 'P=\$(pidof $PKG); T=\$(for t in /proc/\$P/task/*; do [ \"\$(cat \$t/comm)\" = MainThread ] && echo \${t##*/}; done | head -1); simpleperf stat -t \$T --duration $SECS -e cpu-cycles,instructions 2>&1; g=/sys/class/kgsl/kgsl-3d0; echo gpu \$(cat \$g/gpu_busy_percentage) \$(cat \$g/clock_mhz); echo shell \$(cat /sys/class/thermal/thermal_zone49/temp)'" | tr -d '\r')
END=$(now)
CYCLES=$(echo "$STAT" | grep cpu-cycles | awk '{gsub(",", "", $1); print $1}')
INSTR=$(echo "$STAT" | grep instructions | awk '{gsub(",", "", $1); print $1}')
GPU=$(echo "$STAT" | grep "^gpu" | cut -d' ' -f2-)
SHELL_T=$(echo "$STAT" | grep "^shell" | awk '{printf "%.1f", $2 / 1000}')
"$ADB" logcat -d -v time 2>/dev/null | grep -F "[mobile] perf: " | grep -E "perf: [0-9]" | sed -E 's/^[0-9-]+ ([0-9:]+)\.[0-9]+ .*\[mobile\] perf: /\1 /' | awk -v a="$START" -v b="$END" '$1 > a && $1 <= b' > build/sched-exp.lines
awk -v label="$LABEL" -v cycles="$CYCLES" -v instr="$INSTR" -v secs="$SECS" -v gpu="$GPU" -v shell="$SHELL_T" -v settings="$*" '
  { n++; fps += $2; tps += $4; med += $9; gsub(",", "", $0);
    for (i = 1; i <= NF; i++) {
      if ($i == "gc" && $(i + 2) == "pause") { split($(i + 1), g, "/"); gc0 += g[1]; gc1 += g[2]; pause += $(i + 3) }
      if ($i == "render") { rend += $(i + 1) + 0 }
      if ($i == "main") { mn += $(i + 1) + 0; split($(i + 5), c, "/"); lim += c[2] } } }
  END { if (n == 0) { print label ": no report lines"; exit }
    fps /= n; tps /= n;
    printf "%-20s %5.1f fps %4.0f tps | median %4.1f ms | main %3.0f%% render %3.0f%% | per tick %5.2f Mcyc %5.2f Minstr | per frame %5.1f Mcyc %5.1f Minstr | %.2f GHz, limit %.2f | gc0 %3.0f/s pause %4.1f%% | gpu %s MHz | %s C | %s\n",
      label, fps, tps, med / n, mn / n, rend / n, (tps > 0 ? cycles / secs / tps / 1e6 : 0), (tps > 0 ? instr / secs / tps / 1e6 : 0), cycles / secs / fps / 1e6, instr / secs / fps / 1e6, cycles / secs / (mn / n / 100) / 1e9, lim / n / 1000, gc0 / n / 5, pause / n / 50, gpu, shell, settings }' build/sched-exp.lines | tee -a logs/sched-exp.txt
