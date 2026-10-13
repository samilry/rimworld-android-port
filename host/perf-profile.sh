#!/bin/bash
# Samples one thread of the running game and says where its time goes, with the compiled (JIT) code named.
# The game has to run with the runtime's method map on: start it with
#   host/perf-start.sh DOTNET_PerfMapEnabled=3 DOTNET_PerfMapJitDumpPath=/data/user/0/com.local.waylith.port/files
# (game/runtime.env; the map file grows to some 17 MB and is deleted here at the next such start by hand).
# usage: host/perf-profile.sh <label> [seconds] [thread name or id] [pattern...]
#        e.g. host/perf-profile.sh paused 10 MainThread "ThrottleToTargetFrameRate" "callers:^\[vdso\]!"
#        The render thread and the job workers have no names of their own ("Thread-24"): see top -H for them.
# Output: logs/prof-<label>/ (chains.txt, perf.map, maps.txt) and the report of host/perf-profile.js on it.
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
LABEL=${1:?label}
SECS=${2:-10}
THREAD=${3:-MainThread}
shift; shift; shift
OUT=logs/prof-$LABEL
mkdir -p "$OUT"
"$ADB" push "$(cygpath -w host/perf-chains.awk)" /data/local/tmp/perf-chains.awk > /dev/null
"$ADB" shell "su -c '
P=\$(pidof $PKG)
case \"$THREAD\" in [0-9]*) T=$THREAD;; *) T=\$(for t in /proc/\$P/task/*; do [ \"\$(cat \$t/comm)\" = \"$THREAD\" ] && echo \${t##*/}; done | head -1);; esac
[ -z \"\$T\" ] && echo \"no thread named $THREAD\" && exit 1
[ -f /data/user/0/$PKG/files/perf-\$P.map ] || { echo \"no method map: the game was not started with DOTNET_PerfMapEnabled=3\"; exit 1; }
cd /data/local/tmp
simpleperf record -t \$T -e cpu-cycles -f 3000 --call-graph fp --duration $SECS -o perf.data 2>&1 | grep -E \"Samples recorded\"
cat /proc/\$P/maps > maps.txt
cp /data/user/0/$PKG/files/perf-\$P.map perf.map
simpleperf dump perf.data 2>/dev/null | awk -f perf-chains.awk > chains.txt
chmod 644 chains.txt perf.map maps.txt'" | tr -d '\r'
for f in chains.txt perf.map maps.txt; do "$ADB" pull /data/local/tmp/$f "$(cygpath -w "$OUT/$f")" > /dev/null; done
"$ADB" shell "su -c 'rm -f /data/local/tmp/perf.data /data/local/tmp/chains.txt /data/local/tmp/perf.map /data/local/tmp/maps.txt'"
node host/perf-profile.js "$OUT" "$@"
