#!/bin/bash
# One sample of a running game's memory, taken apart by where it comes from: the kernel's list of the process's
# mappings (smaps, with the kernel's names for anonymous memory), the graphics driver's own account for the process
# (kgsl: this is the memory the phone's watchdog counts, and most of it is in no mapping), dmabuf, the system's
# summary (dumpsys meminfo) and, when game/memwatch.txt is there, the last lines of the runtime's and the engine's
# own accounts. Everything is kept in logs/mem-<label>/, and the summary is printed and written to summary.txt.
# usage: host/mem-report.sh <label> [package]       (package: com.local.waylith.port, or the 1.6 port's)
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
LABEL=$1; PKG=${2:-com.local.waylith.port}
OUT=logs/mem-$LABEL
mkdir -p "$OUT"
PID=$("$ADB" shell pidof $PKG | tr -d '\r')
[ -z "$PID" ] && echo "$PKG is not running" && exit 1
"$ADB" shell "su -c 'cat /proc/$PID/smaps'" | tr -d '\r' > "$OUT/smaps.txt"
"$ADB" shell "su -c 'cat /proc/$PID/status; echo; cat /proc/$PID/smaps_rollup'" | tr -d '\r' > "$OUT/status.txt"
"$ADB" shell "su -c 'cd /sys/class/kgsl/kgsl/proc/$PID 2>/dev/null && for f in *; do [ -f \$f ] && echo \$f \$(cat \$f 2>/dev/null); done'" | tr -d '\r' > "$OUT/kgsl.txt"
"$ADB" shell "su -c 'cat /sys/kernel/debug/kgsl/proc/$PID/mem 2>/dev/null'" | tr -d '\r' > "$OUT/kgsl-mem.txt"
"$ADB" shell "su -c 'dmabuf_dump $PID 2>&1'" | tr -d '\r' > "$OUT/dmabuf.txt"
"$ADB" shell "dumpsys meminfo $PID" | tr -d '\r' > "$OUT/meminfo.txt"
"$ADB" logcat -d -v time 2>/dev/null | grep -a -F "[memwatch]" | tail -40 | tr -d '\r' > "$OUT/memwatch.txt"
node host/mem-report.js "$OUT" | tee "$OUT/summary.txt"
