#!/bin/bash
# The controls of the interface as the game last drew it (needs the measuring scene: perftest.txt), summed up.
# usage: host/controls-dump.sh <label> [--all]
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
cd "$(dirname "$0")/.."
bash host/perf-cmd.sh "controls=$1"
sleep 7
mkdir -p logs/controls
MSYS_NO_PATHCONV=1 "$ADB" shell "su -c 'cat /data/media/0/Android/data/com.local.waylith.port/files/logs/controls-$1.txt'" | tr -d '\r' > "logs/controls/$1.txt"
node host/controls-report.js "logs/controls/$1.txt" 17.8 $2
