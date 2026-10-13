#!/bin/bash
# Watches the phone while the game runs a measuring scene (mobile/Perf.cs, game/perftest.txt): once every two seconds
# each CPU cluster's clock and the limit the phone holds it to, the GPU's load and clock, and the temperatures of the
# processor, the GPU and the shell. Then the game's own report lines from the same period ("[mobile] perf: ...").
# usage: host/perf-watch.sh <seconds> [label]        output also in logs/perf-watch-<label>.txt
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
SECS=${1:-30}
LABEL=${2:-run}
OUT=logs/perf-watch-$LABEL.txt
START=$("$ADB" shell "date +%H:%M:%S" | tr -d '\r')
"$ADB" shell "su -c '
i=0
while [ \$i -lt $SECS ]; do
  f=\"\"
  for p in /sys/devices/system/cpu/cpufreq/policy*; do f=\"\$f \$((\$(cat \$p/scaling_cur_freq) / 1000))/\$((\$(cat \$p/scaling_max_freq) / 1000))\"; done
  g=/sys/class/kgsl/kgsl-3d0
  c=0; for z in 17 19 2 4 6; do t=\$(cat /sys/class/thermal/thermal_zone\$z/temp); [ \$t -gt \$c ] && c=\$t; done
  echo \"\$(date +%H:%M:%S) cpu MHz cur/limit:\$f | gpu \$(cat \$g/gpu_busy_percentage | tr -d \" \") at \$(cat \$g/clock_mhz)/\$((\$(cat \$g/devfreq/max_freq) / 1000000)) MHz | temp cpu \$((c / 1000)) gpu \$((\$(cat /sys/class/thermal/thermal_zone33/temp) / 1000)) shell \$((\$(cat /sys/class/thermal/thermal_zone49/temp) / 1000)) batt \$((\$(cat /sys/class/thermal/thermal_zone72/temp) / 1000))\"
  sleep 2; i=\$((i + 2))
done'" | tr -d '\r' > "$OUT"
END=$("$ADB" shell "date +%H:%M:%S" | tr -d '\r')
echo "--- game report lines $START .. $END" >> "$OUT"
"$ADB" logcat -d -v time 2>/dev/null | grep -E "\[mobile\] perf: " | sed -E 's/^[0-9-]+ ([0-9:]+)\.[0-9]+ .*\[mobile\] perf: /\1 /' | awk -v a="$START" -v b="$END" '$1 >= a && $1 <= b' >> "$OUT"
cat "$OUT"
